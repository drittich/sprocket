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
/// Moves a clip from one track to another, setting its timeline start in the same step (PLAN.md step 16e —
/// cross-track drag). The clip object is unchanged apart from its <see cref="Clip.TimelineStart"/>; only which
/// <see cref="Track.Clips"/> list owns it changes (a clip carries no track reference — its track is the list it
/// lives in). Undo restores the original track <em>at the original index</em> (z-order safe, like
/// <see cref="RemoveClipCommand"/>) and the original start. <see cref="Clip.SourceIn"/>/<see cref="Clip.SourceOut"/>
/// and <see cref="Clip.LinkGroupId"/> are untouched. Not coalescing — a track move is one discrete gesture
/// (the cross-track drag commits exactly one command on release, so it is already a single undo entry).
/// </summary>
public sealed class MoveClipToTrackCommand : EditCommand
{
    private readonly Track _from;
    private readonly Track _to;
    private readonly Clip _clip;
    private readonly Timecode _oldStart;
    private readonly Timecode _newStart;
    private int _index = -1;

    /// <summary>Captures the clip's current start and records the destination track + new start to apply.</summary>
    public MoveClipToTrackCommand(Track from, Track to, Clip clip, Timecode newStart, string label = "Move clip to track")
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(clip);
        _from = from;
        _to = to;
        _clip = clip;
        _oldStart = clip.TimelineStart;
        _newStart = newStart;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _index = _from.Clips.IndexOf(_clip);
        if (_index >= 0)
            _from.Clips.RemoveAt(_index);
        _clip.TimelineStart = _newStart;
        _to.Clips.Add(_clip);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _to.Clips.Remove(_clip);
        _clip.TimelineStart = _oldStart;
        if (_index < 0)
            _from.Clips.Add(_clip);
        else
            _from.Clips.Insert(Math.Min(_index, _from.Clips.Count), _clip);
    }
}

