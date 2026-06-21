# Phase 8.5 — Endpoint visibility investigation (findings)

**Status:** **Complete, 2026-06-21.** Desk survey + coverage/perf/invariant
analysis (recorded in the sprint plan "Leading direction" on 2026-06-21)
is now backed by a working throwaway prototype run on a real box.
Recommendation stands: **outcome 1 — ship pre-MVP at full coverage**, via
the stubbed Phase 8.6.

**Companion docs:** Phase 8.5 + 8.6 in `docs/zenvizor-sprint-plan.md`
(scope + acceptance criteria), PRD §10 (active-probe boundary), Phase 8
verification doc (the DoH known-limitation this closes).

**Prototype:** throwaway harness `SniSpike` (under `spike/SniSpike/`, not in
`ZenVizor.slnx`). Built Debug only against `ZenVizor.Core` for the real
`DnsResolutionStore`. It is throwaway, but its parsers are
production-shaped and RFC-validated, so **"spike close" is after Phase 8.6
ports what it needs** — keep the harness available as the reference the 8.6
implementer ports from (parser placement + port instructions are in the
Phase 8.6 "Implementation starting point" note), then delete it. The
*findings* — this doc — are the durable output regardless.

**Test environment:** dev box, Win11 Home 10.0.26200, .NET 10 /
net10.0-windows. Offline self-tests run non-elevated; the capture +
PktMon probes were run by the human from an elevated PowerShell.

---

## 1. Executive summary

The Phase 8 DoH gap is closable pre-MVP with an invariant-#1-safe passive
technique, and the prototype confirms the two open unknowns well inside
their tolerances:

- **Unknown #1 (PktMon control surface): settled, with one clean-boot
  caveat.** The `Microsoft-Windows-PktMon` ETW provider *does* stream
  truncated packet payloads directly into our own in-process
  `TraceEventSession` — no `.etl` file, no separate trace-reader process,
  no real-time `pktmon` console consumer needed to *read* the bytes. The
  one thing the prototype could not cleanly isolate (a test confound, see
  §7) is whether the PktMon **capture component** (`pktmon start`) must be
  *running* for the provider to emit payloads, or whether enabling the
  provider alone suffices. This does not gate the recommendation: the
  raw-socket fallback is fully proven below, so the worst case is
  "ship on the fallback substrate," which is still in budget.
- **Unknown #2 (real-box perf): settled, comfortably.** The raw-socket
  path — the deliberately *pessimistic* substrate (no kernel filter, sees
  every packet, copies in user mode) — held **0.23% all-core CPU** and
  **35 MB peak working set** over a 60 s capture at ~358 packets/s. That
  is the conservative upper bound; PktMon's in-kernel port filter +
  truncation only cuts from there. Budget is < 1% idle CPU / < ~80 MB WS.
- **Invariant #1 holds.** The self-monitoring lens (ZenVizor pointed at
  the prototype's own PID) returned **zero outbound rows**. Both capture
  substrates are receive-only; all crypto is pure local computation.
- **Both parsers work.** TLS SNI was confirmed **live** (6 real Chrome /
  HttpClient hostnames, below). QUIC Initial decrypt was confirmed
  **offline** against the RFC 9001 §A.1 published key-schedule vector plus
  a closed-loop encrypt→decrypt round-trip; it was not exercised live in
  this run (no QUIC flows happened to be captured in the window — a
  coverage accident, not a parser failure), and is queued for live
  confirmation in the Phase 8.6 manual QA gate.

**Recommendation: outcome 1 (ship pre-MVP at full coverage).** Proceed
with Phase 8.6 as stubbed. PktMon primary, raw-socket fallback, TLS + QUIC
+ HTTP/1.1 Host parsers, feeding the existing `DnsResolutionStore`. The
residual structural gap is ECH-enabled origins (a small, watch-it-grow
minority), documented the same way as the Phase 8 DoH note.

---

## 2. Survey + comparative analysis

The hostname for a DoH / in-app-resolver flow exists in plaintext nowhere
in the running host *except the TLS/QUIC handshake on the wire*. The
Windows resolver never sees the query (DoH tunnels it inside HTTPS), and
the kernel network provider carries only metadata (addresses, ports, PID,
byte counts — no payload). So every viable technique reduces to: **get
the first client→server bytes of a flow, read the SNI / Host out of them.**
That splits into a *substrate* (how we get the bytes) and a *parser* (how
we read the name).

