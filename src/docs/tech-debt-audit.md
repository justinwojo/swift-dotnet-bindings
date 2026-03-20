# Tech Debt Audit: Sessions 1-14 Cleanup

**Date**: March 19, 2026
**Scope**: All generator, runtime, and emitter changes from Session 1 (NativeAOT investigation) through Session 14 (generic struct constructors, async optional/typed-throws)
**Commits analyzed**: `9fd1c1b9..e9caf3c9` (~20 commits)
**Files changed**: 65 generator files (+5,386/-687 lines), 14 runtime files (+672/-187 lines)

---

## Executive Summary

The 14 sessions successfully brought runtime tests from ~550 passing to 663 passing (simulator) / 661 (device), with 0 failures on both platforms. This was achieved through incremental, session-by-session fixes that each addressed specific test failures. The result is **functionally correct and well-tested code**, but with predictable tech debt from solving similar problems independently across sessions.

The audit identified **6 major areas** of accumulated debt, prioritized by impact:

| Priority | Area | Estimated Duplicated/Scattered Lines | Risk if Unfixed |
|----------|------|--------------------------------------|-----------------|
| P1 | Wrapper emitter cross-cutting duplication | 1,500-2,000 | Bug fixes applied in one emitter but not others |
| P2 | Optional handling scattered across 14+ files | 800-1,000 | Inconsistent behavior across Optional scenarios |
| P2 | Reflection & NativeAOT trimming safety | N/A (risk-based) | Silent crashes on device from stripped reflection |
| P3 | Generic/protocol dispatch — 3 parallel strategies | 400-600 | New dispatch types require 4-file changes |
| P4 | WrapperValidation decision complexity | 200-300 | Size/field thresholds scattered, hard to update |
| P5 | Runtime workaround organization | 100-150 | Redundant caches, unclear workaround lifecycle |

**No critical bugs found.** All workarounds are intentional and well-motivated. The debt is primarily organizational, with one exception: the SwiftDictionary/SwiftSet witness table lookup uses unconstrained reflection that could crash on NativeAOT device for non-pre-registered types (Finding 6.1).

---

## Area 1: Wrapper Emitter Cross-Cutting Duplication (P1)

### Problem

Method, Property, Constructor, and Operator wrapper emitters each independently implement the same patterns for emitting Swift @_cdecl wrappers and C# P/Invoke code. These grew independently across sessions 6-14 as each fixed specific test failures.

### Finding 1.1: Self Reconstruction — 4+ Implementations

The pattern for converting C# pointers back to Swift objects appears in 4+ files with minor variations:

**Class reconstruction** (identical logic in each file):
```swift
let obj = Unmanaged<ClassName>.fromOpaque(self_).takeUnretainedValue()
```

**Struct reconstruction** (identical logic, but `let` vs `var` inconsistency):
```swift
// PropertyWrapperEmitter, MethodWrapperEmitter — always "let"
let obj = self_.assumingMemoryBound(to: ClassName.self).pointee
// ConstructorWrapperEmitter — uses "var" for setters
var obj = self_.assumingMemoryBound(to: ClassName.self).pointee
```

| File | Location | Pattern |
|------|----------|---------|
| `PropertyWrapperEmitter.cs` | `EmitSelfReconstruction` (~line 682) | Shared helper, class + struct |
| `MethodWrapperEmitter.cs` | Inline (~line 455) | Duplicated inline |
| `ConstructorWrapperEmitter.cs` | Inside `GetCdeclParamMapping` (~line 800+) | Embedded in monolith |
| `OperatorHandler.cs` | Reference type detection (~line 161) | Yet another variant |

**Recommendation**: Extract `SelfReconstructionEmitter` utility with `EmitClassReconstruction()`, `EmitStructReconstruction()`, `EmitProtocolCastReconstruction()` methods. ~50-100 lines eliminated.

### Finding 1.2: Marshalling Sequence — 3 Orderings of Same Operations

`WrapperEmitter.cs` contains three nearly-identical marshalling sequences for constructor, ObjC constructor, and method emission:

```
EmitSwiftSelf → EmitIndirectResult → EmitGenericArguments → EmitBoundGenericArguments
→ EmitClosureMarshalling → EmitTypeConversions → EmitCdeclFrozenStructMarshalling
→ EmitProtocolWitnessTables → EmitPInvokeCall → EmitGenericInoutWriteback
→ EmitSwiftError → EmitReturn
```

Differences between the three sequences:
- Constructor guards generic args in an `if` block; method always emits
- Constructor has no `EmitOptionalReturnBuffer`; method does
- ObjC constructor skips `EmitSwiftSelf`, reorders `EmitBoundGenericArguments`

If a new marshalling pass is needed, it must be added in 3+ places.

**Recommendation**: Extract a `MarshalSequencer` that takes a configuration object (isConstructor, isObjC, hasOptionalReturn) and produces the correct ordering. ~100-150 lines consolidated.

### Finding 1.3: CdeclParamMapping — 1,000+ Line Monolith

`ConstructorWrapperEmitter.GetCdeclParamMapping()` is a single method handling C-to-Swift parameter conversion for ALL type categories: bound generics, Foundation.Date/Data, String, classes, protocols, simple enums, complex enums, non-frozen structs, frozen structs (system vs custom).

It's called from Method, Property, and Constructor emitters — serving as a cross-emitter dependency without being in a shared utility class.

**Recommendation**: Break into type-specific strategy handlers (one per category). Each handler implements `(cdeclParam, reconstruction, callArg)` for its type. ~1,000 lines reorganized into ~10 focused handlers of ~100 lines each.

### Finding 1.4: Generic Protocol Emission — Duplicated Across Emitters

Both `PropertyWrapperEmitter` and `MethodWrapperEmitter` have their own `EmitGenericClassProtocolAndConformance()` methods (~line 392 and ~line 1392) with identical code. Both emit:

