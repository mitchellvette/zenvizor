# Pre-brief — Alerts (spec-derived, Group B)

Placeholder screen today. Derived from `docs/zenvizor-prd.md` §6, §7.6,
§9 + `docs/zenvizor-sprint-plan.md` Phase 6 + the authoritative alert
catalog at `docs/alerts-catalog.md`.

This findings doc names **what the page must communicate** and **why
each thing matters**. It avoids prescribing **how** the design should
compose those things, except where a constraint is load-bearing (a
locked token contract, a known WPF trap, a contract with the catalog).
Composition, control choice, severity rendering, dismiss affordance,
filter affordance, and card geometry are open design problems for the
brief.

---

## 1. Purpose & IA placement

- **Purpose:** local feed of raised observations, with a dismiss
  flow. PRD §6 calls Alerts "seam #2" — the alert pipeline is one
  of the three structural seams designed in from the start. The
  vocabulary of alert types (current and roadmap), severity
  assignments, source labels, and why-copy lives in
  `docs/alerts-catalog.md`; **the design must render that
  vocabulary, not invent its own.**
- **IA placement:** fifth item in the left nav rail.
  `Symbol="Alert24"`. `NavigationCacheMode.Enabled`. Today routed
  to `AlertsPage : PlaceholderPage` with subtitle "Generic alert
  feed (seam #2) — Phase 6."

## 2. Requirements lifted from the spec

### 2.1 Alert entity (PRD §7.6)

The persistent record the feed shows:

| Field | Type | Notes |
|---|---|---|
| `alert_id` | INTEGER PK | |
| `type` | TEXT | Catalog identifier (`UnsignedFromUserPath` is the MVP type) |
| `severity` | TEXT | `Info` \| `Warning` \| `Critical` |
| `created_at` | INTEGER | Unix ms |
| `source_monitor` | TEXT | Internal identifier. Rendered via the catalog §1.3 user-facing label lookup; never shown raw. |
| `entity_kind` | TEXT | `App` \| `Session` \| (future) `Device`, `File` |
| `entity_ref` | TEXT | Id / key of the referenced entity |
| `title` | TEXT | Service-written headline |
| `detail` | TEXT | Service-written body. Per-instance facts. |
| `acknowledged_at` | INTEGER NULL | Internal column name. User-facing action is "dismiss." Null until dismissed. |

The catalog (`docs/alerts-catalog.md` §1.1) splits copy across two
surfaces: the service writes `title` + `detail` (per-instance facts),
and the UI looks up a static **"why this matters"** block keyed by
`type`. The design must accommodate both surfaces on the same item;
the why-copy is what gives users the framing to decide if they care.

The catalog §1.2 also forbids internal jargon and abbreviations in
user-facing strings: no "ack" / "unack" / `source_monitor` / field
names surface to users. Every visible label uses plain English.

### 2.2 IPC surface (PRD §9.1 / §9.2)

- `GetAlerts(filter)` → query existing alerts. The `filter` shape
  carries the three filter axes locked in §3.7.
- `DismissAlert(alertId)` → mark dismissed. (Renamed from
  `AcknowledgeAlert` for user-facing vocabulary consistency; updates
  the internal `acknowledged_at` column.)
- **`AlertRaised(alert)`** server-push notification — drives the
  live feed and (if enabled) the desktop toast.

### 2.3 Phase 6 scope (sprint plan)

- Alert pipeline + entity + `GetAlerts` / `DismissAlert` IPC +
  `AlertRaised` push + Alerts feed UI + optional toast.
- **First and only producer:** `UnsignedFromUserPath`. The roadmap
  catalog types are part of the **design vocabulary** so the UI is
  built once for a heterogeneous feed; their producers ship
  post-MVP.
- Retention: alerts kept until dismissed + 90 days, configurable
  (PRD §7.9).

## 3. What the page must communicate

The page must let the user answer four questions, in this order of
priority. The brief is free to choose any composition that answers
them.

### 3.1 "Is there anything I need to look at right now?"

- The user lands on this page (or sees the nav badge, see §3.6) and
  needs to know within ~1 second whether active alerts exist and
  how serious they are.
- **Why it matters:** this is the page's entire reason to exist. If
  the surface fails to surface "you have N active items at severity
  X" instantly, the page has failed at its job.
- **What must be communicable:** count of active alerts at each
  severity (or at minimum: count of active Critical / Warning vs
  Info).

### 3.2 "What is each alert, in plain language?"

For each alert, the user must be able to read:

- **The headline (`alert.title`)** — service-written. A factual
  short sentence about what was observed.
- **The per-instance detail (`alert.detail`)** — service-written.
  The specific image path, signer, byte total, timestamp, or
  whatever facts that alert type carries (see catalog §1.2).
- **The "why this matters" framing** — UI-side static copy keyed by
  `alert.type` (see catalog §1.1). This is what tells a
  non-technical user why an unsigned binary from AppData is worth
  thinking about. **Without this surface visible somewhere on each
  alert, the feed is opaque.** The design may show it inline,
  expand-on-demand, in a side surface, or with a "?" affordance —
  open question, but it must be reachable without leaving the page.

### 3.3 "How serious is it?"

- **Severity must be visually unambiguous at item-level glance.**
  The three severities (`Info` / `Warning` / `Critical`) map
  one-to-one to the status tokens
  (`status.neutral` / `status.caution` / `status.critical`). This
  mapping is **locked** — same color contract as Reports' Notable
  items and any future incident-like surface.
- The brief chooses the *expression* (bar, dot, badge, full
  background, icon tint, type ramp, combinations). The token
  mapping does not move.

### 3.4 "What's the alert about?"

Each alert references a domain entity through `entity_kind` +
`entity_ref`. MVP populates `App` and `Session`. The user must be
able to see, at a glance:

- Which app or session the alert is about (the headline does most
  of this work — the design need not duplicate it).
- When the alert was created (`created_at`).
- Which component raised it, rendered as the **user-facing source
  label** from catalog §1.3 (e.g., `Capture` → "Capture",
  `Rollup` → "Daily check"). The raw `source_monitor` value never
  appears in the UI.

The design's job is to surface these without burying the headline
under metadata. If some of this is appropriate to demote to a
hover, expand, or "show more" surface, that is a design call.

### 3.5 Dismiss

"Dismiss" is the canonical user-facing word for closing the loop on
an alert. Internal terms ("acknowledge," "ack," "unack") do not
appear in the UI; the schema column is `acknowledged_at` and stays
that way internally (no migration), but every visible string, the
IPC method (`DismissAlert`), and the CLI subcommand
(`zvctl alerts dismiss`) use "dismiss."

- The user must be able to dismiss any active alert from the feed
  surface without navigating elsewhere.
- Dismiss is the **only** user action MVP supports. There is no
  separate acknowledge state, no mute, no snooze (see §7 out of
  scope).
- Dismiss is a one-click action with no confirmation.
- Dismissed alerts move out of the default `Active` filter view but
  remain accessible via the `Dismissed` or `All` filter. They
  auto-purge at the retention boundary (`Dismissed + 90 days`,
  configurable per PRD §7.9).
- A dismissed alert can re-raise after the producer's cooldown
  (catalog §1.5). When the same dedupe key re-fires after cooldown,
  a new active row appears in the feed; the prior dismissed row
  stays in the dismissed history until retention purges it.
- Dismissed alerts must be visually distinct from active ones in
  any view that mixes them (e.g., the `All` filter). How (opacity,
  demotion of the severity expression, a separate section) is a
  design call.

### 3.6 Live updates

- The page receives `AlertRaised` push from the service. A new
  alert arrives without the user refreshing.
- The user should be drawn to the new arrival — there must be some
  acknowledgement of the moment-of-arrival distinct from a static
  list redraw. The exact treatment (flash, slide-in, highlight,
  badge increment) is a design call.
- **If the new alert would be excluded by the user's active
  filter**, the feed body cannot be the only signal. The nav badge,
  the count surface, or another always-on element must still
  register that something arrived — otherwise filtering feels like
  the product is hiding alerts from the user.
- The nav rail also receives an Alert badge / count when there are
  active alerts (whether this is a number, a dot, or
  severity-tinted is a design call). This is what surfaces "look at
  Alerts" while the user is on a different page.

### 3.7 Filtering

Three filter axes, locked. The affordance composition is a design
call.

- **State** (single-select, default `Active`):
  - `Active` — alerts the user has not dismissed.
  - `Dismissed` — alerts the user has dismissed and that have not
    yet aged out per retention.
  - `All` — both.
- **Severity** (multi-select, default all on): `Critical`,
  `Warning`, `Info`.
- **Type** (multi-select, default all on): one option per shipped
  catalog type. The user-facing label per type is the type's
  display name (provisional per §5), not the raw catalog identifier
  (`UnsignedFromUserPath`). In Phase 6 there is exactly one type;
  the affordance still appears so the vocabulary is consistent and
  the brief does not design a Phase-6-only special case.

Composition: **AND across axes, OR within axis** (multi-select
means "any of these"). Filtering by `source_monitor` or specific
`entity` is out of scope (§7).

The brief also addresses two filter-behavior concerns:

1. **Persistence.** Filters persist within a session (navigating
   away from Alerts and back keeps them). Filters do not persist
   across app restart.
2. **Reset.** The default state must be obvious from the
   affordance; there must be a one-action way to return to the
   default.

The toast-activation path resets filters to default before scrolling
to the activated alert, per catalog §5.3.

## 4. State coverage

The design must address each state. Severity-token mapping in §3.3
applies wherever color is used.

| State | What must be communicated |
|---|---|
| **Empty (no alerts at all)** | The user has no alerts in the system. The tone is "good news, nothing to look at." This is the cold-start state and the post-retention-cleanup state. |
| **Filtered to empty** | Alerts exist but the active filter excludes them all. **Distinct from "no alerts."** The user must be told the filter is hiding things and given a one-action way to reset. Without this, fiddling with filters feels like the product lost data. |
| **Loading** | Initial `GetAlerts` query is in flight on page enter. A determinate loading affordance, no shimmer. |
| **Disconnected (named pipe down)** | The service is unreachable. The user must be told the feed is stale and that dismiss will not work right now. Filters may remain operative as a UI-local concern. |
| **Query error** | `GetAlerts` returned a failure. Surface the failure with enough information to file a bug; do not silently fall back to empty. |
| **Live arrival** | A new alert arrives via `AlertRaised` push. The arrival itself must register visually. See §3.6 for the filter-excluded variant. |
| **All active** | The common "you have work to do" state. The severity composition of the active set should be readable at a glance (see §3.1). |
| **All dismissed** | Some users check in periodically. The "everything seen" state should feel reassuring, not bare. |
| **Mixed** | The default ongoing state. Dismissed alerts (visible when the `All` or `Dismissed` filter is active) demoted; active prominent. |

## 5. Provisional-data flag — MANDATORY

The brief **locks** the durable layers:

- Visual language (tokens, type ramp, density).
- IA placement.
- The four questions in §3 and the order of priority.
- Severity-to-token mapping (`Info` → neutral, `Warning` →
  caution, `Critical` → critical).
- The two-surface copy model (service-written title/detail; UI
  why-copy keyed by `alert.type`). The catalog is the source.
- The user-facing source-label rendering (catalog §1.3 lookup; raw
  `source_monitor` never visible).
- The no-jargon, no-abbreviation rule on user-facing strings
  (catalog §1.2). No "ack" / "unack" / field-name vocabulary
  reaches users.
- The dismiss contract (one click, no confirm, dismissed remains
  accessible via filter until retention).
- The state matrix in §4 (including "filtered to empty" as
  distinct from "no alerts").
- The three filter axes (`State`, `Severity`, `Type`), their
  default values, and the AND-across / OR-within composition.

The brief **flags as provisional**, to lock at Phase 6
implementation:

- The catalog's roadmap entries (`InvalidSignature`,
  `FirstRunWanTalker`, `UnusualDailyVolume`, `LargeDownload`,
  `OutboundHeavy`) are design-vocabulary today, producers later.
  Wording of why-copy and detail templates may iterate.
