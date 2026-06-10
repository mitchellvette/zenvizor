# Pre-brief — Reports (spec-derived, Group B)

Placeholder screen today. There is no current behavior to observe —
this doc derives from `docs/zenvizor-prd.md` §11 + `docs/zenvizor-sprint-plan.md`
Phase 5. **Do not record "current behavior" — there is none.**

This brief deliberately runs lighter on prescription than the other
Group B briefs (Alerts, Settings) because Reports is the page where
Claude Design has the most strategic latitude to shape a new
experience. Composition, top-apps surfacing, hero summary form,
notable-items presentation, temporal model, and HTML export
composition are all open in §9 rather than locked in §3. §3 lists
the outcomes the page must communicate; §9 carries the design
questions. **Do not transcribe §9 into §3** — that defeats the
point of opening them.

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

**Contract latitude.** The on-wire shape of `GetDailyReport` is not
yet locked. If §9B's temporal-model variant grows the contract to
accept a range or comparison form (or if §9D's uncommon-talkers
surface needs new payload fields), Phase 5 implements the chosen
shape. The brief may assume this latitude; it is not a "feature for
later" if it lands in §9.

## 3. Outcomes the page must communicate

> Composition is open to Claude Design — see §9 for the design
> questions. This section lists the outcomes the page must achieve,
> not the layout that delivers them.

The Reports page must let a casual user, in a single visit,
understand:

1. **What date this report covers.** Always visible; trivially
   changeable without losing context. Default = yesterday (today's
   report is incomplete by definition — this default is locked).
2. **The total volume of traffic for the date.** Up and Down bytes,
   readable at a glance from across the room.
3. **The WAN vs Local proportion of that traffic.** Conveyed
   visually, not just numerically — a number alone doesn't read
   at-a-glance.
4. **Which apps were responsible.** Per-row visible signals: app
   name, publisher, signature trust state, Up bytes, Down bytes.
   The user must be able to scan this and form a sense of "what
   did my machine do today."
5. **What was unusual.** Apps with anomalous patterns (MVP rule:
   "new unsigned-from-temp talker"; future rules per PRD §11) must
   visually separate from the ordinary-activity surface. Severity
   inherits the Alerts entity vocabulary
   (`Info / Warning / Critical`) so a notable item maps cleanly to
   its corresponding `Alert` when one exists.
6. **A path to drill into a specific app's activity for the date.**
   Selecting an app row routes the user to the History page
   pre-filtered to `(app, date)`. Drill destination is locked;
   the row-level affordance is open in §9F. History needs to grow
   the `(app, date)` filter capability — tracked in
   `docs/design-briefs/findings/history.md`.
7. **Export of the report in one action.** A single Export menu
   surfaces CSV and HTML choices; the actions row stays minimal
   and future-proof for additional formats (excluding PDF and
   email — both invariant-blocked).
8. **Discovery alongside ranking.** "Top apps for the day" is a
   *ranking* surface — necessary but insufficient for finding
   anomalous behavior, because a stealthy malicious process won't
   rank highly by bytes. The page MUST include a complementary
   surface that telegraphs *apps that don't typically see this
   kind of traffic*, independent of byte-rank. This is the
   principle in `project_discovery_principle.md` applied here:
   ranking does not replace discovery. See §9D for how each
   surface renders.

Locked durable elements that any composition must honor:

- Page outer margin = `space.24`.
- All data-bearing cards take the canonical metal-card treatment
  (`metal.card` + `border.card` + `radius.card` + `shadow.card`).
- Date picker uses `Wpf.Ui.Controls.CalendarDatePicker` (the
  primitive; if §9B selects a range/comparison form, this becomes
  a pair or a custom range control — flag in the brief).
- Notable items rendered as items (cards / list rows / sections),
  not as rows of a `DataGrid`: heterogeneous payload (severity +
  title + detail + entity context) makes `DataGrid` columns the
  wrong primitive.

