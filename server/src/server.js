'use strict';

const crypto = require('crypto');
const { WebSocketServer } = require('ws');
const cfg = require('./config');
const S = require('./state');
const { wakeDevice } = require('./fcm');
const { iceServers } = require('./turn');
const auth = require('./auth');
const proto = require('./protocol');

// Message types a socket may send before it has proven its identity. Everything
// else — relay, pairing, grants, codes — requires an authenticated registration,
// otherwise gating registration would accomplish nothing.
const PRE_AUTH_TYPES = new Set(['register', 'register-proof']);

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
    ws.authenticated = false;
    ws.pendingAuth = null;
    ws.isAlive = true;
    ws.on('pong', () => { ws.isAlive = true; });

    ws.on('message', (raw) => {
      let m;
      try { m = JSON.parse(raw.toString()); } catch { return; }
      handle(ws, m).catch((e) => console.error('[handler]', m?.type, e.message));
    });

    ws.on('close', () => {
      // Only retract the routing entry if it is still ours. A displaced socket
      // closing later must not delete the registration that replaced it.
      if (ws.deviceId && S.devices.get(ws.deviceId) === ws) {
        S.devices.delete(ws.deviceId);
        // Metadata describes a live endpoint; a device that is gone advertises
        // nothing. Leaving it behind would let the UI offer a Sleep button for a
        // machine that is no longer connected.
        S.clearMeta(ws.deviceId);
      }
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
  // Fail closed: an unauthenticated socket may only attempt registration.
  if (!ws.authenticated && !PRE_AUTH_TYPES.has(m.type)) {
    return send(ws, { type: 'not-authenticated', rejected: m.type });
  }

  switch (m.type) {
    // ---- identity / registration (challenge-response, see auth.js) ----
    case 'register': {
      const begun = auth.beginRegistration(m.deviceId, m.publicKey, cfg.registerChallengeTtlMs);
      if (!begun.ok) {
        ws.pendingAuth = null;
        return send(ws, { type: 'register-failed', reason: begun.reason });
      }
      ws.pendingAuth = begun.pending;

      // Endpoint metadata is optional and is held aside, not published, until
      // the identity behind it has been proven. Publishing it here would let an
      // unauthenticated socket overwrite a live device's advertisement by
      // opening a connection and never completing the handshake.
      //
      // A client that sends nothing — every Android build shipped so far — is
      // not degraded in any way; it simply advertises nothing.
      ws.pendingMeta = proto.parseEndpointMeta(m.meta);

      send(ws, {
        type: 'register-challenge',
        challenge: begun.challenge,
        expiresInMs: cfg.registerChallengeTtlMs,
        protocol: { version: proto.PROTOCOL_VERSION, min: proto.MIN_PROTOCOL_VERSION },
      });
      break;
    }

    case 'register-proof': {
      // Consume the challenge before verifying, so it is single-use whether the
      // proof succeeds or fails. A wrong signature cannot be retried against the
      // same nonce, and an observed nonce is worthless once spent.
      const pending = ws.pendingAuth;
      const pendingMeta = ws.pendingMeta;
      ws.pendingAuth = null;
      ws.pendingMeta = null;

      const result = auth.completeRegistration(pending, m.deviceId, m.signature);
      if (!result.ok) {
        console.warn('[auth] registration rejected:', result.reason);
        return send(ws, { type: 'register-failed', reason: result.reason });
      }

      const deviceId = result.deviceId;

      // Authenticated replacement. Both sockets proved possession of the same
      // non-exportable private key, so the newcomer IS the device — this is a
      // legitimate reconnect (network switch, process restart, FCM wake), not a
      // takeover. It is explicit and announced rather than a silent overwrite:
      // the displaced socket is told why and closed, so it stops believing it is
      // registered. An attacker without the private key never reaches this line.
      const existing = S.devices.get(deviceId);
      if (existing && existing !== ws) {
        send(existing, { type: 'session-replaced', reason: 'authenticated-reconnect' });
        existing.authenticated = false;
        existing.deviceId = null;
        try { existing.close(4001, 'replaced by authenticated reconnect'); } catch { /* already gone */ }
        console.log(`[auth] ${deviceId.slice(0, 12)}… replaced an existing registration`);
      }

      ws.deviceId = deviceId;
      ws.authenticated = true;
      S.devices.set(deviceId, ws);
      // Now that the key behind the id is proven, the advertisement can be
      // attributed to it. A reconnect with no meta clears the previous one
      // rather than inheriting it, so a downgraded client cannot keep claiming
      // capabilities it no longer has.
      S.setMeta(deviceId, pendingMeta);

      send(ws, {
        type: 'registered',
        deviceId,
        iceServers: iceServers(),
        protocol: { version: proto.PROTOCOL_VERSION, min: proto.MIN_PROTOCOL_VERSION },
      });

      // flush any wake that was queued while the host was offline
      const pendingWake = S.pendingWakes.get(deviceId);
      if (pendingWake && pendingWake.expires > Date.now()) {
        send(ws, {
          type: 'join-request',
          controllerId: pendingWake.controllerId,
          unattended: pendingWake.unattended,
        });
        S.pendingWakes.delete(deviceId);
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
        // What the controller says it is. The `peerMetaTrusted: false` flag is
        // not decoration — it is the contract. The broker relays this without
        // verifying it, and a compromised broker could fabricate it outright, so
        // a host must treat it as a display hint and nothing more. The host's
        // own grant is what decides whether anything may happen.
        peerMeta: S.getMeta(ws.deviceId),
        peerMetaTrusted: false,
        // The permissions the host itself registered for this controller,
        // echoed back so the host can show them without re-deriving. Still not
        // authorization: the host re-checks against its local grant before it
        // executes anything.
        permissions: grant ? proto.normalizePermissions(grant) : [],
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
      send(ws, {
        type: 'join-pending',
        hostId,
        unattended,
        peerMeta: S.getMeta(hostId),
        peerMetaTrusted: false,
        permissions: grant ? proto.normalizePermissions(grant) : [],
      });
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
      const g = m.grant;
      if (!g || typeof g !== 'object' || typeof g.grantId !== 'string' || typeof g.controllerId !== 'string') {
        return send(ws, { type: 'grant-failed', reason: 'malformed-grant' });
      }
      if (!S.grants.has(ws.deviceId)) S.grants.set(ws.deviceId, new Map());
      // Store the grant with its permissions normalized, so a legacy scope-only
      // grant and a v1 permissions grant are indistinguishable to every later
      // reader. The original fields are preserved for the host that owns it.
      const permissions = proto.normalizePermissions(g);
      S.grants.get(ws.deviceId).set(g.grantId, { ...g, permissions });
      send(ws, { type: 'grant-registered', grantId: g.grantId, permissions });
      break;
    }
    case 'revoke-grant': {
      S.grants.get(ws.deviceId)?.delete(m.grantId);
      break;
    }

    // ---- pairing graph ----
    // A device may only add or remove pairing edges that involve itself.
    //
    // Without this check any authenticated device could insert an edge between
    // two identities it has nothing to do with, and `join` honours that edge for
    // paired-direct dialling — so a third party could unilaterally make A
    // dialable by B. It could equally delete other people's edges. Both fields
    // are named `*Pub` but carry deviceIds, which is what made the omission easy
    // to miss.
    //
    // The shipped Android client already sends its own deviceId as `myPub`
    // (PairingManager.confirm), so this rejects nothing that works today.
    case 'register-pairing': {
      if (m.myPub !== ws.deviceId) {
        return send(ws, { type: 'pairing-failed', reason: 'identity-mismatch' });
      }
      if (typeof m.peerPub !== 'string' || !/^[0-9a-f]{64}$/.test(m.peerPub)) {
        return send(ws, { type: 'pairing-failed', reason: 'malformed-peer' });
      }
      S.linkPair(m.myPub, m.peerPub);
      S.linkPair(m.peerPub, m.myPub);
      send(ws, { type: 'pairing-registered', peerPub: m.peerPub });
      break;
    }
    case 'revoke-pairing': {
      if (m.myPub !== ws.deviceId) {
        return send(ws, { type: 'pairing-failed', reason: 'identity-mismatch' });
      }
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
