# Phase 2 — Plan

**Status:** Open questions resolved 2026-06-01 — proceeding with implementation
**Last updated:** 2026-06-01
**Prerequisite:** Phase 1 complete (commit `8cda9ca`, CI green)

## Resolved open questions (2026-06-01)

All ten §2 questions accepted at the proposed defaults:

| # | Decision |
|---|---|
| Q1 | Signature cache key: per `(image_path, mtime, size)` |
| Q2 | svchost service-list: snapshot at session open, never refresh |
| Q3 | svchost API: native SCM (`EnumServicesStatusEx` + `QueryServiceConfig`) |
| Q4 | `is_user_writable_path`: prefix match against `%TEMP%` / `%LOCALAPPDATA%` / `%APPDATA%` / `%USERPROFILE%\Downloads` / `%PUBLIC%` |
| Q5 | `signature_status` ∈ {Signed, Invalid, Unsigned, Unchecked}, mapped from `WinVerifyTrust` return codes |
| Q6 | Self-signed: follow `WinVerifyTrust` — if local machine trusts the chain, `Signed` |
| Q7 | Enrichment timing: synchronous in `SqliteFlushSink`, per-`(path,mtime,size)` cache |
| Q8 | Publisher change → new app row (PRD §7.1 dedup key) |
| Q9 | No Phase 6 alert plumbing in Phase 2 — strict scope |
| Q10 | Yes, backfill existing `Unchecked` apps rows on first Phase 2 start. Implementation note: backfill runs **synchronously between migrate and capture-monitor start** so it cannot race with new-session inserts. The "batched in groups of 10 with 100 ms sleeps" guidance is preserved as a smoothing measure for the WinVerifyTrust workload, not as concurrency protection. |

> **For a new Claude Code session picking this up cold:** start by reading this
> file top-to-bottom. Surface §2 (Open Questions) to the user and **wait for
> answers** before touching code. The proposed defaults in §2 are reasonable
> starting points but each materially affects how the product detects
> unexpected network traffic — the user must make the call.

---

## 1. Cold-start context

### 1.1 Where we are now

The repo at commit `8cda9ca` has Phase 0 (scaffold/IPC/migrator) and Phase 1
(ETW capture + PID correction + flushing aggregator) shipped. CI is green on
windows-latest. 92 headless tests pass:

- `TitaniRun.Core.Tests` (53) — domain types, classification, aggregation, session tracking
- `TitaniRun.Storage.Tests` (16) — migrator + `SqliteFlushSink` end-to-end
- `TitaniRun.Ipc.Tests` (11) — version negotiation + round-trip
- `TitaniRun.Attribution.Tests` (8) — PID correction across all scenarios
- `TitaniRun.Integration.Tests` (4) — synthetic capture → real SQLite, includes
  the architectural guard *"Observe() must not write to disk"*

### 1.2 What's already in place that Phase 2 builds on

| Concern | Where it lives | Phase 2 hooks here? |
|---|---|---|
| App identity record | `src/TitaniRun.Core/Storage/AppIdentity.cs` | **Yes** — Phase 1 fills `Publisher=null`, `SignatureStatus="Unchecked"`, `IsUserWritablePath=false`. Phase 2 populates them. |
| Process image resolver | `src/TitaniRun.Attribution/RealProcessImageResolver.cs` | **Yes** — `AppIdentity` is built from `ProcessImageInfo` in `SessionTracker.ToAppIdentity`. Refactor target. |
| Session tracker | `src/TitaniRun.Core/Aggregation/SessionTracker.cs` | **Yes** — `ToAppIdentity` is the construction site. Needs to accept an enrichment lookup. |
| Flush sink | `src/TitaniRun.Storage/Repositories/SqliteFlushSink.cs` | **Yes** — `InsertNewSessions` could trigger enrichment as part of the flush transaction (sync) or defer to a background worker (async). |
| Schema | `src/TitaniRun.Storage/Migrations/001_initial.sql` | **No schema changes needed** — the columns `publisher`, `signature_status`, `is_user_writable_path` on `apps` and `hosted_services` on `process_sessions` already exist. |
| Settings | `src/TitaniRun.Storage/Migrations/002_phase1_settings.sql` | Possibly — Phase 2 may add `enrichment.backfill_on_start` etc. |
| ProgramData/SQLite ACL | `src/TitaniRun.Service/ProgramDataAcl.cs` | **No** — Phase 2 doesn't touch storage perms. |

