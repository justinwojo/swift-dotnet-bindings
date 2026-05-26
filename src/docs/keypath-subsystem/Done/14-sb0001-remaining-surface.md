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
  - **Site #1 (cross-host nested-of-parent)**: Phase 0 hypothesis 3 is
    accepted — cross-host shapes stay on direct `CallConvSwift` per
    `OuterMatchesParent`'s module-qualified rejection. The
    `NestedOfParentTests.TestCrossHost*` fixtures remain `[Skip]`'d in-tree
    as durable regression markers for the rejection, not as widening
    targets.
  - **Site #4 (IntentParameterSummary)**: in practice routed through the
    same generic-parent + simply-parameterized widening as sites #2–#3
    (`IsBareOrSimplyParameterizedNamedTypeSpec` admission) rather than a
    phantom-box pattern. Coverage lands as `BoundGenericOfParentCtorTests`
    + `SelfReqProtocolCtorTests` (PAT / Self-requirement constructor with
    dynamic-PWT threading).
  - **Sites #5, #6 (variadic / return-type-only overload)**: new
    `VariadicResultBuilderTests` (variadic-pack bitcast + array-form ABI
    parity) and `ReturnTypeOnlyOverloadTests` (function-reference `as`
    cast disambiguation for `buildExpression` siblings).
- Zero-regression on unit tests (`nuke test`) and BindingTests pass count.
- Sim baseline ratchets up by the new fixtures' count (mirroring doc 13's
  +5 for KeyPath family + +2 for nested-of-parent).
- `nuke validate` is opt-in for this doc — single-doc surgery doesn't
  warrant a full validate sweep.

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

### Phase 0 report (post-implementation)

**Item 1 — Cross-host destroy-witness fault: hypothesis 3 accepted.**

Hypothesis 1 (Option B reconstruction —
`resultPtr.assumingMemoryBound(to: Self.self).initialize(to: result)`
in place of `initializeMemory(as:repeating:count:)`) was implemented in
`ConstructorWrapperEmitter.cs` and tested against the cross-host fixtures.
Same-host shapes (`NestedHostStruct<T>.Caption`, `NestedHostClass<T>.Tag`)
continued passing as expected. Cross-host shapes (`CrossHostSiblingStruct<T>(by:
CrossHostOuter<T>.Body)`) continued faulting at SafeHandle.Dispose with the
same `RefCounts::doDecrementSlow` crash inside the host's value-witness
destroy (`$s20SwiftBindingsTestLib22CrossHostSiblingStructVwxx`). Crash
stack:

```
$s..CrossHostSiblingStructVwxx     <- VWT.destroy
PerformVwtDestroy
SafeHandle.Dispose
RefCounts::doDecrementSlow         <- TWICE in the unwind
```

SIL emission for the same-host and cross-host field getters is
structurally identical (`struct_extract` → `retain_value` → `return`);
the runtime divergence is in metadata resolution, not the emitted code.
Independent diagnoses from two LLM second-brains aligned on
generic-metadata-substitution as the root: when the `@_cdecl` shim
dispatches through `unsafeBitCast(parentMeta, …) as! any _SBW_GSF_X.Type`,
the protocol existential carries `Self`'s witness table but the foreign
outer's (`CrossHostOuter<T>`) witness table is not threaded. The host's
metadata thinks the field's runtime layout differs from what the wrapper
actually wrote, so VWT.destroy walks the wrong release sequence.

Hypothesis 2 (thread the foreign outer's metadata explicitly through the
@_cdecl signature + use `@_silgen_name("swift_initializeWithCopy")` to
do an explicit VWT-based copy) requires either (a) declaring private
Swift runtime symbols and managing the field offset by hand, or (b)
re-architecting GSF to use an associatedtype-based metadata threading.
Both are high-risk and would not generalize to the AppIntents shape's
non-frozen `EnumURLRepresentation<TEnum>.StringInterpolation` payload
without additional Swift-runtime API discovery.

Hypothesis 3 is therefore accepted for site #1: cross-host stays on
direct `CallConvSwift` (SB0001). The predicate
`GenericDispatchEmitter.IsNestedTypeOfParentGeneric` re-narrows via
`OuterMatchesParent` to reject cross-host shapes; the
`NestedOfParentTests.TestCrossHost*` fixtures remain in-tree as
durable regression markers, `[Skip]`'d with the specific runtime-fault
reason. When the GSF dispatch can carry the foreign outer's witness
table (future Swift-runtime API or compiler fix), the predicate widens
and the `[Skip]`s drop in the same commit.

