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
- Chart card: `<Border CornerRadius="6">` wrapping `lvc:CartesianChart x:Name="RatesChart"`,
  two `LineSeries<DateTimePoint>` (Up B/s, Down B/s), 60-point trailing
  window, 2s poll cadence.
- Talkers card: header strip + `ListView x:Name="TalkersList"` (top 10
  by total bytes). Card uses `CardBackgroundFillColorDefaultBrush`.

### `Views/PerAppPage.xaml` — apps over a window

- 3-row Grid: header / picker row / DataGrid card.
- Window preset combo (1h/24h/7d/30d/90d, default 24h) + Refresh button.
- `DataGrid x:Name="AppsGrid"` with virtualization on; `MaxHeight`
  enforced in code via `EnforceAppsGridBound` (`PerAppPage.xaml.cs:43`)
  because `ui:NavigationView` wraps pages in a `DynamicScrollViewer` that
  gives infinite vertical extent. Double-click navigates to App Detail.
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

### `Views/PlaceholderPage.xaml` + `AlertsPage.cs` / `ReportsPage.cs` / `SettingsPage.cs`

- Centered `ui:TextBlock FontTypography="TitleLarge"` title + secondary
  subtitle. Three subclasses populate title/subtitle constructor args.
- Subjects: Alerts → Phase 6 alert feed; Reports → Phase 5 daily reports;
  Settings → Phase 6 autostart/retention.
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
| `surface.background`           | Page root background (sits under Mica)                                                    | `ApplicationBackgroundColor`         |
| `surface.card`                 | **Opaque** card background. Use for cards that carry text/data over Mica — avoids Mica showing through behind text (WCAG AA legibility). | `SolidBackgroundFillColorBase`       |
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
| `accent.default`               | Primary interactive accent — **text / borders / focus** (matches OS accent today; brand target is constant violet) | `SystemAccentColorPrimary`           |
| `accent.secondary`             | Secondary accent (hover/pressed)                                                          | `SystemAccentColorSecondary`         |
| `accent.tertiary`              | Tertiary accent (focused state)                                                           | `SystemAccentColorTertiary`          |
| `accent.fill`                  | **Accent SURFACE** (filled buttons, pills, selection bars) carrying on-accent (white) text. Constant brand violet `#6D3FD1` in BOTH themes — one stop darker than `accent.default` in dark theme so white text clears AA 4.5:1 regardless. **Never use `accent.default` as a filled background.** | constant `#6D3FD1`                   |
| `status.success`               | Success foreground                                                                        | `SystemFillColorSuccess`             |
| `status.success.background`    | Success banner background                                                                 | `SystemFillColorSuccessBackground`   |
| `status.caution`               | Caution foreground — dots / graphics / icon fills                                         | `SystemFillColorCaution`             |
| `status.caution.text`          | Caution **text** on the caution-tint background. Darker amber so small body text clears AA on the light tint; bright amber already passes on the dark tint. v1 ships the light value as a constant. | constant `#8A5A00`                   |
| `status.caution.background`    | Caution banner background                                                                 | `SystemFillColorCautionBackground`   |
| `status.critical`              | Error foreground                                                                          | `SystemFillColorCritical`            |
| `status.critical.background`   | Error banner background                                                                   | `SystemFillColorCriticalBackground`  |
| `status.neutral`               | Neutral / informational                                                                   | `SystemFillColorNeutral`             |
| `status.neutral.background`    | Neutral banner background                                                                 | `SystemFillColorNeutralBackground`   |
| `status.connected`             | **ZenVizor-specific** — service-status dot when pipe is up                                 | `SystemFillColorSuccess`             |
| `status.warming`               | **ZenVizor-specific** — dot/banner while warming (first flush bucket pending)             | `SystemFillColorCaution`             |
| `status.disconnected`          | **ZenVizor-specific** — dot/banner when pipe is down                                       | `SystemFillColorCritical`            |
| `border.card`                  | Card stroke (use in place of `ControlElevationBorderBrush` for cards)                     | `CardStrokeColorDefault`             |
| `border.subtle`                | Lighter divider stroke                                                                    | `CardStrokeColorDefaultSolid`        |

