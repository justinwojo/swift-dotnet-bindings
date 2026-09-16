# B08 / D01 ActivityKit capability validation

Date: 2026-09-14
State: implementation, non-device validation, and the Grok-only review cycle are
complete in a local repository commit; the capability-qualified physical-device gate
remains pending an explicit APNs provisioning profile.

## Provenance and scope

- `swift-bindings` base: `74d44106f9c302741b92b4a6f5c6a7e7cb386825`
  (detached worktree).
- `swift-dotnet-packages` base: `15bff6126cc89fbb067505e133ab3752bf98cac2`
  in isolated worktree `/private/tmp/owner03-b08-swift-dotnet-packages`.
- Host: macOS (`Darwin`).

The first-party opt-in arm uses the dedicated bundle
`com.swiftbindings.activitykit.push.tests`. It requires an explicitly named profile,
checks that profile before regeneration/build, verifies the final app signature plus
embedded profile immediately before installation, and probes the live process's
signed `aps-environment` before calling `Activity.request(..., pushType: .token)`.
Every missing-capability path fails closed and names `aps-environment`; the runtime
message also names the original `SessionCore.PermissionsError Code=3` signature.

## Focused policy validation

Command:

```text
dotnet test src/Swift.Bindings/tests/UnitTests/Swift.Bindings.Unit.Tests.csproj \
  --no-restore --filter FullyQualifiedName~ActivityKitEntitlementGateTests
```

Result: 8 passed, 0 failed, 0 skipped. Coverage includes the exact runtime error,
missing signed/profile entitlements, valid development and production values,
wildcard rejection, unsupported environment rejection, and a mismatched team prefix.

The installed `Wildcard Dev` profile supplies a real negative control. This command
stopped before regeneration/build:

```text
dotnet nuke binding-tests --device --activitykit-push-token \
  --activitykit-provisioning-profile 'Wildcard Dev' \
  --class-filter LiveActivityTests
```

Exact result:

```text
ActivityKit push-token capability preflight failed: provisioning profile is missing required entitlement 'aps-environment'. Profile: Wildcard Dev
```

Log: `.nuke/temp/build.2026-09-14_13-59-38.log`, SHA-256
`7c56506f00bd10015ce3429e743541685dd179dddf7d1ebfddae12450e931379`.

## Simulator regression

The full focused simulator regeneration/build/run completed with 9 passed, 0 failed,
0 skipped, and 0 crashes for `LiveActivityTests`. It retains the supported non-push
observer ownership coverage; the opt-in push-token method is compiled only into the
physical-device arm.

Command:

```text
dotnet nuke binding-tests --class-filter LiveActivityTests
```

Log: `.nuke/temp/build.2026-09-14_13-42-53.log`, SHA-256
`1161f1c32e0de4538b6f65ea95577e24a714111118a014e7786498912cc20fa5`.

The later skip-regeneration attempt that encountered a transient simulator codesign
internal error is not closure evidence and is superseded by the complete green run
above.

Round-1 review repairs were validated with an 8/8 focused policy run, a zero-warning
build of the Nuke harness, and a small iOS compile that links the exact live-probe and
gate sources under `DisableRuntimeMarshalling` with warnings treated as errors. That
compile passed with 0 warnings and 0 errors, proving the accepted blittability repair
without requiring an APNs profile. The focused mutually-exclusive option command also
failed before build with the expected `--activitykit-push-token cannot be combined`
diagnostic. Review identity and all candidate dispositions are recorded in
[`b08-grok-review.md`](b08-grok-review.md).

## Exact downstream package integration

Fresh packages from this exact upstream commit were consumed in the isolated package
worktree:

| Package | SHA-256 |
| --- | --- |
| `SwiftBindings.Runtime.0.20.0.nupkg` | `423f59b42bf1e1f69a89c3a2d881ee7686cd566f9acaf58e91a006ab60b15008` |
| `SwiftBindings.Sdk.0.20.0.nupkg` | `e23b69bc7a12dc2a1a19e64f1e7f7b7a614c99cc2ae4826bd0b271f22541485c` |
| `SwiftBindings.Apple.26.2.10.nupkg` | `0113e8e26c8504af2ae6b37a057475dc74fb4ca9fac08e8dfebacdffb94bafd5` |

The downstream ActivityKit simulator passed 27/27, including
`LiveActivity non-push observer register/replace/release`. Detailed commands and log
hashes are recorded in the package repository's
`artifacts/regression-validate/0.20.0/b08/validation-receipt.md`.

## Deferred external physical-device qualification

Pending. The connected iPhone resolves from CoreDevice identifier
`559479FD-3C60-51E4-8B2C-872D8CBA8B54` to provisioning UDID
`00008110-00114C122E46801E`, is paired, and has Developer Mode enabled. The only
installed profile is wildcard and lacks `aps-environment`; Xcode currently has no
Apple developer account configured.

After an APNs-capable explicit profile for the dedicated bundle is available, closure
requires both the first-party and downstream NativeAOT device arms to verify signed
evidence and pass request, observer registration, replacement, end, and dead-handle
no-op behavior. The exact device logs and signed-evidence hashes will replace this
pending section when external qualification resumes; the source review for this batch
does not convert the deferred gate into a pass.

Resume the first-party NativeAOT arm with:

```text
dotnet nuke binding-tests --device --activitykit-push-token \
  --activitykit-provisioning-profile '<EXPLICIT_APNS_PROFILE_NAME_UUID_OR_PATH>' \
  --class-filter LiveActivityTests \
  --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54
```

Do not accept a successful build, install, or non-push test as substitute evidence.
Closure requires the preinstall verifier's
`ActivityKit push-token capability verified` line and retained signed/profile hashes,
the runtime signed-entitlement probe, and a pass for
`TestPushTokenRequestAndObserver_DeviceCapabilityQualified` from that same run. Then
run the package-repository command recorded in its B08 receipt against newly packed
artifacts from the qualifying upstream commit.
