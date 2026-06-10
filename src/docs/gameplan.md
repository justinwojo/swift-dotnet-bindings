# Gameplan

Active, session-oriented plan for the next stretch of work. Each session is
self-contained and run end-to-end (red fixture → fix → gates → done). Ordered by
value. Line numbers drift — grep/re-confirm before editing.

Source docs distilled here: `audits/REMAINING-WORK.md`, `apple-framework-deferred-work.md`,
`roadmap.md`, and the adversarial re-verification in
`audits/phase2-triage-fable.md` (Fable, 2026-06-09) — which promoted several items
out of the "latent, don't queue" pool by finding live emission sites. Findings tagged
`Fxx` below reference that report. Most of the Future/ docs are deferred-or-landed and
are **not** in this plan (see "Deliberately not sessions").

---

## Tier A — live correctness (do first)

### Session 1 — EC2+ composition owned-return proxy runs a direct `CallConvSwift` VWT destroy on the finalizer thread — ✅ DONE

*Source: triage **F1** (verified end-to-end in the main line). The headline find — a
live emission of the #1 confirmed crash class, not a theoretical latent.*

**Resolution.** `ModuleHandler.cs:1950` swapped to `DestroyWireBufferRetainsFinalizerSafe`
(the EC1 contract). Added 3 finalizer-path probes (inline/boxed/optional EC2) to
`ExistentialReturnLeakProbeTests.cs`. Also corrected 9 stale "EC1 only" ownership-ctor
comments in `WrapperEmitter.Return.cs`/`ExistentialProjection.cs` (EC2+ has owned the
container since prior work). Gates green: sim 2766 (+13), unit 0-fail, device 2778 (+3).
Codex+Grok both confirmed the fix and exonerated the test/comment surface. Note: the
`!ji->async` assertion could **not** be forced deterministically red — it fires only on a
coinciding native fault during signal-unwind (Issue 1), so the probe asserts
no-crash/no-leak rather than reproducing the assertion.

**Problem.** The composition-existential proxy's `ReleaseAdoptedSwiftContainer()`
(`ModuleHandler.cs:1941-1950`) calls `SwiftMarshal.DestroyWireBufferRetains(...)` — a
**direct** `ValueWitnessTable->Destroy` (`CallConvSwift`, `SwiftMarshal.cs:183-190`) — and
is invoked from **both** `Dispose()` (`:1917`) and the finalizer `~Proxy()` (`:1929`). A
direct CallConvSwift VWT call from the GC finalizer thread crashes Mono with the
`!ji->async` assertion (upstream Issue 1; see memory `feedback_mono_jit_blame.md`). The
EC1 proxy emitter avoids exactly this by routing the identical body through
`DestroyWireBufferRetainsFinalizerSafe` (the `SBW_VWTDestroy` `@_cdecl` trampoline,
`SwiftMarshal.cs:230-237`), with a comment naming the hazard.

**Live in current output.** `SwiftBindingsTestLib.cs` — 3 composition proxies with
`~Proxy()` → `ReleaseAdoptedSwiftContainer()` → direct `DestroyWireBufferRetains`
(`AgeableAndNameableProxy`, `DescribableAndTestIdentifiableProxy`,
`LabelableAndRenderableProxy`), each with `ownsContainer: true` owned-return constructions.

**Activation is runtime, not generator-input.** Gates stay green only because the existing
tests dispose deterministically. The crash needs: on Mono (sim/Catalyst), obtain an owned
EC2/EC3 composition existential return, drop it without `Dispose()`, force GC + finalization.

**End-to-end.**
1. **Red fixture first** — an owned EC2/EC3 composition existential return dropped without
   `Dispose()`, forced through finalization on Mono; assert it does NOT crash. (Mirror the
   EC1 finalizer-path probe.)
2. Swap `ModuleHandler.cs:1950` from `DestroyWireBufferRetains` to
   `DestroyWireBufferRetainsFinalizerSafe` (the EC1 contract). Confirm the `Dispose()` path
   is fine either way; the finalizer path is the one that must be FinalizerSafe.
3. Gates: `nuke test` + `nuke binding-tests` + **`--device`** (CC-adjacent / ARC). Note the
   crash is Mono-specific (sim/Catalyst), so the sim run is the primary signal here.

