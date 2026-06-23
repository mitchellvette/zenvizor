# ZenVizor — Alerts catalog

Companion to `docs/zenvizor-prd.md` §6 / §7.6 and
`docs/zenvizor-sprint-plan.md` Phase 6. Single source of truth for the
alert vocabulary: the producer signal, severity, dedupe and cooldown
semantics, the "why this matters" copy the UI renders, and what facts
the service writes into `alert.detail`.

This file is the contract the alerts design brief is written against
and the alerts implementation is wired against. **Add a new alert type
here before adding it in code.** The UI's why-copy lookup table
mirrors this doc one-to-one.

---

## 1. Conventions

### 1.1 Two-surface copy model

Every alert carries two textual surfaces, written by two different
authors:

- **`alert.title`** and **`alert.detail`** — written by the *service*
  per instance. Carry the per-instance facts: the actual image path,
  the actual signer, the connection count, the byte total, the time
  window. Read directly by `zvctl alerts list` and any future
  scripted consumer.
- **"Why this matters"** — a static block per `type`, looked up *in
  the UI* keyed by `alert.type`. Identical copy for every instance of
  the same type. Lives in a `WhyCopyResources.xaml` (or equivalent
  string table) and is updated in lockstep with this doc.

The split keeps the service and database honest (per-instance facts,
no editorial) and lets the UI editorialize the type-level framing
without DB churn or schema migrations.

`zvctl alerts list` shows title + detail only; the why-copy is a UI
affordance, not a payload field.

### 1.2 Field discipline for user-facing copy

Applies to **all** user-facing strings: `alert.title`, `alert.detail`,
the why-copy lookup blocks, the filter values, the source label, and
any other text the UI puts in front of a user.

- **Reference only data the service actually has** at the time of
  raise (image path, signer string, byte totals, connection counts,
  session start time, daily rollup totals). Per PRD §6 / Invariant 5:
  never fabricate precision; never claim "this is malware." Frame
  observation, not verdict.
- **No internal jargon.** Schema field names and IPC identifiers
  (`source_monitor`, `entity_kind`, `acknowledged_at`,
  `traffic_samples`, `Rollup`, "ack," "unack") never appear in
  user-facing strings. The user-facing word for closing the loop on
  an alert is **"dismiss,"** not "acknowledge"; the user-facing label
  for `source_monitor` comes from the lookup in §1.3, not from the
  raw column value.
- **No abbreviations of common words** in UI strings. "Acked" /
  "Unacked" are out. Use the full word or a different word
  ("Active" / "Dismissed").
- **No em-dash** in user-facing copy (memory:
  `feedback_no_emdash_in_ui_copy.md`). Use period, colon, or
  semicolon.

### 1.3 Source labels (UI)

`source_monitor` is the technical identifier the service writes to
the alert row. The UI renders a user-facing label, looked up from
this table:

| `source_monitor` value | User-facing label |
|---|---|
| `Capture` | Capture |
| `Rollup` | Daily check |

New `source_monitor` values added by future producers (e.g.
`HostsFile`, `ProxyWatcher`) land in this table at the same time the
producer ships. The UI's lookup falls back gracefully if it sees a
value it does not know about (render the raw value verbatim is
acceptable; the rule is "do not crash and do not silently hide it").

### 1.4 Severity vocabulary

| Severity | Meaning | Color token |
|---|---|---|
| `Info` | Visibility, no urgency. User glance. | `status.neutral` |
| `Warning` | Anomalous, worth review. User decides. | `status.caution` |
| `Critical` | Strong indicator pattern. User should examine. | `status.critical` |

Three levels, no more. These severity-to-color tokens are the rendering
side of the same table on the Alerts page.

### 1.5 Dedupe and cooldown

Every alert type defines:

- A **dedupe key** — what combination of (`type`, entity, window)
  collapses to one row.
- A **re-raise cooldown** — minimum time after dismiss before the
  same dedupe key can fire again.

The producer must check, before insert:

```
SELECT 1 FROM alerts
 WHERE type = @type
   AND entity_kind = @entity_kind
   AND entity_ref  = @entity_ref
   AND (acknowledged_at IS NULL
        OR acknowledged_at >= @now - @cooldown)
```

The schema column is `acknowledged_at` and stays that way (no
migration). It records the timestamp at which the user dismissed the
alert; the user-facing action, the IPC method, the CLI subcommand,
and the filter value all use "dismiss." Internal column name vs.
external vocabulary is the deliberate split.

