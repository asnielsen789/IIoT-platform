namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// One mock and one issuer it trusts, shared by the tests of a class. Tests that share it use
/// fresh message ids, so they never depend on what another test left in the store.
/// </summary>
public sealed class GatewayMockFixture : IDisposable
{
    public GatewayMockFixture()
    {
        Issuer = new TestTokenIssuer();
        Factory = new GatewayMockFactory(Issuer.GatewaySettings());
        Client = Factory.CreateClient();
    }

    public TestTokenIssuer Issuer { get; }

    public GatewayMockFactory Factory { get; }

    /// <summary>An HTTP client wired straight into the mock's test server.</summary>
    public HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        Issuer.Dispose();
    }
}
