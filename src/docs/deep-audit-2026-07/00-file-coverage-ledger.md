# File Coverage Ledger — Deep Audit 2026-07

**Wave**: 0 (seed)  
**Agent**: M0-F  
**Status**: All in-scope files seeded as `inventory` — nothing `reviewed-deep` yet.  
**Scope**: Source surfaces only; excludes `bin/`, `obj/`, `.build/`, and generated `BindingTests/output/`.  
**LOC method**: physical line count (bytes split on `\n`; final non-terminated line counted).  

---

## SUMMARY

| Metric | Value |
|--------|------:|
| **Total files** | **1799** |
| **Total LOC** | **704,915** |
| Status for all rows | `inventory` |
| Deep-reviewed | 0 |

### Per-area rollup

| Area | Files | LOC |
|------|------:|----:|
| src/Swift.Bindings/src (generator source, *.cs) | 435 | 220,677 |
| src/Swift.Bindings/tests (unit tests, *.cs) | 394 | 305,288 |
| src/Swift.Runtime/src (runtime library, *.cs) | 100 | 20,709 |
| src/Swift.Runtime/tests (runtime tests, *.cs) | 44 | 9,735 |
| src/Swift.Runtime/swift (native runtime: *.swift, *.c, *.sh) | 3 | 1,341 |
| src/Swift.Bindings.Sdk (*.props, *.targets, *.cs, scripts) | 10 | 5,160 |
| src/Swift.Bindings.Apple (*.cs, *.swift, *.targets — not bin/obj) | 16 | 2,770 |
| src/Swift.Analyzers (*.cs) | 3 | 696 |
| src/Swift.Analyzers.Tests (*.cs) | 3 | 965 |
| src/SwiftBindings.TestDiscovery (*.cs) | 1 | 579 |
| src/Swift.Bindings.Templates (template content + project) | 2 | 62 |
| build/ (Nuke targets, scripts, Helpers, Models, Tools) | 65 | 26,234 |
| BindingTests/Sources (*.swift) | 341 | 36,770 |
| BindingTests/RuntimeTestsApp (*.cs only, not bin/obj) | 358 | 67,541 |
| tools/SwiftInterfaceParser/Sources (*.swift) | 18 | 6,074 |
| .claude/rules (*.md) | 6 | 314 |
| **TOTAL** | **1799** | **704,915** |

### Top 30 largest files (whole surface)

| Rank | LOC | Path | Area key |
|-----:|----:|------|----------|
| 1 | 10,715 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftUIBridgeEmitterTests.cs` | gen_tests |
| 2 | 7,432 | `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | gen_src |
| 3 | 7,332 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` | gen_tests |
| 4 | 5,271 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` | gen_tests |
| 5 | 5,107 | `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ApiDefinitionEmitterTests.cs` | gen_tests |
| 6 | 5,015 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolHandlerOutputTests.cs` | gen_tests |
| 7 | 5,000 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodWrapperEmitterTests.cs` | gen_tests |
| 8 | 4,394 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` | gen_tests |
| 9 | 4,381 | `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkTargetsBehaviorTests.cs` | gen_tests |
| 10 | 4,225 | `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | gen_src |
| 11 | 4,224 | `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | gen_src |
| 12 | 4,128 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs` | gen_tests |
| 13 | 4,068 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs` | gen_tests |
| 14 | 3,977 | `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SwiftWrapperCompilerTests.cs` | gen_tests |
| 15 | 3,952 | `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BoundGenericsHandlerTests.cs` | gen_tests |
| 16 | 3,854 | `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstParserTests.cs` | gen_tests |
| 17 | 3,800 | `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` | sdk |
| 18 | 3,617 | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` | gen_src |
| 19 | 3,462 | `build/Build.RuntimeTests.cs` | build |
| 20 | 3,398 | `src/Swift.Bindings/src/Demangler/Swift5Demangler.cs` | gen_src |
| 21 | 3,208 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/AbiSafetyTests.cs` | gen_tests |
| 22 | 3,167 | `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` | gen_tests |
| 23 | 3,058 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolConformanceValidatorTests.cs` | gen_tests |
| 24 | 2,991 | `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` | gen_src |
| 25 | 2,990 | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs` | gen_src |
| 26 | 2,952 | `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` | gen_src |
| 27 | 2,935 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyWrapperEmitterTests.cs` | gen_tests |
| 28 | 2,780 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/WitnessDispatchEmitterTests.cs` | gen_tests |
| 29 | 2,744 | `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | gen_src |
| 30 | 2,694 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureEmitterDirectTests.cs` | gen_tests |

### Suggested deep-review priority tiers

#### T0 — Mega / crash-class load-bearing (Wave 1+ first)

Files that are simultaneously large and on the ABI/marshalling/proxy/wrapper critical path. Deep review should be **branch-level**, not whole-file skims.

| LOC | Path | Why |
|----:|------|-----|
| 10,715 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftUIBridgeEmitterTests.cs` | TBD — SwiftUIBridgeEmitterTests |
| 7,432 | `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | Code emitter: EveryProtocolEmitter |
| 7,332 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` | TBD — ProtocolProxyEmitterTests |
| 5,271 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` | TBD — EveryProtocolEmitterTests |
| 5,107 | `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ApiDefinitionEmitterTests.cs` | Code emitter: ApiDefinitionEmitterTests |
| 5,015 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolHandlerOutputTests.cs` | TBD — ProtocolHandlerOutputTests |
| 5,000 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodWrapperEmitterTests.cs` | TBD — MethodWrapperEmitterTests |
| 4,394 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` | TBD — EnumHandlerOutputTests |
| 4,381 | `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkTargetsBehaviorTests.cs` | TBD — SdkTargetsBehaviorTests |
| 4,225 | `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | SwiftUI bridge emitter: SwiftUIBridgeEmitter |
| 4,224 | `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | ABI/interface parser: SwiftABIParser |
| 4,128 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs` | TBD — ConcreteSpecializationEngineTests |
| 4,068 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs` | TBD — ConstructorWrapperEmitterTests |
| 3,977 | `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SwiftWrapperCompilerTests.cs` | TBD — SwiftWrapperCompilerTests |
| 3,952 | `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BoundGenericsHandlerTests.cs` | TBD — BoundGenericsHandlerTests |
| 3,854 | `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstParserTests.cs` | ABI/interface parser: ClangAstParserTests |
| 3,800 | `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` | MSBuild SDK targets (generate/compile/pack) |
| 3,617 | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` | Emitter type/method handler: ConcreteProtocolSpecializationEmitter |
| 3,462 | `build/Build.RuntimeTests.cs` | Nuke target partial: RuntimeTests |
| 3,398 | `src/Swift.Bindings/src/Demangler/Swift5Demangler.cs` | Swift demangler: Swift5Demangler |
| 3,208 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/AbiSafetyTests.cs` | TBD — AbiSafetyTests |
| 3,167 | `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` | TBD — ClosureHandlerTests |
| 3,058 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolConformanceValidatorTests.cs` | TBD — ProtocolConformanceValidatorTests |
| 2,991 | `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` | Generator configuration/tooling: SwiftWrapperCompiler |
| 2,990 | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs` | Emitter type/method handler: ProtocolExtensionEmitter |
| 2,952 | `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` | Protocol proxy emitter part: ProtocolProxyEmitter.InterfaceImpl |
| 2,935 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyWrapperEmitterTests.cs` | TBD — PropertyWrapperEmitterTests |
| 2,780 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/WitnessDispatchEmitterTests.cs` | TBD — WitnessDispatchEmitterTests |
| 2,744 | `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | Protocol proxy emitter part: ProtocolProxyEmitter.Receivers |
| 2,694 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureEmitterDirectTests.cs` | TBD — ClosureEmitterDirectTests |
| 2,689 | `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/StrippedSymbolCSharpReconcilerTests.cs` | TBD — StrippedSymbolCSharpReconcilerTests |
| 2,651 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncSwiftWrapperTests.cs` | TBD — AsyncSwiftWrapperTests |
| 2,616 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleHandlerTests.cs` | TBD — ModuleHandlerTests |
| 2,599 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodClosureBridgeTests.cs` | TBD — MethodClosureBridgeTests |
| 2,585 | `src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs` | Code emitter: WitnessDispatchEmitter |
| 2,546 | `build/Build.Validation.cs` | Nuke target partial: Validation |
| 2,542 | `src/Swift.Bindings/tests/UnitTests/EmitterTests/NativeThunkEmitterTests.cs` | TBD — NativeThunkEmitterTests |
| 2,526 | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` | Emitter type/method handler: ModuleHandler |
| 2,523 | `src/Swift.Bindings/src/Program.cs` | Generator CLI entry point |
| 2,137 | `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` | Main generator CLI orchestration command |

#### T1 — Load-bearing (emitters, marshaler, runtime ownership, gates, SDK)

Everything under generator Emitter/Marshaler/Parser/TypeDatabase, runtime `Swift/Runtime/`, Nuke pack/binding-tests/validate targets, and `Sdk.targets`/`Sdk.props` that is **not** already T0. Also `.claude/rules/constraints.md` (trap list verification in Wave 10).

| Bucket | Paths |
|--------|-------|
| Generator core | `src/Swift.Bindings/src/{Emitter,Marshaler,Parser,TypeDatabase,Configuration,ObjC,Reporting}/**` |
| Runtime ownership | `src/Swift.Runtime/src/Swift/Runtime/**`, `src/Swift.Runtime/swift/**` |
| Build gates | `build/Build.{BindingTests,Validation,Pack,PackGate,ReleaseGates}*.cs` |
| SDK packaging | `src/Swift.Bindings.Sdk/Sdk/**` |
| Trap rules | `.claude/rules/*.md` |

#### T2 — Rest of inventory

Unit tests, BindingTests fixtures/runtime tests, demangler corpus, Apple supplement shims, templates, analyzers, SwiftInterfaceParser walkers, small helpers/models. Map coverage still required; deep review is sample-driven or finding-driven rather than exhaustive line reads.

---

## AREA LEDGERS

Every row: `status=inventory`. Update later waves to `mapped` / `sampled` / `reviewed-deep` / `n/a`.

## src/Swift.Bindings/src (generator source, *.cs)

