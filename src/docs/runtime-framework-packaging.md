# Repackage the runtime shim as a framework (issue #42, real root cause)

Status: **implemented (2026-06-14), reviewed (Codex + Grok).** Supersedes the SwiftSupport-folder
approach in `swiftsupport-app-store-fix.md` (which treated a symptom). Captured 2026-06-14.

Both reviewers' verdict: this is the correct, complete root-cause fix — no architectural flaw.
Three amendments below (marked **[review]**) were folded in after the review:
- **Flat framework slices everywhere, not versioned.** No `Versions/A` for macOS/Catalyst.
  Our own generated bindings ship flat macOS slices (`BindingTests/output-macos/SwiftBindings.xcframework/macos-arm64/SwiftBindings.framework/Info.plist`)
  that pass `validate --macos`; SBApple builds flat (`Build.AppleSupplement.cs:222`); `PlistGenerator.WriteFrameworkPlist`
  only emits the flat shape. Only third-party `.libraries/` (Firebase/Realm) are versioned.
- **No ProjectReference-propagation shim needed.** `NativeReference` does not flow across a
  `ProjectReference` — but no in-tree consumer relies on that. Every Apple-app ProjectReference
  consumer sets `IncludeSwiftBindingsRuntimeNative=false` and gets the runtime via harness
  injection (only `CompileCheck`, a compile-only gate, leaves it default). Real consumers use
  the NuGet package (`buildTransitive`). Today's csproj `<Content>` propagation only carries the
  *buggy loose dylib* anyway, so dropping it removes a defect, not a feature.
- **Harden the gate with nupkg + slice-arch assertions** (D6).

## TL;DR

App Store Connect rejects the reporter's iOS app (`ITMS-90426` "SwiftSupport folder is
missing", then `ITMS-90429` "files … aren't at the expected location /Frameworks", and the
sibling `ITMS-90171`). All three are **symptoms of one root cause**, documented verbatim by
Apple in **TN2435** ("Embedding Frameworks In An App", section *Embedded .dylib Files*):

> "Dynamic libraries outside of a framework bundle, which typically have the file extension
> `.dylib`, are not supported on iOS, watchOS, or tvOS, except for the system Swift libraries
> provided by Xcode."

