# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Current**: Phase 56 complete, 1239 unit tests, 93/93 must-pass features
**Completed items**: See `CompletedPhases/completed-backlog-items.md`

---

## Real-World Binding Status

Reassessed February 2026 after Phase 56 (protocol conformance validation).

| Metric | BlinkID | Nuke | Lottie |
|--------|---------|------|--------|
| Types emitted | 116/119 (97.5%) | 60/68 (88.2%) | 79/93 (84.9%) |
| Members emitted | 567/572 (99.1%) | 323/342 (94.4%) | 387/428 (90.4%) |
| Members skipped | 5 | 19 | 41 |
| Runtime validated | Yes (15/18 tests) | Yes | Yes (9/9 tests) |
| Compile errors | **0** ✅ | **0** ✅ | 1* |

*\* Lottie has 1 compile error (CS0315) due to `ExistentialContainer0` not implementing `ISwiftObject` constraint — a pre-existing generic constraint issue unrelated to protocol conformance.*

---

## Priority Items

| # | Area | Description | Impact | Priority |
|---|------|-------------|--------|----------|
| 1 | Generator | ~~Missing protocol member implementations~~ | ~~~80 compile errors~~ | ✅ **Done** |
| 2 | Generator | BlinkID enum raw value String marshalling | 3 remaining BlinkID test failures | **P1** |
| 3 | Testing | Deeper protocol runtime tests | Protocol tests are compile checks only | **P2** |
| 4 | Runtime | Async callback marshalling (Array, String) | 2 async tests blocked | P3 |
| 5 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety | P3 |
| 6 | Research | NativeAOT hands-on validation | Desk research done, testing remaining | P4 |
| 7 | Generator | nint emitted as generic type in integration tests | 13 compile errors | P3 |

---

## 1. Missing Protocol Member Implementations (Nuke/Lottie)

**Priority**: ✅ **COMPLETE** (Phase 56)
**Area**: Generator
**Impact**: ~81 compile errors → **0** (Nuke/BlinkID), 1 (Lottie pre-existing)

### Implementation (Phase 56)

Fixed three categories of protocol conformance compile errors:

1. **CS0535 (missing members)**: Created `ProtocolConformanceValidator` that checks if a concrete type can fully implement a protocol interface *before* declaring the interface. The validator:
   - Uses shared `MemberEmissionValidator` to check if each required member can be emitted
   - Validates property accessor preflight (signature placeholders, generic constraints)
   - Validates method return type projection matches interface (including existential, optional existential, protocol→AnyType handling)
   - Validates native type remapping parity (Foundation.Data→NSData, Foundation.URL→NSUrl)
   - Rejects interfaces requiring subscripts (concrete type subscripts not yet emitted)

2. **CS0738 (return type mismatch)**: Added native type remapping to both `ProtocolHandler.EmitInterfaceMethod` and `ProtocolConformanceValidator` to ensure interface and concrete type signatures match.

3. **CS0111 (duplicate P/Invoke)**: Added `HashSet<string>` deduplication in `ProtocolProxyEmitter.EmitWitnessDispatchPInvokes()`.

### Key Files Changed

| File | Change |
|------|--------|
| `MemberEmissionValidator.cs` | New shared validation (property accessor preflight, method return projection with existential/native remapping) |
| `ProtocolConformanceValidator.cs` | New class validates concrete types against protocol interfaces |
| `ProtocolSignatureHelper.cs` | Extracted signature key generation for method/subscript matching |
| `TypeHandlerHelpers.cs` | Passes validator to `GetImplementedInterfaces()` with module-qualified protocol names |
| `ProtocolHandler.cs` | Added native type remapping to interface method emission |
| `ProtocolProxyEmitter.InterfaceImpl.cs` | Added native type remapping to proxy method implementation |
| `ProtocolProxyEmitter.SwiftObject.cs` | Added P/Invoke deduplication |

### Acceptance Criteria

- [x] Nuke compiles without manual intervention (0 CS errors)
- [x] BlinkID compiles without manual intervention (0 CS errors)
- [x] Lottie: 1 remaining error is pre-existing generic constraint issue (unrelated)
- [x] No regression in TestFramework (93/93 must-pass)
- [x] No regression in unit tests (1239/1239 passed)

---

## 2. BlinkID Enum Raw Value String Marshalling

**Priority**: P1
**Area**: Generator
**Status**: ✅ **Implementation complete** (Phase 55) — runtime validation blocked by separate async issue

**Impact**: 3 remaining BlinkID test failures (15/18 → 18/18)

The 3 failing BlinkID tests are all String-based enum raw value accessors:
- `DetectionStatus.FromRawValue(string)` — SwiftString parameter
- `Country.RawValue` getter — SwiftString return
- `DocumentType.RawValue` getter — SwiftString return

Error: `Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.`

**Solution**: Applied the `SBW_Utf8Slice` pattern from Phase 53 (witness dispatch) to enum raw value accessors.

