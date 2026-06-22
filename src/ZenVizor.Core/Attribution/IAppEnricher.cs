// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Attribution;

/// <summary>
/// Composes signature verification, publisher extraction, and the
/// user-writable-path heuristic into a single per-binary lookup. Implementations
/// MUST cache by <c>(image_path, mtime, size)</c> so the underlying signature
/// verification runs once per binary version (Phase 2 Q1).
/// </summary>
/// <remarks>
/// Called from <see cref="Aggregation.SessionTracker"/> at session-open. NOT in
/// the per-event hot path.
/// </remarks>
public interface IAppEnricher
{
    EnrichmentResult Enrich(ProcessImageInfo image);
}

/// <summary>
/// Default used by tests and code paths where no enrichment is configured. Returns
/// <see cref="EnrichmentResult.Unchecked"/> for everything.
/// </summary>
public sealed class NoOpAppEnricher : IAppEnricher
{
    public static NoOpAppEnricher Instance { get; } = new();

    private NoOpAppEnricher() { }

    public EnrichmentResult Enrich(ProcessImageInfo image) => EnrichmentResult.Unchecked;
}
