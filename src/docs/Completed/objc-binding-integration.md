# Objective-C Binding Integration

## Status: Session O6 Complete — Mixed-Framework `[Category]` Emission (55/55 Validation Targets)

**Date:** 2026-02-14 (original), updated 2026-03-06

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

55/55 validation targets pass (including 3 ObjC: BRLMPrinterKit, Realm, Stripe3DS2), libraries with ObjC-rooted classes (BlinkID, SkeletonView, Kingfisher, etc.) and libraries referencing ObjC framework types (Nuke/UIImage, Alamofire/URLCredential, etc.).

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
| ObjC Type Mapper | ~110 | ✅ O2+O4 — pointer/primitive/block/instancetype/protocol-qualified id/CoreFoundation ref mapping. AST-driven generic type param resolution (no hardcoded fallback). |
| ApiDefinition Emitter | ~230 | ✅ O2+O4 — `[BaseType]`/`[Export]`/`[Protocol]`/`[Abstract]`, availability, NSError out, custom setter selectors, class-scoped generic param threading, `using CoreFoundation` |
| StructsAndEnums Emitter | ~155 | ✅ O2 — enums (prefix strip, [Flags]), structs, [Field] constants (extern-only), [DllImport] functions |
| Binding Project Emitter (ObjC variant) | ~60 | ✅ O2 — `<IsBindingProject>` `.csproj`, conditional StructsAndEnums, relative NativeReference |
| ObjC Tests (O2) | ~1,350 | ✅ O2 — 73 new tests (type mapper, api definition, structs/enums, binding project, integration) |
| Mixed Framework Detection | ~60 | ✅ O3 — `DetectMixedFrameworkObjC`, post-hoc validation |
| Member-Level Dedup + `[Category]` Emission | ~30+100 | ✅ O3/O6 — `FilterForMixedFramework` (O3 type-level → O6 member-level), `[Category]` extraction, `EmitCategory` |
| ObjC Metadata Props Emitter | ~50 | ✅ O3 — `binding-metadata.props` for ObjC/mixed |
| Dual-Pipeline Orchestration | ~50 | ✅ O3 — Program.cs mixed detection + ObjC pipeline invocation |
| BindingProject ProjectRef | ~15 | ✅ O3 — conditional `<ProjectReference>` for mixed |
| MSBuild SDK Integration | ~80 | ✅ O3 — `SwiftFrameworkType`, Target 4c, Target 5 split, guards |
| Category Origin Tracking | ~10 | ✅ O3 — `IsFromCategory` (O4 infrastructure) |
| ObjC Tests (O3) | ~700 | ✅ O3 — 26 new tests |
| O5 zero-error fixes (all emitters + mapper + parser) | ~300 | ✅ O5 — typedef resolution (fully threaded), block typedef maps, constructor dedup, protocol init guard, DisableDefaultCtor, method dedup (with second-order collision handling), module-local type skipping, fixed-size arrays (end-to-end model), binding project fixes |
| ObjC Tests (O5) | ~450 | ✅ O5 — 29 new tests |
| O6 `[Category]` emission | ~200 | ✅ O6 — `ObjCCategoryDecl` model, parser category preservation + dedup, `EmitCategory`, `FilterForMixedFramework` member-level rewrite, `Program.cs` mixed classification fix |
| ObjC Tests (O6) | ~770 | ✅ O6 — 24 new tests (parser, emitter, pipeline) |
| **Total new code** | **~6,600+** | **~6,500 complete (O1+O2+O3+O4+O5+O6)** |

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

1. **Detection**: After Swift pipeline succeeds, `DetectMixedFrameworkObjC()` checks for modulemap + non-Swift headers
2. **Swift pipeline**: Generates `Swift.Module.cs` with P/Invoke bindings (as normal)
3. **ObjC pipeline**: Runs with member-level dedup — shared classes are dropped, but their ObjC category members are extracted as `[Category]` binding interfaces
4. **Post-hoc validation**: If ObjC pipeline finds zero classes + protocols + categories (only constants like version numbers), the framework is NOT treated as mixed
5. **Member-level dedup (O6)**: Shared classes are removed from ObjC output, but their category members are preserved. Each category becomes a separate `[Category]` binding interface (e.g., `Widget_Extras`). Shared protocols are still dropped entirely. Category-adopted protocols and lightweight generic params are preserved from the original category/class declarations.
6. **Project emission**: Two projects — Swift `.csproj` (regular) + ObjC `.csproj` (`<IsBindingProject>`) with `<ProjectReference>` from Swift to ObjC

