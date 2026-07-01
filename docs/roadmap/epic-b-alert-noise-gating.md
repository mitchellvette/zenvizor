# Epic B — Alert noise + gating

**Release:** 1.2.0 (minor) — shipped **complete** (gating + per-severity toggles),
standalone
**Status:** shipped
**Depends on:** nothing
**Build order (internal to this release):** the gating phase (defaults +
baseline) is independent of the toggles phase; build gating first, then layer
the per-severity Settings toggles on top.

---

## Summary

A fresh install produces an alert flood. Every program already on the machine
gets a `first_seen` of "now" the moment ZenVizor first observes it, so the
`FirstRunWanTalker` rule fires an **Info** "newly-installed program reached the
network" alert for Chrome, Teams, svchost, and everything else that talks
within 60 s of capture starting. With no per-severity toast gate, that is also
a toast storm and a nav-badge spike on day one.

These are **false** first-runs: the apps are not new — ZenVizor is. The
**gating phase** corrects the day-one noise (defaults + install baseline + a
setup-scan seed). The **toggles phase** adds per-severity Settings toggles to
override the defaults. Both phases ship together in 1.2.0, so the
behaviour-default change never reaches the user without the opt-out control in
the same release.

## Current behavior (verified)

- **Toast:** `MainWindow.OnAlertRaised` (`MainWindow.xaml.cs:391-435`) toasts
  **every** severity when `_toastEnabled` (default true, seeded from
  `settings` key `toast.on_alert='1'`). The nav badge increments per severity
  (`_badgeCritical` / `_badgeWarning` / `_badgeInfo`). There is no
  per-severity gate.
- **`FirstRunWanTalkerRule`** (Info): fires when
  `0 <= (FlushTime - apps.first_seen) <= FirstRunWindowMs (60 s)` and there is
  a WAN connection. Permanent per-app cooldown (`long.MaxValue/2`). Reads
  `AppFirstSeenUnixMs`, which `AlertProducer.OnSessionConnectedWan` enriches
  from `AppFirstSeenRepository.GetFirstSeenUnixMs` (`AlertProducer.cs:112`).
- **`UnsignedFromUserPathRule`** (Critical): Unsigned + user-writable path +
  WAN, 24 h cooldown. This is the security-critical signal and must keep
  firing throughout.
- **Schema:** `apps(first_seen, last_seen, ...)` has **no** `is_baseline`
  column. `settings` is a key/value table (`001_initial.sql:156`) — adding a
  key is an `INSERT`, **not** a DDL migration. (`is_known` exists only on the
  reserved `devices` table, not on `apps`.)

## Scope — gating phase

Three complementary corrections, **no new control**:

1. **Toast severity-gating defaults.** Only **Critical** toasts by default;
   Warning and Info do not. Kills the toast storm. (The toggles to *override*
   these defaults are the toggles phase, not the gating phase.)
2. **Install baseline window (~48 h).** Treat the first-run / new-app signal
   as unreliable during the settling period after install. An app whose
   `first_seen` falls inside the window is *baseline-known* and does not trip
   `FirstRunWanTalker`.
3. **Running-process setup-scan seed.** At first service start, enumerate
   currently-running processes and ensure their `apps` rows exist with
   `first_seen = install epoch`, so already-present apps fall under the
   baseline window immediately instead of waiting to be observed talking.

**Out (gating phase):** the per-severity Settings toggles (→ toggles phase,
same release); a full-disk scan (rejected — performance budget; user-agreed).

## Scope — toggles phase

Three independent user-visible controls on the Settings page that replace the
gating-phase's hard-coded per-severity defaults with per-user preferences.
Ships in the same release so a user who wants a Warning or Info toast can
opt in without reverting to the pre-gating flood.

1. **Three independent Settings toggles.** Replace the single "Show desktop
   notifications" row (`SettingsPage.xaml:496-507`) with three peer
   `ui:ToggleSwitch` rows — Critical / Warning / Info — each an independent
   user preference. No master. Defaults on fresh install match the
   gating-phase defaults: Critical on, Warning + Info off.
