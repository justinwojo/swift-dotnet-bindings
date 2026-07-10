# Next release — remaining work

**Single source of truth** for what we've committed to finishing before the next release. Two buckets:
code fixes we found worth doing now (§A), and the ship-mechanics tail for the Facebook kits + MapLibre
(§B). Everything deferred lives in `roadmap.md` — this doc holds only work we intend to land this cycle.

Consolidated 2026-07-06 from the now-deleted `facebook-maplibre-remaining-work.md`,
`mixed-binding-objc-swift-type-bridge.md`, and `regression-audit-followups.md` after an audit of what was
actually still open.

---

## 0. RESOLVED — 0.17.0 regression: RealityFoundation wrapper compile fails

**Status: FIXED 2026-07-08.** Root-caused, fixed at the generator, covered by a BindingTests fixture +
unit tests, and verified: RealityFoundation now compiles its wrapper clean under the final 0.17.0 SDK
(`RealityFoundationSwiftBindings.xcframework` produced, `Build succeeded, 0 Error(s)`, no SWIFTBIND050/051).

**Root cause.** A read-only Swift protocol-extension-default **property** surfaced on a **generic** conforming
type (RealityKit's `FromToByAction<Value>.isReversible` / `.isAdditive`, extension defaults on an
`AnimatableData`-constrained parent) routes through the concrete-specialization (CSM) path. That path rendered
the getter as a method **call** — `__self.isReversible()` — invoking a `Bool` value like a function, so swiftc
rejected the whole specialization wrapper ("cannot call value of non-function type 'Bool'") and the SDK gave up
(SWIFTBIND051). Fix: carry `MethodDecl.IsExtensionPropertyGetter` (from `extMethod.IsProperty`) through the
synthetic-getter pipeline and, in `ConcreteProtocolSpecializationEmitter`, **read** the member (`__self.name`,
no parens) for that flag.

