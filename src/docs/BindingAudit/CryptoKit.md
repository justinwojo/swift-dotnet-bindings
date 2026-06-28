# CryptoKit — Binding Audit

- **Package**: SwiftBindings.Apple.CryptoKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2 / macos / maccatalyst / tvos
- **Native**: Apple CryptoKit (Xcode SDK / iOS 26.2 / macOS 26.2)
- **Audited at**: main 1e8c27a, generated 2026-06-27

## Verdict

The binding delivers a working crypto toolkit for the common path: AES-GCM, ChaChaPoly, SHA-2/SHA-3 hashing, HMAC one-shot, Ed25519 (Curve25519) signing, ECDH on all four curves, post-quantum ML-DSA-65 and ML-KEM-768/1024, and HKDF key derivation (undocumented, see §1). Types coverage is 116/119 (97.5%); member coverage is 305/379 (80.5%) raw. The headline gap — and a concrete documentation bug — is **P256/P384/P521 ECDSA signing and verification**: the binding emits only an `[Obsolete]` generic stub that cannot be called with `byte[]` from C#, while the CRYPTOKIT-GUIDE.md incorrectly claims these work. All digest and HMAC types also lack a raw `byte[]` accessor (`withUnsafeBytes` is globally skipped). The SHA hashing round-trip (the most fundamental operation) has zero test coverage.

---

## 1. Coverage

### Totals

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 116 | 119 | 97.5% |
| Members (from Swift) | 305 | 379 | 80.5% |
| SynthesizedMembers (generator-added) | 198 | — | — |
| SkippedMembers | 152 | 379 | — |

`EmittedMembersByKind`: Method 131, Property 167, Operator 7.  
`SkippedMembersByKind`: Method 134, Type 12, Operator 4, Property 2.

---

### Skip-reason breakdown (155 skip records)

| Reason | Count | Classification |
|---|---|---|
| UnsupportedSignature | 99 | Correctly excluded — generic constructors with method-own type params (e.g. `init<Bytes: ContiguousBytes>`) |
| GenericProtocolConstraint | 18 | **Real gaps** — see below |
| UnsupportedClosure | 13 | **Real gaps** — `withUnsafeBytes` across all digest/MAC/key types |
| EveryProtocolConformanceSkipped | 12 | Correctly excluded — protocol proxy types not emitted (EveryProtocol limitation) |
| StaticProtocolMember | 8 | Correctly excluded — protocol-level `==` operators and `init` requirements can't be declared in C# interfaces; concrete-type operators are emitted separately |
| OwnedByAppleSupplement | 3 | Correctly excluded — `P256/P384/P521.Signing.ECDSASignature` owned by SwiftBindings.Apple supplement |
| SwiftUIConstraint | 2 | Correctly excluded — `SecureEnclave.P256.Signing.PrivateKey.signature` references SwiftUI |

---

### (a) Correctly excluded — no action needed

- All 99 `UnsupportedSignature` skips: key-type `init<Bytes: ContiguousBytes>` / `init<D: DataProtocol>` overloads. The CSM engine emits concrete `byte[]` and `Foundation.Data` overloads for the usable specializations; the open-generic forms are genuinely unsupportable.
- `EveryProtocolConformanceSkipped` proxy types: these represent protocols (`HashFunction`, `Digest`, `MessageAuthenticationCode`, etc.) that the EveryProtocol engine didn't synthesize conformances for (no decision recorded, or `ConstructorRequirements` blocker). The concrete conforming types are fully emitted and usable.
- `OwnedByAppleSupplement` (ECDSASignature types) and `SwiftUIConstraint` skips: deliberate project-level decisions.

---

### (b) Real gaps

**Gap 1 — P256/P384/P521 ECDSA signing and verification (HIGH)**

| Skipped method | Skip reason |
|---|---|
| `CryptoKit.P256.Signing.PrivateKey.signature` | GenericProtocolConstraint (comment at `CryptoKit.cs:16052`) |
| `CryptoKit.P384.Signing.PrivateKey.signature` | GenericProtocolConstraint |
| `CryptoKit.P521.Signing.PrivateKey.signature` | GenericProtocolConstraint |
| `CryptoKit.P256.Signing.PublicKey.isValidSignature` | GenericProtocolConstraint |
| `CryptoKit.P384.Signing.PublicKey.isValidSignature` | GenericProtocolConstraint |
| `CryptoKit.P521.Signing.PublicKey.isValidSignature` | GenericProtocolConstraint |

