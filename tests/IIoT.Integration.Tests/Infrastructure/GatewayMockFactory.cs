using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IIoT.GatewayMock.Readings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// Hosts the gateway mock in-process, on a test server that never opens a socket.
/// </summary>
/// <remarks>
/// The entry point is named by <see cref="ReadingStore"/> rather than <c>Program</c>: any type in
/// the mock's assembly identifies it, and this project also references IIoT.Functions, whose
/// top-level <c>Program</c> would otherwise share the name.
/// </remarks>
public sealed class GatewayMockFactory : WebApplicationFactory<ReadingStore>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public GatewayMockFactory(IReadOnlyDictionary<string, string?> settings)
    {
        _settings = settings;
    }

    /// <summary>What the mock has accepted so far.</summary>
    public ReadingStore Store => Services.GetRequiredService<ReadingStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
}

/// <summary>Builds requests to the mock by hand, for the cases the forwarder never produces.</summary>
public static class GatewayRequests
{
    /// <summary>
    /// Spelled out here rather than borrowed from the mock: the path and header are part of the
    /// published contract, and a test that reused the implementation's constants would follow a
    /// change to them instead of catching it.
    /// </summary>
    public const string Route = "/v1/readings";

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static HttpRequestMessage PostReadings(
        string body,
        string? bearerToken,
        string? idempotencyKey,
        string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };

        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        return request;
    }

    /// <summary>
    /// A valid batch from the contract's single-reading example, with its identity replaced so
    /// that tests sharing one mock never collide in its store.
    /// </summary>
    public static string ValidBatch(string deviceId, string messageId, string? receivedAt = null)
    {
        var batch = JsonNode.Parse(ContractFixtures.ReadGateway("valid-single-reading.json"))!.AsObject();
        batch["deviceId"] = deviceId;
        batch["messageId"] = messageId;

        if (receivedAt is not null)
        {
            batch["receivedAt"] = receivedAt;
        }

        return batch.ToJsonString();
    }

    public static string NewMessageId() => Guid.NewGuid().ToString("N");
}
