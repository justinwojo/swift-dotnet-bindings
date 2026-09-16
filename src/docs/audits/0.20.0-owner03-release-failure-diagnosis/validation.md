# OWNER-03 diagnosis ledger validation

Date: 2026-09-14
Checkout: `7fbba4b862aa3c5c3d2f972673d61659e50ad47d`

## Static integrity

- `git diff --check`: pass.
- Heading inventory: 12 distinct `D01`-`D12` diagnosis rows and 13 ordered
  `B01`-`B13` batches.
- Identifier inventory: every `D01`-`D12` appears in the ledger.
- Named existing first-party source/test homes were checked for existence.
- StoreKit scan confirms `.storekit` is restricted to a genuinely Xcode-driven
  control and is explicitly prohibited through the current Nuke/simctl/dotnet path.
- Final ledger SHA-256:
  `2968177829c71de2115705b1d2ad1be6071ddab76e9cf2bffe7bbabfbde28553`.

## Focused executable evidence

Command:

```text
dotnet test src/Swift.Bindings/tests/UnitTests/Swift.Bindings.Unit.Tests.csproj \
  --filter FullyQualifiedName~RuntimeBaselinePlatformKeyTests.DeviceMonoAotLane_ScalarAndIdentityFloorsAgreeOnSkipCount
```

Expected diagnostic result: failed, one test, with `Expected: 4030`, `Actual: 4061`
at `RuntimeBaselinePlatformKeyTests.cs:140`. This intentionally reproduces D12; the
diagnosis task does not update either baseline.

The KeychainAccess scratch generation/build receipt and the unavailable CryptoKit
package preflight are recorded in the ledger's authority section. No full downstream
matrix was rerun.

## Grok-only paired review

The first sandboxed launch could not access Grok's service/session registry and made
no repository change. The same immutable scope was then run with the required access.
No Claude review was launched.

- Round 1 session: `01a09e9f-5763-7370-9cf3-6c23312fc638`.
- Round 1 report: `grok-round-1.md`; four accepted findings (one High, three Medium).
- Accepted fixes: StoreKit Sandbox/Xcode path correction; all-shared-lane baseline
  reconciliation; inversion of the existing generic-opening `errorOut` assertion;
  Nuke B05/B03 whole-cell dependency correction. Directly useful Low precision edits
  for the harness/device script, cell/signature wording, and inout fixture names were
  also applied.
- Same-session follow-up: `grok-round-1-follow-up.md`; clean of High/Critical
  regressions. One directly caused Low noted that D12's gate still named only the
  Mono-AOT execution proof.
- Final fix pass: D12 now requires an authoritative current result for every shared
  lane, including a fresh Mono full-AOT `4,061/32/0` result. Per the paired-review
  stopping rule, a Low-only wording fix does not restart review.

Review artifacts are retained beside this file with their run JSON and exact scope
packets. Grok's final follow-up did not edit the workspace.

## B01 / D02 implementation receipt

Date: 2026-09-14
Starting and pre-review HEAD: `da210e120c571f0b10abc8ad4bd9a1a23e6e4862`
(detached at the implementation checkpoint, which was intentionally uncommitted
pending the coordinator-owned Grok-only review slot).

### Contract and coverage

`ThrowingWrapperErrorContractEmitter.EmitInitialization` now owns the synchronous
C-callable throwing-wrapper entry invariant: write `nil` to the caller-owned Swift
error slot immediately before `do`, then publish a retained Swift error only from
`catch`. The invariant is used by every source emitter that retains an error into an
out slot: ordinary/static method wrappers, properties, every initializer shape
(class, struct, failable, generic, and generic static factory), method-level-generic
opening carriers, concrete protocol specializations, protocol witness dispatch
(direct/blittable, existential, class, and indirect/struct return families),
optional-pointer normalization, closure-bearing wrappers and closure invoke thunks,
and ArraySlice normalization.

Semantic emitter assertions cover each family and require the clear to precede
`do`; the existing method-level-generic occurrence-count lock was deliberately
inverted from one assignment to two (clear plus catch). The behavioral fixture adds
`ThrowingItemNamespace.staleErrorProbe<T>` and invokes the same generated
`SongItem` concrete-specialization wrapper first with failure and immediately with
success. Generated Swift inspection showed the clear at wrapper entry and the
retained error only in `catch`. This batch contains no CryptoKit-specific workaround
and does not claim downstream CryptoKit confirmation.

### Fail-first and validation evidence

- Pre-fix: `nuke test` (the target ignored the requested test filter) produced the
  intended reds in the method-level-generic opening and throwing-property contracts.
  Two initial assertions were also attached to non-C-callable fallback tests and were
  moved to their real C-callable emitter tests before implementation. Result:
  18,974 passed, 36 skipped, 5 failed; the remaining non-B01 failure was the already-diagnosed
  D12 device Mono-AOT baseline mismatch (`4030` expected, `4061` actual). Log:
  `/private/tmp/b01-prefixed-emitter-red.log`.
- `nuke compile`: pass, 0 warnings/errors; refreshed the universal2
  SwiftInterfaceParser and generator. Log: `/private/tmp/b01-compile.log`.
