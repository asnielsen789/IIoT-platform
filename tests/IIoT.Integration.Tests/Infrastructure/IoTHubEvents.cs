using System.Text;
using Azure.Core.Amqp;
using Azure.Messaging.EventHubs;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>Device messages as the isolated worker receives them from IoT Hub.</summary>
/// <remarks>
/// The host reads the event from the hub's built-in endpoint and passes the worker the raw AMQP
/// message; the worker's Event Hubs extension rebuilds it with
/// <c>new EventData(AmqpAnnotatedMessage.FromBytes(...))</c>. This makes the same round trip,
/// carrying the annotations IoT Hub stamped on the first live message, so what the Function reads
/// from the transport runs as it does in Azure.
/// </remarks>
public static class IoTHubEvents
{
    /// <summary>
    /// A device message as IoT Hub stamps it, serialized the way the host hands it to the worker and
    /// rebuilt the way the worker's Event Hubs extension rebuilds it.
    /// </summary>
    /// <param name="enqueuedAt">AMQP timestamps carry milliseconds, so finer precision is lost.</param>
    public static EventData AsTheWorkerReceivesIt(string payload, string authenticatedDeviceId, DateTimeOffset enqueuedAt)
    {
        var message = new AmqpAnnotatedMessage(AmqpMessageBody.FromData([Encoding.UTF8.GetBytes(payload)]));
        message.Properties.ContentType = "application/json";
        message.Properties.ContentEncoding = "utf-8";

        message.MessageAnnotations["iothub-connection-device-id"] = authenticatedDeviceId;
        message.MessageAnnotations["iothub-message-source"] = "Telemetry";
        message.MessageAnnotations["iothub-enqueuedtime"] = enqueuedAt.UtcDateTime;
        message.MessageAnnotations["x-opt-sequence-number"] = 0L;
        message.MessageAnnotations["x-opt-offset"] = "0";
        message.MessageAnnotations["x-opt-enqueued-time"] = enqueuedAt.UtcDateTime;

        return new EventData(AmqpAnnotatedMessage.FromBytes(message.ToBytes()));
    }
}
