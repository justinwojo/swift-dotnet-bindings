# Apple Swift-Only Types: Architecture Design

> **Status:** Decided after review (2026-04-14). Implementation pending.
> **Owner:** Justin Wojciechowski
> **Created:** 2026-04-14
> **Context:** Discovered while validating Session 1 of `ship-blockers-round2-plan.md`.
> 6 of 7 target Apple frameworks (Translation, ProximityReader,
> LiveCommunicationKit, FamilyControls, WeatherKit, CryptoKit) fail to compile
> because they reference Swift-only types that have no managed binding.

## Decision summary (post-review)

After two review rounds by Grok and Codex, the resolved approach is:

1. **Adopt Option D**: ONE supplement package `SwiftBindings.Apple`,
   versioned per Apple SDK train (Xcode SDK major). Internal CLR namespacing
   per Swift module (`Swift.Foundation.*`, `Swift.ManagedSettings.*`, etc.).
2. **Supplement contains only NEWLY-generated Apple Swift-only types.** It
   explicitly EXCLUDES the existing legacy Runtime-owned canonical types
   (`Foundation.Date`, `Foundation.Data`, `Foundation.URL`,
   `Foundation.Decimal`, `Foundation.Measurement<T>`, `Foundation.AnyError`,
   `ManagedSettings.Token<T>`, `SwiftUI.Text`). Those stay in
   `SwiftBindings.Runtime` indefinitely.
   - **Reason:** `[TypeForwardedTo]` migration would require
     `SwiftBindings.Runtime` to reference `SwiftBindings.Apple` (cycle /
     violates runtime SDK-agnosticism), and forwarding cannot rename types,
     so namespace migration breaks consumers. Revisit at a deliberate
     major-version cleanup, not now.
3. **VWT-backed opaque storage is the default for ALL supplement types.**
   `[StructLayout(Sequential)]` is only emitted via explicit per-type
   whitelist after metadata size/alignment verification and runtime
   round-trip validation. `frozen=true` in ABI JSON is necessary but not
   sufficient — memory corruption risk is too high to trust a single flag.
4. **Authoritative metadata lives in a third artifact**, not Runtime, not
   solely in the supplement. Ship it embedded under `SwiftBindings.Sdk/tools/`
   for 0.8 (one NuGet coordination surface instead of two). Design the
   manifest format so it can later be extracted into a standalone data
   package if needed. Both the generator and the supplement build consume
   the same manifest.
5. **Package-version invariant (explicit):**
   - **Package major = Apple SDK train major** (e.g., iOS 26 →
     `26.x.x`, iOS 27 → `27.x.x`). One package release per Apple SDK
     train. Minor and patch are package-internal — see the "Versioning
     for consumers" section below for the full semantics.
   - **Cross-major additive-only.** Every new major is a strict
     superset of every prior major's public surface. Types may be
     added; members may be added; items may be deprecated via
     attributes but NOT removed or renamed, across any number of
     majors. Deprecated items get shims, not drops.
   - **Consumer ranges are open-ended** (`>=26.0.0`) and safe under
     this invariant. A graph that mixes an iOS 26–built consumer with
     an iOS 27–built consumer unifies at the higher supplement; the
     additive-only commitment guarantees the iOS 26 consumer's type
     references still resolve.
   - **When an invariant violation is unavoidable** (extraordinarily
     rare — e.g., Apple performs a resilience-boundary reshuffle), we
     ship the break under a new package NAME
     (`SwiftBindings.Apple.v2`), NOT as a breaking-major of
     `SwiftBindings.Apple`. Consumers migrate explicitly with source
     changes. This keeps `>=26.0.0` semantically honest.
   - **v2 coexistence rule.** `SwiftBindings.Apple` and
     `SwiftBindings.Apple.v2` are allowed to coexist in the same
     graph without fracturing type identity because they live under
     **different CLR namespace roots** (e.g., `Swift.Foundation.*` vs
     `Swift.Foundation.V2.*`). They therefore expose **non-overlapping
     CLR types**, so no duplicate-definition or identity-split error
     is possible at the assembly level. A consumer built against v1
     keeps using v1 types; a consumer built against v2 uses v2 types.
     There is no migration "for free" — source code must change when
     a framework author adopts v2.
   - **Runtime ABI compatibility is the user's responsibility.** If a
     graph mixes a v1-built consumer with a v2-built consumer on an
     SDK that v1 can no longer call correctly, that is the same class
     of problem as running a binary built against an older iOS SDK on
     a newer one — we surface it via a compile-time `TypeOwnerRegistry`
     error when the registry detects both v1 and v2 as owners for the
     same Swift identity, and via documented migration guidance. We
     do NOT attempt to enforce mutual exclusion with MSBuild/NuGet
     targets; the namespace split is the enforcement.
6. **Type ownership via `TypeOwnerRegistry`**, with per-type override
   precedence:
   1. **Per-type owner overrides first** (e.g., `Foundation.Date` →
      `SwiftBindings.Runtime` regardless of module default). Legacy
      canonical types are registered here.
   2. **Module default second**: Apple Swift modules → `SwiftBindings.Apple`;
      third-party Swift modules → their generated binding package.
   3. **Same-module-being-generated → local**.

   Consumer pulls the supplement only if it actually references a type the
   registry resolves to `SwiftBindings.Apple`.
