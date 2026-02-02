# Codex Task Specifications

This document contains detailed task specifications for the next phase of binding generator improvements. Tasks are ordered by priority and can be worked independently unless noted.

**Date**: February 2026
**Source Analysis**: `binding-gaps-consolidated.md`, `north-star.md`, `codex-review-notes.md`

---

## Status Summary

| Task | Description | Status | Errors Fixed |
|------|-------------|--------|--------------|
| 1 | Paired Operator Synthesis Validation | ✅ **COMPLETED** | CS0216 eliminated |
| 2 | Duplicate Enum Member Deduplication | ✅ **COMPLETED** | CS0102 eliminated |
| 3 | Generic Enum Type Parameter Propagation | ✅ **COMPLETED** | CS0308 eliminated |
| 4 | SwiftUI Constraint Handling | ✅ **COMPLETED** | CS0246, CS0314 eliminated |
| 5 | Binding Completeness Report | ✅ **COMPLETED** | N/A (DX improvement) |
| 6 | UnsupportedType Placeholder | ✅ **COMPLETED** | N/A (DX improvement) |
| 7 | Generic Constraint Relaxation for Existentials | 🔲 Not Started | CS0315 (1 error) |

**Current Lottie Error Count**: 12 errors (CS0311: 10, CS0738: 1, CS0315: 1)
**BlinkID**: 0 errors ✅
**Nuke**: 0 errors ✅
**Unit Tests**: 1004 passed ✅

**Note**: Task 6 added `[UnsupportedSwiftType]` attributes to members with fallback types. 42 attributes emitted in Lottie bindings. Remaining errors are CS0311 (generic constraint violations), CS0738 (protocol interface mismatch), and CS0315 (existential boxing).

---

## Task 1: Paired Operator Synthesis Validation (CS0216)

### Status: ✅ COMPLETED (February 2026)
### Priority: P1 (High)
### Effort: Low (2-4 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` - `EmitOperator()` now returns `bool` success/failure
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Tracks emitted operators in `HashSet<string>`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` - Same pattern
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` - Same pattern
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs` - New tests added
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerOutputTests.cs` - Updated

**Solution**: `ValidateAndEmitPairs()` now takes an `ISet<string> emittedSymbols` parameter and only synthesizes pairs from operators that were actually emitted successfully.

### Problem Statement

The operator handler synthesizes paired operators (e.g., `!=` from `==`, `>` from `<`) even when the primary operator wasn't successfully emitted due to an unsupported signature. This causes CS0216 errors:

```
error CS0216: The operator 'Keyframe<T0>.operator !=(...)' requires a matching operator '==' to also be defined
```

### Root Cause

In `OperatorHandler.cs`, the `ValidateAndEmitPairs()` method synthesizes complementary operators without checking whether the source operator was actually emitted.

### Files to Modify

1. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs`
2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs`

### Implementation Steps

1. **Track emission success per operator**:
   - In `OperatorHandler.EmitOperator()`, return a boolean indicating success/failure
   - Alternatively, collect emitted operator names in a `HashSet<string>` passed through the emission context

2. **Gate pair synthesis on primary operator success**:
   - In `ValidateAndEmitPairs()` (around line 380), check if the primary operator was emitted before synthesizing its pair
   - For example, only synthesize `!=` if `==` was successfully emitted

3. **Update `EqualityMethodsWriter` in `TypeHandlerHelpers.cs`**:
   - The `WriteEqualityMethods()` and related methods may also need to track which operators were emitted
   - Ensure `GetHashCode()` is only emitted if `==` was emitted

### Acceptance Criteria

- [ ] Regenerate Lottie bindings: `cd BindingTesting/Lottie && ./regenerate-bindings.sh`
- [ ] Verify no CS0216 errors: `dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep "CS0216"` returns empty
- [ ] Existing operator tests pass: `./run-tests.sh` (all 935+ tests pass)
- [ ] Nuke bindings still compile: `cd BindingTesting/Nuke && ./build-testapp.sh`

### Test Validation

