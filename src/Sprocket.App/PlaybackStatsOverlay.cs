using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// A floating, always-on-top diagnostics window (View ▸ Playback Statistics) that reports how well preview
/// playback is keeping up: effective vs. target frame rate, dropped frames, and the process resource cost
/// (CPU, memory, GC). Toggled from the View menu; non-modal so it can sit beside the editor while playing.
/// </summary>
/// <remarks>
/// It owns no playback state — it polls the active <see cref="PlaybackEngine"/> (via the accessor) on a UI
/// timer and derives per-second rates from the delta between two cumulative <see cref="PlaybackStatistics"/>
/// snapshots, using the real measured interval rather than the nominal tick period. Counters are cumulative
/// per engine, so the baseline is re-seeded whenever the active engine instance changes (a Program↔Source tab
/// switch) to avoid a spurious spike. Process CPU/memory/GC come from <see cref="Process"/> + <see cref="GC"/>,
/// so they stay meaningful even while playback is idle.
/// </remarks>
internal sealed class PlaybackStatsOverlay : Window
{
    // Health palette from the shared Palette (Palette.cs); Good matches the status-bar state dot.
    private static readonly IBrush Good = Palette.GoodBrush;
    private static readonly IBrush Warn = Palette.WarnBrush;
    private static readonly IBrush Bad = Palette.BadBrush;
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly Func<PlaybackEngine?> _engine;
    private readonly DispatcherTimer _timer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);

    // Value cells, updated each tick.
    private readonly TextBlock _state, _position, _decode, _targetFps, _previewFps, _dropped, _presented,
        _pumpRate, _cpu, _memory, _gcHeap, _gcCollections, _threads;

    // Baseline for delta-derived rates.
    private PlaybackEngine? _lastEngine;
    private long _prevTimestamp;       // Stopwatch ticks
    private TimeSpan _prevCpu;
    private int _prevGen0;

    // Engine rates (preview fps, drops/s, pump/s) are averaged over a rolling window rather than a single tick:
    // a per-tick count is whole frames over ~0.5s (~15 at 30fps), so ±1 frame reads as ±2fps even when the cadence
    // is perfectly steady. Averaging over ~2.5s shrinks that quantization to ±~0.4fps so the readout sits at ~30.
    private const double RateWindowSeconds = 2.5;
    private readonly Queue<(long Ts, PlaybackStatistics Stats)> _engineHistory = new();

    public PlaybackStatsOverlay(Func<PlaybackEngine?> engineAccessor)
    {
        _engine = engineAccessor;

        Title = "Playback Statistics";
        Icon = AppIcon.Window;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // don't steal focus from the editor (Space keeps toggling play)
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Palette.WindowBgBrush;

        var grid = new Grid
        {
            Margin = new Thickness(16, 14),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions(),
        };

        int row = 0;
        _state = AddRow(grid, ref row, "State",
            "Whether the preview is currently playing or paused.");
        _position = AddRow(grid, ref row, "Position",
            "The playhead position and the total duration of what's loaded.");
        AddSeparator(grid, ref row);
        _decode = AddRow(grid, ref row, "Video decode",
            "The codec decoding the preview, and whether it runs on the GPU (hardware-accelerated) or the CPU "
            + "(software). GPU is faster and is the usual fix for 1080p stutter.");
        _targetFps = AddRow(grid, ref row, "Timeline rate",
            "The sequence's frame rate — the rate the preview is trying to hit.");
        _previewFps = AddRow(grid, ref row, "Preview rate",
            "Frames actually shown per second, averaged over a few seconds. It should match the timeline rate "
            + "during smooth playback; a lower number means the preview can't keep up.");
        _dropped = AddRow(grid, ref row, "Dropped frames",
            "Timeline frames the preview couldn't render in time and had to skip to keep pace with the clock, for "
            + "the current playback (it resets each time you start playing). Frames a clip drops because its frame "
            + "rate is higher than the sequence aren't counted — only frames lost to falling behind. This climbs "
            + "when the preview can't keep up; 0 is ideal.");
        _presented = AddRow(grid, ref row, "Frames shown",
            "Total frames presented to the screen since playback started.");
        _pumpRate = AddRow(grid, ref row, "Pump rate",
            "How many times per second the playback loop runs to schedule and composite a frame "
            + "(target ≈ the timeline rate).");
        AddSeparator(grid, ref row);
        _cpu = AddRow(grid, ref row, "CPU",
            "This process's CPU use — as a share of the whole machine, and as a number of cores in use.");
        _memory = AddRow(grid, ref row, "Working set",
            "Physical memory (RAM) the app is currently using.");
        _gcHeap = AddRow(grid, ref row, "Managed heap",
            "Memory held by .NET managed objects. Decoded frame pixels never live here, so it should stay low "
            + "and steady during playback.");
        _gcCollections = AddRow(grid, ref row, "GC (g0/g1/g2)",
            "Garbage-collection counts for generations 0/1/2. Gen-0 should stay flat while playing — a rising "
            + "count means per-frame managed allocations, which can cause hitches.");
        _threads = AddRow(grid, ref row, "Threads",
            "The number of OS threads the process is running.");

        var footnote = new TextBlock
        {
            Text = "Amber video decode = CPU/software path (the usual 1080p stutter cause); green = GPU. Dropped "
                 + "frames climb when the preview can't keep pace, and gen-0 GC should stay flat — frame pixels "
                 + "never touch the managed heap.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(footnote, row);
        Grid.SetColumnSpan(footnote, 2);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Children.Add(footnote);

        Content = grid;

        SeedBaseline(_engine());

        // ~2 Hz: responsive enough to watch a stutter develop, slow enough to read and cheap to sample.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
    }

    /// <summary>Positions the overlay near the top-right of its owner so it doesn't cover the timeline.</summary>
    public void PlaceNear(Window owner)
    {
        double scale = owner.RenderScaling <= 0 ? 1 : owner.RenderScaling;
        int ownerWidthPx = (int)(owner.Bounds.Width * scale);
        int x = owner.Position.X + Math.Max(0, ownerWidthPx - (int)(Width * scale) - (int)(40 * scale));
        int y = owner.Position.Y + (int)(80 * scale);
        Position = new PixelPoint(x, y);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _process.Dispose();
        base.OnClosed(e);
    }

    private void SeedBaseline(PlaybackEngine? engine)
    {
        _lastEngine = engine;
        _engineHistory.Clear();
        _prevTimestamp = Stopwatch.GetTimestamp();
        _process.Refresh();
        _prevCpu = _process.TotalProcessorTime;
        _prevGen0 = GC.CollectionCount(0);
    }

    private void Refresh()
    {
        PlaybackEngine? engine = _engine();

        // A tab switch swaps the engine instance; its cumulative counters are unrelated to the previous one's,
        // so re-seed the baseline and skip rate math for this tick rather than show a spike.
        if (!ReferenceEquals(engine, _lastEngine))
        {
            SeedBaseline(engine);
            RenderEngineRows(engine, fps: -1, dropRate: -1, pumpRate: -1);
            RenderProcessRows(cpuRate: -1, gen0Rate: -1);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _prevTimestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            return;

        double fps = -1, dropRate = -1, pumpRate = -1;
        if (engine is not null)
        {
            PlaybackStatistics s = engine.GetStatistics();
            _engineHistory.Enqueue((now, s));
            // Keep ~RateWindowSeconds of history (but always at least two samples to derive a rate).
            while (_engineHistory.Count > 2 && (now - _engineHistory.Peek().Ts) / (double)Stopwatch.Frequency > RateWindowSeconds)
                _engineHistory.Dequeue();

            (long oldTs, PlaybackStatistics old) = _engineHistory.Peek();
            double window = (now - oldTs) / (double)Stopwatch.Frequency;
            if (window > 0)
            {
                fps = (s.FramesPresented - old.FramesPresented) / window;
                dropRate = (s.FramesDropped - old.FramesDropped) / window;
                pumpRate = (s.PumpIterations - old.PumpIterations) / window;
            }
        }

        _process.Refresh();
        TimeSpan cpu = _process.TotalProcessorTime;
        double cpuRate = (cpu - _prevCpu).TotalSeconds / seconds; // CPU-seconds per wall-second = cores in use
        _prevCpu = cpu;

        int gen0 = GC.CollectionCount(0);
        double gen0Rate = (gen0 - _prevGen0) / seconds;
        _prevGen0 = gen0;
        _prevTimestamp = now;

        RenderEngineRows(engine, fps, dropRate, pumpRate);
        RenderProcessRows(cpuRate, gen0Rate);
    }

    private void RenderEngineRows(PlaybackEngine? engine, double fps, double dropRate, double pumpRate)
    {
        if (engine is null)
        {
            _state.Text = "no media";
            _state.Foreground = Palette.MutedTextBrush;
            _position.Text = _decode.Text = _targetFps.Text = _previewFps.Text = _dropped.Text
                = _presented.Text = _pumpRate.Text = "—";
            _previewFps.Foreground = _dropped.Foreground = _decode.Foreground = Palette.TextBrush;
            return;
        }

        PlaybackState state = engine.State;
        bool playing = state == PlaybackState.Playing;
        _state.Text = state.ToString();
        _state.Foreground = playing ? Good : Palette.TextBrush;

        _position.Text = $"{FormatTime(engine.Position)} / {FormatTime(engine.Duration)}";

        // Which decoder is feeding the preview, and whether it is GPU-accelerated — the headline answer to
        // "why is 1080p stuttering". Null = nothing decoding at the playhead (gap / synthetic clip).
        if (engine.GetActiveVideoDecodeInfo() is { } decode)
        {
            _decode.Text = decode.IsHardwareAccelerated
                ? $"{decode.CodecName} · {decode.HardwareDeviceName!.ToUpperInvariant()} (GPU)"
                : $"{decode.CodecName} · software (CPU)";
            _decode.Foreground = decode.IsHardwareAccelerated ? Good : Warn;
        }
        else
        {
            _decode.Text = "—";
            _decode.Foreground = Palette.TextBrush;
        }

        double target = Fps(engine.FrameRate);
        _targetFps.Text = $"{target:0.##} fps";

        PlaybackStatistics stats = engine.GetStatistics();
        _presented.Text = stats.FramesPresented.ToString("N0");

        if (fps < 0)
        {
            _previewFps.Text = "measuring…";
            _previewFps.Foreground = Palette.MutedTextBrush;
        }
        else
        {
            _previewFps.Text = $"{fps:0.0} fps";
            // Only judge the rate against target while playing — a paused/stopped engine presents ~0 fps by design.
            _previewFps.Foreground = !playing ? Palette.TextBrush
                : target <= 0 ? Palette.TextBrush
                : fps >= target * 0.95 ? Good
                : fps >= target * 0.80 ? Warn
                : Bad;
        }

        // Show drops for the CURRENT play span (reset each time playback starts), so a warm-up hiccup banked by an
        // earlier play doesn't haunt the readout. The rate still comes from the cumulative, monotonic counter (so
        // the rolling-window delta is clean), but is only surfaced once this span has actually dropped a frame —
        // otherwise a fresh play would read "0 (+x/s)" in red while the window still straddles the previous span.
        long spanDrops = stats.FramesDroppedThisSpan;
        bool activelyDropping = spanDrops > 0 && dropRate > 0.05;
        string dropTotal = spanDrops.ToString("N0");
        _dropped.Text = activelyDropping ? $"{dropTotal}  (+{dropRate:0.0}/s)" : dropTotal;
        _dropped.Foreground = spanDrops == 0 ? Good
            : activelyDropping ? Bad // actively dropping right now
            : Warn;                  // dropped earlier this play span but currently steady

        _pumpRate.Text = pumpRate < 0 ? "—" : $"{pumpRate:0} /s";
    }

    private void RenderProcessRows(double cpuRate, double gen0Rate)
    {
        if (cpuRate < 0)
        {
            _cpu.Text = "measuring…";
            _cpu.Foreground = Palette.MutedTextBrush;
        }
        else
        {
            double machinePct = cpuRate / _cores * 100.0;
            _cpu.Text = $"{machinePct:0}%  ·  {cpuRate:0.0} cores";
            _cpu.Foreground = machinePct < 60 ? Good : machinePct < 85 ? Warn : Bad;
        }

        _memory.Text = $"{_process.WorkingSet64 / (1024.0 * 1024.0):0} MB";
        _gcHeap.Text = $"{GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):0.0} MB";

        string collections = $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}";
        _gcCollections.Text = gen0Rate > 0.05 ? $"{collections}  (+{gen0Rate:0.0} g0/s)" : collections;
        _gcCollections.Foreground = gen0Rate > 0.05 ? Warn : Palette.TextBrush;

        _threads.Text = _process.Threads.Count.ToString();
    }

    // ── Layout helpers ────────────────────────────────────────────────────────────────────────────────

    private static TextBlock AddRow(Grid grid, ref int row, string label, string? tooltip = null)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = Palette.MutedTextBrush,
            FontSize = 12,
            Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var value = new TextBlock
        {
            Text = "—",
            Foreground = Palette.TextBrush,
            FontFamily = Mono,
            FontSize = 12,
            Margin = new Thickness(0, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        // Explain the metric on hover (set on both cells so anywhere on the row shows it). A wider, slightly
        // delayed tip reads better for the multi-sentence explanations than the snappy default.
        if (tooltip is not null)
        {
            foreach (Control cell in new Control[] { labelBlock, value })
            {
                ToolTip.SetTip(cell, tooltip);
                ToolTip.SetShowDelay(cell, 350);
            }
        }

        row++;
        return value;
    }

    private static void AddSeparator(Grid grid, ref int row)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var line = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#23232B")),
            Margin = new Thickness(0, 7),
        };
        Grid.SetRow(line, row);
        Grid.SetColumnSpan(line, 2);
        grid.Children.Add(line);
        row++;
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;

    private static string FormatTime(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
