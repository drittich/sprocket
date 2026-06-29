using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Rendering;

/// <summary>
/// The seam between Core and the Media layer: "give me source <paramref name="media"/>'s frame nearest
/// <paramref name="sourceTime"/>" (ARCHITECTURE.md §5). Core never sees FFmpeg or Skia — the image type
/// is a type parameter the Render/Media layers bind to <c>SKImage</c>, and tests bind to a fake.
/// </summary>
/// <typeparam name="TImage">The concrete frame/image type supplied by the implementing layer.</typeparam>
public interface IFrameSource<TImage>
{
    /// <summary>Returns the source frame nearest <paramref name="sourceTime"/> for the given media.</summary>
    TImage GetFrame(MediaRefId media, Timecode sourceTime);
}

/// <summary>
/// The seam between Core and the Render layer: the GPU operations the render graph drives to turn a
/// <see cref="VideoFramePlan"/> into one composited image (ARCHITECTURE.md §5, §7). Core owns the
/// <em>order</em> of operations; the implementation owns the pixels.
/// </summary>
/// <typeparam name="TImage">The concrete image/surface type (e.g. <c>SKImage</c>).</typeparam>
public interface IVideoCompositor<TImage>
{
    /// <summary>Creates a fresh transparent surface of the target size to composite onto.</summary>
    TImage CreateTransparentSurface(Resolution size);

    /// <summary>
    /// Draws a generator's procedural content (title/text, colour matte) into a fresh <paramref name="size"/>
    /// image at generator-local time <paramref name="localTime"/> (PLAN.md step 19). The result enters the effect
    /// chain exactly like a decoded frame, so generator layers carry effects and composite like any other.
    /// </summary>
    TImage CreateGeneratorFrame(ResolvedGenerator generator, Resolution size, Timecode localTime);

    /// <summary>
    /// Applies one effect to <paramref name="frame"/>, returning the result. Effects are applied in
    /// order so each call's output is the next call's input (the chained shader graph of §7).
    /// </summary>
    TImage ApplyEffect(TImage frame, ResolvedEffect effect);

    /// <summary>Composites <paramref name="layer"/> onto <paramref name="surface"/> with the given opacity and blend mode.</summary>
    void Composite(TImage surface, TImage layer, double opacity, BlendMode blendMode);

    /// <summary>Snapshots the finished surface into an immutable image (the composited frame).</summary>
    TImage Snapshot(TImage surface);
}