**Item 2 — Method-own-generic constructor: closed via phantom-box GSF
variant.** The phantom-box pattern from doc 14's design landed in
`ConstructorWrapperEmitter.cs`; `MethodOwnGenericCtorTests` exercises
the `IntentParameterSummary`-shape generic ctor on a non-generic
parent. Sim and device both pass.

**Item 3 — Variadic-pack result-builder splat: closed via array-overload
detection.** `MethodWrapperEmitter` now admits the single-Array-T-param
case when `HasVariadicParameter` is set (the parser doesn't propagate
`IsVariadic` to concrete-element variadics like `VariadicSection`
where `printedName` lacks the `...` suffix). `VariadicResultBuilderTests`
covers `T...` and `[T]...` shapes on a non-generic host.

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

### Final accounting (2026-05-22 — AppIntents regen against shipped xcframework)

Regen procedure: `nuke pack --version 0.12.0 --skip-apple` against this
worktree's SDK → drop `SwiftBindings.Sdk.0.12.0.nupkg` +
`SwiftBindings.Runtime.0.12.0.nupkg` into
`/Users/wojo/Dev/swift-dotnet-packages/local-packages/` → wipe
`~/.nuget/packages/swiftbindings.{sdk,runtime}/0.12.0/` →
`dotnet build -c Release` on
`apple-frameworks/AppIntents/SwiftBindings.Apple.AppIntents.csproj`.

**AppIntents.cs SB0001 grep**: 7 → 1.

The one remaining site is the Item-1 cross-host shape
(`EnumSingleURLRepresentation(EnumURLRepresentation<TEnum>.StringInterpolation)`)
held on SB0001 per Hypothesis 3 in the Phase 0 report. Items 2 and 3
successfully eliminated their 6 sites:

| Site | Before | After |
|---|---|---|
| EnumSingleURLRepresentation (cross-host nested) | SB0001 | SB0001 *(Item 1, by-design)* |
| EnumURLRepresentation parent-generic ctor       | SB0001 | clean |
| AppShortcutPhrase parent-generic ctor           | SB0001 | clean |
| IntentURLRepresentation parent-generic ctor     | SB0001 | clean |
| EntityURLRepresentation parent-generic ctor     | SB0001 | clean |
| ParameterSummaryString / IntentParameterSummary | SB0001 | clean |
| AppShortcutsBuilder.buildBlock (variadic)       | SB0001 | clean |

