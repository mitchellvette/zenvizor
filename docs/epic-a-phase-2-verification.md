# Epic A — Phase 2 verification (History click-to-attribute popover)

**Release:** 1.1.0 (Epic A complete — both phases shipped)
**Companion docs:** [`roadmap/epic-a-history-click-to-attribute.md`](roadmap/epic-a-history-click-to-attribute.md) (spec), [`epic-a-phase-1-verification.md`](epic-a-phase-1-verification.md) (Phase 1 record), [`epic-a-phase-2-gate-0.md`](epic-a-phase-2-gate-0.md) (LiveCharts2 pixel→data spike), [`versioning.md`](versioning.md)
**Status:** All headless gates green; manual gate verified by the human; cross-cutting rendering-discipline fixes also landed in this slice.

---

## What Phase 2 delivers

Click anywhere on the History chart → popover of the top 5 app talkers for the rendered bucket under the cursor (or its 6-minute centered widening on the 1h preset), with per-grain rate labels that reconcile to the chart's plotted value. Talker rows drill into AppDetail with the same window; "+N more" drills into Per-App with the same window — both via the navigation contracts shipped in Phase 1.

The slice also rolled up three orthogonal improvements that surfaced during the work and were folded in rather than deferred: an app-wide WPF rendering-discipline sweep (replacing the originally-scoped sub-pixel positioning sweep), a chrome theme-staleness fix, and a window-range disclosure caption on Per-App + App Detail.

## Components shipped

### Phase 2 — click-to-attribute popover

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/Services/ChartClickResolver.cs` (new) | `TryResolveClick` (WPF/LiveCharts wrapper — `ScalePixelsToData`, series walk, axis `UnitWidth` read) + pure-math helpers (`TryFindContainingBucketIndex` extent-containment, `ComputePopoverWindow` with 6-min floor + click-centered clamp). `ResolvedClick` record carries the popover window, visual anchor bucket, span, and grain. `BytesPerGrainUnit` divisor lives here as the single source of truth so rate formatting is testable independent of HistoryPage. |
| `src/ZenVizor.Ui/Views/HistoryPage.xaml` | `PopoverOverlay` in-page overlay (Grid + ContentControl chrome). `Background="Transparent"` so backdrop is hit-testable; chrome built in code-behind. |
| `src/ZenVizor.Ui/Views/HistoryPage.xaml.cs` | Click handler with `_popoverRequestSeq` dedupe; chrome builder (canonical metal recipe, header time range, top-5 + "+N more"); positioning at the visual bucket-center pixel via `ScaleDataToPixels` + `Math.Round`; paired MouseDown+MouseUp backdrop dismiss; dismiss-and-respawn on cross-chart click; drill nav via `AppDetailNavParams(appId, Date: null, Window)` + `PerAppNavParams(window)`; per-grain rate formatter delegates to `ResolvedClick.BytesPerGrainUnit`. |
| `tests/ZenVizor.Integration.Tests/ChartClickResolverTests.cs` (new) | 22 pure-math tests: bucket-extent containment (incl. sparse gaps, past-midpoint, edge ticks), popover window widening + clamping (each preset), rate divisor reconciliation (per-preset + sum-linearity + chart-mean-reconciliation 6× guard). |

### Cross-cutting — WPF rendering-discipline sweep

Originally scoped as a sub-pixel-positioning sweep extrapolating the Phase 1 `Math.Round(Margin)` fix; investigation surfaced the broader root cause (WPF defaults of `TextOptions.TextFormattingMode=Ideal` + no `UseLayoutRounding` on Page roots) which produces pervasive small-text blur regardless of `TransformToVisual`.

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/MainWindow.xaml` | `UseLayoutRounding="True"` + `TextOptions.TextFormattingMode="Display"` + `TextRenderingMode="ClearType"` + `TextHintingMode="Fixed"` on the FluentWindow root. Comment block documents the WHY (non-obvious WPF defaults; orthogonal layout-vs-text fixes; chart-axis Skia exception). |
| `src/ZenVizor.Ui/Views/*.xaml` (7 pages) | `UseLayoutRounding="True"` on each Page root. Required because Wpf.Ui's NavigationView Frame chain breaks WPF property inheritance to hosted pages — the MainWindow-level setting alone doesn't reach them. |
| `src/ZenVizor.Ui/Views/Controls/CustomRangeFlyout.xaml` | Same on the UserControl root. |
| `src/ZenVizor.Ui/Views/DashboardPage.xaml` (additionally) | Kept the explicit `UseLayoutRounding="True"` on the status-cards row Grid as belt-and-suspenders + local documentation of the original symptom (the diagnostic site that revealed the broader root cause). |
| `src/ZenVizor.Ui/Views/ReportsPage.xaml(.cs)` | Sub-pixel positioning fix on `PeakLabel` overlay — `Math.Round(peakX/peakY/labelW)` at the source + `UseLayoutRounding="True"` on PeakLabel. Same canonical fix as Phase 1's PerAppPage/AppDetailPage flyout chrome. |

