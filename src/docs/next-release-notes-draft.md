# Release notes — draft (ingestion-hardening cut)

**Draft, for the orchestrator to assemble into the cut's `RELEASE-NOTES.md`.** This holds the
session-09 (ingestion contract) highlights, written to be accurate as-is. Two highlights owned by
sibling sessions are stubbed with a `TODO(orchestrator)` marker — they need the exact details from
the sessions that produced them and must not be invented here.

---

## Highlights

### Bindings degrade soundly or fail closed — never silently narrow

A malformed, incomplete, or partially-unreadable Swift input now produces one of two honest
outcomes: a binding that ships with the unusable surface **withdrawn, tombstoned, and recorded**, or
a **fail-closed** error with an auditable reason. A binding never compiles clean and is wrong at
runtime because of an input the generator could not fully read, and it never drops surface silently.

- **Import-graph preflight (SWIFTBIND119).** A binding whose required sibling modules are not
  present fails before emission with the missing module named, rather than emitting references
  that fail later as opaque `NU1101`/`CS0246`.
- **Proxy-reference integrity backstop (SWIFTBIND122).** Emitted `new {X}Proxy(...)` references
  are reconciled against actually-emitted proxy classes on every path (including the
  single-render no-compile path). A dangling proxy reference now fails the module instead of
  shipping a `CS0246`.
- **Conversion receipts.** `spm-to-xcframework` writes a conversion receipt and the generator
  flags inputs of unknown provenance; multi-module SPM packages get a stricter import-closure
  gate that fails a conversion precisely rather than producing an incomplete xcframework.
- **Input-closure and no-unledgered-loss are now publication obligations.** A binding may publish
  only when its input dependency graph is closed (no required dependency edge left unresolved) and
  every retained declaration was parsed completely. A new fail-closed check (SWIFTBIND121) rejects a
  module whose parser reconciliation does not balance — i.e. a declaration was lost with no recorded
  disposition — rather than shipping a silently-narrowed binding.
- **The ingestion ledger is now on the artifact manifest.** The binding artifact manifest's
  input-resolution section carries a structured row for every withdrawn or dropped declaration —
  identity, disposition, terminal status, and evidence — plus per-status counts. A consumer or CI
  can read exactly which declarations a degraded binding is missing, and why, straight from the
  manifest on disk instead of scraping the log.
- **Fail-closed runs never misreport a withdrawal as shipped.** When a malformed type's withdrawal
  closure cannot be proven complete (SWIFTBIND120), the module fails before emission (writing no
  manifest — the durable record of the failure is the logged SWIFTBIND120 error) and its
  optimistically-quarantined in-memory ledger entries are escalated to fatal, so nothing in the run
  reports a tombstoned-but-shipped withdrawal for a binding that never shipped.
- **Withdrawal evidence names the type it reaches.** A type withdrawn because a stored field embeds a
  malformed type now records that field's type in its ledger evidence, instead of an anonymous `?`.

See `src/docs/ingestion-hardening.md` for the full input contract.

### `[Native]` enum members are now PascalCased (consumer source break — pure-ObjC bindings)

Non-prefix-stripped `[Native]` enum members are now emitted in PascalCase (e.g. `center` →
`Center`). This is cosmetic/ABI-safe — only the underlying integer marshals — but it is a **C#
identifier rename**: pure-ObjC bindings (MapLibre-style) regenerated on the 0.18.0 surface will
show renamed enum members, and **consumers must update their call sites** (`Foo.center` →
`Foo.Center`). Prefix-stripped `EnumNameCaseName`-idiom members are unaffected.

### Issue-05 — void-variant members are skipped

TODO(orchestrator): fill from the session that landed the Issue-05 void-variant handling — what the
void-variant shape is, why it is skipped, and the SWIFTBIND code / skip reason a consumer will see in
the report. (Called out as a documented skip note; details intentionally not invented in this draft.)

---

## Fixed

- **SwiftRichString and similar existential-projection bindings** no longer emit an uncompilable
  reference to a suppressed proxy class (`CS0246 StyleProtocolProxy`). When a type with no Swift
  mangled name (e.g. `Foundation._NSRange`) is quarantined and its reaching protocol methods are
  withdrawn, every suppression path now records the withdrawn proxy so no retained consumer emits
  a reference to it; SWIFTBIND122 backstops the invariant.

---

## Notes for the cut

- RuntimeContract floor: stays **16** per the signed `1.0-decision-record.md` — 0.18.0 is not a
  dispatch-contract break (load gate fails open; raise only on a contract-breaking minor).
- No new consumer-facing platform matrix or tier claims are introduced by the ingestion contract.
