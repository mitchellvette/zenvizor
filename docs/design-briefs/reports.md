# Claude Design brief — Reports

ZenVizor's Reports screen. Self-contained brief for a Claude Design
session whose prior pass already loaded `docs/claude-design-primer.md`
and aligned to ZenVizor's token surface. Paste this brief ALONE; do
not re-paste the primer. The mockup hand-off contract is in §19.

> **This brief deliberately runs lighter on prescription than Alerts
> and Settings.** Reports is a new feature surface where Claude
> Design has the most strategic latitude to shape the reporting
> experience. §3 names the outcomes the page must communicate; §8.4
> carries eight open questions that drive composition, surfacing,
> and visualization choices. §3 is NOT a layout spec — do not
> transcribe it into a row-by-row mock.

---

## 1. Screen identity

- **Screen name:** Reports.
- **XAML file:** does not exist yet — Reports ships in Phase 5. The
  current `src/ZenVizor.Ui/Views/ReportsPage.xaml` is a placeholder
  routing to `PlaceholderPage` with subtitle `"Daily overview +
  CSV/HTML export — Phase 5."`. The Phase 5 implementation lands a
  real `ReportsPage` whose composition is determined by this brief.
- **IA placement:** fourth item in the left nav rail.
  `Symbol="DocumentText24"`. `NavigationCacheMode.Enabled`.
- **Purpose (casual voice):** "what happened on my machine on this
  day — what apps used the network, what was unusual about today,
  and a way to export the whole thing as a self-contained
  document I can archive or share."

---

## 2. UX intent

Reports is the **headline deliverable** of Phase 5 (PRD §11). It
turns the data ZenVizor has been quietly collecting into a daily
artifact the user can read, save, and refer back to. The screen
must serve two distinct intents in one composition: (a) **summary
reporting** — "what did my machine do yesterday" answered at a
glance via the day's totals, WAN/Local split, and ranked apps —
and (b) **discovery** — surfacing apps whose behavior is anomalous
*relative to their own pattern* so a stealthy low-volume process
isn't hidden by being out-ranked by bulk traffic. The ranking
surface and the discovery surface are parallel, not nested, and
the visual must telegraph the difference. The page also functions
as a launching pad: clicking an app row drills into the History
page pre-filtered to that app on that day. Export is one click
to a single Export menu that future-proofs for additional formats
beyond the launch CSV + HTML pair. The HTML export is itself a
designed surface — a self-contained document the user opens in a
browser, archives to disk, or pastes into incident docs.

---

## 3. Controls in scope

The brief carries the surface area; Claude Design composes the
page. This section opens with **what the page must communicate**
(outcomes the user must take away) and then lists the **controls
in scope** (by type and purpose). Composition — layout rows,
widths, spacing, card arrangement — is Claude Design's work and
is NOT prescribed here. Where an outcome has multiple reasonable
visual forms, the open question for the variant proposal lives
in §8.4.

### What the page must communicate

The Reports page must let a casual user, in a single visit,
understand:

1. **What date this report covers.** Always visible; trivially
   changeable without losing context. Default = yesterday (today's
   report is incomplete by definition — locked).
2. **The total volume of traffic for the date.** Up and Down bytes,
   readable at a glance from across the room.
3. **The WAN vs Local proportion of that traffic.** Conveyed
   visually, not just numerically.
4. **Which apps were responsible.** Per-row visible signals: app
   name, publisher, signature trust state, Up bytes, Down bytes.
5. **What was unusual.** Apps with anomalous patterns flagged with
   severity (`Info / Warning / Critical`) inherited from the Alerts
   entity vocabulary. A notable item that has a corresponding
   `Alert` raised must visually read as the same observation.
6. **A path to drill into a specific app's activity for the date.**
   Selecting an app row navigates to History pre-filtered to
   `(app, date)`. The drill destination is locked; the row-level
   affordance is open in §8.4 Q6.
7. **Export of the report in one action.** A single Export menu
   surfaces CSV and HTML choices.
8. **Discovery alongside ranking.** Two parallel surfaces:
   *Top Apps* (ranking, Top-N legitimate per §8.3 override) and
   *Uncommon Talkers* (discovery — apps whose behavior is anomalous
   relative to their own pattern, independent of byte-rank). The
   user must read them as different lenses, not "the same list but
   smaller." Discovery > ranking applies here per
   `project_discovery_principle.md`; ranking does not replace it.

Locked durable elements that any composition must honor:

- Page outer margin = `space.24`.
- All data-bearing cards use the canonical metal-card treatment
  (`metal.card` + `border.card` + `radius.card` + `shadow.card`).
- Date picker uses `Wpf.Ui.Controls.CalendarDatePicker` (the
  primitive). If §8.4 Q2 selects a range/comparison form, the
  picker becomes a `CalendarDatePicker` pair or a custom range
  control — flagged in §10.
- Notable items are rendered as items (cards / sections / list
  rows), not as rows of a `DataGrid` — heterogeneous payloads
  (severity, title, detail, entity reference, optional bytes/time)
  make `DataGrid` columns the wrong primitive.
- Export menu = single `ui:Button` with a `ui:MenuFlyout` carrying
  CSV and HTML items (H locked).
- The drill destination is History pre-filtered to `(app, date)`
  (F locked). History grows this filter capability in Phase 5
  alongside Reports — flagged as a cross-screen consequence in
  §16.

### Controls in scope (by type and purpose)

The page is a `ui:NavigationView`-hosted Page.

### Page chrome

- **Page identity.** `ui:TextBlock` carrying the page title
  `"Reports"`. `Style="text.subtitle"`. Subtitle line beneath
  (`text.caption`, `text.secondary`): a short orientation sentence
  Claude Design proposes (e.g. `"Daily overview of network activity
  on your machine."`). Composition of the chrome row is OPEN —
  the title, the date control, the export menu, and any banner all
  contend for the top of the page.

### Date control

- **Single-day primitive:** `Wpf.Ui.Controls.CalendarDatePicker`,
  bound to a `DateTime?` view-model property; refreshes the
  report on selection change (no polling, no "Refresh" button —
  the date IS the refresh trigger). Default = yesterday.
- **If §8.4 Q2 selects a range or comparison form**, this becomes
  a pair of pickers, a range control, or a window-shorthand picker
  (`1d / 7d / 30d / 90d`) consistent with the project's existing
  shorthand convention.

### Export menu

- Single `ui:Button` with `SymbolIcon ArrowExportLtr24` (or the
  Fluent-symbol equivalent Claude Design picks for "export") +
  text `"Export"` (or `"Export ▾"` with a visible chevron),
  opening a `ui:MenuFlyout`. Menu items: `CSV` and `HTML`. Each
  item opens `Microsoft.Win32.SaveFileDialog` pre-populated with
  the appropriate extension; the UI reads the report payload over
  IPC and formats client-side.