```swift
private protocol _SBW_P_{hash} {
    func/var signature
}
extension ModuleQualifiedName: _SBW_P_{hash} {}
```

The protocol naming varies without clear semantic reason: `_SBW_PG_`, `_SBW_P_`, `_SBW_GSPG_`, `_SBW_GSM_`, `_SBW_CI_`, `_SBW_GSF_` — 6+ prefixes for the same concept.

**Recommendation**: Extract `GenericProtocolEmitter` utility with unified naming scheme. ~400 lines consolidated.

### Finding 1.5: @MainActor Annotation — Identical 15-Line Blocks

`MethodWrapperEmitter` (~line 414) and `PropertyWrapperEmitter` (~line 288) have identical blocks for @MainActor annotation emission. Likely also in `ConstructorWrapperEmitter`.

**Recommendation**: Extract `EmitCdeclAnnotation(needsMainActor, symbolName)` helper. ~30-45 lines eliminated.

### Finding 1.6: String Return Handling — 3 Independent Implementations

SBW_Utf8Slice (pointer + length) return pattern appears in:
- `PropertyWrapperEmitter` (~line 698): `EmitStringGetterBody` (Swift side, 35 lines)
- `MethodWrapperEmitter` (~line 479): Inline delegation
- `WrapperEmitter.Return.cs` (~line 99): C# side unmarshalling (18 lines)

**Recommendation**: Extract `StringReturnEmitter` for Swift side, keep C# side in `WrapperEmitter.Return`. ~40-50 lines consolidated.

### Finding 1.7: Error Infrastructure — Scattered Setup

Error P/Invoke declarations (`SBW_GetErrorDescription`, `SBW_ReleaseError`, `SBW_Free`, `SBW_ExtractTypedError_*`) are emitted in `WrapperEmitter.Marshalling` but consumed across all emitters.

**Recommendation**: Extract `ErrorInfrastructureEmitter` with a clear public API. ~150 lines reorganized.

### Finding 1.8: Return Value Handling — Multiple Strategies, No Abstraction

Each emitter has independent return handling:
- Methods: Direct / IndirectResult / String / Optional / Projection-based
- Properties: String getter / Decomposed Optional / Direct getter
- Constructors: Class pointer / SafeHandle wrapper / Direct struct

`CdeclReturnKind` enum exists in `PropertyWrapperEmitter` (7 variants) but isn't shared.

**Recommendation**: Promote `CdeclReturnKind` to a shared enum. Consolidate return emission to a single `EmitReturnMarshalling()` dispatcher. ~150-200 lines consolidated.

---

## Area 2: Optional Handling Scattered Across 14+ Files (P2)

### Problem

Sessions 8-14 each addressed different Optional scenarios (tag byte fast paths, decomposed parameters, large optional pointer widening, VWT bypass). Each fix was correct in isolation but the overall Optional handling is now spread across 14+ files with multiple strategies.

### Finding 2.1: Tag Byte Computation — Runtime and Emitter Disagree

Two independent implementations compute Optional tag byte offsets:

| Implementation | Location | Approach |
|----------------|----------|----------|
| Runtime | `SwiftOptional.GetTagByteOffset()` + `GetBlittablePrimitiveTagOffset()` | Checks 10 primitive types + size comparison fallback |
| Emitter | `OptionalProjection.GetBlittablePrimitiveSizePublic()` | Hardcoded sizes, NO size comparison fallback |

If a new blittable type is added to Swift ABI, only one path gets updated.

**Recommendation**: Single source of truth for tag byte offset computation, shared between runtime and emitter.

### Finding 2.2: VWT Bypass — 6 Independent Fast Paths

VWT operations are bypassed in 6 separate locations:

| Location | Operation Bypassed |
|----------|-------------------|
| `SwiftOptional.NewSome()` | VWT `DestructiveInjectEnumTag` |
| `SwiftOptional.NewNone()` | VWT `DestructiveInjectEnumTag` |
| `SwiftOptional.Case` (getter) | VWT `GetEnumTag` |
| `OptionalProjection` (return path) | Discriminator byte read |
| `WrapperEmitter.Return.cs` (blittable fast path) | Discriminator byte read |
| `WrapperEmitter.Return.cs` (decomposed) | hasValue byte read |

All 6 follow the same pattern: check if tag byte offset is available, read/write byte directly, skip VWT. Each independently handles the "tag byte not available" fallback.

**Recommendation**: Consolidate into a `OptionalTagByteHelper` with `TryReadTag()` / `TryWriteTag()` that all paths call.

### Finding 2.3: Optional<reference> Detection — Two Paths That Can Diverge

`WrapperValidation.IsOptionalWithReferenceInner()` has two detection paths:
- **Path 1**: TypeRecord lookup for Kind=Class, ObjCBridged, ObjCRooted
- **Path 2**: Fallback heuristic for unresolved Apple framework types (module set + prefix detection)

An ObjC-bridged struct could match Path 2 (prefix heuristic) but not Path 1 (TypeRecord says Struct). Different emitters may see different answers depending on which path they exercise.

**Recommendation**: Eliminate Path 2 heuristic if possible, or document precisely when it fires and add tests for divergence cases.

### Finding 2.4: Four Overlapping Optional Handling Strategies

| Strategy | When Used | Added In |
|----------|-----------|----------|
| **Nullable pointer** | `Optional<class>`, `Optional<ObjC>` — nil = IntPtr.Zero | Original |
| **Decomposed (payload + hasValue)** | `Optional<complex-enum>`, `Optional<non-frozen-struct>` — VWT crashes | Session 11 |
| **Large Optional pointer widening** | `Optional<T>` where size > 8 bytes — truncation prevention | Session 13 |
| **Blittable fast path** | `Optional<primitive>` — tag byte read, no VWT | Session 8-10 |

These strategies interact but are checked independently:
- `PropertyWrapperEmitter` checks decomposed **first**, ignoring large
- `MethodWrapperEmitter` checks large **first**, ignoring decomposed
- For `Optional<non-frozen-String>` (16+ bytes, SafeHandle payload) — **both apply** but which wins depends on which emitter runs

