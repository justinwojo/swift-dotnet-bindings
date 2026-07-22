# Private framework dependencies — SDK feature plan (DEFERRED)

> **Status: deferred 2026-05-02.** Stripe ships through the simpler standalone-NuGet route instead. See `swift-dotnet-packages/PRIVATE-FRAMEWORK-DEPENDENCIES.md` (not on main; recover via git history/worktrees) for the postponement note. Revisit this doc when we have 2–3 real vendor cases (Firebase, Facebook SDK, etc.) — designing the SDK feature against one Stripe-shaped hypothetical risks the wrong abstraction.

## Why this is deferred (not abandoned)

Stripe ships `Stripe3DS2` and `StripeCameraCore` in their public iOS release zip — they are public distribution artifacts, just "internal" by Stripe's mental model. Standalone NuGets work today: NuGet's transitive-dep mechanism is the right tool for shared deps, the existing rewriter and pack pipeline already cover the path, and consumers' disk/download cost is *better* with separate NuGets (one copy in the NuGet cache vs. duplicated bundling across every sibling pkg that links the dep).

The SDK feature only becomes compelling when:
- We have multiple vendors with the same internal-framework graph shape (Firebase, Facebook SDK, etc.), and
- Standalone-NuGet duplication actually creates user-visible problems we can't solve with package descriptions / `DevelopmentDependency` flags.

Until both are true, the SDK feature is YAGNI surface area.

## Goal (when revisited)

Let multi-product Swift libraries declare a build/runtime framework dependency on a sibling xcframework that gets bundled into the consuming pkg's `.nupkg` rather than published as a standalone NuGet.

## Recommended shape (revised from round-1 review)

**Don't add a new item type.** Add a metadata attribute on existing `SwiftFrameworkDependency`:

```xml
<SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework"
                          BundleInPackage="true" />
```

Codex's round-1 review surfaced this as the lower-risk shape, and round-2 reaffirmed it. Why metadata over a new item type:

- Existing dedup against `@(SwiftFrameworkDependency)` in `_DiscoverProjectReferenceDependencies` and `_ResolveSwiftAutoDetectedDependencies` in `Sdk.targets` already sees the entry, so auto-detection won't re-warn about a dep we declared explicitly. A separate item type would have required parallel changes at both sites and any future site that joins them.
- Build-time framework search path, fingerprint identity, and local NativeReference resolution in `Sdk.targets` all already iterate `@(SwiftFrameworkDependency)` — no parallel-iteration code needed.
- Pack-time validation `_ValidateSwiftDependencyMetadata` in `Sdk.targets` just adds a `BundleInPackage != 'true'` clause to its existing condition.
- Rewriter changes in `swift-dotnet-packages` shrink to "set one attribute" instead of "switch element name."

Naming: `BundleInPackage="true"` reads as the SDK behavior (vs. `Visibility="Private"` which collides with `PrivateAssets` semantics on `PackageReference`).

## Implementation surface (when revisited)

Concentrated changes — fewer integration points than the original new-item-type plan.

### SDK changes (`swift-bindings`)

1. **`_ValidateSwiftDependencyMetadata` in `Sdk.targets`** — skip `PackageId`/`PackageVersion` requirement when `%(BundleInPackage)' == 'true'`. SWIFTBIND040 message updates to mention the `BundleInPackage` opt-out.
2. **`_ConfigureSwiftBindingPack` in `Sdk.targets`** — add a `TfmSpecificPackageFile` entry that packs the entire xcframework directory into `runtimes/<rid>/native/<name>.xcframework/` for each SFD with `BundleInPackage=true`. Verify per-item `%(...)/**` glob expansion actually pack-walks every entry; fall back to materializing into an intermediate item if it misbehaves. Add a pre-pack existence guard (a new SWIFTBINDxxx — next free id).
3. **Slicer in `Sdk.targets`** — extend `_SliceSourceXcframework` (or add a parallel `_SlicePrivateXcframework`) so bundled xcframeworks ship per-RID, parity with source xcframeworks. Round-1 review flagged unsliced shipping as avoidable bloat.
4. **`ConsumerTargetsEmitter.cs`** — emit a `NativeReference` for each bundled private xcframework so consumers auto-link when they restore the pkg. Today's emitter handles source/wrapper/bridge; add private-bundled to the same path. Per-RID path matches whatever slicer ships.
5. **Generator metadata flow** — pass the bundle flag through to the generator so `binding-metadata.props` (via `EmitMetadataProps` in `BindingsGeneratorCommand` / `XCFrameworkMetadataExtractor`) and `ConsumerTargetsEmitter` know which deps are bundled. Single CLI flag (e.g. `--bundled-framework-dependency` or `--framework-dependency` with a key:value form), not two parallel flags.
6. **`GetSwiftFrameworkSearchPaths` in `Sdk.targets`** — return bundled deps too so multi-hop ProjectReference graphs (C → B → A where A is bundled in B) get A on `-F` during C's wrapper compile.
7. **Diagnostics** — sharpen SWIFTBIND040 message; tighten the duplicate-declaration check (a new diagnostic id) to compare normalized absolute paths, not filenames; update existing `SwiftFrameworkDependency`-mention strings in `Program.cs` and `SwiftWrapperCompiler.cs` to mention the new attribute.

