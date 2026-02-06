# SwiftUI Interop Bridge Design

**Date**: February 2026
**Status**: v1 + v2 Phases 1-3 complete; Phase 4 (Corpus + Metrics) next
**Prerequisite**: Must be solved before repo goes public
**Reviewers**: Claude, Codex

---

## Framing: Interop Bridge, Not SwiftUI Binding

This feature is **interop bridge support** — enabling .NET apps to present and interact with SwiftUI-based UI components from Swift libraries. It is explicitly not "SwiftUI binding support." We don't generate SwiftUI View structs, compose view hierarchies from C#, or implement the View protocol. We bridge SwiftUI views to UIKit so .NET/MAUI can present them.

This distinction matters for public messaging and user expectations.

---

## Problem

Many Swift libraries that .NET/MAUI apps need to consume include SwiftUI-based UI components. Today, the generator completely skips any type with a SwiftUI or Combine generic constraint — the entire type disappears from the generated bindings.

This is a blocking limitation. Real-world libraries increasingly ship SwiftUI interfaces alongside their core logic. Without a bridge, .NET developers must fall back to the same manual Swift proxy + Objective Sharpie workflow that this entire project exists to eliminate.

---

## Key Insight

.NET/MAUI apps don't compose SwiftUI views or implement the `View` protocol from C#. They need to:

1. **Configure** the UI component (settings, theme, callbacks)
2. **Present** it as a `UIViewController` (which MAUI can embed natively)
3. **Receive results** back (scan results, events, dismissal)

The bridge pattern converts SwiftUI views into UIKit view controllers via `UIHostingController` — the same pattern Apple provides for mixing SwiftUI into UIKit apps.

---

## Architecture

### Three-Layer Approach

**Layer 1: Normal Bindings** — Configuration types, result types, enums, and non-SwiftUI classes bind through the existing pipeline unchanged.

**Layer 2: Swift Bridge** — Auto-generated Swift code that wraps SwiftUI views in `UIHostingController` and exposes a C-callable API via `@_cdecl`.

**Layer 3: C# Bridge Bindings** — Auto-generated C# class that wraps the P/Invoke calls with `IDisposable` lifecycle.

### C ABI Contract

The bridge surface is intentionally small and stable — four functions per bridged view, plus optional `SBW_TEST_` helpers for automated testing:

| Function | Signature (C) | Purpose |
|----------|---------------|---------|
| `SBW_{Module}_{View}_Create` | `void* Create(void(*cb)(void*), void* userData)` | Create session + UIHostingController, return opaque handle |
| `SBW_{Module}_{View}_GetViewController` | `void* GetViewController(void* handle)` | Return UIViewController pointer (unretained) |
| `SBW_{Module}_{View}_Free` | `void Free(void* handle)` | Release session and all owned objects |
| `SBW_TEST_{Module}_{View}_FireRetry` | `void FireRetry(void* handle)` | Test-only: programmatically fire callback |

No Swift generics, actors, or async/await cross the ABI boundary. The bridge flattens all complexity into C-callable functions exported via `@_cdecl`.

#### Naming Convention

- **Production functions**: `SBW_{Module}_{View}_{Action}`
- **Test-only helpers**: `SBW_TEST_{Module}_{View}_{Action}`

#### Calling Convention