### Hero summary surface

A surface carrying the day's totals and WAN/Local proportion (§3-2,
§3-3). Form is OPEN — see §8.4 Q3. Whatever form Claude Design
proposes, the surface carries:

- Total Up bytes for the date.
- Total Down bytes for the date.
- WAN proportion + Local proportion of that traffic, visually
  conveyed.
- (If §8.4 Q2's comparison variant wins) delta indicators against
  the comparison anchor.

### Top Apps surface

The ranking surface (§3-4). Per-row visible signals: app name,
publisher, signature trust state, Up bytes, Down bytes. Form is
OPEN — see §8.4 Q4. DataGrid is one reasonable primitive; ranking
surfaces also invite bar charts with inline metadata, ranked
cards with sparklines, or treemaps.

Each Top Apps row must telegraph that selecting it drills into
History pre-filtered to `(app, date)` — affordance is OPEN, see
§8.4 Q6.

### Uncommon Talkers surface

The discovery surface (§3-8). The signal is "apps whose activity
today is unusual for them" — candidates:

- Apps that don't typically transmit, or transmit at much higher
  rates than their rolling baseline.
- Apps from user-writable paths (the MVP alert basis).
- First-seen publishers.
- Anomalous WAN ratio vs the app's own baseline.

The signal-set is provisional (§8.2); Claude Design's proposal
informs what the `GetDailyReport` contract carries. Form is OPEN —
see §8.4 Q5. Must visually distinguish from Top Apps so the user
reads them as different lenses.

### Notable today surface

Surfaces alert-eligible observations for the day with severity
grouping or severity-tagged cards. Form is OPEN — see §8.4 Q7.
Severity vocabulary inherits the Alerts entity (`Info / Warning /
Critical`). A notable item that has a corresponding `Alert`
raised must read as the same observation.

### Status banner

A `Border` painted either disconnected (pipe down) or error (any
other query failure). See §4 state coverage below. Default
`Visibility=Collapsed`.

---

## 4. State coverage

States to render. Every state below MUST appear in the mockup.

### `state: default` (steady-state, connected, data flowing)

- Date control shows yesterday's date (or the equivalent in the
  selected temporal model per §8.4 Q2).
- Status banner collapsed.
- Hero summary populated with realistic totals (e.g. Up: 1.2 GB,
  Down: 8.7 GB) and a non-trivial WAN/Local split (say 73% / 27%).
- Top Apps surface populated with realistic ranked rows. At least
  one **unsigned** row, at least one row with `Publisher = "(unknown)"`,
  and at least one row that ALSO appears in Uncommon Talkers — so
  the cross-surface visual distinction is auditable.
- Uncommon Talkers surface populated with realistic rows
  representing different anomaly categories (e.g. one
  "new publisher today," one "spike vs 7-day median," one
  "user-writable path"). The surface must NOT be byte-ranked.
- Notable today surface populated with at least one Critical, one
  Warning, and one Info item so all three severity levels render.
  At least one of the Notable items overlaps with an Uncommon
  Talkers row so the user reads the relationship.
- Export menu visible; the mock shows ONE state with the flyout
  open so the CSV + HTML items render.

### `state: empty — zero traffic` (no data for date)

- Date control populated; status banner collapsed.
- Hero summary, Top Apps, Uncommon Talkers, Notable today: ALL
  empty.
- A page-level empty state communicates "No traffic recorded on
  {date}." Copy is `text.body`, Foreground `text.secondary`,
  centered in the content area. The date control stays visible
  above; the Export menu disables (no data to export).

### `state: quiet day` (sparse but non-zero — see §8.4 Q8)

A separate steady-state variant for the low-activity day.
Distinct from `empty — zero traffic`:

- Hero summary populated with small but non-zero totals (e.g. Up:
  40 MB, Down: 180 MB).
- Top Apps populated with a handful of rows (e.g. 3–5), each at
  small byte volumes.
- Uncommon Talkers surface treatment per §8.4 Q8's proposal: the
  page must read as "deliberately quiet today" rather than
  "broken" or "empty."
- Notable today: empty (with whatever copy §8.4 Q8 lands on, or
  the default "Nothing notable today.").

### `state: loading`

- Status banner collapsed.
- Date control operative.
- Hero summary card body: centered `ui:ProgressRing
  IsIndeterminate="True"`. Caption `"Generating report…"`
  (`text.caption`, `text.secondary`) below the ring after ~1 s
  (server-side aggregation can take a moment for historical dates
  with high volume).
- Top Apps, Uncommon Talkers, Notable today: each surface body
  shows a `ProgressRing` OR a placeholder pattern per Claude
  Design's choice (em-dash rows are a Dashboard pattern; a single
  page-level ring is a History pattern).
- Export menu disabled until data arrives.
- No skeleton-shimmer anywhere (§8.1 global lock).

### `state: disconnected` (named-pipe down)

- Status banner visible:
  - Background `status.critical.background`, Foreground
    `status.critical`, `radius.control`, padding ~`space.8`.
  - Copy: `"Service disconnected — last refresh stale."`
- Last-known report values retained at `Opacity=0.6` (history-class
  surface; preserve last-known data per the project's
  history-class pattern). Export menu disabled (no fresh data).
- Date control stays operative for the user's interaction; new
  date selection produces an error banner instead of a successful
  refresh.

### `state: error` (any other query failure — NOT pipe-down)

- Status banner visible:
  - Background `status.caution.background`, Foreground
    `status.caution.text`, `radius.control`, padding ~`space.8`.
  - Copy: `"Report failed: {ExceptionMessage}"`. Example:
    `"Report failed: database is locked"`.
- Last-known report values retained at `Opacity=0.6`. Date control
  stays operative for retry. Export menu disabled.

> **No `warming` state.** Reports is a history-class surface — it
> queries SQLite via `GetDailyReport`, not the in-memory live
> aggregate. There is no fill window to surface.

### Dark theme

Per the template, ONE steady-state layout renders additionally in
**dark** theme so theme-swap behavior is auditable. The
`state: default` is the canonical pick.

---

## 5. Tokens in scope

The constraints. Specific token assignments are Claude Design's
during composition.

### `surface.*`

