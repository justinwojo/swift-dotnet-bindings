# Diagnostic Encyclopedia — SWIFTBIND* + SB*

| Field | Value |
|-------|--------|
| **Date** | 2026-07-16 |
| **Scope** | Codes emitted/referenced in `src/Swift.Bindings/src/**/*.cs`, `src/Swift.Bindings.Sdk/Sdk/*`, and `build/**/*.cs` (build only *mentions* some codes; no new emitters) |
| **Also** | Consumer-facing `SB000x` Obsolete IDs (emitter) + Roslyn `SB100x` (`src/Swift.Analyzers`) |
| **Mode** | Read-only inventory for deep-audit data pack / Track G1 |
| **Count** | **98** distinct diagnostic IDs documented below |

## Severity legend

| Tag | Meaning |
|-----|---------|
| **Hard** | MSBuild `<Error>` / `LogError` + non-zero exit / thrown exception that aborts generation or build |
| **Warn** | MSBuild `<Warning>` / `LogWarning` — build continues |
| **Soft** | Info log, member/type skip, Obsolete attribute, or eligibility reject — binding continues with reduced surface |
| **Strict** | Warn (or silent degrade) by default; escalates to **Hard** under `--strict-inputs` (SWIFTBIND027 rollup) |

## G1 relevance (day-1 package)

Product question from Track G1: *does this kill “drop xcframework → try it” for a new library?*

| Tag | Meaning |
|-----|---------|
| **Kill** | Blocks default `dotnet build` / pack for a normal day-1 binding project |
| **CondKill** | Kills only under specific config (mixed ObjC, pack, strict-inputs, `SwiftWrapperRequired`, explicit arches, etc.) |
| **Degrade** | Surface/fidelity loss; package still builds |
| **Integrity** | Must stay hard — shipping a lie (dangling EP, empty native, hook disconnect) |
| **N/A** | Internal contract / authoring-time only / analyzer advice |

---

## 1. SWIFTBIND — SDK configuration & discovery (`Sdk.targets`)

