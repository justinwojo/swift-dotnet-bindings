# Class Inheritance Implementation Plan

**Created**: February 2026
**Status**: Spike → Implementation
**Prerequisite for**: ObjC Binding Integration, Self-return handling, polymorphic collections

---

## Problem Statement

The generator emits every Swift class as a flat, independent C# class with no base class relationship. Swift's `class Derived : Base` becomes two unrelated C# classes that both independently implement `ISwiftObject, IDisposable`. This causes:

- **1,184 inherited members missing** across 60 derived classes in 12 of 19 validated libraries
- **Broken fluent builder chains** (SnapKit, Alamofire) where base-class methods are inaccessible
- **No polymorphic assignment** — `DataRequest` cannot be assigned to `Request` variable
- **Incorrect protocol conformance** — inherited conformances not properly resolved
- **Redundant infrastructure** — every derived class gets its own `_payload`, `Dispose()`, finalizer

---

## Impact on Library Scores

### Direct Score Improvements (Inheritance Alone)

| Library | Current | Projected | Delta | Reason |
|---------|---------|-----------|-------|--------|
| **Alamofire** | 2.90 | ~3.40 | +0.50 | 399 inherited members restored across Request hierarchy (Id, State, Cancel, Resume, Progress, etc.). DataRequest/UploadRequest/DownloadRequest become fully functional. |
| **SnapKit** | 3.20 | ~3.65 | +0.45 | Fluent chain unbroken: `ConstraintMakerExtendable` gains `equalTo`/`lessThanOrEqualTo` from `ConstraintMakerRelatable`; `ConstraintMakerEditable` gains `priority`/`labeled` from base chain. |
| **Lottie** | 3.85 | ~4.05 | +0.20 | `AnimatedButton`/`AnimatedSwitch` gain 19 members from `AnimatedControl` (Animation, AnimationSpeed, SetValueProvider, etc.). |
| **StripePayments** | 3.20 | ~3.30 | +0.10 | `STPPaymentIntentAction` gains 18 members from `STPIntentAction`. |
| **RxSwift** | 2.30 | ~2.45 | +0.15 | `HistoricalScheduler` gains 13 members from `VirtualTimeScheduler`. Subjects gain `AsObservable()` from `Observable`. |
| **CryptoSwift** | 3.33 | ~3.40 | +0.07 | `CBCMAC` gains 3 members from `CMAC`. |
| **GRDB** | 2.90 | ~2.95 | +0.05 | `TableAlias` gains 3 members from `TableAliasBase`. |

**Estimated grand average lift**: 3.37 → ~3.50 (+0.13 from inheritance alone)

### Indirect Score Improvements (Enables Other Fixes)

| Future Fix | How Inheritance Helps | Libraries Benefited |
|-----------|----------------------|---------------------|
| **Self-returning methods** | With hierarchy modeled, `Request.resume()` returning `Self` resolves to `DataRequest` on the derived type. Without hierarchy, `Self` is unknowable. | Kingfisher (+0.6), SnapKit (+0.3), KeychainAccess (+0.3) |
| **Protocol conformance quality** | Inherited conformances emit correctly. Fixes empty `""` conformance symbols (currently 10+ across RxSwift/Alamofire). `ProtocolConformanceValidator` can check inherited members. | RxSwift (+0.1), Alamofire (+0.1), all |
| **Polymorphic collections** | `IReadOnlyList<Request>` can contain `DataRequest`, `UploadRequest`, etc. Factory methods returning base types work with derived instances. | Alamofire (+0.1), Stripe (+0.1) |
| **ObjC binding integration** | NSObject hierarchy modeling is prerequisite for proper UIKit type bridging. Without it, `UIView` extensions (SkeletonView, SnapKit `snp`) remain unresolvable. | SkeletonView (+0.5), all UIKit-dependent libraries |
| **Dispose correctness** | Derived classes share base `_payload` — no double-dispose risk, correct finalizer chain. | All 12 affected libraries (correctness, not scores) |

### Combined Impact (Inheritance + Subsequent Sessions)

With inheritance as foundation, the subsequent quick-win and closure sessions become more effective:

| Library | Current | After Inheritance | After All Sessions | Total Delta |
|---------|---------|-------------------|-------------------|-------------|
| Alamofire | 2.90 | 3.40 | ~3.90 | +1.00 |
| SnapKit | 3.20 | 3.65 | ~4.10 | +0.90 |
| Lottie | 3.85 | 4.05 | ~4.30 | +0.45 |
| Kingfisher | 3.10 | 3.10 | ~3.80 | +0.70 |
| GRDB | 2.90 | 2.95 | ~3.70 | +0.80 |
| Stripe | 3.20 | 3.30 | ~3.70 | +0.50 |
| RxSwift | 2.30 | 2.45 | ~3.00 | +0.70 |
| **Grand Avg** | **3.37** | **~3.50** | **~3.95** | **+0.58** |

