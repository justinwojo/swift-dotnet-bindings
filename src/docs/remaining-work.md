# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Current**: Phase 53 complete, 1238 unit tests, 93/93 must-pass features
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
| ~~8~~ | ~~Validation~~ | ~~BlinkID runtime validation~~ | ~~15/18 tests pass. 3 failures now addressable via Phase B String dispatch.~~ | ~~Done~~ |
| ~~19B~~ | ~~Generator~~ | ~~Witness dispatch Phase B (String + setters)~~ | ~~String marshalling via SBW_Utf8Slice, property setters, exception-safe GCHandle cleanup~~ | ~~Done~~ |
| ~~21~~ | ~~Generator~~ | ~~UnsupportedSignature triage~~ | ~~All 30 skips categorized. No low-cost fixable cluster — largest are UIKit (10) and Foundation.Bundle (8), both structural.~~ | ~~Done~~ |
| 12B | Generator | Emitter decomposition Phase B | Maintainability / onboarding | P3 |
| 6 | Runtime | Async callback marshalling (Array, String) | 2 async tests blocked | P3 |
| 14 | Runtime | .NET convenience methods on Swift runtime types | DX friction | P3 |
| 15 | Testing | Deeper protocol runtime tests | Protocol tests are compile checks only | P3 |
| 16 | Infrastructure | Upstream .NET runtime bug reports | Mono JIT bugs block existentials/async | P3 |
| 17 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety | P3 |
| 18 | Research | NativeAOT investigation | Desk research done — Blocker #1 bypassed, #2 persists, #3 uncertain. Hands-on testing remaining. | P4 |

### Planned execution order

1. ~~**Item 20** — AnyTypeFallback investigation~~ ✓ Done — all were stale except 1 QuartzCore gap
2. ~~**Item 8** — BlinkID runtime validation~~ ✓ Done — 15/18 tests pass, 3 failures now addressable via Item 19B
3. ~~**Item 19B** — Witness dispatch String marshalling + setters~~ ✓ Done — SBW_Utf8Slice bridge, property setters, projected-type validation gates
4. ~~**Item 21** — UnsupportedSignature triage~~ ✓ Done — all 30 categorized, no low-cost fixable cluster
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

This is a known .NET Mono JIT limitation. Integer-based enums (DocumentOrientation, DocumentRotation, ImageAnalysisDetectionStatus, DocumentImageColorStatus) work perfectly. String-based enum raw values require IntPtr + manual marshalling workaround. The `SBW_Utf8Slice` bridge from Phase 53 (Item 19B) demonstrates the pattern needed — the same approach can be applied to enum raw value accessors.

### Build Issues Documented

1. **Generated Swift wrapper (`Swift.BlinkID.swift`) has compilation errors**: `@convention(c)` callbacks with non-ObjC-representable types (BlinkIDSdk, PingStatus), EveryProtocol doesn't conform to Decodable (Pinglet protocol). SwiftBindings.xcframework disabled in csproj.
2. **Protocol conformance static/instance mismatch**: 9 Pinglet types emit static `SchemaName`/`SchemaVersion` but `ISwiftPinglet` interface requires instance members. Workaround: removed `: ISwiftPinglet` from generated code.
3. **`libSwiftBindingsRuntime.dylib` InstallNameTool failure**: Native library from Phase 49 causes MSBuild `_InstallNameTool` task crash (file not copied to nativelibraries dir). Fix: `IncludeSwiftBindingsRuntimeNative` MSBuild property in `Swift.Runtime.csproj` (default `true`) gates native dylib inclusion; BlinkIdTestApp sets it to `false` via `AdditionalProperties` on the project reference.

---

## ~~19B. Protocol Witness Table Dispatch -- Phase B~~ ✓ DONE

**Status**: Completed in Phase 53. String marshalling and property setters work through witness dispatch.

### What was implemented

**`SBW_Utf8Slice` bridge struct** — `@frozen` Swift struct + `[StructLayout(Sequential)]` C# struct for ABI-stable UTF-8 transfer across the P/Invoke boundary. Emitted once per protocol that has String members.

