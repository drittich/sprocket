# UI Design — Sprocket

> Notes captured from the [`UI.pdf`](UI.pdf) mockup. This is the *target* desktop UI and the
> features its layout implies. Companion to [BRIEF.md](BRIEF.md) (the *what*),
> [PLAN.md](PLAN.md) (build order), and [ARCHITECTURE.md](ARCHITECTURE.md) (the *how*).
> Where the mockup implies a feature beyond the current vertical slice, it is tagged
> **[slice]**, **[deferred]**, or **[new]** and mapped back to an existing seam.

The mockup is a single screen of a polished, professional NLE (non-linear editor) — closest
in spirit to Premiere Pro / DaVinci Resolve / Final Cut, scaled to a focused tool. It is the
north-star layout; the vertical slice implements a subset of it without changing the layout.

---

## 1. Design language

- **Dark theme**, near-black panels (`#12141a`-ish) with subtly lighter raised surfaces.
- **Single accent**: an indigo/violet (`#6c5ce7`-ish) used for the active tool, primary
  buttons (Export), selection highlights, slider fills, and the playhead.
- **Custom window chrome** (frameless): the app draws its own title bar with the menu bar
  inline and custom minimize / maximize / close glyphs at top-right. Implies a borderless
  Avalonia window with a custom caption + hit-test region, not the OS title bar.
- **Three-pane workspace** over a full-width timeline: Project (left) · Monitor (center) ·
  Inspector (right), with the Timeline spanning the bottom and a thin status bar beneath it.
  Panels are titled, with tabs and collapsible sections.
- **Almost all panels are resizable** — specifically the **Project**, **Program**, **Preview**
  (Inspector), and **Timeline** panes. Implies a **splitter-based resizable layout** in
  `Sprocket.App` (draggable splitters between the panes), with the regions sized by the user
  rather than a fixed grid. (Full floating/dockable panels are a larger effort and not implied —
  see §5.)
- **Monospace timecodes** throughout (transport readout, ruler, status bar).

---

## 2. Layout map

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ [S] Sprocket   File Edit Clip Sequence Effects View Window Help                     │  Title + menu bar
│        Episode_04 — Final Cut  • all changes saved                      _ ▢ ✕      │  (project title + save state, window controls)
├──────────────────────────────────────────────────────────────────────────────────┤
│ [Select] Blade Slip Hand Zoom  |  ⌁ Snapping  Linked        1080p·23.98 [Export ▾] AK │  Tool + action bar
├───────────────┬──────────────────────────────────────┬───────────────────────────┤
│ Project  8 it.│  Program | Source              Fit ▾  │ Inspector                 │
│ Media Eff Tra…│ ┌──────────────────────────────────┐  │ ▣ Interview_A.mp4         │
│ 🔍 Search     │ │                                  │  │  Video Clip 3840×2160 …   │
│ ┌────┐ ┌────┐ │ │      (program monitor +          │  │ ── TRANSFORM ──────────⌄  │
│ │thmb│ │thmb│ │ │       safe-area grid)            │  │ Scale        [118%]  ───● │
│ └────┘ └────┘ │ │                                  │  │ Position X   [+24 px]──●─ │
│  …media bin…  │ └──────────────────────────────────┘  │ Position Y   [-8 px] ─●── │
│               │  00:00:18:00  ⏮ ◀◀ ▶ ▶▶ ⏭ 00:01:36:00 │ Rotation … Anchor … Opac. │
│               │                                        │ ── COLOR ──────────────⌄  │
│               │                                        │ Exposure … Contrast …     │
├───────────────┴──────────────────────────────────────┴───────────────────────────┤
│ Timeline  Episode_04 · V1                               ⊟ 100% ⊞   [+ Track]        │
│ ruler  00:00   00:05   00:10  ▮00:18  00:20 …                                        │
│ V2 [M][S] │      [TITLE — Episode 04]        [Adjustment Layer]                      │
│ V1 [M][S] │ [Drone_Establish][ Interview_A.mp4 (selected) ][ Broll_City.mov ]        │
│ A1 [M][S] │            [≈≈ Interview_A.wav ≈≈]                                       │
│ A2 [M][S] │ [≈≈≈≈≈≈≈≈≈≈ Ambient_Score.aif ≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈≈]                    │
├──────────────────────────────────────────────────────────────────────────────────┤
│ ● Ready · GPU · Hardware accelerated      23.98 fps · 1920×1080 · 00:01:36:00 · …   │  Status bar
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Region-by-region detail & implied features

