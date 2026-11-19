# Surface accounting

`nuke surface-accounting` compares the generated public surface of two validation output trees
over the whole corpus. Every removal, addition, shape change, accessor loss and native retarget is
listed, and an unresolved change makes the comparison incomplete. A complete comparison whose tip
side is the current invocation's `nuke validate` output is the `SurfaceAccounting` receipt that
validation-baseline promotion requires. The target never runs the generator, never writes a product
baseline itself, and never selects targets by cache-directory glob.

## Promoting the validation baseline

The old side is the source the current baseline was promoted from (its `git_sha`); the tip side is
the candidate.

1. In a clean clone checked out at the baseline's `git_sha`, with the candidate's `.libraries`
   (copy or symlink it, so both sides generate from the same inputs), run a full `nuke validate`.
   Copy its output base (`$TMPDIR/binding-validation-<branch>`) somewhere it will not be
   overwritten: a later validate on a branch of the same name reuses that directory. A commit
   older than this procedure writes no capture; the first formal promotion therefore uses an old
   side from a commit that has it, and is a bootstrap (see below).
2. On the candidate, from a clean checkout, run everything in one invocation:

```sh
nuke validate pack-gate behavior-tier surface-accounting promote-validation-baseline \
  --surface-old /private/tmp/old-validate-output \
  --surface-output /private/tmp/surface-accounting-run
```

A full validate (no `--filter`, `--tier` or `--quick`) writes `surface-capture.json` and
`input-lock.json` into its output base, beside one hashed command record per target under
`surface-evidence/`. The capture holds the source commit and dirty state (read before and after
the run), the toolchain identity (toolchain profile, `xcodebuild -version`, `swiftc --version`,
`dotnet --version`), the input lock (a tree hash of every `.libraries` xcframework a target
generated from), and, per target, the output-tree hash and the generation, C# compile and Swift
compile results validate recorded, plus the SDK, architecture and triple it generated against.
Paths in the capture are relative to the output base, so a copied output base stays valid.
Any later validate run deletes the capture from its output base before regenerating.

With `--surface-old`, the target builds the `surface-accounting-request/1` in-process: the manifest
is the live `build/validation-libraries.json` and its SHA-256 now, the roster is its
`ValidationManifest.ExpandTargets()`, the old capture is read from the `--surface-old` directory and
the tip capture is the one this invocation's validate wrote. `--surface-request <file>` instead
reads an externally built request (all request paths resolve relative to the request file); exactly
one of the two must be given. Either way the request must name the live manifest's SHA-256 and
contain exactly its expanded targets with the same metadata; the library, module and target counts
are logged, not fixed.

The receipt is recorded only when the comparison is complete and:

- the request's tip capture is this invocation's validate capture, every tip target directory is
  this invocation's output base, and each target tree, re-hashed at record time, still matches the
  hash validate captured;
- the old capture's source commit starts with the baseline's `git_sha`. If it does not (the
  baseline's `git_sha` predates this procedure, or the baseline was edited by hand), the target
  refuses unless `--surface-bootstrap-justification "<why>"` is given; the receipt is then recorded
  and the promotion writes `surface_bootstrap: { "old_source", "justification" }` into the baseline.

A comparison whose tip is not this invocation's validate output (no validate in the invocation, or
a request naming other trees) still writes its artifacts and logs that it is not a promotion
receipt. On promotion, `Promote()` writes `git_sha` (the first nine characters of the tip capture's
commit) only for a clean tip, and drops a `surface_bootstrap` left by an earlier promotion. It never
writes `skip_metrics`: unlike a hand merge of validate's numbers, a formal promotion leaves
`skip_metrics` as it was.

## What fails closed

The request and its captures are rejected, or the comparison is incomplete, for: a request whose
manifest SHA is not the live manifest's, a missing, extra or changed roster row, missing or
placeholder SDK/architecture/triple provenance, old and tip directories that alias each other,
captures generated from different library inputs (the input locks differ) or toolchains, an
incomplete capture (dirty or moving checkout, failed validate, any target that did not generate,
compile its C# without errors, or compile its Swift wrapper where it has one), a capture marked
complete with a dirty patch or a failed command, output-tree or command-record hash drift,
duplicate target identities, missing evidence, malformed or duplicate-key JSON, phantom or ambiguous
API-manifest joins, and unresolved withdrawal multiplicity. The old and tip rosters must be the
same, so a promotion that adds or removes a corpus target cannot pass through this path.

## Artifacts

The output directory must not exist. A successful run writes `surface-accounting/1` evidence for both
captures (`corpus.json`, copied `input-lock.json`, `capture.json`, `files.jsonl`, `members.jsonl`,
`withdrawals.jsonl`, `coverage.json`) and one comparison (`comparison.json`, `changes.jsonl`,
`crosswalk.jsonl`, `summary.md`). `schema.surface-accounting-1.json` is copied as `schema.json`.
Before returning, the writer validates every surface-accounting JSON/JSONL record against that
bundled schema; the copied input lock keeps its own format and hash.

The Roslyn inventory treats additions, removals, return/constraint/default/base changes, accessor
losses, tombstones, raw manifest symbol changes, and native-route changes as separate observations.
It preserves manifest holes and every withdrawal occurrence; identical display strings are reconciled
as a multiset against distinct canonical recovery IDs. Syntax presence remains
`present-callable-unproven`: runtime safety belongs to the later packet-specific evidence.
