# Apple Swift-Only Types: Architecture Reference

Authoritative design reference for the `SwiftBindings.Apple` supplement package.

## Why the supplement exists

Apple's modern iOS frameworks increasingly expose pure-Swift value types
with no Objective-C bridge (`Foundation.Locale.Language`,
`ManagedSettings.Application`, `CryptoKit.P256.Signing.ECDSASignature`,
…). `Microsoft.iOS` binds only the ObjC-bridged surface, so framework
packages like Translation or CryptoKit referenced types that had no
managed definition anywhere. The supplement is the central,
identity-preserving home for those generated types.

Hand-rolling them into `SwiftBindings.Runtime` — the pre-Phase-2 pattern
for a handful of legacy canonicals — doesn't scale: every iOS version
adds types, every hand-roll needs VWT/mangled-symbol/frozen analysis,
and it couples `Runtime` version to iOS SDK version. Those Foundation /
ManagedSettings / SwiftUI hand-rolls have since moved into
`SwiftBindings.Apple` (see decision 2); Runtime keeps only a few stdlib
pins.

## Decision summary

1. **One package: `SwiftBindings.Apple`**, versioned per Apple SDK train
   (iOS 26 → `26.x.x`). Internal CLR namespacing per Swift module
   (`Swift.Foundation.*`, `Swift.ManagedSettings.*`, …).
2. **Supplement owns the Apple Swift-only surface (generated + moved
   hand-rolls).** Legacy Foundation / ManagedSettings / SwiftUI hand-rolls
   that once lived in Runtime now ship from `SwiftBindings.Apple`
   (`Foundation.Data`, `.URL`, `.AnyError`, `.Measurement<T>`,
   `ManagedSettings.Token<T>`, `SwiftUI.Text`, …). `Foundation.Date` and
   `Decimal` are projections (to `double` / `NSDecimalNumber`), not Runtime
   hand-rolls. `SwiftBindings.Runtime` retains only the stdlib pins in
   `TypeOwnerRegistry.s_legacyRuntimeCanonicals`: `Swift.AnyHashable`,
   `Swift.Hasher`, `Swift.DispatchQueue`, `Swift.String`. A broader
   `[TypeForwardedTo]` migration for remaining identity cleanups was
   rejected because it would require `Runtime` to reference `Apple`
   (cycle / violates runtime SDK-agnosticism) and cannot rename types.
   Revisit at a deliberate major-version cleanup.
3. **VWT-backed opaque storage is the default for ALL supplement types.**
   `[StructLayout(Sequential)]` is emitted only via explicit per-type
   whitelist after metadata size/alignment verification and runtime
   round-trip validation. `frozen=true` in ABI JSON is necessary but
   not sufficient — memory-corruption risk is too high to trust a
   single flag.
4. **Authoritative metadata lives in a third artifact.** Embedded under
   `src/Swift.Bindings.Sdk/tools/apple-types-manifest/` (still current in
   the SDK tools tree) — one NuGet coordination surface instead of two.
   Format designed so it can later be extracted into a standalone data
   package without reformatting.
5. **Package-version invariant:**
   - **Package major = Apple SDK train major.** One release per train.
   - **Cross-major additive-only.** Every new major is a strict superset
     of every prior major's public surface. Types/members may be added
     or deprecated via attributes, but NOT removed or renamed.
   - **Consumer ranges are open-ended** (`>=26.0.0`). A graph mixing
     iOS 26– and iOS 27–built consumers unifies at the higher
     supplement; additive-only commitment keeps the 26 consumer's
     references resolving.
   - **When an invariant violation is unavoidable**, ship the break
     under a new package name (`SwiftBindings.Apple.v2`), NOT as a
     breaking-major of `SwiftBindings.Apple`. v2 lives under a distinct
     CLR namespace root (`Swift.Foundation.V2.*`), so v1 and v2 can
     coexist in a graph without duplicate-definition error.
   - **Runtime ABI compatibility is the user's responsibility.**
     `TypeOwnerRegistry` emits a compile-time error if it detects both
     v1 and v2 as owners for the same Swift identity. We do NOT enforce
     mutual exclusion via MSBuild/NuGet targets; the namespace split is
     the enforcement.
6. **Type ownership via `TypeOwnerRegistry`** with per-type override
   precedence. Resolver order:
   1. Per-type owner override (legacy canonical types pinned to
      `SwiftBindings.Runtime`).
   2. Swift stdlib known type.
   3. ObjC workload type / projection (e.g. NSDate for NSLocale).
   4. Module-default supplement lookup (Apple modules →
      `SwiftBindings.Apple`; third-party Swift modules → their
      generated binding package).
   5. Same-module type being generated.
   6. Unsupported (skip member).

   A consumer pulls `SwiftBindings.Apple` only if it actually references
   a type the registry resolves to it.
