# Simulator Test Consolidated Bug Report

**Date**: 2026-04-04
**Repo**: `swift-dotnet-packages` (test execution), `swift-bindings` (this report)
**SDK Version**: SwiftBindings.Sdk 0.5.0
**Runtime**: .NET 10.0-ios / Mono JIT / iOS 26.2 Simulator (arm64)

## Summary

| Library | Pass | Fail | Skip | Total | Status |
|---------|------|------|------|-------|--------|
| BlinkID | 305 | 0 | 0 | 305 | PASS |
| BlinkIDUX | 39 | 1 | 0 | 40* | CRASH |
| Mappedin | 257 | 0 | 0 | 257 | PASS |
| GRDB | 243 | 0 | 4 | 247 | PASS |
| Kingfisher | 243 | 5 | 0 | 248 | FAIL |
| Lottie | 89 | 0 | 0 | 89 | PASS |
| Nuke | 76 | 0 | 1 | 77 | PASS |
| Stripe | 299 | 0 | 0 | 299 | PASS** |
| **Total** | **1551** | **6** | **5** | **1562** | |

\* BlinkIDUX crashed after 40 assertions (Phase 5 of 15); ~116 additional tests never ran.
\*\* Stripe passed all tests but had a post-test SIGSEGV during teardown (no tests affected).

**Pass rate**: 99.6% of executed, non-skipped tests (1551/1557). 6 unique defects across 2 libraries.
**Coverage loss**: ~116 BlinkIDUX tests never executed due to app crash (Phases 6-15 unreachable).

---

## Detailed Failures

### BUG-1: BlinkIDUX App Crash — Missing Resource Bundle [CRITICAL]

- **Library**: BlinkIDUX
- **Impact**: ~116 tests never executed (Phases 6-15 unreachable)
- **Error**:
  ```
  BlinkIDUX/resource_bundle_accessor.swift:44: Fatal error: unable to find bundle named BlinkIDUX_BlinkIDUX
  ```
- **When**: App crashes immediately after Phase 5 completes, when the first Phase 6 test triggers loading of the BlinkIDUX Swift wrapper module.
- **Root cause**: The BlinkIDUX xcframework includes an SPM-generated `resource_bundle_accessor.swift` that expects a `BlinkIDUX_BlinkIDUX.bundle` resource bundle to be present in the app bundle. When the wrapper module initializes (triggered by any P/Invoke into the wrapper dylib), this accessor runs and crashes because the bundle is missing from the sim test app.
- **Category**: Build/packaging issue
- **Prior state**: Session 2 (commit `881263c`) completed all 15 phases with 156 assertions (122 pass, 33 fail, 1 skip). The 32 DllNotFoundException failures were from the *BlinkID* wrapper (SWIFTBIND051), not BlinkIDUX. The current crash is a regression — likely the wrapper build now triggers resource bundle initialization that it previously did not.
- **Build log context**: BlinkID wrapper compilation fails (SWIFTBIND051), but BlinkIDUX wrapper compiles. The crash occurs in the BlinkIDUX wrapper's module initialization path.
- **Fix direction**: Either (a) strip `resource_bundle_accessor.swift` from wrapper compilation inputs, (b) add the resource bundle to the sim test app's build, or (c) make the wrapper's module initializer resilient to missing bundles.

### BUG-2: BlinkIDUX CaptureMode Type Metadata Size = 0

- **Library**: BlinkIDUX
- **Test**: `CaptureMode metadata`
- **Error**: `size is 0` (expected non-zero type metadata size)
- **Category**: Generator bug — CaptureMode type metadata not properly emitted
- **Likely cause**: The generator emits metadata for CaptureMode but the runtime size resolves to 0. This may indicate the type is an opaque/incomplete type that the generator should skip or handle differently. Needs inspection of the generated binding for CaptureMode and comparison with similar enum/struct classification.
- **Impact**: 1 test

### BUG-3: Kingfisher ImagePrefetcher Constructor ARC Crash

- **Library**: Kingfisher
- **Tests**:
  - `ImagePrefetcher_ctor_empty` — root cause
  - `ImagePrefetcher_MaxConcurrentDownloads` — blocked by constructor crash
  - `ImagePrefetcher_MaxConcurrentDownloads_set` — blocked by constructor crash
  - `ImagePrefetcher_Stop` — blocked by constructor crash
