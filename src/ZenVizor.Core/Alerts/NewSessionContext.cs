using ZenVizor.Core.Storage;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// The rule's view of a <see cref="NewSessionEvent"/> — slimmer projection
/// that surfaces just the fields rules read. Rules don't need to know about
/// session ids or flush timestamps; isolating the inputs here keeps
/// <see cref="IAlertRule"/> implementations easy to test (mock a context, no
/// aggregator scaffolding needed).
/// <para>
/// <see cref="WanConnection"/> is intentionally NOT projected away — its
/// timestamp drives the catalog template's "First connection: …" phrase on
/// initial raise. On <c>UpdateDetail</c> the producer swaps in the cached
/// original timestamp from a prior raise, so the rendered string is stable
/// across subsequent observations.
/// </para>
/// </summary>
public sealed record NewSessionContext(
    int AppId,
    string ImagePath,
    string ImageName,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath,
    PendingConnection WanConnection,
    long FlushTimeUnixMs)
{
    public static NewSessionContext From(NewSessionEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(evt.App);
        return new NewSessionContext(
            AppId:              evt.AppId,
            ImagePath:          evt.App.ImagePath,
            ImageName:          evt.App.ImageName,
            Publisher:          evt.App.Publisher,
            SignatureStatus:    evt.App.SignatureStatus,
            IsUserWritablePath: evt.App.IsUserWritablePath,
            WanConnection:      evt.WanConnection,
            FlushTimeUnixMs:    evt.FlushTimeUnixMs);
    }
}