- Page root: `surface.background` (Mica shows through).
- Every data-bearing card (hero summary, Top Apps, Uncommon
  Talkers, Notable today): canonical metal-card treatment
  (`metal.card` + `edge.light` + `border.card` + `shadow.card` +
  `radius.card`) per the project-wide standing decision
  (`docs/design-system.md` §9 "Card surface — canonical
  treatment"). Flat `surface.card` is NOT the default; the metal
  recipe is.
- Status banner: per-state — see `status.*` below.
- Export menu flyout (Wpf.Ui `MenuFlyout`): inherits the Fluent
  popup treatment (`surface.layer` opaque).

> **Precondition check.** The `metal.card` / `edge.light` /
> `shadow.card` token set is reconciled on the brand-dict side and
> ships in `DesignTokens.xaml` today. No new card-surface migration
> blocks Reports' implementation.

### `text.*`

Full type ramp available (`text.title` / `text.subtitle` /
`text.body` / `text.body.strong` / `text.caption` / `text.eyebrow` /
`text.mono`).

Per-screen constraints:

- **Numeric / digit-aligned values** (Up bytes, Down bytes,
  proportions, baseline-relative deltas, AppId-like identifiers if
  rendered) use `text.mono` for column alignment.
- **No em-dash glyph in user-facing prose copy** per
  `feedback_no_emdash_in_ui_copy.md`. Em-dash as a "no data
  placeholder" in a value slot is fine (loading state); em-dash as
  punctuation in sentence copy is not — use period / colon /
  semicolon.
- Page title is `text.subtitle`. Card titles inside surfaces (when
  surfaces carry titles) are `text.body.strong`. Section eyebrows
  (when used to label hero summary values) are `text.eyebrow`.

### `accent.*`

- **No filled accent surfaces in this round** by default. Claude
  Design may propose an accent fill on the Export button if it
  reads as the page's primary action — but `accent.default` is
  NEVER a filled background; only `accent.fill` carries text on an
  accent surface. State the choice in the mock.

### `status.*`

Paired bg/foreground per banner AND per Notable today severity tag.

| Use | Background | Foreground |
|---|---|---|
| Disconnected banner | `status.critical.background` | `status.critical` |
| Error banner | `status.caution.background` | `status.caution.text` |
| Notable Critical | `status.critical.background` | `status.critical` |
| Notable Warning | `status.caution.background` | `status.caution.text` |
| Notable Info | `status.neutral.background` | `status.neutral` |

Severity vocabulary on Notable items inherits the Alerts entity
scheme (§8.2 lock). The specific token assignment within a Notable
item (banner-style, pill, severity bar, icon foreground, card
background, etc.) is Claude Design's per §8.4 Q7.

### `border.*`

- All data-bearing cards: `border.card`, 1px (paired with
  `metal.card` and `shadow.card` per the canonical treatment).
- Status banner: no border (background alone carries the signal).
- Any divider inside a card: `border.subtle`, 1px.

### `space.*`

- Page outer margin: `space.24` (mandatory).
- 4-based scale for all inter-element / inter-card / padding
  choices. Per `project_wpf_spacing_token_thickness.md`, write
  Margin/Padding as literals (`Margin="24"`), not as
  `{StaticResource space.24}` — the tokens are `sys:Double` and
  `Margin` expects `Thickness`.

### `radius.*` (role tokens only)

- All data-bearing cards: `radius.card`.
- Status banner: `radius.control`.
- Date picker / Export button (Wpf.Ui controls): `radius.control`
  (Wpf.Ui defaults).
- Export menu flyout popup: `radius.overlay` (Wpf.Ui default for
  popups).
- Any pills / chips inside cards (e.g. severity tags on Notable
  items): `radius.control`.

### Material / effect

- All data-bearing cards adopt the canonical metal recipe
  (`metal.card` background + `edge.light` baked-in catch-light +
  `border.card` 1px stroke + `shadow.card` elevation +
  `radius.card` corner).
- **No live blur, no continuous animation, no animated sheen.**
  Static gradients + single `DropShadowEffect` only.
- **No animated fills** on the hero summary's WAN/Local viz or any
  sparklines (light-and-fast). Static paints only.

### Chart tokens

Listed under §6.

### New tokens required by this brief

**None.** Every token referenced exists in `DesignTokens.xaml`
and `colors_and_type.css` today. The Uncommon Talkers signal data
shape may grow new `GetDailyReport` payload fields (provisional,
§8.2) but those are contract-side, not visual-token-side.

---

## 6. Chart-chrome — tokens AND behavior spec

Reports does NOT have a primary `lvc:CartesianChart` like Dashboard
/ App Detail / History. But several §8.4 variants introduce
chart-like primitives:

- **WAN/Local stacked bar** in the hero summary (every §8.4 Q3
  variant uses it).
- **Sparkline of day's traffic shape** (Q3 variant b, possible
  per-row in Q4 variants).
- **Hourly heatmap strip** (Q3 variant c).
- **Per-row sparklines on Top Apps rows** (possible Q4 variant).

Whichever chart-like primitives Claude Design proposes, this
section governs their paint.

### 6.1 Chart-chrome paint tokens

| Token | Use |
|---|---|
| `chart.wan` | WAN segment of WAN/Local stacked bar; WAN markers anywhere on this page |
| `chart.local` | Local segment of WAN/Local stacked bar; Local markers |
| `chart.upSeries` | Sparkline stroke (Up) on hero or per-row; stacked-column Up fill if used |
| `chart.downSeries` | Sparkline stroke (Down) on hero or per-row; stacked-column Down fill if used |
| `chart.axis` | Sparkline baseline if drawn |
| `chart.axis.label` | Any axis tick labels on sparkline / heatmap (= `text.tertiary`) |
| `chart.gridline` | Heatmap grid stroke if used (apply low alpha in code) |

A WAN/Local stacked bar is NOT a `lvc:CartesianChart` — render as
two `<Border>`s filled `chart.wan` / `chart.local` with widths
bound to proportions (see §10). The tokens are listed here for
consistency; the consumer is a primitive.

Sparklines and heatmaps, if used, ARE `lvc:CartesianChart`
instances. Chart paints don't inherit theme — they're re-applied
in code on `ApplicationThemeManager.Changed` via
`Services/ChartTheming.cs`. The brief annotates tokens; wiring is
implementation.

### 6.2 Chart behavior spec

If sparklines or heatmaps are proposed:

- **Static paints only.** No animated transitions on series.
- **No legend, no axis labels** on per-row sparklines (they are
  trend-cues, not first-class charts). Hero sparkline may carry a
  minimal axis (e.g. start/end time labels) if Claude Design
  judges it readable; default to chart-less.
- **No tooltip on sparklines** in this round. Per-row sparklines
  are decorative trend-cues; if the user needs to inspect the
  shape, they drill into History (§3-6).
- **Hourly heatmap** (if Q3 variant c wins): static painted cells,
  no tooltip in this round, no hover-state animation. Color
  scale: a gradient between two existing tokens (e.g.
  `surface.subtle` → `chart.upSeries` for intensity); explicit
  per-cell colors annotated.
- **Forgiving-hover** on sparklines that DO carry tooltips: same
  `ChartBuilder` policy as Dashboard / App Detail / History
  (`GeometrySize = 20`, `GeometryFill = GeometryStroke = null`)
  — inherited from the shared service. The brief annotates the
  policy as inherited.
- **Theme-flip re-paint** applies to any chart on this page — the
  `ChartTheming.Apply` + `ChartTheming.Changed` wiring from the
  other history-class pages is the same path.

