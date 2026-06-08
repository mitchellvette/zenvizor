# Pre-brief — App Detail (findings, Group A)

Grounded walk of `src/ZenVizor.Ui/Views/AppDetailPage.xaml` +
`AppDetailPage.xaml.cs`. The busiest screen — two side-by-side grids,
a chart card, and a summary card all on one Page.

---

## 1. Purpose & IA placement

- **Purpose:** drill-down for one app. Header + summary card + traffic-
  over-time chart + side-by-side Connections / Recent Sessions tables.
- **IA placement:** **not** a nav-rail item. Reached only via Per-App
  double-click → `NavigationView.Navigate(typeof(AppDetailPage),
  row.AppId)`. Back button uses `NavigationView.GoBack()`.

## 2. What is literally on it today

Root: `<Grid Margin="24">` with 5 rows
(`Auto / Auto / Auto / * / *`).

### Row 0 — header

- `<StackPanel Orientation="Horizontal">`:
  - `<ui:Button Content="&lt; Per-App" Margin="0,0,16,0"
    Click="OnBackClick">`.
  - `<ui:TextBlock x:Name="HeaderText" FontTypography="Subtitle"
    Text="App detail" VerticalAlignment="Center">`. Text filled in
    code via `ApplyDetail` as `"{ImageName} (app id {AppId})"`
    (`AppDetailPage.xaml.cs:131`).

### Row 1 — picker

- `<StackPanel Orientation="Horizontal">`:
  - `<TextBlock Text="Window:" Margin="0,0,8,0">`.
  - `<ComboBox x:Name="WindowCombo" Width="180">` — same five
    `WindowPreset.All` entries as Per-App, default index 1 (last 24h).
  - `<Border x:Name="StatusBanner" Visibility="Collapsed">` — same
    caution-class banner pattern as Per-App.

### Row 2 — summary card

- `<Border CornerRadius="6" Padding="12,8" Margin="0,0,0,12">` with
  the standard card background/border pair.
- Inner `<StackPanel x:Name="SummaryStack" Orientation="Vertical">`:
  - `<TextBlock x:Name="SummaryLine1">`. Filled in code as
    `"Publisher: {pub}   |   Signature: {status}{user-writable tag}
    |   Grain: {grain}"` (`:132-138`).
  - `<TextBlock x:Name="SummaryLine2"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}"
    Margin="0,4,0,0">`. Filled as
    `"Path: {path}   |   Up: {bytes}   |   Down: {bytes}"`
    (`:139-144`).

### Row 3 — chart card

- `<Border CornerRadius="6" Padding="8" MinHeight="220">` with the
  standard card background/border pair.
- Inner Grid, 3 rows (`Auto / Auto / *`):
  - Row 0: `<ui:TextBlock Text="Traffic over time">`.
  - Row 1: `<TextBlock x:Name="ChartSubtitle"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}">`.
    Filled by `ChartBuilder.DescribeView`
    (e.g. `"Showing per-minute detail over the last 24 hours."`).
  - Row 2: a Grid containing
    `<lvc:CartesianChart x:Name="SeriesChart" Background="Transparent"
    MinHeight="180" LegendPosition="Top">` overlaid with
    `<TextBlock x:Name="NoDataOverlay" Visibility="Collapsed"
    HorizontalAlignment="Center" VerticalAlignment="Center"
    Foreground="{DynamicResource TextFillColorSecondaryBrush}"
    Text="No traffic recorded in this window."
    IsHitTestVisible="False">`.

### Row 4 — side-by-side grids

- `<Grid Margin="0,12,0,0">` with two equal columns (`* / *`).
- **Connections card** (left, `Margin="0,0,6,0"`,
  `Background={DynamicResource CardBackgroundFillColorDefaultBrush}`):
  - `<ui:TextBlock Text="Connections (endpoints)"
    Margin="12,8,0,4">`.
  - `<DataGrid x:Name="ConnectionsGrid">` virtualized; 5 columns:
    `Proto` (60), `Address` (2*), `Port` (60), `Up` (90), `Down` (90).