- Type-to-icon mapping. MVP has one type; the catalog lists six. The
  brief should propose icons for each catalog entry; the
  lookup-table contract is the lock, individual icon choices are
  not.
- The user-facing **display name** per catalog type (the string
  used in the filter affordance and the per-item label). Distinct
  from the catalog identifier (`UnsignedFromUserPath`). Brief
  proposes; catalog locks in lockstep.
- The exact filter affordance (chips / rail / bar / ComboBoxes /
  segmented control / combination).
- The exact dismiss affordance (button / chip / icon button / menu
  item) per item.
- The exact severity expression (bar / dot / badge / background /
  combination).
- The exact "why this matters" surfacing (inline / expand /
  side-rail / "?" affordance).
- The exact live-arrival treatment (flash / slide / highlight /
  badge).
- The filtered-to-empty treatment (copy and reset affordance).

## 6. Two designed states — MANDATORY

### (a) Interim placeholder treatment

What ships in the polish-interlude app between now and Phase 6.

- A deliberate "this is coming" surface, not the working layout
  with dummy data.
- Must communicate: what this page will eventually do, what alert
  arrives first (the MVP `UnsignedFromUserPath` type from the
  catalog, in plain language), and that Phase 6 brings it online.
- The interim is what the user actually opens today; the design
  should treat it as such, not as throwaway.

