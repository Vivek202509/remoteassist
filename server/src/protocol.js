'use strict';

// The Techee protocol contract, in JavaScript.
//
// This is one of three implementations of the same specification — the others
// are `com.remoteassist.protocol` on Android and (from W2) `Techee.Protocol` on
// Windows. None of them is derived from the others; all three are validated
// against the shared golden vectors in `protocol/fixtures/`, which is what keeps
// them from drifting. See docs/PROTOCOL.md for the normative prose.
//
// The broker uses only the endpoint-metadata and permission halves of this file.
// The control-frame half is here because the specification is one document and
// splitting it across repositories is how dialects diverge; it also gives the
// fixtures a runnable reference oracle. The broker never parses control frames —
// they are end-to-end encrypted over the peer connection and the broker cannot
// read them even if it wanted to.

// ---------------------------------------------------------------------------
// Versioning
// ---------------------------------------------------------------------------

// Bumping CURRENT means adding messages or fields. It never means changing what
// a v1 message already means: a receiver that only speaks v1 must be able to
// keep speaking v1 to a newer peer indefinitely.
const PROTOCOL_VERSION = 1;
const MIN_PROTOCOL_VERSION = 1;

// Frames with no `v` are the pre-versioning dialect the shipped Android app
// speaks. It is a real, supported dialect, not an error to be tolerated.
const LEGACY_VERSION = 0;

// ---------------------------------------------------------------------------
// Endpoint metadata
// ---------------------------------------------------------------------------

const PLATFORMS = Object.freeze(['android', 'windows']);

const CAPABILITIES = Object.freeze([
  'screen.share', 'screen.receive',
  'input.send', 'input.receive',
  'audio.microphone.send', 'audio.desktop.send', 'audio.receive',
  'clipboard',
  'display.multi',
  'nav.android',
  'power.lock', 'power.sleep', 'power.hibernate', 'power.restart', 'power.shutdown',
]);

const LIMITS = Object.freeze({
  maxCapabilities: 32,
  maxCapabilityLength: 40,
  maxVersionLength: 32,
  maxPermissions: 32,
  maxFrameBytes: 65536,
  maxTextBytes: 4096,
  maxClipboardBytes: 65536,
  maxSwipeMs: 10000,
  defaultSwipeMs: 200,
});

const CAPABILITY_SET = new Set(CAPABILITIES);

/**
 * Validate a peer's self-description.
 *
 * Returns a normalized `{platform, version, capabilities}` or null.
 *
 * Null is not a failure the caller should escalate — it means "this peer did not
 * tell us what it is", which is exactly what every Android build shipped so far
 * does. Treat it as unknown and hide capability-gated UI, never as a reason to
 * refuse the connection.
 *
 * Unknown capability tokens are dropped rather than rejected, so a newer peer
 * advertising something this build has never heard of still connects with the
 * capabilities both sides do understand.
 *
 * NOTHING here is an authorization decision. A peer claiming `power.shutdown`
 * has described its own hardware, not acquired a right over ours. Authorization
 * lives in grants — see `grantPermits`.
 */
function parseEndpointMeta(raw) {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return null;

  const platform = raw.platform;
  if (typeof platform !== 'string' || !PLATFORMS.includes(platform)) return null;

  let version = raw.version;
  if (version === undefined || version === null) version = 'unknown';
  if (typeof version !== 'string') return null;
  if (version.length === 0 || version.length > LIMITS.maxVersionLength) return null;

  const capabilities = [];
  if (Array.isArray(raw.capabilities)) {
    for (const c of raw.capabilities) {
      if (capabilities.length >= LIMITS.maxCapabilities) break;
      if (typeof c !== 'string') continue;
      if (c.length > LIMITS.maxCapabilityLength) continue;
      if (!CAPABILITY_SET.has(c)) continue;
      if (!capabilities.includes(c)) capabilities.push(c);
    }
  }
  // Sorted so two implementations that build the list in different orders still
  // compare equal — the fixtures rely on this being deterministic.
  capabilities.sort();

  return { platform, version, capabilities };
}

