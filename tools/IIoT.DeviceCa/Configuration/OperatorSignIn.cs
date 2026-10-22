namespace IIoT.DeviceCa.Configuration;

/// <summary>
/// Keeps the tool's sign-in to a person's own (ADR-009: "run by an operator, never by the Function
/// or CI"), including when <c>AZURE_TOKEN_CREDENTIALS</c> is set.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.CreateCredential</c> excludes the credentials a machine or a pipeline brings:
/// environment variables, workload identity and managed identity. Azure.Identity 1.21 also reads
/// <c>AZURE_TOKEN_CREDENTIALS</c>, and a single credential named there replaces the whole chain
/// <em>and ignores those exclusions</em>: with <c>environmentcredential</c> or
/// <c>managedidentitycredential</c> the tool would sign in as a service principal or a managed
/// identity. (Seen by constructing the credential with each value and listing the credentials it
/// holds; <c>prod</c> fails on its own, since every credential it contains is excluded.)
/// </para>
/// <para>
/// So the variable is checked against an allow-list of the developer sign-ins Azure.Identity
/// accepts there. Unset is the normal case. Naming one of them is useful: with
/// <c>azureclicredential</c>, a Visual Studio sign-in to some other account, tried before the
/// Azure CLI by default, cannot be picked by accident.
/// </para>
/// </remarks>
public static class OperatorSignIn
{
    /// <summary>The variable Azure.Identity reads to narrow <c>DefaultAzureCredential</c>.</summary>
    public const string SelectionVariable = "AZURE_TOKEN_CREDENTIALS";

    /// <summary>The values that keep the sign-in a person's own, as Azure.Identity spells them.</summary>
    public static IReadOnlyList<string> DeveloperSelections { get; } =
    [
        "dev",
        "azureclicredential",
        "azurepowershellcredential",
        "azuredeveloperclicredential",
        "visualstudiocredential",
        "visualstudiocodecredential",
        "interactivebrowsercredential",
        "brokercredential"
    ];

    /// <summary>
    /// Returns why <paramref name="selection"/>, the value of <see cref="SelectionVariable"/>, would
    /// let the tool sign in as something other than a person, or <see langword="null"/> when it is
    /// unset or names a developer sign-in.
    /// </summary>
    public static string? Problem(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return null;

        var value = selection.Trim();
        if (DeveloperSelections.Contains(value, StringComparer.OrdinalIgnoreCase))
            return null;

        return $"{SelectionVariable} is '{value}'. This tool signs in only as you, never as a service principal, " +
               $"a pipeline or a managed identity (ADR-009). Unset it, or set one of: {string.Join(", ", DeveloperSelections)}.";
    }
}
