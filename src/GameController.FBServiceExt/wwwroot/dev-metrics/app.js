const refreshSelect = document.getElementById('refreshSelect');
const refreshButton = document.getElementById('refreshButton');
const pauseButton = document.getElementById('pauseButton');
const statusText = document.getElementById('statusText');
const generatedAt = document.getElementById('generatedAt');
const lastRefresh = document.getElementById('lastRefresh');
const apiInstanceCount = document.getElementById('apiInstanceCount');
const workerInstanceCount = document.getElementById('workerInstanceCount');
const apiSummary = document.getElementById('apiSummary');
const rawSummary = document.getElementById('rawSummary');
const processorSummary = document.getElementById('processorSummary');
const queueSummary = document.getElementById('queueSummary');
const queueRows = document.getElementById('queueRows');
const apiInstances = document.getElementById('apiInstances');
const workerInstances = document.getElementById('workerInstances');

let timerId = null;
let paused = false;
let inFlight = false;

const tooltips = {
  apiRequests: 'API-\u10DB \u10DB\u10D8\u10E6\u10D4\u10D1\u10E3\u10DA\u10D8 webhook request-\u10D4\u10D1\u10D8\u10E1 \u10EF\u10D0\u10DB\u10D8.',
  apiMessagingEvents: '\u10D0\u10DB request-\u10D4\u10D1\u10E8\u10D8 \u10DB\u10DD\u10E1\u10E3\u10DA\u10D8 messaging event-\u10D4\u10D1\u10D8.',
  api200Rate: '\u10E0\u10D0\u10DB\u10D3\u10D4\u10DC request-\u10D6\u10D4 \u10D3\u10D0\u10D1\u10E0\u10E3\u10DC\u10D3\u10D0 200 OK.',
  apiAckP95: 'ACK p95: 95% request \u10D0\u10DB \u10D3\u10E0\u10DD\u10D6\u10D4 \u10DC\u10D0\u10D9\u10DA\u10D4\u10D1\u10E8\u10D8 \u10E1\u10E0\u10E3\u10DA\u10D3\u10D4\u10D1\u10D0.',
  apiAckP99: 'ACK p99: tail latency-ის მაჩვენებელი.',
  apiBodyReadP95: 'request body-ის წაკითხვის p95 დრო.',
  apiSignatureP95: 'signature validation-ის p95 დრო.',
  apiAcceptP95: 'controller-იდან ingress accept/publish path-ის p95 დრო.',
  apiPublishP95: 'RabbitMQ publish total path-ის p95 დრო.',
  apiChannelWaitP95: 'publisher channel-ის ლოდინის p95 დრო.',
  apiInflight: 'ამ მომენტში API-ში ერთდროულად დამუშავებული webhook request-ები.',
  apiPublisherPool: 'რამდენი publisher channel არის გამოყენებული და რამდენია თავისუფალი.',
  apiFailures: 'client abort-ები, publish failure-ები და unhandled exception-ები.',
  rawEnvelopes: 'queue-\u10D3\u10D0\u10DC \u10D0\u10E6\u10D4\u10D1\u10E3\u10DA\u10D8 raw envelope-\u10D4\u10D1\u10D8.',
  rawNormalizedEvents: 'raw envelope-\u10D4\u10D1\u10D8\u10D3\u10D0\u10DC \u10DB\u10D8\u10E6\u10D4\u10D1\u10E3\u10DA\u10D8 normalized event-\u10D4\u10D1\u10D8.',
  rawBatchP95: 'raw cycle-\u10E8\u10D8 batch-\u10E8\u10D8 \u10E0\u10D0\u10DB\u10D3\u10D4\u10DC event \u10DB\u10DD\u10D3\u10D8\u10E1.',
  rawParallelism: '\u10E0\u10D0\u10DB\u10D3\u10D4\u10DC\u10D8 raw loop \u10DB\u10E3\u10E8\u10D0\u10DD\u10D1\u10E1 \u10D4\u10E0\u10D7 worker process-\u10E8\u10D8.',
  rawCycleP95: 'raw normalizer cycle-\u10D8\u10E1 p95.',
  rawFailures: 'raw loop-\u10D8\u10E1 \u10E8\u10D4\u10EA\u10D3\u10DD\u10DB\u10D4\u10D1\u10D8.',
  processorEventsSeen: 'processor-\u10DB \u10DC\u10D0\u10DC\u10D0\u10EE\u10D8 normalized event-\u10D4\u10D1\u10D8.',
  processorQueueReceipts: 'normalized queue-\u10D3\u10D0\u10DC \u10D0\u10E6\u10D4\u10D1\u10E3\u10DA\u10D8 event-\u10D4\u10D1\u10D8.',
  processorBusinessFlow: 'state transition-\u10D4\u10D1\u10D8\u10E1 \u10EF\u10D0\u10DB\u10D8.',
  processorIgnored: '\u10D3\u10D0\u10D8\u10D8\u10D2\u10DC\u10DD\u10E0\u10D4\u10D1\u10E3\u10DA\u10D8 event-\u10D4\u10D1\u10D8.',
  normalizedCycleP95: 'normalized processor cycle-\u10D8\u10E1 p95.',
  queueStatus: 'queue pressure-\u10D8\u10E1 \u10D8\u10DC\u10E2\u10D4\u10E0\u10DE\u10E0\u10D4\u10E2\u10D0\u10EA\u10D8\u10D0.',
  readyBacklog: 'queue-\u10E8\u10D8 \u10DB\u10DD\u10DB\u10DA\u10DD\u10D3\u10D8\u10DC\u10D4 message-\u10D4\u10D1\u10D8.',
  unacked: 'consumer-\u10D8\u10E1 \u10EE\u10D4\u10DA\u10E8\u10D8 \u10DB\u10E7\u10DD\u10E4\u10D8 message-\u10D4\u10D1\u10D8.',
  publishAckRate: 'publish/s \u10D3\u10D0 ack/s \u10E8\u10D4\u10D3\u10D0\u10E0\u10D4\u10D1\u10D0.',
  queueName: 'queue-\u10D8\u10E1 \u10E1\u10D0\u10EE\u10D4\u10DA\u10D8.',
  queueConsumers: '\u10D0\u10DB queue-\u10D8\u10E1 consumer-\u10D4\u10D1\u10D8.',
  queueReady: '\u10DB\u10DD\u10DB\u10DA\u10DD\u10D3\u10D8\u10DC\u10D4 message-\u10D4\u10D1\u10D8.',
  queueUnacked: '\u10D0\u10E6\u10D4\u10D1\u10E3\u10DA\u10D8, \u10DB\u10D0\u10D2\u10E0\u10D0\u10DB \u10EF\u10D4\u10E0 \u10D3\u10D0\u10E3\u10D3\u10D0\u10E1\u10E2\u10E3\u10E0\u10D4\u10D1\u10D4\u10DA\u10D8 message-\u10D4\u10D1\u10D8.',
  queuePublishRate: '\u10D0\u10DB queue-\u10E8\u10D8 publish \u10E1\u10D8\u10E9\u10E5\u10D0\u10E0\u10D4.',
  queueAckRate: '\u10D0\u10DB queue-\u10E8\u10D8 ack \u10E1\u10D8\u10E9\u10E5\u10D0\u10E0\u10D4.',
  instanceRequests: '\u10D0\u10DB instance-\u10D6\u10D4 \u10DC\u10D0\u10DC\u10D0\u10EE\u10D8 request/event-\u10D4\u10D1\u10D8.',
  instanceAckP95: '\u10D0\u10DB instance-\u10D8\u10E1 ACK p95.',
  instanceRawP95: '\u10D0\u10DB instance-\u10D8\u10E1 raw cycle p95.',
  instanceNormalizedP95: '\u10D0\u10DB instance-\u10D8\u10E1 normalized cycle p95.',
  instanceOptionsSent: 'OptionsSent \u10D2\u10D0\u10D3\u10D0\u10E1\u10D5\u10DA\u10D4\u10D1\u10D8\u10E1 \u10E0\u10D0\u10DD\u10D3\u10D4\u10DC\u10DD\u10D1\u10D0.',
  instanceVotesAccepted: '\u10D3\u10D0\u10D3\u10D0\u10E1\u10E2\u10E3\u10E0\u10D4\u10D1\u10E3\u10DA\u10D8 \u10EE\u10DB\u10D4\u10D1\u10D8\u10E1 \u10E0\u10D0\u10DD\u10D3\u10D4\u10DC\u10DD\u10D1\u10D0.',
  instanceIdentifier: 'machine, role \u10D3\u10D0 process id \u10D4\u10E0\u10D7\u10D0\u10D3.',
  instanceMeta: 'process id, environment \u10D3\u10D0 \u10D1\u10DD\u10DA\u10DD refresh \u10D3\u10E0\u10DD.'
};

