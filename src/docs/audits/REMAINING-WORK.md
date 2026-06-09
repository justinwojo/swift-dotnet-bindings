# Audit — Remaining Work

**What this is:** the consolidated, self-contained backlog of everything still **open** from the 2026-06 codebase audit + remediation campaign. The audit found ~104 confirmed defects; the 10-session campaign (plus follow-up) fixed them and shipped in **0.13.0**. The three "real survivors" later flagged (protocol-extension overload key, `consuming`/`borrowing` parser regexes, collection-element ObjC fallback) are also **fixed and shipped** (commits `313b2a2d`, `5dd8531f`, `76608c2a`).

This file replaces the historical audit doc set (plan, resume notes, per-track reports, remediation plan, state-of-codebase snapshot, grok review docs, release notes), which were deleted as fully-shipped history. Only the residual open items survive here.

> **Standing rules for anyone picking this up.** Every item below is a *code-trace* verdict, not a runtime repro. Per the repo's "verify before fixing — no patch-on-suspicion" rule: **reproduce each with a red fixture first** (maximum-case, not minimum-repro — see memory `feedback_tdd_for_regression_fixes.md`), then fix, then confirm green. **Line numbers drift** — grep/re-confirm before editing. New work ships with tests at the right layer (parser/emitter logic → unit tests; ABI/marshalling/CC → BindingTests, the durable gate). Zero-regression: `nuke test` + `nuke binding-tests` (add `--device` for any calling-convention / ARC / marshalling change) with pass counts ≥ baseline. After any generator edit, rebuild `src/Swift.Bindings/src -c Debug` before regen — the gates run from `bin/Debug/` and won't rebuild a stale dll (memory `feedback_stale_release_binary_masks_regen.md`).

---

## 1. Reachable open work — worth fixing

### #2b — noncopyable `consuming`/`borrowing` **self** instance methods don't get a `@_cdecl` wrapper

**Symptom:** the 6 noncopyable instance methods in BindingTests (`UniqueResource.consume/inspect`, `FileHandle.close/getDescriptor/isOpen`, `TrackedResource.peek`) emit an `[Obsolete(… SB0001)]` + raw `CallConvSwift` P/Invoke to the mangled Swift symbol instead of a `CallConvCdecl` `SBW_…` wrapper. They **work today** at runtime via `CallConvSwift` (their `OwnershipTests`/`NegativePathTests` cases are not skipped) — so this is ABI *hardening*, not a live crash. But it carries real double-free risk if fixed naively (these are `~Copyable` types with `deinit`).

**Verified root cause (code-traced 2026-06-07):**
1. **Ownership is dropped at parse time.** `SwiftABIParser.cs:2001` stores `IsMutating = node.funcSelfKind == "Mutating"` only. `funcSelfKind: "Consuming"` / `"Borrowing"` are discarded — `MethodDecl` has no `IsConsuming`/`IsBorrowing`. Confirmed in the ABI JSON: the `consume` node carries `"funcSelfKind": "Consuming"`.
2. **Self-reconstruction copies a `~Copyable` value.** `MethodWrapperEmitter.cs:500-501`: for a noncopyable parent, `selfRef = self_.assumingMemoryBound(to: T.self).pointee`. `.pointee` is a borrow; calling a **`consuming`** method on it requires *ownership*, so Swift rejects the wrapper (illegal consume of a borrow). `ShouldEmitWrapper` returns true and a wrapper IS emitted, but it fails Swift compilation and is removed by the build's give-up/strip loop (compile log: "Compilation attempt 1 failed — stripping…"); the C# then degrades to `CallConvSwift`. (`borrowing` self may compile via `.pointee`, but rides the same stale-strip path.)

**Fix shape (when picked up):**
- Add `IsConsuming`/`IsBorrowing` to `MethodDecl`; parse them from `funcSelfKind` in `SwiftABIParser.cs` (and the accessor path at `:2470` if relevant). Keep the two readers of any new flag in sync.
- In `MethodWrapperEmitter` self-reconstruction: **consuming self** → take ownership out of the buffer (`…assumingMemoryBound(to:).move()`), then **mark the C# `SwiftSafeHandle` consumed** so a later `Dispose()` is a no-op — exactly the handle-consumed contract the `TrackedResource` *parameter* path already implements (see `Noncopyable.swift` P0-06 comment). **borrowing self** → a true borrow through the pointer with no copy.
- Verify the exact Swift form via SIL (memory `feedback_verify_swift_abi_sil.md`) and an independent CLI consult before committing — ownership errors here are double-frees.
- **Calling-convention change** → gate on `nuke binding-tests --device` (NativeAOT) in addition to sim.

