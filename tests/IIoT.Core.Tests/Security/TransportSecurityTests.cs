using IIoT.Core.Security;

namespace IIoT.Core.Tests.Security;

public class TransportSecurityTests
{
    [Theory]
    [InlineData("https://gateway.example.com/")]
    [InlineData("https://gateway.example.com:8443/api/")]
    [InlineData("HTTPS://GATEWAY.EXAMPLE.COM/")]
    [InlineData("https://10.0.0.5/")]
    public void Https_IsAllowed(string uri)
    {
        Assert.True(TransportSecurity.IsAllowed(new Uri(uri)));
        Assert.Null(TransportSecurity.DescribeViolation(new Uri(uri), "Setting"));
    }

    /// <summary>Loopback traffic never leaves the machine; in-process tests depend on it.</summary>
    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LocalHost:5000/v1/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.10.20.30:8080/")]
    [InlineData("http://[::1]/")]
    public void PlainHttpToLoopback_IsAllowed(string uri)
    {
        Assert.True(TransportSecurity.IsAllowed(new Uri(uri)));
    }

    /// <summary>
    /// Plain HTTP anywhere else would put a client secret or a bearer token on the wire in clear
    /// text — including names built to look local.
    /// </summary>
    [Theory]
    [InlineData("http://gateway.example.com/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.10:8080/")]
    [InlineData("http://localhost.example.com/")]
    [InlineData("http://localhost@gateway.example.com/")]
    [InlineData("http://127.0.0.1.nip.io/")]
    [InlineData("http://[::2]/")]
    [InlineData("ftp://localhost/")]
    [InlineData("ws://localhost/")]
    public void EverythingElse_IsRefused(string uri)
    {
        Assert.False(TransportSecurity.IsAllowed(new Uri(uri)));
        Assert.Contains("must use https", TransportSecurity.DescribeViolation(new Uri(uri), "Setting"), StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeUri_IsRefused()
    {
        var relative = new Uri("/v1/readings", UriKind.Relative);

        Assert.False(TransportSecurity.IsAllowed(relative));
        Assert.Equal("Setting must be an absolute URL", TransportSecurity.DescribeViolation(relative, "Setting"));
    }

    [Fact]
    public void Missing_IsDescribedAsNotConfigured()
    {
        Assert.Equal("Setting is not configured", TransportSecurity.DescribeViolation(null, "Setting"));
    }

    /// <summary>The reason ends up in a log line, so it names the setting and the offending host.</summary>
    [Fact]
    public void Violation_NamesTheSettingAndTheHost()
    {
        var reason = TransportSecurity.DescribeViolation(new Uri("http://gateway.example.com:8080/x"), "GatewayV1:BaseAddress");

        Assert.Equal(
            "GatewayV1:BaseAddress must use https; refusing to send to http://gateway.example.com:8080 " +
            "because plain HTTP is allowed only to a loopback address",
            reason);
    }
}
