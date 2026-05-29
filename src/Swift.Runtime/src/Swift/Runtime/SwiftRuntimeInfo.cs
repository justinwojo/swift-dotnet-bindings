// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides runtime environment detection for runtime-specific behavior differences.
/// </summary>
/// <remarks>
/// <para>
/// On .NET 10 iOS simulator, none of the standard Mono detection methods work:
/// <c>Type.GetType("Mono.Runtime")</c> returns null, <c>FrameworkDescription</c> says ".NET 10.0.3",
/// and <c>IsDynamicCodeSupported</c> is false (same as NativeAOT). The only reliable distinguisher
/// is <c>RuntimeIdentifier</c>: "iossimulator-arm64" for Mono AOT vs "ios-arm64" for NativeAOT device.
/// </para>
/// <para>
/// Used by <c>RuntimeLimitations</c> to determine which runtime-specific workarounds are needed
/// (e.g., Mono JIT assertion with CallConvSwift, NativeAOT float struct parameter issues).
/// Note: GC finalizer cleanup of Swift structs is safe on all runtimes — VWT Destroy is called
/// via a Cdecl trampoline (<c>SBW_VWTDestroy</c>) whose DllImport stub is resolved by the
/// runtime loader without JIT compilation.
/// </para>
/// <para>
/// Three-way runtime taxonomy:
/// <list type="bullet">
/// <item>Desktop CoreCLR (macOS): <c>IsMonoRuntime=false</c>, <c>IsDynamicCodeSupported=true</c></item>
/// <item>iOS Simulator (Mono AOT): <c>IsMonoRuntime=true</c>, <c>IsDynamicCodeSupported=false</c></item>
/// <item>iOS Device (NativeAOT): <c>IsMonoRuntime=false</c>, <c>IsDynamicCodeSupported=false</c></item>
/// </list>
/// </para>
/// </remarks>
public static class SwiftRuntimeInfo
{
    /// <summary>
    /// True when running on a non-NativeAOT runtime (Mono, CoreCLR).
    /// Used by <see cref="RuntimeLimitations"/> to gate runtime-specific workarounds.
    /// Note: VWT Destroy from the GC finalizer is safe on all runtimes via the Cdecl trampoline.
    /// </summary>
    public static readonly bool IsMonoRuntime = DetectNonNativeAotRuntime();

    /// <summary>
    /// True when running on NativeAOT (iOS device). False on Mono (iOS simulator)
    /// and CoreCLR (desktop macOS).
    /// Uses <c>RuntimeFeature.IsDynamicCodeSupported</c> (false on both NativeAOT and Mono AOT)
    /// combined with <c>IsMonoRuntime</c> to distinguish NativeAOT from Mono AOT.
    /// Public so generated binding assemblies can gate eager static-cctor metadata
    /// caching on NativeAOT (mirrors the SwiftArray eager-init pattern).
    /// </summary>
    public static readonly bool IsNativeAotRuntime = !IsMonoRuntime && !RuntimeFeature.IsDynamicCodeSupported;

    private static bool DetectNonNativeAotRuntime()
    {
        // Classic Mono detection (works on .NET 8 and earlier)
        if (Type.GetType("Mono.Runtime") != null)
            return true;

        // .NET 10+ iOS: RuntimeIdentifier distinguishes simulator (Mono AOT) from device (NativeAOT).
        // On simulator: "iossimulator-arm64" or "iossimulator-x64"
        // On device: "ios-arm64"
        // On macOS: "osx-arm64" or "osx-x64" (CoreCLR — Destroy is safe)
        try
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            if (rid != null && rid.Contains("simulator", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // RuntimeInformation may not be available in all contexts
        }

        return false;
    }
}
