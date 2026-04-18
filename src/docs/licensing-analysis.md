# Licensing Analysis — SwiftBindings.Apple Publication

**Status:** Analysis complete; action items pending execution before first `nuget.org` publish of `SwiftBindings.Apple`.
**Date of analysis:** 2026-04-17
**Reviewed against:** Apple Developer Program License Agreement (ADPLA) version dated March 30, 2026.
**Overall risk assessment:** **2 / 5** (low-to-moderate), possibly **1.5 / 5** with the action items below applied.

This doc exists so that the licensing work for publishing `SwiftBindings.Apple` (and, by extension, the baseline interop packages) is not lost between sessions. It captures the full legal picture, the rationale for why the project can ship, and the concrete pre-publish checklist that must be completed before pushing to `nuget.org`.

Cross-referenced from:
- `src/docs/apple-swift-types-architecture.md` — Q10 item 5 and the Status section's "Non-code tracks" bullet.

## 1. What is being published

`SwiftBindings.Apple` is a supplement NuGet package that ships managed (C#) projections for a curated set of Apple-framework Swift types (e.g., `ProximityReader`, `Translation`, `LCK`, `WeatherKit`, `TipKit`, `FamilyControls`, `CryptoKit`-adjacent types) so that consumers of `SwiftBindings.Runtime` can interoperate with those frameworks without every consumer having to hand-generate bindings.

Inputs to generation:
- Swift ABI JSON emitted by the Apple-supplied Swift compiler.
- `dylib` binaries inside the Apple-supplied SDK `xcframework`s (we read symbols via `dlsym` at runtime — no static redistribution of Apple binaries).
- Swift module interfaces (`.swiftinterface`) for declaration surface.

Outputs in the package:
- C# source + compiled managed assemblies describing the *shape* (layout, size, stride, alignment, method signatures, ABI entry points) of the target Apple Swift types.
- No Apple binaries, no Apple headers, no copied Apple source code, no copied Apple documentation.

## 2. Legal landscape

### 2.1 Apple Developer Program License Agreement (ADPLA, March 30, 2026)

- **§2.6** — restricts redistribution of Apple SDK materials, APIs, and related documentation to other developers or end users outside the sanctioned channels.
- **§7.5 (library carve-out)** — permits the creation and distribution of "libraries" that interoperate with Apple frameworks, provided they do not redistribute Apple-owned SDK contents (headers, source, compiled binaries, docs) and do not present themselves as Apple products.

The §7.5 carve-out is the load-bearing provision. Because `SwiftBindings.Apple` ships only *interoperability metadata* — not Apple SDK contents themselves — the carve-out applies and §2.6 does not bar publication.

### 2.2 Copyrightability of ABI metadata (US)

Layout, stride, size, alignment, calling conventions, and ABI entry-point symbols are functional facts, not creative expression. Controlling precedents:

- **Feist Publications v. Rural Telephone Service Co.**, 499 U.S. 340 (1991) — uncreative factual compilations are not copyrightable.
- **Google v. Oracle**, 141 S. Ct. 1183 (2021) — declaring API code reimplemented for interoperability is fair use.
- **SAS Institute v. World Programming Ltd.**, Case C-406/10 (CJEU) / subsequent US treatment — the functional behavior and interface of a program is not protected; only its specific expressive source code is.
- **17 U.S.C. §102(b)** — codifies that ideas, procedures, processes, systems, and methods of operation are not copyrightable.

Taken together: reproducing a Swift type's ABI shape in C# form is outside copyright's scope even before reaching fair-use analysis.

### 2.3 Trademarks

- **"Swift"** — Apple holds trademarks but the Swift language itself is governed by the permissive Swift.org policy (Apache 2.0) that allows descriptive use of the name when referring to the language.
- **Nominative fair use** applies: the package name `SwiftBindings.Apple` describes the factual target (Swift bindings for Apple-framework types). It is not branding the project *as* Apple, and the disclaimer language (see checklist) removes any implication of endorsement.

### 2.4 Export control (CryptoKit-adjacent types)

- **15 CFR §742.15(b)** — publicly available open-source software containing or using cryptographic functionality is exempt from Export Administration Regulations licensing via the TSU exception, provided a notification email is filed with BIS and the NSA (for the ECCN 5D002 category) on or around first public release.
- CryptoKit *bindings* do not themselves implement cryptography — they are ABI shims over Apple's implementation. Shipping them does not create an ECCN exposure beyond the standard open-source interop case, and the TSU exception covers it regardless.

### 2.5 DMCA §1201 (anti-circumvention)

- `dlsym` against public, unencrypted, undocumented-but-exported symbols in an Apple-shipped `dylib` is not "circumvention of a technological protection measure." There is no access-control measure being bypassed; the symbols are exported and loadable by any program.
- No §1201 exposure.

## 3. Pre-publish checklist

These eleven items must be completed before the first `nuget.org` push of `SwiftBindings.Apple`. Several also apply retroactively to `SwiftBindings.Runtime` and `SwiftBindings.Sdk`.

**Versioning note:** `SwiftBindings.Apple` ships at `26.0.0` (Apple SDK train major — see the "Versioning for consumers" section of `apple-swift-types-architecture.md`). `SwiftBindings.Runtime` and `SwiftBindings.Sdk` ship at `0.8.x` on their own generator cadence. The version stamps are independent; `nuke pack` must be split to support this before the first Apple publish (see checklist item 11).

### Legal / disclosure content

1. **`LICENSE` file (MIT)** — already present in repo, retained as-is.
2. **`NOTICE.md` file (new)** — add at repo root. Content shape:
   - Statement that the project is an independent Swift/.NET interoperability toolkit.
   - Statement that it is not affiliated with, endorsed by, or sponsored by Apple Inc.
   - Statement that "Swift" is a trademark of Apple Inc., used descriptively under Swift.org's Apache 2.0 policy.
   - Statement that the project ships interoperability metadata only, not Apple SDK contents.
   - Statement that consumers building against Apple SDKs are responsible for their own ADPLA compliance.
3. **Repo-level `README.md` — affiliation disclaimer** — add a top-of-README line: "This project is not affiliated with, endorsed by, or sponsored by Apple Inc. `SwiftBindings.*` is an independent interoperability toolkit."
4. **`CONTRIBUTING.md` — derivation-pipeline transparency paragraph** — short paragraph describing that the generator reads Apple-provided ABI JSON and resolves symbols via `dlsym` at runtime; no Apple source, headers, binaries, or docs are copied or redistributed.

### Package metadata (all four NuGet packages: Runtime, SDK, Templates, Apple)

5. **NuGet package metadata** — in each `.csproj` / `.nuspec` (Runtime, SDK, Templates, Apple):
   - `<PackageLicenseFile>LICENSE</PackageLicenseFile>`
   - `<None Include="..\..\LICENSE" Pack="true" PackagePath="" />`
   - `<None Include="..\..\NOTICE.md" Pack="true" PackagePath="" />`
   - `<Copyright>© Justin Wojciechowski</Copyright>` (no Apple copyright string).
   - No `<licenseExpression>` that references Apple.
6. **Package naming** — keep `SwiftBindings.Apple`. Do not rename to avoid "Apple" — the descriptive use is protected and renaming would obscure the package's purpose.
7. **Apply retroactively to `SwiftBindings.Runtime`, `SwiftBindings.Sdk`, and `SwiftBindings.Templates`** — the NOTICE.md + repo-level disclaimer + package-metadata items (2, 3, 5) apply to all four packages, not just `SwiftBindings.Apple`.

### Consumer guidance

8. **Wiki page: entitlements for restricted frameworks** — on the GitHub wiki, a page covering which of the supplemented frameworks require Apple-issued entitlements on the *consumer* side (Family Controls, DeviceActivity, ManagedSettings). The supplement does not itself require entitlements — shipping bindings for these types is not gated — but consumers must obtain entitlements to run code using them on a device. Make this responsibility split explicit.

### Restraints

9. **Do not preemptively contact Apple licensing team.** Opening a ticket creates a written record requesting permission for something the §7.5 carve-out already authorizes. It invites an ambiguous response that would be harder to proceed from than silence. If Apple contacts the project, respond promptly and factually; do not reach out first.
10. **Do not ship Apple binaries, headers, or copied Apple source code** — ever. The §7.5 carve-out depends on this line holding. The generator reads Apple content at the consumer's build machine; the published NuGet package contains only derivative interop metadata.

### Tooling

11. **Decouple the version stamp so `SwiftBindings.Apple` and `SwiftBindings.Runtime`/`Sdk` can ship at independent versions.** Today `build/Helpers/VersionScope.cs` stamps all four csproj files (Runtime, SDK, Templates, Apple) from a single `--version` argument. Split this so `nuke pack --version 0.8.0 --apple-version 26.0.0` (or equivalent — two arguments, two stamp paths) sets each independently. Scope: `VersionScope.cs`, `Build.Pack.cs`, and any CI script that invokes `nuke pack`. Acceptance: built `.nupkg` set contains `SwiftBindings.Runtime.0.8.0.nupkg`, `SwiftBindings.Sdk.0.8.0.nupkg`, `SwiftBindings.Templates.0.8.0.nupkg`, and `SwiftBindings.Apple.26.0.0.nupkg` in the same run.

## 4. Day-of-publish verifications

Immediately before the first publish, re-check these three sources in case Apple has updated terms since this analysis:

- **ADPLA current version** — https://developer.apple.com/terms/ . Confirm §2.6 still counterbalanced by §7.5 library carve-out. If the library carve-out has been removed or materially narrowed, halt the publish and re-assess.
- **Xcode and Apple SDKs Agreement** — skim for any new distribution restrictions specific to Swift interop or to ABI metadata. (This agreement is a sibling of the ADPLA and can change independently.)
- **Swift trademark page** — https://www.swift.org/community/#trademarks . Confirm descriptive use of "Swift" is still permitted under Apache 2.0 / Swift.org policy. If Apple has tightened Swift trademark use, revisit the package name.

If any of the three has materially changed, pause publication and update this doc with the new analysis before continuing.

## 5. Sequencing

1. **M11b code work** — complete the remaining Apple-supplement manifest coverage (Translation, LCK, WeatherKit, TipKit, FamilyControls, CryptoKit revisit). Tracked in `src/docs/apple-swift-types-architecture.md` Status section.
2. **Licensing pass** — execute checklist items 1–7 and 10 in a single PR touching the four packages + repo root files.
3. **Tooling decouple** — checklist item 11 (split the version stamp so Apple can ship at `26.0.0` while Runtime/Sdk stay on `0.8.x`).
4. **Wiki entitlement page** — checklist item 8, on the external wiki repo (`/Users/wojo/Dev/swift-dotnet-packages.wiki`).
5. **`nuke pack --version 0.8.0 --apple-version 26.0.0`** — build all four NuGet packages with the new NOTICE / metadata in place and independent version stamps.
6. **Day-of-publish verification** — §4 items above.
7. **Publish to `nuget.org`** — all four packages in one batch.

## 6. Post-publish

- Keep this doc updated if Apple changes the ADPLA or if the project's shipping content changes (e.g., if binaries ever need to be redistributed, which would require a fresh analysis).
- If a third party raises a licensing concern, respond factually with reference to §7.5 and Feist/Oracle v. Google, not by removing the package.
- Monitor the annual ADPLA refresh; file any change that affects §7.5 or §2.6 as an issue on the main repo.
