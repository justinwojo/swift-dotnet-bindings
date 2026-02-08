---
globs: ["src/Swift.Bindings/src/Parser/**", "src/Swift.Bindings/src/Marshaler/**", "src/Swift.Bindings/src/Model/**"]
---

# Parser & Marshaler Patterns

## Overflow Operators (P0 Bug Pattern)
Operators `&<<`, `&>>`, `&<<=`, `&>>=`, `&+`, `&-`, `&*` MUST be in parser's `_operators` set to route through `CreateOperatorDecl` → `OperatorHandler.IsSupportedOperator()` rejection. Without this, they route to `CreateMethodDecl` and produce invalid C# identifiers.

## Internal Member Detection (`@usableFromInline internal`)
`IsNodeModuleInternal()` three-layer detection:
1. `UsableFromInline` present in DeclAttributes → always internal
2. `Inlinable` without `AccessControl` → internal
3. Swiftinterface cross-reference for ambiguous cases
Use `TypeDecl.IsModuleInternal` + `MethodDecl.IsModuleInternal` flags (NOT `Visibility.Internal` — breaks CS0737)

## ProtocolComposition ABI JSON
Nodes have NO children — protocols encoded solely in `printedName` (e.g., "any Cryptor & Updatable"). `CreateProtocolCompositionTypeSpec` parses `printedName` fallback.

## EveryProtocol Conformance Filtering
`IsMangledNameFromModule()` checks `$s{len}{module}` prefix in mangled name (TypeDatabase filter insufficient — stdlib protocols can have TypeRecords).

## Key Model Fields
- `MethodDecl.IsMutating` — parsed from `funcSelfKind` in ABI JSON
- `MethodDecl.UsesWrapperLibrary` — routes P/Invoke to wrapper lib
- `SwiftInterfaceAccessParser` — CLI option `-s`/`--swiftinterface`, detects `@inlinable internal` members

## ArraySlice Normalization Scope Guards
Skip normalization for: accessor, constructor, mutating struct, generic, inout ArraySlice, closure/tuple/optional containing ArraySlice, internal method/type.

## Hash Functions
`DeterministicHash8()` uses FNV-1a (not `string.GetHashCode()` which is non-deterministic across processes).
