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
            // Non-frozen structs project as a class whose SafeHandle adopts the wire handle's +1.
            WritePayloadConstructionSemantics(PayloadConstructionSemantics.Adopt);
            WriteMarshalToSwiftNonFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
            WriteBoxAsExistential1(emitBoxable);
            RecordTypeIfNonGeneric(PayloadConstructionSemantics.Adopt);
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
            // Frozen structs that carry reference fields project as a class whose NewFromPayload
            // Alloc+InitializeWithCopy takes a fresh +1 (Copy); pure value-field frozen structs are
            // read by value (Inline). Derive from the SAME predicate WriteNewFromPayloadFrozenStruct
            // branches on so the declared contract always matches the emitted construction shape.
            var frozenSemantics =
                MarshallingHelpers.IsFrozenStructProjectedAsClass(
                    _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName))
                    ? PayloadConstructionSemantics.Copy
                    : PayloadConstructionSemantics.Inline;
            WritePayloadConstructionSemantics(frozenSemantics);
            WriteMarshalToSwiftFrozenStruct();
            WriteGetProtocolConformanceDescriptor(pinvokeHelperContext);
            WriteBoxAsExistential1(emitBoxable);
            RecordTypeIfNonGeneric(frozenSemantics);
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
                // metadata + witness tables for any protocol-constrained generic params.
                // Use the type-metadata-accessor-specific arg/param list so the right PWTs
                // flow through. AddMetadataAccessorDeclaration transparently routes to
                // thin-mode (<= 3 args) or buffer-mode (> 3 args) on the helper side;
                // the call site below is identical in either case.
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();

                pinvokeHelperContext.AddMetadataAccessorDeclaration(libPath, _structDecl.MetadataAccessor);
            }
            else if (_swiftWriter != null && _emissionCtx != null &&
                     WrapperValidation.IsXCFrameworkMode(_typeDatabase))
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
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Swift
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
                else
                {
                    var symbol = MetadataWrapperEmitter.GetMetadataSymbolName(moduleName, moduleQualified);
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx, _structDecl);

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module. For an availability-gated
                    // type the wrapper returns null below its OS floor — surfaced here as a
                    // PlatformNotSupportedException instead of a zero TypeMetadata.
                    var metadataAvailability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(null, _structDecl);
                    _writer.WriteLines(MetadataWrapperEmitter.BuildGetTypeMetadataWithFallback(metadataAvailability, moduleQualified));
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
                        EntryPoint = _structDecl.MetadataAccessor,
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
                    EntryPoint = _structDecl.MetadataAccessor,
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
        /// Writes the NewFromPayload method for the struct.
        /// </summary>
        private void WriteNewFromPayloadFrozenStruct()
        {
            TypeRecord typeRecord = _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName);
            if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                // Constructor name uses _constructorName (may differ from _structDecl.Name if renamed).
                // Wrap the raw IntPtr in a SwiftHandle explicitly so the call resolves to the
                // private SwiftHandle-taking constructor. A raw-IntPtr payload constructor would
                // collide (CS0111) with — or be ambiguous (CS0121) against — a public single-arg
                // constructor whose parameter is itself IntPtr-shaped, e.g. a Swift `init(x: Int)`
                // projected as `(nint)` (nint IS IntPtr). The non-frozen-struct and class paths
                // already use this SwiftHandle indirection; this path must match.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    var obj = new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
                    Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                    return obj;
                }

                unsafe {{_constructorName}}(SwiftHandle handle)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)(IntPtr)handle, metadata);
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
        /// Generic types rely on constrained code paths for registration; the open-generic
        /// type definition is rooted via <see cref="ModuleEmissionContext.RecordOpenGenericISwiftObjectType"/>
        /// instead so the trimmer descriptor preserves its reflection metadata.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric(PayloadConstructionSemantics semantics)
        {
            if (_emissionCtx == null)
                return;

            if (_structDecl.IsGeneric)
            {
                _emissionCtx.RecordOpenGenericISwiftObjectType(_structDecl.Name, _structDecl.GenericParameters.Count);
                _emissionCtx.RecordOpenGenericPayloadSemantics(_structDecl.Name, _structDecl.GenericParameters.Count, semantics);
                return;
            }

            var typeAvailability = AvailabilityHelpers.MergeAvailabilityFromAncestors(null, _structDecl);
            _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics, typeAvailability);
            _emissionCtx.RecordPayloadSemantics(_typeNameWithGenerics, semantics);
            foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                _structDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
            {
                _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName, typeAvailability);
            }
        }

        /// <summary>
        /// Emits the explicit-interface <c>PayloadConstructionSemantics</c> property — the single
        /// declared source of truth (Finding 11) the marshal seam reads to balance Swift ARC and free
        /// the wire temporary correctly. Fully qualified so the generated body never collides with the
        /// like-named property in the emitting type's lookup scope.
        /// </summary>
        private void WritePayloadConstructionSemantics(PayloadConstructionSemantics semantics)
        {
            _writer.WriteLine(
                $"static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics => global::Swift.Runtime.PayloadConstructionSemantics.{semantics};");
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
                    // Closed-constrained existentials project to typed C# interfaces (e.g. ILabelledContainer<SwiftString>),
                    // but for a single-PAT conforming type the conformance dictionary is keyed on typeof(object) — so the
                    // typed lookup misses. Fall back to the object key for any generic-protocol lookup; if no object entry
                    // exists, the fallback is a no-op and the throw path runs.
                    if (!(typeof(TProtocol).IsGenericType && _protocolConformanceSymbols.TryGetValue(typeof(object), out symbolName)))
                    {
                        throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_structDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                    }
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the struct.
        /// For generic ISwiftObject types, also emits the eager-init pattern that mirrors
        /// SwiftArray.cs so NativeAOT (ILC) can statically reach SwiftObjectHelper&lt;Self&gt;.
        /// GetTypeMetadata for each closed instantiation. Without this, ILC's reachability
        /// analysis can't prove the explicit interface implementations are called and
        /// trims them, leaving generic wrappers like MeshBuffer&lt;T&gt; failing on device.
        /// </summary>
        private void WriteStaticConstructor()
        {
            bool isGeneric = _structDecl.IsGeneric;
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
        /// Emits a Swift-Sendable XML doc remark and the <c>[SwiftSendable]</c> marker
        /// attribute when the Swift type's conformance list contains <c>Swift.Sendable</c>.
        /// .NET has no native equivalent of Sendable, so the projection is purely informational
        /// — but losing the signal entirely (current 0.10.0 behaviour) forces consumers back
        /// into the swiftinterface to decide whether locking is needed.
        /// </summary>
        internal static void EmitSwiftSendableAnnotation(CSharpWriter csWriter, TypeDecl typeDecl)
        {
            if (!IsSwiftSendable(typeDecl))
                return;
            csWriter.WriteLine("/// <remarks>");
            csWriter.WriteLine("/// The underlying Swift type conforms to <c>Sendable</c>; instances may be shared");
            csWriter.WriteLine("/// across .NET threads without external synchronization.");
            csWriter.WriteLine("/// </remarks>");
            csWriter.WriteLine("[global::Swift.SwiftSendable]");
        }

        /// <summary>
        /// Emits a <c>@MainActor</c> XML doc remark and the <c>[SwiftMainActor]</c> marker
        /// attribute when the Swift type is <c>@MainActor</c>-isolated. The isolation is a
        /// compile-time-only constraint on the Swift side (the <c>@_cdecl</c> wrapper carries
        /// <c>@MainActor</c>), so without this the requirement that members be called on the
        /// main thread is invisible to a C# consumer. The marker is purely informational; the
        /// per-member <c>MainActorGuard.AssertMainThread()</c> call enforces it at runtime in
        /// Debug builds.
        /// </summary>
        internal static void EmitSwiftMainActorAnnotation(CSharpWriter csWriter, TypeDecl typeDecl)
        {
            if (!typeDecl.IsMainActorIsolated)
                return;
            csWriter.WriteLine("/// <remarks>");
            csWriter.WriteLine("/// The underlying Swift type is <c>@MainActor</c>-isolated; its members must be");
            csWriter.WriteLine("/// called on the platform main thread.");
            csWriter.WriteLine("/// </remarks>");
            csWriter.WriteLine("[global::Swift.Runtime.SwiftMainActor]");
        }

        /// <summary>
        /// Emits the per-member <c>@MainActor</c> isolation <c>&lt;remarks&gt;</c> line and the
        /// <c>[SwiftMainActor]</c> marker attribute. Surfaced on an individual member (in addition to any
        /// type-level attribute) so the main-thread requirement is visible on that member's IntelliSense
        /// and via reflection. Emission only — the caller decides whether the member is isolated via
        /// <see cref="WrapperValidation.NeedsMainActorAnnotation"/> (the single isolation oracle). Must
        /// run after the member's XML doc summary and before its signature/availability attributes.
        /// </summary>
        internal static void EmitSwiftMainActorMemberAnnotation(CSharpWriter csWriter)
        {
            csWriter.WriteLine("/// <remarks>");
            csWriter.WriteLine("/// Maps to a Swift <c>@MainActor</c>-isolated declaration; call on the platform main thread.");
            csWriter.WriteLine("/// </remarks>");
            csWriter.WriteLine("[global::Swift.Runtime.SwiftMainActor]");
        }

        private static bool IsSwiftSendable(TypeDecl typeDecl)
        {
            // Sendable is one of the four marker protocols (Sendable / Copyable / Escapable /
            // SendableMetatype). Only Sendable carries the cross-thread guarantee — the other
            // three are about lifetime / move semantics and have no .NET analogue worth surfacing.
            var conformances = typeDecl switch
            {
                StructDecl s => s.Conformances,
                ClassDecl c => c.Conformances,
                EnumDecl e => e.Conformances,
                _ => null
            };
            if (conformances == null) return false;
            foreach (var c in conformances)
            {
                if (c.Protocol.Name == "Sendable") return true;
                if (c.Protocol.ModuleQualifiedName == "Swift.Sendable") return true;
            }
            return false;
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
            : this(csWriter, structDecl, refType, typeNameWithGenerics, false, false, typeDatabase: null)
        {
        }

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
            : this(csWriter, structDecl, refType, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator, typeDatabase: null)
        {
        }

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, ITypeDatabase? typeDatabase)
        {
            _writer = csWriter;
            _structDecl = structDecl;
            _typeNameWithGenerics = typeNameWithGenerics;

            // Filter Equatable / Hashable conformances through the conditional-witness gate so
            // generic types whose Swift conformance is conditional (e.g.
            // `extension Foo : Equatable where T : Equatable`) drop their typed equality / hash
            // surface. The ABI JSON does not preserve the where-clause; without this filter
            // the generated `Equals(T?)` / `GetHashCode()` would dispatch to a runtime
            // protocol-witness lookup that traps when the consumer instantiates with a
            // non-Equatable T.
            bool equatableUnconditional = EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(
                _structDecl, typeDatabase, EquatableConformanceHelper.SwiftEquatableModuleQualifiedName);
            bool hashableUnconditional = EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(
                _structDecl, typeDatabase, EquatableConformanceHelper.SwiftHashableModuleQualifiedName);

            _implementsEquatable = equatableUnconditional
                && _structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            // OptionSet and RawRepresentable imply Hashable in Swift — ABI may not list it explicitly.
            bool directlyDeclaredHashable = _structDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
                (c.Protocol.Name == "Hashable" && string.IsNullOrEmpty(c.Protocol.Module)) ||
                c.Protocol.Name == "OptionSet" ||
                c.Protocol.Name == "RawRepresentable");
            // Require explicit Hashable conformance to route through SwiftHashable.GetHashCode.
            // Inferring Hashable from Equatable is unsafe even with synthesized `==`: Swift's
            // synthesized Equatable compares stored properties semantically (e.g., String
            // performs NFC-normalised value comparison), while the runtime's structural-byte
            // FNV-1a fallback hashes the marshalled bytes — equal values with reference-typed
            // fields (String, Array, class storage) can marshal to different bytes and produce
            // different hashes, breaking the Equals/GetHashCode contract. Equatable-only types
            // get the conservative `return 0;` stub from WriteHashCodeImplementation, which is
            // contract-correct (all values hash the same, lookups degrade to O(n)).
            _implementsHashable = hashableUnconditional && directlyDeclaredHashable;
            _isRefType = refType;
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        /// <summary>
        /// Constructor with Swift wrapper support. When swiftWriter and emissionContext are provided,
        /// emits @_cdecl equality wrappers instead of using SwiftEquatable.Equals (which uses
        /// CallConvSwift and crashes on NativeAOT).
        /// </summary>
        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, SwiftWriter? swiftWriter, ModuleEmissionContext? emissionContext, string? wrapperLibraryName, ITypeDatabase? typeDatabase = null)
            : this(csWriter, structDecl, refType, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator, typeDatabase)
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

            // equality helpers live in the shared `_equality` bucket (also written by ClassHandler and EnumHandler). One helper per struct type; symbol name from GetEqualitySymbolName is unique per type, so cross-emitter collisions in the bucket are impossible by construction.
            if (!_emissionContext.TryAddEqualityWrapperSymbol(symbolName))
                return symbolName; // Already emitted, return for C# P/Invoke

            var swiftTypeName = _structDecl.SwiftTypeName.ToString();

            // Use symbolName as Swift func name (unique per type via hash) to avoid
            // redeclaration errors when multiple types share the same simple name.
            // Add @MainActor when the type's == operator is actor-isolated (Swift 6 strict concurrency).
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(_structDecl, false);
            // Carry availability from the `==` operator (so retroactive Equatable conformances
            // get the operator's @available floor, not just the struct's), merged with any
            // nested ancestor availability.
            var equalityOperator = _structDecl.Operators
                .FirstOrDefault(op => op.OperatorSymbol == "==" && op.Kind == OperatorKind.Binary);
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                equalityOperator?.AvailabilityAnnotations, _structDecl);
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
            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
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
                    // _PInvoke_eq_pinned brackets DangerousAddRef/DangerousRelease around both
                    // SafeHandles so a concurrent GC finalization cannot free the Swift heap
                    // payload between Payload.DangerousGetHandle() and the Swift function entry.
                    return $"_PInvoke_eq_pinned({lhs}, {rhs})";
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
            // For reference types, emit a helper that pins both SafeHandles around the PInvoke.
            // Without the AddRef bracket, GC finalization of either side between the handle
            // access and Swift function entry can free the Swift heap object that the Swift
            // wrapper is about to dereference. The bracket gives the standard SafeHandle
            // GC-pinning guarantee that property getters already enforce.
            bool needsRefHelper = eqSymbol != null && refType;
            if (needsRefHelper)
            {
                _writer.WriteLines($$"""
                private static bool _PInvoke_eq_pinned({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    bool _eqAddedLeft = false;
                    bool _eqAddedRight = false;
                    try
                    {
                        left.Payload.DangerousAddRef(ref _eqAddedLeft);
                        right.Payload.DangerousAddRef(ref _eqAddedRight);
                        return PInvoke_eq(left.Payload.DangerousGetHandle(), right.Payload.DangerousGetHandle());
                    }
                    finally
                    {
                        if (_eqAddedRight) right.Payload.DangerousRelease();
                        if (_eqAddedLeft) left.Payload.DangerousRelease();
                    }
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

        // Classes, structs, and enums-as-class all participate in IEquatable<T>.
        // Enums with associated values are projected as C# classes that go through
        // the @_cdecl Swift equality wrapper just like reference-projected structs.
        bool canEmitEquatable = typeDecl is ClassDecl or StructDecl or EnumDecl;

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

                if (!ShouldEmitConformanceInterface(conformance, moduleName, typeDatabase))
                    continue;

                // Drop IEquatable<T> on generic types whose Equatable conformance can't be proven
                // unconditional via the type's generic-parameter constraints. Swift expresses these
                // through `extension Foo : Equatable where T : Equatable`, but the ABI JSON loses
                // the where clause. Emitting IEquatable<T> + the witness-bound P/Invoke
                // unconditionally crashes the Swift runtime when the consumer instantiates with a
                // non-Equatable T. See EquatableConformanceHelper for the refinement-walk rule.
                if (!EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(
                        typeDecl, typeDatabase, "Swift.Equatable"))
                    continue;

                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics, conformance.Protocol.Module, moduleName);
                if (emitted.Add(iface))
                {
                    interfaces.Add(iface);
                    hasProtocolConformance = true;
                }
            }
            else
            {
                // The dictionary gate is broader than the interface gate — it lets
                // cross-module-with-members conformances through so the descriptor symbol
                // lands in _protocolConformanceSymbols (required by swift_getWitnessTable
                // at existential-box time). Track that so IExistentialBoxable is still
                // appended even when the C# interface side opts out.
                if (ShouldEmitConformanceDictionary(conformance, typeDatabase))
                    hasProtocolConformance = true;

                // All other protocol conformances: emit interface only when the stricter
                // gate accepts it (CS0535 protection for cross-module member stubs).
                if (!ShouldEmitConformanceInterface(conformance, moduleName, typeDatabase))
                    continue;

                // Validate protocol can be fully implemented if validator is provided
                if (conformanceValidator != null)
                {
                    // Use ModuleQualifiedName for precision when same-name protocols exist
                    var protocolDecl = conformanceValidator.FindProtocol(conformance.Protocol.ModuleQualifiedName);

                    // Cross-module protocols (e.g., Swift.Equatable) return null from FindProtocol
                    // since they're not in moduleDecl.Protocols. These are handled above for Equatable.
                    // For other cross-module protocols, we trust ShouldEmitConformanceInterface already validated.
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
                            var resolvedSelfModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
                            var baseName = NameProvider.GetInterfaceName(conformance.Protocol.Name,
                                typeNameWithGenerics, resolvedSelfModule, moduleName);
                            baseName = QualifyNestedProtocolInterface(baseName, conformance.Protocol);
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

                var resolvedModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
                var iface = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeNameWithGenerics, resolvedModule, moduleName);
                iface = QualifyNestedProtocolInterface(iface, conformance.Protocol);
                if (emitted.Add(iface))
                {
                    interfaces.Add(iface);
                    hasProtocolConformance = true;
                }
            }
        }

        // Closed-constrained PAT projection (Case 1: concrete-arg `any P<X>` nominal-assignability): when a conformer's PAT bindings are all
        // concrete (e.g. StringLabel: LabelledContainer where Label == String), emit the
        // closed generic interface in the implements list so consumers can pass
        // `new StringLabel(...)` where `ILabelledContainer<SwiftString>` is expected.
        // ShouldEmitConformanceInterface() above filters PATs out of the main loop, so without this
        // step the conformer would surface as IExistentialBoxable-only and the typed call
        // site would fail CS0029.
        //
        // Open PATs (e.g. GenericContainer<U>: LabelledContainer where Label == U) are
        // explicitly excluded by TryResolveClosedPatBindings — the closed interface depends
        // on a conformer-side type parameter and the typeof(object) PAT box still applies.
        //
        // Pairs with the typed-PAT runtime fallback at the boxing site (this file ~line 421)
        // and the multi-PAT guard below.
        if (conformanceValidator != null)
        {
            foreach (var conformance in conformances)
            {
                if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var protoRecord))
                    continue;
                if (!protoRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                    continue;
                if (protoRecord.Kind != TypeRecordKind.Protocol)
                    continue;

                // Self-requirement protocols project to `IFoo<TSelf> where TSelf : IFoo<TSelf>`
                // (CRTP) — the associated types are folded into Self and don't appear in the
                // C# interface signature. Substituting an associated-type binding for TSelf
                // (e.g. `IHashFunction<SHA256Digest>` instead of `IHashFunction<SHA256>`)
                // produces CS0311 (TSelf constraint unsatisfiable) and CS0535 (missing protocol
                // method impls) on every CryptoKit hash function. The Self-requirement branch
                // in the main loop (~line 1006) already owns the correct emission for these
                // protocols, so the closed-PAT projection must yield to it.
                if (protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    continue;

                var protocolDecl = conformanceValidator.FindProtocol(conformance.Protocol.ModuleQualifiedName);
                if (protocolDecl == null)
                    continue;

                // Don't double-emit if Self-requirement path already covered this conformance,
                // or if HasEmittableInterfaceMembers/CanFullyImplementProtocol would reject it.
                if (!conformanceValidator.HasEmittableInterfaceMembers(protocolDecl))
                    continue;
                if (!conformanceValidator.CanFullyImplementProtocol(typeDecl, protocolDecl))
                    continue;

                if (!conformanceValidator.TryResolveClosedPatBindings(typeDecl, protocolDecl, out var bindings))
                    continue;

                var resolvedModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
                var baseName = NameProvider.GetInterfaceName(
                    conformance.Protocol.Name, typeNameWithGenerics, resolvedModule, moduleName);
                baseName = QualifyNestedProtocolInterface(baseName, conformance.Protocol);
                var closedIface = $"{baseName}<{string.Join(", ", bindings)}>";
                if (emitted.Add(closedIface))
                {
                    interfaces.Add(closedIface);
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

        // AsyncSequence → IAsyncEnumerable<TElement> adoption. Without this,
        // `await foreach (var x in seq)` fails to compile for every Swift type
        // that conforms to AsyncSequence (StoreKit Transactions, MusicKit
        // MusicSubscription.Updates, async event streams, ...). The
        // GetAsyncEnumerator method body is emitted by the corresponding type
        // handler — adding the interface here keeps interface declaration and
        // member emission in sync.
        var asyncSeq = new AsyncSequenceHandler(typeDatabase);
        if (AsyncSequenceHandler.IsAsyncSequence(typeDecl) &&
            asyncSeq.TryResolveElementCSharpType(typeDecl, out var elementCSharpType))
        {
            var ifaceName = $"global::System.Collections.Generic.IAsyncEnumerable<{elementCSharpType}>";
            if (emitted.Add(ifaceName))
                interfaces.Add(ifaceName);
        }

        return interfaces;
    }

        /// <summary>
        /// Returns the C# interface set already implemented by a cross-module class parent and
        /// its ancestors. Walks <see cref="TypeRecord.SuperclassTypeName"/> via the type database
        /// (cycle-guarded) and projects each conformance through <see cref="NameProvider.GetInterfaceName"/>
        /// so the same dedup vocabulary applies as the same-module path. Used by
        /// <c>ClassHandler</c> to filter the derived class's interface declaration list and avoid
        /// re-listing protocols the parent already exposes (e.g. <c>IRealityCoordinateSpace</c>
        /// shared by every <c>Entity</c> subclass).
        /// </summary>
        public static HashSet<string> GetCrossModuleInheritedInterfaces(
            TypeRecord parentRecord,
            ITypeDatabase typeDatabase,
            string currentModuleName = "")
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = parentRecord;
            while (current != null && visited.Add(current.SwiftTypeName.ModuleQualifiedName))
            {
                var parentName = current.CSharpTypeName.FullyQualifiedName;
                var conformances = current.ProtocolConformances;
                if (conformances != null)
                {
                    foreach (var protocolName in conformances)
                    {
                        // Hashable is a marker interface (ISwiftHashable) for PWT lookup, not a
                        // user-facing C# interface — never appears in the derived class's list.
                        if (protocolName.ModuleQualifiedName == "Swift.Hashable" ||
                            (protocolName.Name == "Hashable" && string.IsNullOrEmpty(protocolName.Module)))
                            continue;

                        // Equatable is special-cased to IEquatable<T>: the parent's instantiation is
                        // IEquatable<ParentType>, distinct from the derived class's IEquatable<DerivedType>,
                        // so we only filter the parent's exact instantiation.
                        var resolvedProtocolModule = ResolveProtocolEmissionModule(protocolName, typeDatabase);
                        var iface = NameProvider.GetInterfaceName(protocolName.Name, parentName, resolvedProtocolModule, currentModuleName);
                        iface = QualifyNestedProtocolInterface(iface, protocolName);
                        result.Add(iface);
                    }
                }

                // Walk to the parent's superclass via the type database. Older module-DB XMLs that
                // predate ProtocolConformances or SuperclassTypeName fail the gates here and the
                // walk simply terminates — we lose some dedup precision but still produce valid C#.
                if (current.SuperclassTypeName == null) break;
                if (!typeDatabase.TryGetTypeRecord(current.SuperclassTypeName, out var grandparent)) break;
                if (grandparent.Kind != TypeRecordKind.Class) break;
                current = grandparent;
            }

            // The parent assembly's binding emitted IExistentialBoxable when its conformance list
            // was non-empty. Mirror that here so the derived class doesn't re-declare it.
            if (result.Count > 0)
                result.Add("Swift.Runtime.IExistentialBoxable");

            return result;
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
            // Dictionary entries don't need C# stubs — only the conformance descriptor
            // symbol for swift_getWitnessTable. A cross-module-with-members conformance
            // (e.g. RealityFoundation.AnchorEntity : RealityFoundation.HasAnchoring whose
            // mangled name umbrella-encodes the module as RealityKit) must still appear in
            // _protocolConformanceSymbols so the existential box can resolve at runtime.
            if (!ShouldEmitConformanceDictionary(conformance, typeDatabase))
                continue;

            // Skip Self-requirement protocols — no proxy, no EveryProtocol, no runtime PWT lookup.
            // EXCEPTION: Swift.Hashable is consumed at runtime by SwiftHashable.GetHashCode,
            // which performs a PWT lookup keyed on typeof(ISwiftHashable). Without this entry
            // the lookup falls back to a structural FNV hash over marshalled bytes, which is
            // wrong for SafeHandle-backed reference types (the bytes are heap pointers, so
            // two equal-by-content instances hash differently). Equatable's runtime path goes
            // through the @_cdecl == wrapper, so it does not need a dict entry.
            bool isHashable = conformance.Protocol.ModuleQualifiedName == "Swift.Hashable"
                || (conformance.Protocol.Name == "Hashable" && string.IsNullOrEmpty(conformance.Protocol.Module));
            if (typeDatabase.TryGetTypeRecord(conformance.Protocol, out var protoRecord) &&
                protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) &&
                !isHashable)
                continue;

            var resolvedProtocolModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
            var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeName, resolvedProtocolModule, moduleName);
            protocol = QualifyNestedProtocolInterface(protocol, conformance.Protocol);
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
            // Pair with GenerateProtocolConformanceDictionaryEntries — registration uses the
            // dictionary-level gate so a cross-module-with-members descriptor entry gets a
            // matching factory pre-registration on NativeAOT.
            if (!ShouldEmitConformanceDictionary(conformance, typeDatabase))
                continue;
            // Mirror GenerateProtocolConformanceDictionaryEntries: skip Self-requirement
            // protocols EXCEPT Swift.Hashable. Hashable's witness table is consumed at
            // runtime by SwiftSet/SwiftDictionary (and SwiftHashable.GetHashCode) via a
            // PWT lookup keyed on typeof(ISwiftHashable). Reference (class) Hashable
            // conformances resolve to a Hashable record that carries HasSelfRequirement
            // (Hashable: Equatable), so without this exception they would be dropped from
            // module-init pre-registration — leaving Set<ClassType>/[ClassKey: V] to fall
            // back to AOT-incompatible reflection on NativeAOT. The conformance dictionary
            // already keeps the entry via the same exception; this keeps the two consistent.
            bool isHashable = conformance.Protocol.ModuleQualifiedName == "Swift.Hashable"
                || (conformance.Protocol.Name == "Hashable" && string.IsNullOrEmpty(conformance.Protocol.Module));
            if (typeDatabase.TryGetTypeRecord(conformance.Protocol, out var protoRecord) &&
                protoRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) &&
                !isHashable)
                continue;
            if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor))
                continue;
            var resolvedConformanceModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
            var ifaceName = NameProvider.GetInterfaceName(conformance.Protocol.Name, typeName, resolvedConformanceModule, moduleName);
            ifaceName = QualifyNestedProtocolInterface(ifaceName, conformance.Protocol);
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

    /// <summary>
    /// Returns the module name to use when emitting a protocol's C# interface reference.
    /// Apple's `@_exported import` umbrellas (e.g. RealityKit re-exporting RealityFoundation)
    /// cause the protocol's mangled name to encode the umbrella module, so
    /// <c>conformance.Protocol.Module</c> reads as the umbrella ("RealityKit") rather than the
    /// declaring module ("RealityFoundation"). The TypeDatabase's umbrella fallback resolves
    /// the lookup to the source module's record, whose <see cref="CSharpTypeName.Namespace"/>
    /// is what the rest of the emitter (classes, structs, enums) already qualifies with.
    /// Mirroring that here keeps the inheritance list consistent with member type references.
    /// </summary>
    internal static string ResolveProtocolEmissionModule(TypeConformance conformance, ITypeDatabase typeDatabase)
        => ResolveProtocolEmissionModule(conformance.Protocol, typeDatabase);

    /// <summary>
    /// Overload for any code path that names a protocol via a <see cref="SwiftTypeName"/>
    /// (generic parameter constraints, witness-table lookups, existential lists). Same
    /// umbrella-fallback semantics as the <see cref="TypeConformance"/> variant.
    /// </summary>
    internal static string ResolveProtocolEmissionModule(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            var ns = record.CSharpTypeName.Namespace;
            if (!string.IsNullOrEmpty(ns))
                return ns;
        }
        return protocolTypeName.Module;
    }

    /// <summary>
    /// Prepends the nested-parent type path (e.g. "AssistantSchemas.") onto an
    /// already-I-prefixed interface name when the protocol is nested inside one
    /// or more parent types under its declaring module. Nested protocols emit
    /// inside their parent's C# namespace facade or nested class scope, so a
    /// bare <c>IFoo</c> reference from a sibling type scope (e.g. a singular
    /// umbrella struct that mirrors the plural namespace facade in AppIntents
    /// 0.12 <c>AssistantSchemas</c>) is unresolvable. Returns the original
    /// name unchanged for non-nested protocols.
    /// </summary>
    internal static string QualifyNestedProtocolInterface(string ifaceName, SwiftTypeName protocolTypeName)
    {
        // ModuleQualifiedName parts: Module . [Parent...] . ProtocolLeaf
        // A nested protocol has 3+ parts; the middle ones are the parent type names.
        var parts = protocolTypeName.ModuleQualifiedName.Split('.');
        if (parts.Length < 3)
            return ifaceName;
        var parentPrefix = string.Join(".", parts.Skip(1).Take(parts.Length - 2));
        // `ifaceName` may already be cross-module qualified by NameProvider
        // (e.g. "OtherModule.IFoo"). The parent type prefix must sit BETWEEN
        // the module namespace and the leaf interface name — prepending the
        // whole string would produce "Parent.OtherModule.IFoo" which is
        // unresolvable. Bare names (no dot) take the prefix at the front.
        var lastDot = ifaceName.LastIndexOf('.');
        if (lastDot < 0)
            return $"{parentPrefix}.{ifaceName}";
        return $"{ifaceName.Substring(0, lastDot)}.{parentPrefix}.{ifaceName.Substring(lastDot + 1)}";
    }

    /// <summary>
    /// Shared baseline gate for both the conformance-descriptor dictionary path and the
    /// C# interface inheritance path. Filters out unknown protocols, PATs, non-protocols,
    /// and well-known runtime protocols that have direct runtime mappings (Swift.Error → AnyError).
    /// Does NOT apply the cross-module-with-members skip — that is interface-specific and lives
    /// in <see cref="ShouldEmitConformanceInterface"/>.
    /// </summary>
    internal static bool ShouldEmitConformanceDictionary(TypeConformance conformance, ITypeDatabase typeDatabase)
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

        return true;
    }

    /// <summary>
    /// Stricter gate used to decide whether to add a protocol to a concrete type's C# interface
    /// inheritance list. Layers a cross-module-with-members skip on top of the dictionary gate:
    /// cross-module protocols with emittable members cannot be safely declared as inheritance
    /// because the generator cannot produce stubs for cross-module protocol requirements
    /// (CS0535 at compile time). The dictionary / IExistentialBoxable path is broader — it
    /// only needs the conformance descriptor symbol, not C# member stubs — so a conformance
    /// can land in <c>_protocolConformanceSymbols</c> without surfacing as a direct interface.
    /// Same-module protocols are validated by <c>CanFullyImplementProtocol</c> in the caller.
    /// </summary>
    internal static bool ShouldEmitConformanceInterface(TypeConformance conformance, string moduleName, ITypeDatabase typeDatabase)
    {
        if (!ShouldEmitConformanceDictionary(conformance, typeDatabase))
            return false;

        // Equatable/Hashable always project, even when the record is unavailable — their
        // dictionary-only behaviour is handled by the caller.
        if (conformance.Protocol.ModuleQualifiedName == "Swift.Equatable")
            return true;
        if (conformance.Protocol.ModuleQualifiedName == "Swift.Hashable" || (conformance.Protocol.Name == "Hashable" && string.IsNullOrEmpty(conformance.Protocol.Module)))
            return true;

        // ShouldEmitConformanceDictionary already filtered missing records; this lookup
        // is for the cross-module gate that consumes EmittedMemberCount.
        if (!typeDatabase.TryGetTypeRecord(conformance.Protocol, out var record))
            return false;

        // The protocol's mangled name encodes its umbrella module (e.g. RealityKit for
        // RealityFoundation.HasAnchoring); the type record's CSharpTypeName.Namespace
        // is the real declaring module. Compare against the resolved module so a
        // same-module-via-umbrella conformance is not falsely treated as cross-module.
        var resolvedProtocolModule = ResolveProtocolEmissionModule(conformance, typeDatabase);
        if (resolvedProtocolModule != moduleName && conformance.Protocol.Module != moduleName)
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
            out string propertyName,
            Dictionary<string, string>? enumPropertyRenames = null)
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
            // A `description` property renamed away from a colliding enum case (DescriptionValue)
            // must be referenced under that name here too. No-op unless the enum channel maps it.
            propertyName = NameProvider.GetFinalMemberName(propertyName, enumPropertyRenames);
            return true;
        }

        /// <summary>
        /// Emits 'public override string ToString() => PropertyName;' if the type has a description property.
        /// </summary>
        public static void EmitToStringIfDescriptionExists(
            IndentedTextWriter writer,
            TypeDecl typeDecl,
            Dictionary<string, string>? renames,
            Dictionary<string, string>? enumPropertyRenames = null)
        {
            if (TryGetDescriptionPropertyName(typeDecl, renames, out var propertyName, enumPropertyRenames))
            {
                writer.WriteLine($"public override string ToString() => {propertyName};");
                writer.WriteLine();
            }
        }
    }
}
