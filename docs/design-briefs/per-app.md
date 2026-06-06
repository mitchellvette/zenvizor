# Claude Design brief — Per-App

ZenVizor's Per-App screen. Self-contained brief for a fresh Claude
Design session: paste this file together with
`docs/claude-design-primer.md` and produce an annotated mockup for
every state listed in §4. The mockup hand-off contract is in §9.

---

## 1. Screen identity

- **Screen name:** Per-App.
- **XAML file:** `src/ZenVizor.Ui/Views/PerAppPage.xaml` (+ `PerAppPage.xaml.cs`).
- **IA placement:** second item in the left `ui:NavigationView` rail,
  icon `Symbol="Apps24"`. The user lands here from the nav rail when
  they want the full ranked list of apps for a chosen trailing window.
- **Purpose (casual voice):** "which apps used my network in the last
  hour/day/week, and how much."

---

## 2. UX intent

Per-App is the drill surface — uncapped list of apps over the selected
window, double-click to drill into App Detail. This polish round
upgrades it from "functional data grid" to "first-class drill surface":
compact density that respects how dense this grid actually reads, mono
digits that column-align across rows, ellipsis trimming on overflow
text, header-sort that *works* on the byte columns (today they sort
lexically — `"900 KB"` ranks above `"400 MB"`), a summary strip that
shows the window's totals, a hover chevron that telegraphs the
double-click drill, signature coloring on the security-relevant rows, a
client-side filter for fast narrowing, and an ImagePath tooltip on the
App cell so the user can confirm path safety without drilling. State
coverage gets explicit empty-state copy, a centered `ProgressRing` for
loading, and the canonical history-class split between
`disconnected` (pipe down) and `error` (query failed) — the opposite
of Dashboard's merged treatment. Cards migrate to the opaque
`surface.card` family so text legibility on Mica is no longer
wallpaper-dependent.

---

## 3. Controls in scope

The page is a `ui:NavigationView`-hosted Page. Outer
`<Grid Margin="space.24">` with **3 rows**: `Auto / Auto / *`.

### Row 0 — header + summary strip

A 2-column `Grid` so the header sits left and the summary strip
right-aligns on the same baseline.

- **Left cell — title cluster** (StackPanel, Orientation=Vertical):
  - `ui:TextBlock` page title, `Style="text.subtitle"`, copy
    `"Per-App"`. Bind explicit `FontFamily="{StaticResource font.display}"`
    — the existing XAML omits this and inherits the default; Dashboard
    binds it explicitly and Per-App should match.
  - `ui:TextBlock` caption beneath the title, `Style="text.caption"`,
    Foreground inherits `text.secondary` from the Style, copy
    `"Apps ranked by total bytes over the selected window."`
  - Inline-right of the title at the same baseline: `Border`
    `StatusBanner` (`Visibility="Collapsed"` by default; see §4 for
    visible-state spec). When visible, sits as a pill to the right of
    the title with `space.16` left margin.

- **Right cell — summary strip** (`Border`,
  `padding=space.12,space.8`, `radius.control`, no card surface — sits
  on the page background so it doesn't compete visually with the
  DataGrid card below):
  - `Grid` with 3 equal columns (`* / * / *`), `space.16` column gap.
  - Per cell: a `StackPanel` Vertical with:
    - Eyebrow `ui:TextBlock Style="text.caption"` Foreground
      `text.secondary`, copy `APPS` / `UP` / `DOWN`. Uppercased via the
      template style; NOT the `text.eyebrow` style — summary-strip
      eyebrows are intentionally understated (`text.secondary`) so the
      strip doesn't compete with the DataGrid's own column headers
      below it.
    - Value `ui:TextBlock Style="text.mono"`, Foreground
      `text.primary`. Apps cell shows the count (e.g. `"47"`); Up /
      Down cells show humanized bytes (`FormatBytes` output:
      `"3.2 GB"`).

### Row 1 — picker row

`StackPanel Orientation="Horizontal"`, `space.12` between elements:

- Label `ui:TextBlock Style="text.caption"`, Foreground inherits
  `text.secondary`, copy `"Window:"`, vertically centered.
- `ComboBox x:Name="WindowCombo" Width="120"`. Item template renders
  shorthand: `1h / 24h / 7d / 30d / 90d`. Each item carries a WPF
  `ToolTip` with the long form (`"Last 1 hour"`, `"Last 24 hours"`,
  …). Default `SelectedIndex=1` (last 24 hours) unchanged.
- `ui:Button x:Name="RefreshButton"` — `ui:SymbolIcon Symbol="ArrowSync24"`
  + text `"Refresh"` (icon-leading, `space.4` gap). Replaces today's
  text-only button.
