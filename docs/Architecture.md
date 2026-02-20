# Architecture

How the generator works under the hood. You don't need to understand any of this to use the tool — this is for the curious.

## Pipeline Overview

```
Swift Framework (.xcframework)
         │
         ├── .swiftinterface (ABI contract)
         ├── .dylib (native code)
         └── .tbd (symbol table)
         │
         ▼
┌─────────────────────────────────────┐
│         Generator Pipeline          │
├─────────────────────────────────────┤
│  1. XCFrameworkResolver             │
│     Extracts ABI JSON, dylib, TBD  │
│                                     │
│  2. SwiftABIParser                  │
│     Parses ABI JSON → type model    │
│                                     │
│  3. TypeDatabase                    │
│     Resolves cross-module types     │
│                                     │
│  4. Marshaler                       │
│     Decides how each type crosses   │
│     the Swift/C# boundary           │
│                                     │
│  5. Emitter                         │
│     Generates C# and Swift code     │
│                                     │
│  6. SwiftWrapperCompiler            │
│     Compiles generated Swift →      │
│     xcframework                     │
└─────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────┐
│         Generated Output            │
├─────────────────────────────────────┤
│  Swift.{Module}.cs    (C# bindings) │
│  Swift.{Module}.swift (wrappers)    │
│  {Module}SwiftBindings.xcframework  │
│  binding-report.json  (metrics)     │
│  {Module}.Swift.iOS.csproj          │
└─────────────────────────────────────┘
```

## Step 1: Input Resolution

The `XCFrameworkResolver` takes an `.xcframework` directory and finds everything the generator needs:

- Parses `Info.plist` to discover iOS platform slices (simulator preferred, device fallback)
- Locates the Swift module from `Modules/*.swiftmodule`
- Finds the `.abi.json` (or generates it from `.swiftinterface` via `swift-frontend`)
- Finds the `.tbd` (or generates it from the dylib via `xcrun tapi stubify`)
- Discovers the `.swiftinterface` for internal member detection
- Derives module name and library name automatically

## Step 2: ABI Parsing

The `SwiftABIParser` reads the ABI JSON file — a structured description of the module's public API surface — and builds an in-memory model of every type and member.

The ABI JSON contains:
- Type declarations (classes, structs, enums, protocols)
- Member declarations (methods, properties, constructors)
- Type relationships (inheritance, conformances, generic constraints)
- Mangled symbol names (for P/Invoke entry points)

The parser also reads the `.swiftinterface` to detect `@inlinable internal` and `@usableFromInline internal` members that shouldn't be exposed in bindings.

## Step 3: Type Database

The `TypeDatabase` maps Swift types to their C# representations. It handles:

- **Primitive types**: `Swift.Int` → `nint`, `Swift.Bool` → `bool`, etc.
- **Foundation types**: `Foundation.URL` → `NSUrl`, `Foundation.Date` → `DateTimeOffset`
- **Apple framework types**: UIKit, AppKit, CoreGraphics types with ObjC bridging
- **Cross-module resolution**: Types from dependent frameworks

The type database is stored as XML files shipped with the `Swift.Runtime` package. It's extensible — you can add custom type mappings.

## Step 4: Marshaling

The `Marshaler` decides how each Swift type crosses the interop boundary. Types fall into categories:

| Category | Swift Example | C# Strategy |
|----------|--------------|-------------|
| **Struct** (frozen, blittable) | `CGPoint`, `CGRect` | C# `struct` with same memory layout |
| **ClassWithOpaquePayload** (non-frozen) | Non-frozen struct | C# `class` with `SafeHandle` |
| **ClassWithBufferStruct** (frozen + ref fields) | Frozen struct with `String` | C# `class` with raw buffer |
| **Class** | Swift class | C# `class` with ARC (`SafeHandle`) |
| **Unknown** | Unsupported type | Pruned from output |