**String property getters** — Swift accessor encodes `String` to `Array(result.utf8)`, allocates `SBW_Utf8Slice`, returns pointer. C# decodes via `Encoding.UTF8.GetString`. Free function deallocates both the buffer and the slice.

**String method returns/params** — Same `SBW_Utf8Slice` bridge. Parameters use `GCHandle.Alloc` pinning with exception-safe cleanup (`default(GCHandle)` declaration before `try`, `IsAllocated` check in `finally`).

**Property setters** — Blittable setters use typed pointee assignment (`typedPtr.pointee = existential`) for correct ARC/value semantics. String setters encode to UTF-8 via `fixed` block and pass `Utf8Slice` pointer. `_swiftContainer` field changed from `readonly` to mutable for write-back.

**Projected-type validation gates** — `IsSwiftStringProjectedType()` validates properties project to `Swift.SwiftString` (not `Swift.AnyType`). `IsIdiomaticStringType()` validates method params/returns project to `string`. Prevents dispatch when TypeDatabase is incomplete.

### Files modified

| File | Changes |
|------|---------|
| `WitnessDispatchEmitter.cs` | `SBW_Utf8Slice` struct emission, `IsStringType`/`IsTypeDispatchable`/`IsStringDispatchType`, `IsPropertySetterDispatchable`, String Swift accessors for getters/setters/methods |
| `ProtocolProxyEmitter.cs` | `Utf8Slice` C# struct, mutable `_swiftContainer`, String dispatch in property/method impl, setter dispatch, exception-safe GCHandle cleanup, P/Invoke declarations, projected-type gates |
| `WitnessDispatchEmitterTests.cs` | 22 updated/new tests for String dispatch, setters, `SBW_Utf8Slice` |
| `ProtocolProxyEmitterTests.cs` | 14 updated/new tests for String dispatch, setters, `Utf8Slice`, projected-type validation |

### Remaining sub-items (not in scope for Phase B)

- **Mutating methods**: Same `readonly` fix applied, but no dedicated test coverage yet
- **Subscripts**: Index parameter + return value dispatch
- **Non-frozen types, closures, generics**: Complex marshalling (longer term)

### Verification

- Unit tests: 1238/1238 passed
- TestFramework: 93/93 must-pass, 0 degraded

---

## ~~21. UnsupportedSignature Triage~~ ✓ DONE

**Status**: Investigation complete. All 30 skips categorized. No low-cost fixable cluster identified.

### Triage Results (30 skips)

| Category | Count | Libraries | Root Cause | Fixable? |
|----------|-------|-----------|------------|----------|
| Placeholder types | 5 | BlinkID(2), Nuke(3) | Constructor/method signatures with unresolved generic or internal type params | Low priority — requires deeper generic/placeholder resolution |
| UIKit touch/event methods | 8 | Lottie | `beginTracking`/`endTracking`/etc. reference UITouch, UIEvent, CGPoint | Needs UIKit type records in TypeDatabase |
| Foundation.Bundle params | 8 | Lottie | `named()`, `asset()`, `init(bundle:)` reference Foundation.Bundle | Needs Foundation.Bundle type record |
| CALayer content gravity | 2 | Lottie | `contentsGravity` returns `CALayerContentsGravity` (QuartzCore type alias) | Needs QuartzCore type support |
| Logger autoclosures | 4 | Lottie | `LottieLogger` methods use `@autoclosure () -> String`, `StaticString` | New pattern — generator doesn't handle `@autoclosure` |
| UIControl.State / ClosedRange | 2 | Lottie | `setPlayRange(ClosedRange<CGFloat>)`, `isOn(UIControl.State)` | Needs ClosedRange + UIKit type support |
| Lottie animation config | 1 | Lottie | `LottieButton.animate` with unresolved config type | Unclear without further investigation |

### Assessment

The two largest clusters (UIKit types: 10 skips, Foundation.Bundle: 8 skips) both require adding cross-framework ObjC type records to the TypeDatabase. This is structural work affecting the type resolution pipeline broadly — not a targeted fix. No code changes made for this item.

