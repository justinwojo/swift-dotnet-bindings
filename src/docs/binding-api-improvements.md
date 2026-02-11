# Binding API Improvement Plan

**Created**: February 2026
**Source review**: `binding-review.md` (12 issues, 4 waves, quality scorecard)
**Purpose**: Track implementation status and add new issues discovered since the initial review

---

## Current State

A major refactor pass addressed the most critical issues from the initial review (`binding-review.md`). **11 of 12 original issues are fully resolved**, including the highest-impact P0 items (constructors, string properties), P1/P3 items (IntPtr mapping, Equals/GetHashCode, property suffixes, interface naming), and the WU1-WU5 pass (method naming, parameter normalization, array/subscript type conversion, unsafe removal, simple enums, IDisposable).

**What's fixed**: Real C# constructors, `string` properties, `T?` for optionals, `nint` for integers, `internal` Payload, non-throwing Equals/GetHashCode, clean interface names, no property Value suffixes, `IDisposable` via `ISwiftObject`, simple enums, verb-prefixed method names, type-derived parameter names, array/subscript element conversion, `unsafe` removed from public surface.

**What remains from the review**: ExistentialContainer in some closure parameters and proxy constructors (R6 partial — enum associated values now use typed interfaces).

**Post-WU Codex review fixes**: Protocol proxy getter/setter type asymmetry (interface uses idiomatic types, receivers marshal Swift ABI types), Optional<Array<String>> element conversion in WrapperEmitter.Marshalling, GetSwiftWrapperType raw element type safety, async-void Get prefix ordering, protocol param name normalization.

**AnyType reduction pass**: Eliminated 7 unique AnyType occurrences (optional existential bug fix, Bundle/CTFont/AnyHashable TypeDB registrations). Nuke AnyType lines 10→4. Remaining instances are structural.

**Enum existential promotion**: Enum associated values with protocol-typed parameters now use typed interfaces (`IImageProcessing`, `IImageDecoding`) instead of `ExistentialContainer{N}` in factory methods, TryGet out-parameters, and marshalling. Only applies when all protocols in the composition have TypeRecords with `Kind == Protocol` — unknown/unregistered protocols (e.g., `Swift.Error`) correctly keep their container types.

**#nullable enable**: All generated C# files (main bindings + SwiftUI bridge) now emit `#nullable enable`.

**New issues found post-review**: AnyType fallback has no type info (R7, partially addressed), async naming edge cases (N5), property collision logic (N6), default parameters/overloads.

---

## Issue Tracker

### Wave 1: Type Foundation (P0)

| # | Issue | Status | Implementation Notes |
|---|-------|--------|---------------------|
| R1 | `Init()` methods instead of constructors | **Done** | Real C# constructors emitted. Failable `init?` → `TryCreate()`. |
| R2 | `SwiftString` in property return types | **Done** | Properties return `string`. Type conversion gate removed for accessors. |
| R9 | `Payload` public / `IDisposable` | **Done** | `Payload` is `internal`. `ISwiftObject : IDisposable` (ISwiftObject.cs:9) provides transitive `IDisposable` on all generated types. `using` statements and dispose patterns work. |

### Wave 2: Type Safety (P1)

| # | Issue | Status | Implementation Notes |
|---|-------|--------|---------------------|
| R3 | `SwiftOptional<T>` instead of `T?` | **Done** | Converted in methods, properties, constructors, and subscripts/indexers. WU3 fixed the last edge case. |
| R4 | `IntPtr` for integer types | **Done** | Swift `Int` maps to `nint`. No `System.IntPtr` for non-pointer semantics. |
| R10 | `Equals`/`GetHashCode` throw | **Done** | Equatable types use `SwiftEquatable.Equals()`. Non-equatable types return `GetHashCode() => 0` (correct but O(n) for hash collections). No more throwing. |

### Wave 3: API Shape (P2)

