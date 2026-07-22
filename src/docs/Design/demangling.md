# Demangling

Swift exports many symbols the generator needs that never appear in `abi.json` — type metadata accessors (`…Ma`), protocol conformance descriptors (`…Mc`), protocol witness tables (`…WP`), dispatch thunks (`…Tj`), async function pointers (`…Tu`), and method descriptors (`…Tq`). The generator does **not** re-mangle those strings from first principles. It ingests exported symbols from the framework's `.tbd`, demangles them with a managed port of Apple's Swift 5 demangler, and indexes the reductions the rest of the pipeline looks up.

Where those symbols come from (TBD vs dylib vs dyld cache) is covered in [retrieving-symbols-outside-abi-json.md](retrieving-symbols-outside-abi-json.md). Why the managed demangler is not replaced by `swift-symbolgraph-extract` + forward mangling is recorded in [demangling-replacement-spike.md](demangling-replacement-spike.md) (**NO-GO**).

## Role in the binding pipeline

Demangling runs **before** ABI parse for each module (and for each dependency TBD the run preloads). `Program.cs` calls `DemanglingResults.FromTbd` on the module TBD, then constructs `SwiftABIParser` with that result (and an `ICrossModuleFactResolver` backed by demangled dependency TBDs). The parser writes demangled facts onto decls; the emitter later emits P/Invokes and runtime loads that use those mangled strings as `dlsym` entry points.

```text
.tbd  →  TbdParser  →  Swift5Demangler.Run  →  Swift5Reducer  →  DemanglingResults
                                                                      │
                    SwiftABIParser / ModuleFactIndex / ClosureHandler ←┘
```

The main module's raw export set is also attached to the module model as `ModuleDecl.ExportedSymbols` (`demangledTbdFile.AllSymbols`) for suffix-existence probes that do not need a full reduction.

## Architecture

All code lives under `src/Swift.Bindings/src/Demangler/` (namespace `BindingsGeneration.Demangling`, with TBD parsing in `TbdParsing`).

### TBD ingestion — `TbdParser`

`TbdParser` (`Demangler/TbdParser/TbdParser.cs`) reads Text-Based Dynamic Library stubs. Format detection chooses among:

- `YamlLikeTbdFormatParser` — TBD versions 1–4 (YAML-like)
- `JsonTbdFormatParser` — TBD versions 5+ (JSON)

Exports are categorized by `Symbol.DetermineSymbolType`: names starting with `_$s` are `SymbolType.Swift`. `DemanglingResults.FromTbd` demangles only `ExportEntry.SwiftSymbols`, stripping a leading `_` before demangle and storing the underscore-stripped form in `AllSymbols`.

### Node tree — `Swift5Demangler` + `Node`

`Swift5Demangler` is an explicit port of Apple's Swift 5 demangler (`// This is a port of the Apple Swift 5 demangler` in `Swift5Demangler.cs`). It:

1. Accepts mangling prefixes `_T0` (Swift 4), `$S` / `_$S` (Swift 4.x), `$s` / `_$s` (Swift 5+).
2. Parses the remainder with a stack machine and recursive descent into a tree of `Node` values.
3. Each `Node` has a `NodeKind`, a `PayloadKind` of `None` / `Index` / `Text`, and a list of child nodes (`Node.cs`, `Enums.cs`).

`Run(string mangledName)` demangles under a lock (instance state is reused), reduces the root node, and returns an `IReduction`. Failures that throw per symbol are caught in `FromTbd` and recorded as `ReductionError` so one bad export does not abort the whole TBD.

### Reduction — `Swift5Reducer` + `RuleRunner` + `MatchRule`

`Swift5Reducer` pattern-matches the node tree with a fixed list of `MatchRule`s run by `RuleRunner`. A rule matches on `NodeKind` (and optional child-shape constraints); its reducer builds a typed `IReduction`. There is no separate `ISwiftSymbol` interface in the tree — the common surface is:

