# GRDB Swift Compile Fix — Session Plan

**Goal**: Get GRDB swift_compile passing (61/61 validation). Currently 236 errors from 11 unique error types.

**Reproduce**:
```bash
VALDIR="/var/folders/7y/3slh2cfs72s0lpx7nxzlwktc0000gn/T/binding-validation-main/GRDB"
rm -rf "$VALDIR"
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework .libraries/GRDB/GRDB.xcframework -o "$VALDIR/"
SRCFW=".libraries/GRDB/GRDB.xcframework/ios-arm64_x86_64-simulator"
xcrun swiftc -typecheck -target arm64-apple-ios15.0-simulator \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F "$SRCFW" "$VALDIR/GRDB.Wrapper.swift" 2>&1 | grep 'error:' | \
  sed 's/.*error: //' | sort | uniq -c | sort -rn
```

---

## Session 1: Generic constraint propagation (184 errors)

### Problem

Wrapper functions for methods on generic types declare generic parameters but omit the parent type's `where` constraints. Swift can't verify the generic parameter meets the type's requirements.

### Error breakdown

| Error | Count | Example type |
|---|---|---|
| `generic parameter 'U' could not be inferred` | 88 | `EnumeratedCursor<Base>` — `U` comes from `Cursor` conformance |
| `type 'Base' does not conform to protocol 'Cursor'` | 64 | `DropFirstCursor<Base: Cursor>` |
| `type 'Value' does not conform to 'DatabaseValueConvertible'` | 16 | `DatabaseValueCursor<Value: DatabaseValueConvertible>` |
| `type 'Record' does not conform to 'FetchableRecord'` | 8 | `RecordCursor<Record: FetchableRecord>` |

### Example

Generated (broken):
```swift
@_silgen_name("SBW_DropFirstCursor_enumerated")
public func SBW_DropFirstCursor_enumerated<Base>(
    _ self_: UnsafeMutableRawPointer, _ __baseType: Base.Type
) -> UnsafeMutableRawPointer {
    let instance = unsafeBitCast(self_, to: GRDB.DropFirstCursor<Base>.self)
    // ERROR: type 'Base' does not conform to protocol 'Cursor'
    let result = instance.enumerated()
    return Unmanaged.passRetained(result as AnyObject).toOpaque()
}
```

Should be:
```swift
@_silgen_name("SBW_DropFirstCursor_enumerated")
public func SBW_DropFirstCursor_enumerated<Base>(
    _ self_: UnsafeMutableRawPointer, _ __baseType: Base.Type
) -> UnsafeMutableRawPointer where Base: GRDB.Cursor {
    let instance = unsafeBitCast(self_, to: GRDB.DropFirstCursor<Base>.self)
    let result = instance.enumerated()
    return Unmanaged.passRetained(result as AnyObject).toOpaque()
}
```

### Root cause

The where clause builder at `WrapperEmitter.Async.cs:751-767` only looks at `_env.MethodDecl.GenericParameters` conformances. For inherited generic parameters (from the parent type), the conformances may not be populated on the method-level `GenericParameters` — they exist on the parent `TypeDecl.GenericParameters` instead.

### Key code paths to investigate

1. **Where clause emission** — `WrapperEmitter.Async.cs:745-770`: Builds `<T>` and `where T: Protocol` from `_env.MethodDecl.GenericParameters`. Check if parent type constraints flow into method-level generic params.

2. **GenericArgumentDecl model** — `Model/TypeDecl/GenericArgumentDecl.cs`: Has `GenericConformances` and `AssosiatedTypeConformances`. Verify the parent's constraints populate here.

3. **GenericSignatureParser** — `Parser/GenericSignatureParser.cs`: Parses `GenericSig` from ABI JSON. For methods on generic types, the ABI JSON includes the parent's generic signature in each method's `GenericSig`. Check if the parser correctly propagates parent constraints.

4. **Method-level vs type-level generics** — `WrapperValidation.HasMethodOwnGenericParameters()` at `WrapperValidation.cs:315-327` distinguishes parent-inherited from method-own. The constraint propagation logic may need the same distinction to merge parent constraints.

5. **Other wrapper emitters** — `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `GenericProtocolEmitter.cs` may also build generic function signatures. Search for `GenericParameters.*Select.*SugaredTypeName` and `where.*clause` patterns across the emitter.

### Fix strategy

- Check if `_env.MethodDecl.GenericParameters[i].GenericConformances` is empty for inherited params
- If so, merge constraints from `_env.ParentDecl.GenericParameters` when building the where clause
- The parent `TypeDecl.GenericParameters` should have the constraints (e.g., `Record: FetchableRecord`)
- Ensure module-qualified conformance names (e.g., `GRDB.Cursor` not just `Cursor`)

### Verification

After fixing, the 88+64+16+8 = 176 errors should resolve (8 of the 88 `U` errors may have other causes — verify by re-running the compiler).

---

## Session 2: Remaining bugs (52 errors)

### Bug 2: Internal type `RowKey` leaking (16 errors)

`GRDB.RowKey` is internal but the generator emits enum case factories and wrappers referencing it.

```swift
@_cdecl("SBW_GRDB_RowKey_columnName_1F76D9C0")
public func _sbw_case_columnName_14D37876(...) {
    let result = GRDB.RowKey.columnName(value0Val)
    // ERROR: module 'GRDB' has no member named 'RowKey'
}
```

**Root cause**: `IsModuleInternal` detection misses this type. Check `SwiftABIParser.IsNodeModuleInternal()` and `IsInternalFromPublicTypeNames()`. The type likely lacks the usual `UsableFromInline`/`Inlinable` markers but is still in the ABI JSON (possibly because it's `@frozen` or has public conformances). May also need swiftinterface cross-reference.

**Fix**: Find why RowKey passes the internal detection and add the appropriate gate.

### Bug 3: Missing argument label in constructor call (32 errors)

Generic static factory pattern (`_SBW_GSF_`) drops the argument label for the last parameter.

```swift
// Protocol declaration has: _ arguments: StatementArguments
// But the call site is:
let result = Self(recursive: recursive, named: tableName, columns: columns,
                  sql: sql, arguments)  // Should be: arguments: arguments
