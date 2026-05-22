# Session 14 — SB0001 remaining surface (post-doc-13)

Follow-on from doc 13. Doc 13's Phase B' (extend the generic static factory
gate) closed the parent-generic-host SB0001 surface — KeyPath family,
`Array<T>`, and outer-equals-parent nested-of-parent. That covered 5 of the
9 AppIntents 0.12 SB0001 sites plus the KeyPath constructor crasher.

The remaining 4 AppIntents sites are NOT variations of the same generic-host
gap that doc 13 set out to close. They span **three different emission
mechanisms** that were mis-grouped as one category in doc 13's original
audit:

| # | Site | True category |
|---|---|---|
| 1 | `EnumSingleURLRepresentation(EnumURLRepresentation<TEnum>.StringInterpolation)` | Cross-host nested-of-parent (outer ≠ host). Same GSF emitter path doc 13 widened, but the host's value-witness destroy faults on Dispose because the foreign outer's witness table doesn't flow through the `any _SBW_GSF_X.Type` existential dispatch. |
| 4 | `IntentParameterSummary(string: ParameterSummaryString<TIntent>, table:)` | Method-own-generic on a non-generic Swift parent. Doc 13's GSF is parent-generic-keyed; method-own-generic constructors need a phantom-box Swift type to carry the generic while keeping the `@_cdecl` shim non-generic. |
| 5 | `AppShortcutsBuilder.BuildBlock(IEnumerable<AppShortcut>)` | Variadic-pack splat. Not generic at all — Swift `buildBlock(_ components: AppShortcut...)` is rejected by `WrapperValidation.HasNoWrapperOrThunk` because the generator can't synthesise a `@_cdecl` wrapper that splats a C# array into a Swift variadic. |
| 6 | `AppShortcutsBuilder.BuildBlock(IEnumerable<IEnumerable<AppShortcut>>)` | Same as #5, one collection depth deeper. |

This session closes all three. **No deferrals.** If a sub-problem turns out
to span its own subsystem (the way doc 13 turned into 4-mechanisms-in-one),
re-scope this doc and keep working — do not spawn doc 15.

## Acceptance criteria

- All 4 remaining AppIntents 0.12 sites have **SB0001 removed** from the
  generated `AppIntents.cs` regen — verified by running the AppIntents
  generation against the published xcframework and grepping the output.
- BindingTests fixtures for each shape, passing on `nuke binding-tests
  --sim` AND `nuke binding-tests --device`:
  - **Site #1**: drop the `[Skip]` on `NestedOfParentTests.TestCrossHostStruct_*`
    and `TestCrossHostClass_*` (already in-tree as durable regression markers
    from doc 13's Phase 5 tightening).
  - **Site #4**: new `MethodOwnGenericCtorTests` exercising a generic
    constructor on a non-generic Swift parent with the same shape as
    `IntentParameterSummary` — nested-of-method-own-generic param + PWT.
  - **Sites #5, #6**: new `VariadicBuildBlockTests` exercising a static
    method with variadic-pack params on a non-generic host, plus the
    nested `[[T]]` shape.
- Zero-regression on unit tests (`nuke test`) and BindingTests pass count.
- Sim baseline ratchets up by the new fixtures' count (mirroring doc 13's
  +5 for KeyPath family + +2 for nested-of-parent).
- `nuke validate` only if Item 2 (phantom-box emitter) cross-cuts to other
  emitter paths — single-doc surgery doesn't warrant a full validate sweep.

## Item 1 — Cross-host nested-of-parent destroy-witness fault

Diagnosed in doc 13 (Item 1 follow-on). Recap:

- Wrapper compiles cleanly; construction succeeds; **Dispose** faults in
  the host's value-witness destroy (`$s<host>VwxxOrwxxOrwxx`).
- Predicate (`GenericDispatchEmitter.IsNestedTypeOfParentGeneric` →
  `OuterMatchesParent`) currently requires module-qualified outer == parent.
  Cross-host shapes fall through to direct `CallConvSwift` (SB0001).

### Hypothesis ordering (apply TDD per `feedback_tdd_for_regression_fixes.md`)

