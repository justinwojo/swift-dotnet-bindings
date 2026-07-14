# M0-A — Generator pipeline map

**Wave:** 0 (map)  
**Agent:** M0-A  
**Scope:** `src/Swift.Bindings/src/`  
**Mode:** Architecture-level (not line-complete)  
**Date:** 2026-07-15  

---

## 1. End-to-end pipeline stages (normal generate)

A normal `dotnet run --project src/Swift.Bindings/src -- --xcframework … -o …` run follows this ordered pipeline. Stages marked **optional** run only when inputs/flags require them.

```text
CLI (Program.Main → CliOptions → BindingsGeneratorCommand.Execute)
  │
  ├─ [early out] specialty modes (see §2) — never reach core generate
  │
  ├─ 1. Platform / input validation
  │     PlatformInfoFactory, mutual-exclusivity, --strict-inputs arming
  │
  ├─ 2. Input resolution
  │     XCFrameworkResolver.Resolve  OR  manual -a/-d/-t
  │     SupportedToolchain.AssertSupported
  │     BinaryDependencyAnalyzer (auto deps) + --framework-dependency merge
  │     ResolveSymbolGraphPath (optional docs)
  │
  ├─ 3. Mixed ObjC pre-parse (optional, xcframework + mixed surface)
  │     ObjCPipeline.Parse → ObjCBridgeRecordFactory.CreateRecords
  │     Fail-closed on systemic ObjC parse failure (no Swift-only degrade)
  │
  ├─ 4. GenerateBindings (core)
  │     4a TypeDatabase bootstrap (built-in *Database.xml, --module-database, dep ABI)
  │     4b TBD demangle (DemanglingResults.FromTbd)
  │     4c Interface facts (SwiftSyntaxInterfaceFactsProducer via InterfaceFactsAggregator)
  │     4d Symbol-graph doc parse (optional)
  │     4e SwiftABIParser.ParseModule → ModuleDecl + TypeDecls + ParseReconciliation
  │     4f Collision / underscore / UnderscoreProtocolSynthesizer
  │     4g ReportCollector.Start
  │     4h ModuleProcessor → ModuleTypeDatabase (TypeRecords, layouts, library names)
  │     4i ObjC bridge record rekey + Register (KeepExisting = Swift-wins)
  │     4j typeDatabase.AddModuleDatabase + Freeze
  │     4k ModuleEmissionContext + MarshalingContext + SpecializationEngine
  │     4l ProtocolExtensionEmitter + phantom defaults + ForeignTypeExtensionEmitter
  │     4m StringEmitter.EmitModule (marshal + emit; see §1.1)
  │     4n Reports: binding-artifact-manifest, emission report, degradation diagnostics
  │     4o TrimmerDescriptorEmitter, ModuleDatabaseEmitter, SwiftTypeOwnershipManifest
  │     4p WrapperSymbolIntegrityGate (fail-closed on dangling P/Invoke symbols)
  │
  ├─ 5. --strict-inputs gate (InputResolutionReport degradations → SWIFTBIND027)
  │
  ├─ 6. Wrapper compilation (optional; skipped under --skip-wrapper-compilation)
  │     TryDecideWrapperArchitectures → CompileWrapperForArchitectures
  │     SwiftWrapperCompiler (+ NativeThunkCompiler, SwiftWrapperPostProcessor)
  │     StrippedSymbolCSharpReconciler (co-gate C# to stripped wrapper symbols)
  │
  ├─ 7. Mixed ObjC FilterAndEmit (optional)
  │     Uses swift-types.json exclude set; companion csproj
  │
  └─ 8. Project / packaging artifacts (xcframework or Apple direct)
        BindingProjectEmitter (non-SDK), ConsumerTargetsEmitter,
        XCFrameworkMetadataExtractor, DependencyManifestEmitter
```

### 1.1 EmitModule interior (stage 4m)

`StringEmitter.EmitModule` → `ModuleHandler.Marshal` / `Emit`:

