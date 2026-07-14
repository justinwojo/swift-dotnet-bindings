# Track A4 — Closures / Optional-Closure / Reabstraction

| Field | Value |
|-------|--------|
| **Wave** | 4 |
| **Track** | A4 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (core two-layer gate, optional-escaping lifetime, and callback-return SSOT are mature and fixture-gated; residual risk is surface undercount, intentional design leaks, dual-oracle hygiene, and already-known roadmap shapes) |
| **Confidence** | **high** on Layer1/Layer2 gates, `IsEffectivelyEscaping`, GCHandle/`_SBClosureCtx` ownership, and BindingTests Closures domain; **medium** on dual private `IsCdeclCompatibleType` oracles outside the main ClosureEmitter path |
| **Lenses** | L1 (lifetime / wrong ABI), L2 (fixture honesty), L3 (skip vs emit-then-break), L4 (dual oracles / mega-path family), L5 (AI edit hazard) |

## Headline

SwiftBindings’ closure stack is a **multi-bridge family** (main `@_cdecl` / legacy thick-closure path, MethodClosureBridge, NestedClosureBridge, GenericClosureBridge, ProtocolExtensionClosureBridge, ClosedConstrainedClosure, async baseline helpers) with a **clear two-layer admission model**:

| Layer | Oracle | Role |
|-------|--------|------|
| **Layer 1 — emit?** | `ClosureHandler.IsSupportedClosure` → `IsSupportedClosureParameterType` / `IsSupportedClosureReturnType` | Admit the member at all (or route to a specialized bridge) |
| **Layer 2 — cdecl wrapper?** | `ClosureEmitter.IsClosureCdeclCompatible` → `IsCdeclCompatibleType` per arg/return | Whether *all* thunk closures can ride `@convention(c)` adapters; else CallConvSwift thick path |

**No new emission-live P0** (silent wrong-ABI or free-class crash on current BindingTests/validation corpus) was confirmed from static read. The track’s value is a **closure ownership map**, verification that hunt axes are closed or correctly already-known, and residual dual-oracle / degrade notes.

---

## 1. Method

1. Read methodology, codebase map, prior-art, Wave 3 synthesis, roadmap medium/low/latent closure rows.  
2. Deep-read `ClosureHandler`, `ClosureProjection`, `OptionalProjection` (closure-adjacent), `ClosureEmitter*` partials, `MethodClosureBridge`, `NestedClosureBridge`, `GenericClosureBridgeEmitter`, `MemberEmissionValidator` B20, `WrapperEmitter.Marshalling` GCHandle lifecycle, `WrapperValidation.IsEffectivelyEscaping`.  
3. Cross-check BindingTests `RuntimeTestsApp/Closures/*` + Swift fixtures under `Sources/SwiftBindingsTestLib/Closures/` + unit `ClosureHandlerTests` / `ClosureCdeclEmitterTests` / `OptionalReferenceClassifierTests`.  
4. Tag roadmap / constraints.md items as **already-known**, **verified-held**, or **candidate residual**. Prefer under-claim for NEW findings.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `Marshaler/ClosureHandler.cs` | Layer1 support, optional unwrap, generic-bridge eligibility, optional-reference arg gate, callback-arg ownership helpers |
| `Marshaler/Projection/ClosureProjection.cs` | Plan-level projection (largely diverted; dead CallbackDeclarations path) |
| `Marshaler/Projection/OptionalProjection.cs` | Optional container projection (not Optional\<Closure\>; contrast to closure-arg ABI) |
| `Emitter/…/ClosureEmitter.cs` | Escaping callback + **`BuildCallbackReturnStatement` SSOT** |
| `Emitter/…/ClosureEmitter.SwiftWrapper.cs` | Layer2 `IsCdeclCompatibleType` / `IsClosureCdeclCompatible` / `NeedsClosureCdeclWrapper` (**`.All()`**) |
| `Emitter/…/ClosureEmitter.Throwing.cs` | Shares return SSOT |
| `Emitter/…/ClosureEmitter.{Async,IndirectReturn,StructParams,InvokeThunk,FailFastCatch}.cs` | Sibling emit arms |
| `Emitter/…/Handler/MethodClosureBridge.cs` | Bound-generic / complex-enum / `any Error` bridge |
| `Emitter/…/Handler/NestedClosureBridge.cs` | Nested (outer→inner) bridge + escaping-inner design leak |
| `Emitter/…/Handler/GenericClosureBridgeEmitter.cs` | Method-generic monomorphized noescape bridge |
| `Emitter/…/Handler/WrapperEmitter.Marshalling.cs` | GCHandle alloc + finally free / transfer |
| `Emitter/…/WrapperValidation.cs` | `IsEffectivelyEscaping` |
| `Emitter/…/MemberEmissionValidator.cs` | B20 unsupported-closure skip + bridge exceptions |
| `Emitter/…/MemberValidationPipeline.cs` | Gate 3c internal-parent closure drop; generic thunk gate |
| BindingTests `Closures/*` + Swift `Closures/*` | Runtime matrix |
| Unit: `ClosureHandlerTests`, `MethodClosureBridgeTests`, `ClosureCdeclEmitterTests`, `OptionalReferenceClassifierTests` | Gate pins |