// ---------------------------------------------------------------------------
// Permissions — the authoritative half
// ---------------------------------------------------------------------------

const PERMISSIONS = Object.freeze([
  'screen.view',
  'input.control',
  'clipboard.read', 'clipboard.write',
  'system.lock', 'system.sleep', 'system.hibernate', 'system.restart', 'system.shutdown',
  'files.transfer',
]);

const PERMISSION_SET = new Set(PERMISSIONS);

/**
 * How the shipped Android `enum Scope { VIEW, CONTROL, FILES, CLIPBOARD }` maps
 * onto v1 permission tokens.
 *
 * Note what is absent: no legacy scope maps to any `system.*` permission. Every
 * grant created before power control existed therefore confers no power over the
 * machine, and an upgrade cannot silently hand an old controller the ability to
 * shut down a PC. That is the whole reason this table is data with a fixture
 * behind it rather than an inline conditional.
 */
const LEGACY_SCOPE_MAP = Object.freeze({
  VIEW: ['screen.view'],
  // Controlling a screen you may not see is not a coherent grant.
  CONTROL: ['input.control', 'screen.view'],
  CLIPBOARD: ['clipboard.read', 'clipboard.write'],
  FILES: ['files.transfer'],
});

/**
 * The permissions a grant actually confers, sorted and de-duplicated.
 *
 * Prefers an explicit `permissions` array; falls back to widening a legacy
 * `scope` array. Unknown tokens in either are dropped — a permission this build
 * does not understand is one it cannot enforce, so honouring it would be worse
 * than ignoring it.
 */
function normalizePermissions(grant) {
  if (!grant || typeof grant !== 'object') return [];
  const out = [];
  const add = (p) => {
    if (out.length >= LIMITS.maxPermissions) return;
    if (!PERMISSION_SET.has(p)) return;
    if (!out.includes(p)) out.push(p);
  };

  if (Array.isArray(grant.permissions)) {
    for (const p of grant.permissions) if (typeof p === 'string') add(p);
  } else if (Array.isArray(grant.scope)) {
    for (const s of grant.scope) {
      if (typeof s !== 'string') continue;
      for (const p of LEGACY_SCOPE_MAP[s] || []) add(p);
    }
  }
  out.sort();
  return out;
}

/**
 * Whether a grant is usable at `now`.
 *
 * `expiresAt` absent, null or undefined means "no expiry". Any number present is
 * honoured, including 0: 0 is the Unix epoch, the most expired a grant can
 * possibly be, so a truthiness guard on it fails open. Fail closed instead.
 * `state.js` calls this rather than re-deriving it, which is what keeps the
 * broker's unattended-join gate on the same rule as the fixtures.
 */
function grantUsable(grant, now = Date.now()) {
  if (!grant || typeof grant !== 'object') return false;
  if (grant.active !== true) return false;
  if (grant.expiresAt === undefined || grant.expiresAt === null) return true;
  if (typeof grant.expiresAt !== 'number' || !Number.isFinite(grant.expiresAt)) return false;
  return grant.expiresAt >= now;
}

/** Fail-closed authorization check: the single question a host asks before acting. */
function grantPermits(grant, permission, now = Date.now()) {
  if (!PERMISSION_SET.has(permission)) return false;
  if (!grantUsable(grant, now)) return false;
  return normalizePermissions(grant).includes(permission);
}

// ---------------------------------------------------------------------------
// Control frames
// ---------------------------------------------------------------------------

const NAV_KEYS = Object.freeze(['BACK', 'HOME', 'RECENTS']);
const POINTER_BUTTONS = Object.freeze(['left', 'right', 'middle', 'x1', 'x2']);
const CLIPBOARD_MIMES = Object.freeze(['text/plain']);

