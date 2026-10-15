using IIoT.Core.Authentication;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>Hands out a fixed token and records what the forwarder did with it.</summary>
internal sealed class RecordingTokenProvider : IAccessTokenProvider
{
    private readonly List<string> _invalidated = [];

    public RecordingTokenProvider(string token)
    {
        Token = token;
    }

    public string Token { get; }

    /// <summary>When set, every request for a token fails with this.</summary>
    public Exception? FailWith { get; set; }

    public int Requests { get; private set; }

    public IReadOnlyList<string> Invalidated => _invalidated;

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        Requests++;
        return FailWith is null
            ? ValueTask.FromResult(Token)
            : ValueTask.FromException<string>(FailWith);
    }

    public void Invalidate(string accessToken) => _invalidated.Add(accessToken);
}
