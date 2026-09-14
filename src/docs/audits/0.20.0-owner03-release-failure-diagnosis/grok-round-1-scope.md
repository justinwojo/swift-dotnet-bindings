# OWNER-03 diagnosis ledger — Grok review scope

## Exact review scope

Review the new diagnosis-only artifact:

- `src/docs/audits/0.20.0-owner03-release-failure-diagnosis.md`

No product code or baseline value is changed. The ledger must accurately diagnose the
remaining P8 downstream red signatures and the `4,030/4,061` Mono-AOT baseline mismatch,
assign ownership, choose the best long-term consumer fix, name dependencies and focused
validation, and order independently shippable future batches.

## Repository state

- review checkout HEAD: `7fbba4b862aa3c5c3d2f972673d61659e50ad47d`;
- `swift-dotnet-packages` inspected at
  `9061316e33084709b2fa6ab5631b44ffa2f6df72`, clean;
- `internal-binding-testing` inspected read-only at
  `69f80c4996160f952408e882b3b8396398de92b1`, with 873 pre-existing changes;
- before review, the only task-owned workspace path is the new audit artifact tree.

## Evidence authority

The preserved P8 authority is under
`/Users/wojo/Dev/swift-bindings/src/docs/sessions/0.20.0/execution/P8/final-q1`.
The ledger records SHA-256 values for the qualification receipt, both adjudications,
and both structured downstream receipts. Representative cell logs and generated API
artifacts live below that tree.

Focused extra evidence:

- KeychainAccess fresh generation/build under `/private/tmp/owner03-keychain-current`
  reproduced the 16 CS1503 consumer errors and showed the two collision-safe factories;
- the exact existing baseline unit test reproduced expected `4030`, actual `4061` at
  `RuntimeBaselinePlatformKeyTests.cs:140`;
- a focused current CryptoKit downstream rerun was blocked at preflight because the old
  local `SwiftBindings.Sdk.0.20.0.nupkg` is no longer present; preserved P8 execution is
  therefore the runtime authority.

## Acceptance checklist

1. Every P8 downstream failure identity is present exactly once or deliberately grouped
   only when one root contract explains it.
2. Every row classifies root cause and owning repository without converting a red,
   timeout, crash, absent prerequisite, or unknown result into a pass.
3. Every proposed repair favors the generic root cause. Any bounded mitigation must be
   safe and must require explicit future-work documentation under `src/docs`.
4. Every row names an exact `swift-bindings` unit/BindingTests regression home and a
   focused gate that would catch the signature; downstream-only validation is rejected.
5. Proposed test homes and existing-coverage claims agree with the checked-out source.
6. Dependencies and batch ordering are coherent and each batch labeled independently
   shippable actually has its own regression and owner-repository completion criteria.
7. D02 stale `errorOut`, D06 existential closure forwarding, and D09 Apple metadata
   rooting are supported by evidence and not over- or under-scoped.
8. D01/D03/D07/D08 environment or fixture diagnoses retain fail-closed behavior and do
   not mistake absence of binding evidence for proof of binding correctness.
9. D04/D05/D10 do not recommend reverting safety semantics merely to restore source
   compatibility.
10. D11 provides a first-party regression contract rather than accepting a downstream
    shell-only test.
11. D12 records both pass and skip drift (`4030/33` versus `4061/32`) and does not imply
    that fixing counts accepts the nine x20 clobbers.
12. The ledger explicitly carries forward corpus 24 nonzero/36 absent and Mono x20
    40 safe/2 spilled/9 clobber/1 no_transition blockers.
13. The artifact is diagnosis only: no implementation, acceptance, publication, merge,
    or push claim.

## Repository guidance and reviewer guardrails

Apply `AGENTS.md`, matching scoped rules if any, `CLAUDE.md`, and
`/Users/wojo/.claude/review-notes/swift-bindings.md`. In particular:

- Nuke kebab-case targets are valid.
- Generator C# is not generated code.
- Runtime crashes are our bug until the ABI is disproved; this ledger assigns TipKit to
  the launcher only because the preserved crash occurs before managed entry.
- Generated `bin`, `obj`, and BindingTests output are evidence/build products, not source.
- Existing guard coverage must be verified before calling a proposed test redundant.
- The external reviewer is advisory and read-only. Do not edit files, mutate repositories,
  run destructive commands, launch nested reviewers, or broaden beyond the exact scope.

## Requested output

Return a concise correctness review with severity-ranked actionable findings and exact
file/line evidence. Distinguish proven defects from uncertainties or optional polish.
If clean, say explicitly that no actionable findings remain. Do not implement fixes.
