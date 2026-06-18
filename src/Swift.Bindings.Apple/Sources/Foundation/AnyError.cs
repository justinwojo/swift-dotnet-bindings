// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift.Foundation;

/// <summary>
/// Represents a Swift 'any Swift.Error' existential value.
/// <para>
/// A Swift <c>any Error</c> is a single boxed reference (swift_errorRelease-managed), stored in
/// <see cref="ExistentialContainer1.Payload0"/>; the remaining container words are unused. When a
/// Swift API transfers an error to C# at +1 (a return value or an extracted enum payload), the
/// resulting <see cref="AnyError"/> ADOPTS that retain and releases it on <see cref="Dispose"/> or
/// finalization (<c>ownsContainer: true</c>). Borrowed errors — a closure parameter Swift owns for
/// the duration of the callback — construct non-owning and release nothing.
/// </para>
/// <para>
/// Implements <see cref="ISwiftObject"/> so the type participates in deterministic-disposal tooling
/// (<c>using</c>, <see cref="SwiftDisposeScope"/>, the dispose analyzer) and
/// <see cref="IExistentialContainer"/>/<see cref="ISwiftExistentialConvertible{TContainer}"/> so it
/// flows through the existing existential marshalling paths.
/// </para>
/// </summary>
public sealed class AnyError : ISwiftObject, IExistentialContainer, ISwiftExistentialConvertible<ExistentialContainer1>
{
    private ExistentialContainer1 _container;
    private readonly bool _ownsContainer;
    private bool _disposed;

    /// <summary>
    /// Registers Swift type metadata for 'any Error' so that SwiftResult&lt;TSuccess, AnyError&gt;
    /// can obtain the correct metadata via TypeMetadata.GetTypeMetadataOrThrow&lt;AnyError&gt;().
    /// </summary>
    static AnyError()
    {
        try
        {
            var metadata = PInvokesForAnyError._TypeMetadataAccessor();
            if (metadata.IsValid)
                TypeMetadata.RegisterMetadata(typeof(AnyError), metadata);
        }
        catch
        {
            // SwiftBindingsRuntime may not be loaded yet (e.g., during unit tests).
            // Metadata will be unavailable but that's OK for non-Result paths.
        }
    }

    /// <summary>
    /// Creates a non-owning <see cref="AnyError"/> over an existing container. Use for borrowed
    /// errors (closure parameters) where Swift retains ownership; no retain is adopted or released.
    /// </summary>
    /// <param name="container">The existential container holding the Swift error value.</param>
    public AnyError(ExistentialContainer1 container) : this(container, ownsContainer: false) { }

