# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Current**: Phase 52 complete, 1216 unit tests, 93/93 must-pass features
**Completed items**: See `CompletedPhases/completed-backlog-items.md`

---

## Real-World Binding Status

Reassessed February 2026 after Phases 49-52 (binding reports regenerated).

| Metric | BlinkID | Nuke | Lottie |
|--------|---------|------|--------|
| Types emitted | 116/119 (97.5%) | 60/68 (88.2%) | 79/93 (84.9%) |
| Members emitted | 569/655 (86.9%) | 330/490 (67.3%) | 387/609 (63.5%) |
| Members skipped | 3 | 12 | 42 |
| `[UnsupportedSwiftType]` attrs | 0 | 0 | 25 |
| Runtime validated | Yes (15/18 tests) | Yes | Yes (9/9 tests) |
| Compile errors | 18* | 26* | 39* |

### Skip Reasons Across All Three Libraries (57 total skips)

| Reason | BlinkID | Nuke | Lottie | Total | Notes |
|--------|---------|------|--------|-------|-------|
| UnsupportedSignature | 2 | 3 | 26 | **31** | UIKit types, placeholder types, complex accessors |
| UnsupportedExistential | 0 | 6 | 2 | **8** | Existential type args in bound generics without default args |
| AnyTypeFallback | 0 | 0 | 1 | **1** | QuartzCore.CALayer not in type database |
| UnsatisfiedGenericConstraint | 0 | 0 | 5 | **5** | Generic constraint not satisfiable (Lottie `AnyInterpolatable`) |
| UnsupportedType | 1 | 0 | 4 | **5** | Type resolution failed (UIKit nested types, actor executors) |
| UnsupportedClosure | 0 | 1 | 3 | **4** | Non-invokable closure types |
| AsyncProperty | 0 | 2 | 0 | **2** | Async getters (Nuke `ImageTask.image`, `.response`) |
| SwiftUIConstraint | 0 | 0 | 1 | **1** | By design (LottieView) |

*\* Compile errors are all protocol conformance interface mismatches introduced by Phase 52's protocol conformance emission. They affect types where emitted protocol members don't match the generated interface (static vs instance members, missing implementations). Workaround: remove interface from class declarations. These errors were not present in pre-Phase-52 bindings.*

**Improvement from Phase 49-52**: 94 → 57 skips (-39%). BlinkID went from 15 → 3 skips (all 10 AnyTypeFallback resolved). Nuke went from 19 → 12 skips. Lottie went from 60 → 42 skips.

---

## Status Summary

| # | Area | Description | Impact | Priority |
|---|------|-------------|--------|----------|
| ~~20~~ | ~~Generator~~ | ~~AnyTypeFallback investigation~~ | ~~Resolved: 13/14 were stale (already fixed by Phase 50-52 generic improvements). 1 remaining is QuartzCore.CALayer type gap.~~ | ~~Done~~ |
| ~~8~~ | ~~Validation~~ | ~~BlinkID runtime validation~~ | ~~15/18 tests pass. 3 failures are known SwiftString non-blittable P/Invoke issue.~~ | ~~Done~~ |
| 19B | Generator | Witness dispatch Phase B (String marshalling first) | Enables non-blittable existential dispatch | P2 |
| 21 | Generator | UnsupportedSignature triage | 30 skipped members across all libraries | P2 |
| 12B | Generator | Emitter decomposition Phase B | Maintainability / onboarding | P3 |
| 6 | Runtime | Async callback marshalling (Array, String) | 2 async tests blocked | P3 |
| 14 | Runtime | .NET convenience methods on Swift runtime types | DX friction | P3 |
| 15 | Testing | Deeper protocol runtime tests | Protocol tests are compile checks only | P3 |
| 16 | Infrastructure | Upstream .NET runtime bug reports | Mono JIT bugs block existentials/async | P3 |
| 17 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety | P3 |
| 18 | Research | NativeAOT investigation | May eliminate known runtime bugs | P4 |

