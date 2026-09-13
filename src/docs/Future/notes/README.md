# Deferred reference notes

These notes preserve useful evidence and limitations by subsystem. They are **not a backlog**, a pending approval list, or mandatory startup reading. Search the relevant area when a consumer report arrives or when planning work there; there is no obligation to periodically reread the collection.

- [Protocols and reverse dispatch](protocols.md)
- [Generics and concrete specialization](generics.md)
- [Closures and async](closures-async.md)
- [SwiftUI bridge and KeyPaths](swiftui.md)
- [ObjC and mixed bindings](objc.md)
- [Parsing, type databases and ingestion](ingestion.md)
- [Runtime, marshalling and ABI](runtime-abi.md)
- [Verification and recovery](recovery.md)
- [Emission, naming and consumer surface](emitter.md)
- [SDK, packaging and test infrastructure](tooling.md)

## What earns a record

Keep a finding only if it preserves costly-to-recover evidence, explains an intentional limitation, or names a concrete condition that would change the decision. Otherwise leave it in the task summary. Recording it creates no commitment to implement it.

Prefer a short heading and a few sentences: impact, evidence (symbol/repro/commit), current decision, and a specific reason to revisit. “Owner authorizes it” or “someone edits this file” alone is not useful evidence of priority. Keep long evidence in a linked design/proposal or a clearly marked details block. Existing imported records retain their investigation detail; do not append a new session narrative to them.

```markdown
## Short description

Impact: What a consumer would observe, or what cannot currently be established.
Evidence: Relevant symbol, durable fixture, commit or investigation link.
Current decision: Why this is deferred or accepted.
Revisit when: A specific input, dependency change or demonstrated impact.
```

Settled choices belong in [Design](../../Design/decisions.md), product boundaries in [scope boundaries](../../Design/scope-boundaries.md), and intended work in the [roadmap](../../roadmap.md). Keep a proposal's unresolved choices with that proposal; move a question onto the roadmap only when it blocks intended work. A demonstrated defect affecting supported use deserves an explicit priority decision, not automatic burial among hypothetical shapes.

## When a finding becomes relevant

Recheck whether current code or an existing fixture has already resolved it. Reproduce the exact reaching shape first; recorded line numbers and counts drift. Follow repository validation guidance: rebuild with `nuke compile` before regeneration, and run the required device gate for calling-convention, ARC or marshalling changes. Activation is evidence for prioritization, not blanket authorization to implement every nearby idea.

Before re-chasing a 2026-06 R1–R6 lead, consult the refuted/verified-clean log in git history of `src/docs/regression-audit-followups.md`. Preserve a specific counterexample when rejecting an old conclusion. On resolution, remove the note after retaining any essential invariant in code, tests or Design. Retain a useful limitation even if it may never be fixed; discard observations that no longer earn their space.