7. **Monolithic single assembly first.** Multi-assembly-inside-package
   (D') deferred until concrete need (size, trim, identity hygiene).
8. **Demand-driven prototyping mode shares canonical identity.** When the
   SDK emits a supplement project into `obj/` for prototyping, that project
   must be referenced as a project dependency in the consumer's build graph,
   NOT compiled as duplicate types into each consumer assembly. Otherwise
   identity fractures (see rejected Option B).
9. **CI must validate** supplement metadata against live SDK symbols
   (metadata accessor exists, size/alignment match, VWT copy/destroy smoke,
   optional/container round-trip).
10. **Framework linkage blast radius must be validated.** A single Apple
    supplement that touches every Apple framework's symbols may force-link
    unused frameworks under NativeAOT. Smoke test before committing.
11. **Limits of the package-level guarantee.** The single-package model
    eliminates per-module supplement diamond/version-skew risk. It does
    NOT eliminate all NuGet graph conflicts — exact-version pins, stale
    top-level overrides, private feeds, and lock files can still break
    restore. These are general NuGet hazards, not specific to this design.

The full analysis, options considered, and rejected paths remain below
for context. Where this Decision summary contradicts anything below, the
Decision summary is authoritative.

## Status (2026-04-17): Phase 2 shipped; M11b remaining

Phase 2 landed all architectural machinery described above across 7
commits on `apple-nuget-rework` (`41d00b1e` → `70b37ea0`). The
single-package supplement, `TypeOwnerRegistry` with 6-level resolver,
VWT-backed opaque storage, manifest pipeline, cross-module identity
test, live-SDK CI validation (`ValidateAppleTypesManifest`),
demand-driven prototyping with canonical project-reference identity,
and framework-linkage blast-radius smoke are all in place. See
`src/docs/phase-2-session-plan.md` for the per-session breakdown and
commit SHAs.

The gap between "architecture complete" and "problem statement
resolved" is **framework bootstrapping**. The Problem statement says
this design exists to unblock 6 of 7 target frameworks; currently
only ProximityReader is demonstrably unblocked (14 AnyTypeFallback
skips → 0). CryptoKit has manifest entries but was not re-verified.
Translation has its types in the manifest but was not re-verified.
FamilyControls, LiveCommunicationKit, WeatherKit, and TipKit remain
deferred to M11b with blockers that range from "never inventoried"
to "generator hangs at 99% CPU".

Two other items from the doc are also outstanding: `Foundation.Data.Payload`
(Appendix A "Needs investigation") was never investigated, and
CryptoKit's "SwiftHandle gap (Fix F)" annotation was not followed
up on this phase.

### M11b plan

**Track 1 — M11b-recon + quick wins (one session).** Goal: accurate
classification of every remaining blocker *and* clearing the items
that only need verification. Concrete scope:

1. Re-verify CryptoKit and Translation end-to-end. Their manifest
   entries exist (P256/P384/P521 from Session 2; Locale.Language,
   Locale.Region, Locale.Currency from Sessions 2 and 7). Regenerate;
   confirm SB0001 counts drop for previously-skipped Swift-only-type
   members. If they don't, diagnose.
2. Inventory FamilyControls' own Swift-only surface (beyond the
   ManagedSettings indirection already covered by the manifest) and
   add any missing entries.
3. Reproduce each hard blocker once and capture the exact failure
   signature (concrete error text, stack/CPU sample where relevant).
   Classify — no speculative fixes:
   - **TipKit**: specific generator error.
   - **LiveCommunicationKit**: is it the Appendix A generic-param
     leak (`T`, `TT1`, `TT2`, `TT3`) or something else?
   - **WeatherKit**: CPU sample during the 99% hang; classify as
     runaway generic resolution vs ABI-parser loop vs other.