### 3.1 Title + menu bar
- App identity: logo glyph + "Sprocket".
- **Menu bar**: `File · Edit · Clip · Sequence · Effects · View · Window · Help`. Implies the
  full command surface — file/project ops, undo/redo (Edit), clip ops, sequence settings,
  effects browser, view/layout, window/panel management.
- **Project title + save state**: `Episode_04 — Final Cut  • all changes saved`. Implies a
  named project with **autosave / dirty-state tracking** surfaced in chrome. **[new]** —
  persistence exists ([ARCHITECTURE §12](ARCHITECTURE.md)); the *autosave + dirty indicator*
  is a UI/state addition.
- **Custom window controls** (min/max/close) → frameless window (see §1).

### 3.2 Tool + action bar
- **Tool palette** (radio group, one active): **Select · Blade · Slip · Hand · Zoom**.
  - `Select` — default arrow; move/trim clips. **[slice]** (basic move + trim).
  - `Blade` — razor; split a clip at the cursor. **[new, near-slice]** — a timeline edit op.
  - `Slip` — slip a clip's source in/out without moving it on the timeline. **[new]** — maps
    directly to editing `SourceIn/SourceOut` (non-destructive model already supports it).
  - `Hand` — pan the timeline view. **[new]** (view-only).
  - `Zoom` — zoom the timeline view. **[new]** (view-only; see timeline zoom control).
- **Snapping** toggle (active) — snap edits to clip edges / playhead / markers. **[new]**.
- **Linked** toggle — keep linked A/V (a video clip and its companion audio) selected and
  moved together. **[new]** — implies a *clip-link* relation in the model.
- **Sequence badge**: `1080p · 23.98` — the active sequence's render resolution + frame rate
  (`Timeline.Resolution` + `Timeline.FrameRate`, [ARCHITECTURE §4](ARCHITECTURE.md)). Likely
  click-to-open Sequence Settings.
- **Export ▾** (primary button + dropdown) — export with presets. **[slice]** (export exists;
  the *preset dropdown* is a [new] convenience).
- **User avatar** (`AK`) — account/identity. **[new, optional]**, likely out of scope for a
  local desktop tool unless cloud/sync is planned. *Open question (§5).*

### 3.3 Project panel (left) — media browser
- Header `Project · 8 items`.
- **Tabs**: `Media · Effects · Transitions · Audio`.
  - `Media` — the imported-source bin (the `MediaPool`, [ARCHITECTURE §4](ARCHITECTURE.md)).
  - `Effects` — browsable effect library to drag onto clips. **[new]** (effects exist; a
    *browser* is new UI over the `IVideoEffect` registry).
  - `Transitions` — transition library. **[deferred]** — transitions are explicitly deferred
    ([ARCHITECTURE §17](ARCHITECTURE.md)); the tab anticipates them.
  - `Audio` — audio effects / mixer entry. **[new]**.
- **Search** box — filter the bin.
- **Thumbnail grid** with type-aware previews and metadata badges:
  - Video shows a poster frame + `duration · resolution` (`00:22 · 4K`, `00:38 · 1080p`).
  - **Alpha** media flagged (`Logo_Anim.mov · 00:05 · Alpha`) → implies **alpha-channel /
    premultiplied compositing** support. **[new]**.
  - Audio shows a **waveform** thumbnail + `duration · WAV` (`Ambient_Score.aif`,
    `SFX_Whoosh.wav`).
  - Implies: thumbnail/poster generation, **waveform rendering**, and a probe step surfacing
    duration/resolution/format/alpha. Probe already exists ([ARCHITECTURE §11](ARCHITECTURE.md));
    thumbnails + waveforms are **[new]** UI-side rendering.

### 3.4 Center — Monitor
- **Dual monitor model**: `Program` (active) / `Source` tabs. Program = the composited
  timeline output at the playhead; Source = a raw clip preview for setting in/out before
  editing. **[deferred]** — the slice has a single **preview** surface; the source monitor
  is a later addition. The render path ([ARCHITECTURE §5](ARCHITECTURE.md)) serves both.
