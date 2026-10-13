namespace ExternalAgent;

public sealed record AtlasConfiguration(string? AgentCardUrl, bool AllowHttpLocalhost)
{
    public static AtlasConfiguration FromEnvironment() => new(
        Environment.GetEnvironmentVariable("ATLAS_AGENT_CARD_URL"),
        string.Equals(Environment.GetEnvironmentVariable("ATLAS_ALLOW_HTTP_LOCALHOST"), "true", StringComparison.OrdinalIgnoreCase));
}