| Code | Meaning (one line) | Severity | Fires where | Recovery | G1 |
|------|-------------------|----------|-------------|----------|-----|
| **SWIFTBIND001** | No xcframework found (auto-discover empty). | **Hard** | `Sdk.targets` `_DiscoverSwiftFrameworks` | Add `<SwiftFramework Include="…xcframework"/>` or copy one into project dir | **Kill** |
| **SWIFTBIND002** | More than one xcframework in one project (v1 = one per project). | **Hard** | `Sdk.targets` discovery | Split into separate binding projects | **Kill** |
| **SWIFTBIND003** | Declared `SwiftFramework` path does not exist. | **Hard** | `Sdk.targets` discovery | Fix path | **Kill** |
| **SWIFTBIND004** | `SwiftFrameworkType=ObjC` without body `<IsBindingProject>true`. | **Hard** | `Sdk.targets` (SwiftFramework ObjC path) | Set `IsBindingProject` in csproj body (evaluation-time for bgen) | **CondKill** (ObjC only) |
| **SWIFTBIND005** | Project has `ObjcBindingApiDefinition` items but missing `IsBindingProject`. | **Hard** | `Sdk.targets` pre-bgen guard | Add `IsBindingProject` (+ `SwiftFrameworkType=ObjC` if using generator path) | **CondKill** |
| **SWIFTBIND010** | Unsupported TFM (not Apple platform net10.0-ios/macos/tvos/maccatalyst). | **Hard** | `Sdk.targets` `_ValidateSwiftBindingInputs` | Use Apple TFM | **Kill** |
| **SWIFTBIND011** | **(a)** AppleFrameworkTarget + SwiftFramework both set; **(b)** consumer TFM platform version below package min. | **Hard** (a) / **Warn** (b) | (a) `Sdk.targets`; (b) emitted `{PackageId}.targets` via `ConsumerTargetsEmitter` | (a) pick one mode; (b) raise `SupportedOSPlatformVersion` | **Kill** (a) / **Degrade** (b) |
| **SWIFTBIND012** | Cannot resolve Xcode SDK path via xcrun. | **Hard** | `Sdk.targets` Apple-framework resolve | Install Xcode / `xcode-select` | **Kill** |
| **SWIFTBIND013** | Named Apple framework not found under resolved SDK dir. | **Hard** | `Sdk.targets` | Fix framework name / Xcode version | **Kill** (Apple mode) |
| **SWIFTBIND014** | `.swiftinterface` missing for Apple Swift framework slice. | **Hard** | `Sdk.targets` | Wrong arch/platform or non-Swift framework | **Kill** (Apple Swift) |
| **SWIFTBIND015** | More than one `SwiftAppleFrameworkTarget` per project. | **Hard** | `Sdk.targets` | One framework per binding project | **Kill** |
| **SWIFTBIND016** | Cannot resolve platform version for Apple direct mode. | **Hard** | `Sdk.targets` | Set `SwiftAppleFrameworkPlatformVersion` or versioned TFM | **Kill** |
| **SWIFTBIND017** | Apple framework has neither Swift interface nor ObjC modulemap. | **Hard** | `Sdk.targets` | Unsupported layout / wrong SDK slice | **Kill** |
| **SWIFTBIND018** | ObjC-only Apple framework without body `SwiftFrameworkType=ObjC` + `IsBindingProject`. | **Hard** | `Sdk.targets` | Declare both properties in csproj body | **CondKill** |
| **SWIFTBIND019** | Declared `SwiftFrameworkType=ObjC` but framework has a Swift interface. | **Hard** | `Sdk.targets` | Remove ObjC override or choose ObjC-only framework | **CondKill** |
| **SWIFTBIND020** | xcframework version looks like Xcode placeholder `1.0`. | **Warn** | `Sdk.targets` pack metadata | Set `<PackageVersion>` explicitly | **Degrade** |
| **SWIFTBIND021** | **(a)** Apple ObjC path missing `IsBindingProject`; **(b)** dependency version placeholder / extract fail (generator). | **Hard** (a) / **Warn** (b) | (a) `Sdk.targets`; (b) `Program.cs` dependency metadata | (a) fix csproj; (b) set real version before publish | **CondKill** / **Degrade** |
| **SWIFTBIND030** | Pack with simulator+device platform but `SwiftWrapperArchitectures` not `all`. | **Hard** | `Sdk.targets` `_ValidateSwiftBindingPackSlices` | Set architectures to `all` before pack | **CondKill** (pack) |
| **SWIFTBIND031** | Wrapper xcframework missing required device/sim (or single-platform) slice on disk. | **Hard** | `Sdk.targets` pack slice validation | Fix source xcframework slices; or `IsPackable=false` for local-only | **CondKill** (pack) |
| **SWIFTBIND032** | Apple-framework second-slice compile left device or sim slice missing. | **Hard** | `Sdk.targets` pack slice validation | Inspect `_CompileAppleFrameworkSecondWrapperSlice` output; rebuild | **CondKill** (pack/Apple) |
| **SWIFTBIND035** | Cannot resolve versioned TFM for NuGet pack. | **Hard** | `Sdk.targets` `_ConfigureSwiftBindingPack` | Versioned TFM / workload / `SwiftAppleFrameworkPlatformVersion` | **CondKill** (pack) |
| **SWIFTBIND036** | `SwiftAppleSupplementPrototypeDir` outside intermediate output. | **Warn** | `Sdk.targets` | Prefer path under `$(IntermediateOutputPath)` | **N/A** |
| **SWIFTBIND037** | Pack would omit managed DLL (`IncludeBuildOutput=false` or missing `TargetPath`). | **Hard** | `Sdk.targets` pack | Ensure library output; don’t suppress build output | **CondKill** (pack) |
| **SWIFTBIND038** | **(a)** digester empty module dump (bad triple/version train); **(b)** pack staged zero native files while HasWrapper=True. | **Hard** both | `Sdk.targets` digester + pack | (a) per-platform Min*Version; (b) fix pack graph / clean rebuild | **CondKill** |
| **SWIFTBIND039** | Mixed binding metadata names ObjC companion but no companion assembly embedded/captured at pack. | **Hard** | `Sdk.targets` pack; also emitted standalone pack guard in `BindingProjectEmitter` | Rebuild companion; re-run generation | **CondKill** (mixed) |
| **SWIFTBIND040** | **Multi-site:** source xcframework dropped for static linkage but wrapper not referenced/packed; or `SwiftFrameworkDependency` missing PackageId/Version at pack. | **Hard** | `Sdk.targets` (pack, SDK-direct ref, ProjectReference GetNativeManifest, dep metadata) | Clean intermediate + rebuild wrapper; fix dep metadata + PackageReference | **CondKill** / **Integrity** |
| **SWIFTBIND041** | Mixed metadata names companion but SDK-direct compile has no companion assembly to reference. | **Hard** | `Sdk.targets` `_ReferenceMixedObjCCompanion` | Emit/build companion; clean intermediate | **CondKill** (mixed path b) |
| **SWIFTBIND042** | ProjectReference consumer: companion csproj present but GetTargetPath empty. | **Hard** | Emitted `{PackageId}.ProjectReference.targets` (`ConsumerTargetsEmitter`) | Rebuild referenced binding so companion builds | **CondKill** (mixed path c) |
| **SWIFTBIND043** | Wrapper xcframework Mach-O missing/corrupt; device/sim merge cannot proceed. | **Hard** | `Sdk.targets` Apple second-slice merge | Delete intermediate; raise `SwiftWrapperCompileTimeoutSeconds` | **CondKill** / **Integrity** |
| **SWIFTBIND044** | Apple auto-dep injected as unbounded ProjectReference (cross-train nuspec hazard). | **Warn** (escalatable via `WarningsAsErrors`) | `Sdk.targets` Apple auto-dep | Prefer PackageReference with version range | **Degrade** |
| **SWIFTBIND051** | Wrapper compilation failed / HasWrapper false while `SwiftWrapperRequired` (default true → Error; false → Warning). | **Hard** default / **Warn** if opt-out | `Sdk.targets` `_ValidateSwiftWrapperCompilation` | Fix swiftc / deps; or set `SwiftWrapperRequired=false` (runtime DllNotFound risk) | **Kill** (default) — G1 contested |
| **SWIFTBIND052** | **Multi:** explicit arch missing from source (CLI hard); SwiftUI bridge compile/incomplete slice dropped (non-fatal to main). | **Hard** (explicit arch CLI) / **Warn** (bridge / incomplete) | Generator `Program.cs`/`BindingsGeneratorCommand`; `BridgeBuildOutcome`; `Sdk.targets` bridge paths | Provide arch slice; bridge views may DllNotFound until fixed | **CondKill** / **Degrade** |
| **SWIFTBIND053** | **(a)** interrupted second-slice swap recovery (`.superseded`); **(b)** merge throws if plist/binary/lipo/ditto fail. | **Warn** (SDK recovery) / **Hard** (merger throw) | `Sdk.targets` recovery; `WrapperXCFrameworkMerger.cs` | Next out-of-date rebuild completes fat slice; clean if stuck | **Degrade** / **CondKill** |
| **SWIFTBIND056** | Explicit `SwiftTargetArchitectures` / `--target-architectures` slice(s) not folded into fat wrapper. | **Hard** (even SDK mode) | Generator `WrapperBuildOutcome`; SDK `_ValidateSwiftWrapperCompilation` | Fix per-arch compile/timeout; or use `auto` | **CondKill** / **Integrity** |
| **SWIFTBIND060** | **(a)** SDK: N of M types skipped; **(b)** CLI: unresolved dependency xcframework / missing slice guidance. | **Warn** | (a) `Sdk.targets` post-gen; (b) `Program.cs` FormatDependencyWarning | binding-report.json; add ProjectReference / SwiftFrameworkDependency | **Degrade** |
| **SWIFTBIND061** | **(a)** SDK: N of M members skipped; **(b)** generator: reverse-dispatch receiver degraded to fail-fast (suppressed proxy). | **Warn** | (a) `Sdk.targets`; (b) `EmissionReportEmitter` | report + proxy policy; member still ships as stub | **Degrade** |
| **SWIFTBIND062** | `_GenerateSwiftBindings` hook did not run (disconnected BeforeTargets). | **Hard** | `Sdk.targets` `_AssertSwiftBindingHookWiring` | Re-anchor target in Sdk.targets | **Integrity** |
| **SWIFTBIND063** | `_ResolveSwiftAutoDetectedDependencies` hook did not run. | **Hard** | same | Re-anchor hook | **Integrity** |
| **SWIFTBIND064** | `_ImportSwiftBindingMetadata` hook did not run though metadata exists. | **Hard** | same | Re-anchor hook | **Integrity** |
| **SWIFTBIND065** | Apple-framework generation hook `_GenerateSwiftBindingsAppleFramework` disconnected. | **Hard** | same | Re-anchor hook | **Integrity** |
| **SWIFTBIND073** | **(a)** SDK: ModuleDatabasePath missing on disk; **(b)** generator: failed parse of dependency ABI. | **Warn** (a) / **Hard** (b) | `Sdk.targets`; `Program.cs` | Build dependency first / fix ABI; remove bad ModuleDatabasePath | **Degrade** / **CondKill** |
| **SWIFTBIND080** | Auto-detected cross-module dep with no sibling binding project (guidance for ProjectReference or NuGet pair). | **Warn** | `Sdk.targets` + `AutoDepResolver` WARN lines | Add ProjectReference or SwiftFrameworkDependency+PackageReference | **Degrade** |
| **SWIFTBIND100** | `SwiftPackage` items not supported (use pre-built xcframework). | **Hard** | `Sdk.targets` | Use `SwiftFramework` | **Kill** if misconfigured |

