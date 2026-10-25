using IIoT.DeviceCa.Configuration;

namespace IIoT.DeviceCa.Tests.Configuration;

/// <summary>
/// AZURE_TOKEN_CREDENTIALS must not turn the operator's tool into one a pipeline or a managed
/// identity can run: Azure.Identity honours a single credential named there even when the tool
/// excluded it.
/// </summary>
public class OperatorSignInTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("azureclicredential")]
    [InlineData("AzureCliCredential")]
    [InlineData(" visualstudiocredential ")]
    public void Problem_UnsetOrADeveloperSignIn_IsNone(string? selection)
    {
        Assert.Null(OperatorSignIn.Problem(selection));
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("environmentcredential")]
    [InlineData("workloadidentitycredential")]
    [InlineData("managedidentitycredential")]
    [InlineData("ManagedIdentityCredential")]
    [InlineData("azurepipelinescredential")]
    [InlineData("something-new")]
    public void Problem_AMachineOrPipelineSignIn_IsRefused(string selection)
    {
        var problem = OperatorSignIn.Problem(selection);

        Assert.NotNull(problem);
        Assert.Contains(OperatorSignIn.SelectionVariable, problem, StringComparison.Ordinal);
        Assert.Contains("azureclicredential", problem, StringComparison.Ordinal);
    }
}
