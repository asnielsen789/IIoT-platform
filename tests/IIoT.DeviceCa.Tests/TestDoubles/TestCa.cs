using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>A complete CA with local keys: root and issuing key and certificate.</summary>
public sealed class TestCa : IDisposable
{
    public TestCa(DateTimeOffset now)
    {
        RootKey = new LocalSigner("test-root");
        IssuingKey = new LocalSigner("test-issuing");
        Root = CertificateAuthority.CreateRoot(RootKey, now);
        Issuing = CertificateAuthority.CreateIssuing(IssuingKey, Root, RootKey, now);
    }

    public LocalSigner RootKey { get; }

    public LocalSigner IssuingKey { get; }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Issuing { get; }

    public void Dispose()
    {
        Root.Dispose();
        Issuing.Dispose();
        RootKey.Dispose();
        IssuingKey.Dispose();
    }
}
