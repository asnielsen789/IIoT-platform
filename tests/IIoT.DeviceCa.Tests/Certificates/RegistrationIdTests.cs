using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>
/// DPS's registration ID rules, as documented under "Enrollment group" and "Registration ID" in the
/// DPS terminology article: letters, digits and <c>- . _ :</c>, ending in a letter, a digit or
/// <c>-</c>, and at most 64 characters in an X.509 enrollment group.
/// </summary>
public class RegistrationIdTests
{
    [Theory]
    [InlineData("tank-000003")]
    [InlineData("a")]
    [InlineData("Site_7.tank:3")]
    [InlineData("tank-")] // a trailing dash is allowed
    public void Problem_AnAcceptedId_HasNone(string deviceId)
    {
        Assert.Null(RegistrationId.Problem(deviceId));
    }

    [Fact]
    public void Problem_64Characters_IsTheLimit()
    {
        Assert.Null(RegistrationId.Problem(new string('a', 64)));
        Assert.Contains("at most 64", RegistrationId.Problem(new string('a', 65)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tank@3")] // allowed by IoT Hub, not by DPS
    [InlineData("tank(3)")]
    [InlineData("tank%3")]
    [InlineData("tank 3")]
    [InlineData("tanké")]
    public void Problem_ACharacterDpsRefuses_IsNamed(string deviceId)
    {
        var problem = RegistrationId.Problem(deviceId);

        Assert.NotNull(problem);
        Assert.Contains("contains", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tank.")]
    [InlineData("tank_")]
    [InlineData("tank:")]
    public void Problem_AnEndingDpsRefuses_IsNamed(string deviceId)
    {
        Assert.Contains("must end in", RegistrationId.Problem(deviceId), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Problem_NoId_IsAProblem(string? deviceId)
    {
        Assert.NotNull(RegistrationId.Problem(deviceId));
    }
}
