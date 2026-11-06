'use strict';

/*
 * Authentication contract tests for server.js.
 *
 * They run against the real request handler with an in-memory user store, so the password
 * verification, HWID binding, role claims and HTTP status/error codes are the shipped ones.
 * Run with: npm test   (node --test tests/node)
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const crypto = require('node:crypto');

const {
    createApp,
    hashPassword,
    signToken,
    verifyToken,
    evaluateLicense,
    licenseAccessFor,
    MASTER_ADMIN,
    ERRORS,
    OTP_EMAIL_SUBJECT,
    buildOtpText,
    buildOtpHtml
} = require('../../server.js');

const SIGNING_KEY = 'test-signing-key-0123456789-abcdefghijklmnop';

function createMemoryStore() {
    const users = new Map();

    const store = {
        users,
        async findByLogin(identifier) {
            const needle = String(identifier || '').toLowerCase();
            for (const user of users.values()) {
                if (user.userName.toLowerCase() === needle || (user.email || '').toLowerCase() === needle) {
                    return { ...user };
                }
            }
            return null;
        },
        async findById(id) {
            const user = users.get(id);
            return user ? { ...user } : null;
        },
        async findByIdOrEmail(value) {
            return store.findByLogin(value);
        },
        async createUser(fields) {
            const user = {
                id: fields.id,
                userName: fields.userName,
                displayName: fields.displayName || fields.userName,
                email: fields.email,
                passwordHash: fields.passwordHash,
                hwid: fields.hwid || null,
                role: /admin/i.test(String(fields.role || '')) ? 'admin' : 'technician',
                isActive: fields.isActive !== false,
                isVerified: fields.isVerified === true,
                otpCode: fields.otpCode || null,
                otpExpiresAt: fields.otpExpiresAt || null,
                licenseType: fields.licenseType || 'trial',
                licenseStartedAt: fields.licenseStartedAt || new Date(),
                licenseExpiresAt: fields.licenseExpiresAt || null,
                hwidResetCount: 0,
                failedAttempts: 0,
                lockoutUntil: null,
                lastLoginAt: null,
                createdUtc: new Date()
            };
            users.set(user.id, user);
            return { ...user };
        },
        async updateUser(id, patch) {
            const user = users.get(id);
            assert.ok(user, `user ${id} exists`);
            Object.assign(user, patch);
            return { ...user };
        },
        async deleteUser(id) {
            return users.delete(id);
        },
        async listUsers(options = {}) {
            const search = String((options && options.search) || '').toLowerCase();
            return [...users.values()]
                .filter(user => search.length === 0 ||
                    user.userName.toLowerCase().includes(search) ||
                    String(user.email || '').toLowerCase().includes(search))
                .map(user => ({ ...user }));
        }
    };

    return store;
}

function createUnavailableStore() {
    const boom = async () => {
        throw new Error('store-unavailable');
    };
    return { findById: boom, findByLogin: boom, findByIdOrEmail: boom, createUser: boom, updateUser: boom };
}

async function startApi(store, mailer) {
    const app = createApp({
        store,
        signingKey: SIGNING_KEY,
        mailer: mailer || { configured: false, async sendVerificationCode() { return false; } },
        log: () => {}
    });

    const server = http.createServer(app);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;

    return {
        base,
        async post(path, body, token) {
            const response = await fetch(base + path, {
                method: 'POST',
                headers: token
                    ? { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` }
                    : { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            return { status: response.status, json: await response.json() };
        },
        async get(path, token) {
            const response = await fetch(base + path, {
                headers: token ? { Authorization: `Bearer ${token}` } : {}
            });
            return { status: response.status, json: await response.json() };
        },
        async close() {
            await new Promise(resolve => server.close(resolve));
        }
    };
}

function decodeJwt(token) {
    return JSON.parse(Buffer.from(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
}

async function addUser(store, overrides) {
    const password = overrides.password || 'Technician!Pass1';
    const user = await store.createUser({
        id: crypto.randomUUID(),
        userName: 'tech.one',
        email: 'tech.one@example.com',
        passwordHash: await hashPassword(password),
        role: 'technician',
        isActive: true,
        isVerified: true,
        licenseType: 'trial',
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 3 * 86400000),
        ...overrides
    });
    return { user, password };
}

// ---------------------------------------------------------------------------
test('an unknown account is rejected with 401 invalid-credentials', async () => {
    const api = await startApi(createMemoryStore());
    try {
        const result = await api.post('/api/auth/login', { userName: 'nobody', password: 'whatever123' });
        assert.equal(result.status, 401);
        assert.equal(result.json.error.code, 'invalid-credentials');
        assert.equal(result.json.error.message, 'Invalid username or password.');
    } finally {
        await api.close();
    }
});

test('a wrong password is rejected with 401 and never issues a token', async () => {
    const store = createMemoryStore();
    await addUser(store, {});
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', { userName: 'tech.one', password: 'wrong-password' });
        assert.equal(result.status, 401);
        assert.equal(result.json.tokens, undefined);
        assert.equal(result.json.error.code, 'invalid-credentials');
    } finally {
        await api.close();
    }
});

test('the master admin signs in with BCrypt password and receives an admin lifetime session', async () => {
    const store = createMemoryStore();
    await store.createUser({
        id: crypto.randomUUID(),
        userName: MASTER_ADMIN.username,
        displayName: MASTER_ADMIN.username,
        email: MASTER_ADMIN.email,
        passwordHash: await hashPassword(MASTER_ADMIN.password),
        role: MASTER_ADMIN.role,
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin',
        licenseStartedAt: new Date(),
        licenseExpiresAt: null
    });

    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email,
            password: MASTER_ADMIN.password
        });

        assert.equal(result.status, 200);
        assert.equal(result.json.user.email, MASTER_ADMIN.email);
        assert.equal(result.json.user.userName, MASTER_ADMIN.username);
        assert.deepEqual(result.json.user.roles, ['Admin', 'lifetime_admin']);
        assert.equal(result.json.user.hardwareId, null);
        assert.equal(result.json.license.status, 'Active');
        assert.equal(result.json.license.expiresAt, null);
        assert.ok(result.json.permissions.includes('All'));

        const claims = decodeJwt(result.json.tokens.accessToken);
        assert.equal(claims.role, 'Admin');
        assert.deepEqual(claims.roles, ['Admin', 'lifetime_admin']);
        assert.equal(claims.email, MASTER_ADMIN.email);
        assert.ok(claims.exp - claims.iat <= 60 * 60);
        assert.equal(claims.typ, 'access');
    } finally {
        await api.close();
    }
});

test('administrators are exempt from the HWID binding', async () => {
    const store = createMemoryStore();
    await store.createUser({
        id: crypto.randomUUID(),
        userName: 'Admin Bashir',
        email: MASTER_ADMIN.email,
        passwordHash: await hashPassword(MASTER_ADMIN.password),
        role: 'admin',
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin'
    });

    const api = await startApi(store);
    try {
        const first = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password, deviceFingerprint: 'PC-A'
        });
        const second = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password, deviceFingerprint: 'PC-B'
        });

        assert.equal(first.status, 200);
        assert.equal(second.status, 200);
        const stored = [...store.users.values()][0];
        assert.equal(stored.hwid, null, 'the admin account is never bound to a machine');
    } finally {
        await api.close();
    }
});

test('an unverified technician is blocked with email-not-verified', async () => {
    const store = createMemoryStore();
    await addUser(store, { isVerified: false });
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'PC-A'
        });
        assert.equal(result.status, 403);
        assert.equal(result.json.error.code, 'email-not-verified');
    } finally {
        await api.close();
    }
});

test('a technician signs in from any computer and no fingerprint is stored', async () => {
    const store = createMemoryStore();
    const { user } = await addUser(store, {});
    const api = await startApi(store);
    try {
        const first = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(first.status, 200);
        assert.equal(first.json.user.hardwareId, null, 'no machine is reported as bound');
        assert.deepEqual(first.json.user.roles, ['Technician']);
        assert.equal(store.users.get(user.id).hwid ?? null, null, 'no fingerprint is written to the account');

        const second = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(second.status, 200);
    } finally {
        await api.close();
    }
});

test('a different machine is accepted: the HWID gate has been removed', async () => {
    const store = createMemoryStore();
    await addUser(store, { hwid: 'MACHINE-A' });
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-B'
        });
        assert.equal(result.status, 200);
        assert.equal(result.json.error, undefined);
        assert.ok(result.json.tokens.accessToken);

        // A client that sends no fingerprint at all is equally welcome.
        const withoutFingerprint = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1'
        });
        assert.equal(withoutFingerprint.status, 200);
    } finally {
        await api.close();
    }
});

test('an expired subscription still signs in but grants no permissions', async () => {
    const store = createMemoryStore();
    await addUser(store, {
        licenseType: 'trial',
        licenseStartedAt: new Date(Date.now() - 10 * 86400000),
        licenseExpiresAt: new Date(Date.now() - 86400000)
    });
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-A'
        });
        // An expired trial is not a lockout from the account, only from the bench.
        assert.equal(result.status, 200);
        assert.ok(result.json.tokens.accessToken, 'an expired trial still receives a session');
        assert.equal(result.json.license.status, 'Expired');
        assert.equal(result.json.license.licenseStatus, 'Expired');
        assert.equal(result.json.license.deviceOperationsLocked, true);
        assert.equal(result.json.license.isActive, false);
        assert.deepEqual(result.json.permissions, []);
        assert.equal(result.json.user.status, 'Expired');
        assert.equal(result.json.user.deviceOperationsLocked, true);
    } finally {
        await api.close();
    }
});

test('malformed e-mail shaped identifiers are rejected before any lookup', async () => {
    const store = createMemoryStore();
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', { userName: 'random.text@', password: 'whatever123' });
        assert.equal(result.status, 400);
        assert.equal(result.json.error.code, 'invalid-email');
    } finally {
        await api.close();
    }
});

test('repeated failures lock the account', async () => {
    const store = createMemoryStore();
    const { user } = await addUser(store, {});
    const api = await startApi(store);
    try {
        for (let attempt = 0; attempt < 5; attempt += 1) {
            const result = await api.post('/api/auth/login', { userName: 'tech.one', password: 'nope-nope-nope' });
            assert.equal(result.status, 401);
        }

        const locked = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(locked.status, 403);
        assert.equal(locked.json.error.code, 'account-locked');
        assert.ok(new Date(store.users.get(user.id).lockoutUntil).getTime() > Date.now());
    } finally {
        await api.close();
    }
});

test('a database outage answers 503 instead of a fake session', async () => {
    const api = await startApi(createUnavailableStore());
    try {
        const result = await api.post('/api/auth/login', { userName: 'tech.one', password: 'Technician!Pass1' });
        assert.equal(result.status, 503);
        assert.equal(result.json.error.code, 'database-unavailable');
        assert.equal(result.json.tokens, undefined);
        assert.equal(result.json.success, undefined);
    } finally {
        await api.close();
    }
});

test('registration validates e-mail, password and creates a 15-minute code without a session', async () => {
    const store = createMemoryStore();
    let captured = null;
    const mailer = {
        configured: true,
        async sendVerificationCode(to, userName, code) {
            captured = { to, userName, code };
            return true;
        }
    };
    const api = await startApi(store, mailer);
    try {
        const badEmail = await api.post('/api/auth/register', {
            userName: 'new.tech', email: 'not-an-email', password: 'LongEnough1', acceptedTerms: true
        });
        assert.equal(badEmail.status, 400);
        assert.equal(badEmail.json.error.code, 'invalid-email');

        const weak = await api.post('/api/auth/register', {
            userName: 'new.tech', email: 'new.tech@example.com', password: 'short', acceptedTerms: true
        });
        assert.equal(weak.status, 400);
        assert.equal(weak.json.error.code, 'weak-password');

        const ok = await api.post('/api/auth/register', {
            userName: 'new.tech', email: 'new.tech@example.com', password: 'LongEnough1', acceptedTerms: true
        });
        assert.equal(ok.status, 200);
        assert.equal(ok.json.requiresVerification, true);
        assert.equal(ok.json.verificationEmailSent, true);
        assert.equal(ok.json.tokens, undefined);

        assert.ok(captured && /^\d{6}$/.test(captured.code));
        assert.equal(captured.to, 'new.tech@example.com');

        const created = [...store.users.values()][0];
        assert.equal(created.isVerified, false);
        assert.equal(created.licenseType, 'trial');
        const ttlMinutes = (new Date(created.otpExpiresAt).getTime() - Date.now()) / 60000;
        assert.ok(ttlMinutes > 14 && ttlMinutes <= 15.1, `OTP TTL was ${ttlMinutes} minutes`);

        const wrong = await api.post('/api/auth/verify-code', {
            userName: 'new.tech', code: captured.code === '000000' ? '111111' : '000000'
        });
        assert.equal(wrong.status, 400);
        assert.equal(wrong.json.error.code, 'invalid-code');

        const verified = await api.post('/api/auth/verify-code', { userName: 'new.tech', code: captured.code });
        assert.equal(verified.status, 200);
        assert.equal(verified.json.verified, true);

        const after = store.users.get(created.id);
        assert.equal(after.isVerified, true);
        assert.equal(after.otpCode, null);
        const trialDays = (new Date(after.licenseExpiresAt).getTime() - Date.now()) / 86400000;
        assert.ok(trialDays > 2.9 && trialDays <= 3.01, `trial was ${trialDays} days`);
    } finally {
        await api.close();
    }
});

test('expired verification codes are refused', async () => {
    const store = createMemoryStore();
    await addUser(store, {
        isVerified: false,
        otpCode: '123456',
        otpExpiresAt: new Date(Date.now() - 60000)
    });
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/verify-code', { userName: 'tech.one', code: '123456' });
        assert.equal(result.status, 400);
        assert.equal(result.json.error.code, 'code-expired');
    } finally {
        await api.close();
    }
});

test('passwords written verbatim by the previous build are accepted once and re-hashed', async () => {
    const store = createMemoryStore();
    await store.createUser({
        id: crypto.randomUUID(),
        userName: 'legacy.user',
        email: 'legacy@example.com',
        passwordHash: 'PlainText123',
        role: 'technician',
        isActive: true,
        isVerified: true,
        licenseType: 'active',
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 30 * 86400000)
    });
    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'legacy.user', password: 'PlainText123', deviceFingerprint: 'PC-1'
        });
        assert.equal(result.status, 200);

        const stored = [...store.users.values()][0];
        assert.match(stored.passwordHash, /^\$2[aby]\$/);
        assert.notEqual(stored.passwordHash, 'PlainText123');

        const again = await api.post('/api/auth/login', {
            userName: 'legacy.user', password: 'PlainText123', deviceFingerprint: 'PC-1'
        });
        assert.equal(again.status, 200);
    } finally {
        await api.close();
    }
});

test('the bearer token opens /api/auth/me and a tampered token does not', async () => {
    const store = createMemoryStore();
    const { user } = await addUser(store, {});
    const api = await startApi(store);
    try {
        const login = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-A'
        });
        const token = login.json.tokens.accessToken;

        const me = await api.get('/api/auth/me', token);
        assert.equal(me.status, 200);
        assert.equal(me.json.user.id, user.id);
        // A live trial reports as Active on the wire; the plan name is what marks it a trial.
        assert.equal(me.json.license.status, 'Active');
        assert.equal(me.json.license.isTrial, true);

        const tampered = token.slice(0, -3) + (token.endsWith('AAA') ? 'BBB' : 'AAA');
        const rejected = await api.get('/api/auth/me', tampered);
        assert.equal(rejected.status, 401);
    } finally {
        await api.close();
    }
});

test('issued tokens carry role claims and are signed with the configured key', () => {
    const admin = {
        id: crypto.randomUUID(),
        userName: 'Admin Bashir',
        displayName: 'Admin Bashir',
        email: MASTER_ADMIN.email,
        role: 'admin',
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin',
        hwid: null,
        hwidResetCount: 0,
        licenseStartedAt: new Date(),
        licenseExpiresAt: null
    };
    const tech = {
        id: crypto.randomUUID(),
        userName: 'tech.two',
        displayName: 'tech.two',
        email: 'tech.two@example.com',
        role: 'technician',
        isActive: true,
        isVerified: true,
        licenseType: 'trial',
        hwid: 'PC',
        hwidResetCount: 0,
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 2 * 86400000)
    };

    assert.equal(evaluateLicense(admin).active, true);
    assert.equal(evaluateLicense(tech).status, 'Trial');

    // The wire status is "Active" for the whole 72 hours: the internal trial label must never
    // reach a client that gates device work on status === 'Active'.
    assert.equal(licenseAccessFor(tech).licenseStatus, 'Active');
    assert.equal(licenseAccessFor(tech).deviceOperationsLocked, false);
    assert.equal(licenseAccessFor(tech).isTrial, true);
    assert.equal(licenseAccessFor(admin).deviceOperationsLocked, false);
    assert.equal(licenseAccessFor(admin).isTrial, false);

    const now = Math.floor(Date.now() / 1000);
    const token = signToken({ sub: tech.id, typ: 'access', role: 'Technician', exp: now + 300 }, SIGNING_KEY);
    const payload = verifyToken(token, SIGNING_KEY, 'access');
    assert.equal(payload.sub, tech.id);

    // A different key, an expired token and a refresh token are all rejected for API calls.
    assert.equal(verifyToken(token, 'another-key-0123456789-0123456789-abcdefg', 'access'), null);
    const expired = signToken({ sub: tech.id, typ: 'access', exp: now - 10 }, SIGNING_KEY);
    assert.equal(verifyToken(expired, SIGNING_KEY, 'access'), null);
    const refresh = signToken({ sub: tech.id, typ: 'refresh', exp: now + 300 }, SIGNING_KEY);
    assert.equal(verifyToken(refresh, SIGNING_KEY, 'access'), null);
});

test('health reports the database state instead of a canned subscription', async () => {
    const api = await startApi(createMemoryStore());
    try {
        const result = await api.get('/api/health');
        assert.equal(result.status, 200);
        assert.equal(result.json.database, 'connected');
        assert.equal(result.json.status, 'online');
        assert.equal(result.json.isSubscribed, undefined);
        assert.equal(result.json.daysRemaining, undefined);
    } finally {
        await api.close();
    }
});

test('the admin console routes answer live data instead of 404', async () => {
    const store = createMemoryStore();
    await addUser(store, {
        userName: 'Admin Bashir',
        email: MASTER_ADMIN.email,
        role: 'admin',
        password: MASTER_ADMIN.password,
        licenseType: 'lifetime_admin',
        isVerified: true
    });
    await addUser(store, { userName: 'tech.two', email: 'tech.two@example.com' });
    const api = await startApi(store);
    try {
        const login = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password
        });
        assert.equal(login.status, 200);
        const token = login.json.tokens.accessToken;

        // Every read the desktop console performs now answers 200 with a payload.
        const reads = [
            '/api/admin/workstations', '/api/admin/expired-accounts', '/api/admin/logs',
            '/api/admin/overview', '/api/admin/technicians', '/api/admin/technicians/metrics',
            '/api/users', '/api/licenses', '/api/licenses/plans', '/api/admin/roles',
            '/api/admin/permissions', '/api/admin/settings', '/api/admin/integrations',
            '/api/admin/audit-events', '/api/admin/backups', '/api/admin/versions',
            '/api/admin/maintenance', '/api/admin/health', '/api/admin/status'
        ];
        for (const path of reads) {
            const response = await api.get(path, token);
            assert.equal(response.status, 200, `${path} answered ${response.status}`);
        }

        const workstations = await api.get('/api/admin/workstations', token);
        assert.equal(workstations.json.deviceBinding, 'disabled');
        assert.equal(workstations.json.items.length, 1, 'the directory lists the technician only');
        assert.equal(workstations.json.items[0].hardwareId, null);

        const expired = await api.get('/api/admin/expired-accounts', token);
        assert.deepEqual(expired.json.items, []);

        const logs = await api.get('/api/admin/logs', token);
        assert.ok(logs.json.items.length > 0, 'sign-in activity is recorded');
        assert.ok(logs.json.items.some(entry => /Login Authorized/.test(entry.message)));

        const technician = await store.findByLogin('tech.two');
        const extended = await api.post('/api/admin/extend-license', {
            userId: technician.id, days: 30
        }, token);
        assert.equal(extended.status, 200);
        assert.equal(extended.json.days, 30);
        assert.equal(extended.json.userName, 'tech.two');
        const remainingDays = (new Date(extended.json.expiresAt).getTime() - Date.now()) / 86400000;
        assert.ok(remainingDays > 32 && remainingDays <= 33.1, `licence now ends in ${remainingDays} days`);
        assert.equal(store.users.get(technician.id).isActive, true);

        const invalidDays = await api.post('/api/admin/extend-license', { userId: technician.id, days: 0 }, token);
        assert.equal(invalidDays.status, 400);
        assert.equal(invalidDays.json.error.code, 'invalid-days');

        // A technician token cannot read or drive the management console.
        const techLogin = await api.post('/api/auth/login', {
            userName: 'tech.two', password: 'Technician!Pass1', deviceFingerprint: 'MACHINE-B'
        });
        assert.equal(techLogin.status, 200);
        const forbidden = await api.get('/api/admin/workstations', techLogin.json.tokens.accessToken);
        assert.equal(forbidden.status, 403);
        assert.equal(forbidden.json.error.code, 'forbidden');
    } finally {
        await api.close();
    }
});

test('the verification e-mail uses the approved English subject and body', () => {
    assert.equal(OTP_EMAIL_SUBJECT, 'Your B_VIP Tool Verification Code');

    const text = buildOtpText('428913');
    assert.match(text, /Welcome to B_VIP Tool,/);
    assert.match(text, /smart device management and maintenance platform/);
    assert.match(text, /\[ 428913 \]/);
    assert.match(text, /valid for 15 minutes only/);
    assert.match(text, /Never share it with anyone/);
    assert.match(text, /please disregard this email/);
    assert.match(text, /B_VIP Tool Team/);
    assert.match(text, /Comprehensive ecosystem for smart device maintenance and support/);

    const html = buildOtpHtml('428913', 'tech.one');
    assert.match(html, /<html/);
    assert.match(html, /428913/);
    assert.match(html, /#38BDF8/);
    assert.match(html, /Welcome to B_VIP Tool, tech\.one/);
});

test('unknown endpoints return a 404 error envelope', async () => {
    const api = await startApi(createMemoryStore());
    try {
        const result = await api.post('/api/does-not-exist', {});
        assert.equal(result.status, 404);
        assert.equal(result.json.error.code, ERRORS.notFound.code);
    } finally {
        await api.close();
    }
});

// ---------------------------------------------------------------------------
// Trial lifecycle: 72 hours of full access, then a sign-in that operates nothing.
// ---------------------------------------------------------------------------

test('a fresh trial unlocks every module and reports no lock', async () => {
    const store = createMemoryStore();
    const { user } = await addUser(store, {
        licenseType: 'trial',
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 3 * 86400000)
    });

    const api = await startApi(store);
    try {
        const login = await api.post('/api/auth/login', {
            userName: 'tech.one', password: 'Technician!Pass1'
        });
        assert.equal(login.status, 200);

        const license = login.json.license;
        assert.equal(license.isActive, true);
        assert.equal(license.isTrial, true);
        assert.equal(license.licenseStatus, 'Active');
        assert.equal(license.deviceOperationsLocked, false);
        assert.equal(license.status, 'Active');
        assert.equal(license.planCode, 'trial');
        assert.equal(license.planName, '3-Day Free Trial');
        assert.ok(license.daysRemaining >= 2, `daysRemaining was ${license.daysRemaining}`);
        assert.ok(license.expiresAt, 'the trial carries an expiry');

        // The same verdict on the user object, so either one answers "may this account work?".
        assert.equal(login.json.user.deviceOperationsLocked, false);
        assert.equal(login.json.user.isActive, true);
        assert.equal(login.json.user.isTrial, true);
        assert.equal(login.json.user.licenseStatus, 'Active');

        // Every vendor module is available for the whole trial.
        for (const permission of ['ADB', 'Fastboot', 'FRP', 'Flash', 'MTK', 'Qualcomm', 'SPD', 'Samsung']) {
            assert.ok(login.json.permissions.includes(permission), `${permission} is unlocked during the trial`);
        }

        const claims = decodeJwt(login.json.tokens.accessToken);
        assert.ok(claims.permissions.includes('MTK'), 'the token carries the trial permissions');
        assert.equal(user.id, login.json.user.id);
    } finally {
        await api.close();
    }
});

test('an administrator can restart a technician trial for a full 72 hours', async () => {
    const store = createMemoryStore();
    await store.createUser({
        id: crypto.randomUUID(),
        userName: MASTER_ADMIN.username,
        displayName: MASTER_ADMIN.username,
        email: MASTER_ADMIN.email,
        passwordHash: await hashPassword(MASTER_ADMIN.password),
        role: 'admin',
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin',
        licenseStartedAt: new Date(),
        licenseExpiresAt: null
    });
    const { user: technician } = await addUser(store, {
        userName: 'trial.again',
        email: 'trial.again@example.com',
        licenseType: 'trial',
        licenseStartedAt: new Date(Date.now() - 5 * 86400000),
        licenseExpiresAt: new Date(Date.now() - 2 * 86400000)
    });

    const api = await startApi(store);
    try {
        const admin = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password
        });
        assert.equal(admin.status, 200);
        const token = admin.json.tokens.accessToken;

        const reset = await api.post(`/api/admin/technicians/${technician.id}/reset-trial`, {}, token);
        assert.equal(reset.status, 200);
        assert.equal(reset.json.isActive, true);
        assert.equal(reset.json.isTrial, true);
        assert.equal(reset.json.licenseStatus, 'Active');
        assert.equal(reset.json.deviceOperationsLocked, false);

        const hours = (new Date(reset.json.expiresAt).getTime() - Date.now()) / 3600000;
        assert.ok(hours > 71.9 && hours <= 72.01, `reset trial ran ${hours} hours`);

        // And the technician can now sign in with the bench unlocked.
        const technicianLogin = await api.post('/api/auth/login', {
            userName: 'trial.again', password: 'Technician!Pass1'
        });
        assert.equal(technicianLogin.status, 200);
        assert.equal(technicianLogin.json.license.deviceOperationsLocked, false);
        assert.equal(technicianLogin.json.license.isTrial, true);
        assert.ok(technicianLogin.json.permissions.includes('MTK'));
    } finally {
        await api.close();
    }
});

test('resetting a trial is refused for the administrator account', async () => {
    const store = createMemoryStore();
    const adminId = crypto.randomUUID();
    await store.createUser({
        id: adminId,
        userName: MASTER_ADMIN.username,
        displayName: MASTER_ADMIN.username,
        email: MASTER_ADMIN.email,
        passwordHash: await hashPassword(MASTER_ADMIN.password),
        role: 'admin',
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin',
        licenseStartedAt: new Date(),
        licenseExpiresAt: null
    });

    const api = await startApi(store);
    try {
        const admin = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password
        });
        const result = await api.post(
            `/api/admin/technicians/${adminId}/reset-trial`, {}, admin.json.tokens.accessToken);
        assert.equal(result.status, 400);
        assert.equal(result.json.error.code, 'admin-protected');
    } finally {
        await api.close();
    }
});