7. **Monolithic single assembly first.** Multi-assembly-inside-package
   deferred until concrete need (size, trim, identity hygiene).
8. **Demand-driven prototyping mode shares canonical identity.** SDK
   emits a supplement project into `obj/` and references it as a project
   dependency; NOT compiled as duplicate types into each consumer
   assembly. Otherwise identity fractures.
9. **CI validates supplement metadata against live SDK symbols.**
   Metadata accessor symbol resolves, manifest size/alignment/stride
   match the live VWT, every type (POD and non-POD) passes a zeroed-
   buffer InitializeWithCopy + Destroy smoke, and Optional<T> round-
   trips via the single-payload enum witnesses on T. Implemented as
   `ValidateAppleTypesManifest`.
10. **Framework-linkage blast-radius smoke test.** A macOS `otool -L` /
    `nm -gU` / `strings` diff between a baseline app (Swift.Runtime only)
    and a consumer app (Swift.Runtime + `SwiftBindings.Apple`, touching
    one supplement type). Diffs are committed at
    `BindingTests/BlastRadius.Baseline/measurements/`. Session 3 closed
    the gap that Session 5's smoke flagged: the generator now emits bare
    DllImport names (`[DllImport("CryptoKit", …)]`) and registers
    `SwiftFrameworkResolver` via a `[ModuleInitializer]` side-car, so
    the macios linker's `.framework/`-substring scan no longer force-
    adds `-framework` entries for unreferenced modules. A
    `Locale.Language`-only consumer now shows a zero-byte linkage delta
    across `otool -L` and `nm -gU`; only the `SwiftBindings.Apple`
    managed assembly (~49 KB) and its name string are added.
    PublishAot re-measurement remains deferred until
    `Swift.Analyzers` becomes AOT-compatible.
11. **Single-package model is NOT a cure-all.** It eliminates per-module
    supplement diamond/version-skew risk. It does NOT eliminate all
    NuGet graph conflicts — exact-version pins, stale top-level
    overrides, private feeds, and lock files can still break restore.
    General NuGet hazards, not specific to this design.

## Versioning for consumers

**One-sentence rule:** if your app targets iOS 26, use
`SwiftBindings.Apple >=26.0.0`. iOS 27 → `>=27.0.0`. That's it.

| Digit | Tracks | Consumer impact |
|---|---|---|
| **Major** (e.g., `26`) | Apple SDK train | Pick the major ≥ your app's minimum iOS target. Newer majors are always a strict superset — safe to upgrade. |
| **Minor** (e.g., `26.1.0`) | Package-internal: new types supplemented, new framework added, new generator capability | Always safe to upgrade. Not coupled to Apple's iOS minor cadence. |
| **Patch** (e.g., `26.0.1`) | Package-internal: bug fixes within the same coverage surface | Always safe to upgrade. |

Minor/patch are deliberately decoupled from Apple's iOS minor/patch:
package bug fixes happen on our cadence, not every Apple minor adds
Swift-only types, and release notes (not version digits) tell consumers
"this covers iOS 26.2 types."

### Decoupling from `SwiftBindings.Runtime` / `SwiftBindings.Sdk`

`SwiftBindings.Runtime` and `SwiftBindings.Sdk` version on generator
cadence (currently `0.17.x`). `SwiftBindings.Apple` versions on Apple
SDK train (currently `26.2.x`). These are semantically unrelated and
MUST NOT share a version stamp.

`build/Helpers/VersionScope.cs` + `Build.Pack.cs` accept both
`--version` (main) and `--apple-version` so each package stamps
independently.

## Implementation specifics

1. **Package name:** `SwiftBindings.Apple`. First ship train:
   `26.2.x` (e.g. `26.2.0`), built against iOS 26.2 SDK.
2. **CLR namespaces:** `Swift.Foundation.*`, `Swift.ManagedSettings.*`,
   `Swift.CryptoKit.*`, etc. — mirror Apple's module organization.
3. **Generation modes:**
   - **Demand-driven** (prototyping): SDK detects unresolved Swift-only
     types, emits a canonical supplement project into `obj/` referenced
     as a project dependency from the consumer.
   - **Pre-built** (shipping): single supplement package published to
     NuGet, consumed transitively via `TypeOwnerRegistry`-driven
     references.
