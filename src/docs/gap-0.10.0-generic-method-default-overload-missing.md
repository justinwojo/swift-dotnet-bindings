# Gap: Generic methods skip the empty-literal default trim-overload emission

> SDK 0.10.0 generator default-overload gap. Spotted 2026-05-06 during the
> Bundle 04 #9 (`Set<T>` parameter projection) closure.
>
> **Status: option (b) RESOLVED 2026-05-07 (Session E Phase 3a) for CSM-eligible
> generics across all four shapes — sync / async / sync-throws / async-throws.
> Option (a) remains open for non-CSM class-bound generics; tracked as a
> roadmap candidate (no current consumer blocked).**

## Summary

The `DefaultParameterOverloadEmitter` lifts a Swift `= []` (or other empty-literal)
default into a no-arg trim overload that calls the Swift-defaulted function — for
sync, async, and collection-defaulted parameters on non-generic methods. Generic
methods were originally skipped: the trim overload was not emitted, so callers
had to construct an empty container explicitly even though Swift permits omission.

Phase 3a (Session E) closes the **CSM-routed** subset of this gap: any generic
method that already participates in concrete specialization
(`ConcreteProtocolSpecializationEmitter` for sync, the per-conformer async
specialization path for async/throws) now layers per-conformer trim overloads
on top of each CSM primary, for both no-throws and throws variants.

The non-CSM subset — class-bound protocol generics with no
`specialization-hints.json` entry, e.g. StoreKit2's
`purchase(confirmIn: some UIScene)` — remains open under option (a).

## Original repro

The canonical case is the StoreKit2 `Product.purchase<some UIScene>` overload —
the `confirmIn: some UIScene` family. Post Bundle 04 #9 the four non-generic
`Product.purchase` overloads each emit a trim overload that lets the caller
write `await product.PurchaseAsync()`. The generic overload at
`apple-frameworks/StoreKit2/obj/Debug/.../StoreKit2.cs:24282`:

```csharp
public Task<Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController,
    IReadOnlySet<Product.PurchaseOption> options,    // [BAD] no default, no trim overload
    CancellationToken cancellationToken = default)
    where T0 : ...
```

The matching Swift signature does have a defaulted `options`:

```swift
@MainActor public func purchase(
    confirmIn scene: some UIScene,
    options: Set<Product.PurchaseOption> = []
) async throws -> Product.PurchaseResult
```

This generic shape still requires the workaround (explicit empty Set) because
`some UIScene` is a class-bound existential without a CSM hint — option (a)
work — and not the CSM-routed shape that Phase 3a unblocks.

## Hypothesis (original — superseded by Phase 3a investigation)

`DefaultParameterOverloadEmitter` guarded trim-overload emission on the
parameter list shape but bailed when the method is generic — `methodDecl.IsGeneric`
short-circuits at the top of `TryEmitOverloads`. The bail exists because the
trim overload would need to forward generic type arguments through the
`@_silgen_name` shim and into the C# trim-overload P/Invoke signature.

The D2 investigation (2026-05-07) confirmed lifting the bail alone is dead code
on non-CSM class-bound generics — see "Why this is bigger" below — and motivated
the Phase 3a split into option (b) (CSM-routed, RESOLVED) vs option (a)
(class-bound non-CSM, OPEN).

## Phase 3a resolution (option (b) — CSM-routed)

The realisation that unblocked option (b): when a generic method is already
specialized through CSM, the **synthesized non-generic methodDecl** (concrete
conformer types substituted into `CSSignature`, `GenericParameters` cleared)
satisfies the trim emitter's `methodDecl.IsGeneric` precondition naturally. The
trim emitter doesn't need to thread method-own generics through the
`@_silgen_name` shim — it operates on the already-specialized signature, so
the shim it emits is non-generic by construction.

Phase 3a wires `DefaultParameterOverloadEmitter.TryEmitOverloads(...)` after
each CSM primary, on both branches:

- **CSM-sync** — `ConcreteProtocolSpecializationEmitter.TryEmitConcreteOverload`
  → new helper `EmitTrimOverloadsForCsmSync` builds the substituted
  signature, synthesizes a non-generic `MethodDecl` clone (with the
  per-conformer cdecl symbol stamped into `MangledName` so
  `BuildWrapperSymbol`'s `DeterministicHash8` produces a unique `DBW_…`
  symbol per conformer pairing), pre-populates
  `MethodEnvironment.EmittedProjectedSignatures` with the auto-trim primary's
  key (the CSM-sync primary already emits the maximally-trimmed shape — would
  otherwise CS0111-collide with the deepest trim variant), and tail-calls the
  trim emitter. Constructors are explicitly out of scope (CSM-sync constructors
  take a bespoke `From{Conformer}(…)` factory shape that the standard overload
  emitter doesn't model). Generic initializers with trailing defaults — e.g.
  `init<S: P>(..., options: Set<Int> = [], tag: Int = 1)` — therefore only get
  the CSM factory shape that auto-fills every default; intermediate factory
  overloads exposing `options` while letting Swift fill `tag` are not emitted
  for constructors. This is a deliberate scoping choice for Phase 3a; tracked
  alongside option (a) as a roadmap candidate (no current consumer blocked).

- **CSM-async** — `ConcreteProtocolSpecializationEmitter.TryEmitConcreteOverloadAsync`
  appends a tail call to the trim emitter using the existing pre-built
  `synthesized` MethodDecl. Unlike the sync path, the async primary preserves
  trailing defaults inline (mappable ones render as `nint tag = 13` in C#,
  non-mappable ones force the caller to pass an explicit value). A trim
  variant that drops only the mappable suffix is ambiguous with the primary
  at the call site (`AppendAsync(source, options)` would match both
  `AppendAsync(source, options, tag = 13, ct)` and the trim-1 variant
  `AppendAsync(source, options, ct)`). The new helper
  `BuildMappableSuffixShadowKeys` walks the rightmost contiguous run of
  mappable trailing defaults and seeds the projected-signature dedup set
  with each shadow key — those depths are intentionally suppressed because
  the primary already covers them. Stops at the first non-mappable trailing
  default; deeper trims drop a non-mappable param the primary can't omit and
  expose a genuinely new public surface (so they still emit).

### Mutating-keyword propagation root-cause fix

A latent bug in `DefaultParameterOverloadEmitter.EmitSwiftWrapper` (and the
parallel `EmitDebugParamWrapper`) surfaced once Phase 3a exercised the value-
type CSM-sync trim path: the `_dbw_*` `@_silgen_name` extension shim's body is
`return self.<originalMethod>(...)`, which fails to compile on an immutable
`self` whenever the original method is `mutating` on a struct/enum parent
(`error: cannot use mutating member on immutable value: 'self' is immutable`).
The wrapper-build pipeline silently strips functions that fail compilation
(`SwiftSourceStripper.StripErrorFunctions`), so the symbol disappears from the
dylib and the C# trim P/Invoke that targets it raises
`EntryPointNotFoundException` at runtime.

Both wrapper emit sites now propagate the original method's `IsMutating`
flag onto the shim:

```csharp
bool needsMutating = !isStatic
    && originalMethodDecl.IsMutating
    && !(parentTypeDecl is ClassDecl);
var mutatingKeyword = needsMutating ? "mutating " : "";
// ...
swiftWriter.WriteLine($"public {staticKeyword}{mutatingKeyword}func {swiftFuncName}(...) {{");
```

The class guard is intentional — class instance mutation flows through the
reference and doesn't need `mutating`. Static methods are exempt by
construction (no `self`).

### MethodClosureBridge return-statement parity

A second `MethodClosureBridge` issue surfaced during Phase 3a once the
async-throws trim variant exercised the multi-statement adapter-closure path
with a non-void Bool return. The `if-let / else` branches and the inner
`withUnsafePointer` trailing closures are Swift statements (not expressions),
so each terminal value-producing site needs explicit `return` when the
adapter returns non-Void. `EmitCdeclInvocation` now accepts a `firstLinePrefix`
parameter that the call sites use to inject `return ` exactly where Swift's
control-flow rules require it (the outer `withUnsafePointer { ... in` of the
if-branch, the direct cdecl call of the else-branch, the no-pointer-wrap
single-call body). Inner trailing closures stay single-expression and
auto-return.

## Coverage

BindingTests fixtures (added Phase 3a):

- `BindingTests/Sources/SwiftBindingsTestLib/Generics/MethodLevelGenerics.swift`
  - `DefaultedHasher.append<D: DataProtocol>(_ data:, options: Set<Int> = [], tag: Int = 7)` — sync, mutating struct, two-default mix (non-mappable Set + mappable Int).
  - `DefaultedThrowingHasher.appendOrThrow<D: DataProtocol>(...) throws` — sync-throws, mutating struct, two-default mix.
  - `DefaultedHasherWithFile.append<D: DataProtocol>(_ data:, options: Set<Int> = [], tag: Int = 7, file: StaticString = #file)` — same shape as `DefaultedHasher` plus a trailing `#file` debug parameter; locks the parser-strips-debug-defaults invariant the Codex r1 Medium hypothesised was broken.
  - DataProtocol has two CSM hints (`Foundation.Data` and `[UInt8]`), so each fixture exercises two distinct per-conformer DBW shims plus their trim variants.
- `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncGenericSequence.swift`
  - `DefaultedAsyncRoster.appendAsync<S: Sequence>(...)` — async, two-default mix; class parent (no `mutating`).
  - `DefaultedAsyncRoster.appendOrThrowAsync<S: Sequence>(...) async throws` — async-throws, two-default mix.

Runtime tests (21 total: 8 sync + 9 async + 4 sync-with-#file):

- `BindingTests/RuntimeTestsApp/Generics/DefaultedTrimOverloadTests.cs` — auto-trim primary observation, trim-1 round-trip (`options` exposed, `tag` filled), throws plumbing through the trim shim, per-conformer hash uniqueness (Foundation.Data vs `[UInt8]`).
- `BindingTests/RuntimeTestsApp/Async/DefaultedAsyncTrimOverloadTests.cs` — primary inline-default round-trip, trim=2 fall-through to Swift defaults, suppressed-trim-1 structural proof (primary handles `(source, options)`), Dog-conformer DBW hash uniqueness, async-throws plumbing on both primary and trim=2.
- `BindingTests/RuntimeTestsApp/Generics/DefaultedTrimOverloadWithFileTests.cs` — auto-trim primary + trim variant on the `#file`-augmented fixture, with non-empty `lastFile` assertions confirming Swift fills the debug default; structural symmetry vs `DefaultedTrimOverloadTests` is the regression guard.

Gates verified:
- Original Phase 3a (sync + async trim wiring, mutating-keyword propagation,
  MethodClosureBridge return-statement parity):
  - Sim (Mono JIT): 1921 → 1938 pass (+17), 0 fail, 0 crash, 56 skip (was 57).
  - Device (NativeAOT): 1937 → 1954 pass (+17), 0 fail, 0 crash, 40 skip.
- Post-delta (#file fixture + 4 runtime tests + doc/comment rewrite):
  - Sim: 1938 → 1942 pass (+4), 0 fail, 0 crash.
  - Device: rotating flakes across runs in unrelated classes
    (`OwnershipGCStressTests`, `OptionalMarshallingTests`,
    `OptionalPropertyPathTests`, `ProtocolClosureSkipTests`); the cleanest
    run was 1956 vs 1954 baseline (+4 from new tests, –2 from
    `OptionalMarshallingTests` flake). All 4 new tests pass on every device
    run.
- `nuke test` (unit + analyzer + runtime unit tests): all subtargets succeed.

## Codex r1 Medium — closed as not reproducible (#file edge case)

Codex r1 flagged a hypothesised off-by-N in
`EmitTrimOverloadsForCsmSync` for generic CSM methods that combine a
non-mappable trailing default with a Swift compiler-injected debug parameter
(`#file`, `#line`, `#column`, `#function`). The chain of reasoning was that
`BuildOverloadDecl` removes the last `trimCount` raw entries from
`CSSignature.Skip(1)` without skipping debug params, while
`CountTrailingDefaults` skips them — so feeding the latter into the former
would over-trim by the number of trailing debug args.

Phase 3a verified empirically that this is **not reachable** for swiftinterface-
shaped inputs. The investigation:

1. Added `DefaultedHasherWithFile` to `MethodLevelGenerics.swift` — same shape
   as the existing `DefaultedHasher` (DataProtocol-bound, two trailing defaults
   `options: Set<Int> = []`, `tag: Int = 7`) plus a trailing
   `file: StaticString = #file` debug parameter.
2. Regenerated bindings + emitted a temporary `Console.Error.WriteLine`
   diagnostic at the entry to `TryEmitOverloads` to dump
   `methodDecl.CSSignature.Skip(1)` for `DefaultedHasherWithFile.append`.
3. Diagnostic confirmed:

   ```
   TEMP-DIAG-FILE: parent=DefaultedHasherWithFile method=append
                   csSigCount=3 trailingDefaults=2
     arg[0] name=arg0   type=Foundation.Data        hasDefault=False isDebug=False
     arg[1] name=options type=Swift.Set<Swift.Int>  hasDefault=True  isDebug=False
     arg[2] name=tag    type=Swift.Int              hasDefault=True  isDebug=False
   ```

   The `file: StaticString = #file` parameter is **not present in
   `CSSignature` at all** — the parser strips trailing
   `#file/#line/#column/#function` defaults before the emitter ever sees them.
   `BuildOverloadDecl` and `CountTrailingDefaults` therefore agree on the arg
   set and the off-by-N is mathematically impossible.

4. The generated bindings for `DefaultedHasherWithFile` emit identically to
   `DefaultedHasher` — same five overloads (generic fallback + 2 CSM primaries
   + 2 CSM trims), same `_dbw_*` shim shapes, same per-conformer hash
   uniqueness. The `IsDebugParameter` skip in `CountTrailingDefaults` is
   harmless today because debug args are stripped before they reach the
   emitter — if that parser invariant ever changes, `CountTrailingDefaults`
   and `BuildOverloadDecl` would disagree (the original r1 concern), and the
   `trailingDefaults + trailingDebugCount` seeding fix would need to be
   applied in `EmitTrimOverloadsForCsmSync` to restore symmetry.

5. `BindingTests/RuntimeTestsApp/Generics/DefaultedTrimOverloadWithFileTests.cs`
   pins the empirical equivalence at the runtime layer — auto-trim primary
   observation, trim variant `options` round-trip, and `#file` default
   non-empty observation, all on both `Foundation.Data` and `[UInt8]`
   conformers. Locks the parser-strips-debug-defaults behaviour as regression
   coverage so a future parser change that started passing debug args through
   would fail the assertions before any emitter follow-up was needed.

## Why option (a) is bigger than just lifting the bail (preserved for the open subset)

Initial attempt (D2 work, 2026-05-07): lift the bail and thread method-own
generic params + a `where` clause through `EmitSwiftWrapper` using
`AsyncHarnessEmitter.BuildMethodOwnGenericParams` + `WrapperEmitterHelpers
.BuildSwiftWhereClause`. The Swift-side change is small — both helpers already
exist and the `@_silgen_name` shim shape `public static func _dbw_…<T0>(…)
async throws -> Result where T0: ConstraintProto` compiles cleanly.

However, the C# side does not currently emit the **primary** explicit overload
for the StoreKit-shape input (`AsyncSceneMarker` is a custom class-bound
protocol with no entry in `specialization-hints.json`, and
`MethodGenericBridgeEmitter` rejects async + throws). So the trim overload had
nothing to attach to: we'd be emitting a new `DBW_…` symbol that no C# call
site ever resolves. Verified end-to-end during D2 by adding an exact-shape
fixture (`AsyncPurchaseReceipt.confirm<S: AsyncSceneMarker>(…, options: Set<…> =
[])`) and observing the generated `SwiftBindingsTestLib.cs`:

```text
// Unsupported: method 'confirm' — parameter or return type not yet supported
//   (wrapper not emitted; direct call would be ABI-unsafe)
```

A full option-(a) fix needs **two layers** beyond the bail lift:

1. **Trim-overload @_silgen_name generic threading** — thread
   `methodOwnGenericParams` (`<T0>`) and `methodOwnWhereClause` (` where T0:
   Constraint`) into the three func-decl emit sites in
   `EmitSwiftWrapper` (free function, constructor, type method). Mechanical.

2. **C# trim-overload P/Invoke binding to the new DBW_ symbol** — extend
   either (a) `MethodGenericBridgeEmitter` to handle async/throws so the
   primary explicit overload's existential-opening dispatch covers async +
   class-bound-protocol generics, or (b) the async-generic emission path so
   it emits per-conformer specialized trim overloads alongside the primary
   specialized overloads (mirroring the `Sequence`-element CSM machinery
   used for `AnimalAsyncRoster.insertAsync`). Both paths require the trim
   overload's P/Invoke to bind the new `DBW_…` symbol with matching
   metadata + witness threading; today the trim emitter generates a P/Invoke
   that targets a symbol the wrapper dylib never exports for these shapes.

Layer 1 alone is dead code without layer 2 — the @_silgen_name shim emits but
nothing calls it, so the dylib carries an unused symbol and no consumer
benefit lands. Layer 2 is non-trivial: extending `MethodGenericBridgeEmitter`
to async/throws was previously deferred ("@_cdecl can't throw; skip for v1"
at the top of that emitter), and the per-conformer specialized trim path
needs the trim overload's signature collision logic to dedup against the
specialized primary overloads.

## Severity

**Type-fidelity — Low.** Ergonomic loss only on the still-open option (a) shape;
correctness is intact (the explicit overload still works). Generic-method-with-
collection-default is uncommon enough that no current consumer is blocked, but
the StoreKit `purchase(confirmIn: some UIScene)` case is hit on every iOS app
that wants scene-aware purchase confirmation.

## Workaround (option (a) only)

Pass an explicit empty collection (e.g. `new HashSet<Product.PurchaseOption>()`)
at the call site for the still-open class-bound non-CSM shape.

## Reference

- `gap-0.10.0-swift-set-parameter-becomes-ienumerable-default-lost.md` — the
  parent doc; the Set-projection fix in Bundle 04 #9 made the missing trim
  overload visible on the generic family.
- D2 investigation summary (2026-05-07): scoped the fix at two layers, ruled
  out a single-layer Swift-only patch as dead code, and kept the gap open
  pending broader async-generic dispatch work. The session confirmed that
  the trim-overload bail at `DefaultParameterOverloadEmitter.cs:59-60` is
  the right place to lift once the C#-side dispatch lands.
- Phase 3a (2026-05-07): resolved option (b) for CSM-eligible generics across
  sync / async / sync-throws / async-throws. Touched
  `ConcreteProtocolSpecializationEmitter.cs` (sync trim wiring +
  `EmitTrimOverloadsForCsmSync` helper),
  `ConcreteProtocolSpecializationEmitter.Async.cs` (async trim wiring +
  `BuildMappableSuffixShadowKeys` helper),
  `DefaultParameterOverloadEmitter.cs` (mutating-keyword propagation in
  `EmitSwiftWrapper` + `EmitDebugParamWrapper`; `internal` access on
  `GetProjectedOverloadKey` to support dedup seeding from CSM-sync), and
  `MethodClosureBridge.cs` (return-statement parity for non-Void multi-
  statement adapter closures uncovered by the async-throws trim variant).
