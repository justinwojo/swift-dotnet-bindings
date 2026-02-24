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
                // Skip properties that the interface skipped due to AnyType generic args
                if (_skippedPropertyNames.Contains(property.Name))
                    continue;
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
                // Skip methods that the interface skipped due to AnyType generic args
                if (_skippedMethodKeys.Contains(methodKey))
                    continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitMethodImplementation(writer, method, protocolDecl, dispatchEmitter, idx);
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
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isGetterDispatchable = false;
            }
        }
        if (isSetterDispatchable)
        {
            if (isStringProperty)
            {
                if (!IsSwiftStringProjectedType(csharpTypeName))
                    isSetterDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isSetterDispatchable = false;
            }
        }

        bool isAnyAccessorNonDispatchable =
            (hasGetter && !isGetterDispatchable) || (hasSetter && !isSetterDispatchable);
        if (isAnyAccessorNonDispatchable)
        {
            writer.WriteLine("[Obsolete(\"This member is not dispatchable to Swift and throws NotSupportedException \" +");
            writer.WriteLine("    \"when called on a Swift-backed existential container (SB0003).\",");
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
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                var slice = *(Utf8Slice*)resultPtr;
                                var str = slice.Len > 0
                                    ? System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
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

                writer.WriteLines($$"""
                    get
                    {
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try { return MarshalFromSwift<{{marshalType}}>(resultPtr); }
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
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            var str = value?.ToString() ?? string.Empty;
                            var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(str);
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

        writer.WriteLine("[Obsolete(\"This member is not dispatchable to Swift and throws NotSupportedException \" +");
        writer.WriteLine("    \"when called on a Swift-backed existential container (SB0003).\",");
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

    private void EmitMethodImplementation(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter, int methodIndex)
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
            parameters.Add("System.Threading.CancellationToken cancellationToken = default");
            argNames.Add("cancellationToken");
        }

        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn, isSelfReturning: isSelfReturning);
        var isDispatchable = dispatchEmitter.IsMethodDispatchable(method);

        // Validate that the projected return type matches the dispatch strategy.
        // IsMethodDispatchable checks Swift-side dispatchability, but if the projected
        // return type diverges (e.g. Swift.AnyType from incomplete TypeDatabase),
        // the generated code would have a type mismatch. Disable dispatch.
        if (isDispatchable && hasReturn)
        {
            if (isStringReturn)
            {
                // String return: projected type must be idiomatic "string" (from TypeConversionHandler)
                if (!IsIdiomaticStringType(returnTypeName))
                    isDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(returnTypeName))
            {
                isDispatchable = false;
            }
        }

        // Validate that each parameter's projected C# type matches the dispatch strategy.
        // The Swift-side dispatchability is already checked by IsMethodDispatchable().
        // Here we verify the C# side: if the projected type doesn't match what the
        // marshalling code expects, the generated code would fail to compile.
        if (isDispatchable)
        {
            for (int i = 0; i < projectedParamTypes.Count; i++)
            {
                var isStringParam = WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]);
                if (isStringParam)
                {
                    if (!IsIdiomaticStringType(projectedParamTypes[i]))
                    {
                        isDispatchable = false;
                        break;
                    }
                }
                else if (!WitnessDispatchEmitter.IsBlittablePrimitive(projectedParamTypes[i]))
                {
                    isDispatchable = false;
                    break;
                }
            }
        }

        if (!isDispatchable)
        {
            writer.WriteLine("[Obsolete(\"This member is not dispatchable to Swift and throws NotSupportedException \" +");
            writer.WriteLine("    \"when called on a Swift-backed existential container (SB0003).\",");
            writer.WriteLine("    DiagnosticId = \"SB0003\",");
            writer.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
        }

        writer.WriteLine($"public {returnTypeName} {methodName}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        if (isDispatchable)
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
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs);

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
                                ? System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
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
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs);

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
    /// Emits parameter marshalling for dispatched methods.
    /// String params: encode to UTF-8 bytes, pin via GCHandle, wrap in Utf8Slice.
    /// Blittable params: simple copy.
    /// All params end up as arg{i}Slice for uniform pointer passing.
    /// Handle variables must be pre-declared by EmitPinHandleDeclarations before the enclosing try block.
    /// </summary>
    private static void EmitMethodParameterMarshalling(CSharpWriter writer, List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs)
    {
        for (int i = 0; i < argNames.Count; i++)
        {
            if (WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]))
            {
                // String parameter: encode to UTF-8, pin via GCHandle, wrap in Utf8Slice
                var handleName = $"arg{i}Handle";
                writer.WriteLine($"var arg{i}Bytes = System.Text.Encoding.UTF8.GetBytes({argNames[i]} ?? string.Empty);");
                writer.WriteLine($"{handleName} = GCHandle.Alloc(arg{i}Bytes, GCHandleType.Pinned);");
                writer.WriteLine($"var arg{i}Slice = new Utf8Slice {{ Ptr = {handleName}.AddrOfPinnedObject(), Len = (nint)arg{i}Bytes.Length }};");
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
}
