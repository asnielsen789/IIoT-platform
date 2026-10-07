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
        var credentials = DeviceCredentials.Resolve(_options);

        // One mutator for the whole fleet: it is stateless apart from the clock and the id
        // factory, and every device needs the same behaviour.
        var sequence = new PublicationSequence(selected, _options.Mutate ? new TelemetryMutator() : null);

        await _log.WriteLineAsync(
            $"{credentials.Count} device(s), {selected.Count} fixture(s) " +
            $"[{string.Join(", ", selected.Select(f => f.Name))}], " +
            $"mutate={_options.Mutate}, interval={_options.Interval.TotalSeconds:0.##}s, " +
            $"count={(_options.MessageCount?.ToString() ?? "unbounded")}");

        var devices = credentials
            .Select(credential => new DeviceSimulator(
                new SharedAccessKeyAuthentication(credential.DeviceId, credential.Key),
                _options,
                sequence,
                _log))
            .ToList();

        await Task.WhenAll(devices.Select(device => device.RunAsync(cancellationToken)));
    }
}
