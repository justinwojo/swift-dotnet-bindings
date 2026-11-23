# Per-toolchain copies in one binding package

A SwiftBindings binding package carries one **copy** of the binding per supported .NET + Xcode
pairing. An app on any supported pairing restores the one package and gets the copy built for its
toolchain, or the nearest older one. This document records the layout, the guards that keep a copy
whole, and the merge that assembles a multi-copy package. `SwiftBindings.Runtime` and
`SwiftBindings.Apple` are not binding packages in this sense: each stays one toolchain-neutral copy
with its own layout.

## Layout

```
lib/<T>/<Id>.dll (+ .xml)
buildTransitive/<T>/<Id>.targets        consumer targets; name only ../../native/<T>/
buildTransitive/<T>/<Id>.props          the copy declaration (SwiftBindingPackageCopy)
buildTransitive/<T>/<Module>Database.xml, ILLink.Descriptors.xml, …
native/<T>/<Module>SwiftBindings.xcframework (+ bridge, source xcframework, resource bundles)
```

`<T>` is the copy's **full** target framework, for example `net10.0-ios26.0`: the folder NuGet packs
the managed assembly under, read from NuGet's own folder-name task over the target framework and
platform monikers (so the workload's platform version completes a versionless `TargetFramework`). `SwiftAppleFrameworkPlatformVersion` selects the SDK surface an Apple-framework
binding is generated from; it never relabels the copy. NuGet chooses `lib/`
and `buildTransitive/` separately, by the same nearest-framework rule, so the two agree only when the
package holds the same set of TFMs in both (and one nuspec dependency group per TFM). Each copy's
targets name their own `native/<T>/` literally, so the natives follow the targets NuGet chose. A
single-toolchain package is a one-copy package of the same shape; nothing ships under `runtimes/`,
which is keyed by RID and cannot hold two copies of one platform.

The oldest .NET 10 copy of each platform is labelled `net10.0-<platform>26.0` and built on Xcode 26:
a versionless `net10.0-<platform>` class library gets platform version 26.0 on every .NET 10
toolchain, so a package whose oldest copy names 26.2 is unrestorable from such a library (NU1202).
Newer copies name their SDK (`net10.0-ios27.2`, `net11.0-ios27.2`).

## The copy declaration

`buildTransitive/<T>/<Id>.props` adds one `SwiftBindingPackageCopy` item: the copy TFM, the toolchain
and platform SDK it was built with, the SwiftBindings SDK version, and the copy's `lib/`, `native/`
and targets paths. The generator writes it beside the consumer targets of a standalone project;
`SwiftBindings.Sdk` writes its own at pack time (`PrepareSwiftBindingPackageCopy`), since only the
pack knows the toolchain. One writer composes both (`PackageCopyDeclarationEmitter`).

## Guards

| Code | Where | What it refuses |
|---|---|---|
| SWIFTBIND136 | every app build (`SwiftBindings.Runtime` targets, `_SwiftBindingsCheckPackageCopies`, before `CoreCompile`) | a binding assembly resolved from another copy's `lib/` than the copy whose targets NuGet imported, or an assembly of a copy-layout package with no declared copy at all. Each build also prints one line per package naming the copy it selected and its lib, native and targets paths. |
| SWIFTBIND137 | pack (`PrepareSwiftBindingPackageCopy`) | an Apple-framework copy labelled for another platform SDK than the one it was generated from: the label must have the SDK's major version and be no newer. The SDK judged (and declared) is the one the binding's assembly records it was generated from, in its wrapper identity, not the SDK selected when it packs; the selected SDK stands in only for an assembly that records none. A versionless Apple-framework project (the template's default) is labelled 26.0 on every .NET 10 workload, so on Xcode 27.2 it must name its copy (`net10.0-ios27.2`) to pack. A library binding is never refused, so a versionless `net10.0-ios` library binding packed on Xcode 27.2 keeps its 26.0 label and serves .NET 10 + Xcode 26 apps: its surface is the library's, not the SDK's. |
| SWIFTBIND138 | pack and merge | consumer targets that reference `runtimes/` or another copy's `native/`, found in the text, or a `NativeReference` that does not resolve, from `buildTransitive/<T>/` as NuGet lays the file out, into `native/<T>/` (one through a property, relative to the consuming project, absolute, or out of the package); at merge, every refusal below. |
| SWIFTBIND139 | every app build | a copy older than the build's toolchain (another .NET version, or a lower platform SDK major): an informational message by default, an error with `SwiftBindingsRequireExactCopy=true`. |
| SWIFTBIND133 | pack (`SwiftBindings.Sdk`, and a standalone generated project through `SwiftBindings.Runtime`'s targets) and merge | the transition check, paired by full TFM: `lib/<T>/` is checked only against `native/<T>/`. A package whose `buildTransitive/` declares a copy is held to that rule even when no native landed under `native/<T>/`. It includes the wrapper identity check below. |
| SWIFTBIND132 | every app build the consumer transition check runs in (Mono and the .NET 11 CoreCLR engines on iOS, tvOS and Mac Catalyst; not NativeAOT or macOS) | reads the natives the app actually references, which are the selected copy's (SWIFTBIND136 guarantees the assembly is that copy's too), including the wrapper identity check below. |
| SWIFTBIND140 | merge | a warning: an entry past the Windows restore path budget (below). An error with `SwiftBindingsMergeRequireWindowsPathBudget=true`. |

The build's own transition-evidence and packed residual-guard checks pair assemblies and natives the
same way (`PackagedNative.Serves`).

## Wrapper identity

A copy's managed assembly must ship with the wrapper it was generated with. The transition
manifest's hash (`SBTM_`) cannot say so: routing rewrites the manifest after the first wrapper
compile, and only a wrapper that defines bridges carries it, so two copies of a binding with no
bridges, or with the same bridges, cannot be told apart by it. Generation therefore stamps each
module with a **wrapper identity** (`SwiftWrapperIdentityFormat`), fixed before the wrapper is
compiled:

```
schema=1;module=<M>;library=<wrapper>;copy=<full TFM>;sdk=<platform SDK>;generator=<version>;source=sha256:<wrapper source>
```

It is written twice beside the wrapper source: as `AssemblyMetadata("SwiftBindings.WrapperIdentity",
…)` in `<ns>.WrapperIdentity.g.cs`, which the binding compiles, and as the exported constant
`SBWI_<M>` in `<ns>.WrapperIdentity.s`, a data-only assembly file the wrapper compile links into
every slice with the thunk objects (`NativeThunkCompiler.CollectIdentityFiles`; `.no_dead_strip`, no
undefined symbols, so the thunk weak-link gate has nothing to weaken). Nothing the wrapper compile
settles later (strip, routing, a relink or its rollback) changes it. The consumer and pack checks
(SWIFTBIND132, SWIFTBIND133; `SwiftWrapperIdentityCheck`) require every slice of each image of the
named wrapper to export `SBWI_<M>` holding exactly the assembly's value, so the older copy's wrapper
under the newer copy's assembly, a wrapper of another build, or an unstamped wrapper is refused
(remedy `WrapperMismatch`). An assembly that records no identity (a hand-written table, a binding
from an earlier SDK, a module without a wrapper) is not held to one, and a wrapper no image
provides is left to the app's load. The pack check reads framework binaries and bare dylibs
(`<library>.dylib`, `lib<library>.dylib`), as a consumer's native reference resolves them. The app side
runs where SWIFTBIND132 runs; a NativeAOT or macOS app relies on the pack and merge check, since a
wrapper can only change copies after it by editing a published package. The identity's `sdk` field is
also the binding's record of the SDK it was generated on, which SWIFTBIND137 judges.

## Merge

Each pairing packs its own explicitly versioned TFMs from one source revision and one SwiftBindings
SDK version; the pipeline owns the matrix. The SDK merges the contributions:

```
dotnet msbuild My.Binding.csproj -t:SwiftBindingsMergePackages \
  '-p:SwiftBindingsMergeContributions="x26.nupkg;x27.nupkg;n11.nupkg"' \
  '-p:SwiftBindingsMergeTargetFrameworks="net10.0-ios26.0;net10.0-ios27.2;net11.0-ios27.2"' \
  [-p:SwiftBindingsMergeOutput=dir]
```

The inner double quotes keep MSBuild from splitting the lists at their semicolons; the outer single
quotes keep the shell from removing them.

It refuses (SWIFTBIND138) contributions that are not one package (id, version, nuspec metadata
other than the per-copy groups, SwiftBindings SDK), a signed contribution, a matrix the contributions
do not carry exactly once each, a contribution whose `lib/`, `buildTransitive/` and `native/` TFM sets
differ or that ships files outside the copy layout, a copy without its declaration, targets or own
natives, a shared file that differs between contributions (the readme is taken from the newest
copy), an id or version that is not a plain file name, and — per platform, ordered by .NET and
platform version — a newer copy whose public API (types with the bases and interfaces they convert
to through the assembly's own base chain, members with their signatures, static or instance binding
and public or protected access; widening protected to public is allowed) or per-architecture native exports lack an
older copy's (a library compiles against the oldest copy,
the app that consumes it runs the newest). A type a signature, base or interface names from another
assembly is keyed with that assembly's name, so a same-named type of another assembly is another
type. The manifest-hash symbol (`SBTM_`) differs per copy by design and is not compared; the wrapper
identity `SBWI_<M>` has one name in every copy and a value of each copy's own. It writes a nuspec
with each copy's own dependency group, checks the Windows restore path budget for the real id and
version, and runs SWIFTBIND133 on the result. The
merged package is staged under a name of the merge's own until those checks pass, and the merge
refuses an output directory where the merged package would replace one of its own contributions
(compared through symbolic links and without case, as the default Apple and Windows volumes
resolve names).
Implementation: `src/Swift.Runtime/tasks/PackageMerge.cs`.

### Windows restore path budget

NuGet extracts each entry directly to `<packages>\<id-lower>\<version>\<entry>`, so an entry
restores under `MAX_PATH` (259 usable characters) at `C:\Users\<profile>\.nuget\packages\` when
it is at most `259 - (9 + profile + 17 + len("<id>.<version>") + 1)` characters; the merge budgets a
40-character profile. The dotnet CLI restores long paths on any Windows install; a Visual Studio
restore does not (NuGet/Home#3324, #11979), and `nuget.exe` or `msbuild -t:restore` need
`LongPathsEnabled`. The budget is a policy, not a property of the layout: Apple framework bindings
with long ids are already past it in the one-copy layout (`SwiftBindings.Apple.LiveCommunicationKit`
26.2.16: longest entry 179 against a budget of 144; RealityFoundation 167 against 147;
ProximityReader 164 against 149), because the framework name appears twice in each slice path and
the xcframework's slice directory names are fixed. Only renaming the wrapper framework would shorten
them, which changes every binding's native library name and still leaves the longest id over. The
merge therefore warns (SWIFTBIND140), naming the longest entry and the longest user profile a
Visual Studio restore still fits, and refuses only with `SwiftBindingsMergeRequireWindowsPathBudget=true`.
The copy layout is the same in a one-copy and a merged package, and a merge adds no entry deeper
than its deepest contribution's. This repository's own four packages are still held to the
budget as an error at pack (`Build.WindowsPathGuard.cs`).

## The gate

`nuke binding-tests --multi-toolchain-package` (`Build.BindingTests.MultiToolchainPackage.cs`, driver
`build/scripts/multi-toolchain-package.py`) proves this end to end on one Mac. It runs unprofiled and
packs one throwaway SwiftBindings feed; the driver takes each pairing's environment (`DOTNET_ROOT`,
`DEVELOPER_DIR`, SDK pin) from the toolchain launcher's own profile reader, so the three pairings come
from `build/toolchains/` and are validated as a profile run validates them. Each pairing packs two
Apple-framework fixtures with its explicit TFMs: ProximityReader (iOS and Mac Catalyst), whose 27.2
SDK adds `Element.Name`, so a member is present in the 27.2 copies and absent in the 26.0 copy, and
WeatherKit (macOS and tvOS). The gate merges them with `SwiftBindingsMergePackages` and checks the
layout, each copy's bytes against its contribution, the nuspec groups, the path budget and SWIFTBIND133.
It then builds consumers from clean NuGet caches (iOS simulator Debug and Release, a NativeAOT
`ios-arm64` publish on .NET 10, macOS, tvOS simulator, Mac Catalyst, and a versionless library through
a `ProjectReference`), checks each one's copy line, `project.assets.json` paths, embedded wrapper
identity and SDK, and SWIFTBIND132 verdict, and runs the apps. Controls that must fail with their own
code: NU1202, SWIFTBIND136, 132 and 133 (a wrapper from the older copy), 137, 138 (M1–M6), 139 and 140.
Receipts land in `artifacts/multi-toolchain-package/receipts/`.

