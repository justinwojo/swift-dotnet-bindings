# P1 — Trustworthy acceptance design

## Scope and existing authorities

P1 closes W10-01, W10-02, and W10-03 at their existing acceptance boundaries.
It does not introduce a shared acceptance model across repositories.

The iOS CI driver already delegates runtime-test truth to
`dotnet nuke binding-tests --sim`. Its outer retry loop is responsible only for
deciding whether a new Nuke process may be attempted. The child output is the
available evidence at that boundary. An established pre-test startup failure is
the sole retryable condition; `[FAIL]`, crash evidence, loader/product evidence,
or evidence that the test process started dominates a later timeout or generic
launch-failure marker. The full simulator suite remains selected by the Nuke
command. The Python `tier` argument and the workflow `--tier 2` call sites are
dead labels and will be removed; the unrelated Nuke validation-library Tier is
unchanged.

The corpus runner already owns per-product generation, compilation, packing,
conversion receipt interpretation, and aggregate `all_ok` judgment. Its
authorities remain separate:

- generator exit zero authorizes publication for that generation attempt;
- a managed build records whether an emitted project compiles, but cannot
  authorize or overwrite a failed generation attempt;
- the converter receipt records requested/produced/missing primary products;
- the candidate product list selects requested primaries, while dependency-only
  products remain outside that requested-primary completeness check.

## Repair shape

Both repairs are local corrections because the defects are confined to judgment
already made inside one function in each repository.

In `ci_ios_test.py`, a small output classifier will admit an outer retry only
when output establishes a pre-test startup failure and contains no product,
loader, crash, or test-start evidence. The retry loop will use that classifier
for both nonzero child exits and `TimeoutExpired`. This is a bounded refactor of
the existing conditional so timeout and nonzero paths cannot drift. It does not
parse Nuke's complete result inventory or replace Nuke acceptance.

In `run_library.py`, each requested product missing from the selected primary
set receives its own `convert_incomplete`, `product_name_mismatch`, or
`input_provenance_unknown` product record derived from the conversion receipt
and discovered-versus-selected evidence and the aggregate will remain incomplete. A generated
project may still be compiled after a failed generation to retain useful
diagnostic evidence, but its classification remains `generate_failed` and
`publication_authorized` remains false. Generation-attempt ownership is recorded
with the final attempt's exit and log; an ordinary compile of retained output is
explicitly diagnostic. Healthy generated-and-packed products preserve their
current success classification.

The changes deliberately stay within each harness. A cross-repository
acceptance framework would add coupling without another demonstrated consumer or
shared contract.

## Exact validation matrix

| Boundary | Input | Required result |
| --- | --- | --- |
| CI actual `run_tests` | `[FAIL]` followed by outer timeout; a later success is available | no retry; nonzero |
| CI actual `run_tests` | crash marker followed by outer timeout; a later success is available | no retry; nonzero |
| CI actual `run_tests` | established pre-test startup failure followed by success | one retry; zero |
| CI actual `run_tests` | established pre-test startup failure on every allowed attempt | bounded attempts; nonzero |
| CI command/control | healthy first child exit zero | zero; one invocation; command still selects full `--sim` suite |
| CI call sites | repository-wide Python/YAML search | no `ci_ios_test.py` tier option, argument, log label, or workflow `--tier`; global validation Tier remains present |
| Corpus actual `main` | generator publication refusal/nonzero; retained project compiles | nonzero aggregate; `generate_failed`; compile success retained; publication unauthorized |
| Corpus actual `main` | two requested products, one surviving; partial receipt names the other missing | nonzero aggregate; healthy survivor retained; named missing product recorded |
| Corpus actual `main` | healthy requested product generated, compiled, and packed | zero aggregate; `ok`; publication authorized |
| Corpus actual `main` | two requested products both produced and healthy | zero aggregate; both product records present |
| Python syntax | changed production and test files | `py_compile` succeeds |

The focused Python tests inject subprocess and simulator boundaries. They execute
the production functions but do not run Nuke, a simulator, Swift conversion, the
generator, or a managed compiler. Shared Nuke/regeneration/simulator/device gates
remain lead-owned.
