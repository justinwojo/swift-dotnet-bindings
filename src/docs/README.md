# Internal docs — layout and conventions

Internal engineering docs for the binding generator, runtime, and release process. User-facing
documentation lives in the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki).

## Layout

| Where | What |
|---|---|
| `roadmap.md` | Statement of intent (work we expect to do) + hard policy boundaries (confirmed-upstream blocks, out-of-scope/by-design). Not an exhaustive index of active work. |
| `not-planned.md` | Acknowledged-but-not-planned register: trigger-gated latents, deferred designs, declined refactors, pending owner decisions. Nothing here is queued; an entry reopens only when its trigger fires. |
| `Design/` | As-built architecture and design rationale. Docs live here only while they accurately describe the current implementation. |
| `Future/` | Genuinely future work: deferred plans not yet scheduled, plus the queue of upstream dotnet/runtime issue filings (owner-driven). |
| `sessions/` | Session-runner program docs for **active** programs only. Gitignored (local-only by convention). Empty unless a program is in flight. |
| top-level `*.md` | Live working docs: standing contracts (e.g. `ingestion-hardening.md`, `version-coexistence.md`), signed decision records (`1.0-decision-record.md`). Dated diagnosis/audit memos do not live here — remove completed ones after extracting leftovers. |

## Conventions

- **Keep only future-facing docs.** For completed work, the code and tests are the documentation.
  Historical program/audit docs are **removed from the repo** — tracked ones and gitignored
  `sessions/` ones alike. Git history is a backstop for tracked files only; it holds nothing for
  gitignored docs. Before removing a doc, extract anything still load-bearing into `not-planned.md`,
  `roadmap.md`, or the wiki.
- **When closing out work, route leftovers to `not-planned.md`** (with a reopen trigger), never
  into `roadmap.md`.
- **Durable design rationale goes to `Design/`** — but only if verified against the code it
  describes; a design doc that has drifted is worse than no doc.
