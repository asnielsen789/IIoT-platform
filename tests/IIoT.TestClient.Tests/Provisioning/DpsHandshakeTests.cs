using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.Provisioning;
using IIoT.TestClient.Tests.TestDoubles;
using MQTTnet;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// The whole registration over a real TLS connection on loopback: the test client's own DPS
/// connection options, MQTTnet's MQTT and TLS layers, the device certificate the device CA's code
/// issued, and a server that answers the way DPS does. The only network is 127.0.0.1.
/// </summary>
/// <remarks>
/// What this cannot prove is DPS's side: that DPS accepts the certificate through the enrollment
/// group, assigns the device and registers it in the linked hub. That is the live test of
/// https://github.com/asnielsen789/IIoT-platform/issues/48.
/// </remarks>
public sealed class DpsHandshakeTests : IDisposable
{
    private const string DeviceId = "tank-000003";
    private const string IdScope = "0ne00000000";
    private const string OperationId = "4.d0a671905ea5b2c8.42d78160-4c78-479e-8be7-61d5e55dac0d";
    private const string Hub = "iot-example.azure-devices.net";

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

    [Fact]
    public async Task Provision_OverTls_RegistersPollsAndIsAssigned()
    {
        // The operator's path: create-csr, issue --no-register, then provision with the chain file.
        var keys = DeviceKeyFiles.Create(DeviceId, _directory);
        var csr = new DeviceCsr(DeviceId, File.ReadAllText(keys.KeyPath), File.ReadAllText(keys.CsrPath));
        using var issued = _pki.Issue(csr);
        var chainFile = Path.Combine(_directory, $"{DeviceId}.chain.pem");
        File.WriteAllText(chainFile, _pki.ChainPem(issued));

        using var broker = new LoopbackTlsBroker(_server, SslProtocols.Tls12 | SslProtocols.Tls13, _pki.Verifies)
        {
            Respond = AnswerLikeDps
        };
        using var authentication = X509CertificateAuthentication.LoadByCommonName(chainFile, keys.KeyPath, DateTimeOffset.UtcNow);

        var outcome = await ProvisionAsync(broker, authentication);

        Assert.True(broker.Failure is null, $"Server side failed: {broker.Failure}");
        Assert.Equal(new DpsAssigned(Hub, DeviceId, "initialAssignment"), outcome);

        // DPS authenticates by the certificate alone: the device's, accepted by its CA.
        Assert.Equal(issued.Thumbprint, broker.ClientCertificate?.Thumbprint);
        Assert.True(broker.ClientAccepted);

        // The CONNECT DPS documents for X.509: registration ID as client ID, the scope in the
        // username, and no password field at all.
        Assert.NotNull(broker.Connect);
        Assert.Equal(DeviceId, broker.Connect!.ClientId);
        Assert.Equal(DpsEndpoint.UserName(IdScope, DeviceId), broker.Connect.UserName);
        Assert.Null(broker.Connect.Password);

        Assert.Equal([DpsTopics.ResponseFilter], broker.Subscriptions);
        Assert.Equal(
            [DpsTopics.Register("1"), DpsTopics.OperationStatus("2", OperationId)],
            broker.Published.Select(message => message.Topic));
        Assert.True(broker.DisconnectReceived);
    }

    private static IEnumerable<(string Topic, byte[] Payload)> AnswerLikeDps(string topic, byte[] payload)
    {
        if (topic == DpsTopics.Register("1"))
        {
            using var request = JsonDocument.Parse(payload);
            Assert.Equal(DeviceId, request.RootElement.GetProperty("registrationId").GetString());

            yield return Answer("$dps/registrations/res/202/?$rid=1&retry-after=1",
                $$"""{"operationId":"{{OperationId}}","status":"assigning"}""");
        }
        else if (topic == DpsTopics.OperationStatus("2", OperationId))
        {
            yield return Answer("$dps/registrations/res/200/?$rid=2", $$$"""
                {"operationId":"{{{OperationId}}}","status":"assigned",
                 "registrationState":{"x509":{},"registrationId":"{{{DeviceId}}}","assignedHub":"{{{Hub}}}",
                  "deviceId":"{{{DeviceId}}}","status":"assigned","substatus":"initialAssignment"}}
                """);
        }
    }

    private static (string, byte[]) Answer(string topic, string body) => (topic, Encoding.UTF8.GetBytes(body));

    private async Task<DpsOutcome> ProvisionAsync(LoopbackTlsBroker broker, X509CertificateAuthentication authentication)
    {
        using var cancellation = new CancellationTokenSource(Timeout);

        // The client's real DPS options for a host called localhost, pointed at the loopback port
        // and trusting the test server's certificate instead of the public roots.
        var options = DpsConnection.Build("localhost", IdScope, authentication);
        options.Timeout = Timeout;
        var tcp = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        tcp.RemoteEndpoint = new IPEndPoint(IPAddress.Loopback, broker.Port);
        tcp.TlsOptions.TrustChain = [X509CertificateLoader.LoadCertificate(_server.RawData)];

        var serving = broker.ServeOneAsync(cancellation.Token);
        using var client = new MqttClientFactory().CreateMqttClient();
        var channel = new MqttDpsChannel(client);
        try
        {
            await client.ConnectAsync(options, cancellation.Token);
            var outcome = await new DpsProvisioningClient(channel, (_, _) => Task.CompletedTask, TextWriter.Null)
                .ProvisionAsync(authentication.DeviceId, 10, cancellation.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellation.Token);
            return outcome;
        }
        finally
        {
            await serving.WaitAsync(cancellation.Token);
        }
    }
}
