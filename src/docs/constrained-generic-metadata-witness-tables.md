# Constrained-Generic Metadata Accessor Witness Tables

**Date**: April 7, 2026
**Status**: Plan — pending implementation
**Target release**: 0.7.0
**Codex review**: pass 2 (P1/P2 findings incorporated)

## Summary

The generator emits incomplete P/Invoke signatures for type-metadata accessors of generic types whose generic parameters carry protocol constraints. The Swift ABI for these accessors requires one protocol witness table (PWT) pointer per constraint, in a specific order. We currently pass only the type-metadata pointers, leaving uninitialized register state where Swift expects PWTs. On NativeAOT/arm64e this manifests as a PAC trap deep inside `MetadataCacheKey::operator==` because the cache walker authenticates a stale signed pointer that the C# call site never provided.

A short-term workaround (lazy `_payloadSize` field initializer) currently masks the crash for the Lottie xcframework on the device matrix by perturbing call ordering enough that the relevant register happens to hold zero from `RhpPInvoke`. This is fragile and must be replaced with a proper ABI-correct fix before 0.7.0 ships.

## The bug, in detail

### Concrete repro

Lottie defines:

```swift
public enum ValueProviderStorage<T: AnyInterpolatable> {
    case single(T)
    case keyframes(KeyframeGroup<T>, KeyframeValueMapping<T>)
    case closure((CGFloat) -> T)
}
```

The Swift compiler emits a metadata accessor with the symbol `_$s6Lottie20ValueProviderStorageOMa`. Disassembly of the binary:

```
0xf1410: adrp x4, 151 ; 0x188000
0xf1414: add  x4, x4, #0x150
0xf1418: b    __swift_instantiateGenericMetadata
```

The accessor is a thin tail-call into `__swift_instantiateGenericMetadata`. The descriptor at `x4` declares the expected argument count, which `__swift_instantiateGenericMetadata` reads to walk the cache key. For `ValueProviderStorage<T>`, the descriptor specifies **two** generic-context arguments:

| Register | Value the ABI expects |
|----------|------------------------|
| `x0`     | `MetadataRequest` (e.g. `Complete = 0`) |
| `x1`     | Type metadata for `T` |
| `x2`     | Witness table for `T : AnyInterpolatable` |

The generator currently emits this C# call site (Lottie 0.6.0 generated output):

```csharp
static TypeMetadata ISwiftObject.GetTypeMetadata() =>
    ValueProviderStorage_PInvoke.PInvoke_getMetadata(
        TypeMetadataRequest.Complete,
        SwiftObjectHelper<T>.GetTypeMetadata().Handle);
```

with PInvoke:

```csharp
[DllImport(...)]
internal static extern TypeMetadata PInvoke_getMetadata(
    TypeMetadataRequest request,
    IntPtr t0Metadata);
```

That's two arguments. The third register (`x2`) is whatever the JIT/AOT thunk left there. On NativeAOT, the post-`RhpPInvoke` register state is unspecified for caller-saved registers `x2`–`x18`.

### Why it crashes

`__swift_instantiateGenericMetadata` builds a `MetadataCacheKey` from the argument registers per the descriptor's `numKeyArguments`. For `ValueProviderStorage`, that's two key arguments: the type metadata (`x1`) and the protocol witness table (`x2`). The cache walker dereferences both as authenticated pointers.