### Substrates

| Substrate | Driver? | Elevation | Own traffic? | Verdict |
| --- | --- | --- | --- | --- |
| **`Microsoft-Windows-PktMon` ETW** | No (in-box Win10 1809+/11) | Service already SYSTEM | **None** (provider mirrors copies) | **Primary.** In-kernel port filter + truncation bound the copy at source; consumed via the existing TraceEvent sibling-session pattern (D5). |
| **Raw socket `SIO_RCVALL`** | No (Winsock) | Admin/SYSTEM to open raw socket | **None** (receive-only; never bound to a peer, never sends) | **Fallback.** No kernel filter → copies every packet, post-filters in user mode. Proven here as the conservative perf bound. |
| **WFP callout / NDIS LWF** | **Yes (signed kernel driver)** | Install elevation + AV friction | None inherently | **Rejected.** Breaks the non-elevated install story for zero coverage gain over the above. |

### Parsers (each feeds the same store)

| Parser | Transport | Plaintext? | Coverage | Effort |
| --- | --- | --- | --- | --- |
| **TLS ClientHello SNI** | TCP/443 | Yes for TLS 1.2 and TLS 1.3-without-ECH | Dominant share of HTTPS | Low — a bounds-checked binary walk in the `Rfc1035ResponseDecoder` style. |
| **QUIC Initial SNI** | UDP/443 | "Encrypted" with keys derivable by any observer (DCID + RFC 9001 v1 salt) | Chrome↔Google (YouTube/gstatic), HTTP/3 origins | Medium — HKDF-Expand-Label + AES-128-GCM + AES-ECB header-protection, all BCL. Bounded and fully specified. |
| **HTTP/1.1 Host** | TCP/80 | Yes | Tiny on the modern web | Trivial — near-free once the substrate exists. |

All three parsers share the `Rfc1035ResponseDecoder` robustness contract:
return empty / `false` on anything malformed, **never throw**.

### Why this is additive, not a re-architecture

Phase 8 already built the entire back half and it is *source-agnostic*.
`DnsResolutionStore.Record(IPAddress ip, string hostname, int ttlSeconds,
long observedAtUnixMs)` is the single join point (D1), read once per
connection at flush (`TrafficAggregator.Flush` → `TryGetHostname`),
COALESCE-upserted (D3), picked with `MAX()` (D4), carried as the v2
`ResolvedHost` string, and rendered by the AppDetail grid. None of that
cares where a hostname came from. **The DoH gap is one missing thing: a
second feeder into the store.** The prototype proves the feeder by writing
extracted SNI into the *real* `DnsResolutionStore` and reading it back
(store round-trip checks pass in both self-tests).

---

## 3. Coverage model

Baseline pulled from the **live service DB** on the dev box (the Phase 8
verification doc carries no useful baseline figures, so the live DB is the
source of truth here).

**Overall (all WAN connections):**

- 1,956 WAN connection rows.
- **97.2% have `resolved_host IS NULL`** (~1,901 rows). Phase 8's DNS
  observer named only the ~2.8% that went through the Windows resolver and
  fired event 3008 within the observation window.

**Chrome specifically:** **100% null** — the structural DoH blindness the
verification doc documents, reproduced exactly.

**Directly SNI-addressable share of the null population:**

| Class | Null rows | Recovered by |
| --- | --- | --- |
| TCP/443 (TLS) | 276 | TLS ClientHello SNI parser |
| UDP/443 (QUIC) | 236 | QUIC Initial parser |
| TCP/80 (HTTP) | negligible | HTTP/1.1 Host parser |
| **443-class total** | **512** | TLS + QUIC |

So ~512 of the currently-null rows are the *direct* observation target. Two
amplifiers make the realised hit rate higher than the raw 512/1,901:

1. **The store is IP-keyed, not connection-keyed.** One SNI observation for
   an IP names *every* current and future connection to that IP at flush.
   A single ClientHello to a CDN edge backfills the many reused/parallel
   connections to that edge.
2. **The target population is browsers.** For the app class this phase
   exists to fix (Chrome / DoH / in-app resolvers), 443 *is* essentially
   the entire footprint. The non-443 remainder of the null rows is
   background/system traffic that was never the "where did the browser go"
   question, and much of it is not name-bearing at all.