2. **Legacy-intent-preserving migration.** A user who upgrades from
   1.1.x with `toast.on_alert = '1'` (the pre-gating default) had opted
   into toasts for all severities; the migration honours that by seeding
   all three per-severity keys to `'1'`. `toast.on_alert = '0'` seeds all
   three to `'0'`. Users can then narrow the set via the new toggles.
   This means the gating-phase Critical-only default only applies to
   truly fresh installs and never silently disables notifications a
   1.1.x user had come to rely on.
3. **IPC contract — extend, don't replace.** `SettingsSnapshot` gains
   `ToastOnCritical` / `ToastOnWarning` / `ToastOnInfo` (three new
   trailing booleans, additive per the IPC schema rules).
   `SettingsUpdate` gains three nullable siblings. The existing
   `ToastOnAlert` field is retained: on read, it reports the OR of the
   three (any-severity-enabled); on write, it sets all three at once.
   That keeps older UIs functional against a new service and vice-versa
   without a schema-version bump.
4. **Toast gate reads per-severity settings.** MainWindow's cached
   `_toastEnabled` bool becomes three fields —
   `_toastCritical` / `_toastWarning` / `_toastInfo` — hydrated from the
   snapshot on connect and re-seeded by `SettingsPage` on change
   (mirrors the existing `SetToastEnabled` pattern at
   `MainWindow.xaml.cs:311`). The Critical-only default that the gating
   phase hard-coded is now driven by the settings values, so the
   gating-phase's temporary hard-code is deleted.

**Out (toggles phase):** any change to the nav-rail badge (badge is
"count of unseen" — orthogonal to "notify me"); a master on/off
control on top of the three toggles (degenerate state: master on but
all three severities off ≡ master off); per-alert-*type* toggles
(different axis; not in this epic).

## Design — zero DDL

- **`baseline.install_epoch_ms`** — a new `settings` key written once on first
  service start. Key/value table → no schema migration → stays a patch.
- **Baseline gate.** `FirstRunWanTalker` (or the producer ahead of it)
  suppresses the Info first-run signal for any app whose
  `first_seen <= install_epoch_ms + baselineWindow (48 h)`. The Critical rule
  is untouched.
- **Setup-scan.** At first start, enumerate running processes via the existing
  Attribution machinery (local only — see invariant guard) and upsert their
  `apps` rows with `first_seen = install_epoch_ms`. This makes every
  already-present app baseline-known at once.
- **Toast gate (gating phase).** Wrap the `Tray.ShowNotification` call
  in `OnAlertRaised` in a per-severity default check (Critical-only).
  Keep `_toastEnabled` as the master on/off. The gating phase
  hard-codes the defaults so slice-1 can ship + validate independently.
- **Toast gate (toggles phase).** Replace the hard-codes with three
  settings-backed booleans — `_toastCritical` / `_toastWarning` /
  `_toastInfo` — hydrated from `SettingsSnapshot` on connect + reseeded
  by `SettingsPage` on toggle change. `_toastEnabled` (the legacy
  master) becomes a computed OR of the three fields for back-compat.
- **Settings-key seeding on upgrade (toggles phase).** On first service
  start after upgrade, if the per-severity keys
  (`toast.on_alert.critical` / `.warning` / `.info`) are absent, migrate
  from the legacy `toast.on_alert`: `'1'` → all three `'1'`,
  `'0'` → all three `'0'`. Fresh installs seed
  `('1', '0', '0')` directly so the gating-phase defaults hold. The
  migration runs once and is idempotent (guarded by presence-check on
  the target keys).
- **Why no `apps.is_baseline` column.** The install-epoch settings key plus
  the existing `first_seen` comparison delivers baseline-known with **zero
  DDL** — no migration, no rebuild, no per-row backfill of an existing
  `apps` table. The release is a minor regardless (the toggles add surface),
  but avoiding a schema change keeps the gating phase low-risk and trivially
  reversible.

## Invariant guards

- **Critical keeps firing.** Gating touches only the Info first-run signal;
  `UnsignedFromUserPathRule` fires throughout the baseline window. (Roadmap
  lock — a dropper installed 10 minutes post-install is still caught by the
  Critical rule.)
- **Surfacing vs. raising.** Prefer gating *surfacing* (toast/badge) over
  suppressing the *raise/record*, to preserve the feed as an audit trail
  (discovery-over-ranking). **Exception:** for an app demonstrably present at
  install (setup-scan seeded / within the baseline window), `FirstRunWanTalker`
  is a *false positive* — the app is not new — so not raising it is *correct
  attribution*, not suppression.
