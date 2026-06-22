# Phase 8.6 verification — passive SNI / QUIC / HTTP-Host hostname recovery

**Status:** **CLOSED 2026-06-21.** CI green (554/554 across the
solution; the 41 new tests live in `tests/ZenVizor.Core.Tests/Sni/`)
and all four manual gates walked on a real box, with the §7 PktMon
control-surface question resolved (the capture component is required).
This phase closes the Phase 8 DoH coverage gap and re-confirms
invariant #1 with the new capture session active. See *Sign-off*.

This phase adds a **second passive feeder** into the existing
`DnsResolutionStore`: it extracts hostnames from plaintext **TLS SNI**
(TCP 443), **QUIC Initial SNI** (UDP 443), and **HTTP/1.1 Host**
headers (TCP 80). It recovers the visibility that `Microsoft-Windows-
DNS-Client` (Phase 8) is structurally blind to — Chrome-with-DoH and
any app that ships its own resolver. Strictly observational: it emits
ZERO traffic of its own (invariant #1).

**Companion docs:** `docs/zenvizor-sprint-plan.md` Phase 8.6
(acceptance criteria + scope); `docs/phase-8.5-endpoint-visibility.md`
(the spike that chose the approach — §7 PktMon control surface, §8
build gotchas); `docs/phase-8-verification.md` (the DoH gap this phase
closes, and the ECH note shared with G4 here).

**Test environment:** dev box, Win11 Home 10.0.26200, run from an
elevated PowerShell with the user signed in.

Per the project's "verification docs at phase level, not slice" rule,
this is the single Phase 8.6 verification doc.

---

## Pre-flight tool check (do this first)

| Tool                         | Built-in?          | Used by                       |
| ---------------------------- | ------------------ | ----------------------------- |
| `pktmon.exe`                 | Yes (Win10 1809+)  | §7 clean-boot step, substrate |
| `Get-Counter` / Task Manager | Yes                | G4                            |
| `sqlite3.exe`                | **No — see below** | smoke, G1, G2, G3             |
| Google Chrome                | **No — see below** | G1, G2 (DoH-on browser)       |

`sqlite3.exe` ships separately. Install once:

```powershell
winget install --id SQLite.SQLite -e --accept-source-agreements --accept-package-agreements
sqlite3 -version
```

G1/G2 need a browser that runs **DoH by default** — Chrome is the
reference case. If Chrome isn't installed:

```powershell
winget install --id Google.Chrome -e --accept-source-agreements --accept-package-agreements
```

`pktmon.exe` is built into Windows; nothing to install. If the
primary substrate can't start, the service automatically falls back to
the raw-socket (`SIO_RCVALL`) substrate — see the §7 step for how to
tell which one is live.

---

## Build + install (re-do for every new Phase 8.6 cut)

The Phase 8.6 service binaries add a **third** capture session
(`ZenVizor.Capture.Sni.PktMon`, or the raw-socket fallback) alongside
the Phase 6 kernel-network session and the Phase 8 DNS session. They
write into the same `DnsResolutionStore` → `connections.resolved_host`
join — **no IPC, schema, or UI change** (SNI feeds the existing
source-agnostic `ResolvedHost` field).

Elevated PowerShell, full paths because elevated shells default to
`System32`:

```powershell
cd C:\dev\zenvizor
.\scripts\uninstall-dev.ps1     # stops + removes the previous service
.\scripts\install-dev.ps1       # builds Release + registers + starts
# UI (separate, non-elevated shell)
dotnet run --project C:\dev\zenvizor\src\ZenVizor.Ui --configuration Release
```

> **Note — Release build is locked while the dev service is running.**
> `install-dev.ps1` stops the service before it rebuilds, so the build
> succeeds. If you build Release by hand while the service is up, it
> will fail with a file lock on `bin\Release`; stop the service first.

When the service starts, the file log
(`%ProgramData%\ZenVizor\logs\service-YYYYMMDD.log`) shows **which
substrate won**:

```
SNI capture started on the PktMon substrate.
```

or, if PktMon couldn't start and it fell back:

```
SNI capture failed to start on the PktMon substrate.
SNI capture started on the RawSocket substrate.
```

or, if neither came up (degrades to Phase 8 DNS-only — not a crash):

```
SNI capture could not start on any substrate; resolved_host falls back to DNS observation only.
```

Note which line you see — the §7 step below interprets it.

---

## §7 — PktMon control-surface question (RESOLVED 2026-06-21)

**Answer: the capture component is REQUIRED.** Enabling the
`Microsoft-Windows-PktMon` ETW provider in a `TraceEventSession` is
*not* sufficient on its own — PktMon only emits packet payloads while a
`pktmon start --capture` capture component is running. So the
production source's child-process spawn
(`pktmon start --capture --pkt-size 1600 -m real-time`,
PktMonPacketSource.cs `StartCaptureComponent`) is load-bearing, not
removable. Keep it.

**Why this was open.** The Phase 8.5 spike couldn't isolate the answer
(a confound): its test box likely had a stale `pktmon start` from
manual poking, so when it subscribed to the provider and saw packets,
it couldn't tell whether its own capture or the leftover one was the
source. PktMon has **two layers** that are easy to conflate:

1. **The ETW provider** — the consumer side; `EnableProvider(...)` is
   how we *listen*.
2. **The capture component** — the in-kernel machinery that *mirrors
   packets* into that provider; a global toggle flipped by
   `pktmon start --capture` / `pktmon stop`, independent of any ETW
   subscription.

The question was whether layer 1 alone makes packets flow, or whether
layer 2 must also be running.

**How it was settled (the decisive A/B — no code change, no reboot
needed).** With the service running and hostnames flowing, toggle layer
2 out from under the live layer-1 subscription and watch delivery:

```powershell
# Baseline: service up, capture active, child pktmon present.
pktmon status
Get-Process pktmon -ErrorAction SilentlyContinue | Select-Object Id, StartTime

# Turn the capture component OFF (our ETW session stays subscribed — different layer):
pktmon stop
# Browse a FRESH, never-visited site, wait ~10s, then check it did NOT land:
$db = "C:\ProgramData\ZenVizor\zenvizor.db"
sqlite3 -readonly -header -column $db "SELECT remote_addr, resolved_host, last_seen FROM connections WHERE resolved_host LIKE '%yournewsite%' ORDER BY last_seen DESC LIMIT 5;"

# Control: turn capture back ON, browse the SAME site again, confirm it NOW lands.
Restart-Service ZenVizor   # Dispose kills the old child + pktmon stop; Start re-runs StartCaptureComponent.
pktmon status
# ...browse the same site, wait ~10s, re-run the SELECT above — rows should appear.
```

Capture **off** → the fresh site produced **no rows**; capture **on** →
the **same** site produced rows. The only variable that changed was the
capture component, which rules out the "that site just never lands"
(e.g. ECH) explanation. Hence: capture component required.

**If you ever re-validate on a fresh box:** stopping the capture does
*not* fault the source — `ProcessLoop` only faults if `Source.Process()`
returns/throws, so the session simply goes quiet. Restart the service
afterward to restore normal operation (the bare `pktmon stop` leaves
capture off until the next service start).

---

## Smoke pre-flight — prove the SNI pipeline works end-to-end

One question: **does a hostname pulled out of a TLS/QUIC handshake (no
Windows DNS event at all) reach `connections.resolved_host`?** The
trick is to use a name the Windows resolver never sees, so any hit is
unambiguously from SNI, not Phase 8.

1. **Turn Chrome's DoH on** (it's the default, but confirm):
   `chrome://settings/security` → "Use secure DNS" = **On**. This
   guarantees Chrome bypasses the Windows resolver, so Phase 8 sees
   nothing and any hostname recovered is from Phase 8.6.

