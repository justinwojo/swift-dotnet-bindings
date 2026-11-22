# The consumer transition check

An app that references a compiled Swift binding runs that binding's Swift transitions on the app's
own engine. Mono and the .NET 11 CoreCLR interpreter mishandle some Swift-convention calls that
NativeAOT and the JIT handle, so a binding generated before routing existed, or one whose evidence
does not match its code, can crash an app that builds cleanly. The consumer check refuses such a
build up front. It reads the transition manifest each binding embeds
([swift-lowering-evidence.md](swift-lowering-evidence.md) § "Shipped evidence") and runs the
transition gate's rules against it.

## Where it runs

`SwiftBindings.Runtime` ships the check in `buildTransitive/`: the MSBuild task
`SwiftTransitionConsumerCheck` (`src/Swift.Runtime/tasks/`) and the targets that call it
(`SwiftBindings.Runtime.targets`). Every binding depends on the runtime, so every app that consumes
a binding gets the check, whatever the binding's own SDK version.

The target runs before `CoreCompile`, after reference resolution, in an app (`OutputType` `Exe` or
`WinExe`, or an app extension). It is skipped in design-time builds. It runs on these engines:

| Configuration | Checked |
|---|---|
| .NET 10 iOS or tvOS simulator, Mac Catalyst, device Mono AOT (Mono) | Yes |
| .NET 11 iOS, tvOS or Mac Catalyst, Debug (CoreCLR interpreter) | Yes |
| .NET 11 iOS, tvOS or Mac Catalyst, Release (ReadyToRun leaves some methods to the interpreter) | Yes |
| NativeAOT publish (`_UseNativeAot`) | No |
| macOS, or a target framework with no Apple platform | No |
| A library, or a design-time build | No |

It checks every resolved reference that can make a Swift transition: package references (direct and
transitive), project references and plain `Reference` items. It reads the copy the app loads
(`ReferenceCopyLocalPaths` before `ReferencePath`), because a reference assembly drops private
P/Invokes. A metadata-only prefilter skips assemblies with no Swift surface. Results are cached in
`$(IntermediateOutputPath)swiftbindings-transition-check.json`, keyed by each assembly's MVID, the other
candidates' MVIDs and the native references, so an incremental build reads nothing again.

The app's own source is not a reference. An app that compiles generated C# directly (SDK-direct
mode, or the in-tree `RuntimeTestsApp`) is covered by the producer-side gates instead: the compile
gate's transition ledger cross-check and the SDK's pack check.

## Outcomes

| Evidence state | Result |
|---|---|
| `PresentValid`, the rules hold, and every native image the app references for a routed library holds the manifest hash and exactly its bridges | Accepted, no diagnostic |
| `AbsentPrimitiveOnly` (no manifest, every Swift transition a primitive-only forward call) | Accepted, no diagnostic |
| `PresentValid` with routing gaps that a recorded bridge withdrawal explains, and the assembly and images hold up as that withdrawal (see *Partial bindings*) | Accepted; a message names the withdrawn members and the engines they refuse on |
| `PresentValid` with a rule failure or a mismatched native image | `error SWIFTBIND132` |
| `AbsentNonPrimitive` (no manifest beside a non-primitive transition) | `error SWIFTBIND132` |
| `TamperedOrMismatched` | `error SWIFTBIND132` |
| Any state, with a native image of a wrapper the assembly records a wrapper identity for that does not export that identity (another copy's or build's wrapper, or an unstamped one; `multi-toolchain-packages.md` § Wrapper identity) | `error SWIFTBIND132` (remedy: one build per copy) |

A missing manifest alone is not an error. A routed library with no native image among the app's
`NativeReference` items is logged, not refused: the check cannot read a hash it does not have, and
a missing library is a load failure rather than an engine hazard.

The diagnostic names the assembly and where it came from, the evidence state, the count and the
first three problems (the rest follow as messages), the engine of this build, the affected engines,
and the remedy. For example:

