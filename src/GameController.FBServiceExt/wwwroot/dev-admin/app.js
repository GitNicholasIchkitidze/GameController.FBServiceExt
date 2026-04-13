const refreshSelect = document.getElementById('refreshSelect');
const refreshButton = document.getElementById('refreshButton');
const pauseButton = document.getElementById('pauseButton');
const statusBadge = document.getElementById('statusBadge');
const statusText = document.getElementById('statusText');
const generatedAt = document.getElementById('generatedAt');
const lastRefresh = document.getElementById('lastRefresh');
const sourceText = document.getElementById('sourceText');
const votingState = document.getElementById('votingState');
const currentValue = document.getElementById('currentValue');
const effectText = document.getElementById('effectText');
const activeShowValue = document.getElementById('activeShowValue');
const defaultActiveShowValue = document.getElementById('defaultActiveShowValue');
const activeShowInput = document.getElementById('activeShowInput');
const messageBox = document.getElementById('messageBox');
const turnOnButton = document.getElementById('turnOnButton');
const turnOffButton = document.getElementById('turnOffButton');
const applyShowButton = document.getElementById('applyShowButton');
const useDefaultShowButton = document.getElementById('useDefaultShowButton');

let refreshTimer = null;
let paused = false;
let updating = false;
let currentVotingStarted = false;
let currentActiveShowId = '';
let configuredDefaultActiveShowId = '';
let activeShowInputDirty = false;

function setMessage(text, kind = '') {
  messageBox.textContent = text;
  messageBox.className = 'message';
  if (kind) {
    messageBox.classList.add(kind);
  }
}

function setStatusBadge(kind, text) {
  statusBadge.className = 'badge';
  statusBadge.classList.add(kind);
  statusText.textContent = text;
}

function formatClock(value) {
  if (!value) {
    return '-';
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function applySnapshot(snapshot) {
  const started = !!snapshot.votingStarted;
  const activeShowId = snapshot.activeShowId || '';
  const defaultShowId = snapshot.configuredDefaultActiveShowId || '';
  currentVotingStarted = started;
  currentActiveShowId = activeShowId;
  configuredDefaultActiveShowId = defaultShowId;
  generatedAt.textContent = formatClock(snapshot.utc);
  lastRefresh.textContent = formatClock(new Date().toISOString());
  sourceText.textContent = snapshot.source ?? 'redis';
  currentValue.textContent = started ? 'true' : 'false';
  effectText.textContent = started ? (activeShowId ? 'Process' : 'Blocked - no show') : 'Drop';
  activeShowValue.textContent = activeShowId || '-';
  defaultActiveShowValue.textContent = defaultShowId || '-';
  if (updating || !activeShowInputDirty) {
    activeShowInput.value = activeShowId;
    activeShowInputDirty = false;
  }
  votingState.textContent = started ? 'ON' : 'OFF';
  votingState.className = `big-state ${started ? 'on' : 'off'}`;
  turnOnButton.disabled = updating || started;
  turnOffButton.disabled = updating || !started;
  applyShowButton.disabled = updating;
  useDefaultShowButton.disabled = updating || !defaultShowId;
  setStatusBadge(started ? 'live' : 'off', started ? 'Voting enabled' : 'Voting disabled');
}

async function loadSnapshot() {
  try {
    const response = await fetch('/dev/admin/api/voting', { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const snapshot = await response.json();
    applySnapshot(snapshot);
    if (!updating) {
      if (snapshot.votingStarted) {
        setMessage(
          snapshot.activeShowId
            ? `Voting is enabled for show '${snapshot.activeShowId}'.`
            : (snapshot.configuredDefaultActiveShowId
                ? `Voting is ON, but runtime ActiveShowId is still empty. Config default is '${snapshot.configuredDefaultActiveShowId}'.`
                : 'VotingStarted is ON, but ActiveShowId is not configured.'),
          'success');
      } else {
        setMessage('Voting is disabled. Normal vote traffic should be acknowledged and dropped early.', 'success');
      }
    }
  } catch (error) {
    setStatusBadge('error', 'Fetch failed');
    setMessage(`Failed to load voting state: ${error.message}`, 'error');
  }
}

async function updateVotingState(started) {
  updating = true;
  turnOnButton.disabled = true;
  turnOffButton.disabled = true;
  applyShowButton.disabled = true;
  useDefaultShowButton.disabled = true;
  setMessage(`Updating runtime state to VotingStarted=${started}...`);

  try {
    const response = await fetch('/dev/admin/api/voting', {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        votingStarted: started,
        activeShowId: activeShowInput.value.trim() || null
      })
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const snapshot = await response.json();
    activeShowInputDirty = false;
    applySnapshot(snapshot);
    setMessage(started
      ? `VotingStarted switched ON. ActiveShowId='${snapshot.activeShowId ?? '-'}'.`
      : 'VotingStarted switched OFF. New vote traffic should be dropped except #forgetme flow.', 'success');
  } catch (error) {
    setStatusBadge('error', 'Update failed');
    setMessage(`Failed to update voting state: ${error.message}`, 'error');
  } finally {
    updating = false;
    await loadSnapshot();
  }
}

async function updateActiveShowId() {
  updating = true;
  turnOnButton.disabled = true;
  turnOffButton.disabled = true;
  applyShowButton.disabled = true;
  useDefaultShowButton.disabled = true;
  setMessage(`Applying ActiveShowId='${activeShowInput.value.trim() || '-'}'...`);

  try {
    const response = await fetch('/dev/admin/api/voting/active-show', {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        activeShowId: activeShowInput.value.trim() || null
      })
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const snapshot = await response.json();
    activeShowInputDirty = false;
    applySnapshot(snapshot);
    setMessage(
      snapshot.activeShowId
        ? `ActiveShowId changed to '${snapshot.activeShowId}'. VotingStarted=${snapshot.votingStarted}.`
        : 'ActiveShowId was cleared. Voting flow will stay blocked until a show is set.',
      'success');
  } catch (error) {
    setStatusBadge('error', 'Update failed');
    setMessage(`Failed to update ActiveShowId: ${error.message}`, 'error');
  } finally {
    updating = false;
    await loadSnapshot();
  }
}

function applyConfiguredDefaultToInput() {
  if (!configuredDefaultActiveShowId) {
    setMessage('Configured default ActiveShowId is not set in appsettings for this environment.', 'error');
    return;
  }

  activeShowInput.value = configuredDefaultActiveShowId;
  setMessage(`Loaded configured default ActiveShowId '${configuredDefaultActiveShowId}' into the input.`);
}

function scheduleRefresh() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  if (paused) {
    return;
  }

  const interval = Number.parseInt(refreshSelect.value, 10);
  refreshTimer = window.setInterval(() => {
    void loadSnapshot();
  }, Number.isFinite(interval) ? interval : 2000);
}

refreshButton.addEventListener('click', () => {
  void loadSnapshot();
});

pauseButton.addEventListener('click', () => {
  paused = !paused;
  pauseButton.textContent = paused ? 'Resume' : 'Pause';
  scheduleRefresh();
});

refreshSelect.addEventListener('change', scheduleRefresh);
activeShowInput.addEventListener('input', () => {
  activeShowInputDirty = true;
});
turnOnButton.addEventListener('click', () => void updateVotingState(true));
turnOffButton.addEventListener('click', () => void updateVotingState(false));
applyShowButton.addEventListener('click', () => void updateActiveShowId());
useDefaultShowButton.addEventListener('click', applyConfiguredDefaultToInput);

scheduleRefresh();
void loadSnapshot();