| # | Issue | Status | Implementation Notes |
|---|-------|--------|---------------------|
| R5 | Simple enums are classes | **Done** | `EnumHandler.IsSimpleEnum` detection (line 94) emits real C# `enum` types for enums without associated values. |
| R6 | `ExistentialContainer` in public API | **Partial** | Enum associated values now use typed interfaces (`IImageProcessing`, `IImageDecoding`) for known protocols. `Error.DataLoadingFailed()` correctly keeps `ExistentialContainer1` (Swift.Error has no proxy). Remaining: closure parameters, some proxy constructors. |
| R8 | Parameter names: `arg0`, `_for`, `with` | **Done** | WU4: `GetPublicParameterName()` derives names from types, strips `_` prefixes, deduplicates. Internal codegen names unchanged. |
| — | Default parameters / overloads | **Open** | Swift methods with defaults emit only the full-parameter version. `DefaultParameterOverloadEmitter.cs` exists but scope is limited to wrapper-backed methods. |

### Wave 4: Polish (P3)

| # | Issue | Status | Implementation Notes |
|---|-------|--------|---------------------|
| R7 | `AnyType` fallback with no type info | **Partial** | AnyType reduction pass eliminated 7 unique occurrences: Optional existential in protocol interfaces (3 Nuke), Foundation.Bundle (1 Lottie), CoreText.CTFont (1 Lottie), Swift.AnyHashable (2 Nuke+Lottie). Remaining AnyType instances are structural (ArraySlice in protocols, Self type, generic params, Any/Any.Type). Original `[OriginalSwiftType]` attribute proposal still open. |
| R11 | Property `Value` suffixes | **Done** | Removed — no `ConfigurationValue`, `CacheValue`, etc. in generated output. |
| R12 | `ISwift*` interface prefix | **Done** | Interfaces use `I` + protocol name (`IImageProcessing`, `ICancellable`, etc.). |

---

## New Issues (Post-Review)

These issues were identified during analysis of generated output for Nuke and TestFramework but were not in the original binding-review.md.

### N1: Method Naming — Missing Verb Prefix and Double Async (**Done** — WU1)

**Priority**: P1
**Wave**: 2 or 3
**Status**: **Done** — WU1 implemented verb prefix detection (`Get` for noun-only return methods), double Async stripping, and void-return protection in `NameProvider.GetPublicMethodName()`. 18 unit tests. Post-WU1 integration fix added `Accept`/`Pass`/`Sum` verbs to `_verbPrefixes` (were causing false `Get` prefix on `AcceptsGenericParameters`, `PassThroughArray`, `Sum`, etc.).
**Impact**: Every async method with a noun-only name reads wrong to .NET developers

The generator's method naming logic (`NameProvider.cs:566-572`) does three things:
1. PascalCase the Swift function name
2. Check for property name collision
3. Append `Async` if the method is async

There is no verb-prefix logic. Swift method names are often nouns because they're part of a phrase with parameter labels: `data(for: request)` reads as "data for request." In C# that context is lost.

**Pattern A — Noun-only async methods need a verb prefix:**

| Swift | Current C# | Expected C# |
|-------|-----------|-------------|
| `func image(for: URL) async` | `ImageAsync(url)` | `GetImageAsync(url)` |
| `func data(for: ImageRequest) async` | `DataAsync(request)` | `GetDataAsync(request)` |
| `func response(for: URL) async` | `ResponseAsync(url)` | `GetResponseAsync(url)` |

**Pattern B — Swift names with "Async" prefix get double Async:**

| Swift | Current C# | Expected C# |
|-------|-----------|-------------|
| `func asyncGetString() async` | `AsyncGetStringAsync()` | `GetStringAsync()` |
| `func asyncStaticString() async` | `AsyncStaticStringAsync()` | `GetStaticStringAsync()` |
| `func asyncGetResult() async` | `AsyncGetResultAsync()` | `GetResultAsync()` |

**Pattern C — Methods that already have verbs are correct (no change needed):**

| Swift | Current C# | Correct? |
|-------|-----------|----------|
| `func loadImage(with:)` | `LoadImage(request)` | Yes |
| `func refreshTitle() async` | `RefreshTitleAsync()` | Yes |
| `func removeAll()` | `RemoveAll()` | Yes |

**Implementation approach in `NameProvider.GetPublicMethodName()`:**

1. **Strip leading "Async" prefix**: If the Swift name starts with `async`/`Async` (Swift convention for explicitly-named async methods), strip it before PascalCasing. The C# `Async` suffix already conveys async-ness.