| Step | What |
|------|------|
| Pre-passes | Nested-type renames, silent tombstones, interface property-name cache, error-enum registry |
| TypeSkipPrePass | Predicts handler-skipped types into `ReportCollector` before members emit |
| EveryProtocol | Swift-side vtable structs + conformances (`EveryProtocolEmitter`) |
| SuppressedProxyPrecomputer | Front-loads suppressed proxy names for emit-time gates |
| Topo type walk | `BaseHandler.HandleBaseDecl` via `Conductor` factories (Class/Struct/Enum/Protocol/…) |
| Per-member admission | `MemberValidationPipeline.ValidateMethodEmission` then handler `Marshal` → `Emit` |
| Per-member wrappers | `WrapperEmitter` / `PropertyWrapperEmitter` / … + `ValidateMethodWrapperEligibility` |
| Projections | `TypeProjectionFactory` + visitors; `MethodMarshalPlanBuilder` |
| Reverse dispatch | `ProtocolProxyEmitter` (vtables, receivers, StaticInit), `WitnessDispatchEmitter` |
| Closures / async | `ClosureEmitter*`, async bridges, CSM (`ConcreteProtocolSpecializationEmitter`) |
| Post-write | Namespace collision qualify, `AbiContractChecker`, file-per-type split, API manifest |
| Swift side artifacts | `{ns}.Wrapper.swift`, thunk `.s` files, SwiftUI/Theme bridge sources |

Handlers are selected by `Conductor` + `HandlerFactory` implementations (`ClassHandler`, `FrozenStructHandler`, `NonFrozenStructHandler`, `EnumHandler`, `ProtocolHandler`, `MethodHandler`, `PropertyHandler`, `SubscriptHandler`, `OperatorHandler`, …).

---

## 2. CLI modes and stage coverage

Entry: `Program.Main` → `CliOptions.CreateRootCommand` → `BindingsGeneratorCommand.Execute`.

| Mode / flag cluster | Stages hit | Notes |
|---------------------|------------|--------|
| **Full generate (xcframework)** | 1–8 | Default product path; SDK uses this with `--sdk-mode --skip-wrapper-compilation` on pass 1 |
| **Full generate (manual -a/-d/-t)** | 1, 2 (manual), 4, 5, optional 6/8 for system frameworks | No xcframework resolver; Apple system frameworks get direct wrapper + csproj |
| **`--compile-wrapper-only`** | Resolve xcframework + `RunCompileWrapperOnly` | Skips parse/generate; compiles existing `.Wrapper.swift` + updates metadata |
| **`--compile-bridge-only`** | Resolve + `RunCompileBridgeOnly` | Compiles `.SwiftUIBridge.swift` → `{Module}Bridge.xcframework` |
| **`--objc` (forced pure ObjC)** | XCFramework ObjC resolve → `ObjCPipeline.Run` | No Swift stages |
| **ObjC auto-fallback** | On `SwiftModuleNotFound` / `StaticLibrary` | Same pure-ObjC path |
| **Mixed ObjC+Swift** | 3 (Parse) + 4 + 7 (FilterAndEmit) | Clang once; bridge records into TypeDB; companion after Swift |
| **`--skip-wrapper-compilation`** | Full generate without stage 6 | SDK two-pass: later `--compile-wrapper-only` |
| **`--skip-thunk-compilation`** | Wrapper compile without native `.s` link | Thunk symbols missing from wrapper binary |
| **`--detect-apple-cross-module-deps`** | `AppleFrameworkImportDetector` only | stdout `MODULE\|PACKAGE\|RANGE` for SDK PackageReference injection |
| **`--slice-xcframework`** | `XCFrameworkSlicer` only | Per-RID pack staging |
| **`--resolve-auto-deps`** | `AutoDepResolver` only | stdout `PROJREF\|` / `WARN\|` for SDK auto ProjectReference |
| **`--emit-apple-types-manifest`** | `AppleTypesManifestCommand` | Apple supplement metadata from ABI dumps |
| **`--emit-apple-types-cs`** | `AppleTypesCsCommand` | C# from manifest + sequential-layout whitelist |
| **`--validate-apple-types-manifest`** | `AppleTypesManifestValidateCommand` | Live dlsym/VWT drift check / write-back |
| **`--regen-stdlib-conformances`** | `StdlibConformancesRegenCommand` | Prune embedded stdlib conformance fact table |
| **`--sdk-mode`** | Full generate; skips standalone `.csproj` emission | SDK *is* the project system |
| **`--strict-inputs`** | Escalates input degradations after resolve/generate | Finding 50 / CI compile gate |

