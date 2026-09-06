'use strict';

// In-memory broker state. For production, back these with Redis so multiple
// signaling instances share state; the interface below is deliberately small
// so swapping the storage layer is straightforward.

/** @typedef {import('ws').WebSocket} WS */

const proto = require('./protocol');

// deviceId (public-key id) -> ws
const devices = new Map();

// deviceId -> { platform, version, capabilities }  self-described endpoint metadata
//
// Deliberately a separate map rather than a field on the socket: it is the one
// piece of per-device state that a future Redis-backed broker would want to
// share across instances, whereas `devices` holds live sockets and never can.
// Keeping it separate means the storage seam falls in a usable place.
//
// Everything in here is UNVERIFIED. It is what a peer said about itself, and it
// exists to drive UI affordances, not decisions. Authorization comes from grants.
const deviceMeta = new Map();

// 6-digit code -> { hostId, expires }
const sessions = new Map();

// hostId -> { controllerId, code, unattended, expires }  (delivered on host reconnect)
const pendingWakes = new Map();

// hostId -> Map(grantId -> grant)  standing unattended grants
const grants = new Map();

// pubKeyId -> Set(pubKeyId)  symmetric "may connect" pairing graph
const pairings = new Map();

// deviceId -> fcmToken
const fcmTokens = new Map();

function linkPair(a, b) {
  if (!pairings.has(a)) pairings.set(a, new Set());
  pairings.get(a).add(b);
}
function unlinkPair(a, b) {
  pairings.get(a)?.delete(b);
}
function arePaired(a, b) {
  return pairings.get(a)?.has(b) === true;
}

function setMeta(deviceId, meta) {
  if (meta) deviceMeta.set(deviceId, meta);
  else deviceMeta.delete(deviceId);
}
function getMeta(deviceId) {
  return deviceMeta.get(deviceId) || null;
}
function clearMeta(deviceId) {
  deviceMeta.delete(deviceId);
}

/**
 * The standing grant a host holds for one controller, or null.
 *
 * Usability is decided by `protocol.grantUsable` rather than re-implemented
 * here. That matters: the check this replaced was `g.expiresAt && g.expiresAt <
 * Date.now()`, a truthiness guard that reads `expiresAt: 0` as never-expiring
 * when 0 is the Unix epoch — the most expired a grant can possibly be. The
 * broker gates unattended auto-join on this result, so failing open here hands a
 * revoked controller a session with no consent prompt.
 * `protocol/fixtures/capabilities.json` pins the fail-closed behaviour.
 */
function findGrant(hostId, controllerId, now = Date.now()) {
  const list = grants.get(hostId);
  if (!list) return null;
  for (const g of list.values()) {
    if (g.controllerId === controllerId && proto.grantUsable(g, now)) return g;
  }
  return null;
}

module.exports = {
  devices, sessions, pendingWakes, grants, pairings, fcmTokens, deviceMeta,
  linkPair, unlinkPair, arePaired, findGrant,
  setMeta, getMeta, clearMeta,
};
