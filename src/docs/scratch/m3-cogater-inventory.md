# M3 Session 1 — CoGater inventory + per-library hit histogram

**Captured**: 2026-04-29
**Branch**: `main` (post-M2 close at `d55ec5a5`)
**Companion**: `architecture-gameplan.md` §M3.
**Lifecycle**: Per the standing rule on milestone scaffolding (gameplan §"Standing rules"), this doc lives only under `src/docs/scratch/` and is deleted in the M3 close commit (Session 4). Supersedes the precursor `scratch/cogater-inventory.md` from 2026-04-28, which is removed in the same commit that lands this doc.

## Why this doc exists

M3 of the architecture gameplan is "Improve emitted API surface". Session 1's deliverable, per the gameplan: *"Catalog every handler in `SwiftWrapperPostProcessor`, `CSharpWrapperCoGater` (Steps D–G), `ProcessSuppressedProxyReferencesInDirectory`, `SimulatorOnlyMemberDetector`. For each handler, classify as either 'shouldn't have emitted' (with proposed emission-time fix location: Marshaler / `PropertyHandler` / etc.) or 'essential Swift compiler output normalization' (keep). Histogram each handler's hit count across validation libs to size the fix-or-keep decisions."*

Open Question #3 in the gameplan sets Session 2's threshold: **top 3 by volume, or any class whose fix cost is < 1 session**. The histogram below is what makes that judgment cuttable without re-investigation.

## Method

- Added a temporary `CoGaterHitCounter` static helper (`src/Swift.Bindings/src/Configuration/CoGaterHitCounter.cs`) gated on `SWIFTBIND_DUMP_COGATER_COUNTS=1`. Zero cost when unset; deleted in the M3 close.
- Threaded `CoGaterHitCounter.Increment(...)` calls into every named handler's hit point in:
  - `SwiftWrapperPostProcessor.cs` — Patterns 1, 2, 2b, 3, 3c, 4, 5.
  - `CSharpWrapperCoGater.cs` — Steps D, E, F (both indexer + method branches), G.
  - `CSharpWrapperCoGater.cs` — `ProcessSuppressedProxyReferences` Transformations T1–T4 + the `DowngradeSuppressedWrapFallbacks` pre-pass.
  - `SimulatorOnlyMemberDetector.cs` — Rule D (`ApplySimulatorGuards`) and the device-slice thunk-assembly filter (`FilterThunkAssembly`).
- Dump sites in `Program.cs`: at end of `GenerateBindings` (after `EmissionReportEmitter.Emit`) and at end of `RunCompileWrapperOnly`. Each dump merges into `cogater-counts.json` next to `binding-report.json` in the per-library output directory.
- Ran `SWIFTBIND_DUMP_COGATER_COUNTS=1 nuke validate` once. Total wall time 4:55 (Compile 0:02 / Validate 2:54 / PackGate 0:57 / BehaviorTier 1:02). All four sub-targets succeeded; baseline preserved.

