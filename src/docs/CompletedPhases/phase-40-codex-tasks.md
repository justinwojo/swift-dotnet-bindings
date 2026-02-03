# Codex Task Specifications - Phase 40+

Task specifications for the next phase of binding generator improvements. Tasks are ordered by priority and designed to be worked independently unless noted.

**Date**: February 2026
**Starting Point**: Phase 39 complete, 1024 unit tests passing
**Libraries**: Nuke (0 errors), BlinkID (0 errors), Lottie (7 generator errors)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Skip Members with Unsatisfied Constraints | ✅ **COMPLETED** | P0 |
| 2 | Protocol Proxy Return Type Alignment | ✅ **COMPLETED** | P1 |
| 3 | Protocol Conformance Emission | ✅ **COMPLETED** (partial) | P2 |
| 4 | Namespace Mapping Configuration | ✅ **COMPLETED** | P2 |

**Target**: Lottie 0 errors
**Current**: CS0738 ✅, CS0311 ✅, conformance infrastructure ✅, namespace config ✅ - 7 pre-existing generator bugs (CS0029, CS0305, CS1061)

---

## Task 1: Skip Members with Unsatisfied Constraints (CS0311)

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Critical - blocks Lottie clean compile)
### Effort: Medium (4-6 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Unit Tests**: 1018 passed (up from 1009)

**Files Modified**:
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` - Added `TryGetFirstUnsatisfiedConstraint()` with cross-module support
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Constraint checks for method arguments
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Preflight checks for accessor return types and parameters, fixed accessor context mismatch
- `src/Swift.Bindings/src/Reporting/BindingReport.cs` - Added `UnsatisfiedGenericConstraint` skip reason

**Key fixes**:
1. Constraint checking for local module types (LottieVector3D, etc.)
2. Fail-closed for external types (SwiftArray<double>, etc.)
3. Property preflight now sets `IsAccessor = true` and injects `PInvokeHelperContext` to match emit behavior
4. Return type constraint checking for getters (not just arguments)

**Results**: CS0311 errors eliminated in all test libraries

### Problem Statement

Members are emitted that use generic types with constraints that cannot be satisfied by the argument types. For example:

```csharp
// ValueProviderStorage<T0> where T0 : ISwiftAnyInterpolatable
// But LottieVector3D doesn't implement ISwiftAnyInterpolatable
public ValueProviderStorage<LottieVector3D> storage { get; }  // CS0311!
```

The generator emits members with bound generic types without verifying that the type arguments satisfy the generic constraints.

### Root Cause

When emitting a member that returns `ValueProviderStorage<LottieVector3D>`, the generator:
1. Resolves `ValueProviderStorage<T>` and sees it has constraint `T : AnyInterpolatable`
2. Resolves `LottieVector3D` as the type argument
3. Emits the member without checking if `LottieVector3D` conforms to `AnyInterpolatable`

### Files to Investigate

1. `src/Swift.Bindings/src/Marshaler/Conductor.cs` - Where member marshalling decisions are made
2. `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` - Type and conformance lookup
3. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Member emission
4. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Property emission

### Implementation Approach

**Option A: Skip at marshalling stage** (Recommended)
1. In `Conductor.cs`, when processing a member with bound generic return/param types:
   - Extract the generic type definition and its constraints
   - For each constraint, check if the type argument conforms to the protocol
   - If not, mark member as `UnsupportedSignature` with reason `UnsatisfiedConstraint`

**Option B: Skip at emission stage**
1. In type handlers, before emitting a member:
   - Check bound generic types for constraint satisfaction
   - Skip emission if constraints aren't met

### Implementation Steps

1. **Add constraint checking utility**:
   ```csharp
   // In TypeDatabase or new ConstraintChecker.cs
   public bool SatisfiesConstraint(TypeDecl typeArg, TypeDecl constraint)
   {
       // Check if typeArg conforms to constraint protocol
       // Use conformance information from ABI
   }
   ```

2. **Integrate into bound generic handling**:
   - When a `BoundGenericType` is encountered, validate each type argument against its constraint
   - If validation fails, skip the member with appropriate reason

3. **Add skip reason**:
   - Add `UnsatisfiedGenericConstraint` to the skip reasons enum
   - Include in binding report with details of which constraint failed

4. **Add `[UnsupportedSwiftType]` attribute**:
   - Mark skipped members with the reason for discoverability

### Acceptance Criteria

- [ ] Regenerate Lottie bindings: `cd BindingTesting/Lottie && ./regenerate-bindings.sh`
- [ ] CS0311 errors eliminated: `dotnet build ... 2>&1 | grep "CS0311"` returns empty
- [ ] Skipped members appear in `binding-report.json` with `UnsatisfiedGenericConstraint` reason
- [ ] All unit tests pass: `./run-tests.sh`
- [ ] Nuke and BlinkID still compile clean

### Test Validation

```bash
./run-tests.sh
cd BindingTesting/Lottie && ./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS0311"
# Expected: 0
```

---

## Task 2: Protocol Proxy Return Type Alignment (CS0738)

### Status: ✅ COMPLETED (February 2026)
### Priority: P1 (High)
### Effort: Low (2-3 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Unit Tests**: 1 new test added

**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Added `GetInterfaceCompatiblePropertyTypeName()` method
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` - Added regression test

