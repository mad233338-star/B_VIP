const http = require('http');
const WebSocket = require('ws');
const crypto = require('crypto');

const PORT = process.env.PORT || 3000;
const DB_URL = process.env.ConnectionStrings__DefaultConnection || process.env.DATABASE_URL;
const SIGNING_KEY = process.env.Auth__SigningKey || 'B-VIP-DEFAULT-KEY-2026';

// -------------------------------------------------------------
// 1. إعداد الاتصال بقاعدة البيانات (PostgreSQL)
// -------------------------------------------------------------
let pool = null;
try {
    const { Pool } = require('pg');
    if (DB_URL) {
        pool = new Pool({
            connectionString: DB_URL,
            ssl: { rejectUnauthorized: false }
        });
        console.log('PostgreSQL Pool Initialized.');
    }
} catch (e) {
    console.warn('pg package not installed yet or loading failed. Running in fallback mode.');
}

// إنشاء الجداول تلقائياً ومستخدم الأدمن الافتراضي
async function initDatabase() {
    if (!pool) return;
    try {
        await pool.query(`
            CREATE TABLE IF NOT EXISTS users (
                id SERIAL PRIMARY KEY,
                username VARCHAR(100) UNIQUE NOT NULL,
                password VARCHAR(255) NOT NULL,
                hwid VARCHAR(255),
                role VARCHAR(50) DEFAULT 'User',
                is_active BOOLEAN DEFAULT TRUE,
                expires_at TIMESTAMP DEFAULT (NOW() + INTERVAL '365 days'),
                created_at TIMESTAMP DEFAULT NOW()
            );
        `);

        // التأكد من وجود مستخدم mahhher الأساسي
        const checkUser = await pool.query('SELECT * FROM users WHERE username = $1', ['mahhher']);
        if (checkUser.rows.length === 0) {
            await pool.query(
                `INSERT INTO users (username, password, role, is_active, expires_at) 
                 VALUES ($1, $2, $3, $4, NOW() + INTERVAL '365 days')`,
                ['mahhher', '12345678', 'Admin', true]
            );
            console.log('Default user (mahhher) created successfully.');
        }
    } catch (err) {
        console.error('Database initialization error:', err.message);
    }
}
initDatabase();

// -------------------------------------------------------------
// 2. معالجة طلبات HTTP والـ API الخاصة بالتراخيص وتسجيل الدخول
// -------------------------------------------------------------
const server = http.createServer(async (req, res) => {
    // إعداد ترويسات CORS والـ JSON
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type, Authorization');

    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }

    const url = req.url.toLowerCase();

    // فحص صحة السيرفر عبر GET
    if (req.method === 'GET') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({
            status: 'online',
            service: 'B_VIP Software Licensing & Remote Server',
            developer: 'Bashir Al-Farhan',
            timestamp: new Date().toISOString()
        }));
        return;
    }

    // استقبال طلب تسجيل الدخول (يدعم أي مسار مثل /api/auth/login أو /login)
    if (req.method === 'POST') {
        let rawBody = '';
        req.on('data', chunk => { rawBody += chunk; });
        req.on('end', async () => {
            try {
                let data = {};
                if (rawBody) {
                    try { data = JSON.parse(rawBody); } catch (_) {}
                }

                const username = data.username || data.Username || data.user || '';
                const password = data.password || data.Password || data.pass || '';
                const hwid = data.hwid || data.Hwid || data.deviceId || 'UNKNOWN_HWID';

                // التحقق من قاعدة البيانات إن كانت متاحة
                let userRecord = null;
                if (pool) {
                    try {
                        const result = await pool.query('SELECT * FROM users WHERE LOWER(username) = LOWER($1)', [username]);
                        if (result.rows.length > 0) {
                            userRecord = result.rows[0];
                        }
                    } catch (dbErr) {
                        console.error('DB query error:', dbErr.message);
                    }
                }

                // إذا لم توجد قاعدة بيانات بعد، يتم تمرير الدخول كنسخة احتياطية
                const isAuthenticated = userRecord ? (userRecord.password === password) : (username.length > 0);

                if (!isAuthenticated) {
                    res.writeHead(401, { 'Content-Type': 'application/json; charset=utf-8' });
                    res.end(JSON.stringify({
                        success: false,
                        Success: false,
                        message: 'Invalid username or password',
                        Message: 'Invalid username or password'
                    }));
                    return;
                }

                // ربط بصمة الجهاز (HWID) في أول تسجيل دخول
                if (pool && userRecord && (!userRecord.hwid || userRecord.hwid === '')) {
                    await pool.query('UPDATE users SET hwid = $1 WHERE id = $2', [hwid, userRecord.id]);
                }

                const expiryDate = userRecord?.expires_at 
                    ? new Date(userRecord.expires_at).toISOString() 
                    : new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString();

                const role = userRecord?.role || 'Admin';
                const token = crypto.createHash('sha256').update(username + SIGNING_KEY + Date.now()).digest('hex');

                // إرجاع استجابة JSON متطابقة مع نموذج C# (بصيغتي CamelCase و PascalCase لتفادي أي خطأ)
                const responsePayload = {
                    success: true,
                    Success: true,
                    message: 'Login successful',
                    Message: 'Login successful',
                    token: token,
                    Token: token,
                    username: username,
                    Username: username,
                    role: role,
                    Role: role,
                    isActive: true,
                    IsActive: true,
                    isSubscribed: true,
                    IsSubscribed: true,
                    expiresAt: expiryDate,
                    ExpiresAt: expiryDate,
                    expiryDate: expiryDate,
                    ExpiryDate: expiryDate,
                    daysRemaining: 365,
                    DaysRemaining: 365
                };

                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify(responsePayload));
                console.log(`B_VIP Login Success for user: ${username}`);
            } catch (err) {
                console.error('Error handling login request:', err);
                res.writeHead(500, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({
                    success: false,
                    Success: false,
                    message: 'Server error processing authentication',
                    Message: 'Server error processing authentication'
                }));
            }
        });
        return;
    }

    res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ error: 'Endpoint not found' }));
});

// -------------------------------------------------------------
// 3. خادم WebSocket للتحكم عن بعد وبث الشاشة (كما هو دون تعديل)
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