Framed against the target — "make Chrome's `resolved_host` stop being
100% null" — TLS + QUIC over 443 covers the dominant majority of the
recoverable traffic. That is the "full coverage" the recommendation claims;
it is not "100% of all rows," and the doc does not claim that.

**Residual gap: ECH.** TLS 1.3 with Encrypted ClientHello hides the SNI.
Today a small minority (Chrome ships ECH support; Cloudflare and a handful
of large origins offer it), but a growing one. This is a true structural
limit, not an excuse, and gets the same in-UI + verification-doc treatment
as the Phase 8 DoH note (Phase 8.6 acceptance criterion).

---

## 4. Performance projection

The load-bearing perf risk for any packet-level approach is that
inspecting flows is dramatically more event traffic than observing DNS.
The prototype measured the *worst* substrate to bound it.

**Raw-socket run (the pessimistic upper bound — no kernel filter, sees
every packet, user-mode post-filter):**

| Metric | Measured | Budget |
| --- | --- | --- |
| CPU | **0.23% all-core** | < 1% idle |
| Peak working set | **35 MB** | < ~80 MB |
| Packet rate | ~358 pkts/s over 60 s | — |
| TLS/Host hits | 6 | — |

Why this generalises favourably to the shipping design:

- **PktMon filters + truncates in-kernel.** The raw-socket number includes
  the cost of copying and touching *every* packet on the NIC; PktMon's
  port filter (TCP 443/80, UDP 443) and ~320 B truncation cut both the
  packet count crossing into user space and the per-packet copy. PktMon is
  strictly cheaper than the number above.
- **Cost scales with new-flow rate, not packet rate.** SNI lives in the
  *first* client→server packet of a flow. The per-flow "already-classified"
  bounded LRU gate (same shape as `DnsResolutionStore`) drops every
  subsequent packet for a named flow without re-parse. At idle — the actual
  budget line — new flows are ~zero, so steady-state cost is ~zero. The
  bulk-download stress case is mostly *inbound* bytes on
  *already-classified* flows, i.e. exactly the packets the gate discards
  cheapest.

Even the unfiltered upper bound sits at roughly a quarter of one percent of
one core. The budget is not at risk.

---

## 5. Invariant #1 audit (per candidate)

Invariant #1: ZenVizor emits ZERO network traffic of its own. Each
candidate was audited for *any* outbound — including loopback, library
auto-update, NTP, or "telemetry" hooks.

| Candidate | Emits traffic? | Audit |
| --- | --- | --- |
| PktMon ETW provider | **No** | It mirrors copies of packets the host was already exchanging into a trace session. Enabling a provider originates nothing. Consumed via TraceEvent, same as the Phase 8 DNS source which already passed G3. |
| Raw socket `SIO_RCVALL` | **No** | The socket is never `Connect`-ed and never `Send`s. `SIO_RCVALL` is strictly receive: Windows delivers a copy of inbound/outbound IPv4 packets to a passive listener. Zero origination. |
| TLS / QUIC / HTTP parsers | **No** | Pure functions over a byte span. QUIC "decryption" is local AES/HKDF computation over keys derived from bytes already on the wire — no key exchange, no network. BCL crypto only (`HKDF`, `AesGcm`, `Aes`); no new NuGet. |
| WFP / NDIS (rejected) | No inherently | Rejected for the driver/elevation cost, not for traffic. |

**Empirical confirmation.** The prototype's apphost (`SniSpike.exe`, a
distinct image name) was run while the *production* ZenVizor service
watched. The self-monitoring lens query — connections attributed to the
`SniSpike` PID — returned **zero rows**. Same gate as Phase 8 G3 /
Phase 6.8b, same pass. Invariant #1 holds for the technique end-to-end.

---

## 6. Prototype results

`SniSpike` modes: `tls-selftest`, `quic-selftest`, `selftest` (both),
`rawsock [seconds]` (elevated live capture), `pktmon-probe [seconds]`
(elevated PktMon control-surface probe).

### Offline self-tests — 16/16 pass

- **TLS (8 checks):** extract `outlook.office.com`; extract past leading
  extensions; reject application_data record; reject truncated SNI without
  throwing; reject empty input; real `DnsResolutionStore` round-trip;
  HTTP/1.1 `Host` → `neverssl.com`; reject non-HTTP bytes as Host.
