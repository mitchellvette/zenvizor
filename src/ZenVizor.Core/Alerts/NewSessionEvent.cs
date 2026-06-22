// SPDX-License-Identifier: GPL-3.0-or-later

using ZenVizor.Core.Storage;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Aggregator → producer payload for one qualifying WAN connection observed
/// during a flush tick. Built by <c>TrafficAggregator.Flush</c> after the
/// sink commits, carries everything the producer needs to evaluate rules
/// without re-touching the DB:
/// <list type="bullet">
///   <item><description><see cref="AppId"/> resolves the entity reference for
///   App-scoped alerts (the producer also writes it to
///   <see cref="ZenVizor.Ipc.Contracts.Dto.AlertDto.AppId"/>).</description></item>
///   <item><description><see cref="App"/> snapshot is the
///   <see cref="AppIdentity"/> captured at session-open time — the producer
///   reads <see cref="AppIdentity.SignatureStatus"/> and
///   <see cref="AppIdentity.IsUserWritablePath"/> from this in-memory copy
///   instead of hitting the SignerCache per event (perf-budget lock).</description></item>
///   <item><description><see cref="WanConnection"/> is the specific
///   connection that fired this event — the rule reads its
///   <c>FirstSeenUnixMs</c> for the catalog template's "First connection: …"
///   phrase on initial raise.</description></item>
/// </list>
/// One event per qualifying connection per flush tick: an app generating 5
/// WAN connections in one flush window fires 5 events; the producer's dedupe
/// SQL gate keeps only the first as a new alert and folds the rest into the
/// connection-count via <c>UpdateDetail</c>.
/// </summary>
public sealed record NewSessionEvent(
    int AppId,
    int SessionId,
    AppIdentity App,
    PendingConnection WanConnection,
    long FlushTimeUnixMs);
