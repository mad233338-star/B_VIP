const http = require('http');
const WebSocket = require('ws');
const crypto = require('crypto');

const PORT = process.env.PORT || 3000;
const DB_URL = process.env.ConnectionStrings__DefaultConnection || process.env.DATABASE_URL;
const SIGNING_KEY = process.env.Auth__SigningKey || 'B-VIP-SIGNING-KEY-2026-bashir-alfarhan-9f4c2e7a1b8d6350';

// -------------------------------------------------------------
// 1. الاتصال بقاعدة البيانات PostgreSQL
// -------------------------------------------------------------
let pool = null;
try {
    const { Pool } = require('pg');
    if (DB_URL) {
        pool = new Pool({
            connectionString: DB_URL,
            ssl: { rejectUnauthorized: false }
        });
        console.log('PostgreSQL Connected.');
    }
} catch (e) {
    console.warn('PostgreSQL client not ready, running in fallback mode.');
}

async function initDatabase() {
    if (!pool) return;
    try {
        await pool.query(`
            CREATE TABLE IF NOT EXISTS users (
                id VARCHAR(100) PRIMARY KEY,
                username VARCHAR(100) UNIQUE NOT NULL,
                password VARCHAR(255) NOT NULL,
                hwid VARCHAR(255),
                role VARCHAR(50) DEFAULT 'Admin',
                is_active BOOLEAN DEFAULT TRUE,
                expires_at TIMESTAMP DEFAULT (NOW() + INTERVAL '365 days'),
                created_at TIMESTAMP DEFAULT NOW()
            );
        `);
        console.log('Database tables verified.');
    } catch (err) {
        console.error('DB init warning:', err.message);
    }
}
initDatabase();

// دالة لإنشاء توكن JWT نظامي يقرأه .NET بدون أخطاء
function generateValidJwt(payload, secret) {
    const base64url = (str) => Buffer.from(str).toString('base64').replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
    const header = { alg: 'HS256', typ: 'JWT' };
    const encodedHeader = base64url(JSON.stringify(header));
    const encodedPayload = base64url(JSON.stringify(payload));
    const signature = crypto.createHmac('sha256', secret).update(`${encodedHeader}.${encodedPayload}`).digest('base64')
        .replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
    return `${encodedHeader}.${encodedPayload}.${signature}`;
}

