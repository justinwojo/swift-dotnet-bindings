# Data Pack — CLI Surface + Static/Ambient State Inventory

**Status**: Exhaustive inventory (read-only)  
**Sources**: `CliOptions.cs`, `BindingsGeneratorCommand.cs`, `Program.cs` / `BindingsGenerator`, ambient collectors, `Sdk.props` / `Sdk.targets`  
**Date**: 2026-07-16

---

## Summary counts

| Surface | Count |
|---------|------:|
| **CLI options** (`CliOptions` properties registered on root command) | **64** |
| **Ambient / process-global collectors** (static / ThreadStatic / AsyncLocal mutable sinks) | **7** |
| **Early-out CLI command modes** (return before full generate) | **10** (+ default full-generate path) |

---

## 1. CLI options (`src/Swift.Bindings/src/CliOptions.cs`)

All options are registered on a single `RootCommand` via `CreateRootCommand()`. There are no subcommands — modes are boolean/string flags that early-out inside `BindingsGeneratorCommand.Execute`.

### 1.1 Inputs & output

| Option | Aliases | Purpose |
|--------|---------|---------|
| `SwiftAbi` | `-a`, `--swiftabi` | Path to Swift ABI JSON. |
| `Dylib` | `-d`, `--dylib` | Path to dynamic library (metadata / DllImport base). |
| `Tbd` | `-t`, `--tbd` | Path to TBD file. |
| `OutputDirectory` | `-o`, `--output` | Output directory for generated bindings (also staged xcframework / Apple manifests). |
| `XCFramework` | `--xcframework` | Path to `.xcframework`; auto-resolves ABI/dylib/TBD/swiftinterface. Mutually exclusive with `-a`/`-d`/`-t`. |
| `SwiftInterface` | `-s`, `--swiftinterface` | `.swiftinterface` for `@inlinable` / `@usableFromInline internal` detection. |
| `SymbolGraph` | `--symbolgraph` | Symbol-graph JSON (file or dir) for Swift→C# XML docs. |
| `NoDocs` | `--no-docs` | Disable auto symbol-graph extraction (does not block explicit `--symbolgraph`). |
| `BridgeHints` | `--bridge-hints` | JSON customizing SwiftUI bridge generation. |
| `Config` | `--config` | Config JSON path; default `.swiftbindings.json` in CWD. |

### 1.2 Platform / library naming

| Option | Aliases | Purpose |
|--------|---------|---------|
| `Platform` | `--platform` | Apple platform: `ios` (default), `macos`, `tvos`, `maccatalyst`. |
| `PlatformVersion` | `--platform-version` | Explicit TPV (e.g. `26.2`) for emitted TFM / pack paths. Required when packing with a published `--swift-runtime-version`. |
| `PlatformTarget` | `--platform-target` | xcframework slice: `simulator` (default) or `device`. |
| `LibraryName` | `-l`, `--library-name` | DllImport library name; `\@rpath/...` escape for System.CommandLine response files. |
| `AsyncLibrary` | `--async-library` | Separate dylib name for async wrappers (manual mode). |
| `NamespacePattern` | `--namespace-pattern` | C# namespace pattern (`{Module}`, `{Framework}`). |

### 1.3 Packaging / SDK integration

| Option | Aliases | Purpose |
|--------|---------|---------|
| `SdkMode` | `--sdk-mode` | Skip `.csproj` emission (SDK *is* the project system). |
| `PackageId` | `--package-id` | NuGet package ID override (default `{Module}.Swift.iOS`-style). |
| `AssemblyName` | `--assembly-name` | Assembly name for ILLink descriptor (SDK mode uses consumer `$(AssemblyName)`). |
| `SwiftRuntimeVersion` | `--swift-runtime-version` | `SwiftBindings.Runtime` version stamped into csproj (`0.0.0-dev` = local sentinel / non-packable). |
| `AppleSupplementPrototypeDir` | `--apple-supplement-prototype-dir` | Emit trimmed `SwiftBindings.Apple.Prototype` + ProjectReference instead of PackageReference. |
| `AppleVersion` | `--apple-version` | Apple SDK train / supplement version (default `26.0.0`); PackageReference floor + metadata. |