---

## 12B. Emitter Decomposition -- Phase B (Handler Extraction)

**Priority**: P3
**Area**: Generator (emitter)

Phase A (file split) is complete — all files <= 800 LOC. Phase B extracts true handler components per `emitter-redesign-proposal.md`:

1. Split into focused handlers (Constructor, Static/Instance, SwiftError, Generic, Async)
2. Extract return handlers (IndirectResult, BoundGeneric, Direct, Void)
3. Extract argument handlers (NonFrozen, Generic, BoundGeneric)

`EnumHandler.cs` (1,715 LOC) and `ProtocolProxyEmitter.cs` (1,964 LOC) are also decomposition candidates.

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

## 14. .NET Convenience Methods on Swift Runtime Types ✅

**Priority**: P3
**Area**: Runtime (DX)
**Status**: Complete

C# developers encounter unfamiliar types (`SwiftArray<T>`, `SwiftString`, `SwiftOptional<T>`). Adding .NET-idiomatic convenience methods preserves zero-copy performance while improving developer experience.

**What was implemented**:

`SwiftArray<T>` (`src/Swift.Runtime/src/Swift/SwiftArray.cs`):
- `ToArray()` — copies elements to a new .NET array using indexer
- `ToList()` — copies elements to a new `List<Element>` using indexer
- `ToString()` — returns `"SwiftArray<ElementType>[Count]"`
- Fixed native memory leak in indexer getter — `NativeMemory.Alloc` buffer was never freed after `MarshalFromSwift` copied data out; added try/finally with `NativeMemory.Free`

`SwiftString` (`src/Swift.Runtime/src/Swift/SwiftString.cs`) — already had all needed methods:
- `ToString()` (line 141)
- `implicit operator string` (line 207)
- `implicit operator SwiftString` (line 197)

No changes needed to SwiftString.

Tests (`src/Swift.Runtime/tests/LibraryTests/SwiftArrayTests.cs`):
- 9 new tests covering `ToArray()`, `ToList()`, `ToString()` for empty/non-empty arrays and SwiftString element type

**Acceptance criteria**:
- [x] `SwiftArray<T>` has `ToList()` and `ToArray()` convenience methods
- [x] `SwiftArray<T>` has `ToString()` override
- [x] `SwiftArray<T>` indexer getter frees temporary native buffer
- [x] New convenience methods have test coverage
- [x] `SwiftString` has implicit conversion to `string` (already existed)
- [x] `SwiftString` has `ToString()` override (already existed)

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

## 18. NativeAOT Investigation — RESEARCH COMPLETE, HANDS-ON TESTING REMAINING

**Priority**: P4
**Area**: Research
**Status**: Desk research complete. Hands-on testing not yet started.
**Details**: `src/docs/nativeaot-investigation.md`

### Research findings (Grok + Gemini consultation, Feb 2026)

| Blocker | NativeAOT Impact | Confidence |
|---------|-----------------|------------|
| #1 `!ji->async` JIT assertion crash | **Bypassed** (no JIT in NativeAOT) | High |
| #2 Non-blittable types with `CallConvSwift` | **Likely persists** (ILCompiler enforces same restriction) | Medium |
| #3 SafeHandle across async P/Invoke | **Uncertain** (needs testing) | Low |

NativeAOT eliminates the most severe blocker (existential metadata crash) and is viable for iOS deployment (.NET 9+). The other two blockers likely require the same workarounds we already have.

### Remaining work (hands-on testing)
1. Build a minimal NativeAOT iOS test app reproducing the three blockers
2. Test `[LibraryImport]` + `CustomMarshaller` for `SwiftOptional<T>` blittable lowering
3. Run matrix tests with `[DllImport]` vs `[LibraryImport]` under NativeAOT

**Acceptance criteria**:
- [x] NativeAOT feasibility assessed with written findings
- [x] Document which known issues are resolved (Blocker #1 bypassed)
- [x] Document specific blockers (Blocker #2 persists, #3 uncertain)
- [ ] Hands-on validation with NativeAOT iOS test app

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
