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

## A. Code fixes to land

### A1. ObjC-path skip reporting is invisible — bridge it into the binding report

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
   elsewhere, so the synthetic gates don't cover this. (Note: MapLibre isn't yet present under
   `swift-dotnet-packages/libraries/` — this is the first build of it.)

2. **V-1 Facebook — mixed ObjC+Swift pack lane.** Run `--mixed-pack` on the real binding: pack the 5 kits,
   then from a single-`PackageReference` consumer build (sim) + NativeAOT-publish (device). Assert Login + a
   Share flow round-trip with the ObjC classes registering exactly once ("Class X is implemented in both …"
   is the failure to rule out). (`libraries/Facebook/` exists but is untracked scratch state with no
   completed `--mixed-pack` run yet.)

3. **App Store hygiene.** `nuke binding-tests --appstore-hygiene` (library-agnostic, run once). Asserts the
   runtime nupkg embeds as a signed framework and a built `.ipa` is TN2435-compliant. Must be green before
   any publish.

4. **Cut releases** via the normal `release/**` flow — MapLibre lane and Facebook lane.

5. **Document the two known limitations** (below) in the wiki Known Limitations page as part of the release.

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
