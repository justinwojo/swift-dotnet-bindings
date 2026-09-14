# Offline surface accounting

`nuke surface-accounting` compares two already-captured validation output trees. It never runs the
generator, mutates a product baseline, or selects targets by cache-directory glob. The target is the
offline analysis half of the frozen 0.20 corpus measurement.

The input is a `surface-accounting-request/1` JSON document containing:

- the frozen `build/validation-libraries.json` path and SHA-256;
- an independently frozen `input-lock.json` path and SHA-256;
- old/tip capture receipts, including source/toolchain/input identities, hashed command logs, and one
  output-tree hash plus stage receipt for every target;
- all 132 target rows with full `TargetKey`, explicit old/tip directories, manifest metadata, and
  resolved SDK/architecture/target-triple provenance.
- optional exact old/tip origin crosswalks, keyed by the full target/lane/public key and backed by
  a captured file hash, for classifying proven symbol-only, route, and semantic retargets.

The Nuke target re-expands the manifest with `ValidationManifest.ExpandTargets`, requires distinct
old/tip target directories, verifies every captured output tree and command log against its receipt, requires the fixed
66-library/110-module/132-target roster and rejects missing or changed rows. It also rejects dirty
captures marked complete, hash drift, duplicate target identities, failed stages, missing evidence,
malformed or duplicate-key JSON, phantom/ambiguous API-manifest joins, and unresolved withdrawal
multiplicity.

All request paths, including capture evidence directories, are resolved relative to the request file.
To authorize validation-baseline promotion, run `validate` and `surface-accounting` in the same Nuke
invocation; the target graph runs validation first, records a complete surface receipt, and promotes
only after all required receipts exist.

This repository change supplies the offline tool and contract. It does not itself contain or claim
the coordinated 132-target historical old/tip capture; that run must provide a fresh immutable
request and evidence trees before a comparison can qualify the corpus.

Run it only after the separate old/tip capture procedure has produced immutable trees and a request:

```sh
nuke surface-accounting \
  --surface-request /private/tmp/P1-RUN/surface-request.json \
  --surface-output /private/tmp/P1-RUN/surface-accounting
```

The output directory must not exist. A successful run writes `surface-accounting/1` evidence for both
captures (`corpus.json`, copied `input-lock.json`, `capture.json`, `files.jsonl`, `members.jsonl`,
`withdrawals.jsonl`, `coverage.json`) and one comparison (`comparison.json`, `changes.jsonl`,
`crosswalk.jsonl`, `summary.md`). `schema.surface-accounting-1.json` is copied as `schema.json`.
Before returning, the writer validates every surface-accounting JSON/JSONL record against that
bundled schema; the copied input lock retains its independently frozen format and hash.

The Roslyn inventory treats additions, removals, return/constraint/default/base changes, accessor
losses, tombstones, raw manifest symbol changes, and native-route changes as separate observations.
It preserves manifest holes and every withdrawal occurrence; identical display strings are reconciled
as a multiset against distinct canonical recovery IDs. Syntax presence remains
`present-callable-unproven`: runtime safety belongs to the later packet-specific evidence.
