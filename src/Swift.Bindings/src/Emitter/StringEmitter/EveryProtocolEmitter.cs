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

    /// <summary>
    /// Tracks protocols whose EveryProtocol conformance was skipped.
    /// Used to detect genericSig constraints that reference unsatisfied protocols.
    /// </summary>
    private readonly HashSet<string> _skippedProtocols = new(StringComparer.Ordinal);

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

                // Deinit callback fired when Swift's last retain drops. The C# proxy
                // registers this so the SwiftObjectRegistry strong root and the
                // ProxyLifetimeTracker entry can be torn down when Swift is finished
                // with the existential container. Storage is fileprivate to prevent
                // accidental access from outside this module.
                fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
                fileprivate var onDeinitCtx: UnsafeRawPointer?

                public init() {
                    self.handle = nil
                }

                public init(handle: UnsafeRawPointer) {
                    self.handle = handle
                }

                deinit {
                    // Idempotent, non-throwing. Runs when Swift's retain count reaches 0.
                    // The C# callback is responsible for short-circuiting on process exit.
                    if let cb = onDeinit, let ctx = onDeinitCtx {
                        cb(ctx)
                    }
                }
            }

            // Creates a real Swift EveryProtocol instance (retained +1).
            // C# proxy code calls this instead of raw NativeMemory.Alloc to ensure the
            // existential container payload is a valid ARC-managed Swift object.
            @_cdecl("SBW_CreateEveryProtocol")
            public func _sbw_createEveryProtocol() -> UnsafeMutableRawPointer {
                let instance = EveryProtocol()
                return Unmanaged.passRetained(instance).toOpaque()
            }

            // Releases an EveryProtocol instance created by SBW_CreateEveryProtocol.
            @_cdecl("SBW_ReleaseEveryProtocol")
            public func _sbw_releaseEveryProtocol(_ ptr: UnsafeMutableRawPointer) {
                Unmanaged<EveryProtocol>.fromOpaque(ptr).release()
            }

            // Returns the Swift type metadata pointer for EveryProtocol.
            // Used by C# proxy classes to populate existential container metadata.
            @_cdecl("SBW_GetMetadata_EveryProtocol")
            public func _sbw_getEveryProtocolMetadata() -> UnsafeRawPointer {
                return unsafeBitCast(EveryProtocol.self, to: UnsafeRawPointer.self)
            }

            // Registers a C# deinit callback on an EveryProtocol instance. The callback
            // fires from Swift's deinit when the instance's retain count reaches 0.
            // Uses takeUnretainedValue — we're only reading a property reference, not
            // adding a ref. The caller (C# proxy ctor) already owns a +1 via
            // SBW_CreateEveryProtocol; takeRetainedValue would incorrectly consume it.
            @_cdecl("SBW_SetEveryProtocolDeinitCallback")
            public func _sbw_setEveryProtocolDeinitCallback(
                _ instance: UnsafeMutableRawPointer,
                _ callback: @convention(c) (UnsafeRawPointer) -> Void,
                _ context: UnsafeRawPointer
            ) {
                let ep = Unmanaged<EveryProtocol>.fromOpaque(instance).takeUnretainedValue()
                ep.onDeinit = callback
                ep.onDeinitCtx = context
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

        // Detect mixed-generic protocols (both method-level generic and non-generic instance members).
        // ALL members get fatalError() stubs — no vtable fields needed.
        bool isMixedGenericProtocol = IsMixedGenericProtocol(protocolDecl);

        // Property getters and setters (skip static, @objc optional, closure, Self-typed, and mixed-generic properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic || property.IsObjCOptional)
                continue;
            // Skip vtable fields for closure properties — they get fatalError() stubs
            if (HasClosureInPropertyType(property))
                continue;
            // Skip vtable fields for Self-typed properties — they get fatalError() stubs
            if (ContainsSelfTypeParam(property.SwiftTypeSpec))
                continue;
            // Skip vtable fields for mixed-generic protocols — all members get stubs
            if (isMixedGenericProtocol)
                continue;
            EmitPropertyVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript getters and setters (skip static, Self-typed, and mixed-generic subscripts)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            // Skip vtable fields for Self-typed subscripts — they get fatalError() stubs
            if (ContainsSelfTypeParam(subscript.ReturnTypeSpec) ||
                subscript.IndexParameters.Any(ip => ContainsSelfTypeParam(ip.SwiftTypeSpec)))
            {
                subscriptIndex++;
                continue;
            }
            // Skip vtable fields for mixed-generic protocols — all members get stubs
            if (isMixedGenericProtocol)
            {
                subscriptIndex++;
                continue;
            }
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
                // Skip vtable fields for closure methods — they get fatalError() stubs,
                // not vtable dispatch, so the field would be dead code.
                if (HasClosureInMethodSignature(method))
                    continue;
                // Skip vtable fields for method-level generic methods — they get fatalError() stubs
                if (HasOnlyMethodLevelGenerics(method))
                    continue;
                // Skip vtable fields for Self-typed methods — they get fatalError() stubs
                if (HasSelfTypeParamInSignature(method))
                    continue;
                // Skip vtable fields for mixed-generic protocols — all members get stubs
                if (isMixedGenericProtocol)
                    continue;
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
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(writer, availAnnotations);
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

        // Detect protocols with mixed method-level generics and non-generic members.
        // These protocols need ALL members emitted as stubs because the type projection
        // pipeline generates incorrect types for non-generic members when method-level
        // generic parameters are in scope (e.g., RxSwift.SchedulerType.now resolves
        // RxTime→Double instead of Date). Stubs use raw TypeSpec rendering which is correct.
        bool isMixedGenericProtocol = IsMixedGenericProtocol(protocolDecl);

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
                // Closure properties get fatalError() stubs — closure types can't be
                // dispatched through the @convention(c) vtable.
                if (HasClosureInPropertyType(property))
                    EmitClosurePropertyStub(writer, property);
                // Self-typed properties get fatalError() stubs — τ_0_0 can't be dispatched
                // through the vtable. Renders τ_0_0 as EveryProtocol (the conforming type).
                else if (ContainsSelfTypeParam(property.SwiftTypeSpec))
                    EmitSelfTypedPropertyStub(writer, property);
                // Mixed generic protocols: all properties get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                    EmitClosurePropertyStub(writer, property);
                else
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
                // Self-typed subscripts get fatalError() stubs
                if (ContainsSelfTypeParam(subscript.ReturnTypeSpec) ||
                    subscript.IndexParameters.Any(ip => ContainsSelfTypeParam(ip.SwiftTypeSpec)))
                    EmitSelfTypedSubscriptStub(writer, subscript, subscriptIndex);
                // Mixed generic protocols: all subscripts get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                    EmitSelfTypedSubscriptStub(writer, subscript, subscriptIndex);
                else
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
            var fullSignature = GetSwiftMethodFullSignature(method);

            // Check for global conflicts using full signatures (name + parameter types + return type).
            // This allows same-name methods with different parameter types to coexist as Swift overloads
            // (e.g., validate(input: String) and validate(input: Int32) from different protocols).
            //
            // Return-type-only conflicts (e.g., parse(data:)->Int vs parse(data:)->Void) ARE intentionally
            // emitted — they produce invalid Swift, but the wrapper strip/retry mechanism handles this:
            // the duplicate function is stripped, which fails the conformance, which gets stripped on retry.
            // Using call signatures (without return type) here would PREVENT emission, leaving an empty
            // conformance that the strip script can't handle (no function to strip → unrecoverable error).
            if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(fullSignature))
            {
                _logger.LogDebug($"Skipping method '{method.Name}' in {protocolDecl.Name}: conflicts with already-emitted method");
                continue;
            }

            // Only emit method implementation for new methods (not within-protocol duplicates)
            if (isNewMethod)
            {
                // Methods with closure params/return get fatalError() stubs.
                // The closure types can't be dispatched through the @convention(c) vtable.
                // The C# proxy already throws NotSupportedException for these methods.
                if (HasClosureInMethodSignature(method))
                {
                    EmitClosureMethodStub(writer, method);
                }
                // Methods with only method-level generics (τ_1_0+, no Self τ_0_*) get stub
                // implementations. EveryProtocol satisfies the protocol requirement, but can't
                // dispatch through the vtable (C# can't handle method-level generic dispatch).
                else if (HasOnlyMethodLevelGenerics(method))
                {
                    EmitMethodLevelGenericStub(writer, method);
                }
                // Methods with Self-typed (τ_0_*) params/return get fatalError() stubs.
                // Renders τ_0_0 as EveryProtocol (the conforming type) to satisfy Swift's
                // type system — Self IS EveryProtocol in the conformance context.
                else if (HasSelfTypeParamInSignature(method))
                {
                    EmitSelfTypedMethodStub(writer, method);
                }
                // Mixed generic protocols: all methods get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                {
                    EmitClosureMethodStub(writer, method);
                }
                else
                {
                    // If this full signature is in the non-throwing overrides set, suppress throws.
                    // A non-throwing method satisfies both throwing and non-throwing protocol requirements,
                    // but a throwing method does NOT satisfy a non-throwing requirement.
                    // Uses full signature so overloads with different types are tracked independently.
                    var effectiveThrows = method.Throws &&
                        !(nonThrowingOverrides?.Contains(fullSignature) == true);
                    EmitMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, effectiveThrows);
                }
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
    /// Gets a full Swift method signature including parameter types and return type.
    /// Used for global dedup and non-throwing override tracking.
    internal string GetSwiftMethodFullSignature(MethodDecl method)
    {
        var parts = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param, i);
            var typeName = GetSwiftTypeName(param.SwiftTypeSpec);
            parts.Add($"{label}:{typeName}");
        }
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var returnStr = returnType != null && !returnType.IsEmptyTuple ? GetSwiftTypeName(returnType) : "Void";
        return $"{method.Name}({string.Join(",", parts)})->{returnStr}";
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
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        var availPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(availAnnotations, "            ");

        writer.WriteLines($$"""
            // Returns the protocol witness table pointer for EveryProtocol conforming to {{protocolDecl.Name}}.
            // C# calls this via P/Invoke to obtain the witness table for existential container construction.
            {{availPrefix}}@_silgen_name("{{mangledGetterName}}")
            public func {{getterFunctionName}}() -> UnsafeRawPointer {
                let instance = EveryProtocol()
                return withExtendedLifetime(instance) {
                    var proto: any {{protocolName}} = instance
                    return withUnsafeBytes(of: &proto) { buffer in
                        // Witness table is the last pointer-sized word in the existential container.
                        // Layout depends on class-bound vs opaque:
                        //   Opaque:      [payload0] [payload1] [payload2] [metadata] [WT] (5 words)
                        //   Class-bound: [classRef] [WT] (2 words)
                        // Using MemoryLayout<any Protocol>.size - pointer size handles both.
                        let witnessTableOffset = MemoryLayout<any {{protocolName}}>.size - MemoryLayout<Int>.size
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
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        var availPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(availAnnotations, "            ");

        writer.WriteLines($$"""
            // Called by C# to register the protocol vtable
            {{availPrefix}}@_silgen_name("{{mangledSetFunctionName}}")
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
    /// Pre-scans all protocols to populate _skippedProtocols BEFORE any emission.
    /// This makes genericSig constraint checks order-independent: even if ChildProtocol
    /// appears before ParentProtocol in the list, the pre-scan will have already identified
    /// ParentProtocol as unsatisfied if it has static method requirements, etc.
    /// </summary>
    public void PreScanProtocols(IReadOnlyList<ProtocolDecl> protocols)
    {
        // Pass 1: identify protocols that will be skipped by structural gates
        foreach (var protocolDecl in protocols)
        {
            if (WillSkipConformance(protocolDecl))
            {
                _skippedProtocols.Add(protocolDecl.Name);
                if (protocolDecl.SwiftTypeName != null)
                    _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
            }
        }

        // Pass 2: propagate skips through genericSig constraints.
        // Protocols whose genericSig references a skipped protocol must also be skipped.
        // Repeat until no new skips are found (handles transitive chains).
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var protocolDecl in protocols)
            {
                if (_skippedProtocols.Contains(protocolDecl.Name))
                    continue;
                if (HasUnsatisfiedProtocolConstraintInGenericSig(protocolDecl))
                {
                    _skippedProtocols.Add(protocolDecl.Name);
                    if (protocolDecl.SwiftTypeName != null)
                        _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a protocol's EveryProtocol conformance would be skipped by the structural gates.
    /// Does NOT check order-dependent gates (method type conflicts) — those are checked at emission time.
    /// </summary>
    private bool WillSkipConformance(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.HasSelfRequirement)
            return true;

        if (protocolDecl.HasMissingRequirements)
            return true;

        if (protocolDecl.HasConventionCClosureParameters)
            return true;

        // Self-typed members (τ_0_*) and mixed method-level generics no longer skip the
        // entire protocol. Self-typed members get fatalError() stubs with τ_0_0→EveryProtocol,
        // and method-level generic methods get fatalError() stubs alongside normal vtable
        // dispatch for non-generic members.

        if (IsClassBoundProtocol(protocolDecl))
            return true;

        if (InheritsCaseIterable(protocolDecl))
            return true;

        if (ModuleHandler.InheritsProtocolWithAssociatedTypes(protocolDecl))
            return true;

        if (InheritsUnsatisfiedStdlibProtocol(protocolDecl))
            return true;

        if (protocolDecl.Methods.Any(m => m.IsConstructor))
            return true;

        var hasImplementableMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any(s => !s.IsStatic);
        var hasStaticRequirements = protocolDecl.Properties.Any(p => p.IsStatic) ||
                                    protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);
        bool hasNonTrivialInheritance = protocolDecl.InheritedProtocols.Any(inh =>
                inh.NameWithoutModule != "AnyObject" &&
                inh.NameWithoutModule != "Escapable" &&
                inh.NameWithoutModule != "Copyable" &&
                inh.NameWithoutModule != "Sendable" &&
                inh.NameWithoutModule != "SendableMetatype");

        // Empty marker protocols (no members, no inheritance) are allowed — they need
        // a trivial EveryProtocol conformance for existential container creation.
        if (!hasImplementableMembers && !hasStaticRequirements && !hasNonTrivialInheritance)
        {
            // Truly empty marker protocol — don't skip
            if (!protocolDecl.Properties.Any() && !protocolDecl.Methods.Any() && !protocolDecl.Subscripts.Any())
                return false;
            // Has members but none are implementable (all constructors/static) — skip
            return true;
        }

        if (protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static))
            return true;

        return false;
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
        // Helper to record a skip decision and track the protocol for genericSig constraint checks
        void RecordSkip(string reason)
        {
            _skippedProtocols.Add(protocolDecl.Name);
            if (protocolDecl.SwiftTypeName != null)
                _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
            _emissionContext?.RecordConformanceDecision(protocolDecl.Name, false, reason);
        }

        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple type erasure to Any
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has Self requirement");
            RecordSkip("HasSelfRequirement");
            return;
        }

        // Skip protocols with requirements that failed ABI parsing (e.g., methods with
        // `some` parameters cause GenericSignatureParser count mismatch). The emitter
        // cannot generate stubs for requirements it doesn't know about.
        if (protocolDecl.HasMissingRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has requirements that failed ABI parsing");
            RecordSkip("MissingRequirements");
            return;
        }

        // Skip protocols with @convention(c) or @convention(block) closure parameters.
        // ABI JSON doesn't encode calling conventions on TypeFunc nodes, so the closure
        // stub would emit @escaping instead of @convention(c), causing a type mismatch.
        if (protocolDecl.HasConventionCClosureParameters)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has @convention(c)/@convention(block) closure parameters");
            RecordSkip("ConventionCClosureParameters");
            return;
        }

        // Self-typed members (τ_0_*) and method-level generic methods (τ_1_*) get
        // fatalError() stubs in the extension — they can't be dispatched through the vtable.
        // Non-Self, non-generic members get normal vtable dispatch. This allows protocols
        // with a mix of dispatchable and non-dispatchable members to emit partial conformances.

        // Skip protocols that require NSObjectProtocol identity semantics.
        // Pure AnyObject (class-bound) protocols are allowed since EveryProtocol is a class.
        // Only NSObjectProtocol requires NSObject methods (isEqual:, hash, description).
        if (IsClassBoundProtocol(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: requires NSObjectProtocol identity semantics");
            RecordSkip("NSObjectProtocolRequired");
            return;
        }

        // Skip protocols whose genericSig constrains Self (τ_0_0) to conform to a protocol
        // that EveryProtocol can't satisfy — either from a known ObjC module or a previously
        // skipped protocol from the same module.
        if (HasUnsatisfiedProtocolConstraintInGenericSig(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: genericSig constrains Self to unsatisfied protocol");
            RecordSkip("UnsatisfiedProtocolConstraint");
            return;
        }

        // Skip CaseIterable — requires compiler-synthesized `allCases` static property
        // that EveryProtocol can't provide. Checked transitively.
        if (InheritsCaseIterable(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: CaseIterable requires compiler synthesis");
            RecordSkip("CaseIterable");
            return;
        }

        // Skip protocols that inherit from protocols with associated types.
        // EveryProtocol can't provide concrete associated types for inherited PATs.
        if (ModuleHandler.InheritsProtocolWithAssociatedTypes(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits protocol with associated types");
            RecordSkip("InheritedAssociatedTypes");
            return;
        }

        // Skip protocols that inherit from stdlib protocols with requirements
        // EveryProtocol can't satisfy (CustomStringConvertible, CodingKey, etc.).
        if (InheritsUnsatisfiedStdlibProtocol(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits unsatisfied stdlib protocol");
            RecordSkip("UnsatisfiedStdlibProtocol");
            return;
        }

        // Skip protocols with constructor requirements — EveryProtocol can't provide init methods
        // via the vtable callback pattern. The conformance would be incomplete (missing inits).
        if (protocolDecl.Methods.Any(m => m.IsConstructor))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has constructor requirements");
            RecordSkip("ConstructorRequirements");
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
            // Empty marker protocols (no members at all) need a trivial conformance
            // for existential container creation. Let them through to the else branch below.
            bool isEmptyMarker = !protocolDecl.Properties.Any() && !protocolDecl.Methods.Any() && !protocolDecl.Subscripts.Any();
            if (!isEmptyMarker)
            {
                _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: no implementable instance members and no static requirements");
                RecordSkip("NoImplementableMembers");
                return;
            }
        }

        // Skip protocols with static method requirements — static method stubs can't
        // render correct Swift signatures (parameter labels, types, return type).
        // Static properties work with fatalError() but methods need full signatures.
        var hasStaticMethodRequirements = protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);
        if (hasStaticMethodRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has static method requirements (can't generate correct stub signatures)");
            RecordSkip("StaticMethodRequirements");
            return;
        }

        // Note: MethodTypeConflict pre-scan was removed. Methods with the same label signature
        // but different parameter types are valid Swift overloads. The method dedup in
        // EmitProtocolExtension now uses full signatures (name + types) instead of label-only,
        // so methods like validate(input: String) and validate(input: Int32) from different
        // protocols coexist correctly on EveryProtocol.

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
            var staticAvailAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(writer, staticAvailAnnotations);
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

    /// <summary>
    /// Emits a fatalError() stub for a protocol property that has a closure type.
    /// Satisfies the protocol conformance requirement without vtable dispatch.
    /// </summary>
    private void EmitClosurePropertyStub(SwiftWriter writer, PropertyDecl property)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = GetSwiftTypeName(property.SwiftTypeSpec);

        writer.WriteLine($"public var {property.Name}: {swiftTypeName} {{");
        writer.Indent++;
        if (hasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"EveryProtocol: closure property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        if (hasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"EveryProtocol: closure property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol property that contains Self-typed (τ_0_*) references.
    /// Substitutes τ_0_0 with EveryProtocol (the conforming type) so Swift's type system is satisfied.
    /// </summary>
    private void EmitSelfTypedPropertyStub(SwiftWriter writer, PropertyDecl property)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = RenderTypeSpecWithSelfSubstitution(property.SwiftTypeSpec);

        writer.WriteLine($"public var {property.Name}: {swiftTypeName} {{");
        writer.Indent++;
        if (hasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"EveryProtocol: Self-typed property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        if (hasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"EveryProtocol: Self-typed property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
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
            // String returns use Utf8Slice encoding from C# to avoid ARC issues.
            // The C# receiver returns a pointer to SBW_Utf8Slice (ptr + len),
            // and Swift decodes it into a proper String with correct ARC management.
            bool isStringGetter = property.SwiftTypeSpec is NamedTypeSpec getterNts && getterNts.Name == "Swift.String";
            if (isStringGetter)
            {
                writer.WriteLines($$"""
                    get {
                        var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                        let resultPtr = {{vtableInstanceName}}.func_{{property.Name}}_get!(
                            {{vtableInstanceName}}.csVTHandle, &selfProto)
                        let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                        var str: Swift.String = ""
                        if slice.len > 0 {
                            let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                            str = String(decoding: buffer, as: UTF8.self)
                        }
                        slice.ptr.deallocate()
                        resultPtr.deallocate()
                        return str
                    }
                    """);
            }
            else
            {
                bool isObjCBridgeableGetter = IsObjCBridgeableParam(property.SwiftTypeSpec);
                if (isObjCBridgeableGetter)
                {
                    writer.WriteLines($$"""
                        get {
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            let resultPtr = {{vtableInstanceName}}.func_{{property.Name}}_get!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto)
                            let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                            resultPtr.deallocate()
                            return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).takeUnretainedValue() as! {{swiftTypeNameForMetatype}}
                        }
                        """);
                }
                else
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
            }
        }

        if (hasSetter)
        {
            bool isObjCBridgeableSetter = IsObjCBridgeableParam(property.SwiftTypeSpec);
            if (isObjCBridgeableSetter)
            {
                writer.WriteLines($$"""
                    set {
                        var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                        let newValueNS = newValue as AnyObject
                        var newValueRef = Unmanaged.passUnretained(newValueNS).toOpaque()
                        {{vtableInstanceName}}.func_{{property.Name}}_set!(
                            {{vtableInstanceName}}.csVTHandle, &selfProto, &newValueRef)
                    }
                    """);
            }
            else
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

    /// <summary>
    /// Emits a stub implementation for a method with method-level generic parameters (τ_1_0+).
    /// Returns nil for Optional returns, fatalError for non-Optional. This satisfies the Swift
    /// protocol conformance without vtable dispatch (C# can't handle method-level generics).
    /// Uses the raw TypeSpec to preserve generic param references (GetSwiftTypeName resolves them to Any).
    /// </summary>
    /// <summary>
    /// Emits a fatalError() stub for a protocol method that has closure parameters/return.
    /// The method signature is correct (satisfying the protocol conformance), but the body
    /// crashes if called. The C# proxy already throws NotSupportedException for closure methods,
    /// so the fatalError() is a safety net only.
    /// </summary>
    private void EmitClosureMethodStub(SwiftWriter writer, MethodDecl method)
    {
        // Build generic param name mapping for method-level generics (τ_1_0 → _G0, etc.)
        // Closure methods can also have method-level generics — we need both in the stub.
        var genericNameMap = new Dictionary<string, string>();
        int genericIdx = 0;
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true)
                continue; // Skip depth-0 (Self)
            var safeName = $"_G{genericIdx++}";
            if (gp.TypeName != null) genericNameMap[gp.TypeName] = safeName;
        }

        // Build generic clause with constraints (e.g., <_G0: Decodable>)
        var genericParts = new List<string>();
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true) continue;
            if (!genericNameMap.TryGetValue(gp.TypeName ?? "", out var safeName)) continue;
            if (gp.GenericConformances.Count > 0)
            {
                var constraints = string.Join(" & ", gp.GenericConformances
                    .Select(c => c.ConformanceTarget.Name));
                genericParts.Add($"{safeName}: {constraints}");
            }
            else
            {
                genericParts.Add(safeName);
            }
        }
        var genericClause = genericParts.Count > 0
            ? $"<{string.Join(", ", genericParts)}>"
            : "";

        // Render TypeSpec, substituting generic param names.
        // suppressEscaping: true when inside Optional — Optional closures are always escaping in Swift,
        // so @escaping on Optional<Closure> is invalid syntax.
        string RenderTypeSpec(TypeSpec? ts, bool suppressEscaping = false)
        {
            if (ts == null) return "Any";
            if (ts is NamedTypeSpec named)
            {
                // Direct generic param match: τ_1_0 → _G0
                if (genericNameMap.TryGetValue(named.Name, out var safeName))
                    return safeName;
                // Metatype of generic param: τ_1_0.Type → _G0.Type
                foreach (var (tauName, gName) in genericNameMap)
                {
                    if (named.Name.StartsWith(tauName + "."))
                        return gName + named.Name.Substring(tauName.Length);
                }
                // Self-typed (depth-0) generic params: τ_0_0 → EveryProtocol
                // Dependent member types (τ_0_0.RowDecoder) → Any (associated type erasure)
                // Metatype (τ_0_0.Type) → EveryProtocol.Type
                if (named.Name.StartsWith("τ_0_"))
                {
                    var dotIdx = named.Name.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var suffix = named.Name.Substring(dotIdx);
                        // .Type is metatype, not an associated type
                        if (suffix == ".Type")
                            return "EveryProtocol.Type";
                        // Dependent member types (associated types) → Any
                        return "Any";
                    }
                    return "EveryProtocol";
                }
                if (!TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                {
                    if (named.ContainsGenericParameters && named.GenericParameters.Count > 0)
                    {
                        bool isOptional = named.Name == "Swift.Optional";
                        var renderedParams = string.Join(", ", named.GenericParameters
                            .Select(p => RenderTypeSpec(p, suppressEscaping: isOptional)));
                        return $"{named.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(ts);
                }
                return "Any"; // Fallback for unrecognized generic params
            }
            if (ts is ClosureTypeSpec closure)
            {
                // Render closure arguments: unwrap tuple elements to avoid double-wrapping.
                // A closure (A, B, C) -> D has Arguments as TupleTypeSpec{A, B, C}.
                // If we render the tuple as "(A, B, C)" then wrap in closure parens, we get "((A, B, C))".
                // Instead, render elements directly and let the closure format add the parens.
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpec(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpec(closure.Arguments);
                var ret = RenderTypeSpec(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping")
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";
            }
            if (ts is TupleTypeSpec tuple)
            {
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpec(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";
            }
            if (ts is AssociatedTypeReferenceSpec assocRef)
            {
                // Dependent member types on Self (τ_0_0.RowDecoder) → Any
                // Method-level generic params (_G0.Element) → Any (unconstrained)
                if (assocRef.BaseType.StartsWith("τ_0_") || genericNameMap.ContainsKey(assocRef.BaseType))
                    return "Any";
                return GetSwiftTypeName(ts);
            }
            if (ts.IsEmptyTuple) return "()";
            return GetSwiftTypeName(ts);
        }

        // Build parameter list with proper Swift labeling
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            // RenderTypeSpec already handles @escaping for direct closures and
            // suppresses it for Optional<Closure> (always escaping in Swift).
            var paramTypeName = RenderTypeSpec(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";

            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpec(returnType!) : "Void";
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {method.Name}{genericClause}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"EveryProtocol: closure method '{method.Name}' cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodLevelGenericStub(SwiftWriter writer, MethodDecl method)
    {
        // Build generic param name mapping: τ_1_0 → _G0, τ_1_1 → _G1, etc.
        // Filter out depth-0 params (Self).
        var genericNameMap = new Dictionary<string, string>();
        int genericIdx = 0;
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true)
                continue;
            var safeName = $"_G{genericIdx++}";
            if (gp.TypeName != null) genericNameMap[gp.TypeName] = safeName;
        }
        // Build generic clause with constraints (e.g., <_G0: Decodable>)
        var genericParts = new List<string>();
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true) continue;
            if (!genericNameMap.TryGetValue(gp.TypeName ?? "", out var safeName)) continue;
            if (gp.GenericConformances.Count > 0)
            {
                var constraints = string.Join(" & ", gp.GenericConformances
                    .Select(c => c.ConformanceTarget.Name));
                genericParts.Add($"{safeName}: {constraints}");
            }
            else
            {
                genericParts.Add(safeName);
            }
        }
        var genericClause = genericParts.Count > 0
            ? $"<{string.Join(", ", genericParts)}>"
            : "";

        // Render TypeSpec preserving generic params (replacing τ_1_0 → _G0, etc.)
        // suppressEscaping: true when inside Optional — Optional closures are always escaping in Swift.
        string RenderTypeSpec(TypeSpec? ts, bool suppressEscaping = false)
        {
            if (ts == null) return "Any";
            if (ts is NamedTypeSpec named)
            {
                // Direct generic param match: τ_1_0 → _G0
                if (genericNameMap.TryGetValue(named.Name, out var safeName))
                    return safeName;
                // Metatype of generic param: τ_1_0.Type → _G0.Type
                foreach (var (tauName, gName) in genericNameMap)
                {
                    if (named.Name.StartsWith(tauName + "."))
                        return gName + named.Name.Substring(tauName.Length);
                }
                // Self-typed (depth-0) generic params: τ_0_0 → EveryProtocol
                // Dependent member types (τ_0_0.RowDecoder) → Any (associated type erasure)
                // Metatype (τ_0_0.Type) → EveryProtocol.Type
                if (named.Name.StartsWith("τ_0_"))
                {
                    var dotIdx = named.Name.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var suffix = named.Name.Substring(dotIdx);
                        if (suffix == ".Type")
                            return "EveryProtocol.Type";
                        return "Any";
                    }
                    return "EveryProtocol";
                }
                // Non-generic types: use standard renderer
                if (!TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                {
                    if (named.ContainsGenericParameters && named.GenericParameters.Count > 0)
                    {
                        bool isOptional = named.Name == "Swift.Optional";
                        // Render generic params recursively (e.g., ServiceEntry<τ_1_1> → ServiceEntry<_G1>)
                        var renderedParams = string.Join(", ", named.GenericParameters
                            .Select(p => RenderTypeSpec(p, suppressEscaping: isOptional)));
                        return $"{named.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(ts);
                }
                return "Any"; // Fallback for unrecognized generic params
            }
            if (ts is ClosureTypeSpec closure)
            {
                // Unwrap tuple arguments to avoid double-wrapping (see closure stub RenderTypeSpec).
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpec(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpec(closure.Arguments);
                var ret = RenderTypeSpec(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping") // already handled above
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";
            }
            if (ts is TupleTypeSpec tuple)
            {
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpec(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";
            }
            if (ts is AssociatedTypeReferenceSpec assocRef)
            {
                // Dependent member types on Self (τ_0_0.RowDecoder) → Any
                // Method-level generic params (_G0.Element) → Any (unconstrained)
                if (assocRef.BaseType.StartsWith("τ_0_") || genericNameMap.ContainsKey(assocRef.BaseType))
                    return "Any";
                return GetSwiftTypeName(ts);
            }
            if (ts.IsEmptyTuple) return "()";
            return GetSwiftTypeName(ts);
        }

        // Build parameter list using raw TypeSpec
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpec(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpec(returnType!) : "Void";
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";
        bool isOptionalReturn = hasReturn && returnType is NamedTypeSpec nts &&
            nts.Name == "Swift.Optional";

        writer.WriteLine($"public func {method.Name}{genericClause}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;

        if (!hasReturn)
            writer.WriteLine("// Method-level generic stub: no-op for Void return");
        else if (isOptionalReturn)
            writer.WriteLine("return nil // Method-level generic stub: can't dispatch through vtable");
        else if (method.Throws)
        {
            writer.WriteLine("// Method-level generic stub: throws error — can't dispatch through vtable");
            writer.WriteLine($"throw NSError(domain: \"SwiftBindings\", code: -1, userInfo: [NSLocalizedDescriptionKey: \"Protocol method with generic parameters is not supported\"])");
        }
        else
            writer.WriteLine($"fatalError(\"EveryProtocol: method-level generic method '{method.Name}' cannot be dispatched through vtable\")");

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol method that contains Self-typed (τ_0_*) references
    /// in its parameters or return type. Substitutes τ_0_0 with EveryProtocol so the Swift
    /// conformance compiles — Self IS EveryProtocol in the conformance context.
    /// </summary>
    private void EmitSelfTypedMethodStub(SwiftWriter writer, MethodDecl method)
    {
        // Build parameter list using Self-substituted type rendering
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecWithSelfSubstitution(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecWithSelfSubstitution(returnType!) : "Void";
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {method.Name}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"EveryProtocol: Self-typed method '{method.Name}' cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol subscript that contains Self-typed (τ_0_*) references.
    /// </summary>
    private void EmitSelfTypedSubscriptStub(SwiftWriter writer, SubscriptDecl subscript, int index)
    {
        var parameters = new List<string>();
        foreach (var param in subscript.IndexParameters)
        {
            var typeName = RenderTypeSpecWithSelfSubstitution(param.SwiftTypeSpec);
            var paramName = string.IsNullOrEmpty(param.Name) ? "index" : param.Name;
            parameters.Add($"{paramName}: {typeName}");
        }

        var returnTypeName = RenderTypeSpecWithSelfSubstitution(subscript.ReturnTypeSpec);

        writer.WriteLine($"public subscript({string.Join(", ", parameters)}) -> {returnTypeName} {{");
        writer.Indent++;
        if (subscript.HasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"EveryProtocol: Self-typed subscript cannot be dispatched through vtable\") }}");
        }
        if (subscript.HasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"EveryProtocol: Self-typed subscript cannot be dispatched through vtable\") }}");
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
        // ObjC-bridgeable types (e.g., URL, URLRequest) need special handling:
        // bridge to AnyObject and pass the ObjC pointer instead of the Swift struct bytes.
        // The C# side uses GetNSObject<T>() which expects a valid ObjC pointer.
        var argPassList = new List<string>();
        var argRefList = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var paramName = internalNames[i];
            var param = method.CSSignature[i + 1]; // +1 to skip return type
            bool isObjCBridgeable = IsObjCBridgeableParam(param.SwiftTypeSpec);
            if (isObjCBridgeable)
            {
                // Bridge Swift value type → ObjC object, pass pointer to the opaque reference.
                // C# MarshalFromSwift<IntPtr> reads the 8-byte pointer, then GetNSObject<T> resolves it.
                var escapedParam = NameProvider.EscapeSwiftKeyword(paramName);
                argPassList.Add($"let {paramName}NS = {escapedParam} as AnyObject");
                argPassList.Add($"var {paramName}Ref = Unmanaged.passUnretained({paramName}NS).toOpaque()");
                argRefList.Add($"&{paramName}Ref");
            }
            else
            {
                argPassList.Add($"var {paramName}Copy = {NameProvider.EscapeSwiftKeyword(paramName)}");
                argRefList.Add($"&{paramName}Copy");
            }
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
                writebackLines.Add($"{NameProvider.EscapeSwiftKeyword(internalNames[i])} = {internalNames[i]}Copy");
            }
        }
        var writebackCode = writebackLines.Count > 0 ? "\n        " + string.Join("\n        ", writebackLines) : "";

        if (hasReturn)
        {
            // String returns use Utf8Slice encoding from C# to avoid ARC issues
            bool isStringMethodReturn = returnType is NamedTypeSpec retNts && retNts.Name == "Swift.String";
            if (isStringMethodReturn)
            {
                writer.WriteLines($$"""
                        var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                        {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                            {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                        let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                        var str: Swift.String = ""
                        if slice.len > 0 {
                            let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                            str = String(decoding: buffer, as: UTF8.self)
                        }
                        slice.ptr.deallocate()
                        resultPtr.deallocate()
                        return str
                    """);
            }
            else
            {
                // ObjC-bridgeable return types (e.g., URL): C# writes an ObjC pointer via
                // MarshalToSwiftBuffer(result.Handle). We read the pointer and bridge back to
                // the Swift value type via Unmanaged<AnyObject>.fromOpaque().
                bool isObjCBridgeableReturn = returnType != null && IsObjCBridgeableParam(returnType);
                if (isObjCBridgeableReturn)
                {
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                            resultPtr.deallocate()
                            return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).takeUnretainedValue() as! {{returnTypeNameForMetatype}}
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            return resultPtr.assumingMemoryBound(to: {{returnTypeNameForMetatype}}.self).pointee
                        """);
                }
            }
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
    /// Checks if a method has closure types (ClosureTypeSpec) in any parameter or return type.
    /// Methods with closure types can't be dispatched through the EveryProtocol vtable
    /// because closures aren't representable as UnsafeRawPointer in @convention(c) callbacks.
    /// These methods get fatalError() stubs to satisfy the protocol conformance.
    /// </summary>
    private static bool HasClosureInMethodSignature(MethodDecl method)
    {
        // Check return type (CSSignature[0])
        if (method.CSSignature.Count > 0 && ContainsClosureType(method.CSSignature[0].SwiftTypeSpec))
            return true;

        // Check non-self parameters (skip return type at index 0)
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsClosureType(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a ClosureTypeSpec.
    /// </summary>
    private static bool ContainsClosureType(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case ClosureTypeSpec:
                return true;

            case NamedTypeSpec namedType:
                return namedType.GenericParameters.Any(ContainsClosureType);

            case TupleTypeSpec tupleType:
                return tupleType.Elements.Any(e => ContainsClosureType(e));

            case ProtocolListTypeSpec protocolListType:
                return protocolListType.Protocols.Keys.Any(ContainsClosureType);

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if a property has closure types in its type spec.
    /// </summary>
    private static bool HasClosureInPropertyType(PropertyDecl property)
    {
        return ContainsClosureType(property.SwiftTypeSpec);
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
    /// Checks if a method has ONLY method-level generic parameters (τ_1_0+) but NO
    /// protocol-level Self type params (τ_0_*). Methods like resolve&lt;Service&gt;() have
    /// method-level generics that EveryProtocol can satisfy with stub implementations,
    /// unlike Self-typed methods which can't be properly dispatched.
    /// </summary>
    internal static bool HasOnlyMethodLevelGenerics(MethodDecl method)
    {
        return HasGenericTypeParamInSignature(method) && !HasSelfTypeParamInSignature(method);
    }

    /// <summary>
    /// Detects protocols with both method-level generic and non-generic instance members.
    /// These need ALL members emitted as stubs because the type projection pipeline generates
    /// incorrect types for non-generic members when method-level generic parameters are in scope.
    /// </summary>
    internal static bool IsMixedGenericProtocol(ProtocolDecl protocolDecl)
    {
        return protocolDecl.Methods
            .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
            .Any(m => HasOnlyMethodLevelGenerics(m)) &&
            (protocolDecl.Properties.Any(p => !p.IsStatic) ||
             protocolDecl.Subscripts.Any(s => !s.IsStatic) ||
             protocolDecl.Methods
                .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
                .Any(m => !HasOnlyMethodLevelGenerics(m)));
    }

    /// <summary>
    /// Checks if a method has protocol-level (Self/depth-0) generic type params in its signature.
    /// Returns false for method-level generics (τ_1_0+) which are independent of the conforming type.
    /// </summary>
    private static bool HasSelfTypeParamInSignature(MethodDecl method)
    {
        if (method.CSSignature.Count > 0 && ContainsSelfTypeParam(method.CSSignature[0].SwiftTypeSpec))
            return true;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsSelfTypeParam(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a protocol-level (Self/depth-0) generic type param.
    /// </summary>
    private static bool ContainsSelfTypeParam(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (TypeSpecHelpers.IsProtocolLevelGenericParam(namedType.Name))
                    return true;
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ContainsSelfTypeParam(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                return tupleType.Elements.Any(e => ContainsSelfTypeParam(e));

            case ClosureTypeSpec closureType:
                return ContainsSelfTypeParam(closureType.Arguments) ||
                       ContainsSelfTypeParam(closureType.ReturnType);

            case ProtocolListTypeSpec protocolListType:
                return protocolListType.Protocols.Keys.Any(p => ContainsSelfTypeParam(p));

            case AssociatedTypeReferenceSpec assocType:
                return TypeSpecHelpers.IsProtocolLevelGenericParam(assocType.BaseType)
                    || assocType.BaseType == "Self";

            default:
                return false;
        }
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

    /// <summary>
    /// Checks if a parameter's Swift type is ObjC-bridgeable (e.g., URL, URLRequest).
    /// ObjC-bridgeable types need special vtable marshalling: bridge to AnyObject and pass
    /// the ObjC pointer instead of the raw Swift struct bytes.
    /// </summary>
    private bool IsObjCBridgeableParam(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;
        if (!_typeDatabase.TryGetTypeRecord(named, out var record))
            return false;
        return MarshallingHelpers.IsObjCBridgeable(record);
    }

    private string GetSwiftTypeName(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeName(typeSpec);

    private string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(typeSpec);

    /// <summary>
    /// Renders a TypeSpec to a Swift type string, substituting protocol-level generic params
    /// (τ_0_0, τ_0_1, etc.) with EveryProtocol. Used by Self-typed member stubs so the
    /// conformance compiles — in the extension context, Self IS EveryProtocol.
    /// </summary>
    private string RenderTypeSpecWithSelfSubstitution(TypeSpec? typeSpec, bool suppressEscaping = false)
    {
        if (typeSpec == null)
            return "Any";

        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assocRef:
                // Dependent member types on Self (τ_0_0.RowDecoder) → Any
                if (assocRef.BaseType.StartsWith("τ_0_"))
                    return "Any";
                return GetSwiftTypeName(typeSpec);

            case NamedTypeSpec namedType:
                // τ_0_0 (Self) → EveryProtocol
                if (TypeSpecHelpers.IsProtocolLevelGenericParam(namedType.Name))
                    return "EveryProtocol";
                // Metatype of Self: τ_0_0.Type → EveryProtocol.Type
                // Associated types: τ_0_0.SomeName → Any (EveryProtocol doesn't have associated types)
                if (namedType.Name.StartsWith("τ_0_") && namedType.Name.Contains('.'))
                {
                    var suffix = namedType.Name.Substring(namedType.Name.IndexOf('.'));
                    if (suffix == ".Type")
                        return "EveryProtocol.Type";
                    return "Any";
                }
                // Non-generic types: recurse into generic params
                if (!TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                {
                    if (namedType.ContainsGenericParameters && namedType.GenericParameters.Count > 0)
                    {
                        bool isOptional = namedType.Name == "Swift.Optional";
                        var renderedParams = string.Join(", ", namedType.GenericParameters
                            .Select(p => RenderTypeSpecWithSelfSubstitution(p, suppressEscaping: isOptional)));
                        return $"{namedType.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(typeSpec);
                }
                return "Any"; // Fallback for other generic params

            case ClosureTypeSpec closure:
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpecWithSelfSubstitution(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpecWithSelfSubstitution(closure.Arguments);
                var ret = RenderTypeSpecWithSelfSubstitution(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping")
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";

            case TupleTypeSpec tuple:
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpecWithSelfSubstitution(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";

            default:
                if (typeSpec.IsEmptyTuple) return "()";
                return GetSwiftTypeName(typeSpec);
        }
    }

    private string BuildArgumentPassList(IReadOnlyList<ArgumentDecl> parameters)
    {
        var lines = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var paramName = string.IsNullOrEmpty(param.Name) || param.Name == "_" ? $"arg{i}" : param.Name;
            lines.Add($"var {paramName}Copy = {NameProvider.EscapeSwiftKeyword(paramName)}");
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
        // Create a unique key for method overloading based on name, argument labels, and parameter types.
        // Argument labels are needed to distinguish Swift overloads like:
        //   pageViewController(_:viewControllerBeforeViewController:)
        //   pageViewController(_:viewControllerAfterViewController:)
        // which have the same name and parameter types but different labels.
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p =>
            (p.GetSwiftName() ?? p.Name) + ":" + (p.SwiftTypeSpec?.ToString() ?? ""))) + ")";
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
    /// Checks if a protocol requires NSObjectProtocol identity semantics that EveryProtocol can't provide.
    /// Pure AnyObject (class-bound) protocols are allowed — EveryProtocol is a Swift class and
    /// satisfies the AnyObject constraint. Only NSObjectProtocol requires NSObject methods
    /// (isEqual:, hash, description, etc.) that EveryProtocol doesn't implement.
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

        // Note: protocolDecl.IsClassBound (AnyObject / : class) is NOT a skip reason.
        // EveryProtocol is a Swift class and trivially satisfies the AnyObject constraint.
        // Only NSObjectProtocol requires NSObject identity methods that EveryProtocol can't provide.

        // Check GenericSignature for NSObjectProtocol constraints.
        // ObjC protocols often declare constraints like "<τ_0_0 : ObjectiveC.NSObjectProtocol>"
        // in genericSig instead of listing NSObjectProtocol in inheritedProtocols.
        if (!string.IsNullOrEmpty(protocolDecl.GenericSignature))
        {
            if (protocolDecl.GenericSignature.Contains("NSObjectProtocol"))
                return true;
        }

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            // NSObjectProtocol and other ObjC-rooted protocols require NSObject identity
            // methods that EveryProtocol (plain Swift class) cannot provide. NSCoding/NSCopying
            // /NSSecureCoding inherit from NSObjectProtocol and can only be conformed to by
            // NSObject subclasses. AnyObject is satisfied by EveryProtocol.
            if (simpleName is "NSObjectProtocol" or "NSCoding" or "NSSecureCoding" or "NSCopying" or "NSMutableCopying")
                return true;

            // Intra-module transitive check: if an inherited protocol requires NSObjectProtocol,
            // this protocol transitively requires it too.
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
    /// Checks if a protocol's genericSig constrains Self (τ_0_0) to conform to a protocol
    /// that EveryProtocol can't satisfy. Covers three cases:
    /// 1. Constraint from a known ObjC module (UIKit, AppKit, Foundation) — requires NSObject
    /// 2. Constraint referencing a protocol whose conformance was already skipped (same module)
    /// 3. Constraint referencing an underscore-prefixed internal protocol from another module
    /// </summary>
    internal bool HasUnsatisfiedProtocolConstraintInGenericSig(ProtocolDecl protocolDecl)
    {
        if (string.IsNullOrEmpty(protocolDecl.GenericSignature))
            return false;

        var sig = protocolDecl.GenericSignature;

        // Trivial protocols that don't imply unsatisfied conformance
        var trivialProtocols = new HashSet<string>(StringComparer.Ordinal)
        {
            "Copyable", "Escapable", "Sendable", "SendableMetatype",
            "AnyObject", "Error", "NSObjectProtocol"
        };

        foreach (var constraint in ParseGenericSigConstraints(sig))
        {
            // Check unqualified names
            if (trivialProtocols.Contains(constraint))
                continue;

            var dotIdx = constraint.IndexOf('.');
            if (dotIdx < 0)
            {
                // Unqualified name — check if it's a skipped same-module protocol
                if (_skippedProtocols.Contains(constraint))
                    return true;
                continue;
            }

            var moduleName = constraint.Substring(0, dotIdx);
            var typeName = constraint.Substring(dotIdx + 1);

            // Case 1: Known ObjC/Apple framework module
            if (AppleFrameworkRegistry.IsAutoBridgeModule(moduleName) ||
                AppleFrameworkRegistry.IsOptionalFallbackModule(moduleName) ||
                moduleName == "ObjectiveC" || moduleName == "Foundation")
            {
                return true;
            }

            // Case 2: Same-module protocol that was already skipped
            if (_skippedProtocols.Contains(constraint) || _skippedProtocols.Contains(typeName))
                return true;

            // Case 3: Underscore-prefixed internal protocol from external module.
            // These are often ObjC protocol backing types (e.g., StripeApplePay._stpinternal_STPApplePayContextDelegateBase)
            // that we can't inspect. Conservative: skip rather than emit broken conformance.
            if (moduleName != _moduleName && typeName.StartsWith("_"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses constraint protocol names from a genericSig string.
    /// Extracts types after "τ_0_0 : " markers (Self constraints).
    /// </summary>
    private static IEnumerable<string> ParseGenericSigConstraints(string sig)
    {
        var marker = "τ_0_0 : ";
        int idx = 0;
        while (idx < sig.Length)
        {
            var pos = sig.IndexOf(marker, idx, StringComparison.Ordinal);
            if (pos < 0)
                break;

            pos += marker.Length;
            var end = pos;
            while (end < sig.Length && sig[end] != ',' && sig[end] != '>')
                end++;
            var constraint = sig.Substring(pos, end - pos).Trim();
            idx = end;

            if (!string.IsNullOrEmpty(constraint))
                yield return constraint;
        }
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
        "Hashable",
        "Equatable",
        "Comparable",
    };

    private static bool InheritsUnsatisfiedStdlibProtocolRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (s_unsatisfiedStdlibProtocols.Contains(protocolDecl.Name) && IsSwiftStdlibProtocol(protocolDecl))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);

            // Only short-circuit for explicitly Swift-module-qualified names.
            // Unqualified names (no dot) must fall through to the allProtocols
            // recursive lookup to disambiguate library-defined protocols with
            // the same name (e.g., a library-local "Hashable" vs Swift.Hashable).
            if (s_unsatisfiedStdlibProtocols.Contains(simpleName) &&
                name.StartsWith("Swift.", StringComparison.Ordinal))
                return true;

            // For non-Swift-qualified names (including unqualified), resolve via
            // the allProtocols list which has full ProtocolDecl with module info.
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
    /// Returns true if a protocol declaration is from the Swift standard library.
    /// Prevents false positives where a library defines a protocol with a common
    /// stdlib name (e.g., "Hashable").
    /// </summary>
    private static bool IsSwiftStdlibProtocol(ProtocolDecl protocolDecl)
    {
        // If we have module info, verify it's Swift
        if (protocolDecl.SwiftTypeName != null)
            return protocolDecl.SwiftTypeName.Module == "Swift";
        // If no module info, check mangled name prefix ($ss = Swift stdlib)
        if (!string.IsNullOrEmpty(protocolDecl.MangledName))
            return protocolDecl.MangledName.StartsWith("$ss", StringComparison.Ordinal);
        // No module info available — assume stdlib for backward compat
        return true;
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
