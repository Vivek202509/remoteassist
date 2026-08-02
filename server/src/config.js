'use strict';

// Centralized configuration, sourced from environment with sane local defaults.
module.exports = {
  port: parseInt(process.env.PORT || '8080', 10),

  // TURN (coturn) — must match infra/turnserver.conf
  turn: {
    host: process.env.TURN_HOST || 'localhost',
    secret: process.env.TURN_SECRET || 'dev-only-change-me',
    ttlSec: parseInt(process.env.TURN_TTL_SEC || '3600', 10),
    stunPort: parseInt(process.env.TURN_STUN_PORT || '3478', 10),
    tlsPort: parseInt(process.env.TURN_TLS_PORT || '5349', 10),
  },

  // Session/code lifetimes
  sessionCodeTtlMs: parseInt(process.env.SESSION_CODE_TTL_MS || '300000', 10), // 5 min
  pendingWakeTtlMs: parseInt(process.env.PENDING_WAKE_TTL_MS || '60000', 10),  // 1 min

  // FCM: only used if firebase-admin is installed AND credentials are present.
  fcmEnabled: process.env.FCM_ENABLED === 'true',
};
