// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits @_cdecl invoke thunks for closure return values.
/// When a method returns a closure, C# cannot invoke the returned closure's function pointer
/// directly via delegate* unmanaged[Swift] — both Mono JIT and NativeAOT crash on indirect
/// Swift calling convention calls. The invoke thunk is a @_cdecl wrapper that reconstructs
/// the closure from its (funcPtr, context) components and invokes it via standard Swift dispatch.
/// C# calls the thunk via CallConvCdecl P/Invoke, avoiding the SwiftCC indirect call entirely.
/// </summary>
public static partial class ClosureEmitter
{
    /// <summary>
    /// Gets the @_cdecl entry point name for the invoke thunk of a closure return.
    /// Derived from the method's own @_cdecl symbol name with an "_InvCR" suffix.
    /// </summary>
    public static string GetInvokeThunkEntryPoint(string cdeclSymbolName)
    {
        return cdeclSymbolName + "_InvCR";
    }

    /// <summary>
    /// Emits a Swift @_cdecl invoke thunk for a closure return type.
    /// The thunk takes the closure's raw function pointer and context as Int parameters,
    /// reconstructs the closure via typed memory binding, and invokes it.
    /// Uses storeBytes + assumingMemoryBound(to:).pointee instead of unsafeBitCast because
    /// unsafeBitCast((Int, Int), to: ClosureType) does not handle ARC for the closure's
    /// context pointer, causing SIGSEGV crashes when the reconstructed closure is invoked.
    /// The typed pointee access properly retains the context and balances the implicit
    /// release when the local closure variable goes out of scope.
    /// </summary>
    public static void EmitSwiftInvokeThunk(
        SwiftWriter swiftWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string entryPointName,
        string swiftFuncName,
        ModuleEmissionContext? emissionContext = null)
    {
        // Register the thunk's @_cdecl symbol with the wrapper-symbol contract.
        // entryPointName is derived from the parent method's SBW_ symbol with an
        // "_InvCR" suffix, so it inherits the wrapper-entry-point prefix and
        // would trip the contract check from any Cdecl P/Invoke caller.
        emissionContext?.TryAddMethodWrapperSymbol(entryPointName);

        // Build parameter list: funcPtr (Int), context (Int), then closure args
        var swiftParams = new List<string> { "_ _funcPtr: Int", "_ _context: Int" };
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var swiftType = SwiftBuilder.GetSwiftCdeclParamType(arg, closureHandler);
            swiftParams.Add($"_ arg{argIndex}: {swiftType}");
            argIndex++;
        }

        // Determine Swift return type
        var returnsVoid = closureTypeSpec.ReturnType.IsEmptyTuple;
        string swiftReturnType;
        if (returnsVoid)
        {
            swiftReturnType = "Void";
        }
        else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
        {
            // Bool: @_cdecl maps Swift Bool to C _Bool automatically
            swiftReturnType = "Bool";
        }
        else
        {
            swiftReturnType = SwiftBuilder.GetSwiftCdeclParamType(closureTypeSpec.ReturnType, closureHandler);
        }

        var paramString = string.Join(", ", swiftParams);
        var returnClause = returnsVoid ? "" : $" -> {swiftReturnType}";

        swiftWriter.WriteLines($$"""

            // Invoke thunk for returned closure — C# calls this via CallConvCdecl instead of
            // invoking the closure's function pointer directly via delegate* unmanaged[Swift].
            @_cdecl("{{entryPointName}}")
            public func {{swiftFuncName}}({{paramString}}){{returnClause}} {
            """);
        swiftWriter.Indent++;

        // Reconstruct the closure from funcPtr + context via typed memory binding.
        // We write the two Int values into a temporary buffer and load via
        // assumingMemoryBound(to:).pointee — this performs a proper typed load that
        // handles ARC (retaining the context), unlike unsafeBitCast((Int, Int), to: ClosureType)
        // which produces a closure with an unretained context that crashes on invocation.
        var closureSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(closureTypeSpec)
            .Replace("@escaping ", "").Replace("@Sendable ", "");
        swiftWriter.WriteLines($$"""
            let _buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<(Int, Int)>.size, alignment: MemoryLayout<(Int, Int)>.alignment)
            defer { _buf.deallocate() }
            _buf.storeBytes(of: _funcPtr, as: Int.self)
            _buf.storeBytes(of: _context, toByteOffset: MemoryLayout<Int>.size, as: Int.self)
            let _closure = _buf.assumingMemoryBound(to: ({{closureSwiftType}}).self).pointee
            """);

