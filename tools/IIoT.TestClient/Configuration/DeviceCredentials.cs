using System.Text.Json;
using System.Text.Json.Serialization;

namespace IIoT.TestClient.Configuration;

/// <summary>Reads device credentials from a runtime-supplied devices file.</summary>
/// <remarks>
/// The file format is deliberately minimal and deliberately not a configuration file the build
/// knows about: there is no appsettings entry, no user-secrets id and nothing gitignored-but-
/// expected. The operator passes <c>--devices</c> and keeps the file wherever they keep secrets.
/// </remarks>
public static class DeviceCredentials
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Parses <c>[{ "deviceId": "...", "key": "..." }, ...]</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The content is not a usable list. Thrown rather than returning an empty list, because an
    /// empty fleet produces a run that connects to nothing and reports no error.
    /// </exception>
    public static IReadOnlyList<DeviceCredential> Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(json, Options);
        }
        catch (JsonException ex)
        {
            // Deliberately does not echo the content: it holds keys.
            throw new InvalidOperationException(
                $"The devices file is not valid JSON ({ex.Message.Split(". ")[0]}). Expected " +
                "[{ \"deviceId\": \"...\", \"key\": \"...\" }].", ex);
        }

        if (entries is null || entries.Count == 0)
            throw new InvalidOperationException("The devices file contains no devices.");

        var credentials = new List<DeviceCredential>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.DeviceId) || string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new InvalidOperationException(
                    $"Entry {i} in the devices file is missing 'deviceId' or 'key'.");
            }

            credentials.Add(new DeviceCredential(entry.DeviceId, entry.Key));
        }

        return credentials;
    }

    /// <summary>Reads and parses the file at <paramref name="path"/>.</summary>
    public static IReadOnlyList<DeviceCredential> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Devices file not found: {path}", path);

        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Resolves the credentials a run will use: the devices file if given, otherwise the single
    /// device, truncated to <c>--device-count</c>.
    /// </summary>
    public static IReadOnlyList<DeviceCredential> Resolve(TestClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var available = options.DevicesFile is not null
            ? Load(options.DevicesFile)
            : [new DeviceCredential(options.DeviceId!, options.DeviceKey!)];

        if (options.DeviceCount > available.Count)
        {
            throw new InvalidOperationException(
                $"--device-count {options.DeviceCount} was requested but only {available.Count} " +
                "device(s) are available.");
        }

        return available.Take(options.DeviceCount).ToList();
    }

    private sealed record Entry
    {
        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
