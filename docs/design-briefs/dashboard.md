# Claude Design brief — Dashboard

ZenVizor's Dashboard screen. Self-contained brief for a fresh Claude
Design session: paste this file together with `docs/claude-design-primer.md`
and produce an annotated mockup for every state listed in §4. The mockup
hand-off contract is in §9.

---

## 1. Screen identity

- **Screen name:** Dashboard.
- **XAML file:** `src/ZenVizor.Ui/Views/DashboardPage.xaml` (+ `DashboardPage.xaml.cs`).
- **IA placement:** first item in the left `ui:NavigationView` rail, icon
  `Symbol="DataPie24"`. Selected on startup. The casual user's entry
  point — what the window shows when they pop open ZenVizor from the tray.
- **Purpose (casual voice):** "what's happening on my network *right now*."

---

## 2. UX intent

The Dashboard is the live, glance-shaped surface — the user opens the
window, reads the totals and the trend in one look, and either closes
the window or drills into a specific app from another screen. This polish
pass turns it into something a non-technical person can read at a glance
and trust: a headline-rate row that answers "is anything moving?" without
parsing the chart, a chart that doesn't visibly jitter as throughput
changes scale, X-axis labels that say `"now"` instead of `HH:mm:ss`,
tooltips that snap to where the cursor is, and a talkers list that holds
its rhythm even when rows fall off (dimmed-row persistence, internal
scroll). Cards migrate to the opaque token family so text legibility is
no longer wallpaper-dependent on Mica. Chart series finally pick up the
brand violet/teal up/down paints. Nothing about the page should re-flow
or shimmer in the user's peripheral vision while they're reading it.

---

## 3. Controls in scope

The page is a `ui:NavigationView`-hosted Page. Outer `<Grid Margin="space.24">`
with **4 rows**: `Auto / Auto / * / *`.

### Row 0 — header

- `ui:TextBlock` page title, `Style="text.subtitle"`, copy `"Current activity"`.

### Row 1 — banner row (dedicated; one banner visible at a time, both
collapsed by default)

- `Border` warming-banner — `state: warming`.
- `Border` disconnected-banner — `state: disconnected` (transient and
  steady variants).
- Each is a status-banner component (see component vocab in the primer);
  `radius.control`, `space.8` padding, full-width below the header,
  `space.12` margin-top above the row's content.

### Row 2 — status card row (4-up)

- `Grid` with 4 equal columns (`* / * / * / *`), `space.12` between cards.
- Per card: `Border` (card) wrapping a `StackPanel`:
  - `ui:TextBlock` `Style="text.eyebrow"` (label, uppercased).
  - `ui:TextBlock` value — `Style="text.title.large"`; on the numeric
    cards (UPLOAD, DOWNLOAD, ACTIVE PROCESSES) the value control is
    `Style="text.mono"` overlaid on the title.large size (use the mono
    family at 24 px / SemiBold). On the WAN-vs-LOCAL card, the "value
    slot" is a horizontal stacked-bar visualization instead of a number.
  - `ui:TextBlock` `Style="text.caption"` (subline, optional context).

The four cards (left → right):

| # | Eyebrow            | Value slot                                         | Subline                                  |
|---|--------------------|----------------------------------------------------|------------------------------------------|
| 1 | `UPLOAD`           | total Up rate, `FormatRate`-humanized              | "trailing 2-second average"              |
| 2 | `DOWNLOAD`         | total Down rate, `FormatRate`-humanized            | "trailing 2-second average"              |
| 3 | `ACTIVE PROCESSES` | integer count of apps with non-zero rate this tick | "talking right now"                      |
| 4 | `WAN vs LOCAL`     | horizontal stacked bar (`chart.wan` / `chart.local`) | "WAN 73% · Local 27%" (example)        |

### Row 3 — chart card (residual height)

- `Border` (card) with `padding=space.16`.
- Card header `StackPanel Orientation="Horizontal"`:
  - `ui:TextBlock` `Style="text.subtitle"` copy `"Live rates"`.
  - **No trailing "Last 2 minutes" caption** — the relative X-axis
    labels (`-2m` … `now`) communicate the trailing window directly.
