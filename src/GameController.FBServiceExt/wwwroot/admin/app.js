const loginView = document.getElementById('loginView');
const dashboardView = document.getElementById('dashboardView');
const loginForm = document.getElementById('loginForm');
const usernameInput = document.getElementById('usernameInput');
const passwordInput = document.getElementById('passwordInput');
const loginButton = document.getElementById('loginButton');
const loginMessage = document.getElementById('loginMessage');
const refreshSelect = document.getElementById('refreshSelect');
const refreshButton = document.getElementById('refreshButton');
const pauseButton = document.getElementById('pauseButton');
const logoutButton = document.getElementById('logoutButton');
const sessionBadge = document.getElementById('sessionBadge');
const sessionText = document.getElementById('sessionText');
const operatorName = document.getElementById('operatorName');
const generatedAt = document.getElementById('generatedAt');
const lastRefresh = document.getElementById('lastRefresh');
const apiInstanceCount = document.getElementById('apiInstanceCount');
const workerInstanceCount = document.getElementById('workerInstanceCount');
const activeShowIdText = document.getElementById('activeShowIdText');
const activeShowIdInput = document.getElementById('activeShowIdInput');
const votingState = document.getElementById('votingState');
const votingHint = document.getElementById('votingHint');
const currentValue = document.getElementById('currentValue');
const effectText = document.getElementById('effectText');
const turnOnButton = document.getElementById('turnOnButton');
const turnOffButton = document.getElementById('turnOffButton');
const actionMessage = document.getElementById('actionMessage');
const trafficStats = document.getElementById('trafficStats');
const resultStats = document.getElementById('resultStats');
const queueRows = document.getElementById('queueRows');
const workerInstances = document.getElementById('workerInstances');

let paused = false;
let refreshTimer = null;
let requestInFlight = false;
let updateInFlight = false;

