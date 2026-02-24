# Class Inheritance Implementation Plan

**Created**: February 2026
**Status**: Sessions 1-3 complete, Session 4 next
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

## Current State (After Sessions 1-3)

### Parser & Model ✅
- `Node` record deserializes `superclassUsr`, `superclassNames`, `inheritsConvenienceInitializers`, `hasMissingDesignatedInitializers` from ABI JSON
- `ClassDecl` has `SuperclassUsr`, `SuperclassNames`, `DirectSuperclassName`, `InheritsConvenienceInitializers`, `HasMissingDesignatedInitializers`, `ResolvedSuperclass`, `HasResolvedSuperclass`, `HasExternalSuperclass`
- `ModuleProcessor.ResolveClassHierarchy()` resolves same-module superclass references with cycle detection

### TypeRecord & Module Database ✅
- `TypeRecord.SuperclassTypeName` persists direct superclass for cross-module support
- `ModuleDatabaseEmitter` serializes `superclass` attribute; `TypeDatabase` deserializes it
- Generic superclass names (e.g., `VirtualTimeScheduler<Converter>`) guarded — stored as null

### Emitter (`ClassHandler`) — inheritance emission ✅ (Session 3)
- Topological sort ensures base classes are emitted before derived
- Derived classes emit `class Derived : Base, [new protocols only]`
- Derived classes inherit `_payload` (now `protected`), `Dispose()`, `~Destructor()`, equality from base
- Derived classes re-emit `ISwiftObject` members (`GetTypeMetadata`, `NewFromPayload`, `MarshalToSwift`, `GetProtocolConformanceDescriptor`) with their own type metadata
- Private constructor on derived uses root base type for `SwiftSafeHandle<T>` (VWT Destroy / swift_release operates on isa pointer, ignoring T)
- Disposal `<remarks>` emitted on all class XML doc comments
- **Ownership analysis (Session 3)**: Generated class return path is correct — each Swift P/Invoke return provides +1 ARC retain, wrapper Dispose provides exactly -1. No ARC code changes needed.
- **Deferred to Session G**: Container/optional factory paths (`SwiftMarshal.MarshalFromSwift<T>` beyond generated class returns), manual `NewFromPayload` calls with aliased pointers

### Validation (`ProtocolConformanceValidator`) — own members only (Session 5)
Checks `type.Properties`, `type.Methods`, `type.Subscripts` — does NOT traverse superclass members. Inherited conformances may be suppressed incorrectly.

### Type ordering — topological sort ✅ (Session 3)
`BaseHandler.TopologicallySortTypes()` applies Kahn's algorithm in `HandleBaseDecl` before the emission loop. Handles both top-level types (`ModuleHandler.Emit` → `HandleBaseDecl`) and nested types (`ClassHandler.Emit` → `HandleBaseDecl`). Original index used as tie-breaker for stable ordering. `ReferenceEqualityComparer.Instance` used for ClassDecl identity.

---

## Implementation Sessions

### Session 1: Parse & Model ✅ COMPLETE

**Goal**: Get superclass data from ABI JSON into the model. Pure data plumbing — no emission changes.

**Changes**:

1. **`Node` record** (`SwiftABIParser.cs:63-67`): Added 4 nullable fields matching ABI JSON keys for automatic Newtonsoft.Json deserialization.

2. **`ClassDecl` model** (`ClassDecl.cs`): Added inheritance properties. `DirectSuperclassName` is a computed property from `SuperclassNames[0]` (not stored separately as originally planned).

3. **`CreateClassDecl`** (`SwiftABIParser.cs:705-708`): Populated from Node fields.

4. **Unit tests** (`ClassInheritanceParserTests.cs`): 8 tests covering superclass parsing, ObjC USRs, initializer flags, and default values.

**Results**: 4023 unit tests pass (18 new), 32/32 validation, golden files unchanged.

---

### Session 2: Class Hierarchy Resolution ✅ COMPLETE

**Goal**: Resolve superclass names to actual ClassDecl references. Persist superclass metadata on TypeRecord for cross-module support.

**Changes**:

1. **`ClassDecl`** (`ClassDecl.cs`): Added `ResolvedSuperclass`, `HasResolvedSuperclass`, `HasExternalSuperclass`.

