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

## Step 7: Proxy/interface conformance alignment (Bugs #3, #11, #12)

**Unblocks**: Protocol proxy dispatch for protocols with closure parameters or generic returns

**Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs`
- Type-projection / TypeDatabase code paths

**Assertion**: Interface, proxy receive-dispatch, and vtable all agree on parameter/return type projection and skip decisions. If interface skips a method, proxy must also skip it.

**Tests**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs`
- `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests`

**Validation**: Regenerate → no `CS1503` or `CS1061` on proxy classes; `UpdatableProxy` and `CollectionProxy` compile clean

---

## Step 8: Wrapper extension filtering + cleanup (Bugs #14-17, #7, #8, #9)

**Unblocks**: Swift wrapper compilation (EveryProtocol + extensions), plus generic type edge cases

**Files**:
- Swift wrapper extension emission paths
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**Assertions**:
- No extensions on non-exported/internal/non-module types (Bugs #14, #15)
- No internal-member calls in generated wrappers (Bug #17)
- Correct argument labels on method wrappers (Bug #16)
- `SwiftSafeHandle<GenericType>` includes type parameters (Bug #7)
- Non-frozen generic types not treated as frozen/PayloadBuffer (Bug #8)
- No duplicate property emission for static + instance same name (Bug #9)

**Tests**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlersOutputTests.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerTests.cs`

**Validation**: Regenerate → `xcrun swiftc -typecheck Swift.CryptoSwift.swift` passes; `BatchedCollection<T0>` constructor compiles; no duplicate `Rabbit.KeySize`

---

## Full Validation After All Fixes

```bash
# 1. Unit tests pass
./run-tests.sh

# 2. TestFramework coverage doesn't regress
cd TestFramework && ./build-and-test.sh && ./generate-coverage-report.sh

# 3. CryptoSwift bindings regenerate clean
cd BindingTesting/CryptoSwift && ./regenerate-bindings.sh

# 4. C# compiles without stubs
./build-testapp.sh  # should be 0 errors without NotImplementedException stubs

# 5. Swift wrapper compiles
./build-swift-wrapper.sh  # should succeed with generated Swift.CryptoSwift.swift

# 6. Runtime tests pass
./validate-sim.sh 30
```
