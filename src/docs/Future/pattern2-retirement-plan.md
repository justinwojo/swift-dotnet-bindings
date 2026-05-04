# Pattern 2 Retirement — Recon & Implementation Plan (v2)

## Session Plan

This workstream is sized at **2 sessions** end-to-end, with a real chance of fitting in 1 if the walker and the 4 risk libraries cooperate. The split batches implementation before validation — re-loading context to interpret a `nuke validate` result mid-session is wasted overhead.

| Session | Scope | Steps |
|---|---|---|
| 1 | All implementation. Plumbing onto `ModuleDecl`, walker + unit tests, gate insertion across method/property/subscript paths (including `SubscriptHandler` call site, protocol-subscript investigation, `SkipReason` wiring, telemetry counter), BindingTests fixture (positive + negative). End in a pre-validation state. | 1, 2, 3, 4 |
| 2 | All validation + cleanup. Sub-cause telemetry classifier, `nuke validate --filter` against the 4 risk libraries, full `nuke validate`, `nuke binding-tests` sim + device, residue inventory, documentation. Diagnose and fix in-session if anything regresses. | 5, 6, 7 |

**Could land in 1 session if:** walker leans cleanly on existing `TypeSpec` traversal patterns (likely from `ValidationRuleSet.ReferencesInternalModuleType` shape), the 4 risk libraries pass on first attempt, and `nuke binding-tests --device` is the only wall-clock time sink. Wall-clock runtime for the validation chain is real (~10–15 min for the full sweep + sim + device), but that's build time, not work time.

**Would push to 3 sessions only if:** validation surfaces a real regression that needs walker re-design, or protocol-subscripts turn out to need substantive `MemberGateEvaluator` work we haven't scoped.

The **deferred Pattern 2 retirement followup** (body-reference + `NSInvocation` residue audit, see Out of Scope below) is a separate 1–2 session workstream whenever we pick it up.

## Background

`SwiftWrapperPostProcessor.Pattern2_SilgenOrCdeclBroken` is an after-the-fact cleanup pass: the generator emits Swift `@_cdecl` / `@_silgen_name` wrappers, then a post-processor sweeps in and strips broken wrapper bodies whose *body text* mentions `@usableFromInline internal` type names (the Swift compiler refuses to allow internal types in wrapper signatures or bodies). On the C# side, `CSharpWrapperCoGater` removes the corresponding P/Invoke and its 3-level transitive callers, so the public C# method disappears entirely.

