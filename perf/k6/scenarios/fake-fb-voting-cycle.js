import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import exec from 'k6/execution';
import { buildPostbackWebhookRequest, buildTextWebhookRequest } from '../lib/messengerWebhook.js';
import { classifyTextMessage, clearFakeMetaMessages, isCarouselMessage, isConfirmationMessage, isTextMessage, pollForNextMessage } from '../lib/fakeMetaMessenger.js';

const baseUrl = (__ENV.BASE_URL || 'http://localhost:5277').replace(/\/$/, '');
const endpoint = `${baseUrl}/api/facebook/webhooks`;
const readyUrl = `${baseUrl}${__ENV.READY_PATH || '/health/ready'}`;
const fakeMetaBaseUrl = (__ENV.FAKE_META_BASE_URL || `${baseUrl}/fake-meta`).replace(/\/$/, '');
const pageId = __ENV.PAGE_ID || 'PAGE_ID_PERF';
const appSecret = __ENV.APP_SECRET || '';
const startToken = __ENV.MESSAGE_TEXT || 'GET_STARTED';
const duration = __ENV.TEST_DURATION || '10m';
const fakeUsers = Math.max(1, Number(__ENV.FAKE_FB_USERS || 200));
const recipientOffset = Math.max(0, Number(__ENV.RECIPIENT_OFFSET || 0));
const cooldownSeconds = Math.max(1, Number(__ENV.COOLDOWN_SECONDS || 60));
const cooldownGraceSeconds = Math.max(0, Number(__ENV.COOLDOWN_GRACE_SECONDS || 2));
const requestTimeout = __ENV.REQUEST_TIMEOUT || '10s';
const outboundWaitSeconds = Math.max(3, Number(__ENV.OUTBOUND_WAIT_SECONDS || 10));
const requireReady = String(__ENV.REQUIRE_READY || 'true').toLowerCase() !== 'false';
const minThinkSeconds = Math.max(0, Number(__ENV.MIN_THINK_SECONDS || 0.25));
const maxThinkSeconds = Math.max(minThinkSeconds, Number(__ENV.MAX_THINK_SECONDS || 1.0));
const clearFakeMetaOnSetup = String(__ENV.CLEAR_FAKE_META_ON_SETUP || 'true').toLowerCase() !== 'false';
const startupJitterSeconds = Math.max(0, Number(__ENV.STARTUP_JITTER_SECONDS || 30));
const totalDurationSeconds = parseDurationSeconds(duration);
const minimumCycleStartBudgetSeconds = Math.max(12, Number(__ENV.MIN_CYCLE_START_BUDGET_SECONDS || (outboundWaitSeconds + maxThinkSeconds * 2 + 2)));
const textPatterns = {
    acceptedFormat: __ENV.VOTE_ACCEPTED_TEXT_FORMAT || '',
    cooldownFormat: __ENV.COOLDOWN_ACTIVE_TEXT_FORMAT || '',
    rejectedText: __ENV.VOTE_CONFIRMATION_REJECTED_TEXT || '',
    expiredText: __ENV.VOTE_CONFIRMATION_EXPIRED_TEXT || '',
};
const recipientState = new Map();
let startupJitterApplied = false;

const cycleDuration = new Trend('fakefb_cycle_duration_ms', true);
const outboundWait = new Trend('fakefb_outbound_wait_ms', true);
const cycleSuccess = new Rate('fakefb_cycle_success');
const carouselReceived = new Counter('fakefb_carousel_received');
const confirmationReceived = new Counter('fakefb_confirmation_received');
const acceptedReceived = new Counter('fakefb_accepted_received');
const cooldownTextReceived = new Counter('fakefb_cooldown_text_received');
const rejectedTextReceived = new Counter('fakefb_rejected_text_received');
const expiredTextReceived = new Counter('fakefb_expired_text_received');
const otherTextReceived = new Counter('fakefb_other_text_received');
const unexpectedTextOutcome = new Counter('fakefb_unexpected_text_outcome');
const cyclesCompleted = new Counter('fakefb_cycles_completed');
const wrongShape = new Counter('fakefb_unexpected_outbound_shape');