1. **First attempt — copyMemory through host's copy witness.** Change
   the GSF protocol extension's reconstruction from
   `resultPtr.initializeMemory(as: Self.self, repeating: result, count: 1)`
   to a path that goes through `Self`'s copy witness explicitly. Two
   candidate shapes:
   ```swift
   // Option A: stamp the value into a temporary, then have the host's
   // value-witness copy take ownership.
   withUnsafePointer(to: result) { src in
       UnsafeMutableRawPointer(mutating: resultPtr).initializeMemory(
           as: Self.self, from: src, count: 1)
   }

   // Option B: tell Swift's runtime to use the host's copy witness explicitly
   // via withUnsafeMutablePointer + the existing initialize(to:) path that
   // outer==parent uses today.
   resultPtr.assumingMemoryBound(to: Self.self).initialize(to: result)
   ```
   Verify which (if either) routes the foreign outer's `Body` field
   through its own retain/copy discipline before stamping into `Self`'s
   storage. Use SIL dump (`feedback_verify_swift_abi_sil.md`) — don't guess.

2. **Second attempt — thread foreign outer's witness table explicitly.**
   If hypothesis 1 fails, extend the GSF metadata accessor signature to
   carry the foreign outer's metadata alongside `Self`'s. Pattern mirrors
   the `<Outer<T>.Inner>` emission path in `MethodGenericBridgeEmitter`
   for instance methods; constructors haven't needed it because outer ==
   parent was the only shape admitted.

3. **Final fallback — leave site #1 on direct `CallConvSwift` SB0001.**
   Only if hypotheses 1 and 2 both fail and SIL evidence shows the
   destroy witness cannot be satisfied through the GSF dispatch shape.
   Document the runtime evidence in this doc's Phase 0 report.

### Predicate widen

Once a hypothesis lands, drop the `OuterMatchesParent` rejection in
`GenericDispatchEmitter.IsNestedTypeOfParentGeneric`. The
`NestedOfParentTests.TestCrossHostStruct/Class*` `[Skip]` attributes
come off in the same commit.

### Gating evidence

- SIL of `EnumSingleURLRepresentation.init(stringInterpolation:)` vs
  `EnumURLRepresentation<TEnum>.init(stringInterpolation:)` —
  compare destroy-witness call paths on the indirect-result slot.
- LLVM IR (`-emit-ir`) of the GSF wrapper before/after the
  reconstruction change.
- Sim + device runtime fixtures (already in-tree, just un-`[Skip]`).

## Item 2 — Method-own-generic constructor (phantom-box pattern)

Codex-validated design from doc 13 Item 5 follow-on. Recap:

- Swift parent is non-generic; constructor has its own
  `<Intent: AppIntent>` method-own generic.
- C# generator already pivots the parent into `IntentParameterSummary<TIntent>`
  to host the generic param.
- Gap: `WrapperValidation.HasMethodOwnGenericParameters` is a hard
  reject for the wrapper emission path, so the Swift shim is never
  written and C# falls back to direct `CallConvSwift`.

### Design (phantom-box GSF variant)

