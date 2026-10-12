using System.Security.Claims;
using System.Text.Json;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// <c>POST /v1/readings</c> — the one endpoint of the downstream contract. See
/// <c>docs/gateway-contract.md</c>.
/// </summary>
internal static class ReadingsEndpoint
{
    public const string Route = "/v1/readings";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static RouteHandlerBuilder MapReadings(this IEndpointRouteBuilder endpoints) =>
        // The fallback policy already requires an authenticated caller for every endpoint; this
        // says so again at the endpoint, where a reader looks first.
        endpoints.MapPost(Route, AcceptAsync).RequireAuthorization();

    /// <remarks>
    /// The order of the checks follows what each one needs. Authentication has already happened
    /// by the time this runs. The cheap header checks come first, then the body is parsed and
    /// validated against the schema, and only a valid body is compared with its header.
    /// </remarks>
    private static async Task<IResult> AcceptAsync(
        HttpRequest request,
        ClaimsPrincipal caller,
        ReadingStore store,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(ReadingsEndpoint).FullName!);

        if (!request.HasJsonContentType())
        {
            return Reject(logger, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
        }

        var keys = request.Headers[IdempotencyKeyHeader];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]))
        {
            return Reject(logger, StatusCodes.Status400BadRequest, $"Exactly one non-empty {IdempotencyKeyHeader} header is required");
        }

        var idempotencyKey = keys[0]!;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return Reject(logger, StatusCodes.Status400BadRequest, $"Body is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var body = document.RootElement;

            var schemaError = ReadingsSchema.Validate(body);
            if (schemaError is not null)
            {
                return Reject(logger, StatusCodes.Status400BadRequest, schemaError);
            }

            // Safe to read directly: the schema has just guaranteed both are non-empty strings.
            var messageId = body.GetProperty("messageId").GetString()!;
            var deviceId = body.GetProperty("deviceId").GetString()!;

            if (!string.Equals(messageId, idempotencyKey, StringComparison.Ordinal))
            {
                return Reject(logger, StatusCodes.Status400BadRequest, $"{IdempotencyKeyHeader} must equal the body's messageId");
            }

            var readingCount = body.GetProperty("readings").GetArrayLength();
            var subject = caller.FindFirst("sub")?.Value;

            if (store.TryAdd(deviceId, idempotencyKey, body, subject, timeProvider.GetUtcNow()))
            {
                logger.LogInformation(
                    "Accepted message {MessageId} from {DeviceId} with {ReadingCount} reading(s), delivered by {Caller}",
                    messageId, deviceId, readingCount, subject);
            }
            else
            {
                // At-least-once delivery makes this normal, not an error: the platform's event
                // stream redelivers after a restart, and the platform retries a request whose
                // response it never saw. The first delivery wins; a replay is acknowledged with
                // the same status so the sender stops, and nothing is stored twice.
                logger.LogInformation(
                    "Replay of message {MessageId} from {DeviceId} acknowledged; already stored, nothing added",
                    messageId, deviceId);
            }

            return Results.Accepted();
        }
    }

    private static IResult Reject(ILogger logger, int statusCode, string reason)
    {
        logger.LogWarning("Rejected request with {StatusCode}: {Reason}", statusCode, reason);
        return Results.Problem(statusCode: statusCode, title: "Request rejected", detail: reason);
    }
}
