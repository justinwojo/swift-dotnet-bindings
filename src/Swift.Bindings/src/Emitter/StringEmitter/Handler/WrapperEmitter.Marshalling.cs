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

            // Determine if wrapper needs @MainActor annotation
            bool needsMainActor = ((_env.ParentDecl as TypeDecl)?.IsMainActorIsolated == true
                || _env.MethodDecl.IsActorIsolated)
                && !_env.MethodDecl.IsNonisolated;
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
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = true, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl });
                    if (projection != null)
                        continue;
                }

                // Skip Optional<existential> — handled by dedicated existential marshalling path
                if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec))
                    continue;

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
                        csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
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
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                bool isOptional = _env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec))
                {
                    // For @convention(c) closures, convert delegate to function pointer
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    if (isOptional)
                    {
                        // Optional @convention(c) closure - handle null case
                        csWriter.WriteLines($"""
                            var {csName}FuncPtr = {csName} != null
                                ? ({funcPtrType})Marshal.GetFunctionPointerForDelegate({csName})
                                : ({funcPtrType})IntPtr.Zero;
                            """);
                    }
                    else
                    {
                        // Marshal.GetFunctionPointerForDelegate returns IntPtr, cast to the proper function pointer type
                        csWriter.WriteLine($"var {csName}FuncPtr = ({funcPtrType})Marshal.GetFunctionPointerForDelegate({csName});");
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
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    if (_env.MethodDecl.HasClosureCdeclWrapper)
                    {
                        // Cdecl wrapper: just allocate the GCHandle if closure is non-null.
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
                    {
                        TryEmitParameterConversionViaProjection(csWriter, argumentDecl);
                    }
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
                new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = true, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl });
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

            // Check if large Optional param needs DangerousGetHandle override
            if (projection is OptionalProjection optProjForHandle)
            {
                bool needsLargeOptOverride = _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec) &&
                    (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary ||
                     _env.MethodDecl.IsAsync || _requiresOpaqueReturnWrapper);
                if (needsLargeOptOverride)
                {
                    projection = new OptionalProjection(optProjForHandle.InnerProjection, optProjForHandle.IsExistentialInner, useDangerousGetHandle: true);
                }
            }

            var plan = projection.GetParameterPlan(csName);
            MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);
            return true;
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
                        [System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{descSymbol}")]
                        private static partial IntPtr SBW_GetErrorDescription(IntPtr error);

                        [System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{releaseSymbol}")]
                        private static partial void SBW_ReleaseError(IntPtr error);

                        """);

                    // Emit SBW_Free if not already emitted by Utf8SliceEmitter for this type
                    if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, _emissionContext))
                    {
                        Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, _emissionContext);
                        csWriter.WriteLines($"""
                            [System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{freeSymbol}")]
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
                            [System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{extractorSymbol}")]
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
            var useCdecl = _env.MethodDecl.HasClosureCdeclWrapper;

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
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
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
                    csWriter.WriteLine($"var success = false;");
                    csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a => !a.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(a) && !_env.ClosureHandler.IsClosure(a) && !_env.TupleHandler.IsTuple(a) && !_env.ExistentialHandler.IsExistential(a) && (_env.MethodDecl.IsAccessor || !MarshallingHelpers.IsConvertibleType(a.SwiftTypeSpec))))
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                // ObjC bridged types: extract Handle from .NET iOS binding object
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"IntPtr {csName}Handle = {csName}?.Handle ?? IntPtr.Zero;");
                    continue;
                }

                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($"using PayloadBuffer<{typeRecord.CSharpTypeName}.Buffer> {csName}Disposable = {csName}.PayloadBuffer;");
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
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
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

                // Free GCHandle for escaping closures
                // Note: Async+throwing closures free their GCHandle inside Task.Run's finally block
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
                        !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
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