const staticTitles = [
  ['.hero .sub', '\u10D4\u10E1 \u10D0\u10E0\u10D8\u10E1 \u10DA\u10DD\u10D9\u10D0\u10DA\u10E3\u10E0\u10D8 realtime \u10D3\u10D0\u10E4\u10D0 K6 \u10E2\u10D4\u10E1\u10E2\u10D4\u10D1\u10D8\u10E1\u10D7\u10D5\u10D8\u10E1.'],
  ['label[for="refreshSelect"]', 'refresh \u10D8\u10DC\u10E2\u10D4\u10E0\u10D5\u10D0\u10DA\u10D8.'],
  ['#refreshSelect', 'refresh \u10D8\u10DC\u10E2\u10D4\u10E0\u10D5\u10D0\u10DA\u10D8.'],
  ['#refreshButton', '\u10D0\u10EE\u10DA\u10D0\u10D5\u10D4 \u10D2\u10D0\u10DC\u10D0\u10D0\u10EE\u10DA\u10D4.'],
  ['#pauseButton', 'auto-refresh pause/resume.'],
  ['.meta .meta-row:nth-of-type(1) .meta-label', 'backend metrics endpoint-\u10D8\u10E1 \u10E1\u10E2\u10D0\u10E2\u10E3\u10E1\u10D8.'],
  ['.meta .meta-row:nth-of-type(2) .meta-label', 'snapshot \u10E8\u10D4\u10E5\u10DB\u10DC\u10D8\u10E1 \u10D3\u10E0\u10DD.'],
  ['.meta .meta-row:nth-of-type(3) .meta-label', 'UI refresh-\u10D8\u10E1 \u10D3\u10E0\u10DD.'],
  ['.meta .meta-row:nth-of-type(4) .meta-label', '\u10D0\u10E5\u10E2\u10D8\u10E3\u10E0\u10D8 API instance-\u10D4\u10D1\u10D8.'],
  ['.meta .meta-row:nth-of-type(5) .meta-label', '\u10D0\u10E5\u10E2\u10D8\u10E3\u10E0\u10D8 Worker instance-\u10D4\u10D1\u10D8.'],
  ['.summary-grid .card:nth-of-type(1) h2', 'API ingress-\u10D8\u10E1 \u10DB\u10D0\u10E9\u10D5\u10D4\u10DC\u10D4\u10D1\u10DA\u10D4\u10D1\u10D8.'],
  ['.summary-grid .card:nth-of-type(2) h2', 'Raw worker-\u10D8\u10E1 \u10DB\u10D0\u10E9\u10D5\u10D4\u10DC\u10D4\u10D1\u10DA\u10D4\u10D1\u10D8.'],
  ['.summary-grid .card:nth-of-type(3) h2', 'Worker processor-\u10D8\u10E1 \u10DB\u10D0\u10E9\u10D5\u10D4\u10DC\u10D4\u10D1\u10DA\u10D4\u10D1\u10D8.'],
  ['.queue-grid .card:nth-of-type(1) h2', 'RabbitMQ queue pressure-\u10D8\u10E1 \u10E1\u10E3\u10E0\u10D0\u10D7\u10D8.'],
  ['.queue-grid .card:nth-of-type(2) h2', 'RabbitMQ queue-\u10D4\u10D1\u10D8\u10E1 snapshot.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(1)', 'queue-\u10D8\u10E1 \u10E1\u10D0\u10EE\u10D4\u10DA\u10D8.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(2)', 'queue pressure-\u10D8\u10E1 \u10E1\u10E2\u10D0\u10E2\u10E3\u10E1\u10D8.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(3)', 'consumer-\u10D4\u10D1\u10D8\u10E1 \u10E0\u10D0\u10DD\u10D3\u10D4\u10DC\u10DD\u10D1\u10D0.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(4)', 'ready backlog.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(5)', 'unacked in-flight message-\u10D4\u10D1\u10D8.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(6)', 'publish \u10E1\u10D8\u10E9\u10E5\u10D0\u10E0\u10D4.'],
  ['.queue-grid .card:nth-of-type(2) thead th:nth-of-type(7)', 'ack \u10E1\u10D8\u10E9\u10E5\u10D0\u10E0\u10D4.'],
  ['.instances-grid .card:nth-of-type(1) h2', 'API instance-\u10D4\u10D1\u10D8\u10E1 runtime snapshot.'],
  ['.instances-grid .card:nth-of-type(2) h2', 'Worker instance-\u10D4\u10D1\u10D8\u10E1 runtime snapshot.']
];

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function hintLabel(text, tooltip) {
  const label = escapeHtml(text);
  if (!tooltip) {
    return label;
  }

  return `<span class="hint" title="${escapeHtml(tooltip)}">${label}<span class="hint-dot">?</span></span>`;
}