---

## Current State (What Exists)

### ABI JSON (has the data)
```json
{
  "declKind": "Class",
  "usr": "s:9Alamofire13UploadRequestC",
  "superclassUsr": "s:9Alamofire11DataRequestC",
  "superclassNames": ["Alamofire.DataRequest", "Alamofire.Request"],
  "inheritsConvenienceInitializers": true,
  "hasMissingDesignatedInitializers": true
}
```

### Parser (`Node` record) — missing 4 fields
No `superclassUsr`, `superclassNames`, `inheritsConvenienceInitializers`, `hasMissingDesignatedInitializers`.

### Model (`ClassDecl`) — no inheritance properties
Only `Conformances`, `IsActor`, `IsFinal`. No `SuperclassTypeName`, `SuperclassNames`, `SuperclassDecl`.

### Emitter (`ClassHandler`) — flat emission
Every class independently gets: `_payload`, `_payloadSize`, `Dispose()`, `~Destructor()`, `ISwiftObject` implementation, `GetTypeMetadata()` P/Invoke. Declaration is always `class X : ISwiftObject, IDisposable, [protocols]`.

### Validation (`ProtocolConformanceValidator`) — own members only
Checks `type.Properties`, `type.Methods`, `type.Subscripts` — does NOT traverse superclass members. Inherited conformances may be suppressed incorrectly.

### Type ordering — no topological sort
Types emitted in ABI JSON order (source declaration order). No guarantee base class is processed before derived.

---

## Implementation Sessions

### Session 1: Parse & Model (Small)

**Goal**: Get superclass data from ABI JSON into the model. Pure data plumbing — no emission changes.

**Changes**:

1. **`Node` record** (`SwiftABIParser.cs:44-67`): Add 4 fields
   ```csharp
   public string? superclassUsr { get; set; }
   public string[]? superclassNames { get; set; }
   public bool? inheritsConvenienceInitializers { get; set; }
   public bool? hasMissingDesignatedInitializers { get; set; }
   ```

2. **`ClassDecl` model** (`ClassDecl.cs`): Add inheritance properties
   ```csharp
   public string? SuperclassUsr { get; set; }
   public List<string> SuperclassNames { get; set; } = new();  // full chain
   public string? DirectSuperclassName { get; set; }            // first in chain
   public bool InheritsConvenienceInitializers { get; set; }
   public bool HasMissingDesignatedInitializers { get; set; }
   ```

3. **`CreateClassDecl`** (`SwiftABIParser.cs:678-705`): Populate from Node
   ```csharp
   SuperclassUsr = node.superclassUsr,
   SuperclassNames = node.superclassNames?.ToList() ?? new(),
   DirectSuperclassName = node.superclassNames?.FirstOrDefault(),
   InheritsConvenienceInitializers = node.inheritsConvenienceInitializers ?? false,
   HasMissingDesignatedInitializers = node.hasMissingDesignatedInitializers ?? false,
   ```

4. **Unit tests**: Parse a real ABI JSON snippet (Alamofire UploadRequest) and verify `SuperclassNames` = `["Alamofire.DataRequest", "Alamofire.Request"]`.

**Acceptance gate**: All existing tests pass. New tests verify parsing. No emission changes.

**Commit**: "Session 1: Parse superclass data from ABI JSON into ClassDecl model"

---

### Session 2: Class Hierarchy Resolution (Medium)

**Goal**: Resolve superclass names to actual ClassDecl references. Build hierarchy graph. Topological sort for emission.

**Changes**:

1. **`ClassDecl`**: Add resolved reference
   ```csharp
   public ClassDecl? ResolvedSuperclass { get; set; }  // null for root classes and external bases
   public bool HasResolvedSuperclass => ResolvedSuperclass != null;
   public bool HasExternalSuperclass => DirectSuperclassName != null && ResolvedSuperclass == null;
   ```

2. **`ModuleProcessor`** (or new `ClassHierarchyResolver`): After parsing all types, resolve references
   - Iterate all ClassDecls in the module
   - For each with `DirectSuperclassName`, look up the ClassDecl by SwiftTypeName
   - If found (same module): set `ResolvedSuperclass`
   - If not found (cross-module or ObjC): leave null, mark as external
   - Detect cycles (should be impossible but guard anyway)

