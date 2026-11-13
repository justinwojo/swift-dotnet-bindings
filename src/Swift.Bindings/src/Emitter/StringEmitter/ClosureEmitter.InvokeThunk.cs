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

        // A result the thunk cannot return as one C scalar (an existential, a tuple) leaves
        // through out-pointers the invoker supplies, placed after the closure's own arguments;
        // the thunk itself then returns Void.
        var resultOuts = GetInvokeThunkResultOuts(closureTypeSpec.ReturnType, closureHandler);

        // Build parameter list: funcPtr (Int), context (Int), then closure args, then any
        // result out-pointers, then (for throwing closures) an explicit error-out pointer.
        // Cdecl exits via explicit error-out, not the SwiftCC SwiftError register.
        var swiftParams = new List<string> { "_ _funcPtr: Swift.Int", "_ _context: Swift.Int" };
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // An existential arrives as the address of a container the invoker built.
            var swiftType = closureHandler.IsInvokeThunkExistential(arg)
                ? "Swift.UnsafeRawPointer"
                : SwiftBuilder.GetSwiftCdeclParamType(arg, closureHandler);
            swiftParams.Add($"_ arg{argIndex}: {swiftType}");
            argIndex++;
        }
        foreach (var resultOut in resultOuts)
        {
            swiftParams.Add($"_ {resultOut}: Swift.UnsafeMutableRawPointer");
        }
        if (isThrowing)
        {
            swiftParams.Add("_ _errorOut: Swift.UnsafeMutablePointer<Swift.UnsafeMutableRawPointer?>");
        }

        // Determine Swift return type
        var returnsVoid = closureTypeSpec.ReturnType.IsEmptyTuple || resultOuts.Count > 0;
        string swiftReturnType;
        if (returnsVoid)
        {
            swiftReturnType = "Swift.Void";
        }
        else if (MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType))
        {
            // Bool: @_cdecl maps Swift Bool to C _Bool automatically
            swiftReturnType = "Swift.Bool";
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
            let _buf = Swift.UnsafeMutableRawPointer.allocate(byteCount: Swift.MemoryLayout<(Swift.Int, Swift.Int)>.size, alignment: Swift.MemoryLayout<(Swift.Int, Swift.Int)>.alignment)
            defer { _buf.deallocate() }
            _buf.storeBytes(of: _funcPtr, as: Swift.Int.self)
            _buf.storeBytes(of: _context, toByteOffset: Swift.MemoryLayout<Swift.Int>.size, as: Swift.Int.self)
            let _closure = _buf.assumingMemoryBound(to: ({{renderedSwiftType}}).self).pointee
            """);

        // Build call arguments (convert from C types back to Swift types)
        var callArgs = new List<string>();
        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (closureHandler.IsInvokeThunkExistential(arg))
            {
                // Existential: a copying load out of the invoker's container. The copy is the
                // closure's own (Swift releases it after the call); the invoker's container is left
                // intact for the invoker to destroy.
                callArgs.Add($"arg{argIndex}.load(as: {RenderExistentialMetatypeOperand(arg, closureHandler)}.self)");
            }
            else if (MarshallingHelpers.IsBoolType(arg))
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
            else if (IsInvokeThunkReferenceArg(arg, closureHandler))
            {
                // Class / ObjC class: @_cdecl receives the object pointer. The managed caller keeps
                // its own reference alive across the call, so the thunk borrows (+0) rather than
                // consuming a retain it was never handed.
                var swiftTypeName = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg);
                callArgs.Add($"Swift.Unmanaged<{swiftTypeName}>.fromOpaque(arg{argIndex}).takeUnretainedValue()");
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
                defaultReturnExpr = "return Swift.UnsafeMutableRawPointer(bitPattern: -1)!";
            else
                // Primitive or simple enum: zero of the type the thunk actually declares. Reading
                // that off `swiftReturnType` rather than re-deriving a scalar here is what keeps
                // the sentinel and the signature from drifting apart — a second derivation can
                // disagree with the first (an enum whose info lookup declines where the simple-enum
                // predicate accepted it), and the mismatch surfaces as a wrapper that stops
                // compiling. C# discards the result when the error out-param is set.
                defaultReturnExpr = $"return {swiftReturnType}(0)";

            string successReturn = returnsVoid
                ? "_ = ()"
                : $"return {BuildSwiftInvokeThunkReturnExpr(closureHandler, closureTypeSpec.ReturnType, swiftReturnType, "_result")}";

            // do { try _closure(...) } catch { marshal error; return default }
            ThrowingWrapperErrorContractEmitter.EmitInitialization(swiftWriter, "_errorOut");
            if (resultOuts.Count > 0)
            {
                // Result through out-pointers: written only on success. On a throw they stay
                // uninitialised and the invoker never reads them.
                swiftWriter.WriteLine("do {");
                swiftWriter.Indent++;
                swiftWriter.WriteLine($"let _result = try _closure({callArgsString})");
                EmitInvokeThunkResultStores(swiftWriter, closureTypeSpec.ReturnType, closureHandler, "_result");
                swiftWriter.Indent--;
                swiftWriter.WriteLines($$"""
                    } catch {
                        _errorOut.pointee = Swift.Unmanaged.passRetained(error as Swift.AnyObject).toOpaque()
                        {{defaultReturnExpr}}
                    }
                    """);
            }
            else if (returnsVoid)
            {
                swiftWriter.WriteLines($$"""
                    do {
                        try _closure({{callArgsString}})
                    } catch {
                        _errorOut.pointee = Swift.Unmanaged.passRetained(error as Swift.AnyObject).toOpaque()
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
                        _errorOut.pointee = Swift.Unmanaged.passRetained(error as Swift.AnyObject).toOpaque()
                        {{defaultReturnExpr}}
                    }
                    """);
            }
        }
        else if (resultOuts.Count > 0)
        {
            swiftWriter.WriteLine($"let _result = _closure({callArgsString})");
            EmitInvokeThunkResultStores(swiftWriter, closureTypeSpec.ReturnType, closureHandler, "_result");
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
            return $"Swift.Unmanaged.passRetained({resultExpr}).toOpaque()";

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
                 + $"Swift.withUnsafeMutablePointer(to: &__scalar) {{ __dst in "
                 + $"Swift.withUnsafePointer(to: &__e) {{ __src in "
                 + $"Swift.UnsafeMutableRawPointer(__dst).copyMemory(from: Swift.UnsafeRawPointer(__src), byteCount: Swift.MemoryLayout<{enumSwiftType}>.size) }} }}; "
                 + $"return __scalar }}()";
        }

        // Primitive (Int/Double/pointer/…): the closure result already has the scalar type.
        return resultExpr;
    }

    /// <summary>
    /// The out-pointer parameter names a returned closure's result needs, in thunk-signature
    /// order: one for an existential (the address of a container), one per element for a tuple,
    /// none for a result the thunk returns directly as a C scalar or pointer. The Swift thunk and
    /// the C# invoker both read the parameter list from this, so the two signatures cannot drift.
    /// </summary>
    internal static IReadOnlyList<string> GetInvokeThunkResultOuts(TypeSpec returnType, ClosureHandler closureHandler)
    {
        if (closureHandler.IsInvokeThunkExistential(returnType))
            return ["_resultOut"];
        if (IsInvokeThunkTupleReturn(returnType, closureHandler))
            return ((TupleTypeSpec)returnType).Elements.Select((_, i) => $"_resultOut{i}").ToList();
        return [];
    }

    /// <summary>
    /// Writes a returned closure's result (bound to <paramref name="resultExpr"/>) into the
    /// out-pointers <see cref="GetInvokeThunkResultOuts"/> names. An existential is initialised
    /// in place — the invoker adopts that copy at +1. A tuple stores each element as the scalar
    /// the invoker reads it back as: Bool as one byte, a simple enum as its integer scalar.
    /// </summary>
    private static void EmitInvokeThunkResultStores(
        SwiftWriter swiftWriter, TypeSpec returnType, ClosureHandler closureHandler, string resultExpr)
    {
        if (closureHandler.IsInvokeThunkExistential(returnType))
        {
            swiftWriter.WriteLine(
                $"_resultOut.initializeMemory(as: {RenderExistentialMetatypeOperand(returnType, closureHandler)}.self, repeating: {resultExpr}, count: 1)");
            return;
        }
        var tuple = (TupleTypeSpec)returnType;
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var element = tuple.Elements[i];
            var scalarType = MarshallingHelpers.IsBoolType(element)
                ? "Swift.Bool"
                : SwiftBuilder.GetSwiftCdeclParamType(element, closureHandler);
            var value = BuildSwiftInvokeThunkReturnExpr(closureHandler, element, scalarType, $"{resultExpr}.{i}");
            swiftWriter.WriteLine($"_resultOut{i}.storeBytes(of: {value}, as: {scalarType}.self)");
        }
    }

    /// <summary>
    /// The existential's Swift type as a metatype operand: <c>(any P)</c>, parenthesised so that
    /// <c>.self</c> binds to the whole existential rather than to the protocol name.
    /// </summary>
    private static string RenderExistentialMetatypeOperand(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        var swiftType = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(typeSpec, closureHandler.TypeDatabase);
        return swiftType.StartsWith("any ") ? $"({swiftType})" : swiftType;
    }

    /// <summary>
    /// Whether a returned closure's tuple result is one the thunk hands back element by element:
    /// every element a numeric primitive, Bool, or simple enum — values with a fixed scalar
    /// spelling on both sides that the invoker reassembles into the C# tuple.
    /// </summary>
    internal static bool IsInvokeThunkTupleReturn(TypeSpec returnType, ClosureHandler closureHandler)
    {
        if (returnType is not TupleTypeSpec tuple || tuple.IsEmptyTuple || tuple.Elements.Count < 2)
            return false;
        foreach (var element in tuple.Elements)
        {
            if (element.IsInOut)
                return false;
            if (MarshallingHelpers.IsBoolType(element) || closureHandler.IsSimpleEnum(element))
                continue;
            if (element is NamedTypeSpec named && ClosureHandler.GetBlittablePrimitiveType(named.Name) != null)
                continue;
            return false;
        }
        return true;
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
    /// <param name="invokerIsExternallyVisible">
    /// True when the invoker class is emitted somewhere other than the member that constructs it —
    /// on a generic parent both declarations are hoisted into the non-generic <c>{Parent}_PInvoke</c>
    /// class, because a <c>[DllImport]</c> inside a generic type is CS7042. The class must then be
    /// <c>internal</c> rather than <c>private</c> so the member's body can still name it. The
    /// <c>[DllImport]</c> itself stays private either way: only the invoker calls it.
    /// </param>
    public static void EmitCSharpInvokeThunkHelper(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string helperMethodName,
        string entryPointName,
        string libraryName,
        bool invokerIsExternallyVisible = false)
    {
        var isThrowing = closureTypeSpec.Throws;
        var resultOuts = GetInvokeThunkResultOuts(closureTypeSpec.ReturnType, closureHandler);

        // Build P/Invoke parameter list: funcPtr, ctx, then closure args (all P/Invoke types)
        // Use nint instead of void* for complex enums to avoid requiring unsafe context.
        // Throwing closures append an `out IntPtr errorOut` parameter that the Swift Cdecl
        // thunk fills with a retained Swift `Error` reference; non-zero means failure
        // (cf. EmitSwiftInvokeThunk's _errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>).
        var pinvokeParams = new List<string> { "nint funcPtr", "nint ctx" };
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // An existential travels as the address of a container the invoker builds.
            if (closureHandler.IsInvokeThunkExistential(arg))
            {
                pinvokeParams.Add($"nint arg{argIndex}");
                argIndex++;
                continue;
            }
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
            // Class / ObjC class: the object pointer, as nint so the declaration needs no unsafe context.
            if (IsInvokeThunkReferenceArg(arg, closureHandler))
                csType = "nint";
            pinvokeParams.Add($"{csType} arg{argIndex}");
            argIndex++;
        }
        // A result that is not one C scalar comes back through out-pointers, in the order the Swift
        // thunk declares them (after the arguments, before the error out-pointer).
        foreach (var resultOut in resultOuts)
            pinvokeParams.Add($"nint {resultOut}");
        // RuntimeTestsApp (and other consumers) set DisableRuntimeMarshalling, which forbids
        // by-ref params (CA1420). Use an unmanaged pointer instead — IntPtr* is blittable and
        // marshals trivially. Inside the unsafe Invoke method we pass `&_err`.
        if (isThrowing)
            pinvokeParams.Add("IntPtr* errorOut");
        var pinvokeParamsString = string.Join(", ", pinvokeParams);

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        string returnType;
        if (!hasReturn || resultOuts.Count > 0)
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
            if (closureHandler.IsInvokeThunkExistential(arg))
            {
                AddInvokeThunkExistentialArg(arg, invArgIndex, closureHandler, heapPreTry, heapInTry, heapCleanup);
                invokeCallArgs.Add($"(nint)_arg{invArgIndex}Heap");
            }
            else if (IsInvokeThunkStructArg(arg, closureHandler))
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
            else if (closureHandler.IsClassType(arg))
            {
                // Swift class: hold a SafeHandle reference so a concurrent Dispose cannot free the
                // object while Swift is borrowing it; released even if the call throws.
                int i = invArgIndex;
                heapPreTry.Add($"bool _arg{i}Ref = false;");
                heapInTry.Add($"_arg{i}.Payload.DangerousAddRef(ref _arg{i}Ref);");
                heapCleanup.Add($"if (_arg{i}Ref) _arg{i}.Payload.DangerousRelease();");
                invokeCallArgs.Add($"_arg{i}.Payload.DangerousGetHandle()");
            }
            else if (IsInvokeThunkReferenceArg(arg, closureHandler))
            {
                // ObjC class: the managed wrapper owns the native reference; keep it reachable
                // until Swift returns.
                int i = invArgIndex;
                heapCleanup.Add($"global::System.GC.KeepAlive(_arg{i});");
                invokeCallArgs.Add($"(nint)_arg{i}.Handle");
            }
            else
            {
                invokeCallArgs.Add(GetSwiftInvokeArgExpression(arg, invArgIndex, closureHandler, useNintCast: true));
            }
            invArgIndex++;
        }
        var invokeParamsString = string.Join(", ", invokeParams);
        var hasStructArg = heapPreTry.Count > 0 || frozenPrologue.Count > 0;

        // A result read back through out-pointers: the locals the thunk writes into, their
        // addresses (appended after the closure arguments), and the C# value built from them.
        string resultOutDecls = "";
        string? resultOutValue = null;
        if (resultOuts.Count > 0)
        {
            var (decls, outArgs, value) = BuildInvokeThunkResultRead(closureTypeSpec.ReturnType, closureHandler);
            resultOutDecls = decls;
            resultOutValue = value;
            invokeCallArgs.AddRange(outArgs);
        }
        var invokeCallArgsString = string.Join(", ", invokeCallArgs);

        // Build return type and invoke body with C# delegate types. Throwing closures
        // wrap the call in SwiftResult<T, SwiftError>: the user's delegate is typed
        // `Func<..., SwiftResult<T, SwiftError>>` (cf. ClosureHandler.GetCSharpDelegateType).
        string csReturnType;
        string invokeBody;
        // Throwing closures need `unsafe` to construct `SwiftError` from the raw error
        // pointer; struct args need it for stackalloc/NativeMemory/value-witness pointers, and
        // result out-pointers for the addresses of their locals.
        var invokeModifier = (isThrowing || hasStructArg || resultOuts.Count > 0) ? "internal unsafe" : "internal";
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
            if (resultOutValue != null)
            {
                // The out-pointers are written only on success; on a throw they stay zeroed and
                // are never read.
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; {resultOutDecls} " +
                    $"{helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess({resultOutValue});";
            }
            else if (!hasReturn)
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
            else if (closureHandler.IsClassType(closureTypeSpec.ReturnType))
            {
                // Pure Swift class success: _raw is an owned +1 (passRetained). Route through the
                // public MarshalFromSwiftObject<T> factory: a generated class's (SwiftHandle) ctor is
                // private to the class. The factory adopts the +1, so ownership transfer is identical.
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(SwiftMarshal.MarshalFromSwiftObject<{successType}>(_raw));";
            }
            else if (closureHandler.IsObjCRootedClass(closureTypeSpec.ReturnType))
            {
                // ObjC-rooted Swift class success: the same factory. The class's handle constructor
                // is private to it; NewFromPayload reaches it, adopting the +1, and reuses the
                // object's managed peer when it already has one.
                invokeBody =
                    $"IntPtr _err = IntPtr.Zero; " +
                    $"var _raw = {helperMethodName}({throwingCallArgs}); " +
                    $"if (_err != IntPtr.Zero) {{ {failureExpr} }} " +
                    $"return {csReturnType}.FromSuccess(SwiftMarshal.MarshalFromSwiftObject<{successType}>(_raw));";
            }
            else if (closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
            {
                // ObjC-bridged success: kept on the SwiftHandle-ctor path (their
                // ISwiftObject/NewFromPayload story differs — out of scope for the factory routing).
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
        else if (resultOutValue != null)
        {
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"{resultOutDecls} {helperMethodName}({invokeCallArgsString}); return {resultOutValue};";
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
        else if (closureHandler.IsClassType(closureTypeSpec.ReturnType))
        {
            // Pure Swift class return: P/Invoke returns IntPtr, an owned +1 pointer (the Swift
            // thunk calls Unmanaged.passRetained()). Route through the public
            // MarshalFromSwiftObject<T> factory: a generated class's (SwiftHandle) ctor is private
            // to the class. The factory adopts the +1, so ownership transfer is identical.
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"return SwiftMarshal.MarshalFromSwiftObject<{csReturnType}>({helperMethodName}({invokeCallArgsString}));";
        }
        else if (closureHandler.IsObjCRootedClass(closureTypeSpec.ReturnType))
        {
            // ObjC-rooted Swift class return: P/Invoke returns IntPtr (retained pointer). Same
            // factory: the class's handle constructor is private to it, and NewFromPayload adopts
            // the +1 and reuses the object's managed peer when it already has one.
            csReturnType = closureHandler.TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
            invokeBody = $"return SwiftMarshal.MarshalFromSwiftObject<{csReturnType}>({helperMethodName}({invokeCallArgsString}));";
        }
        else if (closureHandler.IsObjCBridgedClass(closureTypeSpec.ReturnType))
        {
            // ObjC-bridged return: P/Invoke returns IntPtr (retained pointer). Kept on the
            // SwiftHandle-ctor path — their ISwiftObject/NewFromPayload story differs (out of
            // scope for the cross-module class-pointer factory routing).
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
        if (hasStructArg || heapCleanup.Count > 0)
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
        var invokerAccessibility = invokerIsExternallyVisible ? "internal" : "private";
        csWriter.WriteLines($$"""
            {{invokerAccessibility}} sealed class {{invokerClassName}}
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
    /// Builds the container an existential closure argument travels in — the same recipe an
    /// existential method parameter uses. A single-protocol existential goes through
    /// <c>GetOrCreate</c>: a Swift-vended conformer hands over its own container, a C#-authored one
    /// is wrapped in the protocol's proxy (or, when no proxy class is emitted, only a Swift-vended
    /// conformer is accepted). <c>any Error</c> hands over its wrapper's container. The container is
    /// copied to the heap, the thunk takes its own copy of the existential out of it, and the finally
    /// destroys only a container <c>GetOrCreate</c> boxed at +1, frees the buffer, and keeps the
    /// source reachable until Swift has returned.
    /// </summary>
    private static void AddInvokeThunkExistentialArg(
        TypeSpec arg,
        int index,
        ClosureHandler closureHandler,
        List<string> preTry,
        List<string> inTry,
        List<string> cleanup)
    {
        const string container = "global::Swift.Runtime.ExistentialContainer1";
        preTry.Add($"void* _arg{index}Heap = null;");
        string ownsExpr;
        string keepAliveExpr;
        if (closureHandler.NeedsWellKnownProtocolWrapping(arg, out _))
        {
            inTry.Add($"var _arg{index}Container = ((global::Swift.Runtime.ISwiftExistentialConvertible<{container}>)_arg{index}).GetExistentialContainer();");
            ownsExpr = "false";
            keepAliveExpr = $"_arg{index}";
        }
        else
        {
            preTry.Add($"bool _arg{index}Owns = false;");
            preTry.Add($"object? _arg{index}KeepAlive = null;");
            var publicType = ResolveClosureExistentialOrDegrade(closureHandler, arg);
            var proxy = closureHandler.GetQualifiedProxyClassName(arg);
            inTry.Add(proxy != null
                ? $"var _arg{index}Container = global::Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>(_arg{index}, static __v => new {proxy}(__v), out _arg{index}Owns, out _arg{index}KeepAlive);"
                : $"var _arg{index}Container = global::Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>(_arg{index}, out _arg{index}Owns, out _arg{index}KeepAlive);");
            ownsExpr = $"_arg{index}Owns";
            keepAliveExpr = $"_arg{index}KeepAlive";
        }
        inTry.Add($"_arg{index}Heap = global::System.Runtime.InteropServices.NativeMemory.Alloc((nuint)global::System.Runtime.CompilerServices.Unsafe.SizeOf<{container}>());");
        inTry.Add($"global::System.Runtime.CompilerServices.Unsafe.Copy(_arg{index}Heap, ref _arg{index}Container);");
        cleanup.Add($"global::Swift.Runtime.ExistentialContainerFactory.DestroyAndFreeExistential(_arg{index}Heap, 1, {ownsExpr}); global::System.GC.KeepAlive({keepAliveExpr});");
    }

    /// <summary>
    /// The C# half of a result the thunk hands back through out-pointers: the zeroed locals it
    /// writes into, their addresses (in <see cref="GetInvokeThunkResultOuts"/> order), and the value
    /// built from them. An existential is adopted at +1 by its proxy (or <c>AnyError</c>), exactly as
    /// a method's existential result is; a class-bound one occupies only the first two words, which
    /// <c>ReadHeapCell</c> widens into the container the proxy takes. A tuple is reassembled from its
    /// scalar elements.
    /// </summary>
    private static (string decls, IReadOnlyList<string> outArgs, string value) BuildInvokeThunkResultRead(
        TypeSpec returnType, ClosureHandler closureHandler)
    {
        if (closureHandler.IsInvokeThunkExistential(returnType))
        {
            var read = closureHandler.IsClassBoundArity1Existential(returnType)
                ? "global::Swift.Runtime.ClassExistentialContainer1.ReadHeapCell((nint)(&_ret))"
                : "_ret";
            string value;
            if (closureHandler.NeedsWellKnownProtocolWrapping(returnType, out var wellKnown))
            {
                value = $"new global::{wellKnown}({read}{ExistentialHandler.WellKnownOwnedTransferArg(wellKnown)})";
            }
            else
            {
                // The member-body checkpoint turns a suppressed proxy into a restubbed member before
                // any helper is emitted; reaching here without a proxy is a caller bug.
                closureHandler.ThrowIfProxyReferenceSuppressed(returnType);
                var proxy = closureHandler.GetQualifiedProxyClassName(returnType)
                    ?? throw new InvalidOperationException($"No proxy class for returned-closure result '{returnType}'.");
                value = $"new {proxy}({read}, ownsContainer: true)";
            }
            return ("global::Swift.Runtime.ExistentialContainer1 _ret = default;", ["(nint)(&_ret)"], value);
        }

        var tuple = (TupleTypeSpec)returnType;
        var decls = new List<string>();
        var outArgs = new List<string>();
        var parts = new List<string>();
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var element = tuple.Elements[i];
            var elementType = closureHandler.TranslateTypeSpecToCSharp(element);
            string scalar;
            if (MarshallingHelpers.IsBoolType(element))
            {
                scalar = "byte";
                parts.Add($"_r{i} != 0");
            }
            else if (closureHandler.IsSimpleEnum(element))
            {
                scalar = closureHandler.GetSimpleEnumInfo(element)?.csUnderlying ?? "int";
                parts.Add($"({elementType})_r{i}");
            }
            else
            {
                scalar = ClosureHandler.GetBlittablePrimitiveType(((NamedTypeSpec)element).Name)!;
                parts.Add($"({elementType})_r{i}");
            }
            decls.Add($"{scalar} _r{i} = default;");
            outArgs.Add($"(nint)(&_r{i})");
        }
        return (string.Join(" ", decls), outArgs, $"({string.Join(", ", parts)})");
    }

    /// <summary>
    /// Whether a returned closure can be invoked from C#. The invoke thunk is the only route: the
    /// member hands the closure back through typed memory, where Swift keeps it in its most general
    /// form, and only Swift code that loads it back at its own type calls it correctly.
    /// </summary>
    public static bool CanUseInvokeThunk(ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler)
        => GetInvokeThunkRefusal(closureTypeSpec, closureHandler) == null;

    /// <summary>
    /// Whether the Swift wrapper of a closure-returning member emits the invoke thunk the C#
    /// delegate calls. A result whose protocol proxy was suppressed turns the C# member into a
    /// throw stub that never calls the thunk, so the thunk is left out with it rather than shipped
    /// as an export nothing references.
    /// </summary>
    internal static bool ShouldEmitReturnedClosureInvokeThunk(
        ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler, ModuleEmissionContext? emissionContext)
        => closureHandler.IsSupportedClosure(closureTypeSpec, allowInOutArguments: false)
           && CanUseInvokeThunk(closureTypeSpec, closureHandler)
           && !closureHandler.IsProxyReferenceSuppressed(closureTypeSpec.ReturnType, emissionContext);

    /// <summary>
    /// Why the invoke thunk cannot carry a closure shape, or <c>null</c> when it can. The reason
    /// names the first argument or the result the thunk has no marshalling for, so a refused
    /// member's skip record says what to support next.
    /// </summary>
    public static string? GetInvokeThunkRefusal(ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler)
    {
        // An async closure has to be awaited on the Swift side and its result delivered back
        // through a completion, which the synchronous thunk does not do.
        if (closureTypeSpec.IsAsync)
            return "the closure is async; a returned async closure has no invoke thunk";

        int index = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg.IsInOut)
                return $"argument {index} is inout, which the invoke thunk cannot write back";
            if (!IsInvokeThunkCompatibleArg(arg, closureHandler))
                return $"argument {index} ({arg}) has no invoke-thunk marshalling";
            index++;
        }

        if (!closureTypeSpec.ReturnType.IsEmptyTuple
            && !IsInvokeThunkCompatibleReturn(closureTypeSpec.ReturnType, closureHandler))
            return $"the result ({closureTypeSpec.ReturnType}) has no invoke-thunk marshalling";

        return null;
    }

    /// <summary>
    /// Whether the thunk for this shape passes an argument or result by address — an existential
    /// argument, or a result that comes back through out-pointers. The protocol-conformance lane,
    /// which builds the same thunk for closures handed to a C# implementation, does not take these
    /// shapes yet.
    /// </summary>
    internal static bool UsesInvokeThunkAddressedValues(ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler)
        => closureTypeSpec.EachArgument().Any(closureHandler.IsInvokeThunkExistential)
           || GetInvokeThunkResultOuts(closureTypeSpec.ReturnType, closureHandler).Count > 0;

    /// <summary>
    /// Checks if a type is compatible as an invoke thunk argument.
    /// Primitives and simple enums pass directly. Complex enums and by-value
    /// structs pass via pointer (the @_cdecl thunk param is UnsafeMutableRawPointer
    /// and the body reloads the value via assumingMemoryBound(to:).pointee). A single-protocol
    /// existential or <c>any Error</c> passes as the address of a container.
    /// </summary>
    private static bool IsInvokeThunkCompatibleArg(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        // An `inout` argument is a cell Swift reads back after the call. The thunk lowers every
        // argument by value, and the tests below match on the underlying type alone — an Int32
        // still looks like a cdecl primitive, a struct still looks like a by-value struct — so
        // without this the mutation would be silently discarded. Refuse the shape here so the
        // whole thunk is declined rather than emitted with a dropped write-back.
        if (typeSpec.IsInOut)
            return false;
        if (closureHandler.IsInvokeThunkExistential(typeSpec))
            return true;
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
        // Classes and ObjC classes: C# passes the object pointer, Swift reloads it unretained.
        if (IsInvokeThunkReferenceArg(typeSpec, closureHandler))
            return true;
        return false;
    }

    /// <summary>
    /// Whether a closure argument is a class reference (a Swift class or an ObjC class) that the
    /// invoke thunk passes as its object pointer. The thunk is the only correct way to call a
    /// returned closure whose argument is a class: the wrapper hands the closure back through
    /// typed memory, where Swift stores it in its most general form — every argument passed by
    /// address and the result returned indirectly — so a direct swiftcc call that passes the
    /// object pointer itself makes Swift read the object's isa as the object. Only Swift code that
    /// loads the closure back at its own type sees the natural calling convention again.
    /// </summary>
    internal static bool IsInvokeThunkReferenceArg(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (closureHandler.IsClassType(typeSpec))
            return true;
        // ObjC-bridged value types (a typedef'd NSString, URL) are not class references here;
        // only a record that is itself a class crosses as an object pointer.
        return closureHandler.IsObjCBridgedClass(typeSpec)
            && typeSpec is NamedTypeSpec named
            && closureHandler.TypeDatabase.TryGetTypeRecord(named, out var record)
            && record.Kind == TypeRecordKind.Class;
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
        AppleSupplementReferences.Record("Foundation.Data", "ClosureEmitter.InvokeThunk:FoundationData");
        return ($"Swift.Foundation.Data.FromByteArray(_arg{argIndex})", "Swift.Foundation.Data");
    }

    /// <summary>
    /// Checks if a type is compatible as an invoke thunk return type.
    /// Primitives, simple enums, and class/ObjC types (returned as retained pointers) are supported,
    /// and — through out-pointers — a single-protocol existential, <c>any Error</c>, and a tuple of
    /// scalars.
    /// </summary>
    internal static bool IsInvokeThunkCompatibleReturn(TypeSpec returnType, ClosureHandler closureHandler)
    {
        if (closureHandler.IsInvokeThunkExistential(returnType) || IsInvokeThunkTupleReturn(returnType, closureHandler))
            return true;
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
