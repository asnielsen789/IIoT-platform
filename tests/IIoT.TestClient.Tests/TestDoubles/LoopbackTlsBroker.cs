using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace IIoT.TestClient.Tests.TestDoubles;

/// <summary>
/// Just enough of IoT Hub's device endpoint to test certificate authentication on loopback: a TLS
/// server on 127.0.0.1 that requires a client certificate, decides whether to accept it, and then
/// answers one MQTT 3.1.1 CONNECT with a CONNACK.
/// </summary>
/// <remarks>
/// It records what crossed the wire, which is the point: the certificate the client presented,
/// any further certificates it sent with it, and the CONNECT packet. It is not a broker and does
/// not pretend to be IoT Hub's certificate validation; the acceptance decision is the test's.
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

            var (next, _) = await ReadPacketAsync(tls, cancellationToken);
            DisconnectReceived = next == 0xE0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failure = ex;
        }
    }

    public void Dispose() => _listener.Stop();

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

    private static byte[] Field(byte[] body, ref int position)
    {
        var length = (body[position] << 8) | body[position + 1];
        var value = body.AsSpan(position + 2, length).ToArray();
        position += 2 + length;
        return value;
    }
}
