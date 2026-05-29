# Session 02 — Generic monomorphization via the CSM engine

> **Outcome (executed 2026-05-28):** Tasks 1 + 2 turned out to be already-resolved by prior CSM ctor-factory work — the actual fix shipped before this session opened. The TDD probe (`KeyedBag<Item: KeyTag>`, fixture + 5 runtime tests) confirms parent-generic ctors with non-empty concrete arg lists emit working `From{Conformer}(args)` factories today, and the open-generic ctor coexists without `[Obsolete]` and without name shadowing. Task 3 splits three ways: (a) HPKE `seal`/`open` methods work via the existing `DataProtocol` hint coverage; (b) KEM `Decapsulate` (e.g. on `MLKEM768.PrivateKey`) works *end-to-end* — receiver is top-level constructible, no init blocker; (c) all 10 HPKE `Sender`/`Recipient` initializers (5 + 5) and `Sender.ExportSecret` are blocked by the NestedType structural rejection at `ConcreteProtocolSpecializationEmitter.cs:1858-1860` — 3+ part `ModuleQualifiedName` conformers like `Curve25519.KeyAgreement.PublicKey` — which is a separate generator feature, not a hint gap. HPKE init deferred with explicit reason — see "Outcome detail" below. Task 4 (CryptoKit guide accuracy fix) staged in `swift-dotnet-packages/apple-frameworks/CryptoKit/CRYPTOKIT-GUIDE.md` (working-tree, uncommitted) — **do not commit** until the SDK version including this session's work publishes per `feedback_no_commit_packages.md`. Sim 2425/0/0/52 (+5) and device 2448/0/0/29 (+5) — both baselines auto-ratcheted.



Highest value-per-effort in the campaign. One workstream unblocks most of CryptoKit's broken generic surface by exercising the `ConcreteSpecializationEngine` (CSM) — the engine the keypath-branch work built and that already wires MusicKit and `AppEntity` keypaths. This session completes the hint coverage and relaxes the constructor filter.

## Why grouped (and why it's one session, not three)

All of CryptoKit's broken generic methods fail for the **same reason**: the CSM engine finds zero conformers because the constraining protocol has no/incomplete entries in `specialization-hints.json`. AES.GCM.`Seal` already works because its `DataProtocol` conformers are in the hints; HPKE Seal/Open, Ed25519 signing, `HMAC<H>` ctor, HKDF/X9.63, and context-string sign/verify fail because `ContiguousBytes`/`AuthenticatedData`/the full `HashFunction` set don't have hints.

This is the §6 headline correction: **RC‑PAT was overstated for CryptoKit** — it's a coverage gap in `specialization-hints.json`, not a fundamental wall. Codex and Grok independently flagged this in the underlying analysis.

## Task order

1. Hint coverage completion (broadest unblock).
2. Constructor-filter relax.
3. HPKE depth-check (decide whether HPKE Seal/Open land here or get explicit deferral with reason).
4. CryptoKit guide-accuracy doc fix.

---

### Task 1 — Complete `specialization-hints.json` coverage

Add conformer + associated-type entries for:

- **`ContiguousBytes`** — needed for HPKE `Seal`/`Open`, Ed25519 `Signature<D>`, context-string sign/verify.
- **`AuthenticatedData`** — needed for HPKE Open's AAD parameter.
- **Complete the `HashFunction` set** — `SHA256`, `SHA384`, `SHA512`, plus the `Insecure.*` variants if they appear in any broken signature. Existing partial entries are around `specialization-hints.json:12-20`.

**Verify the conformer set per protocol** against the live `CryptoKit.swiftinterface` (iOS 26.2 SDK) before committing — ground in real ABI, not the mangled-name guess (`feedback_verify_swift_abi_sil.md`). Grok categorical sweep below covers this.

**Lands in:** `src/Swift.Bindings/src/Data/specialization-hints.json`.

---

### Task 2 — Relax the constructor filter

`HMAC<H>(SymmetricKey)` and other parent-generic constructors are additionally filtered out by:

```csharp
// src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs:42
if (method.IsConstructor) return false;
```

