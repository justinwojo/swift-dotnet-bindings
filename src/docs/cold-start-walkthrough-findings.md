# Cold-Start Walkthrough Findings

**Date**: March 2, 2026
**Persona**: Senior mobile developer, 8+ years Xamarin/.NET MAUI, extensive ObjC binding experience with Objective Sharpie. First time seeing this repo.
**Phase**: Release Readiness Roadmap — Phase 1

---

## Executive Summary

The documentation is significantly better than any ObjC binding tool I've used. The README is compelling, the Getting Started guide is clear, and the error code table is a huge improvement over Objective Sharpie's cryptic failures. However, there are real friction points that would trip up a new user — particularly around prerequisites, package availability, version mismatches, and the gap between "read the docs" and "actually run the thing."

I evaluated from two perspectives:
1. **Scenario A**: I have a vendor xcframework and want to use it in a .NET MAUI app
2. **Scenario B**: I have an SPM package (source) and want to create a NuGet binding

---

## Friction Points (Ordered by Severity)

### CRITICAL — Would stop me dead

#### F1: NuGet package availability and template version bug ✅ RESOLVED

**Original concern**: Packages not published, no instructions for building from source, version inconsistencies.

**Resolution**: Packages will be published to nuget.org as 1.0.0 at public launch. The README quickstart (`dotnet new install Swift.Bindings.Templates`) is correct — it will grab the latest version.

**Bug found and fixed**: The template had a version replacement bug — `template.json` searched for `0.1.0-preview.1` but `ProjectName.csproj` contained `0.1.0-preview.5`, so the `--sdkVersion` parameter never took effect. Fixed by aligning both to `0.1.0-preview.5`.

**Remaining pre-release work**: All version references across docs (Getting Started, Customization, CLAUDE.md) need a sweep to `1.0.0` before going public. Current scattered references: `0.1.0-preview.1` (docs), `0.1.0-preview.5` (template), `0.1.0-preview.6` (Sdk.props).

#### F2: .NET 10 requirement ✅ RESOLVED

**Original concern**: .NET 10 requirement not prominent enough for users on .NET 8/9.

**Resolution**: Apple requires iOS 26 SDK compilation as of April 2026, which itself requires .NET 10. Not a practical concern for the target audience. Added a prerequisites line with .NET 10 download link to the README Getting Started section so it's visible without clicking through to the full Getting Started guide.

#### F3: No end-to-end "consuming in MAUI" example — DEFERRED

**Original concern**: No guidance on consuming the NuGet in a MAUI app (`#if IOS`, native lib bundling, etc.).

**Resolution**: Deferred — not blocking for initial release. Separate repo (`swift-dotnet-packages`) will contain working .NET iOS app examples with real binding libraries. A MAUI sample app may be added to this repo in the future. The NuGet packaging handles native framework inclusion automatically, so standard `<PackageReference>` consumption works without extra steps.

---

### HIGH — Would cost me significant time

#### F4: "BUILD_LIBRARY_FOR_DISTRIBUTION=YES" is buried ✅ RESOLVED

**Original concern**: Requirement not prominent enough — buried in the "Using Xcode" section and Troubleshooting, not in Prerequisites.

**Resolution**: Added to Getting Started prerequisites as a clear requirement with explanation of why it's needed. Expanded Troubleshooting with scenario-specific guidance (vendor vs SPM) and a new "Alternative approaches for incompatible xcframeworks" section listing fallback options (Objective Sharpie, Maui.NativeLibraryInterop) for libraries that can't meet this requirement.

#### F5: Scenario B (SPM → xcframework → NuGet) has a gap

The README links to `spm-to-xcframework` as a separate tool. This is the right architectural decision. But:

1. The spm-to-xcframework tool usage shown in the README looks simple, but the Getting Started page shows manual `xcodebuild archive` commands as the "Using Xcode" alternative. There's no guidance on when to use which approach.

2. Neither doc mentions common SPM complications:
   - SPM packages with dependencies (transitive xcframeworks)
   - SPM packages that are static libraries by default (need `type: .dynamic` in Package.swift)
   - SPM packages that don't support `BUILD_LIBRARY_FOR_DISTRIBUTION` (conditional compilation, `#if compiler(>=6.0)`, etc.)

