// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting metadata accessor helper functions in Swift @_cdecl wrappers.
/// These helpers call the generic parent type's metadata accessor via dlsym to convert
/// T.self metadata into GenericType&lt;T&gt;.self metadata for protocol metatype dispatch.
///
/// Previously lived in ConstructorWrapperEmitter but was called from Method, Property,
/// and Constructor emitters. Extracted here to remove the implicit dependency.
///
/// Deduplication is tracked by <see cref="ModuleEmissionContext.TryAddMetadataAccessorHelper"/>.
/// </summary>
public static class MetatypeHelperEmitter
{
    /// <summary>
    /// Emits a private Swift helper function that calls the metadata accessor for a generic parent
    /// type via dlsym. This converts T.self metadata into GenericType&lt;T&gt;.self metadata, which is
    /// needed for protocol metatype dispatch. Deduplicates by type mangled name.
    /// Returns the helper function name (e.g., "_sbw_meta_ABCD1234").
    ///
    /// For constrained generic types (e.g., ConstrainedBox&lt;T: Describable&gt;), the metadata accessor
    /// also requires protocol witness table (PWT) pointers for each conformance. The helper accepts
    /// these as additional UnsafeRawPointer parameters after the type metadata parameters.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="parentTypeDecl">The generic parent type declaration.</param>
    /// <param name="ctx">The per-module emission context for deduplication.</param>
    /// <returns>The helper function name to use in subsequent call sites.</returns>
    public static string EmitMetadataAccessorHelperIfNeeded(
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ModuleEmissionContext ctx)
    {
        var mangledName = parentTypeDecl.MangledName;
        // Use mangled name hash for uniqueness — two types with the same short name
        // (e.g., DiskStorage.Backend<T> and MemoryStorage.Backend<T>) need distinct helpers.
        var helperName = $"_sbw_meta_{EmitterUtility.DeterministicHash8(mangledName)}";

        if (!ctx.TryAddMetadataAccessorHelper(mangledName))
            return helperName; // Already emitted, just return the name

        var metaSymbol = $"{mangledName}Ma";
        var genericCount = parentTypeDecl.GenericParameters.Count;

        // Count PWT parameters: one per protocol conformance per generic parameter.
        // Swift metadata accessors for constrained generic types require these after
        // the type metadata parameters.
        var pwtParams = new List<string>();
        var pwtFnTypes = new List<string>();
        var pwtCallArgs = new List<string>();
        int pwtIndex = 0;
        foreach (var genericParam in parentTypeDecl.GenericParameters)
        {
            foreach (var conformance in genericParam.GenericConformances)
            {
                pwtParams.Add($"_ pwt{pwtIndex}: UnsafeRawPointer");
                pwtFnTypes.Add("UnsafeRawPointer");
                pwtCallArgs.Add($"pwt{pwtIndex}");
                pwtIndex++;
            }
        }

        // Build parameter list: type metadata + PWT
        var allParams = Enumerable.Range(0, genericCount).Select(i => $"_ t{i}: UnsafeRawPointer")
            .Concat(pwtParams);
        var paramList = string.Join(", ", allParams);

        // Build function type: (Int, T_metadata..., PWT...) -> (UnsafeRawPointer, Int)
        var allFnTypes = Enumerable.Range(0, genericCount).Select(_ => "UnsafeRawPointer")
            .Concat(pwtFnTypes);
        var fnParamTypes = string.Join(", ",
            new[] { "Int" }.Concat(allFnTypes));

        // Build call arguments: (0, t0, t1, ..., pwt0, pwt1, ...)
        var allCallArgs = Enumerable.Range(0, genericCount).Select(i => $"t{i}")
            .Concat(pwtCallArgs);
        var callArgs = string.Join(", ",
            new[] { "0" }.Concat(allCallArgs));

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private func {{helperName}}({{paramList}}) -> UnsafeRawPointer {
                typealias _Fn = @convention(thin) ({{fnParamTypes}}) -> (UnsafeRawPointer, Int)
                let fn = unsafeBitCast(dlsym(dlopen(nil, RTLD_LAZY), "{{metaSymbol}}")!, to: _Fn.self)
                return fn({{callArgs}}).0
            }
            """);

        return helperName;
    }

    /// <summary>
    /// Returns the total number of PWT parameters for a generic type's metadata accessor.
    /// This is the sum of all protocol conformances across all generic parameters.
    /// </summary>
    public static int GetPwtParameterCount(TypeDecl parentTypeDecl)
    {
        return parentTypeDecl.GenericParameters
            .Sum(gp => gp.GenericConformances.Count);
    }
}
