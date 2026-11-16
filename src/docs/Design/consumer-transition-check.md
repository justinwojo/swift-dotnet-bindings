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
| `PresentValid` with a rule failure or a mismatched native image | `error SWIFTBIND132` |
| `AbsentNonPrimitive` (no manifest beside a non-primitive transition) | `error SWIFTBIND132` |
| `TamperedOrMismatched` | `error SWIFTBIND132` |

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
package: every framework slice must hold the manifest hash and export exactly its bridges. A refused
package is deleted and the build fails with `error SWIFTBIND133`.

The usual cause is a wrapper that kept an earlier routing. The wrapper-compile finalizer does not
relink when there is no lowering receipt, or when a relink fails (SWIFTBIND129) and it restores the
earlier wrapper. That wrapper keeps the earlier hash and bridges while the manifest describes the new
plans. The SDK keeps failing closed rather than recompiling at pack time: the remedy is a clean
build (delete the binding's `bin/` and `obj/`) and a new pack. The consumer check catches the same
mismatch for a `ProjectReference` whose wrapper is among the app's native references.

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