---

## 3. Architecture inventory

### 3.1 Closure kinds and bridges

```text
Method has Closure param/return?
  ├─ Unsupported (Layer1 fail) ──► SkipReason.UnsupportedClosure (B20)  [exceptions: GCB/MCB/NCB/PECB/defaulted Optional]
  ├─ Generic method-generic noescape identity ──► GenericClosureBridgeEmitter
  ├─ Nested (inner ClosureTypeSpec in outer args) ──► NestedClosureBridge
  ├─ Bound-generic / complex enum / any Error in args ──► MethodClosureBridge
  ├─ Protocol extension + bridgeable ──► ProtocolExtensionClosureBridge
  ├─ Baseline async (throwing / non-throwing) ──► ClosureEmitter.Async + AsyncClosureHelper
  ├─ NeedsClosureCdeclWrapper (ALL thunk closures cdecl-compatible) ──► @_cdecl standalone wrapper + UCO callbacks
  └─ Else ──► legacy SwiftClosureData / CallConvSwift thick path (+ optional _SBClosureCtx box for escaping)
```

### 3.2 Optional-as-escaping (trap, verified held)

Swift has **no** `@noescape Optional<Closure>`. ABI JSON does **not** always set `IsEscaping` on the *inner* `ClosureTypeSpec` when wrapped in `Optional`.

| Site | Behavior |
|------|----------|
| `WrapperValidation.IsEffectivelyEscaping` | `IsEscaping \|\| IsOptionalClosure(originalType)` |
| MCB / NCB / WrapperEmitter marshalling / finally | All route through `IsEffectivelyEscaping` for GCHandle ownership |
| constraints.md | Documents trap; **code matches** |

### 3.3 GCHandle lifetime map (C# → Swift callback)

| Shape | Alloc | Free / ownership |
|-------|-------|------------------|
| Non-escaping thunk (sync fire) | `GCHandle.Alloc` | `finally` → `Handle.Free()` |
| Escaping + cdecl | Alloc; `Transferred` after successful P/Invoke | Swift `_SBClosureCtx` deinit → `DestroyClosureContext`; if never transferred → free in `finally` |
| Escaping + legacy SwiftClosureData | Alloc + `TryAllocateBoxedContext` when possible | Box deinit frees handle; no-box → leak-on-escape *or* free if never transferred |
| `@convention(c)` non-optional | ThreadStatic slot + UCO thunk | Save/restore prior slot in `finally` (reentrancy-safe) |
| `@convention(c)` optional | `Marshal.GetFunctionPointerForDelegate` (+ optional bool bridge handle) | Escaping semantics; ThreadStatic **not** used (commented unsound) |
| Baseline async | State GCHandle via async helper | Helper / cont-box policy (Async track A7 owns detail) |
| Nested outer escaping | Same transfer/`_SBClosureCtx` pattern | |
| Nested **escaping inner** box | `Unmanaged.passRetained` on Swift side | **Intentional leak** — no safe sync release (documented) |
| Nested non-escaping inner box | `passRetained` | `release` after cdecl returns |
| GCB | GCHandle around user block | `finally` free (noescape identity-forward) |

### 3.4 Callback return marshalling (SSOT)

`ClosureEmitter.BuildCallbackReturnStatement` is the **single** success-return converter for:

