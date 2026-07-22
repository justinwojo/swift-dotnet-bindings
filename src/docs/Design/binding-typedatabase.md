# Binding Type Database

As-built design for how the generator maps Swift nominal types to managed projections.

The type database answers two questions for every Swift identity the binder encounters:

1. **Projection** — which C# type (namespace + name) to emit in the public API, and which package owns it.
2. **ABI facts** — kind, flags (frozen, memory management, ObjC bridging, protocol shape, …), metadata accessor, optional layout hints (`InlineSize`, `AbiFieldLayout`), and emission-time facts needed by downstream modules.

Source of truth for the in-process model lives under `src/Swift.Bindings/src/TypeDatabase/`. Seed XML databases live under `src/Swift.Runtime/src/Swift/` and ship next to the generator.

---

## Core types

| Type | Role |
|---|---|
| `ITypeDatabase` | Public surface used by parser, marshaler, and emitters. |
| `TypeDatabase` | Concrete registry: loaded module DBs, out-of-module cache, pending cross-module queue, freeze gate, Apple-supplement arm. |
| `ModuleTypeDatabase` | Per-module map of `SwiftTypeName` → `TypeRecord`, plus suppressed-proxy bookkeeping. |
| `TypeRecord` | One Swift identity's managed mapping + flags + optional emission/layout facts. |
| `TypeRecordFlags` / `TypeRecordKind` | Bitflags and kind enum on each record. |
| `TypeResolver` + `IResolutionStrategy` | Ordered strategy chain for `NamedTypeSpec` resolution (`TypeDatabase/Resolver/`). |
| `TypeDatabaseExtensions` | Thin entry points (`TryGetTypeRecord`, `GetTypeRecordOrAnyType`, …) that project over `TypeResolver.Default`. |
| `TypeOwnerRegistry` | Package-ownership oracle (Runtime / Apple supplement / third-party / ObjC workload / local). |
| `AppleSupplementResolver` | Builds synthetic `TypeRecord`s from the embedded apple-types manifest for identities owned by `SwiftBindings.Apple`. |
| `ModuleDatabaseEmitter` | Serializes a finished `ModuleTypeDatabase` to `{Module}Database.xml` for downstream consumers. |
| `GenerationMode` | `Direct` vs `XCFramework`, derived from whether `AsyncLibraryName` is set. |

`TypeRecord` is **not** a full `TypeDecl`. Declarations (methods, properties, constructors) live on the parsed `ModuleDecl` / `TypeDecl` tree. The database holds the **identity + marshalling projection** needed across modules and emission; full decls for framework dependencies can be retained separately via `ITypeDatabase.AddDependencyModuleDecl` when a consumer emitter needs constructor shapes.

---

## Seed XML databases

### Where they live

There is **no** platform / SDK-version / module directory hierarchy. Layout is flat:

| Location | Purpose |
|---|---|
| `src/Swift.Runtime/src/Swift/*Database.xml` | Source of the built-in stubs (one file per module, name `{Module}Database.xml`). |
| Generator output `Swift/*Database.xml` | Copied next to the generator binary at build time (`Swift.Runtime.csproj` `Content` + `CopyToOutputDirectory`; generator loads via `AppDomain.CurrentDomain.BaseDirectory/Swift/`). |
| SDK package `tools/net10.0/any/Swift/*Database.xml` | Same flat set shipped beside the generator inside `SwiftBindings.Sdk`. |
| Generated binding package `buildTransitive/{tfm}/{Module}Database.xml` | Per-module product of a binding run; consumed by downstream binding projects via `SwiftModuleDatabase` (see `Sdk.targets`). |

Built-in files are dependency-resolution stubs for Apple frameworks and the stdlib (`SwiftDatabase.xml`, `FoundationDatabase.xml`, `UIKitDatabase.xml`, …). They are **not** full framework bindings — they list the identities third-party libraries commonly reference so those references resolve to known managed types instead of `AnyType`.

Platform filtering is done at **load time**, not by filesystem layout: `Program.GetBuiltInDatabases(ApplePlatform?)` returns the full list minus frameworks absent on the target platform (e.g. skip `UIKitDatabase.xml` / `HealthKitDatabase.xml` on macOS; include `AppKitDatabase.xml` only for macOS / Catalyst / unspecified).

