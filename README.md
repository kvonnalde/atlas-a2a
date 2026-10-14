# External Agent

A standalone local .NET 8 chat client for testing A2A v1.0 communication with Atlas. It uses the official `A2A` .NET SDK (`1.0.0-preview2`) for Agent Card resolution, JSON-RPC messages, and task retrieval. This project is only an A2A client; it does not host an A2A endpoint, modify Atlas, or fall back to Atlas REST routes.

## Prerequisites

- .NET 8 SDK
- An Atlas Agent Card URL and, if Atlas requires authorization, a bearer token supplied through a secure local environment mechanism

Atlas must expose an authenticated A2A v1.0 JSON-RPC endpoint in its Agent Card. A REST assistant route alone is not sufficient. The current Atlas setup is understood to expose REST but not inbound A2A, so this client cannot complete a smoke test until that prerequisite is implemented. The app reports missing discovery or A2A support rather than treating REST as a fallback.

## Configure and run

`.env.example` contains placeholders only. The app reads configuration directly from its process environment; it does not load `.env` files. Supply `ATLAS_AGENT_CARD_URL` and optionally `ATLAS_A2A_BEARER_TOKEN` through your local environment or secret-management mechanism. Never put a real token in source, browser code, prompts, command history, screenshots, or logs.

For an HTTPS Atlas endpoint:

```powershell
$env:ATLAS_AGENT_CARD_URL = 'https://your-atlas-host/.well-known/agent-card.json'
dotnet run --urls http://127.0.0.1:5080
```

Set `ATLAS_A2A_BEARER_TOKEN` in the process environment using your approved secure method when Atlas requires bearer authorization. The client does not guess an authentication mode. A missing token is sent as no Authorization header; Atlas 401/403 responses are shown explicitly.

To enable general chat, configure `FOUNDRY_ENDPOINT`, `FOUNDRY_MODEL`, and `FOUNDRY_API_KEY`. `FOUNDRY_ENDPOINT` is the Target URI shown for the Foundry deployment; the Azure OpenAI SDK builds the chat-completions request for the selected deployment. Use the exact deployment name for `FOUNDRY_MODEL`. The app uses the deployment to classify each question and answer general questions in the same request. For Atlas-specific questions, it discards any model answer and sends the original question directly to Atlas over A2A. Foundry settings are optional; without them, chat continues to use Atlas only. The question text is sent to Foundry for routing, so follow your organization's data-handling requirements.

In local Development, values in `.env` are loaded without overriding variables already set in the process environment. In Azure App Service, add the endpoint and deployment name as application settings, and store the API key as a protected setting or Key Vault reference. Do not commit a real key.

HTTP Atlas URLs are rejected except for loopback development when both the URL is localhost/loopback and `ATLAS_ALLOW_HTTP_LOCALHOST=true`. The local browser app itself can be served on loopback HTTP as shown above; this does not permit HTTP to a non-loopback Atlas host.

Open <http://127.0.0.1:5080>. On startup, the app attempts Agent Card discovery. Use the refresh control in Diagnostics to retry after correcting the environment and restarting the process.

## Behavior and diagnostics

- Only a discovered interface advertising `JSONRPC` and protocol version `1.0` is used.
- Questions are sent with the SDK as A2A user messages. Immediate message replies and task replies are supported. Tasks are retrieved with the protocol `tasks/get` operation until terminal status or the 60-second request deadline.
- Text from messages and artifacts is displayed as text, never treated as HTML, executable instructions, or links.
- Diagnostics include discovery state, sanitized endpoint, protocol version, local correlation ID, task ID, elapsed time, and sanitized failure category. Paths and credentials are not displayed.
- Requests are bounded and cancellable. Browser cancellation propagates to the server request. Authentication is not disabled or retried.

## Tests

```powershell
dotnet test .\tests\ExternalAgent.Tests.csproj
```

The unit suite covers missing configuration, Agent Card discovery failure, unauthorized response, immediate message response, task polling/result, timeout, and malformed response handling.

## Manual A2A smoke test

1. Confirm Atlas exposes `/.well-known/agent-card.json` (or the configured card URL) over HTTPS and that the card advertises a `JSONRPC` interface with protocol version `1.0`.
2. Confirm Atlas's A2A endpoint accepts the required bearer identity and supports `message/send`; if it returns a task, confirm `tasks/get` is authorized and returns task status/artifacts.
3. Set `ATLAS_AGENT_CARD_URL` to the exact card URL and, where required, provide `ATLAS_A2A_BEARER_TOKEN` via the approved local secret mechanism. Start the app and inspect Diagnostics for the discovered name, version, skill list, endpoint, and protocol.
4. Send a harmless question such as `Reply with the word ready.` Confirm the transcript contains the exact returned text or task artifacts, and Diagnostics shows request correlation, latency, and any task ID.
5. Verify a bad/missing token is reported as 401 or 403 without revealing token contents. Do not count Agent Card discovery alone as a successful message connection.

The current Atlas endpoint is not known to expose inbound A2A. Do not claim the smoke test succeeded unless both card discovery and an authenticated A2A message have actually been verified.
