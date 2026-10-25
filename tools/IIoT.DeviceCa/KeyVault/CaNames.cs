namespace IIoT.DeviceCa.KeyVault;

/// <summary>The names of the CA's keys and certificates in Key Vault.</summary>
/// <remarks>
/// The key names must match <c>deviceCaKeyNames</c> in <c>infra/main.bicep</c>, which creates the
/// keys. The certificate names are this tool's own: it writes those secrets.
/// </remarks>
public static class CaNames
{
    /// <summary>The root CA's signing key.</summary>
    public const string RootKey = "device-ca-root";

    /// <summary>The issuing CA's signing key.</summary>
    public const string IssuingKey = "device-ca-issuing";

    /// <summary>The root CA certificate, PEM.</summary>
    public const string RootCertificate = "device-ca-root-certificate";

    /// <summary>The issuing CA certificate, PEM. The certificate registered in IoT Hub.</summary>
    public const string IssuingCertificate = "device-ca-issuing-certificate";

    /// <summary>The content type the certificate secrets are stored with.</summary>
    public const string PemContentType = "application/x-pem-file";
}