/** Which grant permission each command requires. Null means none. */
const REQUIRED_PERMISSION = Object.freeze({
  'pointer.tap': 'input.control',
  'pointer.swipe': 'input.control',
  'pointer.move': 'input.control',
  'pointer.down': 'input.control',
  'pointer.up': 'input.control',
  'pointer.wheel': 'input.control',
  'keyboard.keyDown': 'input.control',
  'keyboard.keyUp': 'input.control',
  'keyboard.text': 'input.control',
  'nav.key': 'input.control',
  'clipboard.set': 'clipboard.write',
  'clipboard.request': 'clipboard.read',
  'clipboard.data': null,
  'system.lock': 'system.lock',
  'system.sleep': 'system.sleep',
  'system.hibernate': 'system.hibernate',
  'system.restart': 'system.restart',
  'system.shutdown': 'system.shutdown',
  'display.list': 'screen.view',
  'display.select': 'screen.view',
  'display.info': null,
  'host.status': null,
  'host.callState': null,
  hello: null,
  'hello.ack': null,
  error: null,
});

/** v1 type name -> v0 type name, for the messages the legacy dialect can express. */
const V1_TO_V0 = Object.freeze({
  'pointer.tap': 'tap',
  'pointer.swipe': 'swipe',
  'nav.key': 'key',
  'keyboard.text': 'text',
  'host.callState': 'callstate',
});

/** v0 type name -> v1 type name. The inverse of the above. */
const V0_TO_V1 = Object.freeze({
  tap: 'pointer.tap',
  swipe: 'pointer.swipe',
  key: 'nav.key',
  text: 'keyboard.text',
  callstate: 'host.callState',
});

/** A finite number, or null. Rejects NaN, Infinity, strings and booleans. */
function num(v) {
  if (typeof v !== 'number' || !Number.isFinite(v)) return null;
  return v;
}

/** A normalized 0..1 surface coordinate. Finite values clamp; anything else fails. */
function coord(v) {
  const n = num(v);
  if (n === null) return null;
  return Math.min(1, Math.max(0, n));
}

function utf8Length(s) {
  return Buffer.byteLength(s, 'utf8');
}

/**
 * Decode one control frame into a platform-neutral command, or null.
 *
 * Never throws. A frame is attacker-influenced input arriving on a media
 * callback; on an unattended host, throwing here would take down the machine
 * that nobody is standing next to. Every rejection is a return, not an
 * exception, and unknown messages are ignored rather than escalated so a newer
 * peer can send us things we have not learned about yet.
 */
