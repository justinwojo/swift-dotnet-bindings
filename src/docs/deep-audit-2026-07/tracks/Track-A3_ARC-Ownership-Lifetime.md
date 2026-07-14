# Track A3 — ARC / Ownership / Lifetime / Memory Safety

**Wave**: 1  
**Track**: A3  
**Date**: 2026-07-15  
**Mode**: Read-only analysis (no production edits)  
**Risk (headline)**: **Medium** — core ARC/SafeHandle/Design-B2/payload-semantics seams are mature and heavily fixture-gated; residual risk is concentrated in intentional-leak design edges, finalizer-metadata failure modes, and already-known Mono async/finalizer constraints.

---

## Headline

SwiftBindings’ ownership model is **coherent and largely correct** after the 2026 ownership overhaul:

| Pillar | Status |
|--------|--------|
| Class ARC (`Arc` / `SwiftClassHandle` / finalizer trampolines) | Sound; Mono finalizer hazard mitigated |
| Value/buffer ARC (`SwiftSafeHandle` + VWT Destroy / `SBW_VWTDestroy`) | Sound; `MarkConsumed` for consuming params |
| Wire-buffer / extraction seam (`PayloadConstructionSemantics` + `SwiftMarshal`) | Single declared cleanup axis; Borrow/Move/Copy matrix explicit |
| Reverse-dispatch R0 (`ProxyLifetimeTracker` Design B2) | Implemented; inverted-lifetime fabrication defect closed |
| Async self / param lifetime (`AsyncHelpers` / `SwiftAsyncCallHolder`) | Typed holder + deferred release; balances issue #40 ObjC self |
| BindingTests Lifetime/ + MemoryManagement/ | Strong leak-probe culture (carrier, borrowed callback, existential) |

No new **P0 emission-live double-free / UAF** was confirmed from static read alone. The track’s value is a **ownership map**, classification of residual edges, and L3/L4 notes — not a pile of novel crash bugs.

---

## Ownership map (passRetained / Retain → owner → release)

### Class instances (Swift class / EveryProtocol R0)