---

## 7. Density assignment

Reports' surfaces have heterogeneous density requirements:

- **Top Apps surface** (if rendered as a DataGrid): density is
  OPEN. Compact (`style.datagrid.compact`, row 22) is appropriate
  if Top Apps is the dominant ranked-rows surface; default
  (row ~28) is appropriate if Top Apps shares vertical space with
  Uncommon Talkers and Notable today and the page emphasizes
  scannability over density. Claude Design picks per Q4's chosen
  variant and the broader composition direction.
- **Uncommon Talkers surface** (if rendered as a list/grid):
  same — OPEN per Q5. Likely default density given the discovery-
  surface framing (less data, more weight per row).
- **Notable today surface** (item-based, NOT DataGrid): density
  driven by the severity-grouped or feed composition Claude Design
  proposes per Q7. ItemContainer padding ~`space.12,space.8`,
  inter-item margin ~`space.4` is a reasonable starting point;
  Claude Design tunes per variant.

No fixed density assignment — the brief states constraints, the
mock states the chosen density per surface.

---

## 8. Locks and open questions for this screen

### 8.1 Global locks

- **Loading = default Fluent `ProgressRing`**, not skeleton-shimmer
  (light-and-fast principle, design-system §2).
- **High Contrast is handled by `HighContrast.xaml`** merged on HC
  activation. The mock does not draw HC variants; implementer
  verifies HC during the per-page verification gate.
- **Em-dash NOT in user-facing prose copy** per
  `feedback_no_emdash_in_ui_copy.md`. Use period / colon /
  semicolon. Em-dash as a "no data" placeholder glyph in a value
  slot is fine.

### 8.2 Screen-specific outcome locks

What the findings settled at the *outcome* level. Composition is
Claude Design's; these outcomes are not re-litigable in the mock.

- **Default date = yesterday.** Today's report is incomplete by
  definition.
- **Export = single Export menu surfacing CSV + HTML.** No literal
  "Export CSV" + "Export HTML" buttons side-by-side; the menu
  pattern future-proofs for additional formats without re-laying
  out the actions row. PDF and email remain invariant-blocked
  (§15).
- **Severity vocabulary on Notable items inherits Alerts entity:**
  `Info / Warning / Critical`. A Notable item that has a
  corresponding `Alert` raised must read as the same observation
  on this page as it does on the Alerts page (see §16 for the
  cross-screen coherence requirement).
- **Drill destination from any app-row surface = History
  pre-filtered to `(app, date)`.** App Detail is NOT the
  destination in Phase 5 (a richer-fidelity App Detail
  specific-date mode is a post-MVP follow-up tracked in the
  sprint plan); History grows the `(app, date)` filter capability
  alongside Reports (F8 in `docs/design-briefs/findings/history.md`).
- **Notable today rendered as items, NOT DataGrid rows.**
  Heterogeneous payload (severity, title, detail, entity ref,
  optional bytes/time) — DataGrid columns are the wrong primitive.
- **Top Apps AND Uncommon Talkers are parallel surfaces**, not
  one section with a toggle, not nested. The user must read them
  as different lenses on the day's apps.
- **HTML export must be self-contained** — no external CDN refs,
  no embedded analytics, no remote fonts (honors zero-own-traffic
  invariant when opened in a browser).

### 8.3 Boundary-case overrides of hard rules

Reports overrides the `discovery > ranking` rule **on the Top Apps
surface only** — Top-N framing is legitimate there because
Reports is a *summary* surface and Top Apps is explicitly the
ranking lens. Dashboard's "Top 10 talkers" is the precedent.

**The override is partitioned, not blanket.** Uncommon Talkers IS
the discovery surface on this page that satisfies the principle —
it must NOT be capped by byte-rank, because the entire point is
to surface low-byte-but-anomalous apps that ranking misses. A
"stealthy malicious process won't rank highly by bytes" is the
exact failure mode the discovery surface exists to catch.

No other overrides. §13 (passive-only) is non-overridable.

### 8.4 Open design questions for Claude Design

The findings deliberately left these open. Each invites Claude
Design to propose variants the user picks from during iteration.
**This is the section that makes the design round produce value
over the brief alone.**

#### Q1. Page composition

**Open:** how should the Reports page compose? §3's outcomes tell
you what must appear; the arrangement is open. The composition
must accommodate all the other open items below — hero summary
form (Q3), Top Apps treatment (Q4), Uncommon Talkers treatment
(Q5), drill affordance (Q6), Notable items presentation (Q7),
quiet-day treatment (Q8), and the Export menu placement.

**Constraints:**
- Outer `space.24` margin.
- Cards on the canonical metal-card treatment (§5).
- Page may scroll OR may fit without scroll — Claude Design picks.
- The nav rail and any MainWindow chrome are fixed outside the
  page surface.

**Variants:** 2–3. Suggested starting points (Claude Design may
propose others):

- **(a) Single scrollable column:** summary → ranking → discovery
  → notable items, top-down.
- **(b) Two-column dashboard:** summary spans the top; ranking
  and discovery sit side-by-side beneath; notable items band
  across the bottom.
- **(c) Narrative "today's story":** notable items + hero summary
  lead as the headline; ranking and discovery sit below as
  supporting evidence.

#### Q2. Temporal model

**Open:** is "one date, one report" the right temporal frame, or
should the page natively support comparison (yesterday vs today,
week-over-week) and/or a rolling N-day digest?

**Constraints:**
- Variants **may assume IPC contract latitude** — the
  `GetDailyReport(date)` contract is not locked yet. Phase 5
  implements the chosen shape (could grow to `GetReport(range)`
  or `GetReport(date, comparison)`).
- The date control primitive becomes a range or comparison
  control if a multi-date variant wins (propagates to §10's
  date-picker note).
- Hero summary form (Q3) must accommodate the chosen temporal
  variant — e.g. comparison form needs room for delta indicators;
  digest form must read at the chosen N-day scope.

**Variants:** 2–3.

- **(a) Single-day pure.** What §3's outcomes describe today.
  Simplest; aligns with PRD §11's "daily overview" framing.
- **(b) Single-day with comparison.** One primary date plus a
  comparison anchor (yesterday vs today, OR vs same day last
  week). Hero summary shows deltas; Notable items can flag
  "more than yesterday."
- **(c) Rolling N-day digest.** Date control becomes a window
  picker (`1d / 7d / 30d / 90d` shorthand matching the project's
  convention). Pure daily becomes a degenerate case (1d window).

#### Q3. Hero summary visualization

**Open:** how should "the day at a glance" read in one or two
seconds? The numerics (Up bytes, Down bytes, WAN/Local
proportion) must appear (§3-2, §3-3); the *form* is open.

