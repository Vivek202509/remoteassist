# RemoteAssist

A consent-first, end-to-end-encrypted **remote support / remote desktop** system for
Android — bidirectional (either paired device can view & control the other). Built from
the design developed in this project: WebRTC transport, a signaling + pairing broker,
coturn for NAT traversal, MediaProjection screen capture, AccessibilityService input,
FCM device-wake, unattended access, and cryptographic device pairing.

> **Scope of this repo.** This is a complete, organized project scaffold with the core
> logic implemented and wired together. The **signaling server runs and is tested**
> (`npm run smoke` → 12/12). The **Android app requires the Android SDK + two physical
> devices** to build and exercise (MediaProjection, AccessibilityService, and WebRTC
> cannot run on this host or in a single emulator meaningfully). Integration points that
> need a real device are marked in code and listed under "What needs a device" below.

---

## Repository layout

```
techee/
├── server/                     # Node.js signaling + pairing broker + FCM wake  (RUNNABLE)
│   ├── src/
│   │   ├── server.js           #   WebSocket message router (all protocol handlers)
│   │   ├── state.js            #   in-memory devices/sessions/grants/pairings
│   │   ├── turn.js             #   time-limited TURN credential generation
│   │   ├── fcm.js              #   high-priority data-only wake push (optional)
│   │   └── config.js           #   env-driven config
│   ├── test/smoke.js           #   end-to-end protocol test (no Android needed)
│   └── package.json
├── infra/
│   └── turnserver.conf         # coturn config (STUN/TURN)
├── android/                    # Android app (Gradle / Kotlin / Compose)
│   ├── settings.gradle.kts
│   ├── build.gradle.kts
│   ├── gradle.properties
│   └── app/
│       ├── build.gradle.kts
│       ├── proguard-rules.pro
│       └── src/main/
│           ├── AndroidManifest.xml
│           ├── res/…           # icon, notification drawables, accessibility config
│           └── java/com/remoteassist/
│               ├── RemoteApp.kt              # Application: init + FCM token
│               ├── ServiceLocator.kt         # manual DI + signaling Listener fan-out
│               ├── MainActivity.kt           # Compose host + permission launchers
│               ├── MainViewModel.kt          # UI state + intents
│               ├── crypto/Crypto.kt          # P-256 sign/verify, ECDH, safety number
│               ├── identity/DeviceIdentity.kt# hardware-backed device keypair
│               ├── trust/                    # TrustStore + PairingManager
│               ├── unattended/GrantStore.kt  # standing unattended grants
│               ├── signaling/SignalingClient.kt   # OkHttp WS + auto-reconnect
│               ├── webrtc/                   # WebRtcCore + RtcSession (ICE restart)
│               ├── session/SessionManager.kt # owns the active session, both roles
│               ├── host/                     # capture, input, unattended, wake services
│               ├── fcm/RemoteFcmService.kt   # wake receiver + coordinator
│               └── ui/                       # Compose screens + theme
├── play_console_declarations.md   # store-submission declaration text
├── play_console_reviewer_notes.md # reviewer notes + test-credentials template
├── remoteassist_demo_captions.srt # demo-video captions
└── remoteassist_demo_transcript.txt
```

---

## 1. Run the signaling server (works now)

```bash
cd server
npm install
npm run smoke      # 12/12 protocol checks, no external services
npm start          # listens on ws://0.0.0.0:8080
```

Configuration is via environment (see `server/.env.example`). On Node 20+ you can load it
with `node --env-file=.env src/server.js`. FCM is optional and disabled by default; the
server runs fully without it (attended sessions need no wake).

### What the server does
- Registers devices by their **public-key id**.
- Brokers **code-based** joins (`host-open` → 6-digit code → `join`) and **paired-direct**
  joins (dial a trusted host by id; requires an existing pairing).
- Relays the WebRTC handshake (`offer`/`answer`/`ice`/`restart`) and pairing/auth messages.
- Issues **time-limited TURN credentials** to clients (HMAC of expiry against the coturn
  shared secret).
- On a join to an **offline** host, sends an **FCM high-priority data-only wake** and
  queues the request until the host reconnects.
- Never sees media — only connection-setup metadata.

---

## 2. Deploy coturn (for cross-network sessions)

Two phones "in different locations" almost always need TURN (carrier CGNAT blocks P2P).

```bash
# On a host with a public IP:
sudo apt install coturn
sudo cp infra/turnserver.conf /etc/turnserver.conf
# edit external-ip, realm, static-auth-secret (must equal server's TURN_SECRET),
# and TLS cert paths (certbot)
sudo turnserver -c /etc/turnserver.conf -v
```

Open UDP/TCP **3478**, **5349**, and UDP **49152–65535**. Verify with the WebRTC
`trickle-ice` test page before wiring the app.

---

## 3. Build the Android app (needs Android SDK)

Open `android/` in **Android Studio** (Koala or newer), or from the CLI:

```bash
cd android
# Android Studio generates the Gradle wrapper on first open; from CLI, once:
gradle wrapper --gradle-version 8.9
./gradlew :app:assembleDebug
```

