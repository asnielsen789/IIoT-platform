using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IIoT.DeviceCa.Certificates;

/// <summary>
/// A device's certificate signing request, reduced to what the CA takes from it: a verified EC
/// P-256 public key.
/// </summary>
/// <remarks>
/// <para>
/// The device generates its key pair and sends a CSR; the private key never leaves the device
/// (ADR-009). The CSR's signature proves that whoever produced it holds the private key for the
/// public key inside. Nothing else in the request is trusted: the CA sets the subject itself from
/// the device ID it was given, and the extensions from its own profile.
/// </para>
/// </remarks>
public sealed class DeviceCertificateRequest
{
    /// <summary>id-ecPublicKey (RFC 5480).</summary>
    public const string EcPublicKeyOid = "1.2.840.10045.2.1";

    /// <summary>secp256r1, also called prime256v1 or NIST P-256 (RFC 5480).</summary>
    public const string P256CurveOid = "1.2.840.10045.3.1.7";

    private DeviceCertificateRequest(PublicKey publicKey, X500DistinguishedName requestedSubject)
    {
        PublicKey = publicKey;
        RequestedSubject = requestedSubject;
    }

    /// <summary>The device's public key, EC P-256, whose possession the CSR's signature proved.</summary>
    public PublicKey PublicKey { get; }

    /// <summary>The subject the device asked for. Reported, never used.</summary>
    public X500DistinguishedName RequestedSubject { get; }

    /// <summary>Loads a PEM-encoded CSR, verifies its signature and checks the key type.</summary>
    /// <exception cref="InvalidDataException">
    /// The text holds no CSR, its signature does not verify, or its key is not EC P-256.
    /// </exception>
    public static DeviceCertificateRequest LoadPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        CertificateRequest request;
        try
        {
            // The default load options verify the request's self-signature; skipping that check is
            // an explicit option (SkipSignatureValidation) and is never passed. The hash algorithm
            // argument is only what the loaded request would sign with; this one is never signed.
            request = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                "The CSR could not be loaded, or its signature does not verify: it was not signed with " +
                "the key it contains, or it was altered after signing.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("No PEM-encoded certificate signing request was found.", ex);
        }

        RequireP256(request.PublicKey);
        return new DeviceCertificateRequest(request.PublicKey, request.SubjectName);
    }

    private static void RequireP256(PublicKey publicKey)
    {
        // Checked on the encoded algorithm identifier rather than through a platform ECDsa object,
        // whose reported curve names differ between Windows and OpenSSL.
        if (publicKey.Oid.Value != EcPublicKeyOid)
        {
            throw new InvalidDataException(
                $"The CSR's key is {publicKey.Oid.FriendlyName ?? publicKey.Oid.Value}; only EC P-256 is accepted.");
        }

        // A named curve, not explicit parameters: RFC 5480 (section 2.1.1) says the other two forms
        // "MUST NOT be used in PKIX".
        const string NoNamedCurve = "The CSR's EC key does not name its curve; only EC P-256 is accepted.";
        if (publicKey.EncodedParameters is not { } parameters)
            throw new InvalidDataException(NoNamedCurve);

        string curve;
        try
        {
            curve = AsnDecoder.ReadObjectIdentifier(parameters.RawData, AsnEncodingRules.DER, out _);
        }
        catch (AsnContentException ex)
        {
            throw new InvalidDataException(NoNamedCurve, ex);
        }

        if (curve != P256CurveOid)
        {
            var name = new Oid(curve).FriendlyName ?? curve;
            throw new InvalidDataException($"The CSR's key is on curve {name}; only EC P-256 is accepted.");
        }
    }
}
