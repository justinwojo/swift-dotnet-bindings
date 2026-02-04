# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Starting Point**: Phase 48 complete, 1107 unit tests, TestFramework v2.0 (67 files, 145 features)
**Current**: Phase 51 complete, 1151 unit tests, 93/93 must-pass features

---

## Status Summary

| # | Area | Description | Impact | Priority |
|---|------|-------------|--------|----------|
| ~~1~~ | ~~Generator~~ | ~~Generic tuple return marshalling (deferred from Phase 46)~~ | ~~1 degraded feature, 1 skipped member~~ | ~~Done (Phase 48)~~ |
| ~~2~~ | ~~Generator~~ | ~~OpaquePointer in method signatures~~ | ~~2 degraded features, 3 skipped members~~ | ~~Done (Phase 45)~~ |
| ~~3~~ | ~~Generator~~ | ~~NSObject subclass as method parameter~~ | ~~1 degraded feature, 1 skipped member~~ | ~~Done (Phase 45)~~ |
| ~~4~~ | ~~Generator~~ | ~~Existential type argument in bound generic~~ | ~~1 degraded feature, 1 skipped member~~ | ~~Done (Phase 50)~~ |
| ~~5~~ | ~~Runtime~~ | ~~Formalize async concurrency hook as shared library~~ | ~~Async init is copy-pasted per test; no reusable runtime~~ | ~~Done (Phase 49)~~ |
| 6 | Runtime | Fix async callback marshalling (Array, String) | 2 async tests blocked | P3 |
| ~~7~~ | ~~Validation~~ | ~~Lottie 9/9 — fix `LottieConfiguration.Shared` getter~~ | ~~1 runtime test failure~~ | ~~Done (Phase 48)~~ |
| 8 | Validation | BlinkID runtime validation test app | Compiles but never runtime tested | P3 |
| ~~9~~ | ~~Runtime~~ | ~~Add finalizer safety net to `SwiftSafeHandle`~~ | ~~Memory leaks if users forget `Dispose()`~~ | ~~Done (Phase 45)~~ |
| ~~10~~ | ~~Generator~~ | ~~Protocol runtime completion (remove `NotImplementedException` stubs)~~ | ~~Blocks real protocol usage from C#~~ | ~~Done (Phase 47)~~ |
| ~~11~~ | ~~Generator~~ | ~~Wrapper automation for known-problematic patterns~~ | ~~Manual per-library patches don't scale~~ | ~~Done (Phase 51)~~ |
| 12 | Generator | Emitter decomposition — split MethodHandler (Phase A done, Phase B pending) | Blocks maintainability and onboarding | P2 |
| ~~13~~ | ~~Generator~~ | ~~Improve binding report with workaround recommendations~~ | ~~Consumers don't know what to do about gaps~~ | ~~Done (Phase 51)~~ |
| 14 | Runtime | .NET convenience methods on Swift runtime types | DX friction with unfamiliar Swift types | P3 |
| 15 | Testing | Deeper protocol runtime tests | Protocol tests are compile checks, not runtime behavior | P3 |
| 16 | Infrastructure | Upstream .NET runtime bug reports with minimal repros | Mono JIT bugs block existentials/async | P3 |
| 17 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety for lifetime management | P3 |
| 18 | Research | NativeAOT investigation (`[LibraryImport]` for Mono JIT bypass) | May eliminate known runtime bugs | P4 |
| 19 | Generator | Protocol witness table dispatch for Swift-backed existentials | Swift-backed proxies can't access members | P3 |

**Generator target**: 93/93 must-pass features passing ✅ (achieved Phase 50)

### Planned execution order

Items aren't tackled linearly by number. The agreed sequence prioritizes the fastest coverage win, then long-term leverage:

1. ~~**Item 4** — Existential in bound generic (narrow fix, 92→93/93)~~ ✅ Done
2. ~~**Items 11 + 13** — Wrapper automation + binding report guidance (removes recurring manual work)~~ ✅ Done
3. **Item 19** — Protocol witness table dispatch (makes existentials functionally useful)
4. **Item 16** — Upstream Mono bug reports (in parallel as infra hygiene)

---

## 1. ~~Generic Tuple Return Marshalling~~ ✅ Done (Phase 48)

