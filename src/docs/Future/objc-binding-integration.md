# Objective-C Binding Integration

## Status: Partially Implemented / Considering Full Integration

**Date:** 2026-02-14 (original), updated 2026-03-05

## What's Already Implemented

Significant ObjC integration infrastructure already exists in the Swift pipeline, focused on **ObjC-rooted Swift classes** and **ObjC-bridged type references**. This was built incrementally through BX4 and subsequent sessions.

### ObjC-Rooted Swift Classes (BX4, complete)

Swift classes that inherit from ObjC types (NSObject, CALayer, UIControl, etc.) are fully supported. The generator detects them, resolves the type hierarchy, and emits C# classes that inherit from the corresponding .NET MAUI ObjC binding types.

**Key files:**
- `Model/TypeDecl/ClassDecl.cs` — `HasObjCSuperclass` (USR starts with `c:`), `IsObjCRooted` flag
- `Parser/ModuleProcessor.cs` — `ResolveClassHierarchy()`, `UpdateObjCRootedTypeRecords()` (fixed-point transitive detection)
- `Marshaler/Projection/ObjCRootedClassProjection.cs` — Handle-based marshalling (NSObject pointer IS Swift object pointer)
- `Marshaler/Projection/ObjCBridgedProjection.cs` — Standalone ObjC type references (UIImage, etc.)
- `Marshaler/MarshallingHelpers.cs` — `IsObjCRooted()`, `IsObjCBridged()`, `GetObjCBaseTypeName()`, `MapSwiftModuleToNetNamespace()`
- `Marshaler/Projection/MethodMarshalPlan.cs` — `SwiftSelfKind.ObjCRootedClass`
- `Emitter/StringEmitter/Handler/ClassHandler.cs` — Full emission: base class, no `_payload`, no `IDisposable`, constructor chaining with `DangerousRelease()`
- `TypeDatabase/TypeDatabaseExtensions.cs` — `AppleObjCFrameworkModules` (26 frameworks), `AppleFrameworkValueTypes` (100+ types), `IsObjCModuleType()`
- `TypeDatabase/TypeRecord.cs` — `TypeRecordFlags.ObjCRooted`, `TypeRecordFlags.ObjCBridged`
- Tests: `ClassObjCRootedTests.cs` (957 lines — model, resolution, TypeRecord, namespace mapping, emission, hierarchy)

**Emission characteristics for ObjC-rooted classes:**
- C# class inherits from mapped ObjC base type (e.g., `class MyLayer : CoreAnimation.CALayer, ISwiftObject`)
- No `_payload` field — Handle IS the object pointer (ObjC and Swift share same ARC)
- No `IDisposable` — `NSObject.Dispose()` handles lifecycle
- Constructor chains through `base((ObjCRuntime.NativeHandle)handle.Handle)` with `DangerousRelease()` to balance MAUI's retain
- `SwiftHandle` property: `IntPtr ISwiftObject.SwiftHandle => Handle`

### ObjC Framework Infrastructure (complete)

- `XCFrameworkResolver.cs` — `ResolveObjCFramework()` for resolving ObjC-only framework paths
- `BinaryDependencyAnalyzer.cs` — Detects ObjC-only dependencies, classifies framework types
- `FrameworkDependencyInfo.cs` — `IsObjCOnly` flag for dependency classification
- `XCFrameworkResolver.cs` — `ParseModuleNameFromModulemap()` for ObjC module name extraction
- Module-to-namespace mapping: QuartzCore -> CoreAnimation, ObjectiveC -> Foundation, Dispatch -> CoreFoundation, etc.
- Cross-module type resolution via `ModuleDatabaseEmitter` serializes ObjC-rooted flags

### Validation Coverage

35/53 validation targets pass, including libraries with ObjC-rooted classes (BlinkID, SkeletonView, Kingfisher, etc.) and libraries referencing ObjC framework types (Nuke/UIImage, Alamofire/URLCredential, etc.).

---

## Problem Statement (Full ObjC Binding Generation)

The above handles Swift libraries that *reference* or *inherit from* ObjC types. What's NOT yet supported is generating bindings for **pure ObjC libraries** — frameworks with no Swift module, only Objective-C headers.

Microsoft's Objective Sharpie tool generates C# binding definitions (`ApiDefinition.cs` + `StructsAndEnums.cs`) from Objective-C headers. It's the standard way to create .NET bindings for ObjC frameworks. However:

