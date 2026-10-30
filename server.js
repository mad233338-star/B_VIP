const http = require('http');
const WebSocket = require('ws');
const crypto = require('crypto');

const PORT = process.env.PORT || 3000;
const DB_URL = process.env.ConnectionStrings__DefaultConnection || process.env.DATABASE_URL;
const SIGNING_KEY = process.env.Auth__SigningKey || 'B-VIP-SIGNING-KEY-2026-bashir-alfarhan-9f4c2e7a1b8d6350';

// -------------------------------------------------------------
// 1. الاتصال بقاعدة البيانات وإعداد الجداول تلقائياً
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
                id SERIAL PRIMARY KEY,
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

    // الاستجابة لأي فحص فوري أو استعلام GET
    if (req.method === 'GET') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({
            status: 'online',
            service: 'B_VIP Software Licensing & Remote Server',
            developer: 'Bashir Al-Farhan',
            success: true,
            Success: true,
            isActive: true,
            IsActive: true,
            isSubscribed: true,
            IsSubscribed: true,
            daysRemaining: 365,
            DaysRemaining: 365,
            expiresAt: expiryDate,
            ExpiresAt: expiryDate
        }));
        return;
    }

    // معالجة تسجيل الدخول POST
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
                        // دعم صيغة Form URL Encoded إن أرسلها التطبيق
                        const params = new URLSearchParams(rawBody);
                        for (const [k, v] of params.entries()) {
                            data[k] = v;
                        }
                    }
                }

                console.log('Login request received with body:', rawBody);

                // استخراج اسم المستخدم والباسوورد بجميع الصيغ المحتملة
                const username = data.username || data.Username || data.user || data.User || data.login || data.Login || 'mahhher';
                const password = data.password || data.Password || data.pass || data.Pass || data.passwordHash || data.PasswordHash || '';
                const hwid = data.hwid || data.Hwid || data.HWID || data.deviceId || 'DEV_HOST';

                // حفظ أو تحديث المستخدم في قاعدة البيانات تلقائياً
                if (pool) {
                    try {
                        await pool.query(
                            `INSERT INTO users (username, password, hwid, role, is_active, expires_at)
                             VALUES ($1, $2, $3, 'Admin', true, NOW() + INTERVAL '365 days')
                             ON CONFLICT (username) 
                             DO UPDATE SET password = EXCLUDED.password, hwid = COALESCE(users.hwid, EXCLUDED.hwid);`,
                            [username, password, hwid]
                        );
                    } catch (dbErr) {
                        console.error('Auto-sync user DB error:', dbErr.message);
                    }
                }

                const token = crypto.createHash('sha256').update(username + SIGNING_KEY + Date.now()).digest('hex');

                // حزمة بيانات شاملة تلبي كافة نماذج C# و WPF
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
                    jwt: token,
                    username: username,
                    Username: username,
                    role: 'Admin',
                    Role: 'Admin',
                    isActive: true,
                    IsActive: true,
                    isSubscribed: true,
                    IsSubscribed: true,
                    expiresAt: expiryDate,
                    ExpiresAt: expiryDate,
                    expiryDate: expiryDate,
                    ExpiryDate: expiryDate,
                    daysRemaining: 365,
                    DaysRemaining: 365,
                    user: {
                        id: 1,
                        username: username,
                        role: 'Admin',
                        isActive: true,
                        expiresAt: expiryDate
                    },
                    User: {
                        Id: 1,
                        Username: username,
                        Role: 'Admin',
                        IsActive: true,
                        ExpiresAt: expiryDate
                    },
                    subscription: {
                        isActive: true,
                        status: 'Active',
                        expiresAt: expiryDate,
                        daysRemaining: 365
                    },
                    Subscription: {
                        IsActive: true,
                        Status: 'Active',
                        ExpiresAt: expiryDate,
                        DaysRemaining: 365
                    }
                };

                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify(responsePayload));
                console.log(`B_VIP Login approved successfully for: ${username}`);
            } catch (err) {
                console.error('Error handling login POST:', err);
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({
                    success: true,
                    Success: true,
                    message: 'Login approved',
                    token: 'B_VIP_EMERGENCY_PASS'
                }));
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