**Files**: 435  
**LOC**: 220,677  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsCommand.cs` | 136 | inventory | Apple types manifest tooling: AppleTypesCsCommand |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsEmitter.cs` | 518 | inventory | Apple types manifest tooling: AppleTypesCsEmitter |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestBuilder.cs` | 434 | inventory | Apple types manifest tooling: AppleTypesManifestBuilder |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestCommand.cs` | 147 | inventory | Apple types manifest tooling: AppleTypesManifestCommand |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestModel.cs` | 139 | inventory | Apple types manifest tooling: AppleTypesManifestModel |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestSerializer.cs` | 31 | inventory | Apple types manifest tooling: AppleTypesManifestSerializer |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestValidateCommand.cs` | 119 | inventory | Apple types manifest tooling: AppleTypesManifestValidateCommand |
| `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestValidator.cs` | 348 | inventory | Apple types manifest tooling: AppleTypesManifestValidator |
| `src/Swift.Bindings/src/AppleTypesManifest/SequentialLayoutWhitelist.cs` | 45 | inventory | Apple types manifest tooling: SequentialLayoutWhitelist |
| `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` | 2,137 | inventory | Main generator CLI orchestration command |
| `src/Swift.Bindings/src/CliOptions.cs` | 463 | inventory | Generator CLI option definitions |
| `src/Swift.Bindings/src/Configuration/AppleFrameworkImportDetector.cs` | 223 | inventory | Generator configuration/tooling: AppleFrameworkImportDetector |
| `src/Swift.Bindings/src/Configuration/ApplePlatform.cs` | 13 | inventory | Generator configuration/tooling: ApplePlatform |
| `src/Swift.Bindings/src/Configuration/AutoDepResolver.cs` | 216 | inventory | Generator configuration/tooling: AutoDepResolver |
| `src/Swift.Bindings/src/Configuration/BinaryDependencyAnalyzer.cs` | 605 | inventory | Generator configuration/tooling: BinaryDependencyAnalyzer |
| `src/Swift.Bindings/src/Configuration/BindingArtifactManifest.cs` | 467 | inventory | Generator configuration/tooling: BindingArtifactManifest |
| `src/Swift.Bindings/src/Configuration/BindingArtifactManifestStore.cs` | 173 | inventory | Generator configuration/tooling: BindingArtifactManifestStore |
| `src/Swift.Bindings/src/Configuration/BindingGeneratorOptions.cs` | 17 | inventory | Generator configuration/tooling: BindingGeneratorOptions |
| `src/Swift.Bindings/src/Configuration/BridgeBuildOutcome.cs` | 75 | inventory | Generator configuration/tooling: BridgeBuildOutcome |
| `src/Swift.Bindings/src/Configuration/DepModuleCollisionDetector.cs` | 276 | inventory | Generator configuration/tooling: DepModuleCollisionDetector |
| `src/Swift.Bindings/src/Configuration/EmittedSwiftTrapLint.cs` | 134 | inventory | Generator configuration/tooling: EmittedSwiftTrapLint |
| `src/Swift.Bindings/src/Configuration/GeneratorTimeouts.cs` | 51 | inventory | Generator configuration/tooling: GeneratorTimeouts |
| `src/Swift.Bindings/src/Configuration/NamespacePatternResolver.cs` | 40 | inventory | Generator configuration/tooling: NamespacePatternResolver |
| `src/Swift.Bindings/src/Configuration/NativeLinkageProbe.cs` | 108 | inventory | Generator configuration/tooling: NativeLinkageProbe |
| `src/Swift.Bindings/src/Configuration/NativePackagingPolicy.cs` | 93 | inventory | Generator configuration/tooling: NativePackagingPolicy |
| `src/Swift.Bindings/src/Configuration/NativeSymbolProbe.cs` | 326 | inventory | Generator configuration/tooling: NativeSymbolProbe |
| `src/Swift.Bindings/src/Configuration/NativeThunkCompiler.cs` | 300 | inventory | Generator configuration/tooling: NativeThunkCompiler |
| `src/Swift.Bindings/src/Configuration/PlatformInfo.cs` | 92 | inventory | Generator configuration/tooling: PlatformInfo |
| `src/Swift.Bindings/src/Configuration/PlatformInfoFactory.cs` | 201 | inventory | Generator configuration/tooling: PlatformInfoFactory |
| `src/Swift.Bindings/src/Configuration/PlistReader.cs` | 106 | inventory | Generator configuration/tooling: PlistReader |
| `src/Swift.Bindings/src/Configuration/SimulatorOnlyMemberDetector.cs` | 536 | inventory | Generator configuration/tooling: SimulatorOnlyMemberDetector |
| `src/Swift.Bindings/src/Configuration/SliceVariant.cs` | 68 | inventory | Generator configuration/tooling: SliceVariant |
| `src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs` | 2,416 | inventory | Generator configuration/tooling: StrippedSymbolCSharpReconciler |
| `src/Swift.Bindings/src/Configuration/StructuralBraceScanner.cs` | 98 | inventory | Generator configuration/tooling: StructuralBraceScanner |
| `src/Swift.Bindings/src/Configuration/SupportedToolchain.cs` | 153 | inventory | Generator configuration/tooling: SupportedToolchain |
| `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` | 2,991 | inventory | Generator configuration/tooling: SwiftWrapperCompiler |
| `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs` | 656 | inventory | Generator configuration/tooling: SwiftWrapperPostProcessor |
| `src/Swift.Bindings/src/Configuration/SymbolGraphExtractor.cs` | 100 | inventory | Generator configuration/tooling: SymbolGraphExtractor |
| `src/Swift.Bindings/src/Configuration/TopologicalSort.cs` | 103 | inventory | Generator configuration/tooling: TopologicalSort |
| `src/Swift.Bindings/src/Configuration/WrapperBuildOutcome.cs` | 145 | inventory | Generator configuration/tooling: WrapperBuildOutcome |
| `src/Swift.Bindings/src/Configuration/WrapperXCFrameworkMerger.cs` | 242 | inventory | Generator configuration/tooling: WrapperXCFrameworkMerger |
| `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs` | 610 | inventory | Generator configuration/tooling: XCFrameworkMetadataExtractor |
| `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs` | 1,551 | inventory | Generator configuration/tooling: XCFrameworkResolver |
| `src/Swift.Bindings/src/Configuration/XCFrameworkSlicer.cs` | 417 | inventory | Generator configuration/tooling: XCFrameworkSlicer |
| `src/Swift.Bindings/src/Demangler/ContextAttribute.cs` | 13 | inventory | Swift demangler: ContextAttribute |
| `src/Swift.Bindings/src/Demangler/DemanglingResults.cs` | 184 | inventory | Swift demangler: DemanglingResults |
| `src/Swift.Bindings/src/Demangler/Enums.cs` | 414 | inventory | Swift demangler: Enums |
| `src/Swift.Bindings/src/Demangler/IReduction.cs` | 158 | inventory | Swift demangler: IReduction |
| `src/Swift.Bindings/src/Demangler/MatchRule.cs` | 152 | inventory | Swift demangler: MatchRule |
| `src/Swift.Bindings/src/Demangler/Node.cs` | 313 | inventory | Swift demangler: Node |
| `src/Swift.Bindings/src/Demangler/PunyCode.cs` | 115 | inventory | Swift demangler: PunyCode |
| `src/Swift.Bindings/src/Demangler/ReductionDiagnostics.cs` | 172 | inventory | Swift demangler: ReductionDiagnostics |
| `src/Swift.Bindings/src/Demangler/RuleRunner.cs` | 44 | inventory | Swift demangler: RuleRunner |
| `src/Swift.Bindings/src/Demangler/StringSlice.cs` | 233 | inventory | Swift demangler: StringSlice |
| `src/Swift.Bindings/src/Demangler/Swift5Demangler.cs` | 3,398 | inventory | Swift demangler: Swift5Demangler |
| `src/Swift.Bindings/src/Demangler/Swift5Reducer.cs` | 1,041 | inventory | Swift demangler: Swift5Reducer |
| `src/Swift.Bindings/src/Demangler/TbdParser/Models/ParsingException.cs` | 24 | inventory | Build model/DTO: ParsingException |
| `src/Swift.Bindings/src/Demangler/TbdParser/Models/Symbol.cs` | 73 | inventory | Build model/DTO: Symbol |
| `src/Swift.Bindings/src/Demangler/TbdParser/Models/TbdFile.cs` | 79 | inventory | Build model/DTO: TbdFile |
| `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/ITbdFormatParser.cs` | 27 | inventory | Swift demangler: ITbdFormatParser |
| `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/JsonTbdFormatParser.cs` | 181 | inventory | Swift demangler: JsonTbdFormatParser |
| `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/TbdFormatParserBase.cs` | 37 | inventory | Swift demangler: TbdFormatParserBase |
| `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs` | 446 | inventory | Swift demangler: YamlLikeTbdFormatParser |
| `src/Swift.Bindings/src/Demangler/TbdParser/TbdParser.cs` | 91 | inventory | Swift demangler: TbdParser |
| `src/Swift.Bindings/src/Emitter/AbiContractChecker.cs` | 745 | inventory | Code emitter: AbiContractChecker |
| `src/Swift.Bindings/src/Emitter/ApiManifestEmitter.cs` | 84 | inventory | Code emitter: ApiManifestEmitter |
| `src/Swift.Bindings/src/Emitter/AppleSupplementPrototypeEmitter.cs` | 270 | inventory | Code emitter: AppleSupplementPrototypeEmitter |
| `src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs` | 596 | inventory | Code emitter: BindingProjectEmitter |
| `src/Swift.Bindings/src/Emitter/ConsumerTargetsEmitter.cs` | 467 | inventory | Code emitter: ConsumerTargetsEmitter |
| `src/Swift.Bindings/src/Emitter/DependencyManifestEmitter.cs` | 204 | inventory | Code emitter: DependencyManifestEmitter |
| `src/Swift.Bindings/src/Emitter/IEmitter.cs` | 18 | inventory | Code emitter: IEmitter |
| `src/Swift.Bindings/src/Emitter/ModuleDatabaseEmitter.cs` | 305 | inventory | Code emitter: ModuleDatabaseEmitter |
| `src/Swift.Bindings/src/Emitter/RuntimeVersionRange.cs` | 134 | inventory | Code emitter: RuntimeVersionRange |
| `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncSequenceEmitter.cs` | 176 | inventory | Code emitter: AsyncSequenceEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncStreamEmitter.cs` | 353 | inventory | Code emitter: AsyncStreamEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/AvailabilityAttributeEmitter.cs` | 435 | inventory | Code emitter: AvailabilityAttributeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/BridgeHints.cs` | 348 | inventory | Code emitter: BridgeHints |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CancellationTaskEmitter.cs` | 193 | inventory | Code emitter: CancellationTaskEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclLoweringDescriptor.cs` | 83 | inventory | Code emitter: CdeclLoweringDescriptor |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclMarshallingHelper.cs` | 131 | inventory | Code emitter: CdeclMarshallingHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs` | 1,056 | inventory | Code emitter: CdeclParamMapper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclReturnMapping.cs` | 157 | inventory | Code emitter: CdeclReturnMapping |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclReturnRenderer.cs` | 160 | inventory | Code emitter: CdeclReturnRenderer |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclSignatureContract.cs` | 166 | inventory | Code emitter: CdeclSignatureContract |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosedStaticFactoryGate.cs` | 133 | inventory | Code emitter: ClosedStaticFactoryGate |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureContextHelperEmitter.cs` | 101 | inventory | Closure marshalling emitter: ClosureContextHelperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Async.cs` | 414 | inventory | Closure marshalling emitter: ClosureEmitter.Async |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.AsyncSwiftWrapper.cs` | 518 | inventory | Closure marshalling emitter: ClosureEmitter.AsyncSwiftWrapper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` | 1,468 | inventory | Closure marshalling emitter: ClosureEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.FailFastCatch.cs` | 37 | inventory | Closure marshalling emitter: ClosureEmitter.FailFastCatch |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.IndirectReturn.cs` | 315 | inventory | Closure marshalling emitter: ClosureEmitter.IndirectReturn |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.InvokeThunk.cs` | 724 | inventory | Closure marshalling emitter: ClosureEmitter.InvokeThunk |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.StructParams.cs` | 422 | inventory | Closure marshalling emitter: ClosureEmitter.StructParams |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs` | 1,345 | inventory | Closure marshalling emitter: ClosureEmitter.SwiftWrapper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Throwing.cs` | 479 | inventory | Closure marshalling emitter: ClosureEmitter.Throwing |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureParamTombstoneEmitter.cs` | 317 | inventory | Closure marshalling emitter: ClosureParamTombstoneEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CompletionHandlerDetector.cs` | 256 | inventory | Code emitter: CompletionHandlerDetector |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorAdmissibility.cs` | 253 | inventory | Code emitter: ConstructorAdmissibility |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` | 1,428 | inventory | Swift @_cdecl wrapper emitter: ConstructorWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/EnumCaseWrapperEmitter.cs` | 349 | inventory | Swift @_cdecl wrapper emitter: EnumCaseWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ErrorDescriptionEmitter.cs` | 449 | inventory | Code emitter: ErrorDescriptionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ErrorEnumRegistryEmitter.cs` | 232 | inventory | Code emitter: ErrorEnumRegistryEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ErrorRegistryHelperEmitter.cs` | 584 | inventory | Code emitter: ErrorRegistryHelperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | 7,432 | inventory | Code emitter: EveryProtocolEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/FinalizerSeamEmitter.cs` | 45 | inventory | Code emitter: FinalizerSeamEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/GenericDispatchEmitter.cs` | 682 | inventory | Code emitter: GenericDispatchEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/GenericProtocolEmitter.cs` | 138 | inventory | Code emitter: GenericProtocolEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs` | 615 | inventory | Code emitter: GenericTypeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs` | 403 | inventory | Emitter type/method handler: AccessorConversionVisitors |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AppEntityKeyPathSingletonEmitter.cs` | 311 | inventory | Emitter type/method handler: AppEntityKeyPathSingletonEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ArraySliceNormalizationEmitter.cs` | 847 | inventory | Emitter type/method handler: ArraySliceNormalizationEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncHarnessEmitter.cs` | 1,903 | inventory | Emitter type/method handler: AsyncHarnessEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncMethodGenericBridgeEmitter.cs` | 1,472 | inventory | Emitter type/method handler: AsyncMethodGenericBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncResultPlan.cs` | 110 | inventory | Emitter type/method handler: AsyncResultPlan |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` | 1,587 | inventory | Emitter type/method handler: ClassHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClosedConstrainedClosureEmitter.cs` | 576 | inventory | Emitter type/method handler: ClosedConstrainedClosureEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/CodableJsonEmitter.cs` | 257 | inventory | Emitter type/method handler: CodableJsonEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/CollectionProjectionEmitter.cs` | 627 | inventory | Emitter type/method handler: CollectionProjectionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs` | 775 | inventory | Emitter type/method handler: ConcreteProtocolSpecializationEmitter.Async |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` | 1,205 | inventory | Emitter type/method handler: ConcreteProtocolSpecializationEmitter.AsyncGenericParent |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` | 3,617 | inventory | Emitter type/method handler: ConcreteProtocolSpecializationEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs` | 93 | inventory | Emitter type/method handler: ConcreteProtocolSpecializationEmitter.Sync |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConformerKeyPathInitFactoryEmitter.cs` | 533 | inventory | Emitter type/method handler: ConformerKeyPathInitFactoryEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConstrainedExistentialBridge.cs` | 586 | inventory | Emitter type/method handler: ConstrainedExistentialBridge |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConstrainedExtensionEmitter.cs` | 1,728 | inventory | Emitter type/method handler: ConstrainedExtensionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/CrossModuleExtensionEmitter.Class.cs` | 1,505 | inventory | Emitter type/method handler: CrossModuleExtensionEmitter.Class |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/CrossModuleExtensionEmitter.cs` | 777 | inventory | Emitter type/method handler: CrossModuleExtensionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/CrossModuleExtensionEmitter.Struct.cs` | 991 | inventory | Emitter type/method handler: CrossModuleExtensionEmitter.Struct |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs` | 1,174 | inventory | Emitter type/method handler: DefaultParameterOverloadEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` | 1,459 | inventory | Emitter type/method handler: EnumHandler.CaseConstruction |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseInspection.cs` | 506 | inventory | Emitter type/method handler: EnumHandler.CaseInspection |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` | 1,014 | inventory | Emitter type/method handler: EnumHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.Marshalling.cs` | 917 | inventory | Emitter type/method handler: EnumHandler.Marshalling |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.RawRepresentable.cs` | 692 | inventory | Emitter type/method handler: EnumHandler.RawRepresentable |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.SimpleEnum.cs` | 1,817 | inventory | Emitter type/method handler: EnumHandler.SimpleEnum |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumISwiftObjectMethodWriter.cs` | 414 | inventory | Emitter type/method handler: EnumISwiftObjectMethodWriter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EquatableConformanceHelper.cs` | 135 | inventory | Emitter type/method handler: EquatableConformanceHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs` | 1,632 | inventory | Emitter type/method handler: ExistentialBypassEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExtensionMarshallingHelper.cs` | 295 | inventory | Emitter type/method handler: ExtensionMarshallingHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ForeignTypeExtensionEmitter.cs` | 1,376 | inventory | Emitter type/method handler: ForeignTypeExtensionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` | 784 | inventory | Emitter type/method handler: FrozenStructHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/GenericClosureBridgeEmitter.cs` | 1,138 | inventory | Emitter type/method handler: GenericClosureBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/HandlerFactory.cs` | 21 | inventory | Emitter type/method handler: HandlerFactory |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/IMethodBridgeEmitter.cs` | 301 | inventory | Emitter type/method handler: IMethodBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/IMethodPostProcessor.cs` | 126 | inventory | Emitter type/method handler: IMethodPostProcessor |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathBagValueSpecializationEmitter.cs` | 830 | inventory | Emitter type/method handler: KeyPathBagValueSpecializationEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathBagWalker.cs` | 315 | inventory | Emitter type/method handler: KeyPathBagWalker |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathSingletonEmitter.cs` | 533 | inventory | Emitter type/method handler: KeyPathSingletonEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KvoExtensionEmitter.cs` | 300 | inventory | Emitter type/method handler: KvoExtensionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MarkerProtocolOverloadEmitter.cs` | 325 | inventory | Emitter type/method handler: MarkerProtocolOverloadEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MetatypeArrayBridgeEmitter.cs` | 385 | inventory | Emitter type/method handler: MetatypeArrayBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs` | 2,233 | inventory | Emitter type/method handler: MethodClosureBridge |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodGenericBridgeEmitter.cs` | 1,008 | inventory | Emitter type/method handler: MethodGenericBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | 2,014 | inventory | Emitter type/method handler: MethodHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` | 1,085 | inventory | Emitter type/method handler: MethodSignature |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodValidationGates.cs` | 318 | inventory | Emitter type/method handler: MethodValidationGates |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` | 2,526 | inventory | Emitter type/method handler: ModuleHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NativeIntOverloadEmitter.cs` | 430 | inventory | Emitter type/method handler: NativeIntOverloadEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NestedClosureBridge.cs` | 1,673 | inventory | Emitter type/method handler: NestedClosureBridge |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` | 521 | inventory | Emitter type/method handler: NonFrozenStructHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` | 1,049 | inventory | Emitter type/method handler: OperatorHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` | 1,300 | inventory | Emitter type/method handler: PInvokeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` | 1,729 | inventory | Emitter type/method handler: PropertyHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolExtensionClosureBridge.cs` | 842 | inventory | Emitter type/method handler: ProtocolExtensionClosureBridge |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs` | 2,990 | inventory | Emitter type/method handler: ProtocolExtensionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` | 1,701 | inventory | Emitter type/method handler: ProtocolHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ReceiverConversionVisitors.cs` | 184 | inventory | Emitter type/method handler: ReceiverConversionVisitors |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/SubclassClosedParentTrampolineEmitter.cs` | 319 | inventory | Emitter type/method handler: SubclassClosedParentTrampolineEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/SubscriptHandler.cs` | 1,040 | inventory | Emitter type/method handler: SubscriptHandler |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ThrowingClosureSimplificationEmitter.cs` | 423 | inventory | Emitter type/method handler: ThrowingClosureSimplificationEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` | 1,664 | inventory | Emitter type/method handler: TypeHandlerHelpers |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs` | 1,666 | inventory | Emitter type/method handler: WrapperEmitter.Async |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs` | 1,604 | inventory | Emitter type/method handler: WrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.FailableFactory.cs` | 334 | inventory | Emitter type/method handler: WrapperEmitter.FailableFactory |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` | 1,656 | inventory | Emitter type/method handler: WrapperEmitter.Marshalling |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Return.cs` | 1,642 | inventory | Emitter type/method handler: WrapperEmitter.Return |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Signature.cs` | 733 | inventory | Emitter type/method handler: WrapperEmitter.Signature |
| `src/Swift.Bindings/src/Emitter/StringEmitter/HashUtility.cs` | 45 | inventory | Code emitter: HashUtility |
| `src/Swift.Bindings/src/Emitter/StringEmitter/InterfacePropertyNamePrecomputer.cs` | 74 | inventory | Code emitter: InterfacePropertyNamePrecomputer |
| `src/Swift.Bindings/src/Emitter/StringEmitter/InternalTypeReferenceWalker.cs` | 275 | inventory | Code emitter: InternalTypeReferenceWalker |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MarshalPlanRenderer.cs` | 62 | inventory | Code emitter: MarshalPlanRenderer |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` | 1,430 | inventory | Code emitter: MemberEmissionValidator |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberGateEvaluator.cs` | 569 | inventory | Code emitter: MemberGateEvaluator |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs` | 824 | inventory | Code emitter: MemberValidationPipeline |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MetadataWrapperEmitter.cs` | 157 | inventory | Swift @_cdecl wrapper emitter: MetadataWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MetatypeHelperEmitter.cs` | 428 | inventory | Code emitter: MetatypeHelperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs` | 1,955 | inventory | Swift @_cdecl wrapper emitter: MethodWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs` | 1,955 | inventory | Code emitter: ModuleEmissionContext |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` | 313 | inventory | Code emitter: ModuleEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleFileSplitter.cs` | 116 | inventory | Code emitter: ModuleFileSplitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/NamespaceFacadeDetector.cs` | 155 | inventory | Code emitter: NamespaceFacadeDetector |
| `src/Swift.Bindings/src/Emitter/StringEmitter/NamespaceFacadeEmitter.cs` | 83 | inventory | Code emitter: NamespaceFacadeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ObjCOverridePropertyWrapperEmitter.cs` | 183 | inventory | Swift @_cdecl wrapper emitter: ObjCOverridePropertyWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/OptionalMarshalStrategy.cs` | 320 | inventory | Code emitter: OptionalMarshalStrategy |
| `src/Swift.Bindings/src/Emitter/StringEmitter/OptionalPointerWrapperEmitter.cs` | 717 | inventory | Swift @_cdecl wrapper emitter: OptionalPointerWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/OptionalReferenceClassifier.cs` | 35 | inventory | Code emitter: OptionalReferenceClassifier |
| `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeEmitHelper.cs` | 435 | inventory | Code emitter: PInvokeEmitHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` | 878 | inventory | Code emitter: PInvokeHelperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/PropertyWrapperEmitter.cs` | 1,339 | inventory | Swift @_cdecl wrapper emitter: PropertyWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolConformanceValidator.cs` | 1,167 | inventory | Code emitter: ProtocolConformanceValidator |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolExtensionDefaultsIndex.cs` | 486 | inventory | Code emitter: ProtocolExtensionDefaultsIndex |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolMethodDisambiguator.cs` | 303 | inventory | Code emitter: ProtocolMethodDisambiguator |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmissionPolicy.cs` | 106 | inventory | Protocol proxy emitter part: ProtocolProxyEmissionPolicy |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.ClosureThunks.cs` | 182 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.ClosureThunks |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.CrossModuleParent.cs` | 234 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.CrossModuleParent |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` | 331 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Helpers.cs` | 181 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.Helpers |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` | 2,952 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.InterfaceImpl |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | 2,744 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.Receivers |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs` | 773 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.StaticInit |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.SwiftObject.cs` | 818 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.SwiftObject |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Vtables.cs` | 246 | inventory | Protocol proxy emitter part: ProtocolProxyEmitter.Vtables |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolSignatureHelper.cs` | 647 | inventory | Code emitter: ProtocolSignatureHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolVtableMembers.cs` | 27 | inventory | Code emitter: ProtocolVtableMembers |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SelfReconstructionEmitter.cs` | 76 | inventory | Code emitter: SelfReconstructionEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SilentTombstoneRegistrar.cs` | 149 | inventory | Code emitter: SilentTombstoneRegistrar |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SplitFileNaming.cs` | 89 | inventory | Code emitter: SplitFileNaming |
| `src/Swift.Bindings/src/Emitter/StringEmitter/StringReturnEmitter.cs` | 67 | inventory | Code emitter: StringReturnEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SubscriptWrapperEmitter.cs` | 729 | inventory | Swift @_cdecl wrapper emitter: SubscriptWrapperEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SuppressedProxyReferenceException.cs` | 34 | inventory | Code emitter: SuppressedProxyReferenceException |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftBuilder.cs` | 282 | inventory | Code emitter: SwiftBuilder |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftErrorMintEmitter.cs` | 182 | inventory | Code emitter: SwiftErrorMintEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftTypeNameHelper.cs` | 161 | inventory | Code emitter: SwiftTypeNameHelper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeCollector.cs` | 53 | inventory | SwiftUI bridge emitter: SwiftUIBridgeCollector |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.AsyncPattern.cs` | 1,425 | inventory | SwiftUI bridge emitter: SwiftUIBridgeEmitter.AsyncPattern |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | 4,225 | inventory | SwiftUI bridge emitter: SwiftUIBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.InitAnalyzer.cs` | 1,010 | inventory | SwiftUI bridge emitter: SwiftUIBridgeEmitter.InitAnalyzer |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.Lifecycle.cs` | 522 | inventory | SwiftUI bridge emitter: SwiftUIBridgeEmitter.Lifecycle |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIViewDetector.cs` | 68 | inventory | SwiftUI bridge emitter: SwiftUIViewDetector |
| `src/Swift.Bindings/src/Emitter/StringEmitter/TextWriter/IndentedTextWriter.cs` | 139 | inventory | Code emitter: IndentedTextWriter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/TextWriter/IndentedTextWriterExtensions.cs` | 16 | inventory | Code emitter: IndentedTextWriterExtensions |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ThemeBridgeEmitter.cs` | 711 | inventory | Code emitter: ThemeBridgeEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/TypeMetadataAccessorSkipGate.cs` | 65 | inventory | Code emitter: TypeMetadataAccessorSkipGate |
| `src/Swift.Bindings/src/Emitter/StringEmitter/TypeSkipPrePass.cs` | 139 | inventory | Code emitter: TypeSkipPrePass |
| `src/Swift.Bindings/src/Emitter/StringEmitter/UcoGuardEmitter.cs` | 256 | inventory | Code emitter: UcoGuardEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/UnsupportedCommentEmitter.cs` | 77 | inventory | Code emitter: UnsupportedCommentEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/UnsupportedSwiftTypeSupport.cs` | 163 | inventory | Code emitter: UnsupportedSwiftTypeSupport |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Utf8SliceEmitter.cs` | 146 | inventory | Code emitter: Utf8SliceEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ValidationContext.cs` | 120 | inventory | Code emitter: ValidationContext |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ValidationRuleSet.cs` | 469 | inventory | Code emitter: ValidationRuleSet |
| `src/Swift.Bindings/src/Emitter/StringEmitter/VtableLayout.cs` | 346 | inventory | Code emitter: VtableLayout |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs` | 2,585 | inventory | Code emitter: WitnessDispatchEmitter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperEligibility.cs` | 35 | inventory | Swift @_cdecl wrapper emitter: WrapperEligibility |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperEmitterHelpers.cs` | 421 | inventory | Swift @_cdecl wrapper emitter: WrapperEmitterHelpers |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolContractException.cs` | 29 | inventory | Swift @_cdecl wrapper emitter: WrapperSymbolContractException |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolContractGate.cs` | 103 | inventory | Swift @_cdecl wrapper emitter: WrapperSymbolContractGate |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolIntegrityGate.cs` | 139 | inventory | Swift @_cdecl wrapper emitter: WrapperSymbolIntegrityGate |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` | 2,250 | inventory | Swift @_cdecl wrapper emitter: WrapperValidation |
| `src/Swift.Bindings/src/Emitter/StringEmitter/XmlDocCommentEmitter.cs` | 213 | inventory | Code emitter: XmlDocCommentEmitter |
| `src/Swift.Bindings/src/Emitter/SwiftTypeOwnershipManifest.cs` | 222 | inventory | Code emitter: SwiftTypeOwnershipManifest |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/Arm64ThunkTarget.cs` | 290 | inventory | Native thunk emission: Arm64ThunkTarget |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` | 883 | inventory | Native thunk emission: NativeThunkEmitter |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/SwiftCallTargetResolver.cs` | 88 | inventory | Native thunk emission: SwiftCallTargetResolver |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/SysVThunkTarget.cs` | 395 | inventory | Native thunk emission: SysVThunkTarget |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` | 251 | inventory | Native thunk emission: ThunkAssemblyEmitter |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkTargetArch.cs` | 76 | inventory | Native thunk emission: ThunkTargetArch |
| `src/Swift.Bindings/src/Emitter/ThunkEmitter/TypeLowering.cs` | 472 | inventory | Native thunk emission: TypeLowering |
| `src/Swift.Bindings/src/Emitter/TrimmerDescriptorEmitter.cs` | 108 | inventory | Code emitter: TrimmerDescriptorEmitter |
| `src/Swift.Bindings/src/Marshaler/AsyncSequenceHandler.cs` | 285 | inventory | Marshaler/handler: AsyncSequenceHandler |
| `src/Swift.Bindings/src/Marshaler/AsyncStreamHandler.cs` | 260 | inventory | Marshaler/handler: AsyncStreamHandler |
| `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` | 1,885 | inventory | Marshaler/handler: BoundGenericsHandler |
| `src/Swift.Bindings/src/Marshaler/BoundGenericTranslation.cs` | 154 | inventory | Marshaler/handler: BoundGenericTranslation |
| `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` | 2,483 | inventory | Marshaler/handler: ClosureHandler |
| `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs` | 1,690 | inventory | Marshaler/handler: ConcreteSpecializationEngine |
| `src/Swift.Bindings/src/Marshaler/Conductor.cs` | 139 | inventory | Marshaler/handler: Conductor |
| `src/Swift.Bindings/src/Marshaler/ConformanceOracle.cs` | 295 | inventory | Marshaler/handler: ConformanceOracle |
| `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` | 1,362 | inventory | Marshaler/handler: ExistentialHandler |
| `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` | 701 | inventory | Marshaler/handler: IEnvironment |
| `src/Swift.Bindings/src/Marshaler/IFactory.cs` | 23 | inventory | Marshaler/handler: IFactory |
| `src/Swift.Bindings/src/Marshaler/IHandler.cs` | 1,343 | inventory | Marshaler/handler: IHandler |
| `src/Swift.Bindings/src/Marshaler/MarshalingContext.cs` | 138 | inventory | Marshaler/handler: MarshalingContext |
| `src/Swift.Bindings/src/Marshaler/MarshalledType.cs` | 200 | inventory | Marshaler/handler: MarshalledType |
| `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` | 865 | inventory | Marshaler/handler: MarshallingHelpers |
| `src/Swift.Bindings/src/Marshaler/NameProvider.cs` | 2,002 | inventory | Marshaler/handler: NameProvider |
| `src/Swift.Bindings/src/Marshaler/Projection/ArrayProjection.cs` | 475 | inventory | Type projection: ArrayProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/AsyncProjection.cs` | 265 | inventory | Type projection: AsyncProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/BlittableProjection.cs` | 40 | inventory | Type projection: BlittableProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/BoolProjection.cs` | 24 | inventory | Type projection: BoolProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ClassProjection.cs` | 66 | inventory | Type projection: ClassProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ClosureProjection.cs` | 361 | inventory | Type projection: ClosureProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/DataProjection.cs` | 53 | inventory | Type projection: DataProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/DateProjection.cs` | 60 | inventory | Type projection: DateProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/DictionaryProjection.cs` | 632 | inventory | Type projection: DictionaryProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ExistentialElementCarrier.cs` | 53 | inventory | Type projection: ExistentialElementCarrier |
| `src/Swift.Bindings/src/Marshaler/Projection/ExistentialProjection.cs` | 490 | inventory | Type projection: ExistentialProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/FrozenWithMemoryProjection.cs` | 107 | inventory | Type projection: FrozenWithMemoryProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/IProjectionVisitor.cs` | 35 | inventory | Type projection: IProjectionVisitor |
| `src/Swift.Bindings/src/Marshaler/Projection/ITypeProjection.cs` | 200 | inventory | Type projection: ITypeProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs` | 107 | inventory | Type projection: KeyPathProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/MarshalPlan.cs` | 63 | inventory | Type projection: MarshalPlan |
| `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlan.cs` | 183 | inventory | Type projection: MethodMarshalPlan |
| `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` | 1,490 | inventory | Type projection: MethodMarshalPlanBuilder |
| `src/Swift.Bindings/src/Marshaler/Projection/NativeRemappedProjection.cs` | 131 | inventory | Type projection: NativeRemappedProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/NonFrozenStructProjection.cs` | 108 | inventory | Type projection: NonFrozenStructProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ObjCBridgeableProjection.cs` | 64 | inventory | Type projection: ObjCBridgeableProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ObjCBridgedProjection.cs` | 51 | inventory | Type projection: ObjCBridgedProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ObjCRootedClassProjection.cs` | 65 | inventory | Type projection: ObjCRootedClassProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/OptionalProjection.cs` | 843 | inventory | Type projection: OptionalProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/ResultProjection.cs` | 113 | inventory | Type projection: ResultProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/SetProjection.cs` | 381 | inventory | Type projection: SetProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/SimpleEnumProjection.cs` | 55 | inventory | Type projection: SimpleEnumProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/StringProjection.cs` | 61 | inventory | Type projection: StringProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/SuppressedProxyProjectionWalk.cs` | 74 | inventory | Type projection: SuppressedProxyProjectionWalk |
| `src/Swift.Bindings/src/Marshaler/Projection/SuppressedProxyTypeSpecWalk.cs` | 99 | inventory | Type projection: SuppressedProxyTypeSpecWalk |
| `src/Swift.Bindings/src/Marshaler/Projection/TupleProjection.cs` | 264 | inventory | Type projection: TupleProjection |
| `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` | 701 | inventory | Type projection: TypeProjectionFactory |
| `src/Swift.Bindings/src/Marshaler/RouteCSortShapeEligibility.cs` | 291 | inventory | Marshaler/handler: RouteCSortShapeEligibility |
| `src/Swift.Bindings/src/Marshaler/SwiftDefaultValueMapper.cs` | 231 | inventory | Marshaler/handler: SwiftDefaultValueMapper |
| `src/Swift.Bindings/src/Marshaler/TupleHandler.cs` | 668 | inventory | Marshaler/handler: TupleHandler |
| `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs` | 283 | inventory | Marshaler/handler: TypeConversionHandler |
| `src/Swift.Bindings/src/Marshaler/TypeHandlerContext.cs` | 55 | inventory | Marshaler/handler: TypeHandlerContext |
| `src/Swift.Bindings/src/Model/AvailabilityAnnotation.cs` | 18 | inventory | IR model: AvailabilityAnnotation |
| `src/Swift.Bindings/src/Model/AvailabilityHelpers.cs` | 160 | inventory | IR model: AvailabilityHelpers |
| `src/Swift.Bindings/src/Model/DocComment.cs` | 45 | inventory | IR model: DocComment |
| `src/Swift.Bindings/src/Model/FrameworkDependencyInfo.cs` | 100 | inventory | IR model: FrameworkDependencyInfo |
| `src/Swift.Bindings/src/Model/SourcePosition.cs` | 28 | inventory | IR model: SourcePosition |
| `src/Swift.Bindings/src/Model/TypeDecl/AccessorDecl.cs` | 27 | inventory | IR model: AccessorDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ArgumentDecl.cs` | 86 | inventory | IR model: ArgumentDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/BaseDecl.cs` | 61 | inventory | IR model: BaseDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ClassDecl.cs` | 124 | inventory | IR model: ClassDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/EnumDecl.cs` | 307 | inventory | IR model: EnumDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ExtensionMemberCandidate.cs` | 41 | inventory | IR model: ExtensionMemberCandidate |
| `src/Swift.Bindings/src/Model/TypeDecl/GenericArgumentDecl.cs` | 41 | inventory | IR model: GenericArgumentDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/GenericSignatureModel.cs` | 158 | inventory | IR model: GenericSignatureModel |
| `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` | 538 | inventory | IR model: MethodDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ModuleDecl.cs` | 94 | inventory | IR model: ModuleDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/OperatorDecl.cs` | 50 | inventory | IR model: OperatorDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ParameterOwnership.cs` | 37 | inventory | IR model: ParameterOwnership |
| `src/Swift.Bindings/src/Model/TypeDecl/PropertyDecl.cs` | 134 | inventory | IR model: PropertyDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ProtocolConformance.cs` | 37 | inventory | IR model: ProtocolConformance |
| `src/Swift.Bindings/src/Model/TypeDecl/ProtocolDecl.cs` | 139 | inventory | IR model: ProtocolDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/ProtocolExtensionMethodDecl.cs` | 78 | inventory | IR model: ProtocolExtensionMethodDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/StructDecl.cs` | 26 | inventory | IR model: StructDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/SubscriptDecl.cs` | 47 | inventory | IR model: SubscriptDecl |
| `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` | 149 | inventory | IR model: TypeDecl |
| `src/Swift.Bindings/src/Model/TypeNames/CSharpTypeName.cs` | 116 | inventory | IR model: CSharpTypeName |
| `src/Swift.Bindings/src/Model/TypeNames/SwiftTypeName.cs` | 91 | inventory | IR model: SwiftTypeName |
| `src/Swift.Bindings/src/Model/TypeSpec/AssociatedTypeReferenceSpec.cs` | 74 | inventory | IR model: AssociatedTypeReferenceSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/ClosureTypeSpec.cs` | 214 | inventory | IR model: ClosureTypeSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/NamedTypeSpec.cs` | 185 | inventory | IR model: NamedTypeSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/ProtocolListTypeSpec.cs` | 106 | inventory | IR model: ProtocolListTypeSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/Provenance.cs` | 121 | inventory | IR model: Provenance |
| `src/Swift.Bindings/src/Model/TypeSpec/SwiftFunction.cs` | 98 | inventory | IR model: SwiftFunction |
| `src/Swift.Bindings/src/Model/TypeSpec/TupleTypeSpec.cs` | 101 | inventory | IR model: TupleTypeSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/TypeSpec.cs` | 445 | inventory | IR model: TypeSpec |
| `src/Swift.Bindings/src/Model/TypeSpec/TypeSpecAttribute.cs` | 56 | inventory | IR model: TypeSpecAttribute |
| `src/Swift.Bindings/src/Model/TypeSpec/TypeSpecHelpers.cs` | 128 | inventory | IR model: TypeSpecHelpers |
| `src/Swift.Bindings/src/Model/TypeSpec/TypeSpecKind.cs` | 31 | inventory | IR model: TypeSpecKind |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/MemberSignatureNormalizer.cs` | 395 | inventory | IR model: MemberSignatureNormalizer |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/SwiftModuleAliases.cs` | 41 | inventory | IR model: SwiftModuleAliases |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecParseException.cs` | 30 | inventory | IR model: TypeSpecParseException |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecParser.cs` | 459 | inventory | IR model: TypeSpecParser |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecToken.cs` | 180 | inventory | IR model: TypeSpecToken |
| `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecTokenizer.cs` | 261 | inventory | IR model: TypeSpecTokenizer |
| `src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs` | 1,238 | inventory | Code emitter: ApiDefinitionEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCAvailabilityEmitter.cs` | 190 | inventory | Code emitter: ObjCAvailabilityEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCBindingProjectEmitter.cs` | 177 | inventory | Code emitter: ObjCBindingProjectEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCDocCommentEmitter.cs` | 37 | inventory | Code emitter: ObjCDocCommentEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCMetadataPropsEmitter.cs` | 70 | inventory | Code emitter: ObjCMetadataPropsEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCTypeMapper.cs` | 737 | inventory | Code emitter: ObjCTypeMapper |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCUsingsEmitter.cs` | 245 | inventory | Code emitter: ObjCUsingsEmitter |
| `src/Swift.Bindings/src/ObjC/Emitter/StructsAndEnumsEmitter.cs` | 749 | inventory | Code emitter: StructsAndEnumsEmitter |
| `src/Swift.Bindings/src/ObjC/Model/ObjCAvailability.cs` | 42 | inventory | IR model: ObjCAvailability |
| `src/Swift.Bindings/src/ObjC/Model/ObjCBindingDiagnostics.cs` | 65 | inventory | IR model: ObjCBindingDiagnostics |
| `src/Swift.Bindings/src/ObjC/Model/ObjCDeclarations.cs` | 235 | inventory | IR model: ObjCDeclarations |
| `src/Swift.Bindings/src/ObjC/Model/ObjCModule.cs` | 41 | inventory | IR model: ObjCModule |
| `src/Swift.Bindings/src/ObjC/Model/ObjCTypeRef.cs` | 28 | inventory | IR model: ObjCTypeRef |
| `src/Swift.Bindings/src/ObjC/Parser/ClangAstInvoker.cs` | 248 | inventory | ABI/interface parser: ClangAstInvoker |
| `src/Swift.Bindings/src/ObjC/Parser/ClangAstParser.cs` | 2,066 | inventory | ABI/interface parser: ClangAstParser |
| `src/Swift.Bindings/src/ObjC/Parser/ObjCAvailabilityParser.cs` | 513 | inventory | ABI/interface parser: ObjCAvailabilityParser |
| `src/Swift.Bindings/src/ObjC/Parser/ObjCTypeRefParser.cs` | 616 | inventory | ABI/interface parser: ObjCTypeRefParser |
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCBridgeRecordFactory.cs` | 208 | inventory | ObjC pipeline: ObjCBridgeRecordFactory |
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCBridgeRecordRekeyer.cs` | 60 | inventory | ObjC pipeline: ObjCBridgeRecordRekeyer |
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCPipeline.cs` | 767 | inventory | ObjC pipeline: ObjCPipeline |
| `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` | 303 | inventory | ABI/interface parser: GenericSignatureParser |
| `src/Swift.Bindings/src/Parser/ISwiftParser.cs` | 22 | inventory | ABI/interface parser: ISwiftParser |
| `src/Swift.Bindings/src/Parser/ManglingProbes.cs` | 115 | inventory | ABI/interface parser: ManglingProbes |
| `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` | 1,630 | inventory | ABI/interface parser: ModuleProcessor |
| `src/Swift.Bindings/src/Parser/Producers/IInterfaceFactsProducer.cs` | 33 | inventory | ABI/interface parser: IInterfaceFactsProducer |
| `src/Swift.Bindings/src/Parser/Producers/InterfaceFactKind.cs` | 81 | inventory | ABI/interface parser: InterfaceFactKind |
| `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsAggregator.cs` | 157 | inventory | ABI/interface parser: InterfaceFactsAggregator |
| `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsJson.cs` | 336 | inventory | ABI/interface parser: InterfaceFactsJson |
| `src/Swift.Bindings/src/Parser/Producers/PartialSwiftInterfaceFacts.cs` | 70 | inventory | ABI/interface parser: PartialSwiftInterfaceFacts |
| `src/Swift.Bindings/src/Parser/Producers/SwiftSyntaxInterfaceFactsProducer.cs` | 575 | inventory | ABI/interface parser: SwiftSyntaxInterfaceFactsProducer |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | 4,224 | inventory | ABI/interface parser: SwiftABIParser |
| `src/Swift.Bindings/src/Parser/SwiftInterfaceFacts.cs` | 347 | inventory | ABI/interface parser: SwiftInterfaceFacts |
| `src/Swift.Bindings/src/Parser/SwiftTypeListText.cs` | 132 | inventory | ABI/interface parser: SwiftTypeListText |
| `src/Swift.Bindings/src/Parser/SymbolGraphDocParser.cs` | 365 | inventory | ABI/interface parser: SymbolGraphDocParser |
| `src/Swift.Bindings/src/Parser/UnderscoreProtocolSynthesizer.cs` | 525 | inventory | ABI/interface parser: UnderscoreProtocolSynthesizer |
| `src/Swift.Bindings/src/Program.cs` | 2,523 | inventory | Generator CLI entry point |
| `src/Swift.Bindings/src/Reporting/BindingReport.cs` | 349 | inventory | Binding/skip report: BindingReport |
| `src/Swift.Bindings/src/Reporting/BindingReportProjection.cs` | 129 | inventory | Binding/skip report: BindingReportProjection |
| `src/Swift.Bindings/src/Reporting/EmissionReportEmitter.cs` | 383 | inventory | Binding/skip report: EmissionReportEmitter |
| `src/Swift.Bindings/src/Reporting/InputResolutionReport.cs` | 112 | inventory | Binding/skip report: InputResolutionReport |
| `src/Swift.Bindings/src/Reporting/MemberDiagnosticIdentity.cs` | 418 | inventory | Binding/skip report: MemberDiagnosticIdentity |
| `src/Swift.Bindings/src/Reporting/ObjCSkipProjection.cs` | 71 | inventory | Binding/skip report: ObjCSkipProjection |
| `src/Swift.Bindings/src/Reporting/ReportCollector.cs` | 746 | inventory | Binding/skip report: ReportCollector |
| `src/Swift.Bindings/src/Reporting/ReportEmitter.cs` | 141 | inventory | Binding/skip report: ReportEmitter |
| `src/Swift.Bindings/src/Reporting/SkipDisposition.cs` | 281 | inventory | Binding/skip report: SkipDisposition |
| `src/Swift.Bindings/src/Reporting/SkipTriage.cs` | 111 | inventory | Binding/skip report: SkipTriage |
| `src/Swift.Bindings/src/Reporting/SuppressedProxyReporting.cs` | 113 | inventory | Binding/skip report: SuppressedProxyReporting |
| `src/Swift.Bindings/src/Reporting/WorkaroundRecommendations.cs` | 192 | inventory | Binding/skip report: WorkaroundRecommendations |
| `src/Swift.Bindings/src/StdlibConformances/StdlibConformancesRegenCommand.cs` | 275 | inventory | TBD — StdlibConformancesRegenCommand |
| `src/Swift.Bindings/src/TypeDatabase/AppleFrameworkRegistry.cs` | 846 | inventory | Type database: AppleFrameworkRegistry |
| `src/Swift.Bindings/src/TypeDatabase/AppleSupplementReferences.cs` | 76 | inventory | Type database: AppleSupplementReferences |
| `src/Swift.Bindings/src/TypeDatabase/AppleSupplementResolver.cs` | 154 | inventory | Type database: AppleSupplementResolver |
| `src/Swift.Bindings/src/TypeDatabase/ConformanceGraph.cs` | 74 | inventory | Type database: ConformanceGraph |
| `src/Swift.Bindings/src/TypeDatabase/GenerationMode.cs` | 29 | inventory | Type database: GenerationMode |
| `src/Swift.Bindings/src/TypeDatabase/ITypeDatabase.cs` | 195 | inventory | Type database: ITypeDatabase |
| `src/Swift.Bindings/src/TypeDatabase/ModuleDatabase.cs` | 257 | inventory | Type database: ModuleDatabase |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/IResolutionStrategy.cs` | 35 | inventory | Type database: IResolutionStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/ResolutionContext.cs` | 21 | inventory | Type database: ResolutionContext |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/AppleSupplementStrategy.cs` | 50 | inventory | Type database: AppleSupplementStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/BareGenericGuardStrategy.cs` | 48 | inventory | Type database: BareGenericGuardStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/BoundGenericSimdAliasStrategy.cs` | 38 | inventory | Type database: BoundGenericSimdAliasStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/CrossModuleAliasStrategy.cs` | 62 | inventory | Type database: CrossModuleAliasStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/DatabaseLookupStrategy.cs` | 82 | inventory | Type database: DatabaseLookupStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/DynamicSelfStrategy.cs` | 35 | inventory | Type database: DynamicSelfStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/ExistentialStrategy.cs` | 159 | inventory | Type database: ExistentialStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/GenericParameterStrategy.cs` | 41 | inventory | Type database: GenericParameterStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/MetatypeStrategy.cs` | 36 | inventory | Type database: MetatypeStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/ObjCBridgingStrategy.cs` | 39 | inventory | Type database: ObjCBridgingStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/OutOfModuleLookupStrategy.cs` | 49 | inventory | Type database: OutOfModuleLookupStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/PointerStrategy.cs` | 36 | inventory | Type database: PointerStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/PrimitiveAliasStrategy.cs` | 59 | inventory | Type database: PrimitiveAliasStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/SwiftAnyAnyObjectStrategy.cs` | 43 | inventory | Type database: SwiftAnyAnyObjectStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/SwiftErrorStrategy.cs` | 56 | inventory | Type database: SwiftErrorStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/UnsupportedAppleModuleStrategy.cs` | 50 | inventory | Type database: UnsupportedAppleModuleStrategy |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/TypeResolutionResult.cs` | 83 | inventory | Type database: TypeResolutionResult |
| `src/Swift.Bindings/src/TypeDatabase/Resolver/TypeResolver.cs` | 159 | inventory | Type database: TypeResolver |
| `src/Swift.Bindings/src/TypeDatabase/SwiftValueLayout.cs` | 304 | inventory | Type database: SwiftValueLayout |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` | 996 | inventory | Type database: TypeDatabase |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` | 723 | inventory | Type database: TypeDatabaseExtensions |
| `src/Swift.Bindings/src/TypeDatabase/TypeOwnerRegistry.cs` | 579 | inventory | Type database: TypeOwnerRegistry |
| `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs` | 417 | inventory | Type database: TypeRecord |

## src/Swift.Bindings/tests (unit tests, *.cs)