Apple “direct” (system framework via `-a/-d/-t` + system `-l`) is not a separate flag — it is detected by `IsSystemFrameworkTarget` and then runs wrapper compile + project emit without a source xcframework.

---

## 3. Key types / contexts threaded through the pipeline

| Type | Lifetime | Role |
|------|----------|------|
| **`TypeDatabase` / `ITypeDatabase`** | Whole generate | Swift→C# type registry, built-in XML stubs, dep modules; **frozen** after bound module finalize; only `ApplyEmissionResult` mutates post-freeze |
| **`ModuleTypeDatabase` / `TypeRecord`** | Per module | Per-type marshalling kind, C# name, library path, symbols, conformances |
| **`ModuleDecl` (+ nested decls)** | Parse → emit | Canonical AST: types, methods, protocols, extensions |
| **`SwiftInterfaceFacts`** | Parse + inject | Protocol names, extension methods, foreign extensions, availability, marker conformances (from SwiftSyntax host) |
| **`ModuleEmissionContext`** | One per bound module | Dedup sets, suppressed proxies, thunk builders, specialization engine, marshaling, runtime-contract epoch, file-split spans, emission-symbol side table |
| **`MarshalingContext`** | One per module (on emission context) | Shared configured handlers: BoundGenerics, Closure, Tuple, TypeConversion, Existential, AsyncStream |
| **`MethodEnvironment` / `PropertyEnvironment` / `ModuleEnvironment`** | Per member/type | Decl + TypeDB + emission symbol (`PromoteSymbol`), projected names, sibling property set |
| **`TypeHandlerContext`** | Nested during type emit | P/Invoke helper, renames, composition collector, emission context pointer |
| **`Conductor`** | One per `StringEmitter` | Handler factories + composition interface collector |
| **`MemberValidationPipeline`** | Per walk / type | Pre-marshal emit admission + post-marshal wrapper eligibility |
| **`ValidationContext` / `ValidationResult` / `SkipReason`** | Per member check | Admission outcome + report attribution |
| **`ConcreteSpecializationEngine`** | Per module | CSM conformer discovery / specialization routing |
| **`ProtocolExtensionDefaultsIndex`** | Per module (if protocols) | Extension defaults + phantom defaults for C# default interface methods |
| **`VtableLayout` / `VtableLayoutBuilder`** | Per protocol (reverse dispatch) | Single source of slot membership/index/width |
| **`NamespacePatternResolver`** | Whole generate | `{Module}` / `{Framework}` → C# namespace |
| **`PlatformInfo` / `ApplePlatform`** | Whole generate | TFM, slice ids, package-id defaults, arch basis |
| **`FrameworkDependencyInfo`** | Resolve → emit | Cross-module deps (ABI, search paths, auto vs explicit) |
| **`InputResolutionReport`** | Ambient per run | Slice/arch/artifact/dependency degradations for `--strict-inputs` |
| **`ReportCollector` / `BindingReport`** | Ambient during emit | Skip/tombstone inventory → manifest / binding-report |
| **`BindingArtifactManifest`** | Written post-emit; RMW for wrapper/ObjC | Generation + emission + inputResolution + wrapper + ObjC sections |
| **`XCFrameworkResolution`** | Resolve → wrapper/project | Paths to abi.json, dylib, TBD, swiftinterface, slice metadata |
| **`ObjCParseResult` / `ObjCModule`** | Mixed / pure ObjC | Clang AST model; bridge records; companion emission |

**Threading rules worth remembering for deep tracks:**

- `context.GetEmissionContext()` must be passed wherever `WrapperEmitter` is created (dedup / symbol side table).
- `MarshalingContext.EmissionContext` is the single arming point for suppressed-proxy oracles (not only per-method env).
- Emission-promoted symbols live on `MethodEnvironment.EmissionSymbol` / module side table — never mutate `MethodDecl.MangledName`.

---

## 4. Subsystem complexity heatmap

### 4.1 Largest files (approx. LOC, end-of-file line numbers)

