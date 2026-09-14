# OWNER-02 final review scope packet

- Repository: `/Users/wojo/.codex/worktrees/2cea/swift-bindings`
- Candidate scope: staged-only changes against HEAD `d34177058f1f9069b72636cde0d43ef56fd19df3`
- Authoritative complete staged diff: `/private/tmp/paired-owner02-20260914/candidate.diff`
- Diff stat: `/private/tmp/paired-owner02-20260914/candidate.stat`
- Validation logs:
  - `/private/tmp/owner02-focused-tests-final.txt`
  - `/private/tmp/owner02-bindingtests-skip-surface-final.txt`
  - `/private/tmp/owner02-nuke-test-final.txt`
- Temporary untracked review files `.owner02-review-scope.md` and `.owner02-grok-prompt.md` are review infrastructure and are not part of the candidate.

## Objective and intended contract

Resolve OWNER-02 / A-07 in `src/docs/audits/0.20.0-session-audit.md` by removing the audited wave's new hand-coded predictors for compiler-detectable failures. Foundation `NSInvocation`, concrete-specialization internal conformers, and lossless-only marker/concrete-pin constrained-extension failures should render into the wrapper, reach swiftc, attribute to the narrow owning recovery unit, withdraw only that unit, and re-render. Healthy siblings and unrelated surface must remain. The older representable protocol/associated-type constraint check remains as an established semantic fast path; this batch removes the later lossless expansion, not every pre-existing admissibility rule.

The wrapper postprocessor should no longer hide internal-type or Swift-unavailable diagnostics before the recovery loop. It remains only for deterministic placeholder cleanup. Historical manifest bucket names stay present at zero for schema compatibility.

## Changed-path groups

- Generator behavior: `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs`; emitter validation/admission files under `src/Swift.Bindings/src/Emitter/StringEmitter/`; related parser/program comments.
- Focused tests and compiler captures: `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/SwiftWrapperPostProcessorTests.cs`, `Diagnostics/WrapperRecoveryLoopIntegrationTests.cs`, seven new files under `Diagnostics/Fixtures/`, and emitter tests.
- End-to-end recovery fixture: `BindingTests/Sources/ResilienceKitchen/ResilienceKitchen.swift` and `build/Build.BindingTests.ResilienceKitchen.cs`; the former healthy runtime module removes the two deliberate compiler-invalid members and retains its runtime control.
- Build/baselines: wrapper-strip models/validation and the exact BindingTests/validation/skip-surface baseline changes.
- Durable evidence: design decision, recovery note, audit summary/findings/coverage/validation and checksums.

## Implementation / test / docs / claim-evidence checklist

1. Confirm the exact new predictors named by A-07 are removed without deleting unrelated ABI/soundness gates or changing the established representable constrained-extension behavior.
2. Confirm postprocessing preserves internal-type and `NSInvocation` blocks and their provenance, while remaining deterministic placeholder cleanup still removes its intended blocks and preserves source remapping.
3. Confirm recovery attribution is real rather than asserted by construction: the recorded swiftc diagnostics flow through the production parser, attributor, and recovery controller; each family maps to the intended leaf and the healthy sibling is not denied.
4. Confirm ResilienceKitchen actually exercises the removed lossless constrained-extension behavior end to end, checks exact `SwiftCompile`/`EmitterFault` attribution, null cascade, narrow scope, recovered C# compilation, and hostile/control surface parity.
5. Check baseline changes correspond only to the behavior/test-fixture move and do not conceal new loss or weaken a ratchet.
6. Check docs accurately resolve OWNER-02 while preserving OWNER-03 as publication-blocking and do not overstate validation/review status.
7. Inspect all new compiler fixture files, including `InternalConformerRecovery.dependency.swift`, for whether their captured diagnostics and provenance model the claimed family.

## Validation evidence

- Focused OWNER-02 test selection: 532 passed, 0 failed, 0 skipped.
- Full `BindingTests --compile-only --skip-surface`: passed in 4:27. Generated Swift and C# compiled; wrapper strip count stayed 0; ResilienceKitchen recorded exactly four localized withdrawals (two accessor groups and two leaf APIs), all with null cascade, retained 110 declarations identical to the control, and kept nine named healthy siblings; skip-surface ratchet passed over 270 keys.
- Aggregate `Test`: compile and 65 withdrawal assertions passed; Swift.Bindings tests reached 19,011 pass / 2 skip / 1 failure. The sole failure is a pre-existing baseline inconsistency in `RuntimeBaselinePlatformKeyTests.DeviceMonoAotLane_ScalarAndIdentityFloorsAgreeOnSkipCount` (expected 4,030, actual 4,061). This candidate does not change either runtime floor. Because UnitTests failed, later analyzer/runtime test targets were not run in this invocation.
- `git diff --cached --check` passes after normalizing trailing whitespace on otherwise blank swiftc gutter lines. Baseline JSON parsing and audit checksum verification were reported green by the implementation worker.

## Review exclusions and cautions

- Review only the staged candidate. Do not report unrelated pre-existing defects unless newly exposed by this patch.
- Generated `BindingTests/output/**`, `bin/**`, `obj/**`, and artifacts are build products, not candidate source.
- Do not treat Nuke kebab-case targets as invalid. Generator C# produces emitted Swift/C#; distinguish generator control flow from output code.
- Do not attribute runtime failures upstream without first verifying generated P/Invoke/wrapper agreement.
