# Engineering decisions and investigated alternatives

Settled choices and costly-to-recover rationale. These records are reference material; deferred alternatives are not implementation tasks. The dated evidence below is retained from the original decisions, not a fresh code audit.

<a id="objc-naming-and-constant-de-prefixing"></a>

## ObjC naming and constant de-prefixing

**Decision (owner, 2026-08-04):** ship the intentional Swift-import type, enum-case, delegate-selector and constant renames as source-breaking changes. No compatibility re-exports, revert or major-version staging. Prefer the coherent long-term C# surface. Migration mappings belong in the 0.19.0 package release notes; the removed docs-pass ledger remains in git history.

Constant de-prefixing is all-or-nothing per module, using the module tag; the mapping is reversible as `old = tag + new`. This decision covers that surface too. Revisit if consumer reports show a greater compatibility cost than the migration tables cover, or before further naming changes. Tag-source stability remains a separate [deferred issue](../Future/notes/objc.md).

<a id="native-width-accessors-and-initializer-overloads"></a>

## Native-width accessors and initializer overloads

**Decision (owner, 2026-09):** checked narrowing plus a lossless native-width companion shipped together. A narrowed `Int`/`UInt` property getter throws `OverflowException` when its value cannot fit and names the `{Name}Native` companion (`nint`/`nuint`, settable where the property is, with sibling-name disambiguation). Method returns retain their native-width projection. The reverse-dispatch setter receiver keeps its plain cast because exceptions cannot escape an `UnmanagedCallersOnly` frame.

OptionSet operators and `Contains` read `RawValueNative`, preserving high bits and full-width unsigned complement. Initializer convenience overloads apply to all three emitted lanes: constructors, `CreateWith…` factories and `TryCreate…` factories. Collision checks compare occupants within the same lane. The separate question of adding a native-width member to a generated protocol interface remains in [protocol notes](../Future/notes/protocols.md).

Evidence: `KeyPathRouteC.lastKeyPathHash` and the native-width/OptionSet and initializer-lane regression coverage recorded with this decision.

<a id="decided-2026-09-08-owner-reroute-untyped-swiftself-members-onto-cdecl-wrappers-rather-than-wait-for-upstream-d"></a>

## DECIDED (2026-09-08, owner) — reroute untyped-`SwiftSelf` members onto `@_cdecl` wrappers rather than wait for upstream — DONE

**Revisit when:** Mono ships a fix (then re-examine which reroutes still earn their keep), or the opening inventory shows the wrapper route cannot express enough of the exposed population to be worth the widening.

<details>
<summary>Recorded evidence and disposition</summary>

