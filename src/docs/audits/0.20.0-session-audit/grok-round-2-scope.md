# Swift Bindings 0.20.0 Stage B — Grok serious-fix follow-up packet

Original scope remains `/private/tmp/paired-stage-b-gnItcs/scope.md`: historical range `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81` plus the Stage B staged index.

Original Grok session: `01a09d89-173c-75d2-b31d-dd49e6a1b147`. Round 1 report: `/private/tmp/paired-stage-b-gnItcs/grok-r1/result.md`.

Current staged candidate:

- 18 files, 556 insertions, 52 deletions.
- `git diff --cached | shasum -a 256`: `4cf00a78e300676c1ce6808a1f8c8958e5fe73d22158904ba781f1a9c39c457a`.
- `git diff --cached --check`: clean.
- Unrelated unstaged P3 work and the unstaged portions of both `MM` files remain excluded exactly as in round 1.

## Round 1 dispositions and fix delta

- H1 accepted: `CanonicalDirectoryPath` now resolves every symlinked ancestor and recursively canonicalizes an absolute link target whose own parents contain symlinks. New test constructs an ancestor symlink and proves old/tip alias rejection.
- H2 accepted: MethodGenericBridge routes both `OptionalClassPointer` and `OptionalErrorPointer` through `CdeclReturnRenderer.LinesBindingResult`; the existing optional-class unit test now asserts the Swift `.map { Unmanaged.passRetained($0...) }` body and rejects the old direct retain.
- M1 accepted: `ResolvePaths` resolves old/tip `EvidenceDirectory` relative to the request file, matching all other request paths. README documents this.
- M2 accepted: `SurfaceAccounting` is now `.After(Validate, ReleaseGatesAttest)`. When both targets share an invocation, Validate creates the process-local candidate before SurfaceAccounting records its Complete receipt; Promote remains `.After(SurfaceAccounting)`.
- L1 accepted: schema now has strict `commandReceipt` and `targetStageReceipt` item definitions requiring all hash/provenance fields. The acronym property is correctly `c_sharp_compile` under the canonical snake-case serializer.
- L2 accepted: recursive parser comment corrected.
- A-04 fixed as reviewed; A-05 fixed for the proven Stage A spelling; A-01 remains accepted historical debt; A-07 remains OWNER-02; OWNER-03 remains publication-blocking.

## Post-fix validation

- Focused unit slice: 294 passed, 0 failed (`/tmp/stage-b-review-fixes-targeted-r2.log`).
- Full `nuke test`: 19,025 generator tests passed / 2 skipped; 79 analyzer passed; 922 runtime passed / 1 skipped; 65 withdrawal assertions; total 1:52 (`/tmp/stage-b-review-fixes-test-r1.log`).
- `nuke binding-tests --compile-only`: passed in 5:09, including generated C#/Swift compile and resilience/ingestion gates (`/tmp/stage-b-review-fixes-binding-compile-r1.log`).
- Earlier unchanged relevant evidence remains: PackGate passed in 3:10 with all runtime export adversarial controls; full simulator binding tests passed 4,061 / 32 skipped.

The worktree baseline is 19025 because unrelated unstaged P3 tests contribute 46 tests; the staged, task-attributable clean-tree floor is 18979 (HEAD 18970 + nine new Stage B facts/theory cases). Do not treat the unstaged floor as part of the candidate.

## Follow-up review obligations

1. Verify both accepted High fixes across the whole affected category and callers/tests, plus regressions introduced by all accepted fixes.
2. Verify M1/M2/L1/L2 fixes and the target graph logic. Distinguish fail-closed liveness from false-green promotion.
3. Because round 1 explicitly reported incomplete coverage, close its stated groups 5–8 gap: inspect the remaining historical parser/model/config changes, remaining new BindingTests fixtures, API-manifest/skip/runtime-identity and blast-radius changes, remaining session/Future notes, and generated Nuke schema changes. Do not redo already-complete areas except where needed for interactions.
4. Preserve every new/dropped/demoted/inconclusive candidate and its reason. Report whether the original complete scope is now fully reviewed, with any unavoidable missing evidence stated precisely.
5. Report any fresh reachable High/Critical findings. Do not re-litigate A-01, A-07, OWNER-03, or resolved round-1 claims without new contrary evidence.