function applyStaticTitles() {
  for (const [selector, title] of staticTitles) {
    document.querySelectorAll(selector).forEach(element => {
      element.title = title;
    });
  }
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
  if (!total) return '0.00%';
  return `${((part / total) * 100).toFixed(2)}%`;
}

function fmtAgo(utc) {
  if (!utc) return '-';
  const diffMs = Date.now() - new Date(utc).getTime();
  if (diffMs < 1000) return 'now';
  return `${Math.round(diffMs / 1000)}s ago`;
}

function distribution(instances, name) {
  const samples = instances
    .map(instance => instance.distributions?.[name])
    .filter(Boolean);

  if (!samples.length) {
    return { sampleCount: 0, average: 0, p50: 0, p95: 0, p99: 0, max: 0 };
  }

  const totalCount = samples.reduce((sum, item) => sum + Number(item.sampleCount || 0), 0);
  const weightedAverage = totalCount > 0
    ? samples.reduce((sum, item) => sum + (Number(item.average || 0) * Number(item.sampleCount || 0)), 0) / totalCount
    : 0;

  return {
    sampleCount: totalCount,
    average: weightedAverage,
    p50: Math.max(...samples.map(item => Number(item.p50 || 0))),
    p95: Math.max(...samples.map(item => Number(item.p95 || 0))),
    p99: Math.max(...samples.map(item => Number(item.p99 || 0))),
    max: Math.max(...samples.map(item => Number(item.max || 0)))
  };
}

