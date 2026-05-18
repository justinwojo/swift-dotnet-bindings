# Session 2 — Parent-only sync CSM (Concrete Specialization Machinery)

Co-deferred gap 1 from `00-overview.md`. Independent of KeyPath but on the critical path for re-enabling `MusicLibraryRequest<T>` (Session 6). Engine-level work that unblocks any future PAT-constrained generic parent with no-method-own-generics methods.

## Goal

Make the CSM (concrete specialization machinery) engine emit specialized overloads for plain instance methods on PAT-constrained generic parents — methods that have **no method-own generic parameters** but whose containing type is generic with a PAT-constrained parameter (e.g. `func filter(text: String) -> Self` on `struct MusicLibraryRequest<T: MusicLibraryRequestable>`).

Today the engine filters these out at `FindSpecializableMethods` with `if (ownParams.Count == 0) continue;` even though the emitter already has a `methodParams.Count == 0` branch wired and waiting.

## Why this session

- Unblocks `MusicLibraryRequest<T>.filter(text:)` (one of the 11 surface members).
- Same engine path is needed by anything matching the shape "PAT-constrained generic parent, plain sync method, no own generics" — likely to appear in future Apple SDK consumers as Apple leans further into generic APIs over `some Protocol` and PATs.
- Low-risk: the doc characterises this as "predicate alignment". The emission side already exists.

## Dependencies

None — independent of property-drop-bug (Session 1) and KeyPath foundation. Can run in parallel with Session 1 in principle.

## Engine + emitter sites (confirmed by codebase survey)

### Filter site that drops the method

`src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:534–544`:

```csharp
var ownParams = method.GenericParameters
    .Where(p => !parentParamNames.Contains(p.TypeName))
    .ToList();

if (ownParams.Count == 0) continue;          // <-- this is the silent drop

var ownParamNames = new HashSet<string>(
    ownParams.Select(p => p.TypeName), StringComparer.Ordinal);
```

### Emitter branch that's wired but unreached

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2502–2509` (inside `EmitConcreteSpecializationsForGenericParent`):

```csharp
if (methodParams.Count == 0)
{
    // No method-generic params: emit one overload per parent tuple.
    TryEmitConcreteOverload(
        csWriter, swiftWriter, method, typeDecl, parentTuple,
        moduleName, wrapperLibPath, typeDatabase, emissionContext,
        emittedSignatures, logger, isExtension: true);
    continue;
}
```

The engine just needs to feed this branch.

### Predicates that need extending

- `IsCsmSyncEligibleForGenericParent` (`ConcreteProtocolSpecializationEmitter.Sync.cs:~34` per Grok survey; reconfirm at trace time) — must recognise parent-only methods as eligible.
- `MemberValidationPipeline` Phase 4a — must route parent-only methods as `RoutedElsewhere` so the default emission path doesn't also try to bind them (causing duplicate-signature errors).

## Session 2 work breakdown

### Phase 2.1 — Reproduce + trace

Fixture (Swift): `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentSyncMethods.swift`:

```swift
public protocol BagFilterable {
    associatedtype Filter
    static var defaultFilter: Filter { get }
}

public struct PlainStringItem: BagFilterable {
    public struct Filter {
        public let pattern: String
        public init(pattern: String) { self.pattern = pattern }
    }
    public static let defaultFilter = Filter(pattern: "")
}

public struct PlainIntItem: BagFilterable {
    public struct Filter {
        public let lowerBound: Int
        public init(lowerBound: Int) { self.lowerBound = lowerBound }
    }
    public static let defaultFilter = Filter(lowerBound: 0)
}

public struct Bag<Item: BagFilterable> {
    public var hint: String = ""

    public init() {}

    // Plain sync method, no method-own generics, parent's PAT param Item is referenced indirectly via Self
    public mutating func attach(hint: String) {
        self.hint = hint
    }

