# Phase 0 Baseline Snapshot

**Captured**: 2026-04-28
**Branch**: `1.0-milestones` (newly branched from `main` for this session; the snapshot below measures the **source commit** the branch points at, not a separate docs commit)
**Source commit being measured**: `main@d5eccf2f`
**Companion**: `phase0-report-staleness.md` evidences the M1 `binding-report.json` staleness bug.

This snapshot is the floor for the architecture-track milestones. The zero-regression policy in `CLAUDE.md` measures every subsequent commit against the numbers below — pass counts and compile-shape metrics on `1.0-milestones` must stay at or above these values.

## Branch posture

`1.0-milestones` was branched off `main@d5eccf2f` and pushed to origin so subsequent sessions inherit it. Architecture work lands here; `main` stays shippable for hotfixes (gameplan §Phase 0 setup, item 4).

## `nuke test` — unit + analyzer + runtime-lib tests

| Suite | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|
| Swift.Bindings.Unit.Tests | 10159 | 0 | 1 | 22 s |
| Swift.Analyzers.Tests | 20 | 0 | 0 | 1 s |
| Swift.Runtime.Tests | 598 | 0 | 1 | 240 ms |
| **Total** | **10777** | **0** | **2** | **~24 s** |

Nuke target wall time: 0:42. Both skips are permanent (`NestedOptionalOptional_IsKnownLimitation`, `FailsWhenMetadataIsNotValid`). Source: `/tmp/phase0-test.txt`.

## `nuke validate` — cross-library compile gate

Nuke target wall time: 2:44 (Validate) + 0:29 (PackGate) = 3:16 total. All three targets (`Compile`, `Validate`, `PackGate`) reported `Succeeded`.

Validate writes `.validation-baseline.json`. The on-disk baseline updated in this run contains `git_sha: d5eccf2f` plus the per-library `cs_compile` and `swift_compile` shape counts; the SHA bump from `8a93674a` was the only diff vs. the previous on-disk copy, meaning compile counts were stable at HEAD.

`PackGate` produced expected `[#ExistentialAny]` and `[#DeprecatedDeclaration]` warnings from `TipKit.Wrapper.swift` and a handful of `NU5123` "path too long" warnings on the `Nuke.xcframework` swiftinterface paths — these are pre-existing and not regressions.

Source: `/tmp/phase0-validate.txt`. The `.validation-baseline.json` change in this commit is the SHA-line update only and rides along with the new docs (not a standalone SHA-stamp commit).

## `nuke binding-tests --sim` — iOS Simulator (Mono JIT)

Nuke target wall time: 2:10. Build succeeded. Test runner exit code 0 (`BindingTests Succeeded`).

| Metric | Value |
|---|---|
| `[PASS]` markers across both runner processes | 1762 |
| `[FAIL]` markers | 0 |
| `[SKIP]` markers | 0 |
| Final summary line ("ALL TESTS PASSED") | `363 passed, 15 skipped` (second process only) |

**Two-process pattern (current main behavior)**: the runtime test app is launched twice. The first process (PID 17404 in this run) ran A–O test classes through `OrphanedGetterShapeTests` (1398 PASS markers) and then crashed at the start of `OwnershipGCStressTests.TestAnimalCreateUseRelease` with a `Swift_Runtime_SwiftClassHandle_1_T_REF_ReleaseHandle` / `SafeHandle.Finalize` stack. The harness restarted as PID 17563 and ran the remaining O–W classes (363 PASS markers) cleanly. Nuke considers this `Succeeded` because the second process emits the success summary; the crash-and-relaunch is masked at the gate level. **This is a known artifact of the current runner, not a Phase 0 regression** — recording it here so subsequent gates can compare against the same shape.

Source: `/tmp/phase0-bindingtests-sim.txt` (~803 KB, 8260 lines).

## `nuke binding-tests --device` — iOS Device (NativeAOT)

Nuke target wall time: 1:56. Build succeeded. Test runner exit code 0 (`BindingTests Succeeded`).

| Metric | Value |
|---|---|
| `[PASS]` markers across both runner processes | 1774 |
| `[FAIL]` markers | 0 |
| `[SKIP]` markers | 0 |
| Final summary line ("ALL TESTS PASSED") | `363 passed, 15 skipped` (second process only) |

Same two-process split as the simulator path (PIDs 4928 and 4929 in this run). Source: `/tmp/phase0-bindingtests-device.txt`.

The device-path PASS-marker count is 12 higher than the simulator (1774 vs 1762) because `[SkipOnDevice]` and `[SkipOnSimulator]` carve out non-overlapping subsets.

## Dependent local repos

Recorded so subsequent sessions know the inherited state of repos this track touches via packaging / smoke-validation. Phase 0 reads them only — nothing was modified.

| Repo | HEAD | Working tree |
|---|---|---|
| `swift-dotnet-packages` | `92e68900` | Clean *except* two untracked dirs `apple-frameworks/RealityFoundation/` and `apple-frameworks/RealityKit/` (ongoing framework-coverage work, not stale debris). |
| `swift-interop-repro` | `75e2fd2d` | Dirty: modified `ReproApp/Program.cs` and `SwiftReproLib/Sources/ReproLib.swift`. This is the standalone scratch repro repo, expected to carry per-investigation iteration state. |
| `swift-dotnet-bindings.wiki` | `783944aa` | Clean. |
| `spm-to-xcframework` | `80046d79` | Clean *except* `src/__pycache__/` untracked (CPython bytecode, not real change). |

## Captured artifacts in this commit

- `phase0-baselines.md` — this file.
- `phase0-report-staleness.md` — M1 staleness evidence.
- `phase0-artifacts/cryptokit-binding-report.json`
- `phase0-artifacts/cryptokit-binding-emission-report.json`
- `phase0-artifacts/grdb-binding-report.json`
- `phase0-artifacts/grdb-binding-emission-report.json`

The four JSON snapshots are `binding-report.json` and `binding-emission-report.json` for two libraries (CryptoKit and GRDB), captured straight from the Phase 0 `nuke validate` output directory. `binding-report.json` is written at the literal `ReportEmitter.Emit` call site (`Program.cs:513`) before *any* of the post-emission C# disk-mutation passes fire — i.e., before `CSharpWrapperCoGater.ProcessSuppressedProxyReferencesInDirectory` (`Program.cs:521–528`), `SwiftWrapperPostProcessor.Process`, `SimulatorOnlyMemberDetector`, or the second `CSharpWrapperCoGater.ProcessDirectory` (`Program.cs:758`). The companion `binding-emission-report.json` is written *after* the first cogater pass (`Program.cs:531`) but still before the wrapper-compile-phase mutations. They become the baseline that M1's manifest-derived `binding-report.json` will be diff'd against to prove the fix.
