// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <param name="applyVtableMembershipFilter">When true, gate every member through
    /// <see cref="ProtocolVtableMembers"/> so receivers are emitted ONLY for members
    /// that have a corresponding Swift vtable slot. Used for cross-module parent
    /// scaffolding where the per-protocol skip sets (<c>_skippedPropertyNames</c> et al.)
    /// are not populated for the parent — without this filter, the receiver loop would
    /// emit, for example, a property-setter receiver for a non-dispatchable closure
    /// property that has no Swift vtable slot to feed it AND a C# interface type that
    /// doesn't accept the receiver's raw function-pointer value (CS0029).</param>
    private void EmitReceiverMethods(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName, bool applyVtableMembershipFilter = false)
    {
        writer.WriteLine("#region Swift Callback Receivers");
        writer.WriteLine();

        // Track emitted receivers to avoid duplicates
        var emittedReceivers = new HashSet<string>();
        var closureHandler = applyVtableMembershipFilter ? new ClosureHandler(_typeDatabase) : null;

        // Property receivers (skip static properties - they're not part of the interface)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // Skip receivers for properties that the interface skipped due to AnyType generic args
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            if (applyVtableMembershipFilter && !ProtocolVtableMembers.IncludesProperty(property, protocolDecl, closureHandler!))
                continue;
            EmitPropertyReceivers(writer, property, protocolDecl, interfaceName, emittedReceivers);
        }

        // Subscript receivers (skip static subscripts - they're not part of the interface)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            // Skip receivers for subscripts that the interface skipped due to AnyType generic args
            if (_skippedSubscriptIndices.Contains(subscriptIndex))
            {
                subscriptIndex++;
                continue;
            }
            if (applyVtableMembershipFilter && !ProtocolVtableMembers.IncludesSubscript(subscript, protocolDecl))
            {
                subscriptIndex++;
                continue;
            }
            EmitSubscriptReceivers(writer, subscript, protocolDecl, interfaceName, subscriptIndex, emittedReceivers);
            subscriptIndex++;
        }

        // Method receivers
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (_skippedMethodKeys.Contains(methodKey))
                {
                    // Closure-skipped methods are omitted from BOTH the Swift vtable struct
                    // (EveryProtocolEmitter emits a fatalError stub that bypasses the vtable)
                    // AND the C# Swift-facing vtable struct (see ProtocolProxyEmitter.Vtables.cs).
                    // Emitting a Receive_ trampoline here would have no slot to be assigned into,
                    // so skip the receiver entirely.
                    continue;
                }
                if (applyVtableMembershipFilter && !ProtocolVtableMembers.IncludesMethod(method, protocolDecl, closureHandler!))
                    continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                // Only emit receiver for new methods
                EmitMethodReceiver(writer, method, protocolDecl, interfaceName, idx, emittedReceivers);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitPropertyReceivers(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string interfaceName, HashSet<string> emittedReceivers)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Dispatchable closure properties take a dedicated receiver shape — setter
        // accepts (fnPtr, ctxPtr) IntPtr pair, getter returns a 16-byte buffer
        // containing (fnPtr, ctxPtr). Skip the value-shaped emission below.
        var closureHandlerForProp = new ClosureHandler(_typeDatabase);
        if (EveryProtocolEmitter.IsDispatchableClosureProperty(property, closureHandlerForProp))
        {
            EmitDispatchableClosurePropertyReceivers(writer, property, protocolDecl, proxyClassName, interfaceName, closureHandlerForProp, emittedReceivers);
            return;
        }

        // P0: Use ABI type for MarshalFromSwift (setter reads Swift memory layout),
        // not the idiomatic type used for signatures.
        var abiTypeName = GetCSharpTypeName(property.SwiftTypeSpec, forAbiMarshalling: true);

        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);

        // Sibling-property fallback: when this property is part of a sibling group (two or
        // more class-bound protocols declaring the same property name+type with differing
        // accessor sets), the Swift fan-out picks ANY populated sibling vtable, not the one
        // matching the proxy actually registered for this EveryProtocol instance. Without
        // a per-instance fallback in the receiver, a smaller-sibling proxy whose vtable was
        // skipped by the fan-out would silently return "" / no-op. We emit additional
        // lookups across the sibling interfaces (filtered by setter-presence for the setter
        // receiver) so whichever vtable Swift picks correctly locates the proxy.
        // See EveryProtocolEmitter.ComputeSiblingPropertyFallbacks and the receiver-fallback
        // helper EmitGetterFallbackLookups / EmitSetterFallbackLookups below.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var siblingFallbacks = _emissionContext.GetSiblingPropertyFallbacks(protoQName, property.Name);

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // The interface property uses idiomatic C# types (e.g., string, string?, IReadOnlyList<string>)
                // but MarshalToSwiftBuffer expects Swift ABI types (SwiftString, SwiftOptional<SwiftString>, etc.).
                // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                var getterConversion = GetReceiverGetterConversion("result", property.SwiftTypeSpec);
                // F1: If property is narrowed (int/uint), widen back to nint/nuint for Swift ABI MarshalToSwiftBuffer.
                // Plain nint: result is int → (nint)result ensures 8-byte write.
                // Plain nuint: result is uint → (nuint)result ensures 8-byte write.
                // Optional<nint/nuint>: getterConversion builds SwiftOptional<nint>.NewSome(resultVal) where
                //   resultVal is int/uint (unwrapped from int?/uint?) — implicit widening handles it.
                if (getterConversion == null && NativeIntOverloadEmitter.TryGetAbiWideningType(property.SwiftTypeSpec, out var abiType))
                    getterConversion = $"({abiType})result";

                // String returns use Utf8Slice encoding to avoid ARC issues with MarshalToSwiftBuffer<SwiftString>.
                // SwiftString contains ARC-managed references that Unsafe.Write can't retain properly,
                // causing crashes when Swift reads the result. Utf8Slice passes raw bytes safely.
                bool isStringReturn = IsStringTypeSpec(property.SwiftTypeSpec);

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                writer.WriteLine("{");
                writer.Indent++;
                // Read only the first existential word: this is the proxy handle (Payload0),
                // the sole field TryGetProxy actually uses. Avoids the 5-word over-read of
                // *(ExistentialContainer1*)selfContainer, which over-reads stack memory when
                // Swift passes a class-bound (2-word) existential for EveryObjCProtocol.
                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                // Dead-impl safe: use TryGetProxy so an Unregister'd handle
                // (e.g. after user Dispose) is a silent no-op instead of throwing
                // InvalidOperationException across the [UnmanagedCallersOnly] boundary.
                // The impl weak-reference unwrap may ALSO return null if the user dropped
                // the impl while Swift still holds a strong retain on the proxy (Codex P0);
                // in that case we return a zero-filled buffer rather than NRE-crash.
                //
                // Codex P1 #1: the buffer size MUST match the carrier the success path uses
                // for MarshalToSwiftBuffer<T>(...). When a getter conversion is present the
                // carrier is e.g. SwiftOptional<bool>, NOT bool? — using the idiomatic type
                // here would hand Swift a too-small buffer and corrupt the receiver boundary.
                // Use the projection-derived carrier when available, fall back to the public
                // (idiomatic) interface property type for the no-conversion branch.
                var publicPropertyTypeName = GetCSharpTypeName(property.SwiftTypeSpec);
                var carrierTypeName = getterConversion != null
                    ? (GetReceiverGetterCarrierType(property.SwiftTypeSpec) ?? publicPropertyTypeName)
                    : publicPropertyTypeName;
                // For reference-type wrapper carriers (SwiftOptional<U>, SwiftArray<U>, ...)
                // Unsafe.SizeOf<T> is only a pointer, so a zero-filled buffer of that size would
                // be smaller than the native Swift value and Swift would read past it. Size the
                // fallback buffer from the type metadata for those carriers (value types keep
                // the managed size). Mirrors the success-path MarshalToSwiftBuffer<T>.
                var nullReturnStr = isStringReturn
                    ? "MarshalStringToUtf8Slice(string.Empty)"
                    : BuildReceiverNullFallbackExpr(carrierTypeName);
                if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                {
                    writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
                    writer.Indent++;
                    writer.WriteLine($"return {nullReturnStr};");
                    writer.Indent--;
                    writer.WriteLine("var impl = proxy.UserImpl;");
                    writer.WriteLine("if (impl is null)");
                    writer.Indent++;
                    writer.WriteLine($"return {nullReturnStr};");
                    writer.Indent--;
                    writer.WriteLine($"var result = impl.{pascalPropertyName};");
                    if (isStringReturn)
                    {
                        writer.WriteLine("return MarshalStringToUtf8Slice(result);");
                    }
                    else if (getterConversion != null)
                    {
                        writer.WriteLine($"var swiftResult = {getterConversion};");
                        writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                    }
                    else
                    {
                        writer.WriteLine("return MarshalToSwiftBuffer(result);");
                    }
                }
                else
                {
                    EmitGetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, getterConversion, isStringReturn);
                    int siblingIdx = 0;
                    foreach (var sibling in siblingFallbacks)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitGetterLookupHit(writer, siblingIface, $"s{siblingIdx}", pascalPropertyName, getterConversion, isStringReturn);
                        siblingIdx++;
                    }
                    writer.WriteLine($"return {nullReturnStr};");
                }
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Issue #40 / P1-01: a Swift-class (or Optional<class>) value arrives as the address of a
                // borrowed slot holding the heap pointer (&valueCopy). The runtime copy-out helper returns
                // the wrapper (or null) directly, so the marshalled value IS the assignment value — no
                // idiomatic cast (which would re-wrap and, for the optional, false-trip on Unsafe.Read).
                var classCopyOut = GetReceiverClassCopyOutExpr("valuePtr", property.SwiftTypeSpec);

                // Check if the property type needs conversion (e.g., SwiftOptional<SwiftString> → string?)
                // The receiver marshals the Swift ABI type, but the interface uses the idiomatic C# type.
                // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                var returnConversion = classCopyOut != null ? null : GetReceiverSetterConversion("value", property.SwiftTypeSpec);
                var assignmentExpr = returnConversion ?? "value";
                // F1: Narrow nint/nuint ABI value to int/uint for property assignment.
                // Plain nint: value is nint (MarshalFromSwift<nint>) → (int)value.
                // Optional<nint>: returnConversion is "((nint?)value)" → (int?)((nint?)value).
                if (classCopyOut == null && NativeIntOverloadEmitter.TryGetNarrowedType(property.SwiftTypeSpec, out var narrowedType))
                    assignmentExpr = $"({narrowedType}){assignmentExpr}";

                // String property: local MarshalFromSwift<SwiftString> uses Unsafe.Read which
                // can't construct a managed SwiftString from raw Swift memory. Use runtime marshaller.
                var marshalExpr = classCopyOut
                    ?? (IsStringTypeSpec(property.SwiftTypeSpec)
                        ? $"global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr)"
                        : $"MarshalFromSwift<{abiTypeName}>(valuePtr)");

                var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();
                if (setterSiblings == null || setterSiblings.Count == 0)
                {
                    writer.WriteLines($$"""
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static void {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)
                        {
                            var handle = *(IntPtr*)selfContainer;
                            // Dead-impl safe: silently drop the write if the proxy is unregistered
                            // or the managed impl has already been GC'd. A throw here would propagate
                            // across the [UnmanagedCallersOnly] boundary and terminate the process.
                            if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{{interfaceName}}>>(handle, out var proxy) || proxy is null)
                                return;
                            var impl = proxy.UserImpl;
                            if (impl is null)
                                return;
                            var value = {{marshalExpr}};
                            impl.{{pascalPropertyName}} = {{assignmentExpr}};
                        }

                        """);
                }
                else
                {
                    writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)");
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                    writer.WriteLine($"var value = {marshalExpr};");
                    EmitSetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, assignmentExpr);
                    int siblingIdx = 0;
                    foreach (var sibling in setterSiblings)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitSetterLookupHit(writer, siblingIface, $"s{siblingIdx}", pascalPropertyName, assignmentExpr);
                        siblingIdx++;
                    }
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine();
                }
            }
        }
    }

    /// <summary>
    /// Emits the Swift-callback receivers for a dispatchable closure property.
    /// Setter receiver wraps (fnPtr, ctxPtr) into a managed
    /// <c>Action</c> via <see cref="SwiftEscapingClosure{TDelegate}"/> and assigns
    /// it onto the impl. Getter receiver decomposes the user-supplied delegate
    /// stored on the impl back into a (fnPtr, ctxPtr) pair Swift can re-wrap into
    /// a Swift closure value. Currently restricted to <c>() -&gt; Void</c> shape
    /// (enforced by <see cref="EveryProtocolEmitter.IsDispatchableClosureProperty"/>).
    /// </summary>
    private void EmitDispatchableClosurePropertyReceivers(CSharpWriter writer, PropertyDecl property,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, ClosureHandler closureHandler,
        HashSet<string> emittedReceivers)
    {
        if (!EveryProtocolEmitter.TryGetDispatchableClosureParam(property.SwiftTypeSpec, closureHandler, out var closure, out var isOptional) || closure is null)
            throw new InvalidOperationException($"EmitDispatchableClosurePropertyReceivers called on non-dispatchable property '{property.Name}'.");

        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);
        var delegateType = closureHandler.GetCSharpDelegateType(closure);
        var nullableDelegateType = isOptional ? $"{delegateType}?" : delegateType;

        // Sibling fallback applies the same shape as value-typed properties — see
        // EmitPropertyReceivers above. Closure properties have a different value
        // shape (16-byte (fnPtr, ctxPtr) buffer / set takes the pair) but the
        // proxy-lookup-fan-out logic is identical: any populated sibling vtable
        // can route to this receiver, so we must try each sibling's
        // IProtocolProxyImpl<T> after the primary.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var siblingFallbacks = _emissionContext.GetSiblingPropertyFallbacks(protoQName, property.Name);
        var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();

        // Per-(protocol, property) C# cdecl thunk + invoker class — fired from Swift when
        // the materialised getter closure is called. ctx is the GCHandle.ToIntPtr of the
        // user-supplied delegate stored on impl.<PascalProp>; the box wrapping it on the
        // Swift side guarantees release via the SwiftClosureContext destroy callback.
        var getterThunkName = $"_PropClosureThunk_{property.Name}";

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                var entryPoint = EveryProtocolEmitter.GetProtocolClosurePropertyInvokeThunkEntryPoint(protocolDecl, property);
                var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
                var invokerClassName = ClosureEmitter.GetInvokerClassName(helperName);

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawFn, IntPtr rawCtx)");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                if (setterSiblings == null || setterSiblings.Count == 0)
                {
                    writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
                    writer.Indent++;
                    writer.WriteLine("return;");
                    writer.Indent--;
                    writer.WriteLine("var impl = proxy.UserImpl;");
                    writer.WriteLine("if (impl is null)");
                    writer.Indent++;
                    writer.WriteLine("return;");
                    writer.Indent--;
                    EmitClosureSetterBody(writer, isOptional, pascalPropertyName, delegateType, invokerClassName, implVar: "impl");
                }
                else
                {
                    EmitClosureSetterLookupHit(writer, interfaceName, "primary", isOptional, pascalPropertyName, delegateType, invokerClassName);
                    int idx = 0;
                    foreach (var sibling in setterSiblings)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitClosureSetterLookupHit(writer, siblingIface, $"s{idx}", isOptional, pascalPropertyName, delegateType, invokerClassName);
                        idx++;
                    }
                }
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                // 16-byte buffer carrying (fnPtr, ctxPtr). Allocate up-front so every exit
                // path returns the same shape.
                writer.WriteLine("var buf = (IntPtr)NativeMemory.AllocZeroed(16);");
                if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                {
                    writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
                    writer.Indent++;
                    writer.WriteLine("return buf;");
                    writer.Indent--;
                    writer.WriteLine("var impl = proxy.UserImpl;");
                    writer.WriteLine("if (impl is null)");
                    writer.Indent++;
                    writer.WriteLine("return buf;");
                    writer.Indent--;
                    EmitClosureGetterBody(writer, pascalPropertyName, nullableDelegateType, getterThunkName, implVar: "impl");
                    writer.WriteLine("return buf;");
                }
                else
                {
                    EmitClosureGetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, nullableDelegateType, getterThunkName);
                    int idx = 0;
                    foreach (var sibling in siblingFallbacks)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitClosureGetterLookupHit(writer, siblingIface, $"s{idx}", pascalPropertyName, nullableDelegateType, getterThunkName);
                        idx++;
                    }
                    writer.WriteLine("return buf;");
                }
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        // Stable C# cdecl thunk used by the materialised Swift closure on the getter side.
        // ctx is the GCHandle.ToIntPtr of impl.<PascalProp>. Restricted to () -> Void by
        // EveryProtocolEmitter.IsDispatchableClosureProperty.
        if (hasGetter && emittedReceivers.Add(getterThunkName))
        {
            writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            writer.WriteLine($"private static void {getterThunkName}(IntPtr ctx)");
            writer.WriteLine("{");
            writer.Indent++;
            writer.WriteLine("if (ctx == IntPtr.Zero) return;");
            writer.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
            writer.WriteLine("if (!handle.IsAllocated) return;");
            writer.WriteLine($"if (handle.Target is {delegateType} _del)");
            writer.Indent++;
            writer.WriteLine("_del();");
            writer.Indent--;
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Emits the Swift-callback receiver for a closure-returning protocol method.
    /// Receiver takes only <c>(vtHandle, selfContainer)</c>, calls the
    /// C# impl's parameterless method to obtain a managed delegate, and returns a
    /// 16-byte buffer carrying (fnPtr, GCHandle) so Swift can wrap the pair into a
    /// real <c>() -&gt; Void</c> closure. Currently restricted to <c>() -&gt; Void</c>
    /// returns (enforced by <see cref="EveryProtocolEmitter.IsDispatchableClosureReturningMethod"/>).
    /// </summary>
    private void EmitDispatchableClosureReturningMethodReceiver(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, int index, ClosureHandler closureHandler)
    {
        if (method.CSSignature.FirstOrDefault()?.SwiftTypeSpec is not ClosureTypeSpec retClosure)
            throw new InvalidOperationException(
                $"EmitDispatchableClosureReturningMethodReceiver called on method '{method.Name}' without a closure return type.");

        var receiverName = $"Receive_{method.Name}_{index}";
        var pascalMethodName = NameProvider.GetMethodName(method.Name, propertyNames: null);
        var delegateType = closureHandler.GetCSharpDelegateType(retClosure);
        var returnedThunkName = $"_MethodClosureThunk_{method.Name}_{index}";

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        // 16-byte buffer carrying (fnPtr, ctxPtr). Allocate up-front so every exit
        // path returns the same shape (mirrors Shape 3's getter).
        writer.WriteLine("var buf = (IntPtr)NativeMemory.AllocZeroed(16);");
        writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
        writer.Indent++;
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("var impl = proxy.UserImpl;");
        writer.WriteLine("if (impl is null)");
        writer.Indent++;
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine($"{delegateType}? _del = impl.{pascalMethodName}();");
        writer.WriteLine("if (_del is null)");
        writer.Indent++;
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.Alloc(_del);");
        writer.WriteLine($"*(IntPtr*)buf = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&{returnedThunkName};");
        writer.WriteLine("*(IntPtr*)(buf + IntPtr.Size) = global::System.Runtime.InteropServices.GCHandle.ToIntPtr(_gch);");
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Stable C# cdecl thunk fired by the materialised Swift closure on the Swift
        // side. ctx is the GCHandle.ToIntPtr of the Action returned from impl.<Method>().
        // Restricted to () -> Void by IsDispatchableClosureReturningMethod.
        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static void {returnedThunkName}(IntPtr ctx)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("if (ctx == IntPtr.Zero) return;");
        writer.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
        writer.WriteLine("if (!handle.IsAllocated) return;");
        writer.WriteLine($"if (handle.Target is {delegateType} _del)");
        writer.Indent++;
        writer.WriteLine("_del();");
        writer.Indent--;
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the Swift-callback receiver for a protocol method whose single parameter
    /// is an <c>@escaping () async -&gt; Int32</c> closure. Receiver takes
    /// <c>(vtHandle, selfContainer, rawArg0_fn, rawArg0_ctx)</c>, wraps the (fnPtr, ctx) pair
    /// into a managed <c>Func&lt;Task&lt;int&gt;&gt;</c> via the per-(protocol, method) async invoker
    /// class, and invokes the C# impl with that delegate. The invoker bridges
    /// <c>InvokeAsync()</c> → Swift @_cdecl thunk → completion callback → TCS.SetResult.
    /// Currently restricted to <c>() async -&gt; Int32</c> (enforced by
    /// <see cref="EveryProtocolEmitter.IsDispatchableAsyncClosureMethod"/>).
    /// </summary>
    private void EmitDispatchableAsyncClosureMethodReceiver(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, int index, ClosureHandler closureHandler)
    {
        // Locate the single async-closure param (gate guarantees exactly one).
        ArgumentDecl? asyncParam = null;
        ClosureTypeSpec? asyncClosure = null;
        int argIdx = -1;
        foreach (var (param, idx, closure) in EveryProtocolEmitter.EnumerateDispatchableAsyncClosureParams(method, closureHandler))
        {
            asyncParam = param;
            asyncClosure = closure;
            argIdx = idx;
            break;
        }
        if (asyncParam is null || asyncClosure is null || argIdx < 0)
            throw new InvalidOperationException(
                $"EmitDispatchableAsyncClosureMethodReceiver called on method '{method.Name}' without an async closure parameter.");

        var receiverName = $"Receive_{method.Name}_{index}";
        var delegateType = closureHandler.GetCSharpDelegateType(asyncClosure);
        var entryPoint = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkEntryPoint(protocolDecl, method, index, argIdx);
        var helperName = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkHelperName(entryPoint);
        var invokerClassName = GetAsyncClosureInvokerClassName(helperName);

        // Mirror the property-collision rename used by the non-async receiver so the
        // impl method name lines up with what the interface emits.
        var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> receiverPropertyNames = canonicalPropertyNames != null
            ? new HashSet<string>(canonicalPropertyNames)
            : new HashSet<string>();
        var pascalMethodName = NameProvider.GetPublicMethodName(
            method.Name, method.IsAsync, hasReturnValue: false,
            propertyNames: receiverPropertyNames,
            isSelfReturning: false,
            parameterCount: 1);

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0_fn, IntPtr rawArg0_ctx)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
        writer.Indent++;
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("var impl = proxy.UserImpl;");
        writer.WriteLine("if (impl is null)");
        writer.Indent++;
        writer.WriteLine("return;");
        writer.Indent--;
        // Wrap the (fnPtr, ctx) pair in SwiftEscapingClosure so Arc.Retain keeps the Swift
        // context alive across the async boundary; the invoker class holds the wrapper
        // reference for the duration of the impl call. Use a method-group bound to the
        // invoker (Mono-JIT-safe — no display class in the call chain).
        writer.WriteLine($"var _wrapper = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg0_fn, rawArg0_ctx);");
        writer.WriteLine($"var _inv = new {invokerClassName}((nint)_wrapper.FunctionPointer, (nint)_wrapper.Context, _wrapper);");
        writer.WriteLine($"{delegateType} handler = _inv.InvokeAsync;");
        writer.WriteLine($"impl.{pascalMethodName}(handler);");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Derives the async closure invoker class name from the per-method helper name.
    /// Mirrors <see cref="ClosureEmitter.GetInvokerClassName"/> for the async path.
    /// </summary>
    internal static string GetAsyncClosureInvokerClassName(string helperMethodName)
    {
        return $"_AsyncClosureInv_{helperMethodName.Replace("_InvokeAsyncClosureThunk_", "")}";
    }

    private void EmitSubscriptReceivers(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        // P0: Use ABI type for MarshalFromSwift (reads Swift memory layout)
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec, forAbiMarshalling: true);
        var paramCount = subscript.IndexParameters.Count;
        var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));

        // Sibling-subscript fallback: same shape as the property sibling pipeline. When
        // this subscript participates in a sibling group, the Swift fan-out can pick any
        // populated sibling vtable, so each receiver tries its own interface first and then
        // falls back to the recorded sibling interfaces. See
        // EveryProtocolEmitter.ComputeSiblingSubscriptFallbacks.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var subscriptKey = $"subscript_{index}(" +
            string.Join(",", subscript.IndexParameters.Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
        var siblingFallbacks = _emissionContext.GetSiblingSubscriptFallbacks(protoQName, subscriptKey);

        if (subscript.HasGetter)
        {
            var receiverName = $"Receive_subscript_{index}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                // Dead-impl safe: return zeroed buffer rather than throwing on missing
                // proxy / GC'd impl (Codex P0 + P1 #3 — [UnmanagedCallersOnly] cannot throw).
                // Codex P1 #1: size the fallback by the carrier the success path uses for
                // MarshalToSwiftBuffer<T>(...), not by the idiomatic interface type.
                var subscriptIsString = IsStringTypeSpec(subscript.ReturnTypeSpec);
                var subscriptGetterConv = GetReceiverExistentialGetterConversion("result", subscript.ReturnTypeSpec)
                    ?? GetReceiverGetterConversion("result", subscript.ReturnTypeSpec);
                var subscriptPublicReturnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec);
                var subscriptCarrierTypeName = subscriptGetterConv != null
                    ? (GetReceiverGetterCarrierType(subscript.ReturnTypeSpec) ?? subscriptPublicReturnTypeName)
                    : subscriptPublicReturnTypeName;
                // Reference-type wrapper carriers need a metadata-sized fallback buffer (see the
                // property getter null-return note above); AllocZeroedSwiftBuffer<T> matches the
                // success-path MarshalToSwiftBuffer<T> size for both value and reference carriers.
                var subscriptNullReturnStr = subscriptIsString
                    ? "MarshalStringToUtf8Slice(string.Empty)"
                    : BuildReceiverNullFallbackExpr(subscriptCarrierTypeName);

                // Unmarshal index parameters once — same indexes used for every sibling lookup.
                // P0: use ABI types for MarshalFromSwift.
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                    if (IsStringTypeSpec(param.SwiftTypeSpec))
                        writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                    // Issue #40 / P1-01: a Swift-class index arrives as the address of a borrowed slot;
                    // copy it out (deref + ObjC-aware retain) instead of Unsafe.Read-ing the heap pointer.
                    else if (GetReceiverClassCopyOutExpr($"arg{i}", param.SwiftTypeSpec) is string indexClassCopyOut)
                        writer.WriteLine($"var index{i} = {indexClassCopyOut};");
                    else
                        writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                {
                    writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
                    writer.Indent++;
                    writer.WriteLine($"return {subscriptNullReturnStr};");
                    writer.Indent--;
                    writer.WriteLine("var impl = proxy.UserImpl;");
                    writer.WriteLine("if (impl is null)");
                    writer.Indent++;
                    writer.WriteLine($"return {subscriptNullReturnStr};");
                    writer.Indent--;
                    writer.WriteLine($"var result = impl[{indexArgs}];");
                    // String returns use Utf8Slice encoding to avoid ARC issues with
                    // MarshalToSwiftBuffer<SwiftString> — same rationale as the property
                    // getter path: SwiftString contains ARC-managed references that
                    // Unsafe.Write can't retain properly, crashing the receiver when
                    // Swift reads the result.
                    if (subscriptIsString)
                    {
                        writer.WriteLine("return MarshalStringToUtf8Slice(result);");
                    }
                    else if (subscriptGetterConv != null)
                    {
                        writer.WriteLine($"var swiftResult = {subscriptGetterConv};");
                        writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                    }
                    else
                    {
                        writer.WriteLine("return MarshalToSwiftBuffer(result);");
                    }
                }
                else
                {
                    EmitSubscriptGetterLookupHit(writer, interfaceName, "primary", indexArgs, subscript.ReturnTypeSpec, subscriptGetterConv, subscriptIsString);
                    int siblingIdx = 0;
                    foreach (var sibling in siblingFallbacks)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitSubscriptGetterLookupHit(writer, siblingIface, $"s{siblingIdx}", indexArgs, subscript.ReturnTypeSpec, subscriptGetterConv, subscriptIsString);
                        siblingIdx++;
                    }
                    writer.WriteLine($"return {subscriptNullReturnStr};");
                }

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (subscript.HasSetter)
        {
            var receiverName = $"Receive_subscript_{index}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list (includes value after self)
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");

                // Unmarshal value once — same value used for every sibling lookup.
                if (IsStringTypeSpec(subscript.ReturnTypeSpec))
                {
                    writer.WriteLine($"var value = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr).ToString();");
                }
                // Issue #40 / P1-01: a Swift-class (or Optional<class>) value arrives as the address of a
                // borrowed slot; copy it out instead of Unsafe.Read-ing the heap pointer as a managed ref.
                else if (GetReceiverClassCopyOutExpr("valuePtr", subscript.ReturnTypeSpec) is string valueClassCopyOut)
                {
                    writer.WriteLine($"var value = {valueClassCopyOut};");
                }
                else
                {
                    var subscriptSetterConv = GetReceiverExistentialSetterConversion("rawValue", subscript.ReturnTypeSpec);
                    if (subscriptSetterConv != null)
                    {
                        writer.WriteLine($"var rawValue = MarshalFromSwift<{returnTypeName}>(valuePtr);");
                        writer.WriteLine($"var value = {subscriptSetterConv};");
                    }
                    else
                    {
                        writer.WriteLine($"var value = MarshalFromSwift<{returnTypeName}>(valuePtr);");
                    }
                }

                // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                    if (IsStringTypeSpec(param.SwiftTypeSpec))
                        writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                    // Issue #40 / P1-01: a Swift-class index arrives as the address of a borrowed slot;
                    // copy it out (deref + ObjC-aware retain) instead of Unsafe.Read-ing the heap pointer.
                    else if (GetReceiverClassCopyOutExpr($"arg{i}", param.SwiftTypeSpec) is string setterIndexClassCopyOut)
                        writer.WriteLine($"var index{i} = {setterIndexClassCopyOut};");
                    else
                        writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();
                if (setterSiblings == null || setterSiblings.Count == 0)
                {
                    // Dead-impl safe: silently drop the write if the proxy is unregistered
                    // or the impl has been GC'd.
                    writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
                    writer.Indent++;
                    writer.WriteLine("return;");
                    writer.Indent--;
                    writer.WriteLine("var impl = proxy.UserImpl;");
                    writer.WriteLine("if (impl is null)");
                    writer.Indent++;
                    writer.WriteLine("return;");
                    writer.Indent--;
                    writer.WriteLine($"impl[{indexArgs}] = value;");
                }
                else
                {
                    EmitSubscriptSetterLookupHit(writer, interfaceName, "primary", indexArgs);
                    int siblingIdx = 0;
                    foreach (var sibling in setterSiblings)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitSubscriptSetterLookupHit(writer, siblingIface, $"s{siblingIdx}", indexArgs);
                        siblingIdx++;
                    }
                }

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-subscript getter receiver.
    /// On lookup hit, materialises the subscript value, applies any conversion, and returns
    /// via MarshalToSwiftBuffer. On miss, falls through to the next sibling. Indexes are
    /// unmarshalled once by the caller and threaded in via <paramref name="indexArgs"/>.
    /// Uses the bare names <c>result</c>/<c>swiftResult</c>/<c>impl</c>/<c>proxy</c>
    /// because each lookup block is its own C# scope — same pattern as the property
    /// counterpart <see cref="EmitGetterLookupHit"/>.
    /// </summary>
    private void EmitSubscriptGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string indexArgs, TypeSpec returnTypeSpec, string? getterConversion, bool isStringReturn)
    {
        var proxyVar = $"proxy_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var impl = {proxyVar}.UserImpl;");
        writer.WriteLine("if (impl is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var result = impl[{indexArgs}];");
        if (isStringReturn)
        {
            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
        }
        else if (getterConversion != null)
        {
            writer.WriteLine($"var swiftResult = {getterConversion};");
            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
        }
        else
        {
            writer.WriteLine("return MarshalToSwiftBuffer(result);");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-subscript setter receiver.
    /// On lookup hit, performs the assignment and returns. On miss, falls through to the
    /// next sibling.
    /// </summary>
    private void EmitSubscriptSetterLookupHit(CSharpWriter writer, string interfaceName, string slug, string indexArgs)
    {
        var proxyVar = $"proxy_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var impl = {proxyVar}.UserImpl;");
        writer.WriteLine("if (impl is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"impl[{indexArgs}] = value;");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    private void EmitMethodReceiver(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var receiverName = $"Receive_{method.Name}_{index}";
        if (!emittedReceivers.Add(receiverName))
            return;

        var proxyClassName = GetProxyClassName(protocolDecl);

        // Closure-returning protocol methods take a dedicated receiver shape — no
        // params, returns a 16-byte buffer of (fnPtr, ctxPtr) that Swift materialises
        // into a real `() -> Void` closure.
        var closureHandlerForReturn = new ClosureHandler(_typeDatabase);
        if (EveryProtocolEmitter.IsDispatchableClosureReturningMethod(method, closureHandlerForReturn))
        {
            EmitDispatchableClosureReturningMethodReceiver(writer, method, protocolDecl, proxyClassName, interfaceName, index, closureHandlerForReturn);
            return;
        }

        // Async closure-param methods receive (rawArg0_fn, rawArg0_ctx) exactly like
        // regular escaping closure params, but wrap them into a
        // Func<Task<int>> via a TaskCompletionSource bridge so the C# impl can `await`
        // the Swift async closure. Cdecl invoke thunk lives on the Swift side; the
        // completion callback resumes the TCS from a static UnmanagedCallersOnly thunk.
        if (EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, closureHandlerForReturn))
        {
            EmitDispatchableAsyncClosureMethodReceiver(writer, method, protocolDecl, proxyClassName, interfaceName, index, closureHandlerForReturn);
            return;
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!) : "void";

        // Detect existential/optional-existential return types — these can't be marshalled
        // back to Swift via Unsafe.Write because the C# interface type doesn't match
        // the Swift existential container layout (3 payload + 1 metadata + N witness table words).
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool hasOptionalExistentialReturn = hasReturn && existentialHandler.IsOptionalExistential(returnType!);

        var nonEmptyParams = method.CSSignature.Skip(1)
            .Where(p => !DefaultParameterOverloadEmitter.IsDebugParameter(p) && !p.SwiftTypeSpec.IsEmptyTuple)
            .ToList();
        // Dispatchable closure params expand into two P/Invoke slots — fnPtr + ctx — so the
        // receiver signature matches the expanded EveryProtocol cdecl vtable trampoline
        // (see EveryProtocolEmitter.CountVtableSlots). `Optional<Closure>` also uses two slots;
        // nil round-trips as `IntPtr.Zero`. Value-shaped params remain a single IntPtr each.
        var closureHandlerForParams = new ClosureHandler(_typeDatabase);
        var receiverParamFragments = new List<string>();
        for (int i = 0; i < nonEmptyParams.Count; i++)
        {
            if (EveryProtocolEmitter.TryGetDispatchableClosureParam(nonEmptyParams[i].SwiftTypeSpec, closureHandlerForParams, out _, out _))
                receiverParamFragments.Add($"IntPtr rawArg{i}_fn, IntPtr rawArg{i}_ctx");
            else
                receiverParamFragments.Add($"IntPtr rawArg{i}");
        }
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
            receiverParamFragments.Select(f => ", " + f));

        var csharpReturnType = hasReturn ? "IntPtr" : "void";

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static {csharpReturnType} {receiverName}({paramTypes})");
        writer.WriteLine("{");
        writer.Indent++;

        // Optional existential returns: return zeroed buffer representing Optional.none.
        // C# interface types (e.g. IImageDecoding?) can't be correctly marshalled into
        // Swift existential containers — constructing a valid container requires Swift type
        // metadata + protocol witness table pointers that aren't accessible from C#.
        // Returning None is safe; non-null existential return marshalling requires future
        // infrastructure (Swift metadata lookup + witness table construction from C#).
        // This is NOT a regression — before optional existential resolution, these methods
        // used AnyType? with equally invalid Unsafe.Write marshalling.
        if (hasOptionalExistentialReturn)
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(returnType!);
            var containerSizeWords = innerProtocolList != null
                ? existentialHandler.GetExistentialContainerSizeInWords(innerProtocolList)
                : 5; // default: 3 payload + 1 metadata + 1 witness table
            var containerSizeBytes = containerSizeWords * 8;
            writer.WriteLine($"// Optional existential return: can't construct valid Swift existential container from C#");
            writer.WriteLine($"// (needs type metadata + witness table). Return None until existential marshalling is implemented.");
            writer.WriteLine($"return (IntPtr)NativeMemory.AllocZeroed({containerSizeBytes});");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine();
            return;
        }

        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        // Dead-impl safe: use TryGetProxy + impl null check. A throw across
        // the [UnmanagedCallersOnly] boundary is process-terminating, so a GC'd impl or
        // unregistered proxy silently returns a default value instead (Codex P0 + P1 #3).
        //
        // Codex P1 #1: size the null-path buffer by the SAME carrier the success path
        // marshals via MarshalToSwiftBuffer<T>(...). When a return conversion is present
        // the carrier is e.g. SwiftOptional<bool> (8 bytes) — using `Unsafe.SizeOf<bool?>`
        // (2 bytes) here would hand Swift a too-small buffer and corrupt the boundary.
        bool isStringMethodReturnForNullPath = hasReturn && !method.IsAsync && IsStringTypeSpec(returnType!);
        string? methodReturnConvForSizing = null;
        if (hasReturn && !method.IsAsync)
        {
            methodReturnConvForSizing = GetReceiverExistentialGetterConversion("result", returnType!)
                ?? GetReceiverGetterConversion("result", returnType!);
        }
        var methodCarrierTypeName = methodReturnConvForSizing != null
            ? (GetReceiverGetterCarrierType(returnType!) ?? returnTypeName)
            : returnTypeName;

        string methodNullReturnExpr;
        if (!hasReturn)
        {
            methodNullReturnExpr = "return;";
        }
        else if (isStringMethodReturnForNullPath)
        {
            methodNullReturnExpr = "return MarshalStringToUtf8Slice(string.Empty);";
        }
        else
        {
            // SwiftOptional<U> carriers return the canonical .none (not a zeroed buffer, which a
            // tag-byte payload would decode as .some); plain collection carriers return a valid
            // empty collection (a zeroed buffer is a null storage pointer); other reference-type
            // wrapper carriers get a metadata-sized zero buffer matching the success-path
            // MarshalToSwiftBuffer<T>. See BuildReceiverNullFallbackExpr.
            methodNullReturnExpr = $"return {BuildReceiverNullFallbackExpr(methodCarrierTypeName)};";
        }
        writer.WriteLine($"if (!SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var proxy) || proxy is null)");
        writer.Indent++;
        writer.WriteLine(methodNullReturnExpr);
        writer.Indent--;
        writer.WriteLine("var impl = proxy.UserImpl;");
        writer.WriteLine("if (impl is null)");
        writer.Indent++;
        writer.WriteLine(methodNullReturnExpr);
        writer.Indent--;

        // Unmarshal parameters - use param{i} for local variable names to avoid conflicts with rawArg{i}
        // B10: After unmarshalling, apply type conversion from ABI to idiomatic C# types
        // (e.g., SwiftOptional<SwiftString> → string?) to match the interface method signature.
        // P0: Use ABI types for MarshalFromSwift — idiomatic types (string, bool?) can't read Swift memory.
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in nonEmptyParams)
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
            var rawArgName = $"rawParam{argIndex}";
            var argName = $"param{argIndex}";

            // Dispatchable closure params arrive as expanded (fnPtr, ctx) IntPtrs. Wrap them
            // in SwiftEscapingClosure<TDelegate>.FromSwift (which Arc.Retain's the context for
            // ARC-correct lifetime) and bind the result to the per-shape invoker class so the
            // C# impl receives a regular managed delegate (e.g. Action / Func<...>).
            // `Optional<Closure>`: nil arrives as `IntPtr.Zero` and projects to `null`.
            // Same Mono-JIT-safe pattern as closure returns: named invoker class + method
            // group (no display class in the call chain).
            if (EveryProtocolEmitter.TryGetDispatchableClosureParam(param.SwiftTypeSpec, closureHandlerForParams, out var dispatchClosure, out var isOptional))
            {
                var entryPoint = EveryProtocolEmitter.GetProtocolClosureInvokeThunkEntryPoint(protocolDecl, method, index, argIndex);
                var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
                var invokerClassName = ClosureEmitter.GetInvokerClassName(helperName);
                var delegateType = closureHandlerForParams.GetCSharpDelegateType(dispatchClosure!);
                var wrapperVar = $"_closureWrapper{argIndex}";
                var invVar = $"_inv{argIndex}";
                if (isOptional)
                {
                    var nullableDelegateType = $"{delegateType}?";
                    writer.WriteLine($"// Optional Swift closure (fnPtr, ctx) → managed {nullableDelegateType}. nil arrives as IntPtr.Zero.");
                    writer.WriteLine($"{nullableDelegateType} {argName};");
                    writer.WriteLine($"if (rawArg{argIndex}_fn == IntPtr.Zero)");
                    writer.Indent++;
                    writer.WriteLine($"{argName} = null;");
                    writer.Indent--;
                    writer.WriteLine("else");
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine($"var {wrapperVar} = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg{argIndex}_fn, rawArg{argIndex}_ctx);");
                    writer.WriteLine($"var {invVar} = new {invokerClassName}((nint){wrapperVar}.FunctionPointer, (nint){wrapperVar}.Context, {wrapperVar});");
                    writer.WriteLine($"{argName} = {invVar}.Invoke;");
                    writer.Indent--;
                    writer.WriteLine("}");
                }
                else
                {
                    writer.WriteLine($"// Wrap Swift closure (fnPtr, ctx) into a managed {delegateType} via SwiftEscapingClosure (ARC) + invoker class (Mono-JIT-safe).");
                    writer.WriteLine($"var {wrapperVar} = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg{argIndex}_fn, rawArg{argIndex}_ctx);");
                    writer.WriteLine($"var {invVar} = new {invokerClassName}((nint){wrapperVar}.FunctionPointer, (nint){wrapperVar}.Context, {wrapperVar});");
                    writer.WriteLine($"{delegateType} {argName} = {invVar}.Invoke;");
                }
            }
            // String parameter: the local MarshalFromSwift<SwiftString> helper uses Unsafe.Read<T>
            // which can't construct a managed SwiftString from raw Swift memory (16-byte value).
            // Use the runtime's SwiftMarshal.MarshalFromSwift which calls NewFromPayload.
            else if (IsStringTypeSpec(param.SwiftTypeSpec))
            {
                writer.WriteLine($"var {rawArgName} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {rawArgName}.ToString();");
            }
            // Issue #40 / P1-01: a Swift-class (or Optional<class>) param arrives as the address of a
            // borrowed slot holding the heap pointer (the Swift thunk passes &{param}Copy). Copy it out
            // via the runtime helper (deref + ObjC-aware retain + NewFromPayload). The local
            // Unsafe.Read<T> would reinterpret the heap pointer as a managed reference and SIGSEGV.
            else if (GetReceiverClassCopyOutExpr($"rawArg{argIndex}", param.SwiftTypeSpec) is string classCopyOut)
            {
                writer.WriteLine($"var {argName} = {classCopyOut};");
            }
            // Dictionaries need special handling in receiver context: the interface declares
            // IDictionary<K,V> (parameter form), but projection produces .AsProjected()
            // which returns IReadOnlyDictionary<K,V> (return form). IReadOnlyDictionary doesn't
            // implement IDictionary, so we must use .ToDictionary() for eager materialization.
            else if (GetReceiverDictionaryConversion(rawArgName, param.SwiftTypeSpec) is string receiverDictConversion)
            {
                writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {receiverDictConversion};");
            }
            else
            {
                var setterConversion = GetReceiverSetterConversion(rawArgName, param.SwiftTypeSpec);
                if (setterConversion != null)
                {
                    writer.WriteLine($"var {rawArgName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                    writer.WriteLine($"var {argName} = {setterConversion};");
                }
                else
                {
                    writer.WriteLine($"var {argName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
                }
            }
            argNames.Add(argName);
            argIndex++;
        }

        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        // Mirror the property-collision rename applied during interface emission
        // (ProtocolProxyEmitter.InterfaceImpl.cs L62–L88). Use the canonical cached
        // set populated by ProtocolHandler / InterfacePropertyNamePrecomputer so the
        // receiver's view matches what the interface actually emits — including
        // emitted static abstract property names and excluding skipped instance
        // properties. Without this, the receiver invokes `impl.RichText(range)`
        // for a method that the interface emitted as `RichTextMethod(range)`
        // because a same-named property took the PascalCased slot (CS1955), and
        // static-property collisions are missed while skipped-property collisions
        // are over-applied.
        var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> receiverPropertyNames;
        if (canonicalPropertyNames != null)
        {
            receiverPropertyNames = new HashSet<string>(canonicalPropertyNames);
        }
        else
        {
            // Defensive fallback: the prepass populates the cache for every protocol in
            // the module, so this branch should not trigger in practice. Mirror the
            // canonical construction (instance + emitted static).
            receiverPropertyNames = new HashSet<string>();
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic)
                {
                    if (_staticAbstractPropertyNames.Contains(property.Name))
                        receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
                else if (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name))
                {
                    receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
            }
        }
        var pascalMethodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: receiverPropertyNames,
            isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        if (hasReturn)
        {
            // String returns use Utf8Slice encoding to avoid ARC issues with SwiftString.
            // Skip async methods — their C# return is Task<string>, not string.
            bool isStringMethodReturn = !method.IsAsync && IsStringTypeSpec(returnType!);
            var existentialReturnConv = GetReceiverExistentialGetterConversion("result", returnType!);
            // Fall back to regular getter conversion for ObjC-bridgeable, Date, NativeRemapped, etc.
            // Without this, method returns of e.g. Foundation.NSUrl write a managed reference via
            // MarshalToSwiftBuffer instead of extracting .Handle (the ObjC pointer Swift expects).
            // Skip async methods — their C# return is Task<T>, not T, so .Handle doesn't apply.
            var returnConv = existentialReturnConv
                ?? (method.IsAsync ? null : GetReceiverGetterConversion("result", returnType!));
            writer.WriteLine($"var result = impl.{pascalMethodName}({argsString});");
            if (isStringMethodReturn)
            {
                writer.WriteLine("return MarshalStringToUtf8Slice(result);");
            }
            else if (returnConv != null)
            {
                writer.WriteLine($"var swiftResult = {returnConv};");
                writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
            }
            else
            {
                writer.WriteLine("return MarshalToSwiftBuffer(result);");
            }
        }
        else
        {
            writer.WriteLine($"impl.{pascalMethodName}({argsString});");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a dictionary conversion expression for receiver parameters using projections.
    /// Receivers pass unmarshalled ABI types to the C# interface implementation, which expects
    /// IDictionary&lt;K,V&gt; (parameter form). Projection-based .AsProjected() returns IReadOnlyDictionary,
    /// which doesn't implement IDictionary. This method uses .ToDictionary() for eager materialization
    /// to produce a Dictionary&lt;K,V&gt; that satisfies the IDictionary contract.
    /// Returns null if the type is not a dictionary or doesn't need conversion.
    /// </summary>
    private string? GetReceiverDictionaryConversion(string rawArgName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName });
        if (projection is not DictionaryProjection dict) return null;

        var keyConv = dict.KeyProjection.GetReturnElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetReturnElementConversion("kvp.Value");

        // SwiftDictionary<K,V> implements IReadOnlyDictionary, not IDictionary.
        // Receiver params need IDictionary, so always materialize via .ToDictionary().
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";

        // Cast values to the public interface type to satisfy invariant Dictionary<K,V>.
        // e.g., Dictionary<string, RowAdapterProxy> doesn't satisfy IDictionary<string, IRowAdapter>
        // even though RowAdapterProxy : IRowAdapter, because Dictionary is invariant.
        var valPubType = dict.ValueProjection.PublicType;
        var keyPubType = dict.KeyProjection.PublicType;
        return $"{rawArgName}.ToDictionary(kvp => ({keyPubType}){keyExpr}, kvp => ({valPubType}){valueExpr})";
    }

    /// <summary>
    /// Converts C# idiomatic value to Swift ABI for MarshalToSwiftBuffer in getter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first — they're more specific
        var existentialConv = GetReceiverExistentialGetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        return projection switch
        {
            StringProjection => $"new SwiftString({varName})",
            DataProjection => $"Swift.Foundation.Data.FromByteArray({varName})",
            DateProjection => $"({varName} - {DateProjection.SwiftEpoch}).TotalSeconds",
            NativeRemappedProjection nrp => nrp.FromFactoryMethod != null
                ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName})"
                : $"new {nrp.SwiftWrapperType}({varName})",
            ObjCBridgedProjection => $"{varName}.Handle",
            ObjCBridgeableProjection => $"{varName}.Handle",
            ObjCRootedClassProjection => $"{varName}.Handle",
            ArrayProjection arr => GetReceiverArrayGetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictGetterConversion(dict, varName),
            SetProjection set => GetReceiverSetGetterConversion(set, varName),
            OptionalProjection opt => GetReceiverOptionalGetterConversion(opt, varName),
            _ => null
        };
    }

    private string? GetReceiverSetGetterConversion(SetProjection set, string varName)
    {
        var rawElem = set.ElementProjection.SwiftContainerGenericType;
        var elemConv = set.ElementProjection.GetParameterElementConversion("e");
        // Skip per-element conversion when SwiftContainerGenericType matches the C# public type
        // (e.g. NonFrozenStruct): SwiftSet<TWrapper>.FromEnumerable wants typed wrappers directly so
        // ISwiftObject.MarshalToSwift copies the struct's payload bytes by value into each slot.
        // Mirrors the SetProjection.BuildContainerSetup skip-conversion rule.
        if (elemConv != null && rawElem != set.ElementProjection.PublicType)
            return $"SwiftSet<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftSet<{rawElem}>.FromEnumerable({varName})";
    }

    private string? GetReceiverArrayGetterConversion(ArrayProjection arr, string varName)
    {
        var rawElem = arr.ElementProjection.SwiftContainerGenericType;
        var elemConv = arr.ElementProjection.GetParameterElementConversion("e");
        // Same skip-conversion rule as ArrayProjection.BuildContainerSetup.
        if (elemConv != null && rawElem != arr.ElementProjection.PublicType)
            return $"SwiftArray<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftArray<{rawElem}>.FromEnumerable({varName})";
    }

    private string? GetReceiverDictGetterConversion(DictionaryProjection dict, string varName)
    {
        var rawK = dict.KeyProjection.SwiftContainerGenericType;
        var rawV = dict.ValueProjection.SwiftContainerGenericType;
        var keyConv = dict.KeyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetParameterElementConversion("kvp.Value");
        // Same skip-conversion rule as DictionaryProjection.BuildContainerSetup — when
        // SwiftContainerGenericType matches the C# public type for a key or value projection,
        // FromDictionary expects typed wrappers (not IntPtr handles) and ISwiftObject.MarshalToSwift
        // copies struct payload bytes by value via VWT.
        var skipKeyConv = keyConv != null && rawK == dict.KeyProjection.PublicType;
        var skipValConv = valConv != null && rawV == dict.ValueProjection.PublicType;
        var effectiveKeyConv = skipKeyConv ? null : keyConv;
        var effectiveValConv = skipValConv ? null : valConv;
        if (effectiveKeyConv != null || effectiveValConv != null)
        {
            var keyExpr = effectiveKeyConv ?? "kvp.Key";
            var valExpr = effectiveValConv ?? "kvp.Value";
            return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))";
        }
        return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName})";
    }

    private string? GetReceiverOptionalGetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        var optType = inner.SwiftContainerGenericType;
        // Arms that hand the wrapper VALUE straight to NewSome (NonFrozenStruct, blittable/enum,
        // FrozenWithMemory) need the inner's metadata-bearing wrapper type as the generic. This equals
        // SwiftContainerGenericType for all of them EXCEPT FrozenWithMemoryProjection, whose
        // SwiftContainerGenericType is the by-value `.Buffer` struct (nonexistent for a handle-backed
        // wrapper such as SwiftClosedRange<T>). The handle-passing arms (Class/KeyPath/ObjC) keep
        // optType, which is the nil-pointer-optimized IntPtr.
        var passthroughOptType = inner.MarshalFromSwiftType;
        return inner switch
        {
            StringProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(new SwiftString({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            DataProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(Swift.Foundation.Data.FromByteArray({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            DateProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(({varName}Val - {DateProjection.SwiftEpoch}).TotalSeconds) : SwiftOptional<{optType}>.NewNone())",
            NativeRemappedProjection nrp => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({(nrp.FromFactoryMethod != null ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName}Val)" : $"new {nrp.SwiftWrapperType}({varName}Val)")}) : SwiftOptional<{optType}>.NewNone())",
            ObjCBridgedProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
            ObjCBridgeableProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
            ArrayProjection arr => BuildOptionalContainerGetterConversion(arr, varName, optType,
                GetReceiverArrayGetterConversion(arr, $"{varName}Val")),
            DictionaryProjection dict => BuildOptionalContainerGetterConversion(dict, varName, optType,
                GetReceiverDictGetterConversion(dict, $"{varName}Val")),
            SetProjection set => BuildOptionalContainerGetterConversion(set, varName, optType,
                GetReceiverSetGetterConversion(set, $"{varName}Val")),
            // Closures have their own ABI (SwiftClosureData/function pointers) — can't wrap in SwiftOptional.
            // Passthrough; accessor methods handle closure marshalling.
            ClosureProjection => null,
            // ObjC-rooted classes use .Handle (ObjC pointer), not .Payload
            ObjCRootedClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Handle) : SwiftOptional<{optType}>.NewNone())",
            // Class: OptionalProjection.SwiftContainerGenericType returns "IntPtr" (nil-pointer-optimized),
            // so optType=IntPtr and we pass a raw IntPtr handle.
            ClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Payload.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            // KeyPath: same shape as ClassProjection but the wrapper IS the SafeHandle (no
            // .Payload indirection). OptionalProjection still uses IntPtr as the container
            // generic type because KeyPaths are nil-pointer-optimized reference classes.
            KeyPathProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            // NonFrozenStruct: optType IS the typed wrapper (NonFrozenStructProjection.SwiftContainerGenericType
            // returns _typeName). SwiftOptional<TWrapper>.NewSome takes the typed wrapper directly so
            // ISwiftObject.MarshalToSwift copies the struct's payload bytes by value via VWT.
            // Lowering the Some-arg to .Payload.DangerousGetHandle() would type-mismatch (passing IntPtr
            // where the typed wrapper is expected) — same ABI-mismatch class as
            // bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-….
            NonFrozenStructProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{passthroughOptType}>.NewSome({varName}Val) : SwiftOptional<{passthroughOptType}>.NewNone())",
            // Blittable, SimpleEnum, etc. — MarshalToSwiftBuffer writes raw bytes via Unsafe.Write<T>,
            // so C# int? (Nullable<int>) is NOT layout-compatible with SwiftOptional<int> (a class).
            // Must explicitly wrap in SwiftOptional<T>.NewSome/NewNone.
            _ => $"({varName} is {{}} {varName}Val ? SwiftOptional<{passthroughOptType}>.NewSome({varName}Val) : SwiftOptional<{passthroughOptType}>.NewNone())"
        };
    }

    private static string? BuildOptionalContainerGetterConversion(ITypeProjection inner, string varName, string optType, string? innerConv)
    {
        if (innerConv == null) return null;
        return $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({innerConv}) : SwiftOptional<{optType}>.NewNone())";
    }

    /// <summary>
    /// Issue #40 / audit P1-01: route a Swift-class (or <c>Optional&lt;class&gt;</c>) reverse-callback
    /// parameter through the runtime borrowed-slot copy-out instead of the per-proxy local
    /// <c>MarshalFromSwift&lt;T&gt;</c> (which does <c>Unsafe.Read&lt;T&gt;</c> and reinterprets the Swift
    /// heap pointer as a managed reference → SIGSEGV on first use). <paramref name="slotExpr"/> is the
    /// receiver's raw <c>IntPtr</c> argument — the address of the borrowed slot the Swift thunk passed
    /// via <c>&amp;{param}Copy</c>. Applies to true Swift classes (pure-Swift <see cref="ClassProjection"/>
    /// and <c>@objc:NSObject</c> <see cref="ObjCRootedClassProjection"/>), whose ObjC-vs-native retain
    /// dispatch the runtime helper handles via <c>swift_unknownObjectRetain</c>. ObjC-<i>bridged</i> value
    /// types (<see cref="ObjCBridgedProjection"/>, e.g. NSURLSession) are NOT Swift heap classes and keep
    /// their existing <c>MarshalFromSwift&lt;IntPtr&gt;</c> + GetNSObject path. Returns the full RHS marshal
    /// expression, or <c>null</c> for any non-class param (caller keeps its own path).
    /// </summary>
    private string? GetReceiverClassCopyOutExpr(string slotExpr, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        const string marshal = "global::Swift.Runtime.InteropServices.SwiftMarshal";
        return projection switch
        {
            ClassProjection cls => $"{marshal}.MarshalBorrowedClassFromSlot<{cls.PublicType}>({slotExpr})",
            ObjCRootedClassProjection objc => $"{marshal}.MarshalBorrowedClassFromSlot<{objc.PublicType}>({slotExpr})",
            OptionalProjection { InnerProjection: ClassProjection innerCls } =>
                $"{marshal}.MarshalBorrowedOptionalClassFromSlot<{innerCls.PublicType}>({slotExpr})",
            OptionalProjection { InnerProjection: ObjCRootedClassProjection innerObjc } =>
                $"{marshal}.MarshalBorrowedOptionalClassFromSlot<{innerObjc.PublicType}>({slotExpr})",
            _ => null
        };
    }

    /// <summary>
    /// Converts Swift ABI value to C# idiomatic for interface assignment in setter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first
        var existentialConv = GetReceiverExistentialSetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        return projection switch
        {
            StringProjection => $"{varName}.ToString()",
            DataProjection => $"{varName}.ToByteArray()",
            DateProjection => $"{DateProjection.SwiftEpoch}.AddSeconds({varName})",
            NativeRemappedProjection nrp => $"{varName}.{nrp.ToConversionMethod}()",
            ObjCBridgedProjection objc => MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, varName, nonNull: true),
            ObjCBridgeableProjection objc => MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, varName, nonNull: true),
            ArrayProjection arr => GetReceiverArraySetterConversion(arr, varName),
            DictionaryProjection dict => GetReceiverDictSetterConversion(dict, varName),
            SetProjection set => GetReceiverSetSetterConversion(set, varName),
            OptionalProjection opt => GetReceiverOptionalSetterConversion(opt, varName),
            _ => null
        };
    }

    private string? GetReceiverArraySetterConversion(ArrayProjection arr, string varName)
    {
        var elemConv = arr.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.AsProjected(e => {elemConv})";
        return null;  // SwiftArray<T> IS IReadOnlyList<T> — no conversion needed
    }

    private string? GetReceiverSetSetterConversion(SetProjection set, string varName)
    {
        var elemConv = set.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.Select(e => {elemConv}).ToHashSet()";
        return null;  // SwiftSet<T> IS IReadOnlySet<T> — no conversion needed
    }

    private string? GetReceiverDictSetterConversion(DictionaryProjection dict, string varName)
    {
        var keyConv = dict.KeyProjection.GetReturnElementConversion("k");
        var valConv = dict.ValueProjection.GetReturnElementConversion("v");
        if (keyConv == null && valConv == null) return null;
        if (keyConv != null)
        {
            var reverseKeyConv = dict.KeyProjection.GetParameterElementConversion("k") ?? "k";
            var valSelector = valConv != null ? $"v => {valConv}" : "v => v";
            return $"{varName}.AsProjected(k => {keyConv}, k => {reverseKeyConv}, {valSelector})";
        }
        return $"{varName}.AsProjected(v => {valConv})";
    }

    private string? GetReceiverOptionalSetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        return inner switch
        {
            StringProjection => $"((SwiftString?){varName})?.ToString()",
            DataProjection => $"((Swift.Foundation.Data?){varName})?.ToByteArray()",
            DateProjection => $"((double?){varName}) is {{}} {varName}DateVal ? (System.DateTimeOffset?){DateProjection.SwiftEpoch}.AddSeconds({varName}DateVal) : null",
            NativeRemappedProjection nrp => $"(({nrp.SwiftWrapperType}?){varName})?.{nrp.ToConversionMethod}()",
            ObjCBridgedProjection objc => $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, $"{varName}.Some", nonNull: true)})",
            ObjCBridgeableProjection objc => $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, $"{varName}.Some", nonNull: true)})",
            ArrayProjection arr => GetReceiverOptionalContainerSetterConversion(arr, varName, arr.PublicType),
            DictionaryProjection dict => GetReceiverOptionalContainerSetterConversion(dict, varName, dict.PublicType),
            SetProjection set => GetReceiverOptionalContainerSetterConversion(set, varName, set.PublicType),
            // Closures have their own ABI — passthrough, accessor methods handle marshalling.
            ClosureProjection => null,
            // Class/NonFrozenStruct: the Optional is already deserialized as SwiftOptional<PublicType>
            // via MarshalFromSwift — .Some returns PublicType, not IntPtr. Simple nullable cast suffices.
            _ => $"(({inner.PublicType}?){varName})"
        };
    }

    private string? GetReceiverOptionalContainerSetterConversion(ITypeProjection innerContainer, string varName, string idiomaticType)
    {
        var containerConv = innerContainer.GetReturnContainerConversion($"{varName}.Some");
        var someExpr = containerConv ?? $"{varName}.Some";
        // Cast the some arm to the idiomatic type to avoid ternary covariance issues.
        // e.g., Dictionary<string, MixpanelTypeProxy> vs IReadOnlyDictionary<string, IMixpanelType>
        return $"({varName}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType}?)null : ({idiomaticType}){someExpr})";
    }

    /// <summary>
    /// Returns the C# type name that the success-path
    /// <c>MarshalToSwiftBuffer&lt;T&gt;(swiftResult)</c> call would use as <c>T</c>, or
    /// <c>null</c> if the success path takes the no-conversion branch
    /// (<c>MarshalToSwiftBuffer(result)</c> with the idiomatic interface type).
    /// <para>
    /// This MUST stay in lockstep with <see cref="GetReceiverGetterConversion"/> and
    /// <see cref="GetReceiverExistentialGetterConversion"/> — the dead-impl null path
    /// (<see cref="BuildReceiverNullFallbackExpr"/>) allocates a fallback buffer sized from
    /// this carrier's type metadata to match what the success path would emit. If the carrier
    /// here drifts from the success path's carrier, the fallback buffer is the wrong size and
    /// Swift reads garbage memory across the receiver boundary.
    /// </para>
    /// </summary>
    private string? GetReceiverGetterCarrierType(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;
        return GetReceiverGetterCarrierTypeCore(typeSpec);
    }

    /// <summary>
    /// Builds the dead-impl fallback return expression for a receiver (proxy unregistered, or
    /// the user impl GC'd while Swift still holds a strong retain on the proxy). The
    /// <c>[UnmanagedCallersOnly]</c> boundary must not let an exception escape, so we return a
    /// default value rather than throwing.
    /// <para>
    /// For a <c>SwiftOptional&lt;U&gt;</c> carrier a zero-filled buffer is NOT <c>nil</c>: a
    /// tag-byte payload (<c>Optional&lt;ClosedRange&lt;Float&gt;&gt;</c>, <c>Optional&lt;Int&gt;</c>,
    /// any frozen/non-frozen struct inner) stores the discriminator as a trailing byte where
    /// <c>0 = Some</c>, so an all-zero buffer decodes to <c>.some(zeroed-payload)</c> — a fake
    /// value handed back to Swift instead of <c>nil</c>. Produce the canonical <c>.none</c> via
    /// <c>SwiftOptional&lt;U&gt;.NewNone()</c> marshalled through the same
    /// <c>MarshalToSwiftBuffer</c> path the success branch uses for <c>.some</c>; NewNone already
    /// encodes every inner representation correctly (tag-byte, bool, simple enum, class
    /// nil-pointer, and the value-witness fallback).
    /// </para>
    /// <para>
    /// A Swift collection carrier (<c>SwiftArray&lt;U&gt;</c>, <c>SwiftDictionary&lt;K,V&gt;</c>,
    /// <c>SwiftSet&lt;U&gt;</c>) is a single storage pointer, so a zero-filled buffer is a
    /// <em>null</em> pointer — not a valid empty collection. A caller that reads it (Count /
    /// iterate) dereferences null. Construct the canonical empty collection (<c>Array.init</c> /
    /// the empty-dictionary singleton / <c>Set.init</c>) via the wrapper's public parameterless
    /// ctor and marshal it through the same <c>MarshalToSwiftBuffer</c> path the success branch
    /// uses. This ctor shares the success path's construction surface: every collection carrier
    /// reaching this fallback is paired with a success path in the same receiver that builds the
    /// same <c>{carrier}</c> via <c>{carrier}.From*</c>, which chains to this same parameterless
    /// ctor — element <c>TypeMetadata</c>, the Set Hashable witness, the empty-dictionary
    /// singleton, and the storage allocation are resolved identically on both paths. So
    /// <c>new {carrier}()</c> cannot fail to resolve element metadata the success path resolves —
    /// including existential-container element carriers such as
    /// <c>SwiftDictionary&lt;…, ExistentialContainer0&gt;</c> for <c>[String: Any]</c>. This is
    /// not a no-throw guarantee: the ctor can still throw (unresolvable metadata/witness, OOM),
    /// but only for an element type whose <c>From*</c> success path would throw identically — a
    /// collection member that is non-functional regardless of which branch runs. That throw
    /// fail-fasts the boundary, which is strictly preferable to the prior null storage pointer
    /// Swift would dereference.
    /// </para>
    /// <para>
    /// All other non-optional carriers (existential containers, value types, <c>IntPtr</c>) have
    /// no <c>nil</c>/empty case to construct, so they keep the metadata-sized zero buffer — the
    /// least-bad default for a vanished impl.
    /// </para>
    /// </summary>
    private static string BuildReceiverNullFallbackExpr(string carrierTypeName)
    {
        if (carrierTypeName.StartsWith("SwiftOptional<", System.StringComparison.Ordinal))
            return $"MarshalToSwiftBuffer({carrierTypeName}.NewNone())";
        if (carrierTypeName.StartsWith("SwiftArray<", System.StringComparison.Ordinal) ||
            carrierTypeName.StartsWith("SwiftDictionary<", System.StringComparison.Ordinal) ||
            carrierTypeName.StartsWith("SwiftSet<", System.StringComparison.Ordinal))
            return $"MarshalToSwiftBuffer(new {carrierTypeName}())";
        return $"AllocZeroedSwiftBuffer<{carrierTypeName}>()";
    }

    private string? GetReceiverGetterCarrierTypeCore(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        // Existential carriers — must mirror GetReceiverExistentialGetterConversion's order.
        if (projection is ExistentialProjection existProj)
            return existProj.PInvokeType;

        if (projection is OptionalProjection optExist && optExist.InnerProjection is ExistentialProjection innerExist)
            return $"SwiftOptional<{innerExist.PInvokeType}>";

        if (projection is ArrayProjection arrExistProj && arrExistProj.ElementProjection is ExistentialProjection arrExist)
            return $"SwiftArray<{arrExist.PInvokeType}>";

        if (projection is DictionaryProjection dictExistProj && dictExistProj.ValueProjection is ExistentialProjection dictExist)
        {
            var abiKeyType = dictExistProj.KeyProjection.SwiftContainerGenericType;
            return $"SwiftDictionary<{abiKeyType}, {dictExist.PInvokeType}>";
        }

        // Non-existential carriers — must mirror GetReceiverGetterConversion's switch.
        return projection switch
        {
            // StringProjection is special-cased to Utf8Slice in the receiver — never reaches MarshalToSwiftBuffer.
            DataProjection => "Swift.Foundation.Data",
            DateProjection => "double",
            NativeRemappedProjection nrp => nrp.SwiftWrapperType,
            ObjCBridgedProjection => "IntPtr",
            ObjCBridgeableProjection => "IntPtr",
            ObjCRootedClassProjection => "IntPtr",
            ArrayProjection arr => $"SwiftArray<{arr.ElementProjection.SwiftContainerGenericType}>",
            DictionaryProjection dict => $"SwiftDictionary<{dict.KeyProjection.SwiftContainerGenericType}, {dict.ValueProjection.SwiftContainerGenericType}>",
            SetProjection set => $"SwiftSet<{set.ElementProjection.SwiftContainerGenericType}>",
            // FrozenWithMemory inner: SwiftContainerGenericType is the nonexistent by-value `.Buffer`;
            // use the wrapper type so this carrier matches the SwiftOptional<wrapper> built in the getter
            // conversion above. Every other inner keeps SwiftContainerGenericType — in particular class
            // inners stay nil-pointer-optimized (SwiftOptional<IntPtr>).
            OptionalProjection opt => $"SwiftOptional<{(opt.InnerProjection is FrozenWithMemoryProjection ? opt.InnerProjection.MarshalFromSwiftType : opt.InnerProjection.SwiftContainerGenericType)}>",
            // No conversion → success path uses MarshalToSwiftBuffer(result) with the idiomatic type.
            // Caller falls back to that type for sizing.
            _ => null
        };
    }

    /// <summary>
    /// Gets a conversion expression for existential types in getter returns (C# idiomatic → Swift ABI).
    /// Uses TypeProjectionFactory to project the type, then extracts parameter element conversions
    /// (public → ABI direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        // Standalone existential
        if (projection is ExistentialProjection existProj)
            return existProj.GetParameterElementConversion(varName);

        // Optional<existential>
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var containerType = innerExist.PInvokeType;
            var extractExpr = innerExist.GetParameterElementConversion($"{varName}Val");
            return $"({varName} is {{}} {varName}Val ? SwiftOptional<{containerType}>.NewSome({extractExpr}) : SwiftOptional<{containerType}>.NewNone())";
        }

        // Array<existential>
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var containerType = arrExist.PInvokeType;
            var elemConv = arrExist.GetParameterElementConversion("i");
            return $"SwiftArray<{containerType}>.FromEnumerable({varName}.Select(i => {elemConv}))";
        }

        // Dictionary<K, existential>
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var containerType = dictExist.PInvokeType;
            var keyConv = dictProj.KeyProjection.GetParameterElementConversion("kvp.Key");
            var abiKeyType = dictProj.KeyProjection.SwiftContainerGenericType;
            // Skip per-element key conversion when SwiftContainerGenericType matches the C# public type
            // (e.g. NonFrozenStruct like Swift.AnyHashable): SwiftDictionary<TWrapper, V>.FromDictionary
            // wants typed wrappers directly so ISwiftObject.MarshalToSwift copies the struct's payload
            // bytes by value into each slot. Lowering to .Payload.DangerousGetHandle() would yield
            // Dictionary<nint, V> and fail to convert to IEnumerable<KeyValuePair<TWrapper, V>>.
            // Mirrors the skip-conversion rule in DictionaryProjection.BuildContainerSetup and
            // BuildDictionarySetterReceiverExpression.
            var skipKeyConv = keyConv != null && abiKeyType == dictProj.KeyProjection.PublicType;
            var keyExpr = skipKeyConv ? "kvp.Key" : (keyConv ?? "kvp.Key");
            var valConv = dictExist.GetParameterElementConversion("kvp.Value");
            return $"SwiftDictionary<{abiKeyType}, {containerType}>.FromDictionary({varName}.ToDictionary(kvp => {keyExpr}, kvp => {valConv}))";
        }

        return null;
    }

    /// <summary>
    /// Gets a conversion expression for existential types in setter params (Swift ABI → C# idiomatic).
    /// Uses TypeProjectionFactory to project the type, then extracts return element conversions
    /// (ABI → public direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName });
        if (projection == null) return null;

        // Standalone existential
        if (projection is ExistentialProjection existProj)
            return existProj.GetReturnElementConversion(varName);

        // Optional<existential>
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var publicType = innerExist.PublicType;
            var wrapExpr = innerExist.GetReturnElementConversion($"{varName}.Some");
            return $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : ({publicType}?){wrapExpr})";
        }

        // Array<existential>
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var publicType = arrExist.PublicType;
            var elemConv = arrExist.GetReturnElementConversion("c");
            return $"{varName}.AsProjected<{publicType}>(c => {elemConv})";
        }

        // Dictionary<K, existential>
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var publicType = dictExist.PublicType;
            var valConv = dictExist.GetReturnElementConversion("kvp.Value");
            var keyConv = dictProj.KeyProjection.GetReturnElementConversion("kvp.Key");
            var keyExpr = keyConv ?? "kvp.Key";
            return $"{varName}.ToDictionary(kvp => {keyExpr}, kvp => ({publicType}){valConv})";
        }

        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String.
    /// String returns from proxy receivers use Utf8Slice encoding instead of MarshalToSwiftBuffer,
    /// because SwiftString contains ARC-managed references that Unsafe.Write can't retain.
    /// </summary>
    private static bool IsStringTypeSpec(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec nts && nts.Name == "Swift.String";
    }

    /// <summary>
    /// Returns true if the protocol's existential is class-bound (2-word
    /// <c>[classRef][witnessTable]</c> layout). Reads <see cref="TypeRecordFlags.ClassBound"/>
    /// off the protocol's own TypeRecord; ModuleProcessor walks the inheritance chain
    /// when setting the flag so a child protocol inheriting class-boundedness from any
    /// ancestor (Kidoz issue #40) is correctly classified here without a second walk.
    /// Also used by ProtocolProxyEmitter.SwiftObject.cs for the Swift→C# wrap factory.
    /// </summary>
    private bool IsProtocolClassBound(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.SwiftTypeName != null
            && _typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
            && record.Kind == TypeRecordKind.Protocol)
        {
            return (record.Flags & TypeRecordFlags.ClassBound) != 0;
        }
        // Defensive fallback for emit paths that run before the TypeRecord is
        // registered (direct-emitter test fixtures and external callers). Only
        // the directly-declared bit is available here; transitive class-boundedness
        // requires the parse-time walk's TypeRecord flag.
        return protocolDecl.IsClassBound;
    }

    /// <summary>
    /// Records the class-bound protocol's descriptor symbol + exporting library for
    /// <c>ClassExistentialContainer1</c> metadata registration in the module initializer.
    /// No-op for opaque protocols and when the descriptor symbol or library can't be resolved
    /// (cross-module protocols whose defining module isn't in the database). The carrier metadata
    /// is protocol-agnostic for the arity, so a single successful registration covers every
    /// class-bound existential in the module.
    /// </summary>
    private void RecordClassBoundExistentialMetadata(ProtocolDecl protocolDecl)
    {
        if (_emissionContext == ModuleEmissionContext.Default)
            return;
        if (!IsProtocolClassBound(protocolDecl))
            return;
        if (protocolDecl.SwiftTypeName == null
            || !_typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
            || record.Kind != TypeRecordKind.Protocol
            || string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
            return;

        var moduleName = protocolDecl.ModuleDecl?.Name ?? protocolDecl.SwiftTypeName.Module;
        if (string.IsNullOrEmpty(moduleName))
            return;

        string libraryName;
        try
        {
            libraryName = _typeDatabase.GetLibraryPath(moduleName);
        }
        catch
        {
            // Defining module not in the database (cross-module protocol vended by a
            // dependency the binding doesn't own a database for) — skip; registration
            // is best-effort and one class-bound protocol's descriptor suffices.
            return;
        }

        _emissionContext.RecordClassBoundExistentialRegistration(libraryName, record.ProtocolDescriptorSymbol!);
    }

    private void EmitConstructors(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Class-bound protocols (: AnyObject) use a 2-word existential layout:
        //   [classRef] [witnessTable]
        // Opaque protocols use a 5-word layout:
        //   [payload0] [payload1] [payload2] [metadata] [witnessTable]
        // We always allocate ExistentialContainer1 (5 words) but fill the first N words
        // according to the protocol's existential layout.
        //
        // _useObjCBase protocols (NSObjectProtocol-rooted via EveryObjCProtocol) are
        // class-bound by construction even when protocolDecl.IsClassBound is false
        // (the `IsClassBound` bool on the model only reflects an explicit AnyObject /
        // : class constraint — it does not transitively detect NSObjectProtocol
        // inheritance). Swift loads `any P` for these as a 2-word ObjC protocol
        // existential, so the witness table must land at Payload1, not _witnessTable0.
        //
        // Class-boundedness inherits transitively: `protocol Child: Parent` where Parent
        // is `: AnyObject` makes Child class-bound too. ModuleProcessor walks the chain
        // when setting TypeRecordFlags.ClassBound, so reading the flag here is sufficient.
        // Without the 2-word layout, Swift reads WT from Payload1 (which C# leaves zero)
        // → SIGSEGV on the first witness dispatch (Kidoz issue #40 repro).
        var useClassBoundContainerLayout = IsProtocolClassBound(protocolDecl) || _useObjCBase;
        var containerInitLines = useClassBoundContainerLayout
            ? "_swiftContainer.Payload1 = (IntPtr)ProtocolWitnessTableHandle;"
            : "_swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();\n                _swiftContainer[0] = ProtocolWitnessTableHandle;";

        // Metadata cache priming is opaque-layout-only and must never poison the
        // EveryProtocol static cache from the EveryObjCProtocol code path: storing
        // EveryObjCProtocol metadata into EveryProtocol.SetTypeMetadata would survive
        // for the process lifetime and corrupt later opaque-layout proxies that read
        // ObjectMetadata back from the cache (construction-order dependent bug).
        var metadataInitBlock = (_useObjCBase || _useEntityBase)
            ? "// Class-bound carrier (_useObjCBase / _useEntityBase): the 2-word layout doesn't\n                    // consult ObjectMetadata, and the carrier's own metadata handle\n                    // (GetEveryObjCProtocolMetadata / GetEveryEntityProtocolMetadata) MUST NOT be\n                    // cached into EveryProtocol.SetTypeMetadata — that static slot is owned by the\n                    // pure-Swift EveryProtocol code path, and priming it from a carrier would\n                    // poison later opaque-layout proxies that read ObjectMetadata back from it."
            : $"if (EveryProtocol.GetTypeMetadata().Handle == IntPtr.Zero)\n                        EveryProtocol.SetTypeMetadata(NativeMethods.{GetMetadataMethodName}());";

        // Constructor for C# implementation
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy wrapping a C# implementation of {{interfaceName}}.
            /// </summary>
            /// <param name="implementation">The C# implementation of the protocol.</param>
            public unsafe {{proxyClassName}}({{interfaceName}} implementation)
            {
                if (implementation == null) throw new ArgumentNullException(nameof(implementation));
                // Weak reference — see the field declaration in
                // ProtocolProxyEmitter.StaticInit.cs for the rationale. The
                // impl-anchored lifetime model requires that the proxy does NOT
                // strongly root the impl; otherwise the strong-registry chain
                // prevents impl GC, prevents tracker release, prevents deinit,
                // prevents unregister — a permanent leak.
                _csharpImplRef = new WeakReference<{{interfaceName}}>(implementation);

                // Create a real Swift {{(_useObjCBase ? "EveryObjCProtocol" : "EveryProtocol")}} instance via @_cdecl factory.
                // The pointer carries a +1 retain from Unmanaged.passRetained(). We hold
                // it as a plain IntPtr — the +1 is owned by ProxyLifetimeTracker, anchored
                // to the lifetime of _csharpImpl. When the impl is GC'd, the tracker's
                // finalizer calls SwiftReleaseTrampoline.Release (the documented
                // finalizer-safe path on Mono); Swift's deinit then fires and
                // OnEveryProtocolDeinit drops the SwiftObjectRegistry strong root.
                _everyProtocolHandle = NativeMethods.{{CreateHelperMethodName}}();

                try
                {
                    // Initialize EveryProtocol metadata from Swift (once per process).
                    // Only relevant for opaque (non-class-bound) protocols where the
                    // existential container stores ObjectMetadata. Class-bound
                    // (NSObjectProtocol-rooted EveryObjCProtocol) proxies skip this
                    // entirely so the EveryObjCProtocol metadata never poisons the
                    // pure-Swift EveryProtocol static cache.
                    {{metadataInitBlock}}

                    // Create existential container manually
                    _swiftContainer = new ExistentialContainer1();
                    _swiftContainer.Payload0 = _everyProtocolHandle;
                    {{containerInitLines}}

                    // Register this proxy so Swift callbacks can find us. The strong
                    // registry entry is dropped when OnEveryProtocolDeinit fires from
                    // Swift's deinit, which can only happen after ProxyLifetimeTracker
                    // has released the +1 (i.e., after impl GC).
                    SwiftObjectRegistry.RegisterStrong(_everyProtocolHandle, this);

                    // Wire Swift deinit -> C# callback. The context arg is the handle
                    // itself, so OnEveryProtocolDeinit can locate the registry entry
                    // and tracker bookkeeping for targeted teardown.
                    NativeMethods.{{SetDeinitCallbackMethodName}}(
                        _everyProtocolHandle,
                        &Swift.Runtime.ProxyLifetimeTracker.OnEveryProtocolDeinit,
                        _everyProtocolHandle);

                    // Anchor the ground-state +1 to the impl lifetime. Tracker must be
                    // called AFTER the deinit callback is wired up so that a super-fast
                    // Swift release (e.g., never-stored call) still routes through
                    // OnEveryProtocolDeinit before the finalizer path runs.
                    Swift.Runtime.ProxyLifetimeTracker.Track(implementation, _everyProtocolHandle);
                }
                catch
                {
                    // Ctor failed before tracker/registry wiring was complete — release
                    // the +1 directly to avoid leaking the Swift instance.
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
                    try { Arc.Release(_everyProtocolHandle); } catch { /* already deallocating */ }
                    throw;
                }
                // C#-impl-backed proxies never adopt a +1 container (ProxyLifetimeTracker
                // owns the release path); the finalizer's container release is a no-op for
                // them, so suppress it to keep them off the finalizer queue.
                GC.SuppressFinalize(this);
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            /// <summary>
            /// Creates a proxy from an existing Swift existential container.
            /// This constructor is used internally by generated marshalling code.
            /// </summary>
            /// <remarks>
            /// Swift-backed proxies created with this constructor dispatch blittable and String
            /// protocol members through witness table accessors. Non-dispatchable members
            /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
            /// </remarks>
            /// <param name="container">The Swift existential container.</param>
            /// <param name="ownsContainer">
            /// True when this proxy ADOPTS a Swift-returned existential at +1 (set by the
            /// owned-return marshalling paths). The proxy then owns the container's
            /// value-witness retains and releases them on Dispose/finalize. Defaults to
            /// false: borrowed parameter wraps, payload-pointer reads, and externally
            /// constructed/synthetic containers do NOT own a +1 and must not be released.
            /// </param>
            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
            public {{proxyClassName}}(ExistentialContainer1 container, bool ownsContainer = false)
            {
                _swiftContainer = container;
                _csharpImplRef = null;
                _everyProtocolHandle = IntPtr.Zero;
                _ownsContainer = ownsContainer;
                // Only an owning proxy has anything to release; suppress the finalizer for
                // borrowed/synthetic containers so they never queue a no-op finalize (and,
                // critically, never run a value-witness Destroy on a container they don't own).
                if (!ownsContainer)
                    GC.SuppressFinalize(this);
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            """);
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-property getter receiver.
    /// On lookup hit, materialises the property value, applies any conversion, and returns
    /// via the appropriate marshalling helper. On miss, falls through to the next sibling.
    /// </summary>
    private static void EmitGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string? getterConversion, bool isStringReturn)
    {
        var proxyVar = $"proxy_{slug}";
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var {implVar} = {proxyVar}.UserImpl;");
        writer.WriteLine($"if ({implVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var result = {implVar}.{pascalPropertyName};");
        if (isStringReturn)
            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
        else if (getterConversion != null)
        {
            writer.WriteLine($"var swiftResult = {getterConversion};");
            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
        }
        else
        {
            writer.WriteLine("return MarshalToSwiftBuffer(result);");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-property setter receiver.
    /// On lookup hit, assigns the marshalled value and returns. On miss, falls through to
    /// the next sibling. The trailing block implicitly silently drops the write when no
    /// sibling proxy is registered for this handle.
    /// </summary>
    private static void EmitSetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string assignmentExpr)
    {
        var proxyVar = $"proxy_{slug}";
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var {implVar} = {proxyVar}.UserImpl;");
        writer.WriteLine($"if ({implVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{implVar}.{pascalPropertyName} = {assignmentExpr};");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Returns a fully-qualified C# interface name suitable for use in a same-file
    /// <c>SwiftObjectRegistry.TryGetProxy&lt;IProtocolProxyImpl&lt;...&gt;&gt;</c> call.
    /// Cross-module siblings need the <c>global::&lt;ns&gt;.{Interface}</c> prefix because
    /// the proxy class file may not <c>using</c> the sibling's namespace; <c>&lt;ns&gt;</c>
    /// is the GENERATED C# namespace (resolved through <see cref="NamespacePatternResolver"/>),
    /// not the raw Swift module name — under <c>--namespace-pattern</c> they can differ
    /// (e.g. Swift <c>StoreKit</c> → C# <c>StoreKit2</c>).
    /// </summary>
    private string GetQualifiedInterfaceName(ProtocolDecl protocolDecl)
    {
        var moduleName = protocolDecl.ModuleDecl?.Name ?? "";
        var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: moduleName);
        if (protocolDecl.ParentDecl is TypeDecl parentType)
        {
            var parentNames = new List<string>();
            BaseDecl? current = parentType;
            while (current is TypeDecl td)
            {
                parentNames.Insert(0, td.Name);
                current = td.ParentDecl;
            }
            baseName = string.Join(".", parentNames) + "." + baseName;
        }
        if (string.IsNullOrEmpty(moduleName))
            return baseName;
        var csNamespace = _emissionContext.NamespaceResolver?.ResolveNamespace(moduleName) ?? moduleName;
        return $"global::{csNamespace}.{baseName}";
    }

    /// <summary>
    /// Closure-property setter body (assignment + wrapper construction), parameterised
    /// over the impl variable so the no-fallback and per-sibling-lookup paths can share
    /// the same body shape.
    /// </summary>
    private static void EmitClosureSetterBody(CSharpWriter writer, bool isOptional, string pascalPropertyName,
        string delegateType, string invokerClassName, string implVar)
    {
        writer.WriteLine("if (rawFn == IntPtr.Zero)");
        writer.WriteLine("{");
        writer.Indent++;
        if (isOptional)
            writer.WriteLine($"{implVar}.{pascalPropertyName} = null;");
        else
            writer.WriteLine("// Non-Optional closure property: nil fnPtr is a contract violation; leave existing impl value unchanged.");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine($"var _wrapper = SwiftEscapingClosure<{delegateType}>.FromSwift(rawFn, rawCtx);");
        writer.WriteLine($"var _inv = new {invokerClassName}((nint)_wrapper.FunctionPointer, (nint)_wrapper.Context, _wrapper);");
        writer.WriteLine($"{implVar}.{pascalPropertyName} = _inv.Invoke;");
        writer.WriteLine("return;");
    }

    /// <summary>
    /// Closure-property setter sibling-lookup branch: tries
    /// <c>IProtocolProxyImpl&lt;sibling&gt;</c>; on hit it executes the same setter
    /// body and returns. On miss it falls through to the next sibling. The trailing
    /// block silently drops the write when no sibling proxy matches the handle.
    /// </summary>
    private void EmitClosureSetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        bool isOptional, string pascalPropertyName, string delegateType, string invokerClassName)
    {
        var proxyVar = $"proxy_{slug}";
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var {implVar} = {proxyVar}.UserImpl;");
        writer.WriteLine($"if ({implVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        EmitClosureSetterBody(writer, isOptional, pascalPropertyName, delegateType, invokerClassName, implVar);
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closure-property getter body (reads delegate from impl, allocates GCHandle, writes
    /// (fnPtr, ctxPtr) into the 16-byte buffer). Caller is responsible for the
    /// surrounding allocation and the final <c>return buf;</c>.
    /// </summary>
    private static void EmitClosureGetterBody(CSharpWriter writer, string pascalPropertyName,
        string nullableDelegateType, string getterThunkName, string implVar)
    {
        writer.WriteLine($"{nullableDelegateType} _del = {implVar}.{pascalPropertyName};");
        writer.WriteLine("if (_del is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        // GCHandle is freed by the Swift-side _SBClosureCtx box's deinit via the
        // SwiftClosureContext destroy trampoline.
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.Alloc(_del);");
        writer.WriteLine($"*(IntPtr*)buf = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&{getterThunkName};");
        writer.WriteLine("*(IntPtr*)(buf + IntPtr.Size) = global::System.Runtime.InteropServices.GCHandle.ToIntPtr(_gch);");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closure-property getter sibling-lookup branch: tries
    /// <c>IProtocolProxyImpl&lt;sibling&gt;</c>; on hit it executes the getter body
    /// and returns the populated buffer. On miss it falls through to the next sibling
    /// (which keeps the zero-initialised buffer).
    /// </summary>
    private void EmitClosureGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string nullableDelegateType, string getterThunkName)
    {
        var proxyVar = $"proxy_{slug}";
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<{interfaceName}>>(handle, out var {proxyVar}) && {proxyVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var {implVar} = {proxyVar}.UserImpl;");
        writer.WriteLine($"if ({implVar} is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        EmitClosureGetterBody(writer, pascalPropertyName, nullableDelegateType, getterThunkName, implVar);
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }
}
