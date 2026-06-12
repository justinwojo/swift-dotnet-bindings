# SwiftSupport folder for App Store submission (issue #42)

Status: **implemented (Strategy B), now covering BOTH distribution flows.** Captured 2026-06-11,
extended 2026-06-12 to the `.xcarchive` → Xcode Organizer flow. The SDK automation, its packaging,
and a dedicated host gate now ship:
- `src/Swift.Runtime/src/build/add-swiftsupport-folder.sh` — the injector, with `--mode ipa|archive`.
  Both modes share one scan/copy core; ipa mode grow-appends into the finished `.ipa`, archive mode
  writes the folder into the `.xcarchive` root.
- `src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets` — two hooks, both opt-out via
  `<EnableSwiftSupportFolder>false</EnableSwiftSupportFolder>`:
  - `_SwiftBindingsAddSwiftSupportFolder` (`AfterTargets="CreateIpa"`) — the direct `BuildIpa=true`
    path (VS "Distribute", `dotnet publish -p:BuildIpa=true`).
  - `_SwiftBindingsAddSwiftSupportFolderToArchive` (`AfterTargets="Archive"`, gated
    `ArchiveOnBuild=true`) — the `.xcarchive` path the issue #42 reporter actually uses (VS
    "Publish" → Xcode Organizer "Distribute App").
- `src/Swift.Runtime/src/Swift.Runtime.csproj` — packs the script into `buildTransitive/`.
- `nuke binding-tests --swiftsupport` (`build/Build.BindingTests.SwiftSupport.cs`) — the host gate.
  Two legs from one consumer app: an **IPA leg** (`BuildIpa=true` publish) and an **archive leg**
  (`ArchiveOnBuild=true` build). Each asserts the injected `SwiftSupport/iphoneos` is Apple-signed,
  complete, and clean, and that the app signature still verifies.

This doc is self-sufficient: it records the diagnosis and the implemented design, so a fresh
session can understand the fix from it without re-deriving anything. Read it top to bottom; the
**"SDK automation plan"** section documents the IPA design that shipped first, and **"The archive
distribution flow"** documents the second hook added for the reporter's actual flow.

---

## The problem

GitHub issue #42 (reporter `carljohansen`). A consumer built a NuGet binding for the **Kidoz**
Swift SDK with our tooling, referenced it from a **.NET 10 MAUI iOS** app, deployed to a physical
iPhone fine — but App Store Connect **rejects the upload**:

```
ITMS-90426: Invalid Swift Support. The SwiftSupport folder is missing.
            Rebuild your app using the current public (GM) version of Xcode and resubmit it.
```

When the reporter manually added a `SwiftSupport` folder (copying dylibs from the Xcode
toolchain), Apple rejected with a *different* error:

```
ITMS-90430: Invalid Swift Support. The file .DS_Store doesn't have a signing ID.
```

That second error is significant: **the manually-populated folder got them PAST 90426** — the
only remaining blocker was a stray `.DS_Store` Finder dropped into the folder. So a populated +
Apple-signed `SwiftSupport/iphoneos` folder is what clears 90426.

---

## Root cause (validated)

Apple requires a top-level `SwiftSupport/` folder inside the `.ipa` (a sibling of `Payload/`)
whenever the app ships Swift. **Xcode** creates it automatically during *Distribute App → App
Store Connect* (it runs a Swift-stdlib copy pass). The **.NET / MAUI** build zips the `.ipa`
directly (just `Payload/`) and **never runs that step**, so the folder is missing → `ITMS-90426`.

This is a long-standing **.NET-for-iOS toolchain gap**, not a defect in our generator or in
Kidoz — it predates us (Xamarin had the identical issue; the community package
`Xamarin.iOS.SwiftRuntimeSupport` existed to paper over it). Our binding is simply what pulls a
Swift framework into the app, which triggers Apple's requirement.

### The non-obvious part: `swift-stdlib-tool` is the WRONG tool here

