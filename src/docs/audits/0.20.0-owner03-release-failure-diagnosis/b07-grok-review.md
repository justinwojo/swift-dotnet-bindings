# B07 / D11 Grok-only review receipt

Date: 2026-09-14

## Frozen two-repository scope

- `swift-bindings`: `/Users/wojo/.codex/worktrees/87b7/swift-bindings`, base and
  review-time HEAD `af1f1059a6308d2be3f7c5cc48ff9307c825a487`; complete staged
  11-path B07 diff, SHA-256
  `8ebceb64e4fa1245a36330fc7e5db362f58d63142d86d2b10c1c71c8ccb17896`.
- `internal-binding-testing`: isolated worktree
  `/private/tmp/internal-binding-testing-b07`, base and review-time HEAD
  `820cfd1d47ae2f92cfacacd5b0aded41fff3e0e6`; complete staged 5-path B07
  diff, SHA-256
  `254b79c5747cc2377e91234fa94e10ec9cccad00b9e9535fef7af7d5f18d2eae`.
- The reviewer rechecked both snapshots after all four area reviews and found no
  drift. The shared dirty checkout at `/Users/wojo/Dev/internal-binding-testing`
  was not inspected or touched.
- The review covered the C# parser/model and tests, every shared fixture, both bash
  harnesses, the Python validator, both validation receipts, the accepted D11/B07
  requirements, and cited evidence.

The post-review receipt-only changes described below were not part of the frozen
snapshot. No product, parser, validator, harness, fixture, or test source changed after
review.

## Run identity and artifacts

- Reviewer: Grok only; four parallel area reviews plus one adjudicating pass.
- Session: `01a0a0f8-9841-77f2-9527-94e62212ae81`.
- Status: captured; exit code 0; started `2026-09-14T17:30:41.127666+00:00`
  and finished `2026-09-14T17:50:14.054027+00:00`.
- Scope packet: `/private/tmp/paired-b07-d11-QA6bK9/scope.md`, SHA-256
  `2105cd76b71823ceef50664c48eef205428a9abee0db7b3e92e0f77b6b77364b`.
- Preserved report: `/private/tmp/paired-b07-d11-QA6bK9/grok-r1/result.md`,
  SHA-256
  `e4dbeb48f5655566e6b49485a749476ad9e64112b9fb2b4fd9716a5d22cd578f`.
- Run metadata: `/private/tmp/paired-b07-d11-QA6bK9/grok-r1/run.json`,
  SHA-256
  `a16b48fae12eb45d9b7c02af9e0c7d84325b06c2276d723c397c0b75c0053cce`.

The user explicitly overrode the usual paired-review workflow to Grok-only. No Claude
review or Claude process was run. The reviewer did not edit either worktree.

## Result and retained findings

No Critical or High production defect was found. The reviewer confirmed that the C#
and Python validators agree on the strict versioned JSONL contract, both production
harness paths emit and validate a required passing terminal, the original two-value
aggregation is absent from production, the exact shared fixtures drive both self-test
paths, and B06 DeviceKit/KeychainAccess migrations remain outside the B07 diff.

Two evidence findings were retained:

1. **Medium — packet-only lead-rerun hash became stale.** The scope packet cited
   `/tmp/owner03-b07-unit-tests-lead.log` at SHA-256
   `c5981b245bf2e6b29172773f62b8cec2e683940adfb4014307155186fd5057cd`.
   The file was later overwritten/NUL-polluted by a sandbox-denied MSBuild process and
   hashes to `aeca2f7f37e9f798ac9bb91d0b7c50e1b226f3322a147111751f292ae7c833a0`.
   That secondary packet artifact is invalid and superseded. It is not closure evidence.
   The staged durable receipt's primary `/tmp/owner03-b07-unit-tests-final.log` still
   matches its SHA-256
   `565284393bb583076d46ab03e395f3564bac3248aa5c899ddded7ee0f08eb504`
   and cleanly proves 8 passed / 0 failed / 0 skipped.
2. **Low — static checks lacked a hashed evidence log.** The internal receipt asserted
   `bash -n` and Python syntax compilation without attaching a log. The accepted final
   fix pass produced one deterministic log containing each exact command and explicit
   successful exit status, and updated that receipt.

