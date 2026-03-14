# Ownership Automation: Reducing Disposal Burden for Binding Consumers

**Date**: March 13, 2026
**Status**: Design — ready for implementation
**Motivation**: Make consuming Swift bindings as frictionless as Xamarin/ObjC bindings
**Reviewed by**: Codex (March 13, 2026) — three review rounds, all P1/P2/P3 findings incorporated

---

## Problem Statement

The current ownership model requires binding consumers to explicitly `Dispose()` every Swift object or risk leaks. The [Ownership wiki page](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Ownership) describes 10 boundary rules that users "must" follow.

Compared to Xamarin/ObjC bindings — where NSObject instances are reference-counted transparently and the GC finalizer sends `release` automatically — this is a significantly higher cognitive burden. Users coming from that world expect to create objects, use them, and let the GC handle cleanup.

**Goal**: Get as close to the Xamarin/ObjC experience as possible. Users should rarely think about disposal. When they do, it should be for the same reasons .NET developers already understand (deterministic cleanup of scarce resources like file handles).

---

## Current State Analysis

### What already works (better than the wiki suggests)

1. **NativeAOT (production/device)**: The finalizer already calls the `@_cdecl` destroy wrapper via `SwiftDispose.FinalizerCleanup`. If a user forgets `Dispose()`, Swift deinitializers DO run — just non-deterministically.

2. **Mono (simulator only)**: Finalizer cleanup is a no-op due to `jit-info.c:918`. This is a dev-time-only limitation.

3. **The wiki doc is overly conservative**: It says "GC finalization does NOT call Swift deinitializers" which is only true on Mono. On NativeAOT (where production apps run), it does.

### Key architectural insight: classes are already close to ARC-bridged

The current class model is **NOT** a full buffer copy. Here's what actually happens:

```
Swift wrapper:  passRetained(result).toOpaque()  →  IntPtr (+1 ARC retain)
C# receive:     NativeMemory.Alloc(8 bytes) → store IntPtr → wrap in SafeHandle
C# dispose:     @_cdecl destroy wrapper → deinitialize(count: 1) → swift_release
                NativeMemory.Free(8 byte buffer)
```

For classes, the "buffer" is just **8 bytes holding a pointer**. The Swift class object lives on the Swift heap, managed by ARC. We're already doing pointer-based ownership transfer — just with an unnecessary 8-byte indirection and a VWT destroy call instead of a simple `Arc.Release`.

### What genuinely requires manual disposal

| Type | C# Projection | Why | Avoidable? |
|------|--------------|-----|------------|
| **Swift classes** | `class` | ARC reference must be released | **Yes** — finalizer can call `Arc.Release` safely |
| **Non-frozen structs** | `class` (with `_payload`) | VWT destroy runs deinitializers on ref fields | **Partially** — finalizer works on NativeAOT but not Mono |
| **Frozen structs with ref fields** | `class` (with `_payload` + inner `Buffer` struct) | Same as above | **Partially** — same limitation |
| **Protocol proxies** | `class` | Hold existential containers + EveryProtocol wrappers | **Partially** — finalizer could clean up |
| **Closure wrappers** | Internal | GCHandle must be freed, context ARC-released | **Yes** — already handled internally |
| **Frozen blittable structs** | `struct` | None — pure value types, empty `Dispose()` | N/A — no disposal needed |

---

## Proposed Improvements

Four workstreams, ordered by impact and independence. Design constraints from Codex review (March 2026) are noted inline.

### 1. ARC Bridge for Swift Classes

**Impact**: High — classes are the most common type in Swift framework APIs
**Complexity**: Medium — changes SafeHandle semantics and ClassHandler emission

#### The Xamarin Model (proven for 12+ years)

In Xamarin, `NSObject` wrappers hold a raw ObjC pointer. The GC finalizer sends `release`. No explicit disposal required for most uses. This works because:
- ObjC's `release` is thread-safe (atomic ref count decrement)
- ObjC's `dealloc` runs on the releasing thread (no thread affinity)
- The GC finalizer thread is a valid context for this

#### Hypothesis: Swift classes have the same properties

The following properties are believed to hold based on Swift's ARC design and ObjC precedent, but **must be verified on Mono's finalizer thread before committing to this approach** (see Session 2, critical verification):

- `swift_release` is thread-safe (atomic ref count decrement via `Arc.Release`)
- Swift's `deinit` runs on the releasing thread (no thread affinity requirement)
- Our `@_cdecl` destroy wrappers route through C calling convention (no CallConvSwift issues)
- `Arc.Release` already excludes `[SuppressGCTransition]` specifically because deinit may call back into managed code
- `Arc.Release` uses `CallingConvention.Cdecl`, NOT `CallConvSwift` — the `jit-info.c:918` crash only affects CallConvSwift. This is the key assumption that needs empirical validation on Mono's finalizer thread.

