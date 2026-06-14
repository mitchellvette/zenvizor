# Claude Design brief — Alerts

ZenVizor's Alerts screen. Self-contained brief for a Claude Design
session whose prior pass already loaded `docs/claude-design-primer.md`
and aligned to ZenVizor's token surface. Paste this brief ALONE; do
not re-paste the primer. The mockup hand-off contract is in §19.

> **Read this section first.** The Alerts vocabulary — the set of
> alert types ZenVizor knows how to raise, their severity, the
> "why this matters" copy that frames each type for the user, and
> the title/detail templates the service writes — is established
> by `docs/alerts-catalog.md`. The brief inlines the strings Claude
> Design needs (sample instances in §3, why-copy in §3, source
> labels in §3). The catalog itself stays the authoritative source;
> if a design proposal would require the catalog to change, surface
> the catalog edit in the brief return.

---

## 1. Screen identity

- **Screen name:** Alerts.
- **XAML file:** does not exist yet — Alerts ships in Phase 6. The
  current `src/ZenVizor.Ui/Views/AlertsPage.cs` routes to
  `PlaceholderPage` with subtitle `"Generic alert feed (seam #2).
  Phase 6."`. The Phase 6 implementation lands a real `AlertsPage`
  whose composition is determined by this brief.
- **IA placement:** fifth item in the left nav rail.
  `Symbol="Alert24"`. `NavigationCacheMode.Enabled`.
- **Purpose (casual voice):** "things ZenVizor noticed about my
  machine that might be worth a look — what was odd, how serious
  it might be, and a way to mark each one as handled so the next
  visit starts clean."

---

## 2. UX intent

Alerts is the **first wired customer of the alert pipeline seam**
(PRD §6, sprint Phase 6) and the screen the user opens when they
want to know whether ZenVizor's passive observation has surfaced
anything worth attention. The page must answer "is there anything
I should look at right now" within one second of landing and
support a single user action — **dismiss** — to close the loop on
each item. Beyond that, it must editorialize: each alert type
carries a static *"why this matters"* block written from the user's
perspective so a non-technical reader can decide whether to act.
The feed is heterogeneous from day one — the catalog defines six
types and the design must compose for all six even though only one
(`UnsignedFromUserPath`) has a producer in Phase 6. Three filter
axes (state / severity / type) cut the feed; dismissed alerts move
out of the default view but remain accessible until retention purges
them. A push-driven live arrival path means new alerts appear
without refresh, and the user must perceive the moment of arrival
both from the Alerts page itself and from a nav-rail badge that
remains visible while the user is elsewhere in the app.

---

## 3. Controls in scope

The brief carries the surface area; Claude Design composes the
page. This section opens with **what the page must communicate**
(four user questions, lifted from the findings doc) and **the
catalog vocabulary the design must render**. The control list
follows.

### What the page must communicate

The page must let the user answer four questions, in this order
of priority:

**Q1. "Is there anything I need to look at right now?"** Landing
or glancing at the nav badge must tell the user within ~1 second
whether active (undismissed) alerts exist and how serious they
are. Count of active alerts at each severity (or at minimum:
count of active `Critical` / `Warning` vs `Info`) must be
communicable from a quick scan.

**Q2. "What is each alert, in plain language?"** For each alert,
the user reads three pieces of copy:

- **The headline** — service-written, factual one-liner. Names
  what was observed.
- **The per-instance detail** — service-written, multi-sentence.
  Carries the actual facts: image path, signer, byte totals,
  timestamps, connection counts. This is the part that varies
  between instances of the same type.
- **The "why this matters" framing** — UI-side static copy keyed
  by `alert.type`. Same paragraph for every instance of the same
  type. Tells a non-technical user why an unsigned binary from
  AppData is worth thinking about, why a 4× anomaly might or
  might not be normal, etc. **This surface must be reachable on
  each alert without leaving the page** — but *how* it's reached
  (always-on inline, expand on click, side-rail, "?" affordance)
  is open per §8.4 Q3.

**Q3. "How serious is it?"** Each item must communicate severity
at glance via the three status tokens. Severity-to-token mapping
is locked: `Info` → `status.neutral`, `Warning` → `status.caution`,
`Critical` → `status.critical`. The *expression* (bar / dot /
badge / background tint / icon tint / combination) is open per
§8.4 Q2.

**Q4. "What's the alert about?"** Each alert references a domain
entity (an app or a session). The user must be able to see:

- Which app or session (the headline does most of this).
- When the alert was created.
- Which component raised it, rendered as the **user-facing
  source label** from the lookup below. The raw `source_monitor`
  value never appears in the UI.

### Catalog vocabulary the design must render

ZenVizor knows six alert types. **Phase 6 ships a producer for
only the first one (`UnsignedFromUserPath`)**; the other five are
*design vocabulary* — the renderer is built once for the full
catalog, and their producers ship post-MVP. The filter-by-type
control lists every catalog type even when only one has a
producer in Phase 6, so the vocabulary stays consistent and no
Phase-6-only special-case design ships.

For each type, the design must render:

- The item's severity (locked, below).
- The headline and per-instance detail (sample instances below).
- The "why this matters" block (full text below).
- The source label (lookup table below).

#### Severity assignment per type (locked)

| Type | Severity | Toast default |
|---|---|---|
| `UnsignedFromUserPath` | `Critical` | Yes |
| `InvalidSignature` | `Critical` | Yes |
| `FirstRunWanTalker` | `Info` | No |
| `UnusualDailyVolume` | `Warning` | No |
| `LargeDownload` | `Info` | Yes (via dedicated setting) |
| `OutboundHeavy` | `Warning` | No |

#### Source-label lookup (user-facing)

