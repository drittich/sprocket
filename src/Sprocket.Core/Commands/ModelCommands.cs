using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Commands;

// Structural edits to the timeline model, expressed as reversible commands (ARCHITECTURE.md §4). Single-field
// scalar edits (a clip's start, a track's gain/opacity/mute) use the generic SetPropertyCommand<T>; the
// commands here cover the list/structure mutations and the multi-field clip trim that don't fit that shape.
// All run through EditHistory so the editor is undoable by construction (PLAN.md step 10).

/// <summary>
/// Imports a source into the project's <see cref="MediaPool"/>; undo removes it again (PLAN.md step 16b).
/// Import goes through the command stack like every other model mutation (step 10), so it is undoable and
/// flips the dirty indicator. Clips reference media by id, so undoing an import while a clip still uses the
/// source leaves that clip offline (renders as black/silence, §15) rather than corrupting the model.
/// </summary>
public sealed class AddMediaCommand(MediaPool pool, MediaRef media) : EditCommand("Import media")
{
    /// <inheritdoc />
    public override void Apply() => pool.Add(media);

    /// <inheritdoc />
    public override void Revert() => pool.Remove(media.Id);
}

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

/// <summary>
/// Sets a clip's playback speed (retime, PLAN.md step 21). The selected source span is unchanged; only the
/// clip's timeline <see cref="Clip.Duration"/> and its time map derive from the new <see cref="Clip.SpeedRatio"/>.
/// Coalesces with further speed changes of the same clip so dragging the speed control is one undo entry.
/// </summary>
public sealed class SetClipSpeedCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly Rational _oldSpeed;
    private Rational _newSpeed;

    /// <summary>Captures the clip's current speed and records the new (strictly positive) ratio to apply.</summary>
    public SetClipSpeedCommand(Clip clip, Rational newSpeed) : base("Change speed")
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (newSpeed.Num <= 0)
            throw new ArgumentOutOfRangeException(nameof(newSpeed), "Speed must be strictly positive.");
        _clip = clip;
        _oldSpeed = clip.SpeedRatio;
        _newSpeed = newSpeed;
    }

    /// <inheritdoc />
    public override void Apply() => _clip.SpeedRatio = _newSpeed;

    /// <inheritdoc />
    public override void Revert() => _clip.SpeedRatio = _oldSpeed;

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is SetClipSpeedCommand other && ReferenceEquals(other._clip, _clip))
        {
            _newSpeed = other._newSpeed;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Splits a clip in two at timeline time <paramref name="at"/> — the Blade (razor) op (PLAN.md step 13,
/// UI.md §3.2). The original clip becomes the left half (its <see cref="Clip.SourceOut"/> is pulled back to
/// the split point); a new right-half clip is inserted immediately after it, carrying the remaining source
/// span, a copy of the effect stack, and a link group. Undo removes the right half and restores the
/// original out-point. <paramref name="at"/> must lie strictly inside the clip.
/// </summary>
public sealed class SplitClipCommand : EditCommand
{
    private readonly Track _track;
    private readonly Clip _left;
    private readonly Clip _right;
    private readonly Timecode _oldOut;
    private readonly Timecode _splitSource;
    private int _rightIndex = -1;

    /// <summary>
    /// Prepares the split. <paramref name="rightLinkGroup"/> sets the new half's
    /// <see cref="Clip.LinkGroupId"/> (a linked blade gives every right half a fresh shared group so the two
    /// sides stay independently linked); when omitted the right half inherits the original's group.
    /// </summary>
    public SplitClipCommand(Track track, Clip clip, Timecode at, Guid? rightLinkGroup = null)
        : base("Split clip")
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (at <= clip.TimelineStart || at >= clip.TimelineEnd)
            throw new ArgumentException("The split point must lie strictly inside the clip.", nameof(at));

        _track = track;
        _left = clip;
        _oldOut = clip.SourceOut;
        _splitSource = clip.MapToSource(at);

        _right = clip.CloneContentForSpan(_splitSource, clip.SourceOut, at);
        _right.LinkGroupId = rightLinkGroup ?? clip.LinkGroupId;
        foreach (EffectInstance e in clip.Effects)
            _right.Effects.Add(e.Clone());
    }

    /// <summary>The new right-hand clip produced by the split (e.g. to select it after a blade).</summary>
    public Clip RightClip => _right;

    /// <inheritdoc />
    public override void Apply()
    {
        _left.SourceOut = _splitSource;
        int leftIndex = _track.Clips.IndexOf(_left);
        _rightIndex = leftIndex < 0 ? _track.Clips.Count : leftIndex + 1;
        _track.Clips.Insert(_rightIndex, _right);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _track.Clips.Remove(_right);
        _left.SourceOut = _oldOut;
    }
}

/// <summary>
/// Groups several commands into one undo entry, applied in order and reverted in reverse (PLAN.md step 13).
/// A linked edit — moving or blading a clip together with its companion A/V — is one user gesture, so it
/// must undo/redo as a unit. Coalesces with another <see cref="CompositeCommand"/> of the same shape (same
/// number of children, each child mergeable with its counterpart), so a continuous linked drag stays a
/// single undo entry inside a <see cref="EditHistory.BeginCoalescing"/> scope.
/// </summary>
public sealed class CompositeCommand : EditCommand
{
    private readonly IReadOnlyList<IEditCommand> _commands;

    /// <summary>Wraps <paramref name="commands"/> (none yet applied) as one reversible unit.</summary>
    public CompositeCommand(string label, IReadOnlyList<IEditCommand> commands)
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("A composite needs at least one command.", nameof(commands));
        _commands = commands;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        foreach (IEditCommand c in _commands)
            c.Apply();
    }

    /// <inheritdoc />
    public override void Revert()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Revert();
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is not CompositeCommand other || other._commands.Count != _commands.Count)
            return false;
        // All children must merge with their counterpart (no partial merge — the gesture is atomic).
        for (int i = 0; i < _commands.Count; i++)
            if (!_commands[i].TryMergeWith(other._commands[i]))
                return false;
        return true;
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