For each of these, only the open-generic `[Obsolete]` fallback was emitted — e.g., `CryptoKit.cs:16055`:
```csharp
[Obsolete("No @_cdecl wrapper or native thunk available…", DiagnosticId = "SB0001")]
public Swift.CryptoKit.P256.Signing.ECDSASignature Signature<D>(D data) where D : ISwiftObject
```
`byte[]` does not implement `ISwiftObject`; calling `signingKey.Signature(byteArray)` fails to compile. There is no usable P256/P384/P521 ECDSA sign or verify path in C#. **The CSM engine does produce concrete `byte[]`/`Foundation.Data` overloads for Curve25519.Signing.PrivateKey** (see `CryptoKit.cs:686,707`) because its Swift return type is `Foundation.Data` rather than a typed `ECDSASignature` struct — that pattern is fully concretizable. For NIST curves the CSM engine did not emit the same concretization, leaving the entire ECDSA signing surface unreachable.

**Worth fixing**: ECDSA P256/P384/P521 sign + verify is the single most common PKI operation in the crypto ecosystem. The CSM engine's `byte[]`/`Foundation.Data` concretization that already works for Curve25519 should be extended to P256/P384/P521. Generator effort: medium (the DataProtocol→byte[]/Data specialization pattern is established; the return-type marshal for ECDSASignature needs the same treatment as the HMAC `HashedAuthenticationCode`).

Also see §2 — the CRYPTOKIT-GUIDE.md falsely documents these as callable.

**Gap 2 — `withUnsafeBytes` on all digest, MAC, nonce, key, and SharedSecret types (MEDIUM)**

All 13 `UnsupportedClosure` skips cover `withUnsafeBytes(_ body: (UnsafeRawBufferPointer) throws -> R) rethrows -> R`:

`HashedAuthenticationCode`, `SHA256Digest`, `SHA384Digest`, `SHA512Digest`, `SHA3_256Digest`, `SHA3_384Digest`, `SHA3_512Digest`, `Insecure.SHA1Digest`, `Insecure.MD5Digest`, `AES.GCM.Nonce`, `ChaChaPoly.Nonce`, `SymmetricKey`, `SharedSecret`.

There is no alternative raw `byte[]` accessor on any of these types. The only way to read digest bytes is `Description` (a hex-string `"SHA256 digest: 2cf24d…"`). Consumers who need to pass a SHA-256 hash to another .NET API (e.g., `System.Security.Cryptography.RSA.VerifyHash`) have no interop path.

**Worth fixing**: The generator should synthesize a `byte[] ToByteArray()` convenience on each `*Digest`, `HashedAuthenticationCode`, and `*Nonce` type that calls a `@_cdecl` Swift wrapper exposing the bytes via `withUnsafeBytes`. This is a thin Swift-wrapper that converts the generic closure form into a concrete `@_cdecl(UnsafeRawPointer, Int) -> Void` and a C#-side `NativeMemory.Alloc` + copy. Medium effort; high consumer value.

**Gap 3 — HKDF `extract` and `expand` (LOW)**

`HKDF.extract` and `HKDF.expand` are GenericProtocolConstraint-skipped (`CryptoKit.cs:23634,23635`). The standalone step-by-step HKDF extraction and expansion flows are unreachable. `HKDF.DeriveKey` IS available via CSM (see Positive in §1 below); only the raw intermediate steps are blocked. Low priority.

**Gap 4 — `SharedSecret.hkdfDerivedSymmetricKey` / `x963DerivedSymmetricKey` (LOW)**

Both GenericProtocolConstraint-skipped. Callers must use `SymmetricKey.FromCryptoKit_SharedSecret(secret)` as the derivation bridge. The function signature in Swift is `func hkdfDerivedSymmetricKey<H: HashFunction, Salt: DataProtocol, Info: DataProtocol>(...)` — the double-generic constraint makes CSM concretization non-trivial. Low priority.

---

