// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Threading;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// The quality-of-service class a global concurrent dispatch queue schedules its work at.
/// Mirrors <c>Dispatch.DispatchQoS.QoSClass</c>; the numeric values are the underlying
/// <c>qos_class_t</c> constants libdispatch keys its global queues on.
/// https://developer.apple.com/documentation/dispatch/dispatchqos/qosclass
/// </summary>
public enum DispatchQoSClass
{
    /// <summary>No explicit quality of service (<c>QOS_CLASS_UNSPECIFIED</c>).</summary>
    Unspecified = 0x00,
    /// <summary>Work the user is not aware of (<c>QOS_CLASS_BACKGROUND</c>).</summary>
    Background = 0x09,
    /// <summary>Work that may take some time and does not block the user (<c>QOS_CLASS_UTILITY</c>).</summary>
    Utility = 0x11,
    /// <summary>The default priority a global queue runs at (<c>QOS_CLASS_DEFAULT</c>).</summary>
    Default = 0x15,
    /// <summary>Work the user started and is waiting on (<c>QOS_CLASS_USER_INITIATED</c>).</summary>
    UserInitiated = 0x19,
    /// <summary>Work that must complete to keep the UI responsive (<c>QOS_CLASS_USER_INTERACTIVE</c>).</summary>
    UserInteractive = 0x21,
}

/// <summary>
/// Represents Dispatch.DispatchQueue - an object that manages the execution of tasks on the app's main thread or on a background thread.
/// https://developer.apple.com/documentation/dispatch/dispatchqueue
/// </summary>
/// <remarks>
/// <para>
/// <c>DispatchQueue</c> is the Swift name of the Objective-C class <c>OS_dispatch_queue</c>: a Swift
/// value of that type is a single retained object pointer, and that is the shape this wrapper holds.
/// The <see cref="Payload"/> handle's value IS the object pointer — the same class convention every
/// generated binding uses when it marshals a <c>DispatchQueue</c> argument (it copies the address
/// of the pointer, not the pointer itself) or adopts one a Swift call returned.
/// </para>
/// <para>
/// Ownership never goes through the Swift-native reference counting entry points: an Objective-C
/// object's refcount lives in its <c>isa</c>-side storage, so the queue is retained and released
/// through the kind-dispatching <c>swift_unknownObjectRetain</c>/<c>swift_unknownObjectRelease</c>
/// pair (<see cref="Arc.UnknownObjectRetain"/>, <see cref="Arc.UnknownObjectReleaseFinalizerSafe"/>).
/// The payload <see cref="SwiftSafeHandle{T}"/> owns the reference and releases it only after all
/// <c>DangerousAddRef</c>/PInvoke pins have left. Its object-pointer release mode never treats the
/// queue as a value buffer: it runs neither value-witness destroy nor buffer free.
/// </para>
/// <para>
/// The two queues libdispatch vends through this type — the main queue and the global concurrent
/// queues — are process-lifetime singletons whose retain and release are no-ops, so disposing or
/// dropping one of these wrappers is always safe. The ownership dance is still kept uniform so a
/// queue a Swift library hands back (adopted at +1 through <see cref="ISwiftObject.NewFromPayload"/>)
/// is balanced exactly once.
/// </para>
/// </remarks>
public sealed class DispatchQueue : ISwiftObject, ISwiftStruct, IDisposable
{
    private const string ObjCClassName = "OS_dispatch_queue";
    private const string MainQueueSymbol = "_dispatch_main_q";

    private readonly SwiftSafeHandle<DispatchQueue> _payload;
    private int _disposed;

    private static TypeMetadata? _cachedMetadata;
    private static IntPtr _cachedMainQueue;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift. The handle's value is the retained
    /// <c>OS_dispatch_queue</c> object pointer.
    /// </summary>
    public SwiftSafeHandle<DispatchQueue> Payload
    {
        get
        {
            ThrowIfDisposed();
            return _payload;
        }
    }

    /// <summary>
    /// Gets the main dispatch queue associated with the main thread.
    /// </summary>
    public static DispatchQueue Main => Adopt(Arc.UnknownObjectRetain(MainQueueObject));