3. **`ModuleHandler`** or `HandleBaseDecl`: Topological sort
   - Before emitting types, sort so base classes come before derived
   - This ensures TypeRecord for base class exists when derived class is processed
   - Only reorder ClassDecls with resolved superclasses; leave others in original order

4. **`TypeRecord`**: Add optional superclass reference
   ```csharp
   public SwiftTypeName? SuperclassTypeName { get; set; }
   ```

5. **Cross-module support**: When loading module databases (`--module-database`), include superclass info in serialized TypeRecord so downstream modules can resolve cross-module inheritance.

6. **Unit tests**: Verify resolution on Alamofire 3-level chain. Verify topological sort produces `Request` before `DataRequest` before `UploadRequest`. Verify external bases (NSObject) are correctly marked unresolved.

**Acceptance gate**: All existing tests pass. Hierarchy resolution verified. No emission changes yet.

**Commit**: "Session 2: Class hierarchy resolution and topological sort for emission ordering"

---

### Session 3: Core Emission — Base Class Syntax, Shared Infrastructure & Ownership Fix (Large)

**Goal**: Emit `class Derived : Base`, share infrastructure (payload, Dispose, ISwiftObject) between base and derived classes, and fix the SafeHandle ownership model to prevent use-after-free and memory leaks.

**Changes**:

1. **`ClassHandler.Emit`** — class declaration syntax (line 116):
   - If `classDecl.HasResolvedSuperclass`: emit `class Derived : BaseClass, [protocols not on base]`
   - If no superclass: emit current pattern `class X : ISwiftObject, IDisposable, [protocols]`
   - Derived classes must NOT re-declare `ISwiftObject, IDisposable` (base already declares them)
   - Protocol interface list: filter out interfaces that the base class already implements

2. **`ClassHandler`** — skip payload/Dispose for derived classes:
   - `WriteClassPrivateFields` (line 236): Skip `_payload`, `_payloadSize` for derived classes
   - `WriteClassPayload` (line 248): Skip `Dispose()`, `~Destructor()` for derived classes
   - `WriteISwiftObjectImpl`: Skip for derived classes (inherits from base)
   - `WriteEqualityMethods`: Skip for derived classes (inherits from base)

3. **Base class infrastructure — shared payload approach**:

   In Swift, a derived class instance IS the base class — the memory layout includes the base class's fields. When passed to a P/Invoke expecting `Request`, the same handle works. A single `_payload` (from the base class) works for all derived types — the handle is the same object.

   **Approach**:
   - `_payload` is `protected` on base class, used by all derived classes for P/Invoke
   - `_payloadSize` uses `SwiftObjectHelper<ThisType>.GetTypeMetadata().Size` — each class uses its OWN type metadata for construction, but the handle is the same SafeHandle
   - `Dispose()` on base class only — derived classes inherit it
   - Constructor on derived class: constructs using own metadata, stores in inherited `_payload`
   - Factory/NewFromPayload on derived class: creates with own type metadata

4. **SafeHandle ownership fix (critical correctness bug)**:

   This is the right time to fix the ownership model because we're already restructuring how `_payload` and `Dispose()` work for inheritance. Two related bugs:

   **Bug A — No shared ownership (use-after-free)**:
   ```csharp
   var array1 = new SwiftArray<int>();
   var array2 = array1;  // Value copy of SafeHandle — NO Arc.Retain
   array1.Dispose();     // Calls Destroy
   array2.Append(1);     // USE AFTER FREE
   ```
   With inheritance this gets worse: a `DataRequest` stored as both `DataRequest` and `Request` — the first dispose kills both.

   **Bug B — Finalizer leaks Swift memory permanently**:
   `SwiftSafeHandle<T>.ReleaseHandle()` deliberately skips `ValueWitnessTable.Destroy()` during finalization (unsafe during .NET shutdown ordering). Any `ISwiftObject` that isn't explicitly disposed leaks Swift-side memory forever.

   **Fixes**:
   - **For Bug A**: Classes are reference types in C#, so `var b = a` copies the reference (not the SafeHandle). The issue is when a *new* C# wrapper is created for the same Swift object (e.g., `NewFromPayload` with the same pointer). In that case, the factory must call `Arc.Retain()` to balance the `Destroy()` that will happen on each wrapper's Dispose. Audit all `NewFromPayload` / factory paths to ensure retain/release balance.
   - **For Bug B**: Evaluate whether finalization can safely call `Destroy()` for class types (which use ARC, not VWT Destroy). If not, add loud diagnostics: `[DebuggerDisplay]` on finalized-without-dispose handles, `Debug.WriteLine` warning in finalizer, XML doc comments on all `ISwiftObject` types warning that Dispose is required. The Roslyn analyzer (Session G) provides compile-time enforcement.

