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
    private SortedDictionary<string, List<string>>? _compositionCollector;

    /// <summary>
    /// Maximum number of protocol witness tables supported.
    /// This corresponds to ExistentialContainer1 through ExistentialContainer8.
    /// </summary>
    public const int MaxSupportedWitnessTables = 8;

    public ExistentialHandler(ITypeDatabase typeDatabase, SortedDictionary<string, List<string>>? compositionCollector = null)
    {
        _typeDatabase = typeDatabase;
        _compositionCollector = compositionCollector;
    }

    /// <summary>
    /// Sets the composition collector on this handler for late injection.
    /// </summary>
    /// <remarks>
    /// IHandler.Marshal() creates environments (and their ExistentialHandler) before TypeHandlerContext
    /// is available, so the collector is null at construction. IHandler.Emit() receives the context and
    /// injects the collector here. We mutate the existing handler rather than recreating the environment
    /// because downstream code (SignatureHandler, WrapperEmitter) already holds references to this instance.
    /// </remarks>
    public void SetCompositionCollector(SortedDictionary<string, List<string>> collector)
    {
        _compositionCollector = collector;
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

    /// <summary>
    /// Determines whether the specified type spec represents an Optional-wrapped existential type.
    /// This is for types like (any DataCaching)? which are Swift.Optional with an existential generic parameter.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is an Optional containing an existential; otherwise, <c>false</c>.</returns>
    public bool IsOptionalExistential(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        // Check if it's Swift.Optional with exactly one generic parameter
        if (namedTypeSpec.Name != "Swift.Optional" || namedTypeSpec.GenericParameters.Count != 1)
            return false;

        // Check if the generic parameter is an existential
        var innerType = namedTypeSpec.GenericParameters[0];
        return IsExistential(innerType);
    }

    /// <summary>
    /// Extracts the inner existential type from an Optional-wrapped existential.
    /// </summary>
    /// <param name="typeSpec">The type specification (must be an Optional-wrapped existential).</param>
    /// <returns>The inner existential type as a ProtocolListTypeSpec, or null if not an Optional-wrapped existential.</returns>
    public ProtocolListTypeSpec? UnwrapOptionalExistential(TypeSpec typeSpec)
    {
        if (!IsOptionalExistential(typeSpec))
            return null;

        var namedTypeSpec = (NamedTypeSpec)typeSpec;
        var innerType = namedTypeSpec.GenericParameters[0];
        return ToProtocolListTypeSpec(innerType);
    }

    /// <summary>
    /// Gets the appropriate C# type for an Optional-wrapped existential.
    /// Returns a nullable existential container type (e.g., "Swift.Runtime.ExistentialContainer1?").
    /// </summary>
    /// <param name="protocolList">The protocol list type specification from the inner existential.</param>
    /// <returns>The C# nullable existential container type name.</returns>
    public string GetCSharpOptionalExistentialType(ProtocolListTypeSpec protocolList)
    {
        return $"{GetCSharpExistentialType(protocolList)}?";
    }

    /// <summary>
    /// Checks whether a protocol composition maps to a well-known runtime type
    /// (e.g., 'any Swift.Error' → Swift.AnyError). Extensible for future stdlib protocols.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <param name="csharpType">The fully-qualified C# type name if this is a well-known protocol.</param>
    /// <returns><c>true</c> if this is a well-known protocol with a direct runtime type mapping.</returns>
    public bool TryGetWellKnownProtocolType(ProtocolListTypeSpec protocolList, out string csharpType)
    {
        csharpType = "";
        if (protocolList.Protocols.Count != 1)
            return false;

        var protocol = protocolList.Protocols.Keys.First();
        var swiftName = protocol.Name; // e.g., "Swift.Error"

        if (swiftName == "Swift.Error")
        {
            csharpType = "Swift.AnyError";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the protocol interface name for public API (e.g., "IDescribable").
    /// For multi-protocol compositions, returns a combined interface name.
    /// Well-known stdlib protocols (e.g., Swift.Error) return their direct runtime types.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The public-facing interface type name.</returns>
    public string GetPublicExistentialType(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return "object"; // 'any' with no protocols → object

        // Well-known stdlib protocols → direct runtime type (no proxy needed)
        if (TryGetWellKnownProtocolType(protocolList, out var wellKnownType))
            return wellKnownType;

        if (protocolList.Protocols.Count == 1)
        {
            var firstProtocol = protocolList.Protocols.Keys.First();

            // Validate that the protocol has a TypeRecord in the database with Kind=Protocol.
            // This handles multiple cases:
            //   - Metatype expressions (e.g., "Any.Type") misclassified as protocols → no TypeRecord → object
            //   - Real protocols with emitted interfaces → TypeRecord with Kind=Protocol → I{Name}
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(firstProtocol);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) ||
                    typeRecord.Kind != TypeRecordKind.Protocol)
                {
                    return "object";
                }
            }
            catch
            {
                // FromTypeSpec/FromModuleQualifiedName may throw for malformed names
                return "object";
            }

            // Generic protocol existentials (e.g., "any EventStream<τ_0_0.Event>")
            // have associated type refs we can't resolve to concrete C# types.
            // Use AnyType to preserve API surface (not "object" which triggers member pruning).
            if (firstProtocol.GenericParameters.Count > 0)
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;

            return NameProvider.GetInterfaceName(firstProtocol.NameWithoutModule, moduleName: firstProtocol.Module);
        }

        // Multi-protocol: generate combined interface name
        return GetCompositionInterfaceName(protocolList);
    }

    /// <summary>
    /// Returns nullable protocol interface (e.g., "IDescribable?").
    /// </summary>
    /// <param name="protocolList">The protocol list type specification from the inner existential.</param>
    /// <returns>The nullable public-facing interface type name.</returns>
    public string GetPublicOptionalExistentialType(ProtocolListTypeSpec protocolList)
    {
        return $"{GetPublicExistentialType(protocolList)}?";
    }

    /// <summary>
    /// Gets the proxy class name for an existential type (used for container→interface wrapping).
    /// For single protocols: "DescribableProxy". For compositions: "DescribableAndIdentifiableProxy".
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The proxy class name.</returns>
    public string GetProxyClassName(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 1)
        {
            var protocolName = protocolList.Protocols.Keys.First().NameWithoutModule;
            return $"{protocolName}Proxy";
        }

        // Multi-protocol: combined proxy name
        var names = protocolList.Protocols.Keys
            .Select(p => p.NameWithoutModule)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return string.Join("And", names) + "Proxy";
    }

    /// <summary>
    /// Checks whether ALL protocols in a composition have TypeRecords with Kind == Protocol.
    /// Returns false if any protocol is unknown/unregistered or not a Protocol kind.
    /// </summary>
    public bool AllProtocolsHaveTypeRecords(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return false;

        foreach (var protocol in protocolList.Protocols.Keys)
        {
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) ||
                    typeRecord.Kind != TypeRecordKind.Protocol)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Tries to get the proxy class name using the same ObjC-filtered protocol set
    /// as GetCompositionInterfaceName. Returns false if no non-ObjC protocols remain
    /// (e.g., `any NSObjectProtocol` — the proxy class doesn't exist).
    /// </summary>
    public bool TryGetFilteredProxyClassName(ProtocolListTypeSpec protocolList, out string proxyClassName)
    {
        proxyClassName = "";
        var protocols = protocolList.Protocols.Keys
            .Where(p => !TypeDatabaseExtensions.IsObjCModuleType(p))
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();
        if (protocols.Count == 0) return false;
        if (protocols.Count == 1) { proxyClassName = $"{protocols[0].NameWithoutModule}Proxy"; return true; }
        proxyClassName = string.Join("And", protocols.Select(p => p.NameWithoutModule)) + "Proxy";
        return true;
    }

    /// <summary>
    /// Gets the combined interface name for a multi-protocol composition.
    /// Protocol names are sorted alphabetically for determinism.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The combined interface name (e.g., "IDescribableAndTestIdentifiable").</returns>
    public string GetCompositionInterfaceName(ProtocolListTypeSpec protocolList)
    {
        // B17: Filter out protocols from ObjC root modules (Foundation, ObjectiveC, UIKit, etc.)
        // No interface is emitted for these types, so including them would produce invalid C# references.
        var protocols = protocolList.Protocols.Keys
            .Where(p => !TypeDatabaseExtensions.IsObjCModuleType(p))
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();

        // If filtering leaves only 1 protocol, return its interface name directly
        if (protocols.Count == 1)
        {
            return NameProvider.GetInterfaceName(protocols[0].NameWithoutModule, moduleName: protocols[0].Module);
        }

        // If all protocols were filtered out, return object
        if (protocols.Count == 0)
        {
            return "object";
        }

        var names = protocols.Select(p => p.NameWithoutModule).ToList();
        var compositionName = "I" + string.Join("And", names);

        // Collect for later emission via the per-conductor scoped collector
        var parentInterfaces = protocols.Select(p => NameProvider.GetInterfaceName(p.NameWithoutModule, moduleName: p.Module)).ToList();
        _compositionCollector?.TryAdd(compositionName, parentInterfaces);

        return compositionName;
    }
}
