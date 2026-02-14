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
                var csTypeParam = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) switch
                {
                    true => _env.BoundGenericsHandler.GetBufferType(returnType),
                    false => _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnType, _genericContext)
                };
                SetReturnType(csTypeParam);
                return;
            }

            // Handle closure return types (including optional closures)
            // Swift returns closures as SwiftClosureData (function + context pointers)
            // Optional closures use the same struct - nil is represented by zero pointers
            if (_env.ClosureHandler.IsClosure(returnType))
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
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                        _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                        SetReturnType(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec, _genericContext));
                    else
                        SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                    return;
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
                    var existentialType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                    SetReturnType(existentialType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle Optional-wrapped existential return types like (any DataCaching)?
            // For P/Invoke, these use IntPtr since they require indirect marshalling
            if (_env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnType.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    // Optional existentials are passed by pointer/indirect result
                    SetReturnType("IntPtr");
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            if (MarshallingHelpers.MethodRequiresIndirectResult(_env))
            {
                AddParameter("SwiftIndirectResult", "swiftIndirectResult");
                SetReturnType("void");
                return;
            }

            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

            // ObjC bridged types return IntPtr in P/Invoke, then wrapped with GetNSObject<T>
            if (MarshallingHelpers.IsObjCBridged(returnTypeRecord))
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

            // Simple enums: return the underlying integer type, cast back in the wrapper
            if (returnTypeRecord.Kind == TypeRecordKind.Enum && returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                SetReturnType(EnumHandler.GetCSharpEnumUnderlyingType(returnTypeRecord.RawValueTypeName));
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
                AddParameter("AsyncCallback", NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl));
                AddParameter("AsyncErrorCallback", NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl));
                AddParameter("AsyncTask", "handle");
            }
        }

        /// <summary>
        /// Handles the arguments of the method.
        /// Uses GetCSharpParameterName() so P/Invoke call expressions match wrapper body variable names.
        /// </summary>
        public void HandleArguments()
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);

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
                            // Use special type markers that include the parameter name for variable mapping
                            var callbackName = ClosureHandler.GetCallbackFunctionName(
                                _env.MethodDecl.Name, argument.Name, _env.MethodDecl.MangledName);
                            AddParameter($"AsyncThrowingContext:{csName}", csName + "Context");
                            AddParameter($"AsyncThrowingStartFunc:{callbackName}", csName + "StartFunc");
                        }
                        else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                        {
                            if (_env.MethodDecl.HasClosureCdeclWrapper)
                            {
                                // Cdecl closure wrapper: pass func ptr + context as separate IntPtr params
                                var callbackName = ClosureHandler.GetCallbackFunctionName(
                                    _env.MethodDecl.Name, argument.Name, _env.MethodDecl.MangledName);
                                AddParameter($"CdeclClosureFuncPtr:{callbackName}:{csName}", csName + "FuncPtr");
                                AddParameter($"CdeclClosureContext:{csName}", csName + "Context");
                            }
                            else
                            {
                                // Legacy path: pass as SwiftClosureData (for async methods with non-async closures)
                                AddParameter("SwiftClosureData", csName);
                            }
                        }
                        else
                        {
                            // @convention(c) closures just need the function pointer
                            var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
                            AddParameter(funcPtrType, csName);
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
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
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
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var containerType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                        var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                        AddParameter($"Existential:{containerType}:{publicType}", csName);
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
                    if (!MarshallingHelpers.IsTypeFrozen(nativeRemapTypeRecord))
                    {
                        // Non-frozen (URL): use NativeRemappedSafeHandle marker
                        AddParameter("NativeRemappedSafeHandle", csName);
                    }
                    else
                    {
                        // Frozen (Data): use NativeRemapped:{type} marker
                        AddParameter($"NativeRemapped:{swiftWrapperType}", csName);
                    }
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                if (argument.IsGeneric)
                {
                    var payloadName = NameProvider.GetPayloadName(csName);
                    AddParameter("IntPtr", payloadName, inoutModifier);
                    continue;
                }

                TypeRecord argumentTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argument.SwiftTypeSpec);

                // ObjC bridged types use IntPtr in P/Invoke, Handle extracted from the .NET iOS binding
                if (MarshallingHelpers.IsObjCBridged(argumentTypeRecord))
                {
                    // Store the original C# type name for use in wrapper generation
                    AddParameter($"ObjCBridged:{argumentTypeRecord.CSharpTypeName.FullyQualifiedName}", csName);
                    continue;
                }

                // Enum values: simple enums (C# value types) use their underlying int type,
                // complex enums use SafeHandle payload pointer.
                if (argumentTypeRecord.Kind == TypeRecordKind.Enum)
                {
                    if (argumentTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    {
                        // Simple enums are C# enum value types — pass as underlying int
                        var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(argumentTypeRecord.RawValueTypeName);
                        AddParameter($"SimpleEnum:{underlyingType}:{argumentTypeRecord.CSharpTypeName.FullyQualifiedName}", csName);
                    }
                    else if (_env.MethodDecl.IsAsync)
                        AddParameter("IntPtrFromNonFrozen", csName);
                    else
                        AddParameter("EnumSafeHandle", csName);
                    continue;
                }

                if (!MarshallingHelpers.IsTypeFrozen(argumentTypeRecord))
                {
                    // For async methods, SafeHandle cannot be used with Swift calling convention.
                    // Use IntPtr and manage lifetime manually via DangerousAddRef/DangerousRelease.
                    if (_env.MethodDecl.IsAsync)
                        AddParameter("IntPtrFromNonFrozen", csName);
                    else
                        AddParameter("SafeHandle", csName);
                    continue;
                }

                if (MarshallingHelpers.RequiresMemoryManagement(argumentTypeRecord))
                    AddParameter(argumentTypeRecord.CSharpTypeName.FullyQualifiedName + ".Buffer", csName, inoutModifier);
                else
                    AddParameter(argumentTypeRecord.CSharpTypeName.FullyQualifiedName, csName, inoutModifier);
            }
        }

        /// <summary>
        /// Handles the metadata of generic arguments.
        /// </summary>
        public void HandleGenericMetadata()
        {
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var metadataName = NameProvider.GetMetadataName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter);
                AddParameter("TypeMetadata", metadataName);
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
                    AddParameter("ProtocolWitnessTable", pwtName);
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
                // Must be a protocol and must NOT have associated types
                // (protocols with associated types generate generic interfaces which can't be used as constraints)
                return record.Kind == TypeRecordKind.Protocol &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
            }
            return false;
        }

        /// <summary>
        /// Handles the SwiftSelf parameter of the method.
        /// </summary>
        public void HandleSwiftSelf()
        {
            // Standalone closure Cdecl wrapper uses free-function Swift wrapper.
            // Pass self as explicit IntPtr (same as async pattern).
            // Wrapper generator paths (ArraySlice, DefaultParam) keep extension methods
            // with implicit self via SwiftSelf — they set HasClosureCdeclWrapper but NOT UsesFreeFunctionWrapper.
            if (_env.MethodDecl.UsesFreeFunctionWrapper && MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                if (_env.ParentDecl is ClassDecl)
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

            // Async instance methods on non-singleton classes pass self as explicit IntPtr parameter.
            // We use a module-level free function (not extension method) to avoid SwiftSelf binding issues.
            // Singleton classes use the ClassName.shared workaround and don't need _self.
            if (_env.MethodDecl.IsAsync && MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                var hasSingleton = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;
                if (!hasSingleton)
                {
                    // For non-singleton async methods, pass self as explicit IntPtr
                    // Use different parameter names to distinguish at call site:
                    // - _selfClass: class instance (needs dereferencing - payload contains pointer to class)
                    // - _self: struct instance (no dereference - payload IS the data)
                    var selfName = _env.ParentDecl is ClassDecl ? "_selfClass" : "_self";
                    AddParameter("IntPtr", selfName);
                }
                // For singleton classes, don't add any self parameter - we use .shared in Swift
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
                        AddParameter("SwiftSelf", "self");
                    }
                    else
                    {
                        // Getters can use value semantics for frozen structs
                        // Use resolved type name (may be renamed for nested type collision avoidance)
                        var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                        var resolvedName = GetResolvedParentTypeName();
                        if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                            AddParameter($"SwiftSelf<{resolvedName}.Buffer>", "self");
                        else
                            AddParameter($"SwiftSelf<{resolvedName}>", "self");
                    }
                }
                else
                {
                    AddParameter("SwiftSelf", "self");
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
                AddParameter("SwiftError", "error", "out");
            }
        }

        /// <summary>
        /// Adds context parameter to a function pointer type string for escaping closures.
        /// Transforms "delegate* unmanaged[Cdecl]&lt;int, void&gt;" to "delegate* unmanaged[Cdecl]&lt;int, IntPtr, void&gt;"
        /// </summary>
        private static string AddContextToFunctionPointerType(string funcPtrType)
        {
            // Find the last comma before '>'
            int lastAngle = funcPtrType.LastIndexOf('>');
            if (lastAngle == -1)
                return funcPtrType;

            int lastComma = funcPtrType.LastIndexOf(',', lastAngle);
            if (lastComma == -1)
            {
                // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
                // Insert "IntPtr, " before the return type
                int openAngle = funcPtrType.IndexOf('<');
                if (openAngle == -1)
                    return funcPtrType;

                return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
            }

            // Insert ", IntPtr" after the last comma
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
            var entryPoint = NameProvider.GetMangledName(methodDecl);

            // With library evolution, non-final class instance methods and property accessors
            // are dispatched through vtable thunks. The bare method symbol is a local
            // (non-exported) symbol in the dylib; only the dispatch thunk (Tj suffix) is
            // globally exported. Final classes use direct dispatch and export bare symbols.
            // Individual members can also be final (e.g. stored let properties) — these
            // use direct dispatch even inside non-final classes.
            // Constructors and static methods are directly exported and don't need this.
            // Wrapper library methods use @_silgen_name/@_cdecl free functions, not thunked.
            if (!needsWrapperLib &&
                methodDecl.ParentDecl is ClassDecl classParent &&
                !classParent.IsFinal &&
                !methodDecl.IsFinal &&
                methodDecl.MethodType == MethodType.Instance &&
                !methodDecl.IsConstructor)
            {
                entryPoint += "Tj";
            }

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
                    MetadataParameters = methodEnv.PInvokeHelperContext.GetMetadataParameterDeclarations()
                };
                methodEnv.PInvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                // Emit directly (non-generic type)
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{entryPoint}\")]");
                var pInvokeParams = pInvokeSignature.PInvokeParametersString();
                var unsafeModifier = pInvokeParams.Contains("void*") || pInvokeParams.Contains("delegate*") ? "unsafe " : "";
                csWriter.WriteLine($"private static {unsafeModifier}extern {(methodDecl.IsAsync ? "void" : pInvokeSignature.ReturnType)} {pInvokeName}({pInvokeParams});");
            }
        }
    }
}
