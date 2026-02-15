# Binding API — Remaining Future Work

**Created**: February 2026
**Source**: Consolidated from `binding-review.md` (R6, R7) and `binding-api-improvements.md` (N5, N6, cross-cutting)
**Completed work**: See `Completed/binding-api-review-and-improvements.md`

---

## Open Items

### R6 (Partial): ExistentialContainer in Public API

**Priority**: P2 | **Difficulty**: Hard

Enum associated values now use typed interfaces for known protocols, but `ExistentialContainer` still appears in:
- Closure parameters (e.g., `Action<ExistentialContainer1>`)
- Some protocol proxy constructors

Requires mapping existential containers to their corresponding protocol interfaces in these contexts. Gated on `AllProtocolsHaveTypeRecords()` — unregistered protocols (e.g., `Swift.Error`) can't be projected.

### R7 (Partial): AnyType Fallback — Original Type Info

**Priority**: P3 | **Difficulty**: Easy

When a Swift type can't be resolved and falls back to `AnyType`, the original Swift type name is lost. Proposal: emit `[OriginalSwiftType("CoreText.CTFont")]` attribute so consumers know what the Swift API actually expects.

The AnyType reduction pass eliminated 7 occurrences. Remaining AnyType instances are structural and unlikely to be resolved without architecture changes: ArraySlice in protocol interfaces (15), Protocol Self type (6), Any/Any.Type (3), generic type arguments (4), associated type protocols (2), cross-module nested types (1), closure containing ArraySlice (1).

### N5: Async Method Naming Edge Cases

**Priority**: P3 | **Difficulty**: Medium

Edge cases in async naming not covered by WU1:
- **Callback-based methods**: Methods accepting a completion callback could offer a `Task`-based overload (requires generating `TaskCompletionSource` wrappers)
- **Library task types**: Methods returning library-specific task-like types (e.g., Nuke's `ImageTask`) correctly don't get `Async` suffix — no change needed

### N6: Property Collision Logic (Value Suffix)

**Priority**: P3 | **Difficulty**: Easy

When a type has both a nested type and a property of the same name, the generator appends `Value` to the property (e.g., `CacheTypeValue`). In C#, the compiler can disambiguate `response.CacheType` (property) from `ImageResponse.CacheType` (type reference). The `Value` suffix is likely unnecessary but needs verification across all C# contexts (generic type arguments, `typeof()`, `nameof()`).

---

## Cross-Cutting Concerns (Not Started)

These were identified in the original review as future improvements. None block current usage.

### Exception Mapping for Swift `throws`

All Swift errors currently wrap in generic `SwiftRuntimeException`. Target: `SwiftException<TError>` with access to the error enum's case and associated values.

### CancellationToken on Async Methods

Async methods currently have no cancellation support. Target: optional `CancellationToken` parameter on all `Task`-returning methods, wired to Swift's `Task.cancel()`.

### Default Parameters / Overloads

Swift methods with default parameter values emit only the full-parameter version. `DefaultParameterOverloadEmitter.cs` exists but scope is limited to wrapper-backed methods.

### Collection Interfaces — **Done** (2026-02-14)

`SwiftArray<T>` now implements `IReadOnlyList<T>` and `IList<T>` with lazy indexed access. Constructors from `T[]` and `IEnumerable<T>`, implicit conversion from `T[]`, bounds-checked indexer, and `AsProjected<TResult>()` for zero-copy string array returns. See roadmap Session 5.

### Golden Scenarios

The original review defined 3 end-to-end acceptance scenarios (Nuke image loading, Lottie animation, BlinkID scanning) that should compile without interop types. Status: 0/3 — blocked by remaining ExistentialContainer and AnyType gaps.

---

## Quality Scorecard — Remaining Gates

| Metric | Gate | Status |
|--------|------|--------|
| Public `ExistentialContainer*` | 0 | Partial (closures/proxy ctors remain) |
| Golden scenarios compile without interop types | 3/3 | 0/3 |
