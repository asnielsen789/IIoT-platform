namespace IIoT.TestClient.Authentication;

/// <summary>
/// Where a simulated device keeps its private key and CSR, and how they are written.
/// </summary>
/// <remarks>
/// <para>
/// The default directory is <c>~/.secrets/iiot</c>, outside the repository, where the README also
/// keeps devices files. A key written inside the repository by mistake is caught by the
/// <c>*.key.pem</c> pattern in <c>.gitignore</c>; that is the guard, not the plan.
/// </para>
/// <para>
/// Files are never overwritten. Replacing a key silently would orphan the certificate issued for
/// the old one, and the old key would be gone.
/// </para>
/// </remarks>
public static class DeviceKeyFiles
{
    /// <summary>The private key file's suffix. Matches the <c>.gitignore</c> guard.</summary>
    public const string KeySuffix = ".key.pem";

    /// <summary>The CSR file's suffix.</summary>
    public const string CsrSuffix = ".csr.pem";

    /// <summary><c>~/.secrets/iiot</c>, in the user's home directory.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".secrets", "iiot");

    /// <summary>
    /// The device ID as a file name: anything but letters, digits, '.', '_' and '-' becomes '_'.
    /// The same rule as <c>IIoT.DeviceCa</c>'s <c>IssueCommand.FileStem</c>, so a device's key, CSR,
    /// certificate and chain files share one stem.
    /// </summary>
    public static string FileStem(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return string.Concat(deviceId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
    }

    /// <summary>Generates a key pair and CSR for <paramref name="deviceId"/> and writes both.</summary>
    /// <param name="deviceId">The device ID.</param>
    /// <param name="directory">Where to write. Created if missing, and on Unix readable by the owner only.</param>
    /// <returns>The two files written.</returns>
    /// <exception cref="IOException">Either file exists already. Nothing was written.</exception>
    public static WrittenKeyFiles Create(string deviceId, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var keyPath = Path.Combine(directory, FileStem(deviceId) + KeySuffix);
        var csrPath = Path.Combine(directory, FileStem(deviceId) + CsrSuffix);

        foreach (var path in new[] { keyPath, csrPath })
        {
            if (File.Exists(path))
            {
                throw new IOException(
                    $"{path} exists; nothing was written. A device keeps its key: move the old files away " +
                    "only if this device is really meant to get a new key and certificate.");
            }
        }

        var csr = DeviceCsr.Create(deviceId);

        if (OperatingSystem.IsWindows())
        {
            // The user profile's ACL already keeps other users out.
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        WriteNew(keyPath, csr.PrivateKeyPem, secret: true);
        WriteNew(csrPath, csr.CsrPem, secret: false);

        return new WrittenKeyFiles(keyPath, csrPath);
    }

    private static void WriteNew(string path, string content, bool secret)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        // Owner read and write only, set as the file is created rather than after, so the key is
        // never readable by others even for a moment.
        if (secret && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Write('\n');
    }
}

/// <summary>What <see cref="DeviceKeyFiles.Create"/> wrote.</summary>
/// <param name="KeyPath">The private key. Stays with the device.</param>
/// <param name="CsrPath">The CSR, for <c>IIoT.DeviceCa issue --csr</c>.</param>
public sealed record WrittenKeyFiles(string KeyPath, string CsrPath);
