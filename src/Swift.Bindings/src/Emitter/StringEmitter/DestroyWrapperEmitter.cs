// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-type @_cdecl destroy wrappers in the Swift wrapper framework and
/// corresponding C# registration code.
///
/// On NativeAOT (device builds), <c>SwiftSafeHandle&lt;T&gt;.ReleaseHandle()</c> crashes when
/// calling <c>ValueWitnessTable-&gt;Destroy()</c> via indirect CallConvSwift function pointer.
/// The @_cdecl wrapper routes the destroy through C calling convention, avoiding the crash.
///
/// Swift side: Emits <c>SBW_Destroy_{Module}_{Type}</c> per type using <c>deinitialize(count: 1)</c>.
/// C# side: Emits a P/Invoke declaration and static constructor that registers the destroy
/// action with <c>SwiftSafeHandle&lt;T&gt;.RegisterDestroyAction()</c>.
///
/// State is tracked on <see cref="ModuleEmissionContext"/> to prevent duplicate emission.
/// </summary>
public static class DestroyWrapperEmitter
{
    /// <summary>
    /// Emits a Swift @_cdecl destroy wrapper for a specific type.
    /// Called once per type that uses SwiftSafeHandle.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="moduleName">The Swift module name (e.g., "Nuke").</param>
    /// <param name="moduleQualifiedSwiftName">The module-qualified Swift type name (e.g., "Nuke.ImageRequest").</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <returns>True if emitted, false if already emitted for this type.</returns>
    public static bool EmitSwiftDestroyWrapper(
        SwiftWriter swiftWriter,
        string moduleName,
        string moduleQualifiedSwiftName,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        var symbolName = GetDestroySymbolName(moduleName, moduleQualifiedSwiftName);
        if (!ctx.TryAddDestroyWrapperSymbol(symbolName))
            return false;

        swiftWriter.WriteLines($$"""
            // Per-type destroy wrapper for {{moduleQualifiedSwiftName}}.
            // Routes Dispose() through @_cdecl to avoid CallConvSwift crash on NativeAOT.
            @_cdecl("{{symbolName}}")
            public func {{symbolName}}(_ bufferPtr: UnsafeMutableRawPointer) {
                bufferPtr.assumingMemoryBound(to: {{moduleQualifiedSwiftName}}.self).deinitialize(count: 1)
            }

            """);

        return true;
    }

    /// <summary>
    /// Emits C# P/Invoke declaration and static field initializer that registers the destroy wrapper.
    /// Uses a static field initializer (not a static constructor) to avoid CS0111 conflicts
    /// with existing static constructors (e.g., protocol conformance descriptor initialization).
    /// Called from type handlers (ClassHandler, NonFrozenStructHandler, FrozenStructHandler, EnumHandler).
    /// </summary>
    /// <param name="csWriter">The C# writer positioned inside the type body.</param>
    /// <param name="csharpTypeName">The C# type name (e.g., "ImageRequest"), without generic parameters.</param>
    /// <param name="safeHandleTypeName">The type parameter for SwiftSafeHandle (root base for classes).</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="moduleQualifiedSwiftName">The module-qualified Swift type name (e.g., "Nuke.ImageRequest").</param>
    /// <param name="wrapperLibraryName">The wrapper library name (e.g., "NukeSwiftBindings").</param>
    public static void EmitCSharpDestroyRegistration(
        CSharpWriter csWriter,
        string csharpTypeName,
        string safeHandleTypeName,
        string moduleName,
        string moduleQualifiedSwiftName,
        string wrapperLibraryName)
    {
        var symbolName = GetDestroySymbolName(moduleName, moduleQualifiedSwiftName);

        // Use a static field initializer to register the destroy action.
        // This avoids conflicting with existing static constructors (e.g., protocol conformance).
        // Static field initializers are merged into the type's cctor alongside any explicit
        // static constructor, running before the explicit static constructor body.
        csWriter.WriteLines($$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            private static readonly bool _sbw_destroyRegistered = _SBW_RegisterDestroy();
            private static bool _SBW_RegisterDestroy()
            {
                SwiftSafeHandle<{{safeHandleTypeName}}>.RegisterDestroyAction(_SBW_Destroy);
                return true;
            }

            [System.Runtime.InteropServices.DllImport("{{wrapperLibraryName}}", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "{{symbolName}}")]
            private static extern void _SBW_Destroy(IntPtr handle);
            """);
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits both Swift and C# sides of the destroy wrapper for a type.
    /// Convenience method that combines <see cref="EmitSwiftDestroyWrapper"/> and
    /// <see cref="EmitCSharpDestroyRegistration"/>.
    /// </summary>
    /// <param name="csWriter">The C# writer positioned inside the type body.</param>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="csharpTypeName">The C# type name without generic parameters.</param>
    /// <param name="safeHandleTypeName">The type parameter for SwiftSafeHandle (root base for classes).</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="moduleQualifiedSwiftName">The module-qualified Swift type name (e.g., "Nuke.ImageRequest").</param>
    /// <param name="wrapperLibraryName">The wrapper library name (e.g., "NukeSwiftBindings"), or null if unavailable.</param>
    /// <param name="ctx">The per-module emission context.</param>
    public static void EmitIfNeeded(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        string csharpTypeName,
        string safeHandleTypeName,
        string moduleName,
        string moduleQualifiedSwiftName,
        string? wrapperLibraryName,
        ModuleEmissionContext? ctx = null)
    {
        // Only emit when a wrapper library is available (xcframework mode).
        // Without a wrapper library, the @_cdecl function doesn't exist and
        // SwiftSafeHandle falls back to VWT->Destroy.
        if (string.IsNullOrEmpty(wrapperLibraryName))
            return;

        // Skip when the CONTAINING type is generic — DllImport cannot be applied inside
        // generic type definitions (CS7042). Check that the type itself has generic parameters
        // (safeHandleTypeName starts with csharpTypeName followed by '<'), not merely that
        // the safe handle type parameter is a closed generic from a base class.
        // A non-generic derived type like IntContainer with SwiftSafeHandle<Container<int>>
        // CAN legally host the DllImport and should not be skipped.
        bool isContainingTypeGeneric = safeHandleTypeName.Length > csharpTypeName.Length
            && safeHandleTypeName.StartsWith(csharpTypeName, StringComparison.Ordinal)
            && safeHandleTypeName[csharpTypeName.Length] == '<';
        if (isContainingTypeGeneric)
            return;

        EmitSwiftDestroyWrapper(swiftWriter, moduleName, moduleQualifiedSwiftName, ctx);
        EmitCSharpDestroyRegistration(csWriter, csharpTypeName, safeHandleTypeName, moduleName, moduleQualifiedSwiftName, wrapperLibraryName);
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a type's destroy wrapper.
    /// Strips the module prefix from the qualified name to avoid duplication
    /// (e.g., "Nuke.ImageRequest" → "SBW_Destroy_Nuke_ImageRequest", not "SBW_Destroy_Nuke_Nuke_ImageRequest").
    /// </summary>
    public static string GetDestroySymbolName(string moduleName, string moduleQualifiedSwiftName)
    {
        // Strip module prefix if present (SwiftTypeName.ToString() includes it)
        var typePart = moduleQualifiedSwiftName.StartsWith(moduleName + ".")
            ? moduleQualifiedSwiftName.Substring(moduleName.Length + 1)
            : moduleQualifiedSwiftName;
        var safeSuffix = typePart.Replace(".", "_");
        return $"SBW_Destroy_{moduleName}_{safeSuffix}";
    }
}
