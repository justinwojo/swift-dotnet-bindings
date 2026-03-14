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
        private readonly string _constructorName;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionCtx;

        public ISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, StructDecl structDecl, string typeNameWithGenerics, SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionCtx = null)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _structDecl = structDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            _swiftWriter = swiftWriter;
            _emissionCtx = emissionCtx;
            // Constructor name is the type name without generic parameters (e.g., "ContentTypeInfo<T>" → "ContentTypeInfo")
            var angleBracket = typeNameWithGenerics.IndexOf('<');
            _constructorName = angleBracket >= 0 ? typeNameWithGenerics.Substring(0, angleBracket) : typeNameWithGenerics;
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
        /// <param name="isProjectedAsClass">True if the frozen struct is projected as a class (already has Dispose via _payload).</param>
        public void WriteFrozenStructImplementation(PInvokeHelperContext? pinvokeHelperContext = null, bool isProjectedAsClass = false)
        {
            WriteGetTypeMetadata(pinvokeHelperContext);
            WriteNewFromPayloadFrozenStruct();
            WriteMarshalToSwiftFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
            if (!isProjectedAsClass)
            {
                // Frozen value-type structs have no managed resources to dispose
                _writer.WriteLine("public void Dispose() { }");
                _writer.WriteLine();
            }
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
            else if (_swiftWriter != null && _emissionCtx != null &&
                     !string.IsNullOrEmpty(_typeDatabase.AsyncLibraryName))
            {
                // Xcframework mode: emit @_cdecl metadata wrapper.
                // Internal types are inaccessible by name — fall back to CallConvSwift.
                var moduleQualified = _structDecl.SwiftTypeName.ModuleQualifiedName;
                var moduleName = _structDecl.SwiftTypeName.Module;

                if (_structDecl.IsModuleInternal)
                {
                    // Fallback: use CallConvSwift P/Invoke targeting the dylib's metadata accessor
                    _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = _structDecl.MetadataAccessor,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
                else
                {
                    var symbol = MetadataWrapperEmitter.GetMetadataSymbolName(moduleName, moduleQualified);
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx);

                    _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = _typeDatabase.AsyncLibraryName!,
                        EntryPoint = symbol,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Cdecl
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
            }
            else
            {
                // Manual mode: existing CallConvSwift path
                _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                _writer.WriteLine();

                foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                {
                    LibraryPath = libPath,
                    EntryPoint = _structDecl.MetadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    Visibility = PInvokeVisibility.Internal
                }))
                    _writer.WriteLine(line);
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
                // Constructor name uses _constructorName (may differ from _structDecl.Name if renamed)
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    var obj = new {{_typeNameWithGenerics}}(handle);
                    Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                    return obj;
                }

                unsafe {{_constructorName}}(IntPtr handle)
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
                [EditorBrowsable(EditorBrowsableState.Never)]
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
            [EditorBrowsable(EditorBrowsableState.Never)]
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                var obj = new {{_typeNameWithGenerics}}(handle);
                Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                return obj;
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
            var text = $$"""
            {{_constructorName}}(SwiftHandle handle)
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
                [EditorBrowsable(EditorBrowsableState.Never)]
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
                [EditorBrowsable(EditorBrowsableState.Never)]
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
            [EditorBrowsable(EditorBrowsableState.Never)]
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
            [EditorBrowsable(EditorBrowsableState.Never)]
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
            [EditorBrowsable(EditorBrowsableState.Never)]
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_constructorName}}()
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

    /// <summary>
    /// Shared helpers for emitting type-level XML doc annotations (disposal remarks, opaque type attributes).
    /// Called after XmlDocCommentEmitter.EmitDocComment, before the type declaration.
    /// </summary>
    internal static class TypeAnnotationHelper
    {
        /// <summary>
        /// Emits disposal remarks as XML doc comments for types that wrap Swift objects.
        /// </summary>
        internal static void EmitDisposalRemarks(CSharpWriter csWriter, TypeDecl typeDecl)
        {
            // If the symbol graph already provided remarks, skip to avoid duplication
            if (typeDecl.Documentation != null && !typeDecl.Documentation.IsEmpty
                && (typeDecl.Documentation.Remarks.Count > 0 || !string.IsNullOrWhiteSpace(typeDecl.Documentation.Throws)))
                return;

            if (typeDecl is ClassDecl)
            {
                // Classes use ARC-bridged SwiftClassHandle — disposal is optional.
                csWriter.WriteLine("/// <remarks>");
                csWriter.WriteLine("/// This type wraps a Swift class with automatic ARC bridging.");
                csWriter.WriteLine("/// Dispose() is available for deterministic cleanup but is not required.");
                csWriter.WriteLine("/// The GC finalizer handles ARC release automatically.");
                csWriter.WriteLine("/// </remarks>");
            }
            else
            {
                var swiftKind = typeDecl switch
                {
                    EnumDecl => "enum",
                    StructDecl => "struct",
                    _ => "type"
                };
                csWriter.WriteLine("/// <remarks>");
                csWriter.WriteLine($"/// This type wraps a Swift {swiftKind} and must be disposed explicitly.");
                csWriter.WriteLine("/// Use a 'using' block or call Dispose(). Failure to dispose may leak native memory.");
                csWriter.WriteLine("/// </remarks>");
            }
        }

        /// <summary>
        /// Emits opaque type remarks and [OpaqueSwiftType] attribute for types with zero
        /// projectable public members.
        /// </summary>
        internal static void EmitOpaqueTypeAnnotation(CSharpWriter csWriter, int skippedCount)
        {
            csWriter.WriteLine("/// <remarks>");
            csWriter.WriteLine($"/// This type has no projectable public members ({skippedCount} Swift member(s) could not be represented in C#).");
            csWriter.WriteLine("/// It can still be used as an opaque handle when passed to or returned from other Swift APIs.");
            csWriter.WriteLine("/// </remarks>");
            csWriter.WriteLine($"[global::Swift.OpaqueSwiftType({skippedCount})]");
        }
    }

    public class EqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly StructDecl _structDecl;
        private readonly string _typeNameWithGenerics;
        private readonly bool _implementsEquatable;
        private readonly bool _implementsHashable;
        private readonly bool _isRefType;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics)
            : this(csWriter, structDecl, refType, typeNameWithGenerics, false, false)
        {
        }

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _structDecl = structDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            _implementsEquatable = _structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            // OptionSet and RawRepresentable imply Hashable in Swift — ABI may not list it explicitly.
            _implementsHashable = _structDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
                (c.Protocol.Name == "Hashable" && string.IsNullOrEmpty(c.Protocol.Module)) ||
                c.Protocol.Name == "OptionSet" ||
                c.Protocol.Name == "RawRepresentable");
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
            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }

            public override int GetHashCode()
            {
                {{hashCodeBody}}
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                string equalityBody;
                if (refType)
                {
                    equalityBody = $$"""
                    public static bool operator ==({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                    {
                        if (left is null) return right is null;
                        if (right is null) return false;
                        return Swift.Runtime.SwiftEquatable.Equals(left, right);
                    }
                    """;
                }
                else
                {
                    equalityBody = $$"""
                    public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                    {
                        return Swift.Runtime.SwiftEquatable.Equals(left, right);
                    }
                    """;
                }
                _writer.WriteLines(equalityBody);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                string inequalityBody;
                if (refType)
                {
                    inequalityBody = $$"""
                    public static bool operator !=({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                    {
                        if (left is null) return right is not null;
                        if (right is null) return true;
                        return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                    }
                    """;
                }
                else
                {
                    inequalityBody = $$"""
                    public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                    {
                        return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                    }
                    """;
                }
                _writer.WriteLines(inequalityBody);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}{{(refType == true ? "?" : "")}} other)
            {
                {{(refType == true ? "if (other is null) return false;\n            " : "")}}return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            """;

            _writer.WriteLines(equatableEquals);
            _writer.WriteLine();
        }

        private void WriteDefaultEquatableImplementation()
        {
            // Non-Equatable types: no Equals/GetHashCode/operator overrides.
            // Classes inherit reference equality from object.
            // Structs projected as classes inherit reference equality from object.
            // Frozen structs projected as value types inherit reflection-based equality from ValueType.
        }
    }

    /// <summary>
    /// Static helper class for protocol conformance code generation shared across type handlers.
    /// </summary>
internal static class ProtocolConformanceHelper
{

    /// <summary>
    /// Builds the C# interface list for a concrete Swift type declaration.
    /// Includes ISwiftObject and supported protocol conformances.
    /// Enums with associated values are emitted as C# classes without Equals implementation,
    /// so they do not get the IEquatable interface.
    /// </summary>
    /// <param name="typeDecl">The type declaration to get interfaces for.</param>
    /// <param name="typeNameWithGenerics">The C# type name including generic parameters.</param>
    /// <param name="moduleName">The current module name.</param>
    /// <param name="typeDatabase">The type database for type lookups.</param>
    /// <param name="conformanceValidator">Optional validator to check if all protocol members can be emitted.</param>
    /// <returns>List of interface names the type should implement.</returns>
    public static List<string> GetImplementedInterfaces(
        TypeDecl typeDecl,
        string typeNameWithGenerics,
        string moduleName,
        ITypeDatabase typeDatabase,
        ProtocolConformanceValidator? conformanceValidator = null)
    {
        var interfaces = new List<string> { typeof(ISwiftObject).Name, nameof(IDisposable) };
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
            // Hashable is a marker interface (ISwiftHashable) for PWT lookup only — not a user-facing
            // C# interface. The conformance descriptor is still emitted via GenerateProtocolConformanceDictionaryEntries().
            if (conformance.Protocol.ModuleQualifiedName == "Swift.Hashable" || (conformance.Protocol.Name == "Hashable" && string.IsNullOrEmpty(conformance.Protocol.Module)))
                continue;

            // Special handling for Equatable: only emit for classes/structs with Equals implementation
            if (conformance.Protocol.ModuleQualifiedName == "Swift.Equatable")
            {
                if (!canEmitEquatable)
                    continue;

                if (!ShouldEmitConformance(conformance, moduleName, typeDatabase))
                    continue;

                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics, conformance.Protocol.Module);
                if (emitted.Add(iface))
                    interfaces.Add(iface);
            }
            else
            {
                // All other protocol conformances: emit if the protocol has a valid TypeRecord
                if (!ShouldEmitConformance(conformance, moduleName, typeDatabase))
                    continue;

                // Validate protocol can be fully implemented if validator is provided
                if (conformanceValidator != null)
                {
                    // Use ModuleQualifiedName for precision when same-name protocols exist
                    var protocolDecl = conformanceValidator.FindProtocol(conformance.Protocol.ModuleQualifiedName);

                    // Cross-module protocols (e.g., Swift.Equatable) return null from FindProtocol
                    // since they're not in moduleDecl.Protocols. These are handled above for Equatable.
                    // For other cross-module protocols, we trust ShouldEmitConformance already validated.
                    if (protocolDecl != null)
                    {
                        // Same-module protocol - validate concrete type members
                        if (!conformanceValidator.CanFullyImplementProtocol(typeDecl, protocolDecl))
                            continue;  // Skip interface if we can't fully implement it

                        // Self-requirement protocols produce generic interfaces (IFoo<TSelf>)
                        // The concrete type provides itself as the type argument
                        if (protocolDecl.HasSelfRequirement)
                        {
                            var baseName = NameProvider.GetInterfaceName(conformance.Protocol.Name,
                                typeNameWithGenerics, conformance.Protocol.Module);
                            var genericIface = $"{baseName}<{typeNameWithGenerics}>";
                            if (emitted.Add(genericIface))
                                interfaces.Add(genericIface);
                            continue;
                        }
                    }
                }

                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics, conformance.Protocol.Module);
                if (emitted.Add(iface))
                    interfaces.Add(iface);
            }
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

            // Skip Self-requirement protocols — no proxy, no EveryProtocol, no runtime PWT lookup
            if (typeDatabase.TryGetTypeRecord(conformance.Protocol, out var protoRecord) &&
                protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                continue;

            var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeName, conformance.Protocol.Module);
            var protocolConformanceSymbol = conformance.ProtocolConformanceDescriptor;

            // Skip empty conformance symbols — an empty string would crash at runtime
            // via LoadFromSymbol("lib", ""). This can happen when an inherited conformance's
            // symbol lives under the base class's TBD entry and is not found for the derived class.
            if (string.IsNullOrEmpty(protocolConformanceSymbol))
                continue;

                entries.Add($"{{typeof({protocol}), \"{protocolConformanceSymbol}\"}}");
            }

        return string.Join(",\n", entries);
    }

    private static bool ShouldEmitConformance(TypeConformance conformance, string moduleName, ITypeDatabase typeDatabase)
    {
        // Preserve existing behavior for Equatable/Hashable even when protocol records are unavailable.
        if (conformance.Protocol.ModuleQualifiedName == "Swift.Equatable")
            return true;
        if (conformance.Protocol.ModuleQualifiedName == "Swift.Hashable" || (conformance.Protocol.Name == "Hashable" && string.IsNullOrEmpty(conformance.Protocol.Module)))
            return true;

        // Skip unknown protocols and protocols with associated types (PATs).
        // Cross-module protocols require a loaded module database (--module-database)
        // to have a TypeRecord; without one they are silently skipped.
        if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var record))
            return false;

        if (record.Kind != TypeRecordKind.Protocol)
            return false;

        if (record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
            return false;

        // Well-known runtime protocols (e.g., Swift.Error → AnyError) map to direct
        // runtime types, not generated interfaces. Skip them.
        if (TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(record))
            return false;

        // Cross-module protocols with interface members cannot be safely emitted as
        // conformances because the generator cannot produce stubs for cross-module
        // protocol requirements, causing CS0535 at compile time.
        // Same-module protocols are validated by CanFullyImplementProtocol in the caller.
        if (conformance.Protocol.Module != moduleName)
        {
            // EmittedMemberCount == null means an older database without this field.
            // Conservatively skip to avoid potential CS0535.
            if (record.EmittedMemberCount == null || record.EmittedMemberCount > 0)
                return false;
        }

        return true;
    }
}

    /// <summary>
    /// Helper for emitting ToString() override on types conforming to CustomStringConvertible.
    /// </summary>
    internal static class ToStringHelper
    {
        /// <summary>
        /// Checks if the type has a non-static 'description' property returning Swift.String
        /// (indicating CustomStringConvertible conformance) and returns the emitted C# property name.
        /// </summary>
        public static bool TryGetDescriptionPropertyName(
            TypeDecl typeDecl,
            Dictionary<string, string>? renames,
            out string propertyName)
        {
            propertyName = "";

            var descProp = typeDecl.Properties.FirstOrDefault(p =>
                p.Name == "description" &&
                !p.IsStatic &&
                p.Accessors.Any(a => a is GetAccessorDecl) &&
                p.SwiftTypeSpec is NamedTypeSpec named &&
                named.Name == "Swift.String");

            if (descProp == null)
                return false;

            propertyName = NameProvider.GetFinalMemberName(
                NameProvider.GetPropertyName(descProp.Name, typeDecl.Name), renames);
            return true;
        }

        /// <summary>
        /// Emits 'public override string ToString() => PropertyName;' if the type has a description property.
        /// </summary>
        public static void EmitToStringIfDescriptionExists(
            IndentedTextWriter writer,
            TypeDecl typeDecl,
            Dictionary<string, string>? renames)
        {
            if (TryGetDescriptionPropertyName(typeDecl, renames, out var propertyName))
            {
                writer.WriteLine($"public override string ToString() => {propertyName};");
                writer.WriteLine();
            }
        }
    }
}
