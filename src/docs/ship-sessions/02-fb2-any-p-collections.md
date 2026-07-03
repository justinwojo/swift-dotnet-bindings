# Session 2 — FB-2: collections of `any P` (`[any AppLinkTargetProtocol]`)

## Context

This repo generates C# bindings from compiled Swift/ObjC libraries. Pipeline:
Parser → TypeDatabase → Marshaler → Emitter. Read `CLAUDE.md` first — its build/test targets,
zero-regression policy, and "no shortcuts / root-cause fixes" rule are binding.

This session is one item from the standing ship-readiness doc
`src/docs/facebook-maplibre-remaining-work.md` (see its **FB-2** section). The previous session
(FB-3, `NS_OPTIONS` bridge) should already be committed — read `.agent/phase-1-summary.md` for
what it changed and any flags it left. Batch A landed earlier in commit `da0cb117`.

**Scope is FB-2 only.** Do not attempt FB-1b or any V-* verification.

## The problem

`any P` in a *direct* parameter position is already supported. `Array<any P>` — a bound generic
whose element is an existential — is dropped as `UnsupportedExistential`. The
`ExistentialHandler` gate `HasUnsupportedObjCProtocolExistentialPosition` rejects it today.

**This item is NOT code-path-scoped for you** — unlike FB-3, do not treat the file:line pointers
as a finished map. **Investigate the `ExistentialHandler` gate and the projection/container
marshalling paths first** (use an Explore subagent), understand exactly why bound-generic element
position is rejected while direct position is allowed, and design the fix before implementing.
Sanity-check the design past Codex and Grok in consult mode (per `/coding-rules`) before you
write code — this touches projection + container marshalling and has real blast radius.

**Evidence (consumer-facing, ~6 of 12 skips).** `AppLink.targets` / `.init` / `.appLink`,
`AppLinkFactory.createAppLink`, `AppLinkNavigation.navigationType`, `ShareMediaContent` — all
report *"Bound generic contains existential type argument 'any …Protocol'."* (The rest are
internal `_BridgeAPI*` / `AEMReporter` and out of scope.)

## Deliverable

Extend existential support to **bound-generic element position** — `Array<any P>` and
`Dictionary<…, any P>` — in both **reverse dispatch** (Swift calling back into a C# conformer)
and **forward projection** (C# calling Swift). One general fix, reusable across libraries; the
FB feature itself (App Links deep linking) is niche — the *fix* is the value.

Cover both an **ObjC** protocol existential and a **Swift** protocol existential as the element
type (they route differently). Respect the existing existential-container size-parity guards —
element filtering must not desync a container's element count from its declared size.

## Tests (required — new work ships with tests)

BindingTests is the real gate (ABI/marshalling change). Add a fixture with a method that takes
**and** returns `[any P]` — one case for an ObjC protocol element, one for a Swift protocol
element. Assert a **heterogeneous** collection (≥2 distinct concrete conformers) round-trips
through both the forward and reverse-dispatch directions, preserving per-element identity/behavior.

Add unit coverage for the `ExistentialHandler` gate change (the projection-parity visitors are
compile-time exhaustive — if you add an `ITypeProjection`, implement every `IProjectionVisitor`
arm, or the build breaks; see `.claude/rules/constraints.md` "Projection parity pattern").

## Validation (hard gates — must be green ≥ baseline before you commit)

1. `nuke test` — unit tests; ≥ the `swift_bindings_unit_pass` floor in
   `build/baselines/validation-baseline.json`.
2. `nuke binding-tests` — default iOS Simulator (Mono JIT); regenerates + compiles + runs. Pass
   count ≥ the `runtime_tests.simulator.pass` baseline, 0 fail.
3. `nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54` — physical
   iPhone (NativeAOT). **Required** — existential/container marshalling is exactly where Mono and
   NativeAOT diverge. First `--device` run must NOT use `--skip-regen`.

Recommended canary (optional, not a hard gate): `nuke validate` — FB-2 touches shared
projection/existential paths with broad blast radius, so a real-world sweep is worth it here. If
you run it, `git checkout` the ~8 `-behaviortier` version-stamp files it dirties but **keep** the
updated `build/baselines/validation-baseline.json`; treat only a `cs_compile`/`swift_compile`
drop below baseline as a regression.

## Guardrails

- **Investigate before implementing** (restated because it matters): the existing gate exists for
  a reason; understand it before lifting it, or you will trade one crash for another.
- Root-cause fix, not symptom suppression. No weakened assertions, no `[Skip]` to force green.
- When you lift the gate, grep for **every** `HasUnsupportedObjCProtocolExistentialPosition` (and
  sibling existential-position guards) so no path is left half-lifted.
- Keep it general (Array/Dictionary of `any P`), not an App-Links-specific hack.
- Confirm regenerated output compiles — the `nuke binding-tests` regen does this; don't assume.
- If you discover the fix is materially larger than "lift the gate + wire container element
  projection" (e.g. it needs new runtime existential-container plumbing), land the coherent
  subset that passes all gates with tests, and document the remainder as a scope flag in your
  summary rather than forcing a half-finished change.
