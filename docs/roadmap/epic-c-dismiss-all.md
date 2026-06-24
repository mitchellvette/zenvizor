# Epic C — Dismiss All (visible-only)

**Release:** 1.2.0 (minor) · ships alone · **Status:** spec
**Depends on:** nothing (builds on the existing per-alert dismiss path)

---

## Summary

Add a header-row **Dismiss all** action to the Alerts page that dismisses
every alert *currently visible in the feed* — the filtered set, not the
entire active set. A user who has filtered to "Critical · UnsignedFromUserPath"
and wants to clear that view should not also silently dismiss the Info
alerts hidden by their filter.

## Current behavior (verified)

- Per-alert dismiss: `AlertsPage.OnDismissAlertClick`
  (`AlertsPage.xaml.cs:361`) flips the row optimistically via
  `AlertsViewModel.MarkAlertDismissed(id, whenMs)`, awaits
  `_alertsClient.DismissAlertAsync(alertId)`, and rolls back +
  banners on failure.
- IPC: `DismissAlertAsync(long alertId)` is idempotent — a no-op on an
  already-dismissed or missing alert (`IZenVizorIpc.cs:105`). Double-clicks
  cannot throw.
- The VM keeps two collections: `_allAlerts` (the full set matching the
  current **State** filter) and `Alerts` (the visible set after the
  client-side Severity + Type axes). `MarkAlertDismissed` / `RollbackDismiss`
  re-run the KPI + filter pipeline so the badge and feed stay in sync.

## Scope

**In:**
- A single header action that dismisses every alert in the visible `Alerts`
  collection that is still active.
- Optimistic UI + idempotent per-id IPC loop, reusing the existing
  `MarkAlertDismissed` / `DismissAlertAsync` / `RollbackDismiss` machinery.

**Out:**
- A server-side bulk-dismiss IPC method. Each dismiss stays a per-id call;
  the loop lives client-side. The existing idempotent contract makes partial
  retries safe, so a batched IPC buys nothing for v1.
- Undo. (Possible later nicety; not v1.)

## Design

- **Visible-only snapshot.** Capture the list of active `AlertVm`s from the
  *filtered* `Alerts` collection at click time. Alerts hidden by the
  Severity/Type axes — and any alert that arrives via `OnServiceAlertRaised`
  *after* the click — are intentionally untouched. "What you see is what you
  dismiss."
- **Where it shows.** Visible only when `Content == Populated` and
  `SelectedState == Active`. Dismissing in the `Dismissed`/`All` views is
  either a no-op or confusing; lock the action to the Active view. Label
  carries the count, e.g. `Dismiss all (N)`. (No em-dash in the rendered
  label — see UI copy convention.)
- **Mechanism.** For each snapshot id: `MarkAlertDismissed` (optimistic) →
  `UpdateNavBadge` → `await DismissAlertAsync(id)`. On a per-id failure,
  `RollbackDismiss(id)` that one and surface the error in the inline banner;
  continue the rest. Idempotency means a retry of the whole action is safe.
- **Confirmation.** Per-item dismiss is deliberately one-click with no
  confirm (brief §3.5 lock). Bulk dismiss of N at once is higher-consequence
  and not one-click-reversible (no undo), so this spec recommends a
  lightweight confirm ("Dismiss all N alerts?"). **Open decision** below.

## Invariant guards

- No core invariant is touched: UI + existing IPC only, no DB access from
  the UI (invariant 3 preserved — dismiss flows over `IZenVizorIpc`).
- Reuses `DismissAlertAsync`; no new IPC method. The *new control* (the
  button) is what makes this a minor rather than a patch.

## Open decisions

1. **Confirm dialog?** Recommend yes for the bulk action (distinct from the
   no-confirm per-item lock), since there is no undo.
2. **Offer in Dismissed/All views?** Recommend no — Active-only keeps the
   action meaningful and non-destructive.

## Acceptance criteria

- With a Severity filter active (e.g. Info hidden), Dismiss all clears the
  visible feed and leaves the hidden alerts active (verify via State=All).
- Nav badge drops to reflect remaining *active* alerts (including any hidden
  by filter), not zero.
- An alert pushed mid-operation is not dismissed.
- Double-invoking the action does not throw (idempotent IPC).
- A simulated per-id IPC failure rolls back exactly that row and banners,
  leaving a coherent feed.

## Version classification

**1.2.0 (minor).** Adds a new user-facing control. No IPC/DB/contract change.