**Key fixes**:
1. Property type resolution in proxy now uses `TypeDatabase.GetTypeRecordOrAnyType()` to match interface emission
2. Removed hardcoded `ExistentialContainer1` fallback for unsupported existentials
3. Uses `BoundGenericsHandler` for bound generic properties (consistent with interface)

**Results**: CS0738 error eliminated - proxy `valueType` now returns `Swift.AnyType` matching interface

### Problem Statement

Protocol proxy classes have properties with return types that don't match the interface:

```csharp
interface ISwiftAnyValueProvider {
    AnyType valueType { get; }  // Interface expects AnyType
}

class AnyValueProviderProxy : ISwiftAnyValueProvider {
    public SomeOtherType valueType { get; }  // CS0738!
}
```

### Root Cause

The proxy emitter and interface emitter use different logic to determine property return types. The interface uses the Swift protocol's declared type, but the proxy may resolve it differently.

### Files Modified

1. `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Proxy emission
2. `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` - Unit tests

### Implementation Steps

1. **Identify the mismatch**:
   - Check how `valueType` is emitted in `ISwiftAnyValueProvider`
   - Check how it's emitted in `AnyValueProviderProxy`
   - Determine which is correct (likely the interface)

2. **Align proxy to interface**:
   - Ensure proxy property types exactly match interface declarations
   - May need to share type resolution logic between emitters

3. **Add test case**:
   - Unit test that verifies proxy properties match interface types

### Acceptance Criteria

- [x] CS0738 error eliminated in Lottie
- [x] Proxy class compiles and implements interface correctly
- [x] Unit tests pass

### Test Validation

```bash
cd BindingTesting/Lottie && ./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS0738"
# Expected: 0
```

---

## Task 3: Protocol Conformance Emission

### Status: ✅ COMPLETED (partial - February 2026)
### Priority: P2 (Medium - enhances API but not blocking)
### Effort: High (8-12 hours)
### Dependencies: Task 1 provides interim fix; this is the proper solution

### Completion Notes

**Implemented by**: Codex (initial), Claude (fixes)
**Unit Tests**: 2 new tests added

**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` - Added `ProtocolConformanceHelper.GetImplementedInterfaces()` and refactored `ShouldEmitConformance()`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Uses shared conformance helper
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` - Uses shared conformance helper
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` - Uses shared conformance helper
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Uses shared conformance helper
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlersOutputTests.cs` - Added regression tests

**Key implementation**:
1. Shared `ProtocolConformanceHelper` for all type handlers
2. `GetImplementedInterfaces()` builds interface list from conformances
3. `ShouldEmitConformance()` filters by module, protocol kind, and PATs
4. Currently only emits `IEquatable<T>` interface (has C# implementation via `SwiftEquatable.Equals`)
5. Other protocols tracked in `GetProtocolConformanceDescriptor` dictionary but NOT in interface list (pending protocol method emission)
6. Enums excluded from `IEquatable` (emitted as C# classes without Equals)

**Limitations** (future work):
- Non-Equatable protocol interfaces not emitted until protocol method emission on conforming types is implemented
- Full conformance (e.g., `LottieVector3D : ISwiftAnyInterpolatable`) requires emitting protocol methods on types

### Problem Statement

When a Swift type conforms to a protocol, the C# projection should implement the corresponding interface. Currently:

```swift
// Swift
struct LottieVector3D: AnyInterpolatable { ... }
```

```csharp
// Generated (current)
public struct LottieVector3D : ISwiftObject { ... }