- `lvc:CartesianChart x:Name="RatesChart"` — `LegendPosition="Top"`,
  `Background="Transparent"`. Two `LineSeries<DateTimePoint>` (Up, Down).
  See §6 for paint tokens and §10 for the axis/tooltip behavior spec.

### Row 3 — talkers card (residual height, capped)

- `Border` (card) `MinHeight=140` (~4-row floor) and `MaxHeight=320`
  (~10-row visible cap). Outer `padding=0` (header strip and rows manage
  their own padding).
- Inner `Grid` with header strip + body:
  - Header strip `Border` `padding=space.12,space.8`, bottom border
    `border.subtle 1px`: a 5-column `Grid` (`* / 200 / 90 / 110 / 110`)
    of `ui:TextBlock`s `Style="text.body.strong"`, copy
    `App / Publisher / Signature / Up/s / Dn/s` (right-aligned on the
    two numeric headers). Each header carries a WPF `ToolTip` — see §10.
  - Body `ListView x:Name="TalkersList"` — `Background="Transparent"`,
    `BorderThickness=0`, `density: default` (8,4 item padding stays as-is).
    `ScrollViewer.VerticalScrollBarVisibility="Auto"` — when the list
    overflows the card's `MaxHeight`, the ListView scrolls **inside** the
    card. The Dashboard page itself does NOT scroll.
  - ItemTemplate: 5-column `Grid` matching the header, with columns:
    `ui:TextBlock` AppLabel `Style="text.body"`, Publisher
    `Style="text.body"` Foreground=`text.secondary`,
    SignatureStatus `Style="text.body"`, Up rate `Style="text.mono"`
    right-aligned, Down rate `Style="text.mono"` right-aligned. App and
    Publisher columns set `TextTrimming="CharacterEllipsis"`.

### Chrome that lives in MainWindow (not the page, but visible while
this page is shown — call out for the mockup)

- **Bottom-bar rate mirror**, right side of the existing status bar
  opposite the connection indicator. Layout: a 2-column `Grid` inside
  the bar's right slot. Content: `ui:SymbolIcon ArrowUp16` +
  `ui:TextBlock Style="text.mono"` (Up rate) + `space.12` gap +
  `ui:SymbolIcon ArrowDown16` + `ui:TextBlock Style="text.mono"`
  (Down rate). Visible on every screen, but shown in this mockup so the
  Dashboard's totals reconcile with the bar.

---

## 4. State coverage

States to render. Every state below MUST appear in the mockup.

### `state: default` (steady-state, connected, data flowing)

Headline cards filled. Chart shows two trailing lines, ~60 buckets across.
Legend at top of chart with two pills `Up` / `Down` (no `/s` suffix —
axis owns the units). Talkers list shows up to 10 active rows + up to ~10
dimmed "recently dropped" rows. Status row, chart, and talkers card all
visible without page scroll.

### `state: empty` (initial paint, before first 2-s tick lands)

Status cards: values render as `"—"` (em-dash), `Style="text.mono"`,
`Foreground=text.tertiary`. Sublines: hide (collapsed) until first data.
Chart card body: centered `ProgressRing` (default Fluent
`ui:ProgressRing IsIndeterminate="True"`), `space.12` below it a
`ui:TextBlock Style="text.caption" Foreground=text.secondary` copy
`"Waiting for first sample…"`. Talkers card body: same centered
`ProgressRing` + caption `"Waiting for first sample…"`.

### `state: loading`

Functionally identical to `state: empty` for Dashboard — the same
centered `ProgressRing` covers both first-paint and any in-flight
refetch where no data is on hand yet. No skeleton-shimmer (locked
decision §8). Mock both labels on the same surface treatment.

### `state: warming` (capture started, first flush bucket pending,
i.e. `snap.WindowSeconds <= 0` or `snap.Apps.Count == 0`)

