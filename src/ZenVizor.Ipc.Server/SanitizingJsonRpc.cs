using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace ZenVizor.Ipc.Server;

/// <summary>
/// <see cref="JsonRpc"/> subclass that scrubs RPC errors before they cross the
/// wire. StreamJsonRpc's default <c>CommonErrorData</c> strategy serializes
/// the underlying exception's type, message, and stack trace into
/// <c>error.data</c>; a SqliteException would expose the DB path, schema
/// names, and parameter values to any local pipe client. This subclass:
/// <list type="bullet">
///   <item><description>Returns a generic "internal server error" for unhandled exceptions, with no exception details on the wire.</description></item>
///   <item><description>Preserves the explicit error code and message of <see cref="LocalRpcException"/> (the path the handler/gate uses to surface typed faults).</description></item>
///   <item><description>Logs the full exception server-side via Serilog so operators retain diagnostics.</description></item>
/// </list>
/// </summary>
internal sealed class SanitizingJsonRpc : JsonRpc
{
    private const string GenericErrorMessage = "Internal server error.";

    private readonly ILogger _logger;

    public SanitizingJsonRpc(IJsonRpcMessageHandler messageHandler, ILogger? logger)
        : base(messageHandler)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        // Local typed faults (validation, negotiation gate) carry their own
        // code/message — surface those verbatim. We still log so the server
        // operator sees the rejection.
        if (exception is LocalRpcException local)
        {
            _logger.LogDebug(
                "RPC method {Method} rejected with code {Code}: {Message}",
                request.Method, local.ErrorCode, local.Message);

            return new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)local.ErrorCode,
                Message = local.Message ?? GenericErrorMessage,
                Data = local.ErrorData,
            };
        }

        // Everything else — including SqliteException, NullReferenceException,
        // anything from the storage repos — gets sanitized. Full exception
        // stays in the service log; the client sees only a generic fault.
        _logger.LogError(
            exception,
            "RPC method {Method} failed with an unhandled exception. " +
            "Returning generic fault to client.",
            request.Method);

        return new JsonRpcError.ErrorDetail
        {
            Code = JsonRpcErrorCode.InvocationError,
            Message = GenericErrorMessage,
            Data = null,
        };
    }
}
