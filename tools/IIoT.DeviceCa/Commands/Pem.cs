using System.Security.Cryptography.X509Certificates;

namespace IIoT.DeviceCa.Commands;

/// <summary>Reading and writing the PEM files and secrets the CA exchanges.</summary>
internal static class Pem
{
    /// <summary>Loads the one certificate a CA secret holds.</summary>
    /// <exception cref="InvalidDataException">The text is not exactly one PEM certificate.</exception>
    public static X509Certificate2 LoadCertificate(string pem, string source)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidDataException($"{source} is not a PEM certificate.", ex);
        }

        return certificates.Count == 1
            ? certificates[0]
            : throw new InvalidDataException($"{source} holds {certificates.Count} certificates, expected one.");
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a new file. Never overwrites: an existing file may be a
    /// certificate a device still uses.
    /// </summary>
    /// <exception cref="IOException">The file exists.</exception>
    public static void WriteNewFile(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
