using System.Net;
using System.Text;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.IotHub;
using MQTTnet;
using MQTTnet.Formatter;

namespace IIoT.TestClient.Tests.IotHub;

/// <summary>
/// Asserts what the CONNECT packet would contain. No connection is attempted anywhere in this
/// project: there is no IoT Hub provisioned yet, and a test that reached for the network would
/// fail in CI for reasons that have nothing to do with the code.
/// </summary>
public class DeviceConnectionTests
{
    private const string HostName = "example-hub.azure-devices.net";
    private const string DeviceId = "tank-000123";
    private const string PlaceholderKey = "UGxhY2Vob2xkZXJLZXlOb3RBUmVhbFNlY3JldDEyMzQ1Njc4OTA=";

    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(1767225600);

    [Fact]
    public void UserName_CarriesTheApiVersionIotHubExpects()
    {
        // Omitting api-version gives undefined behaviour and a wrong value fails the connect with
        // no useful reason, so the string is pinned rather than composed at the call site.
        Assert.Equal(
            "example-hub.azure-devices.net/tank-000123/?api-version=2021-04-12",
            DeviceConnection.UserName(HostName, DeviceId));
    }

    [Fact]
    public void Build_UsesTheDeviceIdAsClientId()
    {
        Assert.Equal(DeviceId, Build().ClientId);
    }

    [Fact]
    public void Build_TargetsPort8883OverTls()
    {
        var channel = Assert.IsType<MqttClientTcpOptions>(Build().ChannelOptions);
        var endPoint = Assert.IsType<DnsEndPoint>(channel.RemoteEndpoint);

        Assert.Equal(HostName, endPoint.Host);
        Assert.Equal(8883, endPoint.Port);

        // IoT Hub refuses plaintext MQTT entirely, and the contract forbids unencrypted transport.
        Assert.True(channel.TlsOptions.UseTls);
        Assert.Equal(HostName, channel.TlsOptions.TargetHost);
        Assert.False(channel.TlsOptions.AllowUntrustedCertificates);
        Assert.False(channel.TlsOptions.IgnoreCertificateChainErrors);
    }

    [Fact]
    public void Build_SpeaksMqtt311()
    {
        // The hub's device endpoint supports no later version.
        Assert.Equal(MqttProtocolVersion.V311, Build().ProtocolVersion);
    }

    [Fact]
    public void Build_PutsTheSasTokenInThePasswordField()
    {
        var options = Build();

        Assert.NotNull(options.Credentials);
        Assert.Equal(
            DeviceConnection.UserName(HostName, DeviceId),
            options.Credentials!.GetUserName(options));

        var password = Encoding.UTF8.GetString(options.Credentials.GetPassword(options));
        Assert.StartsWith(SasToken.Prefix, password, StringComparison.Ordinal);
        Assert.Equal(
            SasToken.Create(SasToken.DeviceResourceUri(HostName, DeviceId), PlaceholderKey, Expiry),
            password);
    }

    [Fact]
    public void Build_LetsTheMechanismContributeToTls()
    {
        // The hook X.509 client certificates will use (issue #11). Proving it is called now is
        // what keeps that from becoming a restructuring job later.
        var authentication = new RecordingAuthentication();

        DeviceConnection.Build(HostName, authentication, Expiry);

        Assert.True(authentication.TlsConfigured);
    }

    [Fact]
    public void Build_WithoutAPassword_SendsAnEmptyOne()
    {
        // A certificate-authenticated device supplies no password; IoT Hub ignores the field.
        var options = DeviceConnection.Build(HostName, new RecordingAuthentication(), Expiry);

        Assert.Empty(options.Credentials!.GetPassword(options));
    }

    private static MqttClientOptions Build() => DeviceConnection.Build(
        HostName, new SharedAccessKeyAuthentication(DeviceId, PlaceholderKey), Expiry);

    private sealed class RecordingAuthentication : IDeviceAuthentication
    {
        public string DeviceId => "tank-000123";

        public bool TlsConfigured { get; private set; }

        public string? CreatePassword(string hostName, DateTimeOffset expiresAt) => null;

        public void ConfigureTls(MqttClientTlsOptionsBuilder tls) => TlsConfigured = true;
    }
}
