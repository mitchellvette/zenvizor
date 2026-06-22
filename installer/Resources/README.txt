================================================================================
ZenVizor Read Me
================================================================================

ZenVizor is a passive Windows network monitor. It attributes outbound and
inbound traffic to the originating process and (for svchost.exe) the
specific hosted service, stores history locally in SQLite, shows a
near-live dashboard, and produces daily reports. It is not a firewall:
there is no blocking, shaping, or active intervention of any kind.

ZenVizor emits zero network traffic of its own. No telemetry, no update
checks, no DNS lookups, no analytics. This is a hard invariant verified
by self-monitoring at every release.

This file is also available at:
  %ProgramFiles%\ZenVizor\README.txt


--------------------------------------------------------------------------------
1. INSTALL LAYOUT
--------------------------------------------------------------------------------

  %ProgramFiles%\ZenVizor\Service\    Service binaries
  %ProgramFiles%\ZenVizor\Ui\         WPF dashboard
  %ProgramFiles%\ZenVizor\Cli\        zvctl.exe (added to system PATH)
  %ProgramData%\ZenVizor\             SQLite database + config

The data directory under %ProgramData%\ is ACL'd to SYSTEM and
Administrators only. Standard users (including the logged-in user)
cannot read the database file directly. All data is reached through the
"zvctl" CLI or the dashboard, both of which go through a named-pipe IPC
the service owns.


--------------------------------------------------------------------------------
2. LAUNCHING THE DASHBOARD
--------------------------------------------------------------------------------

From the Start menu:

  Start -> ZenVizor -> ZenVizor

Or from any PowerShell session, sanity-check that the service is
reachable:

  zvctl ping


--------------------------------------------------------------------------------
3. COMMAND-LINE INTERFACE (zvctl)
--------------------------------------------------------------------------------

After install, "zvctl" is on your system PATH and runs from any shell.
The verbs below are the ones the service exposes over IPC.

  zvctl ping
    Round-trip a ping over the named-pipe IPC.

  zvctl status
    Print the service status from the IPC handshake.

  zvctl stats
    Print capture-pipeline observation counters.

  zvctl snapshot
    Print the current per-app activity snapshot.

  zvctl apps
    List apps with traffic in the window.

  zvctl app
    Show detail for one app (summary, sessions, time series).

  zvctl connections
    List endpoints an app talked to in the window.

  zvctl history
    Aggregate traffic time series across all apps.

  zvctl report
    Fetch the Phase-5 daily report for one date.

  zvctl alerts
    Read and dismiss alerts; print the catalog of types.

  zvctl alerts list
    List alerts matching the filter (newest first).

  zvctl alerts dismiss
    Mark an alert as dismissed. Idempotent — already-dismissed and unknown ids succeed silently on the wire; CLI echoes confirmation either way.

  zvctl alerts catalog
    Print the AlertType catalog: locked severity, source monitor, producer-wired status, one-line description.

  zvctl alerts run-rollup-rules-now
    Phase 6.7 QA hook: force the rollup-source rules (UnusualDailyVolume) to re-evaluate without waiting for the next UTC day-roll. Idempotent.

For full options on any command, run:

  zvctl <command> --help


--------------------------------------------------------------------------------
4. CONTROLLING THE WINDOWS SERVICE
--------------------------------------------------------------------------------

ZenVizor installs a Windows service named "ZenVizor" that runs as
LocalSystem. The start mode is "demand": the service starts
automatically the first time the dashboard or "zvctl" connects to it,
and stays up while clients are connected.

To start or stop the service manually (requires admin):

  sc.exe start ZenVizor
  sc.exe stop  ZenVizor

Or use services.msc and find "ZenVizor".


--------------------------------------------------------------------------------
5. DATA LOCATION AND HOW TO WIPE IT
--------------------------------------------------------------------------------

Your local network telemetry lives in:

  %ProgramData%\ZenVizor\zenvizor.db

The default uninstall preserves this file so a reinstall or upgrade
keeps your history. To wipe it on uninstall:

  ZenVizorSetup.exe /uninstall REMOVE_DATA=1

Or, after uninstall, manually delete:

  %ProgramData%\ZenVizor\


--------------------------------------------------------------------------------
6. UNINSTALL
--------------------------------------------------------------------------------

Settings -> Apps -> ZenVizor -> Uninstall.

Or from a shell:

  ZenVizorSetup.exe /uninstall

The .NET 10 Desktop Runtime that the installer bundles is intentionally
left in place on uninstall, because it is a shared component that other
applications may also use.


--------------------------------------------------------------------------------
7. LICENSE AND NOTICES
--------------------------------------------------------------------------------

ZenVizor is licensed under the GNU General Public License v3.0 or later.

  LICENSE.txt    The full GPL-3.0 text plus a Section-7 additional
                 permission that allows ZenVizor to be combined and
                 distributed with the WiX Toolset installer runtime.
  TRADEMARK.txt  The "ZenVizor" trademark policy. The name and logo are
                 common-law trademarks and are NOT licensed under the
                 GPL.
  NOTICES.txt    Third-party components incorporated into ZenVizor and
                 the licenses they are distributed under.

All three are installed alongside this README in %ProgramFiles%\ZenVizor\.


--------------------------------------------------------------------------------
8. SUPPORT AND MORE INFORMATION
--------------------------------------------------------------------------------

ZenVizor is open-source software. The source repository, issue tracker,
and project documentation are at the canonical ZenVizor project URL
(see the channel through which you downloaded ZenVizorSetup.exe).

Trademark inquiries, brand-use questions, and reports of misuse are
covered in TRADEMARK.txt.

================================================================================
