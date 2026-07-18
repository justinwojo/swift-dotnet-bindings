All grounding complete. Here is the design critique and proposal.

---

# Design Critique: Structural Binding Resilience for the Swift→C# Generator

## 0. Summary of position

The brief's diagnosis is correct — the residual whole-binding failures all stem from *prediction without verification*. But the proposed spine picks the wrong **recovery primitive**. Three of its five elements (provenance map, per-member transactions, strip-on-failure loops reusing the PostProcessor/Reconciler) are built around **post-hoc surgery on emitted artifacts**, and this codebase is precisely the kind of system where artifact surgery cannot be made sound: member existence feeds *upstream* decisions (collision-suffix naming, projected-key dedup, vtable slot layout, interface fillability, adopted-name reservation — all documented as load-bearing, multi-site invariants in the project's own constraint rules). Text-level deletion after the fact leaves those decisions stale in ways no strip pass can repair.

The primitive that *is* sound here — because the generator is deterministic and the TypeDatabase is frozen after load (Finding 47, `Program.cs:500`) — is **regenerate-with-denylist**: attribute the failure to a logical decl, add it to an exclusion list, and re-run emission so the tombstone flows through the *existing* skip machinery (`MemberValidationPipeline`, `UnsupportedCommentEmitter`, `ReportCollector`, vtable fillability, dedup keys), which already knows how to produce a consistent binding around an absent member. That is the architecture I argue for below: **predict → verify → attribute → regenerate**, with compiler-as-oracle for *compilability* and the hand-coded gate catalog retained as the sole authority for *soundness* (ABI-wrong-but-compiles), which no compiler oracle can detect.

---

## 1. Where the proposed spine is right, wrong, incomplete

### Right

- **Verify-on-failure-only.** Correct latency posture. The wrapper is already compiled once per generation; adding a Roslyn probe only on the C# side's failure path (or even always — see §5, it's cheap in-process) costs healthy bindings ~nothing.
- **Acting on `AbiContractChecker`.** Its result is genuinely discarded at `ModuleEmitter.cs:132` (`AbiContractChecker.Validate(wholeOutput, …)` — return value dropped, warn-only). This is the single cheapest soundness win in the whole program: the checker already detects CC-001..004 (non-blittable CallConvSwift params/returns, wrapper entry points targeting the wrong library, Cdecl-on-mangled-symbol) with structured `AbiCheckViolation` records including `EntryPoint` — a ready-made attribution key. It must become load-bearing.
- **Degradation report as product surface with owner classification.** Right, and cheap — `SkipReason`, `SkipTriage.cs`, `WorkaroundRecommendations.cs`, and the `api-surface.md` emitter already exist; this is classification plumbing, not new architecture.
- **The observation that prediction is forever incomplete.** The 864-line `MemberValidationPipeline` plus `TypeSkipConditions` plus `WrapperValidation` plus per-handler inline gates is an impressive but unmistakably open-ended catalog. Every gate in it is a fossilized postmortem. The unmodeled tail is structural, not a bug count that converges to zero.

### Wrong

**W1. "Reuse PostProcessor + Reconciler" as the recovery actuator.** The `StrippedSymbolCSharpReconciler` is 2,437 lines of regex-based text surgery computing a 3-level transitive closure (P/Invoke → caller → property forwarder) with hand-built scope maps, ambiguity bail-outs, and exemption lists — and its own doc comment calls it a **"7b liability … dead the day that leg is retired and must not masquerade as live architecture"** (`StrippedSymbolCSharpReconciler.cs:16-22`). Building the new resilience spine on it doubles down on the component the codebase has already sentenced. Worse, it is **currently unsound**: Step A3 *exempts* P/Invokes whose public caller implements an interface member (`StrippedSymbolCSharpReconciler.cs:101-110`), to avoid CS0535. An exempted member keeps its P/Invoke against a symbol that was just stripped from the wrapper → guaranteed `EntryPointNotFoundException` at runtime. That is exactly the class of compile-clean/runtime-broken outcome the hard constraint forbids, shipping today.

**W2. "Per-member emission transactions" as stated.** Generalizing the `WrapperSymbolContractGate` checkpoint pattern to arbitrary exceptions is not feasible as in-place rollback — see §4. The existing rollback works only because the contract exception is thrown *eagerly, before* the member's side-table registrations commit, and only the C# buffer needs unwinding. `SwiftWriter` has **no checkpoint at all** (`IndentedTextWriter.cs:129-139` — an empty subclass), `ModuleEmissionContext` carries on the order of a hundred mutable collections, and `ReportCollector` is ambient AsyncLocal static state. Transactional semantics are achievable — but via poison-and-regenerate, not rollback (§4).

