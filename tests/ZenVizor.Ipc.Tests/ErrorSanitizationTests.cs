using FluentAssertions;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// StreamJsonRpc's default <c>CommonErrorData</c> strategy serializes the
/// underlying exception's type, message, and stack trace into the wire-level
/// <c>error.data</c>. For us, that means a <c>SqliteException</c> originating
/// in the query repo could leak the DB path, table/column names, and parameter
/// values to any local pipe client. These tests verify the
/// <see cref="Server.SanitizingJsonRpc"/> override returns a generic fault
/// instead — and that explicit typed faults (<see cref="LocalRpcException"/>)
/// still flow through with their <c>ErrorCode</c> intact.
/// </summary>
public sealed class ErrorSanitizationTests
{
    /// <summary>
    /// Distinctive exception subclass so the test can assert that even the
    /// type name doesn't leak. Mirrors the shape of <c>SqliteException</c>
    /// (the canonical real-world offender) without taking the dependency.
    /// </summary>
    private sealed class SensitiveSqlException : Exception
    {
        public SensitiveSqlException(string message) : base(message) { }
    }

    private const string SensitivePath = @"C:\ProgramData\ZenVizor\zenvizor.db";
    private const string SensitiveTable = "session_tokens";
    private const string SensitiveColumn = "password_hash";

    private static readonly string ExceptionMessage =
        $"SQLITE_ERROR: no such column: {SensitiveColumn} at {SensitivePath} " +
        $"(near table {SensitiveTable})";

    [Fact]
    public async Task UnhandledException_IsScrubbedToGenericFault()
    {
        // Wire a provider that throws as if the storage layer had blown up.
        var handler = ProductionHandlerFactory.CreateDefault(
            appListProvider: _ => throw new SensitiveSqlException(ExceptionMessage));

        await using var session = GatedRpcSession.Create(handler);
        var negotiate = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);
        negotiate.Accepted.Should().BeTrue();

        var act = async () => await session.Proxy.GetAppListAsync(new QueryWindow(0, 1_000));
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        var thrown = ex.Which;

        // Wire-level error must not carry the exception type, message, or any
        // of the sensitive substrings.
        var wireFootprint = string.Join("\n",
            thrown.Message ?? "",
            thrown.ErrorData?.ToString() ?? "",
            thrown.DeserializedErrorData?.ToString() ?? "");

        wireFootprint.Should().NotContain(nameof(SensitiveSqlException));
        wireFootprint.Should().NotContain(SensitivePath);
        wireFootprint.Should().NotContain(SensitiveTable);
        wireFootprint.Should().NotContain(SensitiveColumn);
        wireFootprint.Should().NotContain("SQLITE_ERROR");
    }

    [Fact]
    public async Task TypedLocalRpcException_IsPreservedThroughSanitizer()
    {
        // Sanity check: the sanitizer must not flatten typed faults. The
        // validation path (and the negotiation gate) rely on LocalRpcException
        // carrying its ErrorCode all the way to the client; if the sanitizer
        // collapsed it to a generic fault, clients couldn't distinguish
        // "you passed bad input" from "the server fell over."
        var handler = ProductionHandlerFactory.CreateDefault();

        await using var session = GatedRpcSession.Create(handler);
        await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        // appId <= 0 trips the validation gate which throws LocalRpcException.
        var act = async () => await session.Proxy.GetAppDetailAsync(
            appId: 0,
            window: new QueryWindow(0, 1_000),
            grain: TrafficGrain.Auto);

        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }
}
