// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Swift.Runtime;

/// <summary>
/// Closure-context owner-token bridge: pairs Swift's <c>_SBClosureCtx</c> reference-
/// counted box (defined in the SwiftBindingsRuntime native framework) with the C# free
/// callback that releases the wrapped <see cref="GCHandle"/> when the box deinits.
/// </summary>
/// <remarks>
/// <para>
/// Each escaping Swift closure that captures a managed delegate previously leaked the
/// <see cref="GCHandle"/> rooting the delegate — the C# wrapper had no way to know
/// when Swift released the closure. The owner-token model wraps the captured
/// <see cref="GCHandle"/> pointer in a Swift class whose <c>deinit</c> upcalls the
/// trampoline registered here, freeing the handle exactly once.
/// </para>
/// <para>
/// Restricted to <c>@escaping</c> closures: non-escaping closures still free in the
/// C# wrapper's <c>finally</c> block (the original Cat 1 path), where Swift cannot
/// retain the closure past the call.
/// </para>
/// </remarks>
internal static class SwiftClosureContext
{
    private static readonly object s_lock = new();
    private static Registration? s_registration;
    private const string LibraryName = "SwiftBindingsRuntime";

    internal enum OwnerTokenStatus
    {
        NativeUnavailable,
        RequiredExportUnavailable,
        RegisteredNotProcessVisible,
        ExternalResolverUnavailable,
        ExternalResolverUnverified,
        Ready
    }

    // Published only after registration and visibility verification. A single
    // selected image supplies ALL three owner operations, including on failure
    // to promote it. A real box can never be paired with raw-context passthrough.
    private sealed record Registration(
        OwnerTokenStatus Status, IntPtr PinnedHandle, IntPtr Factory,
        IntPtr Unbox, string? Diagnostic, bool UseExternalResolver = false);

    private static Registration Current
    {
        get
        {
            EnsureRegistered();
            return Volatile.Read(ref s_registration)!;
        }
    }

    internal static OwnerTokenStatus Status => Current.Status;
    internal static string? RegistrationDiagnostic => Current.Diagnostic;
    internal static IntPtr FactoryAddress => Current.Factory;

    /// <summary>
    /// Registers the destroy trampoline with the runtime dylib. Idempotent — safe
    /// to call multiple times. Called once from
    /// <see cref="SwiftFrameworkResolver.InitializeRuntime"/>.
    /// </summary>
    internal static unsafe void EnsureRegistered()
    {
        if (Volatile.Read(ref s_registration) != null) return;
        lock (s_lock)
        {
            if (s_registration != null) return;
            var registration = RegisterRuntime();
            if (registration.Diagnostic != null)
                Debug.WriteLine($"[SwiftClosureContext] {registration.Status}: {registration.Diagnostic}");
            Volatile.Write(ref s_registration, registration);
        }
    }

    private static unsafe Registration RegisterRuntime()
    {
        if (!SwiftFrameworkResolver.RuntimeAssemblyResolverOwned)
            return RegisterThroughExternalResolver();

        // Runtime module initialization installs our resolver before reaching
        // here. Invoke that authority explicitly, then retain the old default
        // probing fallback on a miss. NativeLibrary.TryLoad(Assembly, ...) does
        // NOT invoke a per-assembly DllImportResolver. This is not a mechanism
        // for honoring an arbitrary replacement resolver on Swift.Runtime.
        var assembly = typeof(SwiftFrameworkResolver).Assembly;
        var handle = SwiftFrameworkResolver.ResolveSwiftFramework(LibraryName, assembly, null);
        if (handle == IntPtr.Zero
            && !NativeLibrary.TryLoad(LibraryName, assembly, null, out handle))
            return new(OwnerTokenStatus.NativeUnavailable, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                "Native runtime is absent; closure contexts retain the raw-handle fallback.");

        // Treat a partial/old native owner API coherently: do not install a
        // callback or allocate any box unless registration, factory and unbox
        // all exist in this one selected image.
        string? missing = null;
        if (!NativeLibrary.TryGetExport(handle, "SwiftBindings_SetClosureContextDestroyCallback", out var register))
            missing = "SwiftBindings_SetClosureContextDestroyCallback";
        if (!NativeLibrary.TryGetExport(handle, "SwiftBindings_NewClosureContext", out var factory))
            missing ??= "SwiftBindings_NewClosureContext";
        if (!NativeLibrary.TryGetExport(handle, "SBW_UnboxClosureContext", out var unbox))
            missing ??= "SBW_UnboxClosureContext";
        if (missing != null)
        {
            NativeLibrary.Free(handle); // No callback or box has escaped this image.
            return new(OwnerTokenStatus.RequiredExportUnavailable, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                $"Native runtime lacks {missing}; closure contexts retain the raw-handle fallback.");
        }

        var setCallback = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<IntPtr, void>, void>)register;
        setCallback(&DestroyClosureContext);