- `ui:TextBox` (Wpf.Ui's `TextBox` with placeholder support) —
  `x:Name="FilterInput"`, `Width="240"`, `PlaceholderText="Filter
  apps…"`. Sits at the end of the row, optionally right-aligned in
  the StackPanel via a trailing spacer. Filter is client-side — see
  §8 and §10 for wiring.

### Row 2 — DataGrid card

- `Border` (card) — Background `surface.card` (opaque),
  BorderBrush `border.card` 1px, `radius.card`, `padding=0` (the grid
  manages its own row padding via the compact style).
- Inner `DataGrid x:Name="AppsGrid"` — apply
  `Style="{StaticResource style.datagrid.compact}"` (row 22, padding
  6,2, body font; see §7). Properties unchanged: `AutoGenerateColumns=False`,
  `IsReadOnly=True`, `HeadersVisibility=Column`, `GridLinesVisibility=None`,
  `Background=Transparent`, `BorderThickness=0`,
  `RowBackground=Transparent`,
  `AlternatingRowBackground="{DynamicResource surface.subtle.alt}"`
  (token rename — currently uses raw `SubtleFillColorTertiaryBrush`),
  `MouseDoubleClick=OnRowDoubleClick`, `SelectionMode=Single`,
  `SelectionUnit=FullRow`, virtualization unchanged.
- **Columns** (left → right):

  | # | Header      | Width | Binding            | SortMemberPath  | Template                                           |
  |---|-------------|-------|--------------------|-----------------|----------------------------------------------------|
  | 1 | App         | `2*`  | `ImageName`        | `ImageName`     | `DataGridTemplateColumn` — `TextBlock` with `TextTrimming=CharacterEllipsis` and `ToolTip={Binding ImagePath}` |
  | 2 | Publisher   | `2*`  | `PublisherDisplay` | `PublisherDisplay` | `DataGridTextColumn` with `TextTrimming=CharacterEllipsis` (set on `ElementStyle`) |
  | 3 | Signature   | `100` | `SignatureStatus`  | `SignatureStatus`  | `DataGridTemplateColumn` — `TextBlock` with conditional Foreground per signature value (see §8) |
  | 4 | Up          | `110` | `UpText`           | `BytesUp`       | `DataGridTextColumn`, `text.mono`, right-aligned   |
  | 5 | Down        | `110` | `DownText`         | `BytesDown`     | `DataGridTextColumn`, `text.mono`, right-aligned   |
  | 6 | (chevron)   | `24`  | —                  | —               | `DataGridTemplateColumn` — `ui:SymbolIcon Symbol="ChevronRight12"` Foreground `text.tertiary`, default `Visibility=Hidden`, flipped to `Visible` via the row's `IsMouseOver` trigger (see §10) |

  Column headers carry `Style="text.body.strong"` (set via the
  compact DataGrid style's `ColumnHeaderStyle`). Numeric headers
  (`Up`, `Down`) right-align so the header text agrees with its
  column's right-aligned values.

- **No "Path" column.** `AppListEntry.ImagePath` surfaces via the App
  cell's tooltip (column 1) — not a column. Locked in §8.

---

## 4. State coverage

States to render. Every state below MUST appear in the mockup.

### `state: default` (steady-state, connected, data flowing)

- Title cluster filled; caption beneath; StatusBanner collapsed.
- Summary strip filled with the window's totals (apps count, Up
  bytes, Down bytes).
- Picker row: ComboBox at `24h`, Refresh button enabled, filter
  input empty (placeholder visible).
- DataGrid populated with ~10–40 rows visible (more virtualized
  below). Header row at top, alternating row backgrounds.
- One row drawn in **hover state** — trailing chevron visible in
  column 6, row background subtly highlighted per Wpf.Ui's row hover.
  This is the canonical drill affordance — see §8 for the cross-screen
  pattern note.
- One App cell drawn with a WPF tooltip popped showing the full
  `ImagePath` (e.g. `"C:\Program Files\Google\Chrome\Application\chrome.exe"`),
  Foreground `text.primary`, Background `surface.layer` (opaque),
  `border.card` 1px stroke, `radius.overlay`, `padding=space.8`.
- At least one row with Signature `"Unsigned"` and at least one with
  `"Invalid"` to show the conditional Foreground (`status.caution`)
  — see §8. At least one `"Signed"` row (Foreground `text.primary`)
  and one `"Unchecked"` row (Foreground `text.tertiary`).

### `state: loading`

- StatusBanner collapsed.
- Summary strip values render as `"—"` (em-dash), `Style="text.mono"`,
  Foreground `text.tertiary` until first paint completes.
- DataGrid card body: centered `ui:ProgressRing IsIndeterminate="True"`,
  `space.12` below it a `ui:TextBlock Style="text.caption"`
  Foreground `text.secondary` copy `"Loading…"`. Caption renders only
  after ~1 s of wait so quick refreshes don't flash. NO
  skeleton-shimmer (locked decision §8).

### `state: empty` (window has no traffic)

- StatusBanner collapsed.
- Summary strip values render `"0"` / `"0 B"` / `"0 B"` (NOT em-dash
  — the query succeeded, the answer is genuinely zero).
- DataGrid card body: centered `ui:TextBlock Style="text.body"`
  Foreground `text.secondary` copy
  `"No applications observed in this window."` No spinner.

### `state: empty (filtered)` (filter input narrows to zero matches)

- Picker row unchanged; FilterInput shows the typed value.
- Summary strip continues to show the **unfiltered** totals — the
  summary describes the *window*, not the *visible grid*. (Filtered
  totals would mislead — "I typed 'chrome' and the totals went to
  zero because chrome isn't in the window.")
- DataGrid card body: centered `ui:TextBlock Style="text.body"`
  Foreground `text.secondary` copy `"No apps match \"{filter}\""`.
  The filter value is interpolated.

### `state: disconnected` (named-pipe down — `HistoryQueryClient.IsConnectionLost`)

- StatusBanner visible to the right of the title cluster:
  - Background `status.critical.background`, Foreground
    `status.critical`, `radius.control`, `padding=space.8`.
  - Copy: `"Service disconnected — last refresh stale."`
- Summary strip retains last-known values at `Opacity=0.6` (history
  preserved, NOT cleared — matches Dashboard's
  preserve-last-known treatment).
- DataGrid retains last-known rows at `Opacity=0.6`.

### `state: error` (any other query failure — NOT pipe-down)

- StatusBanner visible:
  - Background `status.caution.background`, Foreground
    `status.caution.text`, `radius.control`, `padding=space.8`.
  - Copy: `"Query failed ({ExceptionTypeName}): {ExceptionMessage}"`.
    The mock shows one realistic example, e.g.
    `"Query failed (SqliteException): database is locked"`.
- Summary strip retains last-known values at `Opacity=0.6`.
- DataGrid retains last-known rows at `Opacity=0.6`.

> **No `warming` state.** Per-App is a history-class surface — it
> queries SQLite via `HistoryQueryClient`, not the in-memory
> aggregate. There is no fill window to surface.

---

## 5. Tokens in scope

### `surface.*`

- Page root: `surface.background` (Mica shows through).
- DataGrid card: `surface.card` (opaque). Data-bearing card on Mica
  must be opaque.
- DataGrid alternating row: `surface.subtle.alt` (token rename — the
  current XAML uses the raw `SubtleFillColorTertiaryBrush` key).
- Summary strip background: `surface.subtle` (light decorative tint;
  understated so it doesn't compete with the DataGrid card). If the
  brand-dict resolves `surface.subtle` identically to `surface.background`
  on a given theme, the strip naturally reads as a borderless
  alignment of the three cells — that's the intended outcome.
- StatusBanner background: per-state, see `status.*` below.

### `text.*`

- Page title (`"Per-App"`): `Style="text.subtitle"` with
  `FontFamily="{StaticResource font.display}"`.
- Caption beneath title: `Style="text.caption"`, Foreground inherits
  `text.secondary`.
- Picker label (`"Window:"`): `Style="text.caption"`, Foreground
  inherits `text.secondary`.
- Refresh button text: default Wpf.Ui button typography.
- Filter input placeholder: default Wpf.Ui placeholder treatment
  (Foreground `text.tertiary`).
- Filter input typed value: default body, Foreground `text.primary`.
- Summary strip eyebrows: `Style="text.caption"`, Foreground
  inherits `text.secondary`. (Uppercase via the eyebrow rendering
  rule — apply `Typography.Capitals` or upper-cased copy.)
- Summary strip values: `Style="text.mono"`, Foreground
  `text.primary`.
- DataGrid column headers: `Style="text.body.strong"` (carried by
  `style.datagrid.compact.ColumnHeaderStyle`).
- DataGrid App / Publisher cells: `Style="text.body"`, Foreground
  `text.primary` (App) / `text.secondary` (Publisher).
- DataGrid Signature cell: `Style="text.body"`, conditional Foreground
  per signature value — see §8.
- DataGrid Up / Down cells: `Style="text.mono"`, right-aligned,
  Foreground `text.primary`.
- Hover chevron: Foreground `text.tertiary`.
- Empty-state copy ("No applications observed…",
  "No apps match \"{filter}\""): `Style="text.body"`, Foreground
  `text.secondary`.
- Loading caption ("Loading…"): `Style="text.caption"`, Foreground
  `text.secondary`.
- Em-dash placeholder values in loading summary: `Style="text.mono"`,
  Foreground `text.tertiary`.

### `accent.*`

- **No filled accent surfaces.** Per-App has no buttons, pills, or
  selection bars that carry an accent fill. The summary strip
  eyebrows are intentionally `text.secondary`, NOT the accent
  Foreground from `text.eyebrow` — keeping the strip understated.
  (Reminder: `accent.default` is NEVER a filled background; locked
  decision §8.)

### `status.*`

Paired bg/foreground per banner. See §4 for the per-state copy.

| State          | Background                   | Foreground             |
|----------------|------------------------------|------------------------|
| disconnected   | `status.critical.background` | `status.critical`      |
| error          | `status.caution.background`  | `status.caution.text`  |

Also used: `status.caution` as the **Foreground** for Signature cells
where `SignatureStatus ∈ {Unsigned, Invalid}` — see §8.

### `border.*`

- DataGrid card stroke: `border.card`, 1px.
- Summary strip: no outer stroke (sits on `surface.subtle`); inner
  cell-divider rules optional — if drawn, `border.subtle` 1px on the
  inter-column gaps.
- StatusBanner inherits no border (background alone carries the
  signal).
- Filter input: `border.control` (Wpf.Ui default text-box stroke).

### `space.*`

- Page outer margin: `space.24` (mandatory).
- Between row 0 (header+summary) and row 1 (picker): `space.12`.
- Between row 1 (picker) and row 2 (DataGrid card): `space.12`.
- Picker row inter-element gap: `space.12`.
- Refresh button icon → text gap: `space.4`.
- Summary strip inner padding: `space.12,space.8`; inter-cell gap
  `space.16`.
- StatusBanner inner padding: `space.8`.
- StatusBanner margin (left of title cluster): `space.16`.
- DataGrid card padding: 0 (rows carry their own padding via the
  compact style — `6,2`).

### `radius.*` (role tokens only)

- DataGrid card: `radius.card`.
- Summary strip: `radius.control`.
- StatusBanner: `radius.control`.
- Filter input: `radius.control` (Wpf.Ui default).
- App-cell tooltip popup: `radius.overlay`.

### `material / effect`

- Not used on Per-App. No brushed / metallic surfaces this round.
  (Dashboard introduced `metal.card` + `edge.light` + `shadow.card`
  on its status cards; Per-App's single DataGrid card stays flat
  `surface.card` until a later round explicitly decides to migrate.)

### New tokens required by this brief

**None.** Every token referenced above already exists in
`DesignTokens.xaml` and `colors_and_type.css` (verified against the
crosswalk in the CSS header). The rename of `AlternatingRowBackground`
from `SubtleFillColorTertiaryBrush` to `surface.subtle.alt` is a
pointer change, not a new token (per findings item 7).

---

## 6. Chart-chrome tokens

**Intentionally n/a.** Per-App has no chart — the entire data surface
is the DataGrid. Chart-chrome tokens land on Dashboard, App Detail,
and History.

---

## 7. Density assignment

- **`AppsGrid` DataGrid: `style.datagrid.compact`** (row 22, padding
  6,2, body font). This is the canonical compact-density use case —
  per `docs/design-system.md §8`, compact density exists for
  data-dense grids like this one. Currently uses default density (row
  ~28) which wastes vertical density for a page whose entire job is
  showing as many app rows as possible above the fold.
- No ListView on Per-App.

---

## 8. Locked decisions relevant to this screen

Carry these into every state spec; do NOT re-litigate any of them in
the mock.

### 8.1 Global locks

- **Loading = default Fluent `ProgressRing`**, centered in the
  DataGrid card body. Indeterminate; caption
  `Style="text.caption"` Foreground `text.secondary` copy `"Loading…"`
  rendered only after ~1 s. NO skeleton-shimmer anywhere — shimmer is
  continuous animation, pays no benchmark dividend under WPF, conflicts
  with the light-and-fast principle (design-system §2).
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HC activation. The mock does
  not draw HC variants; `HighContrast.xaml` collapses every semantic
  token onto `SystemColors.*` at runtime. Implementer verifies HC
  during the per-page verification gate (design-system §10).

### 8.2 Screen-specific locks

- **Window picker shorthand labels.** ComboBox items render
  `1h / 24h / 7d / 30d / 90d` inline; the long form
  (`"Last 1 hour"`, …) lives in a WPF `ToolTip` on each item AND on
  the ComboBox's selection display. Smaller picker, rhythm-consistent
  with the typography ladder.
- **Signature column conditional Foreground.** Implemented as a
  `DataGridTemplateColumn` with a `TextBlock` whose Foreground is
  driven by a value converter (or `DataTrigger`) keyed on
  `SignatureStatus`:

  | `SignatureStatus` | Foreground       |
  |-------------------|------------------|
  | `Signed`          | `text.primary`   |
  | `Unsigned`        | `status.caution` |
  | `Invalid`         | `status.caution` |
  | `Unchecked`       | `text.tertiary`  |

  Foreground-only treatment — NOT pills, NOT backgrounds.
  Conversion to `DataGridTemplateColumn` is the implementation cost;
  visually the value still reads as plain colored text. `SortMemberPath`
  stays `SignatureStatus` so column-click sort still groups by
  signature value (Signed / Unsigned / Invalid / Unchecked).
- **Hover chevron is a NEW canonical pattern.** Per-App establishes
  the canonical hover-drill affordance for grid rows — a trailing
  `ui:SymbolIcon Symbol="ChevronRight12"` Foreground `text.tertiary`,
  Visibility `Hidden` by default and flipped to `Visible` via the row's
  `IsMouseOver` trigger. No precedent in the app today. Future
  grid-based drill rows (App Detail's recent sessions, any future
  drill surfaces, History if it ever gains drill) should adopt the
  same pattern — see §16.
- **Summary strip is a NEW canonical pattern.** Per-App establishes
  the 3-cell horizontal summary-strip shape (`text.caption` eyebrow
  + `text.mono` value). History's current flat 2-line `SummaryLine`
  will adopt this shape in its own polish round (History findings
  items 2 & 12) — see §16. Summary describes the **unfiltered**
  window, not the filtered grid (see §4 `state: empty (filtered)`).
- **Filter is CLIENT-side.** `CollectionViewSource` wrapping the
  existing `Rows` `ObservableCollection`; ~150 ms debounce via
  `DispatcherTimer` on `TextChanged`; case-insensitive `Contains`
  predicate against `ImageName` and `PublisherDisplay`. NO contract
  change to `IZenVizorIpc.GetAppListAsync`. Bounded result sets
  (low hundreds at most over a 7d / 30d / 90d window) make
  client-side fine. Server-side filter is the deferred fallback only
  (F2 in §15), not a planned feature.
- **Path surfaces as TOOLTIP on the App cell, NOT a column.**
  `AppListEntry.ImagePath` is plumbed through `AppRowViewModel`
  (currently discarded) and bound as a `ToolTip` on the App cell's
  `TextBlock`. No new column — preserves grid width and reading
  rhythm. An explicit Path column remains out of scope.
- **Column-click sort is enabled today** (`DataGrid.CanUserSortColumns`
  defaults to `true`). App / Publisher / Signature sort correctly on
  their bound string values. Up / Down need `SortMemberPath="BytesUp"`
  / `"BytesDown"` to redirect sort to the raw numeric fields; the
  display binding stays on `UpText` / `DownText`. Implementation must
  add `BytesUp` and `BytesDown` (raw `long`) to `AppRowViewModel`
  alongside the existing `TotalBytes`.
- **Disconnected vs error are SPLIT** on Per-App — this is the
  canonical history-class default (template §4). `disconnected` paints
  `status.critical.background` + `status.critical`; `error` paints
  `status.caution.background` + `status.caution.text`. The dispatch
  uses `HistoryQueryClient.IsConnectionLost` in the catch.
- **No `warming` state** — Per-App queries SQLite, not the in-memory
  aggregate.

### 8.3 Boundary-case overrides of hard rules

**Intentionally n/a.** Per-App is the canonical drill surface and
fully aligns with every hard rule:

- §11 (discovery > ranking): list is uncapped server-side; client-side
  filter narrows by user-typed substring, never by score.
- §12 (honest attribution): one row = one PID = one app; no svchost
  service decoration this round because `AppListEntry` does not carry
  `HostedServices` (F4 in §15 — contract change deferred).
- §13 (passive-only): no kill / block / quarantine affordances; F5
  in §15 is a hard no.
- Template §4's disconnected/error split: Per-App takes the default
  split (not the merge); not an override.

---

## 9. Annotation rules

The hand-off contract: mockup annotations reference design tokens by
**semantic name**. The full rules are in
`docs/design-mockup-template.md §1`. The non-negotiables:

- **Canonical dotted token names ONLY.** `surface.card`,
  `text.secondary`, `status.caution`, `radius.card`, `space.16`. Never
  PascalCase. Never strip the dots.
- **NEVER use legacy CSS aliases** from
  `docs/design/colors_and_type.css` (`--fg1`, `--fill-card`,
  `--status-critical`, `--space-3`, etc.). Those aliases exist for
  back-compat on the mock side ONLY; they do not cross the hand-off
  boundary. The crosswalk in the CSS file header documents which alias
  maps to which dotted token.
- **`--fill-card` is the translucent variant.** Mapping it to the
  DataGrid card would migrate the wrong surface and fail WCAG AA.
  The DataGrid card on Per-App is `surface.card` (opaque) because
  it carries 100% of the page's data.
- **New tokens follow the pattern `<category>.<role>[.<modifier>]`**
  (dotted lowercase). The brief introduces NO new tokens (see §5).
  Any further token the mock needs MUST be named in the same pattern
  and listed in the hand-off notes.

---

## 10. Per-screen WPF translation gotchas

Per-screen reminders so the implementer doesn't trip on landmines the
design system already documents.

- **`ui:NavigationView` wraps each page in a `DynamicScrollViewer`** —
  hosted pages have infinite vertical extent. Per-App ALREADY enforces
  `AppsGrid.MaxHeight = Math.Max(200, window.ActualHeight - 220)` in
  `EnforceAppsGridBound`, wired on both `Loaded` and `SizeChanged`
  (`PerAppPage.xaml.cs:43–49`). DO NOT remove this. The polish round
  does not change the wiring; it changes only the contents of the
  grid. Without the MaxHeight cap, the DataGrid materializes every
  row instead of virtualizing.
  *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled`** on the Per-App nav-rail item —
  the `PerAppPage` instance survives nav-away/back, so `Loaded` does
  NOT refire on return (`MainWindow.xaml.cs:52`). The MaxHeight
  enforcement already hangs off `SizeChanged` so it works on revisit;
  any new re-measure logic added in polish must follow the same
  pattern (subscribe to `SizeChanged`, not `Loaded`).
- **`DataGrid` sort is on by default.** Adding `SortMemberPath="BytesUp"`
  / `"BytesDown"` on the byte columns redirects sort to the raw numeric
  fields without touching the display binding. The polish round adds
  `BytesUp` and `BytesDown` (`long`) to `AppRowViewModel` —
  currently absent (the record has `TotalBytes` but not the per-direction
  raw values).
- **`AppRowViewModel.ImagePath` does not exist today.** Adding the
  ImagePath tooltip (App-cell tooltip) requires plumbing `ImagePath`
  through `AppRowViewModel.From(AppListEntry e)` — `AppListEntry`
  already carries it (`Ipc.Contracts/Dto/AppListResult.cs:14`); the
  row VM currently discards it. Both `BytesUp` / `BytesDown` (for sort)
  and `ImagePath` (for tooltip) land in the same one-line record
  edit.
- **Signature column as `DataGridTemplateColumn`.** Convert from
  `DataGridTextColumn`. CellTemplate: a `TextBlock Style="text.body"`
  with `Foreground` driven by either (a) an
  `IValueConverter` keyed on `SignatureStatus` returning the
  per-status brush, or (b) `DataTrigger`s on the `TextBlock` style
  binding to `SignatureStatus`. Either pattern is fine; the
  converter is one class and one resource entry, triggers stay in
  XAML. Set `SortMemberPath="SignatureStatus"` on the column so
  header-click sort still works (template columns don't infer a sort
  path).
- **Hover chevron via `DataGridRow.Style`.** Add a 6th
  `DataGridTemplateColumn` of fixed width 24, CellTemplate containing
  `ui:SymbolIcon Symbol="ChevronRight12" Foreground="{DynamicResource
  text.tertiary}" Visibility="Hidden"`. In the DataGrid's
  `DataGridRow` style, add a `Trigger` on
  `Property="IsMouseOver" Value="True"` whose setter targets the icon
  via a `DataTrigger` or named-element resolution to flip Visibility
  to `Visible`. Drill behavior unchanged — the existing
  `MouseDoubleClick=OnRowDoubleClick` handler stays put. Mock
  implementation note: WPF's `DataGridRow.IsMouseOver` is reliable;
  the icon's visibility flip is the only behavior change.
- **Tooltip on the App cell.** The current `App` column is a
  `DataGridTextColumn`, which gives no direct surface for a per-cell
  `ToolTip`. Convert to `DataGridTemplateColumn` whose CellTemplate
  is `<TextBlock Text="{Binding ImageName}" TextTrimming="CharacterEllipsis"
  ToolTip="{Binding ImagePath}" />`. WPF's tooltip pops on hover
  with the standard ~1 s delay — no custom handler needed.
- **Filter debounce.** `DispatcherTimer` with `Interval =
  TimeSpan.FromMilliseconds(150)`, `Stop()` + `Start()` reset on each
  `TextChanged` event so only the trailing keystroke triggers the
  filter apply. The `Tick` handler stops the timer and calls
  `CollectionViewSource.GetDefaultView(Rows).Refresh()` (or
  reassigns the predicate). Filter predicate: case-insensitive
  `Contains` against `ImageName` and `PublisherDisplay`. Don't run
  the predicate on every keystroke.
- **`CollectionViewSource` over `Rows`.** Wrap the existing
  `ObservableCollection<AppRowViewModel> Rows` in a
  `CollectionViewSource` declared in the page's resources;
  `AppsGrid.ItemsSource = collectionViewSource.View`. The ICollectionView
  carries the live filter predicate. The view defaults to no sort and
  honors the DataGrid header-sort gesture naturally.
- **`Wpf.Ui.Controls.TextBox` placeholder text.** Wpf.Ui's `TextBox`
  has `PlaceholderText` baked in — prefer it over plain
  `System.Windows.Controls.TextBox` + a separate placeholder layer.
  If `ui:AutoSuggestBox` is in use elsewhere in the app, it's also
  acceptable; this brief specifies `ui:TextBox` because the filter
  predicate is local and doesn't need suggestion behavior.
- **Compact DataGrid style font.** `style.datagrid.compact` (per
  `design-system.md §8`) drops cell font to body size. The Up/Down
  columns still use `text.mono` overlaid on the compact style for
  digit alignment; specify both: `Style="{StaticResource text.mono}"`
  on the cell `TextBlock` within the column's `ElementStyle`. The
  mono Style sets `FontFamily` only — sizing comes from the compact
  DataGrid style.

---

## 11. Discovery > ranking constraint (HARD RULE)

Per-App is the **canonical drill-down surface** for the application.
The list is uncapped server-side — `AppListResult.Apps` returns every
app with non-zero bytes in the window, sorted by total bytes
descending. There is no top-N gate, no "see more" affordance, no
ellipsis truncation that hides rows.

The new client-side filter (item 17 in findings) coarsens by
**user-typed substring**, not by score. Filter narrows the visible
rows; it does NOT remove apps the user might want to see. A stealthy
malicious process won't be in any top-N — Per-App's whole job is
that drill surface where it can be found at all.

Dashboard's "Top 10" talkers list is the explicit, documented
boundary-case exception to this rule; Per-App is the drill surface
that pairs with it.

*Memory: `project_discovery_principle.md`.*

---

## 12. Honest attribution constraint (HARD RULE)

Don't visually imply precision we lack:

- **One row = one PID = one app.** Per-App rows have NO svchost
  service decoration in this round. `AppListEntry` does NOT carry
  `HostedServices` (verified — `Ipc.Contracts/Dto/AppListResult.cs:11–21`).
  Adding it requires a contract change → F4 in §15, deferred. The mock
  must NOT add a `[Service1, Service2]` bracket convention on Per-App
  rows this round — there is no data to fill it. (Dashboard's talkers
  rows DO carry `HostedServices` and surface the bracketed list; that
  is a different DTO.)
- **Up / Down columns report the PID's byte total over the window.**
  No per-process-instance breakdown, no per-co-hosted-service split.
- **Signature coloring (§8) is per-signing-state, NOT a fabricated
  trust score.** The Foreground change makes signing state visible at
  a glance; it does NOT claim anything beyond what
  `WinVerifyTrust` already returned. The ImagePath tooltip is the
  separate path-safety signal; the user combines the two glances
  themselves.
- **Traffic from DLL injection / LOLBins** (`rundll32`, `regsvr32`,
  `mshta`, `powershell`) attributes to the host process. The Per-App
  row for such a process reports the host's total bytes. This is a
  known documented boundary; the screen does not pretend otherwise —
  no asterisk, no caveat icon.

---

## 13. Passive-only constraint (HARD RULE)

ZenVizor observes; it never blocks. NO "Block" buttons, NO kill
icons, NO quarantine affordances, NO "Stop this app" right-click,
NO hover-revealed action buttons on Per-App rows. The hover chevron
(§3 column 6) is a **drill** affordance, not an action affordance —
it navigates to App Detail and that is its entire job.

This is a CLAUDE.md invariant. **Passive-only is non-overridable** —
there is no design or product circumstance under which Per-App may
add an active affordance. A Claude Design session must not quietly
add a "Block" button to a row.

---

## 14. Performance budget reminder

Idle CPU < 1%, working set < ~80 MB, no per-event DB writes. The
mock's design proposals must not regress this. Specifically for
Per-App:

- **No shimmer.** Loading uses static `ProgressRing`. No continuous
  animation anywhere on the page.
- **Filter debounce ~150 ms.** Don't run the filter predicate on every
  keystroke — `DispatcherTimer` collapses runs of TextChanged events
  into one filter apply (§10).
- **Client-side filter against a bounded result set.** Low hundreds
  of rows at most over any window; `CollectionViewSource.Refresh()`
  is comfortably under any noticeable threshold at that scale. If
  result sets ever blow past ~1000 rows for a single window, F2
  (server-side filter) becomes the right answer; not now.
- **DataGrid virtualization is preserved.** The existing
  `EnforceAppsGridBound` MaxHeight cap keeps the DataGrid in its
  virtualized mode (memory:
  `project_wpfui_navigationview_scrollviewer.md`). Adding the
  hover-chevron column and the signature-conditional-Foreground
  template column does not change virtualization behavior.
- **No new poll cadence.** Per-App refreshes on `Loaded`,
  ComboBox `SelectionChanged`, and Refresh button click. Same as
  today.
- **No new background work.** The filter is a foreground
  `ICollectionView` predicate; there is no new task, no new poller,
  no new pipe round-trip.

---

## 15. Out-of-scope — features flagged for later

Items the findings doc sorted into the **Feature** bucket (renumbered
in the findings doc after items F1, F2, F6 were reclassified into
polish). The mock must NOT design for them in this round. Listed here
so a later phase has the queue ready.

- **F1. Custom window picker (free-form date range).** Today is 5
  presets. Custom range = feature — Calendar / DatePicker primitives,
  validation, "start before end" guards.
- **F2. Server-side filter for `GetAppListAsync`.** Deferred
  fallback for item 17 (client-side filter) only. Requires a
  contract revision
  (`GetAppListAsync(QueryWindow window, string filter)`) which
  touches `ZenVizor.Ipc.Contracts`, the service implementation, and
  version-envelope handling. NOT planned — flagged only so a future
  iteration knows the contract-shaped escape hatch exists if
  client-side filter ever proves inadequate. Most likely never
  needed.
- **F3. Persist picker selection across launches.** Settings concern
  (Phase 6).
- **F4. svchost service decoration on Per-App rows.** `AppListEntry`
  does NOT carry `HostedServices`. Adding it requires a contract
  change → feature, not polish.
- **F5. Active-action affordances (kill / block buttons).** HARD NO
  per the passive-only invariant (§13). Not even in the feature
  backlog — out of scope for the product, period. Listed here only
  so the brief explicitly forbids them in a mock.

---

## 16. Chrome / cross-screen consequences

Per-App's polish introduces TWO new canonical patterns that propagate
to other screens. Both are flagged here so other briefs know what
NOT to redesign.

### 16.1 Summary strip pattern

- **What changed.** New 3-cell horizontal summary strip:
  `text.caption` eyebrow (uppercased, `text.secondary` Foreground) +
  `text.mono` value (`text.primary` Foreground). Cells separated by
  `space.16` column gap. Sits on `surface.subtle` with
  `padding=space.12,space.8` and `radius.control`.
- **Where it lives.** Implemented per-page for now (Per-App row 0,
  right cell). If a third screen adopts the same shape, factor into
  `Resources/Components/SummaryStrip.xaml` as a shared `UserControl`
  or `ResourceDictionary` template.
- **Propagates to.** History (which today has a flat 2-line
  `SummaryLine` — `"{count} buckets   |   Up: {bytes}   |   Down:
  {bytes}"`). History findings items 2 & 12 (`docs/design-briefs/findings/history.md`)
  already flag the restructure; the History brief will reuse this
  shape with its own labels (`BUCKETS / UP / DOWN` or similar — the
  History brief decides).
- **Per-screen brief work needed elsewhere.** History brief: replace
  its summary-line section with this strip shape and matching token
  set. App Detail and Dashboard already have their own summary
  treatments (App Detail's summary card, Dashboard's status-card row)
  — no rework on those.

### 16.2 Hover-drill chevron pattern

- **What changed.** Trailing `ui:SymbolIcon Symbol="ChevronRight12"`
  Foreground `text.tertiary` on grid rows; Visibility `Hidden` by
  default, flipped to `Visible` via the row's `IsMouseOver` trigger.
  Drill behavior (double-click) unchanged — chevron is a presentational
  affordance.
- **Where it lives.** Implemented per-page via `DataGridRow.Style`.
  If App Detail's recent-sessions grid adopts (or a future drill grid
  is added), factor into a shared `style.datagrid.row.drill` in
  `Resources/Styles.Grids.xaml`.
- **Propagates to.** App Detail's recent-sessions grid (if its polish
  round decides to surface a drill from a session to a connection
  detail). History: not today — History's chart bucket has no
  per-row drill. Dashboard: not applicable — Dashboard's talkers
  ListView has its own future drill (F2 in the Dashboard brief),
  which would adopt this pattern when that feature lands.
