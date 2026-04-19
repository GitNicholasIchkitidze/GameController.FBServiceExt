import http from 'k6/http';
import { sleep } from 'k6';

function buildUrl(baseUrl, path) {
    return `${baseUrl.replace(/\/$/, '')}${path}`;
}

function normalizeText(value) {
    if (value === null || value === undefined) {
        return '';
    }

    return String(value).replace(/\s+/g, ' ').trim();
}

function extractStaticFragments(format) {
    return normalizeText(format)
        .split(/\{[^}]+\}/)
        .map((fragment) => normalizeText(fragment))
        .filter((fragment) => fragment.length > 0);
}

function containsFragmentsInOrder(text, fragments) {
    if (fragments.length === 0) {
        return false;
    }

    let cursor = 0;
    for (const fragment of fragments) {
        const index = text.indexOf(fragment, cursor);
        if (index < 0) {
            return false;
        }

        cursor = index + fragment.length;
    }

    return true;
}

function messageMatchesFormat(messageText, format) {
    const normalizedText = normalizeText(messageText);
    const fragments = extractStaticFragments(format);

    if (!normalizedText || fragments.length === 0) {
        return false;
    }

    return containsFragmentsInOrder(normalizedText, fragments);
}

export function clearFakeMetaMessages(fakeMetaBaseUrl) {
    const response = http.del(buildUrl(fakeMetaBaseUrl, '/api/messages'), null, {
        timeout: '5s',
        tags: { endpoint: 'fake_meta_clear' },
    });

    return response.status === 204;
}

export function listFakeMetaMessages(fakeMetaBaseUrl, recipientId, afterSequence, waitSeconds = 0) {
    const safeWaitSeconds = Math.max(0, Math.ceil(Number(waitSeconds || 0)));
    const response = http.get(
        buildUrl(fakeMetaBaseUrl, `/api/recipients/${encodeURIComponent(recipientId)}/messages?afterSequence=${afterSequence}&waitSeconds=${safeWaitSeconds}`),
        {
            timeout: `${Math.max(5, Math.ceil(safeWaitSeconds) + 5)}s`,
            tags: { endpoint: 'fake_meta_poll' },
        });

    if (response.status !== 200) {
        return {
            ok: false,
            messages: [],
            status: response.status,
        };
    }

    const body = response.json();
    return {
        ok: true,
        messages: Array.isArray(body) ? body : [],
        status: response.status,
    };
}

export function pollForNextMessage({
    fakeMetaBaseUrl,
    recipientId,
    afterSequence,
    predicate,
    timeoutSeconds = 10,
    pollIntervalSeconds = 0.25,
}) {
    const deadline = Date.now() + timeoutSeconds * 1000;
    let lastSequence = afterSequence;
    const seen = [];

    while (Date.now() < deadline) {
        const remainingSeconds = Math.max(0.25, (deadline - Date.now()) / 1000);
        const result = listFakeMetaMessages(fakeMetaBaseUrl, recipientId, lastSequence, remainingSeconds);
        if (result.ok && result.messages.length > 0) {
            for (const message of result.messages) {
                seen.push(message);
                lastSequence = Math.max(lastSequence, Number(message.sequence || 0));
                if (!predicate || predicate(message)) {
                    return {
                        found: true,
                        message,
                        lastSequence,
                        seen,
                    };
                }
            }

            continue;
        }

        if (result.ok) {
            break;
        }

        sleep(pollIntervalSeconds);
    }

    return {
        found: false,
        message: null,
        lastSequence,
        seen,
    };
}

export function isCarouselMessage(message) {
    return message && message.templateType === 'generic' && Array.isArray(message.elements) && message.elements.length >= 1;
}

export function isConfirmationMessage(message) {
    if (!message || message.templateType !== 'generic' || !Array.isArray(message.elements) || message.elements.length !== 1) {
        return false;
    }

    const buttons = message.elements[0].buttons || [];
    return buttons.some((button) => typeof button.payload === 'string' && button.payload.includes(':YES'));
}

export function isTextMessage(message) {
    return message && message.kind === 'text' && typeof message.text === 'string';
}

export function classifyTextMessage(message, patterns = {}) {
    if (!isTextMessage(message)) {
        return 'non_text';
    }

    const text = message.text || '';

    if (patterns.acceptedFormat && messageMatchesFormat(text, patterns.acceptedFormat)) {
        return 'accepted';
    }

    if (patterns.cooldownFormat && messageMatchesFormat(text, patterns.cooldownFormat)) {
        return 'cooldown';
    }

    if (patterns.rejectedText && messageMatchesFormat(text, patterns.rejectedText)) {
        return 'rejected';
    }

    if (patterns.expiredText && messageMatchesFormat(text, patterns.expiredText)) {
        return 'expired';
    }

    return 'other_text';
}