3. The `spm-to-xcframework` tool is referenced but there's no indication of its maturity or whether it handles these edge cases.

**Recommendation**: Add a brief "Common SPM issues" subsection that sets expectations: "Not all SPM packages can be converted to xcframeworks. Libraries that use conditional compilation (`#if`), rely on package plugins, or don't support library evolution may require manual Xcode project setup."

#### F6: No "is my library compatible?" pre-check

Before spending time on binding generation, I'd want to know if my xcframework is even a candidate. There's no quick validation step like:
```bash
# Check if an xcframework is bindable
dotnet run --project src/Swift.Bindings/src -- --xcframework MyLib.xcframework --dry-run
```

The closest thing is running the generator and reading `binding-report.json`, but that's a heavyweight operation. A "pre-flight check" that verifies: has .swiftmodule, has ABI JSON (or can extract it), is dynamic not static — would save users 5-10 minutes of false starts.

**Recommendation**: Consider a `--validate-only` or `--dry-run` CLI flag. Even without implementation, document the quick manual checks:
```bash
# Quick check: is it dynamic?
file MyLib.xcframework/ios-arm64-simulator/MyLib.framework/MyLib
# Should say "dynamically linked shared library", NOT "current ar archive"

# Quick check: has Swift module?
ls MyLib.xcframework/ios-arm64-simulator/MyLib.framework/Modules/*.swiftmodule/
# Should have .swiftinterface files
```

#### F7: What namespace will my types be in? — PLANNED

**Decision**: Change the default namespace pattern from `Swift.{Module}` to `{Module}`. The `Swift.` prefix is redundant — the package ID (`Nuke.Swift.iOS`) already communicates it's a Swift binding. The namespace should just get out of the way: `using Nuke;`.

**Implementation plan** (do as a focused session):

1. **`NamespacePatternResolver.cs:12`** — Change `DefaultPattern` from `"Swift.{Module}"` to `"{Module}"`
2. **`Program.cs:47`** — Update CLI help text for `--namespace-pattern` default description
3. **`Program.cs:163`** — Update help output string
4. **`CrossModuleExtensionEmitter.cs:500`** — Hardcoded `Swift.{module}.{TypeName}` fallback needs to go through the resolver instead
5. **Docs** — Update Getting Started step 5 (`using Swift.MyLibrary;` → `using MyLibrary;`), Customization, any other `Swift.{Module}` references
6. **Tests** — Bulk update assertions expecting `Swift.Nuke`, `Swift.Alamofire`, etc. across unit and integration tests
7. **TestFramework golden files** — Regenerate (`check-golden-files.sh`)
8. **Validation baseline** — Regenerate (`.validation-baseline.json`)

Package ID convention (`{Module}.Swift.iOS`) stays as-is — only the C# namespace changes.

---

### MEDIUM — Would slow me down or cause confusion

#### F8: Version inconsistencies across documentation ✅ RESOLVED

**Original concern**: Version numbers scattered across docs, template, SDK, and generator were all different.

**Resolution**: Two-part fix: (1) Removed all specific version numbers from docs — examples show `Sdk="Swift.Bindings.Sdk"` without a version, Customization table says "matches SDK version" instead of a hardcoded number. (2) Added `template.json` and `BindingProjectEmitter.DefaultSwiftRuntimeVersion` to the release pipeline's version patching step, so all hardcoded versions in code are updated automatically at release time alongside the existing `Sdk.props` and `ProjectName.csproj` patches. All three packages ship in lockstep.

#### F9: "Static xcframework detected" — what do I do?

The Troubleshooting says: "Rebuild the framework as a dynamic library. In Xcode, set `MACH_O_TYPE` to `mh_dylib`."

For Scenario A (vendor xcframework), I can't rebuild it. I need the vendor to do it. The docs don't say this explicitly. For Scenario B (SPM), the default SPM product type is often static. The docs don't mention this or how to force dynamic linking in SPM.

