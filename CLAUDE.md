# Claude Code Guide for Swift Bindings

## Project Overview

This is Microsoft's **experimental** Swift/.NET interoperability project. It generates C# bindings from compiled Apple Swift libraries, allowing Swift frameworks to be consumed in .NET/C# applications on Apple platforms (iOS, macOS, tvOS, Catalyst).

**Branch**: `feature/swift-bindings`
**Status**: Experimental (last active ~9 months ago by Microsoft, recently picked up by Justin Wojciechowski)
**Target**: .NET 9.0+ on Apple platforms

## Copyright and Licensing

This project is licensed under the **MIT License**. The original codebase is copyrighted by Microsoft Corporation. New contributions and modifications are additionally copyrighted by Justin Wojciechowski.

### Copyright Header Requirements

**When creating new C# files** (original work, not derived from Microsoft code):
```csharp
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

**When modifying existing Microsoft files** that don't already have Justin's copyright, add his copyright line:
```csharp
// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

**When creating files derived from Microsoft code** (based on their patterns/templates):
```csharp
// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

**Files already containing Justin's copyright** should not be modified (the header is already correct).

This approach follows MIT license best practices: original work gets the author's copyright, while derived/modified work preserves the original copyright alongside the contributor's.

## Why This Project Matters

Apple is increasingly shipping Swift-only APIs (StoreKit 2, SwiftUI, etc.). Without Swift interop, .NET on iOS becomes progressively less capable. Traditional Objective-C bindings don't work for Swift-only libraries. This project attempts to solve that fundamental problem.

## North Star Vision

**Read `/north-star.md` for the project's long-term vision and roadmap.**

The end goal: Any .NET developer can bind any Swift library and distribute it via NuGet with a simple workflow:
1. Create binding project → 2. Add xcframework → 3. Build → 4. Distribute NuGet package

When making architectural decisions or prioritizing work, refer to the north star document to ensure alignment with the project's direction. Key priorities:
- **Phase 1** (current): Foundation completion - async SafeHandle fix, property setters, enum cases
- **Phase 2**: Type system completeness - existentials, generics, protocols
- **Phase 3**: Developer experience - MSBuild SDK, project templates, NuGet automation

See also: `/src/docs/nuke-binding-roadmap.md` for real-world gap tracking from Nuke library testing.

## Repository Structure

```
runtimelab/
├── src/
│   ├── Swift.Bindings/          # Core binding generator tool
│   │   ├── src/                 # Generator source (73 C# files)
│   │   │   ├── Demangler/       # Swift symbol demangling
│   │   │   ├── Parser/          # ABI JSON parsing
│   │   │   ├── Model/           # Type declarations (TypeDecl, MethodDecl, etc.)
│   │   │   ├── TypeDatabase/    # Type caching and lookup
│   │   │   ├── Marshaler/       # Marshalling strategy decisions
│   │   │   └── Emitter/         # C# code generation
│   │   └── tests/
│   │       ├── UnitTests/       # Component tests
│   │       ├── IntegrationTests/ # Swift library tests
│   │       └── FrameworkTests/  # Apple framework tests (StoreKit)
│   │
│   ├── Swift.Runtime/           # Runtime support library
│   │   └── src/Swift/
│   │       ├── SwiftArray<T>, SwiftString, SwiftSet<T>, etc.
│   │       └── Runtime/         # Handles, metadata, ARC, marshalling
│   │
│   ├── Dynamo/                  # Generic code generation utilities
│   ├── samples/                 # HelloWorld, HikingApp examples
│   └── docs/                    # Emitter redesign proposal
│
├── docs/                        # Technical documentation (27 files)
├── eng/                         # Build infrastructure, Azure Pipelines
├── build.sh / build.cmd         # Build scripts
└── generate.sh                  # Framework binding generation
```

## How It Works

### Data Flow Pipeline

```
Swift Framework (.dylib + .swiftinterface)
    ↓
generate.sh runs swift-frontend to extract ABI.json
    ↓
