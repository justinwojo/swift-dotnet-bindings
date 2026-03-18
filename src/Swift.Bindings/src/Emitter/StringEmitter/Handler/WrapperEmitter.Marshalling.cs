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
                        opaqueDerefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(p, p.Name, p.Name));
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
                    return p.Name switch
                    {
                        var n when n.StartsWith("arg") => valueRef,
                        var n when n.StartsWith("_") => $"{n.Substring(1)}: {valueRef}",
                        _ => $"{p.Name}: {valueRef}"
                    };
                }));

            var genericParams = _env.MethodDecl.IsGeneric
                ? $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>"
                : "";

            var whereClause = (_env.MethodDecl.IsGeneric && _env.MethodDecl.GenericParameters.Any(p => p.GenericConformances.Any() || p.AssosiatedTypeConformances.Any()))
                ? " where " + string.Join(", ", _env.MethodDecl.GenericParameters.Select(p =>
                {
                    var genericConformances = p.GenericConformances
                        .Select(gc => $"{p.SugaredTypeName} : {gc.ConformanceTarget.Name}");
                    var typeConformances = p.AssosiatedTypeConformances
                        .Select(tc => $"{p.SugaredTypeName}.{string.Join(".", tc.Path.Skip(1))} == {tc.ConformanceTarget.Name}");
                    return string.Join(", ", genericConformances.Concat(typeConformances));
                }))
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
            extension {{parentTypeName.ModuleQualifiedName}} {
                {{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
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
            extension {{parentTypeName.ModuleQualifiedName}} {
                {{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
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
                swiftWriter.WriteLine($$"""
            {{mainActorAttr}}@_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
                {{freeDerefCode}}return {{(moduleName.Length > 0 ? moduleName + "." : "")}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
            }
            """);
            }
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
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = true, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                    if (projection != null)
                        continue;
                }

                // Skip Optional<existential> — handled by dedicated existential marshalling path
                if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec))
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
                            if (MethodWrapperEmitter.IsOptionalWithReferenceInner(argumentDecl.SwiftTypeSpec, _env.TypeDatabase))
                            {
                                csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                                csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                            }
                            else
                            {
                                csWriter.WriteLine($"IntPtr {bufferName} = {csName}.Payload.DangerousGetHandle();");
                            }
                        }
                        // Large optional accessor params (e.g., Optional<SwiftString>) have payloads
                        // exceeding IntPtr size. PayloadBuffer<IntPtr> would truncate — use the full
                        // buffer via DangerousGetHandle instead.
                        else if (_env.MethodDecl.IsAccessor && _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec))
                        {
                            csWriter.WriteLine($"IntPtr {bufferName} = {csName}.Payload.DangerousGetHandle();");
                        }
                        else
                        {
                            csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                            csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                        }
                    }
                    else
                    {
                        // Non-frozen type: use handle-based marshalling
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}.Payload.DangerousGetHandle();");
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

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount))
                {
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    if (closureTypeSpec.IsEscaping)
                    {
                        // Escaping @convention(c) closures may be stored and called later on any thread.
                        // ThreadStatic is unsound for this case — fall back to Marshal.GetFunctionPointerForDelegate.
                        // This works on NativeAOT (device); on Mono AOT (simulator) it requires JIT trampolines
                        // which may not be available. Escaping @convention(c) closures are rare in practice.
                        //
                        // Bool bridge: Marshal.GetFunctionPointerForDelegate creates a native thunk where bool
                        // maps to 4-byte BOOL, but Swift's @convention(c) Bool is 1 byte. Generate a wrapper
                        // delegate with byte types so the thunk matches Swift's ABI.
                        string marshalSource = csName;
                        if (ClosureEmitter.NeedsConventionCBoolBridge(closureTypeSpec))
                        {
                            var bridgeName = $"_{csName}_boolBridge";
                            ClosureEmitter.EmitConventionCBoolBridge(csWriter, csName, bridgeName, closureTypeSpec, _env.ClosureHandler);
                            marshalSource = bridgeName;
                        }

                        if (isOptional)
                        {
                            csWriter.WriteLines($"""
                                var {csName}FuncPtr = {csName} != null
                                    ? ({funcPtrType})Marshal.GetFunctionPointerForDelegate({marshalSource})
                                    : ({funcPtrType})IntPtr.Zero;
                                """);
                        }
                        else
                        {
                            csWriter.WriteLine($"var {csName}FuncPtr = ({funcPtrType})Marshal.GetFunctionPointerForDelegate({marshalSource});");
                        }
                    }
                    else
                    {
                        // Non-escaping @convention(c) closures are called synchronously during the P/Invoke
                        // and cannot be stored by Swift. Use [UnmanagedCallersOnly] callback + [ThreadStatic]
                        // delegate to avoid Marshal.GetFunctionPointerForDelegate which requires JIT on Mono.
                        var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);

                        if (isOptional)
                        {
                            csWriter.WriteLines($"""
                                {baseName}_del = {csName};
                                var {csName}FuncPtr = {csName} != null
                                    ? ({funcPtrType}){baseName}_ptr
                                    : ({funcPtrType})IntPtr.Zero;
                                """);
                        }
                        else
                        {
                            csWriter.WriteLine($"{baseName}_del = {csName};");
                            csWriter.WriteLine($"var {csName}FuncPtr = ({funcPtrType}){baseName}_ptr;");
                        }
                    }
                }
                else if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                {
                    // Async+throwing closures use a special pattern with AsyncThrowingClosureState
                    // The state holds the user's async delegate, and we pass context + start function to Swift
                    ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
                        csWriter,
                        _env.MethodDecl.Name,
                        csName,
                        closureTypeSpec,
                        _env.ClosureHandler,
                        _env.MethodDecl.MangledName);
                }
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount))
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
                        // Legacy SwiftClosureData path (for async methods with non-async closures)
                        var callbackName = ClosureHandler.GetCallbackFunctionName(_env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);

                        if (isOptional)
                        {
                            // Optional escaping closure - handle null case with zero-initialized SwiftClosureData
                            csWriter.WriteLine($"SwiftClosureData {csName}Closure;");
                            csWriter.WriteLine($"if ({csName} != null)");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                            csWriter.WriteLine($"{csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({csName}Handle));");
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
                            csWriter.WriteLine("else");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Closure = default; // Zero-initialized = nil in Swift");
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
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
            if (_env.MethodDecl.IsAccessor)
            {
                foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
                {
                    if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                        !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
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
        /// Tries to emit parameter conversion via the projection factory.
        /// Returns true if the projection handled the parameter, false if fallback is needed.
        /// </summary>
        private bool TryEmitParameterConversionViaProjection(CSharpWriter csWriter, ArgumentDecl argumentDecl)
        {
            var projection = s_projectionFactory.Project(argumentDecl.SwiftTypeSpec,
                new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = true, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
            if (projection == null)
                return false;

            var csName = NameProvider.GetCSharpParameterName(argumentDecl);

            // B12: ObjC optional inner — extract Handle directly instead of using projection
            if (projection is OptionalProjection optProj)
            {
                var optNamed = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                var innerElement = optNamed?.GenericParameters.FirstOrDefault();
                if (innerElement is NamedTypeSpec innerNamed && innerNamed.HasModule() &&
                    TypeDatabaseExtensions.IsObjCModuleType(innerNamed))
                {
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                    csWriter.WriteLine($"IntPtr {bufferName} = {csName}?.Handle ?? IntPtr.Zero;");
                    return true;
                }
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
                    !MethodWrapperEmitter.IsOptionalWithReferenceInner(argumentDecl.SwiftTypeSpec, _env.TypeDatabase);

                if (needsLargeOptOverride || needsCdeclOptOverride)
                {
                    projection = new OptionalProjection(optProjForHandle.InnerProjection, optProjForHandle.IsExistentialInner, useDangerousGetHandle: true);
                }
            }

            var plan = projection.GetParameterPlan(csName);

            // @_cdecl wrapper: collection params (Array, Dictionary, Set) pass a pointer to the
            // container value via UnsafeRawPointer. Swift does .load(as: T.self) from the pointer.
            // The projection's plan uses PayloadBuffer<IntPtr>.Buffer which DEREFERENCES the buffer
            // (extracts the value, not a pointer to it). For @_cdecl, use .Payload.DangerousGetHandle()
            // which gives the pointer TO the value — matching what .load(as:) expects.
            if (_env.MethodDecl.UsesCdeclWrapper &&
                (projection is ArrayProjection or DictionaryProjection or SetProjection))
            {
                var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                // Render only the container creation setup (Using SwiftArray/Dict/Set),
                // then pass the handle directly instead of dereferencing via PayloadBuffer.
                foreach (var stmt in plan.SetupStatements)
                {
                    // Skip PayloadBuffer and .Buffer lines — replace with DangerousGetHandle
                    if (stmt is MarshalStatement.Using u && u.Type.StartsWith("PayloadBuffer"))
                        continue;
                    if (stmt is MarshalStatement.Line l && l.Code.Contains("Disposable.Buffer"))
                        continue;
                    MarshalPlanRenderer.RenderStatement(csWriter, stmt);
                }
                csWriter.WriteLine($"IntPtr {bufferName} = {csName}Swift.Payload.DangerousGetHandle();");
                return true;
            }

            MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);
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
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
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
                else
                {
                    // Blittable frozen struct: allocate a stack buffer, marshal the struct into it.
                    csWriter.WriteLines($"""
                        var {csName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csTypeName}>();
                        byte* {csName}Buffer = stackalloc byte[(int){csName}Metadata.Size];
                        var {csName}Span = new Span<byte>({csName}Buffer, (int){csName}Metadata.Size);
                        SwiftMarshal.MarshalToSwift({csName}, ref {csName}Span);
                        IntPtr {csName}Ptr = (IntPtr){csName}Buffer;
                        """);
                }
            }
        }

        /// <summary>
        /// Emits P/Invoke declarations for SBW_GetErrorDescription, SBW_ReleaseError, and SBW_Free
        /// (if not already emitted by Utf8SliceEmitter). These are class-level member declarations
        /// emitted before the method signature, deduped per C# type.
        /// </summary>
        private void EmitErrorHelperPInvokes(CSharpWriter csWriter)
        {
            if (_syncPlan.SwiftError == null) return;

            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var isGenericType = _env.PInvokeHelperContext != null;

            // Base error P/Invokes (GetErrorDescription, ReleaseError, Free) — one per C# type
            if (!ErrorDescriptionEmitter.HasErrorPInvokeForType(typeKey, _emissionContext))
            {
                ErrorDescriptionEmitter.MarkErrorPInvokeEmittedForType(typeKey, _emissionContext);

                var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
                var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
                var descSymbol = ErrorDescriptionEmitter.GetDescriptionSymbolName(moduleDecl.Name);
                var releaseSymbol = ErrorDescriptionEmitter.GetReleaseSymbolName(moduleDecl.Name);
                var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);

                if (isGenericType)
                {
                    // CS7042: DllImport/LibraryImport cannot appear inside generic types.
                    // Collect into PInvokeHelperContext for emission in non-generic helper class.
                    _env.PInvokeHelperContext!.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = descSymbol,
                        MethodName = "SBW_GetErrorDescription",
                        ReturnType = "IntPtr",
                        ParametersString = "IntPtr error",
                        OmitCallingConvention = true,
                        UsePrivateVisibility = false,
                    });
                    _env.PInvokeHelperContext!.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = releaseSymbol,
                        MethodName = "SBW_ReleaseError",
                        ReturnType = "void",
                        ParametersString = "IntPtr error",
                        OmitCallingConvention = true,
                        UsePrivateVisibility = false,
                    });

                    if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, _emissionContext))
                    {
                        Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, _emissionContext);
                        _env.PInvokeHelperContext!.AddDeclaration(new PInvokeDeclaration
                        {
                            LibraryPath = wrapperLibPath,
                            EntryPoint = freeSymbol,
                            MethodName = "SBW_Free",
                            ReturnType = "void",
                            ParametersString = "IntPtr ptr",
                            OmitCallingConvention = true,
                            UsePrivateVisibility = false,
                        });
                    }
                }
                else
                {
                    csWriter.WriteLines($"""
                        [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{descSymbol}")]
                        private static partial IntPtr SBW_GetErrorDescription(IntPtr error);

                        [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{releaseSymbol}")]
                        private static partial void SBW_ReleaseError(IntPtr error);

                        """);

                    // Emit SBW_Free if not already emitted by Utf8SliceEmitter for this type
                    if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, _emissionContext))
                    {
                        Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, _emissionContext);
                        csWriter.WriteLines($"""
                            [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{freeSymbol}")]
                            private static partial void SBW_Free(IntPtr ptr);

                            """);
                    }
                }
            }

            // C2: Extractor P/Invoke — one per (C# type, Swift error type) pair.
            // Must be outside the base P/Invoke dedup block because multiple methods in the
            // same type can throw different error types (e.g., ParseError vs RangeError).
            if (_syncPlan.SwiftError is { IsTypedThrows: true, SwiftErrorTypeName: not null, TypedErrorSafeSuffix: not null })
            {
                var extractorKey = typeKey + ":extractor:" + _syncPlan.SwiftError.SwiftErrorTypeName;
                if (!ErrorDescriptionEmitter.HasExtractorPInvokeForType(extractorKey, _emissionContext))
                {
                    ErrorDescriptionEmitter.MarkExtractorPInvokeEmittedForType(extractorKey, _emissionContext);
                    var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
                    var extractorSymbol = ErrorDescriptionEmitter.GetExtractorSymbolName(
                        moduleDecl.Name, _syncPlan.SwiftError.SwiftErrorTypeName);

                    if (isGenericType)
                    {
                        _env.PInvokeHelperContext!.AddDeclaration(new PInvokeDeclaration
                        {
                            LibraryPath = wrapperLibPath,
                            EntryPoint = extractorSymbol,
                            MethodName = $"SBW_ExtractTypedError_{_syncPlan.SwiftError.TypedErrorSafeSuffix}",
                            ReturnType = "IntPtr",
                            ParametersString = "IntPtr error",
                            OmitCallingConvention = true,
                            UsePrivateVisibility = false,
                        });
                    }
                    else
                    {
                        csWriter.WriteLines($"""
                            [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{extractorSymbol}")]
                            private static partial IntPtr SBW_ExtractTypedError_{_syncPlan.SwiftError.TypedErrorSafeSuffix}(IntPtr error);

                            """);
                    }
                }
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

                // Non-escaping @convention(c) closures: emit [UnmanagedCallersOnly(CallConvCdecl)] callback +
                // [ThreadStatic] delegate storage. Replaces Marshal.GetFunctionPointerForDelegate which
                // requires JIT (crashes on iOS AOT/Mono). Escaping @convention(c) closures skip this —
                // they use Marshal.GetFunctionPointerForDelegate (works on NativeAOT, Mono limitation accepted).
                if (_env.ClosureHandler.IsConventionC(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount)
                    && !closureTypeSpec.IsEscaping)
                {
                    EmitConventionCCallback(csWriter, argumentDecl, closureTypeSpec);
                    csWriter.WriteLine();
                    continue;
                }

                if (_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount))
                {
                    // Check if this is an async+throwing closure (must check before throwing-only)
                    if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                    {
                        // Async+throwing closures use a special "start" callback pattern
                        // The start function is synchronous and spawns Task.Run
                        // These always use their own Cdecl pattern, not gated by useCdecl
                        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitAsyncThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    // Check if this is a throwing closure (but not async+throwing)
                    else if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        // Throwing closures need special callback that handles SwiftError
                        ClosureEmitter.EmitThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    // Check if this closure needs indirect return marshalling
                    else if (_env.ClosureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitIndirectReturnCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitIndirectReturnCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitEscapingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Gets the convention-c callback field name for a parameter.
        /// </summary>
        private static string GetConventionCCallbackName(string methodName, string paramName) =>
            $"_convC_{methodName}_{NameProvider.StripVerbatimPrefix(paramName)}";

        /// <summary>
        /// Emits a [ThreadStatic] delegate field, [UnmanagedCallersOnly(CallConvCdecl)] callback,
        /// and function pointer field for an @convention(c) closure parameter.
        /// This replaces Marshal.GetFunctionPointerForDelegate which requires JIT on iOS AOT/Mono.
        ///
        /// Thread safety: @convention(c) closures are non-escaping by Swift language definition —
        /// they carry no context and must be called synchronously during the function's execution.
        /// Swift cannot store them for later or cross-thread invocation. The [ThreadStatic] field
        /// is therefore safe: each thread has its own slot, and the closure is invoked and completed
        /// within the same P/Invoke call before the field could be overwritten.
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
                // @convention(c) closures use primitive types — P/Invoke type matches delegate type
                paramCalls.Add($"arg{argIdx}");
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

            // Emit [UnmanagedCallersOnly] callback
            csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"private static unsafe {callbackReturnType} {baseName}_impl({callbackParams})");
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
                        csWriter.WriteLine($"var success = false;");
                        csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                    }
                }
                else if (_env.ParentDecl is ClassDecl classParent)
                {
                    if (!classParent.IsObjCRooted)
                    {
                        // Swift classes use SwiftClassHandle — still need DangerousAddRef/Release
                        // to prevent SafeHandle closure during P/Invoke
                        csWriter.WriteLine($"var success = false;");
                        csWriter.WriteLine($"_handle.DangerousAddRef(ref success);");
                    }
                    // ObjC-rooted: no SafeHandle — lifecycle managed by NSObject via ARC
                }
                else if (_env.ParentDecl is EnumDecl)
                {
                    // Non-simple enums use _payload SafeHandle like classes
                    csWriter.WriteLine($"var success = false;");
                    csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
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
                if (MarshallingHelpers.IsObjCBridged(typeRecord) ||
                    MarshallingHelpers.IsObjCRooted(typeRecord))
                {
                    csWriter.WriteLine($"IntPtr {csName}Handle = {csName}?.Handle ?? IntPtr.Zero;");
                    continue;
                }

                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($"using PayloadBuffer<{typeRecord.CSharpTypeName}.Buffer> {csName}Disposable = {csName}.PayloadBuffer;");
                }
            }

            // @_cdecl existential params: extract container into local variable for ref passing
            if (_env.MethodDecl.UsesCdeclWrapper)
            {
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
                    if (containerType == "Swift.Runtime.ExistentialContainer1" && !_env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
                        csWriter.WriteLine($"var {csName}Container = Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({csName});");
                    else
                        csWriter.WriteLine($"var {csName}Container = ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){csName}).GetExistentialContainer();");
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

                // Free GCHandle for non-escaping closures only (callback fires synchronously).
                // Escaping closures are intentionally leaked: Swift may store the function pointer +
                // context beyond the P/Invoke return (e.g., EventHandler.onComplete stored for later
                // fire()). Freeing here would leave Swift with a stale GCHandle context.
                // The callback thunk also does NOT free — escaping closures may fire multiple times.
                // Async+throwing closures free their GCHandle inside Task.Run's finally block.
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    var cleanupClosureCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);

                    // Clear non-escaping @convention(c) ThreadStatic delegate to release references.
                    if (_env.ClosureHandler.IsConventionC(closureTypeSpec, _env.MethodDecl.MangledName, cleanupClosureCount)
                        && !closureTypeSpec.IsEscaping)
                    {
                        var baseName = GetConventionCCallbackName(_env.MethodDecl.Name, csName);
                        csWriter.WriteLine($"{baseName}_del = null;");
                    }
                    else if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, cleanupClosureCount) &&
                        !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec) &&
                        !closureTypeSpec.IsEscaping)
                    {
                        csWriter.WriteLine($"if ({csName}Handle.IsAllocated) {csName}Handle.Free();");
                    }
                }
            }

            // NOTE: Async non-frozen parameters are NOT released here.
            // They are kept alive by the GCHandle (in the object[] holder) until the callback fires.
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
        }

        private void EmitProtocolWitnessTables(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.WitnessTableStatements)
                csWriter.WriteLine(line);
            csWriter.WriteLine();
        }
    }
}
