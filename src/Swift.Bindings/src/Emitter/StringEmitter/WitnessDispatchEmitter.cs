// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Classifies how a method can be dispatched through the witness table.
/// </summary>
public enum MethodDispatchKind
{
    /// <summary>Method cannot be dispatched (unsupported return type, async, etc.).</summary>
    NotDispatchable,
    /// <summary>Method returns blittable or String types (existing dispatch path).</summary>
    BlittableOrString,
    /// <summary>Method returns a protocol existential (new dispatch path with ARC-safe typed pointer).</summary>
    ExistentialReturn,
    /// <summary>Throwing method with blittable/String/void return (error out-parameter pattern).</summary>
    ThrowingBlittableOrString,
    /// <summary>Method returns a Swift class (ARC via Unmanaged.passRetained). Handles throwing internally.</summary>
    ClassReturn,
    /// <summary>Method returns a non-frozen struct or frozen+RefFields struct (indirect result buffer). Handles throwing internally.</summary>
    StructReturn,
    /// <summary>Method returns a bound generic collection (Array, Dictionary, Set). Uses heap-allocated pointer pattern like ExistentialReturn.</summary>
    BoundGenericReturn
}

/// <summary>
/// Pairs a <see cref="MethodDispatchKind"/> with an optional human-readable reason
/// explaining why the method is not dispatchable (null when dispatchable).
/// </summary>
public readonly record struct DispatchClassification(MethodDispatchKind Kind, string? Reason);

