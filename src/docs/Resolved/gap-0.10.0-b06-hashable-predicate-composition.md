# gap-0.10.0-b06-hashable-predicate-composition

> **Status: RESOLVED (2026-05-10) — closed via safe-stance, shipped in
> `af3a91cb`.**

## Resolution (2026-05-10) — closed via safe-stance, not predicate broadening

The naive predicate flip ("infer Hashable from Equatable") was unsound —
this doc's "Why a naive flip is unsafe" analysis remains correct. The
ship decision was to *commit to the safe stance permanently* rather than
chase the runtime hardening that would have unblocked the broader
predicate:

- Equatable-only types continue to emit the contract-correct `return 0;`
  GetHashCode stub. Equatable still works; types just don't hash uniquely.
- Only types with explicit (or transitively-implied) `Hashable`
  conformance route through `SwiftHashable.GetHashCode`. Type-database
  conformance tracking already covers the transitive case.
- `SwiftHashable` gained a defense-in-depth identity-hash fallback for
  reference-shaped types whose Hashable witness registration is missed
  at runtime.

Why this is the right close: the original gap's headline (1390-test
crash) was a runtime-correctness wall. Hardening `SwiftHashable.GetHashCode<T>`
to safely structural-hash byte-different-but-semantically-equal values
is a research problem (Swift's synthesized `==` operates on properties
post-bridging conventions; the runtime sees marshalled bytes — they are
fundamentally different views of the same value). The contract-correct
`return 0;` stub preserves Equals semantics and is the standard .NET
guidance for "I have Equals but not a stable hash." Consumers who need a
stable hash can implement `GetHashCode` themselves or wrap the value in
a Hashable-conforming Swift type upstream.

Coverage: `TypeHandlerHelpersTests` exercises the explicit-Hashable
predicate; `SwiftHashable.GetHashCode` reference-shape fallback is in
`Swift.Runtime/src/Swift/SwiftHashable.cs`.

Re-open trigger: only if a concrete consumer scenario shows the
`return 0;` stub blocking real usage in a way the user-supplied
override doesn't address.

## Summary

Bundle 06 #1a (Equatable Defect 1 — GetHashCode stub returns 0) is open
work. The current safe behavior emits `return 0;` for any type that
conforms to Equatable but not Hashable; that stub is preserved until
the runtime hardening lands.

## Why a naive flip is unsafe

The naive predicate change — extending `_implementsHashable` in
`TypeHandlerHelpers.cs`, `ClassHandler.cs`, and `EnumHandler.cs` to also fire on
`Swift.Equatable` so every Equatable type routes through
`SwiftHashable.GetHashCode(this)` — looks one-line but is unsound:

1. **Equatable does not actually imply Hashable.** A custom `==`
   (e.g., tolerance-based or normalising) compares byte-different values as
   equal. The runtime helper's structural-hash fallback (`ComputeStructuralHash`
   over the marshalled Swift bytes) hashes those same byte-different values to
   different codes — violating the .NET Equals/GetHashCode contract.

2. **Empirically unsafe at runtime.** Verified by running BindingTests after
   the predicate change: SwiftUI / bridge types crashed 7 methods in
   `BridgeStateUpdateTests` and propagated into a 1390-test regression
   (sim baseline 1860 → 470 with crashed-class harness backoff). The
   `SwiftHashable.GetHashCode<T>` helper's
   `stackalloc + SwiftMarshal.MarshalToSwift(value, ref span)` path is not
   safe for every `T : ISwiftObject` — at minimum some class-projected and
   SwiftUI-wrapper types die in there.

The runtime helper's XML doc invites the routing ("emit this call for any type
whose Swift declaration conforms to Equatable, because Swift synthesizes
Hashable for any Equatable value type whose stored properties are all
Hashable"), but the runtime path that backs that promise needs hardening
before the generator can lean on it.

## Proposed design (when re-attempted)

Route `_implementsHashable` through B05's
`EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(decl, db, …)`
predicate — same shape as `IsEquatableUnconditional`, parameterised on
`Swift.Hashable` instead of `Swift.Equatable`. Sketch:

```csharp
// New helper to add alongside IsEquatableUnconditional:
public static bool IsTypeHashableUnconditional(TypeDecl decl, ITypeDatabase? db) =>
    IsConformanceUnconditionalForCSharp(decl, db,
        SwiftHashableModuleQualifiedName); // "Swift.Hashable"

// In each EqualityMethodsWriter.ctor:
_implementsHashable = directlyDeclaredHashable
    || optionSetOrRawRepresentable
    || (db is not null && EquatableConformanceHelper.IsTypeHashableUnconditional(decl, db));
```

This catches the legitimately transitive cases (e.g., a struct whose every
stored property is Hashable, where Swift synthesises Hashable but the symbol
graph reports only Equatable) while leaving custom-`==` Equatable-only types
emitting the stub.

## Required runtime hardening (separate prerequisite)

Before flipping the generator predicate, `SwiftHashable.GetHashCode<T>` needs:

1. A class-projected-T detection that skips the stackalloc+MarshalToSwift path
   and either (a) hashes the SwiftSafeHandle's underlying pointer for
   reference-equality semantics, or (b) bails to `RuntimeHelpers.GetHashCode`.
2. A repro test in `Swift.Runtime/tests/RuntimeUnitTests/` covering the
   SwiftUI / bridge type shape that crashed BridgeStateUpdateTests during the
   Bundle 06 #1a investigation.
3. The structural-fallback FNV-1a path stays as-is for trivially-marshallable
   value types — that part already works and is contract-correct.

## Coverage when re-landed

Add a new fact to `TypeHandlerHelpersTests` that asserts the post-fix
behavior — for an Equatable conformer where the type database resolves the
Hashable conformance, the generator emits `SwiftHashable.GetHashCode(this)`.
Keep the current `WriteSwiftEquatable_EquatableOnly_EmitsZeroHashStubAwaitingHashableConformance`
test as the negative-case anchor (Equatable-only without resolvable Hashable
stays a stub).

End-to-end: a BindingTests fixture for `struct WithCustomEquatable: Equatable`
that asserts `(a == b) → a.GetHashCode() == b.GetHashCode()` round-trips
through the Swift PWT with no runtime crash.

## Tracking

Bundle 06 #1a stays in_progress in the in-flight task list, owner: future
bundle. Cross-reference: `bug-0.10.0-equatable-not-lowered.md` (now in
`Resolved/`) closed the structural-projection half; this doc tracks the
hash-routing half.
