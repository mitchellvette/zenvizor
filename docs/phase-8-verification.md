# Phase 8 verification — passive DNS observer + hostname resolution

**Status:** **Closed with known gap, 2026-06-21.** All four manual gates
walked; pipeline is healthy end-to-end. Hit rate on DoH-using apps
(notably Chrome) is structurally zero — see the *Known limitations*
section below. Pre-MVP follow-up is **Phase 8.5 — Endpoint visibility
investigation** in `docs/zenvizor-sprint-plan.md`.

Code: 513/513 tests pass against the slice work; see Phase 8 design
decisions D1–D5 in `docs/zenvizor-sprint-plan.md`.
**Companion doc:** `docs/zenvizor-sprint-plan.md` Phase 8 (acceptance
criteria + scope).
**Test environment:** dev box, Win11 Home 10.0.26200, run from an
elevated PowerShell with the user signed in (so the resolver actually
sees user-initiated traffic).

Per the project's "verification docs at phase level, not slice" rule, this
is the single Phase 8 verification doc — all four manual gates + the
high-level-provider smoke pre-flight live here.

---

## Pre-flight tool check (do this first)

The four gates below use these tools. Every one of them is built into
Windows — there's nothing to install. Confirming up front so a gate
doesn't stall mid-walkthrough.

| Tool                         | Built-in?           | Used by gate         |
| ---------------------------- | ------------------- | -------------------- |
| `Get-DnsClientCache`         | Yes (PowerShell)    | G1, G2               |
| `Get-Counter` / Task Manager | Yes                 | G4                   |
| `sqlite3.exe`                | **No — see below**  | G1 spot-check, G3    |

`sqlite3.exe` ships separately. Install once:

```powershell
winget install --id SQLite.SQLite -e --accept-source-agreements --accept-package-agreements
# verify
sqlite3 -version
```

If `winget` itself is missing on a stripped-down image, the SQLite
spot-checks below are skippable — the IPC-surface checks via `zvctl`
cover the same ground without needing the file directly.

---

## Build + install (re-do for every new Phase 8 cut)

The Phase 8 service binaries add a second ETW kernel session
(`ZenVizor.Capture.Dns`) and write `connections.resolved_host`. Existing
rows from before this install stay null — that's a documented property
of the rollout, not a regression.

Elevated PowerShell, full paths because elevated shells default to
`System32`:

```powershell
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1     # stops + removes the previous service
.\scripts\install-dev.ps1       # builds Release + registers + starts
# UI (separate, non-elevated shell)
dotnet run --project C:\dev\zenvizor\src\ZenVizor.Ui --configuration Release
```

