# Objective-C Binding Integration

## Status: Session O1 Complete — Parser + Model + Routing Implemented

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

## Output Format and User Expectations

The ObjC pipeline emits the same `ApiDefinition.cs` + `StructsAndEnums.cs` files that Sharpie users are familiar with, using the same `[BaseType]`, `[Export]`, `[Protocol]`, `[Model]` attribute format. The binding project compilation model is identical — the MAUI registrar processes these files the same way regardless of which tool generated them.

**Users will still need to hand-edit some output.** This is inherent to the binding definition format, not a tooling limitation:
- **`[Model]` vs `[Protocol]`** — The ObjC AST doesn't always make it clear whether a protocol is meant to be a delegate model (concrete default impl) or a pure protocol (abstract interface). v1 emits `[Protocol]` conservatively; users add `[Model]` where needed. Future sessions can improve this by analyzing usage patterns (e.g., is the protocol used as a `delegate` property type?).
- **`[NullAllowed]` placement** — Depends on nullability annotations in headers. Well-annotated frameworks are fine. Older headers without `NS_ASSUME_NONNULL_BEGIN` need manual review.
- **Complex block signatures** — May need manual `delegate` typedef definitions for deeply nested block types.

**v1 target: Sharpie-equivalent output quality.** The immediate win is a tool that actually works with current Xcode versions. Improvements over Sharpie are expected over time — we have advantages Sharpie didn't (the existing type database with 26 framework mappings, access to Swift ABI JSON for mixed frameworks, and the ability to analyze protocol usage patterns). The goal is to surpass Sharpie quality, but matching it is already a major win given Sharpie is effectively dead.

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
  |   +-- Has both?           -> Both pipelines, two-project output
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

# Mixed library -- both pipelines run, two projects emitted (NEW)
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

## Code Estimate (revised with O1 actuals)

O1 is complete. Remaining work (O2-O4) estimated below.

| Component | Lines | Status |
|-----------|-------|--------|
| ObjC Declaration Model | ~200 | ✅ O1 — 4 files |
| ObjC Type Ref Parser | ~120 | ✅ O1 |
| Clang AST Parser | ~500 | ✅ O1 |
| Clang AST Invoker | ~185 | ✅ O1 |
| ObjC Pipeline | ~100 | ✅ O1 |
| Detection/Routing (Program.cs) | ~40 | ✅ O1 |
| ObjC Tests (O1) | ~900 | ✅ O1 — 39 tests |
| ObjC Type Mapper | 100-250 | ⬜ O2 — map ObjC types to .NET types for emission |
| ApiDefinition Emitter | 400-600 | ⬜ O2 — `[BaseType]`/`[Export]`/`[Protocol]` C# binding definitions |
| StructsAndEnums Emitter | 200-300 | ⬜ O2 — `NS_ENUM` → C# enum, struct → C# struct |
| Binding Project Emitter (ObjC variant) | 50-100 | ⬜ O2 — `.csproj` with `<IsBindingProject>` |
| Mixed Framework Support | 100-200 | ⬜ O3 — two-project output, type dedup |
| MSBuild SDK Integration | 50-100 | ⬜ O3 |
| **Total new code** | **~2,500-3,500** | **~2,000 complete (O1)** |

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
4. **Member-level dedup**: Types appearing in both pipelines are NOT suppressed entirely — ObjC categories can add members not in Swift ABI. The ObjC pipeline skips individual members with Swift ABI equivalents and emits ObjC-only additions.
5. **Project emission**: Two projects — Swift `.csproj` (regular) + ObjC `.csproj` (`<IsBindingProject>`) with `<ProjectReference>` from Swift to ObjC

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

Based on the velocity of this project (~500 commits in 1 month, all Claude-driven), and that the ObjC type system is dramatically simpler than Swift's (all reference types, no value witnesses, no existentials, no generics complexity), the full ObjC binding pipeline can be built in **3-5 focused sessions**. Session O1 (foundation) is complete.