**Files**: 394  
**LOC**: 305,288  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Bindings/tests/UnitTests/ApiManifestBaselineTests.cs` | 141 | inventory | TBD — ApiManifestBaselineTests |
| `src/Swift.Bindings/tests/UnitTests/AppleTypesManifestTests/AppleTypesCsCommandTests.cs` | 209 | inventory | TBD — AppleTypesCsCommandTests |
| `src/Swift.Bindings/tests/UnitTests/AppleTypesManifestTests/AppleTypesCsEmitterTests.cs` | 507 | inventory | TBD — AppleTypesCsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/AppleTypesManifestTests/AppleTypesManifestBuilderTests.cs` | 282 | inventory | TBD — AppleTypesManifestBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/AppleTypesManifestTests/AppleTypesManifestValidatorTests.cs` | 84 | inventory | TBD — AppleTypesManifestValidatorTests |
| `src/Swift.Bindings/tests/UnitTests/ArtifactParityGateTests.cs` | 777 | inventory | TBD — ArtifactParityGateTests |
| `src/Swift.Bindings/tests/UnitTests/AsyncVoidTestMethodTests.cs` | 63 | inventory | TBD — AsyncVoidTestMethodTests |
| `src/Swift.Bindings/tests/UnitTests/BindingsGeneratorCommandTests.cs` | 1,027 | inventory | TBD — BindingsGeneratorCommandTests |
| `src/Swift.Bindings/tests/UnitTests/CatchFreeUcoValidatorTests.cs` | 134 | inventory | TBD — CatchFreeUcoValidatorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/AppleFrameworkImportDetectorTests.cs` | 465 | inventory | TBD — AppleFrameworkImportDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/AutoDepResolverCliTests.cs` | 109 | inventory | TBD — AutoDepResolverCliTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/AutoDepResolverTests.cs` | 287 | inventory | TBD — AutoDepResolverTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/BinaryDependencyAnalyzerTests.cs` | 1,258 | inventory | TBD — BinaryDependencyAnalyzerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/BindingArtifactManifestTests.cs` | 816 | inventory | TBD — BindingArtifactManifestTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/BuildOutcomeTests.cs` | 502 | inventory | TBD — BuildOutcomeTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/CreateLoggerFactoryTests.cs` | 76 | inventory | TBD — CreateLoggerFactoryTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/DepModuleCollisionDetectorTests.cs` | 512 | inventory | TBD — DepModuleCollisionDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/EmittedSwiftTrapLintTests.cs` | 94 | inventory | TBD — EmittedSwiftTrapLintTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/MixedFrameworkDetectionTests.cs` | 191 | inventory | TBD — MixedFrameworkDetectionTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/NativeLinkageProbeTests.cs` | 177 | inventory | TBD — NativeLinkageProbeTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/NativePackagingPolicyTests.cs` | 77 | inventory | TBD — NativePackagingPolicyTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/NativeSymbolProbeTests.cs` | 225 | inventory | TBD — NativeSymbolProbeTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/NativeThunkCompilerTests.cs` | 480 | inventory | TBD — NativeThunkCompilerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/PlatformInfoTests.cs` | 982 | inventory | TBD — PlatformInfoTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/PlistReaderTests.cs` | 289 | inventory | TBD — PlistReaderTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/ProgramModuleDatabaseTests.cs` | 303 | inventory | TBD — ProgramModuleDatabaseTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/ProgramSdkModeTests.cs` | 2,002 | inventory | TBD — ProgramSdkModeTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SimulatorOnlyMemberDetectorTests.cs` | 1,101 | inventory | TBD — SimulatorOnlyMemberDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/StrippedSymbolCSharpReconcilerTests.cs` | 2,689 | inventory | TBD — StrippedSymbolCSharpReconcilerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/StructuralBraceScannerTests.cs` | 135 | inventory | TBD — StructuralBraceScannerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SupportedToolchainMatrixTests.cs` | 222 | inventory | TBD — SupportedToolchainMatrixTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SwiftWrapperCompilerTests.cs` | 3,977 | inventory | TBD — SwiftWrapperCompilerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SwiftWrapperPostProcessorTests.cs` | 2,025 | inventory | TBD — SwiftWrapperPostProcessorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SymbolGraphExtractorTests.cs` | 417 | inventory | TBD — SymbolGraphExtractorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/WrapperXCFrameworkMergerTests.cs` | 379 | inventory | TBD — WrapperXCFrameworkMergerTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/XCFrameworkMetadataExtractorTests.cs` | 881 | inventory | TBD — XCFrameworkMetadataExtractorTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/XCFrameworkMetadataPropsTests.cs` | 536 | inventory | TBD — XCFrameworkMetadataPropsTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/XCFrameworkResolverTests.cs` | 2,321 | inventory | TBD — XCFrameworkResolverTests |
| `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/XCFrameworkSlicerTests.cs` | 505 | inventory | TBD — XCFrameworkSlicerTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/AsyncMarkerTests.cs` | 85 | inventory | TBD — AsyncMarkerTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/BasicDemanglingTests.cs` | 270 | inventory | TBD — BasicDemanglingTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/DemangleSymbolTests.cs` | 956 | inventory | TBD — DemangleSymbolTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/FunctionAnnotationDemanglingTests.cs` | 92 | inventory | TBD — FunctionAnnotationDemanglingTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/PunyCodeTests.cs` | 166 | inventory | TBD — PunyCodeTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/ReductionCorpusLoudnessTests.cs` | 200 | inventory | TBD — ReductionCorpusLoudnessTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/ReductionDiagnosticsTests.cs` | 90 | inventory | TBD — ReductionDiagnosticsTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/StringSliceTests.cs` | 394 | inventory | TBD — StringSliceTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/Swift5ReducerTests.cs` | 907 | inventory | TBD — Swift5ReducerTests |
| `src/Swift.Bindings/tests/UnitTests/DemanglerTests/VariadicMarkerTests.cs` | 56 | inventory | TBD — VariadicMarkerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AbiContractCheckerTests.cs` | 1,034 | inventory | TBD — AbiContractCheckerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AbiSafetyTests.cs` | 3,208 | inventory | TBD — AbiSafetyTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AppEntityKeyPathSingletonEmitterTests.cs` | 196 | inventory | TBD — AppEntityKeyPathSingletonEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ArraySliceNormalizationEmitterTests.cs` | 1,110 | inventory | TBD — ArraySliceNormalizationEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncHarnessEmitterCleanupTests.cs` | 240 | inventory | TBD — AsyncHarnessEmitterCleanupTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncMethodGenericBridgeEmitterTests.cs` | 1,337 | inventory | TBD — AsyncMethodGenericBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncResultPlannerTests.cs` | 124 | inventory | TBD — AsyncResultPlannerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncStreamEmitterTests.cs` | 571 | inventory | TBD — AsyncStreamEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncSwiftWrapperTests.cs` | 2,651 | inventory | TBD — AsyncSwiftWrapperTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AvailabilityAttributeEmitterTests.cs` | 935 | inventory | TBD — AvailabilityAttributeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/AvailabilityHelpersTests.cs` | 163 | inventory | TBD — AvailabilityHelpersTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/BindingProjectEmitterTests.cs` | 2,302 | inventory | TBD — BindingProjectEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/BridgeDispatchTableTests.cs` | 443 | inventory | TBD — BridgeDispatchTableTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CancellationTokenEmitterTests.cs` | 1,508 | inventory | TBD — CancellationTokenEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CdeclLoweringDescriptorTests.cs` | 575 | inventory | TBD — CdeclLoweringDescriptorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CdeclMarshallingHelperTests.cs` | 202 | inventory | TBD — CdeclMarshallingHelperTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CdeclReturnRendererTests.cs` | 398 | inventory | TBD — CdeclReturnRendererTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CdeclSignatureContractTests.cs` | 481 | inventory | TBD — CdeclSignatureContractTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassHandlerTests.cs` | 614 | inventory | TBD — ClassHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassIdentityEmitterTests.cs` | 141 | inventory | TBD — ClassIdentityEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassInheritanceEmissionTests.cs` | 1,951 | inventory | TBD — ClassInheritanceEmissionTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassObjCRootedTests.cs` | 1,258 | inventory | TBD — ClassObjCRootedTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosedConstrainedClosureEmitterTests.cs` | 338 | inventory | TBD — ClosedConstrainedClosureEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosedStaticFactoryGateTests.cs` | 379 | inventory | TBD — ClosedStaticFactoryGateTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureBridgeClassificationParityTests.cs` | 180 | inventory | TBD — ClosureBridgeClassificationParityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureCdeclEmitterTests.cs` | 2,124 | inventory | TBD — ClosureCdeclEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureEmitterAsyncTests.cs` | 522 | inventory | TBD — ClosureEmitterAsyncTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureEmitterDirectTests.cs` | 2,694 | inventory | TBD — ClosureEmitterDirectTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureEmitterStructParamsTests.cs` | 355 | inventory | TBD — ClosureEmitterStructParamsTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClosureParamTombstoneEmitterTests.cs` | 684 | inventory | TBD — ClosureParamTombstoneEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CodableJsonEmitterTests.cs` | 239 | inventory | TBD — CodableJsonEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CollisionRankDeterminismTests.cs` | 99 | inventory | TBD — CollisionRankDeterminismTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CompletionHandlerDetectorTests.cs` | 792 | inventory | TBD — CompletionHandlerDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs` | 4,128 | inventory | TBD — ConcreteSpecializationEngineTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationNeverAssociatedTypeTests.cs` | 239 | inventory | TBD — ConcreteSpecializationNeverAssociatedTypeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConditionalExtensionConstraintTests.cs` | 856 | inventory | TBD — ConditionalExtensionConstraintTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConformerKeyPathInitFactoryEmitterTests.cs` | 416 | inventory | TBD — ConformerKeyPathInitFactoryEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstrainedExistentialBridgeTests.cs` | 931 | inventory | TBD — ConstrainedExistentialBridgeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstrainedExtensionEmitterTests.cs` | 1,364 | inventory | TBD — ConstrainedExtensionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorAdmissibilityTests.cs` | 402 | inventory | TBD — ConstructorAdmissibilityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorHandlerOutputTests.cs` | 1,244 | inventory | TBD — ConstructorHandlerOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs` | 4,068 | inventory | TBD — ConstructorWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConsumerSafetyAttributeTests.cs` | 810 | inventory | TBD — ConsumerSafetyAttributeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConsumerTargetsEmitterTests.cs` | 1,234 | inventory | TBD — ConsumerTargetsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/CrossModuleExtensionEmitterTests.cs` | 942 | inventory | TBD — CrossModuleExtensionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/DebugParameterTests.cs` | 122 | inventory | TBD — DebugParameterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/DefaultParameterOverloadEmitterTests.cs` | 1,781 | inventory | TBD — DefaultParameterOverloadEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/DispatchThunkEmitterTests.cs` | 511 | inventory | TBD — DispatchThunkEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/DotNetIdiomPolishTests.cs` | 939 | inventory | TBD — DotNetIdiomPolishTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EmitterFamilyLivenessTests.cs` | 147 | inventory | TBD — EmitterFamilyLivenessTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EmitterTestHelpers.cs` | 19 | inventory | TBD — EmitterTestHelpers |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EmitterUtilityTests.cs` | 105 | inventory | TBD — EmitterUtilityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumAbiWidthConsistencyTests.cs` | 140 | inventory | TBD — EnumAbiWidthConsistencyTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumCaseAssociatedValueTests.cs` | 105 | inventory | TBD — EnumCaseAssociatedValueTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumCaseWrapperEmitterTests.cs` | 788 | inventory | TBD — EnumCaseWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumExtractionTests.cs` | 277 | inventory | TBD — EnumExtractionTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` | 4,394 | inventory | TBD — EnumHandlerOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerTests.cs` | 998 | inventory | TBD — EnumHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ErrorEnumRegistryEmitterTests.cs` | 712 | inventory | TBD — ErrorEnumRegistryEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ErrorRegistryHelperEmitterTests.cs` | 150 | inventory | TBD — ErrorRegistryHelperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolCarrierAbsenceEmitterTests.cs` | 260 | inventory | TBD — EveryProtocolCarrierAbsenceEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` | 5,271 | inventory | TBD — EveryProtocolEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ExistentialBypassEmitterTests.cs` | 1,035 | inventory | TBD — ExistentialBypassEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ExistentialOptionalGuardTests.cs` | 687 | inventory | TBD — ExistentialOptionalGuardTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ExistentialUnionReturnProjectionParityTests.cs` | 173 | inventory | TBD — ExistentialUnionReturnProjectionParityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/FinalizerSeamEmitterTests.cs` | 76 | inventory | TBD — FinalizerSeamEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ForeignTypeExtensionEmitterTests.cs` | 534 | inventory | TBD — ForeignTypeExtensionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/FrozenStructHandlerTests.cs` | 894 | inventory | TBD — FrozenStructHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenerationModeTests.cs` | 79 | inventory | TBD — GenerationModeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericClosureBridgeEmitterTests.cs` | 1,209 | inventory | TBD — GenericClosureBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericDispatchEmitterTests.cs` | 172 | inventory | TBD — GenericDispatchEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericMethodEmitterTests.cs` | 309 | inventory | TBD — GenericMethodEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericProtocolUnificationTests.cs` | 1,034 | inventory | TBD — GenericProtocolUnificationTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericTypeEmitterTests.cs` | 1,257 | inventory | TBD — GenericTypeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/GetCallArgumentStringTests.cs` | 225 | inventory | TBD — GetCallArgumentStringTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/InternalTypeReferenceWalkerTests.cs` | 386 | inventory | TBD — InternalTypeReferenceWalkerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/KeyPathBagWalkerAvailabilityTests.cs` | 193 | inventory | TBD — KeyPathBagWalkerAvailabilityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MainActorEmitterTests.cs` | 197 | inventory | TBD — MainActorEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MarkerProtocolOverloadEmitterTests.cs` | 416 | inventory | TBD — MarkerProtocolOverloadEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MarshalPlanRendererTests.cs` | 173 | inventory | TBD — MarshalPlanRendererTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MemberEmissionValidatorTests.cs` | 1,096 | inventory | TBD — MemberEmissionValidatorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MemberGateEvaluatorTests.cs` | 1,103 | inventory | TBD — MemberGateEvaluatorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MemberValidationPipelineTests.cs` | 2,355 | inventory | TBD — MemberValidationPipelineTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MetadataWrapperEmitterTests.cs` | 348 | inventory | TBD — MetadataWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MetatypeArrayBridgeEmitterTests.cs` | 256 | inventory | TBD — MetatypeArrayBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MetatypeHelperEmitterTests.cs` | 836 | inventory | TBD — MetatypeHelperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodClosureBridgeTests.cs` | 2,599 | inventory | TBD — MethodClosureBridgeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodEmissionSymbolSideTableTests.cs` | 155 | inventory | TBD — MethodEmissionSymbolSideTableTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodGenericBridgeEmitterTests.cs` | 797 | inventory | TBD — MethodGenericBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodHandlerOutputTests.cs` | 1,866 | inventory | TBD — MethodHandlerOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodWrapperClosureTests.cs` | 732 | inventory | TBD — MethodWrapperClosureTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodWrapperEmitterTests.cs` | 5,000 | inventory | TBD — MethodWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleDatabaseEmitterTests.cs` | 903 | inventory | TBD — ModuleDatabaseEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleEmissionContextCollisionTests.cs` | 101 | inventory | TBD — ModuleEmissionContextCollisionTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleEmissionContextOpenGenericTrackingTests.cs` | 107 | inventory | TBD — ModuleEmissionContextOpenGenericTrackingTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleFileSplitterTests.cs` | 216 | inventory | TBD — ModuleFileSplitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleHandlerTests.cs` | 2,616 | inventory | TBD — ModuleHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NamespaceFacadeDetectorTests.cs` | 330 | inventory | TBD — NamespaceFacadeDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NamespaceFacadeEmitterTests.cs` | 263 | inventory | TBD — NamespaceFacadeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NativeIntOverloadEmitterTests.cs` | 850 | inventory | TBD — NativeIntOverloadEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NativeThunkEmitterTests.cs` | 2,542 | inventory | TBD — NativeThunkEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NestedClosureBridgeTests.cs` | 1,427 | inventory | TBD — NestedClosureBridgeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NestedTypeRenameTests.cs` | 972 | inventory | TBD — NestedTypeRenameTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/NonFrozenStructHandlerTests.cs` | 487 | inventory | TBD — NonFrozenStructHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ObjCExistentialFailClosedTests.cs` | 537 | inventory | TBD — ObjCExistentialFailClosedTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ObjCOverridePropertyWrapperEmitterTests.cs` | 454 | inventory | TBD — ObjCOverridePropertyWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ObjCRootedInheritedPropertyDriftTests.cs` | 218 | inventory | TBD — ObjCRootedInheritedPropertyDriftTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerOutputTests.cs` | 738 | inventory | TBD — OperatorHandlerOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs` | 444 | inventory | TBD — OperatorHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OptionalMarshalStrategyTests.cs` | 870 | inventory | TBD — OptionalMarshalStrategyTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OptionalPointerWrapperTests.cs` | 1,998 | inventory | TBD — OptionalPointerWrapperTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OptionalReferenceClassifierTests.cs` | 229 | inventory | TBD — OptionalReferenceClassifierTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ParameterSignatureTests.cs` | 337 | inventory | TBD — ParameterSignatureTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeEmitterTests.cs` | 1,693 | inventory | TBD — PInvokeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeHelperEmitterTests.cs` | 1,538 | inventory | TBD — PInvokeHelperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PostProcessorTableTests.cs` | 90 | inventory | TBD — PostProcessorTableTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerSkipReportingTests.cs` | 523 | inventory | TBD — PropertyHandlerSkipReportingTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerTests.cs` | 2,398 | inventory | TBD — PropertyHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyWrapperEmitterTests.cs` | 2,935 | inventory | TBD — PropertyWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolConformanceCacheTests.cs` | 431 | inventory | TBD — ProtocolConformanceCacheTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolConformanceValidatorTests.cs` | 3,058 | inventory | TBD — ProtocolConformanceValidatorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionArrayParamTests.cs` | 176 | inventory | TBD — ProtocolExtensionArrayParamTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionClosureBridgeTests.cs` | 530 | inventory | TBD — ProtocolExtensionClosureBridgeTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionDataParamTests.cs` | 92 | inventory | TBD — ProtocolExtensionDataParamTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionDefaultsIndexTests.cs` | 791 | inventory | TBD — ProtocolExtensionDefaultsIndexTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionEmitterTests.cs` | 584 | inventory | TBD — ProtocolExtensionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionExistentialParamTests.cs` | 456 | inventory | TBD — ProtocolExtensionExistentialParamTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionExistentialReturnTests.cs` | 943 | inventory | TBD — ProtocolExtensionExistentialReturnTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionPrimitiveReturnTests.cs` | 74 | inventory | TBD — ProtocolExtensionPrimitiveReturnTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionStructConformerTests.cs` | 743 | inventory | TBD — ProtocolExtensionStructConformerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionTestHelpers.cs` | 166 | inventory | TBD — ProtocolExtensionTestHelpers |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolExtensionThrowingTests.cs` | 198 | inventory | TBD — ProtocolExtensionThrowingTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolHandlerOutputTests.cs` | 5,015 | inventory | TBD — ProtocolHandlerOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolHandlerTests.cs` | 997 | inventory | TBD — ProtocolHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmissionPolicyTests.cs` | 141 | inventory | TBD — ProtocolProxyEmissionPolicyTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` | 7,332 | inventory | TBD — ProtocolProxyEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolSignatureHelperTests.cs` | 1,463 | inventory | TBD — ProtocolSignatureHelperTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolVtableMembersInvariantTests.cs` | 180 | inventory | TBD — ProtocolVtableMembersInvariantTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/RealityFrameworkRemapFixTests.cs` | 824 | inventory | TBD — RealityFrameworkRemapFixTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ReceiverConversionVisitorTests.cs` | 342 | inventory | TBD — ReceiverConversionVisitorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SelfReconstructionEmitterTests.cs` | 111 | inventory | TBD — SelfReconstructionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SignatureBuilderTests.cs` | 1,294 | inventory | TBD — SignatureBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SilentTombstoneRegistrarTests.cs` | 399 | inventory | TBD — SilentTombstoneRegistrarTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SilgenNameTrampolineTests.cs` | 1,441 | inventory | TBD — SilgenNameTrampolineTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SpiMemberFilteringTests.cs` | 296 | inventory | TBD — SpiMemberFilteringTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/StringByValueFastPathEmitterTests.cs` | 363 | inventory | TBD — StringByValueFastPathEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/StringReturnEmitterTests.cs` | 125 | inventory | TBD — StringReturnEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SubclassClosedParentTrampolineEmitterTests.cs` | 426 | inventory | TBD — SubclassClosedParentTrampolineEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SubscriptHandlerProjectionTests.cs` | 334 | inventory | TBD — SubscriptHandlerProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SubscriptWrapperEmitterTests.cs` | 1,305 | inventory | TBD — SubscriptWrapperEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SuppressedProxyPoisonSurfaceTests.cs` | 81 | inventory | TBD — SuppressedProxyPoisonSurfaceTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftBuilderTests.cs` | 249 | inventory | TBD — SwiftBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftErrorMintEmitterTests.cs` | 413 | inventory | TBD — SwiftErrorMintEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftTypeNameHelperTests.cs` | 450 | inventory | TBD — SwiftTypeNameHelperTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftUIBridgeEmitterTests.cs` | 10,715 | inventory | TBD — SwiftUIBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftUIViewDetectorTests.cs` | 326 | inventory | TBD — SwiftUIViewDetectorTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SysVThunkTargetTests.cs` | 668 | inventory | TBD — SysVThunkTargetTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThemeBridgeEmitterTests.cs` | 1,615 | inventory | TBD — ThemeBridgeEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThirdPartyValidationFixTests.cs` | 1,016 | inventory | TBD — ThirdPartyValidationFixTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThirdPartyValidationFixTestsV3.cs` | 1,521 | inventory | TBD — ThirdPartyValidationFixTestsV3 |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThirdPartyValidationFixTestsV4.cs` | 1,822 | inventory | TBD — ThirdPartyValidationFixTestsV4 |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThrowingClosureSimplificationTests.cs` | 303 | inventory | TBD — ThrowingClosureSimplificationTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThunkAssemblyEmitterTests.cs` | 1,361 | inventory | TBD — ThunkAssemblyEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/Tier2LibraryFixTests.cs` | 973 | inventory | TBD — Tier2LibraryFixTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TrimmerDescriptorEmitterTests.cs` | 173 | inventory | TBD — TrimmerDescriptorEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TupleClassElementParamEmitterTests.cs` | 279 | inventory | TBD — TupleClassElementParamEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TupleDateElementEmitterTests.cs` | 144 | inventory | TBD — TupleDateElementEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypedThrowsEmitterTests.cs` | 774 | inventory | TBD — TypedThrowsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlerHelpersTests.cs` | 2,337 | inventory | TBD — TypeHandlerHelpersTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlersOutputTests.cs` | 1,464 | inventory | TBD — TypeHandlersOutputTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeLoweringTests.cs` | 1,185 | inventory | TBD — TypeLoweringTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeSkipPrePassTests.cs` | 231 | inventory | TBD — TypeSkipPrePassTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/UcoGuardEmitterTests.cs` | 317 | inventory | TBD — UcoGuardEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/UnsupportedCommentEmitterTests.cs` | 177 | inventory | TBD — UnsupportedCommentEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/UnsupportedSwiftTypeSupportTests.cs` | 239 | inventory | TBD — UnsupportedSwiftTypeSupportTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/Utf8SliceEmitterTests.cs` | 214 | inventory | TBD — Utf8SliceEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ValidationResultTests.cs` | 174 | inventory | TBD — ValidationResultTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ValidationRuleSetClassificationTests.cs` | 215 | inventory | TBD — ValidationRuleSetClassificationTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/VtableLayoutBuilderTests.cs` | 565 | inventory | TBD — VtableLayoutBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WasEmittedAssignmentCountTests.cs` | 128 | inventory | TBD — WasEmittedAssignmentCountTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WitnessDispatchEmitterTests.cs` | 2,780 | inventory | TBD — WitnessDispatchEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperConsistencyTests.cs` | 2,285 | inventory | TBD — WrapperConsistencyTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperDedupTests.cs` | 371 | inventory | TBD — WrapperDedupTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperEmitterHelpersTests.cs` | 190 | inventory | TBD — WrapperEmitterHelpersTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperEmitterReturnTests.cs` | 1,094 | inventory | TBD — WrapperEmitterReturnTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperProjectionParityTests.cs` | 476 | inventory | TBD — WrapperProjectionParityTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperSymbolCanonicalizationTests.cs` | 323 | inventory | TBD — WrapperSymbolCanonicalizationTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperSymbolContractTests.cs` | 780 | inventory | TBD — WrapperSymbolContractTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/WrapperValidationTests.cs` | 389 | inventory | TBD — WrapperValidationTests |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/XmlDocCommentEmitterTests.cs` | 423 | inventory | TBD — XmlDocCommentEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/Issue1SkipAttributionTests.cs` | 255 | inventory | TBD — Issue1SkipAttributionTests |
| `src/Swift.Bindings/tests/UnitTests/JsonlConsoleRecoveryTests.cs` | 177 | inventory | TBD — JsonlConsoleRecoveryTests |
| `src/Swift.Bindings/tests/UnitTests/MachOReaderTests.cs` | 301 | inventory | TBD — MachOReaderTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/AccessModifierTests.cs` | 51 | inventory | TBD — AccessModifierTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/AsyncCallbackSignatureTests.cs` | 394 | inventory | TBD — AsyncCallbackSignatureTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/AsyncSequenceHandlerTests.cs` | 219 | inventory | TBD — AsyncSequenceHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/AsyncStreamHandlerTests.cs` | 424 | inventory | TBD — AsyncStreamHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BaseHandlerDedupTests.cs` | 994 | inventory | TBD — BaseHandlerDedupTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BoundGenericsHandlerTests.cs` | 3,952 | inventory | TBD — BoundGenericsHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BoundGenericTranslationTests.cs` | 273 | inventory | TBD — BoundGenericTranslationTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureExistentialTests.cs` | 689 | inventory | TBD — ClosureExistentialTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` | 3,167 | inventory | TBD — ClosureHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/CollisionSuffixTests.cs` | 221 | inventory | TBD — CollisionSuffixTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ComplexProjectionTests.cs` | 1,698 | inventory | TBD — ComplexProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/CompositeProjectionTests.cs` | 732 | inventory | TBD — CompositeProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ConductorTests.cs` | 499 | inventory | TBD — ConductorTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ConformanceGraphResolutionTests.cs` | 391 | inventory | TBD — ConformanceGraphResolutionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ExistentialHandlerTests.cs` | 1,804 | inventory | TBD — ExistentialHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/GenericContextTests.cs` | 512 | inventory | TBD — GenericContextTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/IndirectResultDecompositionTests.cs` | 419 | inventory | TBD — IndirectResultDecompositionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/KeyPathProjectionTests.cs` | 230 | inventory | TBD — KeyPathProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/MarshalingContextTests.cs` | 167 | inventory | TBD — MarshalingContextTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/MarshalledTypeTests.cs` | 305 | inventory | TBD — MarshalledTypeTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/MarshallingHelpersTests.cs` | 951 | inventory | TBD — MarshallingHelpersTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/MarshalPlanRegressionTests.cs` | 1,018 | inventory | TBD — MarshalPlanRegressionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/MethodMarshalPlanBuilderTests.cs` | 1,949 | inventory | TBD — MethodMarshalPlanBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderMethodNamingTests.cs` | 588 | inventory | TBD — NameProviderMethodNamingTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderParameterTests.cs` | 540 | inventory | TBD — NameProviderParameterTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderRenameTests.cs` | 956 | inventory | TBD — NameProviderRenameTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderSanitizationTests.cs` | 421 | inventory | TBD — NameProviderSanitizationTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderSyntheticNameTests.cs` | 201 | inventory | TBD — NameProviderSyntheticNameTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/OptionalHandlerTests.cs` | 307 | inventory | TBD — OptionalHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ProjectionVisitorTests.cs` | 563 | inventory | TBD — ProjectionVisitorTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/PublicMethodNameContextTests.cs` | 265 | inventory | TBD — PublicMethodNameContextTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/RouteCSortShapeEligibilityTests.cs` | 286 | inventory | TBD — RouteCSortShapeEligibilityTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/SimpleProjectionTests.cs` | 530 | inventory | TBD — SimpleProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/SwiftDefaultValueMapperTests.cs` | 415 | inventory | TBD — SwiftDefaultValueMapperTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TupleHandlerTests.cs` | 1,049 | inventory | TBD — TupleHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TypeConversionHandlerTests.cs` | 226 | inventory | TBD — TypeConversionHandlerTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TypeProjectionConsistencyTests.cs` | 877 | inventory | TBD — TypeProjectionConsistencyTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TypeProjectionFactoryComplexTests.cs` | 730 | inventory | TBD — TypeProjectionFactoryComplexTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TypeProjectionFactoryTests.cs` | 1,030 | inventory | TBD — TypeProjectionFactoryTests |
| `src/Swift.Bindings/tests/UnitTests/MarshalerTests/UnderscorePrefixSuppressionTests.cs` | 267 | inventory | TBD — UnderscorePrefixSuppressionTests |
| `src/Swift.Bindings/tests/UnitTests/ModelTests/FrameworkDependencyInfoTests.cs` | 113 | inventory | TBD — FrameworkDependencyInfoTests |
| `src/Swift.Bindings/tests/UnitTests/ModelTests/MarkEmittedTests.cs` | 46 | inventory | TBD — MarkEmittedTests |
| `src/Swift.Bindings/tests/UnitTests/ModelTests/TypeRecordTests.cs` | 114 | inventory | TBD — TypeRecordTests |
| `src/Swift.Bindings/tests/UnitTests/MsBuildPropertyValueTests.cs` | 55 | inventory | TBD — MsBuildPropertyValueTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ApiDefinitionEmitterTests.cs` | 5,107 | inventory | Code emitter: ApiDefinitionEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ObjCAvailabilityEmitterTests.cs` | 164 | inventory | Code emitter: ObjCAvailabilityEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ObjCBindingProjectEmitterTests.cs` | 409 | inventory | Code emitter: ObjCBindingProjectEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ObjCMetadataPropsEmitterTests.cs` | 99 | inventory | Code emitter: ObjCMetadataPropsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ObjCTypeMapperTests.cs` | 1,410 | inventory | Code emitter: ObjCTypeMapperTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/ObjCUsingsEmitterTests.cs` | 344 | inventory | Code emitter: ObjCUsingsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/StructsAndEnumsEmitterTests.cs` | 2,284 | inventory | Code emitter: StructsAndEnumsEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Model/ObjCBindingDiagnosticsTests.cs` | 122 | inventory | IR model: ObjCBindingDiagnosticsTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/ObjCModuleBuilder.cs` | 331 | inventory | TBD — ObjCModuleBuilder |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/ObjCModuleBuilderTests.cs` | 114 | inventory | TBD — ObjCModuleBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/ObjCTestHelpers.cs` | 118 | inventory | TBD — ObjCTestHelpers |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/ObjCVariadicAndOutParamTests.cs` | 642 | inventory | TBD — ObjCVariadicAndOutParamTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstCensusTests.cs` | 162 | inventory | ABI/interface parser: ClangAstCensusTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstInvokerTests.cs` | 237 | inventory | ABI/interface parser: ClangAstInvokerTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstParserAvailabilityTests.cs` | 501 | inventory | ABI/interface parser: ClangAstParserAvailabilityTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ClangAstParserTests.cs` | 3,854 | inventory | ABI/interface parser: ClangAstParserTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ObjCAvailabilityParserTests.cs` | 282 | inventory | ABI/interface parser: ObjCAvailabilityParserTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Parser/ObjCTypeRefParserTests.cs` | 657 | inventory | ABI/interface parser: ObjCTypeRefParserTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/MixedFrameworkDedupTests.cs` | 321 | inventory | TBD — MixedFrameworkDedupTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/NativeSymbolGuardTests.cs` | 397 | inventory | TBD — NativeSymbolGuardTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/ObjCBridgeRecordFactoryTests.cs` | 374 | inventory | TBD — ObjCBridgeRecordFactoryTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/ObjCBridgeRecordRekeyerTests.cs` | 182 | inventory | TBD — ObjCBridgeRecordRekeyerTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/ObjCPipelineIntegrationTests.cs` | 676 | inventory | TBD — ObjCPipelineIntegrationTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/ObjCPipelinePostProcessTests.cs` | 698 | inventory | TBD — ObjCPipelinePostProcessTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/SwiftTypeNameCollectorTests.cs` | 136 | inventory | TBD — SwiftTypeNameCollectorTests |
| `src/Swift.Bindings/tests/UnitTests/ObjCTests/Pipeline/SwiftTypeOwnershipManifestTests.cs` | 402 | inventory | TBD — SwiftTypeOwnershipManifestTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/AbiIngestionContractTests.cs` | 494 | inventory | TBD — AbiIngestionContractTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ActorMetadataParserTests.cs` | 313 | inventory | TBD — ActorMetadataParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ClassHierarchyResolutionTests.cs` | 228 | inventory | TBD — ClassHierarchyResolutionTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ClassInheritanceParserTests.cs` | 415 | inventory | TBD — ClassInheritanceParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/DependentMemberParserTests.cs` | 168 | inventory | TBD — DependentMemberParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/EnumCasePayloadParserTests.cs` | 235 | inventory | TBD — EnumCasePayloadParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/EnumParserTests.cs` | 333 | inventory | TBD — EnumParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ForeignExtensionVisibilityTests.cs` | 241 | inventory | TBD — ForeignExtensionVisibilityTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/GenericSignatureParityTests.cs` | 472 | inventory | TBD — GenericSignatureParityTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/GenericSignatureParserTests.cs` | 434 | inventory | TBD — GenericSignatureParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/HandleConformanceRobustnessTests.cs` | 292 | inventory | TBD — HandleConformanceRobustnessTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/InterfaceFactKindAlignmentTests.cs` | 58 | inventory | TBD — InterfaceFactKindAlignmentTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ManglingProbesTests.cs` | 198 | inventory | TBD — ManglingProbesTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/MemberSignatureNormalizerTests.cs` | 219 | inventory | TBD — MemberSignatureNormalizerTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ModuleProcessorCycleTests.cs` | 406 | inventory | TBD — ModuleProcessorCycleTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ObjCImportedTypeNamesParserTests.cs` | 317 | inventory | TBD — ObjCImportedTypeNamesParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/OpaqueAndSendingDegradeTests.cs` | 178 | inventory | TBD — OpaqueAndSendingDegradeTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/OpaqueParameterSynthesisTests.cs` | 197 | inventory | TBD — OpaqueParameterSynthesisTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/OriginallyDefinedInConformanceTests.cs` | 270 | inventory | TBD — OriginallyDefinedInConformanceTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ProtocolParserTests.cs` | 398 | inventory | TBD — ProtocolParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ProtocolTbdDescriptorParserTests.cs` | 391 | inventory | TBD — ProtocolTbdDescriptorParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/ScreamingCasePascalCaseTests.cs` | 190 | inventory | TBD — ScreamingCasePascalCaseTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SimpleEnumDemotionTests.cs` | 339 | inventory | TBD — SimpleEnumDemotionTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SourceProvenanceTests.cs` | 180 | inventory | TBD — SourceProvenanceTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftABIParserRuntimeTests.cs` | 2,179 | inventory | TBD — SwiftABIParserRuntimeTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftABIParserTests.cs` | 1,556 | inventory | TBD — SwiftABIParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftInterfaceErrorRecoveryTests.cs` | 119 | inventory | TBD — SwiftInterfaceErrorRecoveryTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftInterfaceFactsTests.cs` | 643 | inventory | TBD — SwiftInterfaceFactsTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftSyntaxInterfaceFactsProducerTests.cs` | 647 | inventory | TBD — SwiftSyntaxInterfaceFactsProducerTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftTypeListTextTests.cs` | 127 | inventory | TBD — SwiftTypeListTextTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/SymbolGraphDocParserTests.cs` | 346 | inventory | TBD — SymbolGraphDocParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/TypeNameAliasParserTests.cs` | 193 | inventory | TBD — TypeNameAliasParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/UmbrellaReExportTests.cs` | 240 | inventory | TBD — UmbrellaReExportTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/UnboundGenericsParserTests.cs` | 102 | inventory | TBD — UnboundGenericsParserTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/UnderscoreProtocolSynthesizerTests.cs` | 728 | inventory | TBD — UnderscoreProtocolSynthesizerTests |
| `src/Swift.Bindings/tests/UnitTests/ParserTests/VariadicTypeSpecCreationTests.cs` | 249 | inventory | TBD — VariadicTypeSpecCreationTests |
| `src/Swift.Bindings/tests/UnitTests/ProjectionCompletenessTests.cs` | 637 | inventory | TBD — ProjectionCompletenessTests |
| `src/Swift.Bindings/tests/UnitTests/ReleaseGatesManifestTests.cs` | 640 | inventory | TBD — ReleaseGatesManifestTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/EmissionReportEmitterTests.cs` | 359 | inventory | TBD — EmissionReportEmitterTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/InputResolutionReportTests.cs` | 99 | inventory | TBD — InputResolutionReportTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/MemberDiagnosticIdentityTests.cs` | 404 | inventory | TBD — MemberDiagnosticIdentityTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/ObjCSkipProjectionTests.cs` | 89 | inventory | TBD — ObjCSkipProjectionTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/ReportCollectorTests.cs` | 914 | inventory | TBD — ReportCollectorTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/SkipDispositionClassifierTests.cs` | 252 | inventory | TBD — SkipDispositionClassifierTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/SkipTriageBuilderTests.cs` | 149 | inventory | TBD — SkipTriageBuilderTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/SuppressedProxyReportingTests.cs` | 595 | inventory | TBD — SuppressedProxyReportingTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/TestModelFactory.cs` | 215 | inventory | TBD — TestModelFactory |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/WorkaroundRecommendationsTests.cs` | 93 | inventory | TBD — WorkaroundRecommendationsTests |
| `src/Swift.Bindings/tests/UnitTests/ReportingTests/WrapperSymbolIntegrityGateTests.cs` | 144 | inventory | TBD — WrapperSymbolIntegrityGateTests |
| `src/Swift.Bindings/tests/UnitTests/RuntimeIdentityBaselineTests.cs` | 276 | inventory | TBD — RuntimeIdentityBaselineTests |
| `src/Swift.Bindings/tests/UnitTests/RuntimeTests/SwiftOptionalSpanSizeTests.cs` | 223 | inventory | TBD — SwiftOptionalSpanSizeTests |
| `src/Swift.Bindings/tests/UnitTests/RuntimeTests/SymbolicReferenceGrammarTests.cs` | 137 | inventory | TBD — SymbolicReferenceGrammarTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/AppleFrameworkCrossModuleDepsInjectionTests.cs` | 735 | inventory | TBD — AppleFrameworkCrossModuleDepsInjectionTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/AppleSupplementCsprojTests.cs` | 85 | inventory | TBD — AppleSupplementCsprojTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/AppleSupplementPlatformAttributeTests.cs` | 145 | inventory | TBD — AppleSupplementPlatformAttributeTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/CompileSmokeTests.cs` | 609 | inventory | TBD — CompileSmokeTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/RuntimeRangeRestoreTests.cs` | 244 | inventory | TBD — RuntimeRangeRestoreTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkPropsTargetsTests.cs` | 2,067 | inventory | TBD — SdkPropsTargetsTests |
| `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkTargetsBehaviorTests.cs` | 4,381 | inventory | TBD — SdkTargetsBehaviorTests |
| `src/Swift.Bindings/tests/UnitTests/SplitModuleSource.cs` | 40 | inventory | TBD — SplitModuleSource |
| `src/Swift.Bindings/tests/UnitTests/StdlibConformancesTests/StdlibConformancesRegenCommandTests.cs` | 125 | inventory | TBD — StdlibConformancesRegenCommandTests |
| `src/Swift.Bindings/tests/UnitTests/TbdParserTests/TbdParserTests.cs` | 539 | inventory | TBD — TbdParserTests |
| `src/Swift.Bindings/tests/UnitTests/TestDecls.cs` | 242 | inventory | TBD — TestDecls |
| `src/Swift.Bindings/tests/UnitTests/TestDiscoveryDiagnosticsTests.cs` | 210 | inventory | Test discovery source generator: TestDiscoveryDiagnosticsTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/AppleFrameworkRegistryTests.cs` | 1,617 | inventory | TBD — AppleFrameworkRegistryTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/ConformanceGraphTests.cs` | 103 | inventory | TBD — ConformanceGraphTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/RegistryContractTests.cs` | 283 | inventory | TBD — RegistryContractTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/Resolver/ResolverParityTests.cs` | 439 | inventory | TBD — ResolverParityTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/Resolver/StrategyTests.cs` | 519 | inventory | TBD — StrategyTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/Resolver/TypeResolverTests.cs` | 405 | inventory | TBD — TypeResolverTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/SuperclassModuleDatabaseTests.cs` | 128 | inventory | TBD — SuperclassModuleDatabaseTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/SwiftValueLayoutTests.cs` | 496 | inventory | TBD — SwiftValueLayoutTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/TypeDatabaseExtensionsTests.cs` | 2,420 | inventory | TBD — TypeDatabaseExtensionsTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/TypeDatabaseTests.cs` | 1,358 | inventory | TBD — TypeDatabaseTests |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/TypeOwnerRegistryTests.cs` | 495 | inventory | TBD — TypeOwnerRegistryTests |
| `src/Swift.Bindings/tests/UnitTests/TypeNameTests/CSharpTypeNameTests.cs` | 79 | inventory | TBD — CSharpTypeNameTests |
| `src/Swift.Bindings/tests/UnitTests/TypeNameTests/SwiftTypeNameTests.cs` | 71 | inventory | TBD — SwiftTypeNameTests |
| `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/AssociatedTypeReferenceSpecTests.cs` | 97 | inventory | TBD — AssociatedTypeReferenceSpecTests |
| `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/RawValueTypeNameNormalizationTests.cs` | 105 | inventory | TBD — RawValueTypeNameNormalizationTests |
| `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecHelpersTests.cs` | 101 | inventory | TBD — TypeSpecHelpersTests |
| `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecParserTests.cs` | 586 | inventory | TBD — TypeSpecParserTests |
| `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecTests.cs` | 297 | inventory | TBD — TypeSpecTests |

