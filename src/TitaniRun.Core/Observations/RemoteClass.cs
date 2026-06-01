namespace TitaniRun.Core.Observations;

/// <summary>
/// Coarse classification of a remote endpoint. Matches the string values
/// persisted to <c>traffic_samples.remote_class</c> and <c>connections.remote_class</c>.
/// </summary>
public enum RemoteClass
{
    /// <summary>
    /// RFC1918, loopback, link-local (v4 169.254/16, v6 fe80::/10),
    /// IPv6 ULA fc00::/7, IPv6 loopback ::1.
    /// </summary>
    Local,

    /// <summary>Anything else — public/internet-routable address.</summary>
    Wan,
}

public static class RemoteClassExtensions
{
    /// <summary>Stable string form for storage (capitalized to match the schema enum text).</summary>
    public static string ToStorageString(this RemoteClass value) => value switch
    {
        RemoteClass.Local => "Local",
        RemoteClass.Wan => "Wan",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
