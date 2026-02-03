// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods.
    /// </summary>
    class ISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly StructDecl _structDecl;
        private readonly string _typeNameWithGenerics;

        public ISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, StructDecl structDecl, string typeNameWithGenerics)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _structDecl = structDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for non-frozen structs.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        public void WriteNonFrozenStructImplementation(PInvokeHelperContext? pinvokeHelperContext = null)
        {
            WriteGetTypeMetadata(pinvokeHelperContext);
            WriteNewFromPayloadNonFrozenStruct();
            WriteMarshalToSwiftNonFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for frozen structs.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        public void WriteFrozenStructImplementation(PInvokeHelperContext? pinvokeHelperContext = null)
        {
            WriteGetTypeMetadata(pinvokeHelperContext);
            WriteNewFromPayloadFrozenStruct();
            WriteMarshalToSwiftFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the struct along with the PInvoke method.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        private void WriteGetTypeMetadata(PInvokeHelperContext? pinvokeHelperContext)
        {
            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);

            if (pinvokeHelperContext != null)
            {
                // For generic types, call the helper class with type metadata arguments
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({metadataArgs});");
                _writer.WriteLine();

                // Add the P/Invoke declaration to the helper context
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = _structDecl.MetadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                pinvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                _writer.WriteLine();

                var pinvokeText = $$"""
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("{{libPath}}", EntryPoint = "{{_structDecl.MetadataAccessor}}")]
                internal static extern TypeMetadata PInvoke_getMetadata();
                """;

                _writer.WriteLines(pinvokeText);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the NewFromPayload method for the struct.
        /// </summary>
        private void WriteNewFromPayloadFrozenStruct()
        {
            TypeRecord typeRecord = _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName);
            if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                // Note: Constructor name uses _structDecl.Name (not generic), but type references use _typeNameWithGenerics
                var text = $$"""
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new {{_typeNameWithGenerics}}(handle);
                }

                unsafe {{_structDecl.Name}}(IntPtr handle)
                {
                    IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({{_typeNameWithGenerics}}.Buffer));
                    *({{_typeNameWithGenerics}}.Buffer*)bufferPtr = *({{_typeNameWithGenerics}}.Buffer*)handle;
                    _payload = new SwiftSafeHandle<{{_typeNameWithGenerics}}>(bufferPtr);
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                var text = $$"""
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return *({{_typeNameWithGenerics}}*)handle;
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the NewFromPayload method for the struct.
        /// </summary>
        private void WriteNewFromPayloadNonFrozenStruct()
        {
            var text = $$"""
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new {{_typeNameWithGenerics}}(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();

            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            // Note: Constructor name uses _structDecl.Name (not generic), but SwiftSafeHandle uses _typeNameWithGenerics
            var text = $$"""
            {{_structDecl.Name}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_typeNameWithGenerics}}>(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the struct.
        /// </summary>
        private void WriteMarshalToSwiftFrozenStruct()
        {
            TypeRecord typeRecord = _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName);
            if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                var text = $$"""
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        // Ensure that the instance is valid before making copy
                        bool success = false;
                        _payload.DangerousAddRef(ref success);
                        try
                        {
                            metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                            return (int)metadata.Size;
                        }
                        finally
                        {
                            if (success)
                                _payload.DangerousRelease();
                        }
                    }
                }
                """;

                _writer.WriteLines(text);
            }
            else
            {
                var text = $$"""
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* payload = &this)
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, payload, metadata);
                        return (int)metadata.Size;
                    }
                }
                """;

                _writer.WriteLines(text);
            }

            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the struct.
        /// </summary>
        private void WriteMarshalToSwiftNonFrozenStruct()
        {
            var text = $$"""
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                        return (int)metadata.Size;
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the GetProtocolConformanceDescriptor method for the struct.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context (unused, for API consistency).</param>
        private void WriteGetProtocolConformanceDescriptor(PInvokeHelperContext? pinvokeHelperContext)
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            // Note: LoadFromSymbol is a runtime call, not a DllImport, so no helper class needed
            var text = $$"""
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_structDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the struct.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_structDecl.Name}}()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {{GenerateGetProtocolConformanceDictionaryEntries()}}
                };
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                _structDecl.Conformances,
                _moduleDecl.Name,
                _typeNameWithGenerics,
                _typeDatabase);
        }
    }

    public class EqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly StructDecl _structDecl;
        private readonly string _typeNameWithGenerics;
        private readonly bool _implementsEquatable;
        private readonly bool _isRefType;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType)
            : this(csWriter, structDecl, refType, false, false)
        {
        }

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _structDecl = structDecl;
            // Use type name with generics for operators to fix CS0563/CS0305 errors on generic types
            _typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl);
            _implementsEquatable = _structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            _isRefType = refType;
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        public void WriteSwiftEquatableImplementation()
        {
            if (_implementsEquatable)
            {
                WriteSwiftEquatableImplementationWithSwiftEquals(_isRefType);
            }
            else
            {
                WriteDefaultEquatableImplementation();
            }
        }

        private void WriteSwiftEquatableImplementationWithSwiftEquals(bool refType)
        {
            // Always write Equals and GetHashCode methods
            // Use simple name for is-check and error messages
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}{{(refType == true ? "?" : "")}} other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            """;

            _writer.WriteLines(equatableEquals);
            _writer.WriteLine();
        }

        private void WriteDefaultEquatableImplementation()
        {
            // Always write Equals and GetHashCode methods
            // Use simple name for error messages
            var equalsMethods = $$"""
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.

            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }
        }
    }

    /// <summary>
    /// Static helper class for protocol conformance code generation shared across type handlers.
    /// </summary>
