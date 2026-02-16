// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides methods for emitting closure-related code, including callback functions
/// and marshalling setup for Swift closures.
/// </summary>
public static partial class ClosureEmitter
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
    /// <param name="useCdecl">When true, emit CallConvCdecl with IntPtr context instead of CallConvSwift with SwiftSelf.</param>
    public static void EmitEscapingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
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
        // Cdecl: context is a plain IntPtr parameter.
        // Swift: context is passed in the Swift "self" register via SwiftSelf.
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");

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

        var callConvType = useCdecl ? "typeof(CallConvCdecl)" : "typeof(CallConvSwift)";
        var contextExtraction = useCdecl ? "contextPtr" : "new IntPtr(context.Value)";

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
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
    /// <param name="useCdecl">When true, emit Cdecl function pointer type with IntPtr context.</param>
    public static void EmitClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        var funcPtrType = BuildEscapingClosureCallbackFunctionPointerType(closureTypeSpec, closureHandler, useCdecl);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = useCdecl
            ? AddCdeclContextToFunctionPointerType(funcPtrType)
            : AddContextToFunctionPointerType(funcPtrType);

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

        // Direct existential params: P/Invoke type is ExistentialContainer (blittable),
        // but delegate type is now the protocol interface. Wrap with proxy constructor.
        if (closureHandler.NeedsProxyWrapping(typeSpec, out var proxyName))
            return $"new {proxyName}(arg{argIndex})";

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

        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        // Use nesting-aware search to skip commas inside generic type arguments
        int lastComma = EmitterUtility.FindLastTopLevelComma(funcPtrType, lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Swift]<void>"
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "SwiftSelf, ");
        }

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
            return closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);

        return GetCallbackParameterType(closureTypeSpec.ReturnType, closureHandler);
    }

    /// <summary>
    /// Builds the function pointer type for an escaping closure callback.
    /// </summary>
    private static string BuildEscapingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        bool useCdecl = false)
    {
        var types = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
        }

        types.Add(GetEscapingClosureCallbackReturnType(closureTypeSpec, closureHandler));
        var callConv = useCdecl ? "Cdecl" : "Swift";
        return $"delegate* unmanaged[{callConv}]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Builds the function pointer type for a throwing closure callback.
    /// </summary>
    private static string BuildThrowingClosureCallbackFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        bool useCdecl = false)
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
        var callConv = useCdecl ? "Cdecl" : "Swift";
        return $"delegate* unmanaged[{callConv}]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Adds IntPtr context parameter to a Cdecl function pointer type string.
    /// Used for closure Cdecl wrapper path where context is a plain IntPtr (not SwiftSelf).
    /// </summary>
    internal static string AddCdeclContextToFunctionPointerType(string funcPtrType)
    {
        // Transform "delegate* unmanaged[Cdecl]<int, void>" to "delegate* unmanaged[Cdecl]<int, IntPtr, void>"
        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        // Use nesting-aware search to skip commas inside generic type arguments
        int lastComma = EmitterUtility.FindLastTopLevelComma(funcPtrType, lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
        }

        return funcPtrType.Insert(lastComma + 1, " IntPtr,");
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
}
