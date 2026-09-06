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
    /// <param name="useBoxedContext">When true, the context slot holds an <c>_SBClosureCtx</c> box
    /// pointer (the legacy <c>SwiftClosureData</c> escaping path with no Swift-side unbox), so the
    /// trampoline must resolve it via <c>GetDelegateFromBoxedContext</c>. Mirrors the gate in
    /// <see cref="EmitEscapingClosureCallback"/> so a boxed setter context is never read as a raw
    /// <see cref="System.Runtime.InteropServices.GCHandle"/> (which throws InvalidCastException).</param>
    public static void EmitIndirectReturnCallback(
        CSharpWriter csWriter,
        string methodName,
        string parameterName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string mangledName,
        bool useCdecl = false,
        bool useBoxedContext = false)
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
                // Direct lane: a loadable argument arrives exploded across registers, so declare the
                // words past the first as their own parameters.
                AppendDirectLaneExtraWordParameters(parameters, arg, argIndex, closureHandler, useCdecl);
            }
            argTypes.Add(arg);
            argIndex++;
        }
        parameters.Add(useCdecl ? "IntPtr contextPtr" : "SwiftSelf context");
        var parametersString = string.Join(", ", parameters);

        // Existential-argument proxy suppression: see EmitEscapingClosureCallback. An existential
        // ARGUMENT whose protocol proxy was suppressed (its EveryProtocol conformance was not emitted)
        // cannot be marshalled into the user delegate — there is no type to construct. This is the
        // indirect-return trampoline, which fills a Swift-allocated buffer the adapter unconditionally
        // .move()s; a silent empty body would leave that buffer uninitialized for Swift to move
        // (undefined behavior), and a throw across the [UnmanagedCallersOnly] boundary aborts the
        // process. Report through this callback's own established failure channel — FailFast (see the
        // catch below) — before Swift touches the buffer. Local check-and-branch only: the member-body
        // checkpoint rollback used by the closure-RETURN sites would desync the Swift writer / symbol
        // claims for a helper-emitted callback (Hazard D).
        if (argTypes.Any(closureHandler.IsProxyReferenceSuppressed))
        {
            var suppressedCallConv = useCdecl
                ? "typeof(global::System.Runtime.CompilerServices.CallConvCdecl)"
                : "typeof(global::System.Runtime.CompilerServices.CallConvSwift)";
            csWriter.WriteLines($$"""
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { {{suppressedCallConv}} })]
                private static unsafe void {{callbackName}}({{parametersString}})
                {
                    // Protocol proxy unavailable — a required existential argument's proxy class was
                    // suppressed (its EveryProtocol conformance was not emitted), so the Swift-vended
                    // existential cannot be marshalled into the user delegate and no result can be
                    // produced. The Swift adapter unconditionally .move()s the buffer this callback
                    // fills, so a silent empty body would leave it uninitialized; fail loudly through
                    // this callback's FailFast channel BEFORE Swift touches the buffer. The throw is
                    // caught by the shared UCO guard (CatchFreeUcoValidatorTests requires EVERY UCO body
                    // to be guarded), which converts the escape into a controlled FailFast.
                    try
                    {
                        throw new global::System.NotSupportedException(
                            "Protocol proxy unavailable: an existential argument's EveryProtocol conformance was not emitted.");
                    }
                    catch (global::System.Exception __ex)
                    {
                        SwiftClosureMarshaller.FailFastUnhandledClosureException(__ex);
                        throw;
                    }
                }
                """);
            return;
        }

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

        // No per-case delegate-type rewrite here: GetCSharpDelegateType is the SINGLE computation the
        // public signature and this cast both read, and it already spells a [String] closure return as
        // IReadOnlyList<string> (SwiftMarshal has no System.String element conversion, so the
        // SwiftArray<string> carrier is unusable as a delegate type — see the conversion block below,
        // which is what bridges the two). Re-deriving the type on one side makes the GCHandle store Action<A> and
        // this callback cast to Action<B>.
        var effectiveDelegateType = delegateType;

        // Box vs raw context: identical gate to EmitEscapingClosureCallback. On the non-cdecl legacy
        // SwiftClosureData escaping path the context slot carries an `_SBClosureCtx` box pointer with
        // no Swift-side unbox, so resolve via GetDelegateFromBoxedContext; otherwise the box pointer is
        // misread as a GCHandle and the cast throws (escaping this callback aborts the process).
        var extractCall = useBoxedContext
            ? $"SwiftClosureMarshaller.GetDelegateFromBoxedContext<{effectiveDelegateType}>({contextExtraction})"
            : $"SwiftClosureMarshaller.GetDelegateFromContext<{effectiveDelegateType}>({contextExtraction})";

        // Branch-specific marshalling of `result` into the Swift-allocated `indirectResult`
        // buffer. Each block runs inside the shared try below.
        string marshalBlock;
        if (isPlainString)
        {
            marshalBlock = """
                // Convert string → SwiftString (System.String has no Swift metadata)
                using var _swiftStr = new Swift.SwiftString(result);
                var metadata = Swift.Runtime.SwiftObjectHelper<Swift.SwiftString>.GetTypeMetadata();
                var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                ((Swift.Runtime.ISwiftObject)_swiftStr).MarshalToSwift(ref resultSpan);
                """;
        }
        else if (isOptionalString)
        {
            marshalBlock = """
                // Convert string? → SwiftOptional<SwiftString> (System.String has no Swift metadata)
                using var _swiftStr = result != null ? new Swift.SwiftString(result) : null;
                using var _swiftOpt = _swiftStr != null
                    ? SwiftOptional<Swift.SwiftString>.NewSome(_swiftStr)
                    : SwiftOptional<Swift.SwiftString>.NewNone();
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<Swift.SwiftString>>();
                var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                SwiftMarshal.MarshalToSwift(_swiftOpt, ref resultSpan);
                """;
        }
        else if (isArrayString)
        {
            marshalBlock = """
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
                """;
        }
        else if (isObjCBridged)
        {
            marshalBlock = """
                // ObjC-bridged type: write the handle pointer to the result buffer.
                // The Swift struct wraps an ObjC reference — the handle IS the ABI representation.
                *(IntPtr*)indirectResult = result.Handle;
                """;
        }
        else if (isClassReturn)
        {
            marshalBlock = """
                // Class type: retain the pointer before writing to the result buffer.
                // Swift's wrapper will .move() this value and eventually passRetained it —
                // the expression release consumes the original +1, so the buffer must carry
                // its own +1 to prevent over-release when both the C# wrapper and the
                // Swift-returned wrapper are finalized.
                var __ptr = result.Payload.DangerousGetHandle();
                global::Swift.Runtime.Arc.Retain(__ptr);
                *(IntPtr*)indirectResult = __ptr;
                """;
        }
        else
        {
            marshalBlock = $$"""
                // Marshal the result to the indirect result buffer
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<{{returnCSharpType}}>();
                var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                SwiftMarshal.MarshalToSwift(result, ref resultSpan);
                """;
        }

        // Non-throwing indirect-return closure: there is no error channel back to Swift, and
        // the Swift adapter unconditionally .move()s the buffer this callback fills. If `del`
        // (or the marshalling) throws, the buffer is never written and Swift would .move()
        // uninitialized storage. A managed exception escaping into native Swift also aborts
        // the process. Wrap the body so any unhandled exception becomes a controlled FailFast
        // BEFORE Swift touches the buffer.
        var argPrologue = BuildDirectLaneWordBufferPrologue(closureTypeSpec, closureHandler, useCdecl);
        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { {{callConvType}} })]
            private static unsafe void {{callbackName}}({{parametersString}})
            {
                try
                {
                    {{argPrologue}}var del = {{extractCall}};
                    var result = del({{invokeArgsString}});

                    {{marshalBlock}}
                }
                catch (global::System.Exception __ex)
                {
                    SwiftClosureMarshaller.FailFastUnhandledClosureException(__ex);
                    throw;
                }
            }
            """);
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
        // One oracle with the delegate-type computation: ClosureHandler projects exactly this shape
        // to IReadOnlyList<string>, and this marshal block is the conversion that pairs with it.
        => ClosureHandler.IsStringArray(returnType);

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
            : BuildIndirectReturnSwiftFunctionPointerType(closureTypeSpec, closureHandler);

        // Add context parameter to the function pointer type
        var funcPtrTypeWithContext = useCdecl
            ? AddCdeclContextToFunctionPointerType(funcPtrType)
            : AddContextToFunctionPointerType(funcPtrType);

        csWriter.WriteLine($"private static unsafe readonly {funcPtrTypeWithContext} s_{callbackName} = &{callbackName};");
    }

    /// <summary>
    /// Builds the direct-lane (<c>CallConvSwift</c>) function pointer type for an indirect-return
    /// callback. Mirrors <see cref="EmitIndirectReturnCallback"/>'s parameter expansion, including the
    /// extra registers a loadable argument arrives in — the reverse trampoline and the pointer type
    /// that names it have to agree on arity. This deliberately does NOT reuse
    /// <c>ClosureHandler.GetPInvokeFunctionPointerTypeWithIndirectReturn</c>: that one types the
    /// FORWARD direction (C# invoking a closure Swift handed back), where the argument words are
    /// supplied by the caller rather than declared as parameters.
    /// </summary>
    private static string BuildIndirectReturnSwiftFunctionPointerType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var types = new List<string> { "void*" }; // indirect result buffer
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            types.Add(GetCallbackParameterType(arg, closureHandler));
            AppendDirectLaneExtraWordTypes(types, arg, closureHandler, useCdecl: false);
        }

        types.Add("void");
        return $"delegate* unmanaged[Swift]<{string.Join(", ", types)}>";
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
