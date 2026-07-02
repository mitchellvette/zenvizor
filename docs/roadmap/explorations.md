# Explorations

Proactive ideas surfaced outside the shipped-epic-uncovers-followup loop —
usually while thinking about invariants, scope, or user-facing framing.

Not commitments. Not on the release sequence in [`README.md`](README.md).
Not [tracked-followups](tracked-followups.md) (those surfaced from real
epic work). Parked here so that when planning a future release, a
proactive-idea inventory exists alongside the reactive-followup inventory.

Each item names: (a) how it surfaced, (b) what it would do, (c) how it
fits ZenVizor's invariants, (d) rough shape / seam it slots into, (e)
what would need to be validated before it becomes an epic.

Items graduate out of this file when promoted to an epic (with a
letter/slot) or closed as won't-fit with a note.

---

## WFP / firewall-drop visibility

- **Surfaced in:** post-1.2.0 conversation about ZenVizor's semantic scope
  (what it does not see vs cannot see). Firewall-blocked outbound attempts
  today produce no `Microsoft-Windows-Kernel-Network` event, so ZenVizor
  is silent on drops — a real signal-quality gap for the security story.
- **What it would do:** consume `Microsoft-Windows-WFP` and/or
  `Microsoft-Windows-Firewall` ETW providers to surface filter-decision
  events (drops with rule identifiers). Enables statements like
  "Chrome tried to reach 1.2.3.4:443 and Windows Firewall blocked it."
- **Third-party AV note:** Defender publishes rich telemetry via
  `Microsoft-Antimalware-*` providers. Most third-party AVs that hook via
  NDIS filter drivers don't; that visibility is bounded and partial. Not
  a reason to skip the Windows Firewall side; just a scoping honesty
  note per invariant #5.
- **Invariant fit:** clean. Purely passive ETW consumption — no traffic
  emitted, no active probing. Same aggregate-in-memory / interval-flush
  model as the existing capture. Fits the `IMonitor` seam as a sibling
  to `EtwCaptureSource`.
- **Shape:** new `IMonitor` implementation (`WfpDropMonitor` or similar);
  new attribution path (drop events → app / rule / host tuple); a facet
  in app-detail view or a new alert producer. DB additions would be
  additive (new nullable columns or a new table).
- **What to validate before this becomes an epic:**
  1. Confirm which ETW providers actually surface consumer-relevant
     drops (WFP vs Windows Firewall vs both), and volume characteristics
     under normal use — a chatty firewall could push the per-event /
     flush cadence budget.
  2. Confirm attribution is trustworthy — a WFP drop event carries the
     originating PID / app id in most cases, but edge cases (system
     drops, early-boot drops before the PID is resolvable) may not.
     Attribution honesty (invariant #5) means we don't fabricate what
     isn't there; if PID is missing the row shows "unknown", not a guess.
  3. Decide the user story: is the value in the alert path (surface
     drops as a security signal — "app X tried to talk and was
     blocked"), the historical / detail path (show "attempted-but-
     blocked" alongside "successful" traffic per app), or both? The
     answer shapes the DB schema and the IPC surface.