/// <summary>
/// Ripple-trims one edge of a clip and shifts a captured set of downstream clips so the track stays gap-free
/// (PLAN.md step 22, the Ripple Edit tool). Unlike a plain trim, the clip's <see cref="Clip.TimelineStart"/> is
/// fixed for <em>both</em> edges: an OUT trim changes <see cref="Clip.SourceOut"/>, an IN trim changes
/// <see cref="Clip.SourceIn"/>, and every clip after the clip's original end shifts by the resulting duration
/// change (<paramref name="shiftTicks"/>). Coalesces with further ripple-trims of the same clip so a drag is one
/// undo entry. The downstream set is captured once (at drag start) by the caller and passed unchanged on every
/// update; <see cref="Apply"/> re-derives each downstream start from its captured original plus the latest shift,
/// so repeated coalesced applies stay exact.
/// </summary>
public sealed class RippleTrimCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly Timecode _oldIn, _oldOut;
    private Timecode _newIn, _newOut;
    private readonly IReadOnlyList<(Clip Clip, Timecode OrigStart)> _downstream;
    private long _shift;

    /// <summary>Captures the clip's current trim and records the new in/out plus the downstream shift to apply.</summary>
    public RippleTrimCommand(
        Clip clip, Timecode newSourceIn, Timecode newSourceOut,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> downstream, long shiftTicks)
        : base("Ripple trim")
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(downstream);
        if (newSourceOut < newSourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(newSourceOut));
        _clip = clip;
        _oldIn = clip.SourceIn;
        _oldOut = clip.SourceOut;
        _newIn = newSourceIn;
        _newOut = newSourceOut;
        _downstream = downstream;
        _shift = shiftTicks;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _clip.SourceIn = _newIn;
        _clip.SourceOut = _newOut;
        foreach ((Clip d, Timecode origStart) in _downstream)
            d.TimelineStart = new Timecode(origStart.Ticks + _shift);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _clip.SourceIn = _oldIn;
        _clip.SourceOut = _oldOut;
        foreach ((Clip d, Timecode origStart) in _downstream)
            d.TimelineStart = origStart;
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        // Keep this entry's captured originals (and downstream set); absorb only the latest target + shift.
        if (next is RippleTrimCommand other && ReferenceEquals(other._clip, _clip))
        {
            _newIn = other._newIn;
            _newOut = other._newOut;
            _shift = other._shift;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Rolls the shared cut between two adjacent clips (PLAN.md step 22, the Rolling Edit tool): the left clip's
/// out-point and the right clip's in-point + start move together so the cut shifts while the clips' combined
/// span — and everything downstream — stays fixed. The caller computes the clamped new source/timeline values
/// (it owns the speed and media bounds); this command just applies/reverts them and coalesces with further rolls
/// of the same pair so a drag is one undo entry.
/// </summary>
public sealed class RollEditCommand : EditCommand
{
    private readonly Clip _left, _right;
    private readonly Timecode _oldLeftOut, _oldRightIn, _oldRightStart;
    private Timecode _newLeftOut, _newRightIn, _newRightStart;

    /// <summary>Captures both clips' current edge/placement and records the rolled values to apply.</summary>
    public RollEditCommand(Clip left, Clip right, Timecode newLeftOut, Timecode newRightIn, Timecode newRightStart)
        : base("Roll edit")
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (newLeftOut < left.SourceIn)
            throw new ArgumentException("The left clip's out-point cannot precede its in-point.", nameof(newLeftOut));
        if (newRightIn > right.SourceOut)
            throw new ArgumentException("The right clip's in-point cannot follow its out-point.", nameof(newRightIn));
        _left = left;
        _right = right;
        _oldLeftOut = left.SourceOut;
        _oldRightIn = right.SourceIn;
        _oldRightStart = right.TimelineStart;
        _newLeftOut = newLeftOut;
        _newRightIn = newRightIn;
        _newRightStart = newRightStart;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _left.SourceOut = _newLeftOut;
        _right.SourceIn = _newRightIn;
        _right.TimelineStart = _newRightStart;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _left.SourceOut = _oldLeftOut;
        _right.SourceIn = _oldRightIn;
        _right.TimelineStart = _oldRightStart;
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is RollEditCommand other && ReferenceEquals(other._left, _left) && ReferenceEquals(other._right, _right))
        {
            _newLeftOut = other._newLeftOut;
            _newRightIn = other._newRightIn;
            _newRightStart = other._newRightStart;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Slides a clip along the timeline while its neighbours absorb the change (PLAN.md step 22, the Slide tool —
/// the complement of slip): the clip's source window is unchanged and it simply moves, the previous clip's
/// out-point extends/retracts to follow, and the next clip's in-point + start move so it stays butted. The slid
/// clip's duration and everything beyond the next clip are fixed. <paramref name="prev"/> / <paramref name="next"/>
/// may be <see langword="null"/> when there is no adjacent neighbour on that side (that side is simply not
/// adjusted). The caller computes the clamped values; this command applies/reverts them and coalesces per gesture.
/// </summary>
public sealed class SlideClipCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly Timecode _oldStart;
    private Timecode _newStart;
    private readonly Clip? _prev;
    private readonly Timecode _oldPrevOut;
    private Timecode _newPrevOut;
    private readonly Clip? _next;
    private readonly Timecode _oldNextIn, _oldNextStart;
    private Timecode _newNextIn, _newNextStart;

    /// <summary>Captures the clip's and neighbours' current placement and records the slid values to apply.</summary>
    public SlideClipCommand(
        Clip clip, Timecode newStart,
        Clip? prev, Timecode newPrevOut,
        Clip? next, Timecode newNextIn, Timecode newNextStart)
        : base("Slide clip")
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (prev is not null && newPrevOut < prev.SourceIn)
            throw new ArgumentException("The previous clip's out-point cannot precede its in-point.", nameof(newPrevOut));
        if (next is not null && newNextIn > next.SourceOut)
            throw new ArgumentException("The next clip's in-point cannot follow its out-point.", nameof(newNextIn));
        _clip = clip;
        _oldStart = clip.TimelineStart;
        _newStart = newStart;
        _prev = prev;
        _oldPrevOut = prev?.SourceOut ?? default;
        _newPrevOut = newPrevOut;
        _next = next;
        _oldNextIn = next?.SourceIn ?? default;
        _oldNextStart = next?.TimelineStart ?? default;
        _newNextIn = newNextIn;
        _newNextStart = newNextStart;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _clip.TimelineStart = _newStart;
        if (_prev is not null)
            _prev.SourceOut = _newPrevOut;
        if (_next is not null)
        {
            _next.SourceIn = _newNextIn;
            _next.TimelineStart = _newNextStart;
        }
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _clip.TimelineStart = _oldStart;
        if (_prev is not null)
            _prev.SourceOut = _oldPrevOut;
        if (_next is not null)
        {
            _next.SourceIn = _oldNextIn;
            _next.TimelineStart = _oldNextStart;
        }
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is SlideClipCommand other
            && ReferenceEquals(other._clip, _clip)
            && ReferenceEquals(other._prev, _prev)
            && ReferenceEquals(other._next, _next))
        {
            _newStart = other._newStart;
            _newPrevOut = other._newPrevOut;
            _newNextIn = other._newNextIn;
            _newNextStart = other._newNextStart;
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

/// <summary>
/// Appends an effect to an audio effect chain — a track's insert chain (<see cref="AudioTrack.Effects"/>), a
/// sequence bus (<see cref="Timeline.AudioEffects"/>), or the project master chain
/// (<see cref="ProjectSettings.MasterAudioEffects"/>) — undo removes it (PLAN.md step 31). The clip-scope
/// chain is the clip's ordinary effect stack, edited by <see cref="AddEffectCommand"/>.
/// </summary>
public sealed class AddChainEffectCommand(IList<EffectInstance> chain, EffectInstance effect)
    : EditCommand("Add audio effect")
{
    /// <inheritdoc />
    public override void Apply() => chain.Add(effect);

    /// <inheritdoc />
    public override void Revert() => chain.Remove(effect);
}

/// <summary>Removes an effect from an audio effect chain; undo re-inserts it at the same position — chain
/// order is the processing order (PLAN.md step 31).</summary>
public sealed class RemoveChainEffectCommand(IList<EffectInstance> chain, EffectInstance effect)
    : EditCommand("Remove audio effect")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = chain.IndexOf(effect);
        if (_index >= 0)
            chain.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        chain.Insert(Math.Min(_index, chain.Count), effect);
    }
}

/// <summary>Toggles an effect's <see cref="EffectInstance.Enabled"/> flag; undo restores the prior state. Disabling
/// (rather than removing) keeps parameters/keyframes intact for later re-enabling, and — since <c>Enabled</c> is
/// part of the persisted/hashed effect state — invalidates any render-cache segment covering the clip.</summary>
public sealed class SetEffectEnabledCommand : EditCommand
{
    private readonly EffectInstance _effect;
    private readonly bool _oldEnabled;
    private readonly bool _newEnabled;

    /// <summary>Captures the effect's current enabled state and records the new one to apply.</summary>
    public SetEffectEnabledCommand(EffectInstance effect, bool enabled) : base(enabled ? "Enable effect" : "Disable effect")
    {
        ArgumentNullException.ThrowIfNull(effect);
        _effect = effect;
        _oldEnabled = effect.Enabled;
        _newEnabled = enabled;
    }

    /// <inheritdoc />
    public override void Apply() => _effect.Enabled = _newEnabled;

    /// <inheritdoc />
    public override void Revert() => _effect.Enabled = _oldEnabled;
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

/// <summary>
/// Adds a sequence to the project; undo removes it (PLAN.md step 23). Creating a sequence — including the
/// new child sequence a "nest selected clips" gesture builds — goes through the command stack so it is undoable
/// and flips the dirty indicator. Switching the <em>active</em> sequence is navigation, not a model edit, so it
/// is not a command; undo therefore never strips the active sequence (the nest flow leaves the parent active).
/// </summary>
public sealed class AddSequenceCommand(Project project, Sequence sequence) : EditCommand("Add sequence")
{
    /// <inheritdoc />
    public override void Apply() => project.Sequences.Add(sequence);

    /// <inheritdoc />
    public override void Revert() => project.Sequences.Remove(sequence);
}

/// <summary>
/// Removes a sequence from the project; undo re-inserts it at the same index (PLAN.md step 23). The active
/// sequence can't be removed (switch away first), and a sequence still referenced by a nested-sequence clip
/// removes anyway — the dangling reference renders as nothing (§15), and undo restores it.
/// </summary>
public sealed class RemoveSequenceCommand : EditCommand
{
    private readonly Project _project;
    private readonly Sequence _sequence;
    private int _index = -1;

    /// <summary>Captures the sequence to remove.</summary>
    public RemoveSequenceCommand(Project project, Sequence sequence) : base("Remove sequence")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (ReferenceEquals(project.ActiveSequence, sequence))
            throw new InvalidOperationException("The active sequence cannot be removed; switch to another sequence first.");
        _project = project;
        _sequence = sequence;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _index = _project.Sequences.IndexOf(_sequence);
        if (_index >= 0)
            _project.Sequences.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        _project.Sequences.Insert(Math.Min(_index, _project.Sequences.Count), _sequence);
    }
}

/// <summary>
/// Adds a synced multicam source to the project; undo removes it (PLAN.md step 24). Building a multicam source
/// goes through the command stack so it is undoable and flips the dirty indicator. A clip referencing a removed
/// source renders as nothing (§15), and undo restores it.
/// </summary>
public sealed class AddMulticamSourceCommand(Project project, MulticamSource source) : EditCommand("Add multicam source")
{
    /// <inheritdoc />
    public override void Apply() => project.MulticamSources.Add(source);

    /// <inheritdoc />
    public override void Revert() => project.MulticamSources.Remove(source);
}

/// <summary>Removes a multicam source from the project; undo re-inserts it at the same index (PLAN.md step 24).</summary>
public sealed class RemoveMulticamSourceCommand : EditCommand
{
    private readonly Project _project;
    private readonly MulticamSource _source;
    private int _index = -1;

    /// <summary>Captures the source to remove.</summary>
    public RemoveMulticamSourceCommand(Project project, MulticamSource source) : base("Remove multicam source")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        _project = project;
        _source = source;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _index = _project.MulticamSources.IndexOf(_source);
        if (_index >= 0)
            _project.MulticamSources.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        _project.MulticamSources.Insert(Math.Min(_index, _project.MulticamSources.Count), _source);
    }
}

