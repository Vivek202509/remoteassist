# RemoteAssist — Play Console Declaration Text

Ready-to-paste text for each Play Console form. Replace bracketed placeholders
(`[App Name]`, URLs, contact email) with real values. "RemoteAssist" is a placeholder
name; the remote-support framing is deliberate.

> These are drafts to adapt, not legal advice. The Data Safety answers must match the
> app's actual runtime behavior — a mismatch between the form and observed behavior is
> itself a rejection/removal reason.

---

## 1. Accessibility API — Permissions Declaration

**Form:** Policy → App content → Accessibility / Permissions declaration for `BIND_ACCESSIBILITY_SERVICE`.

### "How does your app use the AccessibilityService API?"

```
RemoteAssist is a remote-support / remote-desktop application (comparable to
TeamViewer or AnyDesk). It uses the AccessibilityService API for one purpose only:
to let a remote support technician or a device the user has explicitly paired
perform touch gestures (tap, swipe, scroll), press the Back/Home/Recents
navigation keys, and enter text on the controlled device during a live,
user-authorized remote-control session.

The AccessibilityService is NOT used to assist users with disabilities, and the
app does not declare itself an accessibility tool. It is used solely as the
Android-supported mechanism for injecting input events during remote control,
which no other public API provides.

The service performs input actions only while a remote session is active. It does
not read, collect, log, store, or transmit screen content, credentials, messages,
or any data from other apps. Input events flow one direction — from the paired,
authenticated controlling device to the controlled device — over an end-to-end
encrypted WebRTC data channel.
```

### "Why is the AccessibilityService API necessary and why can't you use a less sensitive API?"

```
Remote control requires programmatically dispatching touch gestures and text input
to whatever app is on screen. On Android, the only public API capable of injecting
gestures into arbitrary apps is AccessibilityService.dispatchGesture() together with
performGlobalAction(). There is no alternative public API (MediaProjection provides
screen capture only, not input injection). Without the AccessibilityService, the app
can offer view-only screen sharing but cannot provide the remote-control functionality
that is its core, disclosed purpose.
```

### In-app prominent disclosure (shown before sending the user to Settings)

```
RemoteAssist needs Accessibility access to control this device remotely.

When you enable this, RemoteAssist can perform taps, swipes, and type text on this
device — but only while you have an active remote session that you have approved,
and only from a device you have paired and trusted.

RemoteAssist does NOT read your screen content, passwords, or data from other apps,
and never runs in the background collecting information. A notification is always
shown while remote access is active. You can turn this off at any time in Settings.

[ Enable Accessibility ]      [ Not now ]
```

---

## 2. Foreground Service declarations

**Form:** Policy → App content → Foreground service permissions. One entry per FGS type.

### 2a. `mediaProjection`

**Which foreground service type(s):** `mediaProjection`

**Describe the core functionality that requires this type:**
```
RemoteAssist shares this device's screen with a remote, user-authorized device so a
support person (or the user's own paired device) can see the screen during a live
remote-support session. Screen capture is started only after the user grants the
Android MediaProjection system consent dialog. A non-dismissible foreground-service
notification is displayed for the entire duration of screen sharing, clearly stating
that the screen is being shared. Capture stops when the user ends the session.
```

**Why a foreground service (vs. WorkManager/background):**
```
Screen sharing is an ongoing, real-time, user-initiated task that must run
continuously and visibly for as long as the remote session is active. It requires
immediate, uninterrupted execution and cannot be deferred or batched, so it does not
fit background execution APIs such as WorkManager. Android also requires MediaProjection
to run within a foreground service of type mediaProjection.
```

### 2b. `connectedDevice`

**Which foreground service type(s):** `connectedDevice`

**Describe the core functionality that requires this type:**
```
When a paired remote device requests a session, RemoteAssist briefly runs a
foreground service to re-establish its real-time signaling connection to the remote
device and complete the connection handshake. This maintains the live, bidirectional
connection to the specific paired device the user is communicating with. The service
runs only around an active or incoming session and stops once the session ends or the
request times out. A notification is shown while it runs.
```

**Why a foreground service:**
```
Establishing and maintaining a real-time peer connection to the paired device is a
time-sensitive, user-facing task triggered by an incoming or outgoing session. It must
execute immediately and stay connected for the duration of the interaction, which
deferred/background execution APIs cannot provide.
```

> For each FGS type, attach a 20–40s screencast: user approves a session → notification
> appears → session runs → session ends. Console requests this for FGS declarations.

---

## 3. Individual permission declarations

**Form:** App content → Sensitive/restricted permissions.

### 3a. `SYSTEM_ALERT_WINDOW` (Display over other apps)

```
Used to display remote-session status and control affordances (an on-screen indicator
and session controls) on top of other apps while a remote-control session is active, so
the user of the controlled device always sees that a session is in progress and can end
it from anywhere. It is not used for advertising, overlays that obscure system UI, or
any deceptive purpose, and is active only during an authorized session.
```

### 3b. `USE_FULL_SCREEN_INTENT`

```
Used to present an incoming remote-session request as a call-style, high-priority
notification so the device owner can promptly review and approve or decline a connection
attempt, including when the screen is off. This is functionally equivalent to an incoming
call notification. If the permission is not granted, the app degrades gracefully to a
standard heads-up notification.
```

### 3c. `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`

