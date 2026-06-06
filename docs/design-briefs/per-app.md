# Claude Design brief — Per-App

ZenVizor's Per-App screen. Self-contained brief for a fresh Claude Design
session: paste this file together with `docs/claude-design-primer.md` and
produce an annotated mockup for every state listed in §4. The mockup
hand-off contract is in §9.

---

## 1. Screen identity

- **Screen name:** Per-App.
- **XAML file:** `src/ZenVizor.Ui/Views/PerAppPage.xaml` (+ `PerAppPage.xaml.cs`).
- **IA placement:** second item in the left `ui:NavigationView` rail,
  icon `Symbol="Apps24"`. Reached either by clicking the nav-rail item or
  by being the natural drill from Dashboard's talkers list ("I saw
  Discord using a lot of bandwidth, show me the full picture").
- **Purpose (casual voice):** "show me which apps used the network over a
  time window — sorted by who used the most."

---

## 2. UX intent

This polish pass turns Per-App from a data grid that opens to a wall of
rows into the drill experience the casual user expects after spotting
something on Dashboard's talkers list. The empty state speaks instead of
showing a blank grid. Loading shows a deliberate spinner instead of just
a wait cursor. The disconnect-vs-query-failed banner distinction lands so
the user can tell "ZenVizor's down" from "weird SQL hiccup." Security-
relevant signature states (Unsigned / Invalid) finally have visual weight
through caution-colored foreground rather than reading as plain text.
Drill discoverability gets a hover chevron telegraphing the
double-click-to-App-Detail behavior. A summary strip mirrors History's
pattern so the two screens read similarly. Density tightens to the
compact data-grid scale, typography ramps consistently, and the card
joins Dashboard's `metal.card` + `shadow.card` material family for
Mica-safe legibility and visual rhythm continuity.

---

## 3. Controls in scope

The page is a `ui:NavigationView`-hosted Page. Outer `<Grid Margin="space.24">`
with **4 rows**: `Auto / Auto / Auto / *`.

### Row 0 — header

- `ui:TextBlock` page title, `Style="text.subtitle"`, copy `"Per-App"`,
  `FontFamily="font.display"`.
- Beneath, `TextBlock` subtitle, `Style="text.caption"`,
  `Foreground="text.secondary"`, copy `"Apps ranked by total bytes over
  the selected window."`. `Margin="0,space.4,0,0"` from the title.

### Row 1 — picker + summary row

A single `<Grid>` with two columns (`*` / `Auto`) so the summary triplet
right-aligns.

Left column — picker (`<StackPanel Orientation="Horizontal">`):

- `TextBlock` label, `Style="text.caption"`, `Foreground="text.secondary"`,
  copy `"Window"`, `VerticalAlignment="Center"`, `Margin="0,0,space.8,0"`.
- `ComboBox x:Name="WindowCombo"`, `Width="120"`, items `"1h"` / `"24h"` /
  `"7d"` / `"30d"` / `"90d"` (shorthand; the long form
  `"Last 24 hours"` etc. carries as a `ToolTip`), default `SelectedIndex=1`
  (`"24h"`).
- `ui:Button x:Name="RefreshButton"`, `Margin="space.12,0,0,0"`. Content
  is a horizontal `StackPanel`: `ui:SymbolIcon Symbol="ArrowSync24"` +
  `TextBlock Style="text.body"` `"Refresh"`. Picks up Fluent button
  surface from Wpf.Ui's stock template.

Right column — summary triplet (`<StackPanel Orientation="Horizontal">`):

Three pairs of `text.caption` label + `text.mono` value, separated by
`space.16`:

- `"Apps"` / `"<count>"`
- `ui:SymbolIcon Symbol="ArrowUp16"` `Foreground="chart.upSeries"` +
  `"<total up rate-formatted>"`
- `ui:SymbolIcon Symbol="ArrowDown16"` `Foreground="chart.downSeries"`
  + `"<total down rate-formatted>"`

