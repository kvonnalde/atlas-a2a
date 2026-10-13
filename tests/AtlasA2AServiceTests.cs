using System.Net;
using A2A;
using ExternalAgent;
using Xunit;

namespace ExternalAgent.Tests;

public sealed class AtlasA2AServiceTests
{
    private const string CardUrl = "https://atlas.example.invalid/.well-known/agent-card.json";

    [Fact]
    public async Task MissingConfigurationIsReportedWithoutCallingAtlas()
    {
        var protocol = new FakeProtocol();
        var service = CreateService(new AtlasConfiguration(null, false), protocol);

        var diagnostics = await service.DiscoverAsync(CancellationToken.None);

        Assert.Equal("MissingConfiguration", diagnostics.State);
        Assert.Contains("ATLAS_AGENT_CARD_URL", diagnostics.Message);
        Assert.Equal(0, protocol.DiscoveryCalls);
    }

    [Fact]
    public async Task MissingAgentCardReportsThatAtlasIsNotA2AEnabled()
    {
        var protocol = new FakeProtocol
        {
            DiscoveryException = new HttpRequestException("upstream detail", null, HttpStatusCode.NotFound)
        };
        var service = CreateService(new AtlasConfiguration(CardUrl, false), protocol);

        var diagnostics = await service.DiscoverAsync(CancellationToken.None);

        Assert.Equal("NotA2AEnabled", diagnostics.State);
        Assert.Contains("Atlas is not A2A-enabled at this endpoint", diagnostics.Message);
        Assert.DoesNotContain("upstream detail", diagnostics.Message);
    }

