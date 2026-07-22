# Retrieving symbols missing from `abi.json`

The generator's primary declaration source is the module's ABI JSON (`swift-api-digester -dump-sdk` / `.abi.json`). That file lists public types, members, and many member mangled names — but it does **not** export the full set of symbols the binding needs at generation time and at runtime. Those extra symbols come from the library's **text-based stub** (`.tbd`), parsed in-process and demangled before ABI parse.

This document describes that path as built. Demangling itself is covered in [demangling.md](demangling.md).

---

## What the ABI JSON alone cannot supply

| Symbol kind | Why the binding needs it | How it is recovered from the TBD |
|---|---|---|
| **Type metadata accessors** (`…Ma`) | P/Invoke entry points for `ISwiftObject.GetTypeMetadata()`, layout probes in `ModuleProcessor`, native thunk metatype setup | Demangled as `MetadataAccessorReduction`; looked up via `DemanglingResults.TryGetMetadataAccessor` / `ModuleFactIndex` and stored on `StructDecl` / `EnumDecl` / `TypeRecord.MetadataAccessor` |
| **Protocol conformance descriptors** (`…Mc`) | Runtime `ProtocolConformanceDescriptor.LoadFromSymbol(lib, symbol)` in emitted `GetProtocolConformanceDescriptor<TProtocol>()` | Demangled as `ProtocolConformanceDescriptorReduction`; attached to `TypeConformance.ProtocolConformanceDescriptor` during `SwiftABIParser.HandleConformance` |
| **Full export set** (raw symbol strings) | Presence checks that the ABI JSON cannot answer | `DemanglingResults.AllSymbols` → `ModuleDecl.ExportedSymbols` |

Consumers of the raw export set (`AllSymbols` / `ExportedSymbols`):

- **Async property accessors** — `ManglingProbes.IsAsyncAccessor` looks for `{base}Tu` or `{base}TjTu` in the TBD. The ABI accessor node alone does not always surface this reliably; the TBD export is the oracle.
- **Protocol method descriptors** — `ManglingProbes.HasMethodDescriptor` looks for `{base}Tq`. A required protocol method missing its `Tq` export means EveryProtocol reverse-dispatch is skipped (`HasMissingTbdMethodDescriptors`).
- **P/Invoke entry-point export checks** — `MethodHandler.CheckExportedSymbol` marks `MethodDecl.IsMissingExportedSymbol` when the computed entry point is absent from the TBD (wrapper-routed symbols are exempt).
- **Native thunk call targets** — `NativeThunkEmitter.IsSwiftCallTargetExported` refuses to emit a thunk whose `bl`/`callq` target (including a `Tj` dispatch-thunk suffix) is not in the export set.

**Note on demangled groupings that are not active lookup APIs today.** `DemanglingResults` also partitions `DispatchThunks` and `ProtocolWitnessTables` from the demangler reductions. Live code does **not** look up symbols through those arrays: dispatch-thunk entry points are formed by the stable mangling convention (`ManglingProbes.DispatchThunkSuffix` / `SwiftCallTargetResolver`) and then checked against `ExportedSymbols`. Conformance work uses the **descriptor** (`Mc`) path, not the witness-table reduction list.

---

## Pipeline as built

```
.tbd on disk
    │
    ▼
TbdParser.ParseFile                    (YAML v1–4 or JSON v5+)
    │
    ▼
DemanglingResults.FromTbd              (Swift5Demangler per export)
    │
    ├── MetadataAccessors / ProtocolConformanceDescriptors
    │         └── SwiftABIParser + ModuleFactIndex (cross-module preload)
    │
    └── AllSymbols
              └── ModuleDecl.ExportedSymbols (export-set probes)
```

Entry points:

1. **CLI** — `--tbd` / `-t` (required when not using `--xcframework`). See `CliOptions.Tbd` and `BindingsGeneratorCommand`.
2. **Generation start** — `Program` calls `DemanglingResults.FromTbd(tbdPath, …)` and passes the result into `SwiftABIParser` (and into dependency preload / `ModuleFactIndex` for cross-module facts).
3. **xcframework resolution** — `XCFrameworkResolver.FindOrGenerateTbd` supplies a TBD path before generation (see below).

Cross-module fact preload builds a `ModuleFactIndex` per dependency TBD so metadata accessors and conformance descriptors can be resolved without parse-order dependence on the type database (`Parser/CrossModule/ModuleFactIndex.cs`). Lookups go through `ICrossModuleFactResolver` / `IndexBackedCrossModuleFactResolver`.

### Fallbacks when a demangled hit is missing

- **Metadata accessor** — `SwiftABIParser.ResolveMetadataAccessor` prefers the TBD hit; if absent for a same-module or already-registered foreign type it falls back to the canonical mangling `{mangledName}Ma` (covers umbrella re-exports and some cross-module extensions). Truly unknown foreign types still throw.
- **Protocol conformance descriptor** — if the (implementing type, protocol) pair is not in the TBD, the parser retries with the implementing type's **original** module extracted from the ABI mangled name (`ManglingProbes.TryGetModuleFromMangledName`) for `@_originallyDefinedIn` re-exports. Still missing → empty descriptor string (logged); emission skips empty symbols so `LoadFromSymbol("", …)` never ships.
- **Inherent / synthesized conformances** — some ABI-listed conformances have no exportable descriptor; the empty-descriptor path is intentional, not a TBD parse failure.

