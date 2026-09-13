# SDK, packaging and test infrastructure — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="q1-compile-time-marking-of-wrapper-dependent-members"></a>

## Q1 — compile-time marking of wrapper-dependent members

Deep-audit §3.6 owner decision, **not authorized**. The fail-closed wrapper default stays; the sound enhancement is compile-time marking of wrapper-dependent members so a missing wrapper fails at build time instead of runtime — *without* flipping the default. Decision context: 2026-07 deep-audit verification §3.6 (deleted from repo; git history at `9b0e8ec9`). **Trigger:** owner authorizes it, or post-0.18 usage feedback shows runtime wrapper failures are a recurring support burden.

<a id="version-coexistence-end-state-apicompat-baseline-minor-window-scheme"></a>

## Runtime minor-window coexistence

**Current policy:** SDK-driven projects and generated bindings keep the bounded `[X.Y.Z, X.(Y+1).0)` Runtime range. Bindings built against different Runtime minors can conflict at restore (`NU1107`). A looser coexistence model remains an owner decision, with consumer migration cost and actual ABI compatibility to establish first.

The Runtime pack's ApiCompat baseline check **already shipped** in `82ef2b438`; it is no longer coupled to this deferred range decision. See [version compatibility](../../Design/version-compatibility.md) for restore, load and pack behavior and the separate Apple coverage boundary. Do not implement the baseline gate again or infer permission to widen the range.

**Revisit when:** concrete consumer pain from the minor boundary warrants reconsideration, or the owner selects a future 1.x compatibility promise. A proposed breaking patch must be addressed under the existing patch-additivity policy, not treated as authorization to relax it.

<a id="should-the-third-party-regression-matrix-become-a-mechanical-release-gate-precondition"></a>

## Should the third-party regression matrix become a mechanical release-gate precondition?

Raised by the 0.18.0 regression post-mortem: the matrix (or just its surface-diff subset, which needs no device and no signing) would have blocked 0.17.0, which published with a clobbered artifact cell. Today it is a human-run pre-release flow, not a step in `.github/workflows/release.yml`. Wiring it in is cheap mechanically but changes the release contract — it makes publishing depend on third-party inputs (network acquisition, upstream package drift, a corpus checkout outside this repo), which is exactly why it was kept out. **Decision, not authorized:** either wire the surface-diff subset into `release.yml` and accept the third-party coupling, or keep it human-run and accept that a surface regression can reach NuGet again. **Trigger:** owner decides, or a second release ships a regression the matrix already had the evidence to catch.

<a id="where-does-a-generation-only-surface-ratchet-live-if-anywhere"></a>

## Where does a generation-only surface ratchet live, if anywhere?

