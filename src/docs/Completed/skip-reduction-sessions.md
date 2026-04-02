# Skip Reduction Sessions

**Goal**: Reduce the addressable skip rate across validation libraries. Every member we can emit correctly is one less gap a consumer hits.

**Current baseline** (0.5.0, git SHA 7f916efa):
- 10,713 emitted members, 2,037 skipped (16% skip rate)
- 95/95 CS compile, 61/61 Swift compile

---

## Skip Landscape

### Addressable skip reasons (ordered by count)

| Reason | Count | What triggers it |
|--------|------:|------------------|
| **UnsupportedSignature** | 357 | Associated type refs (`Self.Element`), bare generics, placeholder types, method-level generics in wrapper context |
| **AnyTypeFallback** | 315 | TypeDatabase can't resolve a type → falls back to `Swift.AnyType`. Causes: missing registrations, unresolvable generic params, Optional<existential> inner protocols |
| **EveryProtocolConformanceSkipped** | 156 | Protocol conformance skipped → proxy suppressed. Sub-reasons: SelfTypedMembers, MissingRequirements, ConventionCClosureParameters, MethodLevelGenericsWithNonGenericMembers, HasSelfRequirement |
| **UnsupportedClosure** | 153 | Closure patterns not handled by any bridge (MethodClosureBridge, GenericClosureBridge, NestedClosureBridge, ProtocolExtensionClosureBridge) |
| **UnsatisfiedGenericConstraint** | 126 | Generic constraint requires ISwiftObject conformance or other C#-inexpressible constraint |
| **UnsupportedType** | 68 | Type can't be projected at all (no TypeRecord, not in any known framework) |
| **GenericTypeCallback** | 15 | Complex closure in generic type context — callback can't be hoisted to non-generic helper |

### Not addressable (correct behavior)

| Reason | Count | Why not |
|--------|------:|---------|
| ModuleInternal | 722 | Private API — correct to skip |
| SynthesizedCodable | 178 | .NET uses own serialization |
| GenericProtocolConstraint | 67 | Architecturally blocked by associated type erasure |
| SwiftUIConstraint | 64 | Framework boundary |
| SwiftUIView | 54 | SwiftUI bridge handles these |
| UnsupportedExistential | 41 | Opaque generics — fundamental limitation |
| StaticProtocolMember | 22 | Requires witness table dispatch infrastructure |

---

## Key Code Locations

These are the gate decision points that sessions will modify. Workers should read these files first.