**Recommendation**: Create `OptionalMarshalStrategy` enum that classifies each Optional into exactly one strategy. Single decision point that all emitters consult:

```csharp
public enum OptionalMarshalStrategy
{
    NullablePointer,         // Class/ObjC inner — IntPtr.Zero = None
    DecomposedBuffers,       // Complex enum/non-frozen struct — (payload, hasValue)
    LargeOptionalPointer,    // > 8 bytes — UnsafeRawPointer transport
    BlittableFastPath,       // Primitive inner — direct tag byte
    FullSwiftOptional        // General case — SwiftOptional<T> with VWT
}
```

### Finding 2.5: Decomposed Optional Reconstruction — Inconsistent Syntax

Three different patterns for the same boolean flag:

| Location | Pattern |
|----------|---------|
| `PropertyWrapperEmitter` (getter) | `hasValuePtr.storeBytes(of: Int8(1), as: Int8.self)` |
| `PropertyWrapperEmitter` (setter) | `if _hasValue == 0 { ... }` — reads as byte, no cast |
| `WrapperEmitter.Return.cs` | `byte _hasValue = ((byte*)hasValuePtr)[0]` — C# syntax |

**Recommendation**: Standardize on a single naming convention (`hasValue` / `_hasValue`) and access pattern.

### Finding 2.6: Large Optional Detection — Hardcoded Small Types List

`BoundGenericsHandler.IsLargeOptionalParam()` hardcodes a "small" types list:
```csharp
"Swift.Bool", "Swift.Int8", "Swift.Int16", "Swift.Int32", "Swift.Float"
```

Missing: `UnicodeScalar`, custom user enums with single raw values. Conservative fallback (assume large) is safe but over-allocates buffers.

**Recommendation**: Derive "small" from `TypeRecord.InlineSize` when available rather than hardcoding type names.

---

## Area 3: Generic & Protocol Dispatch — 3 Parallel Strategies (P3)

### Problem

Methods, Properties, and Constructors each have independent generic dispatch implementations that share the same concepts (protocol-based type erasure, metatype helpers, metadata parameter passing) but implement them separately.

### Finding 3.1: Three Dispatch Strategies Instead of One

| Strategy | Used By | Protocol Prefix | Metadata Param Order |
|----------|---------|-----------------|---------------------|
| Generic Static Dispatch | Methods | `_SBW_P_`, `_SBW_GSM_` | After self |
| Generic Static Factory | Constructors | `_SBW_GSF_`, `_SBW_CI_` | N/A (no self) |
| Generic Static Getter/Setter | Properties | `_SBW_GSPG_`, `_SBW_GSPS_` | Before self |

All three use protocol-based type erasure with identical structure:
1. Define private protocol with method signature
2. Extend concrete type to conform to protocol
3. Cast metadata to protocol metatype, dispatch

But each emitter builds this independently with different helper methods.

**Recommendation**: Extract `GenericDispatchEmitter` with a configuration object that captures the differences (protocol prefix, param order, accessor kind). Single implementation, three configurations.

### Finding 3.2: Metatype Helpers — Single Emission, Multiple Consumers

`ConstructorWrapperEmitter.EmitMetadataAccessorHelperIfNeeded()` is the sole emission point for `_sbw_meta_*` helpers. It's called from Method, Property, and Constructor emitters as a cross-emitter dependency.

This makes `ConstructorWrapperEmitter` a de facto central dependency — conceptually wrong (metatype helpers are not constructor-specific).

**Recommendation**: Move metatype helper emission to a shared `MetatypeHelperEmitter` utility class. Keep deduplication in `ModuleEmissionContext`.

### Finding 3.3: Protocol Cast Patterns — 21 Instances, 8 Files, No Helper

The `Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocol}` pattern appears 21 times across 8 files with no shared helper:

- `PropertyWrapperEmitter` (1 instance)
- `SubscriptWrapperEmitter` (2 instances)
- `MethodWrapperEmitter` (1 instance)
- `ClassHandler` (2 instances — casts to specific class, not protocol)
- `NestedClosureBridge` (3 instances — conditional logic based on type)
- `ForeignTypeExtensionEmitter` (4 instances)
- `ProtocolExtensionEmitter` (3 instances)
- `ErrorDescriptionEmitter` (2 instances)

**Recommendation**: Extract `SwiftObjectCast.EmitProtocolCast()`, `SwiftObjectCast.EmitClassCast()`, `SwiftObjectCast.EmitStructDereference()` helpers.

### Finding 3.4: Guard Condition Divergence

Each emitter has slightly different logic for deciding when generic dispatch is needed:

```csharp
// Methods
isGenericParent && !isStatic && NeedsGenericStaticDispatch(env, parentTypeDecl)

// Properties
(isGenericParent && !isGenericClassParent) || (isGenericClassParent && propertyReferencesT)

// Constructors
isGenericParent && !isStatic && NeedsGenericStaticDispatch(env, parentTypeDecl)
```

Methods and Constructors use the same guard. Properties have a different, more complex condition. These must be kept in sync manually.

**Recommendation**: Centralize in `WrapperValidation.NeedsGenericDispatch(env, memberKind)` with a `MemberKind` enum (Method, Property, Constructor).

### Finding 3.5: InheritedGenericContext — Correct but Isolated

Session 14 added `IsInheritedGenericContext()` in `WrapperValidation.cs` for nested types whose generic params come from an outer parent. It's only used in constructor validation.

Question: Should Method and Property validation also check this? If a nested type like `AuthenticationInterceptor<A>.RefreshWindow` has methods/properties, the same issue applies.

**Recommendation**: Audit whether `IsInheritedGenericContext` should gate generic dispatch for all member types, not just constructors.

### Finding 3.6: Module Initializer — String Replacement Hack for Nested Conformances

