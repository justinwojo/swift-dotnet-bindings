// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides runtime environment detection for safe P/Invoke behavior from the GC finalizer thread.
/// </summary>
/// <remarks>
/// <para>
/// On .NET 10 iOS simulator, none of the standard Mono detection methods work:
/// <c>Type.GetType("Mono.Runtime")</c> returns null, <c>FrameworkDescription</c> says ".NET 10.0.3",
/// and <c>IsDynamicCodeSupported</c> is false (same as NativeAOT). The only reliable distinguisher
/// is <c>RuntimeIdentifier</c>: "iossimulator-arm64" for Mono AOT vs "ios-arm64" for NativeAOT device.
/// </para>
/// <para>
/// The finalizer crash occurs because Swift's @_cdecl destroy wrappers internally call VWT operations
/// (deinitialize), which trigger Mono's <c>jit-info.c:918</c> assertion from the finalizer thread.
/// On NativeAOT, all calls are statically compiled and safe from any thread.
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
internal static class SwiftRuntimeInfo
{
    /// <summary>
    /// True when running on a non-NativeAOT runtime (Mono, CoreCLR) where calling Swift destroy
    /// functions from the GC finalizer thread is unsafe. On these runtimes, only explicit Dispose()
    /// on a user thread triggers the destroy action.
    /// </summary>
    internal static readonly bool IsMonoRuntime = DetectNonNativeAotRuntime();

    /// <summary>
    /// True when running on NativeAOT (iOS device). False on Mono (iOS simulator)
    /// and CoreCLR (desktop macOS).
    /// Uses <c>RuntimeFeature.IsDynamicCodeSupported</c> (false on both NativeAOT and Mono AOT)
    /// combined with <c>IsMonoRuntime</c> to distinguish NativeAOT from Mono AOT.
    /// </summary>
    internal static readonly bool IsNativeAotRuntime = !IsMonoRuntime && !RuntimeFeature.IsDynamicCodeSupported;

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