    // Sync method returning Self (no method-own generics)
    public func withHint(_ hint: String) -> Bag<Item> {
        var copy = self
        copy.hint = hint
        return copy
    }
}
```

Regen with `nuke binding-tests --compile-only --permissive` to confirm `attach(hint:)` and `withHint(_:)` are absent (or emit only as the un-specialized generic-parent fallback) before the fix.

### Phase 2.2 — Engine: relax `ownParams.Count > 0`

In `ConcreteSpecializationEngine.cs:534–544`, drop the early `continue` on `ownParams.Count == 0`. Instead, route the method to the same `EmitConcreteSpecializationsForGenericParent` path, but with `ownParams` empty.

Concretely:
- Introduce a new shape `MethodSpecializationKind { OwnGenerics, ParentOnly }` (or similar) — `MethodSpecializationKind.ParentOnly` when `ownParams.Count == 0`.
- `ResolveParentSpecializableParams` (per `00-overview.md` reference doc) must return a non-error result for the parent-only case — parent-tuple resolution is sufficient; no method-own substitution needed.
- Feed the result into `EmitConcreteSpecializationsForGenericParent`. The emitter's existing `methodParams.Count == 0` branch handles emission per parent tuple.

### Phase 2.3 — Eligibility predicate alignment

Extend `IsCsmSyncEligibleForGenericParent` (`ConcreteProtocolSpecializationEmitter.Sync.cs`) to accept methods where `ownParamCount == 0` but the parent has PAT generic params. Add a unit test mirroring the predicate's existing test pattern.

### Phase 2.4 — Pipeline routing

`MemberValidationPipeline` Phase 4a — when a method is parent-only-CSM-eligible, route it as `RoutedElsewhere`. Currently methods with `ownParams.Count == 0` fall through to the default emission path; after the engine change, the default path would attempt to emit them as plain generic-parent methods, producing duplicate-signature errors against the CSM-specialised overloads. Phase 4a is the routing junction (see `CLAUDE.md` reference to MemberValidationPipeline).

Confirm: the routing flip must happen *before* the default emission path runs but *after* the CSM engine has registered the parent-only specialisation. Wrong ordering = either duplicate emission or no emission. The integration test (regen + grep for the method name) catches both failure modes.

### Phase 2.5 — BindingTests fixture

C# side: `BindingTests/RuntimeTestsApp/Generics/PatParentSyncMethodsTests.cs`. Cover:

- `Bag<PlainStringItem>` and `Bag<PlainIntItem>` — both closed conformers compile and run.
- `attach(hint:)` mutating method — call, verify `hint` property changes (assumes Session 1's property fix is in; if not, expose `hint` via an explicit getter method).
- `withHint(_:)` — call, verify return type is the closed `Bag<PlainStringItem>` (not the open `Bag<T>`).
- Negative: an `XCTest`-style assertion in C# that the method is generated as one specialised overload per conformer (not as a single generic stub). Either grep the generated `.cs` for the specialised symbol name, or call the method on both conformers and confirm distinct symbol invocation via P/Invoke trace.

## Validation gates

| Gate | Expected | Notes |
|---|---|---|
| `nuke test` | Baseline; new `ConcreteSpecializationEngineTests` cases for parent-only path | Engine logic change → unit test required |
| `nuke binding-tests` (sim) | New `PatParentSyncMethodsTests` passes | |
| `nuke binding-tests --device` | Same | Method dispatch can differ on NativeAOT; verify |
| `nuke validate` | Cross-cutting engine change — run for at least one tier 1 generic-heavy lib | Recommended; per CLAUDE.md it's opt-in but generator/emitter changes are listed as candidate-for-validate |

## Exit criteria

- `ConcreteSpecializationEngine.cs:534` no longer drops parent-only sync methods.
- `IsCsmSyncEligibleForGenericParent` recognises the new shape.
- `MemberValidationPipeline` Phase 4a routes correctly.
- Fixture passes on sim + device.
- No duplicate-signature regressions in validate output (or if a regression appears, it's investigated and confirmed pre-existing).
- Commit message describes the *why* (parent-only sync CSM unblocks `MusicLibraryRequest<T>.filter(text:)` and similar shapes).

## Risks specific to Session 2

- **Risk: duplicate signature emission.** If routing flips the method as `RoutedElsewhere` *before* CSM machinery registers the specialisation, the default emission path may also emit the method (in the parent-generic class) producing duplicate signatures. Diagnostic: regen + grep for the method name, expect exactly N specialised overloads (one per conformer) and zero open-generic stubs.
- **Risk: overlap with non-CSM closed-conformer emission paths.** Some methods on closed conformers already emit via the `BoundGenericsHandler` path. Parent-only CSM should not duplicate that work. Diagnostic: confirm via trace that `BoundGenericsHandler` either no longer fires for this method or that its emission is suppressed when CSM has already covered it.
- **Risk: `BoundGenericsHandler`'s `ShouldSkipConstraint` interaction.** Per `.claude/rules/constraints.md` line 36, `ShouldSkipConstraint` exists only in `BoundGenericsHandler`. The parent-only CSM path must not invoke it in a way that suppresses the method. Verify by tracing the method through both `BoundGenericsHandler` and the CSM engine post-fix.
- **Risk: `WasEmitted` flag** must flip true on the CSM-specialised emission path, not on the (now-skipped) default path. Per constraint #18, mis-flipping breaks `HasMethodInResolvedAncestors`. Add a debug assert.

## References

- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:534–544` (the gate)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2502–2509` (the wired-but-unreached branch)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs` (eligibility predicate)
- `src/Swift.Bindings/src/Marshaler/MemberValidationPipeline.cs` (Phase 4a routing)
- `.claude/rules/constraints.md` line 18, line 20 (generic protocol extension ABI), line 36
