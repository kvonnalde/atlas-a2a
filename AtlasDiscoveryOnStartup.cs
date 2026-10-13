namespace ExternalAgent;

public sealed class AtlasDiscoveryOnStartup(AtlasA2AService atlas) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await atlas.DiscoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
