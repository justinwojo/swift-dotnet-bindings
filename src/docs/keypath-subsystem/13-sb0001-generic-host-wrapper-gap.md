# Session 13 — SB0001 generic-host wrapper gap (CallConvSwift fallback runtime risk)

Follow-on from session 12. Closing the macOS `EntityQuerySort<TEntity>.By_Get`
CS0029 (commit on `keypath-worktree`, `KeyPathProjection.ContainerTypeName`
override) unblocked the property-accessor return path on generic hosts. The
*construction* path on those same generic hosts is still on the unsafe
`[Obsolete(SB0001)]` direct-`CallConvSwift` fallback — emits, compiles, but
the runtime ABI may not match. The KeyPath-typed fixture added alongside the
session-12 fix is a confirmed crasher; the production AppIntents 0.12 surface
has 7 constructors + 2 builder statics in the same shape with unknown
runtime behaviour.

## Problem statement

`@_cdecl` rejects generic-parameter signatures: a Swift `init(x:)` on a
generic host like `EntityURLRepresentation<TEntity>` cannot be wrapped as
`@_cdecl func _sbw_init(...)` because the wrapper would itself be generic.
The generator detects this in `WrapperValidation.cs` (`HasNoWrapperOrThunk`
and the SB0001-narrow gates at lines 1414–2151) and falls back to emitting
a P/Invoke that targets the mangled Swift symbol directly with
`CallConvSwift`, marked `[Obsolete(SB0001)]` so the consumer at least sees
a compile-time warning.

The fallback is not guaranteed broken — for many shapes the C# marshalling
happens to match Swift's calling convention, parameter order, metatype
threading, retain semantics, and indirect-result handling. But every site
is unverified, and the one site exercised by a runtime test in this branch
**does crash**.

## Confirmed crasher (this branch)

Fixture: `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathGenericReturn.swift`

```swift
public struct KeyPathGenericSort<TElement> {
    public let by: PartialKeyPath<TElement>
    public init(by: PartialKeyPath<TElement>) { self.by = by }
    public var lookup: PartialKeyPath<TElement> { by }
}
public class KeyPathGenericContainer<TElement> {
    public let by: PartialKeyPath<TElement>
    public init(by: PartialKeyPath<TElement>) { self.by = by }
    public var lookup: PartialKeyPath<TElement> { by }
}
```

Regen at `BindingTests/output/SwiftBindingsTestLib.cs:107795-107820`:

```csharp
[Obsolete("No @_cdecl wrapper or native thunk available. ...",
          DiagnosticId = "SB0001", ...)]
public KeyPathGenericSort( Swift.PartialKeyPath<TTElement> by)
{
    unsafe {
        TypeMetadata TTElementMetadata = TypeMetadata.GetTypeMetadataOrThrow<TTElement>();
        _payload = new SwiftSafeHandle<KeyPathGenericSort<TTElement>>(
            (IntPtr)NativeMemory.Alloc(_payloadSize));
        var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());

        using SafeHandlePin byPin = new SafeHandlePin(by.Payload);
        IntPtr byBuffer = byPin.Handle;

        KeyPathGenericSort_PInvoke.PInvoke_init_DB845CC9(
            swiftIndirectResult,
            byBuffer,
            TTElementMetadata.Handle,
            KeyPathGenericSort_PInvoke.PInvoke_getMetadata(
                TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<TTElement>().Handle).Handle);

        Swift.Runtime.SwiftDisposeScope.TryRegister(this);
    }
}
```

P/Invoke at line 107841:

```csharp
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[LibraryImport("SwiftBindingsTestLib",
    EntryPoint = "$s20SwiftBindingsTestLib18KeyPathGenericSortV2byACyxGs07PartialeF0CyxG_tcfC")]
internal static partial void PInvoke_init_DB845CC9(
    SwiftIndirectResult swiftIndirectResult,
    IntPtr byBuffer,
    IntPtr TTElementMetadata,
    IntPtr metatype);
```

Demangled Swift symbol:
`SwiftBindingsTestLib.KeyPathGenericSort.init(by: Swift.PartialKeyPath<A>)
-> SwiftBindingsTestLib.KeyPathGenericSort<A>`

### Repro

Test: `BindingTests/RuntimeTestsApp/KeyPath/KeyPathGenericReturnTests.cs`.
Running the suite as authored (one passing test) is GREEN. Re-adding the
three siblings that the session-12 close-out trimmed (`_LookupAccessor`,
`KeyPathGenericContainer_ByProperty`, `KeyPathGenericContainer_Lookup`)
reproduces:

- Test 1 (`_ByPropertyReturnsTypedPartialKeyPath`) — PASS.
- Tests 2–4 — SIGSEGV inside the **second** invocation of
  `KeyPathFactory.MakeReferenceWritableBoxNPath()` (a `@_cdecl`-wrapped
  factory that works fine in `KeyPathFoundationTests`, including in
  isolation).

The crash on a *previously-safe* factory call on its second run is
consistent with cache or heap corruption seeded by the first test's
disposal phase — i.e., the SB0001 constructor over-released or
mis-stored something, and the second factory call hits the corrupted
state. Direct ARC over-release on the `by` parameter is the obvious
hypothesis but has not been confirmed; see Phase 0 below.

## Production surface (AppIntents 0.12 regen)

`/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/AppIntents/obj/Debug/net10.0-ios26.2/swift-binding/AppIntents.cs`,
9 sites:

