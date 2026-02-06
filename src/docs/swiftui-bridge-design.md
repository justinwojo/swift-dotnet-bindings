# SwiftUI Interop Bridge Design

**Date**: February 2026
**Status**: Step 3 Complete (test infrastructure implemented + validated 2026-02-05)
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

### Real-World Example: BlinkIDUX

Microblink ships two packages for document scanning:
- **BlinkID** — Core scanning logic (no UI). We already bind this successfully (18/18 runtime tests).
- **BlinkIDUX** — SwiftUI scanning interface. Currently unbindable.

Today, to use BlinkIDUX from .NET, developers must:
1. Write a custom Swift proxy that wraps `BlinkIDUXView` in a `UIHostingController`
2. Expose the controller via `@objc` attributes
3. Generate ObjC bindings with Objective Sharpie
4. Maintain both the proxy and the bindings

This is exactly the workflow our project eliminates for non-SwiftUI libraries. The interop bridge extends that to SwiftUI-based libraries.

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

**Layer 1: Normal Bindings** — Configuration types, result types, enums, and non-SwiftUI classes bind through the existing pipeline unchanged:
- `ScanningUXSettings` → C# struct
- `ScanningResult<T,U>` → C# enum
- `DocumentSide`, `PassportOrientation`, `CameraStatus` → C# enums
- `BlinkIDScanningAlertType` → C# enum
- `MicroblinkColor`, `ReticleState`, `UIEvent` → C# enums

**Layer 2: Swift Bridge** — Hand-written (now) or generated (later) Swift code that wraps SwiftUI views in `UIHostingController` and exposes a C-callable API.

**Layer 3: C# Bridge Bindings** — Hand-written (now) or generated (later) C# class that wraps the P/Invoke calls with `IDisposable` lifecycle.

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

- **Production functions**: `SBW_{Module}_{View}_{Action}` (e.g. `SBW_BlinkIDUX_NoInternetView_Create`)
- **Test-only helpers**: `SBW_TEST_{Module}_{View}_{Action}` — prefixed `SBW_TEST_` so they are clearly separated from the production surface

#### Calling Convention