The "obvious" fix everyone reaches for — `xcrun swift-stdlib-tool --copy ... --unsigned-destination
SwiftSupport/iphoneos` — produces an **empty** folder for this app, and would NOT fix 90426.

Reason: the consuming app uses Swift's **stable ABI**. It does not embed any `libswift*.dylib`;
it links the OS-resident runtime at `/usr/lib/swift/`. `swift-stdlib-tool` only copies libraries
that are embedded / back-deployed (`@rpath/libswift*` loads); for stable-ABI `/usr/lib/swift`
loads it copies nothing. **Verified** (see "How this was validated").

The correct fix is to copy the **Apple-signed back-deployment copies** of the referenced Swift
dylibs **directly from the active Xcode toolchain** into `SwiftSupport/iphoneos`. That is what the
reporter stumbled into manually, and what cleared 90426.

---

## How this was validated (re-runnable)

All of the following was run against the exact NuGet bundle sent to the reporter.

- **Test bundle (the consumer):** `/Users/wojo/Dev/kidoz-issue40-testbundle/` — `KidozFixSample/`
  references `KidozSDK.Swift.iOS 10.1.5` + `SwiftBindings.Runtime 0.12.2` from `local-packages/`.
- **Binding workspace:** `/Users/wojo/Dev/internal-binding-testing/Kidoz/` — has the
  `KidozSDK.xcframework` (the third-party SDK) and `KidozSDKSwiftBindings.xcframework` (our wrapper).

Facts established (commands in parentheses):

1. **Kidoz is a STATIC mixed ObjC+Swift archive.** `KidozSDK.xcframework/ios-arm64/.../KidozSDK`
   is an `ar` archive of `.o` files with `__swift5_*` sections (`otool -D`, `otool -l | grep swift5`).
   Its Swift is force-loaded into our **dynamic** wrapper `KidozSDKSwiftBindings.framework`.
2. **The wrapper links only stable-ABI Swift.** `otool -L` on
   `KidozSDKSwiftBindings.xcframework/ios-arm64/.../KidozSDKSwiftBindings` shows exclusively
   `/usr/lib/swift/libswift*.dylib` (no `@rpath/libswift*`).
3. **A real device build embeds no Swift runtime.** Built with:
   ```bash
   cd /Users/wojo/Dev/kidoz-issue40-testbundle/KidozFixSample
   dotnet build -c Release -f net10.0-ios -p:RuntimeIdentifier=ios-arm64
   ```
   Result: `bin/Release/net10.0-ios/ios-arm64/KidozFixSample.app/Frameworks/` contains only
   `KidozSDKSwiftBindings.framework` + a bare `libSwiftBindingsRuntime.dylib`. **Zero
   `libswift*.dylib`.** The app references **24** `/usr/lib/swift/libswift*.dylib`
   (16 non-weak / back-deployable, 8 weak / OS-only).
4. **`swift-stdlib-tool` is empty for this app:**
   ```bash
   xcrun swift-stdlib-tool --print \
     --scan-executable .../KidozFixSample.app/KidozFixSample \
     --scan-folder    .../KidozFixSample.app/Frameworks \
     --platform iphoneos          # → prints NOTHING
   ```
5. **Toolchain back-deployment dylibs are split across versioned dirs** under
   `$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib/`:
   `swift-5.0/iphoneos` (most libs), `swift-5.5/iphoneos` (`libswift_Concurrency.dylib`),
   `swift-6.2/iphoneos` (a couple). The unversioned `swift/iphoneos` has **none** of them.
   Of the 24 referenced libs: 16 have a toolchain copy; the **8 with no copy are all weak**
   (OS-only — correctly NOT included). All copies are Apple-signed
   (`codesign -dv` → `Authority=Software Signing / Apple Code Signing Certification Authority`).
6. **A clean IPA was produced and structurally verified.** The validated stopgap script (below)
   produced `/tmp/KidozFixSample-swiftsupport.ipa` with: `Payload/` + `SwiftSupport/iphoneos/`
   at the root, **16 Apple-signed** dylibs, **no `.DS_Store` / `__MACOSX`**, app signature
   untouched (`unzip -l`, `codesign -dv` spot checks).