| LOC (approx) | Path | Subsystem |
|-------------:|------|-----------|
| ~7430 | `Emitter/StringEmitter/EveryProtocolEmitter.cs` | Reverse dispatch / EveryProtocol |
| ~4225 | `Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | SwiftUI bridge |
| ~4220 | `Parser/SwiftABIParser.cs` | ABI JSON parse |
| ~3395 | `Demangler/Swift5Demangler.cs` | Demangler |
| ~2990 | `Configuration/SwiftWrapperCompiler.cs` | Wrapper compile |
| ~2740 | `Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | Protocol reverse receivers |
| ~2580 | `Emitter/StringEmitter/WitnessDispatchEmitter.cs` | Witness property/method dispatch |
| ~2520 | `Program.cs` | Orchestration utilities + generate core |
| ~2250 | `Emitter/StringEmitter/WrapperValidation.cs` | Wrapper admission |
| ~2135 | `BindingsGeneratorCommand.cs` | CLI orchestration |
| ~2010 | `Emitter/StringEmitter/Handler/MethodHandler.cs` | Method emit |
| ~2000 | `Marshaler/NameProvider.cs` | Naming / projected keys |
| ~1950 | `Emitter/StringEmitter/ModuleEmissionContext.cs` | Emission state |
| ~1880 | `Marshaler/BoundGenericsHandler.cs` | Generics / CSM constraints |
| ~1730 | `Emitter/StringEmitter/Handler/PropertyHandler.cs` | Properties |
| ~1700 | `Emitter/StringEmitter/Handler/ProtocolHandler.cs` | Protocol interfaces |
| ~1600 | `Emitter/StringEmitter/Handler/WrapperEmitter.cs` | `@_cdecl` wrapper bodies |
| ~1550 | `Configuration/XCFrameworkResolver.cs` | Slice / artifact resolve |
| ~1470 | `Emitter/StringEmitter/ClosureEmitter.cs` | Closures (partial; more in siblings) |
| ~1340 | `Marshaler/IHandler.cs` | Base admission / type walk |
| ~1010 | `Emitter/StringEmitter/Handler/EnumHandler.cs` | Enums (+ partials) |

**ClosureEmitter** is split across many partials (`Async`, `Throwing`, `SwiftWrapper`, `InvokeThunk`, …) — aggregate complexity is higher than the primary file alone.

### 4.2 Intertwining (who depends on whom)

```text
                    ┌──────────────────┐
                    │ TypeDatabase +   │
                    │ AppleFramework   │
                    │ Registry + XML   │
                    └────────┬─────────┘
           ┌─────────────────┼─────────────────┐
           ▼                 ▼                 ▼
    ┌────────────┐   ┌──────────────┐   ┌────────────────┐
    │ Parser /   │   │ Marshaler /  │   │ Emitter        │
    │ Demangler  │──▶│ Projection   │──▶│ Handlers +     │
    │ ModuleProc │   │ Environments │   │ Wrappers +     │
    └────────────┘   └──────┬───────┘   │ Proxy/EveryP   │
                            │           └───────┬────────┘
                            │                   │
                            ▼                   ▼
                     MemberValidation    Reporting
                     Pipeline gates      ReportCollector
                                         BindingArtifactManifest
                            │
                            ▼
                     Configuration
                     SwiftWrapperCompiler
                     XCFrameworkResolver
                     NativeThunkCompiler
```

**Highest intertwine zones (for deep-audit prioritization):**

1. **Protocol reverse dispatch** — `EveryProtocolEmitter` ↔ `VtableLayout` ↔ `ProtocolProxyEmitter.*` ↔ `WitnessDispatchEmitter` ↔ `ProtocolVtableMembers` (must stay slot-aligned).
2. **Admission stack** — `MemberValidationPipeline` + `MemberGateEvaluator` + `MemberEmissionValidator` + `WrapperValidation` + `MethodValidationGates` + handler-local existential checks.
3. **Projected naming** — `NameProvider.GetPublicMethodName` / `PublicMethodNameContext.ForMethod` ↔ `ProtocolSignatureHelper.BuildProjectedMethodKey` ↔ dedup loops in `IHandler` / `ProtocolHandler` / default-parameter overloads.
4. **Type projection** — `TypeProjectionFactory` must agree with `MarshallingHelpers.IsOptionalObjCBridged`, property vs method conversion paths, and all `IProjectionVisitor` receivers.
5. **Wrapper compile vs C# plan** — emission-time `WrapperSymbolContractGate` + post-compile `StrippedSymbolCSharpReconciler` + `WrapperSymbolIntegrityGate`.
6. **Mixed ObjC** — ObjC parse records ↔ Swift TypeDB ↔ `swift-types.json` exclude ↔ companion packaging ↔ `IsMixedFramework` metadata.

