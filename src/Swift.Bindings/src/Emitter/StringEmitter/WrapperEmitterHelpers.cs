// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared helpers for the wrapper emitters (Method, Constructor, Property, Subscript).
/// Contains code-emission utilities that are identical across all wrapper types.
/// </summary>
public static class WrapperEmitterHelpers
{
    /// <summary>
    /// Emits the @MainActor (if needed) and @_cdecl annotations for a Swift wrapper function.
    /// Consolidates the identical annotation pattern used by MethodWrapperEmitter,
    /// PropertyWrapperEmitter, and ConstructorWrapperEmitter.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="symbolName">The @_cdecl symbol name string.</param>
    /// <param name="needsMainActor">Whether to prepend @MainActor before @_cdecl.</param>
    public static void EmitCdeclAnnotation(SwiftWriter swiftWriter, string symbolName, bool needsMainActor)
    {
        if (needsMainActor)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
    }
}
