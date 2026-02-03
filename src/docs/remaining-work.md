# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Starting Point**: Phase 44 complete, 1032 unit tests, TestFramework v2.0 (67 files, 145 features)

---

## Status Summary

| # | Area | Description | Impact | Priority |
|---|------|-------------|--------|----------|
| 1 | Generator | Unbound generic type parameters (`AnyTypeFallback`) | 4 degraded features, 12 skipped members | P1 |
| 2 | Generator | OpaquePointer in method signatures | 2 degraded features, 3 skipped members | P2 |
| 3 | Generator | NSObject subclass as method parameter | 1 degraded feature, 1 skipped member | P2 |
| 4 | Generator | Existential type argument in bound generic | 1 degraded feature, 1 skipped member | P3 |
| 5 | Runtime | Formalize async concurrency hook as shared library | Async init is copy-pasted per test; no reusable runtime | P2 |
| 6 | Runtime | Fix async callback marshalling (Array, String) | 2 async tests blocked | P3 |
| 7 | Validation | Lottie 9/9 — fix `LottieConfiguration.Shared` getter | 1 runtime test failure | P2 |
| 8 | Validation | BlinkID runtime validation test app | Compiles but never runtime tested | P3 |
| 9 | Runtime | Add finalizer safety net to `SwiftSafeHandle` | Memory leaks if users forget `Dispose()` | P1 |
| 10 | Generator | Protocol runtime completion (remove `NotImplementedException` stubs) | Blocks real protocol usage from C# | P1 |
| 11 | Generator | Wrapper automation for known-problematic patterns | Manual per-library patches don't scale | P2 |
| 12 | Generator | Emitter decomposition — split MethodHandler (3,361 LOC) | Blocks maintainability and onboarding | P2 |
| 13 | Generator | Improve binding report with workaround recommendations | Consumers don't know what to do about gaps | P2 |
| 14 | Runtime | .NET convenience methods on Swift runtime types | DX friction with unfamiliar Swift types | P3 |
| 15 | Testing | Deeper protocol runtime tests | Protocol tests are compile checks, not runtime behavior | P3 |
| 16 | Infrastructure | Upstream .NET runtime bug reports with minimal repros | Mono JIT bugs block existentials/async | P3 |
| 17 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety for lifetime management | P3 |
| 18 | Research | NativeAOT investigation (`[LibraryImport]` for Mono JIT bypass) | May eliminate known runtime bugs | P4 |

**Generator target**: 93/93 must-pass features passing (currently 85, 8 degraded)

---

## 1. Unbound Generic Type Parameters

**Priority**: P1 — fixes 4 degraded features, 12 skipped members
**Area**: Generator (marshaler/emitter)

Generic structs, classes, and functions with unbound type parameters have their properties and methods skipped. The marshaler resolves generic type parameters to `Swift.AnyType` and the emitter rejects them as `AnyTypeFallback` or `UnsupportedSignature`.

**Affected features**:

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `generic_function` | `Generics/Functions.swift` | `pair` (returns generic tuple) |
| `generic_struct` | `Generics/Types.swift` | `Wrapper.wrapped`, `Wrapper.init`, `GenericPair.first`, `.second`, `.init`, `.swapped` |
| `generic_class` | `Generics/Types.swift` | `GenericClass.value`, `GenericClass.init` |
| `where_clause` | `Generics/Constraints.swift` | `ConstrainedBox.item`, `ConstrainedBox.init` |