| `source_monitor` value | User-facing label |
|---|---|
| `Capture` | Capture |
| `Rollup` | Daily check |

The raw `source_monitor` never appears in the UI; the label
column does.

#### "Why this matters" copy per type (UI static lookup, render verbatim)

**`UnsignedFromUserPath`** (Critical, source: Capture)

> An unsigned program is making network connections from a folder
> you can write to (Temp, AppData, Downloads, or similar). This
> pattern shows up in installers, updater stubs, and small
> utilities; it also shows up in malware that uses the same
> folders to avoid attention. ZenVizor cannot tell which one this
> is. The image path and signer below are the facts you can use
> to decide whether you recognize this program.

**`InvalidSignature`** (Critical, source: Capture)

> This program was signed by its publisher, but the signature
> does not verify. The binary may have been modified after
> signing, the certificate chain may be broken, or the
> certificate may have expired in a way the OS cannot resolve.
> An invalid signature is a stronger signal than no signature at
> all and is worth examining before you keep running the program.

**`FirstRunWanTalker`** (Info, source: Capture)

> ZenVizor noticed this program for the first time and it has
> already made a network connection. Most installed software
> phones home on first run. This alert exists so you can spot the
> case where the program is one you do not remember installing.

**`UnusualDailyVolume`** (Warning, source: Daily check)

> One of your programs moved noticeably more data today than its
> typical day for the past two weeks. Streaming sessions, big
> game patches, large cloud-sync runs, and runaway updaters all
> look like this. Open the program's detail to see when the spike
> happened and which endpoints it talked to.

**`LargeDownload`** (Info, source: Capture)

> One of your programs just pulled down a large download.
> Auto-updates for browsers, system components, and game
> launchers usually look like this. This alert exists so you can
> spot the case where an update happened that you did not ask
> for or did not expect.

**`OutboundHeavy`** (Warning, source: Daily check)

> One of your programs sent out a lot more data than it pulled in
> today. Backup clients, cloud-sync, and video-call apps
> legitimately look like this. The pattern is also what data
> exfiltration looks like, so it is worth confirming the program
> is one you expect to be uploading.

#### Sample instances (Claude Design composes against these)

Render every state's mock against this set so the heterogeneous
feed is genuinely heterogeneous. Each line is one rendered alert
item.

1. **`UnsignedFromUserPath`** (Critical, Capture, 2026-06-11
   14:32):
   - **Headline:** Unsigned program talking to the network:
     `7zG.exe`
   - **Detail:** `7zG.exe` is running from a user-writable
     folder and started making network connections. Image path:
     `C:\Users\Mitch\AppData\Local\Temp\7zS9F2A3.tmp\7zG.exe`.
     Signer: none (unsigned). First connection: 2026-06-11
     14:32. Connections so far: 3.

2. **`InvalidSignature`** (Critical, Capture, 2026-06-11 09:15):
   - **Headline:** Program signature does not verify:
     `legacy-installer.exe`
   - **Detail:** `legacy-installer.exe` started a network
     connection while its signature did not verify. Image path:
     `C:\Program Files (x86)\OldVendor\legacy-installer.exe`.
     Signer: `OldVendor LLC` (signature invalid). First
     connection: 2026-06-11 09:15. Connections so far: 1.

3. **`FirstRunWanTalker`** (Info, Capture, 2026-06-11 10:48):
   - **Headline:** First-time program reached the network:
     `Notion.exe`
   - **Detail:** `Notion.exe` was seen for the first time and
     connected to a remote endpoint within seconds. Image path:
     `C:\Users\Mitch\AppData\Local\Programs\Notion\Notion.exe`.
     Signer: `Notion Labs, Inc.`. First seen: 2026-06-11 10:48.
     First connection: 2026-06-11 10:48.

4. **`UnusualDailyVolume`** (Warning, Daily check, 2026-06-11
   00:05):
   - **Headline:** Higher-than-usual data use today: `chrome.exe`
   - **Detail:** `chrome.exe` has moved 8.4 GB today, against a
     typical 1.9 GB over the past 14 days. Today's volume is
     about 4.4× the recent median. Open the program's history
     to see when the activity spiked.

5. **`LargeDownload`** (Info, Capture, 2026-06-11 13:21):
   - **Headline:** Large download in progress:
     `MicrosoftEdgeUpdate.exe`
   - **Detail:** `MicrosoftEdgeUpdate.exe` pulled down 187 MB
     from an endpoint it had not used today. Image path:
     `C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe`.
     Signer: `Microsoft Corporation`. Started: 2026-06-11 13:21.

6. **`OutboundHeavy`** (Warning, Daily check, 2026-06-11 00:05):
   - **Headline:** Uploads dominated downloads today:
     `Backblaze.exe`
   - **Detail:** `Backblaze.exe` sent 4.1 GB and received 78 MB
     today. The outbound-to-inbound ratio is unusual; backup
     clients legitimately look like this. Signer: `Backblaze,
     Inc.`.

### Dismiss vocabulary (locked)

The user-facing word for closing the loop on an alert is
**"dismiss."** Internal terms ("acknowledge," "ack," "unack") do
not appear in the UI. Filter values use full words ("Active" /
"Dismissed" / "All"), not abbreviations.

- Dismiss is **one click, no confirmation**.
- Dismissed alerts move out of the default `Active` filter view
  but remain accessible via `Dismissed` or `All`. They auto-purge
  at the retention boundary (dismissed + 90 days, configurable).
- A dismissed alert can re-raise after the producer's cooldown.
  When the same dedupe key re-fires after cooldown, a *new*
  active row appears; the prior dismissed row stays in dismissed
  history until retention purges it.