```bash
# Run unit tests
./run-tests.sh

# Validate Lottie
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS"
# Expected: fewer errors than before (was 21)

# Validate BlinkID still works
cd BindingTesting/BlinkId
./regenerate-bindings.sh
dotnet build BlinkIdTestApp/BlinkIdTestApp.csproj 2>&1 | grep -c "error CS"
# Expected: 0
```

---

## Task 2: Duplicate Enum Member Deduplication (CS0102)

### Status: ✅ COMPLETED (February 2026)
### Priority: P1 (High)
### Effort: Low-Medium (4-6 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Tracks emitted case constructor names, skips duplicate static properties
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` - New test added

**Solution**: `EmitEnumCaseWithAssociatedValues()` now returns `bool` and emitted case names are tracked in `emittedCaseConstructorNames`. Before emitting static properties, the handler checks for name collisions using `NameProvider.ToPascalCase()` and skips duplicates with an info log.

### Problem Statement

Swift enums can have both a case constructor (for cases with associated values) and a static getter property with the same name. When both are emitted to C#, it causes CS0102:

```
error CS0102: The type 'LottiePlaybackMode' already contains a definition for 'Paused'
```

Example in Swift:
```swift
enum LottiePlaybackMode {
    case Paused(frame: Double)  // Case with associated value
    static var Paused: Self { ... }  // Static property (simple case accessor)
}
```

### Root Cause

`EnumHandler.cs` emits both:
1. Case constructors as static methods: `public static LottiePlaybackMode Paused(double frame)`
2. Static case properties: `public static LottiePlaybackMode Paused { get; }`

No deduplication occurs when names collide.

### Files to Modify

1. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`

### Implementation Steps

1. **Collect case constructor names first**:
   - Before emitting static properties, build a `HashSet<string>` of case constructor names that will be emitted

2. **Skip static property when constructor exists**:
   - In the static property emission loop, check if the property name matches an existing case constructor
   - If so, skip emitting the static property (the constructor provides the same functionality)

3. **Alternative approach - rename the property**:
   - Use `NameProvider.GetPropertyName()` with collision detection
   - Rename the property to `PausedValue` or similar when collision detected
   - This preserves both accessors but may be confusing to users

**Recommended**: Skip the static property when a case constructor with the same name exists. The constructor is more functional (accepts associated values).

### Acceptance Criteria

- [ ] Regenerate Lottie bindings
- [ ] Verify no CS0102 errors for enum members
- [ ] Enum case constructors still work (associated value cases)
- [ ] Static case properties still work (simple cases without constructors)
- [ ] All existing enum tests pass

### Test Validation

```bash
./run-tests.sh

cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep "CS0102"
# Expected: no CS0102 errors related to enum members
```

---

## Task 3: Generic Enum Type Parameter Propagation (CS0308)

### Status: ✅ COMPLETED (February 2026)
### Priority: P2 (Medium)
### Effort: Medium (1-2 days)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Major updates for generic enum emission:
  - Added `typeNameWithGenerics` and `whereClause` generation using `GenericTypeEmitter`
  - Added `PInvokeHelperContext` for generic enums to avoid CS7042
  - Updated all case constructors, `FromRawValue`, and P/Invoke calls to use helper class pattern
  - Added `TryGetGenericTypeParameterName()` to map `τ_0_0` → `T0`
  - Updated `GetCSharpTypeNameForEnumCase()`, `GetPInvokeArgument()`, `GetPInvokeType()` to handle generic params
  - Updated `EnumISwiftObjectMethodWriter` for generic type names and helper-based metadata
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` - New test `Emit_GenericEnum_EmitsGenericTypeAndPInvokeHelper`
- `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftABIParserTests.cs` - New test `CreateEnumDecl_WithGenericParameters_SetsGenericParameters`

**Solution**: Generic enums now emit with proper type parameters and constraints. For example:
```csharp
// Before: public unsafe class ValueProviderStorage : ISwiftObject
// After:  public unsafe class ValueProviderStorage<T0> : ISwiftObject where T0 : ISwiftObject, ISwiftAnyInterpolatable
```

P/Invoke declarations are emitted to a helper class (`ValueProviderStorage_PInvoke`) to avoid CS7042 "cannot use generic type parameters in P/Invoke".

**Impact**: CS0308 errors eliminated. New CS0311 errors surfaced at call sites where types don't satisfy the now-correct constraints (e.g., `LottieVector3D` doesn't implement `ISwiftAnyInterpolatable`). This is expected progress - the constraints are now correct.

### Problem Statement

Some Swift enums are generic (e.g., `ValueProviderStorage<T>`) but the binding generator emits them as non-generic types. When code references them with type arguments, it causes CS0308:

```
error CS0308: The non-generic type 'ValueProviderStorage' cannot be used with type arguments
```

### Root Cause

The ABI JSON parser (`SwiftABIParser.cs`) doesn't extract generic parameters from enum type declarations. The generic parameters are present in member signatures but not propagated to `EnumDecl.GenericParameters`.

### Files to Modify

1. `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Extract generic params from enum decls
2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Emit generic type syntax
3. `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs` - May need updates for enum generics

