# BlinkIDUX Binding Fixes

Investigation and fix date: 2026-02-17

## Summary

BlinkIDUX.xcframework bindings generate C# code that compiles cleanly (0 errors), but the Swift wrapper compilation had 7 errors across 3 categories. Three generator-level fixes were implemented, resolving 1 error fully and eliminating dead code. The remaining 6 errors are actor isolation violations that require a new feature (actor-aware wrapper emission, tracked as roadmap item #5).

## Fixes Implemented

### Fix 1: Dependency module imports in Swift wrapper

**Problem**: `--framework-dependency` adds `-F` search paths for swiftc but never emits `import BlinkID` in the wrapper. The `BlinkIDClassFilter` protocol references `BlinkID.BlinkIDSDK.DocumentClassInfo`, causing `cannot find type 'BlinkID' in scope`.

**Solution**: Added `DependencyModuleNames` property to `ModuleDecl` (distinct from ABI-derived `Dependencies` which is filtered through `AppleFrameworks`). `Program.cs` extracts module names from `--framework-dependency` resolved dependencies. `ModuleHandler.EmitSwiftImports()` emits `import {dep}` for each dependency module.

**Files changed**:
- `src/Swift.Bindings/src/Model/TypeDecl/ModuleDecl.cs` — Added `DependencyModuleNames` property
- `src/Swift.Bindings/src/Program.cs` — Extract and pass dependency module names into `GenerateBindings`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` — Emit `import` statements for dependency modules

**Tests**: `EmitSwiftImports_ImportsDependencyModuleNames`, `EmitSwiftImports_DependencyModulesDoNotDuplicateSelf`, `EmitSwiftImports_DependencyModulesDoNotDuplicateAppleFramework`

### Fix 2: `-strict-concurrency=minimal` for wrapper compilation (temporary)

**Problem**: Swift 6 strict concurrency rejects accessing `@MainActor`-isolated and actor-isolated properties from nonisolated wrapper functions. The ABI JSON encodes `@MainActor` as generic "Custom" in `declAttributes` — indistinguishable from other custom attributes.

**Solution**: Added `-strict-concurrency=minimal` to swiftc args in `SwiftWrapperCompiler.cs`. This is a temporary mitigation — wrapper code is C-interop level (`@_silgen_name` functions with raw pointer operations) where concurrency safety is managed by the C# caller.

**Important finding**: `-strict-concurrency=minimal` only affects Sendable checking, **not** actor isolation. The 6 actor isolation errors remain as pre-existing. Alternatives tested and rejected:
- `-swift-version 5` — still enforces actor isolation in Swift 6 toolchain
- `@preconcurrency import` — does not suppress actor isolation errors
- `-disable-access-control` — only affects visibility, not concurrency
- `@MainActor` on wrapper functions — fixes 4 of 6 errors but not custom actor or Task-block isolation

The proper fix (parsing `@MainActor` from `.swiftinterface` and emitting matching actor isolation on wrapper functions) is tracked as roadmap item #5.

**Files changed**:
- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` — Added `-strict-concurrency=minimal` flag
- `src/docs/roadmap.md` — Added roadmap item #5 (Actor-Aware Wrapper Emission)

**Tests**: `InvokeSwiftCompiler_IncludesStrictConcurrencyMinimal`

### Fix 3: Skip protocol proxy for protocols with unsupported member types

**Problem**: `UXThemeProtocol` has 21 required properties returning `SwiftUI.Color`/`SwiftUI.Font`. The EveryProtocol conformance emits all of them, fails to compile, and the post-processor strips the extension block — but vtable structs, SetVtable functions, and registration code survive as dead code. If the Swift EveryProtocol conformance is skipped but the C# proxy is still emitted, the proxy's calls to `SetVtable`/`WitnessTableGetter` Swift symbols would cause linker/runtime failures.

**Solution**: Used `MemberEmissionValidator.ReferencesUnsupportedModule()` (changed from `private` to `internal`) as a shared predicate to skip both:
1. **Swift side**: EveryProtocol conformance + witness dispatch accessors (in `ModuleHandler.EmitEveryProtocolConformances`)
2. **C# side**: Proxy class emission (in `ProtocolHandler.Emit`)

The C# **interface** is still emitted — it's a pure type contract with no Swift symbol dependencies. Only the proxy class (which contains P/Invoke calls to Swift) is skipped. A `HasMembersReferencingUnsupportedModule` helper method checks all non-static properties, method signatures, and subscripts.

**Reporting**: Uses `RecordMemberSkipped` (not `RecordTypeSkipped`) because the type was already marked emitted for the interface. `RecordTypeSkipped` silently drops entries for already-emitted types. The proxy skip is recorded as a member-level entry with name `"{protocolName}Proxy"`.

