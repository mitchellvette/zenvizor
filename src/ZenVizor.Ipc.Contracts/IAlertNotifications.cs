using System.Threading.Tasks;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// Client-side callback surface for server-pushed alert events. The UI
/// registers a concrete implementation as a JsonRpc local target so the
/// service can invoke <see cref="OnAlertRaisedAsync"/> via
/// <c>JsonRpc.NotifyAsync</c> when an alert producer raises a new row.
/// <para>
/// This is the first server-to-client notification surface in ZenVizor.
/// The pattern is StreamJsonRpc's canonical "client-as-target" — the
/// client adds its IAlertNotifications implementation to its own JsonRpc
/// instance before <c>StartListening</c>, and the server tracks each
/// connection's JsonRpc to broadcast notifications.
/// </para>
/// <para>
/// Method names on this interface must remain stable: StreamJsonRpc
/// dispatches by name. Renaming <c>OnAlertRaisedAsync</c> breaks the wire
/// (server NotifyAsync calls would never reach the client).
/// </para>
/// </summary>
public interface IAlertNotifications
{
    /// <summary>
    /// Invoked by the server when a producer raises a new alert. The
    /// implementation typically forwards the payload to the UI thread,
    /// updates the in-memory alert collection, increments the active-set
    /// summary counts, and fires the nav-rail badge pulse.
    /// </summary>
    Task OnAlertRaisedAsync(AlertDto alert);
}
