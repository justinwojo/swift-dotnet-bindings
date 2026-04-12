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
                else
                {
                    // Large Optional returns use out-buffer pattern — PInvoke returns void
                    // Guard: only when a Swift wrapper exists to handle the buffer
                    if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                        (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary))
                    {
                        SetReturnType("void");
                        return;
                    }

                    var csTypeParam = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) switch
                    {
                        true => _env.BoundGenericsHandler.GetBufferType(returnType),
                        false => _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnType, _genericContext)
                    };
                    SetReturnType(csTypeParam);
                    return;
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
            // async methods skip indirect result (MarshallingHelpers returns false for async),
            // so generic-element tuples on async methods are unsupported.
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
                    // Async methods don't use indirect result, so generic-element tuples are unsupported
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    return;
                }
                // Generic-element tuples (sync): fall through to indirect result handling
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
                    else
                    {
                        // Non-cdecl: use IntPtr (legacy CallConvSwift path)
                        SetReturnType("IntPtr");
                        return;
                    }
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    return;
                }
                // @_cdecl supported: fall through to MethodRequiresIndirectResult
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
                    AddParameter("IntPtr", "resultPtr");
                    // Decomposed Optional getter: add hasValuePtr after resultPtr.
                    // The Swift wrapper writes the inner payload to resultPtr and the hasValue flag to hasValuePtr.
                    if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                        !_env.MethodDecl.IsSubscriptAccessor &&
                        returnType.SwiftTypeSpec != null && OptionalMarshalClassifier.IsDecomposed(returnType.SwiftTypeSpec, _env.TypeDatabase))
                    {
                        AddParameter("IntPtr", "hasValuePtr");
                    }
                }
                else
                {
                    AddParameter("SwiftIndirectResult", "swiftIndirectResult");
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
                AddParameter(MarshalledType.AsyncCallback, NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl));
                AddParameter(MarshalledType.AsyncErrorCallback, NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl));
                AddParameter(MarshalledType.AsyncTask, "handle");
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
                        // Async+throwing closures use a different pattern - they pass context + start function
                        // to a Swift wrapper that creates the actual async closure
                        if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                        {
                            // Pass context pointer and start function pointer as separate parameters
                            // The Swift wrapper will use these to create the async closure
                            var callbackName = ClosureHandler.GetCallbackFunctionName(
                                _env.MethodDecl.Name, argument.Name, _env.MethodDecl.MangledName);
                            AddParameter(new MarshalledType.AsyncThrowingContext(csName), csName + "Context");
                            AddParameter(new MarshalledType.AsyncThrowingStartFunc(callbackName), csName + "StartFunc");
                        }
                        else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount))
                        {
                            if (_env.MethodDecl.HasCdeclClosureMarshalling)
                            {
                                // Cdecl closure wrapper (standalone or @_cdecl inline): pass func ptr + context as separate IntPtr params
                                var callbackName = ClosureHandler.GetCallbackFunctionName(
                                    _env.MethodDecl.Name, argument.Name, _env.MethodDecl.MangledName);
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
                    // Unsafe.Write — only safe for blittable-primitive elements. Non-blittable elements
                    // (existentials, bound generics, non-frozen types) need per-element marshalling
                    // that doesn't exist yet, so they fall through to the standard tuple path.
                    if (_env.MethodDecl.UsesCdeclWrapper && IsCdeclSafeTuple(tupleTypeSpec))
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
                                proxyClassName = _env.ExistentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
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
                    else
                    {
                        // Frozen (Data): use NativeRemappedFrozen type.
                        // For @_cdecl wrappers, C# passes the Swift.Data struct (16 bytes) via CallConvCdecl
                        // in two GP registers, matching the two-Int-word @_cdecl parameter in the Swift wrapper.
                        AddParameter(new MarshalledType.NativeRemappedFrozen(swiftWrapperType!), csName);
                    }
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                // @_cdecl property/subscript wrapper: String params via UTF-8 pointer + length.
                // Swift @_cdecl receives UnsafePointer<UInt8> + Int, reconstructs String from UTF-8.
                // Constructor/method wrappers use SwiftString.Buffer (two-word) path instead.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    argument.SwiftTypeSpec is NamedTypeSpec argStrNamed && argStrNamed.Name == "Swift.String")
                {
                    AddParameter("IntPtr", csName + "Utf8Ptr");
                    AddParameter("nint", csName + "Utf8Len");
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

                // SwiftString ABI decomposition: @_cdecl wrappers receive String as two Int words.
                // Decompose SwiftString.Buffer into two nint fields to match the Swift @_cdecl
                // parameter layout, avoiding ARM64 AAPCS64 struct-to-register ambiguity when
                // 4+ strings fill x0-x7. Non-@_cdecl paths retain FrozenBuffer (struct) passing.
                if (MarshallingHelpers.ShouldDecomposeStringForCdecl(_env.MethodDecl, argument.SwiftTypeSpec))
                {
                    AddParameter("nint", csName + "_w0");
                    AddParameter("nint", csName + "_w1");
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
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName);
                foreach (var conformance in conformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used here)
                    // This must match the check in EmitProtocolWitnessTables to avoid generating
                    // PInvoke signatures with parameters that have no corresponding variables.
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget, _env.TypeDatabase))
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
        /// Determines whether a protocol can be used as a generic constraint.
        /// Returns false for unknown protocols or protocols with associated types.
        /// </summary>
        private static bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
        {
            if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
            {
                // Must be a protocol and must NOT have associated types or Self requirements
                // (both generate generic interfaces which can't be used as non-generic constraints)
                return record.Kind == TypeRecordKind.Protocol &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
            }
            return false;
        }

        /// <summary>
        /// Returns true if all tuple elements are blittable primitives that can be safely
        /// written to a raw ABI buffer with Unsafe.Write. Non-primitive elements (existentials,
        /// bound generics, non-frozen structs, closures, strings) require per-element marshalling
        /// that the CdeclTuple path doesn't support.
        /// </summary>
        private static bool IsCdeclSafeTuple(TupleTypeSpec tupleTypeSpec)
        {
            return tupleTypeSpec.Elements.All(element => CdeclParamMapper.IsCdeclPrimitive(element));
        }

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
            var hasOpaqueReturn = methodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };
            var needsWrapperLib = methodDecl.IsAsync || hasOpaqueReturn || methodDecl.UsesWrapperLibrary;

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
        /// Emits the PInvoke signature or collects it to a helper context for generic types.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        /// <param name="methodEnv">The method environment.</param>
        /// <param name="signatureHandler">The signature handler.</param>
        public static void EmitPInvoke(CSharpWriter csWriter, MethodEnvironment methodEnv, SignatureHandler signatureHandler)
        {
            var methodDecl = (MethodDecl)methodEnv.MethodDecl;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pInvokeName = NameProvider.GetPInvokeName(methodDecl);
            var moduleLibPath = methodEnv.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var (entryPoint, needsWrapperLib) = ComputeEntryPoint(methodDecl);
            var libPath = needsWrapperLib && methodEnv.TypeDatabase.AsyncLibraryName != null
                ? methodEnv.TypeDatabase.AsyncLibraryName
                : moduleLibPath;

            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

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
                    CallingConvention = WrapperValidation.GetCallingConvention(methodDecl),
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
