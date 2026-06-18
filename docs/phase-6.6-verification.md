# Phase 6.6 — `zvctl alerts` subcommands

Phase 6.6 adds the `alerts` command group to `zvctl`. Three subcommands:
`list`, `dismiss`, `catalog`. No new IPC methods — `GetAlertsAsync` and
`DismissAlertAsync` already shipped in Phase 6.1; this phase exposes
them via the CLI for scripting + manual QA.

CI doesn't gate this directly (no `ZenVizor.Cli.Tests` project exists;
`zvctl ping` / `snapshot` / `apps` / etc. are all manually-gated). The
IPC contracts are covered by `ZenVizor.Ipc.Tests.InProcessRpcTests`
(`GetAlertsList_Active_RoundTrip` + `DismissAlert_RecordsAlertIdOnHandler`,
unchanged in this phase). The CLI-side novelty is parsing + client-side
post-filter + catalog metadata, all small static helpers — manual gate.

---

## Pre-flight dependencies

None beyond Phases 0–6.5. `zvctl` is a self-contained .NET 10 console
app shipped in the same `bin\Debug\net10.0-windows\` output as the rest
of the solution. No external tools required.

---

## 0. One-time build

```powershell
cd C:\dev\zenvizor
dotnet build .\ZenVizor.slnx -c Debug
dotnet test  .\ZenVizor.slnx -c Debug
```

Test totals unchanged from 6.5 close — **415 pass.**

The CLI binary lives at
`.\src\ZenVizor.Cli\bin\Debug\net10.0-windows\zvctl.exe`. For the rest
of this doc, set a session alias so the examples below can use the
short `zvctl ...` form:

```powershell
Set-Alias zvctl C:\dev\zenvizor\src\ZenVizor.Cli\bin\Debug\net10.0-windows\zvctl.exe
```

The alias persists for the current PowerShell session only. Add to your
`$PROFILE` to make it permanent.

---

## Gate 1 — `zvctl alerts catalog`

Offline. No service / pipe required.

```powershell
zvctl alerts catalog
```

Expected output: six rows. Columns: `Type`, `Severity`, `Source`,
`Producer`, `Description`. Locked values:

| Type | Severity | Source | Producer |
|---|---|---|---|
| UnsignedFromUserPath | Critical | Capture | wired |
| InvalidSignature | Critical | Capture | (none) |
| FirstRunWanTalker | Info | Capture | (none) |
| UnusualDailyVolume | Warning | Rollup | (none) |
| LargeDownload | Info | Capture | (none) |
| OutboundHeavy | Warning | Capture | (none) |

Trailing summary: `6 alert types. 1 producer-wired in this build; the
rest are vocabulary placeholders for post-MVP rules.`

JSON form for scripting:

```powershell
zvctl alerts catalog --json
```

Should emit a 6-element JSON array with the same fields. Exit code 0.

---

## Gate 2 — `zvctl alerts list`

Service must be running (`Start-Service ZenVizor` from elevated PS if
needed).

**2.1 Default (active, no filter).**

```powershell
zvctl alerts list
```

Empty state: `(no alerts matched the filter)`. With at least one active
alert (trigger via the Phase 6.1 fixture binary): tabular output with
`Id`, `Severity`, `Type`, `Created`, `Entity`, `Title` columns and
indented `Detail` line beneath each row. Trailing line:
`N alerts (M active, K dismissed).`

**2.2 State filter.**

```powershell
zvctl alerts list --state dismissed
zvctl alerts list --state all
zvctl alerts list --state nonsense   # exit code 1, helpful error
```

`--state all` shows dismissed rows tagged with `(dismissed)` in the
Severity column.

**2.3 Severity + type filters (client-side).**

```powershell
zvctl alerts list --state all --severity critical
zvctl alerts list --type UnsignedFromUserPath
zvctl alerts list --severity warning --type LargeDownload   # likely empty
zvctl alerts list --type bogus      # exit code 1, lists allowed names
```

Type names match the catalog one-to-one, case-insensitive.

**2.4 Max-rows transport cap.**

```powershell
zvctl alerts list --max-rows 1
```

If more rows would have returned, a trailing `NOTE` line surfaces the
`HasMore` flag and tells the user how to widen.

**2.5 JSON round-trip.**

```powershell
zvctl alerts list --json
```

Emits the raw `IpcEnvelope<AlertsResult>` JSON — `SchemaVersion`,
`Payload.Filter`, `Payload.Alerts` (full `AlertDto` rows),
`Payload.HasMore`.

---

## Gate 3 — `zvctl alerts dismiss`

Service must be running. Use an `Id` from `alerts list`.

```powershell
zvctl alerts list                       # note an active id, say 42
zvctl alerts dismiss 42
# → "Dismissed alert #42."
zvctl alerts list                       # row no longer in active feed
zvctl alerts list --state dismissed     # appears here with (dismissed) tag
```

Idempotency check (per the brief — no `--yes` confirmation, one-click):

```powershell
zvctl alerts dismiss 42                 # already dismissed — echoes anyway
zvctl alerts dismiss 999999             # unknown id — server no-op, echoes
```

Both should print `Dismissed alert #N.` and exit 0. Server-side the
already-dismissed and unknown-id calls are silent no-ops per
`IZenVizorIpc.DismissAlertAsync` contract.

Invalid argument check:

```powershell
zvctl alerts dismiss notanumber
# → System.CommandLine parse error, exit code 1
```

---

## Gate 4 — Cross-page sync after CLI dismiss

The CLI dismiss path goes through the same IPC handler as the UI's
optimistic-dismiss click. Confirm:

1. With the UI open on the Alerts page and at least one active alert,
   note the row's `Id`.
2. From a separate non-elevated PS:
   ```powershell
   zvctl alerts dismiss <id>
   ```
3. The UI Alerts page should remove the row from the active feed on
   the next AlertRaised push or page refresh. The nav-rail badge
   counter should decrement on the next `RefreshAsync` cycle.

Lag is acceptable — the UI doesn't currently subscribe to an
`AlertDismissed` push (only `AlertRaised`). The next page-Loaded or
ServiceReconnected fan-out picks up the change.

---

## Exit codes

Matching the rest of `zvctl`:

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | parse error, invalid argument, generic exception |
| 2 | IPC version mismatch (negotiation rejected, or envelope `SchemaVersion` floor failed) |
| 3 | service unreachable (named-pipe connect timed out) |

---

## Pass criteria

- All four gates exit cleanly with the expected output.
- `catalog` prints the six locked types + their severity / source /
  producer-wired flag without requiring a running service.
- `list` round-trips against the live service, shows the same data the
  Alerts UI feed shows for the same `--state`.
- `dismiss` succeeds idempotently and the UI reflects the change on its
  next refresh cycle.
- JSON output is well-formed for both `list` and `catalog`.