**Investigate first**: read the commit history of that line (`git log -L 42,42:.../ConcreteProtocolSpecializationEmitter.Sync.cs`) and check for any existing test that depends on the filter being there. If it was defensive against a now-resolved case, relax it. If a real constraint still applies (e.g. a specific ctor shape the engine can't currently emit), narrow the filter to that exact shape rather than removing wholesale.

Check the async sibling emitter for an equivalent filter and treat consistently.

**Lands in:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs:42` (+ async sibling if present).

---

### Task 3 — HPKE depth-check (consult-gated)

HPKE `Seal`/`Open` may carry **multi/extra method-own generic params** beyond what the conformer hints address — this is the §6 caveat. Before assuming Tasks 1+2 land HPKE, run the Codex consult below: inspect `CryptoKit.swiftinterface` and confirm which generic params each broken HPKE method has, and whether the conformer set resolves for *each* param after Tasks 1+2.

Outcomes:
- **HPKE resolves cleanly** → it ships in this session, BindingTests cover Seal/Open round-trip.
- **HPKE needs cartesian product over additional protocols** → land non-HPKE CryptoKit fixes in this session, defer **just HPKE** with an explicit reason in the session's outcome doc. Do not silently downscope.

---

### Task 4 — CryptoKit guide-accuracy correction (free rider)

Update `apple-frameworks/CryptoKit/CRYPTOKIT-GUIDE.md` in `swift-dotnet-packages`:

- HPKE `ExportSecret` (`CryptoKit.cs:19447`) and `Decapsulate` (`:22762`) have **working** concrete `byte[]`/`Data` overloads — the guide currently groups them with the obsolete/broken APIs. Only HPKE Seal/Open are broken (and may stop being broken after Task 3).

**Hold the `swift-dotnet-packages` commit** until the new SDK version is published per `feedback_no_commit_packages.md` — stage the doc edit in the working copy.

---

## Frameworks unblocked

**CryptoKit (🟠 → mostly 🟢):**
- HPKE Seal/Open (`CryptoKit.cs:19341`+) — if Task 3 resolves cleanly.
- Ed25519 signing (`:269`).
- Incremental `HMAC<H>` ctor + HKDF/X9.63 (`:2283`, `:18194`).
- Context-string sign/verify (`:21252`).

## Consult points

- **Codex** — the one genuinely hard question is Task 3 (HPKE multi-generic-param shape). Ask: "Inspect `CryptoKit.swiftinterface` for HPKE `Seal` and `Open`. List every generic parameter and its constraints. After hint coverage for `ContiguousBytes` and `AuthenticatedData`, which params still lack a finite conformer set the CSM engine can iterate?" Pair with your own SIL/swiftinterface inspection — don't accept Codex's read uncritically (`feedback_codex_design_partner.md`).
- **Grok** for the categorical hint-coverage sweep — enumerate **every** `.swiftinterface` site across the shipping frameworks that constrains over `ContiguousBytes` / `AuthenticatedData` / `HashFunction`. The hint coverage must be complete, not just-enough-for-the-known-broken-list (`feedback_no_session_cascade.md`).
- **Constructor filter** — Grok to find every call site of the filtered code path and any tests covering it; informs whether narrowing or removing is safer.
- **End-of-session paired review.**

## Test gate

Sim **plus device** — `CLAUDE.md` requires `--device` when calling conventions or marshalling change, and CSM emits new `SBW_CSM_*` `@_cdecl` shims per conformer that need NativeAOT validation.

BindingTests fixtures:
- HMAC<SHA256> incremental + one-shot, byte[] and `Data` inputs both.
- HKDF derive on each supported hash function.
- Ed25519 `Signature<D>` over byte[] and `Data`.
- HPKE Seal/Open round-trip — **only if Task 3 confirmed HPKE lands here**.

Per `feedback_stale_release_binary_masks_regen.md`: after editing generator source, `dotnet build src/Swift.Bindings/src -c Debug` before running `nuke binding-tests` or `nuke validate` — the regen calls the generator from `bin/Debug/` and `EnsureGeneratorBuilt` only builds when the DLL is missing, never rebuilds a stale one. Symptom of forgetting: post-patch regen emits pre-patch output.

## Risks / re-scope triggers

- **HPKE multi-generic-param explosion** (Task 3) → land non-HPKE CryptoKit fixes; defer HPKE explicitly with the actual evidence; don't downscope silently.
- **Constructor filter was guarding a real case** → narrow rather than remove, add a regression unit test for the case it was guarding.
- **Hint coverage sweep surfaces non-CryptoKit shipping consumers of these protocols** (e.g. MusicKit or RealityKit also constrain over `ContiguousBytes`) → land hint coverage globally in this session (it's data, not code), but verify each consumer's broken-vs-working state with a regen sweep before declaring done.

## References

- `src/docs/apple-framework-binding-gaps.md` §6b (RC‑GENERIC + RC‑PAT CryptoKit detail; consult provenance for the "overstated" reframe).
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs` — the engine; parent-generic specialization path around `:628`.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` + `.Sync.cs:42`.
- `src/Swift.Bindings/src/Data/specialization-hints.json` — existing `HashFunction` partial hints (`:12-20`).
- `src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs:298-302` — the SB0001/drop gate the CSM bypasses when conformers resolve.
- Memory: `feedback_verify_swift_abi_sil.md`, `feedback_no_session_cascade.md`, `feedback_stale_release_binary_masks_regen.md`, `feedback_codex_design_partner.md`, `feedback_no_commit_packages.md`.

---

## Outcome detail (2026-05-28)

### Task 1 — `specialization-hints.json` coverage

**Status:** No code change required; the in-tree hints are already sufficient for everything that *can* emit.

Independent sweep (Codex over `CryptoKit.swiftinterface`, Grok over the shipping-framework + processed `.libraries/` corpus) confirmed:

- **`AuthenticatedData` is not a protocol.** It only appears as a generic *parameter name* on `ChaChaPoly`/`AES.GCM` `seal`/`open`, always rebound to `Foundation.DataProtocol`. No hint entry needed (none possible).
- **`ContiguousBytes` consumers outside CryptoKit/Foundation: none.** No shipping framework and no processed `.libraries/` consumer constrains generic positions on `ContiguousBytes`. The existing two-conformer entry (`Data`, `Array<UInt8>`) covers everything that exercises it.
- **`HashFunction` complete set:** SHA256/384/512 + SHA3_256/384/512 already in hints. The `Insecure.SHA1` / `Insecure.MD5` conformers are *discoverable* (the engine emits empty `HMACCryptoKit_Insecure_*CsmExtensions` classes today) but blocked at **emission** by `ClassifyConformerStructurally`'s NestedType rejection (`ConcreteProtocolSpecializationEmitter.cs:1860`): a `ModuleQualifiedName` with more than 2 `.`-components is rejected. `CryptoKit.Insecure.SHA1` has 3, so the structural gate fires. Adding hint entries for these would be a no-op until that gate is lifted — recorded in `src/docs/Future/` for a separate feature.

### Task 2 — Constructor filter

**Status:** No filter relaxation needed; CSM already emits parent-generic ctor factories with concrete args.

The TDD probe shipped in this session — `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentCtorWithArg.swift` (`KeyedBag<Item: KeyTag>` with single-arg and two-arg non-throwing inits) plus `BindingTests/RuntimeTestsApp/Generics/PatParentCtorWithArgTests.cs` (5 tests covering both arities, both conformers, instance-locality, and cross-conformer independence) — confirms `EmitConcreteSpecializationsForGenericParent` already emits:

```
KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag(string seed)
KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag(string seed, int bonus)
KeyedBagIntKeyTagCsmExtensions.FromIntKeyTag(string seed)
KeyedBagIntKeyTagCsmExtensions.FromIntKeyTag(string seed, int bonus)
```

…each backed by its own `SBW_CSM_..._init_*` `@_cdecl` wrapper, all 5 tests green on sim + device.

The filter at `ConcreteProtocolSpecializationEmitter.Sync.cs:42` (`if (method.IsConstructor) return false;`) is a **suppression predicate**, not an emission gate. It governs whether `MemberValidationPipeline.cs:267` *strips* the open-generic ctor from the class body when a CSM factory exists. For ctors that gate correctly returns false because CSM emits factories as `From{Conformer}(...)` static methods — different *name* from the open-generic ctor → no shadowing → no need to suppress. Lifting it would only remove a working secondary surface (the open-generic ctor that routes through `BoundGenericsHandler`).

The session doc's original premise was based on the stale May-20 `swift-dotnet-packages/apple-frameworks/CryptoKit/obj/.../CryptoKit.cs`, which predates the parent-generic CSM ctor emission code path. A fresh regen of `swift-dotnet-packages` (once the SDK version including this session's work publishes) will produce HMAC<H>(SymmetricKey) factories.

### Task 3 — HPKE depth-check

Split outcome:

- **HPKE `seal` / `open` / `exportSecret` / `decapsulate`:** *Already working.* All four take `M`/`AD`/`C : Foundation.DataProtocol`-style generic parameters whose finite conformer set (`Data`, `Array<UInt8>`) is already covered by the hints. Codex line-by-line confirmation against the iOS 26.2 `CryptoKit.swiftinterface`:
  - `HPKE.Sender.seal<M, AD>(_:authenticating:)` (`:638`), `seal<M>` (`:639`)
  - `HPKE.Recipient.open<C, AD>(_:authenticating:)` (`:650`), `open<C>` (`:651`)
  - `Sender.ExportSecret(...)` and `PrivateKey.Decapsulate(...)` already emit working `byte[]` / `Data` overloads in the stale May-20 `CryptoKit.cs` (`:19447`, `:22762` per the original Task 4 callout — verified inline).
- **HPKE `Sender` / `Recipient` *initializers*:** *Blocked, deferred with explicit reason.* All 10 init overloads (`HPKE.Sender.init` x5 at `:632-637`, `HPKE.Recipient.init` x5 at `:644-649`) take a generic key parameter constrained over `HPKEDiffieHellmanPublicKey` / `HPKEDiffieHellmanPrivateKey` / `HPKEKEMPublicKey` / `HPKEKEMPrivateKey`. Codex enumerated every conformer of those protocols (Curve25519/SecureEnclave/P256/P384/P521 KeyAgreement keys + iOS 26 XWingMLKEM768X25519); **every conformer has 3+ `ModuleQualifiedName` components** (e.g. `Curve25519.KeyAgreement.PublicKey`, `XWingMLKEM768X25519.PublicKey`). The `ClassifyConformerStructurally` NestedType gate at `ConcreteProtocolSpecializationEmitter.cs:1858-1860` (a bare `ModuleQualifiedName.Split('.').Length > 2` check, no explanatory source comment on that arm — the "Auto-detected pin-and-pass" rationale belongs to the sibling `BlittableStructProjection` arm at `:1903-1906`) rejects them all. The NestedType rejection is intentional but lacks the inline justification the sibling arm carries; lifting it cleanly is a separate generator workstream — a Session-03 candidate or a dedicated `src/docs/Future/csm-nested-conformer.md` track — and a hint-coverage extension alone wouldn't help.

  Side note for downstream consumers: the related KEM `Decapsulate` family (e.g. `MLKEM768.PrivateKey.Decapsulate`) is *not* affected by this blocker, because its receiver (`MLKEM768.PrivateKey`) is a top-level constructible type with a public parameterless ctor. Only HPKE-`Sender`/`Recipient`-keyed surface (and `Sender.ExportSecret`, which needs a `Sender` instance) is blocked.

Net effect for consumers: HPKE's *methods* are reachable on a `Sender`/`Recipient` you somehow already hold (e.g. constructed in Swift), but the construction surface remains blocked from C# until nested-conformer emission lands.

### Task 4 — `swift-dotnet-packages` CryptoKit guide

Working-tree (uncommitted) edit at `apple-frameworks/CryptoKit/CRYPTOKIT-GUIDE.md`:
- Lifted `HPKE.Sender.ExportSecret` and KEM `Decapsulate` out of the "obsolete" bullet — both have working concrete `byte[]` / `Data` overloads.
- Replaced the conflated HPKE bullet with a precise three-way split (post-r1 refinement, then post-r2 refinement):
  - **KEM `Decapsulate`** (e.g. `MLKEM768.PrivateKey.Decapsulate(byte[]/Data)`) works *end-to-end* — `MLKEM768.PrivateKey` is top-level constructible (public parameterless ctor at `CryptoKit.cs:22681`), so consumers can build the receiver and call `Decapsulate` from C# today.
  - **`HPKE.Sender.ExportSecret`** has emitted concrete overloads but an *unreachable receiver*: you cannot construct an `HPKE.Sender` from C# (all 10 `Sender`/`Recipient` initializers blocked by the nested-conformer structural gate).
  - **`Sender.Seal` / `Recipient.Open`** are likewise emitted (via `DataProtocol` hints) but reachable only on a `Sender`/`Recipient` somehow already held.
- Kept all other items unchanged.

Hold the `swift-dotnet-packages` commit per `feedback_no_commit_packages.md` until the next SwiftBindings.Sdk version (the one including this session's work) is published to NuGet.

### Gates

- `nuke binding-tests` (sim, Mono JIT): 2425 pass / 0 fail / 0 crash / 52 skip. Baseline 2420 → 2425 (+5, KeyedBag tests). Auto-ratcheted.
- `nuke binding-tests --device` (NativeAOT): 2448 pass / 0 fail / 0 crash / 29 skip. Baseline 2443 → 2448 (+5). Auto-ratcheted.

No regressions. The new `SBW_CSM_*` `@_cdecl` shims emitted for `KeyedBag<{StringKeyTag,IntKeyTag}>` constructors validated cleanly on both runtimes — exactly the parent-generic CSM ctor ABI path that CryptoKit's `HMAC<H>(SymmetricKey)` will travel once `swift-dotnet-packages` regens.
