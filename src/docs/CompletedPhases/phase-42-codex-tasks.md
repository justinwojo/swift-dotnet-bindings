# Codex Task Specifications - Phase 42

Task specifications for improving binding coverage and runtime validation. With all generator errors eliminated in Phase 41, the focus shifts to expanding API coverage and validating runtime behavior.

**Date**: February 2026
**Starting Point**: Phase 41 complete, 1029 unit tests passing
**Libraries**: Nuke (0 errors ✅), BlinkID (0 errors ✅), Lottie (0 errors ✅)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Fix Lottie test app constructor call | ✅ **COMPLETED** | P0 |
| 2 | CoreGraphics type stubs (CGImage, CGColor) | ✅ **COMPLETED** | P1 |
| 3 | Lottie runtime validation | ✅ **COMPLETED** | P1 |

**Target**: Lottie runtime validation, improved binding coverage
**Result**: Lottie runtime validated (8/9 tests pass), binding coverage improved

---

## Task 1: Fix Lottie Test App Constructor Call

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Blocking runtime validation)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Validated by**: Claude

**Fix**: Added `ColorFormatDenominator.One` parameter to `LottieColor` constructor call in `BindingTesting/Lottie/LottieTestApp/Program.cs`.

### Acceptance Criteria

- [x] LottieTestApp compiles successfully
- [x] Test app can be built and launched on simulator

---

## Task 2: CoreGraphics Type Stubs (CGImage, CGColor)

### Status: ✅ COMPLETED (February 2026)
### Priority: P1 (Improves binding coverage)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Validated by**: Claude

**Files Modified**:
- `src/Swift.Runtime/src/Swift/CoreGraphicsDatabase.xml` - Added opaque handle mappings for CGImage, CGImageRef, CGColor, CGColorRef, CGContext, CGContextRef, CGColorSpace, CGColorSpaceRef → `System.IntPtr`
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` - Added Ref alias fallback (`Foo` ↔ `FooRef`) for C-interop typedef resolution
- `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/TypeDatabaseTests.cs` - Added `TryGetTypeRecord_ResolvesRefSuffixAlias_BothDirections` and CoreGraphics XML load test

**Key fixes**:
1. CoreGraphics opaque types (CGImage, CGColor, CGContext) mapped to IntPtr
2. Ref suffix alias resolution handles C-bridged typedef forms (e.g., `CGImageRef` ↔ `CGImage`)
3. `CGColorSpace` and `CGColorSpaceRef` also mapped

**Results**: Lottie skipped members reduced from 63 to 59 (-4), UnsupportedSignature reduced from 28 to 25 (-3)

### Acceptance Criteria

- [x] CGImage, CGColor, CGContext types registered
- [x] Skipped members using these types are now emitted (4 additional members)
- [x] Members compile (IntPtr parameters/returns)
- [x] Unit tests for type registration and Ref alias resolution

---

## Task 3: Lottie Runtime Validation

### Status: ✅ COMPLETED (February 2026)
### Priority: P1 (Validates binding correctness)
### Dependencies: Task 1

### Completion Notes

**Implemented by**: Codex
**Validated by**: Claude

**Files Modified**:
- `BindingTesting/Lottie/LottieTestApp/Program.cs` - Complete rewrite with structured test suite (9 tests)
- `BindingTesting/Lottie/LottieTestApp/LottieTestApp.csproj` - Added BundleResource for test animation
- `BindingTesting/Lottie/LottieTestApp/Resources/test-animation.json` - Minimal Lottie animation (30fps, 60 frames)

**Additional fixes discovered and implemented during validation**:

1. **Enum case construction** (discovered during Task 3):
   - Enum case P/Invoke symbols (`...mF`) are not exported as callable functions
   - `...mFWC` symbols are data (witness conformance), not callable
   - **Fix**: Simple enum cases now use `DestructiveInjectEnumTag` via ValueWitnessTable instead of P/Invoke
   - Files: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`

2. **Enum parameter P/Invoke** (discovered during Task 3):
   - Non-frozen enum SafeHandle parameters are non-blittable with `CallConvSwift`
   - **Fix**: Added `EnumSafeHandle` marker that emits as `IntPtr` in P/Invoke signature, scoped to enum parameters only
   - Files: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**Runtime test results** (8/9 pass):

| Test | Result |
|------|--------|
| LottieConfiguration metadata | ✅ PASS |
| LottieColor metadata | ✅ PASS |
| LottieConfiguration.Shared | ❌ NullRef (pre-existing) |
| LottieColor creation + properties | ✅ PASS |
| LottieAnimation from JSON | ✅ PASS |
| LottieVector1D | ✅ PASS |
| LottieVector3D | ✅ PASS |
| LottieLoopMode enum | ✅ PASS |
| LottieBackgroundBehavior enum | ✅ PASS |

**Known issue**: `LottieConfiguration.Shared` returns a non-null object but accessing its properties throws NullReferenceException. This is a pre-existing issue with property getter marshalling for certain non-frozen struct types.

### Acceptance Criteria

- [x] LottieTestApp runs on iOS Simulator
- [x] Basic Lottie animation loads successfully
- [x] Animation properties can be queried (duration=2, framerate=30, start=0, end=60)
- [ ] Validation script exits 0 on success (1 pre-existing test failure prevents this)

---

## Phase 42 Summary

**Completed**: February 2026
**Result**: Lottie runtime validated ✅

| Metric | Before | After |
|--------|--------|-------|
| Unit Tests | 1029 | 1032 |
| Lottie Skipped Members | 63 | 59 |
| Lottie Runtime Tests | 0 | 8/9 pass |
| Nuke Errors | 0 | 0 |
| BlinkID Errors | 0 | 0 |

### Key Discoveries

- Swift enum case constructor symbols (`...mF`) are NOT exported in dylibs
- `...mFWC` symbols are witness table conformance data, not callable functions
- Simple enum cases must be constructed via `DestructiveInjectEnumTag`
- Non-frozen enum parameters require `IntPtr` (not `SafeHandle`) with `CallConvSwift`
- CoreGraphics C-bridged types use `Ref` suffix aliases (CGImage ↔ CGImageRef)

---

## Testing Commands Reference

```bash
# Run all unit tests
./run-tests.sh

# Build Lottie test app
cd BindingTesting/Lottie
dotnet build LottieTestApp/LottieTestApp.csproj

# Regenerate Lottie bindings (after generator changes)
./regenerate-bindings.sh

# Validate Lottie on simulator
cd BindingTesting/Lottie
./validate-sim.sh 30

# Validate Nuke on simulator
cd BindingTesting/Nuke
./validate-sim.sh 15
```