---

## 2. SWIFTBIND — Generator input resolution, parse, registry

| Code | Meaning (one line) | Severity | Fires where | Recovery | G1 |
|------|-------------------|----------|-------------|----------|-----|
| **SWIFTBIND022** | Custom global-actor-isolated type: skip sync ctor / default-param overloads (no sync entry into custom actor). | **Soft** (`LogInformation` + member skip) | `MethodHandler`, `DefaultParameterOverloadEmitter`, `WrapperValidation` | Use async factory if available; construct in Swift | **Degrade** |
| **SWIFTBIND023** | Protocol existential degraded to `object` (lost static fidelity). | **Warn** | `EmissionReportEmitter` / existential recorders | Prefer concrete types; see `degradedExistentials` in report | **Degrade** |
| **SWIFTBIND024** | Type-registry collision (keep-existing ignore / overwrite kind or content). | **Soft**/`LogInformation` or **Warn** | `ModuleDatabase.Register` | Fix double-register sources | **N/A** (internal) |
| **SWIFTBIND025** | Declaration left unbound as `// Unsupported:` comment-drop. | **Warn** | `EmissionReportEmitter` ← `UnsupportedCommentEmitter` / ReportCollector | See `unsupportedCommentDrops` in report | **Degrade** |
| **SWIFTBIND026** | Swift type projected to bare `object` without `[UnsupportedSwiftType]`. | **Warn** | `EmissionReportEmitter` / closure & signature paths | See `objectDegradations` in report | **Degrade** |
| **SWIFTBIND027** | Input-resolution degradation summary under `--strict-inputs` (categories: AbiJson, slice, toolchain, dep, …). | **Hard** only with `--strict-inputs`; otherwise underlying warns | `BindingsGeneratorCommand` | Fix degraded inputs listed in prior SWIFTBIND027/033/034/… lines | **CondKill** (CI/strict) |
| **SWIFTBIND028** | ObjC native-symbol probe systemic failure (`nm` all failed). | **Hard** (throw) | `ObjCPipeline.FilterToNativeSymbolBackedClasses` | Fix toolchain / `nm` | **CondKill** (mixed/ObjC) |
| **SWIFTBIND029** | Clang AST empty (systemic) **or** unrecognized top-level node kinds. | **Hard** (empty dump throw) / **Warn** (novel kinds) | `ClangAstParser` | Fix umbrella compile; teach KnownTopLevelNodeKinds | **CondKill** / **Degrade** |
| **SWIFTBIND033** | ABI JSON missing or mismatched `json_format_version`. | **Warn** + **Strict** degradation | `SwiftABIParser.GateAbiFormatVersion` | Matching digester/Xcode; strict fails | **CondKill** under strict |
| **SWIFTBIND034** | Unrecognized ABI node kind dropped. | **Warn** + **Strict** | `SwiftABIParser` dispatch default | Teach parser allowlist | **CondKill** under strict |
| **SWIFTBIND045** | Type registry frozen; structural Register/UpdateTypeRecord after Freeze. | **Hard** (throw) | `ModuleDatabase` / `TypeDatabase` | Generator bug — fix registration order | **Integrity** |
| **SWIFTBIND046** | Load-bearing ABI field absent; declaration dropped. | **Warn** + **Strict** | `SwiftABIParser` (AbiRecordDroppedException) | Digester/parser shape fix | **CondKill** under strict |
| **SWIFTBIND047** | Conformance registration attempted for open-generic type (Mono-unsafe). | **Hard** (throw) | `ModuleHandler` | Recorder must skip open generics | **Integrity** |
| **SWIFTBIND048** | `SuppressPayloadFinalizer` targets unknown payload field. | **Hard** (throw) | `FinalizerSeamEmitter` | Use `_handle` or `_payload` only | **Integrity** |
| **SWIFTBIND049** | Member skipped for `AbsentFrameworkType` (would CS0234). | **Warn** | `EmissionReportEmitter` | Bind dependency frameworks; report skippedItems | **Degrade** |
| **SWIFTBIND050** | Wrapper compile fail or all blocks stripped (SDK mode soft); also XCFrameworkSlicer I/O/RID failures use this code in messages. | **Warn**/exit 0 in SDK mode; CLI may Fatal; slicer **Hard** throws | `Program.HandleWrapperCompilationOutcome`, `XCFrameworkSlicer` | See swiftc stderr; fix deps; raise timeout | **CondKill** via 051 default |
| **SWIFTBIND054** | Dropped ObjC classes with no `_OBJC_CLASS_$_` in any binary. | **Warn** | `ObjCPipeline` | Review over-bound headers | **Degrade** |
| **SWIFTBIND055** | **(a)** Xcode major outside calibrated envelope / unqueryable; **(b)** dropped free C symbols with no native export. | **Warn**; (a) also **Strict** if out of range | `SupportedToolchain`; `ObjCPipeline` free-symbol filter | Use supported Xcode; ignore unexported helpers | **CondKill** under strict (a) / **Degrade** (b) |
| **SWIFTBIND058** | Demangle reduction missed undocumented node kind(s). | **Warn** | `Program.cs` + `ReductionDiagnostics` | Teach demangler rules; corpus tests | **Degrade** |
| **SWIFTBIND070** | `--module-database` path does not exist (CLI). | **Hard** | `BindingsGeneratorCommand` | Fix path | **CondKill** (CLI deps) |
| **SWIFTBIND071** | Module database targets current module — skipped. | **Soft** (`LogInformation`) | `Program.cs` | Remove self-DB from list | **N/A** |
| **SWIFTBIND072** | Invalid or failed load of module database XML. | **Hard** | `Program.cs` | Fix/regenerate Database.xml | **CondKill** |
| **SWIFTBIND101** | Static archive xcframework / static binary not supported for Swift binding. | **Hard** (throw/message) | `XCFrameworkResolver` | Use dynamic framework or ObjC tools | **Kill** (static inputs) |
| **SWIFTBIND102** | No Swift module found in xcframework. | **Hard** | `XCFrameworkResolver` | ObjC-only or no library evolution | **Kill** |
| **SWIFTBIND103** | `swift-frontend` ABI extraction from interface failed. | **Hard** | `XCFrameworkResolver` | Install SDK; resolve companion modules; fix interface | **Kill** |
| **SWIFTBIND104** | Failed to enumerate symbols from static archive (`nm`). | **Hard** | `XCFrameworkResolver` | `xcode-select --install` | **CondKill** |
| **SWIFTBIND105** | Swift type-ownership manifest schema version mismatch. | **Hard** (throw) | `SwiftTypeOwnershipManifest` | Regenerate with current generator | **Integrity** |
| **SWIFTBIND106** | Ownership manifest not parseable JSON. | **Hard** (throw) | same | Regenerate; fix corruption | **Integrity** |
| **SWIFTBIND107** | Throwing property getter rejected for `@_cdecl` property wrapper (no try/catch). | **Soft** (eligibility reject reason string; skip/wrapper fallthrough) | `PropertyWrapperEmitter.GetRejectionReason` | Member may still emit via alternate path or skip; not an MSBuild Error | **Degrade** |
| **SWIFTBIND108** | Post-emit integrity: C# EntryPoint refs wrapper symbol Swift never defined. | **Hard** (`LogError`, generation fails) | `WrapperSymbolIntegrityGate` | Generator defect — skip member at plan time or emit wrapper | **Integrity** |

