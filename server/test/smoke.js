'use strict';

// End-to-end smoke test of the signaling broker: spins up the server, connects
// a "host" and a "controller", and exercises registration authentication,
// pairing, direct dial, join-request, consent, and offer/answer/ice relay.
// No Android/WebRTC involved — pure protocol.

// Must be set before ../src/config is loaded. Short enough that the expiry test
// does not stall, long enough that a localhost handshake never races it.
process.env.REGISTER_CHALLENGE_TTL_MS = process.env.REGISTER_CHALLENGE_TTL_MS || '1000';

const crypto = require('crypto');
const WebSocket = require('ws');
const { start } = require('../src/server');
const auth = require('../src/auth');
const proto = require('../src/protocol');

const PORT = process.env.PORT || 8080;
const URL = `ws://localhost:${PORT}`;

/** A device identity: P-256 keypair whose deviceId is the hash of its public key. */
function makeIdentity() {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const der = publicKey.export({ format: 'der', type: 'spki' });
  return {
    der,
    publicKeyB64: der.toString('base64'),
    privateKey,
    deviceId: auth.deviceIdFor(der),
  };
}

const hostId = makeIdentity();
const ctrlId = makeIdentity();
const HOST = hostId.deviceId;
const CTRL = ctrlId.deviceId;

const signWith = (privateKey, bytes) => crypto.sign('sha256', bytes, privateKey).toString('base64');

/** Raw socket that has not registered. */
function connect() {
  return new Promise((resolve) => {
    const ws = new WebSocket(URL);
    ws.on('open', () => resolve(ws));
  });
}

/**
 * Full authenticated registration. `signingKey` defaults to the identity's own
 * private key; the adversarial tests pass a different one.
 */
async function open(identity, signingKey = identity.privateKey, meta = undefined) {
  const ws = await connect();
  send(ws, { type: 'register', deviceId: identity.deviceId, publicKey: identity.publicKeyB64, meta });
  const chal = await next(ws, null);
  if (chal.type !== 'register-challenge') return { ws, failure: chal };

  const transcript = auth.registrationTranscript(identity.deviceId, chal.challenge);
  send(ws, {
    type: 'register-proof',
    deviceId: identity.deviceId,
    signature: signWith(signingKey, transcript),
  });
  const result = await next(ws, null);
  if (result.type !== 'registered') return { ws, failure: result, challenge: chal.challenge };
  return { ws, first: result, challenge: chal.challenge };
}
function next(ws, type) {
  return new Promise((resolve) => {
    const on = (raw) => {
      const m = JSON.parse(raw.toString());
      if (!type || m.type === type) { ws.off('message', on); resolve(m); }
    };
    ws.on('message', on);
  });
}
const send = (ws, o) => ws.send(JSON.stringify(o));

let passed = 0, failed = 0;
function check(name, cond) {
  if (cond) { passed++; console.log(`  ✓ ${name}`); }
  else { failed++; console.log(`  ✗ ${name}`); }
}

