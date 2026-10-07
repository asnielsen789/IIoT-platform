using IIoT.TestClient.Configuration;

namespace IIoT.TestClient.Tests.Configuration;

public class DeviceCredentialsTests
{
    private const string PlaceholderKey = "UGxhY2Vob2xkZXJLZXlOb3RBUmVhbFNlY3JldDEyMzQ1Njc4OTA=";

    private const string ThreeDevices = """
        [
          { "deviceId": "tank-000001", "key": "UGxhY2Vob2xkZXItMQ==" },
          { "deviceId": "tank-000002", "key": "UGxhY2Vob2xkZXItMg==" },
          { "deviceId": "tank-000003", "key": "UGxhY2Vob2xkZXItMw==" }
        ]
        """;

    [Fact]
    public void Parse_ReadsTheFleetInOrder()
    {
        var credentials = DeviceCredentials.Parse(ThreeDevices);

        Assert.Equal(
            ["tank-000001", "tank-000002", "tank-000003"],
            credentials.Select(c => c.DeviceId));
    }

    [Fact]
    public void Parse_AnEmptyArray_Fails()
    {
        // An empty fleet would run, connect to nothing and exit successfully.
        Assert.Throws<InvalidOperationException>(() => DeviceCredentials.Parse("[]"));
    }

    [Fact]
    public void Parse_AMissingKey_Fails()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DeviceCredentials.Parse("""[{ "deviceId": "tank-000001" }]"""));

        Assert.Contains("Entry 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Malformed_FailsWithoutEchoingTheContent()
    {
        // The file holds keys, so nothing from it belongs in an error message or a log.
        var exception = Assert.Throws<InvalidOperationException>(
            () => DeviceCredentials.Parse("""{ "deviceId": "tank-000001", "key": "SECRETVALUE" }"""));

        Assert.DoesNotContain("SECRETVALUE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TakesTheFirstNEntriesOfTheFleet()
    {
        var path = WriteTemporaryDevicesFile();
        try
        {
            var credentials = DeviceCredentials.Resolve(new TestClientOptions
            {
                Hub = "example-hub",
                DevicesFile = path,
                DeviceCount = 2
            });

            Assert.Equal(["tank-000001", "tank-000002"], credentials.Select(c => c.DeviceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_MoreDevicesThanTheFileHolds_Fails()
    {
        var path = WriteTemporaryDevicesFile();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => DeviceCredentials.Resolve(new TestClientOptions
                {
                    Hub = "example-hub",
                    DevicesFile = path,
                    DeviceCount = 99
                }));

            Assert.Contains("only 3", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_WithoutADevicesFile_UsesTheSingleDevice()
    {
        var credentials = DeviceCredentials.Resolve(new TestClientOptions
        {
            Hub = "example-hub",
            DeviceId = "tank-000123",
            DeviceKey = PlaceholderKey
        });

        var credential = Assert.Single(credentials);
        Assert.Equal("tank-000123", credential.DeviceId);
    }

    [Fact]
    public void Load_AMissingFile_Fails() =>
        Assert.Throws<FileNotFoundException>(
            () => DeviceCredentials.Load(Path.Combine(Path.GetTempPath(), "no-such-devices.json")));

    private static string WriteTemporaryDevicesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iiot-devices-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ThreeDevices);

        return path;
    }
}
