// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Resolves the callable Swift symbol for a method declaration.
/// Shared between PInvokeEmitter (for P/Invoke entry points) and ThunkAssemblyEmitter
/// (for ARM64 thunk branch targets). Centralizes the Tj dispatch thunk suffix logic
/// to avoid symbol mismatches between what P/Invoke expects and what the thunk calls.
/// </summary>
public static class SwiftCallTargetResolver
{
    /// <summary>
    /// Resolves the full callable Swift symbol for a method, including the Tj suffix
    /// for non-final class instance methods that use vtable dispatch.
    /// </summary>
    /// <param name="methodDecl">The method declaration containing the mangled name.</param>
    /// <param name="parentDecl">The parent type declaration (ClassDecl, StructDecl, etc.), or null for free functions.</param>
    /// <returns>The fully resolved symbol name (without leading underscore — caller adds platform prefix).</returns>
    public static string Resolve(MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return Resolve(methodDecl.MangledName, methodDecl, parentDecl);
    }

    /// <summary>
    /// Resolves the full callable Swift symbol using an explicit mangled name, including
    /// the Tj suffix for non-final class instance methods that use vtable dispatch.
    /// Use this overload when methodDecl.MangledName has been overwritten (e.g., with a thunk symbol).
    /// </summary>
    /// <param name="mangledName">The original Swift mangled name to resolve.</param>
    /// <param name="methodDecl">The method declaration (used for dispatch classification, not for MangledName).</param>
    /// <param name="parentDecl">The parent type declaration (ClassDecl, StructDecl, etc.), or null for free functions.</param>
    /// <returns>The fully resolved symbol name (without leading underscore — caller adds platform prefix).</returns>
    public static string Resolve(string mangledName, MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        var symbol = mangledName;

        // With library evolution, non-final class instance methods are dispatched through
        // vtable thunks. The bare method symbol is a local (non-exported) symbol in the
        // dylib; only the dispatch thunk (Tj suffix) is globally exported.
        //
        // Gates (all must be true for Tj):
        // - Parent is a non-final class
        // - Method itself is not final
        // - Instance method (not static, not constructor)
        // - Not an extension method (static dispatch, no vtable entry)
        if (parentDecl is ClassDecl classParent
            && !classParent.IsFinal
            && !methodDecl.IsFinal
            && methodDecl.MethodType == MethodType.Instance
            && !methodDecl.IsConstructor
            && !methodDecl.IsExtensionMethod)
        {
            symbol += ManglingProbes.DispatchThunkSuffix;
        }

        return symbol;
    }

    /// <summary>
    /// Returns the symbol with the Apple linker underscore prefix for use in assembly code.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <param name="parentDecl">The parent type declaration, or null for free functions.</param>
    /// <returns>The symbol prefixed with underscore (e.g., "_$s6Module6methodyyF").</returns>
    public static string ResolveWithPrefix(MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return "_" + Resolve(methodDecl, parentDecl);
    }

    /// <summary>
    /// Returns the symbol with the Apple linker underscore prefix, using an explicit mangled name.
    /// Use this overload when methodDecl.MangledName has been overwritten (e.g., with a thunk symbol).
    /// </summary>
    /// <param name="mangledName">The original Swift mangled name.</param>
    /// <param name="methodDecl">The method declaration (used for dispatch classification).</param>
    /// <param name="parentDecl">The parent type declaration, or null for free functions.</param>
    /// <returns>The symbol prefixed with underscore.</returns>
    public static string ResolveWithPrefix(string mangledName, MethodDecl methodDecl, BaseDecl? parentDecl)
    {
        return "_" + Resolve(mangledName, methodDecl, parentDecl);
    }
}
