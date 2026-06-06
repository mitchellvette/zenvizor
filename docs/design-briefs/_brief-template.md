# Per-screen Claude Design brief — template

Reusable structure for every screen's Claude Design brief. One brief per
screen, self-contained, paste-ready into Claude Design. Brief = the input
that produces a mockup; the mockup is then translated back to XAML here.

> **Briefs are written AFTER the human reviews the corresponding pre-brief
> doc in `docs/design-briefs/findings/`.** That review is where UX
> judgments get injected. Filling a brief before review draws against
> unreviewed assumptions and burns a Claude Design session.

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

The actual controls Claude Design should render. For Group A: the real
XAML control list (lifted verbatim from the findings doc's "what is
literally on it today" section, minus anything the scope sort flagged
out, plus any presentation-only additions the polish allows).

For Group B: the proposed control list from the spec-derived doc.

Each control listed by Mockup-template label
(see `docs/design-mockup-template.md §2`) — `ui:FluentWindow`,
`ui:NavigationView`, `ui:TextBlock`, `ui:Button`, `Border`, `DataGrid`,
`ListView`, `ui:ProgressRing`, `lvc:CartesianChart`, etc.

### 4. State coverage (R)

The full applicable state set for this screen, per the primer's
per-screen state matrix
(`docs/claude-design-primer.md "State matrix"`).
Every cell that applies to this screen must appear in the brief — the
mock has to span the matrix, not just the happy path.

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

The semantic-token surface area for this screen. Pull from
`docs/design-system.md`. List the tokens by category:

> **Precondition check (R).** For every token listed, verify it resolves to
> the brand-spec value today, not a stock Wpf.Ui placeholder. The
> `docs/design/colors_and_type.css` header crosswalk records which values
> are reconciled vs deferred. If any token's current XAML value would
> visibly diverge from the mockup (e.g. accent text appearing OS-blue
> instead of brand-violet because the brand-dict migration hasn't reached
> that token yet), flag it in the brief as a dependency on a prerequisite
> sub-phase rather than discovering the gap after the mock returns. The
> Dashboard polish round learned this the hard way — eyebrows landed
> OS-blue because the brand-dict migration was implicit, not scheduled.

- `surface.*` — which cards/sections are which surface, with the
  Mica + contrast rule applied: any text/data-bearing card sits on
  `surface.card` (opaque), not `surface.card.alt` (translucent).
- `text.*` — which type-ramp Style each text run uses
  (`text.title` / `text.subtitle` / `text.body` /
  `text.body.strong` / `text.caption` / `text.eyebrow` / `text.mono`).
- `accent.*` — only `accent.fill` carries text. `accent.default` is
  never a filled background (AA violation in dark theme).
- `status.*` — paired bg/foreground for every banner / pill.
- `border.*` — `border.card` for cards (NOT
  `ControlElevationBorderBrush`).
- `space.*` — paddings and margins, from the 4-based scale. Outer page
  margin is always `space.24`.
- `radius.*` — **role tokens** (`radius.card`, `radius.control`,
  `radius.overlay`), not raw scale tokens.
- **Material / effect tokens** — when the screen has metallic /
  brushed surfaces, drop shadows, or catch-light highlights, enumerate
  them explicitly alongside the `surface.*` row: `metal.card` (gradient
  brushed-card surface, `LinearGradientBrush`), `edge.light` (inset top
  catch-light per CSS `box-shadow inset 0 1px 0`), `shadow.card`
  (`DropShadowEffect`). These are XAML types other than
  `SolidColorBrush` so the mock annotations carry the distinction
  between flat `surface.card` and gradient `metal.card`, and the
  implementation knows to wire effects vs background brushes. The
  Dashboard polish round 2 introduced these — every subsequent
  data-bearing card uses `metal.card` + `shadow.card` for consistency.

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

### 8. Locked decisions relevant to this screen (R)

Pin the decisions the screen must adopt without re-litigating in the
mock. Organize as three buckets:

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

#### 8.2 Screen-specific locks (carry per-screen as applicable)

UX decisions from the reviewed findings doc that must not be re-opened
in the mock. Lift them verbatim. Recurring examples:

- **App Detail flyout (when proposed):** opaque
  `surface.layer` panel, NOT a frosted/acrylic surface. WPF has no
  per-element backdrop; translucent over Mica fails text contrast and
  costs measurable GPU per frame. Real Acrylic is reserved for OS
  surfaces only (tray context menu).
