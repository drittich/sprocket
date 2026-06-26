using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests for the undo/redo command stack (PLAN.md step 10, ARCHITECTURE.md §4): the <see cref="EditHistory"/>
/// stack semantics + coalescing, and that each command reverses cleanly against the real model. All headless.
/// </summary>
public class EditHistoryTests
{
    // A trivial counter-mutating command so the stack mechanics are tested independently of model commands.
    private sealed class Increment(int[] cell, int by, object? mergeKey = null) : EditCommand("inc")
    {
        public override void Apply() => cell[0] += by;
        public override void Revert() => cell[0] -= by;
        public object? MergeKey => mergeKey;
        // Merging keeps this (earlier) entry and drops the newer one; both are already applied.
        public override bool TryMergeWith(IEditCommand next)
            => next is Increment n && mergeKey is not null && mergeKey.Equals(n.MergeKey);
    }

    [Fact]
    public void Execute_Applies_And_Records()
    {
        int[] cell = [0];
        var history = new EditHistory();

        history.Execute(new Increment(cell, 5));

        Assert.Equal(5, cell[0]);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_Then_Redo_Restores_State()
    {
        int[] cell = [0];
        var history = new EditHistory();
        history.Execute(new Increment(cell, 5));
        history.Execute(new Increment(cell, 3));
        Assert.Equal(8, cell[0]);

        Assert.True(history.Undo());
        Assert.Equal(5, cell[0]);
        Assert.True(history.Undo());
        Assert.Equal(0, cell[0]);
        Assert.False(history.Undo()); // nothing left

        Assert.True(history.Redo());
        Assert.Equal(5, cell[0]);
        Assert.True(history.Redo());
        Assert.Equal(8, cell[0]);
        Assert.False(history.Redo());
    }

    [Fact]
    public void Executing_After_Undo_Discards_The_Redo_Stack()
    {
        int[] cell = [0];
        var history = new EditHistory();
        history.Execute(new Increment(cell, 5));
        history.Execute(new Increment(cell, 3));
        history.Undo(); // cell = 5, one entry on redo

        Assert.True(history.CanRedo);
        history.Execute(new Increment(cell, 100)); // new branch
        Assert.False(history.CanRedo);
        Assert.Equal(105, cell[0]);
    }

    [Fact]
    public void Coalescing_Scope_Merges_Compatible_Commands_Into_One_Entry()
    {
        int[] cell = [0];
        var history = new EditHistory();
        object key = "drag";

        using (history.BeginCoalescing())
        {
            history.Execute(new Increment(cell, 1, key));
            history.Execute(new Increment(cell, 1, key));
            history.Execute(new Increment(cell, 1, key));
        }

        Assert.Equal(3, cell[0]);
        // All three merged into a single undo entry: one undo reverses only the last, since merged commands
        // keep just the top entry. The merged entry's net effect is the last applied increment's revert.
        Assert.True(history.Undo());
        Assert.Equal(2, cell[0]); // the surviving (last) entry reverts its own +1
        Assert.False(history.CanUndo); // only one entry existed
    }

    [Fact]
    public void Without_A_Scope_Each_Command_Is_A_Separate_Entry()
    {
        int[] cell = [0];
        var history = new EditHistory();
        object key = "drag";

        history.Execute(new Increment(cell, 1, key));
        history.Execute(new Increment(cell, 1, key));

        // No coalescing scope → two entries despite equal merge keys.
        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.Equal(0, cell[0]);
    }

    [Fact]
    public void Different_Merge_Keys_Do_Not_Coalesce_Even_In_A_Scope()
    {
        int[] cell = [0];
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new Increment(cell, 1, "a"));
            history.Execute(new Increment(cell, 1, "b"));
        }

        Assert.Equal(2, cell[0]);
        Assert.Equal(2, history.UndoLabels.Count); // two distinct entries
    }

    [Fact]
    public void Labels_And_Clear_Reflect_The_Stacks()
    {
        int[] cell = [0];
        var history = new EditHistory();
        history.Execute(new Increment(cell, 1));
        history.Execute(new Increment(cell, 1));

        Assert.Equal("inc", history.UndoLabel);
        Assert.Equal(["inc", "inc"], history.UndoLabels);

        history.Undo();
        Assert.Equal("inc", history.RedoLabel);

        history.Clear();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.UndoLabel);
    }

    [Fact]
    public void Changed_Fires_On_Each_Mutation()
    {
        int[] cell = [0];
        var history = new EditHistory();
        int changes = 0;
        history.Changed += () => changes++;

        history.Execute(new Increment(cell, 1)); // 1
        history.Undo();                           // 2
        history.Redo();                           // 3
        history.Clear();                          // 4

        Assert.Equal(4, changes);
    }
}

