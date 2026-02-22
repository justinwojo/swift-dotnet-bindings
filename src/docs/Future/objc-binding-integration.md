# Objective-C Binding Integration

## Status: Considering (not planned)

**Date:** 2026-02-14

## Problem Statement

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
Parse:   ABI JSON → TypeDecl / MethodDecl model
Marshal: TypeDatabase → type mapping → marshaling decisions
Emit:    Swift.Module.cs (direct P/Invoke with LibraryImport)
         + wrapper.swift (Cdecl wrappers for Mono JIT)
         + regular .csproj
Runtime: CallConvSwift calling convention, Swift ARC, Value Witness Tables
```

### ObjC Pipeline (proposed)

```
Input:   Headers (.h) + modulemap (from xcframework)
Parse:   clang -ast-dump=json → ObjCInterfaceDecl / ObjCMethodDecl model
Map:     ObjC types → .NET types (NSString→string, NSArray→NSArray, etc.)
Emit:    ApiDefinition.cs + StructsAndEnums.cs
         + binding .csproj (<IsBindingProject>true</IsBindingProject>)
Runtime: objc_msgSend (existing .NET MAUI ObjC registrar — no new runtime needed)
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

These components already exist and would be reused directly:

| Component | Current Location | Sharing Effort |
|-----------|-----------------|----------------|
| XCFramework resolution (slicing, plist, arch) | `XCFrameworkResolver.cs`, `PlistReader.cs` | Zero — already generic |
| Framework dependency resolution (incl. ObjC) | `XCFrameworkResolver.cs`, `ResolveObjCFramework()` | Zero — already handles ObjC |
| ObjC-only dependency detection | `FrameworkDependencyInfo.IsObjCOnly`, `BinaryDependencyAnalyzer.cs` | Zero — already distinguishes ObjC-only deps in the dependency graph |
| CLI + System.CommandLine | `Program.cs` | Trivial — add detection branch |
| Type database (ObjC bridged types) | `TypeDatabase`, `TypeDatabaseExtensions.cs` | Small extension |
| MSBuild SDK (discover → generate → package) | `Swift.Bindings.Sdk/` | Moderate — route by framework type |
| `.csproj` emission | `BindingProjectEmitter.cs` | Fork for `<IsBindingProject>` variant |
| NuGet pack layout | `ConsumerTargetsEmitter.cs`, SDK targets | Small adaptation |
| Modulemap parsing | `ParseModuleNameFromModulemap()` | Already exists for ObjC deps |

Estimated shared code: ~15-20% of total generator codebase.

## Proposed Integrated Architecture

```
CLI Entry Point (Program.cs)
  │
  ├── XCFramework Resolution (shared)
  │
  ├── Framework Detection:
  │   ├── Has abi.json?       → Swift pipeline (existing)
  │   ├── Has modulemap only? → ObjC pipeline (new)
  │   └── Has both?           → Both pipelines, merged output
  │
  ├── Swift Pipeline (existing, unchanged):
  │   ├── ABI JSON Parser → TypeDatabase → Marshaler → Emitter
  │   ├── Swift wrapper compiler
  │   └── Output: Swift.Module.cs + wrapper.swift + regular .csproj
  │
  ├── ObjC Pipeline (new):
  │   ├── Clang AST Parser (clang -ast-dump=json)
  │   ├── ObjC Type Mapper
  │   ├── ApiDefinition Emitter
  │   └── Output: ApiDefinition.cs + StructsAndEnums.cs + binding .csproj
  │
  ├── Type Database (shared — cross-references between pipelines)
  ├── Dependency Resolution (shared)
  ├── Project Emitter (shared, different .csproj shapes per pipeline)
  └── NuGet / MSBuild SDK (shared)
```

### User Experience

```bash
# Swift library — works exactly as today
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework Nuke.xcframework -o output/

# ObjC library — same command, auto-detected
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework SomeObjCLib.xcframework -o output/

# Mixed library — both pipelines run, unified output
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework MixedLib.xcframework -o output/
```

### MSBuild SDK Experience

```xml
<!-- Same SDK for both Swift and ObjC — auto-detected -->
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

Sharpie used **libclang C bindings** — a compiled native dependency linked against a specific Clang version. Apple updates Clang with each Xcode release. The API isn't guaranteed stable. Result: Sharpie needs rebuilding for each Xcode version, and Microsoft stopped doing that.

### Proposed: clang -ast-dump=json

```bash
xcrun clang -x objective-c -ast-dump=json \
  -isysroot $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F /path/to/framework \
  /path/to/Headers/Module.h
