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

        // Build parameter list: one UnsafeRawPointer per generic parameter
        var paramList = string.Join(", ",
            Enumerable.Range(0, genericCount).Select(i => $"_ t{i}: UnsafeRawPointer"));

        // Build function type: (Int, UnsafeRawPointer, ...) -> (UnsafeRawPointer, Int)
        var fnParamTypes = string.Join(", ",
            new[] { "Int" }.Concat(
                Enumerable.Range(0, genericCount).Select(_ => "UnsafeRawPointer")));

        // Build call arguments: (0, t0, t1, ...)
        var callArgs = string.Join(", ",
            new[] { "0" }.Concat(
                Enumerable.Range(0, genericCount).Select(i => $"t{i}")));

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
}