## src/Swift.Runtime/src (runtime library, *.cs)

**Files**: 100  
**LOC**: 20,709  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Runtime/src/Swift/AnyHashable.cs` | 54 | inventory | Runtime type/API: AnyHashable |
| `src/Swift.Runtime/src/Swift/AnyType.cs` | 99 | inventory | Runtime type/API: AnyType |
| `src/Swift.Runtime/src/Swift/CGPoint.cs` | 74 | inventory | Runtime type/API: CGPoint |
| `src/Swift.Runtime/src/Swift/CGRect.cs` | 139 | inventory | Runtime type/API: CGRect |
| `src/Swift.Runtime/src/Swift/CGSize.cs` | 74 | inventory | Runtime type/API: CGSize |
| `src/Swift.Runtime/src/Swift/DispatchQueue.cs` | 131 | inventory | Runtime type/API: DispatchQueue |
| `src/Swift.Runtime/src/Swift/Hasher.cs` | 126 | inventory | Runtime type/API: Hasher |
| `src/Swift.Runtime/src/Swift/ISwiftEncoder.cs` | 14 | inventory | Runtime type/API: ISwiftEncoder |
| `src/Swift.Runtime/src/Swift/KeyValueObserving.cs` | 87 | inventory | Runtime type/API: KeyValueObserving |
| `src/Swift.Runtime/src/Swift/OpaqueSwiftTypeAttribute.cs` | 27 | inventory | Runtime type/API: OpaqueSwiftTypeAttribute |
| `src/Swift.Runtime/src/Swift/OriginalSwiftTypeAttribute.cs` | 29 | inventory | Runtime type/API: OriginalSwiftTypeAttribute |
| `src/Swift.Runtime/src/Swift/Runtime/Arc.cs` | 419 | inventory | Runtime core: Arc |
| `src/Swift.Runtime/src/Swift/Runtime/AsyncClosureHelper.cs` | 523 | inventory | Runtime core: AsyncClosureHelper |
| `src/Swift.Runtime/src/Swift/Runtime/AsyncClosureState.cs` | 46 | inventory | Runtime core: AsyncClosureState |
| `src/Swift.Runtime/src/Swift/Runtime/AsyncHelpers.cs` | 377 | inventory | Runtime core: AsyncHelpers |
| `src/Swift.Runtime/src/Swift/Runtime/AsyncThrowingClosureState.cs` | 102 | inventory | Runtime core: AsyncThrowingClosureState |
| `src/Swift.Runtime/src/Swift/Runtime/ClosureHandle.cs` | 150 | inventory | Runtime core: ClosureHandle |
| `src/Swift.Runtime/src/Swift/Runtime/ComparableConformanceRegistry.cs` | 62 | inventory | Runtime core: ComparableConformanceRegistry |
| `src/Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs` | 106 | inventory | Runtime core: EveryProtocol |
| `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs` | 1,699 | inventory | Runtime core: ExistentialContainer |
| `src/Swift.Runtime/src/Swift/Runtime/ExistentialUnion.cs` | 123 | inventory | Runtime core: ExistentialUnion |
| `src/Swift.Runtime/src/Swift/Runtime/HashableConformanceRegistry.cs` | 60 | inventory | Runtime core: HashableConformanceRegistry |
| `src/Swift.Runtime/src/Swift/Runtime/IExistentialBoxable.cs` | 22 | inventory | Runtime core: IExistentialBoxable |
| `src/Swift.Runtime/src/Swift/Runtime/IExistentialContainer.cs` | 67 | inventory | Runtime core: IExistentialContainer |
| `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` | 2,075 | inventory | Runtime core: SwiftMarshal |
| `src/Swift.Runtime/src/Swift/Runtime/IProtocolProxyImpl.cs` | 22 | inventory | Runtime core: IProtocolProxyImpl |
| `src/Swift.Runtime/src/Swift/Runtime/ISwiftExistentialConvertible.cs` | 22 | inventory | Runtime core: ISwiftExistentialConvertible |
| `src/Swift.Runtime/src/Swift/Runtime/ISwiftObject.cs` | 347 | inventory | Runtime core: ISwiftObject |
| `src/Swift.Runtime/src/Swift/Runtime/ISwiftStruct.cs` | 19 | inventory | Runtime core: ISwiftStruct |
| `src/Swift.Runtime/src/Swift/Runtime/ITypeMetadataCache.cs` | 31 | inventory | Runtime core: ITypeMetadataCache |
| `src/Swift.Runtime/src/Swift/Runtime/KnownLibraries.cs` | 23 | inventory | Runtime core: KnownLibraries |
| `src/Swift.Runtime/src/Swift/Runtime/MainActorGuard.cs` | 56 | inventory | Runtime core: MainActorGuard |
| `src/Swift.Runtime/src/Swift/Runtime/Marshalling/BlittableOptionalInt32.cs` | 20 | inventory | Runtime core: BlittableOptionalInt32 |
| `src/Swift.Runtime/src/Swift/Runtime/Marshalling/BlittableSwiftString.cs` | 18 | inventory | Runtime core: BlittableSwiftString |
| `src/Swift.Runtime/src/Swift/Runtime/Marshalling/SwiftOptionalInt32Marshaller.cs` | 32 | inventory | Runtime core: SwiftOptionalInt32Marshaller |
| `src/Swift.Runtime/src/Swift/Runtime/Marshalling/SwiftStringMarshaller.cs` | 58 | inventory | Runtime core: SwiftStringMarshaller |
| `src/Swift.Runtime/src/Swift/Runtime/Marshalling/Utf8Slice.cs` | 24 | inventory | Runtime core: Utf8Slice |
| `src/Swift.Runtime/src/Swift/Runtime/ObjCInterop.cs` | 68 | inventory | Runtime core: ObjCInterop |
| `src/Swift.Runtime/src/Swift/Runtime/PayloadConstructionSemantics.cs` | 50 | inventory | Runtime core: PayloadConstructionSemantics |
| `src/Swift.Runtime/src/Swift/Runtime/ProtocolConformanceDescriptor.cs` | 228 | inventory | Runtime core: ProtocolConformanceDescriptor |
| `src/Swift.Runtime/src/Swift/Runtime/ProtocolDescriptor.cs` | 119 | inventory | Runtime core: ProtocolDescriptor |
| `src/Swift.Runtime/src/Swift/Runtime/ProtocolWitnessTable.cs` | 203 | inventory | Runtime core: ProtocolWitnessTable |
| `src/Swift.Runtime/src/Swift/Runtime/ProxyLifetimeTracker.cs` | 253 | inventory | Runtime core: ProxyLifetimeTracker |
| `src/Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs` | 169 | inventory | Runtime core: RuntimeContract |
| `src/Swift.Runtime/src/Swift/Runtime/StringAsyncClosureHelper.cs` | 172 | inventory | Runtime core: StringAsyncClosureHelper |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs` | 138 | inventory | Runtime core: SwiftClassHandle |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftClosure.cs` | 514 | inventory | Runtime core: SwiftClosure |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftClosureContext.cs` | 161 | inventory | Runtime core: SwiftClosureContext |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftCollectionCdeclWrappers.cs` | 123 | inventory | Runtime core: SwiftCollectionCdeclWrappers |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftConcurrency.cs` | 117 | inventory | Runtime core: SwiftConcurrency |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftConformance.cs` | 101 | inventory | Runtime core: SwiftConformance |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftDispose.cs` | 45 | inventory | Runtime core: SwiftDispose |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftDisposeScope.cs` | 99 | inventory | Runtime core: SwiftDisposeScope |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftDisposeScopeExtensions.cs` | 30 | inventory | Runtime core: SwiftDisposeScopeExtensions |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftException.cs` | 130 | inventory | Runtime core: SwiftException |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftExitGuard.cs` | 72 | inventory | Runtime core: SwiftExitGuard |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftFrameworkResolver.cs` | 417 | inventory | Runtime core: SwiftFrameworkResolver |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs` | 404 | inventory | Runtime core: SwiftHandle |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftLeakCensus.cs` | 68 | inventory | Runtime core: SwiftLeakCensus |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftMainActorAttribute.cs` | 26 | inventory | Runtime core: SwiftMainActorAttribute |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftMetadata.cs` | 413 | inventory | Runtime core: SwiftMetadata |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs` | 202 | inventory | Runtime core: SwiftObjectRegistry |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftRuntimeException.cs` | 19 | inventory | Runtime core: SwiftRuntimeException |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftRuntimeInfo.cs` | 223 | inventory | Runtime core: SwiftRuntimeInfo |
| `src/Swift.Runtime/src/Swift/Runtime/SymbolicReferenceGrammar.cs` | 102 | inventory | Runtime core: SymbolicReferenceGrammar |
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` | 1,193 | inventory | Runtime core: TypeMetadata |
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadataCache.cs` | 70 | inventory | Runtime core: TypeMetadataCache |
| `src/Swift.Runtime/src/Swift/Runtime/UnownedSerialExecutor.cs` | 53 | inventory | Runtime core: UnownedSerialExecutor |
| `src/Swift.Runtime/src/Swift/Runtime/ValueWitnessTable.cs` | 157 | inventory | Runtime core: ValueWitnessTable |
| `src/Swift.Runtime/src/Swift/Runtime/WeakSwiftReference.cs` | 49 | inventory | Runtime core: WeakSwiftReference |
| `src/Swift.Runtime/src/Swift/RuntimeLimitations.cs` | 147 | inventory | Runtime type/API: RuntimeLimitations |
| `src/Swift.Runtime/src/Swift/SwiftArray.cs` | 763 | inventory | Runtime type/API: SwiftArray |
| `src/Swift.Runtime/src/Swift/SwiftArrayProjection.cs` | 51 | inventory | Runtime type/API: SwiftArrayProjection |
| `src/Swift.Runtime/src/Swift/SwiftAsyncStream.cs` | 413 | inventory | Runtime type/API: SwiftAsyncStream |
| `src/Swift.Runtime/src/Swift/SwiftClosedRange.cs` | 298 | inventory | Runtime type/API: SwiftClosedRange |
| `src/Swift.Runtime/src/Swift/SwiftColor.cs` | 42 | inventory | Runtime type/API: SwiftColor |
| `src/Swift.Runtime/src/Swift/SwiftDictionary.cs` | 847 | inventory | Runtime type/API: SwiftDictionary |
| `src/Swift.Runtime/src/Swift/SwiftDictionaryProjection.cs` | 148 | inventory | Runtime type/API: SwiftDictionaryProjection |
| `src/Swift.Runtime/src/Swift/SwiftEquatable.cs` | 60 | inventory | Runtime type/API: SwiftEquatable |
| `src/Swift.Runtime/src/Swift/SwiftErrorException.cs` | 30 | inventory | Runtime type/API: SwiftErrorException |
| `src/Swift.Runtime/src/Swift/SwiftFont.cs` | 108 | inventory | Runtime type/API: SwiftFont |
| `src/Swift.Runtime/src/Swift/SwiftHashable.cs` | 184 | inventory | Runtime type/API: SwiftHashable |
| `src/Swift.Runtime/src/Swift/SwiftKeyPath.cs` | 398 | inventory | Runtime type/API: SwiftKeyPath |
| `src/Swift.Runtime/src/Swift/SwiftOptional.cs` | 679 | inventory | Runtime type/API: SwiftOptional |
| `src/Swift.Runtime/src/Swift/SwiftResult.cs` | 567 | inventory | Runtime type/API: SwiftResult |
| `src/Swift.Runtime/src/Swift/SwiftSendableAttribute.cs` | 22 | inventory | Runtime type/API: SwiftSendableAttribute |
| `src/Swift.Runtime/src/Swift/SwiftSet.cs` | 959 | inventory | Runtime type/API: SwiftSet |
| `src/Swift.Runtime/src/Swift/SwiftString.cs` | 455 | inventory | Runtime type/API: SwiftString |
| `src/Swift.Runtime/src/Swift/SwiftUI/Animation.cs` | 113 | inventory | SwiftUI runtime stub: Animation |
| `src/Swift.Runtime/src/Swift/SwiftUI/AnyView.cs` | 113 | inventory | SwiftUI runtime stub: AnyView |
| `src/Swift.Runtime/src/Swift/SwiftUI/Binding.cs` | 43 | inventory | SwiftUI runtime stub: Binding |
| `src/Swift.Runtime/src/Swift/SwiftUI/Color.cs` | 113 | inventory | SwiftUI runtime stub: Color |
| `src/Swift.Runtime/src/Swift/SwiftUI/EdgeInsets.cs` | 113 | inventory | SwiftUI runtime stub: EdgeInsets |
| `src/Swift.Runtime/src/Swift/SwiftUI/Font.cs` | 113 | inventory | SwiftUI runtime stub: Font |
| `src/Swift.Runtime/src/Swift/SwiftUI/Image.cs` | 113 | inventory | SwiftUI runtime stub: Image |
| `src/Swift.Runtime/src/Swift/SwiftVoid.cs` | 17 | inventory | Runtime type/API: SwiftVoid |
| `src/Swift.Runtime/src/Swift/UnsafeBufferPointer.cs` | 75 | inventory | Runtime type/API: UnsafeBufferPointer |
| `src/Swift.Runtime/src/Swift/UnsafePointer.cs` | 79 | inventory | Runtime type/API: UnsafePointer |
| `src/Swift.Runtime/src/Swift/UnsupportedSwiftTypeAttribute.cs` | 38 | inventory | Runtime type/API: UnsupportedSwiftTypeAttribute |
| `src/Swift.Runtime/src/Util/DynamicLibraryLoader.cs` | 63 | inventory | Runtime type/API: DynamicLibraryLoader |

## src/Swift.Runtime/tests (runtime tests, *.cs)

**Files**: 44  
**LOC**: 9,735  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Runtime/tests/LibraryTests/AsyncResumeGuardTests.cs` | 103 | inventory | Runtime unit test: AsyncResumeGuardTests |
| `src/Swift.Runtime/tests/LibraryTests/ClosureHandleTests.cs` | 160 | inventory | Runtime unit test: ClosureHandleTests |
| `src/Swift.Runtime/tests/LibraryTests/DisposeSafetyTests.cs` | 612 | inventory | Runtime unit test: DisposeSafetyTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftArrayProjectionTests.cs` | 128 | inventory | Runtime unit test: SwiftArrayProjectionTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftArrayTests.cs` | 467 | inventory | Runtime unit test: SwiftArrayTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftAsyncCallHolderTests.cs` | 238 | inventory | Runtime unit test: SwiftAsyncCallHolderTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftAsyncCancellationTests.cs` | 98 | inventory | Runtime unit test: SwiftAsyncCancellationTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftColorTests.cs` | 138 | inventory | Runtime unit test: SwiftColorTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftDictionaryTests.cs` | 377 | inventory | Runtime unit test: SwiftDictionaryTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftEquatableTests.cs` | 24 | inventory | Runtime unit test: SwiftEquatableTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftFontTests.cs` | 107 | inventory | Runtime unit test: SwiftFontTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftOptionalTests.cs` | 252 | inventory | Runtime unit test: SwiftOptionalTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftSetTests.cs` | 262 | inventory | Runtime unit test: SwiftSetTests |
| `src/Swift.Runtime/tests/LibraryTests/SwiftStringTests.cs` | 104 | inventory | Runtime unit test: SwiftStringTests |
| `src/Swift.Runtime/tests/LibraryTests/TestProtocols.cs` | 9 | inventory | Runtime unit test: TestProtocols |
| `src/Swift.Runtime/tests/MetadataTests/AotAnnotationTests.cs` | 190 | inventory | Runtime unit test: AotAnnotationTests |
| `src/Swift.Runtime/tests/MetadataTests/ArcTests.cs` | 76 | inventory | Runtime unit test: ArcTests |
| `src/Swift.Runtime/tests/MetadataTests/BorrowedMarshalFinalizerTests.cs` | 168 | inventory | Runtime unit test: BorrowedMarshalFinalizerTests |
| `src/Swift.Runtime/tests/MetadataTests/CacheFirstDispatchTests.cs` | 311 | inventory | Runtime unit test: CacheFirstDispatchTests |
| `src/Swift.Runtime/tests/MetadataTests/ExistentialContainerFactoryTests.cs` | 499 | inventory | Runtime unit test: ExistentialContainerFactoryTests |
| `src/Swift.Runtime/tests/MetadataTests/ExistentialMetadataWrapperTests.cs` | 76 | inventory | Runtime unit test: ExistentialMetadataWrapperTests |
| `src/Swift.Runtime/tests/MetadataTests/ExistentialUnionTests.cs` | 65 | inventory | Runtime unit test: ExistentialUnionTests |
| `src/Swift.Runtime/tests/MetadataTests/KnownMetadataTests.cs` | 65 | inventory | Runtime unit test: KnownMetadataTests |
| `src/Swift.Runtime/tests/MetadataTests/ObjCInteropTests.cs` | 77 | inventory | Runtime unit test: ObjCInteropTests |
| `src/Swift.Runtime/tests/MetadataTests/PayloadSemanticsDispatchTests.cs` | 134 | inventory | Runtime unit test: PayloadSemanticsDispatchTests |
| `src/Swift.Runtime/tests/MetadataTests/ProtocolConformanceDescriptorTests.cs` | 171 | inventory | Runtime unit test: ProtocolConformanceDescriptorTests |
| `src/Swift.Runtime/tests/MetadataTests/ProtocolWitnessTableTests.cs` | 140 | inventory | Runtime unit test: ProtocolWitnessTableTests |
| `src/Swift.Runtime/tests/MetadataTests/ProxyLifetimeTrackerTests.cs` | 298 | inventory | Runtime unit test: ProxyLifetimeTrackerTests |
| `src/Swift.Runtime/tests/MetadataTests/RuntimeContractTests.cs` | 126 | inventory | Runtime unit test: RuntimeContractTests |
| `src/Swift.Runtime/tests/MetadataTests/RuntimeLimitationsTests.cs` | 268 | inventory | Runtime unit test: RuntimeLimitationsTests |
| `src/Swift.Runtime/tests/MetadataTests/SafeHandleLifetimeHelpersTests.cs` | 198 | inventory | Runtime unit test: SafeHandleLifetimeHelpersTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftClassHandleTests.cs` | 739 | inventory | Runtime unit test: SwiftClassHandleTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftConformanceTests.cs` | 306 | inventory | Runtime unit test: SwiftConformanceTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftDisposeScopeTests.cs` | 460 | inventory | Runtime unit test: SwiftDisposeScopeTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftExitGuardCollection.cs` | 30 | inventory | Runtime unit test: SwiftExitGuardCollection |
| `src/Swift.Runtime/tests/MetadataTests/SwiftExitGuardTestScope.cs` | 54 | inventory | Runtime unit test: SwiftExitGuardTestScope |
| `src/Swift.Runtime/tests/MetadataTests/SwiftFrameworkResolverTests.cs` | 309 | inventory | Runtime unit test: SwiftFrameworkResolverTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftObjectRegistryTests.cs` | 339 | inventory | Runtime unit test: SwiftObjectRegistryTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftRuntimeInfoClassificationTests.cs` | 212 | inventory | Runtime unit test: SwiftRuntimeInfoClassificationTests |
| `src/Swift.Runtime/tests/MetadataTests/SwiftStringWrapperTests.cs` | 366 | inventory | Runtime unit test: SwiftStringWrapperTests |
| `src/Swift.Runtime/tests/MetadataTests/TestTypes.cs` | 90 | inventory | Runtime unit test: TestTypes |
| `src/Swift.Runtime/tests/MetadataTests/TupleTests.cs` | 446 | inventory | Runtime unit test: TupleTests |
| `src/Swift.Runtime/tests/MetadataTests/TypeMetadataTests.cs` | 403 | inventory | Runtime unit test: TypeMetadataTests |
| `src/Swift.Runtime/tests/MetadataTests/ValueWitnessFlagsTests.cs` | 40 | inventory | Runtime unit test: ValueWitnessFlagsTests |

## src/Swift.Runtime/swift (native runtime: *.swift, *.c, *.sh)

**Files**: 3  
**LOC**: 1,341  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Runtime/swift/build-runtime.sh` | 244 | inventory | Native runtime build script: build-runtime |
| `src/Swift.Runtime/swift/SwiftBindingsRuntime.swift` | 891 | inventory | Native runtime Swift: SwiftBindingsRuntime |
| `src/Swift.Runtime/swift/SwiftBindingsRuntimeCollections.c` | 206 | inventory | Native runtime C: SwiftBindingsRuntimeCollections |

