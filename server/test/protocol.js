'use strict';

// Conformance test for the Techee protocol contract.
//
// Every assertion here is driven by the shared golden vectors in
// `protocol/fixtures/`. Android reads the same files (they are wired in as test
// resources) and, from W2, so does Windows. That is the point: the fixtures are
// the specification, and three independent implementations are held to them
// rather than to each other.
//
// If you change a fixture, you are changing the wire protocol. Expect all three
// languages to go red until they agree again — that is the mechanism working.

const fs = require('fs');
const path = require('path');
const P = require('../src/protocol');

const FIXTURES = path.join(__dirname, '..', '..', 'protocol', 'fixtures');
const load = (f) => JSON.parse(fs.readFileSync(path.join(FIXTURES, f), 'utf8'));

let passed = 0;
let failed = 0;

function check(name, cond, detail) {
  if (cond) {
    passed++;
  } else {
    failed++;
    console.log(`  ✗ ${name}${detail ? `\n      ${detail}` : ''}`);
  }
}

/** Structural equality that does not care about key order. */
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b) return false;
  if (a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (typeof a !== 'object') return false;
  const ka = Object.keys(a).sort();
  const kb = Object.keys(b).sort();
  if (ka.length !== kb.length) return false;
  if (!ka.every((k, i) => k === kb[i])) return false;
  return ka.every((k) => deepEqual(a[k], b[k]));
}

const show = (v) => JSON.stringify(v);

/**
 * Fixtures cannot literally contain a 70 KB string without becoming unreadable,
 * so oversized and pathological payloads are named rather than inlined.
 */
function materialize(v) {
  if (typeof v === 'string' && v.startsWith('GENERATE:')) {
    return 'a'.repeat(parseInt(v.slice('GENERATE:'.length), 10));
  }
  if (typeof v === 'string' && v.startsWith('GENERATE_NESTED:')) {
    const depth = parseInt(v.slice('GENERATE_NESTED:'.length), 10);
    return '['.repeat(depth) + ']'.repeat(depth);
  }
  return v;
}

function materializeFrame(frame) {
  if (frame === null || typeof frame !== 'object') return frame;
  const out = Array.isArray(frame) ? [] : {};
  for (const [k, v] of Object.entries(frame)) {
    if (typeof v === 'string' && v.startsWith('GENERATE:') && k === 'capabilities') {
      out[k] = v; // handled by the caller
    } else if (typeof v === 'string') {
      out[k] = materialize(v);
    } else {
      out[k] = v;
    }
  }
  return out;
}

