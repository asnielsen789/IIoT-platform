using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Simulation;

/// <summary>Builds the simulated fleet from a configuration and runs it.</summary>
public sealed class SimulationRunner
{
    private readonly TestClientOptions _options;
    private readonly TelemetryFixtures _fixtures;
    private readonly TextWriter _log;

    public SimulationRunner(TestClientOptions options, TelemetryFixtures fixtures, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _fixtures = fixtures;
        _log = log;
    }

    /// <summary>Starts every device and waits for all of them.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var selected = _fixtures.Select(_options.Fixture);

        // Before anything is logged or connected: a certificate for the wrong device, or a key
        // that does not match it, fails here with the reason rather than as a refused CONNECT.
        var authentications = CreateAuthentications();
        try
        {
            // One mutator for the whole fleet: it is stateless apart from the clock and the id
            // factory, and every device needs the same behaviour.
            var sequence = new PublicationSequence(selected, _options.Mutate ? new TelemetryMutator() : null);

            await _log.WriteLineAsync(
                $"{authentications.Count} device(s), {selected.Count} fixture(s) " +
                $"[{string.Join(", ", selected.Select(f => f.Name))}], " +
                $"mutate={_options.Mutate}, interval={_options.Interval.TotalSeconds:0.##}s, " +
                $"count={(_options.MessageCount?.ToString() ?? "unbounded")}");

            foreach (var certificate in authentications.OfType<X509CertificateAuthentication>())
            {
                await _log.WriteLineAsync(
                    $"[{certificate.DeviceId}] X.509 certificate {certificate.Certificate.Subject}, issued by " +
                    $"{certificate.Certificate.Issuer}, valid to {new DateTimeOffset(certificate.Certificate.NotAfter):u}, " +
                    $"SHA-1 thumbprint {certificate.Certificate.Thumbprint}");
            }

            var devices = authentications
                .Select(authentication => new DeviceSimulator(authentication, _options, sequence, _log))
                .ToList();

            await Task.WhenAll(devices.Select(device => device.RunAsync(cancellationToken)));
        }
        finally
        {
            // A certificate's key sits in a temporary key container on Windows until disposed.
            foreach (var disposable in authentications.OfType<IDisposable>())
                disposable.Dispose();
        }
    }

    private List<IDeviceAuthentication> CreateAuthentications()
    {
        if (_options.UsesCertificate)
        {
            return
            [
                X509CertificateAuthentication.Load(
                    _options.DeviceId!, _options.CertificateFile!, _options.KeyFile!, DateTimeOffset.UtcNow)
            ];
        }

        return DeviceCredentials.Resolve(_options)
            .Select(credential => new SharedAccessKeyAuthentication(credential.DeviceId, credential.Key))
            .ToList<IDeviceAuthentication>();
    }
}
