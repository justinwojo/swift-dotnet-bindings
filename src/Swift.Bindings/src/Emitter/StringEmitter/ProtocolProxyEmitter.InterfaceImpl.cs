// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitInterfaceImplementation(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName, WitnessDispatchEmitter dispatchEmitter)
    {
        writer.WriteLine("#region Interface Implementation");
        writer.WriteLine();

        // Track emitted members to avoid duplicates
        var emittedMembers = new HashSet<string>();

        // Properties (skip static properties - they're not part of the interface)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (emittedMembers.Add($"property:{property.Name}"))
            {
                if (_skippedPropertyNames.Contains(property.Name))
                {
                    // Closure-skipped properties are now in the interface — emit NotSupported stub
                    if (_closureSkippedPropertyNames.Contains(property.Name))
                        EmitNotSupportedPropertyStub(writer, property);
                    continue;
                }
                EmitPropertyImplementation(writer, property, protocolDecl, dispatchEmitter);
            }
        }

        // Subscripts (as indexers) - skip static subscripts
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var key = $"subscript:{subscriptIndex}";
            if (emittedMembers.Add(key))
            {
                // Skip subscripts that the interface skipped due to AnyType generic args
                if (_skippedSubscriptIndices.Contains(subscriptIndex))
                {
                    subscriptIndex++;
                    continue;
                }
                EmitSubscriptImplementation(writer, subscript, protocolDecl, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Collect emitted C# property names for method/property collision detection.
        // Include closure-skipped properties: they ARE emitted in the interface (proxy gets
        // NotSupportedException stubs), so proxy methods must match interface collision renames.
        var emittedCSharpPropertyNames = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            if (!property.IsStatic &&
                (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name)))
                emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
        }

        // Methods - track by signature to handle overloads
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (_skippedMethodKeys.Contains(methodKey))
                {
                    // Closure-skipped methods are now in the interface — emit NotSupported stub
                    if (_closureSkippedMethodKeys.Contains(methodKey))
                    {
                        var projectedKeySkipped = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                        if (!emittedCSharpKeys.Add(projectedKeySkipped))
                            continue;
                        EmitNotSupportedMethodStub(writer, method, "Closure parameters cannot be marshalled in protocol proxy.", emittedCSharpPropertyNames);
                    }
                    continue;
                }
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitMethodImplementation(writer, method, protocolDecl, dispatchEmitter, idx, emittedCSharpPropertyNames);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitPropertyImplementation(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
        var propertyName = NameProvider.GetPropertyName(property.Name);
        var isGetterDispatchable = hasGetter && dispatchEmitter.IsPropertyGetterDispatchable(property);
        var isSetterDispatchable = hasSetter && dispatchEmitter.IsPropertySetterDispatchable(property);
        var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
        var isClassReturnGetter = hasGetter && !isGetterDispatchable && dispatchEmitter.IsPropertyClassReturn(property);
        var isStructReturnGetter = hasGetter && !isGetterDispatchable && !isClassReturnGetter && dispatchEmitter.IsPropertyStructReturn(property);
        var isCollectionReturnGetter = false;

        // Validate that the projected C# property type matches the dispatch strategy.
        // IsPropertyGetterDispatchable checks Swift-side dispatchability, but if the
        // projected type diverges (e.g. Swift.AnyType from incomplete TypeDatabase),
        // the generated return statement would be type-incompatible. Disable dispatch.
        // For blittable types: projected type must be a blittable primitive.
        // For String types: projected type must be SwiftString (not AnyType).
        if (isGetterDispatchable)
        {
            if (isStringProperty)
            {
                if (!IsSwiftStringProjectedType(csharpTypeName))
                    isGetterDispatchable = false;
            }
            else if (dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec) ||
                     dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))
            {
                // Class/struct properties use ClassReturn/StructReturn getter path,
                // not the blittable dispatch path. Force off so they fall through
                // to isClassReturnGetter/isStructReturnGetter (computed above with !isGetterDispatchable).
                isGetterDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isGetterDispatchable = false;
            }
        }
        // Re-evaluate ClassReturn/StructReturn now that class/struct types are forced off the blittable path
        if (!isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter)
        {
            isClassReturnGetter = hasGetter && dispatchEmitter.IsPropertyClassReturn(property);
            isStructReturnGetter = hasGetter && !isClassReturnGetter && dispatchEmitter.IsPropertyStructReturn(property);
        }
        if (isSetterDispatchable)
        {
            if (isStringProperty)
            {
                if (!IsSwiftStringProjectedType(csharpTypeName))
                    isSetterDispatchable = false;
            }
            else if (dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec) ||
                     dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))
            {
                // No setter dispatch for class/struct types yet — defer
                isSetterDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isSetterDispatchable = false;
            }
        }
        // Secondary validation for ClassReturn/StructReturn getters: reject if projected type is AnyType
        if (isClassReturnGetter || isStructReturnGetter)
        {
            if (csharpTypeName == "object" ||
                csharpTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                isClassReturnGetter = false;
                isStructReturnGetter = false;
            }
        }
        // Collection return getter: check after other paths are excluded
        if (hasGetter && !isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter
            && dispatchEmitter.IsPropertyCollectionReturn(property))
        {
            isCollectionReturnGetter = true;
        }

        bool isGetterDispatched = isGetterDispatchable || isClassReturnGetter || isStructReturnGetter || isCollectionReturnGetter;
        bool isAnyAccessorNonDispatchable =
            (hasGetter && !isGetterDispatched) || (hasSetter && !isSetterDispatchable);
        if (isAnyAccessorNonDispatchable)
        {
            var propReason = dispatchEmitter.GetPropertyNonDispatchReason(property);
            var reasonSuffix = propReason != null
                ? $": {propReason}. Throws"
                : " and throws";
            writer.WriteLine($"[Obsolete(\"This member is not dispatchable to Swift{reasonSuffix} NotSupportedException \" +");
            writer.WriteLine("    \"on Swift-backed existential containers (SB0003).\",");
            writer.WriteLine("    DiagnosticId = \"SB0003\",");
            writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
        }

        writer.WriteLine($"public {csharpTypeName} {propertyName}");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasGetter)
        {
            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "get", property.Name, 0);
            var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "get", property.Name, 0);

            if (isGetterDispatchable && isStringProperty)
            {
                // String getter: decode SBW_Utf8Slice → string (or SwiftString if interface uses that)
                var returnExpr = csharpTypeName == "string" ? "str" : "new Swift.SwiftString(str)";
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                var slice = *(Utf8Slice*)resultPtr;
                                var str = slice.Len > 0
                                    ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                                    : string.Empty;
                                return {{returnExpr}};
                            }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else if (isGetterDispatchable)
            {
                // Blittable getter: existing MarshalFromSwift path
                // Use the dispatch emitter's canonical blittable type for marshalling,
                // not the interface-projected type which may differ (e.g. Swift.AnyType)
                var marshalType = dispatchEmitter.GetBlittableCSharpType(property.SwiftTypeSpec) ?? csharpTypeName;
                // F1: If property type is narrowed (int/uint), MarshalFromSwift returns nint/nuint — add cast.
                var returnExpr = marshalType != csharpTypeName
                    ? $"({csharpTypeName})MarshalFromSwift<{marshalType}>(resultPtr)"
                    : $"MarshalFromSwift<{marshalType}>(resultPtr)";

                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try { return {{returnExpr}}; }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else if (isClassReturnGetter)
            {
                // ClassReturn getter: Unmanaged.passRetained on Swift side, NativeMemory+SwiftMarshal on C# side
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            unsafe
                            {
                                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                                try
                                {
                                    *(IntPtr*)classPayload = resultPtr;
                                    return ({{csharpTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{csharpTypeName}}>(new IntPtr(classPayload));
                                }
                                catch { NativeMemory.Free(classPayload); Arc.Release(resultPtr); throw; }
                            }
                        }
                    }
                    """);
            }
            else if (isStructReturnGetter)
            {
                // StructReturn getter: pre-allocate buffer, Swift writes into it
                // Frozen+RefFields structs: NewFromPayload copies to a new buffer, so original must be freed on success
                bool isFrozenRefFields = dispatchEmitter.IsFrozenStructWithRefFields(property.SwiftTypeSpec);
                var cleanupKeyword = isFrozenRefFields ? "finally" : "catch";
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            unsafe
                            {
                                var metadata = SwiftObjectHelper<{{csharpTypeName}}>.GetTypeMetadata();
                                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                                try
                                {
                                    NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr, buffer);
                                    return ({{csharpTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{csharpTypeName}}>(buffer);
                                }
                                {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                            }
                        }
                    }
                    """);
            }
            else if (isCollectionReturnGetter)
            {
                // Collection return getter: heap-allocated pointer + typed free
                var marshalExpr = GetCollectionMarshalExpression(property.SwiftTypeSpec, "resultPtr");
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                return {{marshalExpr}};
                            }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        throw new NotSupportedException(
                            "Cannot get property '{{propertyName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                    }
                    """);
            }
        }

        if (hasSetter)
        {
            var setterSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "set", property.Name, 0);

            if (isSetterDispatchable && isStringProperty)
            {
                // String setter: encode to UTF-8, pass SBW_Utf8Slice to Swift
                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            var str = value?.ToString() ?? string.Empty;
                            var utf8Bytes = global::System.Text.Encoding.UTF8.GetBytes(str);
                            fixed (byte* utf8Ptr = utf8Bytes)
                            {
                                var slice = new Utf8Slice { Ptr = (IntPtr)utf8Ptr, Len = (nint)utf8Bytes.Length };
                                NativeMethods.{{setterSymbol}}((IntPtr)containerPtr, (IntPtr)(&slice));
                            }
                        }
                    }
                    """);
            }
            else if (isSetterDispatchable)
            {
                // Blittable setter: pass value by pointer
                var marshalType = dispatchEmitter.GetBlittableCSharpType(property.SwiftTypeSpec) ?? csharpTypeName;

                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            var valueCopy = ({{marshalType}})value;
                            NativeMethods.{{setterSymbol}}((IntPtr)containerPtr, (IntPtr)(&valueCopy));
                        }
                    }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        throw new NotSupportedException(
                            "Cannot set property '{{propertyName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                    }
                    """);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitSubscriptImplementation(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index)
    {
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec, isParameter: false);

        // Build parameter list
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
        }
        var parametersString = string.Join(", ", parameters);

        var argNames = subscript.IndexParameters.Select(p =>
            NameProvider.GetCSharpParameterName(p)).ToList();
        var argsString = string.Join(", ", argNames);

        writer.WriteLine("[Obsolete(\"This member is not dispatchable to Swift: subscript dispatch is not yet implemented. \" +");
        writer.WriteLine("    \"Throws NotSupportedException on Swift-backed existential containers (SB0003).\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");

        writer.WriteLine($"public {returnTypeName} this[{parametersString}]");
        writer.WriteLine("{");
        writer.Indent++;

        if (subscript.HasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                        return _csharpImpl[{{argsString}}];
                    throw new NotSupportedException(
                        "Cannot get subscript on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                }
                """);
        }

        if (subscript.HasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                    {
                        _csharpImpl[{{argsString}}] = value;
                        return;
                    }
                    throw new NotSupportedException(
                        "Cannot set subscript on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodImplementation(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter, int methodIndex, IReadOnlySet<string>? propertyNames = null)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && WitnessDispatchEmitter.IsStringDispatchType(returnType!);
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

        // Wrap return type for async methods to match interface declaration
        if (method.IsAsync)
        {
            if (returnTypeName == "void")
                returnTypeName = "Task";
            else
                returnTypeName = $"Task<{returnTypeName}>";
        }

        // Build parameter list
        var parameters = new List<string>();
        var argNames = new List<string>();
        var projectedParamTypes = new List<string>();
        var paramSwiftTypeSpecs = new List<TypeSpec?>();
        int argIndex = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            // Skip debug params and empty tuple () params (zero-sized Void)
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
            projectedParamTypes.Add(paramTypeName);
            paramSwiftTypeSpecs.Add(param.SwiftTypeSpec);
            argIndex++;
        }
        // Add CancellationToken to async proxy methods (matches interface + WrapperEmitter emission)
        if (method.IsAsync)
        {
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            argNames.Add("cancellationToken");
        }

        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: propertyNames, isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));
        var dispatchClassification = dispatchEmitter.ClassifyMethodDispatchWithReason(method);
        var dispatchKind = dispatchClassification.Kind;
        var dispatchReason = dispatchClassification.Reason;

        // Secondary C#-side validation for ExistentialReturn — verify proxy class exists
        // and projected return type is a valid interface (not "object" or "AnyType")
        if (dispatchKind == MethodDispatchKind.ExistentialReturn && hasReturn)
        {
            var existentialHandler = new ExistentialHandler(_typeDatabase);
            // Handle Optional<any Protocol> — unwrap before resolving protocol list
            bool isOptionalExistential = existentialHandler.IsOptionalExistential(returnType!);
            var protocolList = isOptionalExistential
                ? existentialHandler.UnwrapOptionalExistential(returnType!)
                : existentialHandler.ToProtocolListTypeSpec(returnType!);
            if (protocolList == null ||
                !existentialHandler.TryGetFilteredProxyClassName(protocolList, out _) ||
                returnTypeName == "object" ||
                returnTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= $"projected return type '{returnTypeName}' has no proxy class";
            }
        }

        // Validate projected types for BlittableOrString dispatch
        if (dispatchKind == MethodDispatchKind.BlittableOrString)
        {
            if (hasReturn)
            {
                if (isStringReturn)
                {
                    if (!IsIdiomaticStringType(returnTypeName))
                    {
                        dispatchKind = MethodDispatchKind.NotDispatchable;
                        dispatchReason ??= $"projected return type '{returnTypeName}' is not an idiomatic string type";
                    }
                }
                else if (!WitnessDispatchEmitter.IsBlittablePrimitive(returnTypeName))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= $"projected return type '{returnTypeName}' is not a blittable primitive";
                }
            }

            if (dispatchKind == MethodDispatchKind.BlittableOrString)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        // Validate projected types for ThrowingBlittableOrString dispatch (same return + param checks)
        if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
        {
            if (hasReturn)
            {
                if (isStringReturn)
                {
                    if (!IsIdiomaticStringType(returnTypeName))
                    {
                        dispatchKind = MethodDispatchKind.NotDispatchable;
                        dispatchReason ??= $"projected return type '{returnTypeName}' is not an idiomatic string type";
                    }
                }
                else if (!WitnessDispatchEmitter.IsBlittablePrimitive(returnTypeName))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= $"projected return type '{returnTypeName}' is not a blittable primitive";
                }
            }

            if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        // Validate params for ExistentialReturn dispatch (same param validation)
        if (dispatchKind == MethodDispatchKind.ExistentialReturn)
        {
            if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= "projected parameter type is not dispatchable";
            }
        }

        // Secondary C#-side validation for ClassReturn, StructReturn, and BoundGenericReturn:
        // Reject if projected return type is "object" or "AnyType" (TypeDatabase degradation)
        if (dispatchKind is MethodDispatchKind.ClassReturn or MethodDispatchKind.StructReturn or MethodDispatchKind.BoundGenericReturn)
        {
            if (returnTypeName == "object" ||
                returnTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= $"projected return type '{returnTypeName}' degraded to AnyType (incomplete type database)";
            }

            // Validate params
            if (dispatchKind is MethodDispatchKind.ClassReturn or MethodDispatchKind.StructReturn or MethodDispatchKind.BoundGenericReturn)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        var isDispatchable = dispatchKind != MethodDispatchKind.NotDispatchable;

        if (!isDispatchable)
        {
            var reasonSuffix = dispatchReason != null
                ? $": {dispatchReason}. Throws"
                : " and throws";
            writer.WriteLine($"[Obsolete(\"This member is not dispatchable to Swift{reasonSuffix} NotSupportedException \" +");
            writer.WriteLine("    \"on Swift-backed existential containers (SB0003).\",");
            writer.WriteLine("    DiagnosticId = \"SB0003\",");
            writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
        }

        writer.WriteLine($"public {returnTypeName} {methodName}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");

        if (dispatchKind == MethodDispatchKind.ExistentialReturn)
        {
            EmitExistentialReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.BlittableOrString)
        {
            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

            if (hasReturn)
            {
                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

                writer.WriteLines($$"""
                    if (_csharpImpl != null)
                        return _csharpImpl.{{methodName}}({{argsString}});
                    fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                    {
                    """);
                writer.Indent++;

                // Declare pin handles before try for exception-safe cleanup
                var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
                bool needsOuterTry = pinHandles.Count > 0;

                if (needsOuterTry)
                {
                    writer.WriteLine("try");
                    writer.WriteLine("{");
                    writer.Indent++;
                }

                // Marshal each parameter — String via GCHandle-pinned Utf8Slice, blittable via copy
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

                // Build P/Invoke call
                var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
                for (int i = 0; i < argNames.Count; i++)
                {
                    pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
                }
                var pInvokeArgsString = string.Join(", ", pInvokeArgs);

                if (isStringReturn)
                {
                    // String return: decode SBW_Utf8Slice → string
                    writer.WriteLines($$"""
                        IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                        try
                        {
                            var slice = *(Utf8Slice*)resultPtr;
                            return slice.Len > 0
                                ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                                : string.Empty;
                        }
                        finally
                        {
                            NativeMethods.{{freeSymbol}}(resultPtr);
                        }
                        """);
                }
                else
                {
                    // Blittable return: existing MarshalFromSwift path
                    var marshalReturnType = dispatchEmitter.GetBlittableCSharpType(returnType!) ?? GetCSharpTypeName(returnType!);

                    writer.WriteLines($$"""
                        IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                        try { return MarshalFromSwift<{{marshalReturnType}}>(resultPtr); }
                        finally
                        {
                            NativeMethods.{{freeSymbol}}(resultPtr);
                        }
                        """);
                }

                if (needsOuterTry)
                {
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine("finally");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitPinHandleCleanup(writer, pinHandles);
                    writer.Indent--;
                    writer.WriteLine("}");
                }

                writer.Indent--;
                writer.WriteLine("}");
            }
            else
            {
                if (method.IsAsync)
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                            return _csharpImpl.{{methodName}}({{argsString}});
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{methodName}}({{argsString}});
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                        """);
                }
                writer.Indent++;

                // Declare pin handles before try for exception-safe cleanup
                var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
                bool needsOuterTry = pinHandles.Count > 0;

                if (needsOuterTry)
                {
                    writer.WriteLine("try");
                    writer.WriteLine("{");
                    writer.Indent++;
                }

                // Marshal each parameter — String via GCHandle-pinned Utf8Slice, blittable via copy
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

                var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
                for (int i = 0; i < argNames.Count; i++)
                {
                    pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
                }
                var pInvokeArgsString = string.Join(", ", pInvokeArgs);

                writer.WriteLine($"NativeMethods.{accessorSymbol}({pInvokeArgsString});");

                if (needsOuterTry)
                {
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine("finally");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitPinHandleCleanup(writer, pinHandles);
                    writer.Indent--;
                    writer.WriteLine("}");
                }

                writer.Indent--;
                writer.WriteLine("}");
            }
        }
        else if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
        {
            EmitThrowingBlittableMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType, returnTypeName, hasReturn, isStringReturn);
        }
        else if (dispatchKind == MethodDispatchKind.ClassReturn)
        {
            EmitClassReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.StructReturn)
        {
            EmitStructReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.BoundGenericReturn)
        {
            EmitCollectionReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else
        {
            // Non-dispatchable: keep NotSupportedException
            if (hasReturn)
            {
                writer.WriteLines($$"""
                    if (_csharpImpl != null)
                        return _csharpImpl.{{methodName}}({{argsString}});
                    throw new NotSupportedException(
                        "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                    """);
            }
            else
            {
                if (method.IsAsync)
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                            return _csharpImpl.{{methodName}}({{argsString}});
                        throw new NotSupportedException(
                            "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{methodName}}({{argsString}});
                            return;
                        }
                        throw new NotSupportedException(
                            "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                        """);
                }
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return protocol existentials.
    /// Uses typed pointer allocation on the Swift side, Unsafe.Read on the C# side to
    /// recover the ExistentialContainer, and constructs a proxy class instance.
    /// Throwing methods use error out-parameter pattern matching GenericClosureBridgeEmitter.
    /// </summary>
    private void EmitExistentialReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        // Resolve the existential container type and proxy class name
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool isOptionalExistential = existentialHandler.IsOptionalExistential(returnType);
        var protocolList = isOptionalExistential
            ? existentialHandler.UnwrapOptionalExistential(returnType)
            : existentialHandler.ToProtocolListTypeSpec(returnType);
        var containerType = existentialHandler.GetCSharpExistentialType(protocolList!);
        existentialHandler.TryGetFilteredProxyClassName(protocolList!, out var proxyClassName);

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        for (int i = 0; i < argNames.Count; i++)
        {
            pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Throwing pattern: error out-parameter, null result means error
            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero)
                {
                    string _errorMessage;
                    var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                    try
                    {
                        _errorMessage = _descPtr != IntPtr.Zero
                            ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                            : "Unknown Swift error";
                    }
                    finally
                    {
                        if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                        NativeMethods.SBW_ReleaseError(errorOut);
                    }
                    throw new Swift.Runtime.SwiftException(_errorMessage);
                }
                try
                {
                    var container = Unsafe.Read<{{containerType}}>((void*)resultPtr);
                    return new {{proxyClassName}}(container);
                }
                finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                """);
        }
        else if (isOptionalExistential)
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Optional existential pattern: IntPtr.Zero → return null
            writer.WriteLines($$"""
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero) return null;
                try
                {
                    var container = Unsafe.Read<{{containerType}}>((void*)resultPtr);
                    return new {{proxyClassName}}(container);
                }
                finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                """);
        }
        else
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Non-throwing, non-optional pattern: direct allocation
            writer.WriteLines($$"""
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                try
                {
                    var container = Unsafe.Read<{{containerType}}>((void*)resultPtr);
                    return new {{proxyClassName}}(container);
                }
                finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the C# dispatch body for throwing methods that return blittable/String/void types.
    /// Value-returning: resultPtr == IntPtr.Zero means error; otherwise MarshalFromSwift/Utf8Decode.
    /// Void: errorOut != IntPtr.Zero means error.
    /// </summary>
    private void EmitThrowingBlittableMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec? returnType, string returnTypeName, bool hasReturn, bool isStringReturn)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        if (hasReturn)
        {
            var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

            writer.WriteLines($$"""
                if (_csharpImpl != null)
                    return _csharpImpl.{{methodName}}({{argsString}});
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                """);
            writer.Indent++;

            // Declare pin handles before try for exception-safe cleanup
            var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
            bool needsOuterTry = pinHandles.Count > 0;

            if (needsOuterTry)
            {
                writer.WriteLine("try");
                writer.WriteLine("{");
                writer.Indent++;
            }

            // Marshal each parameter
            EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

            // Build P/Invoke call args
            var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
            for (int i = 0; i < argNames.Count; i++)
            {
                pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
            }
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            if (isStringReturn)
            {
                // String return with error check: resultPtr == IntPtr.Zero means error
                writer.WriteLines($$"""
                    IntPtr errorOut = IntPtr.Zero;
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero)
                    {
                        string _errorMessage;
                        var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                        try
                        {
                            _errorMessage = _descPtr != IntPtr.Zero
                                ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                : "Unknown Swift error";
                        }
                        finally
                        {
                            if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                            NativeMethods.SBW_ReleaseError(errorOut);
                        }
                        throw new Swift.Runtime.SwiftException(_errorMessage);
                    }
                    try
                    {
                        var slice = *(Utf8Slice*)resultPtr;
                        return slice.Len > 0
                            ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                            : string.Empty;
                    }
                    finally
                    {
                        NativeMethods.{{freeSymbol}}(resultPtr);
                    }
                    """);
            }
            else
            {
                // Blittable return with error check
                var marshalReturnType = dispatchEmitter.GetBlittableCSharpType(returnType!) ?? GetCSharpTypeName(returnType!);

                writer.WriteLines($$"""
                    IntPtr errorOut = IntPtr.Zero;
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero)
                    {
                        string _errorMessage;
                        var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                        try
                        {
                            _errorMessage = _descPtr != IntPtr.Zero
                                ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                : "Unknown Swift error";
                        }
                        finally
                        {
                            if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                            NativeMethods.SBW_ReleaseError(errorOut);
                        }
                        throw new Swift.Runtime.SwiftException(_errorMessage);
                    }
                    try { return MarshalFromSwift<{{marshalReturnType}}>(resultPtr); }
                    finally
                    {
                        NativeMethods.{{freeSymbol}}(resultPtr);
                    }
                    """);
            }

            if (needsOuterTry)
            {
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("finally");
                writer.WriteLine("{");
                writer.Indent++;
                EmitPinHandleCleanup(writer, pinHandles);
                writer.Indent--;
                writer.WriteLine("}");
            }

            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            // Void throwing: check errorOut after call
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                {
                    _csharpImpl.{{methodName}}({{argsString}});
                    return;
                }
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                """);
            writer.Indent++;

            // Declare pin handles before try for exception-safe cleanup
            var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
            bool needsOuterTry = pinHandles.Count > 0;

            if (needsOuterTry)
            {
                writer.WriteLine("try");
                writer.WriteLine("{");
                writer.Indent++;
            }

            EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

            var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
            for (int i = 0; i < argNames.Count; i++)
            {
                pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
            }
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (errorOut != IntPtr.Zero)
                {
                    string _errorMessage;
                    var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                    try
                    {
                        _errorMessage = _descPtr != IntPtr.Zero
                            ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                            : "Unknown Swift error";
                    }
                    finally
                    {
                        if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                        NativeMethods.SBW_ReleaseError(errorOut);
                    }
                    throw new Swift.Runtime.SwiftException(_errorMessage);
                }
                """);

            if (needsOuterTry)
            {
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("finally");
                writer.WriteLine("{");
                writer.Indent++;
                EmitPinHandleCleanup(writer, pinHandles);
                writer.Indent--;
                writer.WriteLine("}");
            }

            writer.Indent--;
            writer.WriteLine("}");
        }
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return a Swift class.
    /// Uses Unmanaged.passRetained on Swift side; C# wraps IntPtr in NativeMemory + SwiftMarshal.
    /// Matches ExtensionMarshallingHelper.SwiftClass pattern (try/catch, not try/finally).
    /// Throwing: resultPtr == IntPtr.Zero means error (same as ExistentialReturn throwing).
    /// </summary>
    private void EmitClassReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        for (int i = 0; i < argNames.Count; i++)
        {
            pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Throwing: error out-parameter, null result means error
            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero)
                {
                    string _errorMessage;
                    var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                    try
                    {
                        _errorMessage = _descPtr != IntPtr.Zero
                            ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                            : "Unknown Swift error";
                    }
                    finally
                    {
                        if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                        NativeMethods.SBW_ReleaseError(errorOut);
                    }
                    throw new Swift.Runtime.SwiftException(_errorMessage);
                }
                unsafe
                {
                    var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                    try
                    {
                        *(IntPtr*)classPayload = resultPtr;
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(new IntPtr(classPayload));
                    }
                    catch { NativeMemory.Free(classPayload); Arc.Release(resultPtr); throw; }
                }
                """);
        }
        else
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Non-throwing: direct class return
            writer.WriteLines($$"""
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                unsafe
                {
                    var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                    try
                    {
                        *(IntPtr*)classPayload = resultPtr;
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(new IntPtr(classPayload));
                    }
                    catch { NativeMemory.Free(classPayload); Arc.Release(resultPtr); throw; }
                }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return a non-frozen struct.
    /// C# pre-allocates buffer via NativeMemory.Alloc(metadata.Size), passes as resultBuf.
    /// Swift writes into buffer. SafeHandle takes ownership via SwiftMarshal.MarshalFromSwift.
    /// Non-frozen structs: try/catch (SafeHandle takes buffer ownership on success).
    /// Frozen+RefFields structs: try/finally (NewFromPayload copies to new buffer, original must be freed).
    /// Throwing: errorOut != IntPtr.Zero means error (same as void throwing pattern).
    /// </summary>
    private void EmitStructReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        bool isFrozenRefFields = dispatchEmitter.IsFrozenStructWithRefFields(returnType);
        var cleanupKeyword = isFrozenRefFields ? "finally" : "catch";

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args: containerPtr + resultBuf + params + errorOut
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        // resultBuf inserted below after buffer allocation
        var argPInvokeList = new List<string>();
        for (int i = 0; i < argNames.Count; i++)
        {
            argPInvokeList.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            // Throwing struct return: errorOut check, void P/Invoke
            writer.WriteLines($$"""
                unsafe
                {
                    var metadata = SwiftObjectHelper<{{returnTypeName}}>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    try
                    {
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        IntPtr errorOut = IntPtr.Zero;
                """);
            writer.Indent += 2;

            var throwingPInvokeArgs = new List<string> { "(IntPtr)containerPtr", "(IntPtr)indirectResult.Value" };
            throwingPInvokeArgs.AddRange(argPInvokeList);
            throwingPInvokeArgs.Add("(IntPtr)(&errorOut)");
            var throwingPInvokeArgsString = string.Join(", ", throwingPInvokeArgs);

            writer.WriteLines($$"""
                        NativeMethods.{{accessorSymbol}}({{throwingPInvokeArgsString}});
                        if (errorOut != IntPtr.Zero)
                        {
                            string _errorMessage;
                            var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                            try
                            {
                                _errorMessage = _descPtr != IntPtr.Zero
                                    ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                    : "Unknown Swift error";
                            }
                            finally
                            {
                                if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                                NativeMethods.SBW_ReleaseError(errorOut);
                            }
                            throw new Swift.Runtime.SwiftException(_errorMessage);
                        }
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(buffer);
                    }
                    {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                }
                """);
            writer.Indent -= 2;
        }
        else
        {
            // Non-throwing struct return
            var nonThrowingPInvokeArgs = new List<string> { "(IntPtr)containerPtr", "(IntPtr)indirectResult.Value" };
            nonThrowingPInvokeArgs.AddRange(argPInvokeList);
            var nonThrowingPInvokeArgsString = string.Join(", ", nonThrowingPInvokeArgs);

            writer.WriteLines($$"""
                unsafe
                {
                    var metadata = SwiftObjectHelper<{{returnTypeName}}>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    try
                    {
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        NativeMethods.{{accessorSymbol}}({{nonThrowingPInvokeArgsString}});
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(buffer);
                    }
                    {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits parameter marshalling for dispatched methods.
    /// String params: encode to UTF-8 bytes, pin via GCHandle, wrap in Utf8Slice.
    /// Class/struct params: extract SafeHandle payload pointer via Payload.DangerousGetHandle().
    /// Blittable params: simple copy.
    /// All params end up as arg{i}Slice for uniform pointer passing.
    /// Handle variables must be pre-declared by EmitPinHandleDeclarations before the enclosing try block.
    /// </summary>
    private static void EmitMethodParameterMarshalling(CSharpWriter writer, List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs, WitnessDispatchEmitter? dispatchEmitter = null)
    {
        for (int i = 0; i < argNames.Count; i++)
        {
            if (WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]))
            {
                // String parameter: encode to UTF-8, pin via GCHandle, wrap in Utf8Slice
                var handleName = $"arg{i}Handle";
                writer.WriteLine($"var arg{i}Bytes = global::System.Text.Encoding.UTF8.GetBytes({argNames[i]} ?? string.Empty);");
                writer.WriteLine($"{handleName} = GCHandle.Alloc(arg{i}Bytes, GCHandleType.Pinned);");
                writer.WriteLine($"var arg{i}Slice = new Utf8Slice {{ Ptr = {handleName}.AddrOfPinnedObject(), Len = (nint)arg{i}Bytes.Length }};");
            }
            else if (dispatchEmitter != null &&
                     (dispatchEmitter.IsSwiftClassType(paramSwiftTypeSpecs[i]) ||
                      dispatchEmitter.IsIndirectStructType(paramSwiftTypeSpecs[i])))
            {
                // ObjC-rooted classes use .Handle (ObjC pointer), pure Swift classes use .Payload
                if (dispatchEmitter.IsObjCRootedClassType(paramSwiftTypeSpecs[i]))
                    writer.WriteLine($"var arg{i}Slice = {argNames[i]}.Handle;");
                else
                    writer.WriteLine($"var arg{i}Slice = {argNames[i]}.Payload.DangerousGetHandle();");
            }
            else
            {
                // Blittable parameter: simple copy
                writer.WriteLine($"var arg{i}Slice = {argNames[i]};");
            }
        }
    }

    /// <summary>
    /// Emits GCHandle.Free() calls for pinned string parameter handles.
    /// Uses IsAllocated check for exception-safe cleanup.
    /// </summary>
    private static void EmitPinHandleCleanup(CSharpWriter writer, List<string> pinHandles)
    {
        foreach (var handle in pinHandles)
        {
            writer.WriteLine($"if ({handle}.IsAllocated) {handle}.Free();");
        }
    }

    /// <summary>
    /// Emits GCHandle variable declarations initialized to default before try blocks.
    /// This ensures handles can be safely checked with IsAllocated in finally blocks
    /// even if an exception occurs during allocation of subsequent handles.
    /// </summary>
    private static List<string> EmitPinHandleDeclarations(CSharpWriter writer, List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs)
    {
        var pinHandles = new List<string>();
        for (int i = 0; i < argNames.Count; i++)
        {
            if (WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]))
            {
                var handleName = $"arg{i}Handle";
                writer.WriteLine($"var {handleName} = default(GCHandle);");
                pinHandles.Add(handleName);
            }
        }
        return pinHandles;
    }

    /// <summary>
    /// Validates that all param projections are compatible with witness dispatch.
    /// Returns false if any param has an incompatible projection.
    /// </summary>
    private static bool ValidateParamProjections(List<string> projectedParamTypes, List<TypeSpec?> paramSwiftTypeSpecs, WitnessDispatchEmitter dispatchEmitter)
    {
        for (int i = 0; i < projectedParamTypes.Count; i++)
        {
            if (!IsParamProjectionValid(paramSwiftTypeSpecs[i], projectedParamTypes[i], dispatchEmitter))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a single param's projected C# type is valid for witness dispatch.
    /// String: must be idiomatic string type. Class/struct: must not be degraded to "object" or AnyType.
    /// Otherwise: must be blittable primitive.
    /// </summary>
    private static bool IsParamProjectionValid(TypeSpec? swiftType, string projectedType, WitnessDispatchEmitter dispatchEmitter)
    {
        if (WitnessDispatchEmitter.IsStringDispatchType(swiftType))
            return IsIdiomaticStringType(projectedType);

        if (dispatchEmitter.IsSwiftClassType(swiftType) || dispatchEmitter.IsIndirectStructType(swiftType))
        {
            // Class/struct param: reject if projected type degraded to "object" or AnyType
            return projectedType != "object" &&
                   projectedType != TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        return WitnessDispatchEmitter.IsBlittablePrimitive(projectedType);
    }

    /// <summary>
    /// Gets the C# marshal expression for a collection return type using the TypeProjectionFactory.
    /// Composes MarshalFromSwift&lt;ContainerType&gt;(ptr) with the container conversion suffix.
    /// </summary>
    private string GetCollectionMarshalExpression(TypeSpec returnTypeSpec, string ptrVar)
    {
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(returnTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = GenericContext.Empty
        });
        if (projection == null)
            return $"Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<object>({ptrVar})";

        var containerType = projection.ContainerTypeName;
        // Pass empty containerVar to get just the conversion suffix (e.g., ".AsProjected(e => ...)")
        var suffix = projection.GetReturnContainerConversion("");
        return $"Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{containerType}>({ptrVar}){suffix}";
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return collection types (Array, Dictionary, Set).
    /// Uses the same heap-allocated pointer + free function pattern as ExistentialReturn,
    /// but uses TypeProjectionFactory for the return conversion.
    /// </summary>
    private void EmitCollectionReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        var marshalExpr = GetCollectionMarshalExpression(returnType, "resultPtr");

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        for (int i = 0; i < argNames.Count; i++)
        {
            pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Throwing pattern: error out-parameter, null result means error
            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero)
                {
                    string _errorMessage;
                    var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
                    try
                    {
                        _errorMessage = _descPtr != IntPtr.Zero
                            ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                            : "Unknown Swift error";
                    }
                    finally
                    {
                        if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                        NativeMethods.SBW_ReleaseError(errorOut);
                    }
                    throw new Swift.Runtime.SwiftException(_errorMessage);
                }
                try
                {
                    return {{marshalExpr}};
                }
                finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                """);
        }
        else
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Non-throwing pattern: direct allocation
            writer.WriteLines($$"""
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                try
                {
                    return {{marshalExpr}};
                }
                finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a NotSupportedException stub for a closure property that is in the interface
    /// but can't be dispatched by the proxy (closure marshalling not supported).
    /// </summary>
    private void EmitNotSupportedPropertyStub(CSharpWriter writer, PropertyDecl property)
    {
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
        var propertyName = NameProvider.GetPropertyName(property.Name);
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        writer.WriteLine("[Obsolete(\"This member has closure parameters that cannot be marshalled in protocol proxy (SB0003).\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
        writer.WriteLine($"public {csharpTypeName} {propertyName}");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                        return _csharpImpl.{{propertyName}};
                    throw new NotSupportedException(
                        "Cannot get property '{{propertyName}}' on a Swift-backed existential container. " +
                        "Closure-typed properties cannot be marshalled in protocol proxy.");
                }
                """);
        }

        if (hasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                    {
                        _csharpImpl.{{propertyName}} = value;
                        return;
                    }
                    throw new NotSupportedException(
                        "Cannot set property '{{propertyName}}' on a Swift-backed existential container. " +
                        "Closure-typed properties cannot be marshalled in protocol proxy.");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a NotSupportedException stub for a method that is in the interface
    /// but can't be dispatched by the proxy (e.g. closure or existential parameter marshalling).
    /// </summary>
    private void EmitNotSupportedMethodStub(CSharpWriter writer, MethodDecl method, string reason, IReadOnlySet<string>? propertyNames = null)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

        if (method.IsAsync)
        {
            returnTypeName = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";
        }

        var parameters = new List<string>();
        var argNames = new List<string>();
        foreach (var param in method.CSSignature.Skip(1))
        {
            // Skip debug params and empty tuple () params (zero-sized Void)
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
        }
        if (method.IsAsync)
        {
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            argNames.Add("cancellationToken");
        }

        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: propertyNames, isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        writer.WriteLine($"[Obsolete(\"{reason} (SB0003)\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
        writer.WriteLine($"public {returnTypeName} {methodName}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");

        if (hasReturn || method.IsAsync)
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                    return _csharpImpl.{{methodName}}({{argsString}});
                throw new NotSupportedException(
                    "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                    "{{reason}}");
                """);
        }
        else
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                {
                    _csharpImpl.{{methodName}}({{argsString}});
                    return;
                }
                throw new NotSupportedException(
                    "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                    "{{reason}}");
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }
}