### Implementation Steps

1. **Investigate ABI JSON structure**:
   - Find a generic enum in the Lottie ABI JSON
   - Identify where generic parameters are declared (likely in `genericSig` or similar field)
   - Compare with how generic structs/classes extract their parameters

2. **Update parser to extract enum generic parameters**:
   - In `SwiftABIParser.ParseEnumDecl()` or related method
   - Extract generic parameters similar to how `ParseStructDecl()` does it
   - Populate `EnumDecl.GenericParameters`

3. **Update EnumHandler for generic emission**:
   - Check if `EnumDecl.GenericParameters` is non-empty
   - If so, emit `EnumName<T0, T1, ...>` syntax
   - Add `where` clause constraints if present
   - Use `GenericTypeEmitter.GetTypeNameWithGenerics()` pattern

4. **Handle P/Invoke for generic enums**:
   - Generic enums may need the same `_PInvoke` helper class pattern used for generic structs/classes (Phase 31)
   - Check if `PInvokeHelperEmitter` needs to handle enums

### Acceptance Criteria

- [ ] `ValueProviderStorage` emits as `ValueProviderStorage<T0>` in Lottie bindings
- [ ] Generic enum members reference the correct generic type name
- [ ] No CS0308 errors for generic enum usage
- [ ] Existing enum tests pass
- [ ] BlinkID and Nuke bindings unaffected

### Investigation Commands

```bash
# Find generic enums in Lottie ABI
cd BindingTesting/Lottie
grep -r "ValueProviderStorage" output-ios/

# Check current enum emission
grep -A 20 "class ValueProviderStorage" output-ios/Swift.Lottie.cs

# Compare with generic struct handling in parser
grep -A 30 "ParseStructDecl" src/Swift.Bindings/src/Parser/SwiftABIParser.cs
```

---

## Task 4: SwiftUI Constraint Handling (CS0246)

### Status: ✅ COMPLETED (February 2026)
### Priority: P2 (Medium)
### Effort: Medium (1-2 days)
### Dependencies: None

### Completion Notes

**Implemented by**: Justin Wojciechowski
**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs` - Added `TryGetUnsupportedConstraint()` and `UnsupportedConstraintModules` (SwiftUI, Combine)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Checks for unsupported constraints, skips with warning
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` - Same pattern
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` - Same pattern
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Same pattern
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericTypeEmitterTests.cs` - New tests for constraint detection
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlersOutputTests.cs` - New tests for handler skip behavior

**Solution**: Centralized detection of unsupported protocol constraints in `GenericTypeEmitter.TryGetUnsupportedConstraint()`. Type handlers check at the start of `Emit()` and skip with an informative warning log when a type has generic constraints referencing SwiftUI or Combine protocols.

**Impact**: All CS0246/CS0314 errors for `ISwiftView` eliminated. Lottie error count reduced from 38 to 24.

### Problem Statement

Types with generic constraints referencing SwiftUI protocols (e.g., `View`) fail to compile because the corresponding C# interface doesn't exist:

```
error CS0246: The type or namespace name 'ISwiftView' could not be found
```

Example:
```csharp
public class LottieView<T0> : ISwiftObject where T0 : ISwiftObject, ISwiftView  // ISwiftView doesn't exist!
```

### Root Cause

SwiftUI protocols like `View` are:
1. Not included in the binding generation (SwiftUI is out of scope per north-star.md)
2. Protocol-with-associated-types (PATs) that can't be fully represented anyway
3. Referenced in generic constraints but no C# interface is emitted

### Implementation Options

**Option A: Skip types with unsupported constraints (Recommended for short-term)**
- Detect when a generic constraint references a protocol from an unsupported module (SwiftUI)
- Skip emitting the entire type with a warning
- Keeps compilation green

**Option B: Emit stub interfaces**
- Generate empty marker interfaces for SwiftUI protocols: `public interface ISwiftView { }`
- Types compile but can't actually be used
- May confuse users

**Option C: Remove unsupported constraints**
- Strip `ISwiftView` from the where clause, keeping only `ISwiftObject`
- Type compiles but constraint is weaker than Swift's

### Files to Modify

1. `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs` - `GetWhereClause()`
2. `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Type pruning logic
3. Possibly `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` - Protocol resolution