#### Proposed change: `SwiftClassHandle<T>`

New SafeHandle specifically for Swift classes. Different from the current buffer-based `SwiftSafeHandle<T>`:

```csharp
/// <summary>
/// Lightweight ARC-bridged handle for Swift class instances.
/// The handle IS the Swift object pointer (no buffer indirection).
/// Finalizer-safe: calls Arc.Release (direct Cdecl P/Invoke to swift_release).
/// </summary>
public sealed class SwiftClassHandle<T> : SafeHandleZeroOrMinusOneIsInvalid where T : ISwiftObject
{
    public SwiftClassHandle(IntPtr swiftObjectPointer)
        : base(ownsHandle: true)
    {
        SetHandle(swiftObjectPointer);
        // NOTE: DisposeScope registration happens at the wrapper level
        // (generated code), not here. See "Registration hook points" below.
    }

    /// <summary>
    /// Explicit Dispose: deterministic release. Use for scarce resources.
    /// Not required for correctness — finalizer handles ARC cleanup.
    /// </summary>
    public new void Dispose()
    {
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    protected override bool ReleaseHandle()
    {
        if (handle == IntPtr.Zero)
            return true;

        try
        {
            // Thread-safe: swift_release is atomic. Deinit may run but
            // has no thread affinity requirement (same as ObjC dealloc).
            Arc.Release(handle);
        }
        catch
        {
            // Swallow — ReleaseHandle must not throw per SafeHandle contract.
        }

        handle = IntPtr.Zero;
        return true;
    }
}
```

#### What changes in generated code

**Before** (current):
```csharp
public partial class ImagePipeline : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<ImagePipeline> _payload;  // Owns 8-byte buffer → IntPtr → Swift object

    public ImagePipeline(/* args */)
    {
        _payload = new SwiftSafeHandle<ImagePipeline>((IntPtr)NativeMemory.Alloc(_payloadSize));
        var resultPtr = _payload.DangerousGetHandle();
        PInvoke_init(resultPtr, /* args */);  // Writes IntPtr into buffer
    }

    public void Dispose()               // REQUIRED or leak
    {
        _payload.Dispose();
        GC.SuppressFinalize(this);
    }

    ~ImagePipeline()
    {
        SwiftDispose.FinalizerCleanup(_payload);  // Only works on NativeAOT
    }
}
```

**After** (ARC bridge):
```csharp
public partial class ImagePipeline : ISwiftObject, IDisposable
{
    private SwiftClassHandle<ImagePipeline> _handle;  // IS the Swift object pointer

    public ImagePipeline(/* args */)
    {
        var ptr = PInvoke_init(/* args */);  // Returns IntPtr directly (+1 retained)
        _handle = new SwiftClassHandle<ImagePipeline>(ptr);
    }

    // Dispose available for deterministic cleanup but NOT required.
    // Finalizer handles ARC release automatically.
    public void Dispose()
    {
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    // No generated finalizer needed — SwiftClassHandle<T> inherits SafeHandle's
    // built-in polink finalizer, which calls ReleaseHandle() -> Arc.Release().
    // This is safe on NativeAOT. Mono finalizer-thread safety is the key
    // hypothesis to verify before shipping (see Session 2, critical verification).
}
```

#### What changes in wrapper emission

**Class constructors already return `passRetained().toOpaque()`** in the current Swift wrappers — no change needed on the Swift side. The `resultPtr.initializeMemory` pattern is only used for struct constructors.

Current Swift wrapper (unchanged):
```swift
@_cdecl("SBW_init_ImagePipeline")
public func SBW_init_ImagePipeline(_ config: ...) -> UnsafeMutableRawPointer {
    let result = ImagePipeline(configuration: config)
    return Unmanaged.passRetained(result).toOpaque()
}
```

**Class method returns** and **class property getters** also already use `passRetained().toOpaque()`.

The marshalling changes are **C#-side only**: update to wrap the returned `IntPtr` in `SwiftClassHandle<T>` instead of allocating a buffer + `NativeMemory.Alloc` + storing the pointer. The only Swift-side change is removing per-class `SBW_Destroy_*` functions (via `DestroyWrapperEmitter`).

**Destroy wrappers eliminated for classes**: No more per-type `SBW_Destroy_*` functions. `SwiftClassHandle.ReleaseHandle()` calls `Arc.Release()` directly — no VWT involved.

#### Scope of change

