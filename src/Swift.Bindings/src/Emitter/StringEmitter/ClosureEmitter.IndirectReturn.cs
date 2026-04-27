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
    /// <param name="useCdecl">When true, emit CallConvCdecl with IntPtr context instead of CallConvSwift with SwiftSelf.</param>
    public static void EmitIndirectReturnCallback(
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
        var returnCSharpType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);

        // Build parameter list: void* indirectResult, arguments..., context
        var parameters = new List<string> { "void* indirectResult" };
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: split into (ptr, len)
            // pair to match the Swift @convention(c) decomposition. Mirrors EmitEscapingClosureCallback.
            if (useCdecl && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                parameters.Add($"void* arg{argIndex}");
                parameters.Add($"nint arg{argIndex}_len");
            }
            else
            {
                var paramType = GetCallbackParameterType(arg, closureHandler, useCdecl);
                parameters.Add($"{paramType} arg{argIndex}");
            }
            argTypes.Add(arg);
            argIndex++;
        }
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");
        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            var argExpr = GetInvokeArgExpression(argTypes[i], i, closureHandler, useCdecl);
            invokeArgs.Add(argExpr);
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        var callConvType = useCdecl ? "typeof(global::System.Runtime.CompilerServices.CallConvCdecl)" : "typeof(global::System.Runtime.CompilerServices.CallConvSwift)";
        var contextExtraction = useCdecl ? "contextPtr" : "new IntPtr(context.Value)";

        // String-containing return types need special handling: System.String has no Swift
        // TypeMetadata, so the generic MarshalToSwift path fails. Convert C# string values
        // to SwiftString, wrap in the correct Swift container, and marshal that.
        bool isPlainString = WitnessDispatchEmitter.IsStringType(closureTypeSpec.ReturnType);
        bool isOptionalString = IsOptionalStringReturn(closureTypeSpec.ReturnType);
        bool isArrayString = IsArrayStringReturn(closureTypeSpec.ReturnType);
        // ObjC-bridged returns (e.g., Foundation.URL → NSUrl): write the handle pointer.
        // The Swift struct (e.g., URL) wraps an ObjC reference — writing the handle to the
        // buffer correctly represents the Swift struct's ABI layout.
        bool isObjCBridged = closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType);
        bool isClassReturn = closureHandler.IsClassType(closureTypeSpec.ReturnType);

        if (isPlainString)
        {
            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // Convert string → SwiftString (System.String has no Swift metadata)
                    using var _swiftStr = new Swift.SwiftString(result);
                    var metadata = Swift.Runtime.SwiftObjectHelper<Swift.SwiftString>.GetTypeMetadata();
                    var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                    ((Swift.Runtime.ISwiftObject)_swiftStr).MarshalToSwift(ref resultSpan);
                }
                """);
        }
        else if (isOptionalString)
        {
            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // Convert string? → SwiftOptional<SwiftString> (System.String has no Swift metadata)
                    using var _swiftStr = result != null ? new Swift.SwiftString(result) : null;
                    using var _swiftOpt = _swiftStr != null
                        ? SwiftOptional<Swift.SwiftString>.NewSome(_swiftStr)
                        : SwiftOptional<Swift.SwiftString>.NewNone();
                    var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<Swift.SwiftString>>();
                    var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                    SwiftMarshal.MarshalToSwift(_swiftOpt, ref resultSpan);
                }
                """);
        }
        else if (isArrayString)
        {
            // Array<String> delegate type: GetCSharpDelegateType returns SwiftArray<string> but
            // the public API uses IReadOnlyList<string>. The GCHandle stores the public API type,
            // so the callback must recover using the same type.
            var arrayDelegateType = delegateType.Replace("Swift.SwiftArray<string>", "IReadOnlyList<string>");

            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{arrayDelegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // Convert IReadOnlyList<string> → SwiftArray<SwiftString> (System.String has no Swift metadata)
                    using var _swiftArray = new Swift.SwiftArray<Swift.SwiftString>();
                    foreach (var _item in result)
                    {
                        using var _str = new Swift.SwiftString(_item);
                        _swiftArray.Append(_str);
                    }
                    var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftArray<Swift.SwiftString>>();
                    var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                    SwiftMarshal.MarshalToSwift(_swiftArray, ref resultSpan);
                }
                """);
        }
        else if (isObjCBridged)
        {
            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // ObjC-bridged type: write the handle pointer to the result buffer.
                    // The Swift struct wraps an ObjC reference — the handle IS the ABI representation.
                    *(IntPtr*)indirectResult = result.Handle;
                }
                """);
        }
        else if (isClassReturn)
        {
            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // Class type: retain the pointer before writing to the result buffer.
                    // Swift's wrapper will .move() this value and eventually passRetained it —
                    // the expression release consumes the original +1, so the buffer must carry
                    // its own +1 to prevent over-release when both the C# wrapper and the
                    // Swift-returned wrapper are finalized.
                    var __ptr = result.Payload.DangerousGetHandle();
                    Swift.Runtime.Arc.Retain(__ptr);
                    *(IntPtr*)indirectResult = __ptr;
                }
                """);
        }
        else
        {
            csWriter.WriteLines($$"""
                [UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>({{contextExtraction}});
                    var result = del({{invokeArgsString}});

                    // Marshal the result to the indirect result buffer
                    var metadata = TypeMetadata.GetTypeMetadataOrThrow<{{returnCSharpType}}>();
                    var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                    SwiftMarshal.MarshalToSwift(result, ref resultSpan);
                }
                """);
        }
    }

    /// <summary>
    /// Checks if the return type is Optional&lt;String&gt; (needs String-specific indirect return marshalling).
    /// </summary>
    private static bool IsOptionalStringReturn(TypeSpec returnType)
    {
        return returnType is NamedTypeSpec named &&
               named.Name == "Swift.Optional" &&
               named.GenericParameters.Count == 1 &&
               WitnessDispatchEmitter.IsStringType(named.GenericParameters[0]);
    }

    /// <summary>
    /// Checks if the return type is Array&lt;String&gt; (needs String-specific indirect return marshalling).
    /// </summary>
    private static bool IsArrayStringReturn(TypeSpec returnType)
    {
        return returnType is NamedTypeSpec named &&
               named.Name == "Swift.Array" &&
               named.GenericParameters.Count == 1 &&
               WitnessDispatchEmitter.IsStringType(named.GenericParameters[0]);
    }

    /// <summary>
    /// Emits the static field that holds the function pointer for an indirect return callback.
    /// </summary>
    /// <param name="useCdecl">When true, emit Cdecl function pointer type with IntPtr context.</param>
    public static void EmitIndirectReturnCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName, mangledName);
        // For Cdecl indirect return, build a Cdecl-based function pointer type
        var funcPtrType = useCdecl
            ? BuildIndirectReturnCdeclFunctionPointerType(closureTypeSpec, closureHandler)
            : closureHandler.GetPInvokeFunctionPointerTypeWithIndirectReturn(closureTypeSpec);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = useCdecl
            ? AddCdeclContextToFunctionPointerType(funcPtrType)
            : AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Builds a Cdecl function pointer type for indirect return callbacks.
    /// Format: delegate* unmanaged[Cdecl]&lt;void*, args..., void&gt;
    /// </summary>
    private static string BuildIndirectReturnCdeclFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var types = new List<string> { "void*" }; // indirect result buffer
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                types.Add("void*");
                types.Add("nint");
            }
            else
            {
                types.Add(GetCallbackParameterType(arg, closureHandler, useCdecl: true));
            }
        }
        types.Add("void"); // indirect return callbacks always return void
        return $"delegate* unmanaged[Cdecl]<{string.Join(", ", types)}>";
    }
}