For context: the entire Swift pipeline (35,000+ lines, calling conventions, ARC, value types, existentials, closures, generics, async) was built in ~30 sessions. ObjC bindings are ~5% of that complexity. The 5th session is a buffer for edge cases discovered during real-framework validation (vendor header quirks, ObjC patterns not seen in initial targets).

### Session O1: Foundation — Parser + Model + Routing ✅ COMPLETE

**Completed:** 2026-03-05

**What was built:**

All planned components are implemented and tested (39 ObjC-specific tests, all passing).

**Source files** (`src/Swift.Bindings/src/ObjC/`):
- `Model/ObjCTypeRef.cs` — Type reference record with nullability, blocks, generics, double pointers, protocol qualification
- `Model/ObjCAvailability.cs` — Platform availability annotation record
- `Model/ObjCDeclarations.cs` — All declaration records: class, protocol, method, property, enum, struct, function, constant, typedef, parameter, enum case, struct field
- `Model/ObjCModule.cs` — Top-level container with computed `TotalDeclarations`
- `Parser/ObjCTypeRefParser.cs` — Parses qualType strings (`NSString * _Nonnull`, `void (^)(NSString *)`, etc.) into `ObjCTypeRef`
- `Parser/ClangAstInvoker.cs` — Invokes `xcrun clang -Xclang -ast-dump=json`, resolves umbrella headers (4-strategy: convention → modulemap directive → directory umbrella with `@import` → explicit header list)
- `Parser/ClangAstParser.cs` — Parses clang AST JSON into `ObjCModule` (~500 lines). Two-pass: parse all decls, then merge categories onto classes. Handles: stateful location tracking (clang omits `loc.file` for same-file decls), implicit accessor filtering, optional method inference from source headers, forward declaration skipping, multi-field location fallback chain for public API filtering
- `Pipeline/ObjCPipeline.cs` — Orchestrator: resolve framework → find umbrella header → invoke clang → parse AST → dump summary

**Test files** (`src/Swift.Bindings/tests/UnitTests/ObjCTests/`):
- `Parser/ClangAstParserTests.cs` — 16 test cases covering all decl types, category merging, implicit filtering, optional methods, location filtering, forward declarations
- `Parser/ObjCTypeRefParserTests.cs` — 12 test cases for qualType parsing
- `Parser/ClangAstInvokerTests.cs` — 7 test cases (mock command runner, umbrella header resolution)
- `Pipeline/ObjCPipelineIntegrationTests.cs` — 2 integration tests (xcframework fixture routing, CoreBluetooth real-framework parsing)

**Program.cs changes:**
- Added `--objc` CLI flag for forced ObjC pipeline routing
- Added `SwiftModuleNotFoundException` catch for auto-detection fallback to ObjC pipeline
- Swift path completely unchanged

**Key design decisions made during implementation:**
1. `-fmodules` suppresses AST expansion — only used for `@import` directory umbrella strategy, NOT for default header compilation
2. Category merging reads `element.interface.name` (the owning class), not `element.name` (the category name)
3. Optional protocol methods inferred from source header `@optional`/`@required` section boundaries (clang JSON lacks `isOptional` on methods). Graceful degradation to required when source unavailable
4. Implicit property accessor methods (`isImplicit: true`) filtered from method lists
5. Stateful `currentFile` tracking for location filtering — handles clang omitting `loc.file` on consecutive same-file declarations

### Session O2: Emission — ApiDefinition + StructsAndEnums + Binding Project

**v1 scope gate:** Emit correct `[BaseType]`/`[Export]`/`[Protocol]` attributes, enums, structs, constants. Categories merge onto main class. Do NOT implement delegate/event sugar (`[Wrap]`/`[EventArgs]`), advanced block signature inference, or `[Verify]` hint annotations in v1 — those are polish for O4/O5.

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
- Delegate/event patterns: **deferred to O4/O5** — detect `delegate` properties but don't emit `[Wrap]`/`[EventArgs]` sugar in v1

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
- Apply naming convention: ObjC-only libraries emit as `{Library}.ObjC.iOS` (decided — see Naming Convention section)