/// <summary>
/// Generates Swift @_cdecl accessor functions that reconstruct existential containers
/// and dispatch through the protocol witness table. These accessors enable C# code to call
/// protocol members on Swift-backed existential containers via P/Invoke.
///
/// Phase A scope: blittable property getters, non-mutating methods returning blittable types,
/// non-mutating void methods with blittable parameters.
/// Phase B scope: String property getters/setters, String method params/returns,
/// blittable property setters.
/// Phase C scope: methods returning protocol existentials (throwing/non-throwing).
/// </summary>
public class WitnessDispatchEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;
    private readonly ModuleEmissionContext _emissionContext;

    /// <summary>
    /// Availability heredoc prefix for the protocol currently being emitted. Computed once per
    /// protocol at the top of <see cref="EmitWitnessDispatchFunctions"/> and consumed by every
    /// accessor-emitting method so each top-level @_cdecl function gets @available
    /// annotations matching the protocol's platform requirements. Empty string when the
    /// protocol has no availability constraints.
    /// </summary>
    private string _currentAvailabilityPrefix = string.Empty;

    /// <summary>
    /// Raw availability annotations for the protocol currently being emitted.
    /// Used for non-heredoc emissions via direct writer.WriteLine calls.
    /// </summary>
    private IReadOnlyList<AvailabilityAnnotation>? _currentAvailabilityAnnotations;

    /// <summary>
    /// Emits <c>@available(...)</c> lines directly to <paramref name="writer"/> immediately
    /// before a non-heredoc top-level @_cdecl declaration. Used by accessor methods that
    /// emit their function header through individual writer.WriteLine calls rather than a
    /// single heredoc. No-op when the current protocol has no availability constraints.
    /// </summary>
    private void EmitAvailabilityAttributes(SwiftWriter writer)
    {
        WrapperEmitterHelpers.EmitSwiftAvailability(writer, _currentAvailabilityAnnotations);
    }

    /// <summary>
    /// Set of C# type names that are blittable and can be safely marshalled via Unsafe.Read/Write.
    /// </summary>
    private static readonly HashSet<string> BlittablePrimitiveTypes = new()
    {
        "bool", "System.Boolean",
        "sbyte", "System.SByte",
        "byte", "System.Byte",
        "short", "System.Int16",
        "ushort", "System.UInt16",
        "int", "System.Int32",
        "uint", "System.UInt32",
        "long", "System.Int64",
        "ulong", "System.UInt64",
        "nint", "System.IntPtr",
        "nuint", "System.UIntPtr",
        "float", "System.Single",
        "double", "System.Double",
    };

    /// <summary>
    /// Set of Swift type names that are known blittable primitives.
    /// Used as a fast path before falling back to TypeDatabase lookups.
    /// </summary>
    private static readonly HashSet<string> BlittableSwiftTypes = new()
    {
        "Swift.Int", "Swift.UInt",
        "Swift.Int8", "Swift.UInt8",
        "Swift.Int16", "Swift.UInt16",
        "Swift.Int32", "Swift.UInt32",
        "Swift.Int64", "Swift.UInt64",
        "Swift.Float", "Swift.Double",
        "Swift.Bool",
    };

    /// <summary>
    /// Maps Swift type names to C# type names for resolving types without the type database.
    /// Delegates to <see cref="SwiftBuilder.SwiftToCSharpType"/> (canonical source).
    /// </summary>
    private static readonly Dictionary<string, string> SwiftToCSharpPrimitiveMap = SwiftBuilder.SwiftToCSharpType;

    /// <summary>
    /// Maps C# type names to Swift type names for use in generated Swift code.
    /// Delegates to <see cref="SwiftBuilder.CSharpToSwiftType"/> (canonical source).
    /// </summary>
    private static readonly Dictionary<string, string> CSharpToSwiftTypeMap = SwiftBuilder.CSharpToSwiftType;

    public WitnessDispatchEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName, ModuleEmissionContext? ctx = null)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
        _emissionContext = ctx ?? ModuleEmissionContext.CreateImplicitFallback();
    }

    /// <summary>
    /// Emits all witness dispatch accessor functions for a protocol.
    /// These are Swift functions that reconstruct the existential and dispatch through the witness table.
    /// </summary>
    public void EmitWitnessDispatchFunctions(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var protocolName = protocolDecl.Name;

        // Skip witness dispatch if the conformance was explicitly recorded as not emitted
        // (Self requirements, Self-typed members, no implementable members, etc.).
        // Only check when conformance decisions have been recorded (i.e., EveryProtocolEmitter ran
        // with a shared context). When no decisions are recorded, allow emission for backward compat.
        //
        // Read-only (Swift-vended-only) proxies are the exception: a superclass-constrained
        // protocol gets no EveryProtocol conformance (EveryProtocol can't subclass the required
        // class), yet its witness-dispatch accessors ARE emitted — they reconstruct `any P` via
        // the static type (`containerPtr.load(as: (any P).self)`) and dispatch through the
        // existential's OWN witness table, which needs no EveryProtocol conformance.
        // Conformance marker is keyed on the module-qualified name (matching the recorder);
        // protocolName stays the simple name for IsReadOnlyProxy / logging below.
        //
        // Unlike the proxy-emission policy, carrier existence is deliberately NOT the signal here: a
        // read-only proxy emits its witness-dispatch accessors WITHOUT any EveryProtocol carrier (they
        // reconstruct `any P` and dispatch through the existential's own witness table), and on the
        // empty-suitable-protocol path ModuleHandler only calls this method for read-only protocols —
        // which the IsReadOnlyProxy arm keeps emitting. A non-read-only protocol never reaches here when
        // the carrier is absent, so the count gate has no dangling-symbol hole to close.
        if (_emissionContext.ConformanceDecisions.Count > 0
            && !_emissionContext.WasConformanceEmitted(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolName)
            && !_emissionContext.IsReadOnlyProxy(protocolName))
        {
            _logger.LogDebug("Skipping witness dispatch for {Protocol}: conformance was not emitted", protocolName);
            return;
        }

        var moduleQualifiedName = protocolDecl.SwiftTypeName!.ModuleQualifiedName;

        _currentAvailabilityAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        _currentAvailabilityPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            _currentAvailabilityAnnotations, "            ");

        // Track method indices for overload disambiguation (matching ProtocolProxyEmitter pattern)
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();

        bool anyEmitted = false;

        // Emit the "// Witness dispatch accessors for {protocolName}" header once
        void EnsureHeader()
        {
            if (!anyEmitted)
            {
                writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                anyEmitted = true;
            }
        }

        void EmitUtf8SliceIfNeeded()
        {
            if (NeedsUtf8Slice(protocolDecl))
                Utf8SliceEmitter.EmitIfNeeded(writer, _emissionContext);
        }

        void EmitErrorHelperIfNeeded()
        {
            ErrorDescriptionEmitter.EmitIfNeeded(writer, _moduleName, _emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(writer, _moduleName, _emissionContext);
        }

        // Property getters (skip static properties - not part of witness table)
        var emittedPropertyNames = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            // Eligibility (static / @objc-optional / custom-actor) is the shared predicate so all
            // three witness-dispatch walks agree on which members get an SBW accessor and index.
            if (!IsPropertyWitnessDispatchEligible(property, protocolDecl))
                continue;
            if (!emittedPropertyNames.Add(property.Name + "_get"))
                continue;
            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            if (hasGetter)
            {
                // Property getter dispatch: blittable/string use the blittable accessor,
                // class/struct types use ClassReturn/StructReturn accessor paths
                bool isBlittableOrString = IsTypeBlittable(property.SwiftTypeSpec) || IsStringType(property.SwiftTypeSpec);
                if (isBlittableOrString)
                {
                    EnsureHeader();
                    EmitUtf8SliceIfNeeded();
                    EmitPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyClassReturn(property))
                {
                    EnsureHeader();
                    EmitClassReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyOptionalClassReturn(property))
                {
                    EnsureHeader();
                    EmitOptionalClassReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyStructReturn(property))
                {
                    EnsureHeader();
                    EmitStructReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyCollectionReturn(property))
                {
                    EnsureHeader();
                    EmitCollectionReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
                else if (IsPropertyExistentialReturn(property))
                {
                    EnsureHeader();
                    EmitExistentialReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
                }
            }
        }

        // Property setters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (!IsPropertyWitnessDispatchEligible(property, protocolDecl))
                continue;
            if (!emittedPropertyNames.Add(property.Name + "_set"))
                continue;
            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
            // Property setter dispatch: only blittable/string (no class/struct setter dispatch yet)
            bool isSetterBlittableOrString = IsTypeBlittable(property.SwiftTypeSpec) || IsStringType(property.SwiftTypeSpec);
            if (hasSetter && isSetterBlittableOrString)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                EmitPropertySetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
            }
        }

        // Methods
        foreach (var method in protocolDecl.Methods)
        {
            // Shared eligibility predicate keeps this walk's index in lockstep with both C# walks.
            if (!IsMethodWitnessDispatchEligible(method, protocolDecl))
                continue;

            // EffectiveWitnessSlotKey splits a disambiguated label-only pair into two forward slots (label-inclusive
            // key) while leaving every other method on the label-blind key — the two C# forward walks take the SAME
            // effective key, so producer and consumer indices stay in lockstep.
            var methodKey = ProtocolMethodDisambiguator.EffectiveWitnessSlotKey(method, protocolDecl, _typeDatabase);
            if (methodIndices.ContainsKey(methodKey))
                continue;

            var idx = methodIndex++;
            methodIndices[methodKey] = idx;

            var kind = ClassifyMethodDispatch(method);
            if (kind == MethodDispatchKind.BlittableOrString)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                EmitMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ThrowingBlittableOrString)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                EmitErrorHelperIfNeeded();
                EmitThrowingMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ExistentialReturn)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                if (method.Throws)
                    EmitErrorHelperIfNeeded();
                EmitExistentialMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.ClassReturn)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                if (method.Throws)
                    EmitErrorHelperIfNeeded();
                EmitClassReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.StructReturn)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                if (method.Throws)
                    EmitErrorHelperIfNeeded();
                EmitStructReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
            else if (kind == MethodDispatchKind.BoundGenericReturn)
            {
                EnsureHeader();
                EmitUtf8SliceIfNeeded();
                if (method.Throws)
                    EmitErrorHelperIfNeeded();
                EmitCollectionReturnMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
        }

        if (anyEmitted)
            writer.WriteLine();
    }

    /// <summary>
    /// Determines if a property getter can be dispatched via witness table.
    /// A getter is dispatchable if its return type is blittable or String,
    /// and does not contain unresolved generic type parameters.
    /// </summary>
    public bool IsPropertyGetterDispatchable(PropertyDecl property)
    {
        // Properties whose type contains unresolved generic type parameters (e.g., DateResult<StringType>)
        // cannot be dispatched because the Swift wrapper would generate invalid code like
        // UnsafeMutablePointer<Any> that can't match the concrete generic type.
        if (ContainsGenericTypeParam(property.SwiftTypeSpec))
            return false;
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Determines if a property setter can be dispatched via witness table.
    /// A setter is dispatchable if its type is blittable or String,
    /// and does not contain unresolved generic type parameters.
    /// </summary>
    public bool IsPropertySetterDispatchable(PropertyDecl property)
    {
        if (ContainsGenericTypeParam(property.SwiftTypeSpec))
            return false;
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Returns true if the existential binding loaded from <c>containerPtr</c> must be
    /// declared as <c>var</c> for the property getter to compile. Swift forbids invoking
    /// a <c>mutating get</c> accessor through an immutable existential binding, so any
    /// protocol that could legally declare <c>var foo: T { mutating get }</c> — i.e.
    /// any non-class-bound protocol — must use <c>var</c> when the property has any
    /// mutating-getter signal.
    ///
    /// Class-boundedness is read transitively from the protocol's
    /// <see cref="TypeRecordFlags.ClassBound"/> bit (populated by
    /// <c>ModuleProcessor.ProtocolIsClassBoundTransitive</c>), not from the parser's
    /// direct <c>ProtocolDecl.IsClassBound</c> bit. That keeps <c>protocol Child: Parent</c>
    /// in sync with <c>Parent: AnyObject</c> — both short-circuit to <c>let boxed</c>,
    /// matching the proxy-layout and existential-return machinery.
    ///
    /// The ABI digester sometimes strips the <c>mutating</c> attribute from accessors
    /// (see the <c>PropertyWrapperEmitter.cs</c> concrete-property handling), so the
    /// <c>IsMutating</c> bit alone is not load-bearing. Mirror the same conservative
    /// widening: a settable protocol property on a non-class-bound protocol is treated
    /// as potentially mutating. False positives here cost only a benign
    /// <c>'existential' was never mutated</c> warning; false negatives would be a
    /// compile error in the generated wrapper.
    /// </summary>
    private bool RequiresMutableExistentialBinding(PropertyDecl property, ProtocolDecl protocolDecl)
    {
        if (IsProtocolClassBoundTransitive(protocolDecl))
            return false;
        bool getMutating = property.Accessors
            .OfType<GetAccessorDecl>()
            .FirstOrDefault()?.Method.IsMutating == true;
        bool hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        return getMutating || hasSetter;
    }

    /// <summary>
    /// Reads the transitive class-boundedness of <paramref name="protocolDecl"/> from
    /// its <see cref="TypeRecord"/> if available (populated by
    /// <c>ModuleProcessor.ProtocolIsClassBoundTransitive</c>), falling back to the
    /// direct <see cref="ProtocolDecl.IsClassBound"/> bit when no record is registered
    /// (e.g. synthetic protocols in unit tests).
    /// </summary>
    private bool IsProtocolClassBoundTransitive(ProtocolDecl protocolDecl)
    {
        var swiftTypeName = protocolDecl.SwiftTypeName;
        if (swiftTypeName is not null &&
            _typeDatabase.TryGetTypeRecord(swiftTypeName, out var record) &&
            record.Kind == TypeRecordKind.Protocol)
        {
            return record.Flags.HasFlag(TypeRecordFlags.ClassBound);
        }
        return protocolDecl.IsClassBound;
    }

    /// <summary>
    /// Classifies how a method can be dispatched through the witness table.
    /// Returns <see cref="MethodDispatchKind.BlittableOrString"/> for methods with all blittable/String types,
    /// <see cref="MethodDispatchKind.ExistentialReturn"/> for methods returning protocol existentials
    /// (including throwing methods), or <see cref="MethodDispatchKind.NotDispatchable"/> otherwise.
    /// </summary>
    public MethodDispatchKind ClassifyMethodDispatch(MethodDecl method)
    {
        // Async methods are never dispatchable (require Swift concurrency runtime)
        if (method.IsAsync)
            return MethodDispatchKind.NotDispatchable;

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Check if return type is an existential that can be dispatched
        if (hasReturn && IsExistentialDispatchable(returnType!))
        {
            // Throwing + optional existential conflict: IntPtr.Zero is used as error sentinel,
            // which collides with the .none sentinel for optionals. Block this combination.
            if (method.Throws && MarshallingHelpers.IsSwiftOptional(returnType!))
                return MethodDispatchKind.NotDispatchable;

            // Existential return path: allows throwing (uses error out-parameter)
            // All params must still be blittable/String
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ExistentialReturn;
        }

        // Check if return type is a bound generic collection (Array, Dictionary, Set)
        // Must be before class/struct checks because Array could match IsIndirectStructType
        if (hasReturn && IsBoundGenericReturnDispatchable(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.BoundGenericReturn;
        }

        // Check if return type is a concrete class (ARC via Unmanaged.passRetained)
        // Handles throwing internally (same as ExistentialReturn)
        if (hasReturn && IsClassReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ClassReturn;
        }

        // Check if return type is a non-frozen struct (indirect result buffer)
        // Handles throwing internally (same as ClassReturn)
        if (hasReturn && IsStructReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.StructReturn;
        }

        // Throwing methods with blittable/String/void return use error out-parameter pattern
        if (method.Throws)
        {
            if (hasReturn && !IsTypeDispatchable(returnType!))
                return MethodDispatchKind.NotDispatchable;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return MethodDispatchKind.NotDispatchable;
            }
            return MethodDispatchKind.ThrowingBlittableOrString;
        }

        // Check return type is blittable/String
        if (hasReturn && !IsTypeDispatchable(returnType!))
            return MethodDispatchKind.NotDispatchable;

        // Check all parameters
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (!IsTypeDispatchable(param.SwiftTypeSpec))
                return MethodDispatchKind.NotDispatchable;
        }

        return MethodDispatchKind.BlittableOrString;
    }

    /// <summary>
    /// Classifies method dispatch with a human-readable reason when not dispatchable.
    /// Returns <see cref="DispatchClassification"/> with Kind and optional Reason string.
    /// </summary>
    public DispatchClassification ClassifyMethodDispatchWithReason(MethodDecl method)
    {
        if (method.IsAsync)
            return new DispatchClassification(MethodDispatchKind.NotDispatchable, "async methods require Swift concurrency runtime");

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Check return type dispatchability first for non-blittable return reason
        if (hasReturn && IsExistentialDispatchable(returnType!))
        {
            if (method.Throws && MarshallingHelpers.IsSwiftOptional(returnType!))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable, "throwing methods with optional existential return are not supported");

            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
            }
            return new DispatchClassification(MethodDispatchKind.ExistentialReturn, null);
        }

        if (hasReturn && IsBoundGenericReturnDispatchable(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
            }
            return new DispatchClassification(MethodDispatchKind.BoundGenericReturn, null);
        }

        if (hasReturn && IsClassReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
            }
            return new DispatchClassification(MethodDispatchKind.ClassReturn, null);
        }

        if (hasReturn && IsStructReturn(returnType!))
        {
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
            }
            return new DispatchClassification(MethodDispatchKind.StructReturn, null);
        }

        if (method.Throws)
        {
            if (hasReturn && !IsTypeDispatchable(returnType!))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                    $"return type '{MapForDiagnostic(returnType!)}' is not dispatchable");
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (!IsTypeDispatchable(param.SwiftTypeSpec))
                    return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                        $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
            }
            return new DispatchClassification(MethodDispatchKind.ThrowingBlittableOrString, null);
        }

        if (hasReturn && !IsTypeDispatchable(returnType!))
            return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                $"return type '{MapForDiagnostic(returnType!)}' is not dispatchable");

        foreach (var param in method.CSSignature.Skip(1))
        {
            if (!IsTypeDispatchable(param.SwiftTypeSpec))
                return new DispatchClassification(MethodDispatchKind.NotDispatchable,
                    $"parameter '{param.Name}' has non-dispatchable type '{MapForDiagnostic(param.SwiftTypeSpec)}'");
        }

        return new DispatchClassification(MethodDispatchKind.BlittableOrString, null);
    }

    /// <summary>
    /// Maps Swift module names to .NET namespace equivalents in a TypeSpec's string representation
    /// for use in diagnostic messages (e.g., QuartzCore.CALayer → CoreAnimation.CALayer).
    /// </summary>
    private static string MapForDiagnostic(TypeSpec typeSpec)
        => MarshallingHelpers.MapModulesInString(typeSpec.ToString());

    /// <summary>
    /// Returns a human-readable reason why a property type is not dispatchable via witness table.
    /// Returns null if the property is dispatchable.
    /// </summary>
    public string? GetPropertyNonDispatchReason(PropertyDecl property)
    {
        if (IsTypeDispatchable(property.SwiftTypeSpec)
            || IsPropertyClassReturn(property)
            || IsPropertyOptionalClassReturn(property)
            || IsPropertyStructReturn(property)
            || IsPropertyCollectionReturn(property)
            || IsPropertyExistentialReturn(property))
            return null;

        return $"property type '{MapForDiagnostic(property.SwiftTypeSpec)}' is not dispatchable via witness table";
    }

    /// <summary>
    /// Determines if a method can be dispatched via witness table (backward-compat wrapper).
    /// Returns true if the method is dispatchable via any dispatch kind.
    /// </summary>
    public bool IsMethodDispatchable(MethodDecl method)
    {
        return ClassifyMethodDispatch(method) != MethodDispatchKind.NotDispatchable;
    }

    /// <summary>
    /// Checks if a return type is a protocol existential that can be dispatched
    /// through the witness table using a typed pointer allocation pattern.
    /// Reuses <see cref="ProtocolExtensionEmitter.IsSupportedExistentialReturn"/> for validation,
    /// then adds additional gates: must not be a well-known protocol type, and must have
    /// a valid proxy class name.
    /// </summary>
    public bool IsExistentialDispatchable(TypeSpec returnType)
    {
        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Check for Optional<any Protocol> — unwrap and validate the inner existential
        // Must apply the same safety gates as IsSupportedExistentialReturn (via IsSupportedExistentialCore)
        if (existentialHandler.IsOptionalExistential(returnType))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(returnType);
            if (innerProtocolList == null)
                return false;

            // IsSupportedExistential checks (witness table count limit)
            if (!existentialHandler.IsSupportedExistential(innerProtocolList))
                return false;

            // Well-known types (e.g., "any Error" → AnyError) use different wrappers, not proxy classes
            if (existentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out _))
                return false;

            // Zero-protocol "Any" has no proxy class
            if (existentialHandler.IsAnyType(innerProtocolList))
                return false;

            // All protocols must have TypeRecords in the database
            if (!existentialHandler.AllProtocolsHaveTypeRecords(innerProtocolList))
                return false;

            // Block unresolved/unknown protocols and generic protocol existentials
            var publicType = existentialHandler.GetPublicExistentialType(innerProtocolList);
            if (publicType == "object" ||
                publicType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                return false;

            // ObjC filtering guard: if ObjC filtering drops protocols, proxy expects fewer witness tables than ABI.
            // Mirrors the predicate used by ExistentialHandler.GetEffectiveProtocols so the parity check stays in sync.
            var filteredCount = innerProtocolList.Protocols.Keys
                .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
            if (filteredCount != innerProtocolList.Protocols.Count)
                return false;

            // Must have a valid proxy class name (filters ObjC-only protocols)
            if (!existentialHandler.TryGetFilteredProxyClassName(innerProtocolList, out _))
                return false;

            // Reject protocols with flags that prevent proxy emission (PAT, Self, InheritedRequirementsOnly)
            if (ProtocolExtensionEmitter.HasBlockingProtocolFlagsForReturn(innerProtocolList, _typeDatabase))
                return false;

            return true;
        }

        // Delegate to the existing comprehensive existential validation
        if (!ProtocolExtensionEmitter.IsSupportedExistentialReturn(returnType, _typeDatabase))
            return false;

        // IsSupportedExistentialReturn allows well-known types (e.g., Swift.Error → AnyError)
        // and zero-protocol "Any" → ExistentialContainer0. These use different C# wrappers,
        // not proxy classes, so they can't use the existential dispatch pattern.
        var protocolList = existentialHandler.ToProtocolListTypeSpec(returnType);
        if (protocolList == null)
            return false;

        // Reject well-known types (e.g., "any Error" → AnyError)
        if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
            return false;

        // Reject zero-protocol "Any" (no proxy class)
        if (existentialHandler.IsAnyType(protocolList))
            return false;

        // Must have a valid proxy class name
        if (!existentialHandler.TryGetFilteredProxyClassName(protocolList, out _))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a Swift class (TypeRecordKind.Class) in the type database.
    /// Rejects generic types (ContainsGenericParameters) and ObjC module types.
    /// Does NOT check IsTypeBlittable/IsStringType — use for raw type identification only.
    /// </summary>
    public bool IsSwiftClassType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        if (namedType.ContainsGenericParameters)
            return false;
        if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.Kind == TypeRecordKind.Class
                    && typeRecord.NativeTypeName == null; // Exclude native-remapped (e.g., Foundation.URL → NSUrl)
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents an ObjC-rooted Swift class (inherits from NSObject).
    /// These use .Handle (ObjC pointer) instead of .Payload.DangerousGetHandle().
    /// </summary>
    public bool IsObjCRootedClassType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return MarshallingHelpers.IsObjCRooted(typeRecord);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Checks whether a TypeSpec's C# projection exposes the native pointer via a bare
    /// <c>.Handle</c> accessor rather than an ISwiftObject <c>.Payload</c> SafeHandle — true for
    /// ObjC-rooted, ObjC-bridged, and ObjC-bridgeable types alike (e.g. an NSObject-subclass Swift
    /// class, or a native-remapped <c>CoreGraphics.CGContext</c> that carries <c>objcBridged</c> but
    /// is NOT NSObject-rooted). Widens <see cref="IsObjCRootedClassType"/>'s ObjC-rooted-only test to
    /// the full union so a bridged class no longer falls to the (nonexistent) <c>.Payload</c> path.
    /// </summary>
    public bool UsesHandleAccessor(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return MarshallingHelpers.UsesHandleAccessor(typeRecord);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a struct that requires indirect dispatch
    /// (non-frozen struct or frozen struct with RequiresMemoryManagement).
    /// Does NOT check IsTypeBlittable/IsStringType — use for raw type identification only.
    /// </summary>
    public bool IsIndirectStructType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        if (namedType.ContainsGenericParameters)
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                if (typeRecord.Kind != TypeRecordKind.Struct)
                    return false;
                if (typeRecord.NativeTypeName != null)
                    return false; // Exclude native-remapped (e.g., Foundation.Data → NSData)
                bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                // Frozen value-type structs not supported (would be blittable)
                if (isFrozen && !hasRefFields)
                    return false;
                // Non-frozen OR frozen+RefFields → indirect result buffer
                return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a return type is a Swift class (TypeRecordKind.Class) that can be
    /// dispatched through the witness table using Unmanaged.passRetained.
    /// Rejects generic types (ContainsGenericParameters) and ObjC module types.
    /// </summary>
    public bool IsClassReturn(TypeSpec returnType)
    {
        // Already handled by blittable/String dispatch — use explicit checks to avoid circular dependency
        if (IsTypeBlittable(returnType) || IsStringType(returnType))
            return false;

        return IsSwiftClassType(returnType);
    }

    /// <summary>
    /// Checks if a return type is a struct that requires indirect result buffer
    /// (non-frozen struct or frozen struct with RequiresMemoryManagement).
    /// Matches ExtensionMarshallingHelper.ClassifyReturnType logic for NonFrozenStruct.
    /// </summary>
    public bool IsStructReturn(TypeSpec returnType)
    {
        // Already handled by blittable/String dispatch — use explicit checks to avoid circular dependency
        if (IsTypeBlittable(returnType) || IsStringType(returnType))
            return false;

        return IsIndirectStructType(returnType);
    }

    /// <summary>
    /// Checks if a struct return type is a frozen struct with reference-type fields (ClassWithBufferStruct).
    /// For this subtype, NewFromPayload copies to a new buffer, so the original buffer must be freed on success.
    /// </summary>
    public bool IsFrozenStructWithRefFields(TypeSpec returnType)
    {
        if (returnType is not NamedTypeSpec namedType)
            return false;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                return MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Checks if a property getter returns a Swift class (dispatchable via ClassReturn pattern).
    /// </summary>
    public bool IsPropertyClassReturn(PropertyDecl property)
    {
        return IsClassReturn(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a return type is <c>Optional&lt;SwiftClass&gt;</c> (e.g. <c>Entity?</c>).
    /// Dispatched via the nullable direct-pointer pattern: a nil payload returns a null
    /// pointer (.none), a non-nil payload returns a +1 retained instance pointer that the
    /// C# SafeHandle adopts — the ClassReturn pattern plus a nil guard.
    /// </summary>
    public bool IsOptionalClassReturn(TypeSpec? returnType)
    {
        // Route the optional-reference ABI question through the canonical oracle (shared with the
        // closure bridges and every CdeclParamMapper.IsOptionalWithReferenceInner caller) instead of
        // the local IsSwiftClassType-only test, which excluded ObjC-bridged/rooted class returns.
        return returnType is NamedTypeSpec namedType
            && OptionalReferenceClassifier.UsesNullablePointerAbi(namedType, _typeDatabase);
    }

    /// <summary>
    /// Checks if a property getter returns <c>Optional&lt;SwiftClass&gt;</c>.
    /// </summary>
    public bool IsPropertyOptionalClassReturn(PropertyDecl property)
    {
        return IsOptionalClassReturn(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a property getter returns a protocol existential (<c>any P</c> or
    /// <c>(any P)?</c>) that can be dispatched via the heap-cell pattern. Mirrors the
    /// existential METHOD return path: a typed <c>UnsafeMutablePointer&lt;any P&gt;</c> heap
    /// cell carries the value across the boundary, the C# side reconstructs the container and
    /// constructs a proxy, then frees the cell. Class-bound (single superclass-/AnyObject-
    /// constrained) existentials use the 2-word <c>ClassExistentialContainer1</c> carrier with
    /// retain-on-read ownership; opaque existentials use the 5-word container.
    /// </summary>
    public bool IsPropertyExistentialReturn(PropertyDecl property)
    {
        return IsExistentialDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a property getter returns a struct requiring indirect result buffer.
    /// </summary>
    public bool IsPropertyStructReturn(PropertyDecl property)
    {
        return IsStructReturn(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Checks if a TypeSpec represents a collection type (Array, Dictionary, or Set).
    /// </summary>
    public static bool IsCollectionType(TypeSpec? typeSpec)
    {
        return MarshallingHelpers.IsSwiftArray(typeSpec) ||
               MarshallingHelpers.IsSwiftDictionary(typeSpec) ||
               MarshallingHelpers.IsSwiftSet(typeSpec);
    }

    /// <summary>
    /// Checks if a property getter returns a collection type that can be dispatched.
    /// </summary>
    public bool IsPropertyCollectionReturn(PropertyDecl property)
    {
        return IsCollectionType(property.SwiftTypeSpec) && IsBoundGenericReturnDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Validates that a collection return type can be dispatched:
    /// - Outer type is Array, Dictionary, or Set
    /// - Element types resolve in TypeDatabase (not AnyType)
    /// - For Dictionary: both key AND value must resolve
    /// </summary>
    public bool IsBoundGenericReturnDispatchable(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (!IsCollectionType(typeSpec))
            return false;

        var genericParams = namedType.GenericParameters;
        if (genericParams.Count == 0)
            return false;

        // Validate each element type resolves (not AnyType)
        foreach (var elemType in genericParams)
        {
            if (!IsElementTypeResolvable(elemType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the Swift collection type string for heap allocation in witness dispatch.
    /// E.g., Swift.Array&lt;Swift.String&gt; → "[String]", Swift.Dictionary → "[K: V]", Swift.Set → "Set&lt;T&gt;".
    /// </summary>
    public string? GetSwiftCollectionTypeString(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        string MapElement(TypeSpec elemType)
        {
            if (elemType is NamedTypeSpec namedElem)
            {
                // Known Swift primitives (Swift.Int, Swift.Bool, etc.) — strip module prefix
                if (SwiftToCSharpPrimitiveMap.ContainsKey(namedElem.Name) && namedElem.GenericParameters.Count == 0)
                    return namedElem.NameWithoutModule;
                // Swift.String — strip module prefix
                if (IsStringType(elemType))
                    return namedElem.NameWithoutModule;
                // Keep module-qualified for user types and render nested generics so a
                // nested array element (Swift.Array<Float>) doesn't collapse to "Swift.Array".
                return ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(elemType);
            }
            return ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(elemType);
        }

        if (MarshallingHelpers.IsSwiftArray(typeSpec))
        {
            var elem = MapElement(namedType.GenericParameters[0]);
            return $"[{elem}]";
        }
        if (MarshallingHelpers.IsSwiftDictionary(typeSpec))
        {
            var key = MapElement(namedType.GenericParameters[0]);
            var value = MapElement(namedType.GenericParameters[1]);
            return $"[{key}: {value}]";
        }
        if (MarshallingHelpers.IsSwiftSet(typeSpec))
        {
            var elem = MapElement(namedType.GenericParameters[0]);
            return $"Set<{elem}>";
        }
        return null;
    }

    /// <summary>
    /// Gets the module-qualified Swift type name for a concrete TypeSpec.
    /// Used for struct return's assumingMemoryBound(to:) and class return's type cast.
    /// Returns null if the type cannot be resolved.
    /// </summary>
    public string? GetSwiftConcreteTypeName(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;
        // The Swift name is the module-qualified name from the TypeSpec itself
        return namedType.Name;
    }

    /// <summary>
    /// Gets the C# type name for a concrete class/struct return, suitable for
    /// SwiftMarshal.MarshalFromSwift&lt;T&gt;() calls.
    /// Returns null if the type cannot be resolved.
    /// </summary>
    public string? GetConcreteReturnCSharpType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.CSharpTypeName.FullyQualifiedName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String.
    /// Used by ProtocolProxyEmitter to branch on String-specific marshalling.
    /// </summary>
    public static bool IsStringType(TypeSpec? typeSpec)
    {
        return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.String";
    }

    /// <summary>
    /// Checks if a type can be dispatched through witness accessors.
    /// This includes blittable primitives, Swift.String (via UTF-8 bridge),
    /// Swift classes (via Unmanaged pointer), and indirect structs (non-frozen or frozen+RefFields).
    /// </summary>
    public bool IsTypeDispatchable(TypeSpec? typeSpec)
    {
        return IsTypeBlittable(typeSpec) || IsStringType(typeSpec)
            || IsSwiftClassType(typeSpec) || IsIndirectStructType(typeSpec);
    }

    /// <summary>
    /// Checks if a TypeSpec represents a String dispatch type.
    /// Public for ProtocolProxyEmitter to branch on String vs blittable marshalling.
    /// </summary>
    public static bool IsStringDispatchType(TypeSpec? typeSpec)
    {
        return IsStringType(typeSpec);
    }

    /// <summary>
    /// Single source of truth for whether a protocol method participates in witness-table
    /// forward dispatch (gets a Swift @_cdecl accessor and a matching C# P/Invoke + call site).
    ///
    /// This predicate is consumed by ALL THREE walks that compute the per-member dispatch index:
    /// the Swift wrapper emission (EmitWitnessDispatchFunctions), the C# P/Invoke declaration walk
    /// (ProtocolProxyEmitter.EmitWitnessDispatchPInvokes), and the C# caller walk
    /// (ProtocolProxyEmitter.EmitInterfaceImplementation). Any divergence shifts the index baked
    /// into SBW_{proto}_method_{name}_{idx}, so one side ends up referencing a symbol the other
    /// never emitted — a runtime EntryPointNotFoundException, not a compile error. @objc-optional
    /// methods are non-dispatchable (the existential call returns Optional and the witness is
    /// absent); they consume NO index and the interface satisfies them via a default no-op.
    /// </summary>
    public static bool IsMethodWitnessDispatchEligible(MethodDecl method, ProtocolDecl? owningProtocol = null)
    {
        // A mixed-generic protocol (a method-level generic requirement alongside non-generic
        // members) is excluded from Swift-side witness dispatch wholesale by the protocol-wide
        // IsMixedGenericProtocol gate in ModuleHandler — EmitWitnessDispatchFunctions is never
        // called for it, so NO SBW_ accessor is exported for ANY of its members. The C# proxy
        // pass, however, walks members per-decl with no protocol-wide gate, so without this arm
        // it would emit a P/Invoke to an SBW_ symbol the wrapper never wrote — a dangling
        // EntryPointNotFoundException the WrapperSymbolIntegrityGate now catches. Mirror the
        // Swift-side suppression here so ineligible members degrade to the SB0003 stub instead.
        if (owningProtocol != null && EveryProtocolEmitter.IsMixedGenericProtocol(owningProtocol))
            return false;
        return !method.IsConstructor
            && method.MethodType != MethodType.Static
            && !method.IsObjCOptional;
    }

    /// <summary>
    /// Single source of truth for whether a protocol property participates in witness-table
    /// forward dispatch. Mirrors <see cref="IsMethodWitnessDispatchEligible"/> for the property
    /// accessor walks. Beyond static (not part of the existential witness table), two kinds are
    /// excluded because the Swift wrapper cannot emit an accessor for them:
    /// <list type="bullet">
    /// <item>@objc-optional properties — the existential access returns Optional, no witness.</item>
    /// <item>Custom-actor-isolated (non-@MainActor) properties — a synchronous @_cdecl accessor
    /// cannot be annotated with the custom actor, so accessing the requirement would be a
    /// cross-actor (non-isolated) access that does not compile. @MainActor properties ARE
    /// dispatched (the accessor adds @MainActor).</item>
    /// </list>
    /// Ineligible properties degrade to the existing non-dispatchable fallback in
    /// EmitPropertyImplementation (an SB0003 NotSupportedException getter/setter that still
    /// satisfies the interface), so nothing references an unexported SBW symbol.
    /// </summary>
    public static bool IsPropertyWitnessDispatchEligible(PropertyDecl property, ProtocolDecl? owningProtocol = null)
    {
        // See IsMethodWitnessDispatchEligible: a mixed-generic protocol exports no SBW_ accessor
        // on the Swift side, so its C# proxy accessors must degrade to the SB0003 stub rather
        // than P/Invoke a symbol that was never written (dangling EntryPointNotFoundException).
        if (owningProtocol != null && EveryProtocolEmitter.IsMixedGenericProtocol(owningProtocol))
            return false;
        return !property.IsStatic
            && !property.IsObjCOptional
            && !(property.IsActorIsolated && !property.IsMainActorIsolated);
    }

    /// <summary>
    /// Gets the @_cdecl symbol for an accessor function.
    /// Format: SBW_{Protocol}_{kind}_{name}_{index}
    /// </summary>
    public static string GetAccessorSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Gets the @_cdecl symbol for a free function.
    /// Format: SBW_{Protocol}_free_{kind}_{name}_{index}
    /// </summary>
    public static string GetFreeSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_free_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Checks if a C# type name represents a blittable primitive.
    /// </summary>
    public static bool IsBlittablePrimitive(string csharpTypeName)
    {
        return BlittablePrimitiveTypes.Contains(csharpTypeName);
    }

    /// <summary>
    /// Returns the canonical blittable C# type name for a TypeSpec.
    /// Uses the Swift-name fast-path first, then falls back to the type database.
    /// This must be used for MarshalFromSwift/MarshalToSwift type parameters
    /// to ensure the marshal type matches the dispatch gate decision.
    /// Returns null if the type is not blittable.
    /// </summary>
    public string? GetBlittableCSharpType(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var fqn = record.CSharpTypeName.FullyQualifiedName;
            return IsBlittablePrimitive(fqn) ? fqn : null;
        }
        catch
        {
            return null;
        }
    }

    #region Private Helpers

    /// <summary>
    /// Checks whether a protocol has any dispatchable members that use String types,
    /// which requires the SBW_Utf8Slice struct to be emitted.
    /// </summary>
    private bool NeedsUtf8Slice(ProtocolDecl protocolDecl)
    {
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (IsStringType(property.SwiftTypeSpec))
                return true;
        }
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            // Skip async methods entirely, but for throwing methods check if they
            // are ExistentialReturn dispatchable (those can have String params)
            if (method.IsAsync)
                continue;
            var kind = ClassifyMethodDispatch(method);
            if (kind == MethodDispatchKind.NotDispatchable)
                continue;
            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            if (returnType != null && !returnType.IsEmptyTuple && IsStringType(returnType))
                return true;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (IsStringType(param.SwiftTypeSpec))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if an element type (used inside a collection) can be resolved
    /// in the type database to a concrete type (not AnyType).
    /// </summary>
    private bool IsElementTypeResolvable(TypeSpec elemType)
    {
        if (elemType is not NamedTypeSpec namedElem)
            return false;

        // Known Swift primitive types are always resolvable
        if (SwiftToCSharpPrimitiveMap.ContainsKey(namedElem.Name))
            return true;

        // Swift.String is always resolvable (not in primitive map since it's not blittable)
        if (IsStringType(elemType))
            return true;

        // Check type database
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedElem.Name);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord != TypeDatabaseExtensions.AnyType;
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains unresolved generic type parameters.
    /// Types with generic params (e.g., DateResult&lt;StringType&gt;) can't be used in
    /// Swift wrapper code because the compiler can't infer the concrete type.
    /// </summary>
    private static bool ContainsGenericTypeParam(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return false;
        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                    return true;
                foreach (var gp in namedType.GenericParameters)
                {
                    if (ContainsGenericTypeParam(gp))
                        return true;
                }
                return false;
            case TupleTypeSpec tupleType:
                foreach (var elem in tupleType.Elements)
                {
                    if (ContainsGenericTypeParam(elem))
                        return true;
                }
                return false;
            case ClosureTypeSpec closureType:
                return ContainsGenericTypeParam(closureType.Arguments) ||
                       ContainsGenericTypeParam(closureType.ReturnType);
            case ProtocolListTypeSpec protocolList:
                foreach (var p in protocolList.Protocols.Keys)
                {
                    if (ContainsGenericTypeParam(p))
                        return true;
                }
                return false;
            case AssociatedTypeReferenceSpec assocType:
                // Associated types like Self.Element or τ_0_0.Element reference
                // unresolved generic type parameters through their base type.
                return TypeSpecHelpers.IsGenericTypeParameter(assocType.BaseType)
                    || assocType.BaseType == "Self";
            default:
                return false;
        }
    }

    private bool IsTypeBlittable(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return false;

        // Fast path: check Swift type name directly against known primitives
        if (typeSpec is NamedTypeSpec namedType && BlittableSwiftTypes.Contains(namedType.Name))
            return true;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var csharpType = record.CSharpTypeName.FullyQualifiedName;

            // CoreFoundation opaque pointer types (SecTrust, SecKey, etc.) project to IntPtr/nint
            // in C# but are reference types in Swift. The dispatch emitter would generate
            // `load(as: Int.self)` instead of the correct type name, causing type mismatch.
            // Reject non-Swift-primitive types that project to IntPtr/nint.
            if ((csharpType is "nint" or "System.IntPtr" or "nuint" or "System.UIntPtr")
                && typeSpec is NamedTypeSpec cfNamed && cfNamed.HasModule()
                && cfNamed.Module != "Swift")
                return false;

            return IsBlittablePrimitive(csharpType);
        }
        catch
        {
            return false;
        }
    }

    private string GetCSharpTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "object";

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return record.CSharpTypeName.FullyQualifiedName;
        }
        catch
        {
            return "object";
        }
    }

    private static string GetSwiftPrimitiveType(string csharpTypeName)
    {
        return CSharpToSwiftTypeMap.TryGetValue(csharpTypeName, out var swiftType)
            ? swiftType
            : "Any";
    }

    /// <summary>
    /// Resolves the Swift type name to use in witness-dispatch blittable marshalling — i.e.
    /// for <c>UnsafeMutablePointer&lt;T&gt;</c>, <c>load(as: T.self)</c>, and
    /// <c>assumingMemoryBound(to: T.self)</c>. The resolved name must be the *static Swift type*
    /// of the marshalled value, since <c>initialize(to:)</c> / <c>load(as:)</c> are type-checked.
    /// <list type="bullet">
    /// <item>Swift pointer types (OpaquePointer, UnsafeRawPointer, …) project to nint/IntPtr but
    /// must keep their bare Swift name.</item>
    /// <item>Genuine Swift primitives (Swift.Int, Swift.Double, …) round-trip through the C#
    /// projection to their bare Swift name (Int, Double).</item>
    /// <item>Everything else — including value types that merely *project* to a C# primitive
    /// (e.g. Foundation.Date → double) and generic containers — keeps its real module-qualified
    /// Swift type. Round-tripping these through the projection produces the wrong pointee type
    /// (e.g. <c>UnsafeMutablePointer&lt;Double&gt;</c> for a <c>Foundation.Date</c> value).</item>
    /// </list>
    /// </summary>
    private string GetSwiftBlittableTypeName(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            if (IsSwiftPointerType(named.Name))
                return named.NameWithoutModule;
            if (named.GenericParameters.Count == 0 && SwiftToCSharpPrimitiveMap.ContainsKey(named.Name))
                return GetSwiftPrimitiveType(GetCSharpTypeName(typeSpec));
        }
        return ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
    }

    /// <summary>
    /// Checks if a module-qualified Swift type name is a non-generic pointer type.
    /// These types map to nint/IntPtr in C# but must use their original Swift name
    /// (not Int) in load(as:) calls.
    /// Only includes OpaquePointer and raw pointer types — NOT UnsafePointer/UnsafeMutablePointer,
    /// which are generic (UnsafePointer&lt;Pointee&gt;) and need their full type rendered.
    /// </summary>
    private static bool IsSwiftPointerType(string moduleQualifiedName) => moduleQualifiedName switch
    {
        "Swift.OpaquePointer" => true,
        "Swift.UnsafeRawPointer" => true,
        "Swift.UnsafeMutableRawPointer" => true,
        _ => false
    };

    /// <summary>
    /// Emits a Swift property getter accessor + free function pair using the heap allocation pattern.
    /// The accessor loads the existential, reads the property, allocates memory, initializes it,
    /// and returns an UnsafeMutableRawPointer. The free function deinitializes and deallocates.
    /// Used by blittable and collection property getters (identical structure, different Swift type).
    /// <paramref name="swiftTypeName"/> must be a valid Swift type for both
    /// <c>UnsafeMutablePointer&lt;T&gt;</c> and <c>assumingMemoryBound(to: T.self)</c>.
    /// </summary>
    private void EmitHeapAllocatedPropertyGetter(SwiftWriter writer, string accessorSymbol, string freeSymbol,
        string moduleQualifiedName, string propertyName, string swiftTypeName, bool needsMainActor = false,
        bool needsMutableBinding = false)
    {
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
        writer.WriteLines($$"""
            {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                {{bindKw}} {{bindName}} = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = {{bindName}}.{{propertyName}}
                let ptr = UnsafeMutablePointer<{{swiftTypeName}}>.allocate(capacity: 1)
                ptr.initialize(to: result)
                return UnsafeMutableRawPointer(ptr)
            }

            {{avail}}@_cdecl("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: {{swiftTypeName}}.self).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    private void EmitPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);

        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        // Member-access name for the Swift source: recover the original Swift identifier
        // (the parser rewrites C#-keyword names like `class` to `_class`) and backtick-escape
        // it if it is a Swift keyword (`repeat`). The accessor/free symbols above stay on the
        // raw parser name because they are internal @_cdecl entry points matched by the C# side.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);
        if (IsStringType(property.SwiftTypeSpec))
        {
            var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
            // String getter: convert Swift String to UTF-8 bytes via SBW_Utf8Slice
            writer.WriteLines($$"""
                {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    {{bindKw}} {{bindName}} = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                    let result: String = {{bindName}}.{{swiftMemberName}}
                    let utf8 = Array(result.utf8)
                    let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))
                    if !utf8.isEmpty {
                        utf8.withUnsafeBufferPointer { src in
                            bufferPtr.initialize(from: src.baseAddress!, count: src.count)
                        }
                    }
                    let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)
                    slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))
                    return UnsafeMutableRawPointer(slicePtr)
                }

                {{avail}}@_cdecl("{{freeSymbol}}")
                public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                    slicePtr.pointee.ptr.deallocate()
                    slicePtr.deinitialize(count: 1)
                    slicePtr.deallocate()
                }

                """);
        }
        else
        {
            // Blittable getter: direct pointer allocation. See GetSwiftBlittableTypeName —
            // primitives round-trip to their bare Swift name, container/value types keep their
            // real module-qualified Swift type so generic parameters survive and value types that
            // merely project to a primitive (e.g. Foundation.Date → double) keep their true type.
            var swiftReturnType = GetSwiftBlittableTypeName(property.SwiftTypeSpec);
            EmitHeapAllocatedPropertyGetter(writer, accessorSymbol, freeSymbol, moduleQualifiedName, swiftMemberName, swiftReturnType, needsMainActor, needsMutableBinding);
        }
    }

    private void EmitPropertySetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "set", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);

        if (IsStringType(property.SwiftTypeSpec))
        {
            // String setter: decode SBW_Utf8Slice → String, then assign via typed pointee
            writer.WriteLines($$"""
                {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
                    let str: String
                    if slice.len > 0 {
                        str = String(unsafeUninitializedCapacity: slice.len) { buf in
                            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
                            return slice.len
                        }
                    } else {
                        str = ""
                    }
                    existential.{{swiftMemberName}} = str
                    typedPtr.pointee = existential
                }

                """);
        }
        else
        {
            // Blittable setter: typed pointee assignment
            var swiftType = GetSwiftBlittableTypeName(property.SwiftTypeSpec);

            writer.WriteLines($$"""
                {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    existential.{{swiftMemberName}} = valuePtr.load(as: {{swiftType}}.self)
                    typedPtr.pointee = existential
                }

                """);
        }
    }

    private void EmitMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && IsStringType(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        bool needsMainActor = method.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Build Swift return type
        var swiftReturnDecl = hasReturn ? " -> UnsafeMutableRawPointer" : "";

        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential — use var for methods that may be mutating in the future
        writer.WriteLine($"var existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        if (hasReturn)
        {
            if (isStringReturn)
            {
                // String return: convert to UTF-8 bytes via SBW_Utf8Slice
                writer.WriteLine($"let result: String = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine("let utf8 = Array(result.utf8)");
                writer.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
                writer.WriteLine("if !utf8.isEmpty {");
                writer.Indent++;
                writer.WriteLine("utf8.withUnsafeBufferPointer { src in");
                writer.Indent++;
                writer.WriteLine("bufferPtr.initialize(from: src.baseAddress!, count: src.count)");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
                writer.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
                writer.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
            }
            else
            {
                // Blittable return: direct pointer allocation
                var swiftReturnType = GetSwiftBlittableTypeName(returnType!);
                writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftReturnType}>.allocate(capacity: 1)");
                writer.WriteLine("ptr.initialize(to: result)");
                writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            }
        }
        else
        {
            writer.WriteLine($"existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
        }

        // Write back inout parameters to caller's buffers
        EmitInoutWriteback(writer, method);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function only for methods with return values
        if (hasReturn)
        {
            var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

            var avail = _currentAvailabilityPrefix;
            if (isStringReturn)
            {
                // String return: free SBW_Utf8Slice + buffer
                writer.WriteLines($$"""
                    {{avail}}@_cdecl("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                        slicePtr.pointee.ptr.deallocate()
                        slicePtr.deinitialize(count: 1)
                        slicePtr.deallocate()
                    }

                    """);
            }
            else
            {
                // Blittable return: simple dealloc
                var swiftReturnType = GetSwiftBlittableTypeName(returnType!);

                writer.WriteLines($$"""
                    {{avail}}@_cdecl("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                        ptr.deallocate()
                    }

                    """);
            }
        }
    }

    /// <summary>
    /// Emits a throwing witness dispatch accessor for blittable/String/void return types.
    /// Uses do/catch with error out-parameter pattern:
    /// - Value-returning: returns UnsafeMutableRawPointer? (nil = error), with free function
    /// - Void: returns Void with errorOut param, no free function
    /// </summary>
    private void EmitThrowingMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && IsStringType(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        bool needsMainActor = method.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param + errorOut
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        var swiftParamsString = string.Join(", ", swiftParams);

        // Return type: UnsafeMutableRawPointer? for value-returning (nil = error), Void for void
        var swiftReturnDecl = hasReturn ? " -> UnsafeMutableRawPointer?" : "";

        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        writer.WriteLine($"var existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        // do/catch with error out-parameter
        writer.WriteLine("do {");
        writer.Indent++;

        if (hasReturn)
        {
            if (isStringReturn)
            {
                // String return: convert to UTF-8 bytes via SBW_Utf8Slice inside do block
                writer.WriteLine($"let result: String = try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine("let utf8 = Array(result.utf8)");
                writer.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
                writer.WriteLine("if !utf8.isEmpty {");
                writer.Indent++;
                writer.WriteLine("utf8.withUnsafeBufferPointer { src in");
                writer.Indent++;
                writer.WriteLine("bufferPtr.initialize(from: src.baseAddress!, count: src.count)");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
                writer.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
                writer.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
            }
            else
            {
                // Blittable return: direct pointer allocation
                var swiftReturnType = GetSwiftBlittableTypeName(returnType!);
                writer.WriteLine($"let result = try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
                writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftReturnType}>.allocate(capacity: 1)");
                writer.WriteLine("ptr.initialize(to: result)");
                writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            }
        }
        else
        {
            // Void return
            writer.WriteLine($"try existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
        }

        // Write back inout parameters to caller's buffers (only on success path)
        EmitInoutWriteback(writer, method);

        writer.Indent--;
        writer.WriteLine("} catch {");
        writer.Indent++;
        writer.WriteLine("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())");
        if (hasReturn)
            writer.WriteLine("return nil");
        writer.Indent--;
        writer.WriteLine("}");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function only for methods with return values
        if (hasReturn)
        {
            var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);
            var avail = _currentAvailabilityPrefix;

            if (isStringReturn)
            {
                writer.WriteLines($$"""
                    {{avail}}@_cdecl("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                        slicePtr.pointee.ptr.deallocate()
                        slicePtr.deinitialize(count: 1)
                        slicePtr.deallocate()
                    }

                    """);
            }
            else
            {
                var swiftReturnType = GetSwiftBlittableTypeName(returnType!);

                writer.WriteLines($$"""
                    {{avail}}@_cdecl("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                        ptr.deallocate()
                    }

                    """);
            }
        }
    }

    /// <summary>
    /// Gets the Swift module-qualified existential type string for a return type.
    /// E.g., for a ProtocolListTypeSpec with "Module.Protocol", returns "Module.Protocol".
    /// Used in typed pointer declarations: <c>UnsafeMutablePointer&lt;any Module.Protocol&gt;</c>.
    /// </summary>
    private string? GetSwiftExistentialTypeName(TypeSpec returnType)
    {
        var existentialHandler = new ExistentialHandler(_typeDatabase);

        // Handle Optional<any Protocol> — unwrap to get the inner protocol list
        ProtocolListTypeSpec? protocolList;
        if (existentialHandler.IsOptionalExistential(returnType))
            protocolList = existentialHandler.UnwrapOptionalExistential(returnType);
        else
            protocolList = existentialHandler.ToProtocolListTypeSpec(returnType);

        if (protocolList == null)
            return null;

        // Build module-qualified protocol names for the existential type
        // (parity with ExistentialHandler.GetEffectiveProtocols).
        var protocols = protocolList.Protocols.Keys
            .Where(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p))
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();
        if (protocols.Count == 0)
            return null;

        if (protocols.Count == 1)
            return protocols[0].Name; // e.g., "Module.Protocol"

        // Multi-protocol composition: "ProtocolA & ProtocolB"
        return string.Join(" & ", protocols.Select(p => p.Name));
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning heap-allocated pointer results.
    /// Shared by ExistentialReturn (any Protocol) and BoundGenericReturn (Array, Dictionary, Set).
    /// Pattern: allocate typed pointer → initialize → return UnsafeMutableRawPointer.
    /// Handles throwing (do/catch + errorOut) and optional return (if let unwrap).
    /// Also emits a typed free function for deinitialize + deallocate.
    /// </summary>
    /// <param name="swiftTypeName">The Swift type for allocation, e.g., "any Module.Protocol" or "[String]".</param>
    /// <param name="isOptionalReturn">True for Optional&lt;any Protocol&gt; return types (existential only).</param>
    private void EmitHeapAllocatedSwiftAccessor(
        SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        string moduleQualifiedName, int index,
        string swiftTypeName,
        bool isOptionalReturn = false)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);
        bool needsMainActor = method.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        // + errorOut if throwing
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Return type: UnsafeMutableRawPointer? for optional (nil = .none) and for throwing (nil = error)
        var swiftReturnDecl = (method.Throws || isOptionalReturn)
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential from container
        writer.WriteLine($"var existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            // Throwing pattern: do/catch with error out-parameter
            // Note: throwing + optional is gated out in ClassifyMethodDispatch
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result: {swiftTypeName} = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");

            // Write back inout parameters to caller's buffers (only on success path)
            EmitInoutWriteback(writer, method);

            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())");
            writer.WriteLine("return nil");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else if (isOptionalReturn)
        {
            // Optional existential pattern: if let unwrap, nil = .none
            writer.WriteLine($"let result: ({swiftTypeName})? = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine("if let unwrapped = result {");
            writer.Indent++;
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: unwrapped)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("return nil");
        }
        else
        {
            // Non-throwing, non-optional pattern: direct allocation
            writer.WriteLine($"let result: {swiftTypeName} = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
        }

        // Write back inout parameters to caller's buffers
        EmitInoutWriteback(writer, method);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function — typed deinitialize for ARC-safe cleanup
        // For optional: only called when result is non-nil
        // For "any" types: need parentheses in .self expression: (any Protocol).self
        var freeTypeSelf = swiftTypeName.StartsWith("any ", StringComparison.Ordinal)
            ? $"({swiftTypeName}).self"
            : $"{swiftTypeName}.self";
        var avail = _currentAvailabilityPrefix;

        writer.WriteLines($$"""
            {{avail}}@_cdecl("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: {{freeTypeSelf}}).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    private void EmitExistentialMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftExistentialType = GetSwiftExistentialTypeName(returnType!);
        if (swiftExistentialType == null)
            return; // Should not happen — IsExistentialDispatchable already validated

        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool isOptionalReturn = existentialHandler.IsOptionalExistential(returnType!);

        EmitHeapAllocatedSwiftAccessor(writer, method, protocolDecl, moduleQualifiedName, index,
            $"any {swiftExistentialType}", isOptionalReturn);
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a Swift class.
    /// Non-throwing: returns UnsafeMutableRawPointer via Unmanaged.passRetained.
    /// Throwing: do/catch with errorOut, returns nil on error.
    /// No free function — C# SafeHandle handles ARC release.
    /// </summary>
    private void EmitClassReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftConcreteType = GetSwiftConcreteTypeName(returnType!);
        if (swiftConcreteType == null)
            return;

        EmitPassRetainedAnyObjectMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, index);
    }

    /// <summary>
    /// Emits a witness dispatch accessor whose return value is handed to C# as a +1 retained
    /// object pointer via <c>Unmanaged.passRetained(result as AnyObject).toOpaque()</c> — no free
    /// function (the C# SafeHandle / ObjC bridge adopts the +1). Shared by the Swift-class return
    /// path and the ObjC-bridgeable whole-container return path (Set/Array/Dictionary of an
    /// _ObjectiveCBridgeable element bridges to NSSet/NSArray/NSDictionary through <c>as AnyObject</c>).
    /// </summary>
    private void EmitPassRetainedAnyObjectMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        bool needsMainActor = method.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";

        // Build Swift parameter list
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        var swiftReturnDecl = method.Throws
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        writer.WriteLine($"var existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");

            // Write back inout parameters to caller's buffers (only on success path)
            EmitInoutWriteback(writer, method);

            writer.WriteLine("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())");
            writer.WriteLine("return nil");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
        }

        // Write back inout parameters to caller's buffers
        EmitInoutWriteback(writer, method);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        // No free function — SafeHandle handles ARC release
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a non-frozen struct.
    /// Caller provides resultBuf; Swift writes into it via assumingMemoryBound(to:).initialize(to:).
    /// Throwing: do/catch with errorOut, void return.
    /// No free function — SafeHandle owns the buffer.
    /// </summary>
    private void EmitStructReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var swiftConcreteType = GetSwiftConcreteTypeName(returnType!);
        if (swiftConcreteType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);
        bool needsMainActor = method.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";

        // Build Swift parameter list: containerPtr + resultBuf + per-param + errorOut
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer", "_ resultBuf: UnsafeMutableRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        if (method.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeRawPointer?>");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Struct return always returns void (result written into buffer)
        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}) {{");
        writer.Indent++;

        writer.WriteLine($"var existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            EmitParameterUnmarshal(writer, param, argIdx);
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build labeled args
        var labeledArgs = BuildLabeledArgs(method, callArgs);
        var callArgsString = string.Join(", ", labeledArgs);

        var tryPrefix = method.Throws ? "try " : "";

        if (method.Throws)
        {
            writer.WriteLine("do {");
            writer.Indent++;
            writer.WriteLine($"let result = {tryPrefix}existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"resultBuf.assumingMemoryBound(to: {swiftConcreteType}.self).initialize(to: result)");

            // Write back inout parameters to caller's buffers (only on success path)
            EmitInoutWriteback(writer, method);

            writer.Indent--;
            writer.WriteLine("} catch {");
            writer.Indent++;
            writer.WriteLine("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            writer.WriteLine($"let result = existential.{NameProvider.ParserNameToSwift(method)}({callArgsString})");
            writer.WriteLine($"resultBuf.assumingMemoryBound(to: {swiftConcreteType}.self).initialize(to: result)");
        }

        // Write back inout parameters to caller's buffers
        EmitInoutWriteback(writer, method);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        // No free function — SafeHandle owns the buffer
    }

    /// <summary>
    /// Emits a property getter accessor for class return types.
    /// Returns UnsafeMutableRawPointer via Unmanaged.passRetained.
    /// </summary>
    private void EmitClassReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);

        writer.WriteLines($$"""
            {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                {{bindKw}} {{bindName}} = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = {{bindName}}.{{swiftMemberName}}
                return Unmanaged.passRetained(result as AnyObject).toOpaque()
            }

            """);
        // No free function — SafeHandle handles ARC release
    }

    /// <summary>
    /// Emits a property getter accessor for <c>Optional&lt;SwiftClass&gt;</c> return types.
    /// Returns <c>UnsafeMutableRawPointer?</c>: a nil property value returns nil (.none),
    /// a non-nil value returns a +1 retained instance pointer via <c>Unmanaged.passRetained</c>.
    /// The C# SafeHandle adopts the +1; no free function is needed.
    /// </summary>
    private void EmitOptionalClassReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);

        writer.WriteLines($$"""
            {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer? {
                {{bindKw}} {{bindName}} = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                if let result = {{bindName}}.{{swiftMemberName}} {
                    return Unmanaged.passRetained(result as AnyObject).toOpaque()
                }
                return nil
            }

            """);
        // No free function — SafeHandle handles ARC release
    }

    /// <summary>
    /// Emits a property getter accessor for protocol-existential return types
    /// (<c>any P</c> or <c>(any P)?</c>), mirroring the existential METHOD accessor's
    /// heap-cell pattern: allocate <c>UnsafeMutablePointer&lt;any P&gt;</c>, initialize it
    /// to the (unwrapped) existential, and return an <c>UnsafeMutableRawPointer</c>
    /// (nullable for the optional case). A typed free function deinitializes + deallocates
    /// the cell. The container layout (2-word class-bound vs 5-word opaque) is selected by
    /// the Swift compiler from the protocol's class-boundedness; the C# side reads the
    /// matching carrier (see <c>EmitPropertyImplementation</c>).
    /// </summary>
    private void EmitExistentialReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var swiftExistentialType = GetSwiftExistentialTypeName(property.SwiftTypeSpec);
        if (swiftExistentialType == null)
            return; // Should not happen — IsExistentialDispatchable already validated
        var swiftTypeName = $"any {swiftExistentialType}";

        var existentialHandler = new ExistentialHandler(_typeDatabase);
        bool isOptionalReturn = existentialHandler.IsOptionalExistential(property.SwiftTypeSpec);

        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);

        var swiftReturnDecl = isOptionalReturn
            ? " -> UnsafeMutableRawPointer?"
            : " -> UnsafeMutableRawPointer";

        EmitAvailabilityAttributes(writer);
        writer.WriteLine($"{mainActorAttr}@_cdecl(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}(_ containerPtr: UnsafeRawPointer){swiftReturnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"{bindKw} {bindName} = containerPtr.load(as: (any {moduleQualifiedName}).self)");
        if (isOptionalReturn)
        {
            writer.WriteLine($"let result: ({swiftTypeName})? = {bindName}.{swiftMemberName}");
            writer.WriteLine("if let unwrapped = result {");
            writer.Indent++;
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: unwrapped)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("return nil");
        }
        else
        {
            writer.WriteLine($"let result: {swiftTypeName} = {bindName}.{swiftMemberName}");
            writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
            writer.WriteLine("ptr.initialize(to: result)");
            writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Free function — typed deinitialize for ARC-safe cleanup (only called when non-nil).
        var freeTypeSelf = $"({swiftTypeName}).self";
        writer.WriteLines($$"""
            {{avail}}@_cdecl("{{freeSymbol}}")
            public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                ptr.assumingMemoryBound(to: {{freeTypeSelf}}).deinitialize(count: 1)
                ptr.deallocate()
            }

            """);
    }

    /// <summary>
    /// Emits a property getter accessor for struct return types.
    /// Caller provides resultBuf; Swift writes into it.
    /// </summary>
    private void EmitStructReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var swiftConcreteType = GetSwiftConcreteTypeName(property.SwiftTypeSpec);
        if (swiftConcreteType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        var mainActorAttr = needsMainActor ? "@MainActor " : "";
        var avail = _currentAvailabilityPrefix;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        var (bindKw, bindName) = needsMutableBinding ? ("var", "existential") : ("let", "boxed");
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);

        writer.WriteLines($$"""
            {{avail}}{{mainActorAttr}}@_cdecl("{{accessorSymbol}}")
            public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer, _ resultBuf: UnsafeMutableRawPointer) {
                {{bindKw}} {{bindName}} = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                let result = {{bindName}}.{{swiftMemberName}}
                resultBuf.assumingMemoryBound(to: {{swiftConcreteType}}.self).initialize(to: result)
            }

            """);
        // No free function — SafeHandle owns the buffer
    }

    /// <summary>
    /// Emits a property getter accessor for collection return types (Array, Dictionary, Set).
    /// Uses heap-allocated pointer pattern: allocate → initialize → return UnsafeMutableRawPointer.
    /// Also emits a free function for typed deinitialize + deallocate.
    /// </summary>
    private void EmitCollectionReturnPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        // ObjC-bridgeable containers (Set/Array/Dictionary of an _ObjectiveCBridgeable element)
        // cross the boundary as a whole NS* collection at +1, NOT as a native Swift container box.
        // Emit the same passRetained(result as AnyObject) accessor the class-return path uses (no
        // free function); the C# side reads it via the ObjC whole-container bridge (owns: true).
        if (CdeclParamMapper.IsObjCBridgeableContainer(property.SwiftTypeSpec, _typeDatabase))
        {
            EmitClassReturnPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
            return;
        }

        var protocolName = protocolDecl.Name;
        var swiftCollectionType = GetSwiftCollectionTypeString(property.SwiftTypeSpec);
        if (swiftCollectionType == null)
            return;

        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);
        bool needsMainActor = property.IsMainActorIsolated || protocolDecl.IsMainActorIsolated;
        bool needsMutableBinding = RequiresMutableExistentialBinding(property, protocolDecl);
        // See EmitPropertyGetterAccessor: emit the original, keyword-escaped Swift member name.
        var swiftMemberName = NameProvider.ParserNameToSwift(property);
        EmitHeapAllocatedPropertyGetter(writer, accessorSymbol, freeSymbol, moduleQualifiedName, swiftMemberName, swiftCollectionType, needsMainActor, needsMutableBinding);
    }

    /// <summary>
    /// Emits a witness dispatch accessor for methods returning a collection type.
    /// Non-throwing: allocate → initialize → return UnsafeMutableRawPointer.
    /// Throwing: do/catch with errorOut, returns nil on error.
    /// Also emits a free function for typed deinitialize + deallocate.
    /// </summary>
    private void EmitCollectionReturnMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;

        // ObjC-bridgeable containers cross the boundary as a whole NS* collection at +1 (see
        // EmitCollectionReturnPropertyGetterAccessor). Emit the passRetained(result as AnyObject)
        // accessor (no free function); the C# side adopts the +1 via the whole-container bridge.
        if (returnType != null && CdeclParamMapper.IsObjCBridgeableContainer(returnType, _typeDatabase))
        {
            EmitPassRetainedAnyObjectMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, index);
            return;
        }

        var swiftCollectionType = GetSwiftCollectionTypeString(returnType!);
        if (swiftCollectionType == null)
            return;

        EmitHeapAllocatedSwiftAccessor(writer, method, protocolDecl, moduleQualifiedName, index,
            swiftCollectionType);
    }

    /// <summary>
    /// Emits parameter unmarshalling for a single argument (shared by all accessor types).
    /// Supports String (UTF-8 decode), class (Unmanaged.fromOpaque), struct (assumingMemoryBound),
    /// and blittable (direct load).
    /// </summary>
    private void EmitParameterUnmarshal(SwiftWriter writer, ArgumentDecl param, int argIdx)
    {
        if (IsStringType(param.SwiftTypeSpec))
        {
            // String parameter: decode SBW_Utf8Slice → Swift String
            writer.WriteLine($"let arg{argIdx}Slice = arg{argIdx}Ptr.load(as: SBW_Utf8Slice.self)");
            writer.WriteLine($"let arg{argIdx}: String");
            writer.WriteLine($"if arg{argIdx}Slice.len > 0 {{");
            writer.Indent++;
            writer.WriteLine($"arg{argIdx} = String(unsafeUninitializedCapacity: arg{argIdx}Slice.len) {{ buf in");
            writer.Indent++;
            writer.WriteLine($"UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: arg{argIdx}Slice.ptr, byteCount: arg{argIdx}Slice.len)");
            writer.WriteLine($"return arg{argIdx}Slice.len");
            writer.Indent--;
            writer.WriteLine("}");
            writer.Indent--;
            writer.WriteLine("} else {");
            writer.Indent++;
            writer.WriteLine($"arg{argIdx} = \"\"");
            writer.Indent--;
            writer.WriteLine("}");
        }
        else if (IsSwiftClassType(param.SwiftTypeSpec))
        {
            // Class parameter: load raw pointer, then Unmanaged<T>.fromOpaque().takeUnretainedValue()
            var swiftTypeName = GetSwiftConcreteTypeName(param.SwiftTypeSpec);
            writer.WriteLine($"let rawPtr{argIdx} = arg{argIdx}Ptr.load(as: UnsafeMutableRawPointer.self)");
            writer.WriteLine($"let arg{argIdx} = Unmanaged<{swiftTypeName}>.fromOpaque(rawPtr{argIdx}).takeUnretainedValue()");
        }
        else if (IsIndirectStructType(param.SwiftTypeSpec))
        {
            // Struct parameter: load raw pointer, then assumingMemoryBound(to:).pointee
            // Use var for inout params so the value can be passed by reference
            var binding = param.IsInOut ? "var" : "let";
            var swiftTypeName = GetSwiftConcreteTypeName(param.SwiftTypeSpec);
            writer.WriteLine($"let rawPtr{argIdx} = arg{argIdx}Ptr.load(as: UnsafeMutableRawPointer.self)");
            writer.WriteLine($"{binding} arg{argIdx} = rawPtr{argIdx}.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
        }
        else
        {
            // Blittable parameter: direct load
            // Use var for inout params so the value can be passed by reference.
            // GetSwiftBlittableTypeName handles Swift pointer types (OpaquePointer, … — which map
            // to nint/IntPtr but must keep their bare Swift name), genuine primitives, and value
            // types that project to a primitive (e.g. Foundation.Date → double) but must load as
            // their real type. ABI JSON resolves typealiases (e.g., SQLiteStatement →
            // Swift.OpaquePointer), so checking the TypeSpec name covers alias-backed pointers too.
            var binding = param.IsInOut ? "var" : "let";
            var swiftType = GetSwiftBlittableTypeName(param.SwiftTypeSpec);
            writer.WriteLine($"{binding} arg{argIdx} = arg{argIdx}Ptr.load(as: {swiftType}.self)");
        }
    }

    /// <summary>
    /// Emits writeback code for inout parameters after a method call completes.
    /// Stores the (potentially mutated) local value back through the caller's pointer.
    /// Uses UnsafeMutableRawPointer(mutating:) because the param type is UnsafeRawPointer
    /// but the C# caller provides a mutable buffer.
    /// </summary>
    private void EmitInoutWriteback(SwiftWriter writer, MethodDecl method)
    {
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (param.IsInOut)
            {
                string swiftType;
                if (IsIndirectStructType(param.SwiftTypeSpec))
                    swiftType = GetSwiftConcreteTypeName(param.SwiftTypeSpec) ?? param.SwiftTypeSpec.ToString();
                else
                    swiftType = GetSwiftBlittableTypeName(param.SwiftTypeSpec);
                writer.WriteLine($"UnsafeMutableRawPointer(mutating: arg{argIdx}Ptr).assumingMemoryBound(to: {swiftType}.self).pointee = arg{argIdx}");
            }
            argIdx++;
        }
    }

    /// <summary>
    /// Builds labeled Swift argument list from method signature and call args.
    /// </summary>
    private static List<string> BuildLabeledArgs(MethodDecl method, List<string> callArgs)
    {
        var labeledArgs = new List<string>();
        int argIdx = 0;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param);
            var argRef = param.IsInOut ? $"&{callArgs[argIdx]}" : callArgs[argIdx];
            // Call-argument position: a keyword label is legal bare (and backticking it warns),
            // except `inout`, which the parser claims as the parameter modifier first.
            labeledArgs.Add(label == "_"
                ? argRef
                : $"{NameProvider.EscapeSwiftArgumentLabel(label)}: {argRef}");
            argIdx++;
        }
        return labeledArgs;
    }

    /// <summary>
    /// Gets the Swift parameter label for a method argument.
    /// Mirrors EveryProtocolEmitter.GetSwiftParameterLabel logic.
    /// </summary>
    private static string GetSwiftParameterLabel(ArgumentDecl param)
    {
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || IsGeneratedArgName(param.Name))
            return "_";

        // Strip C# keyword prefix
        if (param.Name.Length > 1 && param.Name[0] == '_')
        {
            var possibleKeyword = param.Name.Substring(1);
            if (CSharpKeywords.Contains(possibleKeyword))
                return possibleKeyword;
        }

        return param.Name;
    }

    private static bool IsGeneratedArgName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("arg"))
            return false;
        return name.Length > 3 && name.Substring(3).All(char.IsDigit);
    }

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "for", "in", "is", "as", "if", "else", "do", "while", "return",
        "break", "continue", "switch", "case", "default", "try", "catch",
        "throw", "new", "this", "base", "null", "true", "false", "class",
        "struct", "enum", "interface", "public", "private", "protected",
        "internal", "static", "readonly", "const", "override", "virtual",
        "abstract", "sealed", "async", "await", "var", "object", "string",
        "int", "long", "float", "double", "bool", "void", "ref", "out",
        "params", "event", "delegate", "operator", "implicit", "explicit",
        "where", "get", "set", "value", "partial", "using", "namespace"
    };

    /// <summary>
    /// Canonical witness-accessor slot-index key. Keys on the method name plus each parameter's
    /// RAW Swift type spec (not the projected C# type), so two overloads whose distinct Swift
    /// parameter types both project to the same C# fallback (Swift.AnyType) are still counted as
    /// two separate witness-table requirements — exactly as the Swift producer numbers them.
    /// This is the SINGLE key the three witness-dispatch walks must agree on for SBW symbol
    /// indices to line up: the producer here, the C# P/Invoke decl walk
    /// (ProtocolProxyEmitter.SwiftObject.EmitWitnessDispatchPInvokes) and the C# call-site walk
    /// (ProtocolProxyEmitter.InterfaceImpl.EmitInterfaceImplementation). Allocating the index on
    /// ProtocolSignatureHelper.GetMethodSignatureKey (the projected C# key) on the consumer side
    /// collapses such an overload pair to one index, shifting every later dispatchable method's
    /// SBW index → EntryPointNotFoundException at runtime.
    ///
    /// Argument labels are INTENTIONALLY OMITTED here — and this is the opposite choice from
    /// <see cref="EveryProtocolEmitter.GetMethodKey"/>, which DOES include them. The difference
    /// is deliberate, not an oversight: the forward/SBW index is an internal symbol-naming
    /// convention shared only between this producer walk and the two C# consumer walks (all three
    /// key off THIS method), and the accessor body dispatches by Swift source-level call — it is
    /// NOT pinned to Swift's ABI witness-table ordering. This key is therefore the label-blind
    /// FALLBACK: the three forward walks do not call it directly, they call
    /// ProtocolMethodDisambiguator.EffectiveWitnessSlotKey, which returns THIS label-blind key for
    /// an ordinary method but the label-INCLUSIVE EveryProtocolEmitter.GetMethodKey for a
    /// disambiguated label-only pair. So a label-only overload pair (`func move(to: Int32)` /
    /// `func move(from: Int32)`, identical name+types, differing only by label) that survives as two
    /// distinct C# members SPLITS into two slots on all three walks in lockstep (and shifts any
    /// trailing method's index by one) — while a pure type-erasure pair, which the disambiguator
    /// leaves alone, still collapses here via this key exactly as before. Making THIS key
    /// unconditionally label-sensitive would wrongly split type-erasure pairs too and re-open the SBW
    /// index-shift it exists to prevent; the split is opted into per-pair by the disambiguator, not by
    /// this key. The reverse/vtable axis matches Swift's label-distinguished slot allocation, which is
    /// why EveryProtocolEmitter.GetMethodKey keeps labels. Pinned by
    /// WitnessDispatchEmitterTests.OverloadDisambiguation_LabelOnlyOverloadPair_SplitsIntoTwoSlots_TrailingMethodShifts.
    /// </summary>
    internal static string GetMethodKey(MethodDecl method)
    {
        // The async effect is part of the key so `func m()` and `func m() async` each get their
        // own witness-accessor symbol. They are distinct Swift witness-table requirements; an
        // async-insensitive key would drop the second method before it is assigned an index,
        // emitting only one `_cdecl` accessor for the pair (mirrors
        // ProtocolSignatureHelper.GetMethodSignatureKey's default and EveryProtocolEmitter.GetMethodKey).
        var asyncSuffix = method.IsAsync ? ":async" : "";
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")" + asyncSuffix;
    }

    #endregion
}
