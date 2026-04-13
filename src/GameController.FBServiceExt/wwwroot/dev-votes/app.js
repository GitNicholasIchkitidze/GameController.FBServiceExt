const refreshSelect = document.getElementById('refreshSelect');
const refreshToggleButton = document.getElementById('refreshToggleButton');
const fromInput = document.getElementById('fromInput');
const toInput = document.getElementById('toInput');

const toggleVotingButton = document.getElementById('toggleVotingButton');
const votingBadge = document.getElementById('votingBadge');
const liveBadge = document.getElementById('liveBadge');
const statusText = document.getElementById('statusText');
const defaultShowText = document.getElementById('defaultShowText');
const generatedAt = document.getElementById('generatedAt');
const lastRefresh = document.getElementById('lastRefresh');
const sourceText = document.getElementById('sourceText');
const activeShowText = document.getElementById('activeShowText');
const workerCount = document.getElementById('workerCount');
const totalVotes = document.getElementById('totalVotes');
const totalUniqueUsers = document.getElementById('totalUniqueUsers');
const candidateCount = document.getElementById('candidateCount');
const candidateSummary = document.getElementById('candidateSummary');
const recentVotesRows = document.getElementById('recentVotesRows');
const recentFilterInput = document.getElementById('recentFilterInput');
const pageSizeSelect = document.getElementById('pageSizeSelect');
const prevPageButton = document.getElementById('prevPageButton');
const nextPageButton = document.getElementById('nextPageButton');
const paginationInfo = document.getElementById('paginationInfo');
const messageBox = document.getElementById('messageBox');
const subText = document.getElementById('subText');
const metricIngress = document.getElementById('metricIngress');
const metricGarbage = document.getElementById('metricGarbage');
const metricSavedVotes = document.getElementById('metricSavedVotes');
const metricWorkers = document.getElementById('metricWorkers');
const metricWorkerP95 = document.getElementById('metricWorkerP95');
const metricOptionsSent = document.getElementById('metricOptionsSent');
const metricVotesAccepted = document.getElementById('metricVotesAccepted');
const metricRawP95 = document.getElementById('metricRawP95');
const metricIgnored = document.getElementById('metricIgnored');
const metricCooldown = document.getElementById('metricCooldown');
const managedWorkerCount = document.getElementById('managedWorkerCount');
const detectedWorkerCount = document.getElementById('detectedWorkerCount');
const targetManagedWorkersInput = document.getElementById('targetManagedWorkersInput');
const applyManagedWorkersButton = document.getElementById('applyManagedWorkersButton');
const decreaseWorkersButton = document.getElementById('decreaseWorkersButton');
const increaseWorkersButton = document.getElementById('increaseWorkersButton');
const increaseWorkersByTwoButton = document.getElementById('increaseWorkersByTwoButton');
const workerControlStatus = document.getElementById('workerControlStatus');
const managedWorkersRows = document.getElementById('managedWorkersRows');

let timerId = null;
let refreshEnabled = true;
let inFlight = false;
let updatingVotingState = false;
let currentPage = 1;
let currentVotingStarted = false;
let currentActiveShowId = '';
let configuredDefaultActiveShowId = '';
let updatingWorkerCount = false;

const ka = {
    subtitle: 'ამ გვერდიდან ხედავ მიმდინარე ვოტინგის სურათს: ჩართვა/გამორთვა, ხმების ჯამი, კანდიდატების განაწილება, ტოპ ფანები, ბოლო 200 მიღებული ხმა და სასარგებლო სამუშაო მეტრიკები.',
    ingress: 'აპი-მ მიიღო ამდენი webhook request.',
    garbage: 'აპი და worker ამტრიებს არასაჭირო traffic-ს.',
    savedVotes: 'ამდენი დადასტურებული ხმა ჩაწერილია AcceptedVotes ცხრილში.',
    workers: 'აქტიური worker instance-ები მომენტში.',
    workerP95: 'დამუშავების p95 გაზომვა normalized flow-ისთვის.',
    optionsSent: 'კანდიდატების carousel ამდენჯერ გაიგზავნა.',
    votesAccepted: 'სისტემამ ამდენი ხმა მიიღო საბოლოოდ.',
    rawP95: 'საწყისი raw normalizer cycle p95.',
    ignored: 'იგნორირებული event-ები processor-იდან.',
    cooldown: 'რამდენჯერ მოხდა მცდელობა cooldown პერიოდში.',
    onMessage: 'ვოტინგი ჩართულია.',
    onHint: (showId) => `ვოტინგი ჩართულია. მონიტორი აკვირდება showId='${showId}'.`,
    offHint: 'ვოტინგი გამორთულია.',
    offMessage: 'ვოტინგი გამორთულია. გვერდი ისტორიულ AcceptedVotes მონაცემებს აჩვენებს.',
    loadError: 'პოლინგის snapshot-ის ჩატვირთვა ვერ მოხერხდა'
};

