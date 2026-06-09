// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration;

/// <summary>
/// Factory class for creating instances of PropertyHandler.
/// </summary>
public class PropertyHandlerFactory : IFactory<BaseDecl, IPropertyHandler>
{
    private readonly ILogger _handlerLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyHandlerFactory"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory instance.</param>
    public PropertyHandlerFactory(ILoggerFactory loggerFactory)
    {
        _handlerLogger = loggerFactory.CreateLogger<PropertyHandler>();
    }

    public bool Handles(BaseDecl decl)
    {
        return decl is PropertyDecl;
    }

    public IPropertyHandler Construct()
    {
        return new PropertyHandler(_handlerLogger);
    }
}

/// <summary>
/// Handler class for property declarations that generates the binding code for Swift properties.
/// </summary>
public class PropertyHandler : BaseHandler, IPropertyHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public PropertyHandler(ILogger logger) : base(logger)
    {
    }

    /// <inheritdoc/>
    public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
    {
        if (baseDecl is not PropertyDecl propertyDecl)
        {
            throw new ArgumentException("The provided decl must be a PropertyDecl.", nameof(baseDecl));
        }
        return new PropertyEnvironment(propertyDecl, typeDatabase);
    }

    /// <inheritdoc/>
    public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
    {
        // This will emit the C# equivalent of the Swift property.
        // To achieve this, the process is divided into the following steps:
        // 1. Check if accessor methods can be emitted (no unsupported types)
        // 2. Emit Accessor Methods: Generate the C# methods that correspond to the Swift property's accessors (getter, setter, etc.).
        // 3. Emit Property Definition: Define the C# property itself, including its type, name, and accessors.
        //    This step utilizes the previously generated accessor methods to implement the property's behavior.

        var propertyEnv = (PropertyEnvironment)env;
        // Inject composition collector into existing ExistentialHandler if not already set.
        // Marshal() creates environments without the collector; Emit() has the context.
        if (context.CompositionCollector != null)
            propertyEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);
        var propertyDecl = propertyEnv.PropertyDecl;

        // Register the per-module SBW_CreateError_{module} helper if this property is a
        // throwing closure (optionally Optional-wrapped), BEFORE the setter's Swift wrapper
        // and the C# binding's wrapper-symbol contract check. The non-optional closure-setter
        // branch in PropertyWrapperEmitter forwards the closure natively without funneling
        // through the adapter, so the C# setter callback's SBW_CreateError reference would
        // otherwise be unregistered → stripped → CS0103. See SwiftErrorMintEmitter.EmitForPropertyIfNeeded.
        SwiftErrorMintEmitter.EmitForPropertyIfNeeded(swiftWriter, propertyDecl, context.GetEmissionContext());

        void SkipProperty(SkipReason reason, string details)
        {
            // Record for binding-report.json AND emit an `// Unsupported:` tombstone in the
            // generated C# so consumers can `grep` to see *why* the property is missing.
            // Mirrors MethodHandler's skip pattern (UnsupportedCommentEmitter.EmitMemberSkipped).
            // Does NOT touch propertyDecl.WasEmitted — that flag is only set after successful
            // accessor emission downstream (line ~808 sync, ~1308 async).
            ReportCollector.RecordMemberSkipped(propertyDecl, reason, details);
            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, reason, details);
        }

        // Pipeline: property-level bound generic gates (bare generic, non-ISwiftObject, unsatisfied constraint)
        var pipeline = new MemberValidationPipeline(propertyEnv.TypeDatabase);
        var propertyValidation = pipeline.ValidatePropertyEmission(propertyDecl, null);
        if (!propertyValidation.ShouldEmit)
        {
            SkipProperty(propertyValidation.Reason ?? SkipReason.Unknown, propertyValidation.Details ?? "");
            return;
        }

        // Handle AsyncStream properties - emit as IAsyncEnumerable<T>
        bool isAsyncStream = propertyEnv.AsyncStreamHandler.IsAsyncStream(propertyDecl.SwiftTypeSpec);
        if (isAsyncStream)
        {
            if (!propertyEnv.AsyncStreamHandler.IsSupportedAsyncStream(propertyDecl.SwiftTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping AsyncStream property {propertyDecl.Name} - element type not supported.");
                SkipProperty(SkipReason.UnsupportedAsyncStream, "AsyncStream element type is not supported.");
                return;
            }
            if (context.PInvokeHelperContext != null)
            {
                SkipProperty(SkipReason.GenericTypeCallback,
                    "AsyncStream property requires [UnmanagedCallersOnly] callback inside generic type.");
                return;
            }
            // Custom actor AsyncStream properties are emitted as `Task { for await e in await __self.prop { ... } }`.
            // The `await` on the property access hops to the actor's serial executor; the stream itself
            // is Sendable and iterates without further isolation. Parameterized-protocol element types
            // are still blocked: the @_cdecl top-level function can't spell iOS 16+ parameterized
            // protocol types at an earlier deployment target.
            if (!propertyDecl.IsStatic && propertyDecl.ParentDecl is ClassDecl { IsActor: true } &&
                WrapperValidation.ContainsParameterizedProtocol(propertyDecl.SwiftTypeSpec, propertyEnv.TypeDatabase))
            {
                SkipProperty(SkipReason.ActorIsolatedAsyncStream,
                    "Actor AsyncStream property has parameterized-protocol element type (@_cdecl wrapper cannot spell iOS 16+ parameterized protocol).");
                return;
            }
            EmitAsyncStreamProperty(csWriter, swiftWriter, propertyEnv, propertyDecl, context.PropertyRenames);
            propertyDecl.WasEmitted = true;
            ReportCollector.RecordMemberEmitted(propertyDecl);
            return;
        }

        // Handle existential types (any Protocol) - check if supported (0-8 protocols)
        bool isExistential = propertyEnv.ExistentialHandler.IsExistential(propertyDecl.SwiftTypeSpec);
        if (isExistential)
        {
            var protocolList = propertyEnv.ExistentialHandler.ToProtocolListTypeSpec(propertyDecl.SwiftTypeSpec);
            if (protocolList == null || !propertyEnv.ExistentialHandler.IsSupportedExistential(protocolList))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported existential (9+ protocols).");
                SkipProperty(SkipReason.UnsupportedExistential, "Existential contains unsupported protocol count.");
                return;
            }
        }

        // Handle Optional-wrapped existential types like (any DataCaching)?
        bool isOptionalExistential = propertyEnv.ExistentialHandler.IsOptionalExistential(propertyDecl.SwiftTypeSpec);
        if (isOptionalExistential)
        {
            var innerProtocolList = propertyEnv.ExistentialHandler.UnwrapOptionalExistential(propertyDecl.SwiftTypeSpec);
            if (innerProtocolList == null || !propertyEnv.ExistentialHandler.IsSupportedExistential(innerProtocolList))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported Optional-wrapped existential.");
                SkipProperty(SkipReason.UnsupportedExistential, "Optional existential contains unsupported protocol count.");
                return;
            }
        }

        // Handle closure properties (property type is a closure/function type)
        bool isClosure = propertyEnv.ClosureHandler.IsClosure(propertyDecl);
        bool isSetterOnlyClosure = false;
        if (isClosure)
        {
            var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl);
            if (closureTypeSpec == null || !propertyEnv.ClosureHandler.IsSupportedClosure(closureTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} with unsupported closure type.");
                SkipProperty(SkipReason.UnsupportedClosure, "Closure type is not supported.");
                return;
            }
            // Async closure-typed properties cannot be safely stored. The async-throwing
            // and async-non-throwing baselines bridge closure *invocation* inside an
            // async outer method frame (Task { await closure(args) } via
            // withCheckedThrowingContinuation). A property setter is sync and only
            // needs to *store* the closure, but Swift has no way to synthesize a
            // (Args...) async (throws) -> T closure value from a C# (funcPtr, context)
            // pair without that bridge. Without the skip, PInvokeEmitter emits a
            // Swift.AnyType placeholder for the setter argument (asyncBridgeEligible
            // is false on accessor frames) and MethodMarshalPlanBuilder + the
            // marshalling emitter disagree on the handle declaration site.
            if (closureTypeSpec.IsAsync)
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} — async closure-typed properties cannot be stored via a sync accessor (no Swift-side closure synthesis from a C# function pointer).");
                SkipProperty(SkipReason.UnsupportedClosure,
                    "Async closure-typed properties cannot be stored via a sync accessor: Swift cannot synthesize a (Args...) async (throws) -> T closure from a C# (funcPtr, context) pair.");
                return;
            }
            // When the closure's parameters can't be marshalled for invocation from C#,
            // or the return type requires unsupported marshalling, emit as setter-only.
            // The setter (callback) marshalling is supported; runtime success depends on
            // whether a @_cdecl wrapper is generated for the setter accessor.
            if (!propertyEnv.ClosureHandler.CanInvokeFromCSharp(closureTypeSpec))
            {
                isSetterOnlyClosure = true;
            }
            if (!isSetterOnlyClosure && !closureTypeSpec.ReturnType.IsEmptyTuple)
            {
                var returnPInvokeType = propertyEnv.ClosureHandler.TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType);
                if (returnPInvokeType == "void*" &&
                    !ClosureEmitter.IsInvokeThunkCompatibleReturn(closureTypeSpec.ReturnType, propertyEnv.ClosureHandler))
                {
                    isSetterOnlyClosure = true;
                }
            }
            // Setter-only closures require a setter accessor — skip if none available.
            if (isSetterOnlyClosure && !propertyDecl.Accessors.Any(a => a is SetAccessorDecl))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} — getter-only closure with non-invocable parameters or unsupported return marshalling.");
                SkipProperty(SkipReason.UnsupportedClosure, "Getter-only closure with parameters not invocable from C# or unsupported return type.");
                return;
            }
        }

        bool processed = propertyEnv.TypeDatabase.TryGetTypeRecord(propertyDecl.SwiftTypeSpec, out var typeRecord);

        // Generic type parameters (τ_0_0 etc.) and bound generics (Optional<T>) won't have type records in the database
        bool isGenericTypeParam = TypeSpecHelpers.IsGenericTypeParameter(propertyDecl.SwiftTypeSpec) &&
                                  propertyDecl.ParentDecl is TypeDecl gtParent && gtParent.IsGeneric;
        bool isBoundGeneric = propertyEnv.BoundGenericsHandler.IsBoundGeneric(propertyDecl);

        // Only skip if not an existential, Optional-existential, closure, generic type param, or bound generic
        if (!processed && !isExistential && !isOptionalExistential && !isClosure && !isGenericTypeParam && !isBoundGeneric)
        {
            _logger.LogWarning($"PropertyHandler: Couldn't process property {propertyDecl.Name} of type {propertyDecl.SwiftTypeSpec}. Skipping.");
            SkipProperty(SkipReason.UnsupportedType, $"Type resolution failed for property type '{propertyDecl.SwiftTypeSpec}'.");
            return;
        }

        if (propertyDecl.Accessors.Count == 0)
        {
            // No public accessors, so we don't need to emit anything
            SkipProperty(SkipReason.UnsupportedType, "Property has no public accessors to emit.");
            return;
        }

        // Bare generic, non-ISwiftObject, and unsatisfied constraint gates are now
        // in MemberValidationPipeline.ValidatePropertyEmission (called above).

        // Build generic context early — needed for bound generic translation, factory projection, and getter/setter emission.
        var propertyGenericContext = propertyDecl.ParentDecl is TypeDecl propParentType && propParentType.IsGeneric
            ? GenericContext.FromType(propParentType)
            : (GenericContext?)null;

        string csTypeName;
        if (isExistential)
        {
            var protocolList = propertyEnv.ExistentialHandler.ToProtocolListTypeSpec(propertyDecl.SwiftTypeSpec)!;
            csTypeName = propertyEnv.ExistentialHandler.GetPublicExistentialType(protocolList);
        }
        else if (isOptionalExistential)
        {
            var innerProtocolList = propertyEnv.ExistentialHandler.UnwrapOptionalExistential(propertyDecl.SwiftTypeSpec)!;
            var publicInnerType = propertyEnv.ExistentialHandler.GetPublicExistentialType(innerProtocolList);
            if (publicInnerType == "object")
            {
                // Inner protocol not in TypeDatabase — can't properly convert
                // SwiftOptional<ExistentialContainer> to a meaningful nullable type.
                _logger.LogWarning($"PropertyHandler: Skipping optional existential property {propertyDecl.Name} - inner protocol resolves to fallback.");
                SkipProperty(SkipReason.AnyTypeFallback, "Optional existential inner protocol not in TypeDatabase.");
                return;
            }
            csTypeName = propertyEnv.ExistentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
        }
        else if (isClosure)
        {
            var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl)!;
            // Check if it's an optional closure and use nullable delegate type if so
            bool isOptionalClosure = propertyEnv.ClosureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec);
            csTypeName = isOptionalClosure
                ? propertyEnv.ClosureHandler.GetCSharpOptionalDelegateType(propertyDecl.SwiftTypeSpec)
                : propertyEnv.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
        }
        else if (propertyEnv.BoundGenericsHandler.IsBoundGeneric(propertyDecl))
        {
            // Non-ISwiftObject and unsatisfied constraint gates moved to pipeline.
            // Existential check stays inline (feeds skip logic that requires emission context).
            if (propertyEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(propertyDecl.SwiftTypeSpec, out var existentialType))
            {
                if (!propertyEnv.BoundGenericsHandler.IsContainerWithSupportedDirectExistential(propertyDecl.SwiftTypeSpec))
                {
                    _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported existential generic argument '{existentialType}'.");
                    SkipProperty(SkipReason.UnsupportedExistential, $"Bound generic contains existential type argument '{existentialType}'.");
                    return;
                }
            }

            // Bound generic property types use TranslateBoundGenericTypeToCSharp for the raw ABI
            // type name (e.g., SwiftOptional<SwiftArray<int>>). WrapperEmitter uses this raw type for
            // marshalling in getter/setter bodies. The public property type (e.g., IReadOnlyList<int>?)
            // is resolved separately by the factory call below.
            // NOTE: Intentionally uses GenericContext.Empty (not propertyGenericContext) so that generic
            // type params (τ_0_0) resolve to AnyType. This causes the AnyType skip below to fire for
            // bound generics containing unresolvable generic params (e.g., Optional<Array<Foo<τ_0_0>>>).
            // Without this, the property would be emitted with ABI type but WrapperEmitter's getter body
            // applies public type conversions (Array→IReadOnlyList), causing a type mismatch (CS0266).
            // The factory projection (with GenericContext) runs after and correctly overrides csTypeName
            // for types it CAN project (standard containers without user-defined bound generics).
            // Deferred to 5B: once WrapperEmitter is replaced by plan-based rendering, use GenericContext.
            csTypeName = propertyEnv.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(propertyDecl);
        }
        else if (TypeSpecHelpers.IsGenericTypeParameter(propertyDecl.SwiftTypeSpec) &&
                 propertyDecl.ParentDecl is TypeDecl genericParentType && genericParentType.IsGeneric)
        {
            // Property type is a generic type parameter (e.g., T in Wrapper<T>)
            var genCtx = GenericContext.FromType(genericParentType);
            var typeName = (propertyDecl.SwiftTypeSpec as NamedTypeSpec)?.Name;
            if (typeName != null && genCtx.TryResolve(typeName, out var resolved))
                csTypeName = resolved;
            else if (typeRecord != null)
                csTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
            else
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} - generic param not resolvable and no type record.");
                SkipProperty(SkipReason.AnyTypeFallback, $"Generic type parameter not resolvable for property {propertyDecl.Name}.");
                return;
            }
        }
        else if (typeRecord != null)
        {
            csTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
        }
        else
        {
            _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} - type not resolved.");
            SkipProperty(SkipReason.UnsupportedType, $"Property type not resolved for {propertyDecl.Name}.");
            return;
        }

        // Apply idiomatic type projection (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.)
        // This unifies property types with method return types.
        // Skip for existential/optional-existential properties — their types are already
        // determined by ExistentialHandler and shouldn't be overridden by generic Optional handling.
        // For bound generics, the factory with GenericContext may project correctly even if the raw
        // ABI name contains AnyType (e.g., Optional<τ_0_0> → T0? when GenericContext resolves τ_0_0).
        if (!isExistential && !isOptionalExistential)
        {
            var projection = s_projectionFactory.Project(propertyDecl.SwiftTypeSpec, new ProjectionContext
            {
                TypeDatabase = propertyEnv.TypeDatabase,
                IsParameter = false,
                GenericContext = propertyGenericContext
            });
            if (projection != null)
            {
                csTypeName = projection.PublicType;
            }
        }

        // F1: Narrow nint/nuint properties to int/uint for idiomatic C#.
        // P/Invoke accessor methods still use nint; getter/setter add casts.
        // Only narrow non-existential, non-closure properties.
        bool isNarrowedNint = false;
        string? nativePropertyType = null;
        if (!isExistential && !isOptionalExistential && !isClosure)
        {
            var narrowed = NativeIntOverloadEmitter.NarrowNativeIntType(csTypeName);
            if (narrowed != csTypeName)
            {
                nativePropertyType = csTypeName;
                csTypeName = narrowed;
                isNarrowedNint = true;
            }
        }

        // Skip properties with AnyType - the accessor methods will be skipped due to unsupported types.
        // This check runs AFTER factory projection, so types that the factory can resolve (e.g.,
        // Optional<τ_0_0> with GenericContext) won't be incorrectly skipped.
        if (csTypeName.Contains("AnyType"))
        {
            _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported AnyType in type {csTypeName}.");
            SkipProperty(SkipReason.AnyTypeFallback, $"Property type resolved to AnyType ({csTypeName}).");
            return;
        }

        // Async properties: emit as Task-returning methods (C# properties can't be async).
        // Route the async getter through MethodHandler's async emission pipeline.
        if (propertyDecl.Accessors.Any(a => a.Method.IsAsync))
        {
            EmitAsyncPropertyAsMethods(csWriter, swiftWriter, propertyDecl, propertyEnv, conductor, context);
            return;
        }

        // Get the C# property name, handling reserved keywords, special cases, and type collisions.
        // Property/nested-type collisions are resolved by renaming the property (not the type),
        // computed by ComputePropertyRenames in the parent type handler.
        string? containingTypeName = (propertyDecl.ParentDecl as TypeDecl)?.Name;
        var baseName = NameProvider.GetPropertyName(propertyDecl.Name, containingTypeName);
        var propertyName = NameProvider.GetFinalMemberName(baseName, context.PropertyRenames);

        // For setter-only closures, filter out getter accessors — the callback (setter) path works
        // but the getter (invocation) path can't marshal the closure's non-primitive parameters.
        var accessorsToEmit = isSetterOnlyClosure
            ? propertyDecl.Accessors.Where(a => a is SetAccessorDecl).ToList()
            : propertyDecl.Accessors.ToList();

        // Issue #33: Determine wrapper strategy (thunk / @_cdecl / ObjC override) BEFORE the preflight.
        // The preflight's `ContainsPlaceholder` check builds the accessor signature by calling
        // `SignatureHandler.GetWrapperSignature()`, which routes through
        // `MethodSignature.HandleReturnType()`. When the accessor will be emitted through the
        // @_cdecl property-wrapper path, HandleReturnType's String-Utf8Slice branch (line ~433)
        // and decomposed-optional branch (line ~443) both gate on `UsesCdeclPropertyWrapper`.
        // Previously that flag was only set inside the real emission loop (~579), so preflight
        // silently took the factory-projection path, passed, then real emission went down a
        // different branch that could resolve the return type to AnyType — emitting an orphaned
        // accessor P/Invoke with no matching public property body. Compute wrapper eligibility
        // here and propagate `UsesCdeclPropertyWrapper` to the MethodDecl before preflight so
        // both phases see the same `HandleReturnType` branch.
        // AccessorDecl is a record — structural equality/hashing includes Method, whose
        // UsesCdeclPropertyWrapper flag we mutate below. Keying the dictionary on the record
        // would invalidate the hash as soon as we set that flag. Use reference equality so the
        // emission loop can still locate entries by the original accessor instance.
        var accessorThunkFlags = new Dictionary<AccessorDecl, bool>(ReferenceEqualityComparer.Instance);
        var accessorCdeclFlags = new Dictionary<AccessorDecl, bool>(ReferenceEqualityComparer.Instance);
        bool needsObjCOverrideWrapper = false;
        foreach (var accessor in accessorsToEmit)
        {
            bool thunkEligible = false;
            bool cdeclEligible = false;
            accessor.Method.IsAccessor = true;
            if (conductor.TryGetMethodHandler(accessor.Method, out var preflightCheckHandler))
            {
                var preflightCheckEnv = (MethodEnvironment)preflightCheckHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                // Try native ARM64 thunk first (preferred over @_cdecl)
                thunkEligible = NativeThunkEmitter.ShouldEmitThunk(preflightCheckEnv);
                if (!thunkEligible)
                {
                    cdeclEligible = WrapperValidation.DeterminePropertyWrapperDecision(propertyDecl, preflightCheckEnv) == WrapperDecision.WrapperRequired;
                    // Only check ObjC override if no accessor got @_cdecl or thunk
                    if (!cdeclEligible && !needsObjCOverrideWrapper)
                        needsObjCOverrideWrapper = ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, preflightCheckEnv);
                }
            }
            accessorThunkFlags[accessor] = thunkEligible;
            accessorCdeclFlags[accessor] = cdeclEligible;

            // Propagate @_cdecl flag to MethodDecl so the preflight's Marshal() sees the same
            // branch selection as real emission. The flag is persistent on the decl — real
            // emission re-sets it to the same value at line 579 (idempotent). If the property
            // is ultimately skipped, the flag is harmless because no accessor emission runs.
            if (cdeclEligible)
                accessor.Method.UsesCdeclPropertyWrapper = true;
        }

        // Check if all accessor methods can be emitted before actually emitting them.
        // If any accessor would be skipped (due to unsupported types like AnyType),
        // skip the entire property to avoid generating a property that references non-existent methods.
        foreach (var accessor in accessorsToEmit)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                // Preflight must mirror actual accessor emission behavior.
                // Note: This mutation is persistent on the MethodDecl graph, but idempotent —
                // property accessor methods should always have IsAccessor = true. The same
                // flag is set again in the emission loop below.
                accessor.Method.IsAccessor = true;
                var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
                {
                    accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase, accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                }
                // Inject composition collector into accessor's ExistentialHandler
                if (context.CompositionCollector != null)
                    accessorEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);
                // Closed-static-factory bypass: parent-PAT constraints inherited by a static
                // accessor whose return type is a fully closed bound generic of the parent
                // are irrelevant — see ClosedStaticFactoryGate for the rule.
                //
                // For the general case, use the accessor-own variant of the predicate:
                // parent-baseline conformances (those inherited from the parent type's
                // generic-param declaration) are ignored regardless of whether the protocol
                // is supported. The parent type's own where clause already governs them,
                // and per-conformer CSM emission supplies witness tables when needed.
                // Only constraints introduced by the accessor's OWN generic parameters
                // (the standard MethodHandler case) still block here.
                if (!ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(propertyDecl, accessor.Method)
                    && MethodValidationGates.HasAccessorOwnUnsupportedProtocolConstraints(accessorEnv))
                {
                    _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} has unsupported protocol constraints.");
                    SkipProperty(SkipReason.GenericProtocolConstraint, $"Accessor '{accessor.Method.Name}' has constraints on protocols with associated types or self requirements.");
                    return;
                }

                if (accessor.Method.CSSignature.Count > 0)
                {
                    var returnArgument = accessor.Method.CSSignature[0];
                    if (accessorEnv.BoundGenericsHandler.IsBoundGeneric(returnArgument) &&
                        accessorEnv.BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint(returnArgument.SwiftTypeSpec, accessor.Method, out var returnConstraintDetails))
                    {
                        _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} return type has unsatisfied generic constraints: {returnConstraintDetails}");
                        SkipProperty(SkipReason.UnsatisfiedGenericConstraint, $"Accessor '{accessor.Method.Name}' return type: {returnConstraintDetails}");
                        return;
                    }
                }

                foreach (var argument in accessor.Method.CSSignature.Skip(1))
                {
                    if (!accessorEnv.BoundGenericsHandler.IsBoundGeneric(argument))
                    {
                        continue;
                    }

                    if (accessorEnv.BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, accessor.Method, out var parameterConstraintDetails))
                    {
                        _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} parameter has unsatisfied generic constraints: {parameterConstraintDetails}");
                        SkipProperty(SkipReason.UnsatisfiedGenericConstraint, $"Accessor '{accessor.Method.Name}' parameter: {parameterConstraintDetails}");
                        return;
                    }
                }

                var signatureHandler = new SignatureHandler(accessorEnv);
                if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
                {
                    _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} has unsupported signature.");
                    SkipProperty(SkipReason.UnsupportedSignature, $"Accessor '{accessor.Method.Name}' has unsupported signature.");
                    return;
                }
            }
            else
            {
                _logger.LogWarning($"No handler found for property accessor {accessor.Method.Name}. Skipping property {propertyDecl.Name}.");
                SkipProperty(SkipReason.MissingHandler, $"No method handler for accessor '{accessor.Method.Name}'.");
                return;
            }
        }

        // Wrapper-strategy eligibility (thunk / @_cdecl / ObjC override) was computed before the
        // preflight above (#33). Each accessor's flags live in accessorThunkFlags/accessorCdeclFlags;
        // `needsObjCOverrideWrapper` holds the shared ObjC-override decision.

        // Track property wrapper strategy and skip reasons for emission report (per accessor).
        if (WrapperValidation.IsXCFrameworkMode(propertyEnv.TypeDatabase))
        {
            foreach (var acc in accessorsToEmit)
            {
                if (accessorThunkFlags.TryGetValue(acc, out var thunk) && thunk)
                {
                    context.GetEmissionContext().IncrementWrapperStrategy("NativeThunk");
                }
                else if (accessorCdeclFlags.TryGetValue(acc, out var cdecl) && cdecl)
                {
                    context.GetEmissionContext().IncrementWrapperStrategy("CdeclProperty");
                }
                else
                {
                    context.GetEmissionContext().IncrementWrapperStrategy("DirectCdecl");
                    if (conductor.TryGetMethodHandler(acc.Method, out var skipCheckHandler))
                    {
                        var skipCheckEnv = (MethodEnvironment)skipCheckHandler.Marshal(acc.Method, propertyEnv.TypeDatabase);
                        var skipReason = PropertyWrapperEmitter.GetRejectionReason(propertyDecl, skipCheckEnv);
                        if (skipReason != null)
                            context.GetEmissionContext().IncrementWrapperSkipReason(skipReason);
                    }
                }
            }
        }

        // Note: properties without @_cdecl wrappers or native thunks may crash at runtime
        // with non-blittable parameters. They are still emitted (suppression would break
        // protocol conformance CS0535). Do NOT call RecordMemberSkipped here —
        // the member IS emitted, and marking it skipped prevents RecordMemberEmitted from
        // tracking it, causing incorrect coverage data.

        // Now emit the accessor methods using MethodHandler
        foreach (var accessor in accessorsToEmit)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                // Mark the method as an accessor to prevent type conversions
                // Type conversions would cause a mismatch between property type and accessor return/param types
                accessor.Method.IsAccessor = true;

                // Native ARM64 thunk: set flags BEFORE Marshal/Emit.
                // If EmitThunk fails, revert flags and fall through to @_cdecl path.
                // Per-accessor thunk eligibility: each accessor independently decides thunk vs @_cdecl.
                bool thunkHandled = false;
                if (accessorThunkFlags.TryGetValue(accessor, out var useThunk) && useThunk &&
                    propertyDecl.ParentDecl is TypeDecl thunkParentType && thunkParentType.SwiftTypeName != null)
                {
                    var originalMangledName = accessor.Method.MangledName;
                    var thunkSymbol = NativeThunkEmitter.GetThunkSymbol(accessor.Method, thunkParentType.SwiftTypeName.Module);
                    accessor.Method.WrapperStrategy = WrapperStrategy.NativeThunk;
                    accessor.Method.UsesWrapperLibrary = true;
                    accessor.Method.MangledName = thunkSymbol;

                    // Emit the thunk assembly — pass the original mangled name since MangledName
                    // has been overwritten with the thunk symbol above
                    var thunkEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                    bool emitted = NativeThunkEmitter.EmitThunk(thunkEnv, thunkParentType.SwiftTypeName.Module, context.GetEmissionContext().AssemblyBuilder, originalMangledName, context.GetEmissionContext().X64AssemblyBuilder);
                    if (emitted)
                    {
                        // Mark as emitted to prevent duplicate emission in MethodHandler.Emit
                        accessor.Method.ThunkAssemblyEmitted = true;
                        thunkHandled = true;
                    }
                    else
                    {
                        // Revert thunk state — fall through to @_cdecl path below
                        accessor.Method.WrapperStrategy = WrapperStrategy.None;
                        accessor.Method.UsesWrapperLibrary = false;
                        accessor.Method.MangledName = originalMangledName;
                    }
                }
                // @_cdecl property wrapper: set flags BEFORE Marshal/Emit so that
                // SignatureHandler and PInvokeEmitter see the updated MangledName and flags.
                // Per-accessor @_cdecl eligibility: each accessor independently decides.
                // Also fires as fallback when thunk emission fails above — in that case,
                // accessorCdeclFlags may not have been computed (only set when thunk was rejected
                // upfront), so we re-evaluate eligibility on-the-fly.
                bool cdeclEligible = accessorCdeclFlags.TryGetValue(accessor, out var useCdecl) && useCdecl;
                if (!thunkHandled && !cdeclEligible && !accessor.Method.UsesCdeclPropertyWrapper)
                {
                    // Thunk failed at emission time — check @_cdecl eligibility now
                    if (conductor.TryGetMethodHandler(accessor.Method, out var fallbackHandler))
                    {
                        var fallbackEnv = (MethodEnvironment)fallbackHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                        cdeclEligible = WrapperValidation.DeterminePropertyWrapperDecision(propertyDecl, fallbackEnv) == WrapperDecision.WrapperRequired;
                        if (cdeclEligible)
                            accessorCdeclFlags[accessor] = true; // Update for downstream bookkeeping (SBW_Free, etc.)
                    }
                }
                if (!thunkHandled && cdeclEligible &&
                    propertyDecl.ParentDecl is TypeDecl parentTypeDecl3 && parentTypeDecl3.SwiftTypeName != null)
                {
                    bool isGetter = accessor is GetAccessorDecl;

                    // Use nested type name (e.g., "OrderContainer.Status") not just leaf name ("Status")
                    // to avoid @_cdecl collisions between nested types with the same name.
                    var nestedTypeName = parentTypeDecl3.SwiftTypeName.ModuleQualifiedName
                        .Substring(parentTypeDecl3.SwiftTypeName.Module.Length + 1);
                    var symbol = PropertyWrapperEmitter.GetAccessorSymbolName(
                        parentTypeDecl3.SwiftTypeName.Module,
                        nestedTypeName,
                        propertyDecl.Name,
                        isGetter);

                    accessor.Method.UsesCdeclPropertyWrapper = true;
                    accessor.Method.UsesWrapperLibrary = true;
                    accessor.Method.UsesFreeFunctionWrapper = true;
                    accessor.Method.MangledName = symbol;

                    // Optional<closure> setter: mark closure params for Cdecl marshalling
                    // so PInvokeEmitter emits IntPtr funcPtr + IntPtr context params.
                    if (!isGetter && propertyDecl.SwiftTypeSpec is NamedTypeSpec optClosureNts &&
                        optClosureNts.Name == "Swift.Optional" && optClosureNts.GenericParameters.Count == 1 &&
                        optClosureNts.GenericParameters[0] is ClosureTypeSpec)
                    {
                        accessor.Method.HasClosureParams = true;
                    }

                    // Get the accessor env for emission
                    var cdeclCheckEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);

                    // Emit the Swift @_cdecl wrapper function
                    if (isGetter)
                    {
                        PropertyWrapperEmitter.EmitSwiftGetterWrapper(
                            swiftWriter, propertyDecl, symbol, cdeclCheckEnv, context.GetEmissionContext());

                        // Emit invoke thunk for closure-returning property getters.
                        // Pass context.GetEmissionContext() directly — cdeclCheckEnv is freshly
                        // marshaled and never receives EmissionContext, so threading it through
                        // env.EmissionContext would silently skip thunk-symbol registration.
                        EmitPropertyClosureInvokeThunkIfNeeded(swiftWriter, propertyDecl, symbol, cdeclCheckEnv,
                            context.GetEmissionContext());
                    }
                    else
                    {
                        PropertyWrapperEmitter.EmitSwiftSetterWrapper(
                            swiftWriter, propertyDecl, symbol, cdeclCheckEnv, context.GetEmissionContext());
                    }
                }
                // ObjC override property wrapper: set flags BEFORE Marshal/Emit so that
                // SignatureHandler and PInvokeEmitter see the updated MangledName and flags.
                // Must happen before methodHandler.Marshal since the environment captures MangledName.
                else if (needsObjCOverrideWrapper && propertyDecl.ParentDecl is TypeDecl parentTypeDecl2 && parentTypeDecl2.SwiftTypeName != null)
                {
                    bool isGetter = accessor is GetAccessorDecl;
                    var symbol = ObjCOverridePropertyWrapperEmitter.GetAccessorSymbolName(
                        parentTypeDecl2.SwiftTypeName.Module,
                        parentTypeDecl2.Name,
                        propertyDecl.Name,
                        isGetter);
                    accessor.Method.UsesWrapperLibrary = true;
                    accessor.Method.UsesFreeFunctionWrapper = true;
                    accessor.Method.MangledName = symbol;

                    // Emit the Swift wrapper function
                    if (isGetter)
                    {
                        ObjCOverridePropertyWrapperEmitter.EmitSwiftGetterWrapper(
                            swiftWriter, propertyDecl, symbol, context.GetEmissionContext());
                    }
                    else
                    {
                        ObjCOverridePropertyWrapperEmitter.EmitSwiftSetterWrapper(
                            swiftWriter, propertyDecl, symbol, context.GetEmissionContext());
                    }
                }

                var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                // Thread PInvokeHelperContext from parent type context into the accessor's environment.
                // PropertyHandler calls methodHandler.Emit directly (bypassing HandleBaseDecl),
                // so we must manually inject the context's PInvokeHelperContext.
                if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
                {
                    accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase, accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                }
                // Inject composition collector into accessor's ExistentialHandler
                if (context.CompositionCollector != null)
                    accessorEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);
                methodHandler.Emit(csWriter, swiftWriter, accessorEnv, conductor, context);
            }
        }

        // @_cdecl property wrapper: emit SBW_Free P/Invoke for string getters (once per type)
        bool anyCdeclWrapper = accessorCdeclFlags.Values.Any(v => v);
        if (anyCdeclWrapper && WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec))
        {
            var typeKey = (propertyDecl.ParentDecl as TypeDecl)?.SwiftTypeName?.ModuleQualifiedName
                ?? propertyDecl.ModuleDecl?.Name ?? "";
            if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, context.GetEmissionContext()))
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, context.GetEmissionContext());
                var moduleName = propertyDecl.ModuleDecl?.Name ?? "";
                var wrapperLibPath = propertyEnv.TypeDatabase.AsyncLibraryName
                    ?? propertyEnv.TypeDatabase.GetLibraryPath(moduleName);
                var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
                if (context.PInvokeHelperContext != null)
                {
                    // CS7042: LibraryImport cannot appear inside generic types.
                    // Collect into PInvokeHelperContext for emission in non-generic helper class.
                    context.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = "SBW_Free",
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        UsePrivateVisibility = false,
                    });
                }
                else
                {
                    PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = "SBW_Free",
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Private,
                    });
                    csWriter.WriteLine();
                }
            }
        }

        TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
        if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(propertyEnv.TypeDatabase, propertyEnv.ClosureHandler, propertyDecl.SwiftTypeSpec, out var foundFallbackInfo))
        {
            fallbackInfo = foundFallbackInfo;
        }

        var staticModifier = propertyDecl.IsStatic ? "static " : string.Empty;

        // Compute virtual/override/sealed override modifier for class instance properties.
        // Can only emit "override" if a resolved ancestor actually has this property in C#.
        // Otherwise CS0115 occurs when the property comes from an external ancestor or was skipped.
        string dispatchModifier = "";
        if (propertyDecl.ParentDecl is ClassDecl classParent && !propertyDecl.IsStatic)
        {
            if (propertyDecl.IsOverride && WrapperEmitter.HasPropertyInResolvedAncestors(classParent, propertyDecl.Name))
            {
                dispatchModifier = propertyDecl.IsFinal ? "sealed override " : "override ";
            }
            else if (!classParent.IsFinal && !propertyDecl.IsFinal)
            {
                dispatchModifier = "virtual ";
            }
        }

        // Then emit the property
        if (fallbackInfo.HasValue)
        {
            UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo.Value);
        }
        XmlDocCommentEmitter.EmitDocComment(csWriter, propertyDecl);
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, propertyDecl, propertyDecl.ParentDecl, emitObsolete: true);
        csWriter.WriteLine($"public {staticModifier}{dispatchModifier}{csTypeName} {propertyName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var getter = accessorsToEmit.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter != null)
        {
            var helperPrefix = context.PInvokeHelperContext != null ? $"{context.PInvokeHelperContext.HelperClassName}." : "";
            EmitGetter(csWriter, getter, propertyEnv, propertyDecl, isExistential, isOptionalExistential, propertyGenericContext,
                isNarrowedNint, csTypeName, helperPrefix);
        }

        var setter = accessorsToEmit.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setter != null)
        {
            EmitSetter(csWriter, setter, propertyEnv, propertyDecl, isExistential, isOptionalExistential, propertyGenericContext,
                isNarrowedNint, nativePropertyType);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // CS0535 fix: When a property was CS0542-renamed (e.g., DatabaseValue → DatabaseValueValue),
        // any conformance interface that declares the original name (DatabaseValue) won't be satisfied.
        // Emit explicit interface implementations to bridge the gap.
        var originalName = NameProvider.GetPropertyName(propertyDecl.Name);
        if (propertyName != originalName && !propertyDecl.IsStatic)
        {
            EmitExplicitInterfaceImplementations(csWriter, propertyDecl, originalName, propertyName, csTypeName, propertyEnv.TypeDatabase, context);
        }

        csWriter.WriteLine();
        propertyDecl.WasEmitted = true;
        ReportCollector.RecordMemberEmitted(propertyDecl);
    }

    /// <summary>
    /// Emits explicit interface implementations when a property was CS0542-renamed.
    /// Searches the parent type's conformances for protocols that declare a property with
    /// the original Swift name, and emits forwarding properties like:
    ///   Type IInterface.OriginalName => RenamedName;
    /// Only emits for interfaces that the type actually implements (validated by GetImplementedInterfaces).
    /// </summary>
    private void EmitExplicitInterfaceImplementations(CSharpWriter csWriter, PropertyDecl propertyDecl,
        string originalName, string renamedName, string csTypeName, ITypeDatabase typeDatabase, TypeHandlerContext context)
    {
        // Get conformances from parent type
        var parentTypeDecl = propertyDecl.ParentDecl as TypeDecl;
        if (parentTypeDecl == null)
            return;

        var conformances = parentTypeDecl switch
        {
            StructDecl s => s.Conformances,
            ClassDecl c => c.Conformances,
            EnumDecl e => e.Conformances,
            _ => null
        };
        if (conformances == null || conformances.Count == 0)
            return;

        var moduleDecl = propertyDecl.ModuleDecl;
        if (moduleDecl == null)
            return;

        // Build the actual list of interfaces this type implements (after all validation gates).
        // Must use a ProtocolConformanceValidator to match the same gates used in the type handler
        // (ShouldEmitConformance + CanFullyImplementProtocol). Without this, we'd emit CS0540
        // for protocols the type conforms to in Swift but that were filtered out during C# emission.
        var validatorEmissionCtx = context.GetEmissionContext();
        var extensionDefaultsIndex = validatorEmissionCtx?.ExtensionDefaultsIndex;
        var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex, validatorEmissionCtx);
        var implementedInterfaces = new HashSet<string>(
            ProtocolConformanceHelper.GetImplementedInterfaces(parentTypeDecl, parentTypeDecl.Name, moduleDecl.Name, typeDatabase, conformanceValidator));

        // For each conformance, check if the protocol declares a property with the original Swift name
        foreach (var conformance in conformances)
        {
            var protocolDecl = moduleDecl.Protocols
                .FirstOrDefault(p => p.SwiftTypeName.Module == conformance.Protocol.Module
                    && p.SwiftTypeName.Name == conformance.Protocol.Name);
            if (protocolDecl == null)
                continue;

            // Check if this protocol declares a property with the same Swift name
            var protocolProperty = protocolDecl.Properties.FirstOrDefault(p => p.Name == propertyDecl.Name);
            if (protocolProperty == null)
                continue;

            var interfaceName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");

            // Only emit explicit implementation if the type actually implements this interface.
            // This prevents CS0540 when a conformance exists in Swift but the interface was
            // filtered out during C# emission (e.g., protocol not in TypeDatabase).
            if (!implementedInterfaces.Contains(interfaceName))
                continue;

            // Use the protocol property's accessor shape, not the concrete type's.
            // A protocol may declare { get } while the concrete type exposes { get; set; }.
            var hasSetter = protocolProperty.Accessors.OfType<SetAccessorDecl>().Any();

            csWriter.WriteLine();
            csWriter.WriteLine($"{csTypeName} {interfaceName}.{originalName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"get => {renamedName};");
            if (hasSetter)
                csWriter.WriteLine($"set => {renamedName} = value;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
    }

    private static readonly TypeProjectionFactory s_projectionFactory = new();

    /// <summary>
    /// Emits the getter implementation for a property.
    /// Uses TypeProjectionFactory to determine if the property type needs conversion
    /// from Swift ABI to idiomatic C# (e.g., SwiftString → string, SwiftArray → IReadOnlyList).
    /// </summary>
    private void EmitGetter(CSharpWriter csWriter, GetAccessorDecl getter, PropertyEnvironment propertyEnv, PropertyDecl propertyDecl,
        bool isExistential = false, bool isOptionalExistential = false, GenericContext? genericContext = null,
        bool isNarrowedNint = false, string? narrowedTypeName = null, string helperPrefix = "")
    {
        var methodName = NameProvider.GetMethodName(getter.Method.Name, null);

        // Existential/optional-existential properties: accessor methods already handle
        // proxy wrapping/unwrapping via WrapperEmitter.Return — just delegate directly
        if (isExistential || isOptionalExistential)
        {
            csWriter.WriteLine($"get => {methodName}();");
            return;
        }

        // @_cdecl property wrapper: String getters return SBW_Utf8Slice → decode to string
        if (getter.Method.UsesCdeclPropertyWrapper && WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec))
        {
            csWriter.WriteLine($"get => SwiftMarshal.ReadUtf8Slice({methodName}());");
            return;
        }

        var projection = s_projectionFactory.Project(propertyDecl.SwiftTypeSpec,
            new ProjectionContext { TypeDatabase = propertyEnv.TypeDatabase, IsParameter = false, GenericContext = genericContext });
        if (projection != null)
        {
            var (conv, requiresDisposal) = GetAccessorGetterConversion(projection, $"{methodName}()");
            if (conv != null)
            {
                // F1: Wrap projection getter conversion with narrowing cast.
                // For Optional<nint>, projection returns ((nint?)MethodName()) but property is int?.
                if (isNarrowedNint)
                {
                    if (requiresDisposal)
                    {
                        var (usingConv, _) = GetAccessorGetterConversion(projection, "__ret");
                        csWriter.WriteLine($"get {{ using var __ret = {methodName}(); return ({narrowedTypeName})({usingConv}); }}");
                    }
                    else
                    {
                        csWriter.WriteLine($"get => ({narrowedTypeName})({conv});");
                    }
                }
                else
                {
                    if (requiresDisposal)
                    {
                        var (usingConv, _) = GetAccessorGetterConversion(projection, "__ret");
                        csWriter.WriteLine($"get {{ using var __ret = {methodName}(); return {usingConv}; }}");
                    }
                    else
                    {
                        // Check if the expression references the method call more than once
                        // (e.g., Optional<ObjC> ternary). Cache to avoid calling P/Invoke twice (ARC leak).
                        var methodCall = $"{methodName}()";
                        var firstIdx = conv.IndexOf(methodCall, StringComparison.Ordinal);
                        if (firstIdx >= 0 && conv.IndexOf(methodCall, firstIdx + 1, StringComparison.Ordinal) >= 0)
                        {
                            var (cachedConv, _) = GetAccessorGetterConversion(projection, "__ptr");
                            csWriter.WriteLine($"get {{ var __ptr = {methodName}(); return {cachedConv}; }}");
                        }
                        else
                        {
                            csWriter.WriteLine($"get => {conv};");
                        }
                    }
                }
                return;
            }
        }
        // F1: Passthrough path — narrow nint→int with explicit cast
        if (isNarrowedNint)
            csWriter.WriteLine($"get => ({narrowedTypeName}){methodName}();");
        else
            csWriter.WriteLine($"get => {methodName}();");
    }

    /// <summary>
    /// Emits the setter implementation for a property.
    /// Uses TypeProjectionFactory to determine if the property type needs conversion
    /// from idiomatic C# to Swift ABI (e.g., string → SwiftString, IEnumerable → SwiftArray).
    /// </summary>
    private void EmitSetter(CSharpWriter csWriter, SetAccessorDecl setter, PropertyEnvironment propertyEnv, PropertyDecl propertyDecl,
        bool isExistential = false, bool isOptionalExistential = false, GenericContext? genericContext = null,
        bool isNarrowedNint = false, string? nativePropertyType = null)
    {
        var methodName = NameProvider.GetMethodName(setter.Method.Name, null);

        // Setter-specific OS guard: when the ABI JSON marks the setter with a tighter
        // introduced version than the property getter, emit accessor-level
        // [SupportedOSPlatform] so C# consumers cannot call the setter under the
        // getter's lower OS floor. Returns true when it emitted a `#pragma warning
        // disable CA1416` that must be closed with a matching restore after the
        // set accessor body — CA1416 does not narrow the inner backing-method call
        // based on accessor attributes alone.
        var emittedSetterPragma = AvailabilityAttributeEmitter.EmitSetterAccessorAvailability(
            csWriter,
            propertyDecl.AvailabilityAnnotations,
            propertyDecl.SetterAvailabilityAnnotations);
        try
        {
            EmitSetterBody(csWriter, setter, propertyEnv, propertyDecl, isExistential,
                isOptionalExistential, genericContext, isNarrowedNint, nativePropertyType, methodName);
        }
        finally
        {
            if (emittedSetterPragma)
                AvailabilityAttributeEmitter.EmitSetterAccessorAvailabilityEpilogue(csWriter);
        }
    }

    private void EmitSetterBody(CSharpWriter csWriter, SetAccessorDecl setter, PropertyEnvironment propertyEnv, PropertyDecl propertyDecl,
        bool isExistential, bool isOptionalExistential, GenericContext? genericContext,
        bool isNarrowedNint, string? nativePropertyType, string methodName)
    {

        // @_cdecl optional existential setter: marshal value to existential container
        // pointer + hasValue flag. The accessor method accepts (IntPtr, bool) matching
        // the Swift @_cdecl wrapper's decomposed optional parameters.
        if (isOptionalExistential && setter.Method.UsesCdeclPropertyWrapper)
        {
            var innerProtocolList = propertyEnv.ExistentialHandler.UnwrapOptionalExistential(propertyDecl.SwiftTypeSpec);
            if (innerProtocolList != null)
            {
                var containerType = propertyEnv.ExistentialHandler.GetPInvokeExistentialType(innerProtocolList);
                var publicType = propertyEnv.ExistentialHandler.GetPublicExistentialType(innerProtocolList);
                // EC1 uses factory method; EC2+ and well-known types use direct cast
                bool useFactory = containerType == "Swift.Runtime.ExistentialContainer1" &&
                    !propertyEnv.ExistentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out _);
                // Resolve proxy class name so users can assign plain C# implementations of the
                // interface directly (generator auto-wraps them in the hidden {Protocol}Proxy).
                // Skip stdlib/external protocols that project to "object" or lack TypeRecords —
                // no proxy class is emitted for them, so the wrap fallback would not compile.
                string? proxyClassName = null;
                if (useFactory && publicType != "object" &&
                    propertyEnv.ExistentialHandler.AllProtocolsHaveTypeRecords(innerProtocolList) &&
                    propertyEnv.ExistentialHandler.TryGetFilteredProxyClassName(innerProtocolList, out var filteredProxy))
                {
                    proxyClassName = propertyEnv.ExistentialHandler.QualifyProxyClassName(filteredProxy, innerProtocolList);
                }
                // When the factory boxes a value conformer at +1, the @in_guaranteed setter
                // wrapper only borrows the buffer (reads via .pointee, copies into the property), so
                // the caller must run the existential value-witness destroy afterward. Thread the
                // runtime owns-bit; borrowed proxy/class containers (and the non-factory EC2+/
                // well-known path) report owns=false and are only freed, never over-released.
                var createExpr = useFactory
                    ? (proxyClassName != null
                        ? $"global::Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>(__v, static __p => new {proxyClassName}(__p), out __owns)"
                        : $"global::Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>(__v, out __owns)")
                    : $"((global::Swift.Runtime.ISwiftExistentialConvertible<{containerType}>)__v).GetExistentialContainer()";
                var ownsArg = useFactory ? "__owns" : "false";
                var ownsDecl = useFactory ? "\n        bool __owns = false;" : "";

                csWriter.WriteLines($$"""
                    set {
                        unsafe {
                            void* __heap = null;{{ownsDecl}}
                            try {
                                IntPtr __ptr = IntPtr.Zero;
                                bool __hasVal = value != null;
                                if (value is { } __v) {
                                    var __container = {{createExpr}};
                                    __heap = NativeMemory.Alloc((nuint)Unsafe.SizeOf<{{containerType}}>());
                                    Unsafe.Copy(__heap, ref __container);
                                    __ptr = (IntPtr)__heap;
                                }
                                {{methodName}}(__ptr, __hasVal);
                            } finally {
                                global::Swift.Runtime.ExistentialContainerFactory.DestroyAndFreeExistential(__heap, 1, {{ownsArg}});
                            }
                        }
                    }
                    """);
                return;
            }
        }

        // Existential/optional-existential properties: accessor methods already handle
        // proxy wrapping/unwrapping — just delegate directly
        if (isExistential || isOptionalExistential)
        {
            csWriter.WriteLine($"set => {methodName}(value);");
            return;
        }

        // @_cdecl property wrapper: String setters encode to UTF-8 bytes, pin, and pass pointer + length
        if (setter.Method.UsesCdeclPropertyWrapper && WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec))
        {
            csWriter.WriteLines($$"""
                set {
                    var __utf8 = System.Text.Encoding.UTF8.GetBytes(value);
                    unsafe { fixed (byte* __p = __utf8) { {{methodName}}((IntPtr)__p, __utf8.Length); } }
                }
                """);
            return;
        }

        // Decomposed Optional setter: pass raw payload pointer + hasValue flag directly.
        // The accessor method takes (IntPtr payload, bool hasValue) — Swift reconstructs Optional<T>.
        if (setter.Method.UsesCdeclPropertyWrapper &&
            OptionalMarshalClassifier.IsDecomposed(propertyDecl.SwiftTypeSpec, propertyEnv.TypeDatabase))
        {
            // Generic param inner: TValue is unconstrained, so .Payload is not available. Allocate a
            // buffer of TValue.Size bytes, marshal the value into it via SwiftMarshal, and pass the
            // pointer. Mirrors the getter's decomposed buffer layout (resultPtr + hasValuePtr).
            if (propertyDecl.ParentDecl is TypeDecl parentTd && parentTd.IsGeneric &&
                propertyDecl.SwiftTypeSpec is NamedTypeSpec namedOpt &&
                namedOpt.GenericParameters.Count == 1 &&
                namedOpt.GenericParameters[0] is NamedTypeSpec innerNamed &&
                (genericContext ?? GenericContext.Empty).TryResolve(innerNamed.Name, out var innerCs))
            {
                csWriter.WriteLines($$"""
                    set {
                        unsafe {
                            void* __heap = null;
                            try {
                                IntPtr __ptr = IntPtr.Zero;
                                bool __hasVal = value is not null;
                                if (__hasVal) {
                                    var __meta = TypeMetadata.GetTypeMetadataOrThrow<{{innerCs}}>();
                                    __heap = NativeMemory.AllocZeroed((nuint)__meta.Size);
                                    var __span = new System.Span<byte>(__heap, (int)__meta.Size);
                                    SwiftMarshal.MarshalToSwift<{{innerCs}}>(value!, ref __span);
                                    __ptr = (IntPtr)__heap;
                                }
                                {{methodName}}(__ptr, __hasVal);
                            } finally {
                                if (__heap != null) NativeMemory.Free(__heap);
                            }
                        }
                    }
                    """);
                return;
            }
            // Bracket the value's payload with DangerousAddRef/Release via SafeHandlePin
            // for the duration of the P/Invoke. Without the pin, GC running between
            // DangerousGetHandle() and the P/Invoke return could collect `value`, run its
            // SafeHandle finalizer, and free the buffer while Swift is still reading from it.
            // The receiver's own SafeHandle is already bracketed inside the accessor method body.
            //
            // `value is { } __value` unwraps the optional uniformly for both inner kinds: a
            // reference type T? narrows to a non-null T, and a value type Nullable<T> (e.g.
            // AnyHashable?/AnyType? — both are structs that carry a .Payload) unwraps to T. A
            // bare `value.Payload` after `value is not null` is a CS1061 for the Nullable<T> case
            // because the SafeHandle-bearing member lives on T, not Nullable<T>.
            csWriter.WriteLines($$"""
                set {
                    if (value is { } __value) {
                        using var __valuePin = new global::Swift.Runtime.SafeHandlePin(__value.Payload);
                        {{methodName}}(__valuePin.Handle, true);
                    } else {
                        {{methodName}}(IntPtr.Zero, false);
                    }
                }
                """);
            return;
        }

        var projection = s_projectionFactory.Project(propertyDecl.SwiftTypeSpec,
            new ProjectionContext { TypeDatabase = propertyEnv.TypeDatabase, IsParameter = true, GenericContext = genericContext });
        if (projection != null)
        {
            var (conv, requiresDisposal) = GetAccessorSetterConversion(projection, "value");
            if (conv != null)
            {
                // F1: Setter projections use implicit int→nint widening (e.g., SwiftOptional<nint>.NewSome(int)),
                // so no explicit cast needed in the projection path.
                if (requiresDisposal)
                {
                    // ObjC container bridge: method expects IntPtr (.Handle), not the collection object
                    var valArg = projection is ArrayProjection { UsesObjCContainerBridge: true }
                        or SetProjection { UsesObjCContainerBridge: true }
                        or DictionaryProjection { UsesObjCContainerBridge: true }
                        ? "__val.Handle" : "__val";
                    csWriter.WriteLine($"set {{ using var __val = {conv}; {methodName}({valArg}); }}");
                }
                else
                {
                    csWriter.WriteLine($"set => {methodName}({conv});");
                }
                return;
            }
        }
        // F1: Passthrough path — widen int→nint for the accessor method
        if (isNarrowedNint)
            csWriter.WriteLine($"set => {methodName}(({nativePropertyType})value);");
        else
            csWriter.WriteLine($"set => {methodName}(value);");
    }

    /// <summary>
    /// Gets a getter conversion expression by dispatching on projection type via visitor.
    /// Returns (conversion_expression, requires_disposal). Null conversion means passthrough.
    /// </summary>
    private static (string? conversion, bool requiresDisposal) GetAccessorGetterConversion(
        ITypeProjection projection, string resultExpr)
    {
        return projection.Accept(new AccessorGetterConversionVisitor(resultExpr));
    }

    internal static (string? conversion, bool requiresDisposal) GetOptionalAccessorGetterConversion(
        OptionalProjection opt, string resultExpr)
    {
        return AccessorGetterConversionVisitor.OptionalAccessorGetterConversion(opt, resultExpr);
    }

    /// <summary>
    /// Gets a setter conversion expression by dispatching on projection type via visitor.
    /// Returns (conversion_expression, requires_disposal). Null conversion means passthrough.
    /// </summary>
    private static (string? conversion, bool requiresDisposal) GetAccessorSetterConversion(
        ITypeProjection projection, string valueExpr)
    {
        return projection.Accept(new AccessorSetterConversionVisitor(valueExpr));
    }

    internal static (string? conversion, bool requiresDisposal) GetDictAccessorSetterConversion(
        DictionaryProjection dict, string valueExpr)
    {
        return AccessorSetterConversionVisitor.DictSetterConversion(dict, valueExpr);
    }

    internal static (string? conversion, bool requiresDisposal) GetSetAccessorSetterConversion(
        SetProjection set, string valueExpr)
    {
        return AccessorSetterConversionVisitor.SetSetterConversion(set, valueExpr);
    }

    internal static (string? conversion, bool requiresDisposal) GetOptionalAccessorSetterConversion(
        OptionalProjection opt, string valueExpr)
    {
        return AccessorSetterConversionVisitor.OptionalSetterConversion(opt, valueExpr);
    }

    /// <summary>
    /// Emits an AsyncStream property as IAsyncEnumerable&lt;T&gt;.
    /// AsyncStream properties require a Swift wrapper function to iterate the stream
    /// and call C# callbacks for each element.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="swiftWriter">The Swift code writer to emit to</param>
    /// <param name="propertyEnv">The property environment</param>
    /// <param name="propertyDecl">The property declaration</param>
    /// <summary>
    /// Emits async property getters as Task-returning methods.
    /// C# properties cannot be async, so async property getters are emitted as methods
    /// like <c>public Task&lt;T&gt; GetPropertyName(CancellationToken cancellationToken = default)</c>.
    /// Routes through the standard async method emission pipeline (MethodHandler → WrapperEmitter.Async).
    /// </summary>
    private void EmitAsyncPropertyAsMethods(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        PropertyEnvironment propertyEnv,
        Conductor conductor,
        TypeHandlerContext context)
    {
        void SkipProperty(SkipReason reason, string details)
        {
            // Mirror of the sync-path SkipProperty: record for binding-report.json and emit
            // a tombstone in the generated C# so the omission is visible to consumers grepping
            // the output. Does NOT touch WasEmitted (set only on the successful-emission
            // path at the tail of this method).
            ReportCollector.RecordMemberSkipped(propertyDecl, reason, details);
            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, reason, details);
        }

        // Async methods require [UnmanagedCallersOnly] callbacks which are illegal inside
        // generic types (CS8895). Same gate as AsyncStream properties.
        if (context.PInvokeHelperContext != null)
        {
            SkipProperty(SkipReason.GenericTypeCallback,
                "Async property requires [UnmanagedCallersOnly] callback inside generic type.");
            return;
        }

        foreach (var accessor in propertyDecl.Accessors)
        {
            if (!accessor.Method.IsAsync)
                continue; // Only emit async accessors as methods; sync accessors on async properties are skipped

            // Only getters are supported — Swift has no async setters, but guard defensively.
            if (accessor is not GetAccessorDecl)
            {
                _logger.LogWarning($"PropertyHandler: Skipping non-getter async accessor on property {propertyDecl.Name}.");
                continue;
            }

            if (!conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                _logger.LogWarning($"PropertyHandler: No handler for async property accessor {accessor.Method.Name}. Skipping property {propertyDecl.Name}.");
                SkipProperty(SkipReason.MissingHandler, $"No method handler for async accessor '{accessor.Method.Name}'.");
                return;
            }

            // Transform the accessor MethodDecl for method-style emission.
            // Note: These mutations are persistent on the MethodDecl graph (same pattern as line 358),
            // but safe because emission is single-pass and no downstream code re-reads these fields.
            // - AsyncPropertyName carries the Swift property name for the wrapper call expression
            // - Name becomes "get{PropertyName}" → PascalCase → "GetPropertyName" in C#
            // - IsAccessor = false so WrapperEmitter emits a public method (not private accessor)
            // - Visibility = Public so it appears in the public API
            var propertyPascalName = NameProvider.ToPascalCase(propertyDecl.Name);
            accessor.Method.AsyncPropertyName = propertyDecl.Name;
            accessor.Method.Name = $"get{propertyPascalName}";
            accessor.Method.IsAccessor = false;
            accessor.Method.Visibility = Visibility.Public;

            // Propagate the property's @available annotations onto the synthesized async-property
            // method so the @_cdecl wrapper carries them. Ensures wrappers for newly-introduced
            // async properties (e.g., StoreKit's iOS 26.2 AppStore.ageRatingCode) carry matching
            // deployment attributes. SwiftABIParser already copies the property's availability
            // onto every accessor via CreatePropertyDecl — reassigning a fresh copy here is
            // idempotent with that propagation and guards against future parser-side skips.
            if (propertyDecl.AvailabilityAnnotations is { Count: > 0 } propertyAvailability)
            {
                accessor.Method.AvailabilityAnnotations = new List<AvailabilityAnnotation>(propertyAvailability);
            }

            var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
            // Thread PInvokeHelperContext from parent type context
            if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
            {
                accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase, accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
            }
            if (context.CompositionCollector != null)
                accessorEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);

            methodHandler.Emit(csWriter, swiftWriter, accessorEnv, conductor, context);
        }

        propertyDecl.WasEmitted = true;
        ReportCollector.RecordMemberEmitted(propertyDecl);
    }

    private void EmitAsyncStreamProperty(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyEnvironment propertyEnv,
        PropertyDecl propertyDecl,
        Dictionary<string, string>? propertyRenames = null)
    {
        var asyncStreamHandler = propertyEnv.AsyncStreamHandler;
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        var swiftWrapperName = asyncStreamHandler.GetSwiftWrapperFunctionName(propertyDecl);
        var callbackName = $"{propertyDecl.Name}_AsyncStream";

        // Get parent type name for Swift wrapper
        var parentTypeName = propertyDecl.ParentDecl is TypeDecl typeDecl ? typeDecl.Name : "Unknown";

        // Get library path — AsyncStream wrappers are @_cdecl in the wrapper library
        var moduleName = propertyDecl.ParentDecl is TypeDecl td ? td.SwiftTypeName.Module : "Unknown";
        var libraryPath = propertyEnv.TypeDatabase.AsyncLibraryName
            ?? propertyEnv.TypeDatabase.GetLibraryPath(moduleName);

        // Get containing type name for CS0542 collision detection
        string? asyncContainingTypeName = (propertyDecl.ParentDecl as TypeDecl)?.Name;

        // Emit callbacks
        csWriter.WriteLine();
        AsyncStreamEmitter.EmitElementCallback(csWriter, propertyDecl, asyncStreamHandler, callbackName);
        csWriter.WriteLine();
        AsyncStreamEmitter.EmitCompletionCallback(csWriter, propertyDecl, asyncStreamHandler, callbackName);
        csWriter.WriteLine();

        // Emit P/Invoke
        AsyncStreamEmitter.EmitPInvokeDeclaration(csWriter, swiftWrapperName, libraryPath, propertyDecl.IsStatic);
        csWriter.WriteLine();

        // Emit property with collision detection for containing type (CS0542) and nested-type renames
        AsyncStreamEmitter.EmitPropertyGetter(csWriter, propertyDecl, asyncStreamHandler, swiftWrapperName, callbackName, asyncContainingTypeName, propertyRenames);
        csWriter.WriteLine();

        // Emit Swift wrapper
        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, propertyDecl, asyncStreamHandler, swiftWrapperName, parentTypeName);
    }

    /// <summary>
    /// Translates a TypeSpec to its C# type name, including generic type arguments.
    /// This matches the logic in WrapperSignatureBuilder.TranslateTypeSpecForConversion
    /// to ensure generic types like SwiftArray&lt;T&gt; are fully qualified.
    /// </summary>
    internal static string TranslateTypeSpecWithGenerics(TypeSpec typeSpec, ITypeDatabase typeDatabase, GenericContext? genericContext = null)
    {
        // Resolve generic type parameters (τ_0_0 → T0) using GenericContext
        if (genericContext != null && typeSpec is NamedTypeSpec genSpec &&
            TypeSpecHelpers.IsGenericTypeParameter(genSpec.Name) &&
            genericContext.TryResolve(genSpec.Name, out var csName))
        {
            return csName;
        }

        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

            // If the type falls back to AnyType or IntPtr, don't append generic parameters
            if (typeRecord == TypeDatabaseExtensions.AnyType ||
                typeRecord == TypeDatabaseExtensions.IntPtrType)
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Recursively translate generic parameters
            if (namedTypeSpec.GenericParameters.Count > 0)
            {
                var translatedParams = namedTypeSpec.GenericParameters
                    .Select(p => TranslateTypeSpecWithGenerics(p, typeDatabase, genericContext))
                    .ToList();
                return $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
            }

            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            var tupleHandler = new TupleHandler(typeDatabase);
            return genericContext != null
                ? tupleHandler.GetCSharpTupleType(tupleTypeSpec, genericContext)
                : tupleHandler.GetCSharpTupleType(tupleTypeSpec);
        }

        return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Emits a Swift @_cdecl invoke thunk for a property getter that returns a closure.
    /// This is the property analogue of the invoke thunk emission in MethodWrapperEmitter
    /// and ClosureEmitter.SwiftWrapper — needed because property getters have their own
    /// Swift wrapper emission path via PropertyWrapperEmitter.
    /// </summary>
    private static void EmitPropertyClosureInvokeThunkIfNeeded(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? emissionContext = null)
    {
        var closureHandler = env.ClosureHandler;
        if (closureHandler == null) return;

        // Check if the property type is a closure (or optional closure)
        ClosureTypeSpec? closureReturnSpec = propertyDecl.SwiftTypeSpec as ClosureTypeSpec;
        if (closureReturnSpec == null && closureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec))
        {
            if (propertyDecl.SwiftTypeSpec is NamedTypeSpec optNts && optNts.GenericParameters.Count == 1)
                closureReturnSpec = optNts.GenericParameters[0] as ClosureTypeSpec;
        }

        if (closureReturnSpec == null) return;
        if (!closureHandler.IsSupportedClosure(closureReturnSpec)) return;
        if (!ClosureEmitter.CanUseInvokeThunk(closureReturnSpec, closureHandler)) return;

        var thunkEntryPoint = ClosureEmitter.GetInvokeThunkEntryPoint(symbolName);
        var thunkFuncName = $"_sbw_inv_closure_{EmitterUtility.DeterministicHash8(thunkEntryPoint)}";
        // env.EmissionContext is freshly marshaled here and never set; rely on the
        // caller-supplied emissionContext to register the thunk with the wrapper-symbol contract.
        ClosureEmitter.EmitSwiftInvokeThunk(swiftWriter, closureReturnSpec, closureHandler,
            thunkEntryPoint, thunkFuncName, emissionContext ?? env.EmissionContext);
    }
}