The 18 handlers below cover every named unit in the four subsystems. Counters that are not exercised by `nuke validate` are reported as zero-hit (e.g. `SimDetector.RuleD` and `SimDetector.FilterThunkAssembly` both require the dual sim+device input branch of `SwiftWrapperCompiler.CompileSlice`, which validate's compile-only single-slice path does not take — so their zeros are "unmeasured here," not "never fires in practice"; flagged below).

## Headline findings

1. **Coverage is narrow.** Of **127 validation target directories** (105 core libs + 22 platform-variant entries: `@macos` / `@maccatalyst` / `@tvos`), only **26** trigger any cogater handler. ~80% of the validation set never reaches the post-emission text rewriters.
2. **Volume is concentrated.** Total cogater invocations measured: **2,594**. The top 4 handlers account for **2,569 / 2,594 (99.0%)**. (Two SimDetector handlers — Rule D and the thunk-assembly filter — fire only on the dual-input ABI path that compile-only validate doesn't take, so their volume is not reflected in this total.)
3. **The Phase 0 prior on Pattern 1 is stale.** `phase0-report-staleness.md` (2026-04-28) used GRDB's 17/18 stripped EveryProtocol conformances as the rubric exemplar of an A-class case. **Pattern 1 fires zero times in this validation run.** M1's emission-side tightening — particularly the typed `WrapperBuildOutcome`, manifest-derived report, and overload-stable identity — has already neutralised Pattern 1 on the current validation surface. Predecessor inventory's "likely the highest-volume A-class fix" assertion is contradicted by data.
4. **Pattern 5 (module/type collision) is the dominant volume**, not the narrow Reachability/SwiftyBeaver case the precursor inventory framed it as. 2,003 hits / 10 targets / avg 200/target. SVGView alone: 624 hits.
5. **Eight of eighteen instrumented handlers fire zero times** (table below). They split between *defensive nets that have already been outpaced by emission-time fixes* (keep as low-cost guards) and *handlers we cannot exercise from `nuke validate` alone* (Rule D and FilterThunkAssembly for SimDetector — both gated on dual-input ABI).

## Histogram — per-handler hit counts

Sorted by total invocations, descending. "Targets" = number of distinct validation-target dirs that hit the handler at least once. "Avg/tgt" = mean count among targets that did hit it (not over the full 127 — a per-target average dominated by zeros would smear volume signal).

| # | Handler | Total | Targets | Avg/tgt | Class |
|---|---|---:|---:|---:|---|
| 1 | `PostProcessor.Pattern5_ModuleCollision` | 2,003 | 10 | 200.3 | A |
| 2 | `PostProcessor.Pattern2_SilgenOrCdeclBroken` | 375 | 10 | 37.5 | A |
| 3 | `ProxyCoGater.DowngradeWrapFallbacks` (pre-pass) | 141 | 10 | 14.1 | A |
| 4 | `ProxyCoGater.T4_PublicMember` | 50 | 10 | 5.0 | A |
| 5 | `CoGater.StepE_DanglingToString` | 12 | 1 | 12.0 | A |
| 6 | `PostProcessor.Pattern3_ExtensionBroken` | 5 | 1 | 5.0 | A |
| 7 | `ProxyCoGater.T3_InterfaceImplementation` | 3 | 3 | 1.0 | A |
| 8 | `ProxyCoGater.T2_UnmanagedCallback` | 2 | 1 | 2.0 | A |
| 9 | `CoGater.StepF_NarrowingOverloads` | 2 | 2 | 1.0 | B |
| 10 | `PostProcessor.Pattern2b_MainActorBroken` | 1 | 1 | 1.0 | A |
| 11 | `PostProcessor.Pattern1_EveryProtocolBlock` | 0 | 0 | — | A |
| 12 | `PostProcessor.Pattern3c_PrivateSbwProtocol` | 0 | 0 | — | A |
| 13 | `PostProcessor.Pattern4_StandaloneFunc` | 0 | 0 | — | A |
| 14 | `CoGater.StepD_LazyAccessors` | 0 | 0 | — | B |
| 15 | `CoGater.StepG_ThrowingClosureFacades` | 0 | 0 | — | B |
| 16 | `ProxyCoGater.T1_StripNonPublic` | 0 | 0 | — | A |
| 17 | `SimDetector.RuleD_ApplyGuards` | 0 | 0 | — | C* |
| 18 | `SimDetector.FilterThunkAssembly` | 0 | 0 | — | C* |

`*` Both SimDetector zeros are **unmeasured-from-validate** rather than zero-in-practice. `nuke validate` runs single-slice compile-only, so `SwiftWrapperCompiler` only reaches the simulator-only detection + thunk-assembly filtering branch when both sim and device ABI JSONs are present (the dual-input branch in `SwiftWrapperCompiler`). Production package builds (e.g. `swift-dotnet-packages` packaging Stripe SDKs) do produce the dual-input case. Volume there is non-zero per the precursor inventory's StripeIdentity references but is not quantified in this run.

## Per-target distribution (top 15 by total hits)

| Target | Total | Handlers fired |
|---|---:|---|
| SVGView | 624 | Pattern5=624 |
| Mixpanel | 402 | Pattern5=402 |
| SwiftyBeaver | 330 | Pattern5=322, Pattern2=8 |
| XMLCoder | 221 | Pattern2=175, T4=18, StepE=12, DowngradeWrapFallbacks=10, Pattern3=5, StepF=1 |
| FSPagerView | 191 | Pattern5=191 |
| Valet | 176 | Pattern5=176 |
| AnimatedCollectionViewLayout | 153 | Pattern5=153 |
| SkeletonView | 132 | Pattern2=118, DowngradeWrapFallbacks=8, T4=5, StepF=1 |
| NVActivityIndicatorView | 93 | Pattern2=58, Pattern5=35 |
| Alamofire | 62 | DowngradeWrapFallbacks=52, T4=10 |
| KeychainSwift | 53 | Pattern5=53 |
| Reachability | 45 | Pattern5=45 |
| GRDB | 30 | DowngradeWrapFallbacks=23, T4=5, T2=2 |
| Nuke | 17 | DowngradeWrapFallbacks=13, T4=3, T3=1 |
| Nuke@macos | 17 | DowngradeWrapFallbacks=13, T4=3, T3=1 |

A note on Nuke's three platform variants (`Nuke`, `Nuke@macos`, `Nuke@tvos`) all firing identical 13/3/1 distributions: the same upstream emission decision is materialising once per platform target. Fixes that land at the emitter benefit all three at once; this should not be misread as 3× the volume.

---

# Per-handler classification

Rubric:
- **A — Shouldn't have emitted this**: the handler papers over an emission bug or convention. The emitter has (or could have) the information needed to avoid producing the broken output. Fix at emission and **delete the handler** (or reduce to a defensive assertion).
- **B — Swift output normalization (essential)**: the handler normalises legitimate compiler output, ABI-JSON conventions, or pipeline-ordering realities we cannot move into the emitter. Must stay.
- **C — Unsure / needs investigation**: classification depends on a question we can't answer from a static read of the code.

Classifications below are inherited from the precursor `cogater-inventory.md` (which had a Plan-subagent adjudication round). The classifications stand; only the volume-driven fix ranking changes.

## Subsystem 1 — `SwiftWrapperPostProcessor`

**File**: `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs`. Caller: `SwiftWrapperCompiler.cs:202` (per-slice loop) and `SwiftWrapperCompiler.cs:606` (second compilation pass). Pattern execution order is sequential.

### Pattern 1 — EveryProtocol blocks referencing internal / Swift-unavailable types
- **Lines**: 94–126 (block detection); helpers `ReferencesInternalType` 500–514 and `ReferencesSwiftUnavailableType` 520–528.
- **Behavior**: For each `extension/class/public final class EveryProtocol …` block, preserves the class definition + Codable/Error stubs + composition extensions. Otherwise strips the block when its body references any name in `internalTypeNames` or any name in `SwiftUnavailableTypes` (`{"NSInvocation"}`); collects stripped `@_cdecl`/`@_silgen_name` symbols into `StrippedSymbols`.
- **Classification**: **A**.
- **Volume**: 0 / 127. M1's emission-side work (overload-stable identity, manifest-derived report, fail-closed gates) has already removed the conditions that produced GRDB's 17/18 stripped conformances on the precursor doc.
- **Upstream fix location** (kept for completeness; not Session 2 priority): `EveryProtocolEmitter` / `ProtocolHandler` conformance-emission gate. `internalTypeNames` already computed in `Program.cs:CollectInternalTypeNames` (1065–1102).
- **Recommendation**: deprioritise. Track to prove not-rotting; if a future library reintroduces volume here, fix at emission then. The handler is cheap to keep as a defensive net; deleting it now buys nothing.

### Pattern 2 — `@_silgen_name` / `@_cdecl` function blocks (broken bodies)
- **Lines**: 128–153; `IsSilgenNameBroken` at 341–371.
- **Behavior**: Strips function block when (a) `IsSilgenNameBroken` returns true, (b) body references an internal type, or (c) body references `NSInvocation`. Sub-pattern 2(a) — `EveryProtocol()` placeholder bodies. Sub-pattern 2(g) — closure type in `.load(as: @escaping`/`.load(as: @Sendable`. Both 2(a) and 2(g) self-identify as safety nets that "should be prevented at emission time"; precedent of 2(b)–2(f) being fully removed in Phase 1 sets the path.
- **Classification**: **A**.
- **Volume**: 375 / 10 targets. Concentrated: XMLCoder=175, SkeletonView=118, NVActivityIndicatorView=58. Pattern 2 is the **second-highest-volume handler**.
- **Upstream fix location**:
  - 2(a): `EveryProtocolEmitter` / `MethodWrapperEmitter` body-stub gate.
  - 2(g): verify completeness of `CanConvertToCdecl` for closure-typed `UnsafeRawPointer.load(as:)` paths.
  - Internal-type body case: `MemberValidationPipeline` / type-resolution at member-emission time (refuse to emit if signature reaches `internalTypeNames`).
- **Recommendation**: **Top-fix candidate #2** for Session 2.

### Pattern 2b — Standalone `@MainActor` preceding broken `@_silgen_name` / `@_cdecl`
- **Lines**: 155–179.
- **Behavior**: When a line is exactly `@MainActor` and the next line opens a `@_silgen_name(`/`@_cdecl(` block, applies Pattern 2's broken-body checks to the next block. Strips both lines if broken (otherwise the `@MainActor` would dangle). `ConstructorWrapperEmitter` emits this two-line form.
- **Classification**: **A**.
- **Volume**: 1 / 1 target (StripePaymentSheet).
- **Upstream fix location**: same as Pattern 2 — once Pattern 2's underlying bugs are gated, no broken constructor wrapper bodies for Pattern 2b to clean.
- **Recommendation**: rides along with the Pattern 2 fix.

### Pattern 3 — Non-EveryProtocol extension blocks
- **Lines**: 181–202; `IsExtensionBroken` at 377–394.
- **Behavior**: For each `extension X { … }` block (non-EveryProtocol), strips when body contains `EveryProtocol()` outside the system carve-out, header *or* body references an internal type, or body references `NSInvocation`. Header check covers `extension XMLCoder.SharedBox: _SBW_…` where the extended type is module-internal.
- **Classification**: **A**.
- **Volume**: 5 / 1 target (XMLCoder).
- **Upstream fix location**: extension-emitting handlers (`ExtensionEmitter` family). Refuse to emit an extension whose extended type or signature reaches `internalTypeNames`.
- **Recommendation**: low individual volume. Folds into the same emission-side type-resolution gate as Pattern 1 / 3c / 4. Worth doing only as part of a single internal-type emission gate (the unifier across Patterns 1, 3, 3c, 4 + parts of Pattern 2). Would require validating that the gate doesn't regress emission for the libraries that currently emit-then-strip.

### Pattern 3c — Private `_SBW_` dispatch protocol declarations
- **Lines**: 204–222.
- **Behavior**: Strips `private protocol _SBW_…` blocks whose header or body references an internal type or `NSInvocation`. `_SBW_` protocols are part of the generic factory dispatch pattern; their associated-type constraints can break when referencing internal types.
- **Classification**: **A**.
- **Volume**: 0 / 127.
- **Upstream fix location**: generic-factory dispatch emitter that produces `_SBW_` protocols.
- **Recommendation**: deprioritise — defensive net, no current volume.

### Pattern 4 — Standalone `public func SBW_` / `public func PInvoke_` blocks
- **Lines**: 224–242; `IsStandaloneFuncBroken` at 400–415.
- **Behavior**: Strips on the same conditions as Pattern 2 but for standalone wrappers without leading `@_silgen_name`/`@_cdecl`. After stripping, calls `RemoveTrailingWrapperPreamble`.
- **Classification**: **A**.
- **Volume**: 0 / 127.
- **Upstream fix location**: same wrapper-emitting paths as Pattern 2.
- **Recommendation**: deprioritise.

### Pattern 5 — Module/type name collision rewrite
- **Lines**: 248–290; regex at 258–260.
- **Behavior**: Active when `moduleNameForCollision` is non-null (module has a public type with the same identifier). Regex `\b<moduleName>\.(\w+(?:\.\w+)*)` strips the module prefix unless the immediate child is in `nestedTypesInCollidingClass`.
- **Classification**: **A**.
- **Volume**: **2,003 / 10 targets / 200.3 avg per affected target — the dominant handler in the validation set.** SVGView=624, Mixpanel=402, SwiftyBeaver=322, FSPagerView=191, Valet=176, AnimatedCollectionViewLayout=153, KeychainSwift=53, Reachability=45, NVActivityIndicatorView=35, AlertToast=2.
- **Upstream fix location**: type-reference qualification at emit time. The collision is detectable pre-emission (module name + set of public type names in the module are both available). Either:
  - **Option A**: At every emit site that prepends `module + "."` to a type name, check whether the module name collides with a public type and emit unqualified.
  - **Option B**: Add a single qualification-policy hook in the Swift wrapper printer (the materialisation point that turns a `TypeSpec` into source text), parametrised by the collision set computed once per module.
  Option B is the smaller surface and is closer to where the type-printing decisions already live.
- **Recommendation**: **Top-fix candidate #1** for Session 2 — by far the largest volume. Important: each Pattern 5 hit is a regex replacement on a single line, so the count is line-rewrites, not member-stripping. The user-facing impact is "wrapper recompilation cost + correctness exposure to regex edge cases" rather than "API surface lost." But the volume tax on the validation gate is real, and a single-emission-time fix moves it cleanly to zero.

### Cross-cutting filter — `ReferencesInternalType`
- **Lines**: 500–514.
- **Behavior**: Word-boundary regex check on block bodies for any name in `internalTypeNames` (union of module-internal types from ABI + underscore-suppressed types, name-collision-resolved against public types).
- **Classification**: **A** (root cause for Patterns 1, 3, 3c; contributes to Patterns 2, 4).
- **Volume**: not directly counted — drives the per-pattern hits above. Effective combined volume: ~5 (Pattern 3) + small share of Pattern 2 = small.
- **Upstream fix location**: type-resolution at the emission gate. Adjacent to (but distinct from) M4's `TypeResolver` central seam. Doing this in M3 means a focused emission-side gate; the M4 work is the broader IR rewrite.

### Cross-cutting filter — `ReferencesSwiftUnavailableType` (`NSInvocation`)
- **Lines**: 520–528. Set is `{"NSInvocation"}`.
- **Classification**: **C**.
- **Volume**: not directly counted.
- **Question to resolve**: does the Swift ABI JSON reliably surface `NS_SWIFT_UNAVAILABLE` for ObjC-bridged types reached transitively? If yes → **A**. If no → **B**. Single-element set today makes either resolution low-cost.

## Subsystem 2 — `CSharpWrapperCoGater` Steps D–G

**File**: `src/Swift.Bindings/src/Configuration/CSharpWrapperCoGater.cs`. Entry point `ProcessDirectory` at line 180, called from `Program.cs` after `SwiftWrapperCompiler` returns its `StrippedSymbols`. Per-file pipeline: A → B → C → D → E → F → G. A–C are P/Invoke transitive-closure detection; D–G are gap-fillers.

### Step D — `StripOrphanedLazyAccessors`
- **Lines**: 959–1008.
- **Behavior**: When Step B strips a `_lazy_X` backing field (its initialiser lambda calls a stripped P/Invoke), strips the corresponding expression-bodied property `public static T Y => _lazy_X.Value;`. Scoped by enclosing type.
- **Classification**: **B**. The `Lazy<T>` cache is not the wrong shape; collapsing it would regress thread-safety/perf. Step D handles the inevitable downstream stripping fallout.
- **Volume**: 0 / 127. Fine — means Step B is producing fewer broken outputs.
- **Recommendation**: keep.

### Step E — `StripDanglingToString`
- **Lines**: 1014–1044.
- **Behavior**: When `Description` is stripped, strips `public override string ToString() => Description;` to avoid a dangling reference.
- **Classification**: **A** (deferred-decision A — the emitter owns `WasEmitted`, but deciding at emission instead of post-cogating is wrong timing).
- **Volume**: 12 / 1 target (XMLCoder).
- **Upstream fix location**: emit `ToString()` to call the same P/Invoke as `Description` — falls inside Step B's transitive closure naturally and gets stripped together.
- **Recommendation**: clean simple fix at emission. Low absolute volume, but the fix is small and well-bounded.

### Step F — `StripOrphanedNarrowingOverloads`
- **Lines**: 1850–2008. Emitter source: `NativeIntOverloadEmitter.cs:133`.
- **Behavior**: `int`/`uint` convenience overloads that forward to `nint`/`nuint` (`Method(int x) => Method((nint)x);`). When the wide version is stripped, the narrowing dangles.
- **Classification**: **B**. Narrowing references the wide method by C# identity, not P/Invoke entry point. Step B walks P/Invoke edges; a separate C#-call-graph pass is the correct architectural layer.
- **Volume**: 2 / 2 targets (XMLCoder, SkeletonView).
- **Recommendation**: keep.

### Step G — `StripOrphanedThrowingClosureFacades`
- **Lines**: 1107–1191.
- **Behavior**: `ThrowingClosureSimplificationEmitter` emits convenience overloads taking simpler delegate types and self-calling the base by C# name. When the base is stripped, the facade's self-call breaks.
- **Classification**: **B** (same structural reason as Step F).
- **Volume**: 0 / 127.
- **Recommendation**: keep.

## Subsystem 3 — `ProcessSuppressedProxyReferencesInDirectory`

**File**: `src/Swift.Bindings/src/Configuration/CSharpWrapperCoGater.cs:2335`. Entry point `ProcessSuppressedProxyReferencesInDirectory(directory, suppressedProxyClassNames, logger)`; invoked from `Program.cs` when `emissionContext.SuppressedProxyClassNames.Count > 0`. Runs **before** Swift wrapper compilation. Suppressed-proxy set populated by `ProtocolHandler.cs:458` `RecordSuppressedProxy(...)` (class-bound protocols, generic constraint conflicts, static-method type conflicts).

### Pre-pass — `DowngradeSuppressedWrapFallbacks`
- **Lines**: 2317–2329. Regex `s_wrapFallbackPattern`.
- **Behavior**: Strips the `static __v => new FooProxy(__v)` lambda from `ExistentialContainerFactory.GetOrCreate<IFoo>(value, …)` calls when `FooProxy` is suppressed. Preserves the surrounding call.
- **Classification**: **A**.
- **Volume**: **141 / 10 targets**. Alamofire=52, GRDB=23, Nuke (×3 platforms)=39 combined, XMLCoder=10, SkeletonView=8, Mappedin=7, LiveCommunicationKit@maccatalyst=1, RoomPlan=1.
- **Upstream fix location**: existential-fallback emission path (likely `ExistentialContainerFactory` call-site emitter / `ProjectionVisitor` for existentials). Consult `emissionContext.SuppressedProxyClassNames` before emitting the lambda argument; emit `GetOrCreate<IFoo>(value)` directly.
- **Recommendation**: **Top-fix candidate #3** for Session 2, *combined with T4 below*. Same root cause; same emission-side gate fixes both.

### Transformation 1 — Strip non-public methods constructing suppressed proxies
- **Behavior**: Block-bodied non-public method/constructor/accessor that contains `new FooProxy(` or `new SwiftInterop.FooProxy(`, not a property helper, fully stripped (declaration + preamble + block).
- **Classification**: **A**. Same root cause as the pre-pass.
- **Volume**: 0 / 127.
- **Recommendation**: piggybacks on the pre-pass / T4 emission fix.

### Transformation 2 — `[UnmanagedCallersOnly]` callbacks → no-op stub
- **Behavior**: Vtable receiver callbacks (function-pointer-stored) cannot be deleted (vtable layout). Body replaced with no-op stub.
- **Classification**: **A**. Emitter could emit no-op directly when proxy is suppressed.
- **Volume**: 2 / 1 target (GRDB).
- **Recommendation**: bundles with the pre-pass / T4 fix.

### Transformation 3 — Interface implementation bodies → `throw NotSupportedException`
- **Behavior**: Method/property that constructs a suppressed proxy AND is an interface implementation gets its body replaced with `throw new NotSupportedException(...)` rather than stripped (would cause CS0535).
- **Classification**: **A**.
- **Volume**: 3 / 3 targets (Nuke and its platform variants).
- **Recommendation**: bundles with the pre-pass / T4 fix.

### Transformation 4 — Public method / property-helper bodies → `throw`
- **Behavior**: Public methods constructing a suppressed proxy get their bodies replaced with `throw NotSupportedException`. Property helpers (`_Get`/`_Set`) likewise (avoids cascade-stripping the public property forwarder). Events fully stripped.
- **Classification**: **A**.
- **Volume**: **50 / 10 targets**. XMLCoder=18, Alamofire=10, GRDB=5, SkeletonView=5, Nuke (×3)=9 combined, LiveCommunicationKit@maccatalyst=1, Mappedin=1, RoomPlan=1.
- **Upstream fix location**: same as the pre-pass — every emission path producing `new <…>Proxy(…)` (vtable callbacks, interface implementations, public forwarders). Consult `emissionContext.SuppressedProxyClassNames` before emission.
- **Recommendation**: **Top-fix candidate #3 (combined with the pre-pass)**.

### Transformation 5 — Shared `StripOrphanedNarrowingOverloads` invocation
- Same Step F helper. Already counted under Step F (Subsystem 2). Not double-counted in the histogram.

## Subsystem 4 — `SimulatorOnlyMemberDetector`

**File**: `src/Swift.Bindings/src/Configuration/SimulatorOnlyMemberDetector.cs`. Reconciles Apple's xcframework reality (slices expose simulator-only members behind `#if targetEnvironment(simulator)`) with our pipeline (one set of generated wrappers across both slices).

### Rule A — `ExtractMembers` (ABI JSON walker)
- **Classification**: **B**. Reads Apple's ABI JSON; no other machine-readable source.

### Rule B — Constructor `c` → `C` mangled-name patch
- **Classification**: **A — non-priority** (one-line patch mirroring an emitter convention; the realistic deliverable is "document and leave"). Don't surface as a fix candidate.

### Rule C — Property (`Var`) hash suppression
- **Classification**: **A**. Direct consequence of property wrapper names omitting hashes (`SBW_Get_<…>` / `SBW_Set_<…>`).
- **Upstream fix location**: property wrapper-name emitter — embed mangled-name hash in the wrapper name. **Caveat**: assumes wrapper-name changes don't ripple into tests / downstream packages / P/Invoke entry-point declarations elsewhere. Verify locality before committing to a "< 1 session" estimate.
- **Combined collapse**: Fixing Rule C also collapses Rule G2 (substitution-decoder) and simplifies Rule F (cdecl block hash-or-name fallback) to a single hash-match.

### Rule D — `ApplySimulatorGuards` (#if insertion)
- **Classification**: **C**. The work (emitting `#if` guards) is essential reality. The form (post-process regex over emitter-emitted comments) is one of two valid architectures.
- **Volume**: 0 / 127 in this run, **unmeasured**. Validate's compile-only path doesn't take the dual-input branch of `SwiftWrapperCompiler`. Production pack paths do; volume there is non-zero on Stripe-family libs per the precursor inventory's StripeIdentity references but is unquantified here.
- **Question to resolve**: Is the generator architecturally constrained to single-ABI-JSON input, or could it accept both routinely? If dual-input is feasible, this is **A** (emit `#if` guards inline at C# / Swift wrapper emission, eliminating the regex post-pass). If single-input is fixed, it's **B**.

### Device-slice thunk filter — `FilterThunkAssembly`
- **Lines**: 401–467 (counter at 451).
- **Behavior**: Filters the device-slice arm64 native-thunk assembly (`*.arm64.s`) to remove thunk blocks that reference simulator-only members. Block detection walks `.globl _thunk_…` markers and matches via `simOnly.MatchesThunkBlock` (mangled-name-hash regex). Without this filter, the device-slice link would dangle on simulator-only symbols.
- **Classification**: **C** — same architectural question as Rule D. If the generator moves to dual-input ABI with inline simulator guards, the device-slice thunk source would also be partitionable at emission and this regex pass becomes unnecessary. If we stay single-ABI per slice, this stays.
- **Volume**: 0 / 127 in this run, **unmeasured** (same reason as Rule D).
- **Recommendation**: deferred behind the same Rule D question. Don't restructure independently.

### Rule E — `ResolveQualifiedName` module-prefix stripping
- **Classification**: **A**. Wrapper-comment emitter writes `<moduleName>.…`; the resolver undoes it.
- **Upstream fix**: drop the module prefix from `// Method @_cdecl wrapper for …` comments. Folds into Rule D's broader question.

### Rule F — `MatchesCdeclBlock` hash-then-name fallback
- **Classification**: **A — conditional** (depends on whether we treat the wrapper-comment shape as a stable contract). If we move disambiguation into emitted metadata, the matcher trivialises and this is A; otherwise B.

### Rule G1 — `MatchesThunkBlock` hash matching
- **Classification**: **A**. Same emission-convention reason as Rule F.

### Rule G2 — Token-aware fallback (Swift mangling decoder)
- **Classification**: **A — dependent collapse of Rule C**.

### Rule H — `FindBlockEnd` brace-depth scanner
- **Classification**: **B**. Pure utility.

### Rule I — Tail-call vs. multi-instruction thunk parser
- **Classification**: **B**. Both forms are emitted by Swift/LLVM.

---

# Top fix candidates (ranked by validated volume)

Per gameplan Open Question #3: target the top ~3 by volume, or any class whose fix cost is < 1 session — whichever yields more.

## #1 — Pattern 5: emit unqualified module-internal type references when the module's own name collides with a public type

**Volume**: **2,003 hits / 10 targets / 99.0% of all cogater volume contributed by handlers ranked #1–4**.

**Targets affected**: SVGView (624), Mixpanel (402), SwiftyBeaver (322), FSPagerView (191), Valet (176), AnimatedCollectionViewLayout (153), KeychainSwift (53), Reachability (45), NVActivityIndicatorView (35), AlertToast (2). All cases where the module name is identical to a public type name in that module (Reachability, SwiftyBeaver, etc.) or where the regex finds qualified references that don't need to be qualified.

**Eliminates**: Pattern 5 entirely.

**Upstream fix location**: type-reference qualification policy at the Swift wrapper printer (the function/site that turns a `TypeSpec` into emitted source text). Detect collision once per module (module name ∈ public type names ⇒ collision-flag), pass into the printer, emit unqualified for matching references.

**Risks** (Session 2 must validate before deleting Pattern 5):
- Nested types in colliding classes (`SwiftyBeaver.Level` where `Level` is nested in class `SwiftyBeaver`): the existing post-process correctly preserves these via `nestedTypesInCollidingClass`. The emission-time fix must handle the same case.
- Imports / type-aliases referencing the module name: the existing post-process skips `import` lines explicitly. The emitter is unlikely to emit an `import` mid-source, but the gate must exclude any qualifying-context where stripping the prefix would change resolution.
- Unit-test exposure: the fix should ship with focused tests covering the collision libraries above (in particular Reachability and SwiftyBeaver, which the precursor inventory already singled out).

**Estimated session cost**: < 1 session for the emission gate + tests. The post-process's regex semantics give a clear oracle.

## #2 — Pattern 2: tighten emission-time gates on broken `@_silgen_name` / `@_cdecl` wrapper bodies

**Volume**: **375 hits / 10 targets / 14.5% of total**.

**Targets affected**: XMLCoder (175), SkeletonView (118), NVActivityIndicatorView (58), StripePaymentSheet (11), SwiftyBeaver (8), CryptoSwift / Quick / StripeCryptoOnramp / StripePayments / StripeUICore (1 each).

**Eliminates**: Pattern 2 entirely. Pattern 2b (1 hit, StripePaymentSheet) goes with it.

**Sub-cause split** (handler currently lumps these; Session 2 should verify the split before scoping):
- 2(a) `EveryProtocol()` placeholder bodies — fix in `EveryProtocolEmitter` / `MethodWrapperEmitter` body-stub gate.
- 2(g) `.load(as: @escaping)` / `.load(as: @Sendable)` — verify `CanConvertToCdecl` rejects these. The handler's `onSafetyNetWarning` callback already exists for 2(g) regression detection; it should never fire after the upstream tighten.
- Internal-type body case — the same emission-time type-resolution gate that handles Patterns 1 / 3 / 3c / 4 (those are zero-volume today, so the unifier serves Pattern 2 in practice). Member emission must refuse when a signature reaches into `internalTypeNames`.

**Upstream fix location**: depends on which sub-cause dominates the 375 hits. **Session 2 needs a one-line decomposition counter**: split the `Pattern2` increment into `Pattern2.EveryProtocolPlaceholder`, `Pattern2.LoadAsClosure`, `Pattern2.InternalType`, `Pattern2.NSInvocation`. The simplest path is a 5-minute change to `IsSilgenNameBroken` + the strip site, run validate again. Doing this *before* picking the fix layer prevents the larger emission gate getting deferred for a smaller sub-fix.

**Estimated session cost**: 1 session if the decomposition reveals a dominant sub-cause; otherwise 1+ if multiple emission paths must be hardened in one go.

## #3 — ProxyCoGater: gate proxy-using emission on `SuppressedProxyClassNames`

**Volume**: **141 (DowngradeWrapFallbacks) + 50 (T4) + 3 (T3) + 2 (T2) = 196 hits / 10 targets / 7.6% of total**.

**Targets affected**: Alamofire (62), GRDB (30), Nuke + Nuke@macos + Nuke@tvos (17 each), XMLCoder (28), SkeletonView (13), Mappedin (8), LiveCommunicationKit@maccatalyst (2), RoomPlan (2).

**Eliminates**: `DowngradeSuppressedWrapFallbacks` (pre-pass), Transformations T1, T2, T3, T4. T5 is shared with Step F (B-class, stays).

**Upstream fix location**: every emission path that produces `new <…>Proxy(…)`, vtable callback function-pointers, or `ExistentialContainerFactory.GetOrCreate<…>(…, static __v => new …Proxy(__v))`. Consult `emissionContext.SuppressedProxyClassNames` before emission; emit no-op / throw-stub / unwrapped form directly.

**Implementation challenge**: proxy suppression is decided during the same emission pass that produces the proxy-using code. Order-of-emission may need a defer-queue or two-pass strategy. Session 2 should not start this without scoping the order-of-decision constraint first.

**Estimated session cost**: ~1 session for the well-bounded surface (every `new …Proxy(` site), but the order-of-emission may push to 1.5.

## Bench (volume-ranked but below the top 3 line)

- **Step E (`StripDanglingToString`)** — A. 12 / 1 (XMLCoder). Clean low-cost fix: emit `ToString()` to call the same P/Invoke as `Description`. Worth bundling with #2 if XMLCoder is in scope.
- **Pattern 3 (extension blocks)** — A. 5 / 1 (XMLCoder). Same internal-type emission gate as Patterns 1 / 3c / 4. Bundles with the broader internal-type gate if Session 3 picks that up.
- **Step F (narrowing overloads)** — B. 2 / 2. Keep.
- **Pattern 1 / 3c / 4 / Step D / Step G / T1** — A or B at zero volume. Keep as defensive nets; deleting buys nothing today; touching them risks regression for cheap.
- **Rule C / G2 / F (SimDetector)** — A-class but unmeasured volume in this run. If Session 3 takes the dual-input ABI direction (per Rule D's open question), Rules C/E/F/G1/G2 mostly collapse together; otherwise leave.

## C-class items requiring investigation before classification

- **`ReferencesSwiftUnavailableType` (NSInvocation)** — does the ABI JSON reliably surface `NS_SWIFT_UNAVAILABLE` for transitively-reached ObjC types? Single-element set today; either resolution is low-cost.
- **`ApplySimulatorGuards` (Rule D)** — is the generator architecturally constrained to single-ABI-JSON input? If dual-input is feasible, emit `#if targetEnvironment(simulator)` directly and the entire wrapper-comment regex post-pass goes away (also eliminates Rule E and parts of F). Session 3 should investigate before committing fix scope.

---

# Counts summary

- **Handlers inventoried**: 18 (instrumented) + 9 (B-class utilities + cross-cutting filters that don't have a single hit point) = ~27 named units.
- **Handlers with measured volume**: 10 / 18.
- **Handlers with zero hits in `nuke validate`**: 8 / 18 — split between handlers M1 has already neutralised (Pattern 1, 3c, 4, T1) and handlers not exercised by validate's compile-only path (Rule D + FilterThunkAssembly in SimDetector — both gated on dual-input ABI).
- **Total cogater invocations across the validation set**: 2,594.
- **Top 3 fix candidates account for**: 2,003 + 375 + 196 = 2,574 / 2,594 = **99.2% of measurable volume**.

The 99.2% concentration is the gate-clearing argument for Session 2: pick #1, do it cleanly, run validate, get a real volume reduction. Session 3 picks #2 + #3 together (or splits across two sessions if Pattern 2's sub-cause decomposition reveals layered fixes).

# Open items handed to Session 2

1. **Pattern 2 sub-cause decomposition counter.** Session 2's first 10 minutes: split the existing `Pattern2_SilgenOrCdeclBroken` counter into `Pattern2.EveryProtocolPlaceholder` / `Pattern2.LoadAsClosure` / `Pattern2.InternalType` / `Pattern2.NSInvocation`, re-run validate, and *only then* pick which emission gate to harden. This decomposition is in scope for #2 and prevents premature commitment to the wrong emission layer.
2. **Pattern 5 nested-type carve-out parity.** When implementing the emission-time qualification policy, ensure `nestedTypesInCollidingClass` semantics are preserved (SwiftyBeaver.Level case). The existing post-process is the oracle.
3. **Suppressed-proxy emission order.** Before starting #3, scope whether `emissionContext.SuppressedProxyClassNames` is fully populated by the time every `new …Proxy(` site emits. If not, decide between a defer-queue and a two-pass strategy in scoping, not implementation.
4. **SimDetector volume.** Get one production-pack measurement (a Stripe-family lib or any library that triggers dual-input ABI) for Rule D + `FilterThunkAssembly` + Rule C volume before committing #3 of M3 (Session 3) to `SwiftUICore` parity vs SimDetector restructure. The current zeros are validate artifacts, not ground truth.

# Roadmap reconciliation note

Per gameplan Open Question #5 (mid-milestone vs end-of-milestone roadmap reconciliation): **end of M3** is when `roadmap.md` Theme A rows update for the skip-cause changes the M3 work surfaces. This Session 1 inventory itself does not move skip rows; Session 4 closes the loop.