**Not yet proven:** an actual successful App Store Connect / TestFlight submission. We have no
distribution cert for the reporter's app and cannot upload from here. We eliminated both *known*
errors (missing folder → populated; `.DS_Store` → stripped). Final acceptance must be confirmed by
the reporter via TestFlight (asked for in the issue reply).

---

## The validated stopgap script (what the reporter gets)

This is the manual workaround posted to the issue. It is also the reference logic for the SDK
target. Run on the **same Mac + same Xcode** used to build the `.ipa`.

```bash
#!/bin/bash
set -euo pipefail
IPA_IN="$1"
IPA_OUT="${2:-${IPA_IN%.ipa}-swiftsupport.ipa}"
TOOLCHAIN="$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT; cd "$WORK"
unzip -q "$IPA_IN"
APP="$(echo Payload/*.app)"
EXE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Info.plist")"

# Every Mach-O that could pull in Swift: main binary, framework binaries, bare dylibs, extensions
machos=("$APP/$EXE")
while IFS= read -r -d '' f; do machos+=("$f"); done < <(
  find "$APP" \( -path '*/Frameworks/*.framework/*' -o -path '*/Frameworks/*.dylib' \
                 -o -path '*/PlugIns/*.appex/*' \) -type f -perm -u+x -print0)

refs="$(for m in "${machos[@]}"; do otool -L "$m" 2>/dev/null; done \
        | grep -oE '/usr/lib/swift/libswift[^ ]*\.dylib' | sort -u)"

mkdir -p SwiftSupport/iphoneos
while read -r ref; do
  [ -n "$ref" ] || continue
  base="$(basename "$ref")"
  src="$(ls "$TOOLCHAIN"/swift-*/iphoneos/"$base" 2>/dev/null | head -1)"  # Apple-signed copy
  [ -n "$src" ] && ditto "$src" "SwiftSupport/iphoneos/$base"             # ditto preserves signature
done <<< "$refs"

find . -name .DS_Store -delete
rm -f "$IPA_OUT"
zip -qry "$IPA_OUT" Payload SwiftSupport   # -y keeps symlinks intact
echo "Wrote $IPA_OUT ($(ls SwiftSupport/iphoneos | wc -l | tr -d ' ') Apple-signed swift dylibs)"
```

---

## SDK automation plan

Goal: a consumer who builds an App Store IPA through our binding NuGet should get a compliant
`SwiftSupport/iphoneos` folder **with no manual step**. This is the real fix; the script is the
stopgap.

### Where the IPA is built (confirmed in the installed net10 workload)

Pack: `/usr/local/share/dotnet/packs/Microsoft.iOS.Sdk.net10.0_26.2/26.2.10233/`

- `tools/msbuild/Xamarin.Shared.targets:3311` —
  ```
  CreateIpaDependsOn = _BeforeCreateIpaForDistribution; _CompileEntitlements;
                       _CoreCreateIpa; _PackageOnDemandResources; _ZipIpa
  ```
- `tools/msbuild/Xamarin.iOS.Common.targets:476` — `_ZipIpa` zips `@(_IpaPackageSource)` with
  `WorkingDirectory=$(DeviceSpecificIntermediateOutputPath)ipa`. So `_CoreCreateIpa` stages
  `Payload/` into `$(DeviceSpecificIntermediateOutputPath)ipa/`, and `_ZipIpa` zips whatever
  is in `@(_IpaPackageSource)`.
- IPA build is gated by `BuildIpa == true` (set for device iOS/tvOS publish in
  `Xamarin.Shared.Sdk.Publish.targets`). The finished path is `$(IpaPackagePath)`.

### Two strategies

**Strategy A — inject before the zip.** Target `BeforeTargets="_ZipIpa"` (runs after
`_CoreCreateIpa`, so `Payload/` already exists in the staging dir). Copy referenced toolchain
swift dylibs into `$(DeviceSpecificIntermediateOutputPath)ipa/SwiftSupport/iphoneos/`, then append
those files to `@(_IpaPackageSource)` so the existing `Zip` task includes them. Single zip,
cleanest output — but couples to **internal** workload items (`_IpaPackageSource`, `_ZipIpa`) that
Microsoft can rename between versions.

