# Claude Code Guide for Swift Bindings

## Project Overview

This is Microsoft's **experimental** Swift/.NET interoperability project. It generates C# bindings from compiled Apple Swift libraries, allowing Swift frameworks to be consumed in .NET/C# applications on Apple platforms (iOS, macOS, tvOS, Catalyst).

**Branch**: `feature/swift-bindings`
**Status**: Experimental (last active ~9 months ago by Microsoft, recently picked up by Justin Wojciechowski)
**Target**: .NET 10.0 on Apple platforms

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

See also: `/src/docs/CURRENT-STATUS.md` for current compilation status and remaining gaps.

## Repository Structure

```
swift-bindings/
├── src/
│   ├── Swift.Bindings/          # Core binding generator tool
│   │   ├── src/                 # Generator source (89 C# files)
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
│   └── docs/                    # Emitter redesign proposal
│
├── BindingTesting/              # Real-world binding test projects
│   └── Nuke/                    # Nuke image library test case
│       ├── Nuke.xcframework/    # Prebuilt Nuke framework
│       ├── NukeTestApp/         # .NET iOS test application
│       ├── output-ios/          # Generated bindings output
│       ├── build-all.sh         # Full rebuild script
│       ├── regenerate-bindings.sh
│       ├── build-swift-wrapper.sh
│       ├── build-testapp.sh
│       └── validate-sim.sh      # iOS Simulator validation
│
├── docs/                        # Technical documentation (27 files)
├── eng/                         # Build infrastructure, Azure Pipelines
├── build.sh                     # Build the project
├── run-tests.sh                 # Run all tests (unit, integration, runtime)
└── generate.sh                  # Apple framework binding generation
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
| `src/docs/known-issues-workarounds.md` | Major runtime issues and workarounds (Mono JIT bugs, etc.) |
| `docs/binding-overview.md` | High-level binding philosophy |

## Current Capabilities

**Status** (February 2026 - Phase 62 + SwiftUI Bridge v2 Phase 3 + ArraySlice normalization + CryptoSwift fixes):
- **Unit Tests**: 1558 passed
- **Runtime Tests**: 13/13 SwiftUI bridge tests passing on iOS Simulator (Tier 2)
- **Nuke**: 0 errors ✅ (runtime validated)
- **BlinkID**: 0 errors ✅ (runtime validated, 18/18 tests)
- **BlinkIDUX**: 0 errors ✅ (SwiftUI bridge validated, 16/16 tests)
- **BridgeParamTest**: 0 errors ✅ (v2 param types validated, 35/35 tests)
- **Lottie**: 0 errors ✅ (runtime + SwiftUI bridge validated, 15/15 tests)
- **CryptoSwift**: 65.1% binding coverage (103/123 types, 427/656 members) — ArraySlice normalization recovers 21 methods

**Working**:
- Classes, structs (frozen and non-frozen), enums (with associated values, runtime case construction)
- Instance and static methods, properties (getters and setters)
- Async methods (via Swift wrapper generation)
- Protocols (interfaces + proxy generation for C# implementations)
- Generics (bound generics, generic enums, generic classes with DllImport, unbound generic type parameters)
- SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>
- Closures (`@convention(c)` and `@escaping` with frozen types)
- Tuples (1-7 elements with frozen types)
- Operators (arithmetic, comparison, bitwise, unary; automatic pair synthesis)
- Existential containers (protocol composition types)
- CoreGraphics opaque types (CGImage, CGColor, CGContext → IntPtr)
- Swift pointer types (OpaquePointer, UnsafePointer, etc. → IntPtr)
- NSObject subclass parameters in free functions (ObjC bridged marshalling)
- ArraySlice parameter normalization (Swift wrapper converts Array→ArraySlice at call site)
- Binding completeness report (`binding-report.json`)
- `[UnsupportedSwiftType]` attribute on degraded members
- StoreKit 2 bindings (published as experimental NuGet)

**Not Working**:
- Async properties
- SwiftUI/Combine framework types (skipped by generator; manual bridge via UIHostingController available)
- Full actor isolation enforcement

See `src/docs/CURRENT-STATUS.md` for full status details.

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
| **Protocol Conformance Emission** | Not started | Types don't implement protocol interfaces |
| **Async Properties** | #2996 | Properties with async getters unsupported |
| **Actors** | No design | Swift actors unsupported |
| **PATs (full)** | Partial | Protocols with associated types limited |

### Partially Implemented

- **Generics**: Bound generics, generic enums, generic classes work; unbound generic type definitions limited
- **Protocols**: Interface + proxy generation works; protocol conformance emission not yet done
- **JSON TBD format**: Only YAML-like format works

### Known Runtime Issues

- **Mono JIT assertion (jit-info.c:918)**: `condition '!ji->async' not met` — kills process when any closure is passed through P/Invoke (both `@convention(c)` and `@escaping`) or when `SwiftString.PInvoke_GetLength` is called via `CallConvSwift`. Affects all closure tests and SwiftString property access at runtime. Bridge tests use `@_cdecl` functions which are unaffected.
- Mono JIT bug: `swift_getExistentialTypeMetadata` crash (workaround: Swift wrappers)
- SafeHandle in async P/Invoke not preserved (workaround: singleton pattern + IntPtr)
- Non-blittable types with `CallConvSwift` require `IntPtr` + manual marshalling
- See `src/docs/known-issues-workarounds.md` for details

## Building & Testing

**Prerequisites**: macOS with Xcode, .NET 10.0 SDK

**IMPORTANT**: Use the helper scripts documented below instead of running commands manually. The scripts handle edge cases, proper paths, and provide consistent behavior.

```bash
# Build the project
./build.sh

