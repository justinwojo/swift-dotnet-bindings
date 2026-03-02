# Foundation Roadmap

**Date**: March 1, 2026
**Launch scope**: 90% of libraries score 4.0+ on binding quality. Remaining gaps (class-level generic closures, ~45 methods) are documented as known limitations with a clear post-launch path.
**Goal**: Make the generator's core abstractions solid enough that the VAST MAJORITY of Swift library patterns are handled by existing infrastructure. The four pillars below eliminate structural gaps; a small number of architectural-level closure patterns (P3.5) are deferred with explicit justification.
**Prerequisite**: Complete all foundation pillars before pillar-DEPENDENT feature work.
**Two-lane policy**: Foundation sessions (A1-D1) run in one lane. Dependency-free features (F1, F2, F5, F6) run in a parallel lane — interleaved between foundation sessions as palate-cleansers or when pillar work needs to settle. One active foundation session at a time; features don't block foundations.

---

## The Four Pillars

The generator's ability to handle "any Swift library" depends on four independent architectural pillars. Each addresses a different class of binding failures. Together they eliminate the structural gaps that currently limit binding quality.

| Pillar | What It Solves | Methods Unblocked | Long-Term Value |
|--------|---------------|:-----------------:|-----------------|
| **1. Type Resolution** | AnyType fallbacks, Self-return, associated types | ~280+ | Every library with protocols |
| **2. Dispatch Architecture** | 19-step cascade fragility, WasEmitted bugs | 0 (refactor) | Every future dispatch kind |
| **3. Closure Bridge System** | 184 skipped closure methods | ~100-140 | Every library with callbacks |
| **4. Generic Constraint Completeness** | 65 blocked generic methods | ~55-60 | Every library with generics |

---

## Pillar 1: Type Resolution Pipeline

**Problem**: Type resolution is scattered across ad-hoc paths. Three distinct sub-problems are conflated as "AnyType":

1. **Protocol extension Self-return** (254 AnyType occurrences) — The `ProtocolExtensionEmitter` correctly substitutes `Self → ConcreteType` for CLASS conformers. But struct conformers are invisible (`CollectConformances` only checks `ClassDecl`), and protocol INTERFACES show `AnyType` for `τ_0_0` returns when `HasSelfRequirement` is not set.

2. **Associated type resolution** (24-32 GRDB methods + growing future) — `DependentMember` nodes in ABI JSON parse as `NamedTypeSpec("τ_0_0.Element")` (dead code path for `AssociatedTypeReferenceSpec`). TypeWitness data exists in ABI JSON but isn't extracted or queryable. `ResolveSelfElement` matches by generic param name — fails when sugared name ≠ associated type name (e.g., `RecordCursor<Record>` where `Element = τ_0_0`).

3. **DynamicSelf fragility** (100 methods, working by accident) — `MethodRequiresIndirectResult` returns correct answer for DynamicSelf via accidental `IsExistentialTypeName("Self") → AnyType → not frozen → indirect`. Needs explicit `IsDynamicSelf` guard.

### Session P1.1: Struct Conformers in Protocol Extensions
**Goal**: Add non-frozen struct types to `ProtocolExtensionEmitter.CollectConformances()`. Emit struct-self Swift wrappers with value-copy semantics.
**Effort**: 1 session
**Impact**: GRDB query builder chain (`Select`, `Filter`, `Order`, `Group`) becomes available on `QueryInterfaceRequest<T>`

Key changes:
- `ProtocolExtensionEmitter.CollectConformances()` line 121 — include `StructDecl` (non-frozen)
- `EmitSwiftWrapper()` — struct self path: `self_.assumingMemoryBound(to: T.self).pointee` instead of `Unmanaged<T>.fromOpaque(self_).takeUnretainedValue()`
- Return path: value-type copy semantics, not `Unmanaged.passRetained`
- Self-return substitution at `BuildSyntheticMethodDecl` line 1518 already works for both class and struct

Key files: `ProtocolExtensionEmitter.cs`
Research: [`research/self-return-protocol-extension-trace.md`]

### Session P1.2: ConformanceGraph from ABI JSON TypeWitness Data
**Goal**: Build a queryable conformance graph from existing ABI JSON data. Wire into type resolution so `Self.Element` resolves for non-generic conformers and name-mismatched generic conformers.
**Effort**: 1 session
**Impact**: ~24-32 GRDB cursor methods unlocked (`forEach`, `reduce`, `contains(where:)`, `first(where:)` on `SQLStatementCursor`, `RowCursor`, `RecordCursor<Record>`, `DatabaseValueCursor<Value>`)

