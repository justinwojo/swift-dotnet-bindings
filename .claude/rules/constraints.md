---
paths:
  - "src/Swift.Bindings/src/**"
---

# Critical Architectural Constraints

These are "trap" constraints — easy to accidentally violate, hard to discover from code alone.

- **BitwiseCopyable in Swift 6+**: `storeBytes(of:as:)` requires it. Classes: `Unmanaged.passRetained().toOpaque()`. Structs/enums: `initializeMemory(as:repeating:count:)`.
- **ABI nested type naming**: ObjC enums in ABI JSON use nested forms (`AVCaptureSession.Preset`), NOT flattened ObjC names.
- **ModuleEmissionContext threading**: ALL code paths creating `WrapperEmitter` in `MethodHandler` MUST pass `context.GetEmissionContext()` to avoid dedup failures. Also applies to `Utf8SliceEmitter.EmitIfNeeded`/`EmitFreeIfNeeded`.
- **Tj dispatch thunks**: Non-final class instance methods need `Tj` suffix. Gates: `!classParent.IsFinal && !methodDecl.IsFinal`.
- **Bool P/Invoke**: All `== "bool"` comparisons use `MarshallingHelpers.IsBoolType()`. Parameter-level: `[MarshalAs(UnmanagedType.U1)]`.
- **Overload/dedup key consistency**: `DefaultParameterOverloadEmitter.GetProjectedOverloadKey` must match `IHandler.GetProjectedCSharpMethodKey` exactly. ~21 call sites across 15 files.
- **C# `@` verbatim identifiers**: `@` at START is valid, AFTER other chars is INVALID. Compound variable names need `StripVerbatimPrefix` before prepending.
- **WasEmitted flag**: Set at 13 emission points across 6 files (MethodHandler x7, PropertyHandler x2, NestedClosureBridge x1, ProtocolExtensionClosureBridge x1, MethodClosureBridge x1, GenericClosureBridgeEmitter x1). Required by `HasMethodInResolvedAncestors`/`HasPropertyInResolvedAncestors`.
- **ProtocolExtensionEmitter pipeline timing**: MUST happen AFTER `typeDatabase.AddModuleDatabase()` and BEFORE `stringEmitter.EmitModule()`.
- **Generic protocol extension ABI**: TWO TypeMetadata per generic param (explicit T.Type + implicit trailing). `IsProtocolExtensionMethod` controls P/Invoke param ordering (self_ first). PInvokeHelperContext metadata MUST be suppressed for protocol extension methods.
- **Swift iterator Arc.Retain**: Non-mutating P/Invoke returning reference to argument needs `Arc.Retain` before the call.
- **Projection parity pattern**: When adding a new `ITypeProjection`, implement `Visit()` methods on `AccessorGetterConversionVisitor`, `AccessorSetterConversionVisitor`, and `OptionalAccessorGetterVisitor` (compile-time exhaustive via `IProjectionVisitor<T>`). Also implement `GetReturnPlan()`/`GetParameterPlan()` on the projection. PropertyHandler and SubscriptHandler are safe (visitor pattern). ProtocolProxyEmitter.Receivers still uses switch with `_ => null` fallback — check manually.
- **Mixed composition existential guard**: ObjC filtering can drop protocols -> size mismatch. Guard `filteredCount == originalCount` in 5 locations.
- **Closure two-layer gate**: Layer 1 (`IsSupportedClosureParameterType`) decides if method emits. Layer 2 (`IsCdeclCompatibleType`) decides if `@_cdecl` wrapper is generated. `.All()` not `.Any()`.
- **WitnessDispatchEmitter branch order**: String FIRST (Swift.String is a frozen+RefFields struct). Property dispatch must check `IsTypeBlittable || IsStringType` directly (NOT `IsPropertyGetterDispatchable`).
- **IReadOnlyDictionary invariance**: Element conversions in containers need explicit cast `(IProtocol)new ProtocolProxy(v)` -- unlike covariant `IReadOnlyList<T>`.
- **GetPublicMethodName parameterCount**: `parameterCount = 0` controls "Get" prefix. ~21 call sites across 15 files must pass consistent param count.
- **nint->int narrowing safety**: Properties ARE narrowed. Method return types are NOT narrowed (C# overload resolution prefers `int` overloads -> silent 64-bit truncation). Protocol receiver getters widen int->(nint) for 8-byte ABI; setters narrow.
- **Cross-module proxy class qualification**: Use `GetQualifiedProxyClassName()` (not `GetProxyClassName()`) when emitting marshalling code. ALL `ProjectionContext` creations must include `CurrentModuleName`. Proxy ExistentialContainer constructor is `public` for cross-assembly access.
- **IsOptionalObjCBridged parity with TypeProjectionFactory**: Must match exactly. ObjCRooted does NOT use IntPtr (uses SwiftOptional<T>). Both fallbacks use `AppleFrameworkRegistry.IsOptionalFallbackModule` + `!IsNestedType` + `!IsKnownAppleValueType` + `HasObjCClassPrefix` — must stay in sync.
- **AppleFrameworkRegistry is the single source of truth** for Apple framework heuristics: module sets (AutoBridge vs OptionalFallback), value type exclusions, ObjC prefix detection, type name remapping, pointer/nested type detection. Data loaded from `apple-frameworks.json` at startup. `TypeDatabaseExtensions.IsObjCModuleType` delegates to it.
- **XML value-type remap entries must use kind="struct"**: Types must be `kind="struct"` (NOT enum) in XML databases. Using `kind="enum"` causes the generator to emit enum member references that don't exist. Only entries that are genuine ObjC enums with `rawValueType` should be `kind="enum"`.
- **NSUnderlineStyle excluded from XML intentionally**: Adding `UIKit.NSUnderlineStyle` to UIKitDatabase.xml makes it resolvable on the SwiftTypeName path, causing tuple P/Invoke raw type mismatch. Enforced via `excludeFromXml` in `apple-frameworks.json`. Keep in `AppleFrameworkRegistry.ValueTypes` and `TypeNameRemaps` only.
- **SwiftUI two-path suppression**: SwiftUI types gated at Path A (TypeDatabaseExtensions) and Path B (MemberEmissionValidator). When adding new SwiftUI stubs: (1) create ISwiftObject stub in Swift.Runtime, (2) register in SwiftUIDatabase.xml.
- **Validation cache invalidation**: `validate-libraries.sh` caches in `/tmp/binding-validation/`. Use `rm -rf /tmp/binding-validation` before full validation when generator source has changed.
- **Conditional extension constraint gates**: Consolidated in `MemberValidationPipeline` Phase 4 for methods via HandleBaseDecl. PropertyHandler retains accessor-level `HasUnsupportedProtocolConstraints` for property accessors not covered by the pipeline. `ShouldSkipConstraint` exists only in `BoundGenericsHandler`.
- **ConformanceGraph**: `IsParentSelfReference` guard -- only depth-0 generic params (`t_0_X`) resolve via graph, depth-1+ skip. `CreateTypeSpec` DependentMember fix: `node.Name == "DependentMember"` inside `kNominal` case produces `AssociatedTypeReferenceSpec`.
- **Complex enum closure heap allocation**: Must use `UnsafeMutableRawPointer.allocate` + `initializeMemory` (NOT `withUnsafePointer` -- stack pointer incompatible with `NativeMemory.Free`).
- **Protocol extension param gate lifts**: Throwing regex `(?<!\bre)throws(?!\s*\()` excludes rethrows/typed-throws. PAT and Self-requirement protocols ARE rejected for existential params. Optional recursion hardened to reject `Optional<BoundGeneric>`.
- **Throwing closure simplification**: `ThrowingClosureSimplificationEmitter` via `IMethodPostProcessor`. Gates: skip constructors, accessors, async, missing symbols, method-level generics. Dedup via `EmittedProjectedSignatures`.
- **Underscore-prefix suppression**: Structurally required `_`-prefixed types (superclass/protocol of non-`_` type) are NOT suppressed. Uses module-qualified names (`SwiftTypeName.ToString()`).
- **Closure return-marshalling parity**: `BuildCallbackReturnStatement()` in `ClosureEmitter.cs` is the single source of truth for callback return type conversion. Both `EmitEscapingClosureCallback` and `EmitThrowingClosureCallback` delegate to it.