**Skip reasons**:
- `AnyTypeFallback` — property type resolved to `AnyType` (no C# mapping)
- `UnsupportedSignature` — constructor/method contains unresolved placeholder type

**Investigation areas**:
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — generic type parameter resolution
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` — whether unbound params are registered
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — `AnyTypeFallback` rejection
- Compare with **bound** generics (e.g., `BoundIntPair`) which work because the type parameter is concretized

**Acceptance criteria**:
- [ ] `Wrapper<T>`, `GenericPair<T, U>`, `GenericClass<T>`, `ConstrainedBox<T>` emit properties and constructors
- [ ] `pair<T>()` function emits
- [ ] Coverage report: all 4 features show `passing`
- [ ] Existing unit tests still pass

---

## 2. OpaquePointer in Method Signatures

**Priority**: P2 — fixes 2 degraded features, 3 skipped members
**Area**: Generator (marshaler)

Methods accepting or returning `OpaquePointer` / `Optional<OpaquePointer>` are skipped with `UnsupportedSignature`. The marshaler doesn't recognize `OpaquePointer` as marshalable.

**Affected features**:

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `opaque_pointer` | `UnsafeTypes/OpaquePointer.swift` | `opaquePointerIsValid`, `HandleWrapper.describe` |
| `optional_opaque_pointer` | `UnsafeTypes/OpaquePointer.swift` | `optionalOpaquePointer` |

**Expected mapping**: `OpaquePointer` → `IntPtr`, `Optional<OpaquePointer>` → `IntPtr` (nil = `IntPtr.Zero`)

**Investigation areas**:
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — type mapping table
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — how `OpaquePointer` appears in ABI JSON
- Compare with `UnsafePointer<T>` / `UnsafeMutablePointer<T>` which already map to `IntPtr`

**Acceptance criteria**:
- [ ] All 3 methods emit with `IntPtr` parameters/returns
- [ ] Coverage report: `opaque_pointer` and `optional_opaque_pointer` show `passing`

---

## 3. NSObject Subclass as Method Parameter

**Priority**: P2 — fixes 1 degraded feature, 1 skipped member
**Area**: Generator (marshaler)

Free functions taking an NSObject subclass parameter are skipped with `UnsupportedSignature`. The NSObject type itself emits correctly — the issue is only when it appears as a function parameter.

**Affected**: `describeNSObject` in `ObjCInterop/NSObjectSubclass.swift` (takes `SimpleNSObject`)

**Investigation areas**:
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — how NSObject subclass types are marshalled as parameters
- `SimpleNSObject` emits as a class with SafeHandle, so the issue is parameter resolution for free functions, not type emission
- Check if marshaler recognizes `SimpleNSObject` as a class type when it's a free function parameter vs. `self` in a method

**Acceptance criteria**:
- [ ] `describeNSObject` emits with `SimpleNSObject` parameter
- [ ] Coverage report: `nsobject_as_parameter` shows `passing`

---

## 4. Existential Type Argument in Bound Generic

**Priority**: P3 — fixes 1 degraded feature, 1 skipped member
**Area**: Generator (marshaler)

Methods with a bound generic containing an existential type argument (e.g., `[any Describable]` = `Array<any Describable>`) are skipped with `UnsupportedExistential`.

**Affected**: `describeAll` in `Generics/Existentials.swift` (takes `[any Describable]`)

**Note**: Even if the generator emits this, it may be blocked at runtime by the Mono JIT existential metadata bug (see `known-issues-workarounds.md`). The generator fix and the runtime issue are independent.

**Investigation areas**:
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — existential types inside bound generics
- `SwiftArray<ExistentialContainer>` is the runtime representation

**Acceptance criteria**:
- [ ] `describeAll` emits (with `[UnsupportedSwiftType]` if runtime is still blocked)
- [ ] Coverage report: `any_protocol_existential` shows `passing`

---

## 5. Formalize Async Concurrency Hook

**Priority**: P2
**Area**: Runtime infrastructure

The Swift concurrency hook (`swift_task_enqueueGlobal_hook` → GCD redirect) is validated and working but lives inline in `AsyncTests.swift`. It needs to be a proper shared library so consuming apps can initialize it without copy-pasting.

**What exists**: Working hook in `src/Swift.Bindings/tests/IntegrationTests/FunctionalTests/AsyncTests/AsyncTests.swift` using `dlsym` + custom `SerialExecutor` + GCD dispatch.

**What's needed**:
1. Create `src/Swift.Runtime/swift/SwiftBindingsRuntime.swift` with the hook code
2. Create `src/Swift.Runtime/swift/build-runtime.sh` to compile `libSwiftBindingsRuntime.dylib`
3. Create `src/Swift.Runtime/src/Swift/Runtime/SwiftConcurrency.cs` with `SwiftConcurrency.Initialize()` P/Invoke wrapper
4. Update `Swift.Runtime.csproj` to include native library in package
5. Wire consuming apps (Nuke, Lottie test apps) to call `SwiftConcurrency.Initialize()` at startup

**Known limitations** (document, don't fix):
- `@MainActor` tasks are NOT intercepted (`swift_task_enqueueMainExecutor_hook` is buggy in Swift 5.5–6.0)
- Task cancellation does not propagate through GCD dispatch
- Custom actor executors are not intercepted

**Full design**: `src/docs/CompletedPhases/swift-concurrency-interop-plan.md`

**Acceptance criteria**:
- [ ] `SwiftConcurrency.Initialize()` callable from any .NET app
- [ ] Nuke and Lottie test apps use shared library instead of inline hook
- [ ] `TestInstanceMethods` and `TestStaticMethods` still pass

---

## 6. Async Callback Marshalling (Array, String)

**Priority**: P3
**Area**: Runtime

`TestArray` and `TestString` async integration tests are blocked by callback marshalling errors (`Cannot marshal type System.String from Swift`). This is separate from the concurrency hook issue (item 5) — the hook works, but the async callback that delivers the result can't marshal complex types.

**Acceptance criteria**:
- [ ] `TestArray` passes
- [ ] `TestString` passes

---

## 7. Lottie 9/9 — LottieConfiguration.Shared

**Priority**: P2
**Area**: Runtime validation

`LottieConfiguration.Shared` returns a non-null object but property access throws `NullReferenceException`. This is the only failing test in the Lottie runtime suite (8/9 pass).

**Investigation**: Check static property getter P/Invoke marshalling — compare with working property getters (e.g., LottieColor).

**Acceptance criteria**:
- [ ] Root cause identified
- [ ] `./validate-sim.sh 30` exits 0 (9/9 tests pass)

---

## 8. BlinkID Runtime Validation

**Priority**: P3
**Area**: Validation

BlinkID bindings compile cleanly (0 errors) but have never been runtime tested. Need a test app similar to `LottieTestApp`.

**Acceptance criteria**:
- [ ] `BindingTesting/BlinkID/BlinkIDTestApp/` project created
- [ ] Basic API smoke tests (type metadata, configuration, enums)
- [ ] `validate-sim.sh` runs and reports results

---

## 9. Add Finalizer Safety Net to SwiftSafeHandle

**Priority**: P1 — architecture review top-4 blocker
**Area**: Runtime
**Source**: Comprehensive architecture review (Codex finding)

`SwiftSafeHandle<T>` only calls `Destroy` on explicit `Dispose()`, not on finalization. This means forgetting `using` or `Dispose()` silently leaks Swift objects. Every other SafeHandle-based pattern in .NET includes a destructor fallback.

**Target file**: `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs:63-124`

**What's needed**:
1. Add destructor (`~SwiftSafeHandle()`) that calls `Dispose(false)`
2. Ensure `Dispose(bool disposing)` calls `swift_release` even when `disposing` is `false`
3. Suppress finalizer in `Dispose()` via `GC.SuppressFinalize(this)`

**Acceptance criteria**:
- [ ] `SwiftSafeHandle<T>` releases Swift objects even without explicit `Dispose()`
- [ ] `GC.SuppressFinalize` called on explicit dispose path (no double-release)
- [ ] Existing runtime tests still pass

---

## 10. Protocol Runtime Completion

**Priority**: P1 — architecture review top-4 blocker
**Area**: Generator (emitter)
**Source**: Comprehensive architecture review (Codex finding)

`ProtocolProxyEmitter.cs` has `NotImplementedException` at line 922 and 12+ incomplete TODOs. Types implementing Swift protocols hit these stubs at runtime. This blocks real protocol usage from C# — types emit protocol interfaces but can't actually implement them.

**Target file**: `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs`

**What's needed**:
1. Audit all `NotImplementedException` paths and incomplete TODOs
2. Replace stubs with actual P/Invoke calls to protocol witness tables
3. Or, where runtime limitations prevent it, emit `[UnsupportedSwiftType]` with clear skip reasons instead of runtime exceptions

**Acceptance criteria**:
- [ ] Zero `NotImplementedException` paths remain in `ProtocolProxyEmitter.cs`
- [ ] Protocol proxy types either work at runtime or degrade gracefully with `[UnsupportedSwiftType]`
- [ ] Existing unit tests still pass

---

## 11. Wrapper Automation for Known-Problematic Patterns

**Priority**: P2 — architecture review top-4 blocker
**Area**: Generator (emitter)
**Source**: Comprehensive architecture review (external AI consensus)

The Swift wrapper fallback pattern (C# → P/Invoke → Swift wrapper → Swift dylib) is validated and endorsed, but it's applied manually per-library. The generator should detect patterns known to fail at runtime and automatically emit Swift wrappers.

**Known-problematic patterns**:
- Existential types in arrays (`[any Protocol]`)
- Async methods with SafeHandle return types
- `swift_getExistentialTypeMetadata` crashes (Mono JIT bug)

**What's needed**:
1. Generator detects problematic patterns during marshalling
2. Automatically emits Swift wrapper functions + corresponding C# P/Invoke
3. Binding report documents: "Uses wrapper due to runtime limitation"

**Acceptance criteria**:
- [ ] Generator auto-generates Swift wrappers for at least one known-problematic pattern
- [ ] Binding report includes wrapper usage annotation
- [ ] No manual per-library wrapper patches needed for detected patterns

---

## 12. Emitter Decomposition — Split MethodHandler

**Priority**: P2 — architecture review top-4 blocker
**Area**: Generator (emitter)
**Source**: Comprehensive architecture review (all reviewers)

`MethodHandler.cs` is 3,361 lines with 7 nested classes, violating SRP. `EnumHandler.cs` (1,715 lines) and `ProtocolProxyEmitter.cs` (1,403 lines) are also oversized. The existing `emitter-redesign-proposal.md` outlines the target architecture.

**Target files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` (3,361 LOC)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` (1,715 LOC)

**What's needed**:
1. Split MethodHandler into focused handlers per the redesign proposal (Constructor, Static/Instance, SwiftError, Generic, Async)
2. Extract return handlers (IndirectResult, BoundGeneric, Direct, Void)
3. Extract argument handlers (NonFrozen, Generic, BoundGeneric)

**Reference**: `src/docs/emitter-redesign-proposal.md`

**Acceptance criteria**:
- [ ] No single handler file exceeds ~800 lines
- [ ] Existing unit tests still pass with no behavioral changes
- [ ] Handler responsibilities are clear and focused

---

## 13. Improve Binding Report with Workaround Recommendations

**Priority**: P2
**Area**: Generator (reporting)
**Source**: Comprehensive architecture review

When the binding report lists skipped items, consumers have no guidance on what to do. Adding a "recommended workaround" field would help adopters understand their options.

**Target file**: Binding report emission in the generator output (`binding-report.json`)

**What's needed**:
1. Add `recommendedWorkaround` field to skipped items in `binding-report.json`
2. Map common skip reasons to workarounds (e.g., `UnsupportedExistential` → "Use Swift wrapper function", `AnyTypeFallback` → "Use concrete bound generic instead")

**Acceptance criteria**:
- [ ] `binding-report.json` includes workaround guidance for skipped items
- [ ] At least the top 5 skip reasons have mapped workarounds

---

## 14. .NET Convenience Methods on Swift Runtime Types

**Priority**: P3
**Area**: Runtime (DX)
**Source**: Comprehensive architecture review (external AI recommendation)

C# developers encounter unfamiliar types (`SwiftArray<T>`, `SwiftString`, `SwiftOptional<T>`). Adding .NET-idiomatic convenience methods preserves zero-copy performance while improving developer experience.

**Target files**:
- `src/Swift.Runtime/src/Swift/SwiftArray.cs`
- `src/Swift.Runtime/src/Swift/SwiftString.cs`

**What's needed**:
```csharp
// SwiftArray<T>
public List<T> ToList() => new List<T>(this);
public T[] ToArray() => AsSpan().ToArray();

// SwiftString
public static implicit operator string(SwiftString s) => s.ToString();
public override string ToString() => Encoding.UTF8.GetString(Utf8Span);
```

**Acceptance criteria**:
- [ ] `SwiftArray<T>` has `ToList()` and `ToArray()` convenience methods
- [ ] `SwiftString` has implicit conversion to `string`
- [ ] Zero-copy paths (`AsSpan()`, `Utf8Span`) remain unchanged

---

## 15. Deeper Protocol Runtime Tests

**Priority**: P3
**Area**: Testing
**Source**: Comprehensive architecture review (Codex finding)

Current protocol tests in `ProtocolsTests.cs` are mostly compile checks — they verify the generated code compiles but don't exercise runtime behavior (method dispatch through protocol witnesses, proxy object lifecycle, etc.).

**What's needed**:
1. Add runtime tests that call methods through protocol interfaces
2. Test protocol proxy object creation and disposal
3. Test protocol conformance checking at runtime
4. Add tests in TestFramework Swift library if needed

**Acceptance criteria**:
- [ ] Protocol tests exercise method dispatch, not just compilation
- [ ] At least 5 runtime behavior tests for protocol proxies
- [ ] Tests cover both Swift-implemented and C#-implemented protocol conformance

---

## 16. Upstream .NET Runtime Bug Reports

**Priority**: P3
**Area**: Infrastructure
**Source**: Comprehensive architecture review

Known Mono JIT bugs block features (existential type metadata crash, async frame marking). These should be filed as dotnet/runtime issues with minimal reproduction cases so the .NET team can prioritize fixes.

**Known bugs to report**:
1. `swift_getExistentialTypeMetadata` crash in Mono JIT (existential containers)
2. SafeHandle not preserved across async P/Invoke boundaries
3. Non-blittable types with `CallConvSwift` marshalling failures

**Acceptance criteria**:
- [ ] At least 2 bugs filed on dotnet/runtime with minimal repros
- [ ] Issue links documented in `known-issues-workarounds.md`

---

## 17. Roslyn Analyzer for Undisposed Swift Objects

**Priority**: P3
**Area**: Tooling
**Source**: Comprehensive architecture review

Complement to item 9 (finalizer safety net). A Roslyn analyzer can warn at compile time when Swift objects implementing `IDisposable` are created without `using` or explicit `Dispose()`.

**What's needed**:
1. Create analyzer project targeting `ISwiftObject` / `SwiftSafeHandle<T>` types
2. Warn on: local variables without `using`, field assignments without dispose in containing type
3. Package as NuGet alongside `Swift.Runtime`

**Acceptance criteria**:
- [ ] Analyzer warns on undisposed `SwiftSafeHandle<T>` locals
- [ ] Analyzer packaged and included in `Swift.Runtime` NuGet
- [ ] No false positives on properly disposed objects

---

## 18. NativeAOT Investigation

**Priority**: P4
**Area**: Research
**Source**: Comprehensive architecture review

.NET 10's `[LibraryImport]` source generator and NativeAOT compilation may bypass Mono JIT bugs entirely. If NativeAOT works for Swift interop, several known runtime issues (items 6, 16) may disappear.

**What's needed**:
1. Test existing bindings under NativeAOT compilation
2. Verify `CallConvSwift` works with `[LibraryImport]` (source-generated marshalling)
3. Check if existential type metadata and async SafeHandle issues reproduce

**Acceptance criteria**:
- [ ] NativeAOT feasibility assessed with written findings
- [ ] If viable, document which known issues are resolved
- [ ] If not viable, document specific blockers

---

## Verification

After completing any generator task (1–4):

```bash
cd TestFramework
./build-and-test.sh
./generate-coverage-report.sh

python3 -c "
import json
with open('output/coverage-matrix.json') as f:
    d = json.load(f)
mp = d['summary']['must_pass']
print(f'Must-pass: {mp[\"passing\"]}/{mp[\"total\"]} passing, {mp[\"degraded\"]} degraded')
for f in d['features']:
    if f.get('test_status') == 'degraded':
        print(f'  {f[\"name\"]}: {len(f.get(\"binding_skips\",[]))} skips')
"
```