const tooltips = {
  loginSub: 'ოპერატორის გვერდი, საიდანაც იცვლება VotingStarted და ჩანს live runtime სურათი.',
  username: 'ადმინისტრატორის ან ოპერატორის მომხმარებლის სახელი.',
  password: 'ოპერატორის პაროლი ავტორიზაციისთვის.',
  signIn: 'ადმინისტრაციულ dashboard-ში შესვლა.',
  loginMessage: 'ავტორიზაციის მიმდინარე პასუხი ან შეცდომა.',
  refresh: 'დაფის ავტომატური განახლების ინტერვალი.',
  refreshNow: 'მონაცემების ახლავე თავიდან წამოღება backend-იდან.',
  pause: 'ავტო-განახლების შეჩერება ან გაგრძელება.',
  session: 'მიმდინარე ავტორიზაციის მდგომარეობა.',
  operator: 'ახლა შესული ოპერატორის სახელი.',
  generated: 'dashboard snapshot-ის გენერირების დრო backend-ზე.',
  lastRefresh: 'ბოლო წარმატებული refresh-ის ლოკალური დრო.',
  apiInstances: 'ამ snapshot-ში აქტიური API instance-ების რაოდენობა.',
  workerInstances: 'ამ snapshot-ში აქტიური Worker instance-ების რაოდენობა.',
  activeShowMeta: 'ამჟამად აქტიური შოუს იდენტიფიკატორი, რომლითაც signed vote payload-ები მოწმდება.',
  signOut: 'ოპერატორის სესიის დასრულება.',
  votingGate: 'სისტემის მთავარი VotingStarted switch. OFF-ზე ჩვეულებრივი ხმები იჭრება, #forgetme მაინც გადის.',
  currentValue: 'Redis-ში შენახული VotingStarted მნიშვნელობა. ეს არის source of truth.',
  effect: 'ON/OFF მდგომარეობის პრაქტიკული ეფექტი traffic-ზე.',
  turnOn: 'VotingStarted=true. ახალი ხმის მიცემის flow ხელახლა ჩაირთვება.',
  turnOff: 'VotingStarted=false. ჩვეულებრივი vote traffic ადრევე დაიჭრება.',
  actionMessage: 'ბოლო ოპერაციის ან live მდგომარეობის ტექსტური განმარტება.',
  activeShowInput: 'runtime-ში ამჟამად აქტიური შოუს იდენტიფიკატორი. ახალი payload-ები ამ მნიშვნელობას უნდა ემთხვეოდეს.',
  trafficCard: 'API ingress-ის და RabbitMQ-ის ამჟამინდელი ტრაფიკის მოკლე სურათი.',
  resultsCard: 'Worker-ის დამუშავების და ხმის მიცემის შედეგების ძირითადი counters.',
  queueCard: 'RabbitMQ რიგების მდგომარეობა და წნეხი.',
  workerCard: 'თითო worker instance-ის ცალკე snapshot.',
  queueName: 'RabbitMQ queue-ის სახელი.',
  queueStatus: 'რიგის ჯანმრთელობის სტატუსი: Stable, Transient, Draining ან Critical.',
  queueConsumers: 'ამ queue-ზე მიერთებული consumer-ების რაოდენობა.',
  queueReady: 'რიგში მომლოდინე, ჯერ დაუმუშავებელი მესიჯები.',
  queueUnacked: 'consumer-ს უკვე აღებული, მაგრამ ჯერ დაუდასტურებელი მესიჯები.',
  queuePublishRate: 'რიგში შეტანის სიჩქარე წამში.',
  queueAckRate: 'დამუშავებული მესიჯების დადასტურების სიჩქარე წამში.',
  workerInstanceId: 'worker instance-ის იდენტიფიკატორი: მანქანა, როლი და პროცესი.',
  workerInstanceMeta: 'process id, გარემო და ბოლო snapshot-ის ასაკი.',
  workerEvents: 'ამ worker-ზე ნანახი normalized event-ების რაოდენობა.',
  workerRawP95: 'raw normalizer ციკლის p95 დრო ამ worker-ზე.',
  workerNormalizedP95: 'normalized processor ციკლის p95 დრო ამ worker-ზე.',
  workerOptions: 'რამდენჯერ გაიხსნა vote-start/options flow.',
  workerVotes: 'დადასტურებული და მიღებული ხმების რაოდენობა.',
  workerParallelism: 'ერთ worker process-ში raw loop-ების პარალელიზმის კონფიგურაცია.',
  apiRequests: 'API-მ მიღებული webhook request-ების ჯამი და მათში მოთავსებული event-ები.',
  api200Rate: 'რამდენ request-ზე დაბრუნდა 200 OK.',
  apiAck: 'webhook ACK latency: რამდენ დროში დაუბრუნდა პასუხი Facebook-ს.',
  queueReadyStat: 'ყველა queue-ის ready backlog ჯამურად.',
  publishAck: 'RabbitMQ publish და ack სიჩქარეების შედარება.',
  garbageDropped: 'არასასარგებლო traffic, რომელიც API-მ business flow-მდე გაჭრა.',
  instancesPair: 'აქტიური API და Worker instance-ების რაოდენობა ერთად.',
  votesAccepted: 'საბოლოოდ მიღებული ხმები.',
  optionsSent: 'vote-start flow რამდენჯერ გაიგზავნა მომხმარებლებთან.',
  eventsSeen: 'processor-ის მიერ ნანახი normalized event-ები, ignore-ებულების ჩათვლით.',
  rawP95: 'raw normalizer stage-ის p95 latency.',
  normalizedP95: 'normalized processor stage-ის p95 latency.',
  cooldownAttempts: 'მცდელობები, როცა cooldown ჯერ არ იყო გასული. msg = ტექსტი, pb = postback.',
  workerParallelismStat: 'worker raw parallelism-ის მიმდინარე მნიშვნელობა.'
};