SwiftBindings tool consumes: ABI.json + dylib + TBD file
    ↓
Parser: SwiftABIParser parses JSON, Swift5Demangler decodes symbols
    ↓
TypeDatabase: Stores type metadata, marshalling decisions
    ↓
Emitter: Generates C# source files (+ Swift wrappers for async)
    ↓
Output: NuGet package with C# bindings
```

### Key Concepts

**Type Marshalling Labels** (from emitter-redesign-proposal.md):
- `Struct` - Frozen structs with only frozen fields → C# struct with matching layout
- `ClassWithOpaquePayload` - Non-frozen structs → C# class with SafeHandle
- `ClassWithBufferStruct` - Frozen structs with ref type fields → C# class with Buffer inner struct
- `Class` - Swift classes → C# class with ARC
- `Unknown` - Unsupported types → Pruned from output

**Memory Management**:
- Swift uses ARC (Automatic Reference Counting)
- Runtime calls `swift_retain()` / `swift_release()` via P/Invoke
- `SwiftSafeHandle<T>` wraps native pointers safely

**Calling Convention**:
- Uses `[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]`
- Swift has custom register usage for `self`, errors, async context
- P/Invoke targets mangled function names in dylib

## Key Files to Understand

| File | Purpose |
|------|---------|
| `src/Swift.Bindings/src/Program.cs` | Entry point, CLI argument handling |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | Parses Swift ABI JSON into type declarations |
| `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` | Processes and validates types |
| `src/Swift.Bindings/src/Marshaler/Conductor.cs` | Orchestrates marshalling decisions |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` | Main code emission |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Method generation (PInvoke, wrappers) |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` | Central type repository |
| `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs` | SafeHandle implementation |
| `src/Swift.Runtime/src/Swift/Runtime/Arc.cs` | Reference counting |
| `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` | Type marshalling |
| `src/Swift.Bindings/src/Marshaler/TupleHandler.cs` | Tuple type handling |
| `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` | Closure type handling |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` | Operator emission |
| `src/docs/emitter-redesign-proposal.md` | Architecture improvement proposal |
| `docs/binding-overview.md` | High-level binding philosophy |

## Current Capabilities

**Working**:
- Classes, structs (frozen and non-frozen), basic enums
- Instance and static methods, properties
- Async methods (via Swift wrapper generation)
- Protocol conformance (basic)
- Generics (bound generics, limited)
- SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>
- Closures (`@convention(c)` and `@escaping` with frozen types)
- Tuples (1-7 elements with frozen types)
- Operators (arithmetic, comparison, bitwise, unary; automatic pair synthesis)
- StoreKit 2 bindings (published as experimental NuGet)

**Example Usage** (from README):
```csharp
SwiftArray<SwiftString> productIdentifiers = new SwiftArray<SwiftString>();
productIdentifiers.Append(new SwiftString("id1"));
Task<SwiftArray<SwiftString>> productsTask = Product.products<SwiftArray<SwiftString>>(productIdentifiers);
SwiftArray<Product> products = await productsTask;
Product product = products[0];
await product.purchase(new SwiftSet<Product.PurchaseOption>());
```

## Recent Implementations

### Closure Support (Issue #2874)

**Files:**
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Closure detection and translation
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Closure emission (callback + return marshalling)
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` - 38 tests

**What's supported:**
- `@convention(c)` closures → C# delegates passed as function pointers
- `@escaping` closures → C# delegates with thunk functions
- Closures with primitive, frozen struct, and tuple parameters/returns
- Pointer types (`UnsafePointer`, `UnsafeMutablePointer`, etc.) → `IntPtr`
- **Closure return types** → Methods returning closures marshal to C# delegates with ARC management

**Not yet supported:**
- Async closures
- Throwing closures
- Non-escaping closures (except `@convention(c)`)
- Closures within closures
- Generic parameters in closures

**C# mapping:**
```
Swift: (Int, Bool) -> Void        →  Action<long, bool>
Swift: (Int) -> String            →  Func<long, string>
Swift: @convention(c) () -> Void  →  delegate* unmanaged[Cdecl]<void>
Swift: func getCallback() -> (Int) -> Bool  →  Func<long, bool> (method return)
```

### Tuple Support (Issue #2873)

**Files:**
- `src/Swift.Bindings/src/Marshaler/TupleHandler.cs` - Tuple detection and translation
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TupleHandlerTests.cs` - 24 tests