When the service starts you should see two new informational lines in
the service log (`%ProgramData%\ZenVizor\logs\`):

```
DNS capture session 'ZenVizor.Capture.Dns' started (provider 'Microsoft-Windows-DNS-Client').
```

If instead you see:

```
DNS capture source failed to start; resolved_host will be null until next service start.
```

…the high-level provider isn't available on this SKU and you're in the
fallback territory described in Phase 8 spec — STOP and re-check
provider availability before continuing the gate (see "Smoke pre-flight"
below).

---

## Smoke pre-flight — prove the pipeline works end-to-end

One question: **does a DNS observation flow all the way from ETW →
mapper → store → connection upsert, in a way I can confirm in the
database?** Clear three-step recipe:

1. **Trigger a brand-new DNS lookup AND a real TCP connection in one
   shot.** `Resolve-DnsName` alone doesn't open a socket, so it would
   populate the in-memory store but produce no `connections` row to
   check — useless as a smoke test. `Test-NetConnection` does both:

   ```powershell
   Test-NetConnection outlook.office.com -Port 443
   ```

2. **Wait one flush cycle plus margin** (default flush is 5 s):

   ```powershell
   Start-Sleep -Seconds 10
   ```

3. **Look for the hostname in the DB.** Elevated shell — the DB is
   ACL'd to SYSTEM + Administrators:

   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   sqlite3 -readonly -header -column $db "SELECT remote_addr, resolved_host, last_seen FROM connections WHERE resolved_host = 'outlook.office.com' ORDER BY last_seen DESC LIMIT 5;"
   ```

**Pass:** at least one row returns with `resolved_host =
"outlook.office.com"`. The pipeline is alive end-to-end.

**Fail:** zero rows. Stop and diagnose:

- Is the service actually running? `Get-Service ZenVizor`
- Did the DNS source start? Check the file log
  (`C:\ProgramData\ZenVizor\logs\service-YYYYMMDD.log`) for either
  `DNS capture session 'ZenVizor.Capture.Dns' started` (success) or
  `DNS capture source failed to start` (failure → high-level provider
  unavailable on this SKU, fall-back territory).
- Has the `outlook.office.com` IP been cached recently? A cached
  lookup does not fire event 3008. Pick a less-common hostname for
  the smoke — e.g., a friend's blog or any rarely-visited site —
  rather than something Windows itself probes hourly.

---

## G1 — Hostnames visible on AppDetail Connections grid (≥30 min usage)

**Goal:** for the most-recent connections of an OS-resolver app,
the Connections grid shows the QNAME the user asked the resolver
for. (Browsers running DoH will mostly show null hostnames; that
is structurally expected and covered by the *Known limitations*
section below.)

**Pick the right app for this gate.** Apps known to use the
Windows resolver: Steam, Outlook desktop, Teams desktop, Windows
Update, Claude desktop, anything using `HttpClient` or system
networking APIs. Apps known to bypass: Chrome (DoH default),
Firefox (DoH config-dependent), modern Edge (DoH config-dependent).
**Run this gate against a known-good OS-resolver app — Steam works
well as the reference case.**

**Steps:**

1. Run ZenVizor for ≥30 minutes of normal use, including some
   browsing or activity in your chosen reference app.
2. Open the UI, navigate to App Detail for that app.
3. Scroll the **Connections** grid. Expected result: most recent
   rows show a hostname as the primary line with the raw IP as a
   smaller subscript underneath. Older rows (whose `first_seen`
   pre-dates the Phase 8 install) may still show IP-only — that's
   documented, not a regression.

**Hit-rate breakdown by app (the load-bearing diagnostic).** This
single query shows you which apps the observer is recovering
hostnames for and which it isn't, all in one view. Elevated shell:

```powershell
$db = "C:\ProgramData\ZenVizor\zenvizor.db"
sqlite3 -readonly -header -column $db "SELECT a.image_name, COUNT(*) AS rows, SUM(c.resolved_host IS NOT NULL) AS with_host, ROUND(100.0 * SUM(c.resolved_host IS NOT NULL) / COUNT(*), 1) AS pct FROM connections c JOIN process_sessions ps ON ps.session_id=c.session_id JOIN apps a ON a.app_id=ps.app_id GROUP BY a.image_name HAVING rows > 5 ORDER BY rows DESC LIMIT 20;"
```

Expected pattern after a normal browsing session:

- OS-resolver apps (Steam, Outlook, Teams, Claude): `pct` in the
  50–90 % range. Not 100 % because of connection reuse + cache
  hits — see the *Coverage notes* sidebar below.
- DoH-using apps (`chrome.exe`, often `msedge.exe`, sometimes
  `firefox.exe`): `pct` near 0 %. Expected and documented.

**Cross-check against Windows' own DNS cache.** The cross-check is
informal — *are we making things up?* The two outputs below should
overlap a lot for OS-resolver apps and be unrelated for DoH apps:

```powershell
# What ZenVizor passively recorded (distinct hostnames in the last hour):
$db = "C:\ProgramData\ZenVizor\zenvizor.db"
sqlite3 -readonly -header -column $db "SELECT DISTINCT resolved_host FROM connections WHERE resolved_host IS NOT NULL AND last_seen > unixepoch('now','-1 hour') * 1000 ORDER BY resolved_host LIMIT 30;"

# What the Windows resolver currently has cached (A records only, sorted):
Get-DnsClientCache | Where-Object Type -eq 'A' | Select-Object Entry,Data | Sort-Object Entry | Format-Table -AutoSize
```

A hostname appearing in *both* lists is a sanity check that ZenVizor
is reporting what Windows actually saw. Hostnames in the ZenVizor
list but absent from the cache are typical (cache TTLs expire faster
than our 30-day retention). Hostnames in the cache but absent from
ZenVizor are also typical (cache survives reboots; we don't backfill
existing rows).

**Pass criteria:** the hit-rate breakdown shows at least one
OS-resolver app at ≥50 %, and the cross-check shows overlap between
ZenVizor's recorded hostnames and the Windows cache. DoH-using apps
near 0 % is documented behaviour, not a fail.

### Coverage notes — why hit rate is never 100 %

These are properties of passive DNS observation, not bugs to chase:

1. **Connection reuse hides DNS.** Once an app resolves a name and
   opens a TCP socket, both sides reuse for ages without new DNS
   lookups. A long-lived Outlook session has one DNS event observed,
   forever.
2. **Cache hits don't fire event 3008.** Apps re-resolving cached
   names go through the in-memory cache without an ETW event.
3. **Pre-install rows stay null.** Any `connections` row whose
   `first_seen` predates the Phase 8 install will never carry a
   hostname — we don't have the historical DNS data to backfill it.

---

## G2 — IPv6-heavy app renders human-readable names

**Goal:** confirm the dual-line column is doing its job for the case it
was designed to fix — long hex-colon IPv6 strings are unreadable on
their own.

**Steps:**

1. Open a browser to a site fronted by an IPv6-only CDN (Outlook Web,
   Cloudflare's homepage, GitHub).
2. Wait one flush cycle (~5 s) for the row to land.
3. In App Detail's Connections grid for that browser, find the row
   whose Remote endpoint subscript shows a `2606:…` or similar IPv6
   address.

**Pass criteria:** the primary line on that row is a human-readable
hostname (e.g. `outlook.office.com`, `cdn.cloudflare.net`). If the row
shows only the IPv6 address with no hostname, check the row's
`first_seen` — if it predates the service install, that's expected.

---

## G3 — Zero-own-traffic invariant (re-run; invariant #1)

**Goal:** Phase 8 adds a new ETW subscriber. The "ZenVizor emits zero
network traffic of its own" invariant must continue to hold after that
addition. Same gate as Phase 6.8b, run again.

**Steps:**

1. With the service running and the UI open and active for ≥5 min:

   ```powershell
   # Find both ZenVizor PIDs (service + UI).
   $svcPid = (Get-CimInstance Win32_Service -Filter "Name='ZenVizor'").ProcessId
   $uiPid  = (Get-Process ZenVizor.Ui -ErrorAction SilentlyContinue).Id
   "svc=$svcPid  ui=$uiPid"
   ```

2. Query the DB for any `connections` rows attributed to either PID:

   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   sqlite3 -readonly -header -column $db "SELECT a.image_name, ps.pid, c.remote_addr, c.bytes_up, c.bytes_down FROM connections c JOIN process_sessions ps ON ps.session_id = c.session_id JOIN apps a ON a.app_id = ps.app_id WHERE a.image_name IN ('ZenVizor.Service.exe','ZenVizor.Ui.exe') ORDER BY c.last_seen DESC LIMIT 20;"
   ```

**Pass criteria:** zero rows. If any row appears with bytes_up > 0 or
bytes_down > 0 attributed to a ZenVizor PID, that's a violation of
invariant #1 and a **stop-the-world bug** — the DNS observer's
TraceEvent subscription must not originate traffic, and the high-level
provider does not.

---

## G4 — Performance budget still holds

**Goal:** the second TraceEventSession + the in-memory DNS store + the
flush-time lookup must not push us past the budget in CLAUDE.md
("Performance budget"): idle CPU < 1%, service working set < ~80 MB.

**Steps:**

1. With the service running and ≥5 minutes of warm-up under normal
   browsing, take a single working-set + CPU sample:

   ```powershell
   $svcPid = (Get-CimInstance Win32_Service -Filter "Name='ZenVizor'").ProcessId
   $ws = (Get-Process -Id $svcPid).WorkingSet64 / 1MB
   "{0:N1} MB working set" -f $ws

   # Idle CPU sample (5 s window).
   Get-Counter "\Process(ZenVizor.Service)\% Processor Time" -SampleInterval 1 -MaxSamples 5 |
       Select-Object -ExpandProperty CounterSamples |
       Measure-Object CookedValue -Average | Select-Object Average
   ```

2. Open the **CaptureStats** surface via `zvctl` to verify both
   capture sources are healthy:

   ```powershell
   zvctl stats
   ```

   The output should show observation counts climbing for the kernel
   network source and (newly) for the DNS source — no faulted flag.

**Pass criteria:** working set < ~80 MB; idle CPU average < 1.0 %.
DNS-event rate is low so the budget should be comfortable; if either
number drifts, **diagnose before signing off** rather than accepting a
new ceiling.

---

## Known limitations

### DoH and in-app resolvers (Chrome ≈ 0 % hit rate)

Phase 8 listens to `Microsoft-Windows-DNS-Client` event 3008, which
only fires for queries that go through the **Windows resolver**.
Apps that resolve names themselves bypass the resolver entirely
and are structurally invisible to the Phase 8 observer:

- **Chrome.** Ships DNS-over-HTTPS on by default
  (`chrome://settings/security` → "Use secure DNS"). Resolves via
  HTTPS to Cloudflare / Google / Quad9. Hit rate on this gate: ~0 %.
- **Firefox / Edge.** DoH is configurable; default state depends on
  region and rollout. Often on.
- **Anything embedding its own resolver** (a few enterprise apps,
  some VPN clients).

Apps still covered: Steam, Outlook desktop, Teams desktop, Windows
Update, Claude desktop, anything using `HttpClient` or system
network APIs.

**User-side workaround** for verifying *Phase 8 itself* against
Chrome: open `chrome://settings/security`, set "Use secure DNS" to
**Off**, restart Chrome, browse for a few minutes, re-run the
hit-rate breakdown query above. Chrome's `pct` should jump from
~0 % to the same range as the OS-resolver apps. Don't ship this
workaround to users — DoH is a real privacy improvement; the right
response to the visibility gap is the **Phase 8.5** investigation,
not asking users to give up DoH.

**Pre-MVP follow-up — resolved.** The Phase 8.5 spike completed
2026-06-21 and recommends **closing this gap pre-MVP at full
coverage** via passive TLS + QUIC + HTTP/1.1 Host SNI extraction
(findings: `docs/phase-8.5-endpoint-visibility.md`; implementation:
Phase 8.6 in `docs/zenvizor-sprint-plan.md`). The technique is
purely passive — it cleared the invariant #1 audit (receive-only
substrate, pure-compute crypto, empty self-monitoring lens) and sits
well inside the perf budget. So this DoH gap is no longer "deferred";
it is closed by Phase 8.6. The one residual structural limit carried
forward is **ECH-enabled origins** (TLS 1.3 Encrypted ClientHello),
which hide the SNI — a small but growing minority, documented in the
UI with the same honesty pattern as this DoH note.

### ECH-enabled origins (the residual gap after Phase 8.6)

Phase 8.6 closed the DoH gap above by extracting the hostname from the
**plaintext** TLS ClientHello / decrypted QUIC Initial / HTTP Host
header. The one case it cannot reach is TLS 1.3 **Encrypted
ClientHello (ECH)**: the inner ClientHello — SNI included — is
encrypted to the origin's public key, so the server name is not in the
clear and is not recoverable by any passive observer. Those flows
render IP-only, exactly like a DoH cache-hit row.

This affects a small but growing minority today (Cloudflare-fronted
opt-in origins). There is no passive workaround — recovery would need
the origin's private key or an active MITM, both of which violate the
"passive monitor, not a firewall" charter. We surface it honestly and
do not chase it:

- **In the UI:** the AppDetail "Remote endpoint" info popup explains
  that some connections stay IP-only "because the name was hidden
  inside encrypted traffic and can't be read"
  (`src/ZenVizor.Ui/Views/AppDetailPage.xaml`).
- **In the docs:** the full write-up + the manual gates that exercise
  it live in `docs/phase-8.6-verification.md`.

### Connection reuse + cache hits

Even on OS-resolver apps, hit rate doesn't reach 100 %. See the
*Coverage notes* sidebar under G1.

---

## Sign-off

**Walked 2026-06-21 (dev box, Win11 Home 10.0.26200):**

- [x] **G1 hostnames-visible** — pass with documented gap. Steam,
      Outlook, Claude desktop produced human-readable hostnames.
      Chrome produced ~0 hostnames due to its default DoH
      configuration (see *Known limitations*); confirmed via the
      hit-rate breakdown query.
- [x] **G2 IPv6-readable** — pass. Outlook's IPv6 endpoints
      rendered as `outlook.office.com` instead of `2603:1006:…`.
- [x] **G3 zero-own-traffic** — pass. Zero rows in `connections`
      attributed to either ZenVizor PID after >5 min of UI use.
      Invariant #1 holds.
- [x] **G4 performance** — pass. Working set and idle CPU within
      budget; second TraceEventSession adds negligible cost as
      design decision D5 predicted.

**Phase 8 status:** closed-with-known-gap. The pipeline works
end-to-end and the four gates pass; the DoH gap is a coverage
limit of the underlying ETW provider, not a defect in the slice
work, and is the trigger for the pre-MVP Phase 8.5 follow-up.

**Queued downstream work:**

1. **Phase 8.5 — Endpoint visibility investigation** (pre-MVP
   requirement) — **complete 2026-06-21.** Brief delivered
   (`docs/phase-8.5-endpoint-visibility.md`); decision is **ship
   pre-MVP at full coverage** via passive TLS/QUIC/Host SNI
   extraction. Implementation is **Phase 8.6**.
2. **Phase 8.6 — Passive SNI/QUIC/Host hostname recovery.** The
   Phase 8.5 implementation; closes the DoH gap. Runs before
   Phase 9.6 so the bundle packs the 8.6 binaries.
3. **Phase 9.6 — Re-cut MSI + Burn bundle.** Now packs Phase 8 +
   Phase 8.6 (the spike landed on ship, not defer).
