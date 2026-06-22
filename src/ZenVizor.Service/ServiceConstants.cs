// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Service;

internal static class ServiceConstants
{
    /// <summary>
    /// Display + SCM service name. Keep in sync with installer scripts.
    /// </summary>
    public const string ServiceName = "ZenVizor";

    /// <summary>
    /// Windows EventLog source name. Created on first run (requires admin).
    /// </summary>
    public const string EventLogSource = "ZenVizor";

    /// <summary>
    /// Standard Windows event log to write to.
    /// </summary>
    public const string EventLogName = "Application";
}