---

## 3. SWIFTBIND — Post-generation ABI contract checker (non-fatal)

Runs in `ModuleEmitter` via `AbiContractChecker.Validate` — logs **warnings only**; does not abort generation by itself.

| Code | Meaning (one line) | Severity | Fires where | Recovery | G1 |
|------|-------------------|----------|-------------|----------|-----|
| **SWIFTBIND090** | CC-001: non-blittable param on CallConvSwift P/Invoke. | **Warn** | `AbiContractChecker` | File issue; expect runtime failure | **Degrade** (compile-but-dead risk) |
| **SWIFTBIND091** | CC-002: non-blittable return on CallConvSwift. | **Warn** | same | same | **Degrade** |
| **SWIFTBIND092** | Tj cross-module: dispatch thunk mangling vs target library mismatch. | **Warn** | same | same | **Degrade** |
| **SWIFTBIND093** | CC-003: `SBW_*` cdecl entry targets original library, not wrapper. | **Warn** | same | same | **Degrade** |
| **SWIFTBIND094** | CC-004: CallConvCdecl on mangled `$s…` Swift symbol. | **Warn** | same | same | **Degrade** |

---

## 4. SB000x — Consumer-facing Obsolete diagnostic IDs (emitted C#)

These are **not** MSBuild codes. They appear as `[Obsolete(..., DiagnosticId = "SBxxxx")]` on generated APIs. SDK `Sdk.props` default-suppresses **SB0001–SB0004** in the binding project itself (`NoWarn`); consumers still see them unless suppressed. Direct interop mode can suppress SB0001 via consumer targets.