`ModuleEmissionContext.RecordConformance()` qualifies nested type conformances via string replacement:

```csharp
qualifiedProtocol = qualifiedProtocol.Replace($"<{csharpTypeName}>", $"<{qualifiedName}>");
```

This works but is fragile — if the protocol string format changes, the replacement silently fails.

**Recommendation**: Use structured type references instead of string manipulation. Track (outerType, innerType, protocol) as a structured record.

---

## Area 4: WrapperValidation Decision Complexity (P4)

### Problem

`WrapperValidation.cs` grew from a simple check to 1,083 lines with 50+ decision points across 3 layers (emission eligibility, wrapper eligibility, ABI safety). While architecturally sound, several patterns are duplicated or scattered.

### Finding 4.1: Float/Bool Field Checks — Verbatim Duplication

Identical checks in two sub-methods:

```csharp
// In IsSelfTypeCdeclRequired() ~line 877
if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasFloatFields))
    return true;
if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasBoolFields))
    return true;

// In IsParamTypeCdeclRequired() ~line 950
if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasFloatFields))
    return true;
if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasBoolFields))
    return true;
```

**Recommendation**: Extract `HasIncompatibleFields(TypeRecord)` helper. Trivial change, eliminates verbatim duplication.

### Finding 4.2: Size Thresholds — Scattered and Implicit

ABI safety size limits are hardcoded inline across multiple methods:

| Context | Threshold | Location |
|---------|-----------|----------|
| Self parameter | > 8 bytes | `IsSelfTypeCdeclRequired` ~line 888 |
| Regular parameter | > 16 bytes | `IsParamTypeCdeclRequired` ~line 958 |
| System type return | > 8 bytes | Various locations |
| C-bridged types | No limit | `IsCBridgingModuleType` check |

**Recommendation**: Create `AbiSizeLimits` constants class:

```csharp
private static class AbiSizeLimits
{
    public const int MaxSelfSize = 8;    // SwiftSelf<T> multi-register limit
    public const int MaxParamSize = 16;  // Parameter passing ABI limit
    public const int MaxSystemReturnSize = 8;
}
```

### Finding 4.3: Optional Type Classification — 3 Functions, 4+ Comment Sites

Three separate predicates for Optional classification:
- `IsOptionalType()` — bare presence check
- `IsOptionalSupportedForCdecl()` — wrapper eligibility
- `IsOptionalWithReferenceInner()` — ABI safety dispatch

The 3-category split (reference, value-type, existential) appears in comments in 4+ locations but isn't expressed as code.

**Recommendation**: Create `OptionalCategory` enum (see Area 2 recommendations). Replace predicate chain with single `ClassifyOptional()` call.

### Finding 4.4: ShouldEmitWrapper — 27 Sequential Guards

`MethodWrapperEmitter.ShouldEmitWrapper()` has 27 sequential guards. Guards 1-15 check method properties (constructor, accessor, actor, async). Guards 16-27 check type signatures (metatype, opaque, DynamicSelf).

These two groups are independent. The type signature checks could be extracted to `HasUnsupportedTypeSignature()` without changing behavior.

**Recommendation**: Extract type-signature guards to a helper method. Reduces ShouldEmitWrapper to ~15 focused guards + 1 delegation call.

### Finding 4.5: Implicit AND Gate at Call Sites

All wrapper decision call sites use the same pattern:

```csharp
if (MethodWrapperEmitter.ShouldEmitWrapper(env) &&
    WrapperValidation.RequiresCdeclForAbiSafety(env))
```

The three states (can't wrap, doesn't need wrapping, needs wrapping) are implicit.

**Recommendation**: Create `WrapperDecision` enum:

```csharp
public enum WrapperDecision { CannotWrap, NoWrapperNeeded, WrapperRequired }
```

Single `DetermineWrapperDecision()` method replaces the AND gate.

### Finding 4.6: Type Classification Rules Scattered Across 3+ Files

Frozen struct classification appears in:
- `WrapperValidation.cs` — `IsCBridgingModuleType()`
- `ConstructorWrapperEmitter.cs` — `IsSystemFrozenStruct()`
- `MarshallingHelpers.cs` — `IsTypeFrozen()`

No single registry for "what frozen struct types are safe?"

**Recommendation**: Create `TypeAbiClassifier` static class that consolidates all type classification logic. Returns an enum (`Safe`, `IncompatibleFields`, `TooLargeForSelf`, `TooLargeForParam`) instead of spreading boolean checks.

---

## Area 5: Runtime Workaround Organization (P5)

### Problem

Runtime changes across sessions 8-14 introduced workarounds for Mono and NativeAOT issues. The workarounds are correct and well-commented, but organizational improvements would help.

### Finding 5.1: Runtime Detection — 3 Redundant Caches

Three separate static caches for `IsMonoRuntime`:

| File | Field | Source |
|------|-------|--------|
| `SwiftRuntimeInfo.cs` | `IsMonoRuntime` (property) | Primary — `Type.GetType()` + `RuntimeIdentifier` fallback |
| `SwiftSafeHandle.cs` | `s_isMonoRuntime` (static field) | Caches `SwiftRuntimeInfo.IsMonoRuntime` |
| `SwiftDispose.cs` | `s_isMonoRuntime` (static field) | Caches `SwiftRuntimeInfo.IsMonoRuntime` |

**Recommendation**: Remove redundant caches. `SwiftSafeHandle` and `SwiftDispose` should reference `SwiftRuntimeInfo.IsMonoRuntime` directly. The JIT will inline the property access.

### Finding 5.2: SwiftSafeHandle.ReleaseHandle — 5 Nested If Statements

`ReleaseHandle()` has 3 distinct code paths interleaved with 5 levels of nesting:
1. Mono finalizer → skip ALL P/Invoke (accepts memory leak on simulator)
2. Process exit → skip cleanup (SwiftExitGuard coordination)
3. Normal release → VWT Destroy + NativeMemory.Free

