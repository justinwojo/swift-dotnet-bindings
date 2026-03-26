# SwiftUI Bridge SDK Integration

**Created**: March 25, 2026
**Status**: Implemented
**Priority**: High — consumers binding SwiftUI libraries (e.g., BlinkIDUX, Lottie) get `DllNotFoundException` at runtime without this

---

## Problem

The generator emits both `{Namespace}.SwiftUIBridge.swift` and `{Namespace}.SwiftUIBridge.cs` for libraries with SwiftUI views. The C# file is included in compilation, but the Swift file is **explicitly excluded** from wrapper compilation (`SwiftWrapperCompiler.cs:691`). The bridge Swift code is never compiled into a native framework, so all bridge P/Invoke calls (`SBW_*` entry points) throw `DllNotFoundException` at runtime.

Today this only works via manual shell scripts in BindingTests (`build-bridge.sh`). A consumer using the SDK (`dotnet build`) gets broken SwiftUI bridge code with no indication why.

## What "Done" Looks Like

`dotnet build` on a project with a SwiftUI-containing xcframework produces a working bridge automatically. `dotnet pack` includes the bridge framework in the NuGet. No manual steps.

---

## Architecture

The bridge compilation is structurally identical to wrapper compilation — same Swift compiler, same xcframework output structure — with simpler inputs:

| | Wrapper | Bridge |
|---|---|---|
| **Input files** | All `*.swift` except bridge | Only `*.SwiftUIBridge.swift` |
| **Module name** | `{Module}SwiftBindings` | `{Module}Bridge` |
| **Swift imports** | `import {Module}` | `import UIKit`, `import SwiftUI`, `import {Module}` |
| **Post-processing** | Yes (strip broken blocks, module collision fixes) | No (self-contained `@_cdecl` functions) |
| **Thunk support** | Yes (`.arm64.s` assembly) | No |
| **Pre-compiled module** | Yes (collision resolution) | No |

---

## Implementation Plan

### 1. `SwiftWrapperCompiler.cs` — Add bridge compilation methods

Add `CompileBridge` and `CompileBridgeAll` methods that reuse `InvokeSwiftCompiler`, `CreateXCFrameworkStructure`, `WriteFrameworkPlist`, etc. Also add `CollectBridgeSwiftFiles()` (collects `*.SwiftUIBridge.swift`).

Key differences from wrapper compilation:
- No `SwiftWrapperPostProcessor` (bridge code is self-contained)
- No thunk compilation (no `.arm64.s` files)
- No pre-compiled module path
- Module name is `{moduleName}Bridge`

### 2. CLI — Add `--compile-bridge-only` flag

Mirror `--compile-wrapper-only`. Resolves xcframework for search paths, collects bridge `.swift` files, compiles into `{Module}Bridge.xcframework`, updates metadata props.

### 3. `binding-metadata.props` — Add bridge properties

Extend `XCFrameworkMetadataExtractor.EmitMetadataProps`:
- `_SwiftBindingHasBridgeSwift` — set during generation when bridge `.swift` is emitted
- `_SwiftBindingBridgeModuleName` — `{Module}Bridge`
- `_SwiftBindingHasBridgeXCFramework` — set after bridge compilation succeeds
- `_SwiftBindingBridgeSliceCount` — number of architecture slices

### 4. `Sdk.targets` — New targets

**`_CompileSwiftUIBridge`** (after `_CompileSwiftWrapper`):
- Condition: `_SwiftBindingHasBridgeSwift == True` and not ObjC
- Invokes generator with `--compile-bridge-only`
- Uses same framework search paths as wrapper (source xcframework + dependencies)
- `ContinueOnError="WarnAndContinue"` (bridge failure is non-fatal)

**`_UpdateSwiftBridgeMetadata`** (after bridge compilation):
- Reads `_SwiftBindingHasBridgeXCFramework` and `_SwiftBindingBridgeModuleName` from metadata props

**`_ValidateSwiftBridgeCompilation`**:
- SWIFTBIND052 warning if bridge `.swift` exists but xcframework doesn't (bridge compilation failed)

### 5. NativeReference resolution (Target 6)

Add bridge xcframework as `<NativeReference>` alongside the wrapper:
```xml
<NativeReference Include=".../{BridgeModuleName}.xcframework"
                 Condition="_SwiftBindingHasBridgeXCFramework == True">
  <Kind>Framework</Kind>
</NativeReference>
```

### 6. NuGet pack layout (Target 7b)

Add bridge xcframework to pack:
```xml
<None Include=".../{BridgeModuleName}.xcframework/**"
      Pack="true"
      PackagePath="runtimes/{rid}/native/{BridgeModuleName}.xcframework/" />
```

### 7. Consumer targets (`ConsumerTargetsEmitter.cs`)

Add bridge NativeReference to the consumer `.targets` file so NuGet consumers get it automatically.

### 8. CLI-mode `.csproj` (`BindingProjectEmitter.cs`)

Add conditional `<NativeReference>` for bridge xcframework in the generated `.csproj` (non-SDK mode).

### 9. Tests

- Unit tests for `CompileBridge`/`CollectBridgeSwiftFiles`
- Unit tests for metadata props bridge properties
- Unit tests for consumer targets bridge NativeReference
- Integration: BindingTests end-to-end through SDK path

---

## Key Files

| File | Changes |
|------|---------|
| `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` | `CompileBridge`, `CompileBridgeAll`, `CollectBridgeSwiftFiles` |
| `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs` | Bridge properties in `EmitMetadataProps` |
| `src/Swift.Bindings/src/Program.cs` / `BindingsGeneratorCommand.cs` | `--compile-bridge-only` handler |
| `src/Swift.Bindings/src/Configuration/CliOptions.cs` | `--compile-bridge-only` option |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` | 3 new targets, updates to NativeReference + pack targets |
| `src/Swift.Bindings/src/Emitter/ConsumerTargetsEmitter.cs` | Bridge NativeReference |
| `src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs` | Bridge NativeReference in generated `.csproj` |

## Error Codes

| Code | Severity | Meaning |
|------|----------|---------|
| SWIFTBIND052 | Warning | SwiftUI bridge compilation failed — bridge sessions will throw `DllNotFoundException` |
