// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        RegisterForAssembly(typeof(SwiftFrameworkResolver).Assembly);

        // Pre-register NewFromPayload factories for Swift.Runtime types that can't have
        // preserve="methods" in the ILLink descriptor (CallConvSwift P/Invoke members crash ILC).
        // The static virtual dispatch here ensures ILC preserves the NewFromPayload method
        // on these types without needing trimmer annotations.
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.DispatchQueue>();
    }

    /// <summary>
    /// Registers the standard Swift framework resolver for an assembly.
    /// Safe to call multiple times — subsequent calls are silently ignored.
    /// </summary>
    public static void RegisterForAssembly(Assembly assembly)
    {
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

    private static IntPtr ResolveSwiftFramework(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Try framework wrapper path first (standard for xcframeworks on device)
        var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
        if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            return handle;

        // Try bare dylib at @rpath (e.g., libSwiftBindingsRuntime.dylib in Frameworks/)
        var bareDylibPath = $"@rpath/lib{libraryName}.dylib";
        if (NativeLibrary.TryLoad(bareDylibPath, out handle))
            return handle;

        // Try bare dylib without lib prefix (matches DllImport name exactly)
        var bareDylibNoPrefix = $"@rpath/{libraryName}.dylib";
        if (NativeLibrary.TryLoad(bareDylibNoPrefix, out handle))
            return handle;

        // Fallback: try @executable_path (resolves to .app root on iOS).
        // NuGet Content items with <Link>Frameworks/...</Link> may land at the
        // .app root instead of .app/Frameworks/ depending on the .NET iOS SDK version.
        // @executable_path catches dylibs placed at the app bundle root.
        var execDylibPath = $"@executable_path/lib{libraryName}.dylib";
        if (NativeLibrary.TryLoad(execDylibPath, out handle))
            return handle;

        var execDylibNoPrefix = $"@executable_path/{libraryName}.dylib";
        if (NativeLibrary.TryLoad(execDylibNoPrefix, out handle))
            return handle;

        return IntPtr.Zero;
    }
}
