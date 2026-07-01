// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Builds the P/Invoke signature (low-level native interop).
    /// </summary>
    public class PInvokeSignatureBuilder : SignatureBuilderBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PInvokeSignatureBuilder"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        public PInvokeSignatureBuilder(MethodEnvironment env) : base(env)
        {
        }

        /// <summary>
        /// Handles the return type of the method.
        /// </summary>
        public void HandleReturnType()
        {
            var returnType = _env.MethodDecl.CSSignature.First();

            // For non-constructor methods, bound generics that require marshalling (SwiftArray, SwiftOptional, etc.)
            // return IntPtr directly from PInvoke. Constructors need special handling via indirect result
            // since failable initializers return Optional<Self> which can't be assigned to 'this'.
            if (!_env.MethodDecl.IsConstructor && _env.BoundGenericsHandler.IsBoundGeneric(returnType))
            {
                // @_cdecl Optional<value-type>: fall through to IndirectResult path.
                // MethodRequiresIndirectResult returns true for these, adding resultPtr below.
                if (_env.MethodDecl.UsesCdeclWrapper &&
                    MethodWrapperEmitter.IsOptionalType(returnType.SwiftTypeSpec) &&
                    !CdeclParamMapper.IsOptionalWithReferenceInner(returnType.SwiftTypeSpec, _env.TypeDatabase))
                {
                    // Fall through to MethodRequiresIndirectResult check below
                }
                else if (_env.MethodDecl.UsesCdeclWrapper &&
                    _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) &&
                    MethodWrapperEmitter.IsSupportedCollectionType(returnType.SwiftTypeSpec) &&
                    !CdeclParamMapper.IsObjCBridgeableContainer(returnType.SwiftTypeSpec, _env.TypeDatabase))
                {
                    // @_cdecl non-ObjC collection returns (Array, Dict, Set): fall through to IndirectResult path.
                    // Swift wrapper writes to resultPtr via initializeMemory(as:).
                    // ObjC-bridgeable containers use ClassPointer return (UnsafeMutableRawPointer) instead.
                }
                else if (_env.MethodDecl.UsesCdeclWrapper &&
                    _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) &&
                    !MethodWrapperEmitter.IsOptionalType(returnType.SwiftTypeSpec) &&
                    !CdeclParamMapper.IsObjCBridgeableContainer(returnType.SwiftTypeSpec, _env.TypeDatabase))
                {
                    // @_cdecl non-optional, non-ObjC-bridgeable bound generic struct returns (e.g., Pair<A, B>):
                    // fall through to IndirectResult path. Swift wrapper writes to resultPtr
                    // via initializeMemory(as:). Optional and ObjC-bridgeable containers have their own paths.
                }
                else if (_env.MethodDecl.UsesCdeclWrapper &&
                    returnType.SwiftTypeSpec is NamedTypeSpec simdRetNts &&
                    TypeDatabaseExtensions.TryResolveBoundGenericAlias(_env.TypeDatabase, simdRetNts, out _))
                {
                    // Bound-generic SIMD alias return (e.g., SIMD2<Float> → Vector2): the Swift @_cdecl
                    // wrapper uses indirect result (resultPtr + void return). Fall through so the
                    // IndirectResult path emits the matching C# PInvoke — resultPtr IntPtr arg, void return.
                    // Do NOT take the by-value `SetReturnType(TranslateBoundGenericTypeToCSharp(...))` path
                    // below, which would silently drop the resultPtr and mismatch the Swift wrapper ABI.
                }
                else
                {
                    // Direct CallConvSwift fallback (no @_cdecl, no native thunk, no wrapper lib):
                    // when the wrapper body needs indirect result (Swift sret) — typical for
                    // generic-typed bound-generic returns like Optional<τ_0_0> on generic structs —
                    // fall through to the unified indirect-result branch so the P/Invoke signature
                    // adds SwiftIndirectResult and returns void. Without this, the wrapper
                    // allocates _cdeclBuf + builds swiftIndirectResult but the P/Invoke is emitted
                    // as IntPtr-returning without the buffer arg, leaving uninitialized memory.
                    if (MarshallingHelpers.MethodRequiresIndirectResult(_env) &&
                        !_env.MethodDecl.UsesNativeThunk &&
                        !_env.MethodDecl.HasOptionalPointerWrapper &&
                        !_env.MethodDecl.UsesWrapperLibrary)
                    {
                        // Fall through to MethodRequiresIndirectResult branch below.
                    }
                    // Large Optional returns use out-buffer pattern — PInvoke returns void
                    // Guard: only when a Swift wrapper exists to handle the buffer
                    else if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                        (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary))
                    {
                        SetReturnType("void");
                        return;
                    }
                    else
                    {
                        var csTypeParam = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) switch
                        {
                            true => _env.BoundGenericsHandler.GetBufferType(returnType),
                            false => _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnType, _genericContext)
                        };
                        SetReturnType(csTypeParam);
                        return;
                    }
                }
            }

            // Handle closure return types (including optional closures)
            // Swift returns closures as SwiftClosureData (function + context pointers)
            // Optional closures use the same struct - nil is represented by zero pointers
            if (_env.ClosureHandler.IsClosure(returnType))
            {
                // @_cdecl: fall through to IndirectResult path — closures written to resultPtr buffer
                if (_env.MethodDecl.UsesCdeclWrapper)
                {
                    // Fall through to MethodRequiresIndirectResult check below
                }
                else
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnType)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        SetReturnType("SwiftClosureData");
                    }
                    else
                    {
                        SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    }
                    return;
                }
            }

            // Handle tuple return types
            // Generic-element tuples (e.g., (T, U)) fall through to the indirect result check below,
            // which adds SwiftIndirectResult + void return. This only works for sync methods —
            // async methods use flattened callback params instead of indirect result.
            if (_env.TupleHandler.IsTuple(returnType.SwiftTypeSpec))
            {
                var tupleTypeSpec = (TupleTypeSpec)returnType.SwiftTypeSpec;
                bool hasGenericElements = _env.TupleHandler.HasGenericTypeParameterElements(tupleTypeSpec);
                if (!hasGenericElements)
                {
                    if (_env.MethodDecl.UsesCdeclWrapper)
                    {
                        // @_cdecl: fall through to indirect result path below.
                        // Tuples aren't C-representable — wrapper writes to resultPtr buffer.
                    }
                    else
                    {
                        if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                            _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                            SetReturnType(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec, _genericContext));
                        else
                            SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                        return;
                    }
                }
                if (_env.MethodDecl.IsAsync)
                {
                    // Async P/Invoke returns void (PInvokeEmitHelper overrides to "void").
                    // Tuple callbacks are emitted by EmitAsyncWrapperForTuple with flattened
                    // params — they don't use ReturnType. Set it to the tuple type for
                    // consistency; unsupported tuples get AnyType (method effectively skipped).
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                        _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                    {
                        SetReturnType(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec, _genericContext));
                    }
                    else
                    {
                        SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    }
                    return;
                }
                // Generic-element tuples (sync): fall through to indirect result handling
            }

            // @objc protocol existential return (scalar `any P` or optional `(any P)?`): a single
            // ObjC object pointer (no witness table, no descriptor), returned BY VALUE — nil =
            // IntPtr.Zero. Identical wire to a class reference, NOT the 40-byte opaque container via
            // sret. Decided before the generic existential / optional-existential arms below (which
            // would route to the indirect-result resultPtr/hasValuePtr path) and before the property
            // getter's forceIndirectForOptionalExistential block (which would do the same).
            if (ExistentialHandler.IsObjCProtocolExistentialSpec(returnType.SwiftTypeSpec, _env.TypeDatabase))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Handle existential return types (any Protocol)
            if (_env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnType.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    if (_env.MethodDecl.UsesCdeclWrapper)
                    {
                        // @_cdecl: fall through to indirect result path below.
                        // Existential containers aren't C-representable — use resultPtr buffer.
                    }
                    else
                    {
                        var existentialType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                        SetReturnType(existentialType);
                        return;
                    }
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    return;
                }
                // @_cdecl supported existential: fall through to MethodRequiresIndirectResult
            }

            // Handle Optional-wrapped existential return types like (any DataCaching)?
            if (_env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnType.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    if (_env.MethodDecl.UsesCdeclWrapper)
                    {
                        // @_cdecl: fall through to indirect result path — Optional<ExistentialContainer>
                        // is too large for register return. Uses decomposed (resultPtr + hasValuePtr).
                    }
                    else if (MarshallingHelpers.MethodRequiresIndirectResult(_env) &&
                             !_env.MethodDecl.UsesNativeThunk &&
                             !_env.MethodDecl.HasOptionalPointerWrapper &&
                             !_env.MethodDecl.UsesWrapperLibrary)
                    {
                        // Direct CallConvSwift (no @_cdecl, no native thunk, no wrapper lib):
                        // Optional<ExistentialContainer> is ≥41 bytes (5-word existential + tag),
                        // address-only in Swift's ABI — returned via sret. Fall through to the
                        // SwiftIndirectResult branch below so the PInvoke signature includes the
                        // sret slot the wrapper body's `_cdeclBuf` allocation writes to. Without
                        // this fall-through, MethodRequiresIndirectResult drives WrapperEmitter
                        // to allocate a buffer + read from it, but the PInvoke signature emits
                        // IntPtr return without the sret arg — the call site reads uninitialized
                        // memory and fabricates an AnyError over random bits.
                    }
                    else
                    {
                        // Native thunk / wrapper library / optional-pointer-wrapper paths keep the
                        // legacy IntPtr return shape — those layers carry their own sret handling.
                        SetReturnType("IntPtr");
                        return;
                    }
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    return;
                }
                // @_cdecl supported or sync sret: fall through to MethodRequiresIndirectResult
            }

            // String @_cdecl property wrappers use indirect result (resultPtr) because
            // @_cdecl can't return Swift structs (SBW_Utf8Slice). Falls through to indirect result path below.

            // DynamicSelf (Self return type) on @_cdecl class wrappers or native thunks:
            // Swift wrapper returns retained class pointer (UnsafeMutableRawPointer) directly.
            // P/Invoke receives IntPtr — no indirect result needed.
            // Thunks also return class pointers in x0 (single register, no indirect result).
            if (returnType.SwiftTypeSpec.IsDynamicSelf && (_env.MethodDecl.UsesCdeclWrapper || _env.MethodDecl.UsesNativeThunk))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Failable class constructor via @_cdecl wrapper: the Swift wrapper returns a nullable
            // retained class pointer (UnsafeMutableRawPointer?) directly, so the P/Invoke receives
            // IntPtr (Zero == nil) — no indirect resultPtr (which would shift every argument by one
            // slot). The bare return spec here is Optional<Self>, so the general return-type-record
            // resolution below would misclassify it; short-circuit explicitly. This mirrors the
            // non-failable class constructor (returnTypeRecord.Kind == Class → IntPtr) and the
            // !isClass decision the Swift CdeclSignatureContract already makes.
            if (_env.MethodDecl.IsConstructor && _env.MethodDecl.IsFailable &&
                _env.MethodDecl.UsesCdeclConstructorWrapper && _env.ParentDecl is ClassDecl)
            {
                SetReturnType("IntPtr");
                return;
            }

            // Optional<existential> property getters: force indirect result. The return type is
            // Optional<ExistentialContainer> which is too large for register return. The @_cdecl
            // wrapper writes to resultPtr + hasValuePtr, so force the void+resultPtr+hasValuePtr path.

            bool forceIndirectForOptionalExistential = false;
            if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                !_env.MethodDecl.IsSubscriptAccessor &&
                returnType.SwiftTypeSpec != null)
            {
                forceIndirectForOptionalExistential =
                    _env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec) ||
                    CdeclParamMapper.IsProtocolExistentialType(returnType.SwiftTypeSpec, _env.TypeDatabase);
            }


            if (forceIndirectForOptionalExistential || MarshallingHelpers.MethodRequiresIndirectResult(_env))
            {
                if (_env.MethodDecl.UsesCdeclWrapper)
                {
                    // @_cdecl wrapper: plain IntPtr result buffer, not SwiftIndirectResult register.
                    // Under CallConvCdecl, SwiftIndirectResult would be placed in a regular register (x0)
                    // instead of the x8 register that the wrapper expects.
                    // NOTE: Native thunks are NOT included here. Thunks rely on AAPCS64's hidden x8
                    // register for struct return buffers (the thunk prologue does `mov x19, x8`).
                    // Replacing SwiftIndirectResult with IntPtr would put resultPtr in x0 instead of x8.
                    AddParameter("IntPtr", _env.SyntheticLocals.ResultPtr);
                    // Decomposed Optional getter: add hasValuePtr after resultPtr.
                    // The Swift wrapper writes the inner payload to resultPtr and the hasValue flag to hasValuePtr.
                    if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                        !_env.MethodDecl.IsSubscriptAccessor &&
                        returnType.SwiftTypeSpec != null && OptionalMarshalClassifier.IsDecomposed(returnType.SwiftTypeSpec, _env.TypeDatabase))
                    {
                        AddParameter("IntPtr", _env.SyntheticLocals.HasValuePtr);
                    }
                }
                else if (MarshallingHelpers.IsMultiElementGenericTupleIndirectReturn(_env)
                    && returnType.SwiftTypeSpec is TupleTypeSpec tupleSpec)
                {
                    // Multi-element generic-element tuples are address-only in Swift's ABI.
                    // Each element is returned via its own @out register (x0, x1, …) instead of
                    // a single x8 SwiftIndirectResult. Emit one IntPtr per element so the call
                    // site allocates and passes N separate buffer pointers in the right registers.
                    for (int i = 0; i < tupleSpec.Elements.Count; i++)
                    {
                        AddParameter("IntPtr", $"tupleResult{i}Ptr");
                    }
                }
                else
                {
                    AddParameter("SwiftIndirectResult", _env.SyntheticLocals.SwiftIndirectResult);
                }
                SetReturnType("void");
                return;
            }

            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec!);

            // ObjC bridged types return IntPtr in P/Invoke, then wrapped with GetNSObject<T>
            if (MarshallingHelpers.IsObjCBridged(returnTypeRecord))
            {
                SetReturnType("IntPtr");
                return;
            }

            // ObjC-bridgeable value types (URL) return IntPtr (ObjC class pointer via bridge)
            if (MarshallingHelpers.IsObjCBridgeable(returnTypeRecord))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Swift classes return pointers directly in registers (not via indirect result)
            // Since classes don't have a Buffer struct, return IntPtr and create the object from it
            if (returnTypeRecord.Kind == TypeRecordKind.Class)
            {
                SetReturnType("IntPtr");
                return;
            }

            if (_env.MethodDecl.IsAsync && !MarshallingHelpers.IsTypeFrozen(returnTypeRecord))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Simple enums: return the underlying integer type, cast back in the wrapper.
            // Note: non-frozen simple enums technically use indirect return under resilient ABI
            // when called via direct CallConvSwift (no wrapper/thunk). In practice, the thunk
            // and @_cdecl wrapper gates ensure non-frozen enum accessors always get a wrapper,
            // so this direct marshalling is safe. If a future SB0001 fallback path does hit
            // a non-frozen enum, the thunk/wrapper gates should be extended to cover that case.
            if (returnTypeRecord.Kind == TypeRecordKind.Enum && returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                SetReturnType(EnumHandler.GetCSharpEnumUnderlyingType(returnTypeRecord.RawValueTypeName));
                return;
            }

            // Complex enums (non-simple) have SafeHandle-based payloads — non-blittable in P/Invoke
            if (returnTypeRecord.Kind == TypeRecordKind.Enum)
            {
                SetReturnType("IntPtr");
                return;
            }

            if (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord))
                SetReturnType(returnTypeRecord.CSharpTypeName.FullyQualifiedName + ".Buffer");
            else
                SetReturnType(returnTypeRecord.CSharpTypeName.FullyQualifiedName);
        }

        /// <summary>
        /// Handles the Swift async arguments of the method.
        /// </summary>
        public void HandleSwiftAsync()
        {
            if (_env.MethodDecl.IsAsync)
            {
                // Our Swift wrapper expects: callback, errorCallback, task (handle), then method arguments
                // No context parameter needed - we handle the callback in Swift
                // For generic parent types, callbacks are hoisted to the PInvokeHelper class.
                // CallExpression provides the qualified reference for the P/Invoke call site.
                var callbackName = NameProvider.GetAsyncCallbackFieldName(_env.EmissionSymbol, _env.MethodDecl);
                var errorCallbackName = NameProvider.GetAsyncErrorCallbackFieldName(_env.EmissionSymbol, _env.MethodDecl);
                string? callbackCallExpr = null;
                string? errorCallbackCallExpr = null;
                if (_env.PInvokeHelperContext != null)
                {
                    var helperClass = _env.PInvokeHelperContext.HelperClassName;
                    callbackCallExpr = $"{helperClass}.{callbackName}";
                    errorCallbackCallExpr = $"{helperClass}.{errorCallbackName}";
                }
                _parameters.Add(new Parameter(MarshalledType.AsyncCallback, callbackName, CallExpression: callbackCallExpr));
                _parameters.Add(new Parameter(MarshalledType.AsyncErrorCallback, errorCallbackName, CallExpression: errorCallbackCallExpr));
                AddParameter(MarshalledType.AsyncTask, "handle");
                // Monotonic cancellation-registry key, distinct from the recyclable GCHandle
                // context above. The wrapper body defines the matching
                // `long _sbwCancelKey = SwiftAsyncCancellation.NextCancelKey();` local before
                // the call; the Swift @_cdecl wrapper registers tasks under this key, not the
                // GCHandle cookie, so a completed task's deferred unregister cannot evict a
                // newer task that reused the cookie.
                AddParameter(MarshalledType.AsyncCancelKey, "_sbwCancelKey");
            }
        }

        /// <summary>
        /// Handles the arguments of the method.
        /// Uses GetCSharpParameterName() so P/Invoke call expressions match wrapper body variable names.
        /// </summary>
        public void HandleArguments()
        {
            var closureParamCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);

            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                // Strip Swift compiler-injected debug params (#file, #line, #column, #function)
                if (DefaultParameterOverloadEmitter.IsDebugParameter(argument))
                    continue;

                // Skip empty tuple () parameters — Swift's Void type is zero-sized, no ABI-level value.
                if (argument.SwiftTypeSpec.IsEmptyTuple)
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argument);

                // Decomposed Optional setter: pass raw inner payload pointer + hasValue bool separately.
                // Must come before IsBoundGeneric (Optional<T> IS a bound generic) to intercept.
                // Uses bool with [MarshalAs(UnmanagedType.U1)] for correct byte-level marshalling.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    !_env.MethodDecl.IsSubscriptAccessor &&
                    OptionalMarshalClassifier.IsDecomposed(argument.SwiftTypeSpec, _env.TypeDatabase))
                {
                    AddParameter("IntPtr", "payload");
                    AddParameter("bool", "hasValue");
                    continue;
                }

                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    // SIMD bound-generic alias (Swift.SIMD2/3/4<Float> → simd.simd_floatN →
                    // System.Numerics.Vector{2,3,4}): the alias resolution at this point would
                    // collapse the bound generic to its managed type and pass it by-value. That
                    // looks fine for one float but mismatches the Swift @_cdecl ABI on every
                    // wider lane — Swift expects the full simd_floatN in a single NEON vector
                    // register, .NET passes Vector3/Vector4 as an HFA across s0,s1,s2,…, and
                    // only lane 0 lines up. Route SIMD bound-generics through CdeclFrozenStruct
                    // (stackalloc + IntPtr) so the bytes cross intact, matching the Swift wrapper
                    // shape produced by CdeclParamMapper.Map for the same input.
                    if (_env.MethodDecl.UsesCdeclWrapper &&
                        argument.SwiftTypeSpec is NamedTypeSpec simdBgSpec &&
                        CdeclParamMapper.IsSimdVectorType(simdBgSpec))
                    {
                        var simdCsType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                        AddParameter(new MarshalledType.CdeclFrozenStruct(simdCsType), csName);
                        continue;
                    }

                    var (csTypeParam, csTypeName) = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(argument) switch
                    {
                        true => (_env.BoundGenericsHandler.GetBufferType(argument), NameProvider.GetBoundGenericBufferName(csName)),
                        false => (_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext), csName)
                    };

                    AddParameter(csTypeParam, csTypeName);
                    continue;
                }

                // Handle closure arguments (including optional closures)
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        // Baseline async closures (throwing and non-throwing) use the
                        // start-thunk bridge: the P/Invoke passes (context, startFunc)
                        // and the Swift wrapper renders a matching adapter inside Task {}.
                        // The non-throwing path keeps the uniform startFunc ABI (trailing
                        // errorFP slot stays — the non-throwing adapter passes a sentinel
                        // pointer for it). The outer method still must be async +
                        // @_cdecl-wrapped; Throws is only required for the throwing baseline.
                        if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                        {
                            bool asyncBridgeEligible =
                                _env.MethodDecl.UsesCdeclMethodWrapper &&
                                _env.MethodDecl.IsAsync &&
                                _env.MethodDecl.Throws &&
                                _env.ClosureHandler.IsBaselineAsyncThrowingClosure(closureTypeSpec);
                            if (asyncBridgeEligible)
                            {
                                var callbackName = ClosureHandler.GetCallbackFunctionName(
                                    _env.MethodDecl.Name, argument.Name, _env.EmissionSymbol);
                                var funcPtrType = _env.ClosureHandler.GetAsyncThrowingStartFunctionPointerType(closureTypeSpec);
                                AddParameter(new MarshalledType.AsyncThrowingContext(csName), csName + "Context");
                                AddParameter(new MarshalledType.AsyncThrowingStartFunc(callbackName, funcPtrType), csName + "StartFunc");
                            }
                            else
                            {
                                AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                            }
                        }
                        else if (_env.ClosureHandler.IsAsyncClosure(closureTypeSpec)
                                 && _env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(closureTypeSpec))
                        {
                            bool asyncBridgeEligible =
                                _env.MethodDecl.UsesCdeclMethodWrapper &&
                                _env.MethodDecl.IsAsync;
                            if (asyncBridgeEligible)
                            {
                                var callbackName = ClosureHandler.GetCallbackFunctionName(
                                    _env.MethodDecl.Name, argument.Name, _env.EmissionSymbol);
                                var funcPtrType = _env.ClosureHandler.GetAsyncThrowingStartFunctionPointerType(closureTypeSpec);
                                AddParameter(new MarshalledType.AsyncThrowingContext(csName), csName + "Context");
                                AddParameter(new MarshalledType.AsyncThrowingStartFunc(callbackName, funcPtrType), csName + "StartFunc");
                            }
                            else
                            {
                                AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                            }
                        }
                        else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, closureParamCount))
                        {
                            if (_env.MethodDecl.HasCdeclClosureMarshalling)
                            {
                                // Cdecl closure wrapper (standalone or @_cdecl inline): pass func ptr + context as separate IntPtr params
                                var callbackName = ClosureHandler.GetCallbackFunctionName(
                                    _env.MethodDecl.Name, argument.Name, _env.EmissionSymbol);
                                AddParameter(new MarshalledType.CdeclClosureFuncPtr(callbackName, csName), csName + "FuncPtr");
                                AddParameter(new MarshalledType.CdeclClosureContext(csName), csName + "Context");
                            }
                            else
                            {
                                // Legacy path: pass as SwiftClosureData (for async methods with non-async closures)
                                AddParameter(MarshalledType.SwiftClosureLegacy, csName);
                            }
                        }
                        else
                        {
                            // @convention(c) closures just need the function pointer
                            var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
                            AddParameter(new MarshalledType.ConventionCFuncPtr(funcPtrType), csName);
                        }
                    }
                    else
                    {
                        // Unsupported closure - use placeholder
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                    }
                    continue;
                }

                // Handle tuple arguments
                if (_env.TupleHandler.IsTuple(argument))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(argument)!;
                    // @_cdecl wrappers receive tuples as UnsafeRawPointer (IntPtr).
                    // ValueTuple has StructLayout.Auto which is incompatible with P/Invoke marshalling.
                    // The marshalling code creates a buffer with tuple elements at ABI offsets using
                    // Unsafe.Write — safe for blittable-primitive elements (written by value) and pure
                    // Swift class elements (written as their object handle into the single pointer-width
                    // slot). Other element kinds (existentials, bound generics, non-frozen/frozen-mem
                    // structs, simple enums) need per-element marshalling that doesn't exist yet, so
                    // IsCdeclBufferMarshallableTuple excludes them and they fall through / fail closed.
                    if (_env.MethodDecl.UsesCdeclWrapper && _env.TupleHandler.IsCdeclBufferMarshallableTuple(tupleTypeSpec))
                    {
                        var csTupleType = _env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, _genericContext);
                        AddParameter(new MarshalledType.CdeclTuple(csTupleType), csName);
                    }
                    else if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                        _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                        AddParameter(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec, _genericContext), csName);
                    else
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                    continue;
                }

                // Handle existential arguments (any Protocol) - pass container by value
                // Uses Existential:{containerType}:{publicType} prefix so that:
                // - PInvokeParametersString() emits the container type for DllImport declarations
                // - CallArgumentsString() generates the ISwiftExistentialConvertible conversion
                // @_cdecl wrappers use CdeclExistential (ref container) instead of Existential (by-value)
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var containerType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                        var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                        if (_env.MethodDecl.UsesCdeclWrapper)
                            AddParameter(new MarshalledType.CdeclExistential(containerType, publicType), csName);
                        else
                        {
                            // Resolve the proxy class name so MethodSignature can emit the wrap
                            // fallback, letting users pass plain C# implementations of the interface
                            // without manually constructing the {Protocol}Proxy. Skip stdlib/external
                            // protocols that project to "object" or lack TypeRecords — no proxy class
                            // is emitted for them, so the wrap fallback would not compile.
                            string? proxyClassName = null;
                            if (containerType == "Swift.Runtime.ExistentialContainer1" &&
                                publicType != "object" &&
                                !_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out _) &&
                                _env.ExistentialHandler.AllProtocolsHaveTypeRecords(protocolList) &&
                                _env.ExistentialHandler.TryGetFilteredProxyClassName(protocolList, out var filteredProxy))
                            {
                                var qualifiedProxy = _env.ExistentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
                                // CONSUME gate: a suppressed proxy (EveryProtocol conformance not emitted)
                                // leaves proxyClassName null so MethodSignature drops the wrap fallback,
                                // byte-identical to the retired CoGater wrap-fallback downgrade. Mirrors
                                // WrapperEmitter.Marshalling's gate for the wrapper-library param path.
                                if (!_env.ExistentialHandler.IsProxyNameSuppressed(filteredProxy, qualifiedProxy, _env.EmissionContext))
                                    proxyClassName = qualifiedProxy;
                            }
                            AddParameter(
                                new MarshalledType.Existential(containerType, publicType) { ProxyClassName = proxyClassName },
                                csName);
                        }
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                    }
                    continue;
                }

                // Handle Optional-wrapped existential arguments like (any DataCaching)?
                // These use buffer-based marshalling (same as regular optionals) since the wrapper body
                // creates SwiftOptional<Container> and passes the buffer to P/Invoke.
                if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        AddParameter("IntPtr", NameProvider.GetBoundGenericBufferName(csName));
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csName);
                    }
                    continue;
                }

                // Handle native type remapping (URL → NSUrl, Data → NSData in public API)
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
                {
                    TypeRecord nativeRemapTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argument.SwiftTypeSpec);
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(argument.SwiftTypeSpec);
                    if (nativeRemapTypeRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable))
                    {
                        // ObjC-bridgeable (URL): use ObjCBridged (IntPtr) instead of SafeHandle
                        AddParameter(new MarshalledType.ObjCBridged(nativeRemapTypeRecord.NativeTypeName!.FullyQualifiedName), csName);
                    }
                    else if (!MarshallingHelpers.IsTypeFrozen(nativeRemapTypeRecord))
                    {
                        // Non-frozen (non-bridgeable): use NativeRemappedNonFrozen marker
                        AddParameter(MarshalledType.NativeRemappedNonFrozen, csName);
                    }
                    else if (MarshallingHelpers.ShouldDecomposeDataForCdecl(_env.MethodDecl, argument.SwiftTypeSpec))
                    {
                        // Foundation.Data ABI decomposition: @_cdecl constructor/method wrappers receive
                        // Data as two Int words (_dW0_/_dW1_), matching the 16-byte struct layout. Passing
                        // the whole struct by value misplaces the second word on AArch64 when the composite
                        // lands after 7 leading int args (split between the last GP register and the stack).
                        // Mirror the SwiftString two-word path; the C# wrapper extracts the words in
                        // WrapperEmitter.Marshalling.TryEmitParameterConversionViaProjection.
                        AddParameter("nint", csName + "_w0");
                        AddParameter("nint", csName + "_w1");
                    }
                    else
                    {
                        // Frozen (Data) on paths that don't decompose: use NativeRemappedFrozen type.
                        // C# passes the Swift.Foundation.Data struct (16 bytes) via CallConvCdecl.
                        AddParameter(new MarshalledType.NativeRemappedFrozen(swiftWrapperType!), csName);
                    }
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                // @_cdecl property/subscript wrapper: String params via UTF-8 pointer + length.
                // Swift @_cdecl receives UnsafePointer<UInt8> + Int, reconstructs String from UTF-8.
                // Constructor/method wrappers use SwiftString.Buffer (two-word) path instead.
                // A carved-out scalar LocalizedStringResource setter takes the same UTF-8 bytes; the
                // Swift wrapper rebuilds the resource via LocalizedStringResource(stringLiteral:).
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    argument.SwiftTypeSpec is NamedTypeSpec argStrNamed &&
                    (argStrNamed.Name == "Swift.String" || MarshallingHelpers.IsLocalizedStringResource(argStrNamed)))
                {
                    AddParameter("IntPtr", csName + "Utf8Ptr");
                    AddParameter("nint", csName + "Utf8Len");
                    continue;
                }

                // SwiftString ABI decomposition: @_cdecl constructor/method wrappers receive String
                // as two Int words. Decompose SwiftString.Buffer into two nint fields to match the
                // Swift @_cdecl parameter layout, avoiding ARM64 AAPCS64 struct-to-register ambiguity
                // when 4+ strings fill x0-x7. Non-@_cdecl paths retain FrozenBuffer (struct) passing.
                // Runs BEFORE the TypeRecord-based branches: a carved-out LocalizedStringResource has
                // an auto-bridged, NON-frozen TypeRecord, so the ObjCBridged / non-frozen-SafeHandle
                // branches below would otherwise intercept it before this two-word path (Swift.String's
                // own TypeRecord is frozen, so it would reach here either way).
                if (MarshallingHelpers.ShouldDecomposeStringForCdecl(_env.MethodDecl, argument.SwiftTypeSpec))
                {
                    AddParameter("nint", csName + "_w0");
                    AddParameter("nint", csName + "_w1");
                    continue;
                }

                // Swift.UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer split: the public C#
                // (ReadOnly)Span<byte> is pinned at the call site via a fixed block; the @_cdecl
                // wrapper receives (UnsafeRawPointer?/UnsafeMutableRawPointer?, Int). The C ABI
                // is identical for both variants, so the same MarshalledType records apply.
                // See CdeclParamMapper.Map for the Swift-side reconstruction and WrapperEmitter
                // for the C# fixed-block emission that surrounds this P/Invoke call.
                if (MarshallingHelpers.IsAnyUnsafeRawBufferPointer(argument.SwiftTypeSpec))
                {
                    AddParameter(new MarshalledType.RawBufferPtr(csName), csName + "Ptr");
                    AddParameter(new MarshalledType.RawBufferLen(csName), csName + "Len");
                    continue;
                }

                if (argument.IsGeneric)
                {
                    var payloadName = NameProvider.GetPayloadName(csName);
                    AddParameter("IntPtr", payloadName, inoutModifier);
                    continue;
                }

                // Foundation.Date: Swift ABI passes Date as Double (8 bytes in FP register).
                // C#'s DateTimeOffset is 12 bytes — ABI mismatch on NativeAOT. Use double.
                // Uses NativeRemappedFrozen marker so GetCallArgumentString returns {name}Swift
                // (matching DateProjection.GetParameterPlan's PInvokeExpression).
                // Skip for accessors: the accessor signature uses raw `double` (per
                // ShouldSkipProjectionForAccessor) and EmitTypeConversions skips projection
                // marshalling for accessors, so `{name}Swift` would never be declared. The
                // property setter body converts DateTimeOffset → double before calling the
                // accessor, so the accessor can pass `value` through directly.
                if (!_env.MethodDecl.IsAccessor &&
                    argument.SwiftTypeSpec is NamedTypeSpec dateArgSpec && dateArgSpec.Name == "Foundation.Date")
                {
                    AddParameter(new MarshalledType.NativeRemappedFrozen("double"), csName, inoutModifier);
                    continue;
                }

                TypeRecord argumentTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argument.SwiftTypeSpec);

                // ObjC bridged/rooted/bridgeable types use IntPtr in P/Invoke, Handle extracted from the .NET iOS binding.
                // ObjC-rooted classes (same-module Swift classes inheriting NSObject) use the same
                // marshalling as ObjC-bridged types — .Handle instead of .Payload.
                if (MarshallingHelpers.IsObjCBridged(argumentTypeRecord) ||
                    MarshallingHelpers.IsObjCRooted(argumentTypeRecord) ||
                    MarshallingHelpers.IsObjCBridgeable(argumentTypeRecord))
                {
                    // Store the original C# type name for use in wrapper generation
                    AddParameter(new MarshalledType.ObjCBridged(argumentTypeRecord.CSharpTypeName.FullyQualifiedName), csName);
                    continue;
                }

                // Enum values: simple enums (C# value types) use their underlying int type,
                // complex enums use SafeHandle payload pointer.
                // Note: non-frozen simple enums technically use indirect passing under resilient ABI
                // when called via direct CallConvSwift. The thunk and @_cdecl wrapper gates ensure
                // non-frozen enum params always get a wrapper, so direct marshalling is safe here.
                if (argumentTypeRecord.Kind == TypeRecordKind.Enum)
                {
                    if (argumentTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    {
                        // Simple enums are C# enum value types — pass as underlying int
                        var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(argumentTypeRecord.RawValueTypeName);
                        AddParameter(new MarshalledType.SimpleEnum(underlyingType, argumentTypeRecord.CSharpTypeName.FullyQualifiedName), csName);
                    }
                    else if (_env.MethodDecl.IsAsync)
                        AddParameter(MarshalledType.NonFrozenIntPtr, csName);
                    else
                        AddParameter(MarshalledType.EnumSafeHandle, csName);
                    continue;
                }

                if (!MarshallingHelpers.IsTypeFrozen(argumentTypeRecord))
                {
                    // For async methods, SafeHandle cannot be used with Swift calling convention.
                    // Use IntPtr and manage lifetime manually via DangerousAddRef/DangerousRelease.
                    if (_env.MethodDecl.IsAsync)
                        AddParameter(MarshalledType.NonFrozenIntPtr, csName);
                    else
                        AddParameter(MarshalledType.NonFrozenSafeHandle, csName);
                    continue;
                }

                // @_cdecl frozen struct params: pass as IntPtr (pointer to marshalled buffer).
                // Custom frozen structs are not C-representable in @_cdecl — Swift wrapper
                // receives UnsafeRawPointer and reconstructs via .load(as: T.self).
                if (_env.MethodDecl.UsesCdeclWrapper &&
                    WrapperValidation.IsNonPrimitiveFrozenStructParam(argument, _env.TypeDatabase))
                {
                    AddParameter(new MarshalledType.CdeclFrozenStruct(
                        argumentTypeRecord.CSharpTypeName.FullyQualifiedName), csName);
                    continue;
                }

                if (MarshallingHelpers.RequiresMemoryManagement(argumentTypeRecord))
                    AddParameter(new MarshalledType.FrozenBuffer(argumentTypeRecord.CSharpTypeName.FullyQualifiedName), csName, inoutModifier);
                else
                    AddParameter(argumentTypeRecord.CSharpTypeName.FullyQualifiedName, csName, inoutModifier);
            }

            // Large Optional returns use out-buffer pattern — add result buffer parameter.
            // Skip when @_cdecl IndirectResult already handles the Optional via resultPtr.
            if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary) &&
                !MarshallingHelpers.MethodRequiresIndirectResult(_env))
            {
                AddParameter("IntPtr", "_optRetPtr");
            }
        }

        /// <summary>
        /// Handles the metadata of generic arguments.
        /// For protocol extension methods on generic types, emits TWO TypeMetadata per
        /// generic parameter: one for the explicit T.Type param in the @_silgen_name wrapper,
        /// and one for the implicit trailing metadata added by Swift's calling convention.
        /// </summary>
        public void HandleGenericMetadata()
        {
            // Closed static factory @_cdecl wrapper takes only resultPtr — no metadata threading.
            // The Swift wrapper hard-codes the closed T at the source-level call so the Swift
            // compiler resolves all parent metadata at compile time. See ClosedStaticFactoryGate.
            if (_env.MethodDecl.IsAccessor &&
                _env.MethodDecl.UsesCdeclPropertyWrapper &&
                ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(_env.MethodDecl))
                return;

            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var metadataName = NameProvider.GetMetadataName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter);
                AddParameter("IntPtr", metadataName);
            }

            // Generic @_silgen_name wrappers require explicit T.Type params (Swift 6 mandate).
            // Swift also adds implicit trailing TypeMetadata after all explicit params.
            // Both values are identical — emit a second IntPtr per generic param.
            if (_env.MethodDecl.IsProtocolExtensionMethod && _env.MethodDecl.GenericParameters.Count > 0)
            {
                foreach (var genericParameter in _env.MethodDecl.GenericParameters)
                {
                    var metadataName = NameProvider.GetMetadataName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter);
                    AddParameter("IntPtr", $"{metadataName}Implicit");
                }
            }
        }

        /// <summary>
        /// Handles the protocol conformances of the generic parameters of the method.
        /// </summary>
        public void HandleProtocolConformance()
        {
            // Closed static factory @_cdecl wrapper takes only resultPtr — no PWT threading.
            // See ClosedStaticFactoryGate.
            if (_env.MethodDecl.IsAccessor &&
                _env.MethodDecl.UsesCdeclPropertyWrapper &&
                ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(_env.MethodDecl))
                return;

            // GSF cdecl-constructor admits PAT/Self-requirement conformances with a captured
            // descriptor symbol; their PWT slot is materialized at the call site via
            // {HelperClass}.Get{Proto}PWT(metadata). The corresponding @_cdecl wrapper
            // declares one UnsafeRawPointer _pwtN slot per such conformance (see
            // MetatypeHelperEmitter.GetTotalPwtParameterCount). Other paths (method,
            // property, subscript) still use the strict gate because their C# call site
            // does not yet thread dynamic PWTs.
            bool admitDynamicPwt = UsesCdeclConstructorOnGenericParent(_env);

            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                // Ordinal to match the PWT slot order emitted by PInvokeHelperEmitter
                // (which sorts the same conformances with StringComparer.Ordinal). A
                // culture-sensitive sort here would assign PInvoke parameter slots in a
                // different order than the witness-table accessor under some cultures,
                // passing the wrong PWT for a conformance.
                var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName, StringComparer.Ordinal);
                foreach (var conformance in conformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used here)
                    // This must match the check in EmitProtocolWitnessTables to avoid generating
                    // PInvoke signatures with parameters that have no corresponding variables.
                    bool admit = IsProtocolAvailableForConstraint(conformance.ConformanceTarget, _env.TypeDatabase);
                    if (!admit && admitDynamicPwt && HasResolvableDynamicDescriptor(conformance.ConformanceTarget, _env.TypeDatabase))
                        admit = true;
                    if (!admit)
                        continue;

                    var pwtName = NameProvider.GetProtocolWitnessTableName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter, conformance.ConformanceTarget.Name);
                    // Use IntPtr instead of ProtocolWitnessTable struct to avoid Mono CallConvSwift
                    // JIT crash (jit-info.c:918). The ProtocolWitnessTable struct wraps a single IntPtr
                    // and is ABI-compatible, but Mono's JIT can't handle struct params in CallConvSwift.
                    AddParameter("IntPtr", pwtName);
                }
            }
        }

        /// <summary>
        /// True when the surrounding method is a @_cdecl-wrapped constructor on a generic
        /// parent type — the GSF cdecl-ctor path that threads dynamic PWTs through the
        /// {HelperClass}.Get{Proto}PWT(metadata) runtime helper.
        /// </summary>
        internal static bool UsesCdeclConstructorOnGenericParent(MethodEnvironment env) =>
            env.MethodDecl.UsesCdeclWrapper &&
            env.MethodDecl.IsConstructor &&
            env.ParentDecl is TypeDecl { IsGeneric: true };

        /// <summary>
        /// True when the protocol is a PAT / Self-requirement protocol whose
        /// protocol-descriptor symbol the parser captured. Such conformances are
        /// resolvable at runtime via the dynamic-PWT path
        /// (<c>SwiftConformance.GetWitnessTableOrThrow</c>) — the C# call site materializes
        /// the witness table by calling <c>{HelperClass}.Get{Proto}PWT(metadata)</c> and
        /// passing <c>.Handle</c> to the @_cdecl wrapper. Mirrors the descriptor-only
        /// branch of <see cref="PInvokeHelperContext.CreateIfGeneric"/>.
        /// </summary>
        internal static bool HasResolvableDynamicDescriptor(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
        {
            if (!typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
                return false;
            if (record.Kind != TypeRecordKind.Protocol)
                return false;
            bool unresolvable =
                record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
            if (!unresolvable)
                return false;
            return !string.IsNullOrEmpty(record.ProtocolDescriptorSymbol);
        }

        /// <summary>
        /// Delegates to <see cref="MethodValidationGates.IsProtocolAvailableForConstraint(SwiftTypeName, ITypeDatabase)"/>
        /// so the constraint-emission filter has a single source of truth across
        /// <c>WrapperEmitter</c>, <c>PInvokeEmitter</c>, and <c>BoundGenericsHandler</c>.
        /// </summary>
        private static bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
            => MethodValidationGates.IsProtocolAvailableForConstraint(protocolTypeName, typeDatabase);

        /// <summary>
        /// Handles the SwiftSelf parameter of the method.
        /// </summary>
        public void HandleSwiftSelf()
        {
            // Standalone closure Cdecl wrapper, @_cdecl method wrapper, and native thunks use free-function style.
            // Pass self as explicit IntPtr (same as async pattern).
            // Under CallConvCdecl, SwiftSelf would be placed in a regular register instead of x20,
            // which doesn't match what the thunk/wrapper expects. Use plain IntPtr instead.
            // Wrapper generator paths (ArraySlice, DefaultParam) keep extension methods
            // with implicit self via SwiftSelf — they set HasClosureCdeclWrapper but NOT UsesFreeFunctionWrapper.
            if ((_env.MethodDecl.UsesFreeFunctionWrapper || _env.MethodDecl.UsesCdeclMethodWrapper || _env.MethodDecl.UsesNativeThunk) && MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                if (_env.ParentDecl is ClassDecl classParentFree && classParentFree.IsObjCRooted)
                {
                    AddParameter("IntPtr", "_selfClassObjC");
                }
                else if (_env.ParentDecl is ClassDecl)
                {
                    AddParameter("IntPtr", "_selfClass");
                }
                else if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    // Frozen struct value types have no _payload SafeHandle.
                    // Use _selfFixed → resolved via fixed block to pin 'this'.
                    // Frozen structs with memory management (ClassWithBufferStruct) have _payload.
                    if (!MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                        AddParameter("IntPtr", "_selfFixed");
                    else
                        AddParameter("IntPtr", "_self");
                }
                else
                {
                    // Non-frozen structs (ClassWithOpaquePayload) have _payload
                    AddParameter("IntPtr", "_self");
                }
                return;
            }

            // Async instance methods pass self as explicit IntPtr parameter.
            // We use a module-level free function (not extension method) to avoid SwiftSelf binding issues.
            // Always pass self — even for singleton classes — so the wrapper operates on the
            // correct instance (callers may use non-shared instances).
            if (_env.MethodDecl.IsAsync && MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                // Use different parameter names to distinguish at call site:
                // - _selfClassObjC: ObjC-rooted class (uses Handle directly, no buffer dereference)
                // - _selfClass: class instance (needs dereferencing - payload contains pointer to class)
                // - _self: struct instance (no dereference - payload IS the data)
                string selfName;
                if (_env.ParentDecl is ClassDecl asyncClassParent && asyncClassParent.IsObjCRooted)
                    selfName = "_selfClassObjC";
                else if (_env.ParentDecl is ClassDecl)
                    selfName = "_selfClass";
                else
                    selfName = "_self";
                AddParameter("IntPtr", selfName);
                return;
            }

            if (MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
                {
                    // Setters always need pointer semantics (even for frozen structs)
                    // because they modify the struct in-place
                    if (MarshallingHelpers.MethodIsSetter(_env.MethodDecl))
                    {
                        AddParameter(MarshalledType.SwiftSelfUntyped, "self");
                    }
                    else
                    {
                        // Getters can use value semantics for frozen structs
                        // Use resolved type name (may be renamed for nested type collision avoidance)
                        var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                        var resolvedName = GetResolvedParentTypeName();
                        if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                            AddParameter(new MarshalledType.SwiftSelfTyped($"{resolvedName}.Buffer"), "self");
                        else
                            AddParameter(new MarshalledType.SwiftSelfTyped(resolvedName), "self");
                    }
                }
                else
                {
                    AddParameter(MarshalledType.SwiftSelfUntyped, "self");
                }
            }
        }

        /// <summary>
        /// Handles the SwiftError parameter of the method.
        /// </summary>
        public void HandleSwiftError()
        {
            // Async methods call our generated Swift wrapper which handles errors internally
            if (_env.MethodDecl.IsAsync)
                return;

            if (_env.MethodDecl.Throws)
            {
                if (_env.MethodDecl.UsesCdeclWrapper || _env.MethodDecl.UsesNativeThunk)
                {
                    // @_cdecl wrapper / native thunk: error reported via out-pointer, not SwiftError register.
                    // Under CallConvCdecl, SwiftError would be placed in a regular register instead of x21,
                    // which doesn't match what the thunk/wrapper expects. Use plain IntPtr instead.
                    // 'out IntPtr' marshals as a pointer parameter — callee writes through it.
                    AddParameter("IntPtr", "errorPtr", "out");
                }
                else
                {
                    // Use 'ref' (not 'out') to match Swift ABI: caller must zero-initialize
                    // the swifterror register before calling a throwing function. 'ref' ensures
                    // the initial default(SwiftError) value is passed to the callee.
                    AddParameter("SwiftError", "swiftError", "ref");
                }
            }
        }

        /// <summary>
        /// Adds context parameter to a function pointer type string for escaping closures.
        /// Transforms "delegate* unmanaged[Cdecl]&lt;int, void&gt;" to "delegate* unmanaged[Cdecl]&lt;int, IntPtr, void&gt;"
        /// </summary>
        private static string AddContextToFunctionPointerType(string funcPtrType)
        {
            int lastAngle = funcPtrType.LastIndexOf('>');
            if (lastAngle == -1)
                return funcPtrType;

            // Use nesting-aware search to skip commas inside generic type arguments
            int lastComma = EmitterUtility.FindLastTopLevelComma(funcPtrType, lastAngle);
            if (lastComma == -1)
            {
                // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
                int openAngle = funcPtrType.IndexOf('<');
                if (openAngle == -1)
                    return funcPtrType;

                return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
            }

            return funcPtrType.Insert(lastComma + 1, " IntPtr,");
        }

        /// <summary>
        /// Gets the resolved simple type name for the parent type, accounting for nested type renames.
        /// </summary>
        private string GetResolvedParentTypeName()
        {
            if (_env.ParentDecl is TypeDecl typeDecl &&
                _env.TypeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
            {
                var name = record.CSharpTypeName.Name;
                var lastDot = name.LastIndexOf('.');
                return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
            }
            return _env.ParentDecl.Name;
        }
    }

    /// <summary>
    /// Provides methods for emitting PInvoke signatures.
    /// </summary>
    internal static class PInvokeEmitter
    {
        /// <summary>
        /// Computes the P/Invoke entry point symbol and whether the method needs the wrapper library.
        /// Used by both EmitPInvoke (for emission) and MethodHandler (for symbol cross-referencing).
        /// </summary>
        /// <param name="methodDecl">The method declaration.</param>
        /// <returns>A tuple of (entryPoint symbol, needsWrapperLib flag).</returns>
        internal static (string entryPoint, bool needsWrapperLib) ComputeEntryPoint(MethodDecl methodDecl)
        {
            var needsWrapperLib = NeedsWrapperLib(methodDecl);

            // Native thunks and @_cdecl wrappers: entry point is the thunk/wrapper symbol
            // (already set in MangledName by MethodHandler/PropertyHandler/SubscriptHandler).
            // The wrapper library hosts both thunk .o files and @_cdecl Swift functions.
            if (needsWrapperLib)
            {
                return (NameProvider.GetMangledName(methodDecl), needsWrapperLib);
            }

            // Direct Swift call: use SwiftCallTargetResolver for Tj dispatch thunk logic.
            var entryPoint = SwiftCallTargetResolver.Resolve(methodDecl, methodDecl.ParentDecl);

            return (entryPoint, needsWrapperLib);
        }

        /// <summary>
        /// AF13 (Finding 13): environment-scoped entry-point resolution. The wrapper/thunk entry
        /// point is reconstructed from the emission-scoped promoted symbol
        /// (<see cref="MethodEnvironment.EmissionSymbol"/>) — not a mutated decl field — plus the
        /// wrapper-kind suffix the decl's flags select. The direct-Swift path resolves from the
        /// immutable silgen symbol via <see cref="SwiftCallTargetResolver"/>. <c>needsWrapperLib</c>
        /// (which selects the library path) stays derived from the decl's routing flags.
        /// </summary>
        internal static (string entryPoint, bool needsWrapperLib) ComputeEntryPoint(MethodEnvironment env)
        {
            var methodDecl = env.MethodDecl;
            var needsWrapperLib = NeedsWrapperLib(methodDecl);

            // Native thunks and @_cdecl wrappers: entry point is the promoted thunk/wrapper symbol
            // (the wrapper library hosts both thunk .o files and @_cdecl Swift functions), with any
            // wrapper-kind suffix (_async/_opaque/_optbuf/_cdecl/_XC) reapplied.
            if (needsWrapperLib)
            {
                return (NameProvider.GetMangledName(GetPromotedSymbol(env), methodDecl), needsWrapperLib);
            }

            // Direct Swift call: resolve from the immutable silgen symbol for Tj dispatch thunk logic.
            var entryPoint = SwiftCallTargetResolver.Resolve(methodDecl, methodDecl.ParentDecl);
            return (entryPoint, needsWrapperLib);
        }

        /// <summary>
        /// AF13 (Finding 13): the promoted emission symbol for this method's P/Invoke. Sourced from
        /// the emission-scoped <see cref="MethodEnvironment.EmissionSymbol"/> side table — the value
        /// <see cref="MethodEnvironment.PromoteSymbol"/> records when a wrapper/thunk strategy promotes
        /// the symbol, defaulting to the decl's immutable silgen symbol when nothing promotes it. The
        /// decl's <see cref="MethodDecl.MangledName"/> is no longer mutated during emission.
        /// </summary>
        private static string GetPromotedSymbol(MethodEnvironment env) => env.EmissionSymbol;

        /// <summary>
        /// Whether this method's P/Invoke must bind into the wrapper library (async/opaque-return
        /// or an explicit wrapper-library routing flag) rather than the module library.
        /// </summary>
        private static bool NeedsWrapperLib(MethodDecl methodDecl)
        {
            var hasOpaqueReturn = methodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };
            return methodDecl.IsAsync || hasOpaqueReturn || methodDecl.UsesWrapperLibrary;
        }

        /// <summary>
        /// Emits the PInvoke signature or collects it to a helper context for generic types.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        /// <param name="methodEnv">The method environment.</param>
        /// <param name="signatureHandler">The signature handler.</param>
        public static void EmitPInvoke(CSharpWriter csWriter, MethodEnvironment methodEnv, SignatureHandler signatureHandler)
        {
            var methodDecl = (MethodDecl)methodEnv.MethodDecl;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pInvokeName = NameProvider.GetPInvokeName(GetPromotedSymbol(methodEnv), methodDecl);
            var moduleLibPath = methodEnv.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var (entryPoint, needsWrapperLib) = ComputeEntryPoint(methodEnv);
            var libPath = needsWrapperLib && methodEnv.TypeDatabase.AsyncLibraryName != null
                ? methodEnv.TypeDatabase.AsyncLibraryName
                : moduleLibPath;

            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            // In-band wrapper-symbol contract: this is the single eager check, shared with
            // the constructor's predict-then-skip gate via FindUnregisteredWrapperSymbol so
            // the throw predicate and the predict predicate can never drift. It surfaces a
            // wrapper-targeting P/Invoke that references a symbol wrapper-emit never
            // registered (Cdecl + SBW_… or Swift CC + SBSW_…) as a throw rather than letting
            // an unresolved P/Invoke leak into the generated bindings. The method/bridge
            // sites catch it and roll their C# buffer back to a pre-member checkpoint;
            // because async @_cdecl symbols register inside EmitMethod (after the public
            // body is written) only this post-body throw — not a pre-emit query — can tell a
            // valid async method from a silent bail. The declaredCallConv local below feeds
            // the generic-helper declaration's CallingConvention.
            var declaredCallConv = WrapperValidation.GetCallingConvention(methodDecl);
            if (WrapperSymbolContractGate.FindUnregisteredWrapperSymbol(methodEnv) is { } missingWrapperSymbol)
            {
                throw new WrapperSymbolContractException(missingWrapperSymbol, pInvokeName);
            }

            // If we're inside a generic type, collect the P/Invoke to the helper context
            // instead of emitting it inline (to avoid CS7042: DllImport in generic type)
            if (methodEnv.PInvokeHelperContext != null)
            {
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = entryPoint,
                    MethodName = pInvokeName,
                    ReturnType = pInvokeSignature.ReturnType,
                    ParametersString = pInvokeSignature.PInvokeParametersString(),
                    IsAsync = methodDecl.IsAsync,
                    // Propagate the correct calling convention to the helper class P/Invoke declaration.
                    // @_cdecl wrappers and native thunks use Cdecl; @_silgen_name wrappers use Swift.
                    CallingConvention = declaredCallConv,
                    // Carry the wrapper-symbol contract through to the helper-class P/Invoke so
                    // helper-hoisted declarations get the same gate as direct emissions. The
                    // format-time check in PInvokeEmitHelper.FormatDeclarationLines runs when
                    // the helper class is finally written — by then the full module's wrapper-emit
                    // has finished and the registry is complete. Covers both pairings
                    // (Cdecl+SBW_ and Swift+SBSW_) — FormatDeclarationLines decides which prefix
                    // applies based on the resolved calling convention.
                    EmissionContext = methodEnv.EmissionContext,
                    EnforceWrapperContract = methodEnv.EmissionContext != null,
                    // Methods with GenericParameters already have per-param TypeMetadata in the
                    // P/Invoke signature via HandleGenericMetadata(). Skip PInvokeHelperContext
                    // trailing metadata to avoid duplicate TypeMetadata params (ABI mismatch).
                    // Constructors additionally need ONE IntPtr for the allocating init's
                    // Self.Type metatype (e.g., Wrapper<T>.Type).
                    // @_cdecl constructors: metadata is already included via HandleGenericMetadata()
                    // (maps to _metadata0 in the @_cdecl wrapper). No extra metatype param needed —
                    // the @_cdecl wrapper handles metatype dispatch internally.
                    MetadataParameters = methodDecl.UsesCdeclWrapper || methodDecl.UsesNativeThunk
                        ? Array.Empty<string>()
                        : methodDecl.GenericParameters.Count > 0
                            ? (methodDecl.IsConstructor
                                ? new[] { "IntPtr metatype" }
                                : Array.Empty<string>())
                            : methodEnv.PInvokeHelperContext.GetMetadataParameterDeclarations()
                };
                methodEnv.PInvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                // Emit directly (non-generic type)
                var pInvokeParams = pInvokeSignature.PInvokeParametersString();
                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = libPath,
                    EntryPoint = entryPoint,
                    MethodName = pInvokeName,
                    ReturnType = pInvokeSignature.ReturnType,
                    ParametersString = pInvokeParams,
                    IsAsync = methodDecl.IsAsync,
                    IsUnsafe = pInvokeParams.Contains("void*") || pInvokeParams.Contains("delegate*") || pInvokeParams.Contains("IntPtr*"),
                    CallingConvention = WrapperValidation.GetCallingConvention(methodDecl)
                });
            }
        }
    }
}
