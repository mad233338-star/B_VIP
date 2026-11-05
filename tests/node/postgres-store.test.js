'use strict';

/*
 * End-to-end authentication tests against the real PostgreSQL store code.
 *
 * A PostgreSQL-compatible in-memory database (pg-mem) executes the shipped SQL: the schema
 * migration (including widening a legacy integer users.id), master administrator seeding,
 * registration, OTP verification and HWID-bound login over HTTP.
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');

const { newDb } = require('pg-mem');

const {
    createApp,
    createPostgresStore,
    migrateUsersTable,
    seedMasterAdmin,
    MASTER_ADMIN
} = require('../../server.js');

const SIGNING_KEY = 'pg-store-test-key-0123456789-abcdefghij';

async function createDatabase(options = {}) {
    const db = newDb({ autoCreateForeignKeyIndices: true });
    const pg = db.adapters.createPg();
    const pool = new pg.Pool();

    if (options.legacyTable) {
        // The shape the previous deployment created: an integer primary key and plaintext password.
        await pool.query(`
            CREATE TABLE users (
                id INTEGER PRIMARY KEY,
                username VARCHAR(100) UNIQUE NOT NULL,
                password VARCHAR(255) NOT NULL,
                hwid VARCHAR(255),
                role VARCHAR(50) DEFAULT 'Admin',
                is_active BOOLEAN DEFAULT TRUE,
                expires_at TIMESTAMP DEFAULT (NOW() + INTERVAL '365 days'),
                created_at TIMESTAMP DEFAULT NOW()
            );
        `);
    }

    const store = createPostgresStore(pool);
    await migrateUsersTable(pool, () => {});
    await seedMasterAdmin(store, pool, () => {});
    return { pool, store };
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
        async post(path, body) {
            const response = await fetch(base + path, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
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

test('the schema migration and master admin seed run on PostgreSQL', async () => {
    const { store } = await createDatabase();
    const admin = await store.findByLogin(MASTER_ADMIN.email);

    assert.ok(admin, 'the master administrator exists');
    assert.equal(admin.userName, MASTER_ADMIN.username);
    assert.equal(admin.email, MASTER_ADMIN.email);
    assert.equal(admin.role, 'admin');
    assert.equal(admin.isVerified, true);
    assert.equal(admin.hwid, null);
    assert.equal(admin.licenseType, 'lifetime_admin');
    assert.equal(admin.licenseExpiresAt, null);
    assert.match(admin.passwordHash, /^\$2[aby]\$/);
});

test('a legacy integer users.id is widened so GUID identifiers can be stored', async () => {
    const { pool, store } = await createDatabase({ legacyTable: true });

    const column = await pool.query(
        `SELECT data_type FROM information_schema.columns
         WHERE table_name = 'users' AND column_name = 'id' LIMIT 1`);
    // PostgreSQL reports character varying; an engine that ignores the length reports text.
    assert.match(String(column.rows[0].data_type), /^(character varying|text)$/);

    const created = await store.createUser({
        id: 'c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d',
        userName: 'guid.user',
        email: 'guid.user@example.com',
        passwordHash: '$2a$10$abcdefghijklmnopqrstuv',
        role: 'technician'
    });
    assert.equal(created.id, 'c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d');
});

test('the master admin signs in over HTTP against the real store', async () => {
    const { store } = await createDatabase();
    const api = await startApi(store);
    try {
        const rejected = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: 'definitely-not-the-password'
        });
        assert.equal(rejected.status, 401);
        assert.equal(rejected.json.error.code, 'invalid-credentials');

        const accepted = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email,
            password: MASTER_ADMIN.password,
            deviceFingerprint: 'ADMIN-PC-ANY'
        });
        assert.equal(accepted.status, 200);
        assert.equal(accepted.json.user.email, MASTER_ADMIN.email);
        assert.deepEqual(accepted.json.user.roles, ['Admin', 'lifetime_admin']);
        assert.equal(accepted.json.license.status, 'Active');
        assert.equal(accepted.json.license.expiresAt, null);

        // The admin is never bound to a workstation, even after signing in from another one.
        const fromElsewhere = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.username,
            password: MASTER_ADMIN.password,
            deviceFingerprint: 'ANOTHER-PC'
        });
        assert.equal(fromElsewhere.status, 200);
        const reloaded = await store.findByLogin(MASTER_ADMIN.email);
        assert.equal(reloaded.hwid, null);
    } finally {
        await api.close();
    }
});

test('a technician registers, verifies, binds one workstation and is refused on another', async () => {
    const { store, pool } = await createDatabase();
    let code = null;
    const mailer = {
        configured: true,
        async sendVerificationCode(to, userName, otp) {
            code = otp;
            return true;
        }
    };

    const api = await startApi(store, mailer);
    try {
        const registered = await api.post('/api/auth/register', {
            userName: 'store.tech',
            email: 'store.tech@example.com',
            password: 'StrongPass1',
            acceptedTerms: true,
            hardwareId: 'MACHINE-A'
        });
        assert.equal(registered.status, 200);
        assert.equal(registered.json.requiresVerification, true);
        assert.equal(registered.json.verificationEmailSent, true);
        assert.match(code, /^\d{6}$/);

        // Stored as BCrypt, never as the submitted text.
        const stored = await pool.query('SELECT password FROM users WHERE username = $1', ['store.tech']);
        assert.match(stored.rows[0].password, /^\$2[aby]\$/);
        assert.notEqual(stored.rows[0].password, 'StrongPass1');

        // Unverified accounts cannot reach the workstation.
        const blocked = await api.post('/api/auth/login', {
            userName: 'store.tech', password: 'StrongPass1', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(blocked.status, 403);
        assert.equal(blocked.json.error.code, 'email-not-verified');

        const verified = await api.post('/api/auth/verify-code', { userName: 'store.tech', code });
        assert.equal(verified.status, 200);
        assert.equal(verified.json.verified, true);

        const licence = await pool.query(
            'SELECT license_type, license_started_at, license_expires_at, otp_code FROM users WHERE username = $1',
            ['store.tech']);
        assert.equal(licence.rows[0].license_type, 'trial');
        assert.equal(licence.rows[0].otp_code, null);
        const days = (new Date(licence.rows[0].license_expires_at).getTime() - Date.now()) / 86400000;
        assert.ok(days > 2.9 && days <= 3.01, `trial was ${days} days`);

        const signedIn = await api.post('/api/auth/login', {
            userName: 'store.tech', password: 'StrongPass1', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(signedIn.status, 200);
        assert.equal(signedIn.json.license.status, 'Trial');
        assert.equal(signedIn.json.user.hardwareId, 'MACHINE-A');
        assert.deepEqual(signedIn.json.user.roles, ['Technician']);
        assert.ok(signedIn.json.permissions.includes('MTK'));

        const hijack = await api.post('/api/auth/login', {
            userName: 'store.tech', password: 'StrongPass1', deviceFingerprint: 'MACHINE-B'
        });
        assert.equal(hijack.status, 403);
        assert.equal(hijack.json.error.code, 'device-mismatch');

        // ... and a password is still required even from the bound machine.
        const wrongPassword = await api.post('/api/auth/login', {
            userName: 'store.tech', password: 'guess-guess', deviceFingerprint: 'MACHINE-A'
        });
        assert.equal(wrongPassword.status, 401);
    } finally {
        await api.close();
    }
});

test('an expired technician licence grants an empty permission set', async () => {
    const { store, pool } = await createDatabase();
    await store.createUser({
        id: 'a1b2c3d4-0000-4000-8000-000000000001',
        userName: 'expired.tech',
        email: 'expired.tech@example.com',
        passwordHash: await require('../../server.js').hashPassword('StrongPass1'),
        role: 'technician',
        isVerified: true,
        licenseType: 'trial',
        licenseStartedAt: new Date(Date.now() - 10 * 86400000),
        licenseExpiresAt: new Date(Date.now() - 86400000)
    });
    await pool.query('UPDATE users SET hwid = $1 WHERE username = $2', ['PC-1', 'expired.tech']);
    await pool.query('UPDATE users SET expires_at = license_expires_at WHERE username = $1', ['expired.tech']);

    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'expired.tech', password: 'StrongPass1', deviceFingerprint: 'PC-1'
        });
        assert.equal(result.status, 200);
        assert.equal(result.json.license.status, 'Expired');
        assert.deepEqual(result.json.permissions, []);
    } finally {
        await api.close();
    }
});

test('a suspended technician is refused', async () => {
    const { store } = await createDatabase();
    const { hashPassword } = require('../../server.js');
    await store.createUser({
        id: 'a1b2c3d4-0000-4000-8000-000000000002',
        userName: 'suspended.tech',
        email: 'suspended.tech@example.com',
        passwordHash: await hashPassword('StrongPass1'),
        role: 'technician',
        isActive: false,
        isVerified: true,
        licenseType: 'active',
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 30 * 86400000)
    });

    const api = await startApi(store);
    try {
        const result = await api.post('/api/auth/login', {
            userName: 'suspended.tech', password: 'StrongPass1', deviceFingerprint: 'PC-1'
        });
        assert.equal(result.status, 403);
        assert.equal(result.json.error.code, 'account-suspended');
    } finally {
        await api.close();
    }
});