### Implementation Steps (Option A - Recommended)

1. **Create list of unsupported protocol modules**:
   ```csharp
   private static readonly HashSet<string> UnsupportedProtocolModules = new()
   {
       "SwiftUI",
       "Combine",  // Also unsupported
   };
   ```

2. **Detect unsupported constraints in `GetWhereClause()`**:
   - When building where clause, check each protocol constraint
   - If protocol is from an unsupported module, flag the type

3. **Skip type emission in ModuleProcessor**:
   - Add a pre-processing pass that marks types with unsupported constraints
   - Skip them during emission with a warning message

4. **Emit diagnostic**:
   - Log: `"Skipping type 'LottieView<T>' - generic constraint references unsupported SwiftUI.View protocol"`

### Acceptance Criteria

- [ ] Types with SwiftUI constraints are skipped with clear warning
- [ ] No CS0246 errors for `ISwiftView` or similar
- [ ] Warning message clearly identifies which types were skipped and why
- [ ] Non-SwiftUI types in Lottie still emit correctly
- [ ] BlinkID and Nuke unaffected

### Test Validation

```bash
cd BindingTesting/Lottie
./regenerate-bindings.sh 2>&1 | grep -i "swiftui\|skipping"
# Expected: warnings about skipped SwiftUI types

dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep "CS0246"
# Expected: no ISwiftView errors
```

---

## Task 5: Binding Completeness Report

### Status: ✅ COMPLETED (February 2026)
### Priority: P2 (Medium)
### Effort: Medium (1-2 days)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Files Created**:
- `src/Swift.Bindings/src/Reporting/BindingReport.cs` - Report data model with SkipReason enum
- `src/Swift.Bindings/src/Reporting/ReportCollector.cs` - Thread-safe collector with AsyncLocal scoping
- `src/Swift.Bindings/src/Reporting/ReportEmitter.cs` - JSON + console output
- `src/Swift.Bindings/tests/UnitTests/ReportingTests/ReportCollectorTests.cs` - Unit tests

**Files Modified**:
- `src/Swift.Bindings/src/Program.cs` - Integrated report lifecycle
- All type handlers (Class, Enum, FrozenStruct, NonFrozenStruct, Protocol) - Type emit/skip tracking
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Method emit/skip + synthesized tracking
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Property emit/skip tracking
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` - Operator emit/skip + synthesized tracking
- `src/Swift.Bindings/src/Marshaler/IHandler.cs` - Added IsSynthesized flag

**Solution**: Comprehensive binding report with:
- JSON report written to `binding-report.json` in output directory
- Console summary with coverage percentages
- Synthesized members (accessor methods, paired operators) tracked separately
- Skip reasons: UnsupportedSignature, UnsupportedType, AnyTypeFallback, UnsupportedClosure, SwiftUIConstraint, etc.

**Sample Output (Lottie)**:
```
Types: 79 emitted, 1 skipped (84.9% coverage)
Members: 385 emitted, 43 skipped, 273 synthesized (63.2% coverage)
```

### Problem Statement

Users have no visibility into what was skipped during binding generation and why. The only way to discover gaps is to compile and hit errors, or manually diff the generated output against the Swift API.

### Goal

Emit a structured report (JSON + console summary) at the end of binding generation showing:
- Total types/members processed
- Types/members skipped with reason codes
- Coverage percentage
- Actionable information for users

### Files to Create/Modify

1. **NEW**: `src/Swift.Bindings/src/Reporting/BindingReport.cs` - Report data model
2. **NEW**: `src/Swift.Bindings/src/Reporting/ReportEmitter.cs` - JSON + console output
3. `src/Swift.Bindings/src/Program.cs` - Integrate report generation
4. Various handlers - Add skip tracking

### Data Model

```csharp
public class BindingReport
{
    public string ModuleName { get; set; }
    public DateTime GeneratedAt { get; set; }

