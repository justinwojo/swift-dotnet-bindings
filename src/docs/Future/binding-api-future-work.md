# Binding API — Remaining Future Work

**Created**: February 2026
**Source**: Consolidated from `binding-review.md` (R6) and `binding-api-improvements.md` (N5, cross-cutting)
**Completed work**: See `Completed/binding-api-review-and-improvements.md` and `Completed/binding-api-completed-items.md`

---

## Open Items

### R6 (Partial): ExistentialContainer in Public API

**Priority**: P2 | **Difficulty**: Hard

Enum associated values now use typed interfaces for known protocols, but `ExistentialContainer` still appears in:
- Closure parameters (e.g., `Action<ExistentialContainer1>`)
- Some protocol proxy constructors

Requires mapping existential containers to their corresponding protocol interfaces in these contexts. Gated on `AllProtocolsHaveTypeRecords()` — unregistered protocols (e.g., `Swift.Error`) can't be projected.

### N5: Async Method Naming Edge Cases

**Priority**: P3 | **Difficulty**: Medium

Edge cases in async naming not covered by WU1:
- **Callback-based methods**: Methods accepting a completion callback could offer a `Task`-based overload (requires generating `TaskCompletionSource` wrappers)
- **Library task types**: Methods returning library-specific task-like types (e.g., Nuke's `ImageTask`) correctly don't get `Async` suffix — no change needed

---

## Cross-Cutting Concerns (Not Started)

These were identified in the original review as future improvements. None block current usage.

### Exception Mapping for Swift `throws`

All Swift errors currently wrap in generic `SwiftRuntimeException`. Target: `SwiftException<TError>` with access to the error enum's case and associated values.

### CancellationToken on Async Methods

Async methods currently have no cancellation support. Target: optional `CancellationToken` parameter on all `Task`-returning methods, wired to Swift's `Task.cancel()`.

### Golden Scenarios

The original review defined 3 end-to-end acceptance scenarios (Nuke image loading, Lottie animation, BlinkID scanning) that should compile without interop types. Status: 0/3 — blocked by remaining ExistentialContainer and AnyType gaps.

---

## Quality Scorecard — Remaining Gates

| Metric | Gate | Status |
|--------|------|--------|
| Public `ExistentialContainer*` | 0 | Partial (closures/proxy ctors remain) |
| Golden scenarios compile without interop types | 3/3 | 0/3 |
