namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Hot-path counters from the capture aggregator. Primarily for verifying
/// attribution correctness: <c>ObservationsUnattributed</c> growing without
/// matching ETW events is the canonical signal that the lifecycle resolver
/// is failing to keep up with short-lived processes.
/// </summary>
/// <param name="CapturedAtUnixMs">Server wall-clock at snapshot time.</param>
/// <param name="ObservationsSeen">Total ETW observations the aggregator has seen since service start.</param>
/// <param name="ObservationsUnattributed">
/// Subset of <c>ObservationsSeen</c> that could not be attributed to a PID
/// (image resolver returned null, ETW PID was invalid, etc.). After the
/// Phase-3 lifecycle-resolver fix this should stay at or near 0 even under
/// heavy short-lived-process load (curl, single-shot CLI tools).
/// </param>
public sealed record CaptureStats(
    long CapturedAtUnixMs,
    long ObservationsSeen,
    long ObservationsUnattributed);
