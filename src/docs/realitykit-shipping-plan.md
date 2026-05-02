# RealityKit / RealityFoundation Shipping Plan

**Goal**: get `RealityKit` and `RealityFoundation` compiling clean against the validation gate so the pair can ship as a NuGet package. NuGet publishing happens in the downstream `swift-dotnet-packages` repo and is out of scope here — this plan ends when both libraries are at 0 errors in `nuke validate`, BindingTests cover the new patterns, and a packaging contract is documented.

**Status (entry)**: pre-experiment baseline.
- `RealityKit`: 29 × CS0234 (cross-module type qualification — references to `RealityKit.Entity` / `RealityFoundation.Entity` / `ARKit.ARRaycastQueryTarget` / `Swift.SIMD3` that don't resolve).
- `RealityFoundation`: skipped (compile-gated on `RealityKit`).

**Status**: Sessions 1–7 done. Session 7 published the downstream-consumable Packaging contract (TFM, dep edges, platform floor, consumer limitations) and re-ran `nuke pack --version 0.10.0-preview.1 --apple-version 26.2.2-preview.1` clean as a sanity check. Dep gate at 15/15; RealityKit/RealityFoundation both at 0 errors. Plan complete — packaging executes downstream in `swift-dotnet-packages`.

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

## Session 5c — Umbrella mangled-name acceptance + proxy reference qualifier

**Goal**: close the symmetric proxy-class-side gap left by 5b. After 5b's interface-side fix, RealityFoundation's apple-framework-mode generated output still emitted only 3 of 7 expected proxies (BindableData, BlendTreeNode, AnimatableData) — the four missing protocols (Component, HasAnchoring, SynchronizationPeerID, SynchronizationService) had their interfaces emit but their proxy classes were skipped, and reference sites in RF.cs dangled with `RealityKit.SwiftInterop.<X>Proxy` qualifications that pointed at the umbrella's namespace, producing 8 CS0246 errors when `swift-dotnet-packages` built RealityFoundation against the SDK.

The user's initial framing (broad `IsObjCModuleType` filter still gating proxy emission) didn't survive investigation. The actual root cause was Apple's `@_implementationOnly` umbrella collapse leaking into two independent name-resolution sites:

1. **Mangled-name filter rejected umbrella prefixes.** `ModuleHandler.IsMangledNameFromModule` requires `$s{len}{moduleName}` to match exactly. The 4 broken protocols carry `mangledName=$s10RealityKit...` (umbrella encoding) even though `usr=s:17RealityFoundation...` and `moduleName=RealityFoundation`. The filter rejected them, so they never reached the EveryProtocol pipeline — `binding-report.json` recorded `EveryProtocolConformanceSkipped: "no decision recorded"` for all four.
2. **Proxy reference qualifier read printedName module.** `ExistentialHandler.QualifyProxyClassName` extracted `p.Module` directly from the parsed protocol type. Apple's ABI emits `printedName: "any RealityKit.HasAnchoring"`, so `p.Module = "RealityKit"` even when the protocol's TypeRecord lives in `RealityFoundation.SwiftInterop`. Without umbrella-aware resolution, every cross-module qualification at proxy reference sites dangled.

### Shape of the fix

1. **`ModuleHandler.IsMangledNameFromModule`** — also accept `$s{len}{umbrella}` when `AppleFrameworkRegistry.MapModuleToCompileImport(moduleName)` resolves to a different umbrella module. Source-truth check stays via the registered remap — only modules with a `compileImportModule` entry in `apple-frameworks.json` get the umbrella branch, so non-Apple modules (CryptoSwift, Nuke) are unaffected. The umbrella's own pass is unaffected too: the umbrella module name has no further remap.
2. **`ExistentialHandler.QualifyProxyClassName`** — replace `Select(p => p.Module)` with `Select(p => ProtocolConformanceHelper.ResolveProtocolEmissionModule(SwiftTypeName.FromTypeSpec(p), _typeDatabase))` so the umbrella key resolves to the source module's `CSharpTypeName.Namespace`, mirroring `GetPublicExistentialType`'s already-umbrella-aware resolution path.

### Coverage

- **Unit tests**: `ModuleHandlerTests.IsMangledNameFromModule_AcceptsCompileImportUmbrellaPrefix` (8 cases pinning umbrella acceptance for the 4 broken protocols, native prefix still accepted, umbrella's own pass unaffected, unrelated modules unaffected). `ExistentialHandlerTests.QualifyProxyClassName_AppleUmbrellaPrintedName_CollapsesToSourceModule` + `_SameSourceModule_NoQualification` (cover both the cross-assembly fully-qualified form and the same-module unqualified form).
- **End-to-end gate**: downstream `swift-dotnet-packages` `RealityFoundation` and `RealityKit` csproj builds. BindingTests cannot reproduce this fix's pattern in isolation — `@_implementationOnly` umbrella collapse requires a multi-module Swift package where one module re-exports another with that attribute, which the single-module BindingTests harness can't express. The downstream RF + RK builds are the durable end-to-end gate.

### Follow-on hardening (same session)

Lifting the mangled-name umbrella filter exposed two adjacent bugs that had been masked by the original suppression:

3. **Conflict-pre-scan including default implementations dropped legitimate protocols.** With the umbrella prefix accepted, RealityKit's `Material` extension default `name: String?` reached the conflicting-property-name pre-scan in `ModuleHandler.cs`. The scan compared property *types* by name across all protocols in the EveryProtocol candidate set, including default-implementation properties from same-protocol extensions. `name: String` (a real protocol requirement on BlendTreeNode/AnimationDefinition/MaterialFunction) collided with `name: String?` (a default impl on Material) and dropped all three legitimate protocols. **Fix**: added `prop.IsProtocolRequirement` to the scan filter — only requirements contribute witnesses to EveryProtocol's conformance, so default impls have no business participating in the conflict check. Restores BlendTreeNode/AnimationDefinition/MaterialFunction proxies that 5c's umbrella fix would otherwise have lost.
4. **Cross-module suppressed-proxy visibility through dependency XML.** When RF suppresses a proxy (UnsatisfiedProtocolConstraint, etc.), RK's pass — running with `--module-database RealityFoundationDatabase.xml` — emits cross-module qualified references like `new RealityFoundation.SwiftInterop.HasCollisionProxy(...)` via the umbrella-aware `QualifyProxyClassName`. Pre-fix, RK's post-pass only knew its own suppressed set, so those refs survived as CS0246 in downstream RK builds. **Fix**: `ModuleDatabaseEmitter` now writes a `<suppressedProxies namespace="...">` element listing names from `EmissionContext.SuppressedProxyClassNames`. The `namespace` attribute persists the dependency's *C# namespace* (NOT the Swift module name — those diverge under a custom `namespacePattern`); `TypeDatabase.ReadVersion1_0` parses it onto `ModuleTypeDatabase` (falling back to the Swift module name for legacy databases that predate the attribute); `ITypeDatabase.GetCrossModuleSuppressedProxyClassNames()` returns `(Namespace, ProxyName)` pairs; `Program.cs` post-pass builds the cross-module set as `{Namespace}.SwiftInterop.{ProxyName}` and threads it into `CSharpWrapperCoGater.ProcessSuppressedProxyReferencesInDirectory` separately from the local set. The local set matches ONLY bare or `SwiftInterop.`-only forms; the cross-module set matches ONLY the fully qualified `{DepNamespace}.SwiftInterop.{Proxy}(` form. This keeps a future module that legitimately emits its own proxy with the same simple class name from being false-positive stripped, and prevents a dependency's suppressed proxy from incorrectly stripping references into a different (non-suppressing) dependency. Backwards-compatible: older databases without the element behave as if the dependency suppressed nothing.

#### Coverage for the follow-on

- **Unit tests**: `ModuleHandlerTests` (existing requirements-only behavior pinned with new property-conflict cases). `ModuleDatabaseEmitterTests.Emit_SuppressedProxies_RoundTripsThroughXml`, `_NoSuppressedProxies_OmitsElement`, `_UnionsAcrossLoadedDependencies` cover the XML schema, omission, and multi-dependency union.
- **End-to-end**: `nuke validate` baseline updated, RealityKit improved fail(6) → ok(0). Full unit test suite (10704) passes.

---

## Session 7 — Packaging hand-off ✅ completed

**Goal**: produce the contract the downstream `swift-dotnet-packages` repo needs to ship the NuGet package, without doing the packaging work itself.

- Append a "Packaging contract" section to this file: target TFMs (iOS-only at entry — RealityKit doesn't ship elsewhere), declared SDK / framework deps (`RealityKit` package depends on `RealityFoundation` package + `SwiftBindings.Runtime` range), platform version, consumer-visible limitations.
- Sanity-check `nuke pack --version <X.Y.Z-preview>` produces the four NuGets (Runtime + Sdk + Templates + Apple supplement) without errors. The Session 7 sanity run packed `0.10.0-preview.1` (with `--apple-version 26.2.2-preview.1`) and confirmed all four nupkgs land in `$TMPDIR/swift-nuget/` (= `/var/folders/.../T/swift-nuget` on macOS, since `Path.GetTempPath()` resolves there). Stamped versions verified: SDK nupkg's `Sdk.props` carries `SwiftRuntimePackageVersionRange=[0.10.0-preview.1,0.11.0)`; Apple-supplement nupkg's nuspec carries the matching bounded `SwiftBindings.Runtime` dep edge across all four Apple TFM groups. `Sdk.props` and the supplement csproj are restored to their original `0.9.0` / `26.2.1` values on Dispose.
- Per memory note: Apple-framework smoke tests are temporary scaffolding — don't overinvest. `swift-dotnet-packages` already wires the smoke pattern as `nuke PackValidateAppleFramework --library RealityKit` (see `Build.AppleFramework.cs::PackValidateAppleFramework`); the contract below points at it instead of duplicating.

### Done when

- "Packaging contract" section in this file the downstream repo can consume directly. ✅
- Local `nuke pack` runs clean. ✅

---

## Packaging contract

This section is the contract the downstream `swift-dotnet-packages` repo consumes to ship `SwiftBindings.Apple.RealityKit` + `SwiftBindings.Apple.RealityFoundation` to NuGet. A downstream packager should be able to implement the package projects without reading the rest of this design doc.

### Scope

Two NuGet packages, one per Swift module:

| Package ID | Backs Swift module | Consumer-facing namespace |
|---|---|---|
| `SwiftBindings.Apple.RealityFoundation` | `RealityFoundation` | `RealityFoundation.*` |
| `SwiftBindings.Apple.RealityKit` | `RealityKit` | `RealityKit.*` |

Symbols originate in `RealityFoundation` and are re-exported via `@_exported import` from `RealityKit`. Consumers that need just the core ECS / animation surface depend on `RealityFoundation`; consumers that need the rendering / interaction layer depend on `RealityKit` (which transitively pulls `RealityFoundation`).

### Target framework

- **TFM**: `net10.0-ios26.2` (single-TFM, iOS-only at entry).
- **`SupportedOSPlatformVersion`**: defaults to `15.0` (the SDK's `SwiftAppleFrameworkMinDeploymentVersion` floor for iOS — see `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:56`). The downstream package may override by setting `<SwiftAppleFrameworkMinDeploymentVersion>` in the csproj if a higher floor is desired (e.g., to use APIs that were added after iOS 15.0). The SDK only warns if `SupportedOSPlatformVersion` falls below the SwiftBindings floor; it does not error.
- **Why `26.2` in the TFM**: the platform version segment of the TFM is the Apple **SDK build** version used at compile time (resolved from `build/validation-libraries.json::platformVersion`, currently `26.2`). The `SupportedOSPlatformVersion` (deployment floor) is a separate property and stays at `15.0`. These two should not be conflated. When the upstream Apple SDK train bumps (e.g., to `27.0`), the TFM segment moves with it; the deployment floor stays put unless the consumer explicitly raises it.

Multi-TFM (macOS / Catalyst / tvOS) RealityKit is explicitly out of scope. RealityKit ships iOS-only at this entry; revisit only if Apple ships it elsewhere (per "Out of Scope" below).

### SDK and dependency edges

Both package projects use the `SwiftBindings.Sdk` MSBuild SDK. Minimum project shape (identical for RealityKit and RealityFoundation modulo `PackageId` / `SwiftAppleFrameworkTarget`):

```xml
<Project Sdk="SwiftBindings.Sdk/X.Y.Z">
  <PropertyGroup>
    <!-- Override the single-TFM default from swift-dotnet-packages/Directory.Build.props:3
         (which sets <TargetFramework>net10.0-ios</TargetFramework>) so we can declare
         the versioned multi-target form below. -->
    <TargetFramework />
    <TargetFrameworks>net10.0-ios26.2</TargetFrameworks>
    <PackageId>SwiftBindings.Apple.RealityKit</PackageId>
    <Version>26.2.1</Version>
  </PropertyGroup>
  <ItemGroup>
    <SwiftAppleFrameworkTarget Include="RealityKit" />
  </ItemGroup>
</Project>
```

No manual `<PackageReference>` is needed for the inter-framework dep edge — the SDK auto-injects it from the swiftinterface's `import` lines (see "Inter-framework dep edge" below). Reference shape verified against `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/RealityKit/SwiftBindings.Apple.RealityKit.csproj` and the matching `RealityFoundation` project, both currently at SDK `0.9.0`.

The SDK injects two implicit dep edges into the packed nuspec for any apple-framework binding:

1. **`SwiftBindings.Runtime`** — bounded version range `[X.Y.Z,X.(Y+1).0)`, threaded from `SwiftRuntimePackageVersionRange` in `src/Swift.Bindings.Sdk/Sdk/Sdk.props`. Currently `[0.9.0,0.10.0)`. The bounded range is load-bearing: a bare floor would let consumers slide into the next minor across an incompatibility boundary. Stamped at pack time by `VersionScope.StampSdkProps` (`build/Helpers/VersionScope.cs:121`).
2. **`SwiftBindings.Apple`** — floor-only range `[$(SwiftAppleSupplementVersion),)`, currently `[26.2.1,)`. Floor-only (not bounded) by design: the supplement is additive across Apple SDK trains, so consumers can always float forward onto a newer Apple supplement.

**Inter-framework dep edge (`SwiftBindings.Apple.RealityKit` → `SwiftBindings.Apple.RealityFoundation`) is auto-injected by the SDK.** `_DetectAppleFrameworkCrossModuleDeps` (`src/Swift.Bindings.Sdk/Sdk/Sdk.targets`) parses the swiftinterface's `import` lines via the generator's `--detect-apple-cross-module-deps` subcommand, resolves them against `apple-frameworks.json`'s `packageId` map (`AppleFrameworkRegistry`), and synthesizes one `<PackageReference Include="SwiftBindings.Apple.<Module>" Version="[X.Y.Z,X.(Y+1).0)" />` per detected dep. The bounded range matches the per-Apple-SDK-train versioning policy below — `RealityFoundation`'s binding surface is an Apple-SDK-train ABI, not an additive contract, so floating across a minor (which here corresponds to a new Apple SDK train) is unsafe. Marker imports (`Swift`, `_Concurrency`, `_StringProcessing`, `simd`, etc.) are filtered out; modules with no `packageId` registered are skipped silently.

Auto-injection runs inside `BeforeTargets="ResolveProjectReferences;CollectPackageReferences"`, so it fires during both `dotnet restore` and `dotnet pack`. The injected `<PackageReference>` flows into `project.assets.json` at restore time and into the packed nuspec's `<dependencies>` group at pack time. A user-declared `<PackageReference>` of the same identity wins (the inject ItemGroup dedups against existing identities), so a downstream consumer that wants to pin or widen the range can do so by adding their own `<PackageReference>` for the dep — auto-injection backs off without warning. Opt out entirely with `<SwiftAutoDetectAppleFrameworkDependencies>false</SwiftAutoDetectAppleFrameworkDependencies>` in the consumer csproj.

Why this is `<PackageReference>` only and does *not* include `<SwiftFrameworkDependency>`:

- `<SwiftFrameworkDependency>` is xcframework-mode vocabulary. The CLI option it feeds (`--framework-dependency`) explicitly **requires `--xcframework`** (see `src/Swift.Bindings/src/CliOptions.cs`) and rejects non-`.xcframework` paths. The apple-framework generator target (`_GenerateSwiftBindingsAppleFramework`, `Sdk.targets`) does not append `--framework-dependency` to its command line at all — that flag is appended only inside the `@(SwiftFramework)`-gated xcframework propertygroup. Adding `<SwiftFrameworkDependency>` to an apple-framework project is at best a no-op for code generation; it would also become a `NativeReference` (`Sdk.targets`) which is undesired when the framework is already SDK-resolved via `_SwiftAppleFrameworkSdkPath`.
- `_ValidateSwiftDependencyMetadata` only fires *if* `<SwiftFrameworkDependency>` items exist; it does not synthesize the nuspec dependency. NuGet pack serializes the `<dependencies>` group from `<PackageReference>` items.

The supplement's own `Swift.Runtime` ProjectReference is stamped to the bounded range at pack time by the override target in `src/Swift.Bindings.Apple/Swift.Bindings.Apple.csproj` — `_GetProjectReferenceVersions` doesn't honor `<Version>`/`<VersionOverride>` metadata on `<ProjectReference>`, so the override is required to keep the supplement nupkg from shipping a min-only Runtime dep that defeats the bounded range. The downstream packager doesn't need to reproduce this; it's already done in this repo for the supplement's pack flow.

> **Downstream migration note.** The current
> `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/RealityKit/SwiftBindings.Apple.RealityKit.csproj`
> still carries an explicit `<PackageReference Include="SwiftBindings.Apple.RealityFoundation" Version="[26.2.1,26.3.0)" />`.
> That block is now redundant — the SDK auto-injects the same edge with the same bounded range —
> but harmless: dedup logic ensures the user-declared `<PackageReference>` wins. The downstream
> packager may delete the manual block at any time without changing nuspec output.

### Versioning

- `SwiftBindings.Apple.RealityKit` and `SwiftBindings.Apple.RealityFoundation` are versioned **per Apple SDK train** (currently `26.2.1`), not per Runtime/SDK. Bump the patch when re-packing against the same Apple SDK with binding fixes; bump minor when picking up a new Apple SDK train.
- The two packages should ship in lockstep — RealityKit's manually-declared dep edge `[X.Y.Z,X.(Y+1).0)` permits patch float on RealityFoundation within the same Apple SDK train (e.g., a `26.2.2` re-pack picks up a `26.2.x` RealityFoundation rebuild) but caps below the next train. A downstream-paired publish (same patch on both) is the recommended cadence.
- Pre-release builds use NuGet's standard `-preview.N` suffix (e.g., `26.2.2-preview.1`). Session 7's sanity check used `nuke pack --version 0.10.0-preview.1 --apple-version 26.2.2-preview.1`; both versions sort above the currently-published `0.9.0` Runtime/SDK and `26.2.1` supplement.

### Build / smoke gate

- Compile gate (downstream): `nuke PackValidateAppleFramework --library RealityKit` — defined in `swift-dotnet-packages/build/Build.AppleFramework.cs::PackValidateAppleFramework`. Wraps `dotnet pack` with a smoke version (`0.0.0-ci`) and verifies the nupkg is well-formed. (Note: `PackValidate` without the `AppleFramework` suffix is a different, third-party-library target in `Build.Pack.cs` that expects a `libraries/<name>/library.json` and is the wrong gate for apple-framework projects.) There is no separate runtime smoke app for RealityKit at present (per `swift-dotnet-packages/apple-frameworks/RealityKit/`, only `README.md` + csproj). Don't add one as part of shipping — Apple-framework smokes are explicitly throwaway scaffolding (memory note `project_smoke_tests_temporary`); the BindingTests in this repo are the durable end-to-end gate.
- Run `PackValidateAppleFramework` for both `RealityFoundation` and `RealityKit` before publishing.
- **Ordering & restore source for the RealityKit gate.** `PackValidateAppleFramework` outputs to `artifacts/packages/` (see `swift-dotnet-packages/build/Build.Pack.cs:17`), but the repo's `NuGet.config` declares `local-packages/` as its only local source (alongside `nuget.org`). Because the RealityKit csproj carries a `<PackageReference Include="SwiftBindings.Apple.RealityFoundation" Version="[26.2.1,26.3.0)" />`, restore inside `PackValidateAppleFramework --library RealityKit` will fail unless that exact-versioned RealityFoundation nupkg is reachable. Two acceptable orderings: **(a)** publish `SwiftBindings.Apple.RealityFoundation 26.2.1` to nuget.org first, then validate RealityKit (cleanest pre-publish workflow); or **(b)** pack RealityFoundation locally with the real version (`dotnet pack apple-frameworks/RealityFoundation/SwiftBindings.Apple.RealityFoundation.csproj -c Release -p:Version=26.2.1 -o local-packages`), then run `PackValidateAppleFramework --library RealityKit`. The smoke target itself does not reproduce this ordering automatically — the downstream packager owns it.

### Consumer-visible limitations

These follow from the broader binding-generator surface; they are not RealityKit-specific bugs and do not block shipping. Surface them in package release notes / README so consumers know what to expect.

| Limitation | Where bound | Notes |
|---|---|---|
| Bare-generic / associated-type signatures | `roadmap.md` Theme A — `UnsupportedSignatures` (~611 portfolio-wide) | Skipped at generation. Affects RealityKit subscript/PAT shapes (e.g., `MeshResource` subscripts where the return type is an associated type). No C# equivalent for Swift PATs. |
| Non-trivial closure shapes | `roadmap.md` Theme A — `UnsupportedClosure` (~188 portfolio-wide) | Closures with generic parameters, nested closures, and async-closure shapes outside the supported arg/return matrix are skipped. Closures with primitive args/returns and the documented `Foundation.Data` zero-arg async shape work. |
| Multi-protocol generic compositions | `roadmap.md` Theme A — blocked | Composition like `<T: A & B>` is not yet expressible across the `@_cdecl` boundary. RealityKit/RealityFoundation hits one such case (`MultipeerConnectivityService.Owner(Entity) -> any SynchronizationPeerID`) — closed in Session 5b, but related compositions in other RealityKit APIs may still skip. |
| Subscript return / index AnyType (PAT-shaped) | `roadmap.md` AnyTypeFallback decomposition (62 portfolio-wide) | Includes the `MeshResource` subscript family; the indexer surfaces but the AnyType return is replaced by `object`-typed fallback or skipped. |
| `Swift.Runtime` trimmer descriptor on transitive NativeAOT | `roadmap.md` P5 | An app that transitively consumes `SwiftBindings.Runtime` via `SwiftBindings.Apple.RealityKit` and publishes with `PublishAot=true` does not yet receive the ILC `--descriptor` injection; consumers must add `<TrimmerRootDescriptor>` and an `<IlcArg>` to their app csproj manually. P5 fix direction is to ship a `buildTransitive` targets file in `SwiftBindings.Runtime`. Worth calling out in the package README until P5 lands. |
| RealityKit-specific wrapper-emission gaps (P2) | `roadmap.md` P2 | Three issues surfaced during the ARRaycastQueryTarget Codex review (Codex session `019de6ef-c41c-7fb3-a708-cda5cde59cf1`); not blocking shipping but tracked as upstream-bug-style gaps. |
| Roadmap-tracked items (P3, P4) | `roadmap.md` P3 / P4 | Existential resolver USR-vs-printedName reconciliation (P3) and `c:@E@…` enum TypeRecord synthesis (P4) — currently masked by the apple-frameworks.json data fix in `c614ae45`; not visible to consumers but logged for future generalization. |

A consolidated, consumer-facing list lives at the wiki [Known Limitations page](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations); RealityKit-specific items added to that page should mirror the rows above.

### What downstream owns (this contract does NOT cover)

- The actual NuGet publish (push to nuget.org with `SwiftBindings.*` API key — memory note `Public Documentation`).
- README / changelog wording in the downstream package directory.
- Deciding whether to add a runtime smoke app for RealityKit (currently none, deliberately).
- Wiki page edits.

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
