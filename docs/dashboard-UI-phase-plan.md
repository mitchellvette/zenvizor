# Dashboard UI polish — implementation record

**Status as of 2026-06-05: COMPLETE.** Phases A → E landed; D.7 implemented
behind a feature flag. Built clean.

This document is the as-implemented record of the Dashboard polish interlude
between Phase 4 (history surface) and Phase 5 (reports). For tokens and
design-system-level decisions, see `docs/design-system.md` §11 (landed-phase
narrative) and §3-§5 (token tables). For ongoing backlog items, this doc
points back at §11.

---

## What landed

| Phase | Scope | Status |
|---|---|---|
| **A** | `status.warming.background` token registered across DesignTokens/HighContrast/design-system/css/primer | ✓ |
| **B** | MainWindow chrome: bottom-bar rate mirror, status-dot brushes migrated to `status.connected`/`.disconnected` tokens, `ActivitySnapshotPoller` lifecycle moved to MainWindow so the mirror updates on every screen | ✓ |
| **C** | DashboardPage layout: 4-up status cards, chart card, talkers card with time-based dimmed-row persistence (~30s), `TalkerRowViewModel` reshaped to `INotifyPropertyChanged`, `RateFormatter` extracted to Services | ✓ |
| **C.0** | IPC contract extension: `ClassBreakdown` DTO + `WanLocalBreakdown` on `ActivitySnapshot`, schema bumped 1→2, CLI snapshot printer surfaces totals, 6 new tests | ✓ |
| **C.5** (+ 3 fixup rounds) | Brand-dict migration: `BrandAccent.{Light,Dark}.xaml` as theme-aware source of truth, `App.xaml.cs.ApplyDirectLevelOverrides()` for Wpf.Ui-shadowed direct-level keys, NavigationView selection chrome | ✓ |
| **C.6** | Chart card `MinHeight=300`, MainWindow `MinHeight=720`/`MinWidth=1000`, talkers `MinHeight=100`/`MaxHeight=320`, `text.metric` style at 24px SemiBold, WAN/LOCAL legend swatches, page-scroll suppression via `FindAncestorScrollViewer`, 500ms minimum-paint floor | ✓ |
| **Nav-selection fix** | Per-item `Initialized` event handlers in `MainWindow.xaml` so `TargetPageType` is set before `NavigationView.OnInitialized` populates the type→item lookup; `Navigate(typeof(DashboardPage))` then resolves the real menu item instead of an orphan | ✓ |
| **Polish round 2** | Full-window Mica showthrough (60% alpha `surface.background` painted ONCE on MainWindow outer Grid, `ApplicationBackgroundBrush` + `NavigationViewContentBackground` direct-overridden to `Transparent`); `metal.card` (gradient brushed surface with baked-in `edge.light`) + `shadow.card` (`DropShadowEffect`) tokens; NavigationView selection — vertical violet fade-out gradient + brand-violet selected icon (`accent.text`) + SemiBold label + 20px icon size + 12px left indent; Up/Dn rate values colored by `chart.upSeries`/`chart.downSeries`; dark `text.on-accent` bug-fix (was black, now white per spec) | ✓ |
| **Phase D** | Chart wiring (see below for as-implemented values) | ✓ |
| **Phase D.7** | Smooth-scroll animation gated behind `EnableChartSmoothScroll`, default OFF | ✓ |
| **Phase E** | Verification scoped to HC token coverage audit; formal screenshot grid declined as redundant after iterative validation | ✓ |

---

## Phase D — as implemented

Touches: `Services/ChartTheming.cs`, `Views/DashboardPage.xaml`,
`Views/DashboardPage.xaml.cs`.

### Series + paints
- `LineSeries.Name` = `"Up"` / `"Down"` (no `/s` — Y axis owns units).
- `chart.upSeries` / `chart.downSeries` brushes read via
  `Application.Current.Resources` and fed into `SolidColorPaint(SKColor)`
  with `StrokeThickness=2` plus a `60/255` (~24%) alpha area fill.
- `ChartTheming.Apply` extended with `ApplyToSeries` so the strokes/fills
  rebuild on `ChartTheming.Changed` (theme flip).

### Y-axis
- `MinLimit=0`, `InitialUpperBound=1024` (1 KB/s floor).
- **Asymmetric EWMA**: instant jump UP when peak exceeds smoothed bound
  (eliminates spike lag), `α=0.3` decay on the way down (anti-jitter).
  *Reversed* from the brief's symmetric EWMA after validation showed the
  symmetric form clipped data off-screen during sudden traffic surges.
- `RoundUpToNiceValue` is **binary-aware**: returns `{1, 2, 5} × 10ⁿ × 1024ᵏ`
  so labels format cleanly through 1024-based `RateFormatter`
  (e.g. 20 KB → 20480 → `"20 KB/s"` instead of decimal 20000 → `"19.5 KB/s"`).
  *Reversed* from the brief's pure-decimal nice values.
- `MinStep = niceUpper / 4` → ticks at 0/25/50/75/100% of MaxLimit.
- Labeler stays `RateFormatter.FormatRate`.