**W3. Provenance map as a *span* map.** Member-level C# spans recorded during emission are invalidated *before the file is even written*: `QualifyNamespaceReferences` regex-rewrites the whole output post-emission (`ModuleEmitter.cs:126-137`), and the file-per-type splitter reslices it. Any span bookkeeping fights two existing text post-passes and every future one. The correct provenance key is not a position; it is the **symbol** — see §3.

### Incomplete

**I1. The spine doesn't address the biggest *current* soundness hole: SDK-mode wholesale wrapper failure.** When swiftc fails on an unrecognized construct, `CompileSlice` throws (`SwiftWrapperCompiler.cs:1971`), `EvaluateResult` maps it to Warning/Fatal, and `EffectiveOutcome` **downgrades Fatal→Warning in SDK mode with the explicit comment "methods referencing the wrapper get DllNotFoundException at runtime"** (`SwiftWrapperCompiler.cs:94-107`). That ships a compiling binding whose entire wrapper-backed surface crashes on first touch. Any resilience plan must treat "wrapper failed, C# ships anyway" as a soundness violation to close, not an accepted degraded mode.

**I2. No escalation ladder.** The spine says "strip the member" but not what happens when stripping a member is itself unsound or when attribution fails. A recovery system without a defined escalation ladder (member → member-cluster → conformance-stub → type-shell → module) degenerates into either infinite loops or silent unsoundness. §2 defines the ladder.

**I3. No fixpoint/no-progress semantics.** swiftc and Roslyn both emit *cascade* diagnostics: one genuinely broken decl produces secondary errors pointing into healthy code (ambiguity, overload resolution). A naive attribute-and-strip loop can strip healthy members forever. The loop needs: primary-diagnostic filtering, per-iteration culprit batching, a no-progress detector that escalates granularity instead of retrying, and a hard iteration cap.

**I4. The strip-then-reconcile channel and the predict channel produce *differently shaped* tombstones.** Today a predicted skip gets an `// Unsupported:` comment, a `SWIFTBIND0xx` row, correct dedup behavior, and correct vtable fillability; a post-strip reconciliation gets a text hole and a retrofitted report row. Two tombstone shapes = permanent report drift. The denylist-regen design collapses them into one channel by construction.

---

## 2. The soundness question: a concrete model for "safe to drop"

The crux, as the brief says. The model has three parts: an **ABI-role classification** of every emitted artifact, a **needs-closure** over the decl graph, and **independent post-hoc gates** that verify the result rather than trusting the strip logic.

### 2.1 ABI-role classification (what kind of thing is X?)

| Role | Examples | Droppable alone? | Why |
|---|---|---|---|
| **R1 Leaf callable** | method, ctor, operator, free function, property/subscript *as a unit* (both accessors together) | **Yes** — native-side always; managed-side subject to obligations (2.2) | Each has its own symbol; no other artifact's layout or ABI depends on its existence. This is why the existing per-member skip machinery is already sound. |
| **R2 Layout-bearing member** | stored field of a by-value frozen struct; enum case/payload; `Buffer` size contributors; `ExistentialContainerN` arity | **No — never.** Escalate to the artifact that *owns the layout* (the type) | Dropping changes size/offset of everything after it — the canonical compile-clean/ABI-corrupt outcome. The generator already encodes this instinct at type granularity (`TypeSkipConditionKind.IndeterminateBufferLayout`, `SubWordOptionalLayoutMismatch` fail closed on the whole type, `TypeSkipConditions.cs:204-229`). Generalize it into a rule: **recovery granularity = layout-ownership granularity.** |
| **R3 Conformance member** | protocol requirement impl, proxy receiver, EveryProtocol witness, vtable *fill* | **Yes, via stubbing** — never via deletion | Deleting breaks CS0535/consumer contracts; the vtable *slot* must survive regardless (the VtableLayout skip-but-consume invariant — layout axis is deliberately independent of fillability, and that separation is exactly the right soundness architecture, already built). Replace the body with a **loud throwing stub** (`throw new SwiftBindingUnavailableException("<reason>, see binding report")`) — the .NET-precedented `PlatformNotSupportedException` pattern. Sound: compiles, conforms, fails loudly and attributably at the exact call, corrupts nothing. This **replaces the Reconciler's unsound interface exemption** (W1). |
| **R4 Conformance as a whole** | `: IProtocol` on an emitted class + proxy | **Avoid removing; stub members instead** | Removal cascades through every existential marshal site that relies on the conformance — an open-ended closure. Stubbing is bounded. Conformance removal happens only inside R5 escalation. |
| **R5 Whole type** | class/struct/enum | **Yes** — escalation terminus for types | Already supported: zero-usable-member types become `[OpaqueSwiftType]` shells so references still resolve. Opaque-shell demotion is the sound "drop" for anything layout-bearing. |
| **R6 Wrapper-side helper** | `_SBW_P…` dispatch protocols, thunk funcs, Utf8Slice helpers | Only with their full referencing closure | Governed by the existing symbol-integrity direction check (`WrapperSymbolIntegrityGate`). |