- **Error**: Native crash in `swift_retain` during constructor wrapper execution
- **Category**: Generator bug — @_cdecl wrapper has ARC over-retain or under-retain
- **Likely cause**: The generated @_cdecl constructor wrapper for `ImagePrefetcher` likely performs an incorrect retain/release sequence or has a parameter marshalling issue. When the constructor is called via P/Invoke, `swift_retain` receives a corrupt or deallocated pointer, causing a SIGSEGV. This is the same crash signature class as ARC wrapper bugs previously found in other libraries (e.g., ObjC-bridged thunk ARC issues fixed in `afa1d661`), though the exact defect needs confirmation via wrapper code inspection.
- **Impact**: 4 tests (1 root failure + 3 blocked)
- **Fix direction**: Audit the ImagePrefetcher constructor wrapper for retain-after-move or double-retain patterns.

### BUG-4: Kingfisher ImageCache Closure Constructor Crash

- **Library**: Kingfisher
- **Test**: `ImageCache_ctor_name_url`
- **Error**: Native crash in `swift_cvw_initWithCopyImpl` during closure marshalling
- **Category**: Generator bug — closure-parameter constructor wrapper has incorrect closure value witness marshalling
- **Likely cause**: The `ImageCache(name:cacheDirectoryURL:)` constructor takes a closure parameter (`@escaping () -> URL`). The generated wrapper crashes in `swift_cvw_initWithCopyImpl`, which is the closure value witness copy function. This likely indicates the closure's context or function pointer is incorrectly marshalled from C# to Swift, though the exact defect (escaping closure lifetime, indirect passing convention, or value witness layout) needs confirmation via generated wrapper inspection.
- **Impact**: 1 test
- **Fix direction**: Investigate closure-parameter constructor wrappers for incorrect value witness initialization. May be related to the `ExistentialContainer` layout for closure types.

---

## Skips (Not Bugs)

### SKIP-1: GRDB DatabaseSnapshotPool — Requires WAL Data (4 tests)

- **Tests**: `DatabaseSnapshotPool(path)`, `DatabaseSnapshotPool.Path`, `DatabaseSnapshotPool(path, config)`, `DatabaseSnapshotPool(path, config, ext)`
- **Reason**: Creating a `DatabaseSnapshotPool` requires an existing SQLite database in WAL mode. The tests cannot create one because `Database.execute()` is not bound (closure-based API).
- **Category**: Known limitation — API gap (no closure-based method binding)

### SKIP-2: Nuke ImageRequest AddValue — No NSUrlRequest Constructor (1 test)

- **Test**: `N1 AddValue`
- **Reason**: `ImageRequest` has no `NSUrlRequest` constructor binding. The test needs to construct an `ImageRequest` from a URL request object, which requires a binding that doesn't exist.
- **Category**: Known limitation — API gap (ObjC bridged type constructor not bound)

---

## Post-Test Crashes (No Tests Affected)

### Stripe Teardown SIGSEGV

- **Error**: SIGSEGV in `class_getInstanceMethod` called from `xamarin_is_user_type` during `NSObject.Disposer.Drain`
- **Stacktrace**:
  ```
  NSObject.Disposer.Drain → NSObject.ReleaseManagedRef → 
  Runtime.TryGetIsUserType → Runtime.SlowIsUserType → 
  xamarin_is_user_type → class_getInstanceMethod → SIGSEGV
  ```
- **Mono assertion**: `condition '!ji->async' not met` at `jit-info.c:918`
- **Context**: Occurs after all 299 tests pass and "TEST SUCCESS" is printed. The crash happens during object disposal on the main thread's run loop, when Mono tries to determine the ObjC type of an object being released.
- **Category**: Likely upstream Mono JIT / Xamarin ObjC interop bug. The crash stack is entirely within the .NET runtime's ObjC bridging layer, though earlier object lifetime corruption from generated code surfacing during teardown cannot be fully ruled out without deeper investigation.
- **Impact**: Zero tests affected. The app exits cleanly from a test perspective.

---

## Failure Patterns Across Libraries

### Pattern 1: @_cdecl Wrapper ARC Bugs (Generator)

Manifests as `swift_retain` or `swift_release` crashes in constructor or method wrappers. The generated @_cdecl thunk performs incorrect ARC operations on parameters or return values.

**Affected**: Kingfisher (ImagePrefetcher constructor)
**Previously fixed instances**: `afa1d661` (ObjC-bridged thunk ARC), `19560c96` (Optional<Class> retain leak)