- **Recent sessions card** (right, `Margin="6,0,0,0"`):
  - `<ui:TextBlock Text="Recent sessions" Margin="12,8,0,4">`.
  - `<DataGrid x:Name="SessionsGrid">` virtualized; 5 columns:
    `Session` (80), `PID` (70), `Start` (*), `End` (*), `Services` (*).

## 3. Current behavior

- **Init / refresh:** ctor wires axes + theming + `DataContextChanged`
  → `OnAppIdReceived` (unpacks `AppId` from `int`/`long` DataContext).
  `Loaded` → `EnforceDataGridBounds()` + `RefreshAsync`.
  `SizeChanged` → `EnforceDataGridBounds()` (`:48-55`).
- **Bound enforcement:** `EnforceDataGridBounds` sets each
  DataGrid's `MaxHeight = Math.Max(200, (window.ActualHeight - 220) /
  2)` (`:64-71`). Both grids share the residual vertical space split
  in half. Without this both DataGrids materialize every row.
- **Parallel queries:** `RefreshAsync` runs
  `GetAppDetailAsync` and `GetConnectionsAsync` concurrently on a
  single shared pipe (StreamJsonRpc supports concurrent in-flight
  requests). `Task.WhenAll` joins them (`:108-115`).
- **Chart series style:** `ChartBuilder.BuildSeries(grain, …)` returns
  `LineSeries<DateTimePoint>` for `Samples` grain (≤24h windows),
  `StackedColumnSeries<DateTimePoint>` for `Hourly` / `Daily`
  (`ChartBuilder.cs:26-67`). Up = bottom of stack, Down = top.
- **Aggregation:** raw `detail.Series` entries are accumulated into
  `SortedDictionary<bucketStartMs, bytes>` then projected to
  `DateTimePoint` lists and downsampled via
  `ChartSeriesDownsampler.Downsample` (`:146-160`). Server returns
  per-bucket up/down rows; client buckets up + down separately and
  hands two parallel point arrays to `ChartBuilder`.
- **`NoDataOverlay` shown** when both `upPoints` and `downPoints` are
  empty.
- **Loading:** `Mouse.OverrideCursor = Cursors.Wait` only.
- **Error:** same `StatusBanner` pattern as Per-App.
- **Theme:** `ChartTheming.Apply(SeriesChart)` in ctor + on theme
  flip. Axis labels / separators / legend repaint.

## 4. Data-presentation reality

- **Header:** `"{ImageName} (app id {AppId})"`. The `(app id 42)`
  suffix is implementation detail — useful for debugging, irrelevant
  to a casual user.
- **Summary line 1:** pipe-separated triplet
  `"Publisher: {pub}   |   Signature: {status}{user-writable tag}   |
  Grain: {grain}"`. The user-writable tag is the literal string
  `"  [user-writable path]"` appended inline after status, with no
  visual elevation. This is a security-relevant signal (it's the
  first-customer alert condition) painted as a bracketed footnote.
- **Summary line 2:** `"Path: {path}   |   Up: {bytes}   |   Down:
  {bytes}"`. Path renders as default proportional text — paths read
  badly in proportional fonts.
- **Chart X-axis labeler:** `"HH:mm"` only — no date qualifier. For a
  Hourly grain over 7 days, the same `HH:mm` repeats every day with
  no day boundary cue.
- **Chart Y-axis labeler:** `$"{FormatBytes((long)v)}/bucket"` — the
  string `/bucket` is internal terminology. A bucket is a minute for
  Samples grain, an hour for Hourly, a day for Daily.
- **ChartSubtitle:** `ChartBuilder.DescribeView(grain, preset)` →
  e.g. `"Showing per-minute detail over the last 24 hours."` The
  `"Showing "` prefix is verbal padding.
