'use strict';

/*
 * B_VIP Tool - cloud licensing and remote support server.
 *
 * Authentication rules enforced here (and nowhere else):
 *   - an account is found by user name or e-mail address; the password is verified with BCrypt;
 *   - a technician account is bound to the first computer that signs in (HWID) and refused on any
 *     other machine until an administrator resets the binding;
 *   - administrators bypass the HWID check and the e-mail verification step;
 *   - a technician whose trial/subscription has run out can still sign in, but receives an expired
 *     licence and no permissions, so every device operation stays locked in the client;
 *   - when the database is unreachable the server fails closed (503): it never answers a login with
 *     a canned "success" payload.
 */

const http = require('http');
const crypto = require('crypto');
const bcrypt = require('bcryptjs');
const nodemailer = require('nodemailer');

// -------------------------------------------------------------
// 1. Constants
// -------------------------------------------------------------
const SERVICE_NAME = 'B_VIP Licensing & Remote Server';
const SERVER_VERSION = '2.0.0';

const EMAIL_REGEX = /^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$/;
const OTP_TTL_MINUTES = 15;
const TRIAL_DAYS = 3;

const ACCESS_TOKEN_MINUTES = 60;
const REFRESH_TOKEN_DAYS = 14;

const MAX_FAILED_ATTEMPTS = 5;
const LOCKOUT_MINUTES = 15;

const DEFAULT_SIGNING_KEY = 'B-VIP-SIGNING-KEY-2026-bashir-alfarhan-9f4c2e7a1b8d6350';

// The one account that always owns the management console.
const MASTER_ADMIN = {
    username: 'Admin Bashir',
    email: 'bviptool@gmail.com',
    password: 'Bashir0935784653AdminB',
    role: 'lifetime_admin'
};

const ADMIN_PERMISSIONS = [
    'All', 'Admin', 'Users.Read', 'Users.Write', 'Licenses.Read', 'Licenses.Write',
    'Operations.Read', 'Operations.Write', 'Tickets.Read', 'Tickets.Write',
    'Audit.Read', 'Settings.Write', 'RemoteControl', 'ScreenShare', 'CurtainMode',
    'Bypass', 'FRP', 'iCloud', 'Repair', 'Flash'
];

const TECHNICIAN_PERMISSIONS = [
    'Devices.Read', 'Devices.Operate', 'Operations.Read', 'Operations.Write',
    'Tickets.Read', 'Tickets.Write', 'Reports.Read', 'Reports.Write',
    'ADB', 'Fastboot', 'FRP', 'Flash', 'Repair', 'MTK', 'Qualcomm', 'SPD', 'Samsung'
];

// Proper error codes the desktop client already understands.
const ERRORS = {
    invalidCredentials: { status: 401, code: 'invalid-credentials', message: 'Invalid username or password.' },
    deviceMismatch: {
        status: 403,
        code: 'device-mismatch',
        message: 'This account is bound to another computer. Please contact administrator to reset HWID.'
    },
    emailNotVerified: {
        status: 403,
        code: 'email-not-verified',
        message: 'This account has not been verified yet. Enter the 6-digit code sent to your e-mail address.'
    },
    accountSuspended: { status: 403, code: 'account-suspended', message: 'This account is suspended. Contact your administrator.' },
    accountLocked: {
        status: 403,
        code: 'account-locked',
        message: 'This account is temporarily locked after too many failed attempts.'
    },
    invalidEmail: {
        status: 400,
        code: 'invalid-email',
        message: 'Enter a valid e-mail address (for example name@example.com).'
    },
    weakPassword: {
        status: 400,
        code: 'weak-password',
        message: 'Choose a password of at least 8 characters.'
    },
    accountExists: { status: 409, code: 'account-exists', message: 'This user name or e-mail address is already registered.' },
    invalidCode: { status: 400, code: 'invalid-code', message: 'The verification code is incorrect.' },
    codeExpired: { status: 400, code: 'code-expired', message: 'The verification code has expired. Request a new one.' },
    rateLimited: { status: 429, code: 'too-many-attempts', message: 'Too many attempts. Wait a moment and try again.' },
    databaseUnavailable: {
        status: 503,
        code: 'database-unavailable',
        message: 'The service cannot verify accounts right now. Please try again shortly.'
    },
    notFound: { status: 404, code: 'not-found', message: 'Endpoint not found.' },
    serverError: { status: 500, code: 'server-error', message: 'The server could not complete the request.' }
};

// -------------------------------------------------------------
// 2. Small helpers
// -------------------------------------------------------------
function normalizeRole(rawRole) {
    const role = String(rawRole || '').trim();
    if (role.length === 0) {
        return 'technician';
    }

    return /admin/i.test(role) ? 'admin' : 'technician';
}

function isAdminRole(rawRole) {
    return normalizeRole(rawRole) === 'admin';
}

function looksLikeBcrypt(stored) {
    return typeof stored === 'string' && /^\$2[aby]\$/.test(stored);
}

function hashPassword(password) {
    return bcrypt.hash(String(password), 10);
}

/**
 * Verifies a password against the stored value. Rows written by the previous build kept the
 * password verbatim, so those are accepted once (constant time) and re-hashed by the caller.
 */
async function verifyPassword(password, stored) {
    const candidates = [String(stored ?? ''), String(stored ?? '').replace(/^bcrypt:/, '')];
    for (const candidate of candidates) {
        if (looksLikeBcrypt(candidate)) {
            if (await bcrypt.compare(String(password), candidate)) {
                return { ok: true, legacy: false };
            }
            continue;
        }

        if (candidate.length > 0) {
            const a = Buffer.from(candidate, 'utf8');
            const b = Buffer.from(String(password), 'utf8');
            if (a.length === b.length && crypto.timingSafeEqual(a, b)) {
                return { ok: true, legacy: true };
            }
        }
    }

    return { ok: false, legacy: false };
}

