using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Sprocket.Spike;

/// <summary>
/// The architecture spike's render surface. Decodes one frame, caches it as an SKImage,
/// and on every frame applies an animated brightness <see cref="SKRuntimeEffect"/> on the
/// GPU, drawing into Avalonia's shared <c>GRContext</c> via an <see cref="ICustomDrawOperation"/>.
/// Reports fps and per-frame managed allocation so the no-managed-pixels claim is measurable.
/// </summary>
public sealed class BrightnessPreviewControl : Control
{
    private const string BrightnessSksl = @"
uniform shader src;
uniform float amount;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    return half4(c.rgb * amount, c.a);
}";

    public event Action<string>? StatsUpdated;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private DecodedFrame? _frame;
    private SKImage? _image;
    private SKRuntimeEffect? _effect;
    private SKRuntimeEffectUniforms? _uniforms;
    private SKRuntimeEffectChildren? _children;
    private SKPaint? _paint;
    private SKShader? _imageShader;
    private SKSize _imageShaderSize;
    private string? _error;

    private float _brightness = 1f;
    private bool _gpuConfirmed;

    // stats accounting
    private int _frameCount, _lastFrameCount, _baseG0, _baseG1, _baseG2;
    private long _lastStatsTicks, _lastAllocBytes;

    public BrightnessPreviewControl()
    {
        try
        {
            string path = TestAsset.EnsureExists();
            _frame = DecodedFrame.DecodeFirst(path);

            var info = new SKImageInfo(_frame.Width, _frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            _image = SKImage.FromPixels(info, _frame.Pixels, _frame.RowBytes);

            _effect = SKRuntimeEffect.CreateShader(BrightnessSksl, out string errors)
                ?? throw new InvalidOperationException($"SkSL compile failed: {errors}");
            _uniforms = new SKRuntimeEffectUniforms(_effect);
            _children = new SKRuntimeEffectChildren(_effect);
            _paint = new SKPaint();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        _baseG0 = GC.CollectionCount(0);
        _baseG1 = GC.CollectionCount(1);
        _baseG2 = GC.CollectionCount(2);
        _lastAllocBytes = GC.GetTotalAllocatedBytes();
        _lastStatsTicks = _clock.ElapsedTicks;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Animate brightness so every tick is a genuine re-render of the shader pipeline.
        double t = _clock.Elapsed.TotalSeconds;
        _brightness = 1.0f + 0.6f * (float)Math.Sin(t * 2.0);

        // Stats are emitted here (a timer callback), never inside Render() — Avalonia 12
        // forbids invalidating any visual during the render pass.
        CollectStats();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new DrawOp(this, new Rect(Bounds.Size), _brightness));
        _frameCount++;
    }

    // Runs on the render thread, inside the Skia API lease.
    private void DrawSkia(SKCanvas canvas, GRContext? grContext, Rect bounds, float brightness)
    {
        _gpuConfirmed = grContext is not null;
        canvas.Clear(new SKColor(0x0E, 0x0E, 0x12));

        if (_error is not null || _image is null || _effect is null || _paint is null)
            return;

        float bw = (float)bounds.Width, bh = (float)bounds.Height;
        if (bw <= 0 || bh <= 0)
            return;

        float scale = Math.Min(bw / _image.Width, bh / _image.Height);
        float dw = _image.Width * scale, dh = _image.Height * scale;
        float dx = (bw - dw) / 2f, dy = (bh - dh) / 2f;

        // Rebuild the image shader only when the fit rectangle changes (not per frame).
        var size = new SKSize(bw, bh);
        if (_imageShader is null || size != _imageShaderSize)
        {
            _imageShader?.Dispose();
            SKMatrix localMatrix = SKMatrix.CreateScaleTranslation(scale, scale, dx, dy);
            _imageShader = _image.ToShader(
                SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear), localMatrix);
            _imageShaderSize = size;
        }

        _uniforms!["amount"] = brightness;
        _children!["src"] = _imageShader;

        // The one acknowledged per-frame allocation (ARCHITECTURE.md §7): ToShader snapshots
        // uniforms. It is a small bounded managed object, never pixel data.
        using SKShader shader = _effect.ToShader(_uniforms, _children);
        _paint.Shader = shader;
        canvas.DrawRect(SKRect.Create(dx, dy, dw, dh), _paint);
    }

    private void CollectStats()
    {
        long now = _clock.ElapsedTicks;
        double elapsed = (now - _lastStatsTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 1.0)
            return;

        int frames = _frameCount - _lastFrameCount;
        double fps = frames / elapsed;

        long allocNow = GC.GetTotalAllocatedBytes();
        double perFrame = frames > 0 ? (allocNow - _lastAllocBytes) / (double)frames : 0;
        int g0 = GC.CollectionCount(0) - _baseG0;
        int g1 = GC.CollectionCount(1) - _baseG1;
        int g2 = GC.CollectionCount(2) - _baseG2;

        string render = _error is not null
            ? $"ERROR: {_error}"
            : (_gpuConfirmed ? "GPU (shared GRContext)" : "raster (no GRContext)");

        string text =
            $"{_image?.Width}x{_image?.Height}   {fps,5:0.0} fps   {render}\n" +
            $"alloc/frame: {perFrame,7:0} B   GC gen0/1/2: {g0}/{g1}/{g2}   brightness: {_brightness:0.00}";
        StatsUpdated?.Invoke(text);
        Console.WriteLine($"[stats] {text.Replace("\n", "  |  ")}");

        _lastStatsTicks = now;
        _lastFrameCount = _frameCount;
        _lastAllocBytes = allocNow;
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly BrightnessPreviewControl _owner;
        private readonly float _brightness;

        public DrawOp(BrightnessPreviewControl owner, Rect bounds, float brightness)
        {
            _owner = owner;
            Bounds = bounds;
            _brightness = brightness;
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
            _owner.DrawSkia(lease.SkCanvas, lease.GrContext, Bounds, _brightness);
        }
    }
}