2. **`ModuleProcessor.ResolveClassHierarchy()`**: Same-module resolution by `SwiftTypeName.ModuleQualifiedName` lookup. Cross-module and ObjC bases left unresolved (`HasExternalSuperclass = true`). Cycle detection via Floyd's tortoise-and-hare with full-cycle participant cleanup (all members of a cycle cleared, not just the detection node).

3. **`TypeRecord.SuperclassTypeName`** (`TypeRecord.cs`): Added `SwiftTypeName?` property. Populated in `RegisterClassType()` with guard for generic superclass names (`Contains('<')` → null, since `SwiftTypeName.FromModuleQualifiedName` rejects generics).

4. **Module database serialization**: `ModuleDatabaseEmitter` writes optional `superclass` attribute. `TypeDatabase.ReadVersion1_0` reads it back with matching generic-name guard.

5. **Unit tests**: `ClassHierarchyResolutionTests.cs` (6 tests: 3-level chain, ObjC base, cross-module base, root class, multiple hierarchies, same-module resolution). `SuperclassModuleDatabaseTests.cs` (4 tests: TypeRecord storage, XML emission, omission for root class, round-trip).

**Results**: 4023 unit tests pass, 32/32 validation, golden files unchanged, no emission changes.

**What was deferred to Session 3**: Topological sort of emission ordering. Originally planned for Session 2, but reordering types changes generated output (golden files, validation line counts), which contradicts Session 2's "no output changes" constraint. The sort is only needed once Session 3 adds `class Derived : Base` syntax (C# requires base class declared before derived in the same file). **Must also handle nested types** — not just top-level `moduleDecl.Types`, but also the `HandleBaseDecl(..., type.Types, ...)` calls for nested class hierarchies.

---

### Session 3: Core Emission — Topological Sort, Base Class Syntax, Shared Infrastructure ✅ COMPLETE

**Goal**: Topologically sort emission ordering (moved from Session 2), emit `class Derived : Base`, share infrastructure (payload, Dispose, ISwiftObject) between base and derived classes, document ownership model.

**Changes**:

0. **Topological sort** (`BaseHandler.TopologicallySortTypes` in `IHandler.cs`):
   - Kahn's algorithm applied in `HandleBaseDecl` before the emission loop — handles all contexts (top-level via `ModuleHandler.Emit`, nested via `ClassHandler.Emit`)
   - `ReferenceEqualityComparer.Instance` for ClassDecl identity. Original index tie-breaking for stability.
   - Cycle safety net: if any nodes remain with non-zero in-degree after Kahn's loop, they are appended in original order (not silently dropped) and a `Debug.WriteLine` warning is emitted.

1. **`ClassHandler.Emit`** — class declaration syntax:
   - Derived: `class Derived : BaseClass, ISwiftObject, [new protocols only]` — keeps ISwiftObject (needed for explicit interface re-implementation), omits IDisposable (inherited), filters base-class protocols.
   - Root: unchanged `class X : ISwiftObject, IDisposable, [protocols]`.
   - **Fallback guard**: `isDerived` requires both `HasResolvedSuperclass` AND that the base class won't be skipped during emission (`!GenericTypeEmitter.TryGetUnsupportedConstraint`). If the base has unsupported constraints (SwiftUI/Combine generics), derived falls back to flat emission.

2. **Derived class payload/Dispose/equality sharing**:
   - `_payload` is `protected` on base classes, inherited by derived.
   - `_payloadSize` is private per class (each class needs its own metadata size).
   - Derived classes inherit `Dispose()`, `~Destructor()`, `Payload` property from base.
   - `IEquatable<T>` is parameterized per type — both base and derived emit their own equality if they conform to Equatable.

3. **ISwiftObject re-implementation on derived classes**:
   - All ISwiftObject static abstract members (`GetTypeMetadata`, `NewFromPayload`, `MarshalToSwift`, `GetProtocolConformanceDescriptor`) re-emitted on derived with their own type metadata. Without this, `SwiftMarshal.MarshalFromSwift<Derived>()` would resolve to Base's `NewFromPayload`.
   - Private constructor on derived uses **root base type** for `SwiftSafeHandle<T>` (VWT Destroy / swift_release operates on the isa pointer, ignoring T). Root type found by walking `ResolvedSuperclass` chain in both `ClassHandler` and `MethodMarshalPlanBuilder`.