function safeEqual(a, b) {
    const left = Buffer.from(String(a ?? ''), 'utf8');
    const right = Buffer.from(String(b ?? ''), 'utf8');
    return left.length === right.length && crypto.timingSafeEqual(left, right);
}

function createOtpCode() {
    return String(crypto.randomInt(0, 1000000)).padStart(6, '0');
}

function newId() {
    return crypto.randomUUID();
}

function base64url(input) {
    return Buffer.from(input).toString('base64').replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function signToken(payload, signingKey) {
    const header = base64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
    const body = base64url(JSON.stringify(payload));
    const signature = crypto
        .createHmac('sha256', signingKey)
        .update(`${header}.${body}`)
        .digest('base64')
        .replace(/=/g, '')
        .replace(/\+/g, '-')
        .replace(/\//g, '_');
    return `${header}.${body}.${signature}`;
}

/** Returns the payload of a valid, unexpired token, or null. */
function verifyToken(token, signingKey, expectedType) {
    if (typeof token !== 'string') {
        return null;
    }

    const parts = token.split('.');
    if (parts.length !== 3) {
        return null;
    }

    const expected = crypto
        .createHmac('sha256', signingKey)
        .update(`${parts[0]}.${parts[1]}`)
        .digest('base64')
        .replace(/=/g, '')
        .replace(/\+/g, '-')
        .replace(/\//g, '_');

    if (!safeEqual(expected, parts[2])) {
        return null;
    }

    let payload;
    try {
        payload = JSON.parse(Buffer.from(parts[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
    } catch (_) {
        return null;
    }

    if (payload.typ !== expectedType) {
        return null;
    }

    if (typeof payload.exp !== 'number' || payload.exp * 1000 <= Date.now()) {
        return null;
    }

    return payload;
}

function readBody(req, limitBytes = 1024 * 1024) {
    return new Promise((resolve, reject) => {
        let size = 0;
        const chunks = [];
        req.on('data', chunk => {
            size += chunk.length;
            if (size > limitBytes) {
                reject(new Error('payload-too-large'));
                req.destroy();
                return;
            }
            chunks.push(chunk);
        });
        req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
        req.on('error', reject);
    });
}

function parseBody(raw) {
    if (!raw || raw.trim().length === 0) {
        return {};
    }

    try {
        return JSON.parse(raw);
    } catch (_) {
        const data = {};
        for (const [key, value] of new URLSearchParams(raw).entries()) {
            data[key] = value;
        }
        return data;
    }
}

function pick(data, ...names) {
    for (const name of names) {
        const value = data[name];
        if (typeof value === 'string' && value.trim().length > 0) {
            return value.trim();
        }
    }
    return '';
}

function isoOrNull(value) {
    if (!value) {
        return null;
    }
    const date = value instanceof Date ? value : new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

// -------------------------------------------------------------
// 3. PostgreSQL store
// -------------------------------------------------------------
const COLUMN_MAP = {
    userName: 'username',
    displayName: 'display_name',
    email: 'email',
    passwordHash: 'password',
    hwid: 'hwid',
    role: 'role',
    isActive: 'is_active',
    isVerified: 'is_verified',
    otpCode: 'otp_code',
    otpExpiresAt: 'otp_expires_at',
    licenseType: 'license_type',
    licenseStartedAt: 'license_started_at',
    licenseExpiresAt: 'license_expires_at',
    hwidResetCount: 'hwid_reset_count',
    failedAttempts: 'failed_attempts',
    lockoutUntil: 'lockout_until',
    lastLoginAt: 'last_login_at'
};

function mapRow(row) {
    return {
        id: row.id,
        userName: row.username,
        displayName: row.display_name || row.username,
        email: row.email || '',
        passwordHash: row.password,
        hwid: row.hwid || null,
        role: normalizeRole(row.role),
        rawRole: row.role,
        isActive: row.is_active !== false,
        isVerified: row.is_verified === true,
        otpCode: row.otp_code || null,
        otpExpiresAt: row.otp_expires_at || null,
        licenseType: row.license_type || (isAdminRole(row.role) ? 'lifetime_admin' : 'trial'),
        licenseStartedAt: row.license_started_at || row.created_at || null,
        licenseExpiresAt: row.license_expires_at || row.expires_at || null,
        hwidResetCount: Number(row.hwid_reset_count || 0),
        failedAttempts: Number(row.failed_attempts || 0),
        lockoutUntil: row.lockout_until || null,
        lastLoginAt: row.last_login_at || null,
        createdUtc: row.created_at || null
    };
}

function createPostgresStore(pool) {
    return {
        async findByLogin(identifier) {
            const result = await pool.query(
                `SELECT * FROM users
                 WHERE lower(username) = lower($1)
                    OR (email IS NOT NULL AND lower(email) = lower($1))
                 LIMIT 1`,
                [identifier]);
            return result.rows.length > 0 ? mapRow(result.rows[0]) : null;
        },

        async findById(id) {
            const result = await pool.query('SELECT * FROM users WHERE id = $1 LIMIT 1', [id]);
            return result.rows.length > 0 ? mapRow(result.rows[0]) : null;
        },

        async findByIdOrEmail(value) {
            const result = await pool.query(
                `SELECT * FROM users
                 WHERE lower(username) = lower($1) OR (email IS NOT NULL AND lower(email) = lower($1))
                 LIMIT 1`,
                [value]);
            return result.rows.length > 0 ? mapRow(result.rows[0]) : null;
        },

        async createUser(fields) {
            const result = await pool.query(
                `INSERT INTO users
                    (id, username, display_name, email, password, hwid, role, is_active, is_verified,
                     otp_code, otp_expires_at, license_type, license_started_at, license_expires_at,
                     hwid_reset_count, failed_attempts, created_at)
                 VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,0,0,NOW())
                 RETURNING *`,
                [
                    fields.id, fields.userName, fields.displayName || fields.userName, fields.email,
                    fields.passwordHash, fields.hwid || null, fields.role || 'technician',
                    fields.isActive !== false, fields.isVerified === true,
                    fields.otpCode || null, fields.otpExpiresAt || null,
                    fields.licenseType || 'trial', fields.licenseStartedAt || new Date(),
                    fields.licenseExpiresAt || null
                ]);
            return mapRow(result.rows[0]);
        },

        async updateUser(id, patch) {
            const assignments = [];
            const values = [];
            let index = 1;

            for (const [key, value] of Object.entries(patch)) {
                const column = COLUMN_MAP[key];
                if (!column) {
                    continue;
                }
                assignments.push(`${column} = $${index}`);
                values.push(value);
                index += 1;

                // Keep the legacy expiry column in step with the licence date.
                if (key === 'licenseExpiresAt' && !('expiresAt' in patch)) {
                    assignments.push(`expires_at = $${index}`);
                    values.push(value);
                    index += 1;
                }
            }

            if (assignments.length === 0) {
                return this.findById(id);
            }

            values.push(id);
            const result = await pool.query(
                `UPDATE users SET ${assignments.join(', ')} WHERE id = $${index} RETURNING *`,
                values);
            return result.rows.length > 0 ? mapRow(result.rows[0]) : null;
        }
    };
}

async function migrateUsersTable(pool, log) {
    const table = await pool.query(
        `SELECT table_name FROM information_schema.tables WHERE table_name = 'users' LIMIT 1`);

    if (table.rows.length === 0) {
        await pool.query(`
            CREATE TABLE users (
                id VARCHAR(100) PRIMARY KEY,
                username VARCHAR(100) UNIQUE NOT NULL,
                password VARCHAR(255) NOT NULL,
                hwid VARCHAR(255),
                role VARCHAR(50) DEFAULT 'technician',
                is_active BOOLEAN DEFAULT TRUE,
                expires_at TIMESTAMP,
                created_at TIMESTAMP DEFAULT NOW()
            );
        `);
        log('users table created.');
    }

    // Older deployments declared users.id as an integer while the API passes GUID strings, which
    // produced "invalid input syntax for type integer". Widen the column so both agree.
    const idType = await pool.query(
        `SELECT data_type FROM information_schema.columns
         WHERE table_name = 'users' AND column_name = 'id' LIMIT 1`);
    const currentType = String(idType.rows[0]?.data_type || '');
    if (/^(smallint|integer|bigint|numeric|real|double precision)$/i.test(currentType)) {
        log(`Migrating users.id from ${currentType} to varchar(100)`);
        try {
            await pool.query('ALTER TABLE users ALTER COLUMN id TYPE VARCHAR(100) USING id::text');
        } catch (error) {
            if (!/syntax error/i.test(String(error.message))) {
                throw error;
            }

            // PostgreSQL-compatible engines that reject the USING clause still accept the plain
            // form; PostgreSQL itself always takes the branch above.
            await pool.query('ALTER TABLE users ALTER COLUMN id TYPE VARCHAR(100)');
        }
    }

    const columns = [
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS display_name VARCHAR(120)',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS email VARCHAR(255)',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS is_verified BOOLEAN DEFAULT FALSE',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS otp_code VARCHAR(10)',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS otp_expires_at TIMESTAMP',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS license_type VARCHAR(50)',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS license_started_at TIMESTAMP',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS license_expires_at TIMESTAMP',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS hwid_reset_count INTEGER DEFAULT 0',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS failed_attempts INTEGER DEFAULT 0',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS lockout_until TIMESTAMP',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMP',
        'ALTER TABLE users ADD COLUMN IF NOT EXISTS updated_at TIMESTAMP DEFAULT NOW()'
    ];
    for (const statement of columns) {
        await pool.query(statement);
    }

    await pool.query(`
        CREATE UNIQUE INDEX IF NOT EXISTS users_email_unique
        ON users (lower(email)) WHERE email IS NOT NULL;
    `);

    // Rows created before licences existed have only expires_at.
    await pool.query(`
        UPDATE users
        SET license_expires_at = expires_at
        WHERE license_expires_at IS NULL AND expires_at IS NOT NULL
          AND role NOT ILIKE '%admin%';
    `);
}

async function seedMasterAdmin(store, pool, log) {
    const passwordHash = await hashPassword(MASTER_ADMIN.password);

    // An existing row may already hold the address under a different user name.
    if (pool) {
        await pool.query(
            `UPDATE users SET username = $1
             WHERE email IS NOT NULL AND lower(email) = lower($2) AND lower(username) <> lower($1)`,
            [MASTER_ADMIN.username, MASTER_ADMIN.email]);
    }

    const existing = await store.findByLogin(MASTER_ADMIN.email);
    if (existing) {
        await store.updateUser(existing.id, {
            userName: MASTER_ADMIN.username,
            displayName: MASTER_ADMIN.username,
            email: MASTER_ADMIN.email,
            passwordHash,
            role: MASTER_ADMIN.role,
            isActive: true,
            isVerified: true,
            hwid: null,
            licenseType: 'lifetime_admin',
            licenseStartedAt: existing.licenseStartedAt || new Date(),
            licenseExpiresAt: null,
            failedAttempts: 0,
            lockoutUntil: null
        });
        log('Master administrator record verified.');
        return;
    }

    await store.createUser({
        id: newId(),
        userName: MASTER_ADMIN.username,
        displayName: MASTER_ADMIN.username,
        email: MASTER_ADMIN.email,
        passwordHash,
        hwid: null,
        role: MASTER_ADMIN.role,
        isActive: true,
        isVerified: true,
        licenseType: 'lifetime_admin',
        licenseStartedAt: new Date(),
        licenseExpiresAt: null
    });
    log('Master administrator record created.');
}

/*
 * The previous build inserted a row for *every* user name typed into the login form, always with
 * the role 'Admin', the typed text as the password and no e-mail address. Left alone those rows
 * would be administrator back doors, so they are demoted on start. A genuine administrator always
 * has an e-mail address, and the master account is excluded by name.
 */
async function demoteStubAccounts(pool, log) {
    if (!pool) {
        return 0;
    }

    const result = await pool.query(
        `UPDATE users
         SET role = 'technician', is_verified = FALSE, is_active = TRUE
         WHERE role ILIKE '%admin%'
           AND email IS NULL
           AND lower(username) <> lower($1)
         RETURNING username`,
        [MASTER_ADMIN.username]);

    const count = result.rows ? result.rows.length : 0;
    if (count > 0) {
        log(`Demoted ${count} account(s) that the previous build stored as administrators without an e-mail address.`);
    }

    return count;
}

// -------------------------------------------------------------
// 4. E-mail (OTP) delivery
// -------------------------------------------------------------
function createMailer(config, log) {
    const host = config.host;
    const user = config.user;
    const password = config.password;
    const from = config.from || user || 'no-reply@b-vip.onrender.com';

    if (!host || !user || !password) {
        log('SMTP is not configured (Email__Host / Email__UserName / Email__Password); verification codes cannot be delivered.');
        return {
            configured: false,
            async sendVerificationCode() {
                return false;
            }
        };
    }

    const transport = nodemailer.createTransport({
        host,
        port: Number(config.port || 587),
        secure: String(config.secure || config.enableSsl || 'true') !== 'false' && Number(config.port || 587) === 465,
        auth: { user, pass: password }
    });

    return {
        configured: true,
        async sendVerificationCode(to, userName, code) {
            const body = [
                '--------------------------------------------------',
                'Welcome to B_VIP Tool,',
                '',
                'Thank you for registering on our smart device maintenance and servicing platform.',
                'To complete your account activation and start your 3-day free trial, use the following verification code inside the tool:',
                '',
                code,
                '',
                'Validity: This code is valid for 15 minutes only.',
                'Security: The B_VIP support team will never ask for this code. Do not share it with anyone.',
                'If you did not request an account on B_VIP Tool, you can safely ignore this email; no action will be taken on your data.',
                '',
                'B_VIP Tool Team',
                'All-in-One Solution for Smart Device Servicing & Maintenance.',
                '--------------------------------------------------'
            ].join('\n');

            await transport.sendMail({
                from: `${config.fromName || 'B_VIP Tool'} <${from}>`,
                to,
                subject: '[B_VIP Tool] Account Verification Code',
                text: body
            });
            log(`Verification code sent to ${to} for ${userName}.`);
            return true;
        }
    };
}

// -------------------------------------------------------------
// 5. Response builders
// -------------------------------------------------------------
function permissionsFor(user) {
    if (user.role === 'admin') {
        return ADMIN_PERMISSIONS;
    }

    // An expired licence keeps the account usable but grants nothing.
    const license = evaluateLicense(user);
    return license.active ? TECHNICIAN_PERMISSIONS : [];
}

function evaluateLicense(user) {
    if (user.role === 'admin') {
        return {
            active: true,
            status: 'Active',
            type: 'lifetime_admin',
            startedAt: user.licenseStartedAt,
            expiresAt: null,
            daysRemaining: null
        };
    }

    const expiresAt = user.licenseExpiresAt ? new Date(user.licenseExpiresAt) : null;
    if (!expiresAt) {
        return { active: false, status: 'Expired', type: user.licenseType || 'trial', startedAt: user.licenseStartedAt, expiresAt: null, daysRemaining: 0 };
    }

    const remainingMs = expiresAt.getTime() - Date.now();
    const active = remainingMs > 0;
    const type = String(user.licenseType || 'trial').toLowerCase();

    return {
        active,
        status: active ? (type === 'trial' ? 'Trial' : 'Active') : 'Expired',
        type,
        startedAt: user.licenseStartedAt,
        expiresAt,
        daysRemaining: active ? Math.ceil(remainingMs / 86400000) : 0
    };
}

function buildLicenseSummary(user) {
    const state = evaluateLicense(user);
    return {
        licenseId: user.id,
        licenseKey: `BV-${String(user.id).replace(/-/g, '').slice(0, 12).toUpperCase()}`,
        planCode: state.type,
        planName: state.type === 'trial' ? '3-Day Free Trial' : (state.type === 'lifetime_admin' ? 'Administrator (Lifetime)' : 'Subscription'),
        status: state.status,
        startsAt: isoOrNull(state.startedAt) || new Date().toISOString(),
        expiresAt: isoOrNull(state.expiresAt),
        maxActivations: state.type === 'lifetime_admin' ? 0 : 1,
        currentActivations: user.hwid ? 1 : 0,
        features: permissionsFor(user)
    };
}

function buildUserSummary(user) {
    const license = evaluateLicense(user);

    return {
        id: user.id,
        userName: user.userName,
        displayName: user.displayName || user.userName,
        email: user.email || '',
        status: user.isActive === false ? 'Suspended' : (license.active ? 'Active' : 'Expired'),
        roles: user.role === 'admin' ? ['Admin', 'lifetime_admin'] : ['Technician'],
        createdUtc: isoOrNull(user.createdUtc) || new Date().toISOString(),
        lastLoginUtc: isoOrNull(user.lastLoginAt),
        emailVerified: user.role === 'admin' ? true : user.isVerified === true,
        hardwareId: user.hwid || null,
        hardwareIdResetCount: user.hwidResetCount || 0
    };
}

function createTokens(user, signingKey) {
    const now = Math.floor(Date.now() / 1000);
    const roles = user.role === 'admin' ? ['Admin', 'lifetime_admin'] : ['Technician'];
    const permissions = permissionsFor(user);

    const base = {
        sub: user.id,
        nameid: user.id,
        unique_name: user.userName,
        name: user.displayName || user.userName,
        email: user.email || '',
        role: roles[0],
        roles,
        permissions,
        iss: 'B_VIP_Server',
        aud: 'B_VIP_Client',
        nbf: now - 60,
        iat: now
    };

    const accessExpires = now + ACCESS_TOKEN_MINUTES * 60;
    const refreshExpires = now + REFRESH_TOKEN_DAYS * 86400;

    const accessToken = signToken({ ...base, typ: 'access', exp: accessExpires, jti: newId() }, signingKey);
    const refreshToken = signToken({ sub: user.id, typ: 'refresh', exp: refreshExpires, jti: newId() }, signingKey);

    return {
        accessToken,
        refreshToken,
        expiresUtc: new Date(accessExpires * 1000).toISOString(),
        refreshExpiresUtc: new Date(refreshExpires * 1000).toISOString()
    };
}

function buildSession(user, signingKey) {
    return {
        tokens: createTokens(user, signingKey),
        user: buildUserSummary(user),
        permissions: permissionsFor(user),
        license: buildLicenseSummary(user),
        serverTimeUtc: new Date().toISOString()
    };
}

// -------------------------------------------------------------
// 6. The request application
// -------------------------------------------------------------
function createApp(options) {
    const store = options.store;
    const signingKey = options.signingKey || DEFAULT_SIGNING_KEY;
    const mailer = options.mailer;
    const log = options.log || (() => {});
    const loginThrottle = new Map();

    function sendJson(res, status, payload) {
        const body = JSON.stringify(payload);
        res.writeHead(status, {
            'Content-Type': 'application/json; charset=utf-8',
            'Content-Length': Buffer.byteLength(body),
            'Cache-Control': 'no-store'
        });
        res.end(body);
    }

    function fail(res, error) {
        sendJson(res, error.status, { error: { code: error.code, message: error.message } });
    }

    function throttle(key) {
        const now = Date.now();
        const entry = loginThrottle.get(key);
        if (!entry || entry.resetAt <= now) {
            loginThrottle.set(key, { count: 1, resetAt: now + 15 * 60 * 1000 });
            return true;
        }

        entry.count += 1;
        return entry.count <= 20;
    }

    function clearThrottle(key) {
        loginThrottle.delete(key);
    }

    async function readSessionUser(req) {
        const header = String(req.headers.authorization || '');
        const match = /^Bearer\s+(.+)$/i.exec(header.trim());
        if (!match) {
            return null;
        }

        const payload = verifyToken(match[1], signingKey, 'access');
        if (!payload) {
            return null;
        }

        const user = await store.findById(payload.sub);
        if (!user || user.isActive === false) {
            return null;
        }

        return user;
    }

    // ---- endpoints -------------------------------------------------
    async function handleHealth(res) {
        let database = 'unavailable';
        try {
            await store.findById('health-probe');
            database = 'connected';
        } catch (_) {
            database = 'unavailable';
        }

        sendJson(res, 200, {
            status: database === 'connected' ? 'online' : 'degraded',
            service: SERVICE_NAME,
            version: SERVER_VERSION,
            developer: 'Bashir Al-Farhan',
            database,
            serverTimeUtc: new Date().toISOString()
        });
    }

    async function handleLogin(req, res) {
        const data = parseBody(await readBody(req));
        const identifier = pick(data, 'userName', 'username', 'email', 'User', 'user');
        const password = typeof data.password === 'string' ? data.password : pick(data, 'Password');
        const hwid = pick(data, 'deviceFingerprint', 'hwid', 'Hwid', 'hardwareId');

        if (identifier.length === 0 || password.length === 0) {
            return fail(res, ERRORS.invalidCredentials);
        }

        // An address-shaped identifier has to be a real address, not random text.
        if (identifier.includes('@') && !EMAIL_REGEX.test(identifier)) {
            return fail(res, ERRORS.invalidEmail);
        }

        const clientIp = req.socket?.remoteAddress || 'unknown';
        const throttleKey = `${clientIp}|${identifier.toLowerCase()}`;
        if (!throttle(throttleKey)) {
            return fail(res, ERRORS.rateLimited);
        }

        let user = await store.findByLogin(identifier);
        if (!user) {
            return fail(res, ERRORS.invalidCredentials);
        }

        if (user.lockoutUntil && new Date(user.lockoutUntil).getTime() > Date.now()) {
            return fail(res, ERRORS.accountLocked);
        }

        const verification = await verifyPassword(password, user.passwordHash);
        if (!verification.ok) {
            const attempts = (user.failedAttempts || 0) + 1;
            const patch = { failedAttempts: attempts };
            if (attempts >= MAX_FAILED_ATTEMPTS) {
                patch.lockoutUntil = new Date(Date.now() + LOCKOUT_MINUTES * 60000);
                patch.failedAttempts = 0;
            }
            await store.updateUser(user.id, patch);
            log(`Rejected sign-in for ${identifier}: wrong password.`);
            return fail(res, ERRORS.invalidCredentials);
        }

        if (user.isActive === false) {
            return fail(res, ERRORS.accountSuspended);
        }

        // Administrators are never tied to a machine and never wait for an OTP.
        if (user.role !== 'admin') {
            if (user.isVerified !== true) {
                return fail(res, ERRORS.emailNotVerified);
            }

            if (!hwid) {
                return fail(res, ERRORS.deviceMismatch);
            }

            if (!user.hwid) {
                user = await store.updateUser(user.id, { hwid });
                log(`HWID bound for ${user.userName}.`);
            } else if (!safeEqual(user.hwid, hwid)) {
                log(`HWID mismatch for ${user.userName}: account bound elsewhere.`);
                return fail(res, ERRORS.deviceMismatch);
            }
        }

        const patch = { failedAttempts: 0, lockoutUntil: null, lastLoginAt: new Date() };
        if (verification.legacy) {
            // The previous build stored the password verbatim; upgrade it now.
            patch.passwordHash = await hashPassword(password);
            log(`Upgraded stored password for ${user.userName} to BCrypt.`);
        }
        user = await store.updateUser(user.id, patch);

        clearThrottle(throttleKey);
        log(`B_VIP Login Authorized for: ${user.userName} (${user.role})`);

        const session = buildSession(user, signingKey);
        return sendJson(res, 200, session);
    }

    async function handleRegister(req, res) {
        const data = parseBody(await readBody(req));
        const userName = pick(data, 'userName', 'username');
        const email = pick(data, 'email', 'Email').toLowerCase();
        const password = typeof data.password === 'string' ? data.password : pick(data, 'Password');
        const acceptedTerms = data.acceptedTerms === true || data.AcceptedTerms === true;
        const hwid = pick(data, 'hardwareId', 'deviceFingerprint', 'hwid');

        if (!EMAIL_REGEX.test(email)) {
            return fail(res, ERRORS.invalidEmail);
        }

        if (userName.length < 4 || !/^[A-Za-z0-9._-]{4,64}$/.test(userName)) {
            return fail(res, {
                status: 400,
                code: 'invalid-username',
                message: 'Choose a user name of 4-64 characters using letters, digits, dot, dash or underscore.'
            });
        }

        if (password.length < 8) {
            return fail(res, ERRORS.weakPassword);
        }

        if (!acceptedTerms) {
            return fail(res, { status: 400, code: 'terms-required', message: 'Accept the terms of service to register.' });
        }

        const existing = await store.findByLogin(userName);
        const existingEmail = await store.findByLogin(email);
        if (existing || existingEmail) {
            return fail(res, ERRORS.accountExists);
        }

        const code = createOtpCode();
        const user = await store.createUser({
            id: newId(),
            userName,
            displayName: userName,
            email,
            passwordHash: await hashPassword(password),
            hwid: hwid || null,
            role: 'technician',
            isActive: true,
            isVerified: false,
            otpCode: code,
            otpExpiresAt: new Date(Date.now() + OTP_TTL_MINUTES * 60000),
            licenseType: 'trial',
            licenseStartedAt: new Date(),
            licenseExpiresAt: new Date(Date.now() + TRIAL_DAYS * 86400000)
        });

        let sent = false;
        try {
            sent = await mailer.sendVerificationCode(email, userName, code);
        } catch (error) {
            log(`Verification e-mail failed: ${error.message}`);
        }

        log(`Technician registered: ${userName} (verification ${sent ? 'sent' : 'pending delivery'}).`);

        return sendJson(res, 200, {
            success: true,
            message: sent
                ? 'Account created. Check your e-mail for the 6-digit verification code.'
                : 'Account created, but the verification e-mail could not be sent. Contact support.',
            verificationEmailSent: sent,
            requiresVerification: true,
            verificationLink: null,
            email
        });
    }

    async function handleVerifyCode(req, res) {
        const data = parseBody(await readBody(req));
        const identifier = pick(data, 'userName', 'username', 'email', 'Email');
        const code = pick(data, 'code', 'Code');

        if (identifier.length === 0 || code.length === 0) {
            return fail(res, ERRORS.invalidCode);
        }

        const user = await store.findByLogin(identifier);
        if (!user) {
            return fail(res, ERRORS.invalidCode);
        }

        if (user.isVerified === true) {
            return sendJson(res, 200, { verified: true, message: 'This account is already verified.', userName: user.userName });
        }

        if (!user.otpCode || !safeEqual(user.otpCode, code)) {
            return fail(res, ERRORS.invalidCode);
        }

        if (!user.otpExpiresAt || new Date(user.otpExpiresAt).getTime() < Date.now()) {
            return fail(res, ERRORS.codeExpired);
        }

        // Verification is what starts the 3-day full-access trial.
        const startedAt = new Date();
        await store.updateUser(user.id, {
            isVerified: true,
            isActive: true,
            otpCode: null,
            otpExpiresAt: null,
            licenseType: 'trial',
            licenseStartedAt: startedAt,
            licenseExpiresAt: new Date(startedAt.getTime() + TRIAL_DAYS * 86400000)
        });

        log(`E-mail verified for ${user.userName}; 3-day trial started.`);
        return sendJson(res, 200, {
            verified: true,
            message: 'Account verified. Your 3-day free trial has started.',
            userName: user.userName
        });
    }

    async function handleResendVerification(req, res) {
        const data = parseBody(await readBody(req));
        const identifier = pick(data, 'email', 'Email', 'userName');

        if (identifier.includes('@') && !EMAIL_REGEX.test(identifier)) {
            return fail(res, ERRORS.invalidEmail);
        }

        const user = await store.findByLogin(identifier);
        if (!user) {
            // Do not disclose which addresses exist.
            return sendJson(res, 200, { sent: true, message: 'If the account exists, a new code has been sent.', verificationLink: null });
        }

        if (user.isVerified === true) {
            return sendJson(res, 200, { sent: false, message: 'This account is already verified.', verificationLink: null });
        }

        const code = createOtpCode();
        await store.updateUser(user.id, {
            otpCode: code,
            otpExpiresAt: new Date(Date.now() + OTP_TTL_MINUTES * 60000)
        });

        let sent = false;
        try {
            sent = await mailer.sendVerificationCode(user.email || identifier, user.userName, code);
        } catch (error) {
            log(`Verification e-mail failed: ${error.message}`);
        }

        return sendJson(res, 200, {
            sent,
            message: sent ? 'A new verification code has been sent.' : 'The verification e-mail could not be sent. Contact support.',
            verificationLink: null
        });
    }

    async function handleMe(req, res) {
        const user = await readSessionUser(req);
        if (!user) {
            return fail(res, { status: 401, code: 'unauthenticated', message: 'Sign in again to continue.' });
        }

        return sendJson(res, 200, {
            user: buildUserSummary(user),
            permissions: permissionsFor(user),
            license: buildLicenseSummary(user),
            defaultLanguage: 'en',
            serverVersion: SERVER_VERSION
        });
    }

    async function handleRefresh(req, res) {
        const data = parseBody(await readBody(req));
        const refreshToken = pick(data, 'refreshToken', 'RefreshToken');
        const payload = verifyToken(refreshToken, signingKey, 'refresh');
        if (!payload) {
            return fail(res, ERRORS.invalidCredentials);
        }

        const user = await store.findById(payload.sub);
        if (!user || user.isActive === false) {
            return fail(res, ERRORS.invalidCredentials);
        }

        return sendJson(res, 200, buildSession(user, signingKey));
    }

    return async function handler(req, res) {
        res.setHeader('Access-Control-Allow-Origin', req.headers.origin || '*');
        res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');
        res.setHeader('Access-Control-Allow-Headers', 'Content-Type, Authorization, Accept, X-Requested-With');
        res.setHeader('X-Content-Type-Options', 'nosniff');

        if (req.method === 'OPTIONS') {
            res.writeHead(204);
            res.end();
            return;
        }

        const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
        const path = url.pathname.replace(/\/+$/, '').toLowerCase();

        try {
            if (path === '' && req.method === 'GET') {
                return sendJson(res, 200, {
                    status: 'online',
                    service: SERVICE_NAME,
                    version: SERVER_VERSION,
                    developer: 'Bashir Al-Farhan'
                });
            }

            if ((path === '/api/health' || path === '/health') && req.method === 'GET') {
                return await handleHealth(res);
            }

            if (path === '/api/auth/login' && req.method === 'POST') {
                return await handleLogin(req, res);
            }

            if (path === '/api/auth/register' && req.method === 'POST') {
                return await handleRegister(req, res);
            }

            if (path === '/api/auth/verify-code' && req.method === 'POST') {
                return await handleVerifyCode(req, res);
            }

            if (path === '/api/auth/resend-verification' && req.method === 'POST') {
                return await handleResendVerification(req, res);
            }

            if (path === '/api/auth/me' && req.method === 'GET') {
                return await handleMe(req, res);
            }

            if (path === '/api/auth/refresh' && req.method === 'POST') {
                return await handleRefresh(req, res);
            }

            if (path === '/api/auth/logout' && req.method === 'POST') {
                return sendJson(res, 200, { message: 'Signed out.' });
            }

            return fail(res, ERRORS.notFound);
        } catch (error) {
            if (error && error.message === 'payload-too-large') {
                return fail(res, { status: 413, code: 'payload-too-large', message: 'The request is too large.' });
            }

            const reason = error && error.message ? error.message : String(error);
            log(`Request failed (${req.method} ${path}): ${reason}`);

            // A database outage is not a credential failure and must not look like one.
            if (isDatabaseError(error)) {
                return fail(res, ERRORS.databaseUnavailable);
            }

            return fail(res, ERRORS.serverError);
        }
    };
}

function isDatabaseError(error) {
    if (!error) {
        return false;
    }

    const code = String(error.code || '');
    return error.message === 'store-unavailable' ||
        /ECONNREFUSED|ENOTFOUND|ETIMEDOUT|timeout exceeded|Connection terminated|no such host/i.test(String(error.message || '')) ||
        /^[0-9A-Z]{5}$/.test(code);
}

// -------------------------------------------------------------
// 7. Wiring: database, seeding, HTTP + WebSocket
// -------------------------------------------------------------
function createPool() {
    const connectionString = process.env.DATABASE_URL ||
        process.env.ConnectionStrings__DefaultConnection ||
        process.env.ConnectionStrings__Default ||
        '';

    if (!connectionString) {
        return null;
    }

    const { Pool } = require('pg');
    const pool = new Pool({
        connectionString,
        ssl: /localhost|127\.0\.0\.1/.test(connectionString) ? false : { rejectUnauthorized: false },
        max: Number(process.env.PG_POOL_MAX || 10),
        connectionTimeoutMillis: 10000
    });
    pool.on('error', error => console.error('PostgreSQL pool error:', error.message));
    return pool;
}

async function startServer() {
    const PORT = Number(process.env.PORT || 3000);
    const signingKey = process.env.Auth__SigningKey || process.env.SIGNING_KEY || DEFAULT_SIGNING_KEY;
    const log = message => console.log(message);

    const pool = createPool();
    let store;

    if (!pool) {
        console.error('No database configured (DATABASE_URL). Authentication will fail closed with HTTP 503.');
        store = {
            async findById() { throw new Error('store-unavailable'); },
            async findByLogin() { throw new Error('store-unavailable'); },
            async findByIdOrEmail() { throw new Error('store-unavailable'); },
            async createUser() { throw new Error('store-unavailable'); },
            async updateUser() { throw new Error('store-unavailable'); }
        };
    } else {
        store = createPostgresStore(pool);
        try {
            await migrateUsersTable(pool, log);
            log('PostgreSQL connected; user schema verified.');
            await seedMasterAdmin(store, pool, log);
            await demoteStubAccounts(pool, log);
        } catch (error) {
            console.error('Database start-up failed:', error.message);
            store = {
                async findById() { throw new Error('store-unavailable'); },
                async findByLogin() { throw new Error('store-unavailable'); },
                async findByIdOrEmail() { throw new Error('store-unavailable'); },
                async createUser() { throw new Error('store-unavailable'); },
                async updateUser() { throw new Error('store-unavailable'); }
            };
        }
    }

    const mailer = createMailer({
        host: process.env.Email__Host || process.env.SMTP_HOST || '',
        port: process.env.Email__Port || process.env.SMTP_PORT || 587,
        user: process.env.Email__UserName || process.env.SMTP_USER || '',
        password: process.env.Email__Password || process.env.SMTP_PASSWORD || '',
        from: process.env.Email__From || process.env.SMTP_FROM || '',
        fromName: process.env.Email__FromName || 'B_VIP Tool',
        enableSsl: process.env.Email__EnableSsl || 'true'
    }, log);

    const app = createApp({ store, signingKey, mailer, log });
    const server = http.createServer(app);

    attachWebSocket(server, log);

    await new Promise(resolve => server.listen(PORT, resolve));
    log(`B_VIP Server listening on port ${PORT}`);
    return server;
}

// -------------------------------------------------------------
// 8. WebSocket relay for remote support sessions
// -------------------------------------------------------------
function attachWebSocket(server, log) {
    const WebSocket = require('ws');
    const wss = new WebSocket.Server({ server });
    const sessions = new Map();

    wss.on('connection', ws => {
        let deviceId = null;
        let peerId = null;

        ws.on('message', (message, isBinary) => {
            if (isBinary) {
                if (peerId && sessions.has(peerId)) {
                    const target = sessions.get(peerId);
                    if (target.readyState === WebSocket.OPEN) {
                        target.send(message, { binary: true });
                    }
                }
                return;
            }

            try {
                const data = JSON.parse(message.toString());

                if (data.type === 'register') {
                    deviceId = data.id;
                    sessions.set(deviceId, ws);
                    ws.send(JSON.stringify({ type: 'registered', status: 'ok', id: deviceId }));
                    log(`B_VIP Device Registered: ${deviceId}`);
                }

                if (data.type === 'connect') {
                    peerId = data.targetId;
                    if (sessions.has(peerId)) {
                        sessions.get(peerId).send(JSON.stringify({ type: 'session_start', peerId: deviceId }));
                        ws.send(JSON.stringify({ type: 'connected_success', targetId: peerId }));
                        log(`Remote session: ${deviceId} -> ${peerId}`);
                    } else {
                        ws.send(JSON.stringify({ type: 'error', message: 'The device is not connected.' }));
                    }
                }

                if (data.type === 'input_event' || data.type === 'command') {
                    if (peerId && sessions.has(peerId)) {
                        sessions.get(peerId).send(JSON.stringify(data));
                    }
                }
            } catch (error) {
                log(`WebSocket message error: ${error.message}`);
            }
        });

        ws.on('close', () => {
            if (deviceId && sessions.get(deviceId) === ws) {
                sessions.delete(deviceId);
            }
            if (peerId && sessions.has(peerId)) {
                const peer = sessions.get(peerId);
                if (peer.readyState === WebSocket.OPEN) {
                    peer.send(JSON.stringify({ type: 'peer_disconnected' }));
                }
            }
        });
    });
}

module.exports = {
    createApp,
    createPostgresStore,
    migrateUsersTable,
    seedMasterAdmin,
    demoteStubAccounts,
    createMailer,
    hashPassword,
    verifyPassword,
    signToken,
    verifyToken,
    evaluateLicense,
    normalizeRole,
    EMAIL_REGEX,
    MASTER_ADMIN,
    ERRORS,
    TRIAL_DAYS,
    OTP_TTL_MINUTES
};

if (require.main === module) {
    startServer().catch(error => {
        console.error('B_VIP Server failed to start:', error);
        process.exit(1);
    });
}