### XML schema (version 1.0)

Root element `swifttypedatabase` with attributes `version="1.0"`, `moduleName`, `modulePath`. Child `entities` holds one `entity` per type:

- **entity**: `managedNameSpace`, `managedTypeName`
- **typedeclaration**: `module`, `name` (type path without module, may be nested e.g. `URLSessionWebSocketTask.CloseCode`), `mangledName`, `kind`, `frozen`, `requiresMemoryManagement`, plus optional flags (`objcBridged`, `simpleEnum`, `nativeType`, `rawValueType`, `inlineSize`, `abiLayout`, protocol shape flags, emission facts, …)

Optional top-level `suppressedProxies` lists proxy class names this module did not emit, with an optional C# `namespace` attribute for cross-module strip of qualified proxy references.

Read path: `TypeDatabase.LoadModuleDatabaseFromFile` → schema check → `ReadVersion1_0` → `AddModuleDatabase`.  
Write path: `ModuleDatabaseEmitter.Emit` writes the same schema so a downstream run can load a previously generated module as a dependency.

Example (stdlib seed, abbreviated):

```xml
<swifttypedatabase version="1.0" moduleName="Swift" modulePath="...">
  <entities>
    <entity managedNameSpace="System" managedTypeName="Int32">
      <typedeclaration kind="struct" name="Int32" module="Swift"
                       mangledName="$ss5Int32V" frozen="true"
                       requiresMemoryManagement="false" />
    </entity>
  </entities>
</swifttypedatabase>
```

---

## Lifecycle of one generation run

`Program.GenerateBindings` owns the lifecycle:

```
new TypeDatabase()
  → load built-in *Database.xml (platform-filtered; skip stub that collides with the target module)
  → load --module-database paths (skip already-loaded / self-reference)
  → finalize framework dependencies (parse ABI → ModuleProcessor → AddModuleDatabase), topo-ordered
  → parse primary module
  → ModuleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase()
  → optional ObjC bridge records into the primary ModuleTypeDatabase (KeepExisting)
  → TypeDatabase.AddModuleDatabase(primary)
  → TypeDatabase.Freeze()
  → emission (reads only; stamps emission facts via ApplyEmissionResult)
  → ModuleDatabaseEmitter.Emit({Module}Database.xml)
```

### Built-in load rules

- Path: `{BaseDirectory}/Swift/{Name}Database.xml`.
- If the input ABI's module name matches a built-in database's `moduleName` (Apple-framework-as-target mode), that stub is **skipped** so parse-and-emit is not short-circuited by `IsModuleLoaded`. Override with `--keep-builtin-database` when a third-party module intentionally shares an Apple name and the caller wants the stub.
- Dependency XMLs that duplicate a built-in module name are skipped with a log line ("already loaded").

### In-run registration (`ModuleProcessor`)

While processing the module being bound (or a dependency finalized from ABI JSON):

1. A **module-local** `ModuleTypeDatabase` is filled first. Same-module lookups during processing hit this local map; it is not yet in the global registry.
2. Each struct / enum / class is registered with a full `TypeRecord` once layout/kind/flags are known (not a separate "light introduction" phase as earlier designs contemplated — recursive processing + cycle detection handle forward references within the module).
3. Nested types declared under `extension ForeignModule.ForeignType { … }` are also mirrored into the foreign module via `TypeDatabase.RegisterCrossModuleType` so lookups keyed by `SwiftTypeName.Module` succeed.
4. When the module is complete, `AddModuleDatabase` publishes it globally.

### Freeze and emission stamps

After the primary module is `AddModuleDatabase`'d, `Freeze()` makes structural writes illegal (`SWIFTBIND045` on `Register` / `UpdateTypeRecord`). The only post-freeze mutations are:

- `ApplyEmissionResult` — stamps emission-discovered facts (`EmittedMemberCount`, `EmittedClassMethods`, `EmittedMetadataPInvoke`, nested-type `CSharpTypeName` renames) onto already-registered records.
- `RestoreEmissionRecord` — rolls back those stamps when an emission attempt is discarded.

