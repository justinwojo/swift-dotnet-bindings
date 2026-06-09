// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Represents Swift closure data as two machine words: function pointer and context pointer.
/// This matches Swift's closure representation in memory.
/// </summary>
/// <remarks>
/// Swift closures are represented as:
/// - Function pointer: Points to the actual code
/// - Context pointer: Heap-allocated context for captured variables (for escaping closures)
/// For @convention(c) closures, context is always null.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SwiftClosureData
{
    /// <summary>
    /// Pointer to the closure's executable code.
    /// </summary>
    public readonly IntPtr FunctionPointer;

    /// <summary>
    /// Pointer to the closure's captured context.
    /// For @convention(c) closures, this is IntPtr.Zero.
    /// For escaping closures, this points to heap-allocated, reference-counted context.
    /// </summary>
    public readonly IntPtr Context;

    /// <summary>
    /// Creates a new SwiftClosureData with the specified function and context pointers.
    /// </summary>
    /// <param name="functionPointer">Pointer to the closure code.</param>
    /// <param name="context">Pointer to the closure context.</param>
    public SwiftClosureData(IntPtr functionPointer, IntPtr context)
    {
        FunctionPointer = functionPointer;
        Context = context;
    }

    /// <summary>
    /// Creates a SwiftClosureData for a @convention(c) closure (no context).
    /// </summary>
    /// <param name="functionPointer">Pointer to the C-compatible function.</param>
    /// <returns>A SwiftClosureData with null context.</returns>
    public static SwiftClosureData FromConventionC(IntPtr functionPointer)
    {
        return new SwiftClosureData(functionPointer, IntPtr.Zero);
    }

    /// <summary>
    /// Returns true if this closure has a context (is not @convention(c)).
    /// </summary>
    public bool HasContext => Context != IntPtr.Zero;

    /// <summary>
    /// Returns true if this is a valid closure (has a function pointer).
    /// </summary>
    public bool IsValid => FunctionPointer != IntPtr.Zero;
}

/// <summary>
/// Provides methods for marshalling C# delegates to Swift closures and vice versa.
/// </summary>
public static class SwiftClosureMarshaller
{
    /// <summary>
    /// Creates a SwiftClosureData from a C# delegate for @convention(c) Swift closures.
    /// The delegate must remain alive for the lifetime of the closure usage.
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type.</typeparam>
    /// <param name="del">The delegate to convert.</param>
    /// <returns>A SwiftClosureData that can be passed to Swift.</returns>
    /// <remarks>
    /// IMPORTANT: The caller must ensure the delegate is kept alive (e.g., via GCHandle)
    /// for as long as Swift may call the closure. Otherwise, the delegate may be garbage
    /// collected and the function pointer will become invalid.
    /// </remarks>
    public static SwiftClosureData CreateConventionCClosure<TDelegate>(TDelegate del) where TDelegate : Delegate
    {
        if (del == null)
            throw new ArgumentNullException(nameof(del));

        IntPtr funcPtr = Marshal.GetFunctionPointerForDelegate(del);
        return SwiftClosureData.FromConventionC(funcPtr);
    }

    /// <summary>
    /// Pins a delegate and returns a SwiftClosureData along with a GCHandle that must be freed.
    /// Use this when passing a delegate to Swift that may be called asynchronously or stored.
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type.</typeparam>
    /// <param name="del">The delegate to convert.</param>
    /// <param name="handle">Output GCHandle that must be freed when the closure is no longer needed.</param>
    /// <returns>A SwiftClosureData that can be passed to Swift.</returns>
    public static SwiftClosureData CreatePinnedConventionCClosure<TDelegate>(TDelegate del, out GCHandle handle)
        where TDelegate : Delegate
    {
        if (del == null)
            throw new ArgumentNullException(nameof(del));

        // Pin the delegate to prevent GC from moving or collecting it
        handle = GCHandle.Alloc(del);
        IntPtr funcPtr = Marshal.GetFunctionPointerForDelegate(del);
        return SwiftClosureData.FromConventionC(funcPtr);
    }

    /// <summary>
    /// Creates a SwiftClosureData for an escaping Swift closure from a C# delegate.
    /// This creates a thunk that Swift can call, with the delegate stored in the context.
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type.</typeparam>
    /// <param name="del">The delegate to wrap.</param>
    /// <param name="thunkPointer">Pointer to the [UnmanagedCallersOnly] thunk function.</param>
    /// <returns>A SwiftClosureData where Context contains a GCHandle to the delegate.</returns>
    /// <remarks>
    /// The context stores a GCHandle to the delegate. The thunk function must:
    /// 1. Receive the context as its last parameter (per Swift ABI)
    /// 2. Convert the context back to the delegate
    /// 3. Invoke the delegate with the other parameters
    /// 4. Return the result to Swift
    /// </remarks>
    public static SwiftClosureData CreateEscapingClosure<TDelegate>(TDelegate del, IntPtr thunkPointer)
        where TDelegate : Delegate
    {
        if (del == null)
            throw new ArgumentNullException(nameof(del));

        // Create a GCHandle to keep the delegate alive
        GCHandle handle = GCHandle.Alloc(del);
        IntPtr context = GCHandle.ToIntPtr(handle);

        return new SwiftClosureData(thunkPointer, context);
    }

