# Claude Design brief — History

ZenVizor's History screen. Self-contained brief for a Claude Design
session whose prior pass already loaded `docs/claude-design-primer.md`
and aligned to ZenVizor's token surface. Paste this brief ALONE; do not
re-paste the primer. The mockup hand-off contract is in §19.

---

## 1. Screen identity

- **Screen name:** History.
- **XAML file:** `src/ZenVizor.Ui/Views/HistoryPage.xaml` (+
  `HistoryPage.xaml.cs`).
- **IA placement:** third item in the left nav rail
  (`Symbol="History24"`). Top-level page; `NavigationCacheMode.Enabled`
  so the page instance survives nav away/back.
- **Purpose (casual voice):** "the up/down traffic this PC made over
  time, all apps combined, for whatever window you pick."

---

## 2. UX intent

History is the **aggregate-over-time** surface — chart-only, no
per-app drill (that's Per-App's job). This polish round upgrades it
from "two stacked default-style TextBlocks over a default-LiveCharts2
chart" to a coherent, scannable history view: a summary that reads as
identity-class data instead of an internal-jargon string
(`"N buckets   |   Up: X   |   Down: Y"`), grain-adaptive chart axes
that drop the `"/bucket"` storage-jargon and add a day-boundary cue
for multi-day windows, a chart subtitle in the
`"<bucket> · <window>"` shorthand inherited from App Detail, the
canonical history-class split between `disconnected` (pipe down) and
`error` (query failed), a centered Fluent `ProgressRing` for loading,
and visual cohesion between the summary and chart surfaces so the page
doesn't read as two flatly stacked Borders. Cards migrate to the
canonical metallic recipe so they match Dashboard / Per-App / App
Detail's visual hierarchy. Picker controls inherit Per-App's
window-shorthand convention. Chart chrome inherits the grain-adaptive
labelers + forgiving-hover behavior that App Detail's polish round
landed centrally in `ChartBuilder`.

---

## 3. Controls in scope

The page is a `ui:NavigationView`-hosted Page. The brief describes
controls by **type and purpose**; composition (layout rows, widths,
inter-control spacing, card arrangement) is Claude Design's work.

### Page header surface

- **Page title.** `ui:TextBlock` carrying the literal `"History"`.
  This is the page identity — a casual-user-facing nav label, not a
  marketing string.
- **Header explainer.** `ui:TextBlock` carrying the literal
  `"Aggregate up/down traffic across all apps in the selected window."`
  Whether it lives as a caption beneath the title, an eyebrow above
  the title, or some other treatment is OPEN (§8.4 Q3).
- **Status banner.** A `Border` that paints either a
  disconnected-state banner (pipe down) or an error-state banner (any
  other query failure) — see §4 for per-state copy and §11.3
  disconnected-vs-error split. Default `Visibility=Collapsed`.

### Window picker row

- **Window picker.** `ComboBox` over `WindowPreset.All`
  (`HistoryQueryClient.cs:117-126`) — five presets:
  `Last 1 hour / Last 24 hours / Last 7 days / Last 30 days /
  Last 90 days`. Default selection is index 1 (Last 24 hours).
  Items render with the **shorthand label**
  `1h / 24h / 7d / 30d / 90d` (Per-App / App Detail's locked
  convention — `WindowPreset` already carries the `Short` field).
  Each item AND the ComboBox selection display carries a WPF `ToolTip`
  with the long form. Picker width follows from the shorthand
  rendering.
- **Refresh control.** `ui:Button` carrying `ui:SymbolIcon
  Symbol="ArrowSync24"` (Per-App / App Detail's locked Fluent vocab —
  the ASCII text-only `"Refresh"` Content in current XAML is gone).
  Whether the button shows the icon alone or icon + `"Refresh"` text
  is part of Claude Design's composition.

### Summary surface

A surface carrying the at-a-glance answer to "what does this window
contain?" It MUST surface:

- **Aggregation grain** used by the chart (`result.GrainUsed` —
  `Samples` / `Hourly` / `Daily`).
- **Window total Up bytes** (humanized via `PerAppPage.FormatBytes`).
- **Window total Down bytes** (humanized same).

It MUST NOT surface the internal `"N buckets"` term — buckets are
storage-implementation jargon the chart already visualizes (§8.2 lock).

