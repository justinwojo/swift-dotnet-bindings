// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Context information needed by the factory to produce a type projection.
/// </summary>
public record ProjectionContext
{
    /// <summary>The type database for type resolution.</summary>
    public required ITypeDatabase TypeDatabase { get; init; }

    /// <summary>Whether this type is being projected as a parameter (true) or return value (false).</summary>
    public bool IsParameter { get; init; }

    /// <summary>Whether the method is async. When true and IsParameter is false, wraps the return projection in AsyncProjection.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Whether the method throws. Used by async projection for error callback generation.</summary>
    public bool Throws { get; init; }

    /// <summary>Unique prefix for callback names. Used by closures and async projections for callback method naming.</summary>
    public string? CallbackNamePrefix { get; init; }

    /// <summary>Optional generic context for resolving τ_0_0 → T0 mappings in bound generic types.</summary>
    public GenericContext? GenericContext { get; init; }

    /// <summary>Optional composition collector for multi-protocol existential interfaces.
    /// When non-null, ExistentialHandler instances will collect composition interface names during projection.</summary>
    public SortedDictionary<string, List<string>>? CompositionCollector { get; init; }

    /// <summary>Optional parent type declaration. When set, enables resolution of "Self" to the concrete type.</summary>
    public TypeDecl? ParentTypeDecl { get; init; }

    /// <summary>Optional module name of the emitting context. When set, existential types from other modules are namespace-qualified.</summary>
    public string? CurrentModuleName { get; init; }

    /// <summary>Optional specialization engine for resolving protocol conformers.
    /// When set, PAT existentials with known conformers use ExistentialUnion instead of object fallback.</summary>
    public ConcreteSpecializationEngine? SpecializationEngine { get; init; }
}

/// <summary>
/// Single entry point for producing type projections.
/// Given a TypeSpec and context, returns the appropriate ITypeProjection
/// that knows how to marshal the type between C# and Swift.
///
/// Supports all Swift type categories:
/// - Simple types (bool, string, enums, ObjC bridged, blittable, non-frozen)
/// - Generic containers (Array, Dictionary, Optional)
/// - Tuples (per-element composition)
/// - Closures (Action/Func with callback declarations)
/// - Protocol existentials (3-tier: well-known, proxy, object)
/// - Async (Task/Task&lt;T&gt; with Swift wrapper and callbacks)
/// </summary>
public class TypeProjectionFactory
{
    /// <summary>
    /// Produces a type projection for the given TypeSpec, or null if the type
    /// is not supported by the factory.
    /// </summary>
    /// <param name="typeSpec">The Swift type to project.</param>
    /// <param name="context">Context for the projection.</param>
    /// <returns>A type projection, or null if unsupported.</returns>
    public ITypeProjection? Project(TypeSpec typeSpec, ProjectionContext context)
    {
        // Async wrapping — must be before all TypeSpec dispatch.
        // When IsAsync && !IsParameter, wrap the inner return projection in AsyncProjection.
        // Strip IsAsync before recursing to prevent double-wrap.
        if (context.IsAsync && !context.IsParameter)
        {
            // Void async methods have empty tuple return → Task (no inner projection)
            if (typeSpec.IsEmptyTuple)
                return new AsyncProjection(null, context.Throws, context.CallbackNamePrefix);

            var innerProjection = Project(typeSpec, context with { IsAsync = false });
            if (innerProjection == null)
                return null;
            return new AsyncProjection(innerProjection, context.Throws, context.CallbackNamePrefix);
        }

        // TypeSpec dispatch (only reached when !IsAsync or IsParameter)
        if (typeSpec is TupleTypeSpec tupleType)
            return ProjectTuple(tupleType, context);

        if (typeSpec is ClosureTypeSpec closureType)
            return ProjectClosure(closureType, context);

        if (typeSpec is ProtocolListTypeSpec protocolList)
            return ProjectExistential(protocolList, context);

        if (typeSpec is NamedTypeSpec namedType)
            return ProjectNamedType(namedType, context);

        return null;
    }