**Recommendation**: Extract into separate methods: `HandleMonoFinalizerCleanup()`, `HandleProcessExitCleanup()`, `HandleNormalRelease()`. Reduces nesting, clarifies intent, allows JIT to optimize each path.

### Finding 5.3: RegisterDestroyAction — Dead Backward-Compatibility Shim

```csharp
public static void RegisterDestroyAction(Action<IntPtr>? action)
{
    // No-op for backward compatibility.
}
```

Session 12 proved VWT Destroy via CallConvSwift is safe, making this dead code. Kept for pre-Session-12 binding compatibility.

**Recommendation**: Mark with `[Obsolete("No longer needed — VWT Destroy is safe via CallConvSwift", false)]`.

### Finding 5.4: SwiftOptional Tag Byte — 3 Call Sites With Identical Null Checks

`NewSome()`, `NewNone()`, and `Case` getter all follow the same pattern:

```csharp
int tagOffset = GetTagByteOffset();
if (tagOffset >= 0) { /* fast path */ }
else { /* VWT fallback */ }
```

The null/negative check is repeated 3 times with near-identical comments.

**Recommendation**: Extract `TryFastPathTagOperation()` helper, or at minimum deduplicate the comments.

### Finding 5.5: EveryProtocol — Double-Check Locking Not Thread-Safe

```csharp
public static TypeMetadata GetTypeMetadata()
{
    if (_typeMetadataHandle != IntPtr.Zero)
        return TypeMetadata.Cache.GetOrAdd(...);
    return default;
}

public static void SetTypeMetadata(IntPtr handle)
{
    lock (_metadataLock)
    {
        if (_typeMetadataHandle == IntPtr.Zero)
            _typeMetadataHandle = handle;
    }
}
```

The read in `GetTypeMetadata()` is outside the lock. In practice, this is called during static initialization so the race window is narrow, but it violates best practices.

**Recommendation**: Use `Lazy<TypeMetadata>` or add `volatile` to `_typeMetadataHandle`.

### Finding 5.6: Collection Lazy Init Inconsistency

`SwiftArray` and `SwiftSet` use lazy metadata initialization to prevent Mono JIT crashes during `.cctor`. `SwiftDictionary` uses eager static properties.

**Recommendation**: Align `SwiftDictionary` to use lazy initialization like `SwiftArray`/`SwiftSet`.

---

## Session Plan

All work is organized into 3 sessions. Each session is designed to be completable in a single Claude Code conversation with agent team parallelism. Sessions must run in order — each builds on the previous.

### Session 1: Quick Wins + Runtime Hardening ✅ COMPLETE

**Date completed**: March 19, 2026
**Commit**: `tech-debt-session-1` branch

**Scope**: All mechanical/low-risk changes plus reflection safety fixes. High parallelism — most items touch different files.

**Agent team structure**: 3 parallel agents

| Agent | Items | Files Touched |
|-------|-------|---------------|
| **Agent A: Validation & Emitter Helpers** | Extract `HasIncompatibleFields()` helper; Create `AbiSizeLimits` constants; Extract `EmitCdeclAnnotation()` helper; `WrapperDecision` enum + centralized decision method; Extract type-signature guards from `ShouldEmitWrapper` | WrapperValidation.cs, MethodHandler.cs, PropertyHandler.cs, DefaultParameterOverloadEmitter.cs, Method/Property/ConstructorWrapperEmitter, new WrapperEmitterHelpers.cs |
| **Agent B: Runtime Cleanup** | Remove redundant `s_isMonoRuntime` caches; Document `RegisterDestroyAction` as no-op shim; Fix EveryProtocol double-check locking; Align SwiftDictionary lazy init; Extract `ReleaseHandle` into 3 separate methods | SwiftHandle.cs, SwiftDispose.cs, EveryProtocol.cs, SwiftDictionary.cs |
| **Agent C: Reflection Safety** | Add `[DynamicallyAccessedMembers]` to reflection helpers; Add ExistentialContainer2-8 to TrimmerRoots.xml; Witness table pre-registration registry (eliminate MakeGenericType in SwiftDictionary/SwiftSet); Pre-registration completeness test | ISwiftObject.cs, TrimmerRoots.xml, SwiftMarshal.cs, ProtocolWitnessTable.cs, ModuleHandler.cs, unit tests |

**Validation gates**: All green
- `run-tests.sh`: 7994 passed, 0 failed
- `validate-libraries.sh`: 90/90 passed, no regressions
- `run-runtime-tests.sh --skip-regen`: 663 passed, 31 skipped, 0 failures

**Changes**: 24 files, +1685/-188 lines. 25 new tests (18 WrapperConsistencyTests, 4 ModuleHandler emission tests, 3 ProtocolWitnessTable registration tests).

**Post-completion review corrections**:
- `[DynamicallyAccessedMembers]` changed from `PublicMethods` to `PublicMethods | NonPublicMethods` — the reflection helpers use `BindingFlags.NonPublic` to find explicit static interface implementations, so preserving only public methods was insufficient for real NativeAOT consumers
- `[Obsolete]` removed from `RegisterDestroyAction` — older generated bindings call this from `[ModuleInitializer]` and would break under `TreatWarningsAsErrors`. The doc comment documents the no-op status without introducing a build-breaking warning

---

### Session 2: Shared Emitter Utilities + Optional Unification

**Scope**: Extract cross-cutting emitter patterns into shared utilities. Unify Optional handling. This is the highest-value session — eliminates the most duplication and the biggest source of "fix it in one emitter, forget the others" risk.

**Agent team structure**: 3 parallel agents (files partition cleanly)

