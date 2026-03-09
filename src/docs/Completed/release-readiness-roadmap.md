# Release Readiness Roadmap

**Date**: March 2, 2026
**Goal**: Confident public release — the tooling works, fails gracefully, and is honest about what's still in progress
**Prerequisite**: F4 (collection dispatch) + F6 (safety) complete

---

## Guiding Principle

We're not trying to look complete. We're trying to make sure that when someone shows up with library #41 (one we've never tested), they either succeed or get a clear explanation of why they didn't. The 40+ validation libraries prove breadth. This roadmap proves the experience.

---

## Phase 1: Cold-Start Walkthrough

**What**: Pretend you've never seen the project. Clone the repo, follow the README, generate bindings for a library not in the test set, and use them in an app.

**Why**: Every friction point in this flow is a doc gap or UX issue that will hit every new user.

**Checklist**:
- [ ] Clone → build from README instructions alone (no tribal knowledge)
- [ ] `dotnet new swift-binding` → drop in an xcframework → `dotnet build` → `dotnet pack`
- [ ] Consume the NuGet from a fresh .NET MAUI iOS app
- [ ] Call at least one method, verify it works on simulator
- [ ] Document every point where you had to guess, Google, or read source code

**Deliverable**: List of friction points → fix or document each one.

---

## Phase 2: Error Message Audit ✅

**Status**: Complete (March 2, 2026)

**What**: When the generator skips a method, type, or entire library, does the user understand what happened and what they can do about it?

**Why**: The skip reporting infrastructure exists (SB0001-SB0003, skip reasons in summary output), but users see it as build output, not as guidance. A cryptic "SB0003: non-dispatchable" doesn't help someone decide if the library is still usable.

**Checklist**:
- [x] Audit all SWIFTBIND error codes (SDK) — are messages actionable?
- [x] Audit SB0001/SB0002/SB0003 skip reasons — do they explain *what's unsupported* and *whether the rest of the library still works*?
- [x] Generator summary output — does it clearly say "X of Y methods were generated, Z were skipped because [reasons]"?
- [x] Fatal errors (generator crash, missing ABI JSON, empty module) — do they produce a clear message or a stack trace?
- [x] Wrapper compilation failures — when the Swift wrapper doesn't compile, does the user see why or just "build failed"?

**Changes made**:
- **A**: Top-level try-catch in `GenerateBindings()` — uncaught exceptions now produce `LogError` with message + `LogDebug` with stack trace (no user-facing stack traces)
- **B**: Console summary reassurance message — "Skipped items are excluded from C# output but don't affect the rest of the generated API"
- **C**: Human-readable skip reason descriptions — e.g., `UnsupportedExistential: 8 — protocol-typed parameter/return not yet projected`
- **D**: Wrapper stderr truncation increased 500→2000 chars, full stderr available via `--verbose 2`
- **E**: SWIFTBIND050 now includes common causes and Troubleshooting doc reference
- **F**: SWIFTBIND060 now includes actionable guidance (verify slices / build dependency separately)
- **G**: Troubleshooting.md — added SWIFTBIND050, 060, 070-073 to error code table
- **Tests**: 29 new test cases covering all changes

**Deliverable**: Error message improvements. Goal: no user-facing stack traces, every skip has a human-readable reason.

---

## Phase 3: Documentation ✅

**Status**: Complete (March 2, 2026)

All wiki pages and README updated across two sessions:
- **README.md** — current test counts (5,100+ unit, 240+ runtime), removed stale "In progress" safety item
- **docs/Getting-Started.md** — verified current, matches SDK + CLI workflow
- **docs/Known-Limitations.md** — added build requirements, generator limitations (typed throws, string enum raw values, UnsafePointer, optional existentials in closures, primitive generic constraints)
- **docs/Supported-Features.md** — updated library counts (40 libraries, 53 targets), fixed witness dispatch, property types, async naming
- **docs/Troubleshooting.md** — added SB0001–SB0004 diagnostic reference section
- **docs/Architecture.md** — updated target count, added history section
- **docs/SwiftUI-Interop.md** — added generic views, two-way state binding, view modifier chains; fixed stale "not supported" list
- **docs/How-Bindings-Map.md** — new page with 11 Swift→C# mapping examples
- **docs/NativeAOT-Deployment.md** — added SB0003/SB0004, fixed links
- **CONTRIBUTING.md** — deferred; README Contributing section covers current needs. Revisit post-launch when contributor traffic warrants a standalone doc.

---

## Phase 4: Validation Refresh ✅