Generic tuple returns (e.g., `pair<T, U>() -> (T, U)`) now emit correctly. Generator changes:
1. `MarshallingHelpers.MethodRequiresIndirectResult` returns `true` for tuples with generic elements
2. `CSSignatureBuilder.HandleReturnType` accepts generic-element tuples via `GenericContext`
3. `PInvokeSignatureBuilder.HandleReturnType` lets generic-element tuples fall through to indirect result handling
4. Both signature builders reject generic-element tuples on async methods (async skips indirect result, so the required sret path is unavailable)

The runtime already supported tuple metadata (`TryGetTupleTypeMetadata`) and per-element extraction (`MarshalTupleFromSwift`) — only the generator needed changes. TestFramework: `generic_function` moved from degraded to passing (92/93 must-pass).

---

## 2. ~~OpaquePointer in Method Signatures~~ ✅ Done (Phase 45)

Added `IntPtrType` static TypeRecord and `IsPointerType()` helper in `TypeDatabaseExtensions.cs`. Early-return checks in all 5 resolution methods cover `OpaquePointer`, `UnsafePointer`, `UnsafeMutablePointer`, `UnsafeRawPointer`, `UnsafeMutableRawPointer`, and `Builtin.RawPointer`. Optional<OpaquePointer> resolves automatically through the existing Optional handler. 46 unit tests added.

---

## 3. ~~NSObject Subclass as Method Parameter~~ ✅ Done (Phase 45)

Added synthetic ObjCBridged TypeRecord generation for known ObjC root classes (NSObject, NSProxy) in `TypeDatabaseExtensions.cs`. Handles TypeSpecParser's `ObjectiveC.X → Foundation.X` remapping with a narrow predicate (`IsKnownObjCRootClass`). DB-first precedence preserved — explicit type database entries override synthetic records. Non-class ObjectiveC module types (Selector, ObjCBool) correctly excluded via `IsObjCRootClassSwiftType()`. 8 unit tests added.

---

## 4. ~~Existential Type Argument in Bound Generic~~ ✅ Done (Phase 50)

Two-part fix in `BoundGenericsHandler.cs` and `MethodHandler.cs`:

1. **Type translation**: `TranslateTypeSpecToCSharp()` now resolves supported existentials (0–8 protocols) to `ExistentialContainer{N}` instead of blanket `AnyType` fallback. Uses `ExistentialHandler.ToProtocolListTypeSpec()` + `IsSupportedExistential()` + `GetCSharpExistentialType()`. Unsupported existentials (9+ protocols) still fall back to `AnyType`.
2. **Method guard**: Added `TryGetFirstUnsupportedExistentialTypeArgument()` — same recursive structure as `TryGetFirstExistentialTypeArgument` but only returns `true` for unsupported existentials. `MethodHandler.Emit` calls this narrower check, allowing methods like `describeAll([any Describable])` to emit as `SwiftArray<ExistentialContainer1>`.

Constructor and property guards intentionally left on the broader check to limit blast radius.

**Acceptance criteria** (all met):
- [x] `describeAll` emits without `[UnsupportedSwiftType]`
- [x] Coverage report: `any_protocol_existential` shows `passing` (93/93 must-pass)
- [x] 1110 unit tests pass (3 new tests for `TryGetFirstUnsupportedExistentialTypeArgument`)

---

## 5. ~~Formalize Async Concurrency Hook~~ ✅ Done (Phase 49)

Extracted the inline concurrency hook from `AsyncTests.swift` into a proper shared library:

1. `src/Swift.Runtime/swift/SwiftBindingsRuntime.swift` — GCDExecutor + `dlsym`-based hook, exported as `SwiftBindings_InitializeConcurrency` and `SwiftBindings_IsConcurrencyInitialized` via `@_cdecl`
2. `src/Swift.Runtime/swift/build-runtime.sh` — Builds `libSwiftBindingsRuntime.dylib` for macOS, iOS device, and iOS Simulator targets
3. `src/Swift.Runtime/src/Swift/Runtime/SwiftConcurrency.cs` — Thread-safe `SwiftConcurrency.Initialize()` with double-checked locking, `IsInitialized` property, XML documentation of limitations
4. `Swift.Runtime.csproj` updated to include native library per platform target
5. `AsyncTests.cs` updated to use `SwiftConcurrency.Initialize()` instead of test-specific P/Invoke; inline hook removed from `AsyncTests.swift`

**Acceptance criteria** (all met):
- [x] `SwiftConcurrency.Initialize()` callable from any .NET app
- [x] AsyncTests use shared library instead of inline hook
- [x] `TestInstanceMethods` and `TestStaticMethods` still pass