| Line | Surface | Why it matters |
|---|---|---|
| 3053 | `EnumSingleURLRepresentation(StringInterpolation)` | Nested under `EnumURLRepresentation<TEnum>` |
| 3249 | `EnumURLRepresentation<TEnum>(StringInterpolation)` | Enum URL routing declaration |
| 4678 | `AppShortcutPhrase<TIntent>(StringInterpolation)` | Siri phrase declaration |
| 11510 | `IntentParameterSummary(string: ParameterSummaryString<TIntent>, table:)` | Parameter summary builder |
| 12572 | `AppShortcutsBuilder.BuildBlock(IEnumerable<AppShortcut>)` | Result-builder static |
| 12595 | `AppShortcutsBuilder.BuildBlock(IEnumerable<IEnumerable<AppShortcut>>)` | Result-builder static |
| 19329 | `IntentURLRepresentation<TIntent>(StringInterpolation)` | Intent URL routing |
| 25280 | `EntityURLRepresentation<TEntity>(StringInterpolation)` | Entity URL routing |
| 29656 | `ParameterSummaryString<TIntent>(StringInterpolation)` | Interpolation building block |

All 7 constructors emit the identical pattern documented above
(`SwiftSafeHandle` alloc → `SwiftIndirectResult` → `SafeHandlePin` →
direct-`CallConvSwift` to mangled init). The two `BuildBlock` statics are
the same shape minus the indirect-result buffer.

These are the *declarative-construction* path of AppIntents — how a C#
consumer would express "this entity has this URL representation", "this
intent's parameter summary is built from these segments". Receiving these
types as return values from query methods or framework callbacks is
unaffected (those go through `@_cdecl` factory wrappers). The blocked
direction is C#-side authoring.

## Wider BindingTests surface

`BindingTests/output/SwiftBindingsTestLib.cs`: **79 SB0001 occurrences**
across ~80 distinct method/constructor shapes. Spot categories:

- Generic struct/class constructors with class-typed params:
  `EquatableContainer<T>(T)`, `OptionalWrapper<T>(T?)`,
  `DependentMemberHost<T>(TValue)`, `CodableContainer<T>(T)`,
  `BufferModeQuad<A,B,C,D>(...)`, `BufferModeDescribablePair<K,V>(...)`,
  `KeyPathGenericContainer<T>(PartialKeyPath<T>)`,
  `KeyPathGenericSort<T>(PartialKeyPath<T>)`.
- Method-own generics on instance methods: `Update<D>(D)`, `Consume<C>(C)`,
  `Append<D>(D, IReadOnlySet<nint>, nint)`, `AppendOrThrow<D>(...)`,
  `AcceptIfSmall<D>(D)`.
- Generic statics with non-blittable params: `Constrained<T>`,
  `MultiConstrained<T>`, `DescribeName<T0>`, `RunProcessor<T0>`,
  `ApplyThreeProtocols<T>`, `ApplyFourProtocols<T>`, etc.
- Generic protocol-extension call-throughs: `LayoutedAdapter<T0>(T0)`,
  `OpaqueLabelCharacterCount<T0>(...)`.

Not every site is necessarily broken — SB0001 is "we have not verified",
not "we know this fails". The session's first job is to bisect: of the 79
sites, which crash, which warn-but-work?

## Phase 0 — diagnose before architecting (mandatory)

Two outcomes possible:

1. **The SB0001 fallback's emission has a fixable bug** (wrong P/Invoke
   parameter order, missing/extra type-metadata arg, wrong indirect-result
   threading, mis-handled retain convention on the class-typed param,
   struct VWT-destroy ordering). One generator change closes the category.
2. **The fallback is fundamentally limited** because no `@_cdecl` wrapper
   can be synthesised for generics. Closure requires a per-instantiation
   wrapper (Path B) or a runtime-metadata trampoline (Path C).

Phase 0 deliverables (no fix code yet):

- `nm -gU` the BindingTests dylib and confirm the Swift symbol exists at
  the expected mangling.
- `swiftc -emit-sil` the fixture to extract SIL for
  `KeyPathGenericSort.init(by:)`. Confirm:
  - Parameter convention on `by` (`@guaranteed` vs `@owned`).
  - Indirect-result slot (`@out` first arg in SIL).
  - Type-metadata args (count, order — is it `T.Type` then
    `Self.Type`, or just `T.Type`, or no metatypes at all?).
  - Whether `let by = by` in the init body emits a retain
    (`strong_retain`) or a take (no extra retain).
- Compare SIL against the C# P/Invoke signature (`PInvoke_init_DB845CC9`):
  param count, order, types. Document the mismatch (if any).
- Repeat for one AppIntents constructor (`EntityURLRepresentation` is the
  closest analogue: same indirect-struct-init shape, plus a protocol
  witness table arg).
- Write a short Phase 0 report into this doc (append-only section
  below) before opening Phase 1.

Memory `feedback_verify_swift_abi_sil.md` applies: do not guess from
mangled names; dump SIL.

## Fix paths

### Path A — Correct the SB0001 fallback emission (Phase 0–dependent)

If Phase 0 reveals a generator bug in the fallback path (param order,
metatype handling, retain convention, indirect-result threading), this is
the right surgery. Candidate emitter sites:

- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- SB0001 gates in `WrapperValidation.cs:1414-2151`

Acceptance: the four KeyPath fixture tests pass on simulator AND device.
Some non-trivial subset of the 79 BindingTests sites drops the SB0001
warning. The 9 AppIntents constructors get a runtime smoke (at least one
per shape) in a new BindingTests fixture mirroring each — they don't have
to all pass yet, but the ones that share Path-A's fixed shape must.

