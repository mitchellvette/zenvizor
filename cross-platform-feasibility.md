# ZenVizor — Cross-Platform Feasibility Analysis (Linux first, macOS long-term)

> Working document (untracked / scratch). Not part of the curated `docs/` set.

## Context

ZenVizor is today a Windows-only passive network monitor (C# 14 / .NET 10, all
projects targeting `net10.0-windows`). The goal of this document is **not** to
ship cross-platform yet — it is to (a) map exactly how Windows-coupled the
codebase is, (b) prove the single hardest unknown on Linux with a throwaway
spike, and (c) lay out the phased path from there. macOS is documented as a
deferred section because it realistically requires Mac hardware + an Apple
Developer account, neither of which exists yet.

The non-negotiable invariants carry over **unchanged** to every platform — most
importantly **zero own network traffic** and **IPC over OS-local IPC only**
(named pipes on Windows → Unix domain sockets on Linux/macOS, never loopback
TCP). Honest attribution (traffic attributes to the host process; never
fabricate precision) is also platform-neutral.

### Decisions locked in this planning session
- **Linux v1 = a capture feasibility spike**, built as a **standalone throwaway proof** (minimal refactor; prove the hard part, then plan the real build).
- **Capture mechanism: eBPF primary, netlink (`sock_diag`) fallback.**
- **Spike runs in a fresh modern Linux VM on Windows** (e.g. Ubuntu 24.04+ / Fedora) — guarantees a modern kernel + BTF for eBPF and is reproducible; decouples from the old physical box.
- **UI: open-source Avalonia only** (no commercial Avalonia XPF). UI direction is *leaning* Avalonia but **not committed** — tradeoffs are documented below for a later decision. The UI is **out of scope for v1** (the spike is headless).
- **macOS: deferred section only.**
- **Zero-own-traffic invariant is a hard requirement on every platform.**

---

## 1. Current platform-coupling map

The backend already has clean seams; the UI and IPC transport do not.

| Concern | Interface seam | Windows impl | Coupling | Linux replacement |
|---|---|---|---|---|
| Capture | `ICaptureSource` (`src/ZenVizor.Capture/ICaptureSource.cs`) | `EtwCaptureSource.cs` | hard (ETW) | **new eBPF/netlink source** |
| PID correction | `IPidTableSnapshotSource` (`ZenVizor.Core`) | `Attribution/IpHelper/IpHelperPidTableSource.cs` | hard (P/Invoke iphlpapi) | netlink `sock_diag` / `/proc/net/*` |
| Service resolution | `IServiceHostResolver` (+ `NoOpServiceHostResolver`) | `Attribution/Services/ScmServiceHostResolver.cs` | hard (SCM/advapi32) | systemd (cgroup→unit, D-Bus) |
| Signature verify | `ISignatureVerifier` | `Attribution/Authenticode/WinVerifyTrustSignatureVerifier.cs` | hard (WinVerifyTrust) | pkg provenance + path heuristics (no Authenticode on Linux) |
| Service host | `IHostedService` | `ZenVizor.Service/Program.cs` (`AddWindowsService`, Serilog EventLog) | medium | systemd `Type=notify`, journald/syslog sink |
| IPC transport | **none yet** | `ZenVizorPipeServer.cs` / `ZenVizorPipeClient.cs` (NamedPipe + `PipeSecurity` ACLs) | hard, but isolated | Unix domain socket + filesystem perms |
| IPC contracts | `IZenVizorIpc` | `ZenVizor.Ipc.Contracts` (pure DTOs) | **none — portable as-is** | reuse |
| Storage | (path/ACL seam, partial) | `Storage/StorageConstants.cs` path + `Service/ProgramDataAcl.cs` | moderate (path + ACLs) | XDG/`/var/lib` path + Unix `0700`/chown |
| CLI (`zvctl`) | depends only on IPC client | `ZenVizor.Cli/Program.cs` | minimal | retarget TFM + swap IPC client |
| Installer | n/a | WiX MSI + Burn bundle | hard, Windows-only | `.deb`/`.rpm` + systemd unit |

**Takeaway:** the four backend `I*` seams + the synthetic capture source + the
no-op resolver mean the *backend* is already structured for porting. The two
real architectural gaps are (1) **no IPC transport abstraction** (pipe types
referenced directly in server/client) and (2) **a pure-WPF UI with no
abstraction**. All projects are `net10.0-windows`; none are neutral yet.

