// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents an associated type declaration within a protocol.
/// </summary>
public sealed record AssociatedTypeDecl
{
    /// <summary>
    /// The name of the associated type.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The default type if specified, otherwise null.
    /// </summary>
    public TypeSpec? DefaultType { get; set; }

    /// <summary>
    /// Constraints on the associated type (e.g., "where Element: Equatable").
    /// </summary>
    public List<string> Constraints { get; set; } = new();
}

/// <summary>
/// Represents a protocol declaration.
/// </summary>
public sealed record ProtocolDecl : TypeDecl
{
    /// <summary>
    /// Associated types declared by this protocol.
    /// Protocols with associated types are known as PATs (Protocols with Associated Types)
    /// and require special handling in C# (typically generic interfaces).
    /// </summary>
    public List<AssociatedTypeDecl> AssociatedTypes { get; set; } = new();

    /// <summary>
    /// Indicates whether the protocol has a Self requirement.
    /// A Self requirement means the protocol references 'Self' in a way that
    /// requires the conforming type to be known at compile time.
    /// This affects how the protocol can be used as an existential type.
    /// </summary>
    public bool HasSelfRequirement { get; set; }

    /// <summary>
    /// Protocols that this protocol inherits from.
    /// In Swift, protocols can inherit from other protocols, meaning conforming
    /// types must also conform to all inherited protocols.
    /// </summary>
    public List<NamedTypeSpec> InheritedProtocols { get; set; } = new();

    /// <summary>
    /// The generic signature of the protocol, if it has generic requirements.
    /// This includes where clauses like "where T: Equatable".
    /// </summary>
    public string? GenericSignature { get; set; }

    /// <summary>
    /// Indicates whether this is a class-bound protocol (can only be adopted by classes).
    /// In Swift, this is specified with ": AnyObject" or ": class".
    /// </summary>
    public bool IsClassBound { get; set; }

    /// <summary>
    /// Indicates whether the protocol can be used as an existential type.
    /// Protocols with Self requirements or associated types cannot be used as existentials
    /// without explicit "any" syntax in Swift 5.6+.
    /// </summary>
    public bool CanBeExistential => !HasSelfRequirement && AssociatedTypes.Count == 0;
}