**Strategy B — post-process the finished IPA (RECOMMENDED, IMPLEMENTED).** Target
`AfterTargets="CreateIpa"` (after `_CoreCreateIpa` + `_ZipIpa`, so `$(IpaPackagePath)` is the
finished `.ipa`). The script unzips it to scan the app's Mach-Os, builds `SwiftSupport/<platform>`
from the Apple-signed toolchain back-deployment dylibs, then **grow-appends** that folder
(`zip -g`) onto a scratch copy of the IPA and swaps it over the original with an atomic `mv`.
Grow-append leaves `Payload/` and every other top-level member byte-for-byte untouched (no
recompress, no dropped sibling, app signature intact), and the atomic swap means a failure
mid-append can corrupt only the discarded copy — never the finished artifact. It depends only on
the public-ish `$(IpaPackagePath)` property and the `CreateIpa` target name — **far more robust to
workload churn** than reaching into the internal `_ZipIpa` / `_IpaPackageSource` items.

**Decision: implement B.** Robustness beats coupling to internal targets. (An earlier draft of B
did a full unzip-and-rewrite; the implemented version grow-appends on a copy to avoid
recompressing `Payload/` while preserving atomicity.) Revisit A only if the extra byte-copy ever
matters.

**Why an MSBuild target (either strategy) is the right shape — not a standalone script.** The
issue #42 reporter builds from **Windows via a remote Mac build host** (VS "Pair to Mac" /
`dotnet` remote build). The IPA and all the Swift dylibs the fix needs live on the **Mac**, and the
fix logic is macOS/Xcode-only (`otool`, `ditto`, the toolchain dirs) — a hand-run script is
awkward-to-impossible for that user (they'd have to SSH into the build host, run it, copy the
result back). Because the target runs as part of `CreateIpa` **on the build host itself**, it works
identically whether the build is kicked off from a Mac or driven remotely from Windows. This is a
concrete argument *for* doing the work server-side inside the build, and a point against ever
shipping the script as the "real" answer.

### Where it ships

The runtime/SDK already injects `buildTransitive` targets that flow to the consuming app:
`src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets` (the one that drops
`libSwiftBindingsRuntime.dylib` into `Frameworks/`) is the natural home, because it is already
imported by every consumer and already platform-gates iOS. Alternatively the SDK
(`src/Swift.Bindings.Sdk/Sdk/Sdk.targets`) for SDK-direct consumers. Confirm both consumption
paths (PackageReference and SDK-direct) get the target.

### Gating

- Only when `'$(BuildIpa)' == 'true'` (device iOS/tvOS IPA publish). Never sim, never `dotnet build`
  that doesn't produce an IPA.
- Only when Swift content is actually present (a binding is referenced). Conservative detection:
  the app's `Frameworks/` contains a `*SwiftBindings.framework` or any framework whose binary
  references `/usr/lib/swift/libswift*`. Cheapest reliable gate: presence of our runtime dylib +
  at least one wrapper framework.
- Provide an opt-out property, e.g. `<EnableSwiftSupportFolder>false</EnableSwiftSupportFolder>`.

### Robustness refinements (from the Codex review — fold into the real target, not the stopgap)

