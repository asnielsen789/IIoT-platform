namespace IIoT.Core.Authentication;

/// <summary>
/// Supplies the bearer token for calls to a downstream system.
/// </summary>
/// <remarks>
/// A seam rather than a detail: the forwarder depends on this, not on any identity provider, so
/// tests can put a local token issuer behind it and the provider can change without the
/// forwarder noticing.
/// </remarks>
public interface IAccessTokenProvider
{
    /// <summary>
    /// A token that is valid now and for a while yet — from cache while it is fresh, otherwise
    /// newly obtained.
    /// </summary>
    /// <exception cref="AccessTokenException">No token could be obtained.</exception>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards <paramref name="accessToken"/> if it is the one being cached, so the next call
    /// obtains a new one. For when a receiver has rejected it — revoked, or signed with a key
    /// the provider has since rotated — before its expiry said it would be.
    /// </summary>
    void Invalidate(string accessToken);
}
