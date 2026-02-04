// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# proxy classes for Swift protocols.
/// The proxy pattern allows C# code to implement Swift protocols by:
/// 1. Wrapping either a C# implementation or a Swift existential container
/// 2. Providing a vtable of function pointers that Swift can call back into
/// 3. Managing the EveryProtocol instance and protocol witness table
/// </summary>
public partial class ProtocolProxyEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;

    public ProtocolProxyEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
    }

    /// <summary>
    /// Emits the complete proxy class for a protocol.
    /// </summary>
    public void EmitProxyClass(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple generic parameters
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping proxy class for {protocolDecl.Name}: has Self requirement");
            return;
        }

        // Skip protocols with associated types (would create generic proxy classes).
        // C# doesn't allow [UnmanagedCallersOnly] or [DllImport] in generic types,
        // and nested classes inside generic types inherit this restriction.
        // Future approaches: Reflection.Emit at runtime, non-generic base class with
        // object-typed dispatch, or source-generated specializations per concrete type.
        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            _logger.LogWarning($"Skipping proxy class for {protocolDecl.Name}: protocols with associated types are not yet supported for proxy generation (would require [UnmanagedCallersOnly] in generic type)");
            return;
        }

        // Skip protocols with no implementable instance members
        // Static members are not part of the witness table, so we only count non-static members
        var hasImplementableMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any(s => !s.IsStatic);
        if (!hasImplementableMembers)
        {
            _logger.LogDebug($"Skipping proxy class for {protocolDecl.Name}: no implementable instance members (may have only static requirements)");
            return;
        }

        var interfaceName = NameProvider.GetInterfaceName(protocolDecl.Name);
        var proxyClassName = GetProxyClassName(protocolDecl);
        var proxyClassNameWithGenerics = GetProxyClassNameWithGenerics(protocolDecl);
        var interfaceNameWithGenerics = GetInterfaceNameWithGenerics(protocolDecl);
        var constraints = GetProxyClassConstraints(protocolDecl);

        writer.WriteLine($"/// <summary>");
        writer.WriteLine($"/// Proxy class that enables C# implementations of the {protocolDecl.Name} protocol.");
        writer.WriteLine($"/// Can wrap either a C# implementation or receive Swift existential containers.");
        writer.WriteLine($"/// </summary>");
        writer.WriteLine($"public unsafe class {proxyClassNameWithGenerics} : {interfaceNameWithGenerics}, ISwiftObject{constraints}");
        writer.WriteLine("{");
        writer.Indent++;

        // Emit vtable structs
        EmitSwiftVtableStruct(writer, protocolDecl);
        EmitLocalVtableStruct(writer, protocolDecl);

        // Emit static fields
        EmitStaticFields(writer, protocolDecl);

        // Emit instance fields
        EmitInstanceFields(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit static constructor (registers vtable with Swift)
        EmitStaticConstructor(writer, protocolDecl);

        // Emit receiver methods (UnmanagedCallersOnly callbacks)
        EmitReceiverMethods(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit constructors
        EmitConstructors(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit interface implementation (with witness dispatch for blittable members)
        var dispatchEmitter = new WitnessDispatchEmitter(_typeDatabase, _logger, _moduleName);
        EmitInterfaceImplementation(writer, protocolDecl, interfaceNameWithGenerics, dispatchEmitter);

        // Emit ISwiftObject implementation
        EmitISwiftObjectImplementation(writer, protocolDecl, dispatchEmitter);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }
}
