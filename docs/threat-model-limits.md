# Threat-model limits

Honest scope boundaries for ZenVizor's passive network detection. This
document records what the detection pipeline **cannot** see by construction
— independent of which alert rules are wired or which producers ship — so
future phases can revisit each item deliberately rather than discovering
the gap during an incident.

**Maintenance contract:** add an entry when an investigation surfaces a
genuine detection gap that is NOT addressable by the next planned phase.
Don't add things that are simply "not yet implemented" — those belong in
the sprint plan. The bar here is "fundamental limitation given the
current architecture / data sources."

---

## Detection gaps as of Phase 6.1a

### 1. DNS-based covert channels through the OS resolver

**What we miss:** A process that exfiltrates data by encoding it in DNS
queries (subdomain names of an attacker-controlled domain) and using the
standard Windows DNS Client for lookups. The malicious process never
opens its own socket — it asks `svchost.exe` (DNS Client service) to do
the lookup. ZenVizor's ETW capture sees a UDP send to the DNS server,
attributed to **the svchost PID hosting DNS Client**, not the calling
process.

**Why:** ZenVizor attributes traffic to the process that owns the socket.
For DNS through `DnsQuery_*` / `getaddrinfo`, that's always
`svchost.exe`. Per-caller attribution would require either
(a) hooking the DNS Client API at user-mode (out of scope per CLAUDE.md
invariant — purely passive monitoring), (b) parsing the kernel DNS
events from a separate ETW provider AND correlating to the caller PID
via some other channel (no reliable ETW signal exists for this), or
(c) snooping the local DNS resolver cache and inferring callers (still
ambiguous when multiple processes resolve the same name).

**Adversary sophistication:** Low to moderate. DNS exfiltration tooling
is freely available (Cobalt Strike, dnscat2, Mythic). This is a real
gap, not a theoretical one.

**Possible future mitigations:**
- Surface unusual DNS query volume per-svchost host with a separate alert
  type, even without per-caller attribution. The user can correlate
  manually.
- Attempt cross-correlation with ETW's `Microsoft-Windows-DNS-Client`
  provider, which fires events with the calling process's PID for
  programs that call DNS APIs directly. Coverage is incomplete (only
  fires for some API paths) but better than nothing.
- Document in user-facing copy: "ZenVizor sees what processes open
  sockets. DNS queries through the OS resolver are attributed to the
  Windows DNS Client service. Watch for unusual volume there."

---

### 2. ICMP-based covert channels

**What we miss:** A process that exfiltrates data by encoding it in ICMP
Echo Request (ping) payload bytes to an attacker-controlled server. The
ICMP traffic never appears in the TCP/UDP capture path.

**Why:** Phase 1 enabled only `KernelTraceEventParser.Keywords.NetworkTCPIP`
on the kernel ETW provider. ICMP fires under a separate keyword and a
different event family; we don't subscribe to it.

**Adversary sophistication:** Moderate. Requires raw-socket privileges
(usually Administrator), which limits the attack surface — but elevated
malware is exactly the class of adversary an unsigned-from-user-folder
alert is trying to catch. Tools exist (Loki, icmpsh).

**Possible future mitigations:**
- Add ICMP to the ETW keyword set in `EtwCaptureSource.Start()` —
  cost is per-event overhead during normal ping volume (low for typical
  desktops) and an additional handler family. Treat ICMP traffic as a
  fourth `RemoteClass` distinct from TCP/UDP for bookkeeping clarity.
- Surface an alert type "ICMP traffic from process X" when the source
  isn't a known network diagnostic tool (`ping.exe`, `tracert.exe`,
  `mtr.exe`). High signal-to-noise.

---

### 3. Kernel-mode rootkits and hooked user-mode APIs

**What we miss:** A driver-level rootkit that operates below ETW (e.g.,
modifies the kernel's network stack directly, replaces functions in
`tcpip.sys`, or hides events from ETW dispatch). Also: user-mode
malware that bundles its own TCP/IP stack (DPDK-style) bypassing the
Windows networking subsystem entirely.

**Why:** ETW is implemented above the kernel's networking layer; if an
adversary patches or hooks below ETW's dispatch point, ETW simply stops
seeing the traffic. ZenVizor has no detection signal at all in that
case. Custom user-mode TCP/IP stacks are extremely rare on Windows
(most malware uses Winsock for compatibility reasons), but they exist
in the sophisticated APT toolkit.

**Adversary sophistication:** High. Driver-signing requirements on
modern Windows (KMCS) make rootkit deployment painful — typically
requires a kernel exploit, a stolen code-signing cert, or a vulnerable
signed driver to load (BYOVD). Custom stacks require nation-state-tier
engineering.

**Possible future mitigations:** None at the user-mode passive-monitor
layer. Document as a known limit of the threat model. Detection at this
adversary tier requires kernel-mode telemetry (Sysmon, EDR), which is a
fundamentally different product class than ZenVizor.

---

## How to add entries

When a future investigation surfaces a new gap, add a section with:

1. **What we miss** — concrete scenario, single paragraph.
2. **Why** — the technical reason in terms of the current data sources.
3. **Adversary sophistication** — Low / Moderate / High, with a
   one-line justification (tools available? requires elevation? requires
   nation-state-tier engineering?).
4. **Possible future mitigations** — specific architectural options,
   even ones we don't plan to take. Useful for future-us to evaluate.

Do NOT add:

- "We haven't shipped feature X yet" — that's sprint plan scope.
- "We have a bug in module Y" — that's `docs/known-bugs.md`.
- Speculative threats with no concrete attack pattern.

The list should be short and load-bearing; if it grows to dozens of
entries we've lost discipline about what belongs here.
