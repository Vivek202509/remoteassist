/* ============================================================================
   RemoteAssist — single source of truth for product facts that repeat.

   Only facts that drift live here: version, platform status, requirements,
   distribution shape and acceptance notices. Ordinary marketing copy stays in
   the HTML where it is readable.

   Every fact below traces to the repository:
     README.md
     docs/CROSS_PLATFORM_TEST_MATRIX.md
     docs/GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md
     docs/WINDOWS_VIDEO_PIPELINE.md
     android/app/build.gradle.kts
     server/package.json

   In the HTML, write the value as the element's own text AND tag it:

     <dd data-status="version.label">1.0 (build 1)</dd>

   The text in the HTML is what a visitor with JavaScript disabled sees, so it
   must match; `web/test/qa.js` fails the build if it drifts. This file then
   overwrites it at runtime, so one edit here fixes every page.

   Status vocabulary, used consistently across the site:
     verified     proven by a test or an observation recorded in the repository
     implemented  the code is complete and unit-tested; no hardware acceptance
     preview      built, but a required acceptance step has not been performed
     planned      not built
   ========================================================================= */
(function () {
  'use strict';

  var STATUS = {
    /* ------------------------------------------------------------ version */
    version: {
      label: '1.0 (build 1)',            // versionName 1.0 / versionCode 1
      name: '1.0',
      channel: 'Source release'
    },

    /* ------------------------------------------------------- requirements */
    requirements: {
      android: '8.0 – 15 · API 26–35',   // minSdk 26, compileSdk/targetSdk 35
      compileSdk: 'API 35',
      jdk: '17 (CI) or 21',              // compileOptions 17; CI uses temurin 17
      gradle: '8.9 (wrapper committed)',
      node: 'Node.js 18+',               // server/package.json engines
      dotnet: '.NET 10',
      transport: 'WebRTC DTLS-SRTP',
      licence: 'MIT'
    },

    /* ---------------------------------------------------------- platforms */
    /* `badge` is the short label; `tone` maps to the .badge--* CSS classes. */
    platforms: {
      android: {
        label: 'Android 8–15',
        state: 'implemented',
        badge: 'Implemented',
        tone: 'ok',
        note: 'Feature-complete and unit-tested. No two-handset session has been ' +
              'recorded yet — every row of the real-device matrix is still open.'
      },
      windowsHost: {
        label: 'Windows 10/11 host',
        state: 'preview',
        badge: 'Preview',
        tone: 'preview',
        note: 'Captures, encodes VP8 and accepts input, proven between two ' +
              'in-process peers through the real broker. No Android controller ' +
              'has rendered it, and TURN has not been exercised.'
      },
      windowsController: {
        label: 'Windows controller',
        state: 'planned',
        badge: 'Planned',
        tone: 'soon',
        note: 'Not started. A Windows machine cannot yet drive another device.'
      },
      otherPlatforms: {
        label: 'iOS / macOS / Linux',
        state: 'planned',
        badge: 'Not built',
        tone: 'soon',
        note: 'No implementation exists. iOS cannot host a controlled session at ' +
              'all — the platform exposes no way to inject input into another app.'
      },
      broker: {
        label: 'Signaling broker',
        state: 'verified',
        badge: 'Tested',
        tone: 'ok',
        note: 'Runs, and its protocol and end-to-end behaviour are covered by ' +
              'automated tests that execute on any machine.'
      }
    },

    /* ------------------------------------------------------- distribution */
    /* No signed binary is published anywhere. Every route is a source build. */
    distribution: {
      android: {
        available: false,
        label: 'Build from source',
        note: 'No signed APK is published. CI builds an unsigned debug APK as a ' +
              'workflow artifact; it is a development build, not a release.'
      },
      windows: {
        available: false,
        label: 'Developer build only',
        note: 'There is no Windows installer. The host is built from source with ' +
              'the .NET SDK and run from the build output.'
      },
      broker: {
        available: false,
        label: 'Run from source',
        note: 'Clone the repository and run the broker with Node.'
      }
    },

    /* ------------------------------------------------------------- checks */
    checks: {
      protocol: '229',      // verified: npm run protocol
      broker: '46',         // verified: npm run smoke
      summary: 'Protocol, broker, Android unit and Windows suites run in CI'
    },

    /* ------------------------------------------------------------ notices */
    notices: {
      androidAcceptance:
        'RemoteAssist has not yet been observed running between two physical ' +
        'Android handsets. The code is complete and its unit tests pass; the ' +
        'device acceptance matrix in the repository is still entirely open.',
      windowsPreview:
        'The Windows host streams and accepts input, but no Android controller ' +
        'has rendered its desktop and no session has crossed a network. Treat it ' +
        'as a preview.',
      turn:
        'TURN has not been exercised from Windows, and no cross-network session ' +
        'has been recorded on any platform. RemoteAssist is not yet demonstrated ' +
        'as Internet-ready remote control.',
      pricing:
        'RemoteAssist has not been commercially released and no prices have been ' +
        'set. The self-hosted software is MIT licensed and free.',
      accounts:
        'There is no account system. Devices authenticate with their own ' +
        'hardware-backed keys, so there is nothing to sign in to.',
      contactForm:
        'Online submission is not enabled: this site is static and the form is ' +
        'not wired to any endpoint. Nothing you type is transmitted or stored.'
    },

    /* --------------------------------------------------------------- repo */
    repo: {
      url: 'https://github.com/Vivek202509/remoteassist',
      matrix: 'https://github.com/Vivek202509/remoteassist/blob/main/docs/CROSS_PLATFORM_TEST_MATRIX.md',
      deviceMatrix: 'https://github.com/Vivek202509/remoteassist/blob/main/docs/GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md'
    }
  };

  /* Resolve "platforms.android.badge" against the object above. */
  function lookup(pathExpr) {
    var node = STATUS;
    var parts = String(pathExpr).split('.');
    for (var i = 0; i < parts.length; i++) {
      if (node == null || typeof node !== 'object' || !(parts[i] in node)) return undefined;
      node = node[parts[i]];
    }
    return typeof node === 'string' ? node : undefined;
  }

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = { STATUS: STATUS, lookup: lookup };   // used by web/test/qa.js
  }

  if (typeof document === 'undefined') return;
  window.RemoteAssistStatus = STATUS;

  /* Text nodes. */
  Array.prototype.forEach.call(document.querySelectorAll('[data-status]'), function (el) {
    var value = lookup(el.getAttribute('data-status'));
    if (value !== undefined) el.textContent = value;
  });

  /* Badges: text plus the tone class, so a status change repaints the pill. */
  Array.prototype.forEach.call(document.querySelectorAll('[data-status-badge]'), function (el) {
    var key = el.getAttribute('data-status-badge');
    var badge = lookup(key + '.badge');
    var tone = lookup(key + '.tone');
    if (badge === undefined) return;
    el.textContent = badge;
    if (!tone) return;
    // Rebuild the class list rather than appending, so repeated runs cannot
    // accumulate "badge badge badge--ok".
    var kept = el.className.split(/\s+/).filter(function (c) {
      return c && c !== 'badge' && c.indexOf('badge--') !== 0;
    });
    kept.push('badge', 'badge--' + tone);
    el.className = kept.join(' ');
  });
})();