### 1.4 Wrapper compile control

| Option | Aliases | Purpose |
|--------|---------|---------|
| `WrapperArchitectures` | `--wrapper-architectures` | Slice scope: `simulator` (default), `device`, or `all`. |
| `TargetArchitectures` | `--target-architectures` | CPU archs: `auto` / `arm64` / `arm64,x86_64` (lipo fat wrapper). |
| `SkipWrapperCompilation` | `--skip-wrapper-compilation` | Emit C# + Swift source only; SDK compiles wrapper later. |
| `SkipThunkCompilation` | `--skip-thunk-compilation` | Skip native thunk `.s` assemble/link. |
| `CompileWrapperOnly` | `--compile-wrapper-only` | Early-out mode: compile existing `.swift` wrappers; no parse/emit. |
| `CompileBridgeOnly` | `--compile-bridge-only` | Early-out mode: compile existing `.SwiftUIBridge.swift` only. |

### 1.5 Dependencies & resolution policy

| Option | Aliases | Purpose |
|--------|---------|---------|
| `FrameworkDependency` | `--framework-dependency` | Repeatable dependency xcframework path (`-F` + PackageReference). Requires `--xcframework`. |
| `LinkFramework` | `--link-framework` | Repeatable system framework for wrapper link (static-archive sources). Requires `--xcframework`. |
| `LinkLibrary` | `--link-library` | Repeatable system library (`-lname`) for wrapper link. Requires `--xcframework`. |
| `ModuleDatabase` | `--module-database` | Repeatable dependency module-database XML for cross-module types. |
| `NoAutoDetect` | `--no-auto-detect` | Disable binary-linkage auto dependency detection. |
| `StrictInputs` | `--strict-inputs` | Fail-closed (SWIFTBIND027) on input-edge degradations (Finding 50 / CI compile gate). |
| `KeepBuiltinDatabase` | `--keep-builtin-database` | Disable Apple-framework target-mode auto-skip of colliding built-in XML stubs. |
| `ObjC` | `--objc` | Force ObjC binding pipeline (else auto-detect). |

### 1.6 Early-out utility modes (SDK / tooling)

| Option | Aliases | Purpose |
|--------|---------|---------|
| `DetectAppleCrossModuleDeps` | `--detect-apple-cross-module-deps` | Parse swiftinterface imports → `MODULE\|PACKAGE_ID\|VERSION_RANGE` stdout for Apple-framework PackageReference injection. |
| `SliceXcframework` | `--slice-xcframework` | Stage RID-pruned xcframework copy (`--xcframework` + `--rid` + `-o`). |
| `Rid` | `--rid` | NuGet RID for `--slice-xcframework` (e.g. `ios-arm64`). |
| `ResolveAutoDeps` | `--resolve-auto-deps` | Resolve auto-deps → `PROJREF|` / `WARN|` lines for SDK `_ResolveSwiftAutoDetectedDependencies`. |
| `AutoDepSpec` | `--auto-dep-spec` | Percent-encoded auto-dep records for resolve mode. |
| `ExplicitDeps` | `--explicit-deps` | Modules already declared via `SwiftFrameworkDependency` (dedup for resolve mode). |

### 1.7 Apple types manifest pipeline

