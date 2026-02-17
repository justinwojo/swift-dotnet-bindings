# Binding API — Remaining Future Work

**Created**: February 2026
**Source**: Consolidated from `binding-review.md` (R6) and `binding-api-improvements.md` (N5, cross-cutting)
**Completed work**: See `Completed/binding-api-sessions-a-d.md`, `Completed/binding-api-review-and-improvements.md`, and `Completed/binding-api-completed-items.md`

---

## Session Plan

| Session | Work Item | Status | Depends On |
|---------|-----------|--------|------------|
| ~~A~~ | ~~ExistentialContainer in Public API~~ | **Done** | — |
| ~~B~~ | ~~Exception Mapping for Swift `throws`~~ | **Done** | — |
| ~~C~~ | ~~CancellationToken on Async Methods~~ | **Done** | — |
| ~~D~~ | ~~Async Callback → Task Wrappers~~ | **Done** | — |
| E | Golden Scenario Validation | **Partial** (P2, Medium) | A |
| F | AnyType in Golden Scenarios | **Open** (P2, Medium) | E |

---

## Open Sessions

### Session E: Golden Scenario Validation — PARTIAL

**Priority**: P2 | **Difficulty**: Medium | **Blocked by**: Session A (done)

The original review defined 3 end-to-end acceptance scenarios that should compile without interop types:
1. **Nuke** — `any Swift.Error` → `AnyError` (**Done**). Remaining: `AnyType` in 1 `UnsafePointer<T>` gap; pre-existing `Progress` duplicate (CS0102)
2. **Lottie** — `any Swift.Error` → `AnyError` (**Done**). Remaining: `AnyType` in ~27 `UnsafePointer<T>` refs
3. **BlinkID** — `any Swift.Error` → `AnyError` (**Done**). Remaining: minor `AnyType` refs

**Status**: ExistentialContainer path improved (Session A done). `UnsafePointer<T>` → `AnyType` projection gap is the remaining blocker → Session F.

---

### Session F: AnyType in Golden Scenarios

**Priority**: P2 | **Difficulty**: Medium | **Blocked by**: Session E

`UnsafePointer<T>` currently projects to `AnyType` because there's no concrete C# projection for Swift pointer types. Lottie has ~27 references.

**Work required:**
- Evaluate `UnsafeRawPointer`/`UnsafeMutableRawPointer` runtime types as projections
- Map `UnsafePointer<T>` to typed pointer projections where T is known
- Validate golden scenarios compile without `AnyType` in call paths

---

## Quality Scorecard — Remaining Gates

| Metric | Gate | Status | Unblocked By |
|--------|------|--------|--------------|
| ~~Public `ExistentialContainer*` for `any Error`~~ | ~~0~~ | **Done** (mapped to `AnyError`) | ~~Session A~~ |
| Golden scenarios compile without interop types | 3/3 | Partial (`AnyType` remains for `UnsafePointer<T>`) | Session E + F |
| ~~Typed Swift error exceptions~~ | ~~Yes~~ | **Done** | ~~Session B~~ |
| ~~Async cancellation support~~ | ~~Yes~~ | **Done** | ~~Session C~~ |
