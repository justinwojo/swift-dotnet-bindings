# Track A7 — Async / Throws / Error Carriers

| Field | Value |
|-------|--------|
| **Wave** | 4 |
| **Track** | A7 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (core Task/GCHandle/error-carrier ownership is mature and largely single-sourced; residual risk is intentional multi-path error ABIs + already-known closure-error leak edge) |
| **Confidence** | **high** on main harness + AMGBE ownership algebra and cancel-key isolation; **medium** on CSM generic-parent cancellation semantics and closure-result Free-without-Destroy matrix |
| **Lenses** | L1 (GCHandle, errorPtr, existential owned-return, cancel vs fault), L2 (fixture honesty), L4 (exact-duplicate extraction only — **full async-emitter merge REJECTED**), L5 (path dual-oracle drift) |

## Headline

**Async/throws is not a silent double-free minefield today.** The main emission spine (`WrapperEmitter.Async` Swift + `AsyncHarnessEmitter` C# callbacks) and the method-generic bridge (`AsyncMethodGenericBridgeEmitter`) share `SwiftAsyncCallHolder.Cleanup`, `AsyncResultPlanner`, unified **6-param** error wire, and process-wide cancel keys distinct from GCHandle cookies. Residual risk is concentrated in **intentionally different jobs** (CSM parent-only **2-param** error ABI, reverse async closures, AsyncStream, SwiftUI CreateAsync) and in **already-known** closure failure-carrier +1 lifetime — not in a missing merge of the three large async files.

**Do not re-propose full async-emitter merge** (`roadmap.md` strategic posture + low-priority “Collapse parallel async-emitter paths”). L4 here is **exact-duplicate extraction only**.

---

## Scope

**In**

| Surface | Role |
|---------|------|
| `WrapperEmitter.Async.cs` | Live **Swift** `@_cdecl` async wrapper + foreground C# launch (TCS, holder, cancel registration, pre-cancel) |
| `AsyncHarnessEmitter.cs` | Live **C#** success/error `[UnmanagedCallersOnly]` callbacks for ordinary async |
| `AsyncMethodGenericBridgeEmitter.cs` | Method-level generic async/throws bridge (own Swift + C#; reuses holder cleanup + planner) |
| `ConcreteProtocolSpecializationEmitter.Async.cs` | CSM async that **delegates** to `WrapperEmitter` + harness (not a third marshaller) |
| `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` | CSM **parent-only** async — **intentionally different** 2-param error ABI + `object[]` holder |
| `AsyncResultPlan.cs` / `AsyncResultPlanner` | Single source for carrier `CallbackTakesOwnership` / `CarrierNeedsDestroy` |
| `ErrorRegistryHelperEmitter.cs` | Cascade dispatch + per-id errorPtr free/transfer algebra |
| Runtime: `AsyncHelpers.cs` (`SwiftAsyncCallHolder`, `SwiftAsyncCancellation`, deferred release types) | Holder cleanup + cancel keys |
| Runtime: `AsyncClosureHelper.cs`, `SwiftResult.cs`, `SwiftException` | Reverse-async + Result + error box finalizer |
| Design: `Design/async-non-frozen-types.md` | Non-frozen param/return ownership history |

**Out (adjacent, mapped only)**

- Full reverse-dispatch vtable (A5*) except async reverse-closure lifecycle notes  
- Full merge / structural rewrite of the three mega-files  
- Sync typed-throws path in `MethodMarshalPlanBuilder` (parallel ownership algebra; not re-audited end-to-end)

---

## 1. Method

1. Read methodology, codebase map (parallel async merge rejected), prior-art (DES-ASYNC-NF, roadmap async reject), W3 synthesis posture, A3 ownership notes for async carriers.  
2. Map every async **job** (ordinary / AMGBE / CSM-async / CSM-parent-only / reverse closure / AsyncStream / SwiftUI).  
3. Trace GCHandle free, holder cleanup, errorPtr ownership, cancellation vs fault, opaque existential owned-return.  
4. Tag intentional divergence vs accidental dual bugs; propose only L4 **exact** extractions.  
5. Prefer under-claim; re-tag roadmap already-known rather than re-discover.

---

## 2. Files reviewed-deep

| Path | LOC (ledger) | Why |
|------|--------------|-----|
| `…/Handler/WrapperEmitter.Async.cs` | ~1,666 | Swift catch body, foreground launch, cancel registration, existential param heaps |
| `…/Handler/AsyncHarnessEmitter.cs` | ~1,903 | C# callbacks, complex/collection/string/tuple, error block, suppressed existential release |
| `…/Handler/AsyncMethodGenericBridgeEmitter.cs` | ~1,472 | Parallel job: method generics; V1 return matrix; cascade/untyped catch only |
| `…/Handler/AsyncResultPlan.cs` | small | Carrier ownership SSOT (S13 Pillar A) |
| `…/Handler/ConcreteProtocolSpecializationEmitter.Async.cs` | — | Reuses WrapperEmitter (no second marshaller) |
| `…/Handler/ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` | ~1,205 | 2-param error ABI + object[] holder |
| `…/ErrorRegistryHelperEmitter.cs` | — | CreateException free/transfer shapes |
| `…/Runtime/AsyncHelpers.cs` | — | `SwiftAsyncCallHolder`, cancel keys, deferred SafeHandle |
| `…/Runtime/AsyncClosureHelper.cs` | ~523 | Reverse async GCHandle **not** freed per invoke; resume once |
| `…/Swift/SwiftResult.cs` | ~567 | C#-only FailureResult; Dispose only native payload |
| Unit: `AsyncHarnessEmitterCleanupTests`, `AsyncMethodGenericBridgeEmitterTests`, `ErrorRegistryHelperEmitterTests` | — | Pins |

---

## 3. Path map — intentional jobs (not “three files to merge”)

Roadmap reality (~10 files / ~9k LOC, ~40% different jobs) matches code:

| Job | Emitter(s) | Error wire | Holder | Cancel ownership |
|-----|------------|------------|--------|------------------|
| **Ordinary async method** | Swift: `WrapperEmitter.Async`; C#: `AsyncHarnessEmitter` | **6-param** (errorPtr, size, msg, isCancellation, task, errorTypeId) — typed / cascade / untyped | `SwiftAsyncCallHolder` | C# token → `SBW_CancelTask(cancelKey)`; Swift `CancellationError` → `isCancellation=1` → `TrySetCanceled` |
| **Method-level generic async** | `AsyncMethodGenericBridgeEmitter` (own Swift+C#) | Same **6-param**; cascade **or** untyped only (**no typed-throws branch**) | `SwiftAsyncCallHolder` | Same cancel-key + registration pattern |
| **CSM concrete async** | `ConcreteProtocolSpecializationEmitter.Async` → **WrapperEmitter** | Same as ordinary | Same | Same |
| **CSM parent-only async** | `…AsyncGenericParent.cs` | **2-param** `(errorPtr, context)`; `passRetained(error as AnyObject)` for **all** errors | Legacy **`object[3]`** `{tcs, resultPtr, cancelReg}` | C# token → `SBW_CancelTask`; Swift error always `ThrowSwiftError` → `TrySetException` (**no `isCancellation`**) |
| **Reverse async closure** | `ClosureEmitter.Async` + `AsyncClosureHelper` | Success buffer / UTF-8 error msg pin | GCHandle owned by Swift `_SBClosureCtx` box (not per-invoke free) | N/A (Swift continuation box + `AsyncResumeGuard`) |
| **AsyncStream / AsyncSequence** | `AsyncStreamEmitter`, sequence bridges | Element/complete/error channels | Stream-specific GCHandle | Separate product surface |
| **SwiftUI CreateAsync** | `SwiftUIBridgeEmitter.AsyncPattern` | Bridge session TCS | Session-local | Latent CreateAsync parity (roadmap) |

### What already converged (do not “merge” again)

| Concern | Shared oracle |
|---------|----------------|
| Carrier ownership (complex value) | `AsyncResultPlanner.ClassifyCarrierOwnership` (+ optional widen on harness) |
| Holder field walk | `SwiftAsyncCallHolder.Cleanup()` via `AsyncHarnessEmitter.BuildHolderCleanupCode` (WrapperEmitter + AMGBE both call it) |
| Cancel registry key vs GCHandle | `SwiftAsyncCancellation.NextCancelKey()` — never GCHandle cookie (recycle hazard documented) |
| Cascade error free/transfer | `ErrorRegistryHelperEmitter.CreateException` + `CascadePayloadShape` |
| Opaque/class-bound suppressed existential +1 | `BuildSuppressedExistentialCarrierRelease` (unit-locked) |
| Fault out of UCO | `BuildAsyncCallbackFaultCatch` / AMGBE twin — cleanup + `TrySetException`, free GCHandle in `finally` |

### Intentional divergences (must not collapse)

1. **AMGBE V1 return matrix** excludes tuple/string/collection/ObjC/optional-class — would import harness shape explosion.  
2. **AMGBE class return** = bare `passRetained` pointer arg; harness complex class often uses **carrier buffer** + `SBW_Free`. Different ABI by design.  
3. **CSM parent-only 2-param error ABI** — roadmap explicitly calls this out as a job that must not merge into the 6-param cascade.  
4. **Async reverse closures** — GCHandle lifetime rides Swift box deinit; freeing in `RunAsync` would dangle multi-await.  
5. **Typed-throws three-shape free algebra** only on harness (+ sync plan builder); cascade shapes live in `ErrorRegistryHelperEmitter`.

---

## 4. Ownership maps

### 4.1 GCHandle (forward async Task)

| Phase | Who owns | Free site |
|-------|----------|-----------|
| Foreground launch | C# `GCHandle.Alloc(_asyncCallHolder)` | Pre-cancel free; launch-catch free; **exactly one** of success/error callback `finally` |
| Callback contract | Single freer (`handle.Free()` in finally) | Documented non-idempotent free — double callback is a real bug, not masked |
| C# `CancellationToken.Register` | Does **not** free GCHandle | Only `SBW_CancelTask` + `TrySetCanceled`; Swift still delivers one terminal callback that frees |

### 4.2 Holder resources (`SwiftAsyncCallHolder`)

| Field | +1 / root source | Released by `Cleanup()` |
|-------|------------------|-------------------------|
| `SelfRetain` | `Arc.UnknownObjectRetain` | `UnknownObjectRelease` (issue #40) |
| `DeferredSelfHandle` | `DangerousAddRef` | `DangerousRelease` |
| `CopyBuffers` | `NativeMemory.Alloc` + copy witness | VWT Destroy + Free |
| `ExistentialHeaps` | heap EC; `OwnsContainer` | `DestroyAndFreeExistential` or free-only |
| `DeferredDisposes` | serialization containers | `Dispose` items |
| `CancellationRegistration` | token registration | `Dispose` |
| `KeepAlives` | GC roots only | Clear list |
| `Tcs` | completion source | **Never** released by Cleanup |

Cleanup is **exception-safe + idempotent** (field clear after each release) so success → fault re-entry cannot double-release.

### 4.3 Result carriers (Swift → C# success)

| Shape | Swift +1 | C# balance |
|-------|----------|------------|
| Complex / collection / frozen-with-ref | `initializeMemory` copy | `AsyncResultPlanner` / collection arms: Destroy before `SBW_Free` or SafeHandle adopt |
| Class (harness buffer path) | retained ptr in buffer | `MarshalFromSwift` adopts; free buffer only |
| Class (AMGBE) | `passRetained` as arg | `SwiftHandle`/class ctor adopts; no carrier free |
| Opaque existential | container in carrier | proxy `ownsContainer: true` bitwise copy; plain `SBW_Free` of carrier |
| Suppressed-proxy existential | still `initializeMemory`’d | **Destroy/UnknownObjectRelease before throw**, then free |
| String / Utf8Slice | allocated bytes | copy string + `SBW_Free` |

### 4.4 Error carriers (Swift → C# fault)

| Path | Wire | Who frees / adopts errorPtr |
|------|------|------------------------------|
| Typed throws (class-direct) | `passRetained` class ptr; cancel → **nil** | Success: `MarshalFromSwift` owns; fail: `Arc.Release`; cancel: nothing |
| Typed throws (buffer shapes) | `allocate` + optional `initializeMemory` | Per-shape: finally Destroy+Free / catch Free only / finally Free — mirrors cascade shapes |
| Cascade (plain throws + registry) | dispatcher allocates typed buffer or id 0 | **`CreateException` only** (helper finally/catch/class-direct) |
| Untyped | nil payload, message only | nothing |
| CSM parent-only | `passRetained(error as AnyObject)` always | `ThrowSwiftError` → `SwiftException` finalizer → `SBW_ReleaseError` |
| Reverse throwing closure (sync result) | `passRetained` into `SwiftError` / `SwiftResult.FromFailure` | **No Dispose on SwiftError** — roadmap medium + A3 candidate |
| Reverse async error | UTF-8 pin via short-lived `GCHandle.Alloc` in `ReportError` | Pinned free in `finally` after errorAction |

### 4.5 Cancellation vs error path ownership

| Event | Task completion | Native resources | GCHandle |
|-------|-----------------|------------------|----------|
| Pre-cancel (token already cancelled) | `Task.FromCanceled` | Holder `Cleanup` immediately | Free immediately |
| C# token fires mid-flight | `TrySetCanceled` (idempotent) | **Deferred** until Swift terminal callback | Deferred until callback |
| Swift `CancellationError` (6-param paths) | `TrySetCanceled(CaptureCancellationToken())` | Holder Cleanup; typed buffer `SBW_Free` if any | Callback finally |
| Swift real error | `TrySetException` | ErrorPtr algebra + Cleanup | Callback finally |
| Marshal fault in UCO | `TrySetException` in catch | Cleanup in fault catch | finally still frees handle |
| C# cancel + later success/error | First TCS write wins | Callback still Cleanup + Free | Single free |

**Key invariant:** cancel keys are **monotonic**, never GCHandle cookies (`SwiftAsyncCancellation` remarks).

---

## 5. Findings

### Confirmed

*(No new emission-live P0 double-free / GCHandle leak confirmed from static read alone on the main 6-param spine.)*

---

### Candidate

#### DA-W4-A7-001: CSM parent-only async maps all Swift errors (incl. CancellationError) to `TrySetException`, not `TrySetCanceled`

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium–high (code path clear; product impact depends on consumer await patterns)  
- **Lenses**: L1, L5  
- **Reachability**: emission-live where CSM parent-only async throws is emitted  
- **Claim**: `AsyncGenericParent` catch always `passRetained` + 2-param `errorCallback`; C# always `ThrowSwiftError` → `tcs.TrySetException`. There is **no** `isCancellation` flag and **no** `error is CancellationError` branch. Cooperative Swift cancel after a C# token usually loses to C# `TrySetCanceled` (first writer wins), but a pure Swift `CancellationError` (or cancel observed only on the Swift side) surfaces as **`SwiftException`**, not **`OperationCanceledException` / canceled Task** — diverging from ordinary async’s 6-param path.  
- **Evidence**: `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs:703–707` (Swift catch); `:935–966` (error UCO); contrast `WrapperEmitter.Async.cs:1390–1449` / `AsyncHarnessEmitter.cs:1605–1615`.  
- **Probe**: BindingTests or unit emit of parent-only async `throws` that throws `CancellationError` without C# token cancel; assert `IsCanceled` vs `IsFaulted`.  
- **Suggested fix direction** (if product wants parity): optional pre-check `error is CancellationError` → nil error + cancel signal **without** collapsing the entire 2-param ABI into 6-param (keep intentional job split).  
- **Prior art**: roadmap “CSM generic-parent 2-param error ABI” intentional job — **do not** cite as reason to merge emitters; only cancellation **semantics** may need a local patch.

#### DA-W4-A7-002: AMGBE cascade helper reference is bare class name (not FQ `global::`)

- **Severity**: P3 (usually same-namespace resolve) / P1 **if** NamespacePattern ever places method type outside helper namespace  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L5, L4  
- **Reachability**: fixture-reachable under remapped namespaces  
- **Claim**: Harness cascade error body uses `ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference` (explicitly for NamespacePattern remaps). AMGBE `EmitErrorCallbackBody` uses bare `GetCSharpHelperClassName(moduleName)` only. Today methods and helper share the resolved module namespace, so bare names compile — but the dual resolver is an accidental drift surface the harness already fixed.  
- **Evidence**: `AsyncMethodGenericBridgeEmitter.cs:1017–1048` vs `AsyncHarnessEmitter.cs:1691–1698`; unit pins only the FQ helper (`ErrorRegistryHelperEmitterTests`).  
- **Probe**: Module with NamespacePattern remap + AMGBE-eligible async throws; compile error callback.  
- **Suggested simplification**: one-line switch to `GetFullyQualifiedHelperReference` (behavior-preserving when ns matches).

#### DA-W4-A7-003: Harness `directTcs` error branch ignores `isCancellation`

- **Severity**: P3  
- **Status**: candidate (likely dead)  
- **Confidence**: medium  
- **Lenses**: L1, L4  
- **Reachability**: latent — live launch sites allocate `SwiftAsyncCallHolder`, not bare TCS (`WrapperEmitter.Async`, AMGBE)  
- **Claim**: `BuildErrorCallbackBlock` cancellation handling is nested only under the holder arm; the `else if (directTcs)` arm always builds a `SwiftException` / `CreateException` path. If any emitter still GCHandle-roots a bare TCS and Swift reports cancel, Task faults instead of cancel.  
- **Evidence**: `AsyncHarnessEmitter.cs:1747–1759` structure; live `GCHandle.Alloc(_asyncCallHolder)` sites.  
- **Probe**: repo-wide search for `GCHandle.Alloc` of bare TCS in async (currently only holder / CSM object[]).  
- **Suggested simplification**: delete directTcs arms once proven unused, **or** add cancel branch for parity if kept as defense-in-depth.

---

### Already-known

#### DA-W4-A7-010: Full async-emitter merge rejected

- **Severity**: n/a (process)  
- **Status**: already-known  
- **Claim**: Do not recommend collapsing `WrapperEmitter.Async` / `AsyncHarnessEmitter` / `AsyncMethodGenericBridgeEmitter` (or the ~10-file job set). Divergence bugs historically found by **new input shapes**, not structure review.  
- **Evidence**: `roadmap.md` strategic posture; low-priority collapse row; codebase-map L4 seed.  
- **L4 survivor only**: exact-duplicate extraction (below).

#### DA-W4-A7-011: Disposable failure carrier for throwing-closure errors

- **Severity**: P2 (throughput-sensitive)  
- **Status**: already-known  
- **Lenses**: L1  
- **Claim**: Swift→C# throwing closure failure uses `passRetained` → `SwiftResult.FromFailure(new SwiftError(...))`; `SwiftError` has no Dispose; C#-only `FailureResult` does not free +1.  
- **Evidence**: roadmap medium “Disposable failure carrier…”; `ClosureEmitter.InvokeThunk.cs` failure path; A3 DA-W1-A3-011.  
- **Trigger**: measurable error-path throughput.

#### DA-W4-A7-012: Mono SafeHandle async lifetime (upstream comment)

- **Severity**: P1 when hit / blocked upstream  
- **Status**: already-known (mitigated)  
- **Claim**: Call-scoped SafeHandle vs async suspension; mitigated by `DeferredSafeHandleRelease` + `DangerousAddRef`.  
- **Evidence**: `AsyncHelpers.cs:40–56`; A3 DA-W1-A3-001; Future upstream notes.

#### DA-W4-A7-013: Issue #40 async self retain uses UnknownObject*

- **Severity**: was P0  
- **Status**: already-known (fixed)  
- **Evidence**: `SwiftAsyncCallHolder.Cleanup` SelfRetain path; WrapperEmitter async class self.

#### DA-W4-A7-014: Async reverse GCHandle not freed per invoke

- **Severity**: n/a (correct)  
- **Status**: already-known / design  
- **Claim**: `AsyncClosureHelper.RunAsync*` deliberately does not free GCHandle; ownership on Swift box deinit; absent native runtime → leak (same as sync escaping).  
- **Evidence**: `AsyncClosureHelper.cs:22–37`.

#### DA-W4-A7-015: `CompleteWithResult` Free without VWT Destroy

- **Severity**: P2 if non-POD reverse returns admitted  
- **Status**: already-known / likely-refuted for current matrix  
- **Evidence**: A3 DA-W1-A3-013; arg matrix Primitive/String/Class only.

#### DA-W4-A7-016: Same-signature closure/async EveryProtocol fan-out gap

- **Severity**: latent loud trap  
- **Status**: already-known  
- **Evidence**: roadmap latent; A5c cross-ref. Not re-investigated as novel A7.

#### DA-W4-A7-017: Async CreateAsync parity gaps (SwiftUI)

- **Status**: already-known latent  
- **Evidence**: roadmap latent CreateAsync.

---

### Refuted (checked correct / already guarded)

| ID | Topic | Why refuted |
|----|-------|-------------|
| DA-W4-A7-R01 | Main path double-frees GCHandle on cancel+complete | C# cancel does not free; exactly one terminal Swift callback frees; TCS uses Try* |
| DA-W4-A7-R02 | Cancel key = GCHandle cookie collision | `NextCancelKey` monotonic; documented recycle hazard avoided |
| DA-W4-A7-R03 | Opaque existential async owned-return leaks +1 on success | proxy `ownsContainer: true`; bitwise container copy; carrier plain free |
| DA-W4-A7-R04 | Suppressed-proxy existential async leaks +1 | `BuildSuppressedExistentialCarrierRelease` before fault throw; unit tests |
| DA-W4-A7-R05 | AMGBE vs harness complex-value ownership algebra drifted | Both call `AsyncResultPlanner.ClassifyCarrierOwnership` |
| DA-W4-A7-R06 | Cascade double-frees errorPtr (callback + CreateException) | Cascade body comments + helper owns free; AMGBE same |
| DA-W4-A7-R07 | Typed cancel buffer Destroy missing | Cancel arm does not `initializeMemory` non-class typed buffer; Free-only is correct |
| DA-W4-A7-R08 | CSM Async reimplements marshalling | `ConcreteProtocolSpecializationEmitter.Async` constructs `WrapperEmitter` |
| DA-W4-A7-R09 | Async reverse double-resume | `AsyncResumeGuard` + dispose-after-resume quiet |

---

### Simplification (L4 only — exact / near-exact)

#### DA-W4-A7-S01: `BuildMethodOwnGenericParams` exact duplicate

- **Status**: simplification  
- **Risk class**: byte-identical  
- **Evidence**: `WrapperEmitter.Async.cs:23–33` (private) vs `AsyncHarnessEmitter.cs:142–152` (public static) — same algorithm.  
- **Shape**: delete private; call `AsyncHarnessEmitter.BuildMethodOwnGenericParams` (or shared static helper).  
- **Do not**: fold AMGBE Swift emission into harness.

#### DA-W4-A7-S02: Fault-catch UCO block dual form

- **Status**: simplification  
- **Risk class**: behavior-preserving (string template vs writer)  
- **Evidence**: `AsyncHarnessEmitter.BuildAsyncCallbackFaultCatch` vs `AsyncMethodGenericBridgeEmitter.EmitAsyncCallbackFaultCatch` — same semantics (holder cleanup + TrySetException); harness also has directTcs arm.  
- **Shape**: one builder used by both; keep directTcs optional flag if still needed.  
- **Do not**: merge entire callback emitters.

#### DA-W4-A7-S03: Untyped / cascade Swift catch-body text

- **Status**: simplification  
- **Risk class**: behavior-preserving with fixtures  
- **Evidence**: `WrapperEmitter.BuildSwiftCatchBody` cascade/untyped arms vs `AMGBE.EmitCatchBody` — same dispatcher call / same untyped shape; harness owns typed-throws extras AMGBE lacks.  
- **Shape**: shared helper parameterized by (typed | cascade | untyped).  
- **Do not**: force AMGBE to gain typed-throws without demand.

#### DA-W4-A7-S04: `SBW_CancelTask` / `SBW_Free` P/Invoke declaration blocks

- **Status**: simplification  
- **Risk class**: behavior-preserving (dedup keys already exist via emission context / Utf8Slice / CancellationTaskEmitter)  
- **Evidence**: repeated LibraryImport snippets in harness, AMGBE, AsyncGenericParent, ErrorRegistry.  
- **Shape**: route remaining inline snippets through existing `CancellationTaskEmitter` / `Utf8SliceEmitter` helpers only where not already.  
- **Do not**: invent a fourth cancel registry.

#### DA-W4-A7-S05: Dead `directTcs` arms (if probe confirms)

- **Status**: simplification  
- **Risk class**: behavior-preserving delete after grep green  
- **Evidence**: no live `GCHandle.Alloc` of bare TCS on main paths.  
- **Do not**: remove until unit coverage of holder-only is asserted.

**Explicit non-goals (rejected merges)**

- Unifying 6-param cascade with CSM 2-param parent-only ABI  
- Folding AMGBE return-kind matrix into harness complex-type switch  
- Merging reverse-closure / AsyncStream / SwiftUI into Task harness  
- “Capability-typed projection” or whole-file decompositions as A7 correctness work

---

## 6. Degrade / test honesty notes (L2/L3)

| Observation | Note |
|-------------|------|
| Suppressed-proxy async existential | Faulting Task + `AsyncReturnProxySuppressed` / SB0006 obsolete — good L3 produce-throw classification |
| AMGBE V1 exclusions | Fail closed by eligibility — good degrade vs half-emitted harness shapes |
| Returned throwing-closure leak tests | A3: may characterize live ≤ N rather than == 0 — soft greenwash risk if still true |
| CSM parent-only cancel semantics | No dedicated assert found in this pass that Task is canceled vs faulted on Swift CancellationError |

---

## 7. Counts

| Category | Count |
|----------|-------|
| **New confirmed P0** | **0** |
| **New candidates** | **3** (CSM cancel semantics; AMGBE helper FQ; directTcs cancel gap) |
| **Already-known (re-tagged)** | **8** |
| **Refuted hazards** | **9** |
| **L4 simplification items** | **5** (exact/near-exact only) |
| **Rejected full merges** | **1** (async-emitter consolidation — standing ban) |
| **Primary files deep-reviewed** | **11** |

---

## 8. Risk summary

| Dimension | Score | Notes |
|-----------|-------|-------|
| Correctness of main Task spine | Low residual | Holder + planner + 6-param error free algebra hardened |
| Path dual-oracle | Medium | Intentional multi-job; accidental duals mostly fixed (planner, holder); AMGBE helper FQ left |
| Error +1 lifetime (closures) | Known P2 | Roadmap disposable carrier |
| Cancellation product parity | Medium on CSM parent-only only | Ordinary path correct |
| Refactor temptation | High hazard | Merge rejected; L4 extract only |

**Overall risk: 2/5** — same band as W1–W3 hardened cores: map + dual-path hygiene + fixture targets, not a pile of novel crash bugs.

---

## 9. Suggested follow-ups (owner-gated; not implemented here)

1. Fixture: CSM parent-only async + `CancellationError` → document or patch to `TrySetCanceled` without 6-param merge.  
2. One-line AMGBE → `GetFullyQualifiedHelperReference`.  
3. Optional L4 PR: `BuildMethodOwnGenericParams` single site.  
4. Leave throwing-closure disposable carrier on roadmap trigger.  
5. **Do not** open “merge async emitters” workstream without a third live divergence bug + new input signal (roadmap trigger).

---

## 10. File coverage ledger updates (for Wave ledger pass)

| Path | Suggested status |
|------|------------------|
| `WrapperEmitter.Async.cs` | reviewed-deep / hazard (multi-shape intentional) |
| `AsyncHarnessEmitter.cs` | reviewed-deep / hazard |
| `AsyncMethodGenericBridgeEmitter.cs` | reviewed-deep / hazard |
| `AsyncResultPlan.cs` | reviewed-deep |
| `ConcreteProtocolSpecializationEmitter.Async.cs` | reviewed |
| `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` | reviewed-deep / hazard (2-param ABI) |
| `ErrorRegistryHelperEmitter.cs` | reviewed-deep |
| `AsyncHelpers.cs` | reviewed-deep |
| `AsyncClosureHelper.cs` | reviewed-deep |
| `SwiftResult.cs` | reviewed (error-carrier edge already-known) |
| `SwiftException.cs` | reviewed |

---

## Bottom line

**Risk 2/5 · 0 new confirmed P0 · 3 candidates · headline: main async/throws ownership is coherent; do not merge emitters; residual = CSM 2-param cancel semantics + known closure error +1 + tiny dual-oracle hygiene.**