**Tests:**
- Unit tests for each emission pattern (classes, protocols, enums, structs, categories)
- Compile gate: generated `ApiDefinition.cs` + `StructsAndEnums.cs` must compile in a binding project
- Validate against Realm and Stripe3DS2 (ObjC-only frameworks already in validation set)

**Deliverable:** `dotnet run --project src/Swift.Bindings/src -- --xcframework Realm.xcframework -o output/` produces a compilable binding project. `cd output && dotnet build` succeeds.

### Session O3: Mixed Frameworks + SDK Integration

**Goal:** Handle libraries that have both Swift and ObjC public API (common pattern: ObjC core with Swift convenience layer), and wire into the MSBuild SDK.

**Mixed framework pipeline:**
- Detect mixed frameworks: has both ABI JSON (Swift module) and ObjC headers with public API not re-exported through Swift
- Run both pipelines, emit two projects (Option A):
  - Swift API -> direct P/Invoke C# code + regular `.csproj` (existing pipeline)
  - ObjC-only API -> `ApiDefinition.cs` + `StructsAndEnums.cs` + binding `.csproj` (`<IsBindingProject>`)
  - Swift project has `<ProjectReference>` to ObjC binding project
- **Type deduplication** (member-level, not type-level): Types that appear in both Swift ABI and ObjC headers must NOT be suppressed entirely — ObjC categories can add members (selectors, protocol refinements) not visible in Swift ABI JSON. Instead, emit the ObjC type but skip individual members that have Swift ABI equivalents. The Swift pipeline already handles ObjC-rooted classes via `ObjCRootedClassProjection`, so the ObjC pipeline defers to Swift for members it already binds and adds ObjC-only members on top.
- Cross-pipeline type references: ObjC types referenced from Swift code use `ObjCBridgedProjection` (already works). Swift types referenced from ObjC categories may need TypeDatabase cross-entries.

**MSBuild SDK integration:**
- SDK Discover target already finds `*.xcframework` — add framework type classification
- Route to Swift generator, ObjC generator, or both based on detection
- Single `dotnet build` and `dotnet pack` for any framework type
- `<SwiftFrameworkDependency>` items may point to ObjC-only frameworks — handle gracefully

**Why two projects for mixed frameworks:**

The core issue is that `<IsBindingProject>true</IsBindingProject>` fundamentally replaces how MSBuild compiles C#. In a normal project, `.cs` files are regular source code compiled by Roslyn. In a binding project, `ApiDefinition.cs` files aren't real C# — they're partial interfaces decorated with `[BaseType]`/`[Export]` attributes that the MAUI registrar processes to generate trampolines, selector dispatch code, and ObjC runtime registration. The registrar injects its own generated code and takes over the compile pipeline.

This means you can't mix the two models in one project:
- The Swift pipeline's output is regular C# (`[LibraryImport]` P/Invoke declarations, concrete classes, real method bodies). It needs a normal `CoreCompile`.
- The ObjC pipeline's output is binding definitions (partial interfaces with no method bodies, attribute-driven). It needs the registrar's specialized compile pipeline.
- If you set `<IsBindingProject>true`, the registrar takes over and the Swift P/Invoke code won't compile correctly (it's not binding definition syntax). If you leave it false, the `ApiDefinition.cs` won't compile (partial interfaces with no bodies aren't valid C#).

Two projects is the clean solution — each uses its native build model, and `<ProjectReference>` wires them together. The consumer sees a single NuGet package; the two-project split is an internal build detail.

