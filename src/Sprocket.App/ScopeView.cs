using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// Shared state between the monitor surface (the scope's producer) and the <see cref="ScopeView"/> panel
/// (its consumer), PLAN.md step 34: which scope is active, one persistent downscaled raster sample surface,
/// and the reusable <see cref="ScopeData"/> bins. <see cref="Capture"/> and the view's draw both run on
/// Avalonia's single render thread, so the state needs no locking; <see cref="ActiveKind"/> is written from
/// the UI thread but is a single enum field read once per draw.
/// </summary>
public sealed class ScopeState : IDisposable
{
    // The analysis sample width: enough columns for a useful waveform, small enough that the per-frame
    // GPU→CPU readback + CPU binning stay trivial (≤ 256×144 px for 16:9).
    private const int AnalysisWidth = 256;

    private SKSurface? _sample;
    private bool _disposed;

    /// <summary>The scope selected in the monitor header (<see cref="ScopeKind.None"/> hides the panel).</summary>
    public ScopeKind ActiveKind { get; set; }

    /// <summary>The most recent analysis, reused frame to frame.</summary>
    public ScopeData Data { get; } = new();

    /// <summary>Raised (on the render thread) after a capture refreshes <see cref="Data"/>.</summary>
    public event Action? Updated;

    /// <summary>
    /// Samples the monitor composite just drawn into <paramref name="leaseSurface"/> within
    /// <paramref name="frameRect"/> (canvas coordinates on <paramref name="canvas"/>) and refreshes
    /// <see cref="Data"/> for the active scope. The snapshot is drawn scaled into a persistent raster
    /// surface — one bounded GPU→CPU readback of a ≤256-wide sample per presented frame, no per-frame
    /// managed pixel buffers (the analysis reads the raster surface's own pixels in place).
    /// </summary>
    public void Capture(SKSurface leaseSurface, SKCanvas canvas, SKRect frameRect)
    {
        if (_disposed || ActiveKind == ScopeKind.None || frameRect.Width <= 0 || frameRect.Height <= 0)
            return;

        canvas.Flush();
        SKRectI region = SKRectI.Round(canvas.TotalMatrix.MapRect(frameRect));
        using SKImage? composite = leaseSurface.Snapshot(region);
        if (composite is null)
            return;

        int w = AnalysisWidth;
        int h = Math.Clamp((int)Math.Round(w * (double)region.Height / Math.Max(1, region.Width)), 16, 256);
        if (_sample is null || _sample.Canvas.DeviceClipBounds.Width != w || _sample.Canvas.DeviceClipBounds.Height != h)
        {
            _sample?.Dispose();
            _sample = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (_sample is null)
                return;
        }

        _sample.Canvas.DrawImage(composite, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear));
        _sample.Canvas.Flush();

        using SKPixmap pixels = _sample.PeekPixels();
        if (pixels is null)
            return;
        ScopeAnalyzer.Analyze(ActiveKind, pixels.GetPixelSpan(), w, h, pixels.RowBytes, Data);
        Updated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sample?.Dispose();
        _sample = null;
    }
}

/// <summary>
/// The grading-scope panel (PLAN.md step 34): draws the latest <see cref="ScopeState.Data"/> — luma
/// waveform, RGB parade, vectorscope, or histogram — through <see cref="ScopeRenderer"/> on Avalonia's
/// shared GPU context, exactly like <see cref="PreviewSurface"/>. It owns no analysis: the monitor
/// surface produces the bins as part of presenting each frame; this control just re-renders on
/// <see cref="ScopeState.Updated"/>.
/// </summary>
public sealed class ScopeView : Control
{
    private ScopeState? _state;
    private ScopeRenderer? _renderer;

    /// <summary>Attaches the shared scope state this panel displays.</summary>
    public void Attach(ScopeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (ReferenceEquals(_state, state))
            return;
        if (_state is not null)
            _state.Updated -= OnUpdated;
        _state = state;
        state.Updated += OnUpdated;
        _renderer ??= new ScopeRenderer();
        InvalidateVisual();
    }

    private void OnUpdated() =>
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_state is not null)
            _state.Updated -= OnUpdated;
        _renderer?.Dispose();
        _renderer = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context) =>
        context.Custom(new DrawOp(new Rect(Bounds.Size), _state, _renderer));

    private sealed class DrawOp(Rect bounds, ScopeState? state, ScopeRenderer? renderer) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public void Dispose() { }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (state is null || renderer is null
                || context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            var rect = SKRect.Create((float)Bounds.Width, (float)Bounds.Height);
            int checkpoint = canvas.Save();
            canvas.ClipRect(rect);
            try
            {
                renderer.Draw(canvas, rect, state.Data);
            }
            finally
            {
                canvas.RestoreToCount(checkpoint);
            }
        }
    }
}
