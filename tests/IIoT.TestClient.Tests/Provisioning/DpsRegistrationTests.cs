using System.Text;
using System.Text.Json;
using IIoT.TestClient.Provisioning;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// What each DPS answer means for the device. The bodies follow DPS's RegistrationOperationStatus
/// and error shapes.
/// </summary>
public class DpsRegistrationTests
{
    private const string OperationId = "4.d0a671905ea5b2c8.42d78160-4c78-479e-8be7-61d5e55dac0d";

    [Fact]
    public void RequestPayload_IsTheRegistrationId()
    {
        using var document = JsonDocument.Parse(DpsRegistration.RequestPayload("tank-000003"));

        Assert.Equal("tank-000003", document.RootElement.GetProperty("registrationId").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void Read_Assigning_IsInProgressWithTheOperationAndTheWait()
    {
        var outcome = Read(202, TimeSpan.FromSeconds(3), $$"""{"operationId":"{{OperationId}}","status":"assigning"}""");

        Assert.Equal(new DpsInProgress(OperationId, TimeSpan.FromSeconds(3)), outcome);
    }

    [Fact]
    public void Read_Assigned_NamesTheHubAndTheDeviceId()
    {
        var outcome = Read(200, null, $$$"""
            {"operationId":"{{{OperationId}}}","status":"assigned",
             "registrationState":{"x509":{},"registrationId":"tank-000003","createdDateTimeUtc":"2026-10-30T12:00:00Z",
              "assignedHub":"iot-iiot-staging-neu-yg77.azure-devices.net","deviceId":"tank-000003",
              "status":"assigned","substatus":"initialAssignment","lastUpdatedDateTimeUtc":"2026-10-30T12:00:01Z","etag":"IjAx"}}
            """);

        Assert.Equal(new DpsAssigned("iot-iiot-staging-neu-yg77.azure-devices.net", "tank-000003", "initialAssignment"), outcome);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("disabled")]
    [InlineData("unassigned")]
    public void Read_FinishedButNotAssigned_IsAFailureWithDpsReason(string status)
    {
        var outcome = Read(200, null, $$$"""
            {"operationId":"{{{OperationId}}}","status":"{{{status}}}",
             "registrationState":{"registrationId":"tank-000003","status":"{{{status}}}","errorCode":400209,"errorMessage":"Device is disabled."}}
            """);

        Assert.Equal(new DpsFailed(200, 400209, "Device is disabled."), outcome);
    }

    [Fact]
    public void Read_AssignedWithoutAHub_IsAFailureNotAConnectionToNowhere()
    {
        var outcome = Read(200, null, $$$"""{"operationId":"{{{OperationId}}}","status":"assigned","registrationState":{"deviceId":"tank-000003"}}""");

        var failed = Assert.IsType<DpsFailed>(outcome);
        Assert.Contains("no hub", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Refused_CarriesDpsErrorCodeAndMessage()
    {
        var outcome = Read(401, null, """{"errorCode":401002,"trackingId":"t-1","message":"The device is unauthorized.","timestampUtc":"2026-10-30T12:00:00Z"}""");

        Assert.Equal(new DpsFailed(401, 401002, "The device is unauthorized."), outcome);
    }

    [Fact]
    public void Read_Throttled_WaitsAsTold()
    {
        var outcome = Read(429, TimeSpan.FromSeconds(10), """{"errorCode":429001,"message":"Throttled."}""");

        Assert.Equal(new DpsThrottled(TimeSpan.FromSeconds(10)), outcome);
    }

    [Fact]
    public void Read_InProgressWithoutAnOperationId_CannotBePolled()
    {
        var outcome = Read(202, TimeSpan.FromSeconds(3), """{"status":"assigning"}""");

        Assert.IsType<DpsFailed>(outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Read_AnUnreadableBody_IsAFailureNotAnException(string body)
    {
        var outcome = Read(200, null, body);

        Assert.IsType<DpsFailed>(outcome);
    }

    [Theory]
    [InlineData(null, 3)] // DPS gave none: the default
    [InlineData(0, 1)] // never a busy loop
    [InlineData(3600, 60)] // never stalled for an hour
    [InlineData(5, 5)]
    public void Read_TheWaitIsBounded(int? seconds, int expected)
    {
        var retryAfter = seconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        var outcome = Assert.IsType<DpsInProgress>(Read(202, retryAfter, $$"""{"operationId":"{{OperationId}}","status":"assigning"}"""));

        Assert.Equal(TimeSpan.FromSeconds(expected), outcome.RetryAfter);
    }

    private static DpsOutcome Read(int status, TimeSpan? retryAfter, string body) =>
        DpsRegistration.Read(new DpsResponseTopic(status, "1", retryAfter), Encoding.UTF8.GetBytes(body));
}
