# Witness Dispatch Emission Deduplication

**Created**: March 2026
**Priority**: P4 (cleanup) | **Effort**: Small-Medium (1 session) | **Risk**: Low

---

## Problem

The witness dispatch system (WitnessDispatchEmitter + ProtocolProxyEmitter) has grown organically as new dispatch kinds were added. Each dispatch kind (ExistentialReturn, ClassReturn, StructReturn, BoundGenericReturn) has its own emission methods that share 80-85% structural overlap.

### C# Side (ProtocolProxyEmitter.InterfaceImpl.cs)

Three method body emitters share near-identical scaffolding:

| Method | Lines | Unique Part |
|--------|-------|-------------|
| `EmitExistentialReturnMethodBody` | ~130 | `Unsafe.Read<Container>` + proxy construction |
| `EmitStructReturnMethodBody` | ~130 | `MarshalFromSwift<ConcreteType>` |
| `EmitCollectionReturnMethodBody` | ~130 | `MarshalFromSwift<SwiftArray<T>>` + conversion suffix |

Shared scaffolding (~80% of each):
1. `_csharpImpl` null-check delegation
2. `fixed (ExistentialContainer1* containerPtr ...)`
3. `EmitPinHandleDeclarations` + outer try/finally
4. `EmitMethodParameterMarshalling`
5. P/Invoke args list construction (`(IntPtr)containerPtr` + per-param slices)
6. Throwing branch: 25-line error handling block (identical across all three)
7. Non-throwing branch: try/finally with free
8. `EmitPinHandleCleanup`

The throwing error block (`SBW_GetErrorDescription` / `PtrToStringUTF8` / `SBW_Free` / `SBW_ReleaseError` / `SwiftException`) is duplicated 7 times total in the file.

### Swift Side (WitnessDispatchEmitter.cs)

Two method accessor emitters share near-identical scaffolding:

| Method | Lines | Unique Part |
|--------|-------|-------------|
| `EmitExistentialMethodAccessor` | ~110 | `any Protocol` type annotation, optional return branch |
| `EmitCollectionReturnMethodAccessor` | ~95 | Collection type string (`[String]`, `Set<Int>`) |

Shared scaffolding (~85% of each):
1. Swift parameter list construction (containerPtr + argPtrs + errorOut)
2. Nullable return declaration
3. `@_silgen_name` header emission
4. `containerPtr.load(as: (any ...).self)`
5. Parameter unmarshal loop
6. `BuildLabeledArgs`
7. Throwing do/catch with `Unmanaged.passRetained(error as AnyObject).toOpaque()`
8. Non-throwing allocate/initialize/return
9. Free function: `assumingMemoryBound(to:).deinitialize(count:)` + `deallocate()`

### Property Getter (Swift Side)

The property getter accessor template (allocate/initialize/return + free function) follows the same heap-allocation pattern as method accessors but is simpler (no parameters, no throwing). Three variants exist:
- `EmitCollectionReturnPropertyGetterAccessor`
- Existential property getter (inline in `EmitPropertyGetterAccessor`)
- Struct/Class property getters have different ABI (result buffer / Unmanaged)

---

## Proposed Refactoring

### 1. Extract `EmitHeapPointerMethodBody` (C# side)

```csharp
private void EmitHeapPointerMethodBody(
    CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
    WitnessDispatchEmitter dispatchEmitter,
    int methodIndex, string methodName, string argsString,
    List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
    Func<string, string> buildReturnExpr)  // ptrVar → return expression
```

All three method body emitters become thin wrappers:
- Existential: `buildReturnExpr = ptr => $"new {proxyClassName}(Unsafe.Read<{containerType}>((void*){ptr}))"`
- Struct: `buildReturnExpr = ptr => $"MarshalFromSwift<{concreteType}>({ptr})"`
- Collection: `buildReturnExpr = ptr => GetCollectionMarshalExpression(returnType, ptr)`

The optional existential variant adds a null check before the expression, which can be handled with an optional `nullCheckExpr` parameter.

### 2. Extract `EmitThrowingErrorCheck` (C# side)

```csharp
private void EmitThrowingErrorCheck(CSharpWriter writer)
```

The 25-line `SBW_GetErrorDescription` / `SwiftException` block is identical across all 7 call sites. Extracting it eliminates ~150 lines of duplication.

### 3. Extract `EmitHeapAllocatedSwiftAccessor` (Swift side)

```csharp
private void EmitHeapAllocatedSwiftAccessor(
    SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
    string moduleQualifiedName, int index,
    string swiftTypeName,          // e.g., "[String]" or "any Module.Protocol"
    bool isOptionalReturn = false) // for Optional<any Protocol>
```

Both existential and collection method accessors become one-line calls differing only in the type string and the optional flag.

### 4. Extract P/Invoke property getter pair helper (C# side)

The accessor + free P/Invoke declaration pair (IntPtr return + void free) is emitted identically for ExistentialReturn and BoundGenericReturn property getters. A small helper eliminates the copy.

---

## Scope and Constraints

- **No behavioral changes** — pure refactoring, output must be identical.
- **Verify with golden files** if available, otherwise `./run-tests.sh` + `./validate-libraries.sh`.
- The StructReturn and ClassReturn method body emitters have slightly different P/Invoke call shapes (StructReturn uses a result buffer, ClassReturn doesn't free). Only ExistentialReturn and BoundGenericReturn are true candidates for full unification. StructReturn can share the scaffolding but needs a different inner call pattern.
- The optional existential branch in `IsExistentialDispatchable` manually replicates `IsSupportedExistentialCore` gates with inverted semantics (reject vs accept well-known/Any). This could be consolidated by adding an `IsSupportedExistentialForProxyDispatch(ProtocolListTypeSpec)` method to `ProtocolExtensionEmitter` that applies the common gates plus the proxy-specific rejection of well-known/Any types.

---

## Estimated Impact

- ~300 lines removed (net) across InterfaceImpl.cs and WitnessDispatchEmitter.cs
- Error handling template goes from 7 copies to 1
- Future dispatch kinds (e.g., tuple returns, nested collection returns) would add ~10 lines instead of ~130