| Agent | Items | Files Touched |
|-------|-------|---------------|
| **Agent A: Swift Wrapper Helpers** | `SelfReconstructionEmitter` utility (class/struct/protocol cast — 4+ files); `StringReturnEmitter` utility; Move metatype helper emission from ConstructorWrapperEmitter to shared `MetatypeHelperEmitter` | New shared utility files, Method/Property/Constructor/SubscriptWrapperEmitter, OperatorHandler |
| **Agent B: Optional Strategy** | `OptionalMarshalStrategy` enum + single classifier; Consolidate Optional tag byte computation (runtime + emitter → single source of truth); Standardize decomposed Optional naming/access patterns | SwiftOptional.cs, OptionalProjection.cs, WrapperValidation.cs, WrapperEmitter.Return.cs, WrapperEmitter.Marshalling.cs, BoundGenericsHandler.cs, PropertyWrapperEmitter.cs |
| **Agent C: Generic Protocol Unification** | Shared `GenericProtocolEmitter` (deduplicate emission across Method/Property); Centralize generic dispatch guard logic in `WrapperValidation.NeedsGenericDispatch()`; Audit `IsInheritedGenericContext` applicability beyond constructors | Property/MethodWrapperEmitter, WrapperValidation.cs, ModuleEmissionContext.cs |

**Validation gates**: `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` (full rebuild — emitter output may change)

**Risk**: Medium. Emitter output changes require full rebuild validation. Each agent's work is file-isolated, but the combined effect needs end-to-end testing.

---

### Session 3: Emitter Architecture

**Scope**: The large structural refactoring items. These change how the emitter is organized internally. Depends on Session 2's shared utilities being in place.

**Agent team structure**: 2 parallel agents (items have more interdependencies)