### Rewriter changes (`swift-dotnet-packages`)

1. **`Build.Dependencies.cs::InjectFrameworkDepsForLibrary`** — when the resolved sibling product has `Internal == true`, set `BundleInPackage="true"` on the emitted SFD instead of populating `PackageId`/`PackageVersion`.
2. **`Helpers/CsprojRewriter.cs::ApplyFrameworkDeps`** — emit the attribute (still a `SwiftFrameworkDependency` element, just with the bundle metadata). Add a rewriter test for the bundled form.

### Tests

Multi-product fixture: project A (`IsPackable=false`, builds an xcframework, no NuGet metadata), project B (`IsPackable=true`, declares `<SwiftFrameworkDependency Include="../A/A.xcframework" BundleInPackage="true" />`). Pack B; assert: pack succeeds, nupkg ships `runtimes/<rid>/native/A.xcframework/`, `B.targets` inside the nupkg references it as `NativeReference`. Negative cases: missing file → a new SWIFTBINDxxx (next free id); declared as both bundled and public → a new diagnostic id.

## Open questions for revisit

- Does the per-item `TfmSpecificPackageFile %(...)/**` glob actually batch correctly at pack time, or do we need to materialize first?
- Module-database limitation: bundled deps don't ship a database, so cross-module type references that span a private boundary can't resolve. Acceptable for the "private dep is implementation-detail" case; document loudly that "public API exposes private module types" is unsupported under bundling.
- Multi-pkg duplication policy: with N siblings each bundling the same private xcframework, consumers installing M of them get M copies on disk (dyld de-dupes at runtime, but disk/download is M×). Codex round-2 noted this is *worse* than the standalone-NuGet path; bundling only wins when "doesn't appear on nuget.org" is a hard product requirement.

## Round-1 review record (for the future implementer)

Codex's round-1 review of an earlier new-item-type version of this plan flagged four High structural issues:

1. **Auto-detect dedup miss** — explicit private deps wouldn't suppress `_AutoFrameworkDependency` warnings unless the dedup sites learn about the new item. Resolved by the metadata-attribute shape above (existing dedup already iterates `SwiftFrameworkDependency`).
2. **Generator-flag double-feed** — passing the same xcframework as both `--framework-dependency` and a new `--private-framework-dependency` would write it into `binding-metadata.props` twice and re-trigger auto-detection. Resolved by single-flag-with-metadata.
3. **Consumer-target wiring** — `runtimes/<rid>/native/` placement alone doesn't auto-link in .NET iOS; the generated `$(PackageId).targets` must explicitly add `NativeReference`. Captured in step 4 above.
4. **SDK-mode emitter coverage** — `ConsumerTargetsEmitter.Emit` is called even in SDK xcframework mode (`BindingsGeneratorCommand`), distinct from Apple-framework mode's `_SynthesizeAppleFrameworkConsumerTargets` path in `Sdk.targets`. The future implementation must cover xcframework-mode bindings — Apple-framework mode is out-of-scope (Apple frameworks have no internal-dep graph).

## Round-2 strategic call (why we deferred)

Codex round-2 framing, paraphrased: "B now, design A deliberately next, after seeing 2–3 real vendor graphs." Picking A for one Stripe-shaped case risks the wrong abstraction; picking B uses paths the SDK already supports. The reversibility argument matters too: A is a public SDK contract once shipped, B is "deprecate two pkgs and migrate" if we ever flip.

## Trigger to revisit

Reopen this plan when any of the following hits:

- A second vendor with the same internal-framework graph lands on the roadmap (Firebase, Facebook SDK, GoogleMobileAds, etc.).
- Consumer feedback explicitly calls out the standalone-NuGet pattern as confusing (e.g., "why does my project depend on `SwiftBindings.Stripe.3DS2`?").
- NuGet feed-publishing constraints force a smaller pkg count (we don't have these today).
- Disk/download duplication from the standalone-NuGet model becomes a measurable problem (it currently isn't — NuGet de-dupes by package identity, so adding pkgs doesn't multiply size for users).