```

Advantages:
- **No native dependencies** — invokes the Clang binary that ships with Xcode
- **Always version-matched** — uses whatever Clang the developer has installed
- **Stable JSON schema** — the AST dump format rarely changes between versions
- **Already available** — every developer with Xcode has this
- **Parseable with System.Text.Json** — no new dependencies in the generator

The JSON output contains all the declarations we need:
- `ObjCInterfaceDecl` → `[BaseType(typeof(NSObject))]` interface
- `ObjCProtocolDecl` → `[Protocol]` interface
- `ObjCMethodDecl` → `[Export("selector:")]` method
- `ObjCPropertyDecl` → `[Export("propertyName")]` property
- `EnumDecl` (NS_ENUM/NS_OPTIONS) → C# enum with `[Native]`
- `TypedefDecl` → type aliases
- `RecordDecl` → C# structs
- `FunctionDecl` → `[DllImport]` / `[LibraryImport]` declarations

### Risk: AST JSON Schema Changes

The `clang -ast-dump=json` format is not formally versioned. However:
- It has been stable across Xcode 14-16 with only additive changes
- We'd parse it defensively (ignore unknown fields, tolerate missing optional fields)
- Breaking changes would affect ALL Clang JSON consumers, creating pressure on Apple to maintain compatibility
- Worst case: a new Xcode needs parser updates, but it's JSON parsing in C# — no native recompilation

## New Code Estimate

| Component | Estimated Lines | Description |
|-----------|----------------|-------------|
| ObjC AST Parser | 500-800 | Parse `clang -ast-dump=json` into ObjC declaration model |
| ObjC Declaration Model | 200-300 | `ObjCInterfaceDecl`, `ObjCProtocolDecl`, `ObjCMethodDecl`, etc. |
| ObjC Type Mapper | 200-400 | Map ObjC types to .NET types for binding definitions |
| ApiDefinition Emitter | 400-600 | Emit `[BaseType]`/`[Export]`/`[Protocol]` C# binding definitions |
| StructsAndEnums Emitter | 200-300 | Emit `NS_ENUM` → C# enum, struct → C# struct |
| Binding Project Emitter | 100-150 | `.csproj` with `<IsBindingProject>true</IsBindingProject>` |
| Detection/Routing | 50-100 | Framework type detection in Program.cs |
| **Total** | **~1,500-2,000** | Compare: Swift pipeline is ~30,000+ lines |

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

## Mixed Library Handling

A framework with both Swift and ObjC public API would run both pipelines:

1. **Detection**: XCFramework has both ABI JSON (Swift module) and Headers directory (ObjC)
2. **Swift pipeline**: Generates `Swift.Module.cs` with P/Invoke bindings
3. **ObjC pipeline**: Generates `ApiDefinition.cs` + `StructsAndEnums.cs` with binding definitions
4. **Type database merge**: Swift types referencing ObjC types get correct cross-references
5. **Project emission**: Single `.csproj` that includes both direct P/Invoke code and binding definitions

The type database already handles ObjC-bridged types (`IsObjCModuleType`, `AppleObjCFrameworkModules`). Extending it to cross-reference between the two pipelines is natural. The cross-module type resolution infrastructure (`ModuleDatabaseEmitter`, `--module-database` CLI option, SDK `_CollectSwiftModuleDatabases` target) could also serve for ObjC↔Swift type resolution in mixed frameworks — dependency module databases already serialize and reload type records across pipeline boundaries.

## Pros and Cons

### Pros

- **Single tool** — one CLI, one SDK, one `dotnet new` template for any Apple framework
- **No Xcode version coupling** — `clang -ast-dump=json` is process-invoked, not linked
- **Mixed library support** — one invocation handles both Swift and ObjC
- **Shared infrastructure** — XCFramework resolution, dependency handling, NuGet packaging already exist
- **Small incremental effort** — ~1,500-2,000 lines of new code on a 30,000+ line codebase
- **ObjC runtime already exists** — .NET MAUI's ObjC registrar handles all the hard runtime work
- **Natural routing** — `SwiftModuleNotFoundException` (already thrown) becomes a routing decision
- **Replaces broken tooling** — Objective Sharpie is effectively abandoned

### Cons

- **Scope creep risk** — ObjC edge cases could distract from Swift pipeline improvements
- **Different output models** — Swift emits direct C# code; ObjC emits binding definitions for the registrar. Two fundamentally different compilation models in one tool.
- **Testing surface increase** — need ObjC-specific test frameworks and validation libraries
- **Binding project complexity** — `<IsBindingProject>` has its own MSBuild infrastructure and quirks
- **Diminishing returns** — ObjC is declining; most new libraries are Swift-only
- **AST schema risk** — `clang -ast-dump=json` format could change (low probability, moderate impact)
- **ObjC has more edge cases than expected** — categories, class extensions, `__attribute__` annotations, nullability annotations, lightweight generics, availability macros all need handling

## Alternative: Separate Standalone Tool

Instead of integrating, build a standalone `dotnet-objc-sharpie` tool that shares extracted libraries:

```
Swift.Bindings (this repo)
  └── shared: XCFramework.Resolution NuGet package