2. **Add verb prefix for noun-only names**: If the method returns a value and the name doesn't start with a recognized verb, prepend `Get`. Verb detection heuristic — check first word against a known verb set:
   - **Common verbs (no prefix needed)**: `Get`, `Set`, `Create`, `Make`, `Build`, `Load`, `Fetch`, `Find`, `Search`, `Calculate`, `Compute`, `Parse`, `Convert`, `Transform`, `Process`, `Validate`, `Check`, `Is`, `Has`, `Can`, `Should`, `Will`, `Did`, `Remove`, `Delete`, `Clear`, `Reset`, `Start`, `Stop`, `Begin`, `End`, `Open`, `Close`, `Read`, `Write`, `Send`, `Receive`, `Push`, `Pop`, `Add`, `Insert`, `Append`, `Update`, `Refresh`, `Flush`, `Store`, `Save`, `Encode`, `Decode`, `Register`, `Unregister`, `Subscribe`, `Unsubscribe`, `Notify`, `Observe`, `Handle`, `Perform`, `Execute`, `Run`, `Apply`, `Sort`, `Filter`, `Map`, `Reduce`, `Merge`, `Split`, `Join`, `Format`, `Render`, `Draw`, `Layout`, `Configure`, `Initialize`, `Dispose`, `Cancel`, `Retry`, `Resume`, `Suspend`, `Invalidate`, `Prefetch`, `Preload`, `Cache`, `Evict`, `Purge`
   - **Heuristic limitation**: This won't be perfect for all Swift method names. For uncommon verbs or domain-specific terminology, the `Get` prefix may be wrong. Consider making the verb set configurable or providing an override mechanism.

3. **Void-returning methods**: Don't add `Get` prefix to void methods. `Flush()` should stay `Flush()`, not `GetFlush()`.

4. **Property-colliding methods**: The existing collision logic (append `Method`) should apply after verb prefix addition, not before.

**Files to modify**: `NameProvider.cs` — `GetPublicMethodName()` (line 566) and `ToPascalCase()` (line 111).

**Risk**: Over-prefixing. A method like `count()` might become `GetCount()` when the Swift intent is to perform a counting action, not retrieve a stored count. The verb set should be conservative (only well-known English verbs) and tested against actual library output.

**Test strategy**: Generate binding names for Nuke, Lottie, BlinkID, CryptoSwift and manually review all public method names. Build a snapshot test of method names per library to catch regressions.

---

### N2: Parameter Name Normalization (**Done** — WU4)

**Priority**: P2
**Wave**: 3
**Status**: **Done** — WU4 added `GetPublicParameterName()` and `GetPublicParameterNames()` to `NameProvider.cs`. Type-derived naming, `_` prefix stripping, operator `left`/`right`, dedup with numeric suffixes. Applied across WrapperEmitter, ProtocolHandler, ProtocolProxyEmitter, and ModuleHandler. 12 unit tests.
**Impact**: Poor IntelliSense experience, confusing parameter names

Extends R8 from the original review with specific normalization rules and implementation details.

**Problem patterns in generated output:**

| Pattern | Example | Expected |
|---------|---------|----------|
| Placeholder names | `Process(UIImage arg0)` | `Process(UIImage image)` |
| Swift external labels | `CachedData(string _for)` | `CachedData(string key)` |
| Keyword-prefixed labels | `ImageAsync(NSUrl _for)` | `GetImageAsync(NSUrl url)` |
| Swift `with` label | `ImageTask(NSUrl with)` | `GetImageTask(NSUrl url)` |

**Root cause**: The Swift ABI JSON contains two names per parameter:
- **External label** (argument label): what the caller writes at the call site (`for`, `with`, `_`)
- **Internal name** (parameter name): what the function body uses (`key`, `url`, `request`)

The generator currently uses the external label. It should prefer the internal name.

**Normalization rules (in priority order):**

1. **Prefer internal name over external label**: If the ABI JSON has both, use the internal name.
2. **Strip leading underscores**: `_for` → `for`, then apply rule 3.
3. **Replace Swift keywords used as labels**: `for` → derive from type name (e.g., `NSUrl` → `url`), `with` → derive from type name, `in` → derive from type name.
4. **Replace `arg0`/`arg1` placeholders**: Use type-derived name. For `UIImage arg0` → `image`. For `ImageRequest arg0` → `request`. For `String arg0` → `value`.
5. **Apply camelCase**: All parameter names in camelCase per C# convention.
6. **Deduplicate**: If two parameters would get the same name after normalization, append a numeric suffix (`url`, `url2`).

