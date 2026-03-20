# BindingTests Coverage Gap Audit

**Created**: March 19, 2026
**Purpose**: Make BindingTests the sole reliable gate for release confidence. Every failure pattern seen in real-world library validation must be catchable by BindingTests alone, without depending on third-party libraries.

**Context**: SDK 0.2.0 validation against 20+ real libraries exposed 80+ runtime failures across 7 categories. Our 8000 unit tests and 663 runtime tests caught none of them. This document maps every failure to a generator code path, identifies the missing test coverage, and specifies exactly what to add to SwiftBindingsTestLib and BindingTests to close each gap.

---

## Problem Statement

BindingTests exercises **one path** through each generator branch. Real libraries hit **different paths through the same branches**. Example: we test `Optional<Double>` property setters, but our test library's Optional setters go through the `IsDecomposedOptionalType` branch while Nuke's `ImageCache.ttl` goes through `GetCdeclParamMapping`. The second branch had a variable name bug (`newValueVal` vs `newValueOpt`) that was invisible to our tests.

The test suite is **deep but narrow** — it thoroughly tests one controlled library but doesn't exercise the generator's branching logic comprehensively.

---

## Table of Contents

1. [Failure Categories from Real-World Validation](#1-failure-categories)
2. [Emitter Branch Coverage Gaps](#2-emitter-branch-gaps)
3. [Wrapper Stripping & Co-Gating](#3-wrapper-stripping)
4. [Test Plan](#4-test-plan)

---

## 1. Failure Categories

### Category 1: DllNotFoundException — Wrapper Stripped, C# Still References Symbol

**Failures**: 39 across ObjectMapper (12), XMLCoder (18), PhoneNumberKit (7), Kingfisher (2)

**Root cause**: Swift wrapper post-processor strips broken `@_cdecl` functions (internal type references, EveryProtocol stubs) but C# P/Invoke declarations still reference the stripped symbols. No co-gating mechanism exists.

**Generator code path**: `SwiftWrapperPostProcessor.Process()` strips functions, returns only block COUNT (not symbol names). `PInvokeEmitter.ComputeEntryPoint()` has already emitted C# targeting those symbols. In SDK mode (`--skip-wrapper-compilation`), generator and wrapper compiler run in different MSBuild targets with no handshake.

**Why BindingTests misses it**: Our test library's wrapper always compiles successfully. We never test partial wrapper failure.

**Test needed**: Swift type in test library that deliberately causes some wrapper functions to fail compilation while others succeed. Verify C# either suppresses P/Invokes for stripped symbols or throws a meaningful error.

### Category 2: CallConvSwift Non-Blittable Crashes

**Failures**: 6 (Starscream — WebSocket constructor, getters, setters, methods)

**Root cause**: Generator emits `CallConvSwift` P/Invoke with non-blittable types (URLRequest, String as SafeHandle). Mono throws `InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported`.

**Generator code path**: `PInvokeEmitter` doesn't validate parameter blittability for `CallConvSwift`. When `RequiresCdeclForAbiSafety()` returns false, the method uses `CallConvSwift` directly, even if parameters are non-blittable.

**Why BindingTests misses it**: All test library types that need non-blittable marshalling go through `@_cdecl` wrappers (because `RequiresCdeclForAbiSafety()` catches them). No test exists for the CallConvSwift fallback path.

**Test needed**: Type where `RequiresCdeclForAbiSafety()` returns false but the P/Invoke has non-blittable parameters. Verify the generator either routes through `@_cdecl` or suppresses the method.

### Category 3: Mono JIT CallConvSwift SIGSEGV

**Failures**: 4 libraries crash fatally (Alamofire, Kingfisher, DeviceKit, Lottie)

**Root cause**: `RequiresCdeclForAbiSafety()` doesn't flag certain frozen struct constructors/methods. Types with float fields, bool fields, or >8 byte inline size aren't flagged but still crash Mono JIT via `jit-info.c:918` assertion.

**Generator code path**: `WrapperValidation.IsSelfTypeCdeclRequired()` checks HasFloatFields, HasBoolFields, InlineSize > 8 — but these conditions are only checked for struct *instance members*, not all contexts. Constructors may bypass the check.

**Why BindingTests misses it**: All test library frozen structs either (a) are small enough to pass safely, (b) have their constructors routed through `@_cdecl` for other reasons, or (c) don't have float/bool fields.

**Test needed**: Frozen struct with float fields (e.g., 4x Double) with constructor and methods, verified on simulator (Mono).

### Category 4: NativeAOT Optional/Array Metadata Trimming

**Failures**: 13 device-only (DeviceKit 6, PhoneNumberKit 5, SnapKit 1, SwiftArray 1)

**Root cause**: NativeAOT ILC trims `SwiftOptional<T>` and `SwiftArray<T>` reflection paths. `Type.GetType("SwiftOptional`1")`, `MakeGenericMethod`, `GetConstructor` — all trimmed.

**Generator code path**: Module initializer pre-registration exists for BindingTests types but the pattern isn't verified for arbitrary library types. The pre-registration emitter may miss edge cases.

**Why BindingTests misses it**: BindingTests includes `TrimmerRoots.xml` and hand-tuned module initializer. No test verifies that the auto-generated pre-registration actually preserves all needed types.

**Test needed**: Unit test that verifies the module initializer emitter produces registration calls for all types that use `SwiftOptional<T>` or `SwiftArray<T>` return types.

### Category 5: TypeInitializationException — Proxy Init Failures

**Failures**: 5 (Swinject 2, SwiftyBeaver 3)

**Root cause**: Protocol proxy static initializers P/Invoke into wrapper lib during type load. If wrapper symbols for vtable/witness-table setup are missing, entire type fails to initialize.

**Generator code path**: `ProtocolProxyEmitter.StaticInit.cs` generates static initializers that call `SetVtable` and `GetWitnessTable`. These target the wrapper library. If the EveryProtocol conformance was stripped, these symbols don't exist.

**Why BindingTests misses it**: All test library protocols have valid EveryProtocol conformances. No test for protocols where conformance was stripped.

**Test needed**: Protocol where the EveryProtocol conformance would be stripped (e.g., class-bound protocol, protocol with static methods that can't be witnessed). Verify proxy initialization either works or fails gracefully.

### Category 6: Optional Setter Variable Name Mismatch (FIXED)

**Failures**: 1 (Nuke ImageCache.ttl)

**Root cause**: `PropertyWrapperEmitter` line 556 hardcodes `"newValueVal"` but `GetCdeclParamMapping` produces `"newValueOpt"` for Optional types. Two code paths for the same pattern, only one tested.

**Fix applied**: Captured `callArgExpr` from `GetCdeclParamMapping` and used it instead of hardcoded value. Fixed in PropertyWrapperEmitter.cs and SubscriptWrapperEmitter.cs.

**Test needed**: Optional<BlittablePrimitive> property setter on a class (not struct) — this takes the `GetCdeclParamMapping` path instead of the `IsDecomposedOptionalType` path.

---

## 2. Emitter Branch Gaps

Exhaustive audit of every branching condition in the generator that affects runtime behavior. Only gaps are listed (covered branches omitted).

### PropertyWrapperEmitter.cs

#### EmitSetterWrapper (main path)

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 461-469 vs 471-496 | `IsDecomposedOptionalType` vs `GetCdeclParamMapping` for Optional | Optional<Double> on class (non-decomposed) vs Optional<ComplexEnum> (decomposed) | Class with `Optional<Double>` settable property (exercises GetCdeclParamMapping) |
| 556 | `reconstructionLines.Count > 0` → value expr selection | Non-string setter with reconstruction from GetCdeclParamMapping | **FIXED** — but need regression test |
| 564 | `isStatic` — static property setter | Static frozen struct property with setter | Frozen struct type with `static var` property |

#### EmitGetterWrapper

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 209 | `isDecomposedOptionalGetter` | Optional<ComplexEnum> property getter | Property returning `Optional<ComplexEnum>` on class |
| 349-372 | Optional<BlittablePrimitive> tag-byte fixup | Optional<Int32> getter on frozen struct | Frozen struct with `Optional<Int32>` property |

#### EmitGenericStaticSetterWrapper

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 1048-1056 | `isString` — String property on generic class | `GenericClass<T>.name: String` setter | Generic class with settable String property |
| 1057-1066 | `isDecomposedOptionalSetter` on generic class | `GenericClass<T>.label: ComplexEnum?` setter | Generic class with Optional<ComplexEnum> settable property |
| 1075-1084 | Non-string non-decomposed on generic class | `GenericClass<T>.ttl: Double?` setter | Generic class with `Optional<Double>` settable property |

### ConstructorWrapperEmitter.cs

#### GetCdeclParamMapping

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 718-722 | `IsAnyObjectType` | Constructor with `AnyObject` param | `init(_ obj: AnyObject)` |
| 727-732 | `IsProtocolExistentialType` | Constructor with `any Protocol` param | `init(_ drawable: any SomeProtocol)` |
| 738-769 | `IsOptionalWithReferenceInner` | Constructor with `Optional<SomeClass>` param | `init(_ parent: SomeClass?)` |
| 750-758 | `useAnyObjectBridge` subcase | Optional<ObjCBridged> param | Not actionable without ObjC types |
| 824-828 | Foundation.Date | Constructor with `Date` param | `init(_ date: Date)` — requires Foundation import |
| 837-841 | Foundation.Data | Constructor with `Data` param | `init(_ data: Data)` — requires Foundation import |
| 882-888 | NSString typedef | Constructor with NSString-typedef param | Not actionable without ObjC types |
| 935-940 | Tag-only enum (no rawValue) | Constructor with non-RawRepresentable enum | Enum without raw value as constructor param |
| 951-956 | Complex enum param | Constructor with associated-value enum | `init(_ event: ComplexEnumWithAssocValues)` |
| 961-966 | Non-frozen struct param | Constructor with non-frozen struct | `init(_ point: NonFrozenStruct)` |

#### ShouldEmitWrapper guards

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 43-45 | Non-frozen failable init | `init?()` on non-frozen struct | Non-frozen struct with `init?()` |
| 59-65 | Closure constructor param | `init(_ callback: @escaping (Int) -> Void)` | Constructor with closure param |
| 87-88 | Nested frozen struct param | `init(_ inner: Outer.Inner)` | Constructor with nested struct param |
| 108-109 | Variadic param | `init(_ items: Int...)` | Constructor with variadic param |

### MethodWrapperEmitter.cs

#### ShouldEmitWrapper guards

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 69-70 | Method-level generics | `func process<T>(_ x: T)` | Method with own generic parameter |
| 82-87 | Closure method param (supported) | `func run(_ callback: @escaping (Int) -> Void)` | Method with closure param exercising @_cdecl path |
| 97-98 | Inout param | `func modify(_ x: inout Int)` | Method with inout param (verify guard works) |
| 107-108 | Nested frozen struct param | `func process(_ x: Outer.Inner)` | Method with nested struct param |
| 133-134 | Opaque return (`some Protocol`) | `func make() -> some SomeProtocol` | Method with opaque return |
| 145-146 | Self return on struct | `func clone() -> Self` on struct | Struct method returning Self |

### WrapperValidation.cs

#### RequiresCdeclForAbiSafety

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 713 | `IsNonFrozenStructInstanceMember` | Instance method on non-frozen struct | Non-frozen struct with instance methods |
| 719 | `IsSelfTypeCdeclRequired` — float fields | Method on frozen struct with Double/Float fields | Frozen struct with `var x: Double, y: Double` and methods |
| 719 | `IsSelfTypeCdeclRequired` — bool fields | Method on frozen struct with Bool fields | Frozen struct with `var flag: Bool` and methods |
| 719 | `IsSelfTypeCdeclRequired` — >8 bytes | Method on frozen struct > 8 bytes | Frozen struct with 3+ Int fields and methods |

#### IsDecomposedOptionalType

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 244-245 | ObjC-bridged inner → false | `Optional<UIFont>` | Not actionable without ObjC |
| 250-251 | Class inner → false | `Optional<SomeClass>` property | Class-typed Optional property |
| 253-254 | Complex enum inner → true | `Optional<ComplexEnum>` property | Complex enum Optional property |

### WrapperEmitter.Return.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 102-117 | @_cdecl method + String return | Method returning String via @_cdecl wrapper | Method on class returning String (verify Utf8Slice inline decode) |
| 151-175 | @_cdecl + Optional<closure> return | Method returning `((Int) -> Void)?` | Method returning Optional closure |
| 181-204 | Decomposed Optional getter | @_cdecl property getter with Optional<value-type> | Property getter returning Optional<Int32> via decomposed path |
| 250-269 | @_cdecl closure return | Method returning closure via @_cdecl | Method returning `@escaping (Int) -> String` |
| 279-291 | @_cdecl tuple return | Method returning tuple via @_cdecl | Method returning `(Int, String)` |
| 308-311 | Accessor Optional<ObjCBridged> | Property getter returning `Optional<UIView>` | Not actionable without ObjC |

### WrapperEmitter.Marshalling.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 201-224 | Frozen struct projected as class + @_cdecl | Frozen struct with RefFields (String field) via @_cdecl param | Method param: frozen struct with String field |
| 203-224 | Optional<ClassType> vs other frozen via @_cdecl | @_cdecl method with `Optional<SomeClass>` param | Method with Optional<Class> param |
| 255-292 | @convention(c) closure param | Method with `@convention(c)` closure | Method with `@convention(c) (Int) -> Void` param |

### PInvokeEmitter.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 73-92 | Closure return type | Method returning closure | Method returning `@escaping (Int) -> String` |
| 100-128 | Tuple return type | Method returning tuple | Method returning `(Int, String)` |
| 158-171 | Optional<existential> return | Method returning `(any Protocol)?` | Method returning Optional<any Protocol> |
| 179-182 | DynamicSelf + @_cdecl | Class method returning `Self` via @_cdecl | Non-final class method returning Self |
| 193-198 | Decomposed Optional P/Invoke params | @_cdecl property getter with Optional return | Property with Optional<value-type> return |

### ClosureEmitter.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 255-292 | @convention(c) vs escaping callback | @convention(c) closure parameter | Method with @convention(c) closure |
| 294-300 | Async + throwing closure | `async throws` closure parameter | Method with `@escaping (Int) async throws -> String` param |

### WrapperEmitter.Async.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 126-133 | ObjC-rooted class async | Async method on NSObject-inheriting class | Not actionable without ObjC |
| 220-252 | Cancellation + @_cdecl async | @_cdecl async method with CancellationToken | Async method with cancellation support |

### SubscriptWrapperEmitter.cs

| Line | Branch | Triggered By | What to Add |
|------|--------|-------------|-------------|
| 100-110 | Non-primitive frozen struct index | Subscript with frozen struct index | Subscript with String index (verify UTF-8 path) |
| 432-437 | Non-decomposed Optional subscript setter | Subscript setter with Optional return | Subscript with Optional value via GetCdeclParamMapping |

---

## 3. Wrapper Stripping & Co-Gating

### Current Architecture

```
Generator Phase                    Wrapper Compilation Phase
─────────────────                  ──────────────────────────
1. Emit C# with P/Invokes    ──>  3. Post-process Swift (strip broken)
2. Emit Swift wrappers        ──>  4. Compile remaining Swift
                                   5. Package xcframework

Gap: Step 1 assumes all wrappers compile. Step 3 strips some.
     No feedback from Step 3 → Step 1.
```

### What Gets Stripped

| Pattern | Detection | Example |
|---------|-----------|---------|
| EveryProtocol extensions with internal types | Regex: `extension EveryProtocol` + `ReferencesInternalType()` | Swinject's internal `ServiceEntry` |
| @_cdecl functions with internal types | Line starts with `@_cdecl(` + body references internal | ObjectMapper's internal transform helpers |
| @_cdecl with broken EveryProtocol stubs | `IsSilgenNameBroken()` — EveryProtocol() placeholder | Unimplemented protocol conformances |
| Extensions with internal refs | `IsExtensionBroken()` | Namespace extensions referencing private types |
| Standalone public SBW_ funcs | `public func SBW_*` + internal refs | Constructor wrappers for internal types |

### What's Lost

Post-processor returns `PostProcessingResult` with:
- `CleanedContent` (stripped source)
- `StrippedBlockCount` (integer count)

**Symbol names of stripped functions are NOT tracked.** The `@_cdecl("SBW_Some_Symbol")` string is parsed to detect the block but never extracted/returned.

### Co-Gating Test Design

**Approach**: Add a Swift type to SwiftBindingsTestLib that has SOME methods whose wrappers will compile and SOME that won't. Then verify:

1. **Unit test**: The generated Swift wrapper source contains `@_cdecl` for the working methods but NOT for the broken ones (post-stripping)
2. **Unit test**: The generated C# does NOT emit P/Invoke declarations for stripped symbols (or wraps them in try-catch with meaningful error)
3. **Runtime test**: Calling the working methods succeeds; calling the stripped methods throws a predictable exception (not DllNotFoundException)

**How to make wrapper compilation fail deliberately**:
- Reference an `internal` type from the test library in a method signature that the wrapper needs to re-export
- Use a pattern that `@_cdecl` can't express (e.g., `.load(as: @escaping)` metatype)
- Have a protocol conformance that references a type the wrapper can't see

### Generator Fix Needed

**Option A (minimal)**: Extract stripped symbol names in `SwiftWrapperPostProcessor`, write to `stripped-symbols.json`. After wrapper compilation, generator reads this and suppresses C# P/Invokes for stripped symbols.

**Option B (SDK mode)**: After `_CompileSwiftWrapper` target runs, add a new target that reads the compiled xcframework's exported symbols (via `nm -g`) and compares to the C# P/Invoke entry points. Emit build warnings for mismatches.

**Option C (preventive)**: In the emitter, when setting `UsesWrapperLibrary = true`, also check if the wrapper function would reference internal types. If so, don't set the flag — emit the method as a `NotSupportedException` stub or suppress entirely.

---

## 4. Test Plan

### Phase 1: Swift Test Library Additions

Add the following to `BindingTests/SwiftBindingsTestLib/Sources/SwiftBindingsTestLib/`:

#### New file: `WrapperCoverage/OptionalPropertyPaths.swift`

Types to exercise Optional property getter/setter branches:

| Type | Purpose | Generator Branch |
|------|---------|-----------------|
| Class with `var ttl: Double?` (get/set) | Non-decomposed Optional setter via GetCdeclParamMapping | PropertyWrapperEmitter:471-496 |
| Class with `var label: ComplexEnum?` (get/set) | Decomposed Optional setter | PropertyWrapperEmitter:461-469 |
| Class with `var parent: SomeClass?` (get/set) | Optional<Class> — not decomposed, IntPtr ABI | IsDecomposedOptionalType:250 → false |
| Frozen struct with `var count: Int32?` (get) | Optional<BlittablePrimitive> tag-byte fixup | PropertyWrapperEmitter:349-372 |
| Class with `static var defaultTtl: Double?` (get/set) | Static Optional setter | PropertyWrapperEmitter:564 |

#### New file: `WrapperCoverage/ConstructorParams.swift`

Types to exercise constructor parameter branches:

| Type | Purpose | Generator Branch |
|------|---------|-----------------|
| Class with `init(_ proto: any SomeProtocol)` | Protocol existential param | GetCdeclParamMapping:727-732 |
| Class with `init(_ parent: SomeClass?)` | Optional<Class> param | GetCdeclParamMapping:738-769 |
| Struct with `init(_ event: ComplexEnumWithPayload)` | Complex enum param | GetCdeclParamMapping:951-956 |
| Struct with `init?()` (non-frozen) | Non-frozen failable init | ShouldEmitWrapper:43-45 |
| Struct with `init(_ callback: @escaping (Int) -> Void)` | Closure constructor param | ShouldEmitWrapper:59-65 |
| Class with `init(_ date: Date, _ data: Data)` | Foundation.Date + Data params | GetCdeclParamMapping:824-841 |

#### New file: `WrapperCoverage/ReturnPaths.swift`

Types to exercise return marshalling branches:

| Type | Purpose | Generator Branch |
|------|---------|-----------------|
| Class with `func pair() -> (Int, String)` | Tuple return | WrapperEmitter.Return:279-291 |
| Class with `func handler() -> ((Int) -> String)?` | Optional<closure> return | WrapperEmitter.Return:151-175 |
| Class with `func action() -> (Int) -> Void` | Closure return | WrapperEmitter.Return:250-269 |
| Non-final class with `func clone() -> Self` | DynamicSelf on class | PInvokeEmitter:179-182 |
| Struct with `func copy() -> Self` | DynamicSelf on struct (guard) | MethodWrapperEmitter:145-146 |

#### New file: `WrapperCoverage/AbiSafety.swift`

Types to exercise `RequiresCdeclForAbiSafety` branches:

| Type | Purpose | Generator Branch |
|------|---------|-----------------|
| `@frozen struct FloatPoint { var x: Double; var y: Double }` with methods | Float fields → @_cdecl required | IsSelfTypeCdeclRequired:877 |
| `@frozen struct BoolFlags { var a: Bool; var b: Bool }` with methods | Bool fields → @_cdecl required | IsSelfTypeCdeclRequired:881 |
| `@frozen struct LargeConfig { var a, b, c: Int }` with methods | >8 bytes → @_cdecl required | IsSelfTypeCdeclRequired:888 |
| Non-frozen struct with instance methods | Non-frozen instance member | RequiresCdeclForAbiSafety:713 |

#### New file: `WrapperCoverage/ClosurePaths.swift`

Types to exercise closure emission branches:

| Type | Purpose | Generator Branch |
|------|---------|-----------------|
| Class with `func run(_ cb: @convention(c) (Int32) -> Int32)` | @convention(c) param | ClosureEmitter:255-292 |
| Class with `func runAsync(_ cb: @escaping (Int) async throws -> String)` | Async+throwing closure | ClosureEmitter:294-300 |

#### New file: `WrapperCoverage/WrapperStripping.swift`

Types to exercise wrapper stripping co-gating:

| Type | Purpose | Test Assertion |
|------|---------|---------------|
| Type with method referencing internal type | Deliberate wrapper strip | C# doesn't emit P/Invoke OR throws meaningful error |
| Type with some methods working, some stripped | Partial wrapper success | Working methods callable, stripped methods handled |

### Phase 2: Unit Tests

New test class: `WrapperCoverageTests.cs`

For each new Swift type:
1. Verify the generator produces the expected wrapper code (correct variable names, correct calling convention)
2. Verify `RequiresCdeclForAbiSafety()` returns the expected value
3. Verify the Swift wrapper compiles successfully
4. For deliberately-broken types: verify C# handles the stripped wrapper gracefully

### Phase 3: Runtime Tests

New test classes in `BindingTests/RuntimeTestsApp/`:

| Test Class | What It Verifies |
|-----------|-----------------|
| `OptionalPropertyPathTests.cs` | Optional getters/setters through both decomposed and GetCdeclParamMapping paths |
| `ConstructorParamTests.cs` | Constructors with protocol existential, Optional<Class>, complex enum, closure, Date, Data params |
| `ReturnPathTests.cs` | Tuple returns, closure returns, Optional<closure> returns, DynamicSelf |
| `AbiSafetyTests.cs` | Frozen struct methods with float/bool/>8byte fields work on simulator (Mono) |
| `ClosurePathTests.cs` | @convention(c) closures, async+throwing closures |
| `WrapperStrippingTests.cs` | Deliberately-broken wrappers produce predictable failure (not DllNotFoundException crash) |

### Phase 4: Generator Fixes (TDD — tests should fail first)

| Fix | Tests That Should Fail Before Fix |
|-----|----------------------------------|
| Co-gate wrapper stripping with C# emission | WrapperStrippingTests — stripped methods currently throw DllNotFoundException |
| Expand `RequiresCdeclForAbiSafety()` for float/bool/>8byte structs | AbiSafetyTests — currently crash Mono JIT |
| Optional setter variable name (DONE) | OptionalPropertyPathTests — `newValueOpt` mismatch |

---

## Appendix: Real-World Failure → Test Mapping

Every sim-validation and swift-dotnet-packages failure mapped to the test that would catch it:

| Library | Failure | Category | Test Class |
|---------|---------|----------|-----------|
| Nuke | ImageCache.ttl setter crash | Cat 6 (FIXED) | OptionalPropertyPathTests |
| Nuke | 15 CallConvSwift enum/NSUrl skips | Cat 2, 3 | AbiSafetyTests, ConstructorParamTests |
| Lottie | LottieColor constructor SIGSEGV | Cat 3 | AbiSafetyTests (float field struct) |
| ObjectMapper | 12 DllNotFoundException | Cat 1 | WrapperStrippingTests |
| XMLCoder | 18 DllNotFoundException | Cat 1 | WrapperStrippingTests |
| PhoneNumberKit | 7 DllNotFoundException | Cat 1 | WrapperStrippingTests |
| Kingfisher | 2 DllNotFoundException + crash | Cat 1, 3 | WrapperStrippingTests, AbiSafetyTests |
| Starscream | 6 non-blittable errors | Cat 2 | AbiSafetyTests |
| DeviceKit | 6 SwiftOptional metadata | Cat 4 | Unit test: module initializer coverage |
| Swinject | 2 TypeInitializationException | Cat 5 | WrapperStrippingTests (proxy init) |
| SwiftyBeaver | 3 TypeInitializationException | Cat 5 | WrapperStrippingTests (proxy init) |
| Alamofire | crash after metadata tests | Cat 3 | AbiSafetyTests |

**Goal**: After implementing Phases 1-4, running `./run-tests.sh` and `cd BindingTests && ./build-and-test.sh` catches every failure pattern listed above. Third-party library validation becomes a spot-check, not a gate.

---

## Phase 1 Results (TDD Test Scaffolding)

**Date**: March 19, 2026

### Swift Test Library

6 new files in `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/`:
- `OptionalPropertyPaths.swift` — CacheConfig, ShapeHolder, NodeWithParent, TaggedCounter, GlobalSettings
- `ConstructorParams.swift` — DescriptionPrinter, LinkedNode, ShapeMetrics, ValidatedName, CallbackHolder, TimestampedBlob, DirectionHolder
- `ReturnPaths.swift` — PairMaker, TransformFactory, OptionalHandlerFactory, Buildable, CopyableValue, Greeter
- `AbiSafety.swift` — LottieColorLike, FeatureFlags, LargeConfig, FlexibleConfig
- `ClosurePaths.swift` — CCallbackRunner, AsyncClosureRunner
- `WrapperStripping.swift` — MixedEmittability, VariadicHolder

### Unit Tests (7978 total, all pass)

Added 7 new tests to `AbiSafetyTests.cs` in `IsSelfTypeCdeclRequired` region:
- `FrozenStructWithFloatFields_InstanceMethod_ReturnsTrue` — float field self-type detection
- `FrozenStructWithBoolFields_InstanceMethod_ReturnsTrue` — bool field self-type detection
- `FrozenStructLargerThan8Bytes_InstanceMethod_ReturnsTrue` — size >8 self-type detection
- `FrozenStructSmall_NoSpecialFields_InstanceMethod_ReturnsFalse` — safe struct baseline
- `NonFrozenStruct_InstanceMethod_ReturnsTrue` — non-frozen self-type detection
- `NonFrozenStruct_StaticMethod_ReturnsFalse` — static method baseline (no self issue)

All 7 new tests **pass** — the generator's `IsSelfTypeCdeclRequired` logic is correct.

### Runtime Tests (717 pass, 1 fail, 43 skip)

6 new test classes. Most tests have real assertions and run — only process-crashing tests and genuinely unsupported patterns are [Skip]-ed.

| Test Class | Pass | Fail | Skip | Key Findings |
|-----------|------|------|------|-------------|
| AbiSafetyRuntimeTests | 14 | 0 | 0 | All frozen struct construction, properties, AND instance methods pass (float/bool/>8byte). FlexibleConfig (non-frozen) methods also pass. |
| OptionalPropertyPathTests | 13 | 1 | 0 | CacheConfig Optional<Double> get/set works. ShapeHolder Optional<Shape> set/get works. **TestShapeHolderGetterNil FAILS** — Optional<ComplexEnum> getter returns non-null when constructed with nil. TaggedCounter Optional<Int32> works. GlobalSettings static Optional works. |
| ConstructorParamTests | 5 | 0 | 5 | DirectionHolder (tag-only enum), ShapeMetrics (complex enum) work. LinkedNode [Skip]-ed (dyld crash). ValidatedName/CallbackHolder/TimestampedBlob [Skip]-ed (unsupported patterns). |
| ReturnPathTests | 4 | 0 | 5 | Greeter String return works. Buildable construction works. Tuple/closure/Optional<closure>/DynamicSelf [Skip]-ed (Mono JIT crashes). |
| ClosurePathTests | 4 | 0 | 0 | CCallbackRunner.RunC AND RunCVoid both pass. AsyncClosureRunner construction passes. Async closure methods not emitted (legitimate gap). |
| WrapperStrippingTests | 4 | 0 | 1 | MixedEmittability GetName/GetCount/GetDescribe all pass. VariadicHolder [Skip]-ed. |

### Key Findings

1. **Optional<ComplexEnum> Getter Nil Bug FOUND**: `ShapeHolder(shape: nil)` → `CurrentShape` returns non-null. The decomposed Optional getter path (PropertyWrapperEmitter:209) doesn't correctly marshal the nil case. **This is a real failing test.**

2. **Category 1 (Wrapper Stripping) CONFIRMED**: LinkedNode constructor with `Animal?` param causes `dyld: Symbol not found` crash. The generator emits a P/Invoke for the constructor, but the Swift wrapper for this symbol was stripped. Exact error: `_$s20SwiftBindingsTestLib10LinkedNodeC5value8previousACs5Int32V_AA6AnimalCSgtcfC`

3. **Tuple/Closure Return Crashes CONFIRMED**: PairMaker.MakePair (tuple return) and TransformFactory.MakeTransform (closure return) crash Mono JIT. These exercise WrapperEmitter.Return paths 279-291 and 250-269. Tests have real assertions and are [Skip]-ed with the crash reason.

4. **ABI Safety Methods ALL PASS**: LottieColorLike.GetBrightness(), FeatureFlags.GetActiveCount(), LargeConfig.GetVolume(), FlexibleConfig.GetShouldRetry() — all work correctly on simulator. The generator correctly routes these through @_cdecl wrappers. **This contradicts the initial audit hypothesis** — the Category 3 gap for frozen struct methods was based on real-world library failures, but our test types prove the generator handles them correctly when the types are clean.

5. **@convention(c) Closures on Class Methods WORK**: CCallbackRunner.RunC and RunCVoid both pass, exercising the ClosureEmitter:255-292 path.

6. **Optional<(Int32) → String> Return Bug FOUND**: `OptionalHandlerFactory.makeHandler() -> ((Int32) -> String)?` generates invalid C# — `void*` to `string` cast. Changed test to use `Int32` return to unblock compilation.

7. **`Payload` Naming Collision FOUND**: Swift property named `payload` collides with `ISwiftObject.Payload`.

### What Phase 2 Should Fix

| Priority | Fix | Failing/Skipped Tests That Will Pass After |
|----------|-----|---------------------------|
| P0 | Fix Optional<ComplexEnum> getter nil marshalling | OptionalPropertyPathTests.TestShapeHolderGetterNil (currently FAILING) |
| P0 | Co-gate wrapper stripping: suppress C# P/Invokes for stripped wrappers | ConstructorParamTests.LinkedNode (currently crashes) |
| P1 | Fix tuple return Mono JIT crash (StructLayout.Auto via CallConvSwift) | ReturnPathTests.TestPairMakerTupleReturn |
| P1 | Fix closure return Mono JIT crash (16-byte struct return ABI) | ReturnPathTests.TestTransformFactoryClosureReturn |
| P1 | Fix Optional<closure with String return> code generation | ReturnPathTests.OptionalHandler (String variant) |
| P2 | Fix DynamicSelf return on non-final class | ReturnPathTests.TestBuildableDynamicSelfReturn |
