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

A pure Objective-C binding has no wrapper, so its copies have no `native/<T>/`. A binding of an
Apple framework (Matter) binds what the OS provides: each copy is its assembly, its declaration and
consumer targets that reference no natives. A binding of an Objective-C library carries the library
inside `lib/<T>/`, as the binding project's resource package (`<Id>.resources.zip`, or the
`<Id>.resources/` directory) that the consuming build extracts, and ships no consumer targets. Each
copy's declaration records which of the three a copy is (`NativePayload` below), and every guard
reads it from there.

## The copy declaration

`buildTransitive/<T>/<Id>.props` adds one `SwiftBindingPackageCopy` item: the copy TFM, the toolchain
and platform SDK it was built with, the SwiftBindings SDK version, where the copy's natives are
(`NativePayload`: `native` for `native/<T>/`, `none`, or `resources` for the resource package in
`lib/<T>/`), and the copy's `lib/`, `native/` and targets paths. A copy with no `native/<T>/` declares
an empty `NativeDirectory`, and one that ships no consumer targets an empty `TargetsFile`. A
declaration from before `NativePayload` means `native`. Every lane declares its copy, the pure
Objective-C ones included; a pure Objective-C assembly records no wrapper identity, so its label is
judged against the SDK selected when it packs. The generator writes it beside the consumer targets of a standalone project;
`SwiftBindings.Sdk` writes its own at pack time (`PrepareSwiftBindingPackageCopy`), since only the
pack knows the toolchain. One writer composes both (`PackageCopyDeclarationEmitter`).

## Guards

| Code | Where | What it refuses |
|---|---|---|
| SWIFTBIND136 | every app build (`SwiftBindings.Runtime` targets, `_SwiftBindingsCheckPackageCopies`, before `CoreCompile`) | a binding assembly resolved from another copy's `lib/` than the copy whose targets NuGet imported, or an assembly of a copy-layout package with no declared copy at all (a package is in the copy layout when it has a copy under `native/` or a copy declaration under `buildTransitive/`, so a pure Objective-C package is too), or a copy whose declared consumer targets are missing (a `native` copy always declares them). Each build also prints one line per package naming the copy it selected and its lib, native and targets paths. |
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
do not carry exactly once each, a contribution whose `lib/` and `buildTransitive/` TFM sets differ,
that ships files outside the copy layout or a `native/<T>/` with no `lib/<T>/`, a copy without its
declaration, a copy whose natives are not where its declaration says (`native/<T>/` exactly for a
`native` copy, a resource package in `lib/<T>/` exactly for a `resources` copy, neither for `none`), a
copy without the consumer targets it declares or with targets it declares it has not, consumer targets
of a copy without `native/<T>/` that reference natives, copies that declare different native payloads
(one binding ships its natives one way), a shared file that differs between contributions (the readme is taken from the newest
copy), an id or version that is not a plain file name, and — per platform, ordered by .NET and
platform version — a newer copy whose public API (types with the bases and interfaces they convert
to through the assembly's own base chain, members with their signatures, static or instance binding
and public or protected access; widening protected to public is allowed) or per-architecture native exports lack an
older copy's (a `resources` copy's exports are read from the images inside its resource package; a
`none` copy has none to compare) (a library compiles against the oldest copy,
the app that consumes it runs the newest). A type a signature, base or interface names from another
assembly is keyed with that assembly's name, so a same-named type of another assembly is another
type. A newer copy must also keep every value a compiler copies out of an older copy into the code
that uses it: each public or protected constant field (an enum member is one) and each parameter
default, keyed by member and, for a default, by parameter position. A library compiled against the
older copy holds those values as literals and keeps them in an app that runs the newer copy, so a
changed value, or one the newer copy no longer declares (a dropped default, a constant turned into a
field), changes what the library does with no member missing. The refusal names ten values with both
copies' values and the merge log names every one, as it names every removed member. The manifest-hash
symbol (`SBTM_`) differs per copy by design and is not compared; the wrapper
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

### Case identity of resilient enums

