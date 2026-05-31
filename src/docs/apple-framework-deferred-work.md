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

## T2.5 — witness-getter `EntryPointNotFound` → `NotSupported` wrap (second shape) — OPEN

Error-quality polish for a fixture-only repro (`ProtocolExtOptionalClassParam.swift`);
no shipping Apple framework hits the second shape today. The first shape
(class-superclass, generator decides upfront not to emit the getter) already shipped
during 0.12.0 — confirm you're not redoing that work.

**What's still open — the second shape.** The §5/§5b fail-clean change covers the case
where the generator decides upfront NOT to emit `Get_EveryProtocol_{P}_WitnessTable`. It
does **not** cover a different, pre-existing failure mode: the generator emits the
witness-getter optimistically, the Swift wrapper then fails to compile it (`value of
type 'EveryProtocol' does not conform to specified type 'P'`), and the wrapper
give-up pass drops that one `@_cdecl` from the dylib — but because the getter *was*
emitted by the generator, its emission marker is set, so the C# proxy still emits the
`[LibraryImport(... "Get_EveryProtocol_P_WitnessTable")]` and P/Invokes it at runtime,
yielding **`EntryPointNotFoundException`** at the CALLBACK boundary instead of the
clean `NotSupportedException`.

**Reproduces today** with `ProtocolExtOptionalClassParam.swift` (`PExtOptChildProtocol`);
every gate stays green because nothing exercises that protocol's C#-implementation
CALLBACK path.

**Fix to land (needs a red fixture first).** Wrap the getter P/Invoke in
`GetWitnessTableFromSwift()` so `EntryPointNotFoundException` rethrows as
`NotSupportedException` with a *generic* message ("the Swift wrapper exports no
witness-table accessor for protocol P …"). **Trade-off:** this also catches a getter
gone missing from an unrelated generator regression, turning a loud "symbol missing"
into a designed-limitation message. The build-time `does not conform` error stays
loud, so the masking risk is bounded but real — decide deliberately, with the
`PExtOptChildProtocol` CALLBACK red fixture in place first.

**Done when.** A red `PExtOptChildProtocol` CALLBACK fixture flips to asserting the
clean `NotSupportedException`, and unit + `binding-tests` (sim) + `--device` stay green.

---

<a id="t26--sibling-emission-marker-name-keying-hardening"></a>

## T2.6 — sibling emission-marker name-keying hardening — OPEN

The witness-table-getter marker was re-keyed to `ModuleQualifiedName`, but its sibling
markers — **SetVtable**, **ObjCBase**, **EntityBase**, **Conformance** — still key on
the simple `.Name`. A local protocol and a cross-module parent protocol with the same
simple name can collide in the shared marker set/dictionary and mis-gate a cross-module
proxy. **Not a reproducing bug today** (no known same-simple-name collision across the
current validation/fixture set; cross-module-parent vtable wiring uses a separate
module-prefixed path), so it is latent. Pure categorical-hardening pass.

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

### Safe-hardening plan

1. **Write the RED fixture first.** Add a dependency-module protocol whose simple name
   collides with a local protocol (BindingTests already has a dependency module with
   cross-module parent delegates), arranged so the two have **differing** setter /
   conformance emission. Confirm it reproduces a dangling P/Invoke or wrong carrier
   before changing code.
2. **SetVtable, ObjCBase, EntityBase** are the low-risk re-keys: each is read at a
   single site with the **same decl** that was marked, so swapping both Mark and read
   to `SwiftTypeName.ModuleQualifiedName` mirrors the proven witness-getter change.
   The `EntityBase` pre-scan Mark must be re-keyed in lockstep with its second Mark
   site.
3. **Conformance is the delicate one.** `WasConformanceEmitted` is read at **three**
   sites, including a **cross-decl** ancestor lookup (`ancestorDecl.Name`). Re-keying
   requires verifying that every reader resolves to the **same** qualified name the
   recorder used for that ancestor, and that the dictionary's last-write-wins
   behaviour is preserved for the intended key. Do this only with the RED fixture in
   place — a naive swap here can break cross-module-parent proxy emission /
   suppression and reintroduce the MusicKit-class crash the witness-getter work fixed.
4. Re-run unit + `binding-tests --compile-only` + `binding-tests --skip-regen`, plus
   `--device` (NativeAOT) since this touches vtable / conformance P/Invoke gating.

**Done when.** A RED fixture (a dependency-module protocol whose simple name collides
with a local protocol, with differing setter/conformance emission) reproduces a
dangling P/Invoke or wrong carrier *before* the change, then goes green; unit +
`binding-tests --compile-only` + `--skip-regen` + `--device` all green.

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