## 4. State coverage

| State | Treatment |
|---|---|
| empty (no data for date) | Full-page "No traffic recorded on {date}." centered `text.body` `text.secondary`. Date picker stays visible above. (See §9G — a sparse but non-zero day may want richer treatment than this naked empty state.) |
| loading | Default Fluent `ProgressRing` centered. NO shimmer. Caption "Generating report…" if wait exceeds ~1 s (server-side aggregation can take a moment for historical dates). |
| disconnected | `status.critical.background` banner inline beneath header: "Service disconnected — last refresh stale." Hide / disable the Export menu. |
| error (query failed) | `status.caution.background` banner: "Report failed: `<msg>`". Date picker stays operative for retry. |
| no warming state | History surface. |

## 5. Locks and provisional

The brief **locks** the durable layers:

- Visual language (tokens, type ramp, density per primer).
- IA placement (fourth nav-rail item, `Symbol="DocumentText24"`,
  `NavigationCacheMode.Enabled`).
- State matrix (§4).
- Export format support: **CSV + HTML, surfaced via a single
  Export ▾ menu** — keeps the actions row minimal and future-proofs
  for additional formats. PDF and email remain invariant-blocked.
- Severity vocabulary for "Notable today" inherits the Alerts
  entity scheme: `Info / Warning / Critical`. Where a notable
  item has a corresponding `Alert` raised, the rendering must be
  visually coherent enough that the user reads them as the same
  observation.
- The Reports page gets a `discovery > ranking` override for its
  top-apps surface (per §8.3): Top-N framing is legitimate
  *only* on the ranking surface, because Reports is a *summary*
  surface explicitly framed "top apps for the day." Per-App
  remains the canonical uncapped drill complement. **The
  parallel "uncommon talkers" surface (§9D) IS the discovery
  surface that satisfies the principle on this page** — the
  override is not a free pass to drop discovery, it is a
  partition.
- The drill destination from a top-apps row is the History page
  pre-filtered to `(app, date)`. History needs to grow that
  filter capability — see `docs/design-briefs/findings/history.md`.
- Default date = yesterday. Today's report is incomplete by
  definition.

The brief **flags as provisional**, to lock at Phase 5
implementation:

- Exact `GetDailyReport` payload field list. PRD §11 enumerates
  intent; the on-wire shape is locked when the contract lands.
  **The contract may grow** to accept a range or comparison form
  if §9B's variants land on one.
- "Notable today" categorization vocabulary (which conditions
  qualify) and the per-category icon set. MVP knows one rule:
  "new unsigned-from-temp talker." More rules arrive post-MVP as
  future `IMonitor`s land (PRD §10).
- The "uncommon talkers" signal definition (§9D). PRD does not
  yet specify per-app baselines; the design surface in §9D may
  require new payload fields (e.g. per-app rolling median,
  publisher-first-seen flag, WAN-ratio baseline). Provisional
  until §9D lands.
- Date-picker bounds (likely tied to retention settings).
- Whether the date control is a single-date picker, a range
  picker, or a comparison picker — depends on §9B.
- Export filename template (`zenvizor-report-YYYY-MM-DD.csv`
  etc.) and the HTML template's implementation details. The
  *composition* of the HTML report is open in §9I.

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

The composition Claude Design proposes from §3's outcomes and §9's
open questions. The brief asks for both mocks side by side so the
implementer can ship (a) immediately and (b) lands in Phase 5.

## 7. WPF translation gotchas

Control- and primitive-level reminders for whichever composition
Claude Design lands on. These do NOT pre-lock composition.

- **Date picker:** use `Wpf.Ui.Controls.CalendarDatePicker` (not
  stock WPF `DatePicker`) so the styling matches the rest of the
  Fluent surface. Bind to a `DateTime?` view-model property;
  refresh on selection change. If §9B selects a range or
  comparison form, the picker becomes a `CalendarDatePicker` pair
  or a custom range control — flag in the brief as a contract
  consequence.
