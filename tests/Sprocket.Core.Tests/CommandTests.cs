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

    private static MediaRef MakeMedia() =>
        new(MediaRefId.New(), "/tmp/a.mp4",
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, new Rational(30, 1), 1920, 1080, true, 48000, 2));

    [Fact]
    public void AddMedia_And_Undo()
    {
        var pool = new MediaPool();
        MediaRef media = MakeMedia();
        var history = new EditHistory();

        history.Execute(new AddMediaCommand(pool, media));
        Assert.Same(media, pool.Get(media.Id));

        history.Undo();
        Assert.Null(pool.Get(media.Id));

        history.Redo();
        Assert.Same(media, pool.Get(media.Id));
    }

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
    public void MoveClipToTrack_Moves_Sets_Start_And_Reverts_To_Original_Track_And_Index()
    {
        var v1 = new VideoTrack();
        var v2 = new VideoTrack();
        Clip a = MakeClip(), b = MakeClip(), c = MakeClip();
        v1.Clips.AddRange([a, b, c]);               // b sits in the middle of v1
        Guid link = Guid.NewGuid();
        b.LinkGroupId = link;
        Timecode origStart = b.TimelineStart, origIn = b.SourceIn, origOut = b.SourceOut;
        var history = new EditHistory();

        history.Execute(new MoveClipToTrackCommand(v1, v2, b, Timecode.FromSeconds(7)));
        Assert.Equal([a, c], v1.Clips);             // removed from the source track
        Assert.Same(b, Assert.Single(v2.Clips));    // landed on the target track
        Assert.Equal(Timecode.FromSeconds(7), b.TimelineStart);
        Assert.Equal(origIn, b.SourceIn);           // source span + link untouched
        Assert.Equal(origOut, b.SourceOut);
        Assert.Equal(link, b.LinkGroupId);

        history.Undo();
        Assert.Equal([a, b, c], v1.Clips);          // back in the middle (index preserved)
        Assert.Empty(v2.Clips);
        Assert.Equal(origStart, b.TimelineStart);

        history.Redo();
        Assert.Same(b, Assert.Single(v2.Clips));
        Assert.Equal(Timecode.FromSeconds(7), b.TimelineStart);
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
    public void SetEffectEnabled_Toggles_And_Undoes()
    {
        var effect = new EffectInstance(EffectTypeIds.Brightness);
        var history = new EditHistory();
        Assert.True(effect.Enabled); // effects default to enabled

        history.Execute(new SetEffectEnabledCommand(effect, false));
        Assert.False(effect.Enabled);

        history.Undo();
        Assert.True(effect.Enabled);
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

/// <summary>
/// Tests for the step-13 editing primitives: <see cref="SplitClipCommand"/> (blade), <see cref="CompositeCommand"/>
/// (linked edits as one undo entry), and the <see cref="Timeline.ClipsLinkedTo"/> link relation. All headless.
/// </summary>
public class EditingToolsTests
{
    private static Clip MakeClip(Timecode start, Timecode dur, MediaRefId? media = null) =>
        new(media ?? MediaRefId.New(), Timecode.Zero, dur, start);

    [Fact]
    public void Split_Divides_Source_And_Timeline_At_The_Cut()
    {
        var track = new VideoTrack();
        // Clip: source [0,10), placed at t=2 → spans [2,12). Cut at t=5 → left [2,5), right [5,12).
        Clip clip = MakeClip(Timecode.FromSeconds(2), Timecode.FromSeconds(10));
        track.Clips.Add(clip);
        var history = new EditHistory();

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(5));
        history.Execute(split);

        Assert.Equal(2, track.Clips.Count);
        // Left half keeps the start, ends at the cut.
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(3), clip.SourceOut); // source 0..3 (3s of the 10s source)
        Assert.Equal(Timecode.FromSeconds(5), clip.TimelineEnd);
        // Right half begins at the cut and carries the remaining source.
        Clip right = split.RightClip;
        Assert.Same(right, track.Clips[1]);
        Assert.Equal(Timecode.FromSeconds(5), right.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(3), right.SourceIn);
        Assert.Equal(Timecode.FromSeconds(10), right.SourceOut);
        Assert.Equal(Timecode.FromSeconds(12), right.TimelineEnd);
    }

    [Fact]
    public void Split_Undo_Restores_The_Single_Clip()
    {
        var track = new VideoTrack();
        Clip clip = MakeClip(Timecode.Zero, Timecode.FromSeconds(8));
        track.Clips.Add(clip);
        var history = new EditHistory();

        history.Execute(new SplitClipCommand(track, clip, Timecode.FromSeconds(3)));
        history.Undo();

        Assert.Same(clip, Assert.Single(track.Clips));
        Assert.Equal(Timecode.FromSeconds(8), clip.SourceOut); // out-point restored
        Assert.Equal(Timecode.FromSeconds(8), clip.TimelineEnd);
    }

    [Fact]
    public void Split_Copies_The_Effect_Stack_Onto_The_Right_Half()
    {
        var track = new VideoTrack();
        Clip clip = MakeClip(Timecode.Zero, Timecode.FromSeconds(6));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        track.Clips.Add(clip);

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(3));
        split.Apply();

        EffectInstance copied = Assert.Single(split.RightClip.Effects);
        Assert.Equal(EffectTypeIds.Brightness, copied.EffectTypeId);
        Assert.Equal(1.2, copied.Parameters[EffectParamNames.Amount].Evaluate(Timecode.Zero));
        // It's a copy, not the same instance — editing one half's stack won't mutate the other.
        Assert.NotSame(clip.Effects[0], copied);
    }

    [Theory]
    [InlineData(0)]  // on the start edge
    [InlineData(6)]  // on the end edge
    public void Split_Rejects_A_Cut_On_Or_Outside_The_Clip(int seconds)
    {
        var track = new VideoTrack();
        Clip clip = MakeClip(Timecode.Zero, Timecode.FromSeconds(6));
        track.Clips.Add(clip);
        Assert.Throws<ArgumentException>(() => new SplitClipCommand(track, clip, Timecode.FromSeconds(seconds)));
    }

    [Fact]
    public void Split_Assigns_The_Right_Half_A_Given_Link_Group()
    {
        var track = new VideoTrack();
        var original = Guid.NewGuid();
        Clip clip = MakeClip(Timecode.Zero, Timecode.FromSeconds(6));
        clip.LinkGroupId = original;
        track.Clips.Add(clip);

        var newGroup = Guid.NewGuid();
        new SplitClipCommand(track, clip, Timecode.FromSeconds(3), newGroup).Apply();

        Assert.Equal(original, clip.LinkGroupId);           // left keeps the original group
        Assert.Equal(newGroup, track.Clips[1].LinkGroupId); // right gets the fresh group
    }

    [Fact]
    public void Composite_Applies_In_Order_And_Reverts_In_Reverse()
    {
        var log = new List<string>();
        var history = new EditHistory();
        var composite = new CompositeCommand("group",
        [
            new Probe("a", log),
            new Probe("b", log),
        ]);

        history.Execute(composite);
        Assert.Equal(["+a", "+b"], log);

        history.Undo();
        Assert.Equal(["+a", "+b", "-b", "-a"], log); // reverse order on revert
    }

    [Fact]
    public void Composite_Is_One_Undo_Entry()
    {
        var log = new List<string>();
        var history = new EditHistory();
        history.Execute(new CompositeCommand("group", [new Probe("a", log), new Probe("b", log)]));

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("group", history.UndoLabel);
    }

    [Fact]
    public void Composite_Coalesces_With_A_Same_Shape_Composite()
    {
        var track = new VideoTrack();
        Clip v = MakeClip(Timecode.FromSeconds(2), Timecode.FromSeconds(4));
        Clip a = MakeClip(Timecode.FromSeconds(2), Timecode.FromSeconds(4));
        track.Clips.AddRange([v, a]);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            // Two consecutive linked moves of the same two clips collapse into one undo entry.
            history.Execute(MoveBoth(v, a, Timecode.FromSeconds(3)));
            history.Execute(MoveBoth(v, a, Timecode.FromSeconds(5)));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(5), v.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(5), a.TimelineStart);

        history.Undo(); // one undo returns both to their original start
        Assert.Equal(Timecode.FromSeconds(2), v.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(2), a.TimelineStart);
    }

    [Fact]
    public void ClipsLinkedTo_Returns_Companions_Sharing_The_Group()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var video = new VideoTrack();
        var audio = new AudioTrack();
        var group = Guid.NewGuid();
        Clip v = MakeClip(Timecode.Zero, Timecode.FromSeconds(5));
        Clip a = MakeClip(Timecode.Zero, Timecode.FromSeconds(5));
        Clip unrelated = MakeClip(Timecode.Zero, Timecode.FromSeconds(5));
        v.LinkGroupId = group;
        a.LinkGroupId = group;
        video.Clips.AddRange([v, unrelated]);
        audio.Clips.Add(a);
        timeline.Tracks.AddRange([video, audio]);

        (Track Track, Clip Clip) companion = Assert.Single(timeline.ClipsLinkedTo(v));
        Assert.Same(a, companion.Clip);
        Assert.Same(audio, companion.Track);
        Assert.Empty(timeline.ClipsLinkedTo(unrelated)); // unlinked → no companions
    }

    private static CompositeCommand MoveBoth(Clip v, Clip a, Timecode start) =>
        new("Move linked clips",
        [
            new SetClipPlacementCommand(v, v.SourceIn, v.SourceOut, start),
            new SetClipPlacementCommand(a, a.SourceIn, a.SourceOut, start),
        ]);

    // Records apply/revert calls in order so composite ordering is observable.
    private sealed class Probe(string id, List<string> log) : EditCommand(id)
    {
        public override void Apply() => log.Add($"+{Label}");
        public override void Revert() => log.Add($"-{Label}");
    }
}

