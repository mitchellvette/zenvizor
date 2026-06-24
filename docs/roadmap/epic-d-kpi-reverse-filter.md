# Epic D — KPI reverse-filter (click-to-include + Clear chip)

**Release:** 1.4.0 (minor) · bundled with Epic B · **Status:** spec
**Depends on:** nothing

---

## Summary

Resolve the overlap between the Alerts KPI tiles and the severity filter:
make the severity KPI tiles *act* as a one-click filter. Clicking the
**Critical** tile isolates Critical alerts (include that severity, switch the
others off); a **Clear** chip restores all severities. The **State** axis
(Active/Dismissed/All) stays completely separate — the ACTIVE tile is not a
filter.

## Current behavior (verified)

- KPI surface: `ActiveCount`, `CriticalCount`, `WarningCount`, `InfoCount`
  derived in `AlertsViewModel.RebuildKpiCounts` from the non-dismissed set.
- Severity filter: three client-side bools — `SeverityCriticalEnabled`,
  `SeverityWarningEnabled`, `SeverityInfoEnabled`. Each setter calls
  `ApplyFilter` (`AlertsViewModel.cs:95-114`).
- Type filter: `EnabledTypes` set. State filter: `SelectedState`
  (server-applied; re-queries via `RefreshAsync`).
- Existing **Reset** link binds `IsFilterNotAtDefault` and resets *all* axes
  (`OnResetFilterClick`, `AlertsPage.xaml.cs:411`).
- The tiles are currently pure readouts; they and the severity toggles can
  visually disagree (the overlap the user flagged).

## Scope

**In:**
- Make the three **severity** KPI tiles clickable to reverse-filter to that
  severity only.
- Add a **Clear** chip (progressive disclosure) that restores all
  severities, shown only while a severity filter is active.

**Out:**
- Making the ACTIVE tile a filter. It maps to the State axis; turning it into
  a filter collides with `SelectedState == Active` (axis collision warned
  about previously). Keep it an inert readout for v1.
- Type-axis tiles (none exist).

## Design

- **Click-to-isolate, click-again-to-clear.** `OnKpiTileClick(severity)`:
  if the filter is currently "only this severity," restore all three
  (toggle off); otherwise set only this severity on. Drives the existing
  `SeverityCriticalEnabled` / `Warning` / `Info` setters so `ApplyFilter`
  runs unchanged.
- **Clear chip.** Bind visibility to a new VM property
  `IsSeverityFilterActive` (= not all three enabled). Click sets all three
  true. Lives next to the tiles, not in the filter bar, so it reads as "clear
  the tile selection."
- **Two distinct reset affordances — document the difference.** The new
  Clear chip is a *severity-axis-only* reset. The existing Reset link is a
  *full* reset (State + Severity + Type). They are not redundant; the Clear
  chip is the lightweight per-axis escape, the Reset link is the global one.
- **Visual sync.** Whatever control mirrors the severity toggles today
  (filter-bar checkboxes/chips) must stay in sync with tile-driven changes —
  both read the same VM bools, so `ApplyFilter` + `OnPropertyChanged` already
  cover it; verify the filter-bar visuals update when a tile is clicked.

## Invariant guards

- UI-only; reuses the existing client-side filter pipeline. No IPC/DB change.

## Open decisions

1. **ACTIVE tile:** inert readout (recommended) vs. clickable → `State=Active`.
   Recommend inert to avoid axis collision.
2. **Re-click behavior:** toggle-clears (recommended) vs. no-op.
3. **Clear chip placement / label** ("Clear" vs "Show all severities").

## Acceptance criteria

- Clicking the Critical tile shows only Critical alerts and reveals the Clear
  chip; the filter-bar severity controls reflect the same state.
- Clear restores all three severities and hides the chip.
- The State filter (Active/Dismissed/All) is unaffected by tile clicks.
- The existing Reset link still resets every axis.

## Version classification

**1.4.0 (minor).** New interactive surface on existing tiles + a new chip
control. No contract change. Bundled with Epic B in 1.4.0.
