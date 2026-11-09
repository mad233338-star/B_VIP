'use strict';

/*
 * B_VIP Tool - cloud licensing and remote support server.
 *
 * Authentication rules enforced here (and nowhere else):
 *   - an account is found by user name or e-mail address; the password is verified with BCrypt;
 *   - no machine binding: an account may sign in from any computer, and no fingerprint is stored
 *     or compared (the HWID requirement was removed deliberately, not disabled per environment);
 *   - administrators skip the e-mail verification step and always hold a lifetime licence;
 *   - a technician whose trial/subscription has run out can still sign in, but receives an expired
 *     licence and no permissions, so every device operation stays locked in the client;
 *   - when the database is unreachable the server fails closed (503): it never answers a login with
 *     a canned "success" payload.
 *
 * The administration console reads its directory, licence and metric data from the /api/admin
 * routes below; every one of them answers an exhausted or empty database with zeros and empty
 * collections instead of a 404 or a 500.
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

// Chipset loader bundles published as release assets. The desktop client asks for a chipset by
// id through LOADER_PROXY_ROUTE and this service streams the matching archive back to it.
const LOADER_PROXY_ROUTE = '/api/v1/loaders/download/';
const LOADER_HEADER_TIMEOUT_MS = 30000;

const LOADER_ASSETS = {
    qualcomm: {
        fileName: 'Loaders-main.zip',
        url: 'https://github.com/mad233338-star/B_VIP-Loaders/releases/download/v1.0/Loaders-main.zip'
    },
    mtk: {
        fileName: 'mtkclient-main.zip',
        url: 'https://github.com/mad233338-star/B_VIP-Loaders/releases/download/v1.0/mtkclient-main.zip'
    },
    spd: {
        fileName: 'SPD_Loaders.zip.zip',
        url: 'https://github.com/mad233338-star/B_VIP-Loaders/releases/download/v1.0/SPD_Loaders.zip.zip'
    }
};

// Proper error codes the desktop client already understands.
const ERRORS = {
    loaderUnavailable: {
        status: 502,
        code: 'loader-unavailable',
        message: 'The loader archive could not be fetched. Try again in a moment.'
    },
    invalidCredentials: { status: 401, code: 'invalid-credentials', message: 'Invalid username or password.' },
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

/**
 * The desktop contracts type every id as a Guid, but rows written by the old integer-keyed schema
 * still exist. Real GUIDs pass through; anything else becomes a stable name-based UUID so the
 * console deserialises the payload instead of failing on the whole list.
 */
function toGuidString(value) {
    const text = String(value ?? '');
    if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(text)) {
        return text.toLowerCase();
    }

    const hash = crypto.createHash('sha1').update(text || 'empty').digest('hex');
    return `${hash.slice(0, 8)}-${hash.slice(8, 12)}-5${hash.slice(13, 16)}-a${hash.slice(17, 20)}-${hash.slice(20, 32)}`;
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
        },

        async deleteUser(id) {
            const result = await pool.query('DELETE FROM users WHERE id = $1 RETURNING id', [id]);
            return result.rows.length > 0;
        },

        /** The administration console's directory, newest first. */
        async listUsers(options = {}) {
            const search = String(options.search || '').trim().toLowerCase();
            const limit = Math.min(Math.max(Number(options.limit) || 500, 1), 2000);
            const result = search.length === 0
                ? await pool.query('SELECT * FROM users ORDER BY username ASC LIMIT $1', [limit])
                : await pool.query(
                    `SELECT * FROM users
                     WHERE lower(username) LIKE $1 OR lower(coalesce(email, '')) LIKE $1
                     ORDER BY username ASC LIMIT $2`,
                    [`%${search}%`, limit]);
            return result.rows.map(mapRow);
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

    const port = Number(config.port || 587);
    const transport = nodemailer.createTransport({
        host,
        port,
        // Email__EnableSsl=false turns TLS off for a plain relay; 465 implies it regardless.
        secure: String(config.secure || config.enableSsl || 'true') !== 'false' && port === 465,
        auth: { user, pass: password },
        // A relay that accepts the socket and then goes quiet must fail fast, because the
        // registration response no longer waits for it and a stuck send would leak a handle.
        connectionTimeout: EMAIL_DISPATCH_TIMEOUT_MS,
        greetingTimeout: EMAIL_DISPATCH_TIMEOUT_MS,
        socketTimeout: EMAIL_DISPATCH_TIMEOUT_MS
    });

    return {
        configured: true,
        async sendVerificationCode(to, userName, code) {
            await transport.sendMail({
                from: `${config.fromName || 'B_VIP Tool'} <${from}>`,
                to,
                subject: OTP_EMAIL_SUBJECT,
                text: buildOtpText(code),
                html: buildOtpHtml(code, userName)
            });
            log(`Verification code sent to ${to} for ${userName}.`);
            return true;
        }
    };
}

const OTP_EMAIL_SUBJECT = 'Your B_VIP Tool Verification Code';

/** How long a verification e-mail may take before the send is abandoned. */
const EMAIL_DISPATCH_TIMEOUT_MS = 5000;

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

/** Plain-text alternative: some clients strip HTML, and a text part improves deliverability. */
function buildOtpText(code) {
    return [
        'Welcome to B_VIP Tool,',
        '',
        'Thank you for registering on our smart device management and maintenance platform.',
        'To complete your account activation and start your 3-day free trial, enter the verification code below in the tool:',
        '',
        `[ ${code} ]`,
        '',
        'Expiry: This code is valid for 15 minutes only.',
        'Security notice: The support team will never ask for this code. Never share it with anyone.',
        'If you did not request this registration, please disregard this email.',
        '',
        'B_VIP Tool Team',
        'Comprehensive ecosystem for smart device maintenance and support'
    ].join('\n');
}