### Pattern 2: Closure Parameter Marshalling (Generator)

Closures passed from C# to Swift crash during value witness operations (`swift_cvw_initWithCopyImpl`). The closure's context, function pointer, or existential container layout is incorrect.

**Affected**: Kingfisher (ImageCache constructor with URL closure)
**Related skips**: GRDB (DatabaseSnapshotPool needs closure-based execute)

### Pattern 3: Constructor Wrappers as a Hotspot (Generator)

Both Kingfisher failures are in constructor wrappers — one ARC/lifetime issue, one closure marshalling issue. Constructors that combine escaping closures, reference types, or ObjC/Swift bridging appear to be disproportionately risky wrapper shapes. This suggests targeted constructor wrapper audits may have high yield.

**Affected**: Kingfisher (ImagePrefetcher constructor, ImageCache closure constructor)

### Pattern 4: Resource Bundle Dependencies (Build)

Swift libraries with SPM resource bundles crash at module initialization when the bundle isn't present in the app. The wrapper inherits the resource bundle accessor from the original package.

**Affected**: BlinkIDUX (resource_bundle_accessor.swift crash)

---

## Priority Ranking

| Priority | Bug | Tests Blocked | Fix Complexity | Notes |
|----------|-----|--------------|----------------|-------|
| P0 | BUG-1: BlinkIDUX resource bundle crash | ~116 | Medium | Build/packaging fix; may need SDK changes |
| P1 | BUG-3: ImagePrefetcher ARC crash | 4 | Medium | Same class as previously fixed ARC bugs |
| P1 | BUG-4: ImageCache closure constructor crash | 1 | Hard | Closure marshalling — cross-cutting pattern |
| P2 | BUG-2: CaptureMode metadata size=0 | 1 | Easy | Likely a type classification issue |
| P3 | Stripe teardown SIGSEGV | 0 | N/A | Upstream Mono bug — cosmetic only |

### Fix Impact Analysis

- **Fixing BUG-1** would unblock ~116 BlinkIDUX tests and bring the BlinkIDUX test suite to its Session 2 baseline (122 pass, 33 fail, 1 skip). Some of the newly-reachable tests may still fail due to DllNotFoundException (SWIFTBIND051 — BlinkID wrapper compilation failure).
- **Fixing BUG-3** would recover 4 Kingfisher tests and bring Kingfisher to 247/248 pass.
- **Fixing BUG-4** would recover the last Kingfisher test and achieve 248/248 pass (100%).
- **Fixing closure marshalling (BUG-4 pattern)** broadly would also unblock the 4 GRDB skipped tests and potentially enable new tests for closure-heavy APIs across all libraries.

---

## Comparison to Prior Session Results

| Library | Session Result | Current Run | Delta |
|---------|---------------|-------------|-------|
| BlinkID (S1) | 305/0/0 | 305/0/0 | No change |
| BlinkIDUX (S2) | 122/33/1 (156 total) | 39/1/0 (40 total, crash) | Regression: app crash blocks 116 tests |
| Mappedin (S3) | 257/0/0 | 257/0/0 | No change |
| GRDB (S4) | 243/0/4 | 243/0/4 | No change |
| Kingfisher (S5) | 243/5/0 | 243/5/0 | No change |
| Lottie (pre-existing) | N/A | 89/0/0 | Baseline established |
| Nuke (pre-existing) | N/A | 76/0/1 | Baseline established |
| Stripe (pre-existing) | N/A | 299/0/0 | Baseline established |

**Key regression**: BlinkIDUX — from 156 executed tests to 40. The resource bundle crash is new. All other libraries are stable.

---

## Recommendations

1. **Immediate**: Fix BUG-1 (BlinkIDUX resource bundle) to restore test coverage. This is a build/packaging issue, not a generator logic bug, so the fix should be isolated.

2. **Short-term**: Fix BUG-3 (ImagePrefetcher ARC) as it follows the same pattern as previously-fixed ARC bugs. The fix approach from `afa1d661` should apply.

3. **Medium-term**: Investigate and fix closure-parameter constructor marshalling (BUG-4). This is a cross-cutting issue that affects any constructor taking an `@escaping` closure, blocking tests across GRDB and Kingfisher.

4. **File upstream**: The Stripe teardown SIGSEGV should be reported to the .NET team as a potential Mono JIT ObjC interop regression. Include the stack trace with `xamarin_is_user_type` → `class_getInstanceMethod` → SIGSEGV.