**Status**: Complete (March 2, 2026)

All checks passed. F4 (collection dispatch) and F6 (safety hardening) landed without regressions. Practical impact confirmed across all validation tiers.

**Results**:

- [x] **Full library validation** — `./validate-libraries.sh --tier all`: **53/53 passed**, no regressions (32 tier-1 + 21 tier-2)
- [x] **Test suites** — Unit: 5,128 passed (0 failed, 1 skipped). Integration: 700 passed (0 failed, 11 skipped). Runtime unit: 247 passed (0 failed, 1 skipped). All green.
- [x] **Spot-check** — XMLCoder (tier-2, never manually assessed): generator produces 23,802 lines of C# across 73 type records. C# compiles clean. Swift wrapper has one failure on a complex generic method (`encode<T>` with multiple optional params) — known limitation, not a regression.
- [x] **Workflow assessment v2 confirmation** — 8/9 target libraries remain USABLE. 128 remaining SB0003 (down from 186, 31% eliminated across Sessions 1–4). Key unlocks: SmartCardIO `Transmit()`, Mappedin delegate callbacks, Nuke pipeline methods, BlinkIDUX constructors, Stripe payment callbacks.

**F4 impact**: Added `CollectionReturn` and `OptionalExistentialReturn` dispatch kinds to witness dispatch. Unblocked `IReadOnlyList<T>` and `IReadOnlyDictionary<K,V>` returns from protocol proxy methods (the 28 "InterfaceReturn" SB0003 category). 24 new tests.

**F6 impact**: Proxy finalizer leak detection (`~Finalizer()` warnings), SB0003 specific dispatch classification reasons (replacing generic "non-dispatchable"), SB1001 Roslyn analyzer for undisposed `ISwiftObject` locals. 14 new tests + 9 analyzer tests.

**Known pre-existing issues (not regressions)**:
- Mono JIT assertion crash in `ArrayMarshallingTests` on iOS Simulator (jit-info.c:918). NativeAOT unaffected.
- Swift wrapper compilation failures on complex generic methods (generic params + optional params + dictionary params). C# bindings are correct; wrapper can't express the full signature.

---

## Phase 5: Release Packaging

**What**: Make sure the actual artifacts (NuGet packages, project templates) are ready for consumption.

**Checklist**:
- [ ] `Swift.Runtime` NuGet package — version, description, license, README in package
- [ ] `Swift.Bindings.Sdk` NuGet package — same
- [ ] `dotnet new swift-binding` template — installs cleanly, produces working project
- [ ] Consumer smoke test (production readiness item K): template → build → pack → consume → call → works
- [ ] Package README / NuGet description — sets expectations correctly

**Deliverable**: Publishable NuGet packages.

---

## Phase 6: Pre-Launch Cleanup

**What**: Small items that should be done before the repo is public.

**Checklist**:
- [ ] ABI & module database versioning notes (production readiness item J)
- [ ] File upstream bug reports for Mono JIT issues (production readiness item L) — blocked on repo being public
- [ ] License file present and correct (MIT)
- [ ] No secrets, credentials, or internal paths in committed files
- [ ] CI pipeline (if applicable) — builds, tests, packs on clean checkout
- [ ] **GitHub release tagging + changelog workflow** — tag releases (e.g., `v0.1.0-preview.1`), create GitHub Releases with changelogs. Changelogs live in GitHub Releases (not wiki docs). Needs GitHub Actions workflow for: build → test → pack → publish NuGet → create GitHub Release with auto-generated or curated notes.
- [ ] Update `UrlFormat` in SB diagnostic attributes (SB0001–SB0004) — updated to `justinwojo/swift-dotnet-bindings`

**Deliverable**: Repo is clean and ready for public eyes.

---

## Sequencing

```
DONE:
  Phase 2: Error message audit  ✅
  Phase 3: Documentation        ✅
  Phase 4: Validation refresh   ✅

NOW:
  Phase 1: Cold-start walkthrough

AFTER phase 1:
  Phase 5: Release packaging
  Phase 6: Pre-launch cleanup
```

---

## What This Roadmap Does NOT Cover

- SwiftUI bridge remaining sessions (constrained generics, lifecycle, observable binding) — valuable but not blocking release
- Binding review v5 scoring — the scoring system wasn't giving reliable signal; workflow assessments are the better measure
- Library-specific patches (Session 10) — do these if the validation refresh in Phase 4 surfaces specific gaps, not preemptively
- Future vision items (ObjC integration, multi-platform, benchmarks) — post-launch