If a row matches, skip. Otherwise insert. Without this check the
producer hammers the alerts table every flush tick, the feed is
unreadable, and dismiss feels broken.

### 1.6 Entity reference convention

`entity_kind` / `entity_ref` lets a single feed reference different
domain objects. For MVP only `App` and `Session` are populated:

- `App` / `<app_id>` — the alert is about a deduplicated program.
  Most common.
- `Session` / `<session_id>` — the alert is about one specific run
  of a PID. Used when re-raising per-run is desired (none in MVP).

`Device` and `File` are reserved per PRD §7.6 for the post-MVP
roadmap; no producer writes them in Phase 6.

---

## 2. MVP — Phase 6 shipping set

### `UnsignedFromUserPath`

| Field | Value |
|---|---|
| Severity | `Critical` |
| `source_monitor` | `Capture` |
| `entity_kind` | `App` |
| `entity_ref` | `<app_id>` |
| Dedupe key | `(type, app_id)` |
| Re-raise cooldown | 24h after dismiss |
| Toast default | Yes |
| Status | **MVP — PRD §6 first customer** |

**Trigger.** First WAN connection observed (i.e. `connections` row
with `remote_class = 'Wan'`) for a session whose owning app has
`apps.signature_status = 'Unsigned'` AND `apps.is_user_writable_path
= 1`. Raised on the flush tick that produces the first qualifying
connection.

**Why this matters (UI static copy):**

> An unsigned program is making network connections from a folder you
> can write to (Temp, AppData, Downloads, or similar). This pattern
> shows up in installers, updater stubs, and small utilities; it also
> shows up in malware that uses the same folders to avoid attention.
> ZenVizor cannot tell which one this is. The image path and signer
> below are the facts you can use to decide whether you recognize this
> program.

**`alert.title` template (service):**

> Unsigned program talking to the network: `{image_name}`

**`alert.detail` template (service):**

> `{image_name}` is running from a user-writable folder and started
> making network connections. Image path: `{image_path}`. Signer:
> none (unsigned). First connection: `{first_seen_local}`. Connections
> so far: `{connection_count}`.

---

## 3. Roadmap catalog (design-vocabulary only in Phase 6)

These types exist in this doc and in the UI's why-copy table so the
feed renderer is built once for a heterogeneous catalog. **No producer
code ships for these in Phase 6.** Each lights up when its rule is
authored in a post-MVP increment.

The UI's icon, severity color, why-copy lookup, source-label lookup
(§1.3), and filter-by-type list must already handle every type listed
here on day one. Shipping the UI bound to a single-type feed and
refactoring it later is the failure mode this doc exists to prevent.

### `InvalidSignature`

| Field | Value |
|---|---|
| Severity | `Critical` |
| `source_monitor` | `Capture` |
| `entity_kind` | `App` |
| Dedupe key | `(type, app_id)` |
| Re-raise cooldown | 24h after dismiss |
| Toast default | Yes |
| Status | Roadmap |

**Trigger.** App makes a connection while `apps.signature_status =
'Invalid'`.

**Why this matters:**

> This program was signed by its publisher, but the signature does not
> verify. The binary may have been modified after signing, the
> certificate chain may be broken, or the certificate may have
> expired in a way the OS cannot resolve. An invalid signature is a
> stronger signal than no signature at all and is worth examining
> before you keep running the program.

### `FirstRunWanTalker`

| Field | Value |
|---|---|
| Severity | `Info` |
| `source_monitor` | `Capture` |
| `entity_kind` | `App` |
| Dedupe key | `(type, app_id)` |
| Re-raise cooldown | Never re-raise (one-shot per app) |
| Toast default | No |
| Status | Roadmap |

**Trigger.** A newly created `app_id` (i.e. `first_seen` within the
last N seconds, suggest 30) reaches the network for the first time.

**Why this matters:**

> ZenVizor noticed this program for the first time and it has already
> made a network connection. Most installed software phones home on
> first run. This alert exists so you can spot the case where the
> program is one you do not remember installing.

### `UnusualDailyVolume`

| Field | Value |
|---|---|
| Severity | `Warning` |
| `source_monitor` | `Rollup` |
| `entity_kind` | `App` |
| Dedupe key | `(type, app_id, YYYY-MM-DD)` |
| Re-raise cooldown | One alert per app per day (key includes date) |
| Toast default | No |
| Status | Roadmap |

