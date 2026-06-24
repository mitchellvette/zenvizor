# Epic A — History click-to-attribute

**Release:** 1.1.0 (minor) — shipped **complete** (Per-App windowing + popover).
**Status:** in-progress
**Build order (internal to this release):** the popover phase depends on the
windowing phase (the windowed Per-App view is the popover's deep-link target),
so build windowing first, then the popover.

---

## Problem

The History page has no actionable surface. A user who sees a traffic spike
cannot find out *what* caused it. Click anywhere on the spike → a popover of
the top talkers for that relative window, each with their individual
contribution, plus a remainder that deep-links into the windowed Per-App view.

## Findings (verified during planning)

- **Feasibility confirmed.** Per-app bucket-grain data exists at all three
  storage tiers: `traffic_samples` (60 s buckets, via the `process_sessions`
  join for `app_id`), `traffic_hourly`, `traffic_daily` (keyed by `app_id`
  directly). The popover is a constrained variant of the existing
  `GetAppListAsync(QueryWindow)` (`IZenVizorIpc.cs:53`).
- **Chart click → window mapping.** Use LiveCharts2 `ScalePixelsToData` (or
  the equivalent nearest-rendered-point API on the pinned version) to recover
  the `[from, to)` window under the cursor from a click-anywhere hit-test on
  the HistoryPage chart. Pixel→data is not used anywhere in the repo today
  and must be confirmed in a small spike before Phase 2 starts (see Risks).
- **Rendered-bucket width is variable.** HistoryPage renders **averaged rate
  per grain unit** — `ChartSeriesDownsampler.DownsampleAverage` caps Samples
  at 240 rendered buckets (1440 → 240 at 24 h ⇒ ~6 min each), then a
  secondary coalesce (factor 2 when >60 rendered buckets) widens hourly/daily
  bars (7 d hourly ⇒ ~2 h bars, 90 d daily ⇒ ~2 d bars). The popover must
  anchor its window on the **actual on-screen rendered span**, not the raw
  storage-bucket width, or a clicked bar won't tie to its own contents.
- **Discovery > ranking (preserved).** Top-5 + "+N more" is acceptable
  *because* the "+N more" remainder deep-links to the full windowed Per-App
  view — nothing is hidden; the top-5 is a surfacing convenience, not a rank
  cap. (See memory: `project_discovery_principle.md`.)

## Resolved design decisions

1. **No new IPC method — reuse `GetAppListAsync(QueryWindow)`.** Over a fixed
   window, rank-by-total-bytes ≡ rank-by-rate (span is constant), and
   `AppListResult` already returns the full ranked list. "Top-5 by rate" is a
   UI-side slice; "+N more" is `Apps.Count - 5`. Reusing the same call also
   guarantees the popover's top-5 order matches the Per-App view it
   deep-links into. Memory: `feedback_prefer_reuse_ipc.md`.
2. **Rate units in the popover match the chart's plotted point.** Per-app
   rows are labeled in the chart's per-grain unit (`/min` at Samples, `/hr`
   at Hourly, `/day` at Daily) so the top-5 + "+N more" totals reconcile to
   the value under the cursor. The Per-App detail page keeps canonical
   `bytes/s` (`RateFormatter`) — no change there.
3. **Ranking metric is combined up + down.** Matches the existing
   `GetAppListAsync` ordering and the Per-App view, so the popover and its
   deep-link target agree.
4. **Popover window width = exactly one rendered bucket.** That window ties
   the popover to the precise point under the cursor (sum of per-app rates ≡
   chart value at that point). HistoryPage already computes the
   downsample+coalesce factor for rendering; it surfaces it for the click
   handler to use.
5. **Click-anywhere on both chart shapes.** Line chart (Samples grain) and
   stacked-column charts (Hourly/Daily) both produce fixed-window targets;
   only the rendered-bucket span differs.
6. **PerAppPage shows a "Custom" combo entry** when navigated to with an
   arbitrary window. Picking any real preset removes the Custom entry. The
   tooltip on the Custom entry shows the concrete from–to range in local
   time.
7. **Top-5 is fixed**, not user-configurable.

## Build phases (both ship together in 1.1.0)

### Phase 1 — Per-App arbitrary windowing

**Goal:** make `PerAppPage` and `AppDetailPage` accept and display an
arbitrary, *fixed* `[from, to)` window — both as the deep-link target of
the popover **and** as a user-driven direct selection via a custom-range
flyout. Today they only support rolling presets (and a calendar-day
override on AppDetail).