- **Invariant 1 (zero own network).** The setup-scan enumerates **local**
  running processes only (no sockets, no DNS, no probes). Verify under the
  self-monitoring gate.
- **Performance budget.** Running-process enumeration only, one-shot at first
  start. No full-disk or per-event work.

## Resolved decisions

1. **Baseline first-run: gate at raise or at surface?** **Raise-gate**
   the setup-scan-seeded / within-window first-runs specifically —
   they are false positives an "is this app new?" test cannot
   distinguish — while keeping the general surfacing-over-raising
   posture for everything else.
2. **Window length / tunability.** 48 h. **Const**, not a tunable
   settings key; revisit if user data suggests it (e.g. a slow-drip
   installer that keeps unpacking past 48 h).
3. **Toggles UI shape.** Three **independent** peer toggles (no master).
   Cleaner mental model, no degenerate state, 1:1 mapping to the three
   new settings keys.
4. **Legacy `toast.on_alert` intent on upgrade.** **Honour** the prior
   value — `'1'` → all three per-severity keys on, `'0'` → all off.
   Users who had opted into all toasts keep them; users who had opted
   out stay out. Fresh installs still get the Critical-only default.
5. **Nav-rail badge.** **Not touched.** Badge counts unseen across all
   severities; toggles affect notifications only.

## Acceptance criteria

### Gating phase

- **Fresh-install simulation** (synthetic `ICaptureSource`: N pre-existing
  apps each opening a WAN connection within 60 s of capture start): zero Info
  first-run toasts; no Info first-run badge spike; **Critical**
  unsigned-from-user-path still fires *and* toasts.
- **No permanent disable:** an app genuinely first-seen *after*
  `install_epoch + 48 h` that talks within 60 s of its `first_seen` fires
  `FirstRunWanTalker` normally.
- **Toast defaults:** on a fresh install, only Critical produces a toast;
  Info and Warning do not.
- **Self-monitoring:** point the tool at itself; the setup-scan produces no
  outbound from ZenVizor's own processes (invariant 1).

### Toggles phase

- **Fresh-install seed:** first service start on a clean DB seeds the
  three per-severity keys `('1', '0', '0')`; the toggle UI reflects
  Critical=on, Warning=off, Info=off.
- **Legacy migration — enabled:** upgrade from a 1.1.x install with
  `toast.on_alert = '1'` and no per-severity keys → per-severity keys
  seeded as `('1', '1', '1')`; the user continues to receive toasts
  for every severity as before.
- **Legacy migration — disabled:** upgrade from a 1.1.x install with
  `toast.on_alert = '0'` → per-severity keys seeded as `('0', '0', '0')`;
  no toasts fire until the user re-enables one.
- **Per-severity gate honours settings:** with `(1, 0, 0)`, a synthetic
  Critical raise fires a toast; a synthetic Warning + Info raise do
  not. With `(0, 1, 0)`, only Warning fires.
- **Toggle round-trip:** flipping the Warning toggle in Settings sends
  a `SettingsUpdate` with only `ToastOnWarning` set; the next snapshot
  reflects the change; MainWindow's cache is reseeded within one push.
- **Legacy field back-compat:** an old UI reading a new service's
  snapshot sees `ToastOnAlert = (critical OR warning OR info)`; a new
  UI writing `ToastOnAlert = false` clears all three per-severity keys.
- **Nav-rail badge unchanged:** flipping toggles off does not change
  the badge count — badge continues to count unseen alerts across all
  severities.

## Version classification

**1.2.0 (minor).** The gating phase on its own is a behaviour-default
correction (a corrected false-positive attribution edge case +
toast-surfacing defaults, no new surface), but it ships **bundled** with the
toggles phase — per-severity toast toggles in Settings, new controls + new
trailing `SettingsSnapshot` / `SettingsUpdate` fields — so the release adds
surface and is a minor. Trailing-nullable additions to the two settings DTOs
are additive per `docs/versioning.md` and do not require an
`IpcSchemaVersion` bump. Ships standalone (not bundled).