---

## 6. Async Callback Marshalling (Array, String)

**Priority**: P3
**Area**: Runtime

`TestArray` and `TestString` async integration tests are blocked by callback marshalling errors (`Cannot marshal type System.String from Swift`). This is separate from the concurrency hook issue (item 5) — the hook works, but the async callback that delivers the result can't marshal complex types.

**Acceptance criteria**:
- [ ] `TestArray` passes
- [ ] `TestString` passes

---

## 7. ~~Lottie 9/9 — LottieConfiguration.Shared~~ ✅ Done (Phase 48)

Root cause: Generated `==` and `!=` operators on reference types accessed `.Payload` without null checks. When C# code did `config != null`, it invoked the overloaded `!=` operator which delegated to `==`, passing `null` as the second argument. `arg1.Payload` threw `NullReferenceException`.

Fix: `OperatorHandler.cs` now emits null guards (`if (arg0 is null) return arg1 is null;`) for equality/inequality operators when the containing type is a C# reference type (ClassDecl, EnumDecl, non-frozen StructDecl, frozen-struct-projected-as-class). Guards are emitted for both explicit operators and synthesized paired operators. Lottie: 9/9 runtime tests pass.

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

## 9. ~~Add Finalizer Safety Net to SwiftSafeHandle~~ ✅ Done (Phase 45)

Added `GC.SuppressFinalize(this)` to `Dispose()` in `SwiftHandle.cs` — standard .NET SafeHandle pattern. Added `Debug.WriteLine` diagnostic warning when a SwiftSafeHandle is finalized without explicit Dispose, alerting developers to the ARC leak. Note: Swift `Destroy` is deliberately skipped during finalization to avoid SIGSEGV from the Swift runtime during .NET shutdown — the buffer is still freed, but Swift ARC is not decremented. This is documented as a known tradeoff.

---

## 10. ~~Protocol Runtime Completion~~ ✅ Done (Phase 47)

Replaced all 7 `NotImplementedException` stubs in `ProtocolProxyEmitter.cs` with descriptive `NotSupportedException` throws. The Swift existential code path (when `_csharpImpl == null`) now throws `NotSupportedException` with messages identifying the specific member and explaining the limitation. The conformance descriptor stub similarly throws `NotSupportedException` explaining that proxy types use EveryProtocol's witness table.

All `TODO` comments removed. XML documentation on the existential container constructor documents the limitation. 8 new tests verify the degradation behavior.

**Acceptance criteria** (all met):
- [x] Zero `NotImplementedException` paths remain in `ProtocolProxyEmitter.cs`
- [x] Protocol proxy types degrade gracefully: Swift-backed proxies throw `NotSupportedException` for member access with descriptive messages
- [x] All 1107 unit tests pass (8 new)

---

## 11. ~~Wrapper Automation for Known-Problematic Patterns~~ ✅ Done (Phase 51)

First-cut automation for existential-in-bound-generic constructors via `ExistentialBypassEmitter`. When a struct constructor has bound generic params containing existential type arguments and all such params have `HasDefaultArg == true`, the generator auto-emits:

1. **Swift wrapper**: `@_silgen_name` function that omits existential params (Swift fills defaults), heap-allocates the result, and returns `UnsafeMutableRawPointer`. Companion free function for cleanup.
2. **C# factory**: Static `Create_{hash}` method with try/finally cleanup, P/Invoke declarations (inline or via `PInvokeHelperContext` for generic types), frozen/non-frozen copy strategies.
3. **Binding report**: `WrappedItems` list with `WrapperKind`, `MangledName` (for overload disambiguation), and details.

**Safety gates** (return false → falls back to skip):
- Parent must be a StructDecl; failable/throwing constructors rejected
- All existential params must have `HasDefaultArg == true`
- Passthrough params with `IsGeneric == true` rejected (no GenericTypeMapping for reduced method)
- Reduced signature must have no placeholders
- Wrapper and P/Invoke parameter signatures must match exactly (rejects types needing marshalling setup: SafeHandle, idiomatic conversions, indirect results)

**Scope limitation**: Handles constructors only, existential-in-bound-generic pattern only. Async SafeHandle and non-blittable CallConvSwift patterns are deferred.

