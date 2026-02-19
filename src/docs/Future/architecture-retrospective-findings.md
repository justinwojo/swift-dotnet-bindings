# Architecture Retrospective: Swift Bindings Generator

**Supplement**: [architecture-retrospective-supplement.md](architecture-retrospective-supplement.md) — Complete string marker inventory, concrete cross-path divergences for 18 real types, and hard marshalling case analysis for TypeProjectionFactory design.

## 1. Executive Summary

This document analyzes the architecture of the Swift-to-.NET binding generator after 18+ months of active development. The generator works — 31 real-world libraries compile, 3400+ unit tests pass, 700+ integration tests pass — but non-trivial generator changes frequently break library bindings despite extensive test coverage. This analysis identifies the root causes of that fragility and proposes targeted fixes.

### Top 5 Findings

1. **Four divergent type conversion pipelines** (Section 2). Swift types are converted to C# types through at least four independent code paths (`GetIdiomaticCSharpType`, `TranslateTypeSpecForConversion`, `TranslateBoundGenericTypeToCSharp`, `GetCSharpTypeName` in ProtocolProxyEmitter). Each makes different decisions about nullable annotations, existential handling, array/dictionary projection, and generic resolution. When one path is updated, the others must be updated independently or bindings break with CS0535/CS0738 errors. This is the single largest source of fragility.

2. **Type information encoded in strings** (Section 3). The `Parameter.Type` field in `MethodSignature.cs` encodes marshalling semantics in string prefixes (`"Existential:"`, `"SimpleEnum:"`, `"ObjCBridged:"`, `"CdeclClosureFuncPtr:"`, `"NativeRemapped:"`, etc.). The `SignatureString()` and `CallArgumentsString()` methods parse these strings with `StartsWith()` and `Split(':')`. This is a discriminated union implemented as string manipulation — fragile, untestable in isolation, and invisible to the type system.

3. **Scattered bool marshalling** (Section 3). The `[MarshalAs(UnmanagedType.U1)]` annotation for `bool` P/Invoke parameters is applied independently in 7+ locations across the emitter layer. Each site has its own `== "bool"` check. Missing this annotation at any one site produces a silent runtime bug (bool values marshalled as 4 bytes instead of 1).

4. **Cross-cutting state through Conductor** (Section 5). The `Conductor` class carries mutable state (`CurrentPInvokeHelperContext`, `NestedTypeRenames`, `CompositionInterfaces`) that flows between type handlers and member handlers via temporal coupling. The `s_activeCompositionCollector` ThreadStatic field enables the `ExistentialHandler` to reach the Conductor's collection without a direct reference — a static coupling that makes testing and reasoning about data flow difficult.

5. **Test architecture gap** (Section 6). Tests predominantly verify individual handler output or emitter behavior against expected string fragments. There are no end-to-end property tests that verify: "for any valid Swift type T, all four type conversion paths produce the same C# type." The 3400+ tests are individually correct but collectively blind to the cross-path consistency that real libraries depend on.

### Recommended Actions (Priority Order)

