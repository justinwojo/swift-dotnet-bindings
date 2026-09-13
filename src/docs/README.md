# Internal docs — layout and conventions

Internal engineering docs for the binding generator, runtime, and release process. User-facing
documentation lives in the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki).

## Layout

| Where | What |
|---|---|
| [`roadmap.md`](roadmap.md) | Short statement of intended work and links to active plans. |
| [`Future/notes/`](Future/notes/README.md) | Searchable deferred evidence by subsystem. Consult when relevant; not a backlog or mandatory reading. |
| [`Future/development-workflow.md`](Future/development-workflow.md) | Deferred opportunities to shorten development feedback loops: current status, measurement rule and correctness checks. Not an implementation commitment. |
| [`Design/`](Design/README.md) | Architecture, [engineering policies](Design/engineering-policies.md), [scope boundaries](Design/scope-boundaries.md), [decisions](Design/decisions.md), and useful troubleshooting rationale. Keep current contracts accurate; date historical evidence. |
| `Future/` | Deferred proposals (including their unresolved choices), subsystem notes, and [confirmed upstream blockers](Future/upstream-blockers.md). Filing is owner-driven. |
| [`sessions/0.20.0/README.md`](sessions/0.20.0/README.md) | Active 0.20.0 remaining-work gameplan, ordered batches and agent-ready plans. `sessions/` is gitignored/local-only; completed wave and review history is archived outside the repository. |
| top-level `*.md` | Active working docs and signed decision records (e.g. `1.0-decision-record.md`); implemented contracts belong in `Design/`. Dated diagnosis/audit memos do not live here — remove completed ones after extracting leftovers. |

## Conventions

- **Intended work goes in the roadmap.** Link the active plan when work is selected; a deferred possibility is not a commitment. Review the short roadmap during planning, not the entire note collection.
- **Record selectively.** Retain a deferred finding only when it preserves costly-to-recover evidence, explains an intentional limitation, or identifies a concrete condition that would change the decision. Otherwise leave it in the task summary. Do not automatically extract every leftover.
- **Give information one home.** Settled rationale belongs in Design; deferred evidence in the relevant subsystem note or proposal; consumer guidance in the wiki. Link across them instead of repeating the investigation. A pending choice stays with its proposal until it blocks intended work.
- **Keep entries short.** Use impact, evidence, current decision and a specific revisit condition; see the [note conventions](Future/notes/README.md). Long investigations can have a separate document when their evidence warrants it.
- **Remove completed records.** Code and tests preserve resolved behavior; extract any essential design rationale before deleting the old narrative. Prune cosmetic observations and superseded notes. Useful limitations can remain as reference indefinitely, with no promise to revisit them.
- **Preserve evidence deliberately.** Tracked history can recover removed tracked docs; gitignored session documents have no such backstop. Before removing a local-only plan, retain its essential unresolved evidence or durable decisions in the appropriate tracked home, or preserve its agreed external archive. Never promote every session leftover into a permanent record.