## src/Swift.Bindings.Sdk (*.props, *.targets, *.cs, scripts)

**Files**: 10  
**LOC**: 5,160  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Bindings.Sdk/Sdk/scripts/compile-wrapper-locked.sh` | 148 | inventory | Build/CI script: compile-wrapper-locked.sh |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.props` | 147 | inventory | MSBuild SDK props |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` | 3,800 | inventory | MSBuild SDK targets (generate/compile/pack) |
| `src/Swift.Bindings.Sdk/Swift.Bindings.Sdk.csproj` | 47 | inventory | SDK artifact: Swift.Bindings.Sdk.csproj |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/include-types.json` | 20 | inventory | SDK artifact: include-types.json |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/manifest.json` | 601 | inventory | SDK artifact: manifest.json |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/README.md` | 59 | inventory | SDK artifact: README.md |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/regenerate.sh` | 102 | inventory | SDK script: regenerate.sh |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/schema.json` | 232 | inventory | SDK artifact: schema.json |
| `src/Swift.Bindings.Sdk/tools/apple-types-manifest/sequential-layout-whitelist.json` | 4 | inventory | SDK artifact: sequential-layout-whitelist.json |

## src/Swift.Bindings.Apple (*.cs, *.swift, *.targets — not bin/obj)

**Files**: 16  
**LOC**: 2,770  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Bindings.Apple/build/SwiftBindings.Apple.targets` | 44 | inventory | Apple package MSBuild targets |
| `src/Swift.Bindings.Apple/Shims/AppleSupplementProbe.swift` | 26 | inventory | Apple supplement Swift shim: AppleSupplementProbe |
| `src/Swift.Bindings.Apple/Shims/AttributedStringShims.swift` | 157 | inventory | Apple supplement Swift shim: AttributedStringShims |
| `src/Swift.Bindings.Apple/Shims/LiveActivityShims.swift` | 385 | inventory | Apple supplement Swift shim: LiveActivityShims |
| `src/Swift.Bindings.Apple/Sources/ActivityKit/LiveActivity.cs` | 385 | inventory | Apple supplement managed type: LiveActivity |
| `src/Swift.Bindings.Apple/Sources/AppleSupplementModuleInit.cs` | 46 | inventory | Apple supplement managed type: AppleSupplementModuleInit |
| `src/Swift.Bindings.Apple/Sources/Foundation/AnyError.cs` | 240 | inventory | Apple supplement managed type: AnyError |
| `src/Swift.Bindings.Apple/Sources/Foundation/AttributedString.cs` | 227 | inventory | Apple supplement managed type: AttributedString |
| `src/Swift.Bindings.Apple/Sources/Foundation/Data.cs` | 215 | inventory | Apple supplement managed type: Data |
| `src/Swift.Bindings.Apple/Sources/Foundation/DataAsyncClosureHelper.cs` | 62 | inventory | Apple supplement managed type: DataAsyncClosureHelper |
| `src/Swift.Bindings.Apple/Sources/Foundation/Measurement.cs` | 278 | inventory | Apple supplement managed type: Measurement |
| `src/Swift.Bindings.Apple/Sources/Foundation/URL.cs` | 97 | inventory | Apple supplement managed type: URL |
| `src/Swift.Bindings.Apple/Sources/Foundation/URLRequest.cs` | 97 | inventory | Apple supplement managed type: URLRequest |
| `src/Swift.Bindings.Apple/Sources/ManagedSettings/Token.cs` | 141 | inventory | Apple supplement managed type: Token |
| `src/Swift.Bindings.Apple/Sources/SwiftUI/Text.cs` | 181 | inventory | Apple supplement managed type: Text |
| `src/Swift.Bindings.Apple/Swift.Bindings.Apple.csproj` | 189 | inventory | Apple supplement managed type: Swift.Bindings.Apple |

## src/Swift.Analyzers (*.cs)

**Files**: 3  
**LOC**: 696  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Analyzers/SwiftObjectDisposeAnalyzer.cs` | 321 | inventory | Roslyn analyzer/test: SwiftObjectDisposeAnalyzer |
| `src/Swift.Analyzers/SwiftObjectDisposeCodeFixProvider.cs` | 120 | inventory | Roslyn analyzer/test: SwiftObjectDisposeCodeFixProvider |
| `src/Swift.Analyzers/SwiftRetainCycleAnalyzer.cs` | 255 | inventory | Roslyn analyzer/test: SwiftRetainCycleAnalyzer |

## src/Swift.Analyzers.Tests (*.cs)

**Files**: 3  
**LOC**: 965  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Analyzers.Tests/SwiftObjectDisposeAnalyzerTests.cs` | 557 | inventory | Roslyn analyzer/test: SwiftObjectDisposeAnalyzerTests |
| `src/Swift.Analyzers.Tests/SwiftObjectDisposeCodeFixTests.cs` | 117 | inventory | Roslyn analyzer/test: SwiftObjectDisposeCodeFixTests |
| `src/Swift.Analyzers.Tests/SwiftRetainCycleAnalyzerTests.cs` | 291 | inventory | Roslyn analyzer/test: SwiftRetainCycleAnalyzerTests |

## src/SwiftBindings.TestDiscovery (*.cs)

**Files**: 1  
**LOC**: 579  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/SwiftBindings.TestDiscovery/TestDiscoveryGenerator.cs` | 579 | inventory | Test discovery source generator: TestDiscoveryGenerator |

## src/Swift.Bindings.Templates (template content + project)

**Files**: 2  
**LOC**: 62  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `src/Swift.Bindings.Templates/content/swift-binding/ProjectName.csproj` | 16 | inventory | dotnet new template content: ProjectName.csproj |
| `src/Swift.Bindings.Templates/Swift.Bindings.Templates.csproj` | 46 | inventory | dotnet new template content: Swift.Bindings.Templates.csproj |

## build/ (Nuke targets, scripts, Helpers, Models, Tools)

**Files**: 65  
**LOC**: 26,234  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `build/_build.csproj` | 40 | inventory | Nuke build project |
| `build/Build.AbiGrid.cs` | 172 | inventory | Nuke target partial: AbiGrid |
| `build/Build.ApiManifestGate.cs` | 207 | inventory | Nuke target partial: ApiManifestGate |
| `build/Build.AppleSupplement.cs` | 237 | inventory | Nuke target partial: AppleSupplement |
| `build/Build.AppleTypesManifest.cs` | 60 | inventory | Nuke target partial: AppleTypesManifest |
| `build/Build.BehaviorTier.cs` | 405 | inventory | Nuke target partial: BehaviorTier |
| `build/Build.BindingTests.AppStoreHygiene.cs` | 550 | inventory | Nuke target partial: BindingTests.AppStoreHygiene |
| `build/Build.BindingTests.cs` | 1,239 | inventory | Nuke target partial: BindingTests |
| `build/Build.BindingTests.MixedDirect.cs` | 384 | inventory | Nuke target partial: BindingTests.MixedDirect |
| `build/Build.BindingTests.MixedPack.cs` | 627 | inventory | Nuke target partial: BindingTests.MixedPack |
| `build/Build.BindingTests.ObjCUmbrella.cs` | 191 | inventory | Nuke target partial: BindingTests.ObjCUmbrella |
| `build/Build.cs` | 133 | inventory | Nuke target partial:  |
| `build/Build.Pack.cs` | 346 | inventory | Nuke target partial: Pack |
| `build/Build.PackGate.cs` | 1,462 | inventory | Nuke target partial: PackGate |
| `build/Build.PackGate.MixedFixture.cs` | 1,405 | inventory | Nuke target partial: PackGate.MixedFixture |
| `build/Build.Parity.cs` | 335 | inventory | Nuke target partial: Parity |
| `build/Build.ReleaseGates.cs` | 337 | inventory | Nuke target partial: ReleaseGates |
| `build/Build.RuntimeTests.cs` | 3,462 | inventory | Nuke target partial: RuntimeTests |
| `build/Build.SkipSurface.cs` | 281 | inventory | Nuke target partial: SkipSurface |
| `build/Build.StdlibConformances.cs` | 100 | inventory | Nuke target partial: StdlibConformances |
| `build/Build.SwiftInterfaceParser.cs` | 258 | inventory | Nuke target partial: SwiftInterfaceParser |
| `build/Build.Test.cs` | 152 | inventory | Nuke target partial: Test |
| `build/Build.Validation.cs` | 2,546 | inventory | Nuke target partial: Validation |
| `build/Build.Validation.Fetch.cs` | 822 | inventory | Nuke target partial: Validation.Fetch |
| `build/Build.WindowsPathGuard.cs` | 132 | inventory | Nuke target partial: WindowsPathGuard |
| `build/Build.WrapperStrip.cs` | 312 | inventory | Nuke target partial: WrapperStrip |
| `build/Build.X64PackGate.cs` | 606 | inventory | Nuke target partial: X64PackGate |
| `build/Build.X64SimGate.cs` | 710 | inventory | Nuke target partial: X64SimGate |
| `build/Build.X64ThunkGate.cs` | 275 | inventory | Nuke target partial: X64ThunkGate |
| `build/Helpers/AbiGridReporter.cs` | 387 | inventory | Build helper: AbiGridReporter |
| `build/Helpers/ArtifactParityGate.cs` | 801 | inventory | Build helper: ArtifactParityGate |
| `build/Helpers/MachOReader.cs` | 175 | inventory | Build helper: MachOReader |
| `build/Helpers/PlistGenerator.cs` | 43 | inventory | Build helper: PlistGenerator |
| `build/Helpers/VersionScope.cs` | 189 | inventory | Build helper: VersionScope |
| `build/Models/AbiGridManifest.cs` | 169 | inventory | Build model/DTO: AbiGridManifest |
| `build/Models/ApiManifestBaseline.cs` | 119 | inventory | Build model/DTO: ApiManifestBaseline |
| `build/Models/ApplePlatform.cs` | 177 | inventory | Build model/DTO: ApplePlatform |
| `build/Models/JsonlTestResults.cs` | 284 | inventory | Build model/DTO: JsonlTestResults |
| `build/Models/MsBuildPropertyValue.cs` | 25 | inventory | Build model/DTO: MsBuildPropertyValue |
| `build/Models/ReleaseGatesManifest.cs` | 535 | inventory | Build model/DTO: ReleaseGatesManifest |
| `build/Models/RuntimeIdentityBaseline.cs` | 221 | inventory | Build model/DTO: RuntimeIdentityBaseline |
| `build/Models/SkipSurfaceBaseline.cs` | 130 | inventory | Build model/DTO: SkipSurfaceBaseline |
| `build/Models/TestClassInventory.cs` | 70 | inventory | Build model/DTO: TestClassInventory |
| `build/Models/TestResult.cs` | 29 | inventory | Build model/DTO: TestResult |
| `build/Models/ValidationBaseline.cs` | 175 | inventory | Build model/DTO: ValidationBaseline |
| `build/Models/ValidationManifest.cs` | 134 | inventory | Build model/DTO: ValidationManifest |
| `build/Models/WrapperStripManifest.cs` | 75 | inventory | Build model/DTO: WrapperStripManifest |
| `build/PackGate/HelloPack/HelloPack.swift` | 11 | inventory | TBD — HelloPack |
| `build/scripts/ci/ci_ios_test.py` | 583 | inventory | Build/CI script: ci_ios_test.py |
| `build/scripts/ci/sim_manager.py` | 687 | inventory | Build/CI script: sim_manager.py |
| `build/scripts/ci/test_sim_manager.py` | 403 | inventory | Build/CI script: test_sim_manager.py |
| `build/scripts/coverage-report.py` | 1,272 | inventory | Build/CI script: coverage-report.py |
| `build/scripts/skip-metrics.py` | 269 | inventory | Build/CI script: skip-metrics.py |
| `build/Tools/ArgumentEscaper.cs` | 29 | inventory | Build tool wrapper: ArgumentEscaper |
| `build/Tools/DeviceCtl.cs` | 263 | inventory | Build tool wrapper: DeviceCtl |
| `build/Tools/SimCtl.cs` | 416 | inventory | Build tool wrapper: SimCtl |
| `build/Tools/SwiftCompiler.cs` | 140 | inventory | Build tool wrapper: SwiftCompiler |
| `build/Tools/SwiftFrontend.cs` | 101 | inventory | Build tool wrapper: SwiftFrontend |
| `build/Tools/SymbolGraphExtract.cs` | 94 | inventory | Build tool wrapper: SymbolGraphExtract |
| `build/Tools/XcodeBuild.cs` | 191 | inventory | Build tool wrapper: XcodeBuild |
| `build/Tools/XcRun.cs` | 49 | inventory | Build tool wrapper: XcRun |
| `build/x64-thunk-gate/Driver/Driver.csproj` | 23 | inventory | TBD — Driver |
| `build/x64-thunk-gate/Driver/Program.cs` | 95 | inventory | TBD — Program |
| `build/x64-thunk-gate/Fixture.swift` | 61 | inventory | TBD — Fixture |
| `build/X64PackGate/Fixture/X64PackFixture.swift` | 23 | inventory | TBD — X64PackFixture |

## BindingTests/Sources (*.swift)