function counter(instances, name) {
  return instances.reduce((sum, instance) => sum + Number(instance.counters?.[name] || 0), 0);
}

function gaugeMax(instances, name) {
  return instances.reduce((max, instance) => Math.max(max, Number(instance.gauges?.[name] || 0)), 0);
}

function classifyQueue(queue) {
  const consumers = Number(queue.consumers || 0);
  const ready = Number(queue.ready || 0);
  const unacked = Number(queue.unacknowledged || 0);
  const publishRate = Number(queue.publishRate || 0);
  const ackRate = Number(queue.ackRate || 0);

  if (consumers === 0 && (ready > 0 || unacked > 0)) {
    return {
      label: 'Critical',
      className: 'danger',
      explanation: 'backlog \u10D0\u10E0\u10D8\u10E1, \u10DB\u10D0\u10D2\u10E0\u10D0\u10DB consumer \u10D0\u10E0 \u10E9\u10D0\u10DC\u10E1.'
    };
  }

  if (ready === 0 && unacked === 0) {
    return {
      label: 'Stable',
      className: 'steady',
      explanation: 'queue \u10E1\u10E2\u10D0\u10D1\u10D8\u10DA\u10E3\u10E0\u10D8\u10D0; backlog \u10D0\u10E0 \u10E9\u10D0\u10DC\u10E1.'
    };
  }

  if (ready === 0 && unacked > 0) {
    return {
      label: 'Draining',
      className: 'info',
      explanation: 'in-flight \u10D3\u10D0\u10E2\u10D5\u10D8\u10E0\u10D7\u10D5\u10D0 \u10DB\u10E3\u10E8\u10D0\u10D5\u10D3\u10D4\u10D1\u10D0.'
    };
  }

  if (ready >= 1000 || (ready >= 250 && publishRate > 0 && ackRate < publishRate * 0.85)) {
    return {
      label: 'Critical',
      className: 'danger',
      explanation: 'backlog \u10D8\u10D6\u10E0\u10D3\u10D4\u10D1\u10D0; publish ack-\u10E1 \u10E3\u10E1\u10EC\u10E0\u10D4\u10D1\u10E1.'
    };
  }

  if (ackRate > 0 && ackRate >= publishRate * 0.95) {
    return {
      label: 'Draining',
      className: 'info',
      explanation: 'backlog \u10D8\u10EC\u10DB\u10D8\u10DC\u10D3\u10D4\u10D1\u10D0; ack publish-\u10E1 \u10D4\u10EC\u10D4\u10D5\u10D0.'
    };
  }

  return {
    label: 'Transient',
    className: 'warn',
    explanation: '\u10D3\u10E0\u10DD\u10D4\u10D1\u10D8\u10D7\u10D8 queue pressure.'
  };
}

