# Epic H — Passive network devices

**Release:** 1.8.0 (minor) if the spike succeeds; otherwise deferred and
the 1.8.0 slot advances to Epic I · **Status:** spike (feasibility spike
required before planning)
**Depends on:** the spike result determines whether this is a real feature or
shelves to ARP-only. Slotted at 1.8.0 to preserve alphabetical
epic-to-version alignment; ships on its own whenever the spike succeeds,
not bundled into any other release.

> Stub: this epic is **not plannable** until the spike answers the question
> below. Capturing the shape + the hard invariant line so it isn't
> re-litigated.

---

## Intent

Surface the devices on the local network — ideally **named** (smart-home gear,
phones, printers) — populating the reserved `devices` table.

## Reserved infrastructure (already in place)

- **`devices` table** is defined and reserved (not yet populated):
  `mac, ip, interface, hostname, first_seen, last_seen, is_known`
  (`001_initial.sql`).
- The **`IMonitor` seam** exists precisely so additional passive watchers slot
  in as new implementations without re-architecting.

## The spike question (green-lit)

Does the **existing capture** expose inbound **mDNS / SSDP / NetBIOS** payloads
**without us emitting anything**? Candidates already in `ZenVizor.Capture`:

- `EtwCaptureSource` (Microsoft-Windows-Kernel-Network).
- `DnsCaptureSource`, `SniCaptureSource` (TLS ClientHello SNI / QUIC / HTTP
  host parsing) and the raw packet sources (`PktMonPacketSource`,
  `RawSocketPacketSource`).

These already demonstrate **passive observation of arriving packets** (parsing
SNI and DNS answers from traffic we are merely receiving) — strong precedent
that passive multicast observation is architecturally consistent. The spike
must confirm a capture source can *see* inbound multicast
(mDNS `224.0.0.251:5353`, SSDP `239.255.255.250:1900`, NetBIOS) **without
joining the group**.

**Spike deliverable:** yes/no on inbound-multicast visibility + which source
exposes it. Yes → passive device discovery + naming is a real feature. No →
shelve to ARP / neighbor-cache read only (lower value).

**User feedback captured:** ARP-only is "low value." The interesting version
is passive mDNS/SSDP/NetBIOS naming via existing capture.

## Hard invariant line (non-negotiable — invariant 1)

Observe **already-arriving** traffic via existing capture **only**:

- **Never** open a listening socket.
- **Never** join a multicast group.
- **Never** send probes (no active scan, no ARP who-has emission).
- **Never** do reverse-DNS.

Device names come **only** from passively-seen mDNS / DNS / NetBIOS. The whole
product promise is zero own traffic; this epic does not get an exception.

## Version classification

**1.x (minor):** populates the reserved `devices` table + a devices view. The
spike gates whether/when this is planned.