**Implementation (Phase 55)**:
- `EnumHandler.RawRepresentable.cs` — String raw types now use UTF-8 marshalling via `Utf8Slice` struct
- `Utf8SliceEmitter.cs` — Shared emitter ensures `SBW_Utf8Slice` struct emitted once per module
- Swift wrapper functions: `SBW_{Module}_{Container}_{Enum}_InitWithRawValue` decode UTF-8 → String → call init(rawValue:)
- Module-qualified wrapper symbols prevent collisions for same-named nested enums (e.g., `Container1.ErrorType` vs `Container2.ErrorType`)
- C# marshalling: `System.Text.Encoding.UTF8.GetBytes` → pinned buffer → P/Invoke wrapper

**Code generation verified**:
- ✅ Swift wrappers correctly generated for Country, Region, DetectionStatus, etc.
- ✅ C# Utf8Slice struct and marshalling code generated in each String raw type enum
- ✅ No duplicate SBW_Utf8Slice definitions (shared emitter works)
- ✅ All 1239 unit tests pass (includes regression test for nested enum symbol collisions)

**Runtime validation blocked**: BlinkID Swift wrapper compilation fails due to **pre-existing** async callback errors (unrelated to enum changes):
```
'(BlinkIDSession, Int64) -> Void' is not representable in Objective-C
@escaping attribute only applies to function types
```
These errors occur in `PingManager` and `BlinkIDSdk` async methods — a separate issue requiring async wrapper generator fixes.

**Acceptance criteria**:
- [x] String-based enum raw values use `SBW_Utf8Slice` bridge
- [x] No regression in other bindings (1238/1238 tests pass)
- [ ] BlinkID: 18/18 runtime tests pass *(blocked by async issue)*

---

## 3. Deeper Protocol Runtime Tests

**Priority**: P2
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

## 4. Async Callback Marshalling (Array, String)

**Priority**: P3
**Area**: Runtime

`TestArray` and `TestString` async integration tests are blocked by callback marshalling errors (`Cannot marshal type System.String from Swift`). The concurrency hook works, but the async callback that delivers the result can't marshal complex types.

**Acceptance criteria**:
- [ ] `TestArray` passes
- [ ] `TestString` passes

---

## 5. Roslyn Analyzer for Undisposed Swift Objects

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

## 6. NativeAOT Hands-On Validation

**Priority**: P4
**Area**: Research
**Details**: `src/docs/nativeaot-investigation.md`

Desk research complete. Findings:

| Blocker | NativeAOT Impact | Confidence |
|---------|-----------------|------------|
| #1 `!ji->async` JIT assertion crash | **Bypassed** (no JIT in NativeAOT) | High |
| #2 Non-blittable types with `CallConvSwift` | **Likely persists** | Medium |
| #3 SafeHandle across async P/Invoke | **Uncertain** | Low |

**Remaining work**:
1. Build a minimal NativeAOT iOS test app reproducing the three blockers
2. Test `[LibraryImport]` + `CustomMarshaller` for `SwiftOptional<T>` blittable lowering
3. Run matrix tests with `[DllImport]` vs `[LibraryImport]` under NativeAOT

**Acceptance criteria**:
- [ ] Hands-on validation with NativeAOT iOS test app
- [ ] Document which workarounds apply under NativeAOT

---

## 7. nint Emitted as Generic Type in Integration Tests

**Priority**: P3
**Area**: Generator
**Impact**: 13 compile errors in integration tests

The generator emits `nint<SomeType>` in some contexts where it should emit `nint`. This causes CS0308 errors: "The non-generic type 'nint' cannot be used with type arguments."

**Affected files** (in `src/Swift.Bindings/tests/IntegrationTests/bin/Debug/net10.0/`):
- `MemoryTests/Swift.MemoryTests.cs` — 7 errors
- `UnsafePointer/Swift.UnsafePointerTests.cs` — 6 errors

**Root cause**: Likely in bound generic type resolution where `Swift.Int` (or similar) is being translated with a generic argument that should be stripped.

**Acceptance criteria**:
- [ ] Integration tests compile without CS0308 errors
- [ ] `nint` emitted correctly in all contexts
- [ ] No regression in unit tests or real-world bindings

---

## Observations: UnsupportedExistential (26 skips)

Not a standalone work item — these are existential type arguments in bound generics where the parameter does **not** have a default value. Phase 51's `ExistentialBypassEmitter` handles the default-arg case. The non-default-arg case requires:

- Constructor/method signatures that accept `ExistentialContainer{N}` as a bound generic type argument
- C# callers to box their protocol-conforming object into a container
- Runtime support for existential container construction from C#

| Library | Existential Types | Count |
|---------|------------------|-------|
| Nuke | ImagePipelineDelegate, ImageProcessing, ImageDecoding, Error, anonymous | 10 |
| Lottie | AnimationImageProvider, AnyValueProvider, AnimationCacheProvider, DotLottieCacheProvider, Error, anonymous | 16 |

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
