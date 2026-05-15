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
        // S5 audited (Tier C): the `_InvCR` suffix makes this a globally unique
        // shape per (parent-method cdecl, closure-arg index); no regular method or
        // property wrapper can ever produce this string. Per-kind method bucket is
        // collision-safe.
        emissionContext?.TryAddMethodWrapperSymbol(entryPointName);

        var isThrowing = closureTypeSpec.Throws;

        // Build parameter list: funcPtr (Int), context (Int), then closure args, then
        // (for throwing closures) an explicit error-out pointer. Cdecl exits via
        // explicit error-out, not the SwiftCC SwiftError register.
        var swiftParams = new List<string> { "_ _funcPtr: Int", "_ _context: Int" };
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var swiftType = SwiftBuilder.GetSwiftCdeclParamType(arg, closureHandler);
            swiftParams.Add($"_ arg{argIndex}: {swiftType}");
            argIndex++;
        }
        if (isThrowing)
        {
            swiftParams.Add("_ _errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
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
        // For throwing closures we render the type WITH `throws` so Swift parses the
        // typed-memory-binding cast correctly; `@escaping` and `@Sendable` are stripped
        // because they're attribute decorators, not part of the closure's stored type.
        var renderedSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(closureTypeSpec)
            .Replace("@escaping ", "").Replace("@Sendable ", "");
        swiftWriter.WriteLines($$"""
            let _buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<(Int, Int)>.size, alignment: MemoryLayout<(Int, Int)>.alignment)
            defer { _buf.deallocate() }
            _buf.storeBytes(of: _funcPtr, as: Int.self)
            _buf.storeBytes(of: _context, toByteOffset: MemoryLayout<Int>.size, as: Int.self)
            let _closure = _buf.assumingMemoryBound(to: ({{renderedSwiftType}}).self).pointee
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
        if (isThrowing)
        {
            // Throwing closure: wrap the invocation in do/catch, marshal the Swift `Error`
            // value into the @_cdecl `_errorOut` parameter on failure, and return a default
            // value (caller must inspect _errorOut before reading the return). The error is
            // retained (+1) via Unmanaged.passRetained so the C# side owns one reference.
            // Cdecl explicit-pointer ABI is the correct family here per codex Q4 — Cdecl
            // does NOT route through the SwiftSelf/SwiftError register convention.
            string defaultReturnExpr;
            if (returnsVoid)
                defaultReturnExpr = "return";
            else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
                defaultReturnExpr = "return false";
            else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                     closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
                // Class/ObjC pointer: UnsafeMutableRawPointer can't be `nil` because the
                // Swift return type is non-optional. Construct a bitPattern-0 pointer; C#
                // ignores the return when _errorOut is non-zero.
                defaultReturnExpr = "return UnsafeMutableRawPointer(bitPattern: -1)!";
            else if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
            {
                var enumInfo = closureHandler.GetSimpleEnumInfo(closureTypeSpec.ReturnType);
                var scalar = enumInfo?.swiftScalar ?? "Int";
                defaultReturnExpr = $"return {scalar}(0)";
            }
            else
                // Primitive: use 0-initialised value. C# discards the result on error.
                defaultReturnExpr = $"return {swiftReturnType}(0)";

            string successReturn;
            if (returnsVoid)
            {
                successReturn = "_ = ()";
            }
            else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                     closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
            {
                successReturn = "return Unmanaged.passRetained(_result).toOpaque()";
            }
            else
            {
                successReturn = "return _result";
            }

            // do { try _closure(...) } catch { marshal error; return default }
            if (returnsVoid)
            {
                swiftWriter.WriteLines($$"""
                    do {
                        try _closure({{callArgsString}})
                    } catch {
                        _errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                        {{defaultReturnExpr}}
                    }
                    """);
            }
            else
            {
                swiftWriter.WriteLines($$"""
                    do {
                        let _result = try _closure({{callArgsString}})
                        {{successReturn}}
                    } catch {
                        _errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                        {{defaultReturnExpr}}
                    }
                    """);
            }
        }
        else if (returnsVoid)
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
        var isThrowing = closureTypeSpec.Throws;

        // Build P/Invoke parameter list: funcPtr, ctx, then closure args (all P/Invoke types)
        // Use nint instead of void* for complex enums to avoid requiring unsafe context.
        // Throwing closures append an `out IntPtr errorOut` parameter that the Swift Cdecl
        // thunk fills with a retained Swift `Error` reference; non-zero means failure
        // (cf. EmitSwiftInvokeThunk's _errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>).
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
        // RuntimeTestsApp (and other consumers) set DisableRuntimeMarshalling, which forbids
        // by-ref params (CA1420). Use an unmanaged pointer instead — IntPtr* is blittable and
        // marshals trivially. Inside the unsafe Invoke method we pass `&_err`.
        if (isThrowing)
            pinvokeParams.Add("IntPtr* errorOut");
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

        // Emit [DllImport] P/Invoke — direct runtime-handled P/Invoke (not source-generated).
        // Throwing form takes IntPtr* and therefore requires `unsafe` on the declaration.
        var pinvokeModifier = isThrowing ? "private static unsafe extern" : "private static extern";
        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.DllImport("{{libraryName}}", EntryPoint = "{{entryPointName}}", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]
            {{pinvokeModifier}} {{returnType}} {{helperMethodName}}({{pinvokeParamsString}});
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

        // Build return type and invoke body with C# delegate types. Throwing closures
        // wrap the call in SwiftResult<T, SwiftError>: the user's delegate is typed
        // `Func<..., SwiftResult<T, SwiftError>>` (cf. ClosureHandler.GetCSharpDelegateType).
        string csReturnType;
        string invokeBody;
        // Throwing closures need `unsafe` on the Invoke method to construct
        // `SwiftError` from the raw error pointer (`SwiftError` only exposes a
        // `void*` constructor; `Value` is read-only).
        var invokeModifier = isThrowing ? "internal unsafe" : "internal";
        if (isThrowing)
        {
            // The success/Void/class transformations mirror the non-throwing branch — the
            // wrap into SwiftResult is the only delta.
            var successType = hasReturn
                ? closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true)
                : "Swift.SwiftVoid";
            csReturnType = $"Swift.SwiftResult<{successType}, SwiftError>";
            // Inject the `&_err` pointer argument right after the closure args, matching the
            // `IntPtr* errorOut` P/Invoke signature emitted above. We are already in an
            // unsafe method, so taking the address of the stack local is direct.
            var throwingCallArgs = invokeCallArgsString + ", &_err";
            // Failure path: build SwiftError wrapping _err and hand it to SwiftResult.FromFailure
            // unchanged. The Swift error was retained (+1) by Unmanaged.passRetained in the
            // @_cdecl thunk; managed code holds that one reference for the SwiftError's lifetime.
            // Matches SwiftErrorException.Error and the wrapped-callback round-trip path
            // (ClosureEmitter.Throwing.cs) — managed code never releases SwiftError pointers,
            // it forwards them. A future Dispose-able failure carrier could plug the per-error
            // leak without breaking the lifetime convention; that refactor is out of scope here.
            var swiftErrorCtor = "new SwiftError((void*)_err)";
            var failureExpr = $"return {csReturnType}.FromFailure({swiftErrorCtor});";
            string successExpr;
            if (!hasReturn)
            {
                successExpr = "Swift.SwiftVoid.Value";
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"{helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess({successExpr});";
            }
            else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
            {
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(_raw != 0);";
            }
            else if (closureHandler.IsSimpleEnum(closureTypeSpec.ReturnType))
            {
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(({successType})_raw);";
            }
            else if (closureHandler.IsClassType(closureTypeSpec.ReturnType) ||
                     closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
            {
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(new {successType}(new Swift.Runtime.SwiftHandle(_raw)));";
            }
            else
            {
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(_raw);";
            }
        }
        else if (!hasReturn)
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

        // `_retainHolder` keeps the SwiftEscapingClosure wrapper alive for as long as the
        // invoker (and therefore the user-visible delegate built from `Invoke` as a method
        // group) is reachable. Without it, the wrapper goes out of scope after the receiver
        // returns, leaking its Arc.Retain'd Swift context permanently — and if a finalizer
        // is ever added to SwiftEscapingClosure, that same path would flip to a dangling
        // pointer because the captured Action holds only raw nint pointers.
        csWriter.WriteLines($$"""
            private sealed class {{invokerClassName}}
            {
                private readonly nint _funcPtr;
                private readonly nint _ctx;
                private readonly object? _retainHolder;
                internal {{invokerClassName}}(nint funcPtr, nint ctx, object? retainHolder = null) { _funcPtr = funcPtr; _ctx = ctx; _retainHolder = retainHolder; }
                {{invokeModifier}} {{csReturnType}} Invoke({{invokeParamsString}}) { {{invokeBody}} }
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

        // Async closures still need new Task-based dispatch machinery — out of scope here.
        if (closureTypeSpec.IsAsync)
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
