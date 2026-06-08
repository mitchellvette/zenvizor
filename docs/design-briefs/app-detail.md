# Claude Design brief — App Detail

ZenVizor's App Detail screen. Self-contained brief for a Claude Design
session whose prior pass already loaded `docs/claude-design-primer.md`
and aligned to ZenVizor's token surface. Paste this brief ALONE; do not
re-paste the primer. The mockup hand-off contract is in §19.

---

## 1. Screen identity

- **Screen name:** App Detail.
- **XAML file:** `src/ZenVizor.Ui/Views/AppDetailPage.xaml` (+
  `AppDetailPage.xaml.cs`).
- **IA placement:** **not** a nav-rail item. Reached from Per-App by
  double-clicking a row — `NavigationView.Navigate(typeof(AppDetailPage),
  row.AppId)`. The back affordance returns to Per-App via
  `NavigationView.GoBack()`. The user only ever lands here from Per-App.
- **Purpose (casual voice):** "everything ZenVizor knows about this one
  app — who it is, where it lives, when it ran, what it talked to, and
  how much network it used."

---

## 2. UX intent

App Detail is **the deepest drill state in the application** — the
last screen the user sees when they want to know everything about a
single app. This polish round upgrades it from "functional dense
data screen" to "scannable, comprehensible drill destination":
restructured app-identity card that reads at a glance instead of two
pipe-separated paragraphs, the user-writable signal lifted out of
footnote-text so its alert-importance is visible AND framed so a
casual user understands what it means and when it matters,
guaranteed full-path visibility (no truncation, ever, on this
screen), AppId surfaced as a labeled record-identifier rather than
shoved into the page title, a Fluent back affordance whose
relationship to the page title is clearer than the current
shoulder-to-shoulder row, grain-adaptive chart axes that drop the
"/bucket" jargon and add a day-boundary cue for multi-day windows,
explicit empty-state copy on the chart AND on each grid, the
existing-but-unbound WAN/Local classification surfaced on
Connections rows, a centered `ProgressRing` for loading, and the
canonical history-class split between `disconnected` (pipe down)
and `error` (query failed). Cards and tooltip migrate to opaque
surfaces so text legibility on Mica is no longer
wallpaper-dependent. The presentation of the Connections and Recent
Sessions grids — density, row contrast, mono digits, responsive
collapse, and crucially how to make their columns comprehensible to
a non-sysadmin user — is intentionally left as an iterative design
exploration rather than a prescribed layout (see §8.4).

---

## 3. Controls in scope

The page is a `ui:NavigationView`-hosted Page. The brief describes
controls by **type and purpose**; composition (layout rows, widths,
inter-control spacing, card arrangement) is Claude Design's work.

### Navigation / header surface

- **Back affordance.** `ui:Button` carrying
  `ui:SymbolIcon Symbol="ChevronLeft20"` + text `"Per-App"`. Returns
  to Per-App via `NavigationView.GoBack()`. The ASCII chevron in the
  current XAML (`"< Per-App"`) is gone. Composition of where the back
  affordance sits relative to the page title is OPEN (§8.4).
- **Page identity.** `ui:TextBlock` carrying the app's image name
  (e.g. `"chrome.exe"`, `"svchost.exe"`,
  `"NetworkServiceLikeAReallyLongName.exe"`). NOT the page word
  "App detail" — the image name IS the page identity. Long names get
  `MaxWidth` + `TextTrimming="CharacterEllipsis"` so they don't push
  the rest of the row.
- **AppId rendering.** The numeric `AppId` must be visible somewhere
  on the page, **labeled** so the user knows it's a record identifier
  (so a support contact can read it back). It must NOT live inline
  in the page-identity TextBlock; it must NOT live in a tooltip on
  the title block. Where and how it surfaces — labeled chip in the
  summary card, footer-style metadata line, copy-to-clipboard cell,
  something else — is OPEN (§8.4).

### Window picker + status row

- **Window picker.** `ComboBox` over `WindowPreset.All`
  (`HistoryQueryClient.cs:117-126`) — five presets:
  `Last 1 hour / Last 24 hours / Last 7 days / Last 30 days /
  Last 90 days`. Default selection is index 1 (Last 24 hours).
  Items render with the **shorthand label**
  `1h / 24h / 7d / 30d / 90d` (Per-App's locked convention —
  `WindowPreset` already carries the `Short` field). Each item AND
  the ComboBox selection display carries a WPF `ToolTip` with the
  long form. Picker width follows from the shorthand rendering.
- **Status banner.** A `Border` that paints either a
  disconnected-state banner (pipe down) or an error-state banner
  (any other query failure) — see §4 for the per-state copy and
  the §11.3 disconnected-vs-error split. Default `Visibility=Collapsed`.

### Summary card (app identity block)

A `Border` carrying the at-a-glance identity for the app. The
card MUST carry these data points; how they compose is OPEN (§8.4):

- **Publisher** (`AppDetailSummary.Publisher`, `"(unknown)"` when
  null).
- **Signature status** (`SignatureStatus` enum: `Signed`,
  `Unsigned`, `Invalid`, `Unchecked`).
- **User-writable-path signal** — conditional on
  `IsUserWritablePath == true`. Composition is OPEN (§8.4) but the
  signal MUST be visually elevated when it appears in the
  alert combination *unsigned + user-writable* (see §8.2 lock).
- **Image path** (`AppDetailSummary.ImagePath`). MUST render in
  monospace (`Style="text.mono"`) and MUST NEVER be truncated on
  this screen (§8.2 lock).
- **Grain used** (`detail.GrainUsed` — `Samples` / `Hourly` /
  `Daily`). Internal aggregation grain — supporting info, not
  primary, but visible.
- **Window totals — Up / Down bytes** (`AppDetailSummary.BytesUp` /
  `BytesDown`, humanized via `PerAppPage.FormatBytes`).
- **AppId** (numeric record identifier — see §3 navigation surface
  above).

### Chart card

A `Border` carrying the traffic-over-time visualization. The card
contains:

- **Chart title.** `ui:TextBlock Style="text.subtitle"`, copy
  `"Traffic over time"`.
- **Chart subtitle.** `ui:TextBlock Style="text.caption"`, Foreground
  inherits `text.secondary` from the Style. Copy is grain+window
  shorthand without the verbal-padding `"Showing "` prefix — e.g.
  `"per-minute detail · last 24 hours"`, `"hourly buckets · last 7
  days"`, `"daily buckets · last 30 days"`. Generated by
  `ChartBuilder.DescribeView`.
- **Chart.** `lvc:CartesianChart`, `Background="Transparent"`,
  `LegendPosition="Top"`. Series shape switches by grain via the
  shared `ChartBuilder.BuildSeries`:
  - `Samples` grain (≤24h windows) → two `LineSeries<DateTimePoint>`
    (Up, Down).
  - `Hourly` / `Daily` grain → two `StackedColumnSeries<DateTimePoint>`
    (Up = bottom of stack, Down = top).
- **No-data overlay.** Centered `TextBlock` rendered when both Up
  and Down point sets are empty. Copy:
  `"No traffic recorded in this window."` Foreground `text.secondary`.
  `IsHitTestVisible="False"` so it doesn't intercept chart hover.

### Connections card (endpoints)

