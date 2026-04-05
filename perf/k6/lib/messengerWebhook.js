import crypto from 'k6/crypto';
import exec from 'k6/execution';

function makeMid(prefix, eventIndex) {
    const scenario = exec.scenario.name;
    const vu = exec.vu.idInTest;
    const iteration = exec.scenario.iterationInTest;
    return `${prefix}.${scenario}.${vu}.${iteration}.${eventIndex}.${Date.now()}`;
}

function buildPayload({ pageId, messaging }) {
    const now = Date.now();
    return {
        object: 'page',
        entry: [
            {
                id: pageId,
                time: now,
                messaging,
            },
        ],
    };
}

function signBody(headers, appSecret, body) {
    if (appSecret && appSecret.length > 0) {
        const signatureHex = crypto.hmac('sha256', appSecret, body, 'hex');
        headers['X-Hub-Signature-256'] = `sha256=${signatureHex}`;
    }
}

export function buildTextWebhookRequest({
    pageId,
    senderId,
    messageText,
    appSecret,
    eventCount = 1,
}) {
    const count = Math.max(1, Number(eventCount || 1));
    const now = Date.now();
    const messaging = [];

    for (let index = 0; index < count; index += 1) {
        messaging.push({
            sender: { id: `${senderId}` },
            recipient: { id: pageId },
            timestamp: now + index,
            message: {
                mid: makeMid('mid.perf', index),
                text: messageText,
            },
        });
    }

    const payload = buildPayload({ pageId, messaging });
    const body = JSON.stringify(payload);
    const headers = {
        'Content-Type': 'application/json',
    };

    signBody(headers, appSecret, body);

    return {
        body,
        headers,
        eventCount: count,
    };
}

export function buildPostbackWebhookRequest({
    pageId,
    senderId,
    payload,
    title,
    appSecret,
}) {
    const messaging = [
        {
            sender: { id: `${senderId}` },
            recipient: { id: pageId },
            timestamp: Date.now(),
            postback: {
                payload,
                title: title || '',
            },
        },
    ];

    const requestPayload = buildPayload({ pageId, messaging });
    const body = JSON.stringify(requestPayload);
    const headers = {
        'Content-Type': 'application/json',
    };

    signBody(headers, appSecret, body);

    return {
        body,
        headers,
        eventCount: 1,
    };
}

export function buildMessengerWebhookRequest({
    eventsPerRequest,
    pageId,
    messageText,
    appSecret,
}) {
    const senderId = `perf-user-${exec.vu.idInTest}-${exec.scenario.iterationInTest}-${Date.now()}`;
    return buildTextWebhookRequest({
        pageId,
        senderId,
        messageText,
        appSecret,
        eventCount: Math.max(1, Number(eventsPerRequest || 1)),
    });
}