---

## 5. Emit-vs-skip admission points (G1 graceful-degradation track)

Admission is multi-layer. Prefer **emission-time skip with `SkipReason`** over emit-then-fail. Flag these for **track G1**.

### 5.1 Type-level

| Gate | Location | Effect |
|------|----------|--------|
| `TypeSkipPrePass` | `ModuleHandler` pre-emit | Seeds skipped types so members referencing them skip cleanly |
| Underscore / module-internal / `@_spi` | `IHandler.HandleBaseDecl` | Type omitted from C# |
| SwiftUI View collection | `SwiftUIViewDetector` + handlers | Type skipped as View; collected for bridge |
| Owned by Apple supplement | Handler checks | Type skipped; reference Apple package |
| Missing type handler | `HandleBaseDecl` | `SkipReason.MissingHandler` |
| Generic unsupported constraint | `GenericTypeEmitter` / pre-pass mirror | SwiftUI/Combine/… constraint skips |
| ObjC pipeline filters | `ObjCPipeline.Filter*` | ObjCSkipReason diagnostics |

### 5.2 Member-level (pre-marshal) — `MemberValidationPipeline.ValidateMethodEmission`

Ordered gates (see file header comments for full list):

1. **Suppression:** `@_spi`, module-internal free funcs, implicit+overriding ctor, synthesized protocol methods  
2. **Internal-type reach (Pattern 2)** — signature names `@usableFromInline internal` type  
3. **Parent module-internal no-fallback** — async/closure on internal parent (`ParentModuleInternalNoFallback`)  
4. **Closure + module gates** via `ShouldSkipMethodEmission` / `MemberGateEvaluator` (unsupported closures, SwiftUI/Combine refs, async tuple edges)  
5. **Generic type callback** — UnmanagedCallersOnly / async generic parent (with CSM / bridge eligibility escapes)  
6. **Protocol constraint / bound-generic / unsatisfied constraint**  
7. **Generic constructor own type params**  

Also in `IHandler.HandleBaseDecl` after pipeline:

- Primary + projected signature **dedup** (`DuplicateSignature`)  
- Empty-tuple constructor collision  
- Override collision-suffix pre-reserve  
- Closure-param **tombstone** route (`ClosureParamTombstoneEmitter`) instead of hard skip  
- CSM / closed-constrained / method-closure-bridge **RoutedElsewhere** (not recorded as skip)

### 5.3 Wrapper-level (post-marshal / eligibility)

| Gate | Location | Effect |
|------|----------|--------|
| `ValidateMethodWrapperEligibility` | `MemberValidationPipeline` | Decides `@_cdecl` wrapper vs direct CallConvSwift |
| `WrapperValidation` | Large eligibility matrix | Arms 2b etc. for internal-parent sync fallback |
| `WrapperEligibility` evaluators | Property/Subscript/Method wrapper emitters | Per-shape reject reasons |
| `ConstructorAdmissibility` | Constructor path | Ctor-specific emit rules |
| `ClosedStaticFactoryGate` | Factories | Closed static factory eligibility |
| `TypeMetadataAccessorSkipGate` | Metadata accessors | Skip unsafe metadata helpers |
| `WrapperSymbolContractGate` | Emit-time | Predict-then-skip if wrapper symbol would violate contract |
| `ProtocolConformanceValidator` | EveryProtocol | Conformance emit vs skip decision |
| Protocol proxy secondary gates | `ProtocolProxyEmitter` | Projected C# type checks for receivers/impls |

### 5.4 Post-emit integrity / co-gating (not “usability skip”)

| Gate | When | Fail mode |
|------|------|-----------|
| `WrapperSymbolIntegrityGate` | End of GenerateBindings | **Fail-closed** dangling EntryPoint refs |
| `StrippedSymbolCSharpReconciler` | After wrapper compile strip | Co-gate C# members to stripped symbols |
| `AbiContractChecker` | After C# string built | ABI/DllImport contract validation |
| `EmitStrictInputsFailureIfDegraded` | After generate | **Fail-closed** under `--strict-inputs` |
| Mixed ObjC abort | Parse/FilterAndEmit failure | **Fail-closed** (no silent Swift-only) |

