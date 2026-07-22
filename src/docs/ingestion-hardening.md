# Ingestion hardening — the input contract

This is the standing contract the generator upholds at its **input edge**: the guarantee that a
malformed, incomplete, or partially-unreadable input either produces a sound binding that honestly
records what it dropped, or fails closed with an auditable reason — never a binding that compiles
clean and is wrong at runtime, and never a silent loss of surface. It is the input-side complement
to `Design/binding-resilience-design.md` (which governs the emission edge: regenerate-from-plan with a
disabled-unit set). Where that doc's recovery primitive keeps *emission* sound, this contract keeps
*ingestion* sound: every input node that is lost, deformed, or withdrawn becomes a structured ledger
entry, and no whole-binding decision rests on a prediction the compilers could have verified.

## The one-sentence conclusion

A binding is only allowed to publish when its input graph is closed and every retained declaration
was parsed completely; anything the input could not supply is either **withdrawn** (dropped from the
surface, tombstoned, and recorded with proof that the withdrawal closure is complete) or **fatal**
(the module fails before emission). "Degrade silently" is not in the contract.

## The obligations the input edge must discharge

The publication obligation ledger (`PublicationObligationLedger`) records a verdict for every
soundness obligation a published binding must meet; two of them are owned by the ingestion contract:

- **The input dependency graph is closed** — no required edge left unresolved. A required
  `--framework-dependency` that fails to parse, or an ingestion-quarantine withdrawal closure that
  cannot be proven complete, fails the module closed before publication. A best-effort auto-detected
  dependency that drops is a recorded input *degradation*, not a graph-closure failure — it narrows
  the surface with a ledger row, it does not fail the module.
- **Every retained declaration parsed completely — no unledgered input loss.** The parser's
  node-level reconciliation must balance: `Parsed == Emitted + SkippedWithReason + DroppedWithError`.
  An unbalanced reconciliation means a declaration node vanished between counting and dispositioning
  — a silent swallow the compiler cannot see — so it fails the module closed.

These are *records* in the ledger and *gates* in the pipeline: the ledger states the verdict a
consumer can audit; the gate (below) enforces it.

## The plane and its terminal states

Ingestion losses live on the **DEGRADE plane**. Every losable input node — a malformed ABI record,
an unresolved dependency, a withdrawn dependent — becomes one `IngestionLedgerEntry` carrying its
stable identity (`module.kind:symbol`, where symbol is the USR/mangled name or the `<absent>`
sentinel so two malformed nodes never collapse onto one identity), the plane and precise cause, the
disposition the policy chose, and human-readable evidence. Each entry terminates in exactly one
state:

| Status | Meaning |
|---|---|
| `Retained` | Bound after all — recorded for completeness, no loss. |
| `Quarantined` | Omitted from the binding, tombstoned, and reported; **the binding still shipped**, with proof its withdrawal closure is complete. |
| `Dropped` | A recorded loss with no proven withdrawal closure (the legacy fail-open drop channel). |
| `Fatal` | The loss failed the module before emission. A published binding never carries one. |

The malformed-record shape the plane exists for is a bindable type (struct/enum/class/protocol,
non-ObjC-rooted) whose load-bearing Swift mangled name is absent: it is `IsIngestionQuarantined`,
withheld from the `TypeDatabase`, and the proven-closure walk
(`IngestionQuarantineClosure.Compute`) either withdraws it **and every structurally- or
signature-reaching dependent** — so no retained declaration is left referencing a withheld type — or
fails the module closed when that closure cannot be proven complete. Structural reach covers
superclass, protocol inheritance, conformance, stored-field layout, and enum associated-value
payload; signature reach covers methods, operators, subscripts, and property accessors as leaf
withdrawals so healthy siblings survive. Cross-module reach is included: a protocol quarantined in a
*dependency* module seeds the primary closure's reachability walk and is filtered out of the
dependency stash, so a primary construct inheriting a malformed dependency protocol withdraws through
the seam rather than emitting against the malformed record by name.

## The gates

Three fail-closed gates enforce the contract; each is a soundness condition the compilers cannot see
(the only justification for a hand-coded prediction gate under the roadmap's prediction-gate freeze
policy):

- **SWIFTBIND119** — input-closure preflight: a required input (a declared dependency module) is
  missing, so the graph cannot be closed. Fails early with a structured obligation, before any
  artifact is produced.
- **SWIFTBIND120** — ingestion closure unprovable: a malformed type's withdrawal closure cannot be
  proven complete, so the type cannot be safely degraded. Fails the module closed. On this path the
  optimistically-`Quarantined` ledger entries are escalated to `Fatal`/`ReportOnlyFatal`, so the
  in-memory ledger never reports a tombstoned-but-shipped withdrawal for a binding that never
  shipped.
- **SWIFTBIND121** — parse-balance / no-unledgered-loss: the node-level reconciliation does not
  balance, meaning a declaration was lost outside the emitted/skipped/dropped buckets. Runs after the
  report and manifest are written (so the honest artifact survives) and fails the module closed. It
  is zero-regression on healthy input — the invariant always holds today — and exists to catch a
  future regression that silently narrows the surface.

## The auditable record: ledger → manifest

The structured ingestion ledger is projected onto the binding artifact manifest's input-resolution
section, so a consumer of a degraded binding can read exactly which declarations it is missing and
why **from the manifest on disk**, not by scraping the log. Each projected row carries the node's
identity, declaring parent, plane, cause, the type it reaches, disposition, terminal status, and
evidence; the section carries per-status counts (quarantined / dropped / fatal) and escalates its own
status to `Fatal` if any fatal entry is present, `Warning` on any quarantine/drop/degradation, else
`Success`. The projection is *total* — a fatal is never silently absent even though a fatal run does
not publish — which is what lets CI and downstream tooling treat the manifest as the authoritative
account of a binding's input losses.

## Standing residual

The contract hardens what happens *once an input graph is produced*. It does not convert to green the
input-side production of that graph in the first place: convert-stage failures (upstream
swiftinterface/ABI production) and cross-module symbol-graph/metadata resolution remain corpus
residuals, tracked in `not-planned.md` under the ingestion honest-red residual families row. Those
reopen only when the owner scopes a follow-on run at them or a library in one of those clusters
becomes a release blocker.

## Where it is proven

- **Unit:** `IngestionQuarantineClosureTests` (the proven-closure withdrawal policy across every
  structural and signature edge, cross-module seeding, and fail-closed on an unmodeled residual),
  `PublicationObligationLedgerTests` (the graph-closed and no-unledgered-loss obligations, honest
  verdicts), `InputResolutionReportTests` (the ledger collector and the quarantine→fatal escalation),
  `BindingArtifactManifestTests` (the ledger's manifest projection and per-status counts).
- **End-to-end:** the IngestionKitchen BindingTests gate — leg 1 (closed graph binds), leg 2
  (SWIFTBIND119 on a missing transitive, no artifacts), leg 3 (single-module proven-closure
  quarantine, with the manifest ledger projection asserted), leg 4 (cross-module
  dependency-protocol quarantine, healthy controls byte-stable).