**Trigger.** Daily-rollup tick determines today's `bytes_up +
bytes_down` for an app is robustly above its 14-day baseline. See
§4 for the algorithm.

**Why this matters:**

> One of your programs moved noticeably more data today than its
> typical day for the past two weeks. Streaming sessions, big game
> patches, large cloud-sync runs, and runaway updaters all look like
> this. Open the program's detail to see when the spike happened and
> which endpoints it talked to.

### `LargeDownload`

| Field | Value |
|---|---|
| Severity | `Info` |
| `source_monitor` | `Capture` |
| `entity_kind` | `Session` |
| Dedupe key | `(type, session_id, connection_id)` |
| Re-raise cooldown | Never re-raise same connection |
| Toast default | Yes, but gated by its own setting (§5) |
| Status | Roadmap |

**Trigger.** A single `connections` row's `bytes_down` delta exceeds
the threshold (suggest 50 MB) over a short window from a session that
had been quiet on the download side. Raised on the connection-upsert
tick.

**Why this matters:**

> One of your programs just pulled down a large download. Auto-updates
> for browsers, system components, and game launchers usually look
> like this. This alert exists so you can spot the case where an
> update happened that you did not ask for or did not expect.

### `OutboundHeavy`

| Field | Value |
|---|---|
| Severity | `Warning` |
| `source_monitor` | `Rollup` |
| `entity_kind` | `App` |
| Dedupe key | `(type, app_id, YYYY-MM-DD)` |
| Re-raise cooldown | One alert per app per day |
| Toast default | No |
| Status | Roadmap |

**Trigger.** Daily-rollup tick: `bytes_up > k × bytes_down` AND
`bytes_up >` absolute floor (suggest k=3, floor=100 MB).

**Why this matters:**

> One of your programs sent out a lot more data than it pulled in
> today. Backup clients, cloud-sync, and video-call apps legitimately
> look like this. The pattern is also what data exfiltration looks
> like, so it is worth confirming the program is one you expect to be
> uploading.

---

## 4. Anomaly algorithm — `UnusualDailyVolume`

Robust against the heavy-tailed distribution of daily traffic. Runs
once per day at the daily-rollup tick. **Daily only in MVP** (no
hourly variant; the intra-day form is feature-class).

For each `app_id` with `(now - first_seen) >= 7 days`:

1. Query last 14 closed `traffic_daily` rows for this app, summing
   `bytes_up + bytes_down` per day. Skip if fewer than 7 rows.
2. Compute the **median** `M` of the 14 totals.
3. Compute the **median absolute deviation** `MAD = median(|x_i − M|)`.
4. Compute a **robust z-score** for today's total `T`:
   `z = (T − M) / (1.4826 × MAD)`.
   (The 1.4826 factor scales MAD to be comparable to a standard
   deviation for normal data.)
5. **Fire** if `z > 3.5` AND `T > 100 MB`.

**Why median + MAD, not mean + stddev.** Daily traffic per app is
heavy-tailed — one streaming-movie day skews mean and stddev so much
that the next genuine anomaly is masked. Median and MAD are
unaffected by one outlier day.

**Why the 7-day eligibility floor.** A brand-new app has no baseline.
Without the floor, every freshly-installed program triggers an
anomaly on its second day.

**Why the absolute byte floor.** A sleepy app that jumps from 1 MB
to 10 MB is "10×" but not interesting. The floor suppresses
micro-noise; only volumes the user would notice in the report.

**Tuning parameters** (live in `settings`, not hard-coded):

| Key | Default | Notes |
|---|---|---|
| `alerts.unusual_volume.z_threshold` | `3.5` | Higher = less sensitive |
| `alerts.unusual_volume.byte_floor` | `104857600` (100 MB) | Absolute today-total floor |
| `alerts.unusual_volume.min_baseline_days` | `7` | Minimum trailing rows |

---

## 5. Toast routing

Toasts are rendered by the **UI process** (Session 0 has no user UI).
The service raises `AlertRaised`; the UI decides whether to surface a
toast based on user settings and the alert type's toast default.

### 5.1 Settings surface

Two controls in the Settings page Alerts section:

1. **Severity floor (ComboBox).** "Show desktop notifications for:"
   - `All alerts` — every raised alert toasts.
   - `Warning and Critical` *(default)* — Info-severity alerts do not
     toast.
   - `Critical only` — only Critical alerts toast.
   - `Off` — no toasts at all.
2. **"Notify on large downloads" (Toggle).** Special-cases
   `LargeDownload` (Info severity) to toast even when the severity
   floor would otherwise suppress it. Off by default. Visible in
   Settings whether or not the `LargeDownload` rule has shipped
   (settings persistence is forward-compatible).

The toggle exists because `LargeDownload` is the one Info-severity
type users overwhelmingly want as a notification ("did Chrome just
update?"). Bundling it under "All alerts" would force users to either
opt into every Info alert or get none.

### 5.2 Per-type toast default

Each catalog entry above specifies a **Toast default** field. This is
*not* a hard toggle; it is the renderer's hint about whether the type
is suitable for an OS toast at all. A user-settings rule applies on
top:

```
should_toast = (alert.severity >= settings.severity_floor)
            OR (alert.type == 'LargeDownload' AND settings.toast_on_large_download)