| Code | Meaning (one line) | Severity | Fires where | Recovery | G1 |
|------|-------------------|----------|-------------|----------|-----|
| **SB0001** | JIT/CC risk: no `@_cdecl` wrapper/native thunk (and non-blittable path); may be unsafe on Mono. | **Warn** Obsolete (+ often `EditorBrowsable(Never)`) | `MethodHandler` / `WrapperEmitter.Signature` safety attributes | Prefer NativeAOT Direct; fix wrapper emission; don’t call from Mono if obsolete | **Degrade** (API still present) |
| **SB0002** | Missing exported symbol and/or silent tombstone/opaque return — always-relevant. | **Warn** Obsolete | same + `SilentTombstoneRegistrar` path | Don’t call; check exports / type DB | **Degrade** |
| **SB0003** | Protocol proxy member non-dispatchable — throws `NotSupportedException` if called on protocol-typed values. | **Warn** Obsolete | `ProtocolProxyEmitter.InterfaceImpl` | Use concrete type / supported witness path | **Degrade** (compile-but-dead) |
| **SB0004** | Protocol interface empty — all declared members skipped (no bases). | **Warn** Obsolete on interface | `ProtocolHandler` | Expect empty contract; check report | **Degrade** |
| **SB0005** | Closure-param tombstone: visible API, `object?` param, throws if invoked. | **Warn** Obsolete | `ClosureParamTombstoneEmitter` | No C# closure bridge yet | **Degrade** |
| **SB0006** | Suppressed-proxy read poison: `[Obsolete(error: true)]` — **compile error** on produce/read paths. | **Hard** at consumer compile (`error: true`) | `WrapperEmitter.EmitSuppressedProxyReadPoison` | Use setters/consume paths only; wait for proxy rescue | **Degrade** surface honesty (not package kill) |
| **SB0007** | **Not currently emitted** (unit tests assert absence on DIM throw stubs). Reserved / retired ID. | — | tests only | N/A | **N/A** |