/** The verification mail: one screen, the code large, the theme's navy and neon cyan. */
function buildOtpHtml(code, userName) {
    const greeting = userName ? `Welcome to B_VIP Tool, ${escapeHtml(userName)}` : 'Welcome to B_VIP Tool,';

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${OTP_EMAIL_SUBJECT}</title>
</head>
<body style="margin:0;padding:0;background:#0B132B;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#0B132B;padding:32px 12px;">
    <tr>
      <td align="center">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#1E293B;border:1px solid #334155;border-radius:14px;overflow:hidden;font-family:'Segoe UI',Arial,Helvetica,sans-serif;">
          <tr>
            <td style="background:linear-gradient(135deg,#0F172A 0%,#1C2536 100%);padding:26px 32px;border-bottom:1px solid #334155;">
              <div style="font-size:20px;font-weight:700;color:#FFFFFF;letter-spacing:0.4px;">B_VIP <span style="color:#38BDF8;">Tool</span></div>
              <div style="font-size:12px;color:#94A3B8;margin-top:4px;">Smart device maintenance &amp; servicing platform</div>
            </td>
          </tr>
          <tr>
            <td style="padding:30px 32px 8px 32px;color:#E2E8F0;font-size:15px;line-height:24px;">
              <p style="margin:0 0 16px 0;color:#FFFFFF;font-size:16px;font-weight:600;">${greeting}</p>
              <p style="margin:0 0 14px 0;">Thank you for registering on our smart device management and maintenance platform.</p>
              <p style="margin:0 0 22px 0;">To complete your account activation and start your 3-day free trial, enter the verification code below in the tool:</p>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding:0 32px;">
              <div style="display:inline-block;padding:18px 30px;background:#0F172A;border:1px solid #38BDF8;border-radius:12px;box-shadow:0 0 22px rgba(0,210,255,0.35);font-size:34px;font-weight:700;letter-spacing:10px;color:#00D2FF;font-family:Consolas,'Courier New',monospace;">${escapeHtml(code)}</div>
            </td>
          </tr>
          <tr>
            <td style="padding:26px 32px 30px 32px;color:#CBD5E1;font-size:13px;line-height:21px;">
              <p style="margin:0 0 8px 0;"><strong style="color:#FFFFFF;">Expiry:</strong> This code is valid for 15 minutes only.</p>
              <p style="margin:0 0 8px 0;"><strong style="color:#FFFFFF;">Security notice:</strong> The support team will never ask for this code. Never share it with anyone.</p>
              <p style="margin:0;">If you did not request this registration, please disregard this email.</p>
            </td>
          </tr>
          <tr>
            <td style="padding:20px 32px 26px 32px;border-top:1px solid #334155;color:#94A3B8;font-size:12px;line-height:19px;">
              <div style="color:#E2E8F0;font-weight:600;font-size:13px;">B_VIP Tool Team</div>
              <div>Comprehensive ecosystem for smart device maintenance and support</div>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>`;
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

/**
 * The trial lifecycle, expressed once so every endpoint agrees.
 *
 * A trial is not a reduced licence: for its 72 hours it is ordinary active access with every
 * module unlocked, and only the plan name marks it as a trial. Marquee statuses are deliberately
 * *not* "Trial" on the wire, because a client that gates on `status === 'Active'` would otherwise
 * lock the bench for exactly the three days the technician was promised full access.
 * An expired licence still signs in; it just reports every device operation as locked.
 */
function licenseAccessFor(user) {
    const state = evaluateLicense(user);
    return {
        isActive: state.active,
        isTrial: user.role !== 'admin' && state.active && state.type === 'trial',
        deviceOperationsLocked: !state.active,
        licenseStatus: state.active ? 'Active' : 'Expired'
    };
}

function buildLicenseSummary(user) {
    const state = evaluateLicense(user);
    const access = licenseAccessFor(user);
    return {
        licenseId: user.id,
        licenseKey: `BV-${String(user.id).replace(/-/g, '').slice(0, 12).toUpperCase()}`,
        planCode: state.type,
        planName: state.type === 'trial' ? '3-Day Free Trial' : (state.type === 'lifetime_admin' ? 'Administrator (Lifetime)' : 'Subscription'),
        status: state.active ? 'Active' : state.status,
        isActive: access.isActive,
        isTrial: access.isTrial,
        licenseStatus: access.licenseStatus,
        deviceOperationsLocked: access.deviceOperationsLocked,
        daysRemaining: state.daysRemaining,
        startsAt: isoOrNull(state.startedAt) || new Date().toISOString(),
        expiresAt: isoOrNull(state.expiresAt),
        // No device binding: a licence is not consumed by, or limited to, any number of machines.
        maxActivations: 0,
        currentActivations: 0,
        features: permissionsFor(user)
    };
}

function buildUserSummary(user) {
    const license = evaluateLicense(user);
    const access = licenseAccessFor(user);

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
        // Reported as null on purpose: the account is not tied to a machine any more.
        hardwareId: null,
        hardwareIdResetCount: 0,
        // The same view of the licence the license block carries, so either object answers
        // "may this account operate a device right now?".
        isActive: access.isActive,
        isTrial: access.isTrial,
        licenseStatus: access.licenseStatus,
        deviceOperationsLocked: access.deviceOperationsLocked
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
    const reporter = options.log || (() => {});
    const recentEvents = [];
    const log = message => {
        record('INFO', String(message));
        reporter(message);
    };
    const loginThrottle = new Map();
    const maintenance = {
        enabled: false,
        message: null,
        startsAt: null,
        endsAt: null,
        updatedUtc: new Date().toISOString()
    };

    // The subscription tiers the console and the in-app cards both show.
    const plans = [
        {
            id: '11111111-1111-4111-8111-111111111101',
            code: '3-months',
            name: '3 Months Plan',
            nameAr: null,
            description: '90 days of full access to every vendor module.',
            durationDays: 90,
            maxActivations: 0,
            isLifetime: false,
            isActive: true,
            sortOrder: 1,
            licenseCount: 0,
            price: 20,
            currency: 'USD'
        },
        {
            id: '22222222-2222-4222-8222-222222222202',
            code: '6-months',
            name: '6 Months Plan',
            nameAr: null,
            description: '180 days of full access to every vendor module.',
            durationDays: 180,
            maxActivations: 0,
            isLifetime: false,
            isActive: true,
            sortOrder: 2,
            licenseCount: 0,
            price: 35,
            currency: 'USD'
        },
        {
            id: '33333333-3333-4333-8333-333333333303',
            code: '1-year',
            name: '1 Year Plan',
            nameAr: null,
            description: '365 days of full access to every vendor module.',
            durationDays: 365,
            maxActivations: 0,
            isLifetime: false,
            isActive: true,
            sortOrder: 3,
            licenseCount: 0,
            price: 50,
            currency: 'USD'
        }
    ];

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

        // The e-mail verification gate stays; the machine check does not exist any more. A
        // technician signs in from any computer and no fingerprint is written or compared.
        if (user.role !== 'admin' && user.isVerified !== true) {
            return fail(res, ERRORS.emailNotVerified);
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

    /**
     * Hands the verification code to the relay without holding the HTTP response open.
     *
     * The user row and the 6-digit code are already committed, so delivery is a background
     * concern: it gets a hard five seconds, and a dead or slamming relay only produces a log
     * line. Registration itself always answers 200.
     */
    function dispatchVerificationEmail(to, userName, code) {
        let timer = null;
        const deadline = new Promise(resolve => {
            timer = setTimeout(() => resolve('timeout'), EMAIL_DISPATCH_TIMEOUT_MS);
            if (typeof timer.unref === 'function') {
                timer.unref();
            }
        });

        let attempt;
        try {
            attempt = Promise.resolve(mailer.sendVerificationCode(to, userName, code));
        } catch (error) {
            attempt = Promise.resolve(false);
            log(`Verification e-mail failed: ${error && error.message ? error.message : error}`);
        }

        return Promise.race([
            attempt.catch(error => {
                log(`Verification e-mail failed: ${error && error.message ? error.message : error}`);
                return false;
            }),
            deadline
        ])
            .then(result => {
                if (result === 'timeout') {
                    log(`Verification e-mail to ${to} timed out after ${EMAIL_DISPATCH_TIMEOUT_MS} ms; the code is still valid.`);
                    return false;
                }

                return result === true;
            })
            .finally(() => {
                if (timer) {
                    clearTimeout(timer);
                }
            });
    }

    async function handleRegister(req, res) {
        const data = parseBody(await readBody(req));
        const userName = pick(data, 'userName', 'username');
        const email = pick(data, 'email', 'Email').toLowerCase();
        const password = typeof data.password === 'string' ? data.password : pick(data, 'Password');
        const acceptedTerms = data.acceptedTerms === true || data.AcceptedTerms === true;

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
            role: 'technician',
            isActive: true,
            isVerified: false,
            otpCode: code,
            otpExpiresAt: new Date(Date.now() + OTP_TTL_MINUTES * 60000),
            licenseType: 'trial',
            licenseStartedAt: new Date(),
            licenseExpiresAt: new Date(Date.now() + TRIAL_DAYS * 86400000)
        });

        // Never awaited: the request returns as soon as the account exists.
        dispatchVerificationEmail(email, userName, code);

        log(`Technician registered: ${userName} (verification code queued for delivery).`);

        return sendJson(res, 200, {
            success: true,
            message: 'Account created. Verification code sent.',
            verificationEmailSent: true,
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

    /**
     * GET /api/operations/queue — the service queue the desktop client polls.
     *
     * It always answers 200 with the documented envelope so a signed-in technician sees a live
     * link instead of "endpoint not found": `success`, `status` and `tasks` are always present.
     * A missing, expired or unreadable token yields an empty queue and `authenticated: false`
     * rather than a 401, because an absent queue is not an authentication failure. A database
     * outage is reported as `degraded`, never as a credential error.
     */
    async function handleServiceQueue(req, res) {
        let user = null;
        let storeReachable = true;
        try {
            user = await readSessionUser(req);
        } catch (_) {
            storeReachable = false;
        }

        return sendJson(res, 200, {
            success: true,
            status: storeReachable ? 'connected' : 'degraded',
            authenticated: Boolean(user),
            userName: user ? user.userName : null,
            generatedUtc: new Date().toISOString(),
            tasks: []
        });
    }

    // ---------------------------------------------------------
    // Loader archive proxy.
    //
    // The desktop client used to fetch these ZIPs straight from the GitHub release CDN, which is
    // blocked or throttled from several regions and left the download bar hanging. The client now
    // asks this service instead: the archive is streamed through unchanged, so the operator never
    // needs direct access to the release host. Nothing is buffered in memory, nothing is cached.
    // ---------------------------------------------------------
    async function handleLoaderDownload(req, res, chipset) {
        const asset = LOADER_ASSETS[chipset];
        if (!asset) {
            return fail(res, ERRORS.notFound);
        }

        const controller = new AbortController();
        const headerTimer = setTimeout(() => controller.abort(), LOADER_HEADER_TIMEOUT_MS);

        // A technician who closes the panel must not leave the upstream transfer running.
        res.on('close', () => {
            if (!res.writableEnded) {
                controller.abort();
            }
        });

        try {
            const upstream = await fetch(asset.url, {
                redirect: 'follow',
                signal: controller.signal,
                headers: { 'User-Agent': 'B_VIP-Desktop/2.0', Accept: 'application/octet-stream' }
            });

            clearTimeout(headerTimer);

            if (!upstream.ok || !upstream.body) {
                log(`Loader asset '${chipset}' answered HTTP ${upstream.status}.`);
                return fail(res, ERRORS.loaderUnavailable);
            }

            const headers = {
                'Content-Type': 'application/zip',
                'Content-Disposition': `attachment; filename="${asset.fileName}"`,
                'Cache-Control': 'no-store'
            };

            const length = upstream.headers.get('content-length');
            if (length) {
                headers['Content-Length'] = length;
            }

            res.writeHead(200, headers);

            // Back-pressure: pause reading while the client's socket is full. A client that goes
            // away mid-transfer resolves on 'close' too, so the wait never hangs.
            for await (const chunk of upstream.body) {
                if (!res.write(chunk)) {
                    await new Promise(resolve => {
                        const resume = () => {
                            res.off('drain', resume);
                            res.off('close', resume);
                            resolve();
                        };

                        res.once('drain', resume);
                        res.once('close', resume);
                    });
                }
            }

            res.end();
        } catch (error) {
            clearTimeout(headerTimer);

            if (res.headersSent) {
                // The transfer was already announced; the client sees a truncated stream and retries.
                res.destroy();
                return;
            }

            log(`Loader proxy failed for '${chipset}': ${error && error.message ? error.message : error}`);
            return fail(res, ERRORS.loaderUnavailable);
        }
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

    // ---- administration console ------------------------------------------------

    // Every log line the app already writes also lands here, so /api/admin/logs can show the
    // recent history without a second logging stack.
    function record(level, message) {
        recentEvents.push({ id: newId(), occurredUtc: new Date().toISOString(), level, message });
        while (recentEvents.length > 250) {
            recentEvents.shift();
        }
    }

    async function requireAdmin(req, res) {
        const user = await readSessionUser(req);
        if (!user) {
            fail(res, { status: 401, code: 'unauthenticated', message: 'Sign in again to continue.' });
            return null;
        }

        if (user.role !== 'admin') {
            fail(res, { status: 403, code: 'forbidden', message: 'This action requires an administrator account.' });
            return null;
        }

        return user;
    }

    async function allUsers() {
        if (typeof store.listUsers !== 'function') {
            return [];
        }

        try {
            return await store.listUsers({ limit: 2000 });
        } catch (error) {
            log(`Directory listing failed: ${error.message}`);
            return [];
        }
    }

    function pageOf(items, url) {
        const page = Math.max(1, Number(url.searchParams.get('page')) || 1);
        const pageSize = Math.min(Math.max(Number(url.searchParams.get('pageSize')) || 100, 1), 500);
        const start = (page - 1) * pageSize;
        return {
            items: items.slice(start, start + pageSize),
            totalCount: items.length,
            page,
            pageSize,
            totalPages: Math.max(1, Math.ceil(items.length / pageSize))
        };
    }

    function directoryFrom(url) {
        const search = String(url.searchParams.get('q') || url.searchParams.get('query') || '').trim().toLowerCase();
        const status = String(url.searchParams.get('status') || '').trim().toLowerCase();
        const role = String(url.searchParams.get('role') || '').trim().toLowerCase();

        return async function filter() {
            let users = await allUsers();
            if (search.length > 0) {
                users = users.filter(user =>
                    String(user.userName || '').toLowerCase().includes(search) ||
                    String(user.email || '').toLowerCase().includes(search));
            }
            if (status.length > 0) {
                users = users.filter(user => String(buildUserSummary(user).status).toLowerCase() === status);
            }
            if (role.length > 0) {
                users = users.filter(user => user.role === (role.includes('admin') ? 'admin' : 'technician'));
            }
            return users;
        };
    }

    function toTechnicianSummary(user) {
        const summary = buildUserSummary(user);
        return { ...summary, id: toGuidString(summary.id) };
    }

    function toLicenseRow(user) {
        const state = evaluateLicense(user);
        const summary = buildLicenseSummary(user);
        return {
            id: toGuidString(`license:${user.id}`),
            licenseKey: summary.licenseKey,
            userId: toGuidString(user.id),
            userName: user.userName,
            planId: toGuidString(`plan:${state.type}`),
            planCode: state.type,
            planName: summary.planName,
            status: state.active ? 'Active' : 'Expired',
            effectiveStatus: state.status,
            startsAt: isoOrNull(state.startedAt) || new Date().toISOString(),
            expiresAt: isoOrNull(state.expiresAt),
            maxActivations: 0,
            currentActivations: 0,
            createdUtc: isoOrNull(user.createdUtc) || new Date().toISOString(),
            lastVerifiedUtc: isoOrNull(user.lastLoginAt),
            source: 'cloud',
            adminNotes: null,
            features: permissionsFor(user),
            activations: []
        };
    }

    async function findAccount(identifier) {
        const value = String(identifier || '').trim();
        if (value.length === 0) {
            return null;
        }

        try {
            const byId = await store.findById(value);
            if (byId) {
                return byId;
            }
        } catch (_) {
            // Not an id: fall through to the login lookup.
        }

        return await store.findByLogin(value);
    }

    async function extendAccount(user, days) {
        const state = evaluateLicense(user);
        const now = Date.now();
        const currentExpiry = state.expiresAt ? new Date(state.expiresAt).getTime() : now;
        const base = Math.max(now, Number.isFinite(currentExpiry) ? currentExpiry : now);
        const expiresAt = new Date(base + days * 86400000);

        const updated = await store.updateUser(user.id, {
            licenseType: user.licenseType === 'trial' ? 'trial' : (user.licenseType || 'subscription'),
            licenseStartedAt: user.licenseStartedAt || new Date(),
            licenseExpiresAt: expiresAt,
            isActive: true,
            failedAttempts: 0,
            lockoutUntil: null
        });

        log(`Subscription extended by ${days} day(s) for ${user.userName}.`);
        return updated || user;
    }

    /**
     * The console's routes. Returns true when the request was answered, false when the path is not
     * one of them (so the caller can fall through to its 404). Every route is administrator-only and
     * answers an empty database with zeros and empty collections rather than an error.
     */
    async function routeAdmin(req, res, path, url) {
        const isGet = req.method === 'GET';
        const isPost = req.method === 'POST';
        const isPatch = req.method === 'PATCH';
        const isDelete = req.method === 'DELETE';

        const known =
            path === '/api/admin/workstations' || path === '/api/admin/expired-accounts' ||
            path === '/api/admin/logs' || path === '/api/admin/extend-license' ||
            path === '/api/admin/overview' || path === '/api/admin/technicians' ||
            path === '/api/admin/technicians/metrics' || path === '/api/admin/roles' ||
            path === '/api/admin/permissions' || path === '/api/admin/settings' ||
            path === '/api/admin/integrations' || path === '/api/admin/audit-events' ||
            path === '/api/admin/backups' || path === '/api/admin/versions' ||
            path === '/api/admin/health' || path === '/api/admin/status' ||
            path === '/api/admin/maintenance' || path === '/api/users' || path === '/api/licenses' ||
            path === '/api/licenses/plans' ||
            path.startsWith('/api/users/') || path.startsWith('/api/licenses/plans/') ||
            path.startsWith('/api/admin/technicians/');

        if (!known) {
            return false;
        }

        const admin = await requireAdmin(req, res);
        if (!admin) {
            return true;
        }

        if (path === '/api/admin/workstations' && isGet) {
            const users = await directoryFrom(url)();
            const items = users
                .filter(user => user.role !== 'admin')
                .map(user => ({
                    userId: toGuidString(user.id),
                    userName: user.userName,
                    email: user.email || '',
                    hardwareId: null,
                    deviceBinding: 'disabled',
                    status: buildUserSummary(user).status,
                    lastLoginUtc: isoOrNull(user.lastLoginAt),
                    createdUtc: isoOrNull(user.createdUtc)
                }));
            return sendJson(res, 200, { items, totalCount: items.length, deviceBinding: 'disabled' });
        }

        if (path === '/api/admin/expired-accounts' && isGet) {
            const users = await allUsers();
            const items = users
                .filter(user => user.role !== 'admin' && !evaluateLicense(user).active)
                .map(user => {
                    const state = evaluateLicense(user);
                    return {
                        userId: toGuidString(user.id),
                        userName: user.userName,
                        email: user.email || '',
                        licenseType: state.type,
                        status: user.isActive === false ? 'Suspended' : 'Expired',
                        expiredUtc: isoOrNull(state.expiresAt),
                        emailVerified: user.isVerified === true,
                        lastLoginUtc: isoOrNull(user.lastLoginAt)
                    };
                });
            return sendJson(res, 200, { items, totalCount: items.length });
        }

        if (path === '/api/admin/logs' && isGet) {
            const level = String(url.searchParams.get('level') || '').toUpperCase();
            const limit = Math.min(Math.max(Number(url.searchParams.get('limit')) || 200, 1), 250);
            const items = recentEvents
                .filter(entry => level.length === 0 || entry.level === level)
                .slice(-limit)
                .reverse();
            return sendJson(res, 200, { items, totalCount: items.length, level: level || null });
        }

        if (path === '/api/admin/extend-license' && isPost) {
            const data = parseBody(await readBody(req));
            const identifier = pick(data, 'userId', 'id', 'userName', 'username', 'email');
            const days = Number(pick(data, 'days', 'Days') || data.days);

            if (!Number.isFinite(days) || days < 1 || days > 3650) {
                return fail(res, {
                    status: 400,
                    code: 'invalid-days',
                    message: 'Enter a number of days between 1 and 3650.'
                });
            }

            const user = await findAccount(identifier);
            if (!user) {
                return fail(res, { status: 404, code: 'account-not-found', message: 'That account does not exist.' });
            }

            if (user.role === 'admin') {
                return sendJson(res, 200, {
                    userId: toGuidString(user.id),
                    userName: user.userName,
                    licenseType: 'lifetime_admin',
                    status: 'Active',
                    expiresAt: null,
                    days: 0,
                    message: 'Administrator accounts never expire; no extension was needed.'
                });
            }

            const updated = await extendAccount(user, Math.round(days));
            const state = evaluateLicense(updated);
            return sendJson(res, 200, {
                userId: toGuidString(updated.id),
                userName: updated.userName,
                licenseType: state.type,
                status: state.status,
                expiresAt: isoOrNull(state.expiresAt),
                days: Math.round(days),
                message: `${Math.round(days)} day(s) added to ${updated.userName}.`
            });
        }

        if (path === '/api/admin/overview' && isGet) {
            const users = await allUsers();
            const technicians = users.filter(user => user.role !== 'admin');
            const states = technicians.map(evaluateLicense);
            return sendJson(res, 200, {
                totalUsers: users.length,
                activeUsers: users.filter(user => user.isActive !== false).length,
                suspendedUsers: users.filter(user => user.isActive === false).length,
                totalLicenses: technicians.length,
                activeLicenses: states.filter(state => state.active).length,
                expiredLicenses: states.filter(state => !state.active).length,
                totalActivations: 0,
                openTickets: 0,
                operationsLast24h: 0,
                failedOperationsLast24h: 0,
                securityEventsLast24h: recentEvents.length,
                lastBackupUtc: null,
                serverStatus: 'Online',
                databaseStatus: 'Connected',
                integrationsStatus: 'Not configured',
                generatedUtc: new Date().toISOString()
            });
        }

        if (path === '/api/admin/technicians/metrics' && isGet) {
            const users = await allUsers();
            const technicians = users.filter(user => user.role !== 'admin');
            const states = technicians.map(evaluateLicense);
            return sendJson(res, 200, {
                totalTechnicians: technicians.length,
                activeSubscriptions: states.filter(state => state.active && state.type !== 'trial').length,
                pendingVerifications: technicians.filter(user => user.isVerified !== true).length,
                suspended: technicians.filter(user => user.isActive === false).length,
                hardwareBindings: 0,
                operationsLogged: 0,
                generatedUtc: new Date().toISOString()
            });
        }

        if (path === '/api/admin/technicians' && isGet) {
            const users = await directoryFrom(url)();
            const rows = users.filter(user => user.role !== 'admin').map(toTechnicianSummary);
            return sendJson(res, 200, pageOf(rows, url));
        }

        if (path === '/api/users' && isGet) {
            const users = await directoryFrom(url)();
            return sendJson(res, 200, pageOf(users.map(toTechnicianSummary), url));
        }

        if (path === '/api/admin/roles' && isGet) {
            const users = await allUsers();
            const adminCount = users.filter(user => user.role === 'admin').length;
            return sendJson(res, 200, [
                {
                    id: toGuidString('role:admin'),
                    name: 'Admin',
                    description: 'Full management console access.',
                    isSystem: true,
                    userCount: adminCount,
                    permissions: ADMIN_PERMISSIONS,
                    createdUtc: new Date(0).toISOString()
                },
                {
                    id: toGuidString('role:technician'),
                    name: 'Technician',
                    description: 'Device servicing workstation, limited to a valid licence.',
                    isSystem: true,
                    userCount: users.length - adminCount,
                    permissions: TECHNICIAN_PERMISSIONS,
                    createdUtc: new Date(0).toISOString()
                }
            ]);
        }

        if (path === '/api/admin/permissions' && isGet) {
            const names = [...new Set([...ADMIN_PERMISSIONS, ...TECHNICIAN_PERMISSIONS])];
            return sendJson(res, 200, names.map(name => ({
                name,
                group: ADMIN_PERMISSIONS.includes(name) ? 'Platform' : 'Devices',
                description: null
            })));
        }

        if (path === '/api/admin/settings' && isGet) {
            return sendJson(res, 200, { settings: [] });
        }

        if (path === '/api/admin/integrations' && isGet) {
            return sendJson(res, 200, []);
        }

        if (path === '/api/admin/backups' && isGet) {
            return sendJson(res, 200, []);
        }

        if (path === '/api/admin/versions' && isGet) {
            return sendJson(res, 200, []);
        }

        if (path === '/api/admin/audit-events' && isGet) {
            const action = String(url.searchParams.get('action') || '').trim().toLowerCase();
            const outcome = String(url.searchParams.get('outcome') || '').trim().toLowerCase();
            const rows = recentEvents
                .slice()
                .reverse()
                .map(entry => ({
                    id: entry.id,
                    occurredUtc: entry.occurredUtc,
                    actorName: 'server',
                    action: entry.message.split(':')[0].slice(0, 80),
                    targetType: 'Server',
                    targetId: null,
                    outcome: /fail|reject|mismatch|error/i.test(entry.message) ? 'Failure' : 'Success',
                    detail: entry.message,
                    ipAddress: null,
                    correlationId: entry.id
                }))
                .filter(row => action.length === 0 || String(row.action).toLowerCase().includes(action))
                .filter(row => outcome.length === 0 || String(row.outcome).toLowerCase() === outcome);
            return sendJson(res, 200, {
                items: rows.slice(0, 200),
                totalCount: rows.length,
                page: 1,
                pageSize: 200
            });
        }

        if (path === '/api/admin/health' && isGet) {
            let database = 'Unavailable';
            try {
                await store.findById('health-probe');
                database = 'Healthy';
            } catch (_) {
                database = 'Unavailable';
            }
            return sendJson(res, 200, {
                overall: database === 'Healthy' ? 'Healthy' : 'Degraded',
                components: [{ name: 'Database', status: database, detail: null, checkedUtc: new Date().toISOString() }],
                checkedUtc: new Date().toISOString()
            });
        }

        if (path === '/api/admin/status' && isGet) {
            return sendJson(res, 200, {
                serverVersion: SERVER_VERSION,
                runtimeVersion: process.version,
                environment: process.env.NODE_ENV || 'production',
                databaseProvider: 'PostgreSQL',
                databaseConnected: true,
                pendingMigrations: 0,
                maintenanceEnabled: maintenance.enabled,
                utcNow: new Date().toISOString(),
                processUptimeSeconds: Math.round(process.uptime()),
                cpuPercent: 0,
                workingSetBytes: process.memoryUsage().rss
            });
        }

        if (path === '/api/admin/maintenance' && (isGet || isPatch || isPost)) {
            if (isPatch || isPost) {
                const data = parseBody(await readBody(req));
                maintenance.enabled = data.enabled === true || data.Enabled === true;
                maintenance.message = typeof data.message === 'string' ? data.message : maintenance.message;
                maintenance.startsAt = data.startsAt || null;
                maintenance.endsAt = data.endsAt || null;
                maintenance.updatedUtc = new Date().toISOString();
                log(`Maintenance mode ${maintenance.enabled ? 'enabled' : 'disabled'}.`);
            }
            return sendJson(res, 200, { ...maintenance });
        }

        if (path === '/api/licenses' && isGet) {
            const users = await allUsers();
            const status = String(url.searchParams.get('status') || '').trim().toLowerCase();
            let rows = users.filter(user => user.role !== 'admin').map(toLicenseRow);
            if (status.length > 0) {
                rows = rows.filter(row => String(row.effectiveStatus).toLowerCase() === status ||
                    String(row.status).toLowerCase() === status);
            }
            return sendJson(res, 200, pageOf(rows, url));
        }

        if (path === '/api/licenses/plans' && isGet) {
            return sendJson(res, 200, plans.map(plan => ({ ...plan })));
        }

        if (path === '/api/licenses/plans' && isPost) {
            const data = parseBody(await readBody(req));
            const name = String(pick(data, 'name', 'Name') || '').trim();
            if (name.length === 0) {
                return fail(res, { status: 400, code: 'invalid-plan', message: 'A plan needs a name.' });
            }
            const plan = {
                id: newId(),
                code: name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || `plan-${plans.length + 1}`,
                name,
                nameAr: null,
                description: String(pick(data, 'description', 'Description') || ''),
                durationDays: Number(data.durationDays) > 0 ? Math.round(Number(data.durationDays)) : null,
                maxActivations: 0,
                isLifetime: data.isLifetime === true,
                isActive: data.isActive !== false,
                sortOrder: plans.length + 1,
                licenseCount: 0,
                price: Number(data.price) || null,
                currency: data.currency ? String(data.currency) : 'USD'
            };
            plans.push(plan);
            log(`Subscription plan created: ${plan.name}.`);
            return sendJson(res, 200, { ...plan });
        }

        const planMatch = /^\/api\/licenses\/plans\/([^/]+)$/.exec(path);
        if (planMatch && (isPatch || isDelete)) {
            const plan = plans.find(item => String(item.id).toLowerCase() === planMatch[1] || item.code === planMatch[1]);
            if (!plan) {
                return fail(res, { status: 404, code: 'plan-not-found', message: 'That plan does not exist.' });
            }
            if (isDelete) {
                plan.isActive = false;
                log(`Subscription plan deactivated: ${plan.name}.`);
                return sendJson(res, 200, { ...plan });
            }
            const data = parseBody(await readBody(req));
            if (typeof data.name === 'string' && data.name.trim().length > 0) {
                plan.name = data.name.trim();
            }
            if (typeof data.description === 'string') {
                plan.description = data.description;
            }
            if (Number(data.durationDays) > 0) {
                plan.durationDays = Math.round(Number(data.durationDays));
            }
            if (typeof data.price === 'number') {
                plan.price = data.price;
            }
            if (typeof data.isActive === 'boolean') {
                plan.isActive = data.isActive;
            }
            return sendJson(res, 200, { ...plan });
        }

        const technicianMatch = /^\/api\/admin\/technicians\/([^/]+)(?:\/([a-z-]+))?$/.exec(path);
        if (technicianMatch) {
            const user = await findAccount(technicianMatch[1]);
            if (!user) {
                return fail(res, { status: 404, code: 'account-not-found', message: 'That account does not exist.' });
            }
            const action = technicianMatch[2] || '';

            if (action === '' && isDelete) {
                if (user.role === 'admin') {
                    return fail(res, { status: 400, code: 'admin-protected', message: 'The administrator account cannot be deleted.' });
                }
                if (typeof store.deleteUser === 'function') {
                    await store.deleteUser(user.id);
                } else {
                    await store.updateUser(user.id, { isActive: false });
                }
                log(`Technician deleted: ${user.userName}.`);
                return sendJson(res, 200, { message: `${user.userName} was removed.` });
            }

            if (action === 'reset-hwid' && isPost) {
                // Device binding is gone; this clears any fingerprint a legacy row still carries.
                const updated = await store.updateUser(user.id, { hwid: null });
                log(`Legacy device fingerprint cleared for ${user.userName}.`);
                return sendJson(res, 200, {
                    userId: toGuidString((updated || user).id),
                    userName: (updated || user).userName,
                    hardwareId: null,
                    hardwareIdResetCount: 0,
                    message: 'Device binding is disabled on this server; the account already signs in from any computer.'
                });
            }

            if (action === 'verify-email' && isPost) {
                const data = parseBody(await readBody(req));
                const verified = data.verified === true || data.Verified === true;
                const patch = { isVerified: verified };
                if (verified) {
                    // A verification delivered by support closes the pending code, exactly as the
                    // technician's own code entry does, and starts the trial if it has not been used.
                    patch.otpCode = null;
                    patch.otpExpiresAt = null;
                    const state = evaluateLicense(user);
                    if (!state.active || state.type === 'trial') {
                        const startedAt = new Date();
                        patch.licenseType = 'trial';
                        patch.licenseStartedAt = startedAt;
                        patch.licenseExpiresAt = new Date(startedAt.getTime() + TRIAL_DAYS * 86400000);
                    }
                }
                const updated = await store.updateUser(user.id, patch);
                log(`E-mail verification for ${user.userName} set to ${verified}.`);
                return sendJson(res, 200, {
                    userId: toGuidString((updated || user).id),
                    userName: (updated || user).userName,
                    emailVerified: verified,
                    message: verified ? 'The account is now verified.' : 'The account is unverified again.'
                });
            }

            if (action === 'reset-trial' && isPost) {
                if (user.role === 'admin') {
                    return fail(res, {
                        status: 400,
                        code: 'admin-protected',
                        message: 'The administrator account has lifetime access and needs no trial.'
                    });
                }

                // Support runs this when a technician's 72 hours were consumed while their account
                // was unreachable: the trial starts again now, not from the original registration.
                const startedAt = new Date();
                const updated = await store.updateUser(user.id, {
                    isActive: true,
                    licenseType: 'trial',
                    licenseStartedAt: startedAt,
                    licenseExpiresAt: new Date(startedAt.getTime() + TRIAL_DAYS * 86400000)
                });
                const account = updated || user;
                const state = evaluateLicense(account);
                log(`3-day trial restarted for ${user.userName} until ${state.expiresAt.toISOString()}.`);
                return sendJson(res, 200, {
                    userId: toGuidString(account.id),
                    userName: account.userName,
                    ...licenseAccessFor(account),
                    startsAt: isoOrNull(state.startedAt),
                    expiresAt: isoOrNull(state.expiresAt),
                    message: `The 3-day trial now runs until ${state.expiresAt.toISOString()}.`
                });
            }

            if (action === 'extend-subscription' && isPost) {
                const data = parseBody(await readBody(req));
                const days = Math.round(Number(data.days) || 0);
                if (days < 1 || days > 3650) {
                    return fail(res, { status: 400, code: 'invalid-days', message: 'Enter a number of days between 1 and 3650.' });
                }
                const updated = await extendAccount(user, days);
                const state = evaluateLicense(updated);
                return sendJson(res, 200, {
                    licenseId: toGuidString(`license:${updated.id}`),
                    licenseKey: buildLicenseSummary(updated).licenseKey,
                    planName: buildLicenseSummary(updated).planName,
                    expiresAt: isoOrNull(state.expiresAt),
                    message: `${days} day(s) added to ${updated.userName}.`
                });
            }

            if (action === 'plan' && isPost) {
                const data = parseBody(await readBody(req));
                const planId = String(pick(data, 'planId', 'id') || '');
                const plan = plans.find(item => String(item.id).toLowerCase() === planId.toLowerCase());
                if (!plan) {
                    return fail(res, { status: 404, code: 'plan-not-found', message: 'That plan does not exist.' });
                }
                const days = plan.durationDays || 3650;
                const updated = await extendAccount(user, days);
                await store.updateUser(user.id, { licenseType: plan.code });
                log(`${user.userName} moved to the ${plan.name} plan.`);
                return sendJson(res, 200, toLicenseRow({ ...updated, licenseType: plan.code }));
            }

            return fail(res, ERRORS.notFound);
        }

        const userAction = /^\/api\/users\/([^/]+)\/(suspend|reactivate|revoke-sessions)$/.exec(path);
        if (userAction && isPost) {
            const user = await findAccount(userAction[1]);
            if (!user) {
                return fail(res, { status: 404, code: 'account-not-found', message: 'That account does not exist.' });
            }
            if (user.role === 'admin') {
                return fail(res, { status: 400, code: 'admin-protected', message: 'The administrator account cannot be suspended.' });
            }
            if (userAction[2] === 'suspend') {
                await store.updateUser(user.id, { isActive: false });
                log(`Account suspended: ${user.userName}.`);
                return sendJson(res, 200, { message: `${user.userName} is suspended.` });
            }
            if (userAction[2] === 'reactivate') {
                await store.updateUser(user.id, { isActive: true, failedAttempts: 0, lockoutUntil: null });
                log(`Account reactivated: ${user.userName}.`);
                return sendJson(res, 200, { message: `${user.userName} is active again.` });
            }
            return sendJson(res, 200, { message: 'Sessions revoked.' });
        }

        return fail(res, ERRORS.notFound);
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

            // The service queue the desktop polls for cloud-synchronised work.
            if ((path === '/api/operations/queue' || path === '/api/v1/service/queue') && req.method === 'GET') {
                return await handleServiceQueue(req, res);
            }

            // Chipset loader archives, streamed through this service (see handleLoaderDownload).
            if (path.startsWith(LOADER_PROXY_ROUTE) && req.method === 'GET') {
                return await handleLoaderDownload(req, res, path.substring(LOADER_PROXY_ROUTE.length));
            }

            // The administration console's routes (see routeAdmin above).
            if ((await routeAdmin(req, res, path, url)) !== false) {
                return;
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
    licenseAccessFor,
    normalizeRole,
    EMAIL_REGEX,
    MASTER_ADMIN,
    ERRORS,
    TRIAL_DAYS,
    OTP_TTL_MINUTES,
    OTP_EMAIL_SUBJECT,
    buildOtpText,
    buildOtpHtml,
    toGuidString
};

if (require.main === module) {
    startServer().catch(error => {
        console.error('B_VIP Server failed to start:', error);
        process.exit(1);
    });
}