**Recommendation**: Add guidance for both scenarios:
- Vendor: "Contact the library vendor and request a dynamic xcframework build"
- SPM: "In the library's `Package.swift`, the product must be declared as `.library(name: ..., type: .dynamic, targets: ...)`"

#### F10: SwiftUI bridging — impressive but unclear when to use it

The SwiftUI Interop doc is thorough, but from a MAUI developer's perspective, I'm unclear on:
- When would I want this vs. just using native MAUI controls?
- What does "present the UIViewController" actually look like in a MAUI app?
- Does the bridge work in a MAUI `ContentPage` or only in custom renderers/handlers?

The example code `PresentViewController(nativeVC, animated: true, completionHandler: null)` is UIKit API — where does this call go in a MAUI context?

**Recommendation**: Add a concrete MAUI example, even if brief:
```csharp
// In a MAUI handler or platform-specific code
#if IOS
var session = new SwiftViewSession(...);
var vc = ObjCRuntime.Runtime.GetNSObject<UIViewController>(session.ViewController);
// Present modally, or embed in a UIViewControllerRepresentable
#endif
```

#### F11: Binding report skip reasons — good but no priority guidance ✅ ALREADY IMPLEMENTED

**Original concern**: No coverage percentage or priority guidance in console output.

**Resolution**: Already implemented in `ReportEmitter.cs`. The console summary prints coverage percentages for both types and members (e.g., `83.8% coverage`), skip reason breakdowns with descriptions, and a pointer to `binding-report.json` for details.

#### F12: No mention of debugging generated bindings ✅ RESOLVED

**Original concern**: No guidance on where generated files live or how to debug runtime issues.

