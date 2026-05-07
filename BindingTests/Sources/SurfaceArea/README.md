# SurfaceArea — Layer B authored corpus

This directory holds the curated Swift snippets that the Layer B "skip-surface"
trend gate scans for skip-class regressions. It is the durable consolidated
catalogue of every adversarial *skip-class* pattern third-party libraries have
surfaced — the long-term replacement for ad-hoc rediscovery via `nuke validate`.

The directory **ships empty** with the scaffolding commit. Each skip-class fix
bundle contributes its own snippet in the same commit as the fix; shape-class
projection fixes land coverage in Layer A (a domain test class under
`BindingTests/Sources/SwiftBindingsTestLib/` plus a generator unit test) instead.

**Routing rule.** Skip-class entries are those whose primary failure mode is
the generator emitting a skip marker (or silently dropping the API) — those
land here. Shape-class entries (wrong projection, missing interface adoption,
lost default value, etc.) compile and run but produce a wrong C# surface;
those land in Layer A as targeted semantic assertions. Trying to do both in
one count-based gate muddles the signal.

## Keying & ratchet

The gate parses mechanically-detectable skip markers from the generator's
`.cs` output and aggregates them by `(source-file, marker-kind, normalized-reason)`.
The committed baseline file is `.skip-surface-baseline.json` at the repository
root.

Markers parsed today:

- `// Unsupported: <reason>` line comments
- `// Skipped: <reason>` line comments
- `[UnsupportedSwiftType("<reason>", …)]` attributes (any namespace prefix)
- `[Obsolete("…", DiagnosticId = "SB0001", …)]` attributes
- Tombstones — declared in metadata cookie maps but absent from generated
  C#. The `Tombstone` marker keyword is reserved in the schema; the detector
  is wired alongside the first skip-class fix that actually surfaces it
  (see plan-doc Bundle 7).

The ratchet semantics are:

| Diff   | Outcome    | Notes                                                              |
|--------|------------|--------------------------------------------------------------------|
| Flat   | Pass       | No change since baseline.                                          |
| Down   | Pass       | Improvement — bundles can either edit the baseline downward in the same commit or leave the `count: 0` row to bank a clear before/after audit trail. |
| Up     | **Fail**   | Either fix the underlying skip OR — if intentional — update the baseline in the same commit. |
| New key| **Fail**   | A current key not present in the baseline fails. New keys are introduced only by committing a baseline update in the same change. |

When you populate a snippet that introduces a new authored skip-key, run
`nuke binding-tests --compile-only --skip-surface` and let it fail; that's the
diff to copy into `.skip-surface-baseline.json` as the same commit's
ratchet update.