| File | What it does |
|------|-------------|
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberGateEvaluator.cs` | Hard/soft gates for properties (P1-P5), methods (M1-M10), subscripts (S1-S5). Emits `SkipReason` for protocol member evaluation. |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` | Shared guard predicates for all four wrapper emitters. Contains `IsUnsupportedGenericContainer()`, `HasMethodOwnGenericParameters()`, `IsOptionalSupportedForCdecl()`. |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` | `CanEmitProperty()`, `CanEmitMethod()`, `CanEmitSubscript()` — where AnyTypeFallback skips happen after type resolution fails. |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs` | Three-phase validation: Phase 1 (wrapper eligibility), Phase 2 (post-processing), Phase 3 (generic type context gate for closures). |
| `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | Protocol conformance decisions (~line 688-907). Each sub-reason has its own gate. |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs` | Bridge for concrete closure args in methods. **Generic parent gate at line ~90-98**: `if (parentTd.IsGeneric) return false;` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/GenericClosureBridgeEmitter.cs` | Narrow pattern: sync, method-generic, noescape, identity-forwarding. **Non-closure param gate at line ~136-140**: hardcoded `return false;` |
| `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` | Closure bridge eligibility (~line 571-670). 10+ conditions for GenericClosureBridge. `RequiresThunk()` (~line 1640+). |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ValidationRuleSet.cs` | `ContainsAssociatedTypeReference()`, `ReferencesUnsupportedModule()` — shared validators. |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` | `GetTypeRecordOrAnyType()` — where AnyType fallback originates. |
| `src/Swift.Bindings/src/TypeDatabase/ConformanceGraph.cs` | Minimal (~55 lines). Stores TypeWitness mappings. Only used in ProtocolExtensionEmitter. |

---

## General Guidance

**Testing is mandatory for every session.** Unit tests validate internal logic; BindingTests validate the full pipeline (Swift source → generated C# → compiled Swift wrapper → runtime). Both matter.

- **Unit tests**: Add or update tests for any modified gate logic, validation rules, or marshalling paths. These live in `src/Swift.Bindings/tests/UnitTests/`.
- **BindingTests**: When a session unlocks a new Swift pattern, add Swift source to `BindingTests/Sources/SwiftBindingsTestLib/` and C# runtime tests to `BindingTests/RuntimeTestsApp/` in the appropriate domain file. This proves the generated code actually works end-to-end, not just that it compiles. A unit test can pass while the generated code crashes at runtime — BindingTests catch that.
- **Validation gates**: Every session must run `nuke test` and `nuke validate` at minimum. Sessions that change emitter output or closure/generic handling must also run `nuke binding-tests`.

---

## Sessions

### Session 1: AnyTypeFallback Reduction ✅ (commit 8ceb6f38)

**Skip target**: AnyTypeFallback (315 skips)
**Difficulty**: Medium — mix of easy wins and harder resolution issues

AnyTypeFallback is the second-highest addressable skip reason. It fires when `GetTypeRecordOrAnyType()` can't resolve a Swift type to a C# projection. Some of these are genuinely unresolvable (associated types, deeply nested generics), but others may be missing TypeDatabase registrations or overly conservative resolution logic.

**Approach**:
1. Run `nuke validate` and capture the per-library binding reports from `/tmp/binding-validation-<branch>/`. Parse the `binding-report.json` files to categorize AnyTypeFallback skips by root cause: what Swift types are falling through?
2. Group the types: (a) types from known Apple frameworks missing from XML databases, (b) generic type parameters that could be resolved via context, (c) types from library dependencies not in scope, (d) genuinely unresolvable (associated types, etc.)
3. For category (a): add missing entries to the appropriate XML database files in `src/Swift.Bindings/src/TypeDatabase/`.
4. For category (b): improve resolution logic in `TypeDatabaseExtensions` and `MemberEmissionValidator`.
5. Leave categories (c) and (d) as known gaps — document them in findings.

**Key files**: `TypeDatabaseExtensions.cs`, `MemberEmissionValidator.cs`, XML database files in `TypeDatabase/`, `MemberGateEvaluator.cs`

**Testing**: Unit tests for any TypeDatabase resolution changes. If a fix unlocks a pattern that can be represented in BindingTests (e.g., a type that was falling back to AnyType now resolves correctly), add Swift source + C# runtime test coverage to prove it works end-to-end.

**Validation**: `nuke test`, `nuke validate`. Compare AnyTypeFallback count in skip_metrics before and after. `nuke binding-tests` if any BindingTests types are affected.

**Success**: AnyTypeFallback count measurably reduced. No regressions in CS/Swift compile counts.

---

### Session 2: Closure Bridge Expansion ✅ (commit 462d6f6b)

**Skip target**: UnsupportedClosure (153 skips) + GenericTypeCallback (15 skips)
**Difficulty**: Medium-Hard — extending existing bridge patterns, not building new ones

The generator has four closure bridge patterns, each covering a narrow set of cases. Two specific gates are known to be overly conservative:

1. **MethodClosureBridge generic parent gate** (`MethodClosureBridge.cs:~90-98`): Currently `if (parentTd.IsGeneric) return false;` — blocks ALL methods on generic classes from using this bridge. The issue is that `[UnmanagedCallersOnly]` callbacks can't be defined inside generic helper classes. The fix likely involves hoisting callbacks to a non-generic helper class (similar to how ProtocolExtensionClosureBridge works).

2. **GenericClosureBridge non-closure parameter gate** (`GenericClosureBridgeEmitter.cs:~136-140`): Currently hardcoded `return false;` for `AreNonClosureParamsCompatible()`. This means the bridge only works when the closure is the sole parameter. Implementing per-parameter marshalling in the Swift wrapper would unlock methods with both closure and value-type parameters.

**Approach**:
1. Read and understand all four bridge patterns (MethodClosureBridge, GenericClosureBridge, NestedClosureBridge, ProtocolExtensionClosureBridge) and how they interact with `MemberValidationPipeline` Phase 3.
2. Profile which of the 153 UnsupportedClosure skips would be unblocked by each gate relaxation. Run validation and check binding reports for closure skip details.
3. Implement the more tractable fix first (likely MethodClosureBridge generic parent expansion — the ProtocolExtensionClosureBridge already demonstrates the callback-hoisting pattern).
4. If time permits, implement GenericClosureBridge non-closure param support.
5. Add BindingTests coverage for newly-supported closure patterns.

**Key files**: `MethodClosureBridge.cs`, `GenericClosureBridgeEmitter.cs`, `ClosureHandler.cs`, `MemberValidationPipeline.cs`, `ProtocolExtensionClosureBridge.cs` (reference for hoisting pattern)

**Validation**: `nuke test`, `nuke validate`, `nuke binding-tests` (closure changes require end-to-end validation).

**Success**: UnsupportedClosure + GenericTypeCallback counts reduced. New BindingTests passing for closure-in-generic-type patterns.

---

### Session 3: EveryProtocolConformanceSkipped Reduction ✅ (commit 17d1219e, regression fix 6540dbce)

**Skip target**: EveryProtocolConformanceSkipped (156 skips)
**Difficulty**: Medium — auditing gates, relaxing where safe

When the EveryProtocolEmitter decides a protocol conformance can't be emitted, the downstream ProtocolHandler suppresses the entire proxy class. 156 skips is significant — each represents a full protocol interface + proxy that consumers can't use.

The conformance decision happens in `EveryProtocolEmitter.EmitProtocolConformance()` (~line 688-907). Sub-reasons:

- **SelfTypedMembers**: Instance methods with `τ_0_0` in return type or params. Current gate skips the entire protocol. Could be relaxed to emit the protocol with those specific members as `NotSupportedException` stubs (similar to InterfaceOnly pattern).
- **MethodLevelGenericsWithNonGenericMembers**: Protocol has BOTH method-level generic methods AND regular members. Current gate skips entirely. Could emit the non-generic members normally and stub the generic ones.
- **MissingRequirements**: Requirements that failed ABI parsing. May be worth re-examining — some might parse now with current parser improvements.
- **ConventionCClosureParameters**: `@convention(c)` closures not encoded in ABI JSON. Hard to fix without ABI JSON changes.
- **HasSelfRequirement**: Protocol declares `Self` in generic signature. Fundamental limitation.

**Approach**:
1. Run validation and categorize the 156 skips by sub-reason (parse `binding-report.json` files for the detail strings).
2. For each sub-reason, assess whether partial emission is safe (emit what we can, stub what we can't).
3. Start with **MethodLevelGenericsWithNonGenericMembers** — this is the most likely to have safe partial emission. The non-generic members should work normally.
4. Then tackle **SelfTypedMembers** — emit protocol interface with Self-typed members using `Swift.AnyType` or similar fallback, emit proxy stubs.
5. Leave HasSelfRequirement and ConventionCClosureParameters as-is (fundamental limitations).

**Key files**: `EveryProtocolEmitter.cs` (~line 688-907), `ProtocolHandler.cs` (~line 440-455), `ModuleEmissionContext.cs` (conformance tracking)

**Validation**: `nuke test`, `nuke validate`, `nuke binding-tests` (protocol changes need end-to-end validation).

**Success**: EveryProtocolConformanceSkipped count reduced. Protocols that were fully suppressed now emit with partial member coverage.

---

### Session 4: Result<T,E> & Generic Container Support ✅ (commit 8cc1b904)

**Skip target**: Subset of UnsupportedSignature + UnsupportedType (estimated ~71 skips across ~20 libraries)
**Difficulty**: Hard — new marshalling path end-to-end

`Result<T, E>` is the most common Swift pattern that consumers will notice is missing. It's blocked at `WrapperValidation.IsUnsupportedGenericContainer()` (line ~333-343) and never reaches the marshaler or Swift wrapper stages.

**Approach**:
1. Profile how `Result` is used across validation libraries. Common patterns: `Result<ConcreteType, any Error>`, `Result<ConcreteType, ConcreteEnum>`, `Result<Data, any Error>`. Rank by frequency.
2. Design the @_cdecl wrapper strategy for Result. Options:
   - Decompose into `(success_ptr, error_ptr, is_success)` tuple through the boundary
   - Use indirect result buffer
   - Map to C# `(T?, Exception?)` tuple or a `SwiftResult<T, E>` wrapper type
3. Start with the most common pattern: `Result<ConcreteClass, any Error>` in return position.
4. Implement: (a) lift the `IsUnsupportedGenericContainer` gate for supported Result patterns, (b) add Result marshalling in `CdeclParamMapper` / wrapper emitters, (c) generate @_cdecl wrapper that decomposes Result, (d) emit C# API.
5. Add BindingTests: Swift function returning `Result<String, any Error>`, verify C# can consume both success and failure cases.

**Key files**: `WrapperValidation.cs` (gate), `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `SubscriptWrapperEmitter.cs` (all reference the gate), plus new marshalling code.