**Files**: 341  
**LOC**: 36,770  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppEntity.swift` | 225 | inventory | Swift BindingTests fixture (AppIntents): MockAppEntity |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/Actors.swift` | 206 | inventory | Swift BindingTests fixture (Async): Actors |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncCallbackClosures.swift` | 30 | inventory | Swift BindingTests fixture (Async): AsyncCallbackClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncClosures.swift` | 198 | inventory | Swift BindingTests fixture (Async): AsyncClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncClosureSpike.swift` | 147 | inventory | Swift BindingTests fixture (Async): AsyncClosureSpike |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncComplexTypes.swift` | 367 | inventory | Swift BindingTests fixture (Async): AsyncComplexTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncExistentialArray.swift` | 60 | inventory | Swift BindingTests fixture (Async): AsyncExistentialArray |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncFactoryMethods.swift` | 118 | inventory | Swift BindingTests fixture (Async): AsyncFactoryMethods |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncFrozenStructParams.swift` | 26 | inventory | Swift BindingTests fixture (Async): AsyncFrozenStructParams |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncGenericSequence.swift` | 150 | inventory | Swift BindingTests fixture (Async): AsyncGenericSequence |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncGenericTuples.swift` | 57 | inventory | Swift BindingTests fixture (Async): AsyncGenericTuples |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncInstanceFrozenStruct.swift` | 37 | inventory | Swift BindingTests fixture (Async): AsyncInstanceFrozenStruct |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncMethodGenericDefaults.swift` | 119 | inventory | Swift BindingTests fixture (Async): AsyncMethodGenericDefaults |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncNonFrozenStructArrayParams.swift` | 32 | inventory | Swift BindingTests fixture (Async): AsyncNonFrozenStructArrayParams |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncNonFrozenStructDictionaryParams.swift` | 24 | inventory | Swift BindingTests fixture (Async): AsyncNonFrozenStructDictionaryParams |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncOpaqueReturn.swift` | 64 | inventory | Swift BindingTests fixture (Async): AsyncOpaqueReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncProperties.swift` | 287 | inventory | Swift BindingTests fixture (Async): AsyncProperties |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncSequence.swift` | 235 | inventory | Swift BindingTests fixture (Async): AsyncSequence |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncSkipPolicyShapes.swift` | 56 | inventory | Swift BindingTests fixture (Async): AsyncSkipPolicyShapes |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncThrowing.swift` | 47 | inventory | Swift BindingTests fixture (Async): AsyncThrowing |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/CancellationRace.swift` | 99 | inventory | Swift BindingTests fixture (Async): CancellationRace |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/CustomGlobalActor.swift` | 189 | inventory | Swift BindingTests fixture (Async): CustomGlobalActor |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/IsolationControl.swift` | 43 | inventory | Swift BindingTests fixture (Async): IsolationControl |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/MainActor.swift` | 101 | inventory | Swift BindingTests fixture (Async): MainActor |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/Methods.swift` | 152 | inventory | Swift BindingTests fixture (Async): Methods |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/ObjCAsyncSelf.swift` | 46 | inventory | Swift BindingTests fixture (Async): ObjCAsyncSelf |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/Sendable.swift` | 101 | inventory | Swift BindingTests fixture (Async): Sendable |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/Autoclosures.swift` | 75 | inventory | Swift BindingTests fixture (Closures): Autoclosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/BufferPointerClosures.swift` | 79 | inventory | Swift BindingTests fixture (Closures): BufferPointerClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/CallbackArgProjection.swift` | 55 | inventory | Swift BindingTests fixture (Closures): CallbackArgProjection |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosedConstrainedClosure.swift` | 115 | inventory | Swift BindingTests fixture (Closures): ClosedConstrainedClosure |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosureParamTombstone.swift` | 76 | inventory | Swift BindingTests fixture (Closures): ClosureParamTombstone |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosureReturns.swift` | 164 | inventory | Swift BindingTests fixture (Closures): ClosureReturns |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosureReturnTypes.swift` | 42 | inventory | Swift BindingTests fixture (Closures): ClosureReturnTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/CompletionHandlers.swift` | 76 | inventory | Swift BindingTests fixture (Closures): CompletionHandlers |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ComplexEnumCompletionHeapOwnership.swift` | 82 | inventory | Swift BindingTests fixture (Closures): ComplexEnumCompletionHeapOwnership |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ConventionC.swift` | 92 | inventory | Swift BindingTests fixture (Closures): ConventionC |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/Escaping.swift` | 282 | inventory | Swift BindingTests fixture (Closures): Escaping |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/GenericClosureArgBridge.swift` | 40 | inventory | Swift BindingTests fixture (Closures): GenericClosureArgBridge |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/GenericClosureBridge.swift` | 171 | inventory | Swift BindingTests fixture (Closures): GenericClosureBridge |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/NestedClosureBridge.swift` | 63 | inventory | Swift BindingTests fixture (Closures): NestedClosureBridge |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/OptionalReferenceClosureArbiter.swift` | 99 | inventory | Swift BindingTests fixture (Closures): OptionalReferenceClosureArbiter |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/OptionalThrowingVoidClosures.swift` | 154 | inventory | Swift BindingTests fixture (Closures): OptionalThrowingVoidClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/PointerArgRenderHandler.swift` | 29 | inventory | Swift BindingTests fixture (Closures): PointerArgRenderHandler |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/StructClosureBridge.swift` | 68 | inventory | Swift BindingTests fixture (Closures): StructClosureBridge |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/SyntheticNameCollisionBridge.swift` | 55 | inventory | Swift BindingTests fixture (Closures): SyntheticNameCollisionBridge |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/ThrowingClosures.swift` | 110 | inventory | Swift BindingTests fixture (Closures): ThrowingClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Closures/UnsupportedClosureShapes.swift` | 136 | inventory | Swift BindingTests fixture (Closures): UnsupportedClosureShapes |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/ArrayOperations.swift` | 80 | inventory | Swift BindingTests fixture (Collections): ArrayOperations |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/ArraySliceOperations.swift` | 60 | inventory | Swift BindingTests fixture (Collections): ArraySliceOperations |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/ClosedRangeOperations.swift` | 126 | inventory | Swift BindingTests fixture (Collections): ClosedRangeOperations |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/ConstructorCollections.swift` | 49 | inventory | Swift BindingTests fixture (Collections): ConstructorCollections |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/DictionaryAny.swift` | 96 | inventory | Swift BindingTests fixture (Collections): DictionaryAny |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/DictionaryConstructor.swift` | 34 | inventory | Swift BindingTests fixture (Collections): DictionaryConstructor |
| `BindingTests/Sources/SwiftBindingsTestLib/Collections/EnumArrays.swift` | 25 | inventory | Swift BindingTests fixture (Collections): EnumArrays |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/AsyncPropertyMethodCollision.swift` | 28 | inventory | Swift BindingTests fixture (Collisions): AsyncPropertyMethodCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/CaseCollisions.swift` | 63 | inventory | Swift BindingTests fixture (Collisions): CaseCollisions |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/ClosureOverloadCollisions.swift` | 35 | inventory | Swift BindingTests fixture (Collisions): ClosureOverloadCollisions |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/ContainerElementOverloadCollisions.swift` | 75 | inventory | Swift BindingTests fixture (Collisions): ContainerElementOverloadCollisions |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/DefaultParamCollisions.swift` | 34 | inventory | Swift BindingTests fixture (Collisions): DefaultParamCollisions |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/EmojiIdentifiers.swift` | 35 | inventory | Swift BindingTests fixture (Collisions): EmojiIdentifiers |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/FailableInitLabelCollision.swift` | 41 | inventory | Swift BindingTests fixture (Collisions): FailableInitLabelCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/GenericArityOverloadCollision.swift` | 64 | inventory | Swift BindingTests fixture (Collisions): GenericArityOverloadCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/NestedTypeFlattening.swift` | 98 | inventory | Swift BindingTests fixture (Collisions): NestedTypeFlattening |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/NestedTypeMethodCollision.swift` | 70 | inventory | Swift BindingTests fixture (Collisions): NestedTypeMethodCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/NonAsciiIdentifiers.swift` | 50 | inventory | Swift BindingTests fixture (Collisions): NonAsciiIdentifiers |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/NullableRefOverrideCollision.swift` | 45 | inventory | Swift BindingTests fixture (Collisions): NullableRefOverrideCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/OverloadDeclarationOrderBareName.swift` | 48 | inventory | Swift BindingTests fixture (Collisions): OverloadDeclarationOrderBareName |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/ParameterNameResultCollision.swift` | 45 | inventory | Swift BindingTests fixture (Collisions): ParameterNameResultCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/PropertyMethodCollision.swift` | 35 | inventory | Swift BindingTests fixture (Collisions): PropertyMethodCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/RenamedNestedTypeMethodCollision.swift` | 78 | inventory | Swift BindingTests fixture (Collisions): RenamedNestedTypeMethodCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/SameModuleOverrideCollision.swift` | 98 | inventory | Swift BindingTests fixture (Collisions): SameModuleOverrideCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Collisions/SyntheticNameCollisions.swift` | 138 | inventory | Swift BindingTests fixture (Collisions): SyntheticNameCollisions |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleInheritance.swift` | 151 | inventory | Swift BindingTests fixture (CrossModule): CrossModuleInheritance |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleNestedExtension.swift` | 169 | inventory | Swift BindingTests fixture (CrossModule): CrossModuleNestedExtension |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleShortNameCollision.swift` | 70 | inventory | Swift BindingTests fixture (CrossModule): CrossModuleShortNameCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleUsage.swift` | 349 | inventory | Swift BindingTests fixture (CrossModule): CrossModuleUsage |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/SubclassExistentialBoxing.swift` | 35 | inventory | Swift BindingTests fixture (CrossModule): SubclassExistentialBoxing |
| `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/TypeAliases.swift` | 44 | inventory | Swift BindingTests fixture (CrossModule): TypeAliases |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/AvailabilityFamilyF.swift` | 159 | inventory | Swift BindingTests fixture (EdgeCases): AvailabilityFamilyF |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/AvailabilityPropagation.swift` | 129 | inventory | Swift BindingTests fixture (EdgeCases): AvailabilityPropagation |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/Deprecation.swift` | 80 | inventory | Swift BindingTests fixture (EdgeCases): Deprecation |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/EdgeCases.disabled/Keywords.swift` | 41 | inventory | Swift BindingTests fixture (EdgeCases): Keywords |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/EdgeCases.disabled/Visibility.swift` | 73 | inventory | Swift BindingTests fixture (EdgeCases): Visibility |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/FilterScope.swift` | 23 | inventory | Swift BindingTests fixture (EdgeCases): FilterScope |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/Keywords.swift` | 60 | inventory | Swift BindingTests fixture (EdgeCases): Keywords |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/OptionalProtocolMembers.swift` | 111 | inventory | Swift BindingTests fixture (EdgeCases): OptionalProtocolMembers |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/RuntimeAvailabilityGuard.swift` | 218 | inventory | Swift BindingTests fixture (EdgeCases): RuntimeAvailabilityGuard |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/Unicode.swift` | 31 | inventory | Swift BindingTests fixture (EdgeCases): Unicode |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/Visibility.swift` | 73 | inventory | Swift BindingTests fixture (EdgeCases): Visibility |
| `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/WitnessDispatchIndexLockstep.swift` | 68 | inventory | Swift BindingTests fixture (EdgeCases): WitnessDispatchIndexLockstep |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/BareClosurePayloadEnumConstructorParam.swift` | 73 | inventory | Swift BindingTests fixture (Enums): BareClosurePayloadEnumConstructorParam |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/CasePropertyCollisionEnum.swift` | 44 | inventory | Swift BindingTests fixture (Enums): CasePropertyCollisionEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/ClassPayloadEnum.swift` | 142 | inventory | Swift BindingTests fixture (Enums): ClassPayloadEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/CrossModulePayloadEnum.swift` | 89 | inventory | Swift BindingTests fixture (Enums): CrossModulePayloadEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/DirectAnyTypePayloadSkip.swift` | 35 | inventory | Swift BindingTests fixture (Enums): DirectAnyTypePayloadSkip |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/EquatableEnum.swift` | 39 | inventory | Swift BindingTests fixture (Enums): EquatableEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/GenericPayloadHolder.swift` | 139 | inventory | Swift BindingTests fixture (Enums): GenericPayloadHolder |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/MultiAssociatedValues.swift` | 84 | inventory | Swift BindingTests fixture (Enums): MultiAssociatedValues |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/OptionalClosurePayloadEnum.swift` | 56 | inventory | Swift BindingTests fixture (Enums): OptionalClosurePayloadEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Enums/StringEnumRawValues.swift` | 220 | inventory | Swift BindingTests fixture (Enums): StringEnumRawValues |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/AnyErrorCallbackFixture.swift` | 124 | inventory | Swift BindingTests fixture (ErrorHandling): AnyErrorCallbackFixture |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/CascadeRegistryFiltering.swift` | 103 | inventory | Swift BindingTests fixture (ErrorHandling): CascadeRegistryFiltering |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/CaselessNamespaceLocalizedError.swift` | 49 | inventory | Swift BindingTests fixture (ErrorHandling): CaselessNamespaceLocalizedError |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/DirectConformanceLocalizedError.swift` | 33 | inventory | Swift BindingTests fixture (ErrorHandling): DirectConformanceLocalizedError |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/ErrorTypes.swift` | 96 | inventory | Swift BindingTests fixture (ErrorHandling): ErrorTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/NestedResultClosureFixture.swift` | 97 | inventory | Swift BindingTests fixture (ErrorHandling): NestedResultClosureFixture |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/ResultOfVoidFixture.swift` | 50 | inventory | Swift BindingTests fixture (ErrorHandling): ResultOfVoidFixture |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/ReverseErrorCarriage.swift` | 43 | inventory | Swift BindingTests fixture (ErrorHandling): ReverseErrorCarriage |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/SimpleEnumLocalizedError.swift` | 45 | inventory | Swift BindingTests fixture (ErrorHandling): SimpleEnumLocalizedError |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/StaticOptionalErrorReturn.swift` | 60 | inventory | Swift BindingTests fixture (ErrorHandling): StaticOptionalErrorReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/ThrowingFunctions.swift` | 182 | inventory | Swift BindingTests fixture (ErrorHandling): ThrowingFunctions |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/ThrowingProtocolWitness.swift` | 35 | inventory | Swift BindingTests fixture (ErrorHandling): ThrowingProtocolWitness |
| `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/TypedThrows.swift` | 145 | inventory | Swift BindingTests fixture (ErrorHandling): TypedThrows |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/DataRegisterStraddle.swift` | 38 | inventory | Swift BindingTests fixture (Foundation): DataRegisterStraddle |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/Date.swift` | 82 | inventory | Swift BindingTests fixture (Foundation): Date |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/FoundationOverlayTypedRemapHelper.swift` | 26 | inventory | Swift BindingTests fixture (Foundation): FoundationOverlayTypedRemapHelper |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/LocalizedStringResource.swift` | 57 | inventory | Swift BindingTests fixture (Foundation): LocalizedStringResource |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/Measurement.swift` | 59 | inventory | Swift BindingTests fixture (Foundation): Measurement |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/TestNSObservable.swift` | 42 | inventory | Swift BindingTests fixture (Foundation): TestNSObservable |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/URLContainerTestHelper.swift` | 78 | inventory | Swift BindingTests fixture (Foundation): URLContainerTestHelper |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/URLContainerWitness.swift` | 197 | inventory | Swift BindingTests fixture (Foundation): URLContainerWitness |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/URLRequestTestHelper.swift` | 57 | inventory | Swift BindingTests fixture (Foundation): URLRequestTestHelper |
| `BindingTests/Sources/SwiftBindingsTestLib/Foundation/URLTestHelper.swift` | 58 | inventory | Swift BindingTests fixture (Foundation): URLTestHelper |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/AssociatedTypes.swift` | 32 | inventory | Swift BindingTests fixture (Generics): AssociatedTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/BitwiseConstrainedCtor.swift` | 63 | inventory | Swift BindingTests fixture (Generics): BitwiseConstrainedCtor |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/BlittableGenericArgs.swift` | 108 | inventory | Swift BindingTests fixture (Generics): BlittableGenericArgs |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/BoundGenericEdgeCases.swift` | 47 | inventory | Swift BindingTests fixture (Generics): BoundGenericEdgeCases |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/BoundGenericOfParentCtor.swift` | 27 | inventory | Swift BindingTests fixture (Generics): BoundGenericOfParentCtor |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/BufferModeMetadata.swift` | 59 | inventory | Swift BindingTests fixture (Generics): BufferModeMetadata |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Bundle05ConditionalEquatable.swift` | 59 | inventory | Swift BindingTests fixture (Generics): Bundle05ConditionalEquatable |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Bundle05MultiSpecAccessors.swift` | 316 | inventory | Swift BindingTests fixture (Generics): Bundle05MultiSpecAccessors |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/CompositionMethodConstraint.swift` | 49 | inventory | Swift BindingTests fixture (Generics): CompositionMethodConstraint |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstrainedExistential.swift` | 104 | inventory | Swift BindingTests fixture (Generics): ConstrainedExistential |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstrainedExtensionDedup.swift` | 58 | inventory | Swift BindingTests fixture (Generics): ConstrainedExtensionDedup |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstrainedExtensionDefaultViaRename.swift` | 100 | inventory | Swift BindingTests fixture (Generics): ConstrainedExtensionDefaultViaRename |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstrainedGenericConcreteProperty.swift` | 58 | inventory | Swift BindingTests fixture (Generics): ConstrainedGenericConcreteProperty |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstrainedGenericMetadata.swift` | 53 | inventory | Swift BindingTests fixture (Generics): ConstrainedGenericMetadata |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Constraints.swift` | 613 | inventory | Swift BindingTests fixture (Generics): Constraints |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstructorAdmissibility.swift` | 184 | inventory | Swift BindingTests fixture (Generics): ConstructorAdmissibility |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/CsmClassConformerCarrier.swift` | 57 | inventory | Swift BindingTests fixture (Generics): CsmClassConformerCarrier |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/CsmKeyPathParam.swift` | 87 | inventory | Swift BindingTests fixture (Generics): CsmKeyPathParam |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/DependentMemberSameTypeConstraint.swift` | 89 | inventory | Swift BindingTests fixture (Generics): DependentMemberSameTypeConstraint |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ExistentialReturnBypass.swift` | 40 | inventory | Swift BindingTests fixture (Generics): ExistentialReturnBypass |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Existentials.swift` | 263 | inventory | Swift BindingTests fixture (Generics): Existentials |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Functions.swift` | 31 | inventory | Swift BindingTests fixture (Generics): Functions |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/GenericAbiWrappers.swift` | 214 | inventory | Swift BindingTests fixture (Generics): GenericAbiWrappers |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/GenericConstrainedExtensionOverload.swift` | 92 | inventory | Swift BindingTests fixture (Generics): GenericConstrainedExtensionOverload |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/GenericExtensionOptionalReturn.swift` | 113 | inventory | Swift BindingTests fixture (Generics): GenericExtensionOptionalReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/GenericIndexableCollection.swift` | 62 | inventory | Swift BindingTests fixture (Generics): GenericIndexableCollection |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/GenericMetadataAotRooting.swift` | 60 | inventory | Swift BindingTests fixture (Generics): GenericMetadataAotRooting |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/MarkerConstrainedCtor.swift` | 35 | inventory | Swift BindingTests fixture (Generics): MarkerConstrainedCtor |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Metatypes.swift` | 144 | inventory | Swift BindingTests fixture (Generics): Metatypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/MethodGenericBridgeReturn.swift` | 96 | inventory | Swift BindingTests fixture (Generics): MethodGenericBridgeReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/MethodLevelGenerics.swift` | 636 | inventory | Swift BindingTests fixture (Generics): MethodLevelGenerics |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/NestedConformerSpecialization.swift` | 188 | inventory | Swift BindingTests fixture (Generics): NestedConformerSpecialization |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/NestedOfParent.swift` | 72 | inventory | Swift BindingTests fixture (Generics): NestedOfParent |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/OpaqueParameters.swift` | 68 | inventory | Swift BindingTests fixture (Generics): OpaqueParameters |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ParentMethodStricterConstraint.swift` | 95 | inventory | Swift BindingTests fixture (Generics): ParentMethodStricterConstraint |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatBagConformerMismatch.swift` | 97 | inventory | Swift BindingTests fixture (Generics): PatBagConformerMismatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatBoundedStaticFactoryShape.swift` | 53 | inventory | Swift BindingTests fixture (Generics): PatBoundedStaticFactoryShape |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentAsyncMethods.swift` | 107 | inventory | Swift BindingTests fixture (Generics): PatParentAsyncMethods |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentAsyncVoidMethods.swift` | 134 | inventory | Swift BindingTests fixture (Generics): PatParentAsyncVoidMethods |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentCtorWithArg.swift` | 56 | inventory | Swift BindingTests fixture (Generics): PatParentCtorWithArg |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentOnlyMethods.swift` | 80 | inventory | Swift BindingTests fixture (Generics): PatParentOnlyMethods |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentPlainProperties.swift` | 70 | inventory | Swift BindingTests fixture (Generics): PatParentPlainProperties |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentStringMethods.swift` | 89 | inventory | Swift BindingTests fixture (Generics): PatParentStringMethods |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/ReturnTypeOnlyOverload.swift` | 37 | inventory | Swift BindingTests fixture (Generics): ReturnTypeOnlyOverload |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/SelfReqProtocolCtor.swift` | 52 | inventory | Swift BindingTests fixture (Generics): SelfReqProtocolCtor |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/SigningSpecialization.swift` | 104 | inventory | Swift BindingTests fixture (Generics): SigningSpecialization |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/StdlibProtocolConstraints.swift` | 75 | inventory | Swift BindingTests fixture (Generics): StdlibProtocolConstraints |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/SubclassClosedGenericParent.swift` | 91 | inventory | Swift BindingTests fixture (Generics): SubclassClosedGenericParent |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/TypedCollectionProjection.swift` | 80 | inventory | Swift BindingTests fixture (Generics): TypedCollectionProjection |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/Types.swift` | 150 | inventory | Swift BindingTests fixture (Generics): Types |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/VariadicGenericPack.swift` | 57 | inventory | Swift BindingTests fixture (Generics): VariadicGenericPack |
| `BindingTests/Sources/SwiftBindingsTestLib/Generics/VariadicResultBuilder.swift` | 86 | inventory | Swift BindingTests fixture (Generics): VariadicResultBuilder |
| `BindingTests/Sources/SwiftBindingsTestLib/Initializers/BasicInit.swift` | 60 | inventory | Swift BindingTests fixture (Initializers): BasicInit |
| `BindingTests/Sources/SwiftBindingsTestLib/Initializers/ConstLiteralInit.swift` | 37 | inventory | Swift BindingTests fixture (Initializers): ConstLiteralInit |
| `BindingTests/Sources/SwiftBindingsTestLib/Initializers/Failable.swift` | 105 | inventory | Swift BindingTests fixture (Initializers): Failable |
| `BindingTests/Sources/SwiftBindingsTestLib/Initializers/GateReducedInit.swift` | 29 | inventory | Swift BindingTests fixture (Initializers): GateReducedInit |
| `BindingTests/Sources/SwiftBindingsTestLib/Initializers/Throwing.swift` | 65 | inventory | Swift BindingTests fixture (Initializers): Throwing |
| `BindingTests/Sources/SwiftBindingsTestLib/Internal/InternalConformerToPublicProtocol.swift` | 117 | inventory | Swift BindingTests fixture (Internal): InternalConformerToPublicProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Internal/InternalTypeReach.swift` | 373 | inventory | Swift BindingTests fixture (Internal): InternalTypeReach |
| `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathFoundation.swift` | 103 | inventory | Swift BindingTests fixture (KeyPath): KeyPathFoundation |
| `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathGenericReturn.swift` | 59 | inventory | Swift BindingTests fixture (KeyPath): KeyPathGenericReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathProtocolBag.swift` | 202 | inventory | Swift BindingTests fixture (KeyPath): KeyPathProtocolBag |
| `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathRouteC.swift` | 97 | inventory | Swift BindingTests fixture (KeyPath): KeyPathRouteC |
| `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathSingletons.swift` | 145 | inventory | Swift BindingTests fixture (KeyPath): KeyPathSingletons |
| `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/EscapingClosureLifetimeFixture.swift` | 47 | inventory | Swift BindingTests fixture (Lifetime): EscapingClosureLifetimeFixture |
| `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/HeapOwnershipTransferFixtures.swift` | 47 | inventory | Swift BindingTests fixture (Lifetime): HeapOwnershipTransferFixtures |
| `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/OwnershipTests.swift` | 274 | inventory | Swift BindingTests fixture (Lifetime): OwnershipTests |
| `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/ProxyLifetimeFixture.swift` | 54 | inventory | Swift BindingTests fixture (Lifetime): ProxyLifetimeFixture |
| `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/ReverseDispatchInvariants.swift` | 85 | inventory | Swift BindingTests fixture (Lifetime): ReverseDispatchInvariants |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/AsyncGenericCollectionReturn.swift` | 64 | inventory | Swift BindingTests fixture (MemoryManagement): AsyncGenericCollectionReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/ExistentialParamLeak.swift` | 58 | inventory | Swift BindingTests fixture (MemoryManagement): ExistentialParamLeak |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/ExistentialReturnLeak.swift` | 239 | inventory | Swift BindingTests fixture (MemoryManagement): ExistentialReturnLeak |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/LeakDetection.swift` | 655 | inventory | Swift BindingTests fixture (MemoryManagement): LeakDetection |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/LibraryEvolution.swift` | 144 | inventory | Swift BindingTests fixture (MemoryManagement): LibraryEvolution |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/RetainCycles.swift` | 194 | inventory | Swift BindingTests fixture (MemoryManagement): RetainCycles |
| `BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/SuppressedProxyAsyncCarrierLeak.swift` | 136 | inventory | Swift BindingTests fixture (MemoryManagement): SuppressedProxyAsyncCarrierLeak |
| `BindingTests/Sources/SwiftBindingsTestLib/Metadata/AbiLayoutTripwire.swift` | 305 | inventory | Swift BindingTests fixture (Metadata): AbiLayoutTripwire |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/NSCodingDelegateDispatch.swift` | 45 | inventory | Swift BindingTests fixture (ObjCInterop): NSCodingDelegateDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/NSObjectSubclass.swift` | 75 | inventory | Swift BindingTests fixture (ObjCInterop): NSObjectSubclass |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/ObjCAttributes.swift` | 92 | inventory | Swift BindingTests fixture (ObjCInterop): ObjCAttributes |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/ObjCClassBoundExistential.swift` | 174 | inventory | Swift BindingTests fixture (ObjCInterop): ObjCClassBoundExistential |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/ObjCExistentialOutOfScopeGate.swift` | 34 | inventory | Swift BindingTests fixture (ObjCInterop): ObjCExistentialOutOfScopeGate |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/ObjCExistentialReverseVtableLockstep.swift` | 42 | inventory | Swift BindingTests fixture (ObjCInterop): ObjCExistentialReverseVtableLockstep |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/ObjCProtocolDispatch.swift` | 34 | inventory | Swift BindingTests fixture (ObjCInterop): ObjCProtocolDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/OptionalObjCClassProperty.swift` | 71 | inventory | Swift BindingTests fixture (ObjCInterop): OptionalObjCClassProperty |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/Selectors.swift` | 57 | inventory | Swift BindingTests fixture (ObjCInterop): Selectors |
| `BindingTests/Sources/SwiftBindingsTestLib/ObjCInterop/Singletons.swift` | 68 | inventory | Swift BindingTests fixture (ObjCInterop): Singletons |
| `BindingTests/Sources/SwiftBindingsTestLib/Operators/Arithmetic.swift` | 36 | inventory | Swift BindingTests fixture (Operators): Arithmetic |
| `BindingTests/Sources/SwiftBindingsTestLib/Operators/Bitwise.swift` | 36 | inventory | Swift BindingTests fixture (Operators): Bitwise |
| `BindingTests/Sources/SwiftBindingsTestLib/Operators/Comparison.swift` | 154 | inventory | Swift BindingTests fixture (Operators): Comparison |
| `BindingTests/Sources/SwiftBindingsTestLib/Operators/Unary.swift` | 28 | inventory | Swift BindingTests fixture (Operators): Unary |
| `BindingTests/Sources/SwiftBindingsTestLib/Optionals/BlittableOptionalCdeclWrappers.swift` | 72 | inventory | Swift BindingTests fixture (Optionals): BlittableOptionalCdeclWrappers |
| `BindingTests/Sources/SwiftBindingsTestLib/Optionals/OptionalAutoBridgeStruct.swift` | 76 | inventory | Swift BindingTests fixture (Optionals): OptionalAutoBridgeStruct |
| `BindingTests/Sources/SwiftBindingsTestLib/Optionals/OptionalTypes.swift` | 172 | inventory | Swift BindingTests fixture (Optionals): OptionalTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/DefaultOptionalParams.swift` | 130 | inventory | Swift BindingTests fixture (Parameters): DefaultOptionalParams |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/Defaults.swift` | 22 | inventory | Swift BindingTests fixture (Parameters): Defaults |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/Inout.swift` | 31 | inventory | Swift BindingTests fixture (Parameters): Inout |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/RealityKitParameterBugRepros.swift` | 57 | inventory | Swift BindingTests fixture (Parameters): RealityKitParameterBugRepros |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/UnderscoreLabels.swift` | 37 | inventory | Swift BindingTests fixture (Parameters): UnderscoreLabels |
| `BindingTests/Sources/SwiftBindingsTestLib/Parameters/Variadic.swift` | 75 | inventory | Swift BindingTests fixture (Parameters): Variadic |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/BuilderPattern.swift` | 38 | inventory | Swift BindingTests fixture (Patterns): BuilderPattern |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/CachePattern.swift` | 89 | inventory | Swift BindingTests fixture (Patterns): CachePattern |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/ControlHierarchy.swift` | 106 | inventory | Swift BindingTests fixture (Patterns): ControlHierarchy |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/HierarchyInspection.swift` | 92 | inventory | Swift BindingTests fixture (Patterns): HierarchyInspection |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift` | 152 | inventory | Swift BindingTests fixture (Patterns): RealWorldCompositions |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/StaticFactory.swift` | 31 | inventory | Swift BindingTests fixture (Patterns): StaticFactory |
| `BindingTests/Sources/SwiftBindingsTestLib/Patterns/StructBackedEnum.swift` | 21 | inventory | Swift BindingTests fixture (Patterns): StructBackedEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/Computed.swift` | 38 | inventory | Swift BindingTests fixture (Properties): Computed |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/Getters.swift` | 45 | inventory | Swift BindingTests fixture (Properties): Getters |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/OrphanedGetterShapes.swift` | 61 | inventory | Swift BindingTests fixture (Properties): OrphanedGetterShapes |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/RealityKitPropertyBugRepros.swift` | 62 | inventory | Swift BindingTests fixture (Properties): RealityKitPropertyBugRepros |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/Setters.swift` | 91 | inventory | Swift BindingTests fixture (Properties): Setters |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/Static.swift` | 72 | inventory | Swift BindingTests fixture (Properties): Static |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/StaticStructSingleton.swift` | 23 | inventory | Swift BindingTests fixture (Properties): StaticStructSingleton |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/StructPropertyAccess.swift` | 39 | inventory | Swift BindingTests fixture (Properties): StructPropertyAccess |
| `BindingTests/Sources/SwiftBindingsTestLib/Properties/Subscripts.swift` | 101 | inventory | Swift BindingTests fixture (Properties): Subscripts |
| `BindingTests/Sources/SwiftBindingsTestLib/PropertyWrappers.disabled/Wrappers.swift` | 95 | inventory | Swift BindingTests fixture (PropertyWrappers.disabled): Wrappers |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/AsyncReverseDispatch.swift` | 42 | inventory | Swift BindingTests fixture (Protocols): AsyncReverseDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/AsyncSiblingMethodDispatch.swift` | 86 | inventory | Swift BindingTests fixture (Protocols): AsyncSiblingMethodDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/AutoBridgeSwiftOnlyExistentialReturn.swift` | 50 | inventory | Swift BindingTests fixture (Protocols): AutoBridgeSwiftOnlyExistentialReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/AutoWrappedDelegate.swift` | 130 | inventory | Swift BindingTests fixture (Protocols): AutoWrappedDelegate |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/BasicProtocols.swift` | 82 | inventory | Swift BindingTests fixture (Protocols): BasicProtocols |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ClassParamCallback.swift` | 216 | inventory | Swift BindingTests fixture (Protocols): ClassParamCallback |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ClosureFanOut.swift` | 152 | inventory | Swift BindingTests fixture (Protocols): ClosureFanOut |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/Composition.swift` | 158 | inventory | Swift BindingTests fixture (Protocols): Composition |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/CompositionArgLifetime.swift` | 66 | inventory | Swift BindingTests fixture (Protocols): CompositionArgLifetime |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/Conformance.swift` | 146 | inventory | Swift BindingTests fixture (Protocols): Conformance |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ConformanceEmissionAgreement.swift` | 79 | inventory | Swift BindingTests fixture (Protocols): ConformanceEmissionAgreement |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/CrossCarrierInheritedProtocol.swift` | 93 | inventory | Swift BindingTests fixture (Protocols): CrossCarrierInheritedProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/CrossCarrierSignatureCollision.swift` | 75 | inventory | Swift BindingTests fixture (Protocols): CrossCarrierSignatureCollision |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/DuplicateSignatureDisambiguation.swift` | 124 | inventory | Swift BindingTests fixture (Protocols): DuplicateSignatureDisambiguation |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/EntityRootedExistential.swift` | 88 | inventory | Swift BindingTests fixture (Protocols): EntityRootedExistential |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ExistentialBoxing.swift` | 165 | inventory | Swift BindingTests fixture (Protocols): ExistentialBoxing |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ExistentialCollectionProjection.swift` | 251 | inventory | Swift BindingTests fixture (Protocols): ExistentialCollectionProjection |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ExistentialReturns.swift` | 64 | inventory | Swift BindingTests fixture (Protocols): ExistentialReturns |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ExtensionDefaultProtocol.swift` | 166 | inventory | Swift BindingTests fixture (Protocols): ExtensionDefaultProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ForwardOnlyProxyRepros.swift` | 99 | inventory | Swift BindingTests fixture (Protocols): ForwardOnlyProxyRepros |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/HiddenRequirementProtocolSkipping.swift` | 48 | inventory | Swift BindingTests fixture (Protocols): HiddenRequirementProtocolSkipping |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/InheritedDelegateDispatch.swift` | 321 | inventory | Swift BindingTests fixture (Protocols): InheritedDelegateDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/InoutStructDispatch.swift` | 43 | inventory | Swift BindingTests fixture (Protocols): InoutStructDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/IntraProtocolEffectOverload.swift` | 57 | inventory | Swift BindingTests fixture (Protocols): IntraProtocolEffectOverload |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/KeyBuilderAsyncBlockingOverloadProtocol.swift` | 44 | inventory | Swift BindingTests fixture (Protocols): KeyBuilderAsyncBlockingOverloadProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/KeyBuilderAsyncOverloadProtocol.swift` | 43 | inventory | Swift BindingTests fixture (Protocols): KeyBuilderAsyncOverloadProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/KeyBuilderParentNameProtocol.swift` | 33 | inventory | Swift BindingTests fixture (Protocols): KeyBuilderParentNameProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/KeywordMemberDispatch.swift` | 42 | inventory | Swift BindingTests fixture (Protocols): KeywordMemberDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/MarkerProtocolUmbrella.swift` | 57 | inventory | Swift BindingTests fixture (Protocols): MarkerProtocolUmbrella |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/MutatingGetterOnlyProtocol.swift` | 44 | inventory | Swift BindingTests fixture (Protocols): MutatingGetterOnlyProtocol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/NestedProtocols.swift` | 40 | inventory | Swift BindingTests fixture (Protocols): NestedProtocols |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/NonBlittableProtocols.swift` | 353 | inventory | Swift BindingTests fixture (Protocols): NonBlittableProtocols |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OpaqueParameterProtocols.swift` | 49 | inventory | Swift BindingTests fixture (Protocols): OpaqueParameterProtocols |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OptionalExistentialProperties.swift` | 94 | inventory | Swift BindingTests fixture (Protocols): OptionalExistentialProperties |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OptionalReferenceWitnessReturn.swift` | 70 | inventory | Swift BindingTests fixture (Protocols): OptionalReferenceWitnessReturn |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OverloadCollapseDispatch.swift` | 68 | inventory | Swift BindingTests fixture (Protocols): OverloadCollapseDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/PATFallbackBoundary.swift` | 72 | inventory | Swift BindingTests fixture (Protocols): PATFallbackBoundary |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolClosureSkipping.swift` | 545 | inventory | Swift BindingTests fixture (Protocols): ProtocolClosureSkipping |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolExtDuplicateSymbol.swift` | 28 | inventory | Swift BindingTests fixture (Protocols): ProtocolExtDuplicateSymbol |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolExtensionClosures.swift` | 39 | inventory | Swift BindingTests fixture (Protocols): ProtocolExtensionClosures |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolExtOptionalClassParam.swift` | 51 | inventory | Swift BindingTests fixture (Protocols): ProtocolExtOptionalClassParam |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/RealityKitProtocolBugRepros.swift` | 773 | inventory | Swift BindingTests fixture (Protocols): RealityKitProtocolBugRepros |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/RefinedReturnProtocols.swift` | 114 | inventory | Swift BindingTests fixture (Protocols): RefinedReturnProtocols |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/SiblingMethodDispatch.swift` | 206 | inventory | Swift BindingTests fixture (Protocols): SiblingMethodDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/SiblingPropertyDispatch.swift` | 318 | inventory | Swift BindingTests fixture (Protocols): SiblingPropertyDispatch |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/SpiRequirementProtocolSkipping.swift` | 58 | inventory | Swift BindingTests fixture (Protocols): SpiRequirementProtocolSkipping |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/StaticOnlyProtocolSkipping.swift` | 51 | inventory | Swift BindingTests fixture (Protocols): StaticOnlyProtocolSkipping |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/SuppressedProxyChannels.swift` | 593 | inventory | Swift BindingTests fixture (Protocols): SuppressedProxyChannels |
| `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ValueProviderPattern.swift` | 132 | inventory | Swift BindingTests fixture (Protocols): ValueProviderPattern |
| `BindingTests/Sources/SwiftBindingsTestLib/SmokeFixtures/AppleSupplementFactory.swift` | 53 | inventory | Swift BindingTests fixture (SmokeFixtures): AppleSupplementFactory |
| `BindingTests/Sources/SwiftBindingsTestLib/SmokeFixtures/TipKitSmokeTip.swift` | 48 | inventory | Swift BindingTests fixture (SmokeFixtures): TipKitSmokeTip |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/AsyncViews.swift` | 57 | inventory | Swift BindingTests fixture (SwiftUI): AsyncViews |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/BridgeEdgeCaseViews.swift` | 319 | inventory | Swift BindingTests fixture (SwiftUI): BridgeEdgeCaseViews |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/SimpleViews.swift` | 314 | inventory | Swift BindingTests fixture (SwiftUI): SimpleViews |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/SupportingTypes.swift` | 76 | inventory | Swift BindingTests fixture (SwiftUI): SupportingTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/ValidationPatternViews.swift` | 224 | inventory | Swift BindingTests fixture (SwiftUI): ValidationPatternViews |
| `BindingTests/Sources/SwiftBindingsTestLib/Tuples/BasicTuples.swift` | 34 | inventory | Swift BindingTests fixture (Tuples): BasicTuples |
| `BindingTests/Sources/SwiftBindingsTestLib/Tuples/Named.swift` | 24 | inventory | Swift BindingTests fixture (Tuples): Named |
| `BindingTests/Sources/SwiftBindingsTestLib/Tuples/TupleOfClassParam.swift` | 112 | inventory | Swift BindingTests fixture (Tuples): TupleOfClassParam |
| `BindingTests/Sources/SwiftBindingsTestLib/Tuples/TupleReturns.swift` | 49 | inventory | Swift BindingTests fixture (Tuples): TupleReturns |
| `BindingTests/Sources/SwiftBindingsTestLib/Tuples/TupleUnderEffects.swift` | 46 | inventory | Swift BindingTests fixture (Tuples): TupleUnderEffects |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/AbsentFrameworkType.swift` | 51 | inventory | Swift BindingTests fixture (Types): AbsentFrameworkType |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/AppleFrameworkTypes.swift` | 66 | inventory | Swift BindingTests fixture (Types): AppleFrameworkTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/AsyncClosurePropertySetter.swift` | 109 | inventory | Swift BindingTests fixture (Types): AsyncClosurePropertySetter |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/Classes.swift` | 325 | inventory | Swift BindingTests fixture (Types): Classes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/CoreGraphicsTypes.swift` | 115 | inventory | Swift BindingTests fixture (Types): CoreGraphicsTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/EnumDemotionAndOptionalSkip.swift` | 83 | inventory | Swift BindingTests fixture (Types): EnumDemotionAndOptionalSkip |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/Enums.swift` | 287 | inventory | Swift BindingTests fixture (Types): Enums |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/FrozenOptionalAbiWidth.swift` | 67 | inventory | Swift BindingTests fixture (Types): FrozenOptionalAbiWidth |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/InlineArray.swift` | 60 | inventory | Swift BindingTests fixture (Types): InlineArray |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/LargeEnum.swift` | 40 | inventory | Swift BindingTests fixture (Types): LargeEnum |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/MCBOverloadTypes.swift` | 38 | inventory | Swift BindingTests fixture (Types): MCBOverloadTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/NamespaceFacade.swift` | 71 | inventory | Swift BindingTests fixture (Types): NamespaceFacade |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/NestedEnums.swift` | 110 | inventory | Swift BindingTests fixture (Types): NestedEnums |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/NestedTypealiasReturns.swift` | 76 | inventory | Swift BindingTests fixture (Types): NestedTypealiasReturns |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/Noncopyable.swift` | 232 | inventory | Swift BindingTests fixture (Types): Noncopyable |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/NonStandardEnums.swift` | 89 | inventory | Swift BindingTests fixture (Types): NonStandardEnums |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/OptionalValueTypes.swift` | 60 | inventory | Swift BindingTests fixture (Types): OptionalValueTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/OptionSets.swift` | 55 | inventory | Swift BindingTests fixture (Types): OptionSets |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/ResultClosureTypes.swift` | 40 | inventory | Swift BindingTests fixture (Types): ResultClosureTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/ResultReturnTypes.swift` | 85 | inventory | Swift BindingTests fixture (Types): ResultReturnTypes |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/SimdFixtures.swift` | 270 | inventory | Swift BindingTests fixture (Types): SimdFixtures |
| `BindingTests/Sources/SwiftBindingsTestLib/Types/Structs.swift` | 558 | inventory | Swift BindingTests fixture (Types): Structs |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/OpaquePointer.swift` | 34 | inventory | Swift BindingTests fixture (UnsafeTypes): OpaquePointer |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/PointerGenerics.swift` | 40 | inventory | Swift BindingTests fixture (UnsafeTypes): PointerGenerics |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/Pointers.swift` | 73 | inventory | Swift BindingTests fixture (UnsafeTypes): Pointers |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/RawPointers.swift` | 42 | inventory | Swift BindingTests fixture (UnsafeTypes): RawPointers |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/Span.swift` | 33 | inventory | Swift BindingTests fixture (UnsafeTypes): Span |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/UnsafeMutableRawBufferParam.swift` | 112 | inventory | Swift BindingTests fixture (UnsafeTypes): UnsafeMutableRawBufferParam |
| `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/UnsafeRawBufferParam.swift` | 49 | inventory | Swift BindingTests fixture (UnsafeTypes): UnsafeRawBufferParam |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/AbiSafety.swift` | 367 | inventory | Swift BindingTests fixture (WrapperCoverage): AbiSafety |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/AbiSizeThreshold.swift` | 84 | inventory | Swift BindingTests fixture (WrapperCoverage): AbiSizeThreshold |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/ClosurePaths.swift` | 52 | inventory | Swift BindingTests fixture (WrapperCoverage): ClosurePaths |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/ConstructorParams.swift` | 166 | inventory | Swift BindingTests fixture (WrapperCoverage): ConstructorParams |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/OptionalPropertyPaths.swift` | 95 | inventory | Swift BindingTests fixture (WrapperCoverage): OptionalPropertyPaths |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/ReturnPaths.swift` | 189 | inventory | Swift BindingTests fixture (WrapperCoverage): ReturnPaths |
| `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/WrapperStripping.swift` | 89 | inventory | Swift BindingTests fixture (WrapperCoverage): WrapperStripping |
| `BindingTests/Sources/SwiftBindingsTestLib/Wrappers/CdeclWrapperCohesion.swift` | 204 | inventory | Swift BindingTests fixture (Wrappers): CdeclWrapperCohesion |
| `BindingTests/Sources/SwiftBindingsTestLibDependency/DependencyTypes.swift` | 475 | inventory | Swift BindingTests fixture (DependencyTypes.swift): DependencyTypes |
| `BindingTests/Sources/SwiftBindingsTestLibDependency/MiniEntityProperty.swift` | 57 | inventory | Swift BindingTests fixture (MiniEntityProperty.swift): MiniEntityProperty |

