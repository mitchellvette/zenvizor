# Epic A — History click-to-attribute

**Release:** 1.1.0 (minor) — shipped **complete** (Per-App windowing + popover)
**Status:** stub (feasibility confirmed; deeper UX planning pending)
**Build order (internal to this release):** the popover phase depends on the
windowing phase (the windowed Per-App view is the popover's deep-link target),
so build windowing first, then the popover.

> Stub: captures the investigation findings so they survive the session.
> Full UX planning (popover interaction, hit-test, deep-link contract) is a
> Claude.ai hand-off.

---

## Problem

The History page has no actionable surface. A user who sees a traffic spike
cannot find out *what* caused it. Click anywhere on the spike → a popover of
the top talkers for that relative window, each with their individual
contribution, plus a remainder that deep-links into the windowed Per-App view.

## Findings (verified during investigation)

- **Feasibility confirmed.** Per-app bucket-grain data exists at all three
  storage tiers: `traffic_samples` (60 s buckets, via the `process_sessions`
  join for `app_id`), `traffic_hourly`, `traffic_daily` (keyed by `app_id`
  directly). The popover is a constrained variant of the existing
  `GetAppListAsync(QueryWindow)` ("apps ranked by total bytes over the
  window", `IZenVizorIpc.cs:53`).
- **Chart click → window mapping.** Use LiveCharts2 `ScalePixelsToData` to
  recover the `[from, to)` window under the cursor from a click-anywhere
  hit-test on the HistoryPage chart.
- **Unit reconciliation (gotcha).** HistoryPage renders **averaged rate per
  grain unit** — `ChartSeriesDownsampler.DownsampleAverage` (1440 → 240
  buckets, averages not sums; rendered buckets ≠ storage buckets). The
  popover's talker numbers must be computed/labeled consistently with that
  averaged-rate convention or they will read N× off the chart.
- **Discovery over ranking (preserved).** Top-5 + "+N more" is acceptable
  *because* the "+N more" remainder deep-links to the full windowed Per-App
  view — nothing is hidden; the top-5 is a surfacing convenience, not a rank
  cap. (See memory: discovery > ranking.)

## Build phases (both ship together in 1.1.0)

- **Phase 1 — Per-App windowing.** Window selector + arbitrary-window
  display state on the Per-App view, which becomes the deep-link target. IPC
  already supports it (`GetAppListAsync(QueryWindow)`); this is UI work on
  `PerAppPage` / `AppDetailPage` (both already carry window plumbing). Build
  this first — it is the popover's deep-link target and stands up the shared
  arbitrary-window query path.
- **Phase 2 — the popover.** Click-anywhere region hit-test → top-5
  talkers (rate-primary, both up/down) + "+N more" remainder that deep-links
  into the windowed Per-App view. **New IPC method** (or an extension of
  `GetAppListAsync`) for rate-primary ordering over an arbitrary window plus
  the remainder count.

## Cross-cutting

**Windowed-query generalization.** Both phases here, plus Epic I (and a
device-peer view under H), all want "query app/endpoint activity over an
arbitrary `[from, to)` window." Design the arbitrary-window path **once** in
the windowing phase and reuse it.

## Open questions (for the planning hand-off)

- New IPC method vs. extend `GetAppListAsync` (rate-primary order + remainder
  metadata)?
- Popover anchoring to the click point; behavior on a dense/coalesced bar.
- Rate units in the popover vs. the averaged-rate chart Y.

## Version classification

**1.1.0 (minor).** New window-selector control + new IPC method + new popover
surface. Shipped complete (both phases), not fragmented across versions.
