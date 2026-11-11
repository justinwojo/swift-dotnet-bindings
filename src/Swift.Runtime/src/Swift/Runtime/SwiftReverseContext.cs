// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Swift.Runtime;

/// <summary>
/// Creates and releases the Swift reference-counted context box that a Swift-convention closure
/// carries when its body is a managed callback. For generated code only.
/// </summary>
/// <remarks>
/// <para>
/// The box is the native <c>_SBReverseCtx</c> class in the SwiftBindingsRuntime framework. It holds
/// two immutable words: the address of a managed Cdecl <c>UnmanagedCallersOnly</c> entry point
/// (<c>target</c>) and the <see cref="GCHandle"/> of the delegate it dispatches to
/// (<c>payload</c>). A stateless native stub reads both at fixed offsets from the closure context, so
/// the layout is a contract: version 1 places <c>target</c> at +16 and <c>payload</c> at +24,
/// after the two-word Swift heap header.
/// </para>
/// <para>
/// Ownership: a successful <see cref="Create"/> returns the box at +1 and transfers the
/// <c>payload</c> handle to it. The box frees the handle exactly once, when its last reference is
/// released, through the same registered destroy callback the closure-context owner token uses. A
/// caller that throws from <see cref="Create"/> still owns the handle and must free it.
/// </para>
/// <para>
/// Creation is mandatory: the first use checks that the native image exports both entry points and
/// reports layout version 1 with the expected offsets. Any failure throws
/// <see cref="InvalidOperationException"/> naming the missing export or the expected and actual
/// layout. There is no fallback to a raw context.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class SwiftReverseContext
{
    internal const int ExpectedLayoutVersion = 1;
    internal const int ExpectedTargetOffset = 16;
    internal const int ExpectedPayloadOffset = 24;

    internal const string FactoryExport = "SwiftBindings_NewReverseContext";
    internal const string LayoutExport = "SwiftBindings_ReverseContextLayout";

    private const string LibraryName = "SwiftBindingsRuntime";

    private static readonly object s_lock = new();
    private static Binding? s_binding;

    /// <summary>
    /// Allocates a reverse-callback context box owning <paramref name="payload"/> and returns it
    /// +1-retained.
    /// </summary>
    /// <param name="target">Address of the managed Cdecl callback the native stub invokes.</param>
    /// <param name="payload">
    /// <see cref="GCHandle.ToIntPtr(GCHandle)"/> of the delegate. On success the box owns it; on any
    /// exception the caller still does.
    /// </param>
    /// <returns>The box, retained once on the caller's behalf.</returns>
    /// <exception cref="ArgumentException">Either pointer is zero.</exception>
    /// <exception cref="InvalidOperationException">
    /// The native runtime lacks a reverse-context export, reports a layout other than version 1
    /// (target +16, payload +24), or could not create the box.
    /// </exception>
    public static IntPtr Create(IntPtr target, IntPtr payload) => Current.Create(target, payload);

    /// <summary>
    /// Releases one reference to a box returned by <see cref="Create"/>. When it is the last
    /// reference, the box frees its payload handle.
    /// </summary>
    /// <param name="context">The box. Zero is a no-op.</param>
    public static void Release(IntPtr context)
    {
        if (context == IntPtr.Zero) return;
        SwiftReleaseTrampoline.ReleaseRaw(context);
    }

    private static Binding Current
    {
        get
        {
            var binding = Volatile.Read(ref s_binding);
            if (binding != null) return binding;
            lock (s_lock)
            {
                binding = s_binding;
                if (binding == null)
                {
                    binding = BindRegisteredImage();
                    Volatile.Write(ref s_binding, binding);
                }
                return binding;
            }
        }
    }

    /// <summary>
    /// Binds to the native image the closure-context destroy callback is registered in, which is
    /// the only image whose boxes can free their payload.
    /// </summary>
    private static Binding BindRegisteredImage()
    {
        SwiftClosureContext.EnsureRegistered();
        var status = SwiftClosureContext.Status;

        if (SwiftClosureContext.UsesExternalResolver)
            return BindThroughExternalResolver();

        if (status is not (SwiftClosureContext.OwnerTokenStatus.Ready
                           or SwiftClosureContext.OwnerTokenStatus.RegisteredNotProcessVisible))
        {
            return Binding.Failed(
                $"Swift-convention callbacks need the SwiftBindingsRuntime reverse-context box, but the " +
                $"runtime's context-destroy callback is not registered ({status}: " +
                $"{SwiftClosureContext.RegistrationDiagnostic ?? "no diagnostic"}). There is no fallback.");
        }

        return BindImage(SwiftClosureContext.RegisteredImageHandle);
    }

    /// <summary>Resolves both exports from one loaded native image and checks its layout.</summary>
    internal static Binding BindImage(IntPtr imageHandle)
    {
        NativeLibrary.TryGetExport(imageHandle, LayoutExport, out var layout);
        NativeLibrary.TryGetExport(imageHandle, FactoryExport, out var factory);
        return BindExports(layout, factory, DescribeImage(layout != IntPtr.Zero ? layout : factory, imageHandle));
    }

    /// <summary>
    /// Validates a pair of resolved export addresses. A zero address is a missing export. The layout
    /// export is called once; the factory is never called here.
    /// </summary>
    internal static Binding BindExports(IntPtr layoutExport, IntPtr factoryExport, string imageDescription)
    {
        if (layoutExport == IntPtr.Zero)
            return Binding.Failed(MissingExportMessage(LayoutExport, imageDescription));
        if (factoryExport == IntPtr.Zero)
            return Binding.Failed(MissingExportMessage(FactoryExport, imageDescription));

        int version, targetOffset, payloadOffset;
        ((delegate* unmanaged[Cdecl]<int*, int*, int*, void>)layoutExport)(&version, &targetOffset, &payloadOffset);
        var mismatch = DescribeLayoutMismatch(version, targetOffset, payloadOffset, imageDescription);
        return mismatch != null ? Binding.Failed(mismatch) : new Binding(factoryExport, external: false, failure: null);
    }

    private static Binding BindThroughExternalResolver()
    {
        // An externally installed resolver cannot be asked for an image handle; reach both exports
        // through the same per-library P/Invoke route the destroy callback was registered through.
        int version, targetOffset, payloadOffset;
        try
        {
            ExternalNativeMethods.Layout(&version, &targetOffset, &payloadOffset);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Binding.Failed(MissingExportMessage(LayoutExport, $"the image the installed resolver selects ({ex.Message})"));
        }

        var mismatch = DescribeLayoutMismatch(version, targetOffset, payloadOffset, "the image the installed resolver selects");
        if (mismatch != null)
            return Binding.Failed(mismatch);

        // Resolve the factory import now, so a missing export fails here rather than at the first
        // Create. Null arguments make the native factory return null without allocating.
        try
        {
            ExternalNativeMethods.NewContext(IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Binding.Failed(MissingExportMessage(FactoryExport, $"the image the installed resolver selects ({ex.Message})"));
        }
        if (SwiftClosureContext.Status != SwiftClosureContext.OwnerTokenStatus.ExternalResolverUnverified)
        {
            return Binding.Failed(
                $"Swift-convention callbacks need the SwiftBindingsRuntime reverse-context box, but the " +
                $"installed resolver's runtime did not register the context-destroy callback " +
                $"({SwiftClosureContext.Status}: {SwiftClosureContext.RegistrationDiagnostic ?? "no diagnostic"}). There is no fallback.");
        }
        return new Binding(IntPtr.Zero, external: true, failure: null);
    }

    internal static string? DescribeLayoutMismatch(int version, int targetOffset, int payloadOffset, string imageDescription)
    {
        if (version == ExpectedLayoutVersion
            && targetOffset == ExpectedTargetOffset
            && payloadOffset == ExpectedPayloadOffset)
            return null;
        return $"SwiftBindingsRuntime reverse-context layout mismatch in {imageDescription}: Swift.Runtime expects " +
               $"version {ExpectedLayoutVersion} (target +{ExpectedTargetOffset}, payload +{ExpectedPayloadOffset}); " +
               $"the native image reports version {version} (target {FormatOffset(targetOffset)}, payload {FormatOffset(payloadOffset)}). " +
               "The managed and native runtime are from different builds. There is no fallback.";
    }

    private static string FormatOffset(int offset) => offset < 0 ? "not found" : $"+{offset}";

    private static string MissingExportMessage(string export, string imageDescription)
        => $"SwiftBindingsRuntime export '{export}' is missing from {imageDescription}. Swift-convention callbacks " +
           "need the reverse-context box; the managed and native runtime are from different builds. There is no fallback.";

    private static string DescribeImage(IntPtr anyExport, IntPtr imageHandle)
    {
        if (anyExport != IntPtr.Zero)
        {
            try
            {
                SwiftClosureContext.DlInfo info;
                if (SwiftClosureContext.Darwin.Dladdr(anyExport, &info) != 0 && info.FileName != IntPtr.Zero)
                    return $"'{Marshal.PtrToStringUTF8(info.FileName)}'";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Fall through to the handle description.
            }
        }
        return $"the native image with handle 0x{imageHandle:x}";
    }

    /// <summary>A checked binding to one native image's reverse-context factory, or the reason there is none.</summary>
    internal sealed class Binding
    {
        private readonly IntPtr _factory;
        private readonly bool _external;

        internal Binding(IntPtr factory, bool external, string? failure)
        {
            _factory = factory;
            _external = external;
            Failure = failure;
        }

        internal static Binding Failed(string failure) => new(IntPtr.Zero, external: false, failure);

        /// <summary>Why this binding cannot create boxes, or null when it can.</summary>
        internal string? Failure { get; }

        internal IntPtr Create(IntPtr target, IntPtr payload)
        {
            if (target == IntPtr.Zero)
                throw new ArgumentException("A reverse-context target must be a callback address.", nameof(target));
            if (payload == IntPtr.Zero)
                throw new ArgumentException("A reverse-context payload must be a GCHandle.", nameof(payload));
            if (Failure != null)
                throw new InvalidOperationException(Failure);

            var box = _external
                ? ExternalNativeMethods.NewContext(target, payload)
                : ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)_factory)(target, payload);
            if (box == IntPtr.Zero)
            {
                // The factory allocates nothing when it returns null, so the caller keeps the payload.
                throw new InvalidOperationException(
                    $"{FactoryExport} returned null: the SwiftBindingsRuntime image that owns the box has no " +
                    "registered context-destroy callback, so a box could never free its payload. There is no fallback.");
            }
            return box;
        }
    }

    private static class ExternalNativeMethods
    {
        [DllImport(LibraryName, EntryPoint = LayoutExport, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Layout(int* version, int* targetOffset, int* payloadOffset);

        [DllImport(LibraryName, EntryPoint = FactoryExport, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr NewContext(IntPtr target, IntPtr payload);
    }
}