## BindingTests/RuntimeTestsApp (*.cs only, not bin/obj)

**Files**: 358  
**LOC**: 67,541  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `BindingTests/RuntimeTestsApp/AppIntents/EntityPropertyFactoryTests.cs` | 199 | inventory | Runtime test (AppIntents): EntityPropertyFactoryTests |
| `BindingTests/RuntimeTestsApp/AppIntents/MockAppEntityTests.cs` | 289 | inventory | Runtime test (AppIntents): MockAppEntityTests |
| `BindingTests/RuntimeTestsApp/AppleSupplement/ActivityKitReadiness.cs` | 38 | inventory | Runtime test (AppleSupplement): ActivityKitReadiness |
| `BindingTests/RuntimeTestsApp/AppleSupplement/AppleSupplementSmokeTests.cs` | 45 | inventory | Runtime test (AppleSupplement): AppleSupplementSmokeTests |
| `BindingTests/RuntimeTestsApp/AppleSupplement/AttributedStringTests.cs` | 94 | inventory | Runtime test (AppleSupplement): AttributedStringTests |
| `BindingTests/RuntimeTestsApp/AppleSupplement/LiveActivityTests.cs` | 242 | inventory | Runtime test (AppleSupplement): LiveActivityTests |
| `BindingTests/RuntimeTestsApp/Async/ActorIsolatedTests.cs` | 234 | inventory | Runtime test (Async): ActorIsolatedTests |
| `BindingTests/RuntimeTestsApp/Async/ActorNonisolatedTests.cs` | 46 | inventory | Runtime test (Async): ActorNonisolatedTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncClosureSpikeTests.cs` | 153 | inventory | Runtime test (Async): AsyncClosureSpikeTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncClosureTests.cs` | 104 | inventory | Runtime test (Async): AsyncClosureTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncComplexTypeTests.cs` | 422 | inventory | Runtime test (Async): AsyncComplexTypeTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncDataTupleTests.cs` | 54 | inventory | Runtime test (Async): AsyncDataTupleTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncExistentialArrayTests.cs` | 93 | inventory | Runtime test (Async): AsyncExistentialArrayTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncFactoryMethodTests.cs` | 169 | inventory | Runtime test (Async): AsyncFactoryMethodTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncFrozenStructParamTests.cs` | 72 | inventory | Runtime test (Async): AsyncFrozenStructParamTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncGenericSequenceTests.cs` | 139 | inventory | Runtime test (Async): AsyncGenericSequenceTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncGenericTupleTests.cs` | 84 | inventory | Runtime test (Async): AsyncGenericTupleTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncInstanceFrozenStructTests.cs` | 77 | inventory | Runtime test (Async): AsyncInstanceFrozenStructTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncMCBCallbackTests.cs` | 82 | inventory | Runtime test (Async): AsyncMCBCallbackTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncMethodGenericDefaultsTests.cs` | 145 | inventory | Runtime test (Async): AsyncMethodGenericDefaultsTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncMethodTests.cs` | 173 | inventory | Runtime test (Async): AsyncMethodTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncNonFrozenStructArrayParamTests.cs` | 86 | inventory | Runtime test (Async): AsyncNonFrozenStructArrayParamTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncNonFrozenStructDictionaryParamTests.cs` | 68 | inventory | Runtime test (Async): AsyncNonFrozenStructDictionaryParamTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncOpaqueReturnTests.cs` | 133 | inventory | Runtime test (Async): AsyncOpaqueReturnTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncPropertyTests.cs` | 57 | inventory | Runtime test (Async): AsyncPropertyTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncReverseWitnessTests.cs` | 165 | inventory | Runtime test (Async): AsyncReverseWitnessTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncSequenceTests.cs` | 415 | inventory | Runtime test (Async): AsyncSequenceTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncStreamOwnershipTests.cs` | 344 | inventory | Runtime test (Async): AsyncStreamOwnershipTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncStringTests.cs` | 134 | inventory | Runtime test (Async): AsyncStringTests |
| `BindingTests/RuntimeTestsApp/Async/AsyncThrowingClosureTests.cs` | 385 | inventory | Runtime test (Async): AsyncThrowingClosureTests |
| `BindingTests/RuntimeTestsApp/Async/CancellationRaceTests.cs` | 196 | inventory | Runtime test (Async): CancellationRaceTests |
| `BindingTests/RuntimeTestsApp/Async/CustomGlobalActorTests.cs` | 405 | inventory | Runtime test (Async): CustomGlobalActorTests |
| `BindingTests/RuntimeTestsApp/Async/DefaultedAsyncTrimOverloadTests.cs` | 272 | inventory | Runtime test (Async): DefaultedAsyncTrimOverloadTests |
| `BindingTests/RuntimeTestsApp/Async/MainActorTests.cs` | 179 | inventory | Runtime test (Async): MainActorTests |
| `BindingTests/RuntimeTestsApp/Async/ObjCAsyncSelfTests.cs` | 124 | inventory | Runtime test (Async): ObjCAsyncSelfTests |
| `BindingTests/RuntimeTestsApp/Async/SendableAnnotationTests.cs` | 83 | inventory | Runtime test (Async): SendableAnnotationTests |
| `BindingTests/RuntimeTestsApp/Closures/BufferPointerClosureTests.cs` | 151 | inventory | Runtime test (Closures): BufferPointerClosureTests |
| `BindingTests/RuntimeTestsApp/Closures/CallbackArgProjectionTests.cs` | 97 | inventory | Runtime test (Closures): CallbackArgProjectionTests |
| `BindingTests/RuntimeTestsApp/Closures/ClosedConstrainedClosureTests.cs` | 122 | inventory | Runtime test (Closures): ClosedConstrainedClosureTests |
| `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs` | 368 | inventory | Runtime test (Closures): ClosureEdgeCaseTests |
| `BindingTests/RuntimeTestsApp/Closures/ClosurePathTests.cs` | 65 | inventory | Runtime test (Closures): ClosurePathTests |
| `BindingTests/RuntimeTestsApp/Closures/ClosureTests.cs` | 650 | inventory | Runtime test (Closures): ClosureTests |
| `BindingTests/RuntimeTestsApp/Closures/GenericClosureBridgeLeakTests.cs` | 124 | inventory | Runtime test (Closures): GenericClosureBridgeLeakTests |
| `BindingTests/RuntimeTestsApp/Closures/GenericClosureBridgeTests.cs` | 304 | inventory | Runtime test (Closures): GenericClosureBridgeTests |
| `BindingTests/RuntimeTestsApp/Closures/NestedClosureBridgeTests.cs` | 79 | inventory | Runtime test (Closures): NestedClosureBridgeTests |
| `BindingTests/RuntimeTestsApp/Closures/OptionalReferenceClosureArbiterTests.cs` | 217 | inventory | Runtime test (Closures): OptionalReferenceClosureArbiterTests |
| `BindingTests/RuntimeTestsApp/Closures/OptionalThrowingVoidClosureTests.cs` | 242 | inventory | Runtime test (Closures): OptionalThrowingVoidClosureTests |
| `BindingTests/RuntimeTestsApp/Closures/PointerArgRenderHandlerTests.cs` | 55 | inventory | Runtime test (Closures): PointerArgRenderHandlerTests |
| `BindingTests/RuntimeTestsApp/Closures/ReturnedThrowingClosureLeakTests.cs` | 111 | inventory | Runtime test (Closures): ReturnedThrowingClosureLeakTests |
| `BindingTests/RuntimeTestsApp/Closures/StructClosureBridgeTests.cs` | 64 | inventory | Runtime test (Closures): StructClosureBridgeTests |
| `BindingTests/RuntimeTestsApp/Closures/SyntheticNameCollisionTests.cs` | 75 | inventory | Runtime test (Closures): SyntheticNameCollisionTests |
| `BindingTests/RuntimeTestsApp/Collections/ClosedRangeTests.cs` | 229 | inventory | Runtime test (Collections): ClosedRangeTests |
| `BindingTests/RuntimeTestsApp/Collections/ConstructorCollectionTests.cs` | 195 | inventory | Runtime test (Collections): ConstructorCollectionTests |
| `BindingTests/RuntimeTestsApp/Collections/DictionaryAnyTests.cs` | 195 | inventory | Runtime test (Collections): DictionaryAnyTests |
| `BindingTests/RuntimeTestsApp/Collections/DictionaryConstructorTests.cs` | 152 | inventory | Runtime test (Collections): DictionaryConstructorTests |
| `BindingTests/RuntimeTestsApp/Collections/EnumArrayTests.cs` | 77 | inventory | Runtime test (Collections): EnumArrayTests |
| `BindingTests/RuntimeTestsApp/Collections/SendableInfoDictTests.cs` | 108 | inventory | Runtime test (Collections): SendableInfoDictTests |
| `BindingTests/RuntimeTestsApp/Collections/SwiftSetWrapperTests.cs` | 192 | inventory | Runtime test (Collections): SwiftSetWrapperTests |
| `BindingTests/RuntimeTestsApp/Collisions/AsyncPropertyMethodCollisionTests.cs` | 68 | inventory | Runtime test (Collisions): AsyncPropertyMethodCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/ClosureOverloadCollisionTests.cs` | 94 | inventory | Runtime test (Collisions): ClosureOverloadCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/CollisionTests.cs` | 450 | inventory | Runtime test (Collisions): CollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/ContainerElementOverloadCollisionTests.cs` | 131 | inventory | Runtime test (Collisions): ContainerElementOverloadCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/FailableInitLabelCollisionTests.cs` | 68 | inventory | Runtime test (Collisions): FailableInitLabelCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/GenericArityOverloadCollisionTests.cs` | 53 | inventory | Runtime test (Collisions): GenericArityOverloadCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/NestedTypeMethodCollisionTests.cs` | 102 | inventory | Runtime test (Collisions): NestedTypeMethodCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/NullableRefOverrideCollisionTests.cs` | 119 | inventory | Runtime test (Collisions): NullableRefOverrideCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/OverloadDeclarationOrderBareNameTests.cs` | 52 | inventory | Runtime test (Collisions): OverloadDeclarationOrderBareNameTests |
| `BindingTests/RuntimeTestsApp/Collisions/PropertyMethodCollisionTests.cs` | 107 | inventory | Runtime test (Collisions): PropertyMethodCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/ProtocolExtSelfParamCollisionTests.cs` | 50 | inventory | Runtime test (Collisions): ProtocolExtSelfParamCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/RenamedNestedTypeMethodCollisionTests.cs` | 124 | inventory | Runtime test (Collisions): RenamedNestedTypeMethodCollisionTests |
| `BindingTests/RuntimeTestsApp/Collisions/SameModuleOverrideCollisionTests.cs` | 282 | inventory | Runtime test (Collisions): SameModuleOverrideCollisionTests |
| `BindingTests/RuntimeTestsApp/Concurrency/BulkCollectionStressTests.cs` | 469 | inventory | Runtime test (Concurrency): BulkCollectionStressTests |
| `BindingTests/RuntimeTestsApp/Concurrency/StressTests.cs` | 570 | inventory | Runtime test (Concurrency): StressTests |
| `BindingTests/RuntimeTestsApp/CrossModule/CrossModuleAliasTests.cs` | 38 | inventory | Runtime test (CrossModule): CrossModuleAliasTests |
| `BindingTests/RuntimeTestsApp/CrossModule/CrossModuleInheritanceTests.cs` | 84 | inventory | Runtime test (CrossModule): CrossModuleInheritanceTests |
| `BindingTests/RuntimeTestsApp/CrossModule/CrossModuleShortNameCollisionTests.cs` | 38 | inventory | Runtime test (CrossModule): CrossModuleShortNameCollisionTests |
| `BindingTests/RuntimeTestsApp/CrossModule/CrossModuleTests.cs` | 592 | inventory | Runtime test (CrossModule): CrossModuleTests |
| `BindingTests/RuntimeTestsApp/CrossModule/NestedTypeRenameTests.cs` | 90 | inventory | Runtime test (CrossModule): NestedTypeRenameTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/AvailabilityPropagationTests.cs` | 401 | inventory | Runtime test (EdgeCases): AvailabilityPropagationTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/EdgeCaseTests.cs` | 112 | inventory | Runtime test (EdgeCases): EdgeCaseTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/OptionalProtocolMembersTests.cs` | 196 | inventory | Runtime test (EdgeCases): OptionalProtocolMembersTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/RuntimeAvailabilityGuardTests.cs` | 299 | inventory | Runtime test (EdgeCases): RuntimeAvailabilityGuardTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/UnsafeMutableRawBufferPointerTests.cs` | 346 | inventory | Runtime test (EdgeCases): UnsafeMutableRawBufferPointerTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/UnsafeRawBufferPointerTests.cs` | 114 | inventory | Runtime test (EdgeCases): UnsafeRawBufferPointerTests |
| `BindingTests/RuntimeTestsApp/EdgeCases/WitnessDispatchIndexLockstepTests.cs` | 43 | inventory | Runtime test (EdgeCases): WitnessDispatchIndexLockstepTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/AnyErrorDescriptionTests.cs` | 279 | inventory | Runtime test (ErrorHandling): AnyErrorDescriptionTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/BasicSyncThrowProbeTests.cs` | 118 | inventory | Runtime test (ErrorHandling): BasicSyncThrowProbeTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/CascadeRegistryFilteringTests.cs` | 98 | inventory | Runtime test (ErrorHandling): CascadeRegistryFilteringTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/DirectConformanceLocalizedErrorTests.cs` | 43 | inventory | Runtime test (ErrorHandling): DirectConformanceLocalizedErrorTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/ErrorIdentityCarriageTests.cs` | 201 | inventory | Runtime test (ErrorHandling): ErrorIdentityCarriageTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/NestedResultClosureTests.cs` | 295 | inventory | Runtime test (ErrorHandling): NestedResultClosureTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/ResultOfVoidTests.cs` | 71 | inventory | Runtime test (ErrorHandling): ResultOfVoidTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/SimpleEnumLocalizedErrorTests.cs` | 111 | inventory | Runtime test (ErrorHandling): SimpleEnumLocalizedErrorTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/StaticOptionalErrorReturnTests.cs` | 96 | inventory | Runtime test (ErrorHandling): StaticOptionalErrorReturnTests |
| `BindingTests/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs` | 810 | inventory | Runtime test (ErrorHandling): ThrowingMethodTests |
| `BindingTests/RuntimeTestsApp/FoundationInterop/FoundationKvoTests.cs` | 119 | inventory | Runtime test (FoundationInterop): FoundationKvoTests |
| `BindingTests/RuntimeTestsApp/FoundationInterop/LocalizedStringResourceTests.cs` | 78 | inventory | Runtime test (FoundationInterop): LocalizedStringResourceTests |
| `BindingTests/RuntimeTestsApp/Generics/AnimalRosterInsertTests.cs` | 122 | inventory | Runtime test (Generics): AnimalRosterInsertTests |
| `BindingTests/RuntimeTestsApp/Generics/AppleShapedForecastTests.cs` | 113 | inventory | Runtime test (Generics): AppleShapedForecastTests |
| `BindingTests/RuntimeTestsApp/Generics/BasicGenericTests.cs` | 617 | inventory | Runtime test (Generics): BasicGenericTests |
| `BindingTests/RuntimeTestsApp/Generics/BitwiseConstrainedCtorTests.cs` | 85 | inventory | Runtime test (Generics): BitwiseConstrainedCtorTests |
| `BindingTests/RuntimeTestsApp/Generics/BlittableGenericArgsTests.cs` | 72 | inventory | Runtime test (Generics): BlittableGenericArgsTests |
| `BindingTests/RuntimeTestsApp/Generics/BoundGenericEdgeCaseTests.cs` | 74 | inventory | Runtime test (Generics): BoundGenericEdgeCaseTests |
| `BindingTests/RuntimeTestsApp/Generics/BoundGenericOfParentCtorTests.cs` | 39 | inventory | Runtime test (Generics): BoundGenericOfParentCtorTests |
| `BindingTests/RuntimeTestsApp/Generics/BufferModeMetadataTests.cs` | 47 | inventory | Runtime test (Generics): BufferModeMetadataTests |
| `BindingTests/RuntimeTestsApp/Generics/Bundle05ConditionalEquatableTests.cs` | 104 | inventory | Runtime test (Generics): Bundle05ConditionalEquatableTests |
| `BindingTests/RuntimeTestsApp/Generics/Bundle05MultiSpecAccessorsTests.cs` | 358 | inventory | Runtime test (Generics): Bundle05MultiSpecAccessorsTests |
| `BindingTests/RuntimeTestsApp/Generics/CollectibleBagTests.cs` | 50 | inventory | Runtime test (Generics): CollectibleBagTests |
| `BindingTests/RuntimeTestsApp/Generics/CompositionMethodConstraintTests.cs` | 72 | inventory | Runtime test (Generics): CompositionMethodConstraintTests |
| `BindingTests/RuntimeTestsApp/Generics/ConcreteSpecializationTests.cs` | 74 | inventory | Runtime test (Generics): ConcreteSpecializationTests |
| `BindingTests/RuntimeTestsApp/Generics/ConstrainedExistentialTests.cs` | 299 | inventory | Runtime test (Generics): ConstrainedExistentialTests |
| `BindingTests/RuntimeTestsApp/Generics/ConstrainedExtensionDefaultViaRenameTests.cs` | 70 | inventory | Runtime test (Generics): ConstrainedExtensionDefaultViaRenameTests |
| `BindingTests/RuntimeTestsApp/Generics/ConstrainedGenericConcretePropertyTests.cs` | 52 | inventory | Runtime test (Generics): ConstrainedGenericConcretePropertyTests |
| `BindingTests/RuntimeTestsApp/Generics/ConstrainedGenericFreeFunctionTests.cs` | 110 | inventory | Runtime test (Generics): ConstrainedGenericFreeFunctionTests |
| `BindingTests/RuntimeTestsApp/Generics/ConstructorAdmissibilityTests.cs` | 160 | inventory | Runtime test (Generics): ConstructorAdmissibilityTests |
| `BindingTests/RuntimeTestsApp/Generics/CsmClassConformerReturnTests.cs` | 95 | inventory | Runtime test (Generics): CsmClassConformerReturnTests |
| `BindingTests/RuntimeTestsApp/Generics/CsmDataProtocolTests.cs` | 732 | inventory | Runtime test (Generics): CsmDataProtocolTests |
| `BindingTests/RuntimeTestsApp/Generics/CsmGenericParentTests.cs` | 165 | inventory | Runtime test (Generics): CsmGenericParentTests |
| `BindingTests/RuntimeTestsApp/Generics/CsmKeyPathParamTests.cs` | 112 | inventory | Runtime test (Generics): CsmKeyPathParamTests |
| `BindingTests/RuntimeTestsApp/Generics/DefaultedTrimOverloadTests.cs` | 217 | inventory | Runtime test (Generics): DefaultedTrimOverloadTests |
| `BindingTests/RuntimeTestsApp/Generics/DefaultedTrimOverloadWithFileTests.cs` | 139 | inventory | Runtime test (Generics): DefaultedTrimOverloadWithFileTests |
| `BindingTests/RuntimeTestsApp/Generics/DemotedEnumProtocolTests.cs` | 58 | inventory | Runtime test (Generics): DemotedEnumProtocolTests |
| `BindingTests/RuntimeTestsApp/Generics/DependentMemberSameTypeConstraintTests.cs` | 85 | inventory | Runtime test (Generics): DependentMemberSameTypeConstraintTests |
| `BindingTests/RuntimeTestsApp/Generics/ExistentialReturnBypassTests.cs` | 46 | inventory | Runtime test (Generics): ExistentialReturnBypassTests |
| `BindingTests/RuntimeTestsApp/Generics/ExistentialUnionTests.cs` | 193 | inventory | Runtime test (Generics): ExistentialUnionTests |
| `BindingTests/RuntimeTestsApp/Generics/ForecastSeriesTests.cs` | 115 | inventory | Runtime test (Generics): ForecastSeriesTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericAbiTests.cs` | 469 | inventory | Runtime test (Generics): GenericAbiTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericConstrainedExtensionOverloadTests.cs` | 50 | inventory | Runtime test (Generics): GenericConstrainedExtensionOverloadTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericExtensionOptionalReturnTests.cs` | 96 | inventory | Runtime test (Generics): GenericExtensionOptionalReturnTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericIndexableCollectionTests.cs` | 134 | inventory | Runtime test (Generics): GenericIndexableCollectionTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericMetadataAotRootingTests.cs` | 50 | inventory | Runtime test (Generics): GenericMetadataAotRootingTests |
| `BindingTests/RuntimeTestsApp/Generics/GenericPayloadHolderTests.cs` | 117 | inventory | Runtime test (Generics): GenericPayloadHolderTests |
| `BindingTests/RuntimeTestsApp/Generics/HashSinkTests.cs` | 82 | inventory | Runtime test (Generics): HashSinkTests |
| `BindingTests/RuntimeTestsApp/Generics/MarkerConstrainedCtorTests.cs` | 54 | inventory | Runtime test (Generics): MarkerConstrainedCtorTests |
| `BindingTests/RuntimeTestsApp/Generics/MetatypeArrayTests.cs` | 81 | inventory | Runtime test (Generics): MetatypeArrayTests |
| `BindingTests/RuntimeTestsApp/Generics/MethodLevelGenericTests.cs` | 55 | inventory | Runtime test (Generics): MethodLevelGenericTests |
| `BindingTests/RuntimeTestsApp/Generics/MusicItemBagTests.cs` | 162 | inventory | Runtime test (Generics): MusicItemBagTests |
| `BindingTests/RuntimeTestsApp/Generics/NestedConformerSpecializationTests.cs` | 290 | inventory | Runtime test (Generics): NestedConformerSpecializationTests |
| `BindingTests/RuntimeTestsApp/Generics/NestedOfParentTests.cs` | 78 | inventory | Runtime test (Generics): NestedOfParentTests |
| `BindingTests/RuntimeTestsApp/Generics/OpaqueParamTests.cs` | 119 | inventory | Runtime test (Generics): OpaqueParamTests |
| `BindingTests/RuntimeTestsApp/Generics/ParentMethodStricterConstraintTests.cs` | 81 | inventory | Runtime test (Generics): ParentMethodStricterConstraintTests |
| `BindingTests/RuntimeTestsApp/Generics/PatBagConformerMismatchTests.cs` | 68 | inventory | Runtime test (Generics): PatBagConformerMismatchTests |
| `BindingTests/RuntimeTestsApp/Generics/PatBoundedStaticFactoryTests.cs` | 100 | inventory | Runtime test (Generics): PatBoundedStaticFactoryTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentAsyncMethodsTests.cs` | 108 | inventory | Runtime test (Generics): PatParentAsyncMethodsTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentAsyncVoidMethodsTests.cs` | 209 | inventory | Runtime test (Generics): PatParentAsyncVoidMethodsTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentCtorWithArgTests.cs` | 88 | inventory | Runtime test (Generics): PatParentCtorWithArgTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentOnlyMethodsTests.cs` | 116 | inventory | Runtime test (Generics): PatParentOnlyMethodsTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentPlainPropertiesTests.cs` | 95 | inventory | Runtime test (Generics): PatParentPlainPropertiesTests |
| `BindingTests/RuntimeTestsApp/Generics/PatParentStringMethodsTests.cs` | 131 | inventory | Runtime test (Generics): PatParentStringMethodsTests |
| `BindingTests/RuntimeTestsApp/Generics/ReturnTypeOnlyOverloadTests.cs` | 38 | inventory | Runtime test (Generics): ReturnTypeOnlyOverloadTests |
| `BindingTests/RuntimeTestsApp/Generics/SelfReqProtocolCtorTests.cs` | 47 | inventory | Runtime test (Generics): SelfReqProtocolCtorTests |
| `BindingTests/RuntimeTestsApp/Generics/SelfRequirementBoxingTests.cs` | 138 | inventory | Runtime test (Generics): SelfRequirementBoxingTests |
| `BindingTests/RuntimeTestsApp/Generics/SigningSpecializationTests.cs` | 270 | inventory | Runtime test (Generics): SigningSpecializationTests |
| `BindingTests/RuntimeTestsApp/Generics/StdlibProtocolConstraintTests.cs` | 110 | inventory | Runtime test (Generics): StdlibProtocolConstraintTests |
| `BindingTests/RuntimeTestsApp/Generics/SubclassClosedParentTrampolineTests.cs` | 128 | inventory | Runtime test (Generics): SubclassClosedParentTrampolineTests |
| `BindingTests/RuntimeTestsApp/Generics/TypedCollectionProjectionTests.cs` | 62 | inventory | Runtime test (Generics): TypedCollectionProjectionTests |
| `BindingTests/RuntimeTestsApp/Generics/UnitBoxClassConstraintTests.cs` | 97 | inventory | Runtime test (Generics): UnitBoxClassConstraintTests |
| `BindingTests/RuntimeTestsApp/Generics/VariadicGenericPackTests.cs` | 66 | inventory | Runtime test (Generics): VariadicGenericPackTests |
| `BindingTests/RuntimeTestsApp/Generics/VariadicResultBuilderTests.cs` | 92 | inventory | Runtime test (Generics): VariadicResultBuilderTests |
| `BindingTests/RuntimeTestsApp/Generics/VerificationOutcomeTests.cs` | 118 | inventory | Runtime test (Generics): VerificationOutcomeTests |
| `BindingTests/RuntimeTestsApp/GlobalUsings.cs` | 3 | inventory | Runtime test (GlobalUsings.cs): GlobalUsings |
| `BindingTests/RuntimeTestsApp/Infrastructure/LifetimeTracker.cs` | 202 | inventory | Runtime test harness: LifetimeTracker |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestBase.cs` | 389 | inventory | Runtime test harness: TestBase |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestDescriptors.cs` | 30 | inventory | Runtime test harness: TestDescriptors |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestLogger.cs` | 98 | inventory | Runtime test harness: TestLogger |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestResults.cs` | 390 | inventory | Runtime test harness: TestResults |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestRunFlags.cs` | 33 | inventory | Runtime test harness: TestRunFlags |
| `BindingTests/RuntimeTestsApp/Initializers/GateReducedInitTests.cs` | 40 | inventory | Runtime test (Initializers): GateReducedInitTests |
| `BindingTests/RuntimeTestsApp/Initializers/InitializerTests.cs` | 256 | inventory | Runtime test (Initializers): InitializerTests |
| `BindingTests/RuntimeTestsApp/Initializers/ThrowingClassConstructorTests.cs` | 66 | inventory | Runtime test (Initializers): ThrowingClassConstructorTests |
| `BindingTests/RuntimeTestsApp/Internal/InternalConformerToPublicProtocolTests.cs` | 148 | inventory | Runtime test (Internal): InternalConformerToPublicProtocolTests |
| `BindingTests/RuntimeTestsApp/Internal/InternalTypeReachTests.cs` | 375 | inventory | Runtime test (Internal): InternalTypeReachTests |
| `BindingTests/RuntimeTestsApp/KeyPath/KeyPathFoundationTests.cs` | 236 | inventory | Runtime test (KeyPath): KeyPathFoundationTests |
| `BindingTests/RuntimeTestsApp/KeyPath/KeyPathGenericReturnTests.cs` | 98 | inventory | Runtime test (KeyPath): KeyPathGenericReturnTests |
| `BindingTests/RuntimeTestsApp/KeyPath/KeyPathProtocolBagTests.cs` | 265 | inventory | Runtime test (KeyPath): KeyPathProtocolBagTests |
| `BindingTests/RuntimeTestsApp/KeyPath/KeyPathRouteCTests.cs` | 120 | inventory | Runtime test (KeyPath): KeyPathRouteCTests |
| `BindingTests/RuntimeTestsApp/KeyPath/KeyPathSingletonTests.cs` | 244 | inventory | Runtime test (KeyPath): KeyPathSingletonTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ArcRoundTripTests.cs` | 117 | inventory | Runtime test (Lifetime): ArcRoundTripTests |
| `BindingTests/RuntimeTestsApp/Lifetime/AsyncClosureContextLifetimeTests.cs` | 176 | inventory | Runtime test (Lifetime): AsyncClosureContextLifetimeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ConsumingNoncopyableTests.cs` | 300 | inventory | Runtime test (Lifetime): ConsumingNoncopyableTests |
| `BindingTests/RuntimeTestsApp/Lifetime/DisposeScopeTests.cs` | 216 | inventory | Runtime test (Lifetime): DisposeScopeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/EscapingClosureLifetimeTests.cs` | 204 | inventory | Runtime test (Lifetime): EscapingClosureLifetimeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/HeapOwnershipTransferTests.cs` | 324 | inventory | Runtime test (Lifetime): HeapOwnershipTransferTests |
| `BindingTests/RuntimeTestsApp/Lifetime/LifetimeTrackingTests.cs` | 310 | inventory | Runtime test (Lifetime): LifetimeTrackingTests |
| `BindingTests/RuntimeTestsApp/Lifetime/NegativePathTests.cs` | 294 | inventory | Runtime test (Lifetime): NegativePathTests |
| `BindingTests/RuntimeTestsApp/Lifetime/NonEscapingClosureLifetimeTests.cs` | 234 | inventory | Runtime test (Lifetime): NonEscapingClosureLifetimeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ObjectIdentityTests.cs` | 129 | inventory | Runtime test (Lifetime): ObjectIdentityTests |
| `BindingTests/RuntimeTestsApp/Lifetime/OwnershipGCStressTests.cs` | 696 | inventory | Runtime test (Lifetime): OwnershipGCStressTests |
| `BindingTests/RuntimeTestsApp/Lifetime/OwnershipTests.cs` | 266 | inventory | Runtime test (Lifetime): OwnershipTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ProxyDisposeTests.cs` | 223 | inventory | Runtime test (Lifetime): ProxyDisposeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ProxyLifetimeTests.cs` | 577 | inventory | Runtime test (Lifetime): ProxyLifetimeTests |
| `BindingTests/RuntimeTestsApp/Lifetime/ReverseDispatchInvariantTests.cs` | 206 | inventory | Runtime test (Lifetime): ReverseDispatchInvariantTests |
| `BindingTests/RuntimeTestsApp/Marshalling/AbiSafetyTests.cs` | 506 | inventory | Runtime test (Marshalling): AbiSafetyTests |
| `BindingTests/RuntimeTestsApp/Marshalling/AbiSizeThresholdTests.cs` | 99 | inventory | Runtime test (Marshalling): AbiSizeThresholdTests |
| `BindingTests/RuntimeTestsApp/Marshalling/AppleFrameworkTypeTests.cs` | 160 | inventory | Runtime test (Marshalling): AppleFrameworkTypeTests |
| `BindingTests/RuntimeTestsApp/Marshalling/AppleSupplementRoundTripTests.cs` | 208 | inventory | Runtime test (Marshalling): AppleSupplementRoundTripTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ArrayMarshallingTests.cs` | 204 | inventory | Runtime test (Marshalling): ArrayMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/BareClosurePayloadEnumConstructorParamTests.cs` | 72 | inventory | Runtime test (Marshalling): BareClosurePayloadEnumConstructorParamTests |
| `BindingTests/RuntimeTestsApp/Marshalling/BlittableRoundTripTests.cs` | 235 | inventory | Runtime test (Marshalling): BlittableRoundTripTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ClassMarshallingTests.cs` | 358 | inventory | Runtime test (Marshalling): ClassMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ClassPayloadEnumTests.cs` | 256 | inventory | Runtime test (Marshalling): ClassPayloadEnumTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ClassSingletonTests.cs` | 166 | inventory | Runtime test (Marshalling): ClassSingletonTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ConstructorParamTests.cs` | 259 | inventory | Runtime test (Marshalling): ConstructorParamTests |
| `BindingTests/RuntimeTestsApp/Marshalling/CrossAssemblyIdentityTests.cs` | 131 | inventory | Runtime test (Marshalling): CrossAssemblyIdentityTests |
| `BindingTests/RuntimeTestsApp/Marshalling/CrossModuleNestedExtensionTests.cs` | 302 | inventory | Runtime test (Marshalling): CrossModuleNestedExtensionTests |
| `BindingTests/RuntimeTestsApp/Marshalling/CrossModulePayloadEnumTests.cs` | 197 | inventory | Runtime test (Marshalling): CrossModulePayloadEnumTests |
| `BindingTests/RuntimeTestsApp/Marshalling/DataRegisterStraddleTests.cs` | 59 | inventory | Runtime test (Marshalling): DataRegisterStraddleTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EntryPointCallConvPairingTests.cs` | 129 | inventory | Runtime test (Marshalling): EntryPointCallConvPairingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EnumCasePropertyCollisionTests.cs` | 67 | inventory | Runtime test (Marshalling): EnumCasePropertyCollisionTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EnumExtensionTests.cs` | 146 | inventory | Runtime test (Marshalling): EnumExtensionTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EnumMarshallingTests.cs` | 543 | inventory | Runtime test (Marshalling): EnumMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EnumObjCBridgedPayloadTests.cs` | 174 | inventory | Runtime test (Marshalling): EnumObjCBridgedPayloadTests |
| `BindingTests/RuntimeTestsApp/Marshalling/EnumOptionalClosurePayloadTests.cs` | 57 | inventory | Runtime test (Marshalling): EnumOptionalClosurePayloadTests |
| `BindingTests/RuntimeTestsApp/Marshalling/FoundationOverlayTypedRemapTests.cs` | 39 | inventory | Runtime test (Marshalling): FoundationOverlayTypedRemapTests |
| `BindingTests/RuntimeTestsApp/Marshalling/FrozenOptionalAbiWidthTests.cs` | 82 | inventory | Runtime test (Marshalling): FrozenOptionalAbiWidthTests |
| `BindingTests/RuntimeTestsApp/Marshalling/LargeEnumTests.cs` | 116 | inventory | Runtime test (Marshalling): LargeEnumTests |
| `BindingTests/RuntimeTestsApp/Marshalling/MeasurementConstructionTests.cs` | 83 | inventory | Runtime test (Marshalling): MeasurementConstructionTests |
| `BindingTests/RuntimeTestsApp/Marshalling/MeasurementTests.cs` | 96 | inventory | Runtime test (Marshalling): MeasurementTests |
| `BindingTests/RuntimeTestsApp/Marshalling/MixedRegisterReturnTests.cs` | 160 | inventory | Runtime test (Marshalling): MixedRegisterReturnTests |
| `BindingTests/RuntimeTestsApp/Marshalling/NestedEnumTests.cs` | 375 | inventory | Runtime test (Marshalling): NestedEnumTests |
| `BindingTests/RuntimeTestsApp/Marshalling/NonStandardEnumTests.cs` | 193 | inventory | Runtime test (Marshalling): NonStandardEnumTests |
| `BindingTests/RuntimeTestsApp/Marshalling/OptionalMarshallingTests.cs` | 369 | inventory | Runtime test (Marshalling): OptionalMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/OptionalValueTypeTests.cs` | 148 | inventory | Runtime test (Marshalling): OptionalValueTypeTests |
| `BindingTests/RuntimeTestsApp/Marshalling/OptionSetTests.cs` | 198 | inventory | Runtime test (Marshalling): OptionSetTests |
| `BindingTests/RuntimeTestsApp/Marshalling/PointerMarshallingTests.cs` | 150 | inventory | Runtime test (Marshalling): PointerMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/ReturnPathTests.cs` | 257 | inventory | Runtime test (Marshalling): ReturnPathTests |
| `BindingTests/RuntimeTestsApp/Marshalling/SetParameterDefaultTests.cs` | 132 | inventory | Runtime test (Marshalling): SetParameterDefaultTests |
| `BindingTests/RuntimeTestsApp/Marshalling/SretSelfProbeTests.cs` | 170 | inventory | Runtime test (Marshalling): SretSelfProbeTests |
| `BindingTests/RuntimeTestsApp/Marshalling/StringMarshallingTests.cs` | 332 | inventory | Runtime test (Marshalling): StringMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/TupleMarshallingTests.cs` | 352 | inventory | Runtime test (Marshalling): TupleMarshallingTests |
| `BindingTests/RuntimeTestsApp/Marshalling/TupleOfClassParamGateTests.cs` | 52 | inventory | Runtime test (Marshalling): TupleOfClassParamGateTests |
| `BindingTests/RuntimeTestsApp/Marshalling/URLBridgeTests.cs` | 186 | inventory | Runtime test (Marshalling): URLBridgeTests |
| `BindingTests/RuntimeTestsApp/Marshalling/URLContainerBridgeTests.cs` | 162 | inventory | Runtime test (Marshalling): URLContainerBridgeTests |
| `BindingTests/RuntimeTestsApp/Marshalling/URLContainerWitnessTests.cs` | 249 | inventory | Runtime test (Marshalling): URLContainerWitnessTests |
| `BindingTests/RuntimeTestsApp/Marshalling/URLRequestTests.cs` | 120 | inventory | Runtime test (Marshalling): URLRequestTests |
| `BindingTests/RuntimeTestsApp/Marshalling/WrapperStrippingTests.cs` | 124 | inventory | Runtime test (Marshalling): WrapperStrippingTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/AsyncCollectionCarrierLeakProbeTests.cs` | 196 | inventory | Runtime test (MemoryManagement): AsyncCollectionCarrierLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/AsyncGenericBridgeCarrierLeakProbeTests.cs` | 113 | inventory | Runtime test (MemoryManagement): AsyncGenericBridgeCarrierLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/BorrowedCallbackArgLeakProbeTests.cs` | 91 | inventory | Runtime test (MemoryManagement): BorrowedCallbackArgLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/ClassBoundExistentialCollectionLeakProbeTests.cs` | 474 | inventory | Runtime test (MemoryManagement): ClassBoundExistentialCollectionLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/ColorLikeWrapper.cs` | 100 | inventory | Runtime test (MemoryManagement): ColorLikeWrapper |
| `BindingTests/RuntimeTestsApp/MemoryManagement/CompositionArgLifetimeProbeTests.cs` | 196 | inventory | Runtime test (MemoryManagement): CompositionArgLifetimeProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/DisposeTests.cs` | 184 | inventory | Runtime test (MemoryManagement): DisposeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/ExistentialParamLeakProbeTests.cs` | 190 | inventory | Runtime test (MemoryManagement): ExistentialParamLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/ExistentialReturnLeakProbeTests.cs` | 479 | inventory | Runtime test (MemoryManagement): ExistentialReturnLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/ExtractionRetainProbeTests.cs` | 415 | inventory | Runtime test (MemoryManagement): ExtractionRetainProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/LeakDetectionTests.cs` | 169 | inventory | Runtime test (MemoryManagement): LeakDetectionTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/LibraryEvolutionTests.cs` | 98 | inventory | Runtime test (MemoryManagement): LibraryEvolutionTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/OpaqueExistentialCollectionParamLeakProbeTests.cs` | 152 | inventory | Runtime test (MemoryManagement): OpaqueExistentialCollectionParamLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/StructVwtDestroyLeakTests.cs` | 219 | inventory | Runtime test (MemoryManagement): StructVwtDestroyLeakTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/SuppressedProxyAsyncCarrierLeakProbeTests.cs` | 289 | inventory | Runtime test (MemoryManagement): SuppressedProxyAsyncCarrierLeakProbeTests |
| `BindingTests/RuntimeTestsApp/MemoryManagement/WireCarrierLeakProbeTests.cs` | 391 | inventory | Runtime test (MemoryManagement): WireCarrierLeakProbeTests |
| `BindingTests/RuntimeTestsApp/Metadata/AbiLayoutTripwireTests.cs` | 395 | inventory | Runtime test (Metadata): AbiLayoutTripwireTests |
| `BindingTests/RuntimeTestsApp/Metadata/ExistentialMetadataTests.cs` | 30 | inventory | Runtime test (Metadata): ExistentialMetadataTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/NSCodingDelegateDispatchTests.cs` | 78 | inventory | Runtime test (ObjCInterop): NSCodingDelegateDispatchTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCClassBoundExistentialTests.cs` | 144 | inventory | Runtime test (ObjCInterop): ObjCClassBoundExistentialTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCExistentialReverseVtableLockstepTests.cs` | 54 | inventory | Runtime test (ObjCInterop): ObjCExistentialReverseVtableLockstepTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCInteropTests.cs` | 240 | inventory | Runtime test (ObjCInterop): ObjCInteropTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCProtocolDispatchTests.cs` | 68 | inventory | Runtime test (ObjCInterop): ObjCProtocolDispatchTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCShapeReceiverTests.cs` | 127 | inventory | Runtime test (ObjCInterop): ObjCShapeReceiverTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/ObjCUmbrellaFixtureTests.cs` | 167 | inventory | Runtime test (ObjCInterop): ObjCUmbrellaFixtureTests |
| `BindingTests/RuntimeTestsApp/ObjCInterop/OptionalObjCClassPropertyTests.cs` | 174 | inventory | Runtime test (ObjCInterop): OptionalObjCClassPropertyTests |
| `BindingTests/RuntimeTestsApp/Operators/EnumEqualityTests.cs` | 85 | inventory | Runtime test (Operators): EnumEqualityTests |
| `BindingTests/RuntimeTestsApp/Operators/OperatorTests.cs` | 155 | inventory | Runtime test (Operators): OperatorTests |
| `BindingTests/RuntimeTestsApp/Operators/StructEqualityTests.cs` | 260 | inventory | Runtime test (Operators): StructEqualityTests |
| `BindingTests/RuntimeTestsApp/Parameters/BlittableOptionalCdeclWrapperTests.cs` | 95 | inventory | Runtime test (Parameters): BlittableOptionalCdeclWrapperTests |
| `BindingTests/RuntimeTestsApp/Parameters/ParameterTests.cs` | 381 | inventory | Runtime test (Parameters): ParameterTests |
| `BindingTests/RuntimeTestsApp/Patterns/BuilderPatternTests.cs` | 132 | inventory | Runtime test (Patterns): BuilderPatternTests |
| `BindingTests/RuntimeTestsApp/Patterns/CachePatternTests.cs` | 180 | inventory | Runtime test (Patterns): CachePatternTests |
| `BindingTests/RuntimeTestsApp/Patterns/CompositionTests.cs` | 291 | inventory | Runtime test (Patterns): CompositionTests |
| `BindingTests/RuntimeTestsApp/Patterns/ControlHierarchyTests.cs` | 200 | inventory | Runtime test (Patterns): ControlHierarchyTests |
| `BindingTests/RuntimeTestsApp/Patterns/HierarchyInspectionTests.cs` | 177 | inventory | Runtime test (Patterns): HierarchyInspectionTests |
| `BindingTests/RuntimeTestsApp/Patterns/StaticFactoryTests.cs` | 82 | inventory | Runtime test (Patterns): StaticFactoryTests |
| `BindingTests/RuntimeTestsApp/Patterns/StructBackedEnumTests.cs` | 125 | inventory | Runtime test (Patterns): StructBackedEnumTests |
| `BindingTests/RuntimeTestsApp/Program.cs` | 511 | inventory | RuntimeTestsApp entry / test runner |
| `BindingTests/RuntimeTestsApp/Properties/NonFrozenOptionalStructTests.cs` | 63 | inventory | Runtime test (Properties): NonFrozenOptionalStructTests |
| `BindingTests/RuntimeTestsApp/Properties/NonFrozenPropertyTests.cs` | 186 | inventory | Runtime test (Properties): NonFrozenPropertyTests |
| `BindingTests/RuntimeTestsApp/Properties/OptionalPropertyPathTests.cs` | 228 | inventory | Runtime test (Properties): OptionalPropertyPathTests |
| `BindingTests/RuntimeTestsApp/Properties/OrphanedGetterShapeTests.cs` | 159 | inventory | Runtime test (Properties): OrphanedGetterShapeTests |
| `BindingTests/RuntimeTestsApp/Properties/StaticStructSingletonTests.cs` | 194 | inventory | Runtime test (Properties): StaticStructSingletonTests |
| `BindingTests/RuntimeTestsApp/Properties/StructPropertyLeakTests.cs` | 103 | inventory | Runtime test (Properties): StructPropertyLeakTests |
| `BindingTests/RuntimeTestsApp/Properties/SubscriptTests.cs` | 252 | inventory | Runtime test (Properties): SubscriptTests |
| `BindingTests/RuntimeTestsApp/Protocols/AsyncSiblingMethodDispatchTests.cs` | 265 | inventory | Runtime test (Protocols): AsyncSiblingMethodDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/AutoBridgeSwiftOnlyExistentialTests.cs` | 64 | inventory | Runtime test (Protocols): AutoBridgeSwiftOnlyExistentialTests |
| `BindingTests/RuntimeTestsApp/Protocols/AutoWrappedDelegateTests.cs` | 252 | inventory | Runtime test (Protocols): AutoWrappedDelegateTests |
| `BindingTests/RuntimeTestsApp/Protocols/ClassBoundExistentialArrayTests.cs` | 454 | inventory | Runtime test (Protocols): ClassBoundExistentialArrayTests |
| `BindingTests/RuntimeTestsApp/Protocols/ClassBoundExistentialArrayWriteParamTests.cs` | 366 | inventory | Runtime test (Protocols): ClassBoundExistentialArrayWriteParamTests |
| `BindingTests/RuntimeTestsApp/Protocols/ClassBoundExistentialDictValueTests.cs` | 233 | inventory | Runtime test (Protocols): ClassBoundExistentialDictValueTests |
| `BindingTests/RuntimeTestsApp/Protocols/ClassParamCallbackTests.cs` | 486 | inventory | Runtime test (Protocols): ClassParamCallbackTests |
| `BindingTests/RuntimeTestsApp/Protocols/ClosureFanOutTests.cs` | 182 | inventory | Runtime test (Protocols): ClosureFanOutTests |
| `BindingTests/RuntimeTestsApp/Protocols/CompositionTests.cs` | 133 | inventory | Runtime test (Protocols): CompositionTests |
| `BindingTests/RuntimeTestsApp/Protocols/ConformanceEmissionAgreementTests.cs` | 101 | inventory | Runtime test (Protocols): ConformanceEmissionAgreementTests |
| `BindingTests/RuntimeTestsApp/Protocols/CrossCarrierInheritedProtocolTests.cs` | 95 | inventory | Runtime test (Protocols): CrossCarrierInheritedProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/CrossCarrierSignatureCollisionTests.cs` | 100 | inventory | Runtime test (Protocols): CrossCarrierSignatureCollisionTests |
| `BindingTests/RuntimeTestsApp/Protocols/DuplicateSignatureDisambiguationTests.cs` | 212 | inventory | Runtime test (Protocols): DuplicateSignatureDisambiguationTests |
| `BindingTests/RuntimeTestsApp/Protocols/EntityRootedExistentialTests.cs` | 124 | inventory | Runtime test (Protocols): EntityRootedExistentialTests |
| `BindingTests/RuntimeTestsApp/Protocols/ExistentialBoxingTests.cs` | 480 | inventory | Runtime test (Protocols): ExistentialBoxingTests |
| `BindingTests/RuntimeTestsApp/Protocols/ExistentialCallbackTests.cs` | 54 | inventory | Runtime test (Protocols): ExistentialCallbackTests |
| `BindingTests/RuntimeTestsApp/Protocols/ExistentialReturnTests.cs` | 154 | inventory | Runtime test (Protocols): ExistentialReturnTests |
| `BindingTests/RuntimeTestsApp/Protocols/ExtensionDefaultProtocolTests.cs` | 134 | inventory | Runtime test (Protocols): ExtensionDefaultProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/ForwardOnlyProxyTests.cs` | 107 | inventory | Runtime test (Protocols): ForwardOnlyProxyTests |
| `BindingTests/RuntimeTestsApp/Protocols/HiddenRequirementProtocolSkipTests.cs` | 81 | inventory | Runtime test (Protocols): HiddenRequirementProtocolSkipTests |
| `BindingTests/RuntimeTestsApp/Protocols/InheritedDelegateDispatchTests.cs` | 443 | inventory | Runtime test (Protocols): InheritedDelegateDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/InoutStructDispatchTests.cs` | 103 | inventory | Runtime test (Protocols): InoutStructDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/IntraProtocolEffectOverloadTests.cs` | 144 | inventory | Runtime test (Protocols): IntraProtocolEffectOverloadTests |
| `BindingTests/RuntimeTestsApp/Protocols/KeyBuilderAsyncBlockingOverloadProtocolTests.cs` | 94 | inventory | Runtime test (Protocols): KeyBuilderAsyncBlockingOverloadProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/KeyBuilderAsyncOverloadProtocolTests.cs` | 96 | inventory | Runtime test (Protocols): KeyBuilderAsyncOverloadProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/KeyBuilderParentNameProtocolTests.cs` | 65 | inventory | Runtime test (Protocols): KeyBuilderParentNameProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/KeywordMemberDispatchTests.cs` | 82 | inventory | Runtime test (Protocols): KeywordMemberDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/MarkerProtocolUmbrellaTests.cs` | 34 | inventory | Runtime test (Protocols): MarkerProtocolUmbrellaTests |
| `BindingTests/RuntimeTestsApp/Protocols/NestedClassBoundExistentialTests.cs` | 318 | inventory | Runtime test (Protocols): NestedClassBoundExistentialTests |
| `BindingTests/RuntimeTestsApp/Protocols/NestedProtocolReferenceTests.cs` | 75 | inventory | Runtime test (Protocols): NestedProtocolReferenceTests |
| `BindingTests/RuntimeTestsApp/Protocols/OptionalClosedRangeProviderTests.cs` | 63 | inventory | Runtime test (Protocols): OptionalClosedRangeProviderTests |
| `BindingTests/RuntimeTestsApp/Protocols/OptionalExistentialPropertyTests.cs` | 315 | inventory | Runtime test (Protocols): OptionalExistentialPropertyTests |
| `BindingTests/RuntimeTestsApp/Protocols/OptionalReferenceWitnessReturnTests.cs` | 70 | inventory | Runtime test (Protocols): OptionalReferenceWitnessReturnTests |
| `BindingTests/RuntimeTestsApp/Protocols/OverloadCollapseDispatchTests.cs` | 84 | inventory | Runtime test (Protocols): OverloadCollapseDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/PATFallbackBoundaryTests.cs` | 117 | inventory | Runtime test (Protocols): PATFallbackBoundaryTests |
| `BindingTests/RuntimeTestsApp/Protocols/ProtocolClosureSkipTests.cs` | 1,459 | inventory | Runtime test (Protocols): ProtocolClosureSkipTests |
| `BindingTests/RuntimeTestsApp/Protocols/ProtocolExtDuplicateSymbolTests.cs` | 38 | inventory | Runtime test (Protocols): ProtocolExtDuplicateSymbolTests |
| `BindingTests/RuntimeTestsApp/Protocols/ProtocolExtensionClosureTests.cs` | 80 | inventory | Runtime test (Protocols): ProtocolExtensionClosureTests |
| `BindingTests/RuntimeTestsApp/Protocols/ProtocolExtOptionalClassParamTests.cs` | 61 | inventory | Runtime test (Protocols): ProtocolExtOptionalClassParamTests |
| `BindingTests/RuntimeTestsApp/Protocols/RefinedReturnProtocolTests.cs` | 76 | inventory | Runtime test (Protocols): RefinedReturnProtocolTests |
| `BindingTests/RuntimeTestsApp/Protocols/SiblingMethodDispatchTests.cs` | 380 | inventory | Runtime test (Protocols): SiblingMethodDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/SiblingPropertyDispatchTests.cs` | 596 | inventory | Runtime test (Protocols): SiblingPropertyDispatchTests |
| `BindingTests/RuntimeTestsApp/Protocols/SpiRequirementProtocolSkipTests.cs` | 80 | inventory | Runtime test (Protocols): SpiRequirementProtocolSkipTests |
| `BindingTests/RuntimeTestsApp/Protocols/StaticOnlyProtocolSkipTests.cs` | 78 | inventory | Runtime test (Protocols): StaticOnlyProtocolSkipTests |
| `BindingTests/RuntimeTestsApp/Protocols/SuppressedProxyChannelTests.cs` | 951 | inventory | Runtime test (Protocols): SuppressedProxyChannelTests |
| `BindingTests/RuntimeTestsApp/Protocols/URLProtocolReceiverTests.cs` | 53 | inventory | Runtime test (Protocols): URLProtocolReceiverTests |
| `BindingTests/RuntimeTestsApp/Protocols/ValueProviderPatternTests.cs` | 251 | inventory | Runtime test (Protocols): ValueProviderPatternTests |
| `BindingTests/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs` | 356 | inventory | Runtime test (Protocols): WitnessDispatchTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/CryptoKitSmokeTests.cs` | 142 | inventory | Runtime test (SmokeTests): CryptoKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/LiveCommunicationKitSmokeTests.cs` | 94 | inventory | Runtime test (SmokeTests): LiveCommunicationKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/MusicKitSmokeTests.cs` | 172 | inventory | Runtime test (SmokeTests): MusicKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/ProximityReaderSmokeTests.cs` | 91 | inventory | Runtime test (SmokeTests): ProximityReaderSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/RoomPlanSmokeTests.cs` | 88 | inventory | Runtime test (SmokeTests): RoomPlanSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/StoreKitSmokeTests.cs` | 357 | inventory | Runtime test (SmokeTests): StoreKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/TipKitSmokeTests.cs` | 226 | inventory | Runtime test (SmokeTests): TipKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/WeatherKitSmokeTests.cs` | 156 | inventory | Runtime test (SmokeTests): WeatherKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SmokeTests/WorkoutKitSmokeTests.cs` | 89 | inventory | Runtime test (SmokeTests): WorkoutKitSmokeTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/AsyncViewBridgeTests.cs` | 109 | inventory | Runtime test (SwiftUIBridge): AsyncViewBridgeTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/BridgeHelpers.cs` | 396 | inventory | Runtime test (SwiftUIBridge): BridgeHelpers |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/BridgeNativeMethods.cs` | 844 | inventory | Runtime test (SwiftUIBridge): BridgeNativeMethods |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/ClosureReturnBridgeTests.cs` | 157 | inventory | Runtime test (SwiftUIBridge): ClosureReturnBridgeTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/ModifierAndLifecycleTests.cs` | 456 | inventory | Runtime test (SwiftUIBridge): ModifierAndLifecycleTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/ObservableBindingTests.cs` | 140 | inventory | Runtime test (SwiftUIBridge): ObservableBindingTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/SimpleViewBridgeTests.cs` | 684 | inventory | Runtime test (SwiftUIBridge): SimpleViewBridgeTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/StateUpdateBridgeTests.cs` | 263 | inventory | Runtime test (SwiftUIBridge): StateUpdateBridgeTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/SwiftUIBridgeGeneratedApiTests.cs` | 250 | inventory | Runtime test (SwiftUIBridge): SwiftUIBridgeGeneratedApiTests |
| `BindingTests/RuntimeTestsApp/SwiftUIBridge/ValidationPatternBridgeTests.cs` | 508 | inventory | Runtime test (SwiftUIBridge): ValidationPatternBridgeTests |
| `BindingTests/RuntimeTestsApp/Types/AnyErrorResultReturnTests.cs` | 52 | inventory | Runtime test (Types): AnyErrorResultReturnTests |
| `BindingTests/RuntimeTestsApp/Types/AsyncClosurePropertySetterTests.cs` | 113 | inventory | Runtime test (Types): AsyncClosurePropertySetterTests |
| `BindingTests/RuntimeTestsApp/Types/CoreGraphicsCFTypeTests.cs` | 56 | inventory | Runtime test (Types): CoreGraphicsCFTypeTests |
| `BindingTests/RuntimeTestsApp/Types/NamespaceFacadeTests.cs` | 122 | inventory | Runtime test (Types): NamespaceFacadeTests |
| `BindingTests/RuntimeTestsApp/Types/ResultReturnTests.cs` | 66 | inventory | Runtime test (Types): ResultReturnTests |
| `BindingTests/RuntimeTestsApp/Types/SimdProjectionTests.cs` | 401 | inventory | Runtime test (Types): SimdProjectionTests |
| `BindingTests/RuntimeTestsApp/Types/SwiftUITextTests.cs` | 56 | inventory | Runtime test (Types): SwiftUITextTests |
| `BindingTests/RuntimeTestsApp/Types/TypealiasReturnTests.cs` | 59 | inventory | Runtime test (Types): TypealiasReturnTests |
| `BindingTests/RuntimeTestsApp/Wrappers/CdeclWrapperCohesionTests.cs` | 168 | inventory | Runtime test (Wrappers): CdeclWrapperCohesionTests |

