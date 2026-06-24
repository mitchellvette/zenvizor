# Epic E — Dashboard gap + live window

**Release:** 1.3.0 (minor) — shipped **complete** (gap-fix + live-window dropdown)
**Status:** stub (shape agreed; the gap-fix phase is ready to spec in full)
**Build order (internal to this release):** the live-window phase builds on the
gap-fix correction, so do the gap-fix first.

---

## Problem

The Dashboard live-rates chart draws a misleading straight line across periods
when no data was added (e.g. while the user was on another page), implying
continuous traffic that did not happen. Separately, the live window is fixed
and short; users want to widen it (2 m / 10 m / 1 h).

## Current behavior (verified)

- `DashboardPage` holds `_upSeries` / `_downSeries`
  (`ObservableCollection<DateTimePoint>`), fed by `OnActivitySnapshot`
  (`DashboardPage.xaml.cs:309`) → `ApplyUpdate` (`:377`), and capped as a ring
  buffer at `ChartHistoryPoints` (`:448-451`).
- The `ActivitySnapshotPoller` is owned by **MainWindow** (`:177`), so the
  snapshot stream keeps flowing even when the Dashboard page is unloaded — but
  the page **unsubscribes** `ActivitySnapshotReceived` on `Unloaded`
  (`:305`), so the series stops being fed while off-page. On return,
  LiveCharts connects the last pre-departure point to the first post-return
  point: a straight line across the absence.
- **Null-valued `DateTimePoint`s are already tolerated** — the peak
  calculation guards `p.Value.HasValue` (`:464`). A null-valued point renders
  as a line break.

## Phase 1 — the gap fix

Two complementary mechanisms; **no new control, no new IPC**:

1. **Keep feeding the buffer while off-page** — subscribe at MainWindow scope
   (or otherwise retain points) so a brief page switch doesn't drop the
   series.
2. **Explicit gap break** — insert a null-valued `DateTimePoint` to break the
   line across a genuine absence (service down / long gap) instead of drawing
   a straight line.

**Open decision:** keep-feeding vs. gap-break vs. both. Recommend *both* —
keep buffering while merely off-page (continuity), and gap-break on a real
absence (honesty about no-data).

## Phase 2 — live-window dropdown

- Window dropdown: **2 m / 10 m / 1 h**. The current buffer is ~60 points
  (`ChartHistoryPoints`); the wider windows need DB back-fill.
- **Invariant guardrail (load-bearing):** back-fill uses
  `GetTrafficHistoryAsync` (the history IPC), **never**
  `GetCurrentActivitySnapshotAsync` (memory-only; MUST NOT read SQLite per the
  IPC contract). Mixing the snapshot path into back-fill would violate the
  "Observe must not read disk on the snapshot path" guard.

## Version classification

**1.3.0 (minor).** The gap-fix phase is a correction on its own (a misleading
straight line across an absence), but it ships **bundled** with the live-window
phase — a new window-selector control + back-fill path — so the release as a
whole adds surface and is a minor. The two phases ship together, not split
across a patch and a later minor.
