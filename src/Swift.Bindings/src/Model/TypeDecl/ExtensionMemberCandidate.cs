// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A single direct-member declaration from an `extension X { ... }` block, captured
/// without knowing whether <c>X</c> is one of this module's protocols, one of this
/// module's owned types, or a type from another module. The producer (regex or
/// SwiftSyntax) emits the flat, module-context-free list; the .NET-side facts helper
/// <see cref="SwiftInterfaceFacts.ResolveForeignExtensions"/> partitions it into
/// foreign-type extensions using <c>moduleName</c> and <c>moduleTypeNames</c> from the
/// ABI parse, and protocol extensions are surfaced via
/// <see cref="SwiftInterfaceFacts.ProtocolExtensionMethods"/>.
/// <para/>
/// Field shape mirrors <see cref="ProtocolExtensionMethodDecl"/> exactly EXCEPT that
/// the type-side key is <see cref="ExtendedTypeName"/> (the verbatim extension target,
/// e.g. "UIKit.UIView", "Mod.MyProto", or "MyOwnedType") instead of
/// <c>ProtocolQualifiedName</c>. Conversions are 1:1 in
/// <see cref="SwiftInterfaceFacts.ResolveForeignExtensions"/>.
/// </summary>
public sealed class ExtensionMemberCandidate
{
    /// <summary>The full extension target name as written in the swiftinterface,
    /// including any module qualifier (e.g., "UIKit.UIView", "Mod.MyProto", "MyType").
    /// Partitioning by module is done in <see cref="SwiftInterfaceFacts.ResolveForeignExtensions"/>
    /// using the first-dot rule.</summary>
    public required string ExtendedTypeName { get; init; }

    public required string MethodName { get; init; }
    public required string RawSignature { get; init; }
    public required string PrintedName { get; init; }
    public bool ReturnsSelf { get; init; }
    public bool IsMainActorIsolated { get; init; }
    public bool IsStatic { get; init; }
    public bool IsProperty { get; init; }
    public bool HasSetter { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsMutating { get; init; }
    public List<string> WhereConstraints { get; init; } = new();
}
