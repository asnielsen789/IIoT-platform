using System.Text.Encodings.Web;
using System.Text.Json;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>One piece of a formatted JSON document, with the kind that decides its colour.</summary>
/// <param name="Text">The text, exactly as it is shown.</param>
/// <param name="Kind">One of <c>key</c>, <c>string</c>, <c>number</c>, <c>literal</c>, <c>punct</c> or <c>space</c>; used as a CSS class.</param>
public sealed record JsonToken(string Text, string Kind);

/// <summary>
/// A stored body as the message view shows it: indented two spaces, in the order the device's
/// platform sent it, split into tokens so the page can colour keys, strings and numbers without a
/// script.
/// </summary>
/// <remarks>
/// <para>
/// Numbers keep their text as received (<see cref="JsonElement.GetRawText"/>), so a value reads as
/// it was sent, not as a <see cref="double"/> would print it. Strings are written as JSON string
/// literals, quotes and escapes included.
/// </para>
/// <para>
/// The relaxed encoder writes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and non-ASCII letters as
/// themselves, so the view shows what was sent rather than <c><</c>. That is safe only because
/// every token is rendered through Razor, which HTML-encodes it; a token must never be rendered as
/// markup.
/// </para>
/// </remarks>
internal static class JsonView
{
    private const string Indent = "  ";

    private static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<JsonToken> Tokens(JsonElement element)
    {
        var tokens = new List<JsonToken>();
        Write(element, 0, tokens);
        return tokens;
    }

    private static void Write(JsonElement element, int depth, List<JsonToken> tokens)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                WriteContainer("{", "}", properties.Count, depth, tokens, index =>
                {
                    tokens.Add(new JsonToken(Quote(properties[index].Name), "key"));
                    tokens.Add(new JsonToken(": ", "punct"));
                    Write(properties[index].Value, depth + 1, tokens);
                });
                break;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                WriteContainer("[", "]", items.Count, depth, tokens, index => Write(items[index], depth + 1, tokens));
                break;

            case JsonValueKind.String:
                tokens.Add(new JsonToken(Quote(element.GetString()!), "string"));
                break;

            case JsonValueKind.Number:
                tokens.Add(new JsonToken(element.GetRawText(), "number"));
                break;

            default:
                tokens.Add(new JsonToken(element.GetRawText(), "literal"));
                break;
        }
    }

    private static void WriteContainer(
        string open, string close, int count, int depth, List<JsonToken> tokens, Action<int> writeItem)
    {
        if (count == 0)
        {
            tokens.Add(new JsonToken(open + close, "punct"));
            return;
        }

        tokens.Add(new JsonToken(open, "punct"));
        for (var index = 0; index < count; index++)
        {
            tokens.Add(new JsonToken("\n" + string.Concat(Enumerable.Repeat(Indent, depth + 1)), "space"));
            writeItem(index);
            if (index < count - 1)
            {
                tokens.Add(new JsonToken(",", "punct"));
            }
        }

        tokens.Add(new JsonToken("\n" + string.Concat(Enumerable.Repeat(Indent, depth)), "space"));
        tokens.Add(new JsonToken(close, "punct"));
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Strings);
}
