// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
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
///
/// EveryProtocol instances must be created from Swift (via SBW_CreateEveryProtocol) to ensure
/// they are valid ARC-managed objects. Raw C# allocations produce fake pointers that crash
/// when Swift tries to retain/release them during existential container operations.
/// </remarks>
[DebuggerDisplay("{DebugDisplay}")]
public sealed class EveryProtocol : ISwiftObject
{
    private string DebugDisplay => _handle?.IsInvalid != false
        ? "EveryProtocol [DISPOSED]"
        : $"EveryProtocol (0x{Handle:X})";

    private readonly SwiftClassHandle<EveryProtocol> _handle;

    /// <summary>
    /// Creates an EveryProtocol from a retained Swift object pointer.
    /// The pointer must carry a +1 ARC retain count (from Unmanaged.passRetained).
    /// This constructor takes ownership of the retain and releases it on Dispose/finalize.
    /// </summary>
    /// <param name="swiftPointer">
    /// A retained Swift EveryProtocol pointer from SBW_CreateEveryProtocol.
    /// </param>
    public EveryProtocol(IntPtr swiftPointer)
    {
        _handle = new SwiftClassHandle<EveryProtocol>(swiftPointer);
    }

    /// <summary>
    /// Gets the native handle for this EveryProtocol instance.
    /// </summary>
    public IntPtr Handle => _handle.DangerousGetHandle();

    IntPtr ISwiftObject.SwiftHandle => _handle.DangerousGetHandle();

    /// <summary>
    /// Not supported on this runtime type. EveryProtocol's Swift type metadata is no longer
    /// held in a process-global latch here; each generated protocol proxy now sources its
    /// EveryProtocol metadata from its OWN module's <c>NativeMethods.GetEveryProtocolMetadata</c>
    /// accessor (the per-proxy <c>s_everyProtocolMetadata</c> static field). The old first-wins latch
    /// returned whichever binding initialized first, so module B's opaque proxies could read
    /// module A's metadata in a multi-binding app. There is no module-agnostic metadata to
    /// return from this shared runtime type, so this throws rather than silently handing back a
    /// zeroed <see cref="TypeMetadata"/>.
    /// </summary>
    public static TypeMetadata GetTypeMetadata()
        => throw new NotSupportedException(
            "EveryProtocol type metadata is per-binding-module; resolve it from the generated " +
            "proxy's own NativeMethods.GetEveryProtocolMetadata accessor, not this shared runtime type.");

    /// <inheritdoc/>
    public static ISwiftObject NewFromPayload(IntPtr payload)
    {
        return new EveryProtocol(payload);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

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

    /// <inheritdoc/>
    public void Dispose() => _handle?.Dispose();
}