/// <summary>
/// Adds a marker to a marker list (a <see cref="Timeline.Markers"/> sequence list or a <see cref="Clip.Markers"/>
/// clip list); undo removes it (PLAN.md step 20). Markers go through the command stack like every other model
/// mutation so they are undoable and flip the dirty indicator.
/// </summary>
public sealed class AddMarkerCommand(IList<Marker> markers, Marker marker) : EditCommand("Add marker")
{
    /// <inheritdoc />
    public override void Apply() => markers.Add(marker);

    /// <inheritdoc />
    public override void Revert() => markers.Remove(marker);
}

/// <summary>Removes a marker from its list; undo re-inserts it at the same position.</summary>
public sealed class RemoveMarkerCommand(IList<Marker> markers, Marker marker) : EditCommand("Remove marker")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = markers.IndexOf(marker);
        if (_index >= 0)
            markers.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        markers.Insert(Math.Min(_index, markers.Count), marker);
    }
}

/// <summary>
/// Moves a marker to a new time (a ruler/clip-body drag). Coalesces with further moves of the same marker so a
/// drag is one undo entry, mirroring the clip-placement command (PLAN.md step 20).
/// </summary>
public sealed class MoveMarkerCommand : EditCommand
{
    private readonly Marker _marker;
    private readonly Timecode _oldTime;
    private Timecode _newTime;

    /// <summary>Captures the marker's current time and records the new one to apply.</summary>
    public MoveMarkerCommand(Marker marker, Timecode newTime) : base("Move marker")
    {
        ArgumentNullException.ThrowIfNull(marker);
        _marker = marker;
        _oldTime = marker.Time;
        _newTime = newTime;
    }

    /// <inheritdoc />
    public override void Apply() => _marker.Time = _newTime;

    /// <inheritdoc />
    public override void Revert() => _marker.Time = _oldTime;

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is MoveMarkerCommand other && ReferenceEquals(other._marker, _marker))
        {
            _newTime = other._newTime;
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