### (b) Eventual functional layout

The full Phase 6 layout addressing §3 and §4. The brief asks for
both states side by side so the polish-interlude promise reads as
continuous with the functional state.

## 7. Out of scope — features flagged for later

- **Multi-select dismiss** — bulk action; feature.
- **Filtering by `source_monitor` or specific `entity`** — adds
  filter axes beyond the locked three; feature.
- **Filter persistence across app restart** — within-session only
  for MVP (§3.7); cross-session = feature.
- **Dedicated alert detail page or modal** — inline surfacing
  covers MVP. If the brief proposes a flyout for some catalog
  entries (e.g. future `Device` alerts with rich detail), it
  inherits the locked flyout rules (opaque + `surface.layer`, per
  the design system).
- **Mute / snooze by type** — would land an `alert_mutes` table;
  feature.
- **Sound or custom notification beyond Windows toast.** Toast is
  the OS-canonical surface; sound = feature.
- **Forward / share alert** (email, copy-link). Email is a hard NO
  per zero-own-traffic. Copy-to-clipboard is fine but feature.
- **Active-action affordances on alert items** — no "kill this
  process," no "block this app," no "quarantine." Passive-only
  invariant — **HARD NO**.
- **Settings overlap.** The toast-routing controls (severity floor
  ComboBox + "Notify on large downloads" toggle, per catalog §5)
  live on the Settings page, not on the Alerts page. The Alerts
  page does not own notification configuration.