---

### Session 2 — Noncopyable `consuming`/`borrowing` self gets a real `@_cdecl` wrapper — ✅ DONE

*Source: `audits/REMAINING-WORK.md` §1 #2b. The only reachable "worth fixing" audit item.*

**Problem.** The 6 noncopyable instance methods (`UniqueResource.consume/inspect`,
`FileHandle.close/getDescriptor/isOpen`, `TrackedResource.peek`) silently degrade to raw
`CallConvSwift` instead of a `CallConvCdecl` `SBW_…` wrapper. They work today at runtime,
so this is ABI *hardening*, not a live crash — but it carries real double-free risk if
fixed naively (`~Copyable` types with `deinit`).

**Root cause (code-traced).**
1. Ownership dropped at parse: `SwiftABIParser.cs:2001` stores `IsMutating` only;
   `funcSelfKind: "Consuming"`/`"Borrowing"` are discarded.
2. Self-reconstruction copies a `~Copyable` value: `MethodWrapperEmitter.cs:500-501` uses
   `.pointee` (a borrow); a `consuming` method on it requires ownership, so the wrapper
   fails Swift compile and is stripped → C# degrades to `CallConvSwift`.

**End-to-end.**
1. **Red fixture first**: assert each method round-trips AND the generated C# uses
   `CallConvCdecl` (no SB0001); add a `deinit`-runs-exactly-once probe for `consuming` self
   (mirror the `TrackedResource` parameter probe).
2. Add `IsConsuming`/`IsBorrowing` to `MethodDecl`; parse from `funcSelfKind` in
   `SwiftABIParser.cs` (and the accessor path at `:2470` if relevant). Keep readers in sync.
3. Self-reconstruction in `MethodWrapperEmitter`: **consuming self** →
   `…assumingMemoryBound(to:).move()` + mark the C# `SwiftSafeHandle` consumed (the
   handle-consumed contract the `TrackedResource` *parameter* path already implements).
   **borrowing self** → a true borrow through the pointer, no copy.
4. Verify the exact Swift form via SIL (`feedback_verify_swift_abi_sil.md`) and an
   independent consult before committing — ownership errors here are double-frees. **This is
   the session gated on a Fable design review** (see "Fable" at the bottom).

**Gates.** `nuke test` + `nuke binding-tests` + **`--device`** (NativeAOT — CC change).
Durable gate: the existing `OwnershipTests`/`NegativePathTests` cases + the CC assertion +
deinit probe.

**Resolution.** Root cause was deeper than the parse-time drop: the swift-syntax walker
func-shape matchers (`AvailabilityWalker`, `ExtensionsWalker`, `MemberCollectionWalker`,
`SignatureFactsWalker`) omitted `consuming`/`borrowing` from their allowed-modifier sets, so
the 6 public methods fell into negative-space (`IsModuleInternal=true`) and never got a
wrapper. Fixed all five matchers (+ their mirrored C# regexes), added `IsConsuming`/
`IsBorrowing` to `MethodDecl` parsed from `funcSelfKind`, and taught `MethodWrapperEmitter`
to reconstruct self correctly: **consuming** → `.assumingMemoryBound(to:).move()` + a C#
`SwiftSafeHandle.MarkConsumed()` so `ReleaseHandle` frees the buffer without a second VWT
destroy (no double-free); **borrowing** → a true borrow through `UnsafeRawPointer.pointee`,
no copy. A use-after-consume guard (`if (_payload.IsConsumed) throw ObjectDisposedException`)
fails fast where Swift's move checker would; property/subscript accessors inherit it
transitively (their backing methods route through the same `WrapperEmitter` path), pinned by
the `GuardedResource` fixture. Gates: `nuke test` 0 failed (12724/21/592), `nuke binding-tests`
sim ALL PASSED, device validated on NativeAOT (consuming deinit-runs-once). Codex+Grok r1/r2:
fixed the use-after-move High, extension-parity corpus, and comment drift; Grok's
property/subscript-guard-gap High was proven a false alarm via the transitive-coverage fixture,
and Codex's concurrency-TOCTOU note documented as a known limitation (mirrors Swift's
single-threaded move checking, out of scope).

---

## Tier B — real consumer exposure, contained fixes

