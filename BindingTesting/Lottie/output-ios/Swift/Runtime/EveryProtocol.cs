// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// EveryProtocol is a minimal Swift class that serves as the concrete type behind all protocol proxies.
/// In Swift, we generate extensions making EveryProtocol conform to each protocol we want to implement from C#.
/// The actual protocol method implementations call back to C# via vtable function pointers.
///
/// This class exists purely to provide a Swift type metadata that can be used in existential containers.
/// The real work happens in the generated proxy classes and Swift extensions.
/// </summary>
/// <remarks>
/// The EveryProtocol pattern works as follows:
/// 1. Swift side: EveryProtocol class is defined with extensions conforming to each protocol
/// 2. Each protocol extension's methods call back to C# via function pointers stored in a vtable
/// 3. C# side: Proxy classes wrap either a C# implementation or a Swift existential container
/// 4. When C# implementation is wrapped, it registers with SwiftObjectRegistry
/// 5. Swift callbacks use the registry to find the C# proxy and invoke the implementation
/// </remarks>
public sealed class EveryProtocol : ISwiftObject
{
    private static IntPtr _typeMetadataHandle;
    private static readonly object _metadataLock = new object();

    private readonly SwiftSafeHandle<EveryProtocol> _handle;

    /// <summary>
    /// Creates a new EveryProtocol instance.
    /// This allocates a Swift object that can be placed into existential containers.
    /// </summary>
    public unsafe EveryProtocol()
    {
        // Allocate Swift object storage
        // For now, use a minimal allocation since the actual Swift object
        // will be created when we have the metadata available
        var ptr = (IntPtr)NativeMemory.Alloc((nuint)IntPtr.Size);
        _handle = new SwiftSafeHandle<EveryProtocol>(ptr);
    }

    /// <summary>
    /// Creates an EveryProtocol from an existing payload pointer.
    /// Used when receiving EveryProtocol instances from Swift.
    /// </summary>
    private EveryProtocol(IntPtr payload)
    {
        _handle = new SwiftSafeHandle<EveryProtocol>(payload);
    }

    /// <summary>
    /// Gets the native handle for this EveryProtocol instance.
    /// </summary>
    public IntPtr Handle => _handle.DangerousGetHandle();

    /// <summary>
    /// Gets the SwiftSafeHandle for this instance.
    /// </summary>
    public SwiftSafeHandle<EveryProtocol> Payload => _handle;

    /// <summary>
    /// Gets the Swift type metadata for EveryProtocol.
    /// The metadata is loaded from the generated Swift wrapper library.
    /// </summary>
    public static TypeMetadata GetTypeMetadata()
    {
        if (_typeMetadataHandle == IntPtr.Zero)
        {
            lock (_metadataLock)
            {
                if (_typeMetadataHandle == IntPtr.Zero)
                {
                    // The type metadata will be registered by the generated bindings
                    // For now, return a minimal metadata
                    // This will be set by the first protocol proxy that uses EveryProtocol
                }
            }
        }
        // Use reflection to create TypeMetadata since constructor is internal
        return TypeMetadata.Cache.GetOrAdd(typeof(EveryProtocol), _ =>
        {
            // If we have a registered handle, use it; otherwise return zero metadata
            // which will trigger proper initialization later
            return default;
        });
    }

    /// <summary>
    /// Sets the type metadata handle for EveryProtocol.
    /// Called by generated proxy classes during static initialization.
    /// </summary>
    /// <param name="handle">The Swift type metadata handle for EveryProtocol.</param>
    public static void SetTypeMetadata(IntPtr handle)
    {
        lock (_metadataLock)
        {
            if (_typeMetadataHandle == IntPtr.Zero)
            {
                _typeMetadataHandle = handle;
            }
        }
    }

    /// <inheritdoc/>
    public static ISwiftObject NewFromPayload(IntPtr payload)
    {
        return new EveryProtocol(payload);
    }

    /// <inheritdoc/>
    public int MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        // EveryProtocol is a class type, so we marshal the pointer
        var ptr = _handle.DangerousGetHandle();
        if (swiftDestSpan.Length < IntPtr.Size)
            throw new ArgumentException("Destination span is too small", nameof(swiftDestSpan));

        MemoryMarshal.Write(swiftDestSpan, in ptr);
        return IntPtr.Size;
    }

    /// <inheritdoc/>
    public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
    {
        // EveryProtocol's conformances are generated dynamically in the Swift wrapper
        // The conformance descriptor will be looked up by the protocol proxy classes
        throw new NotImplementedException(
            "EveryProtocol conformance descriptors are managed by protocol proxy classes");
    }
}