function overallQueueStatus(queues) {
  const order = { Stable: 0, Transient: 1, Draining: 2, Critical: 3 };
  return queues
    .map(classifyQueue)
    .sort((left, right) => (order[right.label] ?? 0) - (order[left.label] ?? 0))[0] || {
      label: 'Stable',
      className: 'steady',
      explanation: 'queue-\u10D4\u10D1\u10D8 \u10E1\u10E2\u10D0\u10D1\u10D8\u10DA\u10E3\u10E0\u10D8\u10D0.'
    };
}

function renderStatGrid(container, stats) {
  container.innerHTML = stats.map(stat => `
    <div class="stat">
      <div class="stat-label">${hintLabel(stat.label, stat.tooltip)}</div>
      <div class="stat-value ${stat.className || ''}" title="${escapeHtml(stat.valueTooltip || stat.tooltip || '')}">${escapeHtml(stat.value)}</div>
      <div class="stat-note" title="${escapeHtml(stat.noteTooltip || stat.tooltip || '')}">${escapeHtml(stat.note || '') || '&nbsp;'}</div>
    </div>`).join('');
}

function renderQueueRows(queues) {
  if (!queues.length) {
    queueRows.innerHTML = '<tr><td colspan="7" class="empty">No queue data.</td></tr>';
    return;
  }

  queueRows.innerHTML = queues.map(queue => {
    const status = classifyQueue(queue);
    return `
      <tr>
        <td title="${escapeHtml(tooltips.queueName)}">${escapeHtml(queue.name)}</td>
        <td title="${escapeHtml(status.explanation)}"><span class="status-pill ${status.className}">${escapeHtml(status.label)}</span></td>
        <td title="${escapeHtml(tooltips.queueConsumers)}">${fmtInt(queue.consumers)}</td>
        <td title="${escapeHtml(tooltips.queueReady)}">${fmtInt(queue.ready)}</td>
        <td title="${escapeHtml(tooltips.queueUnacked)}">${fmtInt(queue.unacknowledged)}</td>
        <td title="${escapeHtml(tooltips.queuePublishRate)}">${fmtRate(queue.publishRate)}</td>
        <td title="${escapeHtml(tooltips.queueAckRate)}">${fmtRate(queue.ackRate)}</td>
      </tr>`;
  }).join('');
}

