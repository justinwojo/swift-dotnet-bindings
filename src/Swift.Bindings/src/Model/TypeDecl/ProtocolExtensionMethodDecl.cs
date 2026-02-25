// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents a protocol extension method parsed from a .swiftinterface file.
/// Protocol extension methods use static dispatch (not witness tables) and
/// do NOT appear in ABI JSON — they only exist in swiftinterface files.
/// </summary>
public class ProtocolExtensionMethodDecl
{
    /// <summary>
    /// The fully-qualified protocol name (e.g., "Kingfisher.KFOptionSetter").
    /// </summary>
    public required string ProtocolQualifiedName { get; set; }

    /// <summary>
    /// The method name (e.g., "targetCache").
    /// </summary>
    public required string MethodName { get; set; }

    /// <summary>
    /// The full raw signature line from the swiftinterface (for type parsing).
    /// May be assembled from multi-line continuation.
    /// </summary>
    public required string RawSignature { get; set; }

    /// <summary>
    /// Whether the method returns Self (the conforming type).
    /// </summary>
    public bool ReturnsSelf { get; set; }

    /// <summary>
    /// Whether the method is annotated with @MainActor / @_Concurrency.MainActor.
    /// </summary>
    public bool IsMainActorIsolated { get; set; }

    /// <summary>
    /// Whether the method is static.
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Whether this is a property (var) extension rather than a method (func).
    /// </summary>
    public bool IsProperty { get; set; }

    /// <summary>
    /// The printed name in ABI format (e.g., "targetCache(_:)") for dedup.
    /// </summary>
    public required string PrintedName { get; set; }

    /// <summary>
    /// Whether this property has a setter (var x: T { get set }).
    /// Only meaningful when IsProperty is true.
    /// </summary>
    public bool HasSetter { get; set; }

    /// <summary>
    /// Whether the member is annotated with @available(*, deprecated, ...).
    /// Used by ForeignTypeExtensionEmitter to skip deprecated members.
    /// </summary>
    public bool IsDeprecated { get; set; }

    /// <summary>
    /// Where constraints from the extension header (e.g., "Self : SomeClass").
    /// Empty for unconstrained extensions. Used to filter out methods that
    /// don't apply to a given conforming type.
    /// </summary>
    public List<string> WhereConstraints { get; set; } = new();
}