### Path B — Per-instantiation `@_cdecl` wrappers

If Path A doesn't close the category, mirror the Session 4 / 6c pattern:
emit one `@_cdecl` wrapper per closed conformer. Eg. for each known
substitution of `KeyPathGenericSort<T>` (Phase-0-driven enumeration —
likely `BoxKP`, `PointKP` in BindingTests; in production whatever closed
`AppEntity` conformers swift-dotnet-packages surfaces), emit
`SBW_KeyPathGenericSort_BoxKP_init` that takes `UnsafeRawPointer` and
forwards to the typed init.

Re-uses:
- `src/docs/keypath-subsystem/04-typed-singleton-emission.md` cdecl
  trampoline shape.
- `src/docs/keypath-subsystem/06c-sort-by-and-existential-keypath-admission.md`
  Route C closed-conformer emission walker.

Acceptance: closed-conformer paths work; open generics still warn. Doc
the open-generic gap as a Path C follow-on.

### Path C — Generic trampoline with type-metadata dispatch

`@_silgen_name`-bound Swift shim that takes `UnsafeRawPointer` for the
param, plus all needed type metadata, and dispatches through Swift's
runtime metadata. Pattern hinted at in
`06b-csm-method-own-generic-machinery.md`. Most general; non-trivial.

Only attempt if Paths A and B together leave material surface uncovered.

## Out of scope

- Async-throws `@_silgen_name` wrappers — separate, see
  `08b-entityproperty-init-keypath.md` "Predicted downstream emitter
  surface".
- Generic NSObject subclasses + nested-NSObject classes — separate
  ObjC-runtime concern, see `07-foundation-kvo-attributedstring.md:298`.
- Multi-protocol existential composition in `@_cdecl` — separate, see
  `roadmap.md` "Multi-protocol generic compositions".
- Enabling AppIntents in `validation-libraries.json` — depends on the
  full AppIntents downstream story (roadmap row), not just this gap.

## Acceptance criteria

- Phase 0 report appended to this doc (SIL dump summary + mismatch list).
- KeyPath fixture in `BindingTests/RuntimeTestsApp/KeyPath/
  KeyPathGenericReturnTests.cs` expanded back to four tests; all pass
  on `nuke binding-tests --sim` AND `nuke binding-tests --device`
  (`feedback_device_gate_flake_vs_regression.md`: confirm 0 crashes,
  re-run if needed).
- For each of the 9 AppIntents sites, either: SB0001 removed *and* a
  parallel BindingTests fixture exercises the same shape on sim+device,
  or: a documented reason (per site) why this session's fix does not
  cover it, with a follow-on entry in this doc's "Carry-out" section.
- Zero-regression on unit tests (`nuke test`) and BindingTests pass
  count.
- `nuke validate` only if Path A is taken and changes touch
  cross-cutting emitter logic (per
  `feedback_validate_is_opt_in.md` — not routine).

## Related

- `src/docs/keypath-subsystem/00-overview.md:34-35` — KeyPath ABI at the
  `@_cdecl` boundary (return `+1`, param `@guaranteed`).
- `src/docs/keypath-subsystem/03-keypath-foundation.md:317` — Risk G:
  generic parameter projection.
- `src/docs/keypath-subsystem/04-typed-singleton-emission.md` —
  per-conformer `@_cdecl` pattern reused by Path B.
- `src/docs/keypath-subsystem/12-appintents-0.12-platform-parity.md`
  Residual section — origin of this work.
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs:1414-2151`
  — SB0001 gate logic.
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConsumerSafetyAttributeTests.cs:106-121`
  — `NoWrapperOrThunk_Constructor_EmitsObsoleteWithSB0001` test.
- Memory `feedback_verify_swift_abi_sil.md` — Phase 0 SIL dump
  requirement.
- Memory `feedback_codex_loop_categorical_audit.md` — bisect the 79
  BindingTests sites before patching path-by-path.

## Phase 0 report

### Symbol presence

```bash
nm -gU BindingTests/output/SwiftBindingsTestLib.framework/SwiftBindingsTestLib | swift demangle | grep -i KeyPathGenericSort
```

Both Swift `init`s present at the expected mangling — emitter is targeting
real symbols, the fallback is not failing on lookup. The fault is in the
*shape of the call*, not the symbol resolution.

### SIL (`swiftc -emit-sil`)

```sil
sil @$s...KeyPathGenericSortV2byACyxG...tcfC :
  $@convention(method) <TElement>
    (@owned PartialKeyPath<TElement>, @thin KeyPathGenericSort<TElement>.Type)
    -> @owned KeyPathGenericSort<TElement>

sil [exact_self_class] @$s...KeyPathGenericContainerC2byACyxG...tcfC :
  $@convention(method) <TElement>
    (@owned PartialKeyPath<TElement>, @thick KeyPathGenericContainer<TElement>.Type)
    -> @owned KeyPathGenericContainer<TElement>
```

LLVM IR (`-emit-ir`) for the same canonical inits:

```llvm
; Struct (frozen, single class-ref field — small direct return)
define swiftcc ptr @"$s...KeyPathGenericSortV...tcfC"(ptr %0) #0
;                  ^^^^^^^^^^^^^^^ direct ptr return — no sret!
;                                  %0 = `by` (PartialKeyPath ref, +1)
;                                  $metatype (@thin) is dropped at LLVM (no runtime rep)
;                                  T_metadata is fully elided (extractable from `by`)

; Class (allocating init — also small direct return)
define swiftcc ptr @"$s...KeyPathGenericContainerC...tcfC"(ptr %0, ptr swiftself %1) #0
;                  ^^^^^^^^^^^^^^^ direct ptr return
;                                  %0 = `by`
;                                  %1 = $metatype in swiftself (@thick)
;                                  T_metadata also elided (extractable from `by`)
```

