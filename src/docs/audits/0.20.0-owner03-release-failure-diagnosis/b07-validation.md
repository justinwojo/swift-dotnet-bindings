# B07 / D11 shared downstream qualification receipt validation

Date: 2026-09-14
State: implementation and focused validation complete; reviewed local repository commit
created, with no merge or push.

## Provenance and scope

- Reviewed `swift-bindings` base:
  `af1f1059a6308d2be3f7c5cc48ff9307c825a487` (detached worktree).
- Downstream harness base: `internal-binding-testing`
  `820cfd1d47ae2f92cfacacd5b0aded41fff3e0e6` in the isolated
  `/private/tmp/internal-binding-testing-b07` worktree.
- The shared JSONL contract has one checked `cell` record per library and exactly one
  final checked `terminal` record. The model rejects malformed JSON, missing/unknown
  fields, unsupported schema versions, negative/non-integral counters, duplicate cells,
  platform drift, cell records after the terminal, missing/duplicate terminal records,
  aggregate drift, false terminal outcomes, and passing cells that carry failed-test
  markers.
- Checked-in fixtures cover zero marker matches, positive marker matches, malformed
  JSON, an incomplete receipt, and a duplicate terminal. The zero-marker source is also
  consumed by the downstream shell self-test that reproduces the original two-line
  `grep -c ... || echo 0` value before proving the replacement helper emits one `0`.

## Focused first-party validation

Command (from the `swift-bindings` root):

```sh
set -o pipefail
dotnet test src/Swift.Bindings/tests/UnitTests/Swift.Bindings.Unit.Tests.csproj \
  --no-restore \
  --filter FullyQualifiedName~DownstreamQualificationReceiptTests \
  --logger "console;verbosity=normal" \
  2>&1 | tee /tmp/owner03-b07-unit-tests-final.log
```

Result: green, 8 passed / 0 failed / 0 skipped. Log:
`/tmp/owner03-b07-unit-tests-final.log`, SHA-256
`565284393bb583076d46ab03e395f3564bac3248aa5c899ddded7ee0f08eb504`.

The isolated worktree initially had no NuGet assets. One restore-enabled invocation was
therefore run outside the filesystem sandbox (MSBuild IPC sockets are denied inside it),
then the final command above ran against that restored graph.

## Downstream validation

The exact downstream commands and results are recorded in
`internal-binding-testing/docs/receipts/owner03-b07-validation.md`. Both harness paths
consume these fixtures directly from this repository; they do not carry copied fixture
data. Simulator and device each passed zero-match, positive-match, malformed/incomplete/
duplicate-terminal rejection, and a deterministic fixture-backed 15-cell summary.

The two generated 15-cell receipts were independently validated as 16 JSONL records
(15 cells plus one terminal), with `terminal_records=1` and terminal
`total=15 pass=15 fail=0 crash=0 skip=0 outcome=pass`. Combined proof log:
`/tmp/owner03-b07-summary-proof.log`, SHA-256
`ce08b5034185ed5e971fd4f282bdfc5557d18b9374110dbec67d62bf56d3fd89`.

Real simulator/device application execution was intentionally not run for this
test-infrastructure batch. The deterministic mode executes the same counter, cell
emission, terminal emission, and validator functions used by production without
changing production qualification semantics.

## Final Grok-only review disposition

The complete two-repository B07 candidate received one final Grok-only review. No
Critical or High defect was found. One Medium packet-evidence issue and one Low static-
evidence gap were accepted and repaired in tracked receipts only; no product, harness,
parser, validator, fixture, or test source changed after review. Per the Medium/Low-only
stopping rule, no second review cycle ran. Full review identity, candidate dispositions,
coverage, and evidence are recorded in
[`b07-grok-review.md`](b07-grok-review.md).

The packet-only `/tmp/owner03-b07-unit-tests-lead.log` is invalid and superseded after a
sandbox MSBuild process overwrote it with NUL-polluted failure output. It is not closure
evidence. The primary `/tmp/owner03-b07-unit-tests-final.log` remains authoritative: its
SHA-256
`565284393bb583076d46ab03e395f3564bac3248aa5c899ddded7ee0f08eb504`
still matches and proves 8 passed / 0 failed / 0 skipped.

The final receipt-only static validation passed bash syntax, Python compilation without
leaving a cache, and unstaged/staged `git diff --check` in both repositories. Exact
commands and exit statuses are in `/tmp/owner03-b07-static-validation.log`, SHA-256
`38b0edc9154726d0b26a59da19f24eb0bba475466b01ba4fcae6400fdc80e135`.
