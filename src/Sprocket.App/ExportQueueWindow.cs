using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sprocket.Export;

namespace Sprocket.App;

/// <summary>
/// The Export Queue window (PLAN.md step 29): a live list of queued / running / finished export jobs with
/// per-job progress + cancel, plus Add / Start / Stop / Clear controls. The window is a thin view over
/// <see cref="ExportQueue"/> — it owns no export logic. It subscribes to <see cref="ExportQueue.Changed"/>
/// (which may fire on a worker thread) and marshals refreshes onto the UI thread. Rows are updated in place on
/// each progress tick (the job set only rebuilds the visual tree when it actually changes), so a running
/// encode's frequent progress reports don't rebuild the list 30×/s.
/// </summary>
/// <remarks>Built in code against the shared dark <see cref="Palette"/> like the other dialogs; look/behaviour
/// rests on manual verification (the App is a UI-bound WinExe). Add / Start are supplied by the composition root
/// (<see cref="MainWindow"/>) because they need the storage pickers, the project, and the playback quiesce.</remarks>
internal sealed class ExportQueueWindow : Window
{
    private readonly ExportQueue _queue;
    private readonly Func<Task> _addJob;
    private readonly Func<Task> _startQueue;

    private readonly StackPanel _list;
    private readonly TextBlock _emptyHint;
    private readonly Button _addButton, _startButton, _stopButton, _clearButton;

    // The job ordering last rendered, so a progress tick updates rows in place and only structural changes
    // (add / remove / reorder) rebuild the tree.
    private readonly List<Guid> _renderedOrder = new();
    private readonly Dictionary<Guid, JobRow> _rows = new();

