# Epic B — Alert noise + gating

**Release:** 1.4.0 (minor) — shipped **complete** (gating + per-severity toggles),
bundled with Epic D
**Status:** spec (gating phase) · proposed (toggles phase)
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
override the defaults. Both phases ship together in 1.4.0, so the
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
- **Toast gate.** Wrap the `Tray.ShowNotification` call in
  `OnAlertRaised` in a per-severity default check (Critical-only). Keep
  `_toastEnabled` as the master on/off. The gating phase hard-codes the
  defaults; the toggles phase replaces them with settings-backed toggles.
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

## Open decisions

1. **Baseline first-run: gate at raise or at surface?** Raise-gating (don't
   even record day-one false first-runs) gives a clean day-one feed but is not
   "surfacing-only." Surface-gating (record to feed, suppress badge/toast) is
   audit-honest but leaves baseline entries in the day-one feed.
   **Recommendation:** raise-gate the setup-scan-seeded / within-window
   first-runs specifically — they are false positives an "is this app new?"
   test cannot distinguish — while keeping the general surfacing-over-raising
   posture for everything else.
2. **Window length / tunability.** 48 h chosen (user decision). Const, or a
   `baseline.window_hours` settings key for tuning.

## Acceptance criteria

- **Fresh-install simulation** (synthetic `ICaptureSource`: N pre-existing
  apps each opening a WAN connection within 60 s of capture start): zero Info
  first-run toasts; no Info first-run badge spike; **Critical**
  unsigned-from-user-path still fires *and* toasts.
- **No permanent disable:** an app genuinely first-seen *after*
  `install_epoch + 48 h` that talks within 60 s of its `first_seen` fires
  `FirstRunWanTalker` normally.
- **Toast storm:** with default settings, only Critical produces a toast;
  Info and Warning do not.
- **Self-monitoring:** point the tool at itself; the setup-scan produces no
  outbound from ZenVizor's own processes (invariant 1).

## Version classification

**1.4.0 (minor).** The gating phase on its own is a behaviour-default
correction (a corrected false-positive attribution edge case +
toast-surfacing defaults, no new surface), but it ships **bundled** with the
toggles phase — per-severity toast toggles in Settings, new controls + new
`SettingsSnapshot` / `SettingsUpdate` fields — so the release adds surface and
is a minor. Shipped complete, with Epic D, in 1.4.0.