Key changes:
- New `ConformanceGraph.cs` (~60 lines): `Dictionary<(SwiftTypeName, string, string), TypeSpec>` keyed by `(ConformingType, Protocol, AssocTypeName)`
- `SwiftABIParser.HandleConformance()` line 539 — parse TypeWitness children from conformance nodes
- `ProtocolExtensionEmitter.ResolveSelfElement` — add fallback query when generic-param name match fails
- `BoundGenericsHandler.TranslateTypeSpecToCSharp` line 627 — replace `AssociatedTypeReferenceSpec => AnyType` with graph lookup
- `TypeProjectionFactory.Project()` — add `AssociatedTypeReferenceSpec` dispatch case

MVP categories:
- Concrete: `(Type, Protocol, AssocTypeName) → NamedTypeSpec("Foundation.Data")` ✓
- Generic forwarding: `(Type, Protocol, AssocTypeName) → GenericParam[N]` ✓
- Chained: `τ_0_0.Fetcher` → defer (limit depth to 1-2 levels, fall back to AnyType)
- `Any`: unresolvable → skip

**Explicit non-goals for MVP**:
- No deep chained resolution (max 1 hop, AnyType fallback beyond)
- No method-generic context resolution (e.g., `func response<T: Serializer>()` — T is unknown at generation time, TypeWitness can't help)
- No conditional conformance awareness (WHERE clause data is swiftinterface-only)
- No cross-module conformance graph merging

**Success metrics**: ≥20 new GRDB cursor methods compile. `SQLStatementCursor.forEach`, `RowCursor.reduce`, `RecordCursor<Record>.first(where:)` emit with concrete element types, not AnyType.

Key files: `ConformanceGraph.cs` (new), `SwiftABIParser.cs`, `BoundGenericsHandler.cs`, `TypeProjectionFactory.cs`, `ProtocolExtensionEmitter.cs`
Research: [`swiftinterface-conformance-audit.md`], [`research/anytype-integration-trace.md`], [`research/typwitness-coverage-validation.md`]

### Session P1.3: DynamicSelf Hardening
**Goal**: Replace accidental correctness with explicit guards. Prevent future breakage if `IsExistentialTypeName` heuristic changes.
**Effort**: 0.5 session (combine with another session)
**Impact**: 0 methods (hardening, not feature)

Key changes:
- `MarshallingHelpers.MethodRequiresIndirectResult` before line 129 — add `if (returnType.SwiftTypeSpec.IsDynamicSelf) return true;`
- `TypeDatabaseExtensions.GetTypeRecordOrThrow` before line 273 — add `if (typeSpec.IsDynamicSelf) return AnyType;`

Key files: `MarshallingHelpers.cs`, `TypeDatabaseExtensions.cs`
Research: [`self-return-investigation.md`]

### Session P1.4: DependentMember Parsing Fix
**Goal**: Fix the dead code path for `DependentMember` nodes in ABI JSON. Currently `kind="TypeNominal", name="DependentMember"` falls through to `TypeSpecParser.Parse("τ_0_0.Element")` which creates `NamedTypeSpec("τ_0_0.Element")` as a single token (`.` is not excluded by the tokenizer). The `case "DependentMember":` at line 1549 is dead code.
**Effort**: 0.5 session (combine with P1.2)
**Impact**: Enables ConformanceGraph to work — parsed TypeSpecs become proper `AssociatedTypeReferenceSpec` instead of mangled `NamedTypeSpec`

Key changes:
- `SwiftABIParser.CreateTypeSpec` — handle `TypeNominal` with `name == "DependentMember"` by creating `AssociatedTypeReferenceSpec` (like the existing dead code path does, but triggered by the correct condition)

Key files: `SwiftABIParser.cs`

### Pillar 1 Dependencies
```
P1.4 (DependentMember fix) ──┐
                              ├── P1.2 depends on P1.4 (correct parsing needed for graph)
P1.1 (struct conformers)  ────── independent
P1.3 (DynamicSelf)         ────── independent (combine with P1.1 or P1.2)
```

---

## Pillar 2: Dispatch Architecture

**Problem**: `MethodHandler.Emit()` is a 19-step cascade with 6 bridge emitters, 4 post-processors, 3 flag-mutation steps, and 1 accumulate-pattern pre-scan. Adding new dispatch kinds is risky. 5 bridge emitters have a latent `WasEmitted` bug (return `true` but never set `WasEmitted` on the `MethodDecl`).

### Session P2.1: Dispatch Table with IMethodBridgeEmitter
**Goal**: Replace the cascade with a declarative dispatch table. Fix WasEmitted bug systematically.
**Effort**: 1 session
**Impact**: 0 methods (refactor), but every future dispatch kind is safer to add

Implemented: `IMethodBridgeEmitter` interface with `BridgeEmitterContext` record and `BridgeEmitResult` record.
7 adapter classes in method-path dispatch table (ordered):
1. `ExistentialBypassBridgeAdapter` — must be first (existential-blocked methods handled before other bridges)
2. `ArraySliceBridgeAdapter`
3. `GenericClosureBridgeAdapter`
4. `ProtocolExtensionClosureBridgeAdapter` — before MethodClosureBridge (invariant #2)
5. `MethodClosureBridgeAdapter`
6. `NestedClosureBridgeAdapter` (added A4, included here)
7. `OptionalClosureBypassAdapter` — last (narrowest scope), internalizes dedup pattern

WasEmitted bug fixed in 5 locations: method-path ArraySlice/ExistentialBypass/OptionalClosureBypass (via dispatch loop), constructor-path ExistentialBypass and OptionalClosureBypass (direct fix).
ConstrainedExistentialBridge is constructor-only — deferred to P2.2 (constructor sharing).
`GetProjectedCSharpMethodKey` widened from `protected` to `internal` for adapter access.
All bridge-dispatch types (`BridgeEmitterContext`, `BridgeEmitResult`, `IMethodBridgeEmitter`) are `internal`. `BridgeEmitters` exposed as `IReadOnlyList<>` (immutable).
13 new unit tests in `BridgeDispatchTableTests.cs` (structural invariants, result semantics, constructor WasEmitted behavior, immutability).

Key files: `IMethodBridgeEmitter.cs` (new), `MethodHandler.cs`, `IHandler.cs`, `BridgeDispatchTableTests.cs` (new)

### Session P2.2: IMethodPostProcessor + ConstructorHandler Sharing
**Goal**: Wrap 4 post-processors in `IMethodPostProcessor` interface. Share dispatch table between `ConstructorHandler` and `MethodHandler` with scope filtering.
**Effort**: 0.5-1 session
**Impact**: 0 methods (refactor)

Key changes:
- Define `IMethodPostProcessor` interface: `void TryPostProcess(MethodDecl, TypeDecl, ...)`
- Wrap DefaultParameterOverloadEmitter, CompletionHandlerDetector, NativeIntOverloadEmitter, ArraySliceNormalizationEmitter
- `MethodBridgeScope` enum: `Methods`, `Constructors`, `Both`
- Share dispatch table between ConstructorHandler and MethodHandler

Key files: `MethodHandler.cs`, `ConstructorHandler.cs`, `IMethodPostProcessor.cs` (new)

### Pillar 2 Dependencies
```
P2.1 (dispatch table)  ────── independent of Pillar 1
P2.2 (post-processors) ────── after P2.1
```

---

## Pillar 3: Closure Bridge System

**Problem**: 184 methods skipped as `UnsupportedClosure` across 9 libraries. Current bridges handle specific patterns but there's no systematic classification. Each new pattern requires a new bridge emitter.

### Classification (from deep investigation)

| Category | Count | Difficulty | Phase |
|----------|:-----:|:----------:|:-----:|
| **P1**: ObjC/Foundation types not in TypeDatabase | ~40 | Medium | 1 |
| **M3b**: Plain enums not flagged `simpleEnum` | ~4 | Easy | 1 |
| **M1**: Multi-closure methods | ~14 | Medium | 2 |
| **M6**: Optional\<String\>/\[String\] closure return | ~4 | Medium | 2 |
| **P2**: Swift class params fail `CanInvokeFromCSharp` | ~15 | Hard | 3 |
| **M2**: Nested closures (closure-in-closure) | ~26 | Hard | 3 |
| **M3a**: Complex enum params (associated values) | ~17 | Hard | 4 |
| **M7**: Result\<(), Error\> closure return | ~4 | Medium | 4 |
| **M4/M5**: Existential array/optional closure return | ~5 | Hard | 4 |
| **M10**: ObjC types as method closure params | ~14 | Medium | 1 |
| **M8**: Class-level generic in closure arg | ~22 | Architectural | 5 |
| **M9/M11/M12**: Complex generics in closures | ~23 | Architectural | 5 |

### Session P3.1: Data Fixes (~58 methods)
**Goal**: Register missing ObjC Foundation types in TypeDatabase XML. Fix `simpleEnum` detection for plain enums without raw types.
**Effort**: 1 session
**Impact**: ~54-58 closure methods unlocked (P1 + M3b + M10)

Key changes:
- Register `URLSession`, `URLSessionTask`, `URLAuthenticationChallenge`, `URLResponse`, `HTTPURLResponse`, `CachedURLResponse`, `Progress`, `NSError` in FoundationDatabase.xml (or new ObjCTypesDatabase.xml)
- Fix `simpleEnum` flag: enums with no associated values and no raw type should be `simpleEnum=true` (they're blittable integer tags)
- `IsCdeclCompatibleType` already handles `IsObjCBridgedClass` — once DB entries exist, paths open

Key files: TypeDatabase XML files, `ModuleProcessor.cs` (simpleEnum detection)

### Session P3.2: Multi-Closure + Return Type Extensions (~18 methods)
**Goal**: Extend bridge emitters to handle 2+ closure params. Lift B7 gate for `Optional<String>`, `[String]` closure returns.
**Effort**: 1 session
**Impact**: ~14-18 methods (M1 + M6)

Key changes:
- MethodClosureBridge: emit N callback+funcPtr pairs (one per closure), each with `MCB_{hash}_{i}` suffix
- P/Invoke receives N funcPtr+context pairs
- B7 gate: allow `Optional<String>` and `[String]` as closure return types

Key files: `MethodClosureBridge.cs`, `ClosureHandler.cs` (B7 gate)

### Session P3.3: Nested Closures + Class Params (~41 methods)
**Goal**: Two-level closure bridge for nested closures. Extend `IsInvocableParameter` for class types.
**Effort**: 1.5 sessions (0.5 prototype spike in Phase A + 1 session full implementation in Phase C)
**Impact**: ~26-41 methods (M2 + P2)

Key changes:
- Inner closure bridge: separate cdecl wrapper with its own funcPtr+context
- Swift wrapper reconstructs inner closure from funcPtr+context before calling original method
- `IsInvocableParameter` extended to accept class types (via `IsClassType` check → IntPtr marshaling)

Key files: `ClosureHandler.cs`, `ClosureEmitter.SwiftWrapper.cs`, `PropertyHandler.cs`
**Risk**: High — new ABI territory for nested closure lifetime management

**De-risk strategy**: Prototype spike in Phase A targets ONE concrete method (e.g., Alamofire `Interceptor.init(adapt:retry:)`). Proves the two-level bridge ABI works before committing to full implementation. If the spike fails, the roadmap adjusts before Phase C — not after.

### Session P3.4: Complex Enum Bridge (~17 methods)
**Goal**: Bridge closures with enum params that have associated values.
**Effort**: 1 session
**Impact**: ~17 methods (M3a)

Key changes:
- Swift wrapper deconstructs enum: passes (tag + payload bytes) as separate cdecl args
- C# callback reconstructs discriminated union from tag + payload
- Requires C# `readonly struct` representation for each complex enum at the cdecl boundary

### Session P3.5: Architectural Generics (~45 methods, DEFERRED)
Categories M8, M9, M11, M12 have class-level generic params in closure args. Monomorphizing `τ_0_0=UnsafeMutableRawPointer` would violate generic constraints. These require either:
- TypeMetadata-based API (passing type information at runtime)
- Type erasure with existential containers
- Accepting the limitation

**Recommendation**: Defer to post-public. These 45 methods are the hardest 25% and require architectural decisions that shouldn't block launch. Document as known limitation.

**Library criticality of deferred methods**: RxSwift (~22 methods, `subscribe`/`flatMap`/`map` variants — high visibility), Alamofire (~8 methods, interceptors — medium), Kingfisher (~6 methods, image processors — medium), GRDB (~5 methods, value observation — low, alternatives exist), others (~4 methods — low). RxSwift is the most impactful post-launch target if this pillar is revisited.

### Pillar 3 Dependencies
```
P3.1 (data fixes)      ────── independent (can start anytime)
P3.2 (multi-closure)   ────── after P2.1 (dispatch table makes adding bridges safer)
P3.3 (nested closures) ────── after P3.2 (builds on multi-closure infrastructure)
P3.4 (complex enums)   ────── after P3.1 (enum DB entries needed)
P3.5 (arch generics)   ────── deferred
```

---

## Pillar 4: Generic Constraint Completeness

**Problem**: ~65 methods blocked by `UnsatisfiedGenericConstraint`. Three sub-problems:

1. **Incomplete `SatisfiesConstraint`** (~45 methods) — returns `false` for all protocols except `Equatable`. Generated types DO implement `ISwiftObject` — the conformance check just isn't implemented. Comment at line 874: `"General protocol conformance emission is handled in a later task."`

2. **Missing conditional extension constraints** (~10-15 methods) — Swift's `extension Table where T: FetchableRecord` adds constraints the parent class doesn't carry. Generator skips instead of emitting method-level `where T0 : IFetchableRecord`.

3. **Non-ISwiftObject type arguments** (~5 methods) — `int`, `string`, `byte[]` genuinely can't implement `ISwiftObject`. Architectural limitation.

### Session P4.1: General Protocol Conformance Check (~45 methods)
**Goal**: Implement the `SatisfiesConstraint` TODO. Check if type argument's `TypeDecl` has a conformance to the protocol constraint.
**Effort**: 0.5-1 session
**Impact**: ~45 methods unlocked (Alamofire serializer chain, GRDB fetch methods, Kingfisher processor methods)

Key changes:
- `BoundGenericsHandler.SatisfiesConstraint()` at line 835 — check `typeArgumentDecl.Conformances` for the protocol constraint instead of returning `false`
- Conformance data already available via existing infrastructure

**Staged rollout criteria** (to prevent regressions from opening a conservative gate):
1. Run full 53-target validation before AND after — diff newly-emitted methods
2. All newly-emitted methods must compile (validation gate catches CS-errors)
3. Add targeted unit tests for each newly-unblocked pattern (serializer chain, fetch cursor, processor)
4. Spot-check 3-5 representative methods at runtime on iOS Simulator to verify witness table dispatch works end-to-end
5. If any runtime failure surfaces, add a conformance-specific allowlist instead of blanket opening

Key files: `BoundGenericsHandler.cs`
Research: [`research/iswiftobject-constraint-investigation.md`]

### Session P4.2: Conditional Extension Constraint Propagation (~10-15 methods)
**Goal**: When a method is from a conditional Swift extension, add the extension's constraints to the C# method's `where` clause.
**Effort**: 1 session
**Impact**: ~10-15 methods (GRDB `fetchAll`, `fetchOne`, `fetchCursor` patterns)

Key changes:
- Parse method's `genericSig` from ABI JSON for constraints beyond parent type
- `GenericTypeEmitter` — emit method-level `where` clauses with additional constraints
- `BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint` — don't reject when constraint can be added to method

Key files: `GenericTypeEmitter.cs`, `BoundGenericsHandler.cs`, `MethodHandler.cs`

### Session P4.3: Document Category 1 Limitation
**Goal**: Accept and document that ~5 methods with primitive/ObjC type arguments can't satisfy `ISwiftObject`. Clear diagnostics.
**Effort**: 0.25 session
**Impact**: Better developer experience, no code fix

### Pillar 4 Dependencies
```
P4.1 (SatisfiesConstraint) ────── independent (can start anytime)
P4.2 (conditional ext)     ────── after P4.1
P4.3 (documentation)       ────── after P4.1
```

---

## Cross-Cutting: Emission Infrastructure

Not a pillar, but supports all four. Can be done in parallel.

### Composition Helpers (from original R1)
- **SwiftPrimitiveMap**: 6 call sites, ~86 lines saved. Normalize CGFloat to `NFloat`.
- **ClassReturnMarshalHelper**: 8 call sites, ~52 lines saved.
- **StructReturnMarshalHelper**: 4 sites, ~47 lines saved.
- **ClosureCallbackHelper**: Minimal extraction only (funcPtr field emission). Full builder deferred (ABIs too different).

**Effort**: 1 session. Independent of all pillars.

---

## Session Plan

Sessions are numbered by phase: A1, A2, etc. Each is sized for a single Claude session.
**Direct** = start coding (research docs are the plan). **Plan** = explore codebase first, confirm approach.

### Phase A — Independent Foundations

All Phase A sessions are independent — no ordering required between them.

---

**A1: Generic Constraint Check + DynamicSelf Hardening** | Direct | **COMPLETE**
*Pillars: 4 (SatisfiesConstraint) + 1 (DynamicSelf) | ~45 methods unlocked + 100 hardened*

**Done.** `SatisfiesConstraint` now checks all protocol conformances via `HasConformance` (direct) + `HasTransitiveConformance` (inherited). 6 DynamicSelf `IsDynamicSelf` guards added across `MarshallingHelpers.cs` and `TypeDatabaseExtensions.cs` (5 type-resolution entry points + `TryGetAnyTypeFallbackInfo`). +610 net lines across Alamofire/GRDB/Lottie/XMLCoder. Kingfisher -17 / Swinject -3 from diagnostic annotation cleanup (Self no longer misclassified as existential fallback). 12 new unit tests. 53/53 validation, 4856 unit tests passing.

Details: [Pillar 4 § P4.1](#session-p41-general-protocol-conformance-check-45-methods), [Pillar 1 § P1.3](#session-p13-dynamicself-hardening)

---

**A2: Struct Conformers in Protocol Extensions** | Direct | **COMPLETE**
*Pillar: 1 (struct conformers) | GRDB query builder chain unlocked*

**Done.** `CollectConformances()` now includes non-frozen `StructDecl` alongside `ClassDecl`. All internal methods generalized from `ClassDecl` to `TypeDecl` (`TryInjectMethod`, `EmitSwiftWrapper`, `EmitClosureSwiftWrapper`, `BuildSyntheticMethodDecl`, `BuildClosureSyntheticMethodDecl`, `ResolveSelfElement`). Struct self uses `assumingMemoryBound(to: T.self).pointee` (value copy). Struct Self-return uses `UnsafeMutableRawPointer.allocate` + `initializeMemory` (buffer allocation, C# wraps in SafeHandle via `MarshalFromSwift<T>()`). Mutating methods: write-back (`self_.pointee = instance`) after call in all return paths (void, Self, class, existential). `IsMutating` tracked in `ProtocolExtensionMethodDecl`, parsed via `mutating func` detection in `ExtensionFuncRegex`. Class paths unchanged. GRDB +70 lines, RxSwift +838 lines (new struct conformer methods). 21 unit tests. 53/53 validation, 4877 unit tests passing.

Details: [Pillar 1 § P1.1](#session-p11-struct-conformers-in-protocol-extensions)

---

**A3: Closure Data Fixes + SimpleEnum + DynamicSelf Guard** | Direct | **COMPLETE**
*Pillar: 3 (closure data) + enum quality | ObjC closure methods + 176 enums fixed*

**Done.** Registered 11 missing ObjC Foundation types (`URLSessionTask`, `URLSessionDataTask`, `URLSessionDownloadTask`, `HTTPURLResponse`, `URLAuthenticationChallenge`, `URLSessionTaskMetrics`, `CachedURLResponse`, `InputStream`, `Progress`, `NSError`) and 2 UIKit types (`UIView`, `UIImageView`) in TypeDatabase XML with `objcBridged="true"`. Fixed `CanSafelyEmitAsSimpleEnum` to exclude synthesized Hashable/Equatable/RawRepresentable/CaseIterable/CodingKey members (`hashValue`, `rawValue`, `allCases`, `stringValue`, `intValue`, `_nsErrorDomain` properties and `hash(into:)` method) — 176 enums across validation libraries now emit as proper C# enums instead of heavyweight class-based types. Fixed `IsArrayOfString` crash on DynamicSelf return types (guard `!namedType.Name.Contains('.')`). 4 new/updated unit tests. 53/53 validation, 4879 unit tests passing.

Details: [Pillar 3 § P3.1](#session-p31-data-fixes-58-methods)

---

**A4: Nested Closure Spike** | ✅ COMPLETE
*Pillar: 3 (nested closures) | Spike proved, Alamofire onHTTPResponse + FlexLayout methods emitting*

Two-level closure bridge ABI proven via `NestedClosureBridge` emitter. Target: `Alamofire.DataRequest.onHTTPResponse(on:perform:)`. Outer closure C#→Swift (existing pattern), inner closure Swift→C# (new): decomposed into funcPtr+context at cdecl boundary, reconstructed as `Action<T>` in C#. Inner trampoline via `@convention(c)`, closure boxed via `Unmanaged.passRetained(as AnyObject)`. Spike succeeds — C2 applies proven pattern. Known spike limitation: one leaked retain count per outer invocation (C2 adds cleanup).

Details: [Pillar 3 § P3.3](#session-p33-nested-closures--class-params-41-methods)

---

### Phase B — New Infrastructure (after Phase A)

---

**B1: DependentMember Fix + ConformanceGraph** | ✅ COMPLETE
*Pillar: 1 (associated types) | ~24-32 GRDB cursor methods unlocked*

Fixed dead `DependentMember` code path in `SwiftABIParser.CreateTypeSpec` — ABI JSON `TypeNominal` nodes with `name="DependentMember"` now produce `AssociatedTypeReferenceSpec`. Built `ConformanceGraph` from TypeWitness entries in `HandleConformance()`, wired into `ResolveSelfElement` (protocol extensions) and `BoundGenericsHandler` (bound generic types). Three TypeWitness categories handled: concrete (Element → GRDB.Statement), generic forwarding (Element → τ_0_0), chained (Fetcher → τ_0_0.Fetcher → AnyType fallback). Ambiguity-safe resolution across multi-protocol conformances. Lifted `IsGeneric` guard for non-generic conformers. 19 new tests (4 parser, 7 graph CRUD, 8 resolution). Golden file updated (simple enum P/Invoke improvement as side effect). 53/53 validation.

Details: [Pillar 1 § P1.4](#session-p14-dependentmember-parsing-fix), [Pillar 1 § P1.2](#session-p12-conformancegraph-from-abi-json-typewitness-data)

---

**B2: Dispatch Table Refactor** | Done
*Pillar: 2 (dispatch architecture) | 0 methods (refactor)*

Replace 19-step `MethodHandler.Emit()` cascade with declarative `IMethodBridgeEmitter[]` dispatch table. Fix latent `WasEmitted` bug in 5 bridges. Must verify 5 ordering invariants hold under new `foreach` design — one wrong adapter ordering = silent emission bugs across all libraries.

Details: [Pillar 2 § P2.1](#session-p21-dispatch-table-with-imethodbridgeemitter)

---

**B3: Conditional Extension Constraints + Category 1 Docs** | Plan
*Pillar: 4 (conditional extensions) | ~10-15 GRDB fetch methods unlocked*

Parse `genericSig` from ABI JSON for constraints beyond parent type. Emit method-level `where` clauses. Need to explore how `GenericTypeEmitter` currently works and whether method-level clauses interact with existing constraint propagation. Also document ~5 methods with primitive/ObjC type arguments as known limitations (P4.3 — 15 min add-on).

Details: [Pillar 4 § P4.2](#session-p42-conditional-extension-constraint-propagation-10-15-methods), [Pillar 4 § P4.3](#session-p43-document-category-1-limitation)

---

### Phase C — Bridge Extensions (after Phase B)

---

**C1: Post-Processors + Multi-Closure Bridge** | Direct
*Pillars: 2 (post-processors) + 3 (multi-closure) | ~14-18 methods unlocked*

Wrap 4 post-processors in `IMethodPostProcessor` (mechanical — follows B2 pattern). Then extend `MethodClosureBridge` to handle N closure params (N callback+funcPtr pairs). Lift B7 gate for `Optional<String>` and `[String]` closure returns.

Details: [Pillar 2 § P2.2](#session-p22-imethodpostprocessor--constructorhandler-sharing), [Pillar 3 § P3.2](#session-p32-multi-closure--return-type-extensions-18-methods)

---

**C2: Nested Closures + Class Params (full)** | Direct (A4 spike succeeded)
*Pillar: 3 (nested closures) | ~26-41 methods unlocked*

Apply the two-level bridge ABI proven in A4 across all nested-closure methods. Extend `IsInvocableParameter` for class types. Add inner closure lifetime cleanup (release mechanism for `passRetained` leak). Extend to Optional<ref> args, multiple nested closures, non-void inner returns.

Details: [Pillar 3 § P3.3](#session-p33-nested-closures--class-params-41-methods)

---

### Phase D — Complex Enums (after Phase C)

---

**D1: Complex Enum Closure Bridge** | Plan
*Pillar: 3 (complex enums) | ~17 methods unlocked*

New territory: Swift wrapper deconstructs enum (tag + payload bytes as separate cdecl args), C# callback reconstructs discriminated union. Tag+payload encoding scheme and C# `readonly struct` representation need design before coding.

Details: [Pillar 3 § P3.4](#session-p34-complex-enum-bridge-17-methods)

---

### Deferred (post-launch)

- **Architectural generic closures** (P3.5): 45 methods, needs design. RxSwift highest priority.
- See [Pillar 3 § P3.5](#session-p35-architectural-generics-45-methods-deferred) for library criticality breakdown.

---

### Summary

```
Phase A:  A1 ─── A2 ─── A3 ─── A4          (4 sessions, independent)
Phase B:  B1 ─── B2 ─── B3                  (3 sessions, after A)
Phase C:  C1 ─── C2                          (2 sessions, after B)
Phase D:  D1                                 (1 session, after C)
                                             ─────────────────────
                                              10 sessions total
```

| Phase | Sessions | Methods Unlocked | Direct | Plan |
|:-----:|:--------:|:----------------:|:------:|:----:|
| A | 4 | ~100-105 | 3 | 1 |
| B | 3 | ~34-47 | 0 | 3 |
| C | 2 | ~40-59 | 2 | 0 |
| D | 1 | ~17 | 0 | 1 |
| **Total** | **10** | **~190-230** | **5** | **5** |

Note: The remaining ~230-250 methods from the original ~420-480 estimate come from feature roadmap sessions (F1-F4) which run in the parallel lane.

---

### Feature Roadmap Session Modes

These run in the parallel lane (two-lane policy). See `feature-roadmap.md` for full details.

| Session | Mode | Rationale |
|---------|:----:|-----------|
| F1 (nint overloads) | **Direct** | Extending existing `NativeIntOverloadEmitter`. Patterns established. |
| F2 (noise reduction) | **Direct** | Three independent fixes, each self-contained. |
| F3 (param gate lifts) | **Direct** | Four gate lifts, each reusing existing patterns. |
| F4 (collection dispatch) | **Plan** | New dispatch kinds, boxing design needs verification. |
| F5 (string enum raw values) | **Direct** | Swiftinterface parsing, clear integration point. |
| F6 (safety) | **Direct** | Four independent hardening tasks at known locations. |
| F7 (runtime metadata) | **Plan** | New runtime infrastructure, ABI verification needed. |

---

## Validation Criteria

Each pillar is "done" when:

1. **Type Resolution**: Zero `AnyType` returns from protocol extension methods where the concrete type is known. ConformanceGraph resolves all concrete and generic-forwarding TypeWitness entries. DynamicSelf has explicit guards.

2. **Dispatch Architecture**: `MethodHandler.Emit()` uses `foreach` over `IMethodBridgeEmitter[]`. All bridge emitters set `WasEmitted`. Adding a new bridge emitter is "create class, add to array."

3. **Closure Bridge**: ObjC Foundation types resolvable. Multi-closure methods emit. Plain enums not blocked. Remaining gaps (nested closures, complex enums) have clear implementation paths, not ad-hoc workarounds.

4. **Generic Constraints**: `SatisfiesConstraint` checks real conformance data. Conditional extension constraints propagated. Known limitations documented.

---

## Pre-Launch: Compatibility Contract Checklist

Not a foundation pillar, but must be addressed before going public. Planned as a standalone session after pillar work stabilizes.

- **Regeneration stability**: Same xcframework + same generator version → identical output. Add golden-file regression suite for 3-5 representative libraries (beyond current single-file golden test).
- **Swift/Xcode/.NET version matrix**: Document tested combinations (Swift 5.9/6.0, Xcode 15/16, .NET 10). CI runs at least 2 Swift versions.
- **Breakage policy**: Define what constitutes a breaking change in generated output (new methods = OK, renamed methods = breaking, reordered P/Invoke = breaking if users take ABI dependency).
- **SB0003 stability**: Ensure SB0003 set only shrinks across generator versions (never re-skip a previously-emitted method).

---

## What This Roadmap Does NOT Cover

- Score optimization (nint overloads, noise reduction, etc.) → see Feature Roadmap
- Runtime safety (Dispose, finalizer, Roslyn analyzer) → see Feature Roadmap
- Runtime metadata introspection (A2) → see Feature Roadmap
- SwiftUI bridge work → separate roadmap
- Production readiness (versioning, CI, docs) → separate roadmap