```

**Root cause**: In `GenericStaticFactoryEmitter` (or wherever `_SBW_GSF_` is emitted), the last parameter's argument label is being treated as `_` (unlabeled) when it shouldn't be. The protocol declaration correctly has `_ arguments:` (external label `_`, internal label `arguments`), but the call site should use `arguments: arguments` since the Swift constructor expects the `arguments:` label.

**Fix**: Check how `_SBW_GSF_` call arguments are built. The issue is that a parameter with external label `_` in the wrapper is being called without its original constructor label. The wrapper should use the original constructor's parameter label in the call, not the wrapper's external label.

### Bug 4a: Throwing property getter not wrapped in try (4 errors)

```swift
@_cdecl("SBW_Get_GRDB_Record_databaseChanges")
public func _sbw_get_databaseChanges_CA0885F1(...) {
    let obj = Unmanaged<GRDB.Record>.fromOpaque(self_).takeUnretainedValue()
    let result = obj.databaseChanges  // ERROR: property access can throw
}
```

Properties `Record.databaseChanges` and `Database.transactionDate` are throwing getters. The property wrapper emitter doesn't detect throws on property access.

**Fix**: Check `PropertyDecl.Throws` or accessor-level throws flag. If the getter throws, either wrap in `try` (and add `do/catch` + error callback like method wrappers) or gate out the property.

### Bug 4b: Int64 vs Int type mismatch (2 errors)

```swift
// Line 2454: closure callback returns Int64, but ComparisonResult(rawValue:) expects Int
return ComparisonResult(rawValue: cdecl_function(__heap_0, __heap_1, functionContext!))!
```

Closure callback declares `Int64` return but `ComparisonResult.rawValue` is `Int` (platform-dependent size). This is a type marshalling issue in the closure emit path.

**Fix**: Add explicit `Int(...)` cast in `ClosureEmitter` when the closure return type doesn't match the expected Swift type.

### Bug 4c: Int used where OpaquePointer expected (2 errors)

```swift
// Witness dispatch for StatementBinding.bind(to:at:)
let arg0 = arg0Ptr.load(as: Int.self)  // Should be OpaquePointer (SQLiteStatement)
```

`SQLiteStatement` is a typealias for `OpaquePointer`, but the witness dispatch loads it as `Int`. Type resolution doesn't resolve the typealias.

**Fix**: This may require TypeDatabase awareness of typealiases, or special-casing `SQLiteStatement` → `OpaquePointer`. Check how the type is represented in ABI JSON.

### Bug 4d: Missing @available annotation (2 errors)

```swift
@_cdecl("SBW_Get_GRDB_TableOptions_strict")
public func _sbw_get_strict_4AE56D95(...) {
    let result = GRDB.TableOptions.strict
    // ERROR: 'strict' is only available in iOS 15.4 or newer
}
```

`WrapperEmitterHelpers.EmitCdeclAnnotation` already supports availability annotations, but this property wrapper isn't passing them. The `AvailabilityAnnotation` on the property or member isn't flowing to the wrapper.

**Fix**: Check `PropertyWrapperEmitter` — it may not be calling `EmitCdeclAnnotation` with the property's availability annotations.

### Bug 4e: Internal member `_checkIndex` emitted (2 errors)

```swift
@_silgen_name("DBG_Row__checkIndex_77BCFDEE")
public func _dbg__checkIndex_77BCFDEE(_ index: Int) -> () {
    return self._checkIndex(index)  // ERROR: inaccessible due to 'internal'
}
```

`Row._checkIndex` is internal but a default-parameter overload wrapper (`DBG_` prefix) was generated. The default-parameter emitter doesn't check member visibility.

**Fix**: Add `IsModuleInternal` check in `DefaultParameterOverloadEmitter` before emitting overloads.

---

## Summary

| Bug | Errors | Sessions | Complexity |
|---|---|---|---|
| Generic constraint propagation | 184 | Session 1 | Medium — data exists, needs plumbing |
| Missing argument label | 32 | Session 2 | Low-medium — call site label logic |
| Internal type RowKey | 16 | Session 2 | Low — detection gap |
| Throwing property getter | 4 | Session 2 | Low — gate or try/catch |
| Type mismatches (Int64, OpaquePointer) | 4 | Session 2 | Low — cast/typealias |
| Availability annotation | 2 | Session 2 | Low — plumbing |
| Internal member _checkIndex | 2 | Session 2 | Low — visibility check |
| **Total** | **236** | **~2 sessions** | |
