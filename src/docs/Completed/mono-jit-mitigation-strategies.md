# Completed: Mono JIT Mitigation Strategies

**Completed**: February 2026
**Source**: `mono-jit-mitigation.md` (Strategies A–D, Steps 1–6)
**Final Baselines**: 1864 unit tests, 185 runtime tests (Tier 2 safe-only), 699 integration tests, 94/94 must-pass features
**Remaining edge cases**: See `Future/mono-jit-future-work.md`

---

## Root Cause

Mono's JIT incorrectly marks `CallConvSwift` P/Invoke frames as "async", then hits assertion `!ji->async` at `jit-info.c:918` during stack unwinding. This is a Mono-specific defect — the called Swift functions are synchronous. No C#-side attribute manipulation can fix the bug; the only viable mitigation is to avoid entering Mono's `CallConvSwift` code path via Swift-side wrappers with C calling convention.

---

## Strategy C: Existential Metadata via Swift Wrapper — Done (Step 1)

**Problem**: `swift_getExistentialTypeMetadata` via `CallConvSwift` crashed Mono. Three P/Invoke workaround variants all failed.

**Solution**: `@_cdecl("SwiftBindings_GetExistentialTypeMetadata")` wrapper in `SwiftBindingsRuntime.swift` calls the function on the Swift side, returning metadata pointer via `CallingConvention.Cdecl`. Wrapper-only, hard-fail without dylib (no crashy fallback).

**Deliverables**: `SwiftBindingsRuntime.swift` wrapper, `TypeMetadata.cs` wrapper-only resolution path, 4 unit tests, 2 Tier 2 runtime tests on iOS Simulator.

**Scope**: Zero-protocol existentials (`Any` / `ExistentialContainer0`). N-protocol cases deferred.

---

## Strategy A: SwiftString Runtime Wrapper — Done (Steps 2–3)

**Problem**: `SwiftString.cs` had 5 `CallConvSwift` P/Invokes against `libswiftCore` — any string operation crashed Mono.

**Solution**: `UnsafeRawPointer` + `assumingMemoryBound(to: String.self).pointee` via `@_cdecl` in `SwiftBindingsRuntime.swift`. Retain-balanced, no ABI risk, no BitwiseCopyable issues. Full lifecycle wrapped: Create + ToUtf8 + GetCount + Destroy + FreeUtf8.

**Design**: Wrapper-first with fallback — `_useWrapperPath` static flag, `DllNotFoundException` downgrade to direct `CallConvSwift` path (pays exception penalty at most once).

**Alternatives rejected**:
- Raw-word `unsafeBitCast` — no ARC balance, use-after-free risk for heap strings
- `NSString` boxing — unnecessary overhead
- `load(as:)` — BitwiseCopyable restriction in Swift 6

**Deliverables**: 5 `@_cdecl` exports in `SwiftBindingsRuntime.swift`, `RuntimeNativeMethods` in `SwiftString.cs`, 13 unit tests, 8 runtime tests on iOS Simulator.

---

## Strategy D: Signature Risk Detection — Done (Step 4)

**Problem**: No mechanism to detect whether a P/Invoke signature would trigger the Mono JIT crash.

**Solution**: `MonoJitRiskDetector` static analysis class detects three risk patterns (closure parameters, existential parameters, SwiftString returns) including Optional-wrapped variants. Sets informational `DetectedJitRisks` flags on `MethodDecl`, decoupled from P/Invoke routing. Consumed by Strategy B for closure Cdecl decision.

**Deliverables**: `MonoJitRiskDetector.cs`, `MethodDecl.DetectedJitRisks` flags, hooked into `BaseHandler.HandleBaseDecl()`, 48 unit tests.

---

## Strategy B: Closure Cdecl Expansion — Done (Step 5)

**Problem**: All non-async escaping closure callbacks used `CallConvSwift` — crashed Mono.

**Solution**: Swift `@_silgen_name` wrapper functions adapt `@convention(c)` function pointers to native `@convention(swift)` closures. All three closure emitter files gate `CallConvCdecl`/`CallConvSwift` via `useCdecl` parameter.

