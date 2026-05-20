# Session 5 — Parent-only async CSM

**Status: complete.** Landing scope:
- New file `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` hosts the parent-only async predicate (`IsCsmAsyncEligibleForGenericParent`) and per-pairing emitter (`TryEmitParentOnlyAsyncOverload`).
- `MemberValidationPipeline` Phase 4a routes plain async methods on generic parents through the new path.
- `EmitConcreteSpecializationsForGenericParent` dispatches the async-with-zero-method-generics shape into the new emitter, returning fall-through behaviour for any wider shape.
- Return-type substitution is gated to non-frozen structs only (`IsReturnSafeHandleBacked`); see code commentary for rejected shapes (class, frozen-struct-projected-as-class, complex enum, blittable).
- Value-type parent gate (`parentTypeDecl is ClassDecl ⇒ reject`) keeps the Swift `let __self = …pointee` capture sound.
- `[UnmanagedCallersOnly]` callbacks hardened with outer catch + `GCHandle.IsAllocated` guards so neither exception escape nor a double-callback can crash the process.
- BindingTests fixture `PatParentAsyncMethods.swift` + 5 runtime tests covering success and throwing variants on two conformers (string and int item).

Gates green: unit (564), sim (2166 ↑ from 2161), device (2187 ↑ from 2182), 0 crashes.

## Known-narrow scope (deferred to Session 6+)

These are deliberately narrow gates in the Session 5 emitter, not bugs. Future sessions can lift each independently:

- **Return-type gate**: only non-frozen structs (`ClassWithOpaquePayload` shape) where `NewFromPayload` wraps the same pointer. Class returns, frozen-struct-projected-as-class returns, complex enums, and blittable types each need a separate emit shape (copy-vs-pointer-wrap-vs-class-pointer-read).
- **Parent kind**: structs / enums only; class parents would need ARC-aware Task capture.
- **Method shape**: zero method-own generic params, zero method parameters. Wider shapes still emit on the open-generic surface unchanged.
- **Optional / existential returns**: `IsEmittableParentOnlyAsyncPairing` rejects non-Named substituted returns; standard async harness has dedicated paths for these.
- **Predicate-emitter parity**: predicate returns true on first viable pairing (same pattern as Session 2 sync). When multiple parent tuples exist and some pass per-pairing gate while others don't, the open-generic surface is suppressed method-wide; partial tuples lose access. Matches Session 2's established design trade-off.

---

Co-deferred gap 2 from `00-overview.md`. Extends Session 2's sync CSM relaxation to the async path. Larger than Session 2 because the async CSM machinery hard-rejects generic parents at two emission sites and the async harness needs to be hoisted into the per-conformer `*CsmExtensions` class for correct return-type substitution.

## Goal

Emit specialized async overloads for plain async methods on PAT-constrained generic parents — no method-own generics — where the return type substitutes the parent's generic parameter (e.g. `func response() async throws -> Bag<Item>.Response` on `struct Bag<Item: BagFilterable>`).

After this session, `MusicLibraryRequest<T>.response() async throws -> MusicLibraryResponse<MusicItemType>` becomes emit-eligible (one of the last two pieces needed before Session 6 re-enables the type).

## Why this session

- Last engine-level gap blocking `MusicLibraryRequest<T>` full re-enable.
- Async generic-parent emission is a generally useful capability; future Apple SDK consumers will have async APIs on generic types.
- Sized "medium-risk focused feature" by Codex; "non-trivial extension of CSM-async machinery" by Grok. Best done after Session 2 (sync foundation) and after the operator has internalised the CSM engine code.

## Dependencies

- **Session 2** (parent-only sync CSM) — async builds on the sync engine work. The new "parent-only" specialisation shape introduced in Session 2 must exist; this session extends it to async.

Independent of Sessions 1, 3, 4. Can ship before or after them, but logically after Session 2.