### Mismatch list

What the SB0001 fallback emits today (struct case, regen line 107795):

| Slot | C# P/Invoke arg | Convention | Swift expects |
|---|---|---|---|
| sret / x8 | `SwiftIndirectResult swiftIndirectResult` | `@out` | *(none — direct ptr return)* |
| x0 | `IntPtr byBuffer` | `IntPtr` | `ptr` (PartialKeyPath ref, +1) ✓ |
| x1 | `IntPtr TTElementMetadata` | `IntPtr` | *(none — elided)* |
| x2 | `IntPtr metatype` (Self metadata) | `IntPtr` | *(none — `@thin`, dropped)* |

Net: 4 args (sret + 3) when Swift wants **1 arg, returning ptr directly**.
Heap corruption seeded by writing into a payload buffer the callee never
fills, then leaking/over-releasing the `by` ref because the C# wrapper
moved a `+1` into the void of `swiftIndirectResult`.

Container case (class init) — line 108013 emits 3 args without `swiftself`,
when Swift wants `(ptr %by, ptr swiftself %metatype) -> ptr`. Same shape
of mismatch.

The fallback is **not a fixable parameter-ordering bug**. It is the
generator emitting the *wrong call shape entirely* because it cannot read
LLVM-level optimizations from the source signature alone:

- `@thin` metatypes are runtime-absent (SIL `@thin Self.Type` lowers to
  zero LLVM args).
- T metadata is *sometimes* elided when the runtime can extract it from
  another arg (here, from the `PartialKeyPath` class metadata).
- Frozen single-field structs return small payloads directly, not via
  `sret`.

These decisions are made per-callee inside the Swift compiler. The
generator cannot reliably predict them from a source-level walk of the
type. **Path A is not viable** for the generic-host constructor category.

### Path B' confirmed viable (empirical IR test)

A generic `@_silgen_name` Swift shim with a normalized
`(out-ptr/<explicit args>/T_metadata[/witness_tables])` ABI is stable and
predictable. Tested directly:

```swift
@_silgen_name("_sbw_KeyPathGenericSort_init")
public func _sbw_KeyPathGenericSort_init<T>(
    _ result: UnsafeMutableRawPointer,
    _ by: UnsafeMutableRawPointer,
    _ t: T.Type
) { /* read by, call real init, store via initialize(to:) */ }
```

Emits:

```llvm
define swiftcc void @_sbw_KeyPathGenericSort_init(ptr %0, ptr %1, ptr %T)
```

— predictable normalized ABI: `void` return + explicit out-ptr + N
explicit args + M implicit type-metadata args (one per generic
parameter) + W witness-table args (one per constraint). All `ptr`-sized,
all regular args, **no `sret` decision, no swiftself decision, no
T-metadata elision**. The C# emitter can target this exact signature
mechanically.

This is the same pattern already in production in
`GenericDispatchEmitter` for `(arg: T)` and `(arg: Array<T>)` parameter
shapes; the gap is that the gate at
`GenericDispatchEmitter.cs:312-355` (Constructor case in
`CanEmitStaticDispatch`) rejects parameter shapes outside bare-T /
Array-of-T. `PartialKeyPath<T>` is in that gap.

### Path selection

**Path B' (extend static-factory dispatch to cover KeyPath family +
class-typed T-references)**. Specifically:

1. Widen the constructor gate in
   `GenericDispatchEmitter.CanEmitStaticDispatch` to admit
   `PartialKeyPath<T>`, `KeyPath<T,V>`, `WritableKeyPath<T,V>`,
   `ReferenceWritableKeyPath<T,V>` parameter shapes.
2. Confirm the existing wrapper emitter
   (`ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor`)
   marshals class-typed T-referencing args correctly — `KeyPath` is a
   class, so the `.assumingMemoryBound(to:).pointee` load on the C# side
   must round-trip a class reference (not a value-type payload).
3. Restore the 4-test `KeyPathGenericReturnTests.cs` fixture and validate
   sim+device.
4. Audit the 9 AppIntents 0.12 sites for shapes Path B' covers vs shapes
   that need follow-on work (witness-tables for `where TIntent : AppIntent`
   etc.).

Path B and Path C are not needed: Path B' subsumes B (works for closed
*and* open conformers) and is less invasive than C (no runtime metadata
trampoline).

### Carry-out

Phase 1 lands the KeyPath-family widening of the static-factory constructor
gate (`GenericDispatchEmitter.CanEmitStaticDispatch` +
`ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor`) and
restores the 4-test `KeyPathGenericReturnTests` fixture. Pass-count rise on
both sim (+38 lift, 2239 vs baseline 2201) and device (+10 lift, 2264 vs
baseline 2254). Device run was clean (0 crashes). The sim runner observed
a non-deterministic finalizer-thread SIGSEGV on a different class each run
whose stack signature matches preexisting branch behaviour
(`feedback_device_gate_flake_vs_regression.md`) — unrelated to Phase 1,
re-runs confirm the new fixture itself never abandons. The confirmed
crasher (the KeyPathGenericSort/Container fixtures) is closed.