We ship our runtime as a **bare, loose `libSwiftBindingsRuntime.dylib`** in the app's
`Frameworks/`. It is a Swift Mach-O (`__swift5_*` sections, links `/usr/lib/swift/libswift*`)
whose name does not begin with `libswift` — exactly the shape TN2435 says to convert to a
framework. TN2435's own example `90429` lists the same `libswiftDarwin.dylib …
libswiftFoundation.dylib …` set the reporter saw.

**Fix:** ship the runtime as `SwiftBindingsRuntime.framework` (inside an
`SwiftBindingsRuntime.xcframework`), referenced via `<NativeReference Kind="Framework">` —
the *exact* shape every other Swift framework in this repo already uses. Then **delete** the
SwiftSupport injector: a stable-ABI app that embeds no loose dylib and no `libswift*` needs no
SwiftSupport folder at all (that is what a normal Xcode Swift app produces).

The SwiftSupport work was chasing a misleading Apple error. It cannot make this app pass
(it converts `90426` into `90429`) and adds ~47 MB. Reporter's own hypothesis was correct;
two independent reviewers (Codex, Grok) and TN2435 concur.

## Why a framework, and why an xcframework specifically

The runtime is the **only** loose dylib this repo drops into a consumer app. Every other
Swift framework already ships the correct way:

- `SwiftBindings.Apple` → `SBApple.xcframework`, referenced as
  `<NativeReference Kind="Framework">` (`build/SwiftBindings.Apple.targets`).
- Every generated binding → `<Module>.xcframework`, referenced as
  `<NativeReference Kind="Framework">` (SDK `_ResolveSwiftNativeReferences`, emitted
  `{PackageId}.targets`, and the checked-in `BindingTests/output/*.targets`).

These are embedded, code-signed, and **dynamically** loaded via the resolver's first search
candidate `@rpath/{name}.framework/{name}`. The Kidoz wrapper framework the reporter ships
on his physical device is loaded exactly this way and Apple never complains about it — only
about our loose dylib. So the framework form is *proven in-repo on device*, including for the
reporter. The runtime was simply the lone holdout on the old `<Content>`/loose-dylib path.

The managed side is already ready:

- `SwiftFrameworkResolver.GetSearchPaths` probes `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`
  **first** (`SwiftFrameworkResolver.cs:175`). No resolver change.
- All P/Invoke callers use the bare name `"SwiftBindingsRuntime"` (Arc.cs, SwiftHandle.cs,
  SwiftString.cs, TypeMetadata.cs, SwiftConcurrency.cs, SwiftClosureContext.cs,
  SwiftCollectionCdeclWrappers.cs, SwiftSet.cs, SwiftKeyPath.cs). No DllImport change.
- The generator/emitter never emits a DllImport against the runtime — all entry points go
  through the shared `Swift.Runtime` assembly. No generated-code change.

This is a **packaging-only** change on the managed/native boundary.

## Decisions

### D1 — Ship `SwiftBindingsRuntime.xcframework`, reference via `NativeReference Kind="Framework"`

Replace the six per-platform `<Content Link="Frameworks/libSwiftBindingsRuntime.dylib">` /
`<NativeReference Kind="Dynamic">` items (in both `Swift.Runtime.csproj` and
`buildTransitive/SwiftBindings.Runtime.targets`) with a **single** `<NativeReference
Kind="Framework">` to the xcframework, gated on an Apple TFM + file existence. The xcframework
carries all slices; the workload selects by RID, embeds the right `.framework` into
`App.app/Frameworks/`, and signs it — same mechanism that already works for bindings on
sim + device.

### D2 — Cover all Apple platforms in the one xcframework

TN2435 restricts loose dylibs on iOS/watchOS/tvOS only; macOS/Catalyst legitimately allow
them. But shipping one consistent framework everywhere removes a whole class of latent
"macOS/Catalyst variant of #42" risk and collapses six conditional item groups into one.
Slice set (mirrors what bindings already ship):

| Slice id | triples |
|---|---|
| `ios-arm64` | `arm64-apple-ios15.0` |
| `ios-arm64_x86_64-simulator` | `arm64`/`x86_64 -ios15.0-simulator` |
| `tvos-arm64` | `arm64-apple-tvos15.0` |
| `tvos-arm64_x86_64-simulator` | `arm64`/`x86_64 -tvos15.0-simulator` |
| `macos-arm64_x86_64` | `arm64`/`x86_64 -macosx12.0` |
| `ios-arm64_x86_64-maccatalyst` | `arm64`/`x86_64 -ios15.0-macabi` |

**[review] All slices are FLAT frameworks** (binary + `Info.plist` at the `.framework/` root —
no `Versions/A`), mirroring SBApple and every generated binding. The runtime carries no Swift
module interface (consumers P/Invoke `@_cdecl` symbols, never `import` it), so a slice is just
the binary + `Info.plist` — even simpler than SBApple (no `Modules/` dir).

**Fallback (documented, not first choice):** if a macOS/Catalyst slice proves troublesome to
embed/sign, keep macOS + Catalyst on the existing loose-dylib `<Content>` / `Kind="Dynamic"`
path (legal there per TN2435) and ship only the iOS/tvOS slices as a framework. The resolver
finds both forms, so a hybrid is functionally safe — only less tidy.

### D3 — Build mechanics (`build-runtime.sh`)

1. Change install_name to `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`
   (currently `@rpath/libSwiftBindingsRuntime.dylib`, line 69).
2. For each slice: build the binary as today (`swiftc -emit-library` + the collections `.o`,
   `lipo` for fat slices), then wrap it in a **flat** `.framework` — binary named
   `SwiftBindingsRuntime` at the root + `Info.plist` (via the same `Info.plist` shape
   `PlistGenerator.WriteFrameworkPlist` emits). Code-sign each slice's binary (`codesign -s -`).
3. Combine all six slices with `xcodebuild -create-xcframework -framework … -output
   native/SwiftBindingsRuntime.xcframework` (auto-generates the xcframework `Info.plist`,
   deriving each slice id from the binary's platform load command).
4. Output replaces `src/Swift.Runtime/native/<platform>/libSwiftBindingsRuntime.dylib`
   with the committed `src/Swift.Runtime/native/SwiftBindingsRuntime.xcframework/` tree.

The slice id → triple mapping is the table in D2. This mirrors SBApple's
`RunBuildAppleSupplementXcframework` (`Build.AppleSupplement.cs:69`) recipe exactly, minus the
`Modules/` dir (the runtime has no Swift module interface). `build-runtime.sh` stays the single
manual regeneration entry point; the committed artifact is the xcframework tree.

### D4 — Packaging (csproj + targets)

- `Swift.Runtime.csproj`: replace the six `<Content>`/`<NativeReference>` item groups
  (lines 65-114) and the six `<None … Pack … native/<platform>>` pack items (lines 147-152)
  with: a recursive pack of the xcframework tree (`<None Include="../native/SwiftBindingsRuntime.xcframework/**"
  Pack="true" PackagePath="native/SwiftBindingsRuntime.xcframework">` — preserve directory
  structure so the framework bundles survive packing), and a single
  `IncludeSwiftBindingsRuntimeNative`-gated, Apple-TFM-gated, `Exists`-gated
  `<NativeReference Kind="Framework">` for Swift.Runtime's own Apple build.
- **[review]** No ProjectReference-propagation shim: every in-tree Apple-app consumer sets
  `IncludeSwiftBindingsRuntimeNative=false` and harness-injects; the csproj `NativeReference` not
  flowing across `ProjectReference` breaks nothing.
- `buildTransitive/SwiftBindings.Runtime.targets`: replace the six item groups (lines 49-115)
  with the single Apple-gated `<NativeReference Kind="Framework">` to
  `$(MSBuildThisFileDirectory)../native/SwiftBindingsRuntime.xcframework`. This is the real
  consumer path (PackageReference + transitively SDK-direct) — the one the #42 reporter uses.
- Keep `IncludeSwiftBindingsRuntimeNative` opt-out and the `_SwiftBindingsInjectRuntimeFlavorSwitch`
  and ILLink/IlcArg targets unchanged.

### D5 — Delete the SwiftSupport injector

A framework-packaged stable-ABI app needs no SwiftSupport folder. Remove:
- `_SwiftBindingsAddSwiftSupportFolder` + `_SwiftBindingsAddSwiftSupportFolderToArchive`
  targets and the `EnableSwiftSupportFolder` property (`SwiftBindings.Runtime.targets`).
- `src/Swift.Runtime/src/build/add-swiftsupport-folder.sh` and its pack `<None>` item.
- The `--swiftsupport` doc block in CLAUDE.md.
- Rewrite `swiftsupport-app-store-fix.md` to record the corrected TN2435 diagnosis and point
  here (preserve the diagnostic history; mark the folder approach superseded). Do **not**
  silently delete it — it explains *why* the first attempt was wrong.

We do not keep the injector dormant-behind-opt-in: it implements an incorrect model, and a
genuine future back-deployment path (embed `libswift*` in `Frameworks/` **and** mirror in
SwiftSupport, `swift-stdlib-tool` style) would be written fresh against that real need. Git
history preserves the code.

### D6 — Rewrite the gate to assert TN2435 hygiene

Repurpose the gate file (renamed `Build.BindingTests.SwiftSupport.cs` →
`Build.BindingTests.AppStoreHygiene.cs`; flag renamed `--swiftsupport` →
`--appstore-hygiene`). Assertions on a device IPA built through the runtime:
- `App.app/Frameworks/` contains **no** loose `libSwiftBindingsRuntime.dylib`.
- The app embeds **zero** `libswift*.dylib` anywhere (stable-ABI link), so no SwiftSupport
  folder is needed — and the IPA carries none at its root (its absence is correct).
- The runtime is present as `App.app/Frameworks/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`,
  with install_name `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime` and a valid
  signature (`codesign --verify --strict`).
- The app bundle signature still verifies.

Implemented as a **single IPA leg** (no archive leg): the old archive leg existed only to
inspect the injected `SwiftSupport/` folder, which no longer exists, and the IPA's
`Payload/<app>.app` is the same artifact the archive's `Products/Applications/<app>.app` would
be — so hygiene on the IPA covers both distribution flows.

**[review]** Also assert the packaging shape so it can't drift:
- The packed `SwiftBindings.Runtime` nupkg preserves `native/SwiftBindingsRuntime.xcframework/**`
  (xcframework `Info.plist` + each slice's `.framework/SwiftBindingsRuntime` binary + per-slice
  `Info.plist`), mirroring what PackGate already does for binding xcframeworks
  (`Build.PackGate.cs:211`).
- The embedded device-slice binary is the expected platform/arch (`lipo -info` / `otool`),
  with install_name `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`.

Also update the simulator gate's `InjectRuntimeDylib` (`Build.RuntimeTests.cs:2466`) to copy the
committed xcframework's simulator-slice **framework bundle** into the app's `Frameworks/`, so the
everyday sim run exercises the same shape consumers get. `BuildRuntimeDeviceXcframework`
(`Build.RuntimeTests.cs:3144`) and the per-platform loose-dylib readers consume the xcframework
slices instead of `native/<platform>/libSwiftBindingsRuntime.dylib` (which no longer exists).

## Risks / open questions (for Codex + Grok review)

1. **NativeReference Kind="Framework" load semantics.** The runtime historically used
   `<Content>` (pure dynamic `@rpath` load), and a resolver comment cites dotnet/macios#25008
   (static-link symbol visibility when DllImports redirect to the main binary). Claim: that
   bug is a *static-link* scenario; our framework has an `@rpath` install_name and is loaded
   via `NativeLibrary.TryLoad("@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime")`,
   i.e. pure dynamic — identical to the Kidoz wrapper, which works on device. **Verify** the
   workload embeds+signs the framework and does **not** static-link/redirect symbols.
2. **macOS/Catalyst framework slices.** Versioned-layout correctness and embed/sign. Fallback
   D2 if troublesome.
3. **Sim vs device RID slice selection** by the workload from one xcframework NativeReference
   (proven for bindings; confirm for the runtime).
4. **Mono JIT simulator load path** unchanged (resolver finds the framework on `@rpath`).
5. **Removing the injector**: confirm nothing else (release pipeline, other gates) depends on
   the `--swiftsupport` flag or `EnableSwiftSupportFolder`.

## Validation plan

- `nuke compile`, `nuke test` (unit).
- `nuke binding-tests --compile-only` then `--skip-regen` (sim runtime) — confirm the runtime
  loads from the framework on Mono JIT.
- `nuke binding-tests --device` — NativeAOT device load from the framework (the path that
  matters for the reporter).
- The rewritten `--appstore-hygiene` gate (device-IPA hygiene leg + runtime-nupkg packaging
  and slice-arch assertions).
- Paired Codex + Grok review of the diff; re-review loop on any High.
- Caveat unchanged: final proof is a real App Store submission (reporter's), which we cannot
  run; but TN2435 matches the errors verbatim and the framework form is device-proven in-repo.
