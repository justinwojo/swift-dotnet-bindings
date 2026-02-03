// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides methods for emitting closure-related code, including callback functions
/// and marshalling setup for Swift closures.
/// </summary>
public static class ClosureEmitter
{
    /// <summary>
    /// Emits an [UnmanagedCallersOnly] callback function that can be used as a Swift closure's
    /// function pointer. The callback extracts the delegate from the context and invokes it.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitEscapingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build parameter list for the callback (arguments + context as last param)
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramType = GetCallbackParameterType(arg, closureHandler);
            parameters.Add($"{paramType} arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        // Context is passed in the Swift "self" register. Using SwiftSelf tells .NET to
        // receive this value from the correct register per Swift calling convention.
        parameters.Add("SwiftSelf context");

        var returnType = GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler);

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        // Handle type conversions: byte->bool, void*->struct marshalling
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);

        // For bool returns, we need to convert: (byte)(result ? 1 : 0)
        string returnStatement;
        if (!hasReturn)
        {
            returnStatement = $"del({invokeArgsString});";
        }
        else if (returnIsBool)
        {
            returnStatement = $"return (byte)(del({invokeArgsString}) ? 1 : 0);";
        }
        else
        {
            returnStatement = $"return del({invokeArgsString});";
        }

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>(new IntPtr(context.Value));
                {{returnStatement}}
            }
            """);
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for the callback.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = BuildEscapingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to create closure data from a delegate for escaping closures.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureHandler">The closure handler.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitEscapingClosureMarshallingSetup(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var closureDataVar = $"{parameterName}ClosureData";
        var handleVar = $"{parameterName}Handle";

        csWriter.WriteLines($$"""
            var {{handleVar}} = GCHandle.Alloc({{parameterName}});
            var {{closureDataVar}} = new SwiftClosureData((IntPtr)s_{{callbackName}}, GCHandle.ToIntPtr({{handleVar}}));
            """);
    }

    /// <summary>
    /// Emits code to clean up escaping closure resources in a finally block.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    public static void EmitEscapingClosureCleanup(
        CSharpWriter csWriter,
        string parameterName)
    {
        var handleVar = $"{parameterName}Handle";

        csWriter.WriteLine($"if ({handleVar}.IsAllocated) {handleVar}.Free();");
    }

    /// <summary>
    /// Gets the C# type for a closure callback parameter.
    /// Delegates to ClosureHandler.TranslateTypeSpecToPInvokeType for consistency
    /// between the callback signature and function pointer type declaration.
    /// </summary>
    private static string GetCallbackParameterType(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        return closureHandler.TranslateTypeSpecToPInvokeType(typeSpec);
    }

    /// <summary>
    /// Checks if a type is a Swift pointer type.
    /// </summary>
    private static bool IsPointerType(NamedTypeSpec namedType)
    {
        return namedType.Name == "Swift.UnsafePointer" ||
               namedType.Name == "Swift.UnsafeMutablePointer" ||
               namedType.Name == "Swift.UnsafeRawPointer" ||
               namedType.Name == "Swift.UnsafeMutableRawPointer" ||
               namedType.Name == "Swift.OpaquePointer" ||
               namedType.Name == "Builtin.RawPointer";
    }

    /// <summary>
    /// Checks if the Swift type is Bool, which requires conversion in callbacks.
    /// </summary>
    private static bool IsBoolType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool";
    }

    /// <summary>
    /// Generates the expression to pass an argument when invoking the delegate.
    /// Handles type conversions:
    /// - byte -> bool for Swift.Bool
    /// - void* -> struct marshalling for complex types
    /// </summary>
    /// <param name="typeSpec">The TypeSpec for the argument.</param>
    /// <param name="argIndex">The argument index.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <returns>The expression string to use when invoking the delegate.</returns>
    private static string GetInvokeArgExpression(TypeSpec typeSpec, int argIndex, ClosureHandler closureHandler)
    {
        // Bool requires byte->bool conversion
        if (IsBoolType(typeSpec))
            return $"arg{argIndex} != 0";

        // Check if this parameter needs marshalling from void*
        var callbackType = GetCallbackParameterType(typeSpec, closureHandler);
        if (callbackType == "void*" && typeSpec is NamedTypeSpec namedType && !IsPointerType(namedType))
        {
            // The callback receives void* but the delegate expects the actual type.
            // Use SwiftMarshal.MarshalFromSwift to convert.
            var delegateType = closureHandler.TranslateTypeSpecToCSharp(typeSpec);
            return $"SwiftMarshal.MarshalFromSwift<{delegateType}>(new IntPtr(arg{argIndex}))";
        }

        return $"arg{argIndex}";
    }


    /// <summary>
    /// Adds SwiftSelf context parameter to a function pointer type string.
    /// SwiftSelf is used because Swift passes closure context in the "self" register.
    /// </summary>
    private static string AddContextToFunctionPointerType(string funcPtrType)
    {
        // Transform "delegate* unmanaged[Swift]<int, void>" to "delegate* unmanaged[Swift]<int, SwiftSelf, void>"
        // The context is the last parameter before the return type

        // Find the last comma before '>'
        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        int lastComma = funcPtrType.LastIndexOf(',', lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Swift]<void>"
            // Insert "SwiftSelf, " before the return type
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "SwiftSelf, ");
        }

        // Insert ", SwiftSelf" after the last comma
        return funcPtrType.Insert(lastComma + 1, " SwiftSelf,");
    }

    /// <summary>
    /// Gets the return type for escaping/throwing closure callbacks.
    /// Frozen structs and primitive scalars can return by value directly.
    /// </summary>
    private static string GetEscapingClosureCallbackReturnType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return "void";

        if (IsBoolType(closureTypeSpec.ReturnType))
            return "byte";

        if (closureHandler.CanUseDirectCallbackReturn(closureTypeSpec.ReturnType))
            return closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType);

        return GetCallbackParameterType(closureTypeSpec.ReturnType, closureHandler);
    }

    /// <summary>
    /// Builds the function pointer type for an escaping closure callback.
    /// </summary>
    private static string BuildEscapingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var types = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
        }

        types.Add(GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
        return $"delegate* unmanaged[Swift]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Builds the function pointer type for a throwing closure callback.
    /// </summary>
    private static string BuildThrowingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var types = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
        }

        types.Add("SwiftError*");
        types.Add(closureTypeSpec.ReturnType.IsEmptyTuple
            ? "void"
            : GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
        return $"delegate* unmanaged[Swift]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Emits an [UnmanagedCallersOnly] callback function that uses indirect return.
    /// The result is marshalled into a buffer pointer instead of being returned directly.
    /// This pattern is used for closures returning bound generic types like SwiftOptional&lt;T&gt;.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitIndirectReturnCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var returnCSharpType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType);

        // Build parameter list: void* indirectResult, arguments..., SwiftSelf context
        var parameters = new List<string> { "void* indirectResult" };
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramType = GetCallbackParameterType(arg, closureHandler);
            parameters.Add($"{paramType} arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        parameters.Add("SwiftSelf context");
        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static void {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>(new IntPtr(context.Value));
                var result = del({{invokeArgsString}});

                // Marshal the result to the indirect result buffer
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<{{returnCSharpType}}>();
                var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                SwiftMarshal.MarshalToSwift(result, ref resultSpan);
            }
            """);
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for an indirect return callback.
    /// </summary>
    public static void EmitIndirectReturnCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerTypeWithIndirectReturn(closureTypeSpec);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits an [UnmanagedCallersOnly] callback function for a throwing closure.
    /// The callback invokes the C# delegate (which returns SwiftResult) and marshals the error
    /// via the SwiftError* out parameter if the result is a failure.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification (must have Throws=true).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitThrowingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build parameter list: arguments..., SwiftError* errorOut, SwiftSelf context
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramType = GetCallbackParameterType(arg, closureHandler);
            parameters.Add($"{paramType} arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        // Error out parameter before context
        parameters.Add("SwiftError* errorOut");
        // Context is passed in the Swift "self" register
        parameters.Add("SwiftSelf context");

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn
            ? GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler)
            : "void";

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Determine the success type for the SwiftResult
        var successType = hasReturn ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType) : "Swift.SwiftVoid";
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);

        // Generate callback body
        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>(new IntPtr(context.Value));
                var swiftResult = del({{invokeArgsString}});

                if (swiftResult.IsFailure)
                {
                    // Set the error out parameter
                    *errorOut = swiftResult.Failure;
            """);

        if (hasReturn)
        {
            if (returnIsBool)
            {
                csWriter.WriteLine("            return 0; // Return default value on error");
            }
            else
            {
                csWriter.WriteLine("            return default; // Return default value on error");
            }
        }

        csWriter.WriteLines($$"""
                }

                // Success case - no error
                *errorOut = default;
            """);

        if (hasReturn)
        {
            if (returnIsBool)
            {
                csWriter.WriteLine("        return (byte)(swiftResult.Success ? 1 : 0);");
            }
            else
            {
                csWriter.WriteLine("        return swiftResult.Success;");
            }
        }

        csWriter.WriteLine("    }");
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for a throwing closure callback.
    /// </summary>
    public static void EmitThrowingClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = BuildThrowingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate for a throwing closure.
    /// The invoker calls the Swift function pointer and captures any error, wrapping the result
    /// in SwiftResult&lt;T, SwiftError&gt;.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification (must have Throws=true).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitThrowingClosureReturnMarshalling(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result")
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerTypeWithError(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        // Build argument list for invoking the Swift function
        var invokeArgs = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var argExpr = GetSwiftInvokeArgExpression(arg, argIndex);
            invokeArgs.Add(argExpr);
            argIndex++;
        }
        // Add error out parameter
        invokeArgs.Add("&_error");
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);
        var successType = hasReturn ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType) : "Swift.SwiftVoid";
        var resultType = $"Swift.SwiftResult<{successType}, SwiftError>";

        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
                    SwiftError _error = default;
            """);

        if (hasReturn)
        {
            csWriter.WriteLines($$"""
                        var _rawResult = _fp({{invokeArgsString}});

                        // Check for error
                        if (_error.Value != IntPtr.Zero)
                        {
                            return {{resultType}}.FromFailure(_error);
                        }

                """);

            if (returnIsBool)
            {
                csWriter.WriteLine($"                return {resultType}.FromSuccess(_rawResult != 0);");
            }
            else
            {
                csWriter.WriteLine($"                return {resultType}.FromSuccess(_rawResult);");
            }
        }
        else
        {
            csWriter.WriteLines($$"""
                        _fp({{invokeArgsString}});

                        // Check for error
                        if (_error.Value != IntPtr.Zero)
                        {
                            return {{resultType}}.FromFailure(_error);
                        }

                        return {{resultType}}.FromSuccess(Swift.SwiftVoid.Value);
                """);
        }

        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate
    /// This creates an invoker delegate that calls the Swift function pointer with proper ARC handling.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitClosureReturnMarshalling(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result")
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        // Build argument list for invoking the Swift function
        // Need to convert C# types to Swift types (e.g., bool -> byte)
        var invokeArgs = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var argExpr = GetSwiftInvokeArgExpression(arg, argIndex);
            invokeArgs.Add(argExpr);
            argIndex++;
        }
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);

        // Generate the closure body
        string invokeExpr = $"_fp({invokeArgsString})";
        string returnExpr;
        if (!hasReturn)
        {
            returnExpr = $"{invokeExpr};";
        }
        else if (returnIsBool)
        {
            returnExpr = $"return {invokeExpr} != 0;";
        }
        else
        {
            returnExpr = $"return {invokeExpr};";
        }

        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
                    {{returnExpr}}
                }
            };

            return _invoker;
            """);
    }

    /// <summary>
    /// Generates the expression to convert a C# argument to Swift-compatible form when invoking a Swift closure.
    /// </summary>
    private static string GetSwiftInvokeArgExpression(TypeSpec typeSpec, int argIndex)
    {
        // Bool requires bool -> byte conversion
        if (IsBoolType(typeSpec))
            return $"(byte)(_arg{argIndex} ? 1 : 0)";

        return $"_arg{argIndex}";
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate,
    /// with support for struct parameters that need marshalling.
    /// For closures like (ImageDecodingContext) -> (any ImageDecoding)?, the struct
    /// parameter must be marshalled to a native buffer before calling the Swift function.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitClosureReturnMarshallingWithStructParams(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result")
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);

        // Start building the closure body with struct marshalling
        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
            """);

        csWriter.Indent += 3;

        // Generate marshalling code for each struct parameter
        var invokeArgs = new List<string>();
        for (int i = 0; i < argTypes.Count; i++)
        {
            var arg = argTypes[i];
            if (closureHandler.IsFrozenStruct(arg))
            {
                // Generate marshalling for frozen struct
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = stackalloc byte[(int)_arg{{i}}Metadata.Size];
                    var _arg{{i}}Span = new Span<byte>(_arg{{i}}Buffer, (int)_arg{{i}}Metadata.Size);
                    SwiftMarshal.MarshalToSwift(_arg{{i}}, ref _arg{{i}}Span);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
            }
            else if (IsBoolType(arg))
            {
                // Bool conversion
                invokeArgs.Add($"(byte)(_arg{i} ? 1 : 0)");
            }
            else
            {
                // Direct pass
                invokeArgs.Add($"_arg{i}");
            }
        }
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Generate the invoke and return
        string invokeExpr = $"_fp({invokeArgsString})";
        if (!hasReturn)
        {
            csWriter.WriteLine($"{invokeExpr};");
        }
        else if (returnIsBool)
        {
            csWriter.WriteLine($"return {invokeExpr} != 0;");
        }
        else
        {
            csWriter.WriteLine($"return {invokeExpr};");
        }

        csWriter.Indent -= 3;
        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate,
    /// with support for non-frozen struct parameters that require heap allocation.
    /// Non-frozen structs cannot use stackalloc since their size is not known at compile time.
    /// Uses NativeMemory.Alloc + InitializeWithCopy with proper cleanup via Destroy + NativeMemory.Free.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitClosureReturnMarshallingWithNonFrozenParams(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result")
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && IsBoolType(closureTypeSpec.ReturnType);

        // Start building the closure body with non-frozen struct marshalling
        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
            """);

        csWriter.Indent += 3;

        // Track which arguments need cleanup and collect invoke args
        var invokeArgs = new List<string>();
        var nonFrozenArgs = new List<int>();

        for (int i = 0; i < argTypes.Count; i++)
        {
            var arg = argTypes[i];
            if (closureHandler.IsNonFrozenStruct(arg))
            {
                // Generate marshalling for non-frozen struct using NativeMemory
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    // Non-frozen struct: allocate on heap, initialize, and clean up after call
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = (byte*)NativeMemory.Alloc((nuint)_arg{{i}}Metadata.Size, (nuint)_arg{{i}}Metadata.Stride);
                    _arg{{i}}Metadata.ValueWitnessTable->InitializeWithCopy(
                        (void*)_arg{{i}}Buffer,
                        (void*)_arg{{i}}.Payload.DangerousGetHandle(),
                        _arg{{i}}Metadata);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
                nonFrozenArgs.Add(i);
            }
            else if (closureHandler.IsFrozenStruct(arg))
            {
                // Generate marshalling for frozen struct using stackalloc
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = stackalloc byte[(int)_arg{{i}}Metadata.Size];
                    var _arg{{i}}Span = new Span<byte>(_arg{{i}}Buffer, (int)_arg{{i}}Metadata.Size);
                    SwiftMarshal.MarshalToSwift(_arg{{i}}, ref _arg{{i}}Span);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
            }
            else if (IsBoolType(arg))
            {
                // Bool conversion
                invokeArgs.Add($"(byte)(_arg{i} ? 1 : 0)");
            }
            else
            {
                // Direct pass
                invokeArgs.Add($"_arg{i}");
            }
        }
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Generate the try block for non-frozen args that need cleanup
        if (nonFrozenArgs.Count > 0)
        {
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Generate the invoke and return
        string invokeExpr = $"_fp({invokeArgsString})";
        if (!hasReturn)
        {
            csWriter.WriteLine($"{invokeExpr};");
        }
        else if (returnIsBool)
        {
            csWriter.WriteLine($"return {invokeExpr} != 0;");
        }
        else
        {
            csWriter.WriteLine($"return {invokeExpr};");
        }

        // Generate finally block for cleanup
        if (nonFrozenArgs.Count > 0)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (var i in nonFrozenArgs)
            {
                csWriter.WriteLines($$"""
                    _arg{{i}}Metadata.ValueWitnessTable->Destroy((void*)_arg{{i}}Buffer, _arg{{i}}Metadata);
                    NativeMemory.Free(_arg{{i}}Buffer);
                    """);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent -= 3;
        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }

    #region Async+Throwing Closure Support (Phase 28)

    /// <summary>
    /// Emits an [UnmanagedCallersOnly] "start" callback function for an async+throwing closure.
    /// This function is called synchronously by Swift and spawns Task.Run to execute the async work.
    /// When the async work completes, it calls the appropriate Swift callback (success or error).
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="methodName">The name of the method containing the closure parameter.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="closureTypeSpec">The closure type specification (must be async+throwing).</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="mangledName">The mangled name of the method (for callback disambiguation).</param>
    public static void EmitAsyncThrowingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnCSharpType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType)
            : null;

        // Determine the state type based on return type
        var stateType = hasReturn
            ? $"AsyncThrowingClosureState<{returnCSharpType}>"
            : "AsyncThrowingClosureStateVoid";

        // Check if return type is Data (special handling for byte arrays)
        var isDataReturn = hasReturn &&
            closureTypeSpec.ReturnType is NamedTypeSpec namedType &&
            (namedType.Name == "Foundation.Data" || namedType.Name == "Swift.Data");

        // NOTE: C# async lambdas cannot contain 'unsafe' blocks, so we use a helper method pattern.
        // The synchronous callback method is marked 'unsafe' to convert function pointers to delegates,
        // then passes those delegates to a non-unsafe helper that runs the async work.
        csWriter.WriteLines($$"""
            /// <summary>
            /// [UnmanagedCallersOnly] start function for async+throwing closure parameter '{{parameterName}}'.
            /// Called synchronously by Swift, spawns Task.Run to execute the async delegate.
            /// </summary>
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
            private static unsafe void {{callbackName}}(
                IntPtr contextPtr,          // GCHandle to {{stateType}}
                IntPtr continuationBoxPtr,  // Swift's ContinuationBox pointer
                IntPtr successFuncPtr,      // Function pointer for success callback
                IntPtr errorFuncPtr)        // Function pointer for error callback
            {
                var handle = GCHandle.FromIntPtr(contextPtr);
                if (handle.Target is not {{stateType}} state)
                    return;

                // Convert function pointers to delegates while we're in the unsafe context
                // These delegates can then be called from the async code without unsafe blocks
            """);

        if (isDataReturn)
        {
            // Data return type - user provides Func<Task<Swift.Data>>, we extract bytes and pass to Swift
            // Use runtime helper to avoid async in unsafe context (the class may be marked unsafe)
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr, nint>((box, dataPtr, len) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, void>)successFuncPtr;
                        fp(box, dataPtr, len);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    // Spawn async work using runtime helper (avoids async in unsafe class context)
                    AsyncClosureHelper.RunDataAsync(handle, state, continuationBoxPtr, successAction, errorAction);
                }
                """);
        }
        else if (hasReturn)
        {
            // Generic return type - success callback takes (boxPtr, resultPtr)
            // Use runtime helper to avoid async in unsafe context (the class may be marked unsafe)
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;
                        fp(box, resultPtr);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    // Spawn async work using runtime helper (avoids async in unsafe class context)
                    AsyncClosureHelper.RunAsync(handle, state, continuationBoxPtr, successAction, errorAction);
                }
                """);
        }
        else
        {
            // Void return type
            // Use runtime helper to avoid async in unsafe context (the class may be marked unsafe)
            csWriter.WriteLines($$"""
                    var successAction = new Action<IntPtr>((box) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, void>)successFuncPtr;
                        fp(box);
                    });
                    var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
                    {
                        var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;
                        fp(box, errPtr);
                    });

                    // Spawn async work using runtime helper (avoids async in unsafe class context)
                    AsyncClosureHelper.RunVoidAsync(handle, state, continuationBoxPtr, successAction, errorAction);
                }
                """);
        }
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for an async+throwing closure's start callback.
    /// </summary>
    public static void EmitAsyncThrowingClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var funcPtrType = "delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>";

        csWriter.WriteLine($"private static unsafe readonly {funcPtrType} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Emits code to set up marshalling for an async+throwing closure parameter.
    /// Creates the AsyncThrowingClosureState and allocates a GCHandle.
    /// </summary>
    public static void EmitAsyncThrowingClosureMarshallingSetup(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName) + "_Start";
        var handleVar = $"{parameterName}Handle";
        var stateVar = $"{parameterName}State";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnCSharpType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType)
            : null;

        var stateType = hasReturn
            ? $"AsyncThrowingClosureState<{returnCSharpType}>"
            : "AsyncThrowingClosureStateVoid";

        csWriter.WriteLines($$"""
            var {{stateVar}} = new {{stateType}} { AsyncFunc = {{parameterName}} };
            var {{handleVar}} = GCHandle.Alloc({{stateVar}});
            var {{parameterName}}ContextPtr = GCHandle.ToIntPtr({{handleVar}});
            """);
    }

    /// <summary>
    /// Generates Swift wrapper code for a method with async+throwing closure parameters.
    /// Uses withCheckedThrowingContinuation to convert the C# callback pattern into a Swift async closure.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer.</param>
    /// <param name="methodName">The wrapper method name.</param>
    /// <param name="parameterName">The closure parameter name.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler.</param>
    /// <param name="parentTypeName">The parent type's Swift name.</param>
    public static void EmitAsyncThrowingClosureSwiftHelpers(
        SwiftWriter swiftWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string parentTypeName)
    {
        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnSwiftType = hasReturn ? closureTypeSpec.ReturnType.ToString() : "Void";

        // Check if return type is Data (special handling)
        var isDataReturn = hasReturn &&
            closureTypeSpec.ReturnType is NamedTypeSpec namedType &&
            (namedType.Name == "Foundation.Data" || namedType.Name == "Swift.Data");

        // Generate the ContinuationBox class (needed for pointer stability)
        swiftWriter.WriteLines($$"""
            // Box to hold continuation (makes it pointer-stable for C# callbacks)
            private class ContinuationBox_{{parameterName}}<T> {
                var continuation: CheckedContinuation<T, Error>?
                init(_ continuation: CheckedContinuation<T, Error>) {
                    self.continuation = continuation
                }
            }

            """);

        // Generate typealias for the start function
        swiftWriter.Write($"private typealias AsyncThrowingStartFunc_{parameterName} = @convention(c) (");
        swiftWriter.WriteLine("UnsafeMutableRawPointer, UnsafeMutableRawPointer,");

        // Success callback signature depends on return type
        if (isDataReturn)
        {
            swiftWriter.WriteLines($$"""
                    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<UInt8>, Int) -> Void,
                    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void
                ) -> Void

                """);
        }
        else if (hasReturn)
        {
            swiftWriter.WriteLines($$"""
                    @convention(c) (UnsafeMutableRawPointer, UnsafeRawPointer) -> Void,
                    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void
                ) -> Void

                """);
        }
        else
        {
            swiftWriter.WriteLines($$"""
                    @convention(c) (UnsafeMutableRawPointer) -> Void,
                    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void
                ) -> Void

                """);
        }
    }

    #endregion
}