    public int TotalTypes { get; set; }
    public int EmittedTypes { get; set; }
    public int SkippedTypes { get; set; }

    public int TotalMembers { get; set; }
    public int EmittedMembers { get; set; }
    public int SkippedMembers { get; set; }

    public List<SkippedItem> SkippedItems { get; set; }
}

public class SkippedItem
{
    public string Kind { get; set; }  // "Type", "Method", "Property", "Operator"
    public string Name { get; set; }
    public string ContainingType { get; set; }
    public SkipReason Reason { get; set; }
    public string Details { get; set; }
}

public enum SkipReason
{
    UnsupportedType,
    AnyTypeFallback,
    AsyncProperty,
    SwiftUIConstraint,
    GenericProtocolProxy,
    CombineFramework,
    ActorType,
    UnsupportedSignature,
    // ... others as discovered
}
```

### Implementation Steps

1. **Create report infrastructure**:
   - Create `Reporting` folder under `src/Swift.Bindings/src/`
   - Implement `BindingReport` and `SkippedItem` classes
   - Implement `ReportCollector` singleton to accumulate data during generation

2. **Instrument skip points**:
   - Find all places where types/members are skipped (search for "skip", "unsupported", "AnyType")
   - Add `ReportCollector.RecordSkip(kind, name, reason, details)` calls

3. **Emit report at end**:
   - In `Program.cs`, after generation completes, call `ReportEmitter.Emit()`
   - Write JSON to `{output}/binding-report.json`
   - Write console summary

4. **Console summary format**:
   ```
   === Binding Generation Report ===
   Module: Lottie

   Types:    45 emitted, 3 skipped (93.8% coverage)
   Methods:  234 emitted, 12 skipped (95.1% coverage)
   Properties: 89 emitted, 5 skipped (94.7% coverage)

   Skipped items by reason:
     SwiftUIConstraint: 3 types
     AnyTypeFallback: 8 methods
     CombineFramework: 2 methods
     AsyncProperty: 4 properties

   Full details in: output-ios/binding-report.json
   ```

### Acceptance Criteria

- [ ] JSON report written to output directory
- [ ] Console summary printed at end of generation
- [ ] All current skip points instrumented with reason codes
- [ ] Report accurately reflects what was/wasn't emitted
- [ ] No performance regression (report collection is lightweight)

### Test Validation

```bash
# Generate bindings and check for report
cd BindingTesting/Lottie
./regenerate-bindings.sh

# Verify JSON report exists
cat output-ios/binding-report.json | jq '.SkippedItems | length'

# Verify console output includes summary
./regenerate-bindings.sh 2>&1 | grep "Binding Generation Report"
```

---

## Task 6: UnsupportedType Placeholder

### Status: ✅ COMPLETED (February 2026)
### Priority: P3 (Medium)
### Effort: Medium (1 day)
### Dependencies: None (but complements Task 5)

### Completion Notes

**Implemented by**: Codex
**Files Created**:
- `src/Swift.Runtime/src/Swift/UnsupportedSwiftTypeAttribute.cs` - Runtime attribute with `Reason` and `SwiftType` properties
- `src/Swift.Bindings/src/Emitter/StringEmitter/UnsupportedSwiftTypeSupport.cs` - Recursive fallback detection helper
- `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/TypeDatabaseExtensionsTests.cs` - Fallback info tests

**Files Modified**:
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` - Added `AnyTypeFallbackInfo` record and `TryGetAnyTypeFallbackInfo()` methods
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Emits attribute on fallback methods
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Emits attribute on fallback properties
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodHandlerOutputTests.cs` - Updated tests
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerTests.cs` - Updated tests