### X-axis
- Text labels suppressed (`Labeler = _ => string.Empty`).
- **Vertical gridlines retained** at 10s intervals
  (`MinStep = TimeSpan.FromSeconds(10).Ticks`). *Reversed* from the brief's
  "no vertical gridlines" lock — user call during D.4 validation: gridlines
  act as visual time anchors between the static overlay markers.
- **Static positional WPF overlay** painting `-2m / -90s / -1m / -30s / now`
  at 0/25/50/75/100% of the plot width. 8-column Grid with
  `Grid.ColumnSpan="2"` on the middle three labels so their centers hit
  the column boundaries. `IsHitTestVisible="False"` so the overlay never
  steals tooltip hits. Visibility flips with chart data state.
- `RatesChart.DrawMargin = new Margin(80, 10, 10, 44)`. *Tuned up* from
  the brief's `(40, 10, 10, 30)` during validation — Left=80 stops the
  plot from drawing over Y-axis labels, Bottom=44 gives vertical breathing
  between the lowest Y label and the X overlay row.
- **Fixed-window scrolling X-axis** (bonus): `MinLimit`/`MaxLimit` anchored
  to the newest data point's timestamp ±120s, updated per tick. Keeps the
  static overlay labels accurate during the first 2 minutes of uptime —
  data accumulates right-to-left rather than stretching across the full
  chart width.

### Tooltip
- Opaque `chart.tooltip.bg` background with `DropShadow(0, 4, 8, 8, 38% black)`
  via `LiveChartsCore.SkiaSharpView.Painting.ImageFilters.DropShadow` for
  backdrop separation.
- `chart.tooltip.text` for text paint.
- `chart.FindingStrategy = FindingStrategy.CompareOnlyXTakeClosest` — X-snap
  to nearest data point regardless of cursor Y.
- `GeometrySize = 20` per series enlarges the hover area;
  `GeometryFill = GeometryStroke = null` keeps the line marker-free.
- `XToolTipLabelFormatter` returns dual-time header:
  `"now · 14:31:55"` or `"-90s · 14:31:55"`.
- `YToolTipLabelFormatter` returns just the rate string (the series Name
  already renders next to the colored stroke icon in the row).
- **Visual layout caveat**: header + per-series rows (vertical). The brief
  called for a strict single-row format
  (`"-90s · 23:34:10 · Up 12 KB/s · Dn 18 KB/s"`). Achieving that requires
  a custom `IChartTooltip<SkiaSharpDrawingContext>` implementation;
  deferred as not worth the substantial work for a formatting preference.

---

## Phase D.7 — smooth-scroll animation (gated OFF)

Wired up behind the `EnableChartSmoothScroll` static readonly flag in
`DashboardPage.xaml.cs`. **OFF by default.**

When enabled:
- `RatesChart.AnimationsSpeed = TimeSpan.FromMilliseconds(2200)` — slightly
  OVER the 2s tick cadence so each tween is interrupted by the next data
  point and chains continuously (no stationary "done" state). *Tuned up*
  from the brief's 1800ms, which left a ~200ms hesitation between ticks.
- `RatesChart.EasingFunction = EasingFunctions.Lineal` (constant velocity).

When disabled: `AnimationsSpeed = TimeSpan.Zero` (snap-only, pre-D.7).

**Why gated off:**
- **Gate 1 (visual) passed** — continuous chained motion, no artifacts,
  static X overlay decoupled from LiveCharts2 so no label desync risk.
- **Gate 2 (perf) failed** — ~8% idle CPU draw, exceeds the project's
  <1% budget.

**Path to user toggle**: graduate `EnableChartSmoothScroll` to a Settings
page checkbox when that page is built (Phase 6 of sprint plan).

---

## Phase E — verification (scoped down)

Decision during close-out: skip the formal 7×2 screenshot grid as redundant
given iterative validation throughout the polish process (each state was
exercised in real conditions during build-validate-iterate cycles).

What did land:
- **Static HC token coverage audit**: every documented semantic token
  (50+ in `design-system.md` §3 + §4) has a `HighContrast.xaml` override
  with sensible `SystemColors` collapses. Three polish round 2 additions
  (`metal.card`, `edge.light`, `shadow.card`) patched in during the audit.