**Resolution**: Added "Debugging Generated Bindings" section to Troubleshooting covering: file locations for both SDK and CLI modes, how to read the generated P/Invoke declarations, and source-level debugging (works for the C# side, not the Swift wrapper side).

#### F13: Consumer `.targets` file — what does it do? ✅ RESOLVED

**Original concern**: No explanation of what the consumer targets file does when the NuGet is referenced.

**Resolution**: Added a sentence to Getting Started step 5 explaining that the NuGet includes MSBuild targets that automatically bundle native frameworks and configure diagnostic suppression.

#### F14: `libSwiftBindingsRuntime.dylib` — where does it come from? ✅ RESOLVED

**Original concern**: No explanation of where this dylib comes from or how to fix it when missing.

**Resolution**: Expanded the Troubleshooting entry to clarify it's bundled in the `Swift.Runtime` NuGet package and included as a transitive dependency. Added clean rebuild suggestion.

---

### LOW — Minor polish items

#### F15: Wiki sidebar links point to `justinwojo/swift-dotnet-bindings` ✅ NO ACTION NEEDED

**Resolution**: URL is correct — repo will be public at `https://github.com/justinwojo/swift-dotnet-bindings`.

#### F16: "Objective-C binding generation — Under consideration" in README status table ✅ RESOLVED

**Resolution**: Removed the single-row table (looked like a stub). The prose section below already covers it with full context. Added a call-to-action inviting users to open an issue if ObjC support would be valuable.

#### F17: Getting Started link syntax uses wiki-style links ✅ RESOLVED

**Resolution**: Added `.md` extensions to all cross-page links across all docs. `_Sidebar.md` kept wiki-style links since it's wiki-only. Docs are now browsable from the GitHub repo file browser.

#### F18: The README "Examples" section uses Nuke and Lottie — but are those bindings published? ✅ RESOLVED

**Resolution**: Nuke and Lottie packages will be published in [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages). Added a link to the packages repo after the examples so users know where to get them.

#### F19: No mention of Xcode version requirements ✅ RESOLVED

**Resolution**: Added "Xcode 26 or later" to both README and Getting Started prerequisites.

#### F20: `--async-library` CLI option is unexplained ✅ RESOLVED

**Resolution**: Updated CLI help text to clarify it's only needed in manual mode when the wrapper is a separate dylib. Xcframework mode handles it automatically.

#### F21: Architecture page says "40 libraries, 53 targets" but README says "40 libraries (53 framework targets)" — IGNORED

Not worth maintaining — counts are close enough and rarely change.

---

## Scenario Walkthroughs

### Scenario A: Vendor xcframework → .NET MAUI app

**Steps I'd follow:**
1. Read README → good, clear value prop, immediately understand what this does. Prerequisites now visible.
2. Follow "Getting Started" link → clear prerequisites
3. Run `dotnet new install Swift.Bindings.Templates` → works (packages on nuget.org)
4. Create project, drop in xcframework, `dotnet build` → likely works if xcframework is compatible
5. `dotnet pack` → produces .nupkg
6. Add to MAUI app → works via standard `<PackageReference>` (examples in swift-dotnet-packages repo)
7. Call a method → need to discover namespace (`Swift.{Module}` default)
8. Runtime crash on simulator → read Troubleshooting, find Mono JIT info → decent guidance but scary

**Verdict**: Steps 1-5 solid. Step 6 works but benefits from external examples. Steps 7-8 need more guidance (namespace discovery, debugging).

### Scenario B: SPM package → NuGet binding → .NET MAUI app

**Steps I'd follow:**
1. Read README → see "Working with Swift Package Manager Libraries" → clear, points to spm-to-xcframework
2. Clone spm-to-xcframework → **UNKNOWN** (is it published? Does it work?)
3. Run `./spm-to-xcframework https://github.com/foo/bar --version 1.0.0` → produces xcframework (hopefully)
4. Follow Scenario A from step 3 onwards
5. If SPM package has dependencies → **UNCLEAR** (do I need to build all deps as xcframeworks too? Use `--framework-dependency`?)
6. If SPM package is static-only → **BLOCKED** (no guidance on forcing dynamic)

**Verdict**: Depends heavily on spm-to-xcframework tool maturity. The docs correctly scope this as out-of-band, but the handoff between the tools needs more guidance for the dependency case.

---

## What's Actually Good (Credit Where Due)

These things are notably better than Objective Sharpie or any ObjC binding tool I've used:

1. **Error codes with tables** — SWIFTBIND001-100 and SB0001-0004 are dramatically better than Objective Sharpie's opaque errors
2. **Binding report** — `binding-report.json` with skip reasons is a massive improvement over "it silently dropped your method"
3. **The 40-library validation story** — Knowing it's been tested against real libraries builds confidence
4. **How Bindings Map page** — Side-by-side Swift/C# is exactly what a binding developer wants to see
5. **NativeAOT deployment guide** — Proactively addressing the "device vs. simulator" split is smart
6. **SwiftUI bridging** — The fact that this exists at all is remarkable. No other tool even attempts this.
7. **Auto-detection of everything** — xcframework → auto-resolve ABI, TBD, module name. This is much better than Objective Sharpie's "go find your .h files"
8. **Incremental builds** — Fingerprint-based incremental builds show maturity

---

## Priority Recommendations

### Resolved:
1. ~~**F1**: Package availability~~ — ✅ Packages will be on nuget.org at launch. Template version bug fixed.
2. ~~**F2**: .NET 10 requirement~~ — ✅ Prerequisites added to README. Apple iOS 26 SDK mandate makes this moot.
3. ~~**F3**: MAUI consumption example~~ — Deferred. Separate examples repo + future MAUI sample.

### Must-fix before release:
4. **F8**: Version inconsistencies ✅

### Should-fix before release:
5. **F4**: BUILD_LIBRARY_FOR_DISTRIBUTION in prerequisites ✅
6. **F7**: Change default namespace from `Swift.{Module}` to `{Module}` — PLANNED (see F7 section for implementation steps)
7. **F12**: Debugging generated bindings ✅

### Nice-to-have:
8. **F5**: SPM edge case documentation
9. ~~**F6**: Pre-flight validation command~~ — WONTFIX (generator already runs fast and gives clear errors)
10. **F9**: Static vs. dynamic guidance for both scenarios
11. ~~**F11**: Coverage percentage in summary output~~ — Already implemented (ReportEmitter prints `{Coverage:P1}` for types and members)
