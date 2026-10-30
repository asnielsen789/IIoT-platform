using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace IIoT.TestClient.Tests.TestDoubles;

/// <summary>
/// Just enough of IoT Hub's or DPS's device endpoint to test certificate authentication on
/// loopback: a TLS server on 127.0.0.1 that requires a client certificate, decides whether to
/// accept it, answers one MQTT 3.1.1 CONNECT with a CONNACK, and then serves the session until
/// the client disconnects.
/// </summary>
/// <remarks>
/// It records what crossed the wire, which is the point: the certificate the client presented,
/// any further certificates it sent with it, the CONNECT packet, and every SUBSCRIBE and PUBLISH
/// that followed. A test that sets <see cref="Respond"/> plays the service: each publish it
/// receives can be answered with messages of its own. It is not a broker and does not pretend to
/// be IoT Hub's certificate validation; the acceptance decision is the test's.
/// </remarks>
public sealed class LoopbackTlsBroker : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _serverCertificate;
    private readonly SslProtocols _protocols;
    private readonly Func<X509Certificate2, bool> _acceptClient;

    public LoopbackTlsBroker(X509Certificate2 serverCertificate, SslProtocols protocols, Func<X509Certificate2, bool> acceptClient)
    {
        _serverCertificate = serverCertificate;
        _protocols = protocols;
        _acceptClient = acceptClient;
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The certificate the client presented, or <see langword="null"/>.</summary>
    public X509Certificate2? ClientCertificate { get; private set; }

    /// <summary>Every other certificate the client sent with its own: its chain on the wire.</summary>
    public List<X509Certificate2> ClientChain { get; } = [];

    /// <summary>Whether the acceptance check passed.</summary>
    public bool ClientAccepted { get; private set; }

    /// <summary>The negotiated protocol, once the handshake has completed.</summary>
    public SslProtocols NegotiatedProtocol { get; private set; }

    /// <summary>The CONNECT packet, once read.</summary>
    public ConnectPacket? Connect { get; private set; }

    /// <summary>The topic filters the client subscribed to, in order.</summary>
    public List<string> Subscriptions { get; } = [];

    /// <summary>The messages the client published, in order.</summary>
    public List<(string Topic, byte[] Payload)> Published { get; } = [];

    /// <summary>
    /// The service's answer to each message the client publishes, sent back as QoS 0 publishes.
    /// Unset, a publish is acknowledged and nothing more.
    /// </summary>
    public Func<string, byte[], IEnumerable<(string Topic, byte[] Payload)>>? Respond { get; init; }

    /// <summary>True when the client ended with an MQTT DISCONNECT.</summary>
    public bool DisconnectReceived { get; private set; }

    /// <summary>Why the server side failed, if it did.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Serves exactly one connection.</summary>
    public async Task ServeOneAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = _protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = Validate
            }, cancellationToken);

            NegotiatedProtocol = tls.SslProtocol;

            var (type, body) = await ReadPacketAsync(tls, cancellationToken);
            if (type != 0x10)
                throw new InvalidDataException($"Expected CONNECT, got packet type 0x{type:X2}.");

            Connect = ConnectPacket.Parse(body);

            // CONNACK: session present 0, return code 0 (accepted).
            await tls.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x00 }, cancellationToken);
            await tls.FlushAsync(cancellationToken);

            await ServeSessionAsync(tls, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failure = ex;
        }
    }

    public void Dispose() => _listener.Stop();

    private async Task ServeSessionAsync(Stream tls, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (type, body) = await ReadPacketAsync(tls, cancellationToken);
            switch (type >> 4)
            {
                case 14: // DISCONNECT
                    DisconnectReceived = true;
                    return;

                case 8: // SUBSCRIBE
                    await SubscribeAsync(tls, body, cancellationToken);
                    break;

                case 3: // PUBLISH
                    await PublishAsync(tls, type, body, cancellationToken);
                    break;

                case 12: // PINGREQ
                    await WritePacketAsync(tls, 0xD0, [], cancellationToken);
                    break;

                default:
                    throw new InvalidDataException($"Unexpected packet type 0x{type:X2}.");
            }
        }
    }

    /// <summary>Records each filter and grants it, at QoS 1 at most, as DPS and IoT Hub do.</summary>
    private async Task SubscribeAsync(Stream tls, byte[] body, CancellationToken cancellationToken)
    {
        // Packet ID, then each filter followed by its requested QoS.
        var position = 2;
        var granted = new List<byte>();
        while (position < body.Length)
        {
            Subscriptions.Add(Encoding.UTF8.GetString(ConnectPacket.Field(body, ref position)));
            granted.Add(Math.Min(body[position++], (byte)1));
        }

        await WritePacketAsync(tls, 0x90, [body[0], body[1], .. granted], cancellationToken);
    }

    /// <summary>Records the message, acknowledges it above QoS 0, and sends <see cref="Respond"/>'s answers.</summary>
    private async Task PublishAsync(Stream tls, byte type, byte[] body, CancellationToken cancellationToken)
    {
        // Topic, a packet ID above QoS 0, then the payload.
        var position = 0;
        var topic = Encoding.UTF8.GetString(ConnectPacket.Field(body, ref position));
        if (((type >> 1) & 0x03) > 0)
        {
            await WritePacketAsync(tls, 0x40, [body[position], body[position + 1]], cancellationToken);
            position += 2;
        }

        var payload = body[position..];
        Published.Add((topic, payload));
        foreach (var (answerTopic, answerPayload) in Respond?.Invoke(topic, payload) ?? [])
        {
            var name = Encoding.UTF8.GetBytes(answerTopic);
            await WritePacketAsync(
                tls, 0x30, [(byte)(name.Length >> 8), (byte)name.Length, .. name, .. answerPayload], cancellationToken);
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte type, byte[] body, CancellationToken cancellationToken)
    {
        // Remaining length: seven bits a byte, least significant first, high bit = more follows.
        var header = new List<byte> { type };
        var length = body.Length;
        do
        {
            var next = (byte)(length & 0x7F);
            length >>= 7;
            header.Add(length > 0 ? (byte)(next | 0x80) : next);
        }
        while (length > 0);

        await stream.WriteAsync(header.ToArray(), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
            return false;

        // Copies: the handshake owns the originals.
        ClientCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        // The chain's extra store holds what the peer sent besides its own certificate. The
        // platform policy errors are expected and ignored: the test CA is in no system store, and
        // the acceptance decision is the test's own.
        foreach (var sent in chain?.ChainPolicy.ExtraStore ?? [])
        {
            if (sent.Thumbprint != ClientCertificate.Thumbprint)
                ClientChain.Add(X509CertificateLoader.LoadCertificate(sent.RawData));
        }

        ClientAccepted = _acceptClient(ClientCertificate);
        return ClientAccepted;
    }

    private static async Task<(byte Type, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[1];
        await stream.ReadExactlyAsync(header, cancellationToken);

        // Remaining length: up to four bytes, seven bits each, least significant first.
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            var next = new byte[1];
            await stream.ReadExactlyAsync(next, cancellationToken);
            length |= (next[0] & 0x7F) << shift;
            if ((next[0] & 0x80) == 0)
                break;
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return (header[0], body);
    }
}

/// <summary>The fields of an MQTT 3.1.1 CONNECT packet that IoT Hub reads.</summary>
/// <param name="ClientId">The client identifier.</param>
/// <param name="UserName">The username, or <see langword="null"/> when the flag is not set.</param>
/// <param name="Password">The password bytes, or <see langword="null"/> when the flag is not set.</param>
public sealed record ConnectPacket(string ClientId, string? UserName, byte[]? Password)
{
    private const byte UserNameFlag = 0x80;
    private const byte PasswordFlag = 0x40;
    private const byte WillFlag = 0x04;

    /// <summary>Parses a CONNECT body: variable header, then payload.</summary>
    public static ConnectPacket Parse(byte[] body)
    {
        var position = 0;
        var protocol = Encoding.UTF8.GetString(Field(body, ref position));
        if (protocol != "MQTT" || body[position] != 4)
            throw new InvalidDataException($"Not MQTT 3.1.1: protocol '{protocol}', level {body[position]}.");

        var flags = body[position + 1];
        position += 4; // level, flags, keep-alive

        var clientId = Encoding.UTF8.GetString(Field(body, ref position));
        if ((flags & WillFlag) != 0)
        {
            Field(body, ref position);
            Field(body, ref position);
        }

        var userName = (flags & UserNameFlag) != 0 ? Encoding.UTF8.GetString(Field(body, ref position)) : null;
        var password = (flags & PasswordFlag) != 0 ? Field(body, ref position) : null;
        return new ConnectPacket(clientId, userName, password);
    }

    /// <summary>Reads one length-prefixed field, as every MQTT string and binary field is encoded.</summary>
    public static byte[] Field(byte[] body, ref int position)
    {
        var length = (body[position] << 8) | body[position + 1];
        var value = body.AsSpan(position + 2, length).ToArray();
        position += 2 + length;
        return value;
    }
}
