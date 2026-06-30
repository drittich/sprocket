using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Inspector;

/// <summary>
/// The type-driven Inspector (PLAN.md step 16, UI.md §3.5): collapsible sections for the selected clip — a
/// read-only Clip section plus one section per effect in the stack, each built automatically from the
/// effect's <see cref="EffectParameterDescriptor"/>s (slider + numeric box + a keyframe toggle). Editing runs
/// through the step-10 command stack (a slider drag coalesces to one undo entry); the keyframe affordance
/// converts a parameter to/from animated and scrubs keyframes in at the playhead. Built entirely in code like
/// <see cref="MediaBrowser.MediaBrowserPanel"/> / <see cref="Timeline.TimelineControl"/>.
/// </summary>
public sealed class InspectorPanel : UserControl
{
    // Palette (mirrors App.axaml so the code control matches the themed shell).
    private static readonly IBrush PanelBg = Hex("#16161C");
    private static readonly IBrush RaisedBg = Hex("#22222B");
    private static readonly IBrush Edge = Hex("#2A2A33");
    private static readonly IBrush TextBrush = Hex("#D5DBE6");
    private static readonly IBrush MutedText = Hex("#9AA4B2");
    private static readonly IBrush FaintText = Hex("#6A7180");
    private static readonly IBrush Accent = Hex("#6C5CE7");

    private Project? _project;
    private EditHistory? _history;
    private Func<Timecode> _playhead = () => Timecode.Zero;

    private Clip? _clip;
    private readonly StackPanel _body;
    private readonly List<Action> _valueRefreshers = new();

    private bool _suppress;          // guards programmatic slider/text updates from re-triggering edits
    private bool _editing;           // true during a drag/commit so history.Changed refreshes values, not rebuild
    private IDisposable? _dragScope; // open coalescing scope for the active slider drag

    public InspectorPanel()
    {
        _body = new StackPanel { Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        Content = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Rebuild();
    }

    /// <summary>Binds the inspector to the project, shared edit history, and a playhead accessor. Call once.</summary>
    public void Attach(Project project, EditHistory history, Func<Timecode> playhead)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(playhead);
        _project = project;
        _history = history;
        _playhead = playhead;
        _history.Changed += OnHistoryChanged;
        Rebuild();
    }

    /// <summary>Shows the given clip's properties (or the empty state when <see langword="null"/>).</summary>
    public void SetSelectedClip(Clip? clip)
    {
        _clip = clip;
        Rebuild();
    }

    /// <summary>Refreshes the displayed parameter values for the current playhead (animated values move with it).</summary>
    public void OnPlayheadMoved() => RefreshValues();

    private void OnHistoryChanged()
    {
        // During a live edit (slider drag / numeric commit) just refresh values so the control isn't torn down
        // mid-gesture; a structural change (add/remove effect, undo/redo) rebuilds the sections.
        if (_editing)
            RefreshValues();
        else
            Rebuild();
    }

    // ── Build ───────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        _valueRefreshers.Clear();
        _body.Children.Clear();

        if (_clip is null || _project is null || _history is null)
        {
            _body.Children.Add(new TextBlock
            {
                Text = "No clip selected.\nSelect a clip in the timeline to edit its properties.",
                FontSize = 12,
                Foreground = FaintText,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Avalonia.Thickness(16, 24),
            });
            return;
        }

        _body.Children.Add(BuildClipSection(_clip));

        if (_clip.Kind == ClipKind.Multicam)
            _body.Children.Add(BuildMulticamSection(_clip));

        foreach (EffectInstance effect in _clip.Effects)
            _body.Children.Add(BuildEffectSection(_clip, effect));

