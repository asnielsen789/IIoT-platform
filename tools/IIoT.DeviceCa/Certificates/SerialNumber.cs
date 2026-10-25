using System.Security.Cryptography;

namespace IIoT.DeviceCa.Certificates;

/// <summary>Certificate serial numbers: random, positive, and 16 bytes long.</summary>
/// <remarks>
/// RFC 5280 (section 4.1.2.2) requires a positive integer of at most 20 octets, unique per issuer.
/// A random serial needs no counter to keep across runs or operators. The first byte has its high
/// bit cleared, so the integer is positive, and its next bit set, so it never starts with a zero
/// byte: every serial is exactly 16 bytes in DER, with 126 random bits.
/// </remarks>
public static class SerialNumber
{
    /// <summary>The serial's length in bytes.</summary>
    public const int Length = 16;

    /// <summary>Creates a new random serial number, big-endian.</summary>
    public static byte[] Create()
    {
        var serial = RandomNumberGenerator.GetBytes(Length);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
        return serial;
    }
}
