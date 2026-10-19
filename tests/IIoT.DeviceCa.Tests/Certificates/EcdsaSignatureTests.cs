using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>
/// The P1363-to-DER conversion every CA signature goes through. Its two traps — a high first bit
/// and leading zero bytes — each occur in only some signatures, so they are pinned with constructed
/// values rather than left to chance.
/// </summary>
public class EcdsaSignatureTests
{
    [Fact]
    public void P1363ToDer_PlainValues_AreTwoIntegersInASequence()
    {
        var r = Filled(0x11);
        var s = Filled(0x22);

        var der = EcdsaSignature.P1363ToDer([.. r, .. s]);

        // SEQUENCE (0x30) of 2 + 32 + 2 + 32 bytes, each INTEGER (0x02) 32 bytes long.
        Assert.Equal([0x30, 0x44, 0x02, 0x20, .. r, 0x02, 0x20, .. s], der);
    }

    [Fact]
    public void P1363ToDer_AHighFirstBit_GainsAZeroByte()
    {
        // 0x80... read as a signed DER integer would be negative.
        var r = Filled(0x80);
        var s = Filled(0xFF);

        var der = EcdsaSignature.P1363ToDer([.. r, .. s]);

        Assert.Equal([0x30, 0x46, 0x02, 0x21, 0x00, .. r, 0x02, 0x21, 0x00, .. s], der);
    }

    [Fact]
    public void P1363ToDer_LeadingZeroBytes_AreRemoved()
    {
        // r = 0x00 0x00 0x7F 0x01 ..., s = 1: DER requires the minimal encoding.
        var r = Filled(0x01);
        r[0] = 0x00;
        r[1] = 0x00;
        r[2] = 0x7F;
        var s = new byte[EcdsaSignature.P256FieldSize];
        s[^1] = 0x01;

        var der = EcdsaSignature.P1363ToDer([.. r, .. s]);

        Assert.Equal([0x30, 0x23, 0x02, 0x1E, .. r[2..], 0x02, 0x01, 0x01], der);
    }

    [Fact]
    public void P1363ToDer_ALeadingZeroBeforeAHighBit_KeepsExactlyOneZero()
    {
        // 0x00 0x80 ...: the zero byte is removed as redundant, then one is needed again for the sign.
        var r = Filled(0x80);
        r[0] = 0x00;
        var s = Filled(0x01);

        var der = EcdsaSignature.P1363ToDer([.. r, .. s]);

        Assert.Equal([0x30, 0x44, 0x02, 0x20, 0x00, .. r[1..], 0x02, 0x20, .. s], der);
    }

    [Fact]
    public void P1363ToDer_RealSignatures_VerifyAsDer()
    {
        // Enough signatures that both traps occur with near certainty (a leading zero byte in r or
        // s about once in 128), on top of the constructed cases above.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sawHighBit = false;

        for (var i = 0; i < 512; i++)
        {
            var digest = SHA256.HashData(BitConverter.GetBytes(i));
            var p1363 = key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var der = EcdsaSignature.P1363ToDer(p1363);

            Assert.True(key.VerifyHash(digest, der, DSASignatureFormat.Rfc3279DerSequence), $"signature {i}");
            var (r, s) = Decode(der);
            Assert.Equal(Unsigned(p1363[..32]), r);
            Assert.Equal(Unsigned(p1363[32..]), s);
            sawHighBit |= p1363[0] >= 0x80 || p1363[32] >= 0x80;
        }

        Assert.True(sawHighBit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(96)]
    public void P1363ToDer_TheWrongLength_IsRejected(int length)
    {
        // 96 bytes would be a P-384 signature: the CA keys are P-256 only.
        Assert.Throws<CryptographicException>(() => EcdsaSignature.P1363ToDer(new byte[length]));
    }

    [Fact]
    public void P1363ToDer_AZeroValue_IsRejected()
    {
        var p1363 = new byte[2 * EcdsaSignature.P256FieldSize];
        p1363[^1] = 0x01;

        Assert.Throws<CryptographicException>(() => EcdsaSignature.P1363ToDer(p1363));
    }

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, EcdsaSignature.P256FieldSize).ToArray();

    private static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static (BigInteger R, BigInteger S) Decode(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadInteger();
        var s = sequence.ReadInteger();
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();
        return (r, s);
    }
}