function decodeControl(frame) {
  if (frame === null || typeof frame !== 'object' || Array.isArray(frame)) return null;

  // Version gate. Absent means the legacy dialect; present must be an integer we
  // support. A future version is refused rather than guessed at.
  const v = frame.v;
  let version;
  if (v === undefined) {
    version = LEGACY_VERSION;
  } else if (typeof v === 'number' && Number.isInteger(v)) {
    if (v < MIN_PROTOCOL_VERSION || v > PROTOCOL_VERSION) return null;
    version = v;
  } else {
    return null;
  }

  const rawType = frame.t;
  if (typeof rawType !== 'string' || rawType.length === 0) return null;

  // Legacy names are lifted into their v1 equivalents so everything downstream
  // reasons about one vocabulary. A legacy frame may only use legacy names.
  let kind;
  if (version === LEGACY_VERSION) {
    kind = V0_TO_V1[rawType];
    if (!kind) return null;
  } else {
    kind = rawType;
    if (!(kind in REQUIRED_PERMISSION)) return null;
  }

  switch (kind) {
    case 'pointer.tap':
    case 'pointer.move': {
      const x = coord(frame.x), y = coord(frame.y);
      if (x === null || y === null) return null;
      return { kind, x, y };
    }

    case 'pointer.down':
    case 'pointer.up': {
      const x = coord(frame.x), y = coord(frame.y);
      if (x === null || y === null) return null;
      const button = frame.b === undefined ? 'left' : frame.b;
      if (typeof button !== 'string' || !POINTER_BUTTONS.includes(button)) return null;
      return { kind, x, y, button };
    }

    case 'pointer.wheel': {
      const x = coord(frame.x), y = coord(frame.y);
      if (x === null || y === null) return null;
      const dx = num(frame.dx), dy = num(frame.dy);
      if (dx === null || dy === null) return null;
      return { kind, x, y, dx, dy };
    }

    case 'pointer.swipe': {
      const x1 = coord(frame.x1), y1 = coord(frame.y1);
      const x2 = coord(frame.x2), y2 = coord(frame.y2);
      if (x1 === null || y1 === null || x2 === null || y2 === null) return null;
      let ms = frame.ms === undefined ? LIMITS.defaultSwipeMs : num(frame.ms);
      if (ms === null) return null;
      // Clamped rather than rejected: this preserves the shipped Android
      // behaviour, where a hostile duration cannot wedge the gesture queue but
      // an ordinary rounding artefact still produces a gesture.
      ms = Math.min(LIMITS.maxSwipeMs, Math.max(1, Math.trunc(ms)));
      return { kind, x1, y1, x2, y2, ms };
    }

    case 'nav.key': {
      const k = frame.k;
      if (typeof k !== 'string' || !NAV_KEYS.includes(k)) return null;
      return { kind, key: k };
    }

    case 'keyboard.keyDown':
    case 'keyboard.keyUp': {
      const code = frame.code;
      if (typeof code !== 'string' || code.length === 0 || code.length > 32) return null;
      const out = { kind, code };
      if (frame.mods !== undefined) {
        if (!Array.isArray(frame.mods)) return null;
        const mods = frame.mods.filter((m) => typeof m === 'string' && m.length > 0 && m.length <= 32);
        if (mods.length !== frame.mods.length) return null;
        out.mods = mods;
      }
      return out;
    }

    case 'keyboard.text': {
      const s = frame.s;
      if (typeof s !== 'string') return null;
      if (utf8Length(s) > LIMITS.maxTextBytes) return null;
      return { kind, text: s };
    }

    case 'clipboard.set':
    case 'clipboard.data': {
      const s = frame.s;
      if (typeof s !== 'string') return null;
      if (utf8Length(s) > LIMITS.maxClipboardBytes) return null;
      const mime = frame.mime === undefined ? 'text/plain' : frame.mime;
      // v1 is text/plain only. Refusing unknown formats outright is deliberate:
      // HTML and file payloads are how a clipboard channel turns into a delivery
      // mechanism, and adding them needs its own threat-model pass.
      if (typeof mime !== 'string' || !CLIPBOARD_MIMES.includes(mime)) return null;
      return { kind, mime, text: s };
    }

    case 'clipboard.request':
    case 'display.list':
    case 'system.lock':
    case 'system.sleep':
    case 'system.hibernate':
    case 'system.restart':
    case 'system.shutdown':
      return { kind };

    case 'display.select': {
      const id = frame.id;
      if (typeof id !== 'string' || id.length === 0 || id.length > 64) return null;
      return { kind, id };
    }

    case 'host.callState': {
      const state = frame.state;
      if (typeof state !== 'string' || state.length === 0 || state.length > 32) return null;
      return { kind, state };
    }

    case 'hello':
    case 'hello.ack': {
      const meta = parseEndpointMeta(frame);
      if (!meta) return null;
      return { kind, ...meta };
    }

    // Types that exist in the vocabulary but carry payloads this reference
    // implementation does not model yet. Recognised, so they are not mistaken
    // for hostile input, but not decoded.
    case 'display.info':
    case 'host.status':
    case 'error':
      return { kind };

    default:
      return null;
  }
}

/**
 * Parse a raw DataChannel payload.
 *
 * Enforces the frame-size ceiling before parsing, so an oversized payload costs
 * a length check rather than a JSON parse.
 */
function decodeControlRaw(raw) {
  let text;
  if (Buffer.isBuffer(raw)) {
    if (raw.length > LIMITS.maxFrameBytes) return null;
    text = raw.toString('utf8');
  } else if (typeof raw === 'string') {
    if (utf8Length(raw) > LIMITS.maxFrameBytes) return null;
    text = raw;
  } else {
    return null;
  }

  let frame;
  try {
    frame = JSON.parse(text);
  } catch {
    return null;
  }
  return decodeControl(frame);
}

