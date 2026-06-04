# ZenVizor design system

Single source of truth for ZenVizor's visual language. Co-evolves with
`src/ZenVizor.Ui/Resources/Fonts.xaml` and `Resources/DesignTokens.xaml` —
those two files implement the tokens documented here.

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
  before `RefreshAsync` resolves shows an empty grid/chart with no cue. A
  brief skeleton/shimmer (or a "Loading…" caption in the chart card) makes
  the first paint feel intentional.
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
| `accent.default`               | Primary interactive accent (matches OS accent)                                            | `SystemAccentColorPrimary`           |
| `accent.secondary`             | Secondary accent (hover/pressed)                                                          | `SystemAccentColorSecondary`         |
| `accent.tertiary`              | Tertiary accent (focused state)                                                           | `SystemAccentColorTertiary`          |
| `status.success`               | Success foreground                                                                        | `SystemFillColorSuccess`             |
| `status.success.background`    | Success banner background                                                                 | `SystemFillColorSuccessBackground`   |
| `status.caution`               | Caution foreground                                                                        | `SystemFillColorCaution`             |
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

### Mica + contrast notes

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
- `ControlElevationBorderBrush` is a `LinearGradientBrush` in Wpf.Ui v4
  with no Color counterpart; it can't be aliased via the
  `Color="{DynamicResource …}"` pattern. Cards should switch to
  `border.card` (a tokenizable solid stroke); controls that depend on the
  gradient (buttons, text boxes) continue to use Wpf.Ui's brush directly.

### Aliasing gotchas (theming + high-contrast)

- DynamicResource at the `Color` property level is what makes theme swap
  work. Don't change it to StaticResource — that would snapshot at load.
- This file defines NEW semantic keys; it never overrides a
  `SystemColors.*` key or a default WPF brush. Windows high-contrast
  behavior is therefore unchanged from baseline Wpf.Ui.

---

## 4. Data-viz palette (LiveCharts2 / SkiaSharp)

**SkiaSharp paints do NOT inherit Wpf.Ui DynamicResource.** Series colors
must be applied in C# code by reading the brush resource at runtime and
feeding the underlying `Color` into an `SKPaint`. The tokens here are the
contract that wiring code targets.

| Token             | Role                                              | Hex       |
|-------------------|---------------------------------------------------|-----------|
| `chart.upSeries`  | Upload series (line or bottom of stacked column)  | `#56B4E9` |
| `chart.downSeries`| Download series (line or top of stacked column)   | `#E69F00` |
| `chart.wan`       | WAN-class endpoint segment                        | `#0072B2` |
| `chart.local`     | LAN/local-class endpoint segment                  | `#009E73` |
| `chart.series.1`  | Categorical ramp slot 1                           | `#0072B2` |
| `chart.series.2`  | Categorical ramp slot 2                           | `#E69F00` |
| `chart.series.3`  | Categorical ramp slot 3                           | `#009E73` |
| `chart.series.4`  | Categorical ramp slot 4                           | `#CC79A7` |
| `chart.series.5`  | Categorical ramp slot 5                           | `#56B4E9` |
| `chart.series.6`  | Categorical ramp slot 6                           | `#D55E00` |
| `chart.series.7`  | Categorical ramp slot 7                           | `#F0E442` |
| `chart.series.8`  | Categorical ramp slot 8                           | `#999999` |

Values are from the Okabe-Ito colorblind-safe palette (deuteranopia +
protanopia friendly), AA-legible on both Light and Dark Mica.

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

| Family       | File(s) on disk                                                         | Role token   | Use                                              |
|--------------|-------------------------------------------------------------------------|--------------|--------------------------------------------------|
| **Urbanist** | `Urbanist-Light.ttf`, `Urbanist-Regular.ttf`, `Urbanist-Bold.ttf`       | `font.display` | Primary UI — body, headers, captions. Three weights available; use `FontWeight` (Light=300, Regular=400, Bold=700) to select. |
| **NF Code**  | `NFCode-Regular.otf`                                                    | `font.mono`  | Numeric, paths, hex, code-like text. Regular only — `FontWeight` is a no-op. |
| **Nuqun**    | `Nuqun-Regular.otf`                                                     | `font.brand` | Wordmark / decorative only. **Not for body text.** Regular only. |

