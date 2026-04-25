# Per-RID xcframework slicing — design

**Status:** Planning. No code yet.
**Tracking issue:** [#27](https://github.com/justinwojo/swift-dotnet-bindings/issues/27)
**Blocks:** 0.8.0 ship
**Related (closed):** [PR #28](https://github.com/justinwojo/swift-dotnet-bindings/pull/28)

## Problem

Multi-TFM Swift binding packages replicate the full source xcframework under
every `runtimes/<rid>/native/` directory, including slices the RID cannot use
(watchOS slices under `ios-arm64`, macOS slices under `tvos-arm64`, etc.).

Measured 2026-04-24 on a 3-TFM fixture (`ios26.2 / tvos26.2 / macos26.2`)
against Nuke.xcframework (7 slices):

- `runtimes/ios-arm64/native/Nuke.xcframework/` — all 7 slices (39MB)
- `runtimes/osx-arm64/native/Nuke.xcframework/` — all 7 slices (39MB, identical)
- `runtimes/tvos-arm64/native/Nuke.xcframework/` — all 7 slices (39MB, identical)
- Total: 341 files, 121MB extracted, 23MB on-wire
- `NU5123` long-path warnings on watchOS slice paths at each RID

Consumers restore all three identical-content directories and every one of
their unused slices. The on-wire cost is modest (outer nupkg zip compresses
duplication), but the on-disk / restore cost is ~5× larger than necessary and
the unused slices produce packaging warnings that shouldn't exist.

## Non-goals

- Zipping xcframeworks before packing. Evaluated in PR #28 and rejected:
  partial fix that hides NU5123 but preserves semantic duplication (all 3 zips
  were byte-identical in the measured case, still contained every slice).
  Zipping remains on the table as a **secondary** toggle
  (`SwiftPackXcframeworkAsZip`) after slicing lands, if file-count or
  long-path warnings persist post-slicing.
- Multi-architecture source xcframeworks. We continue to accept source
  xcframeworks with any combination of architectures per slice
  (e.g. `ios-arm64_x86_64-simulator`). We copy the slice dir verbatim —
  slicing only filters which slice dirs are included, not their contents.
- **Wrapper and bridge xcframeworks (v1).** Those are generated per-TFM by our
  pipeline and already contain only the slices the target RID uses; they have
  no duplication problem to solve. Pack them as-is. Revisit only if a later
  consistency argument demands uniform treatment — not in the initial change.

## Goals

1. Each RID's `runtimes/<rid>/native/<Name>.xcframework/` contains only slices
   that RID can consume.
2. The on-disk layout after restore drops from ~121MB to ~10MB for the
   measured 3-TFM Nuke case.
3. No `NU5123` warnings from unused watchOS slices on non-watchOS RIDs.
4. `BindingProjectEmitter`-generated csprojs and the SDK pack target use the
   **same** asset-shape logic — no divergence between CLI and SDK paths.
5. Validated end-to-end: PackGate asserts the sliced layout; at least one
   consumer restore + build succeeds on the sliced output.

## RID → slice predicate

For each `AvailableLibraries` entry in the source Info.plist, include the
slice dir under `runtimes/<rid>/native/<Name>.xcframework/` iff:

| NuGet RID | SupportedPlatform | SupportedPlatformVariant |
|---|---|---|
| `ios-arm64` | `ios` | empty (device) OR `simulator` — but **not** `maccatalyst` |
| `tvos-arm64` | `tvos` | empty (device) OR `simulator` |
| `osx-arm64` | `macos` | empty (device only) |
| `maccatalyst-arm64` | `ios` | `maccatalyst` |

`ios-arm64` and `tvos-arm64` RIDs intentionally ship both device and simulator
slices so a single package works for both `dotnet build` and simulator dev
loops. The Apple workload's `_ExpandNativeReferences` picks the right slice
at build time based on runtime identifier.

If a source xcframework has zero compatible slices for the current TFM, the
generator has already failed at resolution time (`XCFrameworkResolver`
requires a slice to resolve ABI JSON + dylib inputs). The slicer defensively
errors (`SWIFTBIND050`, TBD code) if it's called with a zero-slice result —
this should be unreachable given the earlier gate.

## Chosen approach

**One shared slicer library, two invocation points.**

- `XCFrameworkSlicer` class in `src/Swift.Bindings/src/Configuration/` — pure
  library, unit-testable. Reuses `PlistReader.ReadPlistDict` (handles binary
  + XML plists) to load the root Info.plist dictionary, then feeds that
  dictionary into a new shared helper `ParseAvailableLibraries(rootDict)`.
  `XCFrameworkResolver.ParseInfoPlist` is refactored to delegate to the same
  helper, so both the resolver and the slicer handle binary plists uniformly.
- New generator **mode flag** `--slice-xcframework` on the generator binary
  (a mode flag in the existing `System.CommandLine` pipeline, not a
  restructured subcommand). Takes `--xcframework <src>`, `--rid <rid>`,
  `-o <dst>`. Writes a staged xcframework containing only compatible slices
  plus a pruned root `Info.plist`.
- Two invocation points share the same slicer library but differ in **when**
  they run:
  - **SDK path (primary)**: `_ConfigureSwiftBindingPack` in `Sdk.targets`
    runs the slicer inside a `<Target>` with per-TFM `Inputs`/`Outputs`
    before `TfmSpecificPackageFile` items are added. One invocation per TFM,
    scoped to the source xcframework.
  - **CLI path**: `BindingProjectEmitter` runs the slicer at **generation
    time** and emits the generated csproj with `<None
    Include="…sliced/…/**" Pack="true">` pointing at the already-staged
    output. This avoids the generated standalone csproj needing to locate
    `Swift.Bindings.dll` at pack time (the binary is not guaranteed to be on
    the standalone-consumer machine at pack time, only at generation time).
    Standalone CLI projects are almost always single-TFM, so generation-time
    slicing is the simpler trade.
- Staging location:
  - SDK path: `$(IntermediateOutputPath)sliced/<rid>/<Name>.xcframework/`.
    Scoped per-RID so multi-TFM projects don't race.
  - CLI path: `<OutputDir>/obj/sliced/<rid>/<Name>.xcframework/`. The
    generator writes the sliced copy alongside the csproj it emits.
- `TfmSpecificPackageFile Include="…/sliced/<rid>/<Name>.xcframework/**"`
  (SDK path) and `<None Include="…sliced…/**" Pack="true">` (CLI path) both
  target `PackagePath="runtimes/<rid>/native/<Name>.xcframework/"`.
- `ConsumerTargetsEmitter` is **unchanged** — consumer targets still reference
  `.xcframework` directories; the asset extension doesn't change.

### Why the two paths invoke differently

Both paths *could* slice at pack time if standalone csprojs could locate the
generator binary. They cannot — standalone projects have no
`Swift.Bindings.Sdk` import and therefore no `$(_SwiftBindingGeneratorDir)`.
Emitting a hard-coded dev-box path would be fragile; adding a
`SwiftBindingsSlicerCommand` property plus a fail-loud check is possible but
adds user surface area that generation-time slicing makes unnecessary. The
trade-off:

| Trade-off | SDK pack-time slicing | CLI generation-time slicing |
|---|---|---|
| Staleness if user edits source xcframework between generate and pack | Re-slices at pack (fresh) | User must re-run generator to re-slice |
| Requires generator binary at pack time | No — SDK resolves it | Yes, but SDK path only |
| Works for multi-TFM projects | Yes | CLI projects are typically single-TFM |

For standalone CLI projects the "edit source xcframework between generate
and pack" flow isn't idiomatic — users re-generate when the source
xcframework changes. Generation-time slicing is the simpler trade.

### Alternatives considered

- **Pure-MSBuild slicing (Copy / XmlPeek).** Rejected. `Info.plist` can be
  either XML or binary (plutil-converted); MSBuild's `XmlPeek` can't handle
  binary plists, and shelling out to `plutil` inside MSBuild inline tasks is
  awkward. We already have robust C# parsing — reuse it.
- **Inline `UsingTask` with `RoslynCodeTaskFactory`.** Rejected. Compiles a
  task assembly on every build unless cached; introduces a new pattern not
  used anywhere else in this repo.
- **Slice at pack time on both paths with a `SwiftBindingsSlicerCommand`
  user-surface property.** Rejected for v1. Adds user-facing surface for a
  marginal flow (edit source xcframework, re-pack without re-generate). Can
  revisit if that flow turns out to matter.
- **Ship slicer as a separate tool nupkg.** Rejected for scope. The generator
  binary is already shipped as part of the SDK and already exposes multiple
  CLI mode flags. One more flag is cheaper than a new tool surface.

## Change list (file by file)

### New files

- `src/Swift.Bindings/src/Configuration/XCFrameworkSlicer.cs`
  Pure library. One public static method: `Slice(string sourceXcfwPath,
  string nuGetRid, string destPath, ILogger logger, ICommandRunner?
  runner = null)`. Uses `PlistReader.ReadPlistDict` to load the root
  Info.plist (handles binary + XML transparently). Calls
  `ParseAvailableLibraries(rootDict)` — the new shared helper — to get the
  slice list, filters by RID, copies matching slice directories using
  `ditto` (preserves symlinks, xattrs, executable bits, and per-slice
  `_CodeSignature`), writes a pruned XML `Info.plist` under `destPath`.
  Throws `SWIFTBIND050` on zero-slice match with a clear platform/RID
  message. On non-macOS hosts the slicer fails loud (`ditto` is macOS-only);
  this matches the repo's existing macOS-only assumption for pack.
- `src/Swift.Bindings/tests/Configuration/XCFrameworkSlicerTests.cs`
  Unit tests — fabricate an xcframework directory tree (including symlinks
  inside `.framework` bundles where the Mach-O is symlinked from
  `Versions/A/Foo` to `Foo`, and xattr-carrying files), assert correct
  filtering per RID, pruned `Info.plist` structure, file-count correctness,
  symlink preservation, executable-bit preservation. Include a binary-plist
  test case (synthesized via `plutil -convert binary1`) to verify the
  `PlistReader` path works end-to-end.

### Modified files

- `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs` (lines
  591–637). Refactor `ParseInfoPlist` to load via `PlistReader.ReadPlistDict`
  (currently uses `XmlDocument.Load` directly and does not handle binary
  root plists), then delegate slice extraction to the new shared
  `ParseAvailableLibraries(Dictionary<string, object> rootDict)` helper.
  Both `XCFrameworkResolver` and `XCFrameworkSlicer` call the same helper.
- `src/Swift.Bindings/src/Program.cs` (or `BindingsGeneratorCommand.cs` —
  whichever owns CLI dispatch).
  Add `--slice-xcframework` as a mode flag in the existing pipeline (not a
  new `System.CommandLine` subcommand — a mode flag is consistent with
  `--emit-apple-types-cs`, `--compile-bridge-only`, etc. already in place).
  Dispatches to `XCFrameworkSlicer.Slice`. Exit code 0 on success, non-zero
  with `SWIFTBIND`-prefixed error on failure.
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — `_ConfigureSwiftBindingPack`.
  Replace the current raw-dir `TfmSpecificPackageFile Include=
  "%(SwiftFramework.Identity)/**"` for the source xcframework (lines
  1647–1649) with:
    1. A new `<Target Name="_SliceSourceXcframework"
       BeforeTargets="_ConfigureSwiftBindingPack"
       Inputs="@(_SliceSourceInput)"
       Outputs="$(_StagedSourceXcfwDir).stamp">` that `<MakeDir>`s, runs the
       slicer `<Exec>`, and `<Touch>`es the stamp. `_SliceSourceInput`
       collects `%(SwiftFramework.Identity)/**/*` (the full source tree, so
       a binary update without an `Info.plist` change still invalidates)
       plus `$(_SwiftBindingGeneratorDir)Swift.Bindings.dll` (so an emitter
       change invalidates cached slices — same pattern as
       `_GenerateAppleTypeSources` in `Swift.Bindings.Apple.csproj`).
    2. In `_ConfigureSwiftBindingPack`, emit `TfmSpecificPackageFile
       Include="$(_StagedSourceXcfwDir)**"
       PackagePath="runtimes/$(_SwiftBindingNuGetRid)/native/$(_SwiftBindingModuleName).xcframework/"`.
  Wrapper and bridge `TfmSpecificPackageFile` items (lines 1650–1655)
  are left unchanged — v1 does not repack them.
- `src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs` (lines 104–113
  for source, 135–142 for wrapper, 161–168 for bridge).
  Source xcframework: call `XCFrameworkSlicer.Slice` during generation,
  write output to `<OutputDir>/obj/sliced/<rid>/<Name>.xcframework/`,
  update the emitted `<None Include=…>` item to reference that path.
  Wrapper/bridge: emit unchanged (raw-dir, per-TFM, no duplication problem
  in v1).
- `build/Build.PackGate.cs` — `ExpectedXcframeworkLayout` (around line 191).
  Update per-RID expectations for the **source** xcframework slicing path
  (wrapper/bridge expectations unchanged in v1):
    - `ios-arm64` → `{ "ios-arm64", "ios-arm64-simulator" }` exact set
    - `tvos-arm64` → `{ "tvos-arm64", "tvos-arm64-simulator" }` exact set
    - `osx-arm64` → `{ "macos-arm64" }` exact set
    - `maccatalyst-arm64` → `{ "ios-arm64-maccatalyst" }` exact set
  PackGate should assert **exact** slice sets for the source xcframework
  (no extra slices present, no required slices missing), so a regression
  where slicing silently stops working fails loud instead of shipping
  everything.
  Since the TipKit fixture currently used by PackGate is an Apple-framework
  (no source xcframework), PackGate needs a **new** multi-platform source
  xcframework fixture to exercise slicing — see next bullet.
- `build/` — add a new PackGate source-xcframework fixture. Candidate: a
  trimmed Nuke.xcframework committed to the repo (or fetched via
  `nuke fetch`), carrying at least iOS + tvOS + macOS + watchOS slices so
  the slicer has real platforms to filter out. The fixture must be durable
  (in-repo or fetched via the existing `nuke fetch` target) — **not**
  `/tmp/issue27-repro/fixture/`, which is ad-hoc and will disappear.
- `src/Swift.Bindings.Sdk/tests/` (or equivalent packaging integration
  location) — add a packaging-integration test that packs the fixture and
  runs a consumer-side restore + build for **both** `iossimulator-arm64`
  and `ios-arm64` (device) paths. RID-graph fallback means the simulator
  path restores from the `ios-arm64` RID; both must produce a working
  build.

### Not modified

- `src/Swift.Bindings/src/Emitter/ConsumerTargetsEmitter.cs` — consumer
  targets still reference `.xcframework` directories; extension unchanged.
- `src/Swift.Runtime/` — runtime is unaffected; pack shape is build-time only.
- `BindingTests/` — slicing is packaging behavior, not ABI behavior. No
  runtime tests are the right layer for this. PackGate + the packaging
  integration test cover it.

## Risks and mitigations

- **Framework layout fidelity.** macOS frameworks depend on symlinks
  (`Versions/A/...` with a `Current` symlink), extended attributes (quarantine
  bits, codesign metadata), and executable bits on Mach-O binaries. A naive
  C# recursive copy does not preserve these. **Mitigation:** the slicer uses
  `ditto` (macOS's metadata-preserving copy tool) rather than a C#
  `Directory.Copy`-equivalent. Unit tests synthesize an xcframework with
  symlinks + xattrs + exec bits and assert they survive the copy.
- **Codesign.** Each `.framework` inside an xcframework slice may be
  codesigned. `ditto`-based copying preserves per-slice `_CodeSignature/`,
  symlinks, and xattrs, so per-framework signatures should remain valid. The
  xcframework's *root* signature (if any — most aren't root-signed) would
  break when we rewrite `AvailableLibraries`; Apple tooling validates
  per-framework signatures at load time, not the root xcframework.
  **Mitigation:** during implementation, pack a signed xcframework
  (StoreKit-style Apple-framework or a signed vendored fx), run `codesign
  -v -v` over each staged `.framework`, document the result in the commit.
  If any toolchain path turns out to require a valid root signature, fall
  back to re-signing the staged xcframework root.
- **Incremental rebuild.** Slicer output must invalidate when the source
  xcframework changes. Missing a change (e.g. a binary update that doesn't
  touch `Info.plist`) would ship stale binaries. **Mitigation:** wrap the
  slicer `<Exec>` in a dedicated `<Target>` (Inputs/Outputs live on
  `<Target>`, not `<Exec>` — that's an MSBuild semantics constraint). Inputs
  include the **full source xcframework tree** (`%(SwiftFramework.Identity)/**/*`
  not just `Info.plist`) plus the slicer binary
  (`$(_SwiftBindingGeneratorDir)Swift.Bindings.dll`) so emitter changes
  invalidate cached slices — same pattern as `_GenerateAppleTypeSources` in
  `Swift.Bindings.Apple.csproj`. Outputs: a `.stamp` file `<Touch>`-ed after
  the `<Exec>` succeeds.
  **Fallback during implementation:** if recursive globbing on `Inputs` turns
  noisy or expensive under MSBuild item batching on large xcframeworks, drop
  the glob and use a generated manifest/stamp instead — a small target
  materializes `$(_SliceInputManifest)` listing `Info.plist` + every file
  path+mtime under the source xcframework, and the slicer target takes
  `Inputs="$(_SliceInputManifest);$(_SwiftBindingGeneratorDir)Swift.Bindings.dll"`.
  Start with the direct glob; fall back only if it causes trouble.
- **Standalone CLI csproj + missing generator binary.** A generated
  standalone csproj can't assume `Swift.Bindings.dll` is resolvable at pack
  time on the user's machine. **Mitigation:** CLI path slices at **generation
  time**, embedding the sliced output in `obj/sliced/…/` at generation. The
  emitted csproj references the already-staged output — no pack-time slicer
  dependency. Trade-off (edit source xcframework between generate and pack
  → stale slice) is flagged in the Chosen Approach section; acceptable
  because the idiomatic CLI flow is re-generate when the source changes.
- **Already-sliced source xcframeworks.** A user may ship a source xcframework
  that only contains (say) iOS slices. For a TFM whose RID is non-iOS, the
  slicer produces zero output. **Mitigation:** fail fast at the slicer with
  `SWIFTBIND050` pointing at the TFM and expected platform. This is the same
  failure class that `XCFrameworkResolver` catches at generation time, but
  slicing can run at pack (SDK path), so we need a user-friendly code + text.
- **Idempotency.** If the user pre-sliced their source xcframework, slicing
  should be a no-op. **Mitigation:** verify with a unit test that
  `Slice(alreadySliced, rid, dst)` produces content-equivalent output
  (symlinks, xattrs, exec bits all preserved) for the matching RID.
- **Dual pack path drift.** CLI and SDK paths must emit the same asset shape.
  **Mitigation:** the pack-path template (`runtimes/<rid>/native/<Name>.xcframework/`)
  is derived by one function in shared C# code consumed by both emitters.
  A unit test asserts both paths produce the same `PackagePath` string for
  the same inputs. The slicer itself is identical — both paths call the
  same `XCFrameworkSlicer.Slice(...)` method.

## Test strategy

1. **Unit tests** (`XCFrameworkSlicerTests`):
   - Synthesize a fabricated xcframework on disk with 7 slices including
     symlinks (`.framework/Versions/A/Foo` ↔ `.framework/Foo`), xattrs
     (`xattr -w com.apple.metadata:test 1 <file>`), and executable bits on
     Mach-O stand-ins. Assert correct filter per RID.
   - Zero-slice match → throws `SWIFTBIND050`.
   - Identity slicing (source already contains only RID-compatible slices)
     → no-op; output is content-equivalent (symlinks + xattrs + exec bits
     preserved).
   - Binary plist input: synthesize via `plutil -convert binary1`, assert
     the `PlistReader`-based path parses it correctly.
   - Pruned `Info.plist` is valid XML and contains exactly the filtered
     `AvailableLibraries` entries.
2. **PackGate** (`nuke PackGate`): update `ExpectedXcframeworkLayout` to
   assert **exact** per-RID slice sets for the source xcframework (no
   extras, no missing). Add a durable multi-platform source xcframework
   fixture (trimmed Nuke, in-repo or fetched via `nuke fetch`) so
   slicing actually runs — the current TipKit fixture is Apple-framework
   mode (no source xcframework). Wrapper/bridge expectations unchanged in
   v1.
3. **Packaging integration test**: pack a durable in-repo multi-TFM fixture
   (committed at `BindingTests/Fixtures/issue27-slicing/` or equivalent),
   assert:
   - File count per RID matches expected sliced count.
   - Extracted size within expected bound.
   - No NU5123 warnings in pack log.
   - Consumer-side: `dotnet restore` + `dotnet build` succeeds for **both**:
     - `iossimulator-arm64` RID (simulator path; restores from `ios-arm64`
       RID via the NuGet RID graph fallback).
     - `ios-arm64` RID (device/publish path).
   - If possible, verify an Apple-workload `_ExpandNativeReferences` run
     selects the correct slice for each path.
4. **Manual codesign verification**: pack a signed xcframework, run
   `codesign -v -v` over each staged framework, confirm valid signatures.
   Document outcome in the commit message.

## Rollout

Single PR against main. No feature flag — the change is internal to pack;
consumer-visible surface is only "fewer files, same functionality". Prior
published 0.7.x packages with raw-directory layout continue to work; consumers
of the new 0.8.0 packages get sliced layout.

## Open questions

- Should `SwiftPackXcframeworkAsZip` be introduced as an opt-in escape valve
  in the same PR, or deferred? **Leaning deferred** — slicing likely
  eliminates the motivation for zipping (no more long-path slices, no more
  duplication). Revisit if we see NU5123 reports after slicing ships.
- Does Mac Catalyst genuinely take `maccatalyst-arm64` as its NuGet RID
  today, or is it `osx-arm64` with a platform variant? Current Sdk.props sets
  `_SwiftBindingNuGetRid` to `maccatalyst-arm64` for Catalyst TFMs — proceed
  with that assumption and verify the .NET Apple workload resolves it.
- PackGate currently asserts slices exist; should it also assert slices
  **don't** exist (e.g. `osx-arm64` RID must not contain `ios-arm64`)?
  **Yes — resolved**: exact-set assertion prevents regressions where slicing
  silently stops working and the fallback is "ship everything".
- Will the CLI path ever need multi-TFM pack support? Today `BindingProjectEmitter`
  emits single-TFM standalone csprojs, so generation-time slicing is trivially
  correct for that surface. If we later emit multi-TFM standalone csprojs,
  we'd need to slice once per TFM at generation time OR move to the SDK-style
  pack-time slicing with a `SwiftBindingsSlicerCommand` property. **Resolved
  for v1** by scoping to single-TFM CLI output, which matches current behavior.
