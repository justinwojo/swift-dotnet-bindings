# Release execution plan — architecture-review wrap-up

Ordered, near-term work toward the release that bundles the 2026-06 architecture review +
sessions work (~52 of 67 findings shipped). This is a **prioritized execution list**, not a
backlog: every item here is judged worth tackling now. The deliberately-latent tail (no
reaching shape / post-1.0 / speculative) is kept out on purpose — see **Left latent** below.

Re-confirm every `file:line` against today's `main` before acting (line numbers drift).

---

## P1 — pull into this release

### 1. Internal-receiver handling fully moved to emission — DONE (post-processor stays, by design)
**Shipped.** All internal-receiver shapes — a PUBLIC member on a `@usableFromInline internal`
parent — are now decided at **emission**, not by post-emission text stripping:
- **sync** Method/Constructor/Property/Subscript: `WrapperValidation` arm 2b rejects only the
  wrapper and binds a direct `CallConvSwift` P/Invoke to the exported silgen/`Tj` symbol (member
  KEPT);
- **async / closure-bearing** methods: dropped at emission via
  `MemberValidationPipeline.ValidateMethodEmission` gate 3c → `SkipReason.ParentModuleInternalNoFallback`.
  Gate 3c scans the **whole** `CSSignature` (return at index 0 + parameters), so a closure in the
  RETURN is dropped too — not only a closure parameter. A closure-returning sync member would
  otherwise slip past a parameter-only scan into the arm-2b keep-via-direct-CallConvSwift path, and a
  closure returned through a direct CallConvSwift P/Invoke crashes Mono+NativeAOT
  (`WrapperValidation.IsReturnTypeCdeclRequired`);
- **operators**: dropped at emission via `OperatorHandler.EmitOperator`'s parent-internal guard
  (same `SkipReason`). The guard drops **every** operator on an internal parent, by design: a
  frozen-struct operator needs a parent-naming `@_cdecl` wrapper with no fallback, and a class /
  non-frozen-struct operator is unreachable dead surface (the internal parent is unconstructible from
  C# and a static operator cannot satisfy a protocol requirement the way an arm-2b sync member can).
  It is **not** narrowed to "only when a wrapper is emitted" — narrowing would emit an uncallable
  operator.

The three legs **DROP** rather than fall back to `CallConvSwift` — and that is correct, not a
shortfall. There is no clean direct-`CallConvSwift` fallback for them (async always needs a
parent-naming bridge wrapper; a closure degrades to a faulting legacy `CallConvSwift` path; a
static frozen-struct operator emitted as a direct `CallConvSwift` P/Invoke crashes ILC on
NativeAOT — see `OperatorHandler.ShouldEmitOperatorWrapper`). The emission-time drop is
public-API-identical to the old emit-then-strip + C# reconcile, but is now decided in one place
alongside the sync arm-2b decision. Verified RED→GREEN: with the gates disabled the
`wrapper_stripped_count` tripwire rises 0→3 (one per leg) and the operator additionally trips the
artifact-parity gate (its post-strip C# reconcile was incomplete — a standalone reason it must be
gated, not stripped); with the gates enabled, strip count returns to 0 and parity passes.

**`SwiftWrapperPostProcessor` stays — and the original "delete the whole subsystem" premise was
wrong.** Internal-receiver rejection was never its only job. It is the generator's general
wrapper-compilation safety net (`SwiftWrapperCompiler` runs it over the emitted wrapper before
`swiftc`), and it strips at least two shapes that have **no** emission-time gate and never will
from this work: `EveryProtocol()` placeholder blocks (conformances whose proxy could not be
emitted) and Swift-**unavailable** ObjC type references (`NSInvocation` et al., distinct from
`@usableFromInline internal`). It is also (a) the shared oracle the BindingTests harness links in
to scrub its own wrapper and drive the getter-parity + `wrapper_stripped_count` gates
(`build/Build.WrapperStrip.cs`), and (b) the source of the `StrippedSymbols` set that
`StrippedSymbolCSharpReconciler` consumes to remove orphaned C# P/Invokes. Its
`ReferencesInternalType` path is now defense-in-depth for body-reference shapes the
signature/parent walk can't predict — the strip-count tripwire turns any such escape into a
compile-gate failure rather than a runtime fault. **Do not re-file "delete `SwiftWrapperPostProcessor`"**
as if closing the internal-receiver legs frees it; it does not.

**F57 not subsumed.** `ReferencesInternalType`'s uncompiled per-call regex still runs (for the
EveryProtocol/Swift-unavailable strips and as the internal-type backstop), so this work does
**not** remove F57's offender. F57 stays post-1.0 (`Future/post-1.0-architecture-roadmap.md`).
The `StrippedSymbolCSharpReconciler` "7b liability — delete with the Swift wrapper strip leg"
note is likewise aspirational: it can only go when the post-processor's *non*-internal-receiver
strips are also subsumed, which is not in scope here.