Wired in `Resources/Fonts.xaml`. Pack URI form:
`pack://application:,,,/Fonts/#<FamilyName>`. The `Fonts/` segment matches
the `Link` attribute on the `<Resource Include="…">` entries in
`ZenVizor.Ui.csproj`.

### Size scale

Mirrors Wpf.Ui's `FontTypography` enum so swapping
`ui:TextBlock FontTypography="…"` for explicit `FontSize`/`FontFamily` is
size-equivalent.

| Token                  | px |
|------------------------|----|
| `font.size.caption`    | 12 |
| `font.size.body`       | 14 |
| `font.size.subtitle`   | 16 |
| `font.size.title`      | 20 |
| `font.size.title.large`| 24 |
| `font.size.display`    | 32 |

### Weights

| Token                   | Value      |
|-------------------------|------------|
| `font.weight.regular`   | `Normal`   |
| `font.weight.semibold`  | `SemiBold` |
| `font.weight.bold`      | `Bold`     |

### Usage rules

- Use **`font.display` Regular 14** for body text on all pages.
- Use **`font.display` SemiBold 16/20** for card titles and page subtitles.
- Use **`font.mono` Regular 14** for numeric rate values (Up B/s, Down B/s),
  byte totals, file paths, hex/IP addresses — anything where column
  alignment of digits matters.
- Use **`font.brand`** only on the title bar wordmark / splash. Never for
  body, never for headers, never for captions.

---

## 6. Spacing scale

4-based scale. Use `space.12` (not `space.10`) for medium spacing — keeps
rhythm. Tokens are `Double` resources so they slot directly into
`Margin`/`Padding` via `{StaticResource space.16}`.

| Token        | px |
|--------------|----|
| `space.4`    | 4  |
| `space.8`    | 8  |
| `space.12`   | 12 |
| `space.16`   | 16 |
| `space.24`   | 24 |
| `space.32`   | 32 |
| `space.48`   | 48 |

### Usage rules

- **24** — page outer margin (matches today's `Margin="24"` on every Page Grid).
- **16** — section gap between major card stacks.
- **12** — gap between a card header and its body.
- **8** — gap between a label and its value, banner padding.
- **4** — gap between a glyph and adjacent text.

---

## 7. Corner radius

| Token        | px | Use                                       |
|--------------|----|-------------------------------------------|
| `radius.sm`  | 4  | Inline banners, chips                     |
| `radius.md`  | 6  | Cards (matches today's `CornerRadius="6"` — keeps the swap invisible) |
| `radius.lg`  | 8  | Large surfaces (Reports cards, dialogs)   |

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
- Standard: `<Border BorderThickness="1" CornerRadius="{StaticResource radius.md}"
  Background="{DynamicResource surface.card}" BorderBrush="{DynamicResource border.card}">`.
- Today uses `CardBackgroundFillColorDefaultBrush` + `ControlElevationBorderBrush` — both
  swap to the tokens above during the polish pass.

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

## 10. Outstanding gaps (queue for polish work)

Not all of these are part of the design-system tokens themselves; some are
mockup-driven follow-up. Track here so they don't drop on the floor.

1. **Hardcoded ellipse colors in MainWindow.xaml.cs** — replace with
   `status.connected` / `.disconnected` tokens.
2. **Chart paint wiring** — feed `chart.*` tokens into `ChartBuilder`
   `SKPaint`s.
3. **Theme-change re-theming for charts** — subscribe to
   `ApplicationThemeManager.Changed` and rebuild chart paints on flip.
4. **Loading state on Per-App / History first paint** — skeleton or
   caption instead of a flicker of empty grid/chart.
5. **Disconnected vs query-failed copy split** on history pages.
6. **Placeholder-page polish** for Reports/Alerts/Settings — read as
   deliberate "coming in Phase 5/6", not unfinished.
7. **Data-carrying cards on Mica** — audit and migrate from
   `surface.card.alt` (translucent) to `surface.card` (opaque) where
   text legibility matters.
8. **Wordmark in TitleBar** using `font.brand` (Nuqun).