Empty-window state: all three render `"—"` in `text.tertiary`.
Disconnected/query-failed state: rendered as `"—"` in `text.tertiary`
(stale values not shown to avoid implying live).

### Row 2 — banner row (dedicated; one banner visible at a time, both collapsed by default)

A single `<Border x:Name="StatusBanner">` swapping background / foreground
/ copy across two states (see §4 sub-states). `Padding="space.8"`,
`CornerRadius="radius.control"`, `Margin="0,space.12,0,0"`.

### Row 3 — DataGrid card

`<Border>` outer card with:

- `Background="metal.card"` (gradient brushed-card surface — matches
  Dashboard polish round 2)
- `BorderBrush="border.card"`, `BorderThickness="1"`
- `CornerRadius="radius.card"`
- `Effect="shadow.card"` (`DropShadowEffect` — same family as Dashboard)
- `Padding="0"` (DataGrid manages its own row padding via the compact
  density style)
- `Margin="0,space.12,0,0"`

Inside, three Z-stacked layers (only one of {grid populated / loading /
empty} is visible at a time):

1. `<DataGrid x:Name="AppsGrid" Style="{StaticResource style.datagrid.compact}">`
   — see column spec below.
2. `<StackPanel x:Name="LoadingOverlay" Visibility="Collapsed"
   HorizontalAlignment="Center" VerticalAlignment="Center">`:
   - `ui:ProgressRing IsIndeterminate="True"`,
     `HorizontalAlignment="Center"`.
   - `TextBlock Style="text.caption"` `Foreground="text.secondary"` copy
     `"Loading…"`, `Margin="0,space.12,0,0"`,
     `HorizontalAlignment="Center"`.
3. `<TextBlock x:Name="EmptyText" Visibility="Collapsed" Style="text.body"
   Foreground="text.secondary" HorizontalAlignment="Center"
   VerticalAlignment="Center">` copy
   `"No applications observed in this window."`

DataGrid column specs:

| Column | Width | Style | Notes |
|---|---|---|---|
| App | `2*` | `text.body` | `TextTrimming="CharacterEllipsis"`. Renders `ImageName` only (no path — drill to App Detail for that). |
| Publisher | `2*` | `text.body`, `Foreground="text.secondary"` | `TextTrimming="CharacterEllipsis"`. `"(unknown)"` when null. |
| Signature | `100` | `text.body` | **Conditional foreground**: `"Signed"` → `text.primary`. `"Unsigned"` / `"Invalid"` → `status.caution`. `"Unchecked"` → `text.tertiary`. Rendered as `DataGridTemplateColumn` to host the conditional binding. |
| Up/s | `110` | `text.mono` | `TextAlignment="Right"`. Rate-formatted (`B/s` → `KB/s` → `MB/s`). |
| Dn/s | `110` | `text.mono` | Same as Up/s. |
| (trailing) | `32` | — | Hover chevron column (see below). |

**Trailing hover chevron** (presentation-only, drill telegraph):

A `DataGridTemplateColumn` Width=`32`, no header. Cell template is a
`ui:SymbolIcon Symbol="ChevronRight12"` `Foreground="text.tertiary"`,
`HorizontalAlignment="Right"`, `Visibility="Collapsed"` by default.
`DataGridRow.Triggers` flips `Visibility` to `Visible` on `IsMouseOver=
True`. Behavior unchanged — double-click is still the drill mechanism;
the chevron is a visual hint that something happens when the user
interacts with the row.

DataGrid behavior properties (carry from current XAML):

- `AutoGenerateColumns="False"`, `IsReadOnly="True"`,
  `HeadersVisibility="Column"`, `GridLinesVisibility="None"`.
- `Background="Transparent"`, `BorderThickness="0"`,
  `RowBackground="Transparent"`,
  `AlternatingRowBackground="surface.subtle.alt"`.