- Sharpie uses **libclang C bindings** internally, which break with each Xcode release (Apple changes the internal Clang API)
- Workarounds exist but are fragile and require manual intervention
- The tool is effectively unmaintained
- Large ObjC-only or mixed ObjC/Swift libraries still exist (and will for years)

This document explores whether Swift Bindings could absorb Objective Sharpie's role — generating ObjC binding definitions alongside Swift bindings from a single tool.

## Architecture Comparison

### Swift Pipeline (existing)

```
Input:   ABI JSON + dylib + TBD + swiftinterface
Parse:   ABI JSON -> TypeDecl / MethodDecl model
Marshal: TypeDatabase -> type mapping -> marshaling decisions
Emit:    Swift.Module.cs (direct P/Invoke with LibraryImport)
         + wrapper.swift (Cdecl wrappers for Mono JIT)
         + regular .csproj
Runtime: CallConvSwift calling convention, Swift ARC, Value Witness Tables
```

### ObjC Pipeline (proposed)

```
Input:   Headers (.h) + modulemap (from xcframework)
Parse:   clang -ast-dump=json -> ObjCInterfaceDecl / ObjCMethodDecl model
Map:     ObjC types -> .NET types (NSString->string, NSArray->NSArray, etc.)
Emit:    ApiDefinition.cs + StructsAndEnums.cs
         + binding .csproj (<IsBindingProject>true</IsBindingProject>)
Runtime: objc_msgSend (existing .NET MAUI ObjC registrar -- no new runtime needed)
```

### Key Differences

| Aspect | Swift | Objective-C |
|--------|-------|-------------|
| Calling convention | `CallConvSwift` (register-based) | `objc_msgSend` (message dispatch) |
| Memory management | Swift ARC (`swift_retain`/`release`) | ObjC retain/release (via NSObject registrar) |
| Symbols | Mangled names (`$s4Nuke11...`) | Selectors (`-[UIImage imageNamed:]`) |
| Type system | Value types, existentials, witnesses | All reference types, categories |
| Output format | Direct P/Invoke C# code (`[LibraryImport]`) | `[BaseType]`/`[Export]` binding definitions |
| Runtime support | Custom (Swift.Runtime NuGet) | Built-in (.NET MAUI ObjC bridge) |

The parsing, marshaling, and emission layers share almost nothing. The shared surface is at the infrastructure level.

## Shared Infrastructure

Components that already exist and would be reused. Items marked **(in use)** are actively used by the ObjC-rooted class support.

| Component | Current Location | Status |
|-----------|-----------------|--------|
| XCFramework resolution (slicing, plist, arch) | `XCFrameworkResolver.cs`, `PlistReader.cs` | **In use** — already generic |
| Framework dependency resolution (incl. ObjC) | `XCFrameworkResolver.cs`, `ResolveObjCFramework()` | **In use** — handles ObjC framework deps |
| ObjC-only dependency detection | `FrameworkDependencyInfo.IsObjCOnly`, `BinaryDependencyAnalyzer.cs` | **In use** — classifies ObjC-only deps |
| ObjC type hierarchy + namespace mapping | `MarshallingHelpers`, `TypeDatabaseExtensions` | **In use** — 26 frameworks, 100+ value types |
| ObjC-rooted/bridged projections | `ObjCRootedClassProjection`, `ObjCBridgedProjection` | **In use** — full marshalling pipeline |
| TypeRecord ObjC flags | `TypeRecord.cs` (ObjCRooted, ObjCBridged bits) | **In use** — serialized in module databases |
| CLI + System.CommandLine | `Program.cs` | **In use** — add detection branch |
| Type database (ObjC bridged types) | `TypeDatabase`, `TypeDatabaseExtensions.cs` | **In use** — `IsObjCModuleType`, Apple framework modules |
| MSBuild SDK (discover -> generate -> package) | `Swift.Bindings.Sdk/` | **In use** — route by framework type |
| `.csproj` emission | `BindingProjectEmitter.cs` | **In use** — fork for `<IsBindingProject>` variant |
| NuGet pack layout | `ConsumerTargetsEmitter.cs`, SDK targets | **In use** — small adaptation needed |
| Modulemap parsing | `ParseModuleNameFromModulemap()` | **In use** — extracts ObjC module names |
| Cross-module type resolution | `ModuleDatabaseEmitter` | **In use** — serializes ObjC flags across modules |