- **Connections grid:** binds `Protocol`, `RemoteAddress`,
  `RemotePort`, `UpText`, `DownText`. **`RemoteClass` (Local / Wan) is
  populated on the view-model but NOT bound to a column** — the data
  exists, nothing renders it.
- **Sessions grid:** binds `SessionId`, `Pid`, `StartText` /
  `EndText` (formatted `"yyyy-MM-dd HH:mm"` local), `HostedServices`
  (raw comma-separated string from server). End column shows
  `"(running)"` for sessions still alive.
- **All DataGrid columns** use default proportional digits, no
  `TextTrimming`, no `AlternatingRowBackground`.

## 5. State coverage today

| State | Handled today | Notes |
|---|---|---|
| empty | partial | `NoDataOverlay` in chart card. **Connections grid has no empty state.** **Sessions grid has no empty state.** |
| loading | partial | Wait cursor only — no spinner. |
| warming | n/a | History surface. |
| disconnected | merged with error | Same `IsConnectionLost` blind spot as Per-App. |
| error | yes | `StatusBanner` `"Query failed (<Type>): <msg>"`. |

## 6. Friction list (paired with proposed direction)

This is the busiest screen; expect the longest list.

1. **Summary card is two long pipe-separated strings.** Lines wrap
   ungracefully on narrow windows; field labels (`Publisher:`,
   `Signature:`, `Grain:`, `Path:`, `Up:`, `Down:`) are inline. The
   eye has no anchor — there's no visual separation between label
   and value, and no consistent vertical alignment for scanning
   between fields. The summary card is meant to be the at-a-glance
   identity block for the app but currently reads as two paragraphs.
   → **Open design problem for Claude Design.** This finding
   documents WHAT is broken; the right layout (labeled grid,
   stacked rows, two columns, key-value chips, etc.) is for the
   Claude Design pass to determine. Specify in brief as a
   "scannable identity block" problem, not a prescribed layout.
   Tied to items 2, 3, and 4 — user-writable framing, path
   rendering, and AppId placement all live in this card and want
   to be solved together.
2. **`is_user_writable_path` is buried as a bracketed footnote AND
   has no user-facing explanation.** Two coupled problems in one
   finding:
   - **(a) Visual elevation.** The literal string
     `"  [user-writable path]"` is appended inline after Signature
     status in body text, with no visual elevation. This is the
     alert-condition signal — *unsigned + user-writable + has
     connections* is the MVP's first real alert per
     `zenvizor-sprint-plan.md:250`. Burying it in inline footnote
     text underplays a high-importance signal.
   - **(b) Context / meaning.** Even with visual elevation, the
     casual user doesn't know what "user-writable path" means or
     what to do with the information. Critically: user-writable
     on its own is NOT a red flag. Chrome, VS Code, and Discord
     are all signed binaries running from user-writable locations
     (`docs/phase-2-verification.md:225`). The signal that matters
     is the *combination*: unsigned + user-writable + active
     connections. Phase 4's filter design already tested the
     user-facing translation — "personal folders" (user-writable)
     vs "system folders" (system-protected) per
     `docs/phase-4-filter-recommendations.md:51`. Without the
     plain-language framing AND the combination logic, surfacing
     the flag prominently risks crying wolf on benign signed apps.
   → **Open design problem for Claude Design**, tied to item 1.
   The card needs both: a presentation that flags the
   high-importance combination without false alarms on the benign
   case, AND a way to convey what "user-writable" / "personal
   folder" means and why it matters here (tooltip on a chip,
   inline help icon, companion plain-language line, contextual
   explainer that only appears in the alert combination — many
   workable directions). Don't prescribe; let Claude Design
   propose.