```
error SWIFTBIND132: Legacy.dll (package Acme.Legacy 1.0.0) carries no Swift transition manifest,
and 1 of its Swift transitions are not primitive-only calls: Counter::CountOf ($s…): breaks R1.
This build runs on Mono. Affected engines: Mono (iOS and tvOS simulators, Mac Catalyst, device
Mono AOT) and the .NET 11 CoreCLR interpreter (Debug, and whatever ReadyToRun leaves to it in
Release); NativeAOT is not affected. Update Acme.Legacy to a version built with the current
SwiftBindings.Sdk, or regenerate the binding with the current SDK. To accept the risk for this
package only, add <SwiftBindingsTransitionSuppress Include="Acme.Legacy" /> (the error becomes
this warning).
```

## Opting out

There is no global switch. One item per package downgrades that package's error to a warning:

```xml
<ItemGroup>
  <SwiftBindingsTransitionSuppress Include="Acme.Legacy" />
</ItemGroup>
```

The key is the NuGet package id; for a project or plain reference it is the assembly name. Matching
ignores case. A suppression that matches nothing is logged.

## The producer side

`SwiftBindings.Sdk` runs the same engine after `Pack` (`_SwiftBindingsCheckPackedTransitions`,
task `SwiftTransitionPackageCheck`). It reads the package it just wrote, as a consumer would receive
it, and checks every `lib/` assembly with Swift transitions. Here a routed library must be in the
package: every framework slice must hold the manifest hash and export exactly its bridges, and every
slice of a wrapper the assembly records an identity for must export that identity. An
assembly in `lib/<tfm>/` is checked against the slices a consumer of that copy links: those under
`native/<tfm>/` of the same TFM, a legacy `runtimes/<rid>/` of its platform (packages from before
the per-TFM layout), and natives shared by every copy. A binding for iOS and
tvOS ships one wrapper per copy, and the manifests differ wherever the copies' routing does. A
refused package is deleted and the build fails with `error SWIFTBIND133`.

The error names the remedy by cause. A wrapper that kept an earlier routing (the wrapper-compile
finalizer does not relink without a lowering receipt, so the wrapper keeps the earlier hash and
bridges while the manifest describes the new plans) wants a clean build (delete the binding's `bin/`
and `obj/`) and a new pack; the SDK fails closed rather than recompiling at pack time. A bridge
withdrawal that does not hold up wants the relink failure fixed, which no clean build does. A
missing or tampered manifest wants the binding regenerated with the current SDK. The consumer check
catches the same mismatches for a `ProjectReference` whose wrapper is among the app's native
references.

## Partial bindings

When the bridge relink fails after generation routed (`SWIFTBIND129`), the finalizer puts the first
compile's wrapper back and the C# back to generation's own guarded direct imports, and the call-plan
sidecar records the withdrawal under `bridgeWithdrawal`:

```json
"bridgeWithdrawal": {
  "cause": "RelinkFailed",
  "reason": "the wrapper could not be relinked with its bridges: …",
  "library": "AcmeSwiftBindings",
  "transitions": [
    { "member": "…", "entryPoint": "$s…", "symbol": "SBSR_…",
      "shapes": ["SelfRegister", "LoweredSignature"],
      "refusesOn": ["Mono", "the CoreCLR interpreter (arm64)", "the CoreCLR interpreter (x86_64)"] }
  ]
}
```

