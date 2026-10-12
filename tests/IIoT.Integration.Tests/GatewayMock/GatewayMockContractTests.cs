using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The receiving side of the downstream contract: the mock accepts every example the contract
/// calls valid and rejects every one it calls invalid, with a reason. The sending side — that
/// the forwarder only ever produces bodies the same schema accepts — is tested in IIoT.Core.Tests
/// against the same file.
/// </summary>
public class GatewayMockContractTests : IClassFixture<GatewayMockFixture>
{
    private readonly GatewayMockFixture _gateway;

    public GatewayMockContractTests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Guards the theories below. A theory whose data source silently returns nothing looks like
    /// a suite that passes, which is what happens when the fixtures stop being copied.
    /// </summary>
    [Fact]
    public void Fixtures_AreDiscovered()
    {
        Assert.True(ContractFixtures.ValidGateway().Count() >= 3, "valid gateway fixtures were not copied to the output directory");
        Assert.True(ContractFixtures.InvalidGateway().Count() >= 5, "invalid gateway fixtures were not copied to the output directory");
        Assert.True(ContractFixtures.ValidDevice().Count() >= 4, "valid device fixtures were not copied to the output directory");
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.ValidGateway), MemberType = typeof(ContractFixtures))]
    public async Task Post_ValidFixture_IsAccepted(string fileName)
    {
        var body = ContractFixtures.ReadGateway(fileName);
        var messageId = MessageIdOf(body);

        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), messageId);
        using var response = await _gateway.Client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"{fileName} should be accepted but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.InvalidGateway), MemberType = typeof(ContractFixtures))]
    public async Task Post_InvalidFixture_IsRejectedWithAReason(string fileName)
    {
        var body = ContractFixtures.ReadGateway(fileName);
        var messageId = MessageIdOf(body);

        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), messageId);
        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The reason has to name what was wrong, not fall back to a generic message — it is what
        // the platform logs when a delivery is refused.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail), $"{fileName} was rejected without a reason");
        Assert.DoesNotContain("does not match the readings v1 schema", problem.Detail!, StringComparison.Ordinal);

        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    [Fact]
    public async Task Post_UnknownContractVersion_NamesTheVersion()
    {
        var body = ContractFixtures.ReadGateway("invalid-unknown-contract-version.json");

        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), MessageIdOf(body));
        using var response = await _gateway.Client.SendAsync(request);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("/contractVersion", problem!.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_MalformedJson_IsBadRequest()
    {
        using var request = GatewayRequests.PostReadings("{ \"contractVersion\": 1, ", _gateway.Issuer.CreateToken(), "key");
        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_NotJson_IsUnsupportedMediaType()
    {
        var messageId = GatewayRequests.NewMessageId();
        var body = GatewayRequests.ValidBatch("tank-000123", messageId);

        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), messageId, "text/plain");
        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// The batch is stored exactly as it arrived. Unknown fields are ignored for validation, not
    /// stripped: a receiver that silently dropped them could not take part in a later revision.
    /// </summary>
    [Fact]
    public async Task Post_UnknownFields_AreAcceptedAndKept()
    {
        var body = ContractFixtures.ReadGateway("valid-unknown-fields.json");
        var batch = JsonNode.Parse(body)!.AsObject();
        var messageId = GatewayRequests.NewMessageId();
        batch["messageId"] = messageId;

        using var request = GatewayRequests.PostReadings(batch.ToJsonString(), _gateway.Issuer.CreateToken(), messageId);
        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_gateway.Factory.Store.Batches, b => b.IdempotencyKey == messageId);
        Assert.True(stored.Body.TryGetProperty("futureBatchField", out _));
    }

    private static string MessageIdOf(string body) =>
        JsonNode.Parse(body)?["messageId"]?.GetValue<string>() ?? GatewayRequests.NewMessageId();
}