| Option | Aliases | Purpose |
|--------|---------|---------|
| `EmitAppleTypesManifest` | `--emit-apple-types-manifest` | Ingest Apple ABI dumps → `manifest.json` at `-o`. |
| `AppleAbiJson` | `--apple-abi-json` | Repeatable Apple SDK ABI dump paths. |
| `AppleIncludeTypes` | `--apple-include-types` | Positive-list `include-types.json` of Swift identities. |
| `AppleSdkTrainMajor` | `--apple-sdk-train-major` | Optional sdk_train.major override (else from `--apple-version`). |
| `AppleSdkTrainLabel` | `--apple-sdk-train-label` | Free-form `sdk_train.label`. |
| `AppleSdkMinIos` | `--apple-sdk-min-ios` | `sdk_train.platforms.ios`. |
| `AppleSdkMinMaccatalyst` | `--apple-sdk-min-maccatalyst` | `sdk_train.platforms.maccatalyst`. |
| `AppleSdkMinTvos` | `--apple-sdk-min-tvos` | `sdk_train.platforms.tvos`. |
| `AppleSdkMinMacos` | `--apple-sdk-min-macos` | `sdk_train.platforms.macos`. |
| `EmitAppleTypesCs` | `--emit-apple-types-cs` | Manifest → C# sources for `SwiftBindings.Apple`. |
| `AppleTypesManifest` | `--apple-types-manifest` | Path to manifest for emit-cs / validate modes. |
| `AppleTypesSequentialLayoutWhitelist` | `--apple-types-sequential-layout-whitelist` | Optional sequential-layout whitelist JSON. |
| `AllowPartialAppleTypesManifest` | `--allow-partial-apple-types-manifest` | Dev opt-in: allow unmatched include-types identities. |
| `ValidateAppleTypesManifest` | `--validate-apple-types-manifest` | Live-SDK VWT probe of every host-platform manifest entry. |
| `AppleTypesManifestWriteBack` | `--apple-types-manifest-write-back` | With validate: write probed size/align/stride back into manifest. |

### 1.8 Stdlib conformances tooling

| Option | Aliases | Purpose |
|--------|---------|---------|
| `RegenStdlibConformances` | `--regen-stdlib-conformances` | Verify/prune `stdlib-conformances.json` vs digester dump. |
| `StdlibDump` | `--stdlib-dump` | `swift-api-digester -dump-sdk -module Swift` JSON path. |
| `StdlibConformances` | `--stdlib-conformances` | Path to fact table JSON. |
| `StdlibConformancesWriteBack` | `--stdlib-conformances-write-back` | With regen: rewrite table in place. |

### 1.9 Diagnostics / producer selection

| Option | Aliases | Purpose |
|--------|---------|---------|
| `InterfaceFactsProducer` | `--interface-facts-producer` | `auto` / `swift-syntax` (both shell out to SwiftInterfaceParser; macOS-only; no regex fallback). |
| `Verbose` | `-v`, `--verbose` | Log level: 0=none, 1=info (default), 2=debug. |
| `Help` | `-h`, `--help` | Print custom help and exit. |

**Option count: 64.**

---

## 2. Early-out command modes (`BindingsGeneratorCommand.Execute`)

Not `ICommand` interfaces — the root handler branches on flags **before** platform validation / full generate. Order below matches source control flow.

| # | Gate | Inputs required | Exit surface |
|---|------|-----------------|--------------|
| 1 | `--help` | none | `PrintHelp()` |
| 2 | `--detect-apple-cross-module-deps` | swiftinterface path; `--apple-version` | stdout dep edges; exit 0/1 |
| 3 | `--slice-xcframework` | `--xcframework`, `--rid`, `-o` | staged xcframework; exit 0/1 |
| 4 | `--resolve-auto-deps` | `--auto-dep-spec` (± `--explicit-deps`) | `PROJREF|` / `WARN|` stdout |
| 5 | `--emit-apple-types-manifest` | `-o`, `--apple-abi-json`, `--apple-include-types` | `AppleTypesManifestCommand.Run` |
| 6 | `--emit-apple-types-cs` | `-o`, `--apple-types-manifest` (± whitelist) | `AppleTypesCsCommand.Run` |
| 7 | `--validate-apple-types-manifest` | `--apple-types-manifest` (± write-back) | `AppleTypesManifestValidateCommand.Run` |
| 8 | `--regen-stdlib-conformances` | `--stdlib-dump`, `--stdlib-conformances` (± write-back) | `StdlibConformancesRegenCommand.Run` |
| 9 | `--compile-wrapper-only` | `--xcframework`, `-o` (+ arch/deps flags) | `BindingsGenerator.RunCompileWrapperOnly` |
| 10 | `--compile-bridge-only` | `--xcframework`, `-o` | `BindingsGenerator.RunCompileBridgeOnly` |
| — | **default** | `--xcframework` **or** `-a`+`-d`+`-t`, and `-o` | full parse → emit → optional wrapper/bridge compile |

