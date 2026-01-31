# Phase 1: Infrastructure (Required to Run Anything)

**Status**: COMPLETE

This phase established the foundation required to generate and run any bindings at all.

---

## 1.1 Fix Hardcoded Library Paths
**Status**: DONE

**Problem**: All `DllImport` attributes contained absolute paths.

**Solution**: Added `-l` / `--library-name` CLI flag to specify runtime library name:
```bash
dotnet run --project src/Swift.Bindings/src -c Release -- \
  -a Nuke.abi.json \
  -d /path/to/Nuke.framework/Nuke \
  -t Nuke.tbd \
  -l "Nuke" \
  -o output/
```

The `-d` flag specifies the dylib for metadata extraction during generation.
The `-l` flag specifies the library name used in generated `DllImport` attributes.

**Important**: If the library name starts with `@` (e.g., `@rpath/Nuke.framework/Nuke`), you must escape it with a backslash because .NET interprets `@filename` as a response file directive:
```bash
-l '\@rpath/Nuke.framework/Nuke'
```

**Files modified**:
- `src/Swift.Bindings/src/Program.cs` - Added library name argument
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Separate dylib path from runtime library name

---

## 1.2 iOS Workload Setup
**Status**: DONE

```bash
sudo dotnet workload install ios maui maui-ios android
```

**Note**: The test app uses its own `global.json` and `Directory.Build.props/targets` files to isolate from the repo's Arcade SDK build system.

---

## 1.3 Framework Bundling
**Status**: DONE

Test project configured with:
- References xcframework
- Uses `NativeReference` for framework bundling
- **Build succeeds** with generated bindings

---

## 1.4 Code Generation Bugs
**Status**: DONE

Three bugs in the binding generator caused ~95 compilation errors. All fixed:

**1. Duplicate Interface Members** (~20 errors)
- `ProtocolHandler` now tracks emitted properties and methods to prevent duplicates
- Added `GetMethodSignatureKey()` for method signature comparison
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**2. Missing ISwiftObject Implementations** (~60 errors)
- `EnumHandler` now emits stub implementations for all ISwiftObject methods
- Added `EmitEnumISwiftObjectImplementation()` method
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**3. Duplicate Method Definitions** (~15 errors)
- `BaseHandler.HandleBaseDecl()` now tracks method signatures to prevent duplicates
- `NameProvider.GetPInvokeName()` now includes mangled name hash for uniqueness
- **Files modified**:
  - `src/Swift.Bindings/src/Marshaler/IHandler.cs`
  - `src/Swift.Bindings/src/Marshaler/NameProvider.cs`

---

## 1.5 Additional Code Generation Issues
**Status**: DONE

After fixing the Phase 1.4 bugs, 24 additional errors were revealed. All fixed:

**1. Enum Property Access Errors** (~8 errors)
- Added `_payload` field, `_payloadSize`, and `Payload` property to `EnumHandler`
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**2. Missing Type Definitions** (~2 errors)
- Refactored `URL` class to use `SwiftSafeHandle<URL>` for P/Invoke compatibility
- Added protocol type detection to skip methods with interface parameters
- **Files modified**:
  - `src/Swift.Runtime/src/Swift/URL.cs`
  - `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**3. Missing Property Accessors** (~4 errors)
- `PropertyHandler` now checks if accessor methods will be skipped before emitting property
- Skips properties with `AnyType` or other unsupported types in signature
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`

**4. Bound Generic Buffer Issues** (~1 error)
- Added `EmitBoundGenericArguments()` call to `WrapperEmitter.EmitConstructor()`
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**5. Protocol Parameter Errors** (~9 errors)
- `WrapperSignatureBuilder` now detects `TypeRecordKind.Protocol` and uses `AnyType` placeholder
- Methods with protocol parameters/return types are skipped (interfaces don't have `Payload`)
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

---

## Summary

Phase 1 established critical infrastructure:
- Library path configuration via CLI flags
- iOS workload and framework bundling
- Fixed 95+ compilation errors from code generation bugs
- Fixed 24+ additional code generation issues

This phase was foundational - without these fixes, no bindings could compile or run.
