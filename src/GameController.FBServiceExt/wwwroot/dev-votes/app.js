const refreshSelect = document.getElementById('refreshSelect');
const refreshButton = document.getElementById('refreshButton');
const pauseButton = document.getElementById('pauseButton');
const fromInput = document.getElementById('fromInput');
const toInput = document.getElementById('toInput');
const showInput = document.getElementById('showInput');
const statusText = document.getElementById('statusText');
const generatedAt = document.getElementById('generatedAt');
const lastRefresh = document.getElementById('lastRefresh');
const sourceText = document.getElementById('sourceText');
const activeShowText = document.getElementById('activeShowText');
const totalVotes = document.getElementById('totalVotes');
const totalUniqueUsers = document.getElementById('totalUniqueUsers');
const candidateCount = document.getElementById('candidateCount');
const candidateSummary = document.getElementById('candidateSummary');
const recentVotesRows = document.getElementById('recentVotesRows');
const messageBox = document.getElementById('messageBox');
let timerId = null;
let paused = false;
let inFlight = false;

function setDefaultRange() {
  const now = new Date();
  const from = new Date(now.getTime() - 24 * 60 * 60 * 1000);
  fromInput.value = toLocalInputValue(from);
  toInput.value = toLocalInputValue(now);
}
function toLocalInputValue(value) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
}
function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}
function formatDateTime(value) {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}
function setMessage(text) { messageBox.textContent = text; }
function buildQuery() {
  const params = new URLSearchParams();
  if (fromInput.value) params.set('fromUtc', new Date(fromInput.value).toISOString());
  if (toInput.value) params.set('toUtc', new Date(toInput.value).toISOString());
  if (showInput.value.trim()) params.set('showId', showInput.value.trim());
  params.set('limit', '200');
  return params.toString();
}
function renderCandidates(candidates) {
  if (!candidates.length) {
    candidateSummary.innerHTML = '<div class="empty">No accepted votes in the selected range.</div>';
    return;
  }
  candidateSummary.innerHTML = candidates.map((candidate) => {
    const fans = candidate.topFans.length
      ? candidate.topFans.map((fan) => `<strong>${escapeHtml(fan.userAccountName)}</strong> (${fan.voteCount})`).join(' | ')
      : 'No repeat fans yet';
    return `
      <div class="summary-item">
        <div class="summary-head">
          <div class="candidate">${escapeHtml(candidate.candidateDisplayName)} <span style="color:#95a3bb">${escapeHtml(candidate.candidateId)}</span></div>
          <div class="numbers">Votes: <strong>${candidate.voteCount}</strong> (${candidate.votePercentage.toFixed(2)}%)</div>
          <div class="unique">Unique: ${candidate.uniqueUsers}</div>
        </div>
        <div class="fans">Top 3 fans: ${fans}</div>
      </div>`;
  }).join('');
}
function renderRecentVotes(votes) {
  if (!votes.length) {
    recentVotesRows.innerHTML = '<tr><td colspan="4" class="empty">No accepted votes yet.</td></tr>';
    return;
  }
  recentVotesRows.innerHTML = votes.map((vote) => `
    <tr>
      <td>${escapeHtml(vote.userAccountName)}<div style="color:#95a3bb;font-size:12px">${escapeHtml(vote.userId)}</div></td>
      <td>${escapeHtml(vote.candidateDisplayName)}<div style="color:#95a3bb;font-size:12px">${escapeHtml(vote.candidateId)}</div></td>
      <td>${escapeHtml(vote.showId)}</td>
      <td>${escapeHtml(formatDateTime(vote.confirmedAtUtc))}</td>
    </tr>`).join('');
}
async function refreshSnapshot() {
  if (paused || inFlight) return;
  inFlight = true;
  statusText.textContent = 'Loading';
  try {
    const response = await fetch(`/dev/votes/api?${buildQuery()}`, { headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const snapshot = await response.json();
    totalVotes.textContent = snapshot.totalVotes;
    totalUniqueUsers.textContent = snapshot.totalUniqueUsers;
    candidateCount.textContent = snapshot.candidates.length;
    generatedAt.textContent = formatDateTime(snapshot.generatedAtUtc);
    lastRefresh.textContent = new Date().toLocaleTimeString();
    sourceText.textContent = snapshot.source || 'active-show';
    activeShowText.textContent = snapshot.showId || '-';
    renderCandidates(snapshot.candidates || []);
    renderRecentVotes(snapshot.recentVotes || []);
    statusText.textContent = 'Live';
    setMessage(snapshot.showId
      ? `Showing accepted votes for show '${snapshot.showId}'.`
      : 'No explicit show filter. Using current active show if configured.');
  } catch (error) {
    statusText.textContent = 'Error';
    setMessage(`Failed to load votes snapshot: ${error.message}`);
  } finally {
    inFlight = false;
  }
}
function applyTimer() {
  if (timerId) { clearInterval(timerId); timerId = null; }
  if (paused) return;
  const interval = Number(refreshSelect.value);
  timerId = setInterval(refreshSnapshot, Number.isFinite(interval) ? interval : 120000);
}
refreshButton.addEventListener('click', refreshSnapshot);
pauseButton.addEventListener('click', () => {
  paused = !paused;
  pauseButton.textContent = paused ? 'Resume' : 'Pause';
  statusText.textContent = paused ? 'Paused' : 'Live';
  applyTimer();
});
refreshSelect.addEventListener('change', applyTimer);
fromInput.addEventListener('change', refreshSnapshot);
toInput.addEventListener('change', refreshSnapshot);
showInput.addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); refreshSnapshot(); } });
setDefaultRange();
applyTimer();
refreshSnapshot();
