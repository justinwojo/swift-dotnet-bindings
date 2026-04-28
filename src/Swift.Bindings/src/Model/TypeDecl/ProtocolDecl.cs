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

    /// <summary>
    /// Indicates whether the protocol has requirements that failed ABI parsing.
    /// When true, EveryProtocol conformance should be skipped because the emitter
    /// cannot generate stubs for requirements it doesn't know about.
    /// This is detected by comparing ABI JSON children with protocolReq=true
    /// against successfully parsed methods.
    /// </summary>
    public bool HasMissingRequirements { get; set; }

    /// <summary>
    /// Indicates whether the protocol has methods with @convention(c) or @convention(block)
    /// closure parameters. These conventions are not encoded in ABI JSON, so the EveryProtocol
    /// closure stub would emit @escaping instead, causing a type mismatch.
    /// Detected via swiftinterface cross-reference.
    /// </summary>
    public bool HasConventionCClosureParameters { get; set; }

    /// <summary>
    /// Indicates whether the protocol declares an underscore-prefixed (e.g., <c>__linkSPI</c>)
    /// requirement that swift-api-digester strips from the ABI JSON yet the Swift compiler
    /// still enforces at conformance type-check time. The parser only sees what the ABI JSON
    /// exposes, so EveryProtocol's extension cannot emit a witness for the hidden member;
    /// Swift then rejects the conformance with "protocol requires property '__X'". Detected
    /// via swiftinterface cross-reference and only set when no same-protocol extension supplies
    /// a default implementation for the hidden member.
    /// </summary>
    public bool HasUnsatisfiedHiddenRequirements { get; set; }

    /// <summary>
    /// Indicates whether at least one protocol-requirement method's method-descriptor symbol
    /// (mangled name + <c>Tq</c>) is missing from the framework's TBD on this slice. Apple
    /// occasionally ships a swiftinterface that declares a protocol requirement which the
    /// binary's TBD does not export — most often on Mac Catalyst, where the macOS dylib backs
    /// a macCatalyst-only swiftinterface (e.g. <c>LiveCommunicationKit.ConversationManagerDelegate.didActivate</c>
    /// is declared in the macabi swiftinterface but its <c>Tq</c> descriptor isn't in
    /// <c>LiveCommunicationKit.tbd</c> under MacOSX.sdk). The synthesized
    /// <c>extension EveryProtocol: P</c> would emit a witness table referencing the missing
    /// descriptor, producing an undefined-symbol link error in the wrapper. Skipping the
    /// conformance leaves the protocol callable through its existing existential surface
    /// while letting the wrapper link.
    /// </summary>
    public bool HasMissingTbdMethodDescriptors { get; set; }
}
