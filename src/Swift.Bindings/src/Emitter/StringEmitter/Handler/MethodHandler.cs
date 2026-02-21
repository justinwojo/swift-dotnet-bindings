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

            // Skip constructors that need [UnmanagedCallersOnly] callbacks in generic types.
            // DllImport is handled by PInvokeHelperContext hoisting, but callbacks can't be hoisted.
            if (methodEnv.PInvokeHelperContext != null)
            {
                bool hasThunkClosure = methodEnv.MethodDecl.CSSignature.Skip(1)
                    .Where(arg => methodEnv.ClosureHandler.IsClosure(arg))
                    .Any(arg => methodEnv.ClosureHandler.RequiresThunk(
                        methodEnv.ClosureHandler.GetClosureTypeSpec(arg)!));
                if (hasThunkClosure)
                {
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.ParentDecl, SkipReason.GenericTypeCallback,
                        "Constructor requires [UnmanagedCallersOnly] callback inside generic type.");
                    return;
                }
            }

            var isAccessor = methodEnv.MethodDecl.IsAccessor;

            bool hasExistentialArg = false;
            string? firstExistentialType = null;

            foreach (var argument in methodEnv.MethodDecl.CSSignature)
            {
                if (methodEnv.BoundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, methodEnv.MethodDecl.ModuleDecl))
                {
                    var details = $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.";
                    _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: {details}");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.ParentDecl,
                        SkipReason.UnsupportedSignature,
                        details);
                    return;
                }

                if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    continue;
                }

                if (methodEnv.BoundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec))
                {
                    var details = "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.";
                    _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: {details}");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.ParentDecl,
                        SkipReason.UnsatisfiedGenericConstraint,
                        details);
                    return;
                }

                if (methodEnv.BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, methodEnv.MethodDecl, out var constraintDetails))
                {
                    _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: {constraintDetails}");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.ParentDecl,
                        SkipReason.UnsatisfiedGenericConstraint,
                        constraintDetails);
                    return;
                }

                if (methodEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
                {
                    // Allow Optional<any Protocol> with known protocols through to normal emission
                    // (WrapperEmitter.Marshalling handles Optional existential marshalling correctly).
                    var outerNamedType = argument.SwiftTypeSpec as NamedTypeSpec;
                    bool isOptionalWithKnownExistential = outerNamedType != null &&
                        methodEnv.TypeConversionHandler.IsSwiftOptional(outerNamedType) &&
                        outerNamedType.GenericParameters.Count > 0 &&
                        methodEnv.ExistentialHandler.IsExistential(outerNamedType.GenericParameters[0]);
                    if (isOptionalWithKnownExistential)
                    {
                        var innerProtocolList = methodEnv.ExistentialHandler.ToProtocolListTypeSpec(outerNamedType!.GenericParameters[0]);
                        isOptionalWithKnownExistential = innerProtocolList != null &&
                            methodEnv.ExistentialHandler.AllProtocolsHaveTypeRecords(innerProtocolList) &&
                            methodEnv.ExistentialHandler.GetPublicExistentialType(innerProtocolList) != "object";
                        // P1 fix: Mixed compositions where ObjC filtering drops protocols
                        // would produce proxy/container size mismatch at runtime.
                        if (isOptionalWithKnownExistential && innerProtocolList != null)
                        {
                            var filteredCount = innerProtocolList.Protocols.Keys
                                .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
                            if (filteredCount != innerProtocolList.Protocols.Count)
                                isOptionalWithKnownExistential = false;
                        }
                    }

                    if (!isOptionalWithKnownExistential)
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
                    ReportCollector.RecordMemberWrapped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.MangledName,
                        methodEnv.MethodDecl.ParentDecl,
                        "ExistentialBypass",
                        $"Existential parameter(s) omitted; Swift defaults used.");
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

            // C# does not support generic constructors. If the constructor has method-own
            // generic parameters (not inherited from the parent type), skip it.
            if (methodEnv.MethodDecl.IsGeneric)
            {
                var typeParamNames = methodEnv.ParentDecl is TypeDecl td2 && td2.IsGeneric
                    ? new HashSet<string>(td2.GenericParameters.Select(p => p.TypeName))
                    : new HashSet<string>();
                bool hasMethodOwnGenericParams = methodEnv.MethodDecl.GenericParameters
                    .Any(p => !typeParamNames.Contains(p.TypeName));
                if (hasMethodOwnGenericParams)
                {
                    _logger.LogWarning($"Skipping constructor {methodEnv.MethodDecl.Name}: C# does not support generic constructors.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method,
                        methodEnv.MethodDecl.Name,
                        methodEnv.MethodDecl.ParentDecl,
                        SkipReason.UnsupportedSignature,
                        "C# does not support generic constructors with method-own type parameters.");
                    return;
                }
            }

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
                MonoJitRiskDetector.NeedsClosureCdeclWrapper(methodEnv.MethodDecl, methodEnv.ClosureHandler))
            {
                methodEnv.MethodDecl.HasClosureCdeclWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                ClosureEmitter.EmitClosureCdeclSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl);
            }

            // Optional pointer wrapper for constructors with large Optional params.
            // Same constraints as closure Cdecl: frozen struct only, not failable, not async.
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsFailable &&
                !methodEnv.MethodDecl.IsAsync &&
                methodEnv.ParentDecl is StructDecl ctorOptStruct && ctorOptStruct.IsFrozen &&
                methodEnv.BoundGenericsHandler.HasLargeOptionalParams(methodEnv.MethodDecl))
            {
                methodEnv.MethodDecl.HasOptionalPointerWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                OptionalPointerWrapperEmitter.EmitSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl);
            }

            MethodHandler.CheckExportedSymbol(methodEnv);

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler);
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
            ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);

            // Emit constructor overloads for trailing default parameters
            DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, methodEnv, _logger);

            csWriter.WriteLine();
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

            // Skip methods that need [UnmanagedCallersOnly] callbacks in generic types.
            // DllImport is handled by PInvokeHelperContext hoisting, but callbacks can't be hoisted.
            if (methodEnv.PInvokeHelperContext != null)
            {
                bool hasThunkClosure = methodEnv.MethodDecl.CSSignature.Skip(1)
                    .Where(arg => methodEnv.ClosureHandler.IsClosure(arg))
                    .Any(arg => methodEnv.ClosureHandler.RequiresThunk(
                        methodEnv.ClosureHandler.GetClosureTypeSpec(arg)!));
                bool isAsync = methodEnv.MethodDecl.IsAsync;
                if (hasThunkClosure || isAsync)
                {
                    if (!methodEnv.MethodDecl.IsAccessor)
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method, methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl, SkipReason.GenericTypeCallback,
                            "Member requires [UnmanagedCallersOnly] callback inside generic type.");
                    return;
                }
            }

            var isAccessor = methodEnv.MethodDecl.IsAccessor;

            // Skip methods with constraints on protocols with associated types
            // (these protocols generate generic C# interfaces which can't be used as constraints without type arguments)
            if (MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv))
            {
                _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: has constraints on protocols with associated types");
                if (!isAccessor)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl, SkipReason.GenericProtocolConstraint, "Method has constraints on protocols with associated types.");
                }
                return;
            }

            if (!isAccessor)
            {
                foreach (var argument in methodEnv.MethodDecl.CSSignature)
                {
                    if (methodEnv.BoundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, methodEnv.MethodDecl.ModuleDecl))
                    {
                        var details = $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.";
                        _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: {details}");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.UnsupportedSignature,
                            details);
                        return;
                    }

                    if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                    {
                        continue;
                    }

                    if (methodEnv.BoundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec))
                    {
                        var details = "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.";
                        _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: {details}");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.UnsatisfiedGenericConstraint,
                            details);
                        return;
                    }

                    if (methodEnv.BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, methodEnv.MethodDecl, out var constraintDetails))
                    {
                        _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: {constraintDetails}");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.UnsatisfiedGenericConstraint,
                            constraintDetails);
                        return;
                    }

                    if (methodEnv.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
                    {
                        _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: bound generic contains existential type argument '{existentialType}'.");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.UnsupportedExistential,
                            $"Bound generic contains existential type argument '{existentialType}'.");
                        return;
                    }

                    // B6: Catch supported existentials in non-Array/non-Optional bound generics.
                    // Array<any Protocol> has dedicated existential handling in WrapperEmitter.Marshalling
                    // (line 276-285) that correctly converts interface→container.
                    // Optional<any Protocol> is handled by WrapperEmitter.Marshalling (line 311-326).
                    // Dictionary, Set, and other non-Array/non-Optional types fall to generic
                    // marshalling which produces a type mismatch between public interface and ABI container.
                    if (methodEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var supportedExistentialType))
                    {
                        var outerNamedType = argument.SwiftTypeSpec as NamedTypeSpec;
                        bool isArrayWithDirectExistentialElement = outerNamedType != null &&
                            methodEnv.TypeConversionHandler.IsSwiftArray(outerNamedType) &&
                            outerNamedType.GenericParameters.Count > 0 &&
                            methodEnv.ExistentialHandler.IsExistential(outerNamedType.GenericParameters[0]);

                        // Allow Optional<any Protocol> when all protocols have TypeRecords and
                        // the public type is a known interface (not "object" from ObjC/metatype fallback).
                        // P1 fix: Also require filteredCount == originalCount — mixed compositions
                        // where ObjC filtering drops protocols would produce container size mismatch.
                        bool isOptionalWithDirectExistentialElement = false;
                        if (outerNamedType != null &&
                            methodEnv.TypeConversionHandler.IsSwiftOptional(outerNamedType) &&
                            outerNamedType.GenericParameters.Count > 0 &&
                            methodEnv.ExistentialHandler.IsExistential(outerNamedType.GenericParameters[0]))
                        {
                            var innerProtocolList = methodEnv.ExistentialHandler.ToProtocolListTypeSpec(outerNamedType.GenericParameters[0]);
                            isOptionalWithDirectExistentialElement = innerProtocolList != null &&
                                methodEnv.ExistentialHandler.AllProtocolsHaveTypeRecords(innerProtocolList) &&
                                methodEnv.ExistentialHandler.GetPublicExistentialType(innerProtocolList) != "object";
                            if (isOptionalWithDirectExistentialElement && innerProtocolList != null)
                            {
                                var filteredCount = innerProtocolList.Protocols.Keys
                                    .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
                                if (filteredCount != innerProtocolList.Protocols.Count)
                                    isOptionalWithDirectExistentialElement = false;
                            }
                        }

                        if (!isArrayWithDirectExistentialElement && !isOptionalWithDirectExistentialElement)
                        {
                            _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: bound generic contains existential type argument '{supportedExistentialType}' in non-Array context.");
                            ReportCollector.RecordMemberSkipped(
                                BindingItemKind.Method,
                                methodEnv.MethodDecl.Name,
                                methodEnv.MethodDecl.ParentDecl,
                                SkipReason.UnsupportedExistential,
                                $"Bound generic contains existential type argument '{supportedExistentialType}'.");
                            return;
                        }
                    }
                }
            }

            // Try ArraySlice normalization — emits Swift wrapper + normalized C# method
            if (!isAccessor && ArraySliceNormalizationEmitter.TryEmitNormalizedMethod(
                csWriter, swiftWriter, methodEnv, _logger))
            {
                ReportCollector.RecordMemberWrapped(
                    BindingItemKind.Method,
                    methodEnv.MethodDecl.Name,
                    methodEnv.MethodDecl.MangledName,
                    methodEnv.MethodDecl.ParentDecl,
                    "ArraySliceNormalization",
                    "ArraySlice parameters normalized to Array via Swift wrapper.");
                return;
            }

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

            // Set closure Cdecl flags BEFORE WrapperEmitter reads P/Invoke signature.
            // NeedsClosureCdeclWrapper() already excludes async methods and opaque return methods.
            // ONLY set flags when no other generator owns the wrapper. When UsesWrapperLibrary is
            // already true (DefaultParam, ArraySlice, etc.), their Swift wrappers use @_silgen_name
            // which forces original ABI — closure params must remain native Swift types.
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                MonoJitRiskDetector.NeedsClosureCdeclWrapper(methodEnv.MethodDecl, methodEnv.ClosureHandler))
            {
                methodEnv.MethodDecl.HasClosureCdeclWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                ClosureEmitter.EmitClosureCdeclSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl);
            }

            // Optional pointer wrapper for methods with large Optional params (e.g., Optional<String>)
            // or large Optional returns (e.g., Optional<String> → 16 bytes, exceeds IntPtr capacity).
            // Excluded: async (own wrapper), already-wrapped methods,
            // opaque returns (their own _opaque wrapper doesn't handle Optional param rewriting).
            // Accessors: getters have no params beyond return so HasLargeOptionalParams returns false;
            // setters with large Optional value params are handled (property assignment in Swift wrapper).
            // NOTE: If a concrete SubscriptHandler is added in the future, revisit this — subscript
            // accessors may need different treatment.
            // Mutating: wrapper uses through-pointer access (.pointee.method()) to preserve mutations.
            if (!methodEnv.MethodDecl.UsesWrapperLibrary &&
                !methodEnv.MethodDecl.IsAsync &&
                !_requiresOpaqueReturn(methodEnv) &&
                (methodEnv.BoundGenericsHandler.HasLargeOptionalParams(methodEnv.MethodDecl) ||
                 methodEnv.BoundGenericsHandler.IsLargeOptionalReturn(methodEnv.MethodDecl)))
            {
                methodEnv.MethodDecl.HasOptionalPointerWrapper = true;
                methodEnv.MethodDecl.UsesWrapperLibrary = true;
                methodEnv.MethodDecl.UsesFreeFunctionWrapper = true;
                OptionalPointerWrapperEmitter.EmitSwiftWrapper(swiftWriter, methodEnv, methodEnv.ParentDecl as TypeDecl);
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

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler, fallbackInfo);
            wrapperEmitter.EmitMethod(csWriter, swiftWriter);
            PInvokeEmitter.EmitPInvoke(csWriter, methodEnv, signatureHandler);
            if (isAccessor)
            {
                ReportCollector.RecordMemberSynthesized(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);
            }
            else
            {
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodEnv.MethodDecl.Name, methodEnv.MethodDecl.ParentDecl);
            }

            // Emit default parameter overloads (additional convenience methods)
            DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, methodEnv, _logger);

            // Emit Task-returning overload for callback-based methods (WU8)
            if (!isAccessor)
            {
                TryEmitCompletionHandlerOverload(csWriter, methodEnv);
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
        private void TryEmitCompletionHandlerOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
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
                // Build projected key for the overload (same params minus closure, plus CancellationToken)
                var overloadParamTypes = parameters
                    .Take(parameters.Count - 1)
                    .Select(p =>
                    {
                        var factory = new TypeProjectionFactory();
                        var projection = factory.Project(p.SwiftTypeSpec, new ProjectionContext
                        {
                            TypeDatabase = methodEnv.TypeDatabase,
                            IsParameter = true
                        });
                        if (projection != null) return projection.PublicType;
                        if (methodEnv.TypeDatabase.TryGetTypeRecord(p.SwiftTypeSpec, out var record))
                            return record.CSharpTypeName.FullyQualifiedName;
                        return p.SwiftTypeSpec.ToString();
                    })
                    .ToList();
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
            overloadParams.Add("System.Threading.CancellationToken cancellationToken = default");

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

            // Emit the overload
            csWriter.WriteLines($$"""
                /// <summary>
                /// Task-returning overload for <see cref="{{baseMethodName}}"/>.
                /// </summary>
                /// <param name="cancellationToken">Cancels the returned Task but does not cancel the underlying operation.</param>
                {{accessModifier}} async {{taskType}} {{asyncMethodName}}({{paramString}})
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