function setDefaultText() {
    subText.textContent = ka.subtitle;
    document.getElementById('metricIngressNote').textContent = ka.ingress;
    document.getElementById('metricGarbageNote').textContent = ka.garbage;
    document.getElementById('metricSavedVotesNote').textContent = ka.savedVotes;
    document.getElementById('metricWorkersNote').textContent = ka.workers;
    document.getElementById('metricWorkerP95Note').textContent = ka.workerP95;
    document.getElementById('metricOptionsSentNote').textContent = ka.optionsSent;
    document.getElementById('metricVotesAcceptedNote').textContent = ka.votesAccepted;
    document.getElementById('metricRawP95Note').textContent = ka.rawP95;
    document.getElementById('metricIgnoredNote').textContent = ka.ignored;
    document.getElementById('metricCooldownNote').textContent = ka.cooldown;
}

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

function formatMs(value) {
    return `${Number(value || 0).toFixed(Number(value || 0) >= 100 ? 0 : 2)} ms`;
}

function setMessage(text) {
    messageBox.textContent = text;
}

function fmtInt(value) {
    return new Intl.NumberFormat().format(Number(value || 0));
}

function buildVotesQuery() {
    const params = new URLSearchParams();
    if (fromInput.value) params.set('fromUtc', new Date(fromInput.value).toISOString());
    if (toInput.value) params.set('toUtc', new Date(toInput.value).toISOString());
    if (recentFilterInput.value.trim()) params.set('userFilter', recentFilterInput.value.trim());
    params.set('page', String(currentPage));
    params.set('pageSize', pageSizeSelect.value || '200');
    return params.toString();
}

function counter(instances, name) {
    return (instances || []).reduce((sum, instance) => sum + Number(instance.counters?.[name] || 0), 0);
}

function distribution(instances, name) {
    const samples = (instances || []).map(instance => instance.distributions?.[name]).filter(Boolean);
    if (!samples.length) return { p95: 0 };
    return { p95: Math.max(...samples.map(item => Number(item.p95 || 0))) };
}

function renderVotingState(snapshot) {
    currentVotingStarted = !!snapshot.votingStarted;
    const activeShowId = snapshot.activeShowId || '';
    const defaultShowId = snapshot.configuredDefaultActiveShowId || '';
    currentActiveShowId = activeShowId;
    configuredDefaultActiveShowId = defaultShowId;

    votingBadge.textContent = currentVotingStarted ? 'Voting: ON' : 'Voting: OFF';
    votingBadge.className = `badge ${currentVotingStarted ? 'on' : 'off'}`;
    votingBadge.title = currentVotingStarted
        ? ka.onHint(activeShowId || '-')
        : ka.offHint;

    activeShowText.textContent = activeShowId || '-';
    defaultShowText.textContent = defaultShowId || '-';


    toggleVotingButton.disabled = updatingVotingState;
    toggleVotingButton.textContent = currentVotingStarted ? 'Turn Voting OFF' : 'Turn Voting ON';
    toggleVotingButton.className = currentVotingStarted ? 'danger main-toggle' : 'primary main-toggle';
}