The coverage entries stay as they are (`executable` true, `bridged` false, `bridgeRefusal` the
reason, lowering evidence kept), and the manifest embeds the sidecar, so the record ships inside the
assembly and is covered by the manifest hash. `cause` is `RelinkFailed` (the relink did not
complete), `RoutingDefect` (rewriting the C# or emitting the bridges threw), or `ReceiptMismatch`
(the relinked wrapper is not the one the receipt describes). `refusesOn` is the engine table
(`SwiftTransitionEngines`) applied to `shapes`, which a unit test holds to the runtime guard's own.

Such a package is safe to consume, and its named members refuse, before Swift runs, on the named
engines. The pack and consumer checks accept a routing gap only when all of this holds:

- the gap is listed in the record by member and entry point, with the same bridge symbol, and its
  `bridgeRefusal` is the record's reason; any other gap is unrecorded and refused;
- the cause is `RelinkFailed` or `RoutingDefect`. A `ReceiptMismatch` is refused: two compiles of
  the same wrapper lowered differently, so the receipt no longer shows the ABI the restored imports
  were judged against;
- the shapes are shape names and `refusesOn` is exactly the engines that refuse them;
- nothing in the module is still bridged, its manifest routes nothing, and the assembly imports
  no bridge (a dangling `SBSR_` import leaves the evidence `TamperedOrMismatched`);
- each listed transition is a Swift-convention import of that member and entry point in the
  assembly, with the recorded shapes, and the residual-guard check (R1) passes at full strength:
  every restored import is behind a guard of its own shapes before anything can enter it;
- every image of the withdrawn library carries no bridge and no manifest hash (a stale wrapper is
  refused, with the clean-build remedy). The pack check requires those images; an app's check
  logs a library it has no image of, as for a routed one. The record's library is not trusted to
  be the only one: no image of any other library may export the module's manifest hash symbol
  (`SBTM_<module>`), which every wrapper that bridged the module exports. (A restored import of a
  dynamic framework calls the module's own library, not the wrapper that hosted the bridges, so
  the imports do not name that wrapper; and bridge names follow the plan, not the module, so
  another library's bridges are not taken for leftovers.);
- every other rule still holds.

The record explains the gaps; it exempts nothing. `PackageTransitionEvidence.Check` reads strictly by
default (every gap fatal, as the compile gate's transition ledger reads its own output); the pack and
consumer checks pass `acceptWithdrawals`. `SWIFTBIND129` stays a warning and names the withdrawn
members and the engines they refuse on; the pack check and the app's check log the same. NativeAOT
and CoreCLR's JIT run every known shape, so a withdrawn member runs there: on a device, NativeAOT
runs both shapes of a self-register, lowered getter, while Mono full-AOT refuses its `SelfRegister`.

## Where it is proven

- Unit tests: `ConsumerTransitionCheckTests` (the engine) and `TransitionCheckTasksTests` (both tasks
  through MSBuild: which configurations run, the diagnostic text, the opt-out, which reference copy
  is read, the cache, and the pack check).
- `binding-tests --bridge-consumer`, mode `transition`: build-only consumers on each platform family
  of the run, Debug and Release, against the binding the `package` mode packs (accepted and
  verified), a manifestless first-party fixture with an untyped `SwiftSelf` import (SWIFTBIND132),
  and a manifestless primitive-only fixture (accepted). The iOS family also builds the unsafe
  fixture with its suppression (warning) and from a macOS app (not checked), and builds the three
  consumers for the device, unsigned and never installed: Mono full-AOT on .NET 10, the CoreCLR
  interpreter (Debug) and ReadyToRun (Release) on .NET 11. Each accepted build
  must show the verdict the check recorded, so a check that stopped running cannot pass.
- `binding-tests --bridge-consumer`, mode `rollback`: a fresh rollback and a rollback after a build
  that routed must both pack, their evidence from the nupkg alone must pass the accepting check over
  every shipped slice and fail the strict one; doctored copies of the package (an unrecorded gap, an
  inconsistent record, a dangling bridge import, a missing guard, the routed build's wrapper beside
  the rolled-back assembly) must be refused by the engine and by the packaged pack and consumer tasks
  for their own reasons while the undoctored package passes both; and a clean `PackageReference`
  consumer must build with the binding recorded `Verified` and run it: the members that need no
  bridge return the right values, and the withdrawn getter throws before Swift runs on every engine
  the record names. `BridgeWithdrawalCheckTests` covers the same rule in unit tests, a late guard
  included.
