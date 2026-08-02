'use strict';

// End-to-end smoke test of the signaling broker: spins up the server, connects
// a "host" and a "controller", and exercises pairing, direct dial, join-request,
// consent, and offer/answer/ice relay. No Android/WebRTC involved — pure protocol.

const WebSocket = require('ws');
const { start } = require('../src/server');

const PORT = process.env.PORT || 8080;
const URL = `ws://localhost:${PORT}`;

const HOST = 'host-pub-AAA';
const CTRL = 'ctrl-pub-BBB';

function open(deviceId) {
  return new Promise((resolve) => {
    const ws = new WebSocket(URL);
    ws.on('open', () => ws.send(JSON.stringify({ type: 'register', deviceId })));
    ws.on('message', (raw) => {
      const m = JSON.parse(raw.toString());
      if (m.type === 'registered') resolve({ ws, first: m });
    });
  });
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

  const host = await open(HOST);
  const ctrl = await open(CTRL);
  check('both devices register', host.first.deviceId === HOST && ctrl.first.deviceId === CTRL);
  check('register returns iceServers', Array.isArray(host.first.iceServers) && host.first.iceServers.length >= 1);

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

  console.log(`\n${passed} passed, ${failed} failed`);
  host.ws.close(); ctrl.ws.close(); server.close();
  process.exit(failed === 0 ? 0 : 1);
})();