    private ITypeProjection? ProjectNamedType(NamedTypeSpec namedType, ProjectionContext context)
    {
        var name = namedType.Name;

        // Generic type parameter resolution: trust GenericContext over shape-based check
        // (parity with BoundGenericsHandler.TranslateBoundGenericTypeToCSharp). Apple framework
        // ABI JSON emits sugared parameter names ("Value", "Element", "SignedType") directly
        // instead of the τ_0_0 form, so a context hit is the authoritative signal that the
        // typespec is a generic parameter in the current scope — even when IsGenericTypeParameter's
        // shape check would miss it (multi-character non-τ names).
        if (namedType.GenericParameters.Count == 0 && namedType.InnerType == null &&
            context.GenericContext?.TryResolve(name, out var resolvedCsName) == true)
        {
            return new BlittableProjection(resolvedCsName, isGenericParameter: true);
        }

        // Recognized as a generic param by shape but no resolution → can't project.
        if (TypeSpecHelpers.IsGenericTypeParameter(name))
            return null;

        // Parameter packs (Swift 5.9+ variadic generics) can't be projected
        if (name is "repeat")
            return null;

        // Self type: resolve to the concrete parent type when available
        if (name is "Self")
        {
            if (context.ParentTypeDecl == null)
                return null;
            if (!context.TypeDatabase.TryGetTypeRecord(context.ParentTypeDecl.SwiftTypeName, out var selfTypeRecord))
                return null;
            // For generic parent types (e.g., ServiceEntry<TService>), append the mapped C# generic
            // type parameters to the resolved type name. Without this, Self resolves to the bare name
            // "ServiceEntry" instead of "ServiceEntry<TService>", causing CS0305.
            string? typeNameOverride = null;
            if (context.ParentTypeDecl.IsGeneric && context.ParentTypeDecl.GenericParameters.Count > 0)
            {
                var genericContext = context.GenericContext ?? GenericContext.FromType(context.ParentTypeDecl);
                var csGenericParams = context.ParentTypeDecl.GenericParameters
                    .Select(gp => genericContext.TryResolve(gp.TypeName, out var csName) ? csName : gp.TypeName)
                    .ToList();
                typeNameOverride = $"{selfTypeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", csGenericParams)}>";
            }
            return CreateProjectionForTypeRecord(selfTypeRecord, typeNameOverride);
        }

        // Route NamedTypeSpec.IsAny to existential
        if (namedType.IsAny)
        {
            var handler = new ExistentialHandler(context.TypeDatabase, context.CompositionCollector)
            { CurrentModuleName = context.CurrentModuleName };
            var protocolList = handler.ToProtocolListTypeSpec(namedType);
            if (protocolList != null)
                return ProjectExistential(protocolList, context);
            return null;
        }

        // Generic container types
        if (name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
        {
            var inner = namedType.GenericParameters[0];

            // CX-16: Reject nested optionals (Optional<Optional<T>>) — no clean C# representation.
            // Swift allows T?? but C# nullable doesn't nest. Skip these members.
            if (inner is NamedTypeSpec innerOpt && innerOpt.Name == "Swift.Optional")
                return null;

            var isExistentialInner = inner is ProtocolListTypeSpec ||
                (inner is NamedTypeSpec innerNamed && innerNamed.IsAny);

            // Optional inner types always use IsParameter=false (return-style projection).
            // This matches the legacy GetIdiomaticCSharpType behavior where Optional<Dictionary<K,V>>
            // always produces IReadOnlyDictionary (not IDictionary), regardless of outer context.
            var innerProjection = Project(inner, context with { IsParameter = false });
            if (innerProjection == null)
            {
                // Bound-generic optional fallback: Optional<UserType<A,B>> where inner factory
                // returns null (user-defined generics bail at line 184). Resolve via
                // BoundGenericsHandler to get raw C# name, then create projection for the base type.
                if (inner is NamedTypeSpec innerBoundGeneric && innerBoundGeneric.ContainsGenericParameters &&
                    innerBoundGeneric.HasModule() &&
                    !IsStdlibContainer(innerBoundGeneric.Name))
                {
                    var bgh = new BoundGenericsHandler(context.TypeDatabase);
                    var rawCSharpName = bgh.TranslateBoundGenericTypeToCSharp(inner, context.GenericContext ?? GenericContext.Empty);
                    if (!rawCSharpName.Contains("AnyType") &&
                        context.TypeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(innerBoundGeneric.Name), out var baseRecord))
                    {
                        var baseProjection = CreateProjectionForTypeRecord(baseRecord, rawCSharpName);
                        if (baseProjection != null)
                            return new OptionalProjection(baseProjection, isExistentialInner);
                    }
                }

                // Apple framework ObjC class fallback: for Optional<T> where T is from a known
                // Apple framework module AND has an ObjC class naming convention (2-3 letter
                // uppercase prefix like UI/MK/CL/CB/WK/etc.). This dual guard prevents
                // misprojecting Swift value types/enums (e.g., StoreKit.Transaction,
                // Vision.VNConfidence) that live in these same modules but are NOT ObjC classes.
                // Uses ObjCBridgedProjection for nullable pointer ABI (nil = IntPtr.Zero).
                if (inner is NamedTypeSpec innerUnresolved &&
                    innerUnresolved.HasModule() &&
                    !innerUnresolved.ContainsGenericParameters &&
                    !IsStdlibContainer(innerUnresolved.Name) &&
                    !AppleFrameworkRegistry.IsPointerType(innerUnresolved.Name) &&
                    !AppleFrameworkRegistry.IsNestedType(innerUnresolved.Name) &&
                    !TypeDatabaseExtensions.IsKnownAppleValueType(innerUnresolved) &&
                    AppleFrameworkRegistry.IsOptionalFallbackModule(innerUnresolved.Module) &&
                    AppleFrameworkRegistry.HasObjCClassPrefix(innerUnresolved.Name))
                {
                    // Apply Swift→.NET typeRemap (e.g. Foundation.NSMutableURLRequest →
                    // Foundation.NSMutableUrlRequest). Microsoft.iOS uses camelCased "Url" forms
                    // even though the Swift overlay re-exports the all-caps ObjC name; without
                    // this step the projected nullable type references an Foundation namespace
                    // member that doesn't exist in the .NET binding (CS0234).
                    var bridgedName = AppleFrameworkRegistry.TryGetNetTypeName(innerUnresolved.Name, out var remappedName)
                        ? remappedName
                        : innerUnresolved.Name;
                    return new OptionalProjection(
                        new ObjCBridgedProjection(bridgedName), isExistentialInner);
                }

                // Concrete-class fallback for modules that ship Swift-native classes whose
                // names don't always match an ObjC class prefix (RealityFoundation.Entity,
                // RealityKit.AnchorEntity). Mirrors WrapperValidation.IsOptionalWithReferenceInner
                // Path 3 so the C# projection stays in sync with the Swift @_cdecl wrapper's
                // UnsafeMutableRawPointer? signature. Path 2 above already claimed
                // ObjC-prefixed names (SCN*, RE*) with ObjCBridgedProjection; anything reaching
                // here is a non-ObjC Swift class whose C# binding follows the standard
                // ISwiftObject/.Payload SafeHandle shape — so ClassProjection is the matching
                // C# side (`.Payload.DangerousGetHandle()` parameter / MarshalFromSwiftObject
                // return), not ObjCBridgedProjection (which would emit `.Handle` and
                // `GetNSObject<T>` — the NSObject path).
                if (inner is NamedTypeSpec innerConcrete &&
                    innerConcrete.HasModule() &&
                    !innerConcrete.ContainsGenericParameters &&
                    !IsStdlibContainer(innerConcrete.Name) &&
                    !AppleFrameworkRegistry.IsPointerType(innerConcrete.Name) &&
                    !AppleFrameworkRegistry.IsNestedType(innerConcrete.Name) &&
                    !TypeDatabaseExtensions.IsKnownAppleValueType(innerConcrete) &&
                    AppleFrameworkRegistry.IsConcreteClassFallbackModule(innerConcrete.Module))
                {
                    var bridgedName = AppleFrameworkRegistry.TryGetNetTypeName(innerConcrete.Name, out var remappedConcreteName)
                        ? remappedConcreteName
                        : innerConcrete.Name;
                    return new OptionalProjection(
                        new ClassProjection(bridgedName), isExistentialInner);
                }

                return null;
            }
            return new OptionalProjection(innerProjection, isExistentialInner);
        }

        if (name == "Swift.Array" && namedType.GenericParameters.Count == 1)
        {
            var elemProjection = Project(namedType.GenericParameters[0], context)
                ?? TryProjectObjCElement(namedType.GenericParameters[0]);
            if (elemProjection == null)
                return null;
            return new ArrayProjection(elemProjection, context.IsParameter);
        }

        if (name == "Swift.Set" && namedType.GenericParameters.Count == 1)
        {
            var elemProjection = Project(namedType.GenericParameters[0], context)
                ?? TryProjectObjCElement(namedType.GenericParameters[0]);
            if (elemProjection == null)
                return null;
            return new SetProjection(elemProjection, context.IsParameter);
        }

        if (name == "Swift.Dictionary" && namedType.GenericParameters.Count == 2)
        {
            var keyProjection = Project(namedType.GenericParameters[0], context)
                ?? TryProjectObjCElement(namedType.GenericParameters[0]);
            var valueProjection = Project(namedType.GenericParameters[1], context)
                ?? TryProjectObjCElement(namedType.GenericParameters[1]);
            if (keyProjection == null || valueProjection == null)
                return null;
            return new DictionaryProjection(keyProjection, valueProjection, context.IsParameter);
        }

        if (name == "Swift.Result" && namedType.GenericParameters.Count == 2)
        {
            var successProjection = Project(namedType.GenericParameters[0], context);
            var failureProjection = Project(namedType.GenericParameters[1], context);
            if (successProjection == null || failureProjection == null)
                return null;
            return new ResultProjection(successProjection, failureProjection);
        }

        // Swift KeyPath family — reference classes with single-pointer ABI at the @_cdecl
        // boundary. Same shape as Swift.Array/Dictionary/Result (handled by name above the
        // generic class fallback) so Optional<KeyPath<R,V>> composes correctly. Generic args
        // are projected for the public C# type only; runtime marshalling is opaque pass-through.
        if (KeyPathFamilyArities.TryGetValue(name, out var expectedKeyPathArity) &&
            namedType.GenericParameters.Count == expectedKeyPathArity)
        {
            var genericArgPublicTypes = new List<string>(expectedKeyPathArity);
            foreach (var gp in namedType.GenericParameters)
            {
                var argProjection = Project(gp, context with { IsParameter = false });
                if (argProjection == null)
                    return null;
                genericArgPublicTypes.Add(argProjection.PublicType);
            }
            // Strip the "Swift." module prefix for the bare class short name.
            var shortName = name.Substring("Swift.".Length);
            return new KeyPathProjection(shortName, genericArgPublicTypes);
        }

        // Well-known simple types
        if (name == "Swift.Bool")
            return new BoolProjection();

        if (name == "Swift.String")
            return new StringProjection();

        // Foundation.Data is hand-rolled in SwiftBindings.Apple under Swift.Foundation.Data.
        // AppleSupplementResolver short-circuits this identity (not a machine-generated entry),
        // so TryGetTypeRecord does not record the Apple supplement dependency — record it
        // explicitly so the consumer's csproj picks up the PackageReference.
        if (name == "Foundation.Data")
        {
            AppleSupplementReferences.Record("Foundation.Data");
            return new DataProjection();
        }

        // Foundation.Date marshals as a plain Double at the P/Invoke boundary (see
        // DateProjection) and does not reference any Swift.Foundation type in emitted code,
        // so it does NOT require an Apple supplement dependency.
        if (name == "Foundation.Date")
            return new DateProjection();

        // Pointer types are always mapped to System.IntPtr
        if (AppleFrameworkRegistry.IsPointerType(name))
            return new BlittableProjection("System.IntPtr");

        // User-defined types with generic parameters: return null to fall through to
        // TranslateBoundGenericTypeToCSharp which handles raw ABI type names correctly.
        // The factory produces public types (string, IReadOnlyList) for generic args, which
        // violate ISwiftObject constraints on generic type parameters. Deferred to 5B when
        // proper public-vs-raw type distinction is implemented.
        if (namedType.GenericParameters.Count > 0)
            return null;

        // Guard: names without module qualification (no dot) can't be resolved.
        // Test fixtures may use bare names like "SomeUnknownProtocol".
        if (!name.Contains('.'))
            return null;

        // Foundation typealiases to stdlib primitives (e.g., Foundation.TimeInterval → Swift.Double → "double").
        // The ABI parser unwraps a TypeNameAlias node when it's the immediate target of CreateTypeSpec, but
        // when Optional<TimeInterval?> is encoded as a TypeNominal whose printedName is "Foundation.TimeInterval?",
        // the alias name survives in the resulting NamedTypeSpec. There's no TypeRecord for an alias, so the
        // DB lookup below misses and the caller falls back to BoundGenericsHandler, which renders the inner as
        // IntPtr — producing Swift.SwiftOptional<IntPtr> instead of double?. Resolve known aliases to their
        // primitive C# name here so OptionalProjection(BlittableProjection("double")) forms correctly.
        if (MarshallingHelpers.TypeAliasToCSPrimitive.TryGetValue(name, out var aliasCsName))
            return new BlittableProjection(aliasCsName);

        // Try to resolve from the type database
        if (!context.TypeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(name), out var typeRecord))
        {
            // Fall back to extension method ONLY for types whose Swift module maps to a
            // different .NET namespace (e.g., QuartzCore → CoreAnimation). This handles
            // the case where QuartzCore.CALayer needs CoreAnimation.CALayer as its C# name.
            // Don't fall back for UIKit/Foundation types — they resolve via the normal DB
            // and falling through creates projection/marshalling mismatches.
            if (namedType.HasModule())
            {
                var mappedNs = MarshallingHelpers.MapSwiftModuleToNetNamespace(namedType.Module);
                if (mappedNs != namedType.Module)
                {
                    if (!context.TypeDatabase.TryGetTypeRecord(namedType, out typeRecord))
                        return null;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return CreateProjectionForTypeRecord(typeRecord);
    }

    /// <summary>
    /// Creates the appropriate projection for a resolved type record.
    /// Dispatches based on the type's kind and flags (ObjC bridged, enum, class, struct, etc.).
    /// </summary>
    /// <param name="typeRecord">The resolved type record.</param>
    /// <param name="typeNameOverride">Optional override for the C# type name. When set, used instead of
    /// typeRecord.CSharpTypeName (e.g., for bound generics like "DateResult&lt;StringResult&gt;").</param>
    internal ITypeProjection? CreateProjectionForTypeRecord(TypeRecord typeRecord, string? typeNameOverride = null)
    {
        var typeName = typeNameOverride ?? typeRecord.CSharpTypeName.FullyQualifiedName;

        // ObjC bridged types
        if (MarshallingHelpers.IsObjCBridged(typeRecord))
            return new ObjCBridgedProjection(typeName);

        // Simple enums
        if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
        {
            var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
            return new SimpleEnumProjection(typeName, underlyingType);
        }

        // Classes (non-frozen, pointer-based)
        if (typeRecord.Kind == TypeRecordKind.Class)
        {
            if (MarshallingHelpers.IsObjCRooted(typeRecord))
                return new ObjCRootedClassProjection(typeName);
            return new ClassProjection(typeName);
        }

        // ObjC-bridgeable value types (URL → NSUrl via ObjC bridge pointer)
        // Must be checked BEFORE nativeType so URL uses ObjCBridgeableProjection (IntPtr)
        // instead of NativeRemappedProjection (SafeHandle). Data (no objcBridgeable) is unaffected.
        if (typeRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable) && typeRecord.NativeTypeName != null)
            return new ObjCBridgeableProjection(typeRecord.NativeTypeName.FullyQualifiedName);

        // Native remapped types (Data → NSData) — types with nativeType but NOT objcBridgeable
        if (typeRecord.NativeTypeName != null)
        {
            var isFrozen = MarshallingHelpers.IsTypeFrozen(typeRecord);
            var requiresDisposal = MarshallingHelpers.RequiresMemoryManagement(typeRecord);
            var nativeName = typeRecord.NativeTypeName.FullyQualifiedName;
            // Derive factory methods from the native type name suffix (NSUrl → FromNSUrl/ToNSUrl)
            var nativeShortName = nativeName.Contains('.') ? nativeName.Substring(nativeName.LastIndexOf('.') + 1) : nativeName;
            return new NativeRemappedProjection(
                nativeName,
                typeName,
                isFrozen,
                toConversionMethod: $"To{nativeShortName}",
                fromFactoryMethod: $"From{nativeShortName}",
                requiresDisposal: requiresDisposal);
        }

        // Complex enums (non-simple) are C# classes with SafeHandle — not blittable.
        // They need MarshalFromSwift marshalling. P/Invoke returns IntPtr.
        if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return new NonFrozenStructProjection(typeName, useMarshalFromSwift: true);

        // Non-frozen structs/classes
        if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            return new NonFrozenStructProjection(typeName);

        // Blittable frozen types
        if (!MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            return new BlittableProjection(typeName);

        // Frozen with memory management (ClassWithBufferStruct) — P/Invoke returns .Buffer by value
        return new FrozenWithMemoryProjection(typeName);
    }

    private ITypeProjection? ProjectTuple(TupleTypeSpec tupleType, ProjectionContext context)
    {
        if (tupleType.Elements.Count == 0)
            return null;

        var elementProjections = new List<ITypeProjection>();
        foreach (var element in tupleType.Elements)
        {
            var proj = Project(element, context);
            if (proj == null)
                return null;
            elementProjections.Add(proj);
        }

        return new TupleProjection(elementProjections);
    }

    private ITypeProjection? ProjectClosure(ClosureTypeSpec closureType, ProjectionContext context)
    {
        var argProjections = new List<ITypeProjection>();
        foreach (var arg in closureType.EachArgument())
        {
            var proj = Project(arg, context with { IsParameter = true });
            if (proj == null)
                return null;
            argProjections.Add(proj);
        }

        ITypeProjection? returnProjection = null;
        if (closureType.HasReturn())
        {
            returnProjection = Project(closureType.ReturnType, context with { IsParameter = false });
            if (returnProjection == null)
                return null;
        }

        var callbackName = context.CallbackNamePrefix != null
            ? $"{context.CallbackNamePrefix}Callback"
            : "closureCallback";

        return new ClosureProjection(
            argProjections,
            returnProjection,
            closureType.IsEscaping,
            closureType.Throws,
            closureType.IsAsync,
            callbackName);
    }

    private ITypeProjection? ProjectExistential(ProtocolListTypeSpec protocolList, ProjectionContext context)
    {
        var handler = new ExistentialHandler(context.TypeDatabase, context.CompositionCollector)
        {
            CurrentModuleName = context.CurrentModuleName,
            SpecializationEngine = context.SpecializationEngine,
        };
        var containerType = handler.GetCSharpExistentialType(protocolList);
        var publicType = handler.GetPublicExistentialType(protocolList);
        bool isBareAny = handler.IsBareAny(protocolList);
        bool isClassBoundArity1 = handler.IsClassBoundArity1Existential(protocolList);

        // Determine proxy class name:
        // - well-known protocols (e.g. Swift.Error → AnyError): no proxy
        // - "object" fallback: no proxy
        // - bare Any: no proxy (uses Box/Unbox)
        // - ExistentialUnion: no proxy (uses try-cast)
        // - known protocols with interface: has proxy
        string? proxyClassName = null;
        if (!handler.TryGetWellKnownProtocolType(protocolList, out _) &&
            publicType != "object" &&
            publicType != "Swift.Runtime.ExistentialUnion")
        {
            proxyClassName = handler.GetQualifiedProxyClassName(protocolList);
        }

        return new ExistentialProjection(containerType, publicType, proxyClassName, isBareAny, isClassBoundArity1);
    }

    /// <summary>
    /// Determines whether a Swift type name is a stdlib container that has a dedicated projection handler.
    /// These should not be resolved via the bound-generic optional fallback.
    /// </summary>
    private static bool IsStdlibContainer(string name) =>
        name is "Swift.Array" or "Swift.Dictionary" or "Swift.Set" or "Swift.Optional" or "Swift.Result"
            or "Swift.AnyKeyPath" or "Swift.PartialKeyPath" or "Swift.KeyPath"
            or "Swift.WritableKeyPath" or "Swift.ReferenceWritableKeyPath";

    /// <summary>
    /// Generic-parameter arity for each Swift KeyPath family class. Used to gate the
    /// KeyPath projection branch in <see cref="ProjectNamedType"/>.
    /// </summary>
    private static readonly Dictionary<string, int> KeyPathFamilyArities = new()
    {
        { "Swift.AnyKeyPath", 0 },
        { "Swift.PartialKeyPath", 1 },
        { "Swift.KeyPath", 2 },
        { "Swift.WritableKeyPath", 2 },
        { "Swift.ReferenceWritableKeyPath", 2 },
    };

    /// <summary>
    /// True when <paramref name="moduleQualifiedName"/> names a Swift KeyPath family class
    /// (AnyKeyPath / PartialKeyPath / KeyPath / WritableKeyPath / ReferenceWritableKeyPath).
    /// Single source of truth for KeyPath-family identification so ABI category classification
    /// (<see cref="MethodClosureBridge"/>) and projection (this factory) cannot drift.
    /// </summary>
    internal static bool IsKeyPathFamily(string moduleQualifiedName) =>
        KeyPathFamilyArities.ContainsKey(moduleQualifiedName);

    /// <summary>
    /// Returns the generic-parameter arity for a KeyPath family class, or -1 if
    /// <paramref name="moduleQualifiedName"/> isn't a KeyPath family member.
    /// </summary>
    internal static int GetKeyPathArity(string moduleQualifiedName) =>
        KeyPathFamilyArities.TryGetValue(moduleQualifiedName, out var arity) ? arity : -1;

    /// <summary>
    /// Fallback projection for collection element types that are unresolved Apple ObjC classes.
    /// Uses a broader module set than the Optional fallback (includes UIKit/Foundation)
    /// because collection elements are projected by value — no Optional ABI parity concern.
    /// Returns ObjCBridgedProjection if the element is an ObjC class, null otherwise.
    /// </summary>
    private static ITypeProjection? TryProjectObjCElement(TypeSpec elementTypeSpec)
    {
        if (elementTypeSpec is NamedTypeSpec elemNamed &&
            elemNamed.HasModule() &&
            !elemNamed.ContainsGenericParameters &&
            !IsStdlibContainer(elemNamed.Name) &&
            !AppleFrameworkRegistry.IsPointerType(elemNamed.Name) &&
            !AppleFrameworkRegistry.IsNestedType(elemNamed.Name) &&
            AppleFrameworkRegistry.IsKnownModuleForElements(elemNamed.Module) &&
            AppleFrameworkRegistry.HasObjCClassPrefix(elemNamed.Name))
        {
            // Same Swift→.NET typeRemap step as the Optional fallback above: prefer the
            // .NET name (e.g. Foundation.NSUrl) over the raw Swift overlay name when the
            // registry has an explicit remap.
            var bridgedName = AppleFrameworkRegistry.TryGetNetTypeName(elemNamed.Name, out var remappedName)
                ? remappedName
                : elemNamed.Name;
            return new ObjCBridgedProjection(bridgedName);
        }
        return null;
    }

}