2. **Flush the Windows resolver cache** so we can prove the name isn't
   coming from there:
   
   ```powershell
   Clear-DnsClientCache
   ```

3. **Browse a distinctive, rarely-visited site in Chrome** — pick
   something Windows itself never probes (a personal blog, a niche
   docs site). Give it ~10 s for a flush cycle.

4. **Confirm the name landed in the DB but NOT in the Windows cache:**
   
   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   # ZenVizor recovered it passively from the handshake:
   sqlite3 -readonly -header -column $db "SELECT remote_addr, resolved_host, last_seen FROM connections WHERE resolved_host LIKE '%<your-site>%' ORDER BY last_seen DESC LIMIT 5;"
   # The Windows resolver never saw it (DoH bypassed it):
   Get-DnsClientCache | Where-Object Entry -like '*<your-site>*'
   ```

**Pass:** the SQLite query returns the hostname; `Get-DnsClientCache`
returns nothing. That gap is the proof — the name could only have come
from SNI/QUIC extraction.

**Fail:** zero rows from SQLite. Check the service log for the
substrate line (Build + install), re-confirm Chrome DoH is on, and
verify the child `pktmon` process per §7.

---

## G1 — Chrome (DoH on) shows hostnames on the AppDetail Connections grid

**Goal:** the Phase 8 baseline for `chrome.exe` was ~0 % hostnames
(DoH bypasses the resolver). With 8.6 live, the bulk of Chrome's TLS
**and** QUIC flows should render human-readable hostnames.

**Steps:**

1. With DoH **on**, browse normally in Chrome for ≥10 min (mixed
   sites — news, search, a video).

2. Open the UI → App Detail for `chrome.exe` → **Connections** grid.
   Expected: most recent rows show a hostname as the primary line with
   the raw IP as the subscript underneath.

3. **Hit-rate breakdown — the load-bearing diagnostic.** Elevated
   shell:
   
   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   sqlite3 -readonly -header -column $db "SELECT a.image_name, COUNT(*) AS rows, SUM(c.resolved_host IS NOT NULL) AS with_host, ROUND(100.0 * SUM(c.resolved_host IS NOT NULL) / COUNT(*), 1) AS pct FROM connections c JOIN process_sessions ps ON ps.session_id=c.session_id JOIN apps a ON a.app_id=ps.app_id WHERE a.image_name='chrome.exe' GROUP BY a.image_name;"
   ```