- Focused emitter command:
  `dotnet test src/Swift.Bindings/tests/UnitTests/Swift.Bindings.Unit.Tests.csproj
  --no-restore --filter "FullyQualifiedName~OptionalPointerWrapperTests|FullyQualifiedName~MethodLevelGenericOpeningTests|FullyQualifiedName~MethodWrapperEmitterTests|FullyQualifiedName~PropertyWrapperEmitterTests|FullyQualifiedName~ConstructorWrapperEmitterTests|FullyQualifiedName~WitnessDispatchEmitterTests|FullyQualifiedName~ConcreteSpecializationEngineTests|FullyQualifiedName~ArraySliceNormalizationEmitterTests|FullyQualifiedName~ClosureEmitterDirectTests"`:
  1,106 passed, 0 failed/skipped. Log:
  `/private/tmp/b01-focused-emitter-tests.log`.
- Full `nuke test`: all B01 assertions passed; the composite stopped solely on the
  pre-existing D12 baseline mismatch above. Unit result: 18,978 passed, 36 skipped,
  1 failed. `WithdrawalGateTests` passed; the dependent analyzer/runtime targets were
  then run directly: `nuke analyzer-tests` passed 79/79 and
  `nuke runtime-unit-tests` passed 922 with 1 skip. Logs:
  `/private/tmp/b01-nuke-test.log`, `/private/tmp/b01-analyzer-tests.log`, and
  `/private/tmp/b01-runtime-unit-tests.log`.
- `nuke binding-tests --compile-only`: pass in 4:44. Generated 1,701 C# files and
  3 Swift wrapper files; C# compile-check and Swift wrapper compile passed; wrapper
  getter parity, artifact parity, resilience kitchen, ingestion kitchen, overload
  naming (54/54 non-numeric), and closure parity gates passed. Log:
  `/private/tmp/b01-binding-tests-compile-only.log`.
- `nuke binding-tests --sim --class-filter BasicThrowingTests`: pass, 57/57 on
  `iossimulator-arm64` Mono; fail-then-success probe passed in 8 ms. Log:
  `/private/tmp/b01-basicthrowing-sim.log`.
- `nuke binding-tests --device --class-filter BasicThrowingTests --device-udid
  559479FD-3C60-51E4-8B2C-872D8CBA8B54`: pass, 57/57 on the selected device with
  `IsNativeAotRuntime=True`; fail-then-success probe passed in 4 ms. Log:
  `/private/tmp/b01-basicthrowing-device-nativeaot.log`.
- `nuke pack --version 0.20.0-b01-d02.1 --apple-version 26.2.8 --skip-apple`:
  pass. B01 is an SDK/generator-only batch, so the unchanged Apple supplement was
  not packed; the SDK was stamped to the latest published `apple-v26.2.8`. ApiCompat
  passed against Runtime 0.19.4, the native-export gate satisfied 49 imports / 478
  requirements over 10 slice-architecture pairs, and the no-source-mutation,
  Windows-path, and version-truth gates passed. Log: `/private/tmp/b01-pack.log`.

Candidate packages:

- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-nuget/SwiftBindings.Runtime.0.20.0-b01-d02.1.nupkg`
- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-nuget/SwiftBindings.Sdk.0.20.0-b01-d02.1.nupkg`
- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-nuget/SwiftBindings.Templates.0.20.0-b01-d02.1.nupkg`

SHA-256, in the same order: `3dddfbcd7ad37440963d41b50057b44cacc39e3c7f07ddd9ae39ce3fcdeb1eb8`,
`ef624372d967cdc726bec5f65a37125ef5817e915bd7e4855dcde7b03ec99a00`,
and `b37f55a71f16bf8113a9643d648acca4fab02f990223d158bf831aaaa2d63180`.

`git diff --check` passed. The pack prerequisite reordered dictionaries in the
tracked Apple xcframework `Info.plist`; that content-equivalent incidental change was
restored, leaving only B01 implementation, tests, fixtures, and this evidence receipt.
At this implementation checkpoint no review or commit had been started. The
subsequent coordinator-authorized Grok-only review and its one Medium-only fix pass
are recorded in `b01-grok-review.md`; no Claude review ran.

### Grok adjudication and final fix pass

Grok session `01a09ee6-abe2-78a1-910b-118a59c4b1cb` found no Critical/High
production defect and retained two Medium test-lock gaps. The complete frozen scope,
coverage, findings, candidate dispositions, artifact hashes, and residuals are in
`b01-grok-review.md`.

The final fix pass added clear-before-`do` assertions for the three previously
unlocked witness-dispatch helpers and the independent throwing class, failable-class,
generic-class, and generic-static-factory constructor bodies. Failable struct shares
the already-locked `EmitThrowingStructBody` and therefore did not need a duplicate
test. Focused witness/constructor tests passed 316/316, and the original nine-family
B01 filter passed 1,107/1,107. Logs:
`/private/tmp/b01-post-grok-witness-constructor.log` and
`/private/tmp/b01-post-grok-focused-emitter-tests.log`.

Production and runtime fixtures did not change during this test-only fix pass, so
the already-green simulator, NativeAOT device, compile-only, and pack gates were not
repeated. `git diff --check` passed. Per the review stopping rule, no second Grok or
Claude cycle was run.