5. **Handle forwarding**: When a method returns a `Request` and we need to wrap it in C#, we may need to check if the actual Swift type is `DataRequest` and create the right C# wrapper. This is a runtime concern — defer to Session 6 as an edge case.

6. **Tests**: Emit a simple hierarchy (2 classes), verify:
   - C# compiles
   - Derived class has `: BaseClass` in declaration
   - Derived class does NOT have its own `Dispose()`, `_payload` field
   - Derived class inherits `ISwiftObject`, `IDisposable` from base
   - Multiple C# references to same Swift object: dispose one, other still valid (ownership test)
   - Finalizer-without-dispose: diagnostic emitted (debug build)

**Acceptance gate**: TestFramework `build-and-test.sh` passes. Alamofire/SnapKit compile with inheritance syntax. No use-after-free in basic ownership scenarios.

**Commit**: "Session 3: Emit class inheritance — base class syntax, shared payload, ownership fix"

---

### Session 4: Member Deduplication (Medium)

**Goal**: Derived classes should not re-emit members that are defined on their base class. Identify inherited vs own members. Handle overrides.

**Changes**:

1. **Member identification strategy**:
   - ABI JSON includes ALL members (inherited + own) on each class
   - Need to identify which members are "own" vs "inherited from base"
   - Strategy: When emitting a derived class, collect all member keys from all ancestor ClassDecls. Skip any member on the derived class whose key matches an ancestor's member.
   - Member key: method mangled name prefix, property name, operator signature

2. **`ClassHandler.Emit`** — filter members before emission:
   - Build `HashSet<string>` of base class member keys (all ancestors)
   - For properties: filter `classDecl.Properties` to exclude those with matching name+type in base
   - For methods: filter `classDecl.Methods` to exclude those with matching projected C# signature in base
   - For operators: same pattern

3. **Override detection**:
   - If a derived class has a method with the same name/signature as a base class method, it's an override
   - Emit `override` keyword in C# for virtual (non-final) base methods
   - For Swift, non-final class methods are implicitly virtual (use Tj dispatch thunks)
   - If base method is `final`: derived shouldn't have it (ABI JSON won't include it). If somehow present, use `new` keyword.

4. **Property overrides**: Same pattern — if derived class explicitly declares a property that exists on base, check if it's an override vs computed-property-with-stored-in-base.

5. **Edge case — member hiding**: If a derived class intentionally hides a base member with a different type, emit `new` keyword to suppress CS0108 warning.

6. **Tests**:
   - Verify `DataRequest` does NOT emit `Cancel()`, `Resume()`, `State`, `Id` (inherited from `Request`)
   - Verify `DataRequest` DOES emit its own unique members
   - Verify override methods get `override` keyword
   - Verify member count on `UploadRequest` is ~4 (own) not ~104 (own + inherited)

**Acceptance gate**: Alamofire `DataRequest` has only own members. Full validation suite passes.

**Commit**: "Session 4: Member deduplication — skip inherited members, emit override keyword"

---

### Session 5: Protocol Conformance Inheritance (Medium)

**Goal**: Fix protocol conformance resolution for inherited conformances. Fix empty conformance symbols.

**Changes**:

1. **`ProtocolConformanceValidator.CanFullyImplementProtocol`** (line 71-205):
   - When checking if a type's members satisfy a protocol requirement, also check ancestor class members
   - Traverse `ResolvedSuperclass` chain collecting properties, methods, subscripts
   - A protocol requirement satisfied by a base class member counts as satisfied for the derived class

2. **`ProtocolConformanceHelper.GetImplementedInterfaces`**:
   - If the base class already declares an interface, the derived class should NOT re-declare it
   - Build set of interfaces declared on all ancestors
   - Filter the derived class's interface list to exclude already-declared interfaces
   - This prevents redundant `: IEquatable<T>` on every derived class