- Dismissed alerts must be visually distinct from active ones in
  any view that mixes them (e.g. the `All` filter). How — opacity,
  demotion of the severity expression, a separate section — is a
  design call (open per §8.4 Q2).

### Filter axes (locked)

Three axes; affordance composition is open per §8.4 Q4.

- **State** (single-select, default `Active`): `Active` /
  `Dismissed` / `All`.
- **Severity** (multi-select, default all on): `Critical` /
  `Warning` / `Info`.
- **Type** (multi-select, default all on): one option per
  catalog type (six in total). User-facing label for each type
  is the type's display name (provisional per §8.4 Q7), not the
  raw catalog identifier.

Composition: **AND across axes, OR within axis**. Filtering by
source or specific entity is out of scope (§15).

Persist filters within session (nav away and back keeps them);
do not persist across app restart. One-action reset to defaults
must be obvious from the affordance.

### Controls in scope (by type and purpose)

Components Claude Design picks from. Composition (arrangement,
hierarchy, density, spacing) is its work.

#### Page chrome

- `ui:TextBlock` — page title and any subtitle / framing copy.
- `Border` — page-level surfaces (the canonical metal-card
  recipe applies per §5; per-item vs. one-outer-card application
  is open per §8.4 Q1).

#### Active-set summary surface

A surface that answers Q1 above ("is there anything to look at
right now"). May be cells, a strip, eyebrow text, a hero number,
or part of the page chrome — composition open. The surface must
communicate count by severity for the active filter, and must
remain readable when the filter state changes.

#### Filter affordance

Three axes per the locked list above. Composition open per
§8.4 Q4. Candidate controls — `ui:Button` styled as chips,
`ComboBox`, segmented control, a left rail, or combinations.

#### Feed container

A virtualizable list of alert items. `ListView` is the implicit
default for variable-height items; `ItemsControl` is acceptable
if the design needs more layout latitude than a `ListView`
ItemTemplate gives, provided virtualization is preserved (see
§10).

#### Per-item template

Every item carries:

- Severity expression (open per §8.4 Q2; uses status tokens).
- Type icon — `ui:SymbolIcon`; brief proposes icons per catalog
  entry as part of mock annotation. Lookup keyed by `alert.type`.
- Headline — `ui:TextBlock` (`text.body.strong` or similar; per
  primer type ramp).
- Per-instance detail — `ui:TextBlock` with `TextWrapping=Wrap`.
- "Why this matters" framing — surface and trigger open per
  §8.4 Q3.
- Metadata — created-at timestamp, user-facing source label,
  optional entity reference. Composition open.
- Dismiss affordance — `ui:Button` (or chip / icon button / menu
  item). One click, no confirm. Treatment when the alert is
  already dismissed must be visually distinct.

Paths, image names, and signer strings are aligned digit / mono
content where they sit in column-like runs; per primer rules,
those runs use `text.mono`.

#### Banners (for disconnected / error states)

- `Border` with `status.critical.background` for disconnected.
- `Border` with `status.caution.background` for query failure.
  See §4 sub-state copy.

#### Loading

- `ui:ProgressRing` per global lock (§4 / §8.1).

#### MainWindow chrome (this brief adds)

- Nav-rail Alerts item carries a **badge** when active alerts
  exist. Treatment open per §8.4 Q8. The badge is drawn in this
  brief's mock so the cross-page perception of "look at Alerts"
  is auditable alongside the page itself (§16.1).

---

## 4. State coverage

Every state below renders in the default (light) theme. The
**Default / steady-state (mixed active + dismissed)** state
additionally renders in **dark** theme so theme-swap behavior is
auditable.

### `state: default` (steady-state, connected, mixed active + dismissed)

Realistic ongoing state once the product has been running. The
feed contains a mix of active and dismissed alerts; the active
set is dominant; dismissed items are present (when the active
filter is `All` or `Dismissed`) but demoted.

Use the six sample instances in §3. Treat instances 1–4 as
active and instances 5–6 as dismissed for this state, so the
mock exercises the active/dismissed visual distinction with the
default filter (`Active`) and also with `All` (showing
dismissed). Render both filter selections in the default state.

### `state: all active` (no dismissed alerts yet, common after first triage)

Filter = `Active`. All visible items are active. Six active
instances. The "you have work to do" state. Severity composition
across the set must read at a glance per Q1.

### `state: all dismissed` (everything has been triaged)

Filter = `Active`. Active set is empty. Default empty-state
copy:

> "Nothing active right now. Everything ZenVizor flagged has
> been dismissed; switch to All or Dismissed to revisit them."

Tone is reassuring, not bare. Treatment distinct from "no alerts
at all" below.

### `state: empty — no alerts at all` (cold start / post-retention)

Filter = `Active`. Zero alerts in the database. Default
empty-state copy:

> "No alerts. ZenVizor will surface anything worth a look here
> when it sees it."

Centered, paired with a reassuring `ui:SymbolIcon`
(`ShieldCheckmark48` recommended; final icon at Claude Design's
discretion).

### `state: filtered to empty` (alerts exist but the active filter excludes them all)

**Distinct from "no alerts at all."** Alerts exist; the active
filter combination hides them. Copy must tell the user the
filter is hiding things and offer a one-action way to reset:

> "No alerts match the current filter. Reset filter to see N
> hidden alert(s)."

Treatment and reset affordance open per §8.4 Q6. Without this
state, fiddling with filters feels like the product lost data.

### `state: loading` (initial `GetAlerts` query in flight)

Default Fluent `ui:ProgressRing` (per global lock §8.1),
centered in the surface that will hold the feed. Indeterminate.
No shimmer. The summary surface (Q1) renders in a placeholder
form using `text.mono` em-dash placeholders for the per-severity
counts.

### `state: disconnected` (named-pipe down)

Banner inline beneath the page header (`Border` with
`status.critical.background`, foreground `status.critical`):

> "Service disconnected. The alert feed is stale and dismiss is
> unavailable until the connection is restored."

Filter affordance remains visually present and operable (filter
state is UI-local). Dismiss affordances on individual items
visually disable.

### `state: error` (any other query failure — NOT pipe-down)

Banner inline beneath the page header (`Border` with
`status.caution.background`, foreground `status.caution.text`):

> "Failed to load alerts: `<msg>`."

Where `<msg>` is the exception type. Standard split per template
§4; no override.

### `state: live arrival — visible to active filter` (sub-state)

`AlertRaised` push delivers a new alert that the user's active
filter would include. The new item appears at the top of the
feed and registers a one-shot visual cue (composition open per
§8.4 Q5). The active-set summary surface increments. Mock shows
the moment immediately after arrival — visible cue still
present, with one of the existing six samples designated as
"just arrived" for the mock.

### `state: live arrival — excluded by active filter` (sub-state)

Same push, but the active filter excludes the new alert (e.g.,
user is on `Dismissed`, or has unchecked the new alert's
severity). The feed body **does not** change. The summary
surface (Q1) and the nav-rail badge (§16.1) **still register**
that something arrived. Without this distinction, filtering
feels like the product is hiding alerts. Mock annotates which
surfaces update and which do not.

### Dark theme

Render the **`state: default`** mock in dark theme as the
auditable variant. Other states are theme-trivial (banners and
backgrounds swap per token semantics).

---

## 5. Tokens in scope

The primer carries the full token table. This section narrows
the palette to what's in scope for Alerts and states the hard
constraints.

> **Precondition check.** All token categories listed here are
> reconciled to brand values per
> `docs/design/colors_and_type.css`. No deferred crosswalk entry
> applies to Alerts.

### `surface.*`

- Mica + contrast rule: any text- or data-bearing card on this
  screen MUST sit on `surface.card` (opaque). Translucent
  `surface.card.alt` forbidden for text-bearing surfaces. The
  canonical metal-card recipe (`metal.card` + `edge.light` +
  `border.card` + `shadow.card` + `radius.card`) applies per
  the project-wide default — see material/effect note below.
- Decorative non-text panels MAY use `surface.subtle` or
  `surface.card.alt` as Claude Design judges fit.

### `text.*`

- Full type ramp is available. Per-screen constraint: any
  path, image name, signer string, IP, byte count, or column-
  aligned digit run uses `text.mono`. Headlines and detail
  prose use the body/strong ramp per primer rules.
- The "why this matters" block uses the body ramp (it's prose,
  not data).

### `accent.*`

- This screen does NOT use filled accent surfaces. `accent.fill`
  text appears nowhere on Alerts. `accent.default` may foreground
  individual elements at Claude Design's discretion (e.g. an
  "expand" affordance, the reset-filter link in the
  filtered-to-empty state), but no large filled-accent surfaces.
  Claude Design has no `accent.fill` work to design.