**Files changed**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` — Changed `ReferencesUnsupportedModule` from `private` to `internal`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` — Added `HasMembersReferencingUnsupportedModule` helper; added `.Where()` filter in `EmitEveryProtocolConformances`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` — Guarded proxy emission with unsupported module check

**Tests**:
- Predicate tests: `HasMembersReferencingUnsupportedModule_SwiftUIProperty_ReturnsTrue`, `_SupportedProperty_ReturnsFalse`, `_StaticSwiftUIProperty_ReturnsFalse`, `_CombineProperty_ReturnsTrue`, `_MethodWithSwiftUIArg_ReturnsTrue`, `_EmptyProtocol_ReturnsFalse`
- Swift emission tests: `EmitEveryProtocol_SkipsProtocolWithSwiftUIPropertyTypes`, `EmitEveryProtocol_EmitsProtocolWithSupportedTypes`
- Full pipeline tests (P3 Codex fix): `Emit_SkipsProxyForProtocolWithSwiftUIMembers` (verifies interface IS emitted, proxy is NOT, no Swift EveryProtocol), `Emit_EmitsProxyForProtocolWithSupportedTypes` (verifies both interface AND proxy emitted, Swift EveryProtocol present)

## Current Status (post-fix)

- **C# bindings**: 0 compile errors
- **Swift wrapper**: 6 pre-existing actor isolation errors (tracked in roadmap #5)
  - 4 on `CameraModel` `@MainActor` protocol properties
  - 1 on `BlinkIDEventStream` custom actor `stream` property
  - 1 on `Camera` `@MainActor` class `sampleBuffer` property
- **Dead code eliminated**: `UXThemeProtocol` vtable/SetVtable/registration code no longer emitted in Swift wrapper; proxy class no longer emitted in C#
- **Dependency imports**: `import BlinkID` correctly emitted in Swift wrapper
- **Coverage**: 31 types emitted (4 skipped), 124 members emitted (48 skipped, 41 synthesized)
- **Test baseline**: 3185 unit tests, 700 integration, 156 runtime library (0 failures)

## Generator Skip Report (52 items)

| Reason | Count | Details |
|--------|-------|---------|
| SwiftUIConstraint | 31 | `BlinkIDTheme` color/font properties (SwiftUI.Color, SwiftUI.Font), `@Published` projected properties, `UXThemeProtocolProxy` |
| UnsupportedSignature | 6 | `BlinkIDAnalyzer.events`, `Camera.error`, `ScanningViewModel.reticleState` (non-simple enum) |
| UnsupportedType | 5 | Actor `unownedExecutor` (x3), `CMSampleBuffer`, `BlinkID.RegionOfInterest` |
| SwiftUIView | 4 | `BlinkIDUXView`, `CameraPreview`, `CameraView`, `NoInternetView` (bridge generation available for 2) |
| AnyTypeFallback | 3 | `BlinkIDResultState.scanningResult`, `CameraModel.error`, `ScanningResultProtocol.scanResult` |
| GenericTypeCallback | 2 | `ScanningViewModel.analyze/processAnalyzerResult` (async in generic class) |
| UnsupportedExistential | 1 | `PreviewSource.connect(to: any PreviewTarget)` |

## Demangler Issues

Several symbols failed to demangle, all involving constrained existential types with associated type constraints:

```
BlinkIDAnalyzer.events  -- EventStream_p where Event == UIEvent
BlinkIDUXModel.init     -- CameraFrameAnalyzer_p where Event == UIEvent
ScanningViewModel.init  -- CameraFrameAnalyzer_p where Event == UIEvent
```

These use Swift's constrained existential syntax (`any Protocol where AssociatedType == ConcreteType`) which the demangler doesn't fully support yet.

## Reproduction

```bash
# Generate bindings
mkdir -p /tmp/validation/BlinkIDUX
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /Users/wojo/Dev/Libraries/BlinkIDUX.xcframework \
  --framework-dependency BindingTesting/BlinkId/BlinkID.xcframework \
  -o /tmp/validation/BlinkIDUX/

# Verify C# compiles
cd /tmp/validation/BlinkIDUX
dotnet build BlinkIDUX.Swift.iOS.csproj -p:EnableDefaultCompileItems=false

# See Swift wrapper errors directly
xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
  -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -strict-concurrency=minimal \
  -F /Users/wojo/Dev/Libraries/BlinkIDUX.xcframework/ios-arm64_x86_64-simulator \
  -F BindingTesting/BlinkId/BlinkID.xcframework/ios-arm64_x86_64-simulator \
  -module-name BlinkIDUXSwiftBindings \
  -o /tmp/BlinkIDUXWrapper.dylib \
  /tmp/validation/BlinkIDUX/Swift.BlinkIDUX.swift
```