```

`UnsignedFromUserPath` and `InvalidSignature` toast by default at the
shipping severity floor (`Warning and Critical`). `FirstRunWanTalker`
and `OutboundHeavy` do not toast at the default floor (one is Info,
the other is Warning but rollup-paced — the user sees it in the feed
without a toast urgency).

### 5.3 Toast XML

Toast XML is OS-owned; design tokens do not apply. The toast carries:

- `alert.title` as the headline.
- A truncated first sentence of `alert.detail` as the body.
- Activation argument = `alertId`. When the user clicks the toast,
  the UI brings the Alerts page forward, **resets the page filter
  to its default state** (so the activated alert is visible
  regardless of the user's prior filter selection), and scrolls to
  the activated alert id.

AUMID (AppUserModelID) must be registered by the WiX installer at
install time so toasts render with the ZenVizor brand. Without it,
toasts render as "Generic notification source" — Phase 6 installer
must wire this.

---

## 6. Phase 6 cut

What actually ships in Phase 6:

- **One producer:** `UnsignedFromUserPathRule`, wired through the full
  alert pipeline (rule → producer → repository → IPC →
  `AlertRaised` push → UI feed → optional toast → dismiss flow).
- **IPC method:** `DismissAlert(alertId)`. (Renamed from
  `AcknowledgeAlert` for user-facing vocabulary consistency. The
  rename lands in `ZenVizor.Ipc.Contracts` at the same time the
  pipeline is wired. Internal schema column `acknowledged_at` is
  not renamed.)
- **CLI:** `zvctl alerts list`, `zvctl alerts dismiss <id>`. Reads
  flow over the dismiss-vocabulary path top to bottom.
- **Catalog scaffolding:** UI's why-copy table, icon mapping,
  source-label lookup (§1.3), and toast routing know about every type
  in this doc. The feed renderer does not branch on type identity
  beyond the lookup tables. The filter-by-type control lists every
  type in the catalog (even those without producers in Phase 6), so
  the vocabulary is consistent and no Phase-6-only special-case
  design ships.
- **Settings:** severity-floor ComboBox + "Notify on large downloads"
  toggle, even though `LargeDownload` does not have a producer yet.
- **Synthetic test gate:** the Phase 6 CI alert-rule test (sprint
  plan acceptance criterion) fires exactly one `UnsignedFromUserPath`
  for the qualifying fixture, dedupes a second flush tick, and
  re-raises after dismiss + cooldown.

Out of Phase 6, on the post-MVP roadmap:

- All roadmap producers in §3.
- Per-type mute / snooze (would land as an `alert_mutes` table).
- Multi-select dismiss.
- Filtering by `source_monitor` or specific `entity`.

---

## 7. How to add a new alert type

1. Add a new entry to §3 (or §2 if MVP-shipping) with the full table:
   severity, source monitor, entity kind, dedupe key, cooldown, toast
   default, status.
2. Write the **why-copy** block. Plain English, no verdicts, no
   em-dash, no internal jargon (see §1.2).
3. Write the **`alert.title`** and **`alert.detail`** templates.
   Reference only fields the service actually has access to at the
   time of raise.
4. If the new producer uses a new `source_monitor` value, add it to
   the §1.3 lookup table with its user-facing label.
5. Land the catalog entry in the UI's `WhyCopyResources.xaml` (or
   equivalent string table), icon-lookup function, and the
   filter-by-type list. The renderer should not need branching beyond
   the lookups.
6. If the type warrants its own settings toggle (rare — the
   `LargeDownload` precedent), add it to §5.
7. Implement the producer (one `IAlertRule` class, registered with
   the `AlertProducer`).
8. Add a synthetic deterministic test: fixture inputs produce
   exactly one alert; second-tick dedupes; dismiss + cooldown
   re-raise works.

The catalog entry is the contract; the code follows it, not the
other way around.