### `status.*`

Banners and severity expressions on this screen:

- **Disconnected banner** — `status.critical.background` /
  `status.critical`.
- **Error banner** — `status.caution.background` /
  `status.caution.text`.
- **Severity expression per item** (open per §8.4 Q2; uses the
  three status tokens per severity per §3).

State coverage in §4 specifies the *state*; Claude Design picks
the paired bg/foreground token within the primer's mapping.

### `border.*`

Cards take `border.card`. Subtle dividers (e.g. between filter
controls and feed, or between metadata sub-rows) take
`border.subtle`. No raw Wpf.Ui keys.

### `space.*`

Page outer margin is `space.24`. Other gaps choose from the
4-based scale per Claude Design's composition. Per WPF gotcha
in memory (`project_wpf_spacing_token_thickness.md`), Margin
and Padding are literals, not bound to `space.*` resources, in
the implementer's XAML — but the mock annotates token-equivalent
values.

### `radius.*` (role tokens only)

Cards take `radius.card`. Any small chip/pill/button uses
`radius.control`. Banners use `radius.card`. No raw scale tokens.

### Material / effect

The project-wide default for every text- and data-bearing card
on every page is the canonical metal-card recipe: `metal.card`
background + `edge.light` baked-in catch-light + `border.card`
1px stroke + `shadow.card` elevation + `radius.card` corner.
See `docs/design-system.md` §9 "Card surface — canonical
treatment" — this is not a per-brief negotiation.

**For Alerts the open question is composition, not whether to
use the recipe.** Whether each alert item gets its own metal-
recipe card or whether the feed is contained in one outer
metal card with items as inner rows is open per §8.4 Q1. Either
interpretation honors the project default.

Available material/effect tokens: `metal.card`, `edge.light`,
`shadow.card`, `shadow.sm`, `metal.control`. Static gradients +
single `DropShadowEffect` only — no live blur, no animated sheen,
no acrylic on text cards.

### New tokens required by this brief

None. The catalog uses existing tokens.

---

## 6. Chart-chrome — tokens AND behavior spec

**Intentionally n/a:** no chart appears on this screen. Alerts is
a list / item surface, not a visualization surface. The summary
surface in Q1 communicates counts, not series.

---

## 7. Density assignment

- **Default** (Wpf.Ui stock, row 28-equivalent) on the feed.
- **Rationale:** alert items are wrap-multiline rich content —
  headline, multi-sentence detail, optional "why this matters"
  block, metadata, dismiss affordance. DataGrid-style row density
  (`Compact` 22px) does not apply because items are not
  single-row records. The vertical breathing space per item is
  set by the composition Claude Design proposes (per §8.4 Q1)
  rather than by the row density token.
