using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Commands;

// Structural edits to the timeline model, expressed as reversible commands (ARCHITECTURE.md §4). Single-field
// scalar edits (a clip's start, a track's gain/opacity/mute) use the generic SetPropertyCommand<T>; the
// commands here cover the list/structure mutations and the multi-field clip trim that don't fit that shape.
// All run through EditHistory so the editor is undoable by construction (PLAN.md step 10).

/// <summary>Adds a clip to a track; undo removes it.</summary>
public sealed class AddClipCommand(Track track, Clip clip) : EditCommand("Add clip")
{
    /// <inheritdoc />
    public override void Apply() => track.Clips.Add(clip);

    /// <inheritdoc />
    public override void Revert() => track.Clips.Remove(clip);
}

/// <summary>Removes a clip from a track; undo re-inserts it at the same position so z-order is preserved.</summary>
public sealed class RemoveClipCommand(Track track, Clip clip) : EditCommand("Remove clip")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = track.Clips.IndexOf(clip);
        if (_index >= 0)
            track.Clips.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        track.Clips.Insert(Math.Min(_index, track.Clips.Count), clip);
    }
}

/// <summary>
/// Re-trims a clip (its source in/out span). Coalesces with further trims of the same clip so a drag on a trim
/// handle is one undo entry. Moving a clip along the timeline is a <see cref="SetPropertyCommand{T}"/> on
/// <see cref="Clip.TimelineStart"/> keyed on the clip; trimming changes two fields, hence this command.
/// </summary>
public sealed class TrimClipCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly Timecode _oldIn;
    private readonly Timecode _oldOut;
    private Timecode _newIn;
    private Timecode _newOut;

    /// <summary>Captures the clip's current trim and records the new in/out points to apply.</summary>
    public TrimClipCommand(Clip clip, Timecode newSourceIn, Timecode newSourceOut)
        : base("Trim clip")
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (newSourceOut < newSourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(newSourceOut));
        _clip = clip;
        _oldIn = clip.SourceIn;
        _oldOut = clip.SourceOut;
        _newIn = newSourceIn;
        _newOut = newSourceOut;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _clip.SourceIn = _newIn;
        _clip.SourceOut = _newOut;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _clip.SourceIn = _oldIn;
        _clip.SourceOut = _oldOut;
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is TrimClipCommand other && ReferenceEquals(other._clip, _clip))
        {
            _newIn = other._newIn;
            _newOut = other._newOut;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Sets a clip's placement — source in/out <em>and</em> timeline start — atomically. This is the primitive a
/// timeline drag uses: a move changes only <see cref="Clip.TimelineStart"/>; a right-edge trim changes
/// <see cref="Clip.SourceOut"/>; a left-edge trim changes <see cref="Clip.SourceIn"/> and the start together
/// (the right edge stays put); a slip changes both source points but not the start. Coalesces with further
/// placements of the same clip so a whole drag is one undo entry.
/// </summary>
public sealed class SetClipPlacementCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly Timecode _oldIn, _oldOut, _oldStart;
    private Timecode _newIn, _newOut, _newStart;

    /// <summary>Captures the clip's current placement and records the new one to apply.</summary>
    public SetClipPlacementCommand(
        Clip clip, Timecode newSourceIn, Timecode newSourceOut, Timecode newTimelineStart, string label = "Move clip")
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (newSourceOut < newSourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(newSourceOut));
        _clip = clip;
        _oldIn = clip.SourceIn;
        _oldOut = clip.SourceOut;
        _oldStart = clip.TimelineStart;
        _newIn = newSourceIn;
        _newOut = newSourceOut;
        _newStart = newTimelineStart;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _clip.SourceIn = _newIn;
        _clip.SourceOut = _newOut;
        _clip.TimelineStart = _newStart;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _clip.SourceIn = _oldIn;
        _clip.SourceOut = _oldOut;
        _clip.TimelineStart = _oldStart;
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is SetClipPlacementCommand other && ReferenceEquals(other._clip, _clip))
        {
            _newIn = other._newIn;
            _newOut = other._newOut;
            _newStart = other._newStart;
            return true;
        }
        return false;
    }
}

/// <summary>Appends an effect to a clip's effect stack; undo removes it.</summary>
public sealed class AddEffectCommand(Clip clip, EffectInstance effect) : EditCommand("Add effect")
{
    /// <inheritdoc />
    public override void Apply() => clip.Effects.Add(effect);

    /// <inheritdoc />
    public override void Revert() => clip.Effects.Remove(effect);
}

/// <summary>Removes an effect from a clip; undo re-inserts it at the same stack position (order matters, §5d).</summary>
public sealed class RemoveEffectCommand(Clip clip, EffectInstance effect) : EditCommand("Remove effect")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = clip.Effects.IndexOf(effect);
        if (_index >= 0)
            clip.Effects.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        clip.Effects.Insert(Math.Min(_index, clip.Effects.Count), effect);
    }
}

/// <summary>
/// Sets one effect parameter to a new <see cref="AnimatableValue"/> (e.g. brightness amount, fade opacity).
/// Coalesces with further sets of the same parameter on the same effect, so dragging a parameter slider is a
/// single undo entry.
/// </summary>
public sealed class SetEffectParameterCommand : EditCommand
{
    private readonly EffectInstance _effect;
    private readonly string _name;
    private readonly AnimatableValue? _oldValue;
    private readonly bool _hadValue;
    private AnimatableValue _newValue;

    /// <summary>Captures the parameter's current value (if any) and records the new one to apply.</summary>
    public SetEffectParameterCommand(EffectInstance effect, string name, AnimatableValue newValue)
        : base($"Set {name}")
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(newValue);
        _effect = effect;
        _name = name;
        _hadValue = effect.Parameters.TryGetValue(name, out AnimatableValue? existing);
        _oldValue = existing;
        _newValue = newValue;
    }

    /// <inheritdoc />
    public override void Apply() => _effect.Parameters[_name] = _newValue;

    /// <inheritdoc />
    public override void Revert()
    {
        if (_hadValue)
            _effect.Parameters[_name] = _oldValue!;
        else
            _effect.Parameters.Remove(_name);
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is SetEffectParameterCommand other
            && ReferenceEquals(other._effect, _effect)
            && other._name == _name)
        {
            _newValue = other._newValue;
            return true;
        }
        return false;
    }
}

/// <summary>Adds a track to the timeline (appended on top in z-order); undo removes it.</summary>
public sealed class AddTrackCommand(Timeline timeline, Track track) : EditCommand("Add track")
{
    /// <inheritdoc />
    public override void Apply() => timeline.Tracks.Add(track);

    /// <inheritdoc />
    public override void Revert() => timeline.Tracks.Remove(track);
}

/// <summary>Removes a track from the timeline; undo re-inserts it at the same z-order index.</summary>
public sealed class RemoveTrackCommand(Timeline timeline, Track track) : EditCommand("Remove track")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = timeline.Tracks.IndexOf(track);
        if (_index >= 0)
            timeline.Tracks.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        timeline.Tracks.Insert(Math.Min(_index, timeline.Tracks.Count), track);
    }
}
