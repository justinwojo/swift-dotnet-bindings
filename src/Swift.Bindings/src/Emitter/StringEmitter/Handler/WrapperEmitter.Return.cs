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
                    csWriter.WriteLine($@"
                        unsafe {{
                            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({_env.ParentDecl.Name}.Buffer));
                            *({_env.ParentDecl.Name}.Buffer*)bufferPtr = result;
                            _payload = new SwiftSafeHandle<{structDecl.Name}>(bufferPtr);
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

            // Check indirect result first - it takes precedence since the result is stored there.
            // This handles failable initializers (init?) that return SwiftOptional via indirect result.
            if (_requiresIndirectResult)
            {
                // Handle type conversion for indirect result
                if (_env.TypeConversionHandler.IsConvertibleType(returnArg.SwiftTypeSpec))
                {
                    EmitTypeConvertedIndirectReturn(csWriter, returnArg);
                    return;
                }

                // Handle native type remapping for indirect result
                // Skip for property accessors to maintain property/accessor type consistency
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(returnArg.SwiftTypeSpec);
                    if (_env.TypeConversionHandler.IsFoundationURL(returnArg.SwiftTypeSpec))
                    {
                        // URL via indirect result - create from handle and convert
                        csWriter.WriteLines($$"""
                            var swiftResult = new {{swiftWrapperType}}(new IntPtr(swiftIndirectResult.Value));
                            return swiftResult.ToNSUrl();
                            """);
                    }
                    else if (_env.TypeConversionHandler.IsFoundationData(returnArg.SwiftTypeSpec))
                    {
                        // Data via indirect result - marshal and convert
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftWrapperType}}>(new IntPtr(swiftIndirectResult.Value));
                            return swiftResult.ToNSData();
                            """);
                    }
                    return;
                }

                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(swiftIndirectResult.Value));");
                return;
            }

            // Handle type conversion for return values FIRST
            // Skip for property accessors to avoid type mismatch with property declaration
            if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.IsConvertibleType(returnArg.SwiftTypeSpec))
            {
                EmitTypeConvertedReturn(csWriter, returnArg);
                return;
            }

            // Bound generics that return IntPtr directly (not via indirect result)
            if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnArg))
            {
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg, _genericContext)}>(new IntPtr(&result));");
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

            // Handle existential return types (any Protocol) - result is ExistentialContainer, return as-is
            if (_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec))
            {
                csWriter.WriteLine("return result;");
                return;
            }

            // Handle Optional-wrapped existential return types
            if (_env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec))
            {
                csWriter.WriteLine("return result;");
                return;
            }

            // Handle tuple return types - result is ValueTuple, return as-is
            if (_env.TupleHandler.IsTuple(returnArg))
            {
                csWriter.WriteLine("return result;");
                return;
            }

            if (!returnArg.IsGeneric)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnArg.SwiftTypeSpec);

                // ObjC bridged types: wrap IntPtr result with GetNSObject<T>
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{_wrapperSignature.ReturnType}>(result);");
                    return;
                }

                // Swift classes return pointer directly - allocate buffer and store the pointer
                // The buffer is then managed by SwiftSafeHandle
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    csWriter.WriteLines($$"""
                        var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                        *(IntPtr*)classPayload = result;
                        return ({{_wrapperSignature.ReturnType}})SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(classPayload));
                        """);
                    return;
                }

                // Native type remapping: convert Swift type to native .NET type
                // Skip for property accessors to maintain property/accessor type consistency
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(returnArg.SwiftTypeSpec);
                    if (_env.TypeConversionHandler.IsFoundationURL(returnArg.SwiftTypeSpec))
                    {
                        // URL is non-frozen, result is IntPtr (SafeHandle marshalling)
                        // Create Swift.URL from handle, then convert to NSUrl
                        csWriter.WriteLines($$"""
                            var swiftResult = new {{swiftWrapperType}}(result);
                            return swiftResult.ToNSUrl();
                            """);
                    }
                    else if (_env.TypeConversionHandler.IsFoundationData(returnArg.SwiftTypeSpec))
                    {
                        // Data is frozen struct, marshal from buffer and convert to NSData
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftWrapperType}}>(new IntPtr(&result));
                            return swiftResult.ToNSData();
                            """);
                    }
                    return;
                }

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
        /// Emits return handling for type-converted return values.
        /// Converts Swift types (SwiftString, SwiftArray, SwiftOptional) to idiomatic .NET types.
        /// </summary>
        private void EmitTypeConvertedReturn(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (_env.TypeConversionHandler.IsSwiftString(returnArg.SwiftTypeSpec))
            {
                // SwiftString.Buffer -> string
                // Marshal from buffer to SwiftString, then convert to string
                csWriter.WriteLines($$"""
                    unsafe {
                        var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&result));
                        return swiftResult.ToString();
                    }
                    """);
            }
            else if (_env.TypeConversionHandler.IsSwiftArray(returnArg.SwiftTypeSpec))
            {
                // SwiftArray<T> -> IReadOnlyList<T>
                // SwiftArray already implements IReadOnlyList, so marshal and return directly
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg, _genericContext);
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{swiftType}>(new IntPtr(&result));");
            }
            else if (_env.TypeConversionHandler.IsSwiftOptional(returnArg.SwiftTypeSpec))
            {
                // SwiftOptional<T> -> T?
                // Marshal to SwiftOptional, then convert to nullable
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg, _genericContext);
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftType}}>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                    """);
            }
        }

        /// <summary>
        /// Emits return handling for type-converted return values via indirect result.
        /// Converts Swift types (SwiftString, SwiftArray, SwiftOptional) to idiomatic .NET types.
        /// </summary>
        private void EmitTypeConvertedIndirectReturn(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (_env.TypeConversionHandler.IsSwiftString(returnArg.SwiftTypeSpec))
            {
                // SwiftString -> string via indirect result
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToString();
                    """);
            }
            else if (_env.TypeConversionHandler.IsSwiftArray(returnArg.SwiftTypeSpec))
            {
                // SwiftArray<T> -> IReadOnlyList<T> via indirect result
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg, _genericContext);
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{swiftType}>(new IntPtr(swiftIndirectResult.Value));");
            }
            else if (_env.TypeConversionHandler.IsSwiftOptional(returnArg.SwiftTypeSpec))
            {
                // SwiftOptional<T> -> T? via indirect result
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg, _genericContext);
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftType}}>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToNullable();
                    """);
            }
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
                // Other bound generics → void* (opaque pointer)
                return "void*";
            }

            if (element is NamedTypeSpec named)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);

                // ObjC bridged types use IntPtr
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return "IntPtr";
                }

                // Non-frozen types needing memory management use Buffer type
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                    (typeRecord.Flags & TypeRecordFlags.Frozen) == 0)
                {
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
                }

                // Frozen types with memory management use Buffer type
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                    (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
                }

                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return "void*";
        }

        /// <summary>
        /// Gets the C# type name for a tuple element.
        /// </summary>
        private string GetCSharpTypeForTupleElement(TypeSpec element)
        {
            // Handle Optional<T> (bound generic with Optional)
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord))
                {
                    // Recursively translate generic parameters
                    var translatedParams = new List<string>();
                    foreach (var param in namedType.GenericParameters)
                    {
                        translatedParams.Add(GetCSharpTypeForTupleElement(param));
                    }
                    return $"{baseRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }
            }

            if (element is NamedTypeSpec named)
            {
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
            // Handle Optional<T> types
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
                    // Non-ObjC optional: marshal from buffer
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                }
            }

            // Handle non-generic types
            if (element is NamedTypeSpec named)
            {
                if (_env.TypeDatabase.TryGetTypeRecord(named, out var typeRecord))
                {
                    // ObjC bridged types
                    if (MarshallingHelpers.IsObjCBridged(typeRecord))
                    {
                        return $"var {resultName} = ObjCRuntime.Runtime.GetNSObject<{csharpType}>({itemName});";
                    }

                    // Primitive types - use directly
                    if (typeRecord.Kind == TypeRecordKind.Struct &&
                        (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
                        (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) == 0)
                    {
                        return $"var {resultName} = {itemName};";
                    }

                    // Frozen structs requiring memory management
                    if (MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                    }

                    // Non-frozen types
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                }
            }

            // Fallback
            return $"var {resultName} = {itemName};";
        }
    }
}