### Planned execution order

1. ~~**Item 20** — AnyTypeFallback investigation~~ ✓ Done — all were stale except 1 QuartzCore gap
2. ~~**Item 8** — BlinkID runtime validation~~ ✓ Done — 15/18 tests pass, 3 failures are known SwiftString non-blittable issue
3. **Item 19B** — Witness dispatch String marshalling (biggest real-world impact)
4. **Item 21** — UnsupportedSignature triage (understand the bucket, find clusters)
5. **Item 16** — Upstream Mono bug reports (in parallel as infra hygiene)

---

## ~~20. AnyTypeFallback Investigation~~ ✓ RESOLVED

**Status**: Investigated and resolved — binding reports were stale (generated before Phases 49-52).
**Resolution**: Regenerating binding reports with the current generator resolved 13 of 14 skips.

### Findings

**BlinkID (10 → 0 AnyTypeFallback)**: All 10 skips were generic type parameter properties on `VehicleClassInfo<T>`, `DateResult<T>`, and `DriverLicenseDetailedInfo<T>`. The generic context propagation through `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp` and `PropertyHandler`'s `isGenericTypeParam` branch already handles these correctly. The binding reports were simply not regenerated after the Phase 50 generic improvements. Members emitted: 559 → 569.

**Lottie (4 → 1 AnyTypeFallback)**:
- `Keyframe.value` (τ_0_0 directly) — **Fixed**: Same generic parameter resolution as BlinkID.
- `LottieButton.body` / `LottieSwitch.body` — **No longer AnyTypeFallback**: These `some View` opaque types are now handled by other skip paths (UnsupportedSignature for placeholder types), not AnyTypeFallback.
- `LottieAnimationLayer.animationLayer` — **Remains**: Type is `Optional<QuartzCore.CALayer>`. `CALayer` is an Objective-C class from QuartzCore which is not covered by the ObjC bridging logic (only Foundation/ObjectiveC modules are bridged). This is a structural gap requiring broader ObjC framework module support.

### Verification
- Unit tests: 1216/1216 passed
- TestFramework: 93/93 must-pass features, 0 degraded
- BlinkID: 3 skips remaining (0 AnyTypeFallback)
- Lottie: 42 skips remaining (1 AnyTypeFallback — QuartzCore gap)

---

## ~~8. BlinkID Runtime Validation~~ ✓ DONE

**Status**: Completed — 15/18 tests pass on iOS Simulator.

### Test Results (18 tests across 6 suites)

| Suite | Tests | Passed | Failed |
|-------|-------|--------|--------|
| Type Metadata | 3 | 3 | 0 |
| Enum Cases | 4 | 3 | 1 |
| Enum Raw Values | 4 | 2 | 2 |
| Enum FromRawValue | 2 | 2 | 0 |
| Static Properties | 1 | 1 | 0 |
| Extended Metadata | 4 | 4 | 0 |

### Failures (all same root cause)

All 3 failures are **non-blittable types in P/Invoke with Swift calling convention**:
- `DetectionStatus.FromRawValue(string)` — SwiftString parameter
- `Country.RawValue` getter — SwiftString return
- `DocumentType.RawValue` getter — SwiftString return

Error: `Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.`

This is a known .NET Mono JIT limitation. Integer-based enums (DocumentOrientation, DocumentRotation, ImageAnalysisDetectionStatus, DocumentImageColorStatus) work perfectly. String-based enum raw values require IntPtr + manual marshalling workaround (tracked in Item 19B for protocol witness dispatch, same issue).

### Build Issues Documented

