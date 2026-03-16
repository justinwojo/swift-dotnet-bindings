// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via Arc.Retain()
    /// before calling async Swift methods. Must be released via Arc.Release() after callback.
    /// </summary>
    internal readonly struct RetainedSelfPtr
    {
        public readonly IntPtr Ptr;
        public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
    }

    /// <summary>
    /// Factory class for creating instances of ConstructorHandler.
    /// </summary>
    public class ConstructorHandlerFactory : HandlerFactory, IFactory<BaseDecl, IMethodHandler>
    {
        // Tracks which types have had the SwiftOptional metadata accessor P/Invoke emitted (inline path).
        // Prevents duplicate declarations when a type has multiple failable initializers.
        // Instance field scoped to a single generation run (factory is created per Conductor per run).
        private readonly HashSet<string> _emittedOptionalAccessorForTypes = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ConstructorHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ConstructorHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            // Async constructors fall through to MethodHandler for static CreateAsync() factory emission.
            // Failable constructors (init?) are handled here via EmitFailableFactory().
            return decl is MethodDecl methodDecl && methodDecl.IsConstructor && !methodDecl.IsAsync &&
                   (decl.ParentDecl is StructDecl || decl.ParentDecl is ClassDecl);
        }

        /// <summary>
        /// Constructs a new instance of ConstructorHandler.
        /// </summary>
        public IMethodHandler Construct()
        {
            return new ConstructorHandler(_handlerLogger, _emittedOptionalAccessorForTypes);
        }
    }

    /// <summary>
    /// Handler class for constructor declarations.
    /// </summary>
    public class ConstructorHandler : BaseHandler, IMethodHandler
    {
        private readonly HashSet<string> _emittedOptionalAccessorForTypes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="emittedOptionalAccessorForTypes">Shared dedup set from the factory, scoped to one generation run.</param>
        public ConstructorHandler(ILogger logger, HashSet<string> emittedOptionalAccessorForTypes) : base(logger)
        {
            _emittedOptionalAccessorForTypes = emittedOptionalAccessorForTypes;
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not MethodDecl methodDecl)
            {
                throw new ArgumentException("The provided decl must be a MethodDecl.", nameof(baseDecl));
            }
            return new MethodEnvironment(methodDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var methodEnv = (MethodEnvironment)env;
            // Inject composition collector into existing ExistentialHandler if not already set.
            // Marshal() creates environments without the collector; Emit() has the context.
            // Must inject into the existing handler (not create a new env) because signature-building
            // code references the existing ExistentialHandler — replacing the env wouldn't update
            // the handler that was already used during Marshal().
            if (context.CompositionCollector != null)
                methodEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);

            // Phases 3-6 (thunk closure, protocol constraints, bound generic skip gates,
            // generic constructor own params) are now in MemberValidationPipeline.
            // Only existential type argument accumulation remains here — it feeds
            // bypass/bridge fallback logic that requires emission context.

            bool hasExistentialArg = false;
            string? firstExistentialType = null;

            foreach (var argument in methodEnv.MethodDecl.CSSignature)
            {
                if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                    continue;

                if (methodEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
                {
                    // Allow containers with supported existential elements through to normal emission.
                    // Array<any P>, Dictionary<K, any P>, Optional<any P> all have dedicated handling.
                    if (!methodEnv.BoundGenericsHandler.IsContainerWithSupportedDirectExistential(argument.SwiftTypeSpec))
                    {
                        hasExistentialArg = true;
                        firstExistentialType ??= existentialType;
                    }
                }
            }

            if (hasExistentialArg)
            {
                // Try to generate a bypass wrapper instead of skipping
                if (ExistentialBypassEmitter.TryEmitConstructorBypass(csWriter, swiftWriter, methodEnv, _logger))
                {
                    methodEnv.MethodDecl.WasEmitted = true;
                    ReportCollector.RecordMemberWrapped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName,
                        methodEnv.MethodDecl.ParentDecl,
                        "ExistentialBypass",
                        $"Existential parameter(s) omitted; Swift defaults used.");
                    return;
                }

                // Try constrained existential bridge (e.g., any CameraFrameAnalyzer<CameraFrame, UIEvent>)
                if (ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, methodEnv, _logger, context.GetEmissionContext()))
                {
                    ReportCollector.RecordMemberWrapped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName,
                        methodEnv.MethodDecl.ParentDecl,
                        "ConstrainedExistentialBridge",
                        "Constrained existential parameter(s) bridged via @_silgen_name wrapper.");
                    methodEnv.MethodDecl.WasEmitted = true;
                    return;
                }

                // Fallback: skip as before
                _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: bound generic contains unsupported existential type argument '{firstExistentialType}'.");
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Method,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.ParentDecl,
                    SkipReason.UnsupportedExistential,
                    $"Constructor bound generic contains existential type argument '{firstExistentialType}'.");
                return;
            }

            // Try Optional<Closure>+default bypass for constructors — omits unsupported
            // optional closure params, letting Swift fill nil.
            if (!hasExistentialArg &&
                ExistentialBypassEmitter.HasOptionalClosureWithDefault(methodEnv.MethodDecl, methodEnv.TypeDatabase))
            {
                // Reduced-signature dedup — bypass strips params, check reduced projected key.
                // Use Contains() first; only Add() after bypass succeeds, so a failed bypass
                // doesn't poison the set and cause false duplicate skips for later members.
                var reducedCtorDecl = ExistentialBypassEmitter.BuildReducedMethodDecl(
                    methodEnv.MethodDecl, methodEnv.TypeDatabase);
                string? reducedCtorKey = null;
                if (reducedCtorDecl != null && methodEnv.EmittedProjectedSignatures != null)
                {
                    reducedCtorKey = GetProjectedCSharpMethodKey(reducedCtorDecl, methodEnv.TypeDatabase, _logger);
                    if (methodEnv.EmittedProjectedSignatures.Contains(reducedCtorKey))
                    {
                        _logger.LogDebug($"Skipping constructor {methodEnv.MethodDecl.Name}: optional closure bypass reduced signature collides: {reducedCtorKey}");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.DuplicateSignature,
                            $"Optional closure bypass reduced constructor signature collides: {reducedCtorKey}");
                        return;
                    }
                }

                if (ExistentialBypassEmitter.TryEmitConstructorBypass(csWriter, swiftWriter, methodEnv, _logger))
                {
                    // Reserve the reduced key now that emission succeeded
                    if (reducedCtorKey != null)
                        methodEnv.EmittedProjectedSignatures?.Add(reducedCtorKey);
                    methodEnv.MethodDecl.WasEmitted = true;
                    ReportCollector.RecordMemberWrapped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName,
                        methodEnv.MethodDecl.ParentDecl,
                        "OptionalClosureBypass",
                        "Optional closure parameter(s) with defaults omitted; Swift fills nil.");
                    return;
                }
                // Explicit fallback skip — bypass failed (not struct, failable, throwing)
                _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: optional closure params with defaults but constructor bypass not applicable.");
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Method,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.ParentDecl,
                    SkipReason.UnsupportedClosure,
                    "Optional closure parameter(s) with defaults, but constructor shape incompatible with bypass.");
                return;
            }

            // Try constrained existential bridge (e.g., any CameraFrameAnalyzer<CameraFrame, UIEvent>)
            if (ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, methodEnv, _logger, context.GetEmissionContext()))
            {
                ReportCollector.RecordMemberWrapped(
                    BindingItemKind.Method,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.MangledName,
                    methodEnv.MethodDecl.ParentDecl,
                    "ConstrainedExistentialBridge",
                    "Constrained existential parameter(s) bridged via @_silgen_name wrapper.");
                methodEnv.MethodDecl.WasEmitted = true;
                return;
            }

            // Emit Swift wrapper for constructors with debug params (#file, #line, etc.)
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                DefaultParameterOverloadEmitter.HasDebugParameters(methodEnv.MethodDecl))
            {
                DefaultParameterOverloadEmitter.EmitDebugParamWrapper(swiftWriter, methodEnv);
            }

            // Set @_cdecl constructor wrapper flags BEFORE SignatureHandler creation.
            // SignatureHandler reads UsesCdeclConstructorWrapper to decide SwiftIndirectResult vs IntPtr
            // and MangledName to compute the P/Invoke method name via GetPInvokeName().
            if (ConstructorWrapperEmitter.ShouldEmitWrapper(methodEnv))
            {
                var parentType_ = methodEnv.ParentDecl as TypeDecl;
                var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
                    parentType_!.SwiftTypeName.Module,
                    parentType_.Name,
                    methodEnv.MethodDecl.MangledName);
                methodEnv.MethodDecl.UsesCdeclConstructorWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.MangledName = cdeclSymbol;

                // Mark if this @_cdecl constructor wrapper handles closure params inline
                if (methodEnv.MethodDecl.CSSignature.Skip(1).Any(methodEnv.ClosureHandler.IsClosure))
                    methodEnv.MethodDecl.HasClosureParams = true;
            }

            // Track wrapper strategy for emission report (once per constructor, after flags are locked).
            context.GetEmissionContext().IncrementWrapperStrategy(methodEnv.MethodDecl.WrapperStrategy.ToString());

            var signatureHandler = new SignatureHandler(methodEnv);

            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Constructor {methodEnv.MethodDecl.Name} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl, SkipReason.UnsupportedSignature, "Constructor signature contains unsupported placeholder type.");
                return;
            }

            // Set closure Cdecl flags BEFORE WrapperEmitter reads P/Invoke signature.
            // ONLY when no other generator owns the wrapper (see method path comment).
            // Only frozen struct constructors are supported — non-frozen structs and classes
            // require indirect return ABI which the standalone Cdecl wrapper doesn't handle.
            // Failable constructors (init?) also require indirect return for Optional<Self>.
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsFailable &&
                methodEnv.ParentDecl is StructDecl ctorParentStruct && ctorParentStruct.IsFrozen &&
                ClosureEmitter.NeedsClosureCdeclWrapper(methodEnv.MethodDecl, methodEnv.ClosureHandler))
            {
                methodEnv.MethodDecl.HasClosureCdeclWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                ClosureEmitter.EmitClosureCdeclSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl,
                    emissionContext: context.GetEmissionContext());
            }

            // Optional pointer wrapper for constructors with large Optional params.
            // Same constraints as closure Cdecl: frozen struct only, not failable, not async.
            // Skip generic structs — wrapper emits `TypeName.self` without type parameters,
            // causing "generic parameter could not be inferred" errors (Issue O).
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsFailable &&
                !methodEnv.MethodDecl.IsAsync &&
                methodEnv.ParentDecl is StructDecl ctorOptStruct && ctorOptStruct.IsFrozen &&
                !ctorOptStruct.IsGeneric &&
                methodEnv.BoundGenericsHandler.HasLargeOptionalParams(methodEnv.MethodDecl))
            {
                methodEnv.MethodDecl.HasOptionalPointerWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                OptionalPointerWrapperEmitter.EmitSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl,
                    emissionContext: context.GetEmissionContext());
            }

            MethodHandler.CheckExportedSymbol(methodEnv);

            // Emit Swift @_cdecl constructor wrapper AFTER signature validation, BEFORE WrapperEmitter.
            if (methodEnv.MethodDecl.UsesCdeclConstructorWrapper)
            {
                ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
                    swiftWriter, methodEnv, context.GetEmissionContext());
            }

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler, emissionContext: context.GetEmissionContext());

            // C2: Emit Swift typed error extractor for ALL throwing constructors
            // (covers both failable EmitFailableFactory and non-failable EmitConstructor paths)
            wrapperEmitter.EmitTypedErrorExtractor(swiftWriter);

            if (methodEnv.MethodDecl.IsFailable)
            {
                wrapperEmitter.EmitFailableFactory(csWriter);

                // Emit the SwiftOptional metadata accessor P/Invoke once per type.
                // PInvokeHelperContext deduplicates by method name; for inline, use shared factory set.
                var typeKey = methodEnv.ParentDecl is TypeDecl td ? td.SwiftTypeName?.ToString() ?? methodEnv.ParentDecl.Name : methodEnv.ParentDecl.Name;
                if (methodEnv.PInvokeHelperContext != null || _emittedOptionalAccessorForTypes.Add(typeKey))
                {
                    wrapperEmitter.EmitOptionalMetadataAccessorPInvoke(csWriter);
                }
            }
            else
            {
                wrapperEmitter.EmitConstructor(csWriter);
            }
            PInvokeEmitter.EmitPInvoke(csWriter, methodEnv, signatureHandler);
            methodEnv.MethodDecl.WasEmitted = true;
            ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);

            // Post-processor table: only Scope=All processors run for constructors
            var postCtx = new PostProcessorContext(csWriter, swiftWriter, methodEnv, _logger,
                context.GetEmissionContext(), context.MarkerProtocolConformances);
            foreach (var pp in MethodHandler.PostProcessors)
            {
                if (pp.Scope == PostProcessorScope.MethodsOnly)
                    continue;
                pp.TryPostProcess(postCtx);
            }

            csWriter.WriteLine();
        }

        /// <summary>
        /// Returns true if the method's only non-return, non-debug parameters are empty tuples ().
        /// </summary>
        internal static bool HasOnlyEmptyTupleParams(MethodDecl method)
        {
            var realParams = method.CSSignature.Skip(1)
                .Where(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a));
            return realParams.Any() && realParams.All(a => a.SwiftTypeSpec.IsEmptyTuple);
        }

        /// <summary>
        /// Returns true if the same type has another constructor that is truly parameterless
        /// (zero non-debug params), i.e. a sibling that won't itself be skipped by the
        /// empty-tuple gate. Excludes siblings that have only empty tuple params because
        /// those will also be skipped (avoiding mutual exclusion where both get dropped).
        /// </summary>
        internal static bool HasParameterlessConstructorSibling(MethodDecl method)
        {
            var siblingMethods = method.ParentDecl switch
            {
                TypeDecl typeDecl => typeDecl.Methods,
                _ => method.ModuleDecl?.Methods
            };
            if (siblingMethods == null)
                return false;

            return siblingMethods.Any(m =>
                m.IsConstructor &&
                m.MangledName != method.MangledName &&
                !HasOnlyEmptyTupleParams(m) &&
                !m.CSSignature.Skip(1)
                    .Where(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a))
                    .Any(a => !a.SwiftTypeSpec.IsEmptyTuple));
        }
    }

    /// <summary>
    /// Represents a method handler factory.
    /// </summary>
    public class MethodHandlerFactory : HandlerFactory, IFactory<BaseDecl, IMethodHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MethodHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public MethodHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<MethodHandler>())
        {
        }

        /// <summary>
        /// Checks if the factory can handle the declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        /// <returns></returns>
        public bool Handles(BaseDecl decl)
        {
            return decl is MethodDecl;
        }

        /// <summary>
        /// Constructs a handler.
        /// </summary>
        public IMethodHandler Construct()
        {
            return new MethodHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Represents a method handler.
    /// </summary>
    public class MethodHandler : BaseHandler, IMethodHandler
    {
        /// <summary>
        /// Bridge dispatch table — ordered sequence of bridge adapters tried before Section C (normal emission).
        /// Ordering invariants:
        /// 1. ExistentialBypass FIRST — existential-blocked methods must be handled/skipped before other bridges
        /// 2. ProtocolExtensionClosureBridge before MethodClosureBridge
        /// 3. OptionalClosureBypass LAST — narrowest scope
        /// </summary>
        private static readonly IMethodBridgeEmitter[] _bridgeEmitters =
        [
            new ExistentialBypassBridgeAdapter(),          // Must be first: existential-blocked methods
            new ArraySliceBridgeAdapter(),                 // ArraySlice normalization
            new GenericClosureBridgeAdapter(),             // Generic closures
            new ProtocolExtensionClosureBridgeAdapter(),   // Invariant #2: before MethodClosureBridge
            new MethodClosureBridgeAdapter(),              // Bound generic closure args
            new NestedClosureBridgeAdapter(),              // Two-level trampoline
            new OptionalClosureBypassAdapter(),            // Last: narrowest scope
        ];

        /// <summary>
        /// Read-only view of the bridge dispatch table for testing and inspection.
        /// </summary>
        internal static IReadOnlyList<IMethodBridgeEmitter> BridgeEmitters => _bridgeEmitters;

        /// <summary>
        /// Post-processor table — ordered sequence of post-processors run after normal method emission.
        /// Ordering: DefaultParameter first (emits Swift wrappers that other overloads reference).
        /// </summary>
        private static readonly IMethodPostProcessor[] _postProcessors =
        [
            new DefaultParameterOverloadPostProcessor(),    // Must be first: emits Swift wrappers
            new CompletionHandlerPostProcessor(),           // Task-returning overloads (WU8)
            new MarkerProtocolOverloadPostProcessor(),      // Typed marker protocol overloads
            new NativeIntOverloadPostProcessor(),           // int/uint convenience overloads
            new ThrowingClosureSimplificationPostProcessor(), // Action/Func overloads for throwing closures
        ];

        /// <summary>
        /// Read-only view of the post-processor table for testing and inspection.
        /// </summary>
        internal static IReadOnlyList<IMethodPostProcessor> PostProcessors => _postProcessors;

        /// <summary>
        /// Initializes a new instance of the <see cref="MethodHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public MethodHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not MethodDecl methodDecl)
            {
                throw new ArgumentException("The provided decl must be a MethodDecl.", nameof(baseDecl));
            }
            return new MethodEnvironment(methodDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var methodEnv = (MethodEnvironment)env;
            // Inject composition collector into existing ExistentialHandler if not already set.
            // Marshal() creates environments without the collector; Emit() has the context.
            // Must inject into the existing handler (not create a new env) because signature-building
            // code references the existing ExistentialHandler — replacing the env wouldn't update
            // the handler that was already used during Marshal().
            if (context.CompositionCollector != null)
                methodEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);

            // Phases 3-5 (thunk closure, protocol constraints, bound generic skip gates)
            // are now in MemberValidationPipeline. Only existential type argument
            // accumulation remains here — it feeds bypass/bridge fallback logic.

            var isAccessor = methodEnv.MethodDecl.IsAccessor;

            // Existential state accumulated by validation loop, passed to bridge dispatch table.
            // Declared at method scope so BridgeEmitterContext can reference them for both accessor
            // and non-accessor paths (accessors skip validation, so these stay at defaults).
            bool hasMethodExistentialArg = false;
            string? firstMethodExistentialType = null;

            if (!isAccessor)
            {
                foreach (var argument in methodEnv.MethodDecl.CSSignature)
                {
                    if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                        continue;

                    if (methodEnv.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
                    {
                        // Accumulate for bypass attempt instead of returning immediately
                        hasMethodExistentialArg = true;
                        firstMethodExistentialType ??= existentialType;
                        continue;
                    }

                    // B6: Catch supported existentials in non-container bound generics.
                    // Array<any Protocol>, Dictionary<K, any Protocol>, and Optional<any Protocol>
                    // have dedicated existential handling via TypeProjectionFactory → ExistentialProjection.
                    // Other types fall to generic marshalling which produces a type mismatch.
                    if (methodEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var supportedExistentialType))
                    {
                        if (!methodEnv.BoundGenericsHandler.IsContainerWithSupportedDirectExistential(argument.SwiftTypeSpec))
                        {
                            // Accumulate for bypass attempt instead of returning immediately
                            hasMethodExistentialArg = true;
                            firstMethodExistentialType ??= supportedExistentialType.ToString();
                            continue;
                        }
                    }
                }
            }

            // B2: Bridge dispatch table — try each bridge adapter in order.
            // Non-null result means the method was handled (emitted or explicitly skipped).
            // On success, set WasEmitted and record the bridge; on skip, just return.
            // Accessors skip bridges (all adapters guard !isAccessor via eligibility checks).
            if (!isAccessor)
            {
                var bridgeContext = new BridgeEmitterContext(
                    csWriter, swiftWriter, methodEnv, _logger, context.GetEmissionContext(),
                    hasMethodExistentialArg, firstMethodExistentialType);

                foreach (var bridge in _bridgeEmitters)
                {
                    var result = bridge.TryEmit(bridgeContext);
                    if (result != null)
                    {
                        if (result.WasEmitted)
                        {
                            methodEnv.MethodDecl.WasEmitted = true;
                            ReportCollector.RecordMemberWrapped(
                                BindingItemKind.Method, methodEnv.MethodDecl.Name,
                                methodEnv.MethodDecl.MangledName, methodEnv.MethodDecl.ParentDecl,
                                result.BridgeName, result.Description);
                        }
                        return;
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // PHASE 1: FLAG SETTING — all BEFORE SignatureHandler creation.
            // UsesCdeclMethodWrapper and related flags are consumed by SignatureHandler,
            // WrapperEmitter, and PInvokeEmitter. All must be set before line creating SignatureHandler.
            // ══════════════════════════════════════════════════════════════

            // Save original mangled name before any wrapper changes it.
            var originalMangledName = methodEnv.MethodDecl.MangledName;

            // Check for debug params BEFORE EmitDebugParamWrapper removes them from CSSignature.
            bool hadDebugParams = !methodEnv.MethodDecl.UsesWrapperLibrary &&
                DefaultParameterOverloadEmitter.HasDebugParameters(methodEnv.MethodDecl);

            // Emit Swift wrapper for methods with debug params (#file, #line, etc.)
            // Must happen before SignatureHandler — updates MangledName + UsesWrapperLibrary.
            if (hadDebugParams)
            {
                DefaultParameterOverloadEmitter.EmitDebugParamWrapper(swiftWriter, methodEnv);
            }

            // Set @_cdecl constructor wrapper flags BEFORE SignatureHandler creation.
            // SignatureHandler reads UsesCdeclConstructorWrapper to decide SwiftIndirectResult vs IntPtr
            // and MangledName to compute the P/Invoke method name via GetPInvokeName().
            string? originalMangledNameForCtor = null;
            if (ConstructorWrapperEmitter.ShouldEmitWrapper(methodEnv))
            {
                var parentType_ = methodEnv.ParentDecl as TypeDecl;
                var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
                    parentType_!.SwiftTypeName.Module,
                    parentType_.Name,
                    methodEnv.MethodDecl.MangledName);
                originalMangledNameForCtor = methodEnv.MethodDecl.MangledName;
                methodEnv.MethodDecl.UsesCdeclConstructorWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.MangledName = cdeclSymbol;

                // Mark if this @_cdecl constructor wrapper handles closure params inline
                if (methodEnv.MethodDecl.CSSignature.Skip(1).Any(methodEnv.ClosureHandler.IsClosure))
                    methodEnv.MethodDecl.HasClosureParams = true;
            }

            // Debug param + @_cdecl: check if the debug-param-wrapped method qualifies for @_cdecl.
            // EmitDebugParamWrapper already set UsesWrapperLibrary=true which blocks ShouldEmitWrapper,
            // so temporarily clear it. The debug @_silgen_name wrapper becomes the silgenTarget.
            // NOTE: hadDebugParams was captured BEFORE EmitDebugParamWrapper removed debug params
            // from CSSignature, because HasDebugParameters scans CSSignature.
            string? debugSilgenTarget = null;
            if (hadDebugParams &&
                methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.UsesCdeclConstructorWrapper)
            {
                methodEnv.MethodDecl.UsesWrapperLibrary = false;
                bool debugCdeclEligible = MethodWrapperEmitter.ShouldEmitWrapper(methodEnv);
                methodEnv.MethodDecl.UsesWrapperLibrary = true;

                if (debugCdeclEligible)
                {
                    var hash = EmitterUtility.DeterministicHash8(originalMangledName);
                    debugSilgenTarget = $"_dbg_{methodEnv.MethodDecl.GetSwiftName()}_{hash}";

                    var parentType_ = methodEnv.ParentDecl as TypeDecl;
                    var parentModule = methodEnv.ParentDecl as ModuleDecl;
                    string moduleName = parentType_?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
                    string typeName = parentType_?.Name ?? "Free";
                    var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                        moduleName, typeName,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName);
                    methodEnv.MethodDecl.UsesCdeclMethodWrapper = true;
                    methodEnv.MethodDecl.MangledName = cdeclSymbol;
                }
            }

            // Set @_cdecl method wrapper flags BEFORE SignatureHandler creation.
            // Must come after constructor wrapper check (mutually exclusive).
            if (!methodEnv.MethodDecl.IsConstructor &&
                !methodEnv.MethodDecl.UsesCdeclPropertyWrapper &&
                !methodEnv.MethodDecl.UsesCdeclConstructorWrapper &&
                !methodEnv.MethodDecl.UsesCdeclMethodWrapper && // Not already set by debug-param path
                MethodWrapperEmitter.ShouldEmitWrapper(methodEnv))
            {
                var parentType_ = methodEnv.ParentDecl as TypeDecl;
                var parentModule = methodEnv.ParentDecl as ModuleDecl;
                string moduleName = parentType_?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
                string typeName = parentType_?.Name ?? "Free";
                var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                    moduleName, typeName,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.MangledName);
                methodEnv.MethodDecl.UsesCdeclMethodWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.MangledName = cdeclSymbol;

                // Mark if this @_cdecl method wrapper handles closure params inline
                if (methodEnv.MethodDecl.CSSignature.Skip(1).Any(methodEnv.ClosureHandler.IsClosure))
                    methodEnv.MethodDecl.HasClosureParams = true;
            }

            // Closure wrapper + @_cdecl: set flags BEFORE SignatureHandler.
            // NeedsClosureCdeclWrapper() already excludes async methods and opaque return methods.
            // ONLY set flags when no other generator owns the wrapper.
            bool needsClosureWrapper = false;
            bool closureCdecl = false;
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsModuleInternal &&
                ClosureEmitter.NeedsClosureCdeclWrapper(methodEnv.MethodDecl, methodEnv.ClosureHandler))
            {
                needsClosureWrapper = true;
                methodEnv.MethodDecl.HasClosureCdeclWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;

                closureCdecl = ClosureEmitter.CanConvertToCdecl(methodEnv);
                if (closureCdecl)
                {
                    var parentType_ = methodEnv.ParentDecl as TypeDecl;
                    var parentModule = methodEnv.ParentDecl as ModuleDecl;
                    string moduleName = parentType_?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
                    string typeName = parentType_?.Name ?? "Free";
                    var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                        moduleName, typeName,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName);
                    methodEnv.MethodDecl.UsesCdeclMethodWrapper = true;
                    methodEnv.MethodDecl.MangledName = cdeclSymbol;
                }
            }

            // Optional pointer wrapper + @_cdecl: set flags BEFORE SignatureHandler.
            var parentTypeDecl = methodEnv.ParentDecl as TypeDecl;
            bool needsOptionalPointerWrapper = false;
            bool optPtrCdecl = false;
            // DynamicSelf returns can't be expressed in a free function (@_silgen_name / @_cdecl),
            // so skip the optional pointer wrapper path for those methods.
            var returnSpec = methodEnv.MethodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasDynamicSelfReturn = returnSpec?.IsDynamicSelf == true;
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsAsync &&
                !methodEnv.MethodDecl.IsModuleInternal &&
                !hasDynamicSelfReturn &&
                !WrapperValidation.HasRawGenericTypeParams(methodEnv.MethodDecl) &&
                !_requiresOpaqueReturn(methodEnv) &&
                parentTypeDecl?.IsGeneric != true &&
                (methodEnv.BoundGenericsHandler.HasLargeOptionalParams(methodEnv.MethodDecl) ||
                 methodEnv.BoundGenericsHandler.IsLargeOptionalReturn(methodEnv.MethodDecl)))
            {
                needsOptionalPointerWrapper = true;
                methodEnv.MethodDecl.HasOptionalPointerWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;

                optPtrCdecl = OptionalPointerWrapperEmitter.CanConvertToCdecl(methodEnv);
                if (optPtrCdecl)
                {
                    var parentType_ = methodEnv.ParentDecl as TypeDecl;
                    var parentModule = methodEnv.ParentDecl as ModuleDecl;
                    string moduleName = parentType_?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
                    string typeName = parentType_?.Name ?? "Free";
                    var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                        moduleName, typeName,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName);
                    methodEnv.MethodDecl.UsesCdeclMethodWrapper = true;
                    methodEnv.MethodDecl.MangledName = cdeclSymbol;
                }
            }

            // Async @_cdecl eligibility — must run BEFORE SignatureHandler creation.
            // Can't use ShouldEmitWrapper (gate 7 rejects async). Use HasCdeclCompatibleFunctionShape.
            bool asyncCdeclEligible = false;
            if (methodEnv.MethodDecl.IsAsync &&
                !methodEnv.MethodDecl.UsesWrapperLibrary &&
                MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(methodEnv))
            {
                asyncCdeclEligible = methodEnv.MethodDecl.CSSignature.Skip(1).All(p => {
                    if (p.IsGeneric) return false;
                    if (p.SwiftTypeSpec is ClosureTypeSpec) return false;
                    if (ConstructorWrapperEmitter.IsProtocolExistentialType(p.SwiftTypeSpec, methodEnv.TypeDatabase)) return false;
                    if (MethodWrapperEmitter.IsNestedFrozenStructParam(p, methodEnv.TypeDatabase)) return false;
                    if (MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(p, methodEnv.TypeDatabase)) return false;
                    return true;
                });
            }
            if (asyncCdeclEligible)
            {
                var parentType_ = methodEnv.ParentDecl as TypeDecl;
                var parentModule = methodEnv.ParentDecl as ModuleDecl;
                string moduleName = parentType_?.SwiftTypeName.Module ?? parentModule?.Name ?? "";
                string typeName = parentType_?.Name ?? "Free";
                var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
                    moduleName, typeName,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.MangledName);
                methodEnv.MethodDecl.UsesCdeclMethodWrapper = true;
                methodEnv.MethodDecl.MangledName = cdeclSymbol;
            }

            // Log when a method in xcframework mode falls back to CallConvSwift (no wrapper).
            // Makes silent fallbacks visible for debugging wrapper coverage gaps.
            if (!methodEnv.MethodDecl.UsesCdeclWrapper &&
                !methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsAccessor &&
                !isAccessor &&
                WrapperValidation.IsXCFrameworkMode(methodEnv.TypeDatabase))
            {
                var reason = WrapperValidation.GetRejectionReason(methodEnv);
                if (reason != null)
                {
                    _logger.LogDebug("Method {MethodName} on {ParentName}: falling back to CallConvSwift ({Reason})",
                        methodEnv.MethodDecl.Name,
                        methodEnv.ParentDecl?.Name ?? "free",
                        reason);
                }
            }

            // Track wrapper strategy and skip reasons for emission report.
            // Runs once per method after all @_cdecl flags are locked.
            if (!isAccessor)
            {
                context.GetEmissionContext().IncrementWrapperStrategy(methodEnv.MethodDecl.WrapperStrategy.ToString());
                if (!methodEnv.MethodDecl.UsesCdeclWrapper && WrapperValidation.IsXCFrameworkMode(methodEnv.TypeDatabase))
                {
                    var skipReason = WrapperValidation.GetRejectionReason(methodEnv);
                    if (skipReason != null)
                        context.GetEmissionContext().IncrementWrapperSkipReason(skipReason);
                }
            }

            // ══════════════════════════════════════════════════════════════
            // PHASE 2: PIPELINE — reads flags, emits code.
            // All @_cdecl flags are locked above this point.
            // ══════════════════════════════════════════════════════════════

            var signatureHandler = new SignatureHandler(methodEnv);

            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Method {methodEnv.MethodDecl.Name} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType} [params: {string.Join(", ", signatureHandler.GetWrapperSignature().Parameters.Select(p => $"{p.Type}:{p.Name}"))}]");
                if (!isAccessor)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl, SkipReason.UnsupportedSignature, "Method signature contains unsupported placeholder type.");
                }
                return;
            }

            // Skip symbol cross-referencing for accessors — [Obsolete] on an accessor method
            // would cause CS0619 in the property body that calls it directly.
            if (!isAccessor)
                CheckExportedSymbol(methodEnv);

            TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
            if (!isAccessor)
            {
                foreach (var argument in methodEnv.MethodDecl.CSSignature)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(methodEnv.TypeDatabase, methodEnv.ClosureHandler, argument.SwiftTypeSpec, out var foundFallbackInfo))
                    {
                        fallbackInfo = foundFallbackInfo;
                        break;
                    }
                }
            }

            // Pre-scan: flag methods that will get throwing closure simplification overloads
            // so WrapperEmitter can annotate the original with [EditorBrowsable(Never)].
            // ShouldSimplify includes dedup check (Contains, not Add) to avoid hiding the
            // original when the overload would be blocked by a signature collision.
            if (!isAccessor && ThrowingClosureSimplificationEmitter.ShouldSimplify(methodEnv))
            {
                methodEnv.MethodDecl.HasThrowingClosureSimplification = true;
            }

            // Emit Swift @_cdecl constructor wrapper AFTER signature validation, BEFORE WrapperEmitter.
            if (methodEnv.MethodDecl.UsesCdeclConstructorWrapper)
            {
                ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
                    swiftWriter, methodEnv, context.GetEmissionContext());
            }

            // Emit closure Swift wrapper (Phase 2 — flags already set in Phase 1)
            if (needsClosureWrapper)
            {
                ClosureEmitter.EmitClosureCdeclSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl,
                    useCdecl: closureCdecl, emissionContext: context.GetEmissionContext());
            }

            // Emit optional pointer Swift wrapper (Phase 2 — flags already set in Phase 1)
            if (needsOptionalPointerWrapper)
            {
                OptionalPointerWrapperEmitter.EmitSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl,
                    useCdecl: optPtrCdecl, emissionContext: context.GetEmissionContext());
            }

            // Emit Swift @_cdecl method wrapper AFTER signature validation, BEFORE WrapperEmitter.
            // Only for standard method wrappers — closure, optional-pointer, and async paths
            // emit their own @_cdecl wrappers.
            if (methodEnv.MethodDecl.UsesCdeclMethodWrapper &&
                !methodEnv.MethodDecl.HasClosureCdeclWrapper &&
                !methodEnv.MethodDecl.HasOptionalPointerWrapper &&
                !methodEnv.MethodDecl.IsAsync)
            {
                MethodWrapperEmitter.EmitSwiftMethodWrapper(
                    swiftWriter, methodEnv, context.GetEmissionContext(),
                    silgenTarget: debugSilgenTarget,
                    silgenHasResultBuffer: debugSilgenTarget != null && methodEnv.BoundGenericsHandler.IsLargeOptionalReturn(methodEnv.MethodDecl));
            }

            // @_cdecl method wrapper: emit SBW_Free P/Invoke for string-returning methods (once per type)
            if (methodEnv.MethodDecl.UsesCdeclMethodWrapper &&
                WitnessDispatchEmitter.IsStringType(methodEnv.MethodDecl.CSSignature.First().SwiftTypeSpec))
            {
                var parentTypeDeclForFree = methodEnv.ParentDecl as TypeDecl;
                var typeKey = parentTypeDeclForFree?.SwiftTypeName?.ModuleQualifiedName
                    ?? methodEnv.MethodDecl.ModuleDecl?.Name ?? "";
                if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, context.GetEmissionContext()))
                {
                    Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, context.GetEmissionContext());
                    var moduleName = parentTypeDeclForFree?.SwiftTypeName?.Module ?? methodEnv.MethodDecl.ModuleDecl?.Name ?? "";
                    var wrapperLibPath = methodEnv.TypeDatabase.AsyncLibraryName
                        ?? methodEnv.TypeDatabase.GetLibraryPath(moduleName);
                    var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
                    csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{freeSymbol}\")]");
                    csWriter.WriteLine("private static partial void SBW_Free(IntPtr ptr);");
                    csWriter.WriteLine();
                }
            }

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler, fallbackInfo, context.GetEmissionContext());
            wrapperEmitter.EmitMethod(csWriter, swiftWriter);
            PInvokeEmitter.EmitPInvoke(csWriter, methodEnv, signatureHandler);
            methodEnv.MethodDecl.WasEmitted = true;
            if (isAccessor)
            {
                ReportCollector.RecordMemberSynthesized(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);
            }
            else
            {
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);
            }

            // Post-processor table: overload generation after normal emission
            var postCtx = new PostProcessorContext(csWriter, swiftWriter, methodEnv, _logger,
                context.GetEmissionContext(), context.MarkerProtocolConformances);
            foreach (var pp in _postProcessors)
            {
                if (pp.Scope == PostProcessorScope.MethodsOnly && isAccessor)
                    continue;
                pp.TryPostProcess(postCtx);
            }

            csWriter.WriteLine();
        }

        /// <summary>
        /// Cross-references the method's P/Invoke entry point against the module's exported symbols.
        /// Sets <see cref="MethodDecl.IsMissingExportedSymbol"/> when the symbol is not found in the TBD.
        /// Must be called AFTER all wrapper-routing flags are finalized (closure Cdecl, async, etc.).
        /// </summary>
        internal static void CheckExportedSymbol(MethodEnvironment methodEnv)
        {
            var methodDecl = (MethodDecl)methodEnv.MethodDecl;
            var moduleDecl = methodDecl.ModuleDecl;
            if (moduleDecl?.ExportedSymbols == null) return;

            var (entryPoint, needsWrapperLib) = PInvokeEmitter.ComputeEntryPoint(methodDecl);
            if (needsWrapperLib) return; // Wrapper symbols are generated, not in TBD

            if (!moduleDecl.ExportedSymbols.Contains(entryPoint))
            {
                methodDecl.IsMissingExportedSymbol = true;
            }
        }

        /// <summary>
        /// Returns true when the method has an opaque return type (some Protocol).
        /// </summary>
        private static bool _requiresOpaqueReturn(MethodEnvironment methodEnv) =>
            methodEnv.MethodDecl.CSSignature.Count > 0 &&
            methodEnv.MethodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };

        /// <summary>
        /// Emits a Task-returning overload for methods with completion handler closures.
        /// The overload calls the original method with a TCS-based lambda and returns the Task.
        /// </summary>
        internal static void TryEmitCompletionHandlerOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
        {
            var methodDecl = methodEnv.MethodDecl;
            var parameters = methodDecl.CSSignature.Skip(1).ToList();
            if (parameters.Count == 0)
                return;

            // Find the completion handler parameter (must be last)
            var lastParam = parameters.Last();
            if (!methodEnv.ClosureHandler.IsClosure(lastParam))
                return;

            if (!CompletionHandlerDetector.IsCompletionHandler(methodDecl, lastParam, methodEnv.ClosureHandler))
                return;

            var closureSpec = methodEnv.ClosureHandler.GetClosureTypeSpec(lastParam)!;
            var shape = CompletionHandlerDetector.GetCallbackShape(closureSpec);
            if (shape == CompletionHandlerDetector.CallbackShape.Unsupported)
                return;

            // Build the async method name
            var baseMethodName = methodEnv.CSharpMethodName;
            var asyncMethodName = baseMethodName + "Async";

            // Check for name collision with existing methods
            if (methodEnv.EmittedProjectedSignatures != null)
            {
                // Build projected key for the overload (same params minus closure, plus CancellationToken).
                // Must match the key format from IHandler.GetProjectedCSharpMethodKey so that
                // completion handler wrappers collide with native async methods of the same name.
                var overloadParamTypes = new List<string>();
                foreach (var p in parameters.Take(parameters.Count - 1))
                {
                    var factory = new TypeProjectionFactory();
                    var projection = factory.Project(p.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = methodEnv.TypeDatabase,
                        IsParameter = true
                    });
                    string paramType;
                    if (projection != null)
                        paramType = projection.PublicType;
                    else if (methodEnv.TypeDatabase.TryGetTypeRecord(p.SwiftTypeSpec, out var record))
                        paramType = record.CSharpTypeName.FullyQualifiedName;
                    else
                        paramType = p.SwiftTypeSpec.ToString();
                    paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
                        paramType, p.SwiftTypeSpec, methodEnv.TypeDatabase);
                    overloadParamTypes.Add(paramType);
                }
                overloadParamTypes.Add("System.Threading.CancellationToken");
                var overloadKey = $"{asyncMethodName}({string.Join(",", overloadParamTypes)})";
                if (!methodEnv.EmittedProjectedSignatures.Add(overloadKey))
                    return; // Collision — skip
            }

            // Resolve the result type for the Task
            var resultTypeName = CompletionHandlerDetector.GetResultTypeName(
                closureSpec, shape, methodEnv.TypeDatabase, methodEnv.TypeConversionHandler);

            // Guard: shapes that require a result type must have one resolved
            // (e.g., bound generic Result<T, Error> with unresolvable generic args)
            if (resultTypeName == null &&
                (shape == CompletionHandlerDetector.CallbackShape.SingleResult ||
                 shape == CompletionHandlerDetector.CallbackShape.ResultWithError))
                return;

            // Guard: verify the callback result type (as seen by the lambda) is compatible with
            // the TCS result type. The closure handler resolves types to wrapper forms (SwiftOptional<T>,
            // SwiftArray<T>) while GetResultTypeName uses idiomatic types (T?, IEnumerable<T>).
            // If they differ and no implicit conversion exists, the lambda can't pass through.
            if (resultTypeName != null)
            {
                var resultArg = closureSpec.GetArgument(0);
                var closureArgType = methodEnv.ClosureHandler.TranslateTypeSpecToCSharp(resultArg);
                if (!IsCompletionResultCompatible(closureArgType, resultTypeName))
                    return;
            }

            var taskType = resultTypeName != null ? $"Task<{resultTypeName}>" : "Task";
            var tcsType = resultTypeName != null ? $"TaskCompletionSource<{resultTypeName}>" : "TaskCompletionSource<bool>";
            var tcsSetResult = resultTypeName != null ? "" : "true";

            // Build non-closure parameters for the overload signature
            var nonClosureParams = parameters.Take(parameters.Count - 1).ToList();
            var overloadParams = new List<string>();
            foreach (var p in nonClosureParams)
            {
                var csName = NameProvider.GetCSharpParameterName(p);
                var factory = new TypeProjectionFactory();
                var projection = factory.Project(p.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = methodEnv.TypeDatabase,
                    IsParameter = true
                });
                string paramType;
                if (projection != null)
                    paramType = projection.PublicType;
                else if (methodEnv.TypeDatabase.TryGetTypeRecord(p.SwiftTypeSpec, out var record))
                    paramType = record.CSharpTypeName.FullyQualifiedName;
                else
                    paramType = p.SwiftTypeSpec.ToString();
                overloadParams.Add($"{paramType} {csName}");
            }
            overloadParams.Add("global::System.Threading.CancellationToken cancellationToken = default");

            var paramString = string.Join(", ", overloadParams);
            var accessModifier = NameProvider.GetAccessModifier(methodDecl.Visibility);

            // Build the lambda body based on callback shape
            var callArgs = new List<string>();
            foreach (var p in nonClosureParams)
            {
                callArgs.Add(NameProvider.GetCSharpParameterName(p));
            }

            string lambdaBody;
            switch (shape)
            {
                case CompletionHandlerDetector.CallbackShape.VoidResult:
                    lambdaBody = "() => tcs.TrySetResult(true)";
                    break;
                case CompletionHandlerDetector.CallbackShape.SingleResult:
                    lambdaBody = "result => tcs.TrySetResult(result)";
                    break;
                case CompletionHandlerDetector.CallbackShape.ErrorOnly:
                    lambdaBody = """
                        error =>
                                {
                                    if (error is { } err)
                                        tcs.TrySetException(new SwiftException(err.ToString()));
                                    else
                                        tcs.TrySetResult(true);
                                }
                        """;
                    break;
                case CompletionHandlerDetector.CallbackShape.ResultWithError:
                    lambdaBody = $$"""
                        (result, error) =>
                                {
                                    if (error is { } err)
                                        tcs.TrySetException(new SwiftException(err.ToString()));
                                    else
                                        tcs.TrySetResult(result);
                                }
                        """;
                    break;
                default:
                    return;
            }

            callArgs.Add(lambdaBody);
            var callArgsString = string.Join(", ", callArgs);

            // Determine if void result (Task without type param requires special handling)
            var awaitResult = resultTypeName != null ? "return await tcs.Task;" : "await tcs.Task;";

            // Propagate SB0001/SB0002 safety attributes from the underlying method
            var safetyAttr = GetSafetyObsoleteAttribute(methodDecl);

            // Emit the overload
            csWriter.WriteLines($$"""
                /// <summary>
                /// Task-returning overload for <see cref="{{baseMethodName}}"/>.
                /// </summary>
                /// <param name="cancellationToken">Cancels the returned Task but does not cancel the underlying operation.</param>
                {{(safetyAttr != null ? safetyAttr + "\n    " : "")}}{{accessModifier}} async {{taskType}} {{asyncMethodName}}({{paramString}})
                {
                    var tcs = new {{tcsType}}(TaskCreationOptions.RunContinuationsAsynchronously);
                    var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                    try
                    {
                        {{baseMethodName}}({{callArgsString}});
                        {{awaitResult}}
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                }
                """);
        }

        /// <summary>
        /// Returns the [Obsolete] attribute string for SB0001/SB0002 safety diagnostics if the method
        /// has JIT risk or missing symbol issues, or null if no safety attribute is needed.
        /// Used to propagate safety attributes to derived methods (e.g., async wrappers).
        /// </summary>
        internal static string? GetSafetyObsoleteAttribute(MethodDecl methodDecl)
        {
            bool hasJitRisk = false;
            var issues = new List<string>();

            if (!methodDecl.UsesCdeclWrapper)
            {
                hasJitRisk = true;
                issues.Add("Uses CallConvSwift P/Invoke (no @_cdecl wrapper available). " +
                    "May crash on Mono runtime. Safe on NativeAOT (PublishAot=true)");
            }

            if (methodDecl.IsMissingExportedSymbol)
            {
                issues.Add("P/Invoke entry point not exported by the library. " +
                    "This method will throw EntryPointNotFoundException at runtime");
            }

            if (issues.Count == 0)
                return null;

            var message = string.Join(". ", issues) + ".";
            var diagnosticId = hasJitRisk ? "SB0001" : "SB0002";
            return $"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\", " +
                $"DiagnosticId = \"{diagnosticId}\", " +
                $"UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/blob/main/src/docs/known-issues-workarounds.md\")]";
        }

        /// <summary>
        /// Checks if the closure handler's callback argument type is compatible with the TCS result type.
        /// The lambda parameter type is determined by the closure handler (e.g., SwiftOptional&lt;SwiftString&gt;),
        /// while the TCS type is the idiomatic C# type (e.g., string?). SwiftArray&lt;T&gt; implements
        /// IReadOnlyList&lt;T&gt; and IEnumerable&lt;T&gt;, so those conversions are compatible when element types match.
        /// </summary>
        private static bool IsCompletionResultCompatible(string closureType, string tcsType)
        {
            if (closureType == tcsType)
                return true;

            // SwiftArray<T> implements IReadOnlyList<T> and IEnumerable<T>, so check element types
            const string arrayPrefix = "Swift.SwiftArray<";
            if (closureType.StartsWith(arrayPrefix) && closureType.EndsWith(">"))
            {
                var elementType = closureType.Substring(arrayPrefix.Length, closureType.Length - arrayPrefix.Length - 1);
                if (tcsType == $"IReadOnlyList<{elementType}>" || tcsType == $"IEnumerable<{elementType}>")
                    return true;
            }

            return false;
        }
    }
}
