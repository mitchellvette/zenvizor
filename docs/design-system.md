# ZenVizor design system

Single source of truth for ZenVizor's visual language **on the app side**.
Co-evolves with `src/ZenVizor.Ui/Resources/Fonts.xaml`,
`Resources/DesignTokens.xaml`, and `Resources/HighContrast.xaml` — those
three files implement the tokens documented here.

The mock-side source of truth is `docs/design/colors_and_type.css` (with
`docs/design/README.md` and `docs/design/SKILL.md` as the brand companion).
Token names match between the two; value differences are tracked in the
crosswalk in the CSS header. See `CLAUDE.md` "Design system: source of
truth" for the contract.

This doc is XAML-aware: it cites current control structure so a Claude Code
session has full context. The XAML-free condensed projection for pasting into
Claude Design is `docs/claude-design-primer.md`; keep the two in sync — token
changes here must be reflected there.

---

## 1. UI surface inventory

What ships today, walked from `src/ZenVizor.Ui/`:

### `MainWindow.xaml`

- Chrome: `ui:FluentWindow` with `WindowBackdropType="Mica"` and
  `ExtendsContentIntoTitleBar="True"` — Mica is on, title bar is owned by
  `ui:TitleBar`.
- Layout: 3-row Grid — title bar / content / status bar.
- Navigation: `ui:NavigationView` (left pane, 220px) with menu items
  Dashboard / Per-App / History / Reports / Alerts; footer item Settings.
  Target pages are wired in code (`MainWindow.xaml.cs:24`) to dodge the
  same-assembly `x:Type` resolution issue; navigation cache mode is
  `Enabled` for every item (`MainWindow.xaml.cs:34`) so picker state
  survives a nav-rail click.
- Bottom bar: 1px top-border strip, holds the service-status `Ellipse`
  (hardcoded `Brushes.DarkOrange` / `Brushes.MediumSeaGreen` in
  `MainWindow.xaml.cs:108-114` — this is the tokenization gap).
- Tray: `H.NotifyIcon.Wpf` `TaskbarIcon`, close-to-tray via
  `OnClosing` (`MainWindow.xaml.cs:54`).

### `Views/DashboardPage.xaml` — current activity

- 3-row Grid (`Margin="24"`): header row / chart row / talkers row.
- Header: `ui:TextBlock FontTypography="Subtitle"` "Current Activity" with
  inline `WarmingBanner` and `DisconnectedBanner` (both `Visibility="Collapsed"` until needed).
- Chart card: canonical recipe (`metal.card` + `border.card` +
  `radius.card` + `shadow.card`) wrapping `lvc:CartesianChart x:Name="RatesChart"`,
  two `LineSeries<DateTimePoint>` (Up B/s, Down B/s), 60-point trailing
  window, 2s poll cadence.
- Talkers card: same canonical recipe — header strip + `ListView x:Name="TalkersList"`
  (top 10 by total bytes). Talkers rows are intentionally NOT drillable —
  AppActivity carries no app_id (in-memory snapshot path) so the canonical
  drill would require an extra IPC lookup or piercing the capture
  pipeline; Phase 6 scope.

### `Views/PerAppPage.xaml` — apps over a window

- 3-row Grid: header / picker row / DataGrid card.
- Window preset combo (1h/24h/7d/30d/90d, default 24h) + Refresh button.
- `DataGrid x:Name="AppsGrid"` with virtualization on; `MaxHeight`
  enforced in code via `EnforceAppsGridBound`
  (`PerAppPage.xaml.cs` `OnLoaded` + `SizeChanged`) because
  `ui:NavigationView` wraps pages in a `DynamicScrollViewer` that gives
  infinite vertical extent. **Single-click** navigates to App Detail (hover
  chevron + `Cursor=Hand` + single click is the canonical drill
  affordance — never double-click).
- 5 columns: App / Publisher / Signature / Up / Down.

### `Views/AppDetailPage.xaml` — drill-down

- 5-row Grid: back-button header / window picker / summary card / chart
  card / side-by-side grids.
- Header: `<` Per-App button + `HeaderText` (filled in code from
  `ApplyDetail`).
- Summary card: two `TextBlock`s — line 1 publisher/signature/grain,
  line 2 path/up/down.
- Chart card: `lvc:CartesianChart x:Name="SeriesChart"` (Series picked
  by `ChartBuilder` — `LineSeries` for Samples grain, `StackedColumnSeries`
  for Hourly/Daily). `NoDataOverlay` `TextBlock` centered when empty.
- Two side-by-side cards in the bottom row:
  - **Connections** `DataGrid x:Name="ConnectionsGrid"` — Proto / Address / Port / Up / Down.
  - **Recent sessions** `DataGrid x:Name="SessionsGrid"` — Session / PID / Start / End / Services.
- Both grids virtualized; `MaxHeight` enforced in `EnforceDataGridBounds`
  (`AppDetailPage.xaml.cs:62`).

### `Views/HistoryPage.xaml` — aggregate timeline

- 4-row Grid: header / picker / summary strip / chart.
- Window preset combo + Refresh button.
- Summary strip: subtitle (grain + window described by
  `ChartBuilder.DescribeView`) + count/up/down line.
- Chart card: `lvc:CartesianChart x:Name="HistoryChart"` with the same
  `ChartBuilder.BuildSeries` flow as App Detail; `NoDataOverlay` likewise.

### `Views/ReportsPage.xaml` — daily report (full page, post-Phase 5)

- Date picker + anchor menu (`Avg7d` default / `Avg30d` / `Avg90d` /
  `SpecificDate` placeholder). Refresh + Export CSV/HTML actions.
- Hero card (Up/Down/Total/WAN-ratio with delta-vs-anchor chips),
  24-hour sparkline chart (`SparklineChart`), Top Apps DataGrid
  (`TopAppsGrid`), Uncommon Talkers mini-card row, Notable section by
  severity (Critical/Warning/Info).
- Top Apps row: single-click drill → `AppDetailPage` carrying
  `AppDetailNavParams(appId, reportDate)`. Uncommon Talker mini-card:
  same drill semantics (`Cursor=Hand` on the card root).
- `TopAppsGrid` `MaxHeight` enforced in code (`EnforceTopAppsGridBound`,
  Loaded before first IPC + SizeChanged) for the same NavigationView
  reason as PerApp; wheel forwarder is conditional — forwards to
  PageScroll only when the inner ScrollViewer has no headroom in the
  requested direction.

### `Views/PlaceholderPage.xaml` + `AlertsPage.cs` / `SettingsPage.cs`

- Centered `ui:TextBlock FontTypography="TitleLarge"` title + secondary
  subtitle. Two subclasses populate title/subtitle constructor args.
- Subjects: Alerts → Phase 6 alert feed; Settings → Phase 6
  autostart/retention.
- **Polish-interlude scope:** placeholder treatment is fine for now —
  these pages get real content in their phase. But the placeholder
  *visual* should match the design system once it lands (typography
  tokens, surface tokens) so the empty state reads as deliberate.

### Primary controls inventory

`ui:FluentWindow`, `ui:TitleBar`, `ui:NavigationView`, `ui:NavigationViewItem`,
`ui:SymbolIcon`, `ui:TextBlock`, `ui:Button`, `tb:TaskbarIcon`, WPF `Border`,
`Grid`, `StackPanel`, `TextBlock`, `Ellipse`, `ComboBox`, `DataGrid`,
`DataGridTextColumn`, `ListView`, `lvc:CartesianChart`.

---

## 2. Per-screen state matrix

