using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Attribution;

/// <summary>
/// Applies the PID-correction layer per PRD §8 step 2:
/// - On the receive path (Direction.Down), ETW can fire in DPC context with the
///   wrong PID. We trust the IP Helper table by local endpoint.
/// - On the send path (Direction.Up), the ETW PID is generally correct; we use
///   it as-is and only fall back to the snapshot when the PID is missing.
/// </summary>
public sealed class PidCorrector
{
    /// <summary>
    /// Resolve the authoritative PID for <paramref name="observation"/> against
    /// <paramref name="snapshot"/>. Returns null if no PID can be attributed.
    /// </summary>
    public int? Correct(NetworkObservation observation, PidTableSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(snapshot);

        return observation.Direction switch
        {
            Direction.Down => CorrectReceive(observation, snapshot),
            Direction.Up   => CorrectSend(observation, snapshot),
            _ => observation.Pid,
        };
    }

    private static int? CorrectReceive(NetworkObservation observation, PidTableSnapshot snapshot)
    {
        if (snapshot.TryGetOwningPid(observation.Protocol, observation.LocalEndpoint, out var owning))
        {
            return owning;
        }

        // No snapshot entry — fall back to whatever ETW gave us (may be null).
        return observation.Pid;
    }

    private static int? CorrectSend(NetworkObservation observation, PidTableSnapshot snapshot)
    {
        if (observation.Pid is int etwPid)
        {
            return etwPid;
        }

        if (snapshot.TryGetOwningPid(observation.Protocol, observation.LocalEndpoint, out var owning))
        {
            return owning;
        }

        return null;
    }
}
