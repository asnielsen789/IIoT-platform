using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Payloads;
using IIoT.TestClient.Simulation;
using MQTTnet;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// <c>provision</c>: what a device does on first power-up with zero-touch provisioning (#48).
/// Register with DPS by certificate, wait for the assignment, then connect to the hub DPS named,
/// with the same certificate, and send one message.
/// </summary>
/// <remarks>
/// Nothing about the hub is configured: its hostname comes from DPS, and the device ID is the
/// certificate's CN, which the enrollment group registers as the device ID. The connection to the
/// hub is the ordinary one (<see cref="SimulationRunner"/>), so a provisioned device is shown to
/// work exactly as a device registered by hand does.
/// </remarks>
public sealed class ProvisioningRunner
{
    /// <summary>Requests to send before giving up: the registration, then a poll every few seconds.</summary>
    public const int MaxRequests = 20;

    /// <summary>The longest the whole registration may take.</summary>
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromMinutes(2);

    private readonly ProvisionOptions _options;
    private readonly TelemetryFixtures _fixtures;
    private readonly TextWriter _log;

    public ProvisioningRunner(ProvisionOptions options, TelemetryFixtures fixtures, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _fixtures = fixtures;
        _log = log;
    }

    /// <summary>Provisions the device and, unless <c>--no-send</c>, sends one message.</summary>
    /// <returns><see langword="true"/> when the device was assigned, and its message sent if one was asked for.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        // Before DPS is asked anything: an unknown fixture fails now, not after a registration.
        if (_options.Send)
            _fixtures.Select(_options.Fixture);

        DpsOutcome outcome;
        string registrationId;
        using (var authentication = X509CertificateAuthentication.LoadByCommonName(
            _options.CertificateFile, _options.KeyFile, DateTimeOffset.UtcNow))
        {
            registrationId = authentication.DeviceId;
            await _log.WriteLineAsync(
                $"[{registrationId}] registering with DPS {_options.IdScope} at {_options.DpsHost}:{DpsEndpoint.Port}");

            outcome = await RegisterAsync(authentication, cancellationToken);
        }

        if (outcome is DpsFailed failed)
        {
            await _log.WriteLineAsync(
                $"[{registrationId}] DPS refused: {failed.StatusCode}{(failed.ErrorCode is { } code ? $" ({code})" : "")}: {failed.Reason}");
            return false;
        }

        var assigned = (DpsAssigned)outcome;
        await _log.WriteLineAsync(
            $"[{registrationId}] assigned to {assigned.AssignedHub} as {assigned.DeviceId} ({assigned.Substatus ?? "no substatus"})");

        // IoT Hub accepts a CA-signed certificate only for the device its CN names. An enrollment
        // that registered the device under another ID would leave it unable to connect.
        if (assigned.DeviceId != registrationId)
        {
            await _log.WriteLineAsync(
                $"[{registrationId}] DPS registered the device as '{assigned.DeviceId}', not as its certificate's CN; " +
                "IoT Hub will refuse the certificate for that device. Check the enrollment's device ID setting.");
            return false;
        }

        if (!_options.Send)
            return true;

        var hubOptions = new TestClientOptions
        {
            Hub = assigned.AssignedHub,
            DeviceId = assigned.DeviceId,
            CertificateFile = _options.CertificateFile,
            KeyFile = _options.KeyFile,
            MessageCount = 1,
            Fixture = _options.Fixture
        };
        hubOptions.Validate();

        await new SimulationRunner(hubOptions, _fixtures, _log).RunAsync(cancellationToken);
        return true;
    }

    private async Task<DpsOutcome> RegisterAsync(X509CertificateAuthentication authentication, CancellationToken cancellationToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();

        // Before the connect, so the handler is in place before anything can arrive.
        var channel = new MqttDpsChannel(client);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RegistrationTimeout);
        try
        {
            var connect = await client.ConnectAsync(
                DpsConnection.Build(_options.DpsHost, _options.IdScope, authentication), timeout.Token);
            await _log.WriteLineAsync($"[{authentication.DeviceId}] connected to DPS: {connect.ResultCode}");

            return await new DpsProvisioningClient(channel, Task.Delay, _log)
                .ProvisionAsync(authentication.DeviceId, MaxRequests, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DpsFailed(0, null, $"DPS had not assigned the device within {RegistrationTimeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            if (client.IsConnected)
            {
                // Not the cancellation token, as in DeviceSimulator: a cancelled run still disconnects.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), shutdown.Token);
            }
        }
    }
}