- **Per-screen brief work needed elsewhere.** None this round.
  Whoever opens the next polish brief that proposes row drill should
  reference this section for the canonical pattern.

### 16.3 No MainWindow chrome changes

Per-App's polish does not modify MainWindow's title bar, bottom bar,
or status bar. The bottom-bar rate mirror (introduced by Dashboard's
polish) is visible while Per-App is shown but is not Per-App's
concern. Draw it in the Per-App mock for visual completeness but
flag it `chrome: MainWindow, not PerAppPage`.

---

## 17. Deliverables expected from Claude Design

The mockup hand-back MUST contain:

- Layouts for every state in §4: `default`, `loading`, `empty`,
  `empty (filtered)`, `disconnected`, `error`. No `warming` state.
- The `default` state mock specifically must show:
  - One row in hover state with the trailing chevron visible.
  - One App cell with its ImagePath tooltip popped.
  - At least one row each of `Signed`, `Unsigned`, `Invalid`,
    `Unchecked` Signature so the conditional Foreground mapping is
    auditable in the mock.
  - The summary strip filled with realistic totals
    (`apps=47 / Up=312 MB / Down=2.4 GB`-ish).
- Token annotations using canonical dotted names per §9. Every box,
  border, text run, and tooltip element labeled with its tokens.