/// <summary>
/// Sets a multicam clip's active angle (PLAN.md step 24, an angle switch / cut). Not coalescing — each switch is
/// a discrete edit (live cutting splits the clip first, so each segment's angle is set once). Undo restores the
/// previous angle.
/// </summary>
public sealed class SetClipAngleCommand : EditCommand
{
    private readonly Clip _clip;
    private readonly int _oldAngle;
    private readonly int _newAngle;

    /// <summary>Captures the clip's current angle and records the new one to apply.</summary>
    public SetClipAngleCommand(Clip clip, int newAngle) : base("Switch angle")
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (newAngle < 0)
            throw new ArgumentOutOfRangeException(nameof(newAngle), "The angle index must be non-negative.");
        _clip = clip;
        _oldAngle = clip.ActiveAngle;
        _newAngle = newAngle;
    }

    /// <inheritdoc />
    public override void Apply() => _clip.ActiveAngle = _newAngle;

    /// <inheritdoc />
    public override void Revert() => _clip.ActiveAngle = _oldAngle;
}

/// <summary>
/// Re-syncs a multicam source by setting every angle's <see cref="MulticamAngle.SyncOffset"/> at once (PLAN.md
/// step 24, after a sync pass — by timecode, markers, or audio cross-correlation). Undo restores the previous
/// offsets. One offset per angle.
/// </summary>
public sealed class SetMulticamOffsetsCommand : EditCommand
{
    private readonly MulticamSource _source;
    private readonly Timecode[] _oldOffsets;
    private readonly Timecode[] _newOffsets;