Estimated shared code: ~25-30% of total generator codebase (higher than original estimate due to BX4 work).

## Proposed Integrated Architecture

```
CLI Entry Point (Program.cs)
  |
  +-- XCFramework Resolution (shared, in use)
  |
  +-- Framework Detection:
  |   +-- Has abi.json?       -> Swift pipeline (existing)
  |   +-- Has modulemap only? -> ObjC pipeline (new)
  |   +-- Has both?           -> Both pipelines, merged output
  |
  +-- Swift Pipeline (existing, unchanged):
  |   +-- ABI JSON Parser -> TypeDatabase -> Marshaler -> Emitter
  |   +-- ObjC-rooted class detection + projection (BX4, in use)
  |   +-- Swift wrapper compiler
  |   +-- Output: Swift.Module.cs + wrapper.swift + regular .csproj
  |
  +-- ObjC Pipeline (new):
  |   +-- Clang AST Parser (clang -ast-dump=json)
  |   +-- ObjC Type Mapper
  |   +-- ApiDefinition Emitter
  |   +-- Output: ApiDefinition.cs + StructsAndEnums.cs + binding .csproj
  |
  +-- Type Database (shared -- cross-references between pipelines, partially in use)
  +-- Dependency Resolution (shared, in use)
  +-- Project Emitter (shared, different .csproj shapes per pipeline, in use)
  +-- NuGet / MSBuild SDK (shared, in use)
```

### User Experience

```bash
# Swift library -- works exactly as today
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework Nuke.xcframework -o output/

# ObjC library -- same command, auto-detected (NEW)
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework SomeObjCLib.xcframework -o output/

# Mixed library -- both pipelines run, unified output (NEW)
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework MixedLib.xcframework -o output/
```

### MSBuild SDK Experience

```xml
<!-- Same SDK for both Swift and ObjC -- auto-detected -->
<Project Sdk="Swift.Bindings.Sdk/0.2.0">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
</Project>
```

The SDK's Discover target already finds `*.xcframework`. It would check for ABI JSON (Swift) vs modulemap-only (ObjC) and route accordingly. The user doesn't need to know or care which pipeline runs.

## Clang AST Parsing Strategy

This is the critical design decision that differentiates this approach from Sharpie.

### Why Sharpie Breaks

Sharpie used **libclang C bindings** -- a compiled native dependency linked against a specific Clang version. Apple updates Clang with each Xcode release. The API isn't guaranteed stable. Result: Sharpie needs rebuilding for each Xcode version, and Microsoft stopped doing that.

### Proposed: clang -ast-dump=json

```bash
xcrun clang -x objective-c -ast-dump=json \
  -isysroot $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F /path/to/framework \
  /path/to/Headers/Module.h
```

Advantages:
- **No native dependencies** -- invokes the Clang binary that ships with Xcode
- **Always version-matched** -- uses whatever Clang the developer has installed
- **Stable JSON schema** -- the AST dump format rarely changes between versions
- **Already available** -- every developer with Xcode has this
- **Parseable with System.Text.Json** -- no new dependencies in the generator

The JSON output contains all the declarations we need:
- `ObjCInterfaceDecl` -> `[BaseType(typeof(NSObject))]` interface
- `ObjCProtocolDecl` -> `[Protocol]` interface
- `ObjCMethodDecl` -> `[Export("selector:")]` method
- `ObjCPropertyDecl` -> `[Export("propertyName")]` property
- `EnumDecl` (NS_ENUM/NS_OPTIONS) -> C# enum with `[Native]`
- `TypedefDecl` -> type aliases
- `RecordDecl` -> C# structs
- `FunctionDecl` -> `[DllImport]` / `[LibraryImport]` declarations

### Risk: AST JSON Schema Changes

The `clang -ast-dump=json` format is not formally versioned. However:
- It has been stable across Xcode 14-16 with only additive changes
- We'd parse it defensively (ignore unknown fields, tolerate missing optional fields)
- Breaking changes would affect ALL Clang JSON consumers, creating pressure on Apple to maintain compatibility
- Worst case: a new Xcode needs parser updates, but it's JSON parsing in C# -- no native recompilation

## New Code Estimate (revised)

Several components from the original estimate now partially exist (ObjC type mapping, framework detection, namespace resolution). The remaining new work is smaller:

