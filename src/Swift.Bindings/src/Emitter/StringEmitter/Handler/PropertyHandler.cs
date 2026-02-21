// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

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
        var propertyDecl = propertyEnv.PropertyDecl;
        void SkipProperty(SkipReason reason, string details)
        {
            ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl, reason, details);
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
            EmitAsyncStreamProperty(csWriter, swiftWriter, propertyEnv, propertyDecl);
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

        if (propertyEnv.BoundGenericsHandler.HasBareGenericUsage(propertyDecl.SwiftTypeSpec, propertyDecl.ModuleDecl))
        {
            _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} - type '{propertyDecl.SwiftTypeSpec}' contains generic declaration used without type arguments.");
            SkipProperty(SkipReason.UnsupportedSignature, $"Type '{propertyDecl.SwiftTypeSpec}' contains generic declaration used without type arguments.");
            return;
        }

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
            if (propertyEnv.BoundGenericsHandler.HasNonSwiftObjectGenericArg(propertyDecl.SwiftTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} - bound generic contains non-ISwiftObject type argument.");
                SkipProperty(SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
                return;
            }

            if (propertyEnv.BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint(propertyDecl.SwiftTypeSpec, propertyDecl, out var constraintDetails))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} - {constraintDetails}");
                SkipProperty(SkipReason.UnsatisfiedGenericConstraint, constraintDetails);
                return;
            }

            if (propertyEnv.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(propertyDecl.SwiftTypeSpec, out var existentialType))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported existential generic argument '{existentialType}'.");
                SkipProperty(SkipReason.UnsupportedExistential, $"Bound generic contains existential type argument '{existentialType}'.");
                return;
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
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(propertyDecl.SwiftTypeSpec, new ProjectionContext
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

        // Get the C# property name, handling reserved keywords, special cases, and type collisions
        // Note: nested type collisions are handled by renaming the nested type (not the property)
        // in the parent type handler via ComputeAndApplyNestedTypeRenames.
        string? containingTypeName = (propertyDecl.ParentDecl as TypeDecl)?.Name;
        var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, containingTypeName);

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
                    accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase, accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext);
                }
                if (MethodValidationGates.HasUnsupportedProtocolConstraints(accessorEnv))
                {
                    _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} has unsupported protocol constraints.");
                    SkipProperty(SkipReason.GenericProtocolConstraint, $"Accessor '{accessor.Method.Name}' has constraints on protocols with associated types.");
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

        // Now emit the accessor methods using MethodHandler
        foreach (var accessor in propertyDecl.Accessors)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                // Mark the method as an accessor to prevent type conversions
                // Type conversions would cause a mismatch between property type and accessor return/param types
                accessor.Method.IsAccessor = true;
                var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                // Thread PInvokeHelperContext from parent type context into the accessor's environment.
                // PropertyHandler calls methodHandler.Emit directly (bypassing HandleBaseDecl),
                // so we must manually inject the context's PInvokeHelperContext.
                if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
                {
                    accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase, accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext);
                }
                methodHandler.Emit(csWriter, swiftWriter, accessorEnv, conductor, context);
            }
        }

        TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
        if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(propertyEnv.TypeDatabase, propertyEnv.ClosureHandler, propertyDecl.SwiftTypeSpec, out var foundFallbackInfo))
        {
            fallbackInfo = foundFallbackInfo;
        }

        var staticModifier = propertyDecl.IsStatic ? "static " : string.Empty;
        // Then emit the property
        if (fallbackInfo.HasValue)
        {
            UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo.Value);
        }
        XmlDocCommentEmitter.EmitDocComment(csWriter, propertyDecl);
        csWriter.WriteLine($"public {staticModifier}{csTypeName} {propertyName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter != null)
        {
            EmitGetter(csWriter, getter, propertyEnv, propertyDecl, isExistential, isOptionalExistential, propertyGenericContext);
        }

        var setter = propertyDecl.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setter != null)
        {
            EmitSetter(csWriter, setter, propertyEnv, propertyDecl, isExistential, isOptionalExistential, propertyGenericContext);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
        ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, propertyDecl.ParentDecl);
    }

    /// <summary>
    /// Emits the getter implementation for a property.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="getter">The getter accessor declaration</param>
    private void EmitGetter(CSharpWriter csWriter, GetAccessorDecl getter, PropertyEnvironment propertyEnv, PropertyDecl propertyDecl,
        bool isExistential = false, bool isOptionalExistential = false, GenericContext? genericContext = null)
    {
        // Use PascalCase method name to match how MethodHandler emits the accessor method
        var methodName = NameProvider.GetMethodName(getter.Method.Name, null);

        // Existential/optional-existential properties: accessor methods already handle
        // proxy wrapping/unwrapping via WrapperEmitter.Return — just delegate directly
        if (isExistential || isOptionalExistential)
        {
            csWriter.WriteLine($"get => {methodName}();");
            return;
        }

        // Apply return type conversion if the property type was converted to an idiomatic type
        Func<TypeSpec, string> typeTranslator = ts => TranslateTypeSpecWithGenerics(ts, propertyEnv.TypeDatabase, genericContext);
        var returnConversion = propertyEnv.TypeConversionHandler.GetReturnConversion($"{methodName}()", propertyDecl.SwiftTypeSpec, typeTranslator);
        if (returnConversion != null)
        {
            // Emit with using disposal if the getter returns an IDisposable wrapper
            if (propertyEnv.TypeConversionHandler.RequiresGetterDisposal(propertyDecl.SwiftTypeSpec))
            {
                var usingReturnConversion = propertyEnv.TypeConversionHandler.GetReturnConversion("__ret", propertyDecl.SwiftTypeSpec, typeTranslator);
                csWriter.WriteLine($"get {{ using var __ret = {methodName}(); return {usingReturnConversion}; }}");
            }
            else
            {
                csWriter.WriteLine($"get => {returnConversion};");
            }
        }
        else
        {
            // Fallback: check for native type remapping (URL → NSUrl, Data → NSData)
            var nativeReturnConversion = propertyEnv.TypeConversionHandler.GetNativeReturnConversion($"{methodName}()", propertyDecl.SwiftTypeSpec);
            if (nativeReturnConversion != null)
            {
                if (propertyEnv.TypeConversionHandler.RequiresGetterDisposal(propertyDecl.SwiftTypeSpec))
                {
                    var usingNativeConversion = propertyEnv.TypeConversionHandler.GetNativeReturnConversion("__ret", propertyDecl.SwiftTypeSpec);
                    csWriter.WriteLine($"get {{ using var __ret = {methodName}(); return {usingNativeConversion}; }}");
                }
                else
                {
                    csWriter.WriteLine($"get => {nativeReturnConversion};");
                }
            }
            else
            {
                csWriter.WriteLine($"get => {methodName}();");
            }
        }
    }

    /// <summary>
    /// Emits the setter implementation for a property.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="setter">The setter accessor declaration</param>
    /// <param name="propertyEnv">The property environment</param>
    /// <param name="propertyDecl">The property declaration</param>
    private void EmitSetter(CSharpWriter csWriter, SetAccessorDecl setter, PropertyEnvironment propertyEnv, PropertyDecl propertyDecl,
        bool isExistential = false, bool isOptionalExistential = false, GenericContext? genericContext = null)
    {
        // Use PascalCase method name to match how MethodHandler emits the accessor method
        var methodName = NameProvider.GetMethodName(setter.Method.Name, null);

        // Existential/optional-existential properties: accessor methods already handle
        // proxy wrapping/unwrapping — just delegate directly
        if (isExistential || isOptionalExistential)
        {
            csWriter.WriteLine($"set => {methodName}(value);");
            return;
        }

        // Apply parameter type conversion if the property type was converted to an idiomatic type
        Func<TypeSpec, string> typeTranslator = ts => TranslateTypeSpecWithGenerics(ts, propertyEnv.TypeDatabase, genericContext);
        var paramConversion = propertyEnv.TypeConversionHandler.GetParameterConversion("value", propertyDecl.SwiftTypeSpec, typeTranslator);
        if (paramConversion != null)
        {
            // Emit with using disposal if the setter creates an IDisposable wrapper
            if (propertyEnv.TypeConversionHandler.RequiresSetterDisposal(propertyDecl.SwiftTypeSpec))
            {
                csWriter.WriteLine($"set {{ using var __val = {paramConversion}; {methodName}(__val); }}");
            }
            else
            {
                csWriter.WriteLine($"set => {methodName}({paramConversion});");
            }
        }
        else
        {
            // Fallback: check for native type remapping (NSUrl → URL, NSData → Data)
            var nativeParamConversion = propertyEnv.TypeConversionHandler.GetNativeParameterConversion("value", propertyDecl.SwiftTypeSpec);
            if (nativeParamConversion != null)
            {
                if (propertyEnv.TypeConversionHandler.RequiresSetterDisposal(propertyDecl.SwiftTypeSpec))
                {
                    csWriter.WriteLine($"set {{ using var __val = {nativeParamConversion}; {methodName}(__val); }}");
                }
                else
                {
                    csWriter.WriteLine($"set => {methodName}({nativeParamConversion});");
                }
            }
            else
            {
                csWriter.WriteLine($"set => {methodName}(value);");
            }
        }
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
        PropertyDecl propertyDecl)
    {
        var asyncStreamHandler = propertyEnv.AsyncStreamHandler;
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        var swiftWrapperName = asyncStreamHandler.GetSwiftWrapperFunctionName(propertyDecl);
        var callbackName = $"{propertyDecl.Name}_AsyncStream";

        // Get parent type name for Swift wrapper
        var parentTypeName = propertyDecl.ParentDecl is TypeDecl typeDecl ? typeDecl.Name : "Unknown";

        // Get library path from type database using the parent type's module
        var moduleName = propertyDecl.ParentDecl is TypeDecl td ? td.SwiftTypeName.Module : "Unknown";
        var libraryPath = propertyEnv.TypeDatabase.GetLibraryPath(moduleName);

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

        // Emit property with collision detection for containing type (CS0542)
        AsyncStreamEmitter.EmitPropertyGetter(csWriter, propertyDecl, asyncStreamHandler, swiftWrapperName, callbackName, asyncContainingTypeName);
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
