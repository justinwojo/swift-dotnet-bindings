# SwiftUI Bridge v2: Phases 1-2 (Completed)

Archived from `src/docs/Future/swiftui-bridge-v2-plan.md`. Phases 1A-1D and 2A-2C completed 2026-02-06.

**Final state**: 1419 unit tests, 35/35 BridgeParamTest, 16/16 BlinkIDUX, 15/15 Lottie.

---

## Phase 1: Parameter Type Expansion

Expanded `InitAnalyzer.MapParameterType()` to support enums, `Optional<T>`, typed closures, and class parameters via TypeDatabase.

### Prerequisite: Thread ITypeDatabase into Bridge Emitter

`ITypeDatabase` threaded from `ModuleEmitter` → `EmitBridgeFiles()` → `BridgeContext` → `AnalyzeInitParameters()`.

### Phase 1A: BoundEnum + Optional<Primitive|Enum>

Enums cross the ABI as raw integer values. Optionals of primitives/enums use a hasValue flag + raw value.

**New BridgeParameterKind values:**
- `BoundEnum` — enum from TypeDatabase (pass raw Int value across ABI)
- `OptionalWrapped` — Optional<T> where T is Primitive or BoundEnum only (v2.0)

**C ABI Mapping:**

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `MyEnum` (Int raw) | BoundEnum | `Int32` | `int` | `MyEnum` |
| `Optional<Int>` | OptionalWrapped | `Int32` (hasValue) + `nint` (value) | `int, nint` | `nint?` |
| `Optional<MyEnum>` | OptionalWrapped | `Int32` (hasValue) + `Int32` (rawValue) | `int, int` | `MyEnum?` |

**Implementation:**
- `BridgeContext` record holds `ITypeDatabase?` for type lookups
- `BridgeParameter` record extended with `BridgeTypeName`, `CSharpTypeName`, `InnerParameter`
- `TypeRecord.RawValueTypeName` added — populated from `EnumDecl.RawValueTypeName` in `ModuleProcessor`
- `MapEnumRawValueType()` supports all 10 Swift integer types; String/non-RawRepresentable → template fallback
- C# call-site casts use mapped `CSharpPInvokeType` (not hardcoded `int`)
- 25 new unit tests; runtime-validated via BridgeParamTest (35/35)

### Phase 1B: BoundType for Classes

Class parameters cross the ABI as `UnsafeMutableRawPointer`. The session retains the pointer in Create and releases in Free.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `MyClass` | BoundType | `UnsafeMutableRawPointer` | `IntPtr` | `MyClass` |

**Retain/release contract**: Session takes `takeUnretainedValue()` from opaque pointer. C# factory extracts handle via `obj.Payload.DangerousGetHandle()`. Session release handled by existing `Unmanaged.passRetained(session).release()` in Free.

**Implementation:**
- `BridgeParameterKind.BoundType` added — maps class types from TypeDatabase
- `MapDatabaseType()` handles `TypeRecordKind.Class` → `BoundType`; `TypeRecordKind.Struct` → null (deferred)
- Swift: `UnsafeMutableRawPointer` param → `Unmanaged<ClassName>.fromOpaque().takeUnretainedValue()` reconstruction
- C#: `IntPtr` P/Invoke, typed factory param, `Payload.DangerousGetHandle()` call-site
- `Optional<BoundType>` also implemented (Phase 1D): nullable pointer (`UnsafeMutableRawPointer?` / `IntPtr.Zero` = nil)
- 14 new unit tests; runtime-validated via BridgeParamTest (35/35)

### Phase 1C: TypedClosure

Closures with typed parameters and/or return values. Max 4 closure parameters.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `(T) -> Void` | TypedClosure | `@convention(c) (T_abi, UnsafeMutableRawPointer?) -> Void` | `IntPtr, IntPtr` | `Action<T>` |
| `(T) -> R` | TypedClosure | `@convention(c) (T_abi, UnsafeMutableRawPointer?) -> R_abi` | `IntPtr, IntPtr` | `Func<T, R>` |
| `(T, U) -> Void` | TypedClosure | `@convention(c) (T_abi, U_abi, UnsafeMutableRawPointer?) -> Void` | `IntPtr, IntPtr` | `Action<T, U>` |

