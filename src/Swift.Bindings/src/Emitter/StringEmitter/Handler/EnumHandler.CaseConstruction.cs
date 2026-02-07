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
        private bool EmitEnumCaseWithAssociatedValues(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter list from associated values
            var parameters = new List<(string type, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping case.");
                    return false;
                }

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                // Sanitize parameter name (remove invalid characters, ensure starts with letter)
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, paramName, typeSpec));
            }

            // Generate the static method for this case
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Creates the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");

            var parameterString = string.Join(", ", parameters.Select(p => $"{p.type} {p.name}"));
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}({parameterString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var result = new {enumTypeName}();");

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
                var (type, name, typeSpec) = parameters[i];
                if (typeSpec is NamedTypeSpec genericParamType &&
                    TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
                {
                    csWriter.WriteLine($"var {name}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{type}>();");
                    csWriter.WriteLine($"byte* {name}SwiftBuffer = stackalloc byte[(int){name}Metadata.Size];");
                    csWriter.WriteLine($"var {name}SwiftSpan = new Span<byte>({name}SwiftBuffer, (int){name}Metadata.Size);");
                    csWriter.WriteLine($"SwiftMarshal.MarshalToSwift({name}, ref {name}SwiftSpan);");
                    argList.Add($"(IntPtr){name}SwiftBuffer");
                }
                else
                {
                    argList.Add(GetPInvokeArgument(name, typeSpec, typeDatabase));
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
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine("return result;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor with associated values - uses indirect result
            var pInvokeParams = new List<string> { "SwiftIndirectResult result" };
            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, name, typeSpec) = parameters[i];
                var pInvokeType = GetPInvokeType(typeSpec, typeDatabase);
                pInvokeParams.Add($"{pInvokeType} {name}");
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
                csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"private static extern void {pInvokeName}({string.Join(", ", pInvokeParams)});");
                csWriter.WriteLine();
            }
            return true;
        }

        /// <summary>
        /// Gets the C# type name for an enum case associated value type.
        /// </summary>
        private static string GetCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name) &&
                TryGetGenericTypeParameterName(genericParamType.Name, out var typeParameterName))
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
            if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                // Create a temporary property to use the BoundGenericsHandler
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = typeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use a recursive translator that handles bound generics for each element
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler));
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
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

            // Handle existential types - pass the container directly (it's a blittable struct)
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
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
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // For types that have payloads (non-frozen structs, classes), access the Payload.DangerousGetHandle()
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
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
                return "IntPtr";
            }

            // For types that require memory management, use IntPtr in P/Invoke
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return "IntPtr";
            }

            // For primitives and frozen structs, use the C# type directly
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        private static bool TryGetGenericTypeParameterName(string swiftTypeName, out string typeParameterName)
        {
            typeParameterName = string.Empty;
            if (string.IsNullOrWhiteSpace(swiftTypeName))
                return false;

            if (swiftTypeName.StartsWith("τ_"))
            {
                var parts = swiftTypeName.Split('_');
                if (parts.Length > 0 && int.TryParse(parts[^1], out var index))
                {
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