---

## 5. SB100x — Roslyn analyzers (`src/Swift.Analyzers`)

| Code | Meaning (one line) | Severity | Fires where | Recovery | G1 |
|------|-------------------|----------|-------------|----------|-----|
| **SB1001** | `ISwiftObject` local not disposed (deterministic cleanup guidance). | **Info** | `SwiftObjectDisposeAnalyzer` | `using` / `Dispose` / `SwiftDisposeScope` | **N/A** (consumer hygiene) |
| **SB1002** | Callback/lambda captures the Swift object it is attached to (possible retain cycle). | **Warn** | `SwiftRetainCycleAnalyzer` | Capture `WeakSwiftReference<T>` | **N/A** |

Analyzer release notes: `src/Swift.Analyzers/AnalyzerReleases.Unshipped.md`.

---

## 6. Codes referenced only (no emitter in scoped sources)

| Code | Notes |
|------|--------|
| — | `build/**/*.cs` only **mentions** SWIFTBIND027/032/050/051/052 in comments and log-parse helpers (`Build.Validation.cs`, `Build.BindingTests.cs`, `Build.X64SimGate.cs`). No new diagnostic emitters. |
| — | Number gaps **006–009, 057, 059, 066–069, 074–079, 081–089, 095–099, 109+** (except 100–108 family) have **no** production emitters in the searched trees. |