## Async CSM rejection sites (confirmed)

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs:476–487` (`PassesAsyncMethodLevelGuards`):

```csharp
private static bool PassesAsyncMethodLevelGuards(
    MethodDecl method, TypeDecl parentTypeDecl, ITypeDatabase typeDatabase, ILogger? logger = null)
{
    if (!method.IsAsync) return false;
    if (parentTypeDecl.IsGeneric)                              // <-- hard rejection
    {
        logger?.LogDebug("CSM-async: Skipping {Method} — generic parent type.", method.Name);
        return false;
    }
    // …
}
```

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2447` (inside `EmitConcreteSpecializationsForGenericParent`):

```csharp
foreach (var method in typeDecl.Methods)
{
    if (method.IsAsync) continue;                              // <-- second hard rejection
    // …
}
```

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs:620–633` (`IsCsmAsyncEligible`):

```csharp
public static bool IsCsmAsyncEligible(...)
{
    if (!PassesAsyncMethodLevelGuards(method, parentTypeDecl, typeDatabase)) return false;

    var parentParamNames = parentTypeDecl.IsGeneric
        ? new HashSet<string>(parentTypeDecl.GenericParameters.Select(p => p.TypeName))
        : new HashSet<string>();
    var ownParamCount = method.GenericParameters.Count(p => !parentParamNames.Contains(p.TypeName));
    if (ownParamCount == 0) return false;                       // <-- third rejection
}
```

All three must lift, with corresponding predicate alignment and emission-site adjustment.

## Why this is bigger than sync

The async harness today emits a shared `*AsyncHarness` helper class outside the per-conformer extension. For parent-only async methods, the *return type* substitutes through the parent's generic (e.g. `Bag<Item>.Response` → `Bag<MockBook>.Response`). This means:

- The async callback's `Task<TResult>` generic argument is per-conformer.
- The continuation's `CompletionHandler` is per-conformer-typed.
- The shared harness emission site would either produce a `Task<Bag<TItem>.Response>` (open) or unify across conformers — both wrong.

Correct emission: the async harness must hoist **inside** the per-conformer `*CsmExtensions` class so all generic substitutions are closed at emission time.

## Session 5 work breakdown

### Phase 5.1 — Fixture for trace

Swift: `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentAsyncMethods.swift`:

```swift
public protocol AsyncBagItem {
    associatedtype Response
    static func makeResponse() -> Response
}

public struct StringResponse: Sendable { public let s: String }
public struct IntResponse: Sendable { public let n: Int }

public struct MockStringItem: AsyncBagItem {
    public typealias Response = StringResponse
    public static func makeResponse() -> StringResponse { StringResponse(s: "ok") }
}

public struct MockIntItem: AsyncBagItem {
    public typealias Response = IntResponse
    public static func makeResponse() -> IntResponse { IntResponse(n: 42) }
}

public struct Bag<Item: AsyncBagItem>: Sendable where Item.Response: Sendable {
    public init() {}

    // Parent-only async method, no method-own generics, return substitutes parent's associated type
    public func respond() async -> Item.Response {
        return Item.makeResponse()
    }