```
RemoteAssist maintains a real-time connection so that a paired device can reach this
device for a support session, including when the device is idle. Battery-optimization
exemption is requested only with explicit user action and only to keep the signaling
connection reachable for incoming session requests. The request is optional; if declined,
the app continues to function and guides the user to adjust settings manually. It is not
used to run continuous background work unrelated to remote sessions.
```

### 3d. `RECORD_AUDIO` — only if you actually stream device audio

```
Used only to optionally stream this device's audio to the paired remote device during
an active, user-authorized remote-support session, so the remote user can hear audio
relevant to the support task. Audio is streamed live over an end-to-end encrypted
channel, is never recorded or stored, and capture occurs only during an active session
the user has approved.
```

> If your shipping build does NOT stream audio, remove `RECORD_AUDIO` from the manifest
> and skip 3d — an unused sensitive permission is a rejection reason.

---

## 4. Data Safety form

**Form:** App content → Data safety. Adjust to your actual build.

### Overview answers

- Does your app collect or share any of the required user data types? → **Yes** (device identifiers / app-functionality data).
- Is all user data encrypted in transit? → **Yes** (WebRTC DTLS-SRTP; TLS/WSS for signaling).
- Do you provide a way for users to request data deletion? → **Yes** (in-app: unpair devices, revoke access, clear local data).

### Data types — declare each you actually handle

| Data type | Collected | Shared | Ephemeral only | Purpose | Required/Optional |
|---|---|---|---|---|---|
| Device or other IDs (device identity public key, FCM token) | Yes | No | No | App functionality (pairing, routing, wake) | Required |
| App activity (session start/end, audit log) | Yes | No | No | App functionality, security | Required |
| Screen content / audio (during session) | No (not collected) | Transmitted live to paired device only | Yes — not stored | App functionality | — |

### "Explain how data is collected/used" free-text

```
RemoteAssist collects the minimum data needed to connect two paired devices:
- A per-device cryptographic identity (public key) and a Firebase Cloud Messaging
  token, used to pair devices, route connection requests, and wake a device for an
  incoming session.
- Session metadata (start/end time, which paired device connected) for the user's own
  audit log and for security.

Screen content and, if enabled, audio are transmitted in real time directly between the
two paired devices over an end-to-end encrypted WebRTC connection during an active,
user-authorized session. This media is NOT collected by us, NOT stored, and NOT
accessible to our servers, which only relay connection-setup messages and cannot decrypt
session media.

All data in transit is encrypted (DTLS-SRTP for media; TLS for signaling). Users can
delete their data by unpairing devices and clearing app data; unpairing immediately
revokes all access and stops routing.
```

> If a TURN server relays media when P2P fails, note it relays encrypted packets it
> cannot decrypt — still not "collection." Only claim "not stored" if your TURN config
> does not log/record media (coturn does not by default).

---

## 5. Privacy policy (hosted content the Console links to)

```
RemoteAssist Privacy Policy

What RemoteAssist does
RemoteAssist is a remote-support / remote-access tool. It lets a device you have paired
and explicitly authorized view and control this device during a live session that you
approve. It is not a monitoring or surveillance tool, cannot be hidden, and always shows
a visible notification while access is active or armed.

Information we handle
- Device identity: each installation generates a cryptographic key pair stored securely
  on the device. The public key identifies the device for pairing. Private keys never
  leave the device.
- Connection tokens: a Firebase Cloud Messaging token so a paired device can reach this
  device for an incoming session.
- Session records: start/end times and the identity of the connecting paired device,
  kept for your audit log.

Screen content and audio
Live screen and optional audio are sent directly between paired devices over an
end-to-end encrypted connection during sessions you authorize. We do not collect, store,
record, or have the ability to decrypt this content. Our servers only relay the messages
needed to establish the connection.

Consent and control
- A session on the controlled device requires either explicit per-session approval or an
  unattended-access grant that the device owner set up on the device with authentication.
- A persistent notification is shown whenever access is active or armed.
- You can revoke any paired device or turn off all remote access at any time, which
  immediately ends active sessions and stops future access.

Data sharing and retention
We do not sell your data. We do not share it with third parties except infrastructure
providers strictly needed to deliver the service (e.g., push-message delivery, connection
relay). Session media is never retained. You can delete your data by unpairing devices
and clearing app data.

Contact
[privacy@yourdomain.com]
```

---

## 6. Store listing text

### Title / short description

```
Title:  RemoteAssist — Remote Support & Access
Short:  Securely view and control your own devices, or get remote help — with consent.
```

### Full description (opening — sets the compliant frame)

```
RemoteAssist lets you securely connect to and control another Android device you own or
have been given permission to help — like remote-desktop tools for phones and tablets.

CONSENT-FIRST BY DESIGN
- The device being accessed always approves the connection.
- A visible notification is shown the entire time access is active.
- You can end a session or revoke a device at any time.

SECURE
- Devices are paired with cryptographic verification.
- Sessions are end-to-end encrypted; we can't see your screen.

USE IT FOR
- Helping family or colleagues who need hands-on support.
- Accessing your own second device from across the room or across the country.
- IT/helpdesk remote assistance with the user's consent.

RemoteAssist is a remote-support tool. It is not a hidden monitoring or spying app: it
cannot be concealed, and it always notifies the person using the device that a session
is in progress.
```

> Keep every screenshot consistent with this: show the consent dialog, the pairing
> screen, and the persistent notification. Avoid any surveillance vocabulary
> ("spy," "hidden," "secret," "monitor a partner/employee").