**Constraints:**
- Readable at a glance from across the room.
- WAN/Local proportion conveyed visually, not just numerically
  (§3-3). Uses `chart.wan` / `chart.local`.
- Static viz only (no animated fills, no continuous animation —
  light-and-fast).
- Numeric values use `text.mono` for column alignment.
- Must accommodate whichever §8.4 Q2 variant wins.

**Variants:** 2–3. Examples Claude Design may propose:

- **(a) Eyebrow + large mono Up/Down values + horizontal stacked
  WAN/Local bar.** Simplest, closest to the project's existing
  value-card vocabulary.
- **(b) Headline total-bytes number + supporting Up/Down split +
  sparkline of the day's traffic shape.** More narrative; the
  sparkline gives a sense of "when did the day's traffic happen."
- **(c) Hourly heatmap strip across the top conveying "when did
  the day's traffic happen" alongside the totals.** Richest;
  worth weighing against page composition (Q1).

#### Q4. Top Apps surface

**Open:** how should the ranking surface render? Top-N framing is
legitimate per §8.3 override. DataGrid is one reasonable
primitive — `style.datagrid.compact` Per-App is the closest
neighbor in the project's vocabulary — but ranking surfaces also
invite alternative visualizations.

**Constraints:**
- Per-row visible signals: app name, publisher, signature trust
  state, Up bytes, Down bytes.
- Signature trust state (Unsigned, Invalid, Unchecked) must be
  distinguishable at a glance — same constraint as Per-App and
  App Detail's grids.
- Numeric values use `text.mono`.
- Top-N cap is legitimate (e.g. top 10 by daily bytes); show
  more / pagination is OPEN.
- Each row must telegraph that selecting it drills into History
  pre-filtered to `(app, date)` — affordance is open in Q6, but
  the row primitive must be selectable.
- Discovery does NOT live here; Uncommon Talkers (Q5) is its own
  surface. Do not try to merge.

**Variants:** 2.

- **(a) DataGrid in the Per-App vocabulary** — compact rows,
  columnar app / publisher / signature / up / down. Familiar.
- **(b) Alternative ranked visualization** — Claude Design's
  pick: ranked cards with sparklines, horizontal bar chart with
  inline metadata, treemap, or another form. Argue against the
  DataGrid default.

#### Q5. Uncommon Talkers surface

**Open:** how should the discovery surface render? The signal
itself is provisional (§8.2 flags the data shape as locking at
Phase 5); Claude Design's proposal informs what the contract
carries.

**Constraints:**
- Per-row: app name, publisher, signature trust state, AND a
  one-line explanation of *why this row is on the uncommon list*
  (e.g. `"first time seen today"`, `"3× the 7-day average"`,
  `"unsigned · from %LOCALAPPDATA%"`). The "why" is the surface's
  whole job — without it, the user can't act on the signal.
- NOT byte-rank capped. Discovery > ranking applies (§8.3).
- Must visually distinguish from Top Apps (Q4). If both surfaces
  use rows / cards, the distinction is in title, framing copy,
  and column / field set — NOT just position on the page.
- Same drill destination as Top Apps: History pre-filtered to
  `(app, date)`. Affordance per Q6.
- Severity is implied here but NOT the same as Alerts severity
  — Uncommon Talkers is "behaviorally anomalous," which is
  distinct from "alert-raised." A row may also appear in Notable
  today (Q7) if its anomaly crossed an alert rule; the visual
  should allow the user to read the relationship.

**Variants:** 2.

- **(a) Compact list of rows with anomaly-reason captions** —
  each row reads "app · publisher · signature · *reason it's
  here*." Scannable; closest to Top Apps but visually weighted
  toward the "reason" column.
- **(b) Category-grouped cards** — anomalies grouped by category
  (e.g. "New today," "Unusual volume," "Risky paths") with apps
  listed under each. Tells the user about the *kinds* of
  anomalies in one glance; takes more vertical space.

#### Q6. Drill affordance on app rows (Top Apps + Uncommon Talkers)

**Open:** how should it become obvious to the user that selecting
an app row drills into History pre-filtered to `(app, date)`?

**Constraints:**
- Drill destination is locked (History pre-filtered to
  `(app, date)`).
- Affordance is passive (presentation-only, no action affordance
  per §13).
- Single-click drill per `feedback_drill_grid_pattern.md` (hover
  chevron + hand cursor + single click). The pattern is
  established for DataGrid-like surfaces; if Q4 or Q5 propose
  non-DataGrid surfaces (bar chart, treemap, category cards), the
  affordance must read correctly on whichever primitive the row
  IS.
- Both surfaces (Top Apps and Uncommon Talkers) use the same
  drill affordance — the user should not learn two patterns.

**Variants:** 1–2.

#### Q7. Notable today presentation

**Open:** how should "Notable today" render? Severity grouping
and incident-card framings are the most promising directions —
not a generic list with category icon + caption.

**Constraints:**
- Severity vocabulary inherits Alerts entity (§8.2):
  `Info / Warning / Critical`.
- A Notable item that has a corresponding `Alert` raised must
  visually read as the same observation across both pages
  (cross-screen coherence — see §16).
- Notable items are heterogeneous payloads (severity, title,
  detail, entity reference, optional bytes/time). DataGrid
  columns are the wrong primitive (§8.2 lock).
- Distinct from Uncommon Talkers (Q5): Notable items are
  *raised observations* (alert-eligible events); Uncommon
  Talkers is a *baseline-relative signal* (no alert needed to
  appear). Visual must read them as different surfaces.
- MVP knows ONE notable item type today
  (`UnsignedFromUserPath`). The proposal must scale gracefully
  to additional types as future `IMonitor`s land (PRD §10) —
  severity tag + title + detail is the abstraction.

**Variants:** 2.

- **(a) Severity-grouped sections** — Critical / Warning / Info
  in that order; each section contains incident cards. Clear
  priority ordering; section headers double as legend.
- **(b) Mixed-severity feed of incident cards** in
  reverse-chronological order, each card severity-tagged. Reads
  as a timeline of today's notable events.

#### Q8. Quiet-day / sparse-day treatment

**Open:** how should a low-activity day feel? Distinct from
`empty — zero traffic` (which is the genuinely-zero case). A
page that frequently has little to report shouldn't read as
"broken" or "empty" — it should read as "deliberately quiet."

**Constraints:**
- Low effort — one variant proposal with rationale.
- Reuses existing tokens; no new tokens.
- The hero summary still shows the (small) totals; the open
  question is how Top Apps, Uncommon Talkers, and Notable today
  feel when they have few-to-no rows.

**Variants:** 1.

#### Q9. HTML export composition

**Open:** how should the exported HTML report compose? It is a
standalone document — no nav rail, no app-specific chrome,
viewed in a browser, archived to disk, occasionally pasted into
incident docs.

**Constraints:**
- Resembles the in-app composition in hierarchy (summary, top
  apps, uncommon talkers, notable items) so the user reads them
  as the same artifact — not a divergent document.