Phase 4 follow-on (review-driven) tightens the arity check in
`IsKeyPathFamilyOfParentGeneric` to use
`TypeProjectionFactory.GetKeyPathArity` (rejects malformed shapes like
`KeyPath<T>` or `PartialKeyPath<T,V>`) and adds three runtime fixtures
that exercise the 2-arity surface admitted by the widened gate
(`KeyPath<T,Int>`, `WritableKeyPath<T,Int>`,
`ReferenceWritableKeyPath<T,Int>`). Sim 2242 (+41 vs original baseline
2201), device 2267 (+13 vs original baseline 2254); all 7
KeyPathGenericReturnTests pass on both runtimes. Sim baseline ratcheted
to 2242 to remove the lift-slack flagged by Codex review.

The 9 AppIntents 0.12 sites enumerated above are **not** covered by
Phase 1's widening. Per-site audit:

| # | Site | Param shape | Why Phase 1 gate rejects |
|---|---|---|---|
| 1 | `EnumSingleURLRepresentation(StringInterpolation)` | nested-of-parent: `EnumURLRepresentation<TEnum>.StringInterpolation` | Param is `NamedTypeSpec` with `InnerType != null` — not bare-T / Array<T> / KeyPath-family. |
| 2 | `EnumURLRepresentation<TEnum>(StringInterpolation)` | nested-of-parent: `EnumURLRepresentation<TEnum>.StringInterpolation` | Same as #1. |
| 3 | `AppShortcutPhrase<TIntent>(StringInterpolation)` | nested-of-parent: `AppShortcutPhrase<TIntent>.StringInterpolation` | Same as #1. |
| 4 | `IntentParameterSummary(string: ParameterSummaryString<TIntent>, table:)` | top-level generic rooted on method-own generic: `ParameterSummaryString<TIntent>` | Parent (`IntentParameterSummary`) is non-generic; generic is method-own, not parent. Static-factory dispatch is parent-generic-keyed today; method-own-generic constructors don't route through GSF at all. |
| 5 | `AppShortcutsBuilder.BuildBlock(IEnumerable<AppShortcut>)` | concrete-typed static on non-generic host | Out of scope for generic-host GSF. Static-on-non-generic-host is a different mechanism (result-builder dispatch) and warrants its own audit — not addressable by widening the constructor gate. |
| 6 | `AppShortcutsBuilder.BuildBlock(IEnumerable<IEnumerable<AppShortcut>>)` | concrete-typed static on non-generic host | Same as #5. |
| 7 | `IntentURLRepresentation<TIntent>(StringInterpolation)` | nested-of-parent: `IntentURLRepresentation<TIntent>.StringInterpolation` | Same as #1. |
| 8 | `EntityURLRepresentation<TEntity>(StringInterpolation)` | nested-of-parent: `EntityURLRepresentation<TEntity>.StringInterpolation` | Same as #1. |
| 9 | `ParameterSummaryString<TIntent>(StringInterpolation)` | nested-of-parent: `ParameterSummaryString<TIntent>.StringInterpolation` | Same as #1. |

PWT infrastructure note: `MetatypeHelperEmitter.GetResolvablePwtParameterCount`
already threads protocol-witness-table args for *parent-declared*
constraints (`where TEnum : AppEnum`, etc.) through the static-factory
shim's metadata accessor — verified at
`ConstructorWrapperEmitter.cs:988-995, 1176`. The constraint-resolvability
guard (`MetatypeHelperEmitter.HasUnresolvableTypeConformances`) would
still need to be confirmed against `AppEnum`/`AppIntent`/`AppEntity` —
each may carry associated types or `Self` requirements that fail the
resolvable check and block the GSF path entirely.

### Phase 5 ship — nested-of-parent GSF widening (this commit)

1. **Nested-of-parent gate predicate.** `IsNestedTypeOfParentGeneric`
   added to `GenericDispatchEmitter.cs`. Accepts `NamedTypeSpec` with
   non-null `InnerType` whose outer generic args are all parent
   generic params, and whose `InnerType` itself is a leaf (no nested
   `InnerType`, no generic params), **and** whose outer name equals
   the host (parent) type's name. The outer==parent identity gate
   (`OuterMatchesParent`, accepts bare or module-qualified
   short-name matches) was added mid-Phase-5 after a cross-host
   fixture (`CrossHostSiblingStruct<T>(by: CrossHostOuter<T>.Body)`)
   exposed a runtime destroy-witness fault that the original
   outer==parent tests didn't surface. Admitted in the Constructor
   case of `CanEmitStaticDispatch` alongside `IsArrayOfParentGeneric`
   and `IsKeyPathFamilyOfParentGeneric`. The struct-host path uses
   the existing `assumingMemoryBound(to: Self.Inner.self).pointee`
   reconstruction; the class-host path uses `Unmanaged.passRetained`
   for the return. The cross-host shape (outer ≠ host) is documented
   as deferred site #1 below — fixtures live in
   `BindingTests/RuntimeTestsApp/Generics/NestedOfParentTests.cs`
   under `[Skip]` as durable regression markers.
2. **`RenderSwiftTypeSpecWithSugaredNames` audit.** Confirmed:
   `ExistentialBypassEmitter.RenderSwiftTypeSpecCore`
   (`Handler/ExistentialBypassEmitter.cs:1314`) descends into
   `NamedTypeSpec.InnerType` and emits the dotted form
   (`Outer<T>.Inner`) verbatim. The sugared-name regex substitution
   in `WrapperValidation.RenderSwiftTypeSpecWithSugaredNames`
   (`WrapperValidation.cs:2259`) operates on the rendered string and
   is naturally nesting-correct. No code change needed.
