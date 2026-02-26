// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Emits a cached tuple metadata accessor for a specific enum case.
        /// This generates the tuple type metadata once and caches it for efficiency.
        /// </summary>
        private void EmitTupleMetadataAccessor(CSharpWriter csWriter, string capitalizedCaseName, List<(string type, string publicType, string name, TypeSpec typeSpec)> parameters, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null)
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
                EmitGetTypeMetadataForElement(csWriter, typeSpec, i, typeDatabase, genericParams);
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
        private void EmitGetTypeMetadataForElement(CSharpWriter csWriter, TypeSpec typeSpec, int index, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null)
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
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);

            // Check if it's a primitive type with known metadata
            if (IsPrimitiveTypeWithKnownMetadata(csharpType))
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
            }
            // Simple enums (NS_ENUM) are integer-backed and don't implement ISwiftObject.
            // Use the Swift ABI backing type's metadata (e.g., nint for Int, byte for UInt8).
            else if (TryGetSimpleEnumMetadataType(typeSpec, typeDatabase, out var metadataType))
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{metadataType}>();");
            }
            // Apple framework value types (remapped structs/enums like UIViewAnimationOptions)
            // are .NET value types that don't implement ISwiftObject — use GetTypeMetadataOrThrow.
            else if (typeSpec is NamedTypeSpec appleNamedSpec && TypeDatabaseExtensions.IsRemappedAppleValueType(appleNamedSpec))
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
                // C3b: Foundation types mapped to System.* that don't implement ISwiftObject
                "System.DateTimeOffset" => true,
                "System.Guid" => true,
                _ => false
            };
        }

        /// <summary>
        /// Checks if a TypeSpec corresponds to a simple enum in the type database and returns
        /// the C# type to use for Swift metadata lookup. Simple enums are integer-backed and
        /// don't implement ISwiftObject, so they can't use SwiftObjectHelper&lt;T&gt;.GetTypeMetadata().
        /// The metadata type must match the Swift ABI size (e.g., Swift.Int → nint, not int).
        /// </summary>
        private static bool TryGetSimpleEnumMetadataType(TypeSpec typeSpec, ITypeDatabase typeDatabase, [NotNullWhen(true)] out string? metadataType)
        {
            metadataType = null;
            if (typeSpec is not NamedTypeSpec namedSpec)
                return false;
            if (!typeDatabase.TryGetTypeRecord(namedSpec, out var record))
                return false;
            if (record.Kind != TypeRecordKind.Enum || (record.Flags & TypeRecordFlags.SimpleEnum) == 0)
                return false;
            metadataType = GetSwiftAbiMetadataType(record.RawValueTypeName);
            return true;
        }

        /// <summary>
        /// Maps a Swift raw value type name to the C# type whose metadata matches the Swift ABI layout.
        /// Unlike GetCSharpEnumUnderlyingType (which maps Int→int for C# enum declarations),
        /// this preserves pointer-sized semantics: Swift.Int → nint, Swift.UInt → nuint.
        /// This is critical for tuple metadata construction where element sizes must match the Swift ABI.
        /// </summary>
        internal static string GetSwiftAbiMetadataType(string? rawValueTypeName)
        {
            return rawValueTypeName switch
            {
                "Int" => "nint",     // Swift.Int is pointer-sized
                "UInt" => "nuint",   // Swift.UInt is pointer-sized
                "Int8" => "sbyte",
                "UInt8" => "byte",
                "Int16" => "short",
                "UInt16" => "ushort",
                "Int32" => "int",
                "UInt32" => "uint",
                "Int64" => "long",
                "UInt64" => "ulong",
                // Null/unknown: NS_ENUM convention is NSInteger (pointer-sized)
                null or "" => "nint",
                _ => "nint"
            };
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory at a specific offset.
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshalWithOffset(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, string offsetVar, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wktOffset))
                    {
                        // Well-known protocol: marshal container, then wrap in runtime type (e.g., AnyError)
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                        csWriter.WriteLine($"{varName} = new {wktOffset}(_{varName}_raw);");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
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

            // Foundation.Data → byte[]: marshal as Swift.Data, then convert
            if (typeSpec is NamedTypeSpec dataOffset && dataOffset.Name == "Foundation.Data")
            {
                csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<Swift.Data>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                csWriter.WriteLine($"{varName} = _{varName}_raw.ToByteArray();");
                return;
            }

            // For bound generics, check if public type differs (needs conversion after marshal).
            // Skip closures — delegate* can't be used as generic type arguments in MarshalFromSwift<T>.
            if (typeSpec is NamedTypeSpec namedOffset && namedOffset.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedOffset))
            {
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
                if (publicType != csharpType)
                {
                    var genericContext = genericParams != null
                        ? BuildGenericContextFromEnumParams(genericParams)
                        : GenericContext.Empty;
                    var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = false, GenericContext = genericContext
                    });
                    if (projection != null)
                    {
                        var containerType = projection.ContainerTypeName;
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                        var containerConv = projection.GetReturnContainerConversion($"_{varName}_raw");
                        var elemConv = projection.GetReturnElementConversion($"_{varName}_raw");
                        if (containerConv != null)
                            csWriter.WriteLine($"{varName} = {containerConv};");
                        else if (elemConv != null)
                            csWriter.WriteLine($"{varName} = {elemConv};");
                        else
                            csWriter.WriteLine($"{varName} = _{varName}_raw;");
                        return;
                    }
                }
            }

            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory to a C# variable (with assignment).
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshal(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null)
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
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wktMarshal))
                    {
                        // Well-known protocol: marshal container, then wrap in runtime type (e.g., AnyError)
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        csWriter.WriteLine($"{varName} = new {wktMarshal}(_{varName}_raw);");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
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

            // For bound generics, check if public type differs (needs conversion after marshal)
            if (typeSpec is NamedTypeSpec namedMarshal && namedMarshal.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedMarshal))
            {
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
                var internalType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
                if (publicType != internalType)
                {
                    var genericContext = genericParams != null
                        ? BuildGenericContextFromEnumParams(genericParams)
                        : GenericContext.Empty;
                    var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = false, GenericContext = genericContext
                    });
                    if (projection != null)
                    {
                        var containerType = projection.ContainerTypeName;
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        var containerConv = projection.GetReturnContainerConversion($"_{varName}_raw");
                        var elemConv = projection.GetReturnElementConversion($"_{varName}_raw");
                        if (containerConv != null)
                            csWriter.WriteLine($"{varName} = {containerConv};");
                        else if (elemConv != null)
                            csWriter.WriteLine($"{varName} = {elemConv};");
                        else
                            csWriter.WriteLine($"{varName} = _{varName}_raw;");
                        return;
                    }
                }
            }

            // Use GetCSharpTypeNameForEnumCase to properly handle bound generics
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory with a variable declaration.
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshalWithDeclaration(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name for this typeSpec
            string csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);

            // For bound generics, check if public type differs (needs conversion after marshal)
            if (typeSpec is NamedTypeSpec namedDecl && namedDecl.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedDecl))
            {
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);
                if (publicType != csharpType)
                {
                    var genericContext = genericParams != null
                        ? BuildGenericContextFromEnumParams(genericParams)
                        : GenericContext.Empty;
                    var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = false, GenericContext = genericContext
                    });
                    if (projection != null)
                    {
                        var containerType = projection.ContainerTypeName;
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        var containerConv = projection.GetReturnContainerConversion($"_{varName}_raw");
                        var elemConv = projection.GetReturnElementConversion($"_{varName}_raw");
                        if (containerConv != null)
                            csWriter.WriteLine($"var {varName} = {containerConv};");
                        else if (elemConv != null)
                            csWriter.WriteLine($"var {varName} = {elemConv};");
                        else
                            csWriter.WriteLine($"var {varName} = _{varName}_raw;");
                        return;
                    }
                }
            }

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wktDecl))
                    {
                        // Well-known protocol: marshal container, then wrap in runtime type (e.g., AnyError)
                        csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                        csWriter.WriteLine($"var {varName} = new {wktDecl}(_{varName}_raw);");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
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
