# Design brief: structural binding resilience — "always emit a clean, compiling, usable binding"

## The goal (product)

A developer takes an arbitrary compiled Swift library (`.xcframework` + ABI JSON) and runs our
generator to get a C# binding for .NET-on-Apple. **We want: they always get a binding that
compiles and is usable, containing everything we CAN bind, with everything we can't clearly
tombstoned and reported — never a whole-binding failure because of one localized problem we
didn't anticipate.**

Non-negotiable correctness constraint: a degraded binding must be **sound** — never emit
something that compiles but is ABI-wrong or crashes at runtime. Dropping a member is fine;
leaving a type in an inconsistent/ABI-corrupt state is not. "Compiles" is necessary but not
sufficient; "compiles AND every emitted member is correct" is the bar.

## Current architecture (verified, not hypothetical)

The generator is Parser → TypeDatabase → Marshaler → Emitter, producing (a) a
`{Module}.Wrapper.swift` @_cdecl shim compiled as ONE unit, and (b) the C# `.cs` files +
csproj. It is already strongly biased toward per-member/per-type skip-and-continue:

- **Pre-emission prediction gates.** `MemberValidationPipeline` (Emit/Skip/Synthesized/
  RoutedElsewhere per method/property/subscript) + `TypeSkipConditions` (whole-type drops).
  Extensive catalog: variadic packs, unsupported closures, generic-type callbacks, inout ABI
  mismatch, bound-generic constraints, @usableFromInline-internal reachability, indeterminate
  struct/PWT layout, etc. Every skip writes a `// Unsupported: <reason>` tombstone + a
  structured `SWIFTBIND0xx` report row. Zero-usable-member types become `[OpaqueSwiftType]`
  shells so references still resolve.
- **Swift wrapper has a recovery pass.** Pre-emission eligibility gate
  (`ValidateMethodWrapperEligibility`, internal-type-reach walker) + POST-emission
  `SwiftWrapperPostProcessor` that strips known-broken wrapper blocks (regex/pattern-based) +
  `StrippedSymbolCSharpReconciler` that suppresses the now-dangling C# P/Invokes. So one
  *modeled* bad wrapper member does not sink the unit.
- **C# side is prediction-only.** No post-emission "delete the bad member" pass. `AbiContractChecker`
  DETECTS ABI violations (CC-001..004) but is **warn-only and its result is discarded** at the
  call site. So an uncompilable/unsound C# member that slips the pre-gates is written → csproj
  build fails downstream.
- **Whole-module abort paths (the residual risk):**
  1. Any uncaught exception → single top-level `catch` in `Program.cs` → whole module fails.
     (Includes malformed `τ_0_0` names reaching a throwing `SwiftTypeName.FromModuleQualifiedName`,
     unhandled decl kinds, etc.)
  2. Fail-closed self-consistency invariants: silent-tombstone-registry ⊄ emitted divergence
     (throws); `WrapperSymbolIntegrityGate` (dangling wrapper symbol → non-zero exit, no binding).
  3. A wrapper `.swift` construct that is uncompilable but UNRECOGNIZED by the reach-walker/strip
     regexes → fails the whole `.swift` slice → dead native side. (This is the "Family A"
     `_SBW_P…`/EveryProtocol-non-conformance + ambiguous-type residue.)
  4. An uncompilable C# member that passes pre-gates → csproj failure. (The "Family B"
     CS0721 static-type / CS0234 Apple-type residue.)

**Key insight:** the residual whole-binding failures all share one root: the emitter **predicts**
what it can't handle but does not **verify** the artifact it produced and recover when the
prediction was wrong. Prediction is fast but forever incomplete (new libraries = new shapes);
the unmodeled tail always fails hard.

## Proposed spine (to critique / improve / replace)

1. **Provenance map** — emitted artifact (wrapper symbol, C# span) → originating Swift decl.
   Enabling infra for precise attribution of a compiler error back to the member to strip.
2. **Verify-then-recover loops**, triggered ONLY on failure (so healthy bindings pay nothing):
   - Wrapper: it's already compiled once. On failure, parse swiftc diagnostics → attribute to
     member(s) via provenance → strip (reuse PostProcessor + Reconciler) → recompile. Loop until
     clean or no-progress.
   - C#: add a Roslyn compile-probe → CS diagnostics → attribute → strip member + P/Invoke +
     wrapper symbol → recompile. Symmetric to the wrapper loop. (Also: stop discarding the
     AbiContractChecker result — act on it.)
3. **Per-member emission transactions** — wrap each member/type's emission so an uncaught
   exception contains to a member-skip (tombstone) instead of a module abort. Generalize the
   existing transactional-rollback used by WrapperSymbolContractGate.
4. **Soundness guard** — every strip must preserve ABI/type consistency of the remainder. Define
   when a skip is safe (a method: always; a stored property affecting struct layout: may corrupt
   ABI → must drop the whole type, not the field). Never compile-but-wrong.
5. **Degradation report as product surface** — classify each drop by OWNER: user-self-serviceable
   (missing sibling dependency → "pass --framework-dependency X"), upstream/toolchain, or
   us (real generator limitation). Ship it with the binding (extends the existing api-surface.md).

## Open design questions (want the models' best thinking)

- **Attribution strategy**: precise provenance map vs. bisection (strip-half-recompile, O(log n)
  compiles) vs. hybrid. What's the right cost/complexity/robustness tradeoff? How do we attribute
  a swiftc/Roslyn error at file:line to a logical member robustly (line maps drift as we strip)?
- **Soundness line**: how do we STATICALLY guarantee that stripping member X doesn't leave the
  remaining type ABI-corrupt or semantically broken (e.g. a protocol conformance the consumer
  relies on, a field that changes struct size)? This is the crux — aggressive skip is only safe
  if we can prove the remainder is still correct. What's the model for "safe to drop"?
- **Prediction vs verification balance**: do we keep growing the pre-emission gate catalog, or
  treat verify-then-recover as the general backstop and freeze/retire hand-coded gates? Hybrid?
- **Latency**: verify-on-failure-only keeps healthy bindings free, but a pathological library
  could pay many recompiles. Cap? Bisection budget? Is a Roslyn in-process probe (vs shelling to
  `dotnet build`) worth the dependency for speed + structured diagnostics?
- **Exception containment granularity**: can we make per-member emission truly transactional
  given shared mutable emitter state (buffers, collectors, TypeDatabase)? What's the isolation
  boundary?
- **Failure honesty**: when we strip a member the consumer needed, how do we make sure that's
  LOUD and actionable, not a silent quality regression? Where's the line between "graceful
  degradation" and "hiding a bug we should fix"?
- **What are we missing** — is there a fundamentally better architecture than predict+verify-recover?
  (e.g. emit-everything-then-tree-shake; a two-phase "probe build" that discovers the maximal
  compiling subset; compiler-as-oracle from the start rather than as a fallback.)

## Success criteria

- No library in a broad real-world corpus produces a whole-binding failure due to an unmodeled
  localized construct; worst case is a compiling binding with tombstoned members + a report.
- Zero soundness regressions (no compile-but-wrong bindings).
- Healthy bindings pay ~zero added cost.
- Every drop is attributable, owner-classified, and surfaced to the user.
