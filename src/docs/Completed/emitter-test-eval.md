# Emitter Test Evaluation & Audit

> Generated 2026-02-16 (revised 2026-02-16 — Sessions 1–4 complete). Comprehensive audit of all 67 files in `src/Swift.Bindings/src/Emitter/` against 2900+ unit tests.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Top-Level Emitters](#top-level-emitters)
3. [Module Emission](#module-emission)
4. [Type Handlers](#type-handlers)
   - [ClassHandler](#classhandler)
   - [FrozenStructHandler](#frozenstructhandler)
   - [NonFrozenStructHandler](#nonfrozenstructhandler)
   - [EnumHandler](#enumhandler)
5. [Member Handlers](#member-handlers)
   - [MethodHandler & MethodSignature](#methodhandler--methodsignature)
   - [PropertyHandler](#propertyhandler)
   - [OperatorHandler](#operatorhandler)
6. [Protocol Emission](#protocol-emission)
   - [ProtocolHandler](#protocolhandler)
   - [ProtocolProxyEmitter](#protocolproxyemitter)
   - [ProtocolSignatureHelper](#protocolsignaturehelper)
   - [EveryProtocolEmitter](#everyprotocolemitter)
   - [ProtocolConformanceValidator](#protocolconformancevalidator)
   - [WitnessDispatchEmitter](#witnessdispatchemitter)
7. [Closure Emission](#closure-emission)
8. [P/Invoke & Wrapper Emission](#pinvoke--wrapper-emission)
9. [Utility Emitters](#utility-emitters)
10. [Bugs Found](#bugs-found)
11. [Prioritized Missing Tests](#prioritized-missing-tests)
12. [Cross-Cutting Observations](#cross-cutting-observations)

---

## Executive Summary

### Coverage By Area

| Area | Files | Tested Methods | Untested Methods | Est. Coverage |
|------|-------|---------------|-----------------|---------------|
| Top-level emitters | 4 | High | Low | ~80% |
| Module emission | 3 | Medium | High (ModuleEmitter.cs = 0%) | ~45% |
| Type handlers (Class/Struct) | 3 | Factory only; Emit paths low | Many private Emit paths | ~20% |
| EnumHandler (6 files) | 7 | Output tests good | Marshalling/RawRepresentable | ~55% |
| Method/Property/Operator | 4 | Good for Method, weak for Property/Operator | Many branches | ~50% |
| Protocol emission | 6 | Excellent for Proxy; weak for Validator | FindProtocol, member matching | ~65% |
| Closure emission | 7 | CompletionHandler great; ClosureEmitter indirect-only | 17/22 public methods zero direct tests | ~25% |
| P/Invoke & Wrapper | 7 | PInvokeEmitter good; WrapperEmitter.Return weak | WrapperEmitter.Return sparse | ~40% |
| Utility emitters | 15 | Mixed (some excellent, some zero) | TypeHandlerHelpers = 0%, UnsupportedSwiftTypeSupport = 0% | ~40% |

### Key Findings

- **13 bugs/defects found** across the codebase (2 high, 5 medium, 6 low severity)
- **4 files with ZERO dedicated test coverage** that contain significant logic
- **Systemic issue**: Handler test files (ClassHandlerTests, FrozenStructHandlerTests, NonFrozenStructHandlerTests) test the **data model**, not the **emitter output** -- creating a false sense of coverage
- **Strongest areas**: ProtocolProxyEmitter (65 tests), WitnessDispatchEmitter (45 tests), CompletionHandlerDetector (20 tests), ArraySliceNormalizationEmitter (39 tests), CancellationTaskEmitter (32 tests)
- **Weakest areas**: ClosureEmitter (0 direct tests across 6 files), TypeHandlerHelpers (0 tests), UnsupportedSwiftTypeSupport (0 tests)

---

## Top-Level Emitters

### BindingProjectEmitter.cs (154 lines)

**Coverage**: 20+ tests across 4 test classes. All major properties and the `Emit` method are well-tested including dependency handling, wrapper references, version placeholders, and overwrite behavior.

**Test Quality**: Good organization. Tests use `Assert.Contains` on file content (not XML parsing), creating theoretical false-positive risk. Significant code duplication across 4 test classes (vs. `ConsumerTargetsTestHelper` shared pattern).

**Code Issues**:
- No XML escaping of module names/paths in emitted `.csproj`. Module name with `<` or `&` would produce malformed XML.
- `Directory.Exists` on wrapper path (line 52-53): silently omits wrapper references if directory doesn't exist yet.

**Missing Tests**:
- `DisableRuntimeMarshallingAttribute` emission (critical for `[LibraryImport]` migration)
- `NoWarn CS0169;CA1420` suppressions
- Relative path correctness (`Path.GetRelativePath` edge cases)
- Empty dependencies list vs null
- XML structural validity (parse as XML)

### ConsumerTargetsEmitter.cs (112 lines)

**Coverage**: 20+ tests across 5 well-organized regions. `SanitizeModuleName` has 3 direct tests. Interop mode cascade (Auto/Direct/Safe) fully tested.

**Test Quality**: Good. Version ordering Theory test (line 177-196) is particularly valuable. `ConsumerTargetsTestHelper` shared helper avoids duplication.

**Code Issues**:
- `SanitizeModuleName` doesn't handle `+`, `@`, `#` etc. A module named `C++Interop` would produce invalid MSBuild target name.

**Missing Tests**:
- `SanitizeModuleName` with spaces, multiple special chars, empty string
- File overwrite behavior
- XML structural validity

### DependencyManifestEmitter.cs (204 lines)

**Coverage**: 9 tests. Major paths covered including cycle fallback and name-mismatch override.

**Code Issues**:
- Lines 64-79: O(n*m) path comparison with `Path.GetFullPath` on each iteration (unguarded `ArgumentException` on invalid paths)
- Lines 120-138: Duplicate path comparison logic (DRY violation)
- Line 196: Unnecessary `.ToArray()` inside `JArray`

**Missing Tests**:
- Graph-building exception (outer catch path, not just cycle `InvalidOperationException`)
- Dependency that is both auto-detected AND manually specified
- `isObjCOnly` field in manifest JSON
- Multiple unresolved/overridden dependencies

### IEmitter.cs (15 lines)

No issues. Trivial interface definition. Tested indirectly via `StringEmitter` implementation.

---

## Module Emission

### ModuleEmitter.cs / StringEmitter (96 lines)

**Coverage**: **ZERO unit tests.** This is the public API entry point for the entire emission pipeline. Every test bypasses it by constructing `ModuleHandler` directly.

**Code Issues**:
- No verification that output directory exists (line 69-78)
- Logging uses string interpolation instead of structured placeholders
- Note: `SwiftUIBridgeCollector` itself is thread-safe (uses `lock(Sync)` for collect/read/reset), but other static emitter state (`CancellationTaskEmitter`, `Utf8SliceEmitter`, `EnumHandler._emittedWrapperSymbols`) is not

**Missing Tests (HIGH PRIORITY)**:
- `EmitModule` happy path (file I/O verification)
- `EmitModule` with SwiftUI views (bridge emission)
- Custom `NamespacePatternResolver` in output file naming
- `else` branch when no handler found

### ModuleHandler.cs (851 lines)

**Coverage**: Good for imports (8 tests), namespace (5 tests), framework resolver (8 tests), `IsMangledNameFromModule` (9 theory cases). Weak for composition proxies and private scan methods.

**Code Issues**:
- Bug: Wrapper class naming fallback (lines 132-135) -- `while` loop generates `"Functions2"`, `"Functions3"` but initial candidate is `"{moduleName}Functions"` (inconsistent naming)
- `ResolveCSharpTypeName` (line 773) allocates new `TypeConversionHandler` per recursive call
- Lines 686-849 duplicate type resolution logic from `ProtocolHandler`

**Missing Tests**:
- Composition proxy emission (~150 lines, zero tests)
- `ComputeNonThrowingOverrides` (zero tests)
- `ScanTypeSpecForImports` for TupleTypeSpec, ClosureTypeSpec, ProtocolListTypeSpec branches
- `ModuleHandlerFactory.Handles()` and `Construct()` isolation tests
- Module with no methods (no wrapper class emitted)

### HandlerFactory.cs (7 lines)

Trivial base class. No tests needed.

---

## Type Handlers

### ClassHandler

**Coverage**: `ClassHandlerTests.cs` (614 lines) tests **data model only** (factory pattern, declaration construction). `TypeHandlersOutputTests.cs` has 6 actual emission tests including actor `unownedExecutor` skipping.

**SYSTEMIC ISSUE**: Tests prove the model accepts data, NOT that the emitter processes it correctly. Property dedup tests manually reimplement detection logic rather than calling the handler.

**Code Issues**:
- Actor-specific code path (lines 116-138) now tested (`Emit_ClassHandler_Actor_SkipsUnownedExecutor`)
- `IsFinal` on `ClassDecl` is completely unused in ClassHandler
- `ReportCollector` static state not reset between tests

**Missing Tests**:
- ~~Actor class emission (`IsActor = true`, `unownedExecutor` skipping)~~ — **DONE** (Session 4)
- Equatable class body output (`Equals`/`GetHashCode`/operators)
- Hashable class `GetHashCode` output — new `_implementsHashable` field emits `SwiftHashable.GetHashCode(this)` vs `return 0`, no test for either path
- Dispose/Finalizer pattern — new `~ClassName()` with `SwiftDispose.FinalizerCleanup` + `GC.SuppressFinalize`, no test for finalizer emission
- `MarshalToSwift` output verification
- Private constructor output
- Generic class with `PInvokeHelperContext`

**Estimated Emit-path coverage**: ~28% (5 of ~20 paths, new Hashable/Finalizer paths added)

### FrozenStructHandler

**Coverage**: `FrozenStructHandlerTests.cs` (586 lines) again tests **data model only**. `TypeHandlersOutputTests.cs` has 5 emission tests including stored property field emission (value-type and ref-type).

**Code Issues**:
- **BUG (line 73)**: Wrong `nameof` in exception: `nameof(structDecl.ParentDecl)` should be `nameof(structDecl.ModuleDecl)`
- Line 149-157: Dereferences `ValueWitnessTable->Size` pointer with no null check on `ValueWitnessTable`
- Line 200: Asymmetric indent management (`csWriter.Indent -= 2` without matching increment)

**Missing Tests**:
- ~~Struct with stored properties (value type and reference type)~~ — **DONE** (Session 4)
- Operator emission output
- Equatable frozen struct
- Hashable frozen struct — new `_implementsHashable` path in `EqualityMethodsWriter`, untested
- Dispose/Finalizer pattern for class-projected frozen structs — new finalizer emission, untested
- Generic frozen struct
- `StructLayout` attribute with runtime metadata

**Estimated Emit-path coverage**: ~19% (3 of ~18 paths, new Hashable/Finalizer paths added)

### NonFrozenStructHandler

**Coverage**: 1 emission test in `TypeHandlersOutputTests.cs`. `NonFrozenStructHandlerTests.cs` tests model only.

**Code Issues**:
- Line 153: Inconsistent log message says "field" instead of "property" (other handlers say "property")
- Line 178: `isReferenceType: true` hardcoded with no explanatory comment

**Missing Tests**: Operators, Equatable, Hashable, Dispose/Finalizer, generics, methods, nested types, protocol conformance -- all untested.

**Estimated Emit-path coverage**: ~7% (1 of ~16 paths, new Hashable/Finalizer paths added)

### EnumHandler

**Coverage**: `EnumHandlerOutputTests.cs` (1000+ lines) provides the best type-handler test coverage. Tests cover: simple enum (C# enum emission), complex enum (class with tag), case construction with associated values, case inspection with pattern matching, RawRepresentable (Int, String), protocol conformance interfaces, and tuple-in-case-associated-values. String-raw-value enums now route through `EmitSimpleEnum` (C# enum) with `ToRawValue`/`FromRawValue` extensions — tests updated to reflect this.

**Code Issues**:
- String enum raw values use case names (known limitation — ABI JSON lacks individual case raw values). New `EmitStringRawValueExtensions` emits pure-C# `ToRawValue`/`FromRawValue` using case names.
- `ResetUtf8SliceTracking()` calls in tests highlight fragile static state management
- `GetCSharpEnumUnderlyingType` and other marshalling helpers have zero direct tests
- Dispose/Finalizer pattern added to complex enum emission (same as ClassHandler) — untested

**Missing Tests**:
- `EnumISwiftObjectMethodWriter` -- zero tests (`WriteGetTypeMetadata`, `WriteNewFromPayload`, `WriteMarshalToSwift`)
- `EnumHandler.Marshalling` -- `EmitPayloadMarshal`, `EmitTupleMetadataAccessor` zero direct tests
- `EmitStringRawValueExtensions` — new method, no direct test (indirectly covered by updated `Emit_StringRawRepresentableEnum_EmitsCSharpEnum` and `Emit_FrozenStringEnum_WithRawValueConversions`)
- Dispose/Finalizer emission for complex enums
- Non-frozen complex enum
- Generic enum
- Enum with both payload and no-payload cases mixed
- Case construction with class-type associated values (SYSLIB1051 regression area)

---

## Member Handlers

### MethodHandler & MethodSignature

**Coverage**: 26+ tests in `MethodHandlerOutputTests.cs` plus `ConsumerSafetyAttributeTests.cs` for `CheckExportedSymbol`. Good breadth: async, throwing, name collisions, static/instance, generics, existentials, closures, tuples.

**Code Issues**:
- **Potential bug (line 806)**: `TryEmitCompletionHandlerOverload` -- for `ResultWithError` shape, `result` lambda parameter may be `Optional<T>` passed directly to `TrySetResult<T>`, causing type mismatch
- No test for constructor emission happy path (only the skip path tested)
- No test for failable constructor (`IsFailable`)

**MethodSignature.cs**:
- `Signature.GetCallArgumentString` (57 lines, 25+ pattern branches) now has **26 direct tests** covering all major branches (SafeHandle, enum, existential, closure, async, Cdecl closure, ObjC bridged, native remapped, self variants).
- `WrapperSignatureBuilder.HandleReturnType()` and `HandleArguments()` have many untested branches (generic return, native remapping)

### PropertyHandler

**Coverage**: 15+ tests in `PropertyHandlerTests.cs`. Good quality for what's covered.

**Code Issues**:
- **BUG (lines 476-479)**: `EmitGetter` -- redundant call to `GetReturnConversion` with identical arguments. Native type remapping for property getters likely does not work as intended. The `else if` block always produces the same null result, falling through to `$"{methodName}()"`.

**Missing Tests**:
- `PropertyHandlerFactory.Handles` and `Construct`
- Static property emission
- Closure property emission (not fallback)
- Property with async accessors
- `EmitGetter` with native type remapping (would expose the dead code bug)

### OperatorHandler

**Coverage**: Excellent for static utility methods (Theory tests for 33 operators). 6 emission tests cover key scenarios.

**Code Issues**:
- Only `== -> !=` pair synthesis tested; 5 other synthesis paths untested (`!= -> ==`, `< -> >`, `> -> <`, `<= -> >=`, `>= -> <=`)
- `GetPInvokeMethodName` fallback produces invalid C# identifier for unsupported symbols

**Missing Tests**:
- Unary operator emission (completely untested)
- `WillHaveEqualityOperator`/`WillHaveInequalityOperator` (zero tests)
- `ApplyGenericRemap` (zero direct tests)
- Null guards on reference types (lines 303-314)

---

## Protocol Emission

### ProtocolHandler

**Coverage**: `ProtocolHandlerOutputTests.cs` (1780 lines, 30+ tests) provides strong coverage. Tests cover associated types, Self requirements, property/method/subscript emission, dedup at 3 levels, AnyType skipping, `[UnsupportedSwiftType]` emission, async void naming, parameter normalization, CancellationToken.

**Test Quality Issues**: Tests use fragile `csOutput.IndexOf("class XProxy")` to split output. No direct unit tests for private filter methods (`HasBareGenericInSubscriptSignature`, `HasAnyTypeGenericArgInPropertyType`, etc.).

**Missing Tests**:
- Protocol with both inherited protocols AND own methods
- Closure return type in method signature (`GetClosureCSharpType`)
- Non-empty tuple return type (`GetTupleCSharpType`)
- Optional-wrapped existential property type

### ProtocolProxyEmitter

**Coverage**: `ProtocolProxyEmitterTests.cs` (1799 lines, 65+ tests) -- most comprehensive test file in the codebase. Covers class structure, vtable generation, static/instance fields, receiver methods, both constructor paths, ISwiftObject implementation, NativeMethods, conformance filtering, subscripts, dedup, tuple returns, closures, compositions, witness dispatch, Utf8Slice, ABI marshalling, GCHandle lifecycle.

**Missing Tests**:
- Protocol with properties AND methods AND subscripts simultaneously
- Method with multiple mixed-type parameters in dispatch
- `ISwiftExistentialConvertible<ExistentialContainer1>.ToExistentialContainer`
- Existential constructor with zero dispatchable members

### ProtocolSignatureHelper

**Coverage**: 6 tests covering `GetProjectedCSharpMethodKey` and `ProjectTypeToCSharp`.

**Missing Tests (HIGH PRIORITY)**:
- `NormalizeParamTypeForOverloadIdentity` -- ZERO tests for a function that prevents CS0111 duplicate method compilation errors
- `GetMethodSignatureKey` exception fallback path
- `MapAssociatedTypeToGenericParam` edge cases

### EveryProtocolEmitter

**Coverage**: `EveryProtocolEmitterTests.cs` (756 lines) covers class structure, vtable, protocol extension, witness table getter, type metadata, SetVtable, skip guards.

**Code Issues**:
- **Performance bug**: `IsSwiftKeyword` (line 862) allocates new 48-element `HashSet<string>` on every call. Should be `static readonly`.
- Hardcoded indentation in `argPassCode` (line 635)

**Missing Tests**:
- `globalEmittedSignatures` dedup (zero tests)
- `nonThrowingOverrides` throws suppression (zero tests)
- `ContainsGenericTypeParam` traversal through ClosureTypeSpec/ProtocolListTypeSpec

### ProtocolConformanceValidator

**Coverage**: 7 tests. All either hit the early `HasUnemittableInterfaceMembers` guard or use trivially matching void methods.

**Code Issues**:
- **BUG (line 356)**: `GetInterfaceSubscriptReturnType` missing `try/catch NotSupportedException` around `TranslateBoundGenericTypeToCSharp`. Both ProtocolHandler and ProtocolSignatureHelper handle this; the validator does not. Would crash on unrecognized bound generic subscript return type.

**Missing Tests (HIGH PRIORITY)**:
- `FindProtocol` multi-strategy lookup (zero tests)
- Property accessor contract validation (`{get;set;}` vs `{get;}`)
- Property/method type mismatch detection
- Subscript matching
- Inherited protocol recursion
- The entire second half of `CanFullyImplementProtocol`

### WitnessDispatchEmitter

**Coverage**: 45+ tests in `WitnessDispatchEmitterTests.cs` -- excellent. Covers all marshalability predicates, naming conventions, String/blittable/mixed dispatch, setter patterns, Utf8Slice dedup.

**Code Issues**:
- `GetSwiftParameterLabel` (line 668) duplicates logic from `NameProvider` -- changes to keyword handling in NameProvider would not propagate here
- `IsTypeBlittable` and `GetCSharpTypeName` use bare `catch` blocks swallowing all exceptions

**Missing Tests**:
- Property getter for all integer widths (Int8/UInt8/Int16/Int16/Int64/UInt64)
- Method with String return + String param + blittable param simultaneously

---

## Closure Emission

### ClosureEmitter (6 files, ~1500 lines total)

**Coverage**: **14 of 22 public methods have ZERO direct tests.** Most coverage is indirect through `MethodHandler.Emit()` in `ClosureCdeclEmitterTests.cs` (37 tests). `CompletionHandlerDetector` is the exception with 20 well-focused direct tests.

**Directly tested**: `AddCdeclContextToFunctionPointerType` (3 tests), `EmitEscapingClosureCallback` (2 tests — Swift + Cdecl modes), `EmitClosureReturnMarshalling` (1 test — non-void return)

**Code Issues**:
- **DEAD CODE**: `EmitAsyncThrowingClosureSwiftHelpers()` (line 188, ClosureEmitter.Async.cs) is public but never called from any production code
- **Latent bug in `AddContextToFunctionPointerType`** (line 326): `LastIndexOf(',', lastAngle)` doesn't account for nested generic type parameters. `delegate* unmanaged[Swift]<SwiftOptional<int>, void>` would insert SwiftSelf inside the nested generic. Same bug exists in `AddCdeclContextToFunctionPointerType` (line 423).

**Missing Tests by Priority**:
- P0: `EmitClosureReturnMarshalling`, `EmitThrowingClosureCallback`/`EmitThrowingClosureReturnMarshalling`, `AddContextToFunctionPointerType`
- P1: `EmitIndirectReturnCallback`, `EmitClosureReturnMarshallingWithStructParams`/`WithNonFrozenParams`, `GetSwiftConventionCType`/`GetSwiftClosureAdapterCode`
- P2: Bool return paths, Data return path in async-throwing, `GetSwiftArgLabel` edge cases

---

## P/Invoke & Wrapper Emission

### PInvokeEmitter.cs

**Coverage**: ~45 tests in `PInvokeEmitterTests.cs`. Good coverage for return types, arguments, SwiftSelf, SwiftError, ComputeEntryPoint (Tj suffix), EmitPInvoke.

**Code Issues**:
- `AddContextToFunctionPointerType` (line 530) has same `LastIndexOf(',', lastAngle)` nested-generic bug as ClosureEmitter (see Bug #5)

**Missing Tests**:
- `HandleGenericMetadata` (TypeMetadata params)
- `HandleProtocolConformance` (ProtocolWitnessTable params)
- Async instance method SwiftSelf singleton path
- `AddContextToFunctionPointerType` with nested generics

### PInvokeHelperEmitter.cs

**Coverage**: No dedicated test file, but key paths are exercised indirectly. `PInvokeHelperContext.CreateIfGeneric` and deferred emission via `DeferredPInvokeHelperContexts` are tested in `ThirdPartyValidationFixTests.cs` (lines 199, 292). `EmitHelperClass` is called at `ThirdPartyValidationFixTests.cs:292,294` but output assertions are minimal. Coverage is still thin — most methods lack targeted assertions.

**Code Issues**:
- `AddDeclaration` deduplicates by `MethodName` only, not full signature

**Missing Tests**: Dedicated tests for `GetQualifiedTypeName` (nested type naming), `AddDeclaration` dedup behavior, `EmitHelperClass` output structure, `PInvokeDeclaration.Emit` with bool return / async / metadata params

### WrapperEmitter.cs (937 lines)

**Coverage**: Partial. `EmitMethod` well-tested via integration; `EmitFailableFactory` has ZERO tests; many private methods untested.

**Missing Tests**:
- `EmitFailableFactory` (failable initializer factory)
- `EmitSwiftError` with typed throws (`SwiftException<TError>`)
- `GetMethodOwnGenericParams`
- `BuildWhereClause`
- `EmitFixedBlockStart/End`

### WrapperEmitter.Async.cs (~500 lines)

**Coverage**: 11 tests in `AsyncSwiftWrapperTests.cs`. Many sub-paths uncovered.

**Missing Tests**: Async constructors, String returns, typed throws error callback, non-frozen enum parameters, CancellationToken in async context

### WrapperEmitter.Return.cs (624 lines)

**Coverage**: 10 tests for ~20 code paths. Closure return and existential return paths added in Session 4.

**Code Issues**:
- Line 203-208: Class return allocates via `NativeMemory.Alloc` with no try/finally -- memory leak on exception
- Line 169: `result.Equals(default({containerType}))` uses reflection-based `ValueType.Equals()` for struct comparison

**Missing Tests**: Constructor returns, ~~closure returns~~, ~~existential returns~~, generic returns, ~~void returns~~, ObjC bridged returns, SwiftArray/SwiftOptional conversions

### WrapperEmitter.Marshalling.cs (703 lines)

**Missing Tests**: `TranslateTypeSpecForConversion`, `EmitSafeHandleAddRef`/`EmitSafeHandleRelease` (zero tests for ref counting), `EmitGenericArguments`, `EmitProtocolWitnessTables`

### OptionalPointerWrapperEmitter.cs

**Coverage**: Excellent (18+ tests, 1267 lines). Best-tested wrapper file.

---

## Utility Emitters

### Files with ZERO dedicated test coverage:

(Plus `ModuleEmitter.cs` documented in [Module Emission](#module-emission) above.)

| File | Lines | Risk Level | Why It Matters |
|------|-------|-----------|----------------|
| **TypeHandlerHelpers.cs** | 605+ | **HIGH** | `GetImplementedInterfaces` determines C# interface declarations (now includes `Swift.Hashable` skip + cross-module support). `EqualityMethodsWriter` has new Hashable-conditional `GetHashCode` path. Bugs cause CS0535. |
| **UnsupportedSwiftTypeSupport.cs** | 78 | **MEDIUM** | Recursive `TryFindFallbackInfo` drives `[UnsupportedSwiftType]` across generator. |
| **Utf8SliceEmitter.cs** | 153 | **MEDIUM** | Dedup logic critical for wrapper compilation. Same pattern as well-tested CancellationTaskEmitter. |

### Files with indirect-only coverage (no dedicated tests, but exercised through integration):

| File | Lines | Risk Level | Indirect Coverage |
|------|-------|-----------|-------------------|
| **MemberEmissionValidator.cs** | 958+ | **HIGH** | Exercised in `ThirdPartyValidationFixTestsV4.cs` (35+ calls to `CanEmitProperty`, `CanEmitMethod`, `ShouldSkipMethodEmission`), but many of the 20+ skip reasons lack targeted tests. New `HasUnsupportedPropertyType` method (used by NameProvider) also untested directly. |
| **AsyncStreamEmitter.cs** | 165 | **MEDIUM** | AsyncStream property emission validated in `PropertyHandlerTests.cs:227` (IAsyncEnumerable, Swift wrapper). No standalone emitter tests. |
| **PInvokeHelperEmitter.cs** | 206 | **MEDIUM** | `CreateIfGeneric` and deferred emission tested in `ThirdPartyValidationFixTests.cs:199,292`. `EmitHelperClass` called at `ThirdPartyValidationFixTests.cs:292,294` but output assertions are minimal. |

### Files with good-to-excellent coverage:

| File | Lines | Tests | Notes |
|------|-------|-------|-------|
| CancellationTaskEmitter.cs | 125 | 32 | Thorough |
| GenericTypeEmitter.cs | 206 | 20 | Good breadth; `GetGenericMetadataAccessor` untested |
| ArraySliceNormalizationEmitter.cs | 582 | 39 | Excellent |
| ExistentialBypassEmitter.cs | 427 | 19 | Good, but has non-deterministic hash bug |
| XmlDocCommentEmitter.cs | 213 | 19 | Comprehensive |
| DefaultParameterOverloadEmitter.cs | 479 | 9 | Adequate for public API; private emission untested |

### Other utility files:

- **SwiftTypeNameHelper.cs** (122 lines): `GetSwiftTypeName` has zero direct tests; only `GetSwiftTypeNameForMetatype` has 5 tests
- **BridgeHints.cs**: Pure JSON models, integration-tested via SwiftUIBridgeEmitter
- **IndentedTextWriter.cs / Extensions**: Trivial; `WriteLines` uses `RemoveEmptyEntries` which silently drops blank lines

---

## Bugs Found

### High Severity

| # | File | Line | Description |
|---|------|------|-------------|
| 1 | **ProtocolConformanceValidator.cs** | 356 | Missing `try/catch NotSupportedException` around `TranslateBoundGenericTypeToCSharp` in `GetInterfaceSubscriptReturnType`. Crashes validator on unrecognized bound generic subscript return types. Both ProtocolHandler and ProtocolSignatureHelper have this guard; the validator does not. |
| 2 | **ExistentialBypassEmitter.cs** | 138 | Uses `string.GetHashCode()` (non-deterministic per-process since .NET Core) for wrapper symbol names. Every sibling emitter uses `DeterministicHash8` (FNV-1a). Symbols differ across incremental builds. Also, `Math.Abs(int.MinValue)` throws `OverflowException`. |

### Medium Severity

| # | File | Line | Description |
|---|------|------|-------------|
| 3 | **PropertyHandler.cs** | 476-479 | `EmitGetter` -- redundant call to `GetReturnConversion` with identical arguments. Native type remapping for property getters likely does not work as intended. The `else if` block always produces same null result, falling through to `$"{methodName}()"`. |
| 4 | **FrozenStructHandler.cs** | 73 | Wrong `nameof` in exception: `nameof(structDecl.ParentDecl)` should be `nameof(structDecl.ModuleDecl)`. Exception message says wrong property is null. |
| 5 | **ClosureEmitter.cs, PInvokeEmitter.cs** | CE:326,423 PE:537 | `AddContextToFunctionPointerType` (ClosureEmitter) and `AddContextToFunctionPointerType` (PInvokeEmitter) -- `LastIndexOf(',', lastAngle)` doesn't account for nested generics. `delegate* unmanaged[Swift]<SwiftOptional<int>, void>` would insert context param inside nested generic. Same pattern in 3 locations. |
| 6 | **EveryProtocolEmitter.cs** | 862 | `IsSwiftKeyword` allocates new 48-element `HashSet<string>` on every call. Called per parameter per method per protocol. Should be `static readonly`. |
| 7 | **MemberEmissionValidator.cs, PropertyHandler.cs** | MEV:232 PH:318,381 | `CanEmitProperty` (validator) and `PropertyHandler.Emit` both mutate `accessor.Method.IsAccessor = true` as a side-effect. PropertyHandler documents intent (prevents type conversion mismatch), but mutation is persistent if the same MethodDecl graph is reused in later passes. |

### Low Severity

| # | File | Line | Description |
|---|------|------|-------------|
| 8 | **MethodHandler.cs** | 806 | `TryEmitCompletionHandlerOverload` -- for `ResultWithError` shape, `result` lambda parameter may be `Optional<T>` passed to `TrySetResult<T>`, type mismatch. |
| 9 | **ClosureEmitter.Async.cs** | 188-249 | `EmitAsyncThrowingClosureSwiftHelpers()` is dead code -- public but never called from production code. |
| 10 | **AsyncStreamEmitter.cs** | 35 | `EmitElementCallback` marshals element via `SwiftMarshal.MarshalFromSwift<T>` but discards the result. Dead code in emitted C#. |
| 11 | **WrapperEmitter.Return.cs** | 203-208 | Class return allocates via `NativeMemory.Alloc` with no try/finally -- memory leak on exception. |
| 12 | **OperatorHandler.cs** | 110 | `GetPInvokeMethodName` fallback produces invalid C# identifier (`PInvoke_op_??`) for unsupported symbols. |
| 13 | **Multiple files** | -- | `DeterministicHash8` (identical FNV-1a) duplicated in 3 files. Should be shared utility. |

---

## Prioritized Missing Tests

### Tier 1: Critical (would catch real bugs or prevent silent regressions)

| # | Area | Description |
|---|------|-------------|
| 1 | **MemberEmissionValidator.cs** | Has indirect coverage via `ThirdPartyValidationFixTestsV4` but many of the 20+ skip reasons lack targeted tests. Needs expanded coverage for `CanEmitSubscript`, Codable member pruning, `ContainsUnsupportedTupleElement`, and `IsNonSimpleEnumWithMemoryManagement`. |
| 2 | **TypeHandlerHelpers.cs** | Tests for `GetImplementedInterfaces` (now includes Hashable skip logic), `EqualityMethodsWriter` conditional suppression + Hashable-conditional `GetHashCode`, `ISwiftObjectMethodWriter` frozen/non-frozen paths. Also test `ProtocolConformanceHelper.CrossModuleSupportedProtocols` inclusion of `Swift.Hashable`. |
| 3 | **MethodSignature.GetCallArgumentString** | Direct tests for all 25 pattern branches. Incorrect strings cause runtime P/Invoke crashes. |
| 4 | **PInvokeHelperEmitter.cs** | Has indirect coverage via `ThirdPartyValidationFixTests` for `CreateIfGeneric` and deferred emission, but needs targeted tests for `GetQualifiedTypeName`, `AddDeclaration` dedup, `EmitHelperClass` output structure, `PInvokeDeclaration.Emit`. |
| 5 | **ProtocolConformanceValidator member matching** | Tests for `FindProtocol`, property/subscript/method matching, accessor contracts, inherited protocol recursion. |
| 6 | **ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity** | Direct tests for Optional stripping per type kind. Prevents CS0111 duplicate method errors. |

### Tier 2: High Value (significant untested logic)

| # | Area | Description |
|---|------|-------------|
| 7 | **ModuleEmitter (StringEmitter.EmitModule)** | Happy path with file I/O, SwiftUI bridge emission, custom namespace resolver. |
| 8 | **ClosureEmitter direct tests** | `EmitClosureReturnMarshalling`, `EmitThrowingClosureCallback`, `AddContextToFunctionPointerType`. |
| 9 | **WrapperEmitter.Return.EmitReturnMethod** | Closure returns, existential returns, type-converted returns (SwiftString/Array/Optional). Currently 4 tests for ~20 paths. |
| 10 | **WrapperEmitter.EmitFailableFactory** | Failable initializer factory emission. Zero tests. |
| 11 | **ClassHandler actor emission** | `IsActor = true` path with `unownedExecutor` skipping. |
| 12 | **FrozenStructHandler stored properties** | Value-type and reference-type stored property field emission. |
| 13 | **Constructor emission happy path** | Normal constructor through `WrapperEmitter.EmitConstructor`. Only skip path tested. |
| 14 | **EveryProtocolEmitter global dedup** | `globalEmittedSignatures` and `nonThrowingOverrides` parameters. |
| 15 | **Dispose/Finalizer emission** | New pattern across ClassHandler, EnumHandler, FrozenStructHandler, NonFrozenStructHandler — `GC.SuppressFinalize` + `SwiftDispose.FinalizerCleanup`. Zero tests for finalizer output or generic type name stripping for finalizer name. |
| 16 | **EmitStringRawValueExtensions** | New EnumHandler.SimpleEnum method for String-raw-value C# enums. `ToRawValue`/`FromRawValue` switch emission. Indirectly covered by 2 updated tests but no dedicated test for edge cases (empty cases, special characters in case names). |

### Tier 3: Medium Value (good to have)

| # | Area | Description |
|---|------|-------------|
| 17 | **UnsupportedSwiftTypeSupport.TryFindFallbackInfo** | Recursive search for `[UnsupportedSwiftType]`. ClosureTypeSpec recursion untested. |
| 18 | **Utf8SliceEmitter** | Dedup logic, module reset, per-type tracking. Same pattern as well-tested CancellationTaskEmitter. |
| 19 | **AsyncStreamEmitter** | Core path validated via `PropertyHandlerTests.cs:227`, but no standalone emitter tests for individual methods (`EmitElementCallback`, `EmitCompletionCallback`, `EmitPInvokeDeclaration`). |
| 20 | **SwiftTypeNameHelper.GetSwiftTypeName** | Zero direct tests for the most-used helper. |
| 21 | **Operator pair synthesis** | 5 untested synthesis paths (`!= -> ==`, `< -> >`, etc.) |
| 22 | **NonFrozenStructHandler Emit paths** | Currently 7% coverage. Operators, Equatable, Hashable, generics, methods all untested. |
| 23 | **WrapperEmitter.Marshalling.EmitSafeHandleAddRef/Release** | Ref counting correctness. Zero tests. |
| 24 | **ModuleHandler composition proxy** | ~150 lines of proxy generation with zero tests. |
| 25 | **MemberEmissionValidator.HasUnsupportedPropertyType** | New public method for NameProvider collision filtering. No dedicated test. |

---

## Cross-Cutting Observations

### Test Infrastructure Issues

1. **Model tests masquerading as handler tests**: `ClassHandlerTests`, `FrozenStructHandlerTests`, `NonFrozenStructHandlerTests` test the data model (that `ClassDecl` can hold properties), not that the handler emits correct C#. `TypeHandlersOutputTests` is the only file testing real emission, and it has just 9 tests across all 3 handlers.

2. **Helper method duplication**: Every handler test file copies `CreateClassDecl`, `CreateEnumDecl`, `CreateProtocolDecl`, etc. A shared test utility class would reduce maintenance and ensure consistency.

3. **Static mutable state**: `EnumHandler.ResetUtf8SliceTracking()`, `CancellationTaskEmitter.ResetForModule()`, `Utf8SliceEmitter.ResetForModule()` all use static mutable state without synchronization. Tests must call reset methods, and failure to do so causes flaky cross-test contamination. (`SwiftUIBridgeCollector` is the exception -- it properly uses `lock(Sync)` for all operations.)

4. **String-based output validation**: All emission tests use `Assert.Contains` on raw string output. No test parses the emitted C# or Swift to validate structural correctness (matching braces, valid XML, correct indentation). A test that feeds emitted code through Roslyn's syntax parser would catch structural issues.

### No Negative/Error Path Tests

- No test verifies behavior when `OutputDirectory` doesn't exist
- No test passes null for required properties on option classes
- No test verifies behavior with concurrent emission (thread safety)

### Duplicated Code Across Emitter

- `DeterministicHash8` (FNV-1a) duplicated in 3 files
- Type resolution logic (`ResolvePropertyType`, `ResolveMethodReturnType`) duplicated between `ModuleHandler` and `ProtocolHandler`
- Keyword lists duplicated between `WitnessDispatchEmitter` and `NameProvider`
- `GetSwiftArgLabel` heuristic duplicated between `OptionalPointerWrapperEmitter` and `ClosureEmitter.SwiftWrapper`

### Integration Test Gaps

- No test generates bindings from a synthetic ABI JSON, compiles the C# output, and verifies it compiles
- No test verifies the emitted Swift wrapper compiles against a Swift compiler
- No test exercises a method with multiple complex features simultaneously (async + generic + closure + throws)

---

## Remediation Plan

Work is split into 3 sessions. Bugs are fixed first so new tests validate corrected behavior.

### Session 1: Bug Fixes

Fix all 13 bugs from the [Bugs Found](#bugs-found) table. Grouped by effort:

**Quick wins (~10 min):**
- Bug #4 — FrozenStructHandler: fix `nameof(structDecl.ParentDecl)` → `nameof(structDecl.ModuleDecl)`
- Bug #6 — EveryProtocolEmitter: make `IsSwiftKeyword` HashSet `static readonly`
- Bug #9 — ClosureEmitter.Async: remove dead `EmitAsyncThrowingClosureSwiftHelpers()`
- Bug #13 — Extract shared `DeterministicHash8` utility from 3 duplicate implementations

**Medium effort (~30 min):**
- Bug #2 — ExistentialBypassEmitter: replace `string.GetHashCode()` with `DeterministicHash8`
- Bug #3 — PropertyHandler: investigate and fix or remove dead native-type-remapping code path in `EmitGetter`
- Bug #5 — ClosureEmitter + PInvokeEmitter: fix `LastIndexOf(',', lastAngle)` nested-generic bug in all 3 locations
- Bug #7 — MemberEmissionValidator + PropertyHandler: assess `IsAccessor` mutation side-effect; add defensive copy or document as intentional

**Needs care (~30 min):**
- Bug #1 — ProtocolConformanceValidator: add missing `try/catch NotSupportedException` around `TranslateBoundGenericTypeToCSharp`
- Bug #8 — MethodHandler: fix `TryEmitCompletionHandlerOverload` Optional→TrySetResult type mismatch
- Bug #10 — AsyncStreamEmitter: remove dead marshalling code in `EmitElementCallback`
- Bug #11 — WrapperEmitter.Return: add try/finally around `NativeMemory.Alloc` in class return path
- Bug #12 — OperatorHandler: guard `GetPInvokeMethodName` fallback against invalid C# identifiers

**Validation:** Run `./run-tests.sh | tail -20` after each group. Confirm 2700+ tests still pass, 0 failures.

### Session 2: Tier 1 Missing Tests

Write tests for the 6 highest-priority gaps. Each item is a new test file or major expansion of an existing one.

| # | Target | Scope |
|---|--------|-------|
| 1 | **MemberEmissionValidator** | Expand `ThirdPartyValidationFixTestsV4` or create dedicated file. Cover remaining skip reasons: `CanEmitSubscript`, `ContainsUnsupportedTupleElement`, `IsNonSimpleEnumWithMemoryManagement`, Codable member pruning. |
| 2 | **TypeHandlerHelpers** | New test file. Cover `GetImplementedInterfaces` (same-module protocol, cross-module, Equatable), `EqualityMethodsWriter` (suppression when explicit operators exist), `ISwiftObjectMethodWriter` (frozen vs non-frozen). |
| 3 | **MethodSignature.GetCallArgumentString** | New test file or expand `SignatureBuilderTests`. Direct tests for all 25 pattern branches. |
| 4 | **PInvokeHelperEmitter** | New test file. `CreateIfGeneric`, `GetQualifiedTypeName`, `AddDeclaration` dedup, `EmitHelperClass` output, `PInvokeDeclaration.Emit` with bool/async/metadata variants. |
| 5 | **ProtocolConformanceValidator member matching** | Expand existing 7 tests. Cover `FindProtocol` multi-strategy, property accessor contracts, method/subscript matching, inherited protocol recursion. |
| 6 | **ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity** | New tests. Optional stripping per type kind (class vs struct vs enum). |

**Validation:** Run `./run-tests.sh | tail -20` after each test file. Confirm new tests pass and no regressions.

### Session 3: Tier 2–3 Missing Tests

Lower urgency. Can be done incrementally or in one focused session.

**Tier 2 (high value):**
- ModuleEmitter `EmitModule` happy path with file I/O
- ClosureEmitter direct tests (`EmitClosureReturnMarshalling`, `EmitThrowingClosureCallback`, `AddContextToFunctionPointerType`)
- WrapperEmitter.Return `EmitReturnMethod` (closure/existential/type-converted returns)
- WrapperEmitter `EmitFailableFactory`
- ClassHandler actor emission
- FrozenStructHandler stored properties
- Constructor emission happy path
- EveryProtocolEmitter global dedup (`globalEmittedSignatures`, `nonThrowingOverrides`)

**Tier 3 (good to have):**
- UnsupportedSwiftTypeSupport `TryFindFallbackInfo`
- Utf8SliceEmitter dedup logic
- AsyncStreamEmitter standalone methods
- SwiftTypeNameHelper `GetSwiftTypeName`
- Operator pair synthesis (5 untested paths)
- NonFrozenStructHandler emit paths
- WrapperEmitter.Marshalling `EmitSafeHandleAddRef`/`Release`
- ModuleHandler composition proxy

**Validation:** Run full test suite after completing each tier.

---

## Session Status

### Session 1: Bug Fixes — COMPLETE

All 13 bugs fixed (2026-02-16). 2782 unit tests + 699 integration tests passing, 0 failures. Changes:
- Bug #4: `nameof` fix in FrozenStructHandler
- Bug #6: `IsSwiftKeyword` HashSet made `static readonly`
- Bug #9: Removed dead `EmitAsyncThrowingClosureSwiftHelpers`
- Bug #13: Extracted `EmitterUtility.DeterministicHash8` from 3 duplicates
- Bug #2: Replaced `string.GetHashCode()` with `DeterministicHash8` in ExistentialBypassEmitter
- Bug #3: Removed dead native-type-remapping `else if` in PropertyHandler
- Bug #5: Fixed nested-generic `AddContextToFunctionPointerType` in 3 locations via `EmitterUtility.FindLastTopLevelComma`
- Bug #7: Documented `IsAccessor` mutation as intentional idempotent behavior
- Bug #1: Added `try/catch NotSupportedException` in ProtocolConformanceValidator
- Bug #8: REVERTED — original code was type-safe; `(T?, Error?) -> Void` correctly maps to `Task<T?>` (not `Task<T>`)
- Bug #10: Removed dead `SwiftMarshal.MarshalFromSwift` in AsyncStreamEmitter
- Bug #11: Added `try/catch { NativeMemory.Free; throw; }` in WrapperEmitter.Return
- Bug #12: Sanitized `GetPInvokeMethodName` fallback in OperatorHandler

### Sessions 2 & 3: Test Coverage — COMPLETE

104 new tests added (2782 → 2886 unit tests), 0 failures. Codex review incorporated.

**7 new test files:**
- `EmitterUtilityTests.cs` (8) — DeterministicHash8, FindLastTopLevelComma
- `GetCallArgumentStringTests.cs` (15) — 15 of ~25 pattern branches (SafeHandle, enum, existential, closure, ref/out, self variants)
- `TypeHandlerHelpersTests.cs` (11) — GetImplementedInterfaces (6), EqualityMethodsWriter (5)
- `PInvokeHelperEmitterTests.cs` (12) — CreateIfGeneric, AddDeclaration dedup, EmitHelperClass, PInvokeDeclaration.Emit
- `MemberEmissionValidatorTests.cs` (10) — CanEmitSubscript, Codable pruning, HasUnsupportedPropertyType, SwiftUI
- `UnsupportedSwiftTypeSupportTests.cs` (5) — TryFindFallbackInfo recursive search, EscapeStringLiteral
- `Utf8SliceEmitterTests.cs` (6) — Dedup, reset, per-type tracking

**8 expanded test files:**
- `SwiftTypeNameHelperTests.cs` (+7) — GetSwiftTypeName all TypeSpec variants
- `ClosureCdeclEmitterTests.cs` (+2) — Bug #5 nested generic regression
- `OperatorHandlerTests.cs` (+6) — Bug #12 regression + 4 pair synthesis paths
- `ProtocolSignatureHelperTests.cs` (+5) — NormalizeParamTypeForOverloadIdentity
- `ProtocolConformanceValidatorTests.cs` (+4) — Bug #1 subscript regression + property accessor contracts + inheritance
- `WrapperEmitterReturnTests.cs` (+4) — Bug #11 class leak regression + string/enum/void return paths
- `TypeHandlersOutputTests.cs` (+6) — Finalizer (class, frozen struct, non-frozen struct), Hashable, Equatable
- `EnumHandlerOutputTests.cs` (+4) — Finalizer, GC.SuppressFinalize, StringRawValue ToRawValue/FromRawValue

**Tier coverage:**

| Tier | DONE | PARTIAL | OPEN (deferred) |
|------|------|---------|-----------------|
| Tier 1 (6 items) | 4 | 2 | 0 |
| Tier 2 (10 items) | 4 | 3 | 3 |
| Tier 3 (9 items) | 5 | 1 | 3 |

**Remaining OPEN items** (not worth the investment):
- ModuleEmitter `EmitModule` — file I/O orchestration, actual emission logic already tested via handlers
- EveryProtocolEmitter global dedup — hard to test directly, better caught at integration level
- AsyncStreamEmitter standalone — core path already validated via PropertyHandlerTests
- WrapperEmitter.Marshalling SafeHandle — runtime ref-counting concern, not generator output

### Session 4: High-Value Regression Tests — COMPLETE

19 new tests across 4 files, targeting crash-prone code paths where bugs manifest as runtime P/Invoke crashes. Also fixed 1 pre-existing test race condition.

#### 4.1 GetCallArgumentString — 11 new tests ✓
**File:** `GetCallArgumentStringTests.cs` (15 → 26 tests)
**Tests added:** AsyncCallback, AsyncErrorCallback, AsyncContext, AsyncTask, CdeclClosureFuncPtr, CdeclClosureContext, AsyncThrowingContext, AsyncThrowingStartFunc, ObjCBridged, NativeRemappedSafeHandle, NativeRemapped. Covers all remaining non-trivial branches in `Signature.GetCallArgumentString`.

#### 4.2 Closure/existential return paths — 2 new tests ✓
**File:** `WrapperEmitterReturnTests.cs` (8 → 10 tests)
**Tests added:** `Return_ClosureType_EmitsEscapingClosureWrapper` (ClosureTypeSpec return → SwiftEscapingClosure marshalling), `Return_Existential_EmitsProxyConstruction` (ProtocolListTypeSpec return → Proxy construction). Optional class return dropped — requires complex `Optional<T>` generic resolution better suited to integration tests.

#### 4.3 Constructor emission — DROPPED (already covered)
`ConstructorHandlerOutputTests.cs` already has 10 tests covering struct/class/failable/closure/throwing constructors.

#### 4.4 ClosureEmitter direct tests — 3 new tests ✓
**File:** `ClosureEmitterDirectTests.cs` (NEW, 3 tests)
**Tests added:** `EmitClosureReturnMarshalling_NonVoidReturn_EmitsEscapingClosure`, `EmitEscapingClosureCallback_SwiftMode_EmitsCallConvSwift`, `EmitEscapingClosureCallback_CdeclMode_EmitsCallConvCdecl`. Directly tests `ClosureEmitter` static methods with `ClosureTypeSpec` + `ClosureHandler(typeDatabase)`.

#### 4.5 ClassHandler actor emission — 1 new test ✓
**File:** `TypeHandlersOutputTests.cs`
**Test added:** `Emit_ClassHandler_Actor_SkipsUnownedExecutor` — verifies `unownedExecutor` property is skipped while non-runtime properties (Count with real getter accessor) still emit. Uses `CreateTypeDatabaseWithSwiftInt()` for type resolution.

#### 4.6 FrozenStructHandler stored property fields — 2 new tests ✓
**File:** `TypeHandlersOutputTests.cs`
**Tests added:** `Emit_FrozenStructHandler_StoredValueTypeProperty_EmitsTypedField` (Swift.Int → `private long count_`), `Emit_FrozenStructHandler_StoredRefTypeProperty_EmitsIntPtrField` (Swift.String → `private IntPtr name_`). Mixed-properties test dropped — two single-property tests provide equivalent regression coverage.

#### Infrastructure fix: Utf8SliceEmitterTests parallelization race ✓
**File:** `Utf8SliceEmitterTests.cs`
Added `[assembly: CollectionBehavior(DisableTestParallelization = true)]`. `Utf8SliceEmitter` uses static mutable state that production emitters touch during parallel test execution. Suite runs in ~1s, so negligible cost.

**Verification: 2910 unit tests passing, 0 failures.**
