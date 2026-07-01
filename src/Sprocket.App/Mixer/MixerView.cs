using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sprocket.Audio.Loudness;
using Sprocket.Core.Audio;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.App.Mixer;

/// <summary>
/// The Project panel's <b>Audio</b> tab brought to editorial completeness (PLAN.md step 30, UI.md §3.3): a mixer
/// with a live master loudness read-out (EBU R128 integrated / short-term / momentary + true peak + L/R channel
/// meters), a channel strip per audio track (gain fader, pan/balance, mute, solo), and loudness-normalization to a
/// chosen target at track and master scope. Every edit routes through the <see cref="EditHistory"/> so it is
/// undoable; the meters poll the audio engine's <see cref="AudioEngine.CurrentLoudness"/> only while this tab is on
/// screen.
/// </summary>
public sealed class MixerView : UserControl
{
    private const double GainMinDb = -60, GainMaxDb = 12;

    private Project? _project;
    private EditHistory? _history;
    private Func<LoudnessSnapshot>? _readLoudness;
    private Func<AudioTrack, LoudnessMeasurement>? _measureTrack;
    private Func<LoudnessMeasurement>? _measureMaster;

    private double _targetLufs = LoudnessNormalization.StreamingMinus14Lufs;

    /// <summary>The loudness target currently selected in the mixer, so other normalize actions (e.g. Clip ▸
    /// Normalize Audio) use the same target the user picked here (PLAN.md step 30).</summary>
    public double TargetLufs => _targetLufs;

    private readonly DispatcherTimer _timer;
    private bool _suppress;                 // guards programmatic widget updates from re-issuing commands
    private IDisposable? _dragScope;        // open coalescing scope for the active fader drag

    // Master read-out widgets.
    private readonly TextBlock _integratedText = Metric();
    private readonly TextBlock _shortTermText = Metric();
    private readonly TextBlock _momentaryText = Metric();
    private readonly TextBlock _truePeakText = Metric();
    private readonly MeterBar _meterL = new();
    private readonly MeterBar _meterR = new();
    private Slider _masterSlider = null!;
    private readonly TextBlock _masterGainLabel = ValueLabel();

    private readonly StackPanel _strips = new() { Spacing = 6 };
    private readonly List<AudioTrack> _builtOrder = new();
    private readonly Dictionary<AudioTrack, StripWidgets> _stripWidgets = new();

