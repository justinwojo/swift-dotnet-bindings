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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var methodEnv = (MethodEnvironment)env;

            // If the conductor has a P/Invoke helper context and the environment doesn't,
            // create a new environment with the context (for generic type support)
            if (conductor.CurrentPInvokeHelperContext != null && methodEnv.PInvokeHelperContext == null)
            {
                methodEnv = new MethodEnvironment(methodEnv.MethodDecl, methodEnv.TypeDatabase, methodEnv.SiblingPropertyNames, conductor.CurrentPInvokeHelperContext);
            }

            var isAccessor = methodEnv.MethodDecl.IsAccessor;

            bool hasExistentialArg = false;
            string? firstExistentialType = null;

            foreach (var argument in methodEnv.MethodDecl.CSSignature)
            {
                if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    continue;
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
                    hasExistentialArg = true;
                    firstExistentialType ??= existentialType;
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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var methodEnv = (MethodEnvironment)env;

            // If the conductor has a P/Invoke helper context and the environment doesn't,
            // create a new environment with the context (for generic type support)
            if (conductor.CurrentPInvokeHelperContext != null && methodEnv.PInvokeHelperContext == null)
            {
                methodEnv = new MethodEnvironment(methodEnv.MethodDecl, methodEnv.TypeDatabase, methodEnv.SiblingPropertyNames, conductor.CurrentPInvokeHelperContext);
            }

            var isAccessor = methodEnv.MethodDecl.IsAccessor;

            // Skip methods with constraints on protocols with associated types
            // (these protocols generate generic C# interfaces which can't be used as constraints without type arguments)
            if (HasUnsupportedProtocolConstraints(methodEnv))
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
                    if (!methodEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                    {
                        continue;
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
                        _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: bound generic contains unsupported existential type argument '{existentialType}'.");
                        ReportCollector.RecordMemberSkipped(
                            BindingItemKind.Method,
                            methodEnv.MethodDecl.Name,
                            methodEnv.MethodDecl.ParentDecl,
                            SkipReason.UnsupportedExistential,
                            $"Bound generic contains existential type argument '{existentialType}'.");
                        return;
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

            csWriter.WriteLine();
        }

        /// <summary>
        /// Checks if the method has constraints on protocols with associated types.
        /// Such protocols generate generic C# interfaces which can't be used as constraints without type arguments.
        /// </summary>
        private bool HasUnsupportedProtocolConstraints(MethodEnvironment methodEnv)
        {
            if (!methodEnv.MethodDecl.IsGeneric)
                return false;

            foreach (var param in methodEnv.MethodDecl.GenericParameters)
            {
                foreach (var conformance in param.GenericConformances)
                {
                    if (conformance.Kind == ConformanceKind.Protocol)
                    {
                        if (methodEnv.TypeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                        {
                            if (record.Kind == TypeRecordKind.Protocol &&
                                record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }
    }
}
