# Nuke LoadImage Regression (Resolved)

_Archived from `Future/mono-jit-mitigation-and-nuke-loadimage-regression.md`_

## Summary

The Nuke "cannot load image" issue was a **regression in call-path selection**, not an inherent runtime limitation.

## Root Cause

Generated Nuke bindings contained both:
1. A **wrapper-backed async path** (`ImageAsync(ImageRequest)`) using `SwiftBindings` async exports + `CallConvCdecl` callbacks — **working**
2. A **direct callback-based path** (`LoadImage(...)`) using `CallConvSwift` with closure marshalling — **hits Mono JIT crash**

In commit `c4fb3ca`, the NukeTestApp's `LoadImageAsync` helper was wired to the direct `LoadImage` callback path (the known-risk route) instead of the wrapper-backed `ImageAsync` path.

## Resolution (Phase I1/I1a)

- Switched Nuke test app to use wrapper-backed `ImageAsync` path
- Fixed BitwiseCopyable crash in Swift 6+ wrapper code
- Added 8 regression tests
- Nuke image loading confirmed working through wrapper path on iOS Simulator
