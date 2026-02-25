// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Emits a static method for an enum case with associated values.
        /// </summary>
        private bool EmitEnumCaseWithAssociatedValues(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext, Dictionary<string, string>? propertyRenames = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetFinalMemberName(
                NameProvider.ToPascalCase(caseName), propertyRenames);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter list from associated values
            // Track both internal type (for validation/P/Invoke) and public type (for method signatures)
            var parameters = new List<(string type, string publicType, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var enumGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping case.");
                    return false;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams);

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                // Sanitize parameter name (remove invalid characters, ensure starts with letter)
                paramName = SanitizeParameterName(paramName);
                // Skip enum cases with tuple parameters containing unresolved existential elements.
                // When a tuple element is an existential that can't be projected to an interface
                // (e.g., Any → ExistentialContainer0), the public type leaks ABI types that are
                // invalid as C# tuple elements. The method body would also pass @object.object
                // to P/Invoke expecting ExistentialContainer0, with no runtime conversion available.
                if (typeSpec is TupleTypeSpec && publicType.Contains("ExistentialContainer"))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has tuple with unresolved existential element. Skipping case.");
                    return false;
                }

                parameters.Add((csharpType, publicType, paramName, typeSpec));
            }

            // Generate the static method for this case — prefer symbol graph doc over synthetic
            if (caseDecl.Documentation != null && !caseDecl.Documentation.IsEmpty)
            {
                XmlDocCommentEmitter.EmitDocComment(csWriter, caseDecl);
            }
            else
            {
                csWriter.WriteLine($"/// <summary>");
                csWriter.WriteLine($"/// Creates the '{caseName}' case of {enumTypeName}.");
                csWriter.WriteLine($"/// </summary>");
            }

            var parameterString = string.Join(", ", parameters.Select(p => $"{p.publicType} {p.name}"));
            // C10: Use unique local variable name to avoid CS0136 if a parameter is also named "result"
            var resultVarName = parameters.Any(p => p.name == "result") ? "__enumResult" : "result";
            csWriter.WriteLine($"public static unsafe {enumTypeName} {capitalizedName}({parameterString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var {resultVarName} = new {enumTypeName}();");

            // Emit conversions for parameters that differ between public and internal types
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var projectedArgs = new Dictionary<int, MarshalPlan>();
            // Track tuple params with per-element conversion (maps param index → converted tuple expression)
            var tuplePInvokeExprs = new Dictionary<int, string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, publicType, name, typeSpec) = parameters[i];
                if (typeConversionHandler.IsSwiftString(typeSpec))
                {
                    csWriter.WriteLine($"using var __{name} = new SwiftString({name});");
                }
                else if (typeSpec is TupleTypeSpec tupleSpec && publicType != type)
                {
                    // Tuple with projected elements — emit per-element conversion.
                    // Build a converted tuple expression for the P/Invoke call.
                    var genericContext = enumDecl.IsGeneric
                        ? BuildGenericContextFromEnumParams(enumDecl.GenericParameters)
                        : GenericContext.Empty;
                    // Emit per-element marshalling from public types → ABI types.
                    // The public signature uses factory-projected types (string, int?, etc.)
                    // but the P/Invoke tuple uses lowered types (IntPtr, nint, etc.).
                    // We marshal each element from public → ABI, then use GetPInvokeArgument
                    // to get the correct lowered expression for the tuple P/Invoke call.
                    var factory = new TypeProjectionFactory();
                    var elementExprs = new List<string>();
                    for (int j = 0; j < tupleSpec.Elements.Count; j++)
                    {
                        var element = tupleSpec.Elements[j];
                        var elementAccess = !string.IsNullOrEmpty(element.TypeLabel)
                            ? $"{name}.{element.TypeLabel}"
                            : $"{name}.Item{j + 1}";

                        var proj = factory.Project(element, new ProjectionContext
                        {
                            TypeDatabase = typeDatabase, IsParameter = true, GenericContext = genericContext
                        });
                        if (proj is StringProjection)
                        {
                            // String: public is `string`, P/Invoke tuple element is IntPtr.
                            // Convert string → SwiftString, then extract IntPtr handle.
                            var elemVarName = $"__{name}_e{j}";
                            csWriter.WriteLine($"using var {elemVarName} = new SwiftString({elementAccess});");
                            elementExprs.Add(GetPInvokeArgument(elemVarName, element, typeDatabase));
                        }
                        else if (proj is NativeRemappedProjection nrp)
                        {
                            // NativeRemapped (URL, etc.): factory creates wrapper, but P/Invoke tuple
                            // element is IntPtr. Use factory setup, then extract IntPtr from the wrapper.
                            var elemVarName = $"__{name}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = {elementAccess};");
                            var elemPlan = nrp.GetParameterPlan(elemVarName);
                            MarshalPlanRenderer.RenderStatements(csWriter, elemPlan.SetupStatements);
                            // For non-frozen NativeRemapped (URL): plan gives "{name}Swift.Payload" (SwiftSafeHandle).
                            // Need to add .DangerousGetHandle() for IntPtr.
                            var pinvokeExpr = elemPlan.PInvokeExpression;
                            if (pinvokeExpr.EndsWith(".Payload"))
                                pinvokeExpr += ".DangerousGetHandle()";
                            elementExprs.Add(pinvokeExpr);
                        }
                        else if (proj is OptionalProjection or ArrayProjection or DictionaryProjection
                            or SetProjection or ExistentialProjection)
                        {
                            // These projections produce PInvokeExpression that already matches
                            // the tuple P/Invoke type (IntPtr for Optional/Array/Dict, ExistentialContainer for existentials).
                            var elemVarName = $"__{name}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = {elementAccess};");
                            var elemPlan = proj.GetParameterPlan(elemVarName);
                            MarshalPlanRenderer.RenderStatements(csWriter, elemPlan.SetupStatements);
                            elementExprs.Add(elemPlan.PInvokeExpression);
                        }
                        else
                        {
                            // No projection or unsupported — use direct P/Invoke argument lowering
                            elementExprs.Add(GetPInvokeArgument(elementAccess, element, typeDatabase));
                        }
                    }
                    tuplePInvokeExprs[i] = $"({string.Join(", ", elementExprs)})";
                }
                else if (publicType != type && typeSpec is NamedTypeSpec ns && ns.ContainsGenericParameters)
                {
                    // Bound generic with factory projection — emit conversion via MarshalPlan
                    var genericContext = enumDecl.IsGeneric
                        ? BuildGenericContextFromEnumParams(enumDecl.GenericParameters)
                        : GenericContext.Empty;
                    var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = true, GenericContext = genericContext
                    });
                    if (projection != null)
                    {
                        var plan = projection.GetParameterPlan(name);
                        MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);
                        projectedArgs[i] = plan;
                    }
                }
            }

            // Swift enum case constructors use indirect return - allocate buffer and pass it
            var getMetadataCall = pinvokeHelperContext != null
                ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                : "PInvoke_getMetadata()";
            csWriter.WriteLine($"var metadata = {getMetadataCall};");
            csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
            csWriter.WriteLine($"var indirectResult = new SwiftIndirectResult((void*)buffer);");

            // Build the P/Invoke call with arguments
            var argList = new List<string> { "indirectResult" };
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, _, name, typeSpec) = parameters[i];
                if (typeSpec is NamedTypeSpec genericParamType &&
                    TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
                {
                    csWriter.WriteLine($"var {name}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{type}>();");
                    csWriter.WriteLine($"byte* {name}SwiftBuffer = stackalloc byte[(int){name}Metadata.Size];");
                    csWriter.WriteLine($"var {name}SwiftSpan = new Span<byte>({name}SwiftBuffer, (int){name}Metadata.Size);");
                    csWriter.WriteLine($"SwiftMarshal.MarshalToSwift({name}, ref {name}SwiftSpan);");
                    argList.Add($"(IntPtr){name}SwiftBuffer");
                }
                else if (projectedArgs.TryGetValue(i, out var projPlan))
                {
                    argList.Add(projPlan.PInvokeExpression);
                }
                else if (tuplePInvokeExprs.TryGetValue(i, out var tupleExpr))
                {
                    argList.Add(tupleExpr);
                }
                else
                {
                    var argName = typeConversionHandler.IsSwiftString(typeSpec) ? $"__{name}" : name;
                    argList.Add(GetPInvokeArgument(argName, typeSpec, typeDatabase));
                }
            }

            var invokeArgList = string.Join(", ", argList);
            if (pinvokeHelperContext != null)
            {
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList());
                invokeArgList = string.IsNullOrEmpty(invokeArgList) ? metadataArgs : $"{invokeArgList}, {metadataArgs}";
                csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pInvokeName}({invokeArgList});");
            }
            else
            {
                csWriter.WriteLine($"{pInvokeName}({invokeArgList});");
            }
            csWriter.WriteLine($"{resultVarName}._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine($"return {resultVarName};");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor with associated values - uses indirect result
            // C5: Use unique name for indirect result param to avoid CS0100 if an associated value
            // is also named "result"
            var indirectResultParamName = parameters.Any(p => p.name == "result") ? "__result" : "result";
            var pInvokeParams = new List<string> { $"SwiftIndirectResult {indirectResultParamName}" };
            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, _, name, typeSpec) = parameters[i];
                var pInvokeType = GetPInvokeType(typeSpec, typeDatabase);
                var marshalPrefix = MarshallingHelpers.IsBoolType(pInvokeType) ? "[MarshalAs(UnmanagedType.U1)] " : "";
                pInvokeParams.Add($"{marshalPrefix}{pInvokeType} {name}");
            }

            if (pinvokeHelperContext != null)
            {
                pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = caseDecl.MangledName,
                    MethodName = pInvokeName,
                    ReturnType = "void",
                    ParametersString = string.Join(", ", pInvokeParams),
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                });
            }
            else
            {
                csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"private static partial void {pInvokeName}({string.Join(", ", pInvokeParams)});");
                csWriter.WriteLine();
            }
            return true;
        }

        /// <summary>
        /// Gets the C# type name for an enum case associated value type.
        /// </summary>
        private static string GetCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name) &&
                TryGetGenericTypeParameterName(genericParamType.Name, out var typeParameterName, genericParams))
            {
                return typeParameterName;
            }

            // Handle existential types (any Protocol) - return ExistentialContainer
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    return existentialHandler.GetCSharpExistentialType(protocolList);
                }
            }

            // Handle protocol list types (protocol composition)
            if (typeSpec is ProtocolListTypeSpec protocolListSpec)
            {
                return existentialHandler.GetCSharpExistentialType(protocolListSpec);
            }

            // Handle bound generics (e.g., Optional<T>, Array<T>)
            // Use TranslateBoundGenericTypeToCSharp (NOT factory) to produce raw ABI type names
            // (e.g., SwiftArray<string>, SwiftOptional<SwiftResult<...>>). The factory produces
            // public types (IReadOnlyList<string>) which don't have .Payload for P/Invoke access.
            if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use a recursive translator that handles bound generics for each element
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler, genericParams));
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Gets the public C# type name for an enum case associated value type.
        /// For existentials where all protocols have TypeRecords, returns the interface type (e.g., "IImageProcessing").
        /// For existentials with unknown/unregistered protocols, falls back to the container type (ExistentialContainer{N}).
        /// For tuples, recurses per element.
        /// For everything else, delegates to GetCSharpTypeNameForEnumCase.
        /// </summary>
        private static string GetPublicCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);

            // Handle existential types (any Protocol) - return interface if all protocols are known
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    return existentialHandler.GetPublicExistentialType(protocolList);
                }
            }

            // Handle protocol list types (protocol composition)
            if (typeSpec is ProtocolListTypeSpec protocolListSpec)
            {
                if (existentialHandler.AllProtocolsHaveTypeRecords(protocolListSpec))
                {
                    return existentialHandler.GetPublicExistentialType(protocolListSpec);
                }
            }

            // Handle tuple types - recurse per element using idiomatic projection.
            // TupleProjection.GetParameterPlan() handles per-element conversion in the body.
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetPublicCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler, genericParams));
            }

            // Handle SwiftString → string for public API
            if (typeConversionHandler.IsSwiftString(typeSpec))
                return "string";

            // Handle bound generics (Optional<T>, Array<T>, Dictionary<K,V>) via factory
            // The factory produces idiomatic public types (string?, IReadOnlyList<T>, IReadOnlyDictionary<K,V>).
            // Always use IsParameter=false so types are consistent between construction (factory methods)
            // and deconstruction (TryGet out parameters). IReadOnlyList/IReadOnlyDictionary work for both
            // since construction uses IEnumerable-based conversion and TryGet uses AsProjected.
            // Skip closure types — delegate* can't be used as generic type arguments in MarshalFromSwift<T>.
            // Use recursive check to catch nested closures like Optional<Array<Closure>>.
            if (typeSpec is NamedTypeSpec namedBoundGeneric && namedBoundGeneric.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedBoundGeneric))
            {
                var genericContext = genericParams != null
                    ? BuildGenericContextFromEnumParams(genericParams)
                    : GenericContext.Empty;
                var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = false,
                    GenericContext = genericContext
                });
                if (projection != null)
                    return projection.PublicType;
            }

            // Everything else: delegate to the internal type
            return GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
        }

        /// <summary>
        /// Gets the P/Invoke argument expression for a parameter.
        /// </summary>
        private static string GetPInvokeArgument(string paramName, TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Handle existential types
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    // Interface-typed parameter: extract the container via ISwiftExistentialConvertible
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    return $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){paramName}).GetExistentialContainer()";
                }
                // Unknown protocol: pass the container directly (it's a blittable struct)
                return paramName;
            }

            // Handle tuple types - need to construct a ValueTuple with extracted payloads
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var elementArgs = new List<string>();
                for (int i = 0; i < tupleType.Elements.Count; i++)
                {
                    var element = tupleType.Elements[i];
                    // Access tuple element by name if it has a label, otherwise by Item1, Item2, etc.
                    var elementAccess = !string.IsNullOrEmpty(element.TypeLabel)
                        ? $"{paramName}.{element.TypeLabel}"
                        : $"{paramName}.Item{i + 1}";

                    // Recursively get the P/Invoke argument for this element
                    elementArgs.Add(GetPInvokeArgument(elementAccess, element, typeDatabase));
                }
                return $"({string.Join(", ", elementArgs)})";
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // ObjC bridged types use .Handle to get the native pointer
            if (MarshallingHelpers.IsObjCBridged(typeRecord))
            {
                return $"{paramName}.Handle";
            }

            // Enum values are projected as managed wrappers with SafeHandle payload.
            // Extract the raw pointer for P/Invoke.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                {
                    var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                    return $"({underlyingType}){paramName}";
                }
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // For types that have payloads (non-frozen structs, classes), access the Payload.DangerousGetHandle()
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Non-frozen structs (ClassWithOpaquePayload) — extract SafeHandle payload
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Swift classes — extract SafeHandle payload
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // AnyType fallback — extract SafeHandle payload. Reached when an unknown type
            // appears inside a tuple (GetPInvokeArgument recurses per element; the unknown
            // element resolves to AnyType which has Kind=Protocol). Direct AnyType associated
            // values are caught earlier (line 32 skip). Protocol existentials use
            // ProtocolListTypeSpec handled by the existential path above.
            if (typeRecord == TypeDatabaseExtensions.AnyType)
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            return paramName;
        }

        /// <summary>
        /// Gets the P/Invoke parameter type for an associated value.
        /// </summary>
        private static string GetPInvokeType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
            {
                return "IntPtr";
            }

            // Handle existential types - use the ExistentialContainer struct directly
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    return existentialHandler.GetPInvokeExistentialType(protocolList);
                }
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use recursive type translation for P/Invoke tuple elements
                return tupleHandler.GetPInvokeTupleType(tupleType, elementTypeSpec =>
                    GetPInvokeType(elementTypeSpec, typeDatabase));
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // Enum values are projected as managed wrappers (C# classes with SafeHandle payload),
            // which are non-blittable for Swift calling convention P/Invoke.
            // Always use IntPtr for enum parameters.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                return "IntPtr";
            }

            // For types that require memory management, use IntPtr in P/Invoke
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return "IntPtr";
            }

            // Non-frozen structs (ClassWithOpaquePayload) are C# classes — non-blittable.
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return "IntPtr";
            }

            // Swift classes are also non-blittable C# classes.
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return "IntPtr";
            }

            // Frozen blittable structs — use the C# type directly
            if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Fallback — IntPtr is safe for any unknown type (Protocol/AnyType, etc.)
            return "IntPtr";
        }

        /// <summary>
        /// Recursively checks whether a TypeSpec contains any ClosureTypeSpec.
        /// Closure function pointers (delegate*) cannot be used as C# generic type arguments,
        /// so containers with nested closures must skip factory projection.
        /// </summary>
        private static bool ContainsClosureTypeSpec(TypeSpec typeSpec)
        {
            if (typeSpec is ClosureTypeSpec)
                return true;
            if (typeSpec is NamedTypeSpec named)
                return named.GenericParameters.Any(ContainsClosureTypeSpec);
            if (typeSpec is TupleTypeSpec tuple)
                return tuple.Elements.Any(ContainsClosureTypeSpec);
            return false;
        }

        /// <summary>
        /// Builds a GenericContext from enum generic parameters for use with TypeProjectionFactory.
        /// Maps τ_0_0 → T0/T/TKey etc. based on the enum's GenericArgumentDecl list.
        /// </summary>
        private static GenericContext BuildGenericContextFromEnumParams(IReadOnlyList<GenericArgumentDecl> genericParams)
        {
            var mapping = new Dictionary<string, GenericParameterCSName>();
            for (int i = 0; i < genericParams.Count; i++)
            {
                var param = genericParams[i];
                var csName = NameProvider.GetCSharpGenericParameterName(param, i);
                mapping[param.TypeName] = new GenericParameterCSName(csName);
            }
            return new GenericContext(mapping);
        }

        private static bool TryGetGenericTypeParameterName(string swiftTypeName, out string typeParameterName,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            typeParameterName = string.Empty;
            if (string.IsNullOrWhiteSpace(swiftTypeName))
                return false;

            if (swiftTypeName.StartsWith("τ_"))
            {
                var parts = swiftTypeName.Split('_');
                if (parts.Length > 0 && int.TryParse(parts[^1], out var index))
                {
                    if (genericParams != null && index < genericParams.Count)
                        typeParameterName = NameProvider.GetCSharpGenericParameterName(genericParams[index], index);
                    else
                        typeParameterName = $"T{index}";
                    return true;
                }
            }

            if (swiftTypeName.Length > 1 &&
                swiftTypeName[0] == 'T' &&
                int.TryParse(swiftTypeName.Substring(1), out _))
            {
                typeParameterName = swiftTypeName;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Sanitizes a parameter name to be a valid C# identifier.
        /// </summary>
        private static string SanitizeParameterName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "value";

            // Replace invalid characters with underscores
            var sanitized = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sanitized.Append(c);
                else
                    sanitized.Append('_');
            }

            var result = sanitized.ToString();

            // Ensure starts with letter or underscore
            if (result.Length > 0 && char.IsDigit(result[0]))
                result = "_" + result;

            // Handle C# reserved keywords
            var keywords = new HashSet<string> { "string", "int", "bool", "float", "double", "object", "class", "struct", "enum", "delegate", "event", "interface", "namespace", "using", "static", "public", "private", "protected", "internal", "abstract", "sealed", "virtual", "override", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "goto", "throw", "try", "catch", "finally", "lock", "using", "checked", "unchecked", "fixed", "unsafe", "volatile", "extern", "ref", "out", "in", "params", "this", "base", "null", "true", "false", "is", "as", "typeof", "sizeof", "stackalloc", "await", "async", "yield", "nameof", "var", "dynamic" };

            if (keywords.Contains(result))
                result = "@" + result;

            return string.IsNullOrEmpty(result) ? "value" : result;
        }
    }
}
