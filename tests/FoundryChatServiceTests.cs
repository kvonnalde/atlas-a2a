using ExternalAgent;
using Xunit;

namespace ExternalAgent.Tests;

public sealed class FoundryChatServiceTests
{
    [Fact]
    public void FoundryTargetUriMustUseHttps()
    {
        var configuration = new AtlasConfiguration(null, false)
        {
            Foundry = new FoundryChatConfiguration("http://foundry.example.test", "gpt-5.4", "test-key")
        };

        Assert.Throws<ArgumentException>(() => new FoundryChatService(configuration));
    }

    [Fact]
    public void FoundryResponsesEndpointCanBeNormalizedToTargetUri()
    {
        var configuration = new AtlasConfiguration(null, false)
        {
            Foundry = new FoundryChatConfiguration(
                "https://foundry.example.test/openai/responses?api-version=2025-04-01-preview",
                "gpt-5.4",
                "test-key")
        };

        var service = new FoundryChatService(configuration);

        Assert.True(service.IsConfigured);
    }
}