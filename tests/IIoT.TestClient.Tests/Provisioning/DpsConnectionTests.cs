using System.Net;
using System.Security.Cryptography.X509Certificates;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.Provisioning;
using IIoT.TestClient.Tests.TestDoubles;
using MQTTnet;
using MQTTnet.Formatter;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// What the CONNECT to DPS would contain, without connecting anywhere. The values are DPS's, from
/// "Use MQTT to communicate with Azure IoT DPS".
/// </summary>
public sealed class DpsConnectionTests : IDisposable
{
    private const string DeviceId = "tank-000003";
    private const string IdScope = "0ne00000000";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestPki _pki = new(Now);
    private readonly X509CertificateAuthentication _authentication;

    public DpsConnectionTests()
    {
        var csr = DeviceCsr.Create(DeviceId);
        using var issued = _pki.Issue(csr);
        _authentication = X509CertificateAuthentication.FromPem(DeviceId, _pki.ChainPem(issued), csr.PrivateKeyPem, Now);
    }

    public void Dispose()
    {
        _authentication.Dispose();
        _pki.Dispose();
    }

    [Fact]
    public void UserName_NamesTheScopeTheRegistrationAndTheApiVersion()
    {
        Assert.Equal(
            "0ne00000000/registrations/tank-000003/api-version=2019-03-31",
            DpsEndpoint.UserName(IdScope, DeviceId));
    }

    [Fact]
    public void Build_TheRegistrationIdIsTheClientId()
    {
        Assert.Equal(DeviceId, Build().ClientId);
    }

    [Fact]
    public void Build_TargetsTheGlobalEndpointOn8883OverTls()
    {
        var channel = Assert.IsType<MqttClientTcpOptions>(Build().ChannelOptions);
        var endPoint = Assert.IsType<DnsEndPoint>(channel.RemoteEndpoint);

        Assert.Equal(DpsEndpoint.GlobalHostName, endPoint.Host);
        Assert.Equal(8883, endPoint.Port);
        Assert.True(channel.TlsOptions.UseTls);
        Assert.Equal(DpsEndpoint.GlobalHostName, channel.TlsOptions.TargetHost);
        Assert.False(channel.TlsOptions.AllowUntrustedCertificates);
        Assert.False(channel.TlsOptions.IgnoreCertificateChainErrors);
    }

    [Fact]
    public void Build_PresentsTheDeviceCertificate()
    {
        var channel = Assert.IsType<MqttClientTcpOptions>(Build().ChannelOptions);

        var certificate = Assert.Single(channel.TlsOptions.ClientCertificatesProvider.GetCertificates());
        Assert.Equal(_authentication.Certificate.Thumbprint, Assert.IsType<X509Certificate2>(certificate).Thumbprint);
    }

    [Fact]
    public void Build_SpeaksMqtt311WithACleanSession()
    {
        var options = Build();

        Assert.Equal(MqttProtocolVersion.V311, options.ProtocolVersion);
        Assert.True(options.CleanSession);
    }

    [Fact]
    public void Build_SendsTheDpsUserNameAndNoPassword()
    {
        // Not even an empty one: with X.509, DPS documents no password at all.
        var options = Build();

        Assert.Equal(DpsEndpoint.UserName(IdScope, DeviceId), options.Credentials!.GetUserName(options));
        Assert.Null(options.Credentials.GetPassword(options));
    }

    private MqttClientOptions Build() => DpsConnection.Build(DpsEndpoint.GlobalHostName, IdScope, _authentication);
}