1. **Generated Swift wrapper (`Swift.BlinkID.swift`) has compilation errors**: `@convention(c)` callbacks with non-ObjC-representable types (BlinkIDSdk, PingStatus), EveryProtocol doesn't conform to Decodable (Pinglet protocol). SwiftBindings.xcframework disabled in csproj.
2. **Protocol conformance static/instance mismatch**: 9 Pinglet types emit static `SchemaName`/`SchemaVersion` but `ISwiftPinglet` interface requires instance members. Workaround: removed `: ISwiftPinglet` from generated code.
3. **`libSwiftBindingsRuntime.dylib` InstallNameTool failure**: Native library from Phase 49 causes MSBuild `_InstallNameTool` task crash (file not copied to nativelibraries dir). Fix: `IncludeSwiftBindingsRuntimeNative` MSBuild property in `Swift.Runtime.csproj` (default `true`) gates native dylib inclusion; BlinkIdTestApp sets it to `false` via `AdditionalProperties` on the project reference.

---

## 19B. Protocol Witness Table Dispatch -- Phase B (Extended)

**Priority**: P2
**Area**: Generator (emitter + runtime)

Phase A (Phase 52) enabled blittable read-only dispatch. Phase B extends to non-blittable types and write operations. **String marshalling is the highest-impact sub-item** — many real-world protocol members return or accept `String`.

### Sub-items (ordered by real-world impact)

1. **String parameters/returns**: Dedicated marshalling path — `MarshalFromSwift<T>` currently uses `Unsafe.Read<T>` which only works for blittable types. Needs Swift-side String-to-UTF8 conversion in accessor functions, plus C# `SwiftString` construction from returned pointer.
2. **Property setters**: Remove `readonly` from `_swiftContainer` to allow write-back after setter dispatch.
3. **Mutating methods**: Same `readonly` constraint as setters.
4. **Subscripts**: Index parameter + return value dispatch.
5. **Non-frozen types, closures, generics**: Complex marshalling (longer term).

**Acceptance criteria**:
- [ ] String-returning protocol members dispatch correctly
- [ ] Property setters work through Swift-backed existentials
- [ ] Tests exercise round-trip: Swift creates existential -> C# calls method through proxy

---

## 21. UnsupportedSignature Triage

**Priority**: P2
**Area**: Generator
**Source**: Real-world binding analysis (February 2026)

30 skipped members across all three libraries with `UnsupportedSignature`. This is a grab-bag category — triaging it will reveal whether there are fixable clusters or if most are structural limitations.

### Known sub-categories (from binding report details)

**UIKit nested types (Lottie, ~10 skips)**:
- `UIView.ContentMode`, `UIAccessibilityTraits` — nested UIKit types not in TypeDatabase
- `beginTracking`/`endTracking`/`continueTracking`/`cancelTracking` — UIEvent/UITouch params
- These are UIKit event methods inherited by Lottie view subclasses

**Placeholder types (BlinkID + Nuke + Lottie, ~12 skips)**:
- Constructor signatures with unresolved placeholder types
- Methods like `imagePublisher`, `register` with complex generic signatures
- `LottieLogger` methods (`init`, `assert`, `assertionFailure`, `warn`) — likely `@autoclosure` params

**Complex accessor signatures (Nuke, ~4 skips)**:
- `dataCache_Get`, `imageCache_Get`, `delegate_Get` — property accessors with protocol return types
- `dataLoadingError_Get` — error type accessor

**Operator with placeholder (Lottie, 1 skip)**:
- `Keyframe.==` — operator on generic type with unresolved type param

### Investigation approach

1. Categorize all 30 skips by root cause (UIKit types, autoclosure, placeholder generics, etc.)
2. Identify which categories are fixable vs. structural
3. UIKit nested types may be addressable by adding type records (similar to NSObject fix in Phase 45)
4. `@autoclosure` params are a new pattern the generator hasn't handled

**Acceptance criteria**:
- [ ] All 30 skips categorized by root cause
- [ ] Fixable clusters identified with estimated scope
- [ ] At least one cluster fixed if cost is low

---

## 12B. Emitter Decomposition -- Phase B (Handler Extraction)

