# B04 / D12 implementation and validation receipt

Date: 2026-09-14
State: implementation and authoritative platform validation complete; awaiting the
authorized Grok-only review; no commit created.

## Scope and reproduction

- Reviewed base: `7a40c7e3a879c2af5c623105cffb973946e75098` (detached).
- Scope: one count-and-identity authority for the six shared arm64 runtime lanes,
  scalar-only retention for the two x64 lanes, fail-closed structural validation,
  cross-lane invariants, and fresh authoritative lane results.
- Pre-change `nuke UnitTests` reproduced exactly the D12 failure:
  `DeviceMonoAotLane_ScalarAndIdentityFloorsAgreeOnSkipCount`, scalar `4,030` versus
  identity `4,061`; aggregate `18,987` passed, `36` skipped, `1` failed.
- Pre-change log: `/private/tmp/b04-pre-unit-tests.txt`, SHA-256
  `1d652a36a841b07ea8a4845929e114350cd865f5533ff4142c2bc2a2e1f026ce`.

## Authoritative shared-lane results

Every row came from a full, unfiltered, freshly built or freshly generated lane run;
each green run seeded only its `runtime-identity-baseline.json` entry.

| Lane | Result | Command log | SHA-256 |
|---|---:|---|---|
| iOS simulator Mono/JIT | 4,064 pass / 32 skip / 0 fail / 0 crash | `/private/tmp/b04-sim-authority-final.txt` | `d70501b55fd06f494d13fe03d556d42b1392081c488f715e5dcdb14cec3cc86f` |
| iOS device NativeAOT | 4,067 / 29 / 0 / 0 | `/private/tmp/b04-device-nativeaot-authority.txt` | `2da3c0b903aba900abff85f9c2598a46b64516d1a2cf3e2b2fad18bff766667e` |
| iOS device Mono full-AOT | 4,064 / 32 / 0 / 0 | `/private/tmp/b04-device-monoaot-authority.txt` | `8fe673bac751b3dd2efe568e7fa4bd53f7f54a41c6d2f33f4add1e0c4d56e55a` |
| macOS arm64 | 3,204 / 24 / 0 / 0 | `/private/tmp/b04-host-authorities.txt` | `bb8dcf9723ff98696441b048a2fb8bade53f6021877dc5986de462dd33f18a61` |
| Mac Catalyst arm64 | 3,207 / 28 / 0 / 0 | `/private/tmp/b04-catalyst-authority-final.txt` | `5cbfc60a739a80ef82336f612d95877f7fffc2797c8e7dc2e567702a706067e8` |
| tvOS simulator arm64 | 3,314 / 28 / 0 / 0 | `/private/tmp/b04-tvos-authority.txt` | `ebdff14450d57bb27ec455cdb69c60d8d421b8a1821fcbcb3c9226013eddea44` |

The first simulator authority attempt found the stale B01 native-probe oracle described
in the main ledger. After that test-only correction, one full rerun observed a transient
typed-error allocation counter of 12 allocations and 13 deallocations. That failing full
rerun is `/private/tmp/b04-sim-nativeaot-authority-r2.txt`, SHA-256
`833a6e920add1b9dd6ee2c93f41be52c2a7ce1c66d31d41a44a66af404b39e94`.
A subsequent class-filtered diagnostic passed all 9 tests and reported `No leaks: 12
allocated, 12 deallocated`; its log is `/private/tmp/b04-sim-typed-error-focused.txt`,
SHA-256 `64e965b0b7e0950b80ccb8beca50870410db18e51b4ffc1ccdb0870ea729be1d`.
The final full simulator authority was green.

The first combined host attempt completed and seeded macOS, then Mac Catalyst linking
stopped with host `errno=28` (disk full). Only regenerable runtime-app `bin/obj`
directories were removed; the isolated Catalyst rerun and subsequent tvOS run were
green. This was a host-resource failure, not a product or baseline regression.

## Static and unit validation

- `nuke compile`: green, zero warnings and zero errors. Log:
  `/private/tmp/b04-compile-1.txt`, SHA-256
  `63af97ddf8adffad8f0603d2e79fa3e139c9a2557aaff17109edba2bdf7a5a86`.
- Post-implementation `nuke UnitTests`: green, `18,997` passed and `36` skipped; the
  unit-test floor auto-raised to `19,031`. Log: `/private/tmp/b04-unit-tests-1.txt`,
  SHA-256 `fdb794213ec5e1e23edb5089cacbba5df6165b9fab0cf3cc5eac12f081872a48`.
