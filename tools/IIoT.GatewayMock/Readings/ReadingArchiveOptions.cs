using Microsoft.Extensions.Options;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// Where the mock keeps what it accepts beyond its own lifetime (ADR-012). Bound from
/// <c>GatewayMock:Archive</c>.
/// </summary>
/// <remarks>
/// Opt-in, with <see cref="ContainerUri"/> as the switch. Without it the mock holds what it accepts
/// in memory only, as it always has: tests and local runs need no storage account.
/// </remarks>
public sealed class ReadingArchiveOptions
{
    public const string SectionName = "GatewayMock:Archive";

    /// <summary>The value of <see cref="Credential"/> that selects the app's managed identity.</summary>
    public const string ManagedIdentity = "managedidentity";

    /// <summary>
    /// The blob container, as <c>https://&lt;account&gt;.blob.core.windows.net/&lt;container&gt;</c>.
    /// Setting it enables the archive. HTTPS, and no query: access is by Microsoft Entra ID, never by
    /// a SAS token in the configuration.
    /// </summary>
    public string? ContainerUri { get; set; }

    /// <summary>
    /// <c>managedidentity</c> for the app's system-assigned managed identity, as the templates set
    /// it. Absent, the developer's own sign-in (Azure CLI, Visual Studio and the like), for a local
    /// run against a real account.
    /// </summary>
    public string? Credential { get; set; }

    /// <summary>
    /// Whether <paramref name="configuration"/> enables the archive. Decided before the app is
    /// built, because it decides what is registered.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{nameof(ContainerUri)}"]);
}

/// <remarks>
/// Runs only when the archive is enabled. The container URI is no secret, but the messages name the
/// setting rather than repeat its value, as the mock's other validators do.
/// </remarks>
internal sealed class ReadingArchiveOptionsValidator : IValidateOptions<ReadingArchiveOptions>
{
    public ValidateOptionsResult Validate(string? name, ReadingArchiveOptions options)
    {
        const string Section = ReadingArchiveOptions.SectionName;
        var failures = new List<string>();

        if (!Uri.TryCreate(options.ContainerUri, UriKind.Absolute, out var container) ||
            container.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{Section}:ContainerUri must be an absolute HTTPS URL; the batches it receives carry the fleet's readings.");
        }
        else if (container.Query.Length > 0 || container.Fragment.Length > 0)
        {
            failures.Add($"{Section}:ContainerUri must have no query or fragment; the archive is reached with Microsoft Entra ID, not a SAS token.");
        }
        else if (container.AbsolutePath.Trim('/').Length == 0 || container.AbsolutePath.Trim('/').Contains('/'))
        {
            failures.Add($"{Section}:ContainerUri must name exactly one container: https://<account>.blob.core.windows.net/<container>.");
        }

        if (!string.IsNullOrEmpty(options.Credential) &&
            !string.Equals(options.Credential, ReadingArchiveOptions.ManagedIdentity, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{Section}:Credential must be {ReadingArchiveOptions.ManagedIdentity}, or absent for the developer's own sign-in.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