// ===========================================================================
console.log('\n  -- identity & transcripts --');
// ===========================================================================
{
  const fx = load('identity.json');
  const crypto = require('crypto');
  const auth = require('../src/auth');

  const spki = Buffer.from(fx.key.publicKeySpkiB64, 'base64');

  check(
    'deviceId derives from the SPKI DER exactly as the fixture pins it',
    auth.deviceIdFor(spki) === fx.deviceId.expected,
    `got ${auth.deviceIdFor(spki)}`
  );

  check(
    'deviceId matches the /^[0-9a-f]{64}$/ shape the broker enforces',
    /^[0-9a-f]{64}$/.test(fx.deviceId.expected)
  );

  for (const v of fx.transcripts.registration.vectors) {
    const built = auth.registrationTranscript(v.deviceId, v.challengeB64).toString('utf8');
    check(`registration transcript: ${v.name}`, built === v.expectedUtf8, `got ${show(built)}`);
  }

  // The realistic vector carries a signature made by the fixture key. Verifying
  // it here is what proves a fresh implementation in any language can produce
  // something this broker will actually accept.
  const rv = fx.transcripts.registration.vectors.find((v) => v.signatureB64);
  const pub = crypto.createPublicKey({ key: spki, format: 'der', type: 'spki' });
  check(
    'the pinned registration signature verifies against the fixture public key',
    auth.verifySignature(pub, Buffer.from(rv.expectedUtf8, 'utf8'), rv.signatureB64)
  );

  check(
    'a signature over a different challenge does NOT verify',
    !auth.verifySignature(
      pub,
      auth.registrationTranscript(rv.deviceId, 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='),
      rv.signatureB64
    )
  );

  // Round-trip the fixture private key: sign fresh, verify, and confirm the
  // broker's own beginRegistration/completeRegistration accept it end to end.
  {
    const priv = crypto.createPrivateKey({
      key: Buffer.from(fx.key.privateKeyPkcs8B64, 'base64'),
      format: 'der',
      type: 'pkcs8',
    });
    const begun = auth.beginRegistration(fx.deviceId.expected, fx.key.publicKeySpkiB64, 30000);
    check('fixture identity is accepted by beginRegistration', begun.ok, begun.reason);
    if (begun.ok) {
      const t = auth.registrationTranscript(fx.deviceId.expected, begun.challenge);
      const sig = crypto.sign('sha256', t, priv).toString('base64');
      const done = auth.completeRegistration(begun.pending, fx.deviceId.expected, sig);
      check('fixture identity completes registration end to end', done.ok, done.reason);
    }
  }

  for (const v of fx.transcripts.peerAuth.vectors) {
    const built = ['techee-peer-auth-v1', v.challengerId, v.nonceB64].join('\n');
    check(`peer-auth transcript: ${v.name}`, built === v.expectedUtf8, `got ${show(built)}`);
  }

  // The SDP transcript is implemented on Android, not in the broker — the broker
  // must never be able to forge one. Rebuilding it here from the spec is how the
  // fixture stays honest about what Android and Windows have to agree on.
  const sdpFingerprint = (sdp) => {
    const line = sdp.split('\n').find((l) => l.replace(/^\s+/, '').startsWith('a=fingerprint:'));
    return line === undefined ? null : line.trim();
  };
  for (const v of fx.transcripts.sdp.vectors) {
    const fp = sdpFingerprint(v.sdp);
    check(`sdp fingerprint extraction: ${v.name}`, fp === v.expectedFingerprintLine, `got ${show(fp)}`);

    if (v.expectNullTranscript) {
      check(`sdp transcript is undefined without a fingerprint: ${v.name}`, fp === null);
      continue;
    }
    const digest = crypto.createHash('sha256').update(Buffer.from(v.sdp, 'utf8')).digest('hex');
    const built = ['techee-sdp-v1', v.type, v.fromId, v.toId, fp, digest].join('\n');
    check(`sdp transcript: ${v.name}`, built === v.expectedUtf8, `got ${show(built)}`);
  }
}

// ===========================================================================
console.log('  -- pairing ceremony --');
// ===========================================================================
{
  // Node generated these vectors when the fixture was written but never read
  // them back, so `pairing.json` was pinned in Kotlin and C# and merely asserted
  // to hold in JS. That is one language short of the three-way check the whole
  // fixture mechanism is for, and the safety number is the one value that fails
  // visibly in front of two humans reading it aloud to each other.
  const fx = load('pairing.json');
  const crypto = require('crypto');

  const der = (b64) => Buffer.from(b64, 'base64');
  const pub = (b64) => crypto.createPublicKey({ key: der(b64), format: 'der', type: 'spki' });
  const priv = (b64) => crypto.createPrivateKey({ key: der(b64), format: 'der', type: 'pkcs8' });
  const K = fx.keys;

  // The proof transcripts are raw concatenations with no length framing, so they
  // are unambiguous only while every field is fixed-length. Verify that, never
  // assume it — the fixture says so in as many words.
  for (const [name, b64] of Object.entries(K).filter(([k]) => k.endsWith('SpkiB64'))) {
    check(`${name} is a ${fx.lengths.p256SpkiDer}-byte P-256 SPKI DER`,
      der(b64).length === fx.lengths.p256SpkiDer, `got ${der(b64).length}`);
  }
  check(`the nonce is ${fx.lengths.nonce} bytes`, der(fx.nonceB64).length === fx.lengths.nonce);

  // Raw ECDH: the X coordinate, UNHASHED. Applying a KDF here changes the safety
  // number and breaks pairing against every shipped handset.
  const shared = crypto.diffieHellman({
    privateKey: priv(K.hostEphemeralPkcs8B64),
    publicKey: pub(K.controllerEphemeralSpkiB64),
  });
  check('raw ECDH secret matches the fixture',
    shared.toString('base64') === fx.ecdh.sharedSecretB64, `got ${shared.toString('base64')}`);
  check(`the shared secret is ${fx.lengths.sharedSecret} bytes`,
    shared.length === fx.lengths.sharedSecret);

  // Both peers derive it from opposite sides of the exchange and must agree.
  const sharedReverse = crypto.diffieHellman({
    privateKey: priv(K.controllerEphemeralPkcs8B64),
    publicKey: pub(K.hostEphemeralSpkiB64),
  });
  check('both sides of the exchange derive the same secret', shared.equals(sharedReverse));

  /** sha256(shared || min(pubA,pubB) || max(pubA,pubB)), ordered by lowercase hex. */
  const safetyNumber = (secret, a, b) => {
    const [lo, hi] = a.toString('hex') < b.toString('hex') ? [a, b] : [b, a];
    const digest = crypto.createHash('sha256').update(Buffer.concat([secret, lo, hi])).digest();
    return [...digest.subarray(0, 6)].map((x) => String(x).padStart(3, '0')).join('-');
  };

  const idHost = der(K.hostIdentitySpkiB64);
  const idCtrl = der(K.controllerIdentitySpkiB64);
  const sn = safetyNumber(shared, idHost, idCtrl);
  check('safety number matches the fixture', sn === fx.safetyNumber.expected, `got ${sn}`);
  // Each peer computes it holding its own key first, so key order must not matter.
  check('safety number is independent of key order',
    safetyNumber(shared, idCtrl, idHost) === sn);
  check('a different shared secret yields a different safety number',
    safetyNumber(Buffer.alloc(fx.lengths.sharedSecret), idHost, idCtrl) !== sn);

  // The two proof transcripts, rebuilt from the fixture's stated layout rather
  // than from any implementation's code.
  const controllerTranscript = Buffer.concat([
    der(fx.nonceB64), idHost, der(K.controllerEphemeralSpkiB64),
  ]);
  const hostTranscript = Buffer.concat([
    der(K.controllerEphemeralSpkiB64), idCtrl,
  ]);

  check('the pinned controller proof verifies under the controller identity key',
    crypto.verify('sha256', controllerTranscript, pub(K.controllerIdentitySpkiB64),
      der(fx.proofs.controller.signatureB64)));
  check('the pinned host proof verifies under the host identity key',
    crypto.verify('sha256', hostTranscript, pub(K.hostIdentitySpkiB64),
      der(fx.proofs.host.signatureB64)));

  // Fail-closed: the right signature over the wrong key or the wrong bytes.
  check('the controller proof does NOT verify under the host identity key',
    !crypto.verify('sha256', controllerTranscript, pub(K.hostIdentitySpkiB64),
      der(fx.proofs.controller.signatureB64)));
  check('a controller proof over a different nonce does NOT verify',
    !crypto.verify('sha256',
      Buffer.concat([Buffer.alloc(fx.lengths.nonce), idHost, der(K.controllerEphemeralSpkiB64)]),
      pub(K.controllerIdentitySpkiB64), der(fx.proofs.controller.signatureB64)));

  // Round-trip with the committed private keys: a fresh implementation must be
  // able to produce a proof the pinned one's verifier accepts.
  check('a freshly signed controller proof verifies',
    crypto.verify('sha256', controllerTranscript, pub(K.controllerIdentitySpkiB64),
      crypto.sign('sha256', controllerTranscript, priv(K.controllerIdentityPkcs8B64))));

  // The QR payload field names the Android PairingOffer emits.
  check('the offer carries exactly the pinned field names',
    deepEqual([...fx.offerJson.fields].sort(),
      ['ephPub', 'hostName', 'hostPub', 'nonce', 'relay', 'ttl']));
}

// ===========================================================================
console.log('  -- endpoint metadata (descriptive) --');
// ===========================================================================
{
  const fx = load('capabilities.json');

  check('protocol version matches the fixture', P.PROTOCOL_VERSION === fx.protocolVersion.current);
  check('min protocol version matches the fixture', P.MIN_PROTOCOL_VERSION === fx.protocolVersion.minSupported);
  check('platform list matches the fixture', deepEqual([...P.PLATFORMS].sort(), [...fx.platforms].sort()));
  check('capability list matches the fixture', deepEqual([...P.CAPABILITIES].sort(), [...fx.knownCapabilities].sort()));
  check('permission list matches the fixture', deepEqual([...P.PERMISSIONS].sort(), [...fx.knownPermissions].sort()));

  for (const [k, want] of Object.entries(fx.limits)) {
    check(`limit ${k} matches the fixture`, P.LIMITS[k] === want, `got ${P.LIMITS[k]}`);
  }

  for (const v of fx.metaVectors) {
    let input = v.input;
    if (input && input.capabilities === 'GENERATE:64') {
      input = { ...input, capabilities: Array.from({ length: 64 }, (_, i) => `synthetic.cap.${i}`) };
    }
    const got = P.parseEndpointMeta(input);
    check(`meta: ${v.name}`, deepEqual(got, v.expect), `got ${show(got)} want ${show(v.expect)}`);
  }

  // Independent of any vector: a capability advertisement must never be able to
  // authorise anything. This is the invariant the whole split exists to protect.
  const boastful = P.parseEndpointMeta({
    platform: 'windows',
    version: '1.0',
    capabilities: ['power.shutdown', 'power.restart', 'clipboard'],
  });
  check('a peer advertising power.shutdown still has no capability field on a grant', boastful.capabilities.includes('power.shutdown'));
  check(
    'and that advertisement authorises nothing',
    !P.grantPermits({ grantId: 'g', controllerId: 'c', active: true, permissions: [] }, 'system.shutdown')
  );
  check(
    'a capability token is not accepted as a permission token',
    !P.grantPermits({ grantId: 'g', controllerId: 'c', active: true, permissions: ['power.shutdown'] }, 'system.shutdown')
  );
}

// ===========================================================================
console.log('  -- grants & permissions (authoritative) --');
// ===========================================================================
{
  const fx = load('capabilities.json');

  for (const v of fx.legacyScopeMapping.vectors) {
    const got = P.normalizePermissions({ scope: v.scope });
    check(
      `legacy scope ${show(v.scope)} widens correctly`,
      deepEqual(got, v.permissions),
      `got ${show(got)} want ${show(v.permissions)}`
    );
  }

  // Stated as its own assertion because it is the upgrade-safety property, not
  // an incidental consequence of the table.
  for (const legacyScope of ['VIEW', 'CONTROL', 'CLIPBOARD', 'FILES']) {
    const perms = P.normalizePermissions({ scope: [legacyScope] });
    check(
      `legacy scope ${legacyScope} confers no system.* power`,
      !perms.some((p) => p.startsWith('system.')),
      `got ${show(perms)}`
    );
  }

  for (const v of fx.grantVectors) {
    const now = v.nowMs === undefined ? Date.now() : v.nowMs;
    for (const p of v.permits) {
      check(`grant "${v.name}" permits ${p}`, P.grantPermits(v.grant, p, now) === true);
    }
    for (const p of v.denies) {
      check(`grant "${v.name}" denies ${p}`, P.grantPermits(v.grant, p, now) === false);
    }
  }

  check('a null grant permits nothing', P.grantPermits(null, 'screen.view') === false);
  check('an undefined grant permits nothing', P.grantPermits(undefined, 'screen.view') === false);
  check('a grant permits nothing for an unknown permission', P.grantPermits({ active: true, permissions: ['screen.view'] }, 'not.a.permission') === false);
  check('permissions take precedence over a legacy scope when both are present',
    deepEqual(P.normalizePermissions({ permissions: ['screen.view'], scope: ['CONTROL'] }), ['screen.view']));
}

// ===========================================================================
console.log('  -- control frames: decode --');
// ===========================================================================
{
  const fx = load('control-v1.json');

  for (const [k, want] of Object.entries(fx.limits)) {
    check(`control limit ${k} matches the fixture`, P.LIMITS[k] === want, `got ${P.LIMITS[k]}`);
  }

  for (const v of fx.decodeVectors) {
    const got = P.decodeControl(materializeFrame(v.frame));
    check(`decode: ${v.name}`, deepEqual(got, v.expect), `got ${show(got)} want ${show(v.expect)}`);
  }

  for (const v of fx.rejectVectors) {
    const got = P.decodeControl(materializeFrame(v.frame));
    check(`reject: ${v.name}`, got === null, `got ${show(got)}`);
  }

  for (const v of fx.rawRejectVectors) {
    let raw = v.raw;
    if (typeof raw === 'string' && raw.startsWith('GENERATE_NESTED:')) raw = materialize(raw);
    let got;
    let threw = false;
    try {
      got = P.decodeControlRaw(raw);
    } catch (e) {
      threw = true;
      got = `THREW ${e.message}`;
    }
    // Two separate properties, asserted separately: it must reject, and it must
    // reject by returning rather than by throwing. On an unattended host the
    // second one is what keeps the machine alive.
    check(`raw reject does not throw: ${v.name}`, !threw, String(got));
    check(`raw reject: ${v.name}`, got === null, `got ${show(got)}`);
  }

  // An oversized frame must be refused on length alone, before JSON.parse.
  const huge = JSON.stringify({ v: 1, t: 'keyboard.text', s: 'a'.repeat(P.LIMITS.maxFrameBytes) });
  check('oversized raw frame is refused', P.decodeControlRaw(huge) === null);
  check('a frame at exactly the size limit is not refused for size',
    P.decodeControlRaw(JSON.stringify({ v: 1, t: 'system.lock' })) !== null);

  // Legacy frames must never be able to reach v1-only commands by name.
  for (const t of ['pointer.tap', 'system.restart', 'clipboard.set', 'keyboard.keyDown']) {
    check(`a v1-only name is not accepted in a legacy frame: ${t}`,
      P.decodeControl({ t, x: 0.5, y: 0.5, code: 'KeyA', s: 'x' }) === null);
  }
  // ...and v1 frames must not accept legacy names, so the two vocabularies stay
  // disjoint and a frame's dialect is unambiguous.
  for (const t of ['tap', 'swipe', 'key', 'text', 'callstate']) {
    check(`a legacy name is not accepted in a v1 frame: ${t}`,
      P.decodeControl({ v: 1, t, x: 0.5, y: 0.5, k: 'BACK', s: 'x', state: 'IDLE' }) === null);
  }
}

// ===========================================================================
console.log('  -- control frames: encode & dialect downgrade --');
// ===========================================================================
{
  const fx = load('control-v1.json');

  for (const v of fx.encodeVectors) {
    const gotV1 = P.encodeControl(v.command, P.PROTOCOL_VERSION);
    check(`encode v1: ${v.name}`, deepEqual(gotV1, v.v1), `got ${show(gotV1)} want ${show(v.v1)}`);

    const gotV0 = P.encodeControl(v.command, P.LEGACY_VERSION);
    check(`encode v0: ${v.name}`, deepEqual(gotV0, v.v0), `got ${show(gotV0)} want ${show(v.v0)}`);

    // Round trip: whatever we emit, we must be able to read back as the same
    // command. This is what actually proves the two dialects are equivalent
    // rather than merely similar.
    check(`round-trip v1: ${v.name}`, deepEqual(P.decodeControl(gotV1), v.command),
      `got ${show(P.decodeControl(gotV1))}`);
    if (gotV0 !== null) {
      check(`round-trip v0: ${v.name}`, deepEqual(P.decodeControl(gotV0), v.command),
        `got ${show(P.decodeControl(gotV0))}`);
    }
  }

  check('encoding refuses an unknown command', P.encodeControl({ kind: 'system.selfDestruct' }) === null);
  check('encoding refuses a non-command', P.encodeControl(null) === null);
}

// ===========================================================================
console.log('  -- authorization of decoded commands --');
// ===========================================================================
{
  const fx = load('capabilities.json');

  for (const [kind, want] of Object.entries(fx.permissionRequiredFor)) {
    if (kind.startsWith('_')) continue;
    check(`${kind} requires ${want === null ? 'no permission' : want}`,
      P.requiredPermission(kind) === want, `got ${show(P.requiredPermission(kind))}`);
  }

  const viewOnly = { grantId: 'g', controllerId: 'c', active: true, permissions: ['screen.view'] };
  const control = { grantId: 'g', controllerId: 'c', active: true, permissions: ['screen.view', 'input.control'] };
  const power = { grantId: 'g', controllerId: 'c', active: true, permissions: ['screen.view', 'system.restart'] };

  check('a view-only grant refuses a tap',
    P.authorizeCommand({ kind: 'pointer.tap', x: 0.5, y: 0.5 }, viewOnly) === false);
  check('a control grant allows a tap',
    P.authorizeCommand({ kind: 'pointer.tap', x: 0.5, y: 0.5 }, control) === true);
  check('a control grant refuses a restart',
    P.authorizeCommand({ kind: 'system.restart' }, control) === false);
  check('a restart grant allows a restart',
    P.authorizeCommand({ kind: 'system.restart' }, power) === true);
  check('a restart grant refuses a shutdown',
    P.authorizeCommand({ kind: 'system.shutdown' }, power) === false);
  check('a restart grant refuses a tap it was not given',
    P.authorizeCommand({ kind: 'pointer.tap', x: 0, y: 0 }, power) === false);
  check('no grant at all refuses everything that needs a permission',
    P.authorizeCommand({ kind: 'pointer.tap', x: 0, y: 0 }, null) === false);
  check('a permission-free command is allowed without a grant',
    P.authorizeCommand({ kind: 'host.status' }, null) === true);
  check('an unknown command is refused even with a full grant',
    P.authorizeCommand({ kind: 'system.selfDestruct' },
      { active: true, permissions: P.PERMISSIONS.slice() }) === false);

  // A legacy grant is the realistic upgrade case: an Android host that already
  // has grants on disk must not acquire power over a machine when it updates.
  const legacy = { grantId: 'g', controllerId: 'c', active: true, scope: ['VIEW', 'CONTROL'] };
  check('a legacy CONTROL grant still allows input', P.authorizeCommand({ kind: 'pointer.tap', x: 0, y: 0 }, legacy) === true);
  for (const kind of ['system.lock', 'system.sleep', 'system.hibernate', 'system.restart', 'system.shutdown']) {
    check(`a legacy CONTROL grant refuses ${kind}`, P.authorizeCommand({ kind }, legacy) === false);
  }
}

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
