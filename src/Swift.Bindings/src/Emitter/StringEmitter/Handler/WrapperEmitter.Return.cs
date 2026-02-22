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
            if (_env.ParentDecl is StructDecl structDecl)
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    var resolvedName = GetResolvedTypeName();
                    csWriter.WriteLine($@"
                        unsafe {{
                            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({resolvedName}.Buffer));
                            *({resolvedName}.Buffer*)bufferPtr = result;
                            _payload = new SwiftSafeHandle<{resolvedName}>(bufferPtr);
                        }}");
                    return;
                }
            }
            if (!_requiresIndirectResult)
            {
                csWriter.WriteLine("this = result;");
            }
        }

        /// <summary>
        /// Emits the return statement for the method.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitReturnMethod(CSharpWriter csWriter)
        {
            var returnArg = _env.MethodDecl.CSSignature.First();

            if (_requiresSwiftAsync)
            {
                csWriter.WriteLine("return task.Task;");
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
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(swiftIndirectResult.Value));");
                return;
            }

            // Large Optional return via out-buffer fallback — for accessor returns where projection
            // is skipped. Uses projection ContainerTypeName for the SwiftOptional type if available.
            if (_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
                (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary))
            {
                var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                    new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext });
                var swiftType = projection?.ContainerTypeName ?? _wrapperSignature.ReturnType;
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftType}}>(_optRetPtr);
                    return swiftResult.ToNullable();
                    """);
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
                        var proxyName = _env.ExistentialHandler.GetProxyClassName(innerProtocolList);
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
                    new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext });
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

                var proxyClassName = _env.ExistentialHandler.GetProxyClassName(protocolList);
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
                        var optProxyClassName = _env.ExistentialHandler.GetProxyClassName(innerProtocolList);
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
                    csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{_wrapperSignature.ReturnType}>(result);");
                    return;
                }

                // Swift classes return pointer directly - allocate buffer and store the pointer
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    csWriter.WriteLines($$"""
                        var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                        try
                        {
                            *(IntPtr*)classPayload = result;
                            return ({{_wrapperSignature.ReturnType}})SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(classPayload));
                        }
                        catch
                        {
                            NativeMemory.Free(classPayload);
                            throw;
                        }
                        """);
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
                new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext });
            if (projection == null) return false;

            var strategy = DetermineReturnStrategy();
            string resultName = strategy switch
            {
                ReturnStrategy.IndirectResult => "new IntPtr(swiftIndirectResult.Value)",
                ReturnStrategy.OutBuffer => "_optRetPtr",
                _ => "result"
            };

            var plan = projection.GetReturnPlan(resultName, strategy);

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
                csWriter.WriteLine(line);
            }
            csWriter.WriteLine($"return ({string.Join(", ", resultElements)});");
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
            // Handle Optional<T> (bound generic with Optional)
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
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
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional")
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
                            return $"var {resultName} = {itemName} == IntPtr.Zero ? Swift.SwiftOptional<{innerCSharp}>.NewNone() : Swift.SwiftOptional<{innerCSharp}>.NewSome(ObjCRuntime.Runtime.GetNSObject<{innerCSharp}>({itemName}));";
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
                        return $"var {resultName} = ObjCRuntime.Runtime.GetNSObject<{csharpType}>({itemName});";
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

            // Frozen blittable primitives — use directly
            return $"var {resultName} = {itemName};";
        }
    }
}