A Swift enum that is not `@frozen` is resilient: the library that defines it owns its case tags, and
a newer library may insert a case anywhere and renumber every later case (RealityFoundation 27.2 adds
`ModelDebugOptionsComponent.VisualizationMode.baseColorTexture` before `textureCoordinates`). Swift
clients read each case's tag from the case's exported "enum case" global (its mangled name with a
`WC` suffix) when they build or test the case. A binding that numbered its `CaseTag` constants by
declaration order would read and build other cases on an OS other than its SDK's, and its copies
would disagree on the constants a library compiled against the oldest one holds. So the `CaseTag`
constant of a non-frozen enum's case is derived from the case's name alone (31-bit FNV-1a, the next
free value on a collision; `EnumDecl.GetCaseId`), the same in every copy, and the binding translates
between those identities and the running library's tags through `Swift.Runtime.SwiftEnumCaseMap`,
which reads the case globals: the case discriminator maps the value witness tag to an identity (a
case the binding does not name reads as `SwiftEnumCaseMap.UnknownCase`), and a no-payload case
accessor injects the library's tag, refusing with `PlatformNotSupportedException` a case the loaded
library lacks. When the library loads but no case global resolves, the module was built without
library evolution and the declaration-order tags are the tags. A library the map cannot load by name
(a binding whose calls resolve through another resolver) is searched for among the process's loaded
images; with no case global there either, the map refuses (`DllNotFoundException`) rather than read
declaration order. Two case names whose hashes collide
take the next free value in name order, so a newer SDK adding a case whose hash collides with an
existing case's can move the later name's constant: the merge's value check refuses that copy, so the
collision cannot reach a consumer, and its fix is the generator's, not the package's. A `@frozen` enum's tag order is its ABI, so its constants stay
its tags. Simple enums projected as C# enums keep their declaration-order constants: their crossings
already go through the wrapper's case switch, and a constant that moves between copies is refused by
the merge's value check.

## The gate

`nuke binding-tests --multi-toolchain-package` (`Build.BindingTests.MultiToolchainPackage.cs`, driver
`build/scripts/multi-toolchain-package.py`) proves this end to end on one Mac. It runs unprofiled and
packs one throwaway SwiftBindings feed; the driver takes each pairing's environment (`DOTNET_ROOT`,
`DEVELOPER_DIR`, SDK pin) from the toolchain launcher's own profile reader, so the three pairings come
from `build/toolchains/` and are validated as a profile run validates them. Each pairing packs two
Apple-framework fixtures with its explicit TFMs: ProximityReader (iOS and Mac Catalyst), whose 27.2
SDK adds `Element.Name`, so a member is present in the 27.2 copies and absent in the 26.0 copy,
WeatherKit (macOS and tvOS), and CaseOrder (iOS), a first-party Swift library
(`build/scripts/multi-toolchain-fixtures/CaseOrder.swift`) each pairing builds with its own Xcode,
whose Swift 6.4 build inserts enum cases ahead of existing ones, as an Apple SDK does between
releases. Its copies merge only because their `CaseTag` constants do not follow case order. Matter
(iOS) is the pure Objective-C fixture: its copies ship no natives, declare so, and bind none of the
API its headers mark provisional (unavailable). The gate merges them with `SwiftBindingsMergePackages` and checks the
layout, each copy's bytes against its contribution, the nuspec groups, the path budget and SWIFTBIND133.
It then builds consumers from clean NuGet caches (iOS simulator Debug and Release, a NativeAOT
`ios-arm64` publish on .NET 10, macOS, tvOS simulator, Mac Catalyst, and a versionless library through
a `ProjectReference`), checks each one's copy line, `project.assets.json` paths, embedded wrapper
identity and SDK, and SWIFTBIND132 verdict, and runs the apps. The versionless library also compiles
calls into CaseOrder, case-tag comparisons included, against its oldest copy, and the app runs them
against its own copy and that copy's library: each Swift-built case must read as its own `CaseTag`,
and each managed-built case must reach Swift as itself. Controls that must fail with their own
code: NU1202, SWIFTBIND136 (on ProximityReader and on Matter), 132 and 133 (a wrapper from the older
copy), 137, 138 (M1–M8; M7 renumbers one CaseOrder `CaseTag` constant in the 27.2 copy, M8 makes a
Matter copy declare natives it does not ship), 139 and 140.
Receipts land in `artifacts/multi-toolchain-package/receipts/`.

