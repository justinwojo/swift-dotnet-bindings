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
    public static void EmitEscapingClosureCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName);
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);

        // Build parameter list for the callback (arguments + context as last param)
        var parameters = new List<string>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var paramType = GetCallbackParameterType(arg, closureHandler);
            parameters.Add($"{paramType} arg{argIndex}");
            argIndex++;
        }
        // Context is passed in a register (r13 on x64, x20 on ARM64) but for the callback
        // it appears as the last parameter when using Swift calling convention
        parameters.Add("IntPtr context");

        var returnType = closureTypeSpec.ReturnType.IsEmptyTuple
            ? "void"
            : GetCallbackParameterType(closureTypeSpec.ReturnType, closureHandler);

        var parametersString = string.Join(", ", parameters);

        // Build argument list for invoking the delegate
        var invokeArgs = new List<string>();
        for (int i = 0; i < argIndex; i++)
        {
            invokeArgs.Add($"arg{i}");
        }
        var invokeArgsString = string.Join(", ", invokeArgs);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static {{returnType}} {{callbackName}}({{parametersString}})
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<{{delegateType}}>(context);
                {{(hasReturn ? "return " : "")}}del({{invokeArgsString}});
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
    public static void EmitClosureCallbackPointer(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

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
    public static void EmitEscapingClosureMarshallingSetup(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureHandler closureHandler)
    {
        var callbackName = ClosureHandler.GetCallbackFunctionName(methodName, parameterName);
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
    /// </summary>
    private static string GetCallbackParameterType(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Check for pointer types
            if (IsPointerType(namedType))
                return "void*";

            // For now, use the basic translation
            // In a full implementation, we'd use the type database
            return GetBasicCSharpType(namedType.Name);
        }

        if (typeSpec.IsEmptyTuple)
            return "void";

        return "void*";
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
    /// Gets basic C# type mapping for common Swift types.
    /// </summary>
    private static string GetBasicCSharpType(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            "Swift.Int" => "nint",
            "Swift.UInt" => "nuint",
            "Swift.Int8" => "sbyte",
            "Swift.UInt8" => "byte",
            "Swift.Int16" => "short",
            "Swift.UInt16" => "ushort",
            "Swift.Int32" => "int",
            "Swift.UInt32" => "uint",
            "Swift.Int64" => "long",
            "Swift.UInt64" => "ulong",
            "Swift.Float" => "float",
            "Swift.Double" => "double",
            "Swift.Bool" => "bool",
            "Swift.Void" => "void",
            _ => "void*" // Default to pointer for unknown types
        };
    }

    /// <summary>
    /// Adds context parameter to a function pointer type string.
    /// </summary>
    private static string AddContextToFunctionPointerType(string funcPtrType)
    {
        // Transform "delegate* unmanaged[Cdecl]<int, void>" to "delegate* unmanaged[Cdecl]<int, IntPtr, void>"
        // The context is the last parameter before the return type

        // Find the last comma before '>'
        int lastAngle = funcPtrType.LastIndexOf('>');
        if (lastAngle == -1)
            return funcPtrType;

        int lastComma = funcPtrType.LastIndexOf(',', lastAngle);
        if (lastComma == -1)
        {
            // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
            // Insert "IntPtr, " before the return type
            int openAngle = funcPtrType.IndexOf('<');
            if (openAngle == -1)
                return funcPtrType;

            return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
        }

        // Insert ", IntPtr" after the last comma
        return funcPtrType.Insert(lastComma + 1, " IntPtr,");
    }
}
