// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Returns true when the payload type is backed by an ISwiftObject wrapper whose
        /// NewFromPayload consumes a VALUE BUFFER (SwiftSafeHandle&lt;T&gt;) — non-frozen structs,
        /// non-simple enums, ClassWithBufferStruct. For these, the enum-payload extraction path
        /// must heap-allocate + InitializeWithCopy before handing the pointer to NewFromPayload
        /// (SafeHandle Free()s on dispose; a stackalloc address would corrupt the heap).
        /// Excludes:
        /// - Blittable primitives (MarshalFromSwift uses Unsafe.Read, no ownership transfer)
        /// - ObjC bridged/bridgeable types (handle is managed externally via Arc)
        /// - Pure C# struct projections like Swift.CGSize / known Apple value types
        ///   (Frozen + !RequiresMemoryManagement → not ISwiftObject)
        /// - Swift.AnyType (GetTypeMetadata throws; opaque payload uses direct-pointer path)
        /// - Concrete Swift classes (TypeRecordKind.Class): the payload bytes ARE a class
        ///   pointer, not a value buffer. Handled separately by EmitClassPayloadDerefWithOffset
        ///   / EmitClassPayloadDeref before reaching this predicate (SwiftClassHandle wants the
        ///   class pointer directly; wrapping the buffer address would ARC-release a bogus ptr).
        /// </summary>
        private static bool IsSwiftObjectBackedPayload(TypeSpec typeSpec, TypeRecord record, string csharpType)
        {
            if (MarshallingHelpers.IsObjCBridgeable(record)) return false;
            if (MarshallingHelpers.IsObjCBridged(record)) return false;
            if (WitnessDispatchEmitter.IsBlittablePrimitive(csharpType)) return false;
            if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsKnownAppleValueType(named)) return false;
            if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName) return false;
            if (record.Kind == TypeRecordKind.Class) return false;
            // Pure C# struct projection (frozen layout, no SafeHandle wrapper).
            if (MarshallingHelpers.IsTypeFrozen(record) && !MarshallingHelpers.RequiresMemoryManagement(record)
                && record.Kind == TypeRecordKind.Struct) return false;
            return true;
        }

        /// <summary>
        /// Returns true when the payload type is a concrete Swift class whose NewFromPayload
        /// expects a raw class pointer (not a value buffer). Native ObjC classes take the same
        /// class-pointer shape but are handled by the IsObjCBridged branch above.
        /// </summary>
        private static bool IsSwiftClassPayload(TypeRecord record)
        {
            if (record.Kind != TypeRecordKind.Class) return false;
            if (MarshallingHelpers.IsObjCBridgeable(record)) return false;
            if (MarshallingHelpers.IsObjCBridged(record)) return false;
            return true;
        }

        /// <summary>
        /// Emits a cached tuple metadata accessor for a specific enum case.
        /// This generates the tuple type metadata once and caches it for efficiency.
        /// </summary>
        private void EmitTupleMetadataAccessor(CSharpWriter csWriter, string capitalizedCaseName, List<(string type, string publicType, string name, TypeSpec typeSpec)> parameters, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
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
                EmitGetTypeMetadataForElement(csWriter, typeSpec, i, typeDatabase, genericParams, moduleDecl);
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
        private void EmitGetTypeMetadataForElement(CSharpWriter csWriter, TypeSpec typeSpec, int index, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
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
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);

            // Apple framework ABI JSON encodes generic-parameter tuple elements with the
            // SUGARED declarator name (e.g. "SignedType"). GetCSharpTypeNameForEnumCase
            // gates its generic-param resolution on TypeSpecHelpers.IsGenericTypeParameter
            // and falls through to AnyType for the sugared form. Resolve here so the
            // SwiftObjectHelper<T> fallback below emits SwiftObjectHelper<TSignedType>
            // instead of SwiftObjectHelper<global::Swift.AnyType>.
            bool isBareGenericParam = false;
            if (typeSpec is NamedTypeSpec maybeBareParam
                && TryGetGenericTypeParameterName(maybeBareParam.Name, out var resolvedMetadataName, genericParams))
            {
                csharpType = resolvedMetadataName;
                isBareGenericParam = true;
            }

            // Bare generic parameter: the enclosing type's where-clause may not seed
            // ISwiftObject (relaxed for blittable instantiations like
            // VerificationResult<TSignedType> / MeshBuffer<Vector3>). Use the
            // unconstrained metadata accessor — it works for ISwiftObject T and for
            // primitives/SIMD/blittable T alike.
            if (isBareGenericParam)
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
                return;
            }

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
        private void EmitPayloadMarshalWithOffset(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, string offsetVar, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Bare generic type parameter (e.g. VerificationResult<T>.verified(T)): marshal via
            // the C# type parameter name. Mirrors the factory direction (CaseConstruction.cs)
            // which stackallocs by TypeMetadata<T>.Size + MarshalToSwift<T>. Class T needs a
            // dereference: the payload bytes ARE a class pointer, but NewFromPayload for a true
            // Swift class expects the class reference directly (not a pointer to memory holding it).
            // TryGetGenericTypeParameterName resolves both source-compiled forms (τ_X_Y, T) and
            // the Apple ABI sugared form ("SignedType") via its genericParams lookup, so it is
            // the precise decider — no redundant IsGenericTypeParameter pre-gate.
            if (typeSpec is NamedTypeSpec genericParamOffset
                && TryGetGenericTypeParameterName(genericParamOffset.Name, out var csParamNameOffset, genericParams))
            {
                EmitGenericTypeParameterPayloadExtraction(csWriter, csParamNameOffset, varName,
                    sourcePtrExpr: $"{sourcePtr} + (int){offsetVar}", declareVar: false);
                return;
            }

            // Get the C# type name
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wktOffset))
                    {
                        // Well-known protocol (Swift.Error): `any Error` is a single boxed reference,
                        // so read only the 8-byte box pointer into Payload0 rather than over-reading a
                        // full ExistentialContainer1 off the enum-copy buffer (which is sized to the
                        // enum's metadata, not the container). Owned extraction: the enum copy was taken
                        // at +1 (InitializeWithCopy), so the self-owning wrapper adopts and releases it
                        // (AnyError → ownsContainer: true).
                        csWriter.WriteLine($"var _{varName}_raw = new {containerType} {{ Payload0 = *(IntPtr*)({sourcePtr} + (int){offsetVar}) }};");
                        csWriter.WriteLine($"{varName} = new {wktOffset}(_{varName}_raw{ExistentialHandler.WellKnownOwnedTransferArg(wktOffset)});");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy.
                        // Owned extraction: the enum copy was taken at +1 (InitializeWithCopy) into a
                        // buffer that is never value-witness-destroyed, so the proxy adopts the
                        // existential's +1 and releases it via the container's metadata on Dispose/finalize.
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        var ownsProxyArg = ExistentialHandler.IsOwnedExistentialContainerType(containerType) ? ", ownsContainer: true" : string.Empty;
                        // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
                        // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque
                        // container (40 bytes); reading the wider type over-reads past the allocation.
                        var rawRead = existentialHandler.IsClassBoundArity1Existential(protocolList)
                            ? $"Swift.Runtime.ClassExistentialContainer1.ReadHeapCell(new IntPtr({sourcePtr} + (int){offsetVar}))"
                            : $"SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}))";
                        csWriter.WriteLine($"var _{varName}_raw = {rawRead};");
                        csWriter.WriteLine($"{varName} = new {proxyClassName}(_{varName}_raw{ownsProxyArg});");
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
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);
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

            // Concrete Swift classes: payload bytes ARE a class pointer, not a value buffer.
            // Deref and Arc.Retain for +1 C# ownership; the enumCopy's own retain dissolves with
            // the stack frame (mirrors SwiftResult.ExtractPayloadValue). Wrapping the buffer
            // address via SwiftClassHandle would ARC-release a bogus pointer on dispose.
            var fallbackRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            if (IsSwiftClassPayload(fallbackRecord))
            {
                var bareNameClassOffset = NameProvider.StripVerbatimPrefix(varName);
                csWriter.WriteLine($"var _{bareNameClassOffset}_classPtr = *(IntPtr*)({sourcePtr} + (int){offsetVar});");
                csWriter.WriteLine($"Swift.Runtime.Arc.Retain(_{bareNameClassOffset}_classPtr);");
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(_{bareNameClassOffset}_classPtr);");
                return;
            }

            // Generated ISwiftObject wrappers (nested enums, non-frozen structs) take ownership of
            // the pointer passed to NewFromPayload — the SafeHandle would Free() the stackalloc
            // enumCopy address on dispose. Heap-alloc + InitializeWithCopy first (mirrors
            // SwiftResult.ExtractPayloadValue). AnyType fallback catches nested-on-generic-outer
            // types whose TypeRecord isn't in the database but which we generate as ISwiftObject
            // wrappers. Blittable primitives and ObjC-bridged classes keep the source-pointer
            // path (MarshalFromSwift uses Unsafe.Read or ObjC fast paths with no ownership
            // transfer; SwiftObjectHelper<T> also rejects non-ISwiftObject types at compile time).
            if (IsSwiftObjectBackedPayload(typeSpec, fallbackRecord, csharpType))
            {
                var bareNameOffset = NameProvider.StripVerbatimPrefix(varName);
                csWriter.WriteLine($"var _{bareNameOffset}_meta = SwiftObjectHelper<{csharpType}>.GetTypeMetadata();");
                csWriter.WriteLine($"var _{bareNameOffset}_heap = (byte*)NativeMemory.Alloc(_{bareNameOffset}_meta.Size);");
                csWriter.WriteLine($"_{bareNameOffset}_meta.ValueWitnessTable->InitializeWithCopy(_{bareNameOffset}_heap, {sourcePtr} + (int){offsetVar}, _{bareNameOffset}_meta);");
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(_{bareNameOffset}_heap));");
            }
            else
            {
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
            }
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory to a C# variable (with assignment).
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshal(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Bare generic type parameter (e.g. VerificationResult<T>.verified(T)): marshal via
            // the C# type parameter name. See EmitPayloadMarshalWithOffset for the dispatch
            // rationale and the rationale for relying solely on TryGetGenericTypeParameterName.
            if (typeSpec is NamedTypeSpec genericParamMarshal
                && TryGetGenericTypeParameterName(genericParamMarshal.Name, out var csParamNameMarshal, genericParams))
            {
                EmitGenericTypeParameterPayloadExtraction(csWriter, csParamNameMarshal, varName,
                    sourcePtrExpr: sourcePtr, declareVar: false);
                return;
            }

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wktMarshal))
                    {
                        // Well-known protocol (Swift.Error): `any Error` is a single boxed reference,
                        // so read only the 8-byte box pointer into Payload0 rather than over-reading a
                        // full ExistentialContainer1 off the enum-copy buffer (which is sized to the
                        // enum's metadata, not the container). Owned extraction: the enum copy was taken
                        // at +1 (InitializeWithCopy), so the self-owning wrapper adopts and releases it
                        // (AnyError → ownsContainer: true).
                        csWriter.WriteLine($"var _{varName}_raw = new {containerType} {{ Payload0 = *(IntPtr*)({sourcePtr}) }};");
                        csWriter.WriteLine($"{varName} = new {wktMarshal}(_{varName}_raw{ExistentialHandler.WellKnownOwnedTransferArg(wktMarshal)});");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy.
                        // Owned extraction: the enum copy was taken at +1 (InitializeWithCopy) into a
                        // buffer that is never value-witness-destroyed, so the proxy adopts the
                        // existential's +1 and releases it via the container's metadata on Dispose/finalize.
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        var ownsProxyArg = ExistentialHandler.IsOwnedExistentialContainerType(containerType) ? ", ownsContainer: true" : string.Empty;
                        // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
                        // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque
                        // container (40 bytes); reading the wider type over-reads past the allocation.
                        var rawRead = existentialHandler.IsClassBoundArity1Existential(protocolList)
                            ? $"Swift.Runtime.ClassExistentialContainer1.ReadHeapCell(new IntPtr({sourcePtr}))"
                            : $"SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}))";
                        csWriter.WriteLine($"var _{varName}_raw = {rawRead};");
                        csWriter.WriteLine($"{varName} = new {proxyClassName}(_{varName}_raw{ownsProxyArg});");
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
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);
                var internalType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);
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
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);

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

            // See EmitPayloadMarshalWithOffset for rationale.
            var marshalRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            if (IsSwiftClassPayload(marshalRecord))
            {
                var bareNameClassMarshal = NameProvider.StripVerbatimPrefix(varName);
                csWriter.WriteLine($"var _{bareNameClassMarshal}_classPtr = *(IntPtr*)({sourcePtr});");
                csWriter.WriteLine($"Swift.Runtime.Arc.Retain(_{bareNameClassMarshal}_classPtr);");
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(_{bareNameClassMarshal}_classPtr);");
                return;
            }
            if (IsSwiftObjectBackedPayload(typeSpec, marshalRecord, csharpType))
            {
                var bareNameMarshal = NameProvider.StripVerbatimPrefix(varName);
                csWriter.WriteLine($"var _{bareNameMarshal}_meta = SwiftObjectHelper<{csharpType}>.GetTypeMetadata();");
                csWriter.WriteLine($"var _{bareNameMarshal}_heap = (byte*)NativeMemory.Alloc(_{bareNameMarshal}_meta.Size);");
                csWriter.WriteLine($"_{bareNameMarshal}_meta.ValueWitnessTable->InitializeWithCopy(_{bareNameMarshal}_heap, {sourcePtr}, _{bareNameMarshal}_meta);");
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(_{bareNameMarshal}_heap));");
            }
            else
            {
                csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
            }
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory with a variable declaration.
        /// For existentials with known proxies, marshals to a temp container then wraps in the proxy class.
        /// </summary>
        private void EmitPayloadMarshalWithDeclaration(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase, IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Bare generic type parameter (e.g. VerificationResult<T>.verified(T)): marshal via
            // the C# type parameter name. See EmitPayloadMarshalWithOffset for the dispatch
            // rationale and the rationale for relying solely on TryGetGenericTypeParameterName.
            if (typeSpec is NamedTypeSpec genericParamDecl
                && TryGetGenericTypeParameterName(genericParamDecl.Name, out var csParamNameDecl, genericParams))
            {
                EmitGenericTypeParameterPayloadExtraction(csWriter, csParamNameDecl, varName,
                    sourcePtrExpr: sourcePtr, declareVar: true);
                return;
            }

            // Get the C# type name for this typeSpec
            string csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);

            // For bound generics, check if public type differs (needs conversion after marshal)
            if (typeSpec is NamedTypeSpec namedDecl && namedDecl.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedDecl))
            {
                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);
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
                        // Well-known protocol (Swift.Error): `any Error` is a single boxed reference,
                        // so read only the 8-byte box pointer into Payload0 rather than over-reading a
                        // full ExistentialContainer1 off the enum-copy buffer (which is sized to the
                        // enum's metadata, not the container). Owned extraction: the enum copy was taken
                        // at +1 (InitializeWithCopy), so the self-owning wrapper adopts and releases it
                        // (AnyError → ownsContainer: true).
                        csWriter.WriteLine($"var _{varName}_raw = new {containerType} {{ Payload0 = *(IntPtr*)({sourcePtr}) }};");
                        csWriter.WriteLine($"var {varName} = new {wktDecl}(_{varName}_raw{ExistentialHandler.WellKnownOwnedTransferArg(wktDecl)});");
                    }
                    else if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                    {
                        // Known proxy: marshal to temp container, then wrap in proxy.
                        // Owned extraction: the enum copy was taken at +1 (InitializeWithCopy) into a
                        // buffer that is never value-witness-destroyed, so the proxy adopts the
                        // existential's +1 and releases it via the container's metadata on Dispose/finalize.
                        var proxyClassName = existentialHandler.GetProxyClassName(protocolList);
                        var ownsProxyArg = ExistentialHandler.IsOwnedExistentialContainerType(containerType) ? ", ownsContainer: true" : string.Empty;
                        // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
                        // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque
                        // container (40 bytes); reading the wider type over-reads past the allocation.
                        var rawRead = existentialHandler.IsClassBoundArity1Existential(protocolList)
                            ? $"Swift.Runtime.ClassExistentialContainer1.ReadHeapCell(new IntPtr({sourcePtr}))"
                            : $"SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}))";
                        csWriter.WriteLine($"var _{varName}_raw = {rawRead};");
                        csWriter.WriteLine($"var {varName} = new {proxyClassName}(_{varName}_raw{ownsProxyArg});");
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
        /// Emits the runtime dispatch for extracting a bare-generic-type-parameter payload from
        /// a stackalloc enum buffer. Mirrors the concrete-type patterns:
        ///
        /// True Swift classes (metadata Kind == Class, !IsValueType, !ISwiftStruct) — payload
        /// bytes ARE a class pointer at offset 0. Dereference, then <see cref="Arc.Retain"/>
        /// for the +1 ownership <c>SwiftClassHandle</c> expects. Without the explicit retain,
        /// the eventual <c>Arc.Release</c> at dispose time underflows the heap object's
        /// refcount; the previous emit shape would also have produced this defect.
        ///
        /// Non-class generic T (ISwiftObject non-class, ISwiftStruct, primitives, value
        /// structs) — heap-allocate a buffer, <c>InitializeWithCopy</c> from the source
        /// (stack) pointer, hand the heap pointer to <c>MarshalFromSwift</c>. For ISwiftObject
        /// T, the produced wrapper takes ownership of the heap buffer (its SafeHandle's
        /// ReleaseHandle frees + destroys it). For non-ISwiftObject T (primitive, plain
        /// value struct), MarshalFromSwift reads the value out and we own the heap — Destroy
        /// then Free. The CRITICAL invariant: never hand the stack buffer pointer directly
        /// to MarshalFromSwift, because <c>SwiftSafeHandle.ReleaseHandle</c> would call
        /// <c>NativeMemory.Free</c> on a non-heap pointer.
        /// </summary>
        private static void EmitGenericTypeParameterPayloadExtraction(CSharpWriter csWriter, string typeParamName, string varName, string sourcePtrExpr, bool declareVar)
        {
            if (declareVar)
            {
                csWriter.WriteLine($"{typeParamName} {varName};");
            }
            // Hoist metadata so both branches share it without recomputation.
            csWriter.WriteLine($"var __{varName}_meta = global::Swift.Runtime.TypeMetadata.GetTypeMetadataOrThrow<{typeParamName}>();");
            csWriter.WriteLine($"if (typeof(global::Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof({typeParamName}))");
            csWriter.WriteLine($"    && !typeof({typeParamName}).IsValueType");
            csWriter.WriteLine($"    && !typeof(global::Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({typeParamName}))");
            csWriter.WriteLine($"    && __{varName}_meta.Kind == global::Swift.Runtime.TypeMetadataKind.Class)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            // Class T: read the class pointer at sourcePtr and Arc.Retain for SwiftClassHandle's +1
            // ownership. Mirrors the concrete IsSwiftClassPayload branch (EmitPayloadMarshal /
            // EmitPayloadMarshalWithOffset) and SwiftResult.ExtractPayloadValue.
            csWriter.WriteLine($"var __{varName}_classPtr = *(IntPtr*)({sourcePtrExpr});");
            csWriter.WriteLine($"global::Swift.Runtime.Arc.Retain(__{varName}_classPtr);");
            csWriter.WriteLine($"{varName} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{typeParamName}>(__{varName}_classPtr);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("else");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            // Non-class T: heap-allocate, InitializeWithCopy from the stack source. NewFromPayload
            // for an ISwiftObject T takes ownership of the heap pointer (SafeHandle frees on
            // dispose). For primitives / non-ISwiftObject value types, MarshalFromSwift reads the
            // value by value and we Destroy + Free the heap ourselves.
            csWriter.WriteLine($"void* __{varName}_heap = global::System.Runtime.InteropServices.NativeMemory.Alloc(__{varName}_meta.Size);");
            csWriter.WriteLine($"__{varName}_meta.ValueWitnessTable->InitializeWithCopy(__{varName}_heap, (void*)({sourcePtrExpr}), __{varName}_meta);");
            csWriter.WriteLine($"{varName} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{typeParamName}>(new IntPtr(__{varName}_heap));");
            csWriter.WriteLine($"if (!typeof(global::Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof({typeParamName})))");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"__{varName}_meta.ValueWitnessTable->Destroy(__{varName}_heap, __{varName}_meta);");
            csWriter.WriteLine($"global::System.Runtime.InteropServices.NativeMemory.Free(__{varName}_heap);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
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