### 5.5 G1 notes

- Historical generate-then-strip for suppressed proxies and wrapper contracts is largely **retired** in favor of emission-time gates; wrapper post-processor remains a safety net for body-reference shapes.
- **Tombstones / UnsupportedSwiftType / SB000x** can still leave public-looking surface that fails at call sites — G1 should inventory “poison API” vs omit.
- **AnyType fallback → secondary prune** is a common degrade path when deps fail to load; noisy under non-strict runs.
- **PropertyHandler** still holds some accessor-level constraint checks outside the unified pipeline (documented in constraints).

---

## 6. Dual-path / dual-oracle smells

Places where **two implementations must agree**. Consolidation candidates for L4; correctness risk for L1.

| Dual path | Files / symbols | Risk if they drift |
|-----------|-----------------|--------------------|
| **Projected C# method key (one core, multiple shims)** | `ProtocolSignatureHelper.BuildProjectedMethodKey`; shims in `IHandler`, `DefaultParameterOverloadEmitter`, protocol path | CS0111 / silent member drop |
| **Projected key vs emitted-signature dedup (protocol)** | `ProtocolHandler` projected key + `BuildEmittedSignature` must both append async `CancellationToken` | Re-collapse async/sync overloads |
| **Projected key vs reverse-dispatch slot key** | Projected key domain vs `EveryProtocolEmitter.GetMethodKey` / `VtableLayout` | Vtable field shift → SIGSEGV (esp. NativeAOT) |
| **Vtable layout SSOT vs fillability walks** | `VtableLayoutBuilder` vs receivers / `StaticInit` fill filters | Slot index corruption |
| **Still hand-allocating layout axis** | `EnumerateProtocolMethodsForDispatch`, `EnumerateIndexedSubscripts` | Must stay byte-identical to `VtableLayout` |
| **Closure two-layer gate** | `IsSupportedClosureParameterType` (emit?) vs `IsCdeclCompatibleType` (wrapper?) | Emit without wrapper / wrapper without call site |
| **`IsOptionalObjCBridged` vs TypeProjectionFactory** | `MarshallingHelpers` + projection factory four-clause heuristic | Wrong optional ObjC projection |
| **`IsObjCModuleType` / Apple heuristics** | `TypeDatabaseExtensions` delegates to `AppleFrameworkRegistry` | Keep registry as sole oracle |
| **SwiftUI two-path suppression** | TypeDB path A + `MemberEmissionValidator` path B | View types leak or over-suppress |
| **TypeSkipPrePass vs handler skip predicates** | `TypeSkipPrePass` mirrors `GenericTypeEmitter` / PWT shape | CS0234 dangling refs |
| **Property vs method conversion / visitors** | Accessor visitors vs `Receiver*Visitor` family — exhaustive on `ITypeProjection` | Missing arm = build error (good); incomplete plans = runtime bugs |
| **WitnessDispatch string branch** | String checked as frozen+RefFields before pure blittable | Wrong dispatch strategy |
| **Wrapper CPU-arch decision** | `TryDecideWrapperArchitectures` / `CompileWrapperForArchitectures` used by full generate **and** `--compile-wrapper-only` | Dropped arch / false “no wrapper” metadata |
| **Consumer NativeReference “will be produced”** | `wouldCompileWrapper \|\| hasWrapperXcfw` in `ConsumerTargetsEmitter` options | `DllNotFoundException` under SDK two-pass |
| **Runtime contract epoch vs PackageReference version** | `ModuleEmissionContext.RuntimeContractEpoch` + `BindingProjectEmitter` / `RuntimeVersionRange` | Load-time hard abort vs silent mismatch |
| **IsMixedFramework vs FilterAndEmit companion gate** | Enums must count on both sides | CS0234 missing companion enum |
| **`ISwiftObject` seed-drop vs PWT resolvability** | `GenericTypeEmitter.GetWhereClause` mirrors `PInvokeHelperEmitter` resolvable set | CS0314 |
| **Override pre-pass vs main-loop projected keys** | `ClassifyOverridePrePassEmission` + tombstone view | CS0111 on reverse-order siblings |
| **ObjC pure vs mixed parse/emit split** | `ObjCPipeline.Run` vs `Parse` + `FilterAndEmit` | Eligibility filters must match bridge records |
| **Direct-mode vs xcframework wrapper compile** | Two call sites for `CompileWrapperForArchitectures` + arch basis | Fat/sim arch holes |
| **EmitBoundGenericArguments vs EmitTypeConversions** | Both can create `{name}Buffer` | Double buffer / compile errors (constraints) |

