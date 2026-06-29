using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// The Project panel's tabbed browser (PLAN.md step 15, UI.md §3.3): a <b>Media</b> bin of poster-frame /
/// waveform thumbnails with metadata badges and a search filter, an <b>Effects</b> browser over the
/// <see cref="EffectCatalog"/> (double-click to add the effect to the selected clip, through the step-10
/// command stack), a deferred <b>Transitions</b> tab (PLAN.md step 21), and an <b>Audio</b> tab listing the
/// bin's audio sources as waveforms. Built entirely in code like <see cref="TimelineControl"/> /
/// <see cref="PreviewSurface"/>; thumbnails are produced off-thread by <see cref="ThumbnailService"/>.
/// </summary>
public sealed class MediaBrowserPanel : UserControl
{
    // Palette (mirrors App.axaml so the code control matches the themed shell).
    private static readonly IBrush PanelBg = Hex("#16161C");
    private static readonly IBrush RaisedBg = Hex("#22222B");
    private static readonly IBrush PosterBg = Hex("#0E0E12");
    private static readonly IBrush Edge = Hex("#2A2A33");
    private static readonly IBrush TextBrush = Hex("#D5DBE6");
    private static readonly IBrush MutedText = Hex("#9AA4B2");
    private static readonly IBrush FaintText = Hex("#6A7180");
    private static readonly IBrush Accent = Hex("#6C5CE7");
    private static readonly IBrush BadgeBg = Hex("#2E2E38");

    private const double TileWidth = 116;
    private const int PosterW = 104;
    private const int PosterH = 58;

    private Project? _project;
    private EditHistory? _history;
    private ThumbnailService? _thumbs;
    private Clip? _selectedClip;

    private string _search = string.Empty;
    private Tab _activeTab = Tab.Media;

    // Built-once chrome.
    private readonly TextBox _searchBox;
    private readonly WrapPanel _mediaGrid;
    private readonly WrapPanel _audioGrid;
    private readonly StackPanel _effectsList;
    private readonly Decorator _content;            // hosts the active tab's body
    private readonly ScrollViewer _mediaView, _audioView, _effectsView;
    private readonly Control _transitionsView;
    private readonly Dictionary<Tab, Button> _tabButtons = new();

    /// <summary>Raised with a short message for the status strip (effect applied / select-a-clip hint).</summary>
    public event Action<string>? Status;

    /// <summary>Raised with the media-bin item count when the bin is (re)populated, for the pane header.</summary>
    public event Action<int>? ItemCountChanged;

    /// <summary>Raised when OS files are dropped on the bin (PLAN.md step 16b); the shell imports them.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    private enum Tab { Media, Effects, Transitions, Audio }

    public MediaBrowserPanel()
    {
        _searchBox = new TextBox
        {
            PlaceholderText = "Search media…",
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 6),
            Background = PanelBg,
            BorderBrush = Edge,
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _search = _searchBox.Text ?? string.Empty;
            RebuildGrids();
        };

