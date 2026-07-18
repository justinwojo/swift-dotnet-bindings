# Session 08 — Swift diagnostic attribution library

Built a tested, **language-neutral** attribution library: a failed wrapper compile → the set of
root `(DeclId, RecoveryUnit)` culprits, with cascade hygiene and a no-progress detector. No loop
wiring (that is session 09); the library stays language-neutral so wave-2 can reuse it for Roslyn
C# attribution.

**Commit:** the tip of `main` carrying this summary (`git log -1`). Includes the
`build/baselines/validation-baseline.json` ratchet
(`swift_bindings_unit_pass_floor` 15080 → 15141, the real deterministic pass count).

## Capture format: structured stderr parse (not `.dia`)

Chosen deliberately. Under `-emit-library` swiftc writes **no** `.dia` without a per-primary output
map, and its serialized-diagnostics bitstream has **no** managed reader — a native dependency the
generator would have to grow. swiftc's textual diagnostics already carry every field attribution
needs (`file:line:col: severity: message`, with a distinct `note:` severity for follow-ons), so the
parser reads the text stream and preserves raw stderr. `-serialize-diagnostics-path` was tried and
rejected by swiftc under our compile invocation. Recorded in `SwiftDiagnosticParser` remarks.

## Library shape (`src/Swift.Bindings/src/Diagnostics/Attribution/`, namespace `BindingsGeneration.Diagnostics`)

- `CompilerDiagnostic.cs` — `DiagnosticSeverity`, `CompilerDiagnostic` (file, line, UTF-8 byte column,
  severity, message; `Global(...)` for positionless), `DiagnosticGroup` (primary + notes).
- `SwiftDiagnosticParser.cs` — real-stderr → `DiagnosticGroup[]`: positioned primaries, gutter/caret
  continuation lines dropped structurally, notes attach to the current primary, undefined-symbol
  linker blocks folded into one positionless error, tool-prefixed **and** bare driver diagnostics
  captured as global errors.
- `WrapperBlockIndex.cs` — maps a diagnostic line → innermost strippable wrapper block
  (`@_cdecl`/`@_silgen_name` symbol or `// SBW-ORIGIN:` anchor), brace/comment-aware via the shared
  `StructuralBraceScanner` (same block delimiting as the post-processor).
- `AttributionModel.cs` — `AttributionKind`, `ProvenanceSource`, `ProvenanceHit`, `IProvenanceStep`,
  `AttributedDiagnostic`, `AttributionResult` (diagnostics + culprits + fingerprint).
- `ProvenanceSteps.cs` — the priority-ordered steps: `IntervalMapProvenanceStep` (session-06 interval
  map, UTF-8 byte column via `TryResolveUtf8Column`), `SymbolAnchorProvenanceStep` (symbol → else
  `// SBW-ORIGIN:` anchor), `LinkerSymbolProvenanceStep` (positionless linker undefined-symbol match).
- `DiagnosticAttributor.cs` — classification precedes provenance; resolves each error primary through
  the steps in priority order; errors-only cascade collapse (many errors in one unit → one culprit,
  one denylist increment); missing-module → `CauseOwner.InputConfiguration`, never a culprit.
- `DiagnosticFingerprint.cs` — the fingerprint + `NoProgressDetector`.

**Priority order (as delivered):** (1) session-06 interval map → (2) `@_cdecl`/`@_silgen_name` symbol
anchor → (3) `// SBW-ORIGIN:` anchor comment → (4) linker undefined-symbol match → (5) classification
of global failures (missing-module → `InputConfiguration`, never attributed to the last source line).

## Fingerprint definition

8-hex FNV-1a (`EmitterUtility.DeterministicHash8`) over the newline-joined, **sorted multiset** of
normalized error-message tokens `"{count}×{message}"`. `Normalize` elides absolute paths
(`/…` → `<path>`) and collapses whitespace. **Errors only** (warnings/notes excluded).
**Position-independent** — no file/line/col enters, so a pure re-render at different positions is
unchanged, while withdrawing one of two same-message culprits drops its count (2→1) and changes the
fingerprint (real progress). `NoProgressDetector.ShouldEscalate` fires on either the same fingerprint
two rounds running **or** a round that found errors but attributed none to a unit.

## Fixtures (real captured swiftc 6.2.4 output, genericized names)