4. **Type DB split.** `TypeRecord` decomposed into three concepts:
   - **Swift identity** (e.g., `Foundation.Locale.Language`)
   - **Managed projection** (consumer-facing C# type; may be
     `Foundation.NSDate` for Date, supplement type for Locale.Language)
   - **ABI carrier** (C# type used to copy/destroy/pass values across
     the Swift→C boundary)
5. **Cross-module type identity test:** build two consumer assemblies
   referencing the supplement, instantiate a Swift-only type in one,
   pass to the other, assert `typeof(T)` matches. Permanent regression
   guardrail under `BindingTests/`.
6. **Storage strategy:** VWT-backed opaque by default; sequential
   layout is per-type whitelist after ALL of:
   - `frozen=true` in ABI JSON
   - non-generic (or fully layout-known instantiation)
   - all stored fields known and layout-known
   - ABI size/alignment validated by metadata accessor
   - copy/destroy trivial OR explicitly handled
   - runtime round-trip test passing

## Resolved design questions

Short rationale for the non-obvious decisions, for future-maintainer
context. Full reviewer history was pruned from this doc in a
post-Phase-2 cleanup.

- **Q: Why not migrate remaining identity cleanups via
  `[TypeForwardedTo]`?** The hand-rolls (Data, URL, AnyError, …) were
  moved into `SwiftBindings.Apple` directly; a broader TypeForwardedTo
  migration would still require `Runtime` to depend on `Apple` (cycle;
  violates runtime SDK-agnosticism) and cannot rename types. Revisit at
  a deliberate major-version cleanup.
- **Q: Why default to VWT-backed opaque storage?** `frozen=true` is
  necessary but not sufficient for sequential layout. Memory-corruption
  risk from a bad frozen-bit is too high to trust a single flag;
  sequential layout is per-type whitelist after validation.
- **Q: Why a third metadata artifact instead of embedding in Runtime
  or supplement?** Avoids a cycle. Generator consumes it for type
  resolution; supplement build consumes it to emit carriers. Shared
  input keeps the two in sync without either depending on the other.
- **Q: Why `>=26.0.0` open-ended ranges instead of `[26.0,27.0)`?**
  Cross-major additive-only means iOS 26 consumer references still
  resolve under iOS 27 supplement. Open-ended ranges let a graph mixing
  26- and 27-built consumers unify at the higher version. A bounded
  range would force lockstep upgrades.
- **Q: Why single package instead of per-module
  (`SwiftBindings.Apple.Foundation`, `.ManagedSettings`, …)?**
  Per-module creates diamond dependency hell (Translation on
  `Apple.Foundation 26.0` + WeatherKit on `26.1`), a cascading
  release matrix per Apple SDK train, and repeats Microsoft's
  `System.*` mistake. Operationally infeasible for a small team.
- **Q: Why not per-consumer auto-stub (Option B — each consumer
  generates its own copy)?** Breaks type identity. Translation's
  `Foundation.Locale.Language` and ProximityReader's would be different
  CLR types, cannot pass between frameworks, cannot share protocol
  conformance caches.
- **Q: How do non-Apple Swift packages work?** The `TypeOwnerRegistry`
  module-default lookup routes Apple modules → `SwiftBindings.Apple`
  and third-party Swift modules → their own generated binding package.
  Non-Apple consumers don't pay the Apple supplement tax.
- **Q: Monolithic vs multi-assembly inside the package (Option D')?**
  Monolithic first. Split to per-module assemblies only if size, trim,
  or assembly-identity hygiene becomes a concrete pain point. Splitting
  later requires forwarders, doable but not free.
- **Q: Availability / weak-linking?** Generated metadata accessors
  must fail gracefully (or be platform-guarded) when running on an OS
  version that lacks the symbol.
- **Q: Cross-module protocol conformances?** Type ownership is
  module-local, but conformance ownership may not be. A type from
  module A may conform to a protocol from module B. Resolver handles
  this.
- **Q: Typealias representation?** `ApplicationToken = Token<Application>`
  emitted as alias/projection metadata, NOT duplicate type identity.
- **Q: Legal/licensing for shipping generated metadata derived from
  Apple SDKs?** Risk low; ADPLA §7.5 library carve-out applies. See
  [`src/legal/RATIONALE.md`](../../legal/RATIONALE.md) for the full
  rationale and pre-publish checks.
- **Q: Why does `@cdecl` support the opaque-transport model?** Swift
  formally rejects structs/protocol existentials from C-compatible
  signatures. Opaque/VWT transport at the Swift→C boundary is the
  correct model, not C struct projection.