// -------------------------------------------------------------
// 2. معالجة طلبات التراخيص وتسجيل الدخول (API)
// -------------------------------------------------------------
const server = http.createServer(async (req, res) => {
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type, Authorization, Accept, X-Requested-With');

    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }

    const expiryDate = new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString();
    const defaultGuid = "c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d";

    if (req.method === 'GET') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({
            status: 'online',
            service: 'B_VIP Software Licensing & Remote Server',
            developer: 'Bashir Al-Farhan',
            success: true,
            isActive: true,
            isSubscribed: true,
            daysRemaining: 365,
            expiresAt: expiryDate
        }));
        return;
    }

    if (req.method === 'POST') {
        let rawBody = '';
        req.on('data', chunk => { rawBody += chunk; });
        req.on('end', async () => {
            try {
                let data = {};
                if (rawBody) {
                    try {
                        data = JSON.parse(rawBody);
                    } catch (_) {
                        const params = new URLSearchParams(rawBody);
                        for (const [k, v] of params.entries()) {
                            data[k] = v;
                        }
                    }
                }

                const username = data.username || data.Username || data.user || data.User || 'Admin';
                const password = data.password || data.Password || data.pass || '';
                const hwid = data.hwid || data.Hwid || data.HWID || 'DEV_HOST';
                const userId = defaultGuid;

                if (pool) {
                    try {
                        await pool.query(
                            `INSERT INTO users (id, username, password, hwid, role, is_active, expires_at)
                             VALUES ($1, $2, $3, $4, 'Admin', true, NOW() + INTERVAL '365 days')
                             ON CONFLICT (username) 
                             DO UPDATE SET password = EXCLUDED.password, hwid = COALESCE(users.hwid, EXCLUDED.hwid);`,
                            [userId, username, password, hwid]
                        );
                    } catch (dbErr) {
                        console.error('DB query error:', dbErr.message);
                    }
                }

                // مصفوفات الصلاحيات لتغذية دوال LINQ و C#
                const rolesList = ['Admin', 'User', 'Technician'];
                const permissionsList = ['All', 'Admin', 'RemoteControl', 'ScreenShare', 'CurtainMode', 'Bypass', 'FRP', 'iCloud', 'Repair', 'Flash'];
                const claimsList = [
                    { type: 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role', value: 'Admin' },
                    { type: 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name', value: username },
                    { type: 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier', value: userId },
                    { type: 'role', value: 'Admin' },
                    { type: 'name', value: username },
                    { type: 'sub', value: userId }
                ];

                // إنشاء توكن JWT حقيقي وصحيح
                const jwtPayload = {
                    sub: userId,
                    nameid: userId,
                    unique_name: username,
                    name: username,
                    role: 'Admin',
                    roles: rolesList,
                    permissions: permissionsList,
                    nbf: Math.floor(Date.now() / 1000) - 60,
                    exp: Math.floor(Date.now() / 1000) + (365 * 24 * 60 * 60),
                    iss: 'B_VIP_Server',
                    aud: 'B_VIP_Client'
                };
                const token = generateValidJwt(jwtPayload, SIGNING_KEY);

                // كائن الاشتراك المفرد والمصفوفة
                const singleSubscription = {
                    id: userId,
                    Id: userId,
                    name: 'VIP Full Access',
                    Name: 'VIP Full Access',
                    isActive: true,
                    IsActive: true,
                    status: 'Active',
                    Status: 'Active',
                    expiresAt: expiryDate,
                    ExpiresAt: expiryDate,
                    daysRemaining: 365,
                    DaysRemaining: 365,
                    features: permissionsList,
                    Features: permissionsList
                };

                const subscriptionsList = [singleSubscription];

                // كائن ملخص المستخدم مع جميع المصفوفات غير الفارغة
                const userSummary = {
                    id: userId,
                    Id: userId,
                    username: username,
                    Username: username,
                    email: `${username}@b-vip.com`,
                    Email: `${username}@b-vip.com`,
                    role: 'Admin',
                    Role: 'Admin',
                    roles: rolesList,
                    Roles: rolesList,
                    permissions: permissionsList,
                    Permissions: permissionsList,
                    claims: claimsList,
                    Claims: claimsList,
                    features: permissionsList,
                    Features: permissionsList,
                    subscriptions: subscriptionsList,
                    Subscriptions: subscriptionsList,
                    isActive: true,
                    IsActive: true,
                    hwid: hwid,
                    Hwid: hwid,
                    expiresAt: expiryDate,
                    ExpiresAt: expiryDate,
                    createdAt: new Date().toISOString(),
                    CreatedAt: new Date().toISOString()
                };

                // الحزمة الرئيسية التي تدعم كلاً من الاستجابة المباشرة وغلاف Data
                const responsePayload = {
                    success: true,
                    Success: true,
                    status: 'success',
                    Status: 'success',
                    message: 'Login successful',
                    Message: 'Login successful',
                    token: token,
                    Token: token,
                    accessToken: token,
                    AccessToken: token,
                    refreshToken: token,
                    RefreshToken: token,
                    userId: userId,
                    UserId: userId,
                    user: userSummary,
                    User: userSummary,
                    data: userSummary,
                    Data: userSummary,
                    roles: rolesList,
                    Roles: rolesList,
                    permissions: permissionsList,
                    Permissions: permissionsList,
                    claims: claimsList,
                    Claims: claimsList,
                    features: permissionsList,
                    Features: permissionsList,
                    subscription: singleSubscription,
                    Subscription: singleSubscription,
                    subscriptions: subscriptionsList,
                    Subscriptions: subscriptionsList,
                    expiresAt: expiryDate,
                    ExpiresAt: expiryDate,
                    daysRemaining: 365,
                    DaysRemaining: 365,
                    isActive: true,
                    IsActive: true
                };

                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify(responsePayload));
                console.log(`B_VIP Login Fully Authorized for: ${username}`);
            } catch (err) {
                console.error('Login POST error:', err);
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, message: 'Fallback Success' }));
            }
        });
        return;
    }

    res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ error: 'Endpoint not found' }));
});

// -------------------------------------------------------------
// 3. خادم WebSocket للبث والتحكم عن بعد
// -------------------------------------------------------------
const wss = new WebSocket.Server({ server });
const sessions = new Map();

wss.on('connection', (ws) => {
    let deviceId = null;
    let peerId = null;

    ws.on('message', (message, isBinary) => {
        if (isBinary) {
            if (peerId && sessions.has(peerId)) {
                const targetWs = sessions.get(peerId);
                if (targetWs.readyState === WebSocket.OPEN) {
                    targetWs.send(message, { binary: true });
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
                console.log(`B_VIP Device Registered: ${deviceId}`);
            }

            if (data.type === 'connect') {
                peerId = data.targetId;
                if (sessions.has(peerId)) {
                    const peerWs = sessions.get(peerId);
                    peerWs.send(JSON.stringify({ type: 'session_start', peerId: deviceId }));
                    ws.send(JSON.stringify({ type: 'connected_success', targetId: peerId }));
                    console.log(`Connected: ${deviceId} -> ${peerId}`);
                } else {
                    ws.send(JSON.stringify({ type: 'error', message: 'الجهاز غير متصل حالياً' }));
                }
            }

            if (data.type === 'input_event' || data.type === 'command') {
                if (peerId && sessions.has(peerId)) {
                    sessions.get(peerId).send(JSON.stringify(data));
                }
            }
        } catch (err) {
            console.error('Error handling WebSocket message:', err);
        }
    });

    ws.on('close', () => {
        if (deviceId && sessions.has(deviceId)) {
            sessions.delete(deviceId);
        }
        if (peerId && sessions.has(peerId)) {
            const peerWs = sessions.get(peerId);
            if (peerWs.readyState === WebSocket.OPEN) {
                peerWs.send(JSON.stringify({ type: 'peer_disconnected' }));
            }
        }
    });
});

server.listen(PORT, () => {
    console.log(`B_VIP Server listening on port ${PORT}`);
});