`tests/UnitTests/Diagnostics/Fixtures/*.wrapper.swift` + `*.stderr.txt`, each captured once by
compiling the sibling wrapper source with `swiftc -emit-library -parse-as-library`:

| Fixture | Shape | Attribution result |
|---|---|---|
| `SingleBrokenMember` | one broken member (`SBW_Gadget_rotate`), one clean | 1 culprit, the broken unit only |
| `CascadeInOneMember` | 4 errors all in `SBW_Timer_fire` | collapses to 1 culprit (one denylist increment) |
| `TwoBrokenMembers` | two independent broken members, one clean | 2 distinct culprits, clean one absent |
| `MissingModule` | `no such module '…'` | classified `InputConfiguration`, 0 culprits |

**Per-fixture attribution accuracy: 4/4 exact** (correct-unit, cascade collapse, module-level
classification), pinned by `DiagnosticAttributorTests`. No-progress firing pinned by
`DiagnosticFingerprintTests`.

## Tests (all additive)

34 tests across `SwiftDiagnosticParserTests` (9), `WrapperBlockIndexTests` (4),
`DiagnosticAttributorTests` (11), `DiagnosticFingerprintTests` (10). All from recorded real failures.

## Deviation — deliverable #4 (anchor-comment EMISSION) scoped to session 09

The attribution library's `// SBW-ORIGIN:` **consumer** path is complete and fully tested
(`SymbolAnchorStep_WithNoSymbolButAnOriginAnchor_…`, `WrapperBlockIndex` origin-anchor tests). The
generator-side **emission** of the anchors is deferred to session 09, with rationale:

- The valuable strippable blocks (dispatch protocols `_SBW_P…`, conformance extensions, extension
  headers — post-processor Patterns 1/3/3c) are emitted from 7+ scattered **static** helpers
  (`GenericProtocolEmitter.EmitProtocolAndConformance`, `EveryProtocolEmitter`, `SwiftBuilder`,
  `DefaultParameterOverloadEmitter`, `ArraySliceNormalizationEmitter`, `MethodClosureBridge`,
  `ModuleHandler` shared-helper path) that **do not carry the owning `ArtifactId`**. Wiring anchors
  (or the cleaner fragment-owner scoping via the purpose-built but unused `FragmentOwners.ForSharedHelper`/
  `ForDeclWrapper`) means threading identity through each site — a wrapper-**output** change that
  must reconcile with the post-processor's `RemoveTrailingWrapperPreamble` anchor-adjacency caveat
  and wants its own focused sim + validate verification.
- These blocks are **already tiled by the interval map (priority 1)** under the module-root owner, so
  attribution degrades gracefully to module-level rather than failing.
- A partial emission (only the always-present shared-helper bundles, the one clean choke point) would
  pay the full risk surface for the **least-valuable** target (those bundles are rarely stripped).