- Virtualization on: `EnableRowVirtualization="True"`,
  `VirtualizationMode="Recycling"`, `ScrollUnit="Item"`,
  `CanContentScroll="True"`.
- `MouseDoubleClick="OnRowDoubleClick"`, `SelectionMode="Single"`,
  `SelectionUnit="FullRow"`.

### Chrome (MainWindow — drawn in mock for reconciliation, not on this page)

- Title bar at top, `ui:TitleBar` over Mica.
- Bottom-bar rate mirror at right slot of the status bar:
  `ui:SymbolIcon ArrowUp16 Foreground="chart.upSeries"` + `text.mono`
  up-rate value, space, `ui:SymbolIcon ArrowDown16
  Foreground="chart.downSeries"` + `text.mono` down-rate value.
  These reflect the live `ActivitySnapshotPoller` (not the
  window-bounded Per-App total), so they're a different number from
  the Per-App summary triplet — drawn so the design can audit that the
  two numbers don't visually claim to mean the same thing.
- Service-status dot at left of the status bar:
  `status.connected` / `status.disconnected` paint.

---

## 4. State coverage

States to render in the mockup. Per-App is a **history surface** (queries
SQLite, not the in-memory aggregate) so warming and uptime sub-states are
n/a — see brief template §4 for why.

| State | What changes vs default | Notes |
|---|---|---|
| `default` | Connected; grid populated with rows; summary triplet shows real values; no banner. | Steady state. |
| `loading` | Banner hidden; grid hidden behind `LoadingOverlay` (centered `ProgressRing` + `"Loading…"` caption); summary triplet shows `"—"`. | Fires on first paint AND on any in-flight refetch. Wait cursor stays (existing behavior). |
| `empty` | Connected; grid replaced by `EmptyText` `"No applications observed in this window."`; summary triplet shows `"0 / 0 B/s / 0 B/s"` (zeros, not em-dashes — distinguishes from disconnected). | Window had no traffic. Real outcome when the user picks a 1h window on a quiet system. |
| `disconnected` | Banner visible, **critical class**: `Background="status.critical.background"`, foreground `"status.critical"`, copy `"Service disconnected — last refresh stale."` Grid dimmed (`Opacity=0.6`) showing last-known rows. Summary triplet `"—"` in `text.tertiary`. | Service pipe down. Fires when `HistoryQueryClient` catches one of `ConnectionLostException` / `IOException` / `ObjectDisposedException`. |
| `query failed` | Banner visible, **caution class**: `Background="status.caution.background"`, foreground `"status.caution.text"`, copy `$"Query failed ({ex.GetType().Name}): {ex.Message}"`. Grid dimmed showing last-known rows (or empty if first refresh failed). Summary triplet `"—"`. | Non-pipe exceptions (SQL hiccup, etc.). |

States NOT in this brief: `warming` (live-surface only — n/a here);
uptime sub-state (live-surface only — n/a here).

---

## 5. Tokens in scope

> **Precondition check.** All tokens listed below resolve to brand-spec
> values in `BrandAccent.{Light,Dark}.xaml` today — verified at Dashboard
> polish round 2. New tokens added during Dashboard (`metal.card`,
> `edge.light`, `shadow.card`) are present in `DesignTokens.xaml` +
> `BrandAccent.{Light,Dark}.xaml` + `HighContrast.xaml`. No new tokens
> are introduced by this brief.

- **`surface.card`** — n/a on this page; we use `metal.card` instead.
- **`surface.subtle.alt`** — `DataGrid.AlternatingRowBackground` (every
  other row gets a faint tint per design-system §3).
- **`metal.card`** — outer card background (matches Dashboard polish
  round 2 material treatment).
- **`edge.light`** — baked into `metal.card`'s top stop in dark theme;
  no per-element use on this page.
- **`shadow.card`** — outer card `Effect` (`DropShadowEffect`).
- **`text.subtitle`** — page title (`"Per-App"`).
- **`text.caption`** — subtitle, picker label (`"Window"`), summary
  triplet labels, loading caption.