**Secondary fix (dead-symbol parity).** For a generic conformer the member is surfaced exclusively via CSM, so
the generic `@_silgen_name("SBSW_…")` free-function wrapper `ProtocolExtensionEmitter` also emitted had no C#
caller — a dead exported symbol (the parity gate's `symbol-reverse` divergence, which RF shipped). It is now
suppressed, but **only** when the member actually routes to CSM — gated on the pipeline's own
`IsCsmSyncEligibleForGenericParent` predicate, never a blanket `IsGeneric` test, so a nested-generic-parent
conformer (which CSM excludes) keeps the wrapper its open-generic `MethodGenericBridge` P/Invoke still calls.

**Coverage.** `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ExtensionDefaultProtocol.swift` reproduces
the RF shape (`CsmFromToBy<Value: CsmAnimatableValue>` with extension-default Bool getters); four runtime tests
(`ExtensionDefaultProtocolTests.TestJointActionIsReversible*/IsAdditive*`) round-trip the getters on the closed
specialization (sim-green). Unit tests pin the read-not-call body and the non-over-suppression gate
(`ConcreteSpecializationEngineTests`, `ProtocolExtensionEmitterTests`).

**Historical detail (kept for context).** The regression gate never reached its test matrix — it aborted in
pre-flight.

**Symptom.** `RegressionValidate` pre-flight rebuilds the cross-framework Apple supplements from source with
the new SDK (`PackCrossFrameworkDependencies` → `BuildAndPackAppleFramework`, `Build.RegressionValidate.cs:664`).
`Matter` packed OK; **`RealityFoundation` failed** on TFM `net10.0-ios26.2` with:

```
error SWIFTBIND051: Swift wrapper compilation failed for 'RealityFoundationSwiftBindings'.
```

The whole run exited 255; no `artifacts/regression-validate-0.17.0.json` was written, and Step 3
(internal-binding-testing) never ran.

**Detail not yet captured.** The underlying `SWIFTBIND050` swiftc errors are **not** surfaced by the SDK even
at `-v normal` — only the `SWIFTBIND051` give-up prints. The emitted wrapper source is at
`…/apple-frameworks/RealityFoundation/obj/Release/net10.0-ios26.2/swift-binding/RealityFoundation.Wrapper.swift`.
Standalone repro:
`dotnet build apple-frameworks/RealityFoundation/SwiftBindings.Apple.RealityFoundation.csproj -c Release -f net10.0-ios26.2`.
Root-causing needs a way to see the swiftc stderr the wrapper-compile step swallows.

**Strong evidence this is a 0.17.0 regression, not pre-existing.** A `SwiftBindings.Apple.RealityFoundation.26.2.8`
nupkg built successfully under the 0.16.0-era generator (local nupkg dated 2026-06-27, the 0.16.0 /
apple-26.2.8 release window). The same supplement now fails to compile its wrapper under 0.17.0. Suspect
commits: the 42 since `sdk-v0.16.0` that touched Apple type-mapping, generic-parent handling, and
existentials. Not yet bisected.

**Scoping nuance for the ship decision.** This failure is in a cross-framework Apple *supplement* that the
harness always rebuilds in pre-flight. The 0.17.0 plan reuses the *published* Apple 26.2.8, so the shipped
Apple packages would remain the working 0.16.0-built ones — narrowly, this supplement isn't being
republished. Broadly, it proves the 0.17.0 generator can't build a real framework that 0.16.0 built, and the
harness can't complete to clear the rest of the matrix regardless. Either way the gate did not pass.

**Resolution.** Option (a) — root-caused and fixed at the generator (see above); no owner trade-off needed. The
final 0.17.0 SDK-lane nupkgs were repacked and RealityFoundation rebuilt clean against them. Re-run
`/regression-validation --version 0.17.0 --apple-version 26.2.8` to clear the rest of the matrix — the RF
pre-flight rebuild no longer aborts.

**Diagnostic-surfacing follow-up (separable, not blocking).** Root-causing this needed the emitted wrapper
source / the `<binary>.swiftc-stderr.txt` dump because the SDK's two-pass `_CompileSwiftWrapper` catches the
generator's non-zero exit and prints only the SWIFTBIND051 give-up — the generator's SWIFTBIND050 (which
already carries a filtered `error:`-line preview, `SwiftWrapperCompiler.cs:~1915`) is not echoed to normal
build verbosity. A durability win for the *next* wrapper regression would be to surface that preview at
`-v normal`. It's an SDK wrapper-compile-path change (delicate — see `.claude/rules/constraints.md`), so it's
scoped as its own task, not folded into the CSM fix.

---

## A. Code fixes — RESOLVED

All three landed on `main` and are covered by assertions at the right layer (see the per-item
**Status** banners below). The original problem statements are kept for context.

### A1. ObjC-path skip reporting is invisible — bridge it into the binding report

**Status: RESOLVED (`3de82f4a`).** `ObjCBindingDiagnostics` now projects into the *persisted* report:
`BindingsGeneratorCommand` attaches `ObjCSection.From(diagnostics)` to the manifest on both the mixed
(`BindingArtifactManifestStore.ReadModifyWrite`) and pure-ObjC (`PersistPureObjCSkipReport` → `Write`)
paths, and `BindingReportProjection.Project` folds those drops into the rederived `binding-report.json`'s
`SkippedItems` and the single `SkipTriage`/`ReviewCount` gate. The assertions cover the manifest→report fold
(each builds an `ObjCSection`-carrying manifest and runs it through projection; the command-side attach+persist
wiring above is the production path, verified by reading `BindingsGeneratorCommand.cs`, not a command
integration test):
`BindingArtifactManifestTests.ObjCSection_And_Projection_FoldMixedObjCSkipsIntoSkipTriage` (mixed — the
`FBSDKBasicUtility.jsonObjectWithData` / `FBSDKLog` / `OMIDAdSession` drop shapes appear with mapped
reasons and roll into the triage) and `.ObjCSection_Only_PureObjCManifest_ProjectsSkipTriage` (pure-ObjC).
`ObjCSkipProjectionTests` pins the `ObjCSkipReason` → `SkipReason` mapping; `SkipDispositionClassifierTests`
pins that those ObjC reasons classify as `KnownLimitation`/`ExpectedStructural`, never `Review`.

*Original problem statement and plan (historical — the Status above is the outcome):*

A mixed (ObjC+Swift) binding has **two** independent binding surfaces, and only the Swift one is reported.
The Swift path writes `binding-report.json` with the `SkipTriage` roll-up (`src/Swift.Bindings/src/Reporting/`).
The ObjC path (`ClangAstParser → ObjCPipeline → ApiDefinitionEmitter`) has its **own** diagnostics
(`ObjCBindingDiagnostics` / `ObjCSkipReason`, `src/Swift.Bindings/src/ObjC/Model/ObjCBindingDiagnostics.cs`)
whose only sink is `LogSummary(ILogger)` at INFO level (`ObjCPipeline.cs`) — it is **never serialized and
never feeds `SkipTriage`** (confirmed: zero serialization references).

Consequence: on a fresh regen of the FB kits at HEAD, the ObjC path silently dropped **47 symbols in
FBSDKCoreKit and 12 in FBSDKCoreKit_Basics** — none visible in any persisted artifact. The "ReviewCount:
0–1, triage clean" release signal describes the Swift path only. For ObjC-heavy libraries that is precisely
where we're blind.

**Fix (reporting, not binding):** feed `ObjCBindingDiagnostics` into the persisted report so the ObjC drop
set is visible and the existing review gate covers it. Preferred shape: merge into the same
`binding-report.json` / `SkipTriage` so there's one artifact and one `ReviewCount` gate; a sidecar
`objc-skip.json` is the fallback if merging the report schema proves invasive. Decide the integration point
by reading the report-emit plumbing (`Reporting/ReportEmitter.cs`).

**Do this first** — until it lands, the drop sets the next two items and the V-1 runs would surface are
under-reported.

### A2. Missing standard Apple type mappings → silent public-member drops

**Status: RESOLVED (`3de82f4a`).** `NSOperatingSystemVersion`, `NSDataReadingOptions`,
`NSUrlSessionTaskState`, `UIApplicationState`, and the `NSJsonReadingOptions` / `NSJsonWritingOptions` pair
are registered in `objc-type-mappings.json` (`objcValueTypes`, plus `systemStructs` for the struct).
Assertions: `ObjCTypeMapperTests.IsApiDefinitionTypeResolvable_StandardAppleValueTypes_ResolveViaRegistry`
proves each of the six *resolves* through the registry (the passed SDK-name set deliberately excludes them,
so a green result isolates the registry path; negative control `NSUnregisteredOptions` stays unresolvable),
`.IsObjCValueType_StandardAppleValueTypes_Recognized` proves each is *recognized as an ObjC value type* by
`AppleFrameworkRegistry.IsObjCValueType`, and the BindingTests fixture
`ObjCUmbrellaFixtureTests.TestStandardAppleTypesResolveAndRoundTrip` binds and round-trips a member using
each one against Microsoft.iOS.

*Original problem statement and plan (historical — the Status above is the outcome):*

Common Apple Foundation/UIKit types are absent from the ObjC type registry, so any member referencing them
is dropped as `ObjCSkipReason.UnresolvableType`. Confirmed absent (0 hits in generator src):
`NSDataReadingOptions`, `NSUrlSessionTaskState`, `UIApplicationState`, `NSOperatingSystemVersion`. Also
verify/complete the `NSJsonReadingOptions` / `NSJsonWritingOptions` pair (3 hits each — confirm they're
actually registered, not just referenced).

On the FB kits these land on utility methods (`FBSDKBasicUtility`/`FBSDKTypeUtility`/`FBSDKURLSession`:
`dataWithJSONObject:options:error:`, `JSONObjectWithData:options:error:`, the URL-session `state` property),
but the root gap drops **genuinely public API on any ObjC library touching JSON / NSData / URLSession** — a
common surface, so worth closing generally, not just for FB.

**Fix:** add the mappings to `src/Swift.Bindings/src/Data/objc-type-mappings.json` (and
`apple-frameworks.json` as needed), with a BindingTests fixture exercising a member that uses each so the
registry gap can't silently reopen. Drop site to reference while fixing: `ApiDefinitionEmitter.cs` +
`ObjCTypeMapper.cs` unresolvable-type warning path.

### A3. `--resolve-auto-deps` diagnostics go to stdout, not stderr (was R4-D5, + D6 test)

**Status: RESOLVED (`3de82f4a`, stderr-threshold assertion added here).**
`Program.CreateLoggerFactory` routes the console logger's Error/Critical threshold to stderr
(`LogToStandardErrorThreshold = LogLevel.Error`). Assertions: the D6 stdout-grammar test
`AutoDepResolverCliTests.ResolveAutoDeps_ResolvableAndUnresolvableSpecs_StdoutIsOnlyFrozenGrammar` runs the
verb with resolvable + unresolvable specs at default verbosity and asserts every stdout line
`StartsWith("PROJREF|")` or `"WARN|"` (both shapes present, so non-vacuous); and
`CreateLoggerFactoryTests.CreateLoggerFactory_RoutesErrorToStdErr_AndInformationToStdOut` pins the
threshold itself — an Error lands on stderr and an Information on stdout — which the grammar test cannot
observe, since its stdout lines come straight from `Console.Out` and never pass through the logger.

*Original problem statement and plan (historical — the Status above is the outcome):*

`BindingsGeneratorCommand.cs`'s `--resolve-auto-deps` failure path `LogError`s through the default
`AddConsole()` logger, which has no `LogToStandardErrorThreshold`, so errors land on stdout at low
importance. The SDK Exec captures stdout into `_SwiftAutoDepResult` (`ConsoleToMSBuild=true`), and stderr is
left at MSBuild's default High importance. Net: any exception in auto-dep resolution surfaces as an opaque
"command exited with code 1" with the actual diagnostic hidden unless the user reruns at `-v:detailed`.

**Fix:** route the console logger's error threshold to stderr (`LogToStandardErrorThreshold` in
`Program.cs`'s `CreateLoggerFactory`) — one line, zero downside, and it also protects the stdout grammar for
any future log line added to that path. **Pair with D6:** add the regression test that runs the verb with
resolvable + unresolvable specs at default verbosity and asserts every stdout line `StartsWith("PROJREF|")`
or `"WARN|"` — pinning the "stdout = frozen grammar only" contract that is currently only implicit.

---

## B. Ship mechanics (Facebook kits + MapLibre)

**Generator work is COMPLETE for both libraries** — nothing left to build. All FB/MapLibre generator items
(W-1, FB-1/1b/2/3, ML-1, `@objc` reverse-vtable hardening) landed and are runtime-green on sim + device
(final items in `0c76ddb4`; earlier in `da0cb117` / `16ca7c6a` / `8d06bd0d` / `3e5a0a5e`). Owner-locked
2026-07-05: **ship all 5 FB kits** (`SwiftBindings.Facebook.{CoreBasics, AEM, Core, Login, Share}`) and
**MapLibre is GO** (ships on its own V-1, doesn't wait on FB). What remains is packaging + release, not
generator work — until a real consumer links the real package, "shippable" is a claim, not a fact.

1. **V-1 MapLibre — pure-ObjC pack lane.** Build the real nupkg
   (`dotnet nuke BuildLibrary --library MapLibre --all-products`), then from a **fresh
   single-`PackageReference` consumer app**, build + run on the iOS Simulator and on device (NativeAOT).
   Assert the map renders and a delegate callback fires ObjC→C#. No pure-ObjC nupkg-consumption leg exists
   elsewhere, so the synthetic gates don't cover this.

   **Status (2026-07-10) — sim pack lane PROVEN (device remains, attended).** The real
   `SwiftBindings.MapLibre.6.27.0.nupkg` builds (`nuke BuildLibrary --library MapLibre --all-products`) and packs
   (`nuke Pack --library MapLibre --version 6.27.0`), self-carrying the 2-slice `MapLibre.xcframework`
   (device `ios-arm64` + `ios-arm64_x86_64-simulator`) under `runtimes/ios-arm64/native/`, no declared
   deps. A clean single-`PackageReference` consumer (`iossimulator-arm64`) auto-embeds `MapLibre.framework`
   into `Frameworks/`, renders the map (`didFinishLoadingStyle` + `didFinishLoadingMap` + `fullyRendered:true`
   all fired) and fires ObjC→C# delegates (`regionDidChangeAnimated`) — 11/0/0 API-pattern matrix on the
   iOS Simulator, zero native-load errors. See the results table below. (`libraries/MapLibre/` is now
   present as untracked scratch in `swift-dotnet-packages` — the earlier "not yet present" note is
   superseded.)

2. **V-1 Facebook — mixed ObjC+Swift pack lane.** Run `--mixed-pack` on the real binding: pack the 5 kits,
   then from a single-`PackageReference` consumer build (sim) + NativeAOT-publish (device). Assert Login + a
   Share flow round-trip with the ObjC classes registering exactly once ("Class X is implemented in both …"
   is the failure to rule out).

   **Status (2026-07-10) — harness `--mixed-pack` sim leg GREEN; real 5-kit pack + device remain.**
   `nuke binding-tests --mixed-pack` (synthetic mixed ObjC+Swift fixture, the issue-#40 shape) passed on
   the iOS Simulator (Mono JIT): one nupkg, one `PackageReference`, ObjC type usable and the class
   registered exactly once — no "implemented in both". This proves the duplicate-registration *mechanism*;
   packing + consuming the **real 5 FB kits**, and the device (NativeAOT) leg, are still open (attended).
   (`libraries/Facebook/` remains untracked scratch state.)

3. **App Store hygiene.** `nuke binding-tests --appstore-hygiene` (library-agnostic, run once). Asserts the
   runtime nupkg embeds as a signed framework and a built `.ipa` is TN2435-compliant. Must be green before
   any publish.

   **Status (2026-07-10) — PASSED on host (structural + signed IPA).** Structural nupkg checks green
   (framework xcframework preserved: device arm64, sim arm64+x86_64; no loose dylib; no injector script)
   AND the signed-IPA leg *ran* (codesigning identity present) and passed — runtime embeds as a signed
   `Frameworks/SwiftBindingsRuntime.framework` (@rpath install_name), no loose dylib, zero `libswift*.dylib`,
   no `SwiftSupport/`, app + framework signatures verify.

4. **Cut releases** via the normal `release/**` flow — MapLibre lane and Facebook lane.

5. **Document the two known limitations** (below) in the wiki Known Limitations page as part of the release.

### §B unattended-leg results — 2026-07-10 (macOS host + iOS Simulator; no device)

Executed the decision-independent, no-device, no-attendance legs of §B. All three green. The device
(NativeAOT) legs — MapLibre's single-`PackageReference` consumer and the real 5-kit Facebook mixed
pack lane — remain as an owner-attended follow-up (unchanged scope). The App Store hygiene IPA leg is
host-only and already ran here (item 3 above), so nothing device-side remains for it.

| Leg | Command | Wall | Result | Log |
|---|---|---|---|---|
| MapLibre V-1 (pure-ObjC pack lane, sim) | `nuke BuildLibrary --library MapLibre --all-products` + `nuke Pack --library MapLibre --version 6.27.0` (in `swift-dotnet-packages`) → single-`PackageReference` consumer `dotnet build -c Debug` (`iossimulator-arm64`) → `simctl install/launch` on booted iPhone 17 Pro Max | build 12s + pack 11s + consumer 44s + run ~16s | **PASS** — native self-embedded, map rendered, ObjC→C# delegates fired, **11 pass / 0 fail / 0 skip**, 0 native-load errors | `/tmp/sb-maplibre-build.log`, `/tmp/sb-maplibre-build2.log`, `/tmp/sb-maplibre-run.log` |
| Facebook mixed V-1 (synthetic fixture) | `nuke binding-tests --mixed-pack` (default sim) | 109s | **PASS** — structural OK (`_OBJC_CLASS_$_SbMixedPackProbe`, companion in `lib/`); consumer ran on sim: `OBJC_GREETING:objc-mixed-ok` + `TEST SUCCESS`, class registered once, no "implemented in both" | `/tmp/sb-leg2-mixedpack.log` |
| App Store hygiene | `nuke binding-tests --appstore-hygiene` | 31s | **PASS** — structural nupkg OK **and** signed-IPA leg ran (identity present): signed `SwiftBindingsRuntime.framework`, no loose dylib, zero `libswift*`, signatures verify | `/tmp/sb-leg3-hygiene.log` |

Notes: the MapLibre nupkg embeds the full 2-slice xcframework under `runtimes/ios-arm64/native/` and declares
no dependencies (pure-ObjC → Microsoft.iOS ObjC runtime, no `SwiftBindings.Runtime`); a single
`PackageReference` is self-sufficient. The Facebook leg is the harness synthetic-mixed fixture (the issue-#40
duplicate-registration shape), which proves the *mechanism* — packing/consuming the real 5 kits stays open.
The hygiene IPA leg is host-only and ran because the signing identity was present; on an identity-less host it
records an honest SKIP (structural still fail-closed). MT7154 `Swift/*Database.xml` BundleResource-dedup
warnings in the hygiene publish are the known-benign LogicalName collisions, not chased.

### Known limitations to document (deliberate scope, not bugs)

- **FB App Links `[any P]` collections (`@objc` element case).** `AppLink.targets` /
  `AppLink.init(sourceURL:targets:webURL:)` / `AppLink.appLink(…)` / `AppLinkFactory.createAppLink` /
  `AppLinkNavigation.navigationType` don't bind. `AppLinkTargetProtocol` is an **`@objc` protocol**, so
  `[any AppLinkTargetProtocol]` is the heavyweight `@objc`-container-existential case — out of scope (the
  forward feature was dropped deliberately; a turnkey "Design B" revival spec is retrievable via
  `git show 8d06bd0d:src/docs/ship-sessions/02-fb2-deferral.md` if demand appears). Facebook App Links
  deep-linking is therefore unsupported; **Login and Share are unaffected.**
- **FB `[String:Any]` dictionary bridging.** `Share*Content.addParameters(_:options:)` and
  `AppLinkNavigation` extras/appLinkData are by-design `AnyType`-in-container drops.

---

## Facebook surface — one-line evidence base

Measured across the 4 consumer kits: **916/2019 members emitted; 71% of skips never-public, 21% by-design,
8% (99) actionable — ~45 of those internal DI/SPI.** The consumer-facing, cleanly-fixable remainder
(FB-1/1b/2/3) is all resolved. The primary consumer types (Settings, Profile, ApplicationDelegate,
LoginManager, LoginConfiguration, AccessToken, ShareLinkContent, SharePhotoContent, ShareVideoContent,
ShareMediaContent, ShareDialog) are present and runtime-proven. Facebook's ship decision was a product call
about polish + demand, already made (ship all 5).
