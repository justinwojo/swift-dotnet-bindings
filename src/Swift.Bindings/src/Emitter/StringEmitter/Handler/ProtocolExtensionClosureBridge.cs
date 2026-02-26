// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits C# code for protocol extension methods with closure parameters.
/// Takes over entire method emission from MethodHandler when the method is a
/// protocol extension with bridgeable closure params.
/// <para>
/// Emits: [UnmanagedCallersOnly] callback, static function pointer field,
/// P/Invoke declaration (LibraryImport, CallConvSwift), and public method
/// with Func&lt;&gt;/Action&lt;&gt; parameter.
/// </para>
/// </summary>
public static class ProtocolExtensionClosureBridge
{
    /// <summary>
    /// Attempts to emit a protocol extension closure bridge for the given method.
    /// Returns true if the method was handled (caller should skip normal emission).
    /// Returns false if the method is not eligible (caller proceeds normally).
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl)
    {
        var method = env.MethodDecl;
        if (!method.IsProtocolExtensionMethod) return false;

        // Find the closure parameter
        ClosureTypeSpec? closureTypeSpec = null;
        ArgumentDecl? closureArg = null;
        int closureArgIndex = -1;
        int argIndex = 0;
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is ClosureTypeSpec cts)
            {
                closureTypeSpec = cts;
                closureArg = arg;
                closureArgIndex = argIndex;
                break;
            }
            argIndex++;
        }

        if (closureTypeSpec == null || closureArg == null)
            return false;

        // Analyze closure shape
        var closureArgs = closureTypeSpec.EachArgument().ToList();
        var closureReturnIsVoid = closureTypeSpec.ReturnType.IsEmptyTuple;
        var closureReturnIsBool = closureTypeSpec.ReturnType is NamedTypeSpec retBoolNamed &&
            retBoolNamed.Name == "Swift.Bool";

        // Detect method-level generic params (τ_1_X)
        var methodLevelGenerics = new List<GenericArgumentDecl>();
        var classLevelGenericCount = parentDecl is ClassDecl cd ? cd.GenericParameters.Count : 0;
        if (method.GenericParameters.Count > classLevelGenericCount)
        {
            methodLevelGenerics = method.GenericParameters.Skip(classLevelGenericCount).ToList();
        }

        // Check if closure return is a method-level generic.
        // Only match against methodLevelGenerics — do NOT use IsGenericTypeParameter
        // which would also match class-level generics (τ_0_0) that the bridge
        // can't resolve to valid C# names.
        var closureReturnIsMethodGeneric = !closureReturnIsVoid && !closureReturnIsBool &&
            closureTypeSpec.ReturnType is NamedTypeSpec retGenNamed &&
            methodLevelGenerics.Any(g => g.SugaredTypeName == retGenNamed.Name ||
                                         g.TypeName == retGenNamed.Name);

        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName);
        var closureParamName = NameProvider.GetCSharpParameterName(closureArg);
        var methodName = NameProvider.ToPascalCase(method.Name);
        var callbackBaseName = $"PExtCB_{mangledHash}";

        // For generic containing types, emit callback + funcPtr + P/Invoke into the
        // PInvokeHelperContext's non-generic helper class to avoid CS7042.
        var helperClassName = "";
        if (env.PInvokeHelperContext != null)
        {
            helperClassName = env.PInvokeHelperContext.HelperClassName;

            // Emit callback, function pointer field, and P/Invoke as raw code into the helper
            var helperWriter = new System.IO.StringWriter();
            var helperCsWriter = new CSharpWriter(helperWriter) { Indent = 0 };

            EmitCallback(helperCsWriter, closureArgs, closureReturnIsVoid, closureReturnIsBool,
                closureReturnIsMethodGeneric, callbackBaseName);
            EmitFunctionPointerField(helperCsWriter, closureArgs, closureReturnIsVoid, closureReturnIsBool,
                closureReturnIsMethodGeneric, callbackBaseName);
            EmitPInvoke(helperCsWriter, method, asyncLibName, closureReturnIsVoid,
                closureReturnIsBool, closureReturnIsMethodGeneric, closureArg);

            helperCsWriter.Flush();
            env.PInvokeHelperContext.RawCodeBlocks.Add(helperWriter.ToString());
        }
        else
        {
            // Non-generic type: emit directly into the class body
            EmitCallback(csWriter, closureArgs, closureReturnIsVoid, closureReturnIsBool,
                closureReturnIsMethodGeneric, callbackBaseName);
            EmitFunctionPointerField(csWriter, closureArgs, closureReturnIsVoid, closureReturnIsBool,
                closureReturnIsMethodGeneric, callbackBaseName);
            EmitPInvoke(csWriter, method, asyncLibName, closureReturnIsVoid,
                closureReturnIsBool, closureReturnIsMethodGeneric, closureArg);
        }

        // --- Emit public method (always in the class body) ---
        EmitPublicMethod(csWriter, method, methodName, closureTypeSpec, closureArg,
            closureArgs, closureReturnIsVoid, closureReturnIsBool, closureReturnIsMethodGeneric,
            methodLevelGenerics, callbackBaseName, closureParamName, env, parentDecl, helperClassName);

        method.WasEmitted = true;
        return true;
    }

    /// <summary>
    /// Emits the [UnmanagedCallersOnly] callback method.
    /// The callback is non-generic — receives IntPtr args + IntPtr context.
    /// </summary>
    private static void EmitCallback(
        CSharpWriter csWriter,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        bool closureReturnIsBool,
        bool closureReturnIsMethodGeneric,
        string callbackBaseName)
    {
        var paramParts = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
            paramParts.Add($"IntPtr arg{i}");
        if (closureReturnIsMethodGeneric)
            paramParts.Add("IntPtr resultBufPtr");
        paramParts.Add("IntPtr contextPtr");

        string returnType = closureReturnIsBool ? "byte" : "void";

        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe {returnType} {callbackBaseName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        csWriter.WriteLine("var handle = GCHandle.FromIntPtr(contextPtr);");

        if (closureReturnIsBool)
        {
            // Bool return: extract Func<IntPtr..., bool>, invoke, convert to byte
            var funcTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(_ => "IntPtr")) + ", bool";
            csWriter.WriteLine($"var callback = (Func<{funcTypeArgs}>)handle.Target!;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"return (byte)(callback({callArgs}) ? 1 : 0);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch { return 0; }");
        }
        else if (closureReturnIsMethodGeneric)
        {
            // Generic return: extract Action<IntPtr..., IntPtr>, invoke with result buffer
            var actionTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count + 1).Select(_ => "IntPtr"));
            csWriter.WriteLine($"var callback = (Action<{actionTypeArgs}>)handle.Target!;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"callback({callArgs}, resultBufPtr);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch { }");
        }
        else
        {
            // Void return: extract Action<IntPtr...>, invoke
            if (closureArgs.Count > 0)
            {
                var actionTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(_ => "IntPtr"));
                csWriter.WriteLine($"var callback = (Action<{actionTypeArgs}>)handle.Target!;");
            }
            else
            {
                csWriter.WriteLine("var callback = (Action)handle.Target!;");
            }
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var callArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"arg{i}"));
            csWriter.WriteLine($"callback({callArgs});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch { }");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the static function pointer field for the callback.
    /// </summary>
    private static void EmitFunctionPointerField(
        CSharpWriter csWriter,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        bool closureReturnIsBool,
        bool closureReturnIsMethodGeneric,
        string callbackBaseName)
    {
        var delegateParts = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
            delegateParts.Add("IntPtr");
        if (closureReturnIsMethodGeneric)
            delegateParts.Add("IntPtr"); // resultBufPtr
        delegateParts.Add("IntPtr"); // context

        string returnPart = closureReturnIsBool ? "byte" : "void";
        delegateParts.Add(returnPart);

        csWriter.WriteLine($"internal static readonly unsafe IntPtr s_{callbackBaseName} = " +
            $"(IntPtr)(delegate* unmanaged[Cdecl]<{string.Join(", ", delegateParts)}>)&{callbackBaseName};");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the P/Invoke declaration.
    /// </summary>
    private static void EmitPInvoke(
        CSharpWriter csWriter,
        MethodDecl method,
        string asyncLibName,
        bool closureReturnIsVoid,
        bool closureReturnIsBool,
        bool closureReturnIsMethodGeneric,
        ArgumentDecl closureArg)
    {
        var pInvokeName = NameProvider.GetPInvokeName(method);
        var pinvokeParams = new List<string>();

        // self_ first (protocol extension method ABI)
        pinvokeParams.Add("IntPtr self_");

        // Parameters in declaration order
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg)
            {
                // Closure → funcPtr + context
                pinvokeParams.Add("IntPtr funcPtr");
                pinvokeParams.Add("IntPtr context");
            }
            else
            {
                var csName = NameProvider.GetCSharpParameterName(arg);
                pinvokeParams.Add($"IntPtr {csName}");
            }
        }

        // TypeMetadata for each generic param (explicit + implicit per param)
        foreach (var gp in method.GenericParameters)
        {
            var gpLabel = (gp.SugaredTypeName ?? gp.TypeName).ToLowerInvariant();
            pinvokeParams.Add($"TypeMetadata {gpLabel}Type");
            pinvokeParams.Add($"TypeMetadata {gpLabel}ImplicitMetadata");
        }

        // Return type — class returns IntPtr, otherwise void
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        bool returnsClass = returnSpec is NamedTypeSpec rn && !returnSpec.IsEmptyTuple &&
            !MarshallingHelpers.IsSwiftPrimitive(rn.Name);
        string pinvokeReturnType = returnsClass ? "IntPtr" : "void";

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = method.MangledName,
            MethodName = pInvokeName,
            ReturnType = pinvokeReturnType,
            ParametersString = string.Join(", ", pinvokeParams),
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the public method with typed Func&lt;&gt;/Action&lt;&gt; parameter.
    /// </summary>
    private static void EmitPublicMethod(
        CSharpWriter csWriter,
        MethodDecl method,
        string methodName,
        ClosureTypeSpec closureTypeSpec,
        ArgumentDecl closureArg,
        List<TypeSpec> closureArgs,
        bool closureReturnIsVoid,
        bool closureReturnIsBool,
        bool closureReturnIsMethodGeneric,
        List<GenericArgumentDecl> methodLevelGenerics,
        string callbackBaseName,
        string closureParamName,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        string helperClassName = "")
    {
        var classDecl = parentDecl as ClassDecl;
        var pInvokeName = NameProvider.GetPInvokeName(method);

        // Build C# types for closure arguments
        var closureArgCSharpTypes = new List<string>();
        foreach (var arg in closureArgs)
        {
            closureArgCSharpTypes.Add(GetCSharpTypeForClosureArg(arg, env, method));
        }

        // Build closure return C# type
        string closureReturnCSharpType = "";
        if (closureReturnIsBool)
            closureReturnCSharpType = "bool";
        else if (closureReturnIsMethodGeneric)
        {
            // Method-level generic → use C# generic param name
            var retNamed = (NamedTypeSpec)closureTypeSpec.ReturnType;
            closureReturnCSharpType = GetMethodGenericCSharpName(retNamed, methodLevelGenerics);
        }

        // Build delegate type
        string delegateType;
        if (closureReturnIsVoid)
        {
            delegateType = closureArgCSharpTypes.Count > 0
                ? $"Action<{string.Join(", ", closureArgCSharpTypes)}>"
                : "Action";
        }
        else
        {
            var allTypeArgs = new List<string>(closureArgCSharpTypes);
            allTypeArgs.Add(closureReturnCSharpType);
            delegateType = $"Func<{string.Join(", ", allTypeArgs)}>";
        }

        // Build method-level generic clause for C#
        var methodGenericClause = "";
        var methodGenericConstraints = "";
        if (methodLevelGenerics.Count > 0)
        {
            var names = methodLevelGenerics.Select(g => $"T{g.SugaredTypeName}");
            methodGenericClause = $"<{string.Join(", ", names)}>";
            // Add ISwiftObject constraint for method-level generics used as class type returns
            var constraints = methodLevelGenerics
                .Select(g => $"where T{g.SugaredTypeName} : class, ISwiftObject");
            methodGenericConstraints = " " + string.Join(" ", constraints);
        }

        // Build return type
        var returnSpec = method.CSSignature[0].SwiftTypeSpec;
        string returnType;
        bool returnsClass;
        if (returnSpec.IsEmptyTuple)
        {
            returnType = "void";
            returnsClass = false;
        }
        else
        {
            returnType = GetCSharpReturnType(returnSpec, env, method, methodLevelGenerics);
            returnsClass = true;
        }

        // Build public parameter list
        var publicParams = new List<string>();
        publicParams.Add($"{delegateType} {closureParamName}");

        // Emit XML doc
        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, method);

        csWriter.WriteLine($"public unsafe {returnType} {methodName}{methodGenericClause}({string.Join(", ", publicParams)}){methodGenericConstraints}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build inner callback delegate that maps typed args → IntPtr args
        if (closureReturnIsBool)
        {
            // Func<IntPtr..., bool> inner
            var innerTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(_ => "IntPtr")) + ", bool";
            csWriter.WriteLine($"Func<{innerTypeArgs}> __inner = ({string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"IntPtr __p{i}"))}) =>");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            // Marshal each IntPtr → typed arg
            for (int i = 0; i < closureArgs.Count; i++)
            {
                EmitArgMarshal(csWriter, closureArgs[i], closureArgCSharpTypes[i], i, env, method);
            }
            var userArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__a{i}"));
            csWriter.WriteLine($"return {closureParamName}({userArgs});");
            csWriter.Indent--;
            csWriter.WriteLine("};");
        }
        else if (closureReturnIsMethodGeneric)
        {
            // Action<IntPtr..., IntPtr> inner — writes result to buffer
            var innerTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count + 1).Select(_ => "IntPtr"));
            csWriter.WriteLine($"Action<{innerTypeArgs}> __inner = ({string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"IntPtr __p{i}").Append("IntPtr __resBuf"))}) =>");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            for (int i = 0; i < closureArgs.Count; i++)
            {
                EmitArgMarshal(csWriter, closureArgs[i], closureArgCSharpTypes[i], i, env, method);
            }
            var userArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__a{i}"));
            csWriter.WriteLine($"var __result = {closureParamName}({userArgs});");
            // Write result to buffer via SwiftMarshal
            csWriter.WriteLine("var __resSpan = new Span<byte>(__resBuf.ToPointer(), (int)TypeMetadata.GetTypeMetadataOrThrow<" +
                closureReturnCSharpType + ">().Size);");
            csWriter.WriteLine("SwiftMarshal.MarshalToSwift(__result, ref __resSpan);");
            csWriter.Indent--;
            csWriter.WriteLine("};");
        }
        else
        {
            // Action<IntPtr...> inner
            if (closureArgs.Count > 0)
            {
                var innerTypeArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(_ => "IntPtr"));
                csWriter.WriteLine($"Action<{innerTypeArgs}> __inner = ({string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"IntPtr __p{i}"))}) =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                for (int i = 0; i < closureArgs.Count; i++)
                {
                    EmitArgMarshal(csWriter, closureArgs[i], closureArgCSharpTypes[i], i, env, method);
                }
                var userArgs = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__a{i}"));
                csWriter.WriteLine($"{closureParamName}({userArgs});");
                csWriter.Indent--;
                csWriter.WriteLine("};");
            }
            else
            {
                csWriter.WriteLine($"Action __inner = () => {closureParamName}();");
            }
        }

        // Allocate GCHandle — intentionally leaked for @escaping closure lifetime
        csWriter.WriteLine("var __gcHandle = GCHandle.Alloc(__inner);");

        // When in a generic type, callback pointer and P/Invoke live in the helper class
        var helperPrefix = string.IsNullOrEmpty(helperClassName) ? "" : $"{helperClassName}.";

        // Build P/Invoke call
        var callArgs = new List<string>();
        callArgs.Add("Payload.DangerousGetHandle()"); // self_

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg == closureArg)
            {
                callArgs.Add($"{helperPrefix}s_{callbackBaseName}");
                callArgs.Add("GCHandle.ToIntPtr(__gcHandle)");
            }
            else
            {
                var csName = NameProvider.GetCSharpParameterName(arg);
                callArgs.Add(csName);
            }
        }

        // TypeMetadata pairs (explicit + implicit for each generic param)
        foreach (var gp in method.GenericParameters)
        {
            var csharpGenericName = GetCSharpGenericParamName(gp, classDecl, methodLevelGenerics);
            callArgs.Add($"TypeMetadata.GetTypeMetadataOrThrow<{csharpGenericName}>()");
            callArgs.Add($"TypeMetadata.GetTypeMetadataOrThrow<{csharpGenericName}>()");
        }

        if (returnsClass)
        {
            csWriter.WriteLine($"var __result = {helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");

            // Construct C# object from IntPtr via NativeMemory + SwiftMarshal pattern
            csWriter.WriteLine("var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("*(IntPtr*)classPayload = __result;");
            csWriter.WriteLine($"return ({returnType})SwiftMarshal.MarshalFromSwift<{returnType}>(new IntPtr(classPayload));");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("NativeMemory.Free(classPayload);");
            csWriter.WriteLine("throw;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else
        {
            csWriter.WriteLine($"{helperPrefix}{pInvokeName}({string.Join(", ", callArgs)});");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits marshalling code to convert an IntPtr to a typed C# argument.
    /// </summary>
    private static void EmitArgMarshal(
        CSharpWriter csWriter,
        TypeSpec argType,
        string csharpType,
        int index,
        MethodEnvironment env,
        MethodDecl method)
    {
        // For class types that implement ISwiftObject, use SwiftMarshal
        // For primitive types like bool, use direct conversion
        if (MarshallingHelpers.IsBoolType(csharpType))
        {
            csWriter.WriteLine($"var __a{index} = __p{index} != IntPtr.Zero;");
        }
        else
        {
            // Use SwiftMarshal for ISwiftObject types (classes, generic params)
            csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalFromSwift<{csharpType}>(__p{index});");
        }
    }

    /// <summary>
    /// Gets the C# type name for a closure argument TypeSpec.
    /// </summary>
    private static string GetCSharpTypeForClosureArg(
        TypeSpec argType,
        MethodEnvironment env,
        MethodDecl method)
    {
        if (argType is NamedTypeSpec namedArg)
        {
            // Generic type parameter (τ_0_0) → resolve to C# generic param name (T)
            if (TypeSpecHelpers.IsGenericTypeParameter(namedArg.Name))
            {
                return ResolveCSharpGenericParamName(namedArg.Name, method, env);
            }

            // Primitives
            if (namedArg.Name == "Swift.Bool") return "bool";
            if (namedArg.Name == "Swift.Int") return "nint";
            if (namedArg.Name == "Swift.UInt") return "nuint";
            if (namedArg.Name == "Swift.Float") return "float";
            if (namedArg.Name == "Swift.Double") return "double";
            if (namedArg.Name == "Swift.Int32") return "int";
            if (namedArg.Name == "Swift.Int64") return "long";

            // Bound generic (e.g., Event<τ_0_0>) — use BoundGenericsHandler
            if (namedArg.ContainsGenericParameters)
            {
                return env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(
                    argType, GenericContext.Empty);
            }

            // Class type — look up in TypeDatabase
            if (env.TypeDatabase.TryGetTypeRecord(argType, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr"; // Fallback
    }

    /// <summary>
    /// Gets the C# return type for the method.
    /// </summary>
    private static string GetCSharpReturnType(
        TypeSpec returnSpec,
        MethodEnvironment env,
        MethodDecl method,
        List<GenericArgumentDecl> methodLevelGenerics)
    {
        if (returnSpec is NamedTypeSpec namedRet)
        {
            // If return type has generic params (e.g., Observable<τ_0_0>), resolve them
            if (namedRet.ContainsGenericParameters)
            {
                var resolvedGenericParams = namedRet.GenericParameters
                    .Select(gp => ResolveGenericParamForCSharp(gp, method, env, methodLevelGenerics))
                    .ToList();

                // Look up the base type (without generic params)
                string baseName;
                if (env.TypeDatabase.TryGetTypeRecord(
                    new NamedTypeSpec(namedRet.Name), out var baseRecord))
                {
                    baseName = baseRecord.CSharpTypeName.FullyQualifiedName;
                }
                else if (env.TypeDatabase.TryGetTypeRecord(returnSpec, out var fullRecord))
                {
                    // Full spec lookup found — extract base name by stripping generic params
                    var fullName = fullRecord.CSharpTypeName.FullyQualifiedName;
                    var angleBracket = fullName.IndexOf('<');
                    baseName = angleBracket >= 0 ? fullName.Substring(0, angleBracket) : fullName;
                }
                else
                {
                    baseName = namedRet.Name;
                }
                return $"{baseName}<{string.Join(", ", resolvedGenericParams)}>";
            }

            // Simple type
            if (env.TypeDatabase.TryGetTypeRecord(returnSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return "IntPtr";
    }

    /// <summary>
    /// Resolves a generic parameter TypeSpec to its C# name.
    /// </summary>
    private static string ResolveGenericParamForCSharp(
        TypeSpec typeSpec,
        MethodDecl method,
        MethodEnvironment env,
        List<GenericArgumentDecl> methodLevelGenerics)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Self → conforming type's C# name (with generic params if generic)
            if (namedType.Name == "Self")
            {
                var parentClass = method.ParentDecl as ClassDecl;
                if (parentClass != null && env.TypeDatabase.TryGetTypeRecord(
                    new NamedTypeSpec(parentClass.SwiftTypeName.ModuleQualifiedName), out var selfRecord))
                {
                    var baseName = selfRecord.CSharpTypeName.FullyQualifiedName;
                    if (parentClass.IsGeneric && parentClass.GenericParameters.Count > 0)
                    {
                        var typeParams = parentClass.GenericParameters
                            .Select((gp, i) => NameProvider.GetCSharpGenericParameterName(gp, i));
                        baseName = $"{baseName}<{string.Join(", ", typeParams)}>";
                    }
                    return baseName;
                }
                return "IntPtr"; // Fallback
            }

            // τ_0_0 → T (class-level generic)
            if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
            {
                return ResolveCSharpGenericParamName(namedType.Name, method, env);
            }

            // Method-level generic like "Result" → TResult
            foreach (var mlg in methodLevelGenerics)
            {
                if (mlg.TypeName == namedType.Name || mlg.SugaredTypeName == namedType.Name)
                    return $"T{mlg.SugaredTypeName}";
            }

            // Regular named type
            if (env.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;
        }

        return typeSpec.ToString();
    }

    /// <summary>
    /// Resolves a Swift generic type parameter name (e.g., τ_0_0) to its C# name (e.g., T).
    /// </summary>
    private static string ResolveCSharpGenericParamName(
        string swiftGenericName,
        MethodDecl method,
        MethodEnvironment env)
    {
        // Use GenericTypeMapping from MethodEnvironment if available
        if (env.GenericTypeMapping.TryGetValue(swiftGenericName, out var csName))
            return csName.TypeParameter;

        // Fall back to matching by TypeName in GenericParameters
        var classDecl = method.ParentDecl as ClassDecl;
        var classGenericCount = classDecl?.GenericParameters.Count ?? 0;
        for (int i = 0; i < method.GenericParameters.Count; i++)
        {
            var gp = method.GenericParameters[i];
            if (gp.TypeName == swiftGenericName || gp.SugaredTypeName == swiftGenericName)
            {
                if (i < classGenericCount)
                    return NameProvider.GetCSharpGenericParameterName(gp, i);
                else
                    return $"T{gp.SugaredTypeName}";
            }
        }

        return "T"; // Default
    }

    /// <summary>
    /// Gets the C# generic parameter name for a method-level generic.
    /// </summary>
    private static string GetMethodGenericCSharpName(
        NamedTypeSpec retNamed,
        List<GenericArgumentDecl> methodLevelGenerics)
    {
        foreach (var mlg in methodLevelGenerics)
        {
            if (mlg.TypeName == retNamed.Name || mlg.SugaredTypeName == retNamed.Name)
                return $"T{mlg.SugaredTypeName}";
        }
        // Defensive: should not be reached if closureReturnIsMethodGeneric was
        // correctly gated by methodLevelGenerics membership. Return a safe
        // fallback rather than a raw Swift generic name (e.g., τ_0_0).
        return "TResult";
    }

    /// <summary>
    /// Gets the C# name for a generic parameter declaration, considering whether it's
    /// class-level or method-level.
    /// </summary>
    private static string GetCSharpGenericParamName(
        GenericArgumentDecl gp,
        ClassDecl? classDecl,
        List<GenericArgumentDecl> methodLevelGenerics)
    {
        // Check if it's a method-level generic
        foreach (var mlg in methodLevelGenerics)
        {
            if (mlg.TypeName == gp.TypeName)
                return $"T{mlg.SugaredTypeName}";
        }

        // Class-level: use the standard C# generic parameter name (e.g., "Element" → "TElement")
        if (classDecl?.GenericParameters != null)
        {
            for (int i = 0; i < classDecl.GenericParameters.Count; i++)
            {
                if (classDecl.GenericParameters[i].TypeName == gp.TypeName)
                    return NameProvider.GetCSharpGenericParameterName(gp, i);
            }
        }

        return gp.SugaredTypeName ?? "T";
    }
}