- No nav rail or app-specific surrounding chrome.
- Self-contained — no external CDN refs, no embedded analytics,
  no remote fonts (zero-own-traffic invariant). Inline all
  styles; inline the font fallback stack.
- Print-friendly — the user may save-as-PDF via the browser
  (this is NOT first-party PDF export; it's the user's path).
- Uses the same design tokens as the in-app surfaces, projected
  to CSS variables per `docs/design/colors_and_type.css`.

**Variants:** 1 mock of the HTML composition; annotate which
tokens carry over to which CSS variables.

---

## 9. Annotation work specific to this screen

- **New tokens this brief introduces.** **None.** Every token
  referenced is in the primer's token table and resolves
  correctly in `DesignTokens.xaml` and `colors_and_type.css`
  today.
- **Per-screen renames / repointings.** **None.** Reports is a
  new screen; there is no legacy XAML to migrate.
- **Provisional data fields (NOT visual tokens).** The Uncommon
  Talkers signal definition (§8.4 Q5) may require new
  `GetDailyReport` payload fields (e.g. per-app rolling median,
  publisher-first-seen flag, WAN-ratio baseline). These are
  contract-side, not visual-token-side, and lock at Phase 5
  implementation alongside the brief's chosen Q5 variant.

---

## 10. Per-screen WPF translation gotchas

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages have infinite vertical extent. If Top Apps,
  Uncommon Talkers, or Notable today are rendered as a
  `DataGrid` or `ListView`, set `MaxHeight` programmatically on
  `Loaded` + `SizeChanged` so the inner `VirtualizingStackPanel`
  virtualizes. Pattern: `EnforceDataGridBounds` in App Detail,
  `EnforceAppsGridBound` in Per-App.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled`** on Reports' nav-rail entry —
  the page instance survives nav away/back, so `Loaded` does
  NOT refire on return. Anything that must re-measure on revisit
  hangs off `SizeChanged`, not `Loaded`.
- **Date picker primitive:** `Wpf.Ui.Controls.CalendarDatePicker`,
  bound to a `DateTime?` view-model property; refresh on
  `SelectedDateChanged`. If §8.4 Q2 selects a range or comparison
  form, the primitive becomes a pair, a custom range control, or
  a window-shorthand `ComboBox` consistent with Per-App / History
  picker shorthand. Flag the picker choice in the mock so the
  implementer wires the right primitive.
- **WAN/Local stacked bar (hero summary):** render as two
  `<Border>`s filled `chart.wan` / `chart.local` with widths
  bound to proportions — NOT a `lvc:CartesianChart`. LiveCharts2
  is overkill for one bar of two segments and pays measurable
  per-frame cost (light-and-fast).
- **Sparklines (if Q3 or Q4 propose them):** `lvc:CartesianChart`
  is the appropriate primitive. Set `Background="Transparent"`,
  hide axes / legend / tooltip (LiveCharts2 v2 exposes
  per-component visibility), and inherit chart paints from
  `ChartTheming.Apply` per the other history-class pages. NOT a
  custom `Path` geometry — going through `ChartBuilder` /
  `ChartTheming` keeps theme-flip behavior consistent.
- **Hourly heatmap (if Q3 variant c wins):** a `Grid` of 24
  `<Border>`s (one per hour) painted with intensity-mapped
  colors is the lightweight implementation. NOT a custom
  `Image` rasterizer; NOT a chart. Inline tooltips per cell
  are out of scope this round (Q3 constraint).
- **Export menu:** single `ui:Button` opening a `ui:MenuFlyout`
  carrying `CSV` and `HTML` `MenuFlyoutItem`s. Each item:
  `Click` → `Microsoft.Win32.SaveFileDialog` pre-populated with
  the appropriate `DefaultExt` and `Filter`. The UI reads the
  payload over IPC (`GetDailyReport`) and formats client-side
  (CSV / HTML serializers live in the UI project). **The
  service stays out of the user's filesystem.**
- **"Open in browser"** (when surfaced as a follow-up affordance
  after HTML export): `Process.Start(new
  ProcessStartInfo(htmlPath) { UseShellExecute = true })`.
  Standard Windows shell — not a network call.
- **Drill from an app row → History:** navigation passes
  `(app, date)` filter params to `HistoryPage`. History does NOT
  currently accept these — capability tracked in
  `docs/design-briefs/findings/history.md` F8 and lands with
  Phase 5. Until History grows the filter, the drill affordance
  is a Phase 5 implementation hook, not a polish-interlude
  wire-up. **Wire the affordance in Reports' implementation;
  resolve the destination plumbing when History's Phase 5 work
  lands** (same sprint).
- **Notable items inline expansion (if Q7 lands on cards with
  expandable detail):** if cards grow vertically in a `ListView`,
  `VirtualizationMode=Recycling` does not play well with
  variable row heights — switch to `VirtualizationMode=Standard`
  or set `VirtualizingPanel.ScrollUnit=Pixel`. Document the
  chosen mode in the mock.

---

## 11. Discovery > ranking — per-screen application

Reports carries **two** ranking-relevant surfaces with explicitly
distinct relationships to the rule:

- **Top Apps surface** — `discovery > ranking` is **overridden**
  here (§8.3). Top-N framing is legitimate because this is a
  *summary* surface explicitly framed "top apps for the day."
  Per-App remains the canonical uncapped drill complement when
  the user wants the unranked all-apps view; History (post-Phase
  5) is the canonical drill destination from a Top Apps row.
- **Uncommon Talkers surface** — `discovery > ranking` is
  **applied** here. NOT byte-rank capped. The entire surface
  exists to satisfy the principle on this page: a stealthy
  low-volume process is exactly what Top Apps would miss, and
  Uncommon Talkers is what catches it. Server-side: every app
  whose `(date)` behavior crosses any uncommon-talker rule
  appears, regardless of byte volume.

This partition (override on the ranking surface, apply on the
discovery surface) is the page's solution to "ranking is useful
for summary AND the principle still matters for safety."

*Memory: `project_discovery_principle.md`.*

---

## 12. Honest attribution — per-screen application

- **Hero summary totals** attribute to the date's traffic across
  every PID-set observed. No per-app split implied at the hero
  level.
- **Top Apps rows** attribute bytes to each app's PID-set in the
  window. For an `svchost.exe` row, bytes attribute to the host
  PID, NOT split across the co-hosted services that PID
  ran during the day. If `GetDailyReport` carries the
  `HostedServices` field (provisional, §9), the row may
  render `svchost.exe [Schedule, Themes]` in the bracketed-services
  convention used by Dashboard's talkers — but the byte total is
  still the PID's total, not per-service.
- **Uncommon Talkers rows** carry the same honest-attribution
  boundary. The "anomaly reason" copy may name a specific
  behavior (e.g. `"unsigned · from %LOCALAPPDATA%"`) but the
  bytes (if shown) are the PID's, not per-service.
- **Notable today items** name the entity they observed (e.g.
  `entity_kind=App, entity_ref=<AppId>`) per the Alerts entity
  scheme. The Alert pipeline owns the attribution boundary;
  Reports inherits it.
- **Host-process attribution for injected code / LOLBins.**
  Reports does NOT visualize this as a separate category. The
  documented boundary (host PID owns the bytes) is preserved
  without UI hints, asterisks, or caveat icons. If the user
  wants the host-process picture, they drill into History.

Reports does NOT override the rule.

---

## 13. Passive-only — per-screen application

**NO** "Block this app" / "Kill this process" / "Quarantine" /
"Stop this app" / right-click action menu / hover-revealed action
buttons anywhere on Reports. The Notable today surface
specifically must NOT carry "Acknowledge" or "Resolve" controls —
those belong on the Alerts page (where the alert was raised).
Reports' Notable items are a READ-ONLY echo of alert-eligible
observations for the day.

Drill / navigation affordances ARE allowed and ARE passive:

- App row → History (`(app, date)` pre-filter). Allowed.
- Notable item → Alerts page filtered to the matching alert (if
  one exists). Allowed (cross-page navigation is passive).
  Optional in Phase 5; design hook documented for future.
- Export menu → file write to user-chosen location. Allowed
  (local I/O, not network).

**Passive-only is non-overridable.** No Claude Design variant may
add an action affordance to Reports regardless of how natural the
gesture seems on a screen showing anomalies.

---

## 14. Performance budget — per-screen application

- **No shimmer / live blur / continuous animation** on any
  surface this round. Loading uses static `ProgressRing` (§8.1).
  WAN/Local viz, sparklines, heatmaps are all static paints.
- **No new poll cadence.** Reports refreshes on `Loaded` and on
  date control `SelectionChanged`. NO polling. The date IS the
  refresh trigger.
- **Report payload is queried once per date selection.**
  `GetDailyReport(date)` is a SQL aggregation; the UI caches the
  result per date until the user selects a new date. Avoid
  re-querying for incidental UI events (hover, focus, etc.).
- **Chart-paint batching** — any chart-like primitive on this
  page re-applies paints on theme flip via a single
  `ChartTheming.Apply` call per chart, not per-series.
- **Export I/O is on the UI thread but offloaded to
  `Task.Run`** if the payload is large enough that synchronous
  serialization could block the dispatcher for >100 ms.
  Implementer detail; the brief flags it.
- **HTML export size budget — implementation note:** target
  <500 KB for a typical day's self-contained HTML. Inline CSS,
  inline minimal SVG icons where used, no embedded images
  beyond what the report visualizes. Larger payloads (e.g.
  digest variants per Q2) target <2 MB.

---

## 15. Out-of-scope — invariant-blocked only

Items listed here are invariant-blocked (zero-traffic,
passive-only), not "feature, for later." The findings doc moved
several previously-deferred items into §8.4 open questions; what
remains here is the genuinely-off-the-table set.

- **PDF export.** PRD §3.2 / §11.4 — explicitly deferred
  indefinitely. The mock must NOT propose a `PDF` menu item.
- **Email the report.** Would emit network traffic. Hard NO per
  zero-own-traffic invariant. The mock must NOT propose an
  "Email" button or share affordance.
- **Schedule daily report delivery.** Implies email or file-drop
  side-effect; email is invariant-blocked; scheduled delivery
  conflicts with "passive, user-initiated reporting." The mock
  must NOT propose a schedule toggle.
- **Active-action affordances on app rows or Notable items.** No
  "block this app," no "kill this process," no
  "acknowledge from Reports." Passive-only invariant — HARD NO.
  Acknowledge belongs on Alerts.
- **In-app "yesterday's report is ready" surfacing on Reports
  itself.** If we ever notify the user that yesterday's report is
  ready, it lands on the Alerts page, not as decoration on
  Reports. Reports is the destination, not the announcement.
  The mock must NOT design a "report ready" callout on Reports.

---

## 16. Chrome / cross-screen consequences

Reports' Phase 5 implementation creates several cross-screen
consequences. Flagged so the other briefs know what NOT to
redesign — and what TO design.

### 16.1 History grows `(app, date)` filter capability

- **What changed.** History accepts navigation params for a
  single app + a specific calendar day, displays a single app's
  slice of History scoped to that day, AND offers a clear-filter
  affordance back to the unfiltered aggregate view.
- **Where it lives.** `src/ZenVizor.Ui/Views/HistoryPage.xaml`
  and `HistoryPage.xaml.cs`. Tracked in
  `docs/design-briefs/findings/history.md` F8.
- **Propagates to.** History's design — the picker grows a
  specific-date mode alongside trailing-window presets; a filter
  chip / strip near the picker indicates the active app filter
  when one is set; an X / clear control on the chip restores the
  aggregate view.
- **Per-screen brief work needed elsewhere.** History's next
  brief (whenever it lands — likely Phase 5 alongside Reports)
  must carry the `(app, date)` filter surface as a first-class
  design question, not a footnote. The Reports brief depends on
  History's implementation landing in the same Phase 5 sprint —
  flag this dependency in the implementation kickoff.

### 16.2 Alerts severity vocabulary inheritance

- **What changed.** Notable today on Reports inherits the
  `Info / Warning / Critical` severity vocabulary from the
  Alerts entity (PRD §7.6) and the visual treatment must be
  coherent enough across both pages that a Notable item and its
  corresponding Alert read as the same observation.
- **Where it lives.** Notable today rendering (this brief) and
  Alerts feed rendering
  (`docs/design-briefs/findings/alerts.md` §3 alert-card
  template). The two surfaces will be designed in separate
  Claude Design sessions, but the severity-to-token mapping
  MUST be shared.
- **Propagates to.** Alerts brief — when it lands, the
  severity-to-status-token mapping it uses must match Reports'
  Notable items. Both pages use `status.critical` for Critical,
  `status.caution` for Warning, `status.neutral` for Info.
- **Per-screen brief work needed elsewhere.** Alerts brief
  acknowledges Reports' Notable today as a parallel surface
  consuming the same severity vocabulary; the Alert-card
  template and Notable-item template should be visually
  cohesive (not necessarily identical, but recognizable as the
  same observation).

### 16.3 Reports drill → App Detail specific-date mode (post-MVP follow-up)

- **What's deferred.** A richer-fidelity drill destination —
  App Detail with a specific-calendar-day window mode — is
  tracked in `docs/zenvizor-sprint-plan.md` as a post-MVP
  follow-up. Phase 5 routes the Reports drill to History
  instead; App Detail's specific-day mode is a future
  enhancement.
- **Per-screen brief work needed elsewhere.** None this round.
  Flagged so the App Detail brief (next polish round, whenever
  it lands) knows the specific-date mode is a documented
  follow-up, not a fresh design question.

### 16.4 No MainWindow chrome changes

Reports' Phase 5 implementation does not modify MainWindow's
title bar, bottom bar, or status bar. The bottom-bar rate
mirror (introduced by Dashboard's polish) is visible while
Reports is shown but is not Reports' concern. Draw it in the
Reports mock for visual completeness; flag it `chrome:
MainWindow, not ReportsPage`. (Bottom-bar values reflect live
state, which is unrelated to the historical date the user is
viewing on Reports — the visible disjunction is intentional and
should NOT prompt Claude Design to propose a synchronization
mechanism.)

---

## 17. Deliverables expected from Claude Design

The mockup hand-back MUST contain:

- Layouts for every state in §4 in the default (light) theme:
  `default`, `empty — zero traffic`, `quiet day`, `loading`,
  `disconnected`, `error`.
- ONE steady-state layout additionally rendered in **dark** theme
  (`state: default` recommended).
- The `default` state mock specifically must show:
  - Page title + subtitle line + date control + Export menu in
    Claude Design's proposed chrome arrangement.
  - Hero summary populated per Q3's chosen variant.
  - Top Apps surface populated per Q4's chosen variant. At least
    one Unsigned row, one `Publisher = "(unknown)"` row, and one
    row that ALSO appears in Uncommon Talkers so the
    cross-surface relationship is auditable.
  - Uncommon Talkers surface populated per Q5's chosen variant.
    At least one row per anomaly category proposed (e.g. "new
    today," "unusual volume," "risky path").
  - Notable today surface populated per Q7's chosen variant.
    At least one Critical, one Warning, one Info item. At least
    one Notable item overlaps with an Uncommon Talkers row.
  - Drill affordance per Q6 visible on one Top Apps row AND one
    Uncommon Talkers row (so the same pattern reads on both
    surface types).
  - Export menu OPEN on one mock so the `CSV` + `HTML` flyout
    items render.
- Both designed Phase-related deliverables required for Group B:
  - **(a) Interim placeholder treatment** wearing in the shipped
    polish-interlude app, per the findings doc §6(a). Centered
    icon (`DocumentText48`, `text.tertiary` foreground) +
    `text.title.large` "Daily reports" + `text.body.large`
    `text.secondary` "Coming in Phase 5 — date picker, top apps,
    WAN/local split, and CSV/HTML export." On `surface.background`
    (no card).
  - **(b) Eventual functional layout** per Q1 + Q2 + Q3 + Q4 +
    Q5 + Q6 + Q7 + Q8 chosen variants — the steady-state
    composition above.
- **HTML export mock** per Q9. One composition of the
  self-contained HTML report, annotated with which design tokens
  carry over to CSS variables (per
  `docs/design/colors_and_type.css`).
- Variant proposals for each open question in §8.4:
  - Q1 (page composition) — 2–3 variants.
  - Q2 (temporal model) — 2–3 variants.
  - Q3 (hero summary visualization) — 2–3 variants.
  - Q4 (Top Apps surface) — 2 variants.
  - Q5 (Uncommon Talkers surface) — 2 variants.
  - Q6 (drill affordance) — 1–2 variants.
  - Q7 (Notable today presentation) — 2 variants.
  - Q8 (quiet-day treatment) — 1 variant.
  - Q9 (HTML export composition) — 1 variant.
- Token annotations using canonical dotted names per §9. Every
  card, text run, banner, chart-like primitive, severity tag,
  and pill labeled with its tokens.
- Density tags on Top Apps and Uncommon Talkers surfaces
  reflecting the chosen direction per Q4 / Q5.
- Layout hints:
  - `MaxHeight` enforcement annotation on any DataGrid /
    ListView that needs to virtualize (per §10's
    `NavigationView`-wraps-page caveat).
  - `scroll: page` / `scroll: pane` annotations wherever they
    matter — particularly whether the page scrolls as a whole
    OR each card scrolls independently.
  - Outer page `Margin: 24` (annotation as `space.24` token).
- Chart-chrome token names AND chart behavior spec called out
  per §6 wherever a chart-like primitive appears.
- Cross-screen reconciliation:
  - Bottom-bar rate mirror drawn (MainWindow chrome) so the
    screen reads in context; flagged as MainWindow-owned (§16.4).
  - One Notable today item drawn with its Alerts-page
    counterpart referenced (cross-reference annotation, not a
    second mock) so the cross-page coherence (§16.2) is
    auditable.
- Hand-off notes confirming:
  - No new tokens introduced (§9).
  - No pointer renames (Reports is a new page).
  - Provisional `GetDailyReport` payload fields the chosen Q5
    variant assumes (e.g. per-app rolling median,
    publisher-first-seen flag, WAN-ratio baseline) — listed so
    Phase 5 contract work picks them up.
  - History `(app, date)` filter capability is a Phase 5
    dependency (§16.1).

The pre-handback checklist
(`docs/design-briefs/_return-process.md`) restates these
deliverables as a paste-ready prompt for Claude Design to
self-verify against at session end.

---

## 18. Provisional / two-states

Reports is a Group B placeholder screen. The brief requires both
designed states per §17:

- **(a) Interim placeholder treatment** ships in the
  polish-interlude app between now and Phase 5 so the page
  doesn't look unfinished next to the four polished screens
  during QA. Replaces today's `PlaceholderPage.xaml` rendering
  (which uses `FontTypography="TitleLarge"` + a default body
  subtitle, ungrounded in the design system).
- **(b) Eventual functional layout** ships with Phase 5
  alongside the `GetDailyReport(date)` IPC implementation and
  History's `(app, date)` filter capability.

The brief locks the durable layers (visual language, IA, state
matrix, severity vocabulary, drill destination, export pattern)
and explicitly flags data specifics as provisional, to lock at
Phase 5 implementation. Provisional items:

- Exact `GetDailyReport` payload field list. PRD §11 enumerates
  intent; the on-wire shape locks when the contract lands. The
  contract MAY grow to accept range or comparison form per Q2.
- Uncommon Talkers signal definition AND the data fields the
  signal consumes (§8.4 Q5; §9).
- Notable today categorization vocabulary beyond the MVP
  `UnsignedFromUserPath` rule. Additional rules arrive post-MVP
  as future `IMonitor`s land (PRD §10).
- Date-picker bounds (likely tied to retention settings).
- Export filename template (e.g.
  `zenvizor-report-YYYY-MM-DD.csv`) and the HTML template's
  exact CSS — the *composition* is locked by Q9's mock; the
  serializer details are implementation.

---

## 19. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as
idiomatic XAML against Wpf.Ui (functional layout) and as a
self-contained HTML template against the projected CSS variables
(HTML export). Nothing in the mock is portable; the dotted-token
names are the contract.
