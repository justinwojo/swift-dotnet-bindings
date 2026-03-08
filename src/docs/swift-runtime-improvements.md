# Swift.Runtime Improvement Plan

**Date**: February 19, 2026
**Status**: Final — reconciled across Grok 10x Staff+ Engineering Team and Claude review

## Summary

The runtime is in strong production shape. No major rewrite needed. The remaining work is focused, low-risk, and high-value.

## Phase 1 — High-Value, Low-Risk

### ~~Session 1: SwiftSet API Completion~~ DONE (Feb 20, 2026)

Filled the functional gap in `SwiftSet<T>` — previously only exposed `Count` and `Dispose`.

**Delivered API surface:**
- `Contains`, `Add`, `Remove`, `RemoveAll` / `Clear`
- `GetEnumerator` (iterator-based, matching `SwiftDictionary` pattern)
- `ICollection<Element>`, `IReadOnlyCollection<Element>` interfaces
- `FromEnumerable`, `SwiftSet(IEnumerable)` constructor, `ToArray`, `ToList`, `ToString`
- Lazy-cached element metadata (matching `SwiftArray` pattern to avoid `.cctor` crashes)

**Tests**: 17 SwiftSet tests (smoke, dispose, conformance, contains, add, add-duplicate, remove, remove-nonexistent, removeAll, enumeration empty/nonempty, fromEnumerable, toArray, constructor, 1000-element stress, toString).

**Key ABI findings** (documented in detail in memory for future sessions):
- Mutating methods on `Set` pass **full Set type metadata** as generic context (one arg). Non-mutating methods pass element metadata + witness table separately.
- `Set.insert` returns `(Bool, @out Element)` — the `@out` in a mixed return tuple becomes a **regular x0 parameter**, not `SwiftIndirectResult`/x8. This differs from pure `@out` returns (like `Set.remove` → `@out Optional<Element>`) which do use x8.
- Always verify P/Invoke register layout via `swiftc -Onone -emit-sil` and `-emit-assembly` — don't guess from the mangled name.

**Effort**: low-medium | **Impact**: high (functional gap)

### Session 2: Bulk Retain/Release Helpers for Collections

Current per-element `DangerousAddRef`/`DangerousRelease` + P/Invoke pattern is correct for safety but incurs overhead on large collections. Add small Swift-side batch helpers (e.g. `SBW_RetainMany`, `SBW_ReleaseMany`) in the SwiftBindingsRuntime library to cut transition cost.

Applies to `SwiftArray`, `SwiftDictionary`, and `SwiftSet`.

**Effort**: low-medium | **Impact**: high (performance)

### ~~Session 3: SuppressGCTransition on ARC P/Invokes~~ DONE (March 8, 2026)

- `[SuppressGCTransition]` on 5 safe leaf Arc P/Invokes (`swift_retain`, `swift_isDeallocating`, `swift_retainCount`, `swift_unownedRetain`, `swift_unownedRetainCount`)
- Release operations (`swift_release`, `swift_unownedRelease`) intentionally excluded — deinit can trigger managed callbacks via closures/@_cdecl
- 15 new tests verifying attribute presence and absence via reflection

**Effort**: low | **Impact**: medium-high (perf on hot ARC paths)

AOT/trimming annotations deferred — NativeAOT on iOS is not yet a supported configuration. Revisit when upstream support lands.

## Phase 2 — Incremental Polish (Do When Relevant)

### Reactive Structured Logging

Add `[LoggerMessage]` events only when specific debugging pain points appear in real usage. Candidates:

- Witness table lookup failures
- Existential container heap allocation (size > 24 bytes)
- Retain/release diagnostics

Use `DiagnosticSource` or `ActivitySource` so it's zero-cost when not subscribed. Don't plan a logging pass — let real debugging sessions drive what gets instrumented.

**Effort**: medium | **Impact**: medium (debugging value)

### Tuple Source-Generator Fast Path

Only pursue if NativeAOT users report tuple performance issues. The reflection fallback in tuple marshalling is acceptable today — the main hot path (dictionary iteration key-value pairs) already uses the non-reflection `GetTupleTypeMetadataFromElements` + direct offset calculation path.

**Effort**: medium | **Impact**: medium (future-proofing)

## Explicitly Skipped

| Item | Reason |
|------|--------|
| `SwiftSafeHandle` finalizer rewrite | Already correctly implemented — intentional safe-leak + warning on finalization, full VWT Destroy on explicit Dispose |
| `[InlineArray]` existential containers | Already value types and stack-allocable; low runtime benefit |
| `AppDomain.DomainUnload` cleanup | Irrelevant on .NET 10 / iOS — single AppDomain, never unloads |
| Hard cancellation via `swift_task_cancel` | Architecturally mismatched with current callback-based async model |
| Vectorized `SwiftString` copy/validation | Redundant — Swift already validates internally via `SBW_*` wrapper path |
| Striped metadata cache | Premature — `ConcurrentDictionary` reads are lock-free, metadata lookup is cold after startup |
| Async continuation box pooling | `NativeMemory.Alloc` is fast enough; pooling adds complexity without proven need |