**Validation**: `nuke test`, `nuke validate`, `nuke binding-tests`.

**Success**: At least `Result<ConcreteType, any Error>` emits and works at runtime. Real libraries (Alamofire) benefit.

---

### Session 5: Method-Level Generics (Common Patterns) ✅ (commit cb89ae2f, fix f8a710e1)

**Skip target**: Subset of UnsupportedSignature (~179 method-generic skips across ~13 libraries)
**Difficulty**: Hard — requires wrapper strategy for generic type metadata

Methods with their own generic type parameters (`func transform<T>(_ value: T) -> T`) are blocked at `WrapperValidation.HasMethodOwnGenericParameters()` (line ~306-327) and `MethodWrapperEmitter.cs:~71`.

The @_cdecl boundary can't express method-level generics directly. Options:
- **Monomorphization**: Generate specialized wrappers per concrete type used. Requires knowing the call sites.
- **Type-erased dispatch**: Pass type metadata pointer through the boundary. Requires `UnsafeMutableRawPointer`-based boxing.
- **CallConvSwift fallback**: Keep as direct Swift calling convention (no @_cdecl wrapper). Limited by upstream Issue #1 (non-blittable type rejection).

**Approach**:
1. Profile the 179 skips: what are the actual generic method signatures? Categorize: single type param + protocol constraint (most common?), unconstrained, multi-param, generic constructors.
2. Focus on the most common pattern — likely `func foo<T: SomeProtocol>(_ value: T)` with a single constrained type parameter.
3. Design the wrapper approach. The GenericClosureBridge already demonstrates one pattern for method-level generics (Unmanaged boxing). Can this be generalized?
4. Implement for the tractable subset. Generic constructors are out (C# doesn't support them) — emit factory methods if needed.
5. Add BindingTests coverage.

**Key files**: `WrapperValidation.cs`, `MethodWrapperEmitter.cs`, `GenericClosureBridgeEmitter.cs` (reference for type metadata handling), `ClosureHandler.cs` (eligibility checks)

**Validation**: `nuke test`, `nuke validate`, `nuke binding-tests`.

**Success**: Single-type-parameter constrained generic methods emit and compile. Portion of 179 skips resolved.

---

### Session 6: UnsatisfiedGenericConstraint Reduction ✅ (commit 7e1e0dd7)

**Skip target**: UnsatisfiedGenericConstraint (126 skips)
**Difficulty**: Medium — some may be relaxable, others fundamental

This fires when a generic constraint requires something C# can't express. Common case: a generic parameter must conform to a protocol that requires `ISwiftObject`, but the projected C# type doesn't implement it.

**Approach**:
1. Profile the 126 skips: what constraints are failing? Parse binding reports for detail strings.
2. Categorize: (a) constraints that could be satisfied with additional `ISwiftObject` conformances on projected types, (b) constraints on associated types or Self, (c) constraints involving unresolvable types.
3. For category (a): add missing interface implementations or adjust the constraint checker to recognize additional satisfiable patterns.
4. For category (b): likely fundamental — document and leave.
5. Check `MemberGateEvaluator.cs` M5 gate and `BoundGenericsHandler.ShouldSkipConstraint()` for the decision logic.

**Key files**: `MemberGateEvaluator.cs` (M5 gate), `BoundGenericsHandler.cs`, `MemberValidationPipeline.cs`

**Testing**: Unit tests for any constraint checker changes. If relaxing a constraint unlocks new generic patterns (e.g., a bound generic method that now emits), add Swift source + C# runtime test in BindingTests to verify the generated code works correctly at runtime.

**Validation**: `nuke test`, `nuke validate`, `nuke binding-tests` if constraint changes affect emitted output.

**Success**: Measurable reduction in UnsatisfiedGenericConstraint count.

---

### Session 7: Validation Gate & Baseline Update ✅ (commit c1ee9580)

**Skip target**: N/A — this is the cleanup session
**Difficulty**: Low

After all prior sessions, run the full validation suite, update baselines, and ensure zero regressions.

**Approach**:
1. Clear the validation cache: `rm -rf /tmp/binding-validation-$(git rev-parse --abbrev-ref HEAD | tr '/' '-')`
2. Run all three gates: `nuke test`, `nuke validate`, `nuke binding-tests`
3. Also run `nuke runtime-tests-simulator` — closure and generic changes may affect runtime behavior
4. Compare new `skip_metrics` against pre-session-1 baseline (documented at top of this file)
5. Update `.validation-baseline.json` with new counts
6. Update `roadmap.md` with new baseline numbers
7. If any regressions exist in CS/Swift compile counts, fix them before committing

**Success**: All gates green. Baseline updated. Skip rate measurably lower than 16%. No compile count regressions.