- Banner row: WarmingBanner visible —
  background `status.warming.background` (NEW token, see §5 notes),
  foreground `status.caution.text`, copy
  `"Warming up — first flush bucket lands within ~5 s"`.
- Status cards: values continue to show `"—"` until first tick.
- Chart card: empty (no series), centered `ProgressRing` +
  caption `"Waiting for first sample…"`. (Mock notes the banner is
  redundant-but-helpful here — the user has both a top-of-page banner
  and a chart-area spinner pointing at the same thing.)
- Talkers card: centered `ui:TextBlock Style="text.body"`
  `Foreground=text.secondary` copy
  `"No active talkers in this window."` (Use this copy ANY time the
  talkers list is empty post-warming — see `state: empty (no talkers)`
  below.)

### `state: warming` → `state: empty (no talkers)`

After warming clears but the system is quiet (no app has logged traffic
yet in the trailing window):
- Status cards show `"0 B/s"` / `"0 B/s"` / `"0"` / WAN-LOCAL bar empty
  with subline `"No active traffic"`.
- Chart shows two flat lines at 0 across the trailing window.
- Talkers card body: centered `ui:TextBlock Style="text.body"`
  `Foreground=text.secondary` copy `"No active talkers in this window."`.

### `state: disconnected — transient` (1st failed cycle, "still
connecting")

- Banner row: DisconnectedBanner visible —
  background `status.caution.background`, foreground
  `status.caution.text`, copy
  `"Service disconnected (<FailureReason>) — retrying"`.
- Headline cards: values dim to `Opacity=0.6`, retain last known
  numbers (history is preserved, NOT cleared).
- Chart: keeps the last-known trailing window, no new tick appended;
  `Opacity=0.6` on the whole chart card content.
- Talkers list: same `Opacity=0.6` treatment (rows preserved, NOT
  cleared) — friction-item #11.

### `state: disconnected — steady` (>1 consecutive failed cycle)

Same layout as transient; banner swaps vocabulary to critical-class:
- Banner background `status.critical.background`, foreground
  `status.critical`, copy
  `"Service disconnected (<FailureReason>) — last refresh stale"`.

In both disconnected sub-states the bottom-bar rate mirror renders as
`"↑ —    ↓ —"` with `Foreground=text.tertiary` on the arrows and
values.

### `state: error`

**Intentionally merged with `state: disconnected`** on Dashboard. The
existing `ActivitySnapshotPoller` reports every exception via
`IsConnected: false` + `FailureReason = ex.GetType().Name`, and the
findings doc accepted this consolidation. No separate banner copy.

(History-class surfaces — Per-App, App Detail, History — DO split
disconnected from query-failed. Dashboard intentionally does not.
Locked decision §8.)

---

## 5. Tokens in scope

### `surface.*`

- Page root: `surface.background` (Mica shows through).
- Status cards (×4), chart card, talkers card: **all `surface.card`
  (opaque)** — not `surface.card.alt`. Data-bearing cards on Mica must
  be opaque or text contrast becomes wallpaper-dependent.
- Status banner backgrounds: per-state, see §4 and the `status.*` list
  below.

### `text.*`

- Page title `"Current activity"`: `Style="text.subtitle"`.
- Card eyebrow labels: `Style="text.eyebrow"` (uppercased per Style,
  Foreground is accent per Style).
- Card values: `Style="text.title.large"` for the numeric cards, but
  rendered in `font.mono` (apply `FontFamily="{StaticResource font.mono}"`
  on top of the Style for digit alignment; SemiBold weight).
- Card sublines: `Style="text.caption"`, Foreground inherits
  `text.secondary` from the Style.
- Chart card header `"Live rates"`: `Style="text.subtitle"`.
- Talkers column headers: `Style="text.body.strong"`.
- Talkers app cell: `Style="text.body"`, Foreground `text.primary`.
- Talkers publisher cell: `Style="text.body"`, Foreground
  `text.secondary`.