**~99% of Pattern 2 hits** are the dominant `Pattern2.InternalType` shape. The plan moves that dominant case to an emission-time gate. The post-processor stays in place as a safety net for the residue (body-reference shapes the emission-time gate can't predict, plus the `NSInvocation` sub-cause and other safety-net shapes — see Findings 6 and 7).

## Goal

1. **Architectural cleanup.** Move the dominant Pattern 2 case to a right-layer eligibility gate. The 99% common path stops needing emit-then-strip.
2. **Net-neutral on user-visible API surface.** Methods stripped today should remain stripped. Goal is *not* to expand callable APIs — that requires per-shape ABI classification, which is a separate (and brittle) workstream.
3. **Cover the same regression-risk surface.** The 4 libraries that broke under a prior attempt (CryptoSwift / SkeletonView / NVActivityIndicatorView / XMLCoder) must continue to compile and pass.
4. **Pattern 2 stays — for now.** Retiring the post-processor entirely is *not* in scope. After this work, Pattern 2 hit count should drop to near-zero; full retirement becomes a separate followup once the body-reference and `NSInvocation` residue is audited and either gated at emission or accepted as legitimate post-processing scope.

## Recon Findings

### Finding 1 — Plumbing: attach `InternalTypeNames` to `ModuleDecl`

`InternalTypeNames` is a per-module `HashSet<string>` built in `Program.cs:1043–1051` (`Program.CollectInternalTypeNames`), called at `Program.cs:271` immediately after `swiftParser.ParseModule()`. It walks `ModuleDecl.Types` recursively, collecting short + module-qualified names for every `TypeDecl` with `IsModuleInternal == true`, then deduplicates short-name collisions against public types. A second merge of underscore-suppressed names happens at `Program.cs:313`.

Today the set is a `Program.cs` local that gets passed sideways into `SwiftWrapperCompiler.Process` via the `internalTypeNames` parameter (`SwiftWrapperCompiler.cs:133`), which forwards it to `SwiftWrapperPostProcessor.Process`. None of the wrapper-emission gates see it.

**Two plumbing options were considered:**

| Option | Approach | Blast radius |
|---|---|---|
| A | Thread through `MethodEnvironment` (add 6th optional ctor param) | 20 src construction sites + 635 test sites. Optional default keeps tests compiling unchanged, but the 20 src sites need audit because some synthesize "reduced" `MethodDecl`s with no natural set to forward. |
| B | Attach to `ModuleDecl` as a `set;` property | 1 change point in `Program.cs` (plus the `:313` merge). Zero construction-site churn. |

**Option B wins clearly** and follows two existing precedents on `ModuleDecl`:
- `ExportedSymbols` (`HashSet<string>?`, populated post-parse from TBD file, read by `PInvokeEmitter.cs:1018`)
- `ConformanceGraph` (populated post-parse, read in `MethodEnvironment` at `IEnvironment.cs:103` via `(methodDecl.ModuleDecl as ModuleDecl)?.ConformanceGraph`)

Two clarifications from review:
- Property must be `set;`, not `init;`. The parser returns an already-constructed `ModuleDecl`; `Program.cs` then assigns and *later merges* underscore-suppressed names at `:313`. `init` would block the merge.
- The `internalTypeNames` parameter on `SwiftWrapperCompiler.Process` and the wrapper-cache save/load paths must continue to receive the set independently — even after Step 1, the post-processor still operates and needs its existing wiring. Plumbing onto `ModuleDecl` is *additive*, not a replacement.

### Finding 2 — `CallConvSwift` fallback is unsafe as a default policy

When `WrapperValidation.DetermineMethodWrapperDecision` returns `CannotWrap`, the method *is* still emitted on the C# side, with `CallConvSwift` as its calling convention (`WrapperValidation.GetCallingConvention` at `:110–120` returns `CallConvSwift` for any method without `UsesCdecl*` or `UsesNativeThunk`). For a wide set of method shapes, this guarantees a runtime crash. The per-shape ABI-safety logic lives in `WrapperValidation.RequiresCdeclForAbiSafety` (method overload at `:1382`, property overload at `:1492`):

| Shape | Why CallConvSwift fails | Documented at |
|---|---|---|
| Non-frozen struct instance members | C# projects them as `ClassWithOpaquePayload` (IntPtr/SafeHandle); Swift expects `SwiftSelf<T>` by value in registers | `WrapperValidation.cs:1448–1452` |
| Class static methods | Hidden `@thick Self.Type` metatype register that C# never populates | `WrapperValidation.cs:1433–1443` |
| Non-final class instance methods | Indirect dispatch via vtable; ABI mismatch | `WrapperValidation.cs:1382` (method overload) |
| Class instance properties (any kind) | Same indirect-dispatch issue | `WrapperValidation.cs:1492` (property overload) |
| Constructors (any kind) | `SwiftIndirectResult` + hidden metatype register both mishandled by Mono `jit-info.c:918` | `WrapperValidation.cs:1418–1431` |
| Many unsafe parameter / return shapes | Per the per-shape classification logic | `WrapperValidation.cs:1382` / `:1492` |

There **are** shapes where `CallConvSwift` is safe — final class instance methods with ABI-safe params/returns, module/free functions with primitive args, etc. (see `WrapperValidation.cs:1439`). But classifying every emitted method per-shape and selectively choosing fallback-vs-skip is brittle: it's exactly the trap that caused the prior 4-library regression. The plan stays **conservative**: skip every method whose signature reaches `InternalTypeNames`, accepting that some `CallConvSwift`-safe shapes are dropped in the bargain.

**Trade-off acknowledgement:** Today, the post-processor strips a method whose *wrapper* is broken; some such methods are nonetheless callable via `CallConvSwift` against the original mangled symbol. The post-processor doesn't try; we won't either. Expanding callable surface via per-shape ABI classification is deferred to a separate workstream ("expand wrapper-fallback coverage").

### Finding 3 — Today's post-processor strips both sides; the replacement must too

`SwiftWrapperPostProcessor.Pattern2_SilgenOrCdeclBroken` at `SwiftWrapperPostProcessor.cs:117–139` strips the entire Swift `@_cdecl`/`@_silgen_name` block plus its dangling preamble via `RemoveTrailingWrapperPreamble` (`:365–398`). Symbol names are surfaced as `PostProcessingResult.StrippedSymbols` (`:20`) and fed to `CSharpWrapperCoGater.Process` (`CSharpWrapperCoGater.cs:59`), which removes the C# P/Invoke + 3-level transitive caller closure. The public C# method disappears entirely.

For an emission-time replacement to match today's behavior, it must skip both the Swift wrapper *and* the C# member. The right insertion point is **`MemberValidationPipeline.ValidateMethodEmission`** (called from `IHandler.cs:202`), **not** `WrapperValidation.CanEmitMember`:

- `MemberValidationPipeline.ValidateMethodEmission` runs before dedup and handler dispatch — returning a "skip emission" verdict suppresses the entire C# member.
- `WrapperValidation.CanEmitMember` (`:148`) only feeds wrapper-eligibility consumers (`MethodWrapperEmitter`, `ConstructorWrapperEmitter`, `PropertyWrapperEmitter`, `SubscriptWrapperEmitter`). Returning false there *only* refuses the wrapper — the C# binding still emits and falls through to `CallConvSwift`, which is the unsafe path from Finding 2.

The recon's earlier draft pointed at `CanEmitMember`; that target is wrong for the goal. Properties have a separate entry — `MemberValidationPipeline.ValidatePropertyEmission`, called from `PropertyHandler` — and subscripts currently have a no-op pipeline entry that needs a real implementation as part of this work.

### Finding 4 — Why the prior 4-library regression happened

`cbe528ca`'s commit message confirms: a previous emission-time gate keyed on "signature reaches `InternalTypeNames`" regressed CryptoSwift, SkeletonView, NVActivityIndicatorView, and XMLCoder because `@usableFromInline internal` types are still emitted as public C# classes — suppressing their members or properties broke interface conformance. Current code reinforces the lesson: `IHandler.cs:220` explicitly avoids suppressing `IsModuleInternal` types, with a comment noting `@usableFromInline` types may need bindings.

The takeaway: the gate must be "skip *methods/accessors/subscripts* whose signature reaches a name in `InternalTypeNames`." It must **not** be "skip the type" or "skip every member of an internal type." The distinction is what the prior attempt got wrong.

This isn't fully reproducible from git alone — the prior attempt may have been local-only — but the commit text and current code both support the hypothesis. Worth a focused diff dive at implementation time before flipping the new gate.

### Finding 5 — No clean reusable signature-walk helper exists; we have to build one

The post-processor's "signature reaches internal" detector is regex-on-block-text, scanning emitted Swift wrapper source for token matches against `InternalTypeNames` (`SwiftWrapperPostProcessor.cs:115`, `:261`, `:421`). It's tangled with line ranges, block extraction, symbol collection, and preamble cleanup — not extractable as a clean unit.

`ValidationRuleSet.ReferencesInternalModuleType` (`:181`) is closer to what we want, but it uses different criteria ("same module, absent from `TypeDatabase`"), not the explicit `InternalTypeNames` set, and diverges from `ModuleHandler`'s older walker (`ModuleHandler.cs:1107`) on unqualified names and protocol lists.

We need a **new canonical TypeSpec walker**: given a `MethodDecl` (or property accessor `MethodDecl` / subscript `MethodDecl`) and an `InternalTypeNames` set, returns true if any parameter, return type, generic argument, optional/tuple/closure inner type, or generic constraint resolves to a name in the set. The walker lives alongside `MemberValidationPipeline` and is unit-tested independently.

This is its own subtask (Step 2 below) — not a refactor. Plan accordingly.

### Finding 6 — Pattern 2 catches body-reference cases the emission-time gate won't

The post-processor strips when the wrapper *body text* mentions an internal name, regardless of whether the *signature* reaches one. A wrapper body can reference internal types via:
- Parent type for vtable dispatch
- Static dispatch protocol witness
- Metadata / type-reconstruction helpers
- Other internal helper symbols emitted as part of the wrapper body

A signature-only emission-time gate misses these. Acceptable, because: (a) they're far less common than the dominant signature case, and (b) the post-processor stays in place as a safety net. Pattern 2 hit count drops to near-zero with the emission-time gate handling the dominant 99% case; the residue is the body-reference cases, which the post-processor continues to catch.

The implication: **don't retire Pattern 2 as part of this workstream.** Retirement becomes a separate followup once the residue is enumerated and either gated at emission (by extending the walker to predict body emission) or formally accepted as post-processing scope.

### Finding 7 — Pattern 2 also catches `NSInvocation` and safety-net sub-causes

Per `cbe528ca`'s sub-cause histogram, `Pattern2.InternalType` is ~99% of hits, but Pattern 2 also catches at least one `NSInvocation` shape and other safety-net cases. These are not internal-type-signature-reach problems and are not addressed by the new gate. Same conclusion as Finding 6: leave Pattern 2 in place.

## Implementation Plan

### Step 1 — Plumb `InternalTypeNames` onto `ModuleDecl`

- Add `HashSet<string>? InternalTypeNames { get; set; }` (matching `ExportedSymbols`'s mutability — *not* `init;`) to `ModuleDecl`.
- In `Program.cs:271`, do **not** simply replace the local — keep both in sync so the existing post-processor/cache flow continues to receive the set:
  ```csharp
  decl.InternalTypeNames = CollectInternalTypeNames(decl);
  internalTypeNames = decl.InternalTypeNames;
  ```
- After the underscore-suppressed-name merge at `Program.cs:313`, keep both in sync again — re-assign `internalTypeNames = decl.InternalTypeNames` (or, equivalently, mutate the same `HashSet<string>` reference and ensure both names point at it). Whichever pattern is chosen, verify post-merge that `internalTypeNames` and `decl.InternalTypeNames` reference the same final set before flow continues.
- **Do not remove** the `internalTypeNames` parameter from `SwiftWrapperCompiler.Process` or the wrapper-cache save/load paths. The post-processor stays in place per Findings 6 and 7; it still needs its existing wiring. Plumbing onto `ModuleDecl` is additive, not a replacement.

### Step 2 — Build the canonical TypeSpec walker

- New file: `MemberValidation/InternalTypeReferenceWalker.cs` (or similar; locate alongside `MemberValidationPipeline`).
- Public surface: `bool SignatureReachesInternalType(MethodDecl method, IReadOnlySet<string> internalTypeNames, string currentModuleName)` — handles parameters, return type, generic args, optional wrappers, tuple element types, closure parameters/returns, and generic constraints recursively.
- **Matching semantics (critical):**
  - If a `NamedTypeSpec` is **module-qualified**, match the module-qualified name *first*. Only that exact qualified form counts.
  - If unqualified or qualified to the current module, fall back to a short-name match against `InternalTypeNames`.
  - **Never** short-name-match a type that's qualified to a *different* module — that would falsely match a public cross-module type whose short name happens to collide with an internal name in the current module. This is the matching trap Codex flagged.
- Unit tests in `src/Swift.Bindings/tests/`: cover bare reference, nested generic arg, optional wrapper, tuple element, closure parameter, closure return, generic constraint, deeply nested combinations, the negative case (signature *doesn't* reach internal — must return false), AND **the cross-module name-collision negative case** (a public type in module Y with the same short name as an internal type in module X must not match when walking module X's methods).
- Reference patterns: `ValidationRuleSet.ReferencesInternalModuleType` (`:181`) and `ModuleHandler` (`:1107`) for prior art on TypeSpec traversal — borrow shape, but use the explicit `InternalTypeNames` set as the input criterion.

### Step 3 — Insert the gate in `MemberValidationPipeline`

- **Decide on `SkipReason` first.** The new gate either gets a new `SkipReason` value (e.g. `Pattern2InternalTypeReach`) or reuses an existing one. If new: update the `SkipReason` enum, report descriptions, recommendations, and the related test fixtures that pin SkipReason coverage. If reusing existing: pick the value deliberately and document the choice. Don't leave this implicit.
- Extend `MemberValidationPipeline.ValidateMethodEmission` (called from `IHandler.cs:202`): if `SignatureReachesInternalType(method, method.ModuleDecl.InternalTypeNames, method.ModuleDecl.Name)` is true, return a skip verdict with the chosen `SkipReason`. Methods are skipped before dedup/handler dispatch.
- Extend `MemberValidationPipeline.ValidatePropertyEmission` (called from `PropertyHandler`): same check against the property's accessor `MethodDecl`(s).
- **Subscripts — three concrete subtasks:**
  1. `ValidateSubscriptEmission` is currently a no-op. Implement it: walk getter/setter `MethodDecl`s through `SignatureReachesInternalType`.
  2. `SubscriptHandler` does **not currently call** `ValidateSubscriptEmission`. Add the call near the top of the subscript loop, before projection/dedup. Confirm the verdict is honored.
  3. **Protocol subscripts** flow through `MemberGateEvaluator`, not `MemberValidationPipeline`. Decide whether protocol-side subscripts can hit Pattern 2 in current validation libraries (inspect post-processor stripped-symbol logs, or grep). If yes, add a parallel gate in `MemberGateEvaluator`. If no, document the deferral with a rationale.
     - **Resolution (Session 1):** added `S6` to `MemberGateEvaluator.EvaluateSubscript` for symmetry with the `EvaluateHardGates` (method) and `EvaluatePropertyHardGates` (property) internal-type checks. Without it, a protocol whose subscript signature reaches an `@usableFromInline internal` type would still emit on the protocol-interface side, and concrete conformers would skip it (via `MemberValidationPipeline.ValidateSubscriptEmission`) — leaving a CS0535 mismatch. Cheap, symmetric, no separate validation-library evidence needed.
- Add a telemetry counter for "skipped due to internal-type signature reach" so we can spot regressions in future validation runs (parallel to today's Pattern 2 hit count).

### Step 4 — BindingTests fixture

- **First, verify the actual XMLCoder / CryptoSwift / SkeletonView / NVActivityIndicatorView source pattern** before writing the fixture. Swift's visibility rules generally forbid a public method from exposing an internal type in its public signature *unless* the method is `@inlinable` (or `@_alwaysEmitIntoClient`, etc.) — which is exactly the case `@usableFromInline internal` exists to support. Pull one or two real instances from the validation libraries' source and mirror that shape exactly.
- Likely shape: `@inlinable public func` with parameters or a return type referencing a `@usableFromInline internal` type. Or: a `@frozen public struct` with a stored property of `@usableFromInline internal` type. Confirm the fixture compiles standalone with `swiftc` before integrating into BindingTests.
- Locate the fixture in `BindingTests/Sources/SwiftBindingsTestLib/Internal/` (create dir if absent).
- Confirm:
  - The public type itself still emits and is constructible from C#.
  - The `@usableFromInline internal`-touching method silently omits from the C# binding (no compile error, no runtime crash, just absent).
  - Sim and device pass counts stay at baseline.
- Add a *negative* fixture: a public type whose method signature does *not* reach an internal type, confirming the gate doesn't over-strip.

### Step 5 — Validate against the 4 known-risk libraries

- Run `nuke validate --filter CryptoSwift,SkeletonView,NVActivityIndicatorView,XMLCoder`.
- All four must compile clean. If any `cs_compile` or `swift_compile` count drops, **stop and diagnose**. Likely causes: (a) the walker is over-matching (catching a public type via a name collision); (b) the gate is suppressing a member needed for an interface conformance; (c) the `InternalTypeNames` set itself contains a false positive. Don't loosen the assertion to make it green — diagnose root cause.

### Step 6 — Run full validation; confirm Pattern 2 drops to near-zero

- **Prerequisite — restore Pattern 2 sub-cause classification telemetry.** Current `SwiftWrapperPostProcessor` does not expose Pattern 2 sub-cause counters in code-readable form (it returns total stripped blocks/symbols only). Before Step 6 can validate "hit count drops to documented residue," add a sub-cause classifier to the post-processor: for each stripped block, classify it as `InternalType` / `NSInvocation` / `Other` based on inspection of the block content, and surface the per-sub-cause counts. Without this, we can't distinguish "the new gate caught the dominant case" from "the gate missed everything but happened to reduce total strips for some unrelated reason."
- `nuke validate` full sweep. The `InternalType` sub-cause count should drop to near-zero. The `NSInvocation` and `Other` sub-cause counts should stay roughly constant. The new emission-time counter should rise to approximately the previous `InternalType` count.
- Inventory the residue: enumerate every remaining `InternalType` strip and classify it (body-reference / signature-reach-the-walker-missed / unexpected). Document in a follow-up doc.
- If unexpected residue surfaces (signature-reach cases the new walker missed), tighten the walker before declaring the work done.
- **Pattern 2 post-processor stays in place.** Removal is a separate followup, gated on the residue being either fully migrated to emission-time gates or formally accepted as post-processing scope.

### Step 7 — Final gates

- `nuke compile`, `nuke test`, `nuke validate` (baseline-clean), `nuke binding-tests` (default sim run), `nuke binding-tests --device` (NativeAOT path — the new gate affects emission, which can change calling-convention selection downstream).
- Zero-regression check against `.validation-baseline.json`, BindingTests pass count, and unit test pass count per CLAUDE.md.

## Open Questions / Risks

1. **Telemetry continuity.** New emission-time counter ("skipped due to internal-type signature reach") parallels today's Pattern 2 sub-cause counter. Both run side-by-side after this work; the emission counter should monotonically increase as the post-processor's count decreases. Discrepancies are diagnostic signal.

2. **Walker false positives via name collision.** `InternalTypeNames` carries short names *and* module-qualified names, with collision dedup against public types in `Program.cs`. Per the matching rule in Step 2: module-qualified exact match first; short-name fallback **only** for unqualified or current-module references. Never short-name-match a type qualified to a different module — that would falsely pull in a public cross-module type whose short name collides with an internal name in the current module. Unit tests must cover this exact collision shape.

3. **`MemberValidationPipeline` subscript path is currently no-op.** Implementing it is part of this work, not optional. Subscripts in real validation libraries do hit Pattern 2; confirm by inspecting the post-processor's stripped-symbol log post-Step 6.

4. **Retirement of Pattern 2 itself is deferred.** This is intentional — see Findings 6 and 7. Don't smuggle retirement into this workstream because the hit count looks low after Step 6.

5. **Save/load paths for wrapper cache.** `SwiftWrapperCompiler` has compile-wrapper-only paths that serialize the `internalTypeNames` set. Verify those paths still receive the set correctly after Step 1. This is plumbing-only — the set still flows through the same channel — but worth a focused check.

## Out of Scope

- **Full Pattern 2 retirement.** Deferred to a separate followup once the body-reference and `NSInvocation` residue is enumerated and either gated at emission or accepted as post-processing scope.
- **Expanding callable API surface via `CallConvSwift` fallback.** Some methods stripped today might be safely callable via `CallConvSwift` against the original mangled symbol (final class instance methods with ABI-safe params/returns; module/free functions with primitive args). Per-shape ABI classification is brittle and out of scope here. Tracked separately as "expand wrapper-fallback coverage."
- **Retiring other post-processor patterns.** Patterns 1, 3, 4 (and any successors) remain; their retirement is independent work.

## Success Criteria

- `InternalTypeNames` is reachable from emitters via `methodDecl.ModuleDecl.InternalTypeNames`.
- `MemberValidationPipeline.ValidateMethodEmission` / `ValidatePropertyEmission` (and the new subscript path) skip C# emission when the method/property/subscript signature reaches an internal type name.
- `SwiftWrapperPostProcessor.Pattern2_SilgenOrCdeclBroken` hit count drops to a small, documented residue (body-reference + `NSInvocation` cases). Post-processor remains in place.
- The 4 risk libraries (CryptoSwift / SkeletonView / NVActivityIndicatorView / XMLCoder) compile clean against `nuke validate`.
- `.validation-baseline.json` `cs_compile` + `swift_compile` counts ≥ baseline.
- BindingTests pass count ≥ baseline on both sim (Mono JIT) and device (NativeAOT).
- New BindingTests fixture covers the `@usableFromInline internal` signature-reach shape end-to-end (positive + negative).
- New emission-time telemetry counter exists and is exercised by validation runs.
- Plan docs the conservatism: "we skip some `CallConvSwift`-safe shapes; expansion is a separate workstream."
