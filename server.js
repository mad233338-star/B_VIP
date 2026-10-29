const http = require('http');
const WebSocket = require('ws');

const PORT = process.env.PORT || 3000;

// سيرفر HTTP للاستجابة وفحص الحالة وتفادي توقف السيرفر
const server = http.createServer((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('B_VIP Remote Server is Online 🚀');
});

const wss = new WebSocket.Server({ server });
const sessions = new Map();

wss.on('connection', (ws) => {
    let deviceId = null;
    let peerId = null;

    ws.on('message', (message, isBinary) => {
        // تمرير بث الشاشة المباشر كبيانات ثنائية (Binary)
        if (isBinary) {
            if (peerId && sessions.has(peerId)) {
                const targetWs = sessions.get(peerId);
                if (targetWs.readyState === WebSocket.OPEN) {
                    targetWs.send(message, { binary: true });
                }
            }
            return;
        }

        // استقبال وتوجيه أوامر الماوس والكيبورد وربط الأجهزة
        try {
            const data = JSON.parse(message.toString());

            // تسجيل الجهاز وتعيين المعرف (ID)
            if (data.type === 'register') {
                deviceId = data.id;
                sessions.set(deviceId, ws);
                ws.send(JSON.stringify({ type: 'registered', status: 'ok', id: deviceId }));
                console.log(`B_VIP Device Registered: ${deviceId}`);
            }

            // ربط جهاز الفني بجهاز الزبون
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

            // تمرير أوامر الفأرة، لوحة المفاتيح، والتحكم
            if (data.type === 'input_event' || data.type === 'command') {
                if (peerId && sessions.has(peerId)) {
                    sessions.get(peerId).send(JSON.stringify(data));
                }
            }
        } catch (err) {
            console.error('Error handling message:', err);
        }
    });

    // عند خروج أو انقطاع اتصال أحدهما
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