When `x2` happens to hold a kernel-poisoned-but-tagged value (which it does on NativeAOT because `RhpPInvoke` doesn't zero it), the `auth*` instruction inside `MetadataCacheKey::operator==` traps.

**Confirmation**: changing the C# emission so the lazy `_payloadSize` field initializer fires earlier in the call sequence happens to leave `x2 = 0` (the trampoline's freshly-zeroed register from a different code path), and the crash disappears. This is empirical evidence that the crash is an uninitialized-register problem, not a Swift runtime bug.

### Why methods on the same type don't crash

Constrained-generic methods (e.g. `func foo<T: P>(_ x: T)`) DO emit PWT arguments today via `PInvokeEmitter.HandleProtocolConformance` and `MethodMarshalPlanBuilder.BuildWitnessTableStatements`. The `MetatypeHelperEmitter` Swift wrapper helper also threads PWTs through for method-level metadata accessors. The bug is specifically in the **type-level** metadata accessor entry point, which goes through `PInvokeHelperContext`. The four type handlers that create a `PInvokeHelperContext` for generic types — `ClassHandler.cs:101`, `FrozenStructHandler.cs:106`, `NonFrozenStructHandler`, and `EnumHandler` — all share the same uninstrumented helper, so generic classes, generic frozen structs, generic non-frozen structs, AND generic enums are all affected. `PInvokeHelperContext` was never taught about per-parameter conformances.

### The Self-requirement complication

`AnyInterpolatable` is declared with a `Self` requirement (`func interpolate(to: Self, ...) -> Self`). The existing `IsProtocolAvailableForConstraint` filter (used at every PWT call site for methods) excludes such protocols:

```csharp
return record.Kind == TypeRecordKind.Protocol &&
       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
       !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
```

This is correct for **method-level** PWTs because Self-requirement protocols often produce no usable C# interface at all (the generator emits a stub or skips them entirely — see `EveryProtocolEmitter` and the protocol-skip diagnostics). The `ProtocolWitnessTable.GetOrThrowAuto<T, IProtocol>()` static-typed lookup path simply has nothing to bind `IProtocol` to.

But the type-level metadata accessor doesn't care whether the C# side has a usable interface — Swift just requires the witness table pointer to be present. So we need a **dynamic** lookup path for these constraints: load the protocol descriptor by its mangled `Mp` symbol, call `swift_conformsToProtocol(typeMetadata, descriptor)` to obtain the witness table at runtime.

## Existing infrastructure (already in the codebase)

| Capability | Location | Notes |
|---|---|---|
| `swift_conformsToProtocol` wrapper | `SwiftConformance.TryGetWitnessTable` | Returns `ProtocolWitnessTable` or null |
| Load protocol descriptor by symbol | `ProtocolDescriptor.LoadFromSymbol(libName, symbolName)` | `dlopen`+`dlsym`; expensive — must be cached |
| Static-typed PWT lookup | `ProtocolWitnessTable.GetOrThrowAuto<TType, TProtocol>()` | NativeAOT-direct + Mono-reflection paths |
| Per-param conformance data on TypeDecl | `TypeDecl.GenericParameters[i].GenericConformances` | Already populated by `GenericSignatureParser` |
| Protocol mangled name in ABI JSON | `ProtocolDecl.MangledName` (e.g. `$s6Lottie16AnyInterpolatableP`) | Form: type symbol, not descriptor |
| Caching pattern reference | `SwiftResult.cs:521-544` (`_errorProtocol` + `_errorWitnessTableCache`) | The shape we should mirror |

The only gap is **plumbing**: the parser drops the protocol mangled name when it converts a `ProtocolDecl` into a `TypeRecord`, and `PInvokeHelperContext` (which drives the type-level metadata accessor PInvoke emission) was never given conformance information.

## ABI ordering rule

From `runtime-metadata.md` line 33:

> The signature of a type metadata accessor is a request followed by n TypeMetadata objects (one for each generic type). Those are then followed by protocol witness tables (one for each protocol conformance). **The witness tables are ordered first by the generic type they correspond to, second by lexicographical order.**

And the example for `Foo<T0: P2 & P0, T1, T2: P1>`:

```
Request, T0_meta, T1_meta, T2_meta, T0[P0], T0[P2], T2[P1]
```

So the canonical iteration is:

```
for each generic param i (declaration order):
    emit metadata[i]
for each generic param i (declaration order):
    for each conformance c on param i, sorted lex by ConformanceTarget.ModuleQualifiedName:
        emit pwt[i, c]
```

> **Caveat** — the same doc notes that **when the total number of generic types + protocol conformances exceeds three**, the ABI changes to use an indirect buffer (`request, IntPtr buffer`). The proper fix MUST handle this case. Lottie's `ValueProviderStorage<T: AnyInterpolatable>` has 1 metadata + 1 PWT = 2 args, so it's in the register-passing range. But Stripe and others may exceed the threshold. See **Open scope** below.

## Plan

### 1. `TypeRecord.ProtocolDescriptorSymbol` (dedicated field)

**Why dedicated**: Codex flagged that reusing `MetadataAccessor` is unsafe — `ModuleDatabaseEmitter.WriteEntity` (line 87) serializes it as the `mangledName` XML attribute for *every* TypeRecord, and `NativeThunkEmitter.GetMetadataAccessorSymbol` (line 660-663) reads it without a `Kind` guard to build underscore-prefixed metadata accessor symbols. Stuffing a protocol descriptor symbol there would cause the thunk emitter to emit `_$s...Mp` references that don't exist as accessors.

**Add** to `TypeRecord`:

```csharp
/// <summary>
/// For Protocol kind: the mangled symbol of the protocol descriptor (e.g. "$s6Lottie16AnyInterpolatableMp").
/// Null for non-protocol kinds. Used by the type-metadata-accessor emitter to construct dynamic
/// witness-table lookups for Self-requirement / associated-type protocols that cannot be expressed
/// as a static C# interface.
/// </summary>
public string? ProtocolDescriptorSymbol { get; init; }
```

**Populate** in `ModuleProcessor.RegisterProtocolType`:

```csharp
ProtocolDescriptorSymbol = ConvertProtocolTypeToDescriptorSymbol(protocolDecl.MangledName),
```

**Helper**:

```csharp
/// <summary>
/// Converts a protocol type symbol (ABI JSON form, ending in 'P') to its protocol descriptor
/// symbol (ending in 'Mp'). Strips exactly one terminal 'P' to avoid corrupting names that
/// happen to contain multiple consecutive Ps (which TrimEnd('P') would mishandle).
/// Verified via swift-demangle:
///   $s20SwiftBindingsTestLib8SummableP  (type)       ->
///   $s20SwiftBindingsTestLib8SummableMp (descriptor)
///   $sSH                                (Hashable)   ->
///   $sSHMp                              (Hashable descriptor)
/// </summary>
private static string? ConvertProtocolTypeToDescriptorSymbol(string? mangled)
{
    if (string.IsNullOrEmpty(mangled)) return null;
    return mangled.EndsWith('P') ? mangled[..^1] + "Mp" : mangled + "Mp";
}
```

**XML round-trip**: `ModuleDatabaseEmitter.WriteEntity` adds a new `protocolDescriptorSymbol` attribute when present. `TypeDatabase.LoadModuleTypeDatabase` reads it back. Bump XML schema version comment.

### 2. `SwiftConformance.GetWitnessTableOrThrow` (runtime helper)

```csharp
public static ProtocolWitnessTable GetWitnessTableOrThrow(
    TypeMetadata typeMetadata,
    ProtocolDescriptor protocolDescriptor)
{
    if (!TryGetWitnessTable(typeMetadata, protocolDescriptor, out var wt))
        throw new SwiftRuntimeException(
            $"Type does not conform to required protocol " +
            $"(metadata: 0x{typeMetadata.Handle:X}, descriptor: 0x{Unsafe.As<ProtocolDescriptor, IntPtr>(ref protocolDescriptor):X}).");
    return wt!.Value;
}
```

Centralizes the throw site and makes generated call sites a single line.

### 3. `PInvokeHelperContext` extension

#### 3a. Pre-flatten conformances at construction

```csharp
public sealed record HelperPwtEntry(
    int GenericParamIndex,
    string GenericParamCsName,         // "T0", "T1", ...
    string ProtocolName,               // "AnyInterpolatable", "Hashable", ...
    string ProtocolModuleQualifiedName, // for sort key
    bool IsResolvable,                 // !HasSelfRequirement && !HasAssociatedTypes && in TypeDatabase
    string? ResolvableInterfaceName,   // "Describable.IDescribable" — built via NameProvider.GetInterfaceName
                                       // (when IsResolvable). For Swift stdlib runtime protocols this
                                       // becomes "ISwiftHashable", "ISwiftCollection", etc. — see the
                                       // _runtimeProtocols set in NameProvider.cs:742
    string? DescriptorSymbol,          // "$s6Lottie16AnyInterpolatableMp" (when !IsResolvable)
    string? LibraryPath);              // dylib path for LoadFromSymbol
```

Built once in `CreateIfGeneric(typeDecl, typeDatabase)`:

```csharp
var entries = new List<HelperPwtEntry>();
for (int i = 0; i < typeDecl.GenericParameters.Count; i++)
{
    var gp = typeDecl.GenericParameters[i];
    var csName = NameProvider.GetCSharpGenericParameterName(gp, i);
    var ordered = gp.GenericConformances
        .OrderBy(c => c.ConformanceTarget.ModuleQualifiedName, StringComparer.Ordinal);
    foreach (var c in ordered)
    {
        // Build entry — populate ResolvableInterfaceName XOR DescriptorSymbol+LibraryPath
        // based on TypeRecord lookup.
    }
}
```

#### 3b. Parameter declarations and argument list

`GetMetadataParameterDeclarations()` and `GetMetadataArgumentList()` walk the same flat ordering: **all metadata params first (declaration order), then all PWT params grouped by generic param then sorted lex by protocol name**, matching the runtime-metadata.md spec.

For each PWT entry, the argument expression is one of:

| Case | Expression |
|---|---|
| Resolvable, Swift stdlib (e.g. `Hashable`) | `ProtocolWitnessTable.GetOrThrowAuto<T0, ISwiftHashable>().Handle` |
| Resolvable, user protocol (e.g. `Describable`) | `ProtocolWitnessTable.GetOrThrowAuto<T0, IDescribable>().Handle` |
| Unresolvable (Self-req / associated) | `ConstrainedBox_PInvoke.GetAnyInterpolatablePWT(SwiftObjectHelper<T0>.GetTypeMetadata()).Handle` |

The interface name comes from `NameProvider.GetInterfaceName(protocolName, moduleName: ...)` — Swift stdlib protocols in the `_runtimeProtocols` set get the `ISwift{Name}` prefix, user-defined protocols get the plain `I{Name}` prefix, and `Equatable` gets a special-cased generic form. The generator must call `NameProvider.GetInterfaceName` so this stays in sync; the doc shows the *result* shapes, not literal hard-coded strings.

The unresolvable case calls into a generated cached helper (see 3c).

#### 3c. Cached dynamic-lookup helpers (generated into the `*_PInvoke` static class)

For each unique unresolvable conformance, emit ONE descriptor cache, ONE witness table cache, and one `Get{Protocol}PWT_{T}` method, deduplicated by `(LibraryPath, DescriptorSymbol, GenericParamCsName)`:

```csharp
private static readonly Lazy<ProtocolDescriptor> _anyInterpolatableDescriptor =
    new(() => ProtocolDescriptor.LoadFromSymbol(
        "@rpath/Lottie.framework/Lottie",
        "$s6Lottie16AnyInterpolatableMp"));

private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, ProtocolWitnessTable>
    _anyInterpolatableWitnessTableCache_T0 = new();

private static ProtocolWitnessTable GetAnyInterpolatablePWT_T0()
{
    var meta = SwiftObjectHelper<T0>.GetTypeMetadata();
    return _anyInterpolatableWitnessTableCache_T0.GetOrAdd(meta.Handle, _ =>
        SwiftConformance.GetWitnessTableOrThrow(meta, _anyInterpolatableDescriptor.Value));
}
```

This mirrors `SwiftResult.cs:521-544` exactly. Per-call-site `LoadFromSymbol` would do `dlopen`/`dlsym`/`dlclose` on every metadata access — totally unacceptable for hot paths.

**Important**: the helper class is `static partial class Foo_PInvoke` but it's NOT itself generic — it lives outside the generic type to satisfy CS7042. The `T0` in `SwiftObjectHelper<T0>` refers to the **outer** generic type parameter, which the helper class can't access directly. Resolution: the cached `Get*PWT_*` method takes a `TypeMetadata` parameter, and the call site (which IS inside the generic type) supplies it:

```csharp
// Inside the generic type:
static TypeMetadata ISwiftObject.GetTypeMetadata() =>
    ValueProviderStorage_PInvoke.PInvoke_getMetadata(
        TypeMetadataRequest.Complete,
        SwiftObjectHelper<T>.GetTypeMetadata().Handle,
        ValueProviderStorage_PInvoke
            .GetAnyInterpolatablePWT(SwiftObjectHelper<T>.GetTypeMetadata())
            .Handle);
```

with the helper:

```csharp
private static ProtocolWitnessTable GetAnyInterpolatablePWT(TypeMetadata typeMetadata) =>
    _anyInterpolatableWitnessTableCache.GetOrAdd(typeMetadata.Handle, _ =>
        SwiftConformance.GetWitnessTableOrThrow(typeMetadata, _anyInterpolatableDescriptor.Value));
```

The cache is keyed by metadata `Handle`, so a single helper serves all generic instantiations of the outer type. Dedup key in `PInvokeHelperContext.RawCodeBlocks` becomes `(LibraryPath, DescriptorSymbol)` only — the `_T0` suffix is unnecessary.

### 4. Hook through type handlers

**Four** type handlers create `PInvokeHelperContext` via `CreateIfGeneric(typeDecl)` and must all be updated:

| Handler | Call site | What it emits |
|---|---|---|
| `ClassHandler.cs:101` | `var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(classDecl);` | Generic class metadata accessor PInvoke (consumed by `ClassISwiftObjectMethodWriter`) |
| `FrozenStructHandler.cs:106` | `var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(structDecl);` | Generic frozen struct metadata accessor PInvoke |
| `NonFrozenStructHandler.cs` | same pattern | Generic non-frozen struct metadata accessor PInvoke |
| `EnumHandler.cs` | same pattern | Generic enum metadata accessor PInvoke (consumed by `EnumISwiftObjectMethodWriter`) |

Update the signature to `CreateIfGeneric(typeDecl, typeDatabase)` so the context can pre-flatten conformances. All four handlers pass their existing `_typeDatabase` reference. The downstream method writers (`EnumISwiftObjectMethodWriter`, `ClassISwiftObjectMethodWriter`, the analogous struct writers) consume `_pinvokeHelperContext.GetMetadataArgumentList()` / `GetMetadataParameterDeclarations()` transparently — no changes needed at the call sites.

Confirm during implementation that **none** of the four handlers diverge in how they construct `PInvokeHelperContext` — they all use the same `CreateIfGeneric` factory, so the conformance pre-flattening only needs to happen in one place.

### 5. Tests

#### Unit tests (emitter output)

All test names use `Emit_Generic{Kind}_*` so we cover the four affected handler paths (enum, frozen struct, non-frozen struct, class) — not just enums.

| Test | Purpose |
|---|---|
| `Emit_GenericEnum_SingleResolvableUserConstraint` | `enum E<T: Describable> { case x }` → assert PInvoke param `IntPtr t0DescribablePWT` and call site `ProtocolWitnessTable.GetOrThrowAuto<T0, IDescribable>().Handle` (user protocol → plain `I` prefix) |
| `Emit_GenericEnum_SingleResolvableSwiftStdlibConstraint` | `enum E<T: Hashable> { case x }` with Hashable in module "Swift" → assert call site uses `ISwiftHashable` (because `Hashable` is in `NameProvider._runtimeProtocols`) |
| `Emit_GenericEnum_MultipleConstraintsLexOrder` | `enum E<T: Describable & Configurable> { case x }` → assert order `t0ConfigurablePWT, t0DescribablePWT` (lex sort within param, both user protocols to keep the test self-contained) |
| `Emit_GenericEnum_MultipleParamsAndConstraints` | `enum E<T0: P2 & P0, T1, T2: P1> { case x }` → assert order `(req, t0Meta, t1Meta, t2Meta, t0_P0_PWT, t0_P2_PWT, t2_P1_PWT)` per runtime-metadata.md spec |
| `Emit_GenericEnum_SelfRequirementConstraint` | mock TypeRecord with `HasSelfRequirement` + `ProtocolDescriptorSymbol` set → assert call site uses `{TypeName}_PInvoke.Get{Protocol}PWT(...)` cached helper, NOT inline `LoadFromSymbol` |
| `Emit_GenericEnum_DescriptorCacheDeduplication` | two generic params constrained on the same Self-requirement protocol → assert ONE `Lazy<ProtocolDescriptor>` field, ONE `ConcurrentDictionary` cache, ONE `Get{Protocol}PWT` method |
| `Emit_GenericClass_SingleResolvableConstraint` | `class C<T: Describable> {}` → assert `ClassHandler` path produces the same PWT plumbing as the enum path |
| `Emit_GenericFrozenStruct_SingleResolvableConstraint` | `@frozen public struct S<T: Describable> {}` → assert `FrozenStructHandler` path produces the same PWT plumbing |
| `Emit_GenericNonFrozenStruct_SingleResolvableConstraint` | `public struct S<T: Describable> {}` (non-frozen) → assert `NonFrozenStructHandler` path produces the same PWT plumbing |

#### BindingTests (real ABI)

| Source | Purpose |
|---|---|
| `BindingTests/Sources/SwiftBindingsTestLib/ConstrainedGenericMetadataTests.swift` | `enum ConstrainedEnum<T: Describable> { case wrap(T) }` instantiated with a concrete conformer. Exercises the **resolvable** path against real Swift metadata accessor on simulator + device. |

The Self-requirement path is harder to write portably in test code because most Self-requirement protocols come from Swift stdlib internals (`_ExpressibleByStringInterpolation`). Lottie itself is the integration test for the unresolvable path — the device-matrix run validates it end-to-end.

#### Validation matrix

| Gate | When |
|---|---|
| `nuke test` | After every code change |
| `nuke validate` | Before signing off; baseline must equal or exceed current |
| `nuke binding-tests` | After generator changes |
| `nuke runtime-tests-simulator` | After runtime + generator changes |
| `nuke runtime-tests-device` | **Before AND after** removing the lazy `_payloadSize` hack |
| All 7 device libs (Lottie, Stripe, BlinkID, BlinkIDUX, Kingfisher, MappedIn, Nuke) | Same — green with hack, then green without |

### 6. Removal of the lazy `_payloadSize` hack

The hack currently lives in **two** places — both must be removed in the same step:

| File | Site | Search anchor |
|---|---|---|
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` | `_payloadSizeLazy` field initializer (generic enum branch) | `LAZY initialization: the helper PInvoke is Swift's constrained-generic` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs:284` | `WritePrivateFields` generic branch — same `_payloadSizeLazy` pattern | same comment text |

The same workaround was added to both handlers because both go through `PInvokeHelperContext` and both hit the same Swift PAC/SIGTRAP at cctor time. Removing only one would leave generic non-frozen structs (or generic enums) crashing at launch even after the proper fix lands.

The hack MUST stay in place at both sites until the proper fix is verified green on the full device matrix. Order:

1. Implement steps 1–5
2. Run all gates with **both** hack sites still present → green (proves the proper fix doesn't regress the workaround)
3. Remove the hack at **both** sites in the same change
4. Re-run all gates → green (proves the proper fix stands alone for both enum and non-frozen struct paths)
5. Commit (the hack removal is a separate commit so a bisect can isolate it)

If step 4 regresses, the proper fix is incomplete somewhere — debug, do not put the hack back. Pay particular attention to whether the regression is enum-only, struct-only, or both: that signals which handler's `CreateIfGeneric` wiring is incomplete.

**Audit before removal**: also `grep -rn "_payloadSizeLazy"` across `src/Swift.Bindings/src/Emitter/` immediately before step 3. If `ClassHandler.cs` or `FrozenStructHandler.cs` have grown the same workaround in the meantime, they need to be removed too. The grep is the source of truth — do not rely on this doc's file list staying in sync.

## Open scope

These are deliberately **not** in scope for 0.7.0 but should be tracked.

### Indirect-buffer ABI for >3 args

`runtime-metadata.md` line 42: when `(num_metadata + num_pwts) > 3`, the metadata accessor signature changes to `(request, IntPtr buffer)` where `buffer` points to a packed struct of metadata + PWT handles. The proper fix as designed handles only the register-passing case (≤3 total args).

**Explicit 0.7.0 release criterion** — pick ONE of the following before sign-off, do NOT leave this ambiguous:

- **Option A: Buffer mode supported.** `PInvokeHelperContext` detects `(num_metadata + num_pwts) > 3`, emits the indirect-buffer P/Invoke signature, and constructs the packed argument buffer at the call site. Has its own dedicated unit test (`Emit_GenericEnum_IndirectBufferMode_FourArgs`). Requires careful handling of the buffer struct layout and lifetime.
- **Option B: Buffer mode skipped with diagnostic.** `PInvokeHelperContext` detects `(num_metadata + num_pwts) > 3`, refuses to emit the type, and produces a clear skip diagnostic. **MUST be paired with**: an audit of all 7 device libs (Lottie, Stripe, BlinkID, BlinkIDUX, Kingfisher, MappedIn, Nuke) confirming none of them contain a constrained-generic type with >3 total args. The audit script + results are committed to `src/docs/Completed/` so the audit is reproducible.

The audit in Option B is non-negotiable: shipping a "skip with diagnostic" path that silently breaks a real-world library would be worse than the current register-state hack. The audit must run **before** the implementation starts, so we know upfront whether we can take the simpler Option B path or whether Option A is forced.

**Implementation order**: run the audit first (script that walks each xcframework's ABI JSON, finds every generic type with constraints, sums metadata + PWT count, flags any >3). If audit returns zero hits across all 7 libs, take Option B. If any hit, take Option A.

### `MetatypeHelperEmitter` Swift wrapper path

`MetatypeHelperEmitter.cs` emits a Swift `dlsym`-based helper for **method/constructor**-level metadata accessor calls. It uses `GetResolvablePwtParameterCount()` which excludes Self-requirement protocols. This path doesn't crash today because methods on constrained-generic types over Self-requirement protocols simply don't get emitted (they're filtered upstream). But the gap is real and parallel to the type-level fix.

**Action for 0.7.0**: add a `// TODO 0.8.0` comment in `MetatypeHelperEmitter.cs` near the `GetResolvablePwtParameterCount` call site referencing this doc. Out of scope for 0.7.0 because it requires teaching the Swift wrapper path to dynamically resolve descriptors via `dlsym`+`swift_conformsToProtocol` from Swift code, which is a separate exercise.

### Multi-module conformance resolution

If `T` lives in module A and the constraint protocol lives in module B, the descriptor symbol `LoadFromSymbol` call needs to know module B's library path. The TypeDatabase already knows this via the module-of-origin lookup, but the dedup key in 3c needs to include the library path so two descriptors with the same symbol but different libraries don't collide. Already covered in the dedup key design — flagged here for review during implementation.

## File-by-file change list

| File | Change |
|---|---|
| `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs` | Add `ProtocolDescriptorSymbol` field |
| `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` | `RegisterProtocolType`: populate new field via helper |
| `src/Swift.Bindings/src/Emitter/ModuleDatabaseEmitter.cs` | `WriteEntity`: serialize new attribute |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` | `LoadModuleTypeDatabase`: read new attribute |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftConformance.cs` | Add `GetWitnessTableOrThrow` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` | Pre-flatten conformances; emit cached descriptor/PWT helpers; updated parameter/arg generators |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` | Pass typeDatabase to `CreateIfGeneric`; remove `_payloadSizeLazy` hack (in the second commit) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` | Pass typeDatabase to `CreateIfGeneric`; remove `_payloadSizeLazy` hack at line ~284 (in the second commit) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` | Pass typeDatabase to `CreateIfGeneric` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` | Pass typeDatabase to `CreateIfGeneric` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumISwiftObjectMethodWriter.cs` | (no change — consumes `PInvokeHelperContext` transparently) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` (`ClassISwiftObjectMethodWriter` nested class) | (no change — consumes `PInvokeHelperContext` transparently) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MetatypeHelperEmitter.cs` | Add `// TODO 0.8.0` comment near `GetResolvablePwtParameterCount` call site |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` | New emitter tests for the enum handler path |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassHandlerOutputTests.cs` | New emitter test mirroring the enum case for the class handler path |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/FrozenStructHandlerOutputTests.cs` | New emitter test for frozen-struct handler path |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NonFrozenStructHandlerOutputTests.cs` | New emitter test for non-frozen-struct handler path |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeHelperEmitterTests.cs` | New file — pre-flattening / dedup / ordering unit tests for `PInvokeHelperContext` |
| `BindingTests/Sources/SwiftBindingsTestLib/ConstrainedGenericMetadataTests.swift` | New Swift source — resolvable-constraint enum, struct, and class to exercise all four handler paths against real ABI |
| `BindingTests/RuntimeTestsApp/.../ConstrainedGenericMetadataRuntimeTests.cs` | New runtime tests — instantiate each kind and invoke metadata accessor |
| `build/scripts/audit_constrained_generic_arg_count.py` | New audit script — walks each xcframework's ABI JSON, counts (metadata + PWT) for every constrained generic, outputs CSV. Used to decide Option A vs Option B for the >3 args case. Output committed alongside the implementation PR. |

## Order of operations

1. **Audit script first** — write `build/scripts/audit_constrained_generic_arg_count.py`, run against all 7 device libs, decide Option A (buffer mode) vs Option B (skip + diagnostic) for the >3 args case. Commit the audit results so the decision is reproducible.
2. **Parser + TypeRecord field + XML round-trip + serialization tests** (foundation; isolated, easy to verify)
3. **`SwiftConformance.GetWitnessTableOrThrow` runtime helper** (isolated, easy to verify)
4. **`PInvokeHelperContext` extension** with all three sub-pieces (3a–3c). If audit chose Option A in step 1, this step also includes buffer-mode emission.
5. **Hook through all four handlers** — `EnumHandler`, `NonFrozenStructHandler`, `FrozenStructHandler`, `ClassHandler`. Mechanical wiring; same one-line change in each.
6. **Unit tests for emitter output** — all four handler paths plus the multi-protocol ordering, Self-requirement, and dedup tests. Add and confirm green.
7. **BindingTests Swift source + C# runtime tests** — exercise enum, struct (frozen + non-frozen), and class with constrained generics.
8. **`nuke test` + `nuke validate` + `nuke binding-tests` + `nuke runtime-tests-simulator`** — local gates green
9. **All 7 device libs WITH the lazy `_payloadSizeLazy` hack still in place at BOTH sites** (`EnumHandler.cs` and `NonFrozenStructHandler.cs:284`) — verify proper fix doesn't regress them
10. **Remove the lazy `_payloadSizeLazy` hack at BOTH sites in a single change** — first re-run `grep -rn "_payloadSizeLazy" src/Swift.Bindings/src/Emitter/` to confirm no additional sites have grown
11. **Re-run device libs WITHOUT the hack** — confirm proper fix stands alone for both enum and non-frozen struct paths
12. **Add `TODO 0.8.0` comment in `MetatypeHelperEmitter.cs`**
13. **Commit in two parts**: (a) the fix, (b) the hack removal — bisectable

## References

- `src/docs/Design/runtime-metadata.md` — ABI ordering rule and indirect-buffer threshold
- `src/docs/Design/binding-generics.md` — generic constraint projection (line 463 onward)
- `src/docs/Design/binding-typedatabase.md` — TypeRecord schema
- `src/Swift.Runtime/src/Swift/SwiftResult.cs:521-544` — canonical caching pattern (descriptor + witness table by metadata handle)
- Memory: `feedback_mono_jit_blame.md` — runtime crash blame policy ("ALL runtime crashes are OUR BUGS until proven otherwise")
