# Post-1.0 Architecture Roadmap

**Status**: Reference inventory — work to schedule after 1.0 ships.
**Companion docs**:
- `src/docs/architecture-gameplan.md` — the 1.0 plan (DONE).
- `src/docs/architecture-gameplan-v2.md` — round 1 of post-1.0 (`libswiftDemangle` swap + SwiftSyntax producer, both graduated from this doc).

The pre-1.0 audits surfaced ~150K LOC of architecture debt. The four milestones in the gameplan address only the parts that move 1.0 quality. The rest is real, but it improves *maintainability*, not *bindings*. It belongs after 1.0 ships.

This doc preserves the full deferred inventory so a future planning session has the complete picture.

---

## Litmus test (how items got here)

Every deferred item below was evaluated against:

> *Will this expose a real binding failure earlier, prevent a known class of bad generated binding, or increase valid emitted API surface?*

Items that passed are in M1–M4 of the gameplan (some at smaller scope). Everything below failed the test — real architectural improvements, not 1.0-blocking.

When picking up post-1.0 work, re-run the test. Priorities can change as 1.0 reveals real consumer pain.

---

## Graduated to v2

The two highest-ROI items in this inventory have moved to `src/docs/architecture-gameplan-v2.md`:

1. **`libswiftDemangle` swap** — v2 Milestone 1.
2. **SwiftSyntax producer behind `SwiftInterfaceFacts`** — v2 Milestone 2.

The rest of the inventory below is genuine improvement but doesn't have the same risk-reduction or ROI density.

---

## Deferred phases

- **`PipelineContext` / kill static collectors.** `ReportCollector` (static + `AsyncLocal<bool>`), `AppleSupplementReferences` (`[ThreadStatic]`), `SwiftUIBridgeCollector` (static). Mechanical refactor; pipeline works today.

- **Pipeline stage abstraction + CLI mode dispatcher.** Replace `BindingsGenerator.GenerateBindings` (500 LOC) and `BindingsGeneratorCommand.Execute` (1,100 LOC, 50+ early returns) with `IPipelineStage` + `ICommandMode`. Refactor; pipeline works today.

- **Diagnostics v1 (full).** The source-position + overload-aware identity subset is in M1/M4 of the gameplan. SARIF / `--explain` / `--suppress` / unified id scheme replacing `SkipReason` + `SWIFTBIND0xx` + `SB000x`: deferred.

- **Plan vs Emit phase separation.** Typed `EmissionPlan` IR; validators become plan-builders; emitters become renderers. The "must yield a Diagnostic to skip" footgun fix is appealing but doesn't fix any binding today.

- **Projection-only Marshaler.** Promote `IProjectionVisitor<T>` to be the only dispatcher; decompose `ClosureHandler` (2,051 LOC), `BoundGenericsHandler` (1,682 LOC), `ExistentialHandler`. Mechanical decomposition; correctness unchanged.

- **Type IR underneath `TypeResolver`.** `TypeId` (declaring-module path + nested-decl spine + interned mangled symbol). `TypeRef = TypeId × Args[]`. The `TypeResolver` seam (M4 of the gameplan) is the load-bearing piece for 1.0; the IR underneath is post-1.0.

- **Strangle post-emission text rewriters (full).** M3 of the gameplan fixes the top causes at emission time. The full subsystem strangle (single `EmissionFeasibilityProbe` consulting per-slice ABI / suppressed proxy refs / etc., retiring `CSharpWrapperCoGater`, `SwiftWrapperPostProcessor`, `ProcessSuppressedProxyReferencesInDirectory`, `SimulatorOnlyMemberDetector`) is post-1.0.

- **`SwiftToolchain` + argument-vector `IProcessRunner`.** 4 duplicated `xcrun` shellouts; shell-string command construction. Path-with-space risk is latent; not currently biting.

- **Build / SDK decompositions.** `Build.RuntimeTests.cs` 3,008 LOC, `Build.Validation.cs` 2,076 LOC, `Sdk.targets` 1,737 LOC, `SwiftWrapperCompiler.cs` 1,712 LOC. Decompose into strategy interfaces. Mechanical; works today.

- **Test architecture rebuild (full).** M2 of the gameplan covers end-to-end consumer test + behavior tier + platform baselines. The full rebuild (4K substring assertion migration to plan assertions, `MockCommandRunner` removal, domain-first taxonomy) is post-1.0.

- **`AppleTypesManifest` carve-out + `TbdParser` extraction.** Build-time tool wearing the costume of a generator subcommand. Mechanical move.

- **Thin code-DOM at the leaf renderer.** Shape decision is contingent on Plan-vs-Emit (also deferred). Decide after that lands.

- **`IGeneratedSwiftObject` migration.** Splitting `ISwiftObject` static-abstract members onto a generator-only interface. Ripples through `SwiftObjectHelper<T>`, `SwiftMarshal`, generated `TypeHandlerHelpers`, protocol proxies, generic containers. Real cleanup; doesn't fix bindings. The minimal 1.0 surface lock in M1 hides the obvious-internals so this can land later without breaking consumers.

- **Dead second metadata model deletion** (`SwiftTypeInfo` / `MetadataKinds`). Throws `NotImplementedException` on every kind except struct. Harmless dead code; deletion is post-1.0.

- **`ExistentialContainer0..8` consolidation.** 1,124 LOC of 9 copy-pasted structs. Real ABI cleanup; works today.

- **Mono/NativeAOT factory consolidation.** ~200 LOC duplicated across `SwiftArray` / `SwiftDictionary` / etc. Refactor; works today.

- **Collection element buffer pooling.** `SwiftArray` indexer alloc-per-access. Performance, not correctness.

---

## When to revisit

Triggers that justify pulling an item forward:

- A 1.0 consumer reports a bug whose root cause is in one of these areas.
- Skip count or `AnyTypeFallback` count plateaus and the next reduction needs the deeper cleanup (e.g., Type IR underneath `TypeResolver`).
- A runtime regression turns out to be drift between the regex parser and a Swift compiler change (justifies SwiftSyntax sooner).
- A new Swift language feature requires per-slice ABI knowledge that the post-emission text rewriters can't provide cleanly.

Without one of these, post-1.0 work should be sequenced by maintainability ROI, not by audit ordering.