Each typed closure generates a C# `[UnmanagedCallersOnly]` trampoline that unpacks `GCHandle → delegate`, converts args, calls delegate.

**Callback threading semantics**: `VoidClosure` wraps the callback in `DispatchQueue.main.async`. `TypedClosure` invokes **synchronously on the calling thread** (required for return values).

**Implementation:**
- `BridgeParameterKind.TypedClosure` added
- `BridgeParameter` extended with `ClosureArguments` (list) and `ClosureReturn` (optional)
- `MapClosureType()` recursively maps each closure arg and return type via `MapPrimitiveOrString`
- Supported closure arg/return types: Primitives (Int, Int32, Int64, Bool, Double, Float)
- Unsupported: String args, async, throwing, >4 params → template fallback
- Swift: `@convention(c)` wrapper with typed ABI params + `UnsafeMutableRawPointer?` userData
- C#: `[UnmanagedCallersOnly]` trampoline with ABI-typed params, GCHandle→delegate cast, arg conversion
- C#: Factory param uses `Action<T...>?` or `Func<T..., R>?` with `= null` default
- Bool ↔ Int32 conversion in both Swift and C#
- 26 new unit tests; runtime-validated via BridgeParamTest (35/35)

### Phase 1D: Optional<BoundType> for Reference Types

Shipped with Phase 1B. Nullable pointer pattern — null pointer = nil. No flag needed.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `Optional<MyClass>` | OptionalWrapped | `UnsafeMutableRawPointer?` | `IntPtr` | `MyClass?` |

### MapParameterType Final Logic

```
1. ClosureTypeSpec:
   a. () -> Void → VoidClosure (existing)
   b. !async && !throws && ≤4 params → recursively map → TypedClosure
   c. async/throwing → null

2. NamedTypeSpec:
   a. Existing primitives → Primitive
   b. Swift.String → String
   c. Swift.Optional with 1 generic param → recursively map inner → OptionalWrapped
   d. TypeDatabase lookup: Enum → BoundEnum, Class → BoundType, Struct → null

3. Everything else → null (template fallback)
```

### Files Modified

| File | Change |
|------|--------|
| `ModuleEmitter.cs` | Pass `_typeDatabase` to bridge emitter |
| `SwiftUIBridgeEmitter.cs` | Accept `ITypeDatabase`, add `BridgeContext`, new Swift/C# emission for each Kind |
| `SwiftUIBridgeEmitter.InitAnalyzer.cs` | Expand `MapParameterType`, new `BridgeParameterKind` values, TypeDatabase lookup |
| `SwiftUIBridgeEmitterTests.cs` | ~65 tests for all parameter kinds |

---

## Phase 2: Generalized Async Factory

Replaced hard-coded `KnownAsyncPatterns` dictionary with ABI-driven inference.

### Phase 2A: ABI-Driven Async Inference + Constructor Ranking

**Constructor/Factory Selection Rules:**
1. Hints override (Phase 3, future)
2. Hard-coded `KnownAsyncPatterns` (backward compat)
3. Inferred: fewest bridgeable params → shallowest async depth → ABI order
4. Template fallback

**Dependency Chain Flattening:**
1. Start from View's init. For each non-primitive param, look up its type's init.
2. If all params are supported, flatten into Create params.
3. Recurse (max depth 3). Unsupported leaf or depth >3 → template.
4. `async throws` inits wrapped in `Task { @MainActor in ... }` with onReady/onError callbacks.

**Precedence Order:**
1. Bridge hints `skip`/`forceTemplate`
2. Bridge hints `asyncPattern`
3. `KnownAsyncPatterns` dictionary
4. ABI-driven inference
5. Simple classification
6. Template fallback

### Phase 2B: Data-Driven Emission from Inferred Chains