**Priority**: P3
**Area**: Generator (emitter)

Phase A (file split) is complete — all files <= 800 LOC. Phase B extracts true handler components per `emitter-redesign-proposal.md`:

1. Split into focused handlers (Constructor, Static/Instance, SwiftError, Generic, Async)
2. Extract return handlers (IndirectResult, BoundGeneric, Direct, Void)
3. Extract argument handlers (NonFrozen, Generic, BoundGeneric)

`EnumHandler.cs` (1,715 LOC) and `ProtocolProxyEmitter.cs` (1,403 LOC) are also decomposition candidates.

**Reference**: `src/docs/emitter-redesign-proposal.md`

**Acceptance criteria**:
- [ ] Handler responsibilities are clear and focused
- [ ] No behavioral changes (all tests pass)

---

## 6. Async Callback Marshalling (Array, String)

**Priority**: P3
**Area**: Runtime

`TestArray` and `TestString` async integration tests are blocked by callback marshalling errors (`Cannot marshal type System.String from Swift`). The concurrency hook works, but the async callback that delivers the result can't marshal complex types.

**Acceptance criteria**:
- [ ] `TestArray` passes
- [ ] `TestString` passes

---

## 14. .NET Convenience Methods on Swift Runtime Types

**Priority**: P3
**Area**: Runtime (DX)

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

A Roslyn analyzer can warn at compile time when Swift objects implementing `IDisposable` are created without `using` or explicit `Dispose()`.

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

## Observations: UnsupportedExistential (26 skips)

Not a standalone work item — these are existential type arguments in bound generics where the parameter does **not** have a default value. Phase 51's `ExistentialBypassEmitter` handles the default-arg case. The non-default-arg case requires the caller to actually construct and pass an `ExistentialContainer`, which needs:

- Constructor/method signatures that accept `ExistentialContainer{N}` as a bound generic type argument
- C# callers to box their protocol-conforming object into a container
- Runtime support for existential container construction from C#

This is a deeper architectural problem than the other items. The 26 skips break down as:

| Library | Existential Type | Count |
|---------|-----------------|-------|
| Nuke | `any ImagePipelineDelegate` | 1 |
| Nuke | `any ImageProcessing` | 2 |
| Nuke | `any ImageDecoding` | 1 |
| Nuke | `any Swift.Error` | 1 |
| Nuke | anonymous existential (empty) | 5 |
| Lottie | `any AnimationImageProvider` | 2 |
| Lottie | `any AnyValueProvider` | 2 |
| Lottie | `any AnimationCacheProvider` | 4 |
| Lottie | `any DotLottieCacheProvider` | 3 |
| Lottie | `any Swift.Error` | 2 |
| Lottie | anonymous existential (empty) | 3 |

Most are library-specific provider/delegate protocols. Addressing these would require the generator to emit methods that accept `ExistentialContainer` parameters in bound generic positions — a significant extension of the current existential support.

---

## Verification

After completing any generator task:

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

After completing any real-world binding task, regenerate and compare:

```bash
# BlinkID
cd BindingTesting/BlinkID && ./regenerate-bindings.sh

# Nuke
cd BindingTesting/Nuke && ./regenerate-bindings.sh

# Lottie
cd BindingTesting/Lottie && ./regenerate-bindings.sh

# Quick skip count comparison
for lib in BlinkID/output-ios Nuke/output-ios Lottie/output-ios; do
  echo "=== $lib ==="
  python3 -c "
import json
with open('BindingTesting/$lib/binding-report.json') as f:
    d = json.load(f)
print(f'Types: {d[\"EmittedTypeCount\"]}/{d[\"TotalTypeCount\"]}')
print(f'Members: {d[\"EmittedMemberCount\"]}/{d[\"TotalMemberCount\"]}')
print(f'Skipped: {d[\"SkippedMemberCount\"]}')
"
done
```