1. **Weak vs non-weak:** parse `otool -l` for `LC_LOAD_DYLIB` vs `LC_LOAD_WEAK_DYLIB`. If a
   **non-weak** referenced `/usr/lib/swift/libswift*` has **no** toolchain copy → **fail the build
   hard** (don't silently ship an incomplete folder). Missing **weak** copies are expected (OS-only).
2. **Dependency closure:** a copied swift dylib may itself reference other swift dylibs not directly
   referenced by the app — include those too.
3. **Discover `swift-*/iphoneos` dirs dynamically** under the active toolchain; do not hard-code
   `swift-5.0 / 5.5 / 6.2`.
4. **Scan all Mach-Os:** app executable, `Frameworks/*.framework/<binary>`, bare `Frameworks/*.dylib`,
   `PlugIns/*.appex` executables + their frameworks.
5. **`ditto`** to copy (preserves Apple signature); **never re-sign** the SwiftSupport dylibs with
   the app identity — they must keep Apple's signature.
6. **`zip -y`** (preserve symlinks); strip `.DS_Store`; never emit `__MACOSX`.
7. Use the **active** Xcode (`xcode-select -p` / `$(_SdkDevPath)` inside the build) — guaranteed to
   match the build, which removes the version-mismatch risk the manual script carries.

### Required gate (non-negotiable — this is how we survive workload churn)

Add a **BindingTests device-IPA fixture** that:
- builds a device IPA through a Swift binding,
- asserts `SwiftSupport/iphoneos` exists, is **non-empty**, every entry is an Apple-signed
  `libswift*.dylib`, and there is no `.DS_Store`,
- asserts `Payload/` is intact and the app signature is unbroken.

Because this leans on workload-version-specific behavior (even strategy B touches `$(IpaPackagePath)`
and `CreateIpa`), a workload bump that breaks the hook must fail **our** CI, not a user's submission.
Wire it as a flag on `nuke binding-tests` (e.g. `--swiftsupport`/folded into an App-Store gate),
consistent with the existing `--mixed-pack` / `--mixed-direct` opt-in heavyweight gates. See
`feedback_bindingtests_durable_gate` in memory — new gate shapes ship as flags inside
`nuke binding-tests`.

### Risks / open questions

1. **Undocumented surface.** `_ZipIpa`, `_IpaPackageSource`, `CreateIpa`, `$(IpaPackagePath)` are
   internal-ish. Mitigated by the BindingTests gate. Strategy B minimizes exposure.
2. **Final App Store acceptance unproven.** The SDK target does not change this — it only removes
   the manual step. First real submission (reporter's, or one of our own test apps with a real
   distribution cert) is the true proof. Until then the doc/issue must say "validated structurally".
3. **Bare `libSwiftBindingsRuntime.dylib` in `Frameworks/`** (a standalone dylib, not inside a
   `.framework`) is an independent, latent App Store-hygiene risk flagged during review
   (ITMS-90432/90087 class). NOT the cause of #42. Track separately; consider wrapping the runtime
   as a proper `.framework` in a future cleanup. Do not bundle into this fix.

---

## The archive distribution flow (issue #42 reporter's actual path)

The original fix hooked `CreateIpa` — it fires only when the build produces an `.ipa` directly
(`BuildIpa=true`: VS "Distribute", `dotnet publish -p:BuildIpa=true`). But the reporter, like most
VS/MAUI users following Microsoft's documented App Store flow, does **not** take that path. He uses
**VS "Publish" → produce a `.xcarchive` → open Xcode Organizer → "Distribute App" → App Store
Connect**. In that flow the .NET build emits an `.xcarchive` (via `ArchiveOnBuild=true` →
`Archive`/`_CoreArchive`) and **Xcode** — not .NET — produces the final IPA during export. The
`CreateIpa` hook never runs, so the original fix did nothing for him. This is why he still hit
ITMS-90426 after the first fix shipped.

### The fix: inject into the archive root, exactly where Xcode expects it

A `.xcarchive` is a directory with `Products/Applications/<app>.app` plus sibling metadata folders.
Apple's archive→export tooling honors a top-level **`SwiftSupport/<platform>/`** folder in the
archive root and carries it into the exported App Store IPA. So the second hook,
`_SwiftBindingsAddSwiftSupportFolderToArchive`, runs `AfterTargets="Archive"` (gated
`ArchiveOnBuild=true`) and writes `<archive>/SwiftSupport/iphoneos/` using the **same** scan/copy
core as the IPA path — same weak/non-weak split, same toolchain back-deployment copies, same
Apple-signed `ditto`, same dependency closure. The only difference is the destination and that there
is no zip step (the archive is a plain directory).

### Why this anchor is right (historical precedent, both reviewers concurred)

This is exactly what Microsoft's own **`Xamarin.iOS.SwiftRuntimeSupport`** did for years: its targets
ran `AfterTargets="Archive"`, gated `ArchiveOnBuild=true`, and copied the Swift dylibs to
`$(ArchiveDir)/SwiftSupport`. Its readme explicitly supported "generating the archive of the app
(not the IPA)" and routed users through Xcode Organizer's Distribute App wizard — Apple's export
honored the archive-root folder. The community `Xamarin.Swift 1.2.0` package carried both anchors
(`_CoreCreateIpa` for the IPA staging dir and `_CoreArchive`/`$(ArchiveDir)` for archives) for the
same reason. We adopted the **public** `Archive` target + `$(ArchiveDir)` output property (Microsoft's
shape) rather than the internal `_CoreArchive`, for robustness to workload churn.

The reporter independently confirmed the carry-through: manually adding a populated `SwiftSupport`
folder to his archive cleared ITMS-90426 through this exact Organizer flow (the only residual was a
Finder `.DS_Store`, which our injector never produces).

### Why the gate asserts the archive folder, not an exported IPA

The faithful end-to-end check would be: inject into the archive, run
`xcodebuild -exportArchive -exportOptionsPlist method=app-store-connect`, unzip the resulting IPA,
and assert `SwiftSupport/iphoneos` survived. That export requires an **Apple Distribution**
certificate + an App Store provisioning profile, which a CI/build host generally lacks (ours has only
a development identity, zero distribution profiles). Shipping an `xcodebuild -exportArchive` step we
cannot actually run would be worse than not having it — an untestable gate that silently no-ops.

So the archive leg asserts **our code's responsibility**: a correct, complete, Apple-signed
`SwiftSupport/iphoneos` at the archive root — deterministically, with only the development identity
the host already has. The carry-through (Xcode copying `<archive>/SwiftSupport` into the App Store
IPA) is Apple-toolchain behavior, backed by the Microsoft precedent above and the reporter's manual
confirmation. The final proof remains a real App Store submission, same caveat as the IPA leg.

### Gate mechanics worth knowing

The workload's `Archive` MSBuild task always writes the `.xcarchive` to Xcode's own Archives
directory (`~/Library/Developer/Xcode/Archives/<date>/<name> <timestamp>.xcarchive`) and exposes the
chosen path only as the `$(ArchiveDir)` **output** property — there is no input property to redirect
it (`ArchivePath` does not control it). The gate's consumer csproj therefore captures `$(ArchiveDir)`
to `archive-dir.txt` via a small `AfterTargets="Archive"` target, and the gate reads that file to
locate the archive precisely instead of globbing the date folder. The gate deletes the throwaway
archive (and its now-empty date folder) afterward so it does not litter Xcode Organizer.

---

## References

- Issue: https://github.com/justinwojo/swift-dotnet-bindings/issues/42 (and #40, the prior Kidoz fix)
- Consults (full transcripts on disk under `/private/tmp/`):
  - Grok session `019eb727-ae8c-7bd0-b1d3-4090d89d60a4` — root-cause concurrence + Xamarin precedent.
  - Codex session `019eb733-949c-74e0-8f3b-e5e088fa7b3e` — caught the empty-folder problem,
    confirmed the corrected copy-from-toolchain approach, pinned the workload hook points.
- Local artifacts (regenerate any time with the commands above): the device `.app` under
  `kidoz-issue40-testbundle/KidozFixSample/bin/Release/net10.0-ios/ios-arm64/`, and
  `/tmp/KidozFixSample-swiftsupport.ipa`.
- Apple precedent: ITMS-90426 is the same gap Xamarin papered over with
  `Xamarin.iOS.SwiftRuntimeSupport`; that package itself had empty-folder / signing bugs across
  Xcode versions — a caution that the dylib set + signatures must be exactly right.