async function loadVotingState() {
    const response = await fetch('/dev/admin/api/voting', { headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`Voting API HTTP ${response.status}`);
    const snapshot = await response.json();
    renderVotingState(snapshot);
    return snapshot;
}

async function updateVotingState(started) {
    updatingVotingState = true;
    toggleVotingButton.disabled = true;
    setMessage(`Updating voting state to ${started ? 'ON' : 'OFF'}...`);
    try {
        const response = await fetch('/dev/admin/api/voting', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify({ votingStarted: started, activeShowId: currentActiveShowId || null })
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        renderVotingState(await response.json());
    } finally {
        updatingVotingState = false;
    }
}


function renderCandidates(candidates) {
    if (!candidates.length) {
        candidateSummary.innerHTML = '<div class="empty">No accepted votes in the selected range.</div>';
        return;
    }

    candidateSummary.innerHTML = candidates.map((candidate) => {
        const fans = candidate.topFans.length
            ? candidate.topFans.map((fan) => `<strong>${escapeHtml(fan.userId)}</strong>_${escapeHtml(fan.userAccountName)} (${fan.voteCount})`).join(' | ')
            : 'Top 3 fans: no repeat fans yet';

        return `
      <div class="poll-item">
        <div class="poll-main">
          <div>
            <div class="poll-name">${escapeHtml(candidate.candidateDisplayName)}</div>
            <div class="poll-id">${escapeHtml(candidate.candidateId)}</div>
          </div>
          <div class="poll-metrics">Votes: <strong>${fmtInt(candidate.voteCount)}</strong> (${candidate.votePercentage.toFixed(2)}%)</div>
          <div class="poll-unique">Unique: ${fmtInt(candidate.uniqueUsers)}</div>
        </div>
        <div class="poll-fans">Top 3 fans: ${fans}</div>
      </div>`;
    }).join('');
}

function renderRecentVotes(page) {
    const votes = page.items || [];
    paginationInfo.textContent = `Page ${page.page} / ${page.totalPages} | Rows ${page.totalCount}`;
    prevPageButton.disabled = page.page <= 1;
    nextPageButton.disabled = page.page >= page.totalPages;

    if (!votes.length) {
        recentVotesRows.innerHTML = '<tr><td colspan="4" class="empty">No accepted votes yet.</td></tr>';
        return;
    }

    recentVotesRows.innerHTML = votes.map((vote) => `
    <tr>
      <td><strong>${escapeHtml(vote.userId)}</strong><div class="muted">${escapeHtml(vote.userAccountName)}</div></td>
      <td>${escapeHtml(vote.candidateDisplayName)}<div class="muted">${escapeHtml(vote.candidateId)}</div></td>
      <td>${escapeHtml(vote.showId)}</td>
      <td>${escapeHtml(formatDateTime(vote.confirmedAtUtc))}</td>
    </tr>`).join('');
}

function renderMetrics(metricsSnapshot, votesSnapshot) {
    const api = metricsSnapshot.apiInstances || [];
    const workers = metricsSnapshot.workerInstances || [];
    const ingress = counter(api, 'api.webhook.requests_total');
    const apiGarbage = counter(api, 'api.webhook.garbage_messages_dropped');
    const workerGarbage = counter(workers, 'worker.raw.garbage_messages_dropped');
    const ignored = counter(workers, 'worker.processor.ignored');
    const cooldownMsg = counter(workers, 'worker.processor.cooldown_message_attempts');
    const cooldownPb = counter(workers, 'worker.processor.cooldown_postback_attempts');

    metricIngress.textContent = fmtInt(ingress);
    metricGarbage.textContent = fmtInt(apiGarbage + workerGarbage);
    metricSavedVotes.textContent = fmtInt(votesSnapshot.totalVotes);
    metricWorkers.textContent = fmtInt(workers.length);
    metricWorkerP95.textContent = formatMs(distribution(workers, 'worker.normalized.cycle_ms').p95);
    metricOptionsSent.textContent = fmtInt(counter(workers, 'worker.processor.options_sent'));
    metricVotesAccepted.textContent = fmtInt(counter(workers, 'worker.processor.vote_accepted'));
    metricRawP95.textContent = formatMs(distribution(workers, 'worker.raw.cycle_ms').p95);
    metricIgnored.textContent = fmtInt(ignored);
    metricCooldown.textContent = fmtInt(cooldownMsg + cooldownPb);
    workerCount.textContent = fmtInt(workers.length);
}
function setWorkerControlStatus(text) {
    workerControlStatus.textContent = text;
}

function updateWorkerControlButtons() {
    const disabled = updatingWorkerCount;
    applyManagedWorkersButton.disabled = disabled;
    decreaseWorkersButton.disabled = disabled;
    increaseWorkersButton.disabled = disabled;
    increaseWorkersByTwoButton.disabled = disabled;
    targetManagedWorkersInput.disabled = disabled;
}

function adjustTargetManagedWorkers(delta) {
    const current = Number(targetManagedWorkersInput.value || 0);
    targetManagedWorkersInput.value = String(Math.max(0, current + delta));
}

function renderWorkerControl(snapshot) {
    const managedWorkers = snapshot.managedWorkers || [];
    managedWorkerCount.textContent = fmtInt(snapshot.desiredManagedWorkerCount || 0);
    detectedWorkerCount.textContent = fmtInt(snapshot.detectedWorkerInstances || 0);

    if (document.activeElement !== targetManagedWorkersInput && !updatingWorkerCount) {
        targetManagedWorkersInput.value = String(snapshot.desiredManagedWorkerCount || 0);
    }

    if (!managedWorkers.length) {
        managedWorkersRows.innerHTML = '<tr><td colspan="4" class="empty">No managed workers started from Poll Monitor.</td></tr>';
        return;
    }

    managedWorkersRows.innerHTML = managedWorkers.map((worker) => `
    <tr>
      <td>${fmtInt(worker.slot)}</td>
      <td>${fmtInt(worker.processId)}</td>
      <td>${escapeHtml(formatDateTime(worker.startedAtUtc))}</td>
      <td>${worker.isRunning ? 'Running' : 'Exited'}</td>
    </tr>`).join('');
}

async function loadWorkerControlState() {
    const response = await fetch('/dev/admin/api/workers', { headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`Worker control API HTTP ${response.status}`);
    const snapshot = await response.json();
    renderWorkerControl(snapshot);
    return snapshot;
}

async function updateManagedWorkerCount(desiredCount) {
    updatingWorkerCount = true;
    updateWorkerControlButtons();
    setWorkerControlStatus(`Applying managed worker count: ${desiredCount}...`);

    try {
        const response = await fetch('/dev/admin/api/workers', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify({ managedWorkerCount: desiredCount })
        });

        if (!response.ok) {
            let detail = `HTTP ${response.status}`;
            try {
                const problem = await response.json();
                if (problem?.detail) {
                    detail = problem.detail;
                }
            } catch {
                // ignore parsing failure and keep the HTTP status text
            }

            throw new Error(detail);
        }

        const snapshot = await response.json();
        renderWorkerControl(snapshot);
        setWorkerControlStatus(`Managed worker count updated to ${snapshot.desiredManagedWorkerCount}.`);
        return snapshot;
    } finally {
        updatingWorkerCount = false;
        updateWorkerControlButtons();
    }
}

async function performManagedWorkerUpdate(nextCount) {
    try {
        await updateManagedWorkerCount(nextCount);
        await refreshSnapshot();
    } catch (error) {
        setWorkerControlStatus(`Worker update failed: ${error.message}`);
    }
}

async function refreshSnapshot() {
    if (!refreshEnabled || inFlight) return;

    inFlight = true;
    statusText.textContent = 'Loading';
    liveBadge.textContent = refreshEnabled ? 'Live' : 'Stopped';

    try {
        const [votesResponse, metricsResponse, votingSnapshot, workerControlSnapshot] = await Promise.all([
            fetch(`/dev/votes/api?${buildVotesQuery()}`, { headers: { Accept: 'application/json' } }),
            fetch('/dev/metrics/api', { headers: { Accept: 'application/json' } }),
            loadVotingState(),
            loadWorkerControlState()
        ]);

        if (!votesResponse.ok) throw new Error(`Votes API HTTP ${votesResponse.status}`);
        if (!metricsResponse.ok) throw new Error(`Metrics API HTTP ${metricsResponse.status}`);

        const votesSnapshot = await votesResponse.json();
        const metricsSnapshot = await metricsResponse.json();

        totalVotes.textContent = fmtInt(votesSnapshot.totalVotes);
        totalUniqueUsers.textContent = fmtInt(votesSnapshot.totalUniqueUsers);
        candidateCount.textContent = fmtInt(votesSnapshot.candidates.length);
        generatedAt.textContent = formatDateTime(votesSnapshot.generatedAtUtc);
        lastRefresh.textContent = new Date().toLocaleTimeString();
        sourceText.textContent = votesSnapshot.source || 'active-show';
        activeShowText.textContent = votesSnapshot.showId || votingSnapshot.activeShowId || '-';

        renderCandidates(votesSnapshot.candidates || []);
        renderRecentVotes(votesSnapshot.recentVotesPage || { page: 1, totalPages: 1, totalCount: 0, items: [] });
        renderMetrics(metricsSnapshot, votesSnapshot);
        renderWorkerControl(workerControlSnapshot);

        statusText.textContent = refreshEnabled ? 'Live' : 'Stopped';
        setMessage(votingSnapshot.votingStarted ? ka.onMessage : ka.offMessage);
    } catch (error) {
        statusText.textContent = 'Error';
        setMessage(`${ka.loadError}: ${error.message}`);
    } finally {
        inFlight = false;
    }
}

function updateRefreshToggleUi() {
    refreshToggleButton.textContent = refreshEnabled ? 'Refresh: ON' : 'Refresh: OFF';
    refreshToggleButton.className = refreshEnabled ? 'secondary' : 'danger';
    liveBadge.textContent = refreshEnabled ? 'Live' : 'Stopped';
}

function applyTimer() {
    if (timerId) {
        clearInterval(timerId);
        timerId = null;
    }

    if (!refreshEnabled) return;

    const interval = Number(refreshSelect.value);
    timerId = setInterval(refreshSnapshot, Number.isFinite(interval) ? interval : 120000);
}

refreshToggleButton.addEventListener('click', async () => {
    refreshEnabled = !refreshEnabled;
    updateRefreshToggleUi();
    applyTimer();
    if (refreshEnabled) {
        await refreshSnapshot();
    }
});

toggleVotingButton.addEventListener('click', async () => {
    await updateVotingState(!currentVotingStarted);
    await refreshSnapshot();
});

applyManagedWorkersButton.addEventListener('click', async () => {
    await performManagedWorkerUpdate(Number(targetManagedWorkersInput.value || 0));
});

decreaseWorkersButton.addEventListener('click', async () => {
    adjustTargetManagedWorkers(-1);
    await performManagedWorkerUpdate(Number(targetManagedWorkersInput.value || 0));
});

increaseWorkersButton.addEventListener('click', async () => {
    adjustTargetManagedWorkers(1);
    await performManagedWorkerUpdate(Number(targetManagedWorkersInput.value || 0));
});

increaseWorkersByTwoButton.addEventListener('click', async () => {
    adjustTargetManagedWorkers(2);
    await performManagedWorkerUpdate(Number(targetManagedWorkersInput.value || 0));
});

refreshSelect.addEventListener('change', applyTimer);
fromInput.addEventListener('change', () => { currentPage = 1; refreshSnapshot(); });
toInput.addEventListener('change', () => { currentPage = 1; refreshSnapshot(); });
recentFilterInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
        event.preventDefault();
        currentPage = 1;
        refreshSnapshot();
    }
});
pageSizeSelect.addEventListener('change', () => { currentPage = 1; refreshSnapshot(); });
prevPageButton.addEventListener('click', () => {
    if (currentPage > 1) {
        currentPage--;
        refreshSnapshot();
    }
});
nextPageButton.addEventListener('click', () => {
    currentPage++;
    refreshSnapshot();
});

setDefaultText();
setDefaultRange();
updateRefreshToggleUi();
updateWorkerControlButtons();
applyTimer();
refreshSnapshot();