**Durable gate:** the existing `OwnershipTests`/`NegativePathTests` cases that call `GetInspect()`/`Consume()` on `UniqueResource`/`FileHandle`/`TrackedResource` — assert they round-trip AND that the generated C# uses `CallConvCdecl` (no SB0001) after the fix. Add a `deinit`-runs-exactly-once probe for `consuming` self (mirror the `TrackedResource` parameter probe) to catch the double-free.

---

## 2. Latent / unreachable-today logged defects — fix only if a reaching shape ever lands

These are real code-trace defects with **zero emission sites in the current surface** (verified against generated `BindingTests/output`). They are logged so they aren't re-discovered from scratch; none is queued. Each lists the activation condition that would make it reachable.

### 2.1 `ClosureProjection` escaping-parameter branch — unguarded dead code
The escaping-parameter branch of `Marshaler/Projection/ClosureProjection.cs` (`CallbackDeclarations`, ~167–242) emits a `[UnmanagedCallersOnly(CallConvCdecl)]` callback with a trailing `IntPtr context`, but wires it into a `SwiftClosureData` (`GetParameterPlan`, ~58–79) with **no Swift-side cdecl adapter** — a cdecl-vs-Swift-self register mismatch. **Inert today:** `MethodMarshalPlan.CallbackDeclarations` has zero read sites; `MethodMarshalPlanBuilder` never populates it nor calls `GetParameterPlan` on a closure projection (closure *parameters* go through `ClosureHandler`/`ClosureEmitter`/`WrapperEmitter.Marshalling`; the projection factory handles only closure *returns*, whose `GetReturnPlan` correctly uses `delegate* unmanaged[Swift]`). **Activation:** any emitter wiring this escaping-parameter path into live emission. **Fix shape:** match the live `ClosureEmitter` contract — either make the callback `CallConvSwift` + `SwiftSelf` and box the context (mirroring `useBoxedContext`), or keep cdecl and emit the Swift-side cdecl-unbox adapter the live `useCdecl=true` branch relies on. Needs both unit + BindingTests coverage before it ships.

### 2.2 Owned existential collection-element carrier fall-through
Two residual carrier-conversion fall-throughs in `ExistentialProjection.GetArrayElementCarrierConversion` (`ExistentialProjection.cs:162`) route an `__owned` existential collection-element param/write through the *non-owning* alias instead of minting/donating an owned carrier (same over-release shape the reachable arity-1 case already fixed). Two sub-shapes fall through: **(a)** an EC1 single-protocol existential with null `_proxyClassName` (publicType `object` / `ExistentialUnion` / a well-known protocol); **(b)** a **composition** existential (`any P & Q`, arity ≥ 2). **Empirically unreachable today:** `grep -cE "Select\(.*GetExistentialContainer\(\)"` = 0 and `FromEnumerable<…ExistentialContainer[23]>` = 0 in generated output; all 19 emitted owned-carrier conversions take the minting path. (For (a), the null-proxy EC1 cases are separately safe by routing — they box a fresh +1 rather than aliasing a borrowed proxy.) **Activation:** a composition or null-proxy existential collection param/write. **Fix shape:** generalize the owned-carrier dispatch to be arity- and proxy-agnostic (an `ECn`-ownership-aware `CreateOwnedExistentialN` mirroring the EC1 no-fallback `ownsContainer` signal), with a composition-existential array/dict `LifetimeTracker` probe as the gate.

### 2.3 Async `CreateAsync` raw-`IntPtr` BoundType/BoundStruct surface
The async `CreateAsync` public C# API surfaces every flattened `BoundType`/`BoundStruct` init param as raw `IntPtr` (`SwiftUIBridgeEmitter.AsyncPattern.cs:1152` type switch, `:1192` null-check, `:1278` forward), while the non-async factory (`SwiftUIBridgeEmitter.cs:3058`) emits the typed wrapper conversion (`param.Name.Handle` for ObjC-bridgeable structs, `param.Payload.DangerousGetHandle()` for class wrappers). **Not a crash** — the surface is pre-existing and uniform; it functionally works because the consumer can pass `nsUrl.Handle` as the `IntPtr` and the Swift `as! URL` toll-free bridge accepts it. Ergonomics, not correctness. **Activation:** an async-pattern View whose leaf init param is e.g. `Foundation.URL`. **Fix shape:** mirror the non-async typed conversion at `:3058` onto the async switch/null-check/forward so `CreateAsync` surfaces typed wrappers and forwards `.Handle` / `.Payload.DangerousGetHandle()`.

