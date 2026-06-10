# Apple-framework deferred work (post-0.12.0)

The Apple-framework gap-fix campaign closed in 0.12.0: every Tier 1 item shipped on
its gated lane (sim + device), and the Tier 2 items / verification debts that
covered core consumer flows graduated alongside them. What remains is a small set
of lower-impact items deferred to 0.13.0, plus a record of the consciously-parked
"won't fix" boundary so it isn't lost.

This is the single source of truth for that residual surface. Each Tier 2 item
carries a status, root cause, fix direction, and done-criterion so a future focused
pass can pick it up cold.

## Status legend

- **OPEN** — no code shipped yet.
- **PARTIAL** — some of the required code shipped; specific sub-pieces still needed.

## At a glance

| Item | Framework | Status | Target |
|---|---|---|---|
| [T2.1](#t21--typed-mesh-buffers-on-nativeaot-rc-aot) RC‑AOT typed mesh buffers on NativeAOT | RealityFoundation | OPEN | 0.13.0 |
| [T2.2](#t22--cryptokit-generic-remainders-hpke-construction) CryptoKit generic remainders (HPKE construction) | CryptoKit | PARTIAL | 0.13.0 |
| [T2.5](#t25--witness-getter-entrypointnotfound-to-notsupported-wrap-second-shape) Witness-getter `EntryPointNotFound`→`NotSupported` wrap (second shape) | generator | OPEN | 0.13.0 |
| [T2.6](#t26--sibling-emission-marker-name-keying-hardening) Sibling emission-marker name-keying hardening | generator | OPEN | 0.13.0 |

---

<a id="t21--typed-mesh-buffers-on-nativeaot-rc-aot"></a>

## T2.1 — typed mesh buffers on NativeAOT (RC‑AOT) — OPEN

**What's blocked.** `MeshBuffer<T>` / `MeshBuffers.Semantic<T>` / `UnsafeForceEffectBuffer<T>`
generic-specialization metadata resolves on Mono/sim but not on NativeAOT/device — the
constraint-relaxation `T : Vector3` instantiation isn't rooted. RealityFoundation device
shows 29/0/11 (8 buffer entries among the skips). The per-package test capability-gates
on `IsDynamicCodeSupported` (fail-if-regressed on Mono, skip on AOT).

**Fix to land.** Root the `T : Vector3` generic-specialization metadata on NativeAOT.
Emitter pattern: synthesize the eager-`cctor` pattern (analogous to `SwiftArray.cs:80-106`'s
`TryEagerInitialize` call) under `SwiftRuntimeInfo.IsNativeAotRuntime` and emit an
`ILLink` descriptor or `[DynamicDependency]` for generated generic `ISwiftObject` types.
This is RC‑AOT's harder case — it needs more than the `SwiftArray` template pattern
Session 01 used.

**Done when.** The 8 buffer entries run and assert on device (the AOT-lane skip is
removed).

---

<a id="t22--cryptokit-generic-remainders-hpke-construction"></a>

## T2.2 — CryptoKit generic remainders (HPKE construction) — PARTIAL

The structural CSM gates (NestedType, indirect-`Data` return) all landed and the
instance-method side now binds to concrete overloads end-to-end. **HPKE construction
is the one remaining real blocker.** HPKE is a niche modern primitive; the rest of
CryptoKit (signatures, HMAC, AEAD, KDFs, hashing, key exchange) binds correctly.

### What's already done (don't redo)

- NestedType structural gate lifted (`ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally`
  reject for 3+ segment `ModuleQualifiedName`s now passes when the type record resolves).
- HPKE `Seal` / `Open` / `ExportSecret` instance-method specialization landed.
  `HPKE.Sender` exposes concrete `Seal(byte[]/Foundation.Data, byte[]/Foundation.Data)`
  and `ExportSecret(byte[]/Foundation.Data, nint)` overloads (`CryptoKit.cs:22878-23371`);
  `HPKE.Recipient` analogous (`:23372-`). **Unreachable in practice today** because
  HPKE construction is still blocked.
- Data-return CSM concrete overloads landed (Ed25519 signing and the sign-side of
  context-string `Signature<D,C>` bind to concrete `byte[]` via the `InlineSwiftStruct`
  preflight admit).
- Context-string verify (`Bool` return) landed + per-package gated: 8 concrete 3-PAT
  cartesian `IsValidSignature(signature, data, context) → bool` overloads emit on
  `MLDSA65.PublicKey` / `MLDSA87.PublicKey`.

### What's still open — HPKE construction

All 10 `HPKE.Sender` / `HPKE.Recipient` initializers (`CryptoKit.swiftinterface:632-637`,
`:644-649`) and, transitively, the user-reachable use of `HPKE.Sender.ExportSecret` and
`Seal`/`Open`, are still SB0001 / dropped stubs. The regen'd `CryptoKit.cs` shows each
init dropped with:

```
// Unsupported: method 'init' — parameter or return type not yet supported
//   (C# does not support generic constructors with method-own type parameters.)
```

**Root cause.** The CSM specialization path runs for instance methods (`Seal`, `Open`,
`ExportSecret`), but it does **not** run for initializers carrying method-own generic
type parameters. Inits fall through the "C# does not support generic constructors with
method-own type parameters" arm and drop. The structural NestedType lift was necessary
but not sufficient — what's needed is an init-specialization path that emits a
non-generic `From{Conformer}` static factory per conformer (the same shape
`Seal`/`ExportSecret` now use, applied at the constructor site).

**Fix to land.** Extend the CSM specialization engine to emit
`public static Sender From{Conformer}(...)` factories for method-own-generic inits — per
conformer of the key-constraining protocols (`Curve25519.KeyAgreement.PublicKey`,
`XWingMLKEM768X25519.PublicKey`, the P256/P384/P521 KeyAgreement keys, …). The
conformer set is the same one already exercised for HPKE.Sender's instance-method
specialization, so this is reusing existing conformer enumeration in a new context
(init), not discovering a new one.

**Done when.** A BindingTest round-trips an HPKE `Sender` end-to-end (construct → Seal
→ Open via a Recipient), and a CSM unit test asserts a 3+-segment-`ModuleQualifiedName`
conformer emits a *constructor* factory (not just an instance-method factory). At that
point HPKE.Sender's `Seal` / `ExportSecret` concrete overloads — already emitted —
become reachable in practice.

---

<a id="t25--witness-getter-entrypointnotfound-to-notsupported-wrap-second-shape"></a>

## T2.5 — witness-getter `EntryPointNotFound` → `NotSupported` wrap (second shape) — RESOLVED (premise was flawed; landed Option A)

**Resolution:** the documented premise turned out to be empirically false for the only
cited repro, so the defensive wrap was *not* the right fix. Instead the protocol's
genuine C#-implementation CALLBACK path was wired up and proven to work. See below.

**Original (flawed) premise.** This item claimed a second failure shape distinct from
the §5/§5b fail-clean change: the generator emits `Get_EveryProtocol_{P}_WitnessTable`
optimistically, the Swift wrapper then *fails to compile* the conformance (`value of
type 'EveryProtocol' does not conform to specified type 'P'`), the wrapper give-up pass
drops that `@_cdecl` from the dylib, but the emission marker is still set so the C#
proxy P/Invokes the now-absent symbol → `EntryPointNotFoundException` at the CALLBACK
boundary. The proposed fix was to wrap the getter P/Invoke in
`GetWitnessTableFromSwift()` so the exception rethrows as a generic
`NotSupportedException`.

**Why the premise was false (investigated; confirmed by Codex + Grok, high confidence).**
For `ProtocolExtOptionalClassParam.swift`'s `PExtOptChildProtocol`, the conformance the
generator emits is *trivially valid and compiles cleanly* — the protocol's only
requirement is `var nodeId: Int32 { get }`; `attachTo(_:)` is a defaulted extension
method, not a requirement. The raw `.Wrapper.swift` contains the full
`extension EveryProtocol: PExtOptChildProtocol` plus the paired `@_cdecl` getter, and no
`does not conform` error appears in any give-up log. The witness symbol is absent from
the *test* dylib for one reason only: the **test-harness** `SwiftSourceStripper`
(`build/Helpers/SwiftSourceStripper.cs`) drops every non-allowlisted
`extension EveryProtocol: P` block *and* its getter in lock-step, and
`PExtOptChildProtocol` was not in `PreservedProtocols`. The production SDK path
(`SwiftWrapperPostProcessor`) explicitly preserves `Get_EveryProtocol_*` getters and
would export the symbol fine. So there was no generator give-up to defend against — the
"second shape" did not actually reproduce here.

**Why Option A over the wrap.** Landing the defensive `NotSupportedException` wrap would
have turned a real, fixable absence (a protocol the harness simply hadn't preserved)
into a permanent designed-limitation message — masking exactly the class of regression
the project's "ALL runtime crashes are OUR BUGS" culture exists to keep loud. The
correct long-term move was to make the CALLBACK path genuinely work and lock it under a
runtime test, not to paper over a self-inflicted harness gap.

**What landed (Option A).**
1. Added `PExtOptParent.acceptChild(_ child: any PExtOptChildProtocol) -> Bool` to the
   fixture — accepting the existential forces a C# conformer to synthesize it via
   `Get_EveryProtocol_PExtOptChildProtocol_WitnessTable`, and the defaulted `attachTo`
   then reads `child.nodeId` back through that witness table.
2. Added `PExtOptChildProtocol` to `PreservedProtocols` so the harness stripper keeps the
   conformance + getter (per the new-reverse-dispatch-test rule).
3. Added `TestCSharpChildDispatchesNodeIdToSwift`: a managed `IPExtOptChildProtocol`
   (`nodeId = 77`) handed to Swift via `AcceptChild`; asserts the Swift parent observed
   the C#-supplied id through the witness table.

**Verified:** `nuke binding-tests --class-filter ProtocolExtOptionalClassParamTests`
(simulator) — `TestCSharpChildDispatchesNodeIdToSwift` PASS, all 3 in-class tests pass.
(The class-filtered run's tail "regression" is just the baseline comparator seeing 3
tests vs. the 2785 full baseline — expected for `--class-filter`, not a real regression.)

---

<a id="t26--sibling-emission-marker-name-keying-hardening"></a>

## T2.6 — sibling emission-marker name-keying hardening — RESOLVED

The witness-table-getter marker was re-keyed to `ModuleQualifiedName`, but its sibling
markers — **SetVtable**, **ObjCBase**, **EntityBase**, **Conformance** — still keyed on
the simple `.Name`. A local protocol and a cross-module parent protocol with the same
simple name could collide in the shared marker set/dictionary and mis-gate a cross-module
proxy. **Not a reproducing bug today** (no known same-simple-name collision across the
current validation/fixture set; cross-module-parent vtable wiring uses a separate
module-prefixed path), so it was latent. Pure categorical-hardening pass — now landed.

### Background

`ModuleEmissionContext` carries a family of "emitted" markers that `EveryProtocolEmitter`
sets while emitting the Swift wrapper and `ProtocolProxyEmitter` reads while emitting the
C# proxy. They gate whether the proxy emits a given P/Invoke / base class / vtable call.

One member of this family — the **witness-table getter** marker
(`MarkWitnessTableGetterEmitted` / `WasWitnessTableGetterEmitted`) — was changed to key
on `SwiftTypeName.ModuleQualifiedName` (the codebase's canonical unique type identity)
instead of the simple `.Name`:

- Mark: `EveryProtocolEmitter.cs` (inside the local-only `sourceModule` guard).
- Read: `ProtocolProxyEmitter.cs`.
- Store: `ModuleEmissionContext` (`protocolKey` parameter, qualified-name docs).

The witness-getter marker was the one that produced a real, observed crash
(`EntryPointNotFoundException` for read-only / cross-module CALLBACK; see the original
`bug-0.10.0-proxy-vtable-setters-not-exported.md` ledger), and its same-simple-name
reachability was live in the nested-type space the Data-return CSM work exercises — so
it was an in-scope fix.

### The sibling markers (still simple-name keyed)

| Family | Mark | Read | Mark guarded local-only? | Wrong-decision failure class |
|---|---|---|---|---|
| **SetVtable** | `MarkSetVtableEmitted(.Name)` | `WasSetVtableEmitted(.Name)` (1 site) | No | dangling `Set{Name}_vtable` P/Invoke (gated symbol is simple-named / unprefixed) |
| **ObjCBase** | `MarkObjCBase(.Name)` | `UsesObjCBase(.Name)` (1 site) | No | wrong carrier class (gated symbols are hardcoded `SBW_*EveryObjCProtocol*`, already module-unique → not dangling) |
| **EntityBase** | `MarkEntityBase(.Name)` (pre-scan local-only + a second un-guarded site) | `UsesEntityBase(.Name)` (1 site) | Mixed | wrong carrier class + possible over-emit of the `EveryEntityProtocol` Swift class |
| **Conformance** | `RecordConformanceDecision(.Name, …)` | `WasConformanceEmitted(.Name)` at **3** sites: `ProtocolProxyEmitter.StaticInit` (cross-decl `ancestorDecl.Name`), `WitnessDispatchEmitter`, `ProtocolHandler` | No | proxy emit/suppress mis-gate → terminates in the same dangling simple-named Swift symbols |

### Why it is reachable in principle

A single generator run emits **one** bound module (`Program.cs` constructs one
`ModuleEmissionContext`), but within that run the same context processes both the local
module's protocols **and** the cross-module **parent** protocol decls pulled in from
dependencies (`ModuleHandler` cross-module-parent loop). The sibling Mark sites are
**not** behind the local-only `sourceModule` guard that protects the witness-getter
Mark, so a cross-module parent `Dep.Foo` and a local `Foo` collide on the simple key
`"Foo"` in the shared HashSet / dictionary.

### Why it is NOT a reproducing bug today

- It needs a **naming coincidence across the module boundary**: a local protocol and a
  dependency protocol with the **same simple name**, where exactly one drives the
  marker and their emission decisions **differ**. No validation library or
  BindingTests fixture in the current set is known to exercise that.
- The cross-module-parent **vtable wiring** does not depend on these markers: it runs
  through `EmitCrossModuleParentVtableInit` with a **module-prefixed** entry point
  (`GetCrossModuleSetVtableEntryPoint`), so inherited dispatch is correct regardless
  of a simple-name marker collision.
- `ObjCBase`/`EntityBase` gate **hardcoded, protocol-independent** helper symbols
  (`SBW_*EveryObjCProtocol*` / `SBW_*EveryEntityProtocol*`), so a collision mis-selects
  the carrier class rather than pointing at a non-existent per-protocol symbol.

### What landed

All four sibling marker families now key on `SwiftTypeName.ModuleQualifiedName ??
.Name` — the same canonical identity the witness-getter marker adopted — so a
cross-module parent `Dep.Foo` and a local `Foo` no longer share a marker slot.

1. **SetVtable, ObjCBase, EntityBase** re-keyed at both Mark and read sites
   (`EveryProtocolEmitter.cs`, `ProtocolProxyEmitter.cs`). The `EntityBase` pre-scan
   Mark and its second Mark site were re-keyed in lockstep.
2. **Conformance** re-keyed at the recorder (`EveryProtocolEmitter.cs`
   `RecordConformanceDecision`) and at all three readers: `ProtocolProxyEmitter.StaticInit`
   (the cross-decl ancestor lookup), `WitnessDispatchEmitter`, and `ProtocolHandler`.
   The cross-decl ancestor read is always a **local-module** decl (cross-module ancestors
   are skipped upstream), so recorder and reader resolve to the same qualified key; the
   dictionary's last-write-wins behaviour is unchanged.
3. The `?? .Name` fallback preserves the legacy simple-name key when `SwiftTypeName` is
   null (it is genuinely nullable — null-guarded at `EveryProtocolEmitter.cs:1971`).
   `ModuleEmissionContext` parameters were renamed `protocolName` → `protocolKey` with
   module-qualified-key docs; the value-iterating `ConformanceDecisions` readers in
   `EmissionReportEmitter.cs` are key-agnostic and unaffected.

### Why the durable gate is a unit-level collision test (not a BindingTests fixture)

The original plan called for an end-to-end RED fixture reproducing a dangling P/Invoke.
On implementation that proved **not constructible** for the reasons the "Why it is NOT a
reproducing bug today" analysis above predicts: the cross-module-parent vtable wiring
routes through a **module-prefixed** entry point that bypasses these markers entirely,
and `ObjCBase`/`EntityBase` gate **hardcoded, protocol-independent** carrier symbols — so
a simple-name collision mis-*selects* a carrier rather than emitting a non-existent
per-protocol symbol that would fail to link. The defensible RED-first gate for a latent
categorical re-key is therefore at the unit layer, asserting the markers are keyed by
module-qualified identity:

- `ProtocolConformanceCacheTests.EmitProtocolConformance_SameSimpleNameDifferentModules_KeysMarkersByModuleQualifiedName`
  drives the emitter with a local `TestModule.Service` and a dependency `DepModule.Service`
  and asserts both qualified keys are recorded while the bare `"Service"` key is **not** —
  verified RED before the re-key (both collided on `"Service"`), green after.

**Verified.** Unit `Swift.Bindings.Unit.Tests` 12741/0; `binding-tests --compile-only`
Succeeded; `--skip-regen` (simulator) 2786/0/0; `--device` (NativeAOT) 2798/0/0. (A
first `--device` run showed a one-off `BridgeStateUpdateTests` SwiftUI-bridge crash — a
device/NativeAOT UIView main-thread-dispatch timing fluke in a subsystem this change does
not touch; the same generated code passes that class on the simulator, and a clean device
re-run passed with no crash.)

---

# Excluded — won't fix (the finish-line boundary)

Consciously parked. These are architectural or by-design limits, not debts — closing
them is a *different product* or contradicts a framework's own design. Listed here so
the boundary is explicit and survives the rest of the gap-fix documentation cleanup.

- **AppIntents `perform()` / authoring, ActivityKit Live Activities** (RC‑STRUCTURAL) —
  need a C#→Swift source-gen + macro-expansion subsystem (different product); both on
  the `swift-dotnet-packages` do-not-ship list.
- **WeatherKit** statistics/summaries and `weather(for:including:)` — 6-way
  method-own-generic `async` tuple return exceeds the CSM cartesian cap. Full-bundle
  `WeatherAsync` is the workaround.
- **TipKit result-builder DSL** (RC‑AEIC) — entrypoints are shimmable but the authoring
  experience is not restorable from C#.
- **`() -> [T]` result-builder closures + general SwiftUI composition** — the
  `@ViewBuilder` / result-builder wall.
- **RC‑SB0003 reverse witness dispatch** — case-by-case; many are by-design Swift
  limits; the forward (C#-implements) path works and is the supported mechanism.
- **RC‑CLOSURE `@autoclosure`** — no shipping-framework consumer; revisit only if one
  needs it.
- **RC‑PAT app-defined-conformer cases** (e.g. ProximityReader `requestDocument`) —
  CSM only works for Apple-finite conformer sets; app-defined → source-gen territory.
- **RC‑WILLSET** (RealityKit detached setter trap) — framework `willSet` precondition;
  no ABI route bypasses a Swift property observer. A best-effort preflight guard +
  doc note shipped; nothing more is generator-fixable.
- **`Measurement<T>` general value-only projection** — Foundation type behavior, not a
  binding defect. (The runtime-level `Measurement<T>(double, T)` ctor that makes
  WorkoutKit range alerts constructible from C# is a *targeted* surface, not a general
  round-trippable `Measurement<T>`.)