- **`text.body`** — DataGrid column text (App, Publisher, Signature)
  and column headers.
- **`text.body.strong`** — DataGrid column header weight (carry via
  the compact density style).
- **`text.mono`** — numeric columns (Up/s, Dn/s) and summary triplet
  values; both need tabular numerics for column alignment.
- **`text.primary`** — Signature column when `"Signed"`.
- **`text.secondary`** — Publisher column, subtitle copy, picker label,
  summary labels, loading caption, empty-state text.
- **`text.tertiary`** — Signature column when `"Unchecked"`; em-dash
  placeholders in summary triplet during disconnected / query-failed.
- **`status.caution`** — Signature column when `"Unsigned"` /
  `"Invalid"` (signature failure is a caution-class signal at body
  size).
- **`status.caution.background`** + **`status.caution.text`** — banner
  paints for the `query failed` state.
- **`status.critical`** + **`status.critical.background`** — banner
  paints for the `disconnected` state.
- **`chart.upSeries`** — summary triplet up-arrow `ui:SymbolIcon`
  foreground (matches Dashboard bottom-bar mirror).
- **`chart.downSeries`** — summary triplet down-arrow foreground.
- **`border.card`** — outer card stroke.
- **`space.4`** / **`space.8`** / **`space.12`** / **`space.16`** /
  **`space.24`** — paddings and margins from the 4-based scale. Outer
  page margin `space.24`.
- **`radius.card`** — outer card corners.
- **`radius.control`** — banner corners.

No `accent.*` tokens — Per-App has no accent surfaces (no eyebrows, no
pills, no filled buttons that need brand violet). The Refresh button
inherits Wpf.Ui's stock button treatment.

---

## 6. Chart-chrome — n/a (no chart on this screen)

Per-App has no `lvc:CartesianChart`. The summary triplet's up/down arrows
use `chart.upSeries` / `chart.downSeries` *brushes* for consistency with
Dashboard's bottom-bar rate mirror, but no chart-chrome wiring applies.

---

## 7. Density assignment

- **`AppsGrid`**: **compact** — `Style="{StaticResource style.datagrid.compact}"`
  (row height 22, padding `6,2`, body font 14). Per design-system §8,
  data-dense DataGrids use compact density.

---

## 8. Locked decisions relevant to this screen

### 8.1 Global locks

- **Loading = default Fluent `ProgressRing`**, NOT skeleton-shimmer.
- **High Contrast** handled by `HighContrast.xaml` — the mock does not
  draw HC variants. HC merge wiring is `docs/design-system.md` §11
  item 9 (still pending across the whole app, not Per-App-specific).

### 8.2 Screen-specific locks (carry per-screen)

- **`AppsGrid.MaxHeight` enforced programmatically** in
  `EnforceAppsGridBound` on `Loaded` + `SizeChanged` via the existing
  `Math.Max(200, window.ActualHeight - 220)` formula. Required because
  `ui:NavigationView`'s `DynamicScrollViewer` gives hosted pages infinite
  vertical extent — without the cap, the `DataGrid` materializes every
  row instead of virtualizing
  (memory: `project_wpfui_navigationview_scrollviewer.md`).
- **Default density is compact** on this screen — not a knob the polish
  can re-litigate.
- **Drill behavior is double-click to `AppDetailPage`** (passes
  `AppId` via `DataContext`). The hover chevron is presentation-only;
  double-click stays the interaction.
- **Signature column conditional foreground** is the only visual
  treatment for signature state — no icon, no badge, no chip. Per the
  findings doc, "Signed" / "Unsigned" / "Invalid" / "Unchecked" stay
  as plain-text labels colored per signal class.
- **Server-side sort by total bytes descending** is the only sort.
  Column headers are not sortable.
- **Outer card uses `metal.card` + `shadow.card`** — matches Dashboard
  polish round 2. Every data-bearing card in the app uses the same
  material family.
