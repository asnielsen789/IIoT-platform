using System.Text.Json;

namespace IIoT.Core.Serialization;

/// <summary>
/// A value that satisfied the schema but that the platform cannot hold, and the reason why — an
/// offset no time zone uses, a sequence number beyond the range the platform keeps it in.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="JsonException"/>, so the serializer stamps it with the path of the offending field
/// on its way out, and the validator reports field and reason together the way it reports a schema
/// error. Kept apart from a plain <see cref="JsonException"/>, which still means the models have
/// drifted from the schema: that is a bug here, not a message to reject.
/// </para>
/// <para>The message quotes at most the start of the value, and is written to be logged.</para>
/// </remarks>
internal sealed class RejectedValueException : JsonException
{
    public RejectedValueException(string message)
        : base(message)
    {
    }
}
