namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// Gateway contract v2's schema against its own examples (ADR-011): every <c>valid-</c> example is
/// accepted and every <c>invalid-</c> one rejected with a reason. The forwarder's v2 bodies and the
/// mock's <c>/v2</c> are tested against the same files.
/// </summary>
public class GatewayV2ContractTests
{
    [Fact]
    public void SchemaAndExamples_AreCopiedToTheOutputDirectory()
    {
        Assert.True(File.Exists(GatewayV2Schema.FilePath), $"the v2 schema was not copied to '{GatewayV2Schema.FilePath}'");
        Assert.True(GatewayV2Schema.Valid().Count() >= 5, "valid v2 examples were not copied to the output directory");
        Assert.True(GatewayV2Schema.Invalid().Count() >= 5, "invalid v2 examples were not copied to the output directory");
    }

    [Theory]
    [MemberData(nameof(GatewayV2Schema.Valid), MemberType = typeof(GatewayV2Schema))]
    public void ValidExample_IsAccepted(string fileName)
    {
        var errors = GatewayV2Schema.Errors(GatewayV2Schema.Read(fileName));

        Assert.True(errors.Count == 0, $"{fileName} should be valid: {string.Join("; ", errors)}");
    }

    [Theory]
    [MemberData(nameof(GatewayV2Schema.Invalid), MemberType = typeof(GatewayV2Schema))]
    public void InvalidExample_IsRejected(string fileName)
    {
        Assert.NotEmpty(GatewayV2Schema.Errors(GatewayV2Schema.Read(fileName)));
    }
}
