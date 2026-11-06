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
    demoteStubAccounts,
    verifyPassword,
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

    if (options.stubRow) {
        // Exactly what the previous login endpoint wrote for any typed user name.
        await pool.query(
            `INSERT INTO users (id, username, password, hwid, role)
             VALUES (1, $1, $2, 'DEV_HOST', 'Admin')`,
            [options.stubRow, 'TypedPassword1']);
    }

    const store = createPostgresStore(pool);
    await migrateUsersTable(pool, () => {});
    await seedMasterAdmin(store, pool, () => {});
    await demoteStubAccounts(pool, () => {});
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

test('the seeded master administrator keeps its exact credentials, 20 permissions and no HWID', async () => {
    const { store, pool } = await createDatabase();

    const row = await pool.query(
        'SELECT username, email, password, role, is_verified, hwid, license_type, license_expires_at FROM users WHERE lower(email) = lower($1)',
        [MASTER_ADMIN.email]);
    assert.equal(row.rows.length, 1);

    const stored = row.rows[0];
    assert.equal(stored.username, 'Admin Bashir');
    assert.equal(stored.email, 'bviptool@gmail.com');
    assert.equal(stored.role, 'lifetime_admin');
    assert.equal(stored.is_verified, true);
    assert.equal(stored.hwid, null);
    assert.equal(stored.license_type, 'lifetime_admin');
    assert.equal(stored.license_expires_at, null);

    // The column holds a BCrypt hash, never the plain password, and hashes that exact password.
    assert.match(stored.password, /^\$2[aby]\$10\$/);
    assert.notEqual(stored.password, 'Bashir0935784653AdminB');
    const check = await verifyPassword('Bashir0935784653AdminB', stored.password);
    assert.equal(check.ok, true);
    const wrong = await verifyPassword('Bashir0935784653Admin', stored.password);
    assert.equal(wrong.ok, false);

    const api = await startApi(store);
    try {
        const session = await api.post('/api/auth/login', {
            userName: 'Admin Bashir',
            password: 'Bashir0935784653AdminB',
            deviceFingerprint: 'ANY-WORKSTATION'
        });
        assert.equal(session.status, 200);
        assert.equal(session.json.permissions.length, 20);
        assert.ok(session.json.permissions.includes('All'));
        assert.deepEqual(session.json.user.roles, ['Admin', 'lifetime_admin']);
        assert.equal(session.json.user.hardwareId, null);
        assert.equal(session.json.license.status, 'Active');
        assert.equal(session.json.license.expiresAt, null);
    } finally {
        await api.close();
    }
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

test('accounts the old stub stored as administrators are demoted, not left as back doors', async () => {
    const { store } = await createDatabase({ legacyTable: true, stubRow: 'zzz.random' });

    const demoted = await store.findByLogin('zzz.random');
    assert.ok(demoted, 'the row written by the stub still exists');
    assert.equal(demoted.role, 'technician');
    assert.equal(demoted.isVerified, false);

    const api = await startApi(store);
    try {
        // Its password was the typed text, so it must not open a session any more either.
        const attempt = await api.post('/api/auth/login', {
            userName: 'zzz.random', password: 'TypedPassword1', deviceFingerprint: 'PC-1'
        });
        assert.equal(attempt.status, 403);
        assert.equal(attempt.json.error.code, 'email-not-verified');
        assert.equal(attempt.json.tokens, undefined);
    } finally {
        await api.close();
    }
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

test('a technician registers, verifies and signs in from any workstation', async () => {
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
        assert.equal(signedIn.json.user.hardwareId, null, 'no machine is bound to the account');
        assert.deepEqual(signedIn.json.user.roles, ['Technician']);
        assert.ok(signedIn.json.permissions.includes('MTK'));

        // The second machine signs in just as well, and nothing was saved for the first one.
        const secondMachine = await api.post('/api/auth/login', {
            userName: 'store.tech', password: 'StrongPass1', deviceFingerprint: 'MACHINE-B'
        });
        assert.equal(secondMachine.status, 200);
        const fingerprints = await pool.query('SELECT hwid FROM users WHERE username = $1', ['store.tech']);
        assert.equal(fingerprints.rows[0].hwid, null);

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

test('the console routes read the real directory and extend a licence in PostgreSQL', async () => {
    const { store, pool } = await createDatabase();
    const { hashPassword } = require('../../server.js');
    await store.createUser({
        id: 'a1b2c3d4-0000-4000-8000-00000000000a',
        userName: 'list.tech',
        email: 'list.tech@example.com',
        passwordHash: await hashPassword('StrongPass1'),
        role: 'technician',
        isVerified: true,
        licenseType: 'trial',
        licenseStartedAt: new Date(),
        licenseExpiresAt: new Date(Date.now() + 2 * 86400000)
    });

    const api = await startApi(store);
    try {
        const login = await api.post('/api/auth/login', {
            userName: MASTER_ADMIN.email, password: MASTER_ADMIN.password
        });
        assert.equal(login.status, 200);
        const token = login.json.tokens.accessToken;

        const workstations = await api.get('/api/admin/workstations', token);
        assert.equal(workstations.status, 200);
        assert.equal(workstations.json.deviceBinding, 'disabled');
        const listed = workstations.json.items.find(item => item.userName === 'list.tech');
        assert.ok(listed, 'the technician appears in the directory');
        assert.equal(listed.hardwareId, null);

        const search = await api.get('/api/admin/technicians?q=list&page=1&pageSize=10', token);
        assert.equal(search.status, 200);
        assert.equal(search.json.totalCount, 1);
        assert.equal(search.json.items[0].userName, 'list.tech');

        const overview = await api.get('/api/admin/overview', token);
        assert.equal(overview.status, 200);
        assert.equal(overview.json.totalLicenses, 1);
        assert.equal(overview.json.activeLicenses, 1);

        const extended = await api.post('/api/admin/extend-license', {
            userName: 'list.tech', days: 90
        }, token);
        assert.equal(extended.status, 200);
        assert.equal(extended.json.days, 90);

        const stored = await pool.query(
            'SELECT license_expires_at, is_active FROM users WHERE username = $1', ['list.tech']);
        const remaining = (new Date(stored.rows[0].license_expires_at).getTime() - Date.now()) / 86400000;
        assert.ok(remaining > 91.9 && remaining <= 93.1, `licence now ends in ${remaining} days`);

        const logs = await api.get('/api/admin/logs', token);
        assert.equal(logs.status, 200);
        assert.ok(logs.json.items.some(entry => /extended by 90 day/.test(entry.message)));
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
