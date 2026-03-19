// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods for enums.
    /// </summary>
    class EnumISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly EnumDecl _enumDecl;
        private readonly string _typeNameWithGenerics;
        private readonly string _constructorName;
        private readonly PInvokeHelperContext? _pinvokeHelperContext;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionCtx;
        private readonly bool _hasBoxable;

        public EnumISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, EnumDecl enumDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext, SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionCtx = null, bool hasBoxable = false)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _enumDecl = enumDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            var angleBracket = typeNameWithGenerics.IndexOf('<');
            _constructorName = angleBracket >= 0 ? typeNameWithGenerics.Substring(0, angleBracket) : typeNameWithGenerics;
            _pinvokeHelperContext = pinvokeHelperContext;
            _swiftWriter = swiftWriter;
            _emissionCtx = emissionCtx;
            _hasBoxable = hasBoxable;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for enums.
        /// </summary>
        public void WriteEnumImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
            WriteBoxAsExistential1(_hasBoxable);
            RecordTypeIfNonGeneric();
        }

        /// <summary>
        /// Records this type for NativeAOT factory registration if it's non-generic.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric()
        {
            if (_emissionCtx != null && !_typeNameWithGenerics.Contains('<'))
            {
                _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics);
                foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                    _enumDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
                {
                    _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName);
                }
            }
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the enum along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            if (_pinvokeHelperContext != null)
            {
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetMetadataArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();
                _pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = _enumDecl.MetadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "TypeMetadataRequest request",
                    IsAsync = false,
                    MetadataParameters = _pinvokeHelperContext.GetMetadataParameterDeclarations()
                });
                return;
            }

            if (_swiftWriter != null && _emissionCtx != null &&
                     !string.IsNullOrEmpty(_typeDatabase.AsyncLibraryName))
            {
                // Xcframework mode: emit @_cdecl metadata wrapper.
                // Internal types are inaccessible by name — fall back to CallConvSwift.
                var moduleQualified = _enumDecl.SwiftTypeName.ModuleQualifiedName;
                var moduleName = _enumDecl.SwiftTypeName.Module;

                if (_enumDecl.IsModuleInternal)
                {
                    // Fallback: use CallConvSwift P/Invoke targeting the dylib's metadata accessor
                    _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = _enumDecl.MetadataAccessor,
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

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module.
                    // This handles multi-module frameworks where some modules' wrappers
                    // fail to compile (e.g., Stripe sub-modules).
                    _writer.WriteLines("""
                        static TypeMetadata ISwiftObject.GetTypeMetadata()
                        {
                            try
                            {
                                return PInvoke_getMetadata();
                            }
                            catch (System.DllNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                            catch (System.EntryPointNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                        }
                        """);
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

                    // Fallback P/Invoke targeting the dylib's metadata accessor directly
                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = _enumDecl.MetadataAccessor,
                        MethodName = "PInvoke_getMetadata_fallback",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal
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
                    EntryPoint = _enumDecl.MetadataAccessor,
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
        /// Writes the NewFromPayload method for the enum.
        /// </summary>
        private void WriteNewFromPayload()
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

            EmitDefaultConstructor();
            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the default private constructor (for enum case construction).
        /// </summary>
        private void EmitDefaultConstructor()
        {
            var text = $$"""
            {{_constructorName}}()
            {
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
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
        /// Writes the MarshalToSwift method for the enum.
        /// </summary>
        private void WriteMarshalToSwift()
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
        /// Writes the GetProtocolConformanceDescriptor method for the enum.
        /// </summary>
        private void WriteGetProtocolConformanceDescriptor()
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_enumDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the enum.
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

        private void WriteBoxAsExistential1(bool emit)
        {
            if (!emit)
                return;

            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            ExistentialContainer1 Swift.Runtime.IExistentialBoxable.BoxAsExistential1<TProtocol>()
                => ExistentialContainerFactory.Create<{{_typeNameWithGenerics}}, TProtocol>(this);
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                _enumDecl.Conformances,
                _moduleDecl.Name,
                _typeNameWithGenerics,
                _typeDatabase);
        }
    }
}
