import http from 'k6/http';
import { check, fail } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { buildMessengerWebhookRequest } from '../lib/messengerWebhook.js';

const baseUrl = (__ENV.BASE_URL || 'http://localhost:5277').replace(/\/$/, '');
const endpoint = `${baseUrl}/api/facebook/webhooks`;
const readyUrl = `${baseUrl}${__ENV.READY_PATH || '/health/ready'}`;
const targetMessagesPerSecond = Math.max(1, Number(__ENV.TARGET_MESSAGES_PER_SEC || 250));
const eventsPerRequest = Math.max(1, Number(__ENV.EVENTS_PER_REQUEST || 1));
const requestRate = Math.ceil(targetMessagesPerSecond / eventsPerRequest);
const duration = __ENV.TEST_DURATION || '60s';
const preAllocatedVUs = Math.max(requestRate * 2, Number(__ENV.PRE_ALLOCATED_VUS || 300));
const maxVUs = Math.max(preAllocatedVUs, Number(__ENV.MAX_VUS || 1500));
const ackP95Ms = Math.max(1, Number(__ENV.ACK_P95_MS || 250));
const ackP99Ms = Math.max(ackP95Ms, Number(__ENV.ACK_P99_MS || 500));
const requestTimeout = __ENV.REQUEST_TIMEOUT || '10s';
const requireReady = (__ENV.REQUIRE_READY || 'true').toLowerCase() !== 'false';
const pageId = __ENV.PAGE_ID || 'PAGE_ID_PERF';
const messageText = __ENV.MESSAGE_TEXT || 'GET_STARTED';
const appSecret = __ENV.APP_SECRET || '';

const ackDuration = new Trend('webhook_ack_duration', true);
const sentMessages = new Counter('webhook_messages_sent');
const unexpectedStatus = new Rate('webhook_unexpected_status');
const okStatus = new Rate('webhook_status_200');

export const options = {
    discardResponseBodies: true,
    insecureSkipTLSVerify: true,
    noConnectionReuse: false,
    maxRedirects: 0,
    scenarios: {
        ack_250rps: {
            executor: 'constant-arrival-rate',
            rate: requestRate,
            timeUnit: '1s',
            duration,
            preAllocatedVUs,
            maxVUs,
        },
    },
    thresholds: {
        http_req_failed: ['rate<0.01'],
        dropped_iterations: ['count==0'],
        checks: ['rate>0.99'],
        webhook_unexpected_status: ['rate<0.01'],
        webhook_status_200: ['rate>0.99'],
        http_req_duration: [`p(95)<${ackP95Ms}`, `p(99)<${ackP99Ms}`],
        webhook_ack_duration: [`p(95)<${ackP95Ms}`, `p(99)<${ackP99Ms}`],
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
    if (!requireReady) {
        return;
    }

    const response = http.get(readyUrl, { timeout: '5s', tags: { probe: 'ready' } });
    if (response.status !== 200) {
        fail(`Readiness check failed. GET ${readyUrl} => ${response.status}`);
    }
}

export default function () {
    const request = buildMessengerWebhookRequest({
        eventsPerRequest,
        pageId,
        messageText,
        appSecret,
    });

    const response = http.post(endpoint, request.body, {
        headers: request.headers,
        timeout: requestTimeout,
        tags: {
            endpoint: 'facebook_webhook',
            phase: 'ack',
        },
    });

    ackDuration.add(response.timings.duration);
    sentMessages.add(request.eventCount);
    const is200 = response.status === 200;
    okStatus.add(is200);
    unexpectedStatus.add(!is200);

    check(response, {
        'webhook returns 200': (res) => res.status === 200,
    });
}
