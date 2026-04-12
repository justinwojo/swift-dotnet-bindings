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
        public void WriteNonFrozenStructImplementation(PInvokeHelperContext? pinvokeHelperContext = null, bool emitBoxable = false)
        {
            WriteGetTypeMetadata(pinvokeHelperContext);
            WriteNewFromPayloadNonFrozenStruct();
            WriteMarshalToSwiftNonFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
            WriteBoxAsExistential1(emitBoxable);
            RecordTypeIfNonGeneric();
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for frozen structs.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        /// <param name="isProjectedAsClass">True if the frozen struct is projected as a class (already has Dispose via _payload).</param>
        public void WriteFrozenStructImplementation(PInvokeHelperContext? pinvokeHelperContext = null, bool isProjectedAsClass = false, bool emitBoxable = false)
        {
            WriteGetTypeMetadata(pinvokeHelperContext);
            WriteNewFromPayloadFrozenStruct();
            WriteMarshalToSwiftFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
            WriteBoxAsExistential1(emitBoxable);
            RecordTypeIfNonGeneric();
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
                // Type metadata accessor: Swift's metadata accessor for a generic type expects
                // metadata + witness tables for any protocol-constrained generic params (per
                // runtime-metadata.md). Use the type-metadata-accessor-specific arg/param list
                // so the right PWTs flow through.
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();

                // Add the P/Invoke declaration to the helper context
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = _structDecl.MetadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "TypeMetadataRequest request",
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetTypeMetadataAccessorParameterDeclarations()
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

                if (_structDecl.IsModuleInternal ||
                    WrapperValidation.IsNonCopyableStructParent(_structDecl))
                {
                    // Fallback: use CallConvSwift P/Invoke targeting the dylib's metadata accessor.
                    // Internal types are inaccessible by name in the wrapper.
                    // Noncopyable (~Copyable) types can't use `T.self as Any.Type` in Swift 6
                    // (Any requires Copyable conformance), so we skip the @_cdecl wrapper.
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
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx, _structDecl);

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module.
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
                        EntryPoint = _structDecl.MetadataAccessor,
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
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
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
            // Wrap the raw IntPtr in a SwiftHandle explicitly so the call resolves to the
            // private SwiftHandle-taking constructor, avoiding CS0121 ambiguity against any
            // public single-arg constructor whose parameter accepts an implicit IntPtr conversion
            // (e.g. SwiftOptional<IntPtr> for non-bridged optional parameters).
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                var obj = new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
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
        /// Records this type for NativeAOT factory registration if it's non-generic.
        /// Generic types rely on constrained code paths for registration.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric()
        {
            if (_emissionCtx != null && !_typeNameWithGenerics.Contains('<'))
            {
                _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics);
                foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                    _structDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
                {
                    _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName);
                }
            }
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

        /// <summary>
        /// Writes the IExistentialBoxable.BoxAsExistential1 implementation.
        /// Enables concrete types to be boxed into existential containers for protocol parameter passing.
        /// Only emits when the interface list includes IExistentialBoxable (controlled by caller).
        /// </summary>
        private void WriteBoxAsExistential1(bool emit = true)
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
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionContext;
        private readonly string? _wrapperLibraryName;

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

        /// <summary>
        /// Constructor with Swift wrapper support. When swiftWriter and emissionContext are provided,
        /// emits @_cdecl equality wrappers instead of using SwiftEquatable.Equals (which uses
        /// CallConvSwift and crashes on NativeAOT).
        /// </summary>
        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, SwiftWriter? swiftWriter, ModuleEmissionContext? emissionContext, string? wrapperLibraryName)
            : this(csWriter, structDecl, refType, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator)
        {
            _swiftWriter = swiftWriter;
            _emissionContext = emissionContext;
            _wrapperLibraryName = wrapperLibraryName;
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

        /// <summary>
        /// Gets the @_cdecl symbol name for an equality wrapper.
        /// </summary>
        private static string GetEqualitySymbolName(StructDecl structDecl)
        {
            var moduleName = structDecl.ModuleDecl?.Name ?? "Unknown";
            var safeTypeName = structDecl.Name.Replace(".", "_");
            var hash = EmitterUtility.DeterministicHash8(structDecl.MangledName ?? structDecl.Name);
            return $"SBW_{moduleName}_{safeTypeName}_eq_{hash}";
        }

        /// <summary>
        /// Emits a @_cdecl Swift wrapper for equality comparison and returns the symbol name.
        /// Returns null if wrapper emission is not available or not needed.
        /// </summary>
        private string? TryEmitSwiftEqualityWrapper()
        {
            if (_swiftWriter == null || _emissionContext == null || _wrapperLibraryName == null)
                return null;

            // Skip for generic types (wrapper can't be instantiated)
            if (_structDecl.GenericParameters.Count > 0)
                return null;

            var symbolName = GetEqualitySymbolName(_structDecl);

            // Check dedup — don't emit twice for the same symbol
            if (!_emissionContext.TryAddEqualityWrapperSymbol(symbolName))
                return symbolName; // Already emitted, return for C# P/Invoke

            var swiftTypeName = _structDecl.SwiftTypeName.ToString();

            // Use symbolName as Swift func name (unique per type via hash) to avoid
            // redeclaration errors when multiple types share the same simple name.
            // Add @MainActor when the type's == operator is actor-isolated (Swift 6 strict concurrency).
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(_structDecl, false);
            // Carry availability from the struct (and any nested ancestors) so the wrapper
            // compiles when the type is gated behind an OS version (e.g., iOS 16.4+).
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(null, _structDecl);
            _swiftWriter.WriteLine();
            WrapperEmitterHelpers.EmitCdeclAnnotation(_swiftWriter, symbolName, needsMainActor, availability);
            _swiftWriter.WriteLines($$"""
            public func {{symbolName}}(_ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer) -> UInt8 {
                let l = lhs.assumingMemoryBound(to: {{swiftTypeName}}.self).pointee
                let r = rhs.assumingMemoryBound(to: {{swiftTypeName}}.self).pointee
                return (l == r) ? 1 : 0
            }
            """);

            return symbolName;
        }

        /// <summary>
        /// Emits the C# P/Invoke declaration for the equality wrapper.
        /// </summary>
        private void EmitEqualityPInvoke(string symbolName)
        {
            _writer.WriteLines($$"""
            [global::System.Runtime.InteropServices.LibraryImport("{{_wrapperLibraryName}}", EntryPoint = "{{symbolName}}")]
            [return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]
            private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);
            """);
            _writer.WriteLine();
        }

        private void WriteSwiftEquatableImplementationWithSwiftEquals(bool refType)
        {
            // Try to emit @_cdecl equality wrapper (avoids CallConvSwift which crashes on NativeAOT).
            // Only works for non-generic types with wrapper library support.
            var eqSymbol = TryEmitSwiftEqualityWrapper();
            if (eqSymbol != null)
            {
                EmitEqualityPInvoke(eqSymbol);
            }

            // Equality comparison expression — use @_cdecl P/Invoke if available.
            // Reference types (projected as class): extract pointer from Payload SafeHandle.
            // Value types (frozen struct as C# struct): take address via Unsafe.AsPointer in unsafe block.
            string equalsExpr(string lhs, string rhs)
            {
                if (eqSymbol == null) return $"Swift.Runtime.SwiftEquatable.Equals({lhs}, {rhs})";
                if (refType)
                    return $"PInvoke_eq({lhs}.Payload.DangerousGetHandle(), {rhs}.Payload.DangerousGetHandle())";
                // Value types: wrap in unsafe + use Unsafe.AsPointer to pass stack addresses
                return $"_PInvoke_eq_value(ref {lhs}, ref {rhs})";
            }
            // For value types, emit a helper that wraps the unsafe pointer extraction
            bool needsValueHelper = eqSymbol != null && !refType;
            if (needsValueHelper)
            {
                _writer.WriteLines($$"""
                private static unsafe bool _PInvoke_eq_value(ref {{_typeNameWithGenerics}} lhs, ref {{_typeNameWithGenerics}} rhs)
                {
                    return PInvoke_eq(
                        (IntPtr)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref lhs),
                        (IntPtr)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref rhs));
                }
                """);
                _writer.WriteLine();
            }

            // Always write Equals and GetHashCode methods
            // Use simple name for is-check and error messages
            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && {{equalsExpr("this", "other")}};
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
                        return {{equalsExpr("left", "right")}};
                    }
                    """;
                }
                else
                {
                    equalityBody = $$"""
                    public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                    {
                        return {{equalsExpr("left", "right")}};
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
                        return !{{equalsExpr("left", "right")}};
                    }
                    """;
                }
                else
                {
                    inequalityBody = $$"""
                    public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                    {
                        return !{{equalsExpr("left", "right")}};
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
                {{(refType == true ? "if (other is null) return false;\n            " : "")}}return {{equalsExpr("this", "other")}};
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

        bool hasProtocolConformance = false;
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
                {
                    interfaces.Add(iface);
                    hasProtocolConformance = true;
                }
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
                        // Check if the protocol interface would actually be generated.
                        // If ALL non-static members reference unsupported modules (e.g., UIKit),
                        // the protocol handler won't emit the interface → CS0246.
                        if (!conformanceValidator.HasEmittableInterfaceMembers(protocolDecl))
                            continue;

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
                            {
                                interfaces.Add(genericIface);
                                hasProtocolConformance = true;
                            }
                            continue;
                        }
                    }
                }

                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics, conformance.Protocol.Module);
                if (emitted.Add(iface))
                {
                    interfaces.Add(iface);
                    hasProtocolConformance = true;
                }
            }
        }

        // PAT conformances: the generic interface (e.g., ITaggedAssociator<TSelf>) can't be
        // referenced without type arguments, so we don't add it to the interface list. But we
        // still need IExistentialBoxable so the concrete type can be boxed when passed through
        // an 'object' parameter (the PAT fallback from ExistentialHandler.GetPublicExistentialType).
        //
        // Multi-PAT guard: if a type conforms to multiple PAT protocols, the typeof(object)
        // dictionary key is ambiguous — we can't know which protocol the call site intended.
        // Rather than silently selecting the wrong witness table (which would pass the wrong
        // PWT into Swift and produce bad dispatch or a crash), we skip the PAT boxing path
        // entirely for multi-PAT conformers. They fall back to the pre-fix InvalidCastException,
        // which is a clear failure rather than silent corruption.
        if (!hasProtocolConformance && CountPatConformances(conformances, typeDatabase) == 1)
        {
            hasProtocolConformance = true;
        }

        // Add IExistentialBoxable if any protocol conformances were emitted.
        // This enables concrete types to be passed where protocol existentials are expected
        // (e.g., passing ECB where 'any BlockMode' is needed) via ExistentialContainerFactory.GetOrCreate.
        if (hasProtocolConformance)
            interfaces.Add("Swift.Runtime.IExistentialBoxable");

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

        // PAT conformances: keyed on typeof(object) because ExistentialHandler lowers
        // PAT protocol parameters to the literal 'object' C# type, so BoxAsExistential1
        // is called with TProtocol=object and the dictionary lookup uses typeof(object).
        // Skipped for multi-PAT types where the key is ambiguous (see GetImplementedInterfaces).
        if (CountPatConformances(conformances, typeDatabase) == 1)
        {
            foreach (var conformance in conformances)
            {
                if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var patRecord))
                    continue;
                if (patRecord.Kind != TypeRecordKind.Protocol)
                    continue;
                if (!patRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                    continue;
                if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor))
                    continue;
                entries.Add($"{{typeof(object), \"{conformance.ProtocolConformanceDescriptor}\"}}");
                break;
            }
        }

        return string.Join(",\n", entries);
    }

    /// <summary>
    /// Returns the list of C# protocol interface names for a type's conformances,
    /// using the same filtering as GenerateProtocolConformanceDictionaryEntries.
    /// Used by NativeAOT factory registration to pre-register conformance factories.
    /// </summary>
    public static List<string> GetConformanceProtocolNames(
        IEnumerable<TypeConformance> conformances,
        string moduleName,
        string typeName,
        ITypeDatabase typeDatabase)
    {
        var names = new List<string>();
        foreach (var conformance in conformances)
        {
            if (!ShouldEmitConformance(conformance, moduleName, typeDatabase))
                continue;
            if (typeDatabase.TryGetTypeRecord(conformance.Protocol, out var protoRecord) &&
                protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                continue;
            if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor))
                continue;
            var ifaceName = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeName, conformance.Protocol.Module);

            // Qualify nested protocol interfaces for module-level registration.
            // A nested protocol has 3+ parts in its ModuleQualifiedName (Module.Parent.Protocol).
            // The middle parts are parent type names that must prefix the C# interface name.
            var mqn = conformance.Protocol.ModuleQualifiedName;
            var parts = mqn.Split('.');
            if (parts.Length >= 3)
            {
                // Extract parent type names (everything between module and protocol name)
                var parentPrefix = string.Join(".", parts.Skip(1).Take(parts.Length - 2));
                ifaceName = $"{parentPrefix}.{ifaceName}";
            }

            names.Add(ifaceName);
        }

        // PAT conformances: register with "object" name to match the typeof(object) dictionary key.
        // Skipped for multi-PAT types where the key is ambiguous (see GetImplementedInterfaces).
        if (CountPatConformances(conformances, typeDatabase) == 1)
            names.Add("object");

        return names;
    }

    /// <summary>
    /// Counts how many PAT (Protocol with Associated Types) conformances a type has
    /// that could be boxed via the typeof(object) dictionary key. Used to guard against
    /// multi-PAT ambiguity: when count > 1, the typeof(object) key can't disambiguate
    /// which protocol's witness table to use, so PAT boxing is skipped entirely.
    /// </summary>
    private static int CountPatConformances(IEnumerable<TypeConformance> conformances, ITypeDatabase typeDatabase)
    {
        int count = 0;
        foreach (var conformance in conformances)
        {
            if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var record))
                continue;
            if (record.Kind != TypeRecordKind.Protocol)
                continue;
            if (!record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                continue;
            if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor))
                continue;
            count++;
            if (count > 1)
                break; // No need to keep counting
        }
        return count;
    }

    internal static bool ShouldEmitConformance(TypeConformance conformance, string moduleName, ITypeDatabase typeDatabase)
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
                p.WasEmitted &&
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
