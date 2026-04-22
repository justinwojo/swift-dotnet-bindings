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
            // Apple framework value types (structs/enums like UIViewAnimationOptions)
            // are .NET value types that don't implement ISwiftObject — use GetTypeMetadataOrThrow.
            else if (typeSpec is NamedTypeSpec appleNamedSpec && TypeDatabaseExtensions.IsKnownAppleValueType(appleNamedSpec))
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
            }
            // Frozen structs from framework databases (e.g., CGPoint, UIEdgeInsets) are plain C# value types
            // that don't implement ISwiftObject — SwiftObjectHelper<T> requires ISwiftObject constraint.
            // Check the type database: if it's a frozen struct that isn't projected as a class (no .Buffer),
            // use GetTypeMetadataOrThrow<T>() which works with any blittable value type.
            else if (typeSpec is NamedTypeSpec frozenSpec && frozenSpec.HasModule() &&
                     typeDatabase.TryGetTypeRecord(frozenSpec, out var frozenRecord) &&
                     frozenRecord.Kind == TypeRecordKind.Struct &&
                     MarshallingHelpers.IsTypeFrozen(frozenRecord) &&
                     !MarshallingHelpers.IsFrozenStructProjectedAsClass(frozenRecord))
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

            // Foundation.Data → byte[]: marshal as Swift.Foundation.Data, then convert
            if (typeSpec is NamedTypeSpec dataOffset && dataOffset.Name == "Foundation.Data")
            {
                csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<Swift.Foundation.Data>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                csWriter.WriteLine($"{varName} = _{varName}_raw.ToByteArray();");
                return;
            }

            // Foundation.Date → DateTimeOffset: marshal as double, then convert
            if (typeSpec is NamedTypeSpec dateOffset && dateOffset.Name == "Foundation.Date")
            {
                csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<double>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                csWriter.WriteLine($"{varName} = {DateProjection.SwiftEpoch}.AddSeconds(_{varName}_raw);");
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

            // ObjC-bridgeable types: payload stores an ObjC object pointer.
            if (typeSpec is NamedTypeSpec objcBridgeOffsetSpec)
            {
                var objcOffsetRecord = typeDatabase.GetTypeRecordOrAnyType(objcBridgeOffsetSpec);
                if (MarshallingHelpers.IsObjCBridgeable(objcOffsetRecord) && objcOffsetRecord.NativeTypeName != null)
                {
                    var nativeType = objcOffsetRecord.NativeTypeName.FullyQualifiedName;
                    csWriter.WriteLine($"{varName} = {MarshallingHelpers.FormatObjCBridgeCall(nativeType, $"*(IntPtr*)({sourcePtr} + (int){offsetVar})", nonNull: true)};");
                    return;
                }
            }

            // Simple enum associated values: read discriminator with correct width.
            if (typeDatabase.TryGetTypeRecord(typeSpec, out var offsetEnumRecord) &&
                offsetEnumRecord.Kind == TypeRecordKind.Enum &&
                offsetEnumRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var readExpr = GetSimpleEnumReadExpressionWithOffset(offsetEnumRecord, sourcePtr, offsetVar);
                csWriter.WriteLine($"{varName} = ({csharpType}){readExpr};");
                return;
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
                        // ObjC-bridged container payloads (e.g., [URL]): Swift stores an NSArray
                        // pointer in the enum payload, NOT a SwiftArray<T> structure. Reading the
                        // pointer directly matches the ObjCBridgeable branch below and feeds into
                        // GetReturnContainerConversion, which calls NSArray.ArrayFromHandle<T>.
                        if (projection.UsesObjCContainerBridge)
                        {
                            var objcContainerConv = projection.GetReturnContainerConversion($"_{varName}_raw");
                            if (objcContainerConv != null)
                            {
                                csWriter.WriteLine($"IntPtr _{varName}_raw = *(IntPtr*){sourcePtr};");
                                csWriter.WriteLine($"{varName} = {objcContainerConv};");
                                return;
                            }
                        }

                        // Optional<ObjCContainer> enum payload (e.g., [URL]?, [String: URL]?, Set<URL>?):
                        // Swift nil-pointer-optimizes this to a single IntPtr — IntPtr.Zero = nil,
                        // non-zero = ObjC collection handle. Without this branch, the fallback tries
                        // MarshalFromSwift<SwiftOptional<SwiftArray<IntPtr>>>, which reads the wrong
                        // ABI. Mirrors OptionalProjection.GetReturnPlan for IndirectResult (see
                        // OptionalProjection.cs:274-293). Gated on concrete container projections
                        // so bare Optional<ObjCBridgeable> (e.g. URL?) still flows through the
                        // existing ObjCBridgeable enum-payload path below.
                        if (projection is OptionalProjection optProj
                            && optProj.InnerProjection is ArrayProjection or DictionaryProjection or SetProjection
                            && optProj.InnerProjection.UsesObjCContainerBridge)
                        {
                            var innerContainerConv = optProj.InnerProjection.GetReturnContainerConversion($"_{varName}_raw");
                            if (innerContainerConv != null)
                            {
                                var innerPublicType = optProj.InnerProjection.PublicType;
                                csWriter.WriteLine($"IntPtr _{varName}_raw = *(IntPtr*){sourcePtr};");
                                csWriter.WriteLine($"{varName} = _{varName}_raw == IntPtr.Zero ? ({innerPublicType}?)null : {innerContainerConv};");
                                return;
                            }
                        }

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

            // Foundation.Date → DateTimeOffset: marshal as double, then convert
            if (typeSpec is NamedTypeSpec dateMarshal && dateMarshal.Name == "Foundation.Date")
            {
                csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<double>(new IntPtr({sourcePtr}));");
                csWriter.WriteLine($"{varName} = {DateProjection.SwiftEpoch}.AddSeconds(_{varName}_raw);");
                return;
            }

            // ObjC-bridgeable types: payload stores an ObjC object pointer (e.g., NSURL).
            // Read the pointer and wrap using GetNSObject<T>() — same pattern as ObjCBridgeableProjection.
            if (typeSpec is NamedTypeSpec objcBridgeSpec)
            {
                var objcRecord = typeDatabase.GetTypeRecordOrAnyType(objcBridgeSpec);
                if (MarshallingHelpers.IsObjCBridgeable(objcRecord) && objcRecord.NativeTypeName != null)
                {
                    var nativeType = objcRecord.NativeTypeName.FullyQualifiedName;
                    csWriter.WriteLine($"{varName} = {MarshallingHelpers.FormatObjCBridgeCall(nativeType, $"*(IntPtr*){sourcePtr}", nonNull: true)};");
                    return;
                }
            }

            // Use GetCSharpTypeNameForEnumCase to properly handle bound generics
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams);

            // Simple enum associated values: SwiftMarshal.MarshalFromSwift<T> can't handle
            // simple C# enums (enum Foo : int) because they don't have TypeMetadata registered.
            // Read the discriminator directly using the correct width from InlineSize.
            // Swift stores simple enums as the minimum bytes needed for the discriminator
            // (1 byte for ≤256 cases, 2 for ≤65536, etc.), regardless of raw value type.
            if (typeDatabase.TryGetTypeRecord(typeSpec, out var payloadRecord) &&
                payloadRecord.Kind == TypeRecordKind.Enum &&
                payloadRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var readExpr = GetSimpleEnumReadExpression(payloadRecord, sourcePtr);
                csWriter.WriteLine($"{varName} = ({csharpType}){readExpr};");
                return;
            }

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

            // Foundation.Date → DateTimeOffset: marshal as double, then convert
            if (typeSpec is NamedTypeSpec dateDecl && dateDecl.Name == "Foundation.Date")
            {
                csWriter.WriteLine($"var _{varName}_raw = SwiftMarshal.MarshalFromSwift<double>(new IntPtr({sourcePtr}));");
                csWriter.WriteLine($"var {varName} = {DateProjection.SwiftEpoch}.AddSeconds(_{varName}_raw);");
                return;
            }

            // Simple enum associated values: read discriminator with correct width.
            if (typeDatabase.TryGetTypeRecord(typeSpec, out var simpleEnumRecord) &&
                simpleEnumRecord.Kind == TypeRecordKind.Enum &&
                simpleEnumRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var readExpr = GetSimpleEnumReadExpression(simpleEnumRecord, sourcePtr);
                csWriter.WriteLine($"var {varName} = ({csharpType}){readExpr};");
                return;
            }

            // ObjC-bridgeable types: payload stores an ObjC object pointer.
            if (typeSpec is NamedTypeSpec objcBridgeDeclSpec)
            {
                var objcDeclRecord = typeDatabase.GetTypeRecordOrAnyType(objcBridgeDeclSpec);
                if (MarshallingHelpers.IsObjCBridgeable(objcDeclRecord) && objcDeclRecord.NativeTypeName != null)
                {
                    var nativeType = objcDeclRecord.NativeTypeName.FullyQualifiedName;
                    csWriter.WriteLine($"var {varName} = {MarshallingHelpers.FormatObjCBridgeCall(nativeType, $"*(IntPtr*){sourcePtr}", nonNull: true)};");
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
        /// <summary>
        /// Returns a C# expression to read a simple enum's discriminator from a byte pointer,
        /// using the correct width based on InlineSize from the TypeRecord.
        /// Swift stores simple enums as the minimum bytes needed for the discriminator
        /// (1 byte for ≤256 cases, 2 for ≤65536, etc.), regardless of raw value type.
        /// </summary>
        private static string GetSimpleEnumReadExpression(TypeRecord typeRecord, string sourcePtr)
        {
            var size = typeRecord.InlineSize ?? 1; // Default to 1 byte when InlineSize unavailable
            return size switch
            {
                1 => $"(*{sourcePtr})",
                2 => $"(*(short*){sourcePtr})",
                4 => $"(*(int*){sourcePtr})",
                8 => $"(*(long*){sourcePtr})",
                _ => $"(*{sourcePtr})" // Fallback to 1 byte for unusual sizes
            };
        }

        /// <summary>
        /// Returns a C# expression to read a simple enum's discriminator from a byte pointer
        /// at a given offset, using the correct width based on InlineSize.
        /// </summary>
        private static string GetSimpleEnumReadExpressionWithOffset(TypeRecord typeRecord, string sourcePtr, string offsetVar)
        {
            var size = typeRecord.InlineSize ?? 1;
            return size switch
            {
                1 => $"(*({sourcePtr} + (int){offsetVar}))",
                2 => $"(*(short*)({sourcePtr} + (int){offsetVar}))",
                4 => $"(*(int*)({sourcePtr} + (int){offsetVar}))",
                8 => $"(*(long*)({sourcePtr} + (int){offsetVar}))",
                _ => $"(*({sourcePtr} + (int){offsetVar}))"
            };
        }
    }
}