Non-happy states for each surface. **Polish must cover every cell** — this
matrix is the inventory the mockups need to span. Existing in-repo states
are marked `existing`; gaps are `todo`.

| Page          | empty (no data yet)             | loading                             | warming (capture started, no flush yet) | service-disconnected                                 | error (query failed)                                |
|---------------|---------------------------------|-------------------------------------|-----------------------------------------|------------------------------------------------------|-----------------------------------------------------|
| Dashboard     | `existing` — `WarmingBanner` covers this | `todo` — initial paint shows empty chart + empty list, no cue | `existing` — `WarmingBanner` (orange) `Visibility=Visible` until first snapshot arrives | `existing` — `DisconnectedBanner` (caution) with reason string from `update.FailureReason` | `todo` — `ApplicationStatusPoller` failures aren't surfaced separately |
| Per-App       | `todo` — empty grid + no message | `existing` — `Mouse.OverrideCursor = Wait` during `RefreshAsync` | n/a (history surface; warming is for live) | `todo` — same as error; banner used for all query failures | `existing` — `StatusBanner` shows `Query failed (<Type>): <msg>` |
| App Detail    | `existing` — `NoDataOverlay` "No traffic recorded in this window." | `existing` — wait cursor in `RefreshAsync` | n/a | `todo` — query failure path doesn't distinguish disconnected | `existing` — `StatusBanner` |
| History       | `existing` — `NoDataOverlay` "No traffic recorded in this window." | `existing` — wait cursor | n/a | `todo` | `existing` — `StatusBanner` |
| Reports       | `todo` (placeholder pre-Phase 5)  | n/a | n/a | n/a | n/a |
| Alerts        | `todo` (placeholder pre-Phase 6)  | n/a | n/a | n/a | n/a |
| Settings      | `todo` (placeholder pre-Phase 6)  | n/a | n/a | n/a | n/a |

### What the polish pass should add

- **Loading-vs-empty distinction on Per-App / History.** Today a fresh page
  before `RefreshAsync` resolves shows an empty grid/chart with no cue.
  **Use a centered Fluent `ProgressRing`** in the surface that will hold the
  data (chart card / grid viewport) — indeterminate when no progress
  fraction is known (most queries), determinate when one is. Add a
  `text.secondary` caption beneath if the wait may exceed ~1 s. **Do NOT use
  skeleton-shimmer**: shimmer is a continuous animation that pays no
  benchmark dividend, conflicts with the light-and-fast principle, and
  costs more under WPF than a static ProgressRing.
- **Disconnected vs query-failed on history surfaces.** Per-App / App
  Detail / History bucket every failure under a single `StatusBanner`. The
  service-status poller already knows when the pipe is disconnected — the
  history pages should distinguish "service is down" from "query failed for
  another reason" with separate copy.
- **Empty-state messaging on placeholder pages.** The Reports/Alerts/Settings
  placeholders look unfinished. After the polish interlude they should
  read as deliberate "coming in Phase 5/6" with a small icon + the same
  typography system as the rest of the app.

---

## 3. Color tokens

Wired in `Resources/DesignTokens.xaml`. Every `SolidColorBrush` token
aliases a Wpf.Ui Color resource via
`Color="{DynamicResource <WpfUiColor>}"`, so:

- Light/Dark theme swap propagates automatically (the underlying Wpf.Ui
  Color resolves through the swapped theme dictionary).
- Brand or token revaluation only needs to touch this file.

| Token                          | Use                                                                                       | Maps to (Wpf.Ui Color)               |
|--------------------------------|-------------------------------------------------------------------------------------------|--------------------------------------|
| `surface.background`           | Page root background. **Semi-transparent (60% alpha brand cool gray / dark slate, `#99F8F9FC` light / `#99191C26` dark)** in WPF runtime, painted ONCE on the MainWindow outer Grid (`Background="{DynamicResource surface.background}"` in `MainWindow.xaml`) so the brand tint reads uniform across the page area AND the chrome (nav pane, title bar, bottom-bar). `ApplicationBackgroundBrush` is direct-overridden to `Transparent` in `App.xaml.cs` so Pages render see-through over the MainWindow tint — no double-paint, no seam at NavigationView's column boundary. CSS keeps the opaque `#f8f9fc` / `#191c26` form since mock viewer doesn't render Mica. | `ApplicationBackgroundColor` (DesignTokens alias, but BrandAccent override + Transparent direct-override at App level take over at runtime) |
| `surface.card`                 | **Opaque** card background — brand-aligned (`#FFFFFF` light / `#FF232735` dark). Cards carry text/data and must stay opaque so contrast is not wallpaper-dependent on Mica. | `SolidBackgroundFillColorBase` (brand-overridden) |
| `surface.card.alt`             | Existing `CardBackgroundFillColorDefaultBrush` (slightly translucent) — only for surfaces where Mica visibility is intended | `CardBackgroundFillColorDefault`     |
| `surface.layer`                | Section grouping above `surface.background`                                               | `LayerFillColorDefault`              |
| `surface.subtle`               | Inline hint/banner background                                                             | `SubtleFillColorSecondary`           |
| `surface.subtle.alt`           | DataGrid alternating row background                                                       | `SubtleFillColorTertiary`            |
| `text.primary`                 | Body text                                                                                 | `TextFillColorPrimary`               |
| `text.secondary`               | Captions, metadata                                                                        | `TextFillColorSecondary`             |
| `text.tertiary`                | De-emphasized text (column headers in cards, breadcrumbs)                                 | `TextFillColorTertiary`              |
| `text.disabled`                | Disabled state                                                                            | `TextFillColorDisabled`              |
| `text.inverse`                 | Light text on dark surface                                                                | `TextFillColorInverse`               |
| `text.on-accent`               | Text painted on an accent fill                                                            | `TextOnAccentFillColorPrimary`       |
| `accent.default`               | Primary interactive accent — **text / borders / focus**. Aliases `SystemAccentColorPrimary`, but `BrandAccent.{Light,Dark}.xaml` overrides that Color to brand violet (violet-600 light / violet-500 dark), so every Wpf.Ui control that paints accent picks up brand violet. | `SystemAccentColorPrimary` (overridden to brand violet by `BrandAccent.xaml`) |
| `accent.secondary`             | Secondary accent (hover/pressed) — same brand-override mechanism                          | `SystemAccentColorSecondary` (brand violet-700 / violet-400) |
| `accent.tertiary`              | Tertiary accent (focused state) — same brand-override mechanism                           | `SystemAccentColorTertiary` (brand violet-800 / violet-600) |
| `accent.fill`                  | **Accent SURFACE** (filled buttons, pills, selection bars) carrying on-accent (white) text. Constant brand violet `#6D3FD1` in BOTH themes — one stop darker than `accent.default` in dark theme so white text clears AA 4.5:1 regardless. **Never use `accent.default` as a filled background.** | constant `#6D3FD1`                   |
| `accent.text`                  | **Foreground accent text** on neutral surface.card (eyebrows, accent-coloured small labels). Theme-swap: violet-700 light / violet-300 dark — a darker (light) or lighter (dark) stop than `accent.default` so 12 px SemiBold clears AA 4.5:1. Used by the `text.eyebrow` Style. | constant `#561FB0` light / `#B294F6` dark via `BrandAccent.xaml` |
| `accent.subtle`                | **Soft brand-violet tint** for selected / hovered accent surfaces (e.g. NavigationView selected-item background). Theme-swap: 10% alpha violet-600 light / 18% alpha violet-500 dark — dark theme is intentionally higher alpha so the tint stays visible against the dark backdrop. CSS source-of-truth: `--accent-subtle`. | constant `#1A6D3FD1` light / `#2E8254E6` dark via `BrandAccent.xaml` |
| `status.success`               | Success foreground. Brand-tuned by `BrandAccent.xaml`: `#06B6A3` light / `#2BD1BD` dark.   | `SystemFillColorSuccess` (overridden) |
| `status.success.background`    | Success banner background. Alpha-tinted brand value via `BrandAccent.xaml`.                | `SystemFillColorSuccessBackground` (overridden) |
| `status.caution`               | Caution foreground — dots / graphics / icon fills. Brand-tuned: `#EC9A0B` light / `#F1AD34` dark. | `SystemFillColorCaution` (overridden) |
| `status.caution.text`          | Caution **text** on the caution-tint background. Darker amber so small body text clears AA on the light tint; bright amber already passes on the dark tint. Theme-aware via `BrandAccent.{Light,Dark}.xaml` — `#8A5A00` light / `#F1AD34` dark. | brand constants (per-theme)         |
| `status.caution.background`    | Caution banner background. Alpha-tinted brand value via `BrandAccent.xaml`.                | `SystemFillColorCautionBackground` (overridden) |
| `status.critical`              | Critical foreground — dots / graphics / pill fills. Brand-tuned: `#D62B62` light / `#F5547F` dark. | `SystemFillColorCritical` (overridden) |
| `status.critical.text`         | Critical **text** on the critical-tint background. Light reuses the brand magenta; dark uses a lighter coral-pink (`#FBA3B7`) so text and the same-hue alpha-tinted background separate cleanly. Theme-aware via `BrandAccent.{Light,Dark}.xaml`. | brand constants (per-theme)         |
| `status.critical.background`   | Error banner background. Alpha-tinted brand value via `BrandAccent.xaml`.                  | `SystemFillColorCriticalBackground` (overridden) |
| `status.neutral`               | Neutral / informational                                                                   | `SystemFillColorNeutral`             |
| `status.neutral.background`    | Neutral banner background                                                                 | `SystemFillColorNeutralBackground`   |
| `status.connected`             | **ZenVizor-specific** — service-status dot when pipe is up                                 | `SystemFillColorSuccess`             |
| `status.warming`               | **ZenVizor-specific** — dot/banner while warming (first flush bucket pending)             | `SystemFillColorCaution`             |
| `status.warming.background`    | **ZenVizor-specific** — warming-banner background. Paint identical to `status.caution.background`; the separate key lets the warming banner be repointed without dragging every caution banner along. | `SystemFillColorCautionBackground`   |
| `status.disconnected`          | **ZenVizor-specific** — dot/banner when pipe is down                                       | `SystemFillColorCritical`            |
| `border.card`                  | Card stroke (use in place of `ControlElevationBorderBrush` for cards)                     | `CardStrokeColorDefault`             |
| `border.subtle`                | Lighter divider stroke                                                                    | `CardStrokeColorDefaultSolid`        |