| Reduction type | Typical mangling / node | Meaning |
|---|---|---|
| `MetadataAccessorReduction` | type metadata accessor (`…Ma`) | Host type as `NamedTypeSpec` + mangled symbol |
| `ProtocolConformanceDescriptorReduction` | `…Mc` | Implementing type, protocol, conforming module, symbol |
| `ProtocolWitnessTableReduction` | `…WP` | Implementing type + protocol + symbol |
| `DispatchThunkFunctionReduction` | `…Tj` (dispatch thunk) | Same payload as `FunctionReduction`, distinct type for filtering |
| `FunctionReduction` | free/method function symbols | `SwiftFunction` (name, params, return, async/throws, …) |
| `TypeSpecReduction` | type-shaped symbols / protocol names | A `TypeSpec` (often `NamedTypeSpec`) |
| `ProvenanceReduction` | module / instance / extension context | Intermediate during reduction |
| `ReductionError` | no rule, incomplete tree, … | Symbol + message + severity |

`DemanglingResults` partitions a full TBD demangle into arrays of the reductions the generator indexes, plus `AllSymbols`.

### Batch index — `DemanglingResults`

Factory: `DemanglingResults.FromTbd(path, loggerFactory)`.

Lookups used on the hot path:

- `TryGetMetadataAccessor` / `GetMetadataAccessor` — match `MetadataAccessorReduction.TypeSpec.Name` to `SwiftTypeName.ModuleQualifiedName`
- `TryGetProtocolConformanceDescriptor` / `GetProtocolConformanceDescriptor` — match implementing type + protocol module-qualified names

Cross-module parse order is handled by preloading dependency TBDs into `ModuleFactIndex` / `ModuleFactIndexSet` (`Parser/CrossModule/`), then resolving through `IndexBackedCrossModuleFactResolver` with a `LegacyCrossModuleFactResolver` fallback that still reads the bound module's `DemanglingResults` directly.

`DispatchThunks` and `ProtocolWitnessTables` are collected on `DemanglingResults` and covered by unit tests; production binding lookups today key primarily on metadata accessors, conformance descriptors, and the raw `AllSymbols` set.

## What consumers need demangled info for

### ABI parser (`Parser/SwiftABIParser.cs`)

- **Metadata accessor** on structs/enums: `ResolveMetadataAccessor` prefers the demangled TBD entry, then falls back to the conventional `{node.MangledName}Ma` for same-module / already-registered foreign types (umbrella re-exports, known dependency types).
- **Protocol conformance descriptor** on type conformances: `HandleConformance` resolves via `ICrossModuleFactResolver`, with an `@_originallyDefinedIn` retry that rewrites the implementing type's module from `ManglingProbes.TryGetModuleFromMangledName` when the USR's current module differs from the mangled original module.
- **Protocol identity** from a conformance's mangled name: `demangler.Run` → `TypeSpecReduction` → `NamedTypeSpec`. Unsupported demangler substitutions (notably some `_Concurrency` short forms such as `$sSci`) are caught and fall back to a printedName-derived identity so the enclosing type is not dropped.
- **Method `IsAsync`**: prefers `FunctionReduction.Function.IsAsync`; if the reducer produces no function reduction (constructors, accessors), falls back to `Swift5Demangler.HasAsyncMarker` over the raw node tree (`NodeKind.AsyncAnnotation`).
- **Variadic parameters**: inspects reduced parameter lists when available; otherwise `HasVariadicParameterMarker` walks the raw tree for `NodeKind.VariadicMarker` (needed for constructors the reducer does not fully reduce). Distinguishes `init(x: T...)` from a plain `init(x: [T])` sibling that share the same ABI printedName shape.
- **Protocol method descriptors**: `ManglingProbes.HasMethodDescriptor(AllSymbols, mangledName)` (`…Tq`) gates EveryProtocol eligibility when a required method's descriptor is missing from the TBD.
- **Async property accessors**: ABI JSON does not mark accessors async; `ManglingProbes.IsAsyncAccessor` checks `AllSymbols` for `{name}Tu` or `{name}TjTu`.

### Cross-module fact index

`ModuleFactIndex.FromDemangledTbd` freezes metadata-accessor and conformance-descriptor maps for one module so later parses can resolve foreign types without depending on parse order. It intentionally does **not** carry layout / frozenness facts.

### Closure convention (`Marshaler/ClosureHandler.cs`)

`@convention(c)` detection uses `Swift5Demangler.HasCFunctionPointerMarker` (raw-tree `NodeKind.CFunctionPointer`) and/or a reduced `ClosureTypeSpec.IsConventionC` when the signature reduced through the `CFunctionPointer` function-type rule. Substring scans for `"XC"` were retired because an identifier can contain those characters without encoding a C function pointer.

### String-suffix probes (`Parser/ManglingProbes.cs`)

