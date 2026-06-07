# Design implementation kickoff & tracker

Companion to `_brief-template.md`. The brief template is **outbound**
(Code → Design — how to write the brief you paste into Claude Design).
This document is **inbound** (Design → Code — what Claude Code does
when a completed mockup package arrives back, and how per-screen status
is tracked across the design loop).

---

## Per-screen status table

| Screen | Findings | Brief | Mockup | Implementation | Notes |
|---|---|---|---|---|---|
| **Dashboard** | ✓ | ✓ | ✓ | ✓ COMPLETE | reference screen — `docs/dashboard-UI-phase-plan.md` |
| **Per-App** | ✓ | ✓ | ✓ | ✓ COMPLETE | Phases 1-6 run 2026-06-06; reference for drill-grid pattern |
| **App Detail** | ✓ | — | — | — | drill from Per-App; richest layout (chart + two grids + summary) |
| **History** | ✓ | — | — | — | chart-heavy; inherits Phase D infrastructure |
| **Reports** (Group B) | ✓ | — | — | placeholder live | brief covers interim + eventual layout per template §18 |
| **Alerts** (Group B) | ✓ | — | — | placeholder live | same Group B treatment |
| **Settings** (Group B) | ✓ | — | — | placeholder live | same; underlying logic lands Phase 6 |

Status legend: `✓` = complete · `(in progress)` = active work · `—` = not yet started · `placeholder live` = Group B screen running on a shared `PlaceholderPage`; brief work designs the eventual layout.

---

## Mockup arrival — kickoff prompt template

Use this verbatim when handing back a completed mockup PDF. Substitute the screen name in the placeholders:

```
Mockup for <<<Screen Name>>> has landed at
`docs/design/mockups/<<<screen-slug>>>-design-mockups.pdf`.
Brief: `docs/design-briefs/<<<screen-slug>>>.md`.
Findings: `docs/design-briefs/findings/<<<screen-slug>>>.md`.

Run the implementation kickoff per
`docs/design-briefs/_implementation-kickoff.md` "Pre-implementation
tasks":

  1. Framework reconnaissance — probe library APIs the mockup specs
     require; verify enum names / property availability / defaults
  2. CSS-spec extraction — pull exact gradient / color-mix / shadow
     values from `docs/design/colors_and_type.css` for every visual
     the mockup illustrates
  3. Brief-vs-mockup reconciliation — flag any divergence between
     what the brief asked for and what the mockup actually shows
  4. Token presence verification — confirm every token annotation
     resolves in DesignTokens / BrandAccent / HighContrast
  5. Tag each decision as LOCKED vs STARTING POINT
  6. Outline implementation phases (3-6 phases, each with files
     touched + validation gate) for my approval

Stop after the phase outline; await my approval before starting
implementation.
```

---

## Pre-implementation tasks

Run these in order whenever a new mockup arrives. Each produces a one-page report; the combined report lands in chat before implementation begins.

### 1. Framework reconnaissance

Probe every external-library feature the mockup spec leans on. Confirm availability OR flag a gap.

Concrete probes informed by Dashboard's experience:

- **LiveCharts2 v2** — verify enum names (`FindingStrategy.*`, `EasingFunctions.*`), property defaults (`AnimationsSpeed`, `GeometrySize` hover-area implication), wrapper types (`LiveChartsCore.SkiaSharpView.Painting.ImageFilters.DropShadow` not `SkiaSharp.SKImageFilter` directly), and whether a feature is achievable at all from the default surface (custom tooltips via `IChartTooltip<SkiaSharpDrawingContext>` are non-trivial — flag before implementing).
- **Wpf.Ui v4.0.2** — check template internals when the screen touches NavigationView pane, ContextMenu, Flyout, or any custom control. The Wpf.Ui Gallery sample (`lepoco/wpfui` repo) is the canonical pattern reference — consult before attempting fixes from first principles.
- **WPF base** — confirm the layout primitive supports the requested behavior. The Dashboard chart-clipping bug was caused by `Border.MinHeight` not enforcing row size on a `Height="*"` row; the fix was `RowDefinition.MinHeight`. These constraints aren't intuitive — verify against the brief's layout expectations.

**Output**: a list of "library X supports Y at signature Z" lines, with deltas to reconcile against the brief flagged inline.

### 2. CSS-spec extraction

For every visual the mockup illustrates that the brief describes semantically rather than as a CSS expression, pull the exact CSS from `docs/design/colors_and_type.css`.

Specs that consistently need extraction:

- **Gradient definitions** — `linear-gradient(180deg, color-mix(in srgb, var(--accent-default) 18%, transparent), color-mix(in srgb, var(--accent-default) 7%, transparent))` (nav selection background).
- **Catch-light highlights** — `box-shadow: inset 0 1px 0 var(--edge-light)` (composited into `metal.card` as a third gradient stop in dark theme).
- **Card material** — `--metal-card: linear-gradient(180deg, #ffffff, #f6f8fc)` (light) / `linear-gradient(180deg, #2b3040, #232735)` (dark).
- **Shadows** — `--shadow-md: 0 4px 12px rgba(24,27,39,0.08), 0 1px 3px rgba(24,27,39,0.05)` (light) / `0 6px 18px rgba(0,0,0,0.45), 0 1px 3px rgba(0,0,0,0.35)` (dark). WPF supports one `DropShadowEffect` per element — pick the softer long-distance shadow when CSS specifies two-shadow stacks.

