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
    /// <c>NewFromPayload</c>, and Dispose the wrapped copy. Returns the copy's
    /// <c>GetType()</c> so the caller can assert cross-assembly type identity
    /// on the round-tripped value.
    ///
    /// Ownership contract: <c>Language</c> declares
    /// <c>PayloadConstructionSemantics.Copy</c>, so <c>NewFromPayload</c> copies
    /// the value out of the marshalled buffer and the buffer keeps its own +1.
    /// It is VWT-destroyed before the raw allocation is freed.
    /// </summary>
    public static System.Type RoundTripLanguage(Swift.Foundation.Locale.Language value)
    {
        var metadata = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        var size = (int)metadata.Size;
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)size);
            var initialized = false;
            try
            {
                var span = new Span<byte>(buf, size);
                ((ISwiftObject)value).MarshalToSwift(ref span);
                initialized = true;
                var copy = (Swift.Foundation.Locale.Language)
                    SwiftObjectHelper<Swift.Foundation.Locale.Language>.NewFromPayload((IntPtr)buf);
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
                if (initialized)
                    metadata.ValueWitnessTable->Destroy(buf, metadata);
                NativeMemory.Free(buf);
            }
        }
    }

    /// <summary>
    /// Materializes every element through SwiftArray's unconstrained dispatch seam
    /// and returns the count when all values retain the canonical supplement type.
    /// </summary>
    public static int MaterializeLanguages(
        Swift.SwiftArray<Swift.Foundation.Locale.Language> languages)
    {
        var values = languages.ToArray();
        try
        {
            return values.All(value =>
                    ReferenceEquals(value.GetType(), typeof(Swift.Foundation.Locale.Language)))
                ? values.Length
                : -1;
        }
        finally
        {
            foreach (var value in values)
                value.Dispose();
        }
    }
}