**Scope**: Primitive-arg closures (Int, Bool, Double, Float) get Cdecl wrapping. Non-primitive (String, class, struct args) stay on legacy path.

**Exclusions by design**:
- Async-throwing closures — P/Invoke uses `AsyncThrowingContext`/`StartFunc` incompatible with standalone wrapper
- Non-failable frozen struct constructors only — class/non-frozen require indirect return ABI
- `@convention(c)` closures — already safe, detected via `Contains("XC")` in mangled name
- Wrapper generator paths (DefaultParam, ArraySlice) — keep `@_silgen_name` original function types

**Key implementation details**:
- `_cdecl` suffix on MangledName avoids type mismatch with original symbol
- ALL thunk closures in a method must be Cdecl-compatible (`.All()` not `.Any()`)
- Frozen struct value type instance methods need `_selfFixed` param + `_requiresFixedBlock`
- Each closure parameter gets unique variable name (`cdecl_{paramName}`) to avoid Swift compiler crash

**Deliverables**: `ClosureEmitter.SwiftWrapper.cs`, updates to all three closure emitters + `PInvokeEmitter` + `WrapperEmitter.Marshalling`, 38 unit tests.

---

## Step 6: Tier Promotion Pass — Done

Promoted runtime tests from Tier 3 to Tier 2 after Strategies A–D removed crash vectors. Also fixed dispatch thunk bug (`Tj` suffix for non-final class methods with `-enable-library-evolution`).

**Dispatch thunk fix**: `PInvokeEmitter.cs` appends `Tj` to entry points for non-final class instance methods/accessors. Member-level `IsFinal` parsed from ABI JSON `DeclAttributes`. Gate: `!classParent.IsFinal && !methodDecl.IsFinal && MethodType.Instance && !IsConstructor && !needsWrapperLib`.

**Promotions**: Cdecl-wrapped closures, composition tests, long string tests, 14 safe ownership tests — all to Tier 2.

**Deliverables**: `ClassDecl.IsFinal` + `MethodDecl.IsFinal`, 11 dispatch thunk emitter tests, 7 parser tests, ownership test class split (safe vs crash-risk), baselines from 133 → 185 runtime tests.

---

## Key Architectural Insights

1. **Mixed calling conventions already existed**: `PInvokeEmitter` defaults to `CallConvSwift` for all P/Invokes, but `ProtocolProxyEmitter` and `ClosureEmitter.Async` already used `CallingConvention.Cdecl` for wrapper imports. The mitigation work expanded this pattern.

2. **Async closure pattern was the template**: `ClosureEmitter.Async.cs` demonstrated the complete safe pattern (Cdecl callbacks + `@convention(c)` + GCHandle context). Strategy B generalized this to all closure types.

3. **SwiftString was the hidden cascade trigger**: Many methods that appeared safe still crashed because return values went through `SwiftString.ToString()` which internally used `CallConvSwift`. Wrapping SwiftString unblocked string-returning methods across the board.

4. **Wrapper safety comes from precompilation**: The Swift wrapper does the risky work internally. Since the wrapper is precompiled Swift code, Mono's JIT never processes its internal calls — the `CallConvSwift` annotation on the C# P/Invoke to the wrapper isn't itself the crash trigger.

---

## Related Non-Crash Blockers (unchanged)

| Issue | Error | Impact |
|-------|-------|--------|
| Non-blittable types | `InvalidProgramException: Cannot use non-blittable types with Swift calling convention` | Blocks `SafeHandle`, `SwiftOptional<T>`, managed strings in `CallConvSwift` P/Invoke |
| SafeHandle in async | GC collects handle during Swift async suspension | Blocks async instance methods without manual ARC retain/release |

---

## Upstream Filing Status

Three issues drafted in `Future/upstream-bug-reports-draft.md`:
1. Bug: JIT assertion `!ji->async`
2. Feature: Non-blittable type support with `CallConvSwift`
3. Question: SafeHandle/SwiftSelf in async P/Invoke

Filing deferred until repo goes public.
