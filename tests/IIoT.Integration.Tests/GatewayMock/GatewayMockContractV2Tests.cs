using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The receiving side of gateway contract v2 (ADR-011): <c>/v2/readings</c> accepts every v2
/// example the contract calls valid and rejects every one it calls invalid, with a reason; and
/// each version refuses the other's bodies.
/// </summary>
/// <remarks>
/// Each example is posted under a fresh message ID, so the cases are independent of each other and
/// of their order: several examples share an ID, as the contract's examples of one message do.
/// </remarks>
public class GatewayMockContractV2Tests : IClassFixture<GatewayMockFixture>
{
    private readonly GatewayMockFixture _gateway;

    public GatewayMockContractV2Tests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    [Fact]
    public void Fixtures_AreDiscovered()
    {
        Assert.True(ContractFixtures.ValidGatewayV2().Count() >= 5, "valid v2 fixtures were not copied to the output directory");
        Assert.True(ContractFixtures.InvalidGatewayV2().Count() >= 5, "invalid v2 fixtures were not copied to the output directory");
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.ValidGatewayV2), MemberType = typeof(ContractFixtures))]
    public async Task PostV2_ValidFixture_IsAccepted(string fileName)
    {
        var (body, messageId) = WithFreshMessageId(ContractFixtures.ReadGatewayV2(fileName));

        using var response = await PostAsync(body, messageId, GatewayRequests.RouteV2);

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"{fileName} should be accepted but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.InvalidGatewayV2), MemberType = typeof(ContractFixtures))]
    public async Task PostV2_InvalidFixture_IsRejectedWithAReason(string fileName)
    {
        var (body, messageId) = WithFreshMessageId(ContractFixtures.ReadGatewayV2(fileName));

        using var response = await PostAsync(body, messageId, GatewayRequests.RouteV2);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.False(string.IsNullOrWhiteSpace(problem?.Detail), $"{fileName} was rejected without a reason");
        Assert.DoesNotContain("does not match the readings v2 schema", problem!.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// The per-kind rules ask every reading whether it is each kind. Only the rule that applies may
    /// speak: an energy reading in kW is wrong about its unit, and nothing else.
    /// </summary>
    [Fact]
    public async Task PostV2_WrongUnitForTheKind_NamesOnlyTheUnit()
    {
        var (body, messageId) = WithFreshMessageId(ContractFixtures.ReadGatewayV2("invalid-unit-for-kind.json"));

        using var response = await PostAsync(body, messageId, GatewayRequests.RouteV2);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("/readings/0/unit:", problem!.Detail, StringComparison.Ordinal);
        Assert.Contains("kW.h", problem.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(";", problem.Detail!, StringComparison.Ordinal);
    }

    /// <summary>Each version takes only its own bodies: <c>contractVersion</c> must match the path.</summary>
    [Theory]
    [InlineData("v1-on-v2")]
    [InlineData("v2-on-v1")]
    public async Task Post_TheOtherVersionsBody_IsRejectedOnItsVersion(string direction)
    {
        var (body, route) = direction == "v1-on-v2"
            ? (ContractFixtures.ReadGateway("valid-single-reading.json"), GatewayRequests.RouteV2)
            : (ContractFixtures.ReadGatewayV2("valid-tank-level.json"), GatewayRequests.Route);
        var (fresh, messageId) = WithFreshMessageId(body);

        using var response = await PostAsync(fresh, messageId, route);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("/contractVersion", problem!.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// One message is one message, whichever version delivers it: a level delivered on v1 and
    /// replayed on v2 is acknowledged both times and stored once.
    /// </summary>
    [Fact]
    public async Task OneMessage_OnBothVersions_IsStoredOnce()
    {
        var messageId = GatewayRequests.NewMessageId();
        var v1 = JsonNode.Parse(GatewayRequests.ValidBatch("tank-both-versions", messageId))!.AsObject();
        var v2 = v1.DeepClone().AsObject();
        v2["contractVersion"] = 2;

        using (var first = await PostAsync(v1.ToJsonString(), messageId, GatewayRequests.Route))
        using (var replay = await PostAsync(v2.ToJsonString(), messageId, GatewayRequests.RouteV2))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        }

        var stored = Assert.Single(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
        Assert.Equal(1, stored.Body.GetProperty("contractVersion").GetInt32());
    }

    private async Task<HttpResponseMessage> PostAsync(string body, string messageId, string route)
    {
        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), messageId, route: route);
        return await _gateway.Client.SendAsync(request);
    }

    private static (string Body, string MessageId) WithFreshMessageId(string body)
    {
        var batch = JsonNode.Parse(body)!.AsObject();
        var messageId = GatewayRequests.NewMessageId();
        batch["messageId"] = messageId;
        return (batch.ToJsonString(), messageId);
    }
}
