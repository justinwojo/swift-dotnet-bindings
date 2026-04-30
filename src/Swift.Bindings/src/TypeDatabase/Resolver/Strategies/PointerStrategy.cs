// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Swift pointer identities — <c>OpaquePointer</c>,
/// <c>UnsafePointer</c>, <c>UnsafeMutablePointer</c>, <c>UnsafeRawPointer</c>,
/// <c>UnsafeMutableRawPointer</c>, and <c>Builtin.RawPointer</c> — to
/// <see cref="TypeDatabaseExtensions.IntPtrType"/>. This is an intentional
/// projection (not a fallback): all Swift pointer shapes share <c>IntPtr</c> on
/// the C# side.
/// </summary>
internal sealed class PointerStrategy : IResolutionStrategy
{
    public string Name => "Pointer";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsPointerType(named))
        {
            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.IntPtrType,
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
