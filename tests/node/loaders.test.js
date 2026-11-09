'use strict';

/*
 * Contract tests for the loader archive proxy in server.js.
 *
 * The route exists because the GitHub release CDN (release-assets.githubusercontent.com /
 * objects.githubusercontent.com) is blocked or throttled from several regions, which left the
 * desktop download bar hanging. These tests exercise the real handler with a stubbed `fetch`, so
 * the streaming, the headers and the failure status are the shipped ones.
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');

const { createApp } = require('../../server.js');

const SIGNING_KEY = 'test-signing-key-0123456789-abcdefghijklmnop';

const unavailableStore = {
    async findById() { throw new Error('store-unavailable'); },
    async findByLogin() { throw new Error('store-unavailable'); },
    async findByIdOrEmail() { throw new Error('store-unavailable'); },
    async createUser() { throw new Error('store-unavailable'); },
    async updateUser() { throw new Error('store-unavailable'); }
};

async function startApi() {
    const app = createApp({
        store: unavailableStore,
        signingKey: SIGNING_KEY,
        mailer: { configured: false, async sendVerificationCode() { return false; } },
        log: () => {}
    });

    const server = http.createServer(app);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;

    return {
        base,
        async close() {
            await new Promise(resolve => server.close(resolve));
        }
    };
}

/** Replaces the global fetch for the duration of one test. */
function stubFetch(implementation) {
    const original = globalThis.fetch;
    globalThis.fetch = implementation;
    return () => { globalThis.fetch = original; };
}

test('an unknown chipset is rejected with 404 instead of proxying anything', async () => {
    const api = await startApi();
    const restore = stubFetch(() => {
        throw new Error('the proxy must not reach upstream for an unknown chipset');
    });

    try {
        const response = await fetch(`${api.base}/api/v1/loaders/download/windows98`);
        const payload = await response.json();

        assert.equal(response.status, 404);
        assert.equal(payload.error.code, 'not-found');
    } finally {
        restore();
        await api.close();
    }
});

test('a known chipset is streamed back as a zip with the release file name', async () => {
    let requested = null;

    const api = await startApi();
    const restore = stubFetch(async url => {
        requested = String(url);
        const payload = new Uint8Array([80, 75, 3, 4, 1, 2, 3]);

        return new Response(payload, {
            status: 200,
            headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(payload.length) }
        });
    });

    try {
        const response = await fetch(`${api.base}/api/v1/loaders/download/mtk`);
        const bytes = new Uint8Array(await response.arrayBuffer());

        assert.equal(response.status, 200);
        assert.equal(response.headers.get('content-type'), 'application/zip');
        assert.match(response.headers.get('content-disposition'), /mtkclient-main\.zip/);
        assert.equal(response.headers.get('content-length'), '7');
        assert.deepEqual([...bytes], [80, 75, 3, 4, 1, 2, 3]);
        assert.match(requested, /B_VIP-Loaders\/releases\/download\/v1\.0\/mtkclient-main\.zip$/);
    } finally {
        restore();
        await api.close();
    }
});

test('an unreachable release host answers 502 loader-unavailable, never a hang', async () => {
    const api = await startApi();
    const restore = stubFetch(async () => {
        throw new Error('getaddrinfo ENOTFOUND objects.githubusercontent.com');
    });

    try {
        const response = await fetch(`${api.base}/api/v1/loaders/download/qualcomm`);
        const payload = await response.json();

        assert.equal(response.status, 502);
        assert.equal(payload.error.code, 'loader-unavailable');
        assert.doesNotMatch(payload.error.message, /githubusercontent|ENOTFOUND/);
    } finally {
        restore();
        await api.close();
    }
});

test('an upstream error status is reported as loader-unavailable', async () => {
    const api = await startApi();
    const restore = stubFetch(async () => new Response('missing', { status: 404 }));

    try {
        const response = await fetch(`${api.base}/api/v1/loaders/download/spd`);

        assert.equal(response.status, 502);
        assert.equal((await response.json()).error.code, 'loader-unavailable');
    } finally {
        restore();
        await api.close();
    }
});
