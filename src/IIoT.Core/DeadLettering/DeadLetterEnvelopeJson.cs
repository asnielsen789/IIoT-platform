using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IIoT.Core.DeadLettering;

/// <summary>
/// The dead-letter entry's wire format: how a <see cref="DeadLetterEnvelope"/> is written to a
/// queue message and read back, and how large it may be.
/// </summary>
/// <remarks>
/// <para>
/// One set of options for both directions, as <c>TelemetryJson</c> is for the device contract, so
/// the writer and the reader cannot drift apart. camelCase, unknown members ignored (a later version
/// may add one), a repeated member refused (two values for one field would leave the reader to pick
/// one).
/// </para>
/// <para>
/// The payload is a JSON <em>string</em> inside the entry, not a nested object. Re-serialising it
/// as an object would normalise its whitespace and number formatting; as a string it is kept
/// character for character, which is what "exactly as received" means.
/// </para>
/// <para>
/// Escaping is the relaxed encoder's. The default encoder escapes every non-ASCII character and the
/// HTML-sensitive ones, so each <c>+</c> of a UTC offset would become <c>+</c>: six bytes for
/// one, in an entry an operator has to read. HTML safety buys nothing here. The entry is never
/// embedded in a page, and the queue holds it base64-encoded, so no character of it reaches the
/// service's XML request body as itself. Quotes, backslashes and control characters are still
/// escaped, as JSON requires.
/// </para>
/// </remarks>
public static class DeadLetterEnvelopeJson
{
    /// <summary>
    /// A Storage queue message is at most 64 KiB, measured after encoding (Put Message, REST API
    /// reference: "The encoded message can be up to 64 kibibytes (KiB) in size").
    /// </summary>
    public const int MaxQueueMessageLength = 64 * 1024;

    /// <summary>
    /// The largest entry, in UTF-8 bytes, that still fits in a queue message once base64-encoded.
    /// The Functions queue trigger expects base64 by default, and base64 writes every 3 bytes as 4
    /// characters, so 64 KiB of message holds 48 KiB of entry: 49,152 bytes.
    /// </summary>
    public const int MaxEntryBytes = MaxQueueMessageLength / 4 * 3;

    private const string VersionProperty = "envelopeVersion";
    private const int MaxQuotedLength = 40;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // A null where the model has a non-nullable string, or a missing constructor parameter of a
        // positional record, is refused rather than read as null. Either would otherwise surface
        // later, as a NullReferenceException in the retry, instead of as a malformed entry now.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>The entry as it goes onto the queue, before the queue client base64-encodes it.</summary>
    public static string Serialize(DeadLetterEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    /// <summary>The entry's size as the limit counts it: UTF-8 bytes before base64.</summary>
    public static int ByteCount(string entry) => Encoding.UTF8.GetByteCount(entry);

    /// <summary>Whether <paramref name="entry"/> fits in one queue message. See <see cref="MaxEntryBytes"/>.</summary>
    public static bool FitsInQueueMessage(string entry) => ByteCount(entry) <= MaxEntryBytes;

    /// <summary>
    /// Reads an entry back. Never throws: an entry is either a version-1 envelope with every field a
    /// retry relies on, or refused with a reason that is safe to log, since it names fields, not
    /// their contents.
    /// </summary>
    public static DeadLetterEnvelopeReadResult Read(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return DeadLetterEnvelopeReadResult.Invalid("Entry is empty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(entry, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return DeadLetterEnvelopeReadResult.Invalid($"Entry is not valid JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return DeadLetterEnvelopeReadResult.Invalid($"Entry is not valid text: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DeadLetterEnvelopeReadResult.Invalid("Entry is not a JSON object");

            // Checked before anything else, so an entry from a later build is refused for what it
            // is, not for whichever field it happens to lack.
            if (!root.TryGetProperty(VersionProperty, out var version))
                return DeadLetterEnvelopeReadResult.Invalid($"Entry has no {VersionProperty}");

            if (version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) ||
                number != DeadLetterEnvelope.CurrentVersion)
            {
                return DeadLetterEnvelopeReadResult.Invalid(
                    $"Unsupported {VersionProperty} '{Quote(version.GetRawText())}'; " +
                    $"this build understands version {DeadLetterEnvelope.CurrentVersion}");
            }

            DeadLetterEnvelope? envelope;
            try
            {
                envelope = root.Deserialize<DeadLetterEnvelope>(Options);
            }
            catch (JsonException ex)
            {
                return DeadLetterEnvelopeReadResult.Invalid(
                    $"Entry is not a valid version {DeadLetterEnvelope.CurrentVersion} envelope: {ex.Message}");
            }

            // Present is not the same as usable: a blank identity must not reach the retry, where it
            // would be rejected as an unknown identity and reported as a security event.
            var problem = envelope switch
            {
                null => "Entry is null",
                { MessageId.Length: 0 } => "Entry has an empty messageId",
                _ when string.IsNullOrWhiteSpace(envelope.AuthenticatedDeviceId) => "Entry has an empty authenticatedDeviceId",
                _ when string.IsNullOrWhiteSpace(envelope.Payload) => "Entry has an empty payload",
                _ => null
            };

            return problem is null
                ? DeadLetterEnvelopeReadResult.Valid(envelope!)
                : DeadLetterEnvelopeReadResult.Invalid(problem);
        }
    }

    private static string Quote(string value) =>
        value.Length <= MaxQuotedLength ? value : value[..MaxQuotedLength] + "...";
}