4. **Constructor chaining via `SwiftInheritanceChain` sentinel**:
   - Challenge: derived class constructors must chain to base, but base classes may not have a parameterless constructor (all-defaults constructors with unsupported types get skipped).
   - Solution: `SwiftInheritanceChain` marker struct in `Swift.Runtime/SwiftHandle.cs` — a unique empty struct that cannot conflict with any Swift-generated constructor parameter type.
   - All classes emit `protected ClassName(SwiftInheritanceChain _swiftObject)` constructor.
   - All derived class constructors (private SwiftHandle, protected sentinel, and public user-facing) chain to `base(default(SwiftInheritanceChain))`.
   - Three emission sites: `ClassISwiftObjectMethodWriter.EmitPrivateConstructor`, `ClassHandler.Emit` (sentinel), `WrapperEmitter.Signature.EmitSignatureConstructor`.
   - `HasParameterlessConstructor` check uses `CSSignature.Count <= 1` only (not all-defaults, which causes false positives for skipped constructors).

5. **Ownership analysis (documentation only, no ARC code changes)**:
   - Generated class return path is correct: each Swift P/Invoke return provides +1 ARC retain, wrapper's `Dispose()` → `VWT->Destroy` → `swift_release` provides exactly -1.
   - Disposal `<remarks>` emitted on all class XML doc comments.
   - **Deferred to Session G**: Container/optional factory paths, manual `NewFromPayload` with aliased pointers, Roslyn analyzer for compile-time dispose enforcement.

6. **Tests** (`ClassInheritanceEmissionTests.cs`, 34 tests):
   - Topological sort: no classes, single root, already correct, reversed, 3-level chain, mixed types, cyclic dependency (safety net).
   - Declaration syntax: derived starts with base, keeps ISwiftObject, omits IDisposable, new protocols emitted.
   - Payload sharing: no `_payload` field, no Dispose, no finalizer on derived; protected `_payload` on base.
   - ISwiftObject: own `GetTypeMetadata`, own `NewFromPayload`, root base type for `SwiftSafeHandle<T>`, 3-level hierarchy root base.
   - Fallback: external superclass flat emission, no resolved superclass flat emission, skipped base class (unsupported constraints) flat emission.
   - Disposal remarks on both base and derived.
   - Equality: derived with Equatable gets own equality; derived without Equatable inherits none.

**Results**: 4049 unit tests pass (34 new), 700 integration, 221 runtime. 32/32 validation libraries (up from 22/32 mid-session due to inheritance compilation fixes across Alamofire, SnapKit, Stripe hierarchies). Golden files updated. CompileCheck 0 errors. CS0108/CS0109 member-hiding warnings suppressed in TestFramework projects (member dedup deferred to Session 4).

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
- **Constructor chaining** — ✅ Resolved in Session 3 via `SwiftInheritanceChain` sentinel pattern.
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

**IMPORTANT: Validation runs ONCE at the very end of each session — not after each sub-task.** A session may involve 5-10 sub-tasks (parse this, update that, write tests, etc.). Do NOT run `run-tests.sh`, `validate-libraries.sh`, or `build-and-test.sh` after each sub-task. Run them all once as the final step of the session.

**Output capture**: Always pipe slow commands to a temp file so results can be re-read without re-running. Use the Read tool on the temp file (with offset/limit if needed) instead of re-running with different `tail`/`grep` arguments.

### End-of-session validation (run once, at the very end):
```bash
./run-tests.sh 2>&1 | tee /tmp/test-results.txt                                   # Unit tests (~2 min)
./validate-libraries.sh 2>&1 | tee /tmp/validation-results.txt                     # Compile gate (~5 min)
cd TestFramework && ./build-and-test.sh 2>&1 | tee /tmp/testframework-results.txt  # Integration + coverage (~5 min)
golden/check-golden-files.sh                                                       # Determinism (fast)
```

Sessions I1-I2 produced zero emission changes (model/resolution only) ✅. Sessions I3-I5 will change emission output. Session I6 is the full validation pass.
