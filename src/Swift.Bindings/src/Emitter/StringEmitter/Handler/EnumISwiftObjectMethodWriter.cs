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
            // A Swift payload enum projects as a class whose SafeHandle adopts the wire handle's +1.
            _writer.WriteLine(
                "static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;");
            _writer.WriteLine();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
            WriteBoxAsExistential1(_hasBoxable);
            RecordTypeIfNonGeneric();
        }

        /// <summary>
        /// Records this type for NativeAOT factory registration if it's non-generic.
        /// Generic enums route through <see cref="ModuleEmissionContext.RecordOpenGenericISwiftObjectType"/>
        /// so the trimmer descriptor preserves their reflection metadata.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric()
        {
            if (_emissionCtx == null)
                return;

            if (_enumDecl.IsGeneric)
            {
                _emissionCtx.RecordOpenGenericISwiftObjectType(_enumDecl.Name, _enumDecl.GenericParameters.Count);
                _emissionCtx.RecordOpenGenericPayloadSemantics(
                    _enumDecl.Name, _enumDecl.GenericParameters.Count, Swift.Runtime.PayloadConstructionSemantics.Adopt);
                return;
            }

            _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics);
            _emissionCtx.RecordPayloadSemantics(_typeNameWithGenerics, Swift.Runtime.PayloadConstructionSemantics.Adopt);
            foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                _enumDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
            {
                _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName);
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
                // Type metadata accessor: Swift's metadata accessor for a generic type expects
                // metadata + witness tables for any protocol-constrained generic params.
                // Use the type-metadata-accessor-specific arg/param list so the right PWTs flow through. Method/case/operator P/Invokes have their
                // own conformance handling and continue to use GetMetadataArgumentList().
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();
                _pinvokeHelperContext.AddMetadataAccessorDeclaration(libPath, _enumDecl.MetadataAccessor);
                return;
            }

            if (_swiftWriter != null && _emissionCtx != null &&
                     WrapperValidation.IsXCFrameworkMode(_typeDatabase))
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
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Swift
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
                else
                {
                    var symbol = MetadataWrapperEmitter.GetMetadataSymbolName(moduleName, moduleQualified);
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx, _enumDecl);

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module.
                    // This handles multi-module frameworks where some modules' wrappers
                    // fail to compile (e.g., sub-modules with inaccessible SPI members).
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

                    // Fallback P/Invoke targeting the dylib's metadata accessor directly.
                    // Raw Swift mangled symbol — must pair with CallConvSwift.
                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = _enumDecl.MetadataAccessor,
                        MethodName = "PInvoke_getMetadata_fallback",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Swift
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
                    Visibility = PInvokeVisibility.Internal,
                    CallingConvention = PInvokeCallingConvention.Swift
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
            // NativeAOT trimming: preserve Tag property and CaseTag nested enum for
            // reflection-based access patterns. CaseTag is preserved transitively as
            // the return type of Tag. NewFromPayload is rooted via SwiftObjectReflectionHelper
            // (preserved in ILLink.Descriptors.xml), making it a reliable anchor.
            if (_enumDecl.Cases.Any())
                _writer.WriteLine("""[global::System.Diagnostics.CodeAnalysis.DynamicDependency("Tag")]""");
            // Wrap the raw IntPtr in a SwiftHandle explicitly so the call resolves to the
            // private SwiftHandle-taking constructor, avoiding CS0121 ambiguity against any
            // public single-arg constructor whose parameter accepts an implicit IntPtr conversion.
            //
            // NewFromPayloadCore is factored out so the `_payloadSize` static initializer can
            // hand it to TypeMetadata.RegisterAndGetSize as the NewFromPayloadDispatcher factory.
            // This removes the NativeAOT reflection fallback for generic instantiations.
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            private static ISwiftObject NewFromPayloadCore(IntPtr handle)
            {
                var obj = new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
                Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                return obj;
            }

            [EditorBrowsable(EditorBrowsableState.Never)]
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => NewFromPayloadCore(handle);
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
                    // Closed-constrained existentials project to typed C# interfaces (e.g. ILabelledContainer<SwiftString>),
                    // but for a single-PAT conforming type the conformance dictionary is keyed on typeof(object) — so the
                    // typed lookup misses. Fall back to the object key for any generic-protocol lookup; if no object entry
                    // exists, the fallback is a no-op and the throw path runs.
                    if (!(typeof(TProtocol).IsGenericType && _protocolConformanceSymbols.TryGetValue(typeof(object), out symbolName)))
                    {
                        throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_enumDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                    }
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the enum.
        /// For generic ISwiftObject enums, also emits the eager-init pattern that mirrors
        /// SwiftArray.cs so NativeAOT (ILC) can statically reach SwiftObjectHelper&lt;Self&gt;.
        /// GetTypeMetadata for each closed instantiation.
        /// </summary>
        private void WriteStaticConstructor()
        {
            bool isGeneric = _enumDecl.IsGeneric;
            var eagerInitCallLine = isGeneric
                ? "    if (SwiftRuntimeInfo.IsNativeAotRuntime) { TryEagerInitialize(); }"
                : "";
            var eagerInitHelpers = isGeneric
                ? $$"""

                [EditorBrowsable(EditorBrowsableState.Never)]
                internal static bool TryEagerInitialize()
                {
                    try
                    {
                        NativeAotInitialize();
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
                private static void NativeAotInitialize()
                {
                    _ = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                }
                """
                : "";

            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_constructorName}}()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {{GenerateGetProtocolConformanceDictionaryEntries()}}
                };
            {{eagerInitCallLine}}
            }{{eagerInitHelpers}}
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
