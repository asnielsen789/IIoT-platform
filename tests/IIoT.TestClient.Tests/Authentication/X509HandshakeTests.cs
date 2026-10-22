using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.IotHub;
using IIoT.TestClient.Tests.TestDoubles;
using MQTTnet;

namespace IIoT.TestClient.Tests.Authentication;

/// <summary>
/// A real TLS handshake with a client certificate, on loopback: the test client's own connection
/// options, MQTTnet's own TLS layer, and a certificate the device CA's own code issued for a key
/// the test client generated. The only network is 127.0.0.1.
/// </summary>
/// <remarks>
/// <para>
/// What this proves offline is everything on the device's side of the handshake: the key loads in
/// a form the platform's TLS stack accepts (on Windows, SChannel refuses the ephemeral key a PEM
/// load gives; see <see cref="X509CertificateAuthentication"/>), MQTTnet presents the device
/// certificate when the server asks for one, the certificate verifies against the CA, the chain on
/// the wire is the device certificate alone, and the CONNECT that follows carries no password.
/// </para>
/// <para>
/// What it cannot prove is IoT Hub's side: that the hub accepts a device certificate sent without
/// its issuing CA when that CA is registered and verified, and that it accepts the zero-length
/// password. Those belong to the live test of
/// https://github.com/asnielsen789/IIoT-platform/issues/11.
/// </para>
/// </remarks>
public sealed class X509HandshakeTests : IDisposable
{
    private const string DeviceId = "tank-000001";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TestPki _pki = new(DateTimeOffset.UtcNow);
    private readonly X509Certificate2 _server = TestPki.CreateServerCertificate();
    private readonly string _directory = Directory.CreateTempSubdirectory("iiot-testclient-").FullName;

    public void Dispose()
    {
        _pki.Dispose();
        _server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task Connect_PresentsTheDeviceCertificateAloneAndIsAccepted(SslProtocols protocol)
    {
        // The operator's path, file by file: create-csr, then issue, then connect with the chain file.
        var keys = DeviceKeyFiles.Create(DeviceId, _directory);
        var csr = new DeviceCsr(DeviceId, File.ReadAllText(keys.KeyPath), File.ReadAllText(keys.CsrPath));
        using var issued = _pki.Issue(csr);
        var chainFile = Path.Combine(_directory, $"{DeviceId}.chain.pem");
        File.WriteAllText(chainFile, _pki.ChainPem(issued));

        using var broker = new LoopbackTlsBroker(_server, protocol, _pki.Verifies);
        using var authentication = X509CertificateAuthentication.Load(DeviceId, chainFile, keys.KeyPath, DateTimeOffset.UtcNow);

        var result = await ConnectAsync(broker, authentication);

        Assert.True(broker.Failure is null, $"Server side failed: {broker.Failure}");
        Assert.Equal(MqttClientConnectResultCode.Success, result);
        Assert.Equal(protocol, broker.NegotiatedProtocol);

        // The certificate the CA issued, accepted by a relying party that holds the issuing CA.
        Assert.NotNull(broker.ClientCertificate);
        Assert.Equal(issued.Thumbprint, broker.ClientCertificate!.Thumbprint);
        Assert.True(broker.ClientAccepted);

        // Leaf only, by design: MQTTnet cannot pass a chain, and IoT Hub holds the issuing CA.
        Assert.Empty(broker.ClientChain);

        // The CONNECT a certificate device sends: the same client id and username as with a key,
        // and a zero-length password.
        Assert.NotNull(broker.Connect);
        Assert.Equal(DeviceId, broker.Connect!.ClientId);
        Assert.Equal(DeviceConnection.UserName("localhost", DeviceId), broker.Connect.UserName);
        Assert.NotNull(broker.Connect.Password);
        Assert.Empty(broker.Connect.Password!);
        Assert.True(broker.DisconnectReceived);
    }

    [Fact]
    public async Task Connect_WithACertificateFromAnotherCa_IsRefused()
    {
        // The negative control: the server's check is what accepts the device above, not something
        // that accepts anything.
        using var otherCa = new TestPki(DateTimeOffset.UtcNow);
        var csr = DeviceCsr.Create(DeviceId);
        using var foreign = otherCa.Issue(csr);

        using var broker = new LoopbackTlsBroker(_server, SslProtocols.Tls12 | SslProtocols.Tls13, _pki.Verifies);
        using var authentication = X509CertificateAuthentication.FromPem(
            DeviceId, otherCa.ChainPem(foreign), csr.PrivateKeyPem, DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(broker, authentication));

        Assert.Equal(foreign.Thumbprint, broker.ClientCertificate?.Thumbprint);
        Assert.False(broker.ClientAccepted);
        Assert.Null(broker.Connect);
    }

    private async Task<MqttClientConnectResultCode> ConnectAsync(LoopbackTlsBroker broker, X509CertificateAuthentication authentication)
    {
        using var cancellation = new CancellationTokenSource(Timeout);

        // The client's real options for a hub called localhost, pointed at the loopback port and
        // trusting the test server's certificate instead of the public roots. Everything else,
        // the client certificate included, is what DeviceConnection.Build produced.
        var options = DeviceConnection.Build("localhost", authentication, DateTimeOffset.UtcNow.AddHours(1));
        options.Timeout = Timeout;
        var tcp = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        tcp.RemoteEndpoint = new IPEndPoint(IPAddress.Loopback, broker.Port);
        tcp.TlsOptions.TrustChain = [X509CertificateLoader.LoadCertificate(_server.RawData)];

        var serving = broker.ServeOneAsync(cancellation.Token);
        using var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            var result = await client.ConnectAsync(options, cancellation.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellation.Token);
            return result.ResultCode;
        }
        finally
        {
            await serving.WaitAsync(cancellation.Token);
        }
    }
}