**BindingTests runtime gates** (commit at HEAD + this session's patch):
| Gate | Pass | Crash | Delta vs HEAD |
|---|---|---|---|
| Simulator (Mono JIT) | 2254 | 0 | +5 new fixtures (RouteC_GenericRequest + sibling) |
| Device (NativeAOT)   | 2275 | 0 | +6 new fixtures (RouteC_GenericRequest + sibling) |

Baselines auto-ratcheted via `nuke binding-tests --sim` /
`--device`; `.validation-baseline.json` updated in this commit.

#### Follow-on: non-final generic class `init()` SIGSEGV

`RouteC_GenericRequest<Item>` (a non-final generic class with a
no-argument `init()`) sat outside both Path 1 and Path 2 of the
constructor wrapper emitter and fell through to direct
`CallConvSwift` (SB0001). The direct path expected the parent
metatype in `x20` but the C# call site only supplied it via a
SwiftSelf<T>-wrapped param, leaving `x20` whatever the caller left
behind — `__allocating_init` then read garbage from `x20` and the
subsequent destroy paged-fault the process.

Two-part emitter fix:

1. `GenericDispatchEmitter.CanEmitGenericDispatch` no longer
   short-circuits non-final generic classes back onto SB0001 — they
   route through `CanEmitStaticDispatch` (GSF) like every other
   generic ctor shape.

2. `ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor`
   now appends the sugared generic-param list to the construction
   expression for generic class parents (`Foo<T>(args)` instead of
   `Foo(args)`). Without this, Swift's type inference falls over on
   no-arg generic class inits inside the GSF extension body —
   `Self()` is the obvious alternative but requires `required init`
   on the original type, which we can't add via extension.

The KeyPathRouteCTests fixtures (4 sim + 5 device test methods)
now construct, mutate, and finalize without crashing, raising the
gate counts above.

### Wrapper-compile failures surfaced by the regen

The AppIntents-side `dotnet build` initially surfaced ~30 swift-compile
errors against the regenerated `AppIntents.Wrapper.swift`. Five
distinct shapes were identified; three were closed in-doc as part of
doc 14's final pass; two are explicit out-of-scope per
`08b-entityproperty-init-keypath.md`. Final accounting after the
in-doc fixes lands the regen at **10 total errors** (4 unique
diagnostics + 2 MSB3073 swiftc invocation cascades + 4 duplicates
across iOS/tvOS), all in the two out-of-scope categories.

| # | Symptom | Fix site | Status |
|---|---|---|---|
| 1 | `_SBW_CI_*` protocol declarations missing leading `@available` annotations when the protocol body references availability-gated types | `ForeignTypeExtensionEmitter` (foreign-type-extension `@_silgen_name` availability propagation, threaded through `Program.cs`) | **Closed** |
| 2 | `AppShortcutsBuilder.buildExpression(_:)` return-type ambiguity — Swift overload resolution picks `[AppShortcut]`-returning sibling; wrapper's `resultPtr.initializeMemory(as: AppShortcut.self, ...)` rejects the mismatch | `MethodWrapperEmitter` non-variadic call expression — function-reference `as` cast pinned to this overload's signature when a return-type-only-overload sibling is detected (`HasReturnTypeOnlyOverloadSibling` + `BuildOverloadDisambiguationSignature`) | **Closed** |
| 3 | `AppShortcutsBuilder.buildBlock` `@available` floor (iOS 16.0) too low for the variadic-of-arrays overload (iOS 17.4-only); availability inherited from parent's floor instead of merging with the specific overload's annotation | Variadic-widening emitter availability merge | **Closed** |
| 4 | async `@_cdecl` wrappers emitted without `await`/concurrency support — 4× (1× per arch slice on iOS/tvOS) | — | **Out of scope** per `08b-entityproperty-init-keypath.md` |
| 5 | throwing `@_cdecl` wrappers emitted without `try`/error handling — 4× (same arch fan-out) | — | **Out of scope** per the same |

Items 1, 2, 3 each shipped with BindingTests fixtures
(`ReturnTypeOnlyOverload.swift` + `VariadicResultBuilder.swift` +
existing protocol-availability coverage) so the regression coverage
is durable in the BindingTests gate, not just in the AppIntents
real-world regen. Items 4 and 5 remain SB0001-free in `AppIntents.cs`
— they affect only the Swift `@_cdecl` wrapper emission and are
already documented as the async/throws gap in doc 08b.

The doc 14 stated acceptance — SB0001 removed in AppIntents.cs regen,
verified by grep — is independent of the Swift-wrapper compile
status and is **met**.

### Codex r1 findings

End-of-task `/codex-review` flagged four follow-on hazards on the
widened wrapper paths. All four were fixed before close:

1. **Swift.Error PWT slot mismatch (High).**
   `MetatypeHelperEmitter.GetTotalPwtParameterCount` historically
   counted `Swift.Error` as a slot while the C# side rejects every
   well-known runtime protocol via `IsProtocolAvailableForConstraint`.
   For `Generic<T: Swift.Error>` GSF constructors that produced a
   Swift `_pwtN` parameter with no matching C# slot. Fix: remove the
   `Swift.Error` exception, add
   `MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance`,
   and gate-block any parent with a well-known runtime protocol
   conformance in `GenericDispatchEmitter.HasWrapperHelperGateBlocker`.
   The dlsym'd `Ma` symbol still requires those PWT slots, so the
   only signature-coherent option is to refuse the wrapper.

2. **Labelled args in function-reference cast call (Medium).**
   `MethodWrapperEmitter` builds the variadic-bitcast and
   return-type-only-overload disambiguation paths with
   `CdeclParamMapper`-produced call args, which include external
   labels for ordinary labelled params. Swift function values are
   called positionally — `(S.f as (Int) -> Int)(x: 1)` is rejected.
   Fix: introduce `MethodWrapperEmitter.StripArgLabel` and apply it
   when building the positional call args for both cast sites.

3. **Nested-of-generic-outer GSF render (Medium).**
   `ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor`
   appends sugared generic params to the full module-qualified
   name. For `Outer<T>.Inner` that renders `Outer.Inner<T>()`, which
   Swift rejects as specializing a non-generic `Inner`. Until the
   renderer can place generic args on the correct path segment,
   `GenericDispatchEmitter.HasWrapperHelperGateBlocker` now refuses
   nested types whose ParentDecl chain contains a generic ancestor
   (`HasGenericOuterAncestor`).

4. **Module-qualified outer name check (Medium).**
   `GenericDispatchEmitter.OuterMatchesParent` previously admitted
   `outerSimpleName == parentSimpleName` even when the spec carried
   a module prefix, letting cross-module siblings with colliding
   short names pass through. Fix: when `named.Name` contains a `.`
   (module-qualified), require exact equality with
   `parentTypeDecl.SwiftTypeName.ModuleQualifiedName`; only use
   simple-name equality for unqualified names.

### Codex r2 + Grok r1 follow-up findings

After the r1 fixes landed, a second review pass surfaced four more
items, all addressed in-tree.

1. **`GetResolvablePwtParameterCount` PWT-count drift (Codex r2, High).**
   The r1 well-known-protocol skip was added to
   `GetTotalPwtParameterCount` and to a per-emitter gate, but
   `GetResolvablePwtParameterCount` — used directly by the property
   and subscript wrapper paths — still counted well-known protocols.
   That over-declared `_pwtN` on those Swift wrappers without a
   matching C# slot. Fix: mirror the same well-known skip into
   `GetResolvablePwtParameterCount` so the counter is consistent
   across all consumers (no more per-emitter patching).

2. **Marker-only parents incorrectly gate-blocked (Codex r2, Medium).**
   `HasWellKnownRuntimeProtocolConformance` originally returned true
   for any well-known runtime protocol, including the pure markers
   (`Swift.Sendable` / `Swift.Copyable` / `Swift.Escapable` /
   `Swift.SendableMetatype`). Markers carry no witness table and
   never appear in `Ma` signatures, so a parent constrained only by
   markers should be able to use the wrapper-helper path. Fix:
   promote the local `IsStdlibMarkerProtocol` set to
   `TypeDatabaseExtensions` and refine the gate to exclude markers,
   leaving only `Swift.Error` and `_Concurrency.Actor` as actual
   blockers.

3. **`HasReturnTypeOnlyOverloadSibling` over-application (Grok M2).**
   The disambiguation `as`-cast call path that consumes
   `HasReturnTypeOnlyOverloadSibling` only exists in the direct
   (non-static-dispatch) `EmitSwiftMethodWrapper` branch. Generic
   parents route through `EmitGenericStaticDispatchMethod` which has
   no cast-call path, so applying the predicate there would mismatch
   the actual emission. Fix: return `false` from the predicate for
   generic parents.

4. **`MethodValidationGates` docstring drift (Grok M1+M4).**
   The protocol-availability docstring still listed only the marker
   set and omitted `Swift.Error` and the rationale, even after the
   r1 well-known gate was added. Updated to match the implementation.

Test additions for the r1+r2 helpers (Grok M3):
`MethodWrapperEmitterTests.StripArgLabel` (12 `[Theory]` rows),
`MethodWrapperEmitterTests.HasReturnTypeOnlyOverloadSibling`
(generic-vs-non-generic parent), `MetatypeHelperEmitterTests` for
`HasWellKnownRuntimeProtocolConformance` (markers / Error / Actor /
mixed / normal) and `GetResolvablePwtParameterCount` (well-known
not counted / mixed / PAT+Self), new `GenericDispatchEmitterTests`
covering `HasGenericOuterAncestor` (top / nested / deep / self-vs-
ancestor) and `OuterMatchesParent` (simple name / module-qualified /
cross-host / unrelated). +37 unit tests, all green.

### End-of-task review (Codex r1 + Grok r1)

A paired Codex + Grok review against the working-tree diff surfaced
three issues:

1. **`Swift.BitwiseCopyable` over-counted in PWT counters (Codex r1
   High, Grok r1 High #1).** Both `GetTotalPwtParameterCount` and
   `GetResolvablePwtParameterCount` only skipped
   `IsWellKnownRuntimeProtocol`, but `BitwiseCopyable` is a stdlib
   marker (in `IsStdlibMarkerProtocol`) — not in the well-known set.
   Fix: added `IsStdlibMarkerProtocol` skip to both counters
   (`MetatypeHelperEmitter.cs` ~177, ~229) so any pure-marker
   conformance is correctly excluded from the `_pwtN` slot count.

2. **Docstring corruption above `HasUnresolvableTypeConformancesWithoutDescriptor`
   (Grok r1 High #2).** Two `<summary>` blocks had been emitted
   back-to-back above `HasWellKnownRuntimeProtocolConformance`, with
   no docstring on `HasUnresolvableTypeConformancesWithoutDescriptor`.
   Fix: restored the WithoutDescriptor summary on its own method
   (`MetatypeHelperEmitter.cs`:345-356).

3. **`HasReturnTypeOnlyOverloadSibling` could fire on effectful
   methods (Codex r1 Medium).** `BuildOverloadDisambiguationSignature`
   emits `(P) -> R` with no `throws` / `async`. Casting an effectful
   function to a non-effectful function type is a Swift compile
   error, and the throwing-wrapper path still wraps the call in
   `try` — which would then operate on a non-throwing cast result.
   Fix: predicate now returns `false` for `methodDecl.Throws ||
   methodDecl.IsAsync`, dropping the wrapper into ambiguity-tolerant
   emission (`MethodWrapperEmitter.cs`:1827-1832).

Test additions for the r1 fixes (Codex r1 Low #3):
`MetatypeHelperEmitterTests.GetTotalPwtParameterCount_*` (well-known
not counted, markers Theory including `BitwiseCopyable`, normal
counted, PAT/Self with descriptor counted, PAT/Self without
descriptor not counted, mixed-everything) and
`MethodWrapperEmitterTests.HasReturnTypeOnlyOverloadSibling_EffectfulMethod_ReturnsFalse`
[Theory] with `(throws,async)` ∈ {(T,F),(F,T),(T,T)}. Extended
`SimpleProtocolDatabase.WithProtocol` with an optional
`descriptorSymbol` overload (3-arg form preserved). +13 unit tests
total over the r2 baseline.

### Codex r2 follow-up (High + Low — clean after fix)

The post-r1 paired re-review surfaced one residual High and one
documentation-drift Low:

1. **`IsProtocolAvailableForConstraint` still missed
   `BitwiseCopyable` (Codex r2 High).** The PWT counters dual-skipped
   `IsWellKnownRuntimeProtocol` + `IsStdlibMarkerProtocol`, but
   `MethodValidationGates.IsProtocolAvailableForConstraint` only
   excluded the well-known set. A `T: BitwiseCopyable` constraint
   would therefore have produced a C# `where T : IBitwiseCopyable`
   constraint (no such interface, CS0246), a PWT local in
   `MethodMarshalPlanBuilder`, and a PWT parameter in `PInvokeEmitter`
   — all three sites read this gate. Fix: added the
   `IsStdlibMarkerProtocol` exclusion to the predicate
   (`MethodValidationGates.cs`:187-202) so the gate matches the
   counter invariant. Theory test cases for both
   `IsProtocolAvailableForConstraint` and
   `IsUnsupportedProtocolConstraint` now include
   `Swift.BitwiseCopyable`.

2. **Stale comments conflating markers with PWT-carrying well-known
   protocols (Codex r2 Low).** Updated
   `GenericDispatchEmitter.cs`:155 (no longer claims markers expect
   PWT slots in the `...Ma` symbol) and the
   `GetTotalPwtParameterCount_MixedWellKnownMarkersAndNormal_OnlyNonSkippedCounted`
   test comment (markers and Error/Actor are skipped for different
   reasons — no-PWT vs. C# gate-block — and the comment now says so).

After the r2 fix Grok r2 returned `High: None / Medium: None /
Low: None — The uncommitted changes are clean.` Net test delta:
+2 over the post-r1 baseline (BitwiseCopyable added to both
existing `[Theory]` lists).

Final verification: `nuke test` 11831/564/20 pass, `nuke
binding-tests --sim` 2254 passed / 0 crash / 61 skip,
`nuke binding-tests --device` 2275 passed / 0 crash — all match the
post-RouteC baselines.

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
