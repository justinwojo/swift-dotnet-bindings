# CoGater inventory — emission-fixable vs. essential-normalization

**Captured**: 2026-04-28
**Branch**: `1.0-milestones`
**Companion**: `architecture-gameplan.md` §M3 (this is M3's first session); `phase0-report-staleness.md` (worked example for Pattern 1).

## Why this doc exists

M3 of the architecture gameplan is "Improve emitted API surface" — fewer post-emission text rewrites stripping wrappers, fewer false-positive type suppressions. The gameplan opens with: *"Classify each handler in `SwiftWrapperPostProcessor`, `CSharpWrapperCoGater` (Steps D–G), `ProcessSuppressedProxyReferencesInDirectory`, and `SimulatorOnlyMemberDetector` as either 'we shouldn't have emitted this' (fixable at emission time) or 'Swift compiler output normalization' (essential, keep). The inventory itself is the deliverable for the first session in M3."*

Per Open Question #3 in the gameplan, the follow-up session targets the top ~3 by volume or any class whose fix cost is < 1 session. The "Top fix candidates" section at the bottom is that shortlist.

## Rubric

- **A — Shouldn't have emitted this**: the wrapper/cogater is papering over an emission bug or emission convention. The emitter has (or could have) the information needed to avoid producing the broken output. We could prevent emission upstream and **delete this handler** (or reduce it to a defensive assertion).
- **B — Swift output normalization (essential)**: the wrapper is normalizing legitimate compiler output, ABI-JSON conventions, or pipeline-ordering realities we cannot move into the emitter. Must stay.
- **C — Unsure / needs investigation**: classification depends on a question we can't answer from a static read of the code.

The classifications below are informed by a Plan-subagent adjudication of edge cases (Steps D–G, Pattern 5, Rule D, NSInvocation filter); rationale notes flag where Plan disagreed with the obvious-looking call.

## Worked example — the GRDB case (already evidenced)

`phase0-report-staleness.md` showed that `binding-emission-report.json` for GRDB on `1.0-milestones@d5eccf2f` reports:

```json
"conformanceDecisions": {
  "emittedInSource": 18,
  "skippedAtEmission": 17,
  "note": "Emitted conformances are stripped by post-processor Pattern 1 (unconditional EveryProtocol removal)"
}
```

That's 18 EveryProtocol conformances emitted, then stripped post-emission because they reference module-internal types. This is the canonical **A-class** case: the emitter knows internal types are involved (the ABI parser populates `internalTypeNames`), the emitter chose to emit anyway, and Pattern 1 removes the result. If the emitter refused to emit a conformance whose body references an internal type, Pattern 1's per-block scanning would have nothing to strip on GRDB.

Pattern 1 is therefore the rubric exemplar for "A": same root cause (ABI parser surfaces internal types; emitter renders them faithfully) drives Patterns 1, 3, 3c, the `EveryProtocol()` placeholder family (2(a), 4), and the `ReferencesInternalType` filter machinery itself. Fixing the upstream emission gate (don't emit members whose signatures touch internal types) takes a substantial slice of the post-processor with it.

---

# Subsystem 1 — `SwiftWrapperPostProcessor`

**File**: `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs` (596 lines)
**Public entry point**: `Process(...)` at line 79; called from `SwiftWrapperCompiler.cs:202` (per-slice loop) and `SwiftWrapperCompiler.cs:606` (second compilation pass).
**Pattern execution order** (sequential walk over input lines): Pattern 1 → 2 → 2b → 3 → 3c → 4 → module/type collision regex pass.

## Pattern 1 — EveryProtocol blocks referencing internal / Swift-unavailable types

- **Location**: lines 94–125 (block detection); helper predicates at 527–559.
- **What it does**: For each line starting with `extension EveryProtocol`, `class EveryProtocol`, or `public final class EveryProtocol`, finds the brace-delimited block. Preserves: the class definition itself, Codable/Error stub conformances, and empty-body composition extensions. Otherwise scans the body — strips the entire block if it references any name in `internalTypeNames` or any name in `SwiftUnavailableTypes` (`{"NSInvocation"}`). Stripped block's `@_cdecl`/`@_silgen_name` symbols are collected into `StrippedSymbols` for downstream cogating.
- **Before/after**:
  ```swift
  // Before — InternalType is module-internal:
  extension EveryProtocol: SomeProtocol {
      var prop: InternalType { fatalError() }
  }
  // After: block removed; PInvoke_<conformance> symbol added to StrippedSymbols
  ```
- **Classification**: **A**. The emitter knows the protocol member's type because the ABI parser populates the type record; it also has `internalTypeNames`. Emitting then stripping is wasteful and turns `binding-report.json` into a lie (see `phase0-report-staleness.md`).
- **Upstream fix location**: `EveryProtocolEmitter` / `ProtocolHandler` — the conformance-emission gate that decides whether to emit the body for a given protocol member. The same `internalTypeNames` set used here is computed in `Program.cs:CollectInternalTypeNames` (lines 1065–1102) and is available pre-emission.

## Pattern 2 — `@_silgen_name` / `@_cdecl` function blocks (broken bodies)

- **Location**: lines 127–151; `IsSilgenNameBroken` at 334–364.
- **What it does**: For each line starting with `@_silgen_name(`, `@_cdecl(`, or the inline-`@MainActor` form, finds the function body and strips it if (a) `IsSilgenNameBroken` returns true, (b) body references an internal type, or (c) body references `NSInvocation`. After stripping, `RemoveTrailingWrapperPreamble` cleans up dangling `@available` / `@MainActor` / wrapper-comment preamble lines.
- **Sub-pattern 2(a) — `EveryProtocol()` placeholder** (lines 336–348). Strips wrapper bodies that use `EveryProtocol()` as a stub for unimplemented conformances. Carve-out preserves `Get_EveryProtocol_*`, `SetVtable`, `_vtable`, `SBW_CreateEveryProtocol`, `SBW_ReleaseEveryProtocol`, `SBW_GetMetadata_EveryProtocol`. The file comment notes: *"Sub-patterns 2(b)–2(f) were removed in Phase 1 of the architecture refactoring … prevented at emission time."*
- **Sub-pattern 2(g) — closure type in `.load(as:)` metatype context** (lines 353–362). Strips bodies containing `.load(as: @escaping` or `.load(as: @Sendable`. Comment: *"Prevented at emission time by `CanConvertToCdecl` rejecting closure params."* `onSafetyNetWarning` fires when this triggers because it shouldn't be reached.
- **Before/after** (sub-pattern 2(g)):
  ```swift
  // Before:
  @_cdecl("SBW_callWithOptionalStringReturn_optbuf")
  public func PInvoke_callWithOptionalStringReturn(_ handler: UnsafeRawPointer, ...) {
      let handlerVal = handler.load(as: @escaping (Int32) -> Optional<String>.self)
      ...
  }
  // After: block removed
  ```
- **Classification**: **A**. Both surviving sub-patterns explicitly self-identify as safety nets for emission bugs that "should be prevented" upstream. The 2(b)–2(f) historical removals demonstrate the precedent — safety-net handlers get deleted once the emission gate is solid.
- **Upstream fix location**:
  - 2(a): `EveryProtocolEmitter` / `MethodWrapperEmitter` — the emission path that produces a `@_cdecl` thunk for a conformance that has `fatalError()` body. Skip the wrapper when the body would be a stub.
  - 2(g): `CanConvertToCdecl` (already gates this; verify completeness — closure-typed `UnsafeRawPointer.load(as:)` paths must all funnel through this gate).

## Pattern 2b — Standalone `@MainActor` preceding broken `@_silgen_name` / `@_cdecl`

- **Location**: lines 153–177.
- **What it does**: When a line is exactly `@MainActor` and the next line opens a `@_silgen_name(` / `@_cdecl(` block, applies the same broken-body checks as Pattern 2 to the next block. If broken, strips both the `@MainActor` line and the function block. Without this, stripping the function block alone would leave an orphaned `@MainActor` attribute attached to the next declaration ("expected declaration" Swift error). `ConstructorWrapperEmitter` emits this two-line form for constructors.
- **Classification**: **A**. The two-line `@MainActor` / `@_cdecl` format is our own emission choice (`ConstructorWrapperEmitter`); the broken-body cases are the same A-class issues as Pattern 2. Same upstream fix removes both.
- **Upstream fix location**: same as Pattern 2 — once Pattern 2's underlying emission bugs are gated, no broken constructor wrapper bodies exist for Pattern 2b to clean up.

## Pattern 3 — Non-EveryProtocol extension blocks

- **Location**: lines 178–198; `IsExtensionBroken` at 370–387.
- **What it does**: For each `extension X { … }` block (excluding `extension EveryProtocol …` which Pattern 1 owns), checks header *and* body. Strips if (a) body contains `EveryProtocol()` outside the system-function carve-out, (b) header or body references any name in `internalTypeNames`, or (c) header or body references `NSInvocation`. The header check exists for cases like `extension XMLCoder.SharedBox: _SBW_…` where the type being extended is module-internal.
- **Classification**: **A**. Same root cause as Pattern 1: emitter generates extension-scoped wrappers for types whose signatures (or in this case, the type itself) are module-internal.
- **Upstream fix location**: extension-emitting handlers (`ExtensionEmitter` family, type-handler emit paths). Refuse to emit an extension whose extended type or signature reaches into `internalTypeNames`. Same set is already available pre-emission.

## Pattern 3c — Private `_SBW_` dispatch protocol declarations

- **Location**: lines 200–217.
- **What it does**: For each `private protocol _SBW_…` block, strips it if its header or body references an internal type or `NSInvocation`. The `_SBW_` protocols are part of the generic factory dispatch pattern. When their associated-type constraints reference an internal type (e.g. `SharedBox<T>` where `SharedBox` is internal), they fail to compile.
- **Classification**: **A**. `_SBW_` protocols are a pure generator construct; the emitter has full knowledge of which types are referenced.
- **Upstream fix location**: the generic-factory dispatch emitter that produces `_SBW_` protocols (likely in protocol-bridging emitter family). Refuse to emit a `_SBW_` protocol whose associated-type constraints touch `internalTypeNames`.

## Pattern 4 — Standalone `public func SBW_` / `public func PInvoke_` blocks

- **Location**: lines 219–236; `IsStandaloneFuncBroken` at 393–408.
- **What it does**: For each `public func SBW_…` or `public func PInvoke_…` block (no leading `@_silgen_name` / `@_cdecl` — those are Pattern 2's), strips it on the same conditions: `EveryProtocol()` placeholder, internal type, or `NSInvocation`. After stripping, calls `RemoveTrailingWrapperPreamble` (the Stripe regression test at line 810 documents why: leaving the preamble caused "duplicate attribute" errors when `@_cdecl` re-attached to the next declaration).
- **Classification**: **A**. `SBW_` and `PInvoke_` are generator-prefixed names; same emitter-knowledge case as Pattern 2.
- **Upstream fix location**: same wrapper-emitting paths as Pattern 2. Once the upstream emission gates are tight, the `EveryProtocol()` placeholder and internal-type-reference cases vanish.

## Pattern 5 — Module/type name collision rewrite

- **Location**: lines 242–283; regex at 253–254.
- **What it does**: Only runs when `moduleNameForCollision` is non-null (i.e., the module contains a public type with the same identifier). Applies regex `\b<moduleName>\.(\w+(?:\.\w+)*)` to every non-`import` line and strips the module prefix unless the immediate child name is in `nestedTypesInCollidingClass`. Comment: *"When a module has a public type with the same name (e.g., module 'Reachability' containing class 'Reachability'), Swift resolves bare 'Reachability' as the type, not the module."*
- **Before/after**:
  ```swift
  // Before (module "Reachability" has class "Reachability"):
  let result = Reachability.Reachability()
  // After:
  let result = Reachability()
  ```
- **Classification**: **A**. Both pieces of information needed to avoid the collision are already known to the emitter at emission time: the module name, and the set of public type names in the module. The collision is detectable pre-emission. The regex post-pass is fragile (Plan adjudication concurred: emitter-time qualification is the correct fix).
- **Upstream fix location**: type-reference qualification logic in the emitter (search for places that prepend `module + "."` to type names — likely `TypeProjector`-adjacent or in the printer that materialises type names). Detect collision at emit time; emit unqualified for the colliding module.

## Cross-cutting filter — `ReferencesInternalType`

- **Location**: lines 493–507; `internalTypeNames` set populated by `Program.cs:CollectInternalTypeNames` (lines 1065–1102).
- **What it does**: Word-boundary regex check on block bodies for any name in `internalTypeNames` (which is the union of module-internal type names from the ABI plus the underscore-suppressed type set, with name-collision resolution against public types).
- **Classification**: **A** (root cause for Patterns 1, 3, 3c; contributes to Patterns 2, 4). Not a separate handler but the most-reused filter; fixing the upstream emission gate (don't emit signatures that reference internal types) makes the filter dead-code at the post-process layer.
- **Upstream fix location**: type-resolution in the emitter — when resolving a `TypeSpec` for a member signature, refuse to emit the member if the resolution surfaces an internal type. This is adjacent to (but distinct from) the M4 `TypeResolver` central seam.

## Cross-cutting filter — `ReferencesSwiftUnavailableType` (NSInvocation)

- **Location**: lines 513–521. Set is currently `{"NSInvocation"}`.
- **What it does**: Strips wrapper bodies that reference `NSInvocation`. Comment: *"types exist in ObjC headers but are annotated with NS_SWIFT_UNAVAILABLE."*
- **Classification**: **C** (Plan adjudication: depends on whether the ABI JSON reliably exposes the `NS_SWIFT_UNAVAILABLE` annotation for transitively-reached types).
- **Question to resolve**: Does the Swift ABI JSON carry the `NS_SWIFT_UNAVAILABLE` (or equivalent Swift-availability) signal for ObjC-bridged types reached transitively (e.g., as a parameter type on a method whose owning type is otherwise visible)? If yes, this is **A** — emitter detects and skips. If no, the post-process filter is the only signal we have and it's **B**. The single-element set today (`NSInvocation` only) makes either path low-cost.

---

# Subsystem 2 — `CSharpWrapperCoGater` Steps D–G

**File**: `src/Swift.Bindings/src/Configuration/CSharpWrapperCoGater.cs` (2254 lines)
**Public entry point**: `ProcessDirectory(directory, strippedSymbols, logger)` at line 152, invoked from `Program.cs:758` after `SwiftWrapperCompiler` returns its `StrippedSymbols` set. Per-file pipeline runs Steps A → B → C → D → E → F → G (Steps A–C are the P/Invoke transitive-closure detection that produce the removal set; D–G are gap-fillers for patterns Step B's transitive closure cannot reach).

## Step D — `StripOrphanedLazyAccessors`

- **Location**: lines 908–954.
- **What it does**: When Step B strips a `_lazy_X` backing field (because its initialiser lambda calls a stripped P/Invoke), the corresponding expression-bodied property `public static T Y => _lazy_X.Value;` is left dangling. Step D collects every `_lazy_<name>` token from already-removed lines, scopes them by enclosing type (two enums in the same file can share names like `_lazy_none`), and strips expression-bodied property declarations that reference the removed lazy field. Emitted by `EnumHandler.cs:580` and `EnumHandler.RawRepresentable.cs:422`.
- **Before/after**:
  ```csharp
  // Before — both lines emitted; PInvoke_CaseByIndex stripped at wrapper-compile → _lazy_none stripped by Step B → property dangles
  private static readonly Lazy<MyEnum> _lazy_none = new(() => { IntPtr ptr = PInvoke_CaseByIndex(0); ... });
  public static MyEnum None => _lazy_none.Value;
  // After: both lines gone (Step B got the field; Step D got the property)
  ```
- **Classification**: **B** (Plan adjudication: the `Lazy<T>` field IS the cache; collapsing the split would break thread-safe singleton caching). Step D legitimately handles downstream stripping fallout that the cache mechanism makes inevitable.
- **Why not A**: an emit-time fix would have to either (a) eliminate the `Lazy<T>` cache entirely (regresses thread-safety + performance) or (b) embed the lazy-field reference in a form Step B's caller-detection follows (but Step B follows P/Invoke edges, not C# field references — a cleaner re-architecture would be to teach Step B to follow C# field references too, which is a Step B improvement, not an emission fix).

## Step E — `StripDanglingToString`

- **Location**: lines 960–987.
- **What it does**: When the emitter renders a Swift type conforming to `CustomStringConvertible`, it emits `public override string ToString() => Description;` at `TypeHandlerHelpers.cs:1217`, gated on `WasEmitted` for the `Description` property. If `Description` is later stripped (its P/Invoke gone), `ToString()` dangles. Step E scans for `public override string ToString() => <name>;`, looks for a property declaration of `<name>` in the enclosing class scope's removed-line set, and strips `ToString()` (plus preamble) if found.
- **Before/after**:
  ```csharp
  // Before:
  public string Description { get { /* P/Invoke */ } }   // stripped by Step B
  public override string ToString() => Description;       // dangles → Step E strips
  // After: both gone
  ```
- **Classification**: **A**. The emitter owns the decision — `WasEmitted` is the right signal — but checks it at the wrong time (emission rather than after wrapper-compile stripping resolves). Rubric note: A here covers "generator-owned and avoidable upstream of the post-process text rewrite," not solely "decided at the moment of first emission." Implementing the fix may require deferring the decision past initial emission, which slightly stretches the rubric's "emission-time" wording.
- **Upstream fix location**: defer the `ToString()` emission decision until after `CSharpWrapperCoGater` runs. Either (a) emit `ToString()` lazily as part of a post-cogating C# emit phase, or (b) emit `ToString()` to call the same P/Invoke that `Description` calls (so it falls inside Step B's transitive closure and gets stripped together — simpler, preferred).

## Step F — `StripOrphanedNarrowingOverloads`

- **Location**: lines 1788–1941. Emitter source: `NativeIntOverloadEmitter.cs:133`.
- **What it does**: `NativeIntOverloadEmitter` emits `int`/`uint` convenience overloads that forward to the `nint`/`nuint` versions: `Method(int x) => Method((nint)x);` and `this[int x] => this[(nint)x];`. When the `nint`/`nuint` version is stripped, the convenience overload becomes CS1501/CS1503. Step F handles three sub-cases: (1) single-line expression-bodied indexers, (2) multi-line block-bodied indexers, (3) expression-bodied method overloads. For each, confirms no surviving `nint`/`nuint` overload exists in the same type-scope, then strips.
- **Before/after**:
  ```csharp
  // Before:
  public void Skip(nint count) { /* P/Invoke */ }      // stripped
  public void Skip(int count) => Skip((nint)count);    // dangles → Step F strips
  ```
- **Classification**: **B** (Plan adjudication: narrowing overloads reference the wide method by C# identity, not by P/Invoke entry point. Step B fundamentally walks P/Invoke edges; a separate C#-call-graph pass is the correct architectural layer for this concern).
- **Why not A**: emit-time fixes (e.g., emit narrowing overload to call P/Invoke directly) duplicate marshalling code; teaching Step B to follow C# self-calls is a Step B improvement, not an emission fix.

## Step G — `StripOrphanedThrowingClosureFacades`

- **Location**: lines 1050–1131; `FacadeMethodInfo` at 1033–1048; helpers through 1499. Emitter source: `ThrowingClosureSimplificationEmitter.cs:112–115`.
- **What it does**: `ThrowingClosureSimplificationEmitter` emits convenience overloads that take simpler delegate types (`Action<T>` instead of `Func<T, SwiftResult<U, SwiftError>>`), wrap them in a `_wrapped_X` local that handles `SwiftErrorException`, and self-call the base method by C# name. Because the self-call is by C# name (not P/Invoke), it sits one hop outside Step B's transitive closure. Step G performs a two-phase analysis: classifies each block-bodied method as facade (has `_wrapped_*` locals + same-name self-call) or potential base overload (signature contains `SwiftResult<`); groups by `(ContainingType, MemberName)`; checks whether all valid base candidates have been stripped (matching arity + positional binding of `_wrapped_*` arguments); strips facades whose bases are all gone.
- **Before/after**:
  ```csharp
  // Before:
  public void Upload(string url, Func<int, SwiftResult<bool, SwiftError>> completion) { /* P/Invoke */ }   // stripped
  public void Upload(string url, Action<int> completion)                                                   // facade; dangles
  {
      Func<int, SwiftResult<bool, SwiftError>> _wrapped_completion = (arg0) => { try { completion(arg0); return SwiftResult.FromSuccess(true); } catch (...) { ... } };
      Upload(url, _wrapped_completion);   // → CS1503 when base is gone → Step G strips
  }
  ```
- **Classification**: **B** (Plan adjudication: same structural reason as Step F — facade-to-base is a C#-level call edge outside the P/Invoke graph; Step G is the right layer).
- **Why not A**: as with Step F, the architectural fix would be to teach Step B to follow C# self-calls — a Step B improvement, not an emission-time concern.

---

# Subsystem 3 — `ProcessSuppressedProxyReferencesInDirectory`

**File**: `src/Swift.Bindings/src/Configuration/CSharpWrapperCoGater.cs:2224` (defined alongside the rest of `CSharpWrapperCoGater`).
**Public entry point**: `ProcessSuppressedProxyReferencesInDirectory(directory, suppressedProxyClassNames, logger)`, invoked from `Program.cs:523` and gated on `emissionContext.SuppressedProxyClassNames.Count > 0`. Runs **before** Swift wrapper compilation (and therefore long before Steps D–G's `ProcessDirectory`). Suppressed-proxy set is populated by `ProtocolHandler.cs:458` via `RecordSuppressedProxy(...)` whenever an EveryProtocol conformance is skipped (class-bound protocol, generic constraint conflict, static method type conflict, etc.).

The architectural pivot: this pass exists because protocol proxy classes (e.g. `FooProxy`) are sometimes suppressed at emission time, but other emitted code already references those proxies (`new FooProxy(__v)`, vtable callbacks, factory lambdas). The emitter doesn't unwind those references in the same emission pass.

## Pre-pass — `DowngradeSuppressedWrapFallbacks`

- **Location**: lines 2198–2218. Regex `s_wrapFallbackPattern`.
- **What it does**: The emitter generates `ExistentialContainerFactory.GetOrCreate<IFoo>(value, static __v => new FooProxy(__v))` wherever an existential parameter may need a proxy wrapping fallback. If `FooProxy` is suppressed, the lambda must be removed but the surrounding `GetOrCreate` call (which has a no-proxy code path) must be preserved. Regex strips only `, static __<ident> => new <…Proxy>(<ident>)`, leaving `GetOrCreate<IFoo>(value)` intact.
- **Classification**: **A**. Proxy suppression is decided at emission time (`ProtocolHandler:458` records the suppression); the emitter has full knowledge of which proxies will be suppressed before it finishes. Emit `GetOrCreate<IFoo>(value)` directly when the would-be proxy is suppressed.
- **Upstream fix location**: existential-fallback emission path (likely in `ExistentialContainerFactory` call-site emitter / `ProjectionVisitor` for existentials). Consult `emissionContext.SuppressedProxyClassNames` before emitting the lambda argument.

## Transformation 1 — Strip non-public methods constructing suppressed proxies

- **Location**: lines 2074–2127.
- **What it does**: Any block-bodied method/constructor/accessor that contains `new FooProxy(` or `new SwiftInterop.FooProxy(`, is neither `public` nor a property helper (`_Get`/`_Set` suffix), gets fully stripped (declaration + preamble + block).
- **Classification**: **A**. Same root cause as the pre-pass: emitter has the suppressed-proxy set and could refuse to emit the construction site.
- **Upstream fix location**: every emitter path that produces `new <…>Proxy(…)` constructions. Audit via grep for `new ` + `Proxy` in emitter outputs; gate against `SuppressedProxyClassNames`.

## Transformation 2 — Replace `[UnmanagedCallersOnly]` callback bodies with no-op stubs

- **Location**: lines 2016–2038.
- **What it does**: Vtable receiver callbacks are `static [UnmanagedCallersOnly]` methods whose addresses are stored in vtable structs — they cannot be deleted (the vtable layout would change). Their body is replaced with `// Protocol proxy unavailable — no-op callback` (and `return default;` for non-void returns).
- **Classification**: **A**. Emitter could emit the no-op directly when the proxy is suppressed.
- **Upstream fix location**: vtable callback emitter (search for `[UnmanagedCallersOnly]` emit sites in protocol-bridging emitters). Gate on `SuppressedProxyClassNames` to emit the no-op form directly.

## Transformation 3 — Replace interface-implementation bodies with `throw NotSupportedException`

- **Location**: lines 2041–2073.
- **What it does**: If a method/property that constructs a suppressed proxy is an interface implementation (its name appears in `typeProtectedMembers` for the containing type), stripping it would cause CS0535. Body is replaced with `throw new NotSupportedException("Protocol proxy not available: EveryProtocol conformance was not emitted.");` — for properties, get/set accessors get the throw individually.
- **Classification**: **A**. Same emission-time fix: emitter knows both the suppressed-proxy set AND the interface-membership of the method (it's emitting the `: IFoo` declaration).
- **Upstream fix location**: same as Transformation 1; add a special case for interface members → emit the throw form.

## Transformation 4 — Replace public method / property-helper bodies with throw

- **Location**: lines 2085–2121.
- **What it does**: Public methods constructing a suppressed proxy are NOT stripped (would cascade-strip property declarations + reduce API surface). Their bodies are replaced with throw. Property helpers (`_Get`/`_Set` suffix) get the same treatment to prevent cascade-stripping the public property forwarder. Events are fully stripped (bare throw inside event accessor is invalid C#).
- **Classification**: **A**. Same emission-time fix.
- **Upstream fix location**: same as Transformations 1–3.

## Transformation 5 — `StripOrphanedNarrowingOverloads`

- **Location**: line 2140 (calls the same helper as Step F).
- **What it does**: After bodies are replaced or methods stripped, narrowing forwarders whose targets were removed dangle. This call cleans them up with the same logic as Step F.
- **Classification**: **B** (same rationale as Step F). **Annotation**: this is a shared-helper invocation, not a unique inventory surface — the underlying handler is Step F. Listed here for completeness of the proxy-suppression call sequence; do not double-count when surfacing fix candidates.

---

# Subsystem 4 — `SimulatorOnlyMemberDetector`

**File**: `src/Swift.Bindings/src/Configuration/SimulatorOnlyMemberDetector.cs` (488 lines)
**Public entry points**: `Detect` (line 157), `ApplySimulatorGuards` (line 285), `FilterThunkAssembly` (line 400). Invoked from `SwiftWrapperCompiler.cs:246, 255, 396`.

The subsystem reconciles Apple's reality (some xcframework slices expose simulator-only members gated by `#if targetEnvironment(simulator)`) with our pipeline (one set of generated wrappers for both slices). It detects simulator-only members by diffing the simulator and device ABI JSONs, then guards the corresponding `@_cdecl` blocks and filters the corresponding native thunk assembly.

## Rule A — `ExtractMembers` (ABI JSON walker)

- **Location**: lines 203–220 with helper `WalkNode`.
- **What it does**: Walks `*.abi.json` collecting every `Var` / `Function` / `Constructor` node descended from a `TypeDecl`, returning `Dictionary<mangledName, (qualifiedName, patchedMangledName)>`.
- **Classification**: **B**. Reads Apple's ABI JSON format; no other machine-readable source exists.

## Rule B — Constructor `c` → `C` mangled-name patch

- **Location**: lines 244–247.
- **What it does**: When walking a `Constructor` node whose `mangledName` ends with lowercase `c` (designated constructor), patches it to uppercase `C` (allocating constructor) before storing. Comment: *"the generator patches it to uppercase 'C' (allocating) before computing hashes."*
- **Classification**: **A — non-priority**. Technically an emission-convention mismatch the generator chose; the rule exists only to mirror the choice. But this is a one-line patch with no fragility surface — the realistic deliverable is "document and leave," not "delete the handler." Listed as A for rubric consistency; do not surface in Top Fix Candidates.

## Rule C — Property (`Var`) hash suppression

- **Location**: lines 249–254.
- **What it does**: Sets `patchedMangledName = ""` for `Var` nodes. Comment: *"property @_cdecl wrappers use name-based naming (`SBW_Get_/SBW_Set_`) without including the Var's mangledName hash. Clear the hash for Var entries so MatchesCdeclBlock uses name-only fallback matching."*
- **Classification**: **A**. Direct consequence of the generator's emission convention (no hash in property wrapper names). If the generator embedded property mangled-name hashes in `SBW_Get_<…>`/`SBW_Set_<…>`, this suppression rule and most of Rule G2 disappear.
- **Upstream fix location**: property wrapper-name emitter (search for `SBW_Get_` / `SBW_Set_` formatting). Embed the property's mangled-name hash in the wrapper name. **Caveat**: assumes wrapper-name changes are local to the generator + thunk-matching path. If `SBW_Get_<…>` names are referenced by tests, downstream packages, or P/Invoke entry-point declarations elsewhere in the emitted C#, the change ripples and the "< 1 session" estimate underestimates. Verify locality before committing to the fix.

## Rule D — `ApplySimulatorGuards` wrapper-comment regex + `#if` insertion

- **Location**: `WrapperCommentRegex` at 272–274; `ApplySimulatorGuards` at 285–367.
- **What it does**: Regex matches `// (Property [gs]etter|Method|Constructor|Enum case factory) @_cdecl wrapper for <path>.` to identify each `@_cdecl` block's owning member. For matches that resolve to a simulator-only member (and where the cdecl line's hash matches), wraps the block in `#if targetEnvironment(simulator)` / `#endif`.
- **Classification**: **C**. The work (emitting `#if` guards) is essential — Apple's `#if targetEnvironment(simulator)` gating IS reality. The form (post-process regex over emitter-emitted comments) is one of two valid architectures.
- **Question to resolve**: Is the generator architecturally constrained to a single-ABI-JSON input (e.g., by build-pipeline contract, downstream consumers, caching), or could it accept both simulator and device ABI JSONs as a routine change? If single-input is fixed, this is **B** (post-process is the only place we have both views). If dual-input is feasible, it's **A** (emit `#if` guards inline at C# / Swift wrapper emission time, eliminating the regex post-pass entirely).

## Rule E — `ResolveQualifiedName` — module-prefix stripping

- **Location**: lines 374–388.
- **What it does**: Wrapper comments include the module-qualified path (`StripeIdentity.IdentityVerificationSheet.simulatorDocumentCameraImages`); the simulator-only set stores members without the module prefix (ABI JSON walks from `ABIRoot.children` — no module-level wrapping node). Strips the `<moduleName>.` prefix if exact match fails.
- **Classification**: **A**. Emitter includes the module name in wrapper comments; the resolution step exists to undo that. Either strip the module prefix at emission (in the comment) or include the module-level node in the simulator-only set's keys.
- **Upstream fix location**: wrapper-comment emitter. Drop the module prefix from `// Method @_cdecl wrapper for …` comments.

## Rule F — `MatchesCdeclBlock` hash-then-name fallback

- **Location**: lines 42–62 on `SimulatorOnlyResult`.
- **What it does**: Given a qualified name and an `@_cdecl(...)` line, iterates entries with matching `QualifiedName`. For function entries (with hash), confirms the cdecl line contains the hash (overload-precise). For property entries (no hash — Rule C), name match alone is sufficient.
- **Classification**: **A — conditional**. The hash-fallback exists because the wrapper comment doesn't carry overload-disambiguating info (full mangled name, parameter labels). Reframed strictly: if we choose to move disambiguation into emitted metadata/comments, the matcher trivializes and this is A. If we accept the current comment shape as a stable contract, this is B (necessary normalization over emitted form). The reviewer flagged this as a softer A than the surrounding entries — keeping A but flagging it as conditional on the emission-convention change.
- **Upstream fix location**: wrapper-comment emitter. Either include the mangled name in the comment, or include parameter signature.

## Rule G1 — `MatchesThunkBlock` hash matching

- **Location**: lines 73–78 on `SimulatorOnlyResult`.
- **What it does**: For thunk blocks (`.globl _thunk_<Module>_<hash>` … `ret`), confirms the block text contains the hash for entries with hash.
- **Classification**: **A**. Same emission-convention reason as Rule F.

## Rule G2 — `MatchesThunkBlock` token-aware fallback (Swift mangling decoder)

- **Location**: lines 80–137 on `SimulatorOnlyResult`.
- **What it does**: For property thunks (no hash, Rule C), splits the qualified name on `.` and emits length-prefixed parts (`8Identity`, `4Card`, `2id`); requires all parts to appear in the block text. For Swift substitution-compressed forms (`14StripeIdentity0B17VerificationSheet`), tries length-prefixed suffixes of decreasing length and requires the preceding char to be an uppercase substitution-index terminator.
- **Classification**: **A — dependent collapse**. This rule exists *only* because of Rule C's hash suppression (also classified A). Fixing Rule C upstream takes G2 with it. The substitution-compression decoding is itself Swift-mangling reality (immutable), but it's only invoked because we're forced into name-based matching by Rule C. **Counting note**: G2 is not an independent fix opportunity from Rule C — they collapse together.
- **Upstream fix location**: same as Rule C — once property wrapper names embed mangled-name hashes, G2 is replaced by G1's simple hash match.

## Rule H — `FindBlockEnd` brace-depth scanner

- **Location**: lines 471–486.
- **What it does**: Scans Swift function body brace depth to find the block end line.
- **Classification**: **B**. Pure utility; brace delimiters are Swift syntax.

## Rule I — Tail-call vs. multi-instruction thunk parser

- **Location**: lines 418–443 inside `FilterThunkAssembly`.
- **What it does**: A thunk block ends at either `ret` (multi-instruction form) or the next `.globl ` (tail-call form using `b <symbol>`). Parser detects both forms.
- **Classification**: **B**. Both thunk forms are emitted by Swift/LLVM; not changeable from our side.

---

# Top fix candidates (ranked by frequency / consumer impact)

Per gameplan Open Question #3: target the top ~3 by volume or any class whose fix cost is < 1 session. Rankings below are by **observed frequency** (where evidenced) and **breadth of handlers eliminated** (where a single emission-time fix collapses multiple A-class handlers).

### #1 — Stop emitting members whose signatures reference module-internal types

**Eliminates**: Pattern 1 entirely; Pattern 3 (header + body internal-type cases); Pattern 3c entirely; portions of Patterns 2, 4 (internal-type body cases); the `ReferencesInternalType` filter machinery becomes dead code at the post-process layer.

**Evidence of frequency**: GRDB sheds 17/18 EveryProtocol conformances to Pattern 1 (`phase0-report-staleness.md`); 36 `EveryProtocolConformanceSkipped` proxy entries appear in GRDB's `binding-report.json`. The XMLCoder.SharedBox case in Pattern 3 evidences cross-library reach. Likely the highest-volume A-class fix in the inventory.

**Upstream fix location**: type-resolution gate at member-emission time — when resolving a member's signature, refuse to emit if any resolved type is in `internalTypeNames`. Adjacent to (but distinct from) M4's `TypeResolver` central seam; in M3 it would be a focused gate at the point of decision.

### #2 — Stop emitting code that references suppressed protocol proxies

**Eliminates**: All of `ProcessSuppressedProxyReferencesInDirectory` (DowngradeSuppressedWrapFallbacks pre-pass + Transformations 1–4) — pure A-class. Transformation 5 (StripOrphanedNarrowingOverloads) is shared with Step F and stays.

**Evidence of frequency**: Triggered every time `ProtocolHandler` records a suppressed proxy (class-bound protocols, generic constraint conflicts, static-method type conflicts). Multiple validation libraries hit this — `phase0-report-staleness.md` notes CryptoKit registers 12 `EveryProtocolConformanceSkipped` proxies in its report.

**Upstream fix location**: emission-time gate in every emit path that produces `new <…>Proxy(…)`, vtable callbacks, or `ExistentialContainerFactory.GetOrCreate(... static __v => new …Proxy(__v))`. Consult `emissionContext.SuppressedProxyClassNames` before emitting; emit no-op / throw / unwrapped form directly.

The implementation challenge: proxy suppression is decided during the same emission pass that produces the proxy-using code, so order-of-emission may need a defer-queue or two-pass strategy. That's a real refactor but the surface area is well-bounded.

### #3 — Embed property mangled-name hashes in wrapper names (or the wrapper comment) *(low-cost / breadth pick — not volume-ranked)*

Per gameplan Open Question #3: "top ~3 by volume **or any class whose fix cost is < 1 session**" — this candidate is selected on the second criterion, not observed volume. Candidates #1 and #2 above are volume-ranked with concrete frequency evidence (GRDB 17/18 conformances; CryptoKit 12 proxies). #3 is ranked on **breadth of handlers eliminated per dollar of fix cost**, not on a counted frequency that the inventory has the data to surface.

**Eliminates**: Rule C entirely; Rule F's hash-fallback collapses to single-rule hash matching; Rule G2 collapses to Rule G1; the entire Swift-mangling-substitution-decoder is gone. Note: Rules C and G2 are dependent — they're a single fix opportunity, not two.

**Evidence of breadth**: every library with simulator-only properties exercises the property-thunk path; substitution-compression suffix-matching exists only to compensate for the missing hash. The complexity savings are large; the per-library *frequency* (number of property thunks per library) is not measured here.

**Upstream fix location**: property wrapper-name emitter (the `SBW_Get_<…>` / `SBW_Set_<…>` formatter). Embed the property's mangled-name hash in the name itself. **Caveat**: assumes wrapper-name changes are local to generator + thunk-matching. If `SBW_Get_<…>` names are referenced by tests, downstream packages, or P/Invoke entry-point declarations elsewhere in the emitted C#, the fix ripples and the "< 1 session" estimate underestimates. Verify locality before committing.

### Bench (ranked, but not in top 3)

- **Pattern 5 — module/type collision rewrite**. A. Narrow scope (Reachability, SwiftyBeaver). Low frequency but high impact when it fires; emission-time fix is straightforward and likely < 1 session.
- **Pattern 2(a) / 2(g) safety nets**. A. Already gated upstream — these are explicitly safety nets. Worth verifying the upstream gates (`CanConvertToCdecl` for 2(g); `EveryProtocolEmitter` body-stub gating for 2(a)) are complete, then deleting the post-process safety net per the precedent set by 2(b)–2(f).
- **Step E — `StripDanglingToString`**. A. Low individual frequency but a clean fix: emit `ToString()` to call the same P/Invoke as `Description` (so it falls inside Step B's transitive closure naturally).

### C-class items requiring investigation before classification

- **`ReferencesSwiftUnavailableType` (NSInvocation)** — does the ABI JSON reliably expose the `NS_SWIFT_UNAVAILABLE` annotation for transitively-reached ObjC types? Currently a single-element set, so resolution either way is low-cost.
- **`ApplySimulatorGuards` (Rule D)** — is the generator architecturally constrained to a single-ABI-JSON input? If dual-input is feasible, emit `#if targetEnvironment(simulator)` directly and the entire wrapper-comment regex post-pass goes away (which would also eliminate Rule E and parts of F).

---

# Counts

**Counting basis**: one entry = one named handler / pattern / step / rule / transformation in the document. Sub-patterns inside Pattern 2 (2(a), 2(g)) are folded into Pattern 2's single entry; cross-cutting filters (`ReferencesInternalType`, `ReferencesSwiftUnavailableType`) are separate entries; `Transformation 5` is listed but flagged as a shared-helper invocation rather than a unique surface; `Rule G2` is listed but flagged as a dependent collapse of Rule C.

- **SwiftWrapperPostProcessor**: 9 entries (Patterns 1, 2, 2b, 3, 3c, 4, 5 + 2 cross-cutting filters)
- **CSharpWrapperCoGater Steps D–G**: 4 entries
- **ProcessSuppressedProxyReferencesInDirectory**: 6 entries (pre-pass + Transformations 1–5)
- **SimulatorOnlyMemberDetector**: 10 entries (Rules A, B, C, D, E, F, G1, G2, H, I)
- **Total entries inventoried**: 29

By classification:
- **A — fixable at emission time**: 20 (of which 1 is "non-priority" — Rule B; 1 is "dependent collapse" of another A — Rule G2; 1 is "conditional" — Rule F)
- **B — essential normalization**: 7 (Step D, Step F, Step G, Transformation 5 [shared with Step F], Rule A, Rule H, Rule I)
- **C — needs investigation**: 2 (`ReferencesSwiftUnavailableType`; `ApplySimulatorGuards`)

**Independent A-class fix opportunities** (after collapsing G2 into C, and treating Rule B as non-priority): ~17–18. Ratio still favours A heavily (~60–69%), consistent with the gameplan's M3 thesis that "improve emitted API surface" yields meaningful post-processor reduction without sacrificing correctness.