- Filter chip affordances (if Claude Design proposes chips) use
  small-control sizing per `radius.control` + `space.*`
  composition, not Compact DataGrid density.

---

## 8. Locks and open questions for this screen

### 8.1 Global locks

- **Loading = default Wpf.Ui `ProgressRing`**, not skeleton-
  shimmer. Centered in the feed surface, indeterminate. See §4
  loading and design-system §2.
- **High Contrast is handled by a dedicated `HighContrast.xaml`
  ResourceDictionary** merged on system HighContrast activation.
  The mock does not draw HC variants — semantic tokens collapse
  onto `SystemColors.*` keys at runtime. The implementer
  verifies the screen in HC during the per-page verification
  gate (design-system §10).

### 8.2 Screen-specific outcome locks

UX outcomes from the findings review that must not be re-opened
in the mock.

- **Severity-to-token mapping is locked one-to-one.** `Info` →
  `status.neutral`, `Warning` → `status.caution`, `Critical` →
  `status.critical`. Composition (how severity is *expressed*) is
  open per §8.4 Q2; the token mapping is not.
- **Two-surface copy model is locked.** Service-written
  per-instance headline/detail + UI static "why this matters"
  block keyed by `alert.type`. The why-copy must be reachable on
  each item without leaving the page; how it surfaces is open
  per §8.4 Q3.
- **User-facing source-label rendering is locked.** Raw
  `source_monitor` value (`Capture`, `Rollup`) is never visible.
  The label table in §3 is the contract.
- **No-jargon, no-abbreviation rule on user-facing strings.**
  No "ack" / "unack" / field-name / column-name vocabulary
  reaches users. The dismiss vocabulary is the only word for
  closing the loop.
- **Dismiss contract.** One click, no confirmation. Dismissed
  alerts remain accessible via the `Dismissed` and `All`
  filters until retention. Re-raise after cooldown creates a
  new active row; prior dismissed row stays in history.
- **Filter axes and defaults are locked.** State single-select
  default `Active`; Severity multi-select default all on; Type
  multi-select default all on. AND across, OR within.
- **The filter-by-type control lists every catalog type even in
  Phase 6** when only `UnsignedFromUserPath` has a producer. The
  vocabulary is consistent; no Phase-6-only special case ships.
- **Live-arrival treatment is one-shot, not continuous.** Eye-
  catching for ~200ms one direction (per primer light-and-fast
  principle); no animated highlight, no continuous pulse.
  Composition is open per §8.4 Q5; the no-continuous-animation
  constraint is not.

### 8.3 Boundary-case overrides of hard rules

**Intentionally n/a.** This screen does not override:

- §11 (discovery > ranking): the feed is reverse-chronological
  and uncapped server-side; the filter narrows by user intent,
  not by score. Compliant.
- §12 (honest attribution): no per-PID byte surface on alert
  items; per-instance detail strings reference apps and may name
  svchost-hosted services using the bracketed convention
  inherited from Dashboard. Compliant.
- §13 (passive-only): non-overridable. No active affordances on
  this screen. Dismiss is UI-state, not a network or process
  action. Compliant.
- §4 disconnected / query-failed split: default split applied;
  no merge. Compliant.

### 8.4 Open design questions for Claude Design

Eight questions. For each, propose the requested number of
variants; the user picks one per question during iteration.

#### Q1 — Card surface model

**Open:** how should the canonical metal-card recipe apply to a
feed of alert items? Two reasonable interpretations: each alert
is its own metal-recipe card, **or** the feed sits inside one
outer metal card with items as inner rows.