3. **`AppEnum`/`AppIntent`/`AppEntity` resolvability.** Audited:
   `MetatypeHelperEmitter.HasUnresolvableTypeConformances` (lines
   187–213) returns `true` only when the parent's generic param has
   a Protocol conformance whose record is in the TypeDatabase **and**
   carries `HasAssociatedTypes` / `HasSelfRequirement`. Unknown
   protocols are silently dropped, matching the legacy filter. No
   AppIntents-specific database entry exists today; the gate is
   correctly fail-closed for any future registration. No change
   needed for Phase 5 — the GSF path is not blocked at the
   parent-type level today.
4. **BindingTests fixtures.** Added in
   `BindingTests/Sources/SwiftBindingsTestLib/Generics/NestedOfParent.swift`:
   - `NestedHostStruct<TElement>(caption: Caption)` — non-frozen
     struct host with nested value-type `Caption` param. Mirrors the
     `EnumURLRepresentation<TEnum>(StringInterpolation)` shape.
   - `NestedHostClass<TElement>(tag: Tag)` — class host with nested
     value-type `Tag` param. Mirrors the class-rooted AppIntents
     declarative type set.
   Both tests pass on `nuke binding-tests --sim` (2244 / +2 vs 2242)
   and `nuke binding-tests --device` (2269 / +2 vs 2267). Generated
   bindings no longer carry `[Obsolete(SB0001)]` on either ctor; both
   route through `CallConvCdecl` to `SBW_…_init_…` GSF shims
   (`_SBW_GSF_BA1CD6CF` for the struct host, `_SBW_GSF_B7D84A00` for
   the class host).

### Per-site closure status (9 AppIntents sites)