- **Picker shorthand** (`"1h"` / `"24h"` / `"7d"` / `"30d"` / `"90d"`)
  in the ComboBox display; long form (`"Last 24 hours"`) lives as a
  `ToolTip`.

### 8.3 Boundary-case overrides

**Intentionally n/a.** Per-App follows the default split between
`disconnected` and `query failed` (§4) and is the canonical
*uncapped drill* surface that §11 (discovery > ranking) calls for —
no overrides.

---

## 9. Annotation rules

The hand-off contract — mockup annotations reference design tokens by
**semantic name**. Full rules in `docs/design-mockup-template.md §1`.
Non-negotiables:

- **Canonical dotted token names ONLY.** `surface.card`, `metal.card`,
  `text.secondary`, `status.caution.text`, `radius.card`, `space.16`.
  Never PascalCase. Never strip the dots.
- **Never use the legacy CSS aliases** from
  `docs/design/colors_and_type.css` (`--fg1`, `--fill-card`, etc.).
- **`--fill-card` is the translucent variant** — any data-bearing card
  is `metal.card` (gradient, opaque) or `surface.card` (flat, opaque),
  never `surface.card.alt`.
- No new tokens are introduced by this brief. If the mockup wants a
  token that isn't in §5, flag it in the handoff notes for review
  rather than annotating an unrecognized name.

---