        _mediaGrid = new WrapPanel { Margin = new Avalonia.Thickness(6) };
        _audioGrid = new WrapPanel { Margin = new Avalonia.Thickness(6) };
        _effectsList = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };

        _mediaView = Scroll(_mediaGrid);
        _audioView = Scroll(_audioGrid);
        _effectsView = Scroll(_effectsList);
        _transitionsView = Placeholder(
            "Transitions arrive with the transition library (step 21).\nThe render graph resolves overlapping clips then.");

        _content = new Decorator();

        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Avalonia.Thickness(8, 6),
        };
        foreach (Tab t in Enum.GetValues<Tab>())
        {
            Button button = TabButton(t);
            _tabButtons[t] = button;
            tabs.Children.Add(button);
        }

        var root = new DockPanel();
        var tabsBar = new Border
        {
            Background = PanelBg,
            BorderBrush = Edge,
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Child = tabs,
        };
        DockPanel.SetDock(tabsBar, Dock.Top);
        DockPanel.SetDock(_searchBox, Dock.Top);
        root.Children.Add(tabsBar);
        root.Children.Add(_searchBox);
        root.Children.Add(_content);
        Content = root;

        // OS file-drop onto the bin imports media (PLAN.md step 16b); the shell does the probe + add.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFilesDragOver);
        AddHandler(DragDrop.DropEvent, OnFilesDrop);

        SelectTab(Tab.Media);
    }

    /// <summary>Binds the browser to the project, the shared edit history, and the thumbnail service. Call once.</summary>
    public void Attach(Project project, EditHistory history, ThumbnailService thumbs)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(thumbs);
        _project = project;
        _history = history;
        _thumbs = thumbs;
        BuildEffects();
        RebuildGrids();
    }

    /// <summary>Sets the clip the Effects browser will apply effects to (driven by the timeline selection).</summary>
    public void SetSelectedClip(Clip? clip) => _selectedClip = clip;

    /// <summary>Re-reads the <see cref="MediaPool"/> into the bin (after an import or undo, PLAN.md step 16b).</summary>
    public void Refresh() => RebuildGrids();

    // ── OS file drop (import) ─────────────────────────────────────────────────────────────────────────

    private void OnFilesDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnFilesDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
            return;
        var paths = new List<string>();
        foreach (IStorageItem item in e.DataTransfer.TryGetFiles() ?? [])
            if (item is IStorageFile file && file.TryGetLocalPath() is { } path)
                paths.Add(path);
        if (paths.Count > 0)
            FilesDropped?.Invoke(paths);
    }

    // ── Tabs ────────────────────────────────────────────────────────────────────────────────────────

    private Button TabButton(Tab tab)
    {
        var button = new Button
        {
            Content = tab.ToString(),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = default,
            Padding = new Avalonia.Thickness(2, 2),
            Foreground = FaintText,
        };
        button.Click += (_, _) => SelectTab(tab);
        return button;
    }

    private void SelectTab(Tab tab)
    {
        _activeTab = tab;
        foreach ((Tab t, Button b) in _tabButtons)
        {
            b.Foreground = t == tab ? TextBrush : FaintText;
            b.FontWeight = t == tab ? FontWeight.SemiBold : FontWeight.Normal;
        }

        _searchBox.IsVisible = tab is Tab.Media or Tab.Audio;
        _content.Child = tab switch
        {
            Tab.Media => _mediaView,
            Tab.Effects => _effectsView,
            Tab.Transitions => _transitionsView,
            Tab.Audio => _audioView,
            _ => _mediaView,
        };
    }

    // ── Media / Audio grids ───────────────────────────────────────────────────────────────────────────

    private void RebuildGrids()
    {
        _mediaGrid.Children.Clear();
        _audioGrid.Children.Clear();
        if (_project is null || _thumbs is null)
            return;

        List<MediaRef> items = _project.MediaPool.Items
            .OrderBy(m => Path.GetFileName(m.AbsolutePath), StringComparer.OrdinalIgnoreCase)
            .ToList();
        ItemCountChanged?.Invoke(items.Count);

        foreach (MediaRef media in items)
        {
            string name = Path.GetFileName(media.AbsolutePath);
            if (!MediaSearch.Matches(name, _search))
                continue;

            _mediaGrid.Children.Add(BuildTile(media, name));
            if (media.Info.HasAudio)
                _audioGrid.Children.Add(BuildTile(media, name, audioView: true));
        }

        if (_mediaGrid.Children.Count == 0)
            _mediaGrid.Children.Add(EmptyNote(_search.Length > 0 ? "No media matches the search." : "No media imported."));
        if (_audioGrid.Children.Count == 0)
            _audioGrid.Children.Add(EmptyNote("No audio sources."));
    }

    /// <summary>Builds one bin tile: a poster (video) or waveform (audio) thumbnail, the filename, and badges.</summary>
    private Control BuildTile(MediaRef media, string name, bool audioView = false)
    {
        // In the Audio tab, or for audio-only sources, the thumbnail is the waveform; otherwise the poster.
        bool useWaveform = audioView || !media.Info.HasVideo;

        var poster = new Border
        {
            Width = PosterW,
            Height = PosterH,
            Background = PosterBg,
            CornerRadius = new Avalonia.CornerRadius(3),
            ClipToBounds = true,
        };
        var fallback = new TextBlock
        {
            Text = useWaveform ? "♪" : "▦",
            FontSize = 22,
            Foreground = FaintText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        poster.Child = fallback;

        Task<Bitmap?> task = useWaveform
            ? _thumbs!.GetWaveformAsync(media, PosterW, PosterH)
            : _thumbs!.GetPosterAsync(media, PosterW, PosterH);
        LoadThumb(poster, task);

        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = PosterW,
            Margin = new Avalonia.Thickness(0, 4, 0, 2),
        };

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (string badge in MediaBadges.Describe(media.Info, media.AbsolutePath))
            badges.Children.Add(Badge(badge));

        var stack = new StackPanel { Width = TileWidth - 12 };
        stack.Children.Add(poster);
        stack.Children.Add(nameText);
        stack.Children.Add(badges);

        var tile = new Border
        {
            Width = TileWidth,
            Margin = new Avalonia.Thickness(4),
            Padding = new Avalonia.Thickness(6),
            Background = RaisedBg,
            CornerRadius = new Avalonia.CornerRadius(5),
            Child = stack,
        };
        ToolTip.SetTip(tile, "Drag onto a timeline track to place a clip.");
        // Drag the source onto the timeline to place a clip (PLAN.md step 16b).
        EnableDrag(tile, DragFormats.MediaRefId, () => media.Id.Value.ToString());
        return tile;
    }

    private async void LoadThumb(Border holder, Task<Bitmap?> task)
    {
        try
        {
            Bitmap? bitmap = await task; // resumes on the UI thread (Avalonia sync context)
            if (bitmap is not null)
                holder.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch
        {
            // Leave the fallback glyph in place on failure (§15).
        }
    }

    private static Border Badge(string text) => new()
    {
        Background = BadgeBg,
        CornerRadius = new Avalonia.CornerRadius(3),
        Padding = new Avalonia.Thickness(5, 1),
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = MutedText },
    };

    // ── Effects browser ───────────────────────────────────────────────────────────────────────────────

    private void BuildEffects()
    {
        _effectsList.Children.Clear();
        _effectsList.Children.Add(new TextBlock
        {
            Text = "Double-click an effect to add it to the selected clip.",
            FontSize = 11,
            Foreground = FaintText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        });

        foreach (EffectDescriptor effect in EffectCatalog.BuiltIns)
            _effectsList.Children.Add(EffectRow(effect));
    }

    private Control EffectRow(EffectDescriptor effect)
    {
        var title = new TextBlock { Text = effect.DisplayName, FontSize = 12, Foreground = TextBrush, FontWeight = FontWeight.SemiBold };
        var category = new TextBlock { Text = effect.Category.ToString(), FontSize = 10, Foreground = Accent };
        var header = new DockPanel();
        DockPanel.SetDock(category, Dock.Right);
        header.Children.Add(category);
        header.Children.Add(title);

        var desc = new TextBlock
        {
            Text = effect.Description,
            FontSize = 11,
            Foreground = MutedText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var row = new Border
        {
            Background = RaisedBg,
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, 6),
            Child = new StackPanel { Children = { header, desc } },
        };
        row.DoubleTapped += (_, _) => ApplyEffect(effect);
        ToolTip.SetTip(row, "Double-click to add to the selected clip, or drag onto a timeline clip.");
        // Drag the effect onto a timeline clip to append it (PLAN.md step 16b), complementing double-click.
        EnableDrag(row, DragFormats.EffectId, () => effect.Id);
        return row;
    }

    // ── Drag source ─────────────────────────────────────────────────────────────────────────────────

    // Pending-drag state: a press arms a drag that only begins once the pointer moves past a small threshold,
    // so a plain click (double-click to apply an effect, selecting a tile) still works. Avalonia 12's
    // DoDragDropAsync needs the originating PointerPressedEventArgs, so we hold it until the move fires.
    private Point _dragStart;
    private PointerPressedEventArgs? _pressedArgs;

    /// <summary>Makes <paramref name="source"/> a drag source carrying <paramref name="payloadFactory"/>'s string
    /// under <paramref name="format"/> once the pointer moves past a threshold.</summary>
    private void EnableDrag(Control source, DataFormat<string> format, Func<string> payloadFactory)
    {
        source.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
            {
                _pressedArgs = e;
                _dragStart = e.GetPosition(this);
            }
        };
        source.PointerMoved += (_, e) =>
        {
            if (_pressedArgs is not { } pressed || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
                return;
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4)
                return;

            _pressedArgs = null;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(format, payloadFactory()));
            _ = DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy); // fire-and-forget; result not needed
        };
        source.PointerReleased += (_, _) => _pressedArgs = null;
    }

    private void ApplyEffect(EffectDescriptor effect)
    {
        if (_selectedClip is null || _history is null || _project is null)
        {
            Status?.Invoke("Select a clip in the timeline to apply an effect.");
            return;
        }

        _history.Execute(new AddEffectCommand(_selectedClip, effect.CreateInstance()));
        string clip = Path.GetFileName(_project.MediaPool.Get(_selectedClip.MediaRefId)?.AbsolutePath ?? "clip");
        Status?.Invoke($"Added {effect.DisplayName} to {clip}.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    private static Control Placeholder(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Foreground = FaintText,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Avalonia.Thickness(16),
    };

    private static Control EmptyNote(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = FaintText,
        Margin = new Avalonia.Thickness(8, 12),
    };

    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));
}