- **If the notable-items surface uses a `ListView`:** apply the
  `NavigationView`-wraps-page caveat — set `MaxHeight`
  programmatically on `Loaded` + `SizeChanged` so the
  `VirtualizingStackPanel` virtualizes. Memory:
  `project_wpfui_navigationview_scrollviewer.md`.
- **If a horizontal WAN/Local stacked bar is used in the hero
  summary:** render as two `<Border>`s filled `chart.wan` /
  `chart.local` with widths bound to proportions — NOT a
  `lvc:CartesianChart`. LC2 is overkill for one bar of two
  segments and pays measurable per-frame cost (light-and-fast).
- **Export menu:** single `ui:Button` with `SymbolIcon
  ArrowExportLtr24` (or similar) opening a `ui:MenuFlyout`
  carrying `CSV` and `HTML` items. Each item opens
  `Microsoft.Win32.SaveFileDialog` pre-populated with the
  appropriate extension; the UI reads the report payload over
  IPC and formats client-side. **The service stays out of the
  user's filesystem.**
- **"Open in browser"** (when surfaced after HTML export):
  `Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute
  = true })`. Standard Windows shell — not a network call. The
  exported HTML file must be self-contained (no external CDN
  refs) to honor zero-own-traffic when the user opens it.
- **Drill from a top-apps row → History:** navigation passes
  `(app, date)` filter params to `HistoryPage`. History does NOT
  currently accept these — capability flagged in
  `docs/design-briefs/findings/history.md` and lands with Phase 5.
  Until History grows the filter, the drill affordance is a
  Phase 5 implementation hook, not a polish-interlude wire-up.

## 8. Out-of-scope — invariant-blocked only

The list below is invariant-blocked (zero-traffic, passive-only),
not "feature, for later." Items previously parked here as
"feature" (multi-day rollup, compare dates side-by-side,
click-through to App Detail filtered to a date) are now open in §9.

- **PDF export.** PRD §3.2 / §11.4 — explicitly deferred
  indefinitely.
- **Email the report.** Would emit network traffic. Hard NO per
  zero-own-traffic invariant.
- **Schedule daily report delivery.** Implies email or file-drop
  side-effect; the email path is invariant-blocked above, and
  scheduled delivery conflicts with "passive, user-initiated
  reporting."
- **Active-action affordances on Reports rows.** No "block this
  app," no "kill this process." Passive-only invariant — HARD NO.
- **In-app "report ready" surfacing on Reports itself.** If we
  ever notify the user that yesterday's report is ready, it lands
  on the Alerts page, not as decoration on Reports. Reports is
  the destination, not the announcement.

## 9. Open design questions for Claude Design

The brief deliberately leaves these open. Each invites Claude
Design to propose variants the user picks from during iteration.
**This is the section that makes the design round produce value
over the brief alone** — without it, Claude Design transcribes §3
into paint rather than designing the reporting experience.

### A. Page composition

**Open:** how should the Reports page compose? The locked
outcomes (§3) tell Claude Design what must appear; the
arrangement is open. The composition must accommodate the other
open items on this list — hero summary form (C), top-apps
treatment AND the parallel uncommon-talkers surface (D), notable
items presentation (E), the export menu, the drill affordance on
top-apps rows (F).

*Constraints:* outer `space.24` margin; data-bearing cards on
canonical metal-card treatment; page may scroll OR may fit
without scroll — Claude Design picks; the nav rail and any
chrome are fixed outside the page surface.

*Variants:* 2–3. Suggested starting points (Claude Design may
propose others):

- A single scrollable column: summary → ranking → discovery →
  notable items.
- A two-column dashboard-style report: summary spans the top;
  ranking and discovery sit side-by-side beneath; notable items
  band across the bottom.
- A narrative "today's story" composition: notable items + hero
  summary lead as the headline; ranking and discovery sit
  below as supporting evidence.