### 1.3 What's intentionally NOT in scope here

- **Alert pipeline (seam #2)** — Phase 6 wires the alert. Phase 2 produces the
  data the alert needs (`is_user_writable_path=1` + `signature_status='Unsigned'`)
  but does not implement raising/acknowledging.
- **Live activity IPC** — Phase 3.
- **History rollups / retention** — Phase 4.
- **Daily report** — Phase 5.
- **Installer** — Phase 6.

---

## 2. Open Questions (must be answered first)

Each question is framed against the product's mission: **help the user spot
unexpected processes/network activity from this Windows box**. The trade-off
descriptions are the security/UX tension the user is being asked to resolve.

### Q1. Signature cache key — per `app_id` vs per `(image_path, mtime, size)`?

**Proposed default:** per `(image_path, mtime, size)`.

| Option | Pros | Cons / security risk |
|---|---|---|
| Per `app_id` (the persisted row) | One verification per logical app, ever. Simplest. | **Swap-attack blindness.** If a malicious binary replaces a previously-trusted binary at the same path, the cached "Signed/Microsoft" verdict carries over. This is exactly the substitution pattern the alert is supposed to surface. |
| Per `(image_path, mtime, size)` | Binary changes (legitimate update OR swap) invalidate the cache. Re-verification catches the swap-attack class directly. Mtime/size are cheap to read. | Slightly more verification work — every Chrome update triggers a re-verification of `chrome.exe`. ~50–200 ms per binary, once. Negligible in practice. |

**Why this matters for the product:** Phase 6's headline alert is "unsigned
binary from a user-writable path making connections." If the cache lets a
swap-attacker inherit a clean verdict, the alert never fires and the product
fails its core promise.

### Q2. svchost service-list snapshot — at session open vs periodic refresh?

**Proposed default:** snapshot at session open, never refresh.

| Option | Pros | Cons |
|---|---|---|
| Snapshot at session open only | Cheap (one SCM enum per new svchost PID). Matches CLAUDE.md invariant #5 — honest, documented boundary. | If services start/stop within an existing svchost PID, the stored list goes stale. In practice, svchost PIDs rarely host new services mid-life. |
| Periodic refresh (e.g. every 30 s per svchost PID) | Always-fresh list. | Adds SCM enum cost proportional to number of running svchost PIDs (typically 10–25 on a desktop). Most refreshes find no change. |

**Why this matters for the product:** the user wants to know *which Windows
service* is talking to a given remote address. An at-open snapshot covers
~99% of real-world cases (services almost never come/go inside a running
svchost). A documented edge case is acceptable; CPU budget matters more.

### Q3. svchost API — WMI `Win32_Service` vs native SCM `EnumServicesStatusEx`?

**Proposed default:** native SCM only.

| Option | Pros | Cons |
|---|---|---|
| WMI `Win32_Service` | Easier object model; familiar from PowerShell `Get-CimInstance`. | Slow (100–300 ms first query), repository corruption is a real Windows reliability issue, requires WMI service running. |
| Native SCM (`EnumServicesStatusEx` + `QueryServiceConfig`) | Fast (sub-ms), no extra service dependency, deterministic. | More P/Invoke boilerplate. |

**Why this matters for the product:** the CLAUDE.md performance budget is
hard (< 1% idle CPU). WMI's first-query cost is enough on its own to eat that
budget on systems with many svchost PIDs.

### Q4. `is_user_writable_path` heuristic — prefix match vs full ACL check?

**Proposed default:** prefix match against known user-writable folder roots:
`%TEMP%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%\Downloads`, `%PUBLIC%`.

| Option | Pros | Cons / security risk |
|---|---|---|
| Prefix match | Cheap (string comparison). Cached per app. Catches the realistic threat model — malware almost always drops to `%TEMP%`, `AppData\Local\Temp`, or `Downloads`. | Misses unusual setups where the user made a custom directory writable (e.g. `C:\Tools\`). Misses scenarios where the binary path itself isn't user-writable but the executing user has elevated to admin. |
| Full ACL check | Accurate to actual filesystem perms. | Slow (per-path `GetSecurityInfo` syscall). Hard to cache (perms can change). Doesn't add much over prefix match for the actual threat model. |

**Why this matters for the product:** the alert fires on
`is_user_writable_path=1` AND `signature_status≠Signed` AND
`has-network-connections`. False negatives (missing a real user-writable path)
let attackers through. False positives (flagging system paths as user-writable)
generate alert noise that trains users to ignore alerts — worse than missing
the alert. Prefix match has near-zero false positives.

### Q5. `signature_status` mapping — what does each value mean exactly?

**Proposed default:**

- `Signed` — `WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)` returns
  `ERROR_SUCCESS`. The local machine's certificate stores trust this binary.
- `Invalid` — `WinVerifyTrust` returns a non-zero status indicating a signature
  IS present but verification failed (bad digest, untrusted root, expired
  cert past grace period). Status values: `TRUST_E_BAD_DIGEST`,
  `TRUST_E_SUBJECT_NOT_TRUSTED`, `TRUST_E_PROVIDER_UNKNOWN`,
  `CERT_E_UNTRUSTEDROOT`, etc.
- `Unsigned` — `WinVerifyTrust` returns `TRUST_E_NOSIGNATURE` AND no catalog
  signature is found.
- `Unchecked` — verification couldn't complete (file locked, file disappeared
  mid-check, transient error). Should be **rare** after Phase 2 ships and
  retried on the next flush window.

**Why this matters for the product:** `Invalid` is the most interesting class
— it's the binary signed by a non-trusted CA (potentially attacker's own
self-signed root) OR a tampered version of a previously-good signed binary
(bad digest). Both are highly suspicious. Phase 6's alert should treat
`Invalid` and `Unsigned` equivalently for "suspicious from user-writable path".

### Q6. Self-signed → `Signed` or `Unsigned`?

**Proposed default:** whatever `WinVerifyTrust` returns. Self-signed binaries
where the local machine trusts the signing root chain → `Signed`. Otherwise →
`Invalid` (signature present but no trusted chain) or `Unsigned` (no
signature record at all).

| Option | Pros | Cons |
|---|---|---|
| Follow `WinVerifyTrust` | Matches what the OS uses everywhere else (UAC prompts, AppLocker, SmartScreen). Predictable for users who know Windows behavior. | An attacker who installs a self-signed root (requires admin) gets their malware classified as `Signed`. Bounded threat though — root-install requires elevation. |
| Special-case self-signed | Could surface "self-signed" as a fourth category, treated stricter than `Signed`. | Adds a column value; user-facing complexity for a niche edge case. |

**Why this matters for the product:** the value of `Signed` is supposed to
mean "the local machine trusts this code's identity." Defining it as
"`WinVerifyTrust` succeeded" makes that statement true. Anything stricter
introduces a TitaniRun-specific definition that diverges from OS behavior.

### Q7. Enrichment timing — sync in `SqliteFlushSink` vs async background worker?

**Proposed default:** synchronous, with per-`app_id` cache.

| Option | Pros | Cons |
|---|---|---|
| Sync in flush | Apps row is complete the moment it's inserted. No partial-state bugs. Per-app cache means each binary is verified **once ever** across service lifetime — subsequent flushes that involve the same app are unaffected. First flush that includes a never-seen binary takes ~50–200 ms longer. | First-flush latency for new binaries. Invisible to user. |
| Async worker | Flush tick stays uniformly fast. | Adds queue + worker + state-tracking complexity. Apps rows have a window of "Unchecked" before enrichment lands, which the UI must handle. Failure modes (worker dies, queue overflows) are subtle. |

**Why this matters for the product:** Phase 6 alerts need the enrichment to
have run before they can fire. Async backfill means the alert window for a
malicious binary is the worker's lag — potentially seconds. Sync makes the
alert race-condition-free at the cost of imperceptible first-flush latency.

### Q8. Publisher change → new app row?

**Proposed default:** yes, per PRD §7.1 dedup key `(image_path, publisher)`.

| Option | Pros | Cons |
|---|---|---|
| New row on publisher change | Surfaces re-signing events (legit cert rotation OR malicious re-sign) as a distinct identity. Phase 6 can alert on "binary at this path used to be signed by X, is now signed by Y." | Historical bytes for what the user thinks of as "Chrome" might split across two app rows after a cert rotation. |
| Single row, update publisher in place | Stable history per binary path. | Loses the security signal that publisher changed. Worst-case masks a re-signed malware variant. |

**Why this matters for the product:** publisher changes are rare but always
meaningful. Microsoft rotates a code-signing cert every few years; Chrome
rotates more often. Each rotation gets a new app row. The reporting UI in
Phase 5 can group them ("Chrome (all certs)") for usability while preserving
the signal underneath.

### Q9. Phase 6 alert plumbing in Phase 2?

**Proposed default:** no. Phase 2 produces *only* the data shape (`apps`
columns correctly populated). Phase 6 wires the alert pipeline + the rule
that fires on `is_user_writable_path=1 AND signature_status IN ('Unsigned',
'Invalid') AND <has connections>`.

This is a confirmation question — making sure the next session doesn't drift
into building alert UI prematurely. **Strict scope.**

### Q10. Backfill existing apps rows on Phase 2 first run?

**Proposed default:** yes — a one-time enrichment sweep on first start after
Phase 2 deploys, with batching so it doesn't block the first flush tick.

| Option | Pros | Cons |
|---|---|---|
| Backfill on first start | Existing DB rows from Phase 1 (all with `publisher=NULL`, `signature_status="Unchecked"`) get enriched. Reporting/alerts work over historical data immediately. | One-time cost at first Phase 2 start — scales with number of apps in the DB (typically 30–100). At 200 ms/app worst case, that's 20 s of background work. Batched (10 apps/flush window) means ~10 s spread over 2 minutes. |
| Live-only (enrich only newly-seen apps) | Faster first start. | Stale "Unchecked" rows persist forever; reporting on those apps is broken until they're seen again. |

**Why this matters for the product:** users will install Phase 2 on top of a
running Phase 1 service with weeks of history. If we don't backfill, every
historical app shows as "Unchecked" until the process happens to be observed
again — which for short-lived processes might be never.

---

## 3. Sprint Plan Phase 2 reference

Restated from `docs/titanirun-sprint-plan.md` lines 91–103:

**Goal:** Turn "svchost.exe / unknown" into actionable identity — the core of
the product's value.

**Scope:**

- svchost → **service-name** resolution (`QueryServiceStatusProcess` / WMI
  `Win32_Service`) into `hosted_services`; multi-service PIDs listed,
  **bytes not split**.
- Signer/publisher + `signature_status` via offline `WinVerifyTrust`
  (`WTD_REVOKE_NONE`); cached per `app_id`.
- `is_user_writable_path` heuristic (temp/AppData/user-writable).
- `apps` dedup on `(image_path, publisher)`.

**CI gates (headless):**

- [ ] Given fixture PIDs/paths, service resolution and dedup produce expected
  `apps`/`process_sessions` rows (service lookups mocked behind an interface).
- [ ] Signature classifier maps known signed/unsigned/invalid fixtures to
  correct `signature_status`.
- [ ] Path heuristic flags user-writable locations correctly.

**Manual gates (real box):**

- [ ] Real svchost traffic resolves to named services (e.g., `Dnscache`,
  `Dhcp`), not bare `svchost.exe`; multi-service PIDs show the honest list.
- [ ] A known signed app shows its publisher; an unsigned binary run from
  `%TEMP%` shows `Unsigned` + user-writable flag.
- [ ] Enrichment does **not** raise idle CPU above budget (caching verified —
  no repeated `WinVerifyTrust` per event).

---

## 4. Proposed implementation plan

### 4.1 New abstractions (TitaniRun.Core)

Defined as interfaces in `Core` so the `Storage` layer can call into
implementations from `Attribution`, and tests can swap mocks behind them.

```
src/TitaniRun.Core/Attribution/
  IAppEnricher.cs              — single entry point. Sync method.
                                 AppIdentity Enrich(image_path, image_name, nowMs);
  EnrichmentResult.cs          — record carrying signature_status, publisher,
                                 is_user_writable_path.
  ISignatureVerifier.cs        — VerifyResult Verify(image_path);
  IUserWritablePathClassifier.cs — bool IsUserWritable(path);
  IServiceHostResolver.cs      — IReadOnlyList<string>? ResolveHostedServices(pid);
                                 returns null if PID is not a service host.
```

### 4.2 Implementations (TitaniRun.Attribution)

Phase 2's home for the Win32-specific pieces.

```
src/TitaniRun.Attribution/
  Authenticode/
    WinVerifyTrustSignatureVerifier.cs
      — P/Invoke wrapNet.WinTrust calls.
      — WTD_REVOKE_NONE (no network — invariant #1)
      — Cache key per (path, mtime, size) per Q1 decision
    NativeMethods.WinTrust.cs   — DLLImports + structs

  Services/
    ScmServiceHostResolver.cs
      — QueryServiceStatusProcess + EnumServicesStatusEx per Q3
      — Cache by PID (snapshot at session open per Q2)
    NativeMethods.Scm.cs        — DLLImports + structs

  Paths/
    UserWritablePathClassifier.cs
      — Prefix match against env-resolved roots per Q4
      — No filesystem syscall; pure string comparison

  AppEnricher.cs
    — Composes the three above into a single Enrich() call
    — Caches by (path, mtime, size) per Q1
```

### 4.3 Integration with Phase 1 flush model

`SessionTracker.TryTrack` currently builds an `AppIdentity` with stub values.
Phase 2 changes:

```csharp
// Before (Phase 1, src/TitaniRun.Core/Aggregation/SessionTracker.cs):
private static AppIdentity ToAppIdentity(ProcessImageInfo image) =>
    new(image.ImagePath, image.ImageName, Publisher: null,
        SignatureStatus: "Unchecked", IsUserWritablePath: false);

// After (Phase 2):
//   SessionTracker takes an IAppEnricher in its constructor and calls
//   enricher.Enrich(image, nowMs) instead of producing stub values.
//   The enricher's per-(path, mtime, size) cache ensures the verification
//   happens once per binary version, not once per PID.
```

For svchost-specific enrichment: the resolver returns the hosted services
list, which goes into `NewSessionEntry.HostedServices` (already plumbed
through `FlushBatch`). The flush sink writes it to `process_sessions.hosted_services`
verbatim per CLAUDE.md invariant #5 (no byte-splitting).

### 4.4 Backfill worker (per Q10)

A one-shot startup task in `TitaniRunHostedService`:

```
src/TitaniRun.Service/EnrichmentBackfill.cs
  — On Phase 2 first start (detected by SELECT COUNT FROM apps
    WHERE signature_status='Unchecked' > 0), enumerate Unchecked apps,
    enrich each, UPDATE the apps row.
  — Batched in groups of 10, sleep 100 ms between batches so the flush
    tick stays responsive.
  — Idempotent: re-running does nothing once all apps are enriched.
```

### 4.5 Order of execution

1. `EnrichmentResult` + `IAppEnricher` + supporting interfaces in `Core.Attribution`.
2. `UserWritablePathClassifier` + unit tests (cheapest, no Win32 deps).
3. `WinVerifyTrustSignatureVerifier` + unit tests with fixture binaries
   (`signtool`-signed test binaries committed under `tests/fixtures/`).
4. `ScmServiceHostResolver` + tests (mocked via `IServiceHostResolver`
   interface; native impl tested manually on real box).
5. `AppEnricher` composition + cache + tests.
6. Refactor `SessionTracker` to accept `IAppEnricher`. Update tests.
7. Refactor `SqliteFlushSink` to write the enriched fields. Update tests.
8. Wire enrichment in `TitaniRunHostedService`.
9. `EnrichmentBackfill` worker.
10. Update `docs/phase-2-verification.md` with manual gate walkthroughs.
11. Full build/test sweep, commit, watch CI, manual gates on real box.

---

## 5. Pre-flight tool dependencies

Per CLAUDE.md standing behavior — surface these BEFORE any validation steps.

| Tool | Purpose | Check |
|---|---|---|
| `signtool.exe` | Sign test fixture binaries for the signature verifier tests. Ships with the Windows SDK. | `Get-Command signtool.exe` — if missing, the user has to install the Windows SDK or the smaller "Signing Tools for Desktop Apps" feature via the VS Installer. |
| `sigcheck.exe` | Sysinternals — cross-verifies signature status during manual gates. Not strictly required (PowerShell `Get-AuthenticodeSignature` covers it) but nicer output. | `winget install --id Microsoft.Sysinternals.Sigcheck` |
| `accesschk.exe` | Sysinternals — used to verify the `is_user_writable_path` classifier matches reality on real-box gates. | `winget install --id Microsoft.Sysinternals.AccessChk` |

Already present from Phase 1:

- `procmon` (Process Monitor) — for the no-per-event-writes scaling check
- `sqlite3.exe` — for ad-hoc DB queries

---

## 6. Test strategy

### 6.1 Headless (CI must run on windows-latest with no admin/elevation needs)

| Project | Adds |
|---|---|
| `TitaniRun.Attribution.Tests` | `UserWritablePathClassifierTests` (prefix-match correctness). `AppEnricherTests` (composition + caching: same path+mtime+size hits cache; mtime change invalidates). |
| `TitaniRun.Storage.Tests` | `SqliteFlushSink` writes enriched fields correctly; publisher-change-creates-new-app dedup behavior. |
| `TitaniRun.Integration.Tests` | End-to-end with a fake `IAppEnricher` that returns scripted results; assert exact `apps` rows reflect the enrichment. |
| `TitaniRun.Core.Tests` | `SessionTrackerTests` — verify the enricher is called once per `(path, mtime, size)`, not per `ResolveSessionId`. |

### 6.2 Fixture binaries

Add `tests/fixtures/` with three small executables checked in:

- `signed-microsoft.exe` — system-shipped or VC++ redist sample (well-known publisher)
- `signed-self.exe` — pre-signed with a self-signed cert (commit the cert + cer)
- `unsigned.exe` — no signature

The signature verifier tests run against these. **Note:** if reproducibility
across CI runners is fragile (cert expiry, store contents), prefer to mock
the verifier interface and run only one or two real-binary tests as
smoke tests.

### 6.3 Not on CI

Live SCM enumeration and real WMI fall in the same bucket as live ETW —
manual-gate only. The `IServiceHostResolver` interface is mocked in CI.

---

## 7. Manual gate prep

Build `docs/phase-2-verification.md` with these flows:

1. **svchost resolution** — after the service runs for a minute, query:
   ```sql
   SELECT a.image_name, ps.pid, ps.hosted_services, SUM(s.bytes_up+s.bytes_down) AS bytes
   FROM apps a JOIN process_sessions ps USING(app_id)
                JOIN traffic_samples s USING(session_id)
   WHERE a.image_name = 'svchost.exe'
   GROUP BY ps.pid;
   ```
   Expect `hosted_services` populated with names like `Dnscache,NlaSvc,Dhcp`.

2. **Signer verification** — pick a known signed app (`code.exe`, `chrome.exe`)
   and check its row has `publisher` populated and `signature_status='Signed'`.

3. **Unsigned-from-temp** — copy `nc.exe` or any unsigned binary to `%TEMP%`,
   run it doing a network connect, verify the apps row has
   `is_user_writable_path=1` and `signature_status='Unsigned'`.

4. **CPU budget** — re-run the Phase 1 idle CPU sampler; confirm avg CPU still
   under 1% with enrichment enabled (caching working correctly).

5. **Backfill** — install Phase 2 on top of a running Phase 1 DB; observe the
   backfill log line, then verify all apps rows have non-NULL `publisher`
   (or `Unsigned` status for genuinely unsigned binaries).

---

## 8. Architectural guardrails (do NOT violate)

These are CLAUDE.md invariants the next session must respect:

- **Invariant #1 — Zero outbound network from our processes.** `WinVerifyTrust`
  MUST be called with `WTD_REVOKE_NONE`. Revocation checks require network and
  are forbidden. The signature verifier code path must have no other DLLs
  loaded that might phone home.
- **Invariant #4 — No per-event DB writes.** Enrichment is per-`app_id`
  (or per-binary-version per Q1), not per-observation. The flush model
  established in Phase 1 must remain the sole write path.
- **Invariant #5 — Honest attribution.** When svchost hosts multiple
  services, the list goes into `hosted_services` as a comma-separated string.
  **Do not** split bytes across the services. The user gets the honest "this
  PID hosts services X, Y, Z; here's its total traffic."

---

## 9. Definition of done

All of the following pass:

- [ ] Open questions §2 resolved (answers documented in the commit message
  or a follow-up addendum to this file).
- [ ] CI green: 6 test projects, all passing, no skipped tests. New tests
  for signature/path/service-resolution classifier behavior.
- [ ] Manual gates §7 walked by user on a real box.
- [ ] `docs/phase-2-verification.md` exists with the gates from §7.
- [ ] CPU budget gate still passes (`< 1%` idle).
- [ ] `apps` rows enriched: `publisher` is non-NULL for signed code,
  `signature_status` matches reality, `is_user_writable_path` correctly
  flags AppData/Temp/Downloads.
- [ ] svchost rows show populated `hosted_services` lists.
- [ ] Phase 2 boxes in Sprint Plan checked off.
- [ ] Commit pushed, CI green on the windows-latest runner.

---

## 10. Reference snippets the next session will need

### File listing of Phase 1 outputs (commit `8cda9ca`)

```
src/TitaniRun.Core/
  Aggregation/
    BucketAligner.cs
    SessionTracker.cs       ← Phase 2 modifies ToAppIdentity here
    TrafficAggregator.cs
  Attribution/
    IPidTableSnapshotSource.cs
    IProcessImageResolver.cs
    InMemoryPidTableSource.cs
    InMemoryProcessImageResolver.cs
    PidCorrector.cs
    PidTableSnapshot.cs
  Classification/
    RemoteAddressClassifier.cs
  Monitoring/
    IMonitor.cs
  Observations/
    Direction.cs / NetworkObservation.cs / Protocol.cs / RemoteClass.cs
  Storage/
    AppIdentity.cs          ← Phase 2 fills the existing slots
    FlushBatch.cs           ← NewSessionEntry already carries AppIdentity
    IFlushSink.cs

src/TitaniRun.Attribution/
  IpHelper/                 ← Phase 1
  RealProcessImageResolver.cs  ← Phase 2 may compose with enricher

src/TitaniRun.Storage/
  Migrations/
    001_initial.sql         ← apps columns already exist; no schema changes
    002_phase1_settings.sql
  Migrator.cs
  Repositories/
    ConnectionFactory.cs
    SqliteFlushSink.cs      ← Phase 2 writes the enriched fields

src/TitaniRun.Service/
  CaptureMonitor.cs
  ProgramDataAcl.cs
  TitaniRunHostedService.cs  ← Phase 2 wires enricher into pipeline
  TitaniRunIpcHandler.cs
```

### How to run the full test sweep locally

```powershell
cd C:\dev\titanirun-monitor
dotnet build .\TitaniRun.slnx -c Release
dotnet test  .\TitaniRun.slnx -c Release
```

### How to install/reinstall the dev service

```powershell
# Elevated:
.\scripts\uninstall-dev.ps1 [-PurgeData]
.\scripts\install-dev.ps1
sc.exe query TitaniRun
& .\src\TitaniRun.Cli\bin\Release\net10.0-windows\trctl.exe status
```