3. **`GenerateProtocolConformanceDictionaryEntries`**:
   - Include inherited conformance symbols in the derived class's dictionary
   - If a conformance symbol is `""` (empty), check if the base class has a non-empty symbol for the same protocol — use that instead
   - If no ancestor has the symbol, omit the entry (don't register crashable `""`)

4. **Cross-module inherited conformances**:
   - If a class in module A inherits from a class in module B, and the base class conforms to protocol P, the derived class should inherit that conformance
   - May need module database to carry conformance info

5. **Tests**:
   - Verify RxSwift `BehaviorSubject` no longer has `""` for `IObservableType` conformance
   - Verify `DataRequest` inherits `ICustomStringConvertible` from `Request`
   - Verify derived classes don't re-declare base interfaces

**Acceptance gate**: Zero `""` conformance symbols across all 32 libraries. No CS0535 (missing interface member) errors.

**Commit**: "Session 5: Protocol conformance inheritance — fix empty symbols, inherited interfaces"

---

### Session 6: Validation, Edge Cases & Polish (Medium)

**Goal**: Full validation pass. Handle edge cases. Update tests and golden files.

**Changes**:

1. **Full validation**: `./validate-libraries.sh` — all 32 targets must pass (0 compile errors)

2. **Edge cases to handle**:
   - **Generic base classes**: `HistoricalScheduler : VirtualTimeScheduler<RxTime>` — bound generics in inheritance
   - **Actor inheritance**: Actors with base classes (actors are classes in Swift)
   - **ObjC base classes**: `NSObject`, `UIView`, `UIViewController` — mark as external, no C# base class emitted (until ObjC binding integration). Derived classes remain flat for ObjC bases.
   - **Cross-module inheritance**: Class in StripePaymentSheet inheriting from class in StripeCore — needs module database resolution
   - **Final classes**: `IsFinal` classes cannot be subclassed — no special handling needed, but verify no class tries to inherit from a final class
   - **Classes inheriting from classes that inherit from ObjC**: e.g., `LottieAnimationView : LottieAnimationViewBase : UIView` — model the in-module chain, stop at ObjC boundary

3. **TestFramework**: Add a simple inheritance test case (if `Animal`/`Dog` hierarchy exists in SwiftBindingsTestLib). Verify generated C# compiles and base members accessible.

4. **Golden files**: Regenerate `golden/` files. They will change (inheritance syntax, member dedup).

5. **Unit test updates**: Existing tests may need adjustment for new emission patterns. Update expected output in `ClassHandlerOutputTests`, `ThirdPartyValidationFixTests`, etc.

6. **Run full test suite**: `./run-tests.sh`, `cd TestFramework && ./build-and-test.sh`

**Acceptance gate**: 32/32 validation libraries pass. Unit tests pass (updated as needed). Golden files regenerated. TestFramework coverage maintained.

**Commit**: "Session 6: Class inheritance — validation, edge cases, golden file updates"

---

## Scope Boundaries

### In Scope
- In-module Swift class inheritance (same module)
- Cross-module Swift class inheritance (via module database)
- Base class declaration syntax (`: BaseClass`)
- Shared Dispose/payload infrastructure
- Member deduplication (inherited vs own)
- Method override detection (`override` keyword)
- Protocol conformance inheritance
- Empty conformance symbol fixes
- Topological sort for emission ordering

### Out of Scope (Deferred)
- **ObjC base classes** (NSObject, UIView hierarchy) — requires ObjC binding integration (separate project)
- **Runtime polymorphic unwrapping** — when Swift returns a base type that is actually a derived type, creating the correct C# wrapper. Deferred to runtime improvements.
- **Constructor chaining** — `derived()` calling `base()` through interop. Constructors are handled separately for now.
- **Multiple inheritance** — Swift is single-inheritance for classes. Not applicable.
- **Property/method override ABI differences** — if a derived class overrides a property with different ABI layout. Edge case — defer if encountered.

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Compile errors in validation libraries from incorrect dedup | Medium | High | Conservative approach: if in doubt, emit the member (duplicates are safer than missing members) |
| Dispose/payload sharing causes memory corruption | Low | Critical | Test with runtime tests on iOS simulator. SafeHandle prevents most issues. |
| Topological sort fails for circular references | Very Low | Medium | Cycle detection with clear error message |
| Cross-module base classes not resolved | Medium | Low | Fall back to flat emission (current behavior) when base is unresolved |
| Member override detection produces false positives | Medium | Medium | Use mangled name matching (precise) not just name matching (ambiguous) |
| Existing tests break from emission changes | High | Low | Expected — update test expectations as part of each session |

---

## Validation Checkpoints

After each session, run:
```bash
./run-tests.sh | tail -20                                    # Unit tests
./validate-libraries.sh                                      # Compile gate (32 targets)
cd TestFramework && ./build-and-test.sh                      # Integration + coverage
golden/check-golden-files.sh                                 # Determinism
```

Sessions 1-2 should produce zero emission changes (model/resolution only). Sessions 3-5 will change emission output. Session 6 is the full validation pass.
