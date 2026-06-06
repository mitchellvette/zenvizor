# Pre-brief — Reports (spec-derived, Group B)

Placeholder screen today. There is no current behavior to observe —
this doc derives from `docs/zenvizor-prd.md` §11 + `docs/zenvizor-sprint-plan.md`
Phase 5. **Do not record "current behavior" — there is none.**

---

## 1. Purpose & IA placement

- **Purpose:** in-app daily overview report for a chosen date, plus
  CSV/HTML export. PRD §11 calls this "the headline deliverable" of
  Phase 5.
- **IA placement:** fourth item in the left nav rail.
  `Symbol="DocumentText24"`. `NavigationCacheMode.Enabled`. Today
  routed to `ReportsPage : PlaceholderPage` with subtitle "Daily
  overview + CSV/HTML export — Phase 5."

## 2. Requirements lifted from the spec

From PRD §11.1 and Phase 5 scope:

- **`GetDailyReport(date)`** structured payload containing:
  - Top apps for the day (with their byte totals).
  - Daily up / down totals.
  - WAN vs Local split.
  - Notable items — e.g. "new unsigned-from-temp talker"
    (anomalies surfaced for the day; NOT alert feed entries — Alerts
    has its own page).
- **In-app Daily Report view.** Date selectable; default is "today"
  or "yesterday" (lock at Phase 5 implementation).
- **Export:** CSV + HTML, written from the UI side to a
  user-chosen location. **No PDF** — explicitly deferred indefinitely
  (PRD §3.2, §11.4).
- **No own-network traffic.** Export is a local file write; HTML
  preview opens in the user's default browser via shell. No fetch of
  external assets, no CDN, no embedded analytics.

From PRD §9.1: `GetDailyReport` is a history-query path (cost
profile = SQL aggregation), distinct from the live-snapshot path the
Dashboard uses. Should not poll; refresh on date change.

## 3. Proposed layout & interaction (durable layers)

> The layout below locks the visual language, IA, layout, and
> interaction. Data specifics (exact field rendering, "notable items"
> thresholds, export filename templates) are **provisional** and lock
> at Phase 5 implementation. See §5.

Root: `<Grid Margin="24">` with rows
(`Auto / Auto / Auto / Auto / *`).

### Row 0 — header

- `<ui:TextBlock>` with `Style="{StaticResource text.subtitle}"`,
  content "Reports".
- `text.caption` `text.secondary` subtitle beneath: "Daily overview
  of network activity by app, with export."

### Row 1 — date picker + actions row