const staticTitles = [
  ['#loginView .sub', tooltips.loginSub],
  ['label[for="usernameInput"]', tooltips.username],
  ['#usernameInput', tooltips.username],
  ['label[for="passwordInput"]', tooltips.password],
  ['#passwordInput', tooltips.password],
  ['#loginButton', tooltips.signIn],
  ['#loginMessage', tooltips.loginMessage],
  ['#dashboardView .hero .sub', 'ოპერატორის დაფა VotingStarted-ის სამართავად და live metrics-ის საყურებლად.'],
  ['label[for="refreshSelect"]', tooltips.refresh],
  ['#refreshSelect', tooltips.refresh],
  ['#refreshButton', tooltips.refreshNow],
  ['#pauseButton', tooltips.pause],
  ['#logoutButton', tooltips.signOut],
  ['#sessionBadge', tooltips.session],
  ['#operatorName', tooltips.operator],
  ['#generatedAt', tooltips.generated],
  ['#lastRefresh', tooltips.lastRefresh],
  ['#apiInstanceCount', tooltips.apiInstances],
  ['#workerInstanceCount', tooltips.workerInstances],
  ['.meta .meta-row:nth-of-type(1) .meta-label', tooltips.session],
  ['.meta .meta-row:nth-of-type(2) .meta-label', tooltips.operator],
  ['.meta .meta-row:nth-of-type(3) .meta-label', tooltips.generated],
  ['.meta .meta-row:nth-of-type(4) .meta-label', tooltips.lastRefresh],
  ['.meta .meta-row:nth-of-type(5) .meta-label', tooltips.apiInstances],
  ['.meta .meta-row:nth-of-type(6) .meta-label', tooltips.workerInstances],
  ['.meta .meta-row:nth-of-type(7) .meta-label', tooltips.activeShowMeta],
  ['#activeShowIdText', tooltips.activeShowMeta],
  ['.grid .card:nth-of-type(1) h2', tooltips.votingGate],
  ['#votingState', tooltips.votingGate],
  ['#votingHint', tooltips.votingGate],
  ['#currentValue', tooltips.currentValue],
  ['#effectText', tooltips.effect],
  ['#turnOnButton', tooltips.turnOn],
  ['#turnOffButton', tooltips.turnOff],
  ['#actionMessage', tooltips.actionMessage],
  ['.grid .card:nth-of-type(2) h2', 'VotingStarted მნიშვნელობის ჩართვა/გამორთვის მოქმედებები.'],
  ['label[for="activeShowIdInput"]', tooltips.activeShowInput],
  ['#activeShowIdInput', tooltips.activeShowInput],
  ['.grid:nth-of-type(2) .card:nth-of-type(1) h2', tooltips.trafficCard],
  ['.grid:nth-of-type(2) .card:nth-of-type(2) h2', tooltips.resultsCard],
  ['.wide-grid .card:nth-of-type(1) h2', tooltips.queueCard],
  ['.wide-grid .card:nth-of-type(2) h2', tooltips.workerCard],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(1)', tooltips.queueName],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(2)', tooltips.queueStatus],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(3)', tooltips.queueConsumers],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(4)', tooltips.queueReady],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(5)', tooltips.queueUnacked],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(6)', tooltips.queuePublishRate],
  ['.wide-grid .card:nth-of-type(1) thead th:nth-of-type(7)', tooltips.queueAckRate]
];

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function applyStaticTitles() {
  for (const [selector, title] of staticTitles) {
    document.querySelectorAll(selector).forEach(element => {
      element.title = title;
    });
  }
}

function setMessage(element, text, kind = '') {
  element.textContent = text;
  element.className = 'message';
  if (kind) {
    element.classList.add(kind);
  }
}

function setSessionBadge(kind, text) {
  sessionBadge.className = 'badge';
  sessionBadge.classList.add(kind);
  sessionText.textContent = text;
}

function fmtInt(value) {
  return new Intl.NumberFormat().format(Number(value || 0));
}

function fmtMs(value) {
  const number = Number(value || 0);
  return `${number.toFixed(number >= 100 ? 0 : 2)} ms`;
}

function fmtRate(value) {
  return `${Number(value || 0).toFixed(2)}/s`;
}

function fmtPercent(part, total) {
  if (!total) {
    return '0.00%';
  }

  return `${((part / total) * 100).toFixed(2)}%`;
}