---

## 2. Linux v1 — the capture feasibility spike (the immediate deliverable)

**Goal:** prove that ZenVizor can do **passive, per-PID byte attribution on
Linux** that (a) feeds the *existing* `ZenVizor.Core` aggregation unchanged and
(b) emits **zero network traffic of its own**. Nothing else. Throwaway code is
acceptable; the point is to retire risk before committing to the real port.

### 2.1 Mechanism — eBPF primary
- Attach eBPF programs (kprobe/fentry) to the kernel send/recv paths:
  `tcp_sendmsg`, `tcp_cleanup_rbuf` (rx), `udp_sendmsg`, `udp_recvmsg`/`skb_consume_udp`.
- In each probe, read `bpf_get_current_pid_tgid()` + the byte count and
  accumulate into a per-PID (or per-socket) BPF hash map. Userspace reads the
  map on the existing flush interval — **this matches the ETW model**: the same
  shape of (pid, direction, bytes, proto) observation `EtwCaptureSource`
  produces, and the same as `SyntheticCaptureSource` for tests.
- **Honest-attribution alignment:** probes fire in the *current process
  context*, so injected/host-surfaced code attributes to the host process —
  identical to invariant 5 on Windows. No new attribution semantics.
- **Loading approach for the spike:** start with a **CO-RE eBPF object** (clang +
  libbpf, BTF-based "compile once / run everywhere") loaded via libbpf P/Invoke
  from a thin .NET host implementing `ICaptureSource`. If libbpf interop slows
  the spike, a `bpftrace` JSON-emitting one-liner parsed by the C# host is an
  acceptable *throwaway* stand-in to prove the signal, since the spike is
  explicitly disposable.

### 2.2 Mechanism — netlink fallback (older kernels)
- `NETLINK_SOCK_DIAG` (`inet_diag`) enumerates sockets with `tcp_info`
  (`bytes_sent`/`bytes_received` on newer kernels); socket inode → PID via
  `/proc/[pid]/fd`. Polling-based, coarser, **weak UDP byte attribution**.
- **Critical compliance note:** a netlink socket is `AF_NETLINK`, **not**
  `AF_INET` — it does **not** traverse the IP stack and is **not** observed as
  network traffic. Likewise `bpf()` map reads are syscalls, not sockets. Both
  mechanisms therefore satisfy the zero-own-traffic invariant.

### 2.3 Privilege model
Service runs as root, or (preferred) with `CAP_BPF` + `CAP_PERFMON` +
`CAP_NET_ADMIN` granted via the systemd unit's `AmbientCapabilities`. Mirrors
Windows LocalSystem.

### 2.4 Spike scope (what to build / what to skip)
**Build:**
- A `net10.0` (neutral) spike host implementing `ICaptureSource` against
  eBPF (+ netlink fallback), emitting the existing observation type.
- Wire it into the existing `ZenVizor.Core` aggregation to prove end-to-end.
- A capability probe (kernel version / BTF present) that selects eBPF vs netlink.

**Skip (defer to the real port):** IPC, UI, storage path changes, service
resolution (use `NoOpServiceHostResolver`), signature verification, packaging.

### 2.5 Critical files
- Reuse: `src/ZenVizor.Capture/ICaptureSource.cs`, `SyntheticCaptureSource.cs`
  (reference for the observation shape), `ZenVizor.Core` aggregation.
- New (throwaway): a Linux capture host project + a small eBPF artifact.
  Keep it out of the shipped solution graph so it can't drift into prod.

### 2.6 Spike environment & tooling (surface up front)
Fresh Ubuntu 24.04+ (or Fedora) VM in Hyper-V/VirtualBox. Required tooling — install **before** running the spike so a missing dep doesn't stall validation:
```
sudo apt update
sudo apt install -y clang llvm libbpf-dev linux-headers-$(uname -r) bpftool bpftrace
# .NET 10 SDK:
sudo apt install -y dotnet-sdk-10.0    # or Microsoft package feed if not in distro repo
```
(`bpftrace` only needed if using the throwaway bpftrace stand-in.) Confirm BTF: `ls /sys/kernel/btf/vmlinux`.

---

## 3. The broader Linux path (after the spike proves out)