- **Safe-area / framing grid** overlay (rule-of-thirds + title/action-safe guides), toggleable.
  **[new]** (an overlay on the preview surface).
- **`Fit ▾`** — preview zoom level (Fit / 100% / …). **[new]**.
- **Transport**: `⏮  ◀◀  ▶  ▶▶  ⏭` (jump-to-start, step/shuttle back, play/pause,
  step/shuttle forward, jump-to-end). **[slice]** (play/pause + scrub; per-frame stepping is a
  small add).
- **Timecodes**: current playhead `00:00:18:00` (left) and sequence duration/out `00:01:36:00`
  (right), both `HH:MM:SS:FF` drop-frame-aware. Backed by the tick clock
  ([ARCHITECTURE §3](ARCHITECTURE.md)).

### 3.5 Inspector (right) — selected-clip properties
Shows the selected clip's identity (`Interview_A.mp4 · Video Clip · 3840×2160 · 23.98p`) and
**collapsible parameter sections**, each control = an animatable parameter
([ARCHITECTURE §9](ARCHITECTURE.md)), editable by slider or numeric entry:
- **TRANSFORM**: `Scale 118%`, `Position X +24 px`, `Position Y -8 px`, `Rotation 0.0°`,
  `Anchor Center`, `Opacity 100%`. Implies a **geometric transform effect** (scale / translate
  / rotate about an anchor) + per-clip opacity. **[new]** — a new built-in `IVideoEffect`
  (the slice ships brightness + fade); slots into the effect chain with no model change.
- **COLOR**: `Exposure +0.35`, `Contrast +12`, … (continues below the fold). Implies
  exposure/contrast/(color) grading. **[slice/new]** — brightness is in-slice; exposure /
  contrast / color are the same SkSL-shader shape ([ARCHITECTURE §7](ARCHITECTURE.md)).
- **Implication**: each section is a parameter group on an effect; the row's numeric field +
  slider both bind to one `AnimatableValue`, so adding **keyframe** affordances later is
  additive. A clip with *no* selection / a different clip type (title, audio) would show a
  different inspector — implies a **type-driven inspector**.

### 3.6 Timeline (bottom)
- Header `Timeline · Episode_04 · V1` (sequence name + focused track).
- **Zoom controls** `⊟ 100% ⊞` and **`+ Track`** (add a video/audio track). Multiple tracks
  are in [BRIEF.md](BRIEF.md); the slice is 1 V + 1 A, so 2+ tracks are **[deferred]** but the
  data model is already track-array based ([ARCHITECTURE §4](ARCHITECTURE.md)).
- **Time ruler** in `MM:SS` with a **playhead** (indigo line + handle) at the current time;
  ruler + playhead are scrubbing UI over the tick clock.
- **Tracks** (top→bottom, video above audio):
  - `V2`: a **title clip** `TITLE — Episode 04` (distinct color) and an **Adjustment Layer**.
    - **Title / generator clips** → a clip whose source is generated (text), not media file.
      **[new]** — a new `IFrameSource` kind (generator) feeding the same render graph.
    - **Adjustment Layer** → a clip that applies its effect stack to *all tracks beneath it*
      for its time span. **[new]** — render-graph extension: composite lower tracks, then run
      the adjustment layer's effects over the result before upper tracks. Slots into
      [ARCHITECTURE §5](ARCHITECTURE.md) as a track/clip kind, no model rewrite.
  - `V1`: video clips `Drone_Establish.mp4`, `Interview_A.mp4` (**selected** — indigo border,
    drives the Inspector), `Broll_City.mov`. Clips render with **filmstrip/thumbnail** fills.
  - `A1` / `A2`: audio clips `Interview_A.wav`, `Ambient_Score.aif` with **waveform** fills.
  - Each track has **`M` (Mute) / `S` (Solo)** toggles → already in the model
    (`AudioTrack.Muted/Solo`; video `Enabled`), [ARCHITECTURE §4/§6](ARCHITECTURE.md).