function fmtClock(value) {
  if (!value) {
    return '-';
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function fmtAgo(value) {
  if (!value) {
    return '-';
  }

  const diffMs = Date.now() - new Date(value).getTime();
  if (diffMs < 1000) {
    return 'ახლა';
  }

  return `${Math.round(diffMs / 1000)} წმ. წინ`;
}

function counter(instances, name) {
  return (instances || []).reduce((sum, instance) => sum + Number(instance.counters?.[name] || 0), 0);
}

function gaugeMax(instances, name) {
  return (instances || []).reduce((max, instance) => Math.max(max, Number(instance.gauges?.[name] || 0)), 0);
}

function distribution(instances, name) {
  const samples = (instances || [])
    .map(instance => instance.distributions?.[name])
    .filter(Boolean);

  if (!samples.length) {
    return { p95: 0, p99: 0, max: 0 };
  }

  return {
    p95: Math.max(...samples.map(item => Number(item.p95 || 0))),
    p99: Math.max(...samples.map(item => Number(item.p99 || 0))),
    max: Math.max(...samples.map(item => Number(item.max || 0)))
  };
}

function classifyQueue(queue) {
  const ready = Number(queue.ready || 0);
  const unacked = Number(queue.unacknowledged || 0);
  const publishRate = Number(queue.publishRate || 0);
  const ackRate = Number(queue.ackRate || 0);
  const consumers = Number(queue.consumers || 0);

  if (consumers === 0 && (ready > 0 || unacked > 0)) {
    return { label: 'Critical', className: 'critical', explanation: 'backlog არის, მაგრამ აქტიური consumer არ ჩანს.' };
  }

  if (ready === 0 && unacked === 0) {
    return { label: 'Stable', className: 'stable', explanation: 'რიგი სტაბილურია; backlog არ ჩანს.' };
  }

  if (ready === 0 && unacked > 0) {
    return { label: 'Draining', className: 'draining', explanation: 'მესიჯები უკვე მუშავდება და სისტემა backlog-ს ჭამს.' };
  }

  if (ready >= 1000 || (ready >= 250 && publishRate > 0 && ackRate < publishRate * 0.85)) {
    return { label: 'Critical', className: 'critical', explanation: 'რიგში დაგროვება იზრდება და ack სიჩქარე ჩამორჩება publish-ს.' };
  }

  if (ackRate > 0 && ackRate >= publishRate * 0.95) {
    return { label: 'Draining', className: 'draining', explanation: 'backlog დროებითია და ამჟამად იწმინდება.' };
  }

  return { label: 'Transient', className: 'transient', explanation: 'დროებითი წნეხია, მაგრამ ჯერ კრიტიკული ნიშნები არ ჩანს.' };
}

function renderStatGrid(container, stats) {
  container.innerHTML = stats.map(stat => `
    <div class="stat">
      <div class="stat-label" title="${escapeHtml(stat.title || '')}">${escapeHtml(stat.label)}</div>
      <div class="stat-value" title="${escapeHtml(stat.title || '')}">${escapeHtml(stat.value)}</div>
      <div class="stat-note" title="${escapeHtml(stat.noteTitle || stat.title || '')}">${escapeHtml(stat.note || '') || '&nbsp;'}</div>
    </div>`).join('');
}

function renderQueueTable(queues) {
  if (!queues.length) {
    queueRows.innerHTML = '<tr><td colspan="7" title="რიგების metrics ჯერ არ არის მიღებული.">No queue metrics available.</td></tr>';
    return;
  }

  queueRows.innerHTML = queues.map(queue => {
    const status = classifyQueue(queue);
    return `
      <tr>
        <td title="${escapeHtml(tooltips.queueName)}">${escapeHtml(queue.name)}</td>
        <td title="${escapeHtml(status.explanation)}"><span class="pill ${status.className}">${escapeHtml(status.label)}</span></td>
        <td title="${escapeHtml(tooltips.queueConsumers)}">${fmtInt(queue.consumers)}</td>
        <td title="${escapeHtml(tooltips.queueReady)}">${fmtInt(queue.ready)}</td>
        <td title="${escapeHtml(tooltips.queueUnacked)}">${fmtInt(queue.unacknowledged)}</td>
        <td title="${escapeHtml(tooltips.queuePublishRate)}">${fmtRate(queue.publishRate)}</td>
        <td title="${escapeHtml(tooltips.queueAckRate)}">${fmtRate(queue.ackRate)}</td>
      </tr>`;
  }).join('');
}

function renderWorkerInstances(instances) {
  if (!instances.length) {
    workerInstances.innerHTML = '<div class="instance-item" title="worker snapshot ჯერ არ არის მიღებული.">No worker snapshots yet.</div>';
    return;
  }

  workerInstances.innerHTML = instances.map(instance => {
    const rawCycle = distribution([instance], 'worker.raw.cycle_ms');
    const normalizedCycle = distribution([instance], 'worker.normalized.cycle_ms');
    const votesAccepted = counter([instance], 'worker.processor.vote_accepted') + counter([instance], 'worker.processor.vote_accepted_reconciled');
    return `
      <div class="instance-item">
        <div class="instance-title" title="${escapeHtml(tooltips.workerInstanceId)}">${escapeHtml(instance.instanceId)}</div>
        <div class="instance-meta" title="${escapeHtml(tooltips.workerInstanceMeta)}">Process ${fmtInt(instance.processId)} - ${escapeHtml(instance.environmentName)} - updated ${escapeHtml(fmtAgo(instance.updatedAtUtc))}</div>
        <div class="mini-grid">
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerEvents)}">Events</div><div class="mini-value" title="${escapeHtml(tooltips.workerEvents)}">${fmtInt(counter([instance], 'worker.processor.events_seen'))}</div></div>
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerRawP95)}">Raw p95</div><div class="mini-value" title="${escapeHtml(tooltips.workerRawP95)}">${fmtMs(rawCycle.p95)}</div></div>
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerNormalizedP95)}">Normalized p95</div><div class="mini-value" title="${escapeHtml(tooltips.workerNormalizedP95)}">${fmtMs(normalizedCycle.p95)}</div></div>
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerOptions)}">Options</div><div class="mini-value" title="${escapeHtml(tooltips.workerOptions)}">${fmtInt(counter([instance], 'worker.processor.options_sent'))}</div></div>
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerVotes)}">Votes</div><div class="mini-value" title="${escapeHtml(tooltips.workerVotes)}">${fmtInt(votesAccepted)}</div></div>
          <div class="mini-cell"><div class="mini-label" title="${escapeHtml(tooltips.workerParallelism)}">Parallelism</div><div class="mini-value" title="${escapeHtml(tooltips.workerParallelism)}">${fmtInt(gaugeMax([instance], 'worker.raw.parallelism'))}</div></div>
        </div>
      </div>`;
  }).join('');
}

function applyVotingState(started, activeShowId = '') {
  currentValue.textContent = started ? 'true' : 'false';
  effectText.textContent = started ? (activeShowId ? 'Process traffic' : 'Blocked - no show') : 'Drop traffic';
  votingState.className = `big-state ${started ? 'on' : 'off'}`;
  votingState.firstChild.textContent = started ? 'ON' : 'OFF';
  votingHint.textContent = started
    ? (activeShowId ? 'Traffic is processed normally.' : 'VotingStarted is ON, but ActiveShowId is not configured yet.')
    : 'Regular vote traffic is dropped early.';
  turnOnButton.disabled = updateInFlight || started;
  turnOffButton.disabled = updateInFlight || !started;
}

function showDashboard(username) {
  loginView.classList.add('hidden');
  dashboardView.classList.remove('hidden');
  operatorName.textContent = username || '-';
  setSessionBadge('ok', 'Signed in');
}

function showLogin(message) {
  dashboardView.classList.add('hidden');
  loginView.classList.remove('hidden');
  operatorName.textContent = '-';
  setSessionBadge('off', 'Signed out');
  if (message) {
    setMessage(loginMessage, message, 'error');
  }
}

async function fetchJson(url, options = {}, allowUnauthorized = false) {
  const response = await fetch(url, {
    headers: {
      Accept: 'application/json',
      ...(options.headers || {})
    },
    ...options
  });

  if (response.status === 401 && allowUnauthorized) {
    return { unauthorized: true };
  }

  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  return response.json();
}

async function loadSession() {
  const session = await fetchJson('/admin/api/session');
  if (!session.authenticated) {
    showLogin();
    return false;
  }

  showDashboard(session.username);
  return true;
}

async function loadDashboard() {
  if (dashboardView.classList.contains('hidden') || requestInFlight || paused) {
    return;
  }

  requestInFlight = true;
  try {
    const payload = await fetchJson('/admin/api/dashboard', {}, true);
    if (payload.unauthorized) {
      showLogin('Session expired. Please sign in again.');
      return;
    }

    renderDashboard(payload);
  } catch (error) {
    setSessionBadge('error', 'Fetch failed');
    setMessage(actionMessage, `Failed to load dashboard: ${error.message}`, 'error');
  } finally {
    requestInFlight = false;
  }
}

function renderDashboard(payload) {
  const metrics = payload.metrics || { apiInstances: [], workerInstances: [], queues: [] };
  const apiInstancesData = metrics.apiInstances || [];
  const workerInstancesData = metrics.workerInstances || [];
  const queues = metrics.queues || [];

  showDashboard(payload.operator);
  const activeShowId = payload.activeShowId || '';
  applyVotingState(!!payload.votingStarted, activeShowId);
  activeShowIdText.textContent = activeShowId || '-';
  if (document.activeElement !== activeShowIdInput) {
    activeShowIdInput.value = activeShowId;
  }
  generatedAt.textContent = fmtClock(payload.utc || metrics.generatedAtUtc);
  lastRefresh.textContent = fmtClock(new Date().toISOString());
  apiInstanceCount.textContent = fmtInt(apiInstancesData.length);
  workerInstanceCount.textContent = fmtInt(workerInstancesData.length);

  const apiRequests = counter(apiInstancesData, 'api.webhook.requests_total');
  const api200 = counter(apiInstancesData, 'api.webhook.status.200');
  const apiAck = distribution(apiInstancesData, 'api.webhook.ack_ms');
  const apiEvents = counter(apiInstancesData, 'api.webhook.messaging_events_total');
  const garbageSeen = counter(apiInstancesData, 'api.webhook.garbage_messages_total');
  const garbageDropped = counter(apiInstancesData, 'api.webhook.garbage_messages_dropped');
  const garbageRequests = counter(apiInstancesData, 'api.webhook.garbage_requests_dropped');
  const queueReady = queues.reduce((sum, queue) => sum + Number(queue.ready || 0), 0);
  const queueUnacked = queues.reduce((sum, queue) => sum + Number(queue.unacknowledged || 0), 0);
  const totalPublishRate = queues.reduce((sum, queue) => sum + Number(queue.publishRate || 0), 0);
  const totalAckRate = queues.reduce((sum, queue) => sum + Number(queue.ackRate || 0), 0);

  renderStatGrid(trafficStats, [
    { label: 'API requests', value: fmtInt(apiRequests), note: `${fmtInt(apiEvents)} webhook events`, title: tooltips.apiRequests },
    { label: '200 rate', value: fmtPercent(api200, apiRequests), note: `${fmtInt(api200)} successful ACK`, title: tooltips.api200Rate },
    { label: 'ACK p95', value: fmtMs(apiAck.p95), note: `p99 ${fmtMs(apiAck.p99)}`, title: tooltips.apiAck },
    { label: 'Queue ready', value: fmtInt(queueReady), note: `unacked ${fmtInt(queueUnacked)}`, title: tooltips.queueReadyStat },
    { label: 'Publish / Ack', value: fmtRate(totalPublishRate), note: `ack ${fmtRate(totalAckRate)}`, title: tooltips.publishAck },
    { label: 'Garbage dropped', value: fmtInt(garbageDropped), note: `${fmtInt(garbageSeen)} seen - ${fmtInt(garbageRequests)} req`, title: tooltips.garbageDropped },
    { label: 'Instances', value: `${fmtInt(apiInstancesData.length)} / ${fmtInt(workerInstancesData.length)}`, note: 'API / Worker', title: tooltips.instancesPair }
  ]);

  const votesAccepted = counter(workerInstancesData, 'worker.processor.vote_accepted') + counter(workerInstancesData, 'worker.processor.vote_accepted_reconciled');
  const optionsSent = counter(workerInstancesData, 'worker.processor.options_sent');
  const eventsSeen = counter(workerInstancesData, 'worker.processor.events_seen');
  const ignored = counter(workerInstancesData, 'worker.processor.ignored') + counter(workerInstancesData, 'worker.processor.cooldown_ignored');
  const cooldownAttempts = counter(workerInstancesData, 'worker.processor.cooldown_attempts');
  const cooldownMessageAttempts = counter(workerInstancesData, 'worker.processor.cooldown_message_attempts');
  const cooldownPostbackAttempts = counter(workerInstancesData, 'worker.processor.cooldown_postback_attempts');
  const rawCycle = distribution(workerInstancesData, 'worker.raw.cycle_ms');
  const normalizedCycle = distribution(workerInstancesData, 'worker.normalized.cycle_ms');

  renderStatGrid(resultStats, [
    { label: 'Votes accepted', value: fmtInt(votesAccepted), note: 'Confirmed votes written by workers.', title: tooltips.votesAccepted },
    { label: 'Options sent', value: fmtInt(optionsSent), note: 'Vote-start flows opened.', title: tooltips.optionsSent },
    { label: 'Events seen', value: fmtInt(eventsSeen), note: `${fmtInt(ignored)} ignored`, title: tooltips.eventsSeen },
    { label: 'Raw p95', value: fmtMs(rawCycle.p95), note: `max ${fmtMs(rawCycle.max)}`, title: tooltips.rawP95 },
    { label: 'Normalized p95', value: fmtMs(normalizedCycle.p95), note: `max ${fmtMs(normalizedCycle.max)}`, title: tooltips.normalizedP95 },
    { label: 'Cooldown attempts', value: fmtInt(cooldownAttempts), note: `msg ${fmtInt(cooldownMessageAttempts)} - pb ${fmtInt(cooldownPostbackAttempts)}`, title: tooltips.cooldownAttempts },
    { label: 'Worker parallelism', value: fmtInt(gaugeMax(workerInstancesData, 'worker.raw.parallelism')), note: 'per worker instance', title: tooltips.workerParallelismStat }
  ]);

  renderQueueTable(queues);
  renderWorkerInstances(workerInstancesData);

  setMessage(actionMessage,
    payload.votingStarted
      ? (activeShowId ? `Voting is enabled for show '${activeShowId}'. Operators can monitor traffic, queue pressure, and live counters here.` : 'VotingStarted is ON, but ActiveShowId is not configured. Voting flow will stay blocked until a show is set.')
      : 'Voting is disabled. Regular vote traffic should now be dropped except the #forgetme flow.',
    'success');
}

async function updateVoting(started) {
  updateInFlight = true;
  applyVotingState(started, activeShowIdInput.value.trim());
  setMessage(actionMessage, `Updating runtime state to VotingStarted=${started}...`);

  try {
    const payload = await fetchJson('/admin/api/voting', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ votingStarted: started, activeShowId: activeShowIdInput.value.trim() || null })
    }, true);

    if (payload.unauthorized) {
      showLogin('Session expired. Please sign in again.');
      return;
    }

    renderDashboard(payload);
  } catch (error) {
    setMessage(actionMessage, `Failed to update voting state: ${error.message}`, 'error');
  } finally {
    updateInFlight = false;
    await loadDashboard();
  }
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
    void loadDashboard();
  }, Number.isFinite(interval) ? interval : 2000);
}