**Output**: a list of "spec → exact CSS expression → XAML translation" lines that feed implementation directly.

### 3. Brief vs mockup reconciliation

Walk through the mockup state by state, compare against the brief's specs. Flag every divergence:

- Mockup adds a state the brief didn't mention
- Mockup illustrates a treatment that differs from the brief description (Dashboard's nav selection gradient interpretation is the canonical example — brief said "saturated gradient," I shipped a saturated *block*, the user shared the exact CSS to clarify the fade)
- Mockup omits a state the brief required
- Mockup carries a token name not on the brief's §5 list

**Output**: a "the mockup says X, the brief said Y, my proposed treatment is Z" list. Resolve in chat before implementation.

### 4. Token presence verification

Every token annotation on the mockup must resolve in `DesignTokens.xaml` (alias surface) + `BrandAccent.{Light,Dark}.xaml` (theme-aware override) + `HighContrast.xaml` (HC collapse). New tokens are allowed (brief template §9) but they must be added BEFORE the XAML that references them.

For each annotated token, verify:
- Defined in `DesignTokens.xaml` (or its companion brand dicts)
- Has the right XAML type (`SolidColorBrush` vs `LinearGradientBrush` vs `DropShadowEffect`)
- Has an HC collapse in `HighContrast.xaml` (per brief template §5 precondition)

**Output**: "tokens present / tokens to add / tokens missing HC collapse" lists. Resolve gaps before phase 1.

### 5. Locked vs starting-point decision tagging

Walk through every brief decision and label it explicitly:

- **LOCKED** = durable design law (no Block button, opaque text-bearing cards, top-N cap on Dashboard talkers, passive-only invariant). Don't reconsider during validation. Defend strongly if challenged.
- **STARTING POINT** = first guess that needs empirical tuning (DrawMargin values, animation timings, EWMA α, alpha values for tints, hover zone size). Expect to iterate during validation.

Dashboard had this distinction implicit, which produced wasted cycles when LOCKED items were re-litigated (e.g. nav selection background interpretation) and STARTING POINT items were defended as locked (e.g. the "no vertical gridlines" call that reversed in validation).

**Output**: a categorized list. Inform validation iteration with explicit tolerance for tuning on STARTING POINT items.

### 6. Phase outline

Propose 3-6 implementation phases, ordered to land foundational pieces first and refinements later. Each phase ends with a validation checkpoint.

Phase patterns that worked on Dashboard:

- Phase 1 — tokens + foundational chrome (cards migrate to `surface.card` / `metal.card`, borders to `border.card`, etc.)
- Phase 2 — layout corrections (`MinHeight`, `MaxHeight`, RowDefinition guards, padding/spacing fixes)
- Phase 3 — state coverage (empty / loading / error banners hooked up)
- Phase 4 — interactive behavior (hover, click, scroll, virtualization)
- Phase 5 — chart wiring (when applicable — paints, axes, tooltip, overlay)
- Phase 6 — animation / polish (gated, often deferred)

Each phase ends with a "build / launch / validate" cycle; user signs off before next phase. Stop after the phase outline; await approval before starting.

---

## Per-screen sections

One section per screen. Update each as work progresses; the table at the top of this doc is the at-a-glance status.

### Dashboard — COMPLETE

- **Findings**: `docs/design-briefs/findings/dashboard.md`
- **Brief**: `docs/design-briefs/dashboard.md`
- **Mockup**: `docs/design/mockups/dashboard-design-mockups.pdf` (8 pages, 7 states × 2 themes)
- **Implementation record**: `docs/dashboard-UI-phase-plan.md`
- **Phases run**: A → C.6 + polish round 2 + Phase D + D.7 (gated off) + Phase E (scoped to HC token audit)
- **Commit**: `9b97a1d Dashboard polish (Phases B-E) + Phase D chart wiring complete`

Reference screen for what "complete" looks like. The implementation record captures lessons learned that updated this kickoff doc (the framework recon pre-task, CSS-spec extraction, locked vs starting-point distinction) and the brief template (§4 uptime axis, §5 material tokens, §6.2 tooltip reality clause, §10 framework gotchas).

### Per-App — COMPLETE

- **Findings**: `docs/design-briefs/findings/per-app.md` (243 lines, 15 polish items + 7 feature items)
- **Brief**: `docs/design-briefs/per-app.md`
- **Mockup**: `docs/design/mockups/per-app-design-mockups.pdf` (12 pages)
- **Phases run**: 1 (tokens + VM plumbing) → 2 (chrome migration) → 2.x mini-polish (density / dividers / sort arrow / selection regression / corner overflow) → 3 (column re-templating + sort fix + hover chevron + single-click drill) → 3.x mini-polish (tooltip text inversion / Enter-key drill / descender + ascender ink overshoot) → 4 (state coverage) → 5 (filter wiring + empty-filtered state) → 6 (HC sweep + metal.control + shadow.card)

**Tokens introduced this run**:
- `shadow.sm` (DropShadowEffect, both themes + HC inert) — softer info-card elevation.
- `surface.tooltip.scrim` (SolidColorBrush, text.primary @ 84% alpha + halo) — translucent contrasting popover scrim.
- `metal.control` (LinearGradientBrush, both themes + HC ControlColor) — extends Dashboard's baked-catch-light pattern to short surfaces (controls); dark catch-light at offset 0.05 instead of 0.005 so the lit rim reads at control heights.

**Canonical patterns established here**:
- Summary strip — 3-cell elevated info-card (metal.card + border.card + shadow.sm + radius.md) with text.eyebrow over text.mono. History brief should adopt the same shape (brief §16.1).
- Hover-drill chevron — trailing `Path` (NOT `ui:SymbolIcon` glyph) stroked at 2.75 in `chart.downSeries`, Hidden→Visible on row IsMouseOver via RelativeSource binding. App Detail's recent-sessions grid adopts this when its polish round runs (brief §16.2).
- Single-click row drill — `PreviewMouseLeftButtonUp` walks visual tree to `DataGridRow`, navigates on hit. `Cursor=Hand` + brand-violet selection chrome telegraph clickability. `PreviewKeyDown` for Enter parity. Memory: `feedback_drill_grid_pattern.md`.

**Lessons learned (apply to next screen)**:
- **WPF gotcha**: `Margin="{StaticResource space.16}"` doesn't work — `space.*` tokens are `sys:Double` and `ThicknessConverter` only converts from String. Use literal values in XAML; the tokens document canonical values. Memory: `project_wpf_spacing_token_thickness.md`.
- **WPF gotcha**: `ItemTemplate` and `DisplayMemberPath` are mutually exclusive on ItemsControl — setting both throws `InvalidOperationException` at runtime. When converting a ComboBox to a custom item template, also remove any `DisplayMemberPath` setter from the code-behind.
- **WPF gotcha**: Wpf.Ui's `DataGridCell` selection chrome (brand-violet tint + left pill) lives in its implicit style, NOT in `DefaultDataGridCellStyle` or `DefaultUiDataGridCellStyle`. Custom `CellStyle` setters wholesale-replace the implicit chrome — BasedOn the named keys does NOT recover it. The reliable path is shipping a complete custom `DataGridRow.Template` with explicit IsMouseOver / IsSelected triggers (see `style.datagrid.compact` in `DesignTokens.xaml`).
- **WPF gotcha**: `LineStackingStrategy=MaxHeight` puts slack at the BOTTOM of the line box only — bumping LineHeight on top of that just enlarges bottom slack, never gives ascender room. For ink-overshoot on both ends, use `TextBlock.Padding="0,5,0,1"` (asymmetric — biased top to compensate for the top-anchored baseline). The cell.body.trim style in `PerAppPage.xaml` is the reference.
- **Rule**: no em-dash in user-facing UI copy. Brief or mockup specs that ship em-dash get swapped to period/colon at the implementation layer. Memory: `feedback_no_emdash_in_ui_copy.md`.
- **Wpf.Ui control template parity**: ui:Button and ui:TextBox honor Background TemplateBinding cleanly; ComboBox may fight back. If a follow-up screen applies `metal.control` to a ComboBox and the gradient doesn't paint, BasedOn `DefaultUiComboBoxStyle` is the documented fallback.

### App Detail

- **Findings**: `docs/design-briefs/findings/app-detail.md`
- **Brief**: pending
- **Mockup**: pending
- **Implementation**: pending

### History

- **Findings**: `docs/design-briefs/findings/history.md`
- **Brief**: pending
- **Mockup**: pending
- **Implementation**: pending

### Reports (Group B)

- **Findings**: `docs/design-briefs/findings/reports.md` (spec-derived, not from running XAML)
- **Brief**: pending — must include interim placeholder + eventual layout per brief template §18
- **Mockup**: pending
- **Implementation**: pending; underlying logic lands Phase 5

### Alerts (Group B)

- **Findings**: `docs/design-briefs/findings/alerts.md`
- **Brief**: pending — same Group B two-state treatment
- **Mockup**: pending
- **Implementation**: pending; underlying logic lands Phase 6

### Settings (Group B)

- **Findings**: `docs/design-briefs/findings/settings.md`
- **Brief**: pending — same Group B two-state treatment
- **Mockup**: pending
- **Implementation**: pending; underlying logic lands Phase 6

---

## Source-of-truth pointers

- **Brief template**: `docs/design-briefs/_brief-template.md`
- **Mockup template**: `docs/design-mockup-template.md`
- **Design system**: `docs/design-system.md`
- **Design system primer** (paste with each Claude Design brief): `docs/claude-design-primer.md`
- **CSS source of truth**: `docs/design/colors_and_type.css`
- **Sprint plan**: `docs/zenvizor-sprint-plan.md`