**What's supported:**
- Tuples of 1-7 elements (C# ValueTuple limit without nesting)
- Frozen/primitive element types
- Named tuple elements preserved: `(x: Int, y: Int)` → `(long x, long y)`
- Tuples as closure parameters and return types

**Not yet supported:**
- 8+ element tuples (requires ValueTuple nesting)
- Nested tuples
- Non-frozen types as tuple elements
- Closures as tuple elements

**C# mapping:**
```
Swift: (Int, String)       →  (long, string) / ValueTuple<long, string>
Swift: (x: Int, y: Bool)   →  (long x, bool y)
```

### Operator Support

**Files:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` - Operator emission (411 lines)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs` - 68 tests

**What's supported:**
- Arithmetic: `+`, `-`, `*`, `/`, `%`
- Comparison: `==`, `!=`, `<`, `>`, `<=`, `>=`
- Bitwise: `&`, `|`, `^`, `<<`, `>>`
- Unary: `!`, `~`
- Automatic paired operator synthesis (e.g., `!=` from `==`, `>` from `<`)

**Not supported (no C# equivalent):**
- Logical operators: `&&`, `||` (C# doesn't allow overloading)
- Optional operators: `??`, `?.`
- Assignment operators: `=`, `+=`, `-=`, etc.
- Lambda operator: `=>`

**C# mapping:**
```
Swift: static func ==(lhs: T, rhs: T) -> Bool  →  public static bool operator ==(T left, T right)
Swift: prefix static func !(v: T) -> Bool      →  public static bool operator !(T operand)
```

## Critical Gaps & Limitations

### NOT IMPLEMENTED (Blocking for "any Swift library" goal)

| Feature | Issue | Impact |
|---------|-------|--------|
| **Existential Containers** | #2875 | Protocol composition types fail |
| **Generic Types (unbound)** | Marked Unknown | Generic type definitions skipped |
| **Async Properties** | #2996 | Properties with async getters unsupported |
| **Actors** | No design | Swift actors unsupported |
| **PATs (full)** | Partial | Protocols with associated types limited |

### Partially Implemented

- **Generics**: Bound generics work; generic type definitions marked Unknown
- **Protocols**: Basic conformance; methods/properties incomplete
- **Enums with payloads**: Discriminated unions complex
- **JSON TBD format**: Only YAML-like format works

### Known Bugs

- Type metadata cache can return wrong values (#2966)
- Cross-module type references have workarounds
- Namespace mapping uses temporary `Swift.{Module}` pattern

## Building & Testing

**Prerequisites**: macOS with Xcode, .NET 10.0 SDK

```bash
# Build (macOS only for full build)
./build.sh

# Generate framework bindings
./generate.sh

# Run tests
dotnet test src/Swift.Bindings/tests/UnitTests
dotnet test src/Swift.Runtime/tests

# Integration tests require Swift toolchain
dotnet test src/Swift.Bindings/tests/IntegrationTests
```

**Windows**: Only builds Swift.Bindings tool (no runtime/tests)

**Windows Test Workaround**: The Arcade SDK generates a `.runsettings` file with a relative `DotNetHostPath`, causing test discovery to fail. Use the explicit path override:
```bash
dotnet test src/Swift.Bindings/tests/UnitTests -- RunConfiguration.DotNetHostPath="C:\Program Files\dotnet\dotnet.exe"
```

**iOS Simulator Testing**: The `dotnet build -t:Run` command times out waiting for the app to exit. For faster iteration when testing on iOS simulator:

```bash
# Build the app
dotnet build BindingTesting/Nuke/NukeTestApp -c Debug

# Install and launch with 5-second output capture
xcrun simctl install booted BindingTesting/Nuke/NukeTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/NukeTestApp.app && \
(xcrun simctl launch --console --terminate-running-process booted com.swiftbindings.nuketestapp 2>&1 &); \
sleep 5; echo "---DONE---"
```

This captures console output (including crash logs) without waiting for app termination. Adjust `sleep` duration as needed. See `src/docs/nuke-binding-roadmap.md` for more details on framework resolution and known issues.

## Architecture Notes

### Emitter Redesign (In Progress)

The `src/docs/emitter-redesign-proposal.md` outlines a cleaner architecture:

1. **Type Pre-processing**: Traverse type graph, assign marshalling labels, prune Unknown
2. **Type Processing**: Build representations with handlers for each member type
3. **Emission**: Convert representations to C# code

Key handler groups:
- **Method Handlers**: Constructor, Static/Instance, SwiftError, Generic, Async
- **Return Handlers**: IndirectResult, BoundGeneric, Direct, Void
- **Argument Handlers**: NonFrozen, Generic, BoundGeneric

### Why SafeHandle Matters

Swift objects need deterministic cleanup (ARC), but .NET uses GC. `SwiftSafeHandle<T>` bridges this:
- Calls `swift_release()` on disposal
- Prevents use-after-free
- Enables `using` patterns in C#

## Assessment: Is This Worth Continuing?

### Strengths

1. **Solid Foundation**: ABI parsing, demangling, type database, marshalling architecture all work
2. **Real Results**: StoreKit 2 bindings actually function
3. **Correct Approach**: Uses ABI JSON + TBD files (official Swift metadata sources)
4. **Good Documentation**: 27 design docs explain rationale
5. **Test Coverage**: Unit, integration, and framework tests exist
6. **Recent Improvements**: Emitter redesign proposal shows clear path forward

### Weaknesses

1. **Generics Limited**: Complex generic types not handled
2. **No Actors**: Modern Swift uses actors for concurrency
3. **No Operators**: Swift operator overloads not emitted
4. **Windows Dev Story**: Can't test runtime on Windows
5. **Maintenance Gap**: 9 months without Microsoft activity

### Verdict

**This is the most advanced open-source attempt at Swift/.NET interop.** The architecture is sound, and the gaps are well-understood. However, reaching "any Swift library" support requires:

1. Full generic type support
2. Existential containers
3. Operator support
4. Potentially actors

The codebase is ~5,000+ lines of well-structured C# with clear separation of concerns. Extending it is feasible but requires deep understanding of both Swift ABI and .NET interop.

## Quick Reference

**CLI Usage**:
```bash
SwiftBindings -a path/to/abi.json -d path/to/lib.dylib -t path/to/lib.tbd -o output/
```

**Key Runtime Types**:
- `ISwiftObject` - Base interface for all projected types
- `SwiftSafeHandle<T>` - Safe native pointer wrapper
- `TypeMetadata` - Runtime type information
- `ValueWitnessTable` - Memory layout operations
- `SwiftMarshal` - Marshalling utilities

**Common Patterns**:
```csharp
// Creating Swift types
using var str = new SwiftString("hello");
var array = new SwiftArray<SwiftString>();

// Async calls
var result = await SomeSwiftType.asyncMethod();

// Protocol conformance checked at runtime
var conforms = obj.GetProtocolConformanceDescriptor(typeof(ISomeProtocol));
```

## Contributing Guidelines

1. Read `docs/binding-overview.md` first for philosophy
2. Check `src/docs/emitter-redesign-proposal.md` for architecture direction
3. Add tests for new features in appropriate test project
4. Run `./build.sh` on macOS to validate changes
5. Follow existing code style (no emojis, clear naming)
6. **Include proper copyright headers** (see "Copyright and Licensing" section above)
