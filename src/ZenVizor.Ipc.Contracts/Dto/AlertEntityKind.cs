namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Domain entity an alert references. MVP populates only <see cref="App"/>
/// and <see cref="Session"/>; <c>Device</c> and <c>File</c> are reserved
/// per the catalog §1.6 + PRD §7.6 roadmap but no producer writes them in
/// Phase 6.
/// </summary>
public enum AlertEntityKind
{
    /// <summary>The alert is about a deduplicated program (apps.app_id).</summary>
    App = 0,

    /// <summary>The alert is about one specific run of a PID (sessions.session_id).</summary>
    Session = 1,
}