- **Brand chrome / brushed-steel surfaces (if any):** static
  `LinearGradientBrush` + 1px stroke + single `DropShadowEffect`. No
  live blur.
- (Per-screen items go here — talkers card MinHeight/MaxHeight, list
  ordering rules, dimmed-row persistence durations, legend Name
  strings, etc. — anything the findings doc settled.)

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

### 9. Annotation rules (R)

The hand-off contract: mockup annotations reference design tokens by
**semantic name**. The full rules are in `docs/design-mockup-template.md
§1`. Restate the non-negotiables in every brief so Claude Design can't
miss them:

- **Canonical dotted token names ONLY.** `surface.card`,
  `text.secondary`, `chart.upSeries`, `radius.card`, `space.16`.
  Never PascalCase. Never strip the dots.
- **Never use the legacy CSS aliases** from
  `docs/design/colors_and_type.css` (`--fg1`, `--fill-card`,
  `--series-up`, etc.). The CSS is the mock-side source of truth, but
  annotations come back to the app via the dotted token names — that
  is the bridge. The crosswalk in the CSS file header documents which
  CSS alias maps to which dotted token.
- **`--fill-card` is the translucent variant** of the surface.
  Mapping it to a text-bearing card would migrate the wrong surface
  into the implementation and fail WCAG AA. Any data-bearing card is
  `surface.card` (opaque).
- New tokens that don't exist yet follow the pattern
  `<category>.<role>[.<modifier>]` (`docs/design-mockup-template.md §6`)
  and must be listed in the handoff notes so they get added to
  `DesignTokens.xaml` and `design-system.md` before XAML implementation.

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

### 11. Discovery > ranking constraint (R, hard rule)

Never cap drill-down lists by score / bytes. A stealthy malicious
process won't be in any top-N. If a surface has too much data, coarsen
by **time** (rollups, downsample), not by **rank**. No "see more"
gates, no ellipsis truncation that hides rows.

This shapes Per-App and App Detail in particular. The Dashboard
"Top 10" is a deliberate exception: Dashboard is a live, fixed-height
glance surface explicitly framed as "top by current rate" — it is not
the drill surface. The brief must call this distinction out wherever
relevant.

**If this screen overrides this hard rule, document the override in
§8.3 (boundary-case overrides) with rationale.**

*Memory: `project_discovery_principle.md`.*

### 12. Honest attribution constraint (R, hard rule)

Don't visually imply precision we lack:

- When `svchost.exe` hosts multiple services, list them AND report the
  PID's byte total. Do not split bytes across co-hosted services.
- Traffic from DLL injection / LOLBins (`rundll32`, `regsvr32`,
  `mshta`, `powershell`) attributes to the host process. This is a
  known boundary, not a bug; the screen does not pretend otherwise.

The brief must specify how the screen visually distinguishes
"PID with one service" from "PID hosting many services" without
implying per-service byte attribution.

**If this screen overrides this hard rule, document the override in
§8.3 (boundary-case overrides) with rationale.**

### 13. Passive-only constraint (R, hard rule — non-overridable)

Never hint at "block this app," "kill this connection," or any active
intervention. ZenVizor observes; it never blocks. No "Block" buttons,
no kill icons, no active-action affordances anywhere on any screen.

This is a CLAUDE.md invariant. **Passive-only is non-overridable** —
there is no design or product circumstance under which a screen may
add an active affordance. The brief states this so a Claude Design
session can't quietly add a "Block" button to a row.

### 14. Performance budget reminder (R)

Idle CPU < 1%, working set < ~80 MB, no per-event DB writes. The
brief's design proposals must not regress this. Specifically:

- No shimmer / live blur / continuous animation that pays no benchmark
  dividend.
- Chart paint changes must batch (`chart.UpdateLayout()` once after a
  full paint rebuild, not per-axis).
- Any new polling cadence faster than the existing 2 s dashboard tick
  / 5 s status tick has to justify itself against the budget.

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

- Layouts for every state in §4, annotated per
  `docs/design-mockup-template.md`.
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

- Brief copy targets a Claude Design model: assume it knows the design
  system primer (`docs/claude-design-primer.md`) is loaded, but doesn't
  know this codebase. Be concrete; reference real concrete things by
  name (XAML files, control names, view-models) so the mock can match
  them.
- Keep the brief paste-ready — one Markdown file per screen, no
  attachments. The user pastes the brief into a fresh Claude Design
  conversation; the primer goes along with it.
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
