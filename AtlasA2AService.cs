using System.Diagnostics;
using System.Net;
using System.Text.Json;
using A2A;

namespace ExternalAgent;

public sealed class AtlasA2AService(
    AtlasConfiguration configuration,
    IAtlasA2AProtocol protocol,
    IBearerTokenProvider tokenProvider,
    TimeSpan? requestTimeout = null,
    TimeSpan? discoveryTimeout = null,
    TimeSpan? pollInterval = null)
{
    private const string NotA2AMessage = "Atlas is not A2A-enabled at this endpoint. Atlas must expose an authenticated A2A endpoint first.";
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private AtlasDiagnostics _diagnostics = AtlasDiagnostics.Empty;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    public AtlasDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    public async Task<AtlasDiagnostics> DiscoverAsync(CancellationToken cancellationToken)
    {
        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(configuration.AgentCardUrl))
            {
                return SetDiagnostics(AtlasDiagnostics.Empty with
                {
                    State = "MissingConfiguration",
                    Message = "Set ATLAS_AGENT_CARD_URL to the Atlas Agent Card URL."
                });
            }

            string? urlFailure = null;
            if (!Uri.TryCreate(configuration.AgentCardUrl, UriKind.Absolute, out var cardUrl) ||
                !TryValidateUri(cardUrl, out urlFailure))
            {
                return SetDiagnostics(AtlasDiagnostics.Empty with
                {
                    State = "ConfigurationError",
                    Message = urlFailure ?? "ATLAS_AGENT_CARD_URL must be an absolute HTTPS URL. HTTP is allowed only for explicitly enabled localhost development."
                });
            }

            SetDiagnostics(AtlasDiagnostics.Empty with { State = "Discovering", Message = "Resolving the Atlas Agent Card." });
            using var timeout = new CancellationTokenSource(discoveryTimeout ?? DiscoveryTimeout);
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                var token = await tokenProvider.GetTokenAsync(linkedToken.Token);
                var card = await protocol.ResolveAgentCardAsync(cardUrl, token, linkedToken.Token);
                var agentInterface = card.SupportedInterfaces?.FirstOrDefault(candidate =>
                    string.Equals(candidate.ProtocolBinding, "JSONRPC", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.ProtocolVersion, "1.0", StringComparison.Ordinal));

                if (agentInterface is null || !Uri.TryCreate(agentInterface.Url, UriKind.Absolute, out var endpoint))
                {
                    return SetNotA2A(cardUrl);
                }

                if (!TryValidateUri(endpoint, out var endpointFailure))
                {
                    return SetDiagnostics(new AtlasDiagnostics
                    {
                        State = "ConfigurationError",
                        Message = endpointFailure ?? "The advertised A2A endpoint URL is not allowed.",
                        AgentCardUrl = SanitizeUri(cardUrl),
                        AgentName = card.Name,
                        AgentVersion = card.Version,
                        Skills = MapSkills(card.Skills),
                        ProtocolVersion = agentInterface.ProtocolVersion
                    });
                }

                Volatile.Write(ref _resolvedEndpoint, endpoint.AbsoluteUri);
                return SetDiagnostics(new AtlasDiagnostics
                {
                    State = "Ready",
                    Message = "Atlas Agent Card resolved. A2A request readiness is not verified until a message is accepted.",
                    AgentCardUrl = SanitizeUri(cardUrl),
                    AgentName = card.Name,
                    AgentVersion = card.Version,
                    Skills = MapSkills(card.Skills),
                    Endpoint = SanitizeUri(endpoint),
                    ProtocolVersion = agentInterface.ProtocolVersion
                });
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return SetDiagnostics(AtlasDiagnostics.Empty with
                {
                    State = "Timeout",
                    AgentCardUrl = SanitizeUri(cardUrl),
                    Message = "Atlas Agent Card discovery timed out."
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return DiscoveryFailure(cardUrl, exception);
            }
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    public async Task<ChatResult> AskAsync(string question, CancellationToken cancellationToken)
    {
        var discovery = Diagnostics;
        if (discovery.State != "Ready" || !Uri.TryCreate(configuration.AgentCardUrl, UriKind.Absolute, out _) ||
            string.IsNullOrWhiteSpace(discovery.Endpoint))
        {
            return new ChatResult(false, null, null, [], discovery.Message, discovery);
        }

        var endpoint = FindDiscoveredEndpoint();
        if (endpoint is null)
        {
            return new ChatResult(false, null, null, [], NotA2AMessage, SetNotA2A(null));
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        string? taskId = null;
        using var timeout = new CancellationTokenSource(requestTimeout ?? RequestTimeout);
        using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            var token = await tokenProvider.GetTokenAsync(linkedToken.Token);
            var message = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.User,
                Parts = [Part.FromText(question)]
            };
            var response = await protocol.SendMessageAsync(endpoint, message, token, linkedToken.Token);

            if (response.PayloadCase == SendMessageResponseCase.Message && response.Message is not null)
            {
                var answer = string.Join(Environment.NewLine, ExtractText(response.Message.Parts));
                stopwatch.Stop();
                var updated = SetRequestDiagnostics("Ready", "A2A message completed.", correlationId, null, stopwatch.ElapsedMilliseconds);
                return new ChatResult(true, answer, null, [], null, updated);
            }

            if (response.PayloadCase == SendMessageResponseCase.Task && response.Task is not null)
            {
                var task = response.Task;
                taskId = task.Id;
                task = await protocol.GetTaskAsync(endpoint, taskId, token, linkedToken.Token);
                while (!task.Status.State.IsTerminal())
                {
                    await Task.Delay(pollInterval ?? PollInterval, linkedToken.Token);
                    task = await protocol.GetTaskAsync(endpoint, taskId, token, linkedToken.Token);
                }

                var artifacts = task.Artifacts?
                    .SelectMany(artifact => ExtractText(artifact.Parts))
                    .ToArray() ?? [];
                stopwatch.Stop();
                var updated = SetRequestDiagnostics("Ready", "A2A task completed.", correlationId, taskId, stopwatch.ElapsedMilliseconds);
                return new ChatResult(true, null, task.Status.State.ToString(), artifacts, null, updated);
            }

            throw new A2AException("Unsupported A2A response payload.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var updated = SetRequestDiagnostics("Timeout", "The Atlas A2A request timed out.", correlationId, taskId, stopwatch.ElapsedMilliseconds);
            return new ChatResult(false, null, null, [], updated.Message, updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var statusCode = FindHttpStatusCode(exception);
            var state = statusCode switch
            {
                HttpStatusCode.Unauthorized => "Unauthorized",
                HttpStatusCode.Forbidden => "Forbidden",
                _ when exception is A2AException || exception is JsonException => "MalformedResponse",
                _ => "Error"
            };
            var messageText = statusCode switch
            {
                HttpStatusCode.Unauthorized => "Atlas rejected the A2A request (401 Unauthorized). Check the configured bearer token or Atlas authorization requirements.",
                HttpStatusCode.Forbidden => "Atlas rejected the A2A request (403 Forbidden). The configured identity is not authorized.",
                _ when state == "MalformedResponse" => "Atlas returned a malformed or unsupported A2A response.",
                _ => "The Atlas A2A request failed. See diagnostics for the sanitized failure category."
            };
            var updated = SetRequestDiagnostics(state, messageText, correlationId, taskId, stopwatch.ElapsedMilliseconds);
            return new ChatResult(false, null, null, [], messageText, updated);
        }
    }

    private AtlasDiagnostics DiscoveryFailure(Uri cardUrl, Exception exception)
    {
        var statusCode = FindHttpStatusCode(exception);
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return SetDiagnostics(AtlasDiagnostics.Empty with
            {
                State = "Unauthorized",
                AgentCardUrl = SanitizeUri(cardUrl),
                Message = "Atlas Agent Card discovery was rejected (401 Unauthorized). Check the configured bearer token or Atlas authorization requirements."
            });
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return SetDiagnostics(AtlasDiagnostics.Empty with
            {
                State = "Forbidden",
                AgentCardUrl = SanitizeUri(cardUrl),
                Message = "Atlas Agent Card discovery was rejected (403 Forbidden). The configured identity is not authorized."
            });
        }

        if (statusCode == HttpStatusCode.NotFound || exception is A2AException || exception is JsonException)
        {
            return SetNotA2A(cardUrl);
        }

        var failure = statusCode is null
            ? "Atlas Agent Card discovery failed. Verify the URL and network access."
            : $"Atlas Agent Card discovery failed (HTTP {(int)statusCode.Value}).";
        return SetDiagnostics(AtlasDiagnostics.Empty with
        {
            State = "Error",
            AgentCardUrl = SanitizeUri(cardUrl),
            Message = failure
        });
    }

    private AtlasDiagnostics SetNotA2A(Uri? cardUrl) => SetDiagnostics(AtlasDiagnostics.Empty with
    {
        State = "NotA2AEnabled",
        AgentCardUrl = cardUrl is null ? null : SanitizeUri(cardUrl),
        Message = NotA2AMessage
    });

    private static IReadOnlyList<string> ExtractText(IEnumerable<Part>? parts) =>
        parts is null
            ? []
            : parts
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToArray();

    private static IReadOnlyList<AgentSkillInfo> MapSkills(IEnumerable<AgentSkill>? skills) =>
        skills?.Select(skill => new AgentSkillInfo(skill.Id, skill.Name, skill.Description, skill.Tags ?? []))
            .ToArray() ?? [];

    private bool TryValidateUri(Uri uri, out string? failure)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failure = "URLs containing embedded credentials are not allowed.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            failure = "URLs with query strings or fragments are not allowed. Supply bearer credentials through ATLAS_A2A_BEARER_TOKEN.";
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            failure = null;
            return true;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.IsLoopback && configuration.AllowHttpLocalhost)
        {
            failure = null;
            return true;
        }

        failure = "HTTPS is required. HTTP is permitted only for explicitly enabled localhost development.";
        return false;
    }

    private Uri? FindDiscoveredEndpoint()
    {
        var endpointText = Volatile.Read(ref _discoveredEndpoint);
        return Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    private string? _discoveredEndpoint;

    private AtlasDiagnostics SetDiagnostics(AtlasDiagnostics diagnostics)
    {
        if (diagnostics.Endpoint is not null)
        {
            var protocolEndpoint = _resolvedEndpoint;
            if (protocolEndpoint is not null)
            {
                Volatile.Write(ref _discoveredEndpoint, protocolEndpoint);
            }
        }

        Volatile.Write(ref _diagnostics, diagnostics);
        return diagnostics;
    }

    private string? _resolvedEndpoint;

    private AtlasDiagnostics SetRequestDiagnostics(string state, string message, string correlationId, string? taskId, long latencyMs)
    {
        var updated = Diagnostics with
        {
            State = state,
            Message = message,
            CorrelationId = correlationId,
            TaskId = taskId,
            LatencyMs = latencyMs
        };
        return SetDiagnostics(updated);
    }

    private static string SanitizeUri(Uri uri) => $"{uri.GetLeftPart(UriPartial.Authority)}/[path redacted]";

    private static HttpStatusCode? FindHttpStatusCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } statusCode })
            {
                return statusCode;
            }
        }

        return null;
    }
}

public sealed record AgentSkillInfo(string Id, string Name, string Description, IReadOnlyList<string> Tags);

public sealed record AtlasDiagnostics
{
    public static AtlasDiagnostics Empty => new() { State = "Unknown", Message = "Atlas discovery has not run." };
    public string State { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public string? AgentCardUrl { get; init; }
    public string? AgentName { get; init; }
    public string? AgentVersion { get; init; }
    public IReadOnlyList<AgentSkillInfo> Skills { get; init; } = [];
    public string? Endpoint { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? CorrelationId { get; init; }
    public string? TaskId { get; init; }
    public long? LatencyMs { get; init; }
}

public sealed record ChatRequest(string Question);

public sealed record ChatResult(
    bool Success,
    string? Answer,
    string? TaskStatus,
    IReadOnlyList<string> Artifacts,
    string? Error,
    AtlasDiagnostics Diagnostics);
