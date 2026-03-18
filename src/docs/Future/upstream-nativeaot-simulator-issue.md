# Upstream Issue Draft: NativeAOT Simulator Support for iOS

> **Consolidated into `upstream-bug-reports-draft.md` (Issue 5).** This file is kept for reference but the canonical version is in the main draft.

_Draft for filing on [dotnet/runtime](https://github.com/dotnet/runtime)_

---

## Title

**[iOS] NativeAOT publish does not support `iossimulator-arm64` runtime identifier**

## Labels

`area-NativeAOT-iOS`, `enhancement`

## Body

### Description

`dotnet publish` with `PublishAot=true` fails when targeting `iossimulator-arm64`. NativeAOT for iOS only supports the `ios-arm64` (device) runtime identifier. This forces simulator builds to use the Mono JIT runtime.

### Motivation

We maintain a Swift/.NET interop project that generates C# bindings from compiled Swift libraries. The Mono JIT runtime has three known issues with `CallConvSwift` P/Invoke that are definitively resolved under NativeAOT:

1. **JIT assertion crash** (`jit-info.c:918`) — Mono incorrectly marks `CallConvSwift` P/Invoke frames as async, then hits `!ji->async` assertion during stack unwinding. Process-fatal, bypasses all managed exception handlers.
2. **Non-blittable type rejection** — Complex types in `CallConvSwift` signatures throw `InvalidProgramException`.
3. **SafeHandle lifetime in async** — GC collects SafeHandles during async suspension points with `CallConvSwift`.

All three issues are Mono-specific and do not reproduce under NativeAOT (verified on physical iPhone with `ios-arm64`). However, the lack of simulator NativeAOT support means:

- **Development iteration** requires Mono (simulator) — developers see workaround overhead and JIT-risk warnings during development
- **CI/CD pipelines** without physical devices can only test on Mono, not on the same runtime that ships to production
- **Two runtime paths** must be maintained and tested — the Mono path with workarounds and the NativeAOT path without

### Current Behavior

```bash
# Device (works)
dotnet publish -c Release -r ios-arm64 -p:PublishAot=true
# → Success: NativeAOT binary

# Simulator (fails)
dotnet publish -c Release -r iossimulator-arm64 -p:PublishAot=true
# → Error: NativeAOT does not support iossimulator-arm64
```

### Expected Behavior

`dotnet publish -r iossimulator-arm64 -p:PublishAot=true` should produce a NativeAOT-compiled app runnable on the iOS Simulator.

Both `ios-arm64` and `iossimulator-arm64` target the same ARM64 architecture on Apple Silicon Macs. The primary difference is the target triple (`arm64-apple-ios` vs `arm64-apple-ios-simulator`) and the SDK used for linking (`iphoneos` vs `iphonesimulator`).

### Impact

For projects using Swift interop (`CallConvSwift`), this limitation creates a dual-path constraint:
- Workarounds (Swift wrapper functions, Cdecl closure expansion, risk detection attributes) are needed for Mono but unnecessary on NativeAOT
- The workarounds are harmless on NativeAOT but add code size and complexity
- CI testing on simulators can't validate the production runtime path

### Related Issues

- #80905 — NativeAOT support for iOS/Mac Catalyst (delivered for `ios-arm64` in .NET 9)
- #108662 — Runtime support for Swift interop in .NET 10
- #93631 — Runtime support for Swift interop in .NET 9

### Environment

- .NET SDK: 10.0.100-preview.4
- Target: `net10.0-ios`
- Architecture: ARM64 (Apple Silicon)
- NativeAOT: Works for `ios-arm64`, not supported for `iossimulator-arm64`