export const options = {
    discardResponseBodies: false,
    insecureSkipTLSVerify: true,
    scenarios: {
        fake_fb_voting_cycle: {
            executor: 'constant-vus',
            vus: fakeUsers,
            duration,
            gracefulStop: '30s',
        },
    },
    thresholds: {
        http_req_failed: ['rate<0.01'],
        fakefb_cycle_success: ['rate>0.99'],
        fakefb_cycle_duration_ms: ['p(95)<120000'],
        checks: ['rate>0.99'],
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function parseDurationSeconds(value) {
    const text = String(value || '').trim();
    const match = /^(\\d+)([smh])$/i.exec(text);
    if (!match) {
        return 600;
    }

    const amount = Number(match[1]);
    const unit = match[2].toLowerCase();
    if (unit === 's') return amount;
    if (unit === 'm') return amount * 60;
    if (unit === 'h') return amount * 3600;
    return 600;
}

function remainingTestSeconds() {
    const elapsedSeconds = exec.instance.currentTestRunDuration / 1000;
    return Math.max(0, totalDurationSeconds - elapsedSeconds);
}

function recipientId() {
    return `fakefb-user-${recipientOffset + exec.vu.idInTest}`;
}

function getLastSequence(recipient) {
    return recipientState.get(recipient) || 0;
}

function setLastSequence(recipient, value) {
    recipientState.set(recipient, Number(value || 0));
}

function randomThinkSeconds() {
    if (maxThinkSeconds <= minThinkSeconds) {
        return minThinkSeconds;
    }

    return minThinkSeconds + Math.random() * (maxThinkSeconds - minThinkSeconds);
}

function sendWebhook(request) {
    const response = http.post(endpoint, request.body, {
        headers: request.headers,
        timeout: requestTimeout,
        tags: {
            endpoint: 'facebook_webhook',
            scenario: 'fake_fb_cycle',
        },
    });

    check(response, {
        'webhook returns 200': (res) => res.status === 200,
    });

    return response;
}

function waitForMessage(recipient, afterSequence, predicate, label) {
    const startedAt = Date.now();
    const result = pollForNextMessage({
        fakeMetaBaseUrl,
        recipientId: recipient,
        afterSequence,
        predicate,
        timeoutSeconds: outboundWaitSeconds,
        pollIntervalSeconds: 0.25,
    });

    outboundWait.add(Date.now() - startedAt, { label });
    return result;
}

function markFailure(recipient, lastSequence) {
    setLastSequence(recipient, lastSequence);
    cycleSuccess.add(false);
    sleep(1);
}

export function setup() {
    if (!textPatterns.acceptedFormat) {
        fail('VOTE_ACCEPTED_TEXT_FORMAT must be provided for strict fake-FB validation.');
    }

    if (requireReady) {
        const ready = http.get(readyUrl, { timeout: '5s', tags: { probe: 'ready' } });
        if (ready.status !== 200) {
            fail(`Readiness check failed. GET ${readyUrl} => ${ready.status}`);
        }
    }

    if (clearFakeMetaOnSetup) {
        clearFakeMetaMessages(fakeMetaBaseUrl);
    }
}

export default function () {
    const remainingSeconds = remainingTestSeconds();
    if (remainingSeconds <= minimumCycleStartBudgetSeconds) {
        sleep(remainingSeconds);
        return;
    }

    const startedAt = Date.now();
    const senderId = recipientId();
    let lastSequence = getLastSequence(senderId);

    const startRequest = buildTextWebhookRequest({
        pageId,
        senderId,
        messageText: startToken,
        appSecret,
        eventCount: 1,
    });

    sendWebhook(startRequest);

    const carousel = waitForMessage(senderId, lastSequence, isCarouselMessage, 'carousel');
    lastSequence = carousel.lastSequence;
    if (!carousel.found) {
        wrongShape.add(1);
        markFailure(senderId, lastSequence);
        return;
    }

    carouselReceived.add(1);

    const elements = carousel.message.elements || [];
    const randomElement = elements[Math.floor(Math.random() * elements.length)];
    const candidateButton = (randomElement.buttons || [])[0];
    if (!candidateButton || !candidateButton.payload) {
        wrongShape.add(1);
        markFailure(senderId, lastSequence);
        return;
    }

    sleep(randomThinkSeconds());

    const choiceRequest = buildPostbackWebhookRequest({
        pageId,
        senderId,
        payload: candidateButton.payload,
        title: candidateButton.title || '',
        appSecret,
    });

    sendWebhook(choiceRequest);

    const confirmation = waitForMessage(senderId, lastSequence, isConfirmationMessage, 'confirmation');
    lastSequence = confirmation.lastSequence;
    if (!confirmation.found) {
        wrongShape.add(1);
        markFailure(senderId, lastSequence);
        return;
    }

    confirmationReceived.add(1);

    const confirmButtons = ((confirmation.message.elements || [])[0] || {}).buttons || [];
    const acceptButton = confirmButtons.find((button) => typeof button.payload === 'string' && button.payload.includes(':YES'));
    if (!acceptButton) {
        wrongShape.add(1);
        markFailure(senderId, lastSequence);
        return;
    }

    sleep(randomThinkSeconds());

    const confirmRequest = buildPostbackWebhookRequest({
        pageId,
        senderId,
        payload: acceptButton.payload,
        title: acceptButton.title || '',
        appSecret,
    });

    sendWebhook(confirmRequest);

    const finalText = waitForMessage(senderId, lastSequence, isTextMessage, 'final_text');
    lastSequence = finalText.lastSequence;
    if (!finalText.found) {
        wrongShape.add(1);
        markFailure(senderId, lastSequence);
        return;
    }

    const finalOutcome = classifyTextMessage(finalText.message, textPatterns);
    switch (finalOutcome) {
        case 'accepted':
            acceptedReceived.add(1);
            setLastSequence(senderId, lastSequence);
            cyclesCompleted.add(1);
            cycleSuccess.add(true);
            cycleDuration.add(Date.now() - startedAt);
            sleep(cooldownSeconds + cooldownGraceSeconds);
            return;
        case 'cooldown':
            cooldownTextReceived.add(1);
            unexpectedTextOutcome.add(1);
            break;
        case 'rejected':
            rejectedTextReceived.add(1);
            unexpectedTextOutcome.add(1);
            break;
        case 'expired':
            expiredTextReceived.add(1);
            unexpectedTextOutcome.add(1);
            break;
        default:
            otherTextReceived.add(1);
            unexpectedTextOutcome.add(1);
            break;
    }

    markFailure(senderId, lastSequence);
}








