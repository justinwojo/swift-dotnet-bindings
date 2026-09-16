# B08 / D01 Grok-only review record

Date: 2026-09-14
Review policy: Grok only; Claude was never invoked.
State: final review cycle complete; included in the reviewed local B08 commit. The
external APNs signed-device/NativeAOT qualification remains pending, not green.

## Round 1 identity and scope

- Grok session: `01a0a16f-92de-70e2-baa5-6d95a664d8fa`.
- Complete captured report:
  `/private/tmp/paired-owner03-b08-root/grok-r1-escalated/result.md`.
- Report SHA-256:
  `0062d216a6dec61b8e45734a87a7ecbf6fd19152694ca97c000fab878c83b527`.
- Reviewed `swift-bindings` base:
  `74d44106f9c302741b92b4a6f5c6a7e7cb386825`.
- Reviewed `swift-dotnet-packages` base:
  `15bff6126cc89fbb067505e133ab3752bf98cac2`.
- Frozen round-1 staged diff hashes:
  `adcb92d0e1a037ce0c725f044d093f63874cf5533e4a7ad2d69cbd29c3beba5a`
  and `55b116311decea04114d3fec283ae5a3687f1f3bd686a2761aa21b4ca144c0d8`.

Grok used five inspect-only areas: first-party Nuke options, first-party entitlement
and runtime code, package Nuke lanes, package app/gate code, and cross-repository
receipts/claim evidence. The consolidated result retained two High, three Medium,
and five Low findings, with no Critical finding.

## Candidate dispositions and repairs

| Candidate | Round-1 disposition | Owner adjudication and repair |
| --- | --- | --- |
| Opt-in test can ratchet the default runtime identity baseline | High | Accepted. `CompareRuntimeBaseline` now returns before all comparison/write paths when `--activitykit-push-token` is active. |
| `CFStringGetCString` returns non-blittable `bool` under `DisableRuntimeMarshalling` | High | Accepted. Both live probes declare a blittable `byte` return and compare it to zero. A focused iOS compilation enables `DisableRuntimeMarshalling` and treats warnings as errors. |
| Exclusive BindingTests legs can swallow the ActivityKit opt-in arm | Medium | Accepted. ActivityKit is part of the mutually-exclusive option set; a focused command proves the combination fails before any build. |
| Package device app does not probe its live signed entitlement | Medium (demoted from area High) | Accepted. The dedicated app now reads the running signature through SecTask/CF, validates the exact environment through the shared policy, and names `aps-environment` plus `SessionCore.PermissionsError Code=3` before request. |
| First-party simulator log digest is missing one hexadecimal digit | Medium | Accepted. The receipt now carries the recomputed 64-digit digest `1161f1c32e0de4538b6f65ea95577e24a714111118a014e7786498912cc20fa5`. |
| New first-party gate lacks the required header | Low | Accepted. Added the repository copyright/MIT header. |
| Package policy self-test does not exercise profile preflight branches | Low | Accepted. Added runtime-message, expiry, wrong-device, and valid-profile coverage in addition to signed/profile parity cases. |
| Simulator non-push arm relies on the default argument | Low | Accepted across both repositories. The initial repair hit sibling lifecycle calls; the focused follow-up caught the three original observer call sites, which now all pass `usePushToken: false` explicitly. |
| Package receipt says wrong-device fails before build | Low | Accepted. Receipt now distinguishes profile checks before build from device membership immediately before install. |
| Package resume recipe does not make the temporary 0.20.0 SDK pin exact | Low | Accepted. Receipt now shows the exact validation-only diff, cache-isolation requirement, and device command. |
| `--skip-build` can reuse an already-qualified dedicated app | Dropped | Existing skip-build contract; final signature/profile/device verification still executes before install. |
| Hardware-UDID output format could drift | Dropped | The parser was proven against this host/device. Failure to resolve throws before install, so drift cannot false-pass. |
| `PROVISIONING_PROFILE` fallback is broad | Dropped | Every selected profile still passes the same explicit App ID/APNs/bundle/team/expiry checks; the real wildcard negative control fails closed. |
| TrimmerRoots omit the AppleSupplement namespace | Dropped | Existing NativeAOT discovery already roots and executes these test classes; no reachable defect was shown. |
| Multiple team identifiers / distribution profile shape | Dropped | No real-profile evidence established a reachable case. The accepted gates still fail closed on mismatches. |
| First-party simulator request also relies on default false | Merged into simulator-default Low | Fixed in the same explicit-argument category. |

No candidate from an area reviewer was omitted. Because the accepted round contained
High findings, the coordinator ran the required focused serious-fix follow-up in the
same Grok session before authorizing commit.

## Serious-fix follow-up

The same Grok session reviewed staged hashes
`281d81320ed0506052cea8ab92ed5b99e6b3991c9d3af7bf1c90c2c5d8f0d70a`
and `e590eefe022aeaaf70b245bc05a724224a5d04c75709e6174901dc6d1b778efa`.
Complete follow-up report:
`/private/tmp/paired-owner03-b08-root/grok-r2/result.md`, SHA-256
`ff95b9a1fe9ee69bc4412f2cfc8b843c3734a1d8fa2e6ad0534b378e67793ace`.
It found no remaining Critical, High, or Medium defect. Its one Low observed that the
explicit-non-push repair changed sibling lifecycle calls rather than the original two
first-party and one package observer request sites. That Low was accepted and the
whole three-site category was fixed. Per the Medium/Low stopping rule, affected
validation followed without another review round.
