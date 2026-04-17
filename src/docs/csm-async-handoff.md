# CSM Async/Multi-Param Gate-Lift — Implementation Handoff

Follow-up to Session 5 (AsyncHarnessEmitter extraction). This doc captures the
architectural decisions and exact call sites needed to flip the 6 target async
generic methods from `CallConvSwift` + SB0001 to `CallConvCdecl` + `SBW_CSM_*`.

## Target methods

Current state in downstream `/Users/wojo/Dev/swift-dotnet-packages` generated
output (all annotated `[Obsolete("SB0001…")]` with raw-mangled `CallConvSwift`
P/Invokes — source of the 10/5 NEAR-SHIP counts):

| Method | Shape | Generic params | Current emission location |
|---|---|---|---|
| `StoreKit.Product.products(for:)` | static async throws | 1 (`Identifiers: Collection where Identifiers.Element == String`) | `StoreKit2.cs:23905` — `ProductsAsync<TIdentifiers>`, entry `$s8StoreKit7ProductV8products3forSayACGx_tYaKSlRzSS7ElementRtzlFZ_async` |
| `StoreKit.PromotionInfo.updateAll<T>` | static async throws | 1 (`T: Collection, T.Element == PromotionInfo`) | `StoreKit2.cs:20597` — `UpdateAllAsync<T0>` |
| `MusicKit.MusicLibrary.add<MusicItemType>` | instance async throws | 1 (`MusicItemType: MusicLibraryAddable`) | `MusicKit.cs:6016` + `6192` — two `AddAsync<TMusicItemType>` overloads |
| `MusicKit.MusicLibrary.createPlaylist<S, T>` | instance async throws | 2 (multi-param, `T == S.Element`) | `MusicKit.cs:6565` — `CreatePlaylistAsync<S, TMusicPlaylistAddableType>` |
| `MusicKit.MusicLibrary.edit<S, T>` | instance async throws | 2 (multi-param, `T == S.Element`) | `MusicKit.cs:6970` — `EditAsync<S, TMusicPlaylistAddableType>` |
| `MusicKit.Queue.insert<S, T>` | instance async throws | 2 (multi-param, `T == S.Element`) | `MusicKit.cs:33749` — `InsertAsync<S, TPlayableMusicItemType>` |

None use typed throws, `@MainActor`, or actor-isolation. All use plain `async throws`.

NEAR-SHIP target per ship plan: StoreKit2 10 SB0001 → ~5, MusicKit 5 → 0.
Phase A flips the 3 single-param rows; Phase B flips the 3 multi-param rows.

## Current behavior (verified)

- `MemberValidationPipeline` Phase 4 (`MemberValidationPipeline.cs:156-163`)
  calls `MethodValidationGates.HasUnsupportedProtocolConstraints`.
- That gate returns **false** for the target methods because `Swift.Collection`
  and the MusicKit protocols are **not registered as TypeRecords** (hints-only).
  `IsUnsupportedProtocolConstraint` (`MethodValidationGates.cs:88-98`) returns
  false when the protocol has no TypeRecord.
- So methods flow through Phase 4 → MethodHandler → existential-fallback
  emission with raw-mangled `CallConvSwift` P/Invoke + `[Obsolete("SB0001")]`.
- `ConcreteProtocolSpecializationEmitter` (`ConcreteProtocolSpecializationEmitter.cs:50,58`)
  hard-gates `async || throws` and multi-param, so CSM never emits these.

## Where CSM plugs into the pipeline

`ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations` is invoked
from **three type-level handlers**, AFTER `base.HandleBaseDecl(... methods ...)`
has emitted the type's normal methods but BEFORE the closing `}` of the C#
class body:

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs:380`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs:260`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs:341`

All three guard on `context.GetEmissionContext().SpecializationEngine != null`
and pass the engine through. CSM overloads therefore emit as additional
static/instance members inside the existing type declaration — they are NOT
subject to `BaseHandler.HandleBaseDecl` dedup (`emittedMethodSignatures` /
`emittedProjectedSignatures`), so CSM manages its own dedup via the
`HashSet<string> emittedSignatures` local in `EmitConcreteSpecializations`
(line 44) plus `ModuleEmissionContext.TryAddMethodWrapperSymbol(cdeclSymbol)`.

Phase D's Phase 4 intercept needs to coordinate with this ordering: the Phase 4
skip fires BEFORE the type handler's method-emission loop AND before CSM.
Skipping a method at Phase 4 removes the generic SB0001 emission from
MethodHandler; CSM still runs afterward inside the same type-handler pass and
emits the concrete overloads. Net effect: only concrete overloads appear, no
leftover generic SB0001.

## Architecture decision — REUSE WrapperEmitter

After investigation (Explore report), WrapperEmitter has **no hidden pipeline
entanglement**. It reads only from `MethodEnvironment` (which reads from
`MethodDecl` + `TypeDatabase`). This makes the clean approach:

1. **Synthesize** a concrete `MethodDecl` per `(method, conformer)` by cloning
   via `with` and substituting the generic param everywhere.
2. Construct `MethodEnvironment` + `SignatureHandler` around the synthesized decl.
3. Call `WrapperEmitter.EmitMethod(csWriter, swiftWriter)` + `PInvokeEmitter.EmitPInvoke(...)`.
4. This routes through the SAME async harness as normal `MethodHandler` emission
   — typed-throws, cancellation, actor-isolation, main-actor, tuple returns,
   string returns, array-of-string returns all work uniformly.

Hand-writing the async harness in CSM (duplicating `AsyncHarnessEmitter` logic)
was rejected per the ship plan's "partial async-CSM regresses NEAR-SHIP →
BROKEN" warning — duplication guarantees silent divergence on future fixes.

**`AsyncHarnessEmitter` is already extracted** (Session 5 shipped, commit
`bf0867f7`). Lives at
`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncHarnessEmitter.cs`
(1298 lines). `WrapperEmitter` instantiates it and calls
`EmitAsyncWrapper(csWriter)` + `BuildSwiftAsyncWrapperCode(...)` internally —
you do NOT call `AsyncHarnessEmitter` directly from CSM. Calling
`WrapperEmitter.EmitMethod` on a synthesized async `MethodDecl` is the entire
integration; the harness runs automatically.

## Model facts (verified)

- `MethodDecl` is a **sealed record** → `with` works for cloning.
  `required` props: `MangledName`, `MethodType`, `IsConstructor`, `CSSignature`,
  `Throws`, `IsAsync`, `GenericParameters`, `Visibility`.
- `ArgumentDecl` is a record → `with` works. Key props: `SwiftTypeSpec`,
  `PrivateName`, `IsInOut`, `IsGeneric`, `HasDefaultArg`, `CSharpName`.
- `BaseDecl` is a record. Clone propagates `ParentDecl`, `ModuleDecl`, `Name`,
  `OriginalSwiftName`, `AvailabilityAnnotations`.
- `NamedTypeSpec` constructor: `NamedTypeSpec(string name, params TypeSpec[] generics)`.
- `MethodEnvironment(methodDecl, typeDatabase)` — only 2 required args.
  `MethodDecl.ParentDecl` must be non-null (enforced line 87 of `IEnvironment.cs`).
- `WrapperEmitter(methodEnv, signatureHandler, fallbackInfo, emissionContext)` —
  `SignatureHandler` is NOT optional; construct via `new SignatureHandler(methodEnv)`.
  `emissionContext` MUST be passed (cross-cutting constraint — see `constraints.md`).
- `WrapperStrategy.CdeclMethod` → sets `UsesCdeclMethodWrapper = true` → emits
  `@_cdecl("mangledName")` with `CallConvCdecl` P/Invoke.
- Naming is hash-driven off `MethodDecl.MangledName` — setting a unique
  `MangledName` per conformer gives unique callback field/method names via
  `GetAsyncCallbackFieldName`, `GetAsyncCallbackMethodName`, etc. No collisions.

## Implementation checklist

### Phase A — Single-param async gate lift (~350 LOC)

Create `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs`:

```csharp
internal static bool TryEmitConcreteOverloadAsync(
    CSharpWriter csWriter, SwiftWriter swiftWriter,
    MethodDecl originalMethod, TypeDecl parentTypeDecl,
    ConcreteSpecializationEngine.SpecializableParam specParam,
    ConcreteSpecializationEngine.ConcreteConformer conformer,
    string moduleName, ITypeDatabase typeDatabase,
    ModuleEmissionContext emissionContext,
    HashSet<string> emittedSignatures, ILogger logger)
{
    // 1. Conservative guard — match current AsyncHarnessEmitter capabilities.
    //    Defer (return false) for: PInvokeHelperContext required (generic parent),
    //    typed throws (ThrownErrorType != null), main-actor, actor-isolated.
    //    These paths need separate validation before enabling.

    // 2. Compute cdeclSymbol (same pattern as sync CSM line 128-129) but suffix "_async".
    //    Dedup via emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol).

    // 3. Build substituted CSSignature: for each ArgumentDecl, rewrite SwiftTypeSpec
    //    replacing NamedTypeSpec("τ_0_X") with a NamedTypeSpec for the conformer.
    //    (Recursive rewrite — handle TupleTypeSpec, GenericParameters on NamedTypeSpec,
    //    ClosureTypeSpec, AssociatedTypeReferenceSpec.)

    // 4. Synthesize MethodDecl via `with`:
    var synthesized = originalMethod with {
        MangledName = cdeclSymbol,
        GenericParameters = new List<GenericArgumentDecl>(),  // NEW list, not shared
        CSSignature = rewrittenSignature,                     // NEW list
        Visibility = originalMethod.Visibility,
        // ParentDecl, ModuleDecl, Name auto-propagated via record clone.
    };
    synthesized.WrapperStrategy = WrapperStrategy.CdeclMethod;
    synthesized.UsesWrapperLibrary = true;

    // 5. Env + handler + emitter:
    var env = new MethodEnvironment(synthesized, typeDatabase);
    var sigHandler = new SignatureHandler(env);
    var wrapper = new WrapperEmitter(env, sigHandler, null, emissionContext);
    wrapper.EmitMethod(csWriter, swiftWriter);
    PInvokeEmitter.EmitPInvoke(csWriter, env, sigHandler);

    // 6. Mark original method emitted so override resolution sees it:
    originalMethod.WasEmitted = true;
    return true;
}
```

**Modify `EmitConcreteSpecializations`** (`ConcreteProtocolSpecializationEmitter.cs:46-72`):

- Lift line 58 gate: allow `method.IsAsync` but keep `method.IsAccessor` and
  `method.IsMutating` skipped. Still skip typed-throws via the async-specific
  guard inside `TryEmitConcreteOverloadAsync`.
- Route: `if (method.IsAsync) TryEmitConcreteOverloadAsync(...); else TryEmitConcreteOverload(...);`.
- Keep `SpecializableParams.Count != 1` gate (Phase B handles multi-param).

### TypeSpec substitution utility

Required recursive rewrite. Tested branches:
- `NamedTypeSpec(τ_0_X)` → replace with conformer's `NamedTypeSpec`.
- `NamedTypeSpec(other, GenericParameters: [...])` → recurse into generics.
- `TupleTypeSpec` → recurse elements.
- `ClosureTypeSpec` → recurse Arguments + ReturnType.
- `AssociatedTypeReferenceSpec(BaseType = τ_0_X, Member = Element)` → if conformer declares `AssociatedTypes[Member]`, substitute with that concrete type; else fail the overload.

Add to `SubstituteGenericParamVisitor` in same file or nearby helper class.

### Conformer→NamedTypeSpec conversion

For `ConcreteConformer { SwiftType: SwiftTypeName, SwiftLiteral: "[String]" }`:
- If `SwiftLiteral` starts with `[` → parse as `Swift.Array<T>` → `NamedTypeSpec("Swift.Array", [NamedTypeSpec(inner)])`.
- Else use `SwiftType.ModuleQualifiedName` → `NamedTypeSpec(qualified)`.
- Gracefully fail (return false) when the conformer type can't be constructed.

### Phase B — Multi-param cross-product (~150 LOC)

After Phase A lands:
- Lift line 50 gate (`SpecializableParams.Count != 1`).
- In `EmitConcreteSpecializations`, iterate cartesian product of conformers
  across all specializable params.
- For `T == S.Element` coupling (MusicKit CreatePlaylist/Edit/Insert pattern):
  the pair `(S, T)` must satisfy `S.AssociatedTypes["Element"] == T.SwiftType`.
  Reject cross-product entries that don't satisfy this — otherwise we emit
  overloads with invalid Swift wrappers.
- Re-use same `TryEmitConcreteOverloadAsync` with a list of substitution pairs.

### Phase C — Missing conformer hints

Add to `src/Swift.Bindings/src/Data/specialization-hints.json`:

```json
{
  "protocol": "Swift.Collection",
  "conformers": [
    { "swiftType": "Swift.Array<Swift.String>", "csharpType": "...", "swiftLiteral": "[String]", "associatedTypes": { "Element": "Swift.String" } },
    { "swiftType": "Swift.Array<StoreKit.PromotionInfo>", "csharpType": "Swift.SwiftArray<StoreKit.PromotionInfo>", "swiftLiteral": "[PromotionInfo]", "associatedTypes": { "Element": "StoreKit.PromotionInfo" } }
  ]
}
```

MusicKit protocol conformers (`MusicLibraryAddable`, `MusicPlaylistAddable`,
`PlayableMusicItem`) are auto-discovered from per-module ABI `TypeDecl.Conformances`
via `ConcreteSpecializationEngine.IndexModuleConformances` (invoked per-module
in `Program.cs:442`). No hint needed for those.

### Phase D — Phase 4 intercept (~30 LOC)

After Phases A+B work: `MemberValidationPipeline.cs:156-163` — add a pre-check
that calls `ConcreteSpecializationEngine.CanSpecialize(methodDecl)` and, if
true AND the method is CSM-async-eligible (matches Phase A's conservative
guards), return `ValidationResult.Skip(SkipReason.GenericProtocolConstraint,
"Routed to concrete specialization.")`. This removes the duplicate generic
SB0001 method from output when CSM covers it.

Requires `ConcreteSpecializationEngine` to be available on the pipeline —
currently constructed in `Program.cs`. Either pass via `ValidationContext` or
make `MemberValidationPipeline` accept it as a ctor dep.

## Validation plan per phase

After each phase lands, run **all** of:
- `nuke test` — 9745 + 20 + 551 pass. Unit tests for substitution utility.
- `nuke validate` — 95/95 pass, zero regressions.
- `nuke binding-tests` — all 7 targets green.
- `nuke runtime-tests-device` — 1435 / 0 / 28 on NativeAOT (matches current baseline).
- Downstream `/Users/wojo/Dev/swift-dotnet-packages` rebuild of StoreKit2 + MusicKit
  via `/Users/wojo/Dev/build-and-validate.sh` — confirm SB0001 deltas match
  expected (see ship plan §Session 4 for the expected table).

Expected SB0001 deltas (at phase completion):

| Method | Pre | Post (Phase A) | Post (Phase B) | Post (Phase D) |
|---|---|---|---|---|
| `Product.products(for:)` | `CallConvSwift` raw, SB0001 on `ProductsAsync<TIdentifiers>` | `CallConvCdecl` via `SBW_CSM_StoreKit_Product_SwiftArr_SwiftString_products_XXXXXXXX_async`, concrete `ProductsAsync(SwiftArray<SwiftString>)` added | same | SB0001 generic removed; only concrete remains |
| `PromotionInfo.updateAll<T>` | `CallConvSwift` raw, SB0001 on `UpdateAllAsync<T0>` | `CallConvCdecl` via `SBW_CSM_StoreKit_PromotionInfo_SwiftArr_PromotionInfo_updateAll_XXXXXXXX_async` (requires Phase C hint) | same | SB0001 generic removed |
| `MusicLibrary.add<T>` (×2 overloads) | `CallConvSwift` raw, SB0001 | One `SBW_CSM_*_add_*_async` per auto-discovered MusicLibraryAddable conformer | same | SB0001 generic removed |
| `MusicLibrary.createPlaylist<S, T>` | `CallConvSwift` raw, SB0001 | no change (multi-param) | `SBW_CSM_*_createPlaylist_*_async` per `(S, T)` pair satisfying `T == S.Element` | SB0001 generic removed |
| `MusicLibrary.edit<S, T>` | `CallConvSwift` raw, SB0001 | no change | `SBW_CSM_*_edit_*_async` per pair | SB0001 generic removed |
| `Queue.insert<S, T>` | `CallConvSwift` raw, SB0001 | no change | `SBW_CSM_*_insert_*_async` per pair | SB0001 generic removed |

Rolled up:
- Phase A: StoreKit2 SB0001 count unchanged (generic method still emitted alongside CSM overloads), but `CallConvCdecl` overloads are NEW in the output. NEAR-SHIP unblocked from a runtime-usability standpoint for single-param methods.
- Phase B: Multi-param rows get `CallConvCdecl` overloads.
- Phase D: Generic SB0001 emissions removed. Now StoreKit2 10 → ~5, MusicKit 5 → 0 per ship plan target.

## Non-regression guards

- Sync CSM path (existing `TryEmitConcreteOverload`) stays untouched. New async
  code is additive.
- Conservative guards in `TryEmitConcreteOverloadAsync` preserve current
  behavior for: methods in generic parent types (`PInvokeHelperContext`), typed
  throws, main-actor, actor-isolated. All fall through to existing
  existential-fallback.
- All `WrapperEmitter` constructions must pass `context.GetEmissionContext()`
  per `constraints.md` — the `ModuleEmissionContext` parameter already flows
  through `EmitConcreteSpecializations`.
- Set `originalMethod.WasEmitted = true` when CSM emits (line in `TryEmitConcreteOverloadAsync`)
  so override resolution agrees with the emitted output.

## Why this wasn't delivered in this session

One full implementation session needs to:
1. Write substitution utility + synthesis (~150 LOC) with unit tests.
2. Write `TryEmitConcreteOverloadAsync` + integration (~200 LOC).
3. Run full gate matrix (`nuke test` 2m + `nuke validate` 1m + `nuke binding-tests` 5m + `nuke runtime-tests-device` 5m + downstream rebuild 15m+) — ~30m of validation per iteration.
4. Debug 2–3 iterations likely needed for substitution edge cases.

Total: ~2–3 hours of wall-clock work in one session, but the exploration +
architecture-confirmation phase in this session established the feasibility
(WrapperEmitter has no hidden entanglement) and the exact call sites. A clean
next session can execute Phase A in one pass.