- Talkers signature cell: `Style="text.body"`.
- Talkers Up / Down rate cells: `Style="text.mono"`, right-aligned.
- Empty-state copy: `Style="text.body"`, Foreground
  `text.secondary`.
- Loading caption "Waiting for first sample…": `Style="text.caption"`,
  Foreground `text.secondary`.
- Em-dash placeholder values in empty cards: Foreground
  `text.tertiary`.

### `accent.*`

- Used by the eyebrow Style (accent foreground on uppercased label).
- **No filled accent surfaces** anywhere on this page in this round —
  `accent.fill` is not used. There are no buttons, pills, or selection
  bars on Dashboard. (Reminder: `accent.default` is NEVER a filled
  background; locked decision §8.)

### `status.*`

Paired bg/foreground per banner. See §4 for the per-state copy.

| State                       | Background                       | Foreground            |
|-----------------------------|----------------------------------|-----------------------|
| warming                     | `status.warming.background` (**NEW**, see §5 notes) | `status.caution.text` |
| disconnected (transient)    | `status.caution.background`      | `status.caution.text` |
| disconnected (steady)       | `status.critical.background`     | `status.critical`     |

Also: bottom-bar connection dot reads `status.connected` /
`status.warming` / `status.disconnected` per the chrome change tracked
in design-system §11 item 1.

### `border.*`

- Every card stroke: `border.card`, 1px.
- Talkers card header-strip bottom rule: `border.subtle`, 1px.

### `space.*`

- Page outer margin: `space.24` (mandatory, §5 of the brief template).
- Between header (row 0) and banner row: `space.12`.
- Between banner row and status cards (row 2): `space.12`.
- Between status cards: `space.12` (column gap).
- Between status row (row 2) and chart card (row 3): `space.16`.
- Between chart card and talkers card: `space.12`.
- Card inner padding: `space.16` on chart + status cards; talkers card
  has `padding=0` with the inner header strip carrying
  `space.12,space.8` and rows carrying `8,4` (default ListView density).
- Card header → body gap (chart card): `space.12`.
- Banner inner padding: `space.8`.
- Glyph → text gap (bottom-bar arrows → rates): `space.4`.

### `radius.*` (role tokens only)

- All cards (status ×4, chart, talkers): `radius.card`.
- Banner borders: `radius.control`.

### New tokens that need to be added (handoff notes — must land in
`DesignTokens.xaml` + `design-system.md` + `colors_and_type.css` before
the XAML implementation begins)

- **`status.warming.background`** — caution-class background, semantically
  named for the warming state so warming banners can be repointed away
  from the generic `SubtleFillColorSecondary`. Value: alias of
  `status.caution.background` (= `SystemFillColorCautionBackground` /
  `--status-caution-background`). Reason for the new token rather than
  just reusing `status.caution.background`: warming and "user-visible
  caution" are semantically distinct states even when their paint is
  identical, and the warming banner being repointable without touching
  every caution banner in the app is worth one alias token.

---

## 6. Chart-chrome tokens (REQUIRED — chart is present)

LiveCharts2 paints **none** of the axis / gridline / label / tooltip /
legend chrome from the UI theme. The Dashboard chart MUST specify all
of the following; the wiring lives in
`src/ZenVizor.Ui/Services/ChartTheming.cs` and re-applies on
`ApplicationThemeManager.Changed`.

| Token              | Use                                                       |
|--------------------|-----------------------------------------------------------|
| `chart.axis`       | Axis line stroke                                          |
| `chart.gridline`   | Gridline stroke (apply low alpha in code, ~0x0B)          |
| `chart.axis.label` | Axis tick labels (= `text.tertiary`)                      |
| `chart.tooltip.bg` | Tooltip surface — **OPAQUE** so contrast is stable        |
| `chart.tooltip.text` | Tooltip label text                                      |
| `chart.legend.text`| Legend pill labels                                        |
| `chart.upSeries`   | Up `LineSeries` stroke (brand violet, theme-swaps)        |
| `chart.downSeries` | Down `LineSeries` stroke (brand teal, theme-swaps)        |
| `chart.wan`        | WAN segment of the WAN-vs-LOCAL stacked bar (status card 4) |
| `chart.local`      | LOCAL segment of the WAN-vs-LOCAL stacked bar (status card 4) |

