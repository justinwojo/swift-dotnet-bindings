# SwiftUI Bridge Validation — Sessions 1-6

**Date**: March–April 2026

## Motivation

Stress-test the SwiftUI bridge against real-world SwiftUI-first libraries to measure coverage rate, gate distribution, and end-to-end viability.

## Libraries Validated

| Library | Stars | Why Selected |
|---------|-------|-------------|
| **CodeScanner** (twostraws) | ~1,200 | Simplest real-world SwiftUI view — one view, one closure |
| **AlertToast** (elai950) | ~2,400 | View modifier bridging, enum params |
| **RichTextKit** (danielsaidi) | ~1,250 | Richest SwiftUI surface (27 views), massive MAUI gap |
| **WhatsNewKit** (SvenTiigi) | ~4,300 | Model-driven view, struct/class param bridging |
| **YouTubePlayerKit** (SvenTiigi) | ~960 | Player + config pattern, state observation |

Libraries considered and deferred: Pow, Exyte Chat, VComponents, Pulse, MarkdownUI, ConfettiSwiftUI, BottomSheet.

## Final Bridge Rate: 78.1%

| Library | Views | Bridged | Rate |
|---------|-------|---------|------|
| CodeScanner | 1 | 0 | 0% |
| AlertToast | 2 | 1 | 50% |
| RichTextKit | 27 | 23 | **85%** |
| WhatsNewKit | 1 | 0 | 0% |
| YouTubePlayerKit | 1 | 1 | **100%** |
| **Total** | **32** | **25** | **78.1%** |

## Success Criteria

| Metric | Target | Actual | Status |
|--------|--------|--------|--------|
| Views detected | 100% | 32 views | **Met** |
| Functional bridge rate | >60% | 78.1% | **Exceeded** |
| Gate failures classified | 100% | 100% | **Met** |
| Runtime rendering | >80% | 100% (16/16) | **Exceeded** |
| Zero regressions | Baseline unchanged | 95/95 validation | **Met** |

## Session Summary

### Session 1: Fetch, Generate, Analyze (commit 8277638)

- Added all 5 libraries to validation pipeline
- Initial bridge rate: 62.5% (20/32 views)
- Full gate failure analysis: Binding<T> (#1 gap), Array<T> (#2), non-frozen structs (#3)

### Session 2: Binding<T>, Array<T>, SwiftUI.Image gates (commit 97e2dced)

Added three parameter gates to `InitAnalyzer.cs`:
- **Binding<T>**: Unwraps inner type, supports Primitive/String/BoundEnum. Wrapper passes `$state.name`.
- **Array<T>**: Maps element type. Crosses ABI as `UnsafePointer<T>? + count`, C# pins via `GCHandle`.
- **SwiftUI.Image**: Bridges as String (SF Symbol name), wrapper constructs `Image(systemName:)`.

No views fully unblocked (each has multiple blockers), but infrastructure in place. Key finding: fallthrough regression pattern — gates must return null for unsupported inner types to allow `MapDatabaseType` fallback.

25 new unit tests.

### Session 3: Non-raw-value enum + BoundStruct closures (commit 782469c3)

- Non-raw-value enums bridged as BoundStruct (opaque pointer ABI)
- BoundStruct in closure args: heap-allocated via `UnsafeMutableRawPointer.allocate`, C# uses `SwiftMarshal.MarshalFromSwift<T>`
- Bridge rate: 62.5% → **78.1%** (+15.6pp)
- 5 views flipped: Button, ActionButton, Menu, RichTextExportMenu, RichTextShareMenu (all RichTextKit)

9 new unit tests.

### Session 4: Runtime Validation (commit 535fd6f1)

Created 5 synthetic SwiftUI views replicating validation library parameter patterns. All 8 runtime tests pass on iOS Simulator. Bug fix: multi-string `fixed` statement syntax.

10 new tests (2 unit + 8 runtime).

### Session 5: Foundation.Decimal TypeDatabase (commit a37d5d4f)

Added `Foundation.Decimal` to `FoundationDatabase.xml`. Mapped as non-frozen struct with `objcBridgeable="true"`. 2 unit tests.

### Post-Session Fixes

- **Co-gater FindOpeningBrace** (commit 86f819ca): Handle `where` constraints between method declarations and opening braces. 2 unit tests.
- **Code review fixes** (commit 34ecb89a): BoundStruct closure leak guard, Decimal ObjCBridgeable mapping, Binding<Image> rejection, array pinning inside try block. 5 unit tests.
- **Additional runtime tests** (commit f0e95ea8): BindingToggleView, NumberListView, SymbolIconView — 8 runtime tests.

### Session 6: Result<T,E> closures + Optional<ExternalClass> (commit e9011908)

- **Result<T,E> closures**: Decomposes `(Result<S,F>) -> Void` into `onSuccess(S)` + `onError(F)` callbacks. Swift wrapper switches on `.success`/`.failure`.
- **Optional<ExternalClass>**: Added `AVCaptureDevice`/`AVCaptureSession` to AVFoundationDatabase. Propagated `IsObjCBridgeable` flag on Class BridgeParameters.

19 new unit tests, 5 new runtime tests.

## Remaining Template Views (Architectural)

All 7 remaining template views are blocked by architectural limitations tracked in `src/docs/roadmap.md` (Hard/Deferred section):

| View | Library | Blocker | Roadmap Item |
|------|---------|---------|-------------|
| Picker | RichTextKit | WritableKeyPath | SwiftUI WritableKeyPath bridging |
| Toggle | RichTextKit | WritableKeyPath + generic closure | SwiftUI WritableKeyPath bridging |
| NSMutableParagraphStyleValueLabel | RichTextKit | Generic type param on View | SwiftUI generic type params on Views |
| RichTextEditor | RichTextKit | Existential protocol in closure | SwiftUI existential protocol params |
| CodeScannerView | CodeScanner | Array<AVMetadataObject.ObjectType> | Array<T> with external type elements |
| AlertToast | AlertToast | Enum with associated values | Non-raw-value enum (partial) |
| WhatsNewView | WhatsNewKit | Existential protocol composition | SwiftUI existential protocol params |