The `@_cdecl` shim must stay non-generic (`@_cdecl` rejects generic
functions). Synthesize a Swift phantom-box type in the wrapper library:

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
    _ intentMeta: UnsafeRawPointer,
    _ intentPWT: UnsafeRawPointer
) {
    let metatype = unsafeBitCast(intentMeta, to: Any.Type.self)
        as! any _SBW_GSF_<hash>.Type
    metatype._sbw_create_<hash>(resultPtr: resultPtr,
                                 string: string,
                                 table: table)
}
```

### Gate predicate

In `GenericDispatchEmitter`:

- Non-generic Swift parent + at least one method-own generic param.
- Constructor only (no instance methods in this session — they're a
  different shape).
- Exactly one method-own generic param (multiple is a separate item;
  not in scope).
- C# pivot generic is available on the parent type (already true for
  AppIntents — the generator does this today).
- Generic param's protocol constraints are resolvable
  (`HasUnresolvableTypeConformances` returns false). For `AppIntent`
  this requires either: (a) confirming the protocol is silently
  dropped today and the GSF path admits it via the fail-closed gate
  (Item 3 audit from doc 13 confirms this), or (b) registering a
  TypeDatabase entry for `AppIntent` if PWT threading needs it.
- Constructor signature references the method-own generic *only*
  through admitted value shapes — initially just nested-of-method-own-generic
  (`ParameterSummaryString<TIntent>`), same shape doc 13 Phase 5
  admits for parent-generic.
- Reject closures, inout, variadic, same-type constraints on the
  method-own generic.

### Emitter pass

1. `WrapperValidation.HasMethodOwnGenericParameters` — flip from
   hard-reject to a gate that admits when the new predicate passes.
2. `ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor` —
   branch: if method-own-generic, emit the phantom-box pattern; else
   the existing parent-generic GSF path.
3. New phantom-box emitter helper alongside `EmitGenericStaticFactoryConstructor`
   that declares the box type, the protocol extension, and the
   `@_cdecl` shim.
4. C# side: the existing P/Invoke generation already threads
   `TypeMetadata.GetTypeMetadataOrThrow<TIntent>()` and
   `GetAppIntentPWT(...)` — verify the register count fits the
   metadata-accessor ABI threshold.

### Load-bearing risks (from doc 13)

- `AppIntent` PWT resolvability — verify the silent-drop assumption
  via test fixture, not by reading code.
- Metadata-accessor ABI if `(metadata + PWT)` crosses the >3 register
  threshold for AAPCS64.
- Copy/destroy of `ParameterSummaryString<TIntent>` via
  `.assumingMemoryBound(to:).pointee` — non-frozen-struct projection;
  validate on sim+device, not just compile.

### Gating evidence

- BindingTests fixture: `MethodOwnGenericHost` (non-generic Swift
  parent) with `init<T: SomeProto>(value: NestedString<T>)` shape.
- SIL + LLVM IR for the phantom-box dispatch path.
- AppIntents regen showing SB0001 dropped on site #4.

## Item 3 — Variadic-pack result-builder splat

Doc 13 Item 6 follow-on. Recap:

- `AppShortcutsBuilder.BuildBlock(_ components: AppShortcut...)` is a
  Swift variadic. ABI JSON exposes it as an array; C# surfaces it as
  `IEnumerable<AppShortcut>`. `WrapperValidation.HasNoWrapperOrThunk`
  returns `variadic_params` and the `@_cdecl` wrapper is never
  synthesised.
- Not generic. Not method-own-generic. Not a GSF concern.

### Design

Teach `MethodWrapperEmitter` to forward a `[T]` array param to a
Swift variadic call site with splat syntax:

```swift
@_cdecl("SBW_AppIntents_AppShortcutsBuilder_buildBlock_<hash>")
public func _sbw_buildBlock_<hash>(
    _ resultPtr: UnsafeMutableRawPointer,
    _ componentsBuffer: UnsafeRawPointer,
    _ componentsCount: Int
) {
    let buf = componentsBuffer.assumingMemoryBound(to: AppShortcut.self)
    let components = Array(UnsafeBufferPointer(start: buf, count: componentsCount))
    let result = AppShortcutsBuilder.buildBlock(components)
    // ^^^ requires the Swift compiler to accept passing [AppShortcut]
    //     where AppShortcut... is expected. It DOES NOT in general —
    //     splat is `buildBlock(components[0], components[1], ...)`
    //     and Swift doesn't have a runtime splat operator. The
    //     wrapper has to *unconditionally* call the array-arity
    //     overload (`@_disfavoredOverload buildBlock(_ components: [AppShortcut])`)
    //     if Swift's `@resultBuilder` machinery emits one. Validate
    //     against the actual AppIntents 0.12 SDK swiftinterface.
    resultPtr.initializeMemory(as: type(of: result),
                               repeating: result, count: 1)
}
```

### Verify before emitting

- Read `AppIntents.swiftinterface` for `AppShortcutsBuilder` — does
  it expose an `[AppShortcut]`-array overload alongside the variadic?
  Result builders typically do (`buildBlock(_:)` for the empty case
  + variadic for N≥1; sometimes an explicit array overload).
- If yes: the wrapper can target the array overload directly and
  ignore the variadic shape entirely.
- If no: the wrapper has to inline-construct a tuple at compile time
  by enumerating call-site arities (`buildBlock(c[0], c[1])`,
  `buildBlock(c[0], c[1], c[2])`, etc.). This is a hard rejection —
  Swift doesn't have a runtime splat — and would mean closing this
  surface requires per-arity emission rather than a single wrapper.

### Gate predicate (in `MethodWrapperEmitter`)

- Static method (not instance, not constructor).
- Non-generic Swift parent.
- Exactly one variadic param of an array-projectable element type.
- Return type renderable via existing
  `RenderSwiftTypeSpecWithSugaredNames`.

### Sites #5 vs #6 difference

Site #6 takes `IEnumerable<IEnumerable<AppShortcut>>` — variadic of
arrays. The wrapper has to flatten one level of variadic but preserve
the inner array. Same emission path with an extra projection step
for the element type. Test fixture exercises both depths.

### Gating evidence

- AppIntents regen showing SB0001 dropped on sites #5 and #6.
- BindingTests `VariadicBuildBlockTests` fixture: static method on
  non-generic host taking `T...` and `[T]...` shapes.

## Phase 0 — diagnose before architecting

Per `feedback_verify_swift_abi_sil.md` and the doc-13 precedent:
dump SIL and LLVM IR for one representative shape from each Item
before emitter changes. Specifically:

- Item 1: SIL of `EnumSingleURLRepresentation.init(stringInterpolation:)`
  + LLVM IR of the current cross-host GSF wrapper (the [Skip]'d
  fixture's `@_cdecl` shim).
- Item 2: SIL of `IntentParameterSummary.init<Intent: AppIntent>(string:table:)`
  + the existing parent-generic GSF wrapper output (for comparison).
- Item 3: ABI JSON for `AppShortcutsBuilder.buildBlock` (both
  overloads if both exist) — the variadic-vs-array question is
  parser-level, not SIL-level.

Append a Phase 0 report to this doc before opening implementation.

## Out of scope

Same boundaries as doc 13's "Out of scope":

- Async-throws `@_silgen_name` wrappers — see `08b-entityproperty-init-keypath.md`.
- Generic NSObject subclasses + nested-NSObject — see
  `07-foundation-kvo-attributedstring.md`.
- Multi-protocol existential composition in `@_cdecl` — see `roadmap.md`.
- AppIntents `validation-libraries.json` enrollment — depends on
  the full downstream story, not this gap.
- Method-own-generic instance methods (not just constructors). Item 2
  scopes only constructors; instance methods would re-trigger Phase 0
  for a different signature shape and are deferred — but to roadmap,
  not to a Session 15.

## Carry-out

This is intended to be the last doc in the SB0001 generic-host
sequence. Doc 13 closed parent-generic. Doc 14 closes cross-host,
method-own-generic constructors, and variadic-pack splat. The 79
BindingTests SB0001 sites that doc 13 enumerated will have their
final accounting tallied at the end of this session — any remaining
SB0001 in BindingTests output is an explicit category-skip
(documented in this doc's Phase 0 report), not silent.

## Related

- `13-sb0001-generic-host-wrapper-gap.md` — direct predecessor; sets
  the GSF architecture this doc extends.
- `src/Swift.Bindings/src/Emitter/StringEmitter/GenericDispatchEmitter.cs`
  — `IsNestedTypeOfParentGeneric` + `OuterMatchesParent` (Item 1
  predicate widen), Constructor case in `CanEmitStaticDispatch`
  (Item 2 gate addition).
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
  — `EmitGenericStaticFactoryConstructor` (Items 1 + 2).
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs`
  — variadic-pack wrapper path (Item 3).
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs`
  — `HasMethodOwnGenericParameters` (Item 2), `HasNoWrapperOrThunk`
  variadic-params reject (Item 3).
- `BindingTests/RuntimeTestsApp/Generics/NestedOfParentTests.cs` —
  `[Skip]`'d cross-host fixtures (Item 1 un-`[Skip]`).
- Memory `feedback_no_autonomous_defer.md` — single session, no
  further "Session 15."
- Memory `feedback_tdd_for_regression_fixes.md` — fixture-first per item.
- Memory `feedback_verify_swift_abi_sil.md` — Phase 0 SIL dump.