**Constraints:** the metal recipe applies somewhere per
project default (§5); whichever model wins must support the
visual distinction between active and dismissed items (§8.2);
density and scan-rhythm must serve Q1 ("see the active set at a
glance"); must not break at narrow window widths.

**Variants:** propose 2.

#### Q2 — Severity expression at item level

**Open:** how does each item signal `Info` / `Warning` /
`Critical` so the user reads severity at a glance? Candidates:
left bar, dot/pill, badge near headline, background tint, icon
tint, combinations.

**Constraints:** uses the three status tokens per the locked
mapping; must work in light and dark; must not rely on color
alone (HC variant collapses onto SystemColors); must remain
legible when the item is in the dismissed visual state.

**Variants:** propose 2–3.

#### Q3 — Where the "why this matters" framing lives per item

**Open:** the static type-level "why this matters" block (§3,
multi-sentence paragraph per type) explains why the observation
is worth attention. Where does it surface?

Candidates: always-on inline beneath the detail; expand-on-
click; persistent side-rail when an item is selected; "?"
affordance that opens a popover; hover-revealed.

**Constraints:** must be reachable on each item without leaving
the page; must not bury the per-instance detail (the *facts*
the user acts on); must not require chrome that breaks at
narrow window widths; must work for screen readers (always-on
or affordance with accessible name).

**Variants:** propose 2–3.

#### Q4 — Filter affordance composition

**Open:** three filter axes (State single-select default
`Active`; Severity multi-select default all on; Type multi-
select default all on). How does the page expose them?

Candidates: chips row across the top, filter bar with grouped
controls, segmented control for State + multi-select dropdowns
for Severity/Type, left filter rail, combinations.

**Constraints:** AND across axes, OR within axis; one-action
reset to defaults must be obvious; must remain readable at
narrow widths; State, Severity, and Type may share a metaphor
or differ. Filters persist within session only — composition
need not visualize cross-session memory.

**Variants:** propose 2–3.

#### Q5 — Live-arrival treatment

**Open:** how does the user perceive a new alert arriving via
push?

**Constraints:** must catch the eye (otherwise push is
invisible); ~200ms one-shot, not continuous (light-and-fast);
must work for both sub-states in §4 — visible to active filter
(feed body updates) and excluded by active filter (feed body
does not update, but summary surface and nav badge still
register).

**Variants:** propose 1–2.

#### Q6 — Filtered-to-empty treatment

**Open:** how does the page tell the user the filter is hiding
everything and offer a one-action reset?

**Constraints:** copy must read distinctly different from "no
alerts at all" (different empty-state literals in §4); reset
must be one action; the hidden count must be communicable so
the user knows what they're missing.

**Variants:** propose 1.

#### Q7 — Display name for `UnsignedFromUserPath` (and pattern for future types)

**Open:** the user-facing string for the type appears in the
filter affordance and wherever an item is tagged with its type.
Pre-proposed candidates for Claude Design to pick from or
propose a fourth:

- "Unsigned program"
- "Unsigned from user folder"
- "Suspicious unsigned binary"

The chosen string sets the **pattern** for naming the five
roadmap types when they ship; the brief return locks the
approach so the catalog's per-type display-name table updates
in lockstep.

**Constraints:** plain English; no jargon; describes the
observation, not a verdict (no "malware," no "threat"); short
enough for filter chip labels; consistent verbosity across the
catalog.

**Variants:** propose 1 with rationale; alternative if Claude
Design sees a stronger case.

#### Q8 — Nav-rail badge treatment

**Open:** when active alerts exist, the Alerts nav-rail item
carries a badge readable from any other page. How is it
composed?

Candidates: numeric count, dot, count + severity tint (highest
active severity wins), animated pulse on new arrival (one-shot,
not continuous), combinations.

**Constraints:** must fit inside the nav-rail item without
bumping layout; severity tint uses the locked status tokens;
one-shot animation only (no continuous pulse); the badge is
the cross-page surface for the live-arrival excluded-by-filter
sub-state, so it must register a moment-of-arrival distinctly
even when the user has filtered the page itself to hide the new
item.

**Variants:** propose 2.

---

## 9. Annotation work specific to this screen

**Intentionally n/a: no new tokens, no renames.** Alerts
composes entirely within the existing token surface. The
catalog's why-copy blocks and source labels live in a UI string
table (`WhyCopyResources.xaml` or equivalent) that's a
resource-file content addition, not a token addition; the
mockup does not need to annotate that file — only the tokens
that paint the rendered strings.

---

## 10. Per-screen WPF translation gotchas

- **`ui:NavigationView` wraps each page in a
  `DynamicScrollViewer`** — hosted pages get infinite vertical
  extent. The Alerts feed ListView must have its `MaxHeight` set
  programmatically on `Loaded` + `SizeChanged` so the inner
  `VirtualizingStackPanel` virtualizes. Without this the
  ListView materializes every item at once and breaks under
  load. *Memory: `project_wpfui_navigationview_scrollviewer.md`.*
- **`NavigationCacheMode.Enabled`** on every nav-rail item — the
  `AlertsPage` instance survives nav away/back, so `Loaded` does
  NOT refire on return. The `AlertRaised` push subscription
  must hang off the Page constructor, NOT `Loaded`, or the page
  silently stops receiving live updates after the first nav-away.
- **Variable-height items + virtualization.** If the design Q3
  expand affordance grows item height, the ListView must use
  pixel-unit scrolling (`VirtualizingPanel.ScrollUnit=Pixel`) or
  `VirtualizationMode=Standard` rather than item-unit + recycling.
  Item-unit + recycling assumes uniform heights and corrupts
  scroll position when row heights vary. The brief notes this
  so an "expand inline" Q3 variant doesn't break virtualization
  silently.
- **Filter persistence within session is page-local state.**
  Filters live on the `AlertsPage` view-model and survive
  nav-away/back because the page is cached. They reset on app
  restart because the page instance is reborn. No persistence
  layer wiring needed.
- **Toast activation deep-link.** When the user clicks a Win11
  toast for an alert, the activation handler navigates to
  `AlertsPage`, **resets the filter to defaults**, and scrolls
  to the activated alert id. Per catalog §5.3 — this is locked
  behavior, not a design call. The mock does not need to render
  toast click — but the brief notes the filter reset so a Q4
  variant that uses sticky filter state doesn't accidentally
  break the deep-link.
- **Margin / Padding tokens are literals, not bindings**
  (memory `project_wpf_spacing_token_thickness.md`). The mock
  annotates token-equivalent values (e.g. `Margin="24" /* space.24 */`);
  implementer uses literals.
- **No DataGrid on this screen** — the rendering memory about
  grid-cell descender clipping
  (`project_wpf_layout_clip_grid_cells.md`) does not apply.
  Alert items are wrapped multi-line text inside a `ListView`
  ItemTemplate; descender clipping is not a risk for `TextBlock`
  with `TextWrapping=Wrap`.

---

## 11. Discovery > ranking — per-screen application

The Alerts feed is **reverse-chronological**, not ranked. There
is no top-N cap. The full set within the active filter is
returned and rendered (virtualized). The filter narrows by user
intent — state, severity, type — not by score, importance, or
recency-other-than-natural.

The MainWindow nav badge (§16.1) shows the **count** of active
alerts (or a dot — composition open per §8.4 Q8), not "top 10
most important alerts." Count is a discovery surface, not a
ranking one.

This screen does not override the rule.
*Memory: `project_discovery_principle.md`.*

---

## 12. Honest attribution — per-screen application

- **`svchost` co-hosting.** If an alert is raised against an app
  whose image is `svchost.exe`, the per-instance headline and
  detail name the hosted service(s) using the bracketed
  convention inherited from Dashboard
  (`svchost.exe [Service1, Service2]`). No per-service byte
  attribution is implied. The catalog's title/detail templates
  reference `image_name` and the producer is responsible for
  rendering the bracketed form where applicable. The design
  carries no per-service decoration of its own.
- **Host-process attribution for injected code / LOLBins.** Not
  visualized. The documented boundary is preserved without UI
  hints, asterisks, or caveat icons. If the rendered facts in
  `alert.detail` would mislead (e.g. an alert attributed to
  `powershell.exe` that was actually injected behavior), that is
  a producer-side concern, not a design one — the design renders
  what the catalog hands it.
- **Per-PID byte surface.** Some `alert.detail` strings include
  byte totals (`UnusualDailyVolume`: "8.4 GB today against a
  typical 1.9 GB"; `OutboundHeavy`: "sent 4.1 GB and received
  78 MB"). These are app-level rollup totals, not per-PID
  decoration of a row, and they're sentence-embedded, not
  column-aligned. No per-PID byte column appears on alert items.

This screen does not override the rule.

---

## 13. Passive-only — per-screen application

- **No kill / block / quarantine / "stop this app" /
  right-click action menu / hover-revealed action buttons.** The
  mock MUST NOT include them on alert items.
- **Dismiss is UI-state, not a network or process action.** It
  marks the alert as seen for the user; it does nothing to the
  underlying process or connection. The brief calls this out so
  Claude Design's "dismiss" affordance is not styled as a
  destructive action (e.g. a trash icon implying "delete this
  threat"). Recommended framing: a checkmark or "done" semantic,
  not a "kill" or "remove" semantic.
- **Drill affordance.** An alert that references `entity_kind =
  App` may navigate to `AppDetailPage` for that `app_id` on item
  click or via a "View app" affordance. This is passive
  (visualization-only) and allowed. The exact drill affordance
  composition is open within §8.4 Q1 / Q2 — but a drill from
  alert to App Detail is a permitted MVP behavior. The brief
  notes this so Claude Design can propose the affordance as part
  of its item composition rather than treating it as forbidden.

**Passive-only is non-overridable** — no screen may carry an
active affordance regardless of product circumstance.

---

## 14. Performance budget — per-screen application

- **No shimmer / live blur / continuous animation** on this
  screen. Loading is the static `ProgressRing`. Item
  composition uses static gradients + single `DropShadowEffect`
  for the metal recipe — no animated sheen.
- **Live-arrival cue is one-shot, ~200ms.** Not continuous.
  Specified in §8.2 lock; called out again here so the §8.4 Q5
  variants honor it.
- **No new polling cadence.** The page is push-driven —
  `AlertRaised` notification + on-demand `GetAlerts` on page
  enter and filter change. No periodic polling.
- **Filter application is client-side and instant for the
  visible page.** Filters operate on the in-memory feed
  returned by `GetAlerts`; no per-filter-change IPC round-trip.
  Server-side filter is the `state` axis only (and the
  retention boundary); severity and type filters apply
  in-memory after the query returns.
- **Item virtualization is the implementer's job.** §10 covers
  the WPF gotchas. The brief notes that an "expand inline" Q3
  variant requires pixel-unit virtualization so it doesn't burn
  through layout cost on each expand.

---

## 15. Out-of-scope — features flagged for later

Lifted from the findings doc §7.

- **Multi-select dismiss** — bulk action; feature.
- **Filtering by `source_monitor` or specific `entity`** — adds
  axes beyond the locked three; feature.
- **Filter persistence across app restart** — within-session
  only for MVP; cross-session = feature.
- **Dedicated alert detail page or modal** — inline surfacing
  covers MVP. If Claude Design proposes a flyout for some
  catalog entries (e.g. future `Device` alerts with rich
  detail), it inherits the locked flyout rules (opaque
  `surface.layer`, per design-system §3 flyout rules) — but for
  Phase 6 the detail and why-copy surface inline per §8.4 Q3.
- **Mute / snooze by type** — feature (would land an
  `alert_mutes` table).
- **Sound or custom notification beyond Windows toast.** Toast
  is the OS-canonical surface; sound = feature.
- **Forward / share alert (email, copy-link).** Email is hard
  NO per zero-own-traffic. Copy-to-clipboard is fine but
  feature.
- **Active-action affordances on alert items** — no "kill this
  process," no "block this app," no "quarantine." Passive-only
  invariant — HARD NO (§13).
- **Settings overlap.** The toast-routing controls (severity
  floor ComboBox + "Notify on large downloads" toggle, per
  catalog §5) live on the Settings page, not on the Alerts
  page. The Alerts page does not own notification
  configuration.

---

## 16. Chrome / cross-screen consequences

### 16.1 MainWindow nav-rail Alerts badge (NEW — designed in this brief)

- **What changed:** the Alerts nav-rail item carries a badge
  when active alerts exist. Treatment composition open per
  §8.4 Q8.
- **Where it lives:** `src/ZenVizor.Ui/MainWindow.xaml` — the
  `NavigationView` Alerts `MenuItem` template.
- **Propagates to:** no other screen designs this surface;
  Alerts owns the badge end-to-end.
- **Per-screen brief work needed elsewhere:** none — the badge
  reads from the same view-model state that drives the page's
  active-set summary. No other brief redesigns it.
- **Mock requirement:** the badge is drawn in this brief's
  mock alongside the page itself (in the MainWindow chrome
  framing of the page-level layouts) so the cross-page "look
  at Alerts" perception is auditable. Render at least two
  badge states in the mock: zero active (no badge) and
  multiple active (badge with composition per Q8 variants).

### 16.2 Settings page Alerts section (inherited by Settings brief)

- **What:** Settings page Alerts section carries two controls
  per catalog §5.1 — a severity floor ComboBox ("Show desktop
  notifications for…") and a "Notify on large downloads"
  toggle.
- **Where it lives:** `src/ZenVizor.Ui/Views/SettingsPage.xaml`
  (does not exist yet; ships in Phase 6).
- **Propagates to:** Settings brief (when written) — those
  controls are spec'd in the catalog and the Settings brief
  inherits them.
- **Per-screen brief work needed elsewhere:** Settings brief
  designs the controls' composition; the Alerts brief does not
  re-spec them.

### 16.3 Reports → Alerts deep-link wire-up (inherited from Reports brief §16.2)

- **What:** Reports' "Notable today" incident cards carry an
  `Alerts · #N` chip that, in Phase 6, becomes a click
  navigating to the Alerts feed scrolled to the matching
  `Alert.Id`. Phase 5 ships the chip inert per sprint plan
  §6.
- **Where it lives:** Reports' code-behind — the chip exists
  in the Reports XAML already.
- **Propagates to:** Alerts page handles the deep-link target
  (navigate-to-alert-id + filter reset, the same path the
  toast click takes per §10).
- **Per-screen brief work needed elsewhere:** none — Alerts
  inherits the deep-link contract; Reports does not redesign
  its chip.

### 16.4 Catalog source-label rendering (Alerts-only)

- **What:** the source-label lookup table in §3 is rendered
  only on the Alerts page (and embedded in `zvctl alerts list`
  CLI output, which the design does not own). No other screen
  surfaces `source_monitor` values.
- **Where it lives:** the WhyCopyResources string table (or
  equivalent) and the per-item template on `AlertsPage.xaml`.
- **Propagates to:** none.
- **Per-screen brief work needed elsewhere:** none.

---

## 17. Deliverables expected from Claude Design

The mock must contain:

- Layouts for every state in §4 in the default (light) theme,
  annotated per `docs/design-mockup-template.md`. State
  coverage: default, all-active, all-dismissed, empty-no-alerts,
  filtered-to-empty, loading, disconnected, error, live-arrival
  (both sub-states). Default state additionally renders the
  filter on both `Active` and `All` to exercise the
  active/dismissed visual distinction.
- ONE steady-state layout (the **default** state, mixed active
  + dismissed) additionally rendered in **dark** theme so
  theme-swap behavior is auditable.
- Variant proposals for each open question in §8.4. The user
  picks one variant per question during iteration; the final
  mock at session end carries the selected variant clearly
  indicated.
- Token annotations using canonical dotted names (§9 — no new
  tokens for this brief).
- Density tags where density differs from default (§7 — Default
  Wpf.Ui density on the feed; smaller controls per Claude
  Design's composition).
- Layout hints (`MaxHeight` on the feed `ListView`, `scroll:
  page` vs `scroll: pane` notes) per §10.
- Chrome elements drawn in the mock for reconciliation per §16
  — specifically the MainWindow nav-rail Alerts badge in at
  least the zero-active and multiple-active states.
- The interim placeholder treatment AND the eventual functional
  layout per §18.

The pre-handback checklist
(`docs/design-briefs/_return-process.md`) restates these
deliverables as a paste-ready prompt for Claude Design to
self-verify against at session end.

---

## 18. Provisional / two-states

### (a) Interim placeholder treatment

What ships in the shipped polish-interlude app between now and
Phase 6, so the page doesn't look unfinished next to the four
polished screens.

- Centered on `surface.background` (no card surrounding the
  group).
- Hero icon — `ui:SymbolIcon Symbol="Alert48"` foreground
  `text.tertiary`.
- Title — `text.title.large` "Alerts".
- Body — `text.body.large` `text.secondary`:
  > "Coming in Phase 6 — feed of observations ZenVizor flagged,
  > with a way to mark each one as handled."
- Optional caption — `text.caption` `text.secondary`:
  > "The first alert wired up is for unsigned programs from
  > user-writable folders making network connections."

Tone: deliberate "this is coming," not the working layout with
dummy data.

### (b) Eventual functional layout

The full Phase 6 layout addressing §3 and §4. The brief asks for
both states side by side so the polish-interlude promise reads
as continuous with the functional state.

### Provisional-data flag (locked at Phase 6 implementation)

The brief **locks** the durable layers (visual language, IA,
the four user questions in §3, severity-to-token mapping, the
two-surface copy model, the source-label rendering, the no-
jargon rule, the dismiss contract, the three filter axes with
defaults and composition rules, the state matrix).

The brief **flags as provisional**, to be reconciled at Phase 6
implementation:

- Catalog roadmap entries (`InvalidSignature`,
  `FirstRunWanTalker`, `UnusualDailyVolume`, `LargeDownload`,
  `OutboundHeavy`) are design-vocabulary today; producers ship
  post-MVP. Wording of why-copy and detail templates may iterate
  before their producers ship.
- Type-to-icon mapping. The brief proposes icons for each
  catalog entry; the lookup contract is the lock, individual
  icon choices are not.
- The user-facing display name per catalog type (per §8.4 Q7).
  The Phase 6 implementation lands the chosen pattern in a
  catalog table; the brief return locks the approach.
- The exact filter affordance, severity expression, why-copy
  surfacing, dismiss affordance, live-arrival treatment,
  filtered-to-empty treatment, and nav-badge treatment per the
  §8.4 questions. The brief return locks each.

---

## 19. Hand-off back to Claude Code

Mockup → annotated tokens → Claude Code re-implements as
idiomatic XAML against Wpf.Ui. Nothing in the mock is portable;
the tokens are the contract.