**Within the default path (not early-out of the process, but mode forks):**

- `--objc` forces pure-ObjC resolution (skip Swift primary).
- Mixed Swift+ObjC companion resolution when both surfaces exist.
- `--sdk-mode` suppresses csproj emission; still generates C# + metadata.
- `--skip-wrapper-compilation` defers wrapper compile (SDK two-pass).

**Mutually exclusive / fail-closed checks of note:**

- `--skip-wrapper-compilation` × `--compile-wrapper-only`
- `--xcframework` × `-a`/`-d`/`-t`
- `--link-framework` / `--link-library` without `--xcframework`
- `--platform-version` required when `--swift-runtime-version` is a published (non-sentinel) value — enforced **after** compile-only modes return

---

## 3. Ambient / static state inventory

These are the process- or thread-scoped mutable sinks that **must be Reset** (or session-scoped) so multi-module / parallel tests / sequential CLI modes do not leak.

### 3.1 Primary collectors (audit focus)

| # | Type | Location | Isolation | Reset | Role |
|---|------|----------|-----------|-------|------|
| 1 | **`ReportCollector`** | `Reporting/ReportCollector.cs` | `lock` + **`AsyncLocal<bool> SessionActive`** + static sets/dicts | `Start` / `Complete` / `Reset` | Binding report: emitted/skipped/synthesized members, type skips, bridge summary, recovered CSM members, SWIFTBIND025/026 ambient drops, ObjC-prefix bridges |
| 2 | **`SwiftUIBridgeCollector`** | `Emitter/StringEmitter/SwiftUIBridgeCollector.cs` | `lock` + static `List`/`HashSet` | `Reset` (before/after module emit) | Accumulates SwiftUI `View` types for `SwiftUIBridgeEmitter` |
| 3 | **`AppleSupplementReferences`** | `TypeDatabase/AppleSupplementReferences.cs` | **`[ThreadStatic]`** `Dictionary<identity, SortedSet<hint>>` | `Reset` per module | Records identities resolved to `SwiftBindings.Apple` (+ provenance hints → artifact manifest → csproj PackageReference) |
| 4 | **`InputResolutionReport`** | `Reporting/InputResolutionReport.cs` | **`[ThreadStatic]`** decision list | `Reset` at start of each generation (`BindingsGeneratorCommand`) | Finding 50: slice/arch/artifact/dep/toolchain decisions; `--strict-inputs` fail-closed |
| 5 | **`ReductionDiagnostics`** | `Demangler/ReductionDiagnostics.cs` | process-global + `lock` | `Reset` at start of `GenerateBindings` | Finding 18: demangle rule-miss tallies → SWIFTBIND058; pre-dates ReportCollector session |
| 6 | **`ClangAstParser._sourceByteCache`** | `ObjC/Parser/ClangAstParser.cs` | **`[ThreadStatic]`** `Dictionary<path, bytes>` | cleared in `Parse` `finally` | Per-parse header byte cache for availability recovery |
| 7 | **`ObjCTypeRefParser._additionalGenericContainers`** | `ObjC/Parser/ObjCTypeRefParser.cs` | **`[ThreadStatic]`** `HashSet` | `SetAdditionalGenericContainers(null)` after parse | AST-discovered ObjC generic container names for type-ref parsing |

**Ambient state count (mutable process/thread sinks): 7.**

### 3.2 `ModuleEmissionContext` (not ambient — by design)