- `ConstructionChain == null` → legacy hard-coded emission via `EmitLegacy*` methods
- `ConstructionChain != null` → data-driven emission via `EmitDataDriven*` methods
- View built in Create scope (not session init) — fixes mixed chain + leaf param views
- Session receives pre-built `UIHostingController` + chain outputs (for ARC retention)
- `BuildViewInitArgsFromChain()` applies `FormatFlatParamSwiftValue()` for Bool leaf params

### Phase 2C: Cross-Module Type Resolution + Null-Safety

- Cross-module types: `ResolveModuleType` returns null → falls to `MapParameterType` → TypeDB resolves as BoundType/BoundEnum leaf
- `BridgeParamToFlatParam()` handles BoundType (`UnsafeMutableRawPointer`/`IntPtr`) and BoundEnum (rawValue)
- BoundType Swift ABI: `{name}Ptr: UnsafeMutableRawPointer` + null guard + `Unmanaged<T>.fromOpaque(ptr).takeUnretainedValue()`
- BoundEnum Swift ABI: raw value type + `TypeName(rawValue: val)!` conversion
- `ExtractSwiftModule()` gets module prefix from NamedTypeSpec; populates `AsyncFlatParam.SourceModule`
- ExtraSwiftImports auto-populated from cross-module flatParam source modules
- P1 null-safety: Swift null-pointer guard before Unmanaged cast (error callback) + C# ArgumentNullException before P/Invoke

### Files Modified

| File | Change |
|------|--------|
| `SwiftUIBridgeEmitter.cs` | Accept `ModuleDecl`, precedence comment block, data-driven emission |
| `SwiftUIBridgeEmitter.AsyncPattern.cs` | `InferAsyncViewPattern()`, constructor ranking, chain flattening, cross-module resolution |
| `SwiftUIBridgeEmitter.InitAnalyzer.cs` | Async dependency detection via parameter type init inspection |
| `ModuleEmitter.cs` | Pass `moduleDecl` to bridge emitter |
| `SwiftUIBridgeEmitterTests.cs` | ~28 tests (7 data-driven + 15 async detection + 6 cross-module) |

### Acceptance Criteria — All Met

- `KnownAsyncPatterns` entries take precedence over inference (backward compat) ✅
- Auto-inferred async factory from `init(model: MyModel)` where `MyModel.init(key: String)` is `async throws` ✅
- 3-level chain generates correctly; 4-level falls to template ✅
- Constructor selection is deterministic ✅
- BlinkIDUXView still works (kept in dictionary) ✅
- Runtime-validated: BridgeParamTest 35/35 (MixedAsyncView data-driven) ✅
- Cross-module types resolved via TypeDatabase with auto ExtraSwiftImports ✅
- Null-safety: Swift null-pointer guard + C# ArgumentNullException ✅
- Lottie 15/15 SwiftUI bridge tests ✅

### Risk Mitigations

- `swiftc -typecheck` compilation gate in `regenerate-bindings.sh` prevents incorrect Swift
- 6 cross-module + 7 data-driven emission unit tests
- No incorrect Swift produced during validation

---

## Runtime Validation Summary

| Test Suite | Count | Status |
|------------|-------|--------|
| Unit tests | 1419 | All pass |
| BridgeParamTest | 35/35 | All param kinds + async data-driven |
| BlinkIDUX | 16/16 | NoInternetView + scanning session |
| Lottie | 15/15 | SwiftUI bridge + core |

### BridgeParamTest Parameter Kinds Validated

- BoundEnum (`EnumParamView`) — create + value round-trip + GetViewController
- BoundType (`ClassParamView`) — create + value round-trip + retain/release lifetime
- TypedClosure (`TypedClosureView`) — `(Int32) -> Bool` closure round-trip via generated wrapper
- MultiArgClosure (`MultiArgClosureView`) — `(Int32, Bool) -> Void` multi-param closure
- MixedParam (`MixedParamView`) — enum + void closure + primitive coexistence + callback round-trip
- Optional\<Enum\> (`OptionalEnumView`) — with-value + nil variants
- Optional\<Class\> (`OptionalClassView`) — with-value + nil variants
- MixedAsyncView — data-driven async emission with cross-module types
- Cleanup — sessions disposed, `ObjectDisposedException` verified
