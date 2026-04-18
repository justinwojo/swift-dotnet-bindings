// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace Swift.Runtime;

/// <summary>
/// Centralized DllImport resolver for Swift framework loading on Apple platforms.
/// Maps DllImport library names to framework paths (@rpath/{name}.framework/{name})
/// with fallback to bare dylib paths (@rpath/lib{name}.dylib).
/// Generated bindings call RegisterForAssembly() from [ModuleInitializer].
/// </summary>
public static class SwiftFrameworkResolver
{
    /// <summary>
    /// Auto-registers the framework resolver for the Swift.Runtime assembly itself.
    /// This ensures DllImport("SwiftBindingsRuntime") in SwiftString, TypeMetadata, etc.
    /// resolves to @rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime on iOS device.
    /// </summary>
#pragma warning disable CA2255 // ModuleInitializer is intentional — library needs self-registration
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void InitializeRuntime()
    {
        // Register the shared process-exit guard so finalizer-triggered Swift
        // releases (SwiftClassHandle, ProxyLifetimeTracker) short-circuit during
        // shutdown. Otherwise relying on lazy static init means the first release
        // path may run before AppDomain.ProcessExit has been wired up.
        SwiftExitGuard.EnsureInitialized();

        RegisterAlcFallback();
        RegisterForAssembly(typeof(SwiftFrameworkResolver).Assembly);

        // Pre-register NewFromPayload factories for all non-generic Swift.Runtime ISwiftObject types.
        // On NativeAOT with NuGet packages (not project references), the trimmer may strip
        // explicit interface implementations (ISwiftObject.NewFromPayload), causing
        // MarshalFromSwift<T> reflection fallback to fail with "Failed to find NewFromPayload".
        // Static virtual dispatch here ensures ILC preserves the method and populates the
        // factory cache before any marshalling call.
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.SwiftString>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.Data>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.URL>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.URLRequest>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyHashable>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyType>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.Hasher>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.DispatchQueue>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.CIContext>();
        // Removed Swift.* hand-rolled wrappers for ObjC-imported classes (URLResponse,
        // UIImage, NSImage, NSColor, OperationQueue): they imported `$sSo<ObjCClassName>...`
        // mangled symbols from Swift overlay libraries, but Swift never emits dispatch
        // thunks for ObjC-imported class members — those properties dispatch via
        // objc_msgSend at the call site, not via a Swift function. The helper classes
        // threw `EntryPointNotFoundException` on every method call. The TypeDB now maps
        // these types directly to the .NET iOS / Xamarin ObjC bindings
        // (Foundation.NSUrlResponse, UIKit.UIImage, AppKit.NSImage, AppKit.NSColor,
        // Foundation.NSOperationQueue), which dispatch correctly through the ObjC runtime.
        // CIContext still flows through Swift.CIContext pending a dedicated remap session.
    }

    /// <summary>
    /// Registers the standard Swift framework resolver for an assembly.
    /// Safe to call multiple times — subsequent calls are silently ignored.
    /// Also installs the process-wide ALC fallback on first call, so any assembly
    /// that never called this (hand-written consumers, third-party code) still
    /// gets framework-path resolution via the AssemblyLoadContext fallback event.
    /// </summary>
    public static void RegisterForAssembly(Assembly assembly)
    {
        RegisterAlcFallback();

        try
        {
            NativeLibrary.SetDllImportResolver(assembly, ResolveSwiftFramework);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly.
            // Expected when binding .cs is compiled into consumer assembly
            // (ModuleInitializer fires before consumer's Main).
        }
    }

    private static int s_alcFallbackRegistered;

    /// <summary>
    /// Installs a process-wide <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>
    /// handler as a safety net for assemblies that never called
    /// <see cref="RegisterForAssembly"/>. The documented .NET P/Invoke load order is:
    /// per-assembly <c>DllImportResolver</c> → <c>ALC.LoadUnmanagedDll</c> → built-in
    /// native probing → <c>ALC.ResolvingUnmanagedDll</c>. Per-assembly registrations
    /// still win on the hot path; this only catches misses.
    ///
    /// Safe on both Mono (iOS simulator) and NativeAOT (iOS device) — NativeAOT's lazy
    /// P/Invoke fixup routes through <c>GetResolvedUnmanagedDll</c> which raises this
    /// event, and Mono's ALC implementation has a <c>DynamicDependency</c> keeping the
    /// event accessor rooted against trimming.
    ///
    /// This does NOT address <a href="https://github.com/dotnet/macios/issues/25008">
    /// dotnet/macios#25008</a>: that bug is about statically linked NativeReference
    /// symbols becoming local-visibility when DllImports are redirected to the main
    /// binary. Our model is dynamic <c>@rpath/X.framework/X</c> loading, a different
    /// mechanism, so the fallback is safe here — but a future static-link mode would
    /// need its own symbol-export story.
    /// </summary>
    private static void RegisterAlcFallback()
    {
        if (Interlocked.Exchange(ref s_alcFallbackRegistered, 1) == 0)
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSwiftFrameworkFromAlc;
    }

    private static IntPtr ResolveSwiftFrameworkFromAlc(Assembly assembly, string libraryName)
        => ResolveSwiftFramework(libraryName, assembly, searchPath: null);