    [Fact]
    public async Task AgentCardUrlCannotCarryQueryCredentials()
    {
        var protocol = new FakeProtocol();
        var service = CreateService(new AtlasConfiguration($"{CardUrl}?access_token=secret-value", false), protocol);

        var diagnostics = await service.DiscoverAsync(CancellationToken.None);

        Assert.Equal("ConfigurationError", diagnostics.State);
        Assert.Contains("ATLAS_A2A_BEARER_TOKEN", diagnostics.Message);
        Assert.DoesNotContain("secret-value", diagnostics.Message);
        Assert.Equal(0, protocol.DiscoveryCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized", "401 Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden", "403 Forbidden")]
    public async Task UnauthorizedA2ARequestIsReportedWithoutLeakingFailureDetails(
        HttpStatusCode statusCode,
        string expectedState,
        string expectedMessage)
    {
        var protocol = new FakeProtocol
        {
            SendException = new HttpRequestException("sensitive response detail", null, statusCode)
        };
        var service = CreateService(new AtlasConfiguration(CardUrl, false), protocol);
        await service.DiscoverAsync(CancellationToken.None);

        var result = await service.AskAsync("Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedState, result.Diagnostics.State);
        Assert.Contains(expectedMessage, result.Error);
        Assert.DoesNotContain("sensitive response detail", result.Error);
    }

    [Fact]
    public async Task ImmediateMessageTextIsReturned()
    {
        var protocol = new FakeProtocol
        {
            SendResponse = new SendMessageResponse
            {
                Message = new Message
                {
                    MessageId = "reply-1",
                    Role = Role.Agent,
                    Parts = [Part.FromText("Atlas says ready.")]
                }
            }
        };
        var service = CreateService(new AtlasConfiguration(CardUrl, false), protocol);
        await service.DiscoverAsync(CancellationToken.None);

        var result = await service.AskAsync("Are you ready?", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Atlas says ready.", result.Answer);
        Assert.Equal("Are you ready?", protocol.LastMessage?.Parts[0].Text);
        Assert.Equal("Ready", result.Diagnostics.State);
        Assert.NotNull(result.Diagnostics.CorrelationId);
        Assert.NotNull(result.Diagnostics.LatencyMs);
    }

    [Fact]
    public async Task TaskResponseIsRetrievedUntilTerminalAndReturnsTextArtifacts()
    {
        var protocol = new FakeProtocol
        {
            SendResponse = new SendMessageResponse { Task = CreateTask("task-7", TaskState.Submitted) },
            TaskResponses = new Queue<AgentTask>([
                CreateTask("task-7", TaskState.Working),
                CreateTask("task-7", TaskState.Completed, "The task result.")
            ])
        };
        var service = CreateService(new AtlasConfiguration(CardUrl, false), protocol, pollInterval: TimeSpan.FromMilliseconds(1));
        await service.DiscoverAsync(CancellationToken.None);

        var result = await service.AskAsync("Run a task", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Completed", result.TaskStatus);
        Assert.Equal("task-7", result.Diagnostics.TaskId);
        Assert.Equal(2, protocol.GetTaskCalls);
        Assert.Equal("The task result.", Assert.Single(result.Artifacts));
    }

    [Fact]
    public async Task RequestTimeoutCancelsInFlightCall()
    {
        var protocol = new FakeProtocol
        {
            SendHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new SendMessageResponse();
            }
        };
        var service = CreateService(
            new AtlasConfiguration(CardUrl, false),
            protocol,
            requestTimeout: TimeSpan.FromMilliseconds(30));
        await service.DiscoverAsync(CancellationToken.None);

        var result = await service.AskAsync("Wait", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Timeout", result.Diagnostics.State);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedA2AResponseIsSanitized()
    {
        var protocol = new FakeProtocol { SendResponse = new SendMessageResponse() };
        var service = CreateService(new AtlasConfiguration(CardUrl, false), protocol);
        await service.DiscoverAsync(CancellationToken.None);

        var result = await service.AskAsync("Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("MalformedResponse", result.Diagnostics.State);
        Assert.Contains("malformed or unsupported", result.Error);
    }

    private static AtlasA2AService CreateService(
        AtlasConfiguration configuration,
        FakeProtocol protocol,
        TimeSpan? requestTimeout = null,
        TimeSpan? pollInterval = null) =>
        new(configuration, protocol, new FakeTokenProvider(), requestTimeout, TimeSpan.FromMilliseconds(100), pollInterval);

    private static AgentTask CreateTask(string id, TaskState state, string? artifactText = null) => new()
    {
        Id = id,
        Status = new A2A.TaskStatus { State = state },
        Artifacts = artifactText is null
            ? []
            : [new Artifact { ArtifactId = "artifact-1", Parts = [Part.FromText(artifactText)] }]
    };

    private sealed class FakeTokenProvider : IBearerTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }

    private sealed class FakeProtocol : IAtlasA2AProtocol
    {
        public Exception? DiscoveryException { get; init; }
        public Exception? SendException { get; init; }
        public SendMessageResponse SendResponse { get; init; } = new()
        {
            Message = new Message { MessageId = "default", Role = Role.Agent, Parts = [Part.FromText("default")] }
        };
        public Queue<AgentTask> TaskResponses { get; init; } = new();
        public Func<Message, CancellationToken, Task<SendMessageResponse>>? SendHandler { get; init; }
        public Message? LastMessage { get; private set; }
        public int DiscoveryCalls { get; private set; }
        public int GetTaskCalls { get; private set; }

        public Task<AgentCard> ResolveAgentCardAsync(Uri agentCardUrl, string? bearerToken, CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            if (DiscoveryException is not null) throw DiscoveryException;
            return Task.FromResult(new AgentCard
            {
                Name = "Atlas Agent",
                Version = "2.1",
                SupportedInterfaces = [new AgentInterface { Url = "https://atlas.example.invalid/a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                Skills = [new AgentSkill { Id = "answer", Name = "Answer questions", Description = "Responds to questions", Tags = ["chat"] }]
            });
        }

        public async Task<SendMessageResponse> SendMessageAsync(Uri endpoint, Message message, string? bearerToken, CancellationToken cancellationToken)
        {
            LastMessage = message;
            if (SendException is not null) throw SendException;
            if (SendHandler is not null) return await SendHandler(message, cancellationToken);
            return SendResponse;
        }

        public Task<AgentTask> GetTaskAsync(Uri endpoint, string taskId, string? bearerToken, CancellationToken cancellationToken)
        {
            GetTaskCalls++;
            return Task.FromResult(TaskResponses.Dequeue());
        }
    }
}