    public ExportQueueWindow(ExportQueue queue, Func<Task> addJob, Func<Task> startQueue)
    {
        _queue = queue;
        _addJob = addJob;
        _startQueue = startQueue;

        Title = "Export Queue";
        Icon = AppIcon.Window;
        Width = 560;
        Height = 420;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.WindowBgBrush;

        _addButton = ToolButton("Add…", accent: false);
        _startButton = ToolButton("Start", accent: true);
        _stopButton = ToolButton("Stop", accent: false);
        _clearButton = ToolButton("Clear Finished", accent: false);
        _addButton.Click += (_, _) => _ = SafeInvoke(_addJob);
        _startButton.Click += (_, _) => _ = SafeInvoke(_startQueue);
        _stopButton.Click += (_, _) => _queue.CancelAll();
        _clearButton.Click += (_, _) => _queue.ClearCompleted();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 14, 16, 10),
            Children = { _addButton, _startButton, _stopButton, _clearButton },
        };

        _list = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 12) };
        _emptyHint = new TextBlock
        {
            Text = "No export jobs yet. Click Add… to queue the current sequence.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 13,
            Margin = new Thickness(16, 8, 16, 0),
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _emptyHint, _list } },
        };

        Content = new DockPanel
        {
            Children =
            {
                toolbar.DockTop(),
                scroller,
            },
        };

        _queue.Changed += OnQueueChanged;
        Rebuild();
    }

    protected override void OnClosed(EventArgs e)
    {
        _queue.Changed -= OnQueueChanged;
        base.OnClosed(e);
    }

    private void OnQueueChanged()
    {
        // Changed may fire on the export worker thread; hop to the UI thread to touch controls.
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Rebuilds the row tree only when the job set/order changed; otherwise updates rows in place.</summary>
    private void Refresh()
    {
        IReadOnlyList<ExportJob> jobs = _queue.Jobs;
        bool sameOrder = jobs.Count == _renderedOrder.Count;
        for (int i = 0; sameOrder && i < jobs.Count; i++)
            sameOrder = jobs[i].Id == _renderedOrder[i];

        if (!sameOrder)
            Rebuild();
        else
            foreach (ExportJob job in jobs)
                if (_rows.TryGetValue(job.Id, out JobRow? row))
                    row.Update(job);

        UpdateToolbar(jobs);
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        _renderedOrder.Clear();

        IReadOnlyList<ExportJob> jobs = _queue.Jobs;
        _emptyHint.IsVisible = jobs.Count == 0;

        foreach (ExportJob job in jobs)
        {
            var row = new JobRow(job, () => _queue.CancelJob(job), () => _queue.Remove(job));
            _rows[job.Id] = row;
            _renderedOrder.Add(job.Id);
            _list.Children.Add(row.Root);
        }

        UpdateToolbar(jobs);
    }

    private void UpdateToolbar(IReadOnlyList<ExportJob> jobs)
    {
        bool running = _queue.IsRunning;
        bool hasPending = jobs.Any(j => j.Status == ExportJobStatus.Queued);
        bool hasFinished = jobs.Any(j => j.IsTerminal);

        _startButton.IsEnabled = hasPending && !running;
        _stopButton.IsEnabled = running || hasPending;
        _clearButton.IsEnabled = hasFinished && !running;
        // Add stays enabled during a run — a job added mid-run is picked up by the sequential worker.
    }

    private static async Task SafeInvoke(Func<Task> action)
    {
        try { await action(); }
        catch { /* the callback surfaces its own errors to the user; never crash the queue window */ }
    }

    private static Button ToolButton(string text, bool accent) => new()
    {
        Content = text,
        Padding = new Thickness(14, 5),
        Foreground = accent ? Brushes.White : Palette.TextBrush,
        Background = accent ? Palette.AccentBrush : Palette.PanelBgBrush,
        CornerRadius = new CornerRadius(5),
    };

    /// <summary>One row in the job list: name + format + status + a progress bar, with Cancel / Remove.</summary>
    private sealed class JobRow
    {
        public Border Root { get; }
        private readonly TextBlock _status;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly Button _remove;

        public JobRow(ExportJob job, Action cancel, Action remove)
        {
            var name = new TextBlock
            {
                Text = job.Name,
                Foreground = Palette.TextBrush,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var subtitle = new TextBlock
            {
                Text = FormatLabel(job.Options.Format),
                Foreground = Palette.FaintTextBrush,
                FontSize = 11,
            };
            _status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 6, 0, 0) };

            _cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(10, 4),
                FontSize = 12,
                Foreground = Palette.TextBrush,
                Background = Palette.PanelBgBrush,
                CornerRadius = new CornerRadius(4),
            };
            _remove = new Button
            {
                Content = "Remove",
                Padding = new Thickness(10, 4),
                FontSize = 12,
                Foreground = Palette.MutedTextBrush,
                Background = Palette.PanelBgBrush,
                CornerRadius = new CornerRadius(4),
            };
            _cancel.Click += (_, _) => cancel();
            _remove.Click += (_, _) => remove();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _cancel, _remove },
            };

            var textCol = new StackPanel
            {
                Children = { name, subtitle, _status, _bar },
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(12, 10),
            };
            grid.Children.Add(textCol);
            Grid.SetColumn(buttons, 1);
            buttons.Margin = new Thickness(12, 0, 0, 0);
            grid.Children.Add(buttons);

            Root = new Border
            {
                Background = Palette.RaisedBgBrush,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Palette.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = grid,
            };

            Update(job);
        }

        public void Update(ExportJob job)
        {
            _bar.Value = job.Progress;
            _bar.IsVisible = job.Status is ExportJobStatus.Running or ExportJobStatus.Queued;

            (_status.Text, _status.Foreground) = job.Status switch
            {
                ExportJobStatus.Queued => ("Queued", Palette.MutedTextBrush),
                ExportJobStatus.Running => ($"Exporting… {job.Progress * 100:0}%", Palette.AccentBrush),
                ExportJobStatus.Succeeded => ("Done", Palette.GoodBrush),
                ExportJobStatus.Cancelled => ("Cancelled", Palette.MutedTextBrush),
                ExportJobStatus.Failed => ($"Failed — {job.Error}", Palette.BadBrush),
                _ => (job.Status.ToString(), Palette.MutedTextBrush),
            };

            // Only a still-running or queued job can be cancelled; only a stopped job can be removed.
            _cancel.IsEnabled = job.Status is ExportJobStatus.Running or ExportJobStatus.Queued;
            _remove.IsEnabled = job.Status != ExportJobStatus.Running;
        }

        private static string FormatLabel(ExportFormat format) =>
            $"{ExportCodecs.Container(format.Container).DisplayName} · " +
            $"{ExportCodecs.Video(format.VideoCodec).DisplayName} + {ExportCodecs.Audio(format.AudioCodec).DisplayName}";
    }
}

/// <summary>Tiny layout helper so the toolbar can dock to the top of a <see cref="DockPanel"/> inline.</summary>
internal static class DockExtensions
{
    public static T DockTop<T>(this T control) where T : Control
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }
}