### Positive finding — HKDF.DeriveKey IS emitted (undocumented)

`HKDFSHA256CsmExtensions.DeriveKey(...)`, `HKDFSHA384CsmExtensions.DeriveKey(...)`, and `HKDFSHA512CsmExtensions.DeriveKey(...)` are emitted at `CryptoKit.cs:23639,23778,…` with all `byte[]`/`Foundation.Data` salt/info cartesian overloads (7 overloads per hash variant). **CRYPTOKIT-GUIDE.md states "not projected" for HKDF.DeriveKey — that is wrong**. The GUIDE should document this as a working API; it's among the most valuable key-derivation primitives in the library.

---

### Prioritized generator unlocks

| # | API | Impact | Tractability | Notes |
|---|---|---|---|---|
| 1 | P256/P384/P521 ECDSA Signature + IsValidSignature concrete byte[]/Data overloads | High — core PKI signing unblocked | Medium | CSM pattern established on Curve25519; extend to ECDSASignature return type |
| 2 | `withUnsafeBytes` → synthesized `ToByteArray()` on Digest/MAC/Nonce/SymmetricKey/SharedSecret | High — interop with .NET crypto APIs | Medium | Needs a thin Swift `@_cdecl` wrapper + C# copy helper |
| 3 | `SharedSecret.hkdfDerivedSymmetricKey` / `x963DerivedSymmetricKey` CSM | Medium — ECDH→AES workflow | Hard | Double-generic constraint; may need manual Swift wrappers |

---

## 2. C# Quality

### Naming / shape

- **PascalCase and namespaces**: correct throughout. Swift "caseless enums" (`AES`, `ChaChaPoly`, `Insecure`, `P256`, `P384`, `P521`, `HPKE`, `Curve25519`, `MLDSA65`, `MLKEM768`, `MLKEM1024`) are correctly projected as child namespaces of `CryptoKit`. The GUIDE warns consumers to add `using` aliases; that is the right guidance.
- **`FinalizeSwift()`** (`CryptoKit.cs:25273, 25563, 25853, 2899, 3182, 3465`): renamed from the Swift `finalize()` to avoid the C# reserved `Finalize` finalizer method. Slightly jarring but necessary and documented.
- **`FrombyteArr_`** (`CryptoKit.cs:16133, …`): the trailing underscore in `FrombyteArr_` is visually noisy and machine-generated looking. The guide uses it throughout but a rename to `FromBytes` would be cleaner. Low priority cosmetic issue; the API works.
- **`HKDFSHA256CsmExtensions`** (`CryptoKit.cs:23639`): emitted but not documented in the GUIDE. Discoverable via IDE autocomplete but absent from any prose. The class name is consistent with the CSM engine convention; the gap is documentation-only.

### Documentation bug — GUIDE incorrectly describes ECDSA signing as callable

`CRYPTOKIT-GUIDE.md` "Digital signatures" section, signing-members table:
```
| `.Signature(byte[] data)` | `…Signing.ECDSASignature` | sign raw bytes |
| `.Signature(Foundation.Data data)` | …
```
Applied to "P256 / P384 / P521, identical shape." This is false. The generated code at `CryptoKit.cs:16054–16106` has only:
```csharp
[Obsolete("No @_cdecl wrapper or native thunk available…", DiagnosticId = "SB0001")]
public Swift.CryptoKit.P256.Signing.ECDSASignature Signature<D>(D data) where D : ISwiftObject
```
`byte[]` does not satisfy `ISwiftObject`; this call will not compile. The README.md correctly lists "Signature generation (P-256/P-384/P-521 ECDSA `signature(for:)`)" as a residual stub. **The GUIDE must be corrected**: remove the P256/P384/P521 rows from the signing table and route consumers to Curve25519 signing (which IS concrete and callable at `CryptoKit.cs:686,707`).

The code example:
```csharp
using var signingKey = new P256.Signing.PrivateKey();
var signature = signingKey.Signature(message);     // THIS DOES NOT COMPILE
```
…in the GUIDE will produce a compile error in any consumer project.

### Async

No Swift `async` methods in CryptoKit. Not applicable.

### Nullability

