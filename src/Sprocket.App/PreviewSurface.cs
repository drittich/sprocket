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
/// The program monitor: draws the <see cref="PlaybackEngine"/>'s currently-presented video frame onto
/// Avalonia's shared GPU <c>GRContext</c> via a Skia lease (ARCHITECTURE.md §10). It owns no decoding or
/// timing — it invalidates when the engine signals a new frame and, inside the render-thread lease, asks the
/// engine for the live frame and hands its native pixels to <see cref="FramePresenter"/> (no managed copy, §1).
/// </summary>
public sealed class PreviewSurface : Control
{
    private static readonly SKColor Background = new(0x0E, 0x0E, 0x12);

    private PlaybackEngine? _engine;
    private SkiaEffectPipeline? _pipeline;

    /// <summary>Attaches the engine whose current frame this surface presents. Call once.</summary>
    public void Attach(PlaybackEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        // Compiles the brightness/fade SkSL once; reused on every draw to apply the clip's effect chain (§7).
        _pipeline ??= new SkiaEffectPipeline();
        // The engine raises FramePresented on its pump thread; marshal the invalidation to the UI thread.
        engine.FramePresented += OnFramePresented;
        InvalidateVisual();
    }

    private void OnFramePresented() =>
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pipeline?.Dispose();
        _pipeline = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context) =>
        context.Custom(new DrawOp(new Rect(Bounds.Size), _engine, _pipeline));

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly PlaybackEngine? _engine;
        private readonly SkiaEffectPipeline? _pipeline;

        public DrawOp(Rect bounds, PlaybackEngine? engine, SkiaEffectPipeline? pipeline)
        {
            Bounds = bounds;
            _engine = engine;
            _pipeline = pipeline;
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

            if (_engine is null)
            {
                canvas.Clear(Background);
                return;
            }

            // The engine holds the frame lock for the callback, so the native buffers stay valid while we wrap
            // and draw them. Clear once, then composite each enabled video track's frame bottom→top — the same
            // multi-layer compositing the export path uses (PLAN.md step 14). The draws upload to the GPU on the
            // shared context (§10); pixels are wrapped, not copied (§1).
            _engine.UseLayers(layers =>
            {
                canvas.Clear(Background);
                if (layers.Count == 0 || _pipeline is null)
                    return;

                foreach (PresentedVideoLayer l in layers)
                {
                    SKRect dest = FramePresenter.ComputeFitRect(bounds, l.Width, l.Height);
                    _pipeline.DrawLayer(
                        canvas, dest, l.Pixels, l.RowBytes, l.Width, l.Height,
                        l.Effects, l.Opacity, ToBlendMode(l.BlendMode));
                }
            });
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