| Component | Estimated Lines | Description |
|-----------|----------------|-------------|
| ObjC AST Parser | 500-800 | Parse `clang -ast-dump=json` into ObjC declaration model |
| ObjC Declaration Model | 200-300 | `ObjCInterfaceDecl`, `ObjCProtocolDecl`, `ObjCMethodDecl`, etc. |
| ObjC Type Mapper | 100-250 | Map ObjC types to .NET types (partially exists via `MarshallingHelpers` mappings) |
| ApiDefinition Emitter | 400-600 | Emit `[BaseType]`/`[Export]`/`[Protocol]` C# binding definitions |
| StructsAndEnums Emitter | 200-300 | Emit `NS_ENUM` -> C# enum, struct -> C# struct |
| Binding Project Emitter (ObjC variant) | 50-100 | `.csproj` with `<IsBindingProject>true</IsBindingProject>` (base emitter exists) |
| Detection/Routing | 30-50 | Framework type detection in Program.cs (ObjC detection already exists, just needs routing) |
| **Total new code** | **~1,200-1,800** | Down from original ~1,500-2,000 due to existing ObjC infrastructure |

For context: Swift pipeline is ~35,000+ lines; ObjC-rooted support added ~2,000 lines across model/marshaler/emitter/tests.

## ObjC Type Mapping Reference

Standard mappings the ApiDefinition emitter would produce:

| ObjC Type | C# Type | Attribute |
|-----------|---------|-----------|
| `NSString *` | `string` | (automatic by registrar) |
| `NSArray *` | `NSArray` / `T[]` | `[Params]` where inferrable |
| `NSDictionary *` | `NSDictionary` | |
| `NSNumber *` | `NSNumber` | |
| `NSData *` | `NSData` | |
| `NSURL *` | `NSUrl` | |
| `NSError **` | `NSError` (out param) | |
| `BOOL` | `bool` | |
| `NSInteger` | `nint` | |
| `NSUInteger` | `nuint` | |
| `CGFloat` | `nfloat` | |
| `CGRect` | `CGRect` | |
| `SEL` | `Selector` | |
| `Class` | `Class` | |
| `id` | `NSObject` | |
| `id<Protocol>` | `IProtocol` | `[Protocol]` |
| Block types | `Action<T>` / `Func<T,R>` | `[BlockCallback]` |

Note: The Swift pipeline's `MapSwiftModuleToNetNamespace()` and `AppleObjCFrameworkModules` already map 26 Apple ObjC modules to .NET namespaces. The ObjC pipeline would reuse these mappings directly.

## Mixed Library Handling

A framework with both Swift and ObjC public API would run both pipelines:

1. **Detection**: XCFramework has both ABI JSON (Swift module) and Headers directory (ObjC)
2. **Swift pipeline**: Generates `Swift.Module.cs` with P/Invoke bindings
3. **ObjC pipeline**: Generates `ApiDefinition.cs` + `StructsAndEnums.cs` with binding definitions
4. **Type database merge**: Swift types referencing ObjC types get correct cross-references
5. **Project emission**: Single `.csproj` that includes both direct P/Invoke code and binding definitions

**Already working (partial mixed support):** Swift libraries that inherit from or reference ObjC types are fully handled today. The ObjC-rooted class support (BX4) bridges the two worlds — Swift classes emit as C# classes inheriting from their .NET MAUI ObjC counterparts. The type database, namespace mapping, and cross-module resolution infrastructure all support this. What's missing is generating bindings for ObjC API that isn't re-exported through the Swift module.

## Pros and Cons

### Pros

- **Single tool** -- one CLI, one SDK, one `dotnet new` template for any Apple framework
- **No Xcode version coupling** -- `clang -ast-dump=json` is process-invoked, not linked
- **Mixed library support** -- one invocation handles both Swift and ObjC
- **Substantial shared infrastructure** -- XCFramework resolution, ObjC type mapping, dependency handling, NuGet packaging already exist and are in active use
- **Smaller incremental effort** -- ~1,200-1,800 lines of new code (reduced from original estimate thanks to BX4 work)
- **ObjC runtime already exists** -- .NET MAUI's ObjC registrar handles all the hard runtime work
- **Natural routing** -- `SwiftModuleNotFoundException` (already thrown) becomes a routing decision; ObjC detection already exists in `BinaryDependencyAnalyzer`
- **Replaces broken tooling** -- Objective Sharpie is effectively abandoned
- **ObjC-rooted classes prove the integration model** -- the Swift pipeline already emits classes that inherit from MAUI ObjC types, validating the cross-runtime approach

