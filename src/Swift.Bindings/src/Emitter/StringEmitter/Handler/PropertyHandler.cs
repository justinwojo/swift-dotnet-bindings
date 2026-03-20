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
        void SkipProperty(SkipReason reason, string details)
        {
            ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl, reason, details);
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
            // Skip custom actor instance AsyncStream properties — the wrapper captures `self`
            // as a function parameter and cannot dispatch through the actor's serial executor.
            // @MainActor is NOT blocked: under -strict-concurrency=minimal, nonisolated wrappers
            // can access @MainActor members. The consumer manages thread affinity.
            if (!propertyDecl.IsStatic && propertyDecl.ParentDecl is ClassDecl { IsActor: true })
            {
                SkipProperty(SkipReason.ActorIsolatedAsyncStream,
                    "Custom actor AsyncStream property cannot be wrapped — requires async dispatch through actor executor.");
                return;
            }
            EmitAsyncStreamProperty(csWriter, swiftWriter, propertyEnv, propertyDecl, context.PropertyRenames);
            propertyDecl.WasEmitted = true;
            ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl);
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
        if (isClosure)
        {
            var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl);
            if (closureTypeSpec == null || !propertyEnv.ClosureHandler.IsSupportedClosure(closureTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} with unsupported closure type.");
                SkipProperty(SkipReason.UnsupportedClosure, "Closure type is not supported.");
                return;
            }
            // Check if we can invoke this closure from C# (requires primitive parameters)
            if (!propertyEnv.ClosureHandler.CanInvokeFromCSharp(closureTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} - closure has non-primitive parameters that cannot be marshalled.");
                SkipProperty(SkipReason.UnsupportedClosure, "Closure parameters are not invokable from C#.");
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

        // Detect and skip async properties (properties with async getters/setters are not yet supported)
        if (propertyDecl.Accessors.Any(a => a.Method.IsAsync))
        {
            _logger.LogWarning($"PropertyHandler: Skipping async property {propertyDecl.Name} - async properties are not yet supported.");
            SkipProperty(SkipReason.AsyncProperty, "Property has async getter/setter.");
            return;
        }

        // Get the C# property name, handling reserved keywords, special cases, and type collisions.
        // Property/nested-type collisions are resolved by renaming the property (not the type),
        // computed by ComputePropertyRenames in the parent type handler.
        string? containingTypeName = (propertyDecl.ParentDecl as TypeDecl)?.Name;
        var baseName = NameProvider.GetPropertyName(propertyDecl.Name, containingTypeName);
        var propertyName = NameProvider.GetFinalMemberName(baseName, context.PropertyRenames);

        // Check if all accessor methods can be emitted before actually emitting them.
        // If any accessor would be skipped (due to unsupported types like AnyType),
        // skip the entire property to avoid generating a property that references non-existent methods.
        foreach (var accessor in propertyDecl.Accessors)
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
                if (MethodValidationGates.HasUnsupportedProtocolConstraints(accessorEnv))
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

        // Check if this property needs @_cdecl wrappers BEFORE emitting accessor methods.
        // PropertyWrapperEmitter takes priority; ObjCOverridePropertyWrapperEmitter is fallback.
        bool needsCdeclWrapper = false;
        bool needsObjCOverrideWrapper = false;
        MethodEnvironment? firstAccessorEnv = null;
        if (propertyDecl.Accessors.Count > 0)
        {
            // Use the first accessor for the check (ShouldEmitWrapper only looks at property + class context)
            var firstAccessor = propertyDecl.Accessors[0];
            firstAccessor.Method.IsAccessor = true;
            if (conductor.TryGetMethodHandler(firstAccessor.Method, out var checkHandler))
            {
                firstAccessorEnv = (MethodEnvironment)checkHandler.Marshal(firstAccessor.Method, propertyEnv.TypeDatabase);
                needsCdeclWrapper = WrapperValidation.DeterminePropertyWrapperDecision(propertyDecl, firstAccessorEnv) == WrapperDecision.WrapperRequired;
                // Only check ObjC override if @_cdecl doesn't handle it
                if (!needsCdeclWrapper)
                    needsObjCOverrideWrapper = ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, firstAccessorEnv);
            }
        }

        // Track property wrapper strategy and skip reasons for emission report (one entry per property).
        if (WrapperValidation.IsXCFrameworkMode(propertyEnv.TypeDatabase))
        {
            if (needsCdeclWrapper)
            {
                context.GetEmissionContext().IncrementWrapperStrategy("CdeclProperty");
            }
            else
            {
                context.GetEmissionContext().IncrementWrapperStrategy("LegacyCallConvSwift");
                if (firstAccessorEnv != null)
                {
                    var skipReason = PropertyWrapperEmitter.GetRejectionReason(propertyDecl, firstAccessorEnv);
                    if (skipReason != null)
                        context.GetEmissionContext().IncrementWrapperSkipReason(skipReason);
                }
            }
        }

        // Report properties that use CallConvSwift with non-blittable parameters.
        // Cannot suppress because it would break protocol conformance (CS0535).
        // Properties are emitted but will crash at runtime with InvalidProgramException.
        if (!needsCdeclWrapper && !needsObjCOverrideWrapper && firstAccessorEnv != null)
        {
            if (WrapperValidation.HasNonBlittablePInvokeTypes(firstAccessorEnv, propertyDecl))
            {
                ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl, SkipReason.NonBlittableCallConvSwift, "CallConvSwift property P/Invoke has non-blittable parameters (SafeHandle) — will crash at runtime. @_cdecl wrapper required but not available.");
            }
        }

        // Now emit the accessor methods using MethodHandler
        foreach (var accessor in propertyDecl.Accessors)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                // Mark the method as an accessor to prevent type conversions
                // Type conversions would cause a mismatch between property type and accessor return/param types
                accessor.Method.IsAccessor = true;

                // @_cdecl property wrapper: set flags BEFORE Marshal/Emit so that
                // SignatureHandler and PInvokeEmitter see the updated MangledName and flags.
                if (needsCdeclWrapper && propertyDecl.ParentDecl is TypeDecl parentTypeDecl3 && parentTypeDecl3.SwiftTypeName != null)
                {
                    bool isGetter = accessor is GetAccessorDecl;

                    // Optional<closure> properties: only wrap the getter (IndirectResult buffer).
                    // The setter uses callback thunks that require direct SwiftClosureData passing,
                    // not the UnsafeRawPointer buffer transport used by @_cdecl param mapping.
                    if (!isGetter && propertyDecl.SwiftTypeSpec is NamedTypeSpec optClosureNts &&
                        optClosureNts.Name == "Swift.Optional" && optClosureNts.GenericParameters.Count == 1 &&
                        optClosureNts.GenericParameters[0] is ClosureTypeSpec)
                    {
                        // Leave setter on CallConvSwift — skip @_cdecl wrapping
                    }
                    else
                    {
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

                        // Get the accessor env for emission
                        var cdeclCheckEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);

                        // Emit the Swift @_cdecl wrapper function
                        if (isGetter)
                        {
                            PropertyWrapperEmitter.EmitSwiftGetterWrapper(
                                swiftWriter, propertyDecl, symbol, cdeclCheckEnv, context.GetEmissionContext());
                        }
                        else
                        {
                            PropertyWrapperEmitter.EmitSwiftSetterWrapper(
                                swiftWriter, propertyDecl, symbol, cdeclCheckEnv, context.GetEmissionContext());
                        }
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
        if (needsCdeclWrapper && WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec))
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
                        OmitCallingConvention = true,
                        UsePrivateVisibility = false,
                    });
                }
                else
                {
                    csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{freeSymbol}\")]");
                    csWriter.WriteLine("private static partial void SBW_Free(IntPtr ptr);");
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

        var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter != null)
        {
            var helperPrefix = context.PInvokeHelperContext != null ? $"{context.PInvokeHelperContext.HelperClassName}." : "";
            EmitGetter(csWriter, getter, propertyEnv, propertyDecl, isExistential, isOptionalExistential, propertyGenericContext,
                isNarrowedNint, csTypeName, helperPrefix);
        }

        var setter = propertyDecl.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
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
        ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl);
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
        var extensionDefaultsIndex = context.GetEmissionContext()?.ExtensionDefaultsIndex;
        var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
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
            csWriter.WriteLine($"get {{ var __slice = {methodName}(); " +
                $"if (__slice.Len == 0) return string.Empty; " +
                $"try {{ return global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(__slice.Ptr, (int)__slice.Len) ?? string.Empty; }} " +
                $"finally {{ {helperPrefix}SBW_Free(__slice.Ptr); }} }}");
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
            csWriter.WriteLine($"set => {methodName}(value?.Payload.DangerousGetHandle() ?? IntPtr.Zero, value != null);");
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
                    csWriter.WriteLine($"set {{ using var __val = {conv}; {methodName}(__val); }}");
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
        AsyncStreamEmitter.EmitCompletionCallback(csWriter, callbackName);
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
}
