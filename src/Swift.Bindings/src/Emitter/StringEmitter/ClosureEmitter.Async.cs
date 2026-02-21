// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
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
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
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
        var returnAbiType = hasReturn
            ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
            : null;

        var stateType = hasReturn
            ? $"AsyncThrowingClosureState<{returnAbiType}>"
            : "AsyncThrowingClosureStateVoid";

        // Check if the public delegate return type differs from the ABI type.
        // The delegate parameter uses projected types (e.g., string) from BuildDelegateType,
        // but AsyncThrowingClosureState<T> uses ABI types (e.g., SwiftString) for MarshalToSwift.
        // When they differ, wrap the user's delegate with ContinueWith to convert the result.
        // NOTE: Cannot use async/await here because this code may be inside an unsafe context (CS4004).
        string asyncFuncExpr = parameterName;
        if (hasReturn && returnAbiType != null)
        {
            var typeConversionHandler = new TypeConversionHandler(closureHandler.TypeDatabase);
            var paramConversion = typeConversionHandler.GetParameterConversion("r", closureTypeSpec.ReturnType);
            if (paramConversion != null)
            {
                // e.g., string → new SwiftString(r): wrap Func<Task<string>> → Func<Task<SwiftString>>
                // Use ContinueWith + Unwrap to avoid async/await in unsafe context.
                asyncFuncExpr = $"() => {parameterName}().ContinueWith(t => {{ var r = t.GetAwaiter().GetResult(); return {paramConversion}; }}, TaskContinuationOptions.ExecuteSynchronously)";
            }
        }

        csWriter.WriteLines($$"""
            var {{stateVar}} = new {{stateType}} { AsyncFunc = {{asyncFuncExpr}} };
            var {{handleVar}} = GCHandle.Alloc({{stateVar}});
            var {{parameterName}}ContextPtr = GCHandle.ToIntPtr({{handleVar}});
            """);
    }

}
