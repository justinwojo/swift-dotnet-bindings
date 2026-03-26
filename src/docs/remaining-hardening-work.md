# Remaining Hardening Work

**Created**: March 26, 2026
**Source**: Items not delivered during the hardening sessions (`src/docs/Completed/hardening-sessions.md`).

---

## Easy Wins (unblocked, straightforward)

| # | Item | Origin | Effort | Notes |
|---|------|--------|--------|-------|
| 1 | Binding report as MSBuild warnings | Session 5 Item 1 | Easy | `binding-report.json` already generated. Add target in `Sdk.targets` that reads JSON, emits `<Warning>` per skip category. Follow existing SWIFTBIND0xx pattern. |
| 2 | Bulk retain/release helpers | Session 5 Item 2 | Easy | Add `RetainMultiple`/`ReleaseMultiple` batch loop helpers to Arc. Consider `SuppressGCTransition`. |
| 3 | `pack-all.sh` orchestration | Session 5 Item 4 | Easy | Multi-package build+pack in dependency order (Runtime → Sdk → Templates). Adapt topological sort from `validate-libraries.sh`. |

## Medium (known fix path, needs careful implementation)

| # | Item | Origin | Effort | Notes |
|---|------|--------|--------|-------|
| 4 | Variadic init data retention | Session 1 Bug 4 | Medium | Root cause: `@_cdecl` can't wrap Swift variadic params (`Int...`). ABI JSON represents as `Array<Int>`. Fix path: explicit `swift_retain` before `CallConvSwift` dispatch for `@owned` array params. 1 skipped test. |
| 5 | Existential container ref param marshalling | Session 1 Bug 1 | Medium | `[SkipOnDevice]` — may work on simulator. Root cause: container layout or calling convention mismatch in generated P/Invoke. Needs P/Invoke signature audit vs @_cdecl wrapper. 2 skipped tests. |
| 6 | Static protocol constructors | Session 5 Item 3 | Medium | Protocol `init(...)` → static `Create()` factory on conforming types. Needs witness table init entry lookup. |
| 7 | BindingTests bridge via `--compile-bridge-only` | Session 5 Item 5 | Medium | Replace `build-bridge.sh` with CLI path. Complexity: handle `SwiftUIBridgeTestHelpers.swift`, update NativeReference from .framework to .xcframework, update DllImport library name. |
| 8 | Non-Int32 enum raw values | Session 4 Feature 4 | Medium | ABI JSON lacks raw values. `.swiftinterface` may contain assignments — needs investigation. If not present, truly blocked. 1 skipped test. |

## Hard (significant marshalling/runtime changes)

| # | Item | Origin | Effort | Notes |
|---|------|--------|--------|-------|
| 9 | SwiftString.Buffer ABI decomposition | Session 1 Bug 2 | Hard | 4 `SwiftString.Buffer` structs exceed 8 GPR slots on ARM64. Fix: decompose `Buffer` into individual `nint` pairs in P/Invoke. Significant marshalling change. 1 skipped test. |

## Blocked (needs new infrastructure)

| # | Item | Origin | Blocker |
|---|------|--------|---------|
| 10 | Protocol descriptor pointers for `SwiftArray<ExistentialContainer>` | Session 1 Bug 6 | Requires runtime infrastructure to resolve Swift protocol descriptors and pass them when constructing existential containers inside arrays. 5 skipped tests. |

## Low Priority / Not Worth Revisiting

| Item | Origin | Why |
|------|--------|-----|
| Cross-framework `using` directives | Session 4 Feature 5 | Resolved as not-needed — generator uses fully-qualified names. |
| Presentation helper tests (`PresentAsSheet(IntPtr.Zero)`) | Session 7 Item 9 | May need real UIViewController. Low value. |
| Multi-level protocol hierarchy BindingTests source | Session 2 | Covered by real-world library validation (Alamofire, Kingfisher, GRDB). |