/// <summary>Each concrete model command applies its edit and reverses it exactly against the real model.</summary>
public class ModelCommandTests
{
    private static Clip MakeClip() =>
        new(MediaRefId.New(), Timecode.FromSeconds(0), Timecode.FromSeconds(5), Timecode.FromSeconds(2));

    [Fact]
    public void AddClip_And_Undo()
    {
        var track = new VideoTrack();
        Clip clip = MakeClip();
        var history = new EditHistory();

        history.Execute(new AddClipCommand(track, clip));
        Assert.Single(track.Clips);

        history.Undo();
        Assert.Empty(track.Clips);

        history.Redo();
        Assert.Same(clip, Assert.Single(track.Clips));
    }

    [Fact]
    public void RemoveClip_Restores_Original_Index()
    {
        var track = new VideoTrack();
        Clip a = MakeClip(), b = MakeClip(), c = MakeClip();
        track.Clips.AddRange([a, b, c]);
        var history = new EditHistory();

        history.Execute(new RemoveClipCommand(track, b)); // remove the middle one
        Assert.Equal([a, c], track.Clips);

        history.Undo();
        Assert.Equal([a, b, c], track.Clips); // b is back in the middle
    }

    [Fact]
    public void MoveClip_Via_SetProperty_Coalesces_Across_A_Drag()
    {
        Clip clip = MakeClip();
        var history = new EditHistory();
        Timecode start = clip.TimelineStart;

        using (history.BeginCoalescing())
        {
            foreach (double s in new[] { 3.0, 4.0, 6.0 })
                history.Execute(SetPropertyCommand<Timecode>.Create(
                    "Move clip", () => clip.TimelineStart, v => clip.TimelineStart = v,
                    Timecode.FromSeconds(s), mergeKey: clip));
        }

        Assert.Equal(Timecode.FromSeconds(6), clip.TimelineStart);

        history.Undo(); // one entry → straight back to the gesture's start
        Assert.Equal(start, clip.TimelineStart);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void TrimClip_Applies_And_Reverts_Both_Ends()
    {
        Clip clip = MakeClip(); // in 0s, out 5s
        var history = new EditHistory();

        history.Execute(new TrimClipCommand(clip, Timecode.FromSeconds(1), Timecode.FromSeconds(4)));
        Assert.Equal(Timecode.FromSeconds(1), clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(4), clip.SourceOut);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(0), clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(5), clip.SourceOut);
    }

    [Fact]
    public void TrimClip_Rejects_Inverted_Span()
    {
        Clip clip = MakeClip();
        Assert.Throws<ArgumentException>(
            () => new TrimClipCommand(clip, Timecode.FromSeconds(4), Timecode.FromSeconds(1)));
    }

