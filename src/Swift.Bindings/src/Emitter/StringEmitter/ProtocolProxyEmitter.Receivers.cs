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
                // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                var getterConversion = GetReceiverGetterConversion("result", property.SwiftTypeSpec);
                // F1: If property is narrowed (int/uint), widen back to nint/nuint for Swift ABI MarshalToSwiftBuffer.
                // Plain nint: result is int → (nint)result ensures 8-byte write.
                // Plain nuint: result is uint → (nuint)result ensures 8-byte write.
                // Optional<nint/nuint>: getterConversion builds SwiftOptional<nint>.NewSome(resultVal) where
                //   resultVal is int/uint (unwrapped from int?/uint?) — implicit widening handles it.
                if (getterConversion == null && NativeIntOverloadEmitter.TryGetAbiWideningType(property.SwiftTypeSpec, out var abiType))
                    getterConversion = $"({abiType})result";

                // String returns use Utf8Slice encoding to avoid ARC issues with MarshalToSwiftBuffer<SwiftString>.
                // SwiftString contains ARC-managed references that Unsafe.Write can't retain properly,
                // causing crashes when Swift reads the result. Utf8Slice passes raw bytes safely.
                bool isStringReturn = IsStringTypeSpec(property.SwiftTypeSpec);

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                // Dead-impl safe: use TryGetProxyFromContainer so an Unregister'd handle
                // (e.g. after user Dispose) is a silent no-op instead of throwing
                // InvalidOperationException across the [UnmanagedCallersOnly] boundary.
                // The impl weak-reference unwrap may ALSO return null if the user dropped
                // the impl while Swift still holds a strong retain on the proxy (Codex P0);
                // in that case we return a zero-filled buffer rather than NRE-crash.
                //
                // Codex P1 #1: the buffer size MUST match the carrier the success path uses
                // for MarshalToSwiftBuffer<T>(...). When a getter conversion is present the
                // carrier is e.g. SwiftOptional<bool>, NOT bool? — using the idiomatic type
                // here would hand Swift a too-small buffer and corrupt the receiver boundary.
                // Use the projection-derived carrier when available, fall back to the public
                // (idiomatic) interface property type for the no-conversion branch.
                var publicPropertyTypeName = GetCSharpTypeName(property.SwiftTypeSpec);
                var carrierTypeName = getterConversion != null
                    ? (GetReceiverGetterCarrierType(property.SwiftTypeSpec) ?? publicPropertyTypeName)
                    : publicPropertyTypeName;
                var nullReturnStr = isStringReturn
                    ? "MarshalStringToUtf8Slice(string.Empty)"
                    : $"(IntPtr)NativeMemory.AllocZeroed((nuint)Unsafe.SizeOf<{carrierTypeName}>())";
                writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxyFromContainer<{proxyClassName}>(container, out var proxy) || proxy is null)");
                writer.Indent++;
                writer.WriteLine($"return {nullReturnStr};");
                writer.Indent--;
                writer.WriteLine("var impl = proxy._csharpImpl;");
                writer.WriteLine("if (impl is null)");
                writer.Indent++;
                writer.WriteLine($"return {nullReturnStr};");
                writer.Indent--;
                writer.WriteLine($"var result = impl.{pascalPropertyName};");
                if (isStringReturn)
                {
                    writer.WriteLine("return MarshalStringToUtf8Slice(result);");
                }
                else if (getterConversion != null)
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
                // F1: Narrow nint/nuint ABI value to int/uint for property assignment.
                // Plain nint: value is nint (MarshalFromSwift<nint>) → (int)value.
                // Optional<nint>: returnConversion is "((nint?)value)" → (int?)((nint?)value).
                if (NativeIntOverloadEmitter.TryGetNarrowedType(property.SwiftTypeSpec, out var narrowedType))
                    assignmentExpr = $"({narrowedType}){assignmentExpr}";

                // String property: local MarshalFromSwift<SwiftString> uses Unsafe.Read which
                // can't construct a managed SwiftString from raw Swift memory. Use runtime marshaller.
                var marshalExpr = IsStringTypeSpec(property.SwiftTypeSpec)
                    ? $"global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr)"
                    : $"MarshalFromSwift<{abiTypeName}>(valuePtr)";

                writer.WriteLines($$"""
                    [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                    private static void {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)
                    {
                        var container = *(ExistentialContainer1*)selfContainer;
                        // Dead-impl safe: silently drop the write if the proxy is unregistered
                        // or the managed impl has already been GC'd. A throw here would propagate
                        // across the [UnmanagedCallersOnly] boundary and terminate the process.
                        if (!SwiftObjectRegistry.TryGetProxyFromContainer<{{proxyClassName}}>(container, out var proxy) || proxy is null)
                            return;
                        var impl = proxy._csharpImpl;
                        if (impl is null)
                            return;
                        var value = {{marshalExpr}};
                        impl.{{pascalPropertyName}} = {{assignmentExpr}};
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

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                // Dead-impl safe: return zeroed buffer rather than throwing on missing
                // proxy / GC'd impl (Codex P0 + P1 #3 — [UnmanagedCallersOnly] cannot throw).
                // Codex P1 #1: size the fallback by the carrier the success path uses for
                // MarshalToSwiftBuffer<T>(...), not by the idiomatic interface type.
                var subscriptIsString = IsStringTypeSpec(subscript.ReturnTypeSpec);
                var subscriptGetterConvForSizing = GetReceiverExistentialGetterConversion("result", subscript.ReturnTypeSpec)
                    ?? GetReceiverGetterConversion("result", subscript.ReturnTypeSpec);
                var subscriptPublicReturnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec);
                var subscriptCarrierTypeName = subscriptGetterConvForSizing != null
                    ? (GetReceiverGetterCarrierType(subscript.ReturnTypeSpec) ?? subscriptPublicReturnTypeName)
                    : subscriptPublicReturnTypeName;
                var subscriptNullReturnStr = subscriptIsString
                    ? "MarshalStringToUtf8Slice(string.Empty)"
                    : $"(IntPtr)NativeMemory.AllocZeroed((nuint)Unsafe.SizeOf<{subscriptCarrierTypeName}>())";
                writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxyFromContainer<{proxyClassName}>(container, out var proxy) || proxy is null)");
                writer.Indent++;
                writer.WriteLine($"return {subscriptNullReturnStr};");
                writer.Indent--;
                writer.WriteLine("var impl = proxy._csharpImpl;");
                writer.WriteLine("if (impl is null)");
                writer.Indent++;
                writer.WriteLine($"return {subscriptNullReturnStr};");
                writer.Indent--;

                // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                    if (IsStringTypeSpec(param.SwiftTypeSpec))
                        writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                    else
                        writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"var result = impl[{indexArgs}];");
                var subscriptGetterConv = GetReceiverExistentialGetterConversion("result", subscript.ReturnTypeSpec)
                    ?? GetReceiverGetterConversion("result", subscript.ReturnTypeSpec);
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

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                // Dead-impl safe: silently drop the write if the proxy is unregistered
                // or the impl has been GC'd.
                writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxyFromContainer<{proxyClassName}>(container, out var proxy) || proxy is null)");
                writer.Indent++;
                writer.WriteLine("return;");
                writer.Indent--;
                writer.WriteLine("var impl = proxy._csharpImpl;");
                writer.WriteLine("if (impl is null)");
                writer.Indent++;
                writer.WriteLine("return;");
                writer.Indent--;
                if (IsStringTypeSpec(subscript.ReturnTypeSpec))
                {
                    writer.WriteLine($"var value = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr).ToString();");
                }
                else
                {
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
                }

                // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                    if (IsStringTypeSpec(param.SwiftTypeSpec))
                        writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                    else
                        writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"impl[{indexArgs}] = value;");

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

        var nonEmptyParams = method.CSSignature.Skip(1)
            .Where(p => !DefaultParameterOverloadEmitter.IsDebugParameter(p) && !p.SwiftTypeSpec.IsEmptyTuple)
            .ToList();
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
            nonEmptyParams.Select((p, i) => $", IntPtr rawArg{i}"));

        var csharpReturnType = hasReturn ? "IntPtr" : "void";

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
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
        // Dead-impl safe: use TryGetProxyFromContainer + impl null check. A throw across
        // the [UnmanagedCallersOnly] boundary is process-terminating, so a GC'd impl or
        // unregistered proxy silently returns a default value instead (Codex P0 + P1 #3).
        //
        // Codex P1 #1: size the null-path buffer by the SAME carrier the success path
        // marshals via MarshalToSwiftBuffer<T>(...). When a return conversion is present
        // the carrier is e.g. SwiftOptional<bool> (8 bytes) — using `Unsafe.SizeOf<bool?>`
        // (2 bytes) here would hand Swift a too-small buffer and corrupt the boundary.
        bool isStringMethodReturnForNullPath = hasReturn && !method.IsAsync && IsStringTypeSpec(returnType!);
        string? methodReturnConvForSizing = null;
        if (hasReturn && !method.IsAsync)
        {
            methodReturnConvForSizing = GetReceiverExistentialGetterConversion("result", returnType!)
                ?? GetReceiverGetterConversion("result", returnType!);
        }
        var methodCarrierTypeName = methodReturnConvForSizing != null
            ? (GetReceiverGetterCarrierType(returnType!) ?? returnTypeName)
            : returnTypeName;

        string methodNullReturnExpr;
        if (!hasReturn)
        {
            methodNullReturnExpr = "return;";
        }
        else if (isStringMethodReturnForNullPath)
        {
            methodNullReturnExpr = "return MarshalStringToUtf8Slice(string.Empty);";
        }
        else
        {
            methodNullReturnExpr = $"return (IntPtr)NativeMemory.AllocZeroed((nuint)Unsafe.SizeOf<{methodCarrierTypeName}>());";
        }
        writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxyFromContainer<{proxyClassName}>(container, out var proxy) || proxy is null)");
        writer.Indent++;
        writer.WriteLine(methodNullReturnExpr);
        writer.Indent--;
        writer.WriteLine("var impl = proxy._csharpImpl;");
        writer.WriteLine("if (impl is null)");
        writer.Indent++;
        writer.WriteLine(methodNullReturnExpr);
        writer.Indent--;

        // Unmarshal parameters - use param{i} for local variable names to avoid conflicts with rawArg{i}
        // B10: After unmarshalling, apply type conversion from ABI to idiomatic C# types
        // (e.g., SwiftOptional<SwiftString> → string?) to match the interface method signature.
        // P0: Use ABI types for MarshalFromSwift — idiomatic types (string, bool?) can't read Swift memory.
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in nonEmptyParams)
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
            var rawArgName = $"rawParam{argIndex}";
            var argName = $"param{argIndex}";

            // String parameter: the local MarshalFromSwift<SwiftString> helper uses Unsafe.Read<T>
            // which can't construct a managed SwiftString from raw Swift memory (16-byte value).
            // Use the runtime's SwiftMarshal.MarshalFromSwift which calls NewFromPayload.
            if (IsStringTypeSpec(param.SwiftTypeSpec))
            {
                writer.WriteLine($"var {rawArgName} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {rawArgName}.ToString();");
            }
            // Dictionaries need special handling in receiver context: the interface declares
            // IDictionary<K,V> (parameter form), but projection produces .AsProjected()
            // which returns IReadOnlyDictionary<K,V> (return form). IReadOnlyDictionary doesn't
            // implement IDictionary, so we must use .ToDictionary() for eager materialization.
            else if (GetReceiverDictionaryConversion(rawArgName, param.SwiftTypeSpec) is string receiverDictConversion)
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

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        // Mirror the property-collision rename applied during interface emission
        // (ProtocolProxyEmitter.InterfaceImpl.cs L62–L88). Use the canonical cached
        // set populated by ProtocolHandler / InterfacePropertyNamePrecomputer so the
        // receiver's view matches what the interface actually emits — including
        // emitted static abstract property names and excluding skipped instance
        // properties. Without this, the receiver invokes `impl.RichText(range)`
        // for a method that the interface emitted as `RichTextMethod(range)`
        // because a same-named property took the PascalCased slot (CS1955), and
        // static-property collisions are missed while skipped-property collisions
        // are over-applied.
        var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> receiverPropertyNames;
        if (canonicalPropertyNames != null)
        {
            receiverPropertyNames = new HashSet<string>(canonicalPropertyNames);
        }
        else
        {
            // Defensive fallback: the prepass populates the cache for every protocol in
            // the module, so this branch should not trigger in practice. Mirror the
            // canonical construction (instance + emitted static).
            receiverPropertyNames = new HashSet<string>();
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic)
                {
                    if (_staticAbstractPropertyNames.Contains(property.Name))
                        receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
                else if (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name))
                {
                    receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
            }
        }
        var pascalMethodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: receiverPropertyNames,
            isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        if (hasReturn)
        {
            // String returns use Utf8Slice encoding to avoid ARC issues with SwiftString.
            // Skip async methods — their C# return is Task<string>, not string.
            bool isStringMethodReturn = !method.IsAsync && IsStringTypeSpec(returnType!);
            var existentialReturnConv = GetReceiverExistentialGetterConversion("result", returnType!);
            // Fall back to regular getter conversion for ObjC-bridgeable, Date, NativeRemapped, etc.
            // Without this, method returns of e.g. Foundation.NSUrl write a managed reference via
            // MarshalToSwiftBuffer instead of extracting .Handle (the ObjC pointer Swift expects).
            // Skip async methods — their C# return is Task<T>, not T, so .Handle doesn't apply.
            var returnConv = existentialReturnConv
                ?? (method.IsAsync ? null : GetReceiverGetterConversion("result", returnType!));
            writer.WriteLine($"var result = impl.{pascalMethodName}({argsString});");
            if (isStringMethodReturn)
            {
                writer.WriteLine("return MarshalStringToUtf8Slice(result);");
            }
            else if (returnConv != null)
            {
                writer.WriteLine($"var swiftResult = {returnConv};");
                writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
            }
            else
            {
                writer.WriteLine("return MarshalToSwiftBuffer(result);");
            }
        }
        else
        {
            writer.WriteLine($"impl.{pascalMethodName}({argsString});");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a dictionary conversion expression for receiver parameters using projections.
    /// Receivers pass unmarshalled ABI types to the C# interface implementation, which expects
    /// IDictionary&lt;K,V&gt; (parameter form). Projection-based .AsProjected() returns IReadOnlyDictionary,
    /// which doesn't implement IDictionary. This method uses .ToDictionary() for eager materialization
    /// to produce a Dictionary&lt;K,V&gt; that satisfies the IDictionary contract.
    /// Returns null if the type is not a dictionary or doesn't need conversion.
    /// </summary>
    private string? GetReceiverDictionaryConversion(string rawArgName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false });
        if (projection is not DictionaryProjection dict) return null;

        var keyConv = dict.KeyProjection.GetReturnElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetReturnElementConversion("kvp.Value");

        // SwiftDictionary<K,V> implements IReadOnlyDictionary, not IDictionary.
        // Receiver params need IDictionary, so always materialize via .ToDictionary().
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";

        // Cast values to the public interface type to satisfy invariant Dictionary<K,V>.
        // e.g., Dictionary<string, RowAdapterProxy> doesn't satisfy IDictionary<string, IRowAdapter>
        // even though RowAdapterProxy : IRowAdapter, because Dictionary is invariant.
        var valPubType = dict.ValueProjection.PublicType;
        var keyPubType = dict.KeyProjection.PublicType;
        return $"{rawArgName}.ToDictionary(kvp => ({keyPubType}){keyExpr}, kvp => ({valPubType}){valueExpr})";
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
            DataProjection => $"Swift.Foundation.Data.FromByteArray({varName})",
            DateProjection => $"({varName} - {DateProjection.SwiftEpoch}).TotalSeconds",
            NativeRemappedProjection nrp => nrp.FromFactoryMethod != null
                ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName})"
                : $"new {nrp.SwiftWrapperType}({varName})",
            ObjCBridgedProjection => $"{varName}.Handle",
            ObjCBridgeableProjection => $"{varName}.Handle",
            ObjCRootedClassProjection => $"{varName}.Handle",
            ArrayProjection arr => GetReceiverArrayGetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictGetterConversion(dict, varName),
            SetProjection set => GetReceiverSetGetterConversion(set, varName),
            OptionalProjection opt => GetReceiverOptionalGetterConversion(opt, varName),
            _ => null
        };
    }

    private string? GetReceiverSetGetterConversion(SetProjection set, string varName)
    {
        var rawElem = set.ElementProjection.SwiftContainerGenericType;
        var elemConv = set.ElementProjection.GetParameterElementConversion("e");
        if (elemConv != null)
            return $"SwiftSet<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftSet<{rawElem}>.FromEnumerable({varName})";
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
            DataProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(Swift.Foundation.Data.FromByteArray({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            DateProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(({varName}Val - {DateProjection.SwiftEpoch}).TotalSeconds) : SwiftOptional<{optType}>.NewNone())",
            NativeRemappedProjection nrp => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({(nrp.FromFactoryMethod != null ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName}Val)" : $"new {nrp.SwiftWrapperType}({varName}Val)")}) : SwiftOptional<{optType}>.NewNone())",
            ObjCBridgedProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
            ObjCBridgeableProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
            ArrayProjection arr => BuildOptionalContainerGetterConversion(arr, varName, optType,
                GetReceiverArrayGetterConversion(arr, $"{varName}Val")),
            DictionaryProjection dict => BuildOptionalContainerGetterConversion(dict, varName, optType,
                GetReceiverDictGetterConversion(dict, $"{varName}Val")),
            SetProjection set => BuildOptionalContainerGetterConversion(set, varName, optType,
                GetReceiverSetGetterConversion(set, $"{varName}Val")),
            // Closures have their own ABI (SwiftClosureData/function pointers) — can't wrap in SwiftOptional.
            // Passthrough; accessor methods handle closure marshalling.
            ClosureProjection => null,
            // ObjC-rooted classes use .Handle (ObjC pointer), not .Payload
            ObjCRootedClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
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
            DataProjection => $"{varName}.ToByteArray()",
            DateProjection => $"{DateProjection.SwiftEpoch}.AddSeconds({varName})",
            NativeRemappedProjection nrp => $"{varName}.{nrp.ToConversionMethod}()",
            ObjCBridgedProjection objc => MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, varName, nonNull: true),
            ObjCBridgeableProjection objc => MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, varName, nonNull: true),
            ArrayProjection arr => GetReceiverArraySetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictSetterConversion(dict, varName),
            SetProjection set => GetReceiverSetSetterConversion(set, varName),
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

    private string? GetReceiverSetSetterConversion(SetProjection set, string varName)
    {
        var elemConv = set.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.Select(e => {elemConv}).ToHashSet()";
        return null;  // SwiftSet<T> IS IReadOnlySet<T> — no conversion needed
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
            DataProjection => $"((Swift.Foundation.Data?){varName})?.ToByteArray()",
            DateProjection => $"((double?){varName}) is {{}} {varName}DateVal ? (System.DateTimeOffset?){DateProjection.SwiftEpoch}.AddSeconds({varName}DateVal) : null",
            NativeRemappedProjection nrp => $"(({nrp.SwiftWrapperType}?){varName})?.{nrp.ToConversionMethod}()",
            ObjCBridgedProjection objc => $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, $"{varName}.Some", nonNull: true)})",
            ObjCBridgeableProjection objc => $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, $"{varName}.Some", nonNull: true)})",
            ArrayProjection arr => GetReceiverOptionalContainerSetterConversion(arr, varName, arr.PublicType),
            DictionaryProjection dict => GetReceiverOptionalContainerSetterConversion(dict, varName, dict.PublicType),
            SetProjection set => GetReceiverOptionalContainerSetterConversion(set, varName, set.PublicType),
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
        // Cast the some arm to the idiomatic type to avoid ternary covariance issues.
        // e.g., Dictionary<string, MixpanelTypeProxy> vs IReadOnlyDictionary<string, IMixpanelType>
        return $"({varName}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType}?)null : ({idiomaticType}){someExpr})";
    }

    /// <summary>
    /// Returns the C# type name that the success-path
    /// <c>MarshalToSwiftBuffer&lt;T&gt;(swiftResult)</c> call would use as <c>T</c>, or
    /// <c>null</c> if the success path takes the no-conversion branch
    /// (<c>MarshalToSwiftBuffer(result)</c> with the idiomatic interface type).
    /// <para>
    /// This MUST stay in lockstep with <see cref="GetReceiverGetterConversion"/> and
    /// <see cref="GetReceiverExistentialGetterConversion"/> — the dead-impl null path
    /// uses <c>Unsafe.SizeOf&lt;CarrierType&gt;()</c> to allocate a fallback buffer of
    /// the SAME size the success path would emit. If the carrier here drifts from the
    /// success path's carrier, the fallback buffer is the wrong size and Swift reads
    /// garbage memory across the receiver boundary (Codex P1 #1).
    /// </para>
    /// </summary>
    private string? GetReceiverGetterCarrierType(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true });
        if (projection == null) return null;

        // Existential carriers — must mirror GetReceiverExistentialGetterConversion's order.
        if (projection is ExistentialProjection existProj)
            return existProj.PInvokeType;

        if (projection is OptionalProjection optExist && optExist.InnerProjection is ExistentialProjection innerExist)
            return $"SwiftOptional<{innerExist.PInvokeType}>";

        if (projection is ArrayProjection arrExistProj && arrExistProj.ElementProjection is ExistentialProjection arrExist)
            return $"SwiftArray<{arrExist.PInvokeType}>";

        if (projection is DictionaryProjection dictExistProj && dictExistProj.ValueProjection is ExistentialProjection dictExist)
        {
            var abiKeyType = dictExistProj.KeyProjection.SwiftContainerGenericType;
            return $"SwiftDictionary<{abiKeyType}, {dictExist.PInvokeType}>";
        }

        // Non-existential carriers — must mirror GetReceiverGetterConversion's switch.
        return projection switch
        {
            // StringProjection is special-cased to Utf8Slice in the receiver — never reaches MarshalToSwiftBuffer.
            DataProjection => "Swift.Foundation.Data",
            DateProjection => "double",
            NativeRemappedProjection nrp => nrp.SwiftWrapperType,
            ObjCBridgedProjection => "IntPtr",
            ObjCBridgeableProjection => "IntPtr",
            ObjCRootedClassProjection => "IntPtr",
            ArrayProjection arr => $"SwiftArray<{arr.ElementProjection.SwiftContainerGenericType}>",
            DictionaryProjection dict => $"SwiftDictionary<{dict.KeyProjection.SwiftContainerGenericType}, {dict.ValueProjection.SwiftContainerGenericType}>",
            SetProjection set => $"SwiftSet<{set.ElementProjection.SwiftContainerGenericType}>",
            OptionalProjection opt => $"SwiftOptional<{opt.InnerProjection.SwiftContainerGenericType}>",
            // No conversion → success path uses MarshalToSwiftBuffer(result) with the idiomatic type.
            // Caller falls back to that type for sizing.
            _ => null
        };
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
    /// Checks if a TypeSpec represents Swift.String.
    /// String returns from proxy receivers use Utf8Slice encoding instead of MarshalToSwiftBuffer,
    /// because SwiftString contains ARC-managed references that Unsafe.Write can't retain.
    /// </summary>
    private static bool IsStringTypeSpec(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec nts && nts.Name == "Swift.String";
    }

    private void EmitConstructors(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Class-bound protocols (: AnyObject) use a 2-word existential layout:
        //   [classRef] [witnessTable]
        // Opaque protocols use a 5-word layout:
        //   [payload0] [payload1] [payload2] [metadata] [witnessTable]
        // We always allocate ExistentialContainer1 (5 words) but fill the first N words
        // according to the protocol's existential layout.
        var containerInitLines = protocolDecl.IsClassBound
            ? "_swiftContainer.Payload1 = (IntPtr)ProtocolWitnessTableHandle;"
            : "_swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();\n                _swiftContainer[0] = ProtocolWitnessTableHandle;";

        // Constructor for C# implementation
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy wrapping a C# implementation of {{interfaceName}}.
            /// </summary>
            /// <param name="implementation">The C# implementation of the protocol.</param>
            public unsafe {{proxyClassName}}({{interfaceName}} implementation)
            {
                if (implementation == null) throw new ArgumentNullException(nameof(implementation));
                // Weak reference — see the field declaration in
                // ProtocolProxyEmitter.StaticInit.cs for the rationale. The
                // impl-anchored lifetime model requires that the proxy does NOT
                // strongly root the impl; otherwise the strong-registry chain
                // prevents impl GC, prevents tracker release, prevents deinit,
                // prevents unregister — a permanent leak.
                _csharpImplRef = new WeakReference<{{interfaceName}}>(implementation);

                // Create a real Swift EveryProtocol instance via @_cdecl factory.
                // The pointer carries a +1 retain from Unmanaged.passRetained(). We hold
                // it as a plain IntPtr — the +1 is owned by ProxyLifetimeTracker, anchored
                // to the lifetime of _csharpImpl. When the impl is GC'd, the tracker's
                // finalizer calls Arc.Release; Swift's deinit then fires and
                // OnEveryProtocolDeinit drops the SwiftObjectRegistry strong root.
                _everyProtocolHandle = NativeMethods.CreateEveryProtocol();

                try
                {
                    // Initialize EveryProtocol metadata from Swift (once per process)
                    if (EveryProtocol.GetTypeMetadata().Handle == IntPtr.Zero)
                        EveryProtocol.SetTypeMetadata(NativeMethods.GetEveryProtocolMetadata());

                    // Create existential container manually
                    _swiftContainer = new ExistentialContainer1();
                    _swiftContainer.Payload0 = _everyProtocolHandle;
                    {{containerInitLines}}

                    // Register this proxy so Swift callbacks can find us. The strong
                    // registry entry is dropped when OnEveryProtocolDeinit fires from
                    // Swift's deinit, which can only happen after ProxyLifetimeTracker
                    // has released the +1 (i.e., after impl GC).
                    SwiftObjectRegistry.RegisterStrong(_everyProtocolHandle, this);

                    // Wire Swift deinit -> C# callback. The context arg is the handle
                    // itself, so OnEveryProtocolDeinit can locate the registry entry
                    // and tracker bookkeeping for targeted teardown.
                    NativeMethods.SetEveryProtocolDeinitCallback(
                        _everyProtocolHandle,
                        &Swift.Runtime.ProxyLifetimeTracker.OnEveryProtocolDeinit,
                        _everyProtocolHandle);

                    // Anchor the ground-state +1 to the impl lifetime. Tracker must be
                    // called AFTER the deinit callback is wired up so that a super-fast
                    // Swift release (e.g., never-stored call) still routes through
                    // OnEveryProtocolDeinit before the finalizer path runs.
                    Swift.Runtime.ProxyLifetimeTracker.Track(implementation, _everyProtocolHandle);
                }
                catch
                {
                    // Ctor failed before tracker/registry wiring was complete — release
                    // the +1 directly to avoid leaking the Swift instance.
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
                    try { Arc.Release(_everyProtocolHandle); } catch { /* already deallocating */ }
                    throw;
                }
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            /// <summary>
            /// Creates a proxy from an existing Swift existential container.
            /// This constructor is used internally by generated marshalling code.
            /// </summary>
            /// <remarks>
            /// Swift-backed proxies created with this constructor dispatch blittable and String
            /// protocol members through witness table accessors. Non-dispatchable members
            /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
            /// </remarks>
            /// <param name="container">The Swift existential container.</param>
            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
            public {{proxyClassName}}(ExistentialContainer1 container)
            {
                _swiftContainer = container;
                _csharpImplRef = null;
                _everyProtocolHandle = IntPtr.Zero;
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            """);
    }
}
