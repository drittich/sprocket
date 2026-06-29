using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Playback;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// A monitor surface (PLAN.md steps 4/14/17): draws a <see cref="PlaybackEngine"/>'s currently-presented video
/// frame onto Avalonia's shared GPU <c>GRContext</c> via a Skia lease (ARCHITECTURE.md §10). It owns no decoding
/// or timing — it invalidates when the engine signals a new frame and, inside the render-thread lease, asks the
/// engine for the live frame and hands its native pixels to the effect pipeline (no managed copy, §1). The same
/// control serves both the Program (composited timeline) and Source (raw clip) monitors; the engine it presents
/// is swapped via <see cref="Attach"/>. <see cref="Zoom"/> and <see cref="ShowGuides"/> drive the <c>Fit ▾</c>
/// level and the safe-area / framing-grid overlay (UI.md §3.4).
/// </summary>
public sealed class PreviewSurface : Control
{
    private static readonly SKColor Background = new(0x0E, 0x0E, 0x12);

    private PlaybackEngine? _engine;
    private SkiaEffectPipeline? _pipeline;
    private MonitorZoom _zoom = MonitorZoom.Fit;
    private bool _showGuides;
    private int _frameWidth;
    private int _frameHeight;

    /// <summary>The preview zoom level (the <c>Fit ▾</c> control). Redraws on change.</summary>
    public MonitorZoom Zoom
    {
        get => _zoom;
        set { if (_zoom != value) { _zoom = value; InvalidateVisual(); } }
    }

    /// <summary>Whether to draw the safe-area / rule-of-thirds overlay (UI.md §3.4). Redraws on change.</summary>
    public bool ShowGuides
    {
        get => _showGuides;
        set { if (_showGuides != value) { _showGuides = value; InvalidateVisual(); } }
    }

    /// <summary>
    /// The monitor's logical frame size (the sequence resolution for the Program monitor, the source's resolution
    /// for the Source monitor). When set (both &gt; 0) every layer composites into one zoom rect derived from it
    /// and the overlay is drawn over that rect; otherwise each layer fits individually and no overlay is drawn.
    /// </summary>
    public void SetFrameSize(int width, int height)
    {
        if (_frameWidth == width && _frameHeight == height)
            return;
        _frameWidth = width;
        _frameHeight = height;
        InvalidateVisual();
    }

    /// <summary>Attaches the engine whose current frame this surface presents; detaches any previous one. The
    /// effect pipeline is compiled once on first attach and reused.</summary>
    public void Attach(PlaybackEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (ReferenceEquals(_engine, engine))
            return;
        if (_engine is not null)
            _engine.FramePresented -= OnFramePresented;
        _engine = engine;
        // Compiles the effect SkSL once; reused on every draw to apply each layer's effect chain (§7).
        _pipeline ??= new SkiaEffectPipeline();
        // The engine raises FramePresented on its pump thread; marshal the invalidation to the UI thread.
        engine.FramePresented += OnFramePresented;
        InvalidateVisual();
    }

    /// <summary>Stops presenting the current engine (e.g. when a Source monitor's engine is torn down).</summary>
    public void Detach()
    {
        if (_engine is not null)
            _engine.FramePresented -= OnFramePresented;
        _engine = null;
        InvalidateVisual();
    }

    private void OnFramePresented() =>
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_engine is not null)
            _engine.FramePresented -= OnFramePresented;
        _pipeline?.Dispose();
        _pipeline = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context) =>
        context.Custom(new DrawOp(new Rect(Bounds.Size), _engine, _pipeline, _zoom, _showGuides, _frameWidth, _frameHeight));

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly PlaybackEngine? _engine;
        private readonly SkiaEffectPipeline? _pipeline;
        private readonly MonitorZoom _zoom;
        private readonly bool _showGuides;
        private readonly int _frameWidth;
        private readonly int _frameHeight;

        public DrawOp(Rect bounds, PlaybackEngine? engine, SkiaEffectPipeline? pipeline,
            MonitorZoom zoom, bool showGuides, int frameWidth, int frameHeight)
        {
            Bounds = bounds;
            _engine = engine;
            _pipeline = pipeline;
            _zoom = zoom;
            _showGuides = showGuides;
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            var bounds = SKRect.Create((float)Bounds.Width, (float)Bounds.Height);

            // Confine all drawing — crucially the canvas.Clear calls below — to this surface's own bounds. A
            // Control does not clip to its bounds by default (ClipToBounds is false), so on entry the canvas
            // clip is the enclosing pane's, which also covers the sibling transport bar + monitor header. This
            // surface fills the pane and draws last (on top), so an unclipped Clear repaints the whole pane
            // background and wipes those siblings — the real cause of the "controls blank until hover / flicker
            // only while playing" symptom (a re-clear every presented frame). Clip to our bounds so it cannot.
            int checkpoint = canvas.Save();
            canvas.ClipRect(bounds);
            try
            {
                if (_engine is null)
                {
                    canvas.Clear(Background);
                    return;
                }

                // The engine holds the frame lock for the callback, so the native buffers stay valid while we wrap
                // and draw them. Clear once, then composite each enabled video track's frame bottom→top — the same
                // multi-layer compositing the export path uses (PLAN.md step 14). The draws upload to the GPU on the
                // shared context (§10); pixels are wrapped, not copied (§1). When a logical frame size is set, all
                // layers share one zoom rect and the overlay (UI.md §3.4) is drawn over it (PLAN.md step 17).
                _engine.UseLayers(layers =>
                {
                    canvas.Clear(Background);
                    if (layers.Count == 0 || _pipeline is null)
                        return;

                    bool haveFrame = _frameWidth > 0 && _frameHeight > 0;
                    SKRect frameRect = haveFrame
                        ? FramePresenter.ComputeZoomRect(bounds, _frameWidth, _frameHeight, _zoom)
                        : SKRect.Empty;

                    foreach (PresentedVideoLayer l in layers)
                    {
                        SKRect dest = haveFrame ? frameRect : FramePresenter.ComputeFitRect(bounds, l.Width, l.Height);
                        _pipeline.DrawLayer(
                            canvas, dest, l.Pixels, l.RowBytes, l.Width, l.Height,
                            l.Effects, l.Opacity, ToBlendMode(l.BlendMode));
                    }

                    if (_showGuides && haveFrame)
                        MonitorOverlay.Draw(canvas, frameRect, thirds: true, safeAreas: true);
                });
            }
            finally
            {
                canvas.RestoreToCount(checkpoint);
            }
        }

        private static SKBlendMode ToBlendMode(BlendMode mode) => mode switch
        {
            BlendMode.Multiply => SKBlendMode.Multiply,
            BlendMode.Screen => SKBlendMode.Screen,
            BlendMode.Add => SKBlendMode.Plus,
            _ => SKBlendMode.SrcOver,
        };
    }
}