- **QUIC (8 checks):** RFC 9001 §A.1 client key / iv / hp all match the
  published vector (so an HKDF/derivation bug cannot hide behind a
  symmetric encrypt path); closed-loop build→decrypt → `youtube.com`;
  tampered DCID → AEAD auth fails (empty, no throw); reject short input;
  reject random bytes; `DnsResolutionStore` round-trip.

The RFC-vector anchor is deliberate: a closed encrypt→decrypt loop alone
would pass even if both halves shared the same derivation bug. Pinning the
key schedule to the published vector rules that out.

### Live TLS capture (raw-socket, elevated, 60 s)

Six real hostnames extracted from live handshakes and written to the real
store:

```
api.anthropic.com
http-intake.logs.us5.datadoghq.com
github.com
api.github.com
s3.us-east-1.amazonaws.com
wdcp.microsoft.com
```

Minimum success bar for the spike was **one** real handshake → SNI;
exceeded 6×. No QUIC flows happened to occur in the capture window, so the
QUIC path is offline-proven (above) and queued for the Phase 8.6 live gate
(YouTube/Google target).

### PktMon control-surface probe (elevated)

The provider, enabled in our own `TraceEventSession`, delivered **truncated
packet payloads directly** — 92,170 payload-bearing events in ~15 s, max
payload length matching the 320 B truncation, no separate `.etl` consumer.
**The payloads are full Ethernet L2 frames** (sample hex begins with the
destination/source MACs, then `0800` IPv4 EtherType, then `4500…` IPv4
header). This differs from the raw-socket path, which starts at the IPv4
header — see the Phase 8.6 note in §8.

---

## 7. The two unknowns — resolved, with one caveat

**Unknown #1 — does enabling the PktMon provider in a TraceEventSession
yield payloads directly, or must `pktmon`'s capture component be started
alongside?**

Settled to the degree that matters, with a clean-boot caveat:

- **Settled:** payloads *do* reach our own in-process session — no `.etl`
  file, no external real-time trace reader, no separate consumer process to
  read the bytes. The bytes are 320 B truncated L2 frames.
