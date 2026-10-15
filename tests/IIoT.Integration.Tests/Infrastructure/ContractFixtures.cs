namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// The example payloads of both contracts, copied into the output directory by the project file:
/// <c>docs/schemas/examples/</c> (device to platform) and <c>docs/schemas/gateway/examples/</c>
/// (platform to gateway).
/// </summary>
/// <remarks>
/// Discovered rather than listed, so a new example becomes a test case without anyone registering
/// it. The filename prefix is the expected outcome: <c>valid-</c> or <c>invalid-</c>.
/// </remarks>
public static class ContractFixtures
{
    public static string DeviceRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Device");

    public static string GatewayRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gateway");

    public static IEnumerable<object[]> ValidGateway() => Names(GatewayRoot, "valid-*.json");

    public static IEnumerable<object[]> InvalidGateway() => Names(GatewayRoot, "invalid-*.json");

    public static IEnumerable<object[]> ValidDevice() => Names(DeviceRoot, "valid-*.json");

    public static string ReadDevice(string fileName) => File.ReadAllText(Path.Combine(DeviceRoot, fileName));

    public static string ReadGateway(string fileName) => File.ReadAllText(Path.Combine(GatewayRoot, fileName));

    private static IEnumerable<object[]> Names(string root, string pattern) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new object[] { name! })
            : [];
}