- `EmitEscapingClosureCallback`
- `EmitThrowingClosureCallback` (success arm via `swiftResult.Success`)

Indirect returns use `BuildCallbackIndirectReturnStatement` (sibling). Async reverse success is separate (`ClosureEmitter.Async` / reverse-dispatch lifetime docs).

Owned-existential returns mint independent +1 (`CreateOwnedExistential1` / `CreateOwnedCompositionExistential`) so a proxy’s R0 is not double-released after by-value return — aligned with Design B2 notes.

### 3.5 Two-layer gate — `.All()` not `.Any()` (verified)

`NeedsClosureCdeclWrapper` (`ClosureEmitter.SwiftWrapper.cs`):

1. Collect **thunk** closures: supported + `RequiresThunk` + not async.  
2. Require `thunkClosures.Count > 0` **and**  
   `thunkClosures.All(arg => IsClosureCdeclCompatible(...))`.

If **any** thunk is non-cdecl-compatible, the method does **not** get a standalone cdecl closure wrapper for the whole method — avoids mixed ABI where one closure is cdecl-adapted and a sibling is not.

Layer1 `IsSupportedClosure` uses per-arg/return `IsSupportedClosureParameterType` (foreach — all must pass). Nested closures inside params are rejected at Layer1 (`ClosureTypeSpec` → false) unless NestedClosureBridge eligibility re-admits the method.

### 3.6 Dual oracles that must stay distinct

| Pair | Question answered | Must not swap |
|------|-------------------|---------------|
| `IsOptionalReferenceArg` (ClosureHandler) | Closure **slot** ABI: is Optional inner a true object pointer? | Narrow — **exclude** ObjC-bridgeable **value** types (`URL?`) |
| `IsOptionalWithReferenceInner` / `OptionalReferenceClassifier` | Producer / wrapper **return** ABI: can code present as nullable pointer after `as AnyObject`? | Wider — **includes** bridgeable values |
| Layer1 vs Layer2 | Emit vs cdecl-adapt | Layer2 ⊆ supported shapes but different criteria (e.g. complex enum param yes Layer1; return blocked Layer1) |
| `CanInvokeReturnedClosure` narrow vs rich matrices | Returned-closure invoker compileability | Must mirror emitter branch lists |
| ClosureEmitter `IsCdeclCompatibleType` vs ProtocolExtension / ForeignTypeExtension **private** copies | Different emission contexts | L4 dual-oracle — **not** the same function |

---

## 4. Hunt results (summary)

| Hunt question | Result |
|---------------|--------|
| Optional always-escaping / GCHandle free on optional non-escaping path | **Verified held** — `IsEffectivelyEscaping`; optional treated escaping |
| Reabstraction-thunk SIGSEGV class | **No new emission-live reabstraction SIGSEGV** attributed to closure adapter path; historical SIGSEGVs in corpus are layout/enum/async/Mono (other tracks). Nested “reabstraction” is intentional adapter + trampoline design, not a free-for-all |
| GCHandle lifetime | **Verified held** for main cdecl/legacy boxed paths; residual intentional leaks documented (escaping inner NCB; async helper; ClosureProjection dead plan) |
| Unsupported shape emitted not skipped | **Verified held** at B20 (`ShouldSkipMethodEmission`) + bridge eligibility gates; dual AnyType fallthrough is defense-in-depth if a path skips B20 |
| Return-marshalling drift (`BuildCallbackReturnStatement`) | **Verified held** for sync escaping/throwing; matrices for *returned* closures are separate dual oracles (documented load-bearing) |
| Optional\<ObjC value\> closure args | **Already-known** roadmap medium; narrow predicate + fixtures omit broken capability |
| Layer1 vs Layer2 `.All()` not `.Any()` | **Verified held** at `NeedsClosureCdeclWrapper` |
| Nested escaping-inner leak | **Already-known design** (documented in NCB + A3 map) |
| Closure/async reverse-dispatch fan-out | **Already-known latent** (roadmap) — not A4 emission of ordinary C#→Swift closures |
| ClosureProjection live wiring | **Already-known latent** dead path |

---

## 5. Findings

### Confirmed (new or residual)

*(None newly confirmed as open emission-live defects from static evidence alone.)*

---

