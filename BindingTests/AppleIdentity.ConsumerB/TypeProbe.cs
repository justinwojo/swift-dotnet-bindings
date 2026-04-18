// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;

// CA1416: see ConsumerA/TypeProbe.cs for rationale.
#pragma warning disable CA1416

namespace AppleIdentity.ConsumerB;

/// <summary>
/// Mirror of AppleIdentity.ConsumerA.TypeProbe. See ConsumerA/TypeProbe.cs.
///
/// Also exposes <see cref="AcceptLanguageTyped"/> and
/// <see cref="RoundTripLanguage"/> so a cross-assembly round-trip test can
/// assert that a Language produced in ConsumerA is observed with the same
/// <c>typeof(T)</c> here and survives a MarshalToSwift + NewFromPayload cycle
/// without loading a duplicate type.
/// </summary>
public static class TypeProbe
{
    public static System.Type GetLanguageType() => typeof(Swift.Foundation.Locale.Language);

    public static TypeMetadata GetLanguageMetadata()
        => SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();

    /// <summary>
    /// Returns <c>value.GetType()</c> observed in ConsumerB. Paired with
    /// ConsumerA's factory, this proves both assemblies see the same
    /// <c>System.Type</c> instance for a live supplement value.
    /// </summary>
    public static System.Type AcceptLanguageTyped(Swift.Foundation.Locale.Language value)
        => value.GetType();

    /// <summary>
    /// Exercises the supplement's payload ABI end-to-end from ConsumerB:
    /// <c>MarshalToSwift</c> the input into a fresh buffer, wrap the buffer via
    /// <c>NewFromPayload</c>, destroy the source copy via VWT, and Dispose the
    /// wrapped copy. Returns the copy's <c>GetType()</c> so the caller can
    /// assert cross-assembly type identity on the round-tripped value.
    /// </summary>
    public static System.Type RoundTripLanguage(Swift.Foundation.Locale.Language value)
    {
        var metadata = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        var size = (int)metadata.Size;
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)size);
            try
            {
                var span = new Span<byte>(buf, size);
                ((ISwiftObject)value).MarshalToSwift(ref span);
                var copy = (Swift.Foundation.Locale.Language)
                    SwiftObjectHelper<Swift.Foundation.Locale.Language>.NewFromPayload((IntPtr)buf);
                metadata.ValueWitnessTable->Destroy(buf, metadata);
                try
                {
                    return copy.GetType();
                }
                finally
                {
                    copy.Dispose();
                }
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
    }
}