| Item | Notes |
|------|-------|
| Path | `Emitter/StringEmitter/ModuleEmissionContext.cs` |
| Shape | **Instance** per module emission; `public static ModuleEmissionContext Default { get; }` is a shared empty instance for tests / env-less call sites |
| Statics | Only immutable helpers (`EmptyStringSet`, pure static functions) — **no mutable static side tables** for cross-module state |
| Side tables | Per-instance: method emission symbols (AF13), proxy/skip sets, API manifest keys, EveryProtocol slots, etc. |

Historical: “static mutable state across emitters” was replaced by instance context; new emission code must thread `context.GetEmissionContext()` rather than invent new statics.

### 3.3 Generated / emit-time ThreadStatic (not generator ambient)

`WrapperEmitter.Marshalling` emits **consumer** C# with `[ThreadStatic]` delegate slots for `@convention(c)` callbacks (AOT-safe reentrancy). That is **output** of the generator, not process state of the generator itself — excluded from ambient count.

### 3.4 Reset lifecycle (default generate path)

```
BindingsGeneratorCommand.Execute
  ├─ InputResolutionReport.Reset()          // before resolve
  ├─ SupportedToolchain.AssertSupported(...) // may RecordDegradation
  └─ GenerateBindings / ObjC pipeline
       ├─ ReductionDiagnostics.Reset()      // start of GenerateBindings
       ├─ ReportCollector.Start(module)
       ├─ AppleSupplementReferences.Reset() // module start (emit path)
       ├─ SwiftUIBridgeCollector.Reset()    // module emit bookends
       └─ ReportCollector.Complete() → binding-report.json
```

Parallel unit tests rely on ThreadStatic / AsyncLocal isolation for 1–4 and 6–7; `ReductionDiagnostics` is process-global and tests that assert misses call `Reset` explicitly.

---

## 4. Environment variables & MSBuild props that gate behavior

### 4.1 Generator-consumed environment variables

| Variable | Consumer | Purpose |
|----------|----------|---------|
| `SWIFTBINDINGS_PARSER_TIMEOUT_SECONDS` | `GeneratorTimeouts` | Wall-clock timeout for SwiftInterfaceParser host (default 300s; clamp 30–3600). |
| `SWIFTBINDINGS_SWIFTC_TIMEOUT_SECONDS` | `GeneratorTimeouts` | Wall-clock timeout for swiftc wrapper compile (default 600s; clamp 30–3600). |
| `SWIFT_INTERFACE_PARSER_PATH` | `SwiftSyntaxInterfaceFactsProducer` | Override path to SwiftInterfaceParser binary (tests / emergency). |

Precedence for timeouts (from `GeneratorTimeouts`): **explicit override (if ever threaded) > env var > built-in default**. SDK bridges MSBuild props into env via `EnvironmentVariables` on `<Exec>` so project properties win over a pre-set shell env.

### 4.2 User-facing MSBuild properties (`Sdk.props` defaults)

| Property | Default | Gates |
|----------|---------|-------|
| `SwiftGeneratorVerbosity` | `1` | Generator `-v` |
| `SwiftWrapperArchitectures` | `all` | Slice scope for wrapper compile (`simulator`/`device`/`all`) |
| `SwiftTargetArchitectures` | `auto` | Wrapper CPU arch decision (`auto` / `arm64` / `arm64,x86_64`) — fingerprint-sensitive |
| `SwiftRuntimeVersion` | `0.0.0-dev` | Exact runtime version stamped into generated dependency |
| `SwiftRuntimePackageVersionRange` | `[0.0.0-dev,0.1.0)` | NuGet range on implicit Runtime PackageReference |
| `SwiftAppleSupplementVersion` | `0.0.0-dev` | Apple supplement version / floor |
| `SwiftWrapperRequired` | `true` | Wrapper compile failure hard-fails (set `false` for known-broken wrappers) |
| `SwiftGenerateDocComments` | `true` | Auto symbol-graph / doc comment path |
| `SwiftAutoDetectDependencies` | `true` | Binary-linkage auto-deps |
| `SwiftAppleSupplementPrototypeDir` | *(empty)* | Prototype ProjectReference mode |
| `DisableImplicitSwiftRuntimeReference` | *(unset)* | Opt out of implicit Runtime PackageReference |
| `DisableImplicitSwiftAppleReference` | *(unset)* | Opt out of implicit Apple PackageReference |
| `SwiftFrameworkType` | *(body)* | `ObjC` engages ObjC pipeline + skips Swift runtime attrs/refs |
| `IsBindingProject` | *(body, required for ObjC)* | .NET iOS bgen engagement |
| `IsPackable` | `true` (props) | Doc-file generation; pack layout |