### Session 3 — Identifier/keyword guard for SwiftUI-bridge params and EveryProtocol member names

*Source: triage **F3** (= §4 T1-6c, promoted) + **F13**. Two sites missing the escape
guard every other emitter already applies.*

**Problem.**
- **F3 (SwiftUI bridge):** `SwiftUIBridgeEmitter.cs:2720-2722` emits `$"{type} {param.Name}"`
  with names flowing raw from `SwiftABIParser.cs:2181` — zero keyword-escaping in any of the
  three bridge emitter files. A third-party View with an init param named a C# keyword
  (`init(event:)`, `init(delegate:)`, `init(string:)`) produces uncompilable C# (missing `@`).
  Swift-keyword names break the Swift side symmetrically.
- **F13 (EveryProtocol):** 11 sites emit `public var/func {member.Name}` raw
  (`EveryProtocolEmitter.cs:2441…4241`); parameter labels ARE escaped (and tested via
  `SiblingPropertyDispatch.swift`), but member NAMES are not. A keyword-named protocol member
  → invalid Swift or a non-conforming `_`-rename.

Other emitters guard correctly — `MethodWrapperEmitter.cs:977`,
`ConstructorWrapperEmitter.cs:1249`, `EveryProtocolEmitter.cs:5411` — so the fix is applying
the existing helpers at the unguarded sites.

**End-to-end.** Red fixtures first: a keyword-named View init param (F3) and a keyword-named
protocol member (F13). Apply the existing escape helpers at the emission sites. Gates:
`nuke test` + `nuke binding-tests`. (No CC change — sim run suffices.)

---

### Session 4 — CryptoKit HPKE construction (init-specialization factories)

*Source: `apple-framework-deferred-work.md` T2.2 (PARTIAL). Real feature completion.*

