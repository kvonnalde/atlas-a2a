namespace ExternalAgent;

public sealed record AtlasConfiguration(string? AgentCardUrl, bool AllowHttpLocalhost)
{
    public FoundryChatConfiguration Foundry { get; init; } = new(null, null, null);

    public static AtlasConfiguration FromEnvironment() => new(
        ReadEnvironment("ATLAS_AGENT_CARD_URL"),
        string.Equals(ReadEnvironment("ATLAS_ALLOW_HTTP_LOCALHOST"), "true", StringComparison.OrdinalIgnoreCase))
    {
        Foundry = new FoundryChatConfiguration(
            ReadEnvironment("FOUNDRY_ENDPOINT") ?? ReadEnvironment("FOUNDRY_CHAT_COMPLETIONS_URL"),
            ReadEnvironment("FOUNDRY_MODEL"),
            ReadEnvironment("FOUNDRY_API_KEY"))
    };

    private static string? ReadEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
