using IIoT.TestClient.Provisioning;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// The topics and the CONNECT username exactly as Microsoft documents them for DPS over MQTT, and
/// the reading of a response topic.
/// </summary>
public class DpsTopicsTests
{
    [Fact]
    public void UserName_IsScopeRegistrationAndApiVersion()
    {
        Assert.Equal(
            "0ne01316416/registrations/tank-000003/api-version=2019-03-31",
            DpsEndpoint.UserName("0ne01316416", "tank-000003"));
    }

    [Fact]
    public void Endpoint_IsTheGlobalHostOnTheMqttTlsPort()
    {
        Assert.Equal("global.azure-devices-provisioning.net", DpsEndpoint.GlobalHostName);
        Assert.Equal(8883, DpsEndpoint.Port);
    }

    [Fact]
    public void ResponseFilter_IsTheOneDpsAccepts()
    {
        Assert.Equal("$dps/registrations/res/#", DpsTopics.ResponseFilter);
    }

    [Fact]
    public void Register_CarriesTheRequestId()
    {
        Assert.Equal("$dps/registrations/PUT/iotdps-register/?$rid=1", DpsTopics.Register("1"));
    }

    [Fact]
    public void OperationStatus_CarriesTheRequestAndOperationIds()
    {
        Assert.Equal(
            "$dps/registrations/GET/iotdps-get-operationstatus/?$rid=2&operationId=4.d0a6.42d7",
            DpsTopics.OperationStatus("2", "4.d0a6.42d7"));
    }

    [Fact]
    public void TryParseResponse_InProgress_HasTheStatusRequestAndRetryAfter()
    {
        Assert.True(DpsTopics.TryParseResponse("$dps/registrations/res/202/?$rid=1&retry-after=3", out var response));

        Assert.Equal(new DpsResponseTopic(202, "1", TimeSpan.FromSeconds(3)), response);
    }

    [Fact]
    public void TryParseResponse_Finished_HasNoRetryAfter()
    {
        Assert.True(DpsTopics.TryParseResponse("$dps/registrations/res/200/?$rid=7", out var response));

        Assert.Equal(new DpsResponseTopic(200, "7", null), response);
    }

    [Fact]
    public void TryParseResponse_DecodesTheRequestId()
    {
        Assert.True(DpsTopics.TryParseResponse("$dps/registrations/res/200/?$rid=a%26b", out var response));

        Assert.Equal("a&b", response.RequestId);
    }

    [Fact]
    public void TryParseResponse_ToleratesPropertiesItDoesNotKnow()
    {
        Assert.True(DpsTopics.TryParseResponse("$dps/registrations/res/202/?$rid=1&something=x&retry-after=2", out var response));

        Assert.Equal(TimeSpan.FromSeconds(2), response.RetryAfter);
    }

    [Theory]
    [InlineData("devices/tank-000003/messages/devicebound/")]
    [InlineData("$dps/registrations/res/")]
    [InlineData("$dps/registrations/res/abc/?$rid=1")]
    [InlineData("")]
    public void TryParseResponse_RejectsWhatIsNotAResponse(string topic)
    {
        Assert.False(DpsTopics.TryParseResponse(topic, out _));
    }

    [Fact]
    public void TryParseResponse_IgnoresAnUnreadableRetryAfter()
    {
        Assert.True(DpsTopics.TryParseResponse("$dps/registrations/res/202/?$rid=1&retry-after=soon", out var response));

        Assert.Null(response.RetryAfter);
    }
}