**Timeout props (no default in props — optional, bridged in targets):**

| Property | Env var injected |
|----------|------------------|
| `SwiftWrapperCompileTimeoutSeconds` | `SWIFTBINDINGS_SWIFTC_TIMEOUT_SECONDS` |
| `SwiftInterfaceParserTimeoutSeconds` | `SWIFTBINDINGS_PARSER_TIMEOUT_SECONDS` |

### 4.3 User-facing props defaulted in `Sdk.targets` (post-body)

| Property | Default | Gates |
|----------|---------|-------|
| `SwiftPlatformTarget` | `simulator` when TFM has sim slice | Device vs simulator slice selection |
| `SwiftAppleFrameworkMinDeploymentVersion` | `15.0` / macos `12.0` / catalyst `15.0` | Digester / availability floor for Apple-framework mode |

### 4.4 Item contracts (not props, but mode gates)

| Item | Role |
|------|------|
| `SwiftFramework` | Third-party xcframework binding input |
| `SwiftAppleFrameworkTarget` | System Apple framework binding (mutually exclusive with `SwiftFramework`) |
| `SwiftFrameworkDependency` | Explicit cross-module dep (PackageId + PackageVersion); does **not** auto-inject PackageReference from props |

### 4.5 Internal `_Swift*` properties (targets)

Platform detection (`_SwiftBindingPlatform`, slice IDs, NuGet RID, digester triples, fingerprint, intermediate dir, `_SwiftGeneratorEnv`, etc.) is derived from TFM + user props. Fail-closed diagnostic **SWIFTBIND010** when TFM is non-Apple. Full target graph is mapped in `waves/W0-map/M0-C-build-sdk-gates.md`.

---

## 5. Entry wiring (for orientation)

```
Program.Main / BindingsGenerator.Main
  → CliOptions.CreateRootCommand()
  → SetHandler → BindingsGeneratorCommand.Execute
       → early-out modes (table §2)
       → InputResolutionReport.Reset + toolchain assert
       → XCFramework resolve OR manual -a/-d/-t
       → GenerateBindings / ObjCPipeline
            → ReportCollector, ReductionDiagnostics, AppleSupplement*, SwiftUIBridge*
```

---

## 6. Audit notes / footguns

1. **No subcommands** — mode flags on one root make mutual exclusion easy to miss; tests in `BindingsGeneratorCommandTests` pin several pairs.
2. **Ambient collectors are load-bearing observability** — `ReportCollector` / `InputResolutionReport` / `AppleSupplementReferences` feed reports + PackageReference injection; a forgotten `Reset` leaks across modules in tests.
3. **`ReductionDiagnostics` is process-global** unlike ThreadStatic peers — intentional (demangle runs before report session); multi-run processes must Reset.
4. **SDK two-pass** relies on `--skip-wrapper-compilation` then `--compile-wrapper-only`; `HasWrapperXCFramework` must use “will be produced” not “exists now” (constraints.md).
5. **`SwiftTargetArchitectures` must appear in both SDK fingerprint echoes** or arch flips reuse stale arm64-only wrappers.
6. **Env timeout vars are emergency levers** — prefer MSBuild props so restore/build are reproducible.

---

## 7. Return values (for orchestrator)

| Metric | Value |
|--------|------:|
| **CLI option count** | **64** |
| **Ambient state collector count** | **7** |
| Early-out modes | 10 (+ full generate) |