For the Up/Down line series, `Fill` (area-under-line) is a translucent
alpha variant (~25%) of the same stroke color. The tooltip background
must be opaque; transparency over Mica fails text contrast.

Annotate these tokens by name on the chart and the WAN/LOCAL bar. Wiring
is code, not mock.

---

## 7. Density assignment

- `TalkersList` ListView: **default** density (8,4 item padding stays
  as-is). The reconciled type ramp's larger body sizes work well at this
  spacing; do NOT apply `style.datagrid.compact`.
- No DataGrid on Dashboard. Compact density does not apply here.

---

## 8. Locked decisions relevant to this screen

Carry these into every state spec; do NOT re-litigate any of them in the
mock.

- **Loading = default Fluent `ProgressRing`**, centered in the surface
  that will hold data. Indeterminate when no progress fraction is known;
  add a `text.caption` `text.secondary` caption below
  ("Waiting for first sample…") since the wait may exceed ~1 s on a
  cold ETW pipeline. NO skeleton-shimmer anywhere — shimmer is
  continuous animation, pays no benchmark dividend under WPF, conflicts
  with the light-and-fast principle.
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HC activation. The mock does
  not draw HC variants; `HighContrast.xaml` collapses every semantic
  token onto `SystemColors.*` at runtime. Implementer verifies HC
  during the per-page verification gate (design-system §10).
- **Top 10 cap on talkers list is INTENTIONAL** and is the explicit
  exception to the discovery > ranking rule (see §11). Dashboard is a
  live, fixed-height glance surface explicitly framed as "top by
  current rate." It is NOT the drill surface — Per-App is. Do not
  remove the cap; do not annotate it as a friction point in the mock.
- **Disconnected vs query-failed is INTENTIONALLY merged on Dashboard.**
  Unlike Per-App / App Detail / History, the Dashboard's poller catches
  every exception under `IsConnected: false` with `FailureReason`. The
  brief carries this as a sub-state distinction only (transient vs
  steady) — see §4. No separate "query failed" banner copy on this
  screen.
- **Dimmed-row persistence in talkers list is by TIME, not rank.** A
  row that drops below the active-rate cap stays in the list at
  `Opacity=0.5` for ~30 s before disappearing. The list grows from 10
  active to up to ~20 rows (10 active + ~10 recent-dimmed). The
  internal scroll on the talkers card handles the overflow; the
  Dashboard PAGE never scrolls. This is aligned with discovery >
  ranking (coarsen by time, never by score).
- **Talkers list in disconnected state is dimmed, not cleared.**
  `Opacity=0.6` on the ListView while DisconnectedBanner is visible.
  Matches the chart-history "preserve last known" treatment.
- **Series legend Name strings are `"Up"` / `"Down"` (no `"/s"`)** —
  the Y axis labeler owns the units; legend duplicating them is noise.
- **MainWindow `MinHeight` bumps from 500 → 600** to give the new
  4-row Dashboard composition headroom at the smallest legal window
  size. This is a chrome change, listed in §3 above for context.
- **`ActivitySnapshotPoller` ownership moves from `DashboardPage` to
  `MainWindow`** so the bottom-bar rate mirror works on every screen.
  One poller per process, 2-s cadence unchanged. Chrome change, not a
  page change — see §3 above.

---

## 9. Annotation rules

The hand-off contract: mockup annotations reference design tokens by
**semantic name**. The full rules are in
`docs/design-mockup-template.md §1`. The non-negotiables:

- **Canonical dotted token names ONLY.** `surface.card`,
  `text.secondary`, `chart.upSeries`, `radius.card`, `space.16`.
  Never PascalCase. Never strip the dots.
