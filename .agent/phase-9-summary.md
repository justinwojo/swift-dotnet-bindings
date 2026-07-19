# Session 09 — wrapper verify-recover loop — summary

**Outcome:** inc.3b landed green + reviewed. inc.3a and inc.3c deferred TOGETHER to a fresh session
(deliberate sequencing decision, below). inc.4 remains after them.

## What shipped

**inc.3b — Gate-0 denylist→emission re-render seed. Commit `8e76a420` (main).**
- `EmitterFaultOrigin` enum (`EmitterException` default / `RecoveryWithdrawal`) + factory
  `EmitterFaultRecord.ForRecoveryWithdrawal(subject, scope, reason, escalation?)`. `Details` branches on
  origin: a withdrawal reads "Withdrawn by wrapper verify-recover: …", an exception keeps the existing
  "Emitter threw {type} at {fingerprint}: …" wording. Default origin is `EmitterException`, so every
  `From(...)`-built record and all existing consumers are byte-unchanged.
- `WrapperDenylistSeed.Build(IReadOnlySet<RecoveryUnitId>) → EmitterPoisonList` — turns the controller's
  denylist into the poison list a re-render denies up front, keyed on `unit.Decl` (accessor-group units
  already normalized to the property). Each denied unit leaves through the ordinary skip channel
  (`// Unsupported:` tombstone + report row), indistinguishable from a member the library never had.
- `WrapperDenylistSeedTests` — 5 tests: honest-origin record reads as withdrawal; exception record still
  reads as throw; `Build` poisons every unit under its declaration; empty denylist → empty seed;
  end-to-end render through `ContainedModuleEmission.Run(seed:)` proving each denied unit
  (`register(third:)` + `name`) is tombstoned under its own `Describe()` identity, the `Name` member is
  actually gone (vs a clean render that has it), and the `Count` sibling emits identically to the clean
  render. Floor 15,189 → 15,194.

## Gates
- `nuke test` (Swift.Bindings.Unit.Tests): **15,194 passed / 0 failed / 1 skipped**. Analyzers + runtime
  suites green in the prior run. Generator `dotnet build -c Debug` clean (0 warn / 0 err) before regen.
- Not run (nothing in scope touched them): `nuke binding-tests`, `nuke validate`. inc.3b is a pure
  generator-internal add with no emitted-output change on the healthy path (seed is empty when no unit
  is denied) — unit coverage is the right layer. The binding-tests runtime leg lands with inc.4.

## Review
Paired Codex + Grok on the inc.3b diff.
- Codex `019f77f2-1e38-76a2-8de3-8b3da8c836c5`; Grok `019f77f2-2260-7a70-9ced-e746cf395f83`.
- **No Highs.** Both confirmed the production path sound: `ExceptionType`/`Fingerprint` read only inside
  `Details` (plus test assertions); default origin keeps exception wording; escalation `with` preserves
  origin; accessor-group keying matches the Gate-0 property-level lookup.
- Shared **Medium** (E2E under-assertion — original test could pass with one generic tombstone, never
  tied a tombstone to a specific unit, never checked siblings). **Fixed**: strengthened the test to
  assert both units independently via unique `Describe()`, prove the denied `Name` member is gone, and
  measure `Count`-sibling parity against a clean render.
- **Low** doc-drift on `EmitterFaultRecord` ("One declaration the emitter threw on"). **Fixed**: comment
  now covers withdrawals too.
- `Record`-bool collapse (Codex Low / Grok Medium): distinct units sharing one `Decl.Canonical` collapse
  to one poison record. Unreachable on the wave-1 leaf/accessor path (methods vs properties are distinct
  decl kinds) AND safe when reached (both map to the same declaration gate, so the member is still
  denied). Left as-is by design; noted in the design doc.
- Re-review gate: coding-rules mandates a verifying re-review only for a fixed **High**. None surfaced;
  fixes were test-only + a comment. No second round run.

## Sequencing decision — inc.3a defers WITH inc.3c (settled via paired consult, not escalated)
Consult: Codex `019f77e6-959a-7cd0-ad54-6561e85dc03c`, Grok `019f77e6-99a2-77e1-8c7a-6b796502f30a`.
Both independently converged that inc.3a's cross-slice collecting mode must NOT land as un-wired dead
code — its surface (promote policy, return shape, per-slice dedup, non-swiftc failure handling) is
consumer-defined and the inc.3c recovery driver is the only correct acceptance oracle. Landing 3a first
bakes in a surface 3c must bend around. Grok rated "do not land collecting as dead code" High. So 3a
rides WITH 3c in the fresh session. This matches the team-lead's overriding principle: "a half-built
core-pipeline change is the one outcome worse than a session split."

## Fresh-session work (in order), fully specified in `.agent/phase-9c-design.md`
1. **inc.3a + inc.3c together** — cross-slice structured diagnostic surface in `SwiftWrapperCompiler`
   (catch the whole per-slice body, never promote a partial staging tree, snapshot full stderr in-catch
   because the side-file is overwritten per slice) AND the production in-emission verify-recover loop
   inside `GenerateBindings` (pristine pre-emission baseline restore-before-every-render,
   full-finalization-once, `WrapperSymbolIntegrityGate` re-run + `WrapperStripRemap` post-strip wiring).
2. **inc.4** — resilience fixture Swift lib into `nuke binding-tests --compile-only` + a BindingTests sim
   runtime leg (compile-only cannot prove retained siblings are ABI-sound) + corpus before→after proof.
- 09b (task #6, separate session): RecoveryGraph population, ABI-as-loop-input, PostProcessor-as-iter-0.

## Non-negotiables honored
Fail-closed invariant intact (seed only withdraws leaf/accessor units the controller vetted; nothing
here can leaf-poison a multi-artifact unit). Honest scope (3a NOT stubbed in as dead code). Zero
regressions (floor raised in lockstep with the 5 new tests, 0 fail). Root-cause only; committed on main
in house style; no doc refs in code comments; copyright headers on new files.
