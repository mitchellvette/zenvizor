// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Attribution;

/// <summary>
/// Consumer of kernel-emitted process lifecycle events. Used so the capture
/// layer can populate an image cache the moment a process starts, rather than
/// trying to resolve image identity after the fact (which races process exit
/// for short-lived processes — the bug Phase 3 reliability work targets).
/// </summary>
public interface IProcessLifecycleSink
{
    /// <summary>
    /// Called when the kernel emits a process-start event. <paramref name="imagePath"/>
    /// should be the full image path when available; implementations may fall
    /// back to a basename if the kernel only provided a short name.
    /// </summary>
    void OnProcessStart(int pid, string imagePath, long startUnixMs);

    /// <summary>
    /// Called when the kernel emits a process-exit event. The cached entry
    /// for this PID is typically retained for a grace window so trailing
    /// network events (delivered after process exit due to ETW buffering)
    /// still resolve.
    /// </summary>
    void OnProcessStop(int pid, long stopUnixMs);
}