**Problem.** `Seal`/`Open`/`ExportSecret` instance methods already bind end-to-end, but all
10 `HPKE.Sender`/`Recipient` initializers drop as SB0001 stubs ("C# does not support generic
constructors with method-own type parameters"). Construction blocked → the already-emitted
overloads are unreachable in practice.

**Root cause.** The CSM specialization path runs for instance methods but not for
initializers carrying method-own generic type parameters.

**Fix.** Extend the CSM engine to emit `public static Sender From{Conformer}(...)` factories
for method-own-generic inits — per conformer of the key-constraining protocols
(`Curve25519.KeyAgreement.PublicKey`, `XWingMLKEM768X25519.PublicKey`, the P256/P384/P521
KeyAgreement keys, …). **Reuses the conformer set already exercised** by the instance-method
specialization — existing enumeration in a new (init) context.

**Done when.** A BindingTest round-trips an HPKE `Sender` end-to-end (construct → Seal → Open
via a Recipient), and a CSM unit test asserts a 3+-segment-`ModuleQualifiedName` conformer
emits a *constructor* factory.

---

## Tier C — hardening (latent, defensive, red-fixture-first)

### Session 5 — SDK-targets second-slice atomicity (bridge + wrapper) + fingerprint gaps

*Source: §2.6 (REMAINING-WORK) + triage **F2** (worse sibling) + **F12** (fingerprint gaps).
Grouped — one SDK-targets session.*

**Problem.**
- **§2.6 (bridge):** `_CompileAppleFrameworkSecondBridgeSlice` (`Sdk.targets:1144`) commits via
  `RemoveDir` + `mv` (`:1257-1261`), not the merger's rename-aside protocol
  (`WrapperXCFrameworkMerger.cs:51-67,173-184`). A drop-incomplete repair (SWIFTBIND052) ships
  since 2026-05-30, but residual windows remain: a kill between RemoveDir and mv → **silent**
  bridge loss (repair is `Exists()`-gated, never fires); kill mid-RemoveDir → torn tree can
  pass the dir-count guard.
- **F2 (wrapper — worse):** `_CompileAppleFrameworkSecondWrapperSlice` (`Sdk.targets:884`)
  commits the same non-atomic `RemoveDir` (`:1103-1104`) + `mv` (`:1105-1107`) with **NO
  incomplete-drop guard at all** (no SWIFTBIND053). A kill in the window loses the wrapper
  xcframework; the fingerprint stamp was written pre-swap (`:639-643`), so the next incremental
  build is "up to date", re-probes existence, records `HasWrapperXCFramework=False`, and
  silently drops the consumer `NativeReference` → `DllNotFoundException` for every wrapper-backed
  API.
- **F12 (fingerprint):** three generation inputs missing from the Sdk.targets fingerprint echoes
  — `$(SwiftFrameworkType)` (flips `--objc`, missing from XCFramework-mode echo `:602`),
  `%(SwiftAppleFrameworkTarget.NamespacePattern)` (missing from Apple-mode echo `:625`),
  `$(SwiftAppleSupplementVersion)` (missing from both). Each → stale-reuse on an in-place flip.

**End-to-end.** Adopt `WrapperXCFrameworkMerger.MergeFatSlices`' staging→atomic-swap +
`.superseded` recovery in **both** second-slice targets (or `mv aside && mv in && rm aside` so
no window has zero live trees). Add the three missing fingerprint inputs. Add an
Apple-framework-with-SwiftUI-bridge leg to X64SimGate (its StoreKit leg has no bridge, so the
gap is currently unexercised — confirmed). Gates: SDK/pack tests + **`--device`**.

---

### Session 6 — Sibling emission-marker name-keying hardening (+ witness-getter error wrap)

*Source: `apple-framework-deferred-work.md` T2.6 + T2.5. Both latent; bundled because both are
small red-fixture-first emitter-marker passes.*

**T2.6 — marker keying.** SetVtable/ObjCBase/EntityBase/Conformance markers still key on simple
`.Name` while the witness-getter marker was re-keyed to `ModuleQualifiedName`. A local protocol
and a cross-module parent with the same simple name can collide and mis-gate a cross-module
proxy. SetVtable/ObjCBase/EntityBase are low-risk single-site re-keys; **Conformance is the
delicate one** (read at 3 sites incl. a cross-decl ancestor lookup) — do it only with the red
fixture green.

**T2.5 — witness-getter error wrap (second shape).** Generator emits the getter optimistically,
the Swift wrapper fails to compile it, the give-up pass drops the `@_cdecl`, but the emission
marker is set so the C# proxy still P/Invokes → `EntryPointNotFound` at the callback boundary
instead of a clean `NotSupportedException`. Reproduces with `ProtocolExtOptionalClassParam.swift`
(`PExtOptChildProtocol`). Wrap the getter P/Invoke in `GetWitnessTableFromSwift()`. **Trade-off:**
also masks an unrelated regression's "symbol missing" — decide deliberately with the red fixture
first.

**Gates (both).** Red fixture first for each. unit + `binding-tests --compile-only` +
`--skip-regen` + **`--device`** (vtable/conformance P/Invoke gating).

---

## Needs a probe before it's a session

### F6 — Concrete-class fallback asymmetry: `Optional<Entity>` projects, `[Entity]` drops the member

*Source: triage **F6**. The one finding the BindingTests corpus couldn't settle — needs
validation-library output.*

`TypeProjectionFactory.cs:239` (Optional path) consults `IsConcreteClassFallbackModule`, but
`TryProjectObjCElement` (`:590-611`) does not — element fallback only honors
`IsOptionalFallbackModule` + `HasObjCClassPrefix`. So `RealityFoundation.Entity` /
`RealityKit.AnchorEntity` as an Array/Dictionary/Set **element** → projection null → member
silently dropped, while the same type as `Optional<T>` emits fine. RealityKit/RealityFoundation
are **tier-1 validation libraries**, so a validate sweep can hit this. Severity: silent member
drop (coverage), not a crash. **First step:** a targeted-generation or `nuke validate` probe over
RealityKit element shapes to confirm reach; promote to a session if confirmed.

---

## Queued (lower priority)

### RC-AOT typed mesh buffers on NativeAOT

*Source: `apple-framework-deferred-work.md` T2.1 (OPEN). Niche, device-only, harder.* Root the
`T : Vector3` generic-specialization metadata on NativeAOT (eager-`cctor` à la
`SwiftArray.cs:80-106` under `IsNativeAotRuntime` + `ILLink`/`[DynamicDependency]`). Done when
the 8 RealityFoundation buffer entries run on device. Pick up after the above.

---

## Quick doc/cleanup fixes (fold into any docs pass)

From the triage's doc corrections — none worth a session, all cheap:
- `roadmap.md` says "exactly 4" upstream issues but Issue 4 (Catalyst x64) is missing from the
  Blocked table **and** from the `RuntimeLimitations` enum. ~10-min fix.
- `InitAnalyzer.cs:481-483` comment names a CLASS as an `IsObjCBridgeable` example — stale prose
  (no class record can carry the flag today).
- REMAINING-WORK §2.2 "19 owned-carrier conversions" → 23 on current output; T1-4 "23 duplicated
  loops" → ~18. Update if touching those rows.
- Tier-2 "async non-throwing hang" is now **mitigated** (fault-catch at all 7 sites) — drop from
  the pool. "same-type-dependent unsupported" now parses+emits correctly for the reaching shape.

---

## Logged latents (do NOT re-discover; fix only if a reaching shape lands)

From triage §1 + the audit §2/§4 — confirmed unreachable or narrow, evidence in
`audits/phase2-triage-fable.md`:
- **§2.1–2.5** — confirmed unreachable today (mechanisms live, zero emission). Keep latent.
- **F4** — async `CreateAsync` drops `IsSimpleEnum` (`AsyncPattern.cs:1271`); complex enum leaf
  emits `(int)<class instance>`. Compile-break severity, but same zero-current-fixture status as
  §2.3. Promote if an async-pattern View with a complex-enum leaf lands.
- **F5** — co-gater "never emits unbalanced braces" premise refuted: Swift string defaults pass
  verbatim (`SwiftDefaultValueMapper.cs:75-76`), so a third-party `= "{"` default breaks stateless
  brace walkers. Current corpora clean (0/375k). Log next to the §3 co-gater entry.
- **F7–F10, F14** — protocol-emitter narrow latents (CancellationToken key gap, Vtables skip-key
  gap, duplicate-subscript proxy skip, subscript WasEmitted/override parity, CSM trim-pool null
  propertyNames). Batch with a protocol-emitter session if one opens.
- **F11** — demangler `Yt` (`_const`) unhandled, hit by 4 real mangled names; benign today.
- **F15** — `IsOptionalObjCBridged` missing 3 of the factory's 9 guards (2 masked, 1 theoretical);
  the constraints-file "must match exactly" assertion is literally false today — worth a parity
  test even if behavior-neutral.
- **F16** — enum payload containing `Optional<Closure>` routes through `OptionalProjection`'s
  generic fall-through; **Inconclusive** — needs a compile-probe fixture to confirm or refute.
- **F17** — value generics (`<let N : Int>`) have no gate (parameter packs do); zero presence
  today. Add a gate opportunistically when touching `GenericTypeEmitter`.

---

## Deliberately not sessions

- **Audit §4 Tier 3** — not triaged at all (duplicated ladders/walkers, L2 ObjC pipeline audit,
  L3 perf/API-drift, L1 docs-drift). Requires re-measurement + **owner sign-off before any
  capped fix plan**.
- **`cross-package-nuspec-dependencies`** — SDK fix LANDED; only an owner-driven republish remains.
- **`private-framework-dependencies-plan`** — YAGNI-deferred until 2–3 real vendor cases.
- **`post-1.0-architecture-roadmap`** — maintainability debt, gated on 1.0 + a trigger.
- **`regression-matrix-performance` Round 2** — explicitly parked; diminishing returns.
- **`api-snapshot-tooling` / `interop-performance-validation-plan`** — P3 greenfield tooling.

---

## Standing rules (apply to every session)

- Verify before fixing — reproduce with a red fixture first (maximum-case;
  `feedback_tdd_for_regression_fixes.md`). No patch-on-suspicion.
- After any generator edit, rebuild `src/Swift.Bindings/src -c Debug` before regen — the gates
  run from `bin/Debug/` and won't rebuild a stale dll
  (`feedback_stale_release_binary_masks_regen.md`).
- Zero-regression: `nuke test` + `nuke binding-tests` pass counts ≥ baseline. Add `--device` for
  any calling-convention / ARC / marshalling / vtable-gating change.
- New work ships with tests at the right layer (parser/emitter → unit; ABI/marshalling/CC →
  BindingTests, the durable gate).