- **NEVER use legacy CSS aliases** from
  `docs/design/colors_and_type.css` (`--fg1`, `--fill-card`,
  `--series-up`, `--accent`, `--space-3`, etc.). Those aliases exist
  for back-compat on the mock side ONLY; they do not cross the
  hand-off boundary. The crosswalk in the CSS file header documents
  which alias maps to which dotted token.
- **`--fill-card` is the translucent variant.** Mapping it to a
  data-bearing card would migrate the wrong surface and fail WCAG AA.
  Every card on Dashboard is `surface.card` (opaque) — the chart card,
  the 4 status cards, and the talkers card carry 100% of the page's
  data.
- **New tokens follow the pattern `<category>.<role>[.<modifier>]`**
  (dotted lowercase). The brief introduces ONE new token —
  `status.warming.background` — listed in §5. Any further token the
  mock needs MUST be named in the same pattern and listed in the
  hand-off notes.

---

## 10. Per-screen WPF translation gotchas

Per-screen reminders so the implementer doesn't trip on landmines the
design system already documents.

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages have infinite vertical extent. The talkers card's
  `MaxHeight=320` MUST be enforced programmatically on
  `Loaded` + `SizeChanged` (matching the existing `EnforceDataGridBounds`
  / `EnforceAppsGridBound` pattern on other pages) so the ListView
  inside virtualizes and scrolls instead of expanding past the visible
  area. The Dashboard PAGE never scrolls — that's the whole point of
  the row composition (status cards + chart always visible).
- **`NavigationCacheMode.Enabled`** on the Dashboard nav-rail item —
  the `DashboardPage` instance survives nav-away/back, so `Loaded`
  does NOT refire on return. The talkers-card bounds enforcement and
  any other re-measure logic must hang off `SizeChanged`, not
  `Loaded`.
- **SkiaSharp chart paints do NOT inherit `DynamicResource`.** Chart
  series colors AND chart-chrome paints (`chart.axis`,
  `chart.gridline`, `chart.axis.label`, `chart.tooltip.bg`,
  `chart.tooltip.text`, `chart.legend.text`) are applied in C# by
  reading the brush resource and feeding the underlying `Color` into
  an `SKPaint`. Re-applied on `ApplicationThemeManager.Changed`. The
  brief annotates the token names; wiring is in code
  (`Services/ChartTheming.cs` + `Services/ChartBuilder.cs`).
- **Chart Y-axis behavior (anti-jitter):** Set `Y.MinLimit = 0`.
  Compute the upper bound as the next "nice" round value in the
  current unit (round to 5 / 10 / 20 / 50 / 100 / 200 / 500 / 1000…
  depending on magnitude). Force the tick step so labels land on
  predictable round values (e.g. `0`, `5 KB/s`, `10 KB/s`, `15 KB/s`,
  `20 KB/s`). Smooth the upper bound across ticks (EWMA-ish) so the
  band breathes rather than jitters frame-to-frame. Tradeoff (locked
  in findings): when traffic is in MB/s range, small spikes flatten
  near 0 — correct behavior; peaks need headroom, casual users want
  predictable axis values.
- **Chart X-axis behavior (relative labels):** Switch tick labels to
  relative format — `"-2m"`, `"-90s"`, `"-1m"`, `"-30s"`, `"now"` —
  with tick `MinStep` set so ~6 labels render across the 2-min window
  (≈30-s spacing + endpoints). Sample cadence (2 s) is unchanged;
  ONLY label rendering changes. Absolute wall-clock is preserved via
  the tooltip (next bullet).
- **Chart tooltip:** Tooltip finding strategy switches to X-snap (cursor
  anywhere along the X width activates the tooltip for the nearest
  bucket; Y proximity ignored — the LiveCharts2 enum that means
  "nearest by X only"). Tooltip content for the active bucket carries
  BOTH relative and absolute time in one row:
  `"-90s · 23:34:10 · Up 12 KB/s · Dn 18 KB/s"`. Tooltip background
  MUST be opaque via `chart.tooltip.bg`. Apply finding strategy as a
  direct property assignment on `CartesianChart`; tooltip paint wiring
  goes through `ChartTheming.Apply`.
