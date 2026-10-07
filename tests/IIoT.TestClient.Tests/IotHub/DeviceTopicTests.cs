using IIoT.TestClient.IotHub;

namespace IIoT.TestClient.Tests.IotHub;

public class DeviceTopicTests
{
    /// <summary>
    /// The exact string from section 1 of <c>docs/mqtt-payload.md</c>, pinned character by
    /// character. Get this wrong and IoT Hub accepts the message, routes nothing and reports no
    /// error — there is no failure to notice, which is why it is pinned rather than derived.
    /// </summary>
    [Fact]
    public void Events_MatchesTheContractExactly()
    {
        Assert.Equal(
            "devices/tank-000123/messages/events/$.ct=application%2Fjson&$.ce=utf-8",
            DeviceTopic.Events("tank-000123"));
    }

    [Fact]
    public void PropertyBag_EncodesOnlyTheValues()
    {
        // The '$' and '.' of the keys stay literal; the '/' of the media type does not.
        Assert.Equal("$.ct=application%2Fjson&$.ce=utf-8", DeviceTopic.PropertyBag);
    }

    [Fact]
    public void Events_PlacesTheDeviceIdInTheSecondSegment()
    {
        var topic = DeviceTopic.Events("tank-000777");

        Assert.StartsWith("devices/tank-000777/messages/events/", topic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Events_RejectsAnEmptyDeviceId(string deviceId) =>
        Assert.Throws<ArgumentException>(() => DeviceTopic.Events(deviceId));
}
