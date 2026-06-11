namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// JSON-RPC error codes the ZenVizor service returns for typed faults.
/// Values stay outside the standard JSON-RPC reserved range
/// (-32768..-32000) so they don't collide with framework codes.
/// </summary>
public static class IpcErrorCode
{
    /// <summary>
    /// Client called an envelope-tier RPC before completing
    /// <see cref="IZenVizorIpc.NegotiateVersionAsync"/>. Phase-0 methods
    /// (Negotiate / Ping / GetServiceStatus) stay callable pre-negotiation.
    /// </summary>
    public const int NegotiationRequired = -31001;

    /// <summary>
    /// One or more arguments failed server-side validation
    /// (window, grain, or appId). The client should fix the request — retrying
    /// without changes will fail identically.
    /// </summary>
    public const int InvalidArgument = -31002;
}
