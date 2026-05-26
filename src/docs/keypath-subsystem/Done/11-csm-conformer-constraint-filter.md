# Commit C — CSM defensive conformer-constraint filter

Part of Session 6 (`MusicLibraryRequest<T>` re-enablement). Commit C closes
the engine-pollution leak that drove the MusicKit×4 validation reds
(`MusicCatalogChartsRequest`, `MusicCatalogSearchRequest`,
`MusicLibrarySearchRequest`, `MusicRecentlyPlayedRequest`). It does **not**
change any consumer-visible signature on its own — Commit D wires the
MusicKit binding on top.

## Pre-fix bug

`ConcreteSpecializationEngine.FindSpecializableProtocolConstraint` picks **one**
protocol per generic parameter:

```text
for conformance in param.GenericConformances:
    if Kind != Protocol: continue
    if IsUnsupportedProtocolConstraint(conformance.target):
        return conformance.target              # PAT/Self requirement wins
    if anyProtocolWithConformers == null
       && GetConformers(conformance.target).Count > 0:
        anyProtocolWithConformers = conformance.target
return anyProtocolWithConformers               # else first with conformers
```

When the param carries multiple protocol constraints, the selected protocol's
conformer set is necessarily a **superset** of the legal intersection. The
engine then emits one CSM overload per conformer in that superset — including
conformers that fail the non-selected constraints. The Swift wrapper compiles
each overload against the **full** `where` clause, so the conformers that
satisfy only the selected constraint trigger
`type 'X' does not conform to 'Y'` at wrapper compile time. The C# side
already emitted; the build fails when Swift links.

`MusicRecentlyPlayedRequest<MusicItemType where MusicItemType : MusicRecentlyPlayedRequestable, MusicItemType : Decodable>`
made this manifest. The engine selected one of the two constraints, then
admitted every conformer of that constraint into the cartesian. The conformer
set widened from the legal 5 (the types declaring `MusicRecentlyPlayedRequestable`:
`MusicVideo`, `Song`, `Station`, `Track`, `RecentlyPlayedMusicItem`) to all 22
types declaring the selected constraint — `Album`, `Artist`, `Genre`,
`Playlist`, `RadioShow`, `RecordLabel`, etc. were emitted as CSM
specializations they cannot legally satisfy.

## Fix

A pairing-step intersection filter on top of
`FindSpecializableProtocolConstraint`'s single-protocol selection. The engine
already had the data it needed: `_abiDeclaredProtocolsByType` records the
protocols a type declares directly, and `ProtocolChainContains` walks
`ProtocolDecl.InheritedProtocols` transitively. The fix wires both into a new
helper:

- **`CollectAllProtocolConstraints(param)`** — enumerates every
  `Kind == Protocol` conformance on the generic param's
  `GenericConformances`. Same-type couplings (`Kind != Protocol`) and
  malformed entries (empty target name) are skipped.
- **`ConformerSatisfiesAllConstraints(conformer, allConstraints, selected, out missing)`** —
  for each constraint other than the engine's `selected` choice, runs
  `VerifyHintAgainstAbi(conformer.SwiftQualifiedName, constraintKey)`. The
  existing three-state result (`Confirmed`, `Uncertain`, `Disproved`) maps to:
  - `Confirmed` / `Uncertain` → admit. `Confirmed` means the ABI walk hit
    the constraint via the declared protocols' inheritance chain.
    `Uncertain` means the conformer is not indexed in the current module
    or the chain walks into an unindexed protocol from a plausible-refiner
    module — we lack ground truth, fail open rather than reject a legal
    conformer.
  - `Disproved` → reject. The conformer's declared protocols and their
    InheritedProtocols chains do not include the constraint.

The filter is applied at both pairing sites:

1. `FindSpecializableMethods` — method-own generic params. The previous
   `usableConformers` LINQ filter chain is rewritten as an explicit loop
   that records rejections on `_rejectedPairings` before discarding the
   conformer.
2. `ResolveParentSpecializableParams` — parent-type generic params. Same
   shape; the existing `ClassifyConformerStructurally` parent-only structural
   guard is preserved alongside the new constraint check.

## Visibility — `binding-emission-report.json`