This keeps "what does the database say?" independent of *when* during emission you ask, except for facts that literally cannot exist until the type body is written.

### Conflict policy

`ModuleTypeDatabase.Register(name, record, ConflictPolicy)`:

- `KeepExisting` — first registration wins (parser duplicate guards, cross-module re-home, ObjC bridge fill-gaps).
- `Overwrite` — last write wins (default convenience `RegisterType`, full `UpdateTypeRecord`).

Collisions that change content are logged (`SWIFTBIND024`).

---

## Resolution

### Raw `SwiftTypeName` cascade (`TypeDatabase.TryGetTypeRecord`)

1. **Apple supplement** (`AppleSupplementResolver` via `TypeOwnerRegistry` + embedded manifest) — first, including same-module Apple identities, so framework packages defer to the supplement's canonical projection when the identity is published there.
2. **Database cascade** (`TypeResolver.DatabaseCascade`), shared with the NamedTypeSpec path:
   - `DatabaseLookupStrategy` — module DB, `CoreFoundation`→`CoreGraphics` module alias, `@_implementationOnly` umbrella rewrite via `AppleFrameworkRegistry.GetCompileImportSourceModules`, and CoreFoundation/CoreGraphics `Foo`↔`FooRef` suffix toggle.
   - `OutOfModuleLookupStrategy` — `_outOfModuleTypes` cache (closed generics / foreign instantiations).
   - `CrossModuleAliasStrategy` — static `_typeAliases` table (e.g. `FamilyControls.ApplicationToken` → `ManagedSettings.Token<ManagedSettings.Application>`).
   - `SwiftErrorStrategy` — `Swift.Error` special case.

`TryGetTypeRecordWithoutSupplement` is the same cascade without arm 1, used by `DatabaseLookupStrategy` so the supplement is consulted once at the strategy layer for NamedTypeSpec resolution.

### NamedTypeSpec resolution (`TypeResolver.Default`)

Ordered strategies (first match wins): DynamicSelf → GenericParameter → PrimitiveAlias → Metatype → Existential → Swift.Any/AnyObject → Pointer → UnsupportedAppleModule → BareGenericGuard → BoundGenericSimdAlias → **AppleSupplement** → **DatabaseCascade** → ObjCBridging.

`TypeDatabaseExtensions` entry points are projections over this single chain.

### `IsTypeProcessed` vs `IsTypeRegistered`

| API | Meaning |
|---|---|
| `IsTypeProcessed` | `TryGetTypeRecord` succeeds — resolvable by the full machinery (including supplement). |
| `IsTypeRegistered` | Narrow: already present in a loaded module/dependency DB (module alias / umbrella / Ref-variant / type-alias only). **No** supplement arm. Parser duplicate detection and metadata-accessor choice use this so a supplement-owned same-module type is still emitted rather than treated as "already processed". |

---

## Cross-module records and aliases

### Pending cross-module queue

When `AddModuleDatabase` sees a record whose `SwiftTypeName.Module` differs from the database being added (cross-module nested / extension product):

- If the foreign module DB is already loaded → insert with `KeepExisting`, or merge additive `ProtocolConformances` into the canonical record.
- If not yet loaded → enqueue in `_pendingCrossModuleRecords` keyed by foreign module name; drain on that module's later `AddModuleDatabase`.

