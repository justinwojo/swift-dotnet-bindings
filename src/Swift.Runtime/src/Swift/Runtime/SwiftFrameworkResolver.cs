// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Centralized DllImport resolver for Swift framework loading on Apple platforms.
/// Maps DllImport library names to @rpath/{name}.framework/{name} paths.
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
        var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
        if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            return handle;
        return IntPtr.Zero;
    }
}