### Cross-cutting — chrome theme staleness (`SetResourceReference`)

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/Views/PerAppPage.xaml.cs` `BuildFlyoutChrome` | Replaced static `FindResource("surface.card"/...)` with `SetResourceReference` for theme-flippable properties (Background / BorderBrush / Effect). Fixes the dark-mode chrome staleness when the page was constructed in light theme and viewed in dark (or vice versa). |
| `src/ZenVizor.Ui/Views/AppDetailPage.xaml.cs` `BuildFlyoutChrome` | Same fix. |
| `src/ZenVizor.Ui/Views/HistoryPage.xaml.cs` `BuildPopoverChromeContent` + row builders | Same fix. Although the popover chrome is rebuilt per-show (so it's theme-fresh in practice), the references defend against a future refactor that caches it and also handle the theme-flip-while-popover-open edge case. |

### Cross-cutting — window range caption + hover affordance

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/Services/WindowSelection.cs` | New `FormatRangeShort(QueryWindow)` — date + time, no prefix, no span suffix — for chrome captions next to a picker already labeled "Custom." Distinct from `FormatRange` (combo tooltip) and HistoryPage's popover header. |
| `src/ZenVizor.Ui/Views/PerAppPage.xaml(.cs)` | `WindowRangeCaption` TextBlock in `Grid.Column="3"` (the existing `*` filler between Refresh and Filter inputs); `UpdateWindowRangeCaption` toggles Visible/Collapsed on combo selection. Caption sits in pre-existing layout space → no shift on toggle. |
| `src/ZenVizor.Ui/Views/AppDetailPage.xaml(.cs)` | `WindowRangeCaption` in a reserved `RowDefinition Height="Auto" MinHeight="17"` below the picker cluster (Row 1 of the chrome row's 2-row Grid). Uses Hidden ↔ Visible (not Collapsed) so the reserved row's MinHeight stays load-bearing. `UpdateWindowRangeCaption` also gates on `_specificDate`: when a specific day is set, the caption hides because the date picker IS the disclosure for what's being queried. |

### Cross-cutting — Settings Alert NumberBox chrome + clamp

| File | Purpose |
|------|---------|
| `src/ZenVizor.Ui/Views/SettingsPage.xaml` | Three Alert threshold NumberBoxes (LargeDownload, OutboundHeavy, UnusualDailyVolume): `ClearButtonEnabled="False"` removes the destructive X chrome (NumberBox with Min=1 can't legitimately be empty); Width/MinWidth=140 (Max=1024 fields) or =120 (Max=10.0) so 4-digit input renders without clipping; `ValidationMode="Disabled"` lets out-of-range typed values reach the handler. |
| `src/ZenVizor.Ui/Views/SettingsPage.xaml.cs` `OnAlertThresholdValueChanged` | Commit-time `Math.Clamp(value, Minimum, Maximum)` against the NumberBox's own range; re-sets Value if out of range using the existing `_suppressApply` flag to break recursion. Typing 4444 → field snaps to 1024 + debounced apply sends 1024 to the service. Replaces the prior revert-to-last-valid behaviour. |

## Headless gates

```
dotnet build ZenVizor.slnx -c Debug
# 0 warnings, 0 errors

dotnet test  ZenVizor.slnx -c Debug --no-build
# Storage      141 (unchanged from Phase 1)
# Core         248 (unchanged)
# Attribution   69 (unchanged)
# IPC           61 (unchanged)
# Integration   83 (was 61 — +22 ChartClickResolverTests)
# Total        602 passing, 0 failed, 0 skipped
```

CI matches.

## Manual gates (run + passed)

### Phase 2 — popover, drills, edge probes

- **Click on 1h-preset chart peak** → popover anchored at bucket-center pixel; header reads 6-min range centered on the click; top-5 rows reconcile to chart values at that point.
- **Click in flat / zero-traffic section** → silent no-op (tolerance gate).
- **Click in left Y-axis label band, bottom X-axis label band, top legend strip** → silent no-op (extent-containment rejects).
- **24h preset click on peak** → popover with 6-min range (matches Samples bucket).
- **7d preset, click ON a bar / in the GAP between bars** → bar click opens popover with 2-hour range; gap click silent no-op.
- **90d preset, click on a bar** → popover with `Jun 23 – Jun 24` day-format range.
- **Backdrop click outside popover** → dismisses.
- **Click on a different chart bucket while popover is open** → old popover dismisses, new popover appears for the new bucket (no double-click needed).
- **Talker row click** → navigates to AppDetail with the popover's window; chrome combo shows "Custom"; range caption disclosure visible beneath picker (verified post-tweak).
- **"+N more" row click** → navigates to Per-App with the popover's window; full ranked list visible; caption visible after Refresh button.
- **Navigate away from History (Dashboard) then back, repeat click** → popover works after navigation round-trip.
- **Change WindowCombo while popover open** → popover dismisses cleanly, chart refreshes.
- **Popover text crisp on Light AND Dark** (eyebrow + body + mono rate) — inherits rendering-discipline trio from Page root.
- **Invariant #1 — zero own network traffic** — self-monitoring check across the popover flow confirmed clean.

### Cross-cutting — rendering-discipline sweep

- Walked every page in Light AND Dark theme; small text surfaces (Dashboard KPI eyebrows + line descriptions, History KPI eyebrows, Reports Top apps app-name + path + publisher rows, App Detail "Up this Window" eyebrows + app path, Uncommon Talkers cards, etc.) — all now render crisp.
- Dashboard KPI cards 1-4 all crisp (the diagnostic site — pre-fix, cards 2/3/4 were blurry due to fractional column origin × DropShadowEffect compositor pass).

### Cross-cutting — chrome theme staleness

- Open Per-App custom-range flyout in Light → switch to Dark → re-open: chrome correctly matches Dark.
- Reverse direction: same.
- Theme switch with flyout open: chrome background/border/shadow re-paint live (not just inner content).
- App Detail and History popovers also tracked theme switches correctly.

### Cross-cutting — caption + hover affordance

- Per-App: caption appears in the `*` filler column after Refresh when a Custom window is active; hides on preset switch; Refresh button does NOT move.
- App Detail: caption appears in reserved Row 1 beneath picker cluster; hides on preset switch OR when a specific date is set; DatePicker/ClearButton/ComboBox do NOT shift horizontally on toggle.
- History popover rows: hover shows subtle `surface.subtle` tint + right-pointing chevron (talker rows) / arrow (+N more) fading in on the right edge.
- Deep-link path: from History → "+N more" → arrives on Per-App with caption visible; talker → AppDetail same.

### Cross-cutting — Settings Alert NumberBox

- Click into LargeDownload / OutboundHeavy / UnusualDailyVolume → no X clear button appears; spin buttons remain.
- Type a 4-digit value (e.g. 9999) into LargeDownload (Max 1024) → on commit (Tab / focus loss / spin click) the field snaps to 1024; the service persists 1024 not 9999.
- Type a value below Min (e.g. 0) → snaps to Min on commit.
- Default cases (2-3 digit values within range) commit unchanged.
- Visual: all three fields are wide enough to render their full Max value without clipping.

## Decisions surfaced during implementation

- **Popover window widens on 1h preset (6-min floor).** Single 1-min chart bucket is too narrow for reliable attribution (a quiet 30s in a noisy 5-minute talker's run would mis-attribute). `MinPopoverWindowMs = 6 min` widens click-centered to span 6 chart buckets; matches natural granularity of the 24h preset (also 6-min buckets). Pure-math reconciliation test guards the divisor against an accidental N× bug.
- **Popover anchor is bucket-center, not click position.** Popover represents the BUCKET; click is incidental. Anchor stays stable across multiple clicks within the same bar.
- **Popover window clamps to visible chart bounds.** Discloses a slice of what the user can see, even if storage has data beyond the chart edge — the honest framing.
- **Rendering-discipline trio at every tree root, not just MainWindow.** Wpf.Ui's NavigationView Frame chain breaks WPF property inheritance to hosted Pages. Per-root explicit set is the canonical fix. Memory: `project_wpf_text_options_root.md`.
- **`SetResourceReference` over `FindResource` for theme-flippable chrome properties** built in code-behind. Static `FindResource` snapshots the resource at construction; `SetResourceReference` behaves like XAML `DynamicResource` and tracks runtime theme switches. The chrome being built ONCE in page ctor (vs rebuilt per show, like the popover) is what surfaces the symptom — chrome from one theme rendered in another. Same fix preventatively applied to the History popover chrome.
- **Caption placement is per-page.** Per-App slots into existing `*` filler column (zero layout change). App Detail uses a reserved-space row (one-time +17px chrome row height; zero shift on caption toggle). Different layouts → different non-disruptive strategies.
- **App Detail caption uses Hidden, not Collapsed.** Reserved-space row's `MinHeight="17"` keeps the row's height stable; toggling to Collapsed would let the row shrink to 0 and re-expand, defeating the purpose.
- **App Detail caption hides when `_specificDate` is set.** Date picker IS the disclosure for that mode; caption would compete.

## Follow-ups now in scope but deferred

The following are tracked in the epic spec's §Follow-ups section. None are blocking 1.1.0; each is documented separately for the next slice.

- **Chart axis label rendering (SkiaSharp pipeline) — PASSIVE.** LC2 renders axis labels via SkiaSharp's own glyph rasterizer, not WPF, so they don't inherit the rendering-discipline trio. Symptom: chart X/Y axis labels on History + AppDetail charts remain soft. Fix: `ChartTheming.cs` / `SKPaint` tuning (subpixel text, hinting level, typeface choice).
- **HWND-owning popup text rendering — PASSIVE.** WPF `Tooltip`, `ContextMenu`, `ComboBox` dropdowns live in their own HWND and don't inherit MainWindow attached properties. Fix path: implicit App-level styles (e.g. `<Style TargetType="ToolTip">` with the same setters).
- **App-wide Wpf.Ui v4.0.2 ComboBox chrome styling pass — passive** (carried forward from Phase 1; unchanged).
- **Wpf.Ui v4.0.2 NumberBox chrome + clamp pass — PASSIVE.** Phase 2 fixed the three Alert threshold NumberBoxes; the five Retention NumberBoxes share the same X-clear footgun and revert-vs-clamp behaviour and warrant the same fix in a follow-up slice.

## What 1.1.0 ships

Epic A in its entirety:

- Phase 1 — Per-App arbitrary windowing + custom-range flyout (shipped in 632b3f0).
- Phase 2 — History click-to-attribute popover (this slice).
- Cross-cutting — WPF rendering-discipline trio at every tree root + theme-staleness fix + chrome row caption disclosure + History popover hover affordance.

No IPC schema bump (popover reuses `GetAppListAsync(QueryWindow)`); no storage migration; no new IPC methods. New navigation contracts (`PerAppNavParams`, `AppDetailNavParams.Window`) were additive in Phase 1.