| # | Site | Phase | Status |
|---|---|---|---|
| 1 | `EnumSingleURLRepresentation(StringInterpolation)` | — | **Open** — cross-host nested-of-parent (outer = `EnumURLRepresentation<TEnum>` ≠ host = `EnumSingleURLRepresentation`). Phase 5's predicate now requires outer name == parent name; cross-host shapes route to direct-CallConvSwift fallback. The Swift wrapper compiles for the cross-host case, but the destroy witness on `Self`'s stored field faults on Dispose — verified by `NestedOfParentTests.TestCrossHostStruct_*` + `TestCrossHostClass_*` fixtures (kept in-tree with `[Skip]` as regression markers). Future session: diagnose the value-witness mismatch (likely an `initializeMemory(as: Self.self, …)` interaction with non-host outer's witness table during the `any _SBW_GSF_X.Type` existential dispatch) and widen the predicate. |
| 2 | `EnumURLRepresentation<TEnum>(StringInterpolation)` | 5 | Covered by `IsNestedTypeOfParentGeneric` — same shape as `NestedHostStruct<T>.Caption` (outer == parent). SB0001 should drop on AppIntents regen. |
| 3 | `AppShortcutPhrase<TIntent>(StringInterpolation)` | 5 | Same as #2. |
| 4 | `IntentParameterSummary(string: ParameterSummaryString<TIntent>, table:)` | — | **Open**, see "Item 5 follow-on" below. Method-own generic on non-generic Swift parent; pivoted to C# generic class. GSF keying today only supports parent-generic, not method-own. |
| 5 | `AppShortcutsBuilder.BuildBlock(IEnumerable<AppShortcut>)` | — | **Open**, see "Item 6 follow-on" below. Result-builder dispatch, separate mechanism from generic-host GSF. |
| 6 | `AppShortcutsBuilder.BuildBlock(IEnumerable<IEnumerable<AppShortcut>>)` | — | Same as #5. |
| 7 | `IntentURLRepresentation<TIntent>(StringInterpolation)` | 5 | Same as #2. |
| 8 | `EntityURLRepresentation<TEntity>(StringInterpolation)` | 5 | Same as #2. |
| 9 | `ParameterSummaryString<TIntent>(StringInterpolation)` | 5 | Same as #2. |

The 5 outer-equals-parent sites (#2, #3, #7, #8, #9) are closed by
Phase 5's gate widening. Site #1 (cross-host) remains open: the
predicate was tightened mid-Phase-5 to `outer name == parent name`
after the cross-host fixture exposed a destroy-witness fault not
caught by the original outer==parent runtime tests. The remaining 3
sites (#4, #5, #6) require new emission subsystems that are
documented as separate follow-on work below.

### Item 1 follow-on — cross-host nested-of-parent destroy-witness fault (not in this session)

`EnumSingleURLRepresentation(EnumURLRepresentation<TEnum>.StringInterpolation)`
and the synthetic test shapes `CrossHostSiblingStruct<T>(by:
CrossHostOuter<T>.Body)` / `CrossHostSiblingClass<T>(by:
CrossHostOuter<T>.Body)`. The Swift wrapper compiles cleanly — the
emitted GSF protocol extension reconstructs the foreign nested
value via `payload.assumingMemoryBound(to: CrossHostOuter<T>.Body.self).pointee`
and stamps it into `Self`'s storage via
`resultPtr.initializeMemory(as: Self.self, repeating: result, count: 1)`.
At runtime, construction succeeds: the host's stored field reads
back the correct string. **Dispose** then faults inside the host's
value-witness destroy function
(`$s<host>VwxxOrwxxOrwxx` → `<Host>_<arity>_T_REF_Dispose`),
suggesting that the witness table sees a stamp that didn't go
through the proper non-trivial copy/initialize sequence for the
foreign outer's `Body` field.

Why this only surfaces for cross-host:

- Outer==parent (`NestedHostStruct<T>(caption: NestedHostStruct<T>.Caption)`):
  the inner type lives under `Self`'s own metadata, so the runtime
  resolution of `Self.Caption.self` and the witness for `Caption`
  are emitted alongside `Self` and agree on layout/ARC discipline.
- Cross-host (`EnumSingleURLRepresentation(EnumURLRepresentation<TEnum>.StringInterpolation)`):
  the inner type is anchored to a *different* generic parent's
  metadata. The static-factory protocol extension is keyed on the
  host (`EnumSingleURLRepresentation`), but the inner-type metadata
  flows from `EnumURLRepresentation<TEnum>` — an existential
  dispatch through `any _SBW_GSF_X.Type` doesn't carry the foreign
  outer's witness table context, so the stamped storage looks
  bitwise correct but isn't properly retained per the host's witness
  table contract.

Hypotheses to explore in the follow-on:

1. The cross-host case may need a manual `withUnsafePointer(to:)`
   + `UnsafeRawPointer.copyMemory` round-trip (instead of
   `initializeMemory(as: Self.self, …)`) so the host's witness table
   takes ownership through its own copy witness rather than treating
   the foreign value as already-owned.
2. Alternatively, the GSF protocol may need to thread the foreign
   outer's witness table explicitly, paralleling how the
   method-own-generic case (site #4) needs a phantom-box pattern to
   carry method-own metadata.
3. Worst case, cross-host nested-of-parent stays on direct
   `CallConvSwift` SB0001 fallback permanently and we accept the
   SB0001 warning for site #1 only.

`NestedOfParentTests.TestCrossHostStruct_AcceptsForeignOuterNested`
and `TestCrossHostClass_AcceptsForeignOuterNested` are kept in-tree
under `[Skip]` so the day this fix lands the `[Skip]` drops, the
predicate widens, and the tests turn green as the durable gate.

### Item 5 follow-on — method-own-generic constructors (not in this session)

`IntentParameterSummary(string: ParameterSummaryString<TIntent>, table:)`.
Swift parent is non-generic; init has its own `<Intent: AppIntent>`
generic. C# pivots the parent into `IntentParameterSummary<TIntent>`
to host the generic param (C# constructors can't be generic). All
the C#-side plumbing already exists (TypeMetadata threading, PWT via
`GetAppIntentPWT(...)`); the gap is that
`WrapperValidation.HasMethodOwnGenericParameters` is a hard reject
for the wrapper emission path, so the Swift shim is never written
and the C# side falls back to direct `CallConvSwift` against the
mangled Swift init symbol.

Why this isn't a one-predicate widen of GSF:

- GSF emits `@_cdecl` Swift shims dispatched through type-erased
  `as! any _SBW_GSF_*.Type`. `@_cdecl` rejects generic functions,
  so the existing pattern cannot host a method-own generic.
- `@_silgen_name` with an explicit generic signature on Swift's side
  is allowed, but produces a CallConvSwift symbol with explicit
  T-metadata + PWT register threading. None of today's
  `@_silgen_name` emissions in the generator are generic (grep
  `BindingTests/output/SwiftBindingsTestLib.Wrapper.swift`); landing
  one means re-targeting the C# P/Invoke side too.

**Recommended approach (Codex-validated, ≤1 day not realistic but
single-feature scope):** synthesize a phantom Swift box type in the
wrapper library that adopts an existing-style `_SBW_GSF_<hash>`
protocol, parameterized by the method-own generic. Sketch:

```swift
struct _SBW_GSF_IntentParameterSummary_Box<TIntent: AppIntent>:
       _SBW_GSF_<hash> {
    static func _sbw_create_<hash>(
        resultPtr: UnsafeMutableRawPointer,
        string: UnsafeRawPointer,
        table: UnsafeRawPointer
    ) {
        let stringVal = string
            .assumingMemoryBound(to: ParameterSummaryString<TIntent>.self)
            .pointee
        let table = ... // unpack Optional<String>
        let result = IntentParameterSummary(string: stringVal, table: table)
        resultPtr.initializeMemory(as: IntentParameterSummary.self,
                                   repeating: result, count: 1)
    }
}

@_cdecl("SBW_AppIntents_IntentParameterSummary_init_<hash>")
public func _sbw_init_<hash>(
    _ resultPtr: UnsafeMutableRawPointer,
    _ string: UnsafeRawPointer,
    _ table: UnsafeRawPointer,
    _ intentMeta: UnsafeRawPointer
) {
    let metatype = unsafeBitCast(intentMeta, to: Any.Type.self)
        as! any _SBW_GSF_<hash>.Type
    metatype._sbw_create_<hash>(resultPtr: resultPtr,
                                 string: string,
                                 table: table)
}
```

This keeps the Swift shim non-generic `@_cdecl` and lets the C#
side use `CallConvCdecl` like today, with the phantom box absorbing
the generic. The C#-pivoted `IntentParameterSummary<TIntent>`
already gives the consumer a place to thread `TIntent` metadata /
PWT — both already plumbed at the call site (`GetAppIntentPWT(...)`
already emitted, see AppIntents 0.12 regen line 11536).

Gate predicate: non-generic Swift parent, constructor only, exactly
one method-own generic param, C# pivot generic available, generic
param's protocol constraints resolvable (`HasUnresolvableTypeConformances`
returns false), constructor signature references the method-own
generic *only* through admitted value shapes (initially just
`ParameterSummaryString<TIntent>` — i.e. the same nested-of-parent
shape Phase 5 admits, but rooted on the method-own generic rather
than a parent generic). Reject closures, inout, variadic, same-type
constraints on the method-own generic.

Closest existing emitter to mirror: `ConstructorWrapperEmitter
.EmitGenericStaticFactoryConstructor` (Phase 1 / Phase 5 paths),
with method-own generic discovery borrowed from
`Handler/MethodGenericBridgeEmitter.cs` (not its existential-opening
emission body — only the generic-param walking).

Load-bearing risks: `AppIntent` PWT resolvability (no
`AppIntents` database entry today, so the gate is silently
fail-closed — see Item 3 audit above); the metadata-accessor ABI if
`(metadata + PWT)` crosses the >3 register threshold; and
ownership / copy-destroy semantics of `ParameterSummaryString<TIntent>`
when read via `.assumingMemoryBound(to:).pointee` (non-frozen
struct projection — validate copy/destroy on sim+device).

Scope assessment: not a one-day patch even with the phantom-box
shortcut. New gate predicate, new emitter pass (phantom-box type
declaration in the wrapper library is new), method-own-generic
discovery wired into the existing GSF emitter, PWT register-count
audit, sim+device runtime fixtures, and end-to-end AppIntents
regen smoke. Multi-day. Not attempted in this session.

### Item 6 follow-on — `AppShortcutsBuilder.BuildBlock` (not in this session)

`AppShortcutsBuilder.BuildBlock(IEnumerable<AppShortcut>)`,
`AppShortcutsBuilder.BuildBlock(IEnumerable<IEnumerable<AppShortcut>>)`.
The Swift declaration is `@resultBuilder struct AppShortcutsBuilder`
with `static func buildBlock(_:_:)` overloads. Result builders are
a Swift compile-time DSL feature — calls are usually generated by
the Swift compiler at the use-site, not invoked directly by external
callers. The generator emits the methods as ordinary `static`
methods on a non-generic host.

Codex audit conclusion: the SB0001 fallback here is **not** a result-builder
filter — there is no result-builder-specific gate in the wrapper-emit
path. The gating reason is the **variadic parameter** rejection in
`WrapperValidation.HasNoWrapperOrThunk` (returns `variadic_params`
when `methodDecl.HasVariadicParameter` is true). Swift `buildBlock`
methods are commonly variadic (`func buildBlock(_ components: AppShortcut...)`),
and the ABI JSON exposes the variadic shape as an array. The
generator surfaces it to C# as `IEnumerable<AppShortcut>` but cannot
synthesise the `@_cdecl` wrapper that would call the Swift variadic
parent with splat syntax.

Closing this surface is therefore a localized fix to the variadic
wrapper path in `MethodWrapperEmitter` (teach it to forward
variadic array params with splat syntax, validate array projection
lifetime/ownership), with the nested `IEnumerable<IEnumerable<…>>`
case as one additional collection depth. Not GSF. Not method-own
generic. Single-engineer-day plausible *if* return-type modeling
and DSL-builder lowering don't surface separate issues during
implementation — see `src/docs/keypath-subsystem/12-appintents-0.12-platform-parity.md:7`
and `:274` for the prior variadic-pack regression gating context.

Scope assessment: variadic-pack wrapper subsystem, not generic-host
GSF. Single-engineer-day plausible; multi-day if return-type
modeling reveals further gaps. Not attempted in this session
because it's a different subsystem than the one this doc tracks.

### Out of scope for this doc (unchanged)

Already documented in this doc's "Out of scope" section above:
async-throws `@_silgen_name` wrappers, generic NSObject subclasses,
multi-protocol existential composition, AppIntents
validation-libraries enrollment.

### Phase 1 implementation summary (this session)

- `GenericDispatchEmitter.CanEmitStaticDispatch` (Constructor case):
  admit `IsKeyPathFamilyOfParentGeneric(arg.SwiftTypeSpec,
  genericParamNames)` alongside bare-T and Array<T>.
- `GenericDispatchEmitter.IsKeyPathFamilyOfParentGeneric` (new): delegates
  to `TypeProjectionFactory.IsKeyPathFamily(named.Name)` and asserts the
  root generic argument is a parent generic param.
- `ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor`:
  branch on `IsKeyPathFamilyOfParentGeneric` to use
  `Unmanaged<…>.fromOpaque(label).takeUnretainedValue()` (class ref)
  instead of `…assumingMemoryBound(to:).pointee` (value type) for the
  reconstruction step. KeyPath family is always a Swift class.
- `BindingTests/RuntimeTestsApp/KeyPath/KeyPathGenericReturnTests.cs`:
  4 tests (sort.by + sort.lookup + container.by + container.lookup)
  exercising `KeyPathGenericSort<BoxKP>` (frozen-struct host) and
  `KeyPathGenericContainer<BoxKP>` (class host) constructed via the
  GSF path with `KeyPathFactory.MakeReferenceWritableBoxNPath()` seed.

Generated output verification: `BindingTests/output/SwiftBindingsTestLib.cs`
constructors at `KeyPathGenericSort` and `KeyPathGenericContainer` no
longer carry the `[Obsolete(SB0001)]` attribute and now route through
`CallConvCdecl` to `SBW_SwiftBindingsTestLib_KeyPathGenericSort_init_…`.
`BindingTests/output/SwiftBindingsTestLib.Wrapper.swift` emits
`extension SwiftBindingsTestLib.KeyPathGenericSort: _SBW_GSF_F0B4D148`
with the `Unmanaged<PartialKeyPath<TElement>>.fromOpaque(by)
.takeUnretainedValue()` reconstruction.