### 2.4 Typed-closure ObjC-bridgeable **class** arg decode split
The typed-closure C# trampoline routes `BoundStruct`+`IsObjCBridgeable` through `ObjCRuntime.Runtime.GetNSObject<T>(argN)!` (`SwiftUIBridgeEmitter.cs:3692`) but routes `BoundType`+`IsObjCBridgeable` (an ObjC-bridgeable *class* closure arg) through `SwiftMarshal.MarshalFromSwift<T>` — a narrower split than the Result-closure branch (which special-cases `BoundType`+IsObjC to `GetNSObject` + `passUnretained`). **Not a safe mechanical copy:** `GetNSObject<T>` requires `T : NSObject`; a genuine Swift-class (`ISwiftObject`) closure arg also tagged ObjC-bridgeable would break under blind `GetNSObject` routing. **Reachability unconfirmed** — no fixture exercises a typed closure with an ObjC-bridgeable class arg, and it's not established any real-world shape produces `BoundType`+IsObjC as a typed-closure arg. **Fix shape:** add a typed-closure decode branch routing `BoundType`+IsObjC through `GetNSObject<T>` *only when* `T` is NSObject-derived (not an `ISwiftObject` wrapper), with a typed-closure ObjC-class-arg fixture as the gate.

### 2.5 Same-signature closure-shaped / async method fan-out gap
The same-signature method fan-out (owner emits the shared body, fans out across sibling vtables; receiver tries each sibling interface) covers only the **plain sync value/string/ObjC-return** shape. A same-signature method whose shape is a dispatchable-closure param, a closure return, or async takes a different emit path (`EmitClosureMethodImplementation` / `EmitDispatchableClosureReturningMethodImplementation` / async-closure receiver) that reads only the owner's vtable and does **not** fan out; the C# receiver deliberately disables the sibling fallback there (`!method.IsAsync && !hasDispatchableClosureParamForFallback` in `ProtocolProxyEmitter.Receivers.cs`). So if two emittable protocols share a closure-shaped/async method signature and a C# type implements ONLY the non-owner protocol, dispatch routes into the owner's body, which force-unwraps its own **nil** vtable field (`EveryProtocolEmitter.cs:3732`) → a **loud** Swift trap ("unexpectedly found nil while unwrapping"), not silent corruption. **Reachability:** requires two emittable protocols sharing a closure/async method full signature AND a non-owner-only C# impl AND dispatch through the non-owner existential — no validation-set library produces it (even the plain same-signature collision has zero real-world hits). **Fix shape:** thread the owner-box + sibling-vtable fan-out into the three closure/async emitters and extend the receiver fallback past the guard with matching (fnPtr,ctx) / TCS marshalling. Needs `--device` (changes closure/async ABI) + fixtures for closure-param, closure-return, and async same-signature collisions with a non-owner-only impl.

### 2.6 Apple-framework SwiftUI-bridge second-slice atomicity
The Apple-framework SwiftUI-bridge lipo path (`Sdk.targets` `_CompileAppleFrameworkSecondBridgeSlice` + `_AFB_*` staging) folds the second simulator arch into the bridge xcframework through a *separate* staging mechanism that S9's `WrapperXCFrameworkMerger` transactional rewrite does **not** cover. **Not known to be broken** — its staging differs from the third-party fat-fold that was made transactional, so it doesn't share that torn-tree window — but it was never audited for the same atomic-commit / cross-run-recovery property under an interrupted build, and its arch-threading parity wasn't exercised by X64SimGate (whose Apple-framework leg uses StoreKit, which has no SwiftUI bridge). **Fix shape (if later confirmed needed):** audit `_CompileAppleFrameworkSecondBridgeSlice` for the same staging→atomic-swap + recovery property `WrapperXCFrameworkMerger.MergeFatSlices` now carries, and add an Apple-framework-with-SwiftUI-bridge leg to X64SimGate.

---

## 3. Deferred / minor — capture only, not worth a dedicated pass

- **`DefaultIndicies` typo** — `Swift5Demangler.cs:740` emits `"DefaultIndicies"` (should be `"DefaultIndices"`). **Zero runtime impact**: the demangled name is discarded; only `IsAsync`/variadic shape are read. Fix opportunistically (1-char) if touching that file.
- **`FrozenWithMemory` (ClassWithBufferStruct) closure arg** — SwiftUI bridge emission is code-correct (`defer { deinitialize + deallocate }`) but has **no BindingTests runtime coverage**. Test-coverage gap, not a defect. Add a fixture when convenient.
- **Apple value-type vs ObjC-bridged data-completeness** — `valueTypes` (in `apple-frameworks.json`) and `*Database.xml` must stay in sync. E.g. Metal structs like `MTLTextureSwizzleChannels` / `MTLPackedFloat3` are not in `valueTypes`; if one appeared as `Optional<T>` in a bound API it would misclassify as ObjC-bridged (wrong ARC). **No current binding hits it.** Add entries as real bindings need them.
- **Co-gater brace-walker has no string/comment state** (`CSharpWrapperCoGater.cs` `FindBlockEnd`/`BuildLineToTypeMap`/`FindEnclosingClassStart`) — real code smell, but **unreachable**: generated C# never carries unbalanced braces inside string/comment literals (it wouldn't compile). The P0 `DllImport`-survival case is already fixed; remaining brace/paren/continuation state-machine gaps are latent. Worth a one-line fragility comment so a future emitter author keeps brace-bearing strings balanced within a line.
- **Narrow sibling of the fixed #1 (DEFER, log only):** `ProtocolProxyEmitter.Receivers.cs:491` — `EmitDispatchableClosureReturningMethodReceiver` uses `NameProvider.GetMethodName(method.Name, propertyNames: null)`, so a zero-arg `() -> Void`-returning method that PascalCase-collides with a property would emit a receiver calling a non-existent interface method (CS1061). Very narrow; not worth fixing unless it surfaces.

