'use strict';

// In-memory broker state. For production, back these with Redis so multiple
// signaling instances share state; the interface below is deliberately small
// so swapping the storage layer is straightforward.

/** @typedef {import('ws').WebSocket} WS */

// deviceId (public-key id) -> ws
const devices = new Map();

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

function findGrant(hostId, controllerId) {
  const list = grants.get(hostId);
  if (!list) return null;
  for (const g of list.values()) {
    if (g.controllerId === controllerId && g.active) {
      if (g.expiresAt && g.expiresAt < Date.now()) continue;
      return g;
    }
  }
  return null;
}

module.exports = {
  devices, sessions, pendingWakes, grants, pairings, fcmTokens,
  linkPair, unlinkPair, arePaired, findGrant,
};
