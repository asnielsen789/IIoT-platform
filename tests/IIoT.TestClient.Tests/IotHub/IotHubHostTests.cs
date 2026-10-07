using IIoT.TestClient.IotHub;

namespace IIoT.TestClient.Tests.IotHub;

public class IotHubHostTests
{
    [Theory]
    [InlineData("example-hub", "example-hub.azure-devices.net")]
    [InlineData("  example-hub  ", "example-hub.azure-devices.net")]
    [InlineData("example-hub.azure-devices.net", "example-hub.azure-devices.net")]
    [InlineData("example-hub.azure-devices.cn", "example-hub.azure-devices.cn")]
    public void Resolve_AddsTheSuffixOnlyToABareName(string input, string expected) =>
        Assert.Equal(expected, IotHubHost.Resolve(input));

    [Fact]
    public void Resolve_RejectsAnEmptyValue() =>
        Assert.Throws<ArgumentException>(() => IotHubHost.Resolve("  "));
}
