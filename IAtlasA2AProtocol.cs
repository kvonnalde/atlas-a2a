using A2A;

namespace ExternalAgent;

public interface IAtlasA2AProtocol
{
    Task<AgentCard> ResolveAgentCardAsync(Uri agentCardUrl, string? bearerToken, CancellationToken cancellationToken);
    Task<SendMessageResponse> SendMessageAsync(Uri endpoint, Message message, string? bearerToken, CancellationToken cancellationToken);
    Task<AgentTask> GetTaskAsync(Uri endpoint, string taskId, string? bearerToken, CancellationToken cancellationToken);
}