3. **Path is rendered in proportional text.** Paths are code-like —
   characters per row matters, slashes need column alignment, the
   eye tracks better in a monospaced face.
   → Bind path's value to `Style="{StaticResource text.mono}"`.
   **Path MUST NEVER be truncated.** App Detail is the furthest
   drill-down state in the application — it is the surface where
   everything about an app is supposed to be visible. Hiding part
   of the path here (ellipsis, char-clipping, "hover to see the
   rest") defeats the purpose of the screen. Whatever layout the
   summary card lands on (item 1), the path needs enough room to
   render in full at the page's minimum supported width — wrap
   across multiple lines if necessary; allow horizontal scroll
   inside a bounded container as a last resort; but do NOT
   ellipsize. The presentation problem (fitting a long path
   without making the card visually ugly) is part of item 1's
   design exploration.
4. **Header surfaces `(app id {AppId})` too prominently.** The
   `(app id 42)` suffix renders as part of the `Subtitle`-styled
   header text. It's useful for support / debugging but visually
   competes with the actual app name and creates user confusion
   ("what is app id and why does it matter?").
   → Drop from the visible header. The AppId still needs a
   logical home somewhere on this page — it's the canonical
   handle for the record and support contact will ask for it —
   but tucking it into a tooltip on the header hides it from a
   user who needs to read it off the screen, and a tooltip on a
   non-interactive title block isn't a discoverable affordance
   anyway.
   → **Open design problem for Claude Design**, tied to item 1.
   Find a placement where the AppId is visible without ambiguity
   about what it is. Candidates worth Claude Design exploration
   (not prescriptions): a labeled `app_id: 42` cell inside the
   summary card; a small footer-style metadata line; a
   copy-to-clipboard chip. Whatever treatment lands, it must be
   *labeled* so the user knows it's a record identifier, not a
   piece of app metadata.
5. **Back button is text-only AND shares a row with the page
   header.** Two coupled problems:
   - **(a) ASCII chevron in a Fluent context.** `"< Per-App"`
     uses a literal `<` character; the Fluent vocabulary on this
     app uses `ChevronLeft20` / `ArrowLeft24` from `ui:SymbolIcon`.
   - **(b) Row-sharing hierarchy confusion.** The back button
     sits in the same horizontal `StackPanel` as `HeaderText`
     (the app name). This conflates two kinds of information — a
     navigation control and a page identity label — and creates
     an unclear hierarchy (which element is the "page title"?).
     On narrow windows the back button shifts the header right;
     on wide windows the back button and header drift apart with
     no visual relationship between them.
   → Replace the ASCII chevron with `ui:SymbolIcon
   Symbol="ChevronLeft20"` for the icon part. **The row-sharing
   hierarchy is an open design question for Claude Design** —
   does the back affordance belong above the header on its own
   row (breadcrumb-style), inline with reduced visual weight
   (caption-styled link), or somewhere else? Don't prescribe;
   specify the problem and let the design pass solve it.
6. **Chart card title + subtitle are two stacked TextBlocks at
   default styles.** "Traffic over time" + dynamic subtitle.
   → Title uses `Style="{StaticResource text.subtitle}"`; subtitle
   uses `Style="{StaticResource text.caption}"` with secondary
   foreground (the style already sets it).
7. **Chart axis labelers are grain-agnostic.** Y-axis reads
   `"/bucket"` (internal storage jargon — a bucket is a minute for
   Samples, an hour for Hourly, a day for Daily). X-axis is bare
   `HH:mm` regardless of window span — for Hourly grain over 7 days
   every day's labels repeat indistinguishably with no day-boundary
   cue.
   → Adapt both labelers by grain through one shared closure
   plumbing (grain passed through the closure once, both axes
   consume it).
   Y unit suffix: `/min` for Samples, `/hr` for Hourly, `/day` for
   Daily.
   X format: `HH:mm` for Samples, `MM-dd HH` for Hourly, `MM-dd`
   for Daily.
8. **Connections grid has no empty state.**
   → Centered `text.body` `text.secondary` "No endpoints recorded in
   this window." in the grid viewport when the result is empty.
9. **Sessions grid has no empty state.**
   → Centered `text.body` `text.secondary` "No sessions recorded
   in this window." Same pattern.
10. **Connections grid is missing the Class column.** `RemoteClass`
    (Local / Wan) is on the view-model but never bound. WAN-vs-local
    is THE most user-facing categorical distinction the screen
    surfaces about endpoints.
    → Add a "Class" column (width 60). Render as a `radius.control`
    pill: `chart.wan` background for `Wan`, `chart.local` background
    for `Local`, white text. Same Okabe-Ito palette as the
    categorical viz. Pure presentation; no new data.
11. **Connections + Sessions grid presentation needs design
    review.** A cluster of related pain points, documented here
    without prescribed solutions because the right treatment is
    likely several iterations of Claude Design exploration, not a
    boilerplate token swap. Pain points:
    - **Responsive collapse.** At 800 px window width (`MinWidth`
      in `MainWindow.xaml:11`), each grid gets ~370 px before
      chrome. Connections needs ~360 px to show IPv6 + port
      without clipping; Sessions needs ~330 px to show both
      timestamps. Both clip ungracefully — there is no responsive
      switch.
    - **Density is the default DataGrid (~28 px rows).** Per-App's
      AppsGrid uses `style.datagrid.compact` (per `per-app.md:354
      -358`) because it's the *single dominant grid on its page*
      and that page's "entire job" is the grid. That justification
      does NOT automatically transfer to App Detail's two
      side-by-side grids competing for vertical space with a
      summary card and a chart card above them. Defaulting to
      compact is not the right reflex here — one of ZenVizor's
      goals is to be a light, usable surface, and compacting data
      into a wall works against that. The right density depends
      on the broader layout direction Claude Design takes.
    - **No alternating row background.** Neither grid sets
      `AlternatingRowBackground`. Row-scan ergonomics are worse
      than they need to be, but the right treatment may be alt
      rows, lightweight dividers, or neither, depending on chosen
      density and layout.
    - **Code-like columns render in proportional digits.**
      Connections `Address`, Sessions `Start` / `End` /
      `Services` are all code-like content (IP/port, timestamps,
      comma-separated service tokens) currently rendered in
      proportional text. They read as paragraphs rather than
      tabular data.
    - **Column comprehension is poor.** Most users will not know
      what `Proto`, `Port`, `Session`, `PID`, or `Services`
      represent, or why they should care. The grids assume a
      level of sysadmin literacy that ZenVizor's target audience
      doesn't have. Without contextual help, these columns are
      decoration to a casual user. This is the most consequential
      item in the cluster — without solving it, the grids could
      look beautiful and still fail to inform.
    → **Open design problem for Claude Design.** Document the
    pain points in the brief but explicitly state no solutions
    are prescribed. Iterate. The column-comprehension concern in
    particular may make F1 (deferred endpoint-detail surface —
    see below) a *strong* design candidate rather than a
    pure-future item, since a click-through detail surface is one
    way to teach the user what a row means without bloating the
    row itself.
12. **Card chrome + chart tooltip use translucent / unstyled
    defaults.** Card backgrounds use translucent
    `CardBackgroundFillColorDefaultBrush`; card borders use gradient
    `ControlElevationBorderBrush`; the chart tooltip is LiveCharts2's
    default unstyled surface. Chart card is the highest-data surface
    — axis-label and tooltip contrast become wallpaper-dependent
    under Mica without opacity.
    → Standard token-migration boilerplate, no new design surface:
    - Card background → opaque `surface.card`.
    - Card border → `border.card`.
    - Chart tooltip → follow the **Dashboard's locked treatment**
      (`docs/design-briefs/dashboard.md` §tooltip,
      `docs/dashboard-UI-phase-plan.md:80-85`): opaque
      `chart.tooltip.bg` + `chart.tooltip.text` with
      `DropShadow(0, 4, 8, 8, ~38% black)` for backdrop separation.
      `ChartTheming.Apply()` already wires `TooltipBackgroundPaint`
      / `TooltipTextPaint` / `FindingStrategy.CompareOnlyXTakeClosest`
      (`ChartTheming.cs:63-65`); App Detail's `SeriesChart` inherits
      that automatically since it already calls `ChartTheming.Apply`
      (`AppDetailPage.xaml.cs:116`).

    **Implementation note (not a Claude Design surface):** Dashboard
    additionally widened the line-series hover hit area to make
    tooltip activation more forgiving (`DashboardPage.xaml.cs:96-98,
    106-108`): `GeometrySize = 20`, `GeometryFill = null`,
    `GeometryStroke = null` — invisible markers, 20 px wide hit
    area. App Detail's chart goes through the shared
    `ChartBuilder.BuildSeries`, which currently sets
    `GeometrySize = 0` on both Up and Down line series
    (`ChartBuilder.cs:39, 46`) — hit area is effectively zero, so
    the tooltip is unhittably tight. Close the gap by moving the
    forgiving-hover config into `ChartBuilder` (preferred — single
    source for every line-series chart) rather than re-applying it
    page-side.
13. **`StatusBanner` does not split disconnected vs query-failed.**
    Same fix as Per-App (item 3 there).
14. **Chart series colors still LiveCharts2 defaults.** Same Dashboard
    fix; `chart.upSeries` / `chart.downSeries` wiring (already done
    by `ChartTheming.Apply()` — verify it's running on this chart).
15. **Loading affordance is wait-cursor only.** Three loading
    surfaces (chart, connections grid, sessions grid) all silent
    during refresh.
    → One centered Fluent `ProgressRing` per surface, indeterminate;
    OR a single page-level ring overlaid on the whole content area
    (decide in brief). Either way, no shimmer.
16. **Long header strings collide with picker on the next row.**
    `"svchost.exe (app id 42)"` is short; a name like
    `"NetworkServiceLikeAReallyLongName.exe (app id 12345)"` can
    overflow.
    → Set `MaxWidth` + `TextTrimming="CharacterEllipsis"` on
    `HeaderText`. Combined with item 4, the header is short anyway.
17. **`ChartSubtitle` reads `"Showing X over Y."`** Verbose narrator.
    → Drop `"Showing "`; render as `"per-minute detail · last 24
    hours"` (or grain-by-window without the verb). Specify in brief.

### Scope sort — MANDATORY

**Polish (this round):** 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
14, 15, 16, 17.

**Feature (flagged for later — explicitly out of brief):**

- F1. **Click-a-connection-row → endpoint detail surface** (sessions,
  timeline, per-endpoint chart, plus plain-language explanation of
  what the connection actually represents — the row's hostname,
  whether it's a known service endpoint, why the app is talking to
  it). **Promoted from "speculative future" to a viable design
  direction Claude Design should weigh** — item 11's column-
  comprehension concern is one of the strongest motivations for
  this surface, since a click-through detail layer can teach the
  user what a row means without bloating the row. The primer's old
  "App Detail flyout is opaque, not acrylic" decision is no longer
  load-bearing — if endpoint detail is built, the surface treatment
  itself (flyout, sub-page, expandable inline row, side panel) is
  an open question, not pre-decided. Still out of brief for THIS
  round (it's a new interaction surface, feature-class scope), but
  Claude Design should know the polish-pass result is not the final
  shape of the grids — a future-detail-surface direction may change
  what columns the grids need to carry at all.
- F2. **Reverse DNS / hostname column on Connections.** PRD §7.4
  explicitly reserves `connections.resolved_host` for a future
  passive-DNS module — **NOT IN MVP**. Hard "do not propose this
  column" boundary; brief should state this.
- F3. **Brush-to-zoom on chart.** Click-and-drag to select a
  sub-window. New interaction.
- F4. **Export endpoint list to CSV.** Phase 5 owns export.
- F5. **"See related apps" link from svchost row** — jump to all apps
  that ever shared this PID's services. New nav surface.
- F6. **Active-action affordances (kill / block / disconnect endpoint).**
  HARD NO per the passive-only invariant. Document the boundary; do
  not even mock.
- F7. **Filter/search within Connections** (by port, address, class).
  New capability.
