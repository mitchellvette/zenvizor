# Reports — implementation progress & forward plan

Working doc for the Reports screen build. Phases 1–2 complete; Phases 3–5
queued. This document is the handoff point for the next chat — paired with
the brief / findings / mockup, it should be sufficient context to resume
without re-deriving decisions.

---

## Source-of-truth documents (read in order)

| Doc | What it carries |
|---|---|
| `docs/design-briefs/reports.md` | The brief Claude Design worked from. Open questions Q1–Q9, durable locks, state matrix (§4), token list (§5), WPF gotchas (§10). |
| `docs/design-briefs/findings/reports.md` | Pre-brief — outcomes the page must communicate, deferred items, scope locks. |
| `docs/design/mockups/reports-design-mockups.pdf` | 12-page Claude Design hand-off. Page 1 = state:default. Pages 9-10 = empty/quiet/loading/disconnected/error. Page 12 = locked-variant cheat sheet. |
| `docs/zenvizor-sprint-plan.md` | §Phase 5 = the IPC + real-data + export deliverable. §Phase 6 = Alerts deep-link wiring (deferred from Phase 5). |
| `docs/zenvizor-prd.md` §11 | Daily report PRD intent. |
| `docs/design-system.md` | Token surface, canonical card recipe, HC stance. |
| `docs/design-briefs/_implementation-kickoff.md` | The pre-implementation checklist that drove Phases 1–2. |
| `docs/design-briefs/_chart-implementation-notes.md` | LC2 v2 gotchas (UnitWidth, MinLimit, persistent axes). |
| `src/ZenVizor.Ui/Resources/DesignTokens.xaml` | Token definitions (semantic keys). |
| `src/ZenVizor.Ui/Resources/BrandAccent.{Light,Dark}.xaml` | Brand-tuned theme-aware values. |
| `src/ZenVizor.Ui/Resources/HighContrast.xaml` | HC collapse. |

---

## Status

