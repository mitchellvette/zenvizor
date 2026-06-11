using FluentAssertions;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Phase 4 query RPCs validate every argument at the boundary and surface
/// rejections as typed <see cref="IpcErrorCode.InvalidArgument"/> faults
/// instead of letting the providers throw (which would otherwise be flattened
/// into a generic "internal server error" by the sanitizer — leaving the
/// client unable to act on the failure). Tests host the production
/// <see cref="Service.ZenVizorIpcHandler"/> in-process so behavior cannot
/// drift from what real callers see over the named pipe.
/// </summary>
public sealed class InputValidationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly long FixedNowMs = FixedNow.ToUnixTimeMilliseconds();
    private const long OneDayMs = 86_400_000L;

    private static IZenVizorIpc CreateHandler(
        Func<QueryWindow, AppListResult>? appList = null,
        Func<int, QueryWindow, TrafficGrain, AppDetailResult>? appDetail = null,
        Func<int, QueryWindow, ConnectionListResult>? connections = null,
        Func<QueryWindow, TrafficGrain, TrafficHistoryResult>? history = null)
    {
        // Fixed clock + a tight 30-day lookback bound. Real production uses
        // ~400d; the tighter bound in test keeps the retention-horizon case
        // expressible without enormous magic numbers.
        return ProductionHandlerFactory.CreateDefault(
            clock: () => FixedNow,
            maxWindowLookbackMs: 30 * OneDayMs,
            appListProvider: appList,
            appDetailProvider: appDetail,
            connectionsProvider: connections,
            historyProvider: history);
    }

    private static async Task<GatedRpcSession> NegotiatedSessionAsync(IZenVizorIpc handler)
    {
        var session = GatedRpcSession.Create(handler);
        var negotiate = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);
        negotiate.Accepted.Should().BeTrue();
        return session;
    }

    [Fact]
    public async Task GetAppList_WindowToBeforeFrom_ReturnsInvalidArgument()
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());

        var bad = new QueryWindow(FromUnixMs: FixedNowMs, ToUnixMs: FixedNowMs - OneDayMs);

        var act = async () => await session.Proxy.GetAppListAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task GetAppList_WindowBeyondRetentionHorizon_ReturnsInvalidArgument()
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());

        // 400 days back exceeds the 30-day lookback set in CreateHandler.
        var bad = new QueryWindow(
            FromUnixMs: FixedNowMs - 400 * OneDayMs,
            ToUnixMs: FixedNowMs);

        var act = async () => await session.Proxy.GetAppListAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task GetAppList_ValidWindow_PassesThroughToProvider()
    {
        var providerCalls = 0;
        var handler = CreateHandler(
            appList: window =>
            {
                providerCalls++;
                return new AppListResult(window, Array.Empty<AppListEntry>());
            });

        await using var session = await NegotiatedSessionAsync(handler);

        var good = new QueryWindow(FromUnixMs: FixedNowMs - OneDayMs, ToUnixMs: FixedNowMs);
        var envelope = await session.Proxy.GetAppListAsync(good);

        providerCalls.Should().Be(1);
        envelope.Payload.Window.Should().Be(good);
    }

    [Fact]
    public async Task GetTrafficHistory_UndefinedGrain_ReturnsInvalidArgument()
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());

        var window = new QueryWindow(FixedNowMs - OneDayMs, FixedNowMs);
        var bogusGrain = (TrafficGrain)99;

        var act = async () => await session.Proxy.GetTrafficHistoryAsync(window, bogusGrain);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task GetAppDetail_UndefinedGrain_ReturnsInvalidArgument()
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());

        var window = new QueryWindow(FixedNowMs - OneDayMs, FixedNowMs);
        var bogusGrain = (TrafficGrain)42;

        var act = async () => await session.Proxy.GetAppDetailAsync(1, window, bogusGrain);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GetAppDetail_NonPositiveAppId_ReturnsInvalidArgument(int badAppId)
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());
        var window = new QueryWindow(FixedNowMs - OneDayMs, FixedNowMs);

        var act = async () => await session.Proxy.GetAppDetailAsync(badAppId, window, TrafficGrain.Auto);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task GetConnections_NonPositiveAppId_ReturnsInvalidArgument(int badAppId)
    {
        await using var session = await NegotiatedSessionAsync(CreateHandler());
        var window = new QueryWindow(FixedNowMs - OneDayMs, FixedNowMs);

        var act = async () => await session.Proxy.GetConnectionsAsync(badAppId, window);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task ValidationFailure_ProviderIsNotInvoked()
    {
        var providerCalls = 0;
        var handler = CreateHandler(
            appDetail: (_, _, _) =>
            {
                providerCalls++;
                return new AppDetailResult(
                    new QueryWindow(0, 0), TrafficGrain.Samples,
                    new AppListEntry(0, "", "", null, "Unchecked", false, 0, 0, 0, 0),
                    Array.Empty<TrafficPoint>(), Array.Empty<SessionInfo>());
            });

        await using var session = await NegotiatedSessionAsync(handler);
        var window = new QueryWindow(FixedNowMs - OneDayMs, FixedNowMs);

        var act = async () => await session.Proxy.GetAppDetailAsync(0, window, TrafficGrain.Auto);
        await act.Should().ThrowAsync<RemoteInvocationException>();

        providerCalls.Should().Be(0);
    }
}