### Mica + Acrylic strategy

- **Page-area Mica showthrough is on.** The brand tint paints ONCE on
  the MainWindow outer Grid (`Background="{DynamicResource surface.background}"`
  in `MainWindow.xaml`); cards stay opaque, pages render see-through.
  `surface.background` is 60% alpha brand-cool at runtime (light
  `#99F8F9FC` / dark `#99191C26`). `ApplicationBackgroundBrush` is
  direct-overridden to `Transparent` in `App.xaml.cs ApplyDirectLevelOverrides()`
  so Pages don't double-paint the tint on top of the Grid. The
  split-tint-from-Page-Background design lets chrome (nav rail, title
  bar, bottom-bar) and page area share the same backdrop with no seam
  at NavigationView's column boundary. There's also a separate override
  of `NavigationViewContentBackground` (and the matching
  `*GridBorderBrush`) to `Transparent` because Wpf.Ui's
  `LeftNavigationViewTemplate` paints a ~30% gray Border inside the
  content area that would otherwise occlude Mica on the page side. The
  CSS source carries the opaque form (`#f8f9fc` / `#191c26`) since the
  mock viewer has no Mica — that's what the mockup approximates the
  Mica-blended result *to look like*.
- The Mica backdrop is alpha-blended over the desktop. **Anything that
  carries text or data MUST sit on `surface.card` (opaque) rather than
  `surface.card.alt` (translucent)**, or its text contrast becomes
  desktop-wallpaper-dependent and fails WCAG AA in the worst case.
  Today's pages mostly use `CardBackgroundFillColorDefaultBrush` — the
  polish pass should audit and migrate data-carrying surfaces to
  `surface.card`.
- The chart card and the talkers/sessions/connections grids are the
  highest-risk surfaces (largest data area + most text on Mica). Audit
  them first.
- **Acrylic decision — WPF has no per-element backdrop.** Mica/Acrylic
  are window-level (`WindowBackdropType` via DWM). Therefore:
    - The App-Detail flyout is an **opaque `surface.layer` panel**, NOT a
      frosted one. A translucent flyout over Mica makes its text contrast
      wallpaper-dependent and pays a measurable GPU cost.
    - **Real Acrylic is reserved for OS-level surfaces only** — the tray
      icon context menu and any `ContextMenu` Windows draws for us. We
      don't add `backdrop-blur` to anything we paint ourselves.
    - The brand's chrome / brushed-steel surfaces are **static gradients
      + a 1px stroke + a single drop shadow** (LinearGradientBrush +
      Border + DropShadowEffect). No live blur. Composited once, ~free.
- `ControlElevationBorderBrush` is a `LinearGradientBrush` in Wpf.Ui v4
  with no Color counterpart; it can't be aliased via the
  `Color="{DynamicResource …}"` pattern. Cards should switch to
  `border.card` (a tokenizable solid stroke); controls that depend on the
  gradient (buttons, text boxes) continue to use Wpf.Ui's brush directly.

### Aliasing gotchas (theming + high-contrast)

- DynamicResource at the `Color` property level is what makes theme swap
  work. Don't change it to StaticResource — that would snapshot at load.
- This file defines NEW semantic keys; it never overrides a
  `SystemColors.*` key or a default WPF brush in DesignTokens.xaml. That
  said, the brand brushes are hard-overridden, which means Windows High
  Contrast no longer kicks in automatically — see the HC strategy below.

### High Contrast strategy

- Stance: **ship a dedicated High Contrast `ResourceDictionary`**
  (`src/ZenVizor.Ui/Resources/HighContrast.xaml`) that remaps every
  semantic token (`surface.*`, `text.*`, `accent.*`, `status.*`,
  `border.*`, `chart.*`) onto `SystemColors` brushes
  (`SystemColors.WindowColorKey`, `ControlColorKey`, `WindowTextColorKey`,
  `GrayTextColorKey`, `HighlightColorKey`, `WindowFrameColorKey`).
- HC must be **merged on demand** — added to
  `Application.Current.Resources.MergedDictionaries` *after* the regular
  DesignTokens dictionary so its keys win, when
  `SystemParameters.HighContrast` is true. Subscribe to
  `SystemParameters.StaticPropertyChanged` (or
  `SystemEvents.UserPreferenceChanged` with category `Color`) to merge /
  unmerge on the fly as the user flips HC themes from
  *Settings → Accessibility → Contrast themes*.
- HC collapses the granular semantic palette: success / caution /
  critical foregrounds all become `WindowTextBrush`; backgrounds collapse
  to `ControlBrush`; the 8-slot categorical chart palette collapses to
  three distinct values (Highlight / WindowText / GrayText). This is a
  known and documented limit of HC mode — the OS palette is the
  contract.