### Cons

- **Scope creep risk** -- ObjC edge cases could distract from Swift pipeline improvements
- **Different output models** -- Swift emits direct C# code; ObjC emits binding definitions for the registrar. Two fundamentally different compilation models in one tool.
- **Testing surface increase** -- need ObjC-specific test frameworks and validation libraries
- **Binding project complexity** -- `<IsBindingProject>` has its own MSBuild infrastructure and quirks
- **Diminishing returns** -- ObjC is declining; most new libraries are Swift-only
- **AST schema risk** -- `clang -ast-dump=json` format could change (low probability, moderate impact)
- **ObjC has more edge cases than expected** -- categories, class extensions, `__attribute__` annotations, nullability annotations, lightweight generics, availability macros all need handling

## Alternative: Separate Standalone Tool

Instead of integrating, build a standalone `dotnet-objc-sharpie` tool that shares extracted libraries:

```
Swift.Bindings (this repo)
  +-- shared: XCFramework.Resolution NuGet package

dotnet-objc-sharpie (separate repo)
  +-- references: XCFramework.Resolution NuGet package
  +-- own: Clang parser, ApiDefinition emitter, own SDK
```

### Why Integration Is Preferred

- Duplicates CLI, SDK, NuGet packaging, dependency resolution, template infrastructure
- Two tools for the user to learn, install, and maintain
- Mixed libraries require manual coordination between two separate outputs
- The ObjC-specific code (~1,200-1,800 lines) doesn't justify a separate repository/tool/SDK
- Shared library extraction adds maintenance overhead for a single consumer
- ObjC-rooted class support already proves the two runtimes coexist in a single generated project

### When Separate Makes More Sense

- If ObjC edge cases prove far more complex than estimated (categories, class extensions, etc.)
- If the binding project model (`<IsBindingProject>`) conflicts with the Swift SDK's build targets
- If the ObjC community wants to iterate faster than the Swift pipeline allows

## Proposed Session Work (Claude-assisted)

Based on the velocity of this project (~500 commits in 1 month, all Claude-driven), and that the ObjC type system is dramatically simpler than Swift's (all reference types, no value witnesses, no existentials, no generics complexity), the full ObjC binding pipeline can be built in **3-4 focused sessions**.

For context: the entire Swift pipeline (35,000+ lines, calling conventions, ARC, value types, existentials, closures, generics, async) was built in ~30 sessions. ObjC bindings are ~5% of that complexity.

### Session O1: Foundation — Parser + Model + Routing

**Goal:** Parse any ObjC framework's headers into a structured model and route ObjC-only frameworks through the new pipeline.

**Routing & detection:**
- Wire `BinaryDependencyAnalyzer.IsObjCOnly` detection into `Program.cs` as a pipeline routing decision
- ObjC-only xcframework (modulemap + headers, no ABI JSON) -> ObjC pipeline
- Mixed xcframework (both ABI JSON and headers) -> both pipelines (see Session O3)
- Add `--objc` CLI flag as optional override (auto-detection preferred)

**Clang AST parser:**
- Invoke `xcrun clang -x objective-c -ast-dump=json` with correct `-isysroot` and `-F` flags
- Parse JSON with `System.Text.Json` into ObjC declaration model:
  - `ObjCInterfaceDecl` (classes — name, superclass, protocols, properties, methods)
  - `ObjCProtocolDecl` (protocols — required/optional methods, properties)
  - `ObjCMethodDecl` (instance/class methods — selector, params, return type)
  - `ObjCPropertyDecl` (properties — type, readonly/readwrite, nullability, getter/setter)
  - `EnumDecl` (NS_ENUM, NS_OPTIONS — name, underlying type, cases)
  - `RecordDecl` (C structs — fields, layout)
  - `TypedefDecl` (type aliases — resolve chains)
  - `FunctionDecl` (C functions — name, params, return type)
- Handle nullability annotations (`_Nullable`, `_Nonnull`, `NS_ASSUME_NONNULL_BEGIN` regions)
- Handle availability/deprecation attributes (`__attribute__((availability(...)))`)
- Filter to public API only (skip internal/private declarations from transitive includes)

