namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// SCM start-mode subset exposed in the Settings page. Mirrors a strict
/// subset of <c>SERVICE_AUTO_START</c> / <c>SERVICE_DEMAND_START</c> /
/// <c>SERVICE_DISABLED</c>. The UI only surfaces a binary toggle today
/// (Automatic vs Manual per §6.2 Q3) — Disabled stays valid on the wire so
/// future operator-only escapes don't need a schema bump.
/// </summary>
public enum ServiceStartMode
{
    /// <summary>Service auto-starts at boot. Default.</summary>
    Automatic = 0,

    /// <summary>Service is registered but does not auto-start; user must launch it.</summary>
    Manual = 1,

    /// <summary>Service registration is disabled; even <c>sc start</c> fails until re-enabled.</summary>
    Disabled = 2,
}