### Candidate

#### DA-W4-A4-001: Private `IsCdeclCompatibleType` oracles outside ClosureEmitter

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L4, L5  
- **Reachability**: latent / fixture-reachable for PE/foreign-extension surfaces  
- **Claim**: `ProtocolExtensionEmitter` and `ForeignTypeExtensionEmitter` each define a **private** `IsCdeclCompatibleType(TypeSpec, ITypeDatabase)` that is **narrower** (classes/primitives-focused; excludes SimpleEnum/ObjCBridged for PE) than `ClosureEmitter.IsCdeclCompatibleType(TypeSpec, ClosureHandler)`. A future edit that “fixes cdecl support” on one oracle without the others can re-open PE/foreign vs main-path divergence.  
- **Evidence**: `ProtocolExtensionEmitter.cs:886–` (comment: SimpleEnum/ObjCBridged excluded); `ForeignTypeExtensionEmitter.cs:950–`; primary SSOT at `ClosureEmitter.SwiftWrapper.cs:757–858`.  
- **Probe**: Diff acceptance sets on shared fixtures (Optional primitive, simple enum, ObjC class) across three oracles.  
- **Suggested simplification**: Document intentional policy split in one table; only merge if PE/foreign wrappers gain matching adapter arms (needs fixture).  
- **Prior art**: S12c dual-translator deferral (roadmap low) is adjacent, not identical.

#### DA-W4-A4-002: Returned-closure emit matrices are dual oracles with `BuildCallbackReturnStatement`

- **Severity**: P2 (edit hazard; fail-closed as CS* if drift)  
- **Status**: candidate (hazard, not proven open bug)  
- **Confidence**: high that hazard exists; low that current code is wrong  
- **Lenses**: L4, L5, L3  
- **Reachability**: fixture-reachable when returning closures from methods  
- **Claim**: Parameter-direction callbacks use `BuildCallbackReturnStatement`. **Returned** closures (`CanInvokeReturnedClosure` + `EmitClosureReturnMarshalling` / throwing / struct-param variants) maintain **separate** narrow vs rich matrices that must mirror emitter branches. Drift → CS0266/CS0029 at binding compile (fail-closed) or wrong cast if a branch is added only on one side.  
- **Evidence**: `ClosureHandler.cs:1634–1724` (explicit “keep each matrix in sync”); invoke/return emitters under `ClosureEmitter*.cs`. B21 prune uses `CanInvokeReturnedClosure`.  
- **Probe**: Unit tests that add a return arm and assert gate + emitter co-change.  
- **Prior art**: constraints.md “Closure return-marshalling parity” for callback direction only.

---

### Already-known

#### DA-W4-A4-010: Optional\<ObjC-bridgeable value type\> closure arguments (e.g. `(URL?) -> Void`)

- **Severity**: P1 when consumer needs it; currently **skipped / non-cdecl** rather than silent success  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L1, L3  
- **Reachability**: emission-live if widen predicate incorrectly; intentionally unsupported today  
- **Claim**: Closure slot carries value Optional by Swift **value** layout; reading as object pointer → `_objc_fatal`. `IsOptionalReferenceArg` stays **narrow** (true reference inner only). Distinct from producer oracle `IsOptionalWithReferenceInner`.  
- **Evidence**: `roadmap.md` medium row; `ClosureHandler.IsOptionalReferenceArg` (`:2085–2105`); BindingTests `OptionalReferenceClosureArbiterTests` (documents OUT OF SCOPE); `OptionalReferenceClassifierTests`.  
- **Prior art**: roadmap medium; BA Optional/ObjC themes.

#### DA-W4-A4-011: UnsupportedClosure remaining shapes (~188 skips)

- **Severity**: P2 product surface (undercount), not crash  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3, L2  
- **Claim**: Residual UnsupportedClosure = generic params outside GCB, nested shapes outside NCB, async outside baseline matrix, etc. Honest skips; BindingAudit libs (Alamofire, RxSwift, Stripe handlers) still hit this.  
- **Evidence**: `roadmap.md` low-priority row; BindingAudit UnsupportedClosure tables.  
- **Prior art**: roadmap; BSA recommendations (Stripe confirmHandler).

#### DA-W4-A4-012: Nested escaping-inner box intentional leak

