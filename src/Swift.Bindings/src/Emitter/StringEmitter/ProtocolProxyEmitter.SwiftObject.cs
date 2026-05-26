// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitISwiftObjectImplementation(CSharpWriter writer, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        var witnessTableSymbol = GetWitnessTableSymbol(protocolDecl);
        var wrapperLibPath = _typeDatabase.AsyncLibraryName ?? _typeDatabase.GetLibraryPath(_moduleName);

        // GetTypeMetadata source: class-bound (NSObjectProtocol-rooted EveryObjCProtocol)
        // proxies must NOT return EveryProtocol's metadata — that's a different Swift
        // type's metadata. Read directly from the EveryObjCProtocol accessor without
        // caching into the EveryProtocol static slot (priming that slot from the ObjC
        // code path would poison later opaque-layout proxies — same invariant as the
        // constructor's metadataInitBlock).
        var getTypeMetadataBody = _useObjCBase
            ? $"return TypeMetadata.FromHandle(NativeMethods.{GetMetadataMethodName}());"
            : "// Proxy classes don't have their own Swift metadata\n                // They use the EveryProtocol metadata\n                return EveryProtocol.GetTypeMetadata();";

        // NewFromPayload: Swift→C# wrap factory. Symmetric to the receiver-side fix —
        // for class-bound proxies (AnyObject-rooted OR NSObjectProtocol-rooted EveryObjCProtocol),
        // Swift passes a 2-word existential (`[classRef][witnessTable]`), so dereferencing
        // a full 5-word ExistentialContainer1 would over-read stack memory. Read exactly
        // the two wire words and preserve BOTH — Payload0 carries the class reference
        // (proxy handle when the existential IS our proxy, foreign class ref otherwise),
        // and Payload1 carries the witness table the sender used. Preserving the wire
        // witness table is required so that a foreign Swift/ObjC implementation of the
        // protocol round-trips through its own witness table when marshalled back to Swift
        // (synthesizing our `ProtocolWitnessTableHandle` here would dispatch foreign
        // payloads through our @_cdecl wrappers, whose TryGetProxy<T> lookup would silently
        // miss and return zero — losing the foreign implementation's behaviour). For the
        // our-proxy-on-the-wire case, word 1 IS our witness table already (set by the ctor
        // that originated the proxy), so preserving is also correct.
        var useClassBoundContainerLayout = IsProtocolClassBound(protocolDecl) || _useObjCBase;

        // A proxy that ADOPTED a Swift-returned `any P` / `(any P)?` existential at +1
        // (constructed with `ownsContainer: true` by the owned-return marshalling paths)
        // owns, for the OPAQUE (5-word) layout, the container's value-witness retains —
        // the inline class reference for a class conformer, or the heap box for a boxed
        // value conformer — and must release them on Dispose/finalize, or the payload's
        // +1 is orphaned. Destroying through the opaque existential's own value-witness
        // table (resolved by protocol count) releases either shape correctly. The release
        // is gated on _ownsContainer: borrowed parameter wraps, payload-pointer reads,
        // C#-impl-backed proxies (lifetime anchored by ProxyLifetimeTracker), and
        // externally constructed / zeroed containers do NOT own a +1 and must not be
        // destroyed. The class-bound / ObjC 2-word [classRef][witnessTable] layout is a
        // separate release shape and keeps the original no-release Dispose untouched.
        var disposeOwnsContainer = !useClassBoundContainerLayout;
        var disposeAndFinalizer = disposeOwnsContainer
            ? $$"""
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                GC.SuppressFinalize(this);
                ReleaseAdoptedSwiftContainer();
                // C#-impl-backed proxies anchor their +1 via ProxyLifetimeTracker, so
                // Dispose only unregisters the strong root here; the ARC release waits
                // for impl GC (tracker finalizer) or Swift's last release (deinit
                // callback). The null-safe receivers ensure an Unregister'd handle does
                // not throw across the [UnmanagedCallersOnly] boundary if Swift
                // dispatches concurrently.
                if (_everyProtocolHandle != IntPtr.Zero)
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
            }

            /// <summary>
            /// Finalizer — releases an adopted (<c>_ownsContainer</c>) existential container
            /// if the consumer never called <see cref="Dispose"/>. Non-owning proxies
            /// suppress finalization in their constructor, so this only runs for owners.
            /// </summary>
            ~{{proxyClassName}}()
            {
                if (_disposed) return;
                _disposed = true;
                ReleaseAdoptedSwiftContainer();
            }

            // Releases the value-witness retains of an ADOPTED Swift-returned existential
            // container. Gated to proxies that actually own a +1 (_ownsContainer == true,
            // set only by the owned-return marshalling paths). C#-impl-backed proxies,
            // borrowed parameter wraps, payload-pointer reads, and externally constructed
            // or zeroed containers do NOT own a +1 — destroying their (possibly borrowed
            // or null-metadata) container would be a use-after-free / SIGSEGV. Destroying
            // through the opaque existential's own VWT releases an inline class reference
            // or a boxed value payload alike.
            private void ReleaseAdoptedSwiftContainer()
            {
                if (!_ownsContainer)
                    return;
                try
                {
                    fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                    {
                        var existentialMetadata = Swift.Runtime.TypeMetadata.GetExistentialTypeMetadata(_swiftContainer.Count);
                        Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)containerPtr, existentialMetadata);
                    }
                }
                catch
                {
                    // Existential metadata unavailable (e.g. SwiftBindingsRuntime not
                    // loaded under unit tests) — skip the destroy rather than throw
                    // from Dispose/finalize.
                }
            }
            """
            : """
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                // No finalizer to suppress — the proxy no longer owns any unmanaged
                // resources directly. ProxyLifetimeTracker owns the +1 release path
                // via the impl-keyed ConditionalWeakTable; dropping it here would
                // deallocate the Swift instance while in-flight Swift code may still
                // be dispatching into this proxy. Explicit Dispose unregisters the
                // strong root so further Swift callbacks route to the receivers'
                // null-impl guard (silent no-op / zeroed-buffer default) — the ARC
                // release waits for either impl GC (tracker finalizer) or Swift's
                // last release (deinit callback), whichever comes first. The
                // null-safe receivers (Codex P0/P1 #3 fix) ensure that an
                // Unregister'd handle does NOT throw across the
                // [UnmanagedCallersOnly] boundary if Swift dispatches concurrently.
                if (_everyProtocolHandle != IntPtr.Zero)
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
            }
            """;
        var newFromPayloadBody = useClassBoundContainerLayout
            ? $"// Class-bound (AnyObject-rooted or EveryObjCProtocol): Swift passes a 2-word\n                // existential ([classRef][witnessTable]). Read exactly two wire words;\n                // preserve both so foreign implementations round-trip through their own WT.\n                var wordPtr = (IntPtr*)payload;\n                var container = new ExistentialContainer1\n                {{\n                    Payload0 = wordPtr[0],\n                    Payload1 = wordPtr[1],\n                }};\n                return new {proxyClassName}(container);"
            : $"// Opaque (5-word [payload0][payload1][payload2][metadata][WT]) existential\n                var container = *(ExistentialContainer1*)payload;\n                return new {proxyClassName}(container);";

        writer.WriteLines($$"""
            #region ISwiftObject Implementation

            /// <summary>
            /// Gets the protocol witness table handle for EveryProtocol conforming to {{protocolDecl.Name}}.
            /// </summary>
            public static IntPtr ProtocolWitnessTableHandle
            {
                get
                {
                    if (_protocolWitnessTable == IntPtr.Zero)
                    {
                        // The witness table is generated by the Swift compiler
                        // It will be available after the Swift wrapper is loaded
                        // For now, we look it up dynamically
                        _protocolWitnessTable = GetWitnessTableFromSwift();
                    }
                    return _protocolWitnessTable;
                }
            }

            private static IntPtr GetWitnessTableFromSwift()
            {
                // Call the Swift-exported function that returns the witness table pointer
                // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
                return NativeMethods.GetWitnessTable();
            }

            ExistentialContainer1 ISwiftExistentialConvertible<ExistentialContainer1>.GetExistentialContainer()
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                return _swiftContainer;
            }

            public static TypeMetadata GetTypeMetadata()
            {
                {{getTypeMetadataBody}}
            }

            public static ISwiftObject NewFromPayload(IntPtr payload)
            {
                {{newFromPayloadBody}}
            }

            IntPtr ISwiftObject.SwiftHandle => _swiftContainer.Payload0;

            public int MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                // Marshal the existential container
                var size = _swiftContainer.SizeOf;
                if (swiftDestSpan.Length < size)
                    throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));

                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                    new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
                }
                return size;
            }

            public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            {
                throw new NotSupportedException(
                    "Protocol conformance descriptor is not available for proxy types. " +
                    "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
            }

            {{disposeAndFinalizer}}

            #endregion

            #region Marshalling Helpers

            private static IntPtr MarshalToSwiftBuffer<T>(T value)
            {
                // Use direct memory operations for all types
                // This works for blittable types (value types, structs with blittable fields)
                var size = Unsafe.SizeOf<T>();
                var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
                Unsafe.Write((void*)ptr, value);
                return ptr;
            }

            private static T MarshalFromSwift<T>(IntPtr ptr)
            {
                // Use direct memory operations for all types
                return Unsafe.Read<T>((void*)ptr);
            }

            /// <summary>
            /// Marshals a C# string to a heap-allocated SBW_Utf8Slice struct (ptr + len).
            /// Used for string returns from proxy receiver callbacks to avoid ARC issues
            /// with SwiftString (which contains managed references that Unsafe.Write can't retain).
            /// The Swift side reads the Utf8Slice, creates a String, and frees the buffers.
            /// </summary>
            private static unsafe IntPtr MarshalStringToUtf8Slice(string value)
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
                var dataPtr = (byte*)NativeMemory.Alloc((nuint)Math.Max(utf8.Length, 1));
                if (utf8.Length > 0)
                {
                    fixed (byte* src = utf8)
                        System.Buffer.MemoryCopy(src, dataPtr, utf8.Length, utf8.Length);
                }
                // SBW_Utf8Slice layout: { ptr: UnsafeMutablePointer<UInt8>, len: Int }
                var slicePtr = (byte*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
                *(IntPtr*)slicePtr = (IntPtr)dataPtr;
                *(nint*)(slicePtr + sizeof(nint)) = utf8.Length;
                return (IntPtr)slicePtr;
            }

            #endregion

            """);

        // Emit NativeMethods for SetVtable + witness dispatch
        var setVtableName = GetSetVtablePInvokeName(protocolDecl);
        var mangledName = $"Set{protocolDecl.Name}_vtable";

        // Note: vtable and witness table functions are in the SwiftBindings wrapper, not the original module
        writer.WriteLine("private static partial class NativeMethods");
        writer.WriteLine("{");
        writer.Indent++;

        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = mangledName,
            MethodName = setVtableName,
            ReturnType = "void",
            ParametersString = "IntPtr vtable",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = $"Get_EveryProtocol_{protocolDecl.Name}_WitnessTable",
            MethodName = "GetWitnessTable",
            ReturnType = "IntPtr",
            ParametersString = "",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = CreateHelperEntryPoint,
            MethodName = CreateHelperMethodName,
            ReturnType = "IntPtr",
            ParametersString = "",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        // SBW_SetEveryProtocolDeinitCallback (or its EveryObjCProtocol twin) — wires
        // a C# unmanaged callback onto a helper instance so Swift's deinit can drive
        // the strong-registry and ProxyLifetimeTracker teardown. The function-pointer
        // parameter requires an unsafe P/Invoke; [LibraryImport] supports
        // `delegate* unmanaged[Cdecl]<...>` in .NET 10.
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = SetDeinitCallbackEntryPoint,
            MethodName = SetDeinitCallbackMethodName,
            ReturnType = "void",
            ParametersString = "IntPtr instance, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr context",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public,
            IsUnsafe = true,
        });
        writer.WriteLine();
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = GetMetadataEntryPoint,
            MethodName = GetMetadataMethodName,
            ReturnType = "IntPtr",
            ParametersString = "",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });

        // Emit P/Invoke declarations for witness dispatch accessors
        EmitWitnessDispatchPInvokes(writer, protocolDecl, dispatchEmitter, wrapperLibPath);

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits an accessor + free P/Invoke declaration pair for heap-pointer-returning property getters.
    /// Both blittable/string and collection property getters use identical P/Invoke shapes
    /// (accessor: IntPtr containerPtr → IntPtr, free: IntPtr ptr → void).
    /// </summary>
    private static void EmitHeapPointerGetterPInvokePair(CSharpWriter writer, string accessorSymbol, string freeSymbol, string wrapperLibPath)
    {
        writer.WriteLine();
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = accessorSymbol,
            MethodName = accessorSymbol,
            ReturnType = "IntPtr",
            ParametersString = "IntPtr containerPtr",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = freeSymbol,
            MethodName = freeSymbol,
            ReturnType = "void",
            ParametersString = "IntPtr ptr",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
    }

    /// <summary>
    /// Emits P/Invoke declarations for witness dispatch accessor and free functions.
    /// </summary>
    private void EmitWitnessDispatchPInvokes(CSharpWriter writer, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter, string wrapperLibPath)
    {
        var protocolName = protocolDecl.Name;
        var emittedPInvokes = new HashSet<string>();

        // Property getters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;

            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            // Only emit blittable/string getter P/Invokes here — class/struct properties
            // fall through to IsPropertyClassReturn/IsPropertyStructReturn branches below.
            // Note: Swift.String is a frozen+RefFields struct, so IsIndirectStructType matches it.
            // We must check IsStringDispatchType first to keep string on the blittable path.
            var isStringProp = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
            if (hasGetter && dispatchEmitter.IsPropertyGetterDispatchable(property)
                && (isStringProp || (!dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec)
                    && !dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))))
            {
                var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
                var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
                // Validate projected type allows dispatch (same gate as EmitPropertyImplementation)
                if (!isStringProperty && !WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
                    continue;

                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "get", property.Name, 0);
                EmitHeapPointerGetterPInvokePair(writer, accessorSymbol, freeSymbol, wrapperLibPath);
            }
            else if (hasGetter && dispatchEmitter.IsPropertyClassReturn(property))
            {
                // ClassReturn getter: returns IntPtr, no free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "IntPtr",
                    ParametersString = "IntPtr containerPtr",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
            else if (hasGetter && dispatchEmitter.IsPropertyStructReturn(property))
            {
                // StructReturn getter: returns void, has resultBuf param, no free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "void",
                    ParametersString = "IntPtr containerPtr, IntPtr resultBuf",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
            else if (hasGetter && dispatchEmitter.IsPropertyCollectionReturn(property))
            {
                // BoundGenericReturn getter: same P/Invoke shape as ExistentialReturn
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "get", property.Name, 0);
                EmitHeapPointerGetterPInvokePair(writer, accessorSymbol, freeSymbol, wrapperLibPath);
            }
        }

        // Property setters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;

            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
            // Only emit blittable/string setter P/Invokes — no setter dispatch for class/struct types yet.
            var isSetterStringProp = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
            if (hasSetter && dispatchEmitter.IsPropertySetterDispatchable(property)
                && (isSetterStringProp || (!dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec)
                    && !dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))))
            {
                var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
                var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
                // Validate projected type allows dispatch (same gate as EmitPropertyImplementation)
                if (!isStringProperty && !WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
                    continue;

                var setterSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "set", property.Name, 0);
                if (!emittedPInvokes.Add(setterSymbol))
                    continue;

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = setterSymbol,
                    MethodName = setterSymbol,
                    ReturnType = "void",
                    ParametersString = "IntPtr containerPtr, IntPtr valuePtr",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
        }

        // Methods
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (methodIndices.ContainsKey(methodKey))
                continue;

            var idx = methodIndex++;
            methodIndices[methodKey] = idx;

            var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
            if (!emittedCSharpKeys.Add(projectedKey))
                continue;

            var dispatchKind = dispatchEmitter.ClassifyMethodDispatch(method);
            if (dispatchKind == MethodDispatchKind.NotDispatchable)
                continue;

            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            var hasReturn = returnType != null && !returnType.IsEmptyTuple;
            var paramCount = method.CSSignature.Count - 1;

            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "method", method.Name, idx);
            if (!emittedPInvokes.Add(accessorSymbol))
                continue;

            if (dispatchKind is MethodDispatchKind.ExistentialReturn or MethodDispatchKind.ThrowingBlittableOrString or MethodDispatchKind.ClassReturn or MethodDispatchKind.BoundGenericReturn)
            {
                // ExistentialReturn, ThrowingBlittableOrString, ClassReturn, and BoundGenericReturn share the same P/Invoke shape:
                // params: containerPtr + per-param IntPtrs + errorOut (if throwing)
                // return: IntPtr (or void for ThrowingBlittableOrString void)
                var isThrowingKind = dispatchKind == MethodDispatchKind.ThrowingBlittableOrString ||
                                     (dispatchKind == MethodDispatchKind.ExistentialReturn && method.Throws) ||
                                     (dispatchKind == MethodDispatchKind.ClassReturn && method.Throws) ||
                                     (dispatchKind == MethodDispatchKind.BoundGenericReturn && method.Throws);

                var pInvokeParams = new List<string> { "IntPtr containerPtr" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                if (isThrowingKind)
                {
                    pInvokeParams.Add("IntPtr errorOut");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);

                // ThrowingBlittableOrString: value-returning uses IntPtr (nil=error), void uses void
                // ExistentialReturn / ClassReturn: always IntPtr
                var pInvokeReturnType = (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString && !hasReturn)
                    ? "void"
                    : "IntPtr";

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = pInvokeReturnType,
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });

                // Free function: always for ExistentialReturn and BoundGenericReturn,
                // for value-returning ThrowingBlittableOrString,
                // never for ClassReturn (SafeHandle handles ARC release)
                var needsFree = dispatchKind == MethodDispatchKind.ExistentialReturn ||
                                dispatchKind == MethodDispatchKind.BoundGenericReturn ||
                                (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString && hasReturn);
                if (needsFree)
                {
                    var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "method", method.Name, idx);
                    writer.WriteLine();
                    PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = freeSymbol,
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Public
                    });
                }

                // Emit error helper P/Invokes for throwing methods
                if (isThrowingKind)
                {
                    ErrorDescriptionEmitter.EmitCSharpErrorPInvokesViaPInvokeEmitHelper(
                        writer, GetProxyClassName(protocolDecl), _moduleName,
                        wrapperLibPath, _emissionContext);
                }
            }
            else if (dispatchKind == MethodDispatchKind.StructReturn)
            {
                // StructReturn: different P/Invoke shape — returns void, has resultBuf param
                var isThrowingStruct = method.Throws;

                var pInvokeParams = new List<string> { "IntPtr containerPtr", "IntPtr resultBuf" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                if (isThrowingStruct)
                {
                    pInvokeParams.Add("IntPtr errorOut");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "void",
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
                // No free function — SafeHandle owns the buffer

                // Emit error helper P/Invokes for throwing struct return methods
                if (isThrowingStruct)
                {
                    ErrorDescriptionEmitter.EmitCSharpErrorPInvokesViaPInvokeEmitHelper(
                        writer, GetProxyClassName(protocolDecl), _moduleName,
                        wrapperLibPath, _emissionContext);
                }
            }
            else
            {
                // BlittableOrString: existing pattern
                // Build parameter list: containerPtr + one IntPtr per param
                var pInvokeParams = new List<string> { "IntPtr containerPtr" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);
                var returnTypeStr = hasReturn ? "IntPtr" : "void";

                writer.WriteLine();
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = returnTypeStr,
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });

                if (hasReturn)
                {
                    var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "method", method.Name, idx);
                    writer.WriteLine();
                    PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = freeSymbol,
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Public
                    });
                }
            }
        }
    }
}