A `Border` carrying a `DataGrid` over `Connections` (an
`ObservableCollection<ConnectionRowViewModel>`). The grid surfaces
endpoint rows. The VM record carries:

- `Protocol` (string — `"tcp"` / `"udp"`).
- `RemoteAddress` (string — IPv4 or IPv6).
- `RemotePort` (int).
- `RemoteClass` (string — `"Wan"` / `"Local"`). Currently in the VM
  but unbound to any column today; this round binds it.
- `UpText`, `DownText` (humanized bytes).

A small card title above the grid reading `"Connections (endpoints)"`.
Density, row contrast, column composition, and responsive behavior
are OPEN (§8.4) — the findings deliberately did not prescribe.

### Recent sessions card

A `Border` carrying a `DataGrid` over `Sessions` (an
`ObservableCollection<SessionRowViewModel>`). The VM record carries:

- `SessionId` (long).
- `Pid` (int).
- `StartText` (formatted `"yyyy-MM-dd HH:mm"` local).
- `EndText` (formatted same, OR the literal `"(running)"` for
  sessions still alive — `EndTimeUnixMs is null`).
- `HostedServices` (raw comma-separated string from the server, e.g.
  `"Schedule, Themes"` for an `svchost` row; empty for non-host
  processes).

A small card title above the grid reading `"Recent sessions"`.
Density, row contrast, column composition, and responsive behavior
are OPEN (§8.4) — same as Connections.

### Loading affordance

A `ui:ProgressRing IsIndeterminate="True"` rendered during
refresh. Three independent data surfaces refresh in parallel (chart,
connections, sessions). Whether the loading is rendered per-surface
(three rings) OR as a single page-level overlay is OPEN (§8.4). No
shimmer (§8.1 global lock).

---

## 4. State coverage

States to render. Every state below MUST appear in the mockup.
Per-grid empty states render under the unified `state: empty` cell
because Connections and Sessions can independently be empty even
when traffic exists.

### `state: default` (steady-state, connected, data flowing)

- Back affordance rendered with Fluent chevron + `"Per-App"` text.
- Page identity TextBlock carries an example image name
  (e.g. `"chrome.exe"`).
- AppId surfaces in its (Claude Design–proposed) labeled placement.
- Status banner collapsed.
- Window picker at `24h`, shorthand label rendered, long-form
  tooltip visible on one item (mock the expanded ComboBox).
- Summary card filled — publisher, signature, path (rendered in
  `text.mono`, full and untruncated), grain, Up / Down totals, AppId.
  At least ONE default-state mock shows a **signed app from a
  user-writable path** (e.g. Chrome from `%LOCALAPPDATA%`) so the
  user-writable signal is in scope WITHOUT the alert combination
  — see §8.4 question 2.
- Chart populated with realistic series (Samples grain — line
  series; or Hourly grain — stacked columns; show at least one of
  each across the default + variant mocks).
- Connections grid populated with realistic endpoint rows. At
  least one `Wan` row and one `Local` row so the WAN/Local
  classification surfaces. At least one IPv6 address so the
  responsive-collapse problem is auditable. At least one row with
  a known well-known port (e.g. `:443`).
- Sessions grid populated with realistic rows. At least one
  session showing `"(running)"` in the End column. At least one
  `svchost` row with a non-empty `HostedServices` value
  (e.g. `"Schedule, Themes"`).

### `state: default — alert combination` (unsigned + user-writable + active connections)

A separate steady-state variant for the high-importance
combination case. Same controls populated as `default`, but:

- Summary card carries `signature_status = Unsigned` AND
  `is_user_writable_path = true`. Path renders something like
  `C:\Users\<user>\AppData\Local\Temp\<vendor>\suspicious.exe`.
- The user-writable signal is visually elevated per the Claude
  Design proposal (see §8.4 question 2). This is the state that
  proves the elevation reads as warning-but-not-panic.