    /// <summary>
    /// Releases resources associated with an escaping closure created by CreateEscapingClosure.
    /// </summary>
    /// <param name="closureData">The closure data to release.</param>
    public static void ReleaseEscapingClosure(SwiftClosureData closureData)
    {
        if (closureData.HasContext)
        {
            GCHandle handle = GCHandle.FromIntPtr(closureData.Context);
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    /// <summary>
    /// Terminates the process with a diagnostic when a managed exception escapes a
    /// <b>non-throwing</b> Swift closure callback. Such a callback has no error channel
    /// back to Swift, so letting the exception unwind into native Swift frames would
    /// abort the process anyway (SIGABRT) — but uncontrolled, with no actionable message
    /// and a corrupted stack. Calling this first converts that into a controlled
    /// <see cref="Environment.FailFast(string, Exception)"/> with the original exception
    /// attached. Mirrors <c>AsyncClosureHelper.FailFastNonThrowing</c> for the async path.
    /// </summary>
    /// <param name="ex">The unhandled exception from the user's closure delegate.</param>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void FailFastUnhandledClosureException(Exception ex)
    {
        Environment.FailFast(
            $"Unhandled managed exception in non-throwing Swift closure callback: {ex}", ex);
    }

    /// <summary>
    /// Extracts the delegate from an escaping closure's context.
    /// </summary>
    /// <typeparam name="TDelegate">The expected delegate type.</typeparam>
    /// <param name="context">The context pointer from SwiftClosureData.</param>
    /// <returns>The original delegate.</returns>
    public static TDelegate GetDelegateFromContext<TDelegate>(IntPtr context) where TDelegate : Delegate
    {
        GCHandle handle = GCHandle.FromIntPtr(context);
        return (TDelegate)handle.Target!;
    }

    /// <summary>
    /// Extracts the delegate from an escaping closure's context that may be either
    /// a raw <see cref="GCHandle"/> pointer (when the SwiftBindings runtime dylib
    /// is absent) or an <c>_SBClosureCtx</c> box pointer (the normal case where
    /// Swift owns the GCHandle through the box's deinit). Resolves the box via
    /// the closure-context owner-token bridge when present.
    /// </summary>
    /// <remarks>
    /// Used by trampolines on the legacy <c>SwiftClosureData</c> escaping path
    /// (e.g. <c>ImagePipeline.loadData(didReceiveData:)</c>) — the cdecl path
    /// must continue using <see cref="GetDelegateFromContext{TDelegate}"/>
    /// because the Swift wrapper unboxes before calling the C# trampoline.
    /// </remarks>
    public static TDelegate GetDelegateFromBoxedContext<TDelegate>(IntPtr maybeBoxedContext) where TDelegate : Delegate
    {
        IntPtr ctx = SwiftClosureContext.GetCtx(maybeBoxedContext);
        GCHandle handle = GCHandle.FromIntPtr(ctx);
        return (TDelegate)handle.Target!;
    }

    /// <summary>
    /// Wraps a pinned <see cref="GCHandle"/> pointer in an <c>_SBClosureCtx</c>
    /// ARC box returned by the SwiftBindings runtime dylib. Returns
    /// <see cref="IntPtr.Zero"/> when the dylib is absent — callers fall back to
    /// passing the raw GCHandle pointer (preserving the pre-0.11 leak behaviour
    /// rather than crashing). Used by generated wrappers on the legacy
    /// <c>SwiftClosureData</c> escaping closure path.
    /// </summary>
    public static IntPtr TryAllocateBoxedContext(IntPtr ctx)
        => SwiftClosureContext.TryAllocateBox(ctx);

    /// <summary>
    /// Releases a box pointer returned by <see cref="TryAllocateBoxedContext"/>,
    /// firing the Swift-side <c>_SBClosureCtx.deinit</c> which frees the wrapped
    /// <see cref="GCHandle"/>. No-op for <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static void ReleaseBoxedContext(IntPtr boxPtr)
        => SwiftClosureContext.ReleaseBox(boxPtr);
}

/// <summary>
/// Represents a Swift escaping closure that can be invoked from C#.
/// This is used when receiving closures from Swift.
/// </summary>
/// <typeparam name="TDelegate">The C# delegate type that matches the closure signature.</typeparam>
public sealed class SwiftEscapingClosure<TDelegate> : IDisposable where TDelegate : Delegate
{
    private SwiftClosureData _closureData;
    private TDelegate? _cachedInvoker;
    private bool _disposed;
    private readonly bool _ownsContext;
    private readonly bool _isFromSwift;

    /// <summary>
    /// Creates a wrapper around Swift closure data.
    /// </summary>
    /// <param name="closureData">The Swift closure data.</param>
    /// <param name="ownsContext">If true, the context will be released on dispose.</param>
    /// <param name="isFromSwift">If true, the closure was received from Swift and needs ARC handling.</param>
    internal SwiftEscapingClosure(SwiftClosureData closureData, bool ownsContext = false, bool isFromSwift = false)
    {
        _closureData = closureData;
        _ownsContext = ownsContext;
        _isFromSwift = isFromSwift;
    }

    /// <summary>
    /// Creates a SwiftEscapingClosure from raw closure data received from Swift.
    /// </summary>
    /// <param name="functionPointer">The closure's function pointer.</param>
    /// <param name="context">The closure's context pointer.</param>
    /// <returns>A new SwiftEscapingClosure wrapping the Swift closure.</returns>
    public static SwiftEscapingClosure<TDelegate> FromSwift(IntPtr functionPointer, IntPtr context)
    {
        var closureData = new SwiftClosureData(functionPointer, context);

        // Retain the Swift context if it's a heap object
        if (context != IntPtr.Zero)
        {
            Arc.Retain(context);
        }

        return new SwiftEscapingClosure<TDelegate>(closureData, ownsContext: false, isFromSwift: true);
    }

    /// <summary>
    /// Gets the underlying closure data.
    /// </summary>
    public SwiftClosureData ClosureData => _closureData;

    /// <summary>
    /// Gets whether this closure is valid and can be invoked.
    /// </summary>
    public bool IsValid => !_disposed && _closureData.IsValid;

    /// <summary>
    /// Gets the function pointer for direct invocation.
    /// The caller is responsible for passing the context as the last parameter.
    /// </summary>
    public IntPtr FunctionPointer => _closureData.FunctionPointer;

    /// <summary>
    /// Gets the context pointer that must be passed when invoking the closure.
    /// </summary>
    public IntPtr Context => _closureData.Context;

    /// <summary>
    /// Sets up a cached invoker delegate for this closure.
    /// This should be called with a delegate that properly invokes the Swift closure.
    /// </summary>
    /// <param name="invoker">The invoker delegate.</param>
    internal void SetInvoker(TDelegate invoker)
    {
        _cachedInvoker = invoker;
    }

    /// <summary>
    /// Gets the cached invoker delegate, if one has been set.
    /// </summary>
    public TDelegate? Invoker => _cachedInvoker;

    /// <summary>
    /// Disposes of the closure, releasing any associated resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer — balances the <see cref="Arc.Retain"/> performed by <see cref="FromSwift"/>
    /// when the wrapper becomes unreachable without an explicit Dispose. The owns-context
    /// (C#→Swift) path is intentionally skipped from the finalizer: Swift may still hold
    /// the GCHandle, so freeing it from the finalizer would let Swift dereference a freed
    /// handle.
    /// </summary>
    ~SwiftEscapingClosure()
    {
        Dispose(disposing: false);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (_ownsContext && _closureData.HasContext)
        {
            // C#→Swift path: only release on explicit Dispose. From the finalizer we cannot
            // tell whether Swift still references the GCHandle, so we leak rather than risk
            // a use-after-free on the Swift side.
            if (disposing)
            {
                SwiftClosureMarshaller.ReleaseEscapingClosure(_closureData);
            }
        }
        else if (_isFromSwift && _closureData.HasContext)
        {
            // Swift→C# path: balance the Arc.Retain done in FromSwift.
            if (disposing)
            {
                Arc.Release(_closureData.Context);
            }
            else if (!SwiftExitGuard.IsProcessExiting)
            {
                // Finalizer thread: route through SwiftReleaseTrampoline.SafeReleaseRawForFinalizer,
                // which wraps libswiftCore's swift_release (without going through Unmanaged<AnyObject>)
                // and swallows DllNotFoundException / native faults. The AnyObject cast in
                // SBW_SwiftRelease is only safe for class instances; closure contexts are heap
                // objects without AnyObject metadata and segfault inside _objc_msgSend_uncached
                // if released that way. The single Cdecl boundary into our own dylib also sidesteps
                // the Mono JIT !ji->async assertion that fires when swift_release is called directly
                // via [DllImport] after CallConvSwift contamination. The swallow lives on the
                // non-generic SwiftReleaseTrampoline because emitting try/catch IL inside this
                // generic class' finalizer trips Mono AOT shutdown.
                SwiftReleaseTrampoline.SafeReleaseRawForFinalizer(_closureData.Context);
            }
        }

        _closureData = default;
        _cachedInvoker = null;
        _disposed = true;
    }
}

/// <summary>
/// Provides factory methods for creating Swift closures with specific signatures.
/// The generated bindings will use these to create properly typed closures.
/// </summary>
public static class SwiftClosureFactory
{
    /// <summary>
    /// Wraps a Swift closure (received from Swift) as a callable C# closure wrapper.
    /// </summary>
    /// <typeparam name="TDelegate">The C# delegate type matching the closure signature.</typeparam>
    /// <param name="functionPointer">The Swift closure's function pointer.</param>
    /// <param name="context">The Swift closure's context.</param>
    /// <returns>A wrapper that can be used to invoke the closure.</returns>
    public static SwiftEscapingClosure<TDelegate> WrapSwiftClosure<TDelegate>(IntPtr functionPointer, IntPtr context)
        where TDelegate : Delegate
    {
        return SwiftEscapingClosure<TDelegate>.FromSwift(functionPointer, context);
    }
}
