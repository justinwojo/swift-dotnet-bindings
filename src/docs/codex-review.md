# Codex Review: apple-nuget-rework vs main

Reviewed against:

- `src/docs/0.8.0-ship-plan.md`
- `src/docs/apple-swift-types-architecture.md`

## Plan of attack (3 sessions)

Findings cluster into three buckets: ship-affecting correctness (SDK wiring,
versioning, runtime hang), manifest/gate safety (fail-closed behavior that's
latent today because whitelists are empty), and coverage/polish/docs. Split
across three sessions with a validation checkpoint between each. The
blast-radius linkage fix (#9) gets its own session because real framework
trimming spans generator emission, SDK targets, and NativeAOT
re-measurement, and its scope is uncertain until investigation starts.

### Session 1 — Ship-affecting correctness + fail-closed safety (~4–5h)

Strict order — fail-closed guards land BEFORE the first real manifest
regeneration so silent type loss is physically impossible.

1. **#6a version plumbing (no regen yet)** — thread `--apple-version`
   through generator defaults, SDK metadata, manifest `sdk_train.major`,
   `regenerate.sh` targets, and any tests asserting `18.0.0`. Default on
   this branch becomes `26.0.0`; SDK/pack flows that ship artifacts
   require the flag to be explicit and fail loud if missing, to prevent
   stale defaults surviving future train bumps. Tests assert against
   `26.0.0`. **Do not regenerate `manifest.json` in this step** — only
   update the plumbing and the regen tooling.
2. **#4 SDK direct path** — propagate `AppleSupplementReferences` +
   supplement version into `binding-metadata.props` in `Sdk.targets:373`,
   matching the xcframework path.
3. **#7 runtime range** — split `SwiftRuntimeVersion` from
   `SwiftRuntimePackageVersionRange=[0.8.0,0.9.0)`, use the range in the
   SDK `PackageReference`. Both values derive from a single
   `VersionScope` source of truth; also route `BuildBoundedRuntimeVersionRange`
   callers through the same source so standalone and SDK paths cannot
   drift.
4. **#8 async stream** — concrete shape: the static unmanaged completion
   callback still resolves the stream from `context` (same pattern as
   the element callback), then invokes the instance delegate. Roughly:
   `var stream = SwiftAsyncStream<T>.FromContext(context); stream?.GetCompletionCallback()(context);`
   (or an equivalent runtime helper). Add a runtime test that emits an
   element, calls `EndAsync`, and iterates with a 5s timeout covering
   both element delivery and stream completion.
5. **#3 empty mangledName** — schema `minLength: 1` for accessor
   `symbol`/`library`, builder fails loud, emitter rejects blank accessor.
6. **#2 manifest regen guards** — expose requested identities from
   `IncludeFilter`, fail when any are unmatched (print the unmatched
   set), treat missing SDK / module-dump failures as fatal by default
   with an explicit `--allow-partial` opt-in for dev workflows, write
   manifest via temp file after validation. **Guards must be in place
   before any real regen runs.**
7. **#1 sequential-layout gate** — extend manifest schema with evidence
   fields for the missing 3 conditions (stored-field layout knowledge,
   copy/destroy triviality or explicit handling, runtime round-trip
   result). Evidence lives in the manifest rather than a separate
   validator approval record to keep one coordination surface. Refused
   whitelist entries fall back to VWT-opaque storage with a hard
   diagnostic that **fails a validation/build gate** — never silently
   omit the type, never silently ship a whitelist entry as opaque when
   its evidence is invalid.
8. **#11 PWT safety** — track unresolved conformance requirements in
   `PInvokeHelperContext`, then re-enable `TypeMetadataAccessorSkipGate`
   to return true (skip emission) when PWT shape is indeterminate. Skip
   rather than build-fail so validation-gate coverage keeps moving; skip
   must be visible in the validation baseline/report (not just a logger
   message) so regressions surface.
9. **#6b run regen + approve manifest diff** — only after #2, #3, #1
   guards are live. Regenerate against iOS 26.2 SDK, then **stop and ask
   the user to approve the diff** before the overwrite commits —
   type-set changes determine what ships in `SwiftBindings.Apple 26.0.0`.

Gates: `nuke test` + `nuke validate` + `nuke binding-tests` +
`nuke runtime-tests-simulator`. Also `nuke runtime-tests-device` for
#8/#11 (calling conventions touched).

### Session 2 — Coverage, prototype mode, docs (~3–4h)

1. **#10 cross-assembly round-trip** — add value factory in
   `AppleIdentity.ConsumerA`, typed/object acceptor in ConsumerB, assert
   `typeof(T)` equality plus a dispose/copy round-trip for at least one
   supplement-owned type.
2. **#13 UnsafeRawBuffer coverage** — add sliced/aliased span coverage
   and a large read-only payload case for the existing
   `UnsafeRawBufferPointer` → `ReadOnlySpan<byte>` bridging. **Scope is
   read-only only.** `UnsafeMutableRawBufferPointer` write-back is a
   separate Swift type and a separate API/marshalling contract; tracked
   as a post-ship feature item in `roadmap.md`, not part of §5.2's ship
   claim.
3. **#5 prototype-dir SDK** — add SDK property to enable prototype
   supplement emission, pass `--apple-supplement-prototype-dir` into both
   SDK generator commands, include in generation fingerprint, flow csproj
   path into `binding-metadata.props`.
4. **#12 MusicKit scoping** — scope `MusicLibraryAddable` conformers to
   `["MusicKit"]`, thread module context into static hint callers
   (`BoundGenericsHandler`, `MetatypeArrayBridgeEmitter`), make scoped
   hints fail closed when no module context is provided. No legacy
   opt-in flag — thread context through all callers properly so the
   model stays consistent.
5. **#14/#15/#16 docs + interim #9 honesty** — reword baseline claim to
   "90 clean + 5 known-errors, 61/61 Swift wrapper compile"; label
   missing memory references as external Claude memory; add an
   operational section (yank/deprecate, patch train, local
   manifest/prototype override, field diagnostics) to the architecture
   doc. Also update the blast-radius claim in both the architecture doc
   and the ship plan from "validated no unused force-linking" to
   "current smoke shows extra zero-byte system framework links;
   trimming in progress (Session 3)." This keeps docs truthful if any
   publish happens before Session 3 lands; Session 3 restores the
   stronger claim once trimming is proven.

Gates: `nuke test` + `nuke validate` + `nuke runtime-tests-simulator`
(+ `nuke binding-tests` since #5/#12 touch emitters).

### Session 3 — Framework linkage fix (#9) — **LANDED**

Root cause: the macios linker's
`tools/common/Assembly.cs::ComputeLinkerFlags` scans every referenced
assembly's ModuleReferences for strings containing `.framework/` and
force-adds `-framework X` to the native link line, regardless of P/Invoke
reachability. The old generator emitted
`[DllImport("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", …)]`
for every supplement module, so the scanner matched three paths
(Foundation, CryptoKit, ManagedSettings) and linked all three — even for
a `Locale.Language`-only consumer.

Trimming model: **bare DllImport library names + lazy runtime resolver.**
Chosen over per-module dylib split to minimize architectural churn
(`TypeOwnerRegistry`, NuGet packaging, cross-assembly identity test all
stay untouched).

Changes:
- `AppleTypesCsEmitter.ResolveLibraryPath` returns the bare module name
  (`"CryptoKit"`) for system frameworks; Swift stdlib paths stay
  absolute (they contain no `.framework/` substring).
- Emitter writes a `_AppleSupplementRegistration.cs` side-car with a
  `[ModuleInitializer]` that calls
  `SwiftFrameworkResolver.RegisterForAssembly(...)` so bare names
  `dlopen` from `/System/Library/Frameworks/{name}.framework/{name}` on
  first P/Invoke.
- `SwiftFrameworkResolver.GetSearchPaths` gains
  `/System/Library/Frameworks/{name}.framework/{name}` as the last
  fallback (app-bundled `@rpath` candidates still win when present).
- `AppleTypesManifestValidator.ResolveLibraryPath` keeps absolute
  `/System/Library/Frameworks/...` paths (host-side `NativeLibrary.Load`
  needs a concrete dyld path; no macios linker in play). Divergence
  documented in the method comment.

Result: committed `measurements/otool-L.diff` now shows a zero-framework
delta between baseline and consumer; only `SwiftBindings.Apple.dll`
(~49 KB) and its name string are added. PublishAot re-measurement remains
deferred pending AOT-compatible `Swift.Analyzers`.

Gates run: `nuke test` + `nuke validate` + `nuke binding-tests` +
`nuke runtime-tests-simulator` + `nuke runtime-tests-device`.

## Findings

1. Finding #1 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsEmitter.cs:117`, `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsCommand.cs:92`, `src/Swift.Bindings.Sdk/tools/apple-types-manifest/sequential-layout-whitelist.json:1`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:154` says sequential layout is allowed only after all six conditions are true: frozen, non-generic or fully layout-known, all stored fields known/layout-known, ABI size/alignment validated, copy/destroy trivial or explicitly handled, and runtime round-trip passing.

   Observation: `ShouldUseSequentialLayout` only gates on external whitelist membership, `frozen=true`, no `<` in the Swift identity, and non-null size/alignment. It does not verify stored-field layout knowledge, stride consistency, copy/destroy triviality or explicit handling, or a runtime round-trip result. The current whitelist is empty, so no checked-in type is currently emitted sequentially, but the failure mode is still wrong for a future whitelist entry: `AppleTypesCsEmitter` records the refusal in `SkippedEntries`, and `AppleTypesCsCommand` returns success instead of failing closed or falling back to VWT-opaque storage.

   Recommended fix: Make the sequential path a true all-six-condition gate. Add manifest/schema fields for the missing evidence or require a validator-produced approval record, and make a refused whitelist entry fail the command unless an explicit "skip invalid sequential entries" option is set. If the intended fail-closed behavior is VWT-opaque, convert invalid sequential requests to VWT with a hard diagnostic rather than silently omitting the type.

2. Finding #2 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestCommand.cs:78`, `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestBuilder.cs:384`, `src/Swift.Bindings.Sdk/tools/apple-types-manifest/regenerate.sh:41`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:52` defines the supplement as cross-major additive-only, and `include-types.json` is the positive list of identities the supplement owns.

   Observation: Regeneration can silently drop positive-listed types. The command writes `manifest.json` after logging only `matched {MatchedCount} of filter`; it never compares matched identities to the full include list. `regenerate.sh` also skips missing SDKs or `swift-api-digester` failures per module/platform and still overwrites the manifest if any ABI JSON was produced. If a module dump fails, or an include entry is added without updating `MODULES`, the regenerated manifest can lose types without failing.

   Recommended fix: Expose the requested include identities from `IncludeFilter`, fail when any requested type or alias is unmatched, and write the manifest through a temporary file only after validation passes. Treat skipped module/platform dumps as fatal by default, with an explicit opt-in for partial regeneration.

3. Finding #3 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesManifestBuilder.cs:234`, `src/Swift.Bindings.Sdk/tools/apple-types-manifest/schema.json:143`, `src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsEmitter.cs:87`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:91` says CI validates that the metadata accessor exists.

   Observation: Missing `mangledName` in ABI JSON becomes `metadata_accessor.symbol = ""`. The schema requires the `symbol` property but accepts an empty string, and the emitter only rejects a null `MetadataAccessor`, not a blank symbol or library. That can produce a generated import with an empty entry point instead of failing at manifest generation time.

   Recommended fix: Make `BuildMetadataAccessor` fail loudly for included types without `mangledName`. Add `minLength: 1` for `metadata_accessor.symbol` and `library` in the schema, and have the emitter/validator reject blank accessors as a final guard.

4. Finding #4 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:373`, `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:879`, `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs:312`

   Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:145` says supplement references are wired through the SDK; `src/docs/apple-swift-types-architecture.md:87` says SDK-driven prototyping emits a canonical supplement project into `obj/` and references it as a project dependency.

   Observation: The direct `SwiftAppleFrameworkTarget` SDK path synthesizes `binding-metadata.props` itself and writes only module/minimum OS/package/wrapper properties. It never writes `_SwiftBindingNeedsAppleSupplement`, `_SwiftBindingAppleSupplementVersion`, or `_SwiftBindingAppleSupplementPrototypeCsproj`, so `_InjectAppleSupplementPrototype` has no signal to add either a `ProjectReference` or `PackageReference`. The generator does emit those properties for the xcframework path through `XCFrameworkMetadataExtractor`, but the Apple-framework SDK path discards the `AppleSupplementReferences` signal.

   Recommended fix: Have the direct Apple-framework SDK generation path emit the same supplement-reference metadata as the xcframework path. At minimum, propagate `AppleSupplementReferences.Any`, the selected supplement version, and the optional prototype csproj path into `binding-metadata.props`.

5. Finding #5 — severity: P2 — area: implementation

   Location: `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:350`, `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:568`, `src/Swift.Bindings/src/Emitter/AppleSupplementPrototypeEmitter.cs:199`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:87` says demand-driven prototyping is SDK-driven and preserves canonical identity.

   Observation: The prototype emitter itself uses the safe shape: a separate project with `<AssemblyName>SwiftBindings.Apple</AssemblyName>`, `<RootNamespace>Swift</RootNamespace>`, and explicit compile items. That avoids the duplicate-compile trap. However, neither SDK generator command appends `--apple-supplement-prototype-dir`, so normal SDK builds never materialize the prototype project unless the user invokes the generator manually.

   Recommended fix: Add an SDK property to enable prototype supplement emission, pass `--apple-supplement-prototype-dir` into both SDK generator commands when enabled, include it in the generation fingerprint, and flow the resulting csproj path into `binding-metadata.props`.

6. Finding #6 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs:71`, `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs:320`, `src/Swift.Bindings.Sdk/tools/apple-types-manifest/manifest.json:6`, `src/Swift.Bindings.Sdk/tools/apple-types-manifest/regenerate.sh:58`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:31` says package major follows the Apple SDK train, `src/docs/apple-swift-types-architecture.md:133` says first ship is `SwiftBindings.Apple 26.0.0`, and `src/docs/0.8.0-ship-plan.md:202` packs with `--apple-version 26.0.0`.

   Observation: The generated supplement dependency floor still defaults to `18.0.0` in both standalone project emission and SDK metadata. The manifest also declares `sdk_train.major = 18`, and `regenerate.sh` uses iOS/tvOS 18 and macOS 15 targets. `Build.Pack` and `VersionScope` stamp the `Swift.Bindings.Apple.csproj` with `--apple-version`, but that value does not flow into generator defaults, SDK metadata, PackageReference emission, or the manifest train. A 26-built consumer can therefore emit `Version="18.0.0"` for `SwiftBindings.Apple`.

   Recommended fix: Add an explicit Apple supplement version/train input to generator and SDK flows, default this branch to `26.0.0` / the iOS 26.2 SDK train, and update tests that currently assume `18.0.0`. Keep the PackageReference open-ended, but make the floor the actual Apple train being shipped.

7. Finding #7 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings.Sdk/Sdk/Sdk.props:100`

   Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:60` requires `SwiftBindings.Runtime` dependencies to be bounded as `[0.8.0,0.9.0)`.

   Observation: The SDK path emits `<PackageReference Include="SwiftBindings.Runtime" Version="$(SwiftRuntimeVersion)" />`. `VersionScope` stamps `SwiftRuntimeVersion` with the exact main package version, and NuGet interprets a bare version such as `0.8.0` as a minimum-only range. Standalone generated csprojs use `BuildBoundedRuntimeVersionRange(...)`, but SDK consumers can float to `0.9.0`, which the docs explicitly try to prevent.

   Recommended fix: Split the exact generator/runtime version from the dependency range, for example `SwiftRuntimeVersion=0.8.0` plus `SwiftRuntimePackageVersionRange=[0.8.0,0.9.0)`, and use the range in the SDK `PackageReference`.

8. Finding #8 — severity: P1 — area: implementation

   Location: `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncStreamEmitter.cs:52`, `src/Swift.Runtime/src/Swift/SwiftAsyncStream.cs:131`, `BindingTests/RuntimeTestsApp/Async/ActorIsolatedTests.cs:131`

   Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:167` marks `ActorIsolatedAsyncStream` complete; `SwiftAsyncStream<T>` documents Swift calling a completion callback and C# consuming an `IAsyncEnumerable`.

   Observation: The generated Swift wrapper correctly dispatches through the actor with `for await element in await __self.events`, but the C# completion callback emitted by `AsyncStreamEmitter` is a no-op. `SwiftAsyncStream<T>.OnComplete` is the method that closes the channel, and it is never invoked by the generated static callback. The current runtime test only asserts the getter returns a non-null stream and explicitly avoids iteration, so `await foreach` consumers can hang forever after Swift calls `completionCallback(context)`.

   Recommended fix: Emit a completion callback that resolves the `SwiftAsyncStream<T>` instance from `context` and calls its completion path, or route the generated callback through the instance delegate returned by `GetCompletionCallback()`. Add a runtime test that iterates `ActorEventStream.Events`, emits an element, calls `EndAsync()`, and verifies both element delivery and stream completion under timeout.

9. Finding #9 — severity: P1 — area: implementation

   Location: `BindingTests/BlastRadius.Baseline/FINDINGS.md:35`, `BindingTests/BlastRadius.Baseline/measurements/otool-L.diff:13`, `BindingTests/BlastRadius.Baseline/measure-blast-radius.sh:28`

   Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:95` says the blast-radius smoke validates that the monolithic supplement does not force-link unused Apple frameworks; `src/docs/0.8.0-ship-plan.md:145` treats the smoke as complete.

   Observation: The consumer only references `typeof(Locale.Language)`, but the committed `otool -L` diff adds `CryptoKit.framework` and `ManagedSettings.framework`. `FINDINGS.md` calls this a zero-byte system-framework artifact and defers framework trimming, which means the smoke did not actually validate the "does not force-link unused Apple frameworks" claim. The script also documents that it uses `dotnet build`, not the NativeAOT single-framework app described by the architecture doc.

   Recommended fix: Either change supplement/package emission so unreferenced module P/Invokes do not force framework linkage, then rerun the intended NativeAOT measurement, or downgrade the docs to say the current smoke only measures managed-assembly size and that framework trimming remains deferred.

10. Finding #10 — severity: P2 — area: implementation

    Location: `BindingTests/RuntimeTestsApp/Marshalling/CrossAssemblyIdentityTests.cs:37`, `BindingTests/AppleIdentity.ConsumerA/TypeProbe.cs:21`, `BindingTests/AppleIdentity.ConsumerB/TypeProbe.cs:16`

    Claim / principle it contradicts: `src/docs/apple-swift-types-architecture.md:150` says the guardrail should instantiate a Swift-only type in one consumer assembly, pass it to the other, and assert `typeof(T)` matches.

    Observation: The test does assert `System.Type` reference equality and matching Swift metadata handles across two assemblies, which is stronger than checking fully qualified names. It does not instantiate a Swift-only value in ConsumerA or pass a value to ConsumerB, so it does not exercise payload ABI, copy/destroy, or cross-assembly value flow.

    Recommended fix: Add a value factory in ConsumerA and a typed/object acceptor in ConsumerB for at least one supplement-owned type. Assert that ConsumerB sees the same `typeof(T)`, can obtain metadata, and can dispose/copy/round-trip the value without loading a duplicate type.

11. Finding #11 — severity: P1 — area: implementation

    Location: `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs:196`, `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs:223`, `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs:248`, `src/Swift.Bindings/src/Emitter/StringEmitter/TypeMetadataAccessorSkipGate.cs:28`

    Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:167` marks MusicKit indirect-buffer ABI handling complete; the safety principle for new ABI shapes is to reject shapes the runtime cannot safely express.

    Observation: The new buffer-mode path handles positive cases where all metadata/PWT arguments are known, but it still silently drops unknown protocols and PAT/Self protocols without descriptor symbols before deciding whether `(metadata args + PWT args) > 3`. `TypeMetadataAccessorSkipGate` now always returns false. If ABI JSON describes constraints that the type database cannot resolve, the emitter can undercount PWT parameters and choose the wrong metadata accessor ABI instead of failing closed.

    Recommended fix: Track unresolved or unexpressible conformance requirements in `PInvokeHelperContext`. If any required PWT shape is incomplete, skip or fail type metadata accessor emission with a diagnostic; only compute the indirect-buffer threshold after all ABI-required metadata/PWT arguments are accounted for.

12. Finding #12 — severity: P2 — area: implementation

    Location: `src/Swift.Bindings/src/Data/specialization-hints.json:63`, `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:198`, `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs:195`, `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MetatypeArrayBridgeEmitter.cs:233`

    Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:161` marks MusicKit module-scoped specialization cleanup complete.

    Observation: Most MusicKit-specific hints are module-scoped, but `MusicKit.MusicLibraryAddable` conformers remain global. In addition, static hint callers without module context still allow scoped conformers: `IsConformerAllowedForModule` returns true when `moduleFilter` is null, and both `BoundGenericsHandler` and `MetatypeArrayBridgeEmitter` call the hint registry without a module filter. That leaves cross-module leakage paths despite the module-scoping model.

    Recommended fix: Scope the remaining MusicKit conformers to `["MusicKit"]`, then thread module context into the static hint checks. Where no module context exists, make scoped hints fail closed unless the caller explicitly requests global legacy behavior.

13. Finding #13 — severity: P2 — area: implementation

    Location: `BindingTests/RuntimeTestsApp/EdgeCases/UnsafeRawBufferPointerTests.cs:29`, `BindingTests/Sources/SwiftBindingsTestLib/UnsafeTypes/UnsafeRawBufferParam.swift:31`, `BindingTests/output/SwiftBindingsTestLib.cs:103703`

    Claim / principle it contradicts: `src/docs/0.8.0-ship-plan.md:165` marks `UnsafeRawBufferPointer` to `ReadOnlySpan<byte>` bridging complete.

    Observation: The generated C# pins `ReadOnlySpan<byte>` inside a `fixed` block and the Swift wrapper reconstructs `UnsafeRawBufferPointer(start:count:)` for a synchronous call, so the basic lifetime is sound for nonescaping use. The tests cover non-empty payloads, zero length, byte summing, and stackalloc spans. They do not cover aliased/sliced buffers, large buffers crossing any chunking or copy thresholds, or write-back/out-param behavior; the Swift fixture only has read-only `readBuffer` and `sumBytes` methods.

    Recommended fix: Add sliced/aliased array coverage and a large payload case. If mutable raw buffers or write-back through out/inout parameters are intentionally unsupported, document that boundary and keep a skip/assertion test; otherwise add a fixture that proves write-back semantics.

14. Finding #14 — severity: P2 — area: docs

    Location: `src/docs/0.8.0-ship-plan.md:20`, `.validation-baseline.json:488`

    Claim / principle it contradicts: The ship plan says "Baseline 95/95 CS compile, 61/61 Swift compile."

    Observation: `.validation-baseline.json` contains 95 compile-gate targets and 61 Swift wrapper compiles, but only 90 targets have `"compile": "ok"`. Five targets are `"known_errors"` with nonzero error counts: `FirebaseCoreExtension`, `CodeScanner`, `RichTextKit`, `WhatsNewKit`, and `YouTubePlayerKit`. If the intended metric is "validation gate accepted all 95 targets, including known-errors," the current doc wording reads too clean.

    Recommended fix: Reword the claim to "95/95 validation-gate pass: 90 clean + 5 known-errors; 61/61 Swift wrapper compile," or update the baseline if those five libraries should now be clean.

15. Finding #15 — severity: P2 — area: docs

    Location: `src/docs/0.8.0-ship-plan.md:214`, `src/docs/0.8.0-ship-plan.md:297`, `src/docs/0.8.0-ship-plan.md:306`

    Claim / principle it contradicts: The ship checklist should be executable from repo docs, and AGENTS.md says Claude `MEMORY.md` references are external only when identified as such.

    Observation: The ship plan references `feedback_no_commit_packages.md`, `reference_build_validate_script.md`, and `feedback_sdk_version_stable.md`, but none of those files exist in the repository. The live docs also no longer reference the deleted `m11b-recon.md` or `phase-2-session-plan.md`, which is good, but these remaining missing references make the Phase 3 checklist dependent on unstated external context.

    Recommended fix: Restore the missing docs, inline the required commands/checklist items, or label the references as external Claude memory. For Phase 3, also spell out order-of-operations hazards such as licensing before publish, whether pack is idempotent after a failed validation run, and local-feed validation before any first Apple publish.

16. Finding #16 — severity: P2 — area: gaps

    Location: `src/docs/apple-swift-types-architecture.md:52`, `src/docs/0.8.0-ship-plan.md:202`

    Claim / principle it contradicts: The architecture commits to an additive-only Apple supplement, and the ship plan moves directly from pack to publish.

    Observation: The two surviving docs do not describe a rollback or field-patching plan if `SwiftBindings.Apple 26.0.0` ships with a bad manifest, wrong metadata accessor, or over-broad framework linkage. They also do not explain how a consumer or framework-package author can locally override a supplement type, patch the manifest, or debug a supplement emission issue outside this repository.

    Recommended fix: Add a short operational section covering yanking/deprecating a bad package, publishing a fixed patch train, local manifest/prototype override steps, and field diagnostics for "type resolved to supplement but package/reference/method symbol is missing."

## Verified Matches / Non-Findings

- Runtime detection was `Darwin`; macOS-only inspection was allowed.
- `TypeOwnerRegistry` implements the documented resolver order, and the tests cover per-type overrides beating module defaults, stdlib/Runtime canonicals, ObjC workload ownership, Apple supplement module defaults, third-party package ownership, same-module fallback, and unsupported fallback.
- I found no overlap between `include-types.json` and the legacy Runtime-owned canonical set called out by the docs (`Date`, `Data`, `URL`, `Decimal`, `Measurement<T>`, `AnyError`, `Token<T>`, `SwiftUI.Text`). The supplement emitter also skips identities that `TypeOwnerRegistry` resolves to Runtime.
- When manually invoked, `AppleSupplementPrototypeEmitter` uses the canonical project-reference model rather than duplicate-compiling supplement types into each consumer assembly.
- The actor-isolated `AsyncStream` Swift wrapper awaits `__self.events`, so the actor-isolation dispatch question itself is handled. The issue is completion plumbing on the C# side, not direct actor access.
- The two surviving docs do not reference the deleted `m11b-recon.md` or `phase-2-session-plan.md`.
- TipKit Predicate-init remains documented as a deferral on the existential/fallback path, and Kingfisher `102 SB0001` remains documented as architectural post-ship work rather than something the supplement would have fixed.