Many of these are already partially consolidated (AF05 projected-key core, VtableLayout SSOT, MarshalingContext shared handlers). Remaining dual oracles are the highest L4 ROI.

---

## 7. Dependencies on Runtime / SDK / external tools

### 7.1 Swift.Runtime (NuGet `SwiftBindings.Runtime`)

Generated C# always references `Swift.Runtime` / `Swift.Runtime.InteropServices`:

- Ownership: `ISwiftObject`, `SwiftClassHandle`, `SwiftHandle`, ARC helpers  
- Containers: `SwiftArray`, `SwiftDictionary`, `SwiftSet`, `SwiftOptional`, `SwiftString`, `Utf8Slice`  
- Existentials / protocols: `ExistentialContainer`, `ProtocolWitnessTable`, `EveryProtocol` (runtime side), registries  
- Async/closures: `SwiftClosure*`, async helpers, `CancellationToken` bridges  
- **Load-time contract:** `RuntimeContract.AssertCompatible(epoch)` — epoch derived from package minor  
- Built-in **TypeDatabase XML** stubs live under Runtime (`SwiftDatabase.xml`, `UIKitDatabase.xml`, …) and are loaded by the generator from `AppDomain.BaseDirectory/Swift/`  
- Trimmer descriptor pairing: generator emits per-module ILLink roots; Runtime ships open-generic roots  

### 7.2 SDK (`Swift.Bindings.Sdk`)

Not in this map’s file tree, but the generator is designed for it:

- `--sdk-mode`, `--skip-wrapper-compilation`, later `--compile-wrapper-only` / `--compile-bridge-only`  
- `--assembly-name` for trimmer descriptor in app assembly  
- `--detect-apple-cross-module-deps`, `--resolve-auto-deps`, `--slice-xcframework`  
- Consumes `binding-metadata.props`, consumer `.targets`, Apple-supplement fragments  
- Force-load static archive sole-carrier policy + mixed companion targets  

### 7.3 External / host tools (macOS)

| Tool | Used by |
|------|---------|
| **swiftc** / Apple SDK via `xcrun` | `SwiftWrapperCompiler` |
| **clang** | ObjC AST dump (`ClangAstInvoker`); native thunk compile/link |
| **lipo** | Fat wrapper arch merge |
| **otool** | `BinaryDependencyAnalyzer` dependency scan |
| **nm** / symbol probes | `NativeSymbolProbe`, ObjC eligibility, integrity |
| **ditto** | `XCFrameworkSlicer` (preserve xattrs/signatures) |
| **SwiftSyntax host producer** | Interface facts (macOS-only; hard-fail if missing when interface present) |
| **swift-api-digester dumps** | Indirect: ABI JSON inputs; stdlib conformances regen |
| **dlsym / CallConvSwift** | Apple types manifest validator |

### 7.4 Data files (embedded)

- `Data/apple-frameworks.json` (+ schema) — `AppleFrameworkRegistry`  
- `Data/objc-type-mappings.json`  
- `Data/specialization-hints.json` — CSM  
- `Data/stdlib-conformances.json`  

### 7.5 ObjC subsystem (in-tree)

`ObjC/Parser` (clang AST) → `ObjC/Model` → `ObjC/Emitter` (ApiDefinitions-style + StructsAndEnums + project) → `ObjC/Pipeline`. Parallel track with Swift, joined only at mixed bridge and packaging.

---

## 8. Subsystem inventory (scope directories)