- **Implies**: a custom-drawn timeline control (clip thumbnails, audio waveforms, ruler,
  playhead, drag/trim handles, snapping guides) — the most involved bespoke control in
  `Sprocket.App`.

### 3.7 Status bar
The mockup reads `● Ready · GPU · Hardware accelerated`  …  `23.98 fps · 1920 × 1080 ·
Duration 00:01:36:00 · Avalonia · .NET 9`. Surfaces engine state + live diagnostics:
render/decode state, **GPU / hardware-accel status**, **live fps**, sequence resolution,
duration. Maps to the diagnostics counters in [ARCHITECTURE §15](ARCHITECTURE.md) (fps,
dropped frames, GPU path).

> **Drop the stack identity from the status bar.** The mockup's trailing `Avalonia · .NET 9`
> (and any "`.NET 10`") should **not** appear in the UI — the framework and runtime are
> implementation details, not user-facing. Keep the engine/perf telemetry (state, GPU,
> hw-accel, fps, resolution, duration); omit the tech-stack text.

---

## 4. Feature checklist implied by the mockup

| Feature | Tag | Maps to |
|---|---|---|
| Frameless window + custom chrome/menu | new | `Sprocket.App` window shell |
| Named project, autosave, dirty indicator | new | Persistence §12 + app state |
| Tool palette: Select / Blade / Slip / Hand / Zoom | mixed | timeline edit ops (Select/Slip in-model; Blade [new]; Hand/Zoom view-only) |
| Snapping, Linked A/V | new | timeline edit behavior + clip-link relation |
| Media bin: thumbnails, waveforms, search, format/alpha badges | new | probe (exists) + thumbnail/waveform rendering |
| Effects / Transitions / Audio browsers | mixed | Effects [new browser]; Transitions [deferred §17] |
| Dual Source/Program monitors | deferred | same render graph §5, second surface |
| Safe-area grid + Fit zoom on preview | new | preview overlay |
| Full transport (jump/step/play) | slice+ | playback §8 |
| Inspector: Transform (scale/pos/rot/anchor/opacity) | new | new transform `IVideoEffect` |
| Inspector: Color (exposure/contrast/…) | slice+ | SkSL effects §7 (brightness is in-slice) |
| Animatable params (sliders + numeric) | slice | `AnimatableValue` §9 |
| Multiple V/A tracks + add track | deferred | track array §4 (slice = 1+1) |
| Title / generator clips | new | generator `IFrameSource` |
| Adjustment layers | new | render-graph track-effect stage §5 |
| Per-track Mute/Solo | exists | `AudioTrack.Muted/Solo`, video `Enabled` §4/§6 |
| Timeline zoom, clip thumbnails, waveforms, playhead | new | bespoke timeline control |
| Export with presets | slice+ | export §11 + preset UI |
| Status bar: GPU/hw-accel/fps/duration telemetry | new (surface) | diagnostics §15 |
| Alpha-channel media compositing | new | premultiplied-alpha path in render graph |

**Legend** — *slice*: in the vertical-slice DoD ([PLAN.md](PLAN.md)); *slice+*: slice has the
core, mockup extends it; *new*: not yet planned, but lands on an existing seam; *deferred*:
explicitly post-slice ([ARCHITECTURE §17](ARCHITECTURE.md)); *exists*: already in the model.

---

## 5. Open questions / reconciliations

- **No tech-stack text in the UI.** The mockup's status bar ends with `Avalonia · .NET 9` —
  this is both wrong (the project targets **.NET 10**, not 9) and unwanted: the framework and
  runtime are implementation details and should not be user-facing. Omit them from the status
  bar entirely (§3.7).
- **User avatar (`AK`)** implies account/identity/cloud. No cloud component is in scope today —
  decide whether this is decorative or signals a future sync/collaboration feature before
  building anything behind it.
- **Source monitor + Transitions + Adjustment layers + Title generators** are coherent with the
  architecture's seams but are **post-slice**; sequence them after the slice's DoD is met.
- **Panel docking**: panels are **splitter-resizable** in the fixed three-pane + timeline
  arrangement (confirmed — §1). Fully dockable/floatable/rearrangeable panels are a larger
  effort and **not** required to match the mockup; treat as a possible later enhancement.