- Density tag on the AppsGrid (`density: compact`).
- Layout hints:
  - `AppsGrid.MaxHeight` enforced programmatically — annotation:
    `MaxHeight: window.ActualHeight − 220` (existing logic preserved).
  - `scroll: pane` inside the DataGrid card (the DataGrid's own
    scroller).
  - Page `scroll: none` — the page itself does not scroll.
- The `state: empty (filtered)` mock must show the FilterInput with a
  typed value AND the empty-grid copy interpolating it
  (`"No apps match \"chrome\""` or similar).
- The `state: disconnected` and `state: error` mocks each show the
  StatusBanner with its full per-state copy and the rows/summary at
  `Opacity=0.6`.
- The window picker drawn with both shorthand label AND long-form
  tooltip visible (one expanded ComboBox item showing the tooltip
  popping).
- Refresh button shown with icon + text (`ArrowSync24` + `"Refresh"`).
- Bottom-bar rate mirror drawn (MainWindow chrome) so the screen reads
  in context; flagged as MainWindow-owned (not Per-App).
- Hand-off notes confirming the brief introduces NO new tokens and
  one token rename (`AlternatingRowBackground` →
  `surface.subtle.alt`), which is a pointer change against the
  existing token surface.

---

## 18. Provisional / two-states

**Intentionally n/a.** Per-App is a Group A built screen.
`PerAppPage.xaml` exists, ships traffic data today, and the polish
round operates on the live XAML — there is no interim placeholder
treatment to design.

---

## 19. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as idiomatic
XAML against Wpf.Ui. Nothing in the mock is portable; the dotted-token
names are the contract.