- **Severity**: P2 leak (design), not free-use-after-free on the outer path  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Escaping inner closures get `passRetained` boxes that are **not** released on the sync outer path — no safe release point while Swift may store the inner.  
- **Evidence**: `NestedClosureBridge.cs:737–745`; Track A3 closure ownership map.  
- **Prior art**: DES-MEM / A3.

#### DA-W4-A4-013: ClosureProjection escaping-param dead path (GCHandle without box)

- **Severity**: P2 if re-activated without ownership update  
- **Status**: already-known (latent)  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Claim**: `GetParameterPlan` for escaping allocates GCHandle without `_SBClosureCtx` transfer; live emission diverted to string emitters. Roadmap: CallbackDeclarations has 0 production readers.  
- **Evidence**: `ClosureProjection.cs:58–75` + comments `:111–115`; roadmap latent §2.1.  
- **Prior art**: roadmap low-yield latents.

#### DA-W4-A4-014: Same-signature closure/async reverse-dispatch fan-out gap

- **Severity**: P1 if hit (loud Swift nil-unwrap)  
- **Status**: already-known (latent, zero validation library)  
- **Confidence**: high that mechanism exists  
- **Lenses**: L1  
- **Reachability**: latent  
- **Claim**: Owner/sibling vtable fan-out does not thread into closure/async emitters; non-owner-only C# impl can hit nil owner field.  
- **Evidence**: `roadmap.md` latent “Same-signature closure/async method fan-out gap”.  
- **Prior art**: A5 reverse-dispatch tracks; do not re-chase without fixture.

#### DA-W4-A4-015: Optional always-escaping trap (constraints.md)

- **Severity**: was P0 if free-as-non-escaping  
- **Status**: already-known / **verified held**  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Optional closures always escaping; free-as-non-escaping UAF class closed via `IsEffectivelyEscaping`.  
- **Evidence**: `WrapperValidation.cs:1535–1549`; finally arms `WrapperEmitter.Marshalling.cs:1543–1616`; unit MethodHandler/MCB optional-escaping tests.  
- **Prior art**: constraints.md Closure lifetime.

#### DA-W4-A4-016: GenericClosureBridge borrowed class +1 / NativeAOT UAF (fixed)

- **Severity**: was P0 device crash  
- **Status**: already-known (fixed)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Borrowed class callback args must own +1 (`MarshalBorrowedClassFromSwift`); old suppress-finalize path UAF’d on NativeAOT.  
- **Evidence**: `ClosureHandler.BorrowedCallbackArgMarshal` (`:1972–1992`); BindingTests `GenericClosureBridgeLeakTests` / ownership convergence.  
- **Prior art**: Finding 11 / A3 borrowed-callback leak class.

#### DA-W4-A4-017: Async baseline vs non-baseline skip (silent Task drop prevention)

- **Severity**: was P1 silent drop  
- **Status**: already-known (gated)  
- **Confidence**: high  
- **Lenses**: L1, L3  
- **Claim**: Non-baseline async closures rejected in `IsSupportedClosure` so legacy path cannot invoke async synchronously / drop Task.  
- **Evidence**: `ClosureHandler.IsSupportedClosure` (`:250–262`); baseline helpers `IsBaselineAsync*`.  
- **Prior art**: roadmap UnsupportedClosure / async-closure bridge notes.

---

### Refuted / verified-clean (hunt axes)

| Axis | Verdict |
|------|---------|
| Layer2 uses `.Any()` (admit cdecl if one closure ok) | **Refuted** — uses `.All()` on thunk set (`NeedsClosureCdeclWrapper`) |
| Optional free’d as non-escaping in main finally | **Refuted** for current WrapperEmitter path — `IsEffectivelyEscaping` |
| Escaping/throwing callback return marshalling diverged | **Refuted** for success arm — shared `BuildCallbackReturnStatement` |
| Unsupported closures always emit Action\<\> + CS1503 | **Refuted** at B20; AnyType is fallback if something slips |
| Optional\<class\> / Optional\<@objc class\> closure args always broken | **Refuted** for true-reference inners — arbiter BindingTests green path |
| Reabstraction as demangler `ReabstractionThunk` node alone causes emit crash | **Refuted** as A4 product defect — demangle node exists; not tied to callback adapter SIGSEGV class |