**Type-derived name heuristic**: Strip namespace, strip leading `I` for interfaces, camelCase. `Foundation.NSUrl` → `url`. `UIKit.UIImage` → `image`. `Swift.Nuke.ImageRequest` → `request`. `System.Int32` → `value`. `System.Boolean` → `flag`. For generic types, use the outer type name.

**Files to modify**: `NameProvider.cs` — add `GetParameterName()` method. `MethodSignature.cs` — call it when building parameter lists. May also need `SwiftABIParser.cs` if internal parameter names aren't currently parsed.

---

### N3: `unsafe` on Public Methods (**Done** — WU5)

**Priority**: P2
**Wave**: 3
**Status**: **Done** — WU5 removed `unsafe` from all public class declarations and method/constructor signatures. `unsafe` moved to body-level `unsafe { }` blocks. Kept on genuinely-required types: frozen struct value types (fixed byte buffers), protocol proxy classes (delegate* vtable), composition proxy classes (pointer ops). P/Invoke declarations auto-detect `void*`/`delegate*` for per-method `unsafe`. 7 unit tests. Post-WU5 integration fix added closure parameter detection to `_needsUnsafeBody` (methods with `delegate* unmanaged` from closure marshalling), `unsafe` on closure callback methods, enum factory methods, and `TupleTypeMetadata*` fields.
**Impact**: Forces callers into unsafe context unnecessarily

Every method that internally performs pointer operations is declared `public unsafe`. This forces consumer code to either use `unsafe` blocks or compile with `/unsafe`. For a high-level binding library, this is wrong — the unsafe operations are an internal implementation detail.

**Current pattern (every generated method):**
```csharp
public unsafe string GetName()
{
    SwiftSelf self = new SwiftSelf((IntPtr)Unsafe.AsPointer(ref this));
    // ... pointer operations ...
    return result.ToString();
}
```

**Target pattern:**
```csharp
public string GetName()
{
    unsafe
    {
        SwiftSelf self = new SwiftSelf((IntPtr)Unsafe.AsPointer(ref this));
        // ... pointer operations ...
    }
    return result.ToString();
}
```

Or, for methods where the entire body is unsafe, use a private unsafe helper:
```csharp
public string GetName() => GetName_PInvoke();

private unsafe string GetName_PInvoke()
{
    // ... all the pointer work ...
}
```

**Files to modify**: `WrapperEmitter.cs` and related emitters that generate method signatures. The `unsafe` keyword needs to move from the method declaration to an internal block or helper.

**Consideration**: Some methods return types that themselves require unsafe context (e.g., pointer types). These legitimately need `unsafe` on the public signature. The fix should only remove `unsafe` from methods where the return type and all parameter types are managed.

---

### N4: Array Element Type Conversion (**Done** — WU2)

**Priority**: P1
**Wave**: 2
**Status**: **Done** — WU2 added recursive element type conversion in `TypeConversionHandler.GetIdiomaticCSharpType()`. `IReadOnlyList<SwiftString>` → `IReadOnlyList<string>`. Return conversion uses `.Select()` projection for converted elements. 6 unit tests.
**Impact**: Collections of strings require manual element conversion

`TypeConversionHandler` converts the array container (`SwiftArray<T> → IReadOnlyList<T>`) but doesn't recursively convert the element type. This produces:

```csharp
// Current:
public IReadOnlyList<Swift.SwiftString> GetNames()  // Elements are SwiftString

// Expected:
public IReadOnlyList<string> GetNames()              // Elements are string
```

The conversion should be recursive: if the element type of `SwiftArray<T>` is itself a convertible type (e.g., `SwiftString`), the public API should show `IReadOnlyList<string>`.

**Implementation**: In `TypeConversionHandler.GetIdiomaticCSharpType()`, when handling `SwiftArray<T>`, recursively call `GetIdiomaticCSharpType()` on the element type `T`. If the element converts (e.g., `SwiftString → string`), use the converted element type in the return type.

