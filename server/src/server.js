'use strict';

const crypto = require('crypto');
const { WebSocketServer } = require('ws');
const cfg = require('./config');
const S = require('./state');
const { wakeDevice } = require('./fcm');
const { iceServers } = require('./turn');

function send(ws, obj) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
}
function code6() {
  return String(crypto.randomInt(100000, 1000000));
}

function start() {
  const wss = new WebSocketServer({ port: cfg.port });

  wss.on('connection', (ws) => {
    ws.deviceId = null;
    ws.isAlive = true;
    ws.on('pong', () => { ws.isAlive = true; });

    ws.on('message', (raw) => {
      let m;
      try { m = JSON.parse(raw.toString()); } catch { return; }
      handle(ws, m).catch((e) => console.error('[handler]', m?.type, e.message));
    });

    ws.on('close', () => {
      if (ws.deviceId) S.devices.delete(ws.deviceId);
    });
  });

  // Liveness ping — drop half-open sockets.
  const ping = setInterval(() => {
    wss.clients.forEach((ws) => {
      if (!ws.isAlive) return ws.terminate();
      ws.isAlive = false;
      ws.ping();
    });
  }, 30000);
  wss.on('close', () => clearInterval(ping));

  // Prune expired codes + pending wakes.
  setInterval(() => {
    const now = Date.now();
    for (const [c, s] of S.sessions) if (s.expires < now) S.sessions.delete(c);
    for (const [h, w] of S.pendingWakes) if (w.expires < now) S.pendingWakes.delete(h);
  }, 15000);

  console.log(`[signaling] listening on ws://0.0.0.0:${cfg.port}`);
  return wss;
}

async function handle(ws, m) {
  switch (m.type) {
    // ---- identity / registration ----
    case 'register': {
      ws.deviceId = m.deviceId;
      S.devices.set(m.deviceId, ws);
      send(ws, { type: 'registered', deviceId: m.deviceId, iceServers: iceServers() });

      // flush any wake that was queued while the host was offline
      const pending = S.pendingWakes.get(m.deviceId);
      if (pending && pending.expires > Date.now()) {
        send(ws, {
          type: 'join-request',
          controllerId: pending.controllerId,
          unattended: pending.unattended,
        });
        S.pendingWakes.delete(m.deviceId);
      }
      break;
    }

    case 'report-token': {
      if (ws.deviceId) S.fcmTokens.set(ws.deviceId, m.fcmToken);
      break;
    }

    case 'turn-credentials': {
      send(ws, { type: 'turn-credentials', iceServers: iceServers() });
      break;
    }

    // ---- host opens a code-based session ----
    case 'host-open': {
      const code = code6();
      S.sessions.set(code, { hostId: ws.deviceId, expires: Date.now() + cfg.sessionCodeTtlMs });
      send(ws, { type: 'session-code', code });
      break;
    }

    // ---- controller dials in (by code, or directly to a paired host) ----
    case 'join': {
      let hostId = null;
      if (m.code) {
        const s = S.sessions.get(m.code);
        if (!s || s.expires < Date.now()) return send(ws, { type: 'join-failed', reason: 'invalid-code' });
        hostId = s.hostId;
        S.sessions.delete(m.code); // one-time use
      } else if (m.hostId) {
        // paired-direct dial: requires an existing pairing
        if (!S.arePaired(ws.deviceId, m.hostId)) return send(ws, { type: 'join-failed', reason: 'not-paired' });
        hostId = m.hostId;
      } else {
        return send(ws, { type: 'join-failed', reason: 'no-target' });
      }

      const grant = S.findGrant(hostId, ws.deviceId);
      const unattended = !!grant;

      const req = {
        type: 'join-request',
        controllerId: ws.deviceId,
        unattended,
        grantId: grant?.grantId,
      };

      const host = S.devices.get(hostId);
      if (host) {
        send(host, req);
      } else {
        await wakeDevice(hostId, { controllerId: ws.deviceId, sessionId: m.code, unattended });
        S.pendingWakes.set(hostId, {
          controllerId: ws.deviceId,
          code: m.code,
          unattended,
          expires: Date.now() + cfg.pendingWakeTtlMs,
        });
      }
      send(ws, { type: 'join-pending', hostId, unattended });
      break;
    }

    // ---- host consent result (attended) ----
    case 'consent': {
      send(S.devices.get(m.controllerId), {
        type: 'consent',
        accepted: m.accepted,
        hostId: ws.deviceId,
      });
      break;
    }

    // ---- WebRTC handshake relay ----
    case 'offer':
    case 'answer':
    case 'ice':
    case 'restart':
    case 'hangup': {
      send(S.devices.get(m.to), { ...m, from: ws.deviceId });
      break;
    }

    // ---- unattended grants ----
    case 'register-grant': {
      if (!S.grants.has(ws.deviceId)) S.grants.set(ws.deviceId, new Map());
      S.grants.get(ws.deviceId).set(m.grant.grantId, m.grant);
      send(ws, { type: 'grant-registered', grantId: m.grant.grantId });
      break;
    }
    case 'revoke-grant': {
      S.grants.get(ws.deviceId)?.delete(m.grantId);
      break;
    }

    // ---- pairing graph ----
    case 'register-pairing': {
      S.linkPair(m.myPub, m.peerPub);
      S.linkPair(m.peerPub, m.myPub);
      send(ws, { type: 'pairing-registered', peerPub: m.peerPub });
      break;
    }
    case 'revoke-pairing': {
      S.unlinkPair(m.myPub, m.peerPub);
      S.unlinkPair(m.peerPub, m.myPub);
      break;
    }

    // ---- pairing handshake + per-session auth relay ----
    case 'pair-complete':
    case 'pair-ack':
    case 'auth-challenge':
    case 'auth-response': {
      send(S.devices.get(m.to), { ...m, from: ws.deviceId });
      break;
    }

    default:
      // ignore unknown types
      break;
  }
}

module.exports = { start, handle, send, code6 };

if (require.main === module) {
  start();
}