Classes often store metadata accessors as `{MangledName}Ma` by convention in `ModuleProcessor` rather than requiring a demangled TBD hit for every class; structs and enums go through `ResolveMetadataAccessor`.

---

## What a `.tbd` provides

A TBD is Apple's text-based stub for a dynamic library (or a synthesized stub for static distributions). It lists install name, targets, and **exported symbols** — enough for demangling and export checks without loading a Mach-O into the generator process.

**Formats** (both implemented under `src/Swift.Bindings/src/Demangler/TbdParser/`):

| Format | Detector | Parser |
|---|---|---|
| YAML-like (tbd-version 1–4) | First line `--- !tapi-tbd` | `YamlLikeTbdFormatParser` |
| JSON (tapi_tbd_version 5+) | Root property `tapi_tbd_version` | `JsonTbdFormatParser` |

`TbdParser` tries registered format parsers in order, parses into `TbdFile` / `ExportEntry` / `Symbol`, and classifies symbols by prefix (`_$s…` → Swift, other `_…` → Objective-C, else Other). Only Swift exports are demangled in `FromTbd`; leading underscores are stripped before demangling and for `AllSymbols`.

**Where TBDs come from for each input mode:**

| Input | TBD source |
|---|---|
| Apple / Xcode SDK frameworks | Platform SDK layout, e.g. `…/SDKs/<Platform>.sdk/System/Library/Frameworks/<Name>.framework/<Name>.tbd` (Catalyst under `System/iOSSupport/…`) |
| xcframework with a shipped `.tbd` | First deterministically ordered `*.tbd` under the selected slice's swiftmodule dir (`XCFrameworkResolver.FindOrGenerateTbd`) |
| xcframework dylib, no TBD | `xcrun tapi stubify --filetype=tbd-v4` on the dylib |
| Static archive / non-dylib binary | Minimal JSON TBD v5 synthesized from `nm -gU` (`SynthesizeTbdFromStaticArchive`) — only global symbol names; install-name/target fields are placeholders the demangler does not consult |

Direct mode and SDK packaging still take a platform-appropriate TBD: export sets can differ by platform (a symbol present on iOS may be absent on macOS/Catalyst), so the TBD must match the platform being bound.

---

## Why not the alternatives (retained rationale)

These were investigated before TBD parsing shipped. They remain non-goals; do not re-open without a new platform constraint.

### Mach-O / system dylibs for Apple frameworks

On modern macOS/iOS, system frameworks live in the **dyld shared cache**, not as standalone `.dylib` files on disk. There is no supported public tool chain for "open StoreKit's dylib and list exports" the way third-party xcframeworks allow. Reverse-engineering the shared cache is fragile and outside the generator's scope.

### On-disk DeviceSupport / residual framework binaries

Some host trees still contain framework binaries under Xcode DeviceSupport or similar. Sampling those with `nm` showed incomplete export sets for Swift interop needs (e.g. protocol conformance descriptors missing). They are not a reliable oracle.

### `dyld_info -exports`

`/usr/bin/dyld_info` can print exports for cache-resident paths. It is useful for manual inspection, but it is an external process, platform-selection is awkward for multi-platform binding generation, and it is not wired into the generator. TBD files already encode the same export lists per SDK platform.

### Building Apple's `dyld` sources to dump the cache

Possible in principle, not a public stable API for a NuGet-based binding tool, and unnecessary once SDK TBDs are available.

### In-process Mach-O parsing as the primary path

The original runtimelab design pointed at reading dylibs (Mach-O) as the symbol source. That approach fails for Apple system frameworks (shared cache) and adds binary-format complexity the generator does not need when a text export list exists. For third-party dylibs, the shipping path is **TBD first** (shipped, `tapi stubify`, or `nm`-synthesized stub), then demangle — not a Mach-O reader inside the generator.

`nm` remains a **secondary** tool only: symbol enumeration when synthesizing a TBD for static archives (`NativeSymbolProbe` / `SynthesizeTbdFromStaticArchive`), not a parallel demangling front-end.

---

## Related code

| Area | Location |
|---|---|
| TBD parse entry | `Demangler/TbdParser/TbdParser.cs` |
| YAML / JSON format parsers | `Demangler/TbdParser/Parsing/` |
| Demangle + partition | `Demangler/DemanglingResults.cs` |
| Reductions | `Demangler/IReduction.cs`, `Demangler/Swift5Reducer.cs` |
| ABI attachment | `Parser/SwiftABIParser.cs` (`ResolveMetadataAccessor`, `HandleConformance`) |
| Export-set probes | `Parser/ManglingProbes.cs` |
| Cross-module index | `Parser/CrossModule/ModuleFactIndex.cs` |
| TBD discovery / synthesis | `Configuration/XCFrameworkResolver.cs` (`FindOrGenerateTbd`) |
| Emission of metadata / PCD | `Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs`, `ClassHandler.cs` |
| Export gates | `MethodHandler.CheckExportedSymbol`, `NativeThunkEmitter.IsSwiftCallTargetExported` |

---

## See also

- [demangling.md](demangling.md) — demangler and reduction pipeline
- [demangling-replacement-spike.md](demangling-replacement-spike.md) — why replacing demangling of TBD `Mc`/`WP` symbols with symbol-graph `conformsTo` alone was rejected
- [binding-value-witness-table.md](binding-value-witness-table.md) — why type metadata (and thus metadata accessors) matter for value types