**Solution**:
- `UnsupportedSwiftTypeSupport.TryFindFallbackInfo()` recursively traverses type specs to detect:
  - Missing types from type database
  - Existential type fallbacks
  - Unsupported closure fallbacks
  - Nested generic parameters and tuple elements
- Attribute emitted before method/property signatures when fallback detected

**Sample Output (Lottie)**:
```csharp
[global::Swift.UnsupportedSwiftType("Existential type fallback", "any Lottie.AnimationImageProvider")]
public AnyType imageProvider { get { ... } }
```

**Impact**: 42 `[UnsupportedSwiftType]` attributes emitted in Lottie bindings, providing clear visibility into which members have degraded types.

### Problem Statement

When the generator can't handle a type (e.g., complex generic, unsupported protocol), it falls back to `AnyType` or `object`. This compiles but silently degrades:

```csharp
// User sees this - looks fine
public object SomeProperty { get; }

// But at runtime, they can't actually use it correctly
```

### Goal

Replace silent fallbacks with explicit `UnsupportedType` markers that:
1. Make gaps visible at compile time (or with warnings)
2. Provide clear error messages about what's unsupported
3. Allow users to make informed decisions

### Implementation Options

**Option A: Compile-time error type**
```csharp
// Emitted code
public UnsupportedType<"Swift existential 'any Hashable' not supported"> SomeProperty { get; }

// UnsupportedType<T> is a struct that causes compile errors when used
```

**Option B: Runtime-checked type**
```csharp
// Emitted code
public UnsupportedSwiftType SomeProperty { get; }

// UnsupportedSwiftType throws on any operation
public struct UnsupportedSwiftType
{
    private readonly string _reason;
    public UnsupportedSwiftType(string reason) => _reason = reason;
    // All operations throw NotSupportedException with _reason
}
```

**Option C: Annotated object (Recommended)**
```csharp
// Emitted code
[UnsupportedSwiftType("Existential 'any Hashable' cannot be marshalled")]
public object SomeProperty { get; }

// Attribute is visible in IDE and can be detected by analyzers
```

### Files to Modify

1. **NEW**: `src/Swift.Runtime/src/Swift/UnsupportedSwiftTypeAttribute.cs`
2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
3. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
4. `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` - Where `AnyType` is returned

### Implementation Steps

1. **Create attribute in runtime**:
   ```csharp
   [AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Parameter)]
   public class UnsupportedSwiftTypeAttribute : Attribute
   {
       public string Reason { get; }
       public string SwiftType { get; }

       public UnsupportedSwiftTypeAttribute(string reason, string swiftType = null)
       {
           Reason = reason;
           SwiftType = swiftType;
       }
   }
   ```

2. **Track when AnyType is used as fallback**:
   - In `TypeDatabaseExtensions.GetTypeRecordOrAnyType()`, capture the original Swift type and reason
   - Pass this context through to the emitter

3. **Emit attribute on fallback members**:
   - When emitting a property/method that uses `AnyType` fallback
   - Add `[UnsupportedSwiftType("reason", "SwiftTypeName")]` attribute

4. **Optional: Emit compiler warning**:
   - Use `#warning` directive in generated code for high-visibility

### Acceptance Criteria

- [ ] `UnsupportedSwiftTypeAttribute` added to Swift.Runtime
- [ ] Fallback properties/methods annotated with attribute
- [ ] Attribute includes reason and original Swift type
- [ ] Existing tests pass (fallback behavior unchanged, just annotated)
- [ ] IntelliSense shows the attribute on affected members

### Test Validation

```bash
# Check attribute is emitted
cd BindingTesting/Lottie
./regenerate-bindings.sh
grep "UnsupportedSwiftType" output-ios/Swift.Lottie.cs

# Verify runtime assembly has attribute
dotnet build src/Swift.Runtime/src/Swift.Runtime.csproj
```

