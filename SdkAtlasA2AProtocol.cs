using System.Net.Http.Headers;
using A2A;

namespace ExternalAgent;

public sealed class SdkAtlasA2AProtocol : IAtlasA2AProtocol
{
    public async Task<AgentCard> ResolveAgentCardAsync(Uri agentCardUrl, string? bearerToken, CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(bearerToken);
        var origin = new Uri($"{agentCardUrl.GetLeftPart(UriPartial.Authority)}/");
        var resolver = new A2ACardResolver(origin, httpClient, agentCardUrl.PathAndQuery);
        return await resolver.GetAgentCardAsync(cancellationToken);
    }

    public async Task<SendMessageResponse> SendMessageAsync(Uri endpoint, Message message, string? bearerToken, CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(bearerToken);
        using var client = new A2AClient(endpoint, httpClient);
        return await client.SendMessageAsync(new SendMessageRequest { Message = message }, cancellationToken);
    }

    public async Task<AgentTask> GetTaskAsync(Uri endpoint, string taskId, string? bearerToken, CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(bearerToken);
        using var client = new A2AClient(endpoint, httpClient);
        return await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
    }

    private static HttpClient CreateHttpClient(string? bearerToken)
    {
        var httpClient = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return httpClient;
    }
}