## tools/SwiftInterfaceParser/Sources (*.swift)

**Files**: 18  
**LOC**: 6,074  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ActorIsolationWalker.swift` | 552 | inventory | SwiftSyntax interface fact walker: ActorIsolationWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/AvailabilityWalker.swift` | 1,225 | inventory | SwiftSyntax interface fact walker: AvailabilityWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/EnumFactsWalker.swift` | 335 | inventory | SwiftSyntax interface fact walker: EnumFactsWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ExtensionsWalker.swift` | 514 | inventory | SwiftSyntax interface fact walker: ExtensionsWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/main.swift` | 222 | inventory | SwiftSyntax interface fact walker: main |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/MainActorWalker.swift` | 240 | inventory | SwiftSyntax interface fact walker: MainActorWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/MarkerProtocolWalker.swift` | 117 | inventory | SwiftSyntax interface fact walker: MarkerProtocolWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/MemberCollectionWalker.swift` | 492 | inventory | SwiftSyntax interface fact walker: MemberCollectionWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ObjCRuntimeNamesWalker.swift` | 190 | inventory | SwiftSyntax interface fact walker: ObjCRuntimeNamesWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/Output.swift` | 166 | inventory | SwiftSyntax interface fact walker: Output |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ProtocolFactsWalker.swift` | 313 | inventory | SwiftSyntax interface fact walker: ProtocolFactsWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ProtocolNamesWalker.swift` | 67 | inventory | SwiftSyntax interface fact walker: ProtocolNamesWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/PublicTypeNamesWalker.swift` | 219 | inventory | SwiftSyntax interface fact walker: PublicTypeNamesWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/RegexShape.swift` | 133 | inventory | SwiftSyntax interface fact walker: RegexShape |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/SignatureFactsWalker.swift` | 643 | inventory | SwiftSyntax interface fact walker: SignatureFactsWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/SpiOnlyConformancesScanner.swift` | 205 | inventory | SwiftSyntax interface fact walker: SpiOnlyConformancesScanner |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/SubscriptLabelsWalker.swift` | 238 | inventory | SwiftSyntax interface fact walker: SubscriptLabelsWalker |
| `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ThrowsWalker.swift` | 203 | inventory | SwiftSyntax interface fact walker: ThrowsWalker |

## .claude/rules (*.md)

**Files**: 6  
**LOC**: 314  

| Path | LOC | Status | Purpose |
|------|----:|--------|---------|
| `.claude/rules/bindingtests.md` | 105 | inventory | BindingTests nuke targets and test attributes |
| `.claude/rules/constraints.md` | 48 | inventory | Load-bearing trap constraints for generator/runtime |
| `.claude/rules/csharp-files.md` | 31 | inventory | Copyright header conventions for C#/Swift |
| `.claude/rules/emitter.md` | 38 | inventory | Emitter architecture and projection rules |
| `.claude/rules/parser-marshaler.md` | 35 | inventory | Parser/marshaler patterns and gates |
| `.claude/rules/swiftui-bridge.md` | 57 | inventory | SwiftUI bridge detection and emission |

---

## Regeneration

```bash
python3 src/docs/deep-audit-2026-07/_seed_file_coverage_ledger.py
```

Wave 0 seed only writes this ledger. Later waves must **update statuses in place**, not re-seed blindly.