---

## 4. Phase-2 hardening candidate pool (still-latent, lower yield)

A post-campaign adversarial re-validation (2026-06-07) confirmed the above as the highest-impact survivors and refuted most of the original ~280-item deferred pool as false-positive / already-mitigated / unreachable (Apple short-prefixes, enum width-truncation, parser comment/string blindness, SwiftUI reserved-name collisions, SwiftUI ObjC-closure UAF/leak, demangler `Ya`/`Yb`/`YK` — **do not re-chase these without new evidence**). What remained, in priority order — all "still latent" (mechanism present, no contradicting gate), none queued:

**Tier 1 (highest blast radius)**
1. Apple-framework SwiftUI-bridge second-slice atomicity (= §2.6 above).
2. Co-gater brace-walker state machine + widened scanners (= §3 above).
3. Remaining short/missing Apple prefixes + enum kind/`rawValueType` + collection-fallback classification drifts.
4. Parser brace/scope/paren duplication (23 duplicated `typeStack`/`braceDepth` loops; no state machine) + full comment/string state + negative-space modifier completeness (`consuming`/`borrowing`/`nonisolated`/`override` ordering across `Broad*`/`Bare*`/`Internal*` regexes).
5. Protocol interface/DIM vs proxy key divergence, subscript key duplication, extension manual key, cross-pool workarounds, `WasEmitted`-for-subscripts parity.
6. SwiftUI async reserved-name de-dup + complete ObjC/typed-closure/Result/frozen/UCO parity + identifier guard.
7. Full skip-taxonomy cleanup + runtime-aware coverage matrix (`coverage-matrix.json` still unproduced) + docs/rules alignment (`[MonoJitCrash]` has 0 usages; stale scripts; upstream count wrong).

**Tier 2 (lower daily reach / partially mitigated)**
- Remaining arch/fingerprint/consumer-target P2s on edge sim/pin cases.
- Demangler remaining `Y*`/fallback + `Ya` heuristic + SI substitution.
- Existential residual carriers / box-GCHandle fallbacks / async non-throwing hang; EC2+ composition owned-return proxies use `DestroyWireBufferRetains` (not `…FinalizerSafe`) on the finalizer path.
- `GenericSignatureParser` inline / same-type-dependent / value-generics support.
- EveryProtocol conformance-body locals + skip-ladder + walker defaults (unguarded identifier emission).

**Tier 3 (maintainability / low-reach / docs)**
- Duplicated ladders/walkers across the codebase.
- L2 ObjC interop pipeline audit (never run).
- L3 perf / API-drift readiness.
- Full L1 docs-drift sweep.

**Process before any Phase-2 investment:** re-measure first (grep the current tree for same-shape siblings); verify before fixing (finder + adversarial-verify, majority vote, default to inconclusive); pick by yield and stop when it drops; add the BindingTests fixtures the tracks recommended (they are the durable gates); zero-regression as above; **owner sign-off before any capped fix plan**.

---

## 5. Process / coverage gaps (not code fixes)

- **Throwing-closure runtime behavior is device-only** (`[SkipOnSimulator]`, Mono JIT Issue 1). Emitter unit tests are the mitigation, but any *new* runtime behavior of a throwing closure (not just box/raw) still needs a device run — the sim cannot help. **Keep running `--device` for any change to throwing-closure marshalling.**

---

## Provenance

Consolidated from the now-deleted audit doc set: `confirmed-survivors-to-fix.md` (§1 + §3), `REMEDIATION-PLAN.md` §6 "Discovered / out-of-scope" OPEN items (§2), `grok-phase2-remaining-hardening-candidates.md` §9 + verdict table (§4), and `CLOSURE-REGRESSION-TEST-GAP.md` residual gaps (§2.1, §5). All `file:line` references are as-of 2026-06-07 code-trace verdicts and will drift — re-confirm before editing. Original audit caveats apply ("not found ≠ not present"; ~40–60% per-run recall; most items have no full runtime repro).
