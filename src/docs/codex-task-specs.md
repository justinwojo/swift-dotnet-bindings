# Codex Task Specifications - Phase 42

Task specifications for improving binding coverage and runtime validation. With all generator errors eliminated in Phase 41, the focus shifts to expanding API coverage and validating runtime behavior.

**Date**: February 2026
**Starting Point**: Phase 41 complete, 1029 unit tests passing
**Libraries**: Nuke (0 errors ✅), BlinkID (0 errors ✅), Lottie (0 errors ✅)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Fix Lottie test app constructor call | 🔲 Pending | P0 |
| 2 | CoreGraphics type stubs (CGImage, CGColor) | 🔲 Pending | P1 |
| 3 | Lottie runtime validation | 🔲 Pending | P1 |

**Target**: Lottie runtime validation, improved binding coverage
**Current**: All libraries compile, Nuke runtime validated

---

## Binding Coverage Analysis

**Lottie Skip Reasons** (63 skipped members):
| Reason | Count | Notes |
|--------|-------|-------|
| UnsupportedSignature | 28 | Often CGImage/UIImage types |
| UnsupportedExistential | 18 | Existentials in bound generics |
| UnsatisfiedGenericConstraint | 5 | SwiftUI/Combine constraints |
| UnsupportedType | 5 | Types without bindings |
| AnyTypeFallback | 4 | Type resolution failures |
| UnsupportedClosure | 3 | Non-invokable closures |
| SwiftUIConstraint | 1 | SwiftUI type constraint |

---

## Task 1: Fix Lottie Test App Constructor Call

### Status: 🔲 Pending
### Priority: P0 (Blocking runtime validation)
### Effort: Trivial (15 minutes)
### Dependencies: None

### Problem Statement

The Lottie test app has a compilation error due to an API change in the generated bindings.

**Error**: `CS7036: There is no argument given that corresponds to the required parameter 'denominator' of 'LottieColor.LottieColor(double, double, double, double, ColorFormatDenominator)'`

**Location**: `BindingTesting/Lottie/LottieTestApp/Program.cs:447`

### Current Code

```csharp
var color = new LottieColor(1.0, 0.5, 0.25, 1.0);
```

### Fix Required

Add the `ColorFormatDenominator` parameter. Check the generated `Swift.Lottie.cs` for the enum values and choose the appropriate one (likely `ColorFormatDenominator.One` for values in 0-1 range).

### Acceptance Criteria

- [ ] LottieTestApp compiles successfully
- [ ] Test app can be built and launched on simulator

---

## Task 2: CoreGraphics Type Stubs (CGImage, CGColor)

### Status: 🔲 Pending
### Priority: P1 (Improves binding coverage)
### Effort: Medium (3-4 hours)
### Dependencies: None

### Problem Statement

28 members are skipped with "UnsupportedSignature" because they use CoreGraphics types (CGImage, CGColor, CGContext) that aren't mapped.

### Example Skipped Members

```
Lottie.FilepathImageProvider.imageForAsset - UnsupportedSignature
Lottie.LottieAnimationLayer.image - UnsupportedSignature
```

### Implementation Approach

1. Add type stubs to TypeDatabase for:
   - `CoreGraphics.CGImage` → `IntPtr` (opaque handle)
   - `CoreGraphics.CGColor` → `IntPtr` (opaque handle)
   - `CoreGraphics.CGContext` → `IntPtr` (opaque handle)

2. Register these in the Swift module database initialization

3. Members using these types will compile but work with raw pointers

### Files to Modify

- `src/Swift.Bindings/src/TypeDatabase/BuiltInTypes.cs` or similar
- Possibly `src/Swift.Bindings/src/Parser/ModuleProcessor.cs`

### Acceptance Criteria

- [ ] CGImage, CGColor, CGContext types registered
- [ ] Skipped members using these types are now emitted
- [ ] Members compile (IntPtr parameters/returns)
- [ ] Unit tests for type registration

---

## Task 3: Lottie Runtime Validation

### Status: 🔲 Pending
### Priority: P1 (Validates binding correctness)
### Effort: Medium (2-3 hours)
### Dependencies: Task 1

### Problem Statement

Lottie bindings compile but haven't been runtime validated. Need to verify that basic Lottie functionality works.

### Implementation Approach

1. Update `LottieTestApp/Program.cs` to exercise basic Lottie APIs:
   - Load a Lottie animation from bundled JSON
   - Create an animation view
   - Query animation properties (duration, frameRate, etc.)

2. Add test markers similar to NukeTestApp:
   - `Console.WriteLine("TEST SUCCESS")` on completion
   - `Console.WriteLine("TEST FAILURE: ...")` on errors

3. Update or create `validate-sim.sh` for Lottie

### Acceptance Criteria

- [ ] LottieTestApp runs on iOS Simulator
- [ ] Basic Lottie animation loads successfully
- [ ] Animation properties can be queried
- [ ] Validation script exits 0 on success

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

# Validate Nuke on simulator
cd BindingTesting/Nuke
./validate-sim.sh 15
```

---

## Notes

- Phase 42 focuses on coverage and validation rather than error elimination
- CoreGraphics stubs are pragmatic - full support would require significant work
- Runtime validation is critical before declaring Lottie "production ready"
- Existential-in-bound-generics is a known architectural limitation (deferred)