(async () => {
  const server = start();
  await new Promise((r) => setTimeout(r, 200));

  const host = await open(hostId);
  let ctrl = await open(ctrlId);

  // ================= REGISTRATION AUTHENTICATION (HIGH #1) =================
  console.log('\n  -- registration authentication --');

  // 1. legitimate registration
  check('1. legitimate registration succeeds', host.first.deviceId === HOST && ctrl.first.deviceId === CTRL);
  check('register returns iceServers', Array.isArray(host.first.iceServers) && host.first.iceServers.length >= 1);

  // 2. wrong private key: correct identity claimed, signature from another key
  {
    const impostorKey = makeIdentity().privateKey;
    const attempt = await open(hostId, impostorKey);
    check('2. wrong private key rejected',
      !attempt.first && attempt.failure.type === 'register-failed' && attempt.failure.reason === 'bad-signature');
    attempt.ws.close();
  }

  // 3. replayed challenge: a nonce captured from one connection is useless on another
  {
    const ws = await connect();
    send(ws, { type: 'register', deviceId: HOST, publicKey: hostId.publicKeyB64 });
    const chal = await next(ws, 'register-challenge');
    // Sign the *other* connection's earlier challenge instead of this one.
    const stale = auth.registrationTranscript(HOST, host.challenge);
    send(ws, { type: 'register-proof', deviceId: HOST, signature: signWith(hostId.privateKey, stale) });
    const res = await next(ws, null);
    check('3. replayed challenge rejected',
      res.type === 'register-failed' && res.reason === 'bad-signature' && chal.challenge !== host.challenge);
    ws.close();
  }

  // 4. expired challenge
  {
    const ws = await connect();
    send(ws, { type: 'register', deviceId: HOST, publicKey: hostId.publicKeyB64 });
    const chal = await next(ws, 'register-challenge');
    await new Promise((r) => setTimeout(r, Number(process.env.REGISTER_CHALLENGE_TTL_MS) + 250));
    const transcript = auth.registrationTranscript(HOST, chal.challenge);
    send(ws, { type: 'register-proof', deviceId: HOST, signature: signWith(hostId.privateKey, transcript) });
    const res = await next(ws, null);
    check('4. expired challenge rejected',
      res.type === 'register-failed' && res.reason === 'challenge-expired');
    ws.close();
  }

  // 5. reused challenge: a challenge is single-use even after a success.
  //    Uses a throwaway identity so it does not displace the controller's
  //    registration, which case 7 still needs.
  {
    const spare = makeIdentity();
    const ws = await connect();
    send(ws, { type: 'register', deviceId: spare.deviceId, publicKey: spare.publicKeyB64 });
    const chal = await next(ws, 'register-challenge');
    const transcript = auth.registrationTranscript(spare.deviceId, chal.challenge);
    const sig = signWith(spare.privateKey, transcript);

    send(ws, { type: 'register-proof', deviceId: spare.deviceId, signature: sig });
    const first = await next(ws, null);
    send(ws, { type: 'register-proof', deviceId: spare.deviceId, signature: sig });
    const second = await next(ws, null);
    check('5. reused challenge rejected',
      first.type === 'registered' && second.type === 'register-failed' && second.reason === 'no-challenge');
    ws.close();
  }

  // 6. deviceId takeover: both variants an attacker would actually try
  {
    const attacker = makeIdentity();

    // 6a. victim's deviceId with the attacker's own key
    const ws1 = await connect();
    send(ws1, { type: 'register', deviceId: HOST, publicKey: attacker.publicKeyB64 });
    const r1 = await next(ws1, null);
    check('6a. takeover with attacker key rejected (identity-mismatch)',
      r1.type === 'register-failed' && r1.reason === 'identity-mismatch');
    ws1.close();

    // 6b. victim's deviceId AND victim's public key — both are public values.
    //     Only the private key, which the attacker lacks, can finish this.
    const ws2 = await connect();
    send(ws2, { type: 'register', deviceId: HOST, publicKey: hostId.publicKeyB64 });
    const chal = await next(ws2, 'register-challenge');
    const transcript = auth.registrationTranscript(HOST, chal.challenge);
    send(ws2, { type: 'register-proof', deviceId: HOST, signature: signWith(attacker.privateKey, transcript) });
    const r2 = await next(ws2, null);
    check('6b. takeover with victim public key rejected (bad-signature)',
      r2.type === 'register-failed' && r2.reason === 'bad-signature');
    ws2.close();
  }

  // 6c. an unauthenticated socket cannot use the broker at all
  {
    const ws = await connect();
    send(ws, { type: 'join', hostId: HOST });
    const res = await next(ws, null);
    check('6c. unauthenticated message rejected',
      res.type === 'not-authenticated' && res.rejected === 'join');
    ws.close();
  }

  // 6d. cross-language transcript contract. The Android client builds this same
  //     string (RegistrationAuthTest pins the identical literal). If the two
  //     drift, no real device can register.
  check('6d. registration transcript matches the Android wire format',
    auth.registrationTranscript('device-1', 'nonce-1').toString('utf8')
      === 'techee-register-v1\ndevice-1\nnonce-1');

  // 7. legitimate reconnect displaces the old socket, explicitly
  {
    const replacedP = next(ctrl.ws, 'session-replaced');
    const reconnected = await open(ctrlId);
    const replaced = await replacedP;
    check('7. legitimate reconnect replaces prior registration',
      reconnected.first && reconnected.first.deviceId === CTRL && replaced.reason === 'authenticated-reconnect');
    ctrl.ws.close();
    ctrl = reconnected; // continue the protocol tests on the live socket
  }

  console.log('\n  -- protocol relay --');

  // --- pairing graph ---
  send(ctrl.ws, { type: 'register-pairing', myPub: CTRL, peerPub: HOST });
  const paired = await next(ctrl.ws, 'pairing-registered');
  check('pairing registered', paired.peerPub === HOST);

  // --- direct dial to paired host produces a join-request on the host ---
  const jrP = next(host.ws, 'join-request');
  send(ctrl.ws, { type: 'join', hostId: HOST });
  const jr = await jrP;
  check('host receives join-request', jr.controllerId === CTRL);
  check('join not unattended (no grant yet)', jr.unattended === false);

  // --- register an unattended grant, dial again -> unattended:true ---
  send(host.ws, {
    type: 'register-grant',
    grant: { grantId: 'g1', controllerId: CTRL, active: true, expiresAt: null },
  });
  await next(host.ws, 'grant-registered');
  const jr2P = next(host.ws, 'join-request');
  send(ctrl.ws, { type: 'join', hostId: HOST });
  const jr2 = await jr2P;
  check('join now unattended (grant present)', jr2.unattended === true && jr2.grantId === 'g1');

  // --- consent relay host -> controller ---
  const consentP = next(ctrl.ws, 'consent');
  send(host.ws, { type: 'consent', controllerId: CTRL, accepted: true });
  const consent = await consentP;
  check('controller receives consent', consent.accepted === true && consent.hostId === HOST);

  // --- offer/answer/ice relay with from-stamping ---
  const offerP = next(ctrl.ws, 'offer');
  send(host.ws, { type: 'offer', to: CTRL, sdp: 'SDP_OFFER' });
  const offer = await offerP;
  check('offer relayed with from stamp', offer.sdp === 'SDP_OFFER' && offer.from === HOST);

  const iceP = next(host.ws, 'ice');
  send(ctrl.ws, { type: 'ice', to: HOST, mid: '0', index: 0, cand: 'candidate:...' });
  const ice = await iceP;
  check('ice relayed', ice.cand === 'candidate:...' && ice.from === CTRL);

  // --- code-based join path ---
  const codeP = next(host.ws, 'session-code');
  send(host.ws, { type: 'host-open' });
  const codeMsg = await codeP;
  check('host-open returns 6-digit code', /^\d{6}$/.test(codeMsg.code));

  const jr3P = next(host.ws, 'join-request');
  send(ctrl.ws, { type: 'join', code: codeMsg.code });
  const jr3 = await jr3P;
  check('code join reaches host', jr3.controllerId === CTRL);

  // --- invalid code rejected ---
  const failP = next(ctrl.ws, 'join-failed');
  send(ctrl.ws, { type: 'join', code: '000000' });
  const fail = await failP;
  check('invalid code rejected', fail.reason === 'invalid-code');

  // ================= CROSS-PLATFORM ENDPOINTS (W1) =================
  // Everything above is the Android-era protocol and must keep passing
  // untouched. Everything below is what a Windows endpoint adds.
  console.log('\n  -- cross-platform endpoints --');

  // A Windows host registers with platform + capability metadata.
  const winId = makeIdentity();
  const WIN = winId.deviceId;
  const winMeta = {
    platform: 'windows',
    version: '0.1.0',
    capabilities: ['screen.share', 'input.receive', 'clipboard', 'display.multi', 'power.sleep', 'power.restart'],
  };
  const win = await open(winId, winId.privateKey, winMeta);
  check('windows endpoint registers', win.first && win.first.deviceId === WIN);
  check('registered advertises the protocol version',
    win.first.protocol && win.first.protocol.version === proto.PROTOCOL_VERSION
      && win.first.protocol.min === proto.MIN_PROTOCOL_VERSION);

  // A legacy Android client sends no meta and must be unaffected.
  check('legacy registration without meta still succeeds', host.first.deviceId === HOST);

  // --- capability metadata reaches the peer, flagged as untrusted ---
  {
    send(win.ws, { type: 'register-pairing', myPub: WIN, peerPub: CTRL });
    await next(win.ws, 'pairing-registered');

    const jrP = next(win.ws, 'join-request');
    const jpP = next(ctrl.ws, 'join-pending');
    send(ctrl.ws, { type: 'join', hostId: WIN });
    const [jr, jp] = [await jrP, await jpP];

    check('controller learns the windows host platform',
      jp.peerMeta && jp.peerMeta.platform === 'windows' && jp.peerMeta.version === '0.1.0');
    check('capabilities arrive sorted and filtered',
      JSON.stringify(jp.peerMeta.capabilities)
        === JSON.stringify(['clipboard', 'display.multi', 'input.receive', 'power.restart', 'power.sleep', 'screen.share']));
    check('peer metadata is explicitly marked untrusted', jp.peerMetaTrusted === false);
    check('the windows host sees the controller identity', jr.controllerId === CTRL);
    check('a legacy peer that sent no meta reports null rather than a fabricated one',
      jr.peerMeta === null || jr.peerMeta === undefined);
  }

  // --- unknown capabilities are dropped, not rejected: a NEWER client connects ---
  {
    const future = makeIdentity();
    const f = await open(future, future.privateKey, {
      platform: 'windows',
      version: '99.0.0',
      capabilities: ['screen.share', 'holodeck.project', 'input.receive'],
    });
    check('a client advertising unknown capabilities still registers', !!f.first);

    send(f.ws, { type: 'register-pairing', myPub: future.deviceId, peerPub: CTRL });
    await next(f.ws, 'pairing-registered');
    const jpP = next(ctrl.ws, 'join-pending');
    send(ctrl.ws, { type: 'join', hostId: future.deviceId });
    const jp = await jpP;
    check('unknown capability tokens are filtered out, known ones survive',
      JSON.stringify(jp.peerMeta.capabilities) === JSON.stringify(['input.receive', 'screen.share']));
    f.ws.close();
  }

  // --- a malformed / hostile meta must not register metadata or break the socket ---
  {
    const bad = makeIdentity();
    const b = await open(bad, bad.privateKey, { platform: 'toaster', version: '1.0', capabilities: [] });
    check('an unknown platform registers but advertises nothing', !!b.first);

    send(b.ws, { type: 'register-pairing', myPub: bad.deviceId, peerPub: CTRL });
    await next(b.ws, 'pairing-registered');
    const jpP = next(ctrl.ws, 'join-pending');
    send(ctrl.ws, { type: 'join', hostId: bad.deviceId });
    const jp = await jpP;
    check('a rejected meta yields null, never a partially-parsed one', jp.peerMeta === null);
    b.ws.close();
  }

  // --- permission-scoped grants ---
  {
    const gP = next(win.ws, 'grant-registered');
    send(win.ws, {
      type: 'register-grant',
      grant: {
        grantId: 'w1',
        controllerId: CTRL,
        active: true,
        expiresAt: null,
        permissions: ['screen.view', 'input.control', 'system.sleep'],
      },
    });
    const g = await gP;
    check('a permission-scoped grant is accepted',
      g.grantId === 'w1'
        && JSON.stringify(g.permissions) === JSON.stringify(['input.control', 'screen.view', 'system.sleep']));

    const jrP = next(win.ws, 'join-request');
    send(ctrl.ws, { type: 'join', hostId: WIN });
    const jr = await jrP;
    check('the grant makes the join unattended', jr.unattended === true && jr.grantId === 'w1');
    check('permissions are carried to the host',
      JSON.stringify(jr.permissions) === JSON.stringify(['input.control', 'screen.view', 'system.sleep']));
    check('a sleep grant does NOT confer shutdown',
      !jr.permissions.includes('system.shutdown') && !jr.permissions.includes('system.restart'));
  }

  // --- a legacy scope-only grant confers no system power ---
  {
    const legacyHost = makeIdentity();
    const lh = await open(legacyHost);
    send(lh.ws, { type: 'register-pairing', myPub: legacyHost.deviceId, peerPub: CTRL });
    await next(lh.ws, 'pairing-registered');

    const gP = next(lh.ws, 'grant-registered');
    send(lh.ws, {
      type: 'register-grant',
      grant: { grantId: 'legacy1', controllerId: CTRL, active: true, scope: ['VIEW', 'CONTROL'] },
    });
    const g = await gP;
    check('a legacy scope grant is widened to permissions',
      JSON.stringify(g.permissions) === JSON.stringify(['input.control', 'screen.view']));
    check('a legacy scope grant confers no system.* permission',
      !g.permissions.some((p) => p.startsWith('system.')));
    lh.ws.close();
  }

  // --- an expired grant does not confer unattended access ---
  //
  // `expiresAt: 0` is the epoch, i.e. maximally expired. The broker used to gate
  // this on `g.expiresAt && ...`, a truthiness guard that read 0 as
  // never-expiring and handed the controller an unattended session. Pinned here
  // because the fail-open was invisible: every other expiry value behaved.
  {
    const expiredHost = makeIdentity();
    const eh = await open(expiredHost);
    send(eh.ws, { type: 'register-pairing', myPub: expiredHost.deviceId, peerPub: CTRL });
    await next(eh.ws, 'pairing-registered');

    for (const [name, expiresAt] of [['at the epoch', 0], ['in the past', 1000]]) {
      const gP = next(eh.ws, 'grant-registered');
      send(eh.ws, {
        type: 'register-grant',
        grant: {
          grantId: `exp-${expiresAt}`,
          controllerId: CTRL,
          active: true,
          expiresAt,
          permissions: ['screen.view', 'input.control'],
        },
      });
      await gP;

      const jrP = next(eh.ws, 'join-request');
      send(ctrl.ws, { type: 'join', hostId: expiredHost.deviceId });
      const jr = await jrP;
      check(`a grant that expired ${name} does not make the join unattended`,
        jr.unattended === false && jr.grantId === undefined);
      check(`a grant that expired ${name} confers no permissions`,
        Array.isArray(jr.permissions) && jr.permissions.length === 0);

      send(eh.ws, { type: 'revoke-grant', grantId: `exp-${expiresAt}` });
    }
    eh.ws.close();
  }

  // --- a malformed grant is refused rather than stored or thrown ---
  {
    const failP = next(win.ws, 'grant-failed');
    send(win.ws, { type: 'register-grant', grant: { controllerId: CTRL, active: true } });
    const f = await failP;
    check('a grant with no grantId is refused', f.reason === 'malformed-grant');
  }

  // ================= PAIRING-GRAPH AUTHORIZATION =================
  console.log('\n  -- pairing-graph authorization --');

  // An authenticated device must not be able to pair two identities that are
  // nothing to do with it, nor tear down someone else's pairing.
  {
    const meddler = makeIdentity();
    const md = await open(meddler);

    const failP = next(md.ws, 'pairing-failed');
    send(md.ws, { type: 'register-pairing', myPub: HOST, peerPub: WIN });
    const f = await failP;
    check('a third party cannot forge a pairing between two other devices',
      f.reason === 'identity-mismatch');

    // ...and the forged edge really is absent: HOST cannot dial WIN.
    const jfP = next(host.ws, 'join-failed');
    send(host.ws, { type: 'join', hostId: WIN });
    const jf = await jfP;
    check('the forged pairing did not take effect', jf.reason === 'not-paired');

    // Revocation is identity-bound too.
    const failP2 = next(md.ws, 'pairing-failed');
    send(md.ws, { type: 'revoke-pairing', myPub: CTRL, peerPub: WIN });
    const f2 = await failP2;
    check('a third party cannot revoke someone else\'s pairing', f2.reason === 'identity-mismatch');

    // ...and the real pairing survived the attempt.
    const jpP = next(ctrl.ws, 'join-pending');
    send(ctrl.ws, { type: 'join', hostId: WIN });
    const jp = await jpP;
    check('the targeted pairing survived the revocation attempt', jp.hostId === WIN);

    // A malformed peer id is refused rather than inserted as a dangling edge.
    const failP3 = next(md.ws, 'pairing-failed');
    send(md.ws, { type: 'register-pairing', myPub: meddler.deviceId, peerPub: 'not-a-device-id' });
    const f3 = await failP3;
    check('a malformed peer id is refused', f3.reason === 'malformed-peer');

    md.ws.close();
  }

  // --- metadata is retracted when the endpoint disconnects ---
  {
    const gone = makeIdentity();
    const g = await open(gone, gone.privateKey, { platform: 'windows', version: '0.1.0', capabilities: ['clipboard'] });
    send(g.ws, { type: 'register-pairing', myPub: gone.deviceId, peerPub: CTRL });
    await next(g.ws, 'pairing-registered');
    g.ws.close();
    await new Promise((r) => setTimeout(r, 150));

    const jpP = next(ctrl.ws, 'join-pending');
    send(ctrl.ws, { type: 'join', hostId: gone.deviceId });
    const jp = await jpP;
    check('a disconnected endpoint no longer advertises capabilities', !jp.peerMeta);
  }

  console.log(`\n${passed} passed, ${failed} failed`);
  host.ws.close(); ctrl.ws.close(); win.ws.close(); server.close();
  process.exit(failed === 0 ? 0 : 1);
})();
