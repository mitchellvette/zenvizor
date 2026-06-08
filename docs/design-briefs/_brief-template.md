# Per-screen Claude Design brief — template

Reusable structure for every screen's Claude Design brief. One brief per
screen, self-contained, paste-ready into Claude Design. Brief = the input
that produces a mockup; the mockup is then translated back to XAML here.

> **Briefs are written AFTER the human reviews the corresponding pre-brief
> doc in `docs/design-briefs/findings/`.** That review is where UX
> judgments get injected. Filling a brief before review draws against
> unreviewed assumptions and burns a Claude Design session.

> **The primer is a one-time alignment, NOT a co-paste.**
> `docs/claude-design-primer.md` is pasted into a Claude Design session
> ONCE — before any briefs are written — to calibrate its design-system
> understanding to ZenVizor's: tokens, type ramp, density rules,
> data-viz palette, component vocabulary, project principles. After that
> pass, **each brief is pasted ALONE.** Claude Design carries the
> aligned design system from the prior session.
>
> Do NOT instruct the user to "paste this together with the primer."
> Do NOT restate the primer's content inside the brief — token
> definitions, type-ramp px sizes, the discovery>ranking rule, the
> passive-only rule, the light-and-fast budget, the annotation
> vocabulary, the component list. The brief references those by name
> and assumes Claude Design knows what they mean. Per-screen
> *application* of a principle (which surface on this screen carries
> the rule; whether this screen overrides) belongs in the brief;
> *recitation* of the principle does not.

---

## How the brief is grounded

Each screen has a pre-brief doc in `findings/`. Two groups:

- **Group A — built screens (Dashboard, Per-App, App Detail, History).**
  Pre-brief is a *findings doc* grounded in the running XAML and
  view-models. Its scope sort (polish vs feature) is the input that
  determines what the brief asks Claude Design to design.
- **Group B — placeholder screens (Reports, Alerts, Settings).**
  Pre-brief is a *spec-derived design doc* grounded in
  `docs/zenvizor-prd.md` + `docs/zenvizor-sprint-plan.md`. The brief
  designs durable layers (visual language, IA, layout, interaction) and
  flags data specifics as provisional, to be reconciled when Phase 5/6
  implementation lands.

Both kinds feed the same brief template below.

---

## Brief structure

Each per-screen brief MUST contain the sections below, in this order.
Sections marked **(R)** are required even when "n/a" — answer
"intentionally n/a: <reason>". Sections marked **(C)** are conditional
on the screen having that surface (e.g. chart, DataGrid, flyout, chrome
spillover).

### 1. Screen identity (R)

- Screen name and the file path of its XAML (or `PlaceholderPage` for
  Group B).
- IA placement (which nav-rail item, footer or menu, and what the entry
  point looks like to the user).
- One-sentence purpose, casual-user voice.

### 2. UX intent (R)

The agreed UX intent for this polish iteration, lifted from the reviewed
findings doc. This is the "what the screen should feel like and what it
must do better" paragraph — distilled from the scope sort, NOT a copy
of the friction list. One short paragraph.

For Group B, lifted from the proposed-layout section of the spec-derived
doc.

### 3. Controls in scope (R)

The controls Claude Design should render — described by **type and
purpose**, NOT by layout, width, binding, or arrangement. The brief
carries the surface area; Claude Design composes the page.

For Group A: derive from the findings doc's control walk by
collapsing each control to "what it is for the user" rather than
"what's in the XAML today." E.g. "a window picker for selecting the
trailing time range," NOT "ComboBox x:Name=WindowCombo Width=200
bound to WindowPreset.All with five preset items."

For Group B: derive from the spec-derived doc's proposed control list.

Each control listed by Mockup-template label
(`docs/design-mockup-template.md §2`) — `ui:FluentWindow`,
`ui:NavigationView`, `ui:TextBlock`, `ui:Button`, `Border`, `DataGrid`,
`ListView`, `ui:ProgressRing`, `lvc:CartesianChart`, etc. — paired
with a one-sentence purpose.