All exported functions use **cdecl** calling convention (`@_cdecl` on Swift side, `CallConvCdecl` on C# side). This is the standard C ABI — no Swift calling convention crosses the boundary.

#### Callback ABI

Callbacks use the `userData` pattern standard in C callback APIs:

```c
// Callback signature — all callbacks follow this pattern
typedef void (*SBW_Callback)(void* userData);

// Create accepts callback + userData; the bridge stores both and
// invokes callback(userData) when the event fires.
void* SBW_BlinkIDUX_NoInternetView_Create(
    SBW_Callback retryCallback,  // nullable — null means no-op
    void* userData               // nullable — opaque context pointer
);
```

On the C# side, callbacks are declared with `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]` and passed as `delegate* unmanaged[Cdecl]<IntPtr, void>` function pointers.

---

## Ownership and Lifetime Contract

Clear ownership rules prevent leaks and use-after-free:

### Handle Lifecycle

1. **`Create` retains and tracks** — The factory creates the session object graph, retains it via `Unmanaged.passRetained()`, and registers the handle in a live-handle tracking set. The caller owns the handle. Returns `NULL` only on internal failure (currently cannot happen).

2. **`GetViewController` is unretained** — Returns a pointer to the UIHostingController without additional retain. The session owns the controller; the caller must not release it independently. Returns `NULL` if the handle is invalid.

3. **`Free` releases once and untracks** — Removes the handle from the tracking set, then releases the session's retain. All objects in the session graph are deallocated when the reference count drops to zero.

4. **`Free` is safe against misuse** — Calling `Free` with `NULL`, an already-freed handle, or a stale pointer is a no-op (the handle is not in the tracking set). Double-free does not crash. The C# `IDisposable` wrapper provides an additional managed-side guard.

5. **After `Free`, all derived pointers are invalid** — `GetViewController` returns `NULL` after `Free` (handle is no longer tracked). The C# bridge class sets `_handle = IntPtr.Zero` on dispose and throws `ObjectDisposedException` on subsequent access.

### Handle Tracking

The Swift bridge maintains a `Set<UnsafeMutableRawPointer>` of live handles. This provides:
- **Null safety**: `NULL` handles are rejected (never in the set)
- **Stale pointer safety**: Freed handles are removed from the set; subsequent access returns `NULL`/no-op
- **Double-free safety**: Second `Free` call finds handle already removed; no-op

All set access is serialized on the main thread (see threading contract below).

### Threading Contract

6. **All bridge functions marshal to main thread** — If called from a background thread, the bridge uses `DispatchQueue.main.sync` to execute on main. If already on main, execution is immediate. This replaces `dispatchPrecondition` crash assertions — callers from any thread get correct behavior, never a hard abort.

7. **Callbacks execute on `@MainActor`** — NoInternetView's retry callback dispatches async on main via `DispatchQueue.main.async`. Step 2 callbacks (onReady, onError, onResult) execute directly within `@MainActor`-isolated `Task` blocks — the `@MainActor` annotation serializes them with Free's handle-set mutation, eliminating races without an extra dispatch hop.

8. **Null callbacks are no-op** — If `Create` receives a `NULL` callback function pointer, the session is created successfully but the corresponding action is a no-op. This prevents crashes from non-.NET callers that pass null.

### C# Managed Wrapper Pattern

The C# side wraps the opaque handle in an `IDisposable` class:

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

This pattern enforces single-release semantics and fail-fast on post-dispose access. Future bridge views should follow the same `IDisposable` wrapper pattern.

---

## BlinkIDUX: Concrete API

### What the Library Exposes

```swift
// The SwiftUI View — what we need to bridge
@MainActor public struct BlinkIDUXView : View {
    public init(viewModel: BlinkIDUXModel)
    public var body: some View { get }
}

// The ViewModel — manages scanning state
@MainActor public class BlinkIDUXModel : ScanningViewModel<...> {
    public init(analyzer: any CameraFrameAnalyzer<CameraFrame, UIEvent>,
                uxSettings: ScanningUXSettings = ScanningUXSettings(),
                sessionNumber: Int)
}

// The Analyzer — actor handling frame analysis
public actor BlinkIDAnalyzer : CameraFrameAnalyzer {
    public init(sdk: BlinkIDSdk, ..., eventStream: BlinkIDEventStream,
                classFilter: (any BlinkIDClassFilter)? = nil) async throws
    public func result() async -> ScanningResult<BlinkIDScanningResult, ...>
    public func cancel()
}

// Simplest View — validates pattern without scanning complexity
@MainActor public struct NoInternetView : View {
    public init(retryAction: @escaping () -> Void)
    public var body: some View { get }
}

// Configuration — binds normally through existing pipeline
public struct ScanningUXSettings {
    public init(showIntroductionAlert: Bool = true,
                showHelpButton: Bool = true,
                preferredCameraPosition: Camera.CameraPosition = .back,
                allowHapticFeedback: Bool = true)
}
```

### Dependency Chain

```
BlinkIDUXView
  └── BlinkIDUXModel (@MainActor class)
        └── BlinkIDAnalyzer (actor, async throws init)
              └── BlinkIDSdk (external — from BlinkID module)
              └── BlinkIDEventStream (actor)
              └── BlinkIDSessionSettings (has default)
              └── BlinkIDClassFilter? (optional protocol)
        └── ScanningUXSettings (has default)

NoInternetView (simpler — validates pattern first)
  └── retryAction: () -> Void (just a closure)
```

---

## Deliverables

### Deliverable 1: Manual Bridge (Now)

Hand-written Swift bridge layer + C# bindings for BlinkIDUX. Proves the pattern end-to-end.

#### Step 1: NoInternetView Proof

Start with `NoInternetView(retryAction:)` — the simplest SwiftUI View in the library. This validates:
- SwiftUI → `UIHostingController` creation
- `@convention(c)` callback plumbing
- Ownership (retain/release lifecycle)
- IntPtr → MAUI view controller presentation

**Swift bridge** (`BindingTesting/BlinkId/SwiftBridge/BlinkIDUXBridge.swift`):
```swift
public typealias RetryCallbackFn = @convention(c) (UnsafeMutableRawPointer?) -> Void

final class NoInternetSession {
    let hostingController: UIHostingController<NoInternetView>
    private let retryCallback: RetryCallbackFn?
    private let userData: UnsafeMutableRawPointer?

    init(retryCallback: RetryCallbackFn?, userData: UnsafeMutableRawPointer?) {
        // Wires retryAction to dispatch callback(userData) async on main queue
    }
    func fireRetry() { /* async dispatch matching production path */ }
}

private var liveHandles = Set<UnsafeMutableRawPointer>()

@_cdecl("SBW_BlinkIDUX_NoInternetView_Create")
public func SBW_BlinkIDUX_NoInternetView_Create(
    _ retryCallback: RetryCallbackFn?, _ userData: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? { /* onMainThread { create + track } */ }

@_cdecl("SBW_BlinkIDUX_NoInternetView_GetViewController")
public func SBW_BlinkIDUX_NoInternetView_GetViewController(
    _ handle: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? { /* onMainThread { validate + return } */ }

@_cdecl("SBW_BlinkIDUX_NoInternetView_Free")
public func SBW_BlinkIDUX_NoInternetView_Free(
    _ handle: UnsafeMutableRawPointer?
) { /* onMainThread { untrack + release } */ }
```

**Status**: Implemented and validated. 10/10 tests pass on iOS Simulator.

**Exit criteria** (met):
- NoInternetView creates successfully from .NET (non-null handle)
- UIViewController retrieved and wraps to managed object
- View presents modally on screen
- Retry callback fires back to C# with userData round-trip (0x42 sentinel verified)
- Dispose releases cleanly; post-dispose access throws `ObjectDisposedException`

#### Step 2: Full BlinkIDUXView

Extend to the full scanning flow:
- Session class holding the full object graph (SDK, analyzer, event stream, model, controller)
- Async factory with callback (`onReady`/`onError`) since `BlinkIDAnalyzer.init` is `async throws`
- Result callback when scan completes/cancels
- Event stream bridged to callbacks (optional — can defer)

**Exit criteria**: Scanning UI launches from .NET, scan completes, result received in C#.

#### Step 3: Test Infrastructure

Two test layers:

1. **Generator coverage test** — Run generator on BlinkIDUX, verify SwiftUI types are detected and reported correctly (skip reasons, not crash). Non-SwiftUI types (enums, settings) bind normally.

2. **Runtime bridge test** — iOS Simulator test app:
   - `NoInternetView` creation + presentation + callback + cleanup
   - Full scan session lifecycle (create → get controller → await result → dispose)
   - Post-dispose access throws `ObjectDisposedException`

**Packaging requirements:**
- `NativeReference` for both `BlinkID.xcframework` and `BlinkIDUX.xcframework`
- Resolver paths validated at startup with clear diagnostics if missing

**Status**: Implemented and validated 2026-02-05. Both test layers pass:
- Generator coverage: 36/45 types emitted, 44 members skipped (SwiftUI.Color/Font properties + AnyType fallbacks), 0 crashes
- Runtime bridge: 16/16 tests pass (3/3 framework diagnostics + 5 NoInternetView + 6 scanning session + 2 cleanup)

**Scripts:**
- `./build-all-bridge.sh` — Full Step 3 pipeline (generator coverage + bridge build + test app build)
- `./regenerate-ux-bindings.sh` — Generator coverage test only (generates TBD, runs generator, validates report)
- `./build-ux-testapp.sh` — Build BlinkIDUXTestApp only
- `./validate-bridge.sh` — Run runtime tests on iOS Simulator

#### File Layout

```
BindingTesting/BlinkId/
├── BlinkIDUX.xcframework/     # Already copied by Codex
├── SwiftBridge/
│   └── BlinkIDUXBridge.swift  # Hand-written bridge wrappers
├── BlinkIDUXTestApp/          # .NET test app (16 tests)
│   ├── BlinkIDUXTestApp.csproj
│   └── Program.cs             # P/Invoke, wrappers, tests, framework diagnostics
├── output-ux/                 # Generator coverage output (gitignored)
│   ├── Swift.BlinkIDUX.cs     # Generated bindings
│   └── binding-report.json    # Skip reasons and metrics
├── build-all-bridge.sh        # Full Step 3 pipeline
├── regenerate-ux-bindings.sh  # Generator coverage test
├── build-bridge.sh            # Build SwiftBridge → dylib
├── build-ux-testapp.sh        # Build BlinkIDUXTestApp
└── validate-bridge.sh         # Run bridge tests on simulator
```

### Deliverable 2: Generator Automation (Later)

Teach the generator to auto-detect SwiftUI Views and generate bridge code. Deferred until the manual bridge pattern is proven and stable.

#### Detection Phase
- Identify types conforming to `SwiftUI.View` / `SwiftUICore.View`
- Change skip logic: View types → flag for bridge generation
- Update binding report: `SwiftUIView` (bridge generated) vs `SwiftUIType` (skipped)

#### Emission Phase
- New `SwiftUIBridgeEmitter` generates the Swift wrapper file
- Analyze View `init` parameters, trace dependency chain
- Generate session class, C ABI functions, C# bridge class
- Complex dependency chains may produce a template requiring manual review

#### Binding Report Additions
- `BridgedViews` section listing each bridged View with wrapper details
- New skip reasons: `SwiftUIView`, `SwiftUIType`, `CombinePublished`

---

## Challenges

### Actor and Async Initialization

`BlinkIDAnalyzer` has an `async throws` initializer, which can't be called synchronously from C. The bridge handles this with a Task + callback pattern:

```swift
@_silgen_name("SBW_BlinkIDUX_BlinkIDUXView_Create")
public func createScanningSession(
    /* params */,
    onReady: @convention(c) (UnsafeMutableRawPointer) -> Void,
    onError: @convention(c) (UnsafePointer<UInt8>, Int) -> Void
) {
    Task { @MainActor in
        do {
            let analyzer = try await BlinkIDAnalyzer(sdk: sdk, ...)
            // ... build session ...
            onReady(Unmanaged.passRetained(session).toOpaque())
        } catch {
            let msg = "\(error)"
            msg.withCString { onError($0, msg.count) }
        }
    }
}
```

The C# side uses `TaskCompletionSource` to convert callbacks into `async/await`.

### SwiftUI.Color and SwiftUI.Font

Types like `BlinkIDTheme` use `SwiftUI.Color` and `SwiftUI.Font` properties. These don't map to .NET types. For the manual bridge, we skip theme customization initially. For later automation, options include:
- Skip the properties with `[UnsupportedSwiftType]`
- Bridge via `CGColor`/`UIFont` (UIKit equivalents .NET can work with)
- Accept hex string / font descriptor parameters

### Combine @Published Properties

`BlinkIDUXModel` and `ScanningViewModel` use `@Published` extensively. The bridge encapsulates the reactive model inside the session — the C# side interacts via callbacks, not property observation. Current values can be exposed through getter bridge functions if needed.

---

## Non-Goals

- **Composing SwiftUI views from C#** — No C# View structs or view hierarchy building
- **Implementing View protocol from C#** — No C# types conforming to SwiftUI.View
- **Reactive bindings** — No Combine ↔ INotifyPropertyChanged bridge
- **SwiftUI previews** — No Xcode preview support from .NET
- **@ViewBuilder closures** — No SwiftUI view-building closures from C#
- **Exposing Swift generics/actors directly through bridge ABI** — All complexity flattened behind C-callable functions

---

## References

- `src/docs/known-issues-workarounds.md` — Runtime issues affecting async bridge patterns
- `src/docs/Future/emitter-redesign-proposal.md` — Emitter architecture context
- `BindingTesting/BlinkId/` — Existing BlinkID binding tests (core SDK) + BlinkIDUX.xcframework