## 10. Per-screen WPF translation gotchas

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages get infinite vertical extent. `AppsGrid` virtualization
  depends on `MaxHeight` being set programmatically on `Loaded` and
  `SizeChanged` via `EnforceAppsGridBound`. Without the cap, the
  `DataGrid` materializes every row.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled`** on the Per-App nav item — the page
  instance survives nav away/back, so picker selection persists across
  visits. `Loaded` does NOT refire on return; any re-measure work hangs
  off `SizeChanged`.
- **`Wpf.Ui.NavigationView` paints `NavigationViewContentBackground`
  over the Page content area** — overridden to `Transparent` in
  `App.xaml.cs ApplyDirectLevelOverrides()` (already done during
  Dashboard polish round 2). No new work needed on this brief — just
  flagging so the implementation doesn't reintroduce the occlusion.
- **`DataGridTemplateColumn` for conditional foreground.** The
  Signature column needs a per-row foreground; that requires
  `DataGridTemplateColumn` hosting a `TextBlock` with a converter
  binding, not the simpler `DataGridTextColumn`.
- **DataGrid `AlternatingRowBackground` is a Wpf.Ui-inherited
  property.** Reference the design-system token `surface.subtle.alt`
  via `DynamicResource`; today's code references
  `SubtleFillColorTertiaryBrush` directly (same value, wrong name).
  This is a rename, not a value change.

---

## 11. Discovery > ranking constraint

**Per-App IS the uncapped drill surface** the hard rule calls for. No
top-N cap, no client-side truncation, no "see more" pagination. The user
coarsens by **time** via the window picker (1h → 90d), not by rank. The
server returns the full list for the chosen window sorted by total bytes
descending; the DataGrid virtualizes to render only what's on screen.

If the mockup proposes any kind of row count cap or "show top X" affordance,
that violates §11. Reject in handoff.

---

## 12. Honest attribution constraint

Per-App attributes traffic at the **process level**, not the service
level:

- `AppListEntry` does NOT carry `HostedServices` (verified — see
  `Ipc.Contracts/Dto/AppListResult.cs`). When `svchost.exe` hosts
  multiple services, the Per-App row reads `"svchost.exe"` with no
  service decoration. The PID's byte total is what the row reports —
  bytes are NOT split across co-hosted services.
- Traffic from DLL injection / LOLBins (`rundll32`, `regsvr32`,
  `mshta`, `powershell`) attributes to the host process. Per-App
  doesn't pretend to know the injected code's origin.
- Users who want service-level visibility drill into App Detail (which
  surfaces `HostedServices` on the summary card).

The mockup must NOT visually imply per-service splits, attribution
confidence, or behavioral metadata Per-App doesn't have. Plain rows of
PID-level attribution is the honest treatment.

---

## 13. Passive-only constraint

Never hint at "block this app," "kill its connections," or any active
intervention. No "Block" / "Quarantine" / "Kill" buttons. No
context-menu items beyond drill-to-detail. The hover chevron telegraphs
drill, NOT action.

This is a CLAUDE.md invariant — non-overridable on any screen.

---

## 14. Performance budget reminder

- Idle CPU < 1%, working set < ~80 MB.
- No shimmer / live blur / continuous animation. The hover chevron is a
  static visibility flip on `IsMouseOver`, not an animated affordance.
- Per-App's `RefreshAsync` cadence is user-driven (button or picker
  change), not polled. No new polling introduced by this polish.
- `DataGridTemplateColumn` for Signature adds per-row binding cost
  vs. `DataGridTextColumn` — measure during validation; if it shows
  in the idle budget, switch to a `Style` with `DataTrigger` on the
  cell instead. Likely a non-issue at typical row counts.

---

## 15. Out-of-scope — features flagged for later

From the findings doc's Feature column. Listed explicitly so Claude
Design does NOT design for them in this round:

- **F1. Column-click sort.** DataGrid supports it; not enabled today.
  Adds a new interaction.
- **F2. Inline filter / search box.** `IZenVizorIpc.GetAppList(window,
  filter)` has a filter parameter at the contract layer; UI doesn't
  expose it. Adding a search box = new capability.
- **F3. Custom window picker (free-form date range).** Today is 5
  presets. Custom range = feature.
- **F4. Persist picker selection across launches.** Settings concern
  (Phase 6).
- **F5. svchost service decoration on Per-App rows.** `AppListEntry`
  doesn't carry `HostedServices` (verified above). Adding it requires a
  contract change.
- **F6. Path column** showing `AppListEntry.ImagePath`. Presentation
  polish (data is available) but adds a new visible field and pushes
  the grid wider. Drill to App Detail to see path.
- **F7. Active-action affordances** (kill / block buttons). HARD NO
  per §13 — passive-only is non-overridable. Not in any feature
  backlog. Noted so a mockup can't quietly add one.

---

## 16. Chrome / cross-screen consequences

**Intentionally n/a — Per-App inherits MainWindow chrome unchanged.**

The bottom-bar rate mirror, service-status dot, and title bar are all
MainWindow-scope chrome from Phase B and don't change for Per-App. The
mock draws them alongside the page so the design can audit that the
mirror's live totals (from `ActivitySnapshotPoller`) don't visually
claim to mean the same thing as Per-App's window-bounded summary
triplet — but no new shared-code surface is introduced.

---

## 17. Deliverables expected from Claude Design

- Layouts for every state in §4 (`default`, `loading`, `empty`,
  `disconnected`, `query failed`), light + dark.
- Token annotations using canonical dotted names (§9).
- Density tag on `AppsGrid` (compact — §7).
- Layout hints:
  - `AppsGrid.MaxHeight` enforced programmatically (note in margin).
  - Card uses `metal.card` + `shadow.card`.
  - `scroll: pane` on the DataGrid (internal scroll only; page itself
    does not scroll past the card).
- Chrome (title bar + bottom-bar rate mirror + service-status dot)
  drawn alongside the page so summary-triplet vs. live-mirror
  reconciliation is auditable in the mock (§16).
- No chart — chart-chrome tokens (§6) are n/a here.

---

## 18. Provisional / two-states — n/a

Per-App is a Group A built screen, not a Group B placeholder. This
section does not apply.

---

## 19. Handoff back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the dotted token
names are the contract. The implementation kickoff
(`docs/design-briefs/_implementation-kickoff.md`) walks the pre-flight
tasks — framework recon, CSS-spec extraction, brief-vs-mockup
reconciliation, token presence verification, locked vs starting-point
tagging, phase outline.