Canonical identity fields (C# type, frozen, kind) on an existing stdlib/framework entry are never overwritten by a consumer's parser-side product record; only additive protocol-conformance edges merge in.

### Module aliases

Hard-coded in `TypeDatabase`: `CoreFoundation` → `CoreGraphics` (ABI JSON often spells CG geometry under CoreFoundation while seeds register under CoreGraphics).

### Type aliases

Hard-coded cross-module Swift typealiases in `_typeAliases` (FamilyControls token aliases → `ManagedSettings.Token<…>`). Lookup strips generic args for the TypeRecord key; `TryResolveTypeAlias` returns the full canonical name when code generation needs the specialization.

### Out-of-module types

`_outOfModuleTypes` holds identities that do not live in any loaded module DB (e.g. closed generic instantiations registered across module boundaries). `UpdateTypeRecord` falls back here when the module key is absent.

### Stripped foreign conformances

`RegisterStrippedConformance` / `HasStrippedConformance` record foreign concrete types (no local `TypeDecl`) that conform to synthesized underscore PATs stripped from ABI JSON — used by `BoundGenericsHandler.SatisfiesConstraint` so closed bound generics like `IntentParameter<Int>` are not falsely skipped. Fed by `UnderscoreProtocolSynthesizer`.

---

## Type ownership and the Apple supplement

Package ownership is **not** encoded in the seed XML. It is resolved by `TypeOwnerRegistry`:

1. Per-type override (legacy Runtime canonicals such as `Foundation.Date` / `Data` / `URL`, …)
2. Swift stdlib known type → `SwiftBindings.Runtime`
3. ObjC workload projection (e.g. managed `Foundation.NSDate`)
4. Module default — registered Apple modules → `SwiftBindings.Apple`; third-party modules → their generated package id
5. Same-module type currently being generated → local
6. Unsupported → skip members that reference it

Cross-module **conformance** ownership is a separate table (`RegisterConformanceOwner` / `TryGetConformanceOwner`).

`AppleSupplementResolver` only returns a record when ownership is `AppleSupplement` **and** the identity appears in the embedded `apple-types-manifest.json` (resource `Swift.Bindings.apple-types-manifest.json`, source file under `src/Swift.Bindings.Sdk/tools/apple-types-manifest/`). Hits produce synthetic `TypeRecord`s pointing at the supplement's managed projection.

Authoritative design for the supplement package, manifest format, VWT storage policy, and versioning: **[apple-swift-types-architecture.md](apple-swift-types-architecture.md)**. Do not duplicate that material here.

Apple framework **heuristics** (which modules auto-bridge, optional-fallback modules, ObjC prefixes, remaps) live in `AppleFrameworkRegistry` over `Data/apple-frameworks.json` — complementary to the type database, not a second type store.

---

## Emitted consumer databases

After emission, `ModuleDatabaseEmitter` writes `{Module}Database.xml` next to the generated C# / wrapper. The SDK packs it into the NuGet under `buildTransitive/{tfm}/` and the consumer targets re-surface it as `SwiftModuleDatabase` so a downstream binding project can pass it as `--module-database` (or the SDK equivalent) and resolve the upstream module's types without re-parsing its ABI.

Downstream also receives `suppressedProxies` so umbrella-aware existential marshalling can strip references to proxies that never emitted in the dependency.

---

## What the database deliberately does not do

- **Not a full ABI cache** — no method bodies, property accessors, or complete member lists (except the small `EmittedClassMethods` / `EmittedMemberCount` emission stamps for cross-module override and protocol-empty checks).
- **Not layout truth for resilient types** — size/stride for non-frozen / opaque types come from live value witnesses / runtime at need; seed XML may carry `inlineSize` / `abiLayout` when known at emit time for frozen field embedding and thunk lowering.
- **Not platform-versioned** — one flat stub set; platform differences are load filters and TFM-specific consumer packages, not parallel XML trees.

---

## Open design questions

These are real gaps in the as-built system, not queued work:

1. **`IsModuleLoaded` vs "real bindings generated"** — today loading a built-in stub and having generated a full module for the same name share one predicate. Apple-framework-as-target works by **skipping** the colliding stub. A cleaner split ("dependency stub present" vs "full module finalized") would allow keeping the stub while still emitting the framework; that split does not exist yet.

2. **Hard-coded alias tables** — `_moduleAliases` and `_typeAliases` are static dictionaries in `TypeDatabase`. New cross-module typealiases discovered in the wild require code changes, not data files.

3. **Per-TFM XML supplements** — some Foundation overlay remaps (e.g. macOS-only process/host types) stay coarse in `FoundationDatabase.xml` because there is no per-platform stub overlay. Revisit only if a macOS consumer needs typed remaps that would break iOS (see `src/docs/not-planned.md` for the deferred trigger).

4. **Seed completeness vs Apple supplement** — built-in stubs and the apple-types manifest both participate in resolution. Ownership decides which wins; keeping them consistent when adding a new Apple identity is a process concern, not an automated invariant in the type-database layer.