internal static class ProtocolConformanceHelper
{
        /// <summary>
        /// Protocols from other modules that we support for cross-module conformance.
        /// This will be removed once we process multiple modules properly.
        /// </summary>
    private static readonly HashSet<string> CrossModuleSupportedProtocols = new()
    {
        "Swift.Equatable"
    };

    /// <summary>
    /// Builds the C# interface list for a concrete Swift type declaration.
    /// Includes ISwiftObject and supported protocol conformances.
    /// Note: Only emits IEquatable for classes and structs (which have Equals implementations via SwiftEquatable).
    /// Enums with associated values are emitted as C# classes without Equals implementation.
    /// Other protocol conformances are tracked in GetProtocolConformanceDescriptor but not in the interface list
    /// until protocol method emission on conforming types is implemented.
    /// </summary>
    public static List<string> GetImplementedInterfaces(
        TypeDecl typeDecl,
        string typeNameWithGenerics,
        string moduleName,
        ITypeDatabase typeDatabase)
    {
        var interfaces = new List<string> { typeof(ISwiftObject).Name };
        var emitted = new HashSet<string>(interfaces);

        // Only classes and structs get Equatable interface (they have Equals via SwiftEquatable)
        // Enums with associated values are emitted as C# classes without Equals implementation
        bool canEmitEquatable = typeDecl is ClassDecl or StructDecl;

        IEnumerable<TypeConformance> conformances = typeDecl switch
        {
            ClassDecl classDecl => classDecl.Conformances,
            StructDecl structDecl => structDecl.Conformances,
            EnumDecl enumDecl => enumDecl.Conformances,
            _ => Enumerable.Empty<TypeConformance>()
        };

        foreach (var conformance in conformances)
        {
            // Only emit Equatable interface for classes/structs with Equals implementation
            if (conformance.Protocol.ModuleQualifiedName == "Swift.Equatable")
            {
                if (!canEmitEquatable)
                    continue;

                if (!ShouldEmitConformance(conformance, moduleName, typeDatabase))
                    continue;

                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics);
                if (emitted.Add(iface))
                    interfaces.Add(iface);
            }
            // Other protocols: not emitted in interface list until protocol method emission is implemented
        }

        return interfaces;
    }

        /// <summary>
        /// Generates the dictionary entries for GetProtocolConformanceDescriptor implementation.
        /// </summary>
        /// <param name="conformances">The conformances to process.</param>
        /// <param name="moduleName">The current module name.</param>
        /// <param name="typeName">The name of the type implementing the conformances.</param>
        /// <param name="typeDatabase">The type database for protocol lookups.</param>
        /// <returns>A comma-separated string of dictionary entries.</returns>
    public static string GenerateProtocolConformanceDictionaryEntries(
        IEnumerable<TypeConformance> conformances,
        string moduleName,
        string typeName,
        ITypeDatabase typeDatabase)
        {
            var entries = new List<string>();

        foreach (var conformance in conformances)
        {
            if (!ShouldEmitConformance(conformance, moduleName, typeDatabase))
                continue;

            var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeName);
            var protocolConformanceSymbol = conformance.ProtocolConformanceDescriptor;

                entries.Add($"{{typeof({protocol}), \"{protocolConformanceSymbol}\"}}");
            }

        return string.Join(",\n", entries);
    }

    private static bool ShouldEmitConformance(TypeConformance conformance, string moduleName, ITypeDatabase typeDatabase)
    {
        if (conformance.Protocol.Module != moduleName &&
            !CrossModuleSupportedProtocols.Contains(conformance.Protocol.ModuleQualifiedName))
        {
            return false;
        }

        // Preserve existing behavior for Equatable even when protocol records are unavailable.
        if (conformance.Protocol.ModuleQualifiedName == "Swift.Equatable")
            return true;

        // Skip unknown protocols and protocols with associated types (PATs).
        if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var record))
            return false;

        if (record.Kind != TypeRecordKind.Protocol)
            return false;

        if (record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
            return false;

        return true;
    }
}
}