**Already working:** Swift libraries that inherit from or reference ObjC types are fully handled. The ObjC-rooted class support (BX4) bridges the two worlds — Swift classes emit as C# classes inheriting from their .NET MAUI ObjC counterparts.

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

### Session O2: Emission — ApiDefinition + StructsAndEnums + Binding Project ✅ COMPLETE

**Completed:** 2026-03-05

**What was built:**

All planned emission components are implemented and tested (73 new ObjC-specific tests, 153 total ObjC tests, all passing). 53/53 Swift validation targets unaffected.

**Source files** (`src/Swift.Bindings/src/ObjC/Emitter/`):
- `ObjCTypeMapper.cs` — Static type mapper: pointer types (NSString→string, NSURL→NSUrl, etc.), primitives (BOOL→bool, NSInteger→nint, etc.), special types (SEL→Selector, Class→Class, id→NSObject, id<Proto>→IProto), instancetype→declaringClassName, blocks→Action<T>/Func<T,R> (>16 params→NSObject), CoreFoundation ref types (dispatch_queue_t→DispatchQueue, CGImageRef→CGImage, etc.), AST-driven generic type params (class-scoped, no hardcoded fallback)
- `ApiDefinitionEmitter.cs` — Emits `ApiDefinition.cs`: protocols first (with `[Protocol]`, `[BaseType(typeof(NSObject))]`, `I` prefix, `[Abstract]` for required members), then classes (`[BaseType(typeof(Super))]`, protocol adoption, constructors from init* selectors, `[Static]` for class methods/properties, `[NullAllowed]` from nullability annotations, `[return: NullAllowed]`, NSError** → `[NullAllowed] out NSError error`, block params → Action/Func, custom setter selectors via `[Export("setSomething:")] set;`, iOS-only `[Introduced]`/`[Deprecated]` availability)
- `StructsAndEnumsEmitter.cs` — Emits `StructsAndEnums.cs` (null if nothing to emit): enums with `[Native]`/`[Flags]`, `: long`/`: ulong`, all-or-nothing prefix stripping; structs with `[StructLayout(LayoutKind.Sequential)]` and PascalCase fields; `{Module}Constants` public static partial class with `[Field]` for extern NSString/nint/nuint/nfloat/int/float/double constants, `[DllImport]` for functions; non-extern constants skipped, unsupported types emit `// TODO:` comments
- `ObjCBindingProjectEmitter.cs` — Emits `{PackageId}.csproj` with `<IsBindingProject>true</IsBindingProject>`, `<ObjcBindingApiDefinition>`, conditional `<ObjcBindingCoreSource>`, `<NativeReference>` with relative path. No Swift.Runtime, no AllowUnsafeBlocks, no DisableRuntimeMarshalling.

**Pipeline wiring:**
- `ObjCPipeline.cs` — Added `namespacePattern`/`packageId` params, namespace resolution via `NamespacePatternResolver`, emission step after AST parse, `ObjCPipelineResult` extended with emitted file paths
- `Program.cs` — Both ObjC routing paths (forced `--objc` and auto-detect fallback) pass `namespacePattern`/`packageId`

**Test files** (`src/Swift.Bindings/tests/UnitTests/ObjCTests/Emitter/`):
- `ObjCTypeMapperTests.cs` — 30 test cases (primitives, pointers, blocks, instancetype, protocol-qualified id, passthrough, NSError**)
- `ApiDefinitionEmitterTests.cs` — 24 test cases (classes, protocols, constructors, availability, NSError out, blocks, nullable, custom setter selectors, ordering)
- `StructsAndEnumsEmitterTests.cs` — 17 test cases (prefix stripping, [Flags], explicit values, structs, [Field] constants, non-extern skipping, DllImport, null return, mixed module)
- `ObjCBindingProjectEmitterTests.cs` — 10 test cases (IsBindingProject, ApiDefinition/CoreSource, NativeReference, no Swift.Runtime/AllowUnsafeBlocks, custom PackageId)
- `ObjCPipelineIntegrationTests.cs` — 1 new test (`Pipeline_XCFrameworkFixture_EmitsBindingFiles`) verifying full pipeline emits files with expected content patterns

