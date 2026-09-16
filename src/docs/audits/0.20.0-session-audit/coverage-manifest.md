# Coverage manifest

## Historical range

`d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81`

The audit walked all 11 commits and their combined 190-file diff. It traced the
following shared surfaces through their consumers:

- canonical declaration/recovery identity;
- withdrawal evidence, staged promotion, and release-gate receipts;
- surface-accounting request, capture, syntax/native/origin joins, comparison,
  schema, and writer contracts;
- optional `any Error` and class return ABI rendering;
- generic value-property metadata and witness-table flow;
- method-level generic opening, refusal, metadata, and result lifetime;
- closure carrier and setter ownership;
- concrete specialization admission;
- constrained-extension marker handling;
- packed runtime-native exports and all six required XCFramework slices;
- affected BindingTests fixtures, C# tests, baselines, and release documentation.

The detailed per-commit and adversarial-gate coverage is retained in
[findings.md](findings.md). Grok round 2 states that the original requested scope
was complete after its remaining parser/config and fixture/docs/baseline groups.

## Stage B change surface

The final batch changes these implementation areas:

- Surface Accounting models, scanner, engine, schema, Nuke target, documentation,
  fixtures, and tests.
- Runtime native-export qualification and withdrawal/promotion validation tests.
- CSM conformer parsing and optional-class method-generic return rendering.
- Synthesized property/subscript accessor raw generic-signature preservation.
- The scoped Swift.Bindings unit-test floor.
- This durable audit archive.

Unrelated P3 exposure-reporting files and the non-Stage-B hunks in the two mixed
files (`Build.WithdrawalTests.cs` and `validation-baseline.json`) were excluded by
staging only the audit-owned patches.

## OWNER-02 resolution surface

The 2026-09-14 owner follow-up removed the A-07 prediction paths from
`MemberValidationPipeline`, synchronous concrete specialization admission, and the
f292 lossless constrained-extension member check. It also narrowed
`SwiftWrapperPostProcessor` back to deterministic placeholder cleanup, retaining
the old internal/unavailable sub-cause names only as zero-valued schema fields.

Coverage crosses the consumer boundary rather than stopping at predicate-unit
tests: Xcode 26.3 captures for `Foundation.NSInvocation`, an internal CSM conformer,
and a marker-constrained extension feed the real diagnostic parser, attributor, and
recovery controller. Each capture names one owning leaf, converges after that leaf
is denied, and leaves a healthy sibling untouched. Emitter tests separately prove
the three candidate families are admitted into that path. The compile-only
ResilienceKitchen hostile/control gate additionally exercises the two constrained-
extension spellings against swiftc: both are attributed to `!leaf-api`, the two
existing IUO properties remain `!accessor-group`, all four withdrawals have null
cascade, generated C# compiles, and 110 public declarations plus nine named healthy
siblings are identical to the control. This owner follow-up was not sent through
Claude review. Grok final review session `01a09e63-dfde-7243-8c0b-e49458fb4e22`
covered the complete frozen candidate and found no Critical, High, or Medium defect.
Its one Low documentation inconsistency was accepted and repaired without re-review,
as required by the Low-only stopping rule.

## Review coverage

- Round 1 reviewed the historical range and initial Stage B candidate.
- Round 2 resumed Grok session `01a09d89-173c-75d2-b31d-dd49e6a1b147`, verified
  every accepted Round 1 repair, and closed the historical groups not completed in
  Round 1.
- Round 2's reviewed staged fingerprint was
  `4cf00a78e300676c1ce6808a1f8c8958e5fe73d22158904ba781f1a9c39c457a`.
- The only post-round-2 code delta is the adjudicated Low repair that copies
  `accessor.GenericSig` into the four synthesized accessor `MethodDecl` objects,
  plus its regression test and test-floor increment.
- The later OWNER-02 candidate was reviewed independently by Grok session
  `01a09e63-dfde-7243-8c0b-e49458fb4e22` against staged fingerprint
  `0bf96a12d96e2bdf7b9b7c32d429736de205397f813b9c4f3211a5378f25ab5d`.
  Its sole Low finding produced the final recovery-note trigger edit; no code or
  baseline changed after that frozen review.

## Deliberate limits

- The full 132-target frozen-corpus SurfaceAccounting comparison was not rerun
  because Stage B did not receive fresh old/tip capture roots. Missing proof still
  fails promotion closed; OWNER-03 remains open.
- No device or package publication action was taken.
- Unchanged baseline JSON cells were not exhaustively re-reviewed; changed rows and
  their fixture/test pairings were.
- P3 exposure-reporting work was neither reviewed nor altered.
