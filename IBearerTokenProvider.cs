namespace ExternalAgent;

public interface IBearerTokenProvider
{
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class EnvironmentBearerTokenProvider : IBearerTokenProvider
{
    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Environment.GetEnvironmentVariable("ATLAS_A2A_BEARER_TOKEN");
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(token) ? null : token.Trim());
    }
}