Rejected pairings are surfaced via a new `csmConformerRejections` field on
`EmissionReport`. The engine exposes `RejectedPairings` (a
`HashSet<CsmRejectedPairing>` that accumulates across every
`FindSpecializableMethods` / `ResolveParentSpecializableParams` call within
the module's run). `EmissionReportEmitter.BuildReport` reads the engine via
`emissionContext.SpecializationEngine` and projects each pairing into a
serializable entry:

```json
{
  "parentType": "MusicKit.MusicRecentlyPlayedRequest",
  "genericParam": "MusicItemType",
  "selectedProtocol": "MusicKit.MusicRecentlyPlayedRequestable",
  "conformer": "MusicKit.Album",
  "missingConstraint": "Swift.Decodable",
  "reason": "conformer does not satisfy non-selected protocol constraint per current-module ABI"
}
```

Entries are sorted by `(parentType, genericParam, conformer, missingConstraint)`
for deterministic output. No new diagnostic code is added — `SB0002` continues
to flag silent tombstones; CSM rejections are a separate audit surface
(consumers reading the report see exactly which conformers were filtered out
and why, without needing to grep generator logs).

## Why intersection, not single-protocol broadening

A simpler alternative would have been to teach
`FindSpecializableProtocolConstraint` to enumerate **every** PAT/Self protocol
on the param rather than the first. The intersection filter is the right
shape because:

- The engine still needs a **single** anchor protocol to drive the
  per-conformer extension class naming and the receiver-type closure. CSM's
  emission model is one extension class per conformer of the selected
  protocol; broadening selection to a tuple of protocols would explode the
  cartesian and require redesigning the extension shape.
- Conformer membership in the **selected** protocol's set is the
  precondition for the wrapper to even mention the conformer. The filter
  refines that set against the remaining constraints without changing the
  emission shape.

## Test surface

`BindingTests/Sources/SwiftBindingsTestLib/Generics/PatBagConformerMismatch.swift`
mirrors the MusicKit shape in miniature:

- `protocol PermittedSlot { associatedtype Slot }` — PAT, picked by the
  engine as the selected constraint.
- `protocol Permitted {}` — marker, declared as the second constraint on
  the generic.
- `PermittedString: PermittedSlot, Permitted` / `PermittedInt: PermittedSlot, Permitted` —
  admitted conformers.
- `SlotOnlyDouble: PermittedSlot` — must be rejected by the filter.
- `struct PermittedBag<Item: PermittedSlot & Permitted>` — the multi-constraint
  parent.

`BindingTests/RuntimeTestsApp/Generics/PatBagConformerMismatchTests.cs`
exercises the admitted conformers via runtime CSM calls (Bump / Read
round-trip). The mere existence of these tests is the structural regression
gate: without the filter, the Swift wrapper would attempt to compile a
`PermittedBag<SlotOnlyDouble>` specialization, which fails Swift type-checking
because `SlotOnlyDouble` does not satisfy `Permitted`.

File-level verification (established by the design but not asserted at
runtime):

- Generated `SwiftBindingsTestLib.cs` contains
  `PermittedBagPermittedStringCsmExtensions` and
  `PermittedBagPermittedIntCsmExtensions`.
- Generated `SwiftBindingsTestLib.cs` does **not** contain
  `PermittedBagSlotOnlyDoubleCsmExtensions`.
- `output/binding-emission-report.json.csmConformerRejections` lists a row
  with `conformer: "SwiftBindingsTestLib.SlotOnlyDouble"`.

## Risks

- **Risk A (Uncertain admits too much)** — when the conformer comes from a
  module other than the one being indexed, `VerifyHintAgainstAbi` returns
  `Uncertain`, which the filter treats as admit. The MusicKit×4 reds will
  green because every conformer in question IS indexed in the MusicKit
  module being generated, so `VerifyHintAgainstAbi` returns `Confirmed` or
  `Disproved`, never `Uncertain`. Cross-module conformers (e.g. a hint
  declaring `SomeUserType` against a stdlib protocol) continue to admit
  conservatively — same behavior as the existing hint-vs-ABI check.
- **Risk B (parser doesn't populate `InheritedProtocols`)** — if a
  conformer declares a refining protocol that the parser doesn't surface
  inheritance for, the transitive walk gives `Disproved` for the parent
  constraint and the filter rejects what should have been admitted.
  Mitigation: same backstop as the hint-vs-ABI check — `Uncertain` is
  returned when the chain reaches an unindexed protocol from a
  plausible-refiner module, which lets the filter admit. The MusicKit×4
  shape uses directly-declared protocols (no inheritance chain needed), so
  this risk does not affect the Session 6 target.
- **Risk C (rejection visibility skew)** — `RejectedPairings` accumulates
  across every engine call (including the predicate sites in
  `Sync.cs` / `Async.cs` / `AsyncGenericParent.cs` and the two emitter
  sites). `EmissionReportEmitter.BuildReport` reads the merged collection,
  so the report shows the union — there is no over- or under-counting,
  but a single rejected conformer appears once regardless of how many
  pairing attempts surfaced it.

## Roadmap follow-ups

The Commit C probe surfaced five distinct bug shapes in libraries unrelated
to the engine pollution this commit closes. Each is captured as a roadmap
entry (CS0305 / CS0234 / CS0246 / CS0315 / `@available` propagation gap /
BlinkID `toString` same-type Self == String violation). After Commit D
lands, validate is expected to go from 9 reds to 5, where the 5 remaining
reds match those roadmap entries one-for-one.

## References

- `Marshaler/ConcreteSpecializationEngine.cs` — engine, filter, rejection record.
- `Reporting/EmissionReportEmitter.cs` — `csmConformerRejections` field + projection.
- `Emitter/StringEmitter/ModuleEmissionContext.cs` — `SpecializationEngine` accessor (pre-existing).
- `BindingTests/.../Generics/PatBagConformerMismatch.swift` — regression fixture.
- `BindingTests/.../Generics/PatBagConformerMismatchTests.cs` — runtime exercise.
- `06-musiclibraryrequest-re-enablement.md` — Session 6 overview (Commit C is one of four sequenced commits).
