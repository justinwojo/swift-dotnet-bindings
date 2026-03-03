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

## Phase 2: Error Message Audit

**What**: When the generator skips a method, type, or entire library, does the user understand what happened and what they can do about it?

**Why**: The skip reporting infrastructure exists (SB0001-SB0003, skip reasons in summary output), but users see it as build output, not as guidance. A cryptic "SB0003: non-dispatchable" doesn't help someone decide if the library is still usable.

**Checklist**:
- [ ] Audit all SWIFTBIND error codes (SDK) — are messages actionable?
- [ ] Audit SB0001/SB0002/SB0003 skip reasons — do they explain *what's unsupported* and *whether the rest of the library still works*?
- [ ] Generator summary output — does it clearly say "X of Y methods were generated, Z were skipped because [reasons]"?
- [ ] Fatal errors (generator crash, missing ABI JSON, empty module) — do they produce a clear message or a stack trace?
- [ ] Wrapper compilation failures — when the Swift wrapper doesn't compile, does the user see why or just "build failed"?

**Deliverable**: Error message improvements. Goal: no user-facing stack traces, every skip has a human-readable reason.

---

## Phase 3: Documentation

**What**: README, getting started guide, known limitations, and troubleshooting — updated and honest.

**Why**: The current docs (`docs/` wiki pages) were written at various stages and some are stale. The README is good but doesn't cover limitations or the "what doesn't work yet" story.

**Checklist**:
- [ ] **README.md** — add "Current Status" section: what works well, what's known-unsupported, how to report gaps. Be direct: "This is preview-quality software. Here's what 40+ libraries have validated. Here's what will fail."
- [ ] **docs/Getting-Started.md** — verify it matches current CLI + SDK workflow. Test from scratch.
- [ ] **docs/Known-Limitations.md** — update with current generator limitations (PAT protocols, typed throws, associated types, string enum raw values, UnsafePointer). Explain each in user terms, not implementation terms.
- [ ] **docs/Supported-Features.md** — update feature matrix to match current state (classes, structs, enums, protocols, closures, generics, async, existentials, protocol extensions, SwiftUI bridge)
- [ ] **docs/Troubleshooting.md** — verify SWIFTBIND error codes are current. Add common failure patterns: "my library has 0 types" (missing BUILD_LIBRARY_FOR_DISTRIBUTION), "wrapper compilation failed" (internal types, #if compiler guards)
- [ ] **CONTRIBUTING.md** — architecture overview, how to run tests, how to add a validation library, issue/PR templates (production readiness item H)

**Deliverable**: Docs that a new user can follow without reading source code.

---

## Phase 4: Validation Refresh

**What**: After F4+F6 land, run a targeted assessment of the libraries that were weakest or most affected.

**Why**: Not another scoring exercise. We want to confirm the practical impact: did collection dispatch actually unblock the methods users need? Did safety work make Dispose patterns reliable?

**Checklist**:
- [ ] **Workflow assessment v3** — pick 2-3 libraries most affected by F4 (ones with proxy methods returning collections). Walk through end-to-end usage.
- [ ] **Full library validation** — `./validate-libraries.sh --tier all` to confirm no regressions
- [ ] **Runtime test suite** — verify new runtime tests from F4/F6 pass on simulator
- [ ] Spot-check: pick one library NOT in the test set, generate bindings, confirm they compile and basic calls work

**Deliverable**: Updated workflow assessment confirming practical usability.

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
- [ ] Update `UrlFormat` in SB diagnostic attributes (SB0001–SB0004) — currently point to `malinicr/swift-bindings`, need to match the public repo URL

**Deliverable**: Repo is clean and ready for public eyes.

---

## Sequencing

```
NOW (parallel with F4/F6):
  Phase 3: Documentation        ── can start immediately, no code dependency

AFTER F4 + F6:
  Phase 1: Cold-start walkthrough
  Phase 2: Error message audit
  Phase 4: Validation refresh

AFTER phases 1-4:
  Phase 5: Release packaging
  Phase 6: Pre-launch cleanup
```

Phase 3 (docs) can start now — it doesn't depend on any code changes. Everything else chains off F4+F6 completing.

---

## What This Roadmap Does NOT Cover

- SwiftUI bridge remaining sessions (constrained generics, lifecycle, observable binding) — valuable but not blocking release
- Binding review v5 scoring — the scoring system wasn't giving reliable signal; workflow assessments are the better measure
- Library-specific patches (Session 10) — do these if the validation refresh in Phase 4 surfaces specific gaps, not preemptively
- Future vision items (ObjC integration, multi-platform, benchmarks) — post-launch