# Run all tests (unit, integration, runtime - handles DotNetHostPath workaround)
./run-tests.sh

# Generate Apple framework bindings (StoreKit, etc.)
./generate.sh --platform iPhoneSimulator --framework StoreKit
```

**Windows**: Only builds Swift.Bindings tool (no runtime/tests). Use the explicit path override for tests:
```bash
dotnet test src/Swift.Bindings/tests/UnitTests -- RunConfiguration.DotNetHostPath="C:\Program Files\dotnet\dotnet.exe"
```

## Helper Scripts Reference

**ALWAYS use these scripts** instead of running the underlying commands manually. They handle edge cases, proper working directories, and provide consistent behavior.

### Root-Level Scripts

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `./build.sh` | Build the entire project | After cloning, after code changes |
| `./run-tests.sh` | Run all tests (unit, integration, runtime) | **Always use this** instead of `dotnet test` directly |
| `./generate.sh` | Generate bindings for Apple frameworks | When testing against StoreKit, SwiftUI, etc. |

### Nuke Testing Scripts (BindingTesting/Nuke/)

These scripts are for testing bindings against the Nuke image loading library on iOS Simulator.

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `./build-all.sh` | Full rebuild: bindings + Swift wrapper + test app | After generator code changes |
| `./regenerate-bindings.sh` | Regenerate C# bindings only | After changing emitter/marshaler code |
| `./build-swift-wrapper.sh` | Rebuild the Swift wrapper library | After bindings regeneration |
| `./build-testapp.sh` | Build the NukeTestApp | After changing test app code |
| `./validate-sim.sh [timeout]` | Run test app on iOS Simulator | **Always use this** for simulator testing |

### BlinkIDUX Bridge Scripts (BindingTesting/BlinkId/) — Shadow Validation

> **Note:** SwiftUI bridge testing is now integrated into TestFramework (Tier 2 runtime tests). These scripts are retained as shadow validation against real-world frameworks until TestFramework proves stable in CI.

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `./build-all-bridge.sh` | Full Step 3 pipeline: generator coverage + bridge build + test app | Shadow validation against BlinkIDUX |
| `./validate-bridge.sh [timeout]` | Run bridge tests on iOS Simulator | Shadow validation |

### BridgeParamTest Scripts (BindingTesting/BridgeTest/) — Shadow Validation

> **Note:** SwiftUI bridge param type tests are now integrated into TestFramework (13 features in coverage matrix, Tier 2 runtime tests). These scripts are retained as shadow validation.

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `./build-all.sh` | Full pipeline: xcframework + bindings + bridge + test app | Shadow validation against synthetic views |
| `./validate.sh [timeout]` | Run 26 tests on iOS Simulator | Shadow validation |

### Typical Workflows

**After modifying generator code:**
```bash
cd BindingTesting/Nuke
./build-all.sh && ./validate-sim.sh 15
```

**After modifying bridge emitter or SwiftUI bridge code:**
```bash
cd TestFramework
./build-and-test.sh && ./run-runtime-tests.sh --tier 2 --timeout 90
```

**After modifying only the test app:**
```bash
cd BindingTesting/Nuke
./build-testapp.sh && ./validate-sim.sh 15
```

**Running all tests:**
```bash
./run-tests.sh
```

### validate-sim.sh Details

The `validate-sim.sh` script provides reliable iOS Simulator testing:
- Installs and launches the app on the booted simulator
- Watches for `TEST SUCCESS` marker (exits early on success)
- Detects crashes via console output and crash log files
- Returns exit code 0 on success, 1 on failure/crash/timeout
- Shows `=== VALIDATION PASSED ===` or `=== CRASH DETECTED ===` / `=== TIMEOUT ===`

**Important**: Always use `./validate-sim.sh` instead of manual `xcrun simctl` commands. The script provides reliable pass/fail detection without arbitrary sleep timers.

### TestFramework Scripts (TestFramework/)

These scripts are for the comprehensive Swift test library that systematically exercises all Swift features the generator handles.

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `./build-xcframework.sh` | Build the Swift test library as xcframework | After adding/modifying Swift test files |
| `./regenerate-bindings.sh` | Generate C# bindings from the xcframework | After generator code changes or xcframework rebuild |
| `./build-and-test.sh` | Full pipeline: build xcframework + generate bindings + bridge | One-step validation after any changes |
| `./build-bridge.sh` | Compile generated SwiftUI bridge + test helpers into framework | After regenerating bindings or editing bridge helpers |
| `./run-runtime-tests.sh` | Build + run runtime tests on iOS Simulator | Runtime validation (Tier 1 default, `--tier 2` for SwiftUI bridge) |
| `./generate-coverage-report.sh` | Generate `coverage-matrix.json` from ABI + binding report | After regenerating bindings, to assess coverage |

**Typical workflow:**
```bash
cd TestFramework
./build-and-test.sh          # Full rebuild + binding generation + bridge
./generate-coverage-report.sh # Generate coverage report
```

**Runtime validation (includes SwiftUI bridge tests at Tier 2):**
```bash
cd TestFramework
./run-runtime-tests.sh --tier 2 --timeout 90
```

**Output files:**
- `output/Swift.SwiftBindingsTestLib.cs` - Generated C# bindings
- `output/Swift.SwiftBindingsTestLib.SwiftUIBridge.swift` - Generated SwiftUI bridge
- `output/Swift.SwiftBindingsTestLib.SwiftUIBridge.cs` - Generated bridge C# bindings
- `output/binding-report.json` - Binding completeness report
- `output/coverage-matrix.json` - Feature coverage matrix (from generate-coverage-report.sh)

See `src/docs/CompletedPhases/comprehensive-test-library-design.md` for the full test library design and feature coverage matrix.

## TestFramework Feedback Loop

The TestFramework is the primary validation tool for generator changes. It contains Swift source files exercising mapped features across categories including types, closures, generics, protocols, async, operators, SwiftUI bridge, and more. After any generator change, the TestFramework tells you whether you fixed what you intended and whether you broke anything else. SwiftUI bridge tests (enum/class/closure/optional/async Views) are integrated at Tier 2.

### When to Run

**Always run after changes to these directories:**
- `src/Swift.Bindings/src/Marshaler/` — type marshalling logic
- `src/Swift.Bindings/src/Emitter/` — code generation
- `src/Swift.Bindings/src/Parser/` — ABI parsing
- `src/Swift.Bindings/src/TypeDatabase/` — type lookup and resolution
- `src/Swift.Bindings/src/Model/` — type declarations

**Also run after:**
- Adding new Swift test files to `TestFramework/Sources/`
- Modifying existing Swift test files

**The full validation sequence after generator changes:**
```bash
./run-tests.sh                                          # Unit tests pass first
cd TestFramework && ./build-and-test.sh && ./generate-coverage-report.sh  # Then coverage
```

### Understanding Coverage Report Output

The `generate-coverage-report.sh` script prints a summary and writes `output/coverage-matrix.json`. The summary looks like:

```
Must-pass features: 88/93 passing, 5 degraded, 0 missing
Known-unsupported features: 47/52 have tests (5 compiled out)

