# Epic I — Endpoint-centric lookup

**Release:** 1.5.0 (minor) · bundled with Epics F + G · **Status:** stub
**Depends on:** shares the windowed-query machinery with Epic A (the
arbitrary-window path is built in A's windowing phase and reused here; Epic A
ships first as 1.1.0, so this dependency is satisfied by the sequence)

---

## Intent

Answer the reverse question to the existing per-app drill: **"which apps talked
to this IP / host?"** over a window.

## Current behavior (verified)

- The app → endpoints direction already exists:
  `AppHistoryQueryRepository` (`:309-321`) groups the `connections` table
  `BY protocol, remote_addr, remote_port` for a given app + window, served via
  `GetConnectionsAsync(appId, window)` (`IZenVizorIpc.cs:59`).
- The `connections` table carries `remote_addr`, `remote_port`,
  `remote_class`, `resolved_host` and joins to `apps` through
  `process_sessions`.

## Design shape

- **New repo query:** the inverse aggregation — given a `remote_addr` (or
  `resolved_host`) + window, join `connections → process_sessions → apps` and
  return the apps that talked to that endpoint.
- **New IPC method:** e.g. `GetEndpointTalkersAsync(endpoint, window)`.
- **Reuse the arbitrary-window path** built in Epic A's windowing phase
  (cross-cutting windowed-query generalization).
- **Discovery over ranking:** list *all* apps for the endpoint over the
  window; coarsen by time if the result is large, never cap by rank.

## Open questions

- Endpoint key: IP vs. resolved host vs. both (the table has `remote_addr` and
  `resolved_host`).
- Entry point UX: a search box, and/or click-through from a connection row in
  the per-app Connections grid.

## Version classification

**1.5.0 (minor):** new IPC method + new view. No contract break. Bundled with
Epics F + G in 1.5.0.