All exported functions use **cdecl** calling convention (`@_cdecl` on Swift side, `CallConvCdecl` on C# side). This is the standard C ABI — no Swift calling convention crosses the boundary.

#### Callback ABI

Callbacks use the `userData` pattern standard in C callback APIs:

```c
typedef void (*SBW_Callback)(void* userData);

void* SBW_BlinkIDUX_NoInternetView_Create(
    SBW_Callback retryCallback,  // nullable — null means no-op
    void* userData               // nullable — opaque context pointer
);
```

On the C# side, callbacks are declared with `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]` and passed as `delegate* unmanaged[Cdecl]<IntPtr, void>` function pointers.

---

## Ownership and Lifetime Contract

### Handle Lifecycle

1. **`Create` retains and tracks** — The factory creates the session object graph, retains it via `Unmanaged.passRetained()`, and registers the handle in a live-handle tracking set. The caller owns the handle.

2. **`GetViewController` is unretained** — Returns a pointer to the UIHostingController without additional retain. The session owns the controller.

3. **`Free` releases once and untracks** — Removes the handle from the tracking set, then releases the session's retain.

4. **`Free` is safe against misuse** — Double-free does not crash (handle not in tracking set). The C# `IDisposable` wrapper provides an additional managed-side guard.

5. **After `Free`, all derived pointers are invalid** — `GetViewController` returns `NULL` after `Free`. The C# bridge class sets `_handle = IntPtr.Zero` on dispose and throws `ObjectDisposedException`.

### Handle Tracking

The Swift bridge maintains a `Set<UnsafeMutableRawPointer>` of live handles. This provides null safety, stale pointer safety, and double-free safety. All set access is serialized on the main thread.

### Threading Contract

6. **All bridge functions marshal to main thread** — If called from a background thread, the bridge uses `DispatchQueue.main.sync`. If already on main, execution is immediate.

7. **Callbacks execute on `@MainActor`** — Void callbacks dispatch async on main. Async pattern callbacks (onReady, onError, onResult) execute within `@MainActor`-isolated `Task` blocks.

8. **Null callbacks are no-op** — If `Create` receives a `NULL` callback function pointer, the corresponding action is a no-op.

### C# Managed Wrapper Pattern

```csharp
public class NoInternetViewSession : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public IntPtr Handle => !_disposed
        ? _handle
        : throw new ObjectDisposedException(nameof(NoInternetViewSession));

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            NativeMethods.Free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
```

---

## Completed Deliverables

### Deliverable 1: Manual Bridge (BlinkIDUX)

Hand-written Swift bridge + C# bindings proving the pattern end-to-end. Three steps completed:

1. **NoInternetView Proof** — Simple view with `() -> Void` closure. Validated: creation, UIViewController retrieval, callback round-trip, dispose lifecycle. 10/10 tests.
2. **Full BlinkIDUXView** — Async factory with Task + callback pattern for `async throws` initialization chain. SDK → Analyzer → Model → View → UIHostingController.
3. **Test Infrastructure** — Generator coverage test (36/45 types, 0 crashes) + runtime bridge tests (16/16 on iOS Simulator).

### Deliverable 2: Generator Automation

The generator auto-detects SwiftUI Views and generates bridge code:
- **Detection**: `SwiftUIViewDetector` checks conformances for View protocol
- **Collection**: `SwiftUIBridgeCollector` gathers detected views per module
- **Emission**: `SwiftUIBridgeEmitter` generates Swift + C# bridge files
- **Report**: `SwiftUIView` (bridge generated) vs `SwiftUIConstraint` (generic param, skipped)

75 unit tests + 16/16 runtime tests.

### v2 Phases 1-2: Parameter Expansion + Async Inference

Full details: [`CompletedPhases/swiftui-bridge-v2-phases1-2.md`](CompletedPhases/swiftui-bridge-v2-phases1-2.md)

Expanded beyond v1 (primitives, String, `() -> Void`) to support: BoundEnum, BoundType (classes), TypedClosure (max 4 params), Optional\<T\>, and ABI-driven async factory inference with cross-module type resolution.

**Final state**: 1419 unit tests, 35/35 BridgeParamTest, 16/16 BlinkIDUX, 15/15 Lottie.

### v2 Phase 3: Bridge Hints

JSON sidecar file (`bridge-hints.json`) allowing users to override auto-detection without writing manual bridges. Supports: `skip` (suppress view entirely), `forceTemplate` (always template), `preferredInit` (select constructor by index), `asyncPattern` (force async classification with ABI inference re-run), `extraSwiftImports` (additional Swift imports), and forward-compatible `parameterOverrides`/`resultMonitor` (deserialized but not consumed until Phase 4).

Key implementation details:
- **Discovery**: CLI `--bridge-hints` → `{module}.bridge-hints.json` → `bridge-hints.json` (first match wins)
- **Precedence**: `skip` → `forceTemplate` → generic check → `asyncPattern` → existing auto-detection
- **Validation**: Unknown keys at all nesting levels (root, view, globalSettings, asyncPattern) warned and ignored; malformed JSON falls back to pure auto-detection
- **Safety**: Stale bridge file cleanup only deletes files with the `// Auto-generated by SwiftBindings` marker; user-maintained files are preserved with a warning
- **Import sanitization**: Null/empty/whitespace import values filtered before emission
- **AOT compatibility**: Source-generated `JsonSerializerContext` for trimmer-safe deserialization
- **Report**: Skipped views recorded as `BridgeStatus = "HintSkipped"` in binding report

**Files**: `BridgeHints.cs` (model + loader + validation), `SwiftUIBridgeEmitter.cs` (analysis + emission), `Program.cs` (CLI), `ModuleEmitter.cs` (threading + stale cleanup)

**Final state**: 1439 unit tests (20 bridge hints tests), no regressions.

---

## Challenges

### Actor and Async Initialization

`BlinkIDAnalyzer` has an `async throws` initializer, which can't be called synchronously from C. The bridge handles this with a Task + callback pattern. C# uses `TaskCompletionSource` to convert callbacks into `async/await`.

### SwiftUI.Color and SwiftUI.Font

Types like `BlinkIDTheme` use SwiftUI-specific property types. Options for later: skip with `[UnsupportedSwiftType]`, bridge via `CGColor`/`UIFont`, or accept hex string / font descriptor parameters.

### Combine @Published Properties

The bridge encapsulates the reactive model inside the session — the C# side interacts via callbacks, not property observation.

---

## Non-Goals

- **Composing SwiftUI views from C#** — No C# View structs or view hierarchy building
- **Implementing View protocol from C#** — No C# types conforming to SwiftUI.View
- **Reactive bindings** — No Combine ↔ INotifyPropertyChanged bridge
- **SwiftUI previews** — No Xcode preview support from .NET
- **@ViewBuilder closures** — No SwiftUI view-building closures from C#
- **Exposing Swift generics/actors directly through bridge ABI** — All complexity flattened behind C-callable functions

---

## Next: v2 Phase 4

See [`Future/swiftui-bridge-v2-plan.md`](Future/swiftui-bridge-v2-plan.md) for remaining work:
- **Phase 4**: Corpus + 3-tier Metrics — Track coverage across real libraries

---

## References

- `src/docs/known-issues-workarounds.md` — Runtime issues affecting async bridge patterns
- `src/docs/Future/emitter-redesign-proposal.md` — Emitter architecture context
- `src/docs/Future/swiftui-bridge-v2-plan.md` — v2 remaining phase (4)
- `src/docs/CompletedPhases/swiftui-bridge-v2-phases1-2.md` — v2 completed phases (1-2)
- `BindingTesting/BlinkId/` — BlinkIDUX bridge tests
- `BindingTesting/BridgeTest/` — BridgeParamTest synthetic views
- `BindingTesting/Lottie/` — Lottie bridge tests
