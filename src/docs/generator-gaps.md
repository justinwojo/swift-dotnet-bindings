# Generator Gaps

Binding coverage gaps exposed by the TestFramework v2.0 comprehensive test library (67 Swift files, 145 features tracked). These are generator-side issues where the test file exists and compiles but the emitter skips members due to unsupported type patterns.

**Date**: February 2026
**Starting Point**: Phase 44 complete, 1032 unit tests passing, TestFramework v2.0 complete
**Baseline**: 85/93 must-pass features passing, **8 degraded** (skipped binding members)

---

## Status Summary

| Task | Description | Degraded Features Fixed | Priority |
|------|-------------|------------------------|----------|
| 1 | Unbound generic type parameters (`AnyTypeFallback`) | 4 | P1 |
| 2 | OpaquePointer in method signatures | 2 | P2 |
| 3 | NSObject subclass as method parameter | 1 | P2 |
| 4 | Existential type argument in bound generic | 1 | P3 |

**Target**: 93/93 must-pass features passing (0 degraded)

---

## Task 1: Unbound Generic Type Parameters

### Priority: P1 (fixes 4 degraded features, 12 skipped members)
### Dependencies: None

### Problem Statement

Generic structs, classes, and functions with unbound type parameters have their properties and methods skipped. The marshaler resolves generic type parameters to `Swift.AnyType` and then the emitter rejects them as `AnyTypeFallback` or `UnsupportedSignature`.

### Affected Features

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `generic_function` | `Generics/Functions.swift` | `pair` (returns generic tuple) |
| `generic_struct` | `Generics/Types.swift` | `Wrapper.wrapped` (property), `Wrapper.init`, `GenericPair.first`, `GenericPair.second`, `GenericPair.init`, `GenericPair.swapped` |
| `generic_class` | `Generics/Types.swift` | `GenericClass.value` (property), `GenericClass.init` |
| `where_clause` | `Generics/Constraints.swift` | `ConstrainedBox.item` (property), `ConstrainedBox.init` |

### Skip Reasons

- **`AnyTypeFallback`**: Property type resolved to `AnyType` — the marshaler doesn't know the concrete type for the generic parameter, so it falls back to `Swift.AnyType` which has no C# mapping.
- **`UnsupportedSignature`**: Constructor or method signature contains an unresolved placeholder type (same root cause).

### Investigation Areas

- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — How generic type parameters are resolved during marshalling
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` — Whether unbound generic params are registered
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — Where `AnyTypeFallback` rejection happens
- Look at how **bound** generics (e.g., `BoundIntPair`) succeed — those work because the type parameter is concretized

### Acceptance Criteria

- [ ] `Wrapper<T>`, `GenericPair<T, U>`, `GenericClass<T>`, `ConstrainedBox<T>` emit properties and constructors
- [ ] `pair<T>()` function emits
- [ ] TestFramework coverage report: `generic_function`, `generic_struct`, `generic_class`, `where_clause` all show `passing`
- [ ] Existing unit tests still pass

---

## Task 2: OpaquePointer in Method Signatures

### Priority: P2 (fixes 2 degraded features, 3 skipped members)
### Dependencies: None

### Problem Statement

Methods that accept or return `OpaquePointer` (or `Optional<OpaquePointer>`) are skipped with `UnsupportedSignature`. The marshaler doesn't recognize `OpaquePointer` as a marshalable type.

### Affected Features

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `opaque_pointer` | `UnsafeTypes/OpaquePointer.swift` | `opaquePointerIsValid` (free function), `HandleWrapper.describe` (method) |
| `optional_opaque_pointer` | `UnsafeTypes/OpaquePointer.swift` | `optionalOpaquePointer` (free function) |

### Expected Mapping

`OpaquePointer` should map to `IntPtr` in C# (same as other unsafe pointer types). `Optional<OpaquePointer>` should map to `IntPtr` (null = `IntPtr.Zero`).

### Investigation Areas

- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — Check if `OpaquePointer` is in the type mapping table
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — How `OpaquePointer` appears in ABI JSON
- Compare with `UnsafePointer<T>` / `UnsafeMutablePointer<T>` handling (those work via `IntPtr`)

### Acceptance Criteria

- [ ] `opaquePointerIsValid`, `HandleWrapper.describe`, `optionalOpaquePointer` emit correctly
- [ ] OpaquePointer parameters/returns map to `IntPtr`
- [ ] TestFramework coverage report: `opaque_pointer` and `optional_opaque_pointer` show `passing`

---

## Task 3: NSObject Subclass as Method Parameter

### Priority: P2 (fixes 1 degraded feature, 1 skipped member)
### Dependencies: None

### Problem Statement

Free functions that take an NSObject subclass as a parameter are skipped with `UnsupportedSignature`. The NSObject subclass types themselves emit correctly — the issue is only when they appear as function parameters.

### Affected Features

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `nsobject_as_parameter` | `ObjCInterop/NSObjectSubclass.swift` | `describeNSObject` (free function taking `SimpleNSObject`) |

### Investigation Areas

- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — How NSObject subclass types are marshalled as parameters
- The `SimpleNSObject` type itself emits fine (it's a class with SafeHandle), so the issue is in parameter resolution for functions, not type emission
- Check if the marshaler recognizes `SimpleNSObject` as a class type when it appears as a parameter in a free function (vs. as `self` in a method)

### Acceptance Criteria

- [ ] `describeNSObject` free function emits with `SimpleNSObject` parameter
- [ ] TestFramework coverage report: `nsobject_as_parameter` shows `passing`

---

## Task 4: Existential Type Argument in Bound Generic

### Priority: P3 (fixes 1 degraded feature, 1 skipped member)
### Dependencies: None

### Problem Statement

Methods with a bound generic that contains an existential type argument (e.g., `[any Describable]` which is `Array<any Describable>`) are skipped with `UnsupportedExistential`.

### Affected Features

| Feature | File | Skipped Members |
|---------|------|-----------------|
| `any_protocol_existential` | `Generics/Existentials.swift` | `describeAll` (takes `[any Describable]` parameter) |

### Investigation Areas

- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — How existential types inside bound generics are resolved
- This is related to the Mono JIT existential metadata bug (see `known-issues-workarounds.md`) but the issue here is at the generator level — the emitter rejects the signature before it even gets to runtime
- `SwiftArray<ExistentialContainer>` is the runtime representation; the emitter needs to know how to marshal it

### Acceptance Criteria

- [ ] `describeAll` function emits (may require `[UnsupportedSwiftType]` annotation if runtime is blocked by Mono JIT bug)
- [ ] TestFramework coverage report: `any_protocol_existential` shows `passing` (or remains `degraded` with documented runtime limitation)

---

## Verification

After any task is completed:

```bash
# Rebuild and check
cd TestFramework
./build-and-test.sh
./generate-coverage-report.sh

# Check degraded count decreased
python3 -c "
import json
with open('output/coverage-matrix.json') as f:
    d = json.load(f)
mp = d['summary']['must_pass']
print(f'Must-pass: {mp[\"passing\"]}/{mp[\"total\"]} passing, {mp[\"degraded\"]} degraded')
for f in d['features']:
    if f.get('test_status') == 'degraded':
        print(f'  {f[\"name\"]}: {len(f.get(\"binding_skips\",[]))} skips')
"
```

---

## Notes

- All 8 degraded features are generator-side issues, not test library gaps
- Task 1 (unbound generics) has the highest impact — 4 features and 12 skipped members
- Tasks 2 and 3 are likely straightforward type-mapping additions
- Task 4 may be partially blocked by the Mono JIT existential bug at runtime even if the generator is fixed
- The comprehensive test library design doc has been archived to `src/docs/CompletedPhases/comprehensive-test-library-design.md`
- Previous Phase 43 task specs archived to `src/docs/CompletedPhases/codex-task-specs-phase43.md`