### 2.2 The needs-closure (what else must go with X?)

"Safe to drop X" is never a per-artifact predicate; it is: **compute the downward closure of X under the `needs` relation on the *decl/plan* graph — not the emitted text — and drop/stub exactly that closure, tombstoning every element.** Edges in `needs`:

- C# public member → its P/Invoke(s) → wrapper symbol(s) (already reified: the SBW_/SBSW_ registry, `ModuleEmissionContext.IsWrapperSymbolRegistered`).
- Bridge/specialization emissions → their source member (CSM specializations, closure bridges, default-parameter overloads — these are `RoutedElsewhere` results in the pipeline, so the routing is already modeled).
- Interface requirement → {interface member, proxy receiver, witness dispatch, EveryProtocol member, vtable fill} — the co-drop set for R3, resolved by stubbing.
- Type shell demotion → all members + conformances of the type, plus demotion of member-signature references in *other* types (which the existing OpaqueSwiftType machinery + secondary gates already handle when the decision is made *pre-emission*).

This is the decisive argument for regeneration over surgery: computed pre-emission, the closure is exactly what the existing gate machinery consumes and every downstream invariant (sibling naming, dedup keys, adopted-name reservation, vtable fillability, silent-tombstone registry) is **recomputed by construction**. Computed post-emission over text, every one of those is stale.

### 2.3 The static guarantee: verify, don't trust

You do not statically *prove* the strip logic correct; you make the pipeline **fail closed on independent invariant gates** after every recovery iteration, and treat a gate failure as "escalate one rung," never "ship":

1. `WrapperSymbolIntegrityGate` (exists, already fail-closed) — no dangling P/Invoke.
2. Silent-tombstone divergence invariant (exists, throws).
3. `AbiContractChecker` — **promoted from warn-only to blocking**; its CC violations feed the denylist like compiler errors do.
4. ArtifactParityGate-style vtable width/field parity (exists per the constraints doc) — run on the recovered artifact set.
5. **New, small: layout-hash gate** — for every emitted by-value struct, recompute expected size/offsets from ABI-JSON facts and assert the emitted layout matches. This is the direct guard for R2, and it converts "we believe stripping never touched a layout" into a checked invariant.