4. Investigate `Foundation.Data.Payload` (Appendix A "Needs
   investigation") — confirm real type vs false positive vs renamed.
5. Investigate CryptoKit's "SwiftHandle gap (Fix F)" annotation from
   Appendix A; determine whether it's already addressed or still open.

Exit: one commit with the quick-win coverage + a concise classification
document (`src/docs/m11b-recon.md`) naming the blocker class for each
still-deferred framework. No speculative fixes in this session.

**Track 2 — M11b-finish (one or two sessions, scope decided by recon).**
Execute on whatever recon found. Likely shape:

- If WeatherKit + LiveCommunicationKit both trace back to the
  generic-param emitter bug (Appendix A), fix the emitter — one
  scoped session unblocks both simultaneously. This is an emitter
  change, not a supplement change; runs through generator gates
  carefully.
- Framework-specific remaining blockers get targeted fixes.
- Exit: all 7 target frameworks regenerate cleanly, SB0001 down to
  permanent-limit floors, gates green, validation baseline updated.

### Non-code tracks (parallel, user-side)

- **Q10 item 5 (legal/licensing check)** on shipping generated
  metadata derived from Apple SDKs. Resolved: see
  [`licensing-analysis.md`](./licensing-analysis.md) for the full
  analysis (risk 2/5) and the 10-item pre-publish checklist. Doesn't
  block M11b; checklist must be executed before the first
  `nuget.org` publish.
- **Phase 3 publishing** (`SwiftBindings.Apple 26.0.0` nupkg to
  nuget.org + swift-dotnet-packages commit). Only meaningful after
  M11b lands and the supplement covers the full 7-framework target
  list.

### Rationale for the recon/finish split

Session 7 tried to inventory, diagnose, and fix simultaneously, hit
auto-compact at 1h 35m, and had to rebuild state. Splitting recon
from execution bounds recon to one commit with no speculative fixes,
and gives execution accurate scope informed by concrete error
signatures rather than labels like "unclassified".

## Versioning for consumers

**One-sentence rule:** if your app targets iOS 26, use
`SwiftBindings.Apple >=26.0.0`. If it targets iOS 27, use
`SwiftBindings.Apple >=27.0.0`. That's it.

### What the three digits mean

| Digit | What it tracks | Consumer impact |
|---|---|---|
| **Major** (e.g., `26`) | Apple SDK train (iOS 26, iOS 27, …) | Pick the major ≥ your app's minimum iOS target. Newer majors are always a strict superset — safe to upgrade across majors. |
| **Minor** (e.g., `26.1.0`) | Package-internal: new types supplemented, new framework added, new generator capability | Always safe to upgrade. Not coupled to Apple's iOS minor cadence. |
| **Patch** (e.g., `26.0.1`) | Package-internal: bug fixes within the same coverage surface | Always safe to upgrade. |

### Why minor/patch are NOT coupled to Apple's minor/patch

We deliberately decouple because:

- **Package bug fixes happen on our cadence, not Apple's.** If Apple
  doesn't ship an iOS 26.1, we still need to release 26.0.1 for
  package bug fixes. Coupling would force us to skip version numbers
  or manufacture bumps.
- **Not every Apple minor adds Swift-only types.** If iOS 26.1 has
  zero new Swift-only types, a minor bump would be arbitrary under
  the coupled scheme. Under our scheme, we only bump minor when
  coverage genuinely expands.
- **Release notes, not version digits, tell consumers "covers
  iOS 26.2 types."** The version number answers "is it safe to
  upgrade?"; the changelog answers "what's new?". Separating these
  concerns keeps the version number a clean signal.

### What consumers never need to worry about

- **Matching the supplement version to the SDK version they built
  against.** The supplement's ABI metadata resolves Swift entry
  points at runtime via `dlsym`; `@available(iOS N, *)` gating in
  Apple's SDK is the runtime safety net. If a consumer builds
  against iOS 26.0 SDK and pulls `SwiftBindings.Apple 26.2.0`, the
  supplement may describe types that don't exist on iOS 26.0 — but
  that's only a runtime issue if the consumer *calls* a
  new-in-26.2 type on a 26.0 device, and Apple's availability
  attributes would already flag that at build time.
- **Cross-major mixes within a graph.** A graph containing one
  consumer built against `SwiftBindings.Apple 26.x` and another
  built against `27.x` resolves to the higher version (NuGet's
  default rule). The cross-major additive-only invariant (Decision
  summary item 5) guarantees the 26.x consumer's type references
  still resolve under `27.x`.

### First ship

`SwiftBindings.Apple 26.0.0`. Built against iOS 26.2 SDK, covering
the manifest types listed in the Status section. Coverage bumps ship
as `26.1.0`, `26.2.0`, etc. Bug fixes ship as `26.0.1`. iOS 27 SDK
train ships as `27.0.0` (still additive).

### Decoupling from `SwiftBindings.Runtime` / `SwiftBindings.Sdk` versions

`SwiftBindings.Runtime` and `SwiftBindings.Sdk` version on generator
cadence (currently `0.8.x`). `SwiftBindings.Apple` versions on Apple
SDK train (currently `26.x.x`). These are semantically unrelated and
MUST NOT share a version stamp.

**Tooling gap (blocks first `26.0.0` publish):**
`build/Helpers/VersionScope.cs` currently stamps all four csproj
files (Runtime, SDK, Templates, Apple) from a single `--version`
argument. Before the first `SwiftBindings.Apple 26.0.0` publish, the
stamp must be split so `nuke pack --version 0.8.0 --apple-version
26.0.0` (or equivalent) sets each package independently. This is a
small change local to `VersionScope.cs` and `Build.Pack.cs`, tracked
in the pre-publish checklist of
[`licensing-analysis.md`](./licensing-analysis.md).

## Problem statement

Apple's modern iOS frameworks increasingly expose pure-Swift value types that
have no Objective-C bridge:

- `Foundation.Locale.Language`, `Foundation.Locale.Region` (iOS 16+)
- `Foundation.Locale.Currency`
- `Foundation.Measurement<UnitType>`
- `ManagedSettings.{Application, WebDomain, ActivityCategory}` (iOS 16+)
- `CryptoKit.{P256, P384, P521}.Signing.ECDSASignature`
- ... and growing every iOS version

Microsoft's .NET iOS workload only ships **Objective-C-bridged** Foundation
bindings (`NSDate`, `NSLocale`, etc.). It does NOT bind Swift-only value
types because they have no `@objc` surface to bridge against.

Result: when the Swift Bindings generator emits C# for a framework like
`Translation` whose primary API returns `[Foundation.Locale.Language]`, the
emitted code references `Foundation.LocaleLanguage` (or similar) — a type
that **does not exist** in any package the consumer can reference.

This blocks 6 of the 7 target frameworks for SDK 0.8.0 ship-readiness.

## Why hand-rolling won't scale

Today, high-value Swift-only Foundation types live as **hand-rolled C#
bindings** inside `SwiftBindings.Runtime`:

| Type | Location |
|---|---|
| `Foundation.Date` | `Swift.Runtime/src/Swift/Date.cs` |
| `Foundation.Data` | `Swift.Runtime/src/Swift/Data.cs` |
| `Foundation.URL` | `Swift.Runtime/src/Swift/URL.cs` |
| `Foundation.Decimal` | `Swift.Runtime/src/Swift/Decimal.cs` |
| `Foundation.Measurement<T>` | `Swift.Runtime/src/Swift/Measurement.cs` |
| `Foundation.AnyError` | `Swift.Runtime/src/Swift/AnyError.cs` |
| `ManagedSettings.Token<T>` | `Swift.Runtime/src/Swift/Token.cs` |
| `SwiftUI.Text` | `Swift.Runtime/src/Swift/SwiftUI/Text.cs` |

This was a pragmatic bootstrap — it worked for the first batch of frameworks.
But it doesn't scale:

- **Whack-a-mole.** Every iOS version adds Swift-only types. Every new
  framework drag-in surfaces more. Without automation, we will fall behind.
- **Manual ABI work per type.** Each hand-roll requires deep ABI knowledge
  (VWT, mangled symbols, frozen vs resilient, generic metadata, conformance
  descriptors). Error-prone, tedious, hard to review.
- **Maintenance burden.** When Apple bumps an ABI (rare, but happens at
  resilience boundaries), all hand-rolls require audit.
- **Source of truth split.** Type identity is hand-coded in Runtime; type
  database in Generator says "this maps to that name." Easy to drift, as
  evidenced by Fix A (`ee2e9598`) which added DB entries pointing to
  `Foundation.LocaleLanguage` — a name that no one ever created.

The generator already has Apple's full ABI JSON for every Swift module.
Mechanical emission is solved. The architectural question is **where the
emitted types should live** and **how downstream packages reference them**.

## Constraints

These are non-negotiable inputs, not options to revisit:

1. **NuGet packages must be `SwiftBindings.*`.** `Swift.*` is reserved by
   Microsoft.
2. **CLR namespaces are flexible.** `Swift.*` is acceptable as a CLR
   namespace (precedent: `Swift.Runtime` namespace lives in
   `SwiftBindings.Runtime` package).
3. **No NuGet package management hell.** Diamond dependencies, version skew
   matrix explosions, cascading lockstep upgrades — these are explicit
   anti-goals. Microsoft's early `System.*` per-area package experiment is
   a cautionary tale we will not repeat.
4. **Small-team maintainable.** All code is AI-maintained on behalf of a
   non-coding owner. Architecture must minimize ongoing manual coordination.
5. **Type identity must be canonical.** If two consumer frameworks both
   reference `Foundation.Locale.Language`, they must see the **same** CLR
   `Type` so values flow between them. This forecloses naive "generate per
   consumer" approaches.

## Options considered

### Option A: Continue hand-rolling

Add `Locale.Language`, `Locale.Region`, `ECDSASignature`, etc. to
`Swift.Runtime/src/Swift/` by hand.

**Pros:**
- Zero architectural change.
- Continues established pattern.
- Full control over ergonomics per type.

**Cons:**
- Doesn't scale (see above).
- Couples `SwiftBindings.Runtime` version to iOS SDK version.
- Pollutes runtime for non-Apple consumers (e.g., binding a third-party
  Linux Swift package via `SwiftBindings.Runtime` still pulls all Apple
  Swift types).
- Manual labor per type, every iOS version.

**Verdict:** The bootstrap path. Not viable as the long-term answer.

---

### Option B: Per-consumer auto-stub

When generator detects an unresolved Swift-only type referenced by a
consumer framework, emit a minimal C# stub for it inside that consumer's
generated output.

**Pros:**
- Fully automatic; no central package needed.
- Self-contained per consumer; no version coordination.

**Cons:**
- **Breaks type identity.** Translation's `Foundation.Locale.Language` and
  ProximityReader's `Foundation.Locale.Language` would be different CLR
  types in different assemblies. They cannot be passed between frameworks,
  cannot be used as generic args interchangeably, cannot share protocol
  conformance caches.
- Duplicate emission across packages → larger consumer assemblies.
- Forecloses any future ergonomic overlay (you'd have N copies to update).

**Verdict:** Trap. Both Grok and Codex independently flagged this as wrong.
Eliminates the hard work of central identity by giving up identity itself.
Not viable.

---

### Option C: Per-Apple-module supplemental packages

One NuGet package per Apple Swift module:
`SwiftBindings.Apple.Foundation`, `SwiftBindings.Apple.ManagedSettings`,
`SwiftBindings.Apple.CryptoKit`, ... etc.

Each package generated mechanically from that module's ABI JSON. Consumer
frameworks add `PackageReference` to whichever supplements they need.

**Pros:**
- Matches Apple's source organization (one Apple module = one package).
- Granular dependency: consumers pull only what they reference.
- Per-module versioning matches Apple's per-module deprecation model.

**Cons (the killers):**
- **Diamond dependency conflicts.** Consumer app uses
  `SwiftBindings.Translation` (depends on `Apple.Foundation 26.0`) AND
  `SwiftBindings.WeatherKit` (depends on `Apple.Foundation 26.1`). NuGet
  picks highest; if 26.1 ABI-broke 26.0, build fails.
- **Cascading release matrix.** Each Apple SDK train = N supplement
  releases × M framework releases. CI pipeline owns coordination
  forever.
- **Lockstep upgrade pressure.** Bump iOS 26→27? Every supplement
  releases, every framework releases pinning new majors, every consumer
  migrates. Forget one, transitive resolution breaks.
- **First-mover lockin.** Once `SwiftBindings.Apple.Foundation 1.0`
  ships, every breaking change is a tax.
- **Small-team killer.** Coordinating ~10+ package releases per Apple
  SDK train with breakage detection, version pinning, and consumer
  migration is a full-time release-engineering job we do not have.
- **Repeats Microsoft's `System.*` mistake.** Early .NET Core fragmented
  System into per-area packages. Became unmanageable. Consolidated.

**Verdict:** Architecturally clean on paper. Operationally infeasible for a
small project. Codex and Grok both favored this; both underweighted the
NuGet management cost.

---

### Option D: Single supplemental package per Apple SDK train (selected)

ONE NuGet package: `SwiftBindings.Apple`, containing newly-generated
Apple Swift-only types. Legacy canonical types (Date, URL, etc.) stay in
`SwiftBindings.Runtime` — the supplement does NOT re-emit them.

Versioned per Apple SDK train (Xcode SDK major). Consumer frameworks
reference the supplement with an open-ended range (`>=26.0.0`). Internal
CLR namespacing splits per Swift module:

```
namespace Swift.Foundation;
public partial struct Locale { public partial struct Language { ... } }

namespace Swift.ManagedSettings;
public partial struct Application { ... }

namespace Swift.CryptoKit.P256.Signing;
public partial struct ECDSASignature { ... }
```

**Pros:**
- **Eliminates per-module supplement diamond/version-skew risk.** One
  package = one version per consumer. Cannot partially-upgrade the
  supplement across Apple modules. (Does not eliminate all NuGet graph
  conflicts — see Decision summary item 11.)
- **Versioning matches Apple's release model.** Apple ships per-module
  source but RELEASES per-SDK-train. No partial state ("Foundation 18.1 +
  ManagedSettings 17.0" is impossible). Our packaging matches reality.
- **Single release per Apple SDK train.** One supplement to build,
  validate, publish, document. Tractable for a small team.
- **Bounded size.** Even at ~200 Swift-only types across all Apple
  frameworks, compiled assembly likely <1 MB. Negligible deployment cost.
- **Runtime stays generic.** `SwiftBindings.Runtime` keeps interop
  infrastructure (VWT, SafeHandle, marshalling) PLUS its existing
  canonical types. Non-Apple consumers don't pay the Apple-supplement tax.
- **Type identity automatic.** All consumers reference the same supplement
  → same CLR types → cross-package values flow naturally.
- **Microsoft's `System.*` lesson absorbed.** They consolidated for
  exactly this reason.

**Cons:**
- All-or-nothing dependency: pulling supplement gets all newly-generated
  Apple types. Minor in practice given bounded size.
- A genuine Apple-side ABI break cannot be absorbed by a normal
  version bump — the supplement commits to cross-major additive-only,
  so the break ships under a new package name
  (`SwiftBindings.Apple.v2`) with its own CLR namespace root.
  Consumers opt in by migrating source. See Decision summary item 5.
- Two sources of Apple-type identity (legacy Runtime types + new
  supplement types). `TypeOwnerRegistry` with per-type overrides handles
  this cleanly; see Decision summary item 6.

**Verdict:** Best fit for our constraints. Selected path.

---

### Option D': Single package, multiple assemblies inside

Variant of D. ONE NuGet package, but internally ships multiple `.dll`
files (one per Apple Swift module, or grouped by frequency-of-use).

```
SwiftBindings.Apple.26.0.0.nupkg
├── lib/net10.0-ios26.2/
│   ├── SwiftBindings.Apple.Foundation.dll
│   ├── SwiftBindings.Apple.ManagedSettings.dll
│   ├── SwiftBindings.Apple.CryptoKit.dll
│   └── ...
```

**Additional pros over D:**
- Per-assembly type identity preserved → enables future `[TypeForwardedTo]`
  splits if a single supplement ever grows too large.
- AOT trimming can drop unused module assemblies.
- Consumer frameworks declare assembly references precisely.

**Additional cons:**
- More complex build/pack story.
- Framework-specific csproj must add `<Reference Include>` per Apple
  module, OR depend on a "metapackage-within-package" assembly that
  re-exports all.
- May be premature optimization until we hit the size or trim problem.

**Verdict:** Worth doing if NuGet tooling supports it cleanly. Not a
dealbreaker either way. Falls out of D naturally if we structure the
generator output as per-module-assembly from day one.

---

### Option E: Bundle into the SDK (`SwiftBindings.Sdk`)

Make the Apple Swift types part of the MSBuild SDK package. SDK injects
the reference automatically during `dotnet build`. Consumer never declares
the dependency explicitly.

**Pros:**
- Even simpler than D for end users — invisible.
- Single package to coordinate.

**Cons:**
- SDK becomes the bottleneck for adding any new Apple Swift type. Consumer
  cannot add a missing type without an SDK release.
- SDK version = iOS SDK version coupling. Breaks SDK versioning semantics
  (today the SDK version tracks generator features, not iOS SDK).
- Conflates "tool that generates bindings" with "data about Apple types."
  Two different release cadences, two different concerns.

**Verdict:** Tempting for simplicity, wrong for separation of concerns.
Reject.

---

### Option F: Inject types via SDK at consumer build time (no separate package)

The SDK's MSBuild targets generate Apple Swift type stubs into the
consumer's `obj/` folder, compiled into the consumer assembly. No package
involved.

**Pros:**
- No NuGet coordination at all.
- Always in sync with SDK.

**Cons:**
- **Same type identity problem as Option B.** Each consumer assembly has
  its own copy → different CLR types → cannot share values.
- Could be solved with `[TypeForwardedTo]` to a canonical assembly — but
  then we need a canonical assembly, which means we're back to Option D.

**Verdict:** Reduces to D once you fix the identity problem. Not its own
option.

---

## Implementation specifics

The following items supplement the Decision summary at the top. Where
they overlap, the Decision summary is authoritative.

1. **Package name:** `SwiftBindings.Apple`.
2. **Versioning:** Package major = Apple SDK train major (iOS 26 →
   `26.x.x`, iOS 27 → `27.x.x`). Minor/patch for within-train
   additions. Supplement commits to cross-major additive-only (see
   Decision summary item 5), so consumers use open-ended `>=26.0.0`
   ranges without upper bounds. Genuine Apple ABI breaks are handled
   by shipping under a new package name, not by bumping this package's
   major past its additive guarantee. See "Versioning for consumers"
   below for the consumer-facing mental model.
3. **CLR namespaces:** `Swift.Foundation.*`, `Swift.ManagedSettings.*`,
   `Swift.CryptoKit.*`, etc. Mirror Apple's module organization.
4. **Generation modes:**
   - **Demand-driven** (prototyping): SDK detects unresolved Swift-only
     types, emits a canonical supplement project into `obj/` referenced
     as a project dependency from the consumer (NOT compiled as duplicate
     types into each consumer assembly — identity must stay canonical).
   - **Pre-built** (shipping): single supplement package built per Xcode
     SDK release, published to NuGet, consumed transitively via
     `TypeOwnerRegistry`-driven references.
5. **Type DB refactor:** Split `TypeRecord` into three concepts:
   - **Swift identity** (e.g., `Foundation.Locale.Language`)
   - **Managed projection** (the type a consumer's public surface uses;
     may be `global::Foundation.NSDate` for Date, may be supplement type
     for Locale.Language)
   - **ABI carrier** (the C# type used to copy/destroy/pass values
     safely across the Swift→C boundary)
6. **Cross-module type identity test:** Build two consumer assemblies
   referencing the supplement, instantiate a Swift-only type in one, pass
   to the other, assert `typeof(T)` matches. Permanent regression
   guardrail.
7. **Resolver order** (formalize):
   ```
   Swift type → resolve via:
     1. Per-type owner override in TypeOwnerRegistry
        (e.g., Foundation.Date → SwiftBindings.Runtime)
     2. Swift stdlib known type (from runtime)
     3. ObjC workload type / projection (e.g. NSDate for NSLocale)
     4. Module-default supplement lookup
        (Apple module → SwiftBindings.Apple; third-party module → its
         generated binding package)
     5. Same-module type being generated
     6. Unsupported (skip member)
   ```
8. **Storage strategy:** VWT-backed opaque by default. See Decision
   summary item 3.

## Resolved questions (post-review)

Reviewed by Grok and Codex on 2026-04-14. Where they disagreed, the
answer below adopts the more conservative / operationally safer
position (usually Codex's).

### Q1 (resolved): Single-package-per-Apple-SDK-train avoids `System.*` hell

Yes, for the per-module failure mode that matters most. One package =
one version per consumer → no per-module diamond deps possible.

With open-ended ranges (`>=26.0.0`), iOS 26-built and iOS 27-built
consumers can coexist in a single app. Resolver picks highest; the
**cross-major additive-only** commitment (Decision summary item 5) is
what makes that unification safe — every newer supplement major is a
strict superset of every older major's public surface.

Caveats the single-package model does NOT fix:

- App-level `PackageReference` can override transitives — dangerous if
  users pin stale supplement.
- Exact-version pins, private feeds, and `packages.lock.json` can still
  break restore. General NuGet hazards, not specific to this design.
- Time-skew across frameworks built against different supplement
  majors/minors does NOT cause conflicts under open-ended ranges
  (that's the point), but still requires us to hold the cross-major
  additive-only commitment.
- Version per Apple SDK *train* (Xcode SDK), not strictly "iOS SDK,"
  since `net10.0-ios`, `-maccatalyst`, `-tvos`, `-macos` may diverge.

### Q2 (resolved): `[TypeForwardedTo]` migration — DO NOT do it now

Both reviewers said the mechanism works on iOS workload + NativeAOT.
But Codex raised two killers:

1. Type forwarding requires the OLD assembly to reference the NEW
   assembly. So `SwiftBindings.Runtime` would have to depend on
   `SwiftBindings.Apple`, which violates "runtime stays SDK-agnostic"
   and creates a cycle risk.
2. Forwarding preserves type identity; it does NOT rename. If we move
   `Swift.URL` → `Swift.Foundation.URL`, old binaries and source break.

**Decision:** Existing hand-rolls stay in `SwiftBindings.Runtime`
indefinitely. Supplement contains only NEW Swift-only types. Revisit
migration at a deliberate major-version cleanup, not now.

### Q3 (resolved): Multi-assembly inside single package — defer

Ship monolithic single assembly first (Option D). Split into per-module
assemblies (Option D') only if size, trim, or assembly-identity hygiene
becomes a concrete pain point. Splitting later requires forwarders,
which is doable but not free.

### Q4 (resolved): Scaling

No real concern at 200 or 1,000 types. At ~10,000, NativeAOT generic
instantiation costs become visible. Mitigations (per Codex): minimal
ABI carriers only, split source files by module, lazy metadata access,
no eager static constructors, mark AOT/trim compatibility. Ergonomic
overlays only for high-value types, not blanket.

### Q5 (resolved): Non-Apple Swift packages

Codex's `TypeOwnerRegistry` model with per-type override precedence
(see Decision summary item 6). Resolution is dependency-driven, not
unconditional:

1. **Per-type owner override first.** Legacy canonical types
   (`Foundation.Date`, `Foundation.URL`, `Foundation.Decimal`, etc.)
   are pinned to `SwiftBindings.Runtime` regardless of their Swift
   module. This prevents the supplement from shadowing canonical
   identity.
2. **Module default second.** Apple Swift modules →
   `SwiftBindings.Apple`; third-party Swift modules (Stripe,
   Alamofire, etc.) → their generated binding package.
3. **Same-module-being-generated → local.**

A consumer pulls `SwiftBindings.Apple` only if it actually references a
type the registry resolves to it. Pure third-party bindings stay free
of the Apple supplement. NuGet has no peer-dependencies concept
(npm-style), so this is implemented via standard transitive
`PackageReference`.

### Q6 (resolved): Naming

`SwiftBindings.Apple`. Plain, clear, leaves room for namespace splits
inside.

### Q7 (resolved): Generator bootstrap

Authoritative metadata lives in a THIRD artifact, not Runtime, not
solely in the supplement. Pipeline:

```
Apple Xcode SDK ABI JSON  +  mapping rules
   ↓
Apple metadata manifest  (third artifact)
   ↓                          ↓
Generator TypeDatabase    SwiftBindings.Apple source generation
```

**Bootstrap location (decided):** manifest is embedded under
`SwiftBindings.Sdk/tools/` for 0.8 — one NuGet coordination surface
rather than two. Design the manifest schema so it can later be extracted
into a standalone `SwiftBindings.Apple.Metadata` data package without
changing its on-disk format, but defer that split until the generator
and supplement both need the manifest in contexts the SDK is absent.

XML DB files in `SwiftBindings.Runtime` stay as-is for already-migrated
hand-rolled types. New Apple Swift-only types get their metadata from
the manifest, not new XML DB entries.

### Q8 (resolved): Frozen-vs-resilient detection

Codex's "necessary but not sufficient" rule. Default storage is
**VWT-backed opaque** for ALL Apple Swift-only types. Sequential layout
emission requires ALL of these to be true per type:

- `frozen=true` in ABI JSON
- non-generic (or fully layout-known generic instantiation)
- all stored fields known and layout-known
- ABI size/alignment validated by metadata accessor
- copy/destroy semantics trivial OR explicitly handled
- runtime round-trip test passes for the type

Sequential layout is added per-type via whitelist after validation, not
by default. Memory corruption risk is too high to trust a single flag.

### Q9 (resolved): Existing hand-rolls

Stay in `SwiftBindings.Runtime`. See Q2. They keep their rich overlays
(Date↔DateTime, URL↔Uri, etc.). New supplement types get minimal ABI
carriers; ergonomic overlays optional and per-type.

### Q10 (resolved): Items both reviewers added

Codex flagged seven items the design should account for:

1. **Availability / weak-linking.** Generated metadata accessors must
   fail gracefully (or be platform-guarded) when running on an OS
   version that lacks the symbol.
2. **Framework linkage blast radius.** A monolithic Apple supplement
   that touches every Apple framework's symbols may force the iOS
   linker to bring in every framework even for apps that only use one.
   MUST validate before shipping. Mitigations: lazy P/Invoke, per-module
   conditional symbols, runtime probing.
3. **Cross-module protocol conformances.** Type ownership is module-local,
   but conformance ownership may not be. A type from module A may
   conform to a protocol from module B. Resolver must handle.
4. **Typealias representation.** `ApplicationToken = Token<Application>`
   etc. should be alias/projection metadata, NOT duplicate type
   identity.
5. **Legal/licensing.** Shipping generated API/type metadata derived
   from Apple SDKs is probably fine (analogous to existing bindings),
   but worth a deliberate check before publishing. **Resolved** —
   see [`licensing-analysis.md`](./licensing-analysis.md) for full
   analysis (risk 2/5, ADPLA §7.5 library carve-out applies) and the
   10-item pre-publish checklist.
6. **CI validation against live SDK.** Smoke test that supplement
   metadata accessor symbols exist, size/alignment match, VWT
   copy/destroy works, optional/container round-trip works. Catches
   drift between manifest and shipped Apple SDK.
7. **`@cdecl` limitations support the design.** Swift formally rejects
   structs/protocol existentials from C-compatible signatures. So
   opaque/VWT transport at the Swift→C boundary is the correct model,
   not C struct projection. Aligns with our existing approach.

Grok added one guardrail: a permanent integration test where two
consumer frameworks pass a `Locale.Language` value between them and
assert reference equality on the CLR `Type`.

## Disagreements between reviewers (and how resolved)

| Question | Grok | Codex | Adopted |
|---|---|---|---|
| Migrate existing hand-rolls now? | Yes, do it | No, breaks runtime separation | **Codex** — too operationally risky |
| Frozen detection | Trust ABI JSON 100% | Necessary but not sufficient | **Codex** — corruption risk |
| Multi-assembly inside package | Premature, never | Worth it for identity hygiene | **Defer** — monolith first, split if natural |
| Bootstrap metadata | Just use ABI JSON | Need third manifest artifact | **Codex** — avoids cycle |
| Version ranges | `[26.0,27.0)` | `>=26.0.0` open-ended | **Codex** — NuGet best practice |
| Non-Apple consumer model | Implicit transitive | Formal `TypeOwnerRegistry` | **Codex** — generalizes cleanly |
| Hand-roll overlay strategy | Move overlays into supplement | Keep overlays where the canonical type lives | **Codex** — couples to Q2 decision |

## Decision criteria

Whatever we ship must satisfy:

1. **Eliminates per-module supplement diamond/version-skew risk.** Two
   frameworks targeting the same Apple SDK train cannot conflict on
   supplement version (one supplement = one version per consumer).
   Does not claim to eliminate all NuGet graph conflicts — see
   Decision summary item 11.
2. **One release per Apple SDK train.** Coordinating supplement +
   framework releases must be tractable for a part-time small team.
3. **Type identity preserved.** Cross-framework value flow works.
4. **Runtime stays SDK-version-agnostic.** Non-Apple consumers don't pay
   Apple-types cost.
5. **Backward compatible for existing 0.8.0 consumers.** Legacy
   canonical types (Date, URL, Decimal, Measurement<T>, Token<T>, etc.)
   keep their current assembly, namespace, and surface in
   `SwiftBindings.Runtime`. No `[TypeForwardedTo]` migration now; see
   Q2 for why.

## Appendix A: Discovered Swift-only types (incomplete)

Types that are referenced by the 7 target frameworks but have no managed
binding today. Discovered via `BuildAppleFramework` against
swift-dotnet-packages on 2026-04-14.

| Swift Identity | Used By | Notes |
|---|---|---|
| `Foundation.Locale.Language` | Translation, ProximityReader | Resilient struct |
| `Foundation.Locale.Region` | Translation, ProximityReader | Resilient struct |
| `Foundation.Locale.Currency` | (existing in valueTypes, members skipped) | Resilient struct |
| `Foundation.Data.Payload` | WeatherKit (?) | Needs investigation |
| `ManagedSettings.Application` | FamilyControls | Token<Application>-marker pattern |
| `ManagedSettings.WebDomain` | FamilyControls | Same |
| `ManagedSettings.ActivityCategory` | FamilyControls | Same |
| `CryptoKit.P256.Signing.ECDSASignature` | CryptoKit | Has SwiftHandle gap (Fix F) |
| `CryptoKit.P384.Signing.ECDSASignature` | CryptoKit | Same |
| `CryptoKit.P521.Signing.ECDSASignature` | CryptoKit | Same |

Plus an orthogonal generator bug: generic parameters `T`, `TT1`, `TT2`,
`TT3` leaking as unresolved type names in LiveCommunicationKit and
WeatherKit. NOT a Swift-only-type issue — separate emitter bug.

## Appendix B: External input received

Two rounds of review:

**Round 1** — initial architecture sketch:
- **Grok:** Favored single-package-with-future-split. Pre-generation up
  front. `Swift.Foundation` namespace.
- **Codex:** Favored per-Apple-module supplemental packages. Demand-driven
  generation. VWT-backed opaque storage default. Type DB
  identity/projection/carrier split.

Both rejected per-consumer auto-stub (Option B) on type identity grounds.

**Round 2** — after this doc was written and proposed
single-package-per-Apple-SDK-train explicitly:
- **Grok:** Endorsed Option D as written. "Ship it." Trusted ABI JSON,
  recommended migrating existing hand-rolls via `[TypeForwardedTo]`.
- **Codex:** Endorsed Option D over their original per-module
  recommendation, citing release management cost. Pushed back on
  several operational details (see "Disagreements" table above).

The resolved decisions in this doc adopt the more conservative of each
disagreement, generally Codex's, on the basis that operational safety
beats architectural elegance for a small AI-maintained team.