function renderInstances(container, instances, role) {
  if (!instances.length) {
    container.innerHTML = `<div class="empty">No ${role.toLowerCase()} instances reporting yet.</div>`;
    return;
  }

  container.innerHTML = instances.map(instance => {
    const ack = distribution([instance], 'api.webhook.ack_ms');
    const raw = distribution([instance], 'worker.raw.cycle_ms');
    const normalized = distribution([instance], 'worker.normalized.cycle_ms');
    return `
      <div class="instance-card">
        <div class="instance-title" title="${escapeHtml(tooltips.instanceIdentifier)}">${escapeHtml(instance.instanceId)}</div>
        <div class="muted" title="${escapeHtml(tooltips.instanceMeta)}">Process ${fmtInt(instance.processId)} - ${escapeHtml(instance.environmentName)} - updated ${escapeHtml(fmtAgo(instance.updatedAtUtc))}</div>
        <table>
          <tbody>
            <tr><td title="${escapeHtml(tooltips.instanceRequests)}">Requests / events</td><td title="${escapeHtml(tooltips.instanceRequests)}">${fmtInt(counter([instance], 'api.webhook.requests_total') || counter([instance], 'worker.processor.events_seen'))}</td></tr>
            <tr><td title="${escapeHtml(tooltips.instanceAckP95)}">ACK p95</td><td title="${escapeHtml(tooltips.instanceAckP95)}">${fmtMs(ack.p95)}</td></tr>
            <tr><td title="${escapeHtml(tooltips.instanceRawP95)}">Raw cycle p95</td><td title="${escapeHtml(tooltips.instanceRawP95)}">${fmtMs(raw.p95)}</td></tr>
            <tr><td title="${escapeHtml(tooltips.instanceNormalizedP95)}">Normalized cycle p95</td><td title="${escapeHtml(tooltips.instanceNormalizedP95)}">${fmtMs(normalized.p95)}</td></tr>
            <tr><td title="${escapeHtml(tooltips.instanceOptionsSent)}">OptionsSent</td><td title="${escapeHtml(tooltips.instanceOptionsSent)}">${fmtInt(counter([instance], 'worker.processor.options_sent'))}</td></tr>
            <tr><td title="${escapeHtml(tooltips.instanceVotesAccepted)}">Votes accepted</td><td title="${escapeHtml(tooltips.instanceVotesAccepted)}">${fmtInt(counter([instance], 'worker.processor.vote_accepted'))}</td></tr>
          </tbody>
        </table>
      </div>`;
  }).join('');
}