Raised 2026-09-08 with the register-level attribution (see [Mono GC-safe-region cookie clobber](../Future/notes/runtime-abi.md#mono-clobbers-the-gc-safe-region-cookie-in-x20-so-any-swiftself-carrying-direct-callconvswift-p-invoke-can-abo)): every direct `CallConvSwift` P/Invoke carrying an untyped `SwiftSelf` is exposed, whether a given member aborts is decided by Mono's register allocator, and that allocation can change with any Mono or codegen update — so a green Mono lane is a snapshot, not a property. **Decision:** route the exposed members through `@_cdecl` wrappers, which removes the register collision by removing the reserved register from the signature, and accept the permanent, ABI-visible widening of the wrapper rate that comes with it. The alternatives are rejected on the record: a generator-side shape gate is unsound (there is no shape — the clobber hits non-throwing members and plain getters alike) and would violate the prediction-gate freeze policy; there is no "Mono lane only" emission mode, since one generated assembly runs on Mono and NativeAOT both; and `[SuppressGCTransition]` removes the enter/exit bracket entirely but is legal only for calls that neither block nor re-enter managed code, which the affected surface does. **What makes the work tractable:** the generator is already wrapper-by-default and the direct route is the fallback taken when an eligibility guard rejects, so this is closing eligibility buckets rather than adding a Mono-shaped predicate. **Status — done (2026-09-10).** Executed as guard-narrowing, not as a new predicate: the per-kind eligibility evaluators that were rejecting these members were opened one class at a time — generic parent contexts through generic static dispatch with metadata as a parameter; member-level generics through a free wrapper that opens each type argument's metadata and refuses a missing conformance as a typed exception; nested, variadic and cross-module receiver shapes; Optional-existential subscript getters; class-receiver resilient-struct returns — and the runtime library's own hand-declared `SwiftSelf` P/Invokes were replaced by C shims. The exposed population fell from 149 direct declarations carrying an untyped `SwiftSelf` to 85; the 9 declarations carrying a typed `SwiftSelf<T>` — never exposed, but on the same route — fell to 0. The opening inventory's contradiction is settled by measurement rather than argument: the population was real and most of it was reroutable, so the decision stands as taken. What still takes the direct route is not a shortfall of the wrapper route but several distinct obstructions, each carried by its own row — generic parents behind fail-closed metadata gates, member-level generics outside the opened subset, closure-typed parameters and setters, actor-isolated stored properties, module-internal parents, and two shapes where the direct route is the sound answer. **Trigger to revisit the decision itself:** Mono ships a fix (then re-examine which reroutes still earn their keep), or the opening inventory shows the wrapper route cannot express enough of the exposed population to be worth the widening.

</details>

<a id="swiftwrapperpostprocessor-retirement-do-not-re-file"></a>

## `SwiftWrapperPostProcessor` retirement (do not re-file)

Internal-receiver rejection moved fully to emission-time gates (WrapperValidation arm 2b sync fallback; `MemberValidationPipeline` gate 3c for the async/closure legs; `OperatorHandler` parent-internal guard), proven red→green with fixtures in `Internal/InternalTypeReach.swift` + `MemberValidationPipelineTests`. The old "this retires the post-processor" premise was WRONG: it remains the general wrapper-compile safety net (strips `EveryProtocol()` placeholder blocks and Swift-unavailable ObjC type refs, neither of which has an emission-time gate), the shared oracle the BindingTests harness links (`build/Build.WrapperStrip.cs`), and the source of the `StrippedSymbols` set `StrippedSymbolCSharpReconciler` consumes; its `ReferencesInternalType` path is defense-in-depth behind the strip tripwire. **Trigger:** none for retirement — revisit only if every stripped shape first gains an emission-time gate.

<a id="async-csm-leases-the-synchronous-entry"></a>

## Async CSM leases the synchronous entry

The async CSM `self` parameter uses `SafeHandle`, so the P/Invoke marshaller leases its storage across the synchronous entry. The Swift wrapper copies `self_.pointee` before launching its `Task`, which captures the local by value and retains its reference fields. A task-long storage lease is unnecessary for that shape.

A future wrapper that reads through `self_` after task launch needs a different lifetime contract. ObjC-rooted and native-remapped parents retain their raw `IntPtr` path because they expose no `Payload`. Evidence: `PatParentAsyncSelfLifetimeTests` covers the by-value copy, a 200-iteration entry race, disposal during suspension and churn; `EmitConcreteSpecializations_ParentOnlyAsyncMethod_SelfArgIsLeasedForTheSynchronousEntry` pins emission.

<a id="typerecord-declaredlayout-is-a-separate-lane-from-inlinesize-deliberately"></a>

## `TypeRecord.DeclaredLayout` is a separate lane from `InlineSize` — deliberately

2026-09 wave (s10). Parse-time `DeclaredLayout` (size/alignment straight from the ABI JSON) feeds `SwiftValueLayout.TryResolveReferenceFieldSize` for nested frozen reference-bearing fields; the pre-existing `InlineSize` lane keeps computing the outer struct's buffer. Unifying them was considered and declined: `InlineSize` is consumed by ~dozens of emitter sites with their own fallbacks, and the two disagree by design for records whose ABI JSON omits layout (`DeclaredLayoutIndeterminate`), where `InlineSize` still has a pointer-width fallback the new lane must not inherit. A stored field whose type is a *simple enum* poisons the outer layout (`HasUnknownCustomAlignment`) on purpose — its raw-value width is not in the ABI JSON — and a test locks that. `indirect case` payload enums are over-rejected for the same reason (their box is a single pointer and could be modelled). **Trigger:** a validation library loses a frozen struct to `IndeterminateBufferLayout` for a simple-enum or `indirect` field.

<a id="fragment-transaction-migration-of-the-whole-attempt-snapshot-restore-not-warranted-by-measurement"></a>

## Fragment-transaction migration of the whole-attempt snapshot/restore — NOT WARRANTED by measurement

**Revisit when:** a corpus/validation profile where the whole-attempt rewind + re-render dominates verify-recover wall-clock — concretely, a per-round render cost that rises to rival or exceed the external swiftc+C# recompile (e.g. a very large module whose in-memory rewind + re-render exceeds ~1s), OR a module needing many (≳5) withdrawal rounds so cumulative re-render cost becomes material against the compiles. Reproduce that module as the timing fixture before migrating any emitter to fragment-local transactions.

<details>
<summary>Recorded evidence and disposition</summary>

Wave 1 landed attempt-restart as a **whole-attempt** snapshot/restore: `InEmissionDriver.RenderCompileAttribute` rewinds `_declBaseline.Restore()` + `_contextBaseline.Restore()` + `_rebuildCollaborators()` + `_outerJournal.RestoreInto(_typeDatabase)` before every render, so each render is a pure function of the denylist. Sol's Stage 7 suggested migrating hot leaf emitters to **fragment-local** transactions so a withdrawal rewinds only the affected fragment instead of re-rendering the whole module. **Measured first (per-round wall-clock, single-round profile 2026-07-20): render (pure-C# emission, snapshot-restore inclusive) ≈248ms; swiftc wrapper compile ≈400ms; C# MSBuild/Roslyn verify ≈862ms warm / ≈1566ms cold.** The rewind is **already folded into the 248ms** — both snapshots are in-memory O(module-size) collection/scalar restores that run on *every* render (round 0's is a no-op against the pristine baseline), so the restore is a strict, small subset of the 248ms, not an addition to it. Restarts fire **only on a withdrawal round**: the common path (converge round 0, zero withdrawals) pays zero restart cost, and the 2026-07-19 120-lib sweep withdrew on only 6/120 libs at 1–2 rounds each. **Verdict:** the marginal cost of an attempt-restart is one extra `RenderCompileAttribute` = a ≤248ms re-render **plus** the *unavoidable* re-run of swiftc (~400ms) + C# verify (~862ms) — because a withdrawal changes the denylist → changes the emitted artifact → the compilers MUST re-run regardless of transaction granularity. A fragment-transaction migration can shave only a fraction of the ≤248ms re-render, on ~5% of libs for 1–2 rounds, against ≥1.26s of mandatory recompile it cannot touch. Even under the maximally pessimistic attribution (the *entire* 248ms is restart overhead), the saving ceiling is dwarfed by the recompile — invisible. So the migration is not built; whole-attempt snapshot/restore stays. **Trigger:** a corpus/validation profile where the whole-attempt rewind + re-render dominates verify-recover wall-clock — concretely, a per-round render cost that rises to rival or exceed the external swiftc+C# recompile (e.g. a very large module whose in-memory rewind + re-render exceeds ~1s), OR a module needing many (≳5) withdrawal rounds so cumulative re-render cost becomes material against the compiles. Reproduce that module as the timing fixture before migrating any emitter to fragment-local transactions.

</details>

<a id="objc-arguments-follow-selector-declaration-order"></a>

## ObjC arguments follow selector declaration order

Bind parameters in their native declaration order, even when the selector reads unusually. For example, `validationStateForExpirationYear:inMonth:` takes year before month. Reordering for readability would mis-call the native method. Parser, emitter and value-level runtime tests pin this behavior; the wiki documents it under “Surprising, but correct.” A runtime-observed argument swap is a different, real defect.

<a id="an-objc-umbrella-can-legitimately-declare-no-classes"></a>

## An ObjC umbrella can legitimately declare no classes

An umbrella containing only an options typedef and an error-domain constant has no classes to emit. An empty class surface does not prove that parsing failed; inspect the actual declarations and the emitted enums/constants. A module with neither classes nor extern constants can legitimately have a namespace-only `ApiDefinition`.

<a id="capability-typed-projection-model"></a>

## Capability-typed projection model

Considered post-0.10.0 and deferred. Idea: a `TypeCapabilities` record (`IsFrozen`, `IsObjCBridged`, `IsSendable`, `IsTriviallyCopyable`, `IsIndirectResult`, conformance set) populated once in TypeDatabase, replacing per-emitter property checks across ~150 files. Argument against: most of the 642 `IsFrozen` references are queries, not redundant derivations; only ~4 of the 46 0.10.0 bugs were traceable to "two emitters chose different projections." Cost/risk doesn't justify the leverage today. **Trigger to revisit:** 3+ bugs sharing the "subsystem A inferred property X one way, subsystem B inferred it differently" shape.

<a id="async-emitter-consolidation-was-investigated-and-declined"></a>

## Async-emitter consolidation was investigated and declined

**Async-emitter consolidation: investigated, not pursued.** The "merge the diverged async emitters" idea was audited and rejected. Reality is ~10 files / ~8,980 LOC, not 3; ~40% is genuinely-different jobs that must not merge (SwiftUI async-View, AsyncStream, AsyncSequence, async-closure inversion, the CSM generic-parent 2-param error ABI), and most remaining divergence is *intentional* — a naive merge would introduce a new ABI bug against working marshalling code. The divergence bugs are real (≥7 in 12 months) but were caught by *new input shapes reaching the path*, never by structure review — reinforcing the input-poor thesis. Only survivor is optional Tier-1 exact-duplicate extraction (`BuildMethodOwnGenericParams` ×2, the `SBW_CancelTask`/`SBW_Free` P/Invoke blocks, the Swift catch-body builder); everything above that is not worth the risk. Don't re-open without a new motivating signal.

This later, broader investigation supersedes the old three-file consolidation proposal and its “third divergence bug” trigger.

<a id="s12c-consolidate-the-five-typespec-c-p-invoke-type-string-translators"></a>

## S12c: consolidate the five `TypeSpec`→C#/P-Invoke type-string translators

S12 landed the byte-identical floor: the three private `IsPointerType(NamedTypeSpec)` copies in `BoundGenericsHandler`/`ClosureHandler`/`ClosureEmitter` were deleted and now route through `TypeDatabaseExtensions.IsPointerType`, which delegates to the `AppleFrameworkRegistry.IsPointerType` SSOT (the SIMD alias-collapse oracle was already centralized in `TypeDatabaseExtensions.TryResolveBoundGenericAlias`). The doc's larger "one uniform `TranslateToCSharp(spec, mode)` on `MarshalingContext`" merge is **deferred and is NOT byte-identical** — a 3-way consensus (Codex consult + paired Codex/Grok design review) found the remainder is call-site *policy*, not duplicated leaf mapping. **Safe to centralize next (byte-identical):** the TypeDatabase non-generic leaf fallback (`GetTypeRecordOrAnyType`), the "don't append `<…>` to `AnyType`/`IntPtrType`" guard, and a `Base<TArgs…>` builder that takes caller-*supplied* translated args. **Must stay caller-owned (behavioral policy — moving it changes output):** existential mode choice (BoundGenerics raw-container-after-gates vs Closure proxy-gated vs Tuple ungated), `Optional<T>` nullable syntax, value-tuple shape, stdlib-container substitutions, nested-generic-owner qualification, simple-enum P/Invoke underlying type, closure proxy-existence gating, and projection-factory-first async surfaces. Also: the per-handler bare `_existentialHandler` (constructed without `CurrentModuleName`/`SpecializationEngine`) must NOT be swapped for `MarshalingContext.Existential` — that flips cross-module qualification (`IFoo`→`DepModule.IFoo`). **Trigger:** a sixth pointer/leaf-predicate copy appears, or translator divergence ships a real bug.

<a id="probe-5-proves-the-candidate-is-a-binding-project-not-that-it-binds-this-xcframework-dismissed-by-design"></a>

## Probe 5 proves the candidate is *a* binding project, not that it binds *this* xcframework — dismissed by design

**Revisit when:** a real repo layout puts an unrelated binding project alone beside a vendored dependency xcframework, or the dependency record starts carrying enough identity to make the check free (see [synthesized dependency identity](../Future/notes/tooling.md#auto-dependency-records-still-carry-a-synthesized-package-identity)).

<details>
<summary>Recorded evidence and disposition</summary>

Raised by the 2026-07-31 paired review and deliberately not taken. `AutoDepResolver.ProbeSiblingBindingProject` accepts the sole csproj in the dependency xcframework's own directory whose XML declares `SwiftBindings.Sdk` (exact SDK-name match on any of the three MSBuild declaration forms — the `<Project Sdk>` attribute, a top-level `<Sdk Name>` element, or an `<Import … Sdk>` attribute anywhere in the file; each accepts a `;`-list and an optional `/version` suffix, and malformed XML fails closed). It does **not** parse the candidate's `SwiftFramework` items or generated metadata to prove that project binds *this* xcframework. Requiring that evidence would reject the auto-discovery shape the probe exists to serve — the framework is frequently discovered, not declared, and the metadata does not exist until that project has been built at least once — while the residual exposure is narrow and self-announcing: it needs an unrelated SwiftBindings project to be the *only* csproj beside a vendored dependency xcframework (two make it ambiguous → warn; the project being built is excluded outright via `--consumer-project`, so a self-`ProjectReference` cannot happen), and a wrong hit fails visibly at build — the referenced project simply does not carry the types the generated code names — rather than shipping something broken. Pinned by `Probe5_ADifferentBindingProjectBesideTheXcframework_IsStillAccepted`. **Trigger:** a real repo layout puts an unrelated binding project alone beside a vendored dependency xcframework, or the dependency record starts carrying enough identity to make the check free (see [synthesized dependency identity](../Future/notes/tooling.md#auto-dependency-records-still-carry-a-synthesized-package-identity)).

</details>

<a id="zero-public-surface-transitive-packages-keep-publishing-ruled-correct-as-is-tombstone"></a>

## Zero-public-surface `[Transitive]` packages keep publishing — ruled correct-as-is (tombstone)

`SwiftBindings.Stripe.UICore` and `SwiftBindings.Stripe.CameraCore` 26.0.0 are live with full native payloads and managed assemblies containing **zero** P/Invokes. Not a generator bug: Stripe gates those modules' entire API behind `@_spi(STP)`, so the public `.swiftinterface` has literally zero declarations and zero emission is correct. Owner ruling (2026-07-28): no generator change and no publish-policy change — both are `[Transitive]` native-dependency carriers that siblings need at restore, not entry points. Kept as a tombstone so the next zero-surface finding isn't re-opened as an emission defect. **Trigger:** a zero-surface package that is *not* a transitive native carrier — i.e. one a consumer would reference as an entry point — publishes empty.

<a id="early-managed-only-packages-and-the-superseded-version-audit"></a>

## Early managed-only packages and the superseded-version audit

The 2026-07-28 audit covered 144 package/version pairs and found no RawRepresentable entry-point defects where binary comparison applied. Eleven early managed-only packages predate in-package native payloads and could not be binary-verified; their declared RawRep symbol sets matched later binary-clean versions. Do not describe this as binary proof for every historical package. A missing-entry-point report on one of those managed-only versions is a packaging-era issue; the recorded response is upgrading to its current package. The local audit artifacts were removed.

## Small implementation invariants

Generated extension receiver names must avoid public parameter names. Emission contexts are per-call; do not restore a process-wide `ModuleEmissionContext.Default` (see `EmissionContextIsolationTests`). A non-additive module-database schema change must update the writer and both readers together; the current version literal is not a separate cleanup project.
