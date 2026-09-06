# `protocol/` — the cross-language contract

These fixtures **are** the Techee protocol specification. `docs/PROTOCOL.md` is the
prose that explains them; when the two disagree, the fixtures win.

Three implementations are held to these files independently:

| Language | Implementation | Test | How it reads the fixtures |
|---|---|---|---|
| JavaScript | `server/src/protocol.js` | `server/test/protocol.js` | `fs.readFileSync` from `../../protocol/fixtures` |
| Kotlin | `com.remoteassist.protocol` | `ProtocolFixtureTest` | test-resource classpath, mounted by `app/build.gradle.kts` |
| C# | `Techee.Protocol` | `Techee.Protocol.Tests` | `Fixtures.cs` walks up from `AppContext.BaseDirectory` to find `protocol/fixtures` |

None of the three is generated from the others. That is deliberate. Two
implementations that share code agree by construction and prove nothing; two that
independently satisfy the same vectors have actually been checked against each
other.

## Files

| File | Covers |
|---|---|
| `identity.json` | Device-ID derivation, key/signature encodings, and the three signed transcripts. Includes a committed throwaway P-256 keypair so every language can sign and verify identical bytes. |
| `capabilities.json` | Endpoint metadata parsing, the capability and permission vocabularies, legacy `Scope` → permission migration, and grant evaluation. |
| `control-v1.json` | Control-frame decode/reject/encode vectors for both wire dialects, plus size limits. |
| `pairing.json` | The pairing ceremony: raw (unhashed) ECDH agreement, the safety number two humans read aloud, and both proof transcripts. Includes throwaway identity and ephemeral keypairs for each role. |

## Conventions

**Oversized and pathological payloads are named, not inlined**, so the files stay
readable. A test harness expands them:

| Token | Expansion |
|---|---|
| `"GENERATE:<n>"` | a string of `n` `a` characters |
| `"GENERATE_NESTED:<n>"` | `n` nested JSON arrays, as a parser-bomb probe |
| `"capabilities": "GENERATE:64"` | 64 distinct synthetic capability tokens |

**Vectors named `_comment`, `_note`, `_rule`** are documentation. Harnesses skip
any key starting with `_`.

**`expect: null` means "must be rejected."** For raw vectors it additionally means
*must reject by returning, not by throwing* — control frames are parsed on a media
callback thread, and on an unattended host an exception there kills the machine.

## The keypairs in `identity.json` and `pairing.json`

They are throwaways, generated solely for these files, and are committed on
purpose so all three languages can verify the same signature — and derive the
same ECDH secret — without coordinating at test time. They are not, and must
never become, real device identities.

## Changing the protocol

1. Edit the fixtures.
2. Watch all three suites go red.
3. Make them green one language at a time.
4. Update `docs/PROTOCOL.md`.

A change that cannot be expressed as a fixture is a change that will drift.
