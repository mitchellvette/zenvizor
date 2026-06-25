# ZenVizor Post-MVP Roadmap

Active execution view for post-1.0.0 work. This operationalizes the
architectural roadmap in [`../zenvizor-prd.md`](../zenvizor-prd.md) §10
(which remains the source of truth for *why* each module fits the three
seams) and follows the SemVer rules in
[`../versioning.md`](../versioning.md). Each release maps to a **whole
epic** (or a small bundle of epics); each epic has its own spec in this
directory.

The original MVP build used a single phased sprint plan because its
phases had hard sequential dependencies (capture -> attribution ->
storage -> IPC -> UI). This roadmap is different: it is an **ordered
sequence of capability-expanding releases**, each shipped **complete**
and validated step-by-step by the current 1.0.0 user before the next
begins. The order is driven by user-facing priority (the
quality-of-life / UX pain points first), not by inter-epic dependency —
the one real dependency is noted below and is already satisfied by the
sequence.

**Status legend:** `proposed` (shape agreed, not yet fully spec'd) ·
`spec` (spec written, ready to build) · `in-progress` · `shipped` ·
`spike` (needs a feasibility spike before it can be planned).

---

## SemVer discipline for this roadmap

Per `../versioning.md`:

- **`1.x.0` (minor) — new capability.** A new control, a new IPC
  method, a new view, a new table or trailing nullable column. **Every
  release in this roadmap is a minor**, because every epic here
  *expands* what the app can do — it adds user-facing surface.
- **`1.0.x` (patch) — corrections with no new surface.** Reserved for
  true bug-fix releases (a regression, a corrected attribution edge
  case) once there is a real user base to ship fast, low-risk relief
  to. **This roadmap does not use the patch lane** — nothing here is
  *broken*; the work is capability expansion, so it all lands as
  minors.
- **`2.0.0` — breaks a user-visible contract** (IPC/DB/config/CLI).
  None of the items below are expected to require this.

**Epics ship complete, not fragmented.** Three epics have a
"correction" half and a "new-surface" half (B, E, G). An earlier draft
split those across a patch and a later minor; that fragmentation is
**removed**. Each epic is built whole — its correction phase and its
new-surface phase together — and ships as a single minor, so a release
is one coherent, independently-validatable unit. (The patch lane stays
available if a future need arises to ship a correction *ahead* of its
feature half; this roadmap simply doesn't take it.)

---

## Release sequence

Releases are **sequential and validated one at a time** by the current
1.0.0 user. The order is the commitment; the version advances one minor
per release.

| # | Release | Epic(s) | Content | Status |
|---|---------|---------|---------|--------|
| 1 | 1.1.0 | A | **History click-to-attribute** (complete). Per-App windowing (window selector + arbitrary-window display state) **and** the click-anywhere History popover: top-5 talkers (combined up+down, labeled in the chart's per-grain rate unit) + "+N more" remainder that deep-links into the windowed Per-App view. Reuses `GetAppListAsync` — no new IPC method, no schema bump. | shipped |
| 2 | 1.2.0 | C | **Dismiss All** (visible-only) on the Alerts page: a header action that dismisses the *filtered* set, not the entire active set. Reuses the existing per-id idempotent dismiss IPC. | proposed |
| 3 | 1.3.0 | E | **Dashboard gap + live window** (complete). Gap-break fix (keep feeding the buffer off-page; draw an explicit break across a real absence instead of a straight line) **and** the live-window dropdown (2 m / 10 m / 1 h) with DB back-fill via `GetTrafficHistoryAsync`. | proposed |
| 4 | 1.4.0 | B + D | **Alert noise gating** (complete: ~48 h install-baseline window + running-process setup-scan seed + per-severity toast toggles; Critical keeps firing throughout) **and** **KPI reverse-filter** (click a severity tile to isolate it + Clear chip). | proposed |
| 5 | 1.5.0 | F + G + I | **Reveal in Explorer** (app-detail path action) + **Known-process distinction** (complete: catalog-aware signature verification via `CryptCATAdmin*` + curated common-items annotation) + **Endpoint-centric lookup** ("which apps talked to this IP / host?"). | proposed |
| — | 1.x.0 | H | **Passive network devices** — populate the reserved `devices` table via passive mDNS / SSDP / NetBIOS observation. **Spike-gated**; ships standalone if/when the feasibility spike succeeds. | spike |

**Order rationale.** A, C, E are the current user's primary
quality-of-life pain points (in that order); B and D are the
quick-follow-up cluster; F / G / I are the remainder; H is parked behind
a spike. The one real cross-epic dependency — Endpoint lookup (I) reuses
the arbitrary-window query path that A stands up — is satisfied because
A ships first.

**Prerequisite for the whole cadence:** finish the tag-triggered CI
Release pipeline (already tracked in PRD §10.1). Without it every
release above is a manual chore.

---

## Epic detail index

Listed in build / release order. Each epic ships as one complete
release (see the sequence above).

- **A — History click-to-attribute** → 1.1.0:
  [`epic-a-history-click-to-attribute.md`](epic-a-history-click-to-attribute.md)
- **C — Dismiss All** → 1.2.0:
  [`epic-c-dismiss-all.md`](epic-c-dismiss-all.md)
- **E — Dashboard gap + live window** → 1.3.0:
  [`epic-e-dashboard-gap-live-window.md`](epic-e-dashboard-gap-live-window.md)
- **B — Alert noise + gating** → 1.4.0 (with D):
  [`epic-b-alert-noise-gating.md`](epic-b-alert-noise-gating.md)
- **D — KPI reverse-filter** → 1.4.0 (with B):
  [`epic-d-kpi-reverse-filter.md`](epic-d-kpi-reverse-filter.md)
- **F — App-detail path actions** → 1.5.0 (with G, I):
  [`epic-f-app-detail-path-actions.md`](epic-f-app-detail-path-actions.md)
- **G — Known-process distinction** → 1.5.0 (with F, I):
  [`epic-g-known-process-distinction.md`](epic-g-known-process-distinction.md)
- **I — Endpoint-centric lookup** → 1.5.0 (with F, G):
  [`epic-i-endpoint-centric-lookup.md`](epic-i-endpoint-centric-lookup.md)
- **H — Passive network devices** (spike first) → standalone 1.x.0:
  [`epic-h-passive-network-devices.md`](epic-h-passive-network-devices.md)

---

## Cross-cutting concerns

- **Three distinct trust concepts, kept separate** (do not conflate —
  conflation is how a security tool ends up vouching for an impostor):
  1. **Catalog-signed** — real cryptographic verification of the actual
     file (the G catalog-verification phase). The only one that asserts
     authenticity.
  2. **Baseline-known** — "was already on this machine when ZenVizor
     installed" (the B baseline phase). Per-machine; suppresses the
     *new-app* signal only.
  3. **Common-items** — name/path-keyed *context*, inherently spoofable
     (the G common-items phase). Never asserts trust; flags mismatches
     as caution. **Absence from the list is not suspicion.**

- **Windowed-query generalization** — the History popover (A), Endpoint
  lookup (I), and a device-peer view under H all want "query
  app/endpoint activity over an arbitrary `[from, to)` window." Design
  the arbitrary-window path **once** in A (the Per-App windowing built
  first within that epic) and reuse it.

- **Invariant #1 guardrails** — back-fill (E) must use the history IPC,
  never the memory-only snapshot path. Devices (H) must *observe*
  already-arriving traffic via existing capture; never open a listening
  socket, join a multicast group, send probes, or do reverse-DNS.
  Device names come only from passively-seen mDNS / DNS / NetBIOS.

---

## Pending decisions

- **H** — green-lit for a feasibility spike (does the existing ETW
  capture expose inbound mDNS / SSDP / NetBIOS payloads without us
  emitting anything?). Spike result determines whether H is a real
  feature or shelves to ARP-only.

_Resolved:_ the earlier "B patch-framing" question — ship gating as a
bare patch vs. hold for the toggles — is moot now that B ships complete
as a single minor (1.4.0).
