// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits Swift code for the EveryProtocol pattern.
/// This enables C# code to implement Swift protocols by:
/// 1. Defining an EveryProtocol class that serves as the concrete type behind protocol proxies
/// 2. Generating protocol extensions that call back to C# via vtable function pointers
/// 3. Creating vtable structures that store function pointers for each protocol method
/// 4. Providing SetVtable functions that C# calls to register its vtable with Swift
/// </summary>
public class EveryProtocolEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;
    private readonly ModuleEmissionContext? _emissionContext;

    public EveryProtocolEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName, ModuleEmissionContext? emissionContext = null)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
        _emissionContext = emissionContext;
    }

    /// <summary>
    /// Emits the EveryProtocol class definition.
    /// This class is the concrete Swift type behind all protocol proxy objects.
    /// </summary>
    public void EmitEveryProtocolClass(SwiftWriter writer)
    {
        writer.WriteLines($$"""
            // EveryProtocol is a Swift class that can conform to any protocol.
            // Protocol method implementations call back to C# via vtable function pointers.
            // This class is used by generated proxy classes to implement Swift protocols from C#.
            public final class EveryProtocol {
                // Store a handle back to the C# proxy object
                // This is used by vtable functions to find the C# implementation
                public var handle: UnsafeRawPointer?

                public init() {
                    self.handle = nil
                }

                public init(handle: UnsafeRawPointer) {
                    self.handle = handle
                }
            }

            """);
    }

    /// <summary>
    /// Emits stub conformances for Decodable, Encodable, and/or Error on EveryProtocol
    /// when any suitable protocol inherits from them. Without these stubs, Swift rejects
    /// `extension EveryProtocol: SomeProtocol` when SomeProtocol inherits Decodable/Encodable/Error.
    /// The stubs are no-ops since actual encoding/decoding happens on the C# side.
    /// </summary>
    public void EmitCodableStubsIfNeeded(SwiftWriter writer, IReadOnlyList<ProtocolDecl> suitableProtocols,
        IReadOnlyList<ProtocolDecl> allProtocols, ITypeDatabase typeDatabase)
    {
        bool needsDecodable = false;
        bool needsEncodable = false;
        bool needsError = false;

        foreach (var protocol in suitableProtocols)
        {
            foreach (var inherited in protocol.InheritedProtocols)
            {
                var simpleName = inherited.NameWithoutModule;
                if (simpleName is "Decodable" or "Codable")
                    needsDecodable = true;
                if (simpleName is "Encodable" or "Codable")
                    needsEncodable = true;
                if (simpleName == "Error")
                    needsError = true;

                // Also check transitively: if an inherited protocol is in allProtocols,
                // check its inherited protocols recursively
                CheckTransitiveCodableNeeds(simpleName, inherited.Name, allProtocols, typeDatabase,
                    ref needsDecodable, ref needsEncodable, ref needsError,
                    new HashSet<string>(StringComparer.Ordinal));
            }
        }

        if (needsDecodable)
        {
            writer.WriteLines("""
                // Stub Decodable conformance for EveryProtocol.
                // Actual decoding happens on the C# side via vtable dispatch.
                extension EveryProtocol: Decodable {
                    public convenience init(from decoder: Decoder) throws {
                        self.init()
                    }
                }

                """);
        }

        if (needsEncodable)
        {
            writer.WriteLines("""
                // Stub Encodable conformance for EveryProtocol.
                // Actual encoding happens on the C# side via vtable dispatch.
                extension EveryProtocol: Encodable {
                    public func encode(to encoder: Encoder) throws {
                        // no-op — encoding is handled by C# proxy
                    }
                }

                """);
        }

        if (needsError)
        {
            writer.WriteLines("""
                // Stub Error conformance for EveryProtocol.
                // Error handling is managed by the C# proxy via vtable dispatch.
                extension EveryProtocol: Swift.Error {}

                """);
        }
    }

    private void CheckTransitiveCodableNeeds(string simpleName, string fullName,
        IReadOnlyList<ProtocolDecl> allProtocols, ITypeDatabase typeDatabase,
        ref bool needsDecodable, ref bool needsEncodable, ref bool needsError,
        HashSet<string> visited)
    {
        if (!visited.Add(fullName))
            return;

        // Look up in same-module protocols
        var found = allProtocols.FirstOrDefault(p =>
            p.Name == simpleName || p.Name == fullName ||
            p.SwiftTypeName?.ToString() == fullName);

        if (found != null)
        {
            foreach (var inherited in found.InheritedProtocols)
            {
                var innerSimpleName = inherited.NameWithoutModule;
                if (innerSimpleName is "Decodable" or "Codable")
                    needsDecodable = true;
                if (innerSimpleName is "Encodable" or "Codable")
                    needsEncodable = true;
                if (innerSimpleName == "Error")
                    needsError = true;

                CheckTransitiveCodableNeeds(innerSimpleName, inherited.Name, allProtocols, typeDatabase,
                    ref needsDecodable, ref needsEncodable, ref needsError, visited);
            }
        }
    }

    /// <summary>
    /// Emits the vtable struct for a protocol.
    /// The vtable contains function pointers for each protocol requirement.
    /// </summary>
    public void EmitProtocolVtableStruct(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var vtableName = GetVtableStructName(protocolDecl);

        writer.WriteLine($"// Vtable for {protocolDecl.Name} protocol - stores function pointers to C# implementations");
        writer.WriteLine($"fileprivate struct {vtableName} {{");
        writer.Indent++;

        // First field: handle to C# vtable (used to pass context back to C#)
        writer.WriteLine("var csVTHandle: OpaquePointer? = nil");

        // Track emitted fields to avoid duplicates
        var emittedFields = new HashSet<string>();

        // Property getters and setters (skip static and @objc optional properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic || property.IsObjCOptional)
                continue;
            EmitPropertyVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript getters and setters (skip static subscripts - not part of witness table)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            EmitSubscriptVtableFields(writer, subscript, protocolDecl, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Methods - track by signature to handle overloads correctly
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            // Skip constructors, static, and @objc optional methods
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.IsObjCOptional)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Only emit vtable field for new methods (not duplicates)
                EmitMethodVtableField(writer, method, protocolDecl, idx, emittedFields);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit the global vtable instance
        var instanceName = GetVtableInstanceName(protocolDecl);
        writer.WriteLine($"private var {instanceName} = {vtableName}()");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the protocol extension that makes EveryProtocol conform to the protocol.
    /// Each method/property implementation calls back to C# via the vtable.
    /// </summary>
    public void EmitProtocolExtension(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        EmitProtocolExtension(writer, protocolDecl, null);
    }

    /// <summary>
    /// Emits the protocol extension that makes EveryProtocol conform to the protocol.
    /// Each method/property implementation calls back to C# via the vtable.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track signatures globally across protocols.</param>
    /// <param name="nonThrowingOverrides">Signatures where throws must be suppressed (see EmitProtocolConformance).</param>
    private void EmitProtocolExtension(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides = null)
    {
        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var vtableInstanceName = GetVtableInstanceName(protocolDecl);

        writer.WriteLine($"// EveryProtocol conformance to {protocolDecl.Name}");
        writer.WriteLine($"extension EveryProtocol: {protocolName} {{");
        writer.Indent++;

        // Emit typealiases for associated types
        // For PAT protocols, we use type erasure by mapping associated types to Any
        foreach (var associatedType in protocolDecl.AssociatedTypes)
        {
            writer.WriteLine($"public typealias {associatedType.Name} = Any");
        }
        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            writer.WriteLine();
        }

        // Track emitted members to avoid duplicates within this protocol
        var emittedMembers = new HashSet<string>();

        // Emit property implementations (skip static and @objc optional properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic || property.IsObjCOptional)
                continue;
            var swiftSignature = $"var_{property.Name}";
            // Check for global conflicts
            if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(swiftSignature))
            {
                _logger.LogDebug($"Skipping property '{property.Name}' in {protocolDecl.Name}: conflicts with already-emitted property");
                continue;
            }
            if (emittedMembers.Add($"property:{property.Name}"))
            {
                EmitPropertyImplementation(writer, property, protocolDecl, vtableInstanceName);
            }
        }

        // Emit subscript implementations (skip static subscripts - not part of witness table)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = GetSubscriptKey(subscript, subscriptIndex);
            var swiftSignature = $"subscript_{subscriptKey}";
            // Check for global conflicts
            if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(swiftSignature))
            {
                _logger.LogDebug($"Skipping subscript in {protocolDecl.Name}: conflicts with already-emitted subscript");
                subscriptIndex++;
                continue;
            }
            if (emittedMembers.Add(subscriptKey))
            {
                EmitSubscriptImplementation(writer, subscript, protocolDecl, vtableInstanceName, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Emit method implementations
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            // Skip constructors, static, and @objc optional methods
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.IsObjCOptional)
                continue;

            var methodKey = GetMethodKey(method);

            // Assign vtable index matching EmitProtocolVtableStruct logic.
            // This MUST happen before the global skip check to prevent index drift (Bug #21).
            // The vtable struct assigns sequential indices without knowledge of global skips,
            // so the extension must use the same indices.
            bool isNewMethod = false;
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                isNewMethod = true;
            }

            var swiftSignature = GetSwiftMethodSignature(method);

            // Check for global conflicts (method name + parameter count defines the signature)
            if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(swiftSignature))
            {
                _logger.LogDebug($"Skipping method '{method.Name}' in {protocolDecl.Name}: conflicts with already-emitted method");
                continue;
            }

            // Only emit method implementation for new methods (not within-protocol duplicates)
            if (isNewMethod)
            {
                // If this signature is in the non-throwing overrides set, suppress throws.
                // A non-throwing method satisfies both throwing and non-throwing protocol requirements,
                // but a throwing method does NOT satisfy a non-throwing requirement.
                var effectiveThrows = method.Throws &&
                    !(nonThrowingOverrides?.Contains(swiftSignature) == true);
                EmitMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, effectiveThrows);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a Swift method signature string for conflict detection.
    /// Internal so ModuleHandler can use it for pre-pass analysis.
    /// </summary>
    internal string GetSwiftMethodSignature(MethodDecl method)
    {
        // Generate signature like "removeAll()" or "process(_:)"
        var paramLabels = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param, i);
            paramLabels.Add(label == "_" ? "_" : label);
        }
        return $"{method.Name}({string.Join(":", paramLabels)}{(paramLabels.Count > 0 ? ":" : "")})";
    }

    /// <summary>
    /// Emits Swift functions that export the protocol witness table and type metadata.
    /// These are called via P/Invoke from C# to get the witness table pointer.
    /// </summary>
    public void EmitWitnessTableGetter(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var getterFunctionName = GetWitnessTableGetterFunctionName(protocolDecl);
        var mangledGetterName = GetWitnessTableGetterMangledName(protocolDecl);

        writer.WriteLines($$"""
            // Returns the protocol witness table pointer for EveryProtocol conforming to {{protocolDecl.Name}}.
            // C# calls this via P/Invoke to obtain the witness table for existential container construction.
            @_silgen_name("{{mangledGetterName}}")
            public func {{getterFunctionName}}() -> UnsafeRawPointer {
                let instance = EveryProtocol()
                return withExtendedLifetime(instance) {
                    var proto: any {{protocolName}} = instance
                    return withUnsafeBytes(of: &proto) { buffer in
                        // Existential layout for class-bound protocols:
                        // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
                        // For a single-protocol existential, witness table is at offset 4 * pointer size
                        let witnessTableOffset = 4 * MemoryLayout<Int>.size
                        return buffer.baseAddress!.advanced(by: witnessTableOffset)
                            .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
                    }
                }
            }

            """);
    }

    /// <summary>
    /// Emits Swift function that exports the EveryProtocol type metadata.
    /// </summary>
    public void EmitTypeMetadataGetter(SwiftWriter writer)
    {
        writer.WriteLines($$"""
            // Returns the type metadata pointer for EveryProtocol.
            // C# calls this via P/Invoke to construct existential containers.
            @_silgen_name("Get_EveryProtocol_TypeMetadata")
            public func getEveryProtocolTypeMetadata() -> UnsafeRawPointer {
                return unsafeBitCast(EveryProtocol.self as Any.Type, to: UnsafeRawPointer.self)
            }

            """);
    }

    /// <summary>
    /// Emits the SetVtable function that C# calls to register its vtable.
    /// </summary>
    public void EmitSetVtableFunction(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var vtableName = GetVtableStructName(protocolDecl);
        var vtableInstanceName = GetVtableInstanceName(protocolDecl);
        var setFunctionName = GetSetVtableFunctionName(protocolDecl);
        var mangledSetFunctionName = GetSetVtableMangledName(protocolDecl);

        writer.WriteLines($$"""
            // Called by C# to register the protocol vtable
            @_silgen_name("{{mangledSetFunctionName}}")
            public func {{setFunctionName}}(uvt: UnsafeRawPointer) {
                let vt: UnsafePointer<{{vtableName}}> = uvt.assumingMemoryBound(to: {{vtableName}}.self)
                {{vtableInstanceName}} = vt.pointee
            }

            """);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        EmitProtocolConformance(writer, protocolDecl, null);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track method signatures globally across protocols.
    /// When provided, methods that would conflict with already-emitted signatures are skipped.</param>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl, HashSet<string>? globalEmittedSignatures)
    {
        EmitProtocolConformance(writer, protocolDecl, globalEmittedSignatures, null);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track method signatures globally across protocols.</param>
    /// <param name="nonThrowingOverrides">Signatures where non-throwing MUST be emitted because at least one
    /// protocol requires the method non-throwing. A non-throwing method satisfies both throwing and non-throwing
    /// protocol requirements, but a throwing method does NOT satisfy a non-throwing requirement.</param>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides)
    {
        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple type erasure to Any
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has Self requirement");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "HasSelfRequirement");
            return;
        }

        // Skip protocols with Self-typed INSTANCE members (generic type parameters like τ_0_0 in
        // return/params/properties). Static members are excluded — they're not part of the witness
        // table and don't need EveryProtocol implementations.
        // The parser's HasSelfRequirement check looks for "Self" in GenericSig, but ABI JSON uses τ_0_0.
        // SwiftTypeNameHelper converts generic type params to "Any", so Self-returning methods emit
        // "-> Any" instead of "-> Self", which Swift rejects.
        bool hasSelfTypedMembers = protocolDecl.Methods
            .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
            .Any(m => HasGenericTypeParamInSignature(m));
        bool hasSelfTypedProperties = protocolDecl.Properties
            .Where(p => !p.IsStatic)
            .Any(p => ContainsGenericTypeParam(p.SwiftTypeSpec));
        bool hasSelfTypedSubscripts = protocolDecl.Subscripts
            .Where(s => !s.IsStatic)
            .Any(s => ContainsGenericTypeParam(s.ReturnTypeSpec) ||
                      s.IndexParameters.Any(ip => ContainsGenericTypeParam(ip.SwiftTypeSpec)));
        if (hasSelfTypedMembers || hasSelfTypedProperties || hasSelfTypedSubscripts)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has Self-typed members (generic type params in signature)");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "SelfTypedMembers");
            return;
        }

        // Skip class-bound protocols — EveryProtocol is a class, but protocols that
        // inherit from NSObjectProtocol or AnyObject require NSObject methods/identity
        // that EveryProtocol can't provide (e.g., isEqual:, hash, description).
        if (IsClassBoundProtocol(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: class-bound protocol (NSObjectProtocol/AnyObject)");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "ClassBound");
            return;
        }

        // Skip CaseIterable — requires compiler-synthesized `allCases` static property
        // that EveryProtocol can't provide. Checked transitively.
        if (InheritsCaseIterable(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: CaseIterable requires compiler synthesis");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "CaseIterable");
            return;
        }

        // Skip protocols that inherit from protocols with associated types.
        // EveryProtocol can't provide concrete associated types for inherited PATs.
        if (ModuleHandler.InheritsProtocolWithAssociatedTypes(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits protocol with associated types");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "InheritedAssociatedTypes");
            return;
        }

        // Skip protocols that inherit from stdlib protocols with requirements
        // EveryProtocol can't satisfy (CustomStringConvertible, CodingKey, etc.).
        if (InheritsUnsatisfiedStdlibProtocol(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits unsatisfied stdlib protocol");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "UnsatisfiedStdlibProtocol");
            return;
        }

        // Skip protocols with constructor requirements — EveryProtocol can't provide init methods
        // via the vtable callback pattern. The conformance would be incomplete (missing inits).
        if (protocolDecl.Methods.Any(m => m.IsConstructor))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has constructor requirements");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "ConstructorRequirements");
            return;
        }

        // Check for implementable instance members.
        // Static members are not part of the witness table, so we only count non-static members.
        var hasImplementableMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any(s => !s.IsStatic);

        // Check if this protocol has static requirements that need stub implementations
        var hasStaticRequirements = protocolDecl.Properties.Any(p => p.IsStatic) ||
                                    protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);

        // Composition/marker protocols (no own instance members) still need EveryProtocol
        // conformances so C# proxy classes can create existential containers. They are allowed
        // if they have static requirements OR inherit from non-trivial protocols.
        bool hasNonTrivialInheritance = protocolDecl.InheritedProtocols.Any(inh =>
                inh.NameWithoutModule != "AnyObject" &&
                inh.NameWithoutModule != "Escapable" &&
                inh.NameWithoutModule != "Copyable" &&
                inh.NameWithoutModule != "Sendable" &&
                inh.NameWithoutModule != "SendableMetatype");

        if (!hasImplementableMembers && !hasStaticRequirements && !hasNonTrivialInheritance)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: no implementable instance members and no static requirements");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "NoImplementableMembers");
            return;
        }

        // Skip protocols with static method requirements — static method stubs can't
        // render correct Swift signatures (parameter labels, types, return type).
        // Static properties work with fatalError() but methods need full signatures.
        var hasStaticMethodRequirements = protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);
        if (hasStaticMethodRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has static method requirements (can't generate correct stub signatures)");
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, "StaticMethodRequirements");
            return;
        }

        if (hasImplementableMembers)
        {
            EmitProtocolVtableStruct(writer, protocolDecl);
            EmitProtocolExtension(writer, protocolDecl, globalEmittedSignatures, nonThrowingOverrides);
            EmitSetVtableFunction(writer, protocolDecl);
        }
        else
        {
            // Static-only or composition protocol: emit conformance with stub implementations
            // for static property requirements. Instance member vtable dispatch is not needed.
            var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
            writer.WriteLine($"// EveryProtocol conformance to {protocolDecl.Name} (static/composition protocol)");
            writer.WriteLine($"extension EveryProtocol: {protocolName} {{");
            // Emit stubs for static property requirements.
            // fatalError() returns Never, which satisfies any return type requirement.
            foreach (var prop in protocolDecl.Properties.Where(p => p.IsStatic))
            {
                var propType = ExistentialBypassEmitter.RenderSwiftTypeSpec(prop.SwiftTypeSpec);
                // Self-typed (τ_0_0) static properties: use EveryProtocol as the concrete Self type
                if (propType.Contains("τ_0_0") || propType == "Any")
                    propType = "EveryProtocol";
                writer.WriteLine($"    public static var {prop.Name}: {propType} {{ fatalError(\"EveryProtocol does not support static protocol requirements\") }}");
            }
            writer.WriteLine("}");
            writer.WriteLine();
        }
        EmitWitnessTableGetter(writer, protocolDecl);
        _emissionContext?.RecordConformanceDecision(protocolDecl.Name, true, null);
    }

    #region Private Helper Methods

    private void EmitPropertyVtableFields(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                var funcType = $"(@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }

        if (hasSetter)
        {
            var fieldName = $"func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                var funcType = $"(@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
    }

    private void EmitSubscriptVtableFields(SwiftWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        // Build parameter types: OpaquePointer? (vtable handle), UnsafeRawPointer (self), then index params
        if (subscript.HasGetter)
        {
            var fieldName = $"func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                var paramCount = subscript.IndexParameters.Count;
                var paramList = "OpaquePointer?, UnsafeRawPointer" + string.Concat(Enumerable.Repeat(", UnsafeRawPointer", paramCount));
                var funcType = $"(@convention(c)({paramList}) -> UnsafeRawPointer)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }

        if (subscript.HasSetter)
        {
            var fieldName = $"func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                var paramCount = subscript.IndexParameters.Count;
                // For setter: vtable handle, self, newValue, then index params
                var paramList = "OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer" + string.Concat(Enumerable.Repeat(", UnsafeRawPointer", paramCount));
                var funcType = $"(@convention(c)({paramList}) -> Void)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
    }

    private void EmitMethodVtableField(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        var fieldName = GetMethodVtableFieldName(method, index);
        if (!emittedFields.Add(fieldName))
            return;

        // Build function pointer type
        // Parameters: OpaquePointer? (vtable handle), UnsafeRawPointer (self), then method params
        var paramCount = method.CSSignature.Count - 1; // Exclude return type
        var paramList = "OpaquePointer?, UnsafeRawPointer" + string.Concat(Enumerable.Repeat(", UnsafeRawPointer", paramCount));

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        var returnTypeStr = hasReturn ? "UnsafeRawPointer" : "Void";
        var funcType = $"(@convention(c)({paramList}) -> {returnTypeStr})?";

        writer.WriteLine($"var {fieldName}: {funcType}");
    }

    private void EmitPropertyImplementation(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string vtableInstanceName)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        var swiftTypeName = GetSwiftTypeName(property.SwiftTypeSpec);
        var swiftTypeNameForMetatype = GetSwiftTypeNameForMetatype(property.SwiftTypeSpec);

        writer.WriteLine($"public var {property.Name}: {swiftTypeName} {{");
        writer.Indent++;

        if (hasGetter)
        {
            writer.WriteLines($$"""
                get {
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    let resultPtr = {{vtableInstanceName}}.func_{{property.Name}}_get!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto)
                    return resultPtr.assumingMemoryBound(to: {{swiftTypeNameForMetatype}}.self).pointee
                }
                """);
        }

        if (hasSetter)
        {
            writer.WriteLines($$"""
                set {
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    var newValueCopy = newValue
                    {{vtableInstanceName}}.func_{{property.Name}}_set!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto, &newValueCopy)
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitSubscriptImplementation(SwiftWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string vtableInstanceName, int index)
    {
        // Build parameter list
        var parameters = new List<string>();
        foreach (var param in subscript.IndexParameters)
        {
            var paramTypeName = GetSwiftTypeName(param.SwiftTypeSpec);
            var paramName = string.IsNullOrEmpty(param.Name) ? "index" : param.Name;
            parameters.Add($"{paramName}: {paramTypeName}");
        }
        var parametersString = string.Join(", ", parameters);

        var returnTypeName = GetSwiftTypeName(subscript.ReturnTypeSpec);
        var returnTypeNameForMetatype = GetSwiftTypeNameForMetatype(subscript.ReturnTypeSpec);

        writer.WriteLine($"public subscript({parametersString}) -> {returnTypeName} {{");
        writer.Indent++;

        if (subscript.HasGetter)
        {
            var argPassList = BuildArgumentPassList(subscript.IndexParameters);
            writer.WriteLines($$"""
                get {
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    {{argPassList}}
                    let resultPtr = {{vtableInstanceName}}.func_subscript_{{index}}_get!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto{{BuildArgRefs(subscript.IndexParameters)}})
                    return resultPtr.assumingMemoryBound(to: {{returnTypeNameForMetatype}}.self).pointee
                }
                """);
        }

        if (subscript.HasSetter)
        {
            var argPassList = BuildArgumentPassList(subscript.IndexParameters);
            writer.WriteLines($$"""
                set {
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    var newValueCopy = newValue
                    {{argPassList}}
                    {{vtableInstanceName}}.func_subscript_{{index}}_set!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto, &newValueCopy{{BuildArgRefs(subscript.IndexParameters)}})
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodImplementation(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        string vtableInstanceName, int index, bool? effectiveThrows = null)
    {
        // Build parameter list with proper Swift labeling
        var parameters = new List<string>();
        var internalNames = new List<string>(); // Names used inside the function body
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = GetSwiftTypeName(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            internalNames.Add(internalName);

            // Add inout modifier if the parameter is passed by reference
            var inoutPrefix = param.IsInOut ? "inout " : "";

            // Swift parameter format: "externalLabel internalName: Type" or "_ internalName: Type"
            if (externalLabel == "_")
            {
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            }
            else if (externalLabel == internalName)
            {
                // Same label and name - just use one
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            }
            else
            {
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
            }
        }
        var parametersString = string.Join(", ", parameters);

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetSwiftTypeName(returnType!) : "Void";
        var returnTypeNameForMetatype = hasReturn ? GetSwiftTypeNameForMetatype(returnType!) : "Void";
        var throwsDecl = (effectiveThrows ?? method.Throws) ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        var fieldName = GetMethodVtableFieldName(method, index);

        writer.WriteLine($"public func {method.Name}({parametersString}){throwsDecl}{returnDecl} {{");
        writer.Indent++;

        // Build argument copies for passing to vtable function
        var argPassList = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var paramName = internalNames[i];
            argPassList.Add($"var {paramName}Copy = {paramName}");
        }

        var argRefList = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var paramName = internalNames[i];
            argRefList.Add($"&{paramName}Copy");
        }
        var argRefs = argRefList.Count > 0 ? ", " + string.Join(", ", argRefList) : "";

        var argPassCode = argPassList.Count > 0 ? string.Join("\n        ", argPassList) + "\n        " : "";

        // Build writeback code for inout parameters
        var writebackLines = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var param = method.CSSignature[i + 1]; // +1 to skip return type
            if (param.IsInOut)
            {
                writebackLines.Add($"{internalNames[i]} = {internalNames[i]}Copy");
            }
        }
        var writebackCode = writebackLines.Count > 0 ? "\n        " + string.Join("\n        ", writebackLines) : "";

        if (hasReturn)
        {
            writer.WriteLines($$"""
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                    return resultPtr.assumingMemoryBound(to: {{returnTypeNameForMetatype}}.self).pointee
                """);
        }
        else
        {
            writer.WriteLines($$"""
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    {{argPassCode}}{{vtableInstanceName}}.{{fieldName}}!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Checks if a method has generic type parameters (e.g., τ_0_0 representing Self)
    /// in its return type or non-self parameters. Uses recursive TypeSpec traversal.
    /// </summary>
    private static bool HasGenericTypeParamInSignature(MethodDecl method)
    {
        // Check return type (CSSignature[0])
        if (method.CSSignature.Count > 0 && ContainsGenericTypeParam(method.CSSignature[0].SwiftTypeSpec))
            return true;

        // Check non-self parameters (skip return type at index 0)
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsGenericTypeParam(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a generic type parameter.
    /// Walks through NamedTypeSpec, TupleTypeSpec, ClosureTypeSpec, and ProtocolListTypeSpec.
    /// </summary>
    private static bool ContainsGenericTypeParam(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                    return true;
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ContainsGenericTypeParam(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ContainsGenericTypeParam(element))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ContainsGenericTypeParam(closureType.Arguments))
                    return true;
                if (ContainsGenericTypeParam(closureType.ReturnType))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolListType:
                foreach (var protocol in protocolListType.Protocols.Keys)
                {
                    if (ContainsGenericTypeParam(protocol))
                        return true;
                }
                return false;

            case AssociatedTypeReferenceSpec assocType:
                // Associated types like Self.Element or τ_0_0.Element reference
                // unresolved generic type parameters through their base type.
                return TypeSpecHelpers.IsGenericTypeParameter(assocType.BaseType)
                    || assocType.BaseType == "Self";

            default:
                return false;
        }
    }

    private string GetSwiftTypeName(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeName(typeSpec);

    private string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(typeSpec);

    private string BuildArgumentPassList(IReadOnlyList<ArgumentDecl> parameters)
    {
        var lines = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var paramName = string.IsNullOrEmpty(param.Name) || param.Name == "_" ? $"arg{i}" : param.Name;
            lines.Add($"var {paramName}Copy = {paramName}");
        }
        return lines.Count > 0 ? string.Join("\n        ", lines) : "";
    }

    private string BuildArgRefs(IReadOnlyList<ArgumentDecl> parameters)
    {
        var refs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var paramName = string.IsNullOrEmpty(param.Name) || param.Name == "_" ? $"arg{i}" : param.Name;
            refs.Add($"&{paramName}Copy");
        }
        return refs.Count > 0 ? ", " + string.Join(", ", refs) : "";
    }

    private static string GetVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}_vtable";
    }

    private static string GetVtableInstanceName(ProtocolDecl protocolDecl)
    {
        var name = protocolDecl.Name;
        // Convert first char to lowercase for instance name
        return $"_{char.ToLowerInvariant(name[0])}{name.Substring(1)}_vtable";
    }

    private static string GetSetVtableFunctionName(ProtocolDecl protocolDecl)
    {
        return $"set{protocolDecl.Name}_vtable";
    }

    private static string GetSetVtableMangledName(ProtocolDecl protocolDecl)
    {
        // Use @_silgen_name to control the symbol name that C# will call
        return $"Set{protocolDecl.Name}_vtable";
    }

    private static string GetWitnessTableGetterFunctionName(ProtocolDecl protocolDecl)
    {
        return $"getEveryProtocol{protocolDecl.Name}WitnessTable";
    }

    private static string GetWitnessTableGetterMangledName(ProtocolDecl protocolDecl)
    {
        // Use @_silgen_name to control the symbol name that C# will call
        return $"Get_EveryProtocol_{protocolDecl.Name}_WitnessTable";
    }

    private static string GetMethodKey(MethodDecl method)
    {
        // Create a unique key for method overloading based on name and parameter types
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    private static string GetSubscriptKey(SubscriptDecl subscript, int index)
    {
        // Create a unique key for subscript overloading
        return $"subscript_{index}(" + string.Join(",", subscript.IndexParameters.Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    private static string GetMethodVtableFieldName(MethodDecl method, int index)
    {
        return $"func_{method.Name}_{index}";
    }

    /// <summary>
    /// Gets the Swift parameter label for a method argument.
    /// Uses "_" for unlabeled parameters (Swift convention).
    /// </summary>
    private static string GetSwiftParameterLabel(ArgumentDecl param, int index)
    {
        // The parser converts "_" to "argN" for internal C# use
        // For Swift code generation, we need to convert back to "_"
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || NameProvider.IsGeneratedArgName(param.Name))
        {
            return "_";
        }
        // Strip the underscore prefix added by ExtractUniqueName for C# keywords
        return NameProvider.StripCSharpKeywordPrefix(param.Name);
    }

    /// <summary>
    /// Gets the internal parameter name used in the implementation.
    /// </summary>
    private static string GetSwiftParameterName(ArgumentDecl param, int index)
    {
        // Use private name if available (but not _ which is a discard pattern, not a variable)
        if (!string.IsNullOrEmpty(param.PrivateName) && param.PrivateName != "_")
        {
            return param.PrivateName;
        }
        // If name looks like a generated "argN", keep using it as internal name
        if (NameProvider.IsGeneratedArgName(param.Name))
        {
            return param.Name;
        }
        // Otherwise use the public name or generate one
        if (!string.IsNullOrEmpty(param.Name) && param.Name != "_")
        {
            // Strip C# keyword prefix for Swift
            var swiftName = NameProvider.StripCSharpKeywordPrefix(param.Name);
            // If the name is a Swift keyword, use a modified internal name
            // to avoid conflicts (Swift allows keyword names with backticks, but
            // for simplicity we'll use a suffix for the internal name)
            if (NameProvider.IsSwiftKeyword(swiftName))
            {
                return $"{swiftName}Value"; // Use suffix for Swift keywords
            }
            return swiftName;
        }
        return $"arg{index}";
    }

    /// <summary>
    /// Checks if a protocol is class-bound (inherits from NSObjectProtocol, AnyObject, or is marked class-bound),
    /// either directly or transitively through inherited protocols.
    /// Class-bound protocols require NSObject identity semantics that EveryProtocol can't provide.
    /// </summary>
    /// <param name="protocolDecl">The protocol to check.</param>
    /// <param name="allProtocols">All protocols in the module for intra-module transitive lookup.
    /// If null, only direct inheritance is checked.</param>
    internal static bool IsClassBoundProtocol(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return IsClassBoundProtocolRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsClassBoundProtocolRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (protocolDecl.IsClassBound)
            return true;

        // Check GenericSignature for NSObjectProtocol/AnyObject constraints.
        // ObjC protocols often declare constraints like "<τ_0_0 : ObjectiveC.NSObjectProtocol>"
        // in genericSig instead of listing NSObjectProtocol in inheritedProtocols.
        if (!string.IsNullOrEmpty(protocolDecl.GenericSignature))
        {
            if (protocolDecl.GenericSignature.Contains("NSObjectProtocol") ||
                protocolDecl.GenericSignature.Contains("AnyObject"))
                return true;
        }

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            if (simpleName is "NSObjectProtocol" or "AnyObject")
                return true;

            // Intra-module transitive check
            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && IsClassBoundProtocolRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a protocol is or inherits from CaseIterable, directly or transitively.
    /// </summary>
    internal static bool InheritsCaseIterable(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return InheritsCaseIterableRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool InheritsCaseIterableRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (protocolDecl.Name == "CaseIterable")
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            if (simpleName == "CaseIterable")
                return true;

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && InheritsCaseIterableRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a protocol inherits (directly or transitively) from a standard library
    /// protocol that has requirements EveryProtocol can't satisfy. These protocols have
    /// property or initializer requirements that aren't included in the vtable.
    /// </summary>
    internal static bool InheritsUnsatisfiedStdlibProtocol(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return InheritsUnsatisfiedStdlibProtocolRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Set of stdlib protocol names whose requirements EveryProtocol can't implement.
    /// These protocols require properties (description), initializers (init(from:)),
    /// or static members that can't be provided via the vtable callback pattern.
    /// Note: Codable (Decodable/Encodable) and Error are handled separately via Codable stubs.
    /// </summary>
    private static readonly HashSet<string> s_unsatisfiedStdlibProtocols = new(StringComparer.Ordinal)
    {
        "CustomStringConvertible",
        "CustomDebugStringConvertible",
        "LosslessStringConvertible",
        "CodingKey",
        "RawRepresentable",
        "ExpressibleByStringLiteral",
        "ExpressibleByIntegerLiteral",
        "ExpressibleByFloatLiteral",
        "ExpressibleByBooleanLiteral",
        "ExpressibleByNilLiteral",
        "ExpressibleByArrayLiteral",
        "ExpressibleByDictionaryLiteral",
        "ExpressibleByStringInterpolation",
        "ExpressibleByUnicodeScalarLiteral",
        "ExpressibleByExtendedGraphemeClusterLiteral",
        "Strideable",
        "AdditiveArithmetic",
        "Numeric",
        "IteratorProtocol",
    };

    private static bool InheritsUnsatisfiedStdlibProtocolRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (s_unsatisfiedStdlibProtocols.Contains(protocolDecl.Name))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            if (s_unsatisfiedStdlibProtocols.Contains(simpleName))
                return true;

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && InheritsUnsatisfiedStdlibProtocolRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the simple (unqualified) name from a potentially module-qualified type name.
    /// </summary>
    private static string GetSimpleName(string name)
    {
        var dotIndex = name.LastIndexOf('.');
        return dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;
    }

    #endregion
}
