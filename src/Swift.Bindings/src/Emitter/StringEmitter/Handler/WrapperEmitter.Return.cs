// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// The owned-return ctor argument (<c>, ownsContainer: true</c>) for an existential
        /// return, or empty. A proxy that adopts a Swift-returned existential at +1 must release
        /// the container's value-witness retains on Dispose/finalize, or the payload's +1 leaks.
        /// Both single-protocol (EC1) and composition (EC2+) proxies expose the ownership-aware
        /// ctor and release the adopted container's one conforming value via the existential's own
        /// metadata. Gated on the container TYPE via <see cref="ExistentialHandler.IsOwnedExistentialContainerType"/>,
        /// not the protocol count: ObjC filtering can drop protocols, so a protocol-list count
        /// diverges from the emitted EC width (see the mixed-composition guard in constraints).
        /// </summary>
        private static string OwnedExistentialCtorArg(string containerType) =>
            ExistentialHandler.IsOwnedExistentialContainerType(containerType)
                ? ", ownsContainer: true"
                : string.Empty;

        /// <summary>
        /// Returns the invoke thunk info (entry point, library name, helper method name) if the
        /// closure return type can use one. Returns null if the closure has struct/class params
        /// or is async (CanUseInvokeThunk gates those out). Throwing closures ARE supported.
        /// </summary>
        private (string entryPoint, string libraryName, string helperName)? GetInvokeThunkInfoIfAvailable(ClosureTypeSpec closureTypeSpec)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return null;
            if (!ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, _env.ClosureHandler))
                return null;
            var entryPoint = ClosureEmitter.GetInvokeThunkEntryPoint(_env.EmissionSymbol);
            var helperName = ClosureEmitter.GetInvokeThunkHelperName(_env.EmissionSymbol);
            var moduleDecl = _env.MethodDecl.ModuleDecl;
            if (moduleDecl == null) return null;
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var libraryName = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            return (entryPoint, libraryName, helperName);
        }

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
                        csWriter.WriteLine($"_payload = new SwiftSafeHandle<{resolvedName}>({BufferPtrName});");
                    }
                    else
                    {
                        var resolvedName = GetResolvedTypeName();
                        csWriter.WriteLine($@"
                        unsafe {{
                            IntPtr {BufferPtrName} = (IntPtr)NativeMemory.Alloc((nuint)sizeof({resolvedName}.Buffer));
                            *({resolvedName}.Buffer*){BufferPtrName} = {ReturnLocalName};
                            _payload = new SwiftSafeHandle<{resolvedName}>({BufferPtrName});
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
                    handleTypeName = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(classDecl, _env.TypeDatabase);
                }
                csWriter.WriteLine($"_handle = new SwiftClassHandle<{handleTypeName}>({ReturnLocalName});");
                return;
            }

            if (!_requiresIndirectResult)
            {
                csWriter.WriteLine($"this = {ReturnLocalName};");
            }
            else if (_env.MethodDecl.UsesCdeclConstructorWrapper && _env.ParentDecl is StructDecl frozenStruct && frozenStruct.IsFrozen)
            {
                // @_cdecl frozen blittable struct: result was written to _cdeclResult
                // via resultPtr in BuildIndirectResultSetup. Assign to this.
                csWriter.WriteLine("this = _cdeclResult;");
            }
        }

        /// <summary>
        /// Emits the @_cdecl existential-return marshalling: read the existential container from
        /// the result location and wrap it in the appropriate proxy / well-known / union type.
        /// Applies on both the indirect-result path and the direct-return path. The read location
        /// follows the ACTUAL return convention (_requiresIndirectResult) — the indirect out-param
        /// (ResultPtrName) when the box pointer arrives via the buffer, the function-result local
        /// (ReturnLocalName) when the getter returns it directly — never a hardcoded ResultPtrName.
        /// Returns true when it emitted a return (caller should return); false if not applicable.
        /// </summary>
        private bool TryEmitCdeclExistentialReturn(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (!(_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec) && _env.MethodDecl.UsesCdeclWrapper))
                return false;

            var resultLocation = _requiresIndirectResult ? ResultPtrName : ReturnLocalName;
            var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec)!;

            // A single @objc protocol existential ('any P' where P is @objc) is a bare ObjC object
            // pointer, NOT a container: the @_cdecl extern returns it as IntPtr BY VALUE (direct
            // return), so resultLocation holds the object pointer itself — it does NOT address an
            // ExistentialContainer1. We must WRAP that pointer in the proxy's single-payload
            // container; MarshalFromSwift<ExistentialContainer1>(resultLocation) would instead
            // dereference the object as if it were a 40-byte container (garbage read / crash).
            // Mirrors the Optional<@objc existential> accessor path above, minus the nil check
            // (this return is non-optional). ownsContainer: true adopts the getter's +1 retain.
            if (ExistentialHandler.IsObjCProtocolExistentialSpec(returnArg.SwiftTypeSpec, _env.TypeDatabase))
            {
                var objcPublicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                // Unresolved protocol (publicType == "object") → no proxy class; fall through to the
                // generic path below (an unresolved @objc existential is an upstream-skipped edge).
                if (objcPublicType != "object")
                {
                    var objcProxy = _env.ExistentialHandler.GetRequiredProxyClassName(protocolList, _emissionContext);
                    csWriter.WriteLine($"return new {objcProxy}(new Swift.Runtime.ExistentialContainer1 {{ Payload0 = {resultLocation} }}, ownsContainer: true);");
                    return true;
                }
            }

            var containerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
            // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
            // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque
            // container (40 bytes); reading the wider type pulls uninitialized bytes into the
            // unused container fields. The +1 still transfers via the bitwise copy (the buffer
            // free is a plain dealloc, no VWT Destroy), so the proxy's ownsContainer adoption is
            // unchanged — only the read width differs.
            var existentialRead = _env.ExistentialHandler.IsClassBoundArity1Existential(protocolList)
                ? $"Swift.Runtime.ClassExistentialContainer1.ReadHeapCell({resultLocation})"
                : $"SwiftMarshal.MarshalFromSwift<{containerType}>({resultLocation})";
            csWriter.WriteLine($"var existentialResult = {existentialRead};");

            if (protocolList.Protocols.Count == 0) { csWriter.WriteLine("return existentialResult;"); return true; }
            // Return position (pure read) → allow the PAT-with-conformers union projection.
            var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList, allowUnionProjection: _env.AllowsExistentialReturnUnionProjection);
            if (publicType == "object") { csWriter.WriteLine("return existentialResult;"); return true; }
            // PAT protocol with known conformers → ExistentialUnion (no proxy, uses try-cast).
            if (publicType == "Swift.Runtime.ExistentialUnion")
            { csWriter.WriteLine($"return new Swift.Runtime.ExistentialUnion(existentialResult);"); return true; }
            if (_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out var wk))
            { csWriter.WriteLine($"return new {wk}(existentialResult{ExistentialHandler.WellKnownOwnedTransferArg(wk)});"); return true; }
            var proxy = _env.ExistentialHandler.GetRequiredProxyClassName(protocolList, _emissionContext);
            // Owned return: +1 existential read out of the @_cdecl result location (EC1 or EC2+ composition).
            csWriter.WriteLine($"return new {proxy}(existentialResult{OwnedExistentialCtorArg(containerType)});");
            return true;
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
                // Close the try { opened in EmitAsync (after the cancellation registration block)
                // and emit a catch that runs the holder cleanup loop, frees the GCHandle, then
                // rethrows. Without this, an exception from a parameter conversion or P/Invoke
                // launch leaks the holder + GCHandle + any deferred-list containers / retained
                // self / copy buffers / cancellation registration that were already populated.
                var foregroundCleanup = BuildHolderCleanupCode("_asyncCallHolder", "    ");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("catch");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLines(foregroundCleanup);
                csWriter.WriteLine("handle.Free();");
                // The wrapper never launched, so its `defer { _sbwUnregisterTask }` will not run.
                // Reclaim any WINDOW A cancellation tombstone left for this id (no-op if none).
                csWriter.WriteLine($"{AsyncCallbackPrefix}SBW_UnregisterTask(_sbwCancelKey);");
                csWriter.WriteLine("throw;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("return _tcs.Task;");
                return;
            }

            // @_cdecl method wrapper: String returns SBW_Utf8Slice via resultPtr.
            // Unlike property wrappers (which return Utf8Slice for PropertyHandler to decode),
            // methods decode inline because there's no outer getter layer. A carved-out scalar
            // LocalizedStringResource return is resolved Swift-side with String(localized:) and
            // crosses the wire as the same SBW_Utf8Slice, so it decodes identically.
            if (_env.MethodDecl.UsesCdeclMethodWrapper &&
                returnArg.SwiftTypeSpec is NamedTypeSpec cdeclMethStrNts &&
                (cdeclMethStrNts.Name == "Swift.String" || MarshallingHelpers.IsLocalizedStringResource(cdeclMethStrNts)))
            {
                csWriter.WriteLine($"return SwiftMarshal.ReadUtf8Slice({ResultPtrName});");
                return;
            }

            // @_cdecl property wrapper: String returns SBW_Utf8Slice via resultPtr (out-parameter)
            // because @_cdecl can't return Swift structs. Read the Utf8Slice from the result buffer.
            // The LocalizedStringResource carve-out rides the same Utf8Slice wire.
            if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                returnArg.SwiftTypeSpec is NamedTypeSpec cdeclStrNts &&
                (cdeclStrNts.Name == "Swift.String" || MarshallingHelpers.IsLocalizedStringResource(cdeclStrNts)))
            {
                csWriter.WriteLines($$"""
                    unsafe {
                        return *(Utf8Slice*){{ResultPtrName}};
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
                // Multi-element generic tuple: each element was returned via its own @out buffer
                // (tupleResult{i}Ptr / _tupleResult{i}Buf — first buffer reuses _cdeclBuf).
                // Marshal each element separately, applying the same ownership-transfer rules
                // as the single-buffer fallback below (class T → dereference; ISwiftStruct T →
                // null out buffer; otherwise free buffer normally).
                if (MarshallingHelpers.IsMultiElementGenericTupleIndirectReturn(_env))
                {
                    EmitMultiElementGenericTupleReturn(csWriter, (TupleTypeSpec)returnArg.SwiftTypeSpec);
                    return;
                }

                // @_cdecl wrappers use plain IntPtr resultPtr, not SwiftIndirectResult.
                // Native thunks use SwiftIndirectResult (x8 register passthrough) — NOT resultPtr.
                var resultExpr = _env.MethodDecl.UsesCdeclWrapper ? ResultPtrName : $"new IntPtr({SwiftIndirectResultName}.Value)";

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
                        // Use invoke thunk for closure return when available (avoids delegate* unmanaged[Swift] crash)
                        var invokeThunkInfo = GetInvokeThunkInfoIfAvailable(closureTypeSpec);
                        var invokeThunkName = invokeThunkInfo?.entryPoint;
                        var invokeThunkLib = invokeThunkInfo?.libraryName;
                        var invokeThunkHelper = invokeThunkInfo?.helperName;

                        // A returned closure's INVOKER marshals its ARGUMENTS C#→Swift; a suppressed-proxy
                        // existential arg drops its wrap fallback (GetSwiftInvokeArgExpression / struct-param
                        // path) — a silent CONSUME degrade of this member. Pure read: byte-identical.
                        RecordClosureConsumeDegrade(closureTypeSpec.EachArgument());
                        csWriter.WriteLines($$"""
                            unsafe {
                                var {{ReturnLocalName}} = *(SwiftClosureData*){{ResultPtrName}};
                                if ({{ReturnLocalName}}.FunctionPointer == IntPtr.Zero) return null;
                            """);
                        csWriter.Indent++;
                        if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                            ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
                        else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                        else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                        else
                            ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
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
                        _env.NewProjectionContext(isParameter: false, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
                    if (projection is OptionalProjection optProj)
                    {
                        var innerType = optProj.InnerProjection.MarshalFromSwiftType;

                        // Optional<existential>: read ExistentialContainer from buffer, wrap in proxy.
                        // ExistentialContainer1 is a C# struct — MarshalFromSwift reads it, then
                        // we wrap it in the protocol proxy class for the interface return type.
                        if (optProj.InnerProjection is ExistentialProjection existentialProj)
                        {
                            var containerType = existentialProj.PInvokeType;
                            var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec);
                            string proxyCtorExpr;
                            if (innerProtocolList != null && _env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkType))
                                // Owned return: the getter read the inner existential out of the decomposed
                                // (payload, hasValue) buffer at +1; the well-known wrapper adopts that retain
                                // and releases it on Dispose/finalize or the payload's +1 leaks.
                                proxyCtorExpr = $"new {wkType}(_container{ExistentialHandler.WellKnownOwnedTransferArg(wkType)})";
                            else if (innerProtocolList != null)
                                // Owned return: the property getter read the inner existential out of the
                                // decomposed (payload, hasValue) buffer at +1 and the buffer is freed in the
                                // finally, so the proxy is the sole surviving retain and must release it on
                                // Dispose/finalize (ownsContainer: true) or the payload's +1 leaks.
                                proxyCtorExpr = $"new {_env.ExistentialHandler.GetRequiredProxyClassName(innerProtocolList, _emissionContext)}(_container{OwnedExistentialCtorArg(containerType)})";
                            else
                                proxyCtorExpr = "_container"; // fallback — no proxy available

                            csWriter.WriteLines($$"""
                                unsafe {
                                    {{OptionalMarshalClassifier.CSharpReadHasValue(HasValuePtrName)}}
                                    {{OptionalMarshalClassifier.CSharpHasValueNullCheck()}}
                                    var _container = SwiftMarshal.MarshalFromSwift<{{containerType}}>({{ResultPtrName}});
                                    return {{proxyCtorExpr}};
                                }
                                """);
                            return;
                        }

                        // Unconstrained generic param TValue: the buffer's contents differ by kind.
                        //   * Frozen value-type struct (C# struct): bytes ARE the struct payload.
                        //     NewFromPayload dereferences resultPtr; the temp buffer is freed in finally.
                        //   * ISwiftStruct (non-frozen Swift struct emitted as C# class with SafeHandle):
                        //     bytes are the struct payload, but NewFromPayload TAKES OWNERSHIP of the
                        //     buffer (stores it in SwiftSafeHandle). Suppress the finally-free.
                        //   * ISwiftObject class: the buffer holds a single heap pointer (with retain).
                        //     NewFromPayload expects that heap pointer, not the buffer address.
                        // Dispatch on type at runtime.
                        if (optProj.InnerProjection is BlittableProjection blitGeneric && blitGeneric.IsGenericParameter)
                        {
                            csWriter.WriteLines($$"""
                                unsafe {
                                    {{OptionalMarshalClassifier.CSharpReadHasValue(HasValuePtrName)}}
                                    {{OptionalMarshalClassifier.CSharpHasValueNullCheck()}}
                                    if (typeof({{innerType}}).IsValueType) {
                                        return SwiftMarshal.MarshalFromSwift<{{innerType}}>({{ResultPtrName}});
                                    } else if (typeof(global::Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({{innerType}}))) {
                                        var _result = SwiftMarshal.MarshalFromSwift<{{innerType}}>({{ResultPtrName}});
                                        _cdeclBuf = null; // NewFromPayload took ownership
                                        return _result;
                                    } else {
                                        IntPtr _classHandle = *(IntPtr*){{ResultPtrName}};
                                        return SwiftMarshal.MarshalFromSwift<{{innerType}}>(_classHandle);
                                    }
                                }
                                """);
                            return;
                        }

                        csWriter.WriteLines($$"""
                            unsafe {
                                {{OptionalMarshalClassifier.CSharpReadHasValue(HasValuePtrName)}}
                                {{OptionalMarshalClassifier.CSharpHasValueNullCheck()}}
                                var _result = SwiftMarshal.MarshalFromSwift<{{innerType}}>({{ResultPtrName}});
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
                    !CdeclParamMapper.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                        _env.NewProjectionContext(isParameter: false, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));

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
                        // Frozen blittable struct fast path: use TypeMetadata.Size for tag offset.
                        // Uses the runtime's source of truth (not Unsafe.SizeOf which includes C# padding).
                        // Avoids VWT GetEnumTag which returns incorrect values for frozen structs on Mono.
                        if (optProj.InnerProjection is BlittableProjection blitInner && blittableSize == null &&
                            !OptionalProjection.IsKnownPrimitiveTypeNamePublic(optProj.InnerProjection.PublicType) &&
                            !blitInner.IsGenericParameter)
                        {
                            var innerType = optProj.InnerProjection.PublicType;
                            var containerType = optProj.ContainerTypeName;
                            csWriter.WriteLines($$"""
                                unsafe {
                                    byte* _optPtr = (byte*){{resultExpr}};
                                    if (_optPtr[(int)TypeMetadata.GetTypeMetadataOrThrow<{{innerType}}>().Size] != 0)
                                        return null!; // None — return null reference to bypass VWT
                                    return {{containerType}}.NewSome(Unsafe.ReadUnaligned<{{innerType}}>(ref *_optPtr));
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
                        // Use invoke thunk for closure return when available (avoids delegate* unmanaged[Swift] crash)
                        var invokeThunkInfo = GetInvokeThunkInfoIfAvailable(closureTypeSpec);
                        var invokeThunkName = invokeThunkInfo?.entryPoint;
                        var invokeThunkLib = invokeThunkInfo?.libraryName;
                        var invokeThunkHelper = invokeThunkInfo?.helperName;

                        // See the sibling closure-return block above: record the returned closure's
                        // invoker-ARGUMENT CONSUME degrade for any suppressed proxy. Pure read.
                        RecordClosureConsumeDegrade(closureTypeSpec.EachArgument());
                        csWriter.WriteLines($$"""
                            unsafe {
                                var {{ReturnLocalName}} = *(SwiftClosureData*){{ResultPtrName}};
                            """);
                        csWriter.Indent++;
                        if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                            ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
                        else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                        else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                            ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                        else
                            ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
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

                // @_cdecl existential returns inside indirect-result block: read the container
                // from the result buffer and wrap in proxy class. Without this, existential
                // returns fall through to the generic MarshalFromSwift<IProtocol> catch-all
                // which throws NotSupportedException at runtime (R3 regression).
                if (TryEmitCdeclExistentialReturn(csWriter, returnArg))
                    return;

                // Record bound generic for NativeAOT module initializer registration.
                // Without this, NativeAOT trims the explicit ISwiftObject.GetTypeMetadata()
                // on closed generic types returned via @_cdecl indirect result, causing
                // MarshalFromSwift<T> to fail at runtime.
                if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnArg))
                    _emissionContext.RecordBoundGenericSwiftObjectType(_wrapperSignature.ReturnType);

                // Bare generic parameter return (e.g. T): buffer ownership semantics are
                // resolved at runtime.
                //   - ISwiftStruct T (non-frozen struct, frozen-with-refs, complex enum):
                //     SafeHandle wraps the buffer itself as its handle. Pass the buffer
                //     pointer through MarshalFromSwift<T> unchanged.
                //   - Genuine Swift class T (C# reference type, ISwiftObject, not
                //     ISwiftStruct): Swift writes the class instance pointer into the
                //     buffer. Dereference to get the real class handle before calling
                //     MarshalFromSwift<T> (otherwise it wraps the buffer address as if it
                //     were the class pointer, mis-binding the instance).
                //   - Frozen-blittable T (C# struct projected as value type but still
                //     ISwiftObject, e.g. SummableInt32), primitive T, tuple T:
                //     MarshalFromSwift<T> reads the value out of the buffer, so the
                //     buffer pointer is correct. Value types are excluded from the
                //     dereference branch via typeof(T).IsValueType.
                if (returnArg.SwiftTypeSpec is NamedTypeSpec gpRetNts
                    && TypeSpecHelpers.IsGenericTypeParameter(gpRetNts.Name))
                {
                    var tName = _wrapperSignature.ReturnType;
                    csWriter.WriteLines($$"""
                        if (!typeof({{tName}}).IsValueType && typeof(Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof({{tName}})) && !typeof(Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({{tName}})))
                        {
                            IntPtr _classHandle;
                            unsafe { _classHandle = *(IntPtr*){{resultExpr}}; }
                            return SwiftMarshal.MarshalFromSwift<{{tName}}>(_classHandle);
                        }
                        return SwiftMarshal.MarshalFromSwift<{{tName}}>({{resultExpr}});
                        """);
                    return;
                }

                // Accessor optional-existential return via indirect-result buffer. A large existential
                // (the 40-byte ExistentialContainer1) makes _requiresIndirectResult true, so the
                // small-existential accessor block at ~549 (which marshals from &returnLocal) is
                // unreachable — control returns out of this block before reaching it. The indirect
                // buffer holds a SwiftOptional<Container>: read it, return null for None, else wrap the
                // inner container in the protocol proxy. Without this, the optional-existential
                // subscript/property getter falls through to the catch-all
                // MarshalFromSwift<IProtocol?> below, which has no protocol-interface case and throws
                // NotSupportedException at runtime. Ownership matches the decomposed-property path:
                // the Swift wrapper wrote the inner existential at +1 (initializeMemory), the bitwise
                // container copy does not re-retain, and the buffer's plain NativeMemory.Free (no VWT
                // Destroy) leaves that +1 intact, so the proxy adopts it (ownsContainer: true)
                // and releases it on Dispose/finalize — otherwise the payload's +1 leaks.
                if (_env.MethodDecl.IsAccessor && _env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec)!;
                    var publicType = _env.ExistentialHandler.GetPublicExistentialType(innerProtocolList);
                    // Unresolved protocol (publicType == "object") → no proxy class exists, fall through.
                    if (publicType != "object")
                    {
                        var containerType = _env.ExistentialHandler.GetCSharpExistentialType(innerProtocolList);
                        var marshalType = $"Swift.SwiftOptional<{containerType}>";
                        if (_env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkType))
                        {
                            csWriter.WriteLines($$"""
                                var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>({{resultExpr}});
                                if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                                return new {{wkType}}(swiftResult.Some{{ExistentialHandler.WellKnownOwnedTransferArg(wkType)}});
                                """);
                        }
                        else
                        {
                            var proxyName = _env.ExistentialHandler.GetRequiredProxyClassName(innerProtocolList, _emissionContext);
                            csWriter.WriteLines($$"""
                                var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>({{resultExpr}});
                                if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                                return new {{proxyName}}(swiftResult.Some{{OwnedExistentialCtorArg(containerType)}});
                                """);
                        }
                        return;
                    }
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
                    _env.NewProjectionContext(isParameter: false, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
                var swiftType = projection?.ContainerTypeName ?? _wrapperSignature.ReturnType;
                csWriter.WriteLines($$"""
                    return SwiftMarshal.MarshalFromSwift<{{swiftType}}>(_optRetPtr);
                    """);
                return;
            }

            // Accessor-only: Optional<reference-type> — P/Invoke returns IntPtr (nullable pointer ABI).
            // Covers ObjC-bridged, ObjC-rooted, and pure Swift class optionals.
            // Just return the raw result; PropertyHandler applies the appropriate conversion.
            if (_env.MethodDecl.IsAccessor && CdeclParamMapper.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase))
            {
                csWriter.WriteLine($"return {ReturnLocalName};");
                return;
            }

            // Accessor-only: ObjC-bridgeable container (e.g., [URL], [String: URL], Set<URL>) —
            // P/Invoke returns IntPtr (ObjC collection handle via ClassPointer ABI).
            // Just return the raw IntPtr; PropertyHandler applies NSArray/NSDictionary/NSSet conversion.
            if (_env.MethodDecl.IsAccessor &&
                (CdeclParamMapper.IsObjCBridgeableContainer(returnArg.SwiftTypeSpec, _env.TypeDatabase) ||
                 CdeclParamMapper.IsOptionalObjCBridgeableContainer(returnArg.SwiftTypeSpec, _env.TypeDatabase)))
            {
                csWriter.WriteLine($"return {ReturnLocalName};");
                return;
            }

            // Accessor-only: Optional<@objc protocol existential> — a single ObjC object pointer.
            // The extern returns IntPtr by value (nil = IntPtr.Zero); wrap a non-null pointer in the
            // proxy's single-payload container. No decomposed buffer, no SwiftOptional marshal — an
            // @objc protocol existential has AnyObject ABI, not the resilient container ABI below.
            if (_env.MethodDecl.IsAccessor &&
                _env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec) &&
                ExistentialHandler.IsObjCProtocolExistentialSpec(returnArg.SwiftTypeSpec, _env.TypeDatabase))
            {
                var objcInnerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec)!;
                var objcPublicType = _env.ExistentialHandler.GetPublicExistentialType(objcInnerProtocolList);
                // Unresolved protocol (publicType == "object") → no proxy class exists, fall through.
                if (objcPublicType != "object")
                {
                    var objcProxyName = _env.ExistentialHandler.GetRequiredProxyClassName(objcInnerProtocolList, _emissionContext);
                    var objcRln = ReturnLocalName;
                    csWriter.WriteLines($$"""
                        return {{objcRln}} == IntPtr.Zero
                            ? null
                            : new {{objcProxyName}}(new Swift.Runtime.ExistentialContainer1 { Payload0 = {{objcRln}} }, ownsContainer: true);
                        """);
                    return;
                }
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
                    var rln = ReturnLocalName;
                    if (_env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkType))
                    {
                        // Owned return: Swift returned the inner existential at +1 in the marshalled
                        // SwiftOptional; the well-known wrapper adopts and releases it on Dispose/finalize
                        // (ownsContainer: true) or the payload's +1 leaks.
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&{{rln}}));
                            if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                            return new {{wkType}}(swiftResult.Some{{ExistentialHandler.WellKnownOwnedTransferArg(wkType)}});
                            """);
                    }
                    else
                    {
                        var proxyName = _env.ExistentialHandler.GetRequiredProxyClassName(innerProtocolList, _emissionContext);
                        // Owned return: Swift returned the inner existential at +1 in the marshalled
                        // SwiftOptional; the proxy adopts and releases it on Dispose
                        // (ownsContainer: true) or the payload's +1 leaks.
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&{{rln}}));
                            if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                            return new {{proxyName}}(swiftResult.Some{{OwnedExistentialCtorArg(containerType)}});
                            """);
                    }
                    return;
                }
            }

            // Bound generics that return directly — use projection for type name
            if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnArg))
            {
                var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                    _env.NewProjectionContext(isParameter: false, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
                if (projection != null)
                {
                    var marshalType = projection.ContainerTypeName;
                    // Record closed generic for NativeAOT module initializer registration.
                    // Without this, NativeAOT trims the explicit ISwiftObject.GetTypeMetadata()
                    // on closed generic types, causing MarshalFromSwift<T> to fail.
                    _emissionContext.RecordBoundGenericSwiftObjectType(marshalType);

                    // Optional<Class/ObjC-rooted>: P/Invoke returns IntPtr (nullable pointer ABI).
                    // Construct the SwiftOptional via NewSome/NewNone instead of
                    // MarshalFromSwift<SwiftOptional<T>>(new IntPtr(&result)), which goes through
                    // VWT InitializeWithCopy. The VWT path can produce corrupted results on
                    // some runtimes (e.g., string data corruption when reading properties off
                    // the extracted class). The direct construction avoids VWT entirely.
                    // A carried Optional is wider than one word, so the nullable-pointer shape this
                    // arm assumes does not hold: the result local is a carrier struct, and the
                    // `result == IntPtr.Zero` test below would not even compile against it. The
                    // reference predicate answers a *bridging* question and says yes for Swift value
                    // types that bridge to ObjC objects (String among them), so it reaches shapes
                    // that keep their native multi-word layout on this path. Fall through to the
                    // general arm, which reads the whole value through the Optional's own metadata.
                    var returnCarrier = DirectOptionalAbi.TryGetDirectCarrier(
                        _env.MethodDecl, returnArg.SwiftTypeSpec, _env.TypeDatabase);

                    if (returnCarrier is null &&
                        projection is OptionalProjection optProj &&
                        CdeclParamMapper.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase) &&
                        !MarshallingHelpers.IsOptionalObjCBridged(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                    {
                        var innerType = optProj.InnerProjection.MarshalFromSwiftType;
                        var rln = ReturnLocalName;
                        csWriter.WriteLines($$"""
                            if ({{rln}} == IntPtr.Zero)
                                return SwiftOptional<{{innerType}}>.NewNone();
                            return SwiftOptional<{{innerType}}>.NewSome(({{innerType}})SwiftMarshal.MarshalFromSwift<{{innerType}}>({{rln}}));
                            """);
                        return;
                    }

                    {
                        var rln = ReturnLocalName;
                        // Bound-generic class returns use ClassPointer convention: the Swift
                        // @_cdecl wrapper returns the retained AnyObject pointer by value. Pass
                        // result directly to NewFromPayload — NOT &result, which would point at
                        // the stack-local IntPtr instead of the class instance.
                        if (MarshallingHelpers.IsBoundGenericClassReturn(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                        {
                            csWriter.WriteLines($$"""
                                return SwiftMarshal.MarshalFromSwift<{{marshalType}}>({{rln}});
                                """);
                        }
                        else if (projection is OptionalProjection)
                        {
                            // By-value Optional return: the callee handed back a +1 value sitting in
                            // the result local. Constructing the SwiftOptional takes its own copy
                            // through InitializeWithCopy, so the local's reference has to be
                            // value-witness-destroyed or every Some read leaks it. Methods get that
                            // from the projection's own Direct return plan; accessors never reach
                            // that plan (the projection rewrite early-outs for them), so the same
                            // consuming marshal is spelled out here rather than left to drift.
                            // Applied to every by-value Optional, not just the carried ones — the
                            // Direct plans make the same uniform choice, and a POD payload's witness
                            // Destroy is a no-op, so narrowing it by width would only reintroduce
                            // the leak for single-word containers like `[Element]?`.
                            csWriter.WriteLines($$"""
                                unsafe {
                                    return SwiftMarshal.MarshalFromSwiftObjectConsuming<{{marshalType}}>(&{{rln}});
                                }
                                """);
                        }
                        else
                        {
                            csWriter.WriteLines($$"""
                                unsafe {
                                    return SwiftMarshal.MarshalFromSwift<{{marshalType}}>(new IntPtr(&{{rln}}));
                                }
                                """);
                        }
                    }
                    return;
                }
                // Factory returned null — user-defined generic (e.g., Box<(T) -> ()>,
                // DownloadResponsePublisher<T1>). The factory can't project these because their
                // generic parameters may not satisfy ISwiftObject constraints. The wrapper signature
                // return type is correct here: WrapperSignatureBuilder resolves it via
                // TranslateBoundGenericTypeToCSharp which produces fully-qualified C# type names
                // (not AnyType). MarshalFromSwift<T> instantiates via ISwiftObject.NewFromPayload.
                var fallbackType = _wrapperSignature.ReturnType;
                _emissionContext.RecordBoundGenericSwiftObjectType(fallbackType);
                {
                    var rln = ReturnLocalName;
                    // Same ClassPointer-vs-buffer split as the projection branch above.
                    if (MarshallingHelpers.IsBoundGenericClassReturn(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                    {
                        csWriter.WriteLines($$"""
                            // Bound-generic fallback (ClassPointer): factory cannot project {{fallbackType}}
                            return SwiftMarshal.MarshalFromSwift<{{fallbackType}}>({{rln}});
                            """);
                    }
                    else
                    {
                        csWriter.WriteLines($$"""
                            // Bound-generic fallback: factory cannot project {{fallbackType}}
                            unsafe {
                                return SwiftMarshal.MarshalFromSwift<{{fallbackType}}>(new IntPtr(&{{rln}}));
                            }
                            """);
                    }
                }
                return;
            }

            // Handle closure return types - result is SwiftClosureData, wrap in delegate
            if (_env.ClosureHandler.IsClosure(returnArg))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    // Use invoke thunk for closure return when available (avoids delegate* unmanaged[Swift] crash)
                    var invokeThunkInfo = GetInvokeThunkInfoIfAvailable(closureTypeSpec);
                    var invokeThunkName = invokeThunkInfo?.entryPoint;
                    var invokeThunkLib = invokeThunkInfo?.libraryName;
                    var invokeThunkHelper = invokeThunkInfo?.helperName;

                    // See the sibling closure-return blocks: record the returned closure's invoker-ARGUMENT
                    // CONSUME degrade for any suppressed proxy. Pure read: byte-identical.
                    RecordClosureConsumeDegrade(closureTypeSpec.EachArgument());

                    // Throwing closures need special marshalling to handle SwiftError
                    if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
                    }
                    // Use non-frozen struct marshalling if any parameter is a non-frozen struct
                    // (requires heap allocation with NativeMemory and InitializeWithCopy/Destroy).
                    // Skipped when an invoke thunk is available — the thunk's CallConvCdecl invoker
                    // marshals struct args itself and avoids the raw delegate* unmanaged[Swift] call.
                    else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                    }
                    // Use frozen struct marshalling if any parameter is a frozen struct
                    // (uses stackalloc for stack allocation). Also skipped when a thunk is available.
                    else if (invokeThunkInfo == null && _env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName);
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, ReturnLocalName, invokeThunkName, invokeThunkLib, invokeThunkHelper);
                    }
                    return;
                }
                // Unsupported closure return: MemberEmissionValidator should have caught this,
                // but guard against fallthrough to GetTypeRecordOrThrow (which crashes on ClosureTypeSpec).
                csWriter.WriteLine($"return {ReturnLocalName};");
                return;
            }

            // @_cdecl existential returns: read the container from the actual result location
            // (indirect out-param when box arrives via the buffer, function-result local on the
            // direct-return convention) and wrap in proxy class.
            if (TryEmitCdeclExistentialReturn(csWriter, returnArg))
                return;

            // Handle existential return types (any Protocol) - wrap container in proxy
            if (_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec)!;

                // Any (zero-protocol existential) → no proxy class; return container directly
                // ExistentialContainer0 boxes to 'object' matching the public return type
                if (protocolList.Protocols.Count == 0)
                {
                    csWriter.WriteLine($"return {ReturnLocalName};");
                    return;
                }

                // Metatype/unresolved existential → GetPublicExistentialType returns "object"
                // No proxy class exists; return container directly (public type is AnyType via [UnsupportedSwiftType])
                // Return position (pure read) → allow the PAT-with-conformers union projection.
                var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList, allowUnionProjection: _env.AllowsExistentialReturnUnionProjection);
                if (publicType == "object")
                {
                    csWriter.WriteLine($"return {ReturnLocalName};");
                    return;
                }

                // PAT protocol with known conformers → ExistentialUnion (no proxy, uses try-cast).
                if (publicType == "Swift.Runtime.ExistentialUnion")
                {
                    csWriter.WriteLine($"return new Swift.Runtime.ExistentialUnion({ReturnLocalName});");
                    return;
                }

                // Well-known protocol types (Swift.Error → AnyError) use direct runtime type
                if (_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownReturnType))
                {
                    csWriter.WriteLine($"return new {wellKnownReturnType}({ReturnLocalName}{ExistentialHandler.WellKnownOwnedTransferArg(wellKnownReturnType)});");
                    return;
                }

                var proxyClassName = _env.ExistentialHandler.GetRequiredProxyClassName(protocolList, _emissionContext);
                // Owned return: Swift returned the existential at +1 (EC1 or EC2+ composition).
                var nonCdeclContainerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                csWriter.WriteLine($"return new {proxyClassName}({ReturnLocalName}{OwnedExistentialCtorArg(nonCdeclContainerType)});");
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
                    csWriter.WriteLine($"if ({ReturnLocalName}.Equals(default({containerType}))) return null;");
                    // Well-known protocol types (Swift.Error → AnyError) use direct runtime type
                    if (_env.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wellKnownOptType))
                    {
                        csWriter.WriteLine($"return new {wellKnownOptType}({ReturnLocalName}{ExistentialHandler.WellKnownOwnedTransferArg(wellKnownOptType)});");
                    }
                    else
                    {
                        var optProxyClassName = _env.ExistentialHandler.GetRequiredProxyClassName(innerProtocolList, _emissionContext);
                        // Owned return: Swift returned the inner existential at +1 (EC1 or EC2+ composition); the proxy
                        // adopts and releases it on Dispose or the payload's +1 leaks.
                        csWriter.WriteLine($"return new {optProxyClassName}({ReturnLocalName}{OwnedExistentialCtorArg(containerType)});");
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
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType}){ReturnLocalName};");
                    return;
                }

                // ObjC bridged types: wrap IntPtr result with GetNSObject<T>
                // ObjC-bridgeable value types (URL): same pattern — IntPtr → GetNSObject<T>
                // Swift wrapper (both @_cdecl and @_silgen_name) returns +1 via passRetained or
                // Swift calling convention. GetNSObject adds +1 via DangerousRetain.
                // DangerousRelease() balances, matching the SwiftHandle ctor for ObjC-rooted classes.
                // CoreFoundation: ownsReference=true changes GetINativeObject's owns param directly.
                if (MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCBridgeable(typeRecord))
                {
                    // An Apple NS_STRING_ENUM projects to a C# enum, not an NSObject subclass, so the
                    // pointer has to be materialized as its NSString carrier and then converted —
                    // bridging straight to the public type would ask GetNSObject<T> for an enum.
                    // The retain balancing is unchanged: the release still runs on the carrier.
                    var typedEnumCarrier = typeRecord.Flags.HasFlag(TypeRecordFlags.AppleTypedEnum)
                        ? typeRecord.NativeTypeName?.FullyQualifiedName
                        : null;
                    if (typedEnumCarrier is not null)
                    {
                        var typedEnum = new AppleTypedEnumAdapter(_wrapperSignature.ReturnType, typedEnumCarrier);
                        csWriter.WriteLine($"var _objcResult = {MarshallingHelpers.FormatObjCBridgeCall(typedEnum.CarrierType, ReturnLocalName, nonNull: true)};");
                        csWriter.WriteLine($"_objcResult.DangerousRelease();");
                        csWriter.WriteLine($"return {typedEnum.FromCarrier("_objcResult")};");
                    }
                    else if (MarshallingHelpers.IsCoreFoundationType(_wrapperSignature.ReturnType))
                    {
                        csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, ReturnLocalName, nonNull: true, ownsReference: true)};");
                    }
                    else
                    {
                        csWriter.WriteLine($"var _objcResult = {MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, ReturnLocalName, nonNull: true)};");
                        csWriter.WriteLine($"_objcResult.DangerousRelease();");
                        csWriter.WriteLine($"return _objcResult;");
                    }
                    return;
                }

                // Swift classes return pointer directly — pass to MarshalFromSwift which calls
                // NewFromPayload to create a SwiftClassHandle wrapping the pointer. No buffer needed.
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType})SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>({ReturnLocalName});");
                    return;
                }

                // Complex enums (non-simple) have SafeHandle-based opaque payloads — P/Invoke returns IntPtr
                if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                {
                    csWriter.WriteLine($"return ({_wrapperSignature.ReturnType})SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>({ReturnLocalName});");
                    return;
                }

                // Frozen with memory management — MarshalFromSwift from buffer
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 && (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    var rln = ReturnLocalName;
                    csWriter.WriteLine($$"""
                        unsafe {
                            return SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(&{{rln}}));
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

            csWriter.WriteLine($"return {ReturnLocalName};");
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
                _env.NewProjectionContext(isParameter: false, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
            if (projection == null) return false;

            var strategy = DetermineReturnStrategy();
            bool usesCdecl = _env.MethodDecl.UsesCdeclWrapper;
            string resultName = strategy switch
            {
                // @_cdecl uses IntPtr resultPtr; thunks use SwiftIndirectResult (x8 passthrough).
                ReturnStrategy.IndirectResult => usesCdecl ? ResultPtrName : $"new IntPtr({SwiftIndirectResultName}.Value)",
                ReturnStrategy.OutBuffer => "_optRetPtr",
                _ => ReturnLocalName
            };

            var plan = projection.GetReturnPlan(resultName, strategy);

            // @_cdecl indirect result with PassThrough projection (e.g. BlittableProjection for frozen structs):
            // PassThrough would emit "return resultPtr;" but resultPtr is IntPtr, not the return type.
            // Fall through to the MarshalFromSwift<T> fallback at the IndirectResult handler below.
            if (strategy == ReturnStrategy.IndirectResult && usesCdecl &&
                plan.SetupStatements.Count == 0 && plan.CleanupStatements.Count == 0 &&
                plan.PInvokeExpression == resultName)
                return false;

            // @_cdecl indirect result with ExistentialProjection: the projection wraps resultName
            // in a proxy (e.g. "new DescribableProxy(resultPtr)"), but resultPtr is IntPtr — the
            // container must be read first via SwiftMarshal.MarshalFromSwift<T>(resultPtr).
            // Fall through to the dedicated @_cdecl existential return handler below.
            if (strategy == ReturnStrategy.IndirectResult && usesCdecl &&
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
                csWriter.WriteLine($"return {ReturnLocalName};");
                return;
            }

            var elements = tupleTypeSpec.Elements;
            var marshalLines = new List<string>();
            var resultElements = new List<string>();
            bool needsMarshalling = false;

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var itemName = $"{ReturnLocalName}.Item{i + 1}";
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
                csWriter.WriteLine($"return {ReturnLocalName};");
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
            csWriter.WriteLine($"var _tupleMetaPtr = {ReturnMetadataName}.AsTupleMetadata();");

            // Step 1: Read each element from the buffer into a typed local matching the
            // P/Invoke type. The Swift @_cdecl wrapper writes the entire tuple inline via
            // `resultPtr.initializeMemory(as: TupleType.self)`, so ALL elements are inline
            // in the buffer — regardless of whether they're primitives, structs, or classes.
            //
            // For reference types (classes, ObjC), the inline value IS a pointer (8 bytes),
            // so *(IntPtr*) correctly reads the pointer value for NewFromPayload.
            //
            // For value types > 8 bytes (String=16B, Data=16B, frozen/non-frozen structs),
            // the inline data is larger than IntPtr. We must compute the ADDRESS of the data
            // within the buffer and pass that to MarshalFromSwift, which reads the full size
            // via NewFromPayload's VWT copy.
            var rawNames = new List<string>();
            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var rawName = $"_raw{i}";
                var pinvokeType = GetPInvokeTypeForTupleElement(element);
                var offsetExpr = $"(nint)_tupleMetaPtr->GetElementOffset({i})";

                if (pinvokeType == "IntPtr" && IsTupleElementInlineValue(element))
                {
                    // Inline value type: compute address within buffer (data may be > 8 bytes).
                    // MarshalFromSwift/NewFromPayload reads the full value from this address.
                    csWriter.WriteLine($"IntPtr {rawName} = (IntPtr)((byte*){resultExpr} + {offsetExpr});");
                }
                else
                {
                    // Primitive (reads exact size) or reference type (reads 8-byte pointer).
                    csWriter.WriteLine($"{pinvokeType} {rawName} = *({pinvokeType}*)((byte*){resultExpr} + {offsetExpr});");
                }
                rawNames.Add(rawName);
            }

            // Step 2: Apply projection-aware marshalling via GetTupleElementMarshalCode.
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
        internal string GetPInvokeTypeForTupleElement(TypeSpec element)
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

                // Non-primitive frozen structs use IntPtr. The Swift @_cdecl wrapper writes
                // them inline in the tuple buffer via initializeMemory(as:). The companion
                // method IsTupleElementInlineValue() tells the reader to use address-of
                // instead of dereference, so MarshalFromSwift gets a pointer to the full
                // inline data (which may be > 8 bytes).
                if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
                {
                    // Blittable primitive structs (Int, Double, etc.) are passed directly
                    // by the Swift wrapper — only non-primitive structs are heap-allocated.
                    if (IsSwiftPrimitive(named.Name))
                        return typeRecord.CSharpTypeName.FullyQualifiedName;
                    return "IntPtr";
                }

                // Fallback — IntPtr is safe for any unknown type
                return "IntPtr";
            }

            return "IntPtr";
        }

        /// <summary>
        /// Determines whether a tuple element is stored as an inline value in the tuple buffer
        /// (vs. a reference/pointer). The Swift @_cdecl wrapper writes the entire tuple via
        /// <c>resultPtr.initializeMemory(as: TupleType.self)</c>, so all elements are inline.
        /// For reference types (classes, ObjC), the inline value IS a pointer (8 bytes) and
        /// NewFromPayload expects the pointer value directly. For value types (structs, enums,
        /// String, Data), the inline data may be larger than IntPtr and NewFromPayload expects
        /// a pointer TO the data.
        /// </summary>
        private bool IsTupleElementInlineValue(TypeSpec element)
        {
            // String (16 bytes) and Data (16 bytes) are inline value types
            if (MarshallingHelpers.IsSwiftString(element))
                return true;
            if (element is NamedTypeSpec dataCheck && dataCheck.Name == "Foundation.Data")
                return true;

            if (element is NamedTypeSpec named)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);

                // Classes store a pointer — NewFromPayload wraps the pointer value directly
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return false;

                // ObjC bridged types store a pointer
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                    return false;

                // Structs (frozen and non-frozen) store inline data.
                // Primitives (Int, Double, etc.) are read with their actual type, not IntPtr,
                // so they don't reach this check. Non-primitive structs (FrozenPoint, etc.)
                // use IntPtr and store inline data that may be > 8 bytes.
                if (typeRecord.Kind == TypeRecordKind.Struct)
                    return true;

                // Complex enums store inline data (tag + payload)
                if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return true;
            }

            // Bound generics: Array<T>, Dictionary<K,V>, Set<T> are 8-byte value types
            // containing a single pointer. NewFromPayload wraps the pointer value directly,
            // so dereference is correct. Optional<T> varies but is handled by the
            // Optional-specific code path in GetTupleElementMarshalCode.
            // Default to dereference (pointer) for unknown/generic types.
            return false;
        }

        /// <summary>
        /// Gets the C# type name for a tuple element.
        /// </summary>
        /// <param name="element">The TypeSpec for the tuple element.</param>
        /// <param name="applyIdiomaticConversion">When true, converts bare SwiftString to string. Set to false for recursive calls inside generics.</param>
        internal string GetCSharpTypeForTupleElement(TypeSpec element, bool applyIdiomaticConversion = true)
        {
            // Resolve generic type parameters (τ_0_0 → T) via GenericContext
            if (TypeSpecHelpers.IsGenericTypeParameter(element) && element is NamedTypeSpec genericParam)
            {
                if (_genericContext.TryResolve(genericParam.Name, out var csTypeName))
                    return csTypeName;
            }

            // Handle bound generics — try factory projection first for idiomatic types
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var projection = s_projectionFactory.Project(element,
                    _env.NewProjectionContext(isParameter: false, genericContext: _genericContext));
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

                // Foundation.Date → System.DateTimeOffset. Date's TypeRecord maps to the raw
                // P/Invoke type "double" (Swift Date is a frozen struct wrapping a 2001-epoch
                // Double), so without this special-case the element is surfaced as a bare double,
                // diverging from the scalar DateProjection. Marshalling in GetTupleElementMarshalCode
                // reads the double and applies the epoch conversion (only at top level).
                if (applyIdiomaticConversion && named.Name == "Foundation.Date")
                    return "System.DateTimeOffset";

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Generates marshalling code for a single tuple element.
        /// </summary>
        internal string? GetTupleElementMarshalCode(TypeSpec element, string itemName, string resultName, string csharpType)
        {
            // Handle generic type parameters (τ_0_0 → T) — received as IntPtr from heap-allocated buffer.
            // Use SwiftMarshal.MarshalFromSwift<T> which resolves via type metadata at runtime.
            if (TypeSpecHelpers.IsGenericTypeParameter(element))
            {
                return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>({itemName});";
            }

            // Handle bound generic types (Optional<T>, Array<T>, etc.)
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                // Check for Optional<ObjC> first — ObjC types use bare IntPtr (null = IntPtr.Zero),
                // NOT SwiftOptional buffer layout. Must skip projection to use the ObjC special-case below.
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                bool isOptionalObjC = false;
                bool isOptionalObjCBridgeableValue = false;
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0 &&
                    namedType.GenericParameters[0] is NamedTypeSpec innerObjCCheck &&
                    _env.TypeDatabase.TryGetTypeRecord(innerObjCCheck, out var innerObjCRecord))
                {
                    if (MarshallingHelpers.IsObjCBridged(innerObjCRecord))
                        isOptionalObjC = true;
                    else if ((innerObjCRecord.Kind == TypeRecordKind.Struct || innerObjCRecord.Kind == TypeRecordKind.Enum)
                             && MarshallingHelpers.IsObjCBridgeable(innerObjCRecord)
                             && innerObjCRecord.NativeTypeName != null)
                        isOptionalObjCBridgeableValue = true;
                }

                // Optional<ObjCBridgeable value> tuple element (e.g. tuple has Optional<URL>): the
                // Swift wrapper's tuple loop emits `result.X.map { passRetained($0 as AnyObject).toOpaque() }`,
                // so the C# side receives an IntPtr (or zero for nil). Bridge to the NS native type
                // (e.g. NSUrl) and balance the +1 from passRetained with DangerousRelease.
                if (isOptionalObjCBridgeableValue
                    && namedType.GenericParameters[0] is NamedTypeSpec _optBridgeInner
                    && _env.TypeDatabase.TryGetTypeRecord(_optBridgeInner, out var _optBridgeRec)
                    && _optBridgeRec.NativeTypeName != null)
                {
                    var nativeName = _optBridgeRec.NativeTypeName.FullyQualifiedName;
                    var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(nativeName, itemName, nonNull: true);
                    return $"var {resultName} = {itemName} == IntPtr.Zero ? ({nativeName}?)null : {bridgeCall};\n{resultName}?.DangerousRelease();";
                }

                // Try factory projection for idiomatic return marshalling (skip Optional<ObjC>)
                if (!isOptionalObjC)
                {
                    var projection = s_projectionFactory.Project(element,
                        _env.NewProjectionContext(isParameter: false, genericContext: _genericContext));
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
                            // Optional ObjC type: IntPtr -> nullable reference type
                            // ObjC classes don't implement ISwiftObject, so SwiftOptional<T> can't get
                            // type metadata for them. Use direct null check + GetNSObject instead.
                            // The Swift wrapper retained the element via passRetained (+1) at the
                            // tuple loop; GetNSObject also retains via DangerousRetain (+1).
                            // DangerousRelease balances the passRetained side back to the SwiftHandle
                            // ctor's natural +1.
                            var innerCSharp = innerRecord.CSharpTypeName.FullyQualifiedName;
                            return $"var {resultName} = {itemName} == IntPtr.Zero ? ({innerCSharp}?)null : {MarshallingHelpers.FormatObjCBridgeCall(innerCSharp, itemName, nonNull: true)};\n{resultName}?.DangerousRelease();";
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
                    // ObjC bridged types: the Swift wrapper retained the element via passRetained
                    // (+1) at the tuple loop in WrapperEmitter.Async.cs; GetNSObject also retains
                    // via DangerousRetain (+1). DangerousRelease balances the passRetained side
                    // back to the SwiftHandle ctor's natural +1.
                    if (MarshallingHelpers.IsObjCBridged(typeRecord))
                    {
                        return $"var {resultName} = {MarshallingHelpers.FormatObjCBridgeCall(csharpType, itemName, nonNull: true)};\n{resultName}?.DangerousRelease();";
                    }
                    // ObjCBridgeable value type (Foundation.URL → NSUrl): the Swift wrapper's tuple
                    // loop bridges via `Unmanaged.passRetained(elem as AnyObject).toOpaque()` instead
                    // of heap-alloc'ing raw struct bytes. The C# side reads IntPtr → GetNSObject<NSUrl>
                    // and balances the +1 retain with DangerousRelease, mirroring the scalar path.
                    if ((typeRecord.Kind == TypeRecordKind.Struct || typeRecord.Kind == TypeRecordKind.Enum)
                        && MarshallingHelpers.IsObjCBridgeable(typeRecord)
                        && typeRecord.NativeTypeName != null)
                    {
                        var nativeName = typeRecord.NativeTypeName.FullyQualifiedName;
                        var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(nativeName, itemName, nonNull: true);
                        return $"var {resultName} = {bridgeCall};\n{resultName}?.DangerousRelease();";
                    }
                }
            }

            // Swift.String → string conversion (received as IntPtr to heap-allocated buffer)
            // Must check before generic IntPtr path since non-primitive structs all use IntPtr.
            if (MarshallingHelpers.IsSwiftString(element))
                return $"var {resultName} = SwiftMarshal.MarshalFromSwiftObject<SwiftString>({itemName}).ToString();";

            // Foundation.Data → byte[] conversion (received as IntPtr to heap-allocated buffer).
            // Name-gated bypass of the projection path that records the supplement reference —
            // record here so the generated csproj carries the SwiftBindings.Apple PackageReference.
            if (element is NamedTypeSpec dataElement && dataElement.Name == "Foundation.Data")
            {
                AppleSupplementReferences.Record("Foundation.Data", "WrapperEmitter.Return:TupleElementFoundationData");
                return $"var {resultName} = (*(Swift.Foundation.Data*)(void*){itemName}).ToByteArray();";
            }

            // Foundation.Date → System.DateTimeOffset conversion. The inline buffer holds the raw
            // 2001-epoch Double (read via MarshalFromSwift<double>); mirror the scalar
            // DateProjection's epoch conversion instead of surfacing the bare double.
            if (element is NamedTypeSpec dateElement && dateElement.Name == "Foundation.Date")
                return $"var {resultName} = {DateProjection.SwiftEpoch}.AddSeconds(SwiftMarshal.MarshalFromSwift<double>({itemName}));";

            // Use the computed P/Invoke type to determine marshalling
            if (pinvokeType == "IntPtr")
            {
                // IntPtr IS the pointer — pass directly (no address-of)
                // Use unconstrained MarshalFromSwift<T> here because csharpType may be an ObjC enum
                // (not ISwiftObject). The constrained path is used in projection-layer code where
                // the type is statically known to implement ISwiftObject.
                return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>({itemName});";
            }
            else if (pinvokeType.EndsWith(".Buffer"))
            {
                // SwiftString.Buffer → string (via MarshalFromSwift + ToString)
                if (MarshallingHelpers.IsSwiftString(element))
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwiftObject<SwiftString>(new IntPtr(&{itemName})).ToString();";
                // Other .Buffer types (frozen structs with memory management)
                return $"var {resultName} = SwiftMarshal.MarshalFromSwiftObject<{csharpType}>(new IntPtr(&{itemName}));";
            }

            // Simple enums: P/Invoke uses underlying type (int, long), need cast to C# enum
            if (element is NamedTypeSpec enumNamed &&
                _env.TypeDatabase.TryGetTypeRecord(enumNamed, out var enumRecord) &&
                enumRecord.Kind == TypeRecordKind.Enum && enumRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                return $"var {resultName} = ({csharpType}){itemName};";
            }

            // Frozen blittable primitives — use directly
            return $"var {resultName} = {itemName};";
        }

        /// <summary>
        /// Emits the read site for a multi-element generic-element tuple return whose elements
        /// were written into N separate @out buffers (tupleResult{i}Ptr → backed by _cdeclBuf for
        /// element 0, _tupleResult{i}Buf for elements 1..N-1).
        ///
        /// Per element, dispatches at runtime on the C# generic param's actual type:
        ///   * class T (ISwiftObject, not ISwiftStruct, not value type) — buffer holds the heap
        ///     pointer; dereference, then MarshalFromSwift&lt;T&gt;(classHandle). Buffer is freed
        ///     normally in finally.
        ///   * ISwiftStruct T (non-frozen struct, complex enum) — MarshalFromSwift takes
        ///     ownership of the buffer (SafeHandle wraps it). Null the buffer local so finally
        ///     skips the free.
        ///   * value type / frozen-blittable / primitive T — MarshalFromSwift copies bytes out;
        ///     buffer is freed normally in finally.
        ///
        /// After all elements are read, synthesizes the C# value-tuple `(elem0, elem1, …)`.
        /// </summary>
        private void EmitMultiElementGenericTupleReturn(CSharpWriter csWriter, TupleTypeSpec tupleSpec)
        {
            var elementCount = tupleSpec.Elements.Count;
            var elementCsTypes = new List<string>(elementCount);
            foreach (var element in tupleSpec.Elements)
            {
                elementCsTypes.Add(ResolveGenericTupleElementCSharpType(element));
            }

            for (int i = 0; i < elementCount; i++)
            {
                var elemType = elementCsTypes[i];
                var ptrName = $"tupleResult{i}Ptr";
                var bufferVar = i == 0 ? "_cdeclBuf" : $"_tupleResult{i}Buf";
                var resultName = $"_tupleResult{i}";
                csWriter.WriteLines($$"""
                    {{elemType}} {{resultName}};
                    if (!typeof({{elemType}}).IsValueType && typeof(Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof({{elemType}})) && !typeof(Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({{elemType}})))
                    {
                        IntPtr _classHandle{{i}};
                        unsafe { _classHandle{{i}} = *(IntPtr*){{ptrName}}; }
                        {{resultName}} = SwiftMarshal.MarshalFromSwift<{{elemType}}>(_classHandle{{i}});
                    }
                    else
                    {
                        {{resultName}} = SwiftMarshal.MarshalFromSwift<{{elemType}}>({{ptrName}});
                        if (typeof(Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({{elemType}}))) {{bufferVar}} = null;
                    }
                    """);
            }

            var elementValues = string.Join(", ", Enumerable.Range(0, elementCount).Select(i => $"_tupleResult{i}"));
            csWriter.WriteLine($"return ({elementValues});");
        }

        /// <summary>
        /// Translates a tuple element TypeSpec to its C# type name for use inside
        /// <c>typeof(T)</c> / <c>MarshalFromSwift&lt;T&gt;</c> within the generated wrapper body.
        /// Bare generic parameters resolve via the active <see cref="GenericContext"/>;
        /// concrete and bound-generic types delegate to <see cref="TupleHandler"/>.
        /// </summary>
        private string ResolveGenericTupleElementCSharpType(TypeSpec element)
        {
            if (element is NamedTypeSpec ns &&
                TypeSpecHelpers.IsGenericTypeParameter(ns.Name) &&
                _genericContext.TryResolve(ns.Name, out var csName))
            {
                return csName;
            }
            return _env.TupleHandler.TranslateElementTypeToCSharp(element);
        }
    }
}
