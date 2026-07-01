using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Sprocket.App;

/// <summary>
/// The single source of truth for Sprocket's core color tokens (UI.md §1): near-black surfaces, a
/// neutral text ramp that clears WCAG AA, one indigo accent, and the semantic status colors.
/// <para>
/// Both the XAML shell (<c>App.axaml</c>, via <c>{x:Static}</c>) and the custom-drawn Skia surfaces
/// (Timeline, Inspector, Media browser, keyframe lanes, stats overlay, dialogs) consume these, so the
/// palette can no longer drift between them. Before this existed the palette was copy-pasted into ~8
/// files and had already diverged — the accent was #4227a3 in the shell/dialogs but #6C5CE7 in every
/// drawn surface, and <see cref="FaintText"/> was a darker #6A7180 in the drawn surfaces that failed
/// AA at label sizes. Component-specific colors (clip fills, marker bands, lane stripes) stay local to
/// their control; only the genuinely shared tokens live here.
/// </para>
/// </summary>
public static class Palette
{
    // Surfaces, dark → raised.
    public static readonly Color WindowBg = Color.Parse("#0E0E12");
    public static readonly Color PanelBg = Color.Parse("#16161C");
    public static readonly Color RaisedBg = Color.Parse("#22222B");
    public static readonly Color Edge = Color.Parse("#2A2A33");

    // Text ramp. Every step clears 4.5:1 on the surfaces above at label sizes; FaintText is the AA-safe
    // floor (5.8:1 on PanelBg) — the drawn surfaces used to define it as #6A7180 (≈3.3:1), which failed.
    public static readonly Color Text = Color.Parse("#D5DBE6");
    public static readonly Color MutedText = Color.Parse("#9AA4B2");
    public static readonly Color FaintText = Color.Parse("#8A93A3");

    // One accent, chosen so it works both as a fill behind white text (4.86:1) and as a 1–2px indicator
    // line on the dark surfaces (playhead, selection outline, keyframes). Hover darkens so white text on
    // the Export / Play buttons stays ≥4.5:1 (the old #7D6FF0 hover was 3.9:1 and failed AA).
    public static readonly Color Accent = Color.Parse("#6C5CE7");
    public static readonly Color AccentHover = Color.Parse("#5B4BD6");

    // Semantic status — status-bar state dot + playback-stats health readout.
    public static readonly Color Good = Color.Parse("#3FB950");
    public static readonly Color Warn = Color.Parse("#D29922");
    public static readonly Color Bad = Color.Parse("#F85149");

    // Destructive action (window close-button hover).
    public static readonly Color Danger = Color.Parse("#C0392B");

    // Shared immutable brushes for the custom-drawn surfaces. ImmutableSolidColorBrush is safe to reuse
    // across every draw — this is what lets the drawing hot path stay allocation-free (ARCHITECTURE §1).
    public static readonly IBrush WindowBgBrush = new ImmutableSolidColorBrush(WindowBg);
    public static readonly IBrush PanelBgBrush = new ImmutableSolidColorBrush(PanelBg);
    public static readonly IBrush RaisedBgBrush = new ImmutableSolidColorBrush(RaisedBg);
    public static readonly IBrush EdgeBrush = new ImmutableSolidColorBrush(Edge);
    public static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Text);
    public static readonly IBrush MutedTextBrush = new ImmutableSolidColorBrush(MutedText);
    public static readonly IBrush FaintTextBrush = new ImmutableSolidColorBrush(FaintText);
    public static readonly IBrush AccentBrush = new ImmutableSolidColorBrush(Accent);
    public static readonly IBrush GoodBrush = new ImmutableSolidColorBrush(Good);
    public static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Warn);
    public static readonly IBrush BadBrush = new ImmutableSolidColorBrush(Bad);
}