/**
 * Render a command into the dialect a peer understands.
 *
 * `version` is LEGACY_VERSION for a peer that never sent `hello`. Returns null
 * when the command has no representation in that dialect — a Windows power
 * action simply cannot be expressed to a peer that predates it, and inventing an
 * encoding it would misread is worse than not sending.
 *
 * This is a presentation-layer downgrade and nothing more. It does not, and must
 * not, touch authentication: SDP identity binding is enforced separately, is not
 * negotiated, and has no legacy dialect to fall back to.
 */
function encodeControl(cmd, version = PROTOCOL_VERSION) {
  if (!cmd || typeof cmd !== 'object' || typeof cmd.kind !== 'string') return null;
  if (!(cmd.kind in REQUIRED_PERMISSION)) return null;

  const v1 = { v: PROTOCOL_VERSION, t: cmd.kind };
  switch (cmd.kind) {
    case 'pointer.tap':
    case 'pointer.move':
      Object.assign(v1, { x: cmd.x, y: cmd.y });
      break;
    case 'pointer.down':
    case 'pointer.up':
      Object.assign(v1, { x: cmd.x, y: cmd.y, b: cmd.button });
      break;
    case 'pointer.wheel':
      Object.assign(v1, { x: cmd.x, y: cmd.y, dx: cmd.dx, dy: cmd.dy });
      break;
    case 'pointer.swipe':
      Object.assign(v1, { x1: cmd.x1, y1: cmd.y1, x2: cmd.x2, y2: cmd.y2, ms: cmd.ms });
      break;
    case 'nav.key':
      v1.k = cmd.key;
      break;
    case 'keyboard.keyDown':
    case 'keyboard.keyUp':
      v1.code = cmd.code;
      if (cmd.mods) v1.mods = cmd.mods;
      break;
    case 'keyboard.text':
      v1.s = cmd.text;
      break;
    case 'clipboard.set':
    case 'clipboard.data':
      Object.assign(v1, { mime: cmd.mime || 'text/plain', s: cmd.text });
      break;
    case 'display.select':
      v1.id = cmd.id;
      break;
    case 'host.callState':
      v1.state = cmd.state;
      break;
    default:
      break; // payload-free commands
  }

  if (version !== LEGACY_VERSION) return v1;

  const legacyType = V1_TO_V0[cmd.kind];
  if (!legacyType) return null;

  const v0 = { ...v1, t: legacyType };
  delete v0.v;
  return v0;
}

/** The permission a decoded command requires, or null if it needs none. */
function requiredPermission(kind) {
  return REQUIRED_PERMISSION[kind] ?? null;
}

/**
 * The one call a host makes before executing a decoded command.
 *
 * Fails closed on every path: an unknown command, an unusable grant, or a
 * missing permission all return false.
 */
function authorizeCommand(cmd, grant, now = Date.now()) {
  if (!cmd || typeof cmd.kind !== 'string') return false;
  if (!(cmd.kind in REQUIRED_PERMISSION)) return false;
  const needed = REQUIRED_PERMISSION[cmd.kind];
  if (needed === null) return true;
  return grantPermits(grant, needed, now);
}

module.exports = {
  PROTOCOL_VERSION,
  MIN_PROTOCOL_VERSION,
  LEGACY_VERSION,
  PLATFORMS,
  CAPABILITIES,
  PERMISSIONS,
  LIMITS,
  NAV_KEYS,
  POINTER_BUTTONS,
  LEGACY_SCOPE_MAP,
  REQUIRED_PERMISSION,
  parseEndpointMeta,
  normalizePermissions,
  grantUsable,
  grantPermits,
  decodeControl,
  decodeControlRaw,
  encodeControl,
  requiredPermission,
  authorizeCommand,
};
