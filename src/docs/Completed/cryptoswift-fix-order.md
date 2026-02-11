# CryptoSwift Generator Fix Order

Ordered by runtime-unblocking impact. Each step includes generator files, assertions, and test locations.

> **Source**: Fix-order checklist from Codex review (Feb 2026), validated against current generator source.

---

## Step 1: PInvokeEmitter — Frozen enum P/Invoke (Bug #24) — DONE

**Unblocks**: Runtime validation for any type taking enum parameters (SHA2, HMAC, Digest.Sha2, etc.)

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` — moved enum check before frozen guard
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` — added enum checks to GetPInvokeType()/GetPInvokeArgument()
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs` — extended nonFrozenParams to include enums

**Tests added** (9 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodHandlerOutputTests.cs` — 2 tests (sync + async frozen enum params)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/SignatureBuilderTests.cs` — 7 tests (EnumSafeHandle, call arg extraction, managed type exclusion, async IntPtrFromNonFrozen)

**Validation**: `verify-fix-order.sh 1` → PASS=2 FAIL=0. Unit tests: 1512 passed. TestFramework: 61/61, 0 degraded.

---

## Step 2: Constructor projection for non-frozen classes (Bug #20) — DONE

**Unblocks**: Constructing ANY non-frozen class type without `RuntimeHelpers.GetUninitializedObject()` hack

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — extended `ConstructorHandlerFactory.Handles()` to include `ClassDecl`; added failable class initializer guard; excluded async constructors (need callback-based factory method emission)

**Tests added** (5 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorHandlerOutputTests.cs` — 4 tests (class constructor signature, indirect result, enum param IntPtr, failable skip)
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ConductorTests.cs` — 1 test (async class constructor → MethodHandler) + updated existing class constructor test (→ ConstructorHandler)

**Validation**: `verify-fix-order.sh 2` → PASS=2 FAIL=0. Unit tests: 1517 passed. TestFramework: 61/61, 0 degraded.

---

## Step 3: Operator return ABI (Bugs #1, #4, #10) — DONE

**Unblocks**: 14 arithmetic operators on BigUInt/BigInt, plus generic operator edge cases

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` — added SwiftIndirectResult allocation for non-frozen returns (Bug #1), skip generic operand operators (Bug #4), T1→T0 generic remap (Bug #10)

**Tests added** (3 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerOutputTests.cs` — 3 tests (indirect result allocation, generic operand skip, T1→T0 remap)

**Validation**: `verify-fix-order.sh 3` → PASS=3 FAIL=0. Unit tests: 1520 passed. TestFramework: 61/61, 0 degraded.

---

## Step 4: Tuple return marshalling + pointer safety (Bugs #2, #6) — DONE

**Unblocks**: 3 methods on AEADChaCha20Poly1305, plus latent `void*` risk

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Return.cs` — added `EmitTupleReturnMarshalling()` for per-element marshalling; fixed bound generic handling in `GetTupleElementMarshalCode()`; replaced `void*` with `IntPtr` in `GetPInvokeTypeForTupleElement()`; fixed non-ObjC optional pointer-shape mismatch (was `&itemName`, now `itemName` directly)
- `src/Swift.Bindings/src/Marshaler/TupleHandler.cs` — replaced `void*` with `IntPtr` in `TranslateElementTypeToPInvoke()` for bound generic and existential fallbacks

**Tests added** (6 tests):
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/TupleHandlerTests.cs` — 3 tests (bound generic → IntPtr, multiple bound generics → IntPtr, optional non-ObjC → IntPtr)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodHandlerOutputTests.cs` — 3 tests (tuple with bound generic emits per-element marshalling, optional non-ObjC uses direct IntPtr marshalling, all-primitive tuple returns directly)

**Validation**: `verify-fix-order.sh 4` → PASS=2 FAIL=0. Unit tests: 1526 passed. TestFramework: 61/61, 0 degraded.

---

## Step 5: EveryProtocol vtable/index integrity (Bug #21) — DONE

**Unblocks**: All EveryProtocol protocol conformances with deduplicated methods

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — moved vtable index assignment before global signature dedup check

**Tests added** (3 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` — 3 tests (single global skip preserves indices, multiple global skips preserve indices, no-skip baseline sequential)

**Validation**: `verify-fix-order.sh 5` → PASS=1 FAIL=0. Unit tests: 1529 passed. TestFramework: 61/61, 0 degraded.

---

## Step 6: EveryProtocol signature correctness (Bugs #22a, #22b, #23, #13) — DONE

**Unblocks**: Swift wrapper compilation for protocols with throwing methods or function-type returns

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — check `method.Throws` and insert `throws` keyword before return arrow
- `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftTypeNameHelper.cs` — detect ClosureTypeSpec in `GetSwiftTypeNameForMetatype()` and wrap in parentheses

**Bug #22b (rethrows)**: Investigated and documented as WON'T FIX — the Swift ABI JSON format only provides `"throwing": true` (boolean), with no way to distinguish `throws` from `rethrows`. CryptoSwift has no `rethrows` methods.

**Additional fix** (from Codex review): Cross-protocol dedup was throws-order-dependent. If a throwing protocol was emitted first and a non-throwing protocol with the same method came second, the non-throwing requirement couldn't be satisfied. Fixed with a pre-pass in `ModuleHandler.cs` that computes `nonThrowingOverrides` — signatures where non-throwing MUST win. The emitter now suppresses `throws` for these signatures.

**Files** (modified for dedup fix):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` — added `ComputeNonThrowingOverrides()` pre-pass
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — `GetSwiftMethodSignature` → internal; `EmitMethodImplementation` accepts `effectiveThrows` override; `EmitProtocolConformance` threads `nonThrowingOverrides` through

**Tests added** (11 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` — 6 tests (3 throws emission + 3 cross-protocol dedup override)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftTypeNameHelperTests.cs` — 5 tests (function type metatype parenthesization, optional return, simple named type, existential type, throwing function type)

**Validation**: `verify-fix-order.sh 6` → PASS=2 FAIL=0 WARN=1. Unit tests: 1540 passed. TestFramework: 61/61, 0 degraded.

---

## Step 7: Proxy/interface conformance alignment (Bugs #3, #11, #12) — DONE

**Unblocks**: Protocol proxy dispatch for protocols with closure parameters or generic returns

**Files** (modified):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` — added `ContainsAnyTypeGenericArg()`, `HasAnyTypeGenericArgInSignature()`, `ResolveMethodTypeName()` helpers; restructured `Emit()` loop to check for AnyType generic args before interface emission; collects `skippedMethodKeys` and passes to proxy
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` — added `_skippedMethodKeys` field; `EmitProxyClass()` accepts skip set parameter
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Helpers.cs` — changed `GetMethodKey` from `private` to `internal static`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` — skip implementation for methods in `_skippedMethodKeys`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` — skip receivers for methods in `_skippedMethodKeys`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs` — skip vtable assignments (both local and Swift) for methods in `_skippedMethodKeys`

**Tests added** (11 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolHandlerOutputTests.cs` — 4 tests (AnyType generic return skipped on interface, skipped on proxy, vtable field preserved, valid bound generic emits normally) + 1 Theory with 7 inline data cases (ContainsAnyTypeGenericArg detection)

**Validation**: `verify-fix-order.sh 7` → PASS=3 FAIL=0. Unit tests: 1551 passed. TestFramework: 61/61, 0 degraded.

---

## Step 8: Wrapper extension filtering + cleanup (Bugs #5, #14-17, #7, #8, #9) — DONE

**Unblocks**: Swift wrapper compilation (EveryProtocol + extensions), plus generic type edge cases

**Files** (modified):
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — Bug #5: added overflow operators (`&+`, `&-`, `&*`, `&<<`, `&>>`, `&<<=`, `&>>=`) to `_operators` set so they route to `CreateOperatorDecl` instead of `CreateMethodDecl`; Bugs #15, #17: `IsNodeModuleInternal()` from DeclAttributes → sets `MethodDecl.IsModuleInternal` and `TypeDecl.IsModuleInternal`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` — Bug #14: replaced TypeDatabase filter with mangled name module check (`IsMangledNameFromModule`) to correctly exclude stdlib protocols (Collection, FixedWidthInteger) from EveryProtocol conformance
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ArraySliceNormalizationEmitter.cs` — Bugs #15, #17: skip internal types/methods via `IsModuleInternal` flag (not Visibility, to avoid breaking interface contracts)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs` — Bug #7: generic type params in SwiftSafeHandle<>
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` — Bug #8: non-frozen bound generic → DangerousGetHandle
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` — Bug #9: property name dedup (static/instance collision)
- `src/Swift.Bindings/src/Model/TypeDecl/Visibility.cs` — added `Internal` value
- `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` — added `IsModuleInternal` flag
- `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` — added `IsModuleInternal` flag
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` — map Internal → "internal"

**Tests added** (21 tests):
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ArraySliceNormalizationEmitterTests.cs` — 5 tests (internal method/type skip, public passthrough, IsModuleInternal)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ClassHandlerTests.cs` — 2 tests (duplicate property detection)
- `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftABIParserRuntimeTests.cs` — 5 tests (IsModuleInternal flag + DeclAttributes)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleHandlerTests.cs` — 9 tests (IsMangledNameFromModule Theory)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs` — 7 overflow operator InlineData cases added to IsSupportedOperator Theory

**Bug #16 (argument labels)**: Verified not an issue in current output — all generated wrappers use correct labels.

**Validation**: `./verify-fix-order.sh all` — 25 PASS, 0 FAIL, 1 WARN. Unit tests: 1586 passing. TestFramework: 61/61, 0 degraded. Swift wrapper compiles. C# build has 0 new errors (18 pre-existing known bugs).

---

## Step 9: Protocol composition return types + swiftinterface internal detection (Bugs #13, #16) — DONE

**Unblocks**: Generated Swift.CryptoSwift.swift compiles without errors (0 typecheck errors)

**Files** (new):
- `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs` — parses `.swiftinterface` to extract internal member keys (detects `@inlinable internal` members with `AccessControl` that are ambiguous in ABI JSON)

**Files** (modified):
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — Bug #13: `CreateProtocolCompositionTypeSpec()` now parses protocol names from `printedName` when no children present; Bug #16: expanded `IsNodeModuleInternal()` for `UsableFromInline` regardless of `AccessControl`, and `Inlinable` without `AccessControl`; added `IsInternalFromSwiftInterface()` cross-reference; constructor accepts optional `internalMemberKeys` set
- `src/Swift.Bindings/src/Program.cs` — added `-s`/`--swiftinterface` CLI option; parses swiftinterface and passes internal member keys to parser

**Tests added** (17 tests):
- `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftInterfaceAccessParserTests.cs` — 13 tests (func/var/let/init detection, nested types, multi-param labels, underscore labels, public exclusion, nonexistent file, extension with internal func, extension with conformance, unqualified extension)
- `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftABIParserRuntimeTests.cs` — 4 tests (Inlinable without AccessControl, Inlinable with AccessControl, ProtocolComposition from printedName, ProtocolComposition "Any")

**Validation**: `./verify-fix-order.sh all` — 25 PASS, 0 FAIL, 1 WARN. Unit tests: 1603 passing. TestFramework: 61/61, 0 degraded. Generated Swift typechecks: 0 errors. C# build: 0 new errors (18 pre-existing known bugs).

---

## Full Validation After All Fixes

```bash
# 1. Unit tests pass
./run-tests.sh

# 2. TestFramework coverage doesn't regress
cd TestFramework && ./build-and-test.sh && ./generate-coverage-report.sh

# 3. CryptoSwift bindings regenerate clean
cd BindingTesting/CryptoSwift && ./regenerate-bindings.sh

# 4. Generated Swift typechecks (0 errors)
xcrun swiftc -typecheck \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -target arm64-apple-ios17.0-simulator \
  -F CryptoSwift.xcframework/ios-arm64_x86_64-simulator/ \
  output-ios/Swift.CryptoSwift.swift

# 5. C# compiles (0 new errors, 18 pre-existing known bugs)
./build-testapp.sh

# 6. Runtime tests pass
./validate-sim.sh 30
```