**Tasks:**
1. **Window-state model** — introduce `WindowSelection` in
   `src/ZenVizor.Ui/Services/` as a single discriminated record (factories:
   `FromPreset`, `FromFixedWindow`) with a third "Custom range…" sentinel
   for triggering the flyout. Exposes `Short`/`Label`/`ToWindow()` so the
   existing `ComboBox` `ItemTemplate` binding continues to work unchanged.
   Designed as **reusable** — Epic I and H will want the same
   arbitrary-window abstraction.
2. **`CustomRangeFlyout` UserControl** — `src/ZenVizor.Ui/Views/Controls/`.
   `ui:CalendarDatePicker` for the dates (matches the existing chrome-row
   pattern at `AppDetailPage.xaml:126`) + three `ComboBox`es per row for
   time (hour 1-12 × minute 00/15/30/45 × AM/PM, locked to 15-min steps
   to keep the picker clean; sub-15-min precision is the popover's job).
   Span line + validation hint, Cancel/Apply buttons.
3. **PerAppPage** — rebind `WindowCombo.ItemsSource` to a mutable
   `ObservableCollection<WindowSelection>` seeded with the 5 presets + the
   sentinel pinned at the end. Sentinel pick reverts selection (re-entry-
   guarded) and opens the flyout; flyout Apply inserts/replaces a Custom
   entry at position 0 and selects it. Nav with `PerAppNavParams(QueryWindow)`
   takes the same insertion path. Picking a rolling preset retires any
   Custom entry.
4. **AppDetailPage** — same combo treatment. Extend `AppDetailNavParams`
   to carry an optional `QueryWindow` (additive; the existing
   `(AppId, Date?)` path keeps working for the Reports drill). The
   `_specificDate` chrome-row override still takes precedence over the
   combo selection in `RefreshAsync`.
5. **`PerAppNavParams`** — new positional record alongside
   `AppDetailNavParams`.

**No IPC, Storage, or schema change in Phase 1** — `GetAppListAsync(QueryWindow)`
already accepts arbitrary windows.

**QA gates:**
- *Storage determinism:* add a `GetAppList` test for a narrow ~6-minute
  Samples window, asserting exact rows (extends
  `AppHistoryQueryRepositoryTests`). Confirms the narrow-window path
  returns correct attribution.
- *Build + headless tests:* `dotnet build ZenVizor.slnx` and
  `dotnet test ZenVizor.slnx` green.
- *Manual:* open the WindowCombo on `PerAppPage` and `AppDetailPage` →
  pick "Custom range…" → flyout opens with sane defaults (To = now snapped
  to 15 min, From = To − 1 h) → adjust and Apply → list/chart refresh
  against the chosen window and a Custom entry appears at top of the
  combo; Cancel restores the previous selection unchanged. Re-open the
  flyout while a Custom entry is active → it pre-populates from that
  window. Picking a rolling preset clears the Custom entry. The Reports
  → AppDetail date drill still works unchanged.

### Phase 2 — History popover

**Goal:** click anywhere on the History chart → popover of the top-5 app
talkers for the rendered bucket under the cursor + "+N more" deep-link.

**Tasks:**
1. **Spike (blocking):** confirm LiveCharts2 pixel→data (or nearest-rendered-point)
   API on the pinned version. Fallbacks: hover-API nearest point, or a
   transparent overlay mapping pixels manually from axis min/max +
   `DrawMargin`.
2. **Click → window mapping:** map click-X to the rendered bucket under the
   cursor. `renderedSpan = storageBucketWidth × downsampleFactor × secondaryCoalesceFactor`.
   HistoryPage already knows these (computed during `ApplyResult`); surface
   them to the click handler.
3. **Popover UI:** reuse the InfoPopup `Popup` + `Border` pattern
   (`AppDetailPage.xaml:1339`): `AllowsTransparency`, `shadow.card`,
   `StaysOpen="False"`, scroll-dismiss. Anchored at the pointer.
4. **Query and slice:** call `GetAppListAsync(window)`, take top-5, render
   each row with per-grain rate labels (`/min` | `/hr` | `/day`). The "+N
   more" row appears whenever `Apps.Count > 5`.
5. **Drill behaviour:**
   - Talker row → `nav.Navigate(typeof(AppDetailPage), new AppDetailNavParams(appId, Date: null, Window: window))`.
     Canonical drill affordance: hover chevron + hand cursor + single click
     (memory: `feedback_drill_grid_pattern.md`).
   - "+N more" row → `nav.Navigate(typeof(PerAppPage), new PerAppNavParams(window))`.

**QA gates:**
- *Spike gate (blocking):* pixel→data / nearest-point API confirmed on the
  pinned version before further Phase 2 work begins.
- *Headless unit:* click-X → rendered-bucket-window math (px→ticks→bucket)
  with known axis params. **Reconciliation test:** the popover's
  per-grain-labeled rates over the rendered-bucket window sum to the
  chart's plotted value at that point (guards an accidental N× bug).
- *Integration (synthetic, real pipe):* `GetAppListAsync` over a narrow
  window returns expected top apps + count for "+N more"
  (`ZenVizor.Integration.Tests`).
- *Manual:* click a visible spike → popover top-5 reconciles to the
  plotted value; "+N more" → PerAppPage windowed full list; talker →
  AppDetail same window; dismiss on outside-click and on scroll; empty
  window (no traffic) and dense/coalesced-bar behaviour defined; popover
  anchors sensibly near the click. **Re-run the zero-own-traffic
  self-monitoring check** (Invariant #1) — confirms popover/IPC path
  emits no new egress.

## Cross-cutting

**Windowed-query generalization.** Both phases here, plus Epic I (and a
device-peer view under H), all want "query app/endpoint activity over an
arbitrary `[from, to)` window." The `WindowSelection` model from Phase 1 is
the shared display abstraction; the underlying `GetAppListAsync(QueryWindow)`
and `GetConnectionsAsync(int, QueryWindow)` are the shared query path.

## Risks & mitigations

1. **LiveCharts2 pixel→data API availability (HIGH).** No existing usage in
   the repo. → Spike first; fallbacks listed above.
2. **Rate reconciliation N× bug (MED).** Averaged-rate-per-grain is subtle.
   → Reconciliation unit test + manual visual tie-out at the gate.
3. **AppDetailNavParams refactor regressing the Reports drill (MED).** The
   `(AppId, Date?)` path is consumed by the Reports → AppDetail navigation.
   → Make the window param additive (trailing optional positional); keep
   the `Date` / `LocalDayWindow` path; regression-test the Reports drill at
   the manual gate.
4. **Click ambiguity on dense/coalesced bars and the 20 px line hover geometry
   (MED).** → Snap to nearest rendered bucket; define empty-region behaviour
   (no popover, or a "no traffic here" popover).
5. **`NavigationView` `DynamicScrollViewer` (MED).** The windowed Per-App
   grid needs programmatic `MaxHeight` (memory:
   `project_wpfui_navigationview_scrollviewer.md`). `PerAppPage` already
   applies this pattern in `EnforceAppsGridBound`; verify it still holds for
   the Custom-window path.

## Phase 1 status

**Shipped + verified** (Phase 1 only — popover phase still pending).
Verification record: [`../epic-a-phase-1-verification.md`](../epic-a-phase-1-verification.md).

## Follow-ups (in scope for this release)

- **App-wide sub-pixel text positioning sweep — ACTIVE.** Promoted from
  contingent now that the local fix verified at the Phase 1 manual gate.
  Symptom: small text (12 px `text.eyebrow` in particular) renders
  blurry when its container is positioned by a fractional `Margin`
  (typically `TransformToVisual` output) — glyphs straddle pixel
  boundaries. Local fix in Phase 1 used `UseLayoutRounding="True"` on
  the chrome `ContentControl` + `Math.Round` on the `Margin` in
  `PositionOverlay`. Precedent + rationale already documented at
  `MainWindow.xaml:252-263`.

  Sweep checklist:
  1. Grep for `TransformToVisual` and any code-behind that assigns
     `Margin` / `Padding` from computed `double`s — wrap with
     `Math.Round` and/or set `UseLayoutRounding="True"` on the receiving
     element.
  2. Grep for `RenderTransform` translating by non-integer amounts.
  3. Visual audit small text (`text.eyebrow`, `text.caption`,
     `text.mono` at 12-14 px) on every page in both Light and Dark
     themes, looking for blur.

  Recommended sequencing: do the sweep BEFORE Phase 2 starts so the
  positioning code Phase 2 introduces (popover anchor math) inherits the
  established discipline.

- **App-wide Wpf.Ui v4.0.2 ComboBox chrome styling pass — passive.**
  Phase 1 surfaced that Wpf.Ui's default ComboBox template reserves
  substantial internal padding for the chevron, clipping narrow content.
  Fixed locally for the custom-range time pickers via
  `Padding="6,2,2,2"` + wider widths. Other pages' combos (filter
  inputs, settings dropdowns, etc.) would benefit from a keyed
  `Style x:Key="combobox.compact"` overriding the template padding once.
  Not blocking 1.1.0; tracked here so it doesn't get lost.

## Version classification

**1.1.0 (minor).** New window-selector display state (`WindowSelection`
abstraction + Custom combo entry), new navigation contract
(`PerAppNavParams`, `AppDetailNavParams.Window`), and a new popover
surface. **No new IPC method, no `IpcSchemaVersion` bump** — the
arbitrary-window contract reuses `GetAppListAsync`. Shipped complete (both
phases), not fragmented across versions.
