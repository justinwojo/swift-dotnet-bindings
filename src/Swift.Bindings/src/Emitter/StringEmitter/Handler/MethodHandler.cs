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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var methodEnv = (MethodEnvironment)env;

            // If the conductor has a P/Invoke helper context and the environment doesn't,
            // create a new environment with the context (for generic type support)
            if (conductor.CurrentPInvokeHelperContext != null && methodEnv.PInvokeHelperContext == null)
            {
                methodEnv = new MethodEnvironment(methodEnv.MethodDecl, methodEnv.TypeDatabase, methodEnv.SiblingPropertyNames, conductor.CurrentPInvokeHelperContext);
            }

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

                    // B6: Catch supported existentials in non-Array bound generics.
                    // Array<any Protocol> has dedicated existential handling in WrapperEmitter.Marshalling
                    // (line 276-285) that correctly converts interface→container. But Dictionary, Set, and
                    // Optional-wrapped non-Array types fall to generic marshalling which produces a type
                    // mismatch between the public interface type and the ABI container type.
                    if (methodEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var supportedExistentialType))
                    {
                        // Allow through ONLY if the outermost bound generic is Array AND the
                        // element type is itself an existential (NamedTypeSpec.IsAny or ProtocolListTypeSpec).
                        // WrapperEmitter.Marshalling (line 276-285) has dedicated existential
                        // handling for direct Array<any Protocol> elements that correctly converts
                        // interface→container. All other shapes (nested existentials in Dictionary,
                        // closures/tuples containing existentials, etc.) break.
                        var outerNamedType = argument.SwiftTypeSpec as NamedTypeSpec;
                        bool isArrayWithDirectExistentialElement = outerNamedType != null &&
                            methodEnv.TypeConversionHandler.IsSwiftArray(outerNamedType) &&
                            outerNamedType.GenericParameters.Count > 0 &&
                            methodEnv.ExistentialHandler.IsExistential(outerNamedType.GenericParameters[0]);
                        if (!isArrayWithDirectExistentialElement)
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
