namespace TitaniRun.Ipc.Contracts;

/// <summary>
/// Constants shared by IPC server, client, and CLI.
/// </summary>
public static class IpcConstants
{
    /// <summary>
    /// Named-pipe name (without the <c>\\.\pipe\</c> prefix).
    /// The trailing <c>v1</c> matches <see cref="ProtocolVersion.Major"/>.
    /// </summary>
    public const string PipeName = "TitaniRun.Ipc.v1";
}
