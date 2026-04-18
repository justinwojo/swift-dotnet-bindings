// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift.Foundation;

/// <summary>
/// Represents a Swift 'any Swift.Error' existential type.
/// This is a blittable struct wrapping <see cref="ExistentialContainer1"/> that provides
/// a meaningful public API type name instead of the raw container.
/// Implements <see cref="IExistentialContainer"/> so it works with existing marshalling paths
/// (SwiftOptional, MarshalToSwift/MarshalFromSwift).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnyError : IExistentialContainer, ISwiftExistentialConvertible<ExistentialContainer1>
{
    private ExistentialContainer1 _container;

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
    /// Creates an AnyError from an existing ExistentialContainer1.
    /// </summary>
    /// <param name="container">The existential container holding the Swift error value.</param>
    public AnyError(ExistentialContainer1 container) => _container = container;

    /// <inheritdoc/>
    public ExistentialContainer1 GetExistentialContainer() => _container;

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
            fixed (ExistentialContainer1* ptr = &_container)
            {
                var descPtr = PInvokesForAnyError._GetDescription(ptr);
                return SwiftMarshal.ReadErrorDescription(descPtr);
            }
        }
    }
}

internal static class PInvokesForAnyError
{
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_AnyError_TypeMetadata")]
    public static extern TypeMetadata _TypeMetadataAccessor();

    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_AnyError_GetDescription")]
    public static extern unsafe IntPtr _GetDescription(ExistentialContainer1* container);
}
