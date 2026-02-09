# Mono JIT Mitigation and Nuke LoadImage Regression Notes

Date: 2026-02-09

## Scope

This note captures two related investigation threads:

1. Temporary mitigations for current Mono `CallConvSwift` JIT/runtime failures.
2. Whether Nuke image loading is currently blocked by an inherent runtime limitation or by a regression in call-path selection.

The goal is to support implementation planning and follow-up discussion.

---

## Thread A: Mono JIT issue - what we can do upstream now

### Confirmed current state

- The repo still documents and reproduces Mono assertion crashes at `jit-info.c:918` in `CallConvSwift` paths.
- Crash-prone categories are currently:
  - closure P/Invoke paths
  - `SwiftString`-related `CallConvSwift` paths
  - existential metadata lookup (`swift_getExistentialTypeMetadata`) in specific scenarios

### Important code observations

- `TypeMetadata` still contains direct existential metadata calls plus workaround attempts:
  - `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`
- `SwiftString.Length` still uses `PInvoke_GetLength` through `CallConvSwift`:
  - `src/Swift.Runtime/src/Swift/SwiftString.cs`
- Generator default is still direct `CallConvSwift` for most declarations:
  - `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- The codebase already has proven wrapper patterns using C-compatible entry points:
  - `SBW_Utf8Slice` + `SBW_Free`
  - async wrappers in `SwiftBindings` with Cdecl callbacks
  - witness/proxy bridge patterns using wrapper exports

### Practical temporary strategy (recommended order)

1. Add fail-fast guardrails for known fatal signature shapes on Mono.
   - Prefer explicit `NotSupportedException`/`SwiftRuntimeException` over process abort where possible.
2. Expand automatic wrapper routing for risky signatures.
   - Reuse existing `UsesWrapperLibrary` pattern to route risky members to wrapper library.
3. Prefer UTF-8 slice wrapper strategy for string-heavy boundaries.
   - Extend existing `SBW_Utf8Slice` approach beyond currently-covered cases.
4. Keep crash-prone tests isolated to reduce blast radius.
   - Continue and tighten `CrashRisk`/`--safe-only` and known-failure gating.

---

## Thread B: Nuke image loading - regression or not?

### Conclusion

This appears to be a regression in effective call-path usage, not an inherent "Nuke cannot load images" limitation.

### Why

Current generated Nuke bindings contain both:

1. A wrapper-backed async path that uses `SwiftBindings` async exports and Cdecl callbacks.
2. A direct callback-based `LoadImage(...)` path that uses `CallConvSwift` with closure marshalling and hits known runtime limitations.

### Evidence in current generated output

- Wrapper-backed async methods exist:
  - `BindingTesting/Nuke/output-ios/Swift.Nuke.cs`
    - `ImageAsync(ImageRequest)` and related async wrapper methods
    - wrapper import target: `DllImport("SwiftBindings", EntryPoint = "..._async")`
    - callback trampoline style: `CallConvCdecl`

- Direct callback `LoadImage(...)` overloads also exist:
  - `BindingTesting/Nuke/output-ios/Swift.Nuke.cs`
    - `LoadImage(..., Action<SwiftResult<...>> completion)`
    - direct import target: `DllImport("Nuke", EntryPoint = "...loadImage...")`
    - Swift calling convention on P/Invoke (`CallConvSwift`)

### What changed in test usage

- In commit `c4fb3ca` (2026-02-09), `BindingTesting/Nuke/NukeTestApp/Program.cs` added `LoadImageAsync` helper that calls `pipeline.LoadImage(request, result => ...)`.
- That helper routes through the direct callback-based API (the known-risk path), not the wrapper-backed `ImageAsync` path.

### Interpretation

- Earlier stages could still successfully load images when routed through wrapper-backed async methods.
- Current failure report ("cannot load image") is accurate for the callback-based path, but too broad as a global statement.
- The key issue is path selection/routing, not absolute inability to load.

---

## Suggested near-term decisions

1. Short-term unblock:
   - Switch Nuke app/test helper to wrapper-backed `ImageAsync` methods for image loading paths.
2. Generator policy:
   - Suppress or downgrade emission of callback-based `LoadImage` APIs on Mono-risk profiles, or auto-route them to wrappers.
3. Diagnostics:
   - Add explicit generated comments/attributes for members known to be runtime-risk on Mono (`CallConvSwift` + closure/non-blittable combos).

---

## Validation checklist for follow-up

1. Regenerate Nuke bindings.
2. Confirm test app image-load path uses `ImageAsync(...)` wrapper route.
3. Re-run Nuke simulator validation and confirm image loading succeeds on wrapper path.
4. Separately test direct callback `LoadImage(...)` and keep classified as known-risk until runtime fix.
