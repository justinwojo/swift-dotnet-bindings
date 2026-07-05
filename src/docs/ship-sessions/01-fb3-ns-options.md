# Session 1 — FB-3: bridge ObjC `NS_OPTIONS` bitmasks into the Swift type database

## Context

This repo generates C# bindings from compiled Swift/ObjC libraries. Pipeline:
Parser → TypeDatabase → Marshaler → Emitter. Read `CLAUDE.md` first — its build/test
targets, zero-regression policy, and "no shortcuts / root-cause fixes" rule are binding.

This session is one item from the standing ship-readiness doc
`src/docs/facebook-maplibre-remaining-work.md` (see its **FB-3** section and the
**Facebook — measured skip accounting** table for fuller context). Batch A (the W-1 pure-ObjC
umbrella fixture + FB-1 enum property/case rename) already landed in commit `da0cb117`; do not
redo it.

**Scope is FB-3 only.** Do not attempt FB-2, FB-1b, or any V-* verification — those are
separate sessions / interactive follow-ups.

## The problem

Every `Share*Content` type drops `validate(options: ShareBridgeOptions) throws` as
`UnsupportedSignature` ("unsupported placeholder type"). The signature itself is clean (no
`[String:Any]`). The block: `ShareBridgeOptions` is an ObjC **`NS_OPTIONS`** bitmask
(`NS_SWIFT_NAME(ShareBridgeOptions)`) that never gets a Swift type record in a mixed binding,
so it degrades to `Swift.AnyType` and the whole method is dropped
(`MethodHandler.cs` ~1448-1459, via `MethodSignature.ContainsPlaceholder` at
`MethodSignature.cs` ~138-140).

The Clang parser **already fully extracts it**: `ClangAstParser.cs` (~590-654) produces
`ObjCEnumDecl { IsOptions=true, Cases, UnderlyingType }` — the same data shape as `NS_ENUM`.
It is `ObjCBridgeRecordFactory` that *explicitly* excludes it today:
`if (enumDecl.IsOptions) continue;` (`ObjCBridgeRecordFactory.cs` ~107-108, with the design
comment at ~48-49 / ~90-91).

Verify these line numbers with Grep before editing — they are approximate and the tree moves.

## Deliverable

Add the `NS_OPTIONS` bridge as the **direct sibling** of the existing `NS_ENUM → SimpleEnum`
bridge (same file) and the `NS_TYPED_EXTENSIBLE_ENUM` bridge that landed in commit `be5b70f8`.
This is an established, low-risk pattern — study those two paths first and mirror them.

1. New `IsOptions == true` branch in `ObjCBridgeRecordFactory` that synthesizes a type record
   for the bitmask, reusing the same raw-value round-trip the `SimpleEnum` path already uses.
2. Companion C# emission as a `[Flags]` enum over the bitmask's underlying integer type, e.g.
   `[Flags] public enum ShareBridgeOptions : nuint { Default = 0, PhotoAsset = 1 << 0, … }`.
   Use the parser's `UnderlyingType`/`Cases` — do not hardcode member names.
3. Marshal the parameter/return through the raw-value round-trip (reuse the `SimpleEnum`
   mechanism; do not invent a new marshalling path).

**Payoff.** Unblocks all 8 `validate(options:)` methods + `_ShareUtility.validateShareContent`,
and generalizes to any mixed binding using `NS_OPTIONS` (very common in ObjC).

**Knock-on to VERIFY, not promise.** The two Review-tier EveryProtocol proxies `SharingContent`
/ `SharingValidatable` were skipped precisely because `ShareBridgeOptions` had no Swift type-DB
record. Giving it one *may* flip that predicate and recover their proxies — check after the fix
lands, but the proxy path may have other gates, so don't design toward it or claim it up front.

## Tests (required — new work ships with tests)

BindingTests is the real end-to-end gate here (this is an ABI/marshalling change). Add a mixed
(ObjC + Swift) fixture — the harness already has mixed-fixture machinery (see the ObjC companion
paths and the existing `NS_ENUM`/`NS_TYPED_EXTENSIBLE_ENUM` fixtures) — that declares an
`NS_OPTIONS` typedef consumed by a Swift method `validate(options:)`. Assert:
- the `[Flags]` enum round-trips (individual flags + a combined mask), and
- the previously-dropped method is reachable and behaves.

Add an emitter/unit test for the new `ObjCBridgeRecordFactory` branch (mirror the coverage the
`SimpleEnum`/`NS_TYPED_EXTENSIBLE_ENUM` branches already have). Assert behavior, not exact
generated strings.

## Validation (hard gates — must be green ≥ baseline before you commit)

1. `nuke test` — unit tests; `swift_bindings_unit_pass` ≥ the floor in
   `build/baselines/validation-baseline.json`.
2. `nuke binding-tests` — default iOS Simulator (Mono JIT); regenerates + compiles + runs. Pass
   count ≥ the `runtime_tests.simulator.pass` baseline (currently 3141), 0 fail.
3. `nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54` — physical
   iPhone (NativeAOT). **Required** — this is a marshalling change and Mono/NativeAOT have
   different bugs. Your first `--device` run must NOT use `--skip-regen` (the device build needs
   its own NativeAOT regeneration).

Recommended canary (optional, not a hard gate): a `nuke validate` sweep — FB-3 touches the
shared ObjC-bridge/type-DB path many libs exercise. If you run it, note that validate dirties
~8 `-behaviortier` version-stamp files; `git checkout` those but **keep** the updated
`build/baselines/validation-baseline.json`. Only treat a drop in `cs_compile`/`swift_compile`
below baseline as a real regression.

## Guardrails

- Root-cause fix, not symptom suppression. Do not weaken assertions or `[Skip]` a failing test
  to go green. If the documented scope is insufficient, flag it in your summary.
- When you touch the bridge factory, grep for **all** `IsOptions` references so no sibling path
  (e.g. classification, filtering, size-parity guards) is left inconsistent.
- Keep it a general `NS_OPTIONS` fix, not an FBSDKShareKit-specific hack.
- After the generator change, confirm regenerated output compiles (the `nuke binding-tests`
  regen does this) — do not assume.
