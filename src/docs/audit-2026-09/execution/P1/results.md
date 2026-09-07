# P1 — Trustworthy acceptance results

## Implemented behavior

- The CI outer retry admits only the Nuke harness's established pre-test
  launcher-abort classification. `[FAIL]`, `[CRASH]`, Nuke terminal
  fail/crash/timeout output, JSONL/runtime identity, loader failures, and
  process-start confirmation dominate a simultaneous timeout or startup marker.
  Both stdout and stderr participate in the decision.
- `ci_ios_test.py` no longer accepts or threads `tier`; CI and release invoke
  the unchanged unfiltered `dotnet nuke binding-tests --sim` command. The
  current release documentation now calls this the full simulator suite. The
  unrelated validation-library `nuke validate --tier` option remains.
- A failed corpus generation remains `generate_failed` after a diagnostic
  managed compile succeeds. Product rows separately record
  `generated_success`, `managed_compiles`, and `publication_authorized`.
- Every candidate-requested primary receives a product disposition. A surviving
  subset cannot make a partial receipt green; the surviving product remains in
  the result alongside a named incomplete/mismatch/unknown row for each missing
  requested product. Dependency-only products are outside this completeness set.
- Final generator attempt logs use unique paths under each product's
  `generation-attempts/` directory and are named in `result.json` with a unique
  attempt ID. `generate.log` remains as the latest-log compatibility path.

## Focused validation

All commands ran on Darwin. No Nuke build, regeneration, simulator, or device
operation was run for this Python/YAML package.

| Command | Result |
| --- | --- |
| `python3 -B build/scripts/ci/test_ci_ios_test.py -v` | PASS — 7 tests: actual Nuke exhaustion text on exit and timeout, product-marker dominance, fail/crash+timeout dominance, bounded recovery, and healthy full-suite command control |
| `python3 -B corpus-sweep/scripts/run_library_acceptance_test.py -v` | PASS — 5 tests: refusal+compile, surviving partial subset, one-product healthy, two-product healthy, and requested normalized twins |
| `python3 -B corpus-sweep/scripts/run_library_receipt_test.py -v` | PASS — 11 pre-existing receipt groups |
| `env PYTHONPYCACHEPREFIX=/tmp/p1-final-pycache python3 -m py_compile build/scripts/ci/ci_ios_test.py build/scripts/ci/test_ci_ios_test.py src/docs/audit-2026-09/execution/P1/natural-refusal-classifier-probe.py` | PASS |
| `env PYTHONPYCACHEPREFIX=/tmp/p1-final-corpus-pycache python3 -m py_compile corpus-sweep/scripts/run_library.py corpus-sweep/scripts/run_library_acceptance_test.py` | PASS |
| `git diff --check` on the P1 files in each repository | PASS |
| Python/YAML call-site search for `--tier` / `tier=` | Only the negative assertion in `test_ci_ios_test.py`; no production/workflow occurrence |
| Current release-label search for `tier-2`, `Tier-2`, or `tier 2` | No occurrence in `CLAUDE.md`, workflows, or CI driver |
| `rg -n -- '--tier' CLAUDE.md build/Build.cs build/Build.Validation.cs` | The documented global validation `--tier N` remains; it was not changed |

## Natural refusal control

`natural-refusal-classifier-probe.py` ran actual `run_library.main()` against the
fresh retained `ContractNested` SWIFTBIND111/RequiresGraphClosure outcome.
Discovery and the managed compile were injected to isolate classification; the
generator itself was not rerun. The result passed with corpus exit 3,
`status=generate_failed`, generator exit 1, managed compile success, and
`publication_authorized=false`.

The durable receipt is
[`natural-refusal-classifier-result.json`](natural-refusal-classifier-result.json).
It pins the corpus source, natural generator receipt, and natural generation log
by SHA-256 and records the actual orchestration calls and classified result.

## Compatibility and evidence limits

The removed `tier` CLI option and Python keyword are intentional compatibility
changes; both repository workflow call sites were updated. External private
callers, if any, must remove that no-op argument. Corpus result consumers receive
additive product fields and may now see `convert_incomplete`,
`input_provenance_unknown`, or `product_name_mismatch` for a missing requested
subset that previously reported `ok`. Existing `generate.log` readers continue
to work.

The focused controls execute real acceptance functions with injected subprocess
boundaries. They prove judgment and retry behavior, not a real simulator run,
fresh corpus conversion, fresh generator execution, managed compilation, or
runtime correctness. Shared build/runtime qualification and external paired
review are lead-owned.

## Paired review corrections and compatibility

Round1 accepted High corrected exact Nuke prose collision;7 CI tests pass, including full terminal text, timeout and nonzero paths, and actual verdict/loader dominance. CI and release now run the Python regressions in their test jobs. Corpus5 acceptance cases pass after explicit normalized-twin completeness control. Existing11 receipt groups pass. Generation refusal now consistently reports generate_failed (previous compile_failed labels may shift); managed compilation remains independently diagnostic. New missing-product statuses can require manual trace in existing ratchet consumers, never a green result.

Artifact ownership inventories may include generated native files and increase result size/IO; uniquely retained generation logs accumulate by design. Output ownership describes the final attempt, with earlier attempt logs retained separately; it does not claim historical artifacts belong to final publication. Corpus tests follow the repository’s existing direct-invocation receipt-test convention; no corpus CI exists to wire.