    /// <summary>
    /// Returns true when <paramref name="libraryName"/> is already a dyld-style path
    /// (<c>@rpath/...</c>, <c>@executable_path/...</c>, <c>@loader_path/...</c>, or an
    /// absolute filesystem path) and should NOT have the
    /// <c>@rpath/{name}.framework/{name}</c> prefix applied on top of it. Apple-framework
    /// target bindings emit <c>[LibraryImport("@rpath/StoreKit.framework/StoreKit")]</c>
    /// for direct metadata accessors, and those strings must be passed to dyld verbatim
    /// rather than re-prefixed.
    ///
    /// We match the three dyld load-command tokens explicitly rather than accepting any
    /// <c>@</c>-prefixed string, so a malformed input like <c>@foo/bar</c> falls through
    /// to the normal framework-name search path (where it will fail cleanly) instead of
    /// silently bypassing the standard resolver.
    /// </summary>
    internal static bool IsDyldStylePath(string libraryName) =>
        libraryName.StartsWith("@rpath/", StringComparison.Ordinal)
        || libraryName.StartsWith("@executable_path/", StringComparison.Ordinal)
        || libraryName.StartsWith("@loader_path/", StringComparison.Ordinal)
        || (libraryName.Length > 0 && libraryName[0] == '/');

    /// <summary>
    /// The search paths tried for a bare Swift module / framework name, in order.
    ///
    /// The <c>/System/Library/Frameworks/{name}.framework/{name}</c> fallback lets the
    /// Apple supplement emit bare DllImport names (e.g. <c>"CryptoKit"</c>) without
    /// triggering the macios linker's <c>.framework/</c> substring scan — which otherwise
    /// force-adds <c>-framework X</c> to the native link line for every module in the
    /// shared supplement assembly, regardless of what the consumer actually uses
    /// (BlastRadius FINDINGS #9). The system path is tried last so app-bundled and
    /// rpath-resident frameworks still win when both exist.
    /// </summary>
    internal static string[] GetSearchPaths(string libraryName) =>
    [
        $"@rpath/{libraryName}.framework/{libraryName}",
        $"@rpath/lib{libraryName}.dylib",
        $"@rpath/{libraryName}.dylib",
        $"@executable_path/lib{libraryName}.dylib",
        $"@executable_path/{libraryName}.dylib",
        // macOS .app bundles: Content items with CopyToOutputDirectory land in
        // Contents/Resources/, which is @executable_path/../Resources/.
        $"@executable_path/../Resources/lib{libraryName}.dylib",
        $"@executable_path/../Resources/{libraryName}.dylib",
        $"/System/Library/Frameworks/{libraryName}.framework/{libraryName}",
    ];

    internal static IntPtr ResolveSwiftFramework(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // If the caller already handed us a dyld-style path, try it verbatim and do
        // NOT fall through to the prefix-based search. Prefixing @rpath/... with
        // another @rpath/...framework/... would produce nonsense candidates, and the
        // .NET default resolver still runs when we return IntPtr.Zero so system
        // frameworks (already loaded as transitive dependencies of a wrapper dylib)
        // can still resolve normally.
        if (IsDyldStylePath(libraryName))
        {
            if (NativeLibrary.TryLoad(libraryName, out var directHandle))
            {
                Debug.WriteLine($"[SwiftFrameworkResolver] Loaded dyld-style '{libraryName}' directly");
                return directHandle;
            }

            Debug.WriteLine($"[SwiftFrameworkResolver] Direct load failed for dyld-style '{libraryName}'; deferring to default resolution");
            return IntPtr.Zero;
        }

        foreach (var path in GetSearchPaths(libraryName))
        {
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                Debug.WriteLine($"[SwiftFrameworkResolver] Loaded '{libraryName}' from: {path}");
                return handle;
            }
        }

        Debug.WriteLine($"[SwiftFrameworkResolver] FAILED to load '{libraryName}'. Tried:");
        foreach (var path in GetSearchPaths(libraryName))
            Debug.WriteLine($"[SwiftFrameworkResolver]   - {path}");

        return IntPtr.Zero;
    }

    /// <summary>
    /// Returns a diagnostic string showing which paths were tried for a library name.
    /// Call this when debugging DllNotFoundException to understand resolution failures.
    /// Visible with DOTNET_DebugWriteToStdErr=1 or in the IDE debugger Output window.
    /// </summary>
    public static string DiagnoseResolution(string libraryName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SwiftFrameworkResolver diagnosis for '{libraryName}':");

        if (IsDyldStylePath(libraryName))
        {
            var directLoaded = NativeLibrary.TryLoad(libraryName, out var directHandle);
            sb.AppendLine($"  {(directLoaded ? "OK" : "FAIL")}  {libraryName}  (dyld-style path, tried verbatim)");
            if (directLoaded)
                NativeLibrary.Free(directHandle);
            return sb.ToString().TrimEnd();
        }

        foreach (var path in GetSearchPaths(libraryName))
        {
            var loaded = NativeLibrary.TryLoad(path, out var handle);
            sb.AppendLine($"  {(loaded ? "OK" : "FAIL")}  {path}");
            if (loaded)
                NativeLibrary.Free(handle);
        }

        return sb.ToString().TrimEnd();
    }
}