- The HC ResourceDictionary brush definitions use
  `Color="{DynamicResource {x:Static SystemColors.<Name>ColorKey}}"` so
  the brush instances re-paint when the user switches between Aquatic /
  Desert / Dusk / Night Sky without the app having to rebuild anything.

### Chart-chrome tokens

LiveCharts2 paints **none** of the axis / gridline / label / tooltip /
legend chrome from the UI theme. We must set them explicitly on every
chart. These tokens are read by `ChartBuilder` and fed into SKPaints,
axis label paints, and tooltip styles.

| Token                 | Use                                              | Maps to (Wpf.Ui Color)               |
|-----------------------|--------------------------------------------------|--------------------------------------|
| `chart.axis`          | Axis line stroke                                 | `CardStrokeColorDefaultSolid`        |
| `chart.gridline`      | Gridline stroke (apply lower alpha in code, ~0x0B) | `CardStrokeColorDefault`           |
| `chart.axis.label`    | Axis tick labels                                 | `TextFillColorTertiary`              |
| `chart.tooltip.bg`    | Tooltip surface — OPAQUE so text contrast is stable | `SolidBackgroundFillColorBase`    |
| `chart.tooltip.text`  | Tooltip label text                               | `TextFillColorPrimary`               |
| `chart.legend.text`   | Legend label text                                | `TextFillColorSecondary`             |

All theme-swap (their underlying Wpf.Ui Color aliases do). In HC mode
they collapse to `WindowFrameColorKey` / `GrayTextColorKey` /
`ControlColorKey` / `WindowTextColorKey` per `HighContrast.xaml`.

---

## 4. Data-viz palette (LiveCharts2 / SkiaSharp)

**SkiaSharp paints do NOT inherit Wpf.Ui DynamicResource.** Series colors
must be applied in C# code by reading the brush resource at runtime and
feeding the underlying `Color` into an `SKPaint`. The tokens here are the
contract that wiring code targets.

| Token             | Role                                              | Value                       |
|-------------------|---------------------------------------------------|-----------------------------|
| `chart.upSeries`  | Upload series (line or bottom of stacked column) — **brand violet**, theme-swap via `BrandAccent.xaml` | `#6D3FD1` light (violet-600) / `#9A72F0` dark (violet-400) |
| `chart.downSeries`| Download series (line or top of stacked column) — **brand teal**, theme-swap via `BrandAccent.xaml` | `#20B6C6` light (teal-500) / `#34D0E0` dark (teal-400) |
| `chart.wan`       | WAN-class endpoint segment (fixed Okabe-Ito)      | `#0072B2`                   |
| `chart.local`     | LAN/local-class endpoint segment (fixed Okabe-Ito) | `#009E73`                  |
| `chart.series.1`  | Categorical ramp slot 1 (fixed Okabe-Ito)         | `#0072B2`                   |
| `chart.series.2`  | Categorical ramp slot 2 (fixed Okabe-Ito)         | `#E69F00`                   |
| `chart.series.3`  | Categorical ramp slot 3 (fixed Okabe-Ito)         | `#009E73`                   |
| `chart.series.4`  | Categorical ramp slot 4 (fixed Okabe-Ito)         | `#CC79A7`                   |
| `chart.series.5`  | Categorical ramp slot 5 (fixed Okabe-Ito)         | `#56B4E9`                   |
| `chart.series.6`  | Categorical ramp slot 6 (fixed Okabe-Ito)         | `#D55E00`                   |
| `chart.series.7`  | Categorical ramp slot 7 (fixed Okabe-Ito)         | `#F0E442`                   |
| `chart.series.8`  | Categorical ramp slot 8 (fixed Okabe-Ito)         | `#999999`                   |

The **categorical** palette (`chart.series.1..8`, `chart.wan`,
`chart.local`) uses fixed Okabe-Ito hex (deuteranopia + protanopia
friendly), AA-legible on both Light and Dark Mica without per-theme
swapping.

The **up/down** series are a deliberate **brand deviation**: violet /
teal that theme-swap via `BrandAccent.{Light,Dark}.xaml`. Tradeoff
documented: violet/teal is less colourblind-distinct than blue/orange,
so the categorical palette retains the Okabe-Ito set for multi-series
charts. `DesignTokens.xaml` carries the light-theme values as a fallback
for sessions where the brand dict is unmerged (HC mode); the brand dicts
override with the theme-appropriate value on every Light↔Dark flip.

### How chart paints bind to these tokens (the wiring)

In `Services/ChartBuilder.cs`, replace the implicit default series colors
with explicit `Stroke` / `Fill` `SolidColorPaint`s sourced from the design
tokens:

```csharp
// Sketch — actual wiring is a polish-pass follow-up.
var brush = (SolidColorBrush)Application.Current.Resources["chart.upSeries"];
var c = brush.Color;
var paint = new SolidColorPaint(new SKColor(c.R, c.G, c.B, c.A)) { StrokeThickness = 2 };
upSeries.Stroke = paint;
upSeries.Fill   = new SolidColorPaint(new SKColor(c.R, c.G, c.B, 60));
```

### Light/dark re-theming for charts (MANUAL)

Wpf.Ui re-themes on OS Light/Dark change at runtime (Mica honors the
system theme). SkiaSharp paints set at chart construction time do NOT
re-theme automatically. **A runtime Light↔Dark flip while the app is
running is a real scenario** and must be handled by:

1. Subscribing to Wpf.Ui's theme change notification
   (`ApplicationThemeManager.Changed`).
2. Re-running `ChartBuilder.BuildSeries` (or only the paint reassignments)
   on every visible chart.
3. Calling `chart.UpdateLayout()` or refreshing the `Series` collection so
   LiveCharts2 picks up the new paints.

The data-viz tokens listed above are intentionally chosen to be legible on
**both** Light and Dark Mica without per-theme swapping, so v1 of the
wiring can skip step 1/2 and ship a single palette. If a later iteration
wants per-theme chart palettes, swap the `chart.*` keys via a theme-specific
merged dictionary and follow steps 1-3.

---

## 5. Typography scale

Three font families are present in `fonts/` (probed via
`System.Windows.Media.Fonts.GetFontFamilies`):

| Family       | File(s) on disk                                                                                     | Role token   | Use                                              |
|--------------|-----------------------------------------------------------------------------------------------------|--------------|--------------------------------------------------|
| **Urbanist** | `Urbanist-Light.ttf`, `Urbanist-Regular.ttf`, `Urbanist-SemiBold.ttf`, `Urbanist-Bold.ttf`          | `font.display` | Primary UI — body, headers, captions. **Four real on-disk weights**; use `FontWeight` (Light=300, Regular=400, SemiBold=600, Bold=700) to select. SemiBold is the brand's title/subtitle weight — without `Urbanist-SemiBold.ttf` registered in `ZenVizor.Ui.csproj`, WPF would synthesize the weight from Regular and produce blurry strokes. |
| **Overpass Mono** | `OverpassMono-VariableFont_wght.ttf`                                                           | `font.mono`  | Numeric, paths, hex, code-like text. Variable weight axis (100-900); `text.mono` pins Regular for the canonical look. |
| **Nuqun**    | `Nuqun-Regular.otf`                                                                                 | `font.brand` | Wordmark / decorative only. **Not for body text.** Regular only. |