**Pass criteria:** `chrome.exe` `pct` is **materially above the Phase 8
near-zero baseline** — expect a large majority of post-install flows
to carry a hostname. It won't be 100 % (connection reuse, pre-install
rows, ECH origins — see *Known limitations*). The decisive comparison
is against Phase 8's ~0 % for the same app, captured in
`docs/phase-8-verification.md` G1.

---

## G2 — QUIC-heavy target renders hostnames (proves the decrypt path live)

**Goal:** prove the QUIC Initial decrypt path works end-to-end on real
traffic, not just the RFC 9001 §A.1 fixture. YouTube and Google
properties are HTTP/3 (QUIC) by default in Chrome.

**Steps:**

1. In Chrome (DoH on), watch a couple of YouTube videos and do a few
   Google searches — this drives UDP/443 QUIC.

2. Wait one flush cycle (~5 s), then check for QUIC-sourced hostnames:
   
   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   sqlite3 -readonly -header -column $db "SELECT DISTINCT resolved_host FROM connections WHERE resolved_host LIKE '%google%' OR resolved_host LIKE '%youtube%' OR resolved_host LIKE '%ytimg%' OR resolved_host LIKE '%ggpht%' ORDER BY resolved_host LIMIT 30;"
   ```

**Pass criteria:** Google/YouTube hostnames appear
(`www.youtube.com`, `*.googlevideo.com`, `*.ytimg.com`, etc.). Their
endpoints are predominantly QUIC, so a hit here is the live proof that
DCID-derived key schedule + HKDF + AES-128-GCM AEAD decrypted a real
Initial and read the SNI. Zero rows after sustained YouTube use means
the QUIC path is not delivering — check substrate (§7) and confirm the
captured Initial wasn't truncated (`--pkt-size 1600` must hold the
full datagram; AEAD is all-or-nothing).

---

## G3 — Self-monitoring zero-own-traffic (invariant #1, re-run)

**Goal:** Phase 8.6 adds a capture session (PktMon child process **or**
a raw `SIO_RCVALL` socket). Both are receive-only — the
zero-own-traffic invariant must still hold. Same gate as Phase 6.8b /
Phase 8 G3, re-run with the new session active.

**Steps:**

1. Service running + UI open and active for ≥5 min of real browsing:
   
   ```powershell
   $svcPid = (Get-CimInstance Win32_Service -Filter "Name='ZenVizor'").ProcessId
   $uiPid  = (Get-Process ZenVizor.Ui -ErrorAction SilentlyContinue).Id
   "svc=$svcPid  ui=$uiPid"
   ```

2. Query for any `connections` rows attributed to either ZenVizor PID:
   
   ```powershell
   $db = "C:\ProgramData\ZenVizor\zenvizor.db"
   sqlite3 -readonly -header -column $db "SELECT a.image_name, ps.pid, c.remote_addr, c.bytes_up, c.bytes_down FROM connections c JOIN process_sessions ps ON ps.session_id = c.session_id JOIN apps a ON a.app_id = ps.app_id WHERE a.image_name IN ('ZenVizor.Service.exe','ZenVizor.Ui.exe') ORDER BY c.last_seen DESC LIMIT 20;"
   ```

3. **Raw-socket substrate caveat.** If §7 reported the **RawSocket**
   substrate, double-check: `SIO_RCVALL` opens a promiscuous receive
   socket. Receiving is not transmitting — no bytes leave — but it is
   worth an explicit eyeball that the service shows **zero `bytes_up`**.
   The `pktmon` child process (if present) is a Microsoft binary, not
   a ZenVizor PID, and also originates nothing.

**Pass criteria:** zero rows. Any row with `bytes_up > 0` or
`bytes_down > 0` for a ZenVizor PID is a **stop-the-world** invariant
#1 violation.

---

## G4 — Performance under sustained inbound bulk (the stress case)

**Goal:** the new substrate processes every TCP/443, UDP/443, and
TCP/80 packet header (then the per-flow gate drops already-classified
flows). The worst case is a high-rate inbound stream. Idle CPU < 1%
and service WS < ~80 MB must hold **under a sustained large HTTPS
download** — the inbound-bulk stress case the Phase 8.5 spike flagged.

**Steps:**

1. Start a sustained large download over HTTPS (a multi-GB file, or a
   long 4K YouTube stream). Let it run ≥3 min so the flow is in
   steady state (the per-flow gate should have classified it once and
   be dropping the rest).

2. Sample working set + CPU mid-download:
   
   ```powershell
   $svcPid = (Get-CimInstance Win32_Service -Filter "Name='ZenVizor'").ProcessId
   $ws = (Get-Process -Id $svcPid).WorkingSet64 / 1MB
   "{0:N1} MB working set" -f $ws
   Get-Counter "\Process(ZenVizor.Service)\% Processor Time" -SampleInterval 1 -MaxSamples 5 |
       Select-Object -ExpandProperty CounterSamples |
       Measure-Object CookedValue -Average | Select-Object Average
   ```

**Pass criteria:** working set < ~80 MB; CPU average < 1.0 % even
during the download. The per-flow bounded-LRU gate is what makes this
hold — cost scales with **new-flow** rate, not packet rate, so a
single fat download is one classify then a stream of cheap drops. If
CPU climbs with throughput, the gate is leaking (re-parsing
classified flows) — diagnose, don't accept a new ceiling.

---

## Known limitations

### ECH-enabled origins (Encrypted ClientHello) — the residual gap

Phase 8.6 reads the SNI out of the **plaintext** ClientHello (TLS 1.2
and TLS 1.3-without-ECH) and the decrypted QUIC Initial. TLS 1.3
**Encrypted ClientHello (ECH)** encrypts the inner ClientHello —
including the SNI — to the origin's public key, so the server name is
not in the clear and is not recoverable by passive observation. This
is by design (it's the privacy feature working) and is the one
structural limit carried forward from the Phase 8 DoH gap.

- **Who this affects today:** a small but growing minority. Cloudflare
  fronts ECH for sites that opt in; Chrome/Firefox honour it when the
  origin advertises an `HTTPS`/`SVCB` ECH config. For those flows the
  Connections grid shows **IP-only**, same as a DoH-cache-hit row.
- **Honesty in the UI (not just this doc):** the AppDetail "Remote
  endpoint" info popup says the name "is shown when ZenVizor can work
  it out; some connections stay IP-only because the name was hidden
  inside encrypted traffic and can't be read." That copy covers both
  the ECH case and ordinary connection-reuse misses without
  over-promising. (`src/ZenVizor.Ui/Views/AppDetailPage.xaml`.)
- **Cross-reference:** the matching note lives in
  `docs/phase-8-verification.md` → *Known limitations* (it was written
  forward-looking when Phase 8 closed). Both docs and the UI carry the
  same honesty pattern, per the Phase 8.6 acceptance criterion.

There is no passive workaround — recovering an ECH SNI would require
the origin's private key or an active MITM, both of which violate the
"passive monitor, not a firewall" charter. We surface the gap; we do
not chase it.

### Inherited Phase 8 limits

Connection reuse, cache hits, and pre-install rows still cap hit rate
below 100 % even on recovered apps — see
`docs/phase-8-verification.md` → *Coverage notes*. Phase 8.6 widens
*which apps* get hostnames; it does not change those per-flow facts.

---

## Sign-off

**CI:** 554/554 solution-wide (Debug), 2026-06-21. The 41 Phase 8.6
tests cover all six headless acceptance criteria — TLS 1.2/1.3-no-ECH
SNI, QUIC decrypt against the RFC 9001 §A.1 vector + closed-loop SNI,
HTTP Host, malformed-never-throws, per-flow gate, and the
store-wire-up that lands a hostname for a matching `remote_addr`.

**Manual gates: walked 2026-06-21 (dev box, Win11 Home 10.0.26200) — all pass.**

- [x] **§7 PktMon control surface** — RESOLVED. Started on the PktMon
      substrate; the capture component is **required** (provider-enable
      alone delivers nothing). Settled via the `pktmon stop`
      counterfactual: a fresh site lands with capture on, not with
      capture off. Closes the open Phase 8.5 follow-up.
- [x] **G1 Chrome-DoH-on hostnames** — pass. `chrome.exe` recovered
      hostnames for the bulk of its flows (TLS + QUIC), materially
      above the Phase 8 ~0 % baseline.
- [x] **G2 QUIC decrypt live** — pass. Google/YouTube hostnames
      rendered, proving the Initial-decrypt path on real traffic.
- [x] **G3 zero-own-traffic** — pass. Zero `connections` rows for
      either ZenVizor PID with the new session active. Invariant #1
      holds.
- [x] **G4 performance under bulk** — pass. WS < ~80 MB, idle CPU
      < 1 % sustained through a large HTTPS download.
- [x] **ECH residual gap documented** — UI info popup + this doc +
      `docs/phase-8-verification.md`, same honesty pattern as the DoH
      note.

**Phase 8.6 status: CLOSED 2026-06-21.** CI green (554/554) and all
four manual gates pass, with the §7 control-surface question resolved.
This closes the Phase 8 DoH coverage gap. Next: Phase 9.6 (re-cut MSI +
Burn bundle) packs the 8.6 service binaries.