**ObjC type mapper:**
- Map ObjC types to .NET types (reuse existing `MarshallingHelpers` + `TypeDatabaseExtensions` mappings)
- Block types -> `Action<T>` / `Func<T,R>` with `[BlockCallback]`
- `id<Protocol>` -> `IProtocol` interface references
- Lightweight generics (`NSArray<NSString *>`) -> preserve generic info where possible

**Tests:**
- Unit tests for Clang AST JSON parsing (mock JSON fragments for each decl type)
- Unit tests for type mapping
- Integration test: parse a real ObjC framework header (e.g., from Realm or Stripe3DS2 in validation set)

**Deliverable:** `dotnet run --project src/Swift.Bindings/src -- --xcframework Realm.xcframework -o output/` parses headers and dumps the ObjC model (no emission yet).

### Session O2: Emission — ApiDefinition + StructsAndEnums + Binding Project

**Goal:** Emit compilable `ApiDefinition.cs`, `StructsAndEnums.cs`, and an `<IsBindingProject>` `.csproj` from the ObjC model.

**ApiDefinition emitter:**
- `[BaseType(typeof(NSObject))]` / `[BaseType(typeof(SuperClass))]` for classes
- `[Export("selector:")]` for methods and properties
- `[Static]` for class methods, `[Abstract]` for required protocol methods
- `[Protocol]`, `[Model]` for ObjC protocols (with optional method handling)
- `[NullAllowed]` based on nullability annotations
- Constructor binding: `[Export("initWithFoo:bar:")]`
- Factory methods: `[Static] [Export("fooWithBar:")]`
- Categories: merge onto main class definition (most compatible with existing MAUI patterns)
- Delegate/event patterns: detect `delegate` properties, emit `[Wrap]`/`[EventArgs]` where possible

**StructsAndEnums emitter:**
- `NS_ENUM` -> `public enum Foo : long { ... }` with `[Native]`
- `NS_OPTIONS` -> `[Flags] public enum Foo : ulong { ... }` with `[Native]`
- C structs -> `[StructLayout(LayoutKind.Sequential)]` structs
- C function exports -> `[DllImport]` / `[LibraryImport]` declarations

**Binding project emitter:**
- Fork `BindingProjectEmitter` for `<IsBindingProject>true</IsBindingProject>` variant
- Include `<NativeReference>` for the framework
- Correct `<Compile Include="ApiDefinition.cs" />` and `<Compile Include="StructsAndEnums.cs" />`
- Wire into existing NuGet packaging flow

**Tests:**
- Unit tests for each emission pattern (classes, protocols, enums, structs, categories)
- Compile gate: generated `ApiDefinition.cs` + `StructsAndEnums.cs` must compile in a binding project
- Validate against Realm and Stripe3DS2 (ObjC-only frameworks already in validation set)

**Deliverable:** `dotnet run --project src/Swift.Bindings/src -- --xcframework Realm.xcframework -o output/` produces a compilable binding project. `cd output && dotnet build` succeeds.

### Session O3: Mixed Frameworks + SDK Integration

**Goal:** Handle libraries that have both Swift and ObjC public API (common pattern: ObjC core with Swift convenience layer), and wire into the MSBuild SDK.

**Mixed framework pipeline:**
- Detect mixed frameworks: has both ABI JSON (Swift module) and ObjC headers with public API not re-exported through Swift
- Run both pipelines, merge output into a single project:
  - Swift API -> direct P/Invoke C# code (existing pipeline)
  - ObjC-only API -> `ApiDefinition.cs` + `StructsAndEnums.cs` (new pipeline)
  - Single `.csproj` that includes both — needs `<IsBindingProject>` for the ObjC portion while also having regular C# for Swift
- **Type deduplication**: Types exposed through both Swift ABI and ObjC headers should be bound once. The Swift pipeline already handles ObjC-rooted classes via `ObjCRootedClassProjection` — the ObjC pipeline should skip types that appear in the Swift ABI JSON.
- Cross-pipeline type references: ObjC types referenced from Swift code use `ObjCBridgedProjection` (already works). Swift types referenced from ObjC categories may need TypeDatabase cross-entries.

**MSBuild SDK integration:**
- SDK Discover target already finds `*.xcframework` — add framework type classification
- Route to Swift generator, ObjC generator, or both based on detection
- Single `dotnet build` and `dotnet pack` for any framework type
- `<SwiftFrameworkDependency>` items may point to ObjC-only frameworks — handle gracefully