*** WARNING: 5 must-pass feature(s) have skipped binding members ***
  - generic_struct (generic_types): 6 skipped member(s)
      Property wrapped: AnyTypeFallback
      Method init: UnsupportedSignature
```

**Feature categories:**
- **must_pass** — Features the generator is expected to handle. These have Swift test code and should produce complete bindings.
- **known_unsupported** — Features not yet implemented (actors, property wrappers, keypaths, etc.). Tracked for completeness but don't indicate regressions.

**Feature statuses (within must_pass):**
- **passing** — Test exists, all binding members emitted successfully. This is the goal state.
- **degraded** — Test exists, but some binding members were skipped. The WARNING section lists exactly which members and why.
- **missing** — No test file exists for this feature. Should not happen; add a test if it does.
- **compiled_out** — Swift source guarded by `#if swift(>=6.0)` or similar; absent from ABI on current toolchain.

### Skip Reason Reference

When a member is skipped, the binding report records a `SkipReason`. These are defined in `src/Swift.Bindings/src/Reporting/BindingReport.cs`:

| Skip Reason | Meaning | Typical Fix Area |
|-------------|---------|------------------|
| `UnsupportedSignature` | Method/property signature contains types the marshaller can't handle | `Marshaler/` handlers, `TypeDatabase/` type resolution |
| `AnyTypeFallback` | Type resolved to opaque `Any` instead of a concrete type | `TypeDatabase/TypeDatabaseExtensions.cs`, type XML files |
| `UnsupportedExistential` | Existential type (any Protocol) with unsupported composition | `Marshaler/`, existential handling |
| `AsyncProperty` | Property has async getter/setter (not yet supported) | `Emitter/StringEmitter/Handler/PropertyHandler.cs` |
| `UnsupportedType` | Type resolution failed entirely | Type handlers in `Emitter/StringEmitter/Handler/` |
| `UnsupportedClosure` | Closure type not supported (nested, async, etc.) | `Marshaler/ClosureHandler.cs` |
| `GenericProtocolConstraint` | Protocol with associated types used as constraint | Generic handling in `Marshaler/` |
| `UnsatisfiedGenericConstraint` | Bound generic has unresolvable constraints | Generic handling in `Marshaler/` |
| `DuplicateSignature` | Multiple members with identical C# signatures | Emitter deduplication logic |
| `UnsupportedAsyncStream` | AsyncStream with element type that can't be marshalled | Async handling in `Emitter/StringEmitter/Handler/` |
| `SwiftUIConstraint` | Type from SwiftUI (intentionally skipped) | N/A — by design |
| `CombineFramework` | Type from Combine (intentionally skipped) | N/A — by design |
| `MissingHandler` | No emitter handler for this declaration kind | Add handler in `Emitter/StringEmitter/Handler/` |
| `Unknown` | Catch-all for unclassified skip reasons | Investigate the specific member in the emitter |

