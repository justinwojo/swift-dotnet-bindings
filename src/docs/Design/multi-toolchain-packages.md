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

`<T>` is the copy's **full** target framework, for example `net10.0-ios26.0`. NuGet chooses `lib/`
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
| SWIFTBIND137 | pack (`PrepareSwiftBindingPackageCopy`) | an Apple-framework copy labelled for another platform SDK than the one it was generated from: the label must have the SDK's major version and be no newer. A library binding is never refused, so a versionless `net10.0-ios` library binding packed on Xcode 27.2 keeps its 26.0 label and serves .NET 10 + Xcode 26 apps: its surface is the library's, not the SDK's. |
| SWIFTBIND138 | pack and merge | consumer targets that reference `runtimes/` or another copy's `native/`; at merge, every refusal below. |
| SWIFTBIND139 | every app build | a copy older than the build's toolchain (another .NET version, or a lower platform SDK major): an informational message by default, an error with `SwiftBindingsRequireExactCopy=true`. |
| SWIFTBIND133 | pack and merge | the transition check, paired by full TFM: `lib/<T>/` is checked only against `native/<T>/`. |
| SWIFTBIND132 | every app build | reads the natives the app actually references, which are the selected copy's (SWIFTBIND136 guarantees the assembly is that copy's too). |

The build's own transition-evidence and packed residual-guard checks pair assemblies and natives the
same way (`PackagedNative.Serves`).

## Merge

Each pairing packs its own explicitly versioned TFMs from one source revision and one SwiftBindings
SDK version; the pipeline owns the matrix. The SDK merges the contributions:

```
dotnet msbuild My.Binding.csproj -t:SwiftBindingsMergePackages
  -p:SwiftBindingsMergeContributions="x26.nupkg;x27.nupkg;n11.nupkg"
  -p:SwiftBindingsMergeTargetFrameworks="net10.0-ios26.0;net10.0-ios27.2;net11.0-ios27.2"
  [-p:SwiftBindingsMergeOutput=dir]
```

It refuses (SWIFTBIND138) contributions that are not one package (id, version, nuspec metadata
other than the per-copy groups, SwiftBindings SDK), a signed contribution, a matrix the contributions
do not carry exactly once each, a contribution whose `lib/`, `buildTransitive/` and `native/` TFM sets
differ or that ships files outside the copy layout, a copy without its declaration, targets or own
natives, a shared file that differs between contributions (the readme is taken from the newest
copy), and — per platform, ordered by .NET and platform version — a newer copy whose public API or
per-architecture native exports lack an older copy's (a library compiles against the oldest copy,
the app that consumes it runs the newest). The manifest-hash symbol (`SBTM_`) differs per copy by
design and is not compared. It writes a nuspec with each copy's own dependency group, checks the
Windows restore path budget for the real id and version, and runs SWIFTBIND133 on the result.
Implementation: `src/Swift.Runtime/tasks/PackageMerge.cs`.