Detail in `.claude/rules/constraints.md` (internal-receiver wrapper gate) and memory
`feedback_internal_receiver_wrapper_gate`.

### 2. `XCFrameworkResolver` plist integer parse: `int.Parse` → `long.Parse`
**Value:** `XCFrameworkResolver.cs:1005` parses a plist `<integer>` via 32-bit `int.Parse`; a
single 64-bit/odd value throws, the surrounding try/catch discards the **entire** Info.plist,
and version + minOS silently degrade to placeholder `0.0.0` on a real third-party xcframework.
Cheap, reachable, high-signal — the quick win to bundle.
**Scope:** tiny — `long.Parse` (store `long`) + a malformed-plist fixture asserting version and
minOS survive.
**Gate:** `nuke test`.

### 3. `RuntimeContract` strict version gate + restore-time NU1107 test
**Value:** `RuntimeContract.AssertCompatible`
(`src/Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs`) throws on **any** version inequality
as the first unconditional statement of every binding's `[ModuleInitializer]` (outside the
try/catch) → an uncatchable app-wide SIGABRT, contradicting the documented "patch is
ABI-additive / backward-compatible" promise. Cross-minor safety today rests only on an
**unenforced** convention tying `RuntimeContract.Version` to the package minor. A release
shipping fresh Runtime/SDK versions is the right moment to harden it.
**Scope:** small-medium — error only on `generatedAgainstVersion > Version`; add a guard tying
the contract version to the package minor; add the missing end-to-end restore test asserting
NU1107 blocks the cross-minor diamond.
**Gate:** `nuke test` + the restore-time test; `nuke binding-tests --skip-regen` (runtime change).

---

## P2 — fold in if capacity allows

### 4. AF05 — legacy blocking-async-receiver `CancellationToken` edge
Fails **closed** (generation-time proxy compile error), rare, unfixtured. Thread an explicit
`default(CancellationToken)` through the legacy blocking receiver's impl calls + the shared
sibling-fallback helper (`ProtocolProxyEmitter.Receivers.cs:1152,1169`), gated by a
non-blittable-async + sync-namesake fixture. Full completeness map in `.claude/rules/constraints.md`
("KNOWN INCOMPLETE EDGE"). Naturally rides an adjacent async/closure session.

### 5. Batched "fail-safe defaults + guard tests" session
Cheap audit hardenings sharing one shape — each individually tiny; batch to clear them before
release:
- `ITypeDatabase.ApplyEmissionResult` default body is `{ }` → any non-concrete `ITypeDatabase`
  (test doubles, future DBs) silently swallows every nested-type rename + emission stamp, no
  diagnostic (`ITypeDatabase.cs:122`). Make it abstract / throw.
- Build-time guard that every `RegisterConformanceFactory<…>` call site is a concrete literal —
  preserves the Mono-safety invariant (`SwiftMarshal.cs:671`; memory
  `feedback_cache_first_concrete_registration_mono_safe`).
- Per-emitter field-name guard for the finalizer DIM seam (`SwiftMarshal.cs:1003` — current
  `BorrowedMarshalFinalizerTests` use fake types, proving wiring but not per-emitter field-name
  correctness).
- `SimulatorOnlyMemberDetector.FindBlockEnd` brace-counts raw `{`/`}` ignoring Swift string and
  comment literals (`:471`) → a `}` in a default-value string truncates the
  `#if targetEnvironment(simulator)` guard early.

---

## Left latent (a decision, not a dump)
Tracked where each already lives; promote only on a real trigger.

- **S18 decl-factory rewire (step 10)** — test-infra maintainability only; protective value
  already delivered; ≥51 distinct shapes want their own multi-tranche session.
- **AF13 parser-phase write-backs** — `ModuleProcessor.IsFrozen` / `IsObjCRooted` in-place
  mutation; hygiene, owner-gated.
- **ExistentialUnion on the projection path** — speculative/behavioral; needs a real consumer +
  owner ruling. Memory `feedback_existentialunion_engine_inert_on_projection`.
- **F57 generator scalability** — post-1.0; `Future/post-1.0-architecture-roadmap.md`. (Item 1
  did **not** remove its offender: `SwiftWrapperPostProcessor.ReferencesInternalType`'s
  uncompiled regex stays, since the post-processor still serves non-internal-receiver strips.)
- **R1–R6 P2 latents** ("no current emission site") — `regression-audit-followups.md`.
