# Ingestion contract

The input edge aims to produce a sound binding with an auditable account of input losses, or fail
closed with a reason. This is the input-side complement to
[binding resilience](binding-resilience-design.md), which governs emission recovery through a
disabled-unit set. The intended contract is that no input loss or deformation is silent. Current
closure checks and declaration accounting enforce important parts of that contract; they do not
prove complete parse fidelity or exhaustive ledger coverage.

## Current enforcement

`PublicationObligationLedger` records the input-graph and parse-accounting verdicts. The pipeline
checks required dependencies and quarantined-type reachability before emission, then reconciles
recognized declaration nodes:

`Parsed == Emitted + SkippedWithReason + DroppedWithError`

An imbalance means a counted declaration lacks a disposition and fails the module. A balanced count
alone says nothing about fidelity within a retained declaration: a degraded type specification can
remain in the emitted bucket. Best-effort auto-detected dependencies can also degrade with a recorded
loss; they are distinct from required inputs whose absence fails closure.

Three fail-closed gates implement these checks:

| Gate | Condition and result |
|---|---|
| **SWIFTBIND119** | Input-closure preflight cannot satisfy a required module. Fails before binding emission, with a structured failure reason. |
| **SWIFTBIND120** | The modeled withdrawal closure for an ingestion-quarantined type cannot be proven complete. Fails before emission and escalates optimistic `Quarantined` entries to `Fatal` / `ReportOnlyFatal`. |
| **SWIFTBIND121** | Declaration accounting does not balance. Fails after the report and manifest are written, preserving those diagnostic artifacts. |

These checks address conditions compilation alone cannot establish. Compile-catchable failures belong
to verify-recover under the [prediction-gate freeze policy](engineering-policies.md#prediction-gate-freeze-policy-hard-policy-boundary).

## Quarantine and the ingestion ledger

Recorded ingestion losses live on the **DEGRADE plane**. An `IngestionLedgerEntry` carries input
identity, declaring parent, cause, reached type, disposition, terminal status, and evidence.

| Status | Meaning |
|---|---|
| `Retained` | Retained without a loss for this entry. |
| `Quarantined` | Withdrawn and reported under the modeled proven-closure policy, allowing the remaining binding to ship if subsequent gates pass. |
| `Dropped` | A recorded loss without a proven withdrawal closure, the legacy drop channel. |
| `Fatal` | The input loss prevents publication. |

The quarantine path handles a bindable, non-ObjC-rooted struct, enum, class, or protocol whose
load-bearing Swift mangled name is absent. The parser marks it `IsIngestionQuarantined` and withholds
it from the `TypeDatabase`. `IngestionQuarantineClosure.Compute` withdraws the type and dependents
reachable through modeled edges, or returns an unproven result that triggers SWIFTBIND120.

Modeled structural reach includes superclass, protocol inheritance, conformance, stored-field layout,
and enum associated-value payloads. Signature reach includes methods, operators, subscripts, and
property types; leaf withdrawals preserve healthy siblings. Directly quarantined dependency types
seed the primary-module walk, and quarantined dependency protocols are filtered from the protocol
stash. `ProvenComplete` is the walk's verdict over these modeled edges, subject to the coverage limits
below.

## Durable diagnostics

When generation reaches manifest emission, recorded ingestion entries project into the binding
artifact manifest's input-resolution section, including per-status counts. The section becomes
`Fatal` for a fatal entry, `Warning` for quarantine/drop/degradation, otherwise `Success`. This is an
audit of recorded losses, not proof that every possible deformation has a row.

Early failures do not necessarily produce a manifest. In particular, SWIFTBIND120 updates the
in-memory ledger and writes `binding-failure-report.json` before returning without a manifest.
SWIFTBIND121 runs after report/manifest emission and writes a failure report as well; the existence of
those artifacts does not mean generation succeeded. Consumers must honor the generation result and
failure diagnostics.

## Known coverage limits

The following deferred evidence qualifies the intended contract; it does not authorize further work:

- [Retained parser deformations](../Future/notes/ingestion.md#retained-parser-deformations-bypass-the-ledger): prefix parsing and enum-payload placeholders can retain a degraded specification without an ingestion entry. Node balance does not detect these losses of fidelity.
- [Transitive dependency-protocol quarantine](../Future/notes/ingestion.md#transitive-dependency-protocol-quarantine-degrades-outside-the-ledger-soundness-safe-not-a-soundness-hole): dependency-internal inheritance is outside the direct seed walk; some sound surface reductions lack withdrawal rows.
- [Constrained-extension property accessors](../Future/notes/ingestion.md#constrained-extension-property-accessors-are-outside-the-closure-completeness-proof): accessor-generic edges are outside the property-type walk, so safe downstream suppression can occur outside the ledger.
- [Repeated in-process generation](../Future/notes/ingestion.md#ambient-threadstatic-ledger-is-not-reset-by-the-public-generatebindings-entry-point): the CLI resets the ambient collector, but the public generation entry point does not provide the same reset boundary.

Upstream swiftinterface/ABI production and cross-module symbol-graph/metadata resolution have
separate [corpus residuals](../Future/notes/recovery.md#ingestion-honest-red-residual-families-soak-cause-tally-d-r7-od-w2-3).
The ingestion gates do not make those conversion paths succeed.

## Existing validation

Unit coverage includes `IngestionQuarantineClosureTests` for modeled structural/signature reach,
cross-module seeding, and unproven closure; `PublicationObligationLedgerTests` for obligation verdicts;
`InputResolutionReportTests` for collection and quarantine-to-fatal escalation; and
`BindingArtifactManifestTests` for projection and counts.

The IngestionKitchen gate runs in `nuke binding-tests --compile-only`: leg 1 binds a closed graph;
leg 2 checks missing-transitive SWIFTBIND119 and absence of binding artifacts; leg 3 checks
single-module quarantine and manifest rows; leg 4 checks direct dependency-protocol quarantine and
byte-stable healthy controls. These fixtures establish the exercised cases, not universal coverage
of all malformed inputs.
