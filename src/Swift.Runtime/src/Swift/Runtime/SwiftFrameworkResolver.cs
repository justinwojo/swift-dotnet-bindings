// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.URLResponse>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyHashable>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.AnyType>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.Hasher>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.DispatchQueue>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.OperationQueue>();
        InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<Swift.CIContext>();
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

    /// <summary>
    /// The search paths tried for each library name, in order.
    /// </summary>
    private static string[] GetSearchPaths(string libraryName) =>
    [
        $"@rpath/{libraryName}.framework/{libraryName}",
        $"@rpath/lib{libraryName}.dylib",
        $"@rpath/{libraryName}.dylib",
        $"@executable_path/lib{libraryName}.dylib",
        $"@executable_path/{libraryName}.dylib",
    ];

    private static IntPtr ResolveSwiftFramework(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
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
