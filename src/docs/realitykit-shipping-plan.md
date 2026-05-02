# RealityKit / RealityFoundation Shipping Plan

**Goal**: get `RealityKit` and `RealityFoundation` compiling clean against the validation gate so the pair can ship as a NuGet package. NuGet publishing happens in the downstream `swift-dotnet-packages` repo and is out of scope here — this plan ends when both libraries are at 0 errors in `nuke validate`, BindingTests cover the new patterns, and a packaging contract is documented.

**Status (entry)**: pre-experiment baseline.
- `RealityKit`: 29 × CS0234 (cross-module type qualification — references to `RealityKit.Entity` / `RealityFoundation.Entity` / `ARKit.ARRaycastQueryTarget` / `Swift.SIMD3` that don't resolve).
- `RealityFoundation`: skipped (compile-gated on `RealityKit`).

**Status**: Sessions 5/5b/6 done. Session 5b retired the deferred `IsObjCModuleType` over-classification bug (P1) by splitting `IsObjCExistentialBridgedProtocol` off from the broad helper and threading `CurrentModuleName` through `ProtocolHandler`'s `ProjectionContext` so cross-module existential interface members qualify correctly. Dep gate at 15/15. Session 7 (packaging hand-off) next.

A throwaway dep-order-reversal experiment (now reverted) proved that once `RealityFoundation` is generated against an actually-loaded `RealityKit` module DB, **90 hidden errors in `RealityFoundation` surface**. Those 90 are the real backlog. The 29 surface errors on `RealityKit` are all qualification-resolution failures rooted in the same dep-threading gap.

---

## Bucket Inventory

| Bucket | Errors | Where | Root cause |
|---|---:|---|---|
| **#6 — Cross-module qualification** | 29 (RealityKit) + ~12 (RealityFoundation: CS0234/CS0246/CS0426) | apple-framework mode | The umbrella fallback only resolves cross-module USRs when dep module DBs are loaded into `TypeDatabase._modules`. `--xcframework` mode threads them; **apple-framework mode does not.** |
| **#5 — Blittable-T generic constraints** | 68 (CS0315) | `MeshBuffer<TElement>`, `MeshBuffers.Semantic<TElement>`, `UnsafeForceEffectBuffer<T>`, `FromToByAction<TValue>` | Generic types declared `where T : ISwiftObject` but instantiated with `Vector3` / `Vector2` / `Quaternion` / `uint` / `float`. |
| **#7 — Duplicate member emission** | 8 (6 × CS0111 + 2 × CS0102) | `FromToByAction<TValue>`, `MeshBuffer<TElement>` | Emitter doesn't dedupe ctor / `Count` member emission across overload paths. |
| **Misc — Unconstrained T defaults** | 2 (CS1750) | `RealityFoundation` | `null` literal emitted as default for unconstrained generic `T`; needs `default(T)`. |

**Total**: 119 errors. #6 is the structural blocker that hides #5/#7/Misc.

---

## Why This Order

1. **#6 first** — without dep threading working in apple-framework mode, `RealityFoundation` never reaches the C# compiler, so the other 90 errors stay invisible. Fixing #6 also retires the 29 surface errors on `RealityKit`. **It's the unlock**, not just one of four equally-urgent buckets.
2. **#7 + Misc next** — 10 errors, smallest blast radius, least architectural risk. Uses existing dedup infrastructure (`GetProjectedCSharpMethodKey`).
3. **#5 last** — 68 errors and the deepest change. Constraint relaxation has runtime-metadata implications and touches four coordinated sites. Doing it last means the rest of the surface is already stable when we touch the metadata path.
4. **Final gate** — full `nuke validate` baseline update, BindingTests coverage on sim **and** device, packaging hand-off.

---

## Per-Session Review Gate

Every session ends with `/codex-review` against the session's diff before the user accepts the work. Codex sees the changes with a fresh lens and is good at catching constraint violations from `.claude/rules/*.md` that the implementing session might have missed. The review is required for #6 and #5 (architectural changes); recommended for #7/Misc (smaller diffs but still touches the emitter).

A high-level `/codex-review` of *this plan itself* (before Session 1 starts) is also useful — Codex can sanity-check the bucket ordering, the threading approach, and surface unknowns we missed. See "Recommended pre-Session-1 review" at the bottom of this doc.

---

## Session 1 — Bucket #6: thread dep module DBs through apple-framework mode ✅ completed

**Goal**: load dep module DBs in apple-framework mode so the umbrella fallback resolves cross-module qualifications correctly. Targets RealityKit's 29 + RealityFoundation's ~12 cross-module errors.

### Prep notes (already-researched seams)

**Validation pipeline entry**:
- `build/Build.Validation.cs::GenerateAppleFrameworkTarget` (~line 742) — invoked from `Validate` Phase 3a. Runs `xcrun swift-api-digester` to dump ABI JSON, then invokes the generator DLL with `dotnet <GeneratorDll> -a <abi.json> -d <tbd> -t <tbd> -s <swiftinterface> -l <lib> --platform ...`. **No dep context is threaded.**
- The `dependencies` field exists in `build/validation-libraries.json` but is only consumed in **Phase 3b `--compile-wrapper-only`** (`Build.Validation.cs` ~lines 1280–1296), not Phase 3a generator invocation.

**Generator-side mode divergence**:
- `src/Swift.Bindings/src/BindingsGeneratorCommand.cs::~line 304` — `hasXcframework` flag gates both auto-detection (`BinaryDependencyAnalyzer.Analyze`, ~line 452) and manual `--framework-dependency` (~line 479; hard-errors if `!hasXcframework`).
- `--module-database` is a separate, more general CLI option that does **not** require `--xcframework`. It can be passed to apple-framework runs today; nothing accepts it.

**xcframework's working dep-loading path** (the model to mirror):
- `src/Swift.Bindings/src/Program.cs::GenerateBindings` (~line 52). Two loops at ~lines 120–154 (`moduleDatabasePaths` XML files) and ~lines 159–216 (`resolvedDependencies` ABI JSON). For each dep, `SwiftABIParser` parses the dep's ABI JSON, `ModuleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase()` builds a `ModuleTypeDatabase`, and `typeDatabase.AddModuleDatabase(depModuleDb)` (~line 193) registers it.

**Umbrella fallback (lookup site)**:
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs::TryGetTypeRecordInternal` (~line 475) and `IsTypeProcessedInternal` (~line 522). At ~line 504 the code calls `AppleFrameworkRegistry.GetCompileImportSourceModules(swiftTypeName.Module)`. For `RealityKit` this returns `["RealityFoundation"]` (registered from `apple-frameworks.json`). It then probes `_modules["RealityFoundation"]`. **Fails in apple-framework mode because `RealityFoundation` was never `AddModuleDatabase`'d.**

**Wrong-qualification emission site**:
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` struct/enum/class processing (lines 347, 681, 729, 1241). `_namespacePatternResolver.ResolveNamespace(namedTypeSpec.Module)` is called with the queried module (`RealityKit`); when TypeDatabase lookup fails the `CSharpTypeName.Namespace` is set from the queried module name and downstream emission faithfully writes `RealityKit.Entity`.

**Dep declaration today**:
- `validation-libraries.json:77–87` (`RealityKit`) has no `dependencies` field.
- `validation-libraries.json:89–100` (`RealityFoundation`) declares `["RealityKit"]` — **upside-down for symbol ownership**. Symbols originate in `RealityFoundation` and are re-exported via `@_exported import` in `RealityKit`. Flip this in Session 1.

### Implementation sketch (three coordinated changes)

1. **`build/validation-libraries.json`** — flip the dep declaration: remove `"dependencies": ["RealityKit"]` from `RealityFoundation`'s product; add `"dependencies": ["RealityFoundation"]` to `RealityKit`'s product (apple-framework products don't currently use this field, so this introduces a new contract for that mode).
2. **`build/Build.Validation.cs::GenerateAppleFrameworkTarget`** — read `target.Dependencies`, resolve each dep's swiftinterface + tbd + ABI JSON from the SDK at the same step as the primary module, generate dep ABI JSON ahead of the primary, and pass `--module-database <depOutDir/RealityFoundation.db.xml>` (or thread the dep ABI JSON paths) to the primary generator subprocess.
3. **`src/Swift.Bindings/src/BindingsGeneratorCommand.cs`** — lift any gating that prevents `--module-database` from being honored without `--xcframework`. The XML DB path through `Program.cs::GenerateBindings` should already work end-to-end once the inputs are present.

### Risks to watch

- This changes a code path used by every apple-framework library. Watch for false-positive resolutions — a USR getting qualified to a dep module when it should stay queried-module. Surface this in Session 2, not Session 1 — Session 1's bar is filtered RealityKit/RealityFoundation working, not the full portfolio.
- Dep generation order matters: `RealityFoundation` must be generated *before* `RealityKit` in this run. **Phase 3a runs apple-framework targets in parallel** in the Nuke build — Session 1 has to make this deterministic. Two acceptable strategies: (a) generate dep DBs inline within the same target invocation (primary target produces its deps' DBs as a prelude), or (b) topologically schedule apple-framework generation so deps complete before dependents. Either is fine; pick one and document it. Don't rely on incidental ordering.

### Done when

- `nuke validate --filter RealityKit` reaches the C# compiler with **0 cross-module errors** (the 29 CS0234s gone).
- `nuke validate --filter RealityFoundation` reaches the C# compiler and surfaces the remaining ~78 errors (90 − ~12 cross-module).
- The dep-DB ordering strategy in Phase 3a is documented and deterministic.
- `nuke test` green; unit test added covering apple-framework dep threading (probably in `TypeDatabase` and `BindingsGeneratorCommand` tests).
- `/codex-review` clean.

(Full-portfolio regression sweep is explicitly Session 2's bar, not Session 1's. Session 1 doesn't have to leave the entire portfolio green — only filtered RealityKit/RealityFoundation.)

---

## Session 2 — Bucket #6 portfolio hardening ✅ completed (commit a64fd07a)

**Goal**: catch and fix any cross-portfolio regressions Session 1's threading change surfaced.

Session 1 changed a generic code path. Run full `nuke validate` and diff against entry baseline; for every newly-failing library identify whether it's a real regression (qualification resolved wrong, dep DB clobbered something) or a now-latent issue exposed by the new path. Fix the regressions; only update `.validation-baseline.json` once green is genuine.

If Session 1 happens to land with zero portfolio regressions, this session collapses to a baseline-update commit. Don't skip the diff step — confirm rather than assume.

### Done when

- Full `nuke validate` and `nuke test` green, `.validation-baseline.json` updated, no library worse than entry baseline.
- `/codex-review` clean.

---

## Session 3 — Bucket #7 + Misc: dedupe member emission + `default(T)` literal ✅ completed (commit 50e3ebc6)

**Goal**: 8 dedup errors + 2 default-literal errors gone.

### Prep notes

**Bucket #7 — duplicate ctor / `Count` emission**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` is the main ctor emission entry.
- `Count` is emitted in two places in `Emitter/StringEmitter/Handler/CollectionProjectionEmitter.cs`:
  - Line ~145: `csWriter.WriteLine($"public int Count => {backingCsName}.Count;");`
  - Line ~219: `csWriter.WriteLine("public int Count");` (property block form).
  Both can fire on the same type, producing CS0102.
- **Dedup infrastructure already exists**: `ProtocolSignatureHelper.GetProjectedCSharpMethodKey` (~line 108) is used in 13 places (see `ProtocolProxyEmitter.*`, `ProtocolHandler`, `MethodHandler`, etc.). `DefaultParameterOverloadEmitter.GetProjectedOverloadKey` (~line 221) mirrors it. The `EmittedProjectedSignatures` and `existingProjectedKeys` patterns are the established way to track per-type emitted members.
- **Constraint** (`.claude/rules/constraints.md`): "DefaultParameterOverloadEmitter.GetProjectedOverloadKey must match IHandler.GetProjectedCSharpMethodKey exactly. ~26 call sites across 15 files." Don't invent a parallel key format — extend the existing one if needed.

**Implementation sketch (#7)**:
- Add a per-type `HashSet<string>` of emitted member keys threaded through the type emission context (likely on `ModuleEmissionContext` so dedup spans constructors and property blocks alike).
- At each of the four emission sites (two ctor sites in `ConstructorWrapperEmitter`, two `Count` sites in `CollectionProjectionEmitter`), compute the projected key, check the set, skip if already present.
- Per `CLAUDE.md`: grep for *every* member emission site that could double-fire on the same type — bug is unlikely to be limited to ctor + Count.

**Misc — `null` for unconstrained `T`**:
- `src/Swift.Bindings/src/Marshaler/SwiftDefaultValueMapper.cs::MapNil` (~line 79). Currently: Optional → `"null"`, value types / `SwiftOptional` → `"default"`. The gap is the unconstrained-`T` case, which falls through to the Optional branch and emits `null` — illegal for an unconstrained generic in C#.
- **Fix**: detect `paramTypeSpec` being a generic-parameter reference (depth-0 `t_0_X` or depth-1+) and emit `"default"` (with no `(T)` since C#'s `default` literal infers from context — produces correct code for any T).

### Done when

- `RealityFoundation` error count drops by 10 (8 + 2).
- `nuke test` green; full `nuke validate` baseline preserved or improved.
- Unit tests added: a Theory feeding overload shapes that previously double-emitted; a generated-output check for `default` on unconstrained-T defaults.
- `/codex-review` clean.

---

## Session 4 — Bucket #5: relax `ISwiftObject` constraint for blittable-T generics ✅ completed (commit 92b384da)

**Goal**: 68 × CS0315 errors gone. This is the deepest change in the plan — touches four coordinated sites and the runtime metadata path.

### Prep notes

**`ISwiftObject` surface**:
- `src/Swift.Runtime/src/Swift/Runtime/ISwiftObject.cs` — interface with four static abstract members: `GetTypeMetadata()`, `NewFromPayload(IntPtr)`, `GetProtocolConformanceDescriptor<TProtocol>()`, plus instance `MarshalToSwift(ref Span<byte>)` and a default-throw `SwiftHandle` property.
- `SwiftObjectHelper<T>.GetTypeMetadata()` (~lines 62–83 in same file) is the entry point to VWT lookup; dispatches statically on NativeAOT, via reflection on Mono JIT.

**Constraint emission site**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs::GetWhereClause` (~line 109). Line 123 unconditionally seeds the constraint list with `"ISwiftObject"`:
  ```csharp
  var paramConstraints = new List<string> { "ISwiftObject" };
  ```
  Protocol proxies do the same in `ProtocolProxyEmitter.Helpers.cs::~line 132`.

**Member-skip gates that read the same constraint** (must be updated together):
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs::HasNonSwiftObjectGenericArg` (~line 550) — already special-cases `Swift.Optional`, `Swift.Result`, ObjC-bridgeable containers, `Foundation.Measurement`, `ManagedSettings.Token`, and SIMD aliases via `TryResolveBoundGenericAlias` (~line 684; `SIMD3<Float>` → `Vector3`).
- `Emitter/StringEmitter/MemberGateEvaluator.cs:103–107` — calls `HasNonSwiftObjectGenericArg` to tombstone members.
- `Emitter/StringEmitter/MemberValidationPipeline.cs:~line 327` — same call site, validation-pipeline path.
- Per `.claude/rules/constraints.md`: "`ShouldSkipConstraint` exists only in `BoundGenericsHandler`" — this is the single source of truth for "is this generic arg compatible".

**Runtime metadata resolution**:
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs::TryGetTypeMetadataUncached<T>` (~line 352). Currently handles: `ISwiftObject` (reflective `GetTypeMetadata()`), `IsValueTupleType`, then the manual `RegisterMetadata` cache, plus existentials, CoreGraphics structs, `Guid`, `SwiftVoid`. **Does NOT handle `System.Numerics` / SIMD types** (`Vector2`, `Vector3`, `Quaternion`). This — not the marshaller — is the actual runtime gap for blittable-T generics. A generic accessor for `MeshBuffer<Vector3>` needs a `TypeMetadata` for `Vector3`, and there is currently no path to produce one.
- `SwiftMarshal.MarshalFromSwiftCore<T>` (~line 466 and adjacent paths) **already** has a non-primitive blittable value-type path via `RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false` (3 sites in `SwiftMarshal.cs`). The marshaller side is fine — the missing piece is metadata, not a `Unsafe.Read<T>()` overload.
- The write path (`MarshalToSwift`, ~line 260) already handles `where T : struct` with no reference fields via raw `Unsafe.Write`.

**Existing precedent for blittable Ts**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs::IsBlittablePrimitiveSwiftType` (~line 669) — classifies primitive Swift type names as blittable.
- `Emitter/AbiContractChecker.cs:74` — `BlittableTypes` HashSet covering numeric primitives + known runtime blittable structs.
- These are checker-layer utilities, not constraint-emission utilities. The emitter would have to call into them.

**Design docs to read first**:
- `src/docs/Design/binding-generics.md` — prototype at line 46 already shows `where T : unmanaged, ISwiftObject`, indicating early design acknowledged blittable structs. Uses `GetTypeMetadataOrThrow<T>()` (separable from the `ISwiftObject` constraint).
- `src/docs/Design/runtime-metadata.md` — generic type metadata accessors take `TypeMetadata` per type parameter. Outer type's accessor still needs a `TypeMetadata` for inner T — must come from registration when T isn't `ISwiftObject`.
- `src/docs/Design/runtime-nominal-type-descriptor.md`, `src/docs/Design/binding-value-witness-table.md` — VWT is one word before `TypeMetadata`; cheap to obtain once `TypeMetadata` is registered.

### Pre-implementation grep inventory

Before writing any code, grep for every `where T : ISwiftObject` *emission* site (not just consumer / read site). The plan's prep names `GenericTypeEmitter` and `ProtocolProxyEmitter.Helpers`, but a recent grep also confirms emission paths in:
- `Emitter/StringEmitter/Handler/MethodValidationGates.cs`
- `Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs`
- `Emitter/StringEmitter/Handler/GenericClosureBridgeEmitter.cs`

And likely additional method-level sites Codex called out (`WrapperEmitter.Signature`, protocol-extension closure paths, enum marshalling helpers). Build the full inventory first; constraint relaxation has to be coordinated across **all** of them, not just the type-declaration site, or method-level signatures will keep emitting `where T : ISwiftObject` and contradict the relaxed type-declaration constraint.

### Metadata-source decision (must be made before coding)

`Vector2` / `Vector3` / `Quaternion` need `TypeMetadata` to flow through the generic-type metadata accessor for `MeshBuffer<Vector3>` etc. Three viable strategies; **pick one and write the choice into this doc** before starting:

1. **Runtime built-in lookup** — extend `TypeMetadata.TryGetTypeMetadataUncached<T>` with a SIMD / `System.Numerics` branch that returns the right metadata for the well-known SIMD types. Pro: single source of truth, no per-binding registration. Con: hard-codes Apple-SDK-specific types into `Swift.Runtime` (mild layering concern, but `Guid` / `SwiftVoid` are already there).
2. **Generated wrapper metadata functions** — RealityKit/RealityFoundation bindings emit wrapper metadata helpers per concrete blittable T they reference. Pro: localized to the bindings that need it. Con: more emission complexity; have to track which Ts need wrappers; possible duplication if multiple bindings use `Vector3`.
3. **`SwiftBindingsRuntime` module-init registration** — register the SIMD metadata via `TypeMetadata.RegisterMetadata()` from a module initializer. Pro: matches the existing enum-registration pattern. Con: **module-init timing**. Per Codex's repo read, generated initializers already deliberately avoid eager generic metadata to dodge a known crash class — confirm whether registering plain blittable struct metadata is in the safe subset before committing to this strategy.

**Recommendation (subject to the implementing session validating it against the runtime)**: option 1 (runtime built-in lookup) is the cleanest. SIMD types are stable, well-known, and small in number; treating them like `Guid` / `SwiftVoid` is the existing precedent. Mono JIT vs NativeAOT divergence in `SwiftObjectHelper<T>.GetTypeMetadata` is a non-issue here because the lookup never goes through `T.GetTypeMetadata()` — it short-circuits in `TryGetTypeMetadataUncached` before the `ISwiftObject` branch.

### Implementation sketch (in dependency order)

1. **Grep inventory** (above) — produce a complete list of `ISwiftObject`-constraint emission sites.
2. **Detect blittable-T instantiations** at every relevant emission site. Use `CdeclParamMapper.IsBlittablePrimitiveSwiftType` + `BlittableTypes` HashSet, plus `TryResolveBoundGenericAlias` for SIMD aliases.
3. **Relax constraint emission** across *all* sites from the inventory: `GenericTypeEmitter.GetWhereClause`, `ProtocolProxyEmitter.Helpers`, plus the method-level emitters (`MethodValidationGates`, `EnumHandler.CaseConstruction`, `GenericClosureBridgeEmitter`, and anything else the inventory turns up). When all concrete instantiations are blittable, drop `ISwiftObject`; if mixed, emit two surfaces (constrained `ISwiftObject` overload + unconstrained `unmanaged` overload).
4. **Update the skip gates** so blittable-T args don't tombstone members: extend `BoundGenericsHandler.HasNonSwiftObjectGenericArg` with a "is blittable" branch; downstream call sites in `MemberGateEvaluator` and `MemberValidationPipeline` inherit automatically.
5. **Implement the metadata-source decision** from above. If option 1: extend `TryGetTypeMetadataUncached<T>` with the SIMD branch. If option 2 or 3: implement the chosen strategy with the timing caveats noted.
6. **BindingTests coverage**: add Swift sources to `BindingTests/Sources/SwiftBindingsTestLib/` for a generic struct with a blittable-T generic param matching the `MeshBuffer<Vector3>` shape, plus C# tests on the matching domain file. Per `CLAUDE.md`, this is the real ABI gate — unit tests don't catch marshalling crashes.

### Risks / gotchas

- **All emission sites must move together** (the full inventory from step 1, not just three or four). Skipping one causes blittable-T methods to compile at the type level *but* still get tombstoned at the method level, or vice versa.
- **Module-init timing** (only relevant if option 2 or 3 is chosen): blittable struct `TypeMetadata` must be registered before the first instantiation. Generated initializers already avoid eager generic metadata for known crash reasons — option 1 sidesteps this by short-circuiting in `TryGetTypeMetadataUncached`.
- **NativeAOT vs Mono JIT divergence** in `SwiftObjectHelper<T>.GetTypeMetadata` is bypassed by option 1 (the lookup completes before reaching the `ISwiftObject` branch). For options 2 and 3, divergence is real — test on both via `nuke binding-tests` (sim) AND `nuke binding-tests --device`.
- The `MarshalFromSwiftCore<T>` "gap" mentioned in earlier drafts of this plan was wrong — the marshaller already has a non-primitive blittable value-type path. **Do not** add a redundant `where T : unmanaged` overload there.

### Done when

- 68 × CS0315 errors gone from `RealityFoundation`.
- New BindingTests pass on `nuke binding-tests` (sim) AND `nuke binding-tests --device` (NativeAOT).
- `nuke test` green; `nuke validate` baseline preserved on every other library.
- `/codex-review` clean.

---

## Session 5 — Bucket #5 edge cases + RealityKit/RealityFoundation final green ✅ completed (commits 3a3748dc + c614ae45)

**Goal**: clear stragglers from Session 4 and lock both libraries at 0 errors in `nuke validate`.

### What this catches

- Mixed-generic-arg cases (one blittable + one `ISwiftObject` T).
- Nested generics where the outer is blittable-T but the inner constrains.
- Anything else `nuke validate --filter RealityFoundation` / `--filter RealityKit` still reports.

### Outcome

Four root-cause fixes landed against generator/parser/marshaller layers (no XML or per-library
suppressions). RealityFoundation knownErrors **36 → 2**, RichTextKit **37 → 28**, WhatsNewKit
**4 → 2** as collateral wins from the same emitter improvements.

- **Fix A — `ValidationRuleSet.ReferencesUnsupportedModule` umbrella source-module probe.**
  Reverse-maps an umbrella TypeSpec name (`RealityKit.Entity.ChildCollection.IndexingIterator`)
  through `AppleFrameworkRegistry.GetCompileImportSourceModules` so the gate sees the canonical
  `RealityFoundation.…` skip entry recorded by the pre-pass.
- **Fix B — `NativeIntOverloadEmitter.ResolveType` bound-generic alias short-circuit.**
  Probes `TryResolveBoundGenericAlias` BEFORE the bare-name + generic-arg recursion fallback so
  `Swift.SIMD3<Swift.Float>` resolves to `simd.simd_float3`'s C# name instead of the synthetic
  `Swift.SIMD3<float>`.
- **Fix C — `SwiftABIParser` enum-case associated-value `TypeNameAlias` surgical unwrap.**
  When the textually-parsed tuple's element index has a `TypeNameAlias` ABI child, replace it
  with `CreateTypeSpec(child)` (which unwraps to the underlying nominal); preserve the textually
  parsed `TypeLabel`.
- **Fix D — `WrapperEmitter.Marshalling` B12 ObjC-optional gate delegates to
  `MarshallingHelpers.IsOptionalObjCBridged`.** Same precedence rule as `TypeProjectionFactory`:
  TypeRecord-first, with the auto-bridge fallback gated on `IsOptionalFallbackModule` +
  `HasObjCClassPrefix`. Prevents `?.Handle` emission for plain Swift classes whose ABI
  `printedName` uses an umbrella re-export module — the previous bespoke logic relied on the
  broader `IsObjCModuleType` heuristic and misclassified such types.
- **Fix B follow-on — `Swift.Optional<T>` fallback emits C# nullable form.** When
  `NativeIntOverloadEmitter.ResolveType` falls through the projection layer (incomplete
  TypeDatabase, fragmented generic context), `Swift.Optional<T>` now emits `T?` instead of the
  raw generic shape `Swift.Optional<T>` — that shape is not a valid C# type and would CS0234
  the int overload. Locked in by the nested-Optional regression test.

### Tests

`src/Swift.Bindings/tests/UnitTests/EmitterTests/RealityFrameworkRemapFixTests.cs` — six tests
covering all four fixes (A positive + negative, B alias short-circuit, B-nested
`Optional<SIMD3<Float>>` resolves to `System.Numerics.Vector3?`, C enum-case unwrap with
non-alias element preserved, D ObjC-optional gating defers to TypeRecord).

### Follow-on fixes — RealityFoundation 2 → 0

The two structural patterns that closed Session 5's RealityFoundation gap:

1. **Bug 1 — `Optional<(String, Class)>` per-element decomposition** (`TupleProjection.GetReturnElementConversion`).
   `OptionalProjection`'s "no element conversion" path was casting `_swiftOpt.Some` (an unmaterialised
   `ValueTuple<SwiftString, IntPtr>`) directly to `(string, Animal)?` and tripping CS0030. The fix
   overrides `GetReturnElementConversion` on `TupleProjection` to walk elements: each non-class
   element delegates to its projection's per-element conversion; class elements emit the explicit
   `(T)SwiftMarshal.MarshalFromSwiftObject<T>(itemAccess)` lift to materialise the ARC pointer.
   Pinned by `OptionalTupleOfStringClass_GetReturnPlan_DecomposesPerElement` (unit) and
   `OptionalMarshallingTests.TestFirstNamedAnimal{Some,None}` (BindingTests).
2. **Bug 2 — `[Int: URL]` NSDictionary integer-key unbox** (`DictionaryProjection.FromNSObject` /
   `ToNSObject`). When the value type is ObjC-bridgeable (`URL`/`NSDate`/etc.) the entire `[K:V]`
   bridges to `NSDictionary`, so the Swift `Int` keys arrive as boxed `NSNumber`s under `NSObject`.
   The pre-fix return path emitted `(nint)_nsKey` and the parameter path emitted
   `(Foundation.NSObject)kvp.Key` — both tripping invalid primitive↔NSObject casts. The fix routes
   primitive keys through a symmetric pair of NSNumber tables: returns unbox via the matching
   accessor (`NIntValue` / `Int32Value` / `DoubleValue` / etc.), parameters box via the matching
   `Foundation.NSNumber.FromXxx(...)` factory. Both `BlittableProjection` (12 primitive numerics)
   and `BoolProjection` (Swift `Bool`, which has its own projection class because the P/Invoke side
   needs `[MarshalAs(UnmanagedType.U1)]`) flow through the tables. Pinned by
   `DictionaryIntUrl_ObjCBridgeReturn_UnboxesIntKeyViaNSNumber` (unit), the broader
   `DictionaryBlittableKey_ObjCBridgeReturn_UsesMatchingNSNumberAccessor` `[Theory]` covering the
   12 numeric primitive types, the `DictionaryBoolKey_ObjCBridgeReturn_UnboxesViaBoolValue` Fact,
   the parameter-side `DictionaryBlittableKey_ObjCBridgeParameter_BoxesViaNSNumberFactory` `[Theory]`
   and `DictionaryBoolKey_ObjCBridgeParameter_BoxesViaFromBoolean` Fact, and
   `URLContainerBridgeTests.TestGetURLsBySample` (BindingTests).

### Dep-gate close-out (commit `c614ae45`)

The two `ARView.Raycast` ObjC-USR mismatches (CS0234 ×2 on `ARKit.ARRaycastQueryTarget` /
`ARKit.ARRaycastQueryTargetAlignment`) were closed by registering the canonical ObjC names in
`apple-frameworks.json` so the cross-module resolver finds them at qualification time. RealityKit
dep-gate dropped 3 → 1 (14/15 libraries pass dep gate).

### Remaining (1 follow-up — see roadmap)

`MultipeerConnectivityService.Owner(Entity)` (CS0535) is still suppressed. Diagnosis traced it to a
classification mismatch in `TypeDatabaseExtensions.IsObjCModuleType` that over-classifies Swift-only
protocols in autoBridge modules — load-bearing helper with cross-framework blast radius, not a
soft-gate addition. Tracked as **P1 — Existential return-type suppression for autoBridge-module
Swift-only protocols** in `src/docs/roadmap.md`.

### Done when

- `nuke validate --filter RealityFoundation` reports **0 errors** ✅
- RealityKit dep-gate down to 1 known follow-up (tracked in roadmap) ✅
- `.validation-baseline.json` updated; `cs_compile` and `swift_compile` reflect the wins ✅
- `/codex-review` clean ✅

---

## Session 6 — RealityKit dep-gate close-out + final shipping gate ✅ completed (commit 80174bbe)

**Goal**: clear the three RealityKit dep-gate errors unmasked by Session 5, then prove regression-clean and ABI-safe end-to-end.

### Pre-shipping fixes

1. **`Optional<existential>` return-type fallback on class-side method emission.** Make
   `MemberEmissionValidator`'s `UnsupportedExistential` skip on a method match the protocol-decl
   path: emit the method with `[UnsupportedSwiftType]` and an `object?` return. BindingTests
   coverage on a class implementing a protocol where one method returns `Optional<some-existential>`.
2. **ObjC-USR cross-module nested-enum naming pass.** When emitting a parameter / return type whose
   USR is `c:@E@<ObjCName>` and whose Swift `printedName` is a nested form (`Foo.Bar`), prefer the
   ObjC name from the USR. Generic across all Apple frameworks; verify against ARKit raycast types
   and a couple of other nested ObjC enums in the validation portfolio.

### Final gates

- Full `nuke test`, full `nuke validate`, `nuke binding-tests` (sim).
- `nuke binding-tests --device` (NativeAOT) — required because Sessions 1 and 4 touched calling-convention / marshalling code.
- Diff `.validation-baseline.json` and BindingTests pass count against entry; both ≥ entry per `CLAUDE.md` zero-regression policy.
- If a pattern landed in Session 4/5 isn't reproduced in BindingTests, add it. The validation portfolio is not a substitute for the in-tree end-to-end gate.

### Done when

- All gates green, baseline updated in the same commit as the last fix.
- BindingTests covers every new emission shape introduced by the plan.
- `/codex-review` clean on the cumulative branch diff.

---

## Session 5b — Existential filter narrowing for autoBridge Swift-only protocols ✅ completed

**Goal**: retire the P1 dep-gate suppression discovered in Session 5 — `MultipeerConnectivityService.Owner(Entity) -> any RealityFoundation.SynchronizationPeerID` was being filtered out because `TypeDatabaseExtensions.IsObjCModuleType` classified every non-value-type from an autoBridge module as ObjC, even when the type's name didn't match its module's `objcPrefixes`. That dropped the protocol from `ExistentialHandler.GetEffectiveProtocols`, returned `"object"` from `GetPublicExistentialType`, and tripped `B6 UnsupportedExistential`.

### Shape of the fix (Option A — narrow predicate, broad helper preserved)

1. **`AppleFrameworkRegistry.IsObjCBridgedTypeName(module, qualifiedName)`** — public per-module ObjC prefix gate. Three tiers: autoBridge gate, valueTypes exclusion, typeRemaps signal, then per-module `objcPrefixes` match.
2. **`TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(typeSpec)`** — narrow helper used ONLY at existential filter / parity-guard sites. `IsObjCModuleType` stays broad so the synthetic-record `ObjCBridgingStrategy` path (e.g. `RealityKit.Entity` collapsed from `RealityFoundation.Entity`) is unaffected.
3. **Filter / parity-guard sites updated to call the narrow predicate**: `ExistentialHandler.GetEffectiveProtocols`, `ExistentialHandler.QualifyProxyClassName`, two `WitnessDispatchEmitter` parity guards, one `ProtocolExtensionEmitter` parity guard, one `BoundGenericsHandler` parity guard, two `ClosureHandler` parity guards. `QualifyProxyClassName` additionally drops marker protocols (mirrors `GetEffectiveProtocols` predicate) so a composition ordered `Swift.Sendable & OtherModule.Protocol` qualifies by `OtherModule` rather than picking `Swift` and emitting unqualified.
4. **`ProtocolHandler.GetCSharpTypeName`** — added `CurrentModuleName = protocolContext?.ModuleDecl?.Name` to its `ProjectionContext`. Without this, cross-module existential interface members emitted unqualified names (e.g. `IHasCollision?` instead of `RealityFoundation.IHasCollision?`) on the interface side while the implementation emitted the qualified form, tripping CS0246 + CS0738.

### Coverage

- **Unit tests**: `AppleFrameworkRegistryTests.IsObjCBridgedTypeName_ReturnsExpected` (17 cases), `TypeDatabaseExtensionsTests.IsObjCExistentialBridgedProtocol_PerModulePrefixGate` (10 cases), `IsObjCModuleType_BroadAutoBridgePreserved` (5 cases pinning the broad predicate stays broad for umbrella-collapsed types), `ExistentialHandlerTests.QualifyProxyClassName_MarkerFirstThenSwiftModule_QualifiesCorrectModule` (pins the marker-filter parity in `QualifyProxyClassName`).
- **BindingTests fixture**: `Protocols/AutoBridgeSwiftOnlyExistentialReturn.swift` mirrors the suppression pattern via `Foundation.LocalizedError` (autoBridge with `["NS"]` prefix; LocalizedError doesn't match). `AutoBridgeSwiftOnlyExistentialTests` asserts the method is reachable and the existential round-trips on repeated invocations.

### Result

- Compile gate: 115/115 standalone (was 114/115).
- Overall validation: 129/129 (was 128/129).
- Dependencies: 15/15 (was 14/15) — RealityKit close-out target met.
- Skip count down (more methods now emit instead of being filtered).
- Runtime gates green; sim baseline 1771 → 1774, device baseline 1784 → 1785 (the 3 new fixture tests pass on both Mono JIT sim and NativeAOT device).

---

## Session 7 — Packaging hand-off

**Goal**: produce the contract the downstream `swift-dotnet-packages` repo needs to ship the NuGet package, without doing the packaging work itself.

- Append a "Packaging contract" section to this file: target TFMs (iOS-only at entry — RealityKit doesn't ship elsewhere), declared SDK / framework deps (`RealityKit` package depends on `RealityFoundation` package + `SwiftBindings.Runtime` range), platform version, consumer-visible limitations.
- Sanity-check `nuke pack --version <X.Y.Z-preview>` produces the three NuGets without errors.
- Per memory note: Apple-framework smoke tests are temporary scaffolding — don't overinvest. If `swift-dotnet-packages` already wires the smoke pattern, just point at it.

### Done when

- "Packaging contract" section in this file the downstream repo can consume directly.
- Local `nuke pack` runs clean.

---

## Out of Scope

- Anything inside the downstream `swift-dotnet-packages` repo (NuGet publishing, consumer smoke app, README updates).
- Other apple-framework libraries that gain coverage as a side effect of Session 1 — bank wins, don't expand goals.
- Multi-TFM (macOS / Catalyst / tvOS) RealityKit. iOS-only at entry; revisit only if Apple ships it elsewhere.

---

## Tracking

Update the **Status** line at the top after every session: bucket completed, current `RealityKit` / `RealityFoundation` error counts from `nuke validate`, next session number.

---

## Plan review history

A direction-level `/codex-review` on this plan was completed before Session 1. Codex confirmed:
- Bucket ordering (#6 → #7+Misc → #5) is correct; error-count-driven ordering would design against an error-skewed surface.
- Flipping the `validation-libraries.json` dep direction matches the repo's `compileImportModule` model.
- `--module-database` is the conservative architectural path; a direct ABI-parse alternative would require new CLI surface and isn't worth the cost here.

Codex amendments folded into this doc:
- Session 1 done-when narrowed to filtered RealityKit/RealityFoundation; full-portfolio regression is explicitly Session 2's bar.
- Phase 3a's parallel apple-framework target execution is called out as something Session 1 must make deterministic (inline dep generation OR topological scheduling).
- Bucket #5 prep corrected: the marshaller `MarshalFromSwiftCore<T>` blittable path already exists; the real gap is `TypeMetadata.TryGetTypeMetadataUncached<T>` not knowing about SIMD / `System.Numerics` types.
- Bucket #5 metadata-source decision added with three options and a recommendation (runtime built-in lookup).
- Bucket #5 grep inventory pre-step added — `where T : ISwiftObject` emission spans more than the three sites originally named.
