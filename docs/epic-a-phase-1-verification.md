# Epic A — Phase 1 verification (Per-App arbitrary windowing)

**Release:** 1.1.0 (in-progress — Phase 1 complete, Phase 2 [popover] not yet built)
**Companion docs:** [`roadmap/epic-a-history-click-to-attribute.md`](roadmap/epic-a-history-click-to-attribute.md) (spec), [`versioning.md`](versioning.md) (SemVer policy)
**Status:** Phase 1 verified at the manual gate; Phase 2 deferred per the spec's "ships together in 1.1.0" plan.

---

## What Phase 1 delivers

The receiving + selecting side of arbitrary windows on `PerAppPage` and `AppDetailPage`. Both pages now accept any fixed `[from, to)` `QueryWindow` (either via deep-link navigation or via a user-driven custom-range flyout) in addition to the existing rolling presets (1h / 24h / 7d / 30d / 90d) and the AppDetail chrome-row date override. Phase 2 (the History chart click-to-attribute popover) will produce arbitrary windows for the deep-link path; until it lands, the user-driven flyout is the only entry point.

**No IPC, Storage, or schema change.** `GetAppListAsync(QueryWindow)` already accepted arbitrary windows; Phase 1 is purely UI.

## Components shipped

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/Services/WindowSelection.cs` | Discriminated record: `FromPreset(WindowPreset)` (rolling, recomputes against wall-clock), `FromFixedWindow(QueryWindow)` (absolute), `CustomSentinel` (combo entry that triggers the flyout). Reusable for Epic I + H. |
| `src/ZenVizor.Ui/Views/Controls/CustomRangeFlyout.xaml(.cs)` | The flyout UserControl — two `ui:CalendarDatePicker` rows with F1 fallback date-label overlays + hour/minute/AM-PM `ComboBox` triples at 15-min granularity + live span/validation line + Cancel/Apply. |
| `src/ZenVizor.Ui/Views/PerAppPage.xaml(.cs)` | Combo rebound to `ObservableCollection<WindowSelection>`; in-page overlay anchored top-left under combo; backdrop dismiss with MouseDown+MouseUp pairing; grid-handler gate while overlay visible; `PerAppNavParams(QueryWindow)` record at bottom of file. |
| `src/ZenVizor.Ui/Views/AppDetailPage.xaml(.cs)` | Same combo treatment; overlay anchored top-right; `AppDetailNavParams` extended to carry optional `QueryWindow` (additive — Reports → AppDetail date drill source-compatible). |
| `tests/ZenVizor.Storage.Tests/AppHistoryQueryRepositoryTests.cs` | New test `GetAppList_NarrowSamplesWindow_AggregatesOnlyOverlappingBuckets` — popover-style ~6-minute window asserting only overlapping buckets sum and a lurker app with traffic ±10 min outside the window is excluded. |

## Headless gates

```
dotnet build ZenVizor.slnx -c Debug
# 0 warnings, 0 errors