    public MixerView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) }; // ~15 Hz while visible
        _timer.Tick += (_, _) => UpdateMeters();
        Content = BuildLayout();
        AttachedToVisualTree += (_, _) => { if (_readLoudness is not null) _timer.Start(); UpdateMeters(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Binds the mixer to a session. <paramref name="readLoudness"/> supplies the live master meter (null when the
    /// session has no audio device); the two <c>measure</c> delegates measure a scope's raw loudness for
    /// normalization (null hides the Normalize buttons). Called on attach and re-called on File ▸ New/Open.
    /// </summary>
    public void Attach(
        Project project, EditHistory history,
        Func<LoudnessSnapshot>? readLoudness,
        Func<AudioTrack, LoudnessMeasurement>? measureTrack,
        Func<LoudnessMeasurement>? measureMaster)
    {
        if (_history is not null) _history.Changed -= OnHistoryChanged;
        _project = project;
        _history = history;
        _readLoudness = readLoudness;
        _measureTrack = measureTrack;
        _measureMaster = measureMaster;
        _history.Changed += OnHistoryChanged;

        RebuildStrips();
        RefreshMaster();
        if (_readLoudness is not null && IsEffectivelyVisible) _timer.Start();
    }

    private void OnHistoryChanged()
    {
        // A gain/pan drag issues a stream of commands; only rebuild when the track set actually changed, otherwise
        // just refresh values so an in-progress fader isn't torn out from under the pointer.
        if (_project is null) return;
        List<AudioTrack> now = _project.Timeline.AudioTracks.ToList();
        if (!now.SequenceEqual(_builtOrder))
            RebuildStrips();
        else
            RefreshValues();
        RefreshMaster();
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var header = new TextBlock { Text = "Mixer", FontWeight = FontWeight.SemiBold, Foreground = Palette.TextBrush };

        var stripScroll = new ScrollViewer
        {
            Content = _strips,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var root = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        Control master = BuildMasterPanel();
        DockPanel.SetDock(master, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(master);
        root.Children.Add(stripScroll);
        return root;
    }

    private Control BuildMasterPanel()
    {
        _integratedText.FontSize = 20;
        _integratedText.FontWeight = FontWeight.SemiBold;

        var numbers = new StackPanel { Spacing = 2 };
        numbers.Children.Add(Row("Integrated", _integratedText));
        numbers.Children.Add(Row("Short-term", _shortTermText));
        numbers.Children.Add(Row("Momentary", _momentaryText));
        numbers.Children.Add(Row("True peak", _truePeakText));

        var meters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        meters.Children.Add(LabeledMeter("L", _meterL));
        meters.Children.Add(LabeledMeter("R", _meterR));

        _masterSlider = Fader();
        _masterSlider.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        _masterSlider.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        _masterSlider.ValueChanged += (_, e) =>
        {
            if (_suppress || _project is null || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Master gain", () => _project.Settings.MasterGainDb, v => _project.Settings.MasterGainDb = v,
                e.NewValue, mergeKey: "master.gain"));
            _masterGainLabel.Text = MixerFormat.GainDbLabel(e.NewValue);
        };

        var fader = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
        fader.Children.Add(new TextBlock { Text = "Master", Foreground = Palette.MutedTextBrush, FontSize = 11 });
        fader.Children.Add(_masterSlider);
        fader.Children.Add(_masterGainLabel);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        right.Children.Add(meters);
        right.Children.Add(fader);

        var normalize = new Button { Content = "Normalize", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Bottom };
        normalize.Click += (_, _) => NormalizeMaster();
        right.Children.Add(normalize);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(numbers, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(numbers);
        grid.Children.Add(right);

        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        targetRow.Children.Add(new TextBlock { Text = "Normalize to", Foreground = Palette.MutedTextBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        targetRow.Children.Add(BuildTargetPicker());

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(grid);
        panel.Children.Add(targetRow);

        return new Border
        {
            Background = Palette.PanelBgBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 8),
            Child = panel,
        };
    }

    private ComboBox BuildTargetPicker()
    {
        var picker = new ComboBox { MinWidth = 150 };
        (string label, double lufs)[] targets =
        [
            ("-14 LUFS (streaming)", LoudnessNormalization.StreamingMinus14Lufs),
            ("-16 LUFS", LoudnessNormalization.StreamingMinus16Lufs),
            ("-23 LUFS (broadcast)", LoudnessNormalization.BroadcastMinus23Lufs),
        ];
        foreach ((string label, double lufs) in targets)
            picker.Items.Add(new ComboBoxItem { Content = label, Tag = lufs });
        picker.SelectedIndex = 0;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: double lufs })
                _targetLufs = lufs;
        };
        return picker;
    }

    // ── channel strips ─────────────────────────────────────────────────────────────────────────────────

    private void RebuildStrips()
    {
        _strips.Children.Clear();
        _stripWidgets.Clear();
        _builtOrder.Clear();
        if (_project is null) return;

        foreach (AudioTrack track in _project.Timeline.AudioTracks)
        {
            _builtOrder.Add(track);
            _strips.Children.Add(BuildStrip(track));
        }
        if (_builtOrder.Count == 0)
            _strips.Children.Add(new TextBlock
            {
                Text = "No audio tracks. Add one with + Track.",
                Foreground = Palette.FaintTextBrush,
                Margin = new Thickness(2, 8),
            });
    }

    private Control BuildStrip(AudioTrack track)
    {
        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(track.Name) ? "Audio" : track.Name,
            Foreground = Palette.TextBrush, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Slider pan = Balance();
        pan.Value = track.Pan;
        WirePanFader(pan, track);
        var panLabel = ValueLabel();
        panLabel.Text = MixerFormat.PanLabel(track.Pan);
        panLabel.Width = 34;

        Slider gain = Fader(horizontal: true);
        gain.Value = track.GainDb;
        WireGainFader(gain, track);
        var gainLabel = ValueLabel();
        gainLabel.Text = MixerFormat.GainDbLabel(track.GainDb);
        gainLabel.Width = 62;

        var mute = ToggleBox("M", track.Muted);
        mute.Click += (_, _) =>
        {
            if (_suppress) return;
            Execute(SetPropertyCommand<bool>.Create("Toggle mute", () => track.Muted, v => track.Muted = v, mute.IsChecked == true));
        };
        var solo = ToggleBox("S", track.Solo);
        solo.Click += (_, _) =>
        {
            if (_suppress) return;
            Execute(SetPropertyCommand<bool>.Create("Toggle solo", () => track.Solo, v => track.Solo = v, solo.IsChecked == true));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(name);
        row.Children.Add(new TextBlock { Text = "Pan", Foreground = Palette.MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(pan);
        row.Children.Add(panLabel);
        row.Children.Add(new TextBlock { Text = "Gain", Foreground = Palette.MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(gain);
        row.Children.Add(gainLabel);
        row.Children.Add(mute);
        row.Children.Add(solo);

        Button? normalize = null;
        if (_measureTrack is not null)
        {
            normalize = new Button { Content = "Norm", Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
            normalize.Click += (_, _) => NormalizeTrack(track);
            row.Children.Add(normalize);
        }

        _stripWidgets[track] = new StripWidgets(gain, gainLabel, pan, panLabel, mute, solo);

        return new Border
        {
            Background = Palette.RaisedBgBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Child = row,
        };
    }

    private void WireGainFader(Slider gain, AudioTrack track)
    {
        gain.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        gain.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        gain.ValueChanged += (_, e) =>
        {
            if (_suppress || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track gain", () => track.GainDb, v => track.GainDb = v, e.NewValue, mergeKey: (track, "GainDb")));
            if (_stripWidgets.TryGetValue(track, out StripWidgets? w)) w.GainLabel.Text = MixerFormat.GainDbLabel(e.NewValue);
        };
    }

    private void WirePanFader(Slider pan, AudioTrack track)
    {
        pan.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        pan.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        pan.ValueChanged += (_, e) =>
        {
            if (_suppress || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track pan", () => track.Pan, v => track.Pan = v, e.NewValue, mergeKey: (track, "Pan")));
            if (_stripWidgets.TryGetValue(track, out StripWidgets? w)) w.PanLabel.Text = MixerFormat.PanLabel(e.NewValue);
        };
    }

    private void RefreshValues()
    {
        _suppress = true;
        try
        {
            foreach ((AudioTrack track, StripWidgets w) in _stripWidgets)
            {
                w.Gain.Value = track.GainDb;
                w.GainLabel.Text = MixerFormat.GainDbLabel(track.GainDb);
                w.Pan.Value = track.Pan;
                w.PanLabel.Text = MixerFormat.PanLabel(track.Pan);
                w.Mute.IsChecked = track.Muted;
                w.Solo.IsChecked = track.Solo;
            }
        }
        finally { _suppress = false; }
    }

    private void RefreshMaster()
    {
        if (_project is null) return;
        _suppress = true;
        try
        {
            _masterSlider.Value = _project.Settings.MasterGainDb;
            _masterGainLabel.Text = MixerFormat.GainDbLabel(_project.Settings.MasterGainDb);
        }
        finally { _suppress = false; }
    }

    // ── normalization ────────────────────────────────────────────────────────────────────────────────

    private void NormalizeTrack(AudioTrack track)
    {
        if (_measureTrack is null || _history is null) return;
        LoudnessMeasurement m = _measureTrack(track);
        double gain = LoudnessNormalization.ComputeGainDb(m.IntegratedLufs, m.TruePeakDbtp, _targetLufs);
        if (double.IsNegativeInfinity(m.IntegratedLufs)) return; // silent track: nothing to normalize
        _history.Execute(SetPropertyCommand<double>.Create(
            $"Normalize {track.Name}", () => track.GainDb, v => track.GainDb = v, gain));
    }

    private void NormalizeMaster()
    {
        if (_measureMaster is null || _project is null || _history is null) return;
        LoudnessMeasurement m = _measureMaster();
        if (double.IsNegativeInfinity(m.IntegratedLufs)) return;
        double gain = LoudnessNormalization.ComputeGainDb(m.IntegratedLufs, m.TruePeakDbtp, _targetLufs);
        _history.Execute(SetPropertyCommand<double>.Create(
            "Normalize master", () => _project.Settings.MasterGainDb, v => _project.Settings.MasterGainDb = v, gain));
    }

    // ── meters ────────────────────────────────────────────────────────────────────────────────────────

    private void UpdateMeters()
    {
        if (_readLoudness is null) return;
        LoudnessSnapshot s = _readLoudness();
        _integratedText.Text = MixerFormat.LufsLabel(s.IntegratedLufs);
        _shortTermText.Text = MixerFormat.LufsLabel(s.ShortTermLufs);
        _momentaryText.Text = MixerFormat.LufsLabel(s.MomentaryLufs);
        _truePeakText.Text = MixerFormat.DbtpLabel(s.TruePeakDbtp);
        _meterL.SetLevel(MixerFormat.MeterFillFraction(s.PeakDbLeft));
        _meterR.SetLevel(MixerFormat.MeterFillFraction(s.PeakDbRight));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    private void BeginDrag() { _dragScope ??= _history?.BeginCoalescing(); }
    private void EndDrag() { _dragScope?.Dispose(); _dragScope = null; }
    private void Execute(IEditCommand command) => _history?.Execute(command);

    private static Slider Fader(bool horizontal = true) => new()
    {
        Minimum = GainMinDb, Maximum = GainMaxDb, Value = 0,
        Width = horizontal ? 150 : double.NaN,
        Height = horizontal ? double.NaN : 120,
        Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
        VerticalAlignment = VerticalAlignment.Center,
        SmallChange = 0.5, LargeChange = 3,
    };

    private static Slider Balance() => new()
    {
        Minimum = -1, Maximum = 1, Value = 0, Width = 80,
        VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05, LargeChange = 0.2,
    };

    private static TextBlock Metric() => new() { Foreground = Palette.TextBrush, FontFamily = new FontFamily("Consolas, monospace") };
    private static TextBlock ValueLabel() => new() { Foreground = Palette.MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    private static Control Row(string label, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, Width = 84, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(value);
        return row;
    }

    private static Control LabeledMeter(string label, MeterBar bar)
    {
        var col = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(bar);
        col.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        return col;
    }

    private static ToggleButton ToggleBox(string glyph, bool on) => new()
    {
        Content = glyph, IsChecked = on, Width = 26, Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private sealed record StripWidgets(Slider Gain, TextBlock GainLabel, Slider Pan, TextBlock PanLabel, ToggleButton Mute, ToggleButton Solo);

    /// <summary>A vertical peak meter: an instantaneous fill (green→amber→red) plus a slowly-decaying peak-hold line.</summary>
    private sealed class MeterBar : Control
    {
        private double _level;
        private double _peakHold;

        public MeterBar() { Width = 14; Height = 120; }

        public void SetLevel(double level)
        {
            _level = Math.Clamp(level, 0, 1);
            _peakHold = Math.Max(_level, _peakHold - 0.02); // ~3 s fall from full
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(Palette.WindowBgBrush, new Rect(0, 0, w, h), 2);
            double fill = _level * h;
            if (fill > 0)
                ctx.FillRectangle(BrushFor(_level), new Rect(0, h - fill, w, fill), 2);
            if (_peakHold > 0)
            {
                double y = h - _peakHold * h;
                ctx.DrawLine(new Pen(Palette.TextBrush, 1), new Point(0, y), new Point(w, y));
            }
        }

        private static IBrush BrushFor(double level) =>
            level > 0.95 ? Palette.BadBrush : level > 0.8 ? Palette.WarnBrush : Palette.GoodBrush;
    }
}
