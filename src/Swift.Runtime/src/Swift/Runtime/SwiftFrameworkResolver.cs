// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

        // Wire the C# free trampoline that fires from Swift's _SBClosureCtx deinit
        // (defined in libSwiftBindingsRuntime.dylib). Must run before any wrapper
        // emits a closure-context box; the wrapper's first allocation can happen
        // on the very first P/Invoke from a consumer assembly.
        SwiftClosureContext.EnsureRegistered();

        // Pre-register NewFromPayload factories for all non-generic Swift.Runtime ISwiftObject types.
        // On NativeAOT with NuGet packages (not project references), the trimmer may strip
        // explicit interface implementations (ISwiftObject.NewFromPayload), causing
        // MarshalFromSwift<T> reflection fallback to fail with "Failed to find NewFromPayload".
        // Static virtual dispatch here ensures ILC preserves the method and populates the
        // factory cache before any marshalling call.
        //
        // Foundation canonicals (Data, URL, URLRequest, AnyError) and supplement stubs moved
        // to SwiftBindings.Apple register themselves from that package's ModuleInitializer —
        // Runtime cannot reference them (would be a circular package dep).
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.SwiftString>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyHashable>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyType>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.Hasher>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.DispatchQueue>();
        // Removed Swift.* hand-rolled wrappers for ObjC-imported classes (URLResponse,
        // UIImage, NSImage, NSColor, OperationQueue, CIContext): they imported
        // `$sSo<ObjCClassName>...` mangled symbols from Swift overlay libraries, but
        // Swift never emits dispatch thunks for ObjC-imported class members — those
        // properties dispatch via objc_msgSend at the call site, not via a Swift
        // function. The helper classes threw `EntryPointNotFoundException` on every
        // method call. The TypeDB now maps these types directly to the .NET iOS /
        // Xamarin ObjC bindings (Foundation.NSUrlResponse, UIKit.UIImage,
        // AppKit.NSImage, AppKit.NSColor, Foundation.NSOperationQueue,
        // CoreImage.CIContext), which dispatch correctly through the ObjC runtime.
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
    // Trim-safety: keep the Mono <see cref="AssemblyLoadContext"/>
    // `ResolvingUnmanagedDll` event accessor rooted under ILC so `+=` here binds to a
    // live add_Accessor rather than being trimmed into a no-op. Documented as a
    // conceptual dependency in the XML above; the attribute is the enforceable form.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AssemblyLoadContext))]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "RegisterAlcFallback only subscribes to AssemblyLoadContext.Default.ResolvingUnmanagedDll; " +
            "the IL3050 fires because the DynamicDependency keeps every AssemblyLoadContext member rooted, and ilc " +
            "transitively reaches Enum.GetValues<TEnum>() via members we never invoke at runtime.")]
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

    /// <summary>
    /// Loads the native library that exports a Swift metadata/descriptor symbol, applying
    /// the same Apple-framework fallback used by both <see cref="ProtocolDescriptor"/> and
    /// <see cref="ProtocolConformanceDescriptor"/>. This is the single source of truth for
    /// that resolution so the two descriptor loaders cannot drift apart again.
    ///
    /// First attempt: assembly-context probing (resolves on the simulator and for
    /// app-bundled frameworks via <c>@rpath</c>). On a physical device the per-binding
    /// <see cref="NativeLibrary.SetDllImportResolver(Assembly, DllImportResolver)"/> hook is
    /// registered on the binding assembly, not Swift.Runtime, and explicit
    /// <see cref="NativeLibrary.TryLoad(string, Assembly, DllImportSearchPath?, out IntPtr)"/>
    /// does not invoke it — so the bare name can miss. The fallback reduces
    /// <paramref name="libraryName"/> to its bare framework name and walks the ordered
    /// <see cref="GetSearchPaths"/> list, whose last entry is
    /// <c>/System/Library/Frameworks/{name}.framework/{name}</c>, so an Apple *system*
    /// framework (CryptoKit, RealityKit, …) resolves on device.
    ///
    /// Extracting the bare name first is essential: <see cref="GetSearchPaths"/> applied to
    /// an already-dyld-style <c>"@rpath/Foo.framework/Foo"</c> would produce double-<c>@rpath</c>
    /// nonsense (<c>@rpath/@rpath/Foo.framework/Foo.framework/...</c>) and never reach the
    /// system path — the gap that previously made class-bound existential element metadata
    /// silently fail to register for Apple-framework bindings on device.
    /// </summary>
    /// <param name="libraryName">The embedded library name or dyld-style path.</param>
    /// <param name="handle">The loaded library handle on success; <see cref="IntPtr.Zero"/> otherwise.</param>
    /// <returns><c>true</c> if the library was loaded.</returns>
    internal static bool TryLoadWithFrameworkFallback(string libraryName, out IntPtr handle)
    {
        if (NativeLibrary.TryLoad(libraryName, typeof(SwiftFrameworkResolver).Assembly, null, out handle))
            return true;

        var frameworkName = ExtractFrameworkName(libraryName);
        foreach (var candidate in GetSearchPaths(frameworkName))
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Reduces an embedded library path to the bare framework/module name expected by
    /// <see cref="GetSearchPaths"/>. Handles the three shapes the generator can embed: a
    /// bare name (<c>"CryptoKit"</c>), a dyld-style framework path
    /// (<c>"@rpath/CryptoKit.framework/CryptoKit"</c> or
    /// <c>"/System/Library/Frameworks/CryptoKit.framework/CryptoKit"</c>), and a plain
    /// dylib path (last path segment, minus a <c>lib</c> prefix / <c>.dylib</c> suffix).
    /// </summary>
    internal static string ExtractFrameworkName(string libraryName)
    {
        // ".framework/" wins: the framework name is the path segment immediately before it,
        // independent of any @rpath / @executable_path / absolute-system prefix.
        var fwIdx = libraryName.IndexOf(".framework/", StringComparison.Ordinal);
        if (fwIdx > 0)
        {
            var start = libraryName.LastIndexOf('/', fwIdx);
            return libraryName.Substring(start + 1, fwIdx - start - 1);
        }

        var name = libraryName;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }

        if (name.EndsWith(".dylib", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - ".dylib".Length);
            if (name.StartsWith("lib", StringComparison.Ordinal))
            {
                name = name.Substring("lib".Length);
            }
        }

        return name;
    }

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