Companion to [the third-party release-gate decision](tooling.md#should-the-third-party-regression-matrix-become-a-mechanical-release-gate-precondition) and the narrower half of it. A ratchet that only *generates* (no compile, no runtime, no device) could measure emitted public surface per corpus library and fail on shrink — the missing "surface meter" from [surface-loss policy](../../Design/engineering-policies.md#surface-loss-is-a-distinct-failure-mode-hard-policy-boundary). Same policy tension in weaker form: it still consumes third-party inputs, so putting it upstream of the pre-release flow imports acquisition flakiness into an ordinary gate. The in-repo alternative has no third-party coupling but only covers BindingTests shapes, and half of it already exists: the API-manifest gate fails on a removed member today, so what remains open is `--skip-surface`, which still logs a vanished skip marker as an improvement rather than treating an unexplained disappearance as a removal. **Decision, not authorized. Trigger:** owner decides, or the first-party alternative is attempted and proves too narrow.

<a id="whether-a-hollow-module-should-fail-the-pack"></a>

## Whether a hollow module should fail the pack

A pack-time member-count rider (`cbd5fc22`) makes a module that binds almost nothing visible in the pack report. Whether it should also *fail* the pack is a product policy call rather than a correctness one — a constants-only or umbrella-only ObjC module is legitimately near-empty (see [classless ObjC umbrellas](../../Design/decisions.md#an-objc-umbrella-can-legitimately-declare-no-classes)). Recommendation: keep the rider and warn rather than fail; absent an answer, current behaviour stands. Evidence: docs-pass breakdown item B17. **Trigger:** the owner sets the policy, or a module with zero bound members ships to nuget.org unnoticed.

<a id="shipped-swiftbindings-mappedin-6-4-2-declares-403-p-invokes-against-a-mappedin-library-the-package-does-not-sh"></a>

## Shipped `SwiftBindings.Mappedin` 6.4.2 declares 403 P/Invokes against a `Mappedin` library the package does not ship

**Revisit when:** the next Mappedin regen/repack retires this permanently — and it must beat the CoreCLR-on-iOS migration (stated intent for the.NET 11 timeframe). Lower-priority residual: every green leg consumes a `ProjectReference` build, never the literal published nupkg; a one-off literal-nupkg smoke would close that sliver.

<details>
<summary>Recorded evidence and disposition</summary>

The generator-side root cause is **fixed** (`f4e785d5`, regen-verified — a fresh Mappedin generation emits zero reachable vendor-named imports; the only surviving `LibraryImport("Mappedin")` are the deliberate `PInvoke_getMetadata_fallback` recovery arms, which correctly keep the declared name), so what is left is not a defect but a **call about an already-published artifact**. Verified facts: the shipped managed assembly's 403 P/Invokes carry ModuleRef `Mappedin` (354 metadata accessors, 29 methods, 14 statics, 3 getters, 2 thunks); the nupkg ships only `MappedinSwiftBindings.xcframework` with no `Mappedin` payload and no transitive supplier; the vendor slice is a **static archive**, so all 403 symbols are merged into and exported from the wrapper binary and no loadable `Mappedin` image can exist; `SwiftFrameworkResolver.ResolveSwiftFramework` finds nothing for `"Mappedin"` and defers to default resolution. Measured runtime matrix: **Mono JIT resolves it** (loader fallback probes already-loaded images) and **NativeAOT resolves it** (build-time direct linking) — 12 consecutive green `ios-device` regression runs, 0.9.0 through 0.18.0, over the suite that touches this exact group; **CoreCLR throws `DllNotFoundException`**, proven by a minimal in-process host experiment. Mappedin is iOS-only and iOS is Mono/NativeAOT today, so no consumer-reachable runtime fails. Neither resolving mechanism was traced: this works by runtime behaviour, not by design. The rest of the library-name axis is clean — a `System.Reflection.Metadata` ImplMap/ModuleRef sweep over the published assemblies came back 16/16. **Decision:** repack proactively, or leave 6.4.2 as shipped and let the next ordinary Mappedin release retire it. Recommendation: leave it, but treat the repack as a hard precondition of CoreCLR-on-iOS rather than a nice-to-have — the latent becomes a real consumer break the moment the runtime changes. Absent an answer, leaving it stands. **Trigger:** the next Mappedin regen/repack retires this permanently — and it must beat the CoreCLR-on-iOS migration (stated intent for the.NET 11 timeframe). Lower-priority residual: every green leg consumes a `ProjectReference` build, never the literal published nupkg; a one-off literal-nupkg smoke would close that sliver.

</details>

<a id="moduleemissioncontext-payloadsemantics-exposes-its-live-backing-list-flaky-collection-was-modified-in-parallel"></a>

## `ModuleEmissionContext.PayloadSemantics` exposes its live backing list → flaky `Collection was modified` in parallel unit tests

**Revisit when:** the next `nuke test` red of this shape, or the next session that touches `ModuleEmissionContext` collection exposure.

<details>
<summary>Recorded evidence and disposition</summary>

Observed once (2 failures in one `nuke test` run, green on an immediate identical re-run, same run showing 14,554 passed). Both failures threw `System.InvalidOperationException: Collection was modified; enumeration operation may not execute` from `List<T>.Enumerator.MoveNext()` at the **same** `ModuleHandler.EmitFrameworkResolver` `foreach (var (typeofExpr, semantics) in payloadSemantics)` line, in the **same second**, from **two different test classes** (`ModuleHandlerTests.Emit_FrameworkResolverUsesGlobalQualification` and `ThirdPartyValidationFixTests.ModuleHandler_EmitsThreadingTasksUsing`). Neither class carries a `[Collection(...)]` attribute, so xUnit runs them in parallel; `ModuleEmissionContext.PayloadSemantics` is `=> _payloadSemantics`, handing out the live `List<>` rather than a snapshot, so a concurrent emission's `RecordPayloadSemantics` can mutate it mid-enumeration. Both the test and the emitter are pre-existing and untouched by the qualification work. **Symptom:** intermittent `nuke test` reds — always `Collection was modified` inside `EmitFrameworkResolver`, always ≥2 emitter test classes at once, never reproducing on re-run. **Fix shape (either):** (a) snapshot at the read — `EmitFrameworkResolver` enumerates `payloadSemantics.ToArray()` (and the sibling `factoryTypes` / `conformances` / `simpleEnumRegistrations` / `classBoundExistentialRegistrations` reads have the same shape and deserve the same treatment); or (b) have the collection properties return a defensive copy. Prefer (a) — it's local to the one consumer and doesn't cost a copy on every read. Note the sibling loops share the hazard, so fix the whole read set in one pass rather than only the line that happened to throw. **Trigger to revisit:** the next `nuke test` red of this shape, or the next session that touches `ModuleEmissionContext` collection exposure.

</details>

<a id="donateorthrowasync-void-throwing-error-path-mono-sim-skip-open-upstream-attribution"></a>

## `DonateOrThrowAsync` void-throwing-error-path Mono-sim skip — OPEN upstream attribution

`PatParentAsyncVoidMethodsTests.TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath` carries a runtime-detected `[SkipOnMonoJit]` (Mono simulator only; macOS CoreCLR + device NativeAOT keep running it). It faults in the Mono arm64 exception unwinder with **zero** Swift/binding/P-Invoke frames on the stack — a ~1/14 heisenbug. Distinct from the sibling cancel-cell crash (root-caused to OURS and fixed: an async parent specialization launched the Swift producer on an already-cancelled token; guard-before-allocation added): this cell passes a **non-cancelled** token, so the Swift producer legitimately launches and its error callback fires on a foreign Swift-concurrency thread — the pre-cancel guard cannot apply. Attribution (upstream unwinder vs ours) is **unresolved**; the cancel-cell A/B result transfers suspicion but proves nothing here, and the probe campaign (A/B substitution + foreign-transition mimic, ~30+ runs/arm at 1/14) was not run as it was not load-bearing for the shipped fix. Coverage loss is explicit + bounded (2 Mono-sim-only skips in the void family; device + macOS run everything). **Trigger:** an unwinder-family crash relocates to an unskipped cell, OR the owner preps the upstream dotnet/runtime filing (the probe campaign then produces the repro seed).

<a id="skiponmonojit-skips-five-members-on-the-device-mono-full-aot-lane-which-has-no-jit"></a>

## `[SkipOnMonoJit]` skips five members on the device Mono full-AOT lane, which has no JIT

**Revisit when:** someone needs one of those five members specifically on device Mono full-AOT, or the next edit to the runtime taxonomy / skip attributes — do it in that pass rather than as its own.

<details>
<summary>Recorded evidence and disposition</summary>

`[SkipOnMonoJit]` is runtime-detected off "is this process Mono?", not off "is this process JIT-compiling", so every test carrying it also skips on the `--mono-aot` device lane — a full-AOT runtime where the JIT-specific faults the attribute was written for cannot occur by construction. Five tests are affected: `OptionalMarshallingTests.TestOptionalGenericHolderLargeStructPeek`, `OptionalThrowingVoidClosureTests.TestHolder_SetValidator_DelegateThrows_GracefulFault`, `PatParentAsyncMethodsTests.TestAsyncBagMockIntItem_CancelRespondAsyncSurfacesCancellation`, `PatParentAsyncMethodsTests.TestAsyncBagMockStringItem_CancelRespondAsyncSurfacesCancellation`, and `PatParentAsyncVoidMethodsTests.TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath`. Latent rather than a coverage hole: all five run on the NativeAOT device lane and on macOS CoreCLR, so no member goes unexercised on a phone, and the skip is conservative in the safe direction (a test that would have passed is not run; a test that would have crashed does not take the lane down). Closing it is not an attribute edit — it means adding a fourth runtime-detected predicate that separates Mono full-AOT from Mono JIT, then un-skipping each of the five one at a time and doing a device run per arm to learn whether the fault actually reproduces without a JIT. That is a device-run campaign whose only payoff is five cells that are already covered on two other runtimes. **Trigger:** someone needs one of those five members specifically on device Mono full-AOT, or the next edit to the runtime taxonomy / skip attributes — do it in that pass rather than as its own.

</details>

<a id="d1-corpus-shape-2-swift-class-with-objc-superclass-from-a-sibling-binding-has-no-first-party-fixture"></a>

## D1 corpus Shape 2 (Swift class with ObjC superclass from a sibling binding) has no first-party fixture

The 2026-07 corpus-red root-cause work's Shape 2 — a Swift class inheriting an ObjC superclass that lives in a *sibling* bound module — was fixed and is corpus-proven, but no BindingTests fixture pins it. Reproducing it first-party requires `SwiftBindingsTestLib` (or a sibling test module) to be a **mixed ObjC+Swift module**, which `build/Build.BindingTests.ObjCUmbrella.cs` deliberately prevents: the ObjC umbrella fixture is built as its own separate xcframework precisely so the main test lib stays pure Swift and the mixed-classification path stays isolated. Restructuring the test library into a mixed module just for this shape would trade a fixture gap for a permanent complication of every other gate. Coverage today: the corpus lib that surfaced it (regression-tested in the downstream pre-release flow) plus the unit-level classification tests. **Trigger:** any future restructure that makes a mixed ObjC+Swift test module exist anyway, or a recurrence of the corpus shape — add the fixture then.

<a id="closure-delegate-parity-scanner-matches-trampoline-casts-per-file-not-per-member"></a>

## Closure-delegate parity scanner matches trampoline casts per file, not per member

2026-09 wave (s2). `build/Models/ClosureDelegateParityScanner.cs` asserts that every public closure delegate type has a matching trampoline cast by scanning the generated `.cs` per file; two members in one file with the same delegate shape satisfy each other, so a member whose cast went missing while a sibling's survived is not flagged. The gate still catches the whole-file regression it was built for. Also untested on that gate: the container element-spelling family (`[String?]`, `[Foundation.Data]` inside an array/dictionary element) — the parity fixture covers scalar and class elements only. **Trigger:** a per-member cast regression slips past the gate, or the parity fixture is next extended.

<a id="direct-lane-and-hand-over-arms-with-unit-coverage-only-no-end-to-end-fixture"></a>

## Direct-lane and hand-over arms with unit coverage only (no end-to-end fixture)

2026-09 wave (s3/s7/s9). (1) A closure typed `(GenericHolder<T>.Nested) -> Void` (nested type inside a generic parent) has no BindingTests fixture; (2) `SwiftMarshal.MarshalCallbackArgFromSlot<T>`'s true-class arm has no end-to-end footprint — every fixture class argument reaches it through the ObjC-bridge or by-value paths; (3) the `@owned` hand-over of ObjC-rooted and `URL`-typed init/setter parameters is asserted by the `SetterValue_IsHandedOver_OnlyWhenCalleeIsTheAccessor` oracle only; (4) the `SWIFT_INTERFACE_PARSER_PATH` positive-control log line never appears in BindingTests regen because that path does not pass `--swiftinterface` (by design, not a silent skip). A property-setter direct arm cannot be forced from a unit harness at all (the `@_cdecl` wrapper accepts nearly everything), so setter ownership is covered by the oracle theory and the `OwnedArgSetterHost`/`OwnedArgKeyedHost` runtime fixtures. **Trigger:** any of those arms changes, or a fixture in that shape is cheap to add alongside other closure work.

<a id="borrowedslotparamdispatchtests-intermittent-extra-survivor-causality-open"></a>

## `BorrowedSlotParamDispatchTests` intermittent extra survivor — causality OPEN

`TestBorrowedSlotParamRepeatedDispatchDoesNotLeak` intermittently reports 200 dispatches with 199 released and one survivor. It reproduces only under the full suite, never class-filtered, so direct diagnosis has not been possible; the autorelease-pool hypothesis is disconfirmed (the flake predates the worker thread and nothing on the path autoreleases). Rather than tune the assertion, the test was given the discriminator it lacked: a generation-safe registry identity for the survivor, weak observations of the wrapper and its handle, a fail-closed strong-root positive control, and an integer callback-count assertion in place of a bool. It passes on the current bytes. **A green is not a resolution here** — the classification only exists once the identity experiment fires on a real failure. Two limitations bound what that message can say: registry serials reset to zero on each `Reset`, so survivor identity is not generation-safe across windows; and `LifetimeTracker.Reset` fails open after two seconds, so a delayed prior-window deallocation can cancel or fabricate a current-window count. **Trigger:** the next full-suite occurrence — classify from the survivor identity the test now prints; a class-filtered green is not evidence either way.

<a id="counter-only-leak-probes-cannot-name-their-survivor"></a>

## Counter-only leak probes cannot name their survivor

The `BorrowedSlot` diagnostic work replaced a counter-only leak assertion with one that names the surviving object. The same shape — a bare counter, many distinct payloads, an exact-zero expectation — is still used by a family of probes that would be equally undiagnosable if they flaked: `Protocols/ClassParamCallbackTests.cs` (`TestPureSwiftClassParamReceiverNoLeak`, `TestObjCClassParamReceiverNoLeak`), `Closures/OptionalReferenceClosureArbiterTests.cs` (`TestEmitSwift_NonNil_NoLeak`), `MemoryManagement/ExistentialReturnLeakProbeTests.cs` (two tests), `MemoryManagement/ClassBoundExistentialCollectionLeakProbeTests.cs` (`TestNestedMarkerMapConsumerReceiverReleasesValues`) and `Lifetime/OwnershipGCStressTests.cs` (three `TestBundleB_ClosureLifetime_*`). None is flaking today, and widening the change costs a line per Swift class plus a re-run of every affected lane, which was not worth doing pre-emptively. **Trigger:** any one of them flakes — convert that one to a named-survivor assertion before diagnosing it, because a counter-only red carries no information.

<a id="the-bulk-closure-lifetime-probes-tolerate-a-residual-whose-stated-rationale-no-longer-holds"></a>

## The bulk closure-lifetime probes tolerate a residual whose stated rationale no longer holds

`AsyncClosureContextLifetimeTests.cs:41-42` and `EscapingClosureLifetimeTests.cs:44-45` allow `MaxResidualAlive = 5` out of `BulkIterations = 25`, with comments justifying the tolerance as a conservative-stack-scan noise floor. Those probes now allocate on a finished thread, so the stack the tolerance was sized against is gone and the comment describes a mechanism no longer in play. The number is not demonstrably wrong — it is simply no longer derived from anything, and a 20% allowance is loose enough to hide a real residual. **Fix shape:** re-derive it from observed numbers across the runtimes and rewrite the comment to say what is actually being tolerated; tighten only as far as the measurements support. **Trigger:** the next pass over the closure-lifetime probes, or a residual-leak investigation that needs these probes to be sharp.

<a id="parity-and-verification-tests-assert-presence-where-they-mean-equality"></a>

## Parity and verification tests assert presence where they mean equality

Four cleanup items, none behavioural: the parity tests assert `ContainsKey` only, so a present-but-wrong value passes; single-helper option construction in the same tests hides which helper produced a result; `DirectModeVerificationProjectParityTests.ManagedItems` uses `Elements` where `Descendants` is meant, so a nested item escapes the assertion; and an XML summary at `MemberValidationPipeline.cs:1068-1073` sits on the wrong member. Each weakens a signal rather than breaking one, which is why they were not folded into a fix wave that had accepted Highs in it. **Trigger:** the next pass over these tests, or a parity red whose message turns out not to say enough to diagnose it.

<a id="the-noncopyable-projection-corpus-guard-is-blind-to-the-corpus-s-generator-sha"></a>

## The noncopyable-projection corpus guard is blind to the corpus's generator sha

`NonCopyableValueProjectionCorpusTests` gates on `SkipUnlessAvailable`, which skips when `BindingTests/output` is absent but does not check what generated it. With a corpus present from an older generator sha the test hard-fails (`'NestedFrozenTokenHost' must still be emitted as a type`) instead of skipping. The failure is a stale artifact rather than a product regression, but it reds `nuke test` before the first regeneration after a corpus-affecting emitter change — exactly when a run is least able to tell those two apart. **Fix shape:** key the guard on the corpus's recorded generator sha and skip when it does not match, rather than asserting against it. **Trigger:** the next corpus-affecting emitter commit reds `nuke test` before its first regeneration.

<a id="swift-types-json-writes-the-pre-disambiguation-name"></a>

## `swift-types.json` writes the pre-disambiguation name

Collision-avoidance can rename a type (`BlinkIDSdk` → `BlinkIDSdkInfo`, because `BlinkIDSDK` already occupies the case-insensitive name) without writing the final name back into the type record. `swift-types.json` then names a type the emit does not contain — the machine-readable contract for docs generators. Observed on BlinkID's entry-point type; three package guides shipped samples against the wrong name. Fix: write the post-disambiguation identifier into the record before serialize; invariant: every `projectedCSharpName` exists in the emit. **Trigger:** a docs generator or package guide sample fails to compile because it trusted `swift-types.json`, or a second library hits a case-insensitive type/namespace collision.

<a id="producer-side-deep-versioned-mac-framework-bundles-xcframework-zip-transport"></a>

## Producer-side deep (versioned) Mac framework bundles + `.xcframework.zip` transport

The alternative design from the 2026-08-23 issue-#2 consult (diagnosis doc removed): build the macOS/Catalyst slices as deep bundles at every producer (`build-runtime.sh`, `Build.AppleSupplement.cs`, `SwiftWrapperCompiler`) and carry them across the NuGet boundary as `.xcframework.zip` — the workload's own symlink-safe vehicle — instead of repairing shape on the consumer's Mac. Deliberately not taken for v1: it rewrites the packaging contract across four producers, the merger/lipo path, both emitters, and every pack-structure gate on an unverified `.xcframework.zip` workload floor, and it cannot repair links flattened in transit, which the shipped consumer-side pre-sign deepen (`SwiftBindings.MacFrameworkAnatomy.targets` + `deepen-mac-framework.sh`) covers at the one choke point Apple actually validates. The deepen step is idempotent, so a later move to deep producers composes with it rather than fighting it. **Trigger:** our xcframeworks gain a supported non-.NET consumption path (where no consumer-side MSBuild step exists to repair shape), or the consumer-side step proves unmaintainable across .NET Apple workload updates (e.g. repeated re-anchoring of `_PostProcessAppBundle`/`Codesign`).

<a id="mac-app-store-pipeline-paths-the-appstore-hygiene-mac-legs-do-not-reach"></a>

## Mac App Store pipeline: paths the `--appstore-hygiene` Mac legs do not reach

<details>
<summary>Recorded evidence and disposition</summary>

Surfaced by the 2026-08-27 paired review of the issue-#3 fix (diagnosis doc removed). Four, each out of scope by nature rather than by budget: (1) **A credentialed distribution leg.** The Mac legs sign ad-hoc and carry only `com.apple.security.cs.allow-jit`; a real Mac App Store submission needs the App Sandbox entitlement, a Mac App Distribution identity plus provisioning profile, and a *signed* installer `.pkg` — the publish legs do expand the unsigned `.pkg` the workload builds and assert the payload app's framework layout, but the signing half is not ours to add to a consumer's project, and none of which can run on a host without those credentials. Structural assertions on an ad-hoc bundle would only restate the consumer's own csproj. *Reopen if:* a reporter's rejection names the entitlements/signing/installer of a bundle whose frameworks are already versioned, or the repo gains a signing identity in CI. (2) **Windows / Pair-to-Mac.** The rewrite makes symlinks in the bundle on the Mac that signs it; MSBuild's `Exec` runs on the Windows side, so the targets restrict themselves to a Unix host. *Reopen if:* a Pair-to-Mac consumer reports ITMS-90291/90292. (3) **Pure-ObjC-only consumers.** A pure-ObjC binding is emitted against plain `Microsoft.NET.Sdk` with no runtime reference, so neither import point reaches it; an app whose *only* Swift-bindings dependency is such a binding keeps its vendor framework shallow. *Reopen if:* such a consumer targets the Mac App Store. (4) **App extensions and XPC services with frameworks of their own.** The workload aggregates extension frameworks into the containing app and lists plug-ins/XPC services in `_DirectoriesToPublish`; the step covers `Contents/Frameworks` of the app only. *Reopen if:* a consumer ships a Mac extension or XPC service that embeds a Swift-bindings framework.

</details>

<a id="auto-dependency-records-still-carry-a-synthesized-package-identity"></a>

## Auto-dependency records still carry a *synthesized* package identity

**Revisit when:** a consumer needs the exact package id in the warning, or the dependency-record schema is being revised for another reason.

<details>
<summary>Recorded evidence and disposition</summary>

Residual after the 2026-07-31 SWIFTBIND080 fix (the false-positive half is gone — `AutoDepResolver` probe 5 now finds a sibling binding project by *content* regardless of its file name, and the warning text no longer names a nonexistent package). What is **not** fixed: the record itself. `binding-metadata.props` still emits `FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/…/FBAEMKit.xcframework` — the CLI default PackageId `{Module}.Swift.{Platform}` (`CliOptions.cs:92`, `PlatformInfoFactory.cs:97`) and version `0.0.0`, never the sibling's real identity. It cannot be fixed at the point of emission: the record is written by the *consumer's* generation run, which knows only the dependency's module name and xcframework path; the real package id lives in a sibling csproj this run has no reason to have parsed, and the vendor xcframework carries no such metadata. The consequence is now confined to the genuinely-unresolved case: SWIFTBIND080's remedy text must say `<id of the package that ships {Module}>` instead of a concrete id. **Fix shape if ever wanted:** have the *producing* project stamp its `PackageId`/`Version` into a marker inside its own xcframework or an adjacent metadata file at pack time, and have the extractor read it back — a new cross-run artifact contract, not a code tweak. **Trigger:** a consumer needs the exact package id in the warning, or the dependency-record schema is being revised for another reason.

</details>

<a id="swiftbind064-under-parallel-regression-jobs"></a>

## SWIFTBIND064 under parallel `--regression-jobs`

Observed once (StripeCore `_ImportSwiftBindingMetadata`) during a parallel multi-project regression matrix; never reproduced. Left unfixed as unactionable. **Trigger:** it reproduces on a parallel multi-project matrix run.

<a id="pure-objc-projectreference-consumers-still-hand-wire-the-nativereference"></a>

## Pure-ObjC `ProjectReference` consumers still hand-wire the `NativeReference`

A pure-ObjC package's native reaches `PackageReference` consumers through the classic Microsoft.iOS binding sidecar (`lib/<tfm>/<Assembly>.resources[.zip]`, extracted by `ResolveNativeReferences` — full model: [`Design/objc-binding-consumption.md`](../../Design/objc-binding-consumption.md)). That task runs only for a project that *references the assembly* and only when `IsBindingProject != 'true'`, and a `NativeReference` does not propagate through a `ProjectReference` — so an in-repo test app that project-references the binding must name the vendor xcframework itself (`swift-dotnet-packages` `libraries/MapLibre/tests/SwiftBindings.MapLibre.Tests.csproj`). Platform behaviour, not producer-side fixable; the packaged path (the one consumers use) is proven in the wild by `Plugin.Maui.Intercom.iOS.Binding` 0.7.1. **Trigger:** Microsoft.iOS starts propagating native references through `ProjectReference`, or an in-repo lane grows enough project-referenced ObjC bindings for the hand-wiring to become an error source.

<a id="app-store-privacy-manifest-assertion-only-sees-frameworks-embedded-under-their-source-name"></a>

## App Store privacy-manifest assertion only sees frameworks embedded under their source name

`AssertPrivacyManifestsPreserved` looks for `Frameworks/<Name>.framework` where `<Name>` is the *source* xcframework's framework name; if a copy step ever renamed or flattened the embed, the assertion silently skips it (`!Directory.Exists(embedded) → continue`) instead of failing, because "not embedded in this app" and "embedded under a different name" are indistinguishable from that path alone. Microsoft.iOS preserves bundle names today, and the alternative — treating every unmatched source manifest as a failure — would red-light every leg whose app legitimately embeds only a subset of the feed. The OK banner now reports how many embedded copies were actually compared, so an inert run is visible rather than claimed as coverage. **Trigger:** an embed step that renames framework bundles appears, or an ITMS-91053 rejection lands on a package this gate reported green.

<a id="no-in-repo-pure-objc-pack-fixture"></a>

## No in-repo pure-ObjC *pack* fixture

The harness has a pure-ObjC generator fixture (`BindingTests/Sources/ObjCUmbrella`, `--compile-only` lane) but it is simulator-only and `IsPackable=false` — it exists to exercise the ObjC generator and the mixed-companion shape, not packaging. So the pure-ObjC pack lane (sidecar-carries-the-native, no `runtimes/`, SWIFTBIND074) is gated by target-level MSBuild tests in `SdkTargetsBehaviorTests` and by the one shipped pure-ObjC nupkg (`SwiftBindings.Stripe.ThreeDS2` 26.4.1 — MapLibre and `FBSDKCoreKit_Basics` are built but have never been published), but no end-to-end `dotnet pack` of a pure-ObjC binding runs in this repo. Authoring one means a 2-slice (device + simulator) clang framework fixture plus a pack-and-inspect leg — new harness work, not a tweak. **Trigger:** a pure-ObjC packaging defect escapes the target-level tests, or a session already adding a PackGate lane.

<a id="swiftbind074-asserts-sidecar-shape-not-slice-contents"></a>

## SWIFTBIND074 asserts sidecar *shape*, not slice contents

The pure-ObjC pack guard (`_ValidateObjCBindingPackShape`) proves every xcframework that lands in the sidecar (`@(SwiftFramework)` ∪ existing `@(SwiftFrameworkDependency)` ∪ existing `@(NativeReference)`) is present in the binding resource package and that the packed copy still carries every slice directory the *source* xcframework has (ids compared verbatim against the source, so a vendor's `ios-arm64_x86_64-simulator` and a legitimately device-only xcframework both pass without the guard assuming a shape or a minimum slice count). It does **not** open the Mach-O to confirm the slice's architectures, nor that the slice actually contains the framework binary; an empty or wrong-arch slice would pass. Deliberate: the guard runs on every pack and the cheap shape check catches the whole observed failure class (payload missing / a slice dropped in the copy), while arch verification belongs to the App Store hygiene leg which already `lipo`s embedded binaries. **Trigger:** a pack ships a present-but-wrong-arch slice that the hygiene leg does not catch first.

<a id="performance-benchmarks"></a>

## Performance benchmarks

Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](../interop-performance-validation-plan.md)

<a id="api-snapshot-tooling"></a>

## API snapshot tooling

Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](../api-snapshot-tooling.md)

<a id="private-framework-dependencies-as-an-sdk-feature"></a>

## Private-framework dependencies as an SDK feature

Deferred 2026-05-02 design, not abandoned: an SDK mechanism for vendor-internal framework graphs. Stripe (`Stripe3DS2`, `StripeCameraCore`) ships through the simpler standalone-NuGet route instead, which works today and is *better* on consumer disk/download cost (one copy in the NuGet cache vs duplicated bundling per sibling). [`Future/private-framework-dependencies-plan.md`](../private-framework-dependencies-plan.md). **Trigger:** 2–3 real vendor cases with the same internal-framework graph shape (Firebase, Facebook SDK, …) *and* standalone-NuGet duplication causing user-visible problems that package metadata can't solve — designing against one Stripe-shaped hypothetical risks the wrong abstraction.

<a id="tvos-device-runner"></a>

## tvOS device runner

Requires provisioning profile + physical Apple TV. Generator, SDK, runtime, and build infra already support tvOS; only the `nuke runtime-tests-tvos-device` Nuke target and deployment mechanism are missing. **Trigger:** a tvOS-specific runtime defect is reported that the tvOS *simulator* lane cannot reproduce — hardware is the cost, so simulator coverage has to be shown insufficient first.

<a id="r4-sdk-auto-dep-verb-wording-uncapped-split-d3-d4"></a>

## R4 SDK auto-dep verb — wording + uncapped split (D3/D4)

Two confirmed-present-but-not-live SDK latents from the 2026-06 regression audit. **D3:** the hook-wiring tripwire (`Sdk.targets`, `_AssertSwiftBindingHookWiring`, `BeforeTargets="CoreCompile"`) also fires at `dotnet pack` time but its SWIFTBIND062/065 text says "did not run before **compilation**" — accurate-but-confusing wording on a pack-time failure (no false positive; a correctly-wired project never trips it). **D4:** the SWIFTBIND080 `Include=` reconstruction uses an uncapped `.Split('|')[4]` (`Sdk.targets`, Apple analog too); a literal `|` in a path would truncate it — but the producer percent-encodes `|`→`%7C` before it reaches the split (`XCFrameworkMetadataExtractor.DelimiterEscape`), so it's structurally unreachable via real input. Both cheap to harden opportunistically; neither has a live bad-symptom trigger of its own. **Trigger:** a session is already editing `Sdk.targets` — take both as riders then; standalone they are not worth a build-infra change.

<a id="r6-2-device-thunk-retention-integration-test-infra"></a>

## R6-2 device-thunk retention integration test (infra)

No `CompileAll`-level test builds a real 2-slice xcframework (one shared member + one `#if targetEnvironment(simulator)` thunked member) and asserts the device wrapper links with the sibling thunk retained; `nuke binding-tests --device` doesn't exercise `FilterThunkAssembly` today (it hand-compiles from unfiltered `.arm64.s`). The matching defect itself is fixed + unit-gated; this is the missing durable harness leg. **Trigger:** `FilterThunkAssembly` or the device-wrapper assembly path is changed again — the unit gate covers the fix, not the harness, so the next edit to that path is when the end-to-end leg earns its cost.

<a id="packages-harness-self-tests-are-not-wired-into-ci"></a>

## Packages harness self-tests are not wired into CI

`swift-dotnet-packages` `.github/workflows/ci.yml` never invokes `nuke TestHarness`, even though the aggregate runs in under a second, needs no simulator or packages, and would have caught the zero-hit `ProjectReference` deletion defect at the commit that introduced it. The tests exist and pass locally (125 asserts, 0 failures), so the gate is written but unenforced. **Trigger:** the next `ProjectReference`-injection regression that reaches a consumer, or any CI-workflow edit in that repo.

<a id="cross-library-hand-authored-refs-vs-the-prune-against-canonical-siblings-rule"></a>

## Cross-library hand-authored refs vs the prune-against-canonical-siblings rule

`swift-dotnet-packages` `libraries/BlinkIDUX/SwiftBindings.BlinkIDUX.csproj` holds a `ProjectReference` + `SwiftFrameworkDependency` to `../BlinkID/…`. Siblings are computed per-library, so a cross-library ref is not a known sibling and the prune rule would drop it if it were ever hand-moved INTO the auto-managed block. Safe today: BlinkIDUX is single-product so injection is skipped, and out-of-block items are never touched. **Trigger:** BlinkIDUX gains a second product, or any cross-library ref appears inside an auto-block.

<a id="concurrent-writer-artifact-coherence-residuals-post-1760a9f5"></a>

## Concurrent-writer artifact *coherence* residuals (post-`1760a9f5`)

**Revisit when:** divergent-content writers ever share an output dir by design (e.g. per-RID generation into the RID-agnostic `swift-binding/` dir), a consumer starts cross-checking manifest↔report coherence, or a generator process legitimately runs >1 h.

<details>
<summary>Recorded evidence and disposition</summary>

The atomic-writer fix makes concurrent generator invocations against one output directory complete instead of faulting, with last-writer-wins semantics. Two coherence residuals remain, both correct for the production shape (same module + same inputs through a deterministic generator ⇒ sections identical, only `GeneratedAt`/`UpdatedAt` differ): (1) `ReadModifyWrite` is whole-manifest LWW — divergent mutators from two live writers could silently drop a section; (2) the manifest and its derived `binding-report.json` are renamed independently, so interleaving can publish a pair from two different invocations (differing only in timestamps today; no consumer cross-checks pair coherence, and the next write rederives both). Both raised as the sole Mediums by the paired 2026-07-29 review; neither is reachable-High in the code as it exists. Sub-latents from the same review, all timing-window theoreticals: the 1-hour orphan sweep unlinks a temp whose writer has been suspended >1 h; the 1-day scratch-dir sweep can delete the dir of a >1-day-lived generator process (file writes don't refresh the parent dir mtime); same-process concurrent `Resolve` of one module still shares `{Module}.abi.json` in the per-process scratch dir (pre-existing shape). **Trigger:** divergent-content writers ever share an output dir by design (e.g. per-RID generation into the RID-agnostic `swift-binding/` dir), a consumer starts cross-checking manifest↔report coherence, or a generator process legitimately runs >1 h.

</details>

<a id="direct-write-props-files-fresh-emit-wipe-under-concurrency"></a>

## Direct-write props files & fresh-emit wipe under concurrency

Flagged by the `1760a9f5` category sweep as a *different* defect class (torn read / lost update, not temp-name collision), examined and deliberately not changed: `XCFrameworkMetadataExtractor` (`binding-metadata.props` at :328/:388, read-modify-write at :456–513) and `ObjCMetadataPropsEmitter.cs:41` (`apple-supplement.props`) write via direct `File.WriteAllText` / `XDocument.Load`+`Save` with no temp+rename at all; `AppleTypesCsCommand.cs:79` deletes every `*.cs` in its output dir at run start with no lock. Latent in the same shared-obj-dir scenario the manifest fix addressed. The wrapper aside→merge→restore directory staging (`WrapperXCFrameworkMerger`, `SwiftWrapperCompiler`) was also examined: different mechanism, already serialized by the SDK's `compile-wrapper-locked.sh` mkdir mutex — changing it wants a `--mixed-pack` runtime leg. **Trigger:** an observed torn/lost `*.props` write or a clobbered fresh-emit in a parallel matrix run.

<a id="sb1003-does-not-fire-on-a-struct-property-reached-through-a-generated-protocol-interface"></a>

## SB1003 does not fire on a struct property reached through a generated protocol interface

The write-back analyzer stays silent when the receiver is a generated protocol interface: those types do not implement `ISwiftObject` and are consumer-implementable, so firing there would false-positive on a consumer's own implementation. The result is a known false negative — a lost write through a protocol-typed receiver is not diagnosed. Accepted deliberately, documented in code and pinned by a test. **Trigger:** a consumer hits the lost write through a protocol-typed receiver in practice, or the analyzer gains a way to tell a generated conformer from a consumer-written one.

<a id="a-long-running-gate-does-not-notice-source-edits-made-after-it-started"></a>

## A long-running gate does not notice source edits made after it started

2026-09 wave (s8). A `binding-tests`/`validate` run that takes minutes regenerates once, at launch; an edit to generator or fixture source made while it runs is neither picked up nor flagged, so the green it reports describes the tree at launch and nothing in the result says so. (The sibling trap — a generator dll replaced out of band by an incremental build — is closed: the freshness stamp now pairs the source fingerprint with the dll hash and rebuilds `--no-incremental` on mismatch.) Fix = print the tree SHA and dirty state at each gate boundary and re-fingerprint at the end, failing the run if the tree moved. **Trigger:** a green gate is later shown to have run against source that was edited mid-run.

<a id="sim-with-an-explicit-device-udid-does-not-boot-the-simulator"></a>

## `--sim` with an explicit `--device-udid` does not boot the simulator

Only the no-UDID path calls `SimCtl.EnsureBootedDevice`; when a UDID is passed the target goes straight to the run, so a pinned simulator that happens to be Shutdown fails roughly five minutes in with `SimError 405 … Unable to lookup in current state: Shutdown`. The whole build ahead of the failure is paid first, and the message names the simulator state rather than the missing boot, so it reads as a simulator problem rather than a harness one. **Fix shape:** route the explicit-UDID path through the same boot helper the default path uses. **Trigger:** a harness or script that pins the simulator UDID hits it again.