**Mixed `.csproj` options:**
- **Option A (primary)**: Two projects in one output dir — Swift `.csproj` (regular P/Invoke code) + ObjC binding `.csproj` (`<IsBindingProject>`) with a `<ProjectReference>` from Swift to ObjC. Clean separation, each project uses its native build model.
- **Option B (investigate but don't depend on)**: Single project with `<IsBindingProject>` and both regular + binding C# code. Almost certainly incompatible for the reasons above.
- **Option C (last resort only)**: Emit ObjC bindings as direct P/Invoke code (skip `<IsBindingProject>` entirely). This is NOT a simple fallback — it means recreating registrar-like behavior (selector dispatch, method family semantics, block ABI handling, metadata mapping). Only pursue if Option A proves unworkable.

**NuGet packaging contract (Option A):**
- The **Swift project** owns the NuGet pack metadata (package ID, version, description). It produces the final `.nupkg`.
- The **ObjC binding project** is a build-time dependency only — it compiles into a DLL that the Swift project references, but does NOT produce its own NuGet package.
- **Native framework embedding**: The ObjC binding `.csproj` includes the `<NativeReference>` so the framework gets embedded in its output. The Swift project's `<ProjectReference>` transitively includes the native framework in the final pack. Must verify this transitive flow works — if not, the Swift project also needs the `<NativeReference>`.
- For **ObjC-only** libraries (no Swift project), the ObjC binding `.csproj` IS the pack project and owns all metadata directly.

**Tests:**
- Unit tests for mixed detection and type deduplication
- Integration test: a known mixed framework (find or create one in validation set)
- SDK integration test: `dotnet build` with SDK for ObjC-only, Swift-only, and mixed xcframeworks

**Deliverable:** Mixed frameworks produce two compilable projects (Swift + ObjC binding) with `ProjectReference` wiring. ObjC-only frameworks produce a single binding project. MSBuild SDK works for all three framework types.

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
- `instancetype` return types — must map to the declaring class, not `id`
- `__kindof` type annotations — covariant return type hints
- Variadic methods (`-[NSString stringWithFormat:]`) — limited binding support, mark or skip
- `NS_REFINED_FOR_SWIFT` — methods hidden from Swift, ObjC pipeline should still bind them
- ObjC++ headers (`.mm` / mixed C++ content) — detect and skip or fall back gracefully
- Exported constants (`extern NSString *const`) — `VarDecl` nodes, emit as `[Field]` attributes

**Validation pipeline integration:**
- Add ObjC targets to `validation-libraries.json` (promote Realm, Stripe3DS2 from "known non-binding failures" to real targets)
- `./validate-libraries.sh` runs both Swift and ObjC validation
- Baseline update for new targets

**Documentation (user-facing):**
- **`README.md`**: Update project description and feature list to reflect that the tool handles pure ObjC, hybrid (Swift + ObjC), and pure Swift frameworks. The README is the first thing users see — it must be clear this isn't Swift-only.
- **`docs/objc-bindings.md`** (new): Dedicated ObjC binding guide covering:
  - What the tool does for ObjC frameworks (auto-detection, `ApiDefinition.cs` + `StructsAndEnums.cs` generation)
  - How it differs from Objective Sharpie (no libclang dependency, always Xcode-version-matched)
  - ObjC-only workflow: drop xcframework, run generator or `dotnet build` with SDK
  - Mixed framework workflow: what gets bound where, two-project output explained
  - Supported ObjC patterns and known limitations
  - Naming convention (`.ObjC.iOS` vs `.Swift.iOS`)
- **`docs/binding-overview.md`**: Update to cover all three framework types (currently Swift-focused)
- **`docs/Troubleshooting.md`**: Add ObjC-specific SWIFTBIND error codes and common failure modes (ObjC++ headers, missing modulemap, clang parse failures)
- **`CLAUDE.md`**: Update with ObjC pipeline usage and CLI examples

**Documentation (internal):**
- Error codes for ObjC-specific failures (SWIFTBIND0xx range)
- Update SDK design doc with mixed framework routing

**Deliverable:** All ObjC validation targets compile. Mixed framework support validated. Documentation complete.

### Estimated Total: 3-5 sessions (1 complete, 2-4 remaining)

Session O1 is complete. The biggest risk is Session O3 (mixed framework `.csproj` model). Option A (two projects + `ProjectReference`) is the primary strategy. Option C (ObjC as direct P/Invoke) is last resort only — it's a major architectural fork, not a simple fallback.

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
- **Template**: `dotnet new swift-binding` (unchanged — auto-detects framework type, works for both). Add `dotnet new objc-binding` as a template alias pointing to the same underlying template for discoverability by users searching for ObjC binding tooling.
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
