# RemoteAssist — Play Console Reviewer Notes

Paste into: Play Console → App content → App access (test credentials) and the
review-notes field on submission. Replace bracketed placeholders with real values.

---

```
REVIEW NOTES — RemoteAssist

App category: Remote support / remote desktop (comparable to AnyDesk, TeamViewer
QuickSupport). Requires two devices: one "controller" and one "host."

How to test (test setup provided):
1. Install on two devices, or use the two pre-provisioned test accounts below.
2. Device A (host): tap "Share my screen" to generate a 6-digit code.
3. Device B (controller): tap "Connect," enter the code.
4. Device A shows a CONSENT dialog and the Android screen-capture system dialog — both
   must be approved by the host. Note the persistent notification that appears and stays
   for the whole session.
5. Device B can now view and control Device A. End the session from either device.

Accessibility use: the AccessibilityService is used ONLY to inject taps/swipes/text
during an approved session (remote control). Demo video attached showing enable + use.
It does not read or transmit screen content or data from other apps.

Unattended access (optional): must be set up by the device owner on the host device
behind biometric authentication, references a previously paired+verified device, shows a
persistent "Unattended access ON" notification, and is fully revocable. Steps to test in
the attached document.

Test credentials:
  Controller login: [test-controller@yourdomain.com / <password>]
  Host login:       [test-host@yourdomain.com / <password>]
  (Devices are pre-paired so you can skip the pairing step if desired.)

Demo video: [link]
Privacy policy: [link]
Contact for review questions: [dev@yourdomain.com]
```

---

## Submission attachment checklist

Attach / link these so the reviewer can verify claims without configuring two devices:

- [ ] Demo video (enable Accessibility → consent → MediaProjection dialog → persistent
      notification → control → end). Link pasted into the Accessibility declaration,
      both FGS declarations, and these notes.
- [ ] Two pre-provisioned, pre-paired test accounts (filled in above).
- [ ] Privacy policy URL (filled in above).
- [ ] Written steps to test unattended access, if that feature is included in the build.
- [ ] Contact email for review questions (filled in above).
