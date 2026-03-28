const queryInput = document.getElementById('queryInput');
const limitSelect = document.getElementById('limitSelect');
const refreshSelect = document.getElementById('refreshSelect');
const refreshButton = document.getElementById('refreshButton');
const pauseButton = document.getElementById('pauseButton');
const rows = document.getElementById('rows');
const currentQuery = document.getElementById('currentQuery');
const lastRefresh = document.getElementById('lastRefresh');
const entryCount = document.getElementById('entryCount');
const statusText = document.getElementById('statusText');
const errorText = document.getElementById('errorText');

let timerId = null;
let paused = false;
let inFlight = false;

function escapeHtml(value) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function levelClass(levelName) {
  const normalized = (levelName || '').toLowerCase();
  if (normalized === 'warning') return 'level-warning';
  if (normalized === 'error' || normalized === 'critical' || normalized === 'alert' || normalized === 'emergency') return 'level-error';
  return 'level-info';
}

function trimPrefix(value) {
  const prefix = 'GameController.FBServiceExt.';
  return value.startsWith(prefix) ? value.substring(prefix.length) : value;
}

function buildCallerSource(entry) {
  const callerType = trimPrefix((entry.callerTypeName || '').trim());
  const callerMember = (entry.callerMemberName || '').trim();
  const callerLine = entry.callerLineNumber;

  if (!callerType) {
    return '';
  }

  let result = callerType;
  if (callerMember) {
    result += `.${callerMember}`;
  }
  if (callerLine) {
    result += `:${callerLine}`;
  }

  return result;
}

function buildFallbackSource(entry) {
  const sourceContext = trimPrefix((entry.sourceContext || '').trim());
  if (sourceContext) {
    return sourceContext;
  }

  return (entry.source || '-').trim() || '-';
}

function formatSource(entry) {
  return buildCallerSource(entry) || buildFallbackSource(entry);
}

function formatSourceTooltip(entry) {
  return buildCallerSource(entry) || buildFallbackSource(entry);
}

function renderEntries(entries) {
  if (!entries.length) {
    rows.innerHTML = '<tr><td colspan="5" class="empty">No logs found for the current query.</td></tr>';
    return;
  }

  rows.innerHTML = entries.map((entry) => {
    const shortSource = formatSource(entry);
    const fullSource = formatSourceTooltip(entry);

    return `
    <tr>
      <td class="timestamp">${escapeHtml(entry.timestamp || '')}</td>
      <td>${escapeHtml(entry.serviceRole || '-')}</td>
      <td><span class="level ${levelClass(entry.levelName)}">${escapeHtml(entry.levelName || 'Unknown')}</span></td>
      <td class="source" title="${escapeHtml(fullSource)}">${escapeHtml(shortSource)}</td>
      <td class="message">${escapeHtml(entry.message || '')}</td>
    </tr>`;
  }).join('');
}

async function refreshLogs() {
  if (paused || inFlight) {
    return;
  }

  inFlight = true;
  statusText.textContent = 'Loading';
  errorText.textContent = '';

  const query = queryInput.value.trim();
  const limit = limitSelect.value;
  currentQuery.textContent = query || '-';

  try {
    const response = await fetch(`/dev/logs/api?query=${encodeURIComponent(query)}&limit=${encodeURIComponent(limit)}`, {
      headers: { 'Accept': 'application/json' }
    });

    if (!response.ok) {
      const payload = await response.text();
      throw new Error(payload || `HTTP ${response.status}`);
    }

    const data = await response.json();
    renderEntries(data.entries || []);
    entryCount.textContent = String((data.entries || []).length);
    lastRefresh.textContent = new Date().toLocaleTimeString();
    statusText.textContent = 'Live';
  } catch (error) {
    rows.innerHTML = `<tr><td colspan="5" class="error">${escapeHtml(String(error.message || error))}</td></tr>`;
    statusText.textContent = 'Error';
    errorText.textContent = 'Graylog request failed';
  } finally {
    inFlight = false;
  }
}

function applyTimer() {
  if (timerId) {
    clearInterval(timerId);
    timerId = null;
  }

  const interval = Number(refreshSelect.value);
  if (!paused && interval > 0) {
    timerId = setInterval(refreshLogs, interval);
  }
}

refreshButton.addEventListener('click', refreshLogs);
refreshSelect.addEventListener('change', () => {
  applyTimer();
  refreshLogs();
});
pauseButton.addEventListener('click', () => {
  paused = !paused;
  pauseButton.textContent = paused ? 'Resume' : 'Pause';
  statusText.textContent = paused ? 'Paused' : 'Live';
  applyTimer();
});

document.querySelectorAll('.chip').forEach((chip) => {
  chip.addEventListener('click', () => {
    queryInput.value = chip.dataset.query || '';
    refreshLogs();
  });
});

queryInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    refreshLogs();
  }
});

applyTimer();
refreshLogs();