using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Commands;
using IIoT.DeviceCa.KeyVault;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Commands;

public sealed class CaCertificateCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeCaVault _vault = new();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task Run_WithoutAFile_WritesOnlyTheIssuingCertificatePem()
    {
        await new InitCommand(_vault, new FixedTime(Now), TextWriter.Null).RunAsync(false, CancellationToken.None);
        var output = new StringWriter();

        await new CaCertificateCommand(_vault, output).RunAsync(outputFile: null, CancellationToken.None);

        // Exactly one certificate and nothing else, so the output can be redirected to a file.
        var text = output.ToString();
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", text, StringComparison.Ordinal);
        using var certificate = X509Certificate2.CreateFromPem(text);
        Assert.Equal(CertificateAuthority.IssuingSubject, certificate.Subject);
        Assert.Equal(1, CountOccurrences(text, "BEGIN CERTIFICATE"));
    }

    [Fact]
    public async Task Run_WithAFile_WritesTheIssuingCertificateThere()
    {
        await new InitCommand(_vault, new FixedTime(Now), TextWriter.Null).RunAsync(false, CancellationToken.None);
        var directory = Directory.CreateTempSubdirectory("iiot-deviceca-").FullName;
        try
        {
            var file = Path.Combine(directory, "device-ca-issuing.pem");

            await new CaCertificateCommand(_vault, TextWriter.Null).RunAsync(file, CancellationToken.None);

            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            using var stored = X509Certificate2.CreateFromPem(_vault.Secrets[CaNames.IssuingCertificate]);
            Assert.Equal(stored.Thumbprint, certificate.Thumbprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Run_BeforeInit_SaysSo()
    {
        var exception = await Assert.ThrowsAsync<CaRefusedException>(
            () => new CaCertificateCommand(_vault, TextWriter.Null).RunAsync(null, CancellationToken.None));

        Assert.Contains("init", exception.Message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