| Site | +1 source | Owner | Release path |
|------|-----------|-------|--------------|
| Constructor / method class return | Swift `Unmanaged.passRetained` in `@_cdecl` wrapper | C# `SwiftClassHandle` / class projection `NewFromPayload` | Explicit `Dispose` → `Arc.Release`; finalizer → `SwiftReleaseTrampoline.Release` (`SBW_SwiftRelease`) |
| Class parameter (C# → Swift) | **Borrow** (`takeUnretainedValue`) — no extra +1 | Caller’s wrapper | Unchanged |
| Class round-trip identity | Borrow in + owned out | Both wrappers | Each Dispose releases one; `ArcRoundTripTests` pins rc==2→1→deinit |
| Async instance self | `Arc.UnknownObjectRetain` (isa-dispatch) | `RetainedSelfPtr` in `SwiftAsyncCallHolder` | Callback `Cleanup` → `Arc.UnknownObjectRelease` (issue #40; **not** `Arc.Release`) |
| Protocol proxy C#-impl R0 | `SBW_Create…` `passRetained` | **Proxy** via `ProxyLifetimeTracker` (`_ownsEveryProtocolR0`) | `Dispose`/`~Proxy` → `ProxyLifetimeTracker.ReleaseHandle` → trampoline; impl GCHandle freed on Swift deinit |
| Proxy ctor failure before Track | Factory +1 | Temporary | Direct `Arc.Release` in catch (user thread) |
| Class-bound existential payload | `swift_unknownObjectRetain` | Owning wrapper | `UnknownObjectRelease` / finalizer-safe `UnknownObjectReleaseFinalizerSafe` |
| Error box on `SwiftException` | Retained error handle transfer | Exception finalizer | `~SwiftException` → `SBW_ReleaseError` (skip on process exit) |

### Value / COW / buffer types

| Site | +1 source | Owner | Release path |
|------|-----------|-------|--------------|
| Non-frozen / complex enum / SwiftUI value | Wire buffer +1 (Adopt) | Wrapper `SwiftSafeHandle` | VWT Destroy + `NativeMemory.Free` (Dispose direct; finalizer via `SBW_VWTDestroy`) |
| Frozen-with-ref / Array/Dict/Set/Optional/Result | Wire +1 orphaned after `InitializeWithCopy` (Copy) | Temp cleaned by `DestroyWireBufferRetains` / carrier Destroy; wrapper owns copy | Carrier Destroy before `SBW_Free`; wrapper Dispose |
| `SwiftString` | Bitwise move of +1 (Move) | Wrapper buffer | Free only on temp; Destroy on wrapper dispose |
| Frozen blittable / Inline | No SafeHandle | Stack/value | Free temp only |
| Consuming noncopyable param | Swift `consuming` takes +1 | Swift | `MarkConsumed` → skip Destroy, free buffer only |
| Iterator `makeIterator` | Storage ref consumed by iterator | Iterator Destroy | Pre-`Arc.Retain` on dict/set storage (`SwiftDictionary`/`SwiftSet`) so collection Dispose does not over-release |

### Closures

| Site | +1 / root | Owner | Release path |
|------|-----------|-------|--------------|
| Escaping C# → Swift | GCHandle in `_SBClosureCtx` box | Swift ARC on box | Box deinit → `DestroyClosureContext` frees GCHandle |
| Escaping Swift → C# (`SwiftEscapingClosure.FromSwift`) | `Arc.Retain(context)` | Wrapper | Dispose → `Arc.Release`; finalizer → `SafeReleaseRawForFinalizer` (raw path — no `AnyObject` cast) |
| Non-escaping C# → Swift | GCHandle | C# `finally` | Free after call |
| Nested non-escaping inner box | `passRetained` box | Outer adapter | `Unmanaged.release` after cdecl returns |
| Nested **escaping** inner box | `passRetained` box | **Intentionally not released** on sync path | Documented design leak (`NestedClosureBridge.cs`) |
| Async reverse closure success | C# `MarshalToSwift` into temp; Swift `load(as:)` / bytes copy | Swift continuation value | C# `NativeMemory.Free` after callback; result Dispose quiet |

### Async result carriers (Swift → C# Task)

| Shape | Carrier +1 source | Balanced by |
|-------|-------------------|-------------|
| Collection / frozen-with-ref / non-frozen | `initializeMemory(as:repeating:)` copy witness | Completion callback VWT Destroy **before** `SBW_Free` (`AsyncHarnessEmitter.BuildCollectionCarrierMarshalLines`, carrierNeedsDestroy / newFromPayloadTakesOwnership arms) |
| Class pointer | `passRetained` / pointer carrier | Adopt via `MarshalFromSwift` / GetNSObject owns |
| Suppressed-proxy existential | Still initializeMemory’d | Destroy/release **before** fault throw (fault-only methods must not leak) |

---

## Findings

### Confirmed

*(None newly confirmed as open defects from static evidence alone. Prior confirmed-and-fixed themes are under Already-known / Refuted.)*

---

### Already-known

#### DA-W1-A3-001: Mono SafeHandle async lifetime (upstream comment)

- **Severity**: P1 (when hit) / **blocked upstream**  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L1  
- **Reachability**: emission-live (async instance methods on struct receivers under Mono)  
- **Claim**: CoreCLR SafeHandle marshaling is call-scoped; across Swift async suspension Mono can collect the SafeHandle while the native task is still live.  
- **Evidence**: `src/docs/Future/upstream-issues-README.md` (tracking-issue comment); `roadmap.md` Blocked table “Mono: SafeHandle async lifetime”; mitigated in-tree by `DeferredSafeHandleRelease` + `DangerousAddRef` (`AsyncHelpers.cs:40–56`, `WrapperEmitter.Async.cs` struct self path).  
- **Prior art**: UP SafeHandle note; **do not invent as 5th upstream issue**.

#### DA-W1-A3-002: Mono finalizer `!ji->async` on direct `swift_release` / VWT

- **Severity**: P0 if unmitigated  
- **Status**: already-known (mitigated)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Direct CallConvSwift / libswiftCore release from GC finalizer after CallConvSwift JIT contamination crashes Mono.  
- **Evidence**: `Arc.cs:307–337` (`SwiftReleaseTrampoline`), `SwiftClassHandle.cs:111–128`, `SwiftHandle.cs:184–218` / `VwtDestroyTrampoline`, `SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe`. Upstream issue 1.  
- **Prior art**: UP-01; BindingTests Enum/Existential historical repros.

#### DA-W1-A3-003: Issue #40 — native-only `Arc.Release` on @objc self / class-bound existentials

- **Severity**: P0 if unmitigated  
- **Status**: already-known (fixed)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: `swift_release` / `swift_isDeallocating` mis-handle ObjC isa; must use `UnknownObjectRetain/Release`.  
- **Evidence**: `AsyncHelpers.cs:12–16`, `Cleanup` at `AsyncHelpers.cs:281–290`; `WrapperEmitter.Async.cs:232–264`; class-bound paths in `SwiftMarshal` (`MarshalBorrowedClassFromSlot`, `ExtractCopiedValue`).  
- **Prior art**: issue #40; BindingTests class-param / mixed ObjC.

#### DA-W1-A3-004: Design B2 reverse-dispatch lifetime (Defect G)

- **Severity**: was P0 silent fabrication  
- **Status**: already-known (implemented)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: R0 cannot be gated on EveryProtocol deinit; proxy owns R0; impl rooted by Swift liveness; `ResolveImpl` not weak proxy→impl.  
- **Evidence**: `src/docs/Design/reverse-dispatch-lifetime.md`; `ProxyLifetimeTracker.cs` full file; `ProtocolProxyEmitter.Receivers.cs:2350–2410`, `ProtocolProxyEmitter.SwiftObject.cs:119–154`.  
- **Prior art**: DES-REV.

#### DA-W1-A3-005: Async / wire carrier +1 leak class (frozen containers, etc.)

- **Severity**: P0 when unfixed  
- **Status**: already-known (primary harness arms appear fixed; residual plan in AR-SESS)  
- **Confidence**: high that contract is understood; medium that **every** emission path is covered  
- **Lenses**: L1, L2  
- **Claim**: `initializeMemory` / copy-out leaves carrier +1 that must be VWT-Destroyed before `SBW_Free` if `MarshalFromSwift` took its own copy.  
- **Evidence**:  
  - Fix shape: `AsyncHarnessEmitter.cs:1000–1051`, `BuildCollectionCarrierMarshalLines` (`:1413–1451`)  
  - Sync: `MethodMarshalPlanBuilder` `DestroyWireBufferRetains` cleanup  
  - Probes: `WireCarrierLeakProbeTests`, `AsyncCollectionCarrierLeakProbeTests`, `AsyncGenericBridgeCarrierLeakProbeTests`, `StructVwtDestroyLeakTests`  
- **Prior art**: AR-SESS `async-result-carrier-leak`; DES-MEM.  
- **Residual**: confirm any async path that still emits bare `MarshalFromSwift` + `SBW_Free` without Destroy (generic-bridge/suppressed arms claim coverage; cross-check Wave A7).

#### DA-W1-A3-006: Borrowed callback-arg Copy-wrapper leak (Finding 11)

- **Severity**: was P1 leak  
- **Status**: already-known (fixed)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Blanket `SuppressFinalize` on borrowed marshal leaked `PayloadConstructionSemantics.Copy` independent +1.  
- **Evidence**: `SwiftMarshal.MarshalCallbackArg` (`:1185–1239`) — Copy = owning; Adopt/Move = suppress; class = retain+own. Probe: `BorrowedCallbackArgLeakProbeTests`.  
- **Prior art**: Finding 11 comments in runtime tests.

#### DA-W1-A3-007: Exactly four Mono upstream issues (+ SafeHandle comment)

- **Status**: already-known policy  
- **Do not re-chase** as new upstream invent.

---

### Candidate

#### DA-W1-A3-010: Nested escaping inner-closure box intentional leak

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: high (code documents intent)  
- **Lenses**: L1, L3  
- **Reachability**: fixture-reachable (nested escaping closures)  
- **Claim**: Outer adapter `passRetained`s an inner escaping box and **does not** release it after the cdecl call — “no safe release point on this synchronous path.”  
- **Evidence**: `NestedClosureBridge.cs:733–745`  
- **Probe**: LifetimeTracker on nested escaping fixture; measure live boxes / process growth.  
- **Suggested fixture**: Swift API taking `@escaping ( (@escaping () -> Void) -> Void )` nested shape; assert bounded retain after N calls if a release protocol is designed.  
- **Note**: Non-escaping inner path correctly `Unmanaged.release`s — only escaping is leaky by design.

#### DA-W1-A3-011: Returned throwing-closure error +1 may not fully release

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L1, L2  
- **Reachability**: emission-live (`ReturnedThrowingClosureLeakTests`)  
- **Claim**: Swift→C# returned throwing closures hand errors `passRetained` into `SwiftResult` failure; the leak test **characterizes** live ≤ N rather than asserting live == 0, and comments that the path “passes today regardless of the boundary’s current release behaviour.”  
- **Evidence**: `ReturnedThrowingClosureLeakTests.cs:74–109`; `ClosureEmitter.Throwing.cs` errorOut wiring.  
- **Probe**: Run `TestReturnedThrowingClosureErrorLeakBounded` and inspect `live` count; if live == N after Dispose+drain, promote to confirmed P1 leak.  
- **Suggested fix direction**: ensure Failure payload extraction + `SwiftResult.Dispose` always VWT-Destroys the error arm’s +1 (if not already).

#### DA-W1-A3-012: `SwiftSafeHandle` finalizer skips VWT Destroy when metadata cache is zero

- **Severity**: P2  
- **Status**: candidate (latent / fail-soft)  
- **Confidence**: medium  
- **Lenses**: L1, L5  
- **Reachability**: latent in production if `GetTypeMetadata` fails at construction; common in unit-test mocks  
- **Claim**: Construction catches metadata failure and sets `_metadataHandle = IntPtr.Zero`; finalizer then frees the buffer **without** Destroy → orphaned embedded ARC retains if a real type ever hits this path.  
- **Evidence**: `SwiftHandle.cs:99–114`, `HandleFinalizerRelease` `:268–276`  
- **Probe**: Force metadata failure on a real Copy-type with TrackedRef field; assert leak under finalizer-only dispose.  
- **Mitigation today**: production types resolve metadata; explicit Dispose uses live `SwiftObjectHelper<T>.GetTypeMetadata()` path.

#### DA-W1-A3-013: `CompleteWithResult` Free without VWT Destroy

- **Severity**: P2 (only if non-POD async reverse returns use this path)  
- **Status**: candidate / likely-refuted for current matrix  
- **Confidence**: low–medium  
- **Lenses**: L1  
- **Claim**: `AsyncClosureHelper.CompleteWithResult` does `MarshalToSwift` → successAction → `NativeMemory.Free` without Destroy. Safe for POD `load(as:)` success symbols and UTF-8 Data/String paths; would be wrong for non-POD structs that put +1 into the buffer if ever admitted.  
- **Evidence**: `AsyncClosureHelper.cs:430–447`; `ClosureEmitter.AsyncSwiftWrapper.cs:189–196` (`load(as:)`). Async arg matrix is Primitive/String/Class only (`ClosureHandler.GetAsyncThrowingArgCategory`).  
- **Probe**: If async reverse closure return of Array/String-struct is ever enabled, add Destroy or InitializeWithTake protocol.  
- **Prior art**: roadmap UnsupportedClosure remaining shapes.

#### DA-W1-A3-014: PayloadSemantics / NewFromPayload registration miss on NativeAOT

- **Severity**: P1 if miss  
- **Status**: candidate (latent integrity)  
- **Confidence**: medium mechanism, low known miss  
- **Lenses**: L1, integrity-gate  
- **Claim**: Shared generics must not static-virtual-read `PayloadConstructionSemantics`; registration + reflection backstop. A new `ISwiftObject` without `RegisterPayloadSemantics` can fail or mis-clean on NativeAOT.  
- **Evidence**: `PayloadConstructionSemantics.cs`; `SwiftFrameworkResolver.cs:70–90`; `SwiftMarshal.GetPayloadSemanticsForType` `:813–827`; RuntimeContract floor 16.  
- **Probe**: Strip registration for one BindingTests type under device; expect loud failure or leak.

#### DA-W1-A3-015: Escaping GCHandle leak when native runtime dylib absent

- **Severity**: P3 (packaging / test harness)  
- **Status**: candidate  
- **Confidence**: high  
- **Lenses**: L3  
- **Claim**: `SwiftClosureContext.EnsureRegistered` falls back to leak if `SwiftBindingsRuntime` missing (`IncludeSwiftBindingsRuntimeNative=false`). Documented acceptable for those builds.  
- **Evidence**: `SwiftClosureContext.cs:58–71`.

---

### Refuted (checked correct / already guarded)

| ID | Topic | Why refuted |
|----|-------|-------------|
| DA-W1-A3-R01 | Class inputs consumed (`takeRetainedValue` on params) | SelfReconstruction + wrappers use `takeUnretainedValue`; `ArcRoundTripTests` rc balance |
| DA-W1-A3-R02 | Missing iterator retain → over-release | Explicit `Arc.Retain` before `makeIterator` in Dict/Set (`SwiftDictionary.cs:467–473`, `SwiftSet.cs:587–593`) |
| DA-W1-A3-R03 | Finalizer-only correctness “impossible” for Copy types | By design finalizer **must** Destroy; probes assert finalizer-only borrow path |
| DA-W1-A3-R04 | Process-exit skip of native release is a bug | Intentional `SwiftExitGuard` — deinit against torn-down runtime is worse; explicit Dispose still runs |
| DA-W1-A3-R05 | Dual R0 free (EveryProtocol handle + Tracker) | C#-impl proxies hold plain `IntPtr` + `_ownsEveryProtocolR0`; Tracker’s atomic `Released` is sole native release |
| DA-W1-A3-R06 | Async ObjC-rooted self still uses `Arc.Release` | Emits `UnknownObjectRetain` + holder `UnknownObjectRelease` |
| DA-W1-A3-R07 | Async throwing class args use wrong retain for ObjC | `IsClassType` excludes ObjCBridged/ObjCRooted (`ClosureHandler.cs:1072–1074`) |
| DA-W1-A3-R08 | Design B2 fabrication still live | Receivers `ResolveImpl` + FailFast dead-impl backstop |
| DA-W1-A3-R09 | VWT misuse frozen vs resilient as single bug class | Semantics split Adopt vs Copy is explicit; probes cover both |

---

### Gaps (audit / test / map)

| Gap | Impact | Suggested follow-up |
|-----|--------|---------------------|
| No single matrix table in docs for “who Destroy’s the async carrier” per return kind | A7 / emitter drift risk | Promote `AsyncHarnessEmitter` comments + unit tests of `BuildCollectionCarrierMarshalLines` as the oracle (partially done) |
| Returned-throwing-closure error live count not assert-zero | Soft L2 greenwash risk | Tighten probe if live stays at N |
| Nested escaping box leak not LifetimeTracker-gated | Silent growth in advanced closure apps | Fixture or document as Known Limitation |
| Finalizer-only vs Dispose-only parity not exhaustive for every projection type | Some types only dispose-tested | Sample Adopt/Copy/Move/Inline under finalizer-only drain |
| Device vs sim for ownership | Most leak probes run both; async self SafeHandle residual is Mono-shaped | Keep device in release gates for marshalling changes |
| `CompleteWithResult` non-POD future | Latent | Gate admission of non-POD async reverse returns with Destroy protocol |
| Owned existential collection-element EC2+ | Latent per DES-REV | Already-known roadmap latent — do not re-open without fixture |

---

### Suggested fixtures (BindingTests)

| Priority | Fixture | Catches |
|----------|---------|---------|
| High | Nested `@escaping` inner box retain census | DA-W1-A3-010 |
| High | Returned throwing closure assert `live == 0` after Dispose | DA-W1-A3-011 |
| Medium | Finalizer-only dispose of Adopt non-frozen + Copy array without explicit Dispose | Finalizer path + metadata |
| Medium | Async reverse closure returning class (if not present) retain balance | CompleteWithResult class ABI |
| Low | Metadata-unavailable SafeHandle finalizer (mock) | DA-W1-A3-012 |

Existing strong fixtures (do not duplicate):

- `Lifetime/ArcRoundTripTests`, `ProxyLifetimeTests`, `EscapingClosureLifetimeTests`, `NonEscapingClosureLifetimeTests`, `HeapOwnershipTransferTests`, `ConsumingNoncopyableTests`
- `MemoryManagement/*LeakProbe*`, `DisposeTests`, `ExtractionRetainProbeTests`, `CompositionArgLifetimeProbeTests`

---

## L3 — Graceful degradation (ownership lens)

| Observation | Degrade direction |
|-------------|-------------------|
| Suppressed-proxy async existential still releases carrier +1 before fault | **Good** integrity-on-fault (no silent leak of native storage for dead API) |
| `SwiftException` finalizer-only error release (no Dispose API) | Usability: consumers cannot deterministically free error boxes; finalizer-only is intentional for Mono throw constraints |
| Nested escaping inner leak | Degrades by **process-lifetime growth** not compile failure — should be Known Limitation if kept |
| Missing PayloadSemantics registration | Should **fail closed** (already throws on reflection miss) rather than guess Inline |
| Closure context without native dylib | Accepts leak — packaging honesty for harness apps |

No L3 product change recommended in this track beyond documenting intentional leaks.

---

## L4 — Simplification without capability loss

| ID | Opportunity | Risk class | Do not do if… |
|----|-------------|------------|---------------|
| A3-S1 | Document (not merge) the Marshal extract matrix (`Moved`/`Borrowed`/`Extracted`/`CallbackArg`/`OwnedClass`) | docs-only | Merging APIs loses ownership distinctions |
| A3-S2 | ExistentialContainer0–8 codegen | needs fixture | Hand-edit layout sizes |
| A3-S3 | AsyncClosureState arity bags source-gen | behavior-preserving if API frozen | Public InternalsVisibleTo consumers |
| A3-S4 | Shared `TypeKeyedRegistry<T>` for five dispatchers | byte-identical careful | Concurrent register-once semantics diverge |
| A3-S5 | Unify finalizer trampoline docs (class / VWT / unknownObject / raw) | docs | Collapsing trampoline entry points (different isa/AnyObject rules) |

**Do not simplify**: finalizer vs Dispose release split; PayloadConstructionSemantics four-way cleanup; Design B2 dual-root model; Escaping vs non-escaping ClosureHandle policies.

---

## Sim / device divergence (ownership-relevant)

| Topic | Sim (Mono) | Device (NativeAOT) |
|-------|------------|--------------------|
| Finalizer `swift_release` | Needs cdecl trampoline | Safer but trampoline still used |
| SafeHandle across async | Upstream residual; mitigated by AddRef retain | Generally OK |
| Static virtual PayloadSemantics / NewFromPayload | Reflection / factory cache | Direct dispatch + registration |
| Process exit finalizer timing | ProcessExit vs HasShutdownStarted both checked | Finalization can precede ProcessExit |

---

## Counts

| Bucket | Count |
|--------|------:|
| Confirmed new open defects | **0** |
| Already-known (tracked) | **7** |
| Candidate | **6** |
| Refuted this track | **9** |
| L4 simplification notes | **5** |
| Files reviewed-deep (ledger) | **28** (listed below) |
| BindingTests Lifetime + MemoryManagement files inventoried | **15 + 16** |

---

## Ledger — `reviewed-deep`

Mark these as **reviewed-deep** when the coverage ledger is updated:

### Runtime core
- `src/Swift.Runtime/src/Swift/Runtime/Arc.cs`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs`
- `src/Swift.Runtime/src/Swift/Runtime/PayloadConstructionSemantics.cs`
- `src/Swift.Runtime/src/Swift/Runtime/ProxyLifetimeTracker.cs`
- `src/Swift.Runtime/src/Swift/Runtime/AsyncHelpers.cs`
- `src/Swift.Runtime/src/Swift/Runtime/AsyncClosureHelper.cs`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClosure.cs`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClosureContext.cs`
- `src/Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs`
- `src/Swift.Runtime/src/Swift/Runtime/ISwiftObject.cs`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftException.cs`
- `src/Swift.Runtime/src/Swift/Runtime/WeakSwiftReference.cs`
- `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` (ownership APIs: Destroy/Copy wire, extract matrix, MarshalCallbackArg, PayloadSemantics)
- `src/Swift.Runtime/src/Swift/Runtime/SwiftFrameworkResolver.cs` (RegisterPayloadSemantics hub)
- `src/Swift.Runtime/src/Swift/SwiftDictionary.cs` (iterator retain)
- `src/Swift.Runtime/src/Swift/SwiftSet.cs` (iterator retain)
- `src/Swift.Runtime/src/Swift/SwiftResult.cs` (Copy semantics + SuppressPayloadFinalizer)

### Emitter (ownership-relevant)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncHarnessEmitter.cs` (carrier Destroy contract)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` (R0 Track/Dispose)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.SwiftObject.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NestedClosureBridge.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.AsyncSwiftWrapper.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.IndirectReturn.cs`
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` (async throwing categories / retain)

### Design / tests (reference)
- `src/docs/Design/memory-management.md`
- `src/docs/Design/reverse-dispatch-lifetime.md`
- `BindingTests/RuntimeTestsApp/Lifetime/*` (ownership suite)
- `BindingTests/RuntimeTestsApp/MemoryManagement/*` (leak probes)

Still **inventory** for full Runtime line-complete (Wave 6): ExistentialContainer full file, TypeMetadata, collections beyond iterator sites, ClosureHandle.cs details, BulkArc native, etc.

---

## Recommended backlog (owner-gated)

1. **Tighten** `ReturnedThrowingClosureLeakTests` to `live == 0` or document residual leak (DA-W1-A3-011).  
2. **Decide** nested escaping box strategy: Known Limitation vs deferred free protocol (DA-W1-A3-010).  
3. **A7 pair**: residual async-result-carrier paths not covered by `BuildCollectionCarrierMarshalLines`.  
4. **Do not** re-open Design B2 / PayloadConstructionSemantics design without fixture.  
5. **Do not** invent a 5th Mono upstream issue.

---

## Risk summary

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Crash / double-free residual | **Low–Medium** | Core paths gated; edges are intentional leaks or fail-soft finalizer metadata |
| Silent leak residual | **Medium** | Nested escaping boxes; possibly returned-throw errors; process-exit intentional |
| Sim vs device | **Medium** | Finalizer trampolines + SafeHandle async Mono residual |
| Integrity (wrong ownership claim) | **Low** | Semantics registration + RuntimeContract floor 16 |
| Overall track risk | **Medium** | Mature system; candidates need probes not rewrites |

---

*End of Track A3 report.*