| Directory | Role |
|-----------|------|
| Root (`Program`, `BindingsGeneratorCommand`, `CliOptions`) | Entry + orchestration |
| `Parser/` | ABI JSON → decls; interface facts producers; ModuleProcessor |
| `Demangler/` | Swift 5 demangle + TBD parse |
| `TypeDatabase/` | Type records, Apple registry, conformance graph, resolvers |
| `Marshaler/` | Environments, handlers for types/args, projections, names |
| `Emitter/` | C#/Swift string emission, project files, thunks, reports hooks |
| `Configuration/` | XCFramework, wrapper compile, packaging policy, probes |
| `Model/` | Decls, TypeSpecs, availability, docs |
| `ObjC/` | Full secondary pipeline |
| `Reporting/` | Binding/emission reports, skip triage |
| `AppleTypesManifest/` | Apple supplement generate/validate |
| `StdlibConformances/` | Fact-table regen CLI |
| `Data/` | JSON oracles |

---

## Wave 1+ track splits recommended

- **A1 (P/Invoke / CallConv / sret / thunks):** `WrapperEmitter*`, `PInvokeEmitter`, `NativeThunkEmitter` / `TypeLowering`, `Cdecl*` helpers, `MethodHandler` call sites; sample BindingTests wrapper↔P/Invoke pairs.  
- **A2 (layout / frozen / optional EI):** `ModuleProcessor`, `SwiftValueLayout`, frozen vs non-frozen struct handlers, `OptionalMarshalStrategy` / `OptionalReferenceClassifier`.  
- **A3 (ARC / SafeHandle / async lifetime):** Runtime-facing emit of handles + `Async*` emitters; pair with M0-B runtime map.  
- **A5 / A5b / A5c (protocols / reverse dispatch):** Split mega-file work: (1) `EveryProtocolEmitter` + `VtableLayout`, (2) `ProtocolProxyEmitter.Receivers` + `StaticInit`, (3) projected-key vs slot-key axis + `WitnessDispatchEmitter`.  
- **A6 / M3 (generics / TypeDB / Apple):** `BoundGenericsHandler`, CSM emitters, `TypeDatabase` freeze model, `AppleFrameworkRegistry` + projection parity.  
- **A4 (closures):** `ClosureHandler` + `ClosureEmitter*` partials + two-layer eligibility.  
- **A7 (async / throws):** Async bridges, error registry, real-async vs legacy blocking receivers (known incomplete CT edge).  
- **A8 (parser / demangler):** `SwiftABIParser` + `Swift5Demangler` + interface facts; ParseReconciliation accounting.  
- **G1 (graceful degradation, Wave 7+ threaded):** Inventory all §5 gates; classify fail-closed integrity vs skip-and-continue; tombstone/poison-API surface; dual fail-open/fail-closed siblings in §6.  
- **ObjC track (packaging / mixed):** `ObjCPipeline` eligibility filters + mixed bridge rekey + `IsMixedFramework` lockstep + TN2435 packaging (with M0-C).  
- **SDK two-pass / wrapper arch:** `CompileWrapperForArchitectures` + consumer “will be produced” NativeReference + metadata props (with M0-C).  
- **SwiftUI bridge (optional deep):** `SwiftUIBridgeEmitter*` + collector + view detector — large but product-scoped.  
- **Simplification (L4):** Prefer consolidating remaining dual oracles in §6 over new features; prioritize vtable hand-allocators and TypeSkipPrePass mirror list.

---

## 5-bullet executive summary

1. Normal generate is **resolve → (optional ObjC parse) → TypeDB+parse+freeze → emission context → EmitModule → reports/integrity → (optional wrapper compile) → (optional ObjC companion) → project targets**.  
2. CLI has many **out-of-tree modes** (slice, auto-deps, Apple types, compile-wrapper-only) that never touch the marshal/emit core.  
3. Complexity concentrates in **EveryProtocol / ProtocolProxy receivers / SwiftABIParser / SwiftUI bridge / WrapperValidation / SwiftWrapperCompiler**.  
4. Admission is already multi-layer (`MemberValidationPipeline` + wrapper eligibility + integrity gates); G1 should map **skip vs fail-closed** and remaining emit-then-strip/co-gate paths.  
5. Highest dual-oracle risk remains **vtable slot vs projected key**, **TypeSkipPrePass vs handlers**, **wrapper-arch/metadata dual call sites**, and **ObjC mixed classification lockstep**.