**Key design decisions made during implementation:**
1. `[Protocol]` only (no `[Model]`) — AST cannot reliably distinguish delegate-model protocols from pure protocols; users add `[Model]` where needed (deferred to O4)
2. `instancetype` in protocols maps to `NSObject` (registrar resolves at runtime); in classes maps to the declaring class name
3. NSString* constants use `NSString` as `[Field]` property type (MAUI convention), not the mapped `string`
4. Read-write properties emit explicit getter/setter `[Export]` attributes to support custom ObjC setter selectors (e.g., `isHidden`/`setHidden:`)
5. Non-extern constants (header-only `static const`) are skipped entirely — no exported symbol for `[Field]` to resolve
6. `StructsAndEnums.cs` returns null (not emitted) when module has no enums, structs, extern constants, or functions

### Session O3: Mixed Frameworks + MSBuild SDK Integration ✅ COMPLETE

**Completed:** 2026-03-06

**What was built:**

Mixed framework detection, type-level dedup, ObjC metadata props emission, MSBuild SDK integration for ObjC/Mixed frameworks, dual-pipeline orchestration, and ObjC emitter hardening. 32 new tests, all passing. 52/53 validation targets pass (0 regressions).

**Source files (new):**
- `src/ObjC/Emitter/ObjCMetadataPropsEmitter.cs` — Emits `binding-metadata.props` for ObjC/mixed frameworks with real metadata extracted from xcframework plist
- `src/Configuration/XCFrameworkResolver.cs` — Added `DetectMixedFrameworkObjC()` method: checks for modulemap + non-Swift headers
- `src/Configuration/XCFrameworkMetadataExtractor.cs` — Added `ExtractFromFrameworkPath()` overload, extended `EmitMetadataProps()` with `frameworkType`/`objcProjectName` params, new XML properties `_SwiftBindingFrameworkType`/`_SwiftBindingObjCProjectName`

**Source files (modified):**
- `src/ObjC/Model/ObjCDeclarations.cs` — Added `IsFromCategory` flag to `ObjCMethodDecl` and `ObjCPropertyDecl` (O4 infrastructure)
- `src/ObjC/Parser/ClangAstParser.cs` — Category merge tags methods/properties with `IsFromCategory = true`
- `src/ObjC/Parser/ObjCTypeRefParser.cs` — Strip `enum `/`struct ` C type specifiers from clang qualType
- `src/ObjC/Pipeline/ObjCPipeline.cs` — Added `sdkMode`/`isMixed`/`excludeTypeNames` params, `FilterForMixedFramework()` type-level dedup, post-hoc validation (classes/protocols only)
- `src/ObjC/Emitter/ObjCTypeMapper.cs` — Added `unsigned int`→`uint`, `unsigned short`→`ushort`, `unsigned char`→`byte` mappings
- `src/ObjC/Emitter/StructsAndEnumsEmitter.cs` — Prefix digit-leading enum case names with `_` after prefix stripping
- `src/Program.cs` — Mixed detection after Swift resolution, dual-pipeline orchestration, `CollectSwiftEmittedTypeNames()` helper
- `src/Emitter/BindingProjectEmitter.cs` — `ObjCProjectFileName` option, conditional `<ProjectReference>` emission

**MSBuild SDK files:**
- `Sdk/Sdk.props` — ObjC-only PropertyGroup (`IsBindingProject=true`), `DisableRuntimeMarshallingAttribute` guard
- `Sdk/Sdk.targets` — `--objc` flag for ObjC-only, Target 4c `_InjectMixedObjCProjectReference`, Target 5 split with `Exclude="ApiDefinition.cs;StructsAndEnums.cs"` (prevents ObjC binding definitions from being compiled into Swift project), Target 6/7a ObjC guards