**Do NOT specify** in this section: layout rows/columns, control
widths, padding values, bindings, properties, item arrangement, or
inter-control spacing. Composition is Claude Design's work. If the
brief settled a control's specific *outcome* during the findings
review (e.g. "window picker labels render as shorthand `1h / 24h /
7d / 30d / 90d`"), that outcome lock belongs in §8.2 — not here.

### 4. State coverage (R)

The full applicable state set for this screen, per the primer's
per-screen state matrix
(`docs/claude-design-primer.md "State matrix"`).
Every cell that applies to this screen must appear in the brief — the
mock has to span the matrix, not just the happy path.

**Theme coverage.** Every state below renders in the default (light)
theme. ONE steady-state variant additionally renders in **dark** theme
so theme-swap behavior is auditable. A state whose treatment is
theme-trivial (e.g. a status banner where only the background swaps)
may collapse to a single rendering with a noted dark color; the dark
steady-state mock is mandatory.

Locked decisions to carry into every state spec:

- **Loading state = default Fluent `ProgressRing` (NOT skeleton-shimmer)**,
  centered in the surface that will hold data (chart card / grid
  viewport). Indeterminate when no progress fraction is known; add a
  `text.caption` `text.secondary` caption below if wait may exceed ~1 s.
  Rationale: shimmer is continuous animation that costs more under WPF
  than a static ring and pays no benchmark dividend (light-and-fast
  principle, design-system §2).
  - **Loading-state surfaces.** If the screen has more than one surface
    that holds data (Dashboard has 4 status cards + chart + talkers),
    enumerate the per-surface treatment in the brief. Default pattern:
    spinner on chart / grid / list surfaces; em-dash placeholders
    (`Style="text.mono"`, Foreground `text.tertiary`) on small headline
    cards where a spinner would visually overwhelm the value slot.
- **Empty-state copy is screen-specific** — App Detail and History use
  "No traffic recorded in this window."; Per-App / Connections /
  Sessions need their own copy ("No applications observed…",
  "No endpoints recorded…", "No sessions recorded…"); Dashboard's
  talkers list uses "No active talkers in this window." Specify the
  literal copy in the brief.
- **Warming applies to Dashboard only** (live surface). History
  surfaces do not have a warming state — they query SQLite, not the
  in-memory aggregate.
- **Live surfaces need an uptime sub-state.** Screens whose data fills
  a trailing time window from a live in-memory aggregate (Dashboard's
  chart is the canonical example) must specify what the surface looks
  like during the *initial fill window* — when the buffer holds less
  than its full duration. The brief specifies visible behavior at:
  `t=0` (no data), `t=midway` through fill (partial buffer), and
  `t=steady-state` (buffer full). Catches the class of issue where a
  fixed-width chart with auto-fitting axes misrepresents data positions
  during the initial 1-2 minutes of uptime — the Dashboard polish
  round added a fixed-window scrolling X-axis so the static `-2m / ... /
  now` overlay labels stay positionally accurate during initial fill
  (data accumulates right-to-left rather than stretching to fill).
  History surfaces query SQLite and don't have a fill window — n/a
  there.
- **Disconnected vs query-failed — SPLIT BY DEFAULT.** Most screens
  render two distinct copies — one for pipe down
  (`status.critical.background`, "Service disconnected — last refresh
  stale") and one for any other query failure
  (`status.caution.background`, "Query failed (`<type>`): `<msg>`").
  `HistoryQueryClient.IsConnectionLost` is the existing branch.
  **A screen MAY override this default and merge the two** if its data
  path makes the distinction meaningless (e.g. Dashboard's
  `ActivitySnapshotPoller` catches every exception under
  `IsConnected: false` and exposes the type via `FailureReason` — the
  findings doc accepted the merge). Document any override in §8.3.
- **Sub-state variants are allowed.** One state-matrix cell may host
  two visual treatments under one name when the screen distinguishes
  them (e.g. Dashboard's `disconnected — transient` 1st failed cycle
  vs `disconnected — steady` >1 cycle, with different banner copy and
  token pairs). Render both sub-states in the mock with explicit
  "sub-state:" labels in §4 of the brief.

### 5. Tokens in scope (R)

The **constraints** that bound Claude Design's paint work on this
screen — NOT a per-element assignment list. The primer's token table
lists what's available; this section narrows that palette to what's
in scope for this screen and notes the hard constraints. Claude Design
assigns specific tokens to specific elements as part of its
composition work.

> **Precondition check (R).** For every token category listed, verify
> the in-palette tokens resolve to the brand-spec value today, not a
> stock Wpf.Ui placeholder. The `docs/design/colors_and_type.css`
> header crosswalk records which values are reconciled vs deferred.
> If any token's current XAML value would visibly diverge from the
> mockup (e.g. accent text appearing OS-blue instead of brand-violet
> because the brand-dict migration hasn't reached that token yet),
> flag it in the brief as a dependency on a prerequisite sub-phase
> rather than discovering the gap after the mock returns. The
> Dashboard polish round learned this the hard way — eyebrows landed
> OS-blue because the brand-dict migration was implicit, not
> scheduled.

State the constraint per category — NOT the assignment:

- **`surface.*`** — Mica + contrast rule: any text- or data-bearing
  card on this screen MUST sit on `surface.card` (opaque).
  Translucent `surface.card.alt` is forbidden for text-bearing
  surfaces. Decorative non-text panels MAY use `surface.subtle` or
  `surface.card.alt` as Claude Design judges fit.
- **`text.*`** — full type ramp is available (`text.title` /
  `text.subtitle` / `text.body` / `text.body.strong` / `text.caption`
  / `text.eyebrow` / `text.mono`). Per-screen constraint: any numeric /
  path / IP / column-aligned digit run MUST use `text.mono` (the
  primer's typography rules cover this). Otherwise unconstrained.
- **`accent.*`** — `accent.default` is NEVER a filled background (AA
  violation in dark theme); only `accent.fill` carries text. State
  whether this screen uses any filled accent surfaces in this round
  (yes / no). If no, Claude Design has no accent fill work to design.
- **`status.*`** — list which banners exist on this screen with their
  intended state (warming / disconnected / error / success / neutral).
  Do NOT pre-assign which paired bg/foreground token goes on which
  banner — the brief's state coverage in §4 specifies the *state*,
  Claude Design picks the token pair that paints it within the
  primer's mapping.
- **`border.*`** — cards take `border.card` (not raw Wpf.Ui keys like
  `ControlElevationBorderBrush`). Subtle dividers take `border.subtle`.
- **`space.*`** — page outer margin is always `space.24`. Other gaps
  choose from the 4-based scale per Claude Design's composition.
- **`radius.*`** — role tokens only (`radius.card`, `radius.control`,
  `radius.overlay`). No raw scale tokens.
- **Material / effect tokens** — **the default for every text- and
  data-bearing card on every page is the Dashboard / Per-App metallic
  treatment: `metal.card` background + `edge.light` baked-in
  catch-light + `border.card` 1px stroke + `shadow.card` elevation +
  `radius.card` corner.** This is a project-wide surface decision (see
  `docs/design-system.md` §9 "Card surface — canonical treatment"),
  not a per-brief negotiation. Briefs do NOT need to re-derive it and
  MUST NOT specify "flat `surface.card` only" or "no metallic surfaces
  on this screen" without a documented reason that overrides the
  default (e.g. the screen is OS-chrome-class, not data-class).
  Available tokens — `metal.card` (gradient brushed-card surface,
  `LinearGradientBrush`), `edge.light` (inset top catch-light per CSS
  `box-shadow inset 0 1px 0`), `shadow.card` (`DropShadowEffect`),
  `shadow.sm` (softer sibling for info-strip surfaces), `metal.control`
  (same family at control heights). Static gradients + single
  `DropShadowEffect` only — no live blur, no animated sheen.
  Composition (which surface gets which token) is Claude Design's.

**New tokens** — if this screen needs a token that isn't in the
primer's table, list it here per the §9 rules (canonical dotted
name, proposed value or alias source, one-line rationale). Most
briefs introduce no new tokens.

### 6. Chart-chrome — tokens AND behavior spec (C — required wherever a chart appears)

LiveCharts2 paints **none** of the axis / gridline / label / tooltip /
legend chrome from the UI theme, AND it does not honor design-intent
axis behavior or tooltip ergonomics by default. Where this screen has a
chart, the brief must specify both 6.1 (paint tokens) and 6.2 (behavior).

#### 6.1 Chart-chrome paint tokens

- `chart.axis` — axis line stroke.
- `chart.gridline` — gridline stroke (apply low alpha in code).
- `chart.axis.label` — axis tick labels (= `text.tertiary`).
- `chart.tooltip.bg` — tooltip surface, OPAQUE so contrast is stable.
- `chart.tooltip.text` — tooltip label text.
- `chart.legend.text` — legend label text.
- `chart.upSeries` / `chart.downSeries` for up/down line/stack series.
- `chart.wan` / `chart.local` for any WAN-vs-local categorical split
  (used by chart series AND by card-level viz primitives like a
  WAN/LOCAL stacked bar; the brief lists the token under §6.1 even
  when the consumer isn't a `lvc:CartesianChart`).
- `chart.series.1..8` for any other categorical viz, in slot order.

Chart chrome does NOT inherit theme — it is re-applied in code on
`ApplicationThemeManager.Changed` via `ZenVizor.Ui.Services.ChartTheming`.
The brief states the chart tokens used; wiring is implementation, not
mockup.

#### 6.2 Chart behavior spec

Behavior the implementer wires in code (`ChartBuilder`, `ChartTheming`,
per-page paint application). The brief specifies the design intent;
wiring is implementation. Cover each that applies:

- **Y-axis behavior.** `MinLimit`, upper-bound smoothing OR fixed band
  (to prevent jitter when the axis re-flows on rate changes), tick
  step (anchor to "nice" round values per current unit). Cite the
  unit-progression policy (e.g. round to 5/10/20/50/100…) if the screen
  needs predictable axis values for casual readers.
- **X-axis behavior.** Label format (absolute wall-clock vs. relative
  trailing-window labels like `"-2m" … "now"`), tick `MinStep` so
  density is readable, endpoint anchoring.
- **Tooltip behavior.** Finding strategy (X-snap = cursor anywhere
  along X width activates nearest bucket; default = exact line hit).
  Content format (single-row vs. multi-row; relative + absolute time
  pairing; rate formatting). Tooltip background is OPAQUE via
  `chart.tooltip.bg` — never translucent over Mica.
  - **Tooltip layout reality.** LiveCharts2 v2's default tooltip is
    **header + per-series rows** (vertical layout). Per-series
    formatters (`XToolTipLabelFormatter` / `YToolTipLabelFormatter`)
    customize each row's text but not the overall structure. A strict
    single-row format (e.g. `"-90s · 23:34:10 · Up X · Dn Y"`) requires
    implementing a custom `IChartTooltip<SkiaSharpDrawingContext>` end
    to end — substantial work. The brief specifies which layout the
    screen targets and marks the strict single-row form as a deferred
    follow-up when only achievable via custom tooltip.
  - **Tooltip hover sensitivity is driven by `LineSeries.GeometrySize`.**
    With `GeometrySize=0` (used to hide visible point markers), the
    hit area is effectively zero. For "hover anywhere" behavior, set
    `GeometrySize` to roughly the per-tick unit width (~20px on a
    2-second-cadence series) AND set `GeometryFill = GeometryStroke = null`
    so the markers remain invisible. The brief calls this out for any
    line-series chart that wants forgiving hover.
  - **Tooltip drop shadow** for backdrop separation when the tooltip
    background tone is similar to the card backdrop. Wire via
    `LiveChartsCore.SkiaSharpView.Painting.ImageFilters.DropShadow` on
    the `TooltipBackgroundPaint`. Low-alpha black (~38%), 4px offset,
    8px sigma blur gives clear separation without heaviness.
- **Legend behavior.** Position (`Top` is current convention) and Name
  strings. Do NOT duplicate axis units in Name strings (e.g. `"Up"` /
  `"Down"`, not `"Up B/s"` — the axis owns the units).
- **Series stroke/fill split.** Line series get a stroke from
  `chart.upSeries` / `chart.downSeries`; fill is a translucent alpha
  variant (~25%) of the same color for the area under the line.
  Stacked column series get a fill from the same tokens; stroke
  defaults to none.
- **Theme-flip re-paint.** Chart paints are re-applied on
  `ApplicationThemeManager.Changed` via `ChartTheming`. Brief annotates
  tokens; subscription is in code.

### 7. Density assignment (C — required for data lists: DataGrid OR ListView)

- Compact (`style.datagrid.compact`, row 22, padding 6,2): apply on
  Per-App `AppsGrid`, App Detail `ConnectionsGrid` and `SessionsGrid`,
  and any other data-dense DataGrid added in polish.
- Default (Wpf.Ui stock, row 28): apply on Dashboard `TalkersList`
  ListView (already at 8,4 — keep). Default is the implicit density
  for any list not explicitly tagged.
- Comfortable: not used in ZenVizor today.

### 8. Locks and open questions for this screen (R)

Pin what's settled; surface what's open. Organize as four buckets:

#### 8.1 Global locks (always include)

- **Loading = default WPF `ProgressRing`**, not skeleton-shimmer (see
  §4 and design-system §2).
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HighContrast activation
  (`SystemParameters.HighContrast` flips). The mock does not need to
  draw HC variants — `HighContrast.xaml` collapses every semantic
  token onto `SystemColors.*` keys at runtime. The brief just
  acknowledges this and reminds the implementer to verify the screen
  in HC during the per-page verification gate
  (design-system §10).

#### 8.2 Screen-specific locks — OUTCOMES only (carry per-screen as applicable)

UX outcomes from the findings review that must not be re-opened in
the mock. **Lock outcomes (what the user gets), NOT implementations
(how Claude Design delivers them).**

- **Outcome lock (good):** *"drill-down discoverability must improve"*
  / *"window totals must be visible somewhere on the page"* /
  *"signature trust state must be visually distinguishable for
  Unsigned and Invalid rows"* / *"the filter must narrow visible rows
  client-side, not server-side"*.
- **Implementation lock (avoid here):** *"summary strip is 3 cells in
  APPS / UP / DOWN order with text.caption eyebrows above text.mono
  values"* / *"trailing chevron column 24 px wide visible on row
  hover"* / *"Signature foreground is `status.caution` for Unsigned
  and Invalid"*. These pre-decide Claude Design's composition work
  and defeat the purpose of the design round. They belong in the
  returned mock, not in the brief.

If a finding settled both the outcome AND the implementation, lock
the outcome here; move the implementation to §8.4 as an open
question framed by the finding's reasoning. Claude Design may land
on the same implementation — but it does so by judgment within the
constraint, not by transcription.

Recurring examples of outcome-style locks:

- **App Detail flyout (when proposed):** opaque
  `surface.layer` panel, NOT a frosted / acrylic surface. WPF has no
  per-element backdrop; translucent over Mica fails text contrast and
  costs measurable GPU per frame. Real Acrylic is reserved for OS
  surfaces only (tray context menu). *Outcome locked: opaque.
  Composition open.*
- **Brand chrome / brushed-steel surfaces:** static
  `LinearGradientBrush` (`metal.card` / `metal.control`) + 1px stroke
  (`border.card`) + single `DropShadowEffect` (`shadow.card` /
  `shadow.sm`). No live blur, no animated sheen. *Outcome locked: this
  is the canonical card treatment on every data-bearing surface; see
  §5 material-tokens note. Static, no continuous animation.
  Composition open.*
- (Per-screen items go here — anything the findings doc settled at
  the OUTCOME level: required visibility of a piece of data, required
  hierarchy between two surfaces, required visual distinction between
  states, etc.)

#### 8.3 Boundary-case overrides of hard rules

When this screen explicitly overrides one of the hard rules in §11
(discovery > ranking) or §12 (honest attribution), or §4's default
split between disconnected and query-failed, capture the override here
with rationale and cross-reference the rule's section. §13 (passive-only)
is non-overridable. Recurring examples:

- **Dashboard overrides §11 (discovery > ranking)** for its talkers
  list — the Top 10 cap is intentional, framed explicitly as "top by
  current rate." Drill-down (uncapped) lives on Per-App, not Dashboard.
- **Dashboard overrides §4's disconnected/query-failed split** — the
  `ActivitySnapshotPoller` catches every exception under
  `IsConnected: false` with `FailureReason`. The findings doc accepted
  the merge for the live surface only; history surfaces (Per-App, App
  Detail, History) retain the split.

If the screen overrides nothing, answer "intentionally n/a."

#### 8.4 Open design questions for Claude Design

Where the brief deliberately leaves judgment open, list the question
here. Each question invites Claude Design to **propose one or more
variants** the user picks from during iteration. **This is the section
that makes the design round produce value over the brief alone.**
Without open questions, Claude Design transcribes the brief rather
than designing.

Each question states:

- **What is open** — one sentence, user-facing framing of the
  decision (not the implementation).
- **Constraints** — what bounds the proposal: token availability,
  the findings-doc friction the answer must solve, anything in §8.2
  the proposal must respect, hard rules from §11–§13 that still apply.
- **Variant count** — whether the brief expects one proposal with
  rationale, or 2–3 variants for the user to choose between.

Example shape:

- **Window-total surfacing.** *Open:* how should the window's total
  apps / Up / Down counts be made visible so the user reads them in
  one glance? *Constraints:* must be visible without page scroll;
  uses tokens already in scope (no new tokens); does not compete
  visually with the DataGrid card. *Variants:* propose 2–3.
- **Drill-down discoverability.** *Open:* how should it become
  obvious to the user that a row drills into App Detail on
  double-click? *Constraints:* presentation-only — drill behavior
  (double-click) does not change; no row-action affordances per §13.
  *Variants:* propose 1–2.
- **Signature trust state.** *Open:* how should the visual
  distinguish Unsigned / Invalid signature rows from Signed /
  Unchecked rows at a glance? *Constraints:* must not imply trust
  precision beyond what `WinVerifyTrust` returns (§12); foreground
  color is one option, alternatives include subtle background, icon,
  or pill. *Variants:* propose the option Claude Design recommends
  with rationale; alternative if Claude Design sees a stronger case.

Answer "intentionally n/a: every UX decision was locked at outcome
level in the findings review and no implementation latitude remains"
only when that is genuinely true. **If the section feels empty, the
brief is probably over-locked** — re-read §8.2 with the
outcome-vs-implementation distinction in mind and move the
implementation-level locks here as questions.

### 9. Annotation work specific to this screen (R)

The primer's annotation vocabulary governs the hand-off contract
(canonical dotted token names; no legacy CSS aliases; new-token
naming pattern; no inventing values inline). **Do NOT restate it.**
The brief carries only per-screen annotation work:

- **New tokens this brief introduces.** Any token the screen needs
  that doesn't exist in the primer's token table must be named in
  the `<category>.<role>[.<modifier>]` pattern, listed here with
  its proposed value or alias source, and called out in the
  hand-off notes so it gets added to `DesignTokens.xaml`,
  `design-system.md`, and `colors_and_type.css` before XAML
  implementation begins.
- **Per-screen renames / repointings.** When this screen repoints
  a raw Wpf.Ui resource key to a semantic token (e.g.
  `SubtleFillColorTertiaryBrush` → `surface.subtle.alt`), list the
  rename here so the implementer knows it's a pointer change, not
  a value change. Include the brush key being repointed and the
  semantic token it's pointing to.
- **Answer "intentionally n/a: no new tokens, no renames"** if the
  screen's polish stays entirely within the existing token surface.

### 10. Per-screen WPF translation gotchas (R)

Per-screen reminders so the implementer doesn't trip on the same
landmines the design-system already documents. Chart *behavior* specs
(axis limits, label format, tooltip ergonomics) live in §6.2 — this
section is for layout / control-template / event-wiring gotchas.
Required entries:

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages get infinite vertical extent. Any DataGrid / ListView
  on the page that needs to virtualize must have its `MaxHeight` set
  programmatically on `Loaded` + `SizeChanged`. Today's pages use
  `EnforceDataGridBounds` (App Detail) and `EnforceAppsGridBound`
  (Per-App). New grids added in polish must wire equivalent code.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled`** on every nav-rail item — the page
  instance survives nav away/back, so `Loaded` does NOT refire on
  return. Anything that must re-measure on revisit hangs off
  `SizeChanged`, not `Loaded`.
- **SkiaSharp chart paints do NOT inherit DynamicResource.** Chart
  series colors and chrome are applied in C# (`ChartBuilder` +
  `ChartTheming`) and re-applied on `ApplicationThemeManager.Changed`.
  The brief annotates token names; wiring is in code.