All five phases shipped in commit `1121b4f` ("Reports page: Phases 1-5
complete + AppDetail (app, date) drill"). Subsequent commits hardened
the chart layer (`0a97ab7`), the storage path (`55b2d2b`), and the IPC
contract (`137d983`); none of those changed the Reports surface scope.

- **Phase 1** ✓ COMPLETE — interim placeholder ships in the polish-interlude app.
- **Phase 2** ✓ COMPLETE — chrome row + hero card with mock data, walked through extensive fix passes.
- **Phase 3** ✓ COMPLETE — Notable today + Top Apps + Uncommon Talkers surfaces.
- **Phase 4** ✓ COMPLETE — state coverage matrix (Empty / Quiet day / Loading / Disconnected / Error).
- **Phase 5** ✓ COMPLETE — IPC contract + real data + CSV/HTML serializers + drill to AppDetailPage with the report date (drill destination changed from the original History-prefiltered design to AppDetailPage during Phase 5e; see locked-variants table below).

The drill destination ended up as **AppDetailPage** carrying
`AppDetailNavParams(appId, reportDate)`, not History as originally
locked. AppDetailPage is the better target because its 24-hour grain
already lines up with a daily report's date.

---

## Files touched so far

- `src/ZenVizor.Ui/Views/ReportsPage.xaml` (new — chrome row + hero card)
- `src/ZenVizor.Ui/Views/ReportsPage.xaml.cs` (new)
- `src/ZenVizor.Ui/Views/ReportsPage.cs` (deleted — was the `PlaceholderPage` shim)
- `src/ZenVizor.Ui/Services/ChartTheming.cs` (no net change — a `"Peak"` branch was added and then reverted when the peak dot became a XAML primitive)
- `docs/zenvizor-sprint-plan.md` — sprint Phase 6 scope now lists the Reports → Alerts deep-link as a documented deferral.

---

## Locked variants (page 12 of the mockup hand-off)

These are durable design law — do **not** re-litigate in Phase 3+:

| Question | Choice |
|---|---|
| Q1 page composition | (c) Narrative — Hero → Notable → Top Apps → Uncommon Talkers |
| Q2 temporal model | (b) Single date + selectable anchor (default 7-day average) |
| Q3 hero visualization | (b) Headline total + Up/Down split + day-shape sparkline + 2-segment WAN/Local bar |
| Q4 Top Apps surface | (a) DataGrid in Per-App vocabulary |
| Q5 Uncommon Talkers | (b) Category-grouped cards (New today / Unusual volume / Risky paths) |
| Q6 drill affordance | Hover chevron + hand cursor + single click (shared with Per-App) |
| Q7 Notable today | (a) Severity-grouped sections (Critical / Warning / Info) |
| Q8 quiet-day | "Nothing notable today." + "A deliberately quiet day. No app strayed from its usual pattern." |
| Q9 HTML export | Single self-contained doc, no remote refs |
| Export menu | Single `ui:Button` + `ContextMenu`, CSV + HTML items |
| Drill destination | History pre-filtered to `(app, date)` |
| Severity vocabulary | Inherits Alerts entity: `status.critical` / `status.caution` / `status.neutral` |
| Notable items | Cards / list rows — never a `DataGrid` |

---

## Phase 2 — key patterns & lessons (carry forward)

These earned their keep through iteration and are worth their weight in
Phase 3+:

### Wpf.Ui / WPF interop

1. **WinUI 3 brush-key override for ui:Button states.** Wpf.Ui's
   `ui:Button` template binds hover/pressed Background to `DynamicResource`
   keys `ButtonBackgroundPointerOver` / `ButtonBackgroundPressed` /
   `ButtonForegroundPointerOver` / etc. To retune those states without
   touching the template, override the keys at the element's local
   `<ui:Button.Resources>` scope. This precedence wins over the
   template's DynamicResource lookup. See ExportButton in
   `ReportsPage.xaml` for the canonical use.

2. **ContextMenu has its own NameScope.** `ElementName=`-style bindings
   from inside a `ContextMenu` to elements in the page tree silently
   return null. Use `RelativeSource={RelativeSource Self}` +
   `Path=PlacementTarget.{whatever}` to walk to the trigger. See
   `AnchorMenu.MinWidth` binding in `ReportsPage.xaml`.

3. **`MenuItem` is ambiguous under `using Wpf.Ui.Controls;`.** Wpf.Ui
   ships its own `MenuItem`. The XAML authors plain `<MenuItem>` (no
   `ui:` prefix → `System.Windows.Controls.MenuItem`). Code-behind casts
   must fully qualify: `if (sender is System.Windows.Controls.MenuItem mi)`.

4. **`ui:CalendarDatePicker` does NOT render the formatted date in its
   closed-state button face** (v4.0.2). F1 fallback: overlay a
   `TextBlock` with `IsHitTestVisible="False"`, bound to `Date` via
   `Binding` + `StringFormat="{}{0:ddd\, MMM d yyyy}"`. Pair with
   `HorizontalContentAlignment="Right"` on the picker so its internal
   calendar icon pushes to the right edge and doesn't overlap the
   overlay text. Confirmed working.

5. **Stock WPF `DatePicker` is NOT brand-styled by Wpf.Ui.** It carries
   default Aero/Aero2 chrome (dark underline, dark icon). Use
   `ui:CalendarDatePicker` for any brand-styled date picker.

6. **`MenuItem.Icon` + composite `MenuItem.Header` is standard.** No
   template override needed for icon + title + subtitle items. Bump
   `ui:SymbolIcon FontSize="20"` for visible-size icons.

### Custom-vs-stock policy

Architecture B is locked: **use stock Wpf.Ui primitives + WinUI 3
brush-key overrides**. Custom Button+Popup / Button+Popup+ListBox
patterns were rejected as one-offs after a long iteration.
Template-overrides are also out of scope. Brush-key overrides at the
element's local Resources scope are the documented WinUI 3 retuning
path — those are NOT one-offs.

### LiveCharts2 v2 (sparkline gotchas)

7. **DateTime X axis needs explicit `UnitWidth`, `MinLimit`, `MaxLimit`.**
   Without `UnitWidth`, the axis defaults to 1 Tick (100 ns), and the
   auto-step picker generates absurd labels (e.g. "02:40 / 22:13 / 17:46
   / 13:20 / 08:53" on a 24-hour range). Resize re-projects points at
   impossible coordinates → ghost geometry. See `InitSparkline()`.

8. **Single-point LineSeries leaks Geometry on resize.** LC2 v2's
   animation pipeline doesn't release the prior frame's geometry on
   resize ticks, accumulating ghost dots. For one-off markers (peak,
   anomaly), render as a XAML `<Ellipse>` in a Canvas overlay rather
   than via LC2. Positioned in code by `RepositionPeakOverlay()` using
   `DrawMargin` + data-fraction math.

9. **Persistent axis instances.** `_xAxis` / `_yAxis` are fields, not
   locals. Per `_chart-implementation-notes.md` §3, wholesale axis-array
   replacement combined with same-frame Series reassignment leaves LC2
   v2 in an inconsistent state.

10. **Tooltip + gridlines.** Brief §6.2: sparklines are trend-cues only.
    `SparklineChart.TooltipPosition = TooltipPosition.Hidden`. X-axis
    `SeparatorsPaint = null` AFTER `ChartTheming.Apply` (ChartTheming
    re-applies its own SeparatorsPaint). Y-axis `IsVisible = false`.

### Typography

11. **`text.mono` Style hardcodes `LineHeight=21` (body size).** When
    overriding `FontSize` for large mono displays
    (`font.size.title.large` = 40 px), the glyph overshoots the 21 px
    line box by ~24 px upward and climbs into whatever sits above.
    Override `LineHeight` explicitly: 40 px → `LineHeight=48`, 20 px →
    `LineHeight=28`. See `HeroTotalValue` / `HeroTotalUnit` /
    `HeroUpValue` / `HeroDownValue`.

### Spacing

12. **`Margin/Padding` cannot bind to `Double` tokens.** `space.*`
    tokens are `sys:Double`; `ThicknessConverter` only converts from
    String. Write literal Thickness values (`Margin="24"`), not
    `{StaticResource space.24}`. Already in `CLAUDE.md` and the memory
    file; called out here because it bit me twice.

### Copy

13. **No em-dash in user-facing prose.** Period / colon / semicolon
    instead. En-dash IS permitted in numeric ranges ("Jun 1 – Jun 7").
    Memory file `feedback_no_emdash_in_ui_copy.md`.

---

## Mock-data state (Phase 2)

`ReportsPage.xaml.cs` carries these as `const` / `static readonly`
fields. Phase 5 replaces them with real IPC values.

- `MockDate = new DateTime(2026, 6, 8)` — the page's canonical "today" reference.
- Hero values: **9.9 GB total** (`▼3% vs 7-day avg`) · Up **1.2 GB** (`▲+18%`) · Down **8.7 GB** (`▼6%`).
- WAN/Local: **73% / 27%**.
- Sparkline: 24 hourly points, peak **290 units at hour 19** (mockup canonical).
- Y axis `MaxYValue = 320` (head-room over peak).
- Anchor default: 7-day average. Options resolve range captions against `MockDate - 1` day.

---

## Phase 3 — Notable + Top Apps + Uncommon Talkers

### Scope

Expand the page Grid to 6 rows:

| Row | Content | Status |
|---|---|---|
| 0 | Chrome cluster (title + date + "vs" + anchor + Export) | ✓ Phase 2 |
| 1 | Status banner (Collapsed default) | ✓ Phase 2 |
| 2 | Hero card | ✓ Phase 2 |
| 3 | **Notable today** | ◻ Phase 3 |
| 4 | **Top Apps** | ◻ Phase 3 |
| 5 | **Uncommon Talkers** | ◻ Phase 3 |

### Notable today (Q7a)

- Section header `"Notable today"` + caption `"Alert-eligible observations for the day. Read-only echo of the Alerts feed."`
- Three severity sections in priority order: **Critical → Warning → Info**, each with a count chip in the section header.
- Each section contains incident cards with:
  - 3 px left "sevbar" `BorderThickness="3,0,0,0"` in `status.{critical|caution|neutral}`.
  - Tinted icon tile (`Border` with `Background={DynamicResource status.{critical|caution|neutral}.background}`) containing `ShieldError20` / `Warning20` / `Info20`.
  - `text.body.strong` title + `text.body` `text.secondary` detail line.
  - Entity-ref row in `font.mono caption`: e.g. `App · updater_x.exe · pid 8841 · 14:22`.
  - `Alerts · #N` chip — visible-but-inert. Navigation wires in **sprint Phase 6** alongside the Alerts feed implementation (documented deferral in the sprint plan).
- Use `shadow.sm` (softer than `shadow.card`) on incident cards — they sit grouped within a section, not as primary data cards.
- MVP knows ONE rule: `UnsignedFromUserPath`. Phase 3 mock: 1 Critical + 1 Warning + 1 Info for visual coverage of the severity treatment.

### Top Apps (Q4a)

- Card on canonical metal-card recipe: `metal.card` + `border.card` + `shadow.card` + `radius.card`.
- Card header strip: `"Top apps"` (left, `text.body.strong`) + right-aligned caption `"ranked by total bytes · top 10"` (`text.caption text.secondary`).
- DataGrid carries `style.datagrid.compact` (row 32, padding 12,0 since the 2026-06-11 descender fix) — same vocabulary as Per-App.
- Columns: App / Publisher / Signature / Up / Down + a trailing zero-width hover-chevron column.
- App cell: small icon tile (`accent.subtle` background) + glyph (Globe24 / Home24 / Shield20 — placeholder until app-icon IPC field lands) + `ImageName` text.
- Publisher: `cell.body.secondary.trim`.
- Signature: `cell.signature` style (Foreground-only DataTrigger for Unsigned/Invalid → `status.caution`).
- Up / Down: `cell.mono.right`.
- Single-click drill: `PreviewMouseLeftButtonUp` walks to DataGridRow, navigates to History with `(app, date)` params. History's filter capability lands in Phase 5 — the navigation call is a no-op destination until then. `Cursor=Hand` + brand-violet selection chrome telegraph clickability.
- MaxHeight enforced via the `EnforceDataGridBounds` pattern (Per-App / App Detail precedent) — NavigationView wraps the page in a `DynamicScrollViewer`; the DataGrid needs an explicit MaxHeight bound or virtualization fails.
- Mock data: 10 rows including 1 **Unsigned** (`updater_x.exe` from %TEMP%) and 1 `Publisher = "(unknown)"` (`backup_sync.exe`).

### Uncommon Talkers (Q5b)

- Section header `"Uncommon talkers"` + caption `"Apps behaving unusually for themselves — independent of byte rank."`
- Three category cards in a horizontal row:
  - **New today** — header glyph `Sparkle24` (or similar) in `status.neutral`, count chip.
  - **Unusual volume** — glyph in `status.caution`, count chip.
  - **Risky paths** — glyph in `status.critical`, count chip.
- Each category card: header strip + N anomaly rows.
- Anomaly row: app icon tile + (`AppName · Publisher · Signature`) + reason caption line.
- Row chrome: hover chevron + hand cursor + single-click drill to History.
- Card surfaces use `metal.card` + `border.card` + `shadow.sm` + `radius.md`.
- **NOT byte-rank capped** — discovery > ranking principle (§11 of the brief).

### Phase 3 files touched

- `src/ZenVizor.Ui/Views/ReportsPage.xaml` (expand rows 3-5)
- `src/ZenVizor.Ui/Views/ReportsPage.xaml.cs` (mock data + drill handlers)

### Phase 3 validation gate

- All three surfaces render with mock data in both themes.
- Severity colors match the Alerts entity vocabulary.
- Drill chevron appears on hover for both DataGrid rows AND Uncommon Talkers category rows.
- DataGrid virtualizes (MaxHeight pattern works — verify via "scroll bar appears" + DataGridRow count in debug output).
- Page scrolls as a whole (NavigationView's wrapping ScrollViewer).
- HC sweep (toggle Windows HC, verify each surface degrades reasonably).
- Em-dash audit clean on every visible string.

---

## Phase 4 — State coverage

Per brief §4. Drive each state via a private state-machine helper that
swaps the data + banner + Opacity.

| State | Treatment |
|---|---|
| `default` | Phase 2-3 baseline. |
| `empty — zero traffic` | Page-level centered `DocumentText48` + `"No traffic recorded on {date}."` + `"Nothing was observed talking to the network on this date. Pick another day above."` All data cards collapsed; Export disabled. |
| `quiet day` | Hero with small real totals; Top Apps holds a handful of rows; Notable shows soft success-check + `"Nothing notable today."`; Uncommon Talkers shows `"A deliberately quiet day. No app strayed from its usual pattern."` |
| `loading` | Per-card centered `ui:ProgressRing`; `"Generating report…"` caption after 1 s `DispatcherTimer` delay (HistoryPage pattern). Export disabled. |
| `disconnected` | `status.critical.background` banner with `PlugDisconnected20` glyph + `"Service disconnected. Last refresh stale."` Last-known data at `Opacity=0.6`. Export disabled. |
| `error` | `status.caution.background` banner with `Warning20` glyph + `"Report failed: {ExceptionMessage}"` Last-known at `Opacity=0.6`. |

Banner pattern adapts `HistoryPage.xaml.cs:127-177` verbatim, including
the cross-page `HistoryQueryClient.IsConnectionLost(ex)` predicate that
catches `TimeoutException` (the cross-page fix landed during the History
polish round). See `findings/history.md` for the fix history.

### Phase 4 files touched

- `src/ZenVizor.Ui/Views/ReportsPage.xaml.cs` (state machine + 1 s delay timer)
- `src/ZenVizor.Ui/Views/ReportsPage.xaml` (state copy + empty-state shell)

### Phase 4 validation gate

- Cycle every state via a mock driver function in code-behind.
- Banner glyph swap fires correctly.
- Export disable wires correctly.
- HC contrast clears in every state (banners must remain readable).
- Em-dash audit clean.

---

## Phase 5 — IPC contract + real data + CSV/HTML export + History drill

This is the **sprint plan Phase 5 deliverable**. See
`docs/zenvizor-sprint-plan.md` §Phase 5 for the CI + manual gates.

### IPC contract

- Add `GetDailyReportAsync(DateTime date, AnchorMode anchor)` to
  `src/ZenVizor.Ipc.Contracts/IZenVizorIpc.cs`.
- New DTO: `src/ZenVizor.Ipc.Contracts/Dto/DailyReportResult.cs` carrying:
  - Hero numerics (TotalUp / TotalDown / WanRatio / LocalRatio) + deltas vs anchor.
  - Hourly traffic shape (24-point series).
  - Top apps list (10 rows).
  - Uncommon talkers by category (New today / Unusual volume / Risky paths).
  - Notable items by severity.
- Lock at this phase: the provisional fields the Q5 / Q7 design assumes —
  per-app rolling median, publisher-first-seen flag, WAN-ratio baseline,
  path-class.

### Service-side aggregator

- New: `src/ZenVizor.Core/...` or `src/ZenVizor.Storage/...` — TBD by
  the maintainer (likely Storage since it reads SQLite rollups).
- Reads daily / hourly rollups produced by the existing Phase 4 jobs.
- Stays out of the user's filesystem — CSV/HTML serialization is UI-side.

### UI wire-up

- Replace mock fixtures in `ReportsPage.xaml.cs` with real IPC calls
  bound to `PrimaryDatePicker.Date` + anchor selection changes.
- Date / anchor changes trigger an async refresh via the existing
  `HistoryQueryClient`-style pattern.

### CSV serializer

- New: `src/ZenVizor.Ui/Services/DailyReportCsvWriter.cs`.
- UTF-8 with BOM, headered rows, one section per surface (`# Top apps`,
  `# Uncommon talkers`, `# Notable today`).
- Brief §17: `zenvizor-report-YYYY-MM-DD.csv` filename template.

### HTML serializer

- New: `src/ZenVizor.Ui/Services/DailyReportHtmlWriter.cs`.
- Self-contained: no remote refs, no CDN, no remote fonts, no embedded
  analytics. Inline CSS projected from `docs/design/colors_and_type.css`.
- Brief §15 + §17: top-right callout `Generated locally · No network used`
  is required.
- Brief §10: target <500 KB for typical day.
- Open in browser via `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`.

### Drill plumbing

- `HistoryPage` grows `(appId, date)` filter capability
  (`findings/history.md` F8 — separate workstream within the same
  sprint). Reports' drill `Frame.Navigate(typeof(HistoryPage))` passes
  filter params.

### Phase 5 validation gate (= sprint plan Phase 5)

- Real data flows from the service.
- CSV opens cleanly in a spreadsheet.
- HTML opens in a browser; verify via DevTools network panel that
  **zero remote requests fire** when the HTML loads.
- Reconciles with the History view for the same date.
- Self-monitoring (zero-own-traffic) still passes with Reports open +
  exporting.
- Drill into History pre-filters correctly.
- Reconnect after service stop recovers cleanly (no
  `TimeoutException` regression — `HistoryQueryClient.IsConnectionLost`
  predicate is shared and already handles it).

---

## Open / deferred items

| Item | Status |
|---|---|
| `Alerts · #N` chip wiring on Notable cards | **Sprint Phase 6** (documented in sprint plan). |
| App-icon IPC field (real icons vs placeholder Globe24 / Home24 tiles) | Post-MVP. |
| Brand date format on F1 overlay | ✓ Working (`HorizontalContentAlignment="Right"` + overlay TextBlock + Binding StringFormat). |
| ui:CalendarDatePicker "Pick a date" internal placeholder | If it appears under the overlay, add a transparent Foreground override on the picker's internal text. Walk in Phase 3 QA. |
| ContextMenu shadow + corner radius | Rely on Wpf.Ui defaults — no brush keys exposed for retuning. |
| App Detail polish round | Will reuse Reports patterns (icon tile, ContextMenu, brush-key overrides). |

---

## How to pick up in a fresh chat (Phase 5 follow-up / regression triage)

The Reports surface is complete. New work on this page (Alerts deep-link
once Phase 6 wires alerts; future Phase D refresh on real data) lifts
from these files:

1. `src/ZenVizor.Ui/Views/ReportsPage.xaml` + `.xaml.cs` — page layout, state machine, drill handlers.
2. `src/ZenVizor.Storage/Repositories/DailyReportRepository.cs` — server-side aggregator (Hero / sparkline / Top Apps / Uncommon Talkers / Notable). `AlertId` is sentinel 0 until Phase 6 lands the alerts table.
3. `src/ZenVizor.Ui/Services/DailyReport{Csv,Html}Writer.cs` — export serializers; HTML is self-contained (no remote refs).
4. The IPC contract is `IZenVizorIpc.GetDailyReportAsync`; schema version is `IpcSchemaVersion.DailyReport` (v1).

---

## Memory entries referenced during Phases 1-2

The auto-memory at
`C:\Users\mitch\.claude\projects\C--dev-zenvizor\memory\` was load-bearing
multiple times. Specifically:

- `feedback_no_emdash_in_ui_copy.md` — applied to every visible string.
- `feedback_drill_grid_pattern.md` — hover chevron + hand cursor + single click. Reused for Phase 3 Top Apps.
- `project_canonical_card_treatment.md` — metal-card recipe is the per-page default. Every data card on Reports uses it.
- `project_wpf_spacing_token_thickness.md` — write literal Thickness values (`Margin="24"`), not token references.
- `project_wpfui_navigationview_scrollviewer.md` — NavigationView wraps pages in a DynamicScrollViewer. Top Apps DataGrid in Phase 3 needs explicit MaxHeight to virtualize.
- `project_discovery_principle.md` — applies on Uncommon Talkers (NOT byte-rank capped) but is OVERRIDDEN on Top Apps (Top-N legitimate for summary).
