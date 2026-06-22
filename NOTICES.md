# Third-party notices

ZenVizor is distributed under the GNU General Public License v3.0 or
later (see `LICENSE`). It incorporates and depends on the following
third-party software, each governed by its own license. License
identifiers below use SPDX expressions; consult the upstream project
for the full license text.

This file lists direct dependencies declared in
`Directory.Packages.props` plus the runtime components that ZenVizor's
installer bundles or relies on. Transitive dependencies inherit the
license terms of their respective publishers and are listed only where
they appear in the shipped product output.

---

## Runtime dependencies (shipped with ZenVizor)

### IPC and serialization

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| StreamJsonRpc | MIT | https://github.com/microsoft/vs-streamjsonrpc |
| Nerdbank.Streams | MIT | https://github.com/AArnott/Nerdbank.Streams |
| MessagePack | MIT | https://github.com/MessagePack-CSharp/MessagePack-CSharp |
| MessagePack.Annotations | MIT | https://github.com/MessagePack-CSharp/MessagePack-CSharp |
| Newtonsoft.Json (transitive via StreamJsonRpc) | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| Microsoft.VisualStudio.Threading (transitive) | MIT | https://github.com/microsoft/vs-threading |
| Microsoft.VisualStudio.Validation (transitive) | MIT | https://github.com/microsoft/vs-validation |

### Storage

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| Microsoft.Data.Sqlite | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.core | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite engine (native, bundled via SQLitePCLRaw) | blessing (public-domain equivalent) | https://www.sqlite.org/copyright.html |

### Capture and attribution

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| Microsoft.Diagnostics.Tracing.TraceEvent | MIT | https://github.com/microsoft/perfview |

### Hosting and logging

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| Microsoft.Extensions.Hosting | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Hosting.WindowsServices | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.EventLog | MIT | https://github.com/dotnet/runtime |
| Serilog | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Extensions.Hosting | Apache-2.0 | https://github.com/serilog/serilog-extensions-hosting |
| Serilog.Sinks.File | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| Serilog.Sinks.EventLog | Apache-2.0 | https://github.com/serilog/serilog-sinks-eventlog |

### User interface

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| WPF-UI (lepoco/wpfui) | MIT | https://github.com/lepoco/wpfui |
| H.NotifyIcon.Wpf | MIT | https://github.com/HavenDV/H.NotifyIcon |
| LiveChartsCore.SkiaSharpView.WPF | MIT | https://github.com/beto-rodriguez/LiveCharts2 |
| LiveChartsCore (transitive) | MIT | https://github.com/beto-rodriguez/LiveCharts2 |
| LiveChartsCore.SkiaSharpView (transitive) | MIT | https://github.com/beto-rodriguez/LiveCharts2 |
| SkiaSharp (transitive) | MIT | https://github.com/mono/SkiaSharp |
| SkiaSharp.HarfBuzz (transitive) | MIT | https://github.com/mono/SkiaSharp |
| SkiaSharp.Views.WPF (transitive) | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp (transitive) | MIT | https://github.com/mono/SkiaSharp |

### CLI

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| System.CommandLine | MIT | https://github.com/dotnet/command-line-api |

### .NET runtime

| Component | License (SPDX) | Upstream |
| --- | --- | --- |
| .NET 10 Desktop Runtime (bundled by ZenVizorSetup.exe) | MIT | https://github.com/dotnet/runtime |

The .NET 10 Desktop Runtime is distributed by Microsoft under the MIT
License and is chained as a separate, non-modified payload inside the
ZenVizor installer bundle. It remains installed after ZenVizor is
uninstalled because it is a shared component.

---

## Build-time / installer toolchain (NOT included in installed product)

The WiX Toolset is used at build time to produce the MSI and the Burn
bootstrapper bundle. The WiX source is licensed under the Microsoft
Reciprocal License (Ms-RL, an OSI-approved license). The compiled
binary releases are also subject to an Open Source Maintenance Fee
(OSMF) Agreement for revenue-generating users; ZenVizor's posture on
the OSMF is documented in `docs/licensing-wix-osmf.md`.

The Burn bootstrapper runtime that the installer artifact embeds is
covered by an additional permission under GPL-3.0 Section 7; see the
clause at the end of `LICENSE`.

| Package | License (SPDX / other) | Upstream |
| --- | --- | --- |
| WixToolset.Sdk 6.0.1 | MS-RL (source); OSMF on binary release | https://github.com/wixtoolset/wix |
| WixToolset.Util.wixext 6.0.1 | MS-RL (source); OSMF on binary release | https://github.com/wixtoolset/wix |
| WixToolset.Bal.wixext 6.0.1 | MS-RL (source); OSMF on binary release | https://github.com/wixtoolset/wix |
| WixToolset.NetFx.wixext 6.0.1 | MS-RL (source); OSMF on binary release | https://github.com/wixtoolset/wix |

---

## Test-only dependencies (NOT included in installed product)

The following packages are referenced only by projects under `tests/`
and are not redistributed with ZenVizor.

| Package | License (SPDX) | Upstream |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |
| xunit | Apache-2.0 | https://github.com/xunit/xunit |
| xunit.runner.visualstudio | Apache-2.0 | https://github.com/xunit/visualstudio.xunit |
| FluentAssertions | Apache-2.0 | https://github.com/fluentassertions/fluentassertions |
| coverlet.collector | MIT | https://github.com/coverlet-coverage/coverlet |

---

## Updating this file

When a dependency is added, removed, upgraded across a license-changing
boundary, or replaced, update the relevant table in this file in the
same commit. The `<license>` element of each package's `.nuspec` is
authoritative; consult the local NuGet cache
(`%USERPROFILE%\.nuget\packages\<id>\<version>\<id>.nuspec`) or the
upstream package page on nuget.org when verifying.