**Test files (32 new tests):**
- `ConfigurationTests/MixedFrameworkDetectionTests.cs` — 5 tests (Swift-only patterns, mixed detection, post-hoc filtering, modulemap module name parsing)
- `ObjCTests/Pipeline/MixedFrameworkDedupTests.cs` — 6 tests (type-level dedup: kept/dropped/never-filtered/empty/all-filtered)
- `ObjCTests/Pipeline/SwiftTypeNameCollectorTests.cs` — 6 tests (regex scan of generated C# for type names)
- `ObjCTests/Emitter/ObjCMetadataPropsEmitterTests.cs` — 4 tests (framework type, module name, wrapper props, mixed type)
- `ObjCTests/Parser/ClangAstParserTests.cs` — 3 new tests (IsFromCategory tracking for methods, properties, originals untagged)
- `ObjCTests/Parser/ObjCTypeRefParserTests.cs` — 2 new tests (`enum`/`struct` type specifier stripping)
- `ObjCTests/Emitter/ObjCTypeMapperTests.cs` — 3 new tests (`unsigned int`/`unsigned short`/`unsigned char` mapping)
- `ObjCTests/Emitter/StructsAndEnumsEmitterTests.cs` — 1 new test (digit-leading enum case `_` prefix)
- `EmitterTests/BindingProjectEmitterTests.cs` — 3 new tests (ObjC ProjectReference present/absent/mixed)

**Key design decisions:**
1. **Type-level dedup (O3), not member-level**: O3 removes entire ObjC types whose name matches a Swift-emitted type. ObjC-only members on shared types are lost until O4 adds selector-based member-level dedup. `IsFromCategory` tracking added as O4 infrastructure.
2. **Post-hoc mixed validation**: Run ObjC pipeline, then check if parsed module has classes or protocols. Constants alone (e.g., version numbers) don't make a framework mixed. This correctly handles Swift-only frameworks with non-Swift version headers (Starscream, KeychainAccess, etc.).
3. **SDK ObjC-only requires `<SwiftFrameworkType>ObjC</SwiftFrameworkType>`**: Auto-detection would need outer/inner-build bootstrap. Deferred.
4. **sdkMode suppression**: `sdkMode && !isMixed` → skip .csproj (SDK IS the project). `sdkMode && isMixed` → emit .csproj (SDK is Swift project, ObjC is separate).
5. **`CollectSwiftEmittedTypeNames`**: Regex scan of `*.cs` in output directory for `public [unsafe] [partial] class|struct|enum|interface NAME`. Used for dedup exclude set.

**Known O3 limitations (resolved in O4-O6):**
- ~~ObjC-only members on shared types (e.g., category additions to Swift-visible classes) are suppressed.~~ **Resolved in O6** — category members are now extracted as `[Category]` binding interfaces.
- Mixed SDK `ProjectReference` injection uses `BeforeTargets="ResolveProjectReferences"` timing — not yet behaviorally tested beyond static XML assertions.
- ~~**BRLMPrinterKit duplicate enum definitions**~~ **Resolved in O4** — parser-level declaration dedup (Pass 3).

### Session O4: Validation + Parser Dedup + Polish (complete)

**Goal:** Validate against real-world ObjC frameworks, fix parser/emitter edge cases, add to validation pipeline. Includes post-session Codex review fixes.

**Completed work:**

1. **Parser-level declaration dedup** (`ClangAstParser.cs`): Added Pass 3 after category merging to deduplicate all declaration types by name. Enums and structs use "keep richest" (most cases/fields). Classes and protocols use metadata-merging dedup (`MergeClasses`/`MergeProtocols`) — selects richest by member count, then merges `SuperclassName`, `ProtocolNames`, `GenericTypeParamNames`, and `Availability` from all duplicates. Functions, constants, and typedefs use "keep first". Fixed BRLMPrinterKit's 96 CS0101 duplicate-definition errors (reduced to 23 — remaining are typedef struct/block-typedef references).

2. **Category-aware dedup** (`ClangAstParser.cs`): Pass 2 category merge now applies to ALL matching duplicate classes, not just `FirstOrDefault`. Ensures category members survive dedup regardless of which duplicate is richest. Members/properties are intentionally NOT merged across duplicates (only metadata is) — duplicate declarations come from the same header re-included via umbrella headers, so they have identical members. Disjoint members only arise from categories (handled in Pass 2).

3. **Type mapping improvements** (`ObjCTypeMapper.cs`):
   - Added: `NSTimeInterval` → `double`, `UInt8` → `byte`, `va_list` → `IntPtr`
   - Added: CoreFoundation ref types — `CGImageRef` → `CGImage`, `CGColorRef` → `CGColor`, `CGPathRef` → `CGPath`, `CGContextRef` → `CGContext`, `dispatch_queue_t` → `DispatchQueue`, `dispatch_data_t` → `DispatchData`
   - Added: `using CoreFoundation;` to emitter (for `DispatchQueue`/`DispatchData`)
   - Filtered `NSObject` and `NSFastEnumeration` from protocol/class inheritance lists

4. **AST-driven generic type param detection** (`ClangAstParser.cs`, `ObjCTypeMapper.cs`, `ApiDefinitionEmitter.cs`):
   - Parser extracts `ObjCTypeParamDecl` nodes from class `inner` arrays (e.g., `@interface RLMResults<RLMObjectType>`)
   - `GenericTypeParamNames` field added to `ObjCClassDecl`
   - Generic param resolution is purely AST-driven — no hardcoded fallback set. Avoids cross-type collisions where a generic param name in one class matches a real type name used elsewhere.
   - Params scoped to the declaring class only: each class passes its own `GenericTypeParamNames` to the mapper. Protocols pass null (ObjC protocols don't declare lightweight generics).
   - Confirmed: Realm's `RLMObjectType`/`RLMKeyType` now correctly resolve to `NSObject` (0 leaks in output).

5. **Type ref parser hardening** (`ObjCTypeRefParser.cs`):
   - Strip `__attribute__((...))` decorations from qualType strings
   - Strip ObjC macros: `NS_REFINED_FOR_SWIFT`, `NS_SWIFT_NAME(...)`
   - Strip `_Null_unspecified` nullability annotation
   - Handle `NSError * *` double-pointer (space between stars after nullability stripping)

6. **C# keyword escaping** (`ApiDefinitionEmitter.cs`): Parameter names that are C# keywords (`object`, `event`, `class`, etc.) are prefixed with `@`.

7. **Validation pipeline integration**:
   - Realm and Stripe3DS2 added to `validation-libraries.json` (tier 1, manual mode)
   - Xcframeworks copied to `.libraries/Realm/` and `.libraries/Stripe3DS2/`
   - Updated all docs with new target counts (42 libraries, 55 targets)

8. **Documentation updates**: README ObjC section, CLAUDE.md ObjC CLI examples, count updates across all docs.

**ObjC edge cases fixed in O5:**
- Block typedefs referenced by name → resolved via `blockTypedefMap` to inline `Action`/`Func`
- Nested block types → `FindMatchingParen` depth-aware parsing
- Typedef alias resolution → pre-resolved chain maps with pointer preservation
- `BOOL` in pointer positions → added to `PointerTypeMappings`
- Duplicate constructors → disambiguated as named instance methods
- Protocol init methods → guard prevents invalid bgen constructor generation
- Module-local type accessibility → skip functions/delegates referencing ApiDefinition types

### Session O5: Zero Compile Errors ✅ COMPLETE

**Completed:** 2026-03-06

**Goal:** Get all 55 validation targets passing with 0 compile errors (BRLMPrinterKit, Realm, Stripe3DS2 had remaining errors after O4).

**What was built:**

All planned fixes are implemented and tested (29 new tests, 5892 total unit tests passing). All 55/55 validation targets pass. Post-session code review fixes: typedef resolution threaded through all emitters, fixed-size arrays modeled end-to-end (parser → mapper → emitter), method dedup handles second-order and full-name collisions.

**Fixes applied (all generic — no library-specific workarounds):**

1. **Nested block parsing** (`ObjCTypeRefParser.cs`): Replaced `LastIndexOf('(')` with `FindMatchingParen` helper for correct depth tracking. Fixed Stripe3DS2 `void (^)(UIViewController *, void(^)(void))` parse failures.

2. **Type mapper additions** (`ObjCTypeMapper.cs`):
   - `NSFastEnumeration` protocol-qualified id → `NSObject` (no .NET MAUI binding interface)
   - `NSURLSession` pointer mapping → `NSUrlSession`
   - `BOOL` added to `PointerTypeMappings` (BOOL* in block params)
   - Removed early return for unknown pointer types (step 4) — allows typedef resolution to proceed at step 9

3. **Typedef alias resolution** (`ObjCTypeMapper.cs`): Added `typedefMap` parameter to `MapType()`. Pre-resolved typedef chains (A → B → NSString* becomes A → NSString*). Pointer preservation: when typedef is non-pointer but usage is pointer (e.g., `typedef NSString Alias; Alias *`), creates new TypeRef with pointer flag before recursive resolution.

4. **Block typedef → delegate emission** (`StructsAndEnumsEmitter.cs`): Block typedefs emitted as `public delegate` declarations. Module-local type detection skips delegates referencing types only defined in ApiDefinition.cs (avoids CS0059 accessibility errors).

5. **Block typedef name resolution** (`ObjCTypeMapper.cs`): Added `blockTypedefMap` parameter. Named block typedef references (e.g., `RLMNotificationBlock`) resolved to inline `Action<>`/`Func<>` in ApiDefinition.cs.

6. **Duplicate constructor disambiguation** (`ApiDefinitionEmitter.cs`): Track emitted constructor parameter signatures per class. Duplicates emitted as named instance methods (e.g., `InitWithBLELocalName(string)`) instead of colliding `Constructor(string)` overloads.

7. **Method signature dedup** (`ApiDefinitionEmitter.cs`): Track emitted method signatures per class. Duplicates renamed using `SelectorToFullMethodName` (all selector parts PascalCased). Renamed signatures re-registered to prevent second-order collisions. If the full-selector name also collides with an existing method, a numeric suffix is appended.

8. **Protocol init guard** (`ApiDefinitionEmitter.cs`): Protocol `init*` methods NOT emitted as constructors (bgen generates `public virtual` constructors → invalid C#). Added `!isProtocol` guard.

9. **`[DisableDefaultCtor]`** (`ApiDefinitionEmitter.cs`): Emitted when class has any parameterless init method (including `initWith*` with 0 params) to prevent bgen duplicate constructor conflict.

10. **Constants class fix** (`StructsAndEnumsEmitter.cs`): Removed `[Static]` attribute from constants class (confused bgen into processing it as a binding interface). Changed to plain `public static class` (not `partial`).

11. **Fixed-size array struct fields** — End-to-end: `ObjCTypeRefParser` detects `uint8_t [4]` qualType and sets `FixedArraySize` on `ObjCTypeRef` model, `ObjCTypeMapper` maps to `byte[4]` (handles pointer elements like `NSString *[4]` → `string[4]`), `StructsAndEnumsEmitter` emits `[MarshalAs(UnmanagedType.ByValArray, SizeConst=N)]`.

12. **Module-local type skipping** (`StructsAndEnumsEmitter.cs`): Functions and block delegates referencing types defined in ApiDefinition.cs are skipped to avoid CS0050/CS0059 accessibility errors.

13. **Binding project fixes** (`ObjCBindingProjectEmitter.cs`): Added `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` (prevents auto-including unrelated .cs files), absolute NativeReference path (avoids macOS `/tmp` → `/private/tmp` symlink issues).

14. **Emitter using directives**: Added `using CoreGraphics;` to StructsAndEnums, `using AuthenticationServices;` and `using UIKit;` to ApiDefinition.

**Test files (29 new tests across 6 files):**
- `ObjCTypeMapperTests.cs` — 10 new tests (typedef pointer preservation, BOOL pointer, unknown pointer fallthrough, block typedef map resolution, fixed-size array mapping incl. pointer elements)
- `ApiDefinitionEmitterTests.cs` — 11 new tests (DisableDefaultCtor, protocol init guard, method signature dedup, triple dedup collision, full-name collision with suffix, SelectorToFullMethodName, NSFastEnumeration filtering)
- `StructsAndEnumsEmitterTests.cs` — 6 new tests (fixed-size array, module-local function/delegate skipping, constants class format, typedef alias in struct fields, typedef alias in constants)
- `ObjCBindingProjectEmitterTests.cs` — 2 new tests (EnableDefaultCompileItems, absolute NativeReference path)
- `ObjCTypeRefParserTests.cs` — 3 new tests (constant array types: scalar, unsigned char, pointer element)

### Session O6: Mixed-Framework `[Category]` Emission ✅ COMPLETE

**Completed:** 2026-03-06

**Goal:** Upgrade mixed-framework dedup from type-level (drop entire shared types) to member-level using MAUI's `[Category]` binding pattern.

**What was built:**

All planned work is implemented and tested (24 new tests, 5918 total unit tests passing). All 55/55 validation targets pass with zero regressions.

**Changes:**

1. **`ObjCCategoryDecl` model** (`ObjCDeclarations.cs`): New record with `CategoryName`, `ClassName`, `ProtocolNames`, `GenericTypeParamNames`, `Methods`, `Properties`, `Availability`. Added `CategoryName` field to `ObjCMethodDecl` and `ObjCPropertyDecl` (defaults to `""`). Added `Categories` list to `ObjCModule` (not counted in `TotalDeclarations`).

2. **Parser category preservation** (`ClangAstParser.cs`): `ParseCategoryDecl` returns `ObjCCategoryDecl` with category name (empty string for unnamed categories), protocols adopted by the category, and category-level availability. Pass 2 merge tags members with `CategoryName` and merges category-adopted protocols onto the owning class. Pass 4 deduplicates categories by `(ClassName, CategoryName)` key via `MergeCategories` — unions methods, properties, protocols, and availability from all duplicates.

3. **`FilterForMixedFramework` rewrite** (`ObjCPipeline.cs`): Member-level dedup. Shared classes are dropped from ObjC output, but their `ObjCCategoryDecl` records (populated at parse time) are extracted as separate category interfaces. `GenericTypeParamNames` are copied from the owning class onto each extracted category. ObjC-only class categories are discarded (they stay merged inline). Pure ObjC frameworks clear `Categories` before emission to prevent double-emitting. Post-hoc validation gate updated to check `Categories.Count`.

4. **`EmitCategory`** (`ApiDefinitionEmitter.cs`): Emits `[Category]` + `[BaseType(typeof(ClassName))]` + `partial interface ClassName_CategoryName`. Category-level availability attributes. Protocol conformances as interface inheritance. Init methods filtered out (MAUI limitation). Generic type params from owning class passed to `ObjCTypeMapper`. Duplicate method signature collision handling (same as class emission).

5. **`Program.cs` mixed classification fix**: `isMixed` check now includes `Categories.Count > 0`, so categories-only mixed frameworks get correct `frameworkType: "Mixed"` metadata and ObjC project references.

**Test files (24 new tests across 3 files):**
- `ClangAstParserTests.cs` — 7 new tests (named category, unnamed category, multiple categories, category protocols, category availability, duplicate category dedup, disjoint member merge)
- `ApiDefinitionEmitterTests.cs` — 10 new tests (`[Category]`/`[BaseType]` attributes, method export, properties, init skipping, unnamed suffix, generic param resolution, protocol conformance inheritance, pure ObjC no double-emit, duplicate method signature collision, `GenerateCategoryInterfaceName` theory)
- `MixedFrameworkDedupTests.cs` — 7 new tests (shared class with categories extracted, shared class without categories dropped, ObjC-only class keeps merged, multiple categories grouped, mixed members only category extracted, shared protocol still dropped, categories-only not skipped by post-hoc gate)

**Edge cases handled:**
- Init methods in categories filtered at emission (MAUI can't handle constructors in `[Category]`)
- Unnamed categories emitted as `ClassName_Extensions`
- Multiple categories per class each get their own interface
- Pure ObjC frameworks: categories cleared before emission (no double-emit)
- Category lightweight generic params resolved via owning class's `GenericTypeParamNames`
- Category protocol conformances preserved and emitted as interface inheritance
- Duplicate method signatures in categories renamed (same collision handling as classes)

### Estimated Total: 3-6 sessions (6 complete)

Sessions O1-O3 built the ObjC pipeline (parser, emitter, binding project, mixed-framework support). O4 added parser dedup, type mapping improvements, and real-world validation. O5 achieved 55/55 validation targets with zero compile errors. O6 upgraded mixed-framework dedup from type-level to member-level with `[Category]` emission.

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
2. **Lightweight generics**: ~~ObjC has `NSArray<NSString *>` -- should we preserve generic type info in the binding?~~ **Resolved (O4):** Generic type parameters are detected from `ObjCTypeParamDecl` AST nodes and mapped to `NSObject`. Scoped per-class to avoid cross-type collisions. Generic *arguments* on container types (e.g., `NSArray<NSString *>`) are stripped — the binding uses unparameterized `NSArray`.
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