    // Same shape but throwing
    public func tryRespond() async throws -> Item.Response {
        return Item.makeResponse()
    }
}
```

Regen with `nuke binding-tests --compile-only --permissive`. Confirm `respond()` and `tryRespond()` are absent (suppressed by the three rejections).

### Phase 5.2 — Lift the three rejections

1. **`PassesAsyncMethodLevelGuards`** — remove the `if (parentTypeDecl.IsGeneric) return false;` block, replaced with a predicate that accepts generic parents *if the eligibility check downstream confirms parent-only async emission can produce well-formed output*. The simpler form: drop the rejection and route through `IsCsmAsyncEligibleForParentOnlyGenericParent` (new predicate). The detailed form: a flag `MethodSpecializationKind.ParentOnlyAsync` set by the engine, threaded through the predicate.
2. **`ConcreteProtocolSpecializationEmitter.cs:2447`** — replace `if (method.IsAsync) continue;` with a dispatch to the new async emission path described in Phase 5.3. The current branch is the synchronous one; the async one must be parallel.
3. **`IsCsmAsyncEligible`** — extend the `ownParamCount > 0` check the same way Session 2 extended the sync predicate. Recognise `ownParamCount == 0` AND `parentTypeDecl.IsGeneric` AND `method.IsAsync` as the parent-only-async case.

### Phase 5.3 — Per-conformer async harness emission site

The async harness today: `AsyncHarnessEmitter` (find via grep; likely under `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/`). It emits a single `_AsyncHarness` class with `UnmanagedCallersOnly` callbacks and `TaskCompletionSource<TResult>` plumbing.

The new path: for each closed conformer, emit the async harness *inside* the `*CsmExtensions` class for that conformer. The `TResult` slot is closed to the conformer's substitution (e.g. `StringResponse`); the callback delegate type is closed; the `Task<StringResponse>` return is closed.

Two architectural choices for emission:

- **(a) Per-conformer harness class** — `MockStringItemBagCsmExtensions._RespondAsyncHarness` with `TaskCompletionSource<StringResponse>` baked in. Pro: clean. Con: code bloat × conformer count.
- **(b) Generic harness instantiated per-conformer** — a single `_BagRespondAsyncHarness<TResponse>` reused across conformers, with TResult bound via C# generics. Pro: less code. Con: cross-conformer harness sharing is a new pattern; may interact with `[ModuleInitializer]` and NativeAOT.

**Choose (a)** initially — concrete per-conformer harnesses minimise risk. The async harness is small (~30 lines) and conformer counts are small (MusicKit's 9 is the high-water mark). Revisit (b) only if code bloat becomes measurable.

Concretely: extend `EmitConcreteSpecializationsForGenericParent` to call a new `EmitParentOnlyAsyncOverload(method, conformer, ...)` for each `(method, conformer)` pair where the method is parent-only-async-eligible. That method:
- Emits a per-conformer async harness class as a nested type of `{Conformer}CsmExtensions`.
- Emits the public async wrapper method (`public static async Task<StringResponse> RespondAsync(this Bag<MockStringItem> bag)`).
- Emits the underlying P/Invoke + `@_cdecl` Swift wrapper as a "pseudo-method" of the parent generic (same wrapper symbol naming as Session 4's KeyPath singletons).

### Phase 5.4 — Return-type substitution before async callback type generation

The async harness's callback delegate is C-typed (`UnmanagedCallersOnly`). For a parent-only async method, the return type `Item.Response` must be substituted to `StringResponse` (or whichever) **before** the callback type is generated. Failure mode: callback type emits as `delegate void Cb(IntPtr ctx, Item.Response result)` — invalid C# because `Item` is unbound at that scope.

Fix site: wherever the async harness emits its callback signature, ensure the substitution context includes the closed conformer's associated-type table. Likely already plumbed for the closed-conformer case (no generic parent), so the fix is to *carry the same substitution table* into the parent-only path.

### Phase 5.5 — Throwing variant

The throwing async path (`async throws -> Item.Response`) uses `TaskCompletionSource<StringResponse>` with `SetException`. Same emission path with `TryRespond` instead of `Respond`. Confirm that `ThrowingClosureSimplificationEmitter` (constraint #40 — `IMethodPostProcessor`) doesn't accidentally fire on the synthesised parent-only-async pseudo-method; the simplification is for *closure* signatures, not method signatures, but the gating logic should be verified.

### Phase 5.6 — BindingTests fixture

C#: `BindingTests/RuntimeTestsApp/Generics/PatParentAsyncMethodsTests.cs`. Cover:

- `Bag<MockStringItem>.RespondAsync()` returns `Task<StringResponse>`; awaiting yields `s == "ok"`.
- `Bag<MockIntItem>.RespondAsync()` returns `Task<IntResponse>`; awaiting yields `n == 42`.
- Two-conformer separation: the methods on `Bag<MockStringItem>` and `Bag<MockIntItem>` are distinct C# extension methods on distinct closed types; awaiting both interleaved completes correctly.
- Throwing variant: `TryRespondAsync()` returns `Task<StringResponse>`; success path returns the value. (Throw path requires modifying the fixture to actually throw; defer to a `tryRespondError()` variant if scope allows.)
- Cancellation: pass a `CancellationToken` if the C# extension method accepts one (depends on existing async-CSM convention).
- **NativeAOT — required**: per `feedback_mono_jit_blame.md`, async paths are sensitive on NativeAOT. Run `--device` and `--sim`. Any crash is *our* bug until proven otherwise (per the same memory).

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | New `ConcreteProtocolSpecializationEmitter.Async` unit tests for parent-only path |
| `nuke binding-tests` (sim) | `PatParentAsyncMethodsTests` passes |
| `nuke binding-tests --device` | Same on NativeAOT — async on NativeAOT is the high-risk surface |
| `nuke validate` | Recommended — async CSM change is cross-cutting and any closed-conformer parent-async surface in the validation libs should be detected |

## Exit criteria

- Three rejections lifted, replaced with a parent-only-async predicate.
- Per-conformer async harness emission site exists, emitting inside `*CsmExtensions`.
- Return-type substitution applied before async callback generation.
- Throwing variant covered.
- BindingTests fixture covers both happy-path and error-path on sim + device.
- No regression in existing async CSM tests.

## Risks specific to Session 5

- **Risk A (cross-conformer harness leakage)** — if the per-conformer harness emission accidentally shares state across conformers (e.g. a static `TaskCompletionSource` field), the second conformer's completion callback overwrites the first's. **Diagnostic:** the interleaved-await test in the fixture; both completions must resolve independently. Static field uniqueness verified by inspecting the generated `.cs` for per-conformer `_AsyncHarness` nested type definitions.
- **Risk B (NativeAOT async callback ABI)** — `UnmanagedCallersOnly` async callbacks have historically had NativeAOT-specific bugs. Per `feedback_mono_jit_blame.md`, ANY async crash is our bug until proven otherwise. **Diagnostic:** if device fails, do NOT skip the test or attribute to upstream. Trace the actual call stack.
- **Risk C (return-type substitution miss on throwing variant)** — `async throws` emits a callback signature that includes both the result and the error continuation. If the substitution misses the throwing variant, the error path emits with an unbound `Item.Response`. **Diagnostic:** the throwing fixture variant must compile *and* run successfully.
- **Risk D (existing async CSM behaviour regression)** — closed-conformer-rooted async methods (no parent generic) already work today. The predicate alignment must not regress them. **Diagnostic:** before changes, run `nuke binding-tests --skip-regen` to confirm baseline; after changes, same plus the new fixture. Baseline pass count must be `>=` previous.
- **Risk E (`ThrowingClosureSimplificationEmitter` over-firing)** — per constraint #40, `IMethodPostProcessor` gates include "skip async". Confirm the simplification doesn't accidentally fire on the synthesised async pseudo-methods.
- **Risk F (Task<T> generic param flow)** — `Task<StringResponse>` requires `StringResponse` to be a fully-projected C# type. If the conformer's nested type isn't in `TypeDatabase`, the emission emits an unresolvable C# type spelling. **Diagnostic:** Phase 5.1's fixture exposes only stdlib-shaped types (`StringResponse` defined in the same module); verify the projection. For consumer libs (Session 6 — MusicLibraryResponse), the same risk applies; budget extra trace time there.

## References

- Session 2 — parent-only sync CSM (prerequisite)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs:476–633`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2447`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncHarnessEmitter.cs` (find at trace time)
- `.claude/rules/constraints.md` lines 18, 36, 40
- `feedback_mono_jit_blame.md` (memory) — async on NativeAOT is our bug, never upstream
