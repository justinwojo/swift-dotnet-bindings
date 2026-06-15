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
        // the `_InvCR` suffix makes this a globally unique
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
            else if (closureHandler.IsComplexEnum(arg) || IsInvokeThunkStructArg(arg, closureHandler))
            {
                // Complex enum / by-value struct: @_cdecl receives UnsafeMutableRawPointer,
                // closure expects the value type. Reload the value from the buffer pointer
                // via assumingMemoryBound(to:).pointee — a value-witness copy that retains
                // (for non-frozen) and leaves the caller's buffer intact.
                var swiftTypeName = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg);
                callArgs.Add($"arg{argIndex}.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
            }
            else if (closureHandler.IsSimpleEnum(arg))
            {
                // Simple enum: @_cdecl receives the underlying integer scalar (GetSwiftCdeclParamType
                // lowers a simple enum to enumInfo.swiftScalar), but the closure expects the enum
                // case. Reconstruct it via the same scalar→enum path GetSwiftReturnConversion uses
                // for callback args (init(rawValue:) for numeric-raw enums, a byte load for
                // tag-only/String-raw enums). Without this the thunk passes a bare Int where the
                // closure's enum parameter is required, which fails to compile.
                callArgs.Add(GetSwiftReturnConversion(arg, $"arg{argIndex}", closureHandler));
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

            string successReturn = returnsVoid
                ? "_ = ()"
                : $"return {BuildSwiftInvokeThunkReturnExpr(closureHandler, closureTypeSpec.ReturnType, swiftReturnType, "_result")}";

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
        else
        {
            // Non-void return: convert the closure result to the @_cdecl scalar/pointer the
            // thunk declares (class/ObjC → +1 retained opaque pointer, simple enum → raw scalar,
            // primitive/Bool → as-is). Returning the Swift value directly would not compile when
            // a conversion is required — e.g. a simple enum case where the thunk's return type is
            // its Int scalar.
            var returnExpr = BuildSwiftInvokeThunkReturnExpr(
                closureHandler, closureTypeSpec.ReturnType, swiftReturnType, "_result");
            if (returnExpr == "_result")
            {
                // Primitive/Bool: the result already has the scalar type — return it directly.
                swiftWriter.WriteLine($"return _closure({callArgsString})");
            }
            else
            {
                swiftWriter.WriteLines($$"""
                    let _result = _closure({{callArgsString}})
                    return {{returnExpr}}
                    """);
            }
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds the Swift expression that converts a returned-closure result (bound to
    /// <paramref name="resultExpr"/>, typed as the closure's Swift return type) into the
    /// scalar/pointer value the @_cdecl invoke thunk must return. This is the inverse of
    /// <see cref="GetSwiftReturnConversion"/> (which converts a cdecl scalar back to a Swift
    /// value on the arg/callback side). Caller guarantees the return type is non-void.
    /// </summary>
    private static string BuildSwiftInvokeThunkReturnExpr(
        ClosureHandler closureHandler,
        TypeSpec returnType,
        string swiftReturnType,
        string resultExpr)
    {
        // Bool: swiftReturnType is "Bool"; @_cdecl bridges Swift Bool to C _Bool directly.
        if (MarshallingHelpers.IsBoolType(returnType))
            return resultExpr;

        // Class/ObjC: hand back a +1 retained opaque pointer; C# wraps it in a SwiftHandle.
        if (closureHandler.IsClassType(returnType) || closureHandler.IsObjCBridgedClass(returnType))
            return $"Unmanaged.passRetained({resultExpr}).toOpaque()";

        // Simple enum: the thunk declares the underlying integer scalar (e.g. Int64), but the
        // closure yields an enum case. Numeric-raw enums convert via .rawValue, cast to the
        // scalar because the enum's Swift raw type (e.g. Int) is a distinct type from the scalar
        // (e.g. Int64). Tag-only / String-raw enums have no integer rawValue, so copy the enum's
        // tag bytes into a zero-initialised scalar — the inverse of the load(as:) reconstruction
        // GetSwiftReturnConversion emits on the arg side. The enum's MemoryLayout size never
        // exceeds the scalar's, so copyMemory's source-fits-destination precondition holds.
        var enumInfo = closureHandler.GetSimpleEnumInfo(returnType);
        if (enumInfo != null)
        {
            if (enumInfo.Value.hasRawValue)
                return $"{swiftReturnType}({resultExpr}.rawValue)";
            // Mirror the forward enum→scalar byte copy at NestedClosureBridge (tag-only path):
            // copy MemoryLayout<Enum>.size tag bytes into a zero-initialised scalar.
            var enumSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnType);
            return $"{{ var __scalar: {swiftReturnType} = 0; var __e = {resultExpr}; "
                 + $"withUnsafeMutablePointer(to: &__scalar) {{ __dst in "
                 + $"withUnsafePointer(to: &__e) {{ __src in "
                 + $"UnsafeMutableRawPointer(__dst).copyMemory(from: UnsafeRawPointer(__src), byteCount: MemoryLayout<{enumSwiftType}>.size) }} }}; "
                 + $"return __scalar }}()";
        }

        // Primitive (Int/Double/pointer/…): the closure result already has the scalar type.
        return resultExpr;
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
            // By-value structs marshal through a buffer pointer (the Swift thunk param is
            // UnsafeMutableRawPointer); pass it as nint regardless of the struct's own
            // C# projection (a frozen struct's blittable projection would otherwise be by-value).
            if (IsInvokeThunkStructArg(arg, closureHandler))
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

        // Build Invoke method params with C# delegate types (not P/Invoke types).
        // By-value struct args are marshalled into a Swift-layout buffer and passed as a
        // buffer pointer — the Swift thunk reloads via assumingMemoryBound(to:).pointee.
        // Three flavours:
        //   • Frozen value struct → stackalloc + MarshalToSwift (no heap, no cleanup).
        //   • Non-frozen struct   → heap buffer + value-witness InitializeWithCopy.
        //   • String / Foundation.Data → project to string/byte[] which have NO Swift
        //     TypeMetadata, so the generic GetTypeMetadataOrThrow<string>()/<byte[]>() path
        //     throws at runtime. Convert to the metadata-bearing runtime value (SwiftString /
        //     Foundation.Data) first, then marshal its inline Swift representation into a heap
        //     buffer via the value-witness retaining copy (MarshalToSwift).
        // Heap buffers (non-frozen + String/Data) are declared before the try, allocated
        // INSIDE it, and Destroy+Free'd in a null-guarded finally so an alloc failure on a
        // later arg never leaks an earlier arg's buffer (the buffer carries its own +1 from
        // the retaining copy and must always be released). AllocZeroed makes a Destroy on a
        // partially-initialised buffer safe (zeroed reference fields release as nil no-ops).
        var invokeParams = new List<string>();
        var invokeCallArgs = new List<string> { "_funcPtr", "_ctx" };
        var heapPreTry = new List<string>();    // declarations visible to both try and finally
        var heapInTry = new List<string>();     // allocations + initialisation (inside try, before body)
        var heapCleanup = new List<string>();   // null-guarded Destroy+Free (finally)
        var frozenPrologue = new List<string>(); // frozen stackalloc setup (no cleanup needed)
        int invArgIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var csType = closureHandler.TranslateTypeSpecToCSharp(arg);
            invokeParams.Add($"{csType} _arg{invArgIndex}");
            if (IsInvokeThunkStructArg(arg, closureHandler))
            {
                int i = invArgIndex;
                if (IsMetadataRemappedValueStructArg(arg))
                {
                    var (conv, helperType) = GetMetadataRemappedConversion(arg, i);
                    heapPreTry.Add($"using var _arg{i}Swift = {conv};");
                    heapPreTry.Add($"byte* _arg{i}Buffer = null;");
                    heapPreTry.Add($"var _arg{i}Metadata = Swift.Runtime.SwiftObjectHelper<{helperType}>.GetTypeMetadata();");
                    heapInTry.Add($"_arg{i}Buffer = (byte*)NativeMemory.AllocZeroed((nuint)_arg{i}Metadata.Size, (nuint)_arg{i}Metadata.Stride);");
                    heapInTry.Add($"var _arg{i}Span = new Span<byte>(_arg{i}Buffer, (int)_arg{i}Metadata.Size);");
                    heapInTry.Add($"((Swift.Runtime.ISwiftObject)_arg{i}Swift).MarshalToSwift(ref _arg{i}Span);");
                    heapCleanup.Add($"if (_arg{i}Buffer != null) {{ _arg{i}Metadata.ValueWitnessTable->Destroy((void*)_arg{i}Buffer, _arg{i}Metadata); NativeMemory.Free(_arg{i}Buffer); }}");
                    invokeCallArgs.Add($"(nint)_arg{i}Buffer");
                }
                else if (closureHandler.IsNonFrozenStruct(arg))
                {
                    heapPreTry.Add($"byte* _arg{i}Buffer = null;");
                    heapPreTry.Add($"var _arg{i}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csType}>();");
                    heapInTry.Add($"_arg{i}Buffer = (byte*)NativeMemory.AllocZeroed((nuint)_arg{i}Metadata.Size, (nuint)_arg{i}Metadata.Stride);");
                    heapInTry.Add($"_arg{i}Metadata.ValueWitnessTable->InitializeWithCopy((void*)_arg{i}Buffer, (void*)_arg{i}.Payload.DangerousGetHandle(), _arg{i}Metadata);");
                    heapCleanup.Add($"if (_arg{i}Buffer != null) {{ _arg{i}Metadata.ValueWitnessTable->Destroy((void*)_arg{i}Buffer, _arg{i}Metadata); NativeMemory.Free(_arg{i}Buffer); }}");
                    invokeCallArgs.Add($"(nint)_arg{i}Buffer");
                }
                else
                {
                    frozenPrologue.Add($"var _arg{i}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csType}>();");
                    frozenPrologue.Add($"byte* _arg{i}Buffer = stackalloc byte[(int)_arg{i}Metadata.Size];");
                    frozenPrologue.Add($"var _arg{i}Span = new Span<byte>(_arg{i}Buffer, (int)_arg{i}Metadata.Size);");
                    frozenPrologue.Add($"SwiftMarshal.MarshalToSwift(_arg{i}, ref _arg{i}Span);");
                    invokeCallArgs.Add($"(nint)_arg{i}Buffer");
                }
            }
            else
            {
                invokeCallArgs.Add(GetSwiftInvokeArgExpression(arg, invArgIndex, closureHandler, useNintCast: true));
            }
            invArgIndex++;
        }
        var invokeParamsString = string.Join(", ", invokeParams);
        var invokeCallArgsString = string.Join(", ", invokeCallArgs);
        var hasStructArg = heapPreTry.Count > 0 || frozenPrologue.Count > 0;

        // Build return type and invoke body with C# delegate types. Throwing closures
        // wrap the call in SwiftResult<T, SwiftError>: the user's delegate is typed
        // `Func<..., SwiftResult<T, SwiftError>>` (cf. ClosureHandler.GetCSharpDelegateType).
        string csReturnType;
        string invokeBody;
        // Throwing closures need `unsafe` to construct `SwiftError` from the raw error
        // pointer; struct args need it for stackalloc/NativeMemory/value-witness pointers.
        var invokeModifier = (isThrowing || hasStructArg) ? "internal unsafe" : "internal";
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

        // By-value struct args: emit the marshalling prologue ahead of the thunk call.
        // Heap buffers (non-frozen + String/Data) declare null pointers before a try, allocate
        // and initialise inside it, and Destroy+Free in a null-guarded finally (the early
        // `return`s inside invokeBody run through finally). Frozen stackalloc args carry no
        // cleanup and only need their prologue ahead of the call.
        if (hasStructArg)
        {
            if (heapCleanup.Count > 0)
            {
                var preTryStr = string.Join(" ", heapPreTry);
                var inTryStr = string.Join(" ", heapInTry.Concat(frozenPrologue));
                var cleanupStr = string.Join(" ", heapCleanup);
                invokeBody = $"{preTryStr} try {{ {inTryStr} {invokeBody} }} finally {{ {cleanupStr} }}";
            }
            else
            {
                var prologueStr = string.Join(" ", frozenPrologue);
                invokeBody = $"{prologueStr} {invokeBody}";
            }
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
    /// Primitives and simple enums pass directly. Complex enums and by-value
    /// structs pass via pointer (the @_cdecl thunk param is UnsafeMutableRawPointer
    /// and the body reloads the value via assumingMemoryBound(to:).pointee).
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
        // By-value structs (frozen or non-frozen): C# marshals into a buffer and
        // passes the pointer; Swift reloads via assumingMemoryBound(to:).pointee.
        // This keeps struct-arg closure returns on the safe CallConvCdecl invoker
        // path instead of the raw delegate* unmanaged[Swift] lambda, which SIGSEGVs
        // when invoked from a display-class method on Mono JIT / NativeAOT.
        if (IsInvokeThunkStructArg(typeSpec, closureHandler))
            return true;
        return false;
    }

    /// <summary>
    /// Whether a closure argument is a by-value Swift struct that the invoke thunk
    /// marshals through a buffer pointer (frozen → stackalloc + MarshalToSwift,
    /// non-frozen → NativeMemory + InitializeWithCopy/Destroy). Primitives are
    /// excluded even though stdlib primitives are frozen structs — they pass by value.
    /// Optionals and generics are already excluded by IsFrozenStruct/IsNonFrozenStruct.
    /// </summary>
    internal static bool IsInvokeThunkStructArg(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return false;
        return closureHandler.IsFrozenStruct(typeSpec) || closureHandler.IsNonFrozenStruct(typeSpec);
    }

    /// <summary>
    /// Whether a by-value struct arg is a stdlib/Foundation value type whose C# projection
    /// (Swift.String → string, Foundation.Data → byte[]) carries NO Swift TypeMetadata, so
    /// the generic GetTypeMetadataOrThrow path throws at runtime. These are the only two
    /// frozen value structs that project to a metadata-less C# type — every other frozen
    /// remap (Date/UUID) projects to a generated struct that does carry metadata, and
    /// Foundation.Data is the sole frozen struct with a nativeType= remap.
    /// </summary>
    private static bool IsMetadataRemappedValueStructArg(TypeSpec typeSpec)
        => WitnessDispatchEmitter.IsStringType(typeSpec)
           || (typeSpec is NamedTypeSpec { Name: "Foundation.Data" });

    /// <summary>
    /// Returns the (conversion expression, runtime helper type) used to marshal a
    /// metadata-remapped value struct arg: the C# projection is converted to the
    /// metadata-bearing runtime value (SwiftString / Foundation.Data) so MarshalToSwift can
    /// write its inline Swift representation into the invoke-thunk buffer. Mirrors the String
    /// return path in ClosureEmitter.cs.
    /// </summary>
    private static (string conversion, string helperType) GetMetadataRemappedConversion(TypeSpec typeSpec, int argIndex)
    {
        if (WitnessDispatchEmitter.IsStringType(typeSpec))
            // Swift.SwiftString lives in Swift.Runtime, which every generated binding already
            // references — no Apple supplement dependency needed.
            return ($"new Swift.SwiftString(_arg{argIndex})", "Swift.SwiftString");
        // Foundation.Data → byte[]: build a metadata-bearing Foundation.Data from the bytes.
        // The closure-delegate translation maps Foundation.Data straight to byte[]
        // (ClosureHandler), bypassing the projection path that records the supplement reference
        // (TypeProjectionFactory:324). Record it here so a module whose ONLY Foundation.Data use
        // is a returned-closure argument still emits the SwiftBindings.Apple PackageReference;
        // otherwise the generated invoker references Swift.Foundation.Data with no project ref.
        AppleSupplementReferences.Record("Foundation.Data");
        return ($"Swift.Foundation.Data.FromByteArray(_arg{argIndex})", "Swift.Foundation.Data");
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