### Mica + Acrylic strategy

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
| `chart.upSeries`  | Upload series (line or bottom of stacked column) — **brand violet**, theme-swaps in target state | `#6D3FD1` v1; brand target `#6D3FD1` light / `#9A72F0` dark |
| `chart.downSeries`| Download series (line or top of stacked column) — **brand teal**, theme-swaps in target state | `#20B6C6` v1; brand target `#20B6C6` light / `#34D0E0` dark |
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
teal that theme-swap. Tradeoff documented: violet/teal is less
colourblind-distinct than blue/orange, so the categorical palette retains
the Okabe-Ito set for multi-series charts. v1 carries the light-theme
brand values as constants; the theme-swap wiring requires the brand-dict
refactor and is tracked under "Native reconciliation" below.

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
| **NF Code**  | `NFCode-Regular.otf`                                                                                | `font.mono`  | Numeric, paths, hex, code-like text. Regular only — `FontWeight` is a no-op. |
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
| `text.body.large`   | `font.display`  | 18       | Regular    | 27         |
| `text.body.strong`  | `font.display`  | 14       | SemiBold   | 21         |
| `text.body`         | `font.display`  | 14       | Regular    | 21         |
| `text.caption`      | `font.display`  | 12       | Regular    | 17 (Foreground = `text.secondary`) |
| `text.eyebrow`      | `font.display`  | 12       | SemiBold   | 17 (uppercase the source string; CharacterSpacing=40; Foreground = accent) |
| `text.mono`         | `font.mono`     | 14       | Regular    | 21         |

`LineStackingStrategy="BlockLineHeight"` is set on every style so
LineHeight is authoritative — without it, ascender/descender overshoot
pushes lines apart and the rhythm drifts page-to-page.

### Usage rules

- Use **`text.body`** for body text on all pages.
- Use **`text.subtitle`** for card titles (SemiBold 20).
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
(not `space.10`) for medium spacing — keeps rhythm. Tokens are `Double`
resources so they slot directly into `Margin`/`Padding` via
`{StaticResource space.16}`.

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
grid) feel too airy at default Fluent spacing. The **compact** variant
is the one to apply on those grids.

| Token                    | Value | Use                                |
|--------------------------|-------|------------------------------------|
| `density.row.default`    | 28 px | Standard DataGrid rows             |
| `density.row.compact`    | 22 px | Data-dense DataGrid rows           |

A pre-built compact style is exported as `style.datagrid.compact` and sets
`RowHeight`, `MinRowHeight`, `FontSize`, and cell `Padding="6,2"`:

```xml
<DataGrid Style="{StaticResource style.datagrid.compact}" ... />
```

Apply on `AppsGrid` (PerAppPage), `ConnectionsGrid` and `SessionsGrid`
(AppDetailPage). Do **not** apply on the `TalkersList` ListView — it
already uses 8,4 padding (`DashboardPage.xaml:75`) which sits between
`default` and `compact` and reads well at the Dashboard's larger
typographic rhythm.

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
- `<Border CornerRadius="{StaticResource radius.sm}" Padding="{StaticResource space.8}">`
  wrapping a `<TextBlock>`.
- Background: `status.caution.background` (warming / query failure) or
  `status.critical.background` (disconnected); Foreground:
  `status.caution` / `status.critical`.
- Current XAML pattern (DashboardPage warming banner):
  ```xml
  <Border Background="{DynamicResource SubtleFillColorSecondaryBrush}"
          Padding="8,4" CornerRadius="4">
      <TextBlock Foreground="{DynamicResource TextFillColorSecondaryBrush}"
                 Text="warming up — first flush bucket lands within ~5 s" />
  </Border>
  ```
  After tokenization the literal `SubtleFillColorSecondaryBrush` →
  `surface.subtle`, `TextFillColorSecondaryBrush` → `text.secondary`,
  `4` → `{StaticResource radius.sm}`, `8,4` → `{StaticResource space.8},{StaticResource space.4}`.

### Card border + surface
- Standard: `<Border BorderThickness="1" CornerRadius="{StaticResource radius.card}"
  Background="{DynamicResource surface.card}" BorderBrush="{DynamicResource border.card}">`.
- Today uses `CardBackgroundFillColorDefaultBrush` + `ControlElevationBorderBrush` — both
  swap to the tokens above during the polish pass. Use the semantic
  **`radius.card`** role token, not the raw `radius.md` scale step.

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
- **Chart axis labels** (`chart.axis.label` = `text.tertiary`) on `surface.card` — confirm at least 3:1 (large text); axis labels are subdued by design but must not vanish.

Fail this gate if any audited surface falls below threshold; raise the
token value, do not relax the threshold.

---

## 11. Outstanding gaps (queue for polish work)

Not all of these are part of the design-system tokens themselves; some are
mockup-driven follow-up. Track here so they don't drop on the floor.

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
