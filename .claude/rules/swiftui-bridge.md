---
paths:
  - "**/SwiftUIBridge*"
  - "**/SwiftUIViewDetector*"
  - "**/SwiftUIBridgeCollector*"
  - "**/SwiftUIBridgeEmitter*"
  - "**/AsyncPattern*"
  - "**/InitAnalyzer*"
  - "**/BridgeHint*"
---

# SwiftUI Bridge Architecture

## Detection & Collection Pipeline
1. `SwiftUIViewDetector.cs` checks `Conformances` for View protocol (SwiftUI or SwiftUICore)
2. `BaseHandler.HandleBaseDecl()` in `IHandler.cs` skips View types before handler dispatch
3. `SwiftUIBridgeCollector.cs` — thin static facade over `ModuleEmissionContext.CollectSwiftUIView` (state is per-module-instance, not process-global; dedups on the module-qualified name, so same-leaf views under different parents all collect)
4. `ModuleEmissionContext.AssignBridgeIdentifiers` — every generated Swift symbol and C# type name derives from `ViewBridgeInfo.Identifier`: the leaf name when unique among collected views, else derived from the enclosing-type path for the whole leaf group (`OuterA.ContentView` → `OuterAContentView`); a residual identifier tie keeps the first view in ABI order and skips the rest fail-closed (`DuplicateBridgeIdentifier`). Hint/async-pattern dictionaries stay **leaf-keyed** deliberately
5. `SwiftUIBridgeEmitter.cs` + `.InitAnalyzer.cs` + `.AsyncPattern.cs` — bridge emission

## View Classification
- Simple views: Session class + @_cdecl + handle tracking
- Async views (inferred or dictionary): Task+callback Create pattern
- Precedence: KnownAsyncPatterns dict → ABI inference → Simple → Template
- Unsupported params → entire view falls back to template

## Param Type Support
- v1: `() -> Void` closures, primitives (Int/Bool/Double/Float), String
- v2 (TypedClosure): `(T...) -> R` closures with primitive args/return (max 4 params)
- TypedClosure ABI: `@convention(c) (T_abi..., UnsafeMutableRawPointer?) -> R_abi` + C# trampoline
- `[UnmanagedCallersOnly]` requires all params/return blittable — String closure args unsupported
- **BoundEnum**: C# classes with `.RawValue`, NOT C# enum types. Use `.RawValue` not `(int)` casts

## Async Inference (`InferAsyncPattern()`)
- Recursive constructor chain resolution (max 3 levels, cycle detection)
- `SelectBestConstructor()` ranks by: fewest bridgeable params → shallowest async depth → ABI order
- Cross-module: `ResolveModuleType()` for same-module; `MapParameterType` → TypeDB for cross-module (BoundType/BoundEnum leaf)
- `ConstructionChain == null` → legacy emission; `!= null` → data-driven emission
- View built in Create scope (not session init) — fixes mixed chain + leaf param views

## Swift Code Patterns
- Session fields and liveHandles: `internal` not `private` (test helpers in separate file)
- Typealias visibility: `public typealias` for @_cdecl function params
- ExtraSwiftImports auto-populated from cross-module flatParam source modules
- Bool ↔ Int32 conversion: `? 1 : 0` / `!= 0` in both Swift and C#

## Async Detection Fallback
- Parser `IsAsync` fallback: `DetectAsyncFromMangledName()` (internal) checks for `Ya` marker in mangled name when demangler fails

## Cross-Boundary ABI Patterns
- BoundType Swift ABI: `{name}Ptr: UnsafeMutableRawPointer` + null guard + `Unmanaged<T>.fromOpaque(ptr).takeUnretainedValue()`
- BoundEnum Swift ABI: raw value type + `TypeName(rawValue: val)!` conversion
- Null-safety: Swift null-pointer guard before `Unmanaged` cast (error callback) + C# `ArgumentNullException` before P/Invoke

## Skip Reasons
- `SwiftUIView` — non-generic View conformance (collected for bridge)
- `SwiftUIConstraint` — generic type parameter on View (skipped entirely)
- Both can coexist on the same type
