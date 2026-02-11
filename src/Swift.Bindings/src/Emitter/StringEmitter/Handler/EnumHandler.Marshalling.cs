// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Emits a cached tuple metadata accessor for a specific enum case.
        /// This generates the tuple type metadata once and caches it for efficiency.
        /// </summary>
        private void EmitTupleMetadataAccessor(CSharpWriter csWriter, string capitalizedCaseName, List<(string type, string publicType, string name, TypeSpec typeSpec)> parameters, ITypeDatabase typeDatabase)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Emit the static cached field
            csWriter.WriteLine($"private static unsafe TupleTypeMetadata* _tupleMetadata_{capitalizedCaseName};");
            csWriter.WriteLine();

            // Emit the accessor method
            csWriter.WriteLine($"private static unsafe TupleTypeMetadata* GetTupleMetadata_{capitalizedCaseName}()");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine($"if (_tupleMetadata_{capitalizedCaseName} != null)");
            csWriter.Indent++;
            csWriter.WriteLine($"return _tupleMetadata_{capitalizedCaseName};");
            csWriter.Indent--;
            csWriter.WriteLine();

            // Get element type metadata - build an array of TypeMetadata
            csWriter.WriteLine("// Build tuple metadata from element types");
            csWriter.WriteLine($"var elementMetadataArray = new TypeMetadata[{parameters.Count}];");

            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, _, _, typeSpec) = parameters[i];
                EmitGetTypeMetadataForElement(csWriter, typeSpec, i, typeDatabase);
            }
            csWriter.WriteLine();

            // Use TypeMetadata.GetTupleTypeMetadataFromElements
            csWriter.WriteLine("// Get tuple type metadata from Swift runtime");
            csWriter.WriteLine("var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);");
            csWriter.WriteLine();

            csWriter.WriteLine($"_tupleMetadata_{capitalizedCaseName} = tupleMetadata.AsTupleMetadata();");
            csWriter.WriteLine($"return _tupleMetadata_{capitalizedCaseName};");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits code to get the TypeMetadata for a tuple element type.
        /// Stores the result in elementMetadataArray[index].
        /// </summary>
        private void EmitGetTypeMetadataForElement(CSharpWriter csWriter, TypeSpec typeSpec, int index, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var protocolCount = protocolList.Protocols.Count;
                    csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetExistentialTypeMetadata({protocolCount});");
                    return;
                }
            }

            // For types that implement ISwiftObject, use their static metadata accessor
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Check if it's a primitive type with known metadata
            if (IsPrimitiveTypeWithKnownMetadata(csharpType))
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
            }
            else
            {
                // Assume the type implements ISwiftObject
                csWriter.WriteLine($"elementMetadataArray[{index}] = SwiftObjectHelper<{csharpType}>.GetTypeMetadata();");
            }
        }

        /// <summary>
        /// Checks if a C# type name corresponds to a primitive type with known metadata.
        /// </summary>
        private static bool IsPrimitiveTypeWithKnownMetadata(string csharpType)
        {
            return csharpType switch
            {
                "bool" or "System.Boolean" => true,
                "sbyte" or "System.SByte" => true,
                "byte" or "System.Byte" => true,
                "short" or "System.Int16" => true,
                "ushort" or "System.UInt16" => true,
                "int" or "System.Int32" => true,
                "uint" or "System.UInt32" => true,
                "long" or "System.Int64" => true,
                "ulong" or "System.UInt64" => true,
                "nint" or "System.IntPtr" => true,
                "nuint" or "System.UIntPtr" => true,
                "float" or "System.Single" => true,
                "double" or "System.Double" => true,
                _ => false
            };
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory at a specific offset.
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshalWithOffset(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, string offsetVar, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (AllProtocolsHaveTypeRecords(protocolList, typeDatabase))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                        csWriter.WriteLine($"{varName} = new {proxyClassName}(_{varName}_raw);");
                    }
                    else
                    {
                        csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                    }
                    return;
                }
            }

            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory to a C# variable (with assignment).
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshal(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (AllProtocolsHaveTypeRecords(protocolList, typeDatabase))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        csWriter.WriteLine($"{varName} = new {proxyClassName}(_{varName}_raw);");
                    }
                    else
                    {
                        csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                    }
                    return;
                }
            }

            // Use GetCSharpTypeNameForEnumCase to properly handle bound generics
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);
            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory with a variable declaration.
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshalWithDeclaration(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name for this typeSpec
            string csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (AllProtocolsHaveTypeRecords(protocolList, typeDatabase))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        csWriter.WriteLine($"var {varName} = new {proxyClassName}(_{varName}_raw);");
                    }
                    else
                    {
                        csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                    }
                    return;
                }
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // For types requiring memory management (classes, non-frozen structs), use SwiftMarshal
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
            }
            else
            {
                // For primitives and frozen structs, marshal directly
                csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
            }
        }
    }
}