dotnet test  ZenVizor.slnx -c Debug --no-build
# Storage      141 (was 140 — narrow-window test added)
# Core         248
# Attribution  69
# IPC          61
# Integration  61
# Total        580 passing, 0 failed, 0 skipped
```

CI matches.

## Manual gates (run + passed)

Per `feedback_verification_doc_granularity.md` these are at phase granularity — one walk-through covering every UI lever now that all Phase 1 controls are in place.

### Per-App + App Detail flyout interaction

- **Open the flyout** — `WindowCombo` → "Custom range…" → flyout opens anchored under the combo (top-left on PerApp, top-right on AppDetail). Anchor holds on window resize.
- **Default values** — From = (now − 1 h) snapped down to 15 min; To = now snapped down to 15 min; span line reads e.g. "1 h".
- **Date picker** — clicking either date picker opens its calendar popup; clicking a date selects it and the F1 fallback label updates; the closed picker shows "Jun 24 2026"-style text (not an empty outline).
- **Time pickers** — hour (1–12) / minute (00/15/30/45) / AM/PM dropdowns render their values fully readable (no second-digit / second-letter clipping).
- **Validation** — From ≥ To shows "From must be earlier than To." with Apply disabled; To in future shows "To can't be in the future." with Apply disabled; otherwise span line reads the computed duration and Apply is enabled.
- **Apply** — list/chart refreshes against the chosen window; a "Custom" entry appears at the top of the combo and is selected; tooltip shows the local-time from–to range.
- **Re-open with Custom active** — flyout pre-populates from the active fixed window.
- **Cancel** — overlay dismisses; prior combo selection unchanged.

### Backdrop dismiss + click isolation

- **Outside-click cancel** — full mouse-down + mouse-up sequence on the backdrop dismisses the overlay (= Cancel). Equivalent to Cancel button.
- **Calendar date pick outside chrome bounds** — the date picker's calendar popup overflows the chrome shape. Clicking a date in the overflowing region selects the date; modal stays open. (The mouse-down landed on the calendar popup, not the backdrop; the down+up pairing in `OnBackdropMouseDown`/`OnBackdropMouseUp` skips dismissal when the pair isn't matched.)
- **Drag from backdrop into chrome** — mouse-down on backdrop, drag onto the chrome, mouse-up on chrome → modal stays open (`MouseLeave` clears the flag).
- **Click on backdrop over an AppsGrid row** — modal dismisses; the AppsGrid does NOT navigate to AppDetail. (Backdrop's preview events consume both down + up; grid handler also gates on overlay visibility as belt-and-suspenders.)

### Combo-driven flow

- **Sentinel handling** — picking "Custom range…" reverts combo to the prior selection (no spurious refresh) before opening the flyout.
- **Preset retires Custom** — selecting a rolling preset (1h / 24h / etc.) removes any Custom entry from the combo and refreshes against the preset.

### Existing-path regression

- **Reports → AppDetail date drill** — `ReportsPage.xaml.cs:1004` still passes `new AppDetailNavParams(appId, date)`; AppDetail receives the date, populates the chrome-row date picker, and queries that local day's window. Unchanged.

## Decisions surfaced during implementation

- **No new IPC method.** Rank-by-bytes ≡ rank-by-rate over a fixed window, and `AppListResult` already returns the full ranked list. The deep-link target query path is the existing `GetAppListAsync(QueryWindow)`. Memory: `feedback_prefer_reuse_ipc.md`.
- **Same-assembly UserControl XAML metadata gap.** `<controls:CustomRangeFlyout/>` from sibling page XAML fails (`MC3074`) because the `_wpftmp.csproj` doesn't stub `UserControl` types the way it does `Page`/`Window`. Host via `ContentControl.Content` assigned in code-behind instead. Memory: `project_wpf_usercontrol_same_assembly.md`.
- **In-page overlay over `<Popup>`.** `<Popup>` with `AllowsTransparency=True` lives in its own HWND that flies outside the window on smaller sizes and produces popup-on-popup click-through. In-page overlay (Grid + backdrop + chrome) stays within window bounds and avoids the z-order issue. Now codified as the project default for new popover UI — see CLAUDE.md and `docs/design-system.md` §9.
- **Backdrop dismiss requires MouseDown AND MouseUp on backdrop.** Tried two simpler variants first (dismiss-on-MouseDown, dismiss-on-MouseUp-with-IsCalendarOpen-debounce). Both leaked dismissals from the calendar-popup-close → MouseUp-on-backdrop sequence. Pairing the events is the natural fix: a "click" is the unit, partial events don't count.
- **Sub-pixel text positioning blurs small text.** The chrome's `Margin` from `TransformToVisual` was fractional; 12px eyebrow text inherited the sub-pixel offset and rendered blurry. Fix combined `UseLayoutRounding="True"` on the chrome ContentControl + `Math.Round` on the Margin in `PositionOverlay`. Same root cause + same fix as the alerts-badge digit centering at `MainWindow.xaml:270`.

## Follow-ups now active

- **App-wide sub-pixel text positioning sweep.** Promoted from contingent to active now that the local fix landed and verified at the manual gate. Tracked in `docs/roadmap/epic-a-history-click-to-attribute.md` under §Follow-ups; do the sweep before Phase 2 begins or before 1.1.0 ships, whichever is more natural sequentially.
- **App-wide Wpf.Ui v4.0.2 ComboBox chrome styling pass.** Time pickers here got a local `Padding="6,2,2,2"` + wider widths to compensate for Wpf.Ui's default ComboBox template reserving substantial chevron-side padding. A keyed `Style x:Key="combobox.compact"` would let other pages reuse the fix. Tracked in the same Follow-ups section.

## What Phase 2 will use from Phase 1

- `PerAppNavParams(QueryWindow)` for the "+N more" deep-link target
- `AppDetailNavParams(int, DateOnly?, QueryWindow?)` for the talker-row deep-link target
- The `WindowSelection.FromFixedWindow` + Custom-entry handling for displaying the popover-clicked window after deep-link
- The narrow-window storage path tested via `GetAppList_NarrowSamplesWindow_AggregatesOnlyOverlappingBuckets`