**Acceptance criteria** (all met):
- [x] Generator auto-generates Swift wrappers for existential-in-bound-generic constructors
- [x] Binding report includes `WrappedItems` annotation with wrapper kind and mangled name
- [x] 1151 unit tests pass (20 new ExistentialBypassEmitter tests)

---

## 12. Emitter Decomposition — Split MethodHandler

**Priority**: P2 — architecture review top-4 blocker
**Area**: Generator (emitter)
**Source**: Comprehensive architecture review (all reviewers)

### Phase A: File Split (Complete)

The original `MethodHandler.cs` (3,827 LOC) has been split into 7 files with no behavioral changes:

| File | Lines | Contents |
|------|-------|----------|
| `MethodHandler.cs` | 369 | Handler factories + handlers (public API) |
| `MethodSignature.cs` | 517 | Parameter, Signature, SignatureBuilderBase, WrapperSignatureBuilder, SignatureHandler |
| `PInvokeEmitter.cs` | 535 | PInvokeSignatureBuilder, PInvokeEmitter |
| `WrapperEmitter.cs` | 692 | Core orchestration, constructor emission, structural helpers |
| `WrapperEmitter.Async.cs` | 779 | Async emission pipeline |
| `WrapperEmitter.Marshalling.cs` | 546 | Argument marshalling, closures, SafeHandle, generics |
| `WrapperEmitter.Return.cs` | 444 | Return handling, tuple element helpers |

All files ≤800 LOC. All 1107 unit tests pass. TestFramework coverage unchanged at time of decomposition.

### Phase B: Handler Extraction (Pending)

Extract true handler components per `emitter-redesign-proposal.md`:
1. Split into focused handlers (Constructor, Static/Instance, SwiftError, Generic, Async)
2. Extract return handlers (IndirectResult, BoundGeneric, Direct, Void)
3. Extract argument handlers (NonFrozen, Generic, BoundGeneric)

Phase A is a prerequisite: the code must be navigable before it can be restructured.

`EnumHandler.cs` (1,715 LOC) and `ProtocolProxyEmitter.cs` (1,403 LOC) are also candidates for decomposition.

**Reference**: `src/docs/emitter-redesign-proposal.md`

**Acceptance criteria**:
- [x] No single handler file exceeds ~800 lines (Phase A)
- [x] Existing unit tests still pass with no behavioral changes (Phase A)
- [ ] Handler responsibilities are clear and focused (Phase B)

---

## 13. ~~Improve Binding Report with Workaround Recommendations~~ ✅ Done (Phase 51)

Added `RecommendedWorkaround` field to `SkippedItem` in the binding report data model. `WorkaroundRecommendations.GetRecommendation(SkipReason)` maps all 14 skip reasons to actionable guidance text. Wired into both `RecordTypeSkipped` and `RecordMemberSkipped` in `ReportCollector`. JSON serialization picks it up automatically.

**Files**:
- `BindingReport.cs` — added `RecommendedWorkaround` property to `SkippedItem`
- `WorkaroundRecommendations.cs` — static mapping for all 14 `SkipReason` values
- `ReportCollector.cs` — populates workaround on skip recording
- `ReportEmitter.cs` — wrapper count in console summary

**Acceptance criteria** (all met):
- [x] `binding-report.json` includes workaround guidance for all skipped items
- [x] All 14 skip reasons have mapped workarounds
- [x] 4 new WorkaroundRecommendationsTests + 6 new ReportCollectorTests

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

## 19. Protocol Witness Table Dispatch for Swift-Backed Existentials

**Priority**: P3
**Area**: Generator (emitter + runtime)
**Source**: Follow-up from item 10 (Phase 47)

When C# receives a protocol-typed value from Swift (e.g., `any Describable`), the proxy is created via the existential container constructor. Currently these proxies throw `NotSupportedException` for all member access. Full support requires generating Swift accessor functions that dispatch through the witness table, plus corresponding P/Invoke declarations.

**What's needed**:
1. Generate `@_silgen_name` functions in EveryProtocolEmitter that take existential container pointers, cast to `any Protocol`, call member, return result
2. Generate P/Invoke declarations in ProtocolProxyEmitter's NativeMethods
3. Replace `NotSupportedException` throws with actual P/Invoke calls
4. Handle marshalling for each return/parameter type

**Acceptance criteria**:
- [ ] At least property getters work through Swift-backed existentials
- [ ] Method dispatch through witness tables works for simple signatures
- [ ] Tests exercise round-trip: Swift creates existential → C# calls method through proxy

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
