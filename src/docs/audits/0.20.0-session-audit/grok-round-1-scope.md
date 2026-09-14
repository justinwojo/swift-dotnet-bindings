# Swift Bindings 0.20.0 Stage B — authoritative review scope

Repository: `/Users/wojo/Dev/swift-bindings`

- Historical audit range: `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81`
- Historical range size: 11 commits, 190 files, 17,961 insertions, 2,059 deletions.
- Stage B candidate: the exact staged index diff at review launch (`git diff --cached`).
- Stage B candidate size: 15 files, 468 insertions, 45 deletions.
- Stage A findings: `/Users/wojo/Dev/swift-bindings/.agent/audit/findings.md`.
- Repository guidance: `/Users/wojo/Dev/swift-bindings/AGENTS.md`, `/Users/wojo/Dev/swift-bindings/CLAUDE.md`, matching `.claude/rules` files, and `/Users/wojo/.claude/review-notes/swift-bindings.md`.

## Exact Stage B candidate paths

- `build/Build.RuntimeNativeExports.cs`
- `build/Build.SurfaceAccounting.cs`
- `build/Build.Validation.cs`
- `build/Build.WithdrawalTests.cs`
- `build/Models/SurfaceAccountingModels.cs`
- `build/SurfaceAccounting/README.md`
- `build/SurfaceAccounting/SurfaceAccountingEngine.cs`
- `build/SurfaceAccounting/SurfaceSyntaxScanner.cs`
- `build/SurfaceAccounting/schema.surface-accounting-1.json`
- `build/baselines/validation-baseline.json`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs`
- `src/Swift.Bindings/tests/UnitTests/SurfaceAccountingEngineTests.cs`
- `src/Swift.Bindings/tests/UnitTests/SurfaceAccountingTestFixture.cs`
- `src/Swift.Bindings/tests/UnitTests/SurfaceSyntaxScannerTests.cs`

The working tree also contains unrelated unstaged P3 exposure-reporting work. It is owner state and is excluded from this review. In the two `MM` paths, only the staged hunks are Stage B: `Build.WithdrawalTests.cs` excludes the unstaged `.After(ValidateAppleTypesManifest)` line, and `validation-baseline.json` stages only the attributable unit-test floor `18970 -> 18978`; all other worktree changes in those files are excluded.

## Commands that expose the actual scope

- `git diff d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81 --`
- `git diff --cached --`
- `git diff --cached --check`
- `git status --short`

Inspect surrounding live source as needed, but do not treat unrelated unstaged changes as part of the candidate.

## Implementation / test / docs / claim-evidence checklist

1. A-02: public `const` values and enum raw values participate in canonical public-shape comparison, including implicit enum values and stable invariant formatting; readonly runtime fields remain value-less.
2. A-03: old/tip target directories must be distinct, command logs are contained and SHA-256 verified, stage receipts bind capture/source/toolchain identities, and output trees are deterministically hashed and verified. Adversarial tests cover aliases, log/output tampering, and wrong-source receipts.
3. A-04: the runtime package gate uses an independent six-slice platform/variant/architecture roster rather than the plist under test; actual plist metadata, actual Mach-O architectures, and actual exports are checked independently. Missing-symbol, wrong-slice, and missing-plist-slice controls all reject.
4. A-05: CSM internal-type admission parses conformer type structure and recursively checks nested generic arguments while preserving a defensive direct-name fallback. Tests cover a nested internal type and a cross-module near miss.
5. A-06: validation-baseline promotion requires a successful `SurfaceAccounting` receipt and the surface target records it only after a complete result. The promotion self-test proves the baseline is unchanged until this final receipt.
6. A-01 is intentionally accepted as immutable historical commit-message debt; no history rewrite is in scope.
7. A-07 remains an owner policy choice; no predictor-policy change is in scope.
8. OWNER-03 publication blockers remain unchanged and publication/push are prohibited.
9. Review schema/model serialization compatibility, capture failure modes, symlink/path alias behavior, deterministic hashing, constant semantic evaluation, runtime slice metadata/architecture joins, Nuke receipt scheduling, and the nested-generic parser/walker interaction.
10. Confirm tests exercise the concrete counterexamples from Stage A and that docs/schema match runtime requirements.

## Validation evidence

- `nuke test`: success. Withdrawal model 65 assertions; Swift.Bindings 19,024 passed / 2 skipped; analyzers 79 passed; runtime unit 922 passed / 1 skipped. Log: `/tmp/stage-b-integrated-test-r2.log`.
- `nuke pack-gate`: success in 3:10 (3:17 total). Runtime export gate checked 49 imports, 478 requirements, 10 slice/architecture pairs, six slices; all three new adversarial controls rejected. Log: `/tmp/stage-b-pack-gate-r1.log`.
- `nuke binding-tests --compile-only`: success in 5:13, including generated C# compile, wrapper compile, and ingestion/resilience gates. Log: `/tmp/stage-b-binding-tests-compile.log`.
- `nuke binding-tests`: success in 6:25; simulator runtime 4,061 passed / 32 skipped / 0 failed. Log: `/tmp/stage-b-binding-tests-simulator.log`.
- `git diff --cached --check`: clean before review.

Warnings in the binding logs are established generated Swift/Xamarin warnings; both gates completed successfully. The review should determine defects, not restate known warning volume.