| Agent | Items | Files Touched |
|-------|-------|---------------|
| **Agent A: Parameter & Return Marshalling** | Break `GetCdeclParamMapping` into type-specific strategy handlers; Unified return value dispatcher (promote `CdeclReturnKind` to shared enum); `MarshalSequencer` for constructor/method/ObjC ordering | ConstructorWrapperEmitter (major refactor), WrapperEmitter.cs, WrapperEmitter.Return.cs, new strategy handler files |
| **Agent B: Coordination & Infrastructure** | `ErrorInfrastructureEmitter` (consolidate error P/Invoke setup); `ClosureCallbackOrchestrator` (unify closure marshalling across Property/Method); Unified `GenericDispatchEmitter` (3 strategies → 1 configurable, building on Session 2's GenericProtocolEmitter) | WrapperEmitter.Marshalling.cs, ClosureEmitter.cs, Method/Property/ConstructorWrapperEmitter, new coordinator files |

**Validation gates**: `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` (full rebuild — major emitter changes)

**Risk**: Higher. `GetCdeclParamMapping` breakup is the single largest change (~1,000 lines reorganized). GenericDispatchEmitter touches all 4 wrapper emitters. Run `build-and-test.sh` after each agent completes, not just at the end.

**Note**: The "wrapper emitter base class" item from Phase 4 is deliberately excluded. After Sessions 1-3, reassess whether it's still needed — the shared utilities may provide enough structure without a class hierarchy.

---

### Session Summary

| Session | Items | Agent Parallelism | Risk | Key Validation | Status |
|---------|------:|:-----------------:|------|----------------|--------|
| 1 | 14 | 3 agents | Low | `run-tests.sh` + `validate-libraries.sh` + `run-runtime-tests.sh --skip-regen` | **Done** |
| 2 | 9 | 3 agents | Medium | `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` | Pending |
| 3 | 5 | 2 agents | Higher | `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` (per-agent) | Pending |
| **Total** | **28** | | | | |

### Dependencies Between Sessions

```
Session 1 ──→ Session 2 ──→ Session 3
              │                │
              │ Session 2 creates shared utilities
              │ that Session 3 builds on:
              │ - MetatypeHelperEmitter
              │ - GenericProtocolEmitter
              │ - OptionalMarshalStrategy
              │ - SelfReconstructionEmitter
              └────────────────┘
```

Session 1 has no prerequisites. Sessions 2 and 3 are sequential because Session 3's `GenericDispatchEmitter` builds on Session 2's `GenericProtocolEmitter`, and Session 3's `GetCdeclParamMapping` breakup benefits from Session 2's `SelfReconstructionEmitter`.

---

## Previous Phased Roadmap (superseded by Session Plan above)

The items below are the original phase-based breakdown, preserved for reference. All items are covered by the Session Plan.

<details>
<summary>Original Phase 1-4 breakdown</summary>

### Phase 1: Quick Wins (Low Risk, High Clarity)

These changes are mechanical, low-risk, and immediately improve code clarity.

| Item | Files | Est. Effort | Lines Saved |
|------|-------|-------------|-------------|
| Extract `HasIncompatibleFields()` helper | WrapperValidation.cs | 30 min | ~8 |
| Create `AbiSizeLimits` constants | WrapperValidation.cs | 30 min | ~0 (clarity) |
| Remove redundant `s_isMonoRuntime` caches | SwiftSafeHandle.cs, SwiftDispose.cs | 30 min | ~6 |
| Mark `RegisterDestroyAction` obsolete | SwiftHandle.cs | 5 min | ~0 (signal) |
| Fix EveryProtocol double-check locking | EveryProtocol.cs | 30 min | ~0 (correctness) |
| Align SwiftDictionary lazy init | SwiftDictionary.cs | 30 min | ~0 (consistency) |
| Extract `EmitCdeclAnnotation()` helper | Method/Property/ConstructorWrapperEmitter | 1 hr | ~30 |
| Add `[DynamicallyAccessedMembers]` to reflection helpers | ISwiftObject.cs | 30 min | ~0 (trimmer safety) |
| Add ExistentialContainer2-8 to TrimmerRoots.xml | TrimmerRoots.xml | 15 min | ~0 (proactive) |

### Phase 2: Shared Emitter Helpers (Medium Risk, High Value)

Extract cross-cutting patterns into shared utilities. Each can be done independently.

| Item | Files | Est. Effort | Lines Consolidated |
|------|-------|-------------|-------------------|
| Witness table pre-registration registry | SwiftDictionary.cs, SwiftSet.cs, ModuleHandler.cs | 3-4 hr | ~0 (eliminates AT RISK reflection) |
| Pre-registration completeness test | Unit tests | 2-3 hr | ~0 (prevents regression) |
| `SelfReconstructionEmitter` utility | 4+ emitter files | 2-3 hr | ~100 |
| `StringReturnEmitter` utility | Property/MethodWrapperEmitter | 1-2 hr | ~50 |
| Move metatype helper emission to shared utility | ConstructorWrapperEmitter → new file | 2 hr | ~0 (better ownership) |
| Shared `GenericProtocolEmitter` | Property/MethodWrapperEmitter | 3-4 hr | ~400 |
| `OptionalMarshalStrategy` enum + classifier | 14+ files | 4-6 hr | ~200 (clarity + consistency) |

### Phase 3: Structural Refactoring (Higher Risk, Critical Value)

Larger changes that restructure how emitters work.

| Item | Files | Est. Effort | Lines Consolidated |
|------|-------|-------------|-------------------|
| Break `GetCdeclParamMapping` into strategy handlers | ConstructorWrapperEmitter + new files | 6-8 hr | 1,000 reorganized |
| Unified `GenericDispatchEmitter` (3 strategies → 1 configurable) | Method/Property/ConstructorWrapperEmitter | 8-12 hr | ~500 |
| `MarshalSequencer` for constructor/method/ObjC ordering | WrapperEmitter.cs | 4-6 hr | ~150 |
| Unified return value dispatcher | WrapperEmitter.Return.cs, Property/MethodWrapperEmitter | 6-8 hr | ~200 |
| Consolidate Optional tag byte computation (single source of truth) | SwiftOptional.cs, OptionalProjection.cs, WrapperEmitter.Return.cs | 4-6 hr | ~100 |
| `WrapperDecision` enum + centralized decision method | WrapperValidation.cs, MethodHandler.cs | 2-3 hr | ~20 (clarity) |
| Extract type-signature guards from ShouldEmitWrapper | MethodWrapperEmitter.cs | 2 hr | ~0 (readability) |

### Phase 4: Coordination Patterns (Highest Risk)

These change how emitters coordinate. Should be done last, after Phase 2-3 stabilize.

| Item | Files | Est. Effort | Lines Consolidated |
|------|-------|-------------|-------------------|
| `ErrorInfrastructureEmitter` | WrapperEmitter.Marshalling + others | 4 hr | ~150 |
| `ClosureCallbackOrchestrator` | Property/MethodWrapperEmitter | 4-6 hr | ~80 |
| Wrapper emitter base class or shared interface | All wrapper emitters | 8-12 hr | Architecture change |

</details>

---

## Area 6: Reflection & NativeAOT Trimming Safety (P2)

### Problem

Multiple sessions fixed bugs where reflection-based code worked on Mono JIT but was stripped by NativeAOT trimming. The project now has a multi-layer defense (factory pre-registration, trimmer roots, runtime guards), but several risky patterns remain. A single missed registration can cause a silent crash on device.

### Defense Architecture

The project implements three layers of defense:

| Layer | Mechanism | Coverage |
|-------|-----------|----------|
| Primary | Factory pre-registration via `[ModuleInitializer]` | All non-generic ISwiftObject types |
| Secondary | `TrimmerRoots.xml` preserving Swift.Runtime types | Core runtime types (SwiftString, SwiftArray, etc.) |
| Tertiary | `RuntimeFeature.IsDynamicCodeSupported` guards | Reflection paths isolated to Mono JIT only |

### Finding 6.1: SwiftDictionary/SwiftSet — Unconstrained Reflection (AT RISK)

`SwiftDictionary.cs` and `SwiftSet.cs` call `ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>()` which may call `MakeGenericType()` to look up witness tables. There's an explicit TODO comment acknowledging this:

```csharp
// TODO: Add global conformance registry to eliminate MakeGenericType for all callers.
var witnessTable = ProtocolWitnessTable.GetOrThrow<TKey, ISwiftHashable>();
```

**Risk**: Custom code using `SwiftDictionary<UnknownType, ValueType>` could fail on NativeAOT with `TypeInitializationException`. Generated bindings are safe (they use concrete types that are pre-registered), but the public API is unsafe.

**Recommendation**: Implement a witness table pre-registration registry matching the `NewFromPayloadDispatcher` factory pattern. Populate it from `[ModuleInitializer]` alongside the existing factory registrations. This is the single highest-priority reflection fix.

### Finding 6.2: SwiftObjectReflectionHelper — Missing DynamicallyAccessedMembers

`ISwiftObject.cs` (~lines 131-198) has three reflection methods that search for static methods via `GetMethods()`:

- `InvokeGetTypeMetadata()` — searches by name substring
- `InvokeNewFromPayload()` — searches by name substring
- `InvokeGetProtocolConformanceDescriptor()` — uses `MakeGenericMethod()`

These are suppressed with `[UnconditionalSuppressMessage("Trimming", "IL2070")]` but lack proper `[DynamicallyAccessedMembers]` annotations on their `Type` parameters.

**Current safety**: Guarded by `RuntimeFeature.IsDynamicCodeSupported` — reflection only runs on Mono JIT. On NativeAOT, the static virtual dispatch path is used instead.

**Risk**: If the guard is ever bypassed (e.g., a new caller doesn't check), `GetMethods()` returns empty on NativeAOT → `TypeMetadata.Zero` returned → crash downstream.

**Recommendation**: Add `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]` to the `Type` parameters. This doesn't change behavior but allows the trimmer to validate preservation.

### Finding 6.3: Tuple Metadata — Reflection-Only Path

`TypeMetadata.cs` (~lines 539-590) uses `MakeGenericMethod()` to look up tuple element metadata:

```csharp
var tryGetMethod = typeof(TypeMetadata).GetMethod(nameof(TryGetTypeMetadata), ...);
var genericMethod = tryGetMethod.MakeGenericMethod(elementTypes[i]);
var success = (bool)genericMethod.Invoke(null, args)!;
```

Marked with `[RequiresDynamicCode]` and `[UnconditionalSuppressMessage("AOT", "IL3050")]`.

**Current safety**: Tuple types are rare in Swift bindings. Generated bindings emit inline tuple handling code (Session 12 added `GetTupleTypeMetadataFromElements()` P/Invoke + per-element `MarshalPrimitiveFromSwift<T>()`), so this reflection path is only hit by manual runtime usage.

**Risk**: Low. If it fails, a clear `NotSupportedException` is thrown. But the path exists and could be hit by downstream consumers.

**Recommendation**: Document that tuple marshalling through the generic runtime path is Mono-only. Consider adding a runtime check that throws a clear error on NativeAOT before attempting reflection.

### Finding 6.4: ProtocolConformanceDescriptor.TryGet — Cache-First but Reflection Fallback

`ProtocolConformanceDescriptor.cs` (~lines 88-130) uses a cache-first pattern:

```
1. Check ConformanceDispatcher cache (pre-populated by [ModuleInitializer])
2. If miss → reflection fallback (SwiftObjectReflectionHelper)
3. If miss → MakeGenericType() on Mono, throw on NativeAOT
```

**Current safety**: High. Module initializers pre-register all conformances. The fallback is a safety net, not a primary path.

**Risk**: If a conformance isn't pre-registered AND the user calls this directly, the fallback fires. On NativeAOT, it gracefully returns `false`. On Mono, it uses `MakeGenericType()` successfully.

**Recommendation**: Add a diagnostic log when the fallback fires, so binding authors can identify missing registrations during development.

### Finding 6.5: TrimmerRoots.xml Coverage Gaps

Current `TrimmerRoots.xml` preserves:
- SwiftString, SwiftArray, SwiftOptional, SwiftResult, SwiftDictionary, SwiftSet, Data, AnyHashable
- ExistentialContainer0, ExistentialContainer1

**Not preserved** (intentionally):
- Foundation/UIKit/AppKit stub types — excluded to avoid ILC crashes from CallConvSwift P/Invoke members
- ExistentialContainer2-8 — not currently used in generated bindings

**Risk**: If a binding uses ExistentialContainer2+ (multi-protocol conformance), the trimmer may strip it. The `Activator.CreateInstance()` fallback in `GetProtocolCountFromExistentialType()` would fail.

**Recommendation**: Add ExistentialContainer2-8 to TrimmerRoots.xml proactively, or validate that the name-pattern-matching primary path handles all cases without needing instantiation.

### Finding 6.6: Pre-Registration Completeness — No Automated Verification

Module initializers register all emitted types, but there's no test verifying that every emitted type IS registered. If a new handler emits a type but forgets to call `RecordSwiftObjectType()`, the type won't be pre-registered and will silently fall back to reflection.

**Recommendation**: Add a unit test that:
1. Generates bindings for a known library
2. Parses the emitted `[ModuleInitializer]` code
3. Compares registered types against all emitted ISwiftObject types
4. Fails if any type is missing

### Reflection Inventory Summary

| Pattern | Location | Risk | Mitigation | Failure Mode |
|---------|----------|------|-----------|--------------|
| Static virtual dispatch | ISwiftObject.cs | SAFE | Direct call, no reflection | N/A |
| Factory pre-registration | SwiftMarshal.cs | SAFE | [ModuleInitializer] | N/A |
| P/Invoke (DllImport) | All generated code | SAFE | Compile-time binding | N/A |
| Mono-only reflection guard | ISwiftObject.cs | MITIGATED | `IsDynamicCodeSupported` | N/A on NativeAOT |
| Tuple metadata lookup | TypeMetadata.cs | MITIGATED | [RequiresDynamicCode], rare path | NotSupportedException |
| Conformance lookup | ProtocolConformanceDescriptor.cs | MITIGATED | Cache-first, graceful false return | Silent miss |
| Existential container instantiation | TypeMetadata.cs | MITIGATED | Name-matching primary path | Rare fallback |
| **SwiftDictionary/SwiftSet witness** | SwiftDictionary.cs, SwiftSet.cs | **AT RISK** | TODO comment only | TypeInitializationException |
| **SwiftObjectReflectionHelper** | ISwiftObject.cs | **AT RISK** | Mono guard (bypassable) | MethodAccessException |
| **Pre-registration completeness** | ModuleHandler.cs | **AT RISK** | No automated check | Silent reflection fallback |

---

## Validation Strategy

All refactoring phases should be validated using the existing test gates:

1. **Unit tests** (`./run-tests.sh`) — fast feedback after each change
2. **Library validation** (`./validate-libraries.sh`) — ensures generated code still compiles
3. **Runtime tests** (`cd BindingTests && ./build-and-test.sh`) — end-to-end verification

Phases should be done incrementally, with full validation gates between phases. Each Phase 2-3 item can be an independent PR with its own validation.

---

## What NOT to Refactor

Some patterns that look like duplication are intentionally different:

1. **Metadata parameter ordering** (methods: after self, properties: before self) — matches P/Invoke signature builder requirements. Don't unify the ordering, just document the invariant.

2. **SwiftSafeHandle Mono finalizer workaround** — accepts memory leak on simulator to prevent JIT assertion crash. This is a deliberate tradeoff, not debt.

3. **Optional<closure> vs Optional<value-type> handling** — these are fundamentally different Swift ABI patterns (extra-inhabitant encoding vs tag byte). They should remain separate code paths, just better organized.

4. **MemberValidationPipeline phases** — the 6-phase pipeline is well-structured and each phase has clear responsibility. Don't merge phases.

5. **`ShouldEmitWrapper()` guard ordering** — guards are intentionally ordered (cheap disqualifiers first, expensive type checks last). Keep the ordering, just extract groups into named helpers.
