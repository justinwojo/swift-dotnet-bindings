// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift self-reconstruction patterns in @_cdecl wrappers.
/// Converts C# pointers back to Swift objects at the start of wrapper function bodies.
///
/// Three patterns:
/// - Class: <c>let obj = Unmanaged&lt;ClassName&gt;.fromOpaque(self_).takeUnretainedValue()</c>
/// - Struct (immutable): <c>let obj = self_.assumingMemoryBound(to: ClassName.self).pointee</c>
/// - Struct (mutating): no variable emitted — caller uses through-pointer access directly
/// - Protocol cast: <c>let/var obj = Unmanaged&lt;AnyObject&gt;.fromOpaque(self_).takeUnretainedValue() as! any {protocol}</c>
///
/// Used by PropertyWrapperEmitter, MethodWrapperEmitter, SubscriptWrapperEmitter,
/// and ConstructorWrapperEmitter.
/// </summary>
public static class SelfReconstructionEmitter
{
    /// <summary>
    /// Emits self reconstruction for instance methods and property getters.
    /// For classes: emits Unmanaged.fromOpaque().takeUnretainedValue().
    /// For structs (non-mutating): emits assumingMemoryBound().pointee with <c>let</c>.
    /// For structs (mutating): emits nothing — caller uses through-pointer access directly.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="isClass">Whether the parent type is a class.</param>
    /// <param name="isMutating">Whether the method is mutating (structs only).</param>
    /// <param name="moduleQualifiedName">The module-qualified Swift type name.</param>
    public static void Emit(SwiftWriter swiftWriter, bool isClass, bool isMutating, string moduleQualifiedName)
    {
        if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
        }
        else if (isMutating)
        {
            // Mutating method: use through-pointer access (self_.assumingMemoryBound(...).pointee)
            // so mutations write back. No separate obj variable needed — callExpr uses pointer directly.
        }
        else
        {
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee");
        }
    }

    /// <summary>
    /// Emits self reconstruction for generic parent class types using protocol-based type erasure.
    /// Uses AnyObject + protocol cast for runtime dispatch.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="protocolName">The protocol name to cast to.</param>
    /// <param name="isMutable">Whether the variable needs to be mutable (var vs let). Use true for setters.</param>
    public static void EmitProtocolCast(SwiftWriter swiftWriter, string protocolName, bool isMutable = false)
    {
        var binding = isMutable ? "var" : "let";
        swiftWriter.WriteLine($"{binding} obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
    }
}