---

## 7. Dual-use / overloaded IDs (audit hazard)

| Code | Distinct meanings |
|------|-------------------|
| **SWIFTBIND011** | SDK mutual-exclusion Error vs consumer platform-version Warning |
| **SWIFTBIND021** | SDK IsBindingProject Error vs generator dependency-version Warning |
| **SWIFTBIND038** | Digester empty dump vs pack empty native content |
| **SWIFTBIND040** | Source-drop/wrapper invariant (build/pack/ProjectRef) vs missing SFD PackageId/Version |
| **SWIFTBIND050** | Soft wrapper give-up vs hard slicer/archive errors |
| **SWIFTBIND052** | Hard explicit-arch CLI vs soft bridge degradation |
| **SWIFTBIND053** | Soft `.superseded` recovery vs hard merge exceptions |
| **SWIFTBIND055** | Toolchain envelope vs ObjC free-symbol drops |
| **SWIFTBIND060** | Type-skip count (SDK) vs dependency-missing (CLI) |
| **SWIFTBIND061** | Member-skip count (SDK) vs reverse-dispatch fail-fast (generator) |
| **SWIFTBIND073** | Missing ModuleDatabasePath (SDK warn) vs dep ABI parse fail (generator error) |

---

## 8. G1 map (package-level)

### Default day-1 **Kill** (typical Swift xcframework + `SwiftBindings.Sdk`)

| Cluster | Codes |
|---------|-------|
| Input missing / TFM | 001–003, 010, 100 |
| Primary resolve / digester (Apple mode) | 012–017, 038(a) |
| **Wrapper required (default true)** | **050 → 051** |
| Integrity post-emit | **108** |
| Static / no Swift module / ABI extract | 101–103 |

### Contested G1 (usability vs integrity)

| Policy | Codes | Note from Track G1 |
|--------|-------|---------------------|
| Wrapper compile fail aborts whole build | 050/051 | Managed surface may still be useful; default is package hard-fail |
| Mixed ObjC systemic fail aborts before Swift | 028, 029(empty) | Prevents silent ObjC drop; blocks Swift-only try |
| Explicit fat-arch contract | 056, 052(explicit) | Correct integrity; rare day-1 |
| Pack-only honesty | 030–032, 035, 037–040, 039 | Kill pack not local build |

### Stay **Degrade** (partial success)

| Cluster | Codes |
|---------|-------|
| Skip counts / fidelity | 023, 025, 026, 049, 060, 061, SB0001–5 |
| Actor / bridge soft | 022, 052(bridge), 053(recovery) |
| Unresolved auto-dep | 080, 044 |
| ABI checker | 090–094 |

### Must stay **Integrity Hard**

| Codes | Why |
|-------|-----|
| 062–065 | Silent no-generation |
| 039–042, 040 (drop invariant) | Mixed / native-less package lies |
| 056 | Requested arch not delivered |
| 108 | Dangling EntryPoint |
| 045, 047, 048, 105, 106 | Internal contract violations |

---

## 9. Recovery cheat-sheet (operators)

| Symptom | First codes to search | Action |
|---------|----------------------|--------|
| Empty binding / no gen | 062, 065, 001 | Hook wiring / framework item |
| `DllNotFoundException` wrappers | 050, 051, 040 | Wrapper required, clean intermediate |
| Fat sim x86_64 missing | 056, 052 | arches / timeout |
| CS0246 ObjC types | 041, 042, 039, 004/005/018/021 | Companion + IsBindingProject |
| App Store / pack native empty | 037, 038(b), 031, 040 | Pack graph + slices |
| Existential/`object` | 023, 026 | report degradations |
| Always-throw proxy reads | SB0006 | compile-poison intentional |
| Mono CallConvSwift crash | SB0001 | Direct mode / wrapper |

---

## 10. Count summary

| Family | Count |
|--------|------:|
| SWIFTBIND* with emitters/messages | **87** |
| SB0001–SB0007 (incl. reserved 0007) | **7** |
| SB1001–SB1002 | **2** |
| **Total distinct IDs documented** | **98** |

*Sources grepped: `src/Swift.Bindings/src/**/*.cs`, `src/Swift.Bindings.Sdk/Sdk/*`, `src/Swift.Analyzers/*.cs`, `build/**/*.cs` mentions only.*