- **End-to-end HC mode activation** depends on the runtime merge wiring
  in `App.xaml.cs` (§11 backlog item #9) — that's a separate task.

---

## Locked decisions (still in force)

### Brand-dict architecture (Phase C.5 outcome)

- `Resources/BrandAccent.{Light,Dark}.xaml` are the source of truth for
  theme-aware brand brushes. They define **explicit colors** (not aliases)
  because Wpf.Ui's ThemesDictionary swap doesn't propagate DynamicResource
  invalidation through `Color="{DynamicResource ...}"` chains at runtime.
- `App.xaml.cs.SwapBrandAccentDictionary()` swaps the dict's `Source` URI
  on `ApplicationThemeManager.Changed`.
- `App.xaml.cs.ApplyDirectLevelOverrides()` writes brand values into
  `Application.Current.Resources` direct level (which beats
  MergedDictionaries in WPF lookup precedence) for keys Wpf.Ui shadows
  there. Queued at `DispatcherPriority.ApplicationIdle` so it runs AFTER
  Wpf.Ui's own direct-level writes (from `SystemThemeWatcher.Watch()`).
- `ApplicationThemeManager.ApplySystemTheme(updateAccent: false)` — Wpf.Ui's
  `ApplySystemAccent` would otherwise overwrite our `SystemAccentColor*`
  overrides with the OS accent.
- `Resources/NavigationViewBrand.xaml` is theme-invariant brand overrides
  for NavigationView. Separate file from BrandAccent because it doesn't
  theme-swap.

### Mica showthrough mechanism (polish round 2 outcome)

- `surface.background` is 60% alpha brand-cool at runtime, painted ONCE
  on the MainWindow outer Grid.
- `ApplicationBackgroundBrush` direct-overridden to `Transparent` so Pages
  render see-through over the MainWindow tint (no double-paint).
- `NavigationViewContentBackground` (and `*GridBorderBrush`) overridden to
  `Transparent` because Wpf.Ui's `LeftNavigationViewTemplate` paints a
  ~30% gray Border inside the content area that would otherwise occlude
  Mica on the page side.
- See `design-system.md` §3 + Mica + Acrylic strategy section for detail.

### Token registration locations

| Surface | File |
|---|---|
| App-side brush + Style + Double | `src/ZenVizor.Ui/Resources/DesignTokens.xaml` |
| App-side HC collapse | `src/ZenVizor.Ui/Resources/HighContrast.xaml` |
| App-side theme-aware override | `src/ZenVizor.Ui/Resources/BrandAccent.{Light,Dark}.xaml` |
| Human-readable spec | `docs/design-system.md` §3 / §4 / §5 |
| Mockup primer | `docs/claude-design-primer.md` |
| Mock-side source of truth | `docs/design/colors_and_type.css` |
| Mock-side primer | `docs/design/SKILL.md` (thin pointer at CSS) |

### Chart behavior locks (Phase D, post-validation)

| Behavior | Value | Note |
|---|---|---|
| Y-axis EWMA | **asymmetric** (instant up, α=0.3 decay) | *amended* from symmetric during validation |
| Y-axis nice values | **{1, 2, 5} × 10ⁿ × 1024ᵏ** | *amended* from pure decimal to match 1024-based RateFormatter |
| X-axis text labels | static positional WPF overlay | locked |
| X-axis vertical gridlines | **shown** at 10s intervals | *reversed* from "no vertical gridlines" lock |
| Tooltip finding strategy | `FindingStrategy.CompareOnlyXTakeClosest` | locked |
| Tooltip layout | header + series rows (multi-line) | strict single-row deferred |
| Disconnect machine | `consecutiveFailures > 1` → steady; `<= 1` → transient | locked |

### Cross-screen consequences (Phase B/C — propagate to every screen)

- Bottom-bar rate mirror (right slot, every screen).
- `ActivitySnapshotPoller` lifecycle owned by MainWindow.
- Service-status dot uses `status.connected` / `status.disconnected` tokens.
- `MainWindow.MinHeight = 720`, `MinWidth = 1000`.

### IPC contract additions (Phase C.0)

- `ActivitySnapshot` carries `ClassBreakdown WanLocalBreakdown` (positional
  field). Schema v2.
- Consumers: Dashboard 4-up status card (WAN vs LOCAL), CLI
  `zvctl snapshot` printer.

### Locked decisions from the original Dashboard brief

- Loading state = default Fluent `ProgressRing`, NOT skeleton-shimmer.
- Top 10 cap on talkers list IS intentional (Per-App is the uncapped drill).
- Disconnected vs query-failed merged on Dashboard only.
- Dimmed-row persistence by TIME (~30s), not rank.
- Series legend names "Up" / "Down" (no `/s`).
- HC handled by dedicated `HighContrast.xaml`.
- Passive-only: never any "Block" / kill / active affordances.

---

## Outstanding follow-ups (open, non-Dashboard)

Not blocking; tracked here for the next UI task to consider:

- **`.sln` gap** (`docs/zenvizor-sprint-plan.md` interlude section):
  `dotnet build ZenVizor.sln` doesn't work; per-project builds work.
  Either add `.sln` or update `CLAUDE.md` to reflect per-project invocation.
- **`NavigationViewFluentIconSize`** left at Wpf.Ui's 24px default. Override
  in `Resources/NavigationViewBrand.xaml` if subsequent screens read as
  having small nav icons.
- **`design-system.md` §11 backlog items 4-11** — other-page polish, HC
  merge wiring, ControlCornerRadius override, etc. Gated behind the
  separate UX discovery passes called out in conversation.

---

## Source-of-truth pointers

- **Original Dashboard brief:** `docs/design-briefs/dashboard.md`
- **Original mockup PDF:** `docs/design/mockups/dashboard-design-mockups.pdf`
- **Design system:** `docs/design-system.md`
- **Sprint plan:** `docs/zenvizor-sprint-plan.md`
- **Mock-side colors:** `docs/design/colors_and_type.css`
- **Mock-side primer:** `docs/claude-design-primer.md`