    /// <summary>
    /// Creates an <see cref="AnyError"/> over an existing container, optionally adopting the boxed
    /// error's +1 retain. When <paramref name="ownsContainer"/> is true the instance releases that
    /// retain on <see cref="Dispose"/> or finalization and registers with the active
    /// <see cref="SwiftDisposeScope"/>; otherwise it owns nothing and suppresses finalization.
    /// </summary>
    /// <param name="container">The existential container holding the Swift error value.</param>
    /// <param name="ownsContainer">True if this instance adopts the boxed error's +1 retain.</param>
    public AnyError(ExistentialContainer1 container, bool ownsContainer)
    {
        _container = container;
        _ownsContainer = ownsContainer;
        if (ownsContainer)
            SwiftDisposeScope.TryRegister(this);
        else
            GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public ExistentialContainer1 GetExistentialContainer()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AnyError));
        return _container;
    }

    /// <inheritdoc/>
    public IntPtr Payload0 { get => _container.Payload0; set => _container.Payload0 = value; }

    /// <inheritdoc/>
    public IntPtr Payload1 { get => _container.Payload1; set => _container.Payload1 = value; }

    /// <inheritdoc/>
    public IntPtr Payload2 { get => _container.Payload2; set => _container.Payload2 = value; }

    /// <inheritdoc/>
    public TypeMetadata ObjectMetadata { get => _container.ObjectMetadata; set => _container.ObjectMetadata = value; }

    /// <inheritdoc/>
    public IntPtr this[int index]
    {
        get => _container[index];
        set => _container[index] = value;
    }

    /// <inheritdoc/>
    public int Count => _container.Count;

    /// <inheritdoc/>
    public int SizeOf => _container.SizeOf;

    /// <inheritdoc/>
    public IntPtr CopyTo(IntPtr memory) => _container.CopyTo(memory);

    /// <inheritdoc/>
    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
        => _container.CopyTo(ref container);

    /// <summary>
    /// Gets a human-readable description of the Swift error by calling back into the Swift runtime.
    /// Uses <c>String(describing:)</c> on the error value, which returns the case name for
    /// Swift enum errors (e.g. "divisionByZero") and the full object description for NSError
    /// subclasses. Note: this is not equivalent to <c>NSError.localizedDescription</c> —
    /// it uses Swift's generic string conversion, which may include domain and userInfo details.
    /// </summary>
    public unsafe string LocalizedDescription
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AnyError));
            fixed (ExistentialContainer1* ptr = &_container)
            {
                var descPtr = PInvokesForAnyError._GetDescription(ptr);
                return SwiftMarshal.ReadErrorDescription(descPtr);
            }
        }
    }

    #region ISwiftObject

    /// <summary>Returns the Swift <c>any Error</c> existential type metadata.</summary>
    public static TypeMetadata GetTypeMetadata() => PInvokesForAnyError._TypeMetadataAccessor();

    /// <summary>
    /// Wrap factory for Swift→C# marshalling. Reads the boxed error pointer and returns a
    /// non-owning instance — <see cref="NewFromPayload"/> is the borrowed read path; owned
    /// transfers go through the <c>ownsContainer: true</c> constructor.
    /// </summary>
    public static unsafe ISwiftObject NewFromPayload(IntPtr payload)
        => new AnyError(new ExistentialContainer1 { Payload0 = *(IntPtr*)payload });

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Inline;

    /// <inheritdoc/>
    public unsafe int MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AnyError));
        // `any Error` is a single boxed reference: write the 8-byte box pointer, not the container.
        if (swiftDestSpan.Length < IntPtr.Size)
            throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
        fixed (byte* dest = swiftDestSpan)
        {
            *(IntPtr*)dest = _container.Payload0;
        }
        return IntPtr.Size;
    }

    /// <inheritdoc/>
    IntPtr ISwiftObject.SwiftHandle => _container.Payload0;

    /// <inheritdoc/>
    public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        => throw new NotSupportedException(
            "Protocol conformance descriptor is not available for AnyError; it is the terminal "
            + "'any Error' existential, not a protocol proxy.");

    #endregion

    #region Disposal

    /// <summary>
    /// Releases the adopted boxed error's +1 retain (when this instance owns the container).
    /// Non-owning instances release nothing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        ReleaseAdoptedBox();
    }

    /// <summary>
    /// Finalizer — releases an adopted (<c>ownsContainer: true</c>) boxed error if the consumer
    /// never called <see cref="Dispose"/>. Non-owning instances suppress finalization in the
    /// constructor, so this only runs for owners.
    /// </summary>
    ~AnyError()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAdoptedBox();
    }

    // Releases the boxed (any Error) at Payload0 via the Cdecl runtime helper. Gated on
    // _ownsContainer: only Swift→C# +1 transfers adopt a retain. The helper releases ONLY the
    // 8-byte box, leaving the unused container words untouched (any Error is not a 5-word opaque
    // existential, so the generic existential VWT destroy would misread the unused words).
    private unsafe void ReleaseAdoptedBox()
    {
        if (!_ownsContainer || _container.Payload0 == IntPtr.Zero)
            return;
        try
        {
            fixed (ExistentialContainer1* ptr = &_container)
            {
                PInvokesForAnyError._Destroy((IntPtr)ptr);
            }
            _container.Payload0 = IntPtr.Zero;
        }
        catch
        {
            // SwiftBindingsRuntime unavailable (e.g. unit tests) — skip the release rather than
            // throw from Dispose/finalize.
        }
    }

    #endregion
}

internal static class PInvokesForAnyError
{
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_AnyError_TypeMetadata")]
    public static extern TypeMetadata _TypeMetadataAccessor();

    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_AnyError_GetDescription")]
    public static extern unsafe IntPtr _GetDescription(ExistentialContainer1* container);

    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_AnyError_Destroy")]
    public static extern void _Destroy(IntPtr container);
}
