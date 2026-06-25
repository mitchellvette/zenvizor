# Epic A — Phase 2 Gate 0 result (LiveCharts2 pixel→data spike)

**Release:** 1.1.0 (in-progress — Phase 1 shipped, Phase 2 unblocked by this gate)
**Companion docs:** [`roadmap/epic-a-history-click-to-attribute.md`](roadmap/epic-a-history-click-to-attribute.md) (spec — Phase 2 §Tasks, Risks #1), [`epic-a-phase-1-verification.md`](epic-a-phase-1-verification.md)
**Status:** Gate 0 passed. Phase 2 implementation cleared to start.

---

## What this gate confirmed

Phase 2's blocking risk (spec §Risks #1) was whether the pinned
`LiveChartsCore.SkiaSharpView.WPF 2.0.4` exposes a usable pixel→data API
on `CartesianChart` — no prior usage in the repo. A throwaway click
handler on `HistoryChart` (removed before this doc was written) logged
every click to `%TEMP%\zenvizor-gate0.log` across all five window
presets and both chart shapes. Findings:

### 1. `CartesianChart.ScalePixelsToData(LvcPointD)` exists and works

Signature on 2.0.4:

```csharp
LvcPointD ScalePixelsToData(LvcPointD point, int xAxisIndex = 0, int yAxisIndex = 0);
```

Returns a `LvcPointD` whose `X` is the data-space coordinate on the
first X axis. **`X` is `DateTime.Ticks` as a `double`** — verified by
casting `(long)data.X` and reconstructing `new DateTime(ticks)`, which
matched the click target visually. This is consistent with the axis
labeler's `(long)ticks` cast in `HistoryPage.UpdateAxesForGrain`.

Sample probes:

| Preset / grain | Click px | Resolved data.X (ticks) | Reconstructed DateTime |
|----------------|---------:|------------------------:|------------------------|
| 1h Samples (line) | (391, 49) | 639179142182208640 | 2026-06-24 16:10:18 |
| 24h Samples (line) | (717, 59) | 639178994628220800 | 2026-06-24 12:04:22 |
| 7d Hourly+2× (bars) | (139, 204) | 639176767641717760 | 2026-06-21 22:12:44 |
| 90d Daily+2× (bars) | (730, 216) | 639178369398772992 | 2026-06-23 18:42:19 |

Works identically on both `LineSeries` (Samples) and stacked
`ColumnSeries` (Hourly/Daily).

### 2. `Axis.UnitWidth` is the rendered-bucket span — no surfacing needed

This was an unexpected win and **simplifies Phase 2's "Click → window
mapping" task** (spec §Phase 2 Tasks #2). The spec said HistoryPage
would need to surface its `storageBucketWidth × downsampleFactor ×
secondaryCoalesceFactor` to the click handler. That's not necessary —
`_xAxis.UnitWidth` already carries the same value in ticks:

| Preset | Stored grain | Downsample | Coalesce | UnitWidth (ticks) | UnitWidth (decoded) |
|--------|--------------|-----------:|---------:|------------------:|---------------------|
| 1h     | Samples (1 min)  | none | none | 6.0 × 10⁸     | 1 minute            |
| 24h    | Samples (1 min)  | 1440→240 (~6 min) | none | 3.6 × 10⁹ | 6 minutes      |
| 7d     | Hourly (1 hr)    | none | 2× | 7.2 × 10¹⁰         | 2 hours             |
| 90d    | Daily (1 day)    | none | 2× | 1.728 × 10¹²       | 2 days              |

`ChartBuilder.UnitWidthFor(grain, preset)` already computes these and
assigns them to `_xAxis.UnitWidth` in `UpdateAxesForGrain`. The click
handler reads them back directly. **The popover window is exactly
`[snappedBucketStart, snappedBucketStart + _xAxis.UnitWidth)`.**

### 3. Series values are `DateTimePoint` — snap-to-nearest is trivial

Series enumeration (one-shot, first click of the session) confirmed:

```
[series 0] name='Up'   type=LineSeries`1 count=61 firstX=2026-06-24T15:48:00 lastX=2026-06-24T16:48:00
[series 1] name='Down' type=LineSeries`1 count=61 firstX=2026-06-24T15:48:00 lastX=2026-06-24T16:48:00
```

`HistoryChart.Series[i].Values` enumerates `DateTimePoint` instances
whose `DateTime` is the rendered bucket start. Maximum point count is
240 (24h Samples cap from `ChartSeriesDownsampler.MaxBuckets`), so an
O(n) linear scan for the nearest `|DateTime.Ticks - clickTicks|` is
trivially cheap on every click.

This is the **snap strategy** for Phase 2: walk one series (Up or Down
— same X axis, same buckets), find the nearest point by tick distance,
use its `DateTime` as `bucketStart`, query window =
`[bucketStart, bucketStart + UnitWidth)`.

### 4. Edge-case behaviour is tolerable

`ScalePixelsToData` never threw across any probe, including clicks
deliberately landed:

- **In the left Y-axis label band** (px X=52 with `DrawMargin.Left=80`):
  resolved data.X extrapolated to a tick value *before* the visible
  window start (e.g. `2026-06-23 14:55` when the 24h window started
  ~`2026-06-23 16:49`).
- **Below the X-axis label band** (px Y=292 on a 306-tall chart):
  resolved data.Y came back negative (-70919167.34) — extrapolated
  below the visible Y range.

Both extrapolate linearly rather than failing. The popover handles
them without explicit out-of-bounds detection: snap-to-nearest with a
**half-`UnitWidth` tolerance gate** naturally rejects them — a click
that resolved to a tick more than `UnitWidth/2` away from every series
point lies in a gap or outside the rendered data, so no popover is
shown (or a "no traffic here" popover, per spec §Phase 2 Manual gate).

`Axis.MinLimit` / `MaxLimit` come back `null` (axis is in auto-range
mode against data extents) — irrelevant for the click handler, since
snap-to-nearest works from series values, not from axis limits.

## Implications for Phase 2 implementation

The spec's §Phase 2 Tasks #1–#5 stand. Two specific refinements from
this gate:

- **Task #2 ("Click → window mapping"):** simpler than written. Use
  `ScalePixelsToData(LvcPointD)` to get the click tick, walk one series'
  `DateTimePoint` values to snap to the nearest rendered bucket, take
  the bucket span from `_xAxis.UnitWidth`. No need to surface
  HistoryPage's internal downsample/coalesce factor — the axis already
  encodes the rendered span.
- **Task #1 ("Spike"):** complete. Fallbacks listed in the spec (hover
  API, manual pixel-mapping overlay) are not needed.

The click handler itself is the natural place for the half-UnitWidth
tolerance gate that rejects clicks in axis-label bands, legend strip,
and inter-bar gaps — no separate DrawMargin geometry checks required.

## Spike artefacts

- **Scratch code:** `OnChartClickSpike_Gate0` + `SpikeLog` + log path
  field in `src/ZenVizor.Ui/Views/HistoryPage.xaml.cs`. **Removed** in
  the same commit that adds this doc.
- **Log file:** `%TEMP%\zenvizor-gate0.log` — manual probe artefacts
  captured 2026-06-24 16:49 across all five presets and one full set of
  edge probes. Not checked in.
