using System.Formats.Asn1;
using System.Security.Cryptography;

namespace IIoT.DeviceCa.Certificates;

/// <summary>
/// Converts an ECDSA signature from IEEE P1363 (<c>r || s</c>, what Key Vault returns) to the DER
/// <c>ECDSA-Sig-Value</c> an X.509 certificate carries.
/// </summary>
/// <remarks>
/// <para>
/// Key Vault's ES256 follows JWS (RFC 7518, section 3.4): the two integers r and s as fixed-width,
/// unsigned big-endian values, concatenated. RFC 5480 (section 2.2.3, by way of RFC 3279) requires
/// <c>ECDSA-Sig-Value ::= SEQUENCE { r INTEGER, s INTEGER }</c> in a certificate's signature.
/// </para>
/// <para>
/// The two differ in exactly the details that break signatures intermittently. A DER INTEGER is
/// signed and minimal: leading zero bytes must be removed, and a value whose first byte has the
/// high bit set needs one zero byte in front so it is not read as negative. The high bit is set in
/// r or s in about three signatures in four, and a leading zero byte appears in about one in 128,
/// so a conversion that gets either wrong produces certificates that fail to verify only some of
/// the time.
/// </para>
/// </remarks>
public static class EcdsaSignature
{
    /// <summary>The width of r and of s for P-256, in bytes.</summary>
    public const int P256FieldSize = 32;

    /// <summary>Converts a P-256 signature from P1363 to DER.</summary>
    /// <exception cref="CryptographicException">
    /// The input is not 64 bytes, or r or s is zero, which no valid signature contains.
    /// </exception>
    public static byte[] P1363ToDer(ReadOnlySpan<byte> p1363)
    {
        if (p1363.Length != 2 * P256FieldSize)
        {
            throw new CryptographicException(
                $"Expected a {2 * P256FieldSize}-byte P-256 signature (r || s), got {p1363.Length} bytes.");
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            WriteUnsignedInteger(writer, p1363[..P256FieldSize]);
            WriteUnsignedInteger(writer, p1363[P256FieldSize..]);
        }

        return writer.Encode();
    }

    private static void WriteUnsignedInteger(AsnWriter writer, ReadOnlySpan<byte> value)
    {
        // Minimal encoding first: AsnWriter rejects a redundant leading zero rather than removing
        // it. WriteIntegerUnsigned then adds the one zero byte a high first bit needs.
        var significant = value.TrimStart((byte)0);
        if (significant.IsEmpty)
            throw new CryptographicException("An ECDSA signature value is zero, which is never valid.");

        writer.WriteIntegerUnsigned(significant);
    }
}
