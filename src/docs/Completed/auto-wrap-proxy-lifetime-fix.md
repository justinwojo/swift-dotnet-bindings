# Auto-Wrap Proxy Lifetime Fix

**Priority**: P0 (must-fix before 1.0 — memory leak)
**Status**: Strategy locked (2026-04-06); ready to implement
**Effort**: Small-medium (~150–250 lines, touches Swift codegen + C# runtime + proxy ctor only)
**Risk**: Moderate. Proxy lifetime model changes, but call-site emission is unchanged — most of the 1300+ runtime test surface is not in the regression path.

## TL;DR for the next session

The shipped auto-wrap fix (commit `ad66dd21`) lets users assign plain C# implementations of generated protocol interfaces to delegate properties/ctor args/method args. It works correctly. **It also leaks one proxy per `(impl, protocol)` pair, permanently, until process exit.** The leak is bounded but real, and the user (Justin) considers it a ship-blocker.

**Resolved design (superseding the original plan below)**: anchor the proxy's +1 ARC retain on `EveryProtocol` to the user's `impl` lifetime via a `ConditionalWeakTable<impl, ProxyCleanup>`. When the impl is garbage-collected, a finalizer releases the +1. Combined with a Swift `EveryProtocol.deinit` callback (unchanged from the original plan) that drops the strong registry root when Swift's refcount → 0, this gives a complete lifetime story without touching any call-site emission.

The `BorrowedExistentialContainer1` per-call lease from the original plan **is not needed** and has been dropped. The strategy-lockdown audit (Codex Finding #4) showed the lease model's "release +1 after first handoff" pattern is unsafe for most Swift call sites (extensions, closures, non-storing methods don't retain on entry). The impl-anchored release model sidesteps that entirely — the +1 is released when no calls can be in flight (because impl GC requires no live stack references).

**Read the "## Strategy Lockdown (2026-04-06)" section below and implement from there.** The later "## The fix (Codex-validated direction)" section is the original plan and is preserved for context only — it's superseded.

---

## Why this exists

`ExistentialContainerFactory.GetOrCreate<T>(value, wrapFallback)` (in [`src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs`](../Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs#L1024)) is the auto-wrap entry point. When a user passes a plain C# class implementing a generated protocol interface, the factory calls `wrapFallback` to construct the hidden `{Protocol}Proxy` for them. The proxy registers itself with `SwiftObjectRegistry.RegisterStrong(handle, this)` so Swift callbacks can find it.

The shipped commit added a per-`(impl, protocol)` cache (`ConditionalWeakTable<object, ConcurrentDictionary<Type, Lazy<…>>>`) that prevents creating multiple proxies for the same `(impl, protocol)` pair. That helps with the "set the same delegate 1000 times" pattern but does NOT help with "1000 short-lived delegates assigned once each" — each one still leaks its own proxy.

The roadmap entry at [`src/docs/roadmap.md:97`](roadmap.md#L97) describes the residual leak.

---

## Current architecture (what's there today)

### The Swift side

[`EveryProtocolEmitter.cs:47-83`](../Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs#L47) emits this Swift class:

```swift
public final class EveryProtocol {
    public var handle: UnsafeRawPointer?
    public init() { self.handle = nil }
    public init(handle: UnsafeRawPointer) { self.handle = handle }
}

@_cdecl("SBW_CreateEveryProtocol")
public func _sbw_createEveryProtocol() -> UnsafeMutableRawPointer {
    let instance = EveryProtocol()
    return Unmanaged.passRetained(instance).toOpaque()  // <-- +1 retain
}

@_cdecl("SBW_ReleaseEveryProtocol")
public func _sbw_releaseEveryProtocol(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<EveryProtocol>.fromOpaque(ptr).release()
}
```

**Critical: there is no `deinit`.** When Swift's last reference to an `EveryProtocol` is dropped, the runtime deallocates it silently. C# never finds out.

### The C# proxy constructor

[`ProtocolProxyEmitter.Receivers.cs:778-814`](../Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs#L778) emits this constructor:

```csharp
public {ProxyClass}({Interface} implementation)
{
    _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));

    var everyProtocolPtr = NativeMethods.CreateEveryProtocol();  // +1 from Swift
    _everyProtocol = new EveryProtocol(everyProtocolPtr);         // C# wrapper holds +1

    try
    {
        if (EveryProtocol.GetTypeMetadata().Handle == IntPtr.Zero)
            EveryProtocol.SetTypeMetadata(NativeMethods.GetEveryProtocolMetadata());

        _swiftContainer = new ExistentialContainer1();
        _swiftContainer.Payload0 = _everyProtocol.Handle;
        // ... witness table init ...

        SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);  // <-- registry root
    }
    catch
    {
        _everyProtocol.Dispose();
        throw;
    }
    Swift.Runtime.SwiftDisposeScope.TryRegister(this);
}
```

`_everyProtocol` is a `private readonly EveryProtocol?` field (declared in [`ProtocolProxyEmitter.StaticInit.cs:29`](../Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs#L29)).

### The C# `EveryProtocol` wrapper

[`src/Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs:39-52`](../Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs#L39):

```csharp
private readonly SwiftClassHandle<EveryProtocol> _handle;

public EveryProtocol(IntPtr swiftPointer)
{
    _handle = new SwiftClassHandle<EveryProtocol>(swiftPointer);
}
```

`SwiftClassHandle<T>` (see [`src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs`](../Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs)) is a `SafeHandleZeroOrMinusOneIsInvalid` that takes ownership of a Swift `+1` retain and calls `Arc.Release` on dispose/finalize. So the C# proxy holds a permanent +1 on EveryProtocol via this field.

### `SwiftObjectRegistry`

[`src/Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs:24-64`](../Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs#L24):

```csharp
private static readonly ConcurrentDictionary<IntPtr, WeakReference<object>> _registry = new();
private static readonly ConcurrentDictionary<IntPtr, object> _strongRegistry = new();

public static void RegisterStrong<TProxy>(IntPtr handle, TProxy proxy) where TProxy : class
{
    _strongRegistry[handle] = proxy;       // <-- permanent root
    _registry[handle] = new WeakReference<object>(proxy);
}

public static void Unregister(IntPtr handle)
{
    _registry.TryRemove(handle, out _);
    _strongRegistry.TryRemove(handle, out _);
}
```

`_strongRegistry` is a static `ConcurrentDictionary` keyed by EveryProtocol handle. Once `RegisterStrong` puts the proxy in there, nothing ever removes it (for auto-wrapped proxies — manually-constructed proxies call `Dispose` which calls `Unregister`).

### The auto-wrap cache (just shipped)

[`src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs:974-1066`](../Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs#L974):

```csharp
private static readonly ConditionalWeakTable<object,
    ConcurrentDictionary<Type, Lazy<ISwiftExistentialConvertible<ExistentialContainer1>>>>
    s_autoWrapCache = new();

public static ExistentialContainer1 GetOrCreate<TProtocol>(
    TProtocol value,
    Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
    where TProtocol : class
{
    if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
        return convertible.GetExistentialContainer();

    if (value is IExistentialBoxable boxable)
        return boxable.BoxAsExistential1<TProtocol>();

    if (wrapFallback == null) throw new ArgumentNullException(nameof(wrapFallback));

    var perImplMap = s_autoWrapCache.GetValue(
        value,
        static _ => new ConcurrentDictionary<Type, Lazy<…>>());
    var lazy = perImplMap.GetOrAdd(
        typeof(TProtocol),
        _ => new Lazy<…>(() =>
        {
            var proxy = wrapFallback(value);
            if (proxy is IDisposable disposable)
                SwiftDisposeScope.Detach(disposable);  // cache owns lifetime
            return proxy;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
    return lazy.Value.GetExistentialContainer();
}
```

**Important constraint Codex flagged (P0)**: this cache holds the proxy STRONGLY through `Lazy<T>`. So even if we wired up the deinit callback to call `SwiftObjectRegistry.Unregister(handle)`, the cache would still root the proxy. The cache MUST also be evicted (or made weak) for the fix to work.

### The leak, drawn out

```
_strongRegistry[handle]  ─┐
                          ├──> proxy ──┬──> _everyProtocol ──> SwiftClassHandle ──> +1 retain ──> Swift EveryProtocol
s_autoWrapCache[impl][T] ─┘            └──> _csharpImpl ──> user's C# class

Swift EveryProtocol has no deinit, so Swift never tells C# when it releases its container.
The C# +1 retain prevents EveryProtocol's refcount from reaching zero anyway.
Result: handle stays valid forever, _strongRegistry root never drops, proxy never collected.
```

---

## Strategy Lockdown (2026-04-06)

This section is the authoritative design. It supersedes the "## The fix (Codex-validated direction)" section below, which is preserved for historical context only. The lockdown session resolved the open questions, ran the Codex Finding #4 cdecl audit, and simplified the lifetime model by dropping the per-call lease abstraction entirely.

### What changed vs. the original design

1. **`BorrowedExistentialContainer1` is dropped.** There is no per-call lease, no `using` blocks at call sites, no disposable ref-struct. The six+ generator emit sites that were going to need hand-editing (`MethodSignature.cs`, `WrapperEmitter.Marshalling.cs`, `ClosureEmitter*`, `EnumHandler.CaseConstruction.cs`, `PropertyHandler.cs`, `ExistentialProjection.cs`) stay as-is.
2. **The proxy's +1 retain is anchored to the user's `impl` lifetime** via a new `ProxyLifetimeTracker` runtime type wrapping a `ConditionalWeakTable<object, ProxyCleanup>`. When the `impl` is GC'd, `ProxyCleanup`'s finalizer calls `Arc.Release` on the tracked EveryProtocol handle(s).
3. **The Swift `EveryProtocol.deinit` callback is still part of the fix** (unchanged from the original plan). It fires when Swift's refcount reaches 0 and calls back into C# to drop the `SwiftObjectRegistry._strongRegistry` root and clean up weak cache entries.
4. **The auto-wrap cache becomes weak** (Option A from the original plan — the only cache option that survived lockdown).
5. **The proxy's `_everyProtocol` field changes from `SwiftClassHandle<EveryProtocol>` to a plain `IntPtr _everyProtocolHandle`.** The proxy no longer owns the retain via a SafeHandle; ownership of the release path moves to `ProxyLifetimeTracker`. Proxy `Dispose()` no longer calls `Arc.Release` — it only needs to unregister from the strong registry (the tracker will handle release when impl is collected, and explicit `Dispose()` can additionally hand off early).

### Why the lease model was dropped

The original plan said: make the proxy hold 0-retain, let each call site bump a lease retain for the call duration, and release after the P/Invoke returns. The plan relied on the assumption that Swift would take its own retain during the call (via store or copy), so dropping the lease's retain after the call would be safe.

**Two fatal problems, discovered during the lockdown:**

- **Unresolved initial-retain ownership.** `SBW_CreateEveryProtocol` returns a +1 retain from `Unmanaged.passRetained(instance).toOpaque()`. In the original plan, the proxy is 0-retain and leases are balanced (retain + release). That leaves the initial +1 with no owner. Releasing it in the constructor would drop the refcount to 0 and deallocate EveryProtocol immediately; holding it in a SafeHandle re-creates the original leak. The original plan never resolved this; trying to write it out during lockdown exposed the circular dependency.

- **Swift does not retain existentials on entry for most call sites.** The cdecl audit (full results in "### Audit findings" below) confirmed that Swift's protocol extension wrappers, closure callbacks, and borrow-style method parameters do NOT implicitly retain the existential container's payload when receiving it as a parameter. Only property setters (which trigger VWT-copy via the `didSet`/stored-property retain) reliably retain. The lease model's "release after first handoff" pattern would therefore be correct for property setters and broken for everything else — meaning it couldn't actually be used uniformly.

Codex Finding #4 flagged the second problem ("your first release after handoff is only correct if every generated path copies/retains the existential before control returns or throws"). The audit confirmed the worry was real across most emit sites.

The impl-anchored model sidesteps both problems. The +1 is never dropped at call boundaries — it is released when the user's impl is GC'd. That can only happen when there are no live stack references to impl, which means no in-flight calls can be passing impl's proxy to Swift. Swift's own retain behavior (whatever it does) is irrelevant to the lifetime of our ground-state +1.

### Resolved design

Three orthogonal mechanisms, each with a clearly scoped responsibility:

1. **`ProxyLifetimeTracker`** (new runtime type, `src/Swift.Runtime/src/Swift/Runtime/ProxyLifetimeTracker.cs`) — owns the impl-anchored +1 release path. When a proxy is constructed with a fresh EveryProtocol, the tracker associates the handle with the user's impl via `ConditionalWeakTable`. When impl is GC'd, the tracker's finalizer releases the +1(s).

2. **Swift `EveryProtocol.deinit` callback** — drops the `SwiftObjectRegistry._strongRegistry` root when Swift's refcount reaches 0. Unchanged from the original plan except that it now runs only after `ProxyLifetimeTracker` has released the ground-state +1 (the expected order of events).

3. **Weak auto-wrap cache** — `s_autoWrapCache` becomes `ConditionalWeakTable<object, ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>>`. Cache hits do a `TryGetTarget` liveness check and rebuild on stale entries. No reverse map needed — the strong registry is the single source of truth for proxy liveness.

#### `ProxyLifetimeTracker` (sketch)

```csharp
// src/Swift.Runtime/src/Swift/Runtime/ProxyLifetimeTracker.cs
internal static class ProxyLifetimeTracker
{
    // Primary: weakly keyed by impl. Value becomes collectible when impl is GC'd.
    private static readonly ConditionalWeakTable<object, ProxyCleanup> s_tracker = new();

    // Secondary: handle -> weak impl ref. Used ONLY by the deinit callback to
    // locate the ProxyCleanup entry for targeted removal, so normal-path releases
    // don't double-release when Swift-driven deinit races with impl GC.
    private static readonly ConcurrentDictionary<IntPtr, WeakReference<object>> s_handleToImpl = new();

    public static void Track(object impl, IntPtr handle)
    {
        var cleanup = s_tracker.GetValue(impl, static _ => new ProxyCleanup());
        cleanup.Add(handle);
        s_handleToImpl[handle] = new WeakReference<object>(impl);
    }

    /// <summary>Called by OnEveryProtocolDeinit so ProxyCleanup's finalizer doesn't double-release.</summary>
    public static void NotifyDeinit(IntPtr handle)
    {
        if (s_handleToImpl.TryRemove(handle, out var weak) && weak.TryGetTarget(out var impl))
        {
            if (s_tracker.TryGetValue(impl, out var cleanup))
                cleanup.Remove(handle);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEveryProtocolDeinit(IntPtr handle)
    {
        if (SwiftExitGuard.IsProcessExiting) return;  // shutdown safety
        try
        {
            SwiftObjectRegistry.Unregister(handle);
            NotifyDeinit(handle);
            // Weak cache entries for this handle expire on next access; no eager eviction needed.
        }
        catch
        {
            // Non-throwing across Swift ABI boundary.
        }
    }

    private sealed class ProxyCleanup
    {
        private readonly List<IntPtr> _handles = new();
        private readonly object _lock = new();

        public void Add(IntPtr h) { lock (_lock) _handles.Add(h); }
        public void Remove(IntPtr h) { lock (_lock) _handles.Remove(h); }

        ~ProxyCleanup()
        {
            // Align with SwiftClassHandle.ReleaseHandle precedent: skip native releases
            // during process exit because the Swift runtime may be partially torn down.
            if (SwiftExitGuard.IsProcessExiting) return;
            lock (_lock)
            {
                foreach (var h in _handles)
                {
                    try { Arc.Release(h); }
                    catch { /* already deinit'd via a race; ignore */ }
                }
            }
        }
    }
}
```

**Invariant**: every proxy constructor that creates a fresh EveryProtocol via `SBW_CreateEveryProtocol` MUST call `ProxyLifetimeTracker.Track(implementation, handle)` immediately after registering with `SwiftObjectRegistry.RegisterStrong`. The manual second ctor (the one taking an existing `ExistentialContainer1 container`) does NOT call Track — that path uses a Swift-owned container whose lifetime is not our responsibility.

#### Proxy ctor changes (`ProtocolProxyEmitter.Receivers.cs:778-814`)

```csharp
// BEFORE
public {ProxyClass}({Interface} implementation)
{
    _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
    var everyProtocolPtr = NativeMethods.CreateEveryProtocol();
    _everyProtocol = new EveryProtocol(everyProtocolPtr);   // SafeHandle owns +1
    try {
        /* ... metadata init, container build, witness tables ... */
        SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
    }
    catch { _everyProtocol.Dispose(); throw; }
    Swift.Runtime.SwiftDisposeScope.TryRegister(this);
}

// AFTER
public {ProxyClass}({Interface} implementation)
{
    _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
    _everyProtocolHandle = NativeMethods.CreateEveryProtocol();  // plain IntPtr, +1 from passRetained
    try {
        /* ... metadata init, container build, witness tables ... */
        SwiftObjectRegistry.RegisterStrong(_everyProtocolHandle, this);

        // Wire Swift -> C# deinit callback so Swift refcount -> 0 drops the strong registry root.
        unsafe {
            NativeMethods.SetEveryProtocolDeinitCallback(
                _everyProtocolHandle,
                &Swift.Runtime.ProxyLifetimeTracker.OnEveryProtocolDeinit,
                _everyProtocolHandle);  // context arg is the handle itself
        }

        // Anchor the ground-state +1 to the impl lifetime. When impl is GC'd,
        // ProxyCleanup's finalizer releases the +1, allowing Swift's refcount
        // to eventually reach 0 and fire deinit.
        Swift.Runtime.ProxyLifetimeTracker.Track(implementation, _everyProtocolHandle);
    }
    catch {
        Arc.Release(_everyProtocolHandle);
        throw;
    }
    Swift.Runtime.SwiftDisposeScope.TryRegister(this);
}
```

Field changes in `ProtocolProxyEmitter.StaticInit.cs:29`:

```csharp
// BEFORE
private readonly EveryProtocol? _everyProtocol;

// AFTER
private readonly IntPtr _everyProtocolHandle;
```

Dispose path in `ProtocolProxyEmitter.SwiftObject.cs:86-107` simplifies — the tracker owns the release:

```csharp
// BEFORE
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    GC.SuppressFinalize(this);
    if (_everyProtocol != null)
    {
        SwiftObjectRegistry.Unregister(_everyProtocol.Handle);
        _everyProtocol.Dispose();
    }
}

// AFTER
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    GC.SuppressFinalize(this);
    // Do NOT release the +1 here — ProxyLifetimeTracker owns it via the impl-keyed
    // ConditionalWeakTable. Explicit Dispose still unregisters from the strong
    // registry so further Swift callbacks route to a definite no-op, but the
    // ARC release waits for impl GC (or the deinit callback if Swift drops its
    // last ref before impl is collected).
    if (_everyProtocolHandle != IntPtr.Zero)
        SwiftObjectRegistry.Unregister(_everyProtocolHandle);
}
```

The finalizer warning message is no longer accurate — the proxy is not required to be disposed, and finalization is not a leak. Drop the warning:

```csharp
// AFTER
~{ProxyClass}() { /* No-op: ProxyLifetimeTracker handles the +1 release path. */ }
```

Or remove the finalizer entirely (the proxy holds no unmanaged resources directly anymore — the IntPtr is a plain value, not owned).

#### Swift `EveryProtocol.deinit` + `SBW_SetEveryProtocolDeinitCallback`

Edit `EveryProtocolEmitter.cs` to emit:

```swift
public final class EveryProtocol {
    public var handle: UnsafeRawPointer?
    fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
    fileprivate var onDeinitCtx: UnsafeRawPointer?

    public init() { self.handle = nil }
    public init(handle: UnsafeRawPointer) { self.handle = handle }

    deinit {
        // Idempotent, non-throwing. Fires when Swift's last retain drops.
        if let cb = onDeinit, let ctx = onDeinitCtx {
            cb(ctx)
        }
    }
}

@_cdecl("SBW_CreateEveryProtocol")
public func _sbw_createEveryProtocol() -> UnsafeMutableRawPointer {
    let instance = EveryProtocol()
    return Unmanaged.passRetained(instance).toOpaque()
}

@_cdecl("SBW_ReleaseEveryProtocol")
public func _sbw_releaseEveryProtocol(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<EveryProtocol>.fromOpaque(ptr).release()
}

@_cdecl("SBW_SetEveryProtocolDeinitCallback")
public func _sbw_setEveryProtocolDeinitCallback(
    _ instance: UnsafeMutableRawPointer,
    _ callback: @convention(c) (UnsafeRawPointer) -> Void,
    _ context: UnsafeRawPointer
) {
    // takeUnretainedValue — we're only reading a property reference, not adding a ref.
    // The caller (C# proxy ctor) already owns a +1 via SBW_CreateEveryProtocol.
    let ep = Unmanaged<EveryProtocol>.fromOpaque(instance).takeUnretainedValue()
    ep.onDeinit = callback
    ep.onDeinitCtx = context
}
```

The corresponding C# P/Invoke lives under `NativeMethods` in the runtime (or generated alongside `CreateEveryProtocol`); signature:

```csharp
[DllImport(..., CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_SetEveryProtocolDeinitCallback")]
internal static extern unsafe void SetEveryProtocolDeinitCallback(
    IntPtr instance,
    delegate* unmanaged[Cdecl]<IntPtr, void> callback,
    IntPtr context);
```

#### Cache — Option A (weak)

Replace `ExistentialContainer.cs:974-1066` with:

```csharp
private static readonly ConditionalWeakTable<object,
    ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>>
    s_autoWrapCache = new();

public static ExistentialContainer1 GetOrCreate<TProtocol>(
    TProtocol value,
    Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
    where TProtocol : class
{
    if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
        return convertible.GetExistentialContainer();
    if (value is IExistentialBoxable boxable)
        return boxable.BoxAsExistential1<TProtocol>();
    if (wrapFallback == null) throw new ArgumentNullException(nameof(wrapFallback));

    var perImplMap = s_autoWrapCache.GetValue(
        value,
        static _ => new ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>());

    // Fast path: live weak hit.
    if (perImplMap.TryGetValue(typeof(TProtocol), out var weak) &&
        weak.TryGetTarget(out var cached))
    {
        return cached.GetExistentialContainer();
    }

    // Miss or stale: rebuild.
    var proxy = wrapFallback(value);
    if (proxy is IDisposable disposable)
        SwiftDisposeScope.Detach(disposable);
    perImplMap[typeof(TProtocol)] = new WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>(proxy);
    return proxy.GetExistentialContainer();
}
```

**Known benign race**: two threads concurrently reach "stale or miss" for the same `(impl, protocol)` and both build proxies. The losing proxy becomes a weak-cache orphan but is still tracked by `ProxyLifetimeTracker`, so its +1 is released when impl is GC'd, and its strong-registry entry drops via its own deinit callback. Wasted allocation, no correctness issue. The original `Lazy<T>` synchronization prevented this at the cost of strongly caching the proxy; with Option A the strong registry is the authoritative alive-root, so the race is acceptable.

### Lifetime scenarios (verification)

| # | Scenario | Outcome |
|---|---|---|
| A | User sets `obj.delegate = impl`, then sets `obj.delegate = nil`, then drops `impl` | Swift's setter retains (refcount=2 during set). Clearing releases (refcount=1 = tracker's +1). impl GC → tracker releases +1 → refcount=0 → deinit → unregister → proxy collectible. ✓ |
| B | User passes `impl` to a non-storing method, method returns, user drops `impl` | Swift does not retain during call. Stack holds impl during call. After return, tracker's +1 is still there (refcount=1). impl GC → tracker releases → refcount=0 → deinit → unregister. ✓ |
| C | User passes same `impl` to the same protocol twice | First pass: cache miss, build proxy, register, track. Second pass: weak cache hit, TryGetTarget succeeds, reuse. ✓ |
| D | Cache stale: impl still alive but proxy's strong-registry entry was dropped (only possible if Swift's refcount somehow hit 0 while impl lived — not reachable in practice, since tracker's +1 keeps refcount≥1 until impl GC). No rebuild needed in practice. ✓ |
| E | User constructs proxy manually, never passes to Swift, drops impl | ctor: +1 from passRetained, tracker stores, strong registry stores, deinit callback wired. impl GC → tracker releases → refcount=0 → deinit → unregister → proxy collectible. ✓ |
| F | Process exit mid-finalization | `SwiftExitGuard.IsProcessExiting` short-circuits `ProxyCleanup.~Finalizer` and `OnEveryProtocolDeinit`. Pointers leak but so does the rest of the process state. ✓ |
| G | Swift-first race (Swift deinit fires before impl GC) | Not reachable: tracker's +1 keeps refcount≥1 until impl GC. Swift can only reach 0 after tracker releases. ✓ |
| H | In-flight call during impl GC | Not reachable: the user's stack frame that invoked the call holds `impl` alive (it was passed as an argument or field read). GC can only collect impl after the call returns. ✓ |

### Audit findings — Codex Finding #4 (cdecl wrapper retention invariant)

The audit walked every `@_cdecl` and `@_silgen_name` Swift wrapper that receives an `ExistentialContainer1` parameter, plus every C# `GetOrCreate` emit site. Key results:

**Swift-side retention behavior of existential parameters**:

| Wrapper kind | Retains payload on entry? | File reference |
|---|---|---|
| Protocol proxy receiver (`Receive_*_get/set/method`) | **No** — deref `selfContainer` to stack copy, extract proxy, dispatch. Not a concern for our fix (these are callbacks INTO C#, not C#→Swift handoffs). | `ProtocolProxyEmitter.Receivers.cs:105-399` |
| Protocol extension method wrapper (`@_silgen_name SBW_*`) | **No** — existential passed as value-type parameter, forwarded to instance method. Relies on the instance method to copy if needed. | `ProtocolExtensionEmitter.cs:1170-1412` |
| Method `@_cdecl` wrapper with existential parameter | **Blocked** — `WrapperEmitter` explicitly rejects these. They fall back to CallConvSwift P/Invokes. | `MethodWrapperEmitter.cs:1155-1170` |
| Closure callback with existential parameter | **No** — closure body receives container by value, no explicit retain. | `ClosureEmitter.cs`, `ClosureEmitter.StructParams.cs` |
| Enum case constructor with existential | **No** — constructor receives container, passes to enum case associated value via ordinary move semantics. | `EnumHandler.CaseConstruction.cs` |
| Property setter (non-cdecl, direct store) | **Yes** — VWT copy on store retains the payload. | N/A (standard Swift) |

**Verdict**: the "release after first handoff" pattern from the original `BorrowedExistentialContainer1` design would be safe ONLY for property setters. Every other call site (methods, closures, extension wrappers, enum constructors) would crash with a use-after-free if the lease released the proxy's +1 after the call and Swift had not independently retained.

**This is why the lockdown dropped the lease model.** The impl-anchored tracker model doesn't depend on this invariant at all — the +1 is held for the entire impl lifetime, so Swift's per-call retention behavior is irrelevant.

**`GetOrCreate` emit sites found** (13 total across 9 files — listed for completeness, but note that NONE of these need to change under the resolved design):

1. `Marshaler/Projection/ExistentialProjection.cs:60` — projection-based parameter marshalling
2. `Marshaler/Projection/ExistentialProjection.cs:99` — projection-based element conversion
3. `Emitter/StringEmitter/Handler/MethodSignature.cs:182-183` — method parameter marshalling
4. `Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs:442-443` — @_cdecl wrapper marshalling (heap-allocated)
5. `Emitter/StringEmitter/Handler/PropertyHandler.cs:952-953` — property setter (heap-allocated with try/finally)
6. `Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs:374-375` — enum case (heap-allocated)
7. `Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs:850-851` — enum case (direct)
8. `Emitter/StringEmitter/ClosureEmitter.StructParams.cs:91-97` — closure param (struct vtable dispatch)
9. `Emitter/StringEmitter/ClosureEmitter.StructParams.cs:268-274` — closure param (direct dispatch)
10. `Emitter/StringEmitter/ClosureEmitter.cs:389-395` — closure return value (single)
11. `Emitter/StringEmitter/ClosureEmitter.cs:446-452` — closure return value (tuple element)
12. `Emitter/StringEmitter/ClosureEmitter.cs:940-946` — unsafe closure param
13. `Emitter/StringEmitter/ClosureEmitter.cs:978-984` — unsafe closure param (tuple element)

All 13 continue to emit `ExistentialContainerFactory.GetOrCreate<T>(value, wrapFallback)` unchanged. The `GetOrCreate` signature is unchanged; only the cache internals change.

### Open questions (resolved)

1. **Does Swift `EveryProtocol` need to be a `final class` to support deinit callbacks?**
   **Resolved: no constraint.** Final classes in Swift have deinit — `final` only prevents subclassing, not instance lifetime hooks. Keep `final`.

2. **Does adding `deinit` to `EveryProtocol` change the type's ABI?**
   **Resolved: low risk, but verify via `nuke validate`.** Swift's `HeapMetadata` contains the destructor function pointer; adding deinit populates a non-null destructor slot where the compiler-generated default was previously a no-op destructor. The witness tables, vtable layout, and protocol conformance descriptors are unaffected. The proxy metadata lookup is via symbol, not layout offset. Validation gate (`nuke validate`) catches any regression across ~90 real-world library targets.

3. **`Arc.Retain` cost per call (microbenchmark concern)**.
   **Resolved: not applicable.** No per-call retain/release overhead in the resolved design. Call sites are unchanged. The only extra runtime work is a single `ConditionalWeakTable.GetValue` + `List<IntPtr>.Add` on proxy construction, which is orders of magnitude less frequent than per-call.

4. **`ConditionalWeakTable<object, ConcurrentDictionary<...>>` with weak inner values**.
   **Resolved: supported.** The CWT holds the inner dict strongly relative to the key. `WeakReference<T>` inside the inner dict is an ordinary managed weak reference, unaffected by the CWT. No semantic concerns. Confirmed by the pattern already working in the current (strong-Lazy) cache — only the inner reference kind is changing.

5. **Reverse map sync race window** (from the original Option B design).
   **Resolved: N/A, Option B not chosen.** Option A (weak cache) requires no reverse map. The secondary `s_handleToImpl` in `ProxyLifetimeTracker` is not the same thing — it's populated eagerly in `Track` and drained in `NotifyDeinit`, with no race between insertion and lookup because the deinit callback can only fire after construction has fully completed (Swift holds the retain that would be required to trigger deinit).

6. **Process-exit ordering** (when Swift runtime is torn down before C# managed heap).
   **Resolved: follow `SwiftExitGuard` precedent.** Both `ProxyCleanup.~Finalizer` and `OnEveryProtocolDeinit` short-circuit on `SwiftExitGuard.IsProcessExiting`, mirroring `SwiftClassHandle.ReleaseHandle:100`. Call `SwiftExitGuard.EnsureInitialized()` from the runtime's module initializer if it isn't already (verify during implementation — grep for existing calls).

7. **`takeUnretainedValue` in `SBW_SetEveryProtocolDeinitCallback`**.
   **Resolved: correct.** `takeUnretainedValue()` converts the opaque pointer to a Swift reference without bumping retain. We're only reading the instance to set two fields (`onDeinit`, `onDeinitCtx`) — no new ownership. The caller's +1 (from `SBW_CreateEveryProtocol`) is untouched. `takeRetainedValue()` would be wrong — it would consume the caller's +1 and leave the caller with a dangling pointer.

### Cache decision: Option A (weak cache)

**Locked in.** Rationale:

- `SwiftObjectRegistry._strongRegistry` is the single source of truth for "is this proxy alive?" The cache is a pure memoization optimization. Making it weak lets the registry own liveness and the cache stay simple.
- No reverse map required (the `s_handleToImpl` secondary map in `ProxyLifetimeTracker` is for deinit-callback coordination, not cache eviction).
- Dead weak-ref hits rebuild on next access — the only cost is one extra `TryGetTarget` per access.
- Benign race on concurrent "stale or miss" rebuilds — losing proxies become orphans but are still tracked and cleaned up correctly.
- Codex Finding #3 also recommended Option A.

Option B (strong cache + reverse map eviction) was rejected because it adds state with no measurable benefit — the cache-hit path cost difference is negligible and Option B's reverse-map sync race (Open question #5) is a real complexity cost.

### Resolved implementation order

Dependencies flow top-to-bottom. Each step should be tested before the next (fast feedback via `nuke test`, end-of-session gates via `nuke binding-tests` + `nuke validate`).

1. **Runtime: add `ProxyLifetimeTracker`** (`src/Swift.Runtime/src/Swift/Runtime/ProxyLifetimeTracker.cs`). Unit-test Track → simulate impl GC (force `GC.Collect()` + `WaitForPendingFinalizers()`) → verify Arc.Release call via a stub handle.

2. **Runtime: confirm `SwiftExitGuard.EnsureInitialized()` is called during module init.** Grep; add if missing. Cheap baseline.

3. **Runtime: update `ExistentialContainerFactory.GetOrCreate`** to use the weak cache (Option A). Unit tests for cache hit, cache miss, and stale hit rebuild. Run `nuke test`.

4. **Swift codegen: update `EveryProtocolEmitter.cs`** to emit `deinit`, the `onDeinit`/`onDeinitCtx` fields, and `SBW_SetEveryProtocolDeinitCallback`. Run `nuke validate` to confirm no ABI regressions across the validation libraries.

5. **Runtime: add the `SetEveryProtocolDeinitCallback` P/Invoke declaration** alongside `CreateEveryProtocol` in the runtime's NativeMethods (or wherever `CreateEveryProtocol` is declared — search for it and co-locate).

6. **Codegen: update the proxy emitter**:
   - `ProtocolProxyEmitter.StaticInit.cs:29` — change `_everyProtocol` field to `_everyProtocolHandle` (IntPtr).
   - `ProtocolProxyEmitter.Receivers.cs:778-814` — rewrite the C#-impl ctor per the "Proxy ctor changes" snippet above.
   - `ProtocolProxyEmitter.SwiftObject.cs:86-107` — simplify Dispose/finalizer; drop the SafeHandle path.
   - Grep for any other generated-code references to `_everyProtocol` (the field name). The manual container ctor at `ProtocolProxyEmitter.Receivers.cs:826-833` sets `_everyProtocol = null` — update to `_everyProtocolHandle = IntPtr.Zero`.

7. **Run `nuke binding-tests`** to catch regressions in the generator emission. Then `nuke runtime-tests-simulator` for the 1300+ runtime tests on Mono JIT.

8. **Run `nuke runtime-tests-device`** (full, not filtered) — the deinit callback path uses `[UnmanagedCallersOnly]`, and NativeAOT has historically had subtle bugs with reverse-P/Invoke from Swift release threads. This is the higher-risk runtime environment.

9. **Add new lifetime tests** (see "### Lifetime tests" below). Run against both sim and device.

10. **Run `nuke validate`** as the final compile-gate across all validation libraries.

11. **Delete the residual-leak entry from `src/docs/roadmap.md`** (line 97 per the original doc — verify the line before editing). Move THIS doc to `src/docs/Completed/`.

### Lifetime tests (new, add alongside existing `AutoWrappedDelegateTests`)

Add to `BindingTests/RuntimeTestsApp/Lifetime/ProxyLifetimeTests.cs` (or extend the existing `ProxyDisposeTests.cs`):

1. **One-shot method parameter deallocation**: pass a delegate to a method, drop the C# reference, `GC.Collect()` + `WaitForPendingFinalizers()`, assert `SwiftObjectRegistry.StrongCount` returns to baseline.
2. **Weak delegate set/clear**: assign delegate to a Swift property, then assign `null`, GC, assert the proxy is unregistered and the Swift-side allocation counter decremented.
3. **Overwrite `implA → implB → nil`**: assign first impl, then second, then null. GC. Both proxies unregistered.
4. **Cache reuse after deinit**: assign impl, clear, GC (proxy should unregister), reassign same impl. Cache should return a freshly built proxy (weak-ref rebuild path). Assert the new proxy works and its handle is different from the first.
5. **Cross-thread final release from Swift**: trigger Swift to release the container on a non-main thread (e.g., via a closure invoked on a Swift dispatch queue). Assert `OnEveryProtocolDeinit` runs without crashing on both Mono and NativeAOT.
6. **Process-exit safety** (smoke): ensure `ProxyCleanup.~Finalizer` doesn't crash during shutdown. Hard to write a true shutdown test — cover via manual inspection + the existing `SwiftClassHandle` shutdown test pattern in `SwiftClassHandleTests.cs:228-259`.

Assertions should hit both:
- **Swift-side deinit counters** (pattern from `OwnershipTests.swift:9-40` — add a counter to `EveryProtocol` test fixtures).
- **`SwiftObjectRegistry.StrongCount`** returning to baseline (pattern from `ProxyDisposeTests.cs:124-135`).

Keep one device test in the rotation for (5) specifically — NativeAOT reverse-P/Invoke from Swift release thread is the failure mode the sim cannot catch.

### Updated success criteria

- Auto-wrapped proxies are unregistered from `_strongRegistry` when EITHER the user's impl is GC'd OR Swift releases its last reference to the existential container, whichever comes first (in practice: impl GC triggers the release chain).
- `SwiftObjectRegistry.StrongCount` returns to baseline after a delegate is set, then cleared, then GC'd (with `WaitForPendingFinalizers`).
- All existing runtime tests continue to pass on both iOS Simulator (Mono) and iOS Device (NativeAOT).
- The 6 new lifetime tests pass on Sim; at least test (5) passes on Device.
- `nuke validate` compile gate clean (no regressions).
- The residual-leak entry at `src/docs/roadmap.md:97` is removed.
- This doc is moved to `src/docs/Completed/`.
- `BorrowedExistentialContainer1` is NOT added. `MarshalPlan.UsingDeclarations` is NOT used by `ExistentialProjection`. No call sites gain `using` blocks.

---

## The fix (Codex-validated direction)

**⚠ SUPERSEDED by "## Strategy Lockdown (2026-04-06)" above.** This section is the original design. It was preserved to document the reasoning that was rejected and to keep Codex's review in context. Do not implement from this section — the resolved design is simpler and lives in the lockdown section above. The implementation order, test plan, and success criteria have all been updated up there.

### High-level architecture

Three changes that have to land together:

1. **Swift `EveryProtocol` gets a `deinit` + per-instance callback registration.** When Swift's last ref drops, deinit runs and calls back into C# with the handle.
2. **The C# proxy stops holding the +1 retain permanently.** The `_everyProtocol` SafeHandle is replaced by a per-call "borrowed handoff lease" — every time the proxy hands its existential container to a P/Invoke, it bumps the retain, and the call site releases after the P/Invoke returns.
3. **The auto-wrap cache becomes weak** (or gains explicit eviction on deinit). When the deinit callback fires and unregisters the proxy from `_strongRegistry`, the cache must also stop rooting it, or nothing gets collected.

Codex's strongly-worded recommendation (Finding #7): **introduce a `BorrowedExistentialContainer1` runtime type — a disposable struct/class that wraps a container + a release token — and route all generated existential parameter sites through it via `MarshalPlan`.** Don't try to wrap arbitrary P/Invokes in lambdas.

### Step 1 — Swift side

[`EveryProtocolEmitter.cs`](../Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs):

```swift
public final class EveryProtocol {
    public var handle: UnsafeRawPointer?
    fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
    fileprivate var onDeinitCtx: UnsafeRawPointer?

    public init() { self.handle = nil }
    public init(handle: UnsafeRawPointer) { self.handle = handle }

    deinit {
        // Idempotent, non-throwing. Tells C# to drop _strongRegistry[handle].
        if let cb = onDeinit, let ctx = onDeinitCtx {
            cb(ctx)
        }
    }
}

@_cdecl("SBW_SetEveryProtocolDeinitCallback")
public func _sbw_setEveryProtocolDeinitCallback(
    _ instance: UnsafeMutableRawPointer,
    _ callback: @convention(c) (UnsafeRawPointer) -> Void,
    _ context: UnsafeRawPointer
) {
    // Use takeUnretainedValue — we're just storing fields, not adding a ref.
    let ep = Unmanaged<EveryProtocol>.fromOpaque(instance).takeUnretainedValue()
    ep.onDeinit = callback
    ep.onDeinitCtx = context
}
```

**Constraints from Codex Finding #8**: the C# callback must stay `static`, non-generic, blittable-only, idempotent, and non-throwing. See the [`UnmanagedCallersOnly` docs](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute?view=net-10.0).

### Step 2 — C# runtime callback

Add to `SwiftObjectRegistry` (or a new `EveryProtocolDeinit` static class):

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void OnEveryProtocolDeinit(IntPtr handle)
{
    // Idempotent, non-throwing. Called from arbitrary Swift release thread.
    try
    {
        SwiftObjectRegistry.Unregister(handle);
        ExistentialContainerFactory.EvictAutoWrapEntryByHandle(handle);
    }
    catch
    {
        // Swallow — can't propagate exceptions across the Swift ABI boundary.
    }
}
```

The proxy constructor calls `SetEveryProtocolDeinitCallback(handle, &OnEveryProtocolDeinit, handle)` after creating the EveryProtocol but before returning. The callback's context arg IS the handle, so the unregister knows what to clean up.

**Codex Finding #10**: be aware of [`SwiftClassHandle.cs:96-104`](../Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs#L96)'s process-exit guard. During shutdown, finalizer-triggered releases are skipped to avoid Swift-runtime-teardown crashes. Our deinit callback path needs to be prepared for late-process no-op behavior, and for double-cleanup races between explicit `Dispose()` paths and Swift-driven deinit.

### Step 3 — `BorrowedExistentialContainer1`

This is the key abstraction Codex recommended (Finding #7). A new runtime type in `src/Swift.Runtime/src/Swift/Runtime/`:

```csharp
/// <summary>
/// A short-lived lease on an ExistentialContainer1 for one Swift call.
/// Bumps the EveryProtocol retain on construction, releases on Dispose.
/// The generator emits this at every existential parameter site so the
/// proxy's lifetime is owned by Swift's actual usage, not by the C# proxy
/// instance.
/// </summary>
public readonly ref struct BorrowedExistentialContainer1 : IDisposable
{
    public ExistentialContainer1 Container { get; }
    private readonly IntPtr _handleToRelease;  // Zero if no release needed (e.g. already-convertible path)

    internal BorrowedExistentialContainer1(ExistentialContainer1 c, IntPtr handleToRelease)
    {
        Container = c;
        _handleToRelease = handleToRelease;
    }

    public void Dispose()
    {
        if (_handleToRelease != IntPtr.Zero)
            Arc.Release(_handleToRelease);
    }
}
```

`ExistentialContainerFactory.GetOrCreate` becomes:

```csharp
public static BorrowedExistentialContainer1 BorrowOrCreate<TProtocol>(
    TProtocol value,
    Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
    where TProtocol : class
{
    // (1) Already-convertible path: caller owns the proxy, no extra retain
    if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
        return new BorrowedExistentialContainer1(convertible.GetExistentialContainer(), IntPtr.Zero);

    // (2) Boxable value type: same — boxing creates a fresh container, caller owns
    if (value is IExistentialBoxable boxable)
        return new BorrowedExistentialContainer1(boxable.BoxAsExistential1<TProtocol>(), IntPtr.Zero);

    // (3) Auto-wrap path: cache lookup, then bump retain for the lease
    var lazy = LookupOrCreateCached(value, wrapFallback);
    var proxy = lazy.Value;
    var container = proxy.GetExistentialContainer();
    Arc.Retain(container.Payload0);  // <-- bump for the lease
    return new BorrowedExistentialContainer1(container, container.Payload0);
}
```

Generated call sites become:

```csharp
// BEFORE (today):
NativeMethods.SetDelegate(_handle,
    ExistentialContainerFactory.GetOrCreate<IFoo>(value, wrap));

// AFTER:
using (var __c = ExistentialContainerFactory.BorrowOrCreate<IFoo>(value, wrap))
{
    NativeMethods.SetDelegate(_handle, __c.Container);
}
```

The `using` ensures the release fires on exception too — important per Codex Finding #4 (partial-handoff-on-error is the highest-risk failure mode).

**Why this works for the leak**:
- The proxy itself no longer holds a permanent +1. The proxy's `_everyProtocol` field becomes a plain `IntPtr` (or stays a `SwiftClassHandle` but with a 0-retain semantics).
- Each `BorrowOrCreate` bumps the retain by 1; the `using` releases by 1; net zero.
- Swift's copy of the container during the P/Invoke bumps refcount by 1; eventually Swift releases its copy and refcount drops by 1; net zero from Swift's perspective.
- When Swift's LAST reference drops (last delegate-property setter cleared, last method call returned), refcount → 0, deinit fires, callback unregisters proxy from `_strongRegistry`, cache eviction runs, the proxy becomes collectible.

### Step 4 — Cache must become weak (or gain eviction)

Codex Finding #2 (P0) is the most important point: a deinit callback alone does nothing if the cache still strongly roots the proxy. Two options:

**Option A — Weak cache + reverse map (Codex's preference, Finding #3)**:

```csharp
private static readonly ConditionalWeakTable<object,
    ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>>
    s_autoWrapCache = new();
```

The strong root for the proxy is `_strongRegistry`, which is dropped by the deinit callback. The cache is just a memoization optimization that survives only as long as something else keeps the proxy alive.

Cache hit semantics: check `WeakReference.TryGetTarget` — if dead, rebuild. Validity check is cheap (one weak deref).

Need a reverse map `IntPtr handle -> (object impl, Type protocolType)` so the deinit callback can find and evict the cache entry. The reverse map can also be a `ConcurrentDictionary` populated alongside the cache insertion.

**Option B — Strong cache + reverse map eviction**:

Keep the existing `Lazy<…>` cache. The deinit callback walks the reverse map and removes the matching `(impl, type)` entry from the cache. More work to keep the reverse map in sync, but the strong cache means cache hits are faster (no weak deref).

**Codex's verdict**: Option A is cleaner because it makes the registry the single source of truth. Option B is acceptable but more state to keep consistent. Don't do the "walk the cache from deinit" approach (Codex Finding #3 calls it "not worth it").

### Step 5 — Generator changes (the centralization point)

Codex Finding #7 is the architectural insight: **don't hand-edit every string emitter**. Instead, centralize through `MarshalPlan` (which already supports `UsingDeclarations` per [`MarshalPlan.cs:22`](../Swift.Bindings/src/Marshaler/Projection/MarshalPlan.cs#L22)).

`ExistentialProjection.GetParameterPlan` already returns a `MarshalPlan`. Extend it to:
1. Set `PInvokeExpression = "__c.Container"`
2. Add `UsingDeclaration = ("BorrowedExistentialContainer1", "__c", "ExistentialContainerFactory.BorrowOrCreate<IFoo>(value, wrap)")`

Any handler that consumes `MarshalPlan` (`PropertyHandler`, `WrapperEmitter`, projection-based methods) will automatically emit the right `using` block.

The string-built emitters that DON'T go through projection still need hand-edits:
- [`MethodSignature.cs:164`](../Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs#L164) — already emits `GetOrCreate` directly
- [`WrapperEmitter.Marshalling.cs:428`](../Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs#L428) — same
- `ClosureEmitter.cs`, `ClosureEmitter.StructParams.cs` — closure boundary marshalling
- `EnumHandler.CaseConstruction.cs` — case constructor marshalling
- `PropertyHandler.cs` setter path

All five+ of these emit `GetOrCreate` directly into a string template. Each needs to emit a `using` block instead.

**Audit task before coding** (Codex Finding #4): walk every existing call site that consumes `GetOrCreate` and confirm Swift takes ownership before the call returns. The "release after first P/Invoke" pattern only works if every emitter's generated code copies/retains the container before returning or throwing. The `_cdecl` existential wrappers and direct-call paths need explicit verification.

### Step 6 — Manual proxy users

[`ProtocolProxyEmitter.Receivers.cs:826`](../Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs#L826) emits a second `public {ProxyClass}(ExistentialContainer1 container)` constructor for users who manually construct proxies. There are two compatibility paths to consider:

**Codex Finding #6**: manual proxies should use the same handoff API as auto-wrap proxies. If generated call sites keep calling plain `.GetExistentialContainer()` for manually-constructed proxies, you end up with two incompatible lifetime models.

The cleanest outcome: **route both manual and auto-wrap paths through `BorrowedExistentialContainer1`**. Generated parameter marshalling always uses `BorrowOrCreate`, which checks `is ISwiftExistentialConvertible<EC1>` first and returns a zero-retain lease for that path. Manual proxies still expose `GetExistentialContainer()` for backward compat but the generator stops emitting calls to it directly.

### Step 7 — Tests

Codex Finding #9: a sim-only plan can catch most regressions. Add BindingTests for:

1. **One-shot method parameter deallocation**: pass a delegate to a method, drop the C# reference, GC, assert `SwiftObjectRegistry.StrongCount` returns to baseline. Today this leaks; after the fix it should not.
2. **Weak delegate set/clear**: assign delegate to property, then assign `null`, GC, assert the proxy is unregistered.
3. **Overwrite `implA → implB → nil`**: assign first impl, then second, then null. After GC, both proxies should be unregistered.
4. **Cache reuse after deinit**: assign impl, clear, GC (proxy unregisters), reassign same impl. Cache should detect the dead entry and rebuild a fresh proxy. Assert the new proxy works.
5. **Cross-thread final release from Swift**: tickle Swift to release the container on a non-main thread. Assert the deinit callback runs without crashing on Mono and NativeAOT.
6. **Process-exit safety**: ensure the deinit callback path doesn't crash during Mono/NativeAOT shutdown (interaction with [`SwiftClassHandle.cs:96-104`](../Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs#L96)).

Assert against both:
- Swift-side deinit counters (see [`OwnershipTests.swift:9-40`](../../BindingTests/Sources/SwiftBindingsTestLib/Lifetime/OwnershipTests.swift#L9) for the existing pattern with `_allocationCounter` / `_deallocationCounter`).
- `SwiftObjectRegistry.StrongCount` returning to baseline (see [`ProxyDisposeTests.cs:124-135`](../../BindingTests/RuntimeTestsApp/Lifetime/ProxyDisposeTests.cs#L124) for the existing pattern).

The existing `AutoWrappedDelegateTests` (in `BindingTests/RuntimeTestsApp/Protocols/AutoWrappedDelegateTests.cs`) cover the round-trip semantics. They do NOT cover the lifetime-cleanup invariant, because today there is no cleanup. Add NEW tests for the lifetime semantics; do not modify the existing ones.

**Device test scope**: keep ONE small device test for this fix specifically (NativeAOT reverse-P/Invoke from Swift release thread is the failure mode that sim won't catch). Don't try to run the full device suite per iteration.

---

## Codex's full review (verbatim)

> 1. **P0** The deinit callback is the right primitive, and there is not a safe simpler place to drop the ctor-owned `+1`. The proxy ctor only creates the retained `EveryProtocol` and registry entry; it does not know when Swift has actually taken ownership yet ([ProtocolProxyEmitter.Receivers.cs](../Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs#L784), [EveryProtocol.cs](../Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs#L41)). Releasing in the ctor or directly inside `GetExistentialContainer()` would either deallocate too early or keep the leak. The clean shape is a handoff lease, not a raw container return: "acquire container for one Swift call, then release after the call returns."
>
> 2. **P0** A deinit callback alone still does not fix the leak because the current auto-wrap cache strongly holds the proxy through `Lazy<ISwiftExistentialConvertible<...>>` ([ExistentialContainer.cs](../Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs#L974)). If you only call `SwiftObjectRegistry.Unregister(handle)`, the cache still roots the proxy while the impl is alive. You must either evict the cache entry on deinit, or better, change the cache to hold weak proxy entries and rebuild on miss/invalid-hit. With the current strong `Lazy` cache, reverse-map eviction is required.
>
> 3. **P1** Of your cache options, `b` is the best direction only if you also weaken the cache. My recommendation is: keep `handle -> proxy` in [SwiftObjectRegistry.cs](../Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs#L24), let the deinit callback mark the proxy invalid and drop the strong registry root, and make the `(impl, Type)` cache store a weak reference to the proxy. Then cache hits do a cheap validity check and recreate if the old proxy was invalidated. `a` is acceptable if you keep the strong cache. `c` is not worth it.
>
> 4. **P1** The highest-risk failure mode is not "outermost P/Invoke nesting"; it is partial handoff on error. Your first release after handoff is only correct if every generated path copies/retains the existential before control returns or throws. That means the wrapper-side invariant matters more than nesting: any path that can throw before taking ownership will turn the first post-call release into premature deallocation. I would explicitly audit the `_cdecl` existential wrappers and direct-call paths for that invariant before coding.
>
> 5. **P1** Reentrancy is real, but you do not need "release only after the outermost P/Invoke" if you model handoff as per-call leases. The right state machine is: one initial owned retain on newly-created proxies, then every later use does `retain-before / release-after`. Nested calls can each balance their own temporary retain. What must be atomic is "consume the initial owned retain exactly once."
>
> 6. **P1** Manual proxies should use the same handoff API as auto-wrap proxies. If generated call sites keep calling plain `.GetExistentialContainer()` for manually-constructed proxies, you will end up with two incompatible lifetime models. The better outcome is to move both paths behind one internal lease interface and let generated parameter marshalling use that everywhere it sees an existential proxy.
>
> 7. **P1** There is not one single emitter you missed, but there is a better central seam than hand-editing every string emitter: `ExistentialProjection.GetParameterPlan()` plus `MarshalPlan` cleanup support ([ExistentialProjection.cs](../Swift.Bindings/src/Marshaler/Projection/ExistentialProjection.cs#L42), [MarshalPlan.cs](../Swift.Bindings/src/Marshaler/Projection/MarshalPlan.cs#L10)). That should cover the projection-based method/wrapper paths. The remaining work is the older string-built paths like [MethodSignature.cs](../Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs#L164), [WrapperEmitter.Marshalling.cs](../Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs#L428), `ClosureEmitter*`, and the optional-existential property/enum helpers. I would centralize by introducing a disposable `BorrowedExistentialContainer1` runtime type, not by wrapping arbitrary P/Invokes in lambdas.
>
> 8. **P2** I do not see a showstopper in Swift ABI or `[UnmanagedCallersOnly]`. The callback fits the documented restrictions if it stays `static`, non-generic, and blittable-only; Microsoft's docs explicitly list those limits ([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute?view=net-10.0)). The practical rule is: keep the deinit callback tiny, idempotent, and non-throwing. Your own runtime already assumes `swift_release` may trigger managed callbacks ([Arc.cs](../Swift.Runtime/src/Swift/Runtime/Arc.cs#L10)), so the callback path is conceptually aligned with the existing runtime.
>
> 9. **P2** A sim-only plan can catch most regressions if you target lifetime semantics directly. I would add BindingTests for: one-shot method parameter deallocation, weak delegate set/clear, overwrite `implA -> implB -> nil`, cache reuse after deinit, and cross-thread final release from Swift. Assert both Swift-side deinit counters and `SwiftObjectRegistry.StrongCount` return to baseline ([ProxyDisposeTests.cs](../../BindingTests/RuntimeTestsApp/Lifetime/ProxyDisposeTests.cs#L114), [OwnershipTests.swift](../../BindingTests/Sources/SwiftBindingsTestLib/Lifetime/OwnershipTests.swift#L17)). That gets you most logic bugs on simulator. What it will not prove is NativeAOT reverse-P/Invoke behavior on device threads, so I would still keep one tiny device smoke test for just this class of fix, not the full device suite.
>
> 10. **P2** One missing failure mode: shutdown/finalizer interaction. `SwiftClassHandle` currently suppresses finalizer-time releases during process exit because Swift deinit can be unsafe late in shutdown ([SwiftClassHandle.cs](../Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs#L17)). Your new deinit callback path should be prepared for late-process no-op behavior and double-cleanup races with `Dispose()`/`Unregister()`.

---

## Implementation order (suggested)

**⚠ Superseded by "### Resolved implementation order" in the Strategy Lockdown section above.** The original 11-step order below was built around `BorrowedExistentialContainer1` and hand-edits to 6 emitters; the resolved order is shorter and touches only the runtime + proxy ctor.

1. **Add `BorrowedExistentialContainer1` runtime type** with `using`-friendly disposal. No generator changes yet — keep the existing `GetOrCreate` overload working for backward compat. Unit-test the lease semantics in isolation.

2. **Add the Swift `EveryProtocol.deinit` + `SBW_SetEveryProtocolDeinitCallback`** + the C# `OnEveryProtocolDeinit` callback. Wire the proxy constructor to register the callback after `RegisterStrong`. At this stage the +1 retain is still held by the proxy field, so deinit doesn't fire yet — but the plumbing exists. Add a unit test that manually drops the +1 (calling `_everyProtocol.Dispose()`) and asserts the deinit callback runs and unregisters.

3. **Audit every `_cdecl` existential wrapper and direct-call site for the "Swift takes ownership before return" invariant** (Codex Finding #4). Document any paths that can throw before taking ownership — those need special handling.

4. **Convert auto-wrap cache to weak references** + add reverse map for handle-based eviction. Test cache rebuild semantics after deinit-driven eviction.

5. **Switch the proxy's `_everyProtocol` field from `+1 SafeHandle` to a `0-retain` IntPtr** (or similar). Add `BorrowOrCreate` to `ExistentialContainerFactory` that bumps retain on the lease.

6. **Convert `ExistentialProjection.GetParameterPlan()` to emit `BorrowedExistentialContainer1` via `MarshalPlan.UsingDeclarations`.** This automatically updates all projection-based call sites.

7. **Hand-edit the remaining string emitters** (`MethodSignature.cs`, `WrapperEmitter.Marshalling.cs`, `ClosureEmitter*`, `EnumHandler.CaseConstruction.cs`, `PropertyHandler.cs` setter path) to emit `using` blocks. There are ~6 of these.

8. **Run the full BindingTests + validation gates** at each step. The 1300+ runtime tests are the regression net.

9. **Add new lifetime tests** (the 6 from Step 7 in this doc).

10. **Run `nuke runtime-tests-device --class-filter <new lifetime test class>`** to verify NativeAOT reverse-P/Invoke from Swift release thread works.

11. **Delete the residual-leak entry** from [`src/docs/roadmap.md:97`](roadmap.md#L97). Move this design doc to `src/docs/Completed/`.

---

## Key files index (quick reference)

**Read first:**
- This doc
- `src/docs/roadmap.md` (line 97 is the entry to be deleted when this is done)

**Current state (don't modify until you understand all of these):**
- `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs:920-1066` — auto-wrap factory + cache
- `src/Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs` — registry (24-84 are the relevant methods)
- `src/Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs` — C# wrapper for Swift EveryProtocol class
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs` — SafeHandle that holds the +1 retain
- `src/Swift.Runtime/src/Swift/Runtime/Arc.cs` — `Arc.Retain` / `Arc.Release` P/Invokes
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs:41-83` — Swift EveryProtocol class emission
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs:764-836` — proxy constructor emission
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.SwiftObject.cs:42-107` — `ISwiftExistentialConvertible` impl, `Dispose`, finalizer
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs:25-30` — `_everyProtocol` field declaration
- `src/Swift.Bindings/src/Marshaler/Projection/ExistentialProjection.cs` — central projection (Codex's recommended seam)
- `src/Swift.Bindings/src/Marshaler/Projection/MarshalPlan.cs` — has `UsingDeclarations` support already

**Generator emit sites that need string-level updates:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs:164` (param marshalling)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs:428`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` (multiple sites — search for `GetOrCreate`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.StructParams.cs` (multiple sites)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` (multiple sites)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs:936-960` (setter path)

**Existing test patterns to model from:**
- `BindingTests/Sources/SwiftBindingsTestLib/Lifetime/OwnershipTests.swift:1-40` — Swift-side allocation/deallocation counters
- `BindingTests/RuntimeTestsApp/Lifetime/ProxyDisposeTests.cs:100-150` — `SwiftObjectRegistry.StrongCount` baseline assertions
- `BindingTests/RuntimeTestsApp/Protocols/AutoWrappedDelegateTests.cs` — round-trip semantics for the auto-wrap path (do not modify, add new lifetime-specific tests alongside)
- `BindingTests/Sources/SwiftBindingsTestLib/Protocols/AutoWrappedDelegate.swift` — Swift-side test fixtures

---

## Open questions / things to verify before coding

**⚠ All resolved in the "### Open questions (resolved)" subsection of the Strategy Lockdown section above.** This list is preserved below so the reader can see what was asked originally; the answers are in the lockdown section.

1. **Does Swift `EveryProtocol` need to be a `final class` to support deinit callbacks?** Currently it is `final`. Verify that `final` doesn't interfere with the deinit firing.

2. **Does adding `deinit` to `EveryProtocol` change the type's ABI?** It might affect type metadata layout, vtable, or witness table emission. Run `nuke validate` after the Swift-side change to catch any cascading breakage.

3. **`Arc.Retain` cost per call**: every existential parameter site now does an atomic increment + decrement. For hot paths this matters. Microbenchmark before vs after for a tight delegate-callback loop.

4. **`ConditionalWeakTable<object, ConcurrentDictionary<…>>` with weak proxy values**: `ConditionalWeakTable` itself doesn't directly support weak values, but the inner `ConcurrentDictionary<Type, WeakReference<…>>` works fine. Be aware that the OUTER table key (impl) being weak still controls the entry's existence — when the impl is GC'd, the inner dict (and its weak references) all become collectible together.

5. **Reverse map sync**: if you go with Option B (strong cache + reverse map), the reverse map insertion has to happen atomically with the cache insertion, and the deinit eviction has to remove from BOTH. Race window if not careful.

6. **Process-exit ordering**: during shutdown, will Swift's `EveryProtocol.deinit` run before or after the C# managed runtime is torn down? If after, the C# callback could fire against a dead managed heap. The existing `SwiftExitGuard.IsProcessExiting` check in `SwiftClassHandle.ReleaseHandle` is the precedent — the deinit callback should consult the same guard.

7. **`takeUnretainedValue` in `SBW_SetEveryProtocolDeinitCallback`**: confirm this is the right Unmanaged variant. We don't want to bump the retain count when we're just setting a property — we already hold a +1 from the original `SBW_CreateEveryProtocol` caller (the C# proxy), and the deinit callback registration shouldn't add another.

---

## Success criteria

**⚠ Superseded by "### Updated success criteria" in the Strategy Lockdown section above.** The original criteria below assumed `BorrowedExistentialContainer1`; the resolved criteria reflect the simpler impl-anchored model.

- Auto-wrapped proxies are unregistered from `_strongRegistry` when Swift releases the last reference to their existential container.
- `SwiftObjectRegistry.StrongCount` returns to baseline after a delegate is set, then cleared, then GC'd.
- All existing 1300+ runtime tests continue to pass on both iOS Simulator (Mono) and iOS Device (NativeAOT).
- The 6 new lifetime tests pass on Sim, plus at least one of them passes on Device.
- The roadmap entry at `src/docs/roadmap.md:97` is removed.
- This doc is moved to `src/docs/Completed/`.