        _body.Children.Add(BuildAddEffectBar(_clip));
        RefreshValues();
    }

    private Control BuildClipSection(Clip clip)
    {
        string name = Path.GetFileName(_project!.MediaPool.Get(clip.MediaRefId)?.AbsolutePath ?? "clip");
        var info = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };
        info.Children.Add(InfoRow("Source", name));
        info.Children.Add(InfoRow("Start", FormatSeconds(clip.TimelineStart)));
        info.Children.Add(InfoRow("Duration", FormatSeconds(clip.Duration)));
        info.Children.Add(InfoRow("Trim", $"{FormatSeconds(clip.SourceIn)} – {FormatSeconds(clip.SourceOut)}"));
        info.Children.Add(BuildSpeedRow(clip));
        return Section("Clip", info, expanded: true);
    }

    /// <summary>An editable Speed row (retime, PLAN.md step 21): a percentage box committing a
    /// <see cref="SetClipSpeedCommand"/> on Enter/blur. Linked companions are retimed together so A/V stays in
    /// sync. The Duration row above updates on the resulting rebuild.</summary>
    private Control BuildSpeedRow(Clip clip)
    {
        var box = new TextBox
        {
            Width = 72,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 2),
            Background = PanelBg,
            BorderBrush = Edge,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = SpeedFormat.ToPercentString(clip.SpeedRatio),
        };
        void Commit()
        {
            if (_history is null || _project is null)
                return;
            if (!SpeedFormat.TryParsePercent(box.Text, out Rational speed))
            {
                box.Text = SpeedFormat.ToPercentString(clip.SpeedRatio);
                return;
            }
            if (speed == clip.SpeedRatio)
                return;
            var members = new List<Clip> { clip };
            members.AddRange(_project.Timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
            var commands = members.Select(c => (IEditCommand)new SetClipSpeedCommand(c, speed)).ToList();
            _history.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Change speed", commands));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();

        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(box);
        right.Children.Add(new TextBlock { Text = "%", FontSize = 11, Foreground = FaintText, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = "Speed", FontSize = 11, Foreground = FaintText, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>The Multicam section (PLAN.md step 24): the synced source plus one button per camera angle (the
    /// active one highlighted). Clicking sets the segment's angle via <see cref="SetClipAngleCommand"/> (the
    /// number keys do the same with a playhead cut). Shows each angle's sync offset.</summary>
    private Control BuildMulticamSection(Clip clip)
    {
        var content = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(4, 4, 4, 4) };
        MulticamSource? source = clip.SourceMulticamId is { } id ? _project!.GetMulticam(id) : null;
        if (source is null)
        {
            content.Children.Add(new TextBlock { Text = "Multicam source missing.", FontSize = 11, Foreground = FaintText });
            return Section("Multicam", content, expanded: true);
        }

        content.Children.Add(InfoRow("Source", source.Name));
        content.Children.Add(new TextBlock { Text = "Active angle", FontSize = 11, Foreground = FaintText });

        var angles = new StackPanel { Spacing = 3 };
        for (int i = 0; i < source.Angles.Count; i++)
        {
            int index = i; // capture per iteration
            MulticamAngle a = source.Angles[i];
            bool active = clip.ActiveAngle == i;
            string offset = a.SyncOffset.Ticks == 0 ? "" : $"   ({FormatSeconds(a.SyncOffset)})";
            var button = new Button
            {
                Content = $"{i + 1}.  {a.Name}{offset}",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(8, 3),
                Background = active ? Accent : RaisedBg,
                Foreground = TextBrush,
                BorderBrush = Edge,
            };
            button.Click += (_, _) =>
            {
                if (_history is null || clip.ActiveAngle == index)
                    return;
                _history.Execute(new SetClipAngleCommand(clip, index));
            };
            angles.Children.Add(button);
        }
        content.Children.Add(angles);
        return Section("Multicam", content, expanded: true);
    }

    private Control BuildEffectSection(Clip clip, EffectInstance effect)
    {
        EffectDescriptor? descriptor = EffectCatalog.Find(effect.EffectTypeId);
        string title = descriptor?.DisplayName ?? effect.EffectTypeId;

        var rows = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(4, 6, 4, 4) };

        IReadOnlyList<EffectParameterDescriptor> parameters =
            descriptor?.Parameters ?? FallbackDescriptors(effect);
        if (parameters.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No editable parameters.", FontSize = 11, Foreground = FaintText });

        foreach (EffectParameterDescriptor p in parameters)
            rows.Children.Add(BuildParamRow(effect, p));

        // Header with a remove (✕) button.
        var header = new DockPanel();
        var remove = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 1),
            Background = Brushes.Transparent,
            Foreground = FaintText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Remove effect");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _history!.Execute(new RemoveEffectCommand(clip, effect));
        };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return Section(header, rows, expanded: true);
    }

    private Control BuildParamRow(EffectInstance effect, EffectParameterDescriptor p)
    {
        var slider = new Slider
        {
            Minimum = p.Min,
            Maximum = p.Max,
            SmallChange = p.Step,
            LargeChange = p.Step * 10,
            Margin = new Avalonia.Thickness(0, -2, 0, -2),
        };
        var box = new TextBox
        {
            Width = 64,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 2),
            Background = PanelBg,
            BorderBrush = Edge,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var keyButton = new Button
        {
            FontSize = 12,
            Padding = new Avalonia.Thickness(5, 1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(keyButton, "Toggle keyframing at the playhead");

        // Velocity-graph toggle (PLAN.md step 16d): expands the keyframe strip into the editable value graph.
        var graphButton = new Button
        {
            Content = "∿",
            FontSize = 12,
            Padding = new Avalonia.Thickness(5, 1),
            Background = Brushes.Transparent,
            Foreground = FaintText,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        ToolTip.SetTip(graphButton, "Show / hide the velocity graph");

        var label = new TextBlock
        {
            Text = p.DisplayName,
            FontSize = 11,
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Slider drag → coalesced edits (one undo entry); numeric box → a single discrete edit.
        slider.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        slider.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) => EndDrag();
        slider.ValueChanged += (_, e) =>
        {
            if (_suppress)
                return;
            CommitValue(effect, p, e.NewValue, coalescing: _dragScope is not null);
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitFromBox(effect, p, box);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitFromBox(effect, p, box);

        keyButton.Click += (_, _) => ToggleKeyframe(effect, p);

        // Header line: label + keyframe toggle + numeric box; slider below.
        var top = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 2) };
        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rightGroup.Children.Add(graphButton);
        rightGroup.Children.Add(keyButton);
        rightGroup.Children.Add(box);
        DockPanel.SetDock(rightGroup, Dock.Right);
        top.Children.Add(rightGroup);
        top.Children.Add(label);

        // Keyframe lane (PLAN.md step 16b/16d): shown only when the parameter is animated. It edits the same
        // AnimatableValue through the command stack; a keyframe/handle drag coalesces to one undo entry.
        var lane = new KeyframeLane { IsVisible = false };
        lane.DragStarted += BeginDrag;
        lane.DragEnded += EndDrag;
        lane.Edited += (next, coalescing) => ExecuteParam(effect, p.Name, next, coalescing);

        graphButton.Click += (_, _) =>
        {
            lane.GraphMode = !lane.GraphMode;
            graphButton.Foreground = lane.GraphMode ? Accent : FaintText;
        };

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(slider);
        stack.Children.Add(lane);

        // Refresher: re-read the model value at the playhead and update the widgets (suppressed) + the keyframe
        // glyph + the lane.
        _valueRefreshers.Add(() =>
        {
            AnimatableValue value = ParamValue(effect, p);
            double v = value.Evaluate(_playhead());
            _suppress = true;
            slider.Value = Math.Clamp(v, p.Min, p.Max);
            box.Text = InspectorFormat.Value(v, p.Unit);
            _suppress = false;
            keyButton.Content = value.IsAnimated ? "◆" : "◇";
            keyButton.Foreground = value.IsAnimated ? Accent : FaintText;

            graphButton.IsVisible = value.IsAnimated;
            lane.IsVisible = value.IsAnimated;
            if (value.IsAnimated && _clip is { } clip)
                lane.Update(value, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks, _playhead().Ticks, p.Min, p.Max);
        });

        return stack;
    }

    private Control BuildAddEffectBar(Clip clip)
    {
        var add = new Button
        {
            Content = "+ Effect",
            FontSize = 12,
            Padding = new Avalonia.Thickness(10, 4),
            Margin = new Avalonia.Thickness(8, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = RaisedBg,
            Foreground = TextBrush,
        };

        var items = new List<MenuItem>();
        foreach (EffectDescriptor descriptor in EffectCatalog.BuiltIns)
        {
            var item = new MenuItem { Header = descriptor.DisplayName };
            item.Click += (_, _) => _history!.Execute(new AddEffectCommand(clip, descriptor.CreateInstance()));
            items.Add(item);
        }
        add.Flyout = new MenuFlyout { ItemsSource = items };
        return add;
    }

    // ── Editing ───────────────────────────────────────────────────────────────────────────────────────

    private void BeginDrag()
    {
        if (_history is null || _dragScope is not null)
            return;
        _editing = true;
        _dragScope = _history.BeginCoalescing();
    }

    private void EndDrag()
    {
        _dragScope?.Dispose();
        _dragScope = null;
        _editing = false;
    }

    private void CommitFromBox(EffectInstance effect, EffectParameterDescriptor p, TextBox box)
    {
        if (_suppress)
            return;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            RefreshValues(); // revert the text to the model value
            return;
        }
        v = Math.Clamp(v, p.Min, p.Max);
        CommitValue(effect, p, v, coalescing: false);
    }

    private void CommitValue(EffectInstance effect, EffectParameterDescriptor p, double value, bool coalescing)
    {
        AnimatableValue current = ParamValue(effect, p);
        AnimatableValue next = AnimatableEditing.SetValueAt(current, _playhead(), value);
        ExecuteParam(effect, p.Name, next, coalescing);
    }

    /// <summary>
    /// Runs a parameter edit through the command stack. <paramref name="coalescing"/> (true mid-drag) keeps the
    /// panel in editing mode so <see cref="OnHistoryChanged"/> refreshes values rather than rebuilding the
    /// section out from under an active gesture. Shared by the slider/numeric editors and the keyframe lane.
    /// </summary>
    private void ExecuteParam(EffectInstance effect, string name, AnimatableValue next, bool coalescing)
    {
        if (_history is null)
            return;

        bool wasEditing = _editing;
        _editing = true; // a single discrete commit shouldn't trigger a rebuild mid-update either
        try
        {
            _history.Execute(new SetEffectParameterCommand(effect, name, next));
        }
        finally
        {
            _editing = coalescing && wasEditing; // stay in editing mode only while a drag/lane scope is open
        }
        RefreshValues();
    }

    private void ToggleKeyframe(EffectInstance effect, EffectParameterDescriptor p)
    {
        if (_history is null)
            return;
        AnimatableValue current = ParamValue(effect, p);
        Timecode t = _playhead();
        AnimatableValue next = current.IsAnimated
            ? AnimatableEditing.DisableKeyframing(current, t)
            : AnimatableEditing.EnableKeyframing(current, t);
        _history.Execute(new SetEffectParameterCommand(effect, p.Name, next));
        // history.Changed → Rebuild() (not editing) refreshes the section + keyframe glyph.
    }

    private void RefreshValues()
    {
        foreach (Action refresh in _valueRefreshers)
            refresh();
    }

    private static AnimatableValue ParamValue(EffectInstance effect, EffectParameterDescriptor p) =>
        effect.Parameters.TryGetValue(p.Name, out AnimatableValue? v) ? v : AnimatableValue.Constant(p.Default);

    /// <summary>Descriptors for an unregistered (plugin) effect's existing parameters, so the Inspector still
    /// shows editable sliders with a guessed range rather than nothing.</summary>
    private static IReadOnlyList<EffectParameterDescriptor> FallbackDescriptors(EffectInstance effect)
    {
        var list = new List<EffectParameterDescriptor>();
        foreach ((string name, AnimatableValue value) in effect.Parameters)
        {
            double v = value.Evaluate(Timecode.Zero);
            double max = Math.Max(1.0, Math.Abs(v) * 2.0);
            list.Add(new EffectParameterDescriptor(name, name, v, Math.Min(0.0, -max), max, 0.01));
        }
        return list;
    }

    // ── Section / row chrome ──────────────────────────────────────────────────────────────────────────

    private static Control Section(object header, Control content, bool expanded) => new Expander
    {
        Header = header,
        Content = content,
        IsExpanded = expanded,
        Margin = new Avalonia.Thickness(8, 6, 8, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    private static Control InfoRow(string label, string value)
    {
        var row = new DockPanel();
        var v = new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = FaintText });
        return row;
    }

    private static string FormatSeconds(Timecode t) =>
        $"{t.ToSeconds().ToString("0.00", CultureInfo.InvariantCulture)}s";

    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));
}