- Connections grid carries at least one `Wan` row (the "has
  connections" piece of the alert condition).
- Page is otherwise identical to default — same chart, same
  sessions, same chrome. The point is to show the difference the
  alert-elevation makes.

### `state: loading`

- Status banner collapsed.
- Summary card values render `"—"` placeholders (`Style="text.mono"`,
  Foreground `text.tertiary`) until first paint. AppId, if visible
  somewhere, renders `"—"`.
- Chart card body: loading affordance per §8.4 question 6.
- Connections + Sessions card bodies: per §8.4 question 6 (page-level
  ring OR per-surface rings).
- No skeleton-shimmer anywhere (§8.1 global lock).

### `state: empty — chart no traffic`

- Status banner collapsed.
- Summary card filled (the query succeeded; the window has zero
  traffic but the app metadata is known).
- Window totals render `"0 B"` / `"0 B"` (NOT em-dash — the answer
  is genuinely zero).
- Chart card body: centered no-data overlay copy
  `"No traffic recorded in this window."`, `Style="text.body"`
  Foreground `text.secondary`.
- Connections + Sessions: each may also be empty in this state
  (commonly are, when there's no traffic). Per-grid empty copy
  applies (next two states).

### `state: empty — connections grid`

Sub-state. Connections card body renders centered
`ui:TextBlock Style="text.body"` Foreground `text.secondary` copy
`"No endpoints recorded in this window."`. Other surfaces (summary,
chart, sessions) unchanged from default.

### `state: empty — sessions grid`

Sub-state. Sessions card body renders centered
`ui:TextBlock Style="text.body"` Foreground `text.secondary` copy
`"No sessions recorded in this window."`. Other surfaces unchanged.

### `state: disconnected` (named-pipe down — `HistoryQueryClient.IsConnectionLost`)

- Status banner visible:
  - Background `status.critical.background`, Foreground
    `status.critical`, `radius.control`, `padding=space.8`.
  - Copy: `"Service disconnected — last refresh stale."`
- Summary card retains last-known values at `Opacity=0.6`.
- Chart retains last-known series at `Opacity=0.6` (NOT cleared —
  matches the history-class "preserve last known" pattern).
- Connections + Sessions grids retain last-known rows at
  `Opacity=0.6`.

### `state: error` (any other query failure — NOT pipe-down)

- Status banner visible:
  - Background `status.caution.background`, Foreground
    `status.caution.text`, `radius.control`, `padding=space.8`.
  - Copy: `"Query failed ({ExceptionTypeName}): {ExceptionMessage}"`.
    Mock shows one realistic example, e.g.
    `"Query failed (SqliteException): database is locked"`.
- Summary card / chart / Connections / Sessions retain last-known
  data at `Opacity=0.6`.

> **No `warming` state.** App Detail is a history-class surface — it
> queries SQLite via `HistoryQueryClient`, not the in-memory live
> aggregate. There is no fill window to surface.

### Dark theme

Per the template, ONE steady-state layout renders additionally in
**dark** theme so theme-swap behavior is auditable. The
`state: default` is the canonical pick (the alert-combination
variant in dark theme is OK as a stretch but not required).

---

## 5. Tokens in scope

The constraints. Specific tokens are assigned by Claude Design
during composition.

### `surface.*`

- Page root: `surface.background` (Mica shows through).
- Summary card, Chart card, Connections card, Sessions card: ALL
  use `surface.card` (opaque). Every text- or data-bearing card on
  this screen MUST be opaque — Mica + chart-label contrast +
  text-on-translucent fails AA otherwise.
- StatusBanner background: per-state — see `status.*` below.
- Loading-state placeholder cells (em-dash filler in summary
  values): no surface change; the em-dash sits on the same card
  surface, in `text.tertiary` foreground.

> **Precondition check.** The `surface.card` token must resolve to
> the opaque brand value (not the stock Wpf.Ui
> `CardBackgroundFillColorDefaultBrush`, which is the translucent
> variant). The dashboard polish round migrated the card surfaces;
> this brief assumes that migration reached App Detail's cards or is
> scheduled for the same implementation pass.

### `text.*`

Full type ramp available (`text.title` / `text.subtitle` / `text.body`
/ `text.body.strong` / `text.caption` / `text.eyebrow` / `text.mono`).

Per-screen constraints:

- **Image path MUST use `text.mono`.** Non-negotiable on this
  screen (§8.2 lock).
- **Numeric / digit-aligned values** (window totals, byte values,
  IP addresses, port numbers, timestamps) use `text.mono` for column
  alignment.
- Chart title is `text.subtitle`; chart subtitle is `text.caption`.
- Card titles inside Connections + Sessions are `text.body.strong`
  (the small "Connections (endpoints)" / "Recent sessions"
  labels above each grid).
- Page-identity TextBlock: `text.subtitle` with
  `FontFamily="{StaticResource font.display}"` (the display family is
  set explicitly to match Per-App's title rendering).

### `accent.*`

- **No filled accent surfaces in this round.** No buttons, pills, or
  selection bars carry an accent fill on App Detail. (Reminder:
  `accent.default` is NEVER a filled background; locked decision.)

### `status.*`

Paired bg/foreground per banner. Per-state copy in §4.

| State          | Background                   | Foreground             |
|----------------|------------------------------|------------------------|
| disconnected   | `status.critical.background` | `status.critical`      |
| error          | `status.caution.background`  | `status.caution.text`  |

The **user-writable signal** (when in the alert combination) MAY
use `status.caution.background` + `status.caution.text` as one
elevation option — but the composition is OPEN (§8.4); other
token combinations are valid if Claude Design proposes them.

### `border.*`

- All four cards (summary, chart, connections, sessions): `border.card`,
  1px. (Not the gradient `ControlElevationBorderBrush` in current XAML.)
- StatusBanner: no border (background alone carries the signal).
- Any divider inside the summary card (if Claude Design's composition
  uses dividers): `border.subtle`, 1px.

### `space.*`

- Page outer margin: `space.24` (mandatory).
- 4-based scale for all inter-element / inter-card / padding choices —
  Claude Design composes within the scale.

### `radius.*` (role tokens only)

- All four cards: `radius.card`.
- StatusBanner: `radius.control`.
- The user-writable signal (if rendered as a pill / chip): `radius.control`.
- Window picker (Wpf.Ui ComboBox): `radius.control` (Wpf.Ui default).
- Chart tooltip surface: `radius.control` (Wpf.Ui default for popups).

### Material / effect

- ⚠ **Brief authoring oversight, superseded post-mockup (2026-06-07).**
  This section originally read "No metallic / brushed surfaces, no
  live blur, no continuous animation on this screen. App Detail is
  data-dense; visual effects compete with the data. Dashboard's
  `metal.card` + `edge.light` lives on its status cards; App Detail
  stays flat `surface.card`." That call was an oversight, not a
  design decision — it propagated the brief template's pre-resolution
  default. App Detail adopts the canonical card recipe
  (`metal.card` + `border.card` + `radius.card` + `shadow.card`)
  matching Dashboard and Per-App. See
  `docs/design-system.md` §9 "Card surface — canonical treatment"
  for the project-wide standing decision.
- **No live blur, no continuous animation, no animated sheen.** The
  metallic treatment is static gradients + a single `DropShadowEffect`.
- Chart tooltip background gets a `DropShadow(0, 4, 8, 8, ~38%
  black)` for backdrop separation when its tone matches the chart
  card behind it — see §6.

### Chart tokens

Listed under §6.1.

### New tokens required by this brief

**None.** Every token referenced above exists in
`DesignTokens.xaml` and `colors_and_type.css` today. The summary
card and chart card migrations (`CardBackgroundFillColorDefaultBrush`
→ `surface.card`; `ControlElevationBorderBrush` → `border.card`)
are pointer changes against the existing token surface, not new
tokens.

---

## 6. Chart-chrome — tokens AND behavior spec

App Detail has a chart, so both 6.1 and 6.2 apply.

### 6.1 Chart-chrome paint tokens

| Token              | Use                                                       |
|--------------------|-----------------------------------------------------------|
| `chart.axis`       | Axis line stroke                                          |
| `chart.gridline`   | Gridline stroke (apply low alpha in code, ~0x0B)          |
| `chart.axis.label` | Axis tick labels (= `text.tertiary`)                      |
| `chart.tooltip.bg` | Tooltip surface — **OPAQUE** so contrast is stable        |
| `chart.tooltip.text` | Tooltip label text                                      |
| `chart.legend.text`| Legend pill labels                                        |
| `chart.upSeries`   | Up series stroke (line) or fill (stacked column)          |
| `chart.downSeries` | Down series stroke (line) or fill (stacked column)        |

App Detail does NOT consume `chart.wan` / `chart.local` on the chart
itself — there is no WAN-vs-Local categorical viz on the chart. The
WAN/Local distinction surfaces on the Connections grid rows; whether
Claude Design's grid composition uses `chart.wan` / `chart.local` to
mark those rows is part of §8.4 question 5.

Annotate these tokens by name on the chart. Wiring is code, not
mock — `Services/ChartTheming.cs` applies the paints in C# on
construction AND on `ApplicationThemeManager.Changed`. App Detail's
ctor already calls `ChartTheming.Apply(SeriesChart)`
(`AppDetailPage.xaml.cs:42-43`); paint changes inherit automatically.

### 6.2 Chart behavior spec

- **Y-axis labeler — grain-adaptive.** The current labeler reads
  `"{FormatBytes(v)}/bucket"` (`AppDetailPage.xaml.cs:40`); the
  `"/bucket"` string is internal storage jargon. Replace with a
  grain-adaptive unit suffix:
  - `Samples` grain → `"/min"`.
  - `Hourly` grain → `"/hr"`.
  - `Daily` grain → `"/day"`.
  Sample axis values: `"3.2 MB/min"`, `"12 GB/hr"`, `"450 GB/day"`.
  Plumb the grain through one closure so both axes consume it (next
  bullet).
- **X-axis labeler — grain-adaptive.** The current labeler renders
  `"HH:mm"` regardless of window span (`AppDetailPage.xaml.cs:36`).
  For Hourly grain over 7 days, the same `HH:mm` repeats every day
  with no day-boundary cue. Replace with:
  - `Samples` grain → `"HH:mm"` (unchanged — the window is ≤24h so
    intra-day resolution suffices).
  - `Hourly` grain → `"MM-dd HH"` (day-boundary cue + hour).
  - `Daily` grain → `"MM-dd"`.
  Tick `MinStep` chosen so ~6–8 labels render across the window's
  span — implementer detail.
- **Tooltip behavior.** Finding strategy is X-snap (cursor anywhere
  along the X width activates the nearest bucket; Y proximity
  ignored). `ChartTheming.Apply` already sets
  `FindingStrategy.CompareOnlyXTakeClosest` on every chart
  (`ChartTheming.cs:65`); App Detail inherits.
  - **Tooltip layout.** Default LiveCharts2 v2 vertical layout
    (header + per-series rows). One row per series — Up and Down on
    separate rows. Per-series formatters render rates with the
    grain-adaptive unit so the tooltip row reads
    `"Up · 3.2 MB/min"` etc. Single-row dense form
    (`"23:34 · Up 3.2 MB/min · Dn 4.1 MB/min"`) is deferred — would
    require a custom `IChartTooltip<SkiaSharpDrawingContext>`.
  - **Tooltip hover sensitivity.** Forgiving hover via
    `GeometrySize ~ 20` + `GeometryFill = GeometryStroke = null` on
    every `LineSeries`. Currently NOT applied — `ChartBuilder.cs:39,
    46` sets `GeometrySize = 0` on both Up and Down line series,
    making the hit area effectively zero. The fix moves the
    forgiving config into `ChartBuilder` so every line-series chart
    in the app inherits it (Dashboard currently does this page-side
    at `DashboardPage.xaml.cs:96-108`; centralizing it into
    `ChartBuilder` lets Dashboard delete its page-side override on
    the next polish pass). Cross-screen consequence — see §16.
  - **Tooltip background.** OPAQUE via `chart.tooltip.bg` — never
    translucent. Drop shadow `(0, 4, 8, 8, ~38% black)` via
    `LiveChartsCore.SkiaSharpView.Painting.ImageFilters.DropShadow`
    on the `TooltipBackgroundPaint`. `ChartTheming.Apply` already
    wires this (`ChartTheming.cs:117-128`).
- **Legend behavior.** `LegendPosition="Top"` (current). Series
  Name strings are `"Up"` and `"Down"` (no `"/min"` / `"/hr"` /
  `"/day"` — the Y axis labeler owns the units; duplicating them
  in the legend is noise).
- **Series stroke / fill split.**
  - `LineSeries` (Samples grain): stroke from `chart.upSeries` /
    `chart.downSeries`; fill is a ~25% alpha variant of the same
    color for the area under the line.
  - `StackedColumnSeries` (Hourly / Daily grain): fill from the
    same tokens; stroke defaults to none. Up = bottom of the stack,
    Down = top of the stack.
- **Theme-flip re-paint.** Chart paints re-apply on
  `ApplicationThemeManager.Changed` via `ChartTheming.Changed`
  (`AppDetailPage.xaml.cs:43`). Brief annotates tokens; wiring is
  code.

---

## 7. Density assignment

App Detail carries TWO DataGrids (Connections, Sessions). Density
for these grids is intentionally **OPEN** (§8.4 question 5) — the
findings reviewed Per-App's `style.datagrid.compact` precedent and
explicitly rejected its automatic transfer to App Detail because:

- Per-App's compact density was justified by AppsGrid being the
  *single dominant grid on its page* whose entire job is showing
  rows. App Detail has two side-by-side grids competing for
  vertical space below a summary card and a chart card. The
  justification doesn't transfer.
- Defaulting to compact works against ZenVizor's "light, usable
  surface" goal when the grids are not the page's whole job.

The compact style (`style.datagrid.compact`, row 22, padding 6,2,
body font) IS available if Claude Design proposes it; default
density (row ~28) is equally available; the proposal needs to
justify against the broader layout direction the design pass lands
on. See §8.4 question 5.

No ListView on App Detail.

---

## 8. Locks and open questions for this screen

### 8.1 Global locks

- **Loading = default Fluent `ProgressRing`**, not skeleton-shimmer
  (light-and-fast principle, design-system §2). Where the ring(s)
  sit is OPEN (§8.4 question 6).
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HC activation. The mock does
  not draw HC variants; `HighContrast.xaml` collapses every semantic
  token onto `SystemColors.*` at runtime. Implementer verifies HC
  during the per-page verification gate (design-system §10).

### 8.2 Screen-specific outcome locks

What the findings settled at the *outcome* level. Composition is
Claude Design's; these outcomes are not re-litigable in the mock.

- **Image path MUST NEVER be truncated on App Detail.** This is the
  furthest drill-down state in the app — the screen where everything
  about an app is supposed to be visible. No ellipsis, no
  char-clipping, no "hover for the rest." Whatever summary-card
  layout Claude Design proposes (§8.4 question 1), the path needs
  enough room to render in full at the page's minimum supported
  width. Wrap across lines if necessary; allow horizontal scroll
  inside a bounded container as a last resort. **DO NOT
  ellipsize.** The fitting problem is part of §8.4 question 1.
- **Image path renders in `text.mono`.** Paths are code-like;
  proportional digits break slash-and-segment alignment.
- **AppId MUST be visible somewhere on this page AND must be
  labeled as a record identifier.** It must NOT live inline in the
  page-identity TextBlock; it must NOT live in a tooltip on the
  title. Where it lands is OPEN (§8.4 question 4) — but the
  *visibility + labeling* outcomes are fixed.
- **Back affordance uses `ui:SymbolIcon Symbol="ChevronLeft20"`** —
  the Fluent vocabulary, not the ASCII `<`. (Where the back
  affordance sits relative to the page title is OPEN — §8.4
  question 3.)
- **The user-writable signal MUST be visually elevated when in the
  alert combination** (`is_user_writable_path == true` AND
  `signature_status ∈ {Unsigned, Invalid}` AND the app has at
  least one connection in the window). Elevation outcome locked;
  treatment (chip, badge, banner, color-shift, accompanying
  explainer copy, etc.) is OPEN (§8.4 question 2).
- **The user-writable signal MUST NOT cry wolf on the benign case**
  (signed binary from a user-writable path — Chrome, VS Code,
  Discord). Outcome locked: when `is_user_writable_path == true`
  AND `signature_status == Signed`, the signal either does not
  surface OR surfaces with neutral-not-warning weight.
  Treatment OPEN (§8.4 question 2).
- **`RemoteClass` (WAN / Local) MUST surface on Connections rows.**
  The data is on the VM today and unbound; this round binds it. How
  it surfaces (a Class column, a row-background tint, a leading
  icon, a chip in the address cell, etc.) is OPEN (§8.4 question 5).
- **Y-axis labels MUST NOT say `"/bucket"`.** The grain-adaptive
  `"/min"` / `"/hr"` / `"/day"` suffix in §6.2 is the locked outcome.
- **X-axis labels MUST distinguish day boundaries for multi-day
  grains.** Hourly grain over 7 days must not render the same
  `HH:mm` indistinguishably across days. The grain-adaptive
  `"MM-dd HH"` / `"MM-dd"` format in §6.2 is the locked outcome.
- **Chart subtitle drops the `"Showing "` prefix.** Render as
  grain+window shorthand (`"per-minute detail · last 24 hours"`),
  not a verbal-narrator sentence (`"Showing per-minute detail over
  the last 24 hours."`).
- **StatusBanner SPLITS disconnected vs query-failed** per §11.3.
  Same split as Per-App / History; opposite of Dashboard.
- **Card backgrounds + borders + chart tooltip migrate to opaque
  brand tokens.** `CardBackgroundFillColorDefaultBrush` →
  `surface.card`; `ControlElevationBorderBrush` → `border.card`;
  chart tooltip inherits Dashboard's locked treatment via
  `ChartTheming.Apply` (already wired in App Detail's ctor).

### 8.3 Boundary-case overrides of hard rules

**Intentionally n/a.** App Detail is a history-class drill
surface and fully aligns with every hard rule:

- §11 (discovery > ranking): Connections grid and Sessions grid are
  uncapped server-side — every endpoint with non-zero bytes in the
  window appears in Connections; every session whose window
  overlaps appears in Sessions. There is no top-N gate, no "see
  more" affordance, no client-side filter (yet — that's F7 in §15).
- §12 (honest attribution): Sessions grid surfaces `HostedServices`
  for svchost rows (the raw comma-separated string from the
  server) so users see *which* services were hosted in that PID's
  lifetime — but the per-session byte total (covered indirectly by
  the chart, not by a per-session column) attributes to the PID,
  not split across the co-hosted services. Connections rows
  attribute bytes to the endpoint-from-this-PID, not across
  co-hosted services.
- §13 (passive-only): no kill / block / quarantine / "stop this
  app" / right-click action / hover-action affordances on any
  surface.
- Template §4's disconnected/error split: App Detail takes the
  default split (not the merge); not an override.

### 8.4 Open design questions for Claude Design

The findings deliberately left these open. Each invites Claude
Design to propose variants the user picks from during iteration.

#### Q1. Summary card layout (scannable identity block)

**Open:** the summary card carries publisher, signature, path,
grain, window totals (Up / Down), AppId, and conditionally the
user-writable signal. The current implementation renders all of
this as two long pipe-separated paragraphs (`"Publisher: X |
Signature: Y | Grain: Z"` / `"Path: ... | Up: ... | Down: ..."`)
which wrap ungracefully, have no visual separation between
label and value, and have no consistent alignment for scanning.
How should this card compose so it reads at a glance as an
identity block instead of two paragraphs?

**Constraints:**
- Path renders in `text.mono` AND MUST NEVER be truncated (§8.2).
  This is the hardest constraint — paths are long, and the card
  has to make room.
- AppId surfaces somewhere visible AND labeled (§8.2 + Q4).
- Numeric values (Up, Down, Grain, AppId) use `text.mono`
  (§5 text constraint).
- User-writable signal (when in alert combination) elevates
  visually (§8.2 + Q2).
- Card surface is opaque `surface.card`; border is `border.card`;
  radius `radius.card`.
- No new tokens.

**Variants:** propose 2–3.

#### Q2. User-writable framing (visual elevation AND user-facing context)

**Open:** the `is_user_writable_path` flag is the alert-condition
signal for ZenVizor's first real alert (unsigned + user-writable +
has connections — `zenvizor-sprint-plan.md:250`). The current
implementation appends the literal string `"  [user-writable
path]"` inline after the Signature value with no visual elevation —
a security-relevant signal painted as a bracketed footnote. But
naively elevating it (e.g. always rendering a red pill) creates a
worse problem: user-writable on its own is NOT a red flag. Chrome,
VS Code, and Discord are all signed binaries from user-writable
locations (per `docs/phase-2-verification.md:225`). And the casual
user doesn't know what "user-writable" means anyway — the
user-facing translation Phase 4 tested was "personal folders" vs
"system folders" (`docs/phase-4-filter-recommendations.md:51`).

How should this card simultaneously (a) elevate the
high-importance combination so it reads as warning when relevant,
(b) avoid crying wolf on the benign signed-from-user-writable
case, and (c) convey what "user-writable" / "personal folder"
means and why it matters in this context?

**Constraints:**
- Outcome locked: elevation must apply when in the alert
  combination; must NOT apply with warning weight when the binary
  is `Signed` (§8.2).
- Plain-language framing should map to "personal folders" / "system
  folders" vocabulary if naming the concept.
- Available status tokens: `status.caution.background` +
  `status.caution.text` are one option. Other token combinations
  (caption foreground, eyebrow weight, icon + caption, etc.) are
  valid if Claude Design judges them appropriate.
- The mock's `state: default — alert combination` (§4) is the
  primary test surface; the regular `state: default` shows the
  benign-signed-from-user-writable case for comparison.

**Variants:** propose 2–3. The user picks one during iteration.

#### Q3. Back-affordance hierarchy (relationship to page identity)

**Open:** the back affordance (`< Per-App`) currently sits in the
same horizontal row as the page identity TextBlock (the app
image name). This conflates a navigation control with the page
title and creates an unclear hierarchy — which is the "title"?
On narrow windows the back affordance shifts the title right; on
wide windows they drift apart with no visual relationship. Where
should the back affordance sit so its role is unambiguous and its
relationship to the page title is clear?

**Constraints:**
- Back affordance carries Fluent `ChevronLeft20` icon + `"Per-App"`
  text (§8.2 lock).
- The page identity (app image name) is the page title — the back
  affordance is subordinate to it.
- Page outer margin is `space.24` (mandatory).
- Common patterns: breadcrumb-style above the title; inline-left
  of the title with reduced visual weight (e.g. caption-styled
  link); top-bar pattern; back-pill in a corner. Don't restrict
  to these; propose whatever reads best.

**Variants:** propose 1–2.

#### Q4. AppId placement

**Open:** the AppId is the canonical record handle (support will
ask for it). Currently it's inline in the page title
(`"chrome.exe (app id 42)"`) which both visually competes with
the actual app name and confuses casual users. Where should it
live so it's visible, clearly labeled as a record identifier,
and doesn't compete with the actual app identity?

**Constraints:**
- Outcome locked: visible somewhere on the page AND labeled (§8.2).
- NOT inline in the page-identity TextBlock.
- NOT in a tooltip on the page title (non-discoverable on a
  non-interactive title block).
- Available token surface: `text.caption` or `text.eyebrow` for
  the label; `text.mono` for the numeric value (digit-aligned
  rendering).
- Candidates: labeled cell in the summary card; metadata footer
  line; copy-to-clipboard chip near the page title; something
  else. Not prescriptive.

**Variants:** propose 1–2. May land naturally inside Q1's summary
card composition.

#### Q5. Connections + Sessions grid presentation

**Open:** the two grids carry multiple coupled presentation
problems documented in the findings (cluster, no prescribed
solutions). The presentation needs to read as tabular data, not
paragraphs of digits, AND must be comprehensible to users without
sysadmin literacy. Specifically:

- **Density.** Default DataGrid (row ~28) is the current
  implementation; Per-App's `style.datagrid.compact` (row 22) is
  available but not auto-recommended (§7). The right density
  depends on the layout direction.
- **Responsive collapse.** At 800 px window width
  (`MinWindow.MinWidth` in `MainWindow.xaml:11`), each grid gets
  ~370 px before chrome. Connections needs ~360 px to show IPv6 +
  port without clipping; Sessions needs ~330 px to show both
  timestamps. Both clip ungracefully today. Side-by-side may be
  the right layout at wide widths and the wrong one at narrow
  widths.
- **Row contrast.** No `AlternatingRowBackground` today. Row scan
  ergonomics could use either alt rows, lightweight dividers, or
  neither — depending on density and the broader layout.
- **Code-like columns.** `Address`, `Start`, `End`, `Services` are
  all code-like content (IP/port, timestamps, comma-separated
  service tokens) rendered in proportional digits today. They
  should read as columnar data; `text.mono` is the obvious tool,
  but whether every column gets it or only some depends on
  composition.
- **WAN / Local classification.** `RemoteClass` is on the
  ConnectionRow VM but unbound today. The classification MUST
  surface (§8.2); how — a Class column with `chart.wan` /
  `chart.local` pills; row-background tint by class; leading icon
  on the address cell; something else — is OPEN.
- **Column comprehension** (the biggest one). Most users will not
  know what `Proto`, `Port`, `Session`, `PID`, or `Services`
  represent or why they care. Sessions' `(running)` indicator is
  one of the few self-explanatory cells; everything else assumes
  sysadmin literacy this app's audience doesn't have. How can
  columns surface their meaning gracefully?
    Candidates (not prescriptive): column-header tooltips with
    plain-language explanations (the Dashboard's talkers list
    pattern, `Resources/Strings.Tooltips.xaml`); persistent
    sub-header captions; an information button per card; a
    click-through detail surface for a row that explains what the
    row represents (relates to F1 in §15).

How should the two grids present so they read as tabular data AND
teach the user what their columns mean?

**Constraints:**
- `RemoteClass` MUST surface (§8.2).
- Code-like content uses `text.mono` (§5 text constraint).
- No `accent.*` filled backgrounds (§5 accent constraint).
- Cards are opaque `surface.card` (§8.2).
- Per-App's compact-density precedent does NOT automatically
  transfer (§7).
- The column-comprehension concern may motivate F1 (deferred
  endpoint-detail surface) as a viable design direction worth
  weighing — Claude Design's proposal may reasonably argue
  "presentation alone can't solve this; row click → detail is
  the right answer." If so, surface the argument in the variant.

**Variants:** propose 2–3 across the density / layout / row-class /
comprehension dimensions. Several iterations may be needed — this
is the cluster the findings explicitly flagged as iterative.

#### Q6. Loading-affordance placement

**Open:** three independent data surfaces refresh in parallel
(chart, Connections, Sessions). The current implementation uses
`Mouse.OverrideCursor = Cursors.Wait` only — no visible loading
affordance. Should loading render per-surface (three independent
`ProgressRing`s — one in the chart card body, one in each grid
viewport) or as a single page-level overlay across the whole
content area?

**Constraints:**
- Static `ProgressRing` only — no shimmer (§8.1 global lock).
- Per-surface treatment is the convention in Dashboard / Per-App
  (per-card rings; em-dash placeholders in small headline cells);
  page-level overlay is a deliberate departure if Claude Design
  proposes it.
- Summary card's loading treatment is em-dash placeholders in
  the values (`text.mono`, `text.tertiary`) regardless — that's
  the locked pattern (§4).
- Refresh wait may exceed ~1 s for Hourly / Daily grains over 30d /
  90d windows (large queries); per-surface rings need a `"Loading…"`
  caption (`text.caption`, `text.secondary`) appearing after ~1 s.

**Variants:** propose 1–2.

---

## 9. Annotation work specific to this screen

- **New tokens this brief introduces.** **None.** Every token
  referenced is in the primer's token table and resolves correctly
  in `DesignTokens.xaml` and `colors_and_type.css` today.
- **Per-screen renames / repointings.** The brief migrates two raw
  Wpf.Ui keys to semantic tokens on every card on this page:
  - `CardBackgroundFillColorDefaultBrush` → `surface.card` (opaque
    card background).
  - `ControlElevationBorderBrush` → `border.card` (1px card stroke).
  These are pointer changes against the existing token surface, not
  value changes. The implementer treats them as a per-card
  `Background` / `BorderBrush` swap.

---

## 10. Per-screen WPF translation gotchas

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages have infinite vertical extent. App Detail ALREADY
  enforces both `ConnectionsGrid.MaxHeight` and `SessionsGrid.MaxHeight`
  at `Math.Max(200, (window.ActualHeight - 220) / 2)` in
  `EnforceDataGridBounds`, wired on both `Loaded` and `SizeChanged`
  (`AppDetailPage.xaml.cs:64-71`). DO NOT remove this. The polish
  round does not change the wiring; if Claude Design's responsive
  layout collapses the two grids vertically at narrow widths, the
  cap formula will need a sibling for the stacked case (each grid
  gets the residual non-stacked-half height) — implementer detail,
  flagged here so the responsive change doesn't accidentally break
  virtualization.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode` is the default (`Disabled`) on App Detail.**
  Unlike the nav-rail pages, App Detail re-instantiates on every
  visit (the user can drill into a different app each time, and the
  page's `DataContext` is the AppId). `Loaded` DOES refire on every
  visit. The bounds enforcement still hangs off `SizeChanged` for
  window-resize and `Loaded` for the initial measure — both already
  wired. New `Loaded` work added in polish is safe; it re-runs per
  visit.
- **`Margin / Padding cannot bind a `Double`-typed StaticResource.**
  `space.*` tokens are `sys:Double` (per
  `project_wpf_spacing_token_thickness.md`). XAML cannot resolve
  `Margin="{StaticResource space.24}"` because Margin expects
  `Thickness`. Write spacing values as literals
  (`Margin="24"`, `Padding="12,8"`) — they're token-aligned but not
  token-bound. The mock annotates them as `space.*`; the XAML uses
  literals.
- **`Wpf.Ui.NavigationView` paints `NavigationViewContentBackground`
  over the Page content area.** Default is ~30% gray, occluding Mica
  showthrough. For Mica showthrough on the page side, override the
  resource key to `Transparent` in
  `App.xaml.cs ApplyDirectLevelOverrides()`. (The Dashboard polish
  round already applied this override globally; App Detail inherits
  it.)
- **SkiaSharp chart paints do NOT inherit `DynamicResource`.** Chart
  series colors AND chart-chrome paints
  (`chart.axis` / `chart.gridline` / `chart.axis.label` /
  `chart.tooltip.bg` / `chart.tooltip.text` / `chart.legend.text` /
  `chart.upSeries` / `chart.downSeries`) are applied in C# by
  reading the brush resource and feeding the underlying `Color` into
  an `SKPaint`. Re-applied on `ApplicationThemeManager.Changed` via
  `ChartTheming.Changed` (`AppDetailPage.xaml.cs:43`). The brief
  annotates token names; wiring is in code.
- **Grain-adaptive axis labelers are a `ChartBuilder` change.** The
  axes are created once in the page ctor (`AppDetailPage.xaml.cs:34-41`)
  with bare labeler closures. Plumbing grain through them requires
  either (a) re-creating axes in `ApplyDetail` after `detail.GrainUsed`
  is known, OR (b) factoring the labelers into `ChartBuilder` (or a
  sibling) so they're set alongside `BuildSeries`. (b) is preferred —
  keeps axis-setup adjacent to series-setup, single source for the
  grain-adaptive policy. Cross-screen consequence — History will
  inherit the same labeler policy.
- **Forgiving-hover GeometrySize move is a `ChartBuilder` change.**
  Currently `ChartBuilder.cs:39,46` sets `GeometrySize = 0` on Up
  and Down line series. Move to `GeometrySize = 20`,
  `GeometryFill = GeometryStroke = null`. Affects every line-series
  chart in the app — see §16.1.
- **Subtitle copy convention is a `ChartBuilder.DescribeView`
  change.** Currently produces `"Showing X over Y."`; drop the
  `"Showing "` prefix and the trailing period, render as
  `"X · Y"` shorthand. Cross-screen consequence — History inherits.
- **Window picker shorthand item template** matches Per-App's
  pattern. `WindowPreset` already carries a `Short` field
  (`HistoryQueryClient.cs:117`) — the item template binds `Short`
  for display, `Label` for the per-item `ToolTip`, AND the ComboBox's
  selection display also binds `Short`. Implementation pattern is
  identical to Per-App's polish — see Per-App brief §10 for the
  template structure.
- **Path rendering across multiple lines** (if Claude Design's
  summary-card composition wraps the path rather than scrolling).
  Use `TextWrapping="Wrap"`; bind `Style="{StaticResource text.mono}"`
  for the font. Set `TextTrimming="None"` explicitly — the default
  `CharacterEllipsis` on `TextBlock` will defeat the no-truncation
  lock if a parent container hands the text a smaller arrange than
  measure (rare but possible inside Grids).
- **User-writable signal binding.** `AppDetailSummary.IsUserWritablePath`
  is a `bool`; the alert combination is conditional on it AND
  `SignatureStatus ∈ {Unsigned, Invalid}`. Implementer plumbs both
  through to the elevation logic — a `ValueConverter` keyed on a
  tuple, or two boolean bindings on a `DataTrigger`. The mock shows
  three rendering states (no flag, flag without alert combination,
  flag with alert combination) so the conditional is auditable.

---

## 11. Discovery > ranking — per-screen application

App Detail is a drill destination, not a drill source — the user
arrived here from Per-App because they already chose this specific
app. The screen carries TWO data lists (Connections grid, Sessions
grid), both **uncapped server-side**:

- `ConnectionListResult.Connections` returns every endpoint with
  non-zero bytes for this AppId in the window.
- `AppDetailResult.RecentSessions` returns every session whose
  window overlaps.

No top-N gate, no "see more" affordance, no ellipsis that hides
rows. If the lists grow long, virtualization scrolls them inside
their card; the page never scrolls.

App Detail does NOT override the rule. Dashboard's "Top 10" is the
canonical override; Per-App is the compliant drill source; App
Detail is the compliant drill destination.

*Memory: `project_discovery_principle.md`.*

---

## 12. Honest attribution — per-screen application

- **Per-PID byte totals (Up / Down) on the summary card** attribute
  to the app's PID-set in the window. No per-co-hosted-service
  split.
- **Connections rows** attribute bytes-per-endpoint *from this PID*
  to that endpoint. The endpoint is identified by
  `(Protocol, RemoteAddress, RemotePort, RemoteClass)`. For an
  svchost row, the bytes attribute to the host PID, not split
  across the co-hosted services that PID hosted.
- **Sessions rows** surface `HostedServices` (a raw comma-separated
  string from the server) so users can see *which* services were
  ever hosted in this PID's lifetime — but there is no per-session
  byte split-by-service column. The session row tells you "this
  PID ran from X to Y and hosted these services"; the byte volumes
  attribute to the chart, not to per-service columns in this grid.
  This preserves the "honest about what we measured" boundary while
  still surfacing the useful service-set context.
- **Traffic from DLL injection / LOLBins** (`rundll32`, `regsvr32`,
  `mshta`, `powershell`) attributes to the host process. The App
  Detail screen for such a host process reports the host's total
  Up / Down on the summary and the host's endpoints / sessions on
  the grids. This is a known documented boundary; the screen does
  not pretend otherwise — no asterisk, no caveat icon.

App Detail does NOT override the rule.

---

## 13. Passive-only — per-screen application

NO "Block" buttons, NO kill icons, NO quarantine affordances, NO
"Stop this app" right-click, NO "Disconnect endpoint" affordance on
Connections rows, NO hover-revealed action buttons anywhere on this
page. The user-writable signal is **presentation only** — it tells
the user about a state; it does not enable any action against it.

Drill / navigation affordances ARE allowed and ARE passive:

- The Back affordance returns to Per-App. Allowed.
- A future click-row → endpoint-detail surface (F1 in §15) would
  also be passive — navigation, not action.

This is a CLAUDE.md invariant. **Passive-only is non-overridable.**
A Claude Design session must not quietly add a "Block" or
"Disconnect" button to a Connections row no matter how natural the
gesture seems on a screen full of endpoint data.

---

## 14. Performance budget — per-screen application

- **No shimmer / live blur / continuous animation** on any surface
  this round. Loading uses static `ProgressRing` (§8.1). The
  user-writable signal is a static pill / chip / band per Claude
  Design's choice — no pulsing or blinking attention-grab.
- **No new poll cadence.** App Detail refreshes on `Loaded`,
  ComboBox `SelectionChanged`, and (implicitly) on
  `DataContextChanged`. Same as today.
- **Parallel queries already in place.** `GetAppDetailAsync` and
  `GetConnectionsAsync` run concurrently on a shared pipe
  (`AppDetailPage.xaml.cs:110-115`). StreamJsonRpc handles
  concurrent in-flight requests. No change.
- **Chart-paint batching** — chart paints re-apply on theme flip
  via a single `ChartTheming.Apply` call; one paint pass per flip,
  not per-series.
- **DataGrid virtualization is preserved.** `EnforceDataGridBounds`
  keeps both grids virtualized (`AppDetailPage.xaml.cs:64-71`). If
  Claude Design's responsive layout stacks the grids vertically at
  narrow widths, the implementer extends the cap formula to handle
  the stacked case — virtualization stays.
- **Forgiving-hover `GeometrySize` change does NOT regress the
  budget.** Wider hit area is a per-bucket geometry quad, not a
  per-frame redraw cost; LiveCharts2's hit-test is event-driven,
  not animated.

---

## 15. Out-of-scope — features flagged for later

The items the findings doc sorted into the **Feature** bucket. The
mock must NOT design for them in this round.

- **F1. Click-a-connection-row → endpoint detail surface.** Sessions,
  timeline, per-endpoint chart, plus plain-language explanation of
  what the connection actually represents (hostname when available,
  whether it's a known service endpoint, why this app is talking to
  it). The findings *promoted* this from speculative-future to a
  viable design direction worth weighing — Q5's column-comprehension
  concern is one of the strongest motivations for this surface,
  since a click-through detail layer is one way to teach the user
  what a row means without bloating the row. **Out of brief for this
  round** (new interaction surface, feature-class scope), but Claude
  Design should know the polish-pass result for Connections is not
  the final shape of the grid — a future-detail-surface direction
  may change what columns the grid needs to carry at all. The
  primer's old "App Detail flyout is opaque, not acrylic" decision
  is no longer load-bearing — if endpoint detail is built, the
  surface treatment (flyout, sub-page, expandable inline row, side
  panel) is itself an open question, not pre-decided.
- **F2. Reverse DNS / hostname column on Connections.** PRD §7.4
  reserves `connections.resolved_host` for a future passive-DNS
  module — NOT IN MVP. Hard "do not propose this column" boundary;
  the mock must NOT add a hostname column.
- **F3. Brush-to-zoom on the chart.** Click-and-drag to select a
  sub-window. New interaction surface; deferred.
- **F4. Export endpoint list to CSV.** Phase 5 owns export.
- **F5. "See related apps" link from svchost row** — jump to all
  apps that ever shared this PID's services. New nav surface;
  deferred.
- **F6. Active-action affordances (kill / block / disconnect
  endpoint).** HARD NO per the passive-only invariant (§13). Listed
  here so the brief explicitly forbids them in a mock; not in any
  feature backlog.
- **F7. Filter / search within Connections** (by port, address,
  class). New capability; deferred. (Per-App has a client-side
  filter; App Detail does not in this round.)

---

## 16. Chrome / cross-screen consequences

App Detail's polish reaches into shared services that paint OTHER
screens' charts. Two consequences flagged so the other briefs know
what NOT to redesign.

### 16.1 ChartBuilder forgiving-hover GeometrySize move

- **What changed.** `ChartBuilder.BuildSeries` sets `GeometrySize = 20`
  + `GeometryFill = null` + `GeometryStroke = null` on every
  `LineSeries`. Previously `GeometrySize = 0` (hit area effectively
  zero — tooltips unhittably tight).
- **Where it lives.** `src/ZenVizor.Ui/Services/ChartBuilder.cs`
  (lines 39 + 46 today).
- **Propagates to.** Every line-series chart in the app:
  - Dashboard's live chart (currently sets the forgiving config
    page-side at `DashboardPage.xaml.cs:96-108` — can DELETE that
    page-side override on its next polish pass, since `ChartBuilder`
    will now own the policy).
  - App Detail's chart (this brief).
  - History's chart (when its polish round lands).
- **Per-screen brief work needed elsewhere.** Dashboard's next polish
  brief should remove the page-side `GeometrySize` override. History's
  brief inherits the forgiving-hover behavior automatically; no work
  needed there.

### 16.2 ChartBuilder grain-adaptive axis labelers + DescribeView copy

- **What changed.** `ChartBuilder` (or a sibling) owns grain-adaptive
  X-axis labelers (`HH:mm` / `MM-dd HH` / `MM-dd`), grain-adaptive
  Y-axis rate-unit suffixes (`/min` / `/hr` / `/day`), and the
  subtitle string format (drop `"Showing "` prefix; render as
  `"X · Y"` shorthand).
- **Where it lives.** `src/ZenVizor.Ui/Services/ChartBuilder.cs` —
  `BuildSeries`, `DescribeView`, and a new helper for the
  grain-aware axis closures (factor or inline as the implementer
  prefers).
- **Propagates to.** History's chart (when its polish round lands).
  Dashboard's chart uses relative trailing-window labels (`"-2m" /
  ... / "now"`) and its own subtitle convention — NOT affected by
  this change.
- **Per-screen brief work needed elsewhere.** History brief inherits
  the grain-adaptive labeler policy and the subtitle convention; its
  brief should call them out as inherited so the implementer knows
  the surface is shared, not History-specific.

### 16.3 No MainWindow chrome changes

App Detail's polish does not modify MainWindow's title bar, bottom
bar, or status bar. The bottom-bar rate mirror (introduced by
Dashboard's polish) is visible while App Detail is shown but is
not App Detail's concern. Draw it in the App Detail mock for
visual completeness; flag it `chrome: MainWindow, not AppDetailPage`.

### 16.4 No Per-App consequences

App Detail's back-affordance treatment (Q3) is local to App Detail.
Per-App has no equivalent back surface (it's a top-level nav-rail
page). The Fluent-chevron convention this brief locks IS reusable
if any future drill destination adopts the same back pattern, but
no current brief inherits it.

---

## 17. Deliverables expected from Claude Design

The mockup hand-back MUST contain:

- Layouts for every state in §4 in the default (light) theme:
  `default`, `default — alert combination`, `loading`,
  `empty — chart no traffic`, `empty — connections grid`,
  `empty — sessions grid`, `disconnected`, `error`.
- ONE steady-state layout additionally rendered in **dark** theme
  (`state: default` recommended).
- The `default` state mock specifically must show:
  - Back affordance with Fluent chevron + `"Per-App"` text.
  - Page identity TextBlock with a realistic image name.
  - AppId visible in its proposed placement (Q4), clearly labeled.
  - Summary card filled with publisher, signature, path (`text.mono`,
    untruncated), grain, Up / Down totals, AppId.
  - Window picker at `24h` shorthand with the long-form tooltip
    visible on one expanded item.
  - Chart populated. At least one of the default mocks shows
    Samples grain (line series); at least one shows Hourly or Daily
    grain (stacked column series). Axis labels reflect the
    grain-adaptive format AND grain-adaptive Y-unit suffix.
  - Chart tooltip drawn in its active state on one mock so the
    opaque background + drop shadow + per-series row rendering is
    auditable.
  - Connections grid populated; `RemoteClass` (WAN / Local)
    surfaced per Q5's chosen proposal; at least one IPv6 row and
    at least one known-port row included.
  - Sessions grid populated; at least one row showing `"(running)"`
    in the End column; at least one svchost row showing a non-empty
    `HostedServices` value.
- The `default — alert combination` mock shows the user-writable
  signal visually elevated per Q2's chosen proposal, with the
  Connections grid carrying at least one WAN row (closing the
  alert combination).
- Variant proposals for each open question in §8.4:
  - Q1 (summary card layout) — 2–3 variants.
  - Q2 (user-writable framing) — 2–3 variants.
  - Q3 (back-affordance hierarchy) — 1–2 variants.
  - Q4 (AppId placement) — 1–2 variants (may live inside Q1).
  - Q5 (Connections + Sessions grid presentation) — 2–3 variants
    across density / layout / row-class / comprehension dimensions.
  - Q6 (loading-affordance placement) — 1–2 variants.
- Token annotations using canonical dotted names per §9. Every
  card, text run, banner, chart token, tooltip element, and pill
  labeled with its tokens.
- Density tag on the Connections + Sessions grids reflecting Q5's
  chosen direction.
- Layout hints:
  - `ConnectionsGrid.MaxHeight` and `SessionsGrid.MaxHeight`
    enforced programmatically — annotation:
    `MaxHeight: (window.ActualHeight − 220) / 2` for side-by-side;
    a sibling formula for the stacked case if Q5 lands on
    responsive collapse.
  - `scroll: pane` inside each grid card (the DataGrid's own
    scroller).
  - Page `scroll: none` — the page itself does not scroll.
- Chart-chrome token names AND chart behavior spec called out
  (§6.1 + §6.2). Grain-adaptive axis labelers and forgiving
  GeometrySize annotated as inherited from `ChartBuilder` (the
  shared service).
- Bottom-bar rate mirror drawn (MainWindow chrome) so the screen
  reads in context; flagged as MainWindow-owned (not App Detail).
- Hand-off notes confirming the brief introduces NO new tokens and
  two pointer renames (`CardBackgroundFillColorDefaultBrush` →
  `surface.card`; `ControlElevationBorderBrush` → `border.card`),
  applied to all four cards on this page.

The pre-handback checklist
(`docs/design-briefs/_return-process.md`) restates these
deliverables as a paste-ready prompt for Claude Design to
self-verify against at session end.

---

## 18. Provisional / two-states

**Intentionally n/a.** App Detail is a Group A built screen.
`AppDetailPage.xaml` exists, ships data today, and the polish
round operates on the live XAML — there is no interim placeholder
treatment to design.

---

## 19. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the
dotted-token names are the contract.
