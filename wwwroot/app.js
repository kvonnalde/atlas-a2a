const form = document.querySelector('#chat-form');
const questionField = document.querySelector('#question');
const transcript = document.querySelector('#transcript');
const emptyState = document.querySelector('#empty-state');
const sendButton = document.querySelector('#send-button');
const cancelButton = document.querySelector('#cancel-request');
const chatError = document.querySelector('#chat-error');
let activeRequest;

function setText(id, value, fallback = 'Not available') {
  document.getElementById(id).textContent = value || fallback;
}

function renderDiagnostics(diagnostics) {
  const indicator = document.querySelector('#connection-indicator');
  const stateLabels = {
    Ready: 'Card discovered',
    MissingConfiguration: 'Setup required',
    ConfigurationError: 'Configuration issue',
    NotA2AEnabled: 'A2A not available',
    Unauthorized: 'Unauthorized',
    Forbidden: 'Forbidden',
    Timeout: 'Timed out',
    Error: 'Connection error',
    MalformedResponse: 'Invalid response'
  };
  indicator.dataset.state = diagnostics.state || 'Unknown';
  const foundryReady = diagnostics.foundryConfigured === true;
  const atlasMissing = diagnostics.state === 'MissingConfiguration';
  setText('connection-label', foundryReady && atlasMissing ? 'Foundry ready' : stateLabels[diagnostics.state] || diagnostics.state || 'Unknown');
  setText('diagnostics-message', foundryReady && atlasMissing
    ? 'Foundry can answer general questions. Configure the Atlas Agent Card URL only for Atlas-specific questions.'
    : diagnostics.message, 'No diagnostics available.');
  setText('diag-card', diagnostics.agentCardUrl);
  setText('diag-endpoint', diagnostics.endpoint);
  setText('diag-protocol', diagnostics.protocolVersion ? `A2A ${diagnostics.protocolVersion} / JSON-RPC` : 'A2A 1.0');
  setText('diag-correlation', diagnostics.correlationId, 'None');
  setText('diag-task', diagnostics.taskId, 'None');
  setText('diag-latency', Number.isFinite(diagnostics.latencyMs) ? `${diagnostics.latencyMs} ms` : null, 'None');

  setText('agent-name', diagnostics.agentName, foundryReady && atlasMissing ? 'Foundry Assistant' : 'Atlas Agent');
  setText('agent-version', diagnostics.agentVersion ? `Version ${diagnostics.agentVersion}` : null,
    foundryReady && atlasMissing ? 'General chat available' : 'Waiting for Agent Card');
  const skills = document.querySelector('#skill-list');
  skills.replaceChildren();
  for (const skill of diagnostics.skills || []) {
    const tag = document.createElement('span');
    tag.className = 'skill-tag';
    tag.textContent = skill.name || skill.id;
    tag.title = skill.description || skill.id;
    skills.append(tag);
  }
}

function addMessage(role, content, metadata = {}) {
  emptyState.hidden = true;
  const message = document.createElement('article');
  message.className = `message ${role}`;
  const mark = document.createElement('span');
  mark.className = 'message-mark';
  mark.setAttribute('aria-hidden', 'true');
  mark.textContent = role === 'user' ? 'YOU' : metadata.source === 'Foundry' ? 'F' : 'A';
  const body = document.createElement('div');
  body.className = 'message-body';
  const meta = document.createElement('div');
  meta.className = 'message-meta';
  const author = document.createElement('strong');
  author.textContent = role === 'user' ? 'You' : metadata.source === 'Foundry' ? 'Foundry Assistant' : 'Atlas Agent';
  const time = document.createElement('span');
  time.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  meta.append(author, time);
  body.append(meta);

  if (metadata.loading) {
    const loading = document.createElement('span');
    loading.className = 'loading-dots';
    loading.textContent = 'Finding the right assistant';
    body.append(loading);
  } else {
    const text = document.createElement('div');
    text.className = 'message-content';
    text.textContent = content || (metadata.taskStatus ? `No text artifact was returned. Task status: ${metadata.taskStatus}.` : 'Atlas returned no text.');
    body.append(text);
    if (metadata.taskStatus) {
      const taskState = document.createElement('span');
      taskState.className = 'task-state';
      taskState.textContent = `Task · ${metadata.taskStatus}`;
      body.append(taskState);
    }
    for (const artifact of metadata.artifacts || []) {
      const label = document.createElement('span');
      label.className = 'artifact-label';
      label.textContent = 'Text artifact';
      const artifactText = document.createElement('div');
      artifactText.className = 'message-content';
      artifactText.textContent = artifact;
      body.append(label, artifactText);
    }
  }

  message.append(mark, body);
  transcript.append(message);
  transcript.scrollTop = transcript.scrollHeight;
  return message;
}

async function loadStatus() {
  try {
    const response = await fetch('/api/status', { cache: 'no-store' });
    if (!response.ok) throw new Error('Could not read connection status.');
    renderDiagnostics(await response.json());
  } catch {
    setText('diagnostics-message', 'The local External Agent service is unavailable.');
  }
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  const question = questionField.value.trim();
  if (!question || activeRequest) return;

  chatError.hidden = true;
  addMessage('user', question);
  const pending = addMessage('assistant', '', { loading: true });
  questionField.value = '';
  sendButton.disabled = true;
  cancelButton.hidden = false;
  activeRequest = new AbortController();

  try {
    const response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question }),
      signal: activeRequest.signal
    });
    const result = await response.json();
    renderDiagnostics(result.diagnostics || {});
    pending.remove();
    if (!response.ok || !result.success) {
      chatError.textContent = result.error || 'The assistant could not complete the request.';
      chatError.hidden = false;
      addMessage('assistant', result.error || 'The assistant could not complete the request.', { source: result.source });
      return;
    }
    addMessage('assistant', result.answer, { source: result.source, taskStatus: result.taskStatus, artifacts: result.artifacts });
  } catch (error) {
    pending.remove();
    if (error.name !== 'AbortError') {
      chatError.textContent = 'The local request failed. Check the diagnostics and try again.';
      chatError.hidden = false;
      addMessage('assistant', chatError.textContent);
    } else {
      addMessage('assistant', 'Request cancelled.');
    }
  } finally {
    activeRequest = undefined;
    sendButton.disabled = false;
    cancelButton.hidden = true;
    questionField.focus();
  }
});

cancelButton.addEventListener('click', () => activeRequest?.abort());
document.querySelector('#clear-chat').addEventListener('click', () => {
  transcript.replaceChildren(emptyState);
  emptyState.hidden = false;
  chatError.hidden = true;
});
document.querySelector('#retry-discovery').addEventListener('click', async event => {
  const button = event.currentTarget;
  button.disabled = true;
  try {
    const response = await fetch('/api/discover', { method: 'POST' });
    renderDiagnostics(await response.json());
  } catch {
    setText('diagnostics-message', 'The local External Agent service is unavailable.');
  } finally {
    button.disabled = false;
  }
});

loadStatus();