// Should be (future)
public struct LottieVector3D : ISwiftObject, ISwiftAnyInterpolatable { ... }
```

### Acceptance Criteria

- [ ] `LottieVector3D` implements `ISwiftAnyInterpolatable` *(requires protocol method emission)*
- [ ] Generic constraints are now satisfiable *(requires protocol method emission)*
- [x] All tests pass
- [x] Shared conformance infrastructure in place
- [x] `IEquatable<T>` emitted for classes/structs with Equatable conformance

### Notes

This task established the conformance infrastructure. Full interface emission requires a follow-up task to emit protocol methods on conforming types.

---

## Task 4: Namespace Mapping Configuration

### Status: ✅ COMPLETED (February 2026)
### Priority: P2 (Medium - DX improvement before stable release)
### Effort: Medium (4-6 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Unit Tests**: 1024 passed

**Files Modified**:
- `src/Swift.Bindings/src/Program.cs` - Added `--namespace-pattern` and `--config` support, config loading, framework name inference
- `src/Swift.Bindings/src/Configuration/NamespacePatternResolver.cs` - Added pattern substitution (`{Module}`, `{Framework}`) with default fallback
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Namespace resolution for type registration
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` - Namespace-aware output filenames
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` - Namespace-aware C# `namespace` emission
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` - Wires namespace resolver into module handler factory
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleHandlerTests.cs` - Added namespace pattern regression tests
- `README.md` - Documented default namespace scheme and override options

**Key fixes**:
1. Default namespace remains `Swift.{Module}` (backward compatible)
2. CLI override via `--namespace-pattern`
3. Config override via `.swiftbindings.json` (or `--config` path)
4. Namespace mapping now consistent across emitted files, namespace declarations, and type database records

### Problem Statement

The current namespace mapping uses a hardcoded `Swift.{Module}` pattern:

```csharp
namespace Swift.Lottie { ... }
namespace Swift.StoreKit { ... }
```

This pattern needs to be:
1. Documented as the default
2. Configurable via CLI or config file
3. Consistent before shipping stable packages (breaking change risk)

### Implementation Steps

1. **Add configuration option**:
   - CLI flag: `--namespace-pattern "{Module}Bindings"` or similar
   - Config file option in a `.swiftbindings.json` or similar

2. **Implement pattern substitution**:
   - `{Module}` → Swift module name
   - `{Framework}` → Framework name if different
   - Default: `Swift.{Module}`

3. **Document the scheme**:
   - Add to README or user docs
   - Explain implications of changing after release

### Files to Modify

1. `src/Swift.Bindings/src/Program.cs` - CLI argument parsing
2. `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Namespace registration
3. `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` - Namespace emission

### Acceptance Criteria

- [x] Default behavior unchanged (`Swift.{Module}`)
- [x] CLI flag allows override
- [x] Config file option available
- [x] Unit tests for pattern substitution

### Validation

**Validated by**: Claude
**Date**: February 2026

- All unit tests pass (1024)
- All integration tests pass (678 passed, 13 skipped)
- All runtime tests pass (108 passed, 1 skipped)
- Lottie bindings regenerate successfully with new namespace resolution
- No regressions introduced by this change

---

## Future Tasks (Not Yet Specified)

These are identified gaps but not yet detailed:

| Feature | Notes |
|---------|-------|
| Property setters | Verify current state; may be partially done |
| Async properties | Properties with async getters |
| Actors | Swift actor type support |
| Unbound generic types | Generic type definitions (not just bound) |
| TypeGraph layer | Architectural improvement from emitter redesign |

---

## Testing Commands Reference

```bash
# Run all unit tests
./run-tests.sh

# Regenerate and test Lottie
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj

# Regenerate and test Nuke (with runtime validation)
cd BindingTesting/Nuke
./build-all.sh && ./validate-sim.sh 15

# Count errors by type
dotnet build ... 2>&1 | grep "error CS" | sed 's/.*error \(CS[0-9]*\).*/\1/' | sort | uniq -c
```