- **`Wpf.Ui.NavigationView` paints `NavigationViewContentBackground`
  over the Page content area.** Default is ~30% gray, occluding Mica
  showthrough on the page side (the pane and chrome are outside this
  Border). For Mica showthrough on the page side, override the
  resource key to `Transparent` in `App.xaml.cs ApplyDirectLevelOverrides()`.
  The Dashboard polish round 2 hit this — it was the missing piece
  for page-area Mica visibility after all other transparency overrides
  were in place.
- **`Grid.RowDefinition.MinHeight` is needed** for `Height="*"` rows
  with `MinHeight` children to enforce row minimums. A `Height="*"`
  row will gladly shrink below its child's `Border.MinHeight` and the
  Border overflows downward into the next row, causing visual clipping
  (e.g. Dashboard's chart card under the talkers card at small window
  sizes). Set `MinHeight` on the `RowDefinition` itself (value = child
  `Border.MinHeight + Margin.Top`) so the Grid enforces the minimum
  at the row level. `Border.MinHeight` alone is insufficient.
- **`LiveCharts2.Axis.MinLimit/MaxLimit` can be updated per tick** to
  anchor a fixed-window scrolling axis (e.g. always
  `[newestPoint − 120s, newestPoint]`). Useful for time-series during
  initial buffer fill so static overlay labels stay positionally
  accurate even when the data buffer hasn't filled the full trailing
  window yet (Dashboard's right-to-left fill behavior).
- **`RateFormatter` is binary (1024-aligned).** Nice round axis
  values must be `{1, 2, 5} × 10ⁿ × 1024ᵏ` (binary-aware), not pure
  decimal `{1, 2, 5} × 10ⁿ`, or labels format as `"19.5 KB/s"` when
  the underlying axis value is decimal 20000. The brief's rate-axis
  spec calls out binary alignment when nice rounding is requested.
- Screen-specific entries (carry these where they apply):
  - **App Detail** carries the most: two side-by-side grids + the
    chart card + a flyout (when added). The two grids must each cap
    `MaxHeight` to `(windowH - 220) / 2` so both virtualize; the
    flyout (when added) is opaque `surface.layer`.
  - **Dashboard** talkers card needs programmatic `MaxHeight`
    enforcement on `Loaded` + `SizeChanged` so the ListView
    virtualizes and scrolls internally instead of expanding past the
    visible area. The Dashboard PAGE never scrolls — chart and status
    row are always visible.
  - **History** chart card has a `MinHeight=320` outer / chart
    `MinHeight=280` inner — confirm or revise per its findings doc.

### 11. Discovery > ranking — per-screen application (R)

The primer covers the rule under "Project principles to preserve in
design decisions." **Do NOT restate it.** The brief states only:

- **Where the rule applies on this screen.** Identify the drill list
  or data surface and confirm it is uncapped server-side (no top-N,
  no "see more" gate, no ellipsis that hides rows). If the screen has
  a filter or search input, confirm it narrows by user intent — not
  by score.
- **Whether this screen overrides the rule.** If yes, document the
  override in §8.3 with rationale. Dashboard's "Top 10" talkers list
  is the canonical override (live glance surface explicitly framed as
  "top by current rate"); Per-App is the canonical compliant drill
  surface that pairs with it.
- **Answer "intentionally n/a"** if the screen has no list or drill
  surface that the rule could meaningfully shape (e.g. Settings).

*Memory: `project_discovery_principle.md`.*

### 12. Honest attribution — per-screen application (R)

The primer covers the rule under "Project principles to preserve in
design decisions." **Do NOT restate it.** The brief states only:

- **How this screen handles `svchost` co-hosting.** If the screen has
  rows or cells that could carry per-PID byte totals, specify how
  multi-service PIDs render without implying per-service byte
  attribution. Dashboard uses the bracketed-services convention
  (`svchost.exe [Service1, Service2]`) because `TalkerRowViewModel`
  carries `HostedServices`. Per-App today has no service decoration
  because `AppListEntry` does not carry it (contract change deferred).
  State which path this screen takes and why.
- **How this screen handles host-process attribution for injected
  code / LOLBins.** Typically: it does not visualize it — the
  documented boundary is preserved without UI hints, asterisks, or
  caveat icons. If this screen does something different, state it.
- **Whether this screen overrides the rule.** If yes, document the
  override in §8.3 with rationale.
- **Answer "intentionally n/a"** if the screen has no per-PID byte
  surface (e.g. History aggregates everything; Settings has no
  attribution surface at all).

### 13. Passive-only — per-screen application (R, non-overridable)

The primer covers the rule under "Project principles to preserve in
design decisions" and the CLAUDE.md invariant makes it non-overridable.
**Do NOT restate it.** The brief states only:

- **Explicit non-presence of action affordances on this screen.**
  Confirm no kill / block / quarantine / "stop this app" / right-click
  action menu / hover-revealed action buttons are designed. The mock
  must NOT include them.
- **Drill / navigation affordances ARE allowed and ARE passive.** A
  hover chevron telegraphing double-click drill, a click-to-navigate
  row, an open-in-detail action — all passive. State which drill
  affordances this screen carries so the distinction between drill
  (allowed) and action (forbidden) is auditable.

**Passive-only is non-overridable** — no screen may carry an active
affordance regardless of product circumstance. §8.3 cannot host an
override.

### 14. Performance budget — per-screen application (R)

The primer's "Light and fast" principle covers the budget (idle CPU
< 1%, working set < ~80 MB, no per-event DB writes). **Do NOT restate
it.** The brief states only screen-specific design choices that
protect the budget:

- **No shimmer / live blur / continuous animation** on this screen's
  loading or idle treatments. Confirm static `ProgressRing` for
  loading; static gradients + single `DropShadowEffect` for any
  metallic surfaces (no animated sheen, no acrylic backdrop on text
  cards).
- **Debounce / cadence specifics for any interactive surface** —
  filter / search / scrub / hover-track. State the debounce interval
  (Per-App's filter uses ~150 ms via `DispatcherTimer`) so the brief
  doesn't accidentally invite per-keystroke or per-frame work.
- **Any new polling cadence faster than the existing 2 s Dashboard
  tick / 5 s status tick** has to justify itself against the budget.
  State if added; state "no new cadence" if the screen reuses
  existing pollers or is on-demand.
- **Chart-paint batching** (where a chart is present): one
  `chart.UpdateLayout()` after a full paint rebuild, not per-axis.
  Implementer detail — note that batching is the expectation.

### 15. Out-of-scope — features flagged for later (R)

The items from the findings-doc scope sort that fell into the
**Feature** bucket. The brief lists them explicitly so Claude Design
does NOT design for them in this round, and so a later phase has the
list ready when those features are scheduled.

For Group A this is lifted from the findings doc's "Feature" column.
For Group B this is anything the spec-derived doc flagged as deferred
beyond the phase's MVP scope.

### 16. Chrome / cross-screen consequences (C)

When this screen's findings trigger changes to MainWindow chrome
(bottom bar, status bar, title bar), to shared services
(`ChartTheming`, `ChartBuilder`, shared pollers), or to other screens
that inherit the change via shared code, capture them here so the
brief carries the consequences visibly. Two purposes:

1. **Reconciliation.** Chrome visible while this screen is shown
   (e.g. a bottom-bar rate mirror) must agree with the screen's own
   numbers — the mock draws the chrome alongside the page so the
   reconciliation is auditable.
2. **Cross-screen surface.** When this screen's polish reaches into
   shared code (ChartTheming tooltip paints, ChartBuilder series
   paints, MainWindow-scope pollers), the other six briefs need to
   know what NOT to redesign.

For each consequence list:

- **What changed** — the chrome element / shared-service behavior.
- **Where it lives** — `MainWindow.xaml`, `Services/ChartTheming.cs`,
  etc.
- **Propagates to** — other screens that inherit it via shared code.
- **Per-screen brief work needed elsewhere** — "none, shared code
  handles it" if applicable; otherwise what the other screens still
  need to design around it.

If the screen has no cross-screen consequences, answer "intentionally
n/a."

### 17. Deliverables expected from Claude Design (R)

What the mock must contain when handed back:

- Layouts for every state in §4 in the default (light) theme,
  annotated per `docs/design-mockup-template.md`.
- ONE steady-state layout additionally rendered in **dark** theme so
  theme-swap behavior is auditable. (Mandatory — the brief covers a
  Windows app that follows OS theme; both themes ship.)
- Variant proposals for each open question in §8.4. The user picks
  one variant per question during iteration; the **final** mock at
  session end carries the selected variant clearly indicated.
- Token annotations using canonical dotted names (§9).
- Density tags wherever density differs from default (§7).
- Layout hints (`MinHeight`, `MaxHeight`, `scroll: page` / `scroll: pane`)
  wherever they matter (§10).
- Chart-chrome token names AND chart behavior spec called out where a
  chart appears (§6).
- Chrome / cross-screen elements drawn in the mock for reconciliation
  where applicable (§16).
- For Group B screens, both the interim
  "coming-in-Phase-5/6" placeholder treatment AND the eventual
  functional layout — see "Provisional / two-states" section below.

The pre-handback checklist (`docs/design-briefs/_return-process.md`)
restates these deliverables as a paste-ready prompt for Claude Design
to self-verify against at session end.

### 18. Provisional / two-states (Group B only — R for Reports, Alerts, Settings)

Group B screens are placeholders today. The brief MUST request:

- **(a) Interim placeholder treatment** the page wears in the shipped
  polish-interlude app, so it doesn't look unfinished next to the four
  polished screens during Phase 5/6 QA. Spec the icon, the type ramp
  style, the copy ("Coming in Phase 5/6 — short summary"), and the
  surface (no card; centered on `surface.background`).
- **(b) Eventual functional layout** that lands in Phase 5/6.

And the **provisional-data flag**: the brief locks the durable layers
(visual language, IA, layout, interaction) but explicitly marks data
specifics as "lock at Phase 5/6":

- Reports — exact `GetDailyReport` payload field list, "notable items"
  thresholds.
- Alerts — exact `Alert` entity rendering, severity-to-color mapping
  beyond the three semantic statuses, filter-chip vocabulary.
- Settings — precise per-setting NumberBox bounds, caption copy, key
  ordering within sections.

### 19. Handoff back to Claude Code (R)

The brief closes with a one-line reminder of the handoff contract:
mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the tokens are
the contract.

---

## Style notes for filling the brief

- **The primer is a one-time alignment, not a co-paste** (see the
  intro). Brief copy targets a Claude Design model whose design system
  was aligned to ZenVizor's by a prior primer-loading pass — it knows
  the tokens, type ramp, density rules, palette, component vocabulary,
  and project principles. It does NOT know this codebase. Be concrete:
  reference real things by name (XAML files, control names,
  view-models) so the mock can match them.
- Keep the brief paste-ready — one Markdown file per screen, no
  attachments. The user pastes the brief alone into the aligned
  Claude Design conversation.
- **Don't restate primer content.** No token definitions, no type-ramp
  px sizes, no rule recitations, no annotation-vocabulary rules, no
  component-list restatements. Brief sections that exist to apply a
  primer rule per-screen (§9, §11, §12, §13, §14) state only what's
  screen-specific: where the rule lands, what the screen does with it,
  whether the screen overrides it.
- Don't redefine tokens in the brief — reference them. The source of
  truth for the app side is `DesignTokens.xaml` / `design-system.md`;
  the source of truth for the mock side is
  `docs/design/colors_and_type.css`; the crosswalk in the CSS file
  header is the bridge. The brief never invents values inline.
- Copy strings (banner text, empty-state messages, tooltip strings)
  live inline alongside the tokens that paint them — keeping them in
  context aids review. There is intentionally no separate "all copy"
  aggregator section; copy and its paint surface should be read
  together.
