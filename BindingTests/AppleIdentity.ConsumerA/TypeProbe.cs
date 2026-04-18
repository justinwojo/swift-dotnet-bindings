// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;

// CA1416: callers in RuntimeTestsApp guard the actual invocation with
// OperatingSystem.IsIOSVersionAtLeast(16); the analyzer doesn't see that the
// guard is the only call site, so suppress here at the probe boundary.
#pragma warning disable CA1416

namespace AppleIdentity.ConsumerA;

/// <summary>
/// Session 6 / M8 probe. Exposes a stable type handle + metadata handle for a
/// SwiftBindings.Apple-owned supplement type so RuntimeTestsApp can compare
/// against the mirror probe in AppleIdentity.ConsumerB and assert both
/// assemblies resolve to the exact same System.Type and Swift TypeMetadata.
///
/// Also exposes a value factory (<see cref="CreateDefaultLanguage"/>) that
/// constructs a live Foundation.Locale.Language instance via a SwiftBindingsTestLib
/// @_cdecl helper, so the paired round-trip test in ConsumerB can exercise
/// payload ABI (MarshalToSwift + NewFromPayload + Dispose) across assemblies.
/// </summary>
public static class TypeProbe
{
    public static System.Type GetLanguageType() => typeof(Swift.Foundation.Locale.Language);

    public static TypeMetadata GetLanguageMetadata()
        => SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_CreateLocaleLanguage")]
    private static extern void CreateLocaleLanguage(IntPtr bufferPtr);

    /// <summary>
    /// Constructs a live <see cref="Swift.Foundation.Locale.Language"/> instance
    /// by having a Swift helper write an initialized value into a heap buffer,
    /// then wrapping the buffer through
    /// <c>SwiftObjectHelper&lt;Language&gt;.NewFromPayload</c>. The source buffer
    /// is destroyed via VWT and freed before returning; the caller owns the
    /// returned instance and must Dispose it.
    /// </summary>
    public static Swift.Foundation.Locale.Language CreateDefaultLanguage()
    {
        var metadata = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)metadata.Size);
            try
            {
                CreateLocaleLanguage((IntPtr)buf);
                var instance = (Swift.Foundation.Locale.Language)
                    SwiftObjectHelper<Swift.Foundation.Locale.Language>.NewFromPayload((IntPtr)buf);
                metadata.ValueWitnessTable->Destroy(buf, metadata);
                return instance;
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
    }
}