**Escalation ladder** (each rung sound by construction): leaf member → overload/bridge cluster (its `needs` closure) → conformance-member stubbing → type → opaque type shell → module failure (today's behavior, now the floor of last resort instead of the default). A bounded ladder means the loop always terminates in a sound state.

### 2.4 The prediction/verification division of labor — the principled line

The compiler oracle can only certify **compilability**. It is *structurally blind* to the failures that matter most here: wrong-ABI-but-compiles (the Gate 4a async-generic-parent SIGSEGV documented at `MemberValidationPipeline.cs:334-358`, the inout-mismatch silent corruption at Gate 5c). Therefore:

- **Freeze growth** of gates whose only job is predicting *compile errors* (the Pattern-2 internal-reach family, extension-collision predictions). The verify-recover loop is their general replacement; existing ones stay as fast-path optimizations and documentation.
- **Keep hand-writing gates** for *soundness* conditions — ABI mismatch, layout indeterminacy, register-convention violations. These are the crown jewels; no backstop can replace them. This resolves the brief's "keep growing vs. freeze" question with a criterion instead of a preference: *a new gate is justified iff the failure it prevents would compile.*

---

## 3. Attribution: symbol-anchored provenance, recomputed per iteration; bisection only as bounded fallback

**Pick: hybrid, weighted heavily toward provenance — but the provenance key is the symbol, not a stored file:line map.**

The line-drift problem in the brief is self-inflicted by the "stored map" framing. Don't store positions. The artifacts are already (or can trivially be made) **self-describing**:

- **Wrapper side:** every strippable block either contains a `@_cdecl("SBW_…")`/`@_silgen_name("SBSW_…")` string — which is a globally unique, per-member, cross-artifact key already extracted by regex in three places (`SwiftWrapperPostProcessor.CdeclSymbolRegex:494`, `WrapperSymbolIntegrityGate.DefPattern:51`) — or it is a symbol-less block (extension headers, `_SBW_P…` protocol decls) which gets a one-line **anchor comment** `// SBW-ORIGIN: <DeclId>` emitted at block head (the emitters already write structured preamble comments the PostProcessor knows how to handle, `RemoveTrailingWrapperPreamble:446`). Attribution then is: parse swiftc diagnostics (use `-serialize-diagnostics` for structure rather than scraping stderr — the current stderr filter at `SwiftWrapperCompiler.cs:1930-1940` is preview-grade, not attribution-grade) → for each **primary** error at file:line, walk to the enclosing block with the *same* `StructuralBraceScanner`/`FindBlockEnd` machinery the PostProcessor already uses, **against the exact file that was compiled in this iteration** → read the block's symbol/anchor → symbol→DeclId via the wrapper-symbol registry. Because the map is recomputed from the compiled bytes each round, drift is definitionally impossible.
- **C# side:** in-process Roslyn probe (§5 on why it's worth the dependency). Roslyn hands you the syntax tree: diagnostic span → `AncestorsAndSelf().OfType<MemberDeclarationSyntax>()` → the member's `EntryPoint = "SBW_…"` literal (or an emitted `// SBW-ORIGIN:` doc-trivia anchor for members with no P/Invoke) → DeclId. No bookkeeping during emission at all; the `ApiManifestEntries` (C# signature → symbol, `ApiManifestEmitter.cs`) already demonstrates the mapping exists.
- **DeclId** is the one new piece of real infrastructure: a stable, serializable identity per logical decl (module-qualified parent + member name + label-inclusive signature hash — reuse `DeterministicHash8`/the mangled name where present). It is the denylist key, the report key, and the provenance value. Symbols already *almost* are this; DeclId just makes the identity independent of symbol-promotion details (`EmissionSymbol` vs `MangledName`, per the AF13 side-table rule).

**Bisection**'s role: strictly a **bounded fallback** for diagnostics that attribute to nothing (errors in shared prelude/helper code, linker errors, toolchain crashes with no location). And bisect over **decl exclusion sets with regeneration** — split the module's emittable decl set, regen half, compile — never over text halves (text bisection produces syntactically invalid intermediates and inherits every surgery problem from §1). Budget: ~8 compiles; exhausted budget escalates a rung (type, then module). Justification for not making bisection primary: per-probe cost is a full swiftc slice compile (the dominant cost, minutes-scale on large modules per the timeout machinery), while symbol attribution usually resolves *all* culprits from one failing compile's diagnostics — the expected iteration count for the provenance loop is 2–3 total compiles (initial + one regen + confirm), versus O(log n) *per culprit* for bisection.

**Cascade-error hygiene** (the part naive designs get wrong): attribute only primary diagnostics (serialized diagnostics distinguish notes); batch *all* attributed culprits from one compile into one denylist increment; detect no-progress (identical error fingerprint two rounds running, or a round attributing zero diagnostics) and escalate granularity instead of iterating.

---

## 4. Exception containment with shared mutable emitter state

**In-place per-member rollback is not feasible and should not be attempted.** The inventory of state a generic rollback would have to unwind:

- `CSharpWriter` — has checkpoints (`IndentedTextWriter.cs:70-95`). ✔
- `SwiftWriter` — **no checkpoint exists** (`:129-139`); wrapper text for a member is emitted interleaved with its C#. A member that throws mid-Swift-emit leaves a half-open block that breaks the *whole file's* brace structure — worse than the failure being contained.
- `ModuleEmissionContext` — ~112 mutable collections (wrapper-symbol registry, `ApiManifestEntries`, `TopLevelTypeSpans`, dedup key sets, promoted-symbol side table, thunk `AssemblyBuilder`s, suppressed-proxy sets, …). No snapshot machinery; adding one is a permanent tax on every future field.
- `ReportCollector` — ambient AsyncLocal static session.
- `PInvokeHelperContext.RawCodeBlocks`, closure-emitter dedup sets, `EmittedProjectedSignatures`, name-reservation sets — where a stale reservation from a rolled-back member silently shifts *sibling* collision suffixes (the exact drift class the adopted-name-reservation invariant exists to prevent).

The existing `WrapperSymbolContractGate` rollback (`MethodHandler.cs:704, 1107, 1722-1732`) works only because the exception is thrown *eagerly by design, before* the member's registrations commit, and only the C# buffer is dirty. That precondition does not hold for arbitrary exceptions.

**The feasible design: poison-and-regenerate (transaction = whole-module emission attempt; retry = with exclusions).**

1. Wrap per-member dispatch (`HandleBaseDecl` and the property/subscript equivalents) in a catch that: records `(DeclId, exception)` in a **poison list**, writes nothing, and continues with the next member. No state rollback is attempted — the current attempt's artifacts are now presumed dirty.
2. At end of module: if the poison list is empty, proceed as today. If non-empty, **discard the entire attempt's output and re-run emission once with the poison list as denylist** (a new `ModuleEmissionContext`, fresh `ReportCollector` session, fresh writers). The denylist enters as **Gate 0** of `MemberValidationPipeline` / `TypeSkipConditions` — a `ValidationResult.Skip(SkipReason.EmitterFault, …)` with the captured exception in the details — so the tombstone, report row, dedup behavior, and vtable fillability are all first-class.
3. Type-level and pre-pass exceptions poison the *type* (opaque-shell demotion on retry). A poisoned member on the retry pass (new exception surfaced by the changed context) extends the list; cap at 3 attempts, then module failure.

Why this is safe and cheap: parse + TypeDatabase construction dominate cold cost and are unaffected (frozen registry, Finding 47); emission is a pure function of (frozen DB, decl tree, denylist) → re-emission is seconds. Determinism is the load-bearing assumption — it is already effectively asserted by the project's "bake-verified byte-identical" refactor discipline, but Stage 0 should add an explicit double-emit determinism test to pin it.

This same mechanism *systemically* contains the `SwiftTypeName.FromModuleQualifiedName` class of abort (30+ throwing call sites vs. ~2 `Try*` sites; a `τ_0_0.…` name reaching any of them is today a module abort via `Program.cs:751`). Migrating hot call sites to `TryFromModuleQualifiedName` (whose doc comment at `SwiftTypeName.cs:62-77` already articulates exactly the right philosophy — "degrade to a reasoned skip") remains worthwhile as fast-path hygiene, but the poison loop is the structural guarantee that the next unanticipated throw-site doesn't sink the module.

**One prerequisite regardless:** give `SwiftWriter` the same checkpoint API (move it to `IndentedTextWriter`) so the *existing* contract-gate rollback can also unwind the Swift side; today a rolled-back C# member can leave an orphaned wrapper block (dead but compiled, or worse, uncompilable — precisely a Family-A shape).

## 5. Is there a fundamentally better architecture?

Evaluating the brief's alternatives honestly:

- **Emit-everything-then-tree-shake:** strictly worse. It maximizes reliance on sound post-hoc removal — the thing §1/§2 show is only achievable via regeneration anyway — and abandons the gate catalog's soundness knowledge (the compiler will happily *keep* the wrong-ABI async-generic member that Gate 4a exists to kill).
- **Probe-build / maximal-compiling-subset discovery (ddmin-style):** the compiler-as-only-oracle fallacy. "Maximal compiling subset" is not the target — *maximal sound subset* is, and soundness is invisible to swiftc/Roslyn. Also unbounded compile cost, and "maximal" is not even well-defined (multiple maximal subsets exist when members conflict, e.g. extension-collision pairs).
- **Compiler-as-oracle from the start (always-probe):** right idea on the C# side only, wrong as an architecture. An in-process Roslyn `CSharpCompilation.GetDiagnostics()` over the generated sources with the binding's reference set is fast enough (~1–3s for typical modules) to run *unconditionally*, converting "csproj fails downstream on the user's machine" into "generator-detected, attributed, recovered." Worth the dependency: structured spans, semantic model for free, no `dotnet build` shell-out, and it is the retirement path for the 2,437-line regex Reconciler (its transitive-closure computation becomes a semantic-model reference walk). swiftc, by contrast, cannot be in-process and stays failure-triggered.

**The best architecture is the spine's *shape* with the recovery primitive replaced: closed-loop regeneration.**

```
parse → TypeDatabase (frozen)
  ┌────────────────────────────────────────────┐
  │ EMIT (denylist D, initially ∅)             │
  │   gates predict; poison-catch contains     │──poisoned──► D += poison; re-emit
  │ VERIFY                                     │
  │   wrapper: swiftc (existing compile)       │──errors──► attribute (symbols/anchors)
  │   C#: in-process Roslyn probe (always)     │──errors──► attribute (Roslyn tree)
  │   gates: AbiContract (now blocking),       │
  │     SymbolIntegrity, layout-hash, parity   │──violation─► attribute
  │ RECOVER: D += needs-closure(culprits)      │
  │   per soundness model §2; escalate ladder  │
  │   on no-progress; cap iterations           │
  └────────────────────────────────────────────┘
      clean → ship binding + degradation report (every D entry: DeclId,
              reason, owner class, workaround)
```

Everything is one skip channel; the report is truthful by construction; the PostProcessor pre-strip and the Reconciler become transitional legacy retired in Stage 3–4. Healthy bindings pay one Roslyn probe (~seconds). Pathological libraries pay k re-emissions (seconds each) + k swiftc compiles (the real cost, capped).

## 6. Staged implementation path

**Stage 0 — close live soundness holes + pin assumptions (days; maximal risk-reduction per line):**
1. Make `AbiContractChecker`'s result load-bearing at `ModuleEmitter.cs:132` (violation → member skip via existing predict-then-skip machinery at minimum).
2. Fix the SDK-mode wholesale-wrapper-failure hole (`SwiftWrapperCompiler.cs:94-107`): wrapper failed → either fail the binding or (later, once stubs exist) ship throwing stubs; never a silent DllNotFound surface. Same for the Reconciler's interface-exemption (Step A3): exempted member bodies become throwing stubs, not kept dangling P/Invokes.
3. Determinism pin: double-emit byte-identity test (the regen loop's foundational assumption).
4. `Checkpoint`/`RollbackTo` moved to `IndentedTextWriter` so `SwiftWriter` gets it.

**Stage 1 — denylist + identity (foundational):** DeclId; denylist as Gate 0 in `MemberValidationPipeline`/`TypeSkipConditions`; `SkipReason.EmitterFault`/`SkipReason.VerificationFailure`; owner classification fields in the report. Then the **poison-and-regenerate exception container** (§4) — this alone eliminates abort-path #1 (the `Program.cs:751` catch becomes truly last-resort).

**Stage 2 — wrapper verify loop (kills Family A):** `-serialize-diagnostics` parsing; symbol/anchor attribution; needs-closure into denylist; re-emit + recompile; no-progress escalation ladder; iteration cap. The pre-compile PostProcessor strip stays as a fast-path first iteration.

**Stage 3 — C# probe loop (kills Family B):** in-process Roslyn probe (always-on), Roslyn-tree attribution, same loop; begin retiring `StrippedSymbolCSharpReconciler` (Reconciler runs only when the legacy strip leg fires; loop is primary).

**Stage 4 — hardening + product surface:** layout-hash gate; throwing-stub emitter for R3; corpus soak with a "zero whole-binding failures" ratchet; gate-catalog freeze policy (§2.4 criterion); degradation report shipped with owner classes + workarounds.

Can wait: bisection fallback (Stage 2 can escalate straight to type/module until it exists); retiring the PostProcessor entirely; Try-migration of all 30 `FromModuleQualifiedName` sites (poison loop covers them structurally).

---

### Critical Files for Implementation

- /Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs — Gate 0 denylist entry point; the single skip channel all recovery must flow through
- /Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs — swiftc invocation/failure path (`:1890-1973`), pre-strip integration (`:227`), outcome policy (`:74-107`) — home of the wrapper verify loop
- /Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs — discarded `AbiContractChecker` result (`:132`), post-emission text passes, the emission entry the regen loop re-invokes
- /Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Program.cs — top-level catch (`:751`), generation orchestration where the closed loop and poison-retry live
- /Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs — the unsound interface-exemption to fix in Stage 0 and the component the Roslyn loop retires