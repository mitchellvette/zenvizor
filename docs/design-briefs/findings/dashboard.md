# Pre-brief — Dashboard (findings, Group A)

Grounded walk of `src/ZenVizor.Ui/Views/DashboardPage.xaml` +
`DashboardPage.xaml.cs`. This is the input to the Dashboard Claude
Design brief, not the brief itself. Review this doc and inject your UX
judgments before the brief is generated.

> **Revised 2026-06-03** after walk-through with the user. Substantive
> additions: 4-up status card row (§7.2), bottom-bar rate mirror
> (§7.4), poller lifecycle shift (§7.5), dimmed-row persistence on the
> talkers list (#19), X-axis density + relative labels (#17),
> column-header tooltips (#18), revised Y-axis "nice values" treatment
> (#5), X-snap chart tooltip with dual-time format (#13).

---

## 1. Purpose & IA placement

- **Purpose:** the "what's happening on my network *right now*" view.
  Default landing screen when the window opens.
- **IA placement:** first item in the left nav rail
  (`ui:NavigationView` `MenuItems`). `Symbol="DataPie24"`. Selected on
  startup via `RootNavigation.Navigate(typeof(DashboardPage))` in
  `MainWindow.xaml.cs:65`.

## 2. What is literally on it today

Walked from `DashboardPage.xaml` and the code-behind.

Root: `<Grid Margin="24">` with 3 rows (`Auto / * / 2*`).

### Row 0 — header

- `<StackPanel Orientation="Horizontal">`:
  - `ui:TextBlock FontTypography="Subtitle"`
    `FontFamily="{DynamicResource font.display}"`
    `Text="Current Activity"`.
  - `<Border x:Name="WarmingBanner" Visibility="Collapsed" Margin="16,0,0,0">`
    — `Background="{DynamicResource SubtleFillColorSecondaryBrush}"`,
    `Padding="8,4"`, `CornerRadius="4"`, inner
    `<TextBlock Text="warming up — first flush bucket lands within ~5 s">`
    with `Foreground="{DynamicResource TextFillColorSecondaryBrush}"`.
  - `<Border x:Name="DisconnectedBanner" Visibility="Collapsed" Margin="16,0,0,0">`
    — `Background="{DynamicResource SystemFillColorCautionBackgroundBrush}"`,
    `Padding="8,4"`, `CornerRadius="4"`, inner
    `<TextBlock x:Name="DisconnectedText" Text="service disconnected">`
    with `Foreground="{DynamicResource SystemFillColorCautionBrush}"`.

### Row 1 — live rates chart card

- `<Border Padding="8" CornerRadius="6">` with
  `Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"`
  and `BorderBrush="{DynamicResource ControlElevationBorderBrush}"`.
- Inner: `<lvc:CartesianChart x:Name="RatesChart" Background="Transparent"
  LegendPosition="Top">`.
- Two `LineSeries<DateTimePoint>` (`Up B/s`, `Down B/s`, `GeometrySize=0`),
  values bound to `_upSeries` / `_downSeries`
  `ObservableCollection<DateTimePoint>` (`DashboardPage.xaml.cs:30-44`).
- X axis labeler: `DateTime.ToString("HH:mm:ss")` (`:47`).
- Y axis labeler: `FormatRate(v)` (`:51`).

### Row 2 — talkers card

- `<Border CornerRadius="6" Margin="0,12,0,0">` with the same fill +
  stroke pair as the chart card.
- Inner `<Grid>`: header row + list.
  - Header strip (`Border Padding="12,8" BorderThickness="0,0,0,1"`):
    a 5-column Grid (`* / 200 / 90 / 110 / 110`) with `<TextBlock
    FontWeight="SemiBold">` for `App / Publisher / Signature / Up/s
    / Dn/s` (right-aligned on the two numeric headers).
  - `<ListView x:Name="TalkersList">` with `Background="Transparent"
    BorderThickness="0"`, ItemContainerStyle padding `8,4`, ItemTemplate
    re-uses the same 5-column Grid binding to `AppLabel /
    Publisher / SignatureStatus / UpRateText / DownRateText`. App and
    Publisher columns set `TextTrimming="CharacterEllipsis"`. Publisher
    column foreground is `TextFillColorSecondaryBrush`.

## 3. Current behavior (what it literally does)

- **Refresh cadence:** `ActivitySnapshotPoller` polls
  `IZenVizorIpc.GetCurrentActivitySnapshotAsync` every 2 s
  (`ActivitySnapshotPoller.cs:18`), pushes results to
  `OnSnapshotReceived` → `ApplyUpdate` on the dispatcher.
- **Chart trailing window:** `ChartHistoryPoints = 60` × 2 s cadence ≈
  2 minutes (`DashboardPage.xaml.cs:18`). Older points spliced off the
  front of `_upSeries`/`_downSeries`.
- **Top-talkers ordering:** `OrderByDescending(BytesUpTotal +
  BytesDownTotal).ThenBy(ImageName)`. Take 10 (`:102-107`). Bound to
  `Talkers` `ObservableCollection`; re-populated wholesale every tick
  (Clear + Add).
- **Warming branch:** when `snap.WindowSeconds <= 0` or `snap.Apps.Count
  == 0` → `WarmingBanner.Visibility = Visible`; `Talkers.Clear()`;
  return without touching the chart history (`:85-90`).
- **Disconnected branch:** `update.IsConnected == false` →
  `DisconnectedBanner.Visibility = Visible`, `DisconnectedText.Text =
  $"service disconnected ({update.FailureReason})"`. **Chart history
  is intentionally preserved** so the user can see the last known
  state; the banner indicates staleness (`:74-81`).
- **Resize behavior:** chart re-flows vertically every tick because Y
  axis auto-scales to (Up, Down). When the throughput jumps from KB/s
  to MB/s the vertical band can jump visibly mid-frame.
- **Chart theme:** `ChartTheming.Apply(RatesChart)` runs in the ctor
  and re-runs on `ApplicationThemeManager.Changed`. Axis labels +
  separators repaint on OS light/dark flip. Series colors are still
  LiveCharts2 defaults — `chart.upSeries`/`chart.downSeries` are NOT
  bound yet.
- **Poller lifecycle (today):** `_poller.Start()` on `Loaded`,
  `_poller.Stop()` on `Unloaded`. Because every nav-rail item has
  `NavigationCacheMode.Enabled`, the page instance survives nav-away,
  but `Unloaded` still fires — so polling pauses when the user is on
  any other screen. (This changes in §7.5 — poller moves to
  MainWindow scope.)

## 4. Data-presentation reality

- **Rates** are humanized via `FormatRate`
  (`DashboardPage.xaml.cs:116-129`): `B/s` → `KB/s` → `MB/s` → `GB/s`,
  one decimal until `value >= 100` then no decimal. Returns `"0 B/s"`
  for NaN or `<= 0`.
- **Talker labels:** `ImageName`, or `"ImageName [HostedServices]"`
  when the server surfaces co-hosted services (`TalkerRowViewModel.From
  :139-152`). This is the honest-attribution treatment: services
  listed, PID total reported, no per-service split.
- **Publisher:** `"(unknown)"` when null/empty.
- **Signature:** raw enum string from the server
  (`Signed` | `Unsigned` | `Invalid` | `Unchecked`).
- **Chart X-axis:** `HH:mm:ss`. No date qualifier — fine for a
  2-minute window but undated. At 2560×1440 the default LiveCharts2
  tick density produces ~25 labels across the window (e.g.
  `"23:09:45   23:09:50   23:09:55"`) — long string of close numbers
  that aren't scannable. See friction #17.
- **Chart Y-axis:** humanized via `FormatRate`, so the same labeler
  feeds both legend semantics and tick labels. Values are arbitrary
  ("19.4 KB/s") because the axis auto-scales to peaks rather than
  rounding to predictable thresholds. See friction #5.
- **Legend:** at top of chart, default LiveCharts2 styling, copy =
  series Name strings (`"Up B/s"`, `"Down B/s"`). No tooltip styling
  applied.
- **Bottom-bar dot:** `MainWindow.xaml.cs:170-180` flips between
  `Brushes.MediumSeaGreen` (connected) and `Brushes.DarkOrange`
  (disconnected). Hardcoded — tracked in design-system §11 outstanding
  gaps.
- **No headline rate display.** The user can only infer total Up/Down
  from reading the chart visually. See friction #20 and §7.2.

## 5. State coverage today

| State | Handled today | Notes |
|---|---|---|
| empty | **no** | Initial paint before first 2 s tick = empty chart + empty list with no cue. Warming branch only fires once a snapshot arrives. |
| loading | **no** | No spinner. First paint is the empty surface above; no caption explaining "waiting for first sample." |
| warming | yes | `WarmingBanner` inline in header. Caption: "warming up — first flush bucket lands within ~5 s." |
| disconnected | yes | `DisconnectedBanner` inline in header, copy `"service disconnected (<FailureReason>)"`. Chart history preserved. |
| error | merged with disconnected | `ActivitySnapshotPoller` catches every exception and reports `IsConnected: false` with `FailureReason = ex.GetType().Name`. No distinction between "service is down" and "service is up but the call failed." |

Dashboard does NOT have App Detail's `NoDataOverlay` — different
surface, different fix.

## 6. Friction list (paired with proposed direction)

Each item: observation grounded in the code → proposed direction.
Scope sort consolidated in §9 (covers both this list and the §7
layout changes).

1. **Inline banners share the header row.** WarmingBanner and
   DisconnectedBanner sit horizontally after the "Current Activity"
   subtitle. The row's visual rhythm changes depending on which (if
   any) banner is showing.
   → Move banners to a dedicated row beneath the header. Keeps page
   rhythm stable; banners read as page-state rather than as a title
   appendage.
2. **WarmingBanner uses `SubtleFillColorSecondaryBrush`** — a generic
   subtle background, not the caution-class color the state actually
   represents. The Disconnected banner uses caution.
   → Repoint warming to `status.warming.background` (caution-class) so
   warming and disconnected share a vocabulary. Text repoints to
   `status.caution.text` (the AA-safe darker amber for body text on
   light tint, per design-system §3).
3. **Disconnected banner and bottom-bar dot use different
   vocabularies.** Banner = caution
   (`SystemFillColorCaution*`); bottom-bar dot = `Brushes.DarkOrange`
   (which is *also* caution-flavored, by coincidence). The actual
   semantic state is "pipe down" — which is critical-class, not
   caution.
   → Bottom-bar dot → `status.disconnected` (critical-class). Banner
   → `status.critical.background` + `status.critical` foreground when
   the disconnect is steady (>1 cycle); keep caution colors only for
   transient "still connecting" cases. (Partially tracked as
   design-system §11 item 1.)
4. **Both cards use `CardBackgroundFillColorDefaultBrush` =
   `surface.card.alt` (translucent).** Per design-system §3 + the Mica
   + contrast rule, any text/data-bearing card must sit on
   `surface.card` (opaque). The Dashboard's two cards together carry
   100 % of the page's data.
   → Migrate to `surface.card`. Borders move from
   `ControlElevationBorderBrush` (LinearGradientBrush, not
   tokenizable) to `border.card`.
5. **Y-axis values are not casual-user-parseable.** Today the axis
   auto-scales to (Up, Down) peaks, so a 19.4 KB/s peak labels the
   axis at "19.4 KB/s" instead of a round threshold. The band also
   re-flows on every tick — when traffic spikes, the vertical extent
   visibly jumps mid-frame.
   → Anchor `Y.MinLimit = 0`. Compute upper bound as the next "nice"
   round value in the current unit (round to 5 / 10 / 20 / 50 / 100
   / 200 / 500 / etc.). Force a fixed step so ticks land on round
   values (`0`, `5 KB/s`, `10 KB/s`, `15 KB/s`, `20 KB/s`). Smoothing
   the upper bound across ticks (EWMA-ish) keeps the band breathing,
   not jittering.
   Tradeoff: when traffic is in MB/s range, small spikes flatten near
   0. Correct behavior — peaks need headroom, casuals want
   predictable axis values.
6. **Chart card has no header.** The only label is the LiveCharts2
   legend at top (`"Up B/s"`, `"Down B/s"`). Talkers card has a heavy
   header strip with column titles. Visual weight is mismatched.
   → Add a card-header row to the chart card: `text.subtitle`
   "Live rates". A trailing "Last 2 minutes" caption is **partly
   redundant** once item 17's relative X-axis labels land
   (`"-2m"` … `"now"` already communicates the trailing window).
   Decide in brief whether to keep the caption or drop it; my
   preference is drop — relative labels are self-explanatory.
7. **"Current Activity" uses an ad-hoc one-off binding.**
   `ui:TextBlock FontTypography="Subtitle" FontFamily="{DynamicResource
   font.display}"` — the FontFamily binding is unique to this page.
   The design-system contract is keyed Styles
   (`{StaticResource text.subtitle}`).
   → Switch to `Style="{StaticResource text.subtitle}"`. Remove the
   one-off FontFamily binding.
8. **Talkers column headers are bare `TextBlock FontWeight="SemiBold"`.**
   Inline weight override; the design-system has `text.body.strong`
   for exactly this (14, SemiBold, with the reconciled line-height).
   → Route headers through `Style="{StaticResource text.body.strong}"`.
9. **Up/s and Dn/s columns use proportional digits.** Right-aligned but
   the digits don't visually align across rows.
   → Bind the two numeric columns to `Style="{StaticResource text.mono}"`
   (NF Code Regular 14). Solves the alignment without changing
   semantics.
10. **Legend duplicates units that the Y axis already carries.** Y axis
    label = "42 KB/s"; legend = "Up B/s"/"Down B/s". The "/s" in the
    legend is noise.
    → Change series Name to plain `"Up"`/`"Down"`. Axis owns units.
11. **Talkers list in disconnected state is undefined.** When
    `IsConnected == false`, `ApplyUpdate` returns early before reaching
    Talkers update. The previous list lingers with no staleness cue.
    → Specify behavior: dim the list (`Opacity 0.6`) while the
    DisconnectedBanner is visible. No data-clear (keeps last-known
    state visible, matches chart-history behavior).
12. **Empty talkers list (warming cleared, no apps yet) has no
    treatment.** After warming clears, if no app has logged traffic
    yet (rare but real on a quiet system), the list is silently empty.
    → Add `text.body` `text.secondary` "No active talkers in this
    window." centered in the list viewport.
13. **Chart tooltip ergonomics.** Default LiveCharts2 tooltip requires
    the user to hover ON the series line itself (1-2 px wide) for the
    tooltip to activate. Unusable in practice on a live chart.
    → Two changes at the chart property level:
    (a) Tooltip finding strategy switches to X-snap mode (cursor
    anywhere along the X width activates the tooltip for the nearest
    bucket; Y proximity ignored). Solves "precise cursor location."
    (b) Tooltip carries BOTH relative and absolute time in one row:
    `"-90s · 23:34:10 · Up 12 KB/s · Dn 18 KB/s"`. The relative time
    matches the X-axis labels (item 17); the absolute time anchors
    the bucket to wall-clock so the user can correlate to events.
    (c) Tooltip background must be opaque (`chart.tooltip.bg` already
    specs this); apply via ChartTheming — see §7.7.
    The "bucket scrolls left as new data arrives" residual is
    explicitly NOT addressed by polish — click-to-pin is the
    feature-class fix (see §9 Feature F3).
14. **Chart series colors still LiveCharts2 defaults.**
    `chart.upSeries`/`chart.downSeries` are defined in the design
    system but not bound — chart paint wiring is design-system §11
    item 2.
    → Brief specifies the tokens; implementer wires
    `SolidColorPaint`s in `ChartBuilder` for the Dashboard chart in
    particular (this is the screen where the brand-deviating violet/
    teal up/down most needs to land). See §7.8.
15. **Header subtitle "Current Activity" doesn't convey trailing
    window.** A casual user doesn't know whether this is "last
    minute" or "last hour." **Partly addressed by item 17's relative
    X-axis labels** — `"-2m"` to `"now"` makes the trailing window
    self-evident.
    → Optional caption "Live rates from your network" for casual
    framing; explicit "Last 2 minutes" copy is redundant once
    relative labels land. Decide in brief.
16. **Both cards' `CornerRadius="6"`** is the raw scale value that
    matched the *previous* `radius.md`. Reconciled design system uses
    role tokens — `radius.card` points at `radius.md = 10`.
    → Migrate to `{StaticResource radius.card}`. Visual jump is small
    (6 → 10) but auditable in one diff.
17. **X-axis label density and absolute-clock formatting.** Default
    LiveCharts2 ticks-per-data-point produces ~25 labels across the
    chart at 2560×1440, each `"HH:mm:ss"`. Reads as a long string of
    close numbers — not scannable for the live-trend job the chart
    is doing.
    → Two stacking changes:
    (a) Set tick `MinStep` to ~20-30 s so ~6 labels render across the
    2-min window (5 ticks at 30-s spacing + endpoints).
    (b) Switch label format to **relative** — `"-2m"`, `"-90s"`,
    `"-1m"`, `"-30s"`, `"now"` — since the chart is a live trailing
    window, not an absolute timeline. Absolute wall-clock is
    preserved via the tooltip's dual-format (item 13).
    Sample cadence (2 s) is **unchanged** — only label rendering
    changes.
    Considered and rejected: per-label tooltips ("`-90s`" hover →
    `"23:34:10"` popup). LiveCharts2 paints axis labels as SkiaSharp
    surfaces, not WPF elements; a transparent WPF overlay grid
    tracking label positions across resize is fragile. The
    chart-area tooltip (item 13) carries the absolute time anyway —
    same destination, different door. See §9 Feature F6.
18. **Talkers column headers have no tooltips.** Casual users don't
    know what `"Signature"` means or what "Unsigned" implies for
    their security posture. Same gap on Publisher and the rate
    columns.
    → Add WPF `ToolTip` on every column header with non-obvious
    semantics. Specific copy:
    - Signature: `"Signed = publisher verified offline (no
      revocation check). Unsigned = no signature present. Invalid =
      signature failed verification. Unchecked = verification
      skipped."`
    - Publisher: `"'(unknown)' means no Authenticode signer is
      present."`
    - Up/s, Dn/s: `"Average B/s over the last 2-second polling
      cycle."`
    Centralize the strings as a single `ResourceDictionary` block
    (e.g. `Resources/Strings.Tooltips.xaml`) so future localization
    is one swap. Pure polish; no tradeoff.
19. **Talkers list silently drops rows below #10.** When a process
    falls below the rate cutoff, its row vanishes without trace — the
    user has no way to identify what was just there.
    → Dimmed-row persistence. A row that drops below #10 stays in
    the list at `Opacity = 0.5` for ~30 s before disappearing.
    Memory-state on the page (no new IPC, no new contract). List
    grows from 10 active to up to ~20 rows (10 active + ~10 recent
    at lower opacity). "Recent" defined by **time**, not rank —
    aligns with discovery > ranking.
    Coupled with §7.3's internal card scroll: when the row count
    exceeds the visible row cap, the ListView inside the talkers
    card scrolls; the dashboard PAGE does not scroll (preserves the
    glance property).
    Alternative considered and rejected for polish: separate
    "recent" table beside or beneath the talkers card. New visual
    zone, more screen real-estate, two lists to scan — feature-class
    (see §9 Feature F7).
20. **No headline rate display anywhere on the page.** The user can
    only infer total Up/Down by reading the chart visually. No
    single-glance number.
    → Two surfaces, layered (see §7.2 and §7.4):
    (a) **4-up status card row above the chart** carrying Upload
    rate, Download rate, Active processes, WAN vs Local split. Dashboard-
    only, primary metric, full visual weight.
    (b) **Compact mirror in the bottom bar** opposite the service
    status. Small `text.body.strong` + `text.mono`. Visible on every
    screen (poller lives at MainWindow scope — see §7.5).

## 7. Layout changes proposed (this round)

These are structural changes to the page composition that don't fit
neatly into the per-component friction list. They're listed here so
the brief's "Controls in scope" section can lift the new row layout
verbatim. Scope sort in §9.

### 7.1 Row composition

Today: 3 rows (header / chart `*` / talkers `2*`).

Proposed: **4 rows** (header / status row / chart / talkers) using
`Auto / Auto / * / *` with bounded talkers:

```
<Grid Margin="24">
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto" />   <!-- header + banner row -->
    <RowDefinition Height="Auto" />   <!-- 4-up status card row -->
    <RowDefinition Height="*"    />   <!-- chart card (residual)  -->
    <RowDefinition Height="*"    />   <!-- talkers card (residual + cap) -->
  </Grid.RowDefinitions>
  ...
</Grid>
```

Talkers card carries `MinHeight=140` (~4 rows floor) and
`MaxHeight=320` (~10 rows cap). Status row sizes to content
(~100–110 px with 4-up card padding). Chart + talkers compete for
residual roughly 50/50.

At default 720 px window: status ~105, chart ~225, talkers ~225
(within range). At 1440 p: talkers caps at 320, chart absorbs the
extra. At 600 px window MinHeight (see §7.6): status ~105, chart
~155, talkers floors at ~140 — chart cramped but readable, talkers
preserved.

**MaxHeight on talkers is intentional** — combined with §7.3's
internal scroll, this preserves Dashboard as a glance surface
(chart and status always visible). The full app list is Per-App's
job.

### 7.2 Status card row (4-up)

Above the chart. Horizontal `<Grid>` with 4 equal columns (`* / * /
* / *`), gap = `space.12` between cards. Per card:

- `Border surface.card radius.card padding=space.16`.
- `text.eyebrow` label (uppercased per Style).
- `text.title.large` value, `text.mono` for the numeric cards.
- `text.caption` `text.secondary` subline (optional context).

The four cards:

| # | Eyebrow | Value | Subline |
|---|---|---|---|
| 1 | UPLOAD | `<total Up B/s>`, FormatRate-humanized | `text.caption` "trailing 2-second average" |
| 2 | DOWNLOAD | `<total Down B/s>`, FormatRate-humanized | same |
| 3 | ACTIVE PROCESSES | count of apps with non-zero rate this cycle | `text.caption` "talking right now" |
| 4 | WAN vs LOCAL | horizontal stacked bar (`chart.wan` / `chart.local`) | `text.caption` `"WAN 73% · Local 27%"` |

Tokens: cards use the same surface / radius / spacing tokens as the
rest of the polish pass. The WAN/Local bar reuses the categorical
chart tokens — fits with how Reports will visualize the same split.

### 7.3 Talkers card persistence + internal scroll

Combined with friction #19 (dimmed-row persistence):

- Card has `MinHeight=140` and `MaxHeight=320` per §7.1.
- ListView inside the card has its own `ScrollViewer.VerticalScrollBar
  Visibility=Auto`; when total row count (active + recent-dimmed)
  exceeds visible row capacity, the ListView scrolls internally.
- Dashboard PAGE does NOT scroll. The chart and status row remain
  visible regardless of how many recent talkers are in the list.

This is the **Model B** choice from the design discussion (cap card
with internal scroll), explicitly chosen over Model A (page scrolls,
chart vanishes during inspection) and Model C (custom sticky
ScrollViewer composition).

### 7.4 Bottom-bar rate mirror

MainWindow's bottom bar gets a compact rate display opposite the
service status:

- Layout: bottom bar splits left/right via a `Grid` with two
  columns. Left = current service status indicator (existing —
  ellipse + text). Right = compact rate readout.
- Right-side content: `text.body.strong` labels `↑` and `↓`, with
  `text.mono` rate values: `"↑ 12 KB/s   ↓ 18 KB/s"`. Use
  `ui:SymbolIcon ArrowUp16` / `ArrowDown16` if the arrow glyphs
  read cleaner at body size.
- Visible on every screen — not gated to Dashboard.
- Disconnected state: rate values render as `"—"` (em-dash) with
  `text.tertiary` foreground; arrows fade to `text.tertiary`.

Tradeoff: bar rates feel slightly noisy on screens like Settings
where they're not contextually relevant. Accepted — "is anything
talking right now?" is genuinely useful from any screen, and the
compact size keeps the noise small.

### 7.5 ActivitySnapshotPoller lifecycle moves to MainWindow

Today: `DashboardPage` starts/stops the poller via `Loaded`/`Unloaded`,
so polling pauses when the user nav-aways from Dashboard. With the
bottom-bar mirror (§7.4), polling must continue regardless of which
screen is active.

Proposed:

- Move `ActivitySnapshotPoller` ownership from `DashboardPage` to
  `MainWindow`. Started in `MainWindow.OnLoaded` (alongside the
  existing `ServiceStatusPoller`), stopped in `OnClosed`.
- MainWindow exposes the latest snapshot's `totalUp` /
  `totalDown` / `activeAppCount` / `wanLocalRatio` via a small
  observable (`ActivityHeadline` record or four properties).
- The bottom bar binds to these properties; Dashboard's existing
  `ApplyUpdate` flow subscribes to the **same** poller instance for
  its chart and talkers update path.
- One poller per process; the per-page `_poller` in `DashboardPage`
  is removed.

Cheap architectural shift — ~30 lines moved, no contract change, no
performance shift (the 2-s cadence already runs; it just runs
unconditionally now). The CPU cost of polling while on Settings is
negligible (well inside the <1 % idle budget).

### 7.6 MainWindow MinHeight 500 → 600

The current `MainWindow.xaml:11` sets `MinHeight="500"`. That
predates the 4-up status row. At 500 px the dashboard becomes too
tight — status row (~105) + chart (~120) + talkers (floor 140)
leaves the chart cramped beyond usefulness.

Bump to `MinHeight="600"`. No downside; 500 px was a tighter floor
than typical use. `Width` MinWidth stays at 800.

### 7.7 ChartTheming extensions (tooltip paint surfaces)

The chart-tooltip changes from friction #13 require
`ChartTheming.Apply` to also configure tooltip paints — today it
only sets `LegendTextPaint`:

- `chart.TooltipBackgroundPaint` = opaque paint from `chart.tooltip.bg`.
- `chart.TooltipTextPaint` = paint from `chart.tooltip.text`.

Both re-apply on `ApplicationThemeManager.Changed` (same wiring as
the existing label / separator / legend paints). The tooltip
finding strategy is a direct property assignment on `CartesianChart`
(`TooltipFindingStrategy = TooltipFindingStrategy.CompareAll`-or-
similar-X-snap variant — pick the LiveCharts2 enum that matches
"X-axis only, nearest" at implementation time), not paint-wired.

This change affects ALL chart screens via shared code (Dashboard +
App Detail + History) — see §8 cross-screen notes.

### 7.8 Series paint wiring

The `chart.upSeries` / `chart.downSeries` token binding (friction
#14) — implementation lives in `ChartBuilder` and/or page-specific
paint application. The Dashboard's `LineSeries<DateTimePoint>` Up
and Down get explicit `Stroke` `SolidColorPaint`s sourced from the
tokens; `Fill` gets a translucent alpha variant of the same color
for the area-under-line.

Same paint flow re-applies on theme flip via `ChartTheming.Changed`
subscription that's already in place.

This change also propagates to App Detail and History via shared
ChartBuilder paths — see §8.

## 8. Downstream effects (cross-screen)

Three changes in this round have effects beyond Dashboard:

- **§7.7 ChartTheming tooltip paints + finding strategy**: lands via
  shared `ChartTheming.cs`. App Detail's `SeriesChart` and History's
  `HistoryChart` inherit the same tooltip ergonomics (X-snap finding,
  opaque tooltip, themed paint). When the App Detail and History
  briefs are written, their tooltip friction items resolve to "same
  treatment as Dashboard — no per-screen brief work needed."
- **§7.8 series paint wiring**: `chart.upSeries` /
  `chart.downSeries` apply wherever Up/Down series are built. App
  Detail and History will adopt the same paint tokens via
  `ChartBuilder`. No per-screen design work needed.
- **§7.4–§7.5 bottom-bar mirror + MainWindow-scope poller**: chrome
  change visible on every screen. Per-App / App Detail / History /
  Reports / Alerts / Settings briefs don't need to redesign their
  bottom bars — the change lands once in MainWindow and propagates.

When we revise the other findings docs, these three points should
move from "per-screen friction" (where I tentatively listed them
in app-detail.md #19 / history.md #10) to "covered by Dashboard
chrome work — no per-screen brief required." I'll fold that
reconciliation in when you sign off on those docs.

## 9. Scope sort — consolidated

Covers both §6 friction items and §7 layout changes.

### Polish (this round)

- **Friction list (§6):** items 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
  12, 13, 14, 15, 16, 17, 18, 19, 20.
- **Layout changes (§7):** 7.1 (row composition + min/max heights),
  7.2 (4-up status card row), 7.3 (talkers persistence + internal
  scroll), 7.4 (bottom-bar rate mirror), 7.5 (poller lifecycle
  shift to MainWindow), 7.6 (MainWindow MinHeight 500 → 600), 7.7
  (ChartTheming tooltip paint extensions), 7.8 (series paint
  wiring).

### Feature (flagged for later — explicitly out of brief)

- **F1. "Top 10" cap on talkers list.** Confirmed by the user as
  Dashboard scope — Dashboard is the live glance surface, "top by
  current rate" is the point. Discovery > ranking applies to drill
  surfaces (Per-App, App Detail), not here. **Keep Top 10 on
  Dashboard, framed explicitly as "top by current rate"; ensure
  Per-App has no cap.** This is the boundary-case decision;
  document it in the brief so a later iteration doesn't try to
  "fix" it.
- **F2. One-click bridge from a Dashboard row to App Detail.** Natural
  follow-on for the "background tool I check in on" flow. New
  interaction surface, not presentation polish.
- **F3. Click-to-pin on the live chart (freeze a bucket for extended
  study).** X-snap (friction #13) solves the "precise cursor"
  complaint completely but does NOT solve the "bucket scrolls left
  as new data arrives" residual. Over a typical <5 s hover the
  bucket moves ~5 % of chart width (imperceptible); over 30 s+ it
  becomes noticeable; at 2 min it's off-screen. For glance
  inspection the X-snap is sufficient; for extended study (>30 s on
  one bucket) click-to-pin is the right answer — but it's a new
  interaction surface (state machine for pinned-vs-live, visual cue
  for pinned mode, click-elsewhere-to-unpin). Flag for later if
  user reports show extended-study is a frequent need.
- **F4. User-controlled poll cadence.** A Settings concern (Phase 6),
  not Dashboard polish.
- **F5. Inline search/filter on the talkers list.** Adds filter
  capability — feature.
- **F6. Per-axis-label tooltip overlay** (transparent WPF elements
  tracking the chart's SkiaSharp label band so hovering `"-90s"`
  pops `"23:34:10"`). Considered and rejected for polish — the
  overlay tracking across chart resize is fragile, and the
  chart-area tooltip's dual-time format (item 13) covers the same
  user need from the chart body. If future user testing shows
  people DO try to hover axis labels and get confused, revisit as
  feature.
- **F7. Separate "recent" table** as alternative to dimmed-row
  persistence (Model A from the row-dropoff discussion). New layout
  zone, more vertical space, two lists for the user to scan. The
  dimmed-row approach (item 19) covers the same need within a
  single visual zone; only revisit as feature if the dimmed
  approach causes scan confusion in practice.