- Final post-evidence `nuke compile`: green, zero warnings and zero errors. Log:
  `/private/tmp/b04-final-compile.txt`, SHA-256
  `b115c81edf76fc5e2dbd0387be4d770887563ad9b4aa56f1fd4bba929e08d4cd`.
- Final post-evidence `nuke UnitTests`: green, `19,031` passed and `2` skipped;
  the pass floor held at `19,031`. The smaller skip count reflects the freshly
  generated tvOS output being present for generated-output tests. Log:
  `/private/tmp/b04-final-unit-tests.txt`, SHA-256
  `e8e21ebce28077ce92addfb8a59c251d772930e3fdbd40a71cdce05d765a4425`.
- Post-review-fix `nuke compile`: green, zero warnings and zero errors. Log:
  `/private/tmp/b04-review-fix-compile.txt`, SHA-256
  `713368a1d0586afc59a3e83e46d05faae988f35a53b64d7fa13a7978cb049154`.
- Post-review-fix `nuke UnitTests`: green, `19,032` passed and `2` skipped; the new
  seed-order regression test raised the pass floor from `19,031` to `19,032`. Log:
  `/private/tmp/b04-review-fix-unit-tests.txt`, SHA-256
  `f6f3a7a8bb888fa1edbfe96e611899592566f3e75a39546a071282e5037e1da5`.
- Post-review-fix simulator seed rerun: green, `4,064` passed / `32` skipped / zero
  fail/crash. It reached the explicit shared-lane seed branch. Log:
  `/private/tmp/b04-review-fix-seed-sim-r2.txt`, SHA-256
  `723d60085f1889dcb6b7b6531efc011bcb7b0a76e368787ee22036d3405051c6`.
- Lowered-pass seed proof: the simulator identity pass floor was temporarily raised from
  `4,064` to `4,065`; the next clean full run had `4,064` passes and successfully replaced
  the prior `4,065` floor instead of failing comparison. The saved candidate returned to
  `4,064`. Log: `/private/tmp/b04-review-fix-lowered-seed-proof.txt`, SHA-256
  `11ed70f8c71e7cf328448f30a68236c34de518f37e4b1e209faa693f33a789a3`.
- The first regenerated seed attempt was rejected before seeding by a transient finalizer
  timing failure (`198/200` deallocations); the two subsequent full runs were green. Log:
  `/private/tmp/b04-review-fix-seed-sim.txt`, SHA-256
  `6aaf7f397e604d26fa09de3a8f14eece6bf3e39a82a70deabe872674e45a4da0`.
- `git diff --check`: green.
- `validation-baseline.json` contains only `macos_x64` and `maccatalyst_x64` runtime
  floors. `runtime-identity-baseline.json` contains exactly the six shared lanes.
- No x20 disposition path is present in the candidate diff; all nine clobbers retain
  their prior publication-blocking state.

## Grok-only review receipt

The coordinator authorized one Grok-only final review and explicitly prohibited Claude;
no Claude process was invoked.

- Round 1 used scope packet `/private/tmp/paired-b04-d12-HJwPLK/scope-packet.md`
  (SHA-256 `cd27a7c6ecb6d7ff211928b2fb57afc1f4576037af4d339549f0dfb5e5f8a2a6`)
  and Grok session `01a0a012-b4ba-7d60-84f8-ac13eecfbe8c`. Its complete report is
  `/private/tmp/paired-b04-d12-HJwPLK/grok-r1/result.md` (SHA-256
  `95e8acd61f83ceddf9f511c65eba8327ab69230e9e0ffab99d60de90311ad788`).
- Round 1 retained one High, one Medium, and one Low finding. All three were accepted:
  the shared-lane seed path had to replace its identity authority before enforcing the
  prior pass floor; the transient simulator evidence needed precise log attribution; and
  stale dual-authority wording needed correction.
- Because Round 1 contained an accepted High, the same session received a focused
  serious-fix follow-up using `/private/tmp/paired-b04-d12-HJwPLK/scope-packet-r2.md`
  (SHA-256 `13c899ef2608de117895503f7139942a79bae872f8ba3138468736984298299f`).
  The complete follow-up report is `/private/tmp/paired-b04-d12-HJwPLK/grok-r2/result.md`
  (SHA-256 `0ea3203d62aac6fe26d5daa58bde66c8e02bc9896fc238904c098a9e1c02d96f`).
- The follow-up verified every accepted finding fixed and retained no fresh Critical,
  High, Medium, or Low finding. Review status was complete; unchanged Round 1 areas were
  intentionally not re-audited.

This receipt section itself was appended after the frozen follow-up snapshot. It is the
only post-review change; no implementation, test, baseline, or substantive evidence claim
changed after Grok completed.