Whether the summary lives as a header row INSIDE the chart card
(Option A in findings #12) or as a SEPARATE card above the chart with
a chart-card title `"Total traffic"` (Option B) is OPEN (§8.4 Q1).
The data the summary carries is locked; the composition is not.

### Chart card

A `Border` carrying the traffic-over-time visualization. Whether the
summary lives inside this card (Q1 Option A) determines whether the
card also carries a header row up top.

The chart-card body contains:

- **Chart subtitle.** `ui:TextBlock Style="text.caption"`, Foreground
  inherits `text.secondary` from the Style. Copy is grain+window
  shorthand without the verbal-padding `"Showing "` prefix — e.g.
  `"per-minute detail · last 24 hours"`, `"2-hour buckets · last 7
  days"`, `"daily buckets · last 30 days"`, `"2-day buckets · last
  90 days"`. Generated by `ChartBuilder.DescribeView` (inherited from
  App Detail's polish; no new code).
- **Chart.** `lvc:CartesianChart`, `Background="Transparent"`,
  `LegendPosition="Top"`. Series shape switches by grain via the
  shared `ChartBuilder.BuildSeries`:
  - `Samples` grain (≤24h windows) → two `LineSeries<DateTimePoint>`
    (Up, Down). Forgiving hover via `GeometrySize=20` is inherited
    from `ChartBuilder` (App Detail's polish landed it centrally).
  - `Hourly` / `Daily` grain → two `StackedColumnSeries<DateTimePoint>`
    (Up = bottom of stack, Down = top).
- **No-data overlay.** Centered `TextBlock` rendered when both Up and
  Down point sets are empty. Copy:
  `"No traffic recorded in this window."` Foreground `text.secondary`.
  `IsHitTestVisible="False"` so it doesn't intercept chart hover.

Whether the card also carries the summary (Q1 Option A) or just the
chart subtitle (Q1 Option B with a `"Total traffic"` title above) is
OPEN.

### Loading affordance

A `ui:ProgressRing IsIndeterminate="True"` centered in the chart card
viewport during refresh, with a `text.caption` `text.secondary`
caption `"Loading…"` appearing after a 1s delay so fast refreshes
don't flash. There is exactly one data surface on this page (the
chart), so the per-surface vs page-level ambiguity that App Detail's
brief raised does NOT apply here — the ring goes in the chart card.
No shimmer (§8.1 global lock).

---

## 4. State coverage

States to render. Every state below MUST appear in the mockup.

### `state: default` (steady-state, connected, data flowing)

- Page header rendered with `"History"` title + the explainer copy in
  its (Q3) proposed placement.
- Status banner collapsed.
- Window picker at `24h`, shorthand label rendered, long-form tooltip
  visible on one expanded ComboBox item (mock the expanded state).
- Refresh button rendered with Fluent `ArrowSync24` icon per its
  composition.
- Summary surface filled — grain (e.g. `"Hourly"`), Up total (e.g.
  `"3.2 GB"`), Down total (e.g. `"18.4 GB"`). NOT `"N buckets"`.
- Chart populated with realistic series. Across the default + variant
  mocks, show at least one of each chart shape:
  - Samples grain over 1h or 24h → line series with forgiving-hover
    `GeometrySize=20` (markers invisible; hit area widened).
  - Hourly grain over 7d → stacked columns; subtitle reads
    `"2-hour buckets · last 7 days"` reflecting the Coalesce.
  - Daily grain over 30d or 90d → stacked columns; subtitle reflects
    bucket cadence (90d Daily reads `"2-day buckets · last 90 days"`
    per the Coalesce).
- Chart subtitle reflects grain + window in the inherited shorthand.
- Y axis labels render as nice-rounded rates with grain-adaptive
  suffix: `"20 MB/min"` (Samples), `"50 MB/hr"` (Hourly), `"4 GB/day"`
  (Daily). NOT `"/bucket"`.
- X axis labels render in grain-adaptive format: `HH:mm` (Samples),
  `MM-dd HH` (Hourly), `MM-dd` (Daily).
- Chart tooltip drawn in its active state on one mock so the opaque
  background + drop shadow + per-series row rendering is auditable.

### `state: loading`

- Status banner collapsed.
- Summary surface values render `"—"` placeholders (`Style="text.mono"`,
  Foreground `text.tertiary`) until first paint.
- Chart card body: centered `ProgressRing IsIndeterminate="True"` per
  the locked pattern. After 1s a caption `"Loading…"`
  (`Style="text.caption"`, Foreground `text.secondary`) appears
  beneath the ring. Fast refreshes (<1s) don't flash the ring.
- No skeleton-shimmer anywhere (§8.1 global lock).

### `state: empty` (window query succeeded; zero traffic)

- Status banner collapsed.
- Summary surface filled — grain (the server picked one, even for an
  empty window); Up renders `"0 B"`; Down renders `"0 B"`. (NOT
  em-dash — the answer is genuinely zero.)
- Chart card body: centered no-data overlay copy
  `"No traffic recorded in this window."`, `Style="text.body"`
  Foreground `text.secondary`.
- Chart subtitle still renders the grain+window shorthand from
  `DescribeView` (the query succeeded; we know which grain the server
  would have used).

### `state: disconnected` (named-pipe down — `HistoryQueryClient.IsConnectionLost`)

- Status banner visible:
  - Background `status.critical.background`, Foreground
    `status.critical`, `radius.control`, `padding=space.8`.
  - Copy: `"Service disconnected — last refresh stale."`
- Summary surface retains last-known values at `Opacity=0.6`.
- Chart retains last-known series at `Opacity=0.6` (NOT cleared —
  matches the history-class "preserve last known" pattern).

### `state: error` (any other query failure — NOT pipe-down)

- Status banner visible:
  - Background `status.caution.background`, Foreground
    `status.caution.text`, `radius.control`, `padding=space.8`.
  - Copy: `"Query failed ({ExceptionTypeName}): {ExceptionMessage}"`.
    Mock shows one realistic example, e.g.
    `"Query failed (SqliteException): database is locked"`.
- Summary surface + chart retain last-known data at `Opacity=0.6`.

> **No `warming` state.** History is a history-class surface — it
> queries SQLite via `HistoryQueryClient`, not the in-memory live
> aggregate. There is no fill window to surface.

### Dark theme

Per the template, ONE steady-state layout renders additionally in
**dark** theme so theme-swap behavior is auditable. The
`state: default` is the canonical pick (any grain — Samples or
Hourly preferred so both line and column series are auditable across
the light + dark deliverables, but a single dark mock is enough).

---

## 5. Tokens in scope

The constraints. Specific tokens are assigned by Claude Design during
composition.

### `surface.*`

- Page root: `surface.background` (Mica shows through).
- Summary card (Q1 Option B) and Chart card: `surface.card` (opaque).
  If Q1 lands on Option A (merged single card), that single card uses
  `surface.card`. Every text- or data-bearing card on this screen MUST
  be opaque — Mica + chart-label contrast + text-on-translucent fails
  AA otherwise.
- StatusBanner background: per-state — see `status.*` below.
- Loading-state placeholder cells (em-dash filler in summary values):
  no surface change; the em-dash sits on the same card surface, in
  `text.tertiary` foreground.

> **Precondition check.** The `surface.card` token must resolve to
> the opaque brand value (not the stock Wpf.Ui
> `CardBackgroundFillColorDefaultBrush`, which is the translucent
> variant). Dashboard / Per-App / App Detail polish rounds migrated
> their card surfaces; this brief assumes that migration reaches the
> History cards in the same implementation pass.

### `text.*`

Full type ramp available (`text.title` / `text.subtitle` / `text.body`
/ `text.body.strong` / `text.caption` / `text.eyebrow` / `text.mono`).

Per-screen constraints:

- **Numeric / digit-aligned values** (Up / Down byte totals, any
  numeric grain rendering) use `text.mono` for column alignment and
  to read as data, not prose.
- Chart subtitle is `text.caption` with `text.secondary` Foreground
  (inherited from the Style).
- If Q1 lands on Option B (separate chart-card title `"Total
  traffic"`), that title is `text.subtitle`.
- Page-identity TextBlock (`"History"`): `text.subtitle` with
  `FontFamily="{StaticResource font.display}"` (matches the
  Per-App / App Detail page-title rendering).
- Header explainer copy is `text.caption` `text.secondary` whatever
  placement Q3 lands on (eyebrow / caption / sibling).

### `accent.*`

- **No filled accent surfaces in this round.** No buttons, pills, or
  selection bars carry an accent fill on History. (Reminder:
  `accent.default` is NEVER a filled background; locked decision.)

### `status.*`

Paired bg/foreground per banner. Per-state copy in §4.

| State          | Background                   | Foreground             |
|----------------|------------------------------|------------------------|
| disconnected   | `status.critical.background` | `status.critical`      |
| error          | `status.caution.background`  | `status.caution.text`  |

### `border.*`

- All card surfaces (one card if Q1 Option A; two cards if Q1
  Option B): `border.card`, 1px. (Not the gradient
  `ControlElevationBorderBrush` in current XAML.)
- StatusBanner: no border (background alone carries the signal).
- Any divider inside a merged summary+chart card between the summary
  header row and the chart body (if Claude Design's composition uses
  one for Q1 Option A): `border.subtle`, 1px.

### `space.*`

- Page outer margin: `space.24` (mandatory).
- 4-based scale for all inter-element / inter-card / padding choices —
  Claude Design composes within the scale.

### `radius.*` (role tokens only)

- All card surfaces: `radius.card`.
- StatusBanner: `radius.control`.
- Window picker (Wpf.Ui ComboBox): `radius.control` (Wpf.Ui default).
- Refresh button: `radius.control` (Wpf.Ui default).
- Chart tooltip surface: `radius.control` (Wpf.Ui default for popups).

### Material / effect

- **Canonical card recipe applies.** Every data-bearing card on this
  page (the chart card, AND the summary card if Q1 lands on Option B)
  uses `metal.card` background + `edge.light` inset top catch-light +
  `border.card` 1px stroke + `shadow.card` elevation + `radius.card`
  corner. See `docs/design-system.md` §9 "Card surface — canonical
  treatment". This is the project-wide standing decision; History
  doesn't override it.
- **No live blur, no continuous animation, no animated sheen.** The
  metallic treatment is static gradients + a single
  `DropShadowEffect`.
- Chart tooltip background gets a `DropShadow(0, 4, 8, 8, ~38%
  black)` for backdrop separation when its tone matches the chart
  card behind it — see §6. Wiring inherited from `ChartTheming`.

### Chart tokens

Listed under §6.1.

### New tokens required by this brief

**None.** Every token referenced above exists in `DesignTokens.xaml`
and `colors_and_type.css` today. The summary + chart card migrations
(`CardBackgroundFillColorDefaultBrush` → `metal.card`;
`ControlElevationBorderBrush` → `border.card`) are pointer changes
against the existing token surface, not new tokens.

---

## 6. Chart-chrome — tokens AND behavior spec

History has a chart, so both 6.1 and 6.2 apply. **Almost every chart
spec on this page is INHERITED from App Detail's polish-round
investments in `ChartBuilder` and `ChartTheming`** — the brief
annotates the tokens and behaviors so they're explicit in the mock,
but the wiring already exists in shared code.

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

History does NOT consume `chart.wan` / `chart.local` — there's no
WAN-vs-Local viz on this screen.

Annotate these tokens by name on the chart. Wiring is code, not mock —
`Services/ChartTheming.cs` applies the paints in C# on construction
AND on `ApplicationThemeManager.Changed`. The History page's ctor
already calls `ChartTheming.Apply(HistoryChart)`
(`HistoryPage.xaml.cs:43`); paint changes inherit automatically.

### 6.2 Chart behavior spec

**The grain-adaptive behavior on History is identical to App Detail's
locked outcome — same window→grain matrix, same labelers, same
subtitle copy, same tooltip ergonomics.** History's
`HistoryQueryClient.GetTrafficHistoryAsync(window, TrafficGrain.Auto)`
call delegates grain selection to the server (same as App Detail);
the resolved grain returns via `result.GrainUsed`.

| Window | Grain | Series shape          | X-axis label | Y-axis label suffix | Subtitle (`DescribeView`)        |
|--------|-------|-----------------------|--------------|---------------------|----------------------------------|
| 1h     | Samples | `LineSeries`        | `HH:mm`      | `/min`              | `per-minute detail · last 1 hour`   |
| 24h    | Samples | `LineSeries`        | `HH:mm`      | `/min`              | `per-minute detail · last 24 hours` |
| 7d     | Hourly  | `StackedColumnSeries` | `MM-dd HH` | `/hr`               | `2-hour buckets · last 7 days`      |
| 30d    | Daily   | `StackedColumnSeries` | `MM-dd`    | `/day`              | `daily buckets · last 30 days`      |
| 90d    | Daily   | `StackedColumnSeries` | `MM-dd`    | `/day`              | `2-day buckets · last 90 days`      |

- **Y-axis labeler — grain-adaptive.** Inherited from
  `ChartBuilder.FormatYAxisLabel`. Rate values rounded to nice
  binary-aligned values (`"20 MB/hr"`, not `"19.6 MB/hr"`) with the
  grain-adaptive unit suffix from `ChartBuilder.YUnitSuffix(grain)`.
  Replaces today's `"/bucket"` storage jargon (§8.2 lock).
- **X-axis labeler — grain-adaptive.** Inherited from
  `ChartBuilder.FormatXAxisLabel`. `HH:mm` for Samples (intra-day
  resolution suffices since window ≤24h); `MM-dd HH` for Hourly
  (day-boundary cue + hour); `MM-dd` for Daily. Replaces today's
  universal `"MM-dd HH:mm"` (§8.2 lock).
- **Tooltip behavior.** Finding strategy is X-snap (cursor anywhere
  along the X width activates the nearest bucket; Y proximity
  ignored). `ChartTheming.Apply` already sets
  `FindingStrategy.CompareOnlyXTakeClosest`; History inherits.
  - **Tooltip layout.** Default LiveCharts2 v2 vertical layout
    (header + per-series rows). Per-series formatters render rates
    with the grain-adaptive unit so a tooltip row reads
    `"Up · 3.2 MB/min"` etc. Single-row dense form deferred (would
    require a custom `IChartTooltip<SkiaSharpDrawingContext>`).
  - **Tooltip hover sensitivity.** Forgiving hover via
    `GeometrySize=20` + `GeometryFill=GeometryStroke=null` on every
    `LineSeries`, inherited from `ChartBuilder.BuildSeries`.
  - **Tooltip background.** OPAQUE via `chart.tooltip.bg`. Drop
    shadow `(0, 4, 8, 8, ~38% black)` via
    `LiveChartsCore.SkiaSharpView.Painting.ImageFilters.DropShadow`
    on the `TooltipBackgroundPaint`, already wired in
    `ChartTheming.Apply`.
- **Legend behavior.** `LegendPosition="Top"` (current). Series Name
  strings are `"Up"` and `"Down"` (no `"/min"` / `"/hr"` / `"/day"`
  — the Y axis labeler owns the units; duplicating them in the
  legend is noise).
- **Series stroke / fill split.**
  - `LineSeries` (Samples grain): stroke from `chart.upSeries` /
    `chart.downSeries`; fill is a ~25% alpha variant of the same
    color for the area under the line.
  - `StackedColumnSeries` (Hourly / Daily grain): fill from the same
    tokens; stroke defaults to none. Up = bottom, Down = top.
- **Theme-flip re-paint.** Chart paints re-apply on
  `ApplicationThemeManager.Changed` via `ChartTheming.Changed`
  (`HistoryPage.xaml.cs:38`). Brief annotates tokens; wiring is in
  code.

---

## 7. Density assignment

**Intentionally n/a.** History has no `DataGrid` and no `ListView` —
the only data surface is the chart. The density rule (compact /
default / comfortable) applies to data lists; this screen has none.

---

## 8. Locks and open questions for this screen

### 8.1 Global locks

- **Loading = default Fluent `ProgressRing`**, not skeleton-shimmer
  (light-and-fast principle, design-system §2). Centered in the chart
  card viewport with a 1s-delayed `"Loading…"` caption per the
  inherited pattern in `_chart-implementation-notes.md` §Loading.
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HC activation. The mock does
  not draw HC variants; `HighContrast.xaml` collapses every semantic
  token onto `SystemColors.*` at runtime. Implementer verifies HC
  during the per-page verification gate (design-system §10).

### 8.2 Screen-specific outcome locks

What the findings settled at the *outcome* level. Composition is
Claude Design's; these outcomes are not re-litigable in the mock.

- **The summary MUST NOT surface `"N buckets"`.** Buckets are
  internal storage-implementation jargon. The chart visualizes them;
  the user does not need a literal count. The summary surface
  surfaces grain + Up bytes + Down bytes. Phrasing of any
  non-numeric label that complements the grain (e.g. how grain is
  conveyed — as a labeled value, as an eyebrow, as part of the chart
  subtitle only) is OPEN (§8.4 Q2).
- **Y-axis labels MUST NOT say `"/bucket"`.** Grain-adaptive `"/min"`
  / `"/hr"` / `"/day"` suffix per §6.2 is the locked outcome.
  Inherited from `ChartBuilder.FormatYAxisLabel` (App Detail's
  polish-round investment).
- **X-axis labels MUST distinguish day boundaries for multi-day
  grains.** Hourly grain over 7 days must not render the same
  `HH:mm` indistinguishably across days. Grain-adaptive
  `HH:mm` / `MM-dd HH` / `MM-dd` per §6.2 is locked. Inherited.
- **Chart subtitle drops the `"Showing "` prefix.** Render as
  grain+window shorthand (`"2-hour buckets · last 7 days"`), not a
  verbal-narrator sentence. Inherited from
  `ChartBuilder.DescribeView`.
- **Refresh control carries the Fluent `ArrowSync24` icon.** The
  ASCII text-only `"Refresh"` Content in current XAML is gone. The
  picker row inherits Per-App's icon-on-refresh convention.
- **Window picker renders shorthand items + long-form tooltip.**
  Same convention as Per-App / App Detail —
  `1h / 24h / 7d / 30d / 90d` in the items AND the selection
  display; per-item `ToolTip` carries the long form.
  `WindowPreset.Short` already exists for the binding.
- **Page carries an explainer.** Copy is locked:
  `"Aggregate up/down traffic across all apps in the selected window."`
  Placement (caption beneath, eyebrow above, somewhere else) is OPEN
  (§8.4 Q3).
- **StatusBanner SPLITS disconnected vs query-failed** per §11.3.
  Same split as Per-App / App Detail; opposite of Dashboard.
- **Card backgrounds + borders migrate to the canonical metallic
  recipe.** `CardBackgroundFillColorDefaultBrush` → `metal.card`;
  `ControlElevationBorderBrush` → `border.card`; `edge.light` +
  `shadow.card` + `radius.card` per §5. Chart tooltip inherits the
  opaque + drop-shadow treatment via `ChartTheming.Apply` (already
  wired in History's ctor).
- **Disconnected / error opacity-dim pattern is inherited.** On
  `disconnected` or `error`, the summary + chart Borders dim to
  `Opacity=0.6` (last-known data preserved, not cleared). Pattern
  matches App Detail / Per-App.

### 8.3 Boundary-case overrides of hard rules

**Intentionally n/a.** History fully aligns with every hard rule:

- §11 (discovery > ranking): aggregate surface, no list, no rows,
  no ranking. The rule doesn't have a target on this screen.
- §12 (honest attribution): aggregate surface, no per-PID byte
  cell. No svchost co-hosting decoration is needed.
- §13 (passive-only): no action affordances; the only button on
  the page is Refresh (a UI-state action, not a network/process
  action).
- Template §4's disconnected/error split: History takes the default
  split (not the merge); not an override.

### 8.4 Open design questions for Claude Design

The findings deliberately left these open. Each invites Claude
Design to propose variants the user picks from during iteration.

#### Q1. Summary + chart card composition

**Open:** the current implementation renders two stacked Borders —
a summary card (subtitle + summary line) and a chart card (chart +
no-data overlay) — with a flat visual rhythm and no header
hierarchy. The findings raised two structural options:

- **Option A (recommended in findings #12):** merge the summary
  into the chart card as an internal header row. Mirrors App
  Detail's chart-card composition (title + subtitle + chart). One
  card, internal hierarchy.
- **Option B:** keep summary and chart as separate cards; add a
  chart-card title `"Total traffic"` so the visual rhythm matches.
  Two cards, parallel hierarchy.

How should the page compose so the summary and chart read as one
coherent traffic view rather than two flat stacked Borders?

**Constraints:**
- Summary surface MUST carry grain, Up total, Down total — these
  three data points are visible regardless of composition.
- Summary MUST NOT carry `"N buckets"` (§8.2 lock).
- Chart subtitle (`text.caption`) MUST appear inside the chart card
  body, between any chart-card header element and the chart itself.
- Cards use the canonical metallic recipe (§5 material). Two-card
  composition uses the recipe on both; one-card composition uses it
  once.
- Page outer margin is `space.24` (mandatory).
- No new tokens.

**Variants:** propose 2 (one per option, plus a third hybrid if
Claude Design sees a stronger case).

#### Q2. Summary content scan

**Open:** the summary surface carries grain + Up + Down. Today these
render as a single TextBlock string
(`"{N} buckets   |   Up: X   |   Down: Y"`) with no scan structure.
How should the three values compose so the user reads them at a
glance — e.g. cells with eyebrow labels above mono values, or a
single value-driven line with the grain as a subtle prefix, or
something else? This question lives alongside Q1: the chosen
composition affects whether the summary is a horizontal strip
(Option A's header row) or a more vertical stack (Option B's
standalone card).

**Constraints:**
- The three data points (grain, Up bytes, Down bytes) MUST be
  visible without page scroll and MUST scan in one glance.
- Numeric values (Up, Down) use `text.mono` (§5 text constraint).
- The grain value — `Samples` / `Hourly` / `Daily` — is internal
  vocabulary the casual user has not seen before. Whether to
  surface it as those literal terms with a labeled eyebrow, or
  re-label it as user-facing vocabulary (e.g. "per-minute" /
  "hourly" / "daily"), or fold it into the chart subtitle and drop
  it from the summary entirely (since `DescribeView` already
  conveys it) is part of this question's scope.
- No `"N buckets"` (§8.2 lock).
- No new tokens.

**Variants:** propose 2–3, including at least one that drops the
grain from the summary entirely (relying on the chart subtitle to
convey it).

#### Q3. Page header treatment

**Open:** the page title (`"History"`) needs to coexist with the
explainer copy
(`"Aggregate up/down traffic across all apps in the selected
window."`). The current implementation is title-only — utilitarian
and unfriendly to a casual user. How should the page identify
itself so the title and explainer read as a coherent header
without competing for hierarchy?

**Constraints:**
- Page title is `"History"` rendered `text.subtitle` +
  `font.display` (locked — matches Per-App / App Detail
  page-title rendering).
- Explainer copy is locked verbatim (§8.2):
  `"Aggregate up/down traffic across all apps in the selected
  window."` Token is `text.caption` `text.secondary`. Placement is
  open.
- Candidates (not prescriptive): caption beneath the title;
  eyebrow above; inline sibling on a wider header; collapsed
  inside a tooltip or info-icon (only viable if Claude Design
  argues casual-user discovery isn't compromised). Propose
  whatever reads best.

**Variants:** propose 1–2.

---

## 9. Annotation work specific to this screen

- **New tokens this brief introduces.** **None.** Every token
  referenced is in the primer's token table and resolves correctly
  in `DesignTokens.xaml` and `colors_and_type.css` today.
- **Per-screen renames / repointings.** The brief migrates two raw
  Wpf.Ui keys to semantic tokens on every card on this page:
  - `CardBackgroundFillColorDefaultBrush` → `metal.card` (canonical
    card background per the metallic recipe).
  - `ControlElevationBorderBrush` → `border.card` (1px card stroke).
  These are pointer changes against the existing token surface, not
  value changes. The implementer treats them as a per-card
  `Background` / `BorderBrush` swap, paired with adding
  `edge.light` + `shadow.card` per §5 material.

---

## 10. Per-screen WPF translation gotchas

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages have infinite vertical extent. History does not host a
  DataGrid or ListView, so the `EnforceDataGridBounds`-style
  programmatic-cap pattern doesn't apply here. The chart card uses
  `MinHeight` + `RowDefinition.MinHeight` (next bullet) to anchor its
  vertical extent.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled` on the History nav-rail item** — the
  page instance survives nav away/back. `Loaded` does NOT refire on
  return. Anything that must re-measure on revisit hangs off
  `SizeChanged`, not `Loaded`. The current implementation runs
  `RefreshAsync` on `Loaded` only — which means a stale window
  selection persists between visits (the user comes back and sees
  pre-Refresh data). The implementer should consider whether History
  needs a `SizeChanged`-equivalent re-query or whether nav-back
  intentionally preserves last fetch (parking this — not a §8.2 lock,
  but flag for the implementer's awareness).
- **`Grid.RowDefinition.MinHeight` is needed** for the chart card's
  `*` row. The current XAML has Row 3 = `*` with the chart Border
  carrying `MinHeight="320"` and the inner chart carrying
  `MinHeight="280"`. Under window-height pressure the `*` row will
  shrink below the Border's MinHeight and clip the chart's X-axis
  labels + the card's bottom rounded corners. Set
  `RowDefinition.MinHeight="320"` on the `*` row so the Grid enforces
  the minimum at the row level (matches the App Detail pattern in
  `_chart-implementation-notes.md` §G).
- **SkiaSharp chart paints do NOT inherit `DynamicResource`.** Chart
  series colors AND chart-chrome paints (`chart.axis` / `chart.gridline`
  / `chart.axis.label` / `chart.tooltip.bg` / `chart.tooltip.text` /
  `chart.legend.text` / `chart.upSeries` / `chart.downSeries`) are
  applied in C# by reading the brush resource and feeding the
  underlying `Color` into an `SKPaint`. Re-applied on
  `ApplicationThemeManager.Changed` via `ChartTheming.Changed`
  (`HistoryPage.xaml.cs:38`). The brief annotates token names; wiring
  is in code.
- **Axis lifecycle — ONCE then mutate.** Per
  `_chart-implementation-notes.md` §3, axes are created ONCE in the
  ctor and mutated in place (`Labeler` / `MinStep` / `UnitWidth`)
  per refresh. `HistoryChart.XAxes` / `YAxes` arrays are NEVER
  reassigned after construction. The current implementation creates
  axes once but **does not mutate the labeler per grain** — it sets a
  fixed `MM-dd HH:mm` X labeler and a fixed `/bucket` Y labeler at
  ctor time. The polish-round implementer adds an
  `UpdateAxesForGrain(grain, preset)` helper called from
  `ApplyResult` after `result.GrainUsed` is known, mutating
  `_xAxis.Labeler` / `_yAxis.Labeler` / `_xAxis.MinStep` /
  `_xAxis.UnitWidth` per the matrix in §6.2. Pattern matches App
  Detail.
- **`ChartTheming.ApplyToSeries` after every `Series` assignment.**
  Per `_chart-implementation-notes.md` §A, `ChartTheming.Apply` in
  ctor runs BEFORE the first `Series` is assigned and its internal
  `ApplyToSeries` call no-ops over null. The page's `ApplyResult`
  reassigns `HistoryChart.Series = ChartBuilder.BuildSeries(...)` on
  every refresh; immediately after, call
  `ChartTheming.ApplyToSeries(HistoryChart.Series)` so the Up / Down
  series pick up the brand chart paints. The current implementation
  does NOT do this — the series render in LiveCharts2 defaults. Fix
  in the same pass.
- **`DrawMargin` for legend overlay.** Per
  `_chart-implementation-notes.md` §D, set
  `HistoryChart.DrawMargin = new Margin(80, 10, 10, 30)` so the
  legend shares space with the plot's top edge instead of pushing
  the plot down into its own band. `Left=80` fits the widest
  realistic Y label (e.g. `"500 GB/day"`); `Bottom=30` fits any
  X format.
- **`Margin / Padding cannot bind a `Double`-typed StaticResource.**
  `space.*` tokens are `sys:Double` (per
  `project_wpf_spacing_token_thickness.md`). XAML cannot resolve
  `Margin="{StaticResource space.24}"` because Margin expects
  `Thickness`. Write spacing values as literals
  (`Margin="24"`, `Padding="12,8"`) — token-aligned but not
  token-bound. The mock annotates them as `space.*`; the XAML uses
  literals.
- **`Wpf.Ui.NavigationView` paints `NavigationViewContentBackground`
  over the Page content area.** Already overridden globally to
  `Transparent` by the Dashboard polish round in
  `App.xaml.cs ApplyDirectLevelOverrides()`. History inherits;
  no per-page work needed.
- **Window picker shorthand item template** matches Per-App / App
  Detail's pattern. `WindowPreset` already carries a `Short` field
  (`HistoryQueryClient.cs:117`) — the item template binds `Short`
  for display, `Label` for the per-item `ToolTip`, AND the ComboBox's
  selection display also binds `Short`. The current XAML uses
  `DisplayMemberPath = nameof(WindowPreset.Label)` (the long form);
  the polish swaps to the templated short form. Pattern is identical
  to Per-App's polish — see Per-App brief §10.
- **Disconnected / error pattern.** Per
  `_chart-implementation-notes.md` "Disconnected vs error pattern".
  Currently History catches every exception under one branch
  (`StatusBannerText.Text = $"Query failed (...)"`). The polish-round
  implementer adds the `IsConnectionLost` filter as the first
  `catch` clause, painting `status.critical.background` +
  `"Service disconnected — last refresh stale."`; the existing
  generic catch becomes the second clause, painting
  `status.caution.background` + the query-failed copy. Both apply
  `SetDataOpacity(0.6)` to dim the data Borders.
- **Loading overlay pattern.** Per
  `_chart-implementation-notes.md` "Loading overlay pattern". The
  current implementation uses `Mouse.OverrideCursor = Cursors.Wait`
  only; replace with the chart-card-viewport overlay (centered
  `ProgressRing` + 1s-delayed `"Loading…"` caption) using the
  `DispatcherTimer` + `_isLoading` race-guard pattern.

---

## 11. Discovery > ranking — per-screen application

**Intentionally n/a.** History is an aggregate-over-time surface —
there is no list, no rows, no per-app drill, no ranking. The chart
is a single up/down stacked or line series over time; the rule has
no target on this screen. Drill-down (uncapped per-app rows) lives
on Per-App; History is the aggregate companion.

*Memory: `project_discovery_principle.md`.*

---

## 12. Honest attribution — per-screen application

**Intentionally n/a.** History aggregates `(BytesUp, BytesDown)`
across all apps in the window. There is no per-PID byte cell, no
per-process row, no svchost co-hosting decoration. The aggregate
sums every PID's bytes into the window's Up / Down totals; nothing
on this surface implies per-service attribution. The rule applies
to surfaces that surface per-PID data; History does not.

---

## 13. Passive-only — per-screen application

NO "Block" buttons, NO kill icons, NO quarantine affordances, NO
"Stop this app" actions, NO right-click action menus, NO
hover-revealed action buttons anywhere on this page.

The only interactive controls are:

- **Window picker** — selects the time window. UI-state change, not
  an action against any process or network surface.
- **Refresh button** — re-runs the query. UI-state action, not an
  action against any process or network surface.

There are no drill-down rows on this screen (no DataGrid, no
ListView), so the drill-affordance allowance documented in the
template does not apply here either. History is chart-only.

This is a CLAUDE.md invariant. **Passive-only is non-overridable.**

---

## 14. Performance budget — per-screen application

- **No shimmer / live blur / continuous animation** on any surface
  this round. Loading uses static `ProgressRing` (§8.1). The
  metallic card recipe is static gradients + a single
  `DropShadowEffect` — no pulsing, no animated sheen.
- **No new poll cadence.** History refreshes on `Loaded`,
  ComboBox `SelectionChanged`, and explicit Refresh-button click
  only. On-demand, not polled.
- **Chart-paint batching.** Chart paints re-apply on theme flip via
  a single `ChartTheming.Apply` call; one paint pass per flip, not
  per-series.
- **Inherited downsampling preserves budget.** 24h Samples uses
  `DownsampleAverage` (1440 → 240); 7d Hourly + 90d Daily use
  `Coalesce(factor: 2)`. Both preserve per-unit-time rate semantics
  (averages, not sums). The chart renders ≤240 points worst case;
  the GPU cost stays under budget.
- **Loading-overlay debounce.** 1s `DispatcherTimer` delay before
  the ring shows, so fast refreshes (1h queries) don't flash a ring
  + caption. Pattern inherited from `_chart-implementation-notes.md`
  §Loading.

---

## 15. Out-of-scope — features flagged for later

The items the findings doc sorted into the **Feature** bucket. The
mock must NOT design for them in this round.

- **F1. Grain override** (manually force Samples / Hourly / Daily).
  Today the grain is always `TrafficGrain.Auto`; the server picks
  by window span. Adding a user-facing override = new control + new
  IPC parameter. Deferred.
- **F2. Per-app overlay** (stacked-by-app History view). Adds
  multi-series viz; conflicts with the screen's "aggregate" purpose
  but is a natural extension. New capability. Deferred.
- **F3. WAN vs Local split** as a separate series or overlay.
  PRD §11 names this as a Daily Report element; arguably belongs
  here too. Adding the split = new categorical viz = feature.
  Deferred.
- **F4. Brush-to-zoom on chart.** Click-and-drag to select a
  sub-window. New interaction. Deferred.
- **F5. Custom window picker (free-form date range).** Same as
  Per-App F3. Deferred.
- **F6. Click a chart point to filter Per-App to that window.**
  Cross-page state coordination — new interaction model. Deferred.
- **F7. Export aggregate history to CSV.** Phase 5 owns export.

---

## 16. Chrome / cross-screen consequences

**Intentionally n/a.** History's polish round produces NO new
shared-code changes. Every chart-surface investment this brief
relies on — grain-adaptive labelers, forgiving-hover
`GeometrySize=20`, `DescribeView` shorthand, opaque tooltip with
drop shadow, `ChartTheming` paint propagation — was already
delivered by App Detail's polish round and lives in `ChartBuilder` /
`ChartTheming` today. History inherits these without modifying
them.

The bottom-bar rate mirror (introduced by Dashboard's polish) is
visible while History is shown but is not History's concern. Draw
it in the History mock for visual completeness; flag it
`chrome: MainWindow, not HistoryPage`.

No back affordance (History is a top-level nav-rail page).

---

## 17. Deliverables expected from Claude Design

The mockup hand-back MUST contain:

- Layouts for every state in §4 in the default (light) theme:
  `default`, `loading`, `empty`, `disconnected`, `error`.
- ONE steady-state layout additionally rendered in **dark** theme
  (`state: default` — Samples or Hourly grain preferred so a chart
  shape is auditable across themes).
- The `default` state mock specifically must show:
  - Page header with `"History"` title + explainer copy in its
    proposed Q3 placement.
  - Window picker at `24h` shorthand with the long-form tooltip
    visible on one expanded item.
  - Refresh button with Fluent `ArrowSync24` icon.
  - Summary surface filled per the Q1+Q2 chosen composition —
    grain, Up total, Down total (NOT `"N buckets"`).
  - Chart populated. Across the variants, show at least one
    Samples-grain chart (line series with forgiving hover) and at
    least one Hourly-or-Daily-grain chart (stacked columns). The
    90d Daily 2-day-buckets subtitle should appear in at least one
    variant so the Coalesce-aware copy is auditable.
  - Chart subtitle in the inherited shorthand
    (`"X · Y"`, no `"Showing "` prefix).
  - Y-axis labels with grain-adaptive `"/min"` / `"/hr"` / `"/day"`
    suffix. NOT `"/bucket"`.
  - X-axis labels in grain-adaptive format. NOT universal
    `"MM-dd HH:mm"`.
  - Chart tooltip drawn in its active state on one mock so the
    opaque background + drop shadow + per-series row rendering is
    auditable.
- Variant proposals for each open question in §8.4:
  - Q1 (summary + chart card composition) — 2 variants (Option A,
    Option B), plus a third hybrid if Claude Design judges one.
  - Q2 (summary content scan) — 2–3 variants, including one that
    drops grain from the summary entirely.
  - Q3 (page header treatment) — 1–2 variants.
- Token annotations using canonical dotted names per §9. Every
  card, text run, banner, chart token, and tooltip element labeled
  with its tokens. The metallic-recipe surfaces explicitly carry
  `metal.card` + `edge.light` + `border.card` + `shadow.card` +
  `radius.card` annotations.
- Layout hints:
  - Chart card `MinHeight: 320` + Row `MinHeight: 320` on the `*`
    row (annotation; XAML literal).
  - Inner chart `MinHeight: 280`.
  - `DrawMargin: (80, 10, 10, 30)` on the chart (annotation; code).
  - Page `scroll: none` — page itself does not scroll; the chart
    fills the residual viewport.
- Chart-chrome token names AND chart behavior spec called out
  (§6.1 + §6.2). Grain-adaptive axis labelers, forgiving
  `GeometrySize`, and `DescribeView` shorthand annotated as
  inherited from `ChartBuilder` (the shared service); ApplyToSeries
  re-call annotated as inherited from
  `_chart-implementation-notes.md` §A.
- Disconnected / error state: status banner annotated with the
  paired tokens per §5.status; data surfaces annotated as
  `Opacity=0.6` (inherited pattern).
- Loading state: chart-card-viewport ring annotated with the 1s
  delay caption pattern (inherited from
  `_chart-implementation-notes.md` §Loading).
- Bottom-bar rate mirror drawn (MainWindow chrome) so the screen
  reads in context; flagged as MainWindow-owned (not HistoryPage).
- Hand-off notes confirming the brief introduces NO new tokens and
  two pointer renames (`CardBackgroundFillColorDefaultBrush` →
  `metal.card`; `ControlElevationBorderBrush` → `border.card`),
  applied to every card on this page.

The pre-handback checklist
(`docs/design-briefs/_return-process.md`) restates these
deliverables as a paste-ready prompt for Claude Design to
self-verify against at session end.

---

## 18. Provisional / two-states

**Intentionally n/a.** History is a Group A built screen.
`HistoryPage.xaml` exists, ships data today, and the polish round
operates on the live XAML — there is no interim placeholder
treatment to design.

---

## 19. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the
dotted-token names are the contract.