**Mixed `.csproj` challenge:**
- `<IsBindingProject>` changes the entire MSBuild compilation model
- Option A: Two projects in one output dir (Swift `.csproj` + ObjC binding `.csproj`) with a `<ProjectReference>`
- Option B: Single project with `<IsBindingProject>` and both regular + binding C# code (may not work — needs investigation)
- Option C: Emit ObjC bindings as regular P/Invoke code (skip `<IsBindingProject>` entirely) — more code but simpler build model, consistent with Swift pipeline

**Tests:**
- Unit tests for mixed detection and type deduplication
- Integration test: a known mixed framework (find or create one in validation set)
- SDK integration test: `dotnet build` with SDK for ObjC-only, Swift-only, and mixed xcframeworks

**Deliverable:** Mixed frameworks produce a unified, compilable binding project. MSBuild SDK works for all three framework types.

### Session O4: Validation + Edge Cases + Polish

**Goal:** Validate against real-world ObjC frameworks, fix edge cases, add to validation pipeline.

**Validation targets:**
- **Realm** (already in validation set, currently skipped as ObjC-only) — large, complex ObjC framework
- **Stripe3DS2** (already in validation set, currently skipped as ObjC-only) — moderate size
- Add 2-3 additional ObjC-only frameworks to validation set (Firebase components, Facebook SDK, or similar)
- Find or add a mixed Swift+ObjC library to validation set

**Edge case handling (as discovered during validation):**
- Categories across multiple header files
- Class extensions (anonymous categories)
- `__attribute__` annotations beyond availability (swift_name, objc_runtime_name, etc.)
- Forward declarations (`@class Foo;` before full definition)
- `CF_ENUM` / `CF_OPTIONS` variants
- Typedef chains (e.g., `typedef NSString *FooKey NS_TYPED_ENUM`)
- Complex block signatures (blocks returning blocks, blocks with nullable params)
- `NS_SWIFT_NAME` annotations (relevant for mixed frameworks — tells you what Swift sees)

**Validation pipeline integration:**
- Add ObjC targets to `validation-libraries.json` (promote Realm, Stripe3DS2 from "known non-binding failures" to real targets)
- `./validate-libraries.sh` runs both Swift and ObjC validation
- Baseline update for new targets

**Documentation:**
- Update `CLAUDE.md` with ObjC pipeline usage
- Update SDK docs with mixed framework guidance
- Error codes for ObjC-specific failures (SWIFTBIND0xx range)

**Deliverable:** All ObjC validation targets compile. Mixed framework support validated. Documentation complete.

### Estimated Total: 3-4 sessions

The biggest risk is Session O3 (mixed framework `.csproj` model). If `<IsBindingProject>` proves incompatible with mixed output, the fallback is Option C (emit ObjC bindings as regular P/Invoke code), which is more work but avoids the MSBuild complexity entirely.

---

## Implementation Phases (original human-paced estimate, preserved for reference)

### Phase 1: Detection + Routing (~0.5 day)
- Route ObjC-only frameworks (modulemap present, no ABI JSON) — detection already exists in `BinaryDependencyAnalyzer`
- Add `--objc` CLI flag (optional — auto-detection preferred)
- Route to ObjC pipeline stub (initially: informative error message)

### Phase 2: Clang AST Parser (~3-5 days)
- Invoke `xcrun clang -ast-dump=json` with correct SDK/framework flags
- Parse JSON into `ObjCInterfaceDecl`, `ObjCProtocolDecl`, `ObjCMethodDecl` model
- Handle: classes, protocols, methods, properties, enums, structs, typedefs, C functions
- Handle: nullability annotations (`_Nullable`, `_Nonnull`), availability, deprecation

### Phase 3: ApiDefinition Emitter (~3-5 days)
- Emit `[BaseType]`, `[Export]`, `[Protocol]`, `[Model]` attributes
- Emit `StructsAndEnums.cs` for `NS_ENUM`, `NS_OPTIONS`, C structs
- Type mapping: ObjC types -> .NET types (blocks -> delegates, etc.) — leverage existing `MarshallingHelpers` and `TypeDatabaseExtensions` mappings
- Handle: inheritance, protocol conformance, optional protocol methods
- Handle: class methods vs instance methods, constructors, factory methods

### Phase 4: Binding Project Emission (~1 day)
- Fork existing `BindingProjectEmitter` for `<IsBindingProject>true</IsBindingProject>` variant
- Include native framework reference
- Wire into existing NuGet packaging flow (`ConsumerTargetsEmitter`)

