'use strict';

const cfg = require('./config');
const { fcmTokens } = require('./state');

// firebase-admin is an optional dependency. If it's absent or FCM is disabled,
// wakeDevice() becomes a logged no-op so the rest of the system still runs
// (attended sessions work fine without wake).
let messaging = null;
if (cfg.fcmEnabled) {
  try {
    const admin = require('firebase-admin');
    admin.initializeApp({ credential: admin.credential.applicationDefault() });
    messaging = admin.messaging();
    console.log('[fcm] enabled');
  } catch (e) {
    console.warn('[fcm] enabled in config but firebase-admin/credentials unavailable:', e.message);
  }
} else {
  console.log('[fcm] disabled (set FCM_ENABLED=true + credentials to enable)');
}

/**
 * Wake a dozing/backgrounded host via a high-priority, data-only push so the
 * device's onMessageReceived runs and it reconnects its signaling socket.
 */
async function wakeDevice(deviceId, payload) {
  const token = fcmTokens.get(deviceId);
  if (!token) {
    console.warn(`[fcm] no token for ${deviceId}; cannot wake`);
    return { ok: false, reason: 'no-token' };
  }
  if (!messaging) {
    console.log(`[fcm] (noop) would wake ${deviceId} with`, payload);
    return { ok: false, reason: 'fcm-disabled' };
  }

  const message = {
    token,
    // DATA-ONLY (no `notification` block) so client code always runs in background.
    data: {
      type: 'wake',
      controllerId: String(payload.controllerId ?? ''),
      sessionId: String(payload.sessionId ?? ''),
      unattended: String(payload.unattended ?? ''),
    },
    android: {
      priority: 'high', // required to bypass Doze deferral
      ttl: cfg.pendingWakeTtlMs, // stale wakes should expire, not power on later
    },
  };

  try {
    const id = await messaging.send(message);
    return { ok: true, id };
  } catch (e) {
    if (e.code === 'messaging/registration-token-not-registered') {
      fcmTokens.delete(deviceId); // dead token; device must re-report
    }
    console.error(`[fcm] send failed for ${deviceId}:`, e.message);
    return { ok: false, reason: e.code || 'send-failed' };
  }
}

module.exports = { wakeDevice };