Phased, each phase independently testable. This is the *real* port, not the spike.

1. **TFM split — make the backend neutral.** Multi-target or split so
   `ZenVizor.Core`, `ZenVizor.Ipc.Contracts`, `ZenVizor.Storage`,
   `ZenVizor.Ipc.{Server,Client}`, `ZenVizor.Cli` build as `net10.0`; keep
   Windows impls (`EtwCaptureSource`, IP Helper, SCM, WinVerifyTrust) under
   `net10.0-windows` guards. `Core` is "models + interfaces only" today, so it
   moves cleanly.
2. **IPC transport abstraction.** Introduce `IIpcServer`/`IIpcClient` over the
   existing `ZenVizorPipeServer`/`Client`; add a Unix-domain-socket impl. Drop
   `PipeSecurity`/SID ACLs on Unix in favour of socket-file permissions
   (`0660`, owned by the service user/group). Contracts (`IZenVizorIpc`) are
   already neutral — no change. Honors the "reuse/extend IPC, don't multiply
   methods" preference.
3. **Productionize Linux capture** from the spike learnings (CO-RE object,
   libbpf interop, capability-gated eBPF↔netlink selection) as a real
   `ICaptureSource`.
4. **Linux PID correction & service resolution.** netlink/`/proc` PID source;
   systemd-based `IServiceHostResolver` (PID→cgroup→unit). `NoOpServiceHostResolver`
   remains the safe default.
5. **Service host.** Swap `AddWindowsService` for systemd integration
   (`Type=notify`, sd_notify readiness); EventLog sink → journald/syslog or file.
6. **Storage path/ACL seam.** Extract data-path resolution: `/var/lib/zenvizor`
   (or XDG) instead of `%ProgramData%`; replace `ProgramDataAcl` with Unix
   perms + chown. `Microsoft.Data.Sqlite` is already cross-platform.
7. **Signature verification (Linux).** No Authenticode equivalent. Implement
   `ISignatureVerifier` via **package provenance** (`dpkg -S`/`rpm -qf` → binary
   owned by a distro package = trusted) + path heuristics (binary under
   `/home`, `/tmp`, world-writable = untrusted). This preserves the MVP alert
   ("unsigned binary from a user-writable path making connections") in spirit.
8. **Packaging.** `.deb`/`.rpm` carrying a systemd unit (capabilities, demand
   start), `postinst` for data-dir creation + perms. No Burn/.NET-runtime
   bundle needed if depending on `dotnet-runtime-10.0`; self-contained otherwise.

---

## 4. UI — WPF → Avalonia tradeoffs (open-source only; decision deferred)

The UI is a **thin shell**: logic is reached over IPC and there are **zero**
custom value converters, so ViewModels/IPC clients move as-is. The cost is the
view layer.

**Ports cleanly:** business logic + IPC clients; LiveCharts2 (already
SkiaSharp; `LiveChartsCore.SkiaSharpView.Avalonia` 2.0.5 is the *same engine* —
near-identical visuals across ~4 chart instances); the **in-page `<Grid>`
overlay popover pattern** (your canonical recipe — pays off here); Avalonia
supports .NET 10 (current 12.0.5).

**High-friction items:**
- **Styling rewrite** — Avalonia replaces WPF triggers + `ControlTemplate` with
  a CSS-like selector/pseudo-class system. ~281 trigger/template/static
  occurrences; **36 `ControlTemplate`s**, the heaviest being the custom
  **DataGrid cell/row templates** (drill-grid SelectionPill + compact density),
  which is central to ZenVizor's UX.
- **Wpf.Ui (lepoco) has no Avalonia build** → swap to **FluentAvalonia**
  (NavigationView + SymbolIcon equivalents) + re-theme. Version coupling:
  FluentAvalonia v2.3 ↔ Avalonia 11, v3.0 ↔ Avalonia 12.
- **Text-rendering discipline doesn't transfer** — Avalonia uses Skia/HarfBuzz,
  so the `TextOptions` rendering-trio memory is moot; re-tune, don't reuse.
  Mica/acrylic is Windows-only; Skia blur/shadows are costlier (your shadows are
  already inert fallbacks → minor).
- **Linux system tray** is desktop-environment-dependent (StatusNotifierItem)
  and unreliable across DEs — close-to-tray UX may degrade.