1. **Introduce a `MarshalledType` discriminated union** to replace string-encoded type markers in `Parameter.Type` (estimated effort: 3-5 days, risk: low)
2. **Unify type conversion into a single `TypeProjector`** service that all paths delegate to (estimated effort: 2-3 weeks, risk: medium)
3. **Add cross-path consistency tests** that verify all type conversion paths agree for a corpus of real-world Swift types (estimated effort: 3-5 days, risk: low)
4. **Centralize bool marshalling** into the `MarshalledType` (benefit: immediate, effort: trivial once #1 is done)
5. **Extract Conductor state into explicit parameter objects** (estimated effort: 1 week, risk: low)

---

## 2. Type Conversion Pipeline Analysis

### Overview

Converting a Swift `TypeSpec` to a C# type string is the most critical operation in the generator. It happens in at least four major code paths, each with different entry points, different decision trees, and different callers.

### Path 1: `TypeConversionHandler.GetIdiomaticCSharpType`

**File:** `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs:116`
**Entry point:** `GetIdiomaticCSharpType(TypeSpec, bool isParameter, Func<TypeSpec, string>? typeTranslator)`
**Callers:** `WrapperSignatureBuilder.HandleReturnType` (~line 300), `WrapperSignatureBuilder.HandleArguments` (~line 434), `PropertyHandler`, `ProtocolHandler`, `ProtocolProxyEmitter.Helpers.GetCSharpTypeName` (~line 71), `ModuleHandler`, `ProtocolSignatureHelper`, `ProtocolConformanceValidator`, `DefaultParameterOverloadEmitter`, `MemberEmissionValidator`, `CompletionHandlerDetector`

**Decisions made:**
| Decision | Behavior |
|----------|----------|
| SwiftString → string | Yes, always |
| SwiftArray → IEnumerable\<T\>/IReadOnlyList\<T\> | Yes, param vs return |
| SwiftDictionary → IDictionary/IReadOnlyDictionary | Yes, param vs return |
| SwiftOptional → T? | Yes, except Optional\<Closure\> and Optional\<Existential\> |
| Existential handling | Defers to ExistentialHandler (returns null) |
| Generic parameter resolution | Via `typeTranslator` callback — different callers pass different translators |
| ObjC bridged types | Not handled (returns null) |
| Native remapping (URL/Data) | Not handled (separate `HasNativeTypeRemapping` check) |
| Nullable annotations | `T?` suffix for Optional |

### Path 2: `WrapperSignatureBuilder.TranslateTypeSpecForConversion`

**File:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs:556`
**Entry point:** `TranslateTypeSpecForConversion(TypeSpec typeSpec)` (private)
**Callers:** Used as the `typeTranslator` callback passed to `GetIdiomaticCSharpType` and `TupleHandler.GetCSharpTupleType` from `WrapperSignatureBuilder`

**Decisions made:**
| Decision | Behavior |
|----------|----------|
| Existential types | Uses `GetPublicExistentialType` → interface name, falls back to AnyType |
| "object" fallback | Returns AnyType placeholder (causes method skip) |
| Generic type parameters | Resolves via `_genericContext.TryResolve` |
| Generic containers | Appends translated generic params to base type |
| IntPtr/AnyType fallback | Returns bare name, no generic params appended |
| Unknown types | Returns AnyType |

### Path 3: `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp`

**File:** `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` (~line 150+)
**Entry point:** `TranslateBoundGenericTypeToCSharp(IHasSwiftTypeSpec, GenericContext?)`
**Callers:** `WrapperSignatureBuilder.HandleReturnType` (~line 313), `WrapperSignatureBuilder.HandleArguments` (~line 447), `ProtocolProxyEmitter.GetCSharpTypeName` (~line 116), `EnumHandler.CaseConstruction`, `EnumHandler.CaseInspection`, `PropertyHandler`, `ProtocolHandler`, `ProtocolConformanceValidator`

**Decisions made:**
| Decision | Behavior |
|----------|----------|
| Optional\<T\> | `SwiftOptional<{inner}>` or nullable annotation depending on inner type |
| Array\<T\> | `SwiftArray<{inner}>` |
| Dictionary\<K,V\> | `SwiftDictionary<{inner}, {inner}>` |
| Generic param resolution | Via GenericContext mapping or falls through to TypeDatabase |
| Existential in generic | Falls back to AnyType for existential type arguments |
| Nested generics | Recursive resolution |

### Path 4: `ProtocolProxyEmitter.GetCSharpTypeName`

**File:** `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Helpers.cs:14`
**Entry point:** `GetCSharpTypeName(TypeSpec?, bool forAbiMarshalling = false)`
**Callers:** `EmitMethodImplementation`, `EmitPropertyImplementation`, `EmitSubscriptImplementation`, `GetClosureCSharpType`, `GetTupleCSharpType`, closure argument/return translation within proxy classes

**Decisions made:**
| Decision | Behavior |
|----------|----------|
| AssociatedTypeReferenceSpec | Maps to `T{Name}` |
| Closure types | Translates to Action\<\>/Func\<\> |
| Tuple types | Translates to ValueTuple |
| Existential types | Uses well-known protocol check, then supported check |
| Idiomatic conversion (C9) | Only when `!forAbiMarshalling` — defers to GetIdiomaticCSharpType |
| Optional\<existential\> | Special case for well-known protocols (Error → AnyError?) |
| Bound generics | Creates temporary PropertyDecl, delegates to BoundGenericsHandler |
| Exception handling | Catches all exceptions, returns AnyType or `NameWithoutModule` |
| forAbiMarshalling flag | Bifurcates: ABI path returns SwiftString, public path returns string |

### Comparison Matrix

| Decision | Path 1 (Idiomatic) | Path 2 (Translate) | Path 3 (BoundGenerics) | Path 4 (ProxyHelper) |
|----------|--------------------|--------------------|------------------------|----------------------|
| String → string | Yes | No (delegates to P1) | No (returns SwiftString) | Yes (via P1, non-ABI) |
| Array → IRL/IE | Yes | No (delegates to P1) | No (returns SwiftArray) | Yes (via P1, non-ABI) |
| Optional → T? | Yes | No (delegates to P1) | Partial | Yes (via P1, non-ABI) |
| Existential → interface | No (defers) | Yes | No (returns AnyType) | Yes |
| Generic params | Via callback | Via GenericContext | Via GenericContext | Via BoundGenericsHandler |
| Error handling | Returns null | Returns AnyType | Throws or returns AnyType | Returns AnyType/object |
| Closure handling | No | No | No | Yes (Action/Func) |
| Tuple handling | No | Via TupleHandler | No | Yes (ValueTuple) |
| ObjC bridged | No | No | No | No |
| Native remap | Separate method | No | No | No |

### Where They Disagree (Fragility Sources)

1. **Idiomatic vs ABI divergence in proxies**: `GetCSharpTypeName` in ProtocolProxyEmitter has the `forAbiMarshalling` flag that bifurcates behavior. The protocol interface (`ProtocolHandler`) uses Path 1 for property types but has its own inline type resolution. If the interface resolves `SwiftString` → `string` but the proxy resolves it differently (e.g., because `forAbiMarshalling=true` path is used accidentally), CS0535 results.

2. **BoundGenericsHandler returns raw types**: Path 3 returns `SwiftOptional<Boolean>` while Path 1 returns `bool?`. When Path 1 is applied first (in `WrapperSignatureBuilder`), it catches the idiomatic case. But when Path 3 is called directly (in `EnumHandler.CaseConstruction`, `ProtocolConformanceValidator`), the raw type leaks into signatures that should use idiomatic types.

3. **TranslateTypeSpecForConversion scope**: Path 2 is only accessible as a `private` method inside `WrapperSignatureBuilder`. Other callers that need the same resolution (e.g., `DefaultParameterOverloadEmitter`, `ProtocolConformanceValidator`) must reimplement equivalent logic.

4. **typeTranslator callback variance**: Path 1 accepts an optional `Func<TypeSpec, string>` that fundamentally changes its behavior. `WrapperSignatureBuilder` passes `TranslateTypeSpecForConversion`; `ProtocolProxyEmitter` passes `ts => GetCSharpTypeName(ts)`. These translators handle existentials, generics, and nested types differently.

---

## 3. Scattered Concerns Catalog

### Concern 1: Bool Marshalling (`[MarshalAs(UnmanagedType.U1)]`)

**What:** C# `bool` is 4 bytes by default in P/Invoke; Swift `Bool` is 1 byte. The `[MarshalAs(UnmanagedType.U1)]` attribute must be applied to every P/Invoke declaration involving `bool`.

**Where implemented (7+ sites):**
1. `MethodSignature.cs:28` — `Parameter.PInvokeSignatureString()` pattern match
2. `EnumHandler.CaseConstruction.cs:133` — inline `== "bool"` check for associated value params
3. `PInvokeEmitter.cs:664` — return type check `[return: MarshalAs(UnmanagedType.U1)]`
4. `PInvokeHelperEmitter.cs:192` — return type in helper class
5. `EnumHandler.SimpleEnum.cs:327` — simple enum method params
6. `EnumHandler.SimpleEnum.cs:334` — simple enum method return
7. `OperatorHandler.cs:463` — operator return type

**Implicit contract:** Every site that emits a P/Invoke declaration with `bool` must include the attribute.

**What breaks when they disagree:** Silent runtime corruption. The C# runtime reads/writes 4 bytes where Swift expects 1, corrupting adjacent stack values. This manifests as intermittent wrong-value bugs that are extremely hard to diagnose.

**Can it be centralized?** Yes. A `MarshalledType.Bool` variant in the proposed discriminated union would carry the marshalling annotation intrinsically. Any emission path that serializes a `MarshalledType.Bool` to a P/Invoke string would automatically include `[MarshalAs(UnmanagedType.U1)]`.

### Concern 2: Existential Container Projection

**What:** Swift existential types (`any Protocol`) are represented as `ExistentialContainer{N}` structs at the ABI level. In public C# APIs, they should appear as interface types (`IProtocol`). The conversion between these representations happens at multiple layers.

**Where implemented (12+ files):**
1. `TypeDatabaseExtensions.cs` — `GetExistentialTypeRecord()` creates ExistentialContainer records
2. `ExistentialHandler.cs` — `GetCSharpExistentialType()`, `GetPublicExistentialType()`, `GetPInvokeExistentialType()`
3. `ClosureHandler.cs` — `TranslateTypeSpecToCSharp()` handles existentials inside closures
4. `ProtocolProxyEmitter.Helpers.cs` — `GetCSharpTypeName()` handles existentials for proxy classes
5. `ProtocolProxyEmitter.InterfaceImpl.cs` — subscript/method implementations resolve existentials
6. `ProtocolProxyEmitter.Receivers.cs` — callback marshalling for existential parameters
7. `WrapperEmitter.Return.cs` — existential return value proxy wrapping
8. `MethodSignature.cs:49` — `"Existential:{container}:{public}"` string encoding
9. `ClosureEmitter.cs` — existential handling in closure argument/return types
10. `PInvokeEmitter.cs` — existential argument/return handling in P/Invoke signatures
11. `ProtocolConformanceValidator.cs` — validates existential conformance
12. `MemberEmissionValidator.cs` — filters methods with existential types

**Implicit contract:** The ABI representation (ExistentialContainerN), the public interface type (IProtocol), and the proxy extraction code (`ISwiftExistentialConvertible<T>.GetExistentialContainer()`) must all agree on the protocol count and identity.

**What breaks when they disagree:** CS0535 (proxy doesn't implement interface member), CS0266 (cannot implicitly convert), or runtime crashes from reading the wrong number of payload words.

**Can it be centralized?** Partially. An `ExistentialProjection` value type could capture `(containerType, publicType, protocols[])` as a single unit created once by `ExistentialHandler` and passed through the pipeline. Currently each consumer calls `ExistentialHandler` independently and may get different results if the handler's internal state has changed.

### Concern 3: Optional Handling

**What:** Swift `Optional<T>` has at least 5 different projections depending on context: `T?` (idiomatic), `SwiftOptional<T>` (ABI), `T` (unwrapped in certain contexts), nullable pointer (P/Invoke for class types), and large-Optional buffer (for non-frozen types).

**Where implemented independently:**
1. `TypeConversionHandler.GetIdiomaticCSharpType` — converts Optional → `T?`
2. `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp` — returns `SwiftOptional<T>`
3. `PInvokeSignatureBuilder` in `PInvokeEmitter.cs` — handles Optional in P/Invoke context (nullable IntPtr for classes, SwiftOptional for structs)
4. `WrapperEmitter.Return.cs` — handles large Optional buffer reads
5. `OptionalPointerWrapperEmitter.cs` — handles `UnsafeRawPointer` widening for large Optionals
6. `ClosureEmitter.cs` — handles Optional<Closure> → nullable delegate
7. `ExistentialHandler.IsOptionalExistential` — handles Optional<any Protocol>
8. `EnumHandler.CaseConstruction.cs` — handles Optional associated values
9. `ProtocolProxyEmitter.Helpers.cs:85` — handles Optional<existential> for well-known protocols

**What breaks:** The most common failure mode is when the public signature says `T?` but the P/Invoke or marshalling code generates `SwiftOptional<T>`, causing CS0029 (cannot implicitly convert). The reverse — P/Invoke expecting `T?` when the wrapper produces `SwiftOptional<T>` — causes runtime crashes from size mismatch.

### Concern 4: Type Name Resolution / C# Identifier Generation

**What:** Converting Swift type names to valid C# identifiers involves: PascalCase conversion, keyword escaping, nested type flattening, ObjC name remapping, and collision resolution.

**Where implemented:**
1. `NameProvider.cs` — PascalCase, parameter names, method names, property names, deduplication
2. `TypeDatabaseExtensions.cs` — Apple framework remapping tables (`AppleFrameworkTypeRemappings`, `AppleFrameworkClassRemappings`)
3. `SwiftABIParser.cs` — `ExtractUniqueName()` for keyword escaping of module/type names
4. `ProtocolProxyEmitter.Helpers.cs` — `GetProxyClassName()`, `GetInterfaceNameWithGenerics()`
5. `TypeHandlerHelpers.cs` — `ProtocolConformanceHelper.GetInterfaceList()`
6. `GenericTypeEmitter.cs` — generic parameter name mapping

**What breaks:** Name collisions (CS0102) when two Swift types map to the same C# name, or CS0542 when a member name matches the enclosing type name.

### Concern 5: P/Invoke Entry Point Resolution

**What:** Determining the correct symbol name for a P/Invoke declaration — either the original mangled name or a vtable dispatch thunk.

**Where implemented:**
1. `PInvokeEmitter.cs` — `GetEntryPoint()` with vtable thunk logic for non-final class members
2. `EnumHandler.CaseConstruction.cs` — uses `caseDecl.MangledName` directly
3. `EnumHandler.SimpleEnum.cs` — uses mangled names for simple enum methods
4. `OperatorHandler.cs` — uses `methodDecl.MangledName` directly
5. `DefaultParameterOverloadEmitter.cs` — generates synthetic `@_silgen_name` symbols
6. `ArraySliceNormalizationEmitter.cs` — generates synthetic wrapper symbols
7. `ExistentialBypassEmitter.cs` — generates synthetic wrapper symbols

**Implicit contract:** The emitted symbol must exist in the dylib or the generated Swift wrapper. If a vtable thunk is needed but the original symbol is used (or vice versa), the binding crashes at runtime with `EntryPointNotFoundException`.

---

## 4. Handler/Emitter Boundary Analysis

### Evaluating the Redesign Proposal

The existing redesign proposal (`src/docs/Future/emitter-redesign-proposal.md`) proposes three phases: type pre-processing → type processing → emission. This is directionally correct. Let me evaluate specific aspects.

### Information Flow from Marshalling to Emission

The following information currently flows from the marshalling layer to the emission layer, and must be captured in any intermediate representation (IR):

1. **Type classification** — frozen struct, non-frozen struct, class, protocol, enum (simple/raw-representable/associated-value)
2. **Marshalling strategy per argument** — direct pass, SafeHandle extraction, buffer copy, existential container extraction, closure wrapping, SwiftString conversion, ObjC handle extraction, generic metadata passing
3. **Return value marshalling** — direct return, indirect result buffer, large Optional buffer, async callback, type conversion (SwiftString→string, etc.)
4. **P/Invoke shape** — calling convention, entry point, return type, parameter types with attributes, metadata parameters, conformance parameters, SwiftSelf, SwiftError
5. **Swift wrapper requirements** — opaque return boxing, async callback functions, Cdecl closure trampolines, default parameter overloads, ArraySlice normalization

### Runtime-Dependent Emission Decisions

These decisions depend on state accumulated during emission, not just the current member:

1. **PInvokeHelperContext** — generic types cannot have `[DllImport]` inside them (CS7042). A separate helper class is accumulated during type emission and emitted afterward. Nested generic types defer their helpers to the outermost parent.

2. **NestedTypeRenames** — when a nested type name collides with a property name, the type handler renames it and stores the mapping in `Conductor.NestedTypeRenames`. Member handlers must consult this map.

3. **CompositionInterfaces** — multi-protocol existentials create synthetic `IFooAndBar` interfaces. These are collected per-module via `Conductor.CollectCompositionInterface()` through a ThreadStatic field, and emitted at module level.

4. **Closure deduplication** — `ClosureEmitter` tracks emitted closure types via `_emittedClosureTypes` to avoid duplicate definitions.

5. **Swift wrapper symbol deduplication** — several emitters (`Utf8SliceEmitter`, `EveryProtocolEmitter`, `WrapperEmitter`) track which Swift helper functions have been emitted to avoid duplication.

### IR for Hard Cases

#### Async + Closure

An async method with a closure parameter requires:
- Swift wrapper with `@_silgen_name` that wraps the async call in a `Task{}`
- C# P/Invoke with `delegate* unmanaged[Cdecl]` callback pointer and context
- C# `[UnmanagedCallersOnly]` callback method that completes a `TaskCompletionSource`
- Closure parameter requires `GCHandle` allocation, function pointer extraction, context marshalling
- If the closure throws, an error callback is also needed

IR would need to express: `AsyncMethod { callback: ClosureMarshal { params, return, throws }, errorCallback: Option<ErrorCallback>, cancellation: bool, returnMarshal: ReturnStrategy }`

#### Protocol Proxy Witness Dispatch

A protocol proxy property getter with witness dispatch requires:
- Interface declaration matching the protocol
- Proxy class implementing the interface
- Dual path: C# implementation → delegate to `_csharpImpl`, Swift existential → witness table dispatch
- Swift wrapper `@_silgen_name` accessor that reads from existential container
- NativeMethods P/Invoke declaration
- Memory management (free function for heap-allocated returns)

IR would need to express: `PropertyDispatch { getter: WitnessOrDelegate, setter: Option<WitnessOrDelegate>, type: BlittableOrString, proxyField: ExistentialContainerRef }`

#### Enum Case Construction with Associated Values

An enum case with associated values requires:
- Static factory method
- Indirect result allocation via metadata
- Per-value marshalling (some need conversion: strings, existentials, generics)
- P/Invoke declaration with `SwiftIndirectResult` + typed parameters
- SafeHandle wrapping of result

IR would need to express: `EnumCaseFactory { caseName, mangledName, params: [(type: MarshalledType, name)], indirectResult: MetadataRef }`

### Is Three Phases the Right Split?

Three phases is roughly right, but the boundary between phase 2 (processing) and phase 3 (emission) needs refinement. The current proposal puts all string generation in phase 3, but the current codebase shows that many emission decisions are deeply intertwined with type analysis (e.g., `WrapperEmitter.Marshalling.cs` makes complex type-conditional decisions inline during string emission).

A better decomposition:
1. **Type classification and registration** (current: `ModuleProcessor` + `TypeDatabase`) — keep
2. **Member analysis and marshalling plan** — produce a `MemberMarshalPlan` that captures all decisions without generating strings
3. **Code generation** — purely mechanical: `MemberMarshalPlan` → C# string + Swift string

The key insight is that phase 2 should produce a *plan* that is serializable and testable, not a collection of string fragments. This enables: (a) testing the plan without emission, (b) comparing plans across paths for consistency, (c) snapshot-testing plans against expected plans.

---

## 5. Cross-Cutting State Map

### State Channel 1: `Conductor.CurrentPInvokeHelperContext`

**Type:** `PInvokeHelperContext?` (mutable property)
**Set by:** Type handlers (`FrozenStructHandler`, `NonFrozenStructHandler`, `ClassHandler`, `EnumHandler`) when the type being processed has generic parameters.
**Read by:** `MethodHandler`, `PropertyHandler`, `OperatorHandler`, `PInvokeEmitter`, `WrapperEmitter`, `ClosureEmitter`, `DefaultParameterOverloadEmitter`, `ExistentialBypassEmitter`, `ArraySliceNormalizationEmitter`, `EnumHandler.CaseConstruction`
**Cleared by:** Type handler after all members are emitted
**Ordering dependency:** Must be set before member emission begins; must be cleared after.

**Is it necessary?** Yes — CS7042 requires P/Invoke declarations outside generic types. But the current implementation uses temporal coupling through a mutable property rather than explicit parameter passing.

**Could restructuring eliminate it?** The context could be passed as an explicit parameter to all member handlers. `MethodEnvironment` already holds `PInvokeHelperContext?` — the issue is that it gets populated from `Conductor` rather than being passed at construction.

### State Channel 2: `Conductor.NestedTypeRenames`

**Type:** `Dictionary<string, string>?` (mutable property)
**Set by:** Type handlers when nested type names collide with property names (detected by `NameProvider.GetNestedTypeRenames`)
**Read by:** `NameProvider` and type handlers when resolving nested type references
**Cleared by:** Type handler after processing

**Is it necessary?** Yes — Swift allows types and properties with the same name; C# does not.
**Could restructuring eliminate it?** Yes. The rename map could be computed once during type pre-processing (phase 1) and stored on the `TypeDecl` itself or in the `TypeRecord`.

### State Channel 3: `Conductor.CompositionInterfaces` / `s_activeCompositionCollector` (ThreadStatic)

**Type:** `SortedDictionary<string, List<string>>` accessible via ThreadStatic `s_activeCompositionCollector`
**Set by:** `ModuleHandler.Emit()` via `Conductor.SetActiveCompositionCollector()`
**Written to by:** `ExistentialHandler.GetPublicExistentialType()` via `Conductor.CollectCompositionInterface()`
**Read by:** `ModuleHandler` after all types are emitted, to emit composition interface declarations
**Cleared by:** `ModuleHandler.Emit()` in a `finally` block

**Is it necessary?** Yes — composition interfaces must be emitted at module scope, not inside individual types.
**Could restructuring eliminate it?** Yes. If member analysis (phase 2) produces a manifest of required composition interfaces, the emission phase can emit them without runtime collection. Alternatively, a simple collector could be passed explicitly through the call chain instead of using ThreadStatic.

### State Channel 4: Mutable `MethodDecl` Flags

**What:** Several flags on `MethodDecl` and `PropertyDecl` are set during emission:
- `MethodDecl.IsAccessor` — set by property handler to signal that the method is a property accessor
- `ArgumentDecl.CSharpName` — set by `NameProvider.DeduplicateParameterNames()` during signature building
- `MethodDecl.MangledName` — overwritten by wrapper emitters when generating synthetic symbols

**Is it necessary?** No. These are communication channels between emission phases that should be explicit parameters or return values.

### State Channel 5: Closure/Symbol Deduplication Sets

**What:** Several emitters maintain `HashSet<string>` fields tracking emitted symbols:
- `ClosureEmitter._emittedClosureTypes`
- `EveryProtocolEmitter._emittedProtocols`
- `Utf8SliceEmitter._emitted` (static)
- Various `_emittedWrapperSymbols` in enum handlers

**Is it necessary?** Yes — duplicate definitions cause CS0101/CS0111.
**Could restructuring eliminate it?** A global emission manifest in the IR would make deduplication explicit and testable rather than scattered across per-emitter mutable sets.

---

## 6. Test Architecture Evaluation

### Current Test Distribution

| Category | Count (approx) | Abstraction Level |
|----------|----------------|-------------------|
| Unit - Parser | ~10 files | Parse ABI JSON → TypeDecl tree |
| Unit - Marshaler | ~16 files | Handler behavior in isolation |
| Unit - Emitter | ~43 files | Handler output as string fragments |
| Unit - TypeDatabase | ~2 files | Type lookup/registration |
| Unit - Configuration | ~10 files | Build infrastructure |
| Unit - Demangler | ~5 files | Symbol demangling |
| Integration - Functional | ~11 files | Compile+link generated bindings |
| Integration - Stress | ~4 files | Large-scale compilation |

### What Tests Verify

Most unit tests follow this pattern:
1. Construct a synthetic `TypeDecl`/`MethodDecl` with specific properties
2. Create a handler/emitter with a mock or real `TypeDatabase`
3. Invoke the handler
4. Assert that the output string contains expected fragments (e.g., `output.Contains("[MarshalAs(UnmanagedType.U1)]")`)

Integration tests:
1. Run the generator on the `TestFramework` Swift library
2. Compile the generated C# code
3. Some tests run the compiled code against the iOS Simulator

### Gap Analysis

**Gap 1: Cross-path consistency.** No test verifies that `GetIdiomaticCSharpType`, `TranslateBoundGenericTypeToCSharp`, `TranslateTypeSpecForConversion`, and `ProtocolProxyEmitter.GetCSharpTypeName` produce the same C# type for the same Swift type in the same context. This is the exact property that breaks when library bindings fail.

**Gap 2: Signature agreement.** No test verifies that the wrapper signature, P/Invoke signature, and marshalling code all agree on types. A method's public signature might say `string` (via idiomatic conversion) while the P/Invoke says `SwiftString` and the marshalling code assumes `SwiftString` — this works. But if the public signature says `SwiftOptional<Boolean>` while the P/Invoke says `bool?`, marshalling breaks. Tests check these independently but not their agreement.

**Gap 3: Real-library type corpus.** Unit tests use synthetic type declarations that cover known cases. But real libraries produce type combinations that aren't anticipated — e.g., `Optional<Array<Dictionary<String, any Protocol>>>`. No test exercises the generator against a corpus of types extracted from real libraries.

**Gap 4: Snapshot/golden-file testing.** There are no golden-file tests that capture the complete output for a given ABI JSON input and detect any change. The `*OutputTests.cs` files check fragments but don't capture full output. This means small changes to emission order or whitespace don't trigger test failures, but more importantly, changes to type resolution that affect only specific type combinations go undetected.

**Gap 5: Swift wrapper compilation.** Unit tests verify the C# output but not the generated Swift wrapper code. The Swift wrapper must type-check against the library's types. When the generator emits a wrapper that references a type incorrectly (wrong module qualification, missing generic parameter), the failure only appears during `build-and-test.sh`, not in unit tests.

### What Test Strategy Would Catch Fragility

1. **Type projection property tests.** For a curated corpus of 200+ Swift types extracted from real libraries (including deeply nested generics, protocol compositions, optional existentials):
   - Assert: all type conversion paths produce the same C# type
   - Assert: the wrapper signature type, P/Invoke signature type, and call argument expression are mutually compatible

2. **Golden-file integration tests.** For each library in `BindingTesting/`:
   - Capture the full `Swift.{Module}.cs` output as a golden file
   - On each generator change, diff against the golden file
   - Require explicit approval for any delta

3. **Compile-only validation in CI.** After running `run-tests.sh`, also generate bindings for all 31 libraries and compile them. This is the ultimate integration test — if it compiles, it's consistent.

---

## 7. Marshalling Strategy Design

### Problem Statement

The current type conversion is fragmented across 4+ paths with ad-hoc string-based type markers. We need a unified abstraction that:
- Encapsulates Swift type → C# representation for both P/Invoke and public API
- Handles both parameter and return direction
- Composes for nested types
- Is testable in isolation
- Makes implicit contracts explicit

### Proposed Interface: `ITypeProjection`

```csharp
/// Represents the complete C# projection of a Swift type.
/// Created once per (TypeSpec, Context) pair and reused across all emission sites.
public interface ITypeProjection
{
    /// The C# type for public API signatures (e.g., "string", "IReadOnlyList<string>", "bool?")
    string PublicType { get; }

    /// The C# type for P/Invoke signatures (e.g., "SwiftString", "SwiftArray<SwiftString>", "[MarshalAs(UnmanagedType.U1)] bool")
    string PInvokeType { get; }

    /// Expression to convert from public type to P/Invoke type (parameter direction)
    /// e.g., "new SwiftString({param})" for string → SwiftString
    string? GetParameterConversion(string paramName);

    /// Expression to convert from P/Invoke type to public type (return direction)
    /// e.g., "{result}.ToString()" for SwiftString → string
    string? GetReturnConversion(string resultName);

    /// Whether the converted value needs disposal (using statement)
    bool RequiresDisposal { get; }

    /// P/Invoke attributes (e.g., [MarshalAs(UnmanagedType.U1)] for bool)
    string? PInvokeAttribute { get; }

    /// The call-site expression for passing this type to P/Invoke
    /// (e.g., "{param}.Payload.DangerousGetHandle()" for non-frozen structs)
    string GetCallExpression(string paramName);
}
```

### Concrete Projections

```csharp
// Simple value type — no conversion needed
public record BlittableProjection(string TypeName) : ITypeProjection
{
    public string PublicType => TypeName;
    public string PInvokeType => TypeName;
    public string? GetParameterConversion(string p) => null;
    public string? GetReturnConversion(string r) => null;
    public bool RequiresDisposal => false;
    public string? PInvokeAttribute => TypeName == "bool" ? "[MarshalAs(UnmanagedType.U1)]" : null;
    public string GetCallExpression(string p) => p;
}

// SwiftString ↔ string
public record StringProjection : ITypeProjection
{
    public string PublicType => "string";
    public string PInvokeType => "SwiftString";
    public string? GetParameterConversion(string p) => $"new SwiftString({p})";
    public string? GetReturnConversion(string r) => $"{r}.ToString()";
    public bool RequiresDisposal => true;
    public string? PInvokeAttribute => null;
    public string GetCallExpression(string p) => p;  // SwiftString is blittable
}

// Composable: Optional<T> wraps an inner projection
public record OptionalProjection(ITypeProjection Inner) : ITypeProjection
{
    public string PublicType => $"{Inner.PublicType}?";
    public string PInvokeType => $"SwiftOptional<{Inner.PInvokeType}>";
    // ... conversion logic wraps Inner's conversion
}

// Existential: any Protocol
public record ExistentialProjection(string ContainerType, string InterfaceType) : ITypeProjection
{
    public string PublicType => InterfaceType;
    public string PInvokeType => ContainerType;
    public string GetCallExpression(string p) =>
        $"((ISwiftExistentialConvertible<{ContainerType}>){p}).GetExistentialContainer()";
    // ...
}
```

### Example: Complex Real Case

**Swift:** `func process(_ items: [String: any ImageProcessing]?) -> [String]`

**Projection tree:**
```
ReturnType: ArrayProjection(
    element: StringProjection(),
    isParam: false
)
→ PublicType: "IReadOnlyList<string>"
→ PInvokeType: "SwiftArray<SwiftString>"
→ ReturnConversion: "{r}.AsProjected(e => e.ToString())"

Parameter: OptionalProjection(
    inner: DictionaryProjection(
        key: StringProjection(),
        value: ExistentialProjection("ExistentialContainer1", "IImageProcessing"),
        isParam: true
    )
)
→ PublicType: "IDictionary<string, IImageProcessing>?"
→ PInvokeType: "SwiftOptional<SwiftDictionary<SwiftString, ExistentialContainer1>>"
→ ParameterConversion: complex Select + FromDictionary wrapping
```

### Integration with Current Code

The `TypeProjectionFactory` would be the single entry point:

```csharp
public class TypeProjectionFactory
{
    private readonly ITypeDatabase _typeDatabase;

    public ITypeProjection Project(TypeSpec typeSpec, ProjectionContext context)
    {
        // Single decision tree that handles all cases:
        // Named types → check TypeDatabase, idiomatic conversions, ObjC bridging, native remapping
        // Closures → Action/Func projection
        // Tuples → ValueTuple projection
        // Existentials → ExistentialProjection
        // Bound generics → recursive composition
    }
}
```

All current callers of `GetIdiomaticCSharpType`, `TranslateBoundGenericTypeToCSharp`, `GetCSharpTypeName`, etc. would call `TypeProjectionFactory.Project()` instead. The projection is computed once and provides all needed representations (public, P/Invoke, conversion expressions).

---

## 8. Minimal Viable Refactor Plan

The following changes are ordered by impact-to-effort ratio, designed to be incremental (each step is independently valuable and testable).

### Step 1: Introduce `MarshalledType` Discriminated Union

**What to change:** Replace string-encoded type markers in `Parameter.Type` with a proper type.

**Files affected:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` — `Parameter` record, `Signature.CallArgumentsString()`
- All sites that construct `Parameter` objects (WrapperSignatureBuilder, PInvokeSignatureBuilder)

**New type:**
```csharp
public abstract record MarshalledType
{
    public record Simple(string CSharpType) : MarshalledType;
    public record Bool() : MarshalledType;
    public record Existential(string ContainerType, string PublicType) : MarshalledType;
    public record SimpleEnum(string UnderlyingType, string EnumType) : MarshalledType;
    public record ObjCBridged(string Type) : MarshalledType;
    public record NonFrozenPtr() : MarshalledType;
    public record EnumSafeHandle() : MarshalledType;
    public record SwiftClosure(string ClosureData) : MarshalledType;
    public record CdeclClosure(string CallbackName, string SourceName) : MarshalledType;
    public record NativeRemapped(string SwiftType, bool IsSafeHandle) : MarshalledType;
    public record AsyncCallback() : MarshalledType;
    public record AsyncContext() : MarshalledType;
    public record AsyncTask() : MarshalledType;
    // ... other variants as needed
}
```

**Effort:** 3-5 days
**Risk:** Low — purely internal refactor, no behavioral change
**Validation:** All existing tests pass unchanged. Add new tests that construct `MarshalledType` variants and verify serialization matches current string output.

### Step 2: Add Cross-Path Consistency Tests

**What to change:** Create a new test file `TypeProjectionConsistencyTests.cs` that:
1. Defines a corpus of 100+ `TypeSpec` values (extracted from real library ABI JSON)
2. For each TypeSpec, calls all four type conversion paths
3. Asserts they agree (or documents known intentional differences)

**Files affected:**
- New: `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeProjectionConsistencyTests.cs`

**Effort:** 3-5 days
**Risk:** Very low — pure test addition
**Validation:** Run the tests. Initial failures are expected and document the current divergences. Fix one divergence at a time.

### Step 3: Centralize Bool Marshalling

**What to change:** With `MarshalledType.Bool` from Step 1, update all 7 emission sites to use the type's intrinsic marshalling info instead of ad-hoc string checks.

**Files affected:**
- `EnumHandler.CaseConstruction.cs:133`
- `PInvokeEmitter.cs:664`
- `PInvokeHelperEmitter.cs:192`
- `EnumHandler.SimpleEnum.cs:327,334`
- `OperatorHandler.cs:463`

**Effort:** 1-2 hours (after Step 1)
**Risk:** Very low
**Validation:** Existing tests + search codebase for remaining `MarshalAs(UnmanagedType.U1)` to verify completeness.

### Step 4: Extract `TranslateTypeSpecForConversion` to a Shared Service

**What to change:** Move `WrapperSignatureBuilder.TranslateTypeSpecForConversion` from a private method to a static/injectable service that all type conversion paths can use as their `typeTranslator`.

**Files affected:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` — extract method
- New: `src/Swift.Bindings/src/Marshaler/TypeSpecTranslator.cs`
- Update callers in `ProtocolProxyEmitter.Helpers.cs`, `DefaultParameterOverloadEmitter.cs`, `ProtocolConformanceValidator.cs`, etc.

**Effort:** 3-5 days
**Risk:** Medium — changes the call sites for type translation
**Validation:** Cross-path consistency tests from Step 2 should show fewer divergences.

### Step 5: Replace Conductor Mutable State with Explicit Parameters

**What to change:**
- `CurrentPInvokeHelperContext` → pass as parameter to member emission methods
- `NestedTypeRenames` → compute during type pre-processing, store on TypeDecl
- `CompositionInterfaces` / `s_activeCompositionCollector` → pass an explicit collector object through the call chain

**Files affected:**
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` — remove mutable properties
- All type handlers that set these properties
- All member handlers that read them

**Effort:** 1 week
**Risk:** Low — largely mechanical parameter threading
**Validation:** Existing tests pass. Add a test that runs two modules in parallel to verify no cross-contamination.

### Step 6: Introduce `TypeProjectionFactory` (Longer Term)

**What to change:** Implement the `ITypeProjection` interface from Section 7 and `TypeProjectionFactory`. Initially, have it delegate to existing code paths. Then gradually migrate callers to use projections instead of calling 4 different conversion functions.

**Files affected:** Many — this is a cross-cutting change.
**Effort:** 2-3 weeks for initial implementation, 2-3 more weeks for full migration
**Risk:** Medium-high — fundamental architecture change
**Validation:** Cross-path consistency tests should be 100% green. Library validation should show zero regressions.

### Step 7: Golden-File Testing for Libraries

**What to change:** Capture and commit the generated `Swift.{Module}.cs` for each BindingTesting library. Add a CI step that regenerates and diffs.

**Files affected:**
- New: `BindingTesting/{Library}/expected-output/Swift.{Library}.cs` for each library
- New: CI script to regenerate and compare

**Effort:** 2-3 days
**Risk:** Very low — pure testing infrastructure
**Validation:** Any generator change that affects library output will be caught by the diff.

---

## Appendix: Key Question Answer

> Given what we've built and learned, if we started the generator from scratch today, what would we do differently? And what's the practical incremental path from where we are to where we should be?

### What We'd Do Differently

1. **Single type projection service.** One `TypeSpec → ITypeProjection` function, called once per (type, context) pair. All consumers get the same projection. No parallel paths, no string-encoded type markers.

2. **Explicit IR between analysis and emission.** The analysis phase produces a `ModuleEmitPlan` — a data structure capturing every decision (type classifications, member marshal plans, required Swift wrappers, required helper classes). The emission phase is a pure function from plan to strings. The plan is serializable, diffable, testable.

3. **Property-based cross-consistency tests from day one.** For any Swift type T: `publicType(T) == publicType(T)` regardless of which call path resolves it. This would have caught most library-breaking regressions before they shipped.

4. **Composable marshalling strategies.** Instead of 800-line methods with nested type checks, each type conversion is a small composable unit: `StringMarshal`, `ArrayMarshal<T>`, `OptionalMarshal<T>`, `ExistentialMarshal`. Nesting composes naturally: `OptionalMarshal<ArrayMarshal<StringMarshal>>`.

5. **No mutable state flowing through the Conductor.** All context is passed explicitly. No ThreadStatic, no mutable properties on shared objects, no flags set on model objects during emission.

### The Practical Incremental Path

The 7 steps in Section 8 get us from where we are to where we should be without a rewrite:

1. **Steps 1-3** (1-2 weeks) eliminate the most mechanical fragility (string-encoded types, scattered bool marshalling) with minimal risk.
2. **Steps 2 and 7** (1 week) add the testing infrastructure that catches regressions before they ship.
3. **Step 4** (3-5 days) reduces type conversion path divergence by sharing the resolution logic.
4. **Step 5** (1 week) makes the state flow explicit and testable.
5. **Step 6** (4-6 weeks) is the architectural goal — a unified type projection that eliminates the root cause of fragility.

Each step is independently valuable. If we stop after step 3, we've already eliminated a class of bugs. If we stop after step 5, the architecture is significantly cleaner. Step 6 is the long-term vision that makes the generator robust against any future type system extension.
