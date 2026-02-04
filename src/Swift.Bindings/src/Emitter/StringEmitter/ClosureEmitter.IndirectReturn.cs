// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
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
}