- **Single-instance** (mutex + named pipe) needs a cross-platform redo.
- **DesignTokens.xaml**: ~70% (colors/spacing/type) ports directly; ~30%
  (button/datagrid styles, triggers) needs rework. Keep the
  DesignTokens.xaml ↔ `colors_and_type.css` crosswalk discipline intact.

**Not chosen (per your decision):** Avalonia XPF (commercial WPF-compat layer)
would cut UI port effort dramatically but is a paid license in tension with the
free/donateware GPL model — excluded.

---

## 5. macOS — deferred section (what it would take)

Document now, build later. Hard prerequisites you don't yet have: **a Mac**
(build + test; Avalonia/macOS and codesign tooling are Mac-only) and an **Apple
Developer account** (codesigning + notarization for distribution).

- **Capture:** no ETW/eBPF. Options: Apple **Network Extension** /
  **Endpoint Security** framework (entitlement-gated, requires notarization), or
  `libpcap` + socket-table correlation (coarser PID attribution). All must
  remain passive + zero-own-traffic.
- **PID/socket tables:** `libproc` / `proc_pidinfo` (no `/proc`).
- **Signature verify:** `codesign` / Security.framework / Gatekeeper status —
  cleaner than Linux (binaries are commonly signed).
- **Service host:** `launchd` plist instead of systemd.
- **IPC:** Unix domain sockets (same as Linux) — or XPC, but UDS keeps one path.
- **Packaging:** `.pkg` / notarized `.app`, Developer ID signing.

Design guidance now to avoid a corner: keep the IPC transport seam UDS-based
(works on both Linux and macOS), and keep `ISignatureVerifier` /
`IServiceHostResolver` as the only platform-specific attribution surfaces.

---

## 6. Invariant compliance (cross-platform)

- **Zero own traffic:** eBPF (`bpf()` syscalls) and netlink (`AF_NETLINK`)
  open no `AF_INET` sockets. IPC stays on Unix domain sockets, never loopback
  TCP. No telemetry/update/DNS. The self-monitoring test gate must pass on
  Linux exactly as on Windows.
- **IPC = OS-local only:** UDS has OS-enforced peer identity (`SO_PEERCRED`) and
  filesystem ACLs — the Linux analogue of pipe ACLs; gate data at the IPC
  handler (invariant 3), not by tightening transport perms.
- **Honest attribution:** eBPF current-process-context attribution preserves
  "traffic attributes to the host process" with no new fabrication.
- **No per-event DB writes / signature cache:** unchanged — aggregation and
  caching live in `Core`, which the spike feeds directly.

---

## 7. Verification — how to validate the spike end-to-end

In the Linux VM, as root (or with the capabilities above):
1. **Signal correctness:** start the spike host; generate known traffic
   (`curl -o /dev/null <large file>`); confirm the curl PID's rx bytes ≈
   download size and direction is correct. Repeat for a UDP generator.
2. **Core integration:** confirm observations land in the existing
   `ZenVizor.Core` aggregation (60s buckets) with the right per-PID totals —
   reuse the determinism style of the synthetic-source tests where possible.
3. **Zero-own-traffic gate (the key one):** point the tool at itself — the
   ZenVizor spike process must report ~0 network bytes. Independently verify it
   opens no IP sockets: `ss -tunap | grep <pid>` shows nothing, and
   `strace -f -e trace=socket -p <pid>` shows no `AF_INET`/`AF_INET6` sockets
   (only `AF_NETLINK`/`bpf`).
4. **Fallback path:** force the netlink path (or test on an older-kernel VM) and
   confirm it still produces per-PID attribution (accepting coarser UDP).

**Spike success = all four pass.** That retires the central Linux risk and
unblocks planning the real port (§3).

---

## 8. Open questions / risks to track
- eBPF kprobe symbol stability across kernels — CO-RE/BTF mitigates, but
  validate on at least two kernel versions before productionizing.
- UDP byte attribution fidelity on the netlink fallback (known weak) — decide
  acceptable floor.
- Linux "unsigned binary" semantics (§3.7) are heuristic, not cryptographic —
  confirm the MVP alert's intent survives that translation.
- Avalonia DataGrid custom-template rework is the single largest UI cost — size
  it with a throwaway DataGrid spike before committing to the UI port.