Not every fact goes through a full demangle. `ManglingProbes` is the single home for stable-mangling **suffix/prefix fragments** the generator still assumes by string surgery: `Tq`, `Tu`, `Tj`, `TjTu`, `$s` / `_$s`, stdlib `$ss…`, and module-prefix extraction. These probes answer set-membership questions against `AllSymbols` (or parse a module length-prefix) without building a node tree. They are complementary to the demangler, not a second demangler.

## Raw-tree probes vs full reduction

Some top-level node kinds are **intentionally not reduced** to `FunctionReduction` (constructors, getters/setters, subscripts, several signature wrappers). The allowlist and rationale live in `ReductionDiagnostics.IntentionallyUnreducedKinds`. Detection that still needs those symbols walks the node tree that demangle always builds:

- `HasAsyncMarker` → `AsyncAnnotation`
- `HasVariadicParameterMarker` → `VariadicMarker`
- `HasCFunctionPointerMarker` → `CFunctionPointer`

That split is load-bearing: requiring a successful function reduction would silently disable async/variadic detection for every init/accessor.

## Observability and fail-loud holes

`RuleRunner` records every reduction attempt and every "no rule for node" miss into process-wide `ReductionDiagnostics`. At the end of a generate run, `Program.cs` emits **SWIFTBIND058** when there are **unexpected** misses (kinds outside `IntentionallyUnreducedKinds`). Unit tests under `tests/UnitTests/DemanglerTests/` (including corpus-loudness coverage) fail closed on reachable unexpected misses so a new unruled node kind cannot quietly degrade demangle-based detection.

## Known limits

- **Coverage is partial by design.** The reducer implements rules for symbol shapes the generator consumes (metadata, conformances, functions, selected generics / dependants). Unruled kinds either sit on the intentional allowlist or surface as SWIFTBIND058 / corpus failures.
- **Stdlib short substitutions lag.** Conformance demangle can fail on mangling forms the port does not yet handle (e.g. some `_Concurrency` `Sc*` symbols). Call sites degrade to printedName fallbacks rather than aborting the type.
- **TBD Swift filter is prefix-strict.** Only `_$s…` exports are classified as Swift for demangle; other export styles in a TBD are not fed through `FromTbd`'s demangle loop.
- **Fallback mangling is narrow.** When a metadata accessor is missing from the TBD index, the parser may synthesize `{mangledName}Ma` for known cases; it does **not** implement general forward mangling of Mc/WP/Tj/Tu.
- **Port lag.** The demangler tracks Apple's grammar as of the last port update. New Swift mangling forms require updating `Swift5Demangler` / `NodeKind` / reducer rules, not inventing a parallel system.

## Maintenance stance

Keep the ported managed demangler. The replacement spike ([demangling-replacement-spike.md](demangling-replacement-spike.md)) showed that recovering Mc/WP symbols from symbol-graph `conformsTo` edges without a real substitution-aware mangler falls far short of the hit rate needed, and bindings still need the **exact** mangled string for `dlsym` at runtime.

Practical rules:

1. Prefer extending `Swift5Reducer` rules (or raw-tree marker walks) over new substring heuristics on mangled names.
2. Put every assumed suffix/prefix constant in `ManglingProbes`, not scattered literals.
3. When Apple's demangler gains new node kinds the generator cares about, port them into `Swift5Demangler` / `Enums.NodeKind` and add reducer or intentional-unreduced entries so SWIFTBIND058 stays meaningful.
4. Do not treat `DispatchThunks` / `ProtocolWitnessTables` arrays as dead code solely because production lookups favor Mc/Ma/`AllSymbols` — they are part of the reduction model and test surface; wire them into new consumers only when a call site actually needs typed reductions rather than suffix membership.

## See also

- [retrieving-symbols-outside-abi-json.md](retrieving-symbols-outside-abi-json.md) — TBD/dylib/dyld-cache constraints and symbol sources
- [demangling-replacement-spike.md](demangling-replacement-spike.md) — why symbol-graph forward mangling is a NO-GO
- `src/Swift.Bindings/src/Demangler/` — demangler, reducer, TBD parser
- `src/Swift.Bindings/src/Parser/ManglingProbes.cs` — stable suffix/prefix probes
- `src/Swift.Bindings/src/Parser/CrossModule/` — demangled TBD fact indexes for cross-module resolve
- `src/Swift.Bindings/tests/UnitTests/DemanglerTests/` — demangle/reducer corpus and marker tests