async function handleLogin(event) {
  event.preventDefault();
  loginButton.disabled = true;
  setMessage(loginMessage, 'Signing in...');

  try {
    const payload = await fetchJson('/admin/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: usernameInput.value,
        password: passwordInput.value
      })
    });

    usernameInput.value = payload.username || usernameInput.value;
    passwordInput.value = '';
    setMessage(loginMessage, 'Sign-in successful.', 'success');
    showDashboard(payload.username);
    scheduleRefresh();
    await loadDashboard();
  } catch (error) {
    setMessage(loginMessage, `Sign-in failed: ${error.message}`, 'error');
  } finally {
    loginButton.disabled = false;
  }
}

async function handleLogout() {
  logoutButton.disabled = true;
  try {
    await fetchJson('/admin/api/logout', { method: 'POST' });
  } catch {
    // ignore and force session reset locally
  } finally {
    logoutButton.disabled = false;
    paused = false;
    pauseButton.textContent = 'Pause';
    showLogin('Signed out.');
    scheduleRefresh();
  }
}

loginForm.addEventListener('submit', event => {
  void handleLogin(event);
});
refreshButton.addEventListener('click', () => {
  void loadDashboard();
});
pauseButton.addEventListener('click', () => {
  paused = !paused;
  pauseButton.textContent = paused ? 'Resume' : 'Pause';
  pauseButton.title = paused ? 'ავტო-განახლების გაგრძელება.' : tooltips.pause;
  scheduleRefresh();
});
refreshSelect.addEventListener('change', scheduleRefresh);
logoutButton.addEventListener('click', () => {
  void handleLogout();
});
turnOnButton.addEventListener('click', () => {
  void updateVoting(true);
});
turnOffButton.addEventListener('click', () => {
  void updateVoting(false);
});

applyStaticTitles();

(async () => {
  const authenticated = await loadSession();
  scheduleRefresh();
  if (authenticated) {
    await loadDashboard();
  }
})();