- `AES.GCM.SealedBox.Combined` is `byte[]?` (nullable); the non-null variant is `ChaChaPoly.SealedBox.Combined` which is `byte[]`. This matches Swift's API (AES-GCM combined representation can be nil for a box constructed from separate nonce/ciphertext/tag; ChaChaPoly's is always non-nil). Correct.
- Factory methods that can throw (key import, `SealedBox` construction) correctly route failures through `ThrowSwiftError`; no silent null returns.

### Lifetime / IDisposable

All mutable Swift value types (`SymmetricKey`, private-key types, `SharedSecret`, hashers) implement `ISwiftObject` + `IDisposable` (`CryptoKit.cs:44, 742, 3803, 25038, …`). `IDisposable` is present on every type that holds native memory. The guide correctly recommends `using var`. Static singletons (`CryptoKitError.AuthenticationFailure`, `SymmetricKeySize.Bits256`) are cached and correctly not IDisposable.

### Ergonomic gaps

1. No `byte[] ToByteArray()` on digest types — covered in Coverage §1 Gap 2. This is the most ergonomically painful missing piece for interop.
2. `HKDFSHA256CsmExtensions.DeriveKey(SymmetricKey, byte[], byte[], nint)` (`CryptoKit.cs:23704`) requires `nint` for byte count, not `int`. Slightly unfamiliar for C# callers; no semantic issue.
3. `new SymmetricKeySize(nint bitCount)` takes `nint`, not `int`. Same pattern, minor friction.

---

## 3. Test Coverage

**Test file**: `tests/Tests.cs` — 33 enumerated test cases.  
**Platforms**: Simulator (Mono JIT) primary; device (NativeAOT) via the `dynamicCodeSupported` gate on tests 30–31.

### Case inventory

| Tests | Surface | Depth |
|---|---|---|
| 1–5 | SHA256/384/512/Sha3256/SHA256Digest metadata loads | Weak — proves type registration, not ABI |
| 6–9 | SymmetricKey, SymmetricKeySize, CryptoKitError, HPKE.Ciphersuite metadata | Weak |
| 10–14 | CryptoKitASN1Error, HPKE.KDF/KEM/AEAD enum values, CryptoKitError.CaseTag | Medium — proves discriminant mapping |
| 15 | CryptoKitError singleton `.Tag` access | Medium — proves enum payload |
| 16–18 | HPKE.KDF/KEM/AEAD `AllCases` count | Medium — proves collection extension |
| 19–20 | `SymmetricKeySize.Bits128 / Bits256` singleton reachability | Medium |
| 21–30 | P256/P384/P521 key type metadata + AES.GCM.Nonce, ChaChaPoly.Nonce, Insecure.SHA1 | Weak (metadata only) |
| 25a–25c | `SymmetricKeySize.BitCount` round-trip, `SymmetricKey` construction | **Strong** |
| 26 | AES.GCM Seal/Open byte-level round-trip | **Strong** |
| 27 | ChaChaPoly Seal/Open byte-level round-trip | **Strong** |
| 28 | AES.GCM authentication failure (wrong-key Open throws) | **Strong** |
| 29 | AES.GCM Seal with AAD — verifies ciphertext + 16-byte tag length | Medium (Seal only) |
| 30–31 | HMAC<SHA256/SHA384> incremental == one-shot, platform-gated | **Strong** (sim) |
| 32 | MLDSA65 context-string sign + verify round-trip (post-quantum) | **Strong** |
| 33 | Curve25519.Signing (Ed25519) sign + verify + tamper detection | **Strong** |

### Untested surface (significant gaps)

1. **SHA hashing — no round-trip test** (HIGH gap). Tests 1–5 load metadata but no test exercises `new SHA256(); Update(data); FinalizeSwift()` and asserts the digest value against a known answer. This is the single most fundamental crypto operation in the binding and has zero ABI coverage. Add a KAT (Known Answer Test) for SHA-256 over the empty string or "hello" with expected hex.

2. **P256/P384/P521 ECDSA signing + verification** — untestable (generator gap; documented in §1). No action needed at the test layer until the generator fix lands.

3. **Key import / export round-trip** (MEDIUM gap). No test exercises `PrivateKey.FrombyteArr_(bytes)`, `PrivateKey.FromData(data)`, or `PrivateKey.RawRepresentation` / `PublicKey.RawRepresentation`. A key import → sign or agree → compare-with-known-vector test would cover both CSM init and property marshalling.