Both findings were accepted. The Medium was repaired by making the invalid secondary
packet artifact and authoritative primary evidence explicit in durable tracked receipts;
no attempt was made to reconstruct unavailable historical bytes. The Low was repaired
with `/tmp/owner03-b07-static-validation.log`, SHA-256
`38b0edc9154726d0b26a59da19f24eb0bba475466b01ba4fcae6400fdc80e135`.
Per the Medium/Low-only stopping rule, there was one worthwhile receipt fix pass and no
second review cycle.

## Complete candidate dispositions

| Candidate | Disposition | Basis |
| --- | --- | --- |
| Packet lead-rerun hash mismatch | Keep Medium; fixed in receipts | Secondary evidence is corrupt/superseded; matching primary log remains authoritative. |
| Unlogged `bash -n` / Python syntax checks | Keep Low; fixed | Added exact commands, exit statuses, log, and hash. |
| Empty C# area findings | Keep empty | Required fail-closed fixture cases throw; no parser defect found. |
| Zero-pass cell with `tests_passed=0` | Drop | D11 requires one numeric zero; it does not make a successful zero-marker cell fail. |
| C# test reconstructs grep rather than invoking it | Drop | The shell self-test performs the actual two-value reproduction. |
| Global model namespace / Nuke does not invoke parser | Drop | Matches sibling build models; C# owns the tested contract and Python owns the production gate. |
| Untested extra strict-parser arms | Drop as findings; retain as coverage gap | Both implementations agree; required named fixture set is present. |
| Remaining `grep -c ... || printf` | Drop | It exists only in the zero-mode regression oracle, not production aggregation. |
| Darwin awk bracket-pattern concern | Drop | Actual double-escaped callers and fixture self-tests produce `0/0` and `2/0`. |
| Successful result could hide failed markers | Drop | Both validators reject a pass cell whose `tests_failed` is nonzero. |
| `kill`/`wait` cleanup suppresses errors | Drop | Local process cleanup does not wrap receipt emission or validation. |
| Device bundle id ends in `simtest` | Drop | Pre-existing and outside the reviewed hunk. |
| Bash terminal computation ignores other counters | Drop | One exclusive outcome is counted per library; validators independently check all aggregates. |
| Python `--require-pass` is optional generally | Drop | Both production paths and pass-expecting self-tests explicitly supply it. |
| Emit helpers lack explicit `|| return 1` | Drop | Callers use `set -euo pipefail`; emitted production tokens are validated. |
| Bash 3.2 `set -e` function behavior | Drop | Production validation is top-level and hashed self-tests reached success markers. |
| Malformed mode accepts any validator nonzero | Drop | Other modes and production still require Python and validated passing receipts. |
| UTF-8 BOM difference | Drop | Python is stricter; the emitter writes no BOM. |
| B06 paths accidentally included | Drop | DeviceKit/KeychainAccess appear only in unchanged library-list context. |
| Missing 15-cell proof / live-run overclaim / false primary test claim / suspicious duplicate hashes | Drop | Hashes and logs match; limitations are explicit; identical fixture-mode output is expected. |

No High/Medium candidate was silently discarded, no candidates were merged, and there
were no inconclusive High findings.

## Coverage, gaps, and final validation

All 16 staged paths and relevant surrounding source were reviewed. Four area reports
completed; no assignment failed or was skipped. Review evidence covered the primary
focused test log, all zero/positive/malformed self-test logs, both generated 15-cell
receipts, and the independent single-terminal proof.

The review did not re-execute `dotnet test`, the `bash -u` self-tests, or real simulator/
device applications. Extra strict-parser arms (unknown fields/version, duplicate keys,
platform drift, bool-as-int, and a consistently-counted false pass) are implemented and
cross-checked but do not each have a shared JSONL fixture. Missing-csproj/build/app/install
cell paths were inspected statically; the deterministic summary synthesizes passing cells.
Real simulator/device execution remains intentionally outside this infrastructure batch.

Post-review receipt validation, with no product-code change:

- `bash -n` passed for `run-all-sim.sh`, `run-all-device.sh`, and
  `qualification-receipt.sh`.
- Python bytecode compilation passed using a `/tmp` output that was removed immediately;
  no repository cache was left.
- Unstaged and staged `git diff --check` passed in both repositories.
- Every command and `exit_status=0 result=pass` is recorded in
  `/tmp/owner03-b07-static-validation.log`, SHA-256
  `38b0edc9154726d0b26a59da19f24eb0bba475466b01ba4fcae6400fdc80e135`.