### B. Temporal model

**Open:** is "one date, one report" the right temporal frame, or
should the page natively support comparison (yesterday vs today,
week-over-week) and/or a rolling N-day digest?

*Constraints:* this is the strategic temporal decision for the
Reports surface. Variants **may assume IPC contract latitude** —
the `GetDailyReport(date)` contract is not locked yet; Phase 5
implements the chosen shape. The date picker control becomes a
range or comparison primitive if a multi-date variant wins
(propagates to §7's date-picker gotcha).

*Variants:* 2–3.

- **(a) Single-day pure.** What §3's outcomes describe today.
  Simplest; aligns with PRD §11's "daily overview" framing.
- **(b) Single-day with comparison.** One primary date plus a
  comparison anchor (yesterday vs today, or vs same day last
  week). Hero summary shows deltas; notable items can flag
  "more than yesterday."
- **(c) Rolling N-day digest.** Date picker becomes a window
  picker (1d / 7d / 30d / 90d); the page summarizes the trailing
  range. Pure daily becomes a degenerate case (1d window).

### C. Hero summary visualization

**Open:** how should "the day at a glance" read in one or two
seconds? The numerics (Up bytes, Down bytes, WAN/Local
proportion) must appear (§3-2, §3-3); the *form* is open.

*Constraints:* readable at a glance from across the room;
WAN/Local proportion conveyed visually (§3-3); uses `chart.wan` /
`chart.local` for any categorical viz (the brief lists these
under §6.1 even when the consumer isn't a `lvc:CartesianChart`);
static viz only (no animated fills, no continuous animation —
light-and-fast); accommodates whichever §9B variant wins (a
comparison form must have room for delta indicators; a digest
form must read at the chosen N-day scope).

*Variants:* 2–3. Examples Claude Design may propose:

- Eyebrow + large mono Up/Down values + horizontal stacked
  WAN/Local bar.
- Headline total-bytes number + supporting Up/Down split +
  sparkline of the day's traffic shape.
- Hourly heatmap strip across the top conveying "when did the
  day's traffic happen" alongside the totals.

### D. Top apps AND uncommon talkers — parallel surfaces

**Open:** how should the page render the two surfaces that
together cover ranking AND discovery?

*Top apps* (ranking — bytes-descending, Top-N legitimate per §5
override) and *Uncommon talkers* (discovery — apps whose
activity today is anomalous relative to their own pattern) are
parallel surfaces with different purposes. The user must never
confuse "high byte usage" with "anomalous behavior." A given app
may appear on both lists, on one, or on neither, and the visual
treatment must telegraph **why** each row is on each list.

*Constraints — Top apps:*

- Top-N is the right framing here (ranking-summary surface).
  DataGrid is one reasonable primitive — and "Top 10 list" style
  is the closest neighbor in the project's existing vocabulary
  — but ranking surfaces also invite alternatives: bar chart
  with publisher/signature inline, ranked cards with sparklines,
  treemap. Claude Design may propose alternatives to DataGrid
  with rationale.
- Per-row visible signals: app name, publisher, signature trust
  state (per §3-4), Up bytes, Down bytes.
- Signature trust state (Unsigned, Invalid, Unchecked) must be
  distinguishable at a glance — same constraint as Per-App and
  App Detail's grids.
- Density follows the project's compact pattern when DataGrid is
  chosen; alternative primitives carry their own scale.

*Constraints — Uncommon talkers:*

- The signal is "apps whose activity today is unusual for them"
  — candidates: apps that don't typically transmit; apps from
  user-writable paths (already the MVP alert basis); first-seen
  publishers; anomalous WAN ratio vs the app's baseline; spike
  vs the app's rolling median.
- The data shape for this signal is provisional (§5); the
  design proposal informs what the `GetDailyReport` contract
  carries (per-app baseline, publisher-first-seen flag, etc.).
  Claude Design proposes the surface; the proposal feeds the
  contract decision at Phase 5.
- Must NOT be capped by bytes-rank — the entire point is to
  surface low-byte-but-anomalous apps that ranking misses.
  Discovery > ranking applies here per
  `project_discovery_principle.md`.
- Must visually distinguish from Top Apps so the user reads
  them as different lenses, not "the same list but smaller."

*Variants:* 2 for Top Apps; 2 for Uncommon Talkers. Pair each
with rationale.

### E. Notable items presentation

**Open:** how should "Notable today" render? Severity grouping
and incident-card framings are the most promising directions —
not a generic list with category icon + caption.

*Constraints:*

- Severity vocabulary inherits Alerts entity (§5 lock):
  `Info / Warning / Critical`.
- An item that has a corresponding `Alert` raised must be
  visually coherent with the Alerts page rendering of the same
  observation, so the user reads them as the same event.
- Notable items are heterogeneous payloads — severity, title,
  detail, entity reference, optional bytes/time. DataGrid
  columns are the wrong primitive.
- Distinct from Uncommon Talkers (§9D): Notable items are
  *raised observations* (alert-eligible events); Uncommon
  Talkers is a *baseline-relative signal* (no alert needed to
  appear). The user must read them as different surfaces.

*Variants:* 2.

- Severity-grouped sections (Critical / Warning / Info), each
  containing incident cards.
- Mixed-severity feed with severity-tagged incident cards in
  reverse-chronological order.

### F. Drill affordance on top-apps rows

**Open:** how should it become obvious to the user that
selecting a top-apps row drills into History pre-filtered to
`(app, date)`?

*Constraints:* drill destination is locked (History
pre-filtered to `(app, date)`); affordance is passive
(presentation-only, no action affordance per §13); single-click
drill per `feedback_drill_grid_pattern.md` (hover chevron + hand
cursor + single click) — but this convention is established for
DataGrid-like surfaces, and Top Apps may not be a DataGrid (see
§9D). The affordance must read correctly whether the row is a
grid row, a card, a bar in a chart, or a cell in a treemap.

*Variants:* 1–2.

### G. Quiet-day / sparse-day treatment

**Open:** how should a low-activity day feel? The current
default is one empty-state line ("No traffic recorded on
{date}"). A page that frequently has little to report shouldn't
read as "broken" or "empty" — it should read as "deliberately
quiet" on the days when not much happened.

*Constraints:* low effort — one variant proposal with rationale.
Reuses existing tokens; no new tokens. Distinct from the empty
state in §4 (which is the genuinely-zero case).

*Variants:* 1.

### I. HTML export composition

**Open:** how should the exported HTML report compose? The HTML
is a standalone document — no nav rail, no app-specific chrome,
viewed in a browser, archived to disk, occasionally pasted into
incident docs.

*Constraints:*

- Resembles the in-app composition in hierarchy (summary, top
  apps, uncommon talkers, notable items) so the user reads them
  as the same artifact — not a divergent document.
- No nav rail or app-specific surrounding chrome.
- Self-contained — no external CDN refs, no embedded analytics,
  no remote fonts (honors zero-own-traffic when the user opens
  it).
- Print-friendly — the user may save-as-PDF via the browser
  (this is NOT first-party PDF export; it's the user's path).
- Uses the same design tokens as the in-app surfaces, projected
  to CSS variables per `docs/design/colors_and_type.css`.

*Variants:* 1 mock of the HTML composition; annotate which
tokens carry over to CSS variables.

---

**H (export menu) is locked, not open** — surfaced as a single
`Export ▾` button with a `CSV` / `HTML` flyout menu (see §3-7,
§5, §7). Claude Design composes its placement and visual
treatment within the chosen page composition, but the affordance
shape itself is settled.

**J (in-app report-ready surfacing) is killed** — the
"yesterday's report is ready" notification path lands on the
Alerts page, not Reports. Reports is the destination, not the
announcement.