        // Deliberately pin this ONE original acquisition for process lifetime:
        // exported function pointers, Swift boxes and the registered callback
        // must remain valid. Promote only its actual image; balance the extra
        // promotion reference while this original acquisition remains held.
        var visible = TryMakeFactoryProcessVisible(factory, out var diagnostic);
        return new(visible ? OwnerTokenStatus.Ready : OwnerTokenStatus.RegisteredNotProcessVisible,
            handle, factory, unbox, diagnostic);
    }

    private static unsafe Registration RegisterThroughExternalResolver()
    {
        // An already-installed resolver cannot be retrieved or invoked through
        // NativeLibrary.Load. Preserve its original per-library P/Invoke route.
        try
        {
            ExternalNativeMethods.SetDestroyCallback(&DestroyClosureContext);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new(ex is DllNotFoundException ? OwnerTokenStatus.NativeUnavailable : OwnerTokenStatus.RequiredExportUnavailable,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                $"Installed runtime resolver could not register closure destruction: {ex.Message}");
        }

        // The native unbox function uses Unmanaged.fromOpaque and does NOT
        // accept null or a raw GCHandle. Resolve it using one genuine owner box
        // with a valid GCHandle context, after callback registration. This also
        // prevents a partial custom API from pairing zero-allocation fallback
        // with a native unbox call on the raw context.
        var probeHandle = GCHandle.Alloc(new object());
        var context = GCHandle.ToIntPtr(probeHandle);
        IntPtr box = IntPtr.Zero;
        string? failure = null;
        var failureStatus = OwnerTokenStatus.ExternalResolverUnavailable;
        try
        {
            box = ExternalNativeMethods.NewContext(context);
            if (box == IntPtr.Zero)
                failure = "Installed runtime resolver's owner factory returned null during initialization.";
            else if (ExternalNativeMethods.Unbox(box) != context)
                failure = "Installed runtime resolver's native box did not preserve its context during initialization.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            failureStatus = OwnerTokenStatus.RequiredExportUnavailable;
            failure = $"Installed runtime resolver lacks a complete closure owner API: {ex.Message}";
        }
        finally
        {
            if (box == IntPtr.Zero)
                probeHandle.Free(); // Factory missing/failed/null: ownership never transferred.
            else
            {
                // Once a real box exists, native deinit alone owns the handle,
                // including when importing/calling unbox failed above.
                try { SwiftReleaseTrampoline.ReleaseRaw(box); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    failureStatus = OwnerTokenStatus.ExternalResolverUnavailable;
                    failure = $"Installed runtime resolver could not release the owner preflight box: {ex.Message}";
                    // Do not free its still-owned GCHandle or retry an uncertain
                    // native release. Retain the diagnostic without failing startup.
                }
            }
        }
        if (failure != null)
            return new(failureStatus, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, failure);

        return new(OwnerTokenStatus.ExternalResolverUnverified, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            "Closure owner imports use the installed runtime resolver. Its selected image and process-wide factory identity are unverified.",
            UseExternalResolver: true);
    }

    private static unsafe bool TryMakeFactoryProcessVisible(IntPtr factory, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            fixed (byte* symbol = "SwiftBindings_NewClosureContext\0"u8)
            {
                var process = new IntPtr(-2); // Darwin RTLD_DEFAULT, as used by generated helpers.
                if (Darwin.Dlsym(process, symbol) == factory)
                    return true; // Link-loaded/global images need no promotion.

                DlInfo info;
                if (Darwin.Dladdr(factory, &info) == 0 || info.FileName == IntPtr.Zero)
                {
                    diagnostic = $"dladdr could not identify the registered factory image ({factory:x}).";
                    return false;
                }
                var path = Marshal.PtrToStringUTF8(info.FileName);
                var promotion = Darwin.Dlopen(info.FileName, Darwin.RtldNow | Darwin.RtldNoLoad | Darwin.RtldGlobal);
                if (promotion == IntPtr.Zero)
                {
                    diagnostic = $"Cannot promote registered runtime image '{path}': {GetLoaderError()}";
                    return false;
                }

                // NOLOAD prevents a different image from satisfying promotion.
                // A successful dlopen is one acquire even when it returns the
                // same handle value as the original NativeLibrary acquisition.
                var closeResult = Darwin.Dlclose(promotion);
                if (closeResult != 0)
                {
                    diagnostic = $"Cannot balance promotion reference for '{path}': {GetLoaderError()}";
                    return false; // Do not retry an ambiguous close.
                }
                var processFactory = Darwin.Dlsym(process, symbol);
                if (processFactory != factory)
                {
                    diagnostic = $"Registered runtime image '{path}' is not the process factory after promotion " +
                        $"(registered={factory:x}, process={processFactory:x}).";
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Loader API availability is distinct from a missing runtime. Keep
            // registered boxing usable, report the degraded wrapper capability,
            // and preserve the existing non-throwing startup policy.
            diagnostic = $"Cannot verify registered runtime process visibility: {ex.Message}";
            return false;
        }
    }

    private static string GetLoaderError()
        => Marshal.PtrToStringUTF8(Darwin.Dlerror()) ?? "dyld supplied no error text";

    /// <summary>
    /// Destroy trampoline fired by Swift's <c>_SBClosureCtx.deinit</c>. Receives the
    /// opaque pointer originally produced by <c>GCHandle.ToIntPtr</c> and frees
    /// the handle. The Swift box guarantees this fires exactly once per allocated
    /// context.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void DestroyClosureContext(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Allocates a <c>_SBClosureCtx</c> box wrapping the supplied <see cref="GCHandle"/>
    /// pointer and returns a +1-retained opaque pointer. Returns
    /// <see cref="IntPtr.Zero"/> when the native owner API is unavailable, letting the caller fall back
    /// to the prior raw-handle leak behaviour.
    /// </summary>
    internal static unsafe IntPtr TryAllocateBox(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return IntPtr.Zero;
        var registration = Current;
        if (registration.UseExternalResolver)
            return ExternalNativeMethods.NewContext(ctx);
        var factory = registration.Factory;
        if (factory == IntPtr.Zero) return IntPtr.Zero;
        return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)factory)(ctx);
    }

    /// <summary>
    /// Releases a box pointer returned by <see cref="TryAllocateBox"/>. The Swift
    /// <c>_SBClosureCtx.deinit</c> then fires <see cref="DestroyClosureContext"/>,
    /// freeing the wrapped <see cref="GCHandle"/> exactly once.
    /// </summary>
    internal static void ReleaseBox(IntPtr boxPtr)
    {
        if (boxPtr == IntPtr.Zero) return;
        // This trampoline calls the system swift_release; it has no per-image
        // closure registration state. The object's own metadata runs its deinit.
        SwiftReleaseTrampoline.ReleaseRaw(boxPtr);
    }

    /// <summary>
    /// Returns the original <see cref="GCHandle"/> pointer from a closure context
    /// slot that may hold either a raw <see cref="GCHandle"/> pointer (dylib
    /// absent) or an <c>_SBClosureCtx</c> box pointer (dylib present). The
    /// registration snapshot is published eagerly in <see cref="EnsureRegistered"/> so
    /// trampolines see a definite state by the time they fire.
    /// </summary>
    /// <remarks>
    /// Used by trampolines on the legacy <c>SwiftClosureData</c> escaping path —
    /// the cdecl path receives the raw <see cref="GCHandle"/> pointer directly
    /// (the Swift wrapper unboxes before invoking the C# callback) and must NOT
    /// call this helper.
    /// </remarks>
    internal static unsafe IntPtr GetCtx(IntPtr maybeBoxedCtx)
    {
        if (maybeBoxedCtx == IntPtr.Zero) return IntPtr.Zero;
        var registration = Current;
        if (registration.UseExternalResolver)
            return ExternalNativeMethods.Unbox(maybeBoxedCtx);
        var unbox = registration.Unbox;
        if (unbox == IntPtr.Zero) return maybeBoxedCtx;
        return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)unbox)(maybeBoxedCtx);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr ImageBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    private static class Darwin
    {
        internal const int RtldNow = 0x2;
        internal const int RtldNoLoad = 0x10;
        internal const int RtldGlobal = 0x8;
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        [DllImport(LibSystem, EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Dlopen(IntPtr path, int flags);
        [DllImport(LibSystem, EntryPoint = "dlclose", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Dlclose(IntPtr handle);
        [DllImport(LibSystem, EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe IntPtr Dlsym(IntPtr handle, byte* symbol);
        [DllImport(LibSystem, EntryPoint = "dladdr", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Dladdr(IntPtr address, DlInfo* info);
        [DllImport(LibSystem, EntryPoint = "dlerror", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Dlerror();
    }

    private static class ExternalNativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "SwiftBindings_SetClosureContextDestroyCallback", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void SetDestroyCallback(delegate* unmanaged[Cdecl]<IntPtr, void> callback);
        [DllImport(LibraryName, EntryPoint = "SwiftBindings_NewClosureContext", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr NewContext(IntPtr context);
        [DllImport(LibraryName, EntryPoint = "SBW_UnboxClosureContext", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Unbox(IntPtr box);
    }
}