- **Chart series legend pills:** Legend lives at top of chart
  (`LegendPosition="Top"`), default LiveCharts2 layout. Pill text
  paints with `chart.legend.text`. Series Name strings are `"Up"` and
  `"Down"` — no `/s`.
- **No card-internal `MinHeight` on the Dashboard chart card.** The
  row uses `Height="*"` and competes with the talkers card for residual
  space. At MainWindow's new `MinHeight=600`, the chart's residual
  drops to ~155 px — cramped but readable. Setting `MinHeight=180` on
  the chart (the convention on App Detail / History) would break the
  composition at the minimum window size.
- **Bottom-bar rate mirror lives in `MainWindow.xaml`** alongside the
  service-status indicator, NOT in `DashboardPage.xaml`. It binds to
  `MainWindow`-scoped observables fed by the
  `ActivitySnapshotPoller` (which the chrome change moves up from
  `DashboardPage`).
- **Column-header tooltips on the talkers list** use WPF `ToolTip`
  attached on each header `TextBlock`. Centralize the strings as a
  single `ResourceDictionary` block (e.g.
  `Resources/Strings.Tooltips.xaml`) so future localization is one
  swap. Per-column copy:
  - Signature: `"Signed = publisher verified offline (no revocation
    check). Unsigned = no signature present. Invalid = signature
    failed verification. Unchecked = verification skipped."`
  - Publisher: `"'(unknown)' means no Authenticode signer is
    present."`
  - Up/s, Dn/s: `"Average B/s over the last 2-second polling cycle."`

---

## 11. Discovery > ranking constraint (HARD RULE)

Never cap drill-down lists by score / bytes. A stealthy malicious
process won't be in any top-N. If a surface has too much data, coarsen
by **time** (rollups, downsample), not by **rank**. No "see more"
gates, no ellipsis truncation that hides rows.

**Dashboard is the explicit boundary-case exception.** The "Top 10"
talkers list IS a rank cap, and that's deliberate: Dashboard is a live,
fixed-height glance surface explicitly framed as "top by current rate."
It is not the drill-down surface. Per-App is the drill-down surface and
must have NO cap.

The talkers list's dimmed-row persistence (item 19 / §3) is
discovery-aligned within this boundary: rows that fall out of the top
N stay visible at `Opacity=0.5` for ~30 s — coarsening by **time**,
not by rank. Combined with internal scroll, the talkers list grows
from 10 active to ~20 rows (10 active + ~10 recent-dimmed) without
hiding anything behind a "see more" gate.

---

## 12. Honest attribution constraint (HARD RULE)

Don't visually imply precision we lack:

- When `svchost.exe` hosts multiple services, the talkers row's App
  cell renders as `"svchost.exe [Service1, Service2, Service3]"`
  (the `[bracketed]` list comes from the `TalkerRowViewModel`'s
  `HostedServices` projection). The Up/Down rate cells report the
  PID's byte total — **do NOT split bytes across co-hosted services**.
- The bracketed-services list visually distinguishes "PID with one
  service" from "PID hosting many services" without implying
  per-service byte attribution. No icon or color treatment is needed
  beyond the bracket convention.
- Traffic from DLL injection / LOLBins (`rundll32`, `regsvr32`,
  `mshta`, `powershell`) attributes to the host process. This is a
  known documented boundary. The Dashboard does not pretend otherwise
  — no asterisk, no caveat icon, no UI hint about injection.

---

## 13. Passive-only constraint (HARD RULE)

ZenVizor observes; it never blocks. NO "Block" buttons, NO kill icons,
NO active-action affordances anywhere on the Dashboard. Talkers list
rows are presentational only — no row-level action menu, no
right-click "block this app," no hover-revealed action buttons.

Future drill-down may surface a passive "View details" navigation
(currently flagged for later — see §15 Feature F2), but never an
active intervention.

This is a CLAUDE.md invariant. A Claude Design session must not
quietly add a "Block" button to a row.

---

## 14. Performance budget reminder

