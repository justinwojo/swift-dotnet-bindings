# Gradually narrow real-library validation

Long-term direction, not a scheduled migration. The retirement criterion below has not been established as satisfied.

`nuke validate` exists because we needed a quick "are these libraries still
working?" sanity sweep while the generator was changing rapidly. The long-term
goal has always been to make BindingTests the durable, sole gate. The 0.10.0
release cycle landed the first concrete investment toward that — the Layer B
`--skip-surface` ratchet over the BindingTests corpus, tightened in-bundle as
fixes ship.

**Retirement criterion**: validate is officially decorative when a full `nuke
validate` run surfaces no bug that BindingTests didn't already catch *across
multiple consecutive scheduled sweeps*. We're not close to that yet — the
audits behind the 0.10.0 plan ran against real third-party libraries and found
patterns BindingTests had no coverage for.

**Migration path**:

- **Each skip-class fix** lands a minimized Swift pattern in the BindingTests
  corpus (`BindingTests/Sources/SwiftBindingsTestLib/`), whose generated output
  is what the `--skip-surface` ratchet scans. Each shape-class fix lands Layer A
  coverage instead (Swift repro + C# assertion + generator unit test).
- **Future audit findings** route by class: skip-class drops a new minimized
  Swift pattern into the corpus as the first step of triage; shape-class adds
  Layer A coverage to the appropriate domain test class. In both cases the
  regression test lands as part of triage, not after the fix.
- **Validate's role narrows progressively**:
  - Today: targeted per-bundle gate where the bug was found only in real
    libraries (Bundles 8 and 9), discovery sweep pre-release.
  - Next minor cycles: as the corpus matures, drop targeted-validate gates
    bundle-by-bundle when the domain is provably covered by BindingTests.
  - Eventually: validate runs as scheduled discovery only, no merge gates.
- **Retirement happens** when validate stops surfacing surprises that
  BindingTests didn't catch across, say, three consecutive scheduled sweeps.
  Until then, validate stays in scope as a discovery sweep, not a blanket
  per-bundle blocker.