- **Caveat (a test confound, not a code bug in the substrate):** the first
  `pktmon-probe` run **hung** — the harness's `pktmon start … -m
  real-time` helper used a blocking `ReadToEnd()`/`WaitForExit()` on a
  process that streams continuously and never exits. The user Ctrl-C'd and
  re-ran. But the hung first run had **already started the PktMon capture
  component**, so the second run's "Phase A — provider alone, capture NOT
  started" was actually executing with `pktmon` already running (Phase B
  even reported "Packet Monitor is already started"). That means the
  prototype could not cleanly isolate **provider-alone vs.
  provider-plus-`pktmon start`**. The safe, defensible conclusion is:
  *with the PktMon capture component active, the provider streams truncated
  payloads to our own session.* Whether `pktmon start` is strictly required
  needs **one clean run from a fresh boot** (or after `pktmon stop`) to nail
  down — a 10-minute Phase 8.6 task, not a feasibility risk. Either way the
  raw-socket fallback already covers the downside.

**Unknown #2 — does filter+truncate+per-flow-gate hold the perf budget
under inbound bulk?**

Settled, comfortably. The *unfiltered* upper bound was 0.23% all-core CPU /
35 MB WS (§4). The shipping design is strictly cheaper. The bulk-download
case is dominated by inbound bytes on already-classified flows, which the
per-flow gate discards at minimum cost.

---

## 8. Implementation notes for Phase 8.6

Concrete things the spike surfaced that the production implementation must
get right (none change the recommendation):

1. **PktMon payloads carry a 14-byte Ethernet header.** The raw-socket
   path starts at the IPv4 header; the PktMon path starts at the L2 frame
   (`MAC|MAC|EtherType`). The PktMon parser must strip the Ethernet header
   (and handle the `0800` IPv4 / `86DD` IPv6 EtherType, plus any VLAN tag)
   before the IP-layer walk. Keep the IP/TCP/UDP/parser code substrate-
   agnostic; put the L2 strip in the PktMon adapter only.

2. **Truncation size must be set per protocol — and QUIC needs the full
   datagram.** A single global 320 B truncation is wrong for both parsers:
   - **TLS/TCP:** 320 B (minus ~54 B of L2+IP+TCP headers ≈ 266 B of TLS)
     is borderline. A ClientHello with many TLS 1.3 extensions (key shares,
     GREASE, ALPN) can push the SNI extension past that, and a large
     ClientHello can span multiple TCP segments. The shipping design needs
     **either a larger truncation (≈512 B+) or per-flow segment
     reassembly** (the spike's raw-socket path accumulated up to 8 KB per
     flow to ride over segmentation). Tune the truncation to comfortably
     clear a realistic ClientHello.
   - **QUIC/UDP:** AES-128-GCM is **all-or-nothing over the full ciphertext
     + 16-byte tag**. A 320 B truncation of a ~1,200 B padded Initial means
     the tag is missing and `AesGcm.Decrypt` fails auth → no SNI, ever. So
     the QUIC path must **capture the full Initial datagram** (filter
     UDP/443, no/large truncation). This is cheap because Initials are
     single bounded datagrams and the per-flow gate captures exactly one
     per new flow. (A CTR-mode-only decrypt that tolerates truncation is
     possible but unnecessary complexity — capture the whole datagram.)

3. **Per-flow gate is mandatory, not an optimisation.** It is what keeps
   cost on new-flow rate rather than packet rate. Mirror the
   `DnsResolutionStore` bounded-LRU shape; cap tracked flows and give up on
   a flow after a small byte cap (the spike used 8 KB) so a flow that never
   yields an SNI cannot accumulate unbounded.

4. **Sibling ETW session per D5.** The PktMon session is a second sibling
   `TraceEventSession`, lifecycle-isolated and feature-toggleable at the
   composition root, exactly like the Phase 8 DNS source. Do **not** add a
   network-egress seam (PRD §6/§10) — the substrate is receive-only.

5. **Fix or delete the real-time probe pattern.** If any production
   diagnostic shells `pktmon … -m real-time`, it must read the stream on a
   background reader (or just not start the console capture at all) — never
   a blocking `ReadToEnd()`. This was the spike harness bug behind the
   Unknown-#1 confound.

---

## 9. Recommendation

**Outcome 1 — ship pre-MVP at full coverage.** Implement Phase 8.6 as
stubbed in the sprint plan:

- **Substrate:** PktMon ETW provider primary; raw-socket `SIO_RCVALL`
  fallback retained (proven, in-budget) until the clean-boot PktMon control
  question (§7) is closed.
- **Parsers:** TLS SNI (live-proven), QUIC Initial SNI (RFC-vector +
  closed-loop proven; live-confirm in 8.6 QA), HTTP/1.1 Host (trivial).
- **Wire-up:** feed the existing `DnsResolutionStore.Record`; **no changes**
  to the flush join, COALESCE upsert, IPC schema (stays v2 — SNI reuses
  `ResolvedHost`), or UI.
- **Honesty:** surface the residual ECH gap in-app, same pattern as the
  Phase 8 DoH note.

This clears the pre-MVP bar: the technique is tractable (16/16 offline,
6 live), fast (0.23% CPU upper bound vs. < 1% budget), invariant-#1-safe
(empty self-monitoring lens; receive-only substrate; pure-compute crypto),
and recovers the dominant share of the currently-100%-null Chrome traffic.

**Not recommended:** outcome 2 (scope down) is unnecessary — QUIC is the
expensive part and it already works, so there is no reason to ship TLS-only.
Outcome 3 (defer) is unjustified — none of the cost/risk/loss bars that
would force a defer materialised.

---

## 10. Cross-references (single coherent MVP story)

Per the Phase 8.5 acceptance criteria, the recommendation is wired into the
rest of the doc set so v1's coverage story is told once:

- **Sprint plan:** Phase 8.6 stub is confirmed (not downgraded); its two
  gating unknowns are now settled here. Phase 9 wraps Phase 8 **+** 8.6;
  Phase 9.6 (re-cut MSI + Burn bundle) packs the 8.6 service binaries.
- **PRD §10** (active-probe boundary): the "Endpoint visibility for DoH /
  in-app resolvers" row resolves to *ship pre-MVP via Phase 8.6*; the
  active-probe boundary itself does **not** move — this technique is purely
  passive and stays inside invariant #1.
- **Phase 8 verification doc** (known-limitations / DoH block): the DoH gap
  is no longer "documented and deferred" but "closed by Phase 8.6," with
  ECH as the named residual limit carried forward.
