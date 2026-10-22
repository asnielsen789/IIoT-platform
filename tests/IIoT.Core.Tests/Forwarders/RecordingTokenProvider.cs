using IIoT.Core.Authentication;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// Hands out tokens the way a caching provider does — the same one until it is invalidated, then
/// the next — and records what the forwarder did with them.
/// </summary>
/// <remarks>
/// With no next token left, an invalidated token keeps being handed out, as a provider would whose
/// token endpoint returned the same token again.
/// </remarks>
internal sealed class RecordingTokenProvider : IAccessTokenProvider
{
    private readonly Queue<string> _next;
    private readonly List<string> _invalidated = [];

    public RecordingTokenProvider(string token, params string[] next)
    {
        Current = token;
        _next = new Queue<string>(next);
    }

    /// <summary>The token the next request is given.</summary>
    public string Current { get; private set; }

    /// <summary>When set, every request for a token fails with this.</summary>
    public Exception? FailWith { get; set; }

    public int Requests { get; private set; }

    public IReadOnlyList<string> Invalidated => _invalidated;

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        Requests++;
        return FailWith is null
            ? ValueTask.FromResult(Current)
            : ValueTask.FromException<string>(FailWith);
    }

    public void Invalidate(string accessToken)
    {
        _invalidated.Add(accessToken);

        // Like the real provider, only the cached token is discarded; a stale one changes nothing.
        if (accessToken == Current && _next.Count > 0)
        {
            Current = _next.Dequeue();
        }
    }
}