    /// <summary>Captures the source's current offsets and records the new ones to apply.</summary>
    public SetMulticamOffsetsCommand(MulticamSource source, IReadOnlyList<Timecode> newOffsets) : base("Sync angles")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(newOffsets);
        if (newOffsets.Count != source.Angles.Count)
            throw new ArgumentException("There must be one offset per angle.", nameof(newOffsets));
        _source = source;
        _oldOffsets = source.Angles.Select(a => a.SyncOffset).ToArray();
        _newOffsets = newOffsets.ToArray();
    }

    /// <inheritdoc />
    public override void Apply()
    {
        for (int i = 0; i < _source.Angles.Count; i++)
            _source.Angles[i].SyncOffset = _newOffsets[i];
    }

    /// <inheritdoc />
    public override void Revert()
    {
        for (int i = 0; i < _source.Angles.Count; i++)
            _source.Angles[i].SyncOffset = _oldOffsets[i];
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

/// <summary>
/// Adds a transition to a track's cut (PLAN.md step 25); undo removes it. Applying a transition goes through the
/// command stack like every other model mutation (step 10), so it is undoable and flips the dirty indicator.
/// </summary>
public sealed class AddTransitionCommand(Track track, Transition transition) : EditCommand("Add transition")
{
    /// <inheritdoc />
    public override void Apply() => track.Transitions.Add(transition);

    /// <inheritdoc />
    public override void Revert() => track.Transitions.Remove(transition);
}

/// <summary>Removes a transition from a track; undo re-inserts it at the same position (PLAN.md step 25).</summary>
public sealed class RemoveTransitionCommand(Track track, Transition transition) : EditCommand("Remove transition")
{
    private int _index = -1;

    /// <inheritdoc />
    public override void Apply()
    {
        _index = track.Transitions.IndexOf(transition);
        if (_index >= 0)
            track.Transitions.RemoveAt(_index);
    }

    /// <inheritdoc />
    public override void Revert()
    {
        if (_index < 0)
            return;
        track.Transitions.Insert(Math.Min(_index, track.Transitions.Count), transition);
    }
}

/// <summary>
/// Sets a transition's window — its duration and alignment — atomically (PLAN.md step 25). Used when the user
/// changes a transition's length (e.g. dragging its edge or via the inspector). Coalesces with further changes
/// to the same transition so a drag is one undo entry. The cut point is fixed (it is the edit the transition
/// sits on); only the span around it changes.
/// </summary>
public sealed class SetTransitionWindowCommand : EditCommand
{
    private readonly Transition _transition;
    private readonly Timecode _oldDuration;
    private readonly TransitionAlignment _oldAlignment;
    private Timecode _newDuration;
    private TransitionAlignment _newAlignment;

    /// <summary>Captures the transition's current window and records the new duration/alignment to apply.</summary>
    public SetTransitionWindowCommand(Transition transition, Timecode newDuration, TransitionAlignment newAlignment)
        : base("Adjust transition")
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (newDuration.Ticks <= 0)
            throw new ArgumentOutOfRangeException(nameof(newDuration), "Transition duration must be strictly positive.");
        _transition = transition;
        _oldDuration = transition.Duration;
        _oldAlignment = transition.Alignment;
        _newDuration = newDuration;
        _newAlignment = newAlignment;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _transition.Duration = _newDuration;
        _transition.Alignment = _newAlignment;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _transition.Duration = _oldDuration;
        _transition.Alignment = _oldAlignment;
    }

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (next is SetTransitionWindowCommand other && ReferenceEquals(other._transition, _transition))
        {
            _newDuration = other._newDuration;
            _newAlignment = other._newAlignment;
            return true;
        }
        return false;
    }
}
