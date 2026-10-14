using ExternalAgent;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
{
    LocalEnvironmentFile.Load(Path.Combine(builder.Environment.ContentRootPath, ".env"));
}

var atlasConfiguration = AtlasConfiguration.FromEnvironment();

builder.Services.AddSingleton(atlasConfiguration);
builder.Services.AddSingleton<IBearerTokenProvider, EnvironmentBearerTokenProvider>();
builder.Services.AddSingleton<IAtlasA2AProtocol, SdkAtlasA2AProtocol>();
builder.Services.AddSingleton<IFoundryChatService, FoundryChatService>();
builder.Services.AddSingleton<AtlasA2AService>();
builder.Services.AddHostedService<AtlasDiscoveryOnStartup>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (AtlasA2AService atlas) => atlas.Diagnostics);
app.MapPost("/api/discover", async (AtlasA2AService atlas, CancellationToken cancellationToken) =>
    Results.Ok(await atlas.DiscoverAsync(cancellationToken)));
app.MapPost("/api/chat", async (ChatRequest request, AtlasA2AService atlas, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 4000)
    {
        return Results.BadRequest(new { error = "Enter a question up to 4,000 characters." });
    }

    return Results.Ok(await atlas.AskAsync(request.Question, cancellationToken));
});

app.Run();

public partial class Program;