Deferring emission means **zero added anchor lines → byte-identity fully preserved**; the
`EmissionDeterminismTests` self-comparison stays green and **no determinism-baseline waiver / diff
evidence was needed** (the deliverable-#4 evidence requirement only applies once anchors are emitted).

## Gates

- `nuke test` — UnitTests **15141 pass / 1 skip / 0 fail** (floor 14,772 ✓; baseline floor ratcheted
  15080 → 15141, the real deterministic pass count — all new tests are pure unit tests, no flaky
  headroom), Runtime 719/720, Analyzers 35/35.
- `nuke binding-tests --compile-only` — **EXIT 0** (regen + compile-check clean).
- Sim leg (`nuke binding-tests --skip-regen`) — **3242 pass / 2 crashes** (baseline 3192, +50). The
  two crashes are **pre-existing on clean main and NOT caused by this session** — see the dedicated
  crash-session handoff below. This session is additive new files in a new namespace, not wired into
  the emitter or runtime, so it cannot affect generated-binding runtime behavior: zero call sites of
  `BindingsGeneration.Diagnostics` anywhere in the generator/emitter/runtime, no `Swift.Runtime` or
  emitter file changed, and `--skip-regen` bindings are byte-identical to main HEAD. Committed as
  additive/non-regressing per team-lead go-decision; the crash gets its own dedicated fix session
  spawned before session 09.

## Review

Paired Codex + Grok, foreground (headless worker). r1 surfaced one Grok-High / Codex-Medium
(fingerprint discarded multiplicity → false no-progress after a real one-unit recovery) plus two
Mediums I fixed (bare non-tool `error:` dropped → failed compile invisible; orphan `note:` promoted
to primary). Fixes: fingerprint keeps per-message count; parser adds an anchored bare-diagnostic
branch and drops orphan notes. r2 (one verifying re-review per the fixed High) — **both reviewers
clean, no new defects**. Remaining r1 findings documented as latent (session-09 registry-completeness:
linker multi-symbol under-attribution, non-`SBW_` linker regex, symbol-anchor outer-block fallback) —
all priority-4/registry-dependent, still converge with no false-escalation; block-to-EOF and 32-bit
hash mirror the post-processor / repo conventions.

## Handoff — pre-existing sim-leg crash (dedicated fix session, before session 09)

A separate main defect surfaced while running the session-08 guard sim leg. It is **not** session
08's and must not be filed to roadmap/not-planned — it gets its own fix session. Everything that
session needs:

- **Failing tests (2):** `PatParentAsyncMethodsTests.TestAsyncBagMockStringItem_CancelRespondAsyncSurfacesCancellation`
  and `...TestAsyncBagMockIntItem_CancelRespondAsyncSurfacesCancellation`.
- **Crash signature:** `EXC_BAD_ACCESS` / SIGSEGV, pointer-authentication failure, in the
  async-cancellation dispatch path (parent-only async cancellation surfacing).
- **Suspect commit:** `42fa460a` ("surface parent-only async cancellation") — the feature that
  introduced this test path. Bisect from there.
- **Environment:** iOS Simulator, Xcode/sim **26.2.10233**. May be pointer-auth / toolchain-sensitive.
- **Determinism:** reproduced **2/2 runs** (deterministic, not flaky).
- **Attribution evidence (why it isn't session 08):** zero call sites of the new
  `BindingsGeneration.Diagnostics` namespace in generator/emitter/runtime (grep clean); no
  `Swift.Runtime`/emitter file changed this session; the sim run used `--skip-regen` so the compiled
  bindings are byte-identical to clean main HEAD; independently corroborated by
  `.agent/phase-7-summary.md`, which already recorded these two class-level crashes reproducing on a
  byte-identical HEAD worktree ("not this session's"). Pass count actually rose +50 (3242 vs 3192).
- **Per "ALL runtime crashes are OUR BUGS until proven otherwise":** this is a real generator/runtime
  bug to root-cause, not an environmental write-off — the fix session owns proving and fixing it.

## Notes for session 09 (loop wiring)

**Deferred deliverable (2.3) — anchor-comment EMISSION — MUST be picked up in session 09.** The
attribution library's `// SBW-ORIGIN:` *consumer* path is complete and tested this session, but the
generator-side *emission* of the anchors was deferred (rationale in the "Deviation" section above).
Land it here: emit anchors (or the fragment-owner scoping via the unused
`FragmentOwners.ForSharedHelper`/`ForDeclWrapper`) for symbol-less strippable blocks, verified
against `EmissionDeterminismTests` + a sim leg + the `RemoveTrailingWrapperPreamble` adjacency caveat,
with the deliverable-#4 diff evidence + baseline waiver that emission requires.


- Consume `DiagnosticAttributor.Attribute(...)` → `AttributionResult.Culprits` for the denylist
  increment; `ShouldEscalate(fingerprintHistory, result)` for the escalate-granularity decision.
- Wire the live registries the steps take as `Func<>`s: symbol → `ArtifactId` and `ArtifactId` →
  `RecoveryUnitId`. Registry **completeness** governs the latent findings above — ensure non-`SBW_`
  wrapper exports and every `@_cdecl` symbol are registered, or those blocks fall through to the
  interval map (still module-level correct).
- If precise attribution of stripped symbol-less blocks matters, land deliverable #4 (anchor emission
  / fragment-owner scoping) here, verified against `EmissionDeterminismTests` + a sim leg + the
  `RemoveTrailingWrapperPreamble` adjacency caveat.
- Multi-culprit-per-diagnostic (multi-symbol linker) currently reports one culprit/round; if
  single-round batching of all dead symbols is wanted, widen `IProvenanceStep` to yield multiple hits.
