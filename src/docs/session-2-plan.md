# Session 2: Swiftinterface + Actor Isolation + Marker Protocols

**Implementation Plan**
**Created**: February 2026
**Scope**: 4 sub-tasks, ~1 session
**Libraries improved**: BlinkIDUX, SnapKit, BlinkID, Stripe, CryptoSwift, MicroblinkPlatform, Mappedin

---

## Executive Summary

Session 2 extends the existing `.swiftinterface` parsing infrastructure with three new capabilities: (1) type-level public/internal classification to filter noise, (2) `@MainActor` / actor annotation extraction for correct Swift wrapper compilation, and (3) marker protocol detection for generating typed convenience overloads. These are independent of Sessions 3/4 with minimal merge conflict risk.

**Key insight**: The ABI JSON does NOT represent `@MainActor` at all (actors appear as `kind: "Class"`, `@MainActor` is omitted from `declAttributes`). The `.swiftinterface` file is the ONLY source for actor isolation information. This makes sub-task 2b essential rather than supplementary.

---

## Sub-task 2a: Access-Level Filtering (Type-Level)

### Problem

Currently, the generator only filters **members** that are internal (via `SwiftInterfaceAccessParser.GetInternalMembers()`). Types that are `@usableFromInline internal` or `@inlinable internal` are correctly filtered by `IsNodeModuleInternal()` in `SwiftABIParser.cs`. However, types that appear in ABI JSON but NOT in the `.swiftinterface` (because they're internal) are still emitted as public C# types.

The current workaround relies on ABI JSON flags (`IsInternal`, `UsableFromInline`, `Inlinable`) which are reliable for most cases. The gap is types that have `AccessControl` + `Inlinable` in ABI (ambiguous case) — the same ambiguity that already exists for members.

Additionally, `_`-prefixed types that appear in public API but represent internal implementation details (e.g., `_SkeletonLayer`, `_RTLLayoutConstraintAttribute`) pollute IntelliSense.

### Approach

Add a new `SwiftInterfaceAccessParser.GetPublicTypeNames()` method that returns all type names declared as `public` or `open` in the swiftinterface. Then, during ABI parsing, cross-reference types against this set. Types NOT in the set (when swiftinterface data is available) are marked with `IsModuleInternal = true`.

For the heuristic fallback (when no swiftinterface is available), apply `[EditorBrowsable(EditorBrowsableState.Never)]` to types whose names start with `_`.

### Files to Modify

**1. `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs`**

Add new method `GetPublicTypeNames()` (approximately after line 148):

```csharp
/// <summary>
/// Parses a .swiftinterface file and returns a set of dot-qualified type paths
/// declared as public or open (e.g., "OrderContainer.Status" for nested types,
/// "ConstraintMaker" for top-level types).
/// Types NOT in this set are internal to the module.
/// </summary>
public static HashSet<string> GetPublicTypeNames(string swiftInterfacePath)
```

Implementation: Walk lines using brace-depth tracking (same pattern as `GetInternalMembers`). Maintain a `typeStack` of type names to build dot-qualified paths for nested types, matching the format used by `SwiftABIParser.BuildTypeQualifiedPath()` (line 662). For example, `Status` inside `OrderContainer` produces `"OrderContainer.Status"`.

The regex at line 29 already captures `(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)` — filter for those prefixed with `public` or `open`. Note: must also add `actor` to the TypeDeclRegex if not already there (check: current regex at line 30 is `(?:class|struct|enum|actor|protocol)` — `actor` IS already included).

**Cross-referencing in SwiftABIParser**: Use `BuildTypeQualifiedPath(typeDecl)` (line 662) to produce the lookup key, NOT `typeDecl.Name`. This ensures nested types like `OrderContainer.Status` match correctly against the qualified set. Extension target names from the swiftinterface (e.g., `extension Swift.Int : ...`) should NOT be added to this set — they are external module types, not types defined in this module.

**2. `src/Swift.Bindings/src/Parser/SwiftABIParser.cs`**

- Add new constructor parameter: `HashSet<string>? publicTypeNames = null` (line ~216)
- Store as `private readonly HashSet<string>? _publicTypeNames;`
- In `ParseClass` (around line 493), `ParseStruct` (around line 529), `ParseEnum`, and `ParseProtocol` (around line 706): if `_publicTypeNames` is non-null and non-empty, check if the type name is in the set. If NOT, set `IsModuleInternal = true`.

**3. `src/Swift.Bindings/src/Program.cs`**

- Around line 682, after `GetInternalMembers`: add `publicTypeNames = SwiftInterfaceAccessParser.GetPublicTypeNames(swiftInterfacePath);`
- Thread `publicTypeNames` through to `SwiftABIParser` constructor (line 708)

**4. Heuristic fallback: `_`-prefix suppression**

In the emitter files that emit type declarations (ClassHandler, FrozenStructHandler, NonFrozenStructHandler, EnumHandler, ProtocolHandler), add `[EditorBrowsable(EditorBrowsableState.Never)]` before the type declaration when the type name starts with `_`. This is a small addition to each handler's `Emit` method:

- `ClassHandler.cs` — before class declaration (around line 150)
- `FrozenStructHandler.cs` — before struct declaration
- `NonFrozenStructHandler.cs` — before class declaration
- `EnumHandler.cs` — before enum/class declaration
- `ProtocolHandler.cs` — before interface declaration

### Complexity / Risk

- **Complexity**: Low-Medium. The parser infrastructure exists; adding a new extraction method follows the existing pattern.
- **Risk**: Low. The `IsModuleInternal` flag already propagates correctly through the emit pipeline. Types marked internal are skipped by `CollectInternalTypeNames` which strips Swift wrapper references, and by `DefaultParameterOverloadEmitter` which skips overloads for internal types.
- **Regression risk**: If `GetPublicTypeNames` is too aggressive (misses a public type), that type would be suppressed. Mitigation: only apply when swiftinterface is available AND the set is non-empty. Empty set means no filtering (defensive default).

### Tests

- Add `SwiftInterfaceAccessParserTests.GetPublicTypeNames_ReturnsPublicTypes` — test with a swiftinterface containing public and internal types
- Add `SwiftInterfaceAccessParserTests.GetPublicTypeNames_HandlesActors` — ensure `public actor BlinkIDAnalyzer` is detected
- Add `SwiftInterfaceAccessParserTests.GetPublicTypeNames_ExcludesExtensionTargets` — ensure external extension targets (e.g., `extension Swift.Int : ...`) do NOT appear in set
- Add integration test validating BlinkIDUX emits fewer internal types

---

## Sub-task 2b: Parse `@MainActor` from Swiftinterface

### Problem

The ABI JSON represents actors as `Class` (with `declKind: "Class"`) and does NOT include `@MainActor` in `declAttributes`. The `Custom` attribute appears for `@MainActor` types but is generic (any custom attribute produces `Custom`). The `.swiftinterface` is the ONLY reliable source for:

1. **Type-level `@MainActor`**: `@_Concurrency.MainActor final public class Camera` or `@_Concurrency.MainActor public class ScanningViewModel<T, U>`
2. **Member-level `@MainActor`**: `@_Concurrency.MainActor @preconcurrency public var snp: ...`
3. **Actor declarations**: `public actor BlinkIDAnalyzer` (implicit actor isolation)
4. **`nonisolated` members**: `nonisolated public var sessionNumber: Swift.Int` (opt-out from actor isolation)
5. **`@preconcurrency`**: Used with `@MainActor` for backwards compatibility

### Approach

Add four new parser methods to `SwiftInterfaceAccessParser`:

1. `GetMainActorTypes()` — returns set of type names annotated with `@MainActor` / `@_Concurrency.MainActor`
2. `GetCustomActorTypes()` — returns set of type names declared with the `actor` keyword
3. `GetActorIsolatedMembers()` — returns set of `TypeName.memberName` keys for members with `@MainActor` annotation
4. `GetNonisolatedMembers()` — returns set of `TypeName.memberName` keys for members declared `nonisolated`

Then thread this data through to the emitters.

### Files to Modify

**1. `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs`**

Add two new methods (after `GetPublicTypeNames`):

```csharp
/// <summary>
/// Returns a set of type names annotated with @MainActor / @_Concurrency.MainActor.
/// Does NOT include custom actor declarations (those need different wrapper treatment).
/// </summary>
public static HashSet<string> GetMainActorTypes(string swiftInterfacePath)

/// <summary>
/// Returns a set of type names declared with the 'actor' keyword (custom actors).
/// Custom actors have implicit isolation to their own executor, NOT MainActor.
/// </summary>
public static HashSet<string> GetCustomActorTypes(string swiftInterfacePath)
```

Implementation approach:
- Walk lines with a **pending-attribute accumulator**: when a line contains `@_Concurrency.MainActor` but does NOT contain a declaration keyword (`class`/`struct`/`enum`/`actor`/`protocol`/`func`/`var`/`let`), push the `@MainActor` flag into a `pendingMainActor` boolean. When the next line contains the declaration, combine the pending attribute with the declaration line.
- In practice, real `.swiftinterface` files emit `@_Concurrency.MainActor` on the SAME line as the declaration (confirmed against BlinkIDUX, BlinkID, StripePaymentSheet). But attributes like `@available(...)` can span lines and push `@MainActor` to a separate line in edge cases. The pending-attribute pattern handles both.
- For type declarations, check if the current or pending line contains `@_Concurrency.MainActor` or `@MainActor`, or if the declaration uses `actor` keyword.
- **Always match the fully-qualified form** `@_Concurrency.MainActor` — this is what Swift emits in `.swiftinterface` files. Also match bare `@MainActor` defensively.
- Add to set of actor-isolated type names (using dot-qualified paths from typeStack, same as `GetPublicTypeNames`).
- Track nesting: if `extension FooType {` and FooType is actor-isolated, members inherit isolation.

```csharp
/// <summary>
/// Returns a set of "TypeName.memberName" keys for members that are individually
/// @MainActor-annotated (when the containing type is NOT globally @MainActor).
/// </summary>
public static HashSet<string> GetActorIsolatedMembers(string swiftInterfacePath)
```

This is similar to `GetInternalMembers` but detects `@_Concurrency.MainActor` or `@MainActor` prefix on member declarations (including `func`, `var`, `let`, and `init`). Uses the same pending-attribute accumulator to handle annotations split across lines.

Also parse `nonisolated` members (members that opt out of their type's global isolation):

```csharp
/// <summary>
/// Returns a set of "TypeName.memberName" keys for members declared as nonisolated.
/// These members opt out of their containing type's actor isolation.
/// </summary>
public static HashSet<string> GetNonisolatedMembers(string swiftInterfacePath)
```

**Regex additions** (new private static fields):

```csharp
private static readonly Regex MainActorAnnotationRegex = new(
    @"@(?:_Concurrency\.)?MainActor", RegexOptions.Compiled);

private static readonly Regex ActorDeclRegex = new(
    @"(?:public|internal|open)\s+actor\s+(\w+)", RegexOptions.Compiled);

private static readonly Regex NonisolatedRegex = new(
    @"nonisolated\s+(?:public|final|var|let|func|static)", RegexOptions.Compiled);
```

**2. `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs`** (line ~63)

Add new properties:

```csharp
/// <summary>
/// Whether this type is annotated with @MainActor.
/// When true, generated Swift wrapper functions must include @MainActor annotation.
/// </summary>
public bool IsMainActorIsolated { get; set; } = false;

/// <summary>
/// Whether this type is declared with the 'actor' keyword (custom actor).
/// Custom actors dispatch to their own executor — wrappers do NOT get @MainActor,
/// but the existing async wrapper pattern (Task {}) already handles dispatch.
/// </summary>
public bool IsCustomActor { get; set; } = false;
```

**3. `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs`**

Add new properties:

```csharp
/// <summary>
/// Whether this method is @MainActor-isolated (individually annotated, not inherited).
/// </summary>
public bool IsActorIsolated { get; set; } = false;

/// <summary>
/// Whether this method is declared nonisolated (opts out of containing type's isolation).
/// </summary>
public bool IsNonisolated { get; set; } = false;
```

**3b. `src/Swift.Bindings/src/Model/TypeDecl/PropertyDecl.cs`**

Add matching properties to `PropertyDecl` so property-level isolation is accessible to emitters without needing to re-derive it from the accessor MethodDecl:

```csharp
/// <summary>
/// Whether this property is @MainActor-isolated (individually annotated, not inherited).
/// </summary>
public bool IsActorIsolated { get; set; } = false;

/// <summary>
/// Whether this property is declared nonisolated (opts out of containing type's isolation).
/// </summary>
public bool IsNonisolated { get; set; } = false;
```

**4. `src/Swift.Bindings/src/Parser/SwiftABIParser.cs`**

- Add constructor parameters: `HashSet<string>? mainActorTypes`, `HashSet<string>? customActorTypes`, `HashSet<string>? actorIsolatedMembers`, `HashSet<string>? nonisolatedMembers`
- In `ParseClass` / `ParseStruct` / `ParseEnum`: set `IsMainActorIsolated = true` if type name is in `mainActorTypes` set; set `IsCustomActor = true` if in `customActorTypes` set
- In method parsing (around line 827): set `IsActorIsolated = true` / `IsNonisolated = true` based on member keys
- **In property accessor creation** (`CreateGetAccessor` at line 986, `CreateSetAccessor` at line 1041): these methods create `MethodDecl` objects directly — NOT via `CreateMethodDecl`. Must propagate actor isolation to the accessor's `MethodDecl` here too. Lookup key is `"{TypeName}.{fieldName}"` matching the swiftinterface member key format. Set `methodDecl.IsActorIsolated` / `methodDecl.IsNonisolated` on the accessor `MethodDecl` before returning.
- In `CreatePropertyDecl` (line 1102): set `IsActorIsolated` / `IsNonisolated` on the `PropertyDecl` itself (add properties to PropertyDecl model). This ensures both the property and its accessor methods carry the isolation flag.

**5. `src/Swift.Bindings/src/Program.cs`**

- After other swiftinterface parsing (around line 689): call new parser methods
- Thread results to `SwiftABIParser` constructor

### Complexity / Risk

- **Complexity**: Medium. Four new parser methods (but all follow the existing pattern). Model additions are trivial.
- **Risk**: Low. This sub-task only adds data extraction — it doesn't change any emission behavior. Sub-task 2c uses this data.

### Tests

- `SwiftInterfaceAccessParserTests.GetMainActorTypes_DetectsMainActorClass` — `@_Concurrency.MainActor final public class Camera`
- `SwiftInterfaceAccessParserTests.GetMainActorTypes_DetectsNestedMainActor` — `@_Concurrency.MainActor public class ScanningViewModel<T, U>`
- `SwiftInterfaceAccessParserTests.GetMainActorTypes_ExcludesCustomActors` — `public actor BlinkIDAnalyzer` should NOT be in MainActor set
- `SwiftInterfaceAccessParserTests.GetCustomActorTypes_DetectsActorDecl` — `public actor BlinkIDAnalyzer`
- `SwiftInterfaceAccessParserTests.GetActorIsolatedMembers_DetectsMemberAnnotation` — `@_Concurrency.MainActor @preconcurrency public var snp`
- `SwiftInterfaceAccessParserTests.GetNonisolatedMembers_DetectsNonisolated` — `nonisolated public var sessionNumber: Swift.Int`

---

## Sub-task 2c: Emit Actor Isolation on Swift Wrappers

### Problem

When the generator creates Swift wrapper functions (for async, opaque return, closures, etc.), these wrappers are compiled as a separate Swift source file. If the original Swift type is `@MainActor`, calling its methods from a non-isolated context triggers Swift 6 concurrency errors:

```
error: main actor-isolated instance method 'analyze()' can not be referenced from a nonisolated context
```

The current workaround is the `SwiftWrapperPostProcessor` stripping these broken wrapper functions, which means the corresponding C# methods are also lost. For BlinkIDUX, this strips most of the interesting API.

### Approach

When emitting Swift wrapper code (async wrappers, opaque return wrappers, closure Cdecl wrappers), check if the wrapper needs `@MainActor`. This is true when: (a) the parent type has `IsMainActorIsolated` (type-level `@MainActor`), OR (b) the method/property has `IsActorIsolated` (member-level `@MainActor`), AND the member is NOT `IsNonisolated`. Custom actor types (`IsCustomActor`) do NOT trigger `@MainActor` — their dispatch is handled by the existing async `Task {}` pattern. If the wrapper needs `@MainActor`:

1. For **extension-based wrappers** (async instance methods): add `@MainActor` to the function declaration inside the extension
2. For **free-function wrappers** (non-extension async): add `@MainActor` to the function declaration
3. For **nonisolated members**: do NOT add `@MainActor` even if the containing type is isolated

### Files to Modify

**1. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs`**

The critical change is in `BuildSwiftAsyncWrapperCode()` (line 1236). The Swift function template is generated at line 1282:

```swift
{{i}}@_silgen_name("{{mangledName}}")
{{i}}public {{staticModifier}}func {{pInvokeName}}...
```

When the parent type is `@MainActor` and the method is NOT nonisolated, add `@MainActor` annotation before `@_silgen_name`:

```swift
{{i}}@MainActor
{{i}}@_silgen_name("{{mangledName}}")
{{i}}public {{staticModifier}}func {{pInvokeName}}...
```

Specific changes:
- Add parameter `bool needsMainActor` to `BuildSwiftAsyncWrapperCode` (line 1236)
- In `EmitAsync` method (line 15), resolve whether wrapper needs `@MainActor`:
  ```csharp
  bool needsMainActor = ((_env.ParentDecl as TypeDecl)?.IsMainActorIsolated == true
      || _env.MethodDecl.IsActorIsolated)   // member-level @MainActor on non-actor type
      && !_env.MethodDecl.IsNonisolated;
  ```
  Note: `TypeDecl.IsCustomActor` is intentionally NOT checked here — custom actors use their own executor, not MainActor. The existing async wrapper `Task {}` pattern already handles custom actor dispatch.
- Thread `needsMainActor` to `BuildSwiftAsyncWrapperCode`
- In the template string (line 1282), conditionally emit `{{i}}@MainActor\n` before `@_silgen_name` when `needsMainActor` is true

**2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs`**

This is where `EmitOpaqueReturnWrapper()` lives (line 16). It emits Swift `@_silgen_name` wrapper functions that call the original method and box `some Protocol` returns into `any Protocol` existentials. The wrapper uses `extension TypeName { }` blocks for instance methods (line 94) and free functions for module-level calls (line 122).

Add `@MainActor` annotation before `@_silgen_name` in both the extension path (line 96) and the free function path (line 123) when the parent type is actor-isolated and the method is not nonisolated. Also handle property getter wrappers (line 88-101) which use the same pattern.

Resolve whether wrapper needs `@MainActor`:
```csharp
bool needsMainActor = ((_env.ParentDecl as TypeDecl)?.IsMainActorIsolated == true
    || _env.MethodDecl.IsActorIsolated)   // member-level @MainActor
    && !(_env.MethodDecl.IsNonisolated);
// For property accessors, also check the property-level IsActorIsolated flag
```

**3. `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs`**

The closure Cdecl wrappers use `@_silgen_name` (NOT `@_cdecl` as previously stated). `EmitClosureCdeclSwiftWrapper()` (line 474) emits global wrapper functions at line 601:
```swift
@_silgen_name("wrapperSymbol")
public func wrapperName(...) { ... }
```

Since these are `@_silgen_name` functions (not `@_cdecl`), they CAN be annotated with `@MainActor`. For closure wrappers on `@MainActor`-isolated members, the wrapper calls the original method which requires actor isolation. **Add `@MainActor`** before `@_silgen_name` when the parent type is actor-isolated and the method is not nonisolated.

The `parentDecl` parameter is already available in `EmitClosureCdeclSwiftWrapper` (line 476). Add:
```csharp
bool needsMainActor = ((parentDecl as TypeDecl)?.IsMainActorIsolated == true
    || env.MethodDecl.IsActorIsolated)   // member-level @MainActor
    && !(env.MethodDecl.IsNonisolated);
if (needsMainActor)
    swiftWriter.WriteLine("@MainActor");
// existing: swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
```

**4. `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs`**

No changes needed. The post-processor strips broken wrappers based on compilation errors. With correct `@MainActor` annotations, fewer wrappers will be broken, so fewer will be stripped. The post-processor is defensive (only strips patterns it recognizes as broken).

### Actor Types (not just @MainActor)

For types declared as `actor` (e.g., `BlinkIDAnalyzer`, `CaptureService`, `BlinkIDEventStream`), their methods are implicitly actor-isolated. The wrapper function needs to be called from the actor's isolation domain. Two approaches:

**Approach A (Recommended)**: Mark wrappers for actor-type methods with the actor's isolation. For custom actors, this would be `isolated someActor`, but since we're generating wrapper functions (not methods ON the actor), we need `@MainActor` for MainActor types and for custom actors we need a different approach.

**Approach B (Simpler)**: For custom actors, make the wrapper function `async` and `await` the call to the actor's method. This naturally dispatches to the actor's executor. Since async wrappers already create `Task {}` blocks, this is already handled — the Task body dispatches to the actor.

**Decision**: For `@MainActor` types/members, emit `@MainActor` on the wrapper. For custom `actor` types, the existing async wrapper pattern already handles actor dispatch (the `Task {}` body calls the actor method, which Swift dispatches to the actor's executor). No additional annotation needed for custom actors — only for `@MainActor`.

### Complexity / Risk

- **Complexity**: Low-Medium. The template modification is straightforward. The key design decision (which wrappers get the annotation) is documented above.
- **Risk**: Medium. Incorrect `@MainActor` placement could cause new compilation errors. Mitigation: the post-processor will strip any broken wrappers, maintaining the current baseline. Net effect is strictly additive (more wrappers compile).
- **Validation**: Run `./validate-libraries.sh --filter BlinkIDUX` and check that `generate: fail` count decreases.

### Tests

- Unit test: Mock a TypeDecl with `IsMainActorIsolated = true`, verify generated Swift wrapper contains `@MainActor`
- Unit test: Mock a TypeDecl with `IsCustomActor = true`, verify wrapper does NOT contain `@MainActor`
- Unit test: Mock a MethodDecl with `IsActorIsolated = true` on a non-actor type, verify wrapper contains `@MainActor`
- Unit test: Mock a method with `IsNonisolated = true` on an actor-isolated type, verify wrapper does NOT contain `@MainActor`
- Integration test: BlinkIDUX validation — fewer wrapper compilation errors, more C# methods available
- Regression: 32/32 validation maintained

---

## Sub-task 2d: Marker Protocol Primitive Overloads

### Problem

SnapKit uses empty protocols (marker protocols) as type-erased parameter types:

```swift
public protocol ConstraintOffsetTarget { }
extension Swift.Int : SnapKit.ConstraintOffsetTarget { }
extension Swift.UInt : SnapKit.ConstraintOffsetTarget { }
extension Swift.Float : SnapKit.ConstraintOffsetTarget { }
extension Swift.Double : SnapKit.ConstraintOffsetTarget { }
extension CoreFoundation.CGFloat : SnapKit.ConstraintOffsetTarget { }
```

The method `offset(_ amount: any ConstraintOffsetTarget)` takes this protocol type. In the current bindings, this parameter is projected as `IConstraintOffsetTarget` which has no C# implementations, making the method uncallable with primitive values.

Similar protocols in SnapKit: `ConstraintRelatableTarget`, `ConstraintInsetTarget`, `ConstraintConstantTarget`, `ConstraintMultiplierTarget`, `ConstraintPriorityTarget`.

### Approach

**Detection**: A marker protocol suitable for overloads has:
1. No required members (empty body in ABI JSON — no `children` of kind `Function`/`Var`)
2. All conforming types in the swiftinterface are value types (primitives, CGFloat, CGPoint, UIEdgeInsets)
3. The protocol is used as a parameter type in at least one method

**Overload Generation**: For each method that takes a marker protocol parameter, emit typed convenience overloads for each conforming primitive type. For example:

```csharp
// Original (existential — uncallable with primitives)
public ConstraintMakerEditable Offset(IConstraintOffsetTarget amount) { ... }

// Generated overloads
public ConstraintMakerEditable Offset(double amount) => Offset((IConstraintOffsetTarget)(object)amount);
public ConstraintMakerEditable Offset(float amount) => Offset((IConstraintOffsetTarget)(object)amount);
public ConstraintMakerEditable Offset(int amount) => Offset((IConstraintOffsetTarget)(object)amount);
```

Wait — this won't work because C# primitives don't implement `IConstraintOffsetTarget`. The approach needs to be different.

**Revised Approach**: Generate overloads that call the Swift method directly (via separate P/Invoke with the concrete type). The Swift method's existential container packing happens at the ABI level — the caller passes the value type directly with the protocol witness table pointer.

Actually, the simplest approach: detect marker protocol parameters and replace them with the MOST COMMON conforming primitive type as the primary signature, emitting all conforming primitives as overloads. Each overload calls the same Swift function but marshals the concrete primitive into the existential container.

**Simplest viable approach**: For methods taking `any MarkerProtocol` where the protocol is empty and has only primitive conformers, replace the C# parameter type with the widest primitive type (`double` for numeric protocols). Add `[EditorBrowsable(Never)]` to the raw existential version.

**Even simpler**: Parse conforming types from swiftinterface, and for each method taking the marker protocol, emit convenience overloads that perform explicit existential wrapping in Swift. The Swift wrapper creates the existential from the concrete type.

### Implementation Design

**Phase 1: Detect marker protocols and their conforming types**

Add to `SwiftInterfaceAccessParser.cs`:

```csharp
/// <summary>
/// Returns a dictionary mapping protocol names to their conforming type names,
/// as declared in extension conformance blocks in the swiftinterface.
/// Only includes protocols with zero required members (marker protocols).
/// </summary>
public static Dictionary<string, List<string>> GetMarkerProtocolConformances(string swiftInterfacePath)
```

This parses lines like:
```swift
extension Swift.Int : SnapKit.ConstraintOffsetTarget { }
extension Swift.Double : SnapKit.ConstraintOffsetTarget { }
```

Returns `{"ConstraintOffsetTarget": ["Swift.Int", "Swift.UInt", "Swift.Float", "Swift.Double", "CoreFoundation.CGFloat"]}`.

**Phase 2: Determine which conformers are projectable primitives**

Filter the conformer list to types the generator already knows how to project: `Swift.Int` -> `nint`, `Swift.Double` -> `double`, `Swift.Float` -> `float`, `Swift.UInt` -> `nuint`, `CoreFoundation.CGFloat` -> `nfloat`.

Non-primitive conformers (e.g., `CoreFoundation.CGPoint`, `UIKit.UIEdgeInsets`) are excluded from overloads (they require struct projection which is more complex).

**Phase 3: Emit overloads**

In the method handler, when a parameter's type is a marker protocol with known primitive conformers, emit additional C# method overloads with concrete parameter types.

### Files to Modify

**1. `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs`**

Add `GetMarkerProtocolConformances()` method. Parse `extension Type : Protocol { }` patterns.

The conformance pattern in swiftinterface is:
```
extension Swift.Int : SnapKit.ConstraintOffsetTarget {
}
```

Regex:
```csharp
private static readonly Regex ConformanceExtensionRegex = new(
    @"extension\s+([\w.]+)\s*:\s*([\w.,\s]+)\s*\{",
    RegexOptions.Compiled);
```

**2. `src/Swift.Bindings/src/Emitter/StringEmitter/MarkerProtocolOverloadEmitter.cs`** (NEW FILE)

New emitter class responsible for:
- Checking if a method parameter is a marker protocol with primitive conformers
- Generating overload methods with concrete primitive parameters
- Each overload creates a Swift wrapper that wraps the primitive into the existential

```csharp
/// <summary>
/// Emits typed convenience overloads for methods whose parameters use marker protocols
/// (empty protocols with only primitive-type conformers).
/// </summary>
internal static class MarkerProtocolOverloadEmitter
{
    /// <summary>
    /// Checks if a protocol type is a marker protocol with known primitive conformers.
    /// </summary>
    public static bool IsMarkerProtocolWithPrimitiveConformers(
        TypeSpec paramType,
        ITypeDatabase typeDatabase,
        Dictionary<string, List<string>>? markerProtocolConformances)

    /// <summary>
    /// Gets the list of C# primitive types that conform to the marker protocol.
    /// </summary>
    public static List<(string CSharpType, string SwiftType)> GetPrimitiveConformers(
        string protocolName,
        Dictionary<string, List<string>>? markerProtocolConformances)

    /// <summary>
    /// Emits convenience overloads for a method with marker protocol parameters.
    /// </summary>
    public static void EmitOverloads(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl methodDecl,
        MethodEnvironment env,
        Dictionary<string, List<string>>? markerProtocolConformances)
}
```

**3. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`**

After emitting the main method (around line 280), check for marker protocol parameters and call `MarkerProtocolOverloadEmitter.EmitOverloads()`.

**4. `src/Swift.Bindings/src/Program.cs`**

- Parse marker protocol conformances from swiftinterface
- Thread through to emitter infrastructure (via `TypeHandlerContext` or similar)

**5. `src/Swift.Bindings/src/Emitter/StringEmitter/TypeHandlerContext.cs`** (or wherever context is passed)

Add `Dictionary<string, List<string>>? MarkerProtocolConformances` to the context.

### Overload Strategy (Detailed)

For `ConstraintMakerEditable.Offset(any ConstraintOffsetTarget)`:

**Option A: C#-side casting** (simpler but may not work with existential ABI):
```csharp
public ConstraintMakerEditable Offset(double amount)
{
    // Needs to construct the existential container for 'double' as ConstraintOffsetTarget
    // This requires protocol witness table lookup at runtime
}
```

**Option B: Swift-side wrappers** (reliable, uses Swift's own existential boxing):

Generate Swift wrapper functions:
```swift
@_silgen_name("SBW_ConstraintMakerEditable_offset_Double")
public func SBW_offset_Double(_ self: ConstraintMakerEditable, _ amount: Double) -> ConstraintMakerEditable {
    return self.offset(amount)  // Swift handles existential boxing
}
```

Then emit C# overloads that call these Swift wrappers:
```csharp
public ConstraintMakerEditable Offset(double amount)
{
    var __result = PInvoke_SBW_offset_Double(this.Payload, amount);
    return new ConstraintMakerEditable(__result);
}
```

**Decision**: Option B is more reliable because it leverages Swift's built-in existential boxing rather than trying to construct existential containers from C#. The cost is one Swift wrapper function per (method, concrete type) combination, but these are simple pass-through wrappers.

### Which primitive types to emit overloads for

For numeric marker protocols (ConstraintOffsetTarget, ConstraintInsetTarget, ConstraintMultiplierTarget, ConstraintPriorityTarget):
- `double` (Swift.Double) — primary, most common
- `float` (Swift.Float)
- `nint` (Swift.Int)

For broader marker protocols (ConstraintRelatableTarget which also includes CGSize, CGPoint, UIEdgeInsets):
- Only primitive numeric overloads. Struct conformers are out of scope for Session 2.

### Complexity / Risk

- **Complexity**: Medium-High. This is the most complex sub-task. New emitter class, new Swift wrapper generation, P/Invoke for overloads.
- **Risk**: Medium. The Swift wrappers are simple pass-throughs, so compilation risk is low. The main risk is the P/Invoke signature matching for the overloaded wrappers. Mangled names must be unique.
- **Scoping**: If time-constrained, implement for `ConstraintOffsetTarget` and `ConstraintInsetTarget` only (the two most critical for SnapKit's DSL). The infrastructure will be reusable for other marker protocols.

### Tests

- `SwiftInterfaceAccessParserTests.GetMarkerProtocolConformances_ParsesConformanceExtensions`
- `SwiftInterfaceAccessParserTests.GetMarkerProtocolConformances_FiltersEmptyProtocolsOnly`
- Unit test: `MarkerProtocolOverloadEmitter` generates correct overloads for a mock marker protocol
- Integration: SnapKit `Offset(10.0)` compiles with `double` overload
- Regression: 32/32 validation maintained

---

## Merge Conflict Risk Assessment

### Files Session 2 Touches vs Sessions 3/4

| File | Session 2 | Session 3 | Session 4 | Conflict Risk |
|------|:---------:|:---------:|:---------:|:-------------:|
| `SwiftInterfaceAccessParser.cs` | NEW methods (additive) | No | No | None |
| `SwiftABIParser.cs` | Constructor params, ParseClass/ParseStruct | No | No | None |
| `Program.cs` | New swiftinterface parse calls (~line 689) | Existential setup | Closure setup | **Low** — different sections |
| `TypeDecl.cs` | `IsMainActorIsolated`, `IsCustomActor` properties | No | No | None |
| `MethodDecl.cs` | `IsActorIsolated`, `IsNonisolated` properties | No | No | None |
| `PropertyDecl.cs` | `IsActorIsolated`, `IsNonisolated` properties | No | No | None |
| `WrapperEmitter.Async.cs` | Actor annotation in template | No | Throwing closure changes | **Low** — different templates |
| `WrapperEmitter.Marshalling.cs` | Actor annotation on opaque wrappers | No | No | None |
| `ClosureEmitter.SwiftWrapper.cs` | Actor annotation on closure wrappers | No | Closure changes | **Low** — annotation is additive |
| `MethodHandler.cs` | Marker overload call site | Existential parameter handling | Closure parameter handling | **Medium** — same method body, different concerns |
| `MemberEmissionValidator.cs` | No | Existential validation | Closure validation | None |
| `ExistentialHandler.cs` | No | **Heavy changes** | No | None |
| `ClosureEmitter.cs` | No | No | **Heavy changes** | None |
| `TypeHandlerContext.cs` | Add MarkerProtocolConformances | May add existential context | May add closure context | **Low** — additive |

**Overall conflict risk: LOW**. Session 2's changes are concentrated in the parser layer (`SwiftInterfaceAccessParser`, `SwiftABIParser`) and the wrapper emission layer (`WrapperEmitter.Async.cs`), which Sessions 3 and 4 don't heavily touch. The only shared file with meaningful overlap is `MethodHandler.cs`, where all three sessions add different call sites — these are additive and won't textually conflict.

---

## Implementation Order

**Recommended sequence** (dependencies shown):

```
2a. Access-level filtering        ← Start here (smallest, quickest wins)
 │
 ├─► 2b. Parse @MainActor        ← Independent of 2a, but natural next step
 │    │                             (extends same parser file)
 │    │
 │    └─► 2c. Emit actor isolation  ← Depends on 2b (uses IsMainActorIsolated / IsActorIsolated data)
 │
 └─► 2d. Marker protocol overloads  ← Independent of 2b/2c, most complex
```

**If time-constrained**: Do 2a + 2b + 2c first (noise reduction + actor correctness). These are bounded and have clear acceptance criteria (see "Core gates" in Acceptance Gates). 2d (marker overloads) is the most impactful for SnapKit but also the most complex — it can be deferred to a follow-up session. The acceptance gates are structured so the core gates are self-consistent without 2d; the SnapKit marker overload gate is a "stretch gate" that only applies if 2d is implemented.

---

## Acceptance Gates

### Core gates (2a + 2b + 2c — must pass)

| Gate | Verification |
|------|-------------|
| BlinkIDUX actor isolation errors eliminated | Run `validate-libraries.sh --filter BlinkIDUX`, confirm 0 "main actor-isolated" wrapper compilation errors. The `generate` field should change from `fail` to `pass` or show significantly fewer errors. |
| Internal types filtered for BlinkID/Stripe | Verify `_`-prefixed types get `[EditorBrowsable(Never)]` |
| 32/32 validation maintained | Run `validate-libraries.sh` (full suite) |
| Unit test baselines maintained | Run `./run-tests.sh` |
| Golden files pass | Run `golden/check-golden-files.sh` |

### Stretch gate (2d — only if 2d is implemented this session)

| Gate | Verification |
|------|-------------|
| SnapKit `Offset(10.0)` compiles with `double` overload | Generate SnapKit bindings, verify `Offset(double)` method exists |

If 2d is deferred, the SnapKit marker overload gate moves to the follow-up session. The core gates above are self-consistent without it.

---

## Detailed Line Numbers Reference

### SwiftInterfaceAccessParser.cs (827 lines)

| Line | Content | Relevance |
|------|---------|-----------|
| 29 | `TypeDeclRegex` — type decl regex | Reuse for public type detection |
| 37 | `ExtensionDeclRegex` — extension regex | Reuse for conformance extension parsing |
| 63 | `GetInternalMembers()` | Pattern for new methods (typeStack, brace depth) |
| 148 | End of `GetInternalMembers` | Insert `GetPublicTypeNames` here |
| 294 | `GetEnumCaseLabels()` | Pattern for new conformance parser |
| 305-322 | Continuation line handling in `GetEnumCaseLabels` | Pattern for pending-attribute accumulator |
| 480 | `GetParameterNames()` | Multi-line handling pattern |
| 575 | `GetTypedThrowsErrors()` | Pattern for new actor methods |
| 703-712 | `HasUnmatchedOpenParen()` | Helper for multi-line detection |

### SwiftABIParser.cs

| Line | Content | Relevance |
|------|---------|-----------|
| 211-229 | Constructor | Add new parameters |
| 493 | `ParseClass` — `IsModuleInternal` | Add `IsMainActorIsolated`, `IsCustomActor` |
| 529 | `ParseStruct` — `IsModuleInternal` | Add public type check |
| 662 | `BuildTypeQualifiedPath()` | Use for qualified public type name lookup |
| 706 | `ParseProtocol` — `IsModuleInternal` | Add public type check |
| 827 | Method parsing — `IsModuleInternal` | Add `IsActorIsolated`, `IsNonisolated` |
| 986 | `CreateGetAccessor()` | **Add actor isolation to accessor MethodDecl** |
| 1041 | `CreateSetAccessor()` | **Add actor isolation to accessor MethodDecl** |
| 1102 | `CreatePropertyDecl()` | **Add actor isolation to PropertyDecl** |

### WrapperEmitter.Async.cs

| Line | Content | Relevance |
|------|---------|-----------|
| 15 | `EmitAsync()` — entry point | Resolve `needsMainActor` flag |
| 573 | `BuildSwiftAsyncWrapperCode()` call | Pass actor flag |
| 1236 | `BuildSwiftAsyncWrapperCode()` definition | Add parameter |
| 1282-1284 | Swift function template | Conditionally prepend `@MainActor` |

### WrapperEmitter.Marshalling.cs

| Line | Content | Relevance |
|------|---------|-----------|
| 16 | `EmitOpaqueReturnWrapper()` | **Add `@MainActor` for actor-isolated types** |
| 88-101 | Property getter wrapper (accessor path) | Add `@MainActor` before `@_silgen_name` |
| 94-115 | Extension-wrapped method wrapper | Add `@MainActor` before `@_silgen_name` (line 96) |
| 118-128 | Free function wrapper | Add `@MainActor` before `@_silgen_name` (line 123) |

### ClosureEmitter.SwiftWrapper.cs

| Line | Content | Relevance |
|------|---------|-----------|
| 474 | `EmitClosureCdeclSwiftWrapper()` entry | parentDecl param already available |
| 601 | `@_silgen_name` emission | **Add `@MainActor` before this line** |

### Program.cs

| Line | Content | Relevance |
|------|---------|-----------|
| 680-689 | Swiftinterface parsing block | Add new parse calls |
| 708 | `SwiftABIParser` constructor | Add new parameters |
| 807-835 | `CollectInternalTypeNames` | Reference for internal type handling |