/// <summary>
/// Ripple / roll / slide trim commands (PLAN.md step 22). Each is a pure timeline operation that keeps the
/// sequence continuous; these tests assert it applies + reverses exactly against the real model and coalesces a
/// drag into one undo entry. The control's tool/pointer wiring rests on these + manual verification.
/// </summary>
public class RippleRollSlideTests
{
    private static Clip Clip(double startS, double durS, double sourceInS = 0) =>
        new(MediaRefId.New(), Timecode.FromSeconds(sourceInS), Timecode.FromSeconds(sourceInS + durS), Timecode.FromSeconds(startS));

    // ── Ripple trim ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RippleTrim_Out_Extends_Clip_And_Shifts_Downstream_Right()
    {
        // A[0,5) B[5,10) C[10,15) on one track; ripple A's out by +2s.
        Clip a = Clip(0, 5), b = Clip(5, 5), c = Clip(10, 5);
        var downstream = new List<(Clip, Timecode)> { (b, b.TimelineStart), (c, c.TimelineStart) };
        var history = new EditHistory();

        history.Execute(new RippleTrimCommand(a, a.SourceIn, Timecode.FromSeconds(7), downstream, Timecode.FromSeconds(2).Ticks));

        Assert.Equal(Timecode.FromSeconds(7), a.SourceOut);
        Assert.Equal(Timecode.FromSeconds(7), a.TimelineEnd);     // start fixed, longer by 2s
        Assert.Equal(Timecode.FromSeconds(7), b.TimelineStart);   // butts the new end
        Assert.Equal(Timecode.FromSeconds(12), c.TimelineStart);  // both downstream shifted +2s

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), a.SourceOut);
        Assert.Equal(Timecode.FromSeconds(5), b.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(10), c.TimelineStart);
    }

    [Fact]
    public void RippleTrim_In_Trims_Head_And_Shifts_Downstream_Left()
    {
        // A[0,5) B[5,10); ripple A's in by +2s (drop 2s of head). A's start is fixed; end & B pull left.
        Clip a = Clip(0, 5), b = Clip(5, 5);
        var downstream = new List<(Clip, Timecode)> { (b, b.TimelineStart) };
        var history = new EditHistory();

        history.Execute(new RippleTrimCommand(a, Timecode.FromSeconds(2), a.SourceOut, downstream, Timecode.FromSeconds(-2).Ticks));

        Assert.Equal(Timecode.FromSeconds(2), a.SourceIn);
        Assert.Equal(Timecode.Zero, a.TimelineStart);            // leading edge stays put
        Assert.Equal(Timecode.FromSeconds(3), a.TimelineEnd);    // duration 5→3
        Assert.Equal(Timecode.FromSeconds(3), b.TimelineStart);  // downstream closes the gap

        history.Undo();
        Assert.Equal(Timecode.Zero, a.SourceIn);
        Assert.Equal(Timecode.FromSeconds(5), b.TimelineStart);
    }

    [Fact]
    public void RippleTrim_Coalesces_A_Drag_Into_One_Entry()
    {
        Clip a = Clip(0, 5), b = Clip(5, 5);
        var downstream = new List<(Clip, Timecode)> { (b, b.TimelineStart) }; // captured once, like a real drag
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new RippleTrimCommand(a, a.SourceIn, Timecode.FromSeconds(6), downstream, Timecode.FromSeconds(1).Ticks));
            history.Execute(new RippleTrimCommand(a, a.SourceIn, Timecode.FromSeconds(8), downstream, Timecode.FromSeconds(3).Ticks));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(8), a.SourceOut);       // last value wins
        Assert.Equal(Timecode.FromSeconds(8), b.TimelineStart);

        history.Undo(); // one undo reverses the whole gesture to the captured originals
        Assert.Equal(Timecode.FromSeconds(5), a.SourceOut);
        Assert.Equal(Timecode.FromSeconds(5), b.TimelineStart);
    }

    // ── Roll edit ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Roll_Moves_The_Cut_And_Keeps_The_Combined_Span()
    {
        // L[0,5) R[5,10), cut at 5. Roll +2s: L grows to [0,7), R shrinks to [7,10).
        Clip l = Clip(0, 5), r = Clip(5, 5);
        var history = new EditHistory();

        history.Execute(new RollEditCommand(l, r,
            Timecode.FromSeconds(7), Timecode.FromSeconds(2), Timecode.FromSeconds(7)));

        Assert.Equal(Timecode.FromSeconds(7), l.SourceOut);
        Assert.Equal(Timecode.FromSeconds(7), l.TimelineEnd);
        Assert.Equal(Timecode.FromSeconds(2), r.SourceIn);
        Assert.Equal(Timecode.FromSeconds(7), r.TimelineStart);  // R starts at the new cut
        Assert.Equal(Timecode.FromSeconds(10), r.TimelineEnd);   // combined span unchanged

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), l.SourceOut);
        Assert.Equal(Timecode.Zero, r.SourceIn);
        Assert.Equal(Timecode.FromSeconds(5), r.TimelineStart);
    }

    [Fact]
    public void Roll_Coalesces_A_Drag_Into_One_Entry()
    {
        Clip l = Clip(0, 5), r = Clip(5, 5);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new RollEditCommand(l, r, Timecode.FromSeconds(6), Timecode.FromSeconds(1), Timecode.FromSeconds(6)));
            history.Execute(new RollEditCommand(l, r, Timecode.FromSeconds(8), Timecode.FromSeconds(3), Timecode.FromSeconds(8)));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(8), l.SourceOut);
        Assert.Equal(Timecode.FromSeconds(8), r.TimelineStart);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), l.SourceOut);
        Assert.Equal(Timecode.FromSeconds(5), r.TimelineStart);
    }

    // ── Slide ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Slide_Moves_The_Clip_And_Neighbours_Absorb_It()
    {
        // P[0,5) C[5,9)(source 2..6) N[9,14); slide C +2s. C's source window is untouched.
        Clip p = Clip(0, 5), c = Clip(5, 4, sourceInS: 2), n = Clip(9, 5);
        var history = new EditHistory();

        history.Execute(new SlideClipCommand(
            c, Timecode.FromSeconds(7),
            p, Timecode.FromSeconds(7),
            n, Timecode.FromSeconds(2), Timecode.FromSeconds(11)));

        Assert.Equal(Timecode.FromSeconds(7), c.TimelineStart);   // clip slid +2s
        Assert.Equal(Timecode.FromSeconds(2), c.SourceIn);        // source window unchanged
        Assert.Equal(Timecode.FromSeconds(6), c.SourceOut);
        Assert.Equal(Timecode.FromSeconds(7), p.SourceOut);       // prev extended to follow
        Assert.Equal(Timecode.FromSeconds(7), p.TimelineEnd);
        Assert.Equal(Timecode.FromSeconds(2), n.SourceIn);        // next pulled in
        Assert.Equal(Timecode.FromSeconds(11), n.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(14), n.TimelineEnd);    // downstream end fixed

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), c.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(5), p.SourceOut);
        Assert.Equal(Timecode.Zero, n.SourceIn);
        Assert.Equal(Timecode.FromSeconds(9), n.TimelineStart);
    }

    [Fact]
    public void Slide_With_No_Previous_Neighbour_Only_Adjusts_The_Next()
    {
        // C is the first clip; only the next neighbour absorbs the slide.
        Clip c = Clip(0, 4, sourceInS: 2), n = Clip(4, 5);
        var history = new EditHistory();

        history.Execute(new SlideClipCommand(
            c, Timecode.FromSeconds(1),
            prev: null, default,
            n, Timecode.FromSeconds(1), Timecode.FromSeconds(5)));

        Assert.Equal(Timecode.FromSeconds(1), c.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(1), n.SourceIn);
        Assert.Equal(Timecode.FromSeconds(5), n.TimelineStart);

        history.Undo();
        Assert.Equal(Timecode.Zero, c.TimelineStart);
        Assert.Equal(Timecode.Zero, n.SourceIn);
        Assert.Equal(Timecode.FromSeconds(4), n.TimelineStart);
    }

    [Fact]
    public void Slide_Coalesces_A_Drag_Into_One_Entry()
    {
        Clip p = Clip(0, 5), c = Clip(5, 4, sourceInS: 2), n = Clip(9, 5);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SlideClipCommand(c, Timecode.FromSeconds(6), p, Timecode.FromSeconds(6), n, Timecode.FromSeconds(1), Timecode.FromSeconds(10)));
            history.Execute(new SlideClipCommand(c, Timecode.FromSeconds(7), p, Timecode.FromSeconds(7), n, Timecode.FromSeconds(2), Timecode.FromSeconds(11)));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(7), c.TimelineStart);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), c.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(5), p.SourceOut);
        Assert.Equal(Timecode.FromSeconds(9), n.TimelineStart);
    }
}
