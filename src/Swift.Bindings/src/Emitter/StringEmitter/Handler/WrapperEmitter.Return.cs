// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Emits the return statement for the constructor.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitReturnConstructor(CSharpWriter csWriter)
        {
            // ObjC-rooted: P/Invoke call is in the static helper (CreateSwiftInstance_...),
            // constructor body just needs DangerousRelease() to balance MAUI's retain.
            if (_env.ParentDecl is ClassDecl cd && cd.IsObjCRooted)
            {
                csWriter.WriteLine("DangerousRelease();");
                return;
            }

            if (_env.ParentDecl is StructDecl structDecl)
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    if (_requiresIndirectResult && _env.MethodDecl.UsesCdeclConstructorWrapper)
                    {
                        // @_cdecl wrapper: buffer was allocated in BuildIndirectResultSetup,
                        // P/Invoke wrote the result to resultPtr. Just create the SafeHandle.
                        var resolvedName = GetResolvedTypeName();
                        csWriter.WriteLine($"_payload = new SwiftSafeHandle<{resolvedName}>(bufferPtr);");
                    }
                    else
                    {
                        var resolvedName = GetResolvedTypeName();
                        csWriter.WriteLine($@"
                        unsafe {{
                            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({resolvedName}.Buffer));
                            *({resolvedName}.Buffer*)bufferPtr = result;
                            _payload = new SwiftSafeHandle<{resolvedName}>(bufferPtr);
                        }}");
                    }
                    return;
                }
            }

            // Non-ObjC Swift class: P/Invoke returns IntPtr directly (pointer in register).
            // Wrap directly in SwiftClassHandle — no buffer allocation needed.
            if (_env.ParentDecl is ClassDecl classDecl)
            {
                // Build the full type name including generic parameters (e.g., SpikeBox<TElement>)
                var handleTypeName = GetResolvedTypeName();
                if (classDecl.IsGeneric)
                    handleTypeName += GenericTypeEmitter.GetGenericParameterList(classDecl);

                // For effectively derived classes, _handle is declared as SwiftClassHandle<RootBase>.
                // Use the same predicate as ClassHandler to avoid referencing a skipped base type.
                if (ClassHandler.IsEffectivelyDerived(classDecl))
                {
                    handleTypeName = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(classDecl);
                }
                csWriter.WriteLine($"_handle = new SwiftClassHandle<{handleTypeName}>(result);");
                return;
            }

            if (!_requiresIndirectResult)
            {
                csWriter.WriteLine("this = result;");
            }
            else if (_env.MethodDecl.UsesCdeclConstructorWrapper && _env.ParentDecl is StructDecl frozenStruct && frozenStruct.IsFrozen)
            {
                // @_cdecl frozen blittable struct: result was written to _cdeclResult
                // via resultPtr in BuildIndirectResultSetup. Assign to this.
                csWriter.WriteLine("this = _cdeclResult;");
            }
        }

        /// <summary>
        /// Emits the return statement for the method.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitReturnMethod(CSharpWriter csWriter)
        {
            var returnArg = _env.MethodDecl.CSSignature.First();

            // Async methods always return via callback — never via resultPtr.
            // This must come before @_cdecl string checks to prevent async+@_cdecl methods
            // from emitting inline string decoding (the value arrives via the async callback).
            if (_requiresSwiftAsync)
            {
                csWriter.WriteLine("return _tcs.Task;");
                return;
            }

            // @_cdecl method wrapper: String returns SBW_Utf8Slice via resultPtr.
            // Unlike property wrappers (which return Utf8Slice for PropertyHandler to decode),
            // methods decode inline because there's no outer getter layer.
            if (_env.MethodDecl.UsesCdeclMethodWrapper &&
                returnArg.SwiftTypeSpec is NamedTypeSpec cdeclMethStrNts && cdeclMethStrNts.Name == "Swift.String")
            {
                var hp = _env.PInvokeHelperContext != null ? $"{_env.PInvokeHelperContext.HelperClassName}." : "";
                csWriter.WriteLines(
                    "unsafe {\n" +
                    "    var __slice = *(Utf8Slice*)resultPtr;\n" +
                    "    if (__slice.Len == 0) return string.Empty;\n" +
                    "    try {\n" +
                    "        return global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(\n" +
                    "            __slice.Ptr, (int)__slice.Len) ?? string.Empty;\n" +
                    "    } finally {\n" +
                    $"        {hp}SBW_Free(__slice.Ptr);\n" +
                    "    }\n" +
                    "}");
                return;
            }

            // @_cdecl property wrapper: String returns SBW_Utf8Slice via resultPtr (out-parameter)
            // because @_cdecl can't return Swift structs. Read the Utf8Slice from the result buffer.
            if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                returnArg.SwiftTypeSpec is NamedTypeSpec cdeclStrNts && cdeclStrNts.Name == "Swift.String")
            {
                csWriter.WriteLines("""
                    unsafe {
                        return *(Utf8Slice*)resultPtr;
                    }
                    """);
                return;
            }

            // Projection-based return — handles all return strategies (Direct, IndirectResult, OutBuffer)
            // via DetermineReturnStrategy(). Covers string, array, dictionary, optional, enum, class,
            // existential, ObjC bridged, native remapped, frozen-with-memory, non-frozen structs.
            // Skips: accessors, closures, tuples, generics, async.
            if (TryEmitReturnViaProjection(csWriter, returnArg))
                return;

            // IndirectResult fallback — for accessor returns and generic returns where the factory
            // returns null (user-defined generics). Uses the wrapper signature's return type.
            if (_requiresIndirectResult)
            {
                // @_cdecl wrappers use plain IntPtr resultPtr, not SwiftIndirectResult
                var resultExpr = _env.MethodDecl.UsesCdeclWrapper ? "resultPtr" : "new IntPtr(swiftIndirectResult.Value)";

                // @_cdecl Optional<closure> return: read SwiftClosureData from resultPtr buffer,
                // null-check via FunctionPointer == IntPtr.Zero (extra-inhabitant encoding).
                // Must come before the generic Optional<value-type> path which would return
                // SwiftOptional<SwiftClosureData>.ToNullable() — wrong type (need Func<>?, not SwiftClosureData?).
                if (_env.MethodDecl.UsesCdeclWrapper &&
                    _env.ClosureHandler.IsOptionalClosure(returnArg.SwiftTypeSpec))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        csWriter.WriteLines("""
                            unsafe {
                                var result = *(SwiftClosureData*)resultPtr;
                                if (result.FunctionPointer == IntPtr.Zero) return null;
                            """);
                        csWriter.Indent++;
                        if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                            ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else if (_env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else if (_env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else
                            ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                        return;
                    }
                }

                // Decomposed Optional getter: read hasValue from separate buffer, construct T? directly.
                // Avoids SwiftOptional<T> / VWT operations entirely — the Swift wrapper already
                // decomposed the Optional into (rawPayload, hasValue) in separate buffers.
                // Buffer allocation and hasValuePtr are set up by MethodMarshalPlanBuilder.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    !_env.MethodDecl.IsSubscriptAccessor &&
                    OptionalMarshalClassifier.IsDecomposed(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false,
                            GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl,
                            CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                    if (projection is OptionalProjection optProj)
                    {
                        var innerType = optProj.InnerProjection.MarshalFromSwiftType;
                        csWriter.WriteLines($$"""
                            unsafe {
                                {{OptionalMarshalClassifier.CSharpReadHasValue("hasValuePtr")}}
                                {{OptionalMarshalClassifier.CSharpHasValueNullCheck()}}
                                var _result = SwiftMarshal.MarshalFromSwift<{{innerType}}>(resultPtr);
                                _cdeclBuf = null; // NewFromPayload took ownership
                                return _result;
                            }
                            """);
                        return;
                    }
                }

                // @_cdecl Optional<value-type>: marshal via SwiftOptional<T>.ToNullable()
                if (_env.MethodDecl.UsesCdeclWrapper &&
                    MethodWrapperEmitter.IsOptionalType(returnArg.SwiftTypeSpec) &&
                    !MethodWrapperEmitter.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false,
                            GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl,
                            CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });

                    // Blittable primitive fast path: read tag byte directly from result buffer
                    // instead of going through SwiftOptional<T> + VWT GetEnumTag, which returns
                    // incorrect values for Optional<Int32> on some runtimes (Mono iOS Simulator).
                    // Return null SwiftOptional reference for None to avoid VWT entirely.
                    if (projection is OptionalProjection optProj)
                    {
                        var blittableSize = OptionalProjection.GetBlittablePrimitiveSizePublic(optProj.InnerProjection);
                        if (blittableSize != null)
                        {
                            var innerType = optProj.InnerProjection.PublicType;
                            var containerType = optProj.ContainerTypeName;
                            csWriter.WriteLines($$"""
                                unsafe {
                                    byte* _optPtr = (byte*){{resultExpr}};
                                    if (_optPtr[{{blittableSize.Value}}] != 0)
                                        return null!; // None — return null reference to bypass VWT
                                    return {{containerType}}.NewSome(*(({{innerType}}*)_optPtr));
                                }
                                """);
                            return;
                        }
                    }

                    // Return SwiftOptional<T> directly — the accessor method returns SwiftOptional<T>,
                    // not T?. Calling .ToNullable() is broken for value types (T? with unconstrained T
                    // is T in IL, so default returns 0/false instead of null) and the implicit operator
                    // SwiftOptional<T>(T?) would then wrap 0 back into NewSome(0).
                    var swiftType = projection?.ContainerTypeName ?? _wrapperSignature.ReturnType;
                    csWriter.WriteLines($$"""
                        return SwiftMarshal.MarshalFromSwift<{{swiftType}}>({{resultExpr}});
                        """);
                    return;
                }

                // @_cdecl closure returns: read SwiftClosureData from resultPtr buffer
                if (_env.MethodDecl.UsesCdeclWrapper && _env.ClosureHandler.IsClosure(returnArg))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        csWriter.WriteLines("""
                            unsafe {
                                var result = *(SwiftClosureData*)resultPtr;
                            """);
                        csWriter.Indent++;
                        if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                            ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else if (_env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else if (_env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        else
                            ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                        return;
                    }
                }

                // @_cdecl tuple returns: read each element at its Swift ABI offset and construct
                // the ValueTuple inline. Avoids MarshalFromSwift<ValueTuple<...>>() which uses
                // reflection-based tuple construction (GetConstructor) trimmed on NativeAOT.
                // returnMetadata is declared by MethodMarshalPlanBuilder's allocation code
                // (GetTupleTypeMetadataFromElements) and is in scope here.
                if (_env.MethodDecl.UsesCdeclWrapper && _env.TupleHandler.IsTuple(returnArg))
                {
                    EmitCdeclTupleReturn(csWriter, returnArg, resultExpr);
                    return;
                }

                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>({resultExpr});");
                return;
            }

            // Large Optional return via out-buffer fallback — for accessor returns where projection
            // is skipped. Uses projection ContainerTypeName for the SwiftOptional type if available.
            // Return SwiftOptional<T> directly — the property getter handles conversion via
            // explicit HasValue/Some check. Calling .ToNullable() is broken for value types
            // (same root cause as the @_cdecl indirect result path above).
            if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary))
            {
                var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                    new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                var swiftType = projection?.ContainerTypeName ?? _wrapperSignature.ReturnType;
                csWriter.WriteLines($$"""
                    return SwiftMarshal.MarshalFromSwift<{{swiftType}}>(_optRetPtr);
                    """);
                return;
            }

            // Accessor-only: Optional<ObjC> — P/Invoke returns IntPtr (nullable pointer ABI).
            // Just return the raw result; PropertyHandler applies GetNSObject conversion.
            if (_env.MethodDecl.IsAccessor && MarshallingHelpers.IsOptionalObjCBridged(returnArg.SwiftTypeSpec, _env.TypeDatabase))
            {
                csWriter.WriteLine("return result;");
                return;
            }

            // Accessor-only: Optional-existential returns — P/Invoke returns IntPtr for Optional<existential>,
            // marshal to SwiftOptional<Container> first.
            if (_env.MethodDecl.IsAccessor && _env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec)!;
                var publicType = _env.ExistentialHandler.GetPublicExistentialType(innerProtocolList);
                // Unresolved protocol (publicType == "object") → no proxy class exists, fall through
                if (publicType != "object")
                {
                    var containerType = _env.ExistentialHandler.GetCSharpExistentialType(innerProtocolList);
                    var marshalType = $"Swift.SwiftOptional<{containerType}>";
                    if (_env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkType))
                    {
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&result));
                            if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                            return new {{wkType}}(swiftResult.Some);
                            """);
                    }
                    else
                    {
                        var proxyName = _env.ExistentialHandler.GetQualifiedProxyClassName(innerProtocolList);
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&result));
                            if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                            return new {{proxyName}}(swiftResult.Some);
                            """);
                    }
                    return;
                }
            }

            // Bound generics that return directly — use projection for type name
            if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnArg))
            {
                var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                    new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                if (projection != null)
                {
                    var marshalType = projection.ContainerTypeName;
                    csWriter.WriteLines($$"""
                        unsafe {
                            return SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&result));
                        }
                        """);
                    return;
                }
                // Factory returned null — user-defined generic (e.g., Box<(T) -> ()>,
                // DownloadResponsePublisher<T1>). The factory can't project these because their
                // generic parameters may not satisfy ISwiftObject constraints. The wrapper signature
                // return type is correct here: WrapperSignatureBuilder resolves it via
                // TranslateBoundGenericTypeToCSharp which produces fully-qualified C# type names
                // (not AnyType). MarshalFromSwift<T> instantiates via ISwiftObject.NewFromPayload.
                var fallbackType = _wrapperSignature.ReturnType;
                csWriter.WriteLines($$"""
                    // Bound-generic fallback: factory cannot project {{fallbackType}}
                    unsafe {
                        return SwiftMarshal.MarshalFromSwift<{{fallbackType}}>(new IntPtr(&result));
                    }
                    """);
                return;
            }

            // Handle closure return types - result is SwiftClosureData, wrap in delegate
            if (_env.ClosureHandler.IsClosure(returnArg))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    // Throwing closures need special marshalling to handle SwiftError
                    if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    // Use non-frozen struct marshalling if any parameter is a non-frozen struct
                    // (requires heap allocation with NativeMemory and InitializeWithCopy/Destroy)
                    else if (_env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    // Use frozen struct marshalling if any parameter is a frozen struct
                    // (uses stackalloc for stack allocation)
                    else if (_env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    return;
                }
                // Unsupported closure return: MemberEmissionValidator should have caught this,
                // but guard against fallthrough to GetTypeRecordOrThrow (which crashes on ClosureTypeSpec).
                csWriter.WriteLine("return result;");
                return;
            }

            // @_cdecl existential returns: read container from indirect result buffer
            if (_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec) && _env.MethodDecl.UsesCdeclWrapper)
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec)!;
                var containerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                csWriter.WriteLine($"var existentialResult = SwiftMarshal.MarshalFromSwift<{containerType}>(resultPtr);");

                // Then wrap in proxy (same logic as non-cdecl path below)
                if (protocolList.Protocols.Count == 0) { csWriter.WriteLine("return existentialResult;"); return; }
                var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                if (publicType == "object") { csWriter.WriteLine("return existentialResult;"); return; }
                if (_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out var wk))
                { csWriter.WriteLine($"return new {wk}(existentialResult);"); return; }
                var proxy = _env.ExistentialHandler.GetQualifiedProxyClassName(protocolList);
                csWriter.WriteLine($"return new {proxy}(existentialResult);");
                return;
            }

            // Handle existential return types (any Protocol) - wrap container in proxy
            if (_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec)!;

                // Any (zero-protocol existential) → no proxy class; return container directly
                // ExistentialContainer0 boxes to 'object' matching the public return type
                if (protocolList.Protocols.Count == 0)
                {
                    csWriter.WriteLine("return result;");
                    return;
                }

                // Metatype/unresolved existential → GetPublicExistentialType returns "object"
                // No proxy class exists; return container directly (public type is AnyType via [UnsupportedSwiftType])
                var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                if (publicType == "object")
                {
                    csWriter.WriteLine("return result;");
                    return;
                }

                // Well-known protocol types (Swift.Error → AnyError) use direct runtime type
                if (_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownReturnType))
                {
                    csWriter.WriteLine($"return new {wellKnownReturnType}(result);");
                    return;
                }

                var proxyClassName = _env.ExistentialHandler.GetQualifiedProxyClassName(protocolList);
                csWriter.WriteLine($"return new {proxyClassName}(result);");
                return;
            }

            // Handle Optional-wrapped existential return types
            if (_env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec)!;
                var publicOptType = _env.ExistentialHandler.GetPublicExistentialType(innerProtocolList);
                // Unresolved protocol (publicType == "object") → no proxy class exists, fall through
                if (publicOptType != "object")
                {
                    var containerType = _env.ExistentialHandler.GetCSharpExistentialType(innerProtocolList);
                    // Optional existential: check for default (zero) container
                    csWriter.WriteLine($"if (result.Equals(default({containerType}))) return null;");
                    // Well-known protocol types (Swift.Error → AnyError) use direct runtime type
                    if (_env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wellKnownOptType))
                    {
                        csWriter.WriteLine($"return new {wellKnownOptType}(result);");
                    }
                    else
                    {
                        var optProxyClassName = _env.ExistentialHandler.GetQualifiedProxyClassName(innerProtocolList);
                        csWriter.WriteLine($"return new {optProxyClassName}(result);");
                    }
                    return;
                }
            }

            // Handle tuple return types - marshal each element individually
            if (_env.TupleHandler.IsTuple(returnArg))
            {
                EmitTupleReturnMarshalling(csWriter, returnArg);
                return;
            }

            // Type-record dispatch — handles returns where TryEmitReturnViaProjection returned false
            // (accessor returns, or types the factory can't resolve like ObjC classes from system frameworks).
            if (!returnArg.IsGeneric)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnArg.SwiftTypeSpec);

                // Simple enum return: cast underlying integer back to enum type
                if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                {
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType})result;");
                    return;
                }

                // ObjC bridged types: wrap IntPtr result with GetNSObject<T>
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "result")};");
                    return;
                }

                // Swift classes return pointer directly — pass to MarshalFromSwift which calls
                // NewFromPayload to create a SwiftClassHandle wrapping the pointer. No buffer needed.
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType})SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(result);");
                    return;
                }

                // Complex enums (non-simple) have SafeHandle-based opaque payloads — P/Invoke returns IntPtr
                if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                {
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType})SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(result);");
                    return;
                }

                // Frozen with memory management — MarshalFromSwift from buffer
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 && (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    csWriter.WriteLine($$"""
                        unsafe {
                            return SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(&result));
                        }
                        """);
                    return;
                }
            }

            if (returnArg.SwiftTypeSpec.IsEmptyTuple)
            {
                csWriter.WriteLine("return;");
                return;
            }

            csWriter.WriteLine("return result;");
        }

        /// <summary>
        /// Determines the return strategy based on method characteristics.
        /// </summary>
        private ReturnStrategy DetermineReturnStrategy()
        {
            if (_requiresSwiftAsync) return ReturnStrategy.AsyncCallback;
            if (_requiresIndirectResult) return ReturnStrategy.IndirectResult;
            if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary))
                return ReturnStrategy.OutBuffer;
            return ReturnStrategy.Direct;
        }

        /// <summary>
        /// Tries to emit return marshalling via the projection factory.
        /// Returns true if handled, false to fall through to legacy code.
        /// Skips: async returns, accessors, closures, tuples, generics.
        /// </summary>
        private bool TryEmitReturnViaProjection(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (returnArg.SwiftTypeSpec.IsEmptyTuple) return false;
            if (_requiresSwiftAsync) return false;
            if (_env.MethodDecl.IsAccessor) return false;
            if (_env.ClosureHandler.IsClosure(returnArg)) return false;
            if (_env.TupleHandler.IsTuple(returnArg)) return false;
            if (returnArg.IsGeneric) return false;

            var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
            if (projection == null) return false;

            var strategy = DetermineReturnStrategy();
            string resultName = strategy switch
            {
                ReturnStrategy.IndirectResult => _env.MethodDecl.UsesCdeclWrapper ? "resultPtr" : "new IntPtr(swiftIndirectResult.Value)",
                ReturnStrategy.OutBuffer => "_optRetPtr",
                _ => "result"
            };

            var plan = projection.GetReturnPlan(resultName, strategy);

            // @_cdecl indirect result with PassThrough projection (e.g. BlittableProjection for frozen structs):
            // PassThrough would emit "return resultPtr;" but resultPtr is IntPtr, not the return type.
            // Fall through to the MarshalFromSwift<T> fallback at the IndirectResult handler below.
            if (strategy == ReturnStrategy.IndirectResult && _env.MethodDecl.UsesCdeclWrapper &&
                plan.SetupStatements.Count == 0 && plan.CleanupStatements.Count == 0 &&
                plan.PInvokeExpression == resultName)
                return false;

            // @_cdecl indirect result with ExistentialProjection: the projection wraps resultName
            // in a proxy (e.g. "new DescribableProxy(resultPtr)"), but resultPtr is IntPtr — the
            // container must be read first via SwiftMarshal.MarshalFromSwift<T>(resultPtr).
            // Fall through to the dedicated @_cdecl existential return handler below.
            if (strategy == ReturnStrategy.IndirectResult && _env.MethodDecl.UsesCdeclWrapper &&
                projection is ExistentialProjection)
                return false;

            // Wrap in unsafe block if plan needs it but method-level unsafe is not set
            if (plan.RequiresUnsafe && !_needsUnsafeBody)
            {
                csWriter.WriteLine("unsafe {");
                csWriter.Indent++;
                MarshalPlanRenderer.RenderReturnPlan(csWriter, plan);
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else
            {
                MarshalPlanRenderer.RenderReturnPlan(csWriter, plan);
            }
            return true;
        }

        /// <summary>
        /// Emits per-element marshalling for tuple return types.
        /// Each tuple element is individually marshalled from its P/Invoke representation
        /// to the corresponding C# type.
        /// </summary>
        private void EmitTupleReturnMarshalling(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(returnArg);
            if (tupleTypeSpec == null)
            {
                csWriter.WriteLine("return result;");
                return;
            }

            var elements = tupleTypeSpec.Elements;
            var marshalLines = new List<string>();
            var resultElements = new List<string>();
            bool needsMarshalling = false;

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var itemName = $"result.Item{i + 1}";
                var resultName = $"elem{i}";
                var csharpType = GetCSharpTypeForTupleElement(element);

                var marshalCode = GetTupleElementMarshalCode(element, itemName, resultName, csharpType);
                if (marshalCode != null)
                {
                    marshalLines.Add(marshalCode);
                    // Check if this element actually needs marshalling (not a simple pass-through)
                    if (!marshalCode.Contains($"= {itemName};"))
                        needsMarshalling = true;
                }

                resultElements.Add(resultName);
            }

            // If no elements need marshalling, return directly
            if (!needsMarshalling)
            {
                csWriter.WriteLine("return result;");
                return;
            }

            // Emit per-element marshalling and tuple reconstruction
            foreach (var line in marshalLines)
            {
                // Handle multi-line marshal code (e.g., projection two-step conversion)
                foreach (var subLine in line.Split('\n'))
                    csWriter.WriteLine(subLine);
            }
            csWriter.WriteLine($"return ({string.Join(", ", resultElements)});");
        }

        /// <summary>
        /// Emits inline per-element reading from a @_cdecl indirect result buffer for tuple returns.
        /// Reads each element at its Swift ABI offset (via TupleTypeMetadata) into a typed local
        /// matching the P/Invoke type, then delegates to GetTupleElementMarshalCode for
        /// projection-aware conversion (Optional, Array, ObjC bridged, String, enums, etc.).
        /// Avoids the reflection-based MarshalFromSwift&lt;ValueTuple&lt;...&gt;&gt;() path which uses
        /// GetConstructor (trimmed on NativeAOT).
        /// </summary>
        private void EmitCdeclTupleReturn(CSharpWriter csWriter, ArgumentDecl returnArg, string resultExpr)
        {
            var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(returnArg);
            if (tupleTypeSpec == null)
            {
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>({resultExpr});");
                return;
            }

            var elements = tupleTypeSpec.Elements;
            csWriter.WriteLine("unsafe {");
            csWriter.Indent++;
            csWriter.WriteLine("var _tupleMetaPtr = returnMetadata.AsTupleMetadata();");

            // Phase 1: Read each element from the buffer into a typed local matching the
            // P/Invoke type. This produces the same typed locals that `result.ItemN` would
            // give in the non-@_cdecl path.
            var rawNames = new List<string>();
            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var rawName = $"_raw{i}";
                var pinvokeType = GetPInvokeTypeForTupleElement(element);
                var offsetExpr = $"(nint)_tupleMetaPtr->GetElementOffset({i})";

                csWriter.WriteLine($"{pinvokeType} {rawName} = *({pinvokeType}*)((byte*){resultExpr} + {offsetExpr});");
                rawNames.Add(rawName);
            }

            // Phase 2: Apply projection-aware marshalling via GetTupleElementMarshalCode.
            // This handles all element type conversions (Optional<ObjC>, Array<T>, String,
            // Foundation.Data, simple enums, ObjC bridged, etc.) — same logic as the
            // non-@_cdecl tuple return path in EmitTupleReturnMarshalling.
            var resultElements = new List<string>();
            bool needsMarshalling = false;

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var rawName = rawNames[i];
                var resultName = $"_te{i}";
                var csharpType = GetCSharpTypeForTupleElement(element);

                var marshalCode = GetTupleElementMarshalCode(element, rawName, resultName, csharpType);
                if (marshalCode != null)
                {
                    foreach (var subLine in marshalCode.Split('\n'))
                        csWriter.WriteLine(subLine);
                    if (!marshalCode.Contains($"= {rawName};"))
                        needsMarshalling = true;
                }

                resultElements.Add(resultName);
            }

            // If no elements needed marshalling, use the raw values directly
            if (!needsMarshalling)
                resultElements = rawNames;

            csWriter.WriteLine($"return ({string.Join(", ", resultElements)});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Gets the P/Invoke type for a tuple element.
        /// </summary>
        private string GetPInvokeTypeForTupleElement(TypeSpec element)
        {
            // Handle Optional<T> types - check for ObjC bridged inner types
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0)
                {
                    var innerType = namedType.GenericParameters[0];
                    if (innerType is NamedTypeSpec innerNamed &&
                        _env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                        MarshallingHelpers.IsObjCBridged(innerRecord))
                    {
                        // Optional ObjC type → IntPtr (null is IntPtr.Zero)
                        return "IntPtr";
                    }
                }
                // Other bound generics → IntPtr (opaque pointer, safe for C# generic type arguments)
                return "IntPtr";
            }

            if (element is NamedTypeSpec named)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);

                // ObjC bridged types use IntPtr
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return "IntPtr";
                }

                // Enums: simple enums use underlying integer type, complex enums use IntPtr
                if (typeRecord.Kind == TypeRecordKind.Enum)
                {
                    if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                        return EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                    return "IntPtr";
                }

                // Swift classes are non-blittable C# classes — must use IntPtr (no .Buffer)
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    return "IntPtr";
                }

                // Non-frozen structs (ClassWithOpaquePayload) are non-blittable C# classes — must use IntPtr (no .Buffer)
                if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
                {
                    return "IntPtr";
                }

                // Frozen types with memory management use Buffer type
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                    (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
                }

                // Frozen blittable structs — use type name directly
                if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

                // Fallback — IntPtr is safe for any unknown type
                return "IntPtr";
            }

            return "IntPtr";
        }

        /// <summary>
        /// Gets the C# type name for a tuple element.
        /// </summary>
        /// <param name="element">The TypeSpec for the tuple element.</param>
        /// <param name="applyIdiomaticConversion">When true, converts bare SwiftString to string. Set to false for recursive calls inside generics.</param>
        private string GetCSharpTypeForTupleElement(TypeSpec element, bool applyIdiomaticConversion = true)
        {
            // Handle bound generics — try factory projection first for idiomatic types
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var projection = s_projectionFactory.Project(element, new ProjectionContext
                {
                    TypeDatabase = _env.TypeDatabase,
                    IsParameter = false,
                    GenericContext = _genericContext,
                    CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                });
                if (projection != null)
                    return projection.PublicType;

                // Factory returned null — fall back to raw type translation
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord))
                {
                    // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
                    if (baseRecord == TypeDatabaseExtensions.IntPtrType)
                    {
                        return baseRecord.CSharpTypeName.FullyQualifiedName;
                    }

                    // Recursively translate generic parameters (no idiomatic conversion inside generics)
                    var translatedParams = new List<string>();
                    foreach (var param in namedType.GenericParameters)
                    {
                        translatedParams.Add(GetCSharpTypeForTupleElement(param, applyIdiomaticConversion: false));
                    }
                    return $"{baseRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }
            }

            if (element is NamedTypeSpec named)
            {
                // Bare SwiftString → string (only at top level, not inside generics)
                if (applyIdiomaticConversion && MarshallingHelpers.IsSwiftString(named))
                    return "string";

                // Foundation.Data → byte[] (only at top level, not inside generics)
                if (applyIdiomaticConversion && named.Name == "Foundation.Data")
                    return "byte[]";

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Generates marshalling code for a single tuple element.
        /// </summary>
        private string? GetTupleElementMarshalCode(TypeSpec element, string itemName, string resultName, string csharpType)
        {
            // Handle bound generic types (Optional<T>, Array<T>, etc.)
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                // Check for Optional<ObjC> first — ObjC types use bare IntPtr (null = IntPtr.Zero),
                // NOT SwiftOptional buffer layout. Must skip projection to use the ObjC special-case below.
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                bool isOptionalObjC = false;
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0 &&
                    namedType.GenericParameters[0] is NamedTypeSpec innerObjCCheck &&
                    _env.TypeDatabase.TryGetTypeRecord(innerObjCCheck, out var innerObjCRecord) &&
                    MarshallingHelpers.IsObjCBridged(innerObjCRecord))
                {
                    isOptionalObjC = true;
                }

                // Try factory projection for idiomatic return marshalling (skip Optional<ObjC>)
                if (!isOptionalObjC)
                {
                    var projection = s_projectionFactory.Project(element, new ProjectionContext
                    {
                        TypeDatabase = _env.TypeDatabase,
                        IsParameter = false,
                        GenericContext = _genericContext,
                        CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                    });
                    if (projection != null)
                    {
                        var containerType = projection.ContainerTypeName;
                        var containerConv = projection.GetReturnContainerConversion($"_swift{resultName}");
                        if (containerConv != null)
                        {
                            // Two-step: marshal from Swift container type, then convert to public type
                            return $"var _swift{resultName} = SwiftMarshal.MarshalFromSwift<{containerType}>({itemName});\nvar {resultName} = {containerConv};";
                        }
                        var elemConv = projection.GetReturnElementConversion($"_swift{resultName}");
                        if (elemConv != null)
                        {
                            return $"var _swift{resultName} = SwiftMarshal.MarshalFromSwift<{containerType}>({itemName});\nvar {resultName} = {elemConv};";
                        }
                        // No conversion needed — use container type for MarshalFromSwift
                        return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{containerType}>({itemName});";
                    }
                }

                // Factory returned null or Optional<ObjC> — fall back to raw type marshalling
                if (baseRecord != null && baseRecord.CSharpTypeName.Name == "SwiftOptional")
                {
                    // For optional ObjC types, the P/Invoke type is IntPtr
                    // For optional Swift types, it's SwiftOptional<T>.Buffer
                    if (namedType.GenericParameters.Count > 0)
                    {
                        var innerType = namedType.GenericParameters[0];
                        if (innerType is NamedTypeSpec innerNamed &&
                            _env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                            MarshallingHelpers.IsObjCBridged(innerRecord))
                        {
                            // Optional ObjC type: IntPtr -> SwiftOptional<NSObject>
                            // Use factory methods NewNone() and NewSome() since constructors are private
                            var innerCSharp = innerRecord.CSharpTypeName.FullyQualifiedName;
                            return $"var {resultName} = {itemName} == IntPtr.Zero ? Swift.SwiftOptional<{innerCSharp}>.NewNone() : Swift.SwiftOptional<{innerCSharp}>.NewSome({MarshallingHelpers.FormatObjCBridgeCall(innerCSharp, itemName, nonNull: true)});";
                        }
                    }
                    // Non-ObjC optional: P/Invoke type is IntPtr, pass directly (no address-of)
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>({itemName});";
                }

                // Non-optional bound generics (e.g., SwiftArray<byte>): P/Invoke type is IntPtr
                // The IntPtr IS the pointer value, so pass it directly (no address-of)
                return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>({itemName});";
            }

            // Handle non-generic types — key off computed P/Invoke type to handle all IntPtr cases uniformly
            var pinvokeType = GetPInvokeTypeForTupleElement(element);

            if (element is NamedTypeSpec named)
            {
                if (_env.TypeDatabase.TryGetTypeRecord(named, out var typeRecord))
                {
                    // ObjC bridged types
                    if (MarshallingHelpers.IsObjCBridged(typeRecord))
                    {
                        return $"var {resultName} = {MarshallingHelpers.FormatObjCBridgeCall(csharpType, itemName, nonNull: true)};";
                    }
                }
            }

            // Use the computed P/Invoke type to determine marshalling
            if (pinvokeType == "IntPtr")
            {
                // IntPtr IS the pointer — pass directly (no address-of)
                return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>({itemName});";
            }
            else if (pinvokeType.EndsWith(".Buffer"))
            {
                // SwiftString.Buffer → string (via MarshalFromSwift + ToString)
                if (MarshallingHelpers.IsSwiftString(element))
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&{itemName})).ToString();";
                // Other .Buffer types (frozen structs with memory management)
                return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
            }

            // Simple enums: P/Invoke uses underlying type (int, long), need cast to C# enum
            if (element is NamedTypeSpec enumNamed &&
                _env.TypeDatabase.TryGetTypeRecord(enumNamed, out var enumRecord) &&
                enumRecord.Kind == TypeRecordKind.Enum && enumRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                return $"var {resultName} = ({csharpType}){itemName};";
            }

            // Foundation.Data → byte[] conversion
            if (element is NamedTypeSpec dataElement && dataElement.Name == "Foundation.Data")
                return $"var {resultName} = {itemName}.ToByteArray();";

            // Frozen blittable primitives — use directly
            return $"var {resultName} = {itemName};";
        }
    }
}