    [Fact]
    public void SetClipPlacement_Applies_And_Reverts_All_Three_Fields()
    {
        Clip clip = MakeClip(); // in 0, out 5, start 2
        var history = new EditHistory();

        history.Execute(new SetClipPlacementCommand(
            clip, Timecode.FromSeconds(1), Timecode.FromSeconds(4), Timecode.FromSeconds(7)));
        Assert.Equal(Timecode.FromSeconds(1), clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(4), clip.SourceOut);
        Assert.Equal(Timecode.FromSeconds(7), clip.TimelineStart);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(0), clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(5), clip.SourceOut);
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
    }

    [Fact]
    public void SetClipPlacement_Coalesces_Across_A_Drag_To_One_Entry()
    {
        Clip clip = MakeClip();
        var history = new EditHistory();
        Timecode origStart = clip.TimelineStart;

        using (history.BeginCoalescing())
        {
            foreach (double s in new[] { 3.0, 5.0, 9.0 })
                history.Execute(new SetClipPlacementCommand(
                    clip, clip.SourceIn, clip.SourceOut, Timecode.FromSeconds(s)));
        }

        Assert.Equal(Timecode.FromSeconds(9), clip.TimelineStart);
        history.Undo(); // single coalesced entry → straight back to the gesture's start
        Assert.Equal(origStart, clip.TimelineStart);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void SetClipPlacement_Rejects_Inverted_Span()
    {
        Clip clip = MakeClip();
        Assert.Throws<ArgumentException>(() => new SetClipPlacementCommand(
            clip, Timecode.FromSeconds(4), Timecode.FromSeconds(1), Timecode.Zero));
    }

    [Fact]
    public void AddEffect_And_RemoveEffect_Round_Trip()
    {
        Clip clip = MakeClip();
        var brightness = new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2);
        var history = new EditHistory();

        history.Execute(new AddEffectCommand(clip, brightness));
        Assert.Same(brightness, Assert.Single(clip.Effects));

        history.Execute(new RemoveEffectCommand(clip, brightness));
        Assert.Empty(clip.Effects);

        history.Undo(); // undo the remove → effect back
        Assert.Same(brightness, Assert.Single(clip.Effects));
        history.Undo(); // undo the add → empty
        Assert.Empty(clip.Effects);
    }

    [Fact]
    public void RemoveEffect_Restores_Stack_Order()
    {
        Clip clip = MakeClip();
        var e0 = new EffectInstance(EffectTypeIds.Brightness);
        var e1 = new EffectInstance(EffectTypeIds.Fade);
        var e2 = new EffectInstance(EffectTypeIds.Brightness);
        clip.Effects.AddRange([e0, e1, e2]);
        var history = new EditHistory();

        history.Execute(new RemoveEffectCommand(clip, e1));
        Assert.Equal([e0, e2], clip.Effects);
        history.Undo();
        Assert.Equal([e0, e1, e2], clip.Effects);
    }

    [Fact]
    public void SetEffectParameter_Adds_Then_Reverts_To_Absent()
    {
        var effect = new EffectInstance(EffectTypeIds.Brightness); // no params yet
        var history = new EditHistory();

        history.Execute(new SetEffectParameterCommand(
            effect, EffectParamNames.Amount, AnimatableValue.Constant(1.5)));
        Assert.Equal(1.5, effect.Parameters[EffectParamNames.Amount].Evaluate(Timecode.Zero));

        history.Undo();
        Assert.False(effect.Parameters.ContainsKey(EffectParamNames.Amount)); // back to absent
    }

    [Fact]
    public void SetEffectParameter_Reverts_To_Previous_Value()
    {
        var effect = new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.0);
        var history = new EditHistory();

        history.Execute(new SetEffectParameterCommand(
            effect, EffectParamNames.Amount, AnimatableValue.Constant(2.0)));
        Assert.Equal(2.0, effect.Parameters[EffectParamNames.Amount].Evaluate(Timecode.Zero));

        history.Undo();
        Assert.Equal(1.0, effect.Parameters[EffectParamNames.Amount].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void SetEffectParameter_Coalesces_Same_Param_In_A_Scope()
    {
        var effect = new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, 1.0);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetEffectParameterCommand(effect, EffectParamNames.Opacity, AnimatableValue.Constant(0.8)));
            history.Execute(new SetEffectParameterCommand(effect, EffectParamNames.Opacity, AnimatableValue.Constant(0.5)));
            history.Execute(new SetEffectParameterCommand(effect, EffectParamNames.Opacity, AnimatableValue.Constant(0.2)));
        }

        Assert.Equal(0.2, effect.Parameters[EffectParamNames.Opacity].Evaluate(Timecode.Zero));
        history.Undo(); // single entry → all the way back to the pre-drag value
        Assert.Equal(1.0, effect.Parameters[EffectParamNames.Opacity].Evaluate(Timecode.Zero));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AddTrack_And_RemoveTrack_Preserve_Z_Order()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var bottom = new VideoTrack { Name = "V1" };
        var top = new VideoTrack { Name = "V2" };
        timeline.Tracks.Add(bottom);
        timeline.Tracks.Add(top);
        var history = new EditHistory();

        history.Execute(new RemoveTrackCommand(timeline, bottom)); // remove the bottom (index 0)
        Assert.Equal([top], timeline.Tracks);
        history.Undo();
        Assert.Equal([bottom, top], timeline.Tracks); // bottom restored beneath top

        history.Execute(new AddTrackCommand(timeline, new AudioTrack { Name = "A1" }));
        Assert.Equal(3, timeline.Tracks.Count);
        history.Undo();
        Assert.Equal(2, timeline.Tracks.Count);
    }
}
