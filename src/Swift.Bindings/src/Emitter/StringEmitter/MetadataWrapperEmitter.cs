// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-type @_cdecl Swift wrappers that return type metadata as raw pointers,
/// eliminating CallConvSwift from metadata accessor P/Invokes.
/// </summary>
public static class MetadataWrapperEmitter
{
    /// <summary>
    /// Gets the @_cdecl symbol name for a metadata wrapper.
    /// Uses the module-qualified type name to avoid collisions for nested types.
    /// </summary>
    public static string GetMetadataSymbolName(string moduleName, string moduleQualifiedTypeName)
    {
        var safeTypeName = moduleQualifiedTypeName.Replace(".", "_");
        var hash = EmitterUtility.DeterministicHash8($"{moduleName}.{moduleQualifiedTypeName}");
        return $"SBW_GetMetadata_{moduleName}_{safeTypeName}_{hash}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper that returns type metadata as a raw pointer.
    /// Uses ModuleEmissionContext for dedup (each type emitted once).
    /// </summary>
    /// <param name="typeDecl">The type declaration whose metadata is being accessed.
    /// Used to compute the merged availability annotations from the type and its
    /// ancestors so the emitted Swift wrapper compiles when the type (or an enclosing
    /// type) is gated behind an OS version (e.g., iOS 16.4+).</param>
    public static void EmitIfNeeded(
        SwiftWriter swiftWriter, string moduleName,
        string moduleQualifiedSwiftName, string symbolName,
        ModuleEmissionContext ctx,
        BaseDecl? typeDecl = null)
    {
        if (!ctx.TryAddMetadataWrapperSymbol(symbolName))
            return;

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Metadata accessor @_cdecl wrapper for {moduleQualifiedSwiftName}.");
        var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(null, typeDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
        swiftWriter.WriteLine($"public func _sbw_getMetadata_{EmitterUtility.DeterministicHash8(symbolName)}() -> UnsafeMutableRawPointer {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"unsafeBitCast({moduleQualifiedSwiftName}.self as Any.Type, to: UnsafeMutableRawPointer.self)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }
}