dotnet-objc-sharpie (separate repo)
  └── references: XCFramework.Resolution NuGet package
  └── own: Clang parser, ApiDefinition emitter, own SDK
```

### Why Integration Is Preferred

- Duplicates CLI, SDK, NuGet packaging, dependency resolution, template infrastructure
- Two tools for the user to learn, install, and maintain
- Mixed libraries require manual coordination between two separate outputs
- The ObjC-specific code (~1,500-2,000 lines) doesn't justify a separate repository/tool/SDK
- Shared library extraction adds maintenance overhead for a single consumer

### When Separate Makes More Sense

- If ObjC edge cases prove far more complex than estimated (categories, class extensions, etc.)
- If the binding project model (`<IsBindingProject>`) conflicts with the Swift SDK's build targets
- If the ObjC community wants to iterate faster than the Swift pipeline allows

## Implementation Phases (if pursued)

### Phase 1: Detection + Routing (~1 day)
- Detect ObjC-only frameworks (modulemap present, no ABI JSON)
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
- Type mapping: ObjC types → .NET types (blocks → delegates, etc.)
- Handle: inheritance, protocol conformance, optional protocol methods
- Handle: class methods vs instance methods, constructors, factory methods

### Phase 4: Binding Project Emission (~1-2 days)
- Emit `.csproj` with `<IsBindingProject>true</IsBindingProject>`
- Include native framework reference
- Wire into existing NuGet packaging flow

### Phase 5: SDK Integration (~2-3 days)
- MSBuild SDK detects framework type and routes to correct generator mode
- Single `dotnet build` for either Swift or ObjC frameworks
- Mixed framework support (both pipelines)

### Phase 6: Validation (~3-5 days)
- Test against known ObjC-only frameworks (e.g., some Stripe modules, older SDKs)
- Compare output against hand-written bindings or Sharpie output
- Verify binding project compiles and runs on iOS Simulator

**Total estimate: ~2-3 weeks of focused effort.**

## Open Questions

1. **Categories**: ObjC categories add methods to existing classes. Should these become C# extension methods, or methods on the main binding class?
2. **Lightweight generics**: ObjC has `NSArray<NSString *>` — should we preserve generic type info in the binding?
3. **Swift-imported ObjC**: When Swift re-exports an ObjC type (common in mixed frameworks), which pipeline owns it?
4. **Binding project compatibility**: Does `<IsBindingProject>` work correctly with .NET 10 and the latest MAUI? It's had issues historically.
5. **Block ABI**: ObjC blocks have a specific ABI layout. The registrar handles this, but do we need to annotate parameters correctly for complex block signatures?
6. **SDK naming**: If supporting both, should `Swift.Bindings.Sdk` become `Apple.Bindings.Sdk`?

## References

- Xamarin ObjC binding docs: https://learn.microsoft.com/en-us/previous-versions/xamarin/cross-platform/macios/binding/
- Clang AST dump format: output of `clang -ast-dump=json`
- .NET MAUI binding project: `<IsBindingProject>true</IsBindingProject>` in `.csproj`
- Objective Sharpie (archived): https://learn.microsoft.com/en-us/xamarin/cross-platform/macios/binding/objective-sharpie/
- Current ObjC handling in this repo: `TypeDatabaseExtensions.cs` (`IsObjCModuleType`, `AppleObjCFrameworkModules`), `XCFrameworkResolver.cs` (`ResolveObjCFramework()`)