- Date picker (`ui:CalendarDatePicker` or stock `DatePicker` — decide
  in brief; Wpf.Ui has `CalendarDatePicker`). Default = yesterday
  (today's report is incomplete by definition).
- Two `ui:Button`s on the right: `Export CSV` (with
  `SymbolIcon="DocumentArrowDown24"` or similar), `Export HTML`. A
  third small button `Open in browser` once HTML exists locally — or
  fold into the HTML export's success state.

### Row 2 — hero summary card

- `Border surface.card radius.card padding=space.16`.
- Content: large display of the day's totals.
  - `text.eyebrow` "Daily total".
  - `text.title.large` `Up: {bytes}` and `Down: {bytes}` — two
    values side by side, `text.mono`.
  - Horizontal stacked bar visualizing the WAN vs Local split.
    Two segments: `chart.wan` and `chart.local`. Width proportional
    to bytes; label `"WAN 73% · Local 27%"` (or with absolute bytes,
    decide in brief).

### Row 3 — top apps card

- `Border surface.card radius.card padding=space.16`.
- `text.subtitle` header: "Top apps".
- `text.caption` `text.secondary` subtitle: "Most-talked apps for
  the day."
- A DataGrid (compact density, same as Per-App) or a ListView styled
  like Per-App's compact rows — same control vocabulary as the
  Per-App page. Columns: `App`, `Publisher`, `Signature`, `Up`, `Down`.
- **Top-N is the right call here**: this is a *daily summary*
  surface explicitly framed as "top apps." Per-App remains the
  discovery surface where Discovery > ranking applies. Cap at top
  10 OR show all apps that had any traffic for the day — decide
  in brief; my preference is show all (no cap) because daily
  reports are an investigative surface, not a glance.

### Row 4 — notable items list

- `Border surface.card radius.card padding=space.16`.
- `text.subtitle` header: "Notable today".
- `text.caption` `text.secondary` subtitle: "Apps and events flagged
  for review."
- A vertical `ListView` (NOT DataGrid — rows are heterogeneous: each
  notable item has a category icon + title + caption + optional
  bytes / time).
- Item template: `ui:SymbolIcon` (category-specific) + two-line text
  block (`text.body.strong` title, `text.caption` `text.secondary`
  detail) + right-aligned `text.mono` byte total or timestamp.
- Empty state: centered `text.body` `text.secondary` "Nothing
  notable today."

## 4. State coverage

| State | Treatment |
|---|---|
| empty (no data for date) | Full-page "No traffic recorded on {date}." centered `text.body` `text.secondary`. Date picker stays visible above. |
| loading | Default Fluent `ProgressRing` centered. NO shimmer. Caption "Generating report…" if wait exceeds ~1 s (server-side aggregation can take a moment for historical dates). |
| disconnected | `status.critical.background` banner inline beneath header: "Service disconnected — last refresh stale." Hide / disable Export buttons. |
| error (query failed) | `status.caution.background` banner: "Report failed: `<msg>`". Date picker stays operative for retry. |
| no warming state | History surface. |

## 5. Provisional-data flag — MANDATORY

The brief **locks** the durable layers:

- Visual language (tokens, type ramp, density).
- IA placement and the four-section composition (header / picker /
  hero summary / top apps / notable items).
- Interaction model (date selection refreshes; export buttons emit
  files via standard file dialog).
- State matrix (empty / loading / disconnected / error).
- Export format support (CSV, HTML; no PDF).

The brief **flags as provisional**, to lock at Phase 5
implementation:

- Exact `GetDailyReport` payload field list (PRD §11 enumerates the
  intent — top apps, totals, WAN/Local split, notable items — but
  the on-wire shape is locked when the contract lands).
- "Notable today" categorization vocabulary and per-category icons
  (the rule for what counts as notable lives in the alert/anomaly
  derivation logic in Phase 5; today the only specified rule is
  "new unsigned-from-temp talker" — PRD §11).
- Default date (today vs yesterday) and the date-picker bounds (how
  far back the date picker scrolls — likely tied to retention
  settings).
- Export filename template (`zenvizor-report-YYYY-MM-DD.csv` etc.)
  and the HTML template's actual visual styling — those are
  implementation calls, not mockup deliverables.
- Whether "Top apps" caps at 10 or shows all apps for the day.

## 6. Two designed states — MANDATORY

Reports is a placeholder today. The brief MUST request both:

### (a) Interim placeholder treatment

Wears in the shipped polish-interlude app between now and Phase 5,
so the page doesn't look unfinished next to the four polished
screens during QA.

- Centered StackPanel on `surface.background` (no card — read as
  deliberate "empty state").
- `<ui:SymbolIcon Symbol="DocumentText48"
  Foreground="{DynamicResource text.tertiary}">` (or whichever Symbol
  matches the nav-rail icon at a larger size).
- `text.title.large` "Daily reports".
- `text.body.large` `text.secondary` "Coming in Phase 5 — date
  picker, top apps, WAN/local split, and CSV/HTML export."
- Optional `text.caption` linking to the tracking issue or sprint
  plan section if useful.

This treatment is **different from today's `PlaceholderPage.xaml`** —
today's is `FontTypography="TitleLarge"` + a default body subtitle,
ungrounded in the design system. The polish-interlude treatment
reads as deliberate.

### (b) Eventual functional layout

The layout in §3 above. The brief asks for both mocks side by side
so the implementer can ship (a) immediately and (b) lands in Phase 5.

## 7. WPF translation gotchas

- **Date picker:** Wpf.Ui has `CalendarDatePicker`. Use it (not
  stock WPF `DatePicker`) so the styling matches the rest of the
  Fluent surface. Bind to a `DateTime?` view-model property; refresh
  on selection change.
- **Notable items list:** `ListView` with custom `ItemTemplate`.
  Same NavigationView-wraps-page caveat applies — if the list grows
  beyond the page height, set `MaxHeight` programmatically per the
  `EnforceDataGridBound` pattern. Memory:
  `project_wpfui_navigationview_scrollviewer.md`.
- **WAN/Local stacked bar:** simple `Grid` with two `<Border>`s
  filled `chart.wan` / `chart.local`, widths bound to proportions —
  NOT a LiveCharts2 chart for one bar of two segments.
- **Export buttons:** standard `Microsoft.Win32.SaveFileDialog` for
  filename + location. Writes CSV / HTML synchronously from the UI
  side; the UI does the file I/O, not the service (service stays
  out of the user's filesystem). Reads payload over IPC, formats
  client-side.
- **"Open in browser":** `Process.Start("explorer.exe", htmlPath)`
  or `Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute
  = true })`. Standard Windows shell — not a network call. HTML file
  must be self-contained (no external CDN refs) to honor the
  zero-own-traffic invariant when the user opens it.

## 8. Out-of-scope — features flagged for later

- **PDF export.** PRD §3.2 / §11.4 — explicitly deferred
  indefinitely. Do not include in mock.
- **Email the report.** Would emit network traffic. Hard NO per
  zero-own-traffic invariant.
- **Schedule daily report delivery.** Implies email or file-drop;
  feature for later (and email is hard-blocked above).
- **Multi-day rollup / week-at-a-glance** view. Adds a new aggregate
  surface — feature.
- **Compare two dates side-by-side.** New interaction model.
- **Click-through from a Top Apps row to App Detail filtered to that
  date.** Cross-page state coordination — feature.
- **Active-action affordances.** Same passive-only invariant —
  no "block this app" anywhere on Reports.
