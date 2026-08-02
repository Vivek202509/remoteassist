'use strict';

const crypto = require('crypto');
const cfg = require('./config');

// Generate short-lived TURN credentials compatible with coturn's
// `use-auth-secret` / `static-auth-secret` mechanism.
// username = "<unixExpiry>:<label>", password = base64(HMAC-SHA1(secret, username)).
function turnCredentials(label = 'remoteassist') {
  const expiry = Math.floor(Date.now() / 1000) + cfg.turn.ttlSec;
  const username = `${expiry}:${label}`;
  const password = crypto
    .createHmac('sha1', cfg.turn.secret)
    .update(username)
    .digest('base64');
  return { username, password, ttl: cfg.turn.ttlSec };
}

// The iceServers array the client feeds into PeerConnection.RTCConfiguration.
function iceServers() {
  const { username, password } = turnCredentials();
  const h = cfg.turn.host;
  return [
    { urls: `stun:${h}:${cfg.turn.stunPort}` },
    { urls: `turn:${h}:${cfg.turn.stunPort}?transport=udp`, username, credential: password },
    { urls: `turns:${h}:${cfg.turn.tlsPort}?transport=tcp`, username, credential: password },
  ];
}

module.exports = { turnCredentials, iceServers };