**Marshalling complexity**: The return conversion needs to map elements. Instead of returning the `SwiftArray<SwiftString>` directly (which implements `IReadOnlyList<SwiftString>`), it needs a `.Select(s => s.ToString()).ToList()` or a lazy wrapper. This adds overhead vs. the current zero-copy cast.

**Files to modify**: `TypeConversionHandler.cs` — `GetIdiomaticCSharpType()` for array element recursion, `GetReturnConversion()` for element-level marshalling.

---

### N5: Async Method Naming Convention

**Priority**: P2
**Wave**: 3
**Impact**: Inconsistent async method naming breaks .NET conventions

Related to N1 but specifically about the `Async` suffix convention. .NET guidelines (TAP pattern) state that all `Task`-returning methods should end with `Async`. The generator does append `Async`, but there are edge cases:

| Pattern | Example | Issue |
|---------|---------|-------|
| Callback-based methods | `LoadImage(request, completion)` | No `Async` suffix, but takes a callback — misleading |
| Library-specific task types | `ImageTask(url)` | Returns Nuke's `ImageTask`, not `System.Threading.Tasks.Task` |
| Double naming | `AsyncGetStringAsync()` | Redundant — see N1 Pattern B |

**Callback methods**: Methods that accept a completion callback could offer a Task-based overload generated by the binding. This is an advanced feature (generating `TaskCompletionSource` wrappers) but would significantly improve the async story.