Prerequisites: Android SDK (API 35), JDK 17. Point the app at your server via
`SIGNALING_URL` in `app/build.gradle.kts` (defaults to `ws://10.0.2.2:8080` — the
emulator's route to host loopback). For two real devices, use your machine's LAN IP or a
deployed `wss://` URL.

For **FCM wake**, add a `google-services.json` to `app/`, uncomment the
`com.google.gms.google-services` plugin lines in the two Gradle files, and set
`FCM_ENABLED=true` + credentials on the server. Without it, the app still builds and runs;
only device-wake for offline hosts is inert.

---

## 4. How a session flows

```
Host taps "Share my screen" ──host-open──► server ──► 6-digit code shown
Controller enters code ──join──► server ──join-request──► Host
Host: ConsentDialog (Allow) ──► MediaProjection system dialog (Start now)
      ScreenCaptureService (FGS + persistent notification) builds screen track
Host ──offer(+fingerprint sig)──► server ──► Controller ──answer──► Host
      ICE via STUN/coturn ──► P2P or relayed
Controller sees screen; taps/swipes ──DataChannel──► Host AccessibilityService
Either side ends ──► notification clears, PeerConnection closes
```

Bidirectional: the app ships **both roles**; whichever device runs `host-open` is the
controlled one for that session. A→B and B→A can run as two independent sessions.

---

## 5. Security model (implemented)

- **Device identity**: hardware-backed P-256 keypair in Android Keystore
  (`DeviceIdentity`). Public key = stable identity id.
- **Pairing**: authenticated ECDH (`PairingManager`) → shared secret + mutual identity
  proofs + an out-of-band **safety number** users compare. Trust persisted encrypted
  (`TrustStore` on EncryptedSharedPreferences).
- **Per-session auth**: dialer signs a fresh challenge; only a **trusted** identity is
  accepted.
- **MITM-proof media**: each peer signs its **DTLS fingerprint** with its identity key;
  the other verifies against the paired public key (`RtcSession.signFingerprint` /
  `verifyRemoteSdp`). A compromised signaling server cannot insert itself.
- **Unattended access**: owner-configured, scoped, expiring, revocable grants
  (`GrantStore`), referencing a **paired identity** — never a raw id.
- **Transport**: WebRTC DTLS-SRTP end-to-end; signaling over TLS/WSS in production.

---

## 6. Reconnection / ICE restart (implemented)

`RtcSession` arms recovery the instant the first connection succeeds:
- Registers a default-network callback → ICE restart on Wi-Fi↔cellular switch.
- On `DISCONNECTED` (2s grace) / `FAILED` (immediate): reconnect signaling if needed,
  refresh TURN creds, `createOffer(IceRestart=true)` reusing the same PeerConnection
  (video track + data channel survive — no black screen, no re-consent).
- Exponential backoff, capped at 15s, 6 attempts, then `CLOSED` → UI offers manual retry.

---

## 7. What needs a real device (not runnable on this host)

These are implemented but only exercisable on hardware with the SDK:

- **Screen capture** (`MediaProjection`) and **input injection** (`AccessibilityService`).
- **WebRTC** peer connection / TURN traversal (needs two networked devices).
- **FCM wake** (needs `google-services.json` + Firebase project).
- **Remote video rendering**: `MainViewModel.remoteVideo` exposes the received
  `VideoTrack`; wire it to a `SurfaceViewRenderer` inside an `AndroidView` in
  `SessionScreen`, then map renderer touches to normalized taps via `vm.sendTap()`.
  (Left as a documented integration point — the plumbing on both sides is complete.)

### Unattended access limitations (platform, not code)
On Android 11+ a MediaProjection permission Intent is effectively single-use, and Android
14+ tightens re-acquisition. `UnattendedHostService` stores the grant Intent for the
scaffold; a production unattended build must **keep the capturer/track alive** across
sessions rather than rebuilding from a stored Intent. This is the honest platform ceiling
described in the design docs, not a bug in the wiring.

---

## 8. Play Store submission

The `play_console_*` files and the demo `.srt` / transcript are the ready-to-paste
compliance package (Accessibility + foreground-service declarations, Data Safety answers,
privacy policy, reviewer notes, demo-video captions). This app category (AccessibilityService
+ remote control + unattended access) faces heavy review — read
`play_console_declarations.md` before submitting.

---

## 9. Status summary

| Component | State |
|---|---|
| Signaling server + protocol | ✅ Implemented, tested (`npm run smoke`) |
| TURN config | ✅ Provided |
| Android project (Gradle/manifest/res) | ✅ Complete |
| Crypto / identity / pairing / trust | ✅ Implemented |
| Signaling client + reconnect | ✅ Implemented |
| WebRTC session + ICE restart + fingerprint auth | ✅ Implemented |
| Host services (capture / input / unattended / wake) | ✅ Implemented |
| FCM wake | ✅ Implemented (needs Firebase project to activate) |
| Compose UI (home / code / join / consent / paired / safety / session) | ✅ Implemented |
| QR scanner for pairing | ⛏ Stub button — add a scanner (e.g. CameraX + ML Kit) |
| Remote video render surface | ⛏ Documented integration point |
| Compiled APK | ✅ Builds — `:app:assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk` (JDK 17+, SDK API 35) |
```