### Phase 5: SDK Integration (~2-3 days)
- MSBuild SDK detects framework type and routes to correct generator mode
- Single `dotnet build` for either Swift or ObjC frameworks
- Mixed framework support (both pipelines)

### Phase 6: Validation (~3-5 days)
- Test against known ObjC-only frameworks (e.g., Realm, Stripe3DS2 — already in validation set as known ObjC-only)
- Compare output against hand-written bindings or Sharpie output
- Verify binding project compiles and runs on iOS Simulator

**Total estimate: ~2-3 weeks of focused effort.** Slightly reduced from original estimate due to existing infrastructure.

## Open Questions

1. **Categories**: ObjC categories add methods to existing classes. Should these become C# extension methods, or methods on the main binding class?
2. **Lightweight generics**: ObjC has `NSArray<NSString *>` -- should we preserve generic type info in the binding?
3. **Swift-imported ObjC**: When Swift re-exports an ObjC type (common in mixed frameworks), which pipeline owns it? Note: the Swift pipeline already handles ObjC types referenced from Swift via `ObjCBridgedProjection` — this question is about the *definition* ownership, not references.
4. **Binding project compatibility**: Does `<IsBindingProject>` work correctly with .NET 10 and the latest MAUI? It's had issues historically.
5. **Block ABI**: ObjC blocks have a specific ABI layout. The registrar handles this, but do we need to annotate parameters correctly for complex block signatures?
6. **SDK naming**: **Decision: keep `Swift.Bindings.Sdk`.** The Swift pipeline is the primary and complex capability (~95% of codebase). Renaming to `Apple.Bindings.Sdk` would imply multi-platform support (macOS/tvOS/etc.) beyond the current scope, and the churn cost (SDK, runtime, template, docs, repo) isn't justified.

## Naming Convention (decided)

Keep all existing Swift naming. ObjC-only libraries get a `.ObjC.` suffix in their NuGet package name. Auto-detection handles routing — one template, one SDK, one CLI.

| Framework Type | NuGet Package | Example |
|---|---|---|
| Swift library | `{Library}.Swift.iOS` | `Nuke.Swift.iOS` |
| ObjC-only library | `{Library}.ObjC.iOS` | `Realm.ObjC.iOS` |
| Mixed (Swift + ObjC) | `{Library}.Swift.iOS` | `MixedLib.Swift.iOS` |

- **SDK**: `Swift.Bindings.Sdk` (unchanged)
- **Runtime**: `Swift.Runtime` (unchanged — ObjC bindings don't need it, they use MAUI's registrar)
- **Template**: `dotnet new swift-binding` (unchanged — auto-detects framework type, works for both)
- **Mixed libraries** get `.Swift.` because the Swift pipeline is the primary binding mechanism; the ObjC portion is supplementary
- The `.ObjC.` suffix for ObjC-only packages signals to consumers that this is a traditional binding project (`[Export]`/`[BaseType]`) with different debugging/runtime characteristics than direct P/Invoke

## References

- Xamarin ObjC binding docs: https://learn.microsoft.com/en-us/previous-versions/xamarin/cross-platform/macios/binding/
- Clang AST dump format: output of `clang -ast-dump=json`
- .NET MAUI binding project: `<IsBindingProject>true</IsBindingProject>` in `.csproj`
- Objective Sharpie (archived): https://learn.microsoft.com/en-us/xamarin/cross-platform/macios/binding/objective-sharpie/
- **Existing ObjC infrastructure in this repo:**
  - `TypeDatabaseExtensions.cs` — `IsObjCModuleType`, `AppleObjCFrameworkModules`, `AppleFrameworkValueTypes`
  - `XCFrameworkResolver.cs` — `ResolveObjCFramework()`, `ParseModuleNameFromModulemap()`
  - `BinaryDependencyAnalyzer.cs` — `IsObjCOnly` detection
  - `ObjCRootedClassProjection.cs` / `ObjCBridgedProjection.cs` — marshalling projections
  - `ClassHandler.cs` — ObjC-rooted class emission (BX4)
  - `MarshallingHelpers.cs` — `GetObjCBaseTypeName()`, `MapSwiftModuleToNetNamespace()`
  - `ModuleProcessor.cs` — `ResolveClassHierarchy()`, `UpdateObjCRootedTypeRecords()`
  - `ClassObjCRootedTests.cs` — comprehensive test coverage (957 lines)
