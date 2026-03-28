import crypto from 'k6/crypto';
import exec from 'k6/execution';

function makeMid(prefix, eventIndex) {
    const scenario = exec.scenario.name;
    const vu = exec.vu.idInTest;
    const iteration = exec.scenario.iterationInTest;
    return `${prefix}.${scenario}.${vu}.${iteration}.${eventIndex}.${Date.now()}`;
}

export function buildMessengerWebhookRequest({
    eventsPerRequest,
    pageId,
    messageText,
    appSecret,
}) {
    const eventCount = Math.max(1, Number(eventsPerRequest || 1));
    const now = Date.now();
    const messaging = [];

    for (let index = 0; index < eventCount; index += 1) {
        const senderId = `perf-user-${exec.vu.idInTest}-${exec.scenario.iterationInTest}-${index}-${now}`;
        const mid = makeMid('mid.perf', index);

        messaging.push({
            sender: { id: senderId },
            recipient: { id: pageId },
            timestamp: now + index,
            message: {
                mid,
                text: messageText,
            },
        });
    }

    const payload = {
        object: 'page',
        entry: [
            {
                id: pageId,
                time: now,
                messaging,
            },
        ],
    };

    const body = JSON.stringify(payload);
    const headers = {
        'Content-Type': 'application/json',
    };

    if (appSecret && appSecret.length > 0) {
        const signatureHex = crypto.hmac('sha256', appSecret, body, 'hex');
        headers['X-Hub-Signature-256'] = `sha256=${signatureHex}`;
    }

    return {
        body,
        headers,
        eventCount,
    };
}
