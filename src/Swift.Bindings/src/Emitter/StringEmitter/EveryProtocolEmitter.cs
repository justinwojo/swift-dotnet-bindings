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

    public EveryProtocolEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
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

        // Property getters and setters
        foreach (var property in protocolDecl.Properties)
        {
            EmitPropertyVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript getters and setters
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            EmitSubscriptVtableFields(writer, subscript, protocolDecl, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Methods - track by signature to handle overloads correctly
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            // Skip constructors and static methods
            if (method.IsConstructor || method.MethodType == MethodType.Static)
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
    private void EmitProtocolExtension(SwiftWriter writer, ProtocolDecl protocolDecl, HashSet<string>? globalEmittedSignatures)
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

        // Emit property implementations
        foreach (var property in protocolDecl.Properties)
        {
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

        // Emit subscript implementations
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
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
            // Skip constructors and static methods
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            var swiftSignature = GetSwiftMethodSignature(method);

            // Check for global conflicts (method name + parameter count defines the signature)
            if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(swiftSignature))
            {
                _logger.LogDebug($"Skipping method '{method.Name}' in {protocolDecl.Name}: conflicts with already-emitted method");
                continue;
            }

            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Only emit method implementation for new methods (not duplicates)
                EmitMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a Swift method signature string for conflict detection.
    /// </summary>
    private string GetSwiftMethodSignature(MethodDecl method)
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
        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple type erasure to Any
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has Self requirement");
            return;
        }

        // Skip protocols with no implementable members
        var hasImplementableMembers = protocolDecl.Properties.Any() ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any();
        if (!hasImplementableMembers)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: no implementable members");
            return;
        }

        EmitProtocolVtableStruct(writer, protocolDecl);
        EmitProtocolExtension(writer, protocolDecl, globalEmittedSignatures);
        EmitSetVtableFunction(writer, protocolDecl);
        EmitWitnessTableGetter(writer, protocolDecl);
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

    private void EmitMethodImplementation(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string vtableInstanceName, int index)
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

            // Swift parameter format: "externalLabel internalName: Type" or "_ internalName: Type"
            if (externalLabel == "_")
            {
                parameters.Add($"_ {internalName}: {paramTypeName}");
            }
            else if (externalLabel == internalName)
            {
                // Same label and name - just use one
                parameters.Add($"{internalName}: {paramTypeName}");
            }
            else
            {
                parameters.Add($"{externalLabel} {internalName}: {paramTypeName}");
            }
        }
        var parametersString = string.Join(", ", parameters);

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetSwiftTypeName(returnType!) : "Void";
        var returnTypeNameForMetatype = hasReturn ? GetSwiftTypeNameForMetatype(returnType!) : "Void";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        var fieldName = GetMethodVtableFieldName(method, index);

        writer.WriteLine($"public func {method.Name}({parametersString}){returnDecl} {{");
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

        if (hasReturn)
        {
            writer.WriteLines($$"""
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}})
                    return resultPtr.assumingMemoryBound(to: {{returnTypeNameForMetatype}}.self).pointee
                """);
        }
        else
        {
            writer.WriteLines($$"""
                    var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                    {{argPassCode}}{{vtableInstanceName}}.{{fieldName}}!(
                        {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}})
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private string GetSwiftTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "Any";

        // Handle ProtocolListTypeSpec (protocol composition types)
        // An empty protocol list represents "Any" in Swift
        if (typeSpec is ProtocolListTypeSpec protocolList)
        {
            if (protocolList.Protocols.Count == 0)
                return "Any";
            if (protocolList.Protocols.Count == 1)
                return $"any {protocolList.Protocols.Keys.First().Name}";
            // Multiple protocols: any P1 & P2 & P3
            var protocolNames = string.Join(" & ", protocolList.Protocols.Keys.Select(p => p.Name));
            return $"any {protocolNames}";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Check for generic type parameters first (τ_0_0, T, Element, etc.)
            // These can't be resolved to concrete types, so use Any
            if (IsGenericTypeParameter(namedType.Name))
                return "Any";

            // Handle metatype patterns like "any Any.Type" or "Any.Type"
            if (namedType.Name == "any Any.Type" || namedType.Name == "Any.Type")
                return "Any.Type";

            // Handle existential types (any Protocol)
            var anyPrefix = namedType.IsAny ? "any " : "";

            // Check if this is a generic type (has generic parameters)
            if (namedType.GenericParameters.Count > 0)
            {
                var typeArgs = string.Join(", ", namedType.GenericParameters.Select(GetSwiftTypeName));

                // Special case for Optional - use ? syntax
                // Note: For optionals, the ? goes after the type name, and any prefix goes on inner type
                if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
                {
                    var innerType = GetSwiftTypeName(namedType.GenericParameters[0]);
                    return $"({innerType})?";
                }

                return $"{anyPrefix}{namedType.Name}<{typeArgs}>";
            }
            return $"{anyPrefix}{namedType.Name}";
        }

        if (typeSpec is TupleTypeSpec tupleType)
        {
            if (tupleType.IsEmptyTuple)
                return "Void";
            var elements = string.Join(", ", tupleType.Elements.Select(GetSwiftTypeName));
            return $"({elements})";
        }

        if (typeSpec is ClosureTypeSpec closureType)
        {
            // Build closure type string: (Args) -> Return or (Args) throws -> Return
            var argsString = GetSwiftTypeName(closureType.Arguments);
            // Ensure args are wrapped in parentheses
            if (closureType.Arguments is not TupleTypeSpec)
            {
                argsString = $"({argsString})";
            }
            var returnString = GetSwiftTypeName(closureType.ReturnType);
            if (closureType.ReturnType.IsEmptyTuple)
            {
                returnString = "Void";
            }

            var throwsKeyword = closureType.Throws ? " throws" : "";
            var asyncKeyword = closureType.IsAsync ? " async" : "";

            // Build: @escaping (Args) throws -> Return
            var attributes = closureType.IsEscaping ? "@escaping " : "";
            return $"{attributes}{argsString}{asyncKeyword}{throwsKeyword} -> {returnString}";
        }

        return typeSpec.ToString() ?? "Any";
    }

    /// <summary>
    /// Checks if a type name represents a generic type parameter.
    /// Swift generic type parameters appear as τ_0_0, τ_0_1, etc., or as simple names like T, U, Element.
    /// Delegates to the shared TypeSpecHelpers.IsGenericTypeParameter method.
    /// </summary>
    private static bool IsGenericTypeParameter(string typeName) =>
        TypeSpecHelpers.IsGenericTypeParameter(typeName);

    /// <summary>
    /// Gets the Swift type name suitable for use with .self metatype access.
    /// Wraps existential types (any Protocol) in parentheses since Swift requires
    /// (any Protocol).self instead of any Protocol.self.
    /// </summary>
    private string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec)
    {
        var typeName = GetSwiftTypeName(typeSpec);
        // If the type starts with "any ", it needs to be wrapped in parentheses for .self access
        if (typeName.StartsWith("any ") || typeName.StartsWith("(any "))
        {
            // Wrap in parentheses if not already
            if (!typeName.StartsWith("("))
                return $"({typeName})";
        }
        return typeName;
    }

    private string BuildArgumentPassList(IReadOnlyList<ArgumentDecl> parameters)
    {
        var lines = new List<string>();
        foreach (var param in parameters)
        {
            var paramName = string.IsNullOrEmpty(param.Name) ? "index" : param.Name;
            lines.Add($"var {paramName}Copy = {paramName}");
        }
        return lines.Count > 0 ? string.Join("\n        ", lines) : "";
    }

    private string BuildArgRefs(IReadOnlyList<ArgumentDecl> parameters)
    {
        var refs = new List<string>();
        foreach (var param in parameters)
        {
            var paramName = string.IsNullOrEmpty(param.Name) ? "index" : param.Name;
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
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || IsGeneratedArgName(param.Name))
        {
            return "_";
        }
        // Strip the underscore prefix added by ExtractUniqueName for C# keywords
        return StripCSharpKeywordPrefix(param.Name);
    }

    /// <summary>
    /// Gets the internal parameter name used in the implementation.
    /// </summary>
    private static string GetSwiftParameterName(ArgumentDecl param, int index)
    {
        // Use private name if available
        if (!string.IsNullOrEmpty(param.PrivateName))
        {
            return param.PrivateName;
        }
        // If name looks like a generated "argN", keep using it as internal name
        if (IsGeneratedArgName(param.Name))
        {
            return param.Name;
        }
        // Otherwise use the public name or generate one
        if (!string.IsNullOrEmpty(param.Name) && param.Name != "_")
        {
            // Strip C# keyword prefix for Swift
            var swiftName = StripCSharpKeywordPrefix(param.Name);
            // If the name is a Swift keyword, use a modified internal name
            // to avoid conflicts (Swift allows keyword names with backticks, but
            // for simplicity we'll use a suffix for the internal name)
            if (IsSwiftKeyword(swiftName))
            {
                return $"{swiftName}Value"; // Use suffix for Swift keywords
            }
            return swiftName;
        }
        return $"arg{index}";
    }

    /// <summary>
    /// Checks if a parameter name was auto-generated (arg0, arg1, etc.)
    /// These are created by the parser when Swift has "_" (no external label).
    /// </summary>
    private static bool IsGeneratedArgName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("arg"))
            return false;
        return name.Length > 3 && name.Substring(3).All(char.IsDigit);
    }

    /// <summary>
    /// Strips the underscore prefix added by the parser for C# keywords.
    /// e.g., "_for" -> "for", "_in" -> "in"
    /// </summary>
    private static string StripCSharpKeywordPrefix(string name)
    {
        if (name.Length > 1 && name[0] == '_')
        {
            var possibleKeyword = name.Substring(1);
            // Common C# keywords that might appear in Swift parameter names
            var csharpKeywords = new HashSet<string>
            {
                "for", "in", "is", "as", "if", "else", "do", "while", "return",
                "break", "continue", "switch", "case", "default", "try", "catch",
                "throw", "new", "this", "base", "null", "true", "false", "class",
                "struct", "enum", "interface", "public", "private", "protected",
                "internal", "static", "readonly", "const", "override", "virtual",
                "abstract", "sealed", "async", "await", "var", "object", "string",
                "int", "long", "float", "double", "bool", "void", "ref", "out",
                "params", "event", "delegate", "operator", "implicit", "explicit",
                "where", "get", "set", "value", "partial", "using", "namespace"
            };
            if (csharpKeywords.Contains(possibleKeyword))
            {
                return possibleKeyword;
            }
        }
        return name;
    }

    /// <summary>
    /// Checks if a name is a Swift keyword that needs backtick escaping.
    /// </summary>
    private static bool IsSwiftKeyword(string name)
    {
        var swiftKeywords = new HashSet<string>
        {
            "as", "break", "case", "catch", "class", "continue", "default", "defer",
            "do", "else", "enum", "extension", "fallthrough", "false", "for", "func",
            "guard", "if", "import", "in", "init", "inout", "internal", "is", "let",
            "nil", "operator", "private", "protocol", "public", "repeat", "rethrows",
            "return", "self", "Self", "static", "struct", "subscript", "super",
            "switch", "throw", "throws", "true", "try", "typealias", "var", "where", "while"
        };
        return swiftKeywords.Contains(name);
    }

    #endregion
}
