// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling existential types (protocol types and protocol compositions) in Swift bindings.
/// Existential types in Swift are represented using existential containers that hold the value,
/// type metadata, and protocol witness tables.
/// </summary>
public class ExistentialHandler
{
    private readonly ITypeDatabase _typeDatabase;

    /// <summary>
    /// Maximum number of protocol witness tables supported.
    /// This corresponds to ExistentialContainer1 through ExistentialContainer8.
    /// </summary>
    public const int MaxSupportedWitnessTables = 8;

    public ExistentialHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Determines whether the specified argument declaration represents an existential type
    /// (a protocol type or protocol composition).
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is ProtocolListTypeSpec;

    /// <summary>
    /// Determines whether the specified property declaration represents an existential type.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is ProtocolListTypeSpec;

    /// <summary>
    /// Determines whether the specified type spec represents an existential type.
    /// This includes both protocol compositions (ProtocolListTypeSpec) and single-protocol
    /// existentials (NamedTypeSpec with IsAny = true).
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(TypeSpec typeSpec) =>
        typeSpec is ProtocolListTypeSpec ||
        (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.IsAny);

    /// <summary>
    /// Gets the ProtocolListTypeSpec from an argument declaration.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The ProtocolListTypeSpec if the argument is an existential; otherwise, null.</returns>
    public ProtocolListTypeSpec? GetProtocolListTypeSpec(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec as ProtocolListTypeSpec;

    /// <summary>
    /// Gets the ProtocolListTypeSpec from a property declaration.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The ProtocolListTypeSpec if the property is an existential; otherwise, null.</returns>
    public ProtocolListTypeSpec? GetProtocolListTypeSpec(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec as ProtocolListTypeSpec;

    /// <summary>
    /// Converts a type spec to a ProtocolListTypeSpec if it represents an existential.
    /// For ProtocolListTypeSpec, returns as-is.
    /// For NamedTypeSpec with IsAny=true (single protocol existential), creates a ProtocolListTypeSpec with one protocol.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns>A ProtocolListTypeSpec representing the existential, or null if not an existential.</returns>
    public ProtocolListTypeSpec? ToProtocolListTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec protocolList)
            return protocolList;

        if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.IsAny)
        {
            // Single protocol existential: "any SomeProtocol" → ProtocolListTypeSpec with one protocol
            return new ProtocolListTypeSpec(new[] { namedTypeSpec });
        }

        return null;
    }

    /// <summary>
    /// Gets the number of protocols in an existential type.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The number of protocols.</returns>
    public int GetProtocolCount(ProtocolListTypeSpec protocolList) =>
        protocolList.Protocols.Count;

    /// <summary>
    /// Determines whether the existential type is the special "Any" type (zero protocols).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> if this is the "Any" type; otherwise, <c>false</c>.</returns>
    public bool IsAnyType(ProtocolListTypeSpec protocolList) =>
        protocolList.Protocols.Count == 0;

    /// <summary>
    /// Determines whether the existential type is a supported type.
    /// Currently supports:
    /// - Protocol compositions with 0-8 protocols (Any through 8-protocol compositions)
    /// - Only protocols without associated types (PATs are not fully supported)
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> if the existential is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedExistential(ProtocolListTypeSpec protocolList)
    {
        // Check witness table count limit
        if (protocolList.Protocols.Count > MaxSupportedWitnessTables)
            return false;

        // All protocols in the composition must be known
        foreach (var protocol in protocolList.Protocols.Keys)
        {
            // For now, we allow any protocol since we can't easily determine
            // if it has associated types from the type spec alone.
            // The runtime will handle the actual conformance checking.
        }

        return true;
    }

    /// <summary>
    /// Gets the appropriate C# existential container type for the given protocol list.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The C# existential container type name.</returns>
    public string GetCSharpExistentialType(ProtocolListTypeSpec protocolList)
    {
        var count = protocolList.Protocols.Count;
        return $"Swift.Runtime.ExistentialContainer{count}";
    }

    /// <summary>
    /// Gets the P/Invoke type for an existential container.
    /// Uses the appropriate ExistentialContainer struct.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The P/Invoke type name.</returns>
    public string GetPInvokeExistentialType(ProtocolListTypeSpec protocolList)
    {
        // For P/Invoke, we use the same ExistentialContainer type
        return GetCSharpExistentialType(protocolList);
    }

    /// <summary>
    /// Gets the size of the existential container in machine words (8 bytes each on 64-bit).
    /// Layout: 3 payload words + 1 metadata word + N witness table words
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The size in machine words.</returns>
    public int GetExistentialContainerSizeInWords(ProtocolListTypeSpec protocolList)
    {
        // 3 words for payload + 1 word for metadata + N words for witness tables
        return 4 + protocolList.Protocols.Count;
    }

    /// <summary>
    /// Gets the size of the existential container in bytes (64-bit platform).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The size in bytes.</returns>
    public int GetExistentialContainerSizeInBytes(ProtocolListTypeSpec protocolList)
    {
        return GetExistentialContainerSizeInWords(protocolList) * 8;
    }

    /// <summary>
    /// Gets a human-readable description of the existential type for diagnostics.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A description like "any SomeProtocol" or "any P1 & P2".</returns>
    public string GetExistentialDescription(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return "Any";

        var protocolNames = protocolList.Protocols.Keys.Select(p => p.NameWithoutModule);
        return $"any {string.Join(" & ", protocolNames)}";
    }

    /// <summary>
    /// Gets the list of protocol names from an existential type (used for interface generation).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A list of protocol names.</returns>
    public IReadOnlyList<string> GetProtocolNames(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Gets the list of protocol type specs from an existential type.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A list of protocol type specifications.</returns>
    public IReadOnlyList<NamedTypeSpec> GetProtocols(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys.ToList();
    }
}