### Reacting to Results

**After a targeted fix (you expect specific features to improve):**
1. Run the coverage report
2. Verify the specific features moved from `degraded` → `passing`
3. Verify no other features regressed (degraded count should not increase elsewhere)
4. Report the before/after: "opaque_pointer: degraded → passing, total degraded: 8 → 5"

**If degraded count decreased (features fixed):**
- Confirm the fixed features in the coverage output
- Update `src/docs/CURRENT-STATUS.md` if the fix is significant

**If degraded count increased (regression):**
- Check the WARNING section for newly degraded features
- Look at the skip reason to identify which code area caused it
- The `binding_skips` array in `coverage-matrix.json` has full details per feature:
  ```json
  {
    "name": "feature_name",
    "test_status": "degraded",
    "binding_skips": [
      { "name": "methodName", "kind": "Method", "reason": "UnsupportedSignature", "details": "..." }
    ]
  }
  ```
- Cross-reference the `reason` with the table above to find the responsible code area
- The `details` field often contains the specific type or signature that failed

**If a known_unsupported feature starts passing after your change:**
- This means you accidentally (or intentionally) enabled a new feature
- Consider promoting it: remove it from `KNOWN_UNSUPPORTED_FEATURES` in `generate-coverage-report.sh` to make it a must_pass feature going forward

### Current Baseline (Phase 62 + ArraySlice normalization)

| Metric | Value |
|--------|-------|
| Must-pass features | 116 total |
| Passing | 61 (incl. 13 SwiftUI bridge + 4 ArraySlice) |
| Degraded | 0 |
| Missing | 51 (disabled dirs: Generics, Protocols, Async, etc.) |
| Known-unsupported | 56 |
| Types emitted | 55/65 |
| Members emitted | 266/312 |
| Runtime tests (Tier 2) | 13/13 SwiftUI bridge passing |

**Runtime test tier notes**:
- Tier 1: Core marshalling (string, enum, class, blittable) — pass
- Tier 2: SwiftUI bridge (13 tests) — all pass
- Tier 3: Closure tests, MutableProps (SwiftString) — deferred due to Mono JIT assertion crash

### Investigating a Specific Degraded Feature

To understand why a feature is degraded:

1. **Find the Swift source**: Features map to files in `TestFramework/Sources/SwiftBindingsTestLib/<category>/`. Feature names use snake_case matching the file/section.
2. **Check the binding report**: `jq '.SkippedItems[] | select(.Name == "methodName")' output/binding-report.json`
3. **Check the generated bindings**: Search `output/Swift.SwiftBindingsTestLib.cs` for the type — skipped members have `[UnsupportedSwiftType("reason")]` attributes.
4. **Trace through the generator**: The skip reason tells you which handler rejected the member. Set a breakpoint or add logging in the corresponding handler file.

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
# With bridge hints for SwiftUI view customization:
SwiftBindings -a abi.json -d lib.dylib -t lib.tbd -o output/ --bridge-hints bridge-hints.json
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