## 8. Load-bearing WPF notes

These are not design prescriptions; they are constraints WPF imposes
that the brief must not accidentally invalidate.

- **Scrolling-list measure trap.** Any list-style container on a
  page hosted inside Wpf.Ui's `NavigationView` wraps in a
  `DynamicScrollViewer` that grants infinite vertical measure.
  Without a finite measure constraint, virtualization fails and the
  list materializes every item at once. The implementer will set
  `MaxHeight` programmatically on `Loaded` + `SizeChanged`. The
  brief may assume the list **does** virtualize; it may not assume
  every item is in the visual tree at all times. Memory:
  `project_wpfui_navigationview_scrollviewer.md`.
- **Variable-height items + virtualization.** If the design lets an
  item grow on interaction (e.g. expanding to show more detail),
  the implementer will configure the list for pixel-unit scrolling
  rather than item-unit. The brief may design for expanding items;
  it should know that fixed-height items are cheaper for the
  implementer.
- **Cached page lifecycle.** `NavigationCacheMode.Enabled` means
  the page constructor runs once per process; `Loaded` re-fires on
  every nav-rail revisit. The implementer subscribes to
  `AlertRaised` in the constructor, not `Loaded`. The brief does
  not need to design around this; flagged so QA understands why
  push works correctly even on a freshly-revisited page.
- **Desktop toast surface.** Windows 11
  `ToastNotificationManager` renders toasts. Toast visual is
  OS-owned; ZenVizor tokens do not apply. The brief notes the
  trigger and acknowledges that the toast is not part of the
  design surface to be mocked. Toast routing logic and the
  controlling settings live in the catalog and in the Settings
  brief.

## 9. Contract with the catalog

Two-way: this doc cannot drift from `docs/alerts-catalog.md`, and
the catalog cannot drift from this doc.

- **New alert type** → catalog entry first, then findings (if a
  surface assumption changes), then design refresh, then producer
  code.
- **New `source_monitor` value** → catalog §1.3 entry first
  (with its user-facing label), then any findings update.
- **Severity-to-token mapping** → changes here force a catalog edit
  in lockstep.
- **"Why this matters" copy** → authored in the catalog, rendered
  from a UI string table; the brief renders the lookup, not the
  literal strings.
- **Dismiss vocabulary** → if a future variant introduces a
  distinct user action (e.g., separate "acknowledge" and "dismiss"
  states), it lands in the catalog vocabulary table first and the
  findings second.

If a brief proposal would require the catalog to change (a new
severity level, a new entity kind that needs UI affordances, a
fourth filter axis), surface the catalog-edit explicitly in the
brief return so the catalog stays the source of truth.
