// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Emits a Swift wrapper for methods returning opaque types (some Protocol).
        /// The wrapper calls the original function and boxes the return value into an
        /// existential container (any Protocol) that matches the C# ExistentialContainer type.
        /// </summary>
        private void EmitOpaqueReturnWrapper(SwiftWriter swiftWriter)
        {
            if (!_requiresOpaqueReturnWrapper)
                return;

            // Async / throwing methods are handled by their own harness — EmitAsync emits a
            // Task-based callback wrapper (which already boxes the opaque return into an
            // existential) and the C# P/Invoke that targets it. This thin @_silgen_name alias
            // emits a synchronous `return self.method()` with no `try`/`await`, so for an async
            // or throwing method it (a) fails to compile ("'async' call …", "call can throw …")
            // and (b) for async collides on the shared `{mangled}_async` symbol + PInvoke name
            // the async harness already owns — a duplicate definition. Skip it: the alias is
            // only valid for synchronous, non-throwing opaque-return methods.
            if (_env.MethodDecl.IsAsync || _env.MethodDecl.Throws)
                return;

            // When the method has a @_cdecl wrapper, the standard wrapper body already handles
            // opaque returns via initializeMemory(as: (any Protocol).self). The @_silgen_name
            // wrapper is only needed as a fallback when no @_cdecl wrapper is available.
            if (_env.MethodDecl.UsesCdeclMethodWrapper || _env.MethodDecl.UsesCdeclPropertyWrapper)
                return;

            var returnTypeSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec as ProtocolListTypeSpec;
            if (returnTypeSpec == null)
                return;

            // Build the "any Protocol1 & Protocol2" return type string
            var anyReturnType = "any " + string.Join(" & ", returnTypeSpec.Protocols.Keys.Select(p => p.Name));

            var parentTypeName = (_env.ParentDecl as TypeDecl)?.SwiftTypeName;
            bool isInstanceMethod = _env.MethodDecl.MethodType != MethodType.Static;
            bool isAccessor = _env.MethodDecl.IsAccessor;

            // Build Swift parameter list (matching the original function's signature)
            var opaqueDerefLines = new List<string>();
            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p =>
                {
                    if (OptionalPointerWrapperEmitter.ShouldWidenParam(p, _env.BoundGenericsHandler))
                    {
                        opaqueDerefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(p, p.Name, p.Name, _env.TypeDatabase));
                        return $"{p.Name}: UnsafeRawPointer";
                    }
                    return $"{p.Name}: {(p.IsGeneric ? _env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName : p.SwiftTypeSpec)}";
                });

            string parameters = string.Join(", ", methodParams);

            // Build the argument forwarding list
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p =>
                {
                    var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(p, _env.BoundGenericsHandler)
                        ? $"{p.Name}Val" : p.Name;
                    // Provenance-aware call label (canonical builder) — preserves labels that
                    // genuinely begin with '_' (e.g. _self) and backtick-escapes keywords.
                    return $"{CdeclParamMapper.BuildSwiftCallArgLabel(p)}{valueRef}";
                }));

            var genericParams = _env.MethodDecl.IsGeneric
                ? $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>"
                : "";

            var whereClause = _env.MethodDecl.IsGeneric
                ? WrapperEmitterHelpers.BuildSwiftWhereClause(_env.MethodDecl.GenericParameters)
                : "";

            // Pre-format deref lines for insertion into raw string templates
            var extDerefCode = opaqueDerefLines.Count > 0
                ? string.Join("\n                    ", opaqueDerefLines) + "\n                    "
                : "";
            var freeDerefCode = opaqueDerefLines.Count > 0
                ? string.Join("\n                ", opaqueDerefLines) + "\n                "
                : "";

            // Determine if wrapper needs @MainActor annotation (only for @MainActor, not custom actors)
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
                _env.ParentDecl, _env.MethodDecl.IsMainActorIsolated, _env.MethodDecl.IsNonisolated);
            var mainActorAttr = needsMainActor ? "@MainActor " : "";

            // Merge the method's availability with its parent type chain so the
            // opaque-return @_silgen_name wrapper can reference newer SDK APIs.
            // The availability must sit on the `extension ... {` line itself (the
            // extended type may be gated), not just on the inner function.
            var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
                _env.MethodDecl.AvailabilityAnnotations, _env.ParentDecl);
            var extensionAvailabilityLines = BuildAvailabilityAttributeLines(mergedAvailability, separator: "\n            ");
            var extensionAvailabilityPrefix = extensionAvailabilityLines.Length > 0
                ? extensionAvailabilityLines + "\n            "
                : string.Empty;
            var extensionWhereClause = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(
                _env.MethodDecl, _env.ParentDecl as TypeDecl);

            if (parentTypeName != null)
            {
                if (isAccessor)
                {
                    // Property getter wrapper - strip the _Get/_Set suffix to get the Swift property name
                    var propertyName = _env.MethodDecl.Name;
                    if (propertyName.EndsWith("_Get")) propertyName = propertyName.Substring(0, propertyName.Length - 4);
                    else if (propertyName.EndsWith("_Set")) propertyName = propertyName.Substring(0, propertyName.Length - 4);
                    var staticModifier = !isInstanceMethod ? "static " : "";
                    swiftWriter.WriteLine($$"""
            {{extensionAvailabilityPrefix}}extension {{parentTypeName.ModuleQualifiedName}}{{extensionWhereClause}} {
                {{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.EmissionSymbol, _env.MethodDecl)}}")
                public {{staticModifier}}var _sb_{{propertyName}}: {{anyReturnType}} {
                    return {{(!isInstanceMethod ? parentTypeName.ModuleQualifiedName + "." : "self.")}}{{propertyName}}
                }
            }
            """);
                }
                else
                {
                    // Method wrapper
                    var staticModifier = !isInstanceMethod ? "static " : "";
                    var callPrefix = !isInstanceMethod ? $"{parentTypeName.ModuleQualifiedName}." : "self.";
                    swiftWriter.WriteLine($$"""
            {{extensionAvailabilityPrefix}}extension {{parentTypeName.ModuleQualifiedName}}{{extensionWhereClause}} {
                {{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.EmissionSymbol, _env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.EmissionSymbol, _env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
                    {{extDerefCode}}return {{callPrefix}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
                }
            }
            """);
                }
            }
            else
            {
                // Free function wrapper (module-level)
                var moduleName = _env.MethodDecl.ModuleDecl?.Name ?? "";
                var freeAvailabilityLines = BuildAvailabilityAttributeLines(mergedAvailability, separator: "\n            ");
                var freeAvailabilityPrefix = freeAvailabilityLines.Length > 0
                    ? freeAvailabilityLines + "\n            "
                    : string.Empty;
                swiftWriter.WriteLine($$"""
            {{freeAvailabilityPrefix}}{{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.EmissionSymbol, _env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.EmissionSymbol, _env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
                {{freeDerefCode}}return {{(moduleName.Length > 0 ? moduleName + "." : "")}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
            }
            """);
            }
        }

        /// <summary>
        /// Builds a joined string of <c>@available(Platform Version, *)</c> attributes using the
        /// supplied separator. Returns empty when there are no annotations. Deduped by
        /// platform+version (mirrors <see cref="WrapperEmitterHelpers.EmitSwiftAvailability"/>).
        /// </summary>
        private static string BuildAvailabilityAttributeLines(IReadOnlyList<AvailabilityAnnotation>? annotations, string separator)
        {
            // Route through the shared helper so per-platform deduplication and the
            // macCatalyst-tracks-iOS lift apply uniformly to every wrapper variant.
            var keys = WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations);
            if (keys.Count == 0)
                return string.Empty;

            var parts = new List<string>(keys.Count);
            foreach (var key in keys)
                parts.Add($"@available({key}, *)");
            return string.Join(separator, parts);
        }

        /// <summary>
        /// Returns true when the given NamedTypeSpec is registered in the TypeDatabase as a
        /// Swift struct or enum (i.e., an actual value type) — even if it lives in an
        /// AutoBridge Apple framework module where <see cref="TypeDatabaseExtensions.IsObjCModuleType"/>
        /// would otherwise assume ObjC class semantics.
        ///
        /// Context: newer Apple frameworks (AuthenticationServices, etc.) keep introducing
        /// Swift-native struct types that aren't in AppleFrameworkRegistry.ValueTypes. Without
        /// this check, the caller's parameter marshalling path emits <c>x?.Handle ?? IntPtr.Zero</c>
        /// (ObjC class idiom) for a type that actually exposes <c>.Payload</c> (Swift struct idiom).
        /// </summary>
        private static bool IsKnownSwiftValueType(NamedTypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeSpec.Name);
            if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
                return false;
            return record.Kind == TypeRecordKind.Struct || record.Kind == TypeRecordKind.Enum;
        }

        /// <summary>
        /// Emits bound generic argument marshalling.
        /// Skips arguments that have type conversion (those are handled by EmitTypeConversions).
        /// </summary>
        private void EmitBoundGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.BoundGenericsHandler.IsBoundGeneric))
            {
                // Skip if this argument uses type conversion (already handled in EmitTypeConversions),
                // but only if the projection factory can actually handle it. For cases like
                // Array<BoundGeneric> where the inner type has generic parameters, the factory
                // returns null and EmitTypeConversions won't emit the buffer variable — so we
                // must handle it here via the bound generic buffer extraction path.
                if (!_env.MethodDecl.IsAccessor && MarshallingHelpers.IsConvertibleType(argumentDecl.SwiftTypeSpec))
                {
                    var projection = s_projectionFactory.Project(argumentDecl.SwiftTypeSpec,
                        _env.NewProjectionContext(isParameter: true, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
                    if (projection != null)
                        continue;
                }

                // Skip Optional<existential> — handled by dedicated existential marshalling path
                if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec))
                    continue;

                // Skip decomposed Optional setter params — P/Invoke uses (IntPtr, byte) directly,
                // no bound generic buffer extraction needed. PropertyHandler.EmitSetter passes
                // payload + hasValue directly to the accessor method.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    !_env.MethodDecl.IsSubscriptAccessor &&
                    OptionalMarshalClassifier.IsDecomposed(argumentDecl.SwiftTypeSpec, _env.TypeDatabase))
                    continue;

                // Optional<ObjC> accessor setter: parameter is already IntPtr (nullable pointer ABI).
                // Just alias to the buffer name that the P/Invoke expects.
                if (_env.MethodDecl.IsAccessor && MarshallingHelpers.IsOptionalObjCBridged(argumentDecl.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                    csWriter.WriteLine($"IntPtr {bufferName} = {csName};");
                    continue;
                }

                // ObjC-bridgeable container accessor setter (e.g., [URL], [String: URL], Set<URL>):
                // Parameter is already IntPtr (ObjC collection handle from PropertyHandler conversion).
                // Just alias to the buffer name that the P/Invoke expects.
                if (_env.MethodDecl.IsAccessor &&
                    (CdeclParamMapper.IsObjCBridgeableContainer(argumentDecl.SwiftTypeSpec, _env.TypeDatabase) ||
                     CdeclParamMapper.IsOptionalObjCBridgeableContainer(argumentDecl.SwiftTypeSpec, _env.TypeDatabase)))
                {
                    var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                    csWriter.WriteLine($"IntPtr {bufferName} = {csName};");
                    continue;
                }

                if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(argumentDecl))
                {
                    var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);

                    // Bug #8: Check if the bound generic's root type is a frozen struct projected as class
                    // (has PayloadBuffer). Non-frozen generic types (like BatchedCollectionIndex<T0>)
                    // use SwiftSafeHandle and should be marshalled via .Payload.DangerousGetHandle().
                    var rootTypeName = SwiftTypeName.FromTypeSpec((NamedTypeSpec)argumentDecl.SwiftTypeSpec);
                    if (_env.TypeDatabase.TryGetTypeRecord(rootTypeName, out var argTypeRecord) &&
                        MarshallingHelpers.IsFrozenStructProjectedAsClass(argTypeRecord))
                    {
                        // @_cdecl wrapper: Swift receives UnsafeRawPointer and does .load(as: T.self).
                        // Pass pointer TO the value (DangerousGetHandle), not the dereferenced value
                        // (PayloadBuffer.Buffer). The .load(as:) reads from the pointer location.
                        //
                        // Exception: Optional<ClassType> uses nullable pointer ABI in @_cdecl
                        // (UnsafeMutableRawPointer? — nil for .none, object pointer for .some).
                        // For these, extract the actual IntPtr value via PayloadBuffer, NOT the
                        // buffer address via DangerousGetHandle. DangerousGetHandle returns the
                        // buffer address (always non-nil), causing the Swift wrapper to treat the
                        // buffer address as an object reference → SIGSEGV.
                        if (_env.MethodDecl.UsesCdeclWrapper)
                        {
                            if (CdeclParamMapper.IsOptionalWithReferenceInner(argumentDecl.SwiftTypeSpec, _env.TypeDatabase))
                            {
                                csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                                csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                            }
                            else
                            {
                                // SafeHandlePin brackets DangerousAddRef/DangerousRelease so a
                                // concurrent GC finalization cannot free the Swift heap payload
                                // between Payload.DangerousGetHandle() and the Swift function entry.
                                csWriter.WriteLine($"using SafeHandlePin {csName}Pin = new SafeHandlePin({csName}.Payload);");
                                csWriter.WriteLine($"IntPtr {bufferName} = {csName}Pin.Handle;");
                            }
                        }
                        // Large optional accessor params (e.g., Optional<SwiftString>) have payloads
                        // exceeding IntPtr size. PayloadBuffer<IntPtr> would truncate — use the full
                        // buffer via DangerousGetHandle instead, pinned via SafeHandlePin.
                        else if (_env.MethodDecl.IsAccessor && _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec))
                        {
                            csWriter.WriteLine($"using SafeHandlePin {csName}Pin = new SafeHandlePin({csName}.Payload);");
                            csWriter.WriteLine($"IntPtr {bufferName} = {csName}Pin.Handle;");
                        }
                        else
                        {
                            csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                            csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                        }
                    }
                    else
                    {
                        // Non-frozen type: use handle-based marshalling, pinned via SafeHandlePin
                        // so the SafeHandle cannot be finalized between the handle access and the
                        // Swift function entry.
                        csWriter.WriteLine($"using SafeHandlePin {csName}Pin = new SafeHandlePin({csName}.Payload);");
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Pin.Handle;");
                    }
                }
            }
        }

        /// <summary>
        /// Emits closure argument marshalling.
        /// For @convention(c) closures, converts C# delegates to unmanaged function pointers.
        /// For escaping closures, creates closure data with a thunk and GCHandle context.
        /// For optional closures, handles null by creating a zero-initialized SwiftClosureData.
        /// </summary>
        private void EmitClosureMarshalling(CSharpWriter csWriter)
        {
            var closureParams = _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure).ToList();
            int closureParamCount = closureParams.Count;

            foreach (var argumentDecl in closureParams)
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                bool isOptional = _env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec, _env.EmissionSymbol, closureParamCount))
                {
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    if (isOptional)
                    {
                        // Optional @convention(c) closures may be stored and called later by Swift
                        // (Optional closures are always escaping). ThreadStatic is unsound for this
                        // case — fall back to Marshal.GetFunctionPointerForDelegate. This works on
                        // NativeAOT (device); on Mono AOT (simulator) it requires JIT trampolines
                        // which may not be available. Optional escaping @convention(c) closures are
                        // rare in practice.
                        string marshalSource = csName;
                        if (ClosureEmitter.NeedsConventionCBoolBridge(closureTypeSpec))
                        {
                            var bridgeName = $"_{csName}_boolBridge";
                            ClosureEmitter.EmitConventionCBoolBridge(csWriter, csName, bridgeName, closureTypeSpec, _env.ClosureHandler);
                            marshalSource = bridgeName;
                            csWriter.WriteLine($"var {bridgeName}Handle = System.Runtime.InteropServices.GCHandle.Alloc({bridgeName});");
                        }

                        csWriter.WriteLines($"""
                            var {csName}FuncPtr = {csName} != null
                                ? ({funcPtrType})Marshal.GetFunctionPointerForDelegate({marshalSource})
                                : ({funcPtrType})IntPtr.Zero;
                            """);
                    }
                    else
                    {
                        // Non-optional @convention(c) closure: marshal through the per-method+param
                        // [ThreadStatic] slot + [UnmanagedCallersOnly] thunk. This is the AOT-safe path
                        // (no JIT trampoline) the iOS simulator requires; the optional arm above cannot
                        // use it because an Optional closure escapes and may be invoked after the call.
                        //
                        // The ABI parser conservatively marks ALL @convention(c) closures escaping, so
                        // the escaping signal cannot distinguish a genuinely-stored closure from a plain
                        // synchronous one — both come through here. A single static slot is unsound under
                        // synchronous reentrancy: a callback that re-enters the same method+param would
                        // overwrite the slot the outer call still reads. The slot is made reentrancy-safe
                        // and leak-free on the method path by a save/restore pair around this set: a
                        // pre-try local captures the slot's prior occupant
                        // (EmitConventionCSlotSaveDeclarations) and the trailing finally restores it
                        // (EmitFinally) — restoring rather than clearing to null, which would null-deref
                        // an outer re-invocation. The thunk fires synchronously within the P/Invoke scope.
                        // This set site and the thunk/field emit on every path; the save/restore pair is
                        // method-path-only (constructors of this shape are skipped upstream and emit no
                        // slot). All of these gate on UsesConventionCThreadStaticSlot so they never drift
                        // out of sync.
                        var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);
                        csWriter.WriteLine($"{baseName}_del = {csName};");
                        csWriter.WriteLine($"var {csName}FuncPtr = ({funcPtrType}){baseName}_ptr;");
                    }
                }
                else if (_env.ClosureHandler.IsAsyncClosure(closureTypeSpec)
                         && _env.ClosureHandler.IsBaselineAsyncClosure(closureTypeSpec))
                {
                    // Baseline async closures (throwing and non-throwing) share a
                    // state-based pattern: the state type holds the user's async
                    // delegate, and we pass (context, startFunc) to Swift. Non-baseline
                    // async closures fall through to AnyType placeholder in PInvokeEmitter
                    // so the outer method is skipped cleanly.
                    ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
                        csWriter,
                        _env.MethodDecl.Name,
                        csName,
                        closureTypeSpec,
                        _env.ClosureHandler,
                        _env.EmissionSymbol);
                }
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, closureParamCount))
                {
                    if (_env.MethodDecl.HasCdeclClosureMarshalling)
                    {
                        // Cdecl wrapper (standalone or @_cdecl inline): just allocate the GCHandle if closure is non-null.
                        // The call-argument mapping (MethodSignature) handles passing func ptr and context.
                        if (isOptional)
                        {
                            csWriter.WriteLine($"if ({csName} != null)");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                            csWriter.Indent--;
                        }
                        else
                        {
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                        }
                    }
                    else
                    {
                        // Legacy SwiftClosureData path (for async methods with non-async closures,
                        // and for didReceiveData-shape streaming callbacks). For escaping shapes we
                        // wrap the GCHandle in an `_SBClosureCtx` ARC box via the runtime helper
                        // so Swift's release of the closure frees the handle exactly once. The box
                        // pointer falls back to the raw GCHandle pointer when the runtime dylib is
                        // not packaged — preserving prior behaviour (leak rather than crash).
                        var callbackName = ClosureHandler.GetCallbackFunctionName(_env.MethodDecl.Name, argumentDecl.Name, _env.EmissionSymbol);
                        bool legacyEscaping = WrapperValidation.IsEffectivelyEscaping(
                            closureTypeSpec, argumentDecl.SwiftTypeSpec, _env.ClosureHandler);

                        if (isOptional)
                        {
                            // Optional escaping closure - handle null case with zero-initialized SwiftClosureData
                            csWriter.WriteLine($"SwiftClosureData {csName}Closure;");
                            csWriter.WriteLine($"if ({csName} != null)");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                            if (legacyEscaping)
                            {
                                csWriter.WriteLine($"{csName}Box = SwiftClosureMarshaller.TryAllocateBoxedContext(GCHandle.ToIntPtr({csName}Handle));");
                                csWriter.WriteLine($"{csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, {csName}Box != IntPtr.Zero ? {csName}Box : GCHandle.ToIntPtr({csName}Handle));");
                            }
                            else
                            {
                                csWriter.WriteLine($"{csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({csName}Handle));");
                            }
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
                            csWriter.WriteLine("else");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Closure = default; // Zero-initialized = nil in Swift");
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
                        }
                        else if (legacyEscaping)
                        {
                            csWriter.WriteLines($"""
                                {csName}Handle = GCHandle.Alloc({csName});
                                {csName}Box = SwiftClosureMarshaller.TryAllocateBoxedContext(GCHandle.ToIntPtr({csName}Handle));
                                var {csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, {csName}Box != IntPtr.Zero ? {csName}Box : GCHandle.ToIntPtr({csName}Handle));
                                """);
                        }
                        else
                        {
                            csWriter.WriteLines($"""
                                {csName}Handle = GCHandle.Alloc({csName});
                                var {csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({csName}Handle));
                                """);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Emits type conversions for parameters that use idiomatic .NET types.
        /// Uses projection-first approach: tries TypeProjectionFactory, falls back to inline code.
        /// </summary>
        private void EmitTypeConversions(CSharpWriter csWriter)
        {
            // Skip type conversions for property accessors — property wrapper handles conversion
            // EXCEPT Optional-existential parameters which need container extraction here
            // (but NOT for @_cdecl setters — PropertyHandler.EmitSetter handles marshalling)
            if (_env.MethodDecl.IsAccessor)
            {
                foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
                {
                    if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                        !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec) &&
                        !_env.MethodDecl.UsesCdeclPropertyWrapper)
                        TryEmitParameterConversionViaProjection(csWriter, argumentDecl);
                }
                return;
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (!MarshallingHelpers.IsConvertibleType(argumentDecl.SwiftTypeSpec) &&
                    !_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                    !_env.TypeConversionHandler.HasNativeTypeRemapping(argumentDecl.SwiftTypeSpec))
                    continue;
                if (_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                    continue;

                TryEmitParameterConversionViaProjection(csWriter, argumentDecl);
            }
        }

        /// <summary>
        /// Emits existential container marshalling for @_cdecl wrapper methods.
        /// Creates the container, copies it to a heap allocation, and creates an IntPtr pointer.
        /// Must be called AFTER EmitExistentialHeapDeclarations (which declares the void* heap
        /// variables) and AFTER EmitTryBlockStart (so the allocation is inside the try block
        /// for cleanup in finally).
        /// </summary>
        private void EmitExistentialContainerMarshalling(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return;

            // For async methods the cleanup is owned by the typed holder's Cleanup() (see
            // ExistentialContainerHeap in AsyncHelpers.cs). Each heap buffer is appended to the
            // holder's ExistentialHeaps list in marshalling order; the callback frees them.
            bool isAsync = _requiresSwiftAsync;

            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1)
                .Where(a => _env.ExistentialHandler.IsExistential(a.SwiftTypeSpec)))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(arg.SwiftTypeSpec);
                if (protocolList == null || !_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    continue;
                var containerType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                var csName = NameProvider.GetCSharpParameterName(arg);
                // GetOrCreate only works for single-protocol (EC1) interfaces.
                // Well-known types (AnyError/EC0) and compositions (EC2+) use direct cast.
                bool owningCandidate = IsOwningExistentialCandidate(protocolList);
                if (owningCandidate)
                {
                    // Auto-wrap fallback only when we actually emit a proxy class for the protocol.
                    // Stdlib/external protocols (e.g. Swift.Encodable) project to publicType "object"
                    // and have no generated {Protocol}Proxy class — emitting `new EncodableProxy(...)`
                    // would produce CS0246. Same for protocols without TypeRecords.
                    string? proxyClassName = null;
                    if (publicType != "object" &&
                        _env.ExistentialHandler.AllProtocolsHaveTypeRecords(protocolList) &&
                        _env.ExistentialHandler.TryGetFilteredProxyClassName(protocolList, out var filteredProxy))
                    {
                        var qualifiedProxy = _env.ExistentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
                        // CONSUME gate: when the proxy class was not emitted (EveryProtocol conformance
                        // suppressed), drop the wrap fallback so GetOrCreate uses the no-fallback overload.
                        // The member stays — a Swift-vended conformer still round-trips through its own
                        // witness table. Replaces the retired generate-then-strip wrap-fallback downgrade post-pass.
                        if (!_env.ExistentialHandler.IsProxyNameSuppressed(filteredProxy, qualifiedProxy, _emissionContext))
                            proxyClassName = qualifiedProxy;
                    }
                    // Thread the runtime owns-bit out of GetOrCreate (declared before the try
                    // by EmitExistentialHeapDeclarations) so the finally / async holder destroys ONLY
                    // a freshly boxed value conformer's +1, never a borrowed proxy container. Also
                    // thread out the keep-alive proxy (change 4): the finally / async holder pins it
                    // across the native call so an auto-wrapped proxy cannot be finalized — releasing
                    // R0 — while Swift is still reading the borrowed container.
                    var createExpr = proxyClassName != null
                        ? $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({csName}, static __v => new {proxyClassName}(__v), out {csName}Owns, out {csName}KeepAlive)"
                        : $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({csName}, out {csName}Owns, out {csName}KeepAlive)";
                    csWriter.WriteLine($"var {csName}Container = {createExpr};");
                }
                else
                    csWriter.WriteLine($"var {csName}Container = ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){csName}).GetExistentialContainer();");
                csWriter.WriteLine($"{csName}Heap = NativeMemory.Alloc((nuint)Unsafe.SizeOf<{containerType}>());");
                csWriter.WriteLine($"Unsafe.Copy({csName}Heap, ref {csName}Container);");
                csWriter.WriteLine($"IntPtr {csName}Ptr = (IntPtr){csName}Heap;");

                if (isAsync)
                {
                    // The foreground finally is skipped for async; the holder carries the owns-bit
                    // + witness count so the callback cleanup runs the existential destroy after
                    // the continuation drains the @in_guaranteed buffer. BOTH holders also carry the
                    // keep-alive reference (change 4) so the GCHandle-rooted holder keeps R0 alive
                    // across the suspension (the async analog of the synchronous GC.KeepAlive): the
                    // owning-candidate path pins the GetOrCreate-boxed proxy local, the EC2+/well-known
                    // borrowed path pins the parameter itself (owns=false — it never boxed a +1).
                    var holderCtor = owningCandidate
                        ? $"new ExistentialContainerHeap((IntPtr){csName}Heap, {csName}Owns, {protocolList.Protocols.Count}, {csName}KeepAlive)"
                        : $"new ExistentialContainerHeap((IntPtr){csName}Heap, false, {protocolList.Protocols.Count}, {csName})";
                    csWriter.WriteLine($"_asyncCallHolder.ExistentialHeaps.Add({holderCtor});");
                }
            }
        }

        /// <summary>
        /// Emits Arc.Retain for array parameters in constructors without @_cdecl wrappers.
        /// When a constructor stores an array parameter (e.g., variadic init), Swift expects
        /// @owned semantics (caller transfers ownership). Without a @_cdecl wrapper, the C#
        /// 'using var' blocks dispose the temporary SwiftArray after the P/Invoke, freeing
        /// the data Swift stored. The retain ensures the array survives disposal with a net
        /// +1 retain count that Swift owns.
        /// </summary>
        private void EmitArrayOwnershipRetain(CSharpWriter csWriter)
        {
            // Only needed for constructors without @_cdecl wrappers (direct CallConvSwift dispatch).
            // @_cdecl wrappers handle ownership transfer in the Swift wrapper code.
            if (_env.MethodDecl.UsesCdeclConstructorWrapper)
                return;

            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (MarshallingHelpers.IsSwiftArray(arg.SwiftTypeSpec))
                {
                    var csName = NameProvider.GetCSharpParameterName(arg);
                    csWriter.WriteLine($"Arc.Retain({csName}Buffer);");
                }
            }
        }

        /// <summary>
        /// Tries to emit parameter conversion via the projection factory.
        /// Returns true if the projection handled the parameter, false if fallback is needed.
        /// </summary>
        private bool TryEmitParameterConversionViaProjection(CSharpWriter csWriter, ArgumentDecl argumentDecl)
        {
            var projection = s_projectionFactory.Project(argumentDecl.SwiftTypeSpec,
                _env.NewProjectionContext(isParameter: true, genericContext: _genericContext, parentTypeDecl: _env.ParentDecl as TypeDecl));
            if (projection == null)
                return false;

            var csName = NameProvider.GetCSharpParameterName(argumentDecl);

            // B12: ObjC optional inner — extract Handle directly instead of using projection.
            //
            // Delegate to MarshallingHelpers.IsOptionalObjCBridged so this gate uses the SAME
            // precedence rule as TypeProjectionFactory (TypeRecord-first; auto-bridge fallback
            // gated on IsOptionalFallbackModule + HasObjCClassPrefix). The module-name
            // heuristic alone misclassifies plain Swift classes whose ABI printedName uses an
            // umbrella re-export module — e.g., `RealityKit.Entity` for a class that lives
            // in RealityFoundation — and emits `<param>?.Handle ?? IntPtr.Zero` for a class
            // that has no Handle property (only .Payload), producing CS1061. ObjCRooted
            // classes use SwiftOptional<T> ABI, not nullable pointer, so they are
            // intentionally excluded by IsOptionalObjCBridged.
            if (projection is OptionalProjection &&
                MarshallingHelpers.IsOptionalObjCBridged(argumentDecl.SwiftTypeSpec, _env.TypeDatabase))
            {
                var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                csWriter.WriteLine($"IntPtr {bufferName} = {csName}?.Handle ?? IntPtr.Zero;");
                return true;
            }

            // Check if Optional param needs DangerousGetHandle override.
            // @_cdecl wrappers receive all Optional value-type params as UnsafeRawPointer
            // and call .load(as: Optional<T>.self) — they need a POINTER to the buffer,
            // not the dereferenced value. PayloadBuffer<IntPtr>.Buffer dereferences, giving
            // the raw value bytes which Swift misinterprets as a pointer → misaligned crash.
            // Exception: Optional<Class/ObjC> uses nullable pointer ABI (nil/pointer value).
            //
            // Also covers large Optionals (String, URL, structs ≥ 8B) and Optional<Protocol>
            // where ExistentialContainer (40+ bytes) would be truncated to 8 bytes.
            if (projection is OptionalProjection optProjForHandle)
            {
                bool isLargeOpt = _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec);
                bool isOptProtocol = _env.BoundGenericsHandler.IsLargeOptionalProtocolParam(argumentDecl.SwiftTypeSpec);
                bool needsLargeOptOverride = (isLargeOpt || isOptProtocol) &&
                    (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary ||
                     _env.MethodDecl.IsAsync || _requiresOpaqueReturnWrapper);

                // @_cdecl wrapper: ALL non-reference Optional params need DangerousGetHandle
                // because Swift receives UnsafeRawPointer and calls .load(as: Optional<T>.self).
                bool needsCdeclOptOverride = _env.MethodDecl.UsesCdeclWrapper &&
                    !CdeclParamMapper.IsOptionalWithReferenceInner(argumentDecl.SwiftTypeSpec, _env.TypeDatabase);

                // Raw CallConvSwift with Optional<generic-param> inner: Swift ABI passes generic
                // Optionals indirectly (size unknown at caller). C# must pass the buffer ADDRESS,
                // not the dereferenced first IntPtr — otherwise Swift treats the heap-pointer
                // value as a buffer pointer and reads the inner instance's first 8 bytes
                // (typically a class type-metadata word) into the receiving slot.
                bool needsGenericOptOverride =
                    optProjForHandle.InnerProjection is BlittableProjection blitInner
                    && blitInner.IsGenericParameter;

                if (needsLargeOptOverride || needsCdeclOptOverride || needsGenericOptOverride)
                {
                    projection = new OptionalProjection(optProjForHandle.InnerProjection, optProjForHandle.IsExistentialInner, useDangerousGetHandle: true);
                }

                // Raw CallConvSwift Optional<generic-param> uses Swift @in (callee-destroyed)
                // convention. After PInvoke the buffer is deinitialized, so the SwiftOptional
                // must skip its normal VWT Destroy to avoid double-releasing class fields.
                //
                // Every Swift-side wrapper path (@_cdecl, optional-pointer, async harness, opaque
                // return, wrapper library) reads the C# buffer with `.pointee` / `.load(as:)` —
                // copy semantics, NOT @in — so the caller still owns the value and full Dispose
                // must run. Likewise, `inout Optional<T>` is `@inout` (caller-owned, with writeback),
                // not callee-destroyed.
                bool routesViaSwiftWrapper =
                    _env.MethodDecl.UsesCdeclWrapper ||
                    _env.MethodDecl.HasOptionalPointerWrapper ||
                    _env.MethodDecl.UsesWrapperLibrary ||
                    _env.MethodDecl.IsAsync ||
                    _requiresOpaqueReturnWrapper;

                if (needsGenericOptOverride && !routesViaSwiftWrapper && !argumentDecl.IsInOut)
                {
                    _inConventionOptionalNames.Add(csName);
                }
            }

            // Accessor setter with ObjC bridge container: parameter is already IntPtr (ObjC handle).
            // Just alias it to {name}Buffer for the P/Invoke call naming convention.
            if (_env.MethodDecl.IsAccessor && projection.UsesObjCContainerBridge &&
                (projection is ArrayProjection or DictionaryProjection or SetProjection or OptionalProjection))
            {
                csWriter.WriteLine($"IntPtr {csName}Buffer = {csName};");
                return true;
            }

            var plan = projection.GetParameterPlan(csName);

            // @_cdecl wrapper: collection params (Array, Dictionary, Set) pass a pointer to the
            // container value via UnsafeRawPointer. Swift does .load(as: T.self) from the pointer.
            // Use DangerousGetHandle (pointer TO value) instead of PayloadBuffer (dereferenced value).
            // Exception: ObjC bridge containers already produce the correct handle expression
            // (e.g., NSArray.Handle) — the SwiftArray override would generate invalid code.
            if (_env.MethodDecl.UsesCdeclWrapper &&
                (projection is ArrayProjection or DictionaryProjection or SetProjection) &&
                !projection.UsesObjCContainerBridge)
            {
                // Async hand-off: when the wrapper is async, the SwiftArray/Set/Dictionary's
                // 'using var' would dispose the buffer when the foreground wrapper returns
                // tcs.Task — before the Swift continuation finishes reading the buffer on
                // its own thread. Hoist the container into the per-call AsyncDeferredDisposeList
                // (allocated by EmitAsync) so it's disposed by the holder cleanup loop after
                // the Swift continuation completes.
                string? deferredListName = _env.MethodDecl.IsAsync
                    ? "_asyncDeferredList"
                    : null;
                CdeclMarshallingHelper.RenderWithHandleOverride(csWriter, plan, csName, deferredListName);
                return true;
            }

            // SwiftString ABI decomposition for @_cdecl constructor/method wrappers (Finding 56d
            // string by-value fast path): build the transient Swift String directly into a 16-byte
            // STACK buffer via EphemeralSwiftString instead of the heap SwiftString + SafeHandle +
            // PayloadBuffer the general parameter path (plan.SetupStatements) would allocate. The
            // two extracted words are byte-identical to the heap path's {csName}Disposable.Buffer
            // (same SBW_SwiftString_Create output), the borrowed (+0) value lives across the call,
            // and the +1 is released by the `using` Dispose (SBW_SwiftString_Destroy) exactly once —
            // identical ABI and lifetime, no observable change. The local variable names
            // ({csName}_w0, {csName}_w1) match the P/Invoke parameter names emitted by PInvokeEmitter,
            // so GetCallArgumentString returns them directly; the heap setup is intentionally skipped
            // because the decompose path never references {csName}Disposable.
            if (MarshallingHelpers.ShouldDecomposeStringForCdecl(_env.MethodDecl, argumentDecl.SwiftTypeSpec))
            {
                csWriter.WriteLine($"using var {csName}Swift = new SwiftString.EphemeralSwiftString({csName});");
                csWriter.WriteLine($"var {csName}Buf = {csName}Swift.Buffer;");
                csWriter.WriteLine($"nint {csName}_w0 = Unsafe.As<SwiftString.Buffer, nint>(ref {csName}Buf);");
                csWriter.WriteLine($"nint {csName}_w1 = Unsafe.Add(ref Unsafe.As<SwiftString.Buffer, nint>(ref {csName}Buf), 1);");
                return true;
            }

            MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);

            // Foundation.Data ABI decomposition for @_cdecl constructor/method wrappers:
            // Extract two nint words from the 16-byte Swift.Foundation.Data struct (mirrors the
            // SwiftString two-word path above). The DataProjection setup already declared the mutable
            // local {csName}Swift, so we ref it directly — no extra Buffer copy is needed. The local
            // names ({csName}_w0, {csName}_w1) match the P/Invoke parameter names emitted by
            // PInvokeEmitter, so GetCallArgumentString returns them via the default parameter.Name case.
            if (MarshallingHelpers.ShouldDecomposeDataForCdecl(_env.MethodDecl, argumentDecl.SwiftTypeSpec))
            {
                csWriter.WriteLine($"nint {csName}_w0 = Unsafe.As<Swift.Foundation.Data, nint>(ref {csName}Swift);");
                csWriter.WriteLine($"nint {csName}_w1 = Unsafe.Add(ref Unsafe.As<Swift.Foundation.Data, nint>(ref {csName}Swift), 1);");
            }

            return true;
        }

        /// <summary>
        /// Emits marshalling code for @_cdecl frozen struct parameters.
        /// Blittable frozen structs use stackalloc + MarshalToSwift to create a native buffer.
        /// Frozen structs with ref fields (e.g., containing String) use Payload.DangerousGetHandle()
        /// to get a pointer to the existing native buffer.
        /// Both paths produce an IntPtr variable ({name}Ptr) consumed by GetCallArgumentString.
        /// </summary>
        private void EmitCdeclFrozenStructMarshalling(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return;

            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (!WrapperValidation.IsNonPrimitiveFrozenStructParam(argument, _env.TypeDatabase))
                    continue;

                // Skip parameters already handled by other marshalling paths:
                // - Bound generics (Array, Dict, Set, Optional) → EmitBoundGenericArguments
                // - Closures → EmitClosureMarshalling
                // - Type conversions (String, URL, Data) → EmitTypeConversions
                // - Native-remapped types → handled by NativeRemappedFrozen in PInvokeEmitter
                //
                // SIMD bound-generic aliases (Swift.SIMD2/3/4<Float>) are the exception:
                // PInvokeEmitter wires them through CdeclFrozenStruct so the param is IntPtr,
                // which means the wrapper body MUST emit the matching stackalloc/MarshalToSwift
                // setup here. Skipping them like other bound-generics would leave the wrapper
                // passing the raw Vector3/Vector4 value at a parameter slot now typed as IntPtr,
                // producing a CS-side type mismatch.
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument) &&
                    !(argument.SwiftTypeSpec is NamedTypeSpec simdBgWrap && CdeclParamMapper.IsSimdVectorType(simdBgWrap)))
                    continue;
                if (_env.ClosureHandler.IsClosure(argument))
                    continue;
                if (MarshallingHelpers.IsConvertibleType(argument.SwiftTypeSpec))
                    continue;
                if (_env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argument);
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
                var csTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;

                if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                {
                    // Frozen struct with ref fields (e.g., has String): the native buffer
                    // is already allocated by the C# class's Payload. Get its handle.
                    csWriter.WriteLine($"IntPtr {csName}Ptr = {csName}.Payload.DangerousGetHandle();");
                }
                else if (_env.MethodDecl.IsAsync)
                {
                    // Async method: skip — frozen blittable struct params are heap-allocated
                    // by EmitAsync instead of stackalloc. stackalloc is unsafe across await
                    // boundaries because the stack buffer is invalidated when the frame suspends.
                    continue;
                }
                else
                {
                    // Sync method: blittable frozen struct — allocate a stack buffer, marshal the struct into it.
                    csWriter.WriteLines($"""
                        var {csName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csTypeName}>();
                        byte* {csName}Buffer = stackalloc byte[(int){csName}Metadata.Size];
                        var {csName}Span = new Span<byte>({csName}Buffer, (int){csName}Metadata.Size);
                        SwiftMarshal.MarshalToSwift({csName}, ref {csName}Span);
                        IntPtr {csName}Ptr = (IntPtr){csName}Buffer;
                        """);

                    // inout: the Swift @_cdecl wrapper writes the mutated value back through the buffer
                    // pointer (var + defer { pointee = … }). Read it back into the now-`ref` public param
                    // after the call so the caller observes the mutation. The signature handler emits `ref`
                    // for every concrete inout; this is the readback half for blittable frozen structs.
                    // Primitives need no readback (their P/Invoke takes `ref value` directly); frozen
                    // structs with ref fields share the caller's Payload buffer in place (handled above).
                    if (argument.IsInOut)
                        _cdeclFrozenStructInoutWritebacks.Add(
                            $"{csName} = SwiftMarshal.MarshalFromSwift<{csTypeName}>({csName}Ptr);");
                }
            }
        }

        /// <summary>
        /// Emits P/Invoke declarations for error helpers (SBW_GetErrorDescription, SBW_ReleaseError,
        /// SBW_Free, and optionally SBW_ExtractTypedError_*).
        /// Delegates to <see cref="ErrorDescriptionEmitter"/> for centralized emission.
        /// </summary>
        private void EmitErrorHelperPInvokes(CSharpWriter csWriter)
        {
            if (_syncPlan.SwiftError == null) return;

            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            // C1: Base error P/Invokes (GetErrorDescription, ReleaseError, Free)
            ErrorDescriptionEmitter.EmitCSharpBaseErrorPInvokesIfNeeded(
                csWriter, typeKey, moduleDecl.Name, wrapperLibPath,
                _env.PInvokeHelperContext, _emissionContext);

            // C2: Typed-throws extractor P/Invoke
            if (_syncPlan.SwiftError is { IsTypedThrows: true, SwiftErrorTypeName: not null, TypedErrorSafeSuffix: not null })
            {
                ErrorDescriptionEmitter.EmitCSharpTypedErrorExtractorIfNeeded(
                    csWriter, typeKey, moduleDecl.Name, wrapperLibPath,
                    _syncPlan.SwiftError.SwiftErrorTypeName, _syncPlan.SwiftError.TypedErrorSafeSuffix,
                    _env.PInvokeHelperContext, _emissionContext);
            }
        }

        /// <summary>
        /// Emits callback functions and pointers for escaping closures.
        /// When HasClosureCdeclWrapper is set, non-async closure callbacks use CallConvCdecl
        /// instead of CallConvSwift to avoid Mono JIT assertion crashes.
        /// </summary>
        private void EmitClosureCallbacks(CSharpWriter csWriter)
        {
            // Determine if callbacks should use Cdecl calling convention.
            // Async+throwing closures always use their own Cdecl pattern regardless.
            var useCdecl = _env.MethodDecl.HasCdeclClosureMarshalling;
            var closureParamCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                // Non-optional @convention(c) closures: emit [UnmanagedCallersOnly(CallConvCdecl)]
                // callback + [ThreadStatic] delegate storage. This is the AOT-safe path (no JIT
                // trampoline) the iOS simulator requires, in place of Marshal.GetFunctionPointerForDelegate.
                // The single slot is kept reentrancy-safe and leak-free by the save/restore stack
                // discipline (EmitConventionCSlotSaveDeclarations + the finally restore). The OPTIONAL
                // @convention(c) arm uses a real function pointer and does NOT use the slot — it is
                // excluded by UsesConventionCThreadStaticSlot.
                if (UsesConventionCThreadStaticSlot(argumentDecl, closureTypeSpec, closureParamCount))
                {
                    EmitConventionCCallback(csWriter, argumentDecl, closureTypeSpec);
                    csWriter.WriteLine();
                    continue;
                }

                if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, closureParamCount))
                {
                    // Box-vs-raw context gate, shared by all three escaping-callback shapes
                    // below (non-throwing, throwing, indirect-return). On the non-cdecl legacy
                    // SwiftClosureData path the setter boxes the GCHandle in an `_SBClosureCtx`
                    // (legacyEscaping in EmitClosureMarshalling) and the context slot carries the
                    // box pointer with no Swift-side unbox, so the trampoline must read it via
                    // GetDelegateFromBoxedContext. A mismatch (box stored, raw read) misreads the
                    // box pointer as a GCHandle → InvalidCastException escaping the
                    // [UnmanagedCallersOnly] callback → SIGABRT. Computed ONCE here so the three
                    // callback shapes cannot drift out of sync — that drift was the original
                    // defect (throwing + indirect-return read raw while the setter boxed).
                    bool useBoxedContext = !useCdecl
                        && WrapperValidation.IsEffectivelyEscaping(closureTypeSpec, argumentDecl.SwiftTypeSpec, _env.ClosureHandler);

                    // Baseline async closures (throwing or non-throwing) emit the
                    // Start-thunk callback pair. Non-baseline async-throwing closures
                    // fall through with no callback emitted —
                    // PInvokeEmitter projects them to AnyType so the outer method is skipped
                    // via the placeholder path instead of crashing here. Non-baseline async
                    // non-throwing closures keep their legacy escaping-callback path below — the async bridge only
                    // handles primitive-return baseline shapes.
                    if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec)
                        || _env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(closureTypeSpec))
                    {
                        if (_env.ClosureHandler.IsBaselineAsyncClosure(closureTypeSpec))
                        {
                            ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol);
                            ClosureEmitter.EmitAsyncThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol);
                        }
                    }
                    // Check if this is a throwing closure (but not async+throwing)
                    else if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        // Throwing closures need a special callback that handles SwiftError. A
                        // non-cooperative managed exception is converted to a Swift error via
                        // SBW_CreateError_{module}; emit its C# P/Invoke here (the Swift @_cdecl
                        // helper is emitted alongside the closure adapter in the wrapper lib).
                        var moduleName = _env.MethodDecl.ModuleDecl?.Name ?? "SwiftBindings";
                        var errorMintLib = _env.TypeDatabase.AsyncLibraryName
                            ?? _env.TypeDatabase.GetLibraryPath(moduleName);
                        SwiftErrorMintEmitter.EmitPInvokeIfNeeded(csWriter, moduleName, errorMintLib, _env, _emissionContext);
                        ClosureEmitter.EmitThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, useCdecl);
                        ClosureEmitter.EmitThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, moduleName, useCdecl, useBoxedContext);
                    }
                    // Check if this closure needs indirect return marshalling
                    else if (_env.ClosureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec))
                    {
                        // A non-cdecl escaping closure with an indirect (bound-generic / non-frozen)
                        // return reaches this branch with a boxed context (see shared gate above).
                        ClosureEmitter.EmitIndirectReturnCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, useCdecl);
                        ClosureEmitter.EmitIndirectReturnCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, useCdecl, useBoxedContext);
                    }
                    else
                    {
                        // Legacy SwiftClosureData escaping path: SwiftClosureData.context stores
                        // an `_SBClosureCtx` box pointer (when the runtime dylib is packaged)
                        // so Swift's release of the closure deinits the box and frees the
                        // wrapped GCHandle. The trampoline must unbox to recover the GCHandle
                        // (useBoxedContext from the shared gate above).
                        ClosureEmitter.EmitClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, useCdecl);
                        ClosureEmitter.EmitEscapingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.EmissionSymbol, useCdecl, useBoxedContext);
                    }
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Emits a static helper method for the @_cdecl invoke thunk of a closure return type.
        /// The helper wraps the delegate* unmanaged[Cdecl] call in a regular static method so that
        /// the lambda body only makes a managed call (Mono JIT crashes with !ji->async assertion
        /// when native indirect calls are made directly from lambda/delegate bodies).
        /// </summary>
        private void EmitClosureReturnInvokeThunkHelper(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper) return;

            // Check if method returns a closure (or optional closure)
            var returnArg = _env.MethodDecl.CSSignature.First();
            ClosureTypeSpec? closureTypeSpec = null;

            if (_env.ClosureHandler.IsClosure(returnArg))
                closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg);
            else if (_env.ClosureHandler.IsOptionalClosure(returnArg.SwiftTypeSpec))
                closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg);

            if (closureTypeSpec == null) return;
            if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec)) return;
            if (!ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, _env.ClosureHandler)) return;

            var helperName = ClosureEmitter.GetInvokeThunkHelperName(_env.EmissionSymbol);
            var entryPoint = ClosureEmitter.GetInvokeThunkEntryPoint(_env.EmissionSymbol);
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var libPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            ClosureEmitter.EmitCSharpInvokeThunkHelper(csWriter, closureTypeSpec, _env.ClosureHandler,
                helperName, entryPoint, libPath);
        }

        /// <summary>
        /// Gets the convention-c callback field name for a parameter.
        /// </summary>
        private static string GetConventionCCallbackName(string methodName, string paramName) =>
            $"_convC_{methodName}_{NameProvider.StripVerbatimPrefix(paramName)}";

        /// <summary>
        /// True for a non-optional <c>@convention(c)</c> closure parameter, which marshals through the
        /// per-method+param <c>[ThreadStatic]</c> delegate slot (<c>{base}_del</c>) +
        /// <c>[UnmanagedCallersOnly]</c> thunk rather than <c>Marshal.GetFunctionPointerForDelegate</c>.
        /// That is the AOT-safe path (no JIT trampoline) the iOS simulator requires. The optional
        /// <c>@convention(c)</c> arm uses a real function pointer and does NOT touch the slot, so it is
        /// excluded here. The slot sites that MUST gate on this same predicate, or they drift out of
        /// sync (a slot written but never declared/restored, or a save with no matching set): set
        /// (<see cref="EmitClosureMarshalling"/>) and thunk/field
        /// (<see cref="EmitConventionCCallback"/>) on every emission path, plus the
        /// save/restore pair (save: <see cref="EmitConventionCSlotSaveDeclarations"/>; restore: the
        /// finally cleanup) on the METHOD path only. Constructors that take a non-optional
        /// <c>@convention(c)</c> closure are skipped upstream as unsupported and never emit a slot, so
        /// they carry neither save nor restore.
        /// </summary>
        private bool UsesConventionCThreadStaticSlot(ArgumentDecl argumentDecl, ClosureTypeSpec closureTypeSpec, int closureParamCount) =>
            _env.ClosureHandler.IsConventionC(closureTypeSpec, _env.EmissionSymbol, closureParamCount)
            && !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

        /// <summary>
        /// Emits the pre-try save local <c>var {base}_delSaved = {base}_del;</c> for each non-optional
        /// <c>@convention(c)</c> closure parameter. Declared BEFORE the try block (like the existential
        /// heap declarations) so it remains in scope in the trailing finally, where the slot is restored.
        /// Capturing the slot's prior occupant and restoring it — rather than clearing to null — keeps the
        /// single <c>[ThreadStatic]</c> slot reentrancy-safe: a callback that synchronously re-enters the
        /// same method+param saves the outer delegate, installs its own, then restores the outer on the way
        /// out, so the outer call's later invocations still observe the correct delegate. Restoring (vs.
        /// leaving the slot written) also releases the captured reference, closing the leak.
        /// </summary>
        private void EmitConventionCSlotSaveDeclarations(CSharpWriter csWriter, bool needsTryFinally)
        {
            // The save local is read only by the finally restore. With no finally there is nothing to
            // restore into, so emitting the local would leave it unused (and the slot un-restored
            // anyway). A non-optional @convention(c) closure forces needsTryFinally everywhere it can
            // be restored; the lone exception is an async instance method (cleanup deferred to the async
            // callback, no finally), which keeps its pre-existing un-restored slot behaviour.
            if (!needsTryFinally)
                return;

            var closureParamCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;
                if (!UsesConventionCThreadStaticSlot(argumentDecl, closureTypeSpec, closureParamCount))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);
                csWriter.WriteLine($"var {baseName}_delSaved = {baseName}_del;");
            }
        }

        /// <summary>
        /// Emits a [ThreadStatic] delegate field, [UnmanagedCallersOnly(CallConvCdecl)] callback,
        /// and function pointer field for an @convention(c) closure parameter.
        /// This replaces Marshal.GetFunctionPointerForDelegate which requires JIT on iOS AOT/Mono.
        ///
        /// Thread safety + reentrancy: the slot is [ThreadStatic], so concurrent calls on different
        /// threads never share it. Within a thread the slot CAN be overwritten by a synchronous
        /// reentrant call (the invoked closure calls back into the same method+parameter before the
        /// outer call returns), so the slot is wrapped in a save/restore discipline at each call site:
        /// a local captures the prior occupant before the try, setup installs this call's delegate,
        /// and the finally restores the prior occupant (see EmitConventionCSlotSaveDeclarations and the
        /// restore in EmitSafeHandleRelease). A genuine call-after-return escape — Swift invoking the
        /// pointer after the method has returned — is not supportable through a thread-static slot and
        /// is left unsupported by design; the ABI demangler conservatively marks every @convention(c)
        /// closure escaping, so it cannot be distinguished from a synchronous one here.
        /// </summary>
        private void EmitConventionCCallback(CSharpWriter csWriter, ArgumentDecl argumentDecl, ClosureTypeSpec closureTypeSpec)
        {
            var csName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(argumentDecl));
            var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);
            var delegateType = _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);

            // Build parameter list and return type using ClosureHandler's type translation
            var closureReturn = closureTypeSpec.ReturnType;
            bool returnsVoid = closureReturn.IsEmptyTuple;
            bool returnsBool = !returnsVoid && MarshallingHelpers.IsBoolType(closureReturn);

            var paramDecls = new List<string>();
            var paramCalls = new List<string>();
            int argIdx = 0;
            foreach (var elem in closureTypeSpec.EachArgument())
            {
                var pinvokeType = _env.ClosureHandler.TranslateTypeSpecToPInvokeType(elem);
                paramDecls.Add($"{pinvokeType} arg{argIdx}");
                // The callback parameter carries the P/Invoke type (byte for Bool, the underlying
                // integer for a simple enum) but the delegate declares the idiomatic C# type
                // (bool / the enum). Forwarding the raw arg produces CS1503. Convert here to match
                // GetCSharpDelegateType's idiomatic projection — same bridge GetInvokeArgExpression
                // performs for the escaping/cdecl callbacks.
                if (MarshallingHelpers.IsBoolType(elem))
                {
                    paramCalls.Add($"arg{argIdx} != 0");
                }
                else if (_env.ClosureHandler.IsSimpleEnum(elem))
                {
                    var enumType = _env.ClosureHandler.TranslateTypeSpecToCSharp(elem);
                    paramCalls.Add($"({enumType})arg{argIdx}");
                }
                else
                {
                    paramCalls.Add($"arg{argIdx}");
                }
                argIdx++;
            }

            var callbackReturnType = returnsVoid ? "void" : (returnsBool ? "byte" : _env.ClosureHandler.TranslateTypeSpecToPInvokeType(closureReturn));
            var callbackParams = string.Join(", ", paramDecls);
            var callArgs = string.Join(", ", paramCalls);

            // Build the Cdecl function pointer type (not Swift calling convention)
            var cdeclFuncPtrType = paramDecls.Count == 0
                ? $"delegate* unmanaged[Cdecl]<{callbackReturnType}>"
                : $"delegate* unmanaged[Cdecl]<{string.Join(", ", paramDecls.Select(p => p.Split(' ')[0]))}, {callbackReturnType}>";

            // Emit [ThreadStatic] delegate storage
            csWriter.WriteLine($"[ThreadStatic] private static {delegateType}? {baseName}_del;");
            csWriter.WriteLine();

            // Emit [UnmanagedCallersOnly] callback.
            // @convention(c) closures are non-throwing and have no error channel back to Swift,
            // and this callback is invoked synchronously from native Swift. A managed exception
            // unwinding out of the UnmanagedCallersOnly boundary into native frames aborts the
            // process (SIGABRT). Wrap the body so an unhandled exception becomes a controlled
            // FailFast instead. FailFast is [DoesNotReturn], but C#'s end-point-reachability
            // analysis (CS0161) does NOT honor [DoesNotReturn], so the catch needs a definite
            // terminator on value-returning callbacks; the trailing `throw;` provides one
            // (unreachable at runtime, type-agnostic for void and value-returning shapes).
            csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"private static unsafe {callbackReturnType} {baseName}_impl({callbackParams})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (returnsVoid)
            {
                csWriter.WriteLine($"{baseName}_del!({callArgs});");
            }
            else if (returnsBool)
            {
                csWriter.WriteLine($"return (byte)({baseName}_del!({callArgs}) ? 1 : 0);");
            }
            else
            {
                csWriter.WriteLine($"return ({callbackReturnType}){baseName}_del!({callArgs});");
            }
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch (global::System.Exception __ex)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("SwiftClosureMarshaller.FailFastUnhandledClosureException(__ex);");
            csWriter.WriteLine("throw;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Emit function pointer field
            csWriter.WriteLine($"private static unsafe readonly {cdeclFuncPtrType} {baseName}_ptr = &{baseName}_impl;");
        }

        /// <summary>
        /// Emits the SafeHandle add reference.
        /// Frozen structs are passed as lowered buffers, so explicit retain is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy. InitWithCopy is called to create a copy.
        /// </summary>
        private void EmitSafeHandleAddRef(CSharpWriter csWriter)
        {
            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        // Async struct instance methods: the DeferredSafeHandleRelease holder owns
                        // the +1 refcount end-to-end (its ctor calls DangerousAddRef and the cleanup
                        // loop calls DangerousRelease). EmitSafeHandleRelease returns early for
                        // async, so a separate pre-call AddRef would never be released — pinning
                        // the SafeHandle open forever. Skip it; the holder is the sole live +1.
                        if (!_env.MethodDecl.IsAsync)
                        {
                            csWriter.WriteLine($"var success = false;");
                            csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                        }
                    }
                }
                else if (_env.ParentDecl is ClassDecl classParent)
                {
                    if (!classParent.IsObjCRooted)
                    {
                        // For async class instance methods, skip DangerousAddRef — the async holder
                        // already contains (object)this (preventing GC) and RetainedSelfPtr with
                        // Arc.Retain (keeping the Swift object alive). EmitSafeHandleRelease returns
                        // early for async methods, so this AddRef would leak permanently.
                        if (!_env.MethodDecl.IsAsync)
                        {
                            // Swift classes use SwiftClassHandle — still need DangerousAddRef/Release
                            // to prevent SafeHandle closure during P/Invoke
                            csWriter.WriteLine($"var success = false;");
                            csWriter.WriteLine($"_handle.DangerousAddRef(ref success);");
                        }
                    }
                    // ObjC-rooted: no SafeHandle — lifecycle managed by NSObject via ARC
                }
                else if (_env.ParentDecl is EnumDecl)
                {
                    // Non-simple enums use _payload SafeHandle like classes.
                    // Same async constraint as struct: DeferredSafeHandleRelease owns the +1
                    // end-to-end on async paths; a pre-call AddRef would leak.
                    if (!_env.MethodDecl.IsAsync)
                    {
                        csWriter.WriteLine($"var success = false;");
                        csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                    }
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a => !a.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(a) && !_env.ClosureHandler.IsClosure(a) && !_env.TupleHandler.IsTuple(a) && !_env.ExistentialHandler.IsExistential(a) && (_env.MethodDecl.IsAccessor || !MarshallingHelpers.IsConvertibleType(a.SwiftTypeSpec))))
            {
                // @_cdecl property wrapper: String params are passed as (IntPtr, int) directly.
                // Skip PayloadBuffer extraction — there's no SwiftString to extract from.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    argumentDecl.SwiftTypeSpec is NamedTypeSpec strArgNts && strArgNts.Name == "Swift.String")
                    continue;

                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                // ObjC bridged/rooted types: extract Handle from .NET iOS binding object.
                // ObjC-rooted classes (same-module Swift classes inheriting NSObject) use .Handle
                // instead of .Payload, just like Apple framework ObjC-bridged types.
                // ObjC-bridgeable value types (URL) use the same pattern for accessors only —
                // non-accessor methods are handled by EmitTypeConversions via projection plan.
                if (MarshallingHelpers.IsObjCBridged(typeRecord) ||
                    MarshallingHelpers.IsObjCRooted(typeRecord) ||
                    (_env.MethodDecl.IsAccessor && MarshallingHelpers.IsObjCBridgeable(typeRecord)))
                {
                    csWriter.WriteLine($"IntPtr {csName}Handle = {csName}?.Handle ?? IntPtr.Zero;");
                    continue;
                }

                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($"using PayloadBuffer<{typeRecord.CSharpTypeName}.Buffer> {csName}Disposable = {csName}.PayloadBuffer;");
                }
            }

            // @_cdecl tuple params: create a buffer with elements at ABI offsets.
            // ValueTuple has StructLayout.Auto which is incompatible with P/Invoke marshalling.
            // Allocate via tuple metadata, write elements at GetElementOffset positions, pass IntPtr.
            if (_env.MethodDecl.UsesCdeclWrapper)
            {
                foreach (var arg in _env.MethodDecl.CSSignature.Skip(1)
                    .Where(a => _env.TupleHandler.IsTuple(a)))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(arg)!;
                    // Only emit buffer marshalling for tuples every element of which has a fixed-size,
                    // ABI-faithful slot representation: blittable primitives (written by value) and pure
                    // Swift class elements (written as their object handle). Other element kinds need
                    // per-element marshalling that doesn't exist yet — they fail closed at the validator.
                    if (!_env.TupleHandler.IsCdeclBufferMarshallableTuple(tupleTypeSpec))
                        continue;
                    var csName = NameProvider.GetCSharpParameterName(arg);
                    var elements = tupleTypeSpec.Elements;

                    // Build metadata accessor calls for each element type. For a class element the
                    // translated C# type is its ISwiftObject wrapper, whose GetTypeMetadataOrThrow<T>()
                    // resolves the Swift class metadata — so the tuple metadata sizes that slot as a
                    // single pointer (8 bytes) and GetElementOffset(i) is the ABI-correct slot offset.
                    // A Swift.String element projects to C# `string` (no Swift metadata accessor), so it
                    // is sized via the SwiftString runtime metadata (a 16-byte / two-word value slot).
                    var metadataArgs = new List<string>();
                    for (int i = 0; i < elements.Count; i++)
                    {
                        if (MarshallingHelpers.IsSwiftString(elements[i]))
                        {
                            metadataArgs.Add("TypeMetadata.GetTypeMetadataOrThrow<global::Swift.SwiftString>()");
                            continue;
                        }
                        if (_env.TupleHandler.IsCompositionExistentialElement(elements[i]))
                        {
                            // Composition existential (any P & Q, EC2+): the tuple slot is an opaque
                            // existential container whose stride is determined by the non-marker protocol
                            // count (EC2 = 48 bytes / 6 words). GetExistentialTypeMetadata(count) sizes it.
                            var protocolCount = _env.TupleHandler.GetCompositionExistentialElementProtocolCount(elements[i]);
                            metadataArgs.Add($"TypeMetadata.GetExistentialTypeMetadata({protocolCount})");
                            continue;
                        }
                        var elemType = _env.TupleHandler.TranslateElementTypeToCSharp(elements[i]);
                        metadataArgs.Add($"TypeMetadata.GetTypeMetadataOrThrow<{elemType}>()");
                    }

                    csWriter.WriteLine($"var {csName}TupleMeta = TypeMetadata.GetTupleTypeMetadataFromElements(");
                    csWriter.WriteLine($"    {string.Join(", ", metadataArgs)});");
                    csWriter.WriteLine($"byte* {csName}Buf = stackalloc byte[(int){csName}TupleMeta.Size];");
                    for (int i = 0; i < elements.Count; i++)
                    {
                        var itemAccess = $"{csName}.Item{i + 1}";
                        var slotExpr = $"{csName}Buf + (int){csName}TupleMeta.AsTupleMetadata()->GetElementOffset({i})";
                        if (CdeclParamMapper.IsCdeclPrimitive(elements[i]))
                        {
                            // Primitive scalar: the C# value is byte-for-byte the slot, written by value.
                            csWriter.WriteLine($"System.Runtime.CompilerServices.Unsafe.Write({slotExpr}, {itemAccess});");
                        }
                        else if (MarshallingHelpers.IsSwiftString(elements[i]))
                        {
                            // Swift.String element: the tuple slot is a 16-byte (two-word) value, NOT a
                            // pointer. The element is already projected as a Swift.SwiftString that owns
                            // its 16-byte storage, so bit-copy that borrowed value (Read<Buffer> through
                            // the payload handle) straight into the slot — no fresh materialization, no
                            // UTF-8 round-trip. The copy is a +0 alias of the source element's storage:
                            // the owning ValueTuple is GC.KeepAlive'd past the call (the same source
                            // keep-alive the class slot relies on) so the SwiftString's SafeHandle cannot
                            // finalize and release the value mid-call, and the Swift wrapper's typed
                            // `.pointee` load retains it for the call's duration.
                            csWriter.WriteLine($"System.Runtime.CompilerServices.Unsafe.Write({slotExpr}, System.Runtime.CompilerServices.Unsafe.Read<global::Swift.SwiftString.Buffer>((void*){itemAccess}.Payload.DangerousGetHandle()));");
                        }
                        else if (_env.TupleHandler.IsCompositionExistentialElement(elements[i]))
                        {
                            // Composition existential element (any P & Q, EC2+): project the element to its
                            // ExistentialContainerN via ISwiftExistentialConvertible.GetExistentialContainer()
                            // — for a composition (≥2 protocols) this is ALWAYS a borrowed (+0) container
                            // (owns=false, keep-alive = the source). CopyTo bit-copies the container struct
                            // into the slot (`*(ECN*)slot = container`). The owning ValueTuple is
                            // GC.KeepAlive'd past the call (see EmitTupleParamKeepAlive) so the borrowed
                            // payload/witness-tables survive; the stackalloc buffer IS the transport, and
                            // because the container is +0 there is no per-element teardown to do.
                            var containerVar = $"{csName}Container{i}";
                            var conversion = _env.TupleHandler.GetCompositionExistentialElementConversion(elements[i], itemAccess);
                            csWriter.WriteLine($"var {containerVar} = {conversion};");
                            csWriter.WriteLine($"{containerVar}.CopyTo((IntPtr)({slotExpr}));");
                        }
                        else
                        {
                            // Class element: write the raw object handle (IntPtr) into the pointer-width
                            // slot. The handle is borrowed (+0) — the owning ValueTuple is GC.KeepAlive'd
                            // past the native call (see EmitTupleParamKeepAlive) so the SafeHandle backing
                            // it cannot be finalized and release the Swift object mid-call.
                            csWriter.WriteLine($"System.Runtime.CompilerServices.Unsafe.Write({slotExpr}, {itemAccess}.Payload.DangerousGetHandle());");
                        }
                    }
                    csWriter.WriteLine($"var {csName}Ptr = (IntPtr){csName}Buf;");
                }
            }

            // NOTE: For async methods, non-frozen parameter copy buffers are created in EmitAsync
            // (before the GCHandle holder) using InitializeWithCopy. The {param}Handle and
            // {param}CopyBuffer variables are already declared there. Nothing more to do here.
        }

        /// <summary>
        /// Emits the SafeHandle release.
        /// Frozen structs are passed as lowered buffers, so explicit release is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy; Destroy is called on the copy.
        ///
        /// For async instance methods, DangerousRelease is deferred until the async callback fires.
        /// This prevents the SafeHandle from being released while the Swift async Task is still running.
        /// </summary>
        private void EmitSafeHandleRelease(CSharpWriter csWriter)
        {
            // For async instance methods, skip immediate release - the callback will handle it
            // via DeferredSafeHandleRelease stored in the async holder
            if (_env.MethodDecl.IsAsync && _env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                // Async instance methods defer release to callback
                return;
            }

            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"if (success)");
                        csWriter.WriteLine($"   _payload.DangerousRelease();");
                    }
                }
                else if (_env.ParentDecl is ClassDecl classParentRel)
                {
                    if (!classParentRel.IsObjCRooted)
                    {
                        // Swift classes use SwiftClassHandle
                        csWriter.WriteLine($"if (success)");
                        csWriter.WriteLine($"   _handle.DangerousRelease();");
                    }
                    // ObjC-rooted: no SafeHandle release — NSObject manages lifecycle
                }
                else if (_env.ParentDecl is EnumDecl)
                {
                    // Non-simple enums use _payload SafeHandle like classes
                    csWriter.WriteLine($"if (success)");
                    csWriter.WriteLine($"   _payload.DangerousRelease();");
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                if (argumentDecl.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argumentDecl.SwiftTypeSpec.ToString()].TypeParameter;
                    var metadataName = NameProvider.GetMetadataName(csTypeParamName);
                    var payloadName = NameProvider.GetPayloadName(csName);
                    csWriter.WriteLine($"{metadataName}.ValueWitnessTable->Destroy((void *){payloadName}, {metadataName});");
                    continue;
                }

                // Free GCHandle for non-escaping closures (callback fires synchronously).
                // For cdecl-wrapped escaping closures, Swift's `_SBClosureCtx` ARC box
                // owns the GCHandle once the P/Invoke returns: free here only if the
                // call never reached the Swift wrapper body (transfer flag still false).
                // Async+throwing closures still intentionally leak on this path — see
                // AsyncClosureHelper.RunAsync. Legacy SwiftClosureData escaping closures
                // (non-cdecl path) also leak, since no `_SBClosureCtx` is constructed.
                // Optional closures in Swift are always escaping by definition (no
                // @noescape Optional<Closure> exists), but the inner ClosureTypeSpec may
                // not have the escaping attribute because the ABI parser only propagates
                // it to top-level closure nodes, not those inside Optional wrappers.
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    var cleanupClosureCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);
                    bool isEffectivelyEscaping = WrapperValidation.IsEffectivelyEscaping(
                        closureTypeSpec, argumentDecl.SwiftTypeSpec, _env.ClosureHandler);

                    // Restore the @convention(c) [ThreadStatic] slot to the occupant captured before
                    // the call (EmitConventionCSlotSaveDeclarations). Restoring rather than clearing to
                    // null is what makes the single slot reentrancy-safe: an outer call whose callback
                    // synchronously re-entered the same method+param gets its delegate back, so its later
                    // invocations still dispatch correctly; at the outermost frame the saved value is the
                    // slot's pre-call null, so the reference is also released (no leak). Gated on the same
                    // UsesConventionCThreadStaticSlot predicate as the save/set/thunk sites.
                    if (UsesConventionCThreadStaticSlot(argumentDecl, closureTypeSpec, cleanupClosureCount))
                    {
                        var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);
                        csWriter.WriteLine($"{baseName}_del = {baseName}_delSaved;");
                    }
                    else if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, cleanupClosureCount) &&
                        !_env.ClosureHandler.IsAsyncClosure(closureTypeSpec) &&
                        !isEffectivelyEscaping)
                    {
                        csWriter.WriteLine($"if ({csName}Handle.IsAllocated) {csName}Handle.Free();");
                    }
                    else if (_env.MethodDecl.HasCdeclClosureMarshalling &&
                        _env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, cleanupClosureCount) &&
                        !_env.ClosureHandler.IsAsyncClosure(closureTypeSpec) &&
                        isEffectivelyEscaping)
                    {
                        // Cdecl escaping closure: Swift owns the GCHandle once the wrapper
                        // body ran. If we never got there (Transferred still false), free
                        // here to close the alloc-but-no-call leak window.
                        csWriter.WriteLine($"if (!{csName}Transferred && {csName}Handle.IsAllocated) {csName}Handle.Free();");
                    }
                    else if (!_env.MethodDecl.HasCdeclClosureMarshalling &&
                        _env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.EmissionSymbol, cleanupClosureCount) &&
                        !_env.ClosureHandler.IsAsyncClosure(closureTypeSpec) &&
                        isEffectivelyEscaping)
                    {
                        // Legacy SwiftClosureData escaping closure: Swift's release of
                        // the closure deinits the `_SBClosureCtx` box and frees the
                        // GCHandle exactly once (when {csName}Box is non-zero). If we
                        // never reached the P/Invoke body (Transferred still false), we
                        // must release the box ourselves (which fires the deinit) and
                        // free the GCHandle directly when no box was allocated.
                        csWriter.WriteLines($$"""
                            if (!{{csName}}Transferred)
                            {
                                if ({{csName}}Box != IntPtr.Zero)
                                {
                                    SwiftClosureMarshaller.ReleaseBoxedContext({{csName}}Box);
                                }
                                else if ({{csName}}Handle.IsAllocated)
                                {
                                    {{csName}}Handle.Free();
                                }
                            }
                            """);
                    }
                }
            }

            // NOTE: Async non-frozen parameters are NOT released here.
            // They are kept alive by the GCHandle (in the typed holder's KeepAlives/CopyBuffers) until the callback fires.
            // This prevents SIGSEGV crashes caused by GC finalizing the parameter while Swift's
            // async Task is still pending and may access copy-on-write shared storage.
        }

        /// <summary>
        /// Emits the generic arguments setup.
        /// </summary>
        private void EmitGenericArguments(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.GenericArgumentMarshallingLines)
                csWriter.WriteLine(line);
            csWriter.WriteLine();
        }

        /// <summary>
        /// After a P/Invoke call, writes back modified generic inout payloads to the caller's ref parameters.
        /// Without this, mutations made by Swift to ref generic parameters would be lost.
        /// </summary>
        private void EmitGenericInoutWriteback(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.GenericInoutWritebackLines)
                csWriter.WriteLine(line);
            // Blittable frozen-struct inout readbacks, collected when their stack buffer was emitted.
            foreach (var line in _cdeclFrozenStructInoutWritebacks)
                csWriter.WriteLine(line);
        }

        private void EmitProtocolWitnessTables(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.WitnessTableStatements)
                csWriter.WriteLine(line);
            csWriter.WriteLine();
        }
    }
}