---

## 6. BindingTests / unit coverage snapshot

### Runtime (Closures domain)

| Area | Fixture / tests (representative) |
|------|----------------------------------|
| Escaping / convention(c) | `Escaping.swift`, `ConventionC.swift`, `ClosureTests` / `ClosurePathTests` |
| Nested | `NestedClosureBridge.swift` + `NestedClosureBridgeTests` |
| Generic bridge | `GenericClosureBridge.swift` + leak tests |
| Optional reference arbiter | `OptionalReferenceClosureArbiter.swift` + tests |
| Optional throwing void | `OptionalThrowingVoidClosures` |
| Struct / buffer / closed constrained | `StructClosureBridge`, `BufferPointerClosures`, `ClosedConstrainedClosure` |
| Unsupported shapes (skip honesty) | `UnsupportedClosureShapes.swift` |
| Lifetime | `Lifetime/EscapingClosureLifetimeFixture` |
| Async closures | under `Async/` + `AsyncClosure*` tests (A7 overlap) |

### Unit

Heavy: `ClosureHandlerTests`, `ClosureCdeclEmitterTests`, `MethodClosureBridgeTests`, `MethodWrapperClosureTests`, `OptionalReferenceClassifierTests`, everyprotocol optional-closure escaping annotations.

**L2 note:** coverage is strong for **ownership and gates**; residual consumer gaps (Stripe confirmHandler, Alamofire response closures) are **UnsupportedClosure surface**, not missing free-class gates.

---

## 7. L3 graceful degradation

| Mechanism | Assessment |
|-----------|------------|
| B20 UnsupportedClosure skip | Good — member-level skip with report |
| Bridge eligibility (MCB/NCB/GCB/PECB) | Prefer specialized emit over skip when eligible |
| Layer2 fail → CallConvSwift thick path | Degrade to alternate ABI, not package fail |
| Defaulted Optional closure strip on some ctors | Product compromise (nil pass) — documented in MethodHandler collection-ctor path |
| Wrapper strip postprocessor | Defense-in-depth for uncompilable adapters |
| Residual UnsupportedClosure count | Undercount, not crash — G1/product backlog |

**Do not** widen `IsOptionalReferenceArg` without a Swift value→object bridge thunk (roadmap remediation shape).

---

## 8. L4 simplification inventory (no implement)

| Item | Risk class | Note |
|------|------------|------|
| Document / optionally unify PE+Foreign `IsCdeclCompatibleType` with ClosureEmitter | needs fixture | Only if adapters match |
| ClosureProjection retirement or wire through box ownership | needs fixture | Dead today; dangerous if half-wired |
| Returned-closure matrix + callback SSOT consolidation | needs fixture | High risk; keep dual until one IR |
| Bridge family (MCB/NCB/GCB) shared ownership preamble | behavior-preserving possible | Transfer flag / box helpers already partial-shared (`ClosureContextHelperEmitter`) |

**Do not** re-propose full async-emitter merge (roadmap rejected).

---

## 9. File coverage (ledger status suggestion)

| Cluster | Suggested ledger status |
|---------|-------------------------|
| ClosureHandler + ClosureEmitter* | `reviewed-deep` / `hazard` (dual matrices) |
| MCB / NCB / GCB | `reviewed-deep` |
| ClosureProjection | `deferred-known` (latent dead) + hazard if activated |
| OptionalProjection | `reviewed` (A4 adjacent only) |
| BindingTests Closures | `reviewed` |

---

## 10. Counts & risk rollup

| Category | Count |
|----------|------:|
| New confirmed open P0/P1 | **0** |
| New candidate findings | **2** (dual private cdecl oracles; returned-closure matrix dual-oracle hazard) |
| Already-known (tagged) | **8** |
| Hunt axes verified clean / refuted | **6** |
| Risk rating | **2 / 5** |

**Headline for Wave 4 synthesis:** Closures are a **hardened multi-bridge subsystem**. Optional-escaping and Layer1/Layer2 `.All()` invariants hold. Residual value is **product surface** (UnsupportedClosure undercount, Optional value-in-closure), **documented intentional leaks**, and **L4 dual-oracle hygiene** — not a new free/reabstraction crash minefield on the live BindingTests path.