        // Build call arguments (convert from C types back to Swift types)
        var callArgs = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (MarshallingHelpers.IsBoolType(arg))
            {
                callArgs.Add($"arg{argIndex} != 0");
            }
            else if (closureHandler.IsComplexEnum(arg))
            {
                // Complex enum: @_cdecl receives UnsafeMutableRawPointer, closure expects enum type.
                // Load the enum value from the pointer using assumingMemoryBound.
                var swiftTypeName = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg);
                callArgs.Add($"arg{argIndex}.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
            }
            else
            {
                callArgs.Add($"arg{argIndex}");
            }
            argIndex++;
        }
        var callArgsString = string.Join(", ", callArgs);

        // Call and return
        if (returnsVoid)
        {
            swiftWriter.WriteLine($"_closure({callArgsString})");
        }
        else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                 closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
        {
            // Class/ObjC return: retain the result and return as opaque pointer.
            // The caller (C#) wraps the IntPtr in SwiftClassHandle.
            swiftWriter.WriteLines($$"""
                let _result = _closure({{callArgsString}})
                return Unmanaged.passRetained(_result).toOpaque()
                """);
        }
        else
        {
            swiftWriter.WriteLine($"return _closure({callArgsString})");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Gets a unique C# static helper method name for the invoke thunk.
    /// Uses a deterministic hash of the entry point to avoid collisions.
    /// </summary>
    public static string GetInvokeThunkHelperName(string cdeclSymbolName)
    {
        var hash = EmitterUtility.DeterministicHash8(cdeclSymbolName + "_InvCR");
        return $"_InvokeClosureThunk_{hash}";
    }

    /// <summary>
    /// Gets the nested invoker class name for a closure return invoke thunk.
    /// The invoker class replaces lambdas to avoid Mono JIT !ji->async crashes
    /// that occur when native calls happen from display class methods.
    /// </summary>
    public static string GetInvokerClassName(string helperMethodName)
    {
        return $"_ClosureInv_{helperMethodName.Replace("_InvokeClosureThunk_", "")}";
    }

    /// <summary>
    /// Emits the C# infrastructure for invoking a closure return via the @_cdecl invoke thunk:
    /// 1. A [DllImport] P/Invoke declaration for the @_cdecl invoke thunk
    /// 2. A nested invoker class with an Invoke method (avoids lambdas/display classes)
    ///
    /// Uses [DllImport] (not [LibraryImport]) because [LibraryImport] source-generates a
    /// local [DllImport] function inside the method body, which Mono JIT compiles differently.
    /// Direct [DllImport] on the method itself is handled natively by the runtime and avoids
    /// the !ji->async assertion at jit-info.c:918.
    /// </summary>
    public static void EmitCSharpInvokeThunkHelper(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string helperMethodName,
        string entryPointName,
        string libraryName)
    {
        // Build P/Invoke parameter list: funcPtr, ctx, then closure args (all P/Invoke types)
        // Use nint instead of void* for complex enums to avoid requiring unsafe context
        var pinvokeParams = new List<string> { "nint funcPtr", "nint ctx" };
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var csType = closureHandler.TranslateTypeSpecToPInvokeType(arg);
            // Complex enums: TranslateTypeSpecToPInvokeType returns void*, but the invoke
            // thunk P/Invoke uses CallingConvention.Cdecl (not unmanaged[Swift]), so nint
            // is safe and avoids CS0214 unsafe context requirement
            if (csType == "void*" && closureHandler.IsComplexEnum(arg))
                csType = "nint";
            pinvokeParams.Add($"{csType} arg{argIndex}");
            argIndex++;
        }
        var pinvokeParamsString = string.Join(", ", pinvokeParams);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        string returnType;
        if (!hasReturn)
            returnType = "void";
        else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
            returnType = "byte";
        else if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
            returnType = closureHandler.GetSimpleEnumInfo(closureTypeSpec.ReturnType)?.csUnderlying ?? "int";
        else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                 closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
            // Class/ObjC: use IntPtr (not void*) to avoid requiring unsafe context at the DllImport declaration site.
            returnType = "IntPtr";
        else
            returnType = closureHandler.TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType);

        // Emit [DllImport] P/Invoke — direct runtime-handled P/Invoke (not source-generated)
        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.DllImport("{{libraryName}}", EntryPoint = "{{entryPointName}}", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]
            private static extern {{returnType}} {{helperMethodName}}({{pinvokeParamsString}});
            """);
        csWriter.WriteLine();

        // Emit nested invoker class — avoids lambdas which create display classes.
        // The invoker stores funcPtr + context and exposes an Invoke method with C# delegate
        // types. The delegate is created via method group (no display class in call chain).
        var invokerClassName = GetInvokerClassName(helperMethodName);

        // Build Invoke method params with C# delegate types (not P/Invoke types)
        var invokeParams = new List<string>();
        var invokeCallArgs = new List<string> { "_funcPtr", "_ctx" };
        int invArgIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var csType = closureHandler.TranslateTypeSpecToCSharp(arg);
            invokeParams.Add($"{csType} _arg{invArgIndex}");
            invokeCallArgs.Add(GetSwiftInvokeArgExpression(arg, invArgIndex, closureHandler, useNintCast: true));
            invArgIndex++;
        }
        var invokeParamsString = string.Join(", ", invokeParams);
        var invokeCallArgsString = string.Join(", ", invokeCallArgs);

        // Build return type and invoke body with C# delegate types
        string csReturnType;
        string invokeBody;
        if (!hasReturn)
        {
            csReturnType = "void";
            invokeBody = $"{helperMethodName}({invokeCallArgsString});";
        }
        else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
        {
            csReturnType = "bool";
            invokeBody = $"return {helperMethodName}({invokeCallArgsString}) != 0;";
        }
        else if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
        {
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"return ({csReturnType}){helperMethodName}({invokeCallArgsString});";
        }
        else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                 closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
        {
            // Class/ObjC return: P/Invoke returns IntPtr (retained pointer).
            // Wrap in SwiftHandle → class constructor. The Swift thunk calls
            // Unmanaged.passRetained(), so the SwiftClassHandle takes ownership.
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"return new {csReturnType}(new Swift.Runtime.SwiftHandle({helperMethodName}({invokeCallArgsString})));";
        }
        else
        {
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"return {helperMethodName}({invokeCallArgsString});";
        }

        csWriter.WriteLines($$"""
            private sealed class {{invokerClassName}}
            {
                private readonly nint _funcPtr;
                private readonly nint _ctx;
                internal {{invokerClassName}}(nint funcPtr, nint ctx) { _funcPtr = funcPtr; _ctx = ctx; }
                internal {{csReturnType}} Invoke({{invokeParamsString}}) { {{invokeBody}} }
            }
            """);
        csWriter.WriteLine();
    }

    /// <summary>
    /// Determines whether a closure return type can use an invoke thunk.
    /// Supports closures with primitive/enum/complex-enum args and primitive/enum/class/void returns.
    /// </summary>
    public static bool CanUseInvokeThunk(ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsInvokeThunkCompatibleArg(arg, closureHandler))
                return false;
        }

        // Check return type
        if (!closureTypeSpec.ReturnType.IsEmptyTuple)
        {
            if (!IsInvokeThunkCompatibleReturn(closureTypeSpec.ReturnType, closureHandler))
                return false;
        }

        // Throwing closures not yet supported (need error marshalling in thunk)
        if (closureTypeSpec.Throws)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a type is compatible as an invoke thunk argument.
    /// Primitives and simple enums pass directly. Complex enums pass via pointer.
    /// </summary>
    private static bool IsInvokeThunkCompatibleArg(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return true;
        if (closureHandler.IsSimpleEnum(typeSpec))
            return true;
        // Complex enums: C# extracts payload handle, Swift loads from pointer
        if (closureHandler.IsComplexEnum(typeSpec))
            return true;
        return false;
    }

    /// <summary>
    /// Checks if a type is compatible as an invoke thunk return type.
    /// Primitives, simple enums, and class/ObjC types (returned as retained pointers) are supported.
    /// </summary>
    internal static bool IsInvokeThunkCompatibleReturn(TypeSpec returnType, ClosureHandler closureHandler)
    {
        if (CdeclParamMapper.IsCdeclPrimitive(returnType))
            return true;
        if (closureHandler.IsSimpleEnum(returnType))
            return true;
        if (returnType is NamedTypeSpec named)
        {
            // Class types: Swift returns Unmanaged.passRetained().toOpaque(),
            // C# receives IntPtr and wraps in SwiftClassHandle
            if (closureHandler.IsClassType(named))
                return true;
            if (closureHandler.IsObjCBridgedClass(named))
                return true;
        }
        return false;
    }
}