**Library task types**: When the return type is a library-specific "task-like" type (Nuke's `ImageTask`), the method name shouldn't have `Async` suffix since it doesn't return `Task<T>`. The current generator correctly avoids this — only methods returning `System.Threading.Tasks.Task<T>` get the suffix. This is correct behavior.

---

### N6: Method Names — Property-Like Collision with Value Suffix

**Priority**: P3
**Wave**: 4
**Impact**: Enum and nested type property names have unnecessary suffixes

Related to R11 but covering the method/property collision logic more broadly. When a type has both a nested type and a property of the same name, the generator appends `Value` to the property:

```csharp
public class ImageResponse
{
    public class CacheType { ... }           // Nested type
    public CacheType CacheTypeValue { get; } // Property — "Value" suffix to avoid collision
}
```

In C#, the compiler can disambiguate `response.CacheType` (property access) from `ImageResponse.CacheType` (type reference). The `Value` suffix is unnecessary and clutters the API.

**Files to modify**: `NameProvider.cs` — property naming collision logic. May need to verify that C# compiler handles the ambiguity correctly in all contexts (e.g., generic type arguments, `typeof()`, `nameof()`).

---

### Post-WU Codex Review Fixes (**Done**)

**Priority**: P0/P1
**Status**: **Done** — 3 P0 fixes + 2 P1 fixes + 1 additional bug found during testing. 17 regression tests added.

Codex review of the WU1-WU6 changes identified marshalling correctness issues in protocol proxy receivers and the parameter conversion pipeline. All fixes verified against 4 real-world libraries (0 binding errors each).

| # | Severity | Issue | Fix |
|---|----------|-------|-----|
| CR1 | P0 | Protocol proxy getter receivers marshal idiomatic C# types (string, IReadOnlyList) into MarshalToSwiftBuffer, which requires Swift ABI types (SwiftString, SwiftArray) | Added `GetParameterConversion` reverse-conversion in `ProtocolProxyEmitter.Receivers.cs` getter path |
| CR2 | P0 | Optional<Array<String>> parameter in WrapperEmitter.Marshalling missing `.Select(e => new SwiftString(e))` element projection | Added IsSwiftString check on inner array element type in Optional<Array> branch |
| CR3 | P1 | Protocol proxy setter `GetReturnConversion` didn't handle Optional<Array<String>> | Added Optional<Array<T>> handling in `TypeConversionHandler.GetReturnConversion` before generic fallback |
| CR4 | P1 | `GetSwiftWrapperType` used `GetElementType()` (eagerly converts SwiftString→string) instead of `GetRawElementType()` for Optional/Array marshalling | Fixed to use `GetRawElementType()` — only public API return type uses converted names |
| CR5 | P1 | `hasReturnValue` captured AFTER async conversion turns void→Task, causing wrong `Get` prefix on async void methods | Captured `hasReturnValue` BEFORE async type conversion |
| CR6 | — | `GetParameterConversion` for Optional<String> passed raw `string` to `NewSome()` where `SwiftString` expected | Added IsSwiftString inner check to wrap with `new SwiftString()` in the NewSome call |

**Files modified**: `ProtocolProxyEmitter.Receivers.cs`, `WrapperEmitter.Marshalling.cs`, `TypeConversionHandler.cs`
**Tests added**: 5 in TypeConversionHandlerTests, 4 in ProtocolProxyEmitterTests

---

### AnyType Reduction Pass (**Done**)

**Priority**: P1 (R7 partial)
**Status**: **Done** — Eliminated 7 unique AnyType occurrences across Nuke and Lottie. Nuke AnyType lines: 10 → 4.

Four categories of fixes, each addressing a different root cause of AnyType fallback:

| # | Fix | Libraries | Unique eliminated |
|---|-----|-----------|-------------------|
| AT1 | Optional existential in protocol interface methods — `GetIdiomaticCSharpType` intercepted `Optional<any P>` before `ExistentialHandler` could resolve it | Nuke | 3 (×2 with proxy = 6 lines) |
| AT2 | Foundation.Bundle (NSBundle) TypeDB registration | Lottie | 1 |
| AT3 | CoreText.CTFont TypeDB registration | Lottie | 1 |
| AT4 | Swift.AnyHashable TypeDB registration + runtime struct | Nuke, Lottie | 2 |

**AT1 detail**: `TypeConversionHandler.GetIdiomaticCSharpType()` handled `Optional<T>` by calling `GetElementType()` which fell through to `GetTypeRecordOrAnyType` for existential inner types. Fix: bail out of Optional handling when inner type is existential (mirrors existing Closure bail-out), letting `ExistentialHandler` resolve it. Applied to `ProtocolHandler.GetCSharpTypeName()` (interface signatures) and `ProtocolProxyEmitter.InterfaceImpl.EmitMethodImplementation()` (proxy method signatures). Receiver callbacks guard optional-existential returns with zeroed buffer (Optional.none) since C# can't construct valid Swift existential containers (needs type metadata + witness tables).

**Files modified**: `TypeConversionHandler.cs`, `ProtocolHandler.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ProtocolProxyEmitter.Helpers.cs`, `ProtocolProxyEmitter.Receivers.cs`, `FoundationDatabase.xml`, `CoreTextDatabase.xml` (new), `SwiftDatabase.xml`, `AnyHashable.cs` (new), `Program.cs`

**Remaining AnyType** (structural — not fixable without architecture changes): ArraySlice in protocol interfaces (15), Protocol Self type (6), Any/Any.Type (3), generic type arguments (4), associated type protocols (2), cross-module nested types (1), no C# type (1), closure containing ArraySlice (1).

---

## Cross-Cutting Concerns

These affect multiple waves and should be addressed incrementally:

### Exception Mapping for Swift `throws`

**Current**: All Swift errors wrapped in generic `SwiftRuntimeException`.
**Target**: `SwiftException<TError>` with access to the Swift error enum's case and associated values.
**Incremental**: Improve as throwing methods are touched in each wave.

### CancellationToken on Async Methods

**Current**: Async methods have no cancellation support.
**Target**: Optional `CancellationToken` parameter on all `Task`-returning methods.
**Incremental**: Add as async methods are renamed/restructured in waves 2-3.

### XML Doc Comments

**Current**: Phase K added doc comment generation from Swift symbol graphs.
**Status**: Done — `--symbolgraph` CLI option extracts Swift doc comments and emits C# XML docs.
**Remaining**: Ensure doc comments survive all the naming/type changes in waves 1-4. May need to update param name references in `<param>` tags after N2 parameter normalization.

### Nullable Reference Annotations

**Current**: **Done** — All generated C# files emit `#nullable enable`. Main bindings (`ModuleHandler.cs:84`) and SwiftUI bridge (`SwiftUIBridgeEmitter.cs:697`) both emit the directive.
**Target**: All generated files enable nullable context. Swift non-optional → non-null. Swift optional → nullable.
**Dependency**: R3 (SwiftOptional → T?) already complete.

---

## Quality Scorecard

Track these metrics per generator release. All must reach gate value before external release.

| Metric | Gate | Status |
|--------|------|--------|
| Public `Init()` instance methods (should be ctors) | 0 | **Done** (R1) |
| Public `SwiftString` properties | 0 | **Done** (R2) |
| Public `SwiftOptional<T>` | 0 | **Done** (R3 — subscript edge case fixed by WU3) |
| Public `IntPtr` for non-pointer semantics | 0 | **Done** (R4) |
| Public `ExistentialContainer*` | 0 | **Partial** (R6 — enum associated values promoted to interfaces; closures/proxy ctors remain) |
| `arg0`/`arg1` parameter names | 0 | **Done** (R8/N2 — WU4) |
| `Equals`/`GetHashCode` that throw | 0 | **Done** (R10) |
| Types declaring `IDisposable` on interface list | all | **Done** (R9 — `ISwiftObject : IDisposable` transitive) |
| Public `Payload` property | 0 | **Done** (R9 — now `internal`) |
| Noun-only async methods without verb prefix | 0 | **Done** (N1 — WU1) |
| Double `Async` prefix+suffix | 0 | **Done** (N1 — WU1) |
| `IReadOnlyList<SwiftString>` (unconverted elements) | 0 | **Done** (N4 — WU2) |
| Public methods requiring `unsafe` caller context | 0 | **Done** (N3 — WU5, only genuinely needed types retain unsafe) |
| Missing `#nullable enable` | 0 | **Done** (main bindings + SwiftUI bridge) |
| Golden scenarios compile without interop types | 3/3 | 0/3 |

---

## Implementation Priority

Based on impact and effort. Items marked **Done** from the refactor pass are excluded.

**Done (WU1-WU5 pass):**
1. ~~**N1 — Method naming**~~: **Done** (WU1) — Verb prefix + strip double Async in `NameProvider.GetPublicMethodName()`.
2. ~~**N4 — Array element conversion**~~: **Done** (WU2) — Recursive element conversion in `TypeConversionHandler`.
3. **R9 — IDisposable**: **Done**. `ISwiftObject : IDisposable` (ISwiftObject.cs:9) provides transitive `IDisposable` on all generated types.
4. ~~**R3 partial — SwiftOptional in subscripts**~~: **Done** (WU3) — Subscript type conversion in ProtocolHandler + InterfaceImpl.
5. ~~**N2 — Parameter names**~~: **Done** (WU4) — `GetPublicParameterName()` in NameProvider, applied across all emitters.
6. ~~**R5 — Simple enums as C# enums**~~: **Done** (pre-existing) — `EnumHandler.IsSimpleEnum` path.
7. ~~**N3 — Remove public unsafe**~~: **Done** (WU5) — `unsafe` moved to body blocks, kept only where genuinely needed.

**Next (structural changes):**
8. ~~**R6 partial — ExistentialContainer in enum associated values**~~: **Done** — `GetPublicCSharpTypeNameForEnumCase()` + `AllProtocolsHaveTypeRecords()` gate. Factory signatures, TryGet out-params, and marshalling all use typed interfaces for known protocols. Remaining: closure parameters, proxy constructors.
9. **R7 — AnyType original type attribute**: Add `[OriginalSwiftType("Module.TypeName")]` when falling back to AnyType. Most Nuke instances resolved by AnyType reduction pass; remaining instances are structural (ArraySlice in protocols, Self type, generic params).
10. ~~**Nullable annotations**~~: **Done** — `#nullable enable` in main bindings + SwiftUI bridge.

**Polish:**
11. **N5/N6 — Async naming edge cases, property collision logic**
12. **R6 remaining — ExistentialContainer in closures and proxy constructors**

---

## Relationship to Other Documents

| Document | Relationship |
|----------|-------------|
| `binding-review.md` | Source review. Issues R1-R12 and Waves 1-4 originate there. |
| `roadmap.md` | This work is a prerequisite for DX-1 (external consumption). Updates needed to reflect this phase. |
| `developer-experience.md` | DX-1 through DX-4 assume a usable API surface. This plan gets the API there. |
| `testframework-review.md` | TestFramework hardening (compile gate, baseline budgets) should run in parallel to catch regressions from these changes. |
| `CURRENT-STATUS.md` | Tracks current baselines and development history. |