| Component | Change |
|-----------|--------|
| `SwiftClassHandle<T>` | New class in `Swift.Runtime` |
| `ClassHandler.cs` | Emit `SwiftClassHandle<T>` instead of `SwiftSafeHandle<T>` for classes (C# emission) |
| `PInvokeEmitter.cs` | Update class return/input marshalling to use `SwiftClassHandle<T>` (C# emission) |
| `SwiftMarshal.cs` | Update `MarshalFromSwift<T>` for class path |
| `DestroyWrapperEmitter.cs` | Skip emission for classes — removes `SBW_Destroy_*` from generated Swift wrapper output |
| Golden files | Regenerate |
| Wiki Ownership doc | Update to reflect classes no longer need explicit disposal |

Note: Class constructor/method/property Swift wrappers already use `passRetained().toOpaque()` — no Swift-side changes needed for those. The only Swift-side change is removing destroy wrapper emission (`DestroyWrapperEmitter`).

**Required audit (P2 from Codex review)**: Today `ISwiftObject.SwiftHandle` on a class returns a pointer-to-buffer (via `_payload.DangerousGetHandle()`), not the Swift object pointer directly. With `SwiftClassHandle<T>`, it would return the Swift object pointer. This is a semantic change that affects every consumer of `SwiftHandle` / `Payload.DangerousGetHandle()`. A **repo-wide audit** is required before this change, including:
- `ConstrainedExistentialBridge.cs` (assumes `SwiftHandle` = raw object pointer — would become correct)
- All `MarshalToSwift` implementations (class path currently dereferences buffer → pointer)
- Any generated code that calls `_payload.DangerousGetHandle()` and then dereferences it as `*(IntPtr*)handle`
- Protocol proxy marshalling paths
- Closure context marshalling

#### What this does NOT change

- **Structs**: Still use `SwiftSafeHandle<T>` with buffer model (VWT destroy needed)
- **Protocol proxies**: Still need explicit disposal (existential containers)
- **Closure wrappers**: Already handled internally
- **The 10 boundary rules**: Still valid for contributors/binding authors, but consumers see a simpler model

#### Risk assessment

| Risk | Mitigation |
|------|------------|
| `Arc.Release` from finalizer thread triggers deinit that deadlocks | Same risk as Xamarin/ObjC — proven safe for 12+ years. Swift deinit has no thread affinity. |
| Mono JIT crash on `Arc.Release` from finalizer | `Arc.Release` uses `CallingConvention.Cdecl` (NOT CallConvSwift). The jit-info crash only affects CallConvSwift. Verify in simulator tests. |
| Object used after finalizer runs | Standard .NET GC semantics — if you hold a reference, the object isn't collected. Same as any SafeHandle. |
| Resurrection scenarios | SafeHandle already handles this correctly via `DangerousAddRef`/`DangerousRelease`. |
| Breaking change for existing consumers | SwiftClassHandle<T> is internal to generated code. Public API stays the same (ISwiftObject, IDisposable). Dispose() still works. |

---

### 2. SwiftDisposeScope — Batch Disposal

**Impact**: High — eliminates the "must `using` every variable" pattern
**Complexity**: Low-medium — runtime addition + generator registration hooks

#### Concept

Inspired by TorchSharp's `DisposeScope` pattern. Users wrap a block of code in a scope; all Swift objects created within auto-dispose when the scope exits:

```csharp
// Instead of:
using var img1 = pipeline.GetImage(url1);
using var img2 = pipeline.GetImage(url2);
using var processed = processor.Process(img1);
using var cached = cache.Store(processed);

// Users write:
using (new SwiftDisposeScope())
{
    var img1 = pipeline.GetImage(url1);      // auto-tracked
    var img2 = pipeline.GetImage(url2);      // auto-tracked
    var processed = processor.Process(img1);  // auto-tracked
    var cached = cache.Store(processed);      // auto-tracked
    // ALL disposed at scope exit
}

// Or with explicit escape:
ImagePipeline result;
using (new SwiftDisposeScope())
{
    var img1 = pipeline.GetImage(url1);
    var img2 = pipeline.GetImage(url2);
    result = processor.Process(img1);
    result.DetachFromScope();  // Won't be disposed — caller takes ownership
}
```

#### Implementation

```csharp
/// <summary>
/// Automatic disposal scope for Swift objects. All ISwiftObject instances created
/// within an active scope are tracked and disposed when the scope exits.
///
/// Threading model: async-flow-aware via AsyncLocal, but NOT safe for parallel
/// mutation. A single scope must not be shared across concurrent tasks that
/// create Swift objects simultaneously. Use one scope per sequential async flow.
/// If parallel tasks each need tracking, each should create its own scope.
/// </summary>
public sealed class SwiftDisposeScope : IDisposable
{
    private static readonly AsyncLocal<SwiftDisposeScope?> s_current = new();

    private readonly SwiftDisposeScope? _parent;
    private readonly List<IDisposable> _tracked = new();
    private bool _disposed;

    /// <summary>
    /// The currently active scope, or null if none.
    /// </summary>
    public static SwiftDisposeScope? Current => s_current.Value;

    public SwiftDisposeScope()
    {
        _parent = s_current.Value;
        s_current.Value = this;
    }

    /// <summary>
    /// Register an object for automatic disposal. Called from generated
    /// wrapper constructors and NewFromPayload (heap-backed types only).
    /// </summary>
    internal static void TryRegister(IDisposable obj)
    {
        s_current.Value?._tracked.Add(obj);
    }

    /// <summary>
    /// Remove an object from automatic disposal tracking.
    /// Walks the entire scope chain to find the scope that owns this object,
    /// so it works correctly even when called from a nested inner scope.
    /// </summary>
    public static bool Detach(IDisposable obj)
    {
        var scope = s_current.Value;
        while (scope != null)
        {
            if (scope._tracked.Remove(obj))
                return true;
            scope = scope._parent;
        }
        return false;
    }

    /// <summary>
    /// Move an object from its owning scope to that scope's parent.
    /// Walks the scope chain to find the correct owning scope first.
    /// </summary>
    public static bool MoveToParent(IDisposable obj)
    {
        var scope = s_current.Value;
        while (scope != null)
        {
            if (scope._tracked.Remove(obj))
            {
                scope._parent?._tracked.Add(obj);
                return true;
            }
            scope = scope._parent;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose in reverse creation order (LIFO)
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            try { _tracked[i].Dispose(); }
            catch { /* Swallow — same as SafeHandle contract */ }
        }

        _tracked.Clear();
        s_current.Value = _parent;
    }
}
```

#### Registration hook points — wrapper level, NOT handle level

**Critical design constraint (P1 from Codex review)**: Registration must happen at the **wrapper object** level (the `ISwiftObject`/`IDisposable` the user interacts with), NOT at the inner SafeHandle level. If the scope tracks the inner handle but `DetachFromScope()` is called on the outer wrapper, it removes the wrong object and the handle still gets disposed.

Registration happens in **generated code**, not in SafeHandle constructors. Only **heap-backed disposable reference types** (C# classes that hold native resources and implement `IDisposable` with real cleanup) register. This includes classes with `_payload` SafeHandle AND protocol proxies with ExistentialContainer/EveryProtocol. Frozen blittable structs (projected as C# `struct`) do NOT register — boxing `this` creates a copy, making detach/dispose semantically broken.

**Which types register:**

| Swift Type | C# Projection | Has `_payload`? | Registers? |
|------------|--------------|-----------------|------------|
| Class | `class` | Yes (SwiftClassHandle after ARC bridge) | **Yes** |
| Non-frozen struct | `class` | Yes (SwiftSafeHandle) | **Yes** |
| Frozen struct + ref fields | `class` | Yes (SwiftSafeHandle) | **Yes** |
| Protocol proxy | `class` | No `_payload`, but holds ExistentialContainer + EveryProtocol (heap-backed, disposable) | **Yes** |
| Frozen blittable struct | `struct` | No | **No** |

The generator already knows which path it's in via `isProjectedAsClass` (FrozenStructHandler) or by being NonFrozenStructHandler/ClassHandler/ProtocolProxyEmitter. Registration is emitted in all heap-backed paths:

```csharp
// Generated class constructor (class, non-frozen struct, frozen+ref-fields):
public ImagePipeline(/* args */)
{
    _payload = new SwiftSafeHandle<ImagePipeline>(/* ... */);
    PInvoke_init(/* ... */);
    SwiftDisposeScope.TryRegister(this);  // <-- register the wrapper, not the handle
}

// Generated NewFromPayload (class-projected only):
static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
{
    var obj = new ImagePipeline(new SwiftHandle(handle));
    SwiftDisposeScope.TryRegister(obj);  // <-- register the wrapper
    return obj;
}

// Protocol proxy — YES registration (heap-backed, disposable):
// Proxy constructors are emitted from ProtocolProxyEmitter.Receivers.cs;
// NewFromPayload and Dispose are in ProtocolProxyEmitter.SwiftObject.cs.
// TryRegister goes at the end of each emitted constructor in Receivers.cs
// and in NewFromPayload in SwiftObject.cs.
SwiftDisposeScope.TryRegister(this);  // last line of proxy constructor
SwiftDisposeScope.TryRegister(obj);   // in NewFromPayload after creation

// Frozen blittable struct — NO registration:
static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
{
    return *(FrozenPoint*)handle;  // Direct dereference, no boxing, no scope tracking
}
```

The scope tracks the same object that `DetachFromScope()` / `MoveToParentScope()` operate on — identity match is guaranteed because we register the wrapper instance, and `Detach` walks the scope chain to find the owning scope.

#### Extension method for ergonomic detach

```csharp
public static class SwiftDisposeScopeExtensions
{
    /// <summary>
    /// Detach this object from automatic scope disposal.
    /// Returns the object for fluent chaining.
    /// </summary>
    public static T DetachFromScope<T>(this T obj) where T : ISwiftObject
    {
        SwiftDisposeScope.Detach(obj);
        return obj;
    }

    /// <summary>
    /// Move this object to the parent scope.
    /// Returns the object for fluent chaining.
    /// </summary>
    public static T MoveToParentScope<T>(this T obj) where T : ISwiftObject
    {
        SwiftDisposeScope.MoveToParent(obj);
        return obj;
    }
}
```

#### Design decisions (learned from TorchSharp)

| Decision | Rationale |
|----------|-----------|
| **AsyncLocal** not ThreadLocal | Respects `async/await` logical call chains. ThreadLocal can leak across async boundaries. Note: AsyncLocal provides flow propagation, NOT synchronization. `_tracked` is a plain `List` — concurrent mutation from parallel tasks sharing a scope would race. The contract is: one scope per sequential async flow, parallel tasks create their own scopes. |
| **List** not HashSet | TorchSharp found that disposed objects have invalid handles, breaking `Equals()`. List with O(n) removal is acceptable — detach is rare. |
| **Reverse disposal order** | LIFO matches `using` block semantics and handles dependencies (later objects may reference earlier ones). |
| **Silent when no scope** | `TryRegister` is a no-op when no scope is active. No performance penalty for code not using scopes. |
| **Opt-out, not opt-in** | Objects auto-register. Users opt out via `DetachFromScope()`. This is the right default for "batteries included." |
| **Detach walks scope chain** | P1 from Codex review: `Detach`/`MoveToParent` must search the entire parent chain, not just the current scope. An object registered in an outer scope would silently fail to detach if an inner scope is active. Cost is O(depth × objects) but depth is typically 1-3. |
| **Only heap-backed wrappers register** | P1 from Codex review: Frozen blittable structs (projected as C# `struct`) must NOT register. Boxing `this` creates a copy — detach/dispose would operate on the copy, not the original. Their `Dispose()` is empty anyway. Only class-projected types (non-frozen structs, frozen structs with ref fields, classes) register. |

---

### 3. SB1001 Analyzer Improvements

**Impact**: Medium — better tooling guidance for users who don't use scopes
**Complexity**: Low

#### Current state

`SB1001` (`SwiftObjectDisposeAnalyzer`) warns when `ISwiftObject` locals aren't disposed. It recognizes:
- `using` declarations/statements
- Unconditional `Dispose()` in same block
- `try/finally` with `Dispose()` in finally
- Direct `return` (ownership transfer)

A code fix provider (`SwiftObjectDisposeCodeFixProvider`) already exists and offers "Wrap in `using`".

#### Proposed improvements

**3a. Recognize DisposeScope** — Suppress `SB1001` when assignment is inside an active `SwiftDisposeScope`:
```csharp
using (new SwiftDisposeScope())
{
    var img = pipeline.GetImage(url);  // No warning — scope handles disposal
}
```

**3b. Severity adjustment for ARC-bridged classes** (Session 3 only, after ARC bridge is proven) — Downgrade from Warning to Info/Suggestion for class types, keep Warning for struct types.

#### Explicitly NOT changing (P1 from Codex review)

The following suppressions were considered but rejected as unsound — they hide real leaks without ownership proofs:

- ~~Field/property assignment as ownership transfer~~ — assigning to a field doesn't guarantee the field's owner will ever dispose it
- ~~Method parameter passing as ownership transfer~~ — the called method may not take ownership

The analyzer's current conservative bias is correct. Only suppress when we can structurally prove cleanup will happen (DisposeScope, `using`, explicit `Dispose()`).

---

### 4. Documentation and Wiki Updates

**Impact**: High — sets correct expectations
**Complexity**: Low

#### Wiki Ownership page rewrite

Restructure from "10 rules you must follow" to:

1. **For most users**: Swift classes work like any .NET class. Use `Dispose()` for deterministic cleanup of scarce resources (same as `FileStream`, `HttpClient`). The GC handles the rest.

2. **For performance-sensitive code**: Use `SwiftDisposeScope` to batch-dispose many objects efficiently.

3. **For struct bindings that are projected as classes**: Non-frozen structs and frozen structs with reference fields (e.g., `String` properties) are projected as C# classes with `_payload` SafeHandles. These DO require `using`/`Dispose()` — Swift's deinitializer must run to release ref-counted fields. Frozen blittable structs (projected as C# `struct`) need no disposal at all — they're pure value types with empty `Dispose()` bodies.

4. **For advanced users/contributors**: The full 10-rule breakdown (moved to a separate "Internals" page).

#### Consumer documentation tone shift

**Before** (scary):
> Violating any of these causes leaks, use-after-free, or double-free crashes.

**After** (familiar):
> Swift class instances are reference-counted, like ObjC objects in Xamarin. The GC releases them automatically. For struct/enum bindings projected as classes (non-frozen, or frozen with reference fields), use `Dispose()` for deterministic cleanup — the same pattern as `SafeHandle` elsewhere in .NET. Frozen blittable structs are C# value types and need no disposal.

---

## Session Plan

3 sessions: 2 implementation sessions + 1 documentation cleanup session.

---

### Session 1: SwiftDisposeScope + SB1001 Improvements — **Status: Complete** (commit `3551784b`)

**Scope**: Runtime, analyzer, and generator registration hooks. Immediately usable by consumers.

**Deliverables**:

1. **`SwiftDisposeScope`** in `Swift.Runtime`
   - `SwiftDisposeScope` class with `AsyncLocal<SwiftDisposeScope?>` tracking
   - `TryRegister`, `Detach`, `MoveToParent` static methods
   - Extension methods: `DetachFromScope<T>()`, `MoveToParentScope<T>()`
   - Registration at **wrapper object level** (generated code), NOT SafeHandle constructors
   - LIFO disposal order

2. **Generator changes for scope registration** (heap-backed wrappers only)
   - Emit `SwiftDisposeScope.TryRegister(this)` at end of every generated public constructor for **heap-backed types**: classes, non-frozen structs, frozen structs with ref fields, protocol proxies
   - Emit `SwiftDisposeScope.TryRegister(obj)` in `NewFromPayload` for heap-backed types
   - **Do NOT emit** for frozen blittable structs (C# `struct`) — boxing `this` creates a copy, breaking identity semantics
   - The generator already knows the projection path: ClassHandler always, NonFrozenStructHandler always, FrozenStructHandler only when `isProjectedAsClass == true`, ProtocolProxyEmitter always

3. **Tests for SwiftDisposeScope**
   - Basic scope: create objects, verify all disposed on scope exit
   - Nested scopes: inner scope disposes its objects, outer scope disposes its own
   - `DetachFromScope`: object survives scope exit (identity match — same object registered and detached)
   - **Detach from nested scope**: object registered in outer scope, `DetachFromScope()` called while inner scope is active — must walk chain and find the correct owning scope
   - `MoveToParentScope`: object transfers to parent
   - **MoveToParent from nested scope**: same chain-walking behavior as detach
   - No scope active: `TryRegister` is no-op, no overhead
   - Async/await: scope tracks across `await` boundaries
   - Exception safety: objects disposed even when scope body throws
   - Empty scope: no-op dispose
   - Double-dispose safety: scope tolerates already-disposed objects
   - **Frozen blittable struct NOT tracked**: verify that frozen blittable structs (C# `struct`) are not registered by generated code

4. **SB1001 enhancement: DisposeScope recognition**
   - Suppress `SB1001` when assignment is inside an active `SwiftDisposeScope` using block
   - Existing code fix provider (`SwiftObjectDisposeCodeFixProvider`) already offers "Wrap in `using`" — enhance to also offer "Wrap in SwiftDisposeScope" when multiple undisposed locals in same block
   - **No other suppression changes** — keep the analyzer conservative (no field assignment, no method parameter heuristics)

5. **Integration with existing TestFramework**
   - Add DisposeScope usage examples to RuntimeTestsApp
   - Verify scope works with real Swift objects (classes, structs, protocol proxies)

**Key files**:
- New: `src/Swift.Runtime/src/Swift/Runtime/SwiftDisposeScope.cs`
- New: `src/Swift.Runtime/src/Swift/Runtime/SwiftDisposeScopeExtensions.cs`
- Modified: `src/Swift.Analyzers/SwiftObjectDisposeAnalyzer.cs` (DisposeScope recognition)
- Modified: `src/Swift.Analyzers/SwiftObjectDisposeCodeFixProvider.cs` (DisposeScope suggestion)
- Modified: Generator `ClassHandler.cs`, `NonFrozenStructHandler.cs`, `FrozenStructHandler.cs` (emit TryRegister in constructors/NewFromPayload)
- Modified: Generator `ProtocolProxyEmitter.Receivers.cs` (emit TryRegister in proxy constructors), `ProtocolProxyEmitter.SwiftObject.cs` (emit TryRegister in NewFromPayload)
- New: test files for DisposeScope + analyzer

**Validation**: `run-tests.sh` must pass. Runtime tests on iOS Simulator verify real Swift objects work with scope.

---

### Session 2: ARC Bridge for Swift Classes (End-to-End) — **Status: Complete** (commit `ba0afd9d`)

**Scope**: Full ARC bridge — runtime primitive (`SwiftClassHandle<T>`) + generator emission changes + golden file updates. One session, end-to-end.

**Deliverables**:

**Part A — Runtime Foundation**

1. **`SwiftHandle` / `Payload.DangerousGetHandle()` repo-wide audit**
   - Today `ISwiftObject.SwiftHandle` on classes returns a pointer-to-buffer, not the Swift object pointer
   - With `SwiftClassHandle<T>`, it returns the Swift object pointer directly — semantic change
   - Audit every consumer: `ConstrainedExistentialBridge`, `MarshalToSwift` implementations, protocol proxy marshalling, closure context marshalling, any code that dereferences as `*(IntPtr*)handle`
   - Document which paths need updating vs. which already assume raw object pointer
   - **This audit gates all subsequent work** — if the blast radius is too large, we may need a compatibility shim

2. **`SwiftClassHandle<T>`** in `Swift.Runtime`
   - SafeHandle that directly holds Swift class pointer (no buffer indirection)
   - `ReleaseHandle` calls `Arc.Release()` — relies on SafeHandle's built-in finalizer (no generated per-class finalizer needed)
   - No Mono/NativeAOT split needed (hypothesis — `Arc.Release` uses Cdecl, not CallConvSwift)
   - Debug diagnostics: `DebuggerDisplay`, retain count inspection

3. **Finalizer correctness validation — must pass before proceeding to Part B**
   - Unit test: create class handle, drop reference, force GC, verify Swift object released
   - Unit test: create class handle, call Dispose, verify released exactly once
   - Unit test: Dispose + finalization don't double-release (SafeHandle guarantees this, but verify)
   - **Critical**: verify `Arc.Release` via Cdecl from Mono finalizer thread does NOT crash
   - Unit test: retain count verification before/after release
   - Stress test: create 10,000 class handles, drop all, force GC, verify no leaks
   - If Mono finalizer test fails: fallback to NativeAOT-only finalizer cleanup (same as today's `SwiftDispose` split), document the limitation

4. **Compatibility**
   - `SwiftSafeHandle<T>` continues to work unchanged (structs still use it)
   - `ISwiftObject.SwiftHandle` works with both handle types
   - `SwiftMarshal.MarshalFromSwift<T>` updated to use `SwiftClassHandle<T>` when T is a class
   - `SwiftDispose.FinalizerCleanup` — no longer needed for classes (keep for structs)

**Part B — Generator Emission Changes**

5. **ClassHandler emission changes**
   - Root classes: emit `SwiftClassHandle<T> _handle` instead of `SwiftSafeHandle<T> _payload`
   - `_payloadSize` no longer needed for classes (no buffer allocation)
   - Constructor emission: receive `IntPtr` from P/Invoke, wrap in `SwiftClassHandle<T>`
   - `NewFromPayload`: create `SwiftClassHandle<T>` directly from pointer
   - `MarshalToSwift`: use `_handle.DangerousGetHandle()` directly (no buffer dereference)
   - No generated finalizer — SafeHandle's built-in finalizer calls `ReleaseHandle()` → `Arc.Release()`
   - No generated `Dispose()` body change — still calls `_handle.Dispose()` + `GC.SuppressFinalize(this)`
   - Derived classes: chain to base, same pattern
   - ObjC-rooted classes: unchanged (already use NSObject lifecycle)

6. **Wrapper emission changes** (Swift side — minimal)
   - `ConstructorWrapperEmitter`: class constructors already return `passRetained().toOpaque()` — **no Swift-side change needed**
   - `MethodWrapperEmitter`: class returns already use `passRetained().toOpaque()` — **no Swift-side change needed**
   - `PropertyWrapperEmitter`: same — **no Swift-side change needed**
   - `DestroyWrapperEmitter`: skip emission for classes (only structs need destroy wrappers now)
   - All class wrapper changes are **C#-side marshalling only** — this significantly reduces the blast radius

7. **PInvokeEmitter changes**
   - Class constructor P/Invoke: return `IntPtr` instead of `void` with result buffer param
   - Class return marshalling: wrap `IntPtr` in `SwiftClassHandle<T>` instead of buffer+NativeMemory.Alloc
   - Class input marshalling: `_handle.DangerousGetHandle()` instead of `_payload.DangerousGetHandle()`

8. **CdeclSignatureContract updates** (if session 2 of architecture review is done)
   - Class constructor signature: remove `resultPtr` from parameter list, add return type
   - Class method/property: no change (class returns already pointer-based)

9. **Golden file regeneration**
   - Regenerate all golden files
   - Verify generated code compiles

10. **XML doc comment updates**
    - Generated classes: update to reflect "Dispose optional for classes"
    - Remove "Use a 'using' block or call Dispose()" warning from class types
    - Keep it for struct types

**Key files** (runtime):
- New: `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs`
- Modified: `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs`
- Modified: `src/Swift.Runtime/src/Swift/Runtime/SwiftDispose.cs`

**Key files** (generator — C# emission changes only, no Swift wrapper changes for classes):
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/DestroyWrapperEmitter.cs` (skip for classes)
- Golden files in `TestFramework/golden/`

**Validation**: `run-tests.sh` must pass. `validate-libraries.sh` must show no regressions. `TestFramework/build-and-test.sh` for full integration. Runtime tests on iOS Simulator to verify class lifecycle works end-to-end.

**Critical verification**: Run on iOS Simulator to confirm `Arc.Release` from GC finalizer thread does NOT trigger the `jit-info.c:918` crash. This is the key assumption — `Arc.Release` uses `CallingConvention.Cdecl`, not `CallConvSwift`, so it should be safe. But we must verify early in Part A before proceeding to Part B.

---

### Session 3: Documentation + Cleanup

**Scope**: All documentation updates from sessions 1-2. Struct finalizer hardening. SB1001 severity split. No major new functionality.

**Deliverables**:

1. **Struct finalizer improvement on NativeAOT**
   - Current behavior: `SwiftDispose.FinalizerCleanup` calls `Dispose()` on NativeAOT, no-op on Mono
   - Improvement: route struct destroy through `@_cdecl` wrapper from finalizer (same safety as explicit Dispose)
   - This makes struct lifecycle match class lifecycle on NativeAOT: finalizer provides safety net
   - On Mono (simulator): still a no-op, but this is dev-only

2. **`ISwiftStruct` marker interface** (optional)
   - Distinguish structs from classes in the type system
   - Allows `SB1001` to adjust severity: Warning for structs, Info for classes
   - Allows DisposeScope to log warnings for undisposed structs but not classes

3. **SB1001 severity split** (only if ARC bridge finalizer is proven safe in Session 2)
   - Warning for struct types (disposal is important)
   - Info/Suggestion for class types (finalizer handles it)
   - If Mono finalizer safety was NOT verified: keep Warning for all types, add note in diagnostic message that classes have finalizer cleanup on NativeAOT

4. **Wiki Ownership page rewrite**
   - Restructure as described in section 4 above
   - Clear separation: "For app developers" vs "For binding authors" vs "For contributors"
   - Tone shift from "you must" to "best practice"

5. **Wiki Getting Started update**
   - Show DisposeScope usage in the quick-start example
   - Remove "critical disposal" warnings from the happy path

6. **Roadmap updates**
   - Update `src/docs/roadmap.md` to reflect ownership automation completion
   - Update `src/docs/swift-runtime-improvements.md`
   - Archive this design doc to `src/docs/Completed/`

**Key files**:
- `src/Swift.Runtime/src/Swift/Runtime/SwiftDispose.cs`
- `src/Swift.Analyzers/SwiftObjectDisposeAnalyzer.cs`
- Wiki files in `/Users/wojo/Dev/swift-dotnet-bindings.wiki/`
- `src/docs/roadmap.md`

**Validation**: `run-tests.sh`. Full validation pass. Wiki review.

---

## Impact Summary

| Before | After |
|--------|-------|
| Every Swift object needs `using` or `Dispose()` | Classes: no disposal needed (GC handles it) |
| Forgetting `Dispose()` = guaranteed leak | Forgetting `Dispose()` = non-deterministic cleanup (same as `FileStream`) |
| 10 ownership rules for consumers | 1 rule: use `Dispose()` for deterministic struct cleanup |
| No batch disposal | `SwiftDisposeScope` for batch operations |
| Analyzer warns but doesn't fix | Analyzer offers auto-fix + recognizes scopes |
| Wiki doc is scary | Wiki doc is familiar ("just like IDisposable") |

## Consumer Experience Goal

```csharp
// Casual usage (most common) — no disposal thinking needed:
var pipeline = ImagePipeline.Shared;
var image = await pipeline.GetImageAsync(url);
imageView.Image = image.ToUIImage();
// GC handles cleanup, Swift deinit runs on finalization

// Performance-sensitive (batch operations):
using (new SwiftDisposeScope())
{
    foreach (var url in urls)
    {
        var image = await pipeline.GetImageAsync(url);
        ProcessImage(image);
    }
    // All images disposed deterministically at scope exit
}

// Scarce resources (file handles, network connections):
using var handle = FileHandle.Create(path);
handle.Write(data);
// Deterministic close — same pattern as FileStream
```

This matches what .NET developers already know. No new mental model required.

---

## Appendix: Types That Still Require Explicit Disposal

Even after all improvements, these types benefit from explicit `Dispose()`:

| Type | C# Projection | Reason | Recommendation |
|------|--------------|--------|----------------|
| Non-frozen structs | `class` | VWT destroy needed, finalizer unreliable on Mono | Use `using` or DisposeScope |
| Frozen structs with ref fields | `class` | Same as above (ref-counted fields need deinit) | Use `using` or DisposeScope |
| Protocol proxies | `class` | Existential container + EveryProtocol wrapper | Use `using` or DisposeScope |
| Large collections of Swift objects | Mixed | GC non-determinism may delay cleanup | Use DisposeScope for batch processing |
| Objects holding scarce resources | Any | File handles, network connections, caches | Use `using` (same as any .NET resource) |

For frozen blittable structs (`struct`): no disposal needed at all (pure value types, empty `Dispose()` body).
For Swift classes (`class`): disposal optional after ARC bridge (GC finalizer handles `Arc.Release`).