Wired in `Resources/Fonts.xaml`. Pack URI form:
`pack://application:,,,/Fonts/#<FamilyName>`. The `Fonts/` segment matches
the `Link` attribute on the `<Resource Include="…">` entries in
`ZenVizor.Ui.csproj`.

### Size scale (brand-reconciled)

The brand size scale is **larger** than the Fluent ladder the app shipped
as a placeholder. The crosswalk in `docs/design/colors_and_type.css` records
each delta; the values below are the reconciled brand targets that
`DesignTokens.xaml` now emits.

| Token                  | px | Renders in        |
|------------------------|----|-------------------|
| `font.size.caption`    | 12 | `font.display`    |
| `font.size.body`       | 14 | `font.display`    |
| `font.size.body.large` | 18 | `font.display`    |
| `font.size.subtitle`   | 20 | `font.display`    |
| `font.size.metric`     | 24 | `font.display`    |
| `font.size.title`      | 28 | `font.display`    |
| `font.size.title.large`| 40 | `font.display`    |
| `font.size.display`    | 68 | **`font.brand` (Nuqun)** |

Only `font.size.display` renders in `font.brand` (Nuqun); everything from
`title.large` down renders in `font.display` (Urbanist).

### Weights

| Token                   | Value      | Resolves to                       |
|-------------------------|------------|-----------------------------------|
| `font.weight.regular`   | `Normal`   | `Urbanist-Regular.ttf`            |
| `font.weight.semibold`  | `SemiBold` | `Urbanist-SemiBold.ttf` (real glyphs) |
| `font.weight.bold`      | `Bold`     | `Urbanist-Bold.ttf`               |

### Type ramp Styles (call-site contract)

`DesignTokens.xaml` exports keyed `TextBlock` Styles with **absolute
LineHeight** values computed from the brand line-height ratios. Apply via
`Style="{StaticResource text.subtitle}"` etc. — **prefer these over raw
`FontSize`/`FontFamily`/`FontWeight` triplets at call sites.**

| Style key           | FontFamily      | FontSize | FontWeight | LineHeight |
|---------------------|-----------------|----------|------------|------------|
| `text.display`      | `font.brand`    | 68       | Regular    | 76         |
| `text.title.large`  | `font.display`  | 40       | SemiBold   | 48         |
| `text.title`        | `font.display`  | 28       | SemiBold   | 36         |
| `text.subtitle`     | `font.display`  | 20       | SemiBold   | 28         |
| `text.metric`       | `font.display`  | 24       | SemiBold   | 32         |
| `text.body.large`   | `font.display`  | 18       | Regular    | 27         |
| `text.body.strong`  | `font.display`  | 14       | SemiBold   | 21         |
| `text.body`         | `font.display`  | 14       | Regular    | 21         |
| `text.caption`      | `font.display`  | 12       | Regular    | 17 (Foreground = `text.secondary`) |
| `text.eyebrow`      | `font.display`  | 12       | SemiBold   | 17 (uppercase the source string at call sites; Foreground = `accent.text`) |
| `text.mono`         | `font.mono`     | 14       | Regular    | 21         |

> **Letter-spacing limit.** WPF has no `letter-spacing` / `CharacterSpacing`
> on `TextBlock` / `TextElement`. The brand's `tracking-wide` (`0.04em` on
> eyebrows, `0.05em` on Nuqun display) cannot be applied to a plain
> `TextBlock` without splitting the text into per-character `Run`s — which
> we deliberately don't do for eyebrows. Accepted limitation; visually
> tolerable because eyebrows are small, short, and always uppercase.
> Captured inline in `DesignTokens.xaml` on the `text.eyebrow` Style.

`LineStackingStrategy="BlockLineHeight"` is set on every style so
LineHeight is authoritative — without it, ascender/descender overshoot
pushes lines apart and the rhythm drifts page-to-page.

### Usage rules

- Use **`text.body`** for body text on all pages.
- Use **`text.subtitle`** for card titles (SemiBold 20).
- Use **`text.metric`** for headline metric values on dashboards / status
  cards (SemiBold 24). Pair with `FontFamily="{StaticResource font.mono}"`
  override at the call site when the value is numeric and digit alignment
  matters (rates, byte counts, counts).
- Use **`text.title`** / **`text.title.large`** for page titles.
- Use **`text.mono`** for numeric rate values (Up B/s, Down B/s), byte
  totals, file paths, hex/IP addresses — anywhere column alignment of
  digits matters.
- Use **`text.display`** (Nuqun) only on the wordmark / splash / hero
  moments. Never for body, never for table headers, never for captions.

---

## 6. Spacing scale

4-based scale, named by pixel value. The app set {4,8,12,16,24,32,48} is
a clean subset of the brand scale; brand adds {20,40,64}. Use `space.12`
(not `space.10`) for medium spacing — keeps rhythm.

> ⚠ **DO NOT** write `Margin="{StaticResource space.16}"` or
> `Padding="{StaticResource space.16}"`. The `space.*` tokens are
> `sys:Double` resources (`DesignTokens.xaml:239-248`), and WPF's
> ThicknessConverter only converts from **String** — a boxed Double
> can't be coerced to Thickness and the page fails to load at runtime.
> Write the **literal pixel value** at call sites (`Margin="16"`,
> `Padding="16"`); XAML's string TypeConverter handles it. The token
> values in the table below are the documentation of the canonical
> values — match them by hand at call sites. (CornerRadius has the
> same constraint — write literals there too, never `{StaticResource}`
> a Double into a CornerRadius.) This is captured in user memory as
> `project_wpf_spacing_token_thickness`.

| Token        | px |
|--------------|----|
| `space.4`    | 4  |
| `space.8`    | 8  |
| `space.12`   | 12 |
| `space.16`   | 16 |
| `space.20`   | 20 |
| `space.24`   | 24 |
| `space.32`   | 32 |
| `space.40`   | 40 |
| `space.48`   | 48 |
| `space.64`   | 64 |

### Usage rules

