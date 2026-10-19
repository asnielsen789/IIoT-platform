using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>
/// IoT Hub's device ID rules, as documented under "Device identity properties" in the identity
/// registry article: up to 128 ASCII characters, letters, digits and <c>- . % _ * ? ! ( ) , : = @ $ '</c>.
/// </summary>
public class DeviceIdTests
{
    [Theory]
    [InlineData("tank-000002")]
    [InlineData("a")]
    [InlineData("Tank.Site_7")]
    [InlineData("-.%_*?!(),:=@$'")]
    public void Problem_AnAcceptedId_HasNone(string deviceId)
    {
        Assert.Null(DeviceId.Problem(deviceId));
    }

    [Fact]
    public void Problem_128Characters_IsTheLimit()
    {
        Assert.Null(DeviceId.Problem(new string('a', 128)));
        Assert.NotNull(DeviceId.Problem(new string('a', 129)));
    }

    [Theory]
    [InlineData("tank+2")] // documented as not supported
    [InlineData("tank#2")] // documented as not supported
    [InlineData("tank 2")]
    [InlineData("tank/2")]
    [InlineData("tank\\2")]
    [InlineData("tank\"2")]
    [InlineData("tanké")]
    [InlineData("tank\n2")]
    public void Problem_AForbiddenCharacter_IsNamed(string deviceId)
    {
        var problem = DeviceId.Problem(deviceId);

        Assert.NotNull(problem);
        Assert.Contains("contains", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Problem_NoId_IsAProblem(string? deviceId)
    {
        Assert.NotNull(DeviceId.Problem(deviceId));
    }

    [Fact]
    public void Validate_Throws_WithTheProblem()
    {
        var exception = Assert.Throws<ArgumentException>(() => DeviceId.Validate("tank#2"));

        Assert.Contains("'#'", exception.Message, StringComparison.Ordinal);
    }
}