---

## Task 7: Generic Constraint Relaxation for Existentials (CS0314/CS0315)

### Priority: P3 (Lower - Architectural)
### Effort: High (2-3 days)
### Dependencies: Requires design discussion

### Problem Statement

Generic types constrained to `ISwiftObject` fail when instantiated with existential containers:

```csharp
// Generated constraint
public class Keyframe<T0> where T0 : ISwiftObject { ... }

// Usage in generated code
Keyframe<ExistentialContainer0> x = ...;  // CS0315: ExistentialContainer0 doesn't implement ISwiftObject
```

### Root Cause

1. `GenericTypeEmitter` adds `ISwiftObject` constraint to all generic parameters
2. Existential containers (`ExistentialContainer0`, `ExistentialContainer1`, etc.) don't implement `ISwiftObject`
3. They're structs representing boxed protocol values, not Swift objects themselves

### Complexity

This is architecturally complex because:
- `ISwiftObject` constraint is used for metadata lookup (`SwiftObjectHelper<T>.GetTypeMetadata()`)
- Existentials have different metadata paths (`GetExistentialTypeMetadata`)
- Relaxing constraints may break other code paths

### Implementation Options

**Option A: Add ISwiftObject to ExistentialContainer (Risky)**
- Make `ExistentialContainer{N}` implement `ISwiftObject`
- Requires implementing `Handle`, `Dispose`, etc.
- May not be semantically correct

**Option B: Separate constraint for existential-accepting generics**
- Introduce `ISwiftType` base interface (broader than `ISwiftObject`)
- Use `ISwiftType` for generics that accept existentials
- Existential containers implement `ISwiftType`

**Option C: Route to type-erased companions (Recommended)**
- When a generic argument resolves to existential, use a type-erased version
- E.g., `Keyframe<ExistentialContainer0>` → `AnyKeyframe`
- Requires emitting type-erased companions

**Option D: Skip emission when existential would be used**
- Detect at generation time when a bound generic would use existential
- Skip that specific usage with warning
- Simpler but reduces API coverage

### Files to Modify

1. `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs`
2. `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs`
3. `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`
4. Possibly `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs`

### Recommended Approach

Start with **Option D** (skip with warning) as a short-term fix, then evaluate Option B or C for a proper solution.

### Investigation Steps

1. **Catalog affected usages**:
   ```bash
   cd BindingTesting/Lottie
   grep -n "ExistentialContainer" output-ios/Swift.Lottie.cs | head -30
   ```

2. **Understand metadata flow**:
   - Trace how `SwiftObjectHelper<T>.GetTypeMetadata()` is used
   - Identify if existentials can provide compatible metadata

3. **Prototype Option D**:
   - In `BoundGenericsHandler`, detect when type argument is existential
   - Skip with warning instead of emitting broken code

### Acceptance Criteria

- [ ] No CS0314/CS0315 errors in Lottie bindings
- [ ] Clear warning when existential usage is skipped
- [ ] Design document for long-term solution (if going beyond Option D)
- [ ] Existing generic type tests pass

---

## Execution Order Recommendation

1. **Task 1** (Operator pairs) - Quick win, independent
2. **Task 2** (Enum dedup) - Quick win, independent
3. **Task 5** (Binding report) - Foundation for visibility
4. **Task 4** (SwiftUI constraints) - Requires Task 5 for proper reporting
5. **Task 3** (Generic enums) - Medium complexity
6. **Task 6** (UnsupportedType) - Enhances Task 5
7. **Task 7** (Existential constraints) - Highest complexity, needs design

Tasks 1-2 and 5-6 can be worked in parallel by different agents.

---

## Validation Checklist (Run After Each Task)

```bash
# 1. All unit tests pass
./run-tests.sh

# 2. Nuke bindings compile and validate
cd BindingTesting/Nuke
./build-all.sh && ./validate-sim.sh 15

# 3. BlinkID bindings compile
cd BindingTesting/BlinkId
./regenerate-bindings.sh
dotnet build BlinkIdTestApp/BlinkIdTestApp.csproj

# 4. Lottie error count decreasing
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS"
```