- **24** — page outer margin (matches today's `Margin="24"` on every Page Grid).
- **16** — section gap between major card stacks.
- **12** — gap between a card header and its body.
- **8** — gap between a label and its value, banner padding.
- **4** — gap between a glyph and adjacent text.

---

## 7. Corner radius

Two layers — **scale tokens** (raw pixel steps) and **semantic role
tokens** (intent-named, point at a scale step). Always reference the role
tokens at call sites so future scale tuning doesn't drag the wrong
surface along.

### Scale (brand-reconciled)

| Token         | px   | Notes                                                          |
|---------------|------|----------------------------------------------------------------|
| `radius.xs`   | 4    | Tightest inline elements (matches the app's previous `radius.sm`) |
| `radius.sm`   | 6    | Controls — buttons, inputs, chips, inline banners              |
| `radius.md`   | 10   | Cards, list rows                                               |
| `radius.lg`   | 14   | Overlays, flyouts, dialogs                                     |
| `radius.xl`   | 20   | Large brand / hero surfaces                                    |
| `radius.pill` | 9999 | Fully rounded — sentinel value; at the call site, set `CornerRadius` to half the control height if you want pixel-perfect |

### Semantic roles (use these at call sites)

| Token             | Points at     | Use                                       |
|-------------------|---------------|-------------------------------------------|
| `radius.control`  | `radius.sm`   | Buttons, inputs, chips, inline banners    |
| `radius.card`     | `radius.md`   | Cards, list rows                          |
| `radius.overlay`  | `radius.lg`   | Flyouts, dialogs, popups                  |

### Why split scale from roles

Wpf.Ui ships a single `ControlCornerRadius` resource that some controls
*and* some cards both consume. Migrating cards to 10 px without first
splitting the keys would drag button/input radius along. Keeping the
scale and the roles in separate token layers means:

- If brand later wants cards at 12 px, change **only** `radius.card`'s
  pointer.
- Existing call sites annotated with `radius.card` continue to read
  correctly; controls annotated with `radius.control` stay at 6 px.
- The eventual override of Wpf.Ui's `ControlCornerRadius` in App.xaml
  merged dictionaries points at `radius.control` unambiguously.

The override of Wpf.Ui's `ControlCornerRadius` (so Wpf.Ui buttons / inputs
also pick up brand 6 px instead of stock 4 px) is **deferred** until the
polish-pass call-site sweep, so the visual jump is auditable in one diff.

---

## 8. Density

ZenVizor's data-dense surfaces (Per-App grid, Connections grid, Sessions
grid, Reports' Top Apps grid) feel too airy at default Fluent spacing.
The **compact** variant is the one to apply on those grids.

| Token                    | Value | Use                                |
|--------------------------|-------|------------------------------------|
| `density.row.default`    | 28 px | Standard DataGrid rows             |
| `density.row.compact`    | 32 px | Data-dense DataGrid rows           |

The compact row was originally 22 px; bumped to 32 in the Per-App polish
(June 2026) because 22 was too tight at 14 px body — text crowded the
cell edges and headers had no visible breathing room.

Cell padding is **horizontal-only** (`12,0`, June 2026 descender fix).
Rows are fixed-height, so vertical centering comes from the cell
template's `ContentPresenter`; vertical cell padding only shrinks the
content slot, and once the slot is smaller than the cell TextBlock's
desired height WPF layout-clips the text (ink sheared at the slot edge —
this was the recurring Per-App descender-clipping bug). Cell text styles
pair with this via `LineHeight="Auto"` (natural font metrics, not the
21 px rhythm box); the mono cell style adds `Padding="0,3,0,0"` to align
Overpass Mono's baseline with Urbanist's across a row. See the comments
on `style.datagrid.compact` in `DesignTokens.xaml` and `cell.body.trim`
in `PerAppPage.xaml` for the measured numbers.

A pre-built compact style is exported as `style.datagrid.compact` and sets
`RowHeight`, `MinRowHeight`, `FontSize`, and cell `Padding="12,0"`:

```xml
<DataGrid Style="{StaticResource style.datagrid.compact}" ... />
```

Apply on `AppsGrid` (PerAppPage), `ConnectionsGrid` and `SessionsGrid`
(AppDetailPage). The Reports' `TopAppsGrid` uses the same compact style
but overrides `RowHeight`/`MinRowHeight` to 56 to host the two-line
App-cell layout (image name + mono path on unsigned rows). Do **not**
apply on the `TalkersList` ListView — it already uses 8,4 padding which
sits between `default` and `compact` and reads well at the Dashboard's
larger typographic rhythm.

---

## 9. Component inventory

The recurring visual elements and which tokens they should pull from.

### FluentWindow chrome
- `ui:FluentWindow WindowBackdropType="Mica"` — leave Mica on.
- `ui:TitleBar` — title is the app name. Future polish: consider replacing
  the `Title="ZenVizor"` string with a wordmark using `font.brand`.

### NavigationView rail
- `PaneDisplayMode="Left"`, `OpenPaneLength="220"`.
- Icons are `ui:SymbolIcon` (Fluent System Icons). Keep the existing
  symbols (DataPie24, Apps24, History24, DocumentText24, Alert24,
  Settings24) — they're consistent with the Fluent vocabulary.
- Selected/hover/pressed states inherit from `ui:NavigationView`'s default
  templates; do not restyle without a strong reason.

### Status banner
- `<Border CornerRadius="{StaticResource radius.control}" Padding="8,4">`
  wrapping a `<TextBlock>`. CornerRadius uses the role token because the
  banner is a control-class surface (`radius.control` → 6). Padding and
  Margin are **literals** matching the `space.*` scale (see §6 warning).
- Background: `status.caution.background` (warming / query failure) or
  `status.critical.background` (disconnected); Foreground:
  `status.caution.text` for caution banners (AA-safe on the tint) and
  `status.critical` for critical banners.
- Reference XAML pattern (DashboardPage warming banner):
  ```xml
  <Border Background="{DynamicResource status.warming.background}"
          Padding="8,4"
          CornerRadius="{StaticResource radius.control}">
      <TextBlock Style="{StaticResource text.caption}"
                 Foreground="{DynamicResource status.caution.text}"
                 Text="Warming up. First flush bucket lands within ~5 s." />
  </Border>
  ```
  Note the literal `Padding="8,4"` — the `space.*` tokens are `sys:Double`
  and can't be bound into Thickness (see §6).

### Card surface — canonical treatment

**Every text- and data-bearing card on every page uses the same surface
recipe.** This is a project-wide standing decision, not a per-screen
question. It has been re-litigated in too many UI passes; the
resolution is recorded here so future briefs and mockups default to it
without asking.

```xaml
<Border Background="{DynamicResource metal.card}"
        BorderBrush="{DynamicResource border.card}"
        BorderThickness="1"
        CornerRadius="{StaticResource radius.card}"
        Effect="{DynamicResource shadow.card}">
  <!-- card body -->
</Border>
```

- **Background:** `metal.card` — `LinearGradientBrush` carrying the
  porcelain → cool-steel gradient in light theme and the brushed
  steel + baked-in `edge.light` catch-light in dark theme. `edge.light`
  is a sibling token (also available standalone) but the card-level
  catch-light is composited INTO `metal.card` as a third gradient stop
  at offset 0.015 in dark, so the typical card doesn't need a
  separate inset overlay.
- **Border:** `border.card` 1px stroke. Not the gradient
  `ControlElevationBorderBrush`.
- **Radius:** the semantic `radius.card` role token (10), not the raw
  `radius.md` scale step.
- **Elevation:** `shadow.card` `DropShadowEffect`. Use `shadow.sm` for
  smaller / lighter info-strip cards that sit alongside (not above) a
  primary data card — Per-App's summary strip is the reference.

**Variants:**
- **`metal.control`** is the same family tuned for short surfaces
  (~30-40 px tall controls — ComboBox, refresh button, filter input).
  Per-App's row-1 picker cluster is the reference.
- **`surface.card`** remains the flat opaque solid. Use ONLY where an
  explicit reason rules out the metallic treatment — OS-chrome-class
  surfaces (tray menu, context menu, an HC mode), or a surface inside
  a card where a second card-on-card visual would be confusing.
  *Data-bearing cards on a page do NOT qualify for this exception.*

**Pre-polish state** — `AppDetailPage.xaml` / `HistoryPage.xaml`
shipped with `CardBackgroundFillColorDefaultBrush` +
`ControlElevationBorderBrush`; the polish round migrates them to the
canonical recipe above. Dashboard and Per-App already ship the
canonical recipe (Dashboard polish round 2 + Per-App phases 1-6).

**HC mode** — `HighContrast.xaml` collapses `metal.card` →
`SystemColors.ControlColor`, `border.card` → `WindowFrameColor`,
`edge.light` → `WindowFrameColor`, and `shadow.card` / `shadow.sm` →
inert `DropShadowEffect Opacity="0"`. The recipe degrades cleanly; no
per-screen HC work is needed beyond verifying.

### Summary card (App Detail header)
- Two-line card: line 1 `text.primary`, line 2 `text.secondary`.
- Pattern at `AppDetailPage.xaml:41-52`.

### DataGrid row
- Compact style — `Style="{StaticResource style.datagrid.compact}"`.
- Selected row: leave Wpf.Ui defaults.
- Alternating background: `surface.subtle.alt` (matches current
  `SubtleFillColorTertiaryBrush`).

### Chart card
- Outer `<Border>` per the card spec.
- `<lvc:CartesianChart LegendPosition="Top" MinHeight="180" />`.
- Series styling per `chart.*` tokens via `ChartBuilder` (C# wiring).
- `NoDataOverlay` centered `TextBlock` with `Foreground="{DynamicResource text.secondary}"`
  and `IsHitTestVisible="False"` so it doesn't capture chart input.

### No-data overlay
- Pattern at `AppDetailPage.xaml:72-77` / `HistoryPage.xaml:51-56`.
- Single `TextBlock`, centered, secondary text color, copy = "No traffic
  recorded in this window."
- During polish: consider replacing the bare text with an icon + caption
  pair (using `ui:SymbolIcon` at `space.32` size + caption text below).

### Bottom-bar connection indicator
- `<Border BorderThickness="0,1,0,0" BorderBrush="{DynamicResource border.subtle}"
  Padding="{StaticResource space.12},{StaticResource space.4}">` wrapping
  `<Ellipse>` + `<TextBlock>`.
- Ellipse `Fill` should be `{DynamicResource status.connected}` /
  `status.warming` / `status.disconnected` — replaces the hardcoded
  `Brushes.DarkOrange` / `Brushes.MediumSeaGreen` at
  `MainWindow.xaml.cs:108-114`.

### Tray icon menu
- Standard Win32 `ContextMenu` items: Show ZenVizor / Exit.
- No tokenization needed — uses OS chrome.

---

## 10. Verification gate (design-system polish interlude)

Manual gate the human runs before declaring the polish interlude done.
All steps are user-driven; CI cannot exercise theme/HC paths headlessly.

**Per-page renders (visual audit)**

For each of Dashboard / Per-App / App Detail / History / Reports /
Alerts / Settings, capture a screenshot in all three theme modes:

1. **Light** — `Settings → Personalization → Colors → Choose your mode → Light`. Confirm Mica is light-tinted; text is `text.primary` dark; cards are opaque `surface.card` (not desktop-wallpaper-tinted).
2. **Dark** — `Settings → Personalization → Colors → Choose your mode → Dark`. Confirm Mica is dark-tinted; text inverts; chart series remain legible against the dark backdrop.
3. **High Contrast** — `Settings → Accessibility → Contrast themes → Aquatic` (or Desert / Dusk / Night Sky). Confirm `HighContrast.xaml` merged in: all surfaces collapse to `SystemColors.WindowBrush` / `ControlBrush`; banners read as WindowText on Control; charts collapse to Highlight / WindowText / GrayText series. No fragment of brand violet visible.

For each page in each mode confirm:
- Every text-bearing surface uses `surface.card` (opaque). No card shows desktop-wallpaper bleed-through.
- No `TextBlock` renders black in dark mode (Phase 4 regression class).
- Column headers, banners, breadcrumbs all use their assigned type-ramp Style.
- `ProgressRing` (not skeleton-shimmer) is the loading treatment on Per-App / App Detail / History.

**Runtime theme toggle (re-theming audit)**

With the app open, flip the OS theme without restarting:

1. Open Dashboard, History, and App Detail in turn — let each render once.
2. `Win+I → Personalization → Colors → toggle Light↔Dark`.
3. Confirm: window backdrop, surfaces, text, accent dots, status banners all repaint. **Charts re-theme** — series colors update, axis/gridline/label/legend chrome updates. Confirm `ChartBuilder` is subscribed to `ApplicationThemeManager.Changed` and rebuilds paints.
4. Flip into Contrast themes and back out. Confirm `HighContrast.xaml` merges / un-merges and the visual switches cleanly without an app restart.

**Contrast audit (WCAG AA)**

Use a contrast checker (e.g. Stark, or Edge DevTools accessibility tab against a screenshot) on:

- **`accent.fill` surfaces with on-accent (white) text** — every filled accent button, pill, selection indicator. Must clear **4.5:1** in BOTH Light and Dark themes. (Why this is specifically called out: dark theme's `accent.default` is one stop lighter than `accent.fill`; mistakenly using `accent.default` as a filled background fails AA against white in dark mode. This audit catches that.)
- **Caution-tint surfaces with `status.caution.text`** — every warning banner. Must clear **4.5:1** in light theme on the caution background; **3:1** for large text. In dark theme, bright `status.caution` reused as text against the dark tint must clear 4.5:1.
- **Critical-tint surfaces with `status.critical.text`** — every error/disconnect banner foreground + glyph + alert severity strip text. Must clear **4.5:1** in light theme on `status.critical.background`; **3:1** for large text. In dark theme, the brighter pink (`#FBA3B7`) reused as text against the dark `#2EF5547F` tint must clear 4.5:1.
- **Chart axis labels** (`chart.axis.label` = `text.tertiary`) on `surface.card` — confirm at least 3:1 (large text); axis labels are subdued by design but must not vanish.

Fail this gate if any audited surface falls below threshold; raise the
token value, do not relax the threshold.

---

## 11. Outstanding gaps (queue for polish work)

Not all of these are part of the design-system tokens themselves; some are
mockup-driven follow-up. Track here so they don't drop on the floor.

> **Polish interlude landed (June 2026):** item 1 (status-dot tokenization in
> `MainWindow.xaml.cs`), item 7 (Dashboard data-card opacity audit; cards
> now sit on `surface.card`), and the **brand-dict migration** —
> previously implicit in the "v1 / brand target" deferrals, now scoped as a
> dedicated sub-phase. Brand violet replaces the OS accent for every Wpf.Ui
> accent surface (NavigationView selection, focus rings, primary buttons);
> status colors are brand-tuned; `chart.upSeries` / `chart.downSeries` now
> theme-swap. Wiring: `Resources/BrandAccent.{Light,Dark}.xaml`, swapped on
> `ApplicationThemeManager.Changed` in `App.xaml.cs`. Items 2, 3 (chart
> paint wiring + theme re-paint) consume the brand dict and land in the
> Phase D chart-wiring round. The rest stay pending.
>
> **Dashboard polish round 2 (June 2026):** full Mica showthrough across
> page + chrome — 60% alpha `surface.background` painted ONCE on the
> MainWindow outer Grid, with `ApplicationBackgroundBrush` +
> `NavigationViewContentBackground` direct-overridden to `Transparent`
> (App.xaml.cs) so chrome and page area share one backdrop with no seam.
> Cards migrated to new `metal.card` (`LinearGradientBrush` with baked-in
> `edge.light` catch-light per theme) plus new `shadow.card`
> (`DropShadowEffect`). NavigationView selection: vertical violet
> fade-out gradient (`NavigationViewItemBackgroundSelected` as
> `LinearGradientBrush`, 18%→7% alpha per CSS spec), brand-violet
> selected icon via `accent.text` (per-icon `DataTrigger` in
> NavigationView.Resources — LeftCompact template intercepts plain
> Foreground inheritance), `text.primary` selected label with SemiBold,
> 20px icon size, 12px left indent. Talkers Up/Dn rate values colored
> by `chart.upSeries` / `chart.downSeries`. Latent bug fix: dark
> `text.on-accent` was black, now white per CSS spec.
>
> **Phase D — Dashboard chart wiring (June 2026):** chart series
> strokes + 24% alpha area fills wired to `chart.upSeries` /
> `chart.downSeries` via `ChartTheming.Apply` (re-applied on
> `ChartTheming.Changed` for runtime theme flip). Y-axis: `MinLimit=0`,
> asymmetric EWMA on the upper bound (jump up immediately on spike,
> α=0.3 decay on the way down), binary-aware nice-value rounding
> (`{1, 2, 5} × 10ⁿ × 1024ᵏ`) so the 1024-based `RateFormatter`
> produces clean labels like `5 KB/s` instead of `4.9 KB/s`,
> `MinStep = niceUpper/4`. X-axis: text labels suppressed, gridlines
> at 10s intervals (`MinStep = TimeSpan.FromSeconds(10).Ticks`), with
> a static positional WPF overlay painting `-2m / -90s / -1m / -30s /
> now` at fixed offsets via an 8-column Grid with ColumnSpan-2 on the
> middle three. Fixed-window scrolling X-axis (`MinLimit/MaxLimit`
> anchored to newest data point's timestamp ±120s) so the static
> overlay labels stay accurate during the first 2 minutes of uptime —
> data accumulates right-to-left rather than stretching across the
> full chart width. `DrawMargin(80, 10, 10, 44)` reserves the plot
> area away from Y labels (Left=80) and gives vertical room under
> the lowest Y label for the X overlay row (Bottom=44). Tooltip:
> opaque `chart.tooltip.bg` background with `DropShadow(0, 4, 8, 8,
> 38% black)` for backdrop separation, `chart.tooltip.text`,
> `FindingStrategy.CompareOnlyXTakeClosest` for X-snap, 20px hover
> zone via `GeometrySize` with `GeometryFill = GeometryStroke = null`
> so the lines stay marker-free, dual-time header
> (`-90s · 14:31:55` / `now · 14:33:25`), per-series value rows.
> Resolves §11 backlog items 2 and 3.
>
> **Phase D.7 — smooth-scroll animation (June 2026, GATED OFF):**
> implemented behind `EnableChartSmoothScroll` static readonly flag in
> `DashboardPage.xaml.cs`. When enabled: `RatesChart.AnimationsSpeed
> = 2200ms` (slightly over the 2s tick cadence so each tween chains
> into the next without a stationary "done" state) +
> `EasingFunction = EasingFunctions.Lineal`. Visual result: continuous
> chained line motion, ~200ms imperceptible lag behind real-time.
> Disabled by default because the animation pays ~8% idle CPU (over
> the project's <1% budget). Flag graduates to a user-facing Settings
> toggle when that page is built (§11 backlog item — placeholder
> page today). HC token coverage audit completed alongside D.7 — new
> polish round 2 tokens (`metal.card`, `edge.light`, `shadow.card`)
> added to `HighContrast.xaml`; the full token list is HC-complete,
> only the runtime merge wiring (§11 item 9) remains for HC mode to
> activate end-to-end.
>
> **Brand assets landed (June 2026):** `src/ZenVizor.Ui/Assets/favicon.ico`
> powers the `.exe` icon (`<ApplicationIcon>` in csproj), the FluentWindow
> chrome icon, and the TaskbarIcon. The TitleBar gets a brand lockup in
> `TitleBar.Header` — `Title` is string-only despite its `object` C# type
> (the control template binds it as `TextBlock.Text` and throws
> `ArgumentException` if given a UIElement); `Header` is the documented
> rich-content slot for left-side custom content. Header hosts a
> horizontal `StackPanel`: 16×16 `Image` rendering the `.ico` with
> `RenderOptions.BitmapScalingMode="HighQuality"`, then a `Viewbox`-scaled
> wordmark reconstructed inline in XAML. The wordmark reproduces the
> Illustrator source (`Assets/zv_wordmark_v1.svg`) using the embedded
> Nuqun font with the SVG's exact per-letter `Canvas.Left` positioning —
> the SVG fixed Nuqun's default letter-spacing issues via manual kerning,
> and replicating those positions is the whole point of building the
> wordmark from positioned `TextBlock`s rather than a single TextBlock.
> The "v" sits at `Canvas.Top="-21"` (FontSize 130 against the others'
> 102.21, with a 2 px baseline drop matching the SVG), and the "i" gets
> `ScaleTransform ScaleY="0.82"` (with `RenderTransformOrigin="0,1"` so
> it compresses from the baseline) to preserve the SVG's vertical
> compression. Foreground binds to `text.primary` so the wordmark
> theme-swaps with the chrome. The logomark `Image` gets
> `Margin="0,-8,0,0"` — the wordmark Canvas's declared 116.89 height
> doesn't reflect the v's `Canvas.Top="-21"` overflow, so visual content
> biases toward the top of the Viewbox and a `VerticalAlignment="Center"`
> logomark would otherwise sit visibly low. `Window.Title="ZenVizor"` on
> FluentWindow still serves alt-tab / accessibility. SVG sources for the
> logomark (light + dark variants) live at top-level `assets/` for future
> use (crisper chrome rendering at non-standard sizes, splash screen,
> About dialog). Resolves §11 backlog item 8.

1. **Hardcoded ellipse colors in MainWindow.xaml.cs** — replace with
   `status.connected` / `.disconnected` tokens (also
   `MainWindow.xaml.cs:172,178`: `Brushes.MediumSeaGreen` /
   `Brushes.DarkOrange` need to become `status.connected` /
   `status.disconnected` reads).
2. **Chart paint wiring** — feed `chart.*` tokens into `ChartBuilder`
   `SKPaint`s. Include the chart-chrome tokens (`chart.axis`,
   `chart.gridline`, `chart.axis.label`, `chart.tooltip.bg`,
   `chart.tooltip.text`, `chart.legend.text`) — LiveCharts2 paints none
   of them from the theme.
3. **Theme-change re-theming for charts** — subscribe to
   `ApplicationThemeManager.Changed` and rebuild chart paints on flip.
   Required for the verification-gate runtime theme toggle to pass.
4. **Loading state on Per-App / History first paint** — centered
   `ProgressRing` (NOT skeleton-shimmer; see §2 polish rule) replacing
   the wait-cursor + flicker of empty grid/chart.
5. **Disconnected vs query-failed copy split** on history pages.
6. **Placeholder-page polish** for Reports/Alerts/Settings — read as
   deliberate "coming in Phase 5/6", not unfinished.
7. **Data-carrying cards on Mica** — audit and migrate from
   `surface.card.alt` (translucent) to `surface.card` (opaque) where
   text legibility matters.
8. **Wordmark in TitleBar** using `font.brand` (Nuqun) /
   `text.display` style.
9. **HC merge wiring** — subscribe to
   `SystemParameters.StaticPropertyChanged` (or
   `SystemEvents.UserPreferenceChanged` category Color) in `App.xaml.cs`
   and merge/unmerge `Resources/HighContrast.xaml` as
   `SystemParameters.HighContrast` flips. Without this, the HC tokens
   file ships but never activates.
10. **Override Wpf.Ui `ControlCornerRadius`** to point at `radius.control`
    so Wpf.Ui's own buttons / inputs adopt the brand 6 px (currently 4 px).
    Apply during the polish-pass call-site sweep so the visual jump is
    auditable in one diff.
11. **Native reconciliation — type-ramp vs. compact DataGrid rows.** The
    reconciled `font.size.subtitle` (20) overflows the 22 px compact row.
    Keep grid column headers at `text.body.strong` (14, SemiBold) or bump
    `density.row.compact` to ~30 for any grid that needs a larger header.