The marshaler also handles:
- **Mono JIT risk detection** — flags methods that would trigger the Mono crash
- **ArraySlice normalization** — converts `ArraySlice<T>` parameters to `Array<T>` via Swift wrappers
- **Protocol proxy generation** — determines which protocols need C# implementation support

## Step 5: Code Emission

The `Emitter` generates both C# and Swift code. It's organized by concern:

### C# Emission

- **TypeEmitter** — class/struct/enum declarations
- **MethodHandler** — method bodies with P/Invoke calls
- **PInvokeEmitter** — `[DllImport]` declarations with correct entry points and calling conventions
- **ClosureEmitter** — closure callback + return marshalling
- **ProtocolProxyEmitter** — proxy classes for C#→Swift protocol conformance
- **WitnessDispatchEmitter** — protocol property/method dispatch through witness tables
- **SwiftUIBridgeEmitter** — SwiftUI view bridge C# classes

### Swift Emission

- **Async wrappers** — `@_cdecl` functions that bridge async Swift calls to C# callbacks
- **Protocol dispatch stubs** — `@_silgen_name` functions for witness table access
- **Closure Cdecl wrappers** — `@_silgen_name` functions that convert `@convention(c)` closures to `@convention(swift)`
- **ArraySlice normalizers** — wrappers that convert `ArraySlice<T>` to `Array<T>`
- **SwiftUI bridge** — `UIHostingController` wrapper with `@_cdecl` exports

## Step 6: Wrapper Compilation

The `SwiftWrapperCompiler` takes the generated `.swift` files and compiles them into an xcframework:

1. Post-processes the Swift source (removes known-broken patterns from code generation)
2. Compiles for simulator and/or device architectures using `swiftc`
3. Assembles the slices into `{Module}SwiftBindings.xcframework`

The wrapper xcframework is bundled alongside the source xcframework in the NuGet package.

## Memory Management

Swift uses automatic reference counting (ARC). The generated bindings integrate with this via `SafeHandle`:

- **Class instances**: Wrapped in `SwiftSafeHandle<T>`. `retain` on creation, `release` on `Dispose()` or GC finalization.
- **Struct instances**: Copied into managed memory. Value witness table operations handle initialization and destruction.
- **String marshalling**: `SwiftString` manages the native string lifecycle. Conversion to/from `string` involves UTF-8 encoding at the boundary.

The `Swift.Runtime` package provides the core interop types: `SwiftString`, `SwiftArray<T>`, `SwiftOptional<T>`, `SwiftSafeHandle<T>`, and ARC helpers.

## P/Invoke Calling Conventions

Generated P/Invoke declarations use two calling conventions:

- **`CallConvSwift`** — the Swift calling convention, which uses additional registers for `self`, error, and async context. Used for direct calls into Swift dylibs.
- **`CallConvCdecl`** — standard C calling convention. Used for `@_cdecl` wrapper functions, SwiftUI bridge, and Mono JIT workaround paths.

For non-final class methods with library evolution enabled, the generator uses dispatch thunk symbols (`Tj` suffix) instead of direct function symbols.

## Project Structure

```
src/
├── Swift.Bindings/src/          Generator
│   ├── Parser/                  ABI JSON → type model
│   ├── TypeDatabase/            Type mapping + resolution
│   ├── Marshaler/               Boundary crossing decisions
│   ├── Emitter/                 C# + Swift code generation
│   └── Model/                   Internal type model
├── Swift.Runtime/src/           Runtime library
│   └── Swift/                   SwiftString, SwiftArray, ARC, SafeHandle
├── Swift.Bindings.Sdk/          MSBuild SDK package
└── Swift.Bindings.Templates/    dotnet new template

TestFramework/                   Comprehensive test library (93 features)
validation-libraries.json        Library validation manifest (31 targets)
scripts/                         Fetch + build infrastructure
```

---

## Next Steps

- **[Getting Started](Getting-Started)** — Start using the tool
- **[Customization](Customization)** — Control the generator output
- **[Supported Features](Supported-Features)** — What's covered
