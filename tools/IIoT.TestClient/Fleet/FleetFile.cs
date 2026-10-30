using System.Text.Json;

namespace IIoT.TestClient.Fleet;

/// <summary>
/// Reads a fleet file: the devices to simulate, with their profiles. No secrets: a device's
/// certificate and key are found by its ID in a directory outside the repository.
/// </summary>
/// <remarks>
/// <code>
/// { "devices": [ { "id": "charger-000001", "profile": "charger", "site": "North fuel station" } ] }
/// </code>
/// Profiles are <c>tank</c>, <c>charger</c>, <c>car-wash</c> and <c>oil-separator</c>. Every ID must
/// be a valid DPS registration ID, since the fleet's devices provision themselves (#48), and IDs
/// are unique.
/// </remarks>
public static class FleetFile
{
    /// <summary>The file name of the reference fleet, copied next to the client.</summary>
    public const string ReferenceFileName = "staging-fleet.json";

    /// <summary>The reference fleet as the client's build output holds it.</summary>
    public static string ReferencePath { get; } = Path.Combine(AppContext.BaseDirectory, "Fleet", ReferenceFileName);

    private const int MaxIdLength = 64;
    private const string IdSpecialCharacters = "-._:";

    /// <exception cref="FileNotFoundException">The file is missing.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid fleet.</exception>
    public static IReadOnlyList<FleetDevice> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path)
            ? Parse(File.ReadAllText(path), path)
            : throw new FileNotFoundException($"Fleet file not found: {path}", path);
    }

    /// <exception cref="InvalidDataException">The text is not a valid fleet; the message says why and where.</exception>
    public static IReadOnlyList<FleetDevice> Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("devices", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{source} must be an object with a \"devices\" array.");
            }

            var devices = new List<FleetDevice>();
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                devices.Add(Read(entry, $"{source}, devices[{index++}]"));
            }

            var duplicate = devices.GroupBy(device => device.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidDataException($"{source} lists device '{duplicate.Key}' more than once.");
            }

            return devices;
        }
    }

    private static FleetDevice Read(JsonElement entry, string location)
    {
        var id = Text(entry, "id", location);
        var problem = IdProblem(id);
        if (problem is not null)
        {
            throw new InvalidDataException($"{location}: {problem}");
        }

        var profile = Text(entry, "profile", location) switch
        {
            "tank" => DeviceProfile.Tank,
            "charger" => DeviceProfile.Charger,
            "car-wash" => DeviceProfile.CarWash,
            "oil-separator" => DeviceProfile.OilSeparator,
            var other => throw new InvalidDataException(
                $"{location}: unknown profile '{other}'; expected tank, charger, car-wash or oil-separator.")
        };

        var site = entry.TryGetProperty("site", out var siteElement) && siteElement.ValueKind == JsonValueKind.String
            ? siteElement.GetString() ?? ""
            : "";

        return new FleetDevice(id, profile, site);
    }

    private static string Text(JsonElement entry, string name, string location) =>
        entry.ValueKind == JsonValueKind.Object &&
        entry.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"{location}: \"{name}\" is required.");

    /// <summary>
    /// DPS's rules for a registration ID, as <c>IIoT.DeviceCa</c> enforces them when it issues a
    /// certificate with <c>--no-register</c>: ASCII letters, digits and <c>- . _ :</c>, ending in a
    /// letter, a digit or <c>-</c>, at most 64 characters.
    /// </summary>
    public static string? IdProblem(string id)
    {
        if (id.Length > MaxIdLength)
        {
            return $"device ID '{id}' is longer than {MaxIdLength} characters, the most a DPS registration ID may have.";
        }

        if (id.Any(character => !char.IsAsciiLetterOrDigit(character) && !IdSpecialCharacters.Contains(character, StringComparison.Ordinal)))
        {
            return $"device ID '{id}' may hold only ASCII letters, digits and {IdSpecialCharacters}, as a DPS registration ID.";
        }

        return char.IsAsciiLetterOrDigit(id[^1]) || id[^1] == '-'
            ? null
            : $"device ID '{id}' must end in a letter, a digit or '-', as a DPS registration ID.";
    }
}