4. **ECDH key agreement** (MEDIUM gap). No test exercises `Curve25519.KeyAgreement.PrivateKey.SharedSecretFromKeyAgreement(publicKey)` or the P256/P384/P521 equivalents. Add an Alice–Bob ECDH round-trip asserting `alice.SharedSecret == bob.SharedSecret` at the `Description` level.

5. **HKDF.DeriveKey** (MEDIUM gap). `HKDFSHA256CsmExtensions.DeriveKey(key, salt, info, outputByteCount)` is emitted and usable (`CryptoKit.cs:23647`) but has no test. Add a KAT against RFC 5869 test vector.

6. **HMAC `IsValidAuthenticationCode` verify path** (LOW gap). Tests 30–31 prove the incremental `Finalize` path but no test calls `HMACSHA256CsmExtensions.IsValidAuthenticationCode(receivedCode, data, key)`. Add a positive + negative (tampered code) verify test.

7. **AES.GCM Open-with-AAD** (LOW gap). Test 29 seals with AAD but does not call `AES.GCM.Open` on the result (blocked per test comment by an upstream Mono JIT assertion on the generic Open<TAD> overload). Once that overload is fixed or a non-generic one is added, add the matching Open step to test 29.

8. **MLKEM768 encapsulate + decapsulate round-trip** (LOW gap). `CryptoKit.cs:33156,33859` emits `GetEncapsulate()` and `Decapsulate(byte[])` on MLKEM768. No test covers the full KEM round-trip (`encapsulate → wire → decapsulate → assert same sharedSecret`).

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | P256/P384/P521 ECDSA Signature + IsValidSignature not callable (GenericProtocolConstraint) | CSM-emit concrete `byte[]`/`Foundation.Data` overloads (same pattern as Curve25519.Signing) | M | High |
| 2 | C# Quality | CRYPTOKIT-GUIDE.md "Digital signatures" table incorrectly documents P256/P384/P521 `Signature(byte[])` and `IsValidSignature(signature, byte[])` as callable | Correct the guide: remove those rows from the P256/P384/P521 table; note the limitation; update the code example | S | High (consumer-facing bug) |
| 3 | C# Quality | CRYPTOKIT-GUIDE.md says HKDF.DeriveKey is "not projected" — it IS projected via `HKDFSHA{256,384,512}CsmExtensions.DeriveKey(...)` | Update the guide's Known Limitations section and add a "HKDF key derivation" usage section showing `HKDFSHA256CsmExtensions.DeriveKey(key, salt, info, 32)` | S | Medium |
| 4 | Coverage | `withUnsafeBytes` globally skipped — no raw `byte[]` accessor on any Digest, MAC, Nonce, SymmetricKey, or SharedSecret type | Synthesize `byte[] ToByteArray()` via a thin Swift `@_cdecl` bridge on each affected type | M | High |
| 5 | Tests | SHA hashing has zero ABI coverage — no round-trip test for `new SHA256(); Update(); FinalizeSwift()` | Add `SHA256KnownAnswerTest` asserting the empty-string or "hello" SHA-256 hex digest matches the NIST vector | S | High |
| 6 | Tests | Key import/export not tested — `FrombyteArr_`, `FromData`, `RawRepresentation` | Add key-serialization round-trip test (e.g. generate Curve25519 key, export `.RawRepresentation`, re-import, assert sign/agree still works) | S | Medium |
| 7 | Tests | ECDH key agreement not tested for any curve | Add Curve25519 or P256 Alice–Bob ECDH test asserting `sharedSecret.Description` equality | S | Medium |
| 8 | Tests | HKDF.DeriveKey not tested | Add `HKDFSHA256CsmExtensions.DeriveKey` KAT against RFC 5869 vector | S | Medium |
| 9 | Tests | HMAC IsValidAuthenticationCode (verify path) not tested | Add positive + negative verify test alongside tests 30–31 | S | Low |
| 10 | Tests | MLKEM768 KEM round-trip not tested | Add `GetEncapsulate → Decapsulate → assert sharedSecret Description matches` test | S | Low |