Idle CPU < 1%, working set < ~80 MB, no per-event DB writes. The mock's
design proposals must not regress this. Specifically for Dashboard:

- **No shimmer.** Loading uses static `ProgressRing`. No continuous
  animation anywhere on the page.
- **No live blur, no acrylic backdrop on text cards.** All cards are
  static `surface.card` (opaque). Real Acrylic is reserved for OS
  surfaces only.
- **No glow / sheen on the headline cards.** If the mock proposes a
  metallic/brushed-steel treatment on the status row, it must be a
  static `LinearGradientBrush` + 1px stroke + single `DropShadowEffect`
  composited once (~free). No animated sheen sweeps.
- **No new poll cadence.** The existing 2-s `ActivitySnapshotPoller`
  cadence is unchanged. The chrome change moves the poller's owner
  from page to window scope; cadence and CPU cost are identical.
- **Chart paint changes batch.** `chart.UpdateLayout()` runs once after
  a full paint rebuild on theme flip, not per-axis. Implementer detail,
  not mock content.

---

## 15. Out-of-scope — features flagged for later

Items the findings doc sorted into the **Feature** bucket. The mock
must NOT design for them in this round. Listed here so a later phase
has the queue ready when the features are scheduled.

- **F1. Remove the "Top 10" cap on the talkers list.** Explicitly NOT
  a feature — the cap is the intended boundary-case decision (see §11).
  Listed here only so future iterations don't try to "fix" it as
  drift. Per-App is the drill surface; that's where uncapped discovery
  lives.
- **F2. One-click bridge from a Dashboard row to App Detail.** Natural
  follow-on for the "background tool I check in on" flow, but a new
  interaction surface (selection + navigation contract). Not
  presentation polish.
- **F3. Click-to-pin on the live chart** (freeze a bucket for extended
  study). X-snap (§10) solves the "precise cursor" complaint; click-
  to-pin is the right answer for >30 s hovers but introduces a state
  machine (pinned-vs-live, visual cue for pinned mode,
  click-elsewhere-to-unpin). Feature-class.
- **F4. User-controlled poll cadence.** Settings concern (Phase 6).
- **F5. Inline search/filter on the talkers list.** Adds filter
  capability — feature.
- **F6. Per-axis-label tooltip overlay** (transparent WPF elements
  tracking the chart's SkiaSharp label band so hovering `"-90s"` pops
  `"23:34:10"`). Considered and rejected for polish — the overlay
  tracking across chart resize is fragile, and the chart-area tooltip's
  dual-time format covers the same user need from the chart body.
- **F7. Separate "recent" table** as alternative to dimmed-row
  persistence. New layout zone, more vertical space, two lists for the
  user to scan. The dimmed-row approach (item 19) covers the same need
  within a single visual zone.

---

## 16. Deliverables expected from Claude Design

The mockup hand-back MUST contain:

- Layouts for every state in §4 (`default`, `empty`, `loading`,
  `warming`, `warming → empty (no talkers)`, `disconnected — transient`,
  `disconnected — steady`). The `error` state is intentionally merged
  with disconnected on Dashboard and does NOT need a separate layout.
- Token annotations using canonical dotted names per §9. Every box,
  border, text run, and chart element labeled with its tokens.
- Density tags on the talkers ListView (`density: default`).
- Layout hints (`MinHeight`, `MaxHeight`, `scroll: pane`) wherever they
  matter — specifically: talkers card `MinHeight=140 / MaxHeight=320`,
  `scroll: pane` (inside the talkers card), page `scroll: none`.
- All chart-chrome token names called out on the chart and the WAN-vs-LOCAL
  bar (§6).
- Hand-off notes listing the one NEW token introduced by this brief —
  `status.warming.background` — and the rationale (§5).
- Notes on chrome changes visible while Dashboard is shown (bottom-bar
  rate mirror) — drawn in the mock for reconciliation with the
  Dashboard headline rates, but flagged as "MainWindow chrome, not
  DashboardPage."

---

## 17. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the dotted-token
names are the contract.
