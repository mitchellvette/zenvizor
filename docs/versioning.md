# Versioning policy

This document describes how ZenVizor versions its user-visible
product releases (`<Version>` in `Directory.Build.props`) and how that
product version relates to the internal IPC schema versions that the
service and clients negotiate over the named pipe.

The single source of truth for the product version is the
`<Version>` element in `Directory.Build.props`. Both WiX projects
(`installer/ZenVizor.wixproj`, `installer/Bundle/ZenVizor.Bundle.wixproj`)
read `$(Version)` through `DefineConstants` and consume it as
`$(var.Version)` inside the `.wxs` source. A version bump is a
one-line edit.

---

## Product SemVer

ZenVizor follows Semantic Versioning for the product as a whole.
After the 1.0.0 ship, increments fall into three buckets:

- **`1.0.x` — bugfix releases.** No behaviour changes that surprise
  users. A regression fix, a corrected attribution edge case, an
  installer fix, a localisation fix. Existing installations upgrade
  in place with no user-visible surface change beyond the fix itself.

- **`1.x.0` — new features.** Post-MVP modules from PRD §10
  (Hosts-file watcher, ARP cache watcher, system-proxy watcher,
  device fingerprinting, additional alert producers) and any Phase 8
  follow-ups land here. The IPC surface may gain new methods or
  trailing optional fields; the DB schema may gain new tables or
  trailing nullable columns. Existing automation, scripts pinned to
  documented CLI verbs, and existing user data continue to work
  without intervention.

- **`2.0.0` — breaks in user-visible contracts.** Reserved for
  changes that require user or operator action to upgrade cleanly.
  See the next section for what counts.

---

## What triggers a major version bump

A release becomes `2.0.0` (or later) when ANY of the following apply:

- **IPC contract changes that exceed the additive-tolerance rule.**
  Renaming or removing an IPC method, reordering positional fields
  on an existing DTO, changing a field's type, or breaking a
  client's ability to deserialize a payload from the prior server.
  Adding a new method or a trailing optional/required field to an
  existing payload is additive — the relevant per-surface
  `IpcSchemaVersion` constant bumps but the product stays in the
  current major. See `src/ZenVizor.Ipc.Contracts/IpcSchemaVersion.cs`
  for the full set of per-surface schema versions and their
  evolution history.

- **DB schema migrations that drop or rename existing columns,
  require a rebuild from raw events, or otherwise can't be applied
  to an existing `%ProgramData%\ZenVizor\zenvizor.db` in place.**
  Adding a new table or a trailing nullable column is additive and
  stays inside the current major.

- **Config layout changes that don't carry over.** Renaming or
  removing config keys, moving the data directory, changing how
  settings are stored such that a fresh install on top of an
  existing `%ProgramData%\ZenVizor\` would surface the old values
  incorrectly.

- **Removing a documented `zvctl` verb or changing its output
  contract in a way that would break a pinned script.** Adding new
  verbs or new optional flags is additive.

When in doubt, ask: "would a user with a working `1.x.y` install,
their own data, and any automation they've written around `zvctl`
need to do something to upgrade cleanly?" If yes, that's a `2.0.0`.

---

## The MSI UpgradeCode is locked

`installer/ZenVizor.wxs` pins `UpgradeCode="DAB3A65D-8347-44EE-8946-B8CD57474539"`
for the lifetime of the product. The `<MajorUpgrade>` element carries
forward across every `1.x.x` AND `2.x.x` release — Windows treats a
new MSI with the same `UpgradeCode` as an upgrade of the existing
installation rather than a coexisting product. Do not change
`UpgradeCode` when bumping the major version; the SemVer `2.0.0`
signal is for users and release notes, not for the Windows Installer
upgrade machinery.

---

## IPC schema versions are orthogonal

Each IPC payload family carries its own incremental schema version,
defined in `src/ZenVizor.Ipc.Contracts/IpcSchemaVersion.cs`. That
file is the live source of truth — values are not duplicated here
because they would drift.

The rule for evolving an IPC payload without a major-version bump:

- Payloads are positional `record` types serialized via STJ.
- Adding a trailing field (optional or required) to an existing
  payload is an additive change. Bump that surface's
  `IpcSchemaVersion` constant by one, document the bump in the
  `<remarks>` block on the constant, and rely on the client-side
  floor check to reject older servers.
- Reordering, removing, or retyping an existing field is NOT
  additive. That is a `2.0.0`-class change.

`ProtocolVersion.Major` / `ProtocolVersion.Minor` (in
`src/ZenVizor.Ipc.Contracts/ProtocolVersion.cs`) is a separate
negotiation handshake — it gates whether a client and server are
allowed to talk at all, before any per-surface payload version
floor check runs.