    /// <summary>
    /// Gets the global concurrent queue with the default quality of service — the queue Swift's
    /// <c>DispatchQueue.global()</c> returns when its <c>qos</c> argument is left at its default.
    /// </summary>
    public static DispatchQueue Global() => Global(DispatchQoSClass.Default);

    /// <summary>
    /// Gets the global concurrent queue with the specified quality of service.
    /// </summary>
    /// <param name="qos">The quality-of-service class the queue schedules its work at.</param>
    public static DispatchQueue Global(DispatchQoSClass qos)
    {
        var queue = dispatch_get_global_queue((nint)qos, 0);
        if (queue == IntPtr.Zero)
            throw new SwiftRuntimeException($"libdispatch returned no global queue for quality-of-service class {qos}.");
        return Adopt(Arc.UnknownObjectRetain(queue));
    }

    /// <summary>
    /// Gets the label the queue was created with — <c>com.apple.main-thread</c> for the main queue,
    /// <c>com.apple.root.*-qos</c> for the global queues — or an empty string for an unlabeled queue.
    /// </summary>
    public string Label
    {
        get
        {
            ThrowIfDisposed();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                return Marshal.PtrToStringUTF8(dispatch_queue_get_label(_payload.DangerousGetHandle())) ?? string.Empty;
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get
        {
            ThrowIfDisposed();
            return _payload.DangerousGetHandle();
        }
    }

    /// <summary>
    /// Borrowed-marshal hook: the wrapper was built around a +0 pointer somebody else owns, so it
    /// must not release on Dispose or finalize. The handle still closes normally, but owns no
    /// native reference to balance.
    /// </summary>
    void ISwiftObject.SuppressPayloadFinalizer() => _payload.MarkObjectBorrowed();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= ObjCInterop.GetTypeMetadata(ObjCClassName);
    }

    /// <summary>
    /// Adopts a +1 object pointer a Swift call returned (class return convention: the callee's
    /// <c>@owned</c> result is exactly one count, balanced by this wrapper's release).
    /// </summary>
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return Adopt(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = _cachedMetadata ??= ObjCInterop.GetTypeMetadata(ObjCClassName);
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    // Class convention: the Swift value is the pointer itself, so the value witness
                    // copies from the ADDRESS of our pointer (retaining the object) — never from the
                    // object's own bytes.
                    IntPtr selfPtr = _payload.DangerousGetHandle();
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
    {
        throw new SwiftRuntimeException($"Protocol conformance not implemented for DispatchQueue and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Construction

    private DispatchQueue(IntPtr retainedObject)
    {
        _payload = new SwiftSafeHandle<DispatchQueue>(retainedObject, isObjectPointer: true);
    }

    private static DispatchQueue Adopt(IntPtr retainedObject)
    {
        if (retainedObject == IntPtr.Zero)
            throw new SwiftRuntimeException("Cannot wrap a null dispatch queue.");
        return new DispatchQueue(retainedObject);
    }

    /// <summary>
    /// The main queue is a data symbol (<c>_dispatch_main_q</c>) in libSystem, not a function: its
    /// address is the queue object. Resolving this stable C symbol avoids depending on the Swift
    /// overlay getter's mangled name or calling convention.
    /// </summary>
    private static IntPtr MainQueueObject
    {
        get
        {
            if (_cachedMainQueue != IntPtr.Zero)
                return _cachedMainQueue;
            var libSystem = NativeLibrary.Load(KnownLibraries.LibSystem);
            if (!NativeLibrary.TryGetExport(libSystem, MainQueueSymbol, out var mainQueue) || mainQueue == IntPtr.Zero)
                throw new SwiftRuntimeException($"libSystem does not export {MainQueueSymbol}; cannot resolve the main dispatch queue.");
            _cachedMainQueue = mainQueue;
            return mainQueue;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0 || _payload.IsClosed)
            throw new ObjectDisposedException(nameof(DispatchQueue));
    }

    #endregion

    #region P/Invoke Declarations

    [DllImport(KnownLibraries.LibSystem, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dispatch_get_global_queue(nint identifier, nuint flags);

    [DllImport(KnownLibraries.LibSystem, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dispatch_queue_get_label(IntPtr queue);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the DispatchQueue and releases its reference to the underlying queue object.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _payload.Dispose();
    }

    #endregion
}