function renderDashboard(data) {
  const api = data.apiInstances || [];
  const workers = data.workerInstances || [];
  const queues = data.queues || [];

  apiInstanceCount.textContent = fmtInt(api.length);
  workerInstanceCount.textContent = fmtInt(workers.length);
  generatedAt.textContent = data.generatedAtUtc ? new Date(data.generatedAtUtc).toLocaleTimeString() : '-';

  const apiRequests = counter(api, 'api.webhook.requests_total');
  const apiMessagingEvents = counter(api, 'api.webhook.messaging_events_total');
  const apiSuccess = counter(api, 'api.webhook.status.200');
  const apiAck = distribution(api, 'api.webhook.ack_ms');
  const apiBodyRead = distribution(api, 'api.webhook.body_read_ms');
  const apiSignature = distribution(api, 'api.webhook.signature_validation_ms');
  const apiAccept = distribution(api, 'api.webhook.accept_ms');
  const apiPublish = distribution(api, 'api.ingress.publish_total_ms');
  const apiChannelWait = distribution(api, 'api.ingress.channel_rent_wait_ms');
  const apiInflightCurrent = gaugeMax(api, 'api.webhook.inflight');
  const apiPublisherLeased = gaugeMax(api, 'api.ingress.publisher_channels_leased');
  const apiPublisherAvailable = gaugeMax(api, 'api.ingress.publisher_channels_available');
  const apiPublisherPoolSize = gaugeMax(api, 'api.ingress.publisher_pool_size');
  const apiClientAborts = counter(api, 'api.webhook.client_aborts');
  const apiPublishFailures = counter(api, 'api.ingress.publish_failures');
  const apiExceptions = counter(api, 'api.webhook.exceptions');
  renderStatGrid(apiSummary, [
    {
      label: 'Requests',
      tooltip: tooltips.apiRequests,
      value: fmtInt(apiRequests),
      note: `${fmtInt(apiMessagingEvents)} messaging events`,
      noteTooltip: tooltips.apiMessagingEvents
    },
    {
      label: '200 Rate',
      tooltip: tooltips.api200Rate,
      value: fmtPercent(apiSuccess, apiRequests),
      note: `${fmtInt(apiSuccess)} OK`,
      noteTooltip: tooltips.api200Rate,
      className: apiSuccess === apiRequests ? 'info' : 'warn'
    },
    {
      label: 'ACK p95',
      tooltip: tooltips.apiAckP95,
      value: fmtMs(apiAck.p95),
      note: `p99 ${fmtMs(apiAck.p99)}`,
      noteTooltip: tooltips.apiAckP99
    },
    {
      label: 'Body / Sig',
      tooltip: tooltips.apiBodyReadP95,
      value: fmtMs(apiBodyRead.p95),
      note: `sig ${fmtMs(apiSignature.p95)}`,
      noteTooltip: tooltips.apiSignatureP95
    },
    {
      label: 'Accept / Publish',
      tooltip: tooltips.apiAcceptP95,
      value: fmtMs(apiAccept.p95),
      note: `pub ${fmtMs(apiPublish.p95)}`,
      noteTooltip: tooltips.apiPublishP95
    },
    {
      label: 'Channel wait',
      tooltip: tooltips.apiChannelWaitP95,
      value: fmtMs(apiChannelWait.p95),
      note: `inflight ${fmtInt(apiInflightCurrent)}`,
      noteTooltip: tooltips.apiInflight
    },
    {
      label: 'Pool',
      tooltip: tooltips.apiPublisherPool,
      value: `${fmtInt(apiPublisherLeased)}/${fmtInt(apiPublisherPoolSize || apiPublisherLeased + apiPublisherAvailable)}`,
      note: `${fmtInt(apiPublisherAvailable)} free`,
      noteTooltip: tooltips.apiPublisherPool
    },
    {
      label: 'Failures',
      tooltip: tooltips.apiFailures,
      value: fmtInt(apiClientAborts + apiPublishFailures + apiExceptions),
      note: `abort ${fmtInt(apiClientAborts)} - pub ${fmtInt(apiPublishFailures)} - ex ${fmtInt(apiExceptions)}`,
      noteTooltip: tooltips.apiFailures,
      className: (apiClientAborts + apiPublishFailures + apiExceptions) > 0 ? 'warn' : 'info'
    }
  ]);

  const rawConsumed = counter(workers, 'worker.raw.envelopes_received');
  const rawNormalized = counter(workers, 'worker.raw.events_normalized');
  const rawCycle = distribution(workers, 'worker.raw.cycle_ms');
  const rawBatch = distribution(workers, 'worker.raw.batch_size');
  const rawParallelism = gaugeMax(workers, 'worker.raw.parallelism');
  renderStatGrid(rawSummary, [
    {
      label: 'Raw envelopes',
      tooltip: tooltips.rawEnvelopes,
      value: fmtInt(rawConsumed),
      note: `${fmtInt(rawNormalized)} normalized events`,
      noteTooltip: tooltips.rawNormalizedEvents
    },
    {
      label: 'Batch p95',
      tooltip: tooltips.rawBatchP95,
      value: rawParallelism ? fmtInt(rawBatch.p95) : '0',
      note: `parallelism ${fmtInt(rawParallelism)}`,
      noteTooltip: tooltips.rawParallelism
    },
    {
      label: 'Cycle p95',
      tooltip: tooltips.rawCycleP95,
      value: fmtMs(rawCycle.p95),
      note: `${fmtInt(counter(workers, 'worker.raw.failures'))} failures`,
      noteTooltip: tooltips.rawFailures
    }
  ]);

  const processorSeen = counter(workers, 'worker.processor.events_seen');
  const accepted = counter(workers, 'worker.processor.vote_accepted') + counter(workers, 'worker.processor.vote_accepted_reconciled');
  const ignored = counter(workers, 'worker.processor.ignored') + counter(workers, 'worker.processor.cooldown_ignored');
  const normalizedCycle = distribution(workers, 'worker.normalized.cycle_ms');
  renderStatGrid(processorSummary, [
    {
      label: 'Events seen',
      tooltip: tooltips.processorEventsSeen,
      value: fmtInt(processorSeen),
      note: `${fmtInt(counter(workers, 'worker.normalized.events_received'))} queue receipts`,
      noteTooltip: tooltips.processorQueueReceipts
    },
    {
      label: 'Business flow',
      tooltip: tooltips.processorBusinessFlow,
      value: fmtInt(accepted),
      note: `${fmtInt(counter(workers, 'worker.processor.options_sent'))} options - ${fmtInt(counter(workers, 'worker.processor.confirmation_pending'))} confirmations`
    },
    {
      label: 'Ignored / p95',
      tooltip: tooltips.processorIgnored,
      value: fmtInt(ignored),
      note: `${fmtMs(normalizedCycle.p95)} cycle p95`,
      noteTooltip: tooltips.normalizedCycleP95
    }
  ]);

  const totalReady = queues.reduce((sum, queue) => sum + Number(queue.ready || 0), 0);
  const totalUnacked = queues.reduce((sum, queue) => sum + Number(queue.unacknowledged || 0), 0);
  const totalConsumers = queues.reduce((sum, queue) => sum + Number(queue.consumers || 0), 0);
  const totalPublishRate = queues.reduce((sum, queue) => sum + Number(queue.publishRate || 0), 0);
  const totalAckRate = queues.reduce((sum, queue) => sum + Number(queue.ackRate || 0), 0);
  const queueStatus = overallQueueStatus(queues);
  renderStatGrid(queueSummary, [
    {
      label: 'Queue status',
      tooltip: tooltips.queueStatus,
      value: queueStatus.label,
      valueTooltip: queueStatus.explanation,
      note: queueStatus.explanation,
      className: queueStatus.className
    },
    {
      label: 'Ready backlog',
      tooltip: tooltips.readyBacklog,
      value: fmtInt(totalReady),
      note: totalReady > 0 ? 'Queue-\u10E8\u10D8 backlog \u10D2\u10E0\u10DD\u10D5\u10D3\u10D4\u10D1\u10D0' : 'Ready backlog \u10D0\u10E0 \u10D0\u10E0\u10D8\u10E1',
      className: totalReady > 0 ? 'warn' : 'info'
    },
    {
      label: 'Unacked',
      tooltip: tooltips.unacked,
      value: fmtInt(totalUnacked),
      note: `Consumers ${fmtInt(totalConsumers)}`,
      noteTooltip: tooltips.queueConsumers
    },
    {
      label: 'Publish / Ack',
      tooltip: tooltips.publishAckRate,
      value: fmtRate(totalPublishRate),
      note: `ack ${fmtRate(totalAckRate)}`,
      noteTooltip: tooltips.publishAckRate
    }
  ]);

  renderQueueRows(queues);
  renderInstances(apiInstances, api, 'API');
  renderInstances(workerInstances, workers, 'Worker');
}

async function refreshMetrics() {
  if (paused || inFlight) {
    return;
  }

  inFlight = true;
  statusText.textContent = 'Loading';

  try {
    const response = await fetch('/dev/metrics/api', { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      const payload = await response.text();
      throw new Error(payload || `HTTP ${response.status}`);
    }

    const data = await response.json();
    renderDashboard(data);
    lastRefresh.textContent = new Date().toLocaleTimeString();
    statusText.textContent = 'Live';
  } catch (error) {
    statusText.textContent = `Error: ${error.message || error}`;
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
    timerId = setInterval(refreshMetrics, interval);
  }
}

refreshButton.addEventListener('click', refreshMetrics);
refreshSelect.addEventListener('change', () => {
  applyTimer();
  refreshMetrics();
});
pauseButton.addEventListener('click', () => {
  paused = !paused;
  pauseButton.textContent = paused ? 'Resume' : 'Pause';
  statusText.textContent = paused ? 'Paused' : 'Live';
  applyTimer();
});

applyStaticTitles();
applyTimer();
refreshMetrics();

