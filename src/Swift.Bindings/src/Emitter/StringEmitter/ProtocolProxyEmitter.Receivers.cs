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
        var abiTypeName = GetCSharpTypeName(property.SwiftTypeSpec, forAbiMarshalling: true);

        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // The interface property uses idiomatic C# types (e.g., string, string?, IReadOnlyList<string>)
                // but MarshalToSwiftBuffer expects Swift ABI types (SwiftString, SwiftOptional<SwiftString>, etc.).
                // Use GetParameterConversion to convert the idiomatic value back to the Swift wrapper type.
                var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
                var getterConversion = typeConversionHandler.GetParameterConversion("result", property.SwiftTypeSpec);

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
                    var existentialGetterConv = GetReceiverExistentialGetterConversion("result", property.SwiftTypeSpec);
                    if (existentialGetterConv != null)
                    {
                        writer.WriteLine($"var swiftResult = {existentialGetterConv};");
                        writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                    }
                    else
                    {
                        writer.WriteLine("return MarshalToSwiftBuffer(result);");
                    }
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
                // The receiver marshals the Swift ABI type, but the interface uses the idiomatic C# type
                var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
                var returnConversion = typeConversionHandler.GetReturnConversion("value", property.SwiftTypeSpec);
                var existentialSetterConv = returnConversion == null
                    ? GetReceiverExistentialSetterConversion("value", property.SwiftTypeSpec)
                    : null;
                var assignmentExpr = returnConversion ?? existentialSetterConv ?? "value";

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
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec, forAbiMarshalling: true);
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
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
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
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
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
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
            var rawArgName = $"rawParam{argIndex}";
            var argName = $"param{argIndex}";

            // Dictionaries need special handling in receiver context: the interface declares
            // IDictionary<K,V> (parameter form), but GetReturnConversion produces .AsProjected()
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
            var returnConversion = typeConversionHandler.GetReturnConversion(rawArgName, param.SwiftTypeSpec);
            if (returnConversion != null)
            {
                writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {returnConversion};");
            }
            else
            {
                var existentialParamConv = GetReceiverExistentialSetterConversion(rawArgName, param.SwiftTypeSpec);
                if (existentialParamConv != null)
                {
                    writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                    writer.WriteLine($"var {argName} = {existentialParamConv};");
                }
                else
                {
                writer.WriteLine($"var {argName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                }
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
    /// Gets a conversion expression for existential types in getter returns (C# idiomatic → Swift ABI).
    /// Converts interface types (IProtocol, IReadOnlyList&lt;IProtocol&gt;) back to existential containers
    /// that MarshalToSwiftBuffer expects.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return null;

        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Standalone existential: cast to ISwiftExistentialConvertible and extract container
        if (existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList == null || !existentialHandler.IsSupportedExistential(protocolList))
                return null;
            var publicType = existentialHandler.GetPublicExistentialType(protocolList);
            if (publicType == "object")
                return null;
            var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
            return $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){varName}).GetExistentialContainer()";
        }

        // Array<existential>: project each element via GetExistentialContainer
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
        if (typeSpec is NamedTypeSpec namedType && typeConversionHandler.IsSwiftArray(namedType))
        {
            var elementSpec = namedType.GenericParameters.FirstOrDefault();
            if (elementSpec != null && existentialHandler.IsExistential(elementSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(elementSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                    if (publicType == "object")
                        return null;
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    return $"SwiftArray<{containerType}>.FromEnumerable({varName}.Select(i => ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>)i).GetExistentialContainer()))";
                }
            }
        }

        // Dictionary<K, existential>: project values via GetExistentialContainer
        if (typeSpec is NamedTypeSpec dictType && typeConversionHandler.IsSwiftDictionary(dictType) && dictType.GenericParameters.Count >= 2)
        {
            var valueSpec = dictType.GenericParameters[1];
            if (existentialHandler.IsExistential(valueSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(valueSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                    if (publicType == "object")
                        return null;
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    var keySpec = dictType.GenericParameters[0];
                    var keyExpr = typeConversionHandler.IsSwiftString(keySpec) ? "new SwiftString(kvp.Key)" : "kvp.Key";
                    return $"SwiftDictionary<{GetCSharpTypeName(keySpec, forAbiMarshalling: true)}, {containerType}>.FromDictionary({varName}.ToDictionary(kvp => {keyExpr}, kvp => ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>)kvp.Value).GetExistentialContainer()))";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a conversion expression for existential types in setter params (Swift ABI → C# idiomatic).
    /// Converts existential containers to proxy/interface types that the C# implementation expects.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return null;

        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Standalone existential: wrap container in proxy
        if (existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList == null || !existentialHandler.IsSupportedExistential(protocolList))
                return null;
            var publicType = existentialHandler.GetPublicExistentialType(protocolList);
            if (publicType == "object")
                return null;

            if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wkType))
                return $"new {wkType}({varName})";
            var proxyName = existentialHandler.GetProxyClassName(protocolList);
            return $"new {proxyName}({varName})";
        }

        // Array<existential>: project each element via proxy constructor
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
        if (typeSpec is NamedTypeSpec namedType && typeConversionHandler.IsSwiftArray(namedType))
        {
            var elementSpec = namedType.GenericParameters.FirstOrDefault();
            if (elementSpec != null && existentialHandler.IsExistential(elementSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(elementSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                    if (publicType == "object")
                        return null;

                    string elementProjection;
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wkType))
                        elementProjection = $"new {wkType}(c)";
                    else
                        elementProjection = $"new {existentialHandler.GetProxyClassName(protocolList)}(c)";
                    return $"{varName}.AsProjected<{publicType}>(c => {elementProjection})";
                }
            }
        }

        // Dictionary<K, existential>: project values via proxy constructor
        if (typeSpec is NamedTypeSpec dictType && typeConversionHandler.IsSwiftDictionary(dictType) && dictType.GenericParameters.Count >= 2)
        {
            var valueSpec = dictType.GenericParameters[1];
            if (existentialHandler.IsExistential(valueSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(valueSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                    if (publicType == "object")
                        return null;

                    string valueProjection;
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wkType))
                        valueProjection = $"({publicType})new {wkType}(kvp.Value)";
                    else
                        valueProjection = $"({publicType})new {existentialHandler.GetProxyClassName(protocolList)}(kvp.Value)";

                    var keySpec = dictType.GenericParameters[0];
                    var publicKeyType = GetCSharpTypeName(keySpec, forAbiMarshalling: false);
                    var abiKeyType = GetCSharpTypeName(keySpec, forAbiMarshalling: true);
                    string keyExpr = typeConversionHandler.IsSwiftString(keySpec) ? "kvp.Key.ToString()" : (publicKeyType != abiKeyType ? $"({publicKeyType})kvp.Key" : "kvp.Key");
                    return $"{varName}.ToDictionary(kvp => {keyExpr}, kvp => {valueProjection})";
                }
            }
        }

        return null;
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
