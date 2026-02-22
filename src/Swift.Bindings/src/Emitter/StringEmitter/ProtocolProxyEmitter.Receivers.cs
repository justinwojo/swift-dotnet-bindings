// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitReceiverMethods(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        writer.WriteLine("#region Swift Callback Receivers");
        writer.WriteLine();

        // Track emitted receivers to avoid duplicates
        var emittedReceivers = new HashSet<string>();

        // Property receivers (skip static properties - they're not part of the interface)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // Skip receivers for properties that the interface skipped due to AnyType generic args
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            EmitPropertyReceivers(writer, property, protocolDecl, interfaceName, emittedReceivers);
        }

        // Subscript receivers (skip static subscripts - they're not part of the interface)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            // Skip receivers for subscripts that the interface skipped due to AnyType generic args
            if (_skippedSubscriptIndices.Contains(subscriptIndex))
            {
                subscriptIndex++;
                continue;
            }
            EmitSubscriptReceivers(writer, subscript, protocolDecl, interfaceName, subscriptIndex, emittedReceivers);
            subscriptIndex++;
        }

        // Method receivers
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Skip receivers for methods that the interface skipped due to AnyType generic args
                if (_skippedMethodKeys.Contains(methodKey))
                    continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                // Only emit receiver for new methods
                EmitMethodReceiver(writer, method, protocolDecl, interfaceName, idx, emittedReceivers);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitPropertyReceivers(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string interfaceName, HashSet<string> emittedReceivers)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var proxyClassName = GetProxyClassName(protocolDecl);
        // P0: Use ABI type for MarshalFromSwift (setter reads Swift memory layout),
        // not the idiomatic type used for signatures.
        // For Optional<existential>, override ABI type to use SwiftOptional<ExistentialContainer>
        // instead of SwiftOptional<AnyType> (which GetCSharpTypeName returns as fallback).
        var abiTypeName = OverrideOptionalExistentialAbiType(
            GetCSharpTypeName(property.SwiftTypeSpec, forAbiMarshalling: true),
            property.SwiftTypeSpec);

        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // The interface property uses idiomatic C# types (e.g., string, string?, IReadOnlyList<string>)
                // but MarshalToSwiftBuffer expects Swift ABI types (SwiftString, SwiftOptional<SwiftString>, etc.).
                // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                var getterConversion = GetReceiverGetterConversion("result", property.SwiftTypeSpec);

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");
                writer.WriteLine($"var result = proxy._csharpImpl!.{pascalPropertyName};");
                if (getterConversion != null)
                {
                    writer.WriteLine($"var swiftResult = {getterConversion};");
                    writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                }
                else
                {
                    writer.WriteLine("return MarshalToSwiftBuffer(result);");
                }
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Check if the property type needs conversion (e.g., SwiftOptional<SwiftString> → string?)
                // The receiver marshals the Swift ABI type, but the interface uses the idiomatic C# type.
                // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                var returnConversion = GetReceiverSetterConversion("value", property.SwiftTypeSpec);
                var assignmentExpr = returnConversion ?? "value";

                writer.WriteLines($$"""
                    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                    private static void {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)
                    {
                        var container = *(ExistentialContainer1*)selfContainer;
                        var proxy = SwiftObjectRegistry.GetProxyFromContainer<{{proxyClassName}}>(container);
                        var value = MarshalFromSwift<{{abiTypeName}}>(valuePtr);
                        proxy._csharpImpl!.{{pascalPropertyName}} = {{assignmentExpr}};
                    }

                    """);
            }
        }
    }

    private void EmitSubscriptReceivers(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        // P0: Use ABI type for MarshalFromSwift (reads Swift memory layout)
        var returnTypeName = OverrideOptionalExistentialAbiType(
            GetCSharpTypeName(subscript.ReturnTypeSpec, forAbiMarshalling: true),
            subscript.ReturnTypeSpec);
        var paramCount = subscript.IndexParameters.Count;

        if (subscript.HasGetter)
        {
            var receiverName = $"Receive_subscript_{index}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");

                // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = OverrideOptionalExistentialAbiType(
                        GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true),
                        param.SwiftTypeSpec);
                    writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"var result = proxy._csharpImpl![{indexArgs}];");
                var subscriptGetterConv = GetReceiverExistentialGetterConversion("result", subscript.ReturnTypeSpec);
                if (subscriptGetterConv != null)
                {
                    writer.WriteLine($"var swiftResult = {subscriptGetterConv};");
                    writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                }
                else
                {
                    writer.WriteLine("return MarshalToSwiftBuffer(result);");
                }

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (subscript.HasSetter)
        {
            var receiverName = $"Receive_subscript_{index}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list (includes value after self)
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");
                var subscriptSetterConv = GetReceiverExistentialSetterConversion("rawValue", subscript.ReturnTypeSpec);
                if (subscriptSetterConv != null)
                {
                    writer.WriteLine($"var rawValue = MarshalFromSwift<{returnTypeName}>(valuePtr);");
                    writer.WriteLine($"var value = {subscriptSetterConv};");
                }
                else
                {
                    writer.WriteLine($"var value = MarshalFromSwift<{returnTypeName}>(valuePtr);");
                }

                // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = OverrideOptionalExistentialAbiType(
                        GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true),
                        param.SwiftTypeSpec);
                    writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"proxy._csharpImpl![{indexArgs}] = value;");

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }
    }

    private void EmitMethodReceiver(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var receiverName = $"Receive_{method.Name}_{index}";
        if (!emittedReceivers.Add(receiverName))
            return;

        var proxyClassName = GetProxyClassName(protocolDecl);
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!) : "void";

        // Detect existential/optional-existential return types — these can't be marshalled
        // back to Swift via Unsafe.Write because the C# interface type doesn't match
        // the Swift existential container layout (3 payload + 1 metadata + N witness table words).
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool hasOptionalExistentialReturn = hasReturn && existentialHandler.IsOptionalExistential(returnType!);

        var paramCount = method.CSSignature.Count - 1;
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
            method.CSSignature.Skip(1).Select((p, i) => $", IntPtr rawArg{i}"));

        var csharpReturnType = hasReturn ? "IntPtr" : "void";

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
        writer.WriteLine($"private static {csharpReturnType} {receiverName}({paramTypes})");
        writer.WriteLine("{");
        writer.Indent++;

        // Optional existential returns: return zeroed buffer representing Optional.none.
        // C# interface types (e.g. IImageDecoding?) can't be correctly marshalled into
        // Swift existential containers — constructing a valid container requires Swift type
        // metadata + protocol witness table pointers that aren't accessible from C#.
        // Returning None is safe; non-null existential return marshalling requires future
        // infrastructure (Swift metadata lookup + witness table construction from C#).
        // This is NOT a regression — before optional existential resolution, these methods
        // used AnyType? with equally invalid Unsafe.Write marshalling.
        if (hasOptionalExistentialReturn)
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(returnType!);
            var containerSizeWords = innerProtocolList != null
                ? existentialHandler.GetExistentialContainerSizeInWords(innerProtocolList)
                : 5; // default: 3 payload + 1 metadata + 1 witness table
            var containerSizeBytes = containerSizeWords * 8;
            writer.WriteLine($"// Optional existential return: can't construct valid Swift existential container from C#");
            writer.WriteLine($"// (needs type metadata + witness table). Return None until existential marshalling is implemented.");
            writer.WriteLine($"return (IntPtr)NativeMemory.AllocZeroed({containerSizeBytes});");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine();
            return;
        }

        writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
        writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");

        // Unmarshal parameters - use param{i} for local variable names to avoid conflicts with rawArg{i}
        // B10: After unmarshalling, apply type conversion from ABI to idiomatic C# types
        // (e.g., SwiftOptional<SwiftString> → string?) to match the interface method signature.
        // P0: Use ABI types for MarshalFromSwift — idiomatic types (string, bool?) can't read Swift memory.
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            var paramTypeName = OverrideOptionalExistentialAbiType(
                GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true),
                param.SwiftTypeSpec);
            var rawArgName = $"rawParam{argIndex}";
            var argName = $"param{argIndex}";

            // Dictionaries need special handling in receiver context: the interface declares
            // IDictionary<K,V> (parameter form), but projection produces .AsProjected()
            // which returns IReadOnlyDictionary<K,V> (return form). IReadOnlyDictionary doesn't
            // implement IDictionary, so we must use .ToDictionary() for eager materialization.
            var receiverDictConversion = GetReceiverDictionaryConversion(rawArgName, param.SwiftTypeSpec, typeConversionHandler);
            if (receiverDictConversion != null)
            {
                writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {receiverDictConversion};");
            }
            else
            {
                var setterConversion = GetReceiverSetterConversion(rawArgName, param.SwiftTypeSpec);
                if (setterConversion != null)
                {
                    writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                    writer.WriteLine($"var {argName} = {setterConversion};");
                }
                else
                {
                    writer.WriteLine($"var {argName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                }
            }
            argNames.Add(argName);
            argIndex++;
        }

        var argsString = string.Join(", ", argNames);

        var pascalMethodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn);

        if (hasReturn)
        {
            var existentialReturnConv = GetReceiverExistentialGetterConversion("result", returnType!);
            writer.WriteLine($"var result = proxy._csharpImpl!.{pascalMethodName}({argsString});");
            if (existentialReturnConv != null)
            {
                writer.WriteLine($"var swiftResult = {existentialReturnConv};");
                writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
            }
            else
            {
                writer.WriteLine("return MarshalToSwiftBuffer(result);");
            }
        }
        else
        {
            writer.WriteLine($"proxy._csharpImpl!.{pascalMethodName}({argsString});");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a dictionary conversion expression for receiver parameters.
    /// Receivers pass unmarshalled ABI types to the C# interface implementation, which expects
    /// IDictionary&lt;K,V&gt; (parameter form). GetReturnConversion uses .AsProjected() → IReadOnlyDictionary,
    /// which doesn't implement IDictionary. This method uses .ToDictionary() for eager materialization
    /// to produce a Dictionary&lt;K,V&gt; that satisfies the IDictionary contract.
    /// Returns null if the type is not a dictionary or doesn't need conversion.
    /// </summary>
    private string? GetReceiverDictionaryConversion(string rawArgName, TypeSpec? typeSpec, TypeConversionHandler typeConversionHandler)
    {
        if (typeSpec is not NamedTypeSpec namedType || !typeConversionHandler.IsSwiftDictionary(namedType))
            return null;

        if (namedType.GenericParameters.Count < 2)
            return null;

        var keySpec = namedType.GenericParameters[0];
        var valueSpec = namedType.GenericParameters[1];
        bool keyConverted = typeConversionHandler.IsDictionaryKeyTypeConverted(namedType);
        bool valueConverted = typeConversionHandler.IsDictionaryValueTypeConverted(namedType);

        // If no conversion needed for either key or value, check if value types differ
        // between ABI and public interface (e.g., AnyType → object for existentials)
        var publicValueType = GetCSharpTypeName(valueSpec, forAbiMarshalling: false);
        var abiValueType = GetCSharpTypeName(valueSpec, forAbiMarshalling: true);
        var publicKeyType = GetCSharpTypeName(keySpec, forAbiMarshalling: false);
        var abiKeyType = GetCSharpTypeName(keySpec, forAbiMarshalling: true);

        bool needsConversion = keyConverted || valueConverted || publicValueType != abiValueType || publicKeyType != abiKeyType;
        if (!needsConversion)
            return null;

        // Build key/value selector expressions for .ToDictionary()
        string keyExpr;
        if (typeConversionHandler.IsSwiftString(keySpec))
            keyExpr = "kvp.Key.ToString()";
        else if (publicKeyType != abiKeyType)
            keyExpr = $"({publicKeyType})kvp.Key";
        else
            keyExpr = "kvp.Key";

        string valueExpr;
        if (typeConversionHandler.IsSwiftString(valueSpec))
            valueExpr = "kvp.Value.ToString()";
        else if (valueSpec is NamedTypeSpec valArraySpec && typeConversionHandler.IsSwiftArray(valArraySpec))
        {
            // Nested array: project each value
            var innerElemSpec = valArraySpec.GenericParameters.FirstOrDefault();
            if (innerElemSpec != null && typeConversionHandler.IsSwiftString(innerElemSpec))
                valueExpr = "(IReadOnlyList<string>)kvp.Value.AsProjected(e => e.ToString())";
            else
                valueExpr = $"(IReadOnlyList<{GetCSharpTypeName(innerElemSpec)}>)kvp.Value";
        }
        else if (publicValueType != abiValueType)
        {
            // Check if value is an existential that needs proxy wrapping instead of a plain cast.
            // Skip if publicValueType is "object" — means unresolved protocol, no proxy class exists.
            var existentialHandler = new ExistentialHandler(_typeDatabase);
            if (publicValueType != "object" && existentialHandler.IsExistential(valueSpec))
            {
                var valProtocolList = existentialHandler.ToProtocolListTypeSpec(valueSpec);
                if (valProtocolList != null && existentialHandler.IsSupportedExistential(valProtocolList) &&
                    existentialHandler.GetPublicExistentialType(valProtocolList) != "object")
                {
                    if (existentialHandler.TryGetWellKnownProtocolType(valProtocolList, out var wkValType))
                        valueExpr = $"({publicValueType})new {wkValType}(kvp.Value)";
                    else
                    {
                        var valProxyName = existentialHandler.GetProxyClassName(valProtocolList);
                        valueExpr = $"({publicValueType})new {valProxyName}(kvp.Value)";
                    }
                }
                else
                    valueExpr = $"({publicValueType})kvp.Value";
            }
            else
                valueExpr = $"({publicValueType})kvp.Value";
        }
        else
            valueExpr = "kvp.Value";

        return $"{rawArgName}.ToDictionary(kvp => {keyExpr}, kvp => {valueExpr})";
    }

    /// <summary>
    /// Converts C# idiomatic value to Swift ABI for MarshalToSwiftBuffer in getter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first — they're more specific
        var existentialConv = GetReceiverExistentialGetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true });
        if (projection == null) return null;

        return projection switch
        {
            StringProjection => $"new SwiftString({varName})",
            NativeRemappedProjection nrp => nrp.FromFactoryMethod != null
                ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName})"
                : $"new {nrp.SwiftWrapperType}({varName})",
            ArrayProjection arr => GetReceiverArrayGetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictGetterConversion(dict, varName),
            OptionalProjection opt => GetReceiverOptionalGetterConversion(opt, varName),
            _ => null
        };
    }

    private string? GetReceiverArrayGetterConversion(ArrayProjection arr, string varName)
    {
        var rawElem = arr.ElementProjection.SwiftContainerGenericType;
        var elemConv = arr.ElementProjection.GetParameterElementConversion("e");
        if (elemConv != null)
            return $"SwiftArray<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftArray<{rawElem}>.FromEnumerable({varName})";
    }

    private string? GetReceiverDictGetterConversion(DictionaryProjection dict, string varName)
    {
        var rawK = dict.KeyProjection.SwiftContainerGenericType;
        var rawV = dict.ValueProjection.SwiftContainerGenericType;
        var keyConv = dict.KeyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetParameterElementConversion("kvp.Value");
        if (keyConv != null || valConv != null)
        {
            var keyExpr = keyConv ?? "kvp.Key";
            var valExpr = valConv ?? "kvp.Value";
            return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))";
        }
        return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName})";
    }

    private string? GetReceiverOptionalGetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        var optType = inner.SwiftContainerGenericType;
        return inner switch
        {
            StringProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(new SwiftString({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            NativeRemappedProjection nrp => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({(nrp.FromFactoryMethod != null ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName}Val)" : $"new {nrp.SwiftWrapperType}({varName}Val)")}) : SwiftOptional<{optType}>.NewNone())",
            ArrayProjection arr => BuildOptionalContainerGetterConversion(arr, varName, optType,
                GetReceiverArrayGetterConversion(arr, $"{varName}Val")),
            DictionaryProjection dict => BuildOptionalContainerGetterConversion(dict, varName, optType,
                GetReceiverDictGetterConversion(dict, $"{varName}Val")),
            // Closures have their own ABI (SwiftClosureData/function pointers) — can't wrap in SwiftOptional.
            // Passthrough; accessor methods handle closure marshalling.
            ClosureProjection => null,
            // Class/NonFrozenStruct: optType is IntPtr (PInvokeType), but varName is the public C# type.
            // Extract IntPtr via .Payload.DangerousGetHandle() — matches GetParameterElementConversion.
            ClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Payload.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            NonFrozenStructProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Payload.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            // Blittable, SimpleEnum, etc. — MarshalToSwiftBuffer writes raw bytes via Unsafe.Write<T>,
            // so C# int? (Nullable<int>) is NOT layout-compatible with SwiftOptional<int> (a class).
            // Must explicitly wrap in SwiftOptional<T>.NewSome/NewNone.
            _ => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val) : SwiftOptional<{optType}>.NewNone())"
        };
    }

    private static string? BuildOptionalContainerGetterConversion(ITypeProjection inner, string varName, string optType, string? innerConv)
    {
        if (innerConv == null) return null;
        return $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({innerConv}) : SwiftOptional<{optType}>.NewNone())";
    }

    /// <summary>
    /// Converts Swift ABI value to C# idiomatic for interface assignment in setter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first
        var existentialConv = GetReceiverExistentialSetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false });
        if (projection == null) return null;

        return projection switch
        {
            StringProjection => $"{varName}.ToString()",
            NativeRemappedProjection nrp => $"{varName}.{nrp.ToConversionMethod}()",
            ArrayProjection arr => GetReceiverArraySetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictSetterConversion(dict, varName),
            OptionalProjection opt => GetReceiverOptionalSetterConversion(opt, varName),
            _ => null
        };
    }

    private string? GetReceiverArraySetterConversion(ArrayProjection arr, string varName)
    {
        var elemConv = arr.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.AsProjected(e => {elemConv})";
        return null;  // SwiftArray<T> IS IReadOnlyList<T> — no conversion needed
    }

    private string? GetReceiverDictSetterConversion(DictionaryProjection dict, string varName)
    {
        var keyConv = dict.KeyProjection.GetReturnElementConversion("k");
        var valConv = dict.ValueProjection.GetReturnElementConversion("v");
        if (keyConv == null && valConv == null) return null;
        if (keyConv != null)
        {
            var reverseKeyConv = dict.KeyProjection.GetParameterElementConversion("k") ?? "k";
            var valSelector = valConv != null ? $"v => {valConv}" : "v => v";
            return $"{varName}.AsProjected(k => {keyConv}, k => {reverseKeyConv}, {valSelector})";
        }
        return $"{varName}.AsProjected(v => {valConv})";
    }

    private string? GetReceiverOptionalSetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        return inner switch
        {
            StringProjection => $"((SwiftString?){varName})?.ToString()",
            NativeRemappedProjection nrp => $"(({nrp.SwiftWrapperType}?){varName})?.{nrp.ToConversionMethod}()",
            ArrayProjection arr => GetReceiverOptionalContainerSetterConversion(arr, varName, arr.PublicType),
            DictionaryProjection dict => GetReceiverOptionalContainerSetterConversion(dict, varName, dict.PublicType),
            // Closures have their own ABI — passthrough, accessor methods handle marshalling.
            ClosureProjection => null,
            // Class/NonFrozenStruct: the Optional is already deserialized as SwiftOptional<PublicType>
            // via MarshalFromSwift — .Some returns PublicType, not IntPtr. Simple nullable cast suffices.
            _ => $"(({inner.PublicType}?){varName})"
        };
    }

    private string? GetReceiverOptionalContainerSetterConversion(ITypeProjection innerContainer, string varName, string idiomaticType)
    {
        var containerConv = innerContainer.GetReturnContainerConversion($"{varName}.Some");
        var someExpr = containerConv ?? $"{varName}.Some";
        return $"({varName}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType}?)null : {someExpr})";
    }

    /// <summary>
    /// Gets a conversion expression for existential types in getter returns (C# idiomatic → Swift ABI).
    /// Uses TypeProjectionFactory to project the type, then extracts parameter element conversions
    /// (public → ABI direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true });
        if (projection == null) return null;

        // Standalone existential
        if (projection is ExistentialProjection existProj)
            return existProj.GetParameterElementConversion(varName);

        // Optional<existential>
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var containerType = innerExist.PInvokeType;
            var extractExpr = innerExist.GetParameterElementConversion($"{varName}Val");
            return $"({varName} is {{}} {varName}Val ? SwiftOptional<{containerType}>.NewSome({extractExpr}) : SwiftOptional<{containerType}>.NewNone())";
        }

        // Array<existential>
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var containerType = arrExist.PInvokeType;
            var elemConv = arrExist.GetParameterElementConversion("i");
            return $"SwiftArray<{containerType}>.FromEnumerable({varName}.Select(i => {elemConv}))";
        }

        // Dictionary<K, existential>
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var containerType = dictExist.PInvokeType;
            var keyConv = dictProj.KeyProjection.GetParameterElementConversion("kvp.Key");
            var keyExpr = keyConv ?? "kvp.Key";
            var valConv = dictExist.GetParameterElementConversion("kvp.Value");
            var abiKeyType = dictProj.KeyProjection.SwiftContainerGenericType;
            return $"SwiftDictionary<{abiKeyType}, {containerType}>.FromDictionary({varName}.ToDictionary(kvp => {keyExpr}, kvp => {valConv}))";
        }

        return null;
    }

    /// <summary>
    /// Gets a conversion expression for existential types in setter params (Swift ABI → C# idiomatic).
    /// Uses TypeProjectionFactory to project the type, then extracts return element conversions
    /// (ABI → public direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false });
        if (projection == null) return null;

        // Standalone existential
        if (projection is ExistentialProjection existProj)
            return existProj.GetReturnElementConversion(varName);

        // Optional<existential>
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var publicType = innerExist.PublicType;
            var wrapExpr = innerExist.GetReturnElementConversion($"{varName}.Some");
            return $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : ({publicType}?){wrapExpr})";
        }

        // Array<existential>
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var publicType = arrExist.PublicType;
            var elemConv = arrExist.GetReturnElementConversion("c");
            return $"{varName}.AsProjected<{publicType}>(c => {elemConv})";
        }

        // Dictionary<K, existential>
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var publicType = dictExist.PublicType;
            var valConv = dictExist.GetReturnElementConversion("kvp.Value");
            var keyConv = dictProj.KeyProjection.GetReturnElementConversion("kvp.Key");
            var keyExpr = keyConv ?? "kvp.Key";
            return $"{varName}.ToDictionary(kvp => {keyExpr}, kvp => ({publicType}){valConv})";
        }

        return null;
    }

    /// <summary>
    /// Overrides the ABI type name for Optional&lt;existential&gt; types.
    /// Uses TypeProjectionFactory to determine if the type is Optional&lt;existential&gt;,
    /// and if so, returns the correct <c>SwiftOptional&lt;ExistentialContainer{N}&gt;</c> type.
    /// Returns the original type name if no override is needed.
    /// </summary>
    private string OverrideOptionalExistentialAbiType(string abiTypeName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return abiTypeName;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false });
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
            return $"SwiftOptional<{innerExist.PInvokeType}>";

        return abiTypeName;
    }

    private void EmitConstructors(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Constructor for C# implementation
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy wrapping a C# implementation of {{interfaceName}}.
            /// </summary>
            /// <param name="implementation">The C# implementation of the protocol.</param>
            public {{proxyClassName}}({{interfaceName}} implementation)
            {
                _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
                _everyProtocol = new EveryProtocol();

                // Create existential container manually
                // The container holds: payload (EveryProtocol pointer), metadata, and witness table
                _swiftContainer = new ExistentialContainer1();
                _swiftContainer.Payload0 = _everyProtocol.Handle;
                _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
                _swiftContainer[0] = ProtocolWitnessTableHandle;

                // Register this proxy so Swift callbacks can find us
                SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
            }

            /// <summary>
            /// Creates a proxy from an existing Swift existential container.
            /// Use this when receiving protocol values from Swift code.
            /// </summary>
            /// <remarks>
            /// Swift-backed proxies created with this constructor dispatch blittable and String
            /// protocol members through witness table accessors. Non-dispatchable members
            /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
            /// </remarks>
            /// <param name="container">The Swift existential container.</param>
            internal {{proxyClassName}}(ExistentialContainer1 container)
            {
                _swiftContainer = container;
                _csharpImpl = null;
                _everyProtocol = null;
            }

            """);
    }
}
