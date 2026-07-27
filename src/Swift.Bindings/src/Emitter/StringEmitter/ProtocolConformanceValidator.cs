// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Validates that a concrete type can fully implement a protocol interface.
/// Used to prevent CS0535 (missing interface members) and CS0738 (return type mismatch) errors.
/// </summary>
public class ProtocolConformanceValidator
{
    private readonly ModuleDecl _moduleDecl;
    private readonly ITypeDatabase _typeDatabase;
    private readonly ProtocolExtensionDefaultsIndex? _extensionDefaultsIndex;
    private readonly ModuleEmissionContext? _emissionContext;

    public ProtocolConformanceValidator(ModuleDecl moduleDecl, ITypeDatabase typeDatabase,
        ProtocolExtensionDefaultsIndex? extensionDefaultsIndex = null,
        ModuleEmissionContext? emissionContext = null)
    {
        _moduleDecl = moduleDecl;
        _typeDatabase = typeDatabase;
        _extensionDefaultsIndex = extensionDefaultsIndex;
        _emissionContext = emissionContext;
    }

    /// <summary>
    /// Looks up a protocol by name in the module.
    /// Supports both simple names ("ImageDecoding") and module-qualified names ("ImagePipeline.ImageDecoding").
    /// Prefers module-qualified matches to avoid ambiguity with same-name protocols.
    /// Returns null for cross-module protocols (e.g., Swift.Equatable).
    /// </summary>
    public ProtocolDecl? FindProtocol(string protocolName)
    {
        // If input contains a dot, it's likely module-qualified - try that first for precision
        if (protocolName.Contains('.'))
        {
            // Try exact module-qualified match first (most precise)
            var qualifiedResult = _moduleDecl.Protocols.FirstOrDefault(p =>
                p.SwiftTypeName?.ModuleQualifiedName == protocolName);
            if (qualifiedResult != null)
                return qualifiedResult;

            // Extract simple name and try that (fallback for cross-module references)
            var lastDot = protocolName.LastIndexOf('.');
            var simpleName = protocolName.Substring(lastDot + 1);

            // If multiple protocols have same simple name, prefer one from our module
            var candidates = _moduleDecl.Protocols.Where(p => p.Name == simpleName).ToList();
            if (candidates.Count == 1)
                return candidates[0];
            if (candidates.Count > 1)
            {
                // Prefer protocol whose module matches the prefix
                var modulePrefix = protocolName.Substring(0, lastDot);
                var moduleMatch = candidates.FirstOrDefault(p =>
                    p.SwiftTypeName?.Module == modulePrefix);
                return moduleMatch ?? candidates[0]; // Fall back to first if no module match
            }

            return null;
        }

        // Simple name lookup - only one protocol should match
        var result = _moduleDecl.Protocols.FirstOrDefault(p => p.Name == protocolName);
        return result;
    }

    /// <summary>
    /// Checks if a protocol has any members that would appear in its generated C# interface.
    /// If ALL members are filtered out (e.g., UIKit dependencies), the protocol
    /// handler won't generate the interface, and adding it to a class declaration would cause CS0246.
    /// Includes static properties and static methods (emitted as static abstract).
    /// </summary>
    public bool HasEmittableInterfaceMembers(ProtocolDecl protocolDecl)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var evaluator = new MemberGateEvaluator(_typeDatabase);

        // Check if any property (instance or static) would be emitted in the interface
        foreach (var prop in protocolDecl.Properties)
        {
            if (MemberEmissionValidator.ReferencesUnsupportedModule(prop.SwiftTypeSpec, _typeDatabase))
                continue;
            var result = evaluator.EvaluateProperty(prop, _moduleDecl, protocolDecl);
            if (!result.IsSkipped)
                return true;  // At least one property would appear in the interface
        }

        // Check if any non-constructor method (instance or static) would be emitted
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor) continue;
            var result = evaluator.EvaluateMethod(method, _moduleDecl, protocolDecl);
            if (!result.IsSkipped)
                return true;  // At least one method would appear in the interface
        }

        // Check if any non-static subscript would be emitted in the interface
        // (static subscripts are still skipped — C# has no static indexers)
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic) continue;
            var result = evaluator.EvaluateSubscript(subscript, _moduleDecl, protocolDecl);
            if (!result.IsSkipped)
                return true;  // At least one subscript would appear in the interface
        }

        // Protocol has no emittable interface members — it's either empty (marker) or all-filtered.
        // Empty protocols (no members at all) still get generated → return true.
        bool hasMembers = protocolDecl.Properties.Any() ||
                         protocolDecl.Methods.Any(m => !m.IsConstructor) ||
                         protocolDecl.Subscripts.Any(s => !s.IsStatic);
        return !hasMembers;  // true if empty protocol, false if all members were filtered
    }

    /// <summary>
    /// Resolves a conformer's PAT (Protocol-with-Associated-Type) bindings to fully-qualified
    /// C# type names when ALL bindings are concrete (closed PAT). Returns false for OPEN PATs
    /// where any binding depends on a conformer-side generic type parameter, an associated-type
    /// reference (<c>Self.Element</c>), or a nested generic — leaving such conformances opaque
    /// at the C# nominal-assignability layer and routed exclusively through the typeof(object)
    /// PAT box.
    ///
    /// Used by <see cref="ProtocolConformanceHelper.GetImplementedInterfaces"/> to emit the
    /// closed generic interface in a conformer's implements list (e.g.
    /// <c>StringLabel : ILabelledContainer&lt;Swift.SwiftString&gt;</c>) so that consumers can
    /// pass the conformer where a typed existential parameter is expected. Pairs with the
    /// runtime typed-PAT fallback in the per-type
    /// <c>GetProtocolConformanceDescriptor&lt;TProtocol&gt;()</c> body — together they close
    /// Constrained PAT existential Cases 1 + 2: concrete-arg `any P&lt;X&gt;` and plain `any P`.
    /// </summary>
    /// <param name="conformer">The conforming type (StructDecl/ClassDecl/EnumDecl).</param>
    /// <param name="protocolDecl">The PAT protocol whose bindings to resolve.</param>
    /// <param name="bindingCSharpNames">The fully-qualified C# names for each associated type, in declaration order. Empty when the method returns false.</param>
    /// <returns>True iff every associated type binds to a concrete TypeRecord-resolvable type.</returns>
    public bool TryResolveClosedPatBindings(
        TypeDecl conformer,
        ProtocolDecl protocolDecl,
        out List<string> bindingCSharpNames)
    {
        bindingCSharpNames = new List<string>();

        if (protocolDecl.AssociatedTypes.Count == 0)
            return false;

        if (conformer.SwiftTypeName == null || protocolDecl.SwiftTypeName == null)
            return false;

        var conformerKey = conformer.SwiftTypeName.ModuleQualifiedName;
        var protocolKey = protocolDecl.SwiftTypeName.ModuleQualifiedName;

        foreach (var at in protocolDecl.AssociatedTypes)
        {
            if (!_moduleDecl.ConformanceGraph.TryResolve(conformerKey, protocolKey, at.Name, out var resolved) ||
                resolved is null)
            {
                return false;
            }

            // Open PAT: binding is the conformer's own generic type parameter (e.g.,
            // GenericContainer<U> where Label == U). The closed interface depends on a
            // conformer-side parameter and the typeof(object) PAT box still applies.
            if (resolved is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                return false;

            // Open PAT: binding is an associated-type reference (e.g., Self.Element).
            // These never resolve to a concrete C# type.
            if (resolved is AssociatedTypeReferenceSpec)
                return false;

            // Conservative: nested generics (e.g., Label == Array<Int>) require recursive lowering.
            // Mirrors the same gating ExistentialHandler.TryResolveExistentialGenericArgs uses.
            if (resolved is NamedTypeSpec n && n.GenericParameters.Count > 0)
                return false;

            // Resolve to fully-qualified C# type name via the type database.
            string? csName = null;
            if (resolved is NamedTypeSpec resolvedNamed)
            {
                try
                {
                    var argSwiftName = SwiftTypeName.FromTypeSpec(resolvedNamed);
                    if (_typeDatabase.TryGetTypeRecord(argSwiftName, out var argRecord) &&
                        argRecord.CSharpTypeName != null)
                    {
                        csName = argRecord.CSharpTypeName.FullyQualifiedName;
                    }
                }
                catch
                {
                    csName = null;
                }
            }

            if (string.IsNullOrEmpty(csName))
                return false;

            bindingCSharpNames.Add(csName!);
        }

        return bindingCSharpNames.Count == protocolDecl.AssociatedTypes.Count;
    }

    /// <summary>
    /// Checks if a CONCRETE TYPE can fully implement a protocol interface.
    /// Validates the TYPE'S MEMBERS (not protocol requirements) against interface.
    /// </summary>
    /// <param name="concreteType">The actual type (e.g., ImageDecoders.Empty)</param>
    /// <param name="protocolDecl">The protocol it claims to implement</param>
    /// <param name="visited">Cycle protection - tracks visited protocols</param>
    /// <returns>True if the type can fully implement the protocol</returns>
    public bool CanFullyImplementProtocol(
        TypeDecl concreteType,
        ProtocolDecl protocolDecl,
        HashSet<string>? visited = null)
    {
        // Cycle protection with module-qualified name
        visited ??= new HashSet<string>();
        var qualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                         ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        if (!visited.Add(qualifiedName))
            return true;

        // Resolve the concrete type's C# name for Self-typed position matching.
        // Always resolve — protocols may use Self (τ_0_0) in method signatures without
        // HasSelfRequirement being explicitly set (e.g., AnyInterpolatable._interpolate
        // returns Self but the interface emits AnyType). Without conformingTypeName,
        // AreTypesCompatible rejects AnyType vs the concrete type name.
        string? conformingTypeName = null;
        if (concreteType.SwiftTypeName != null &&
            _typeDatabase.TryGetTypeRecord(concreteType.SwiftTypeName, out var concreteRecord))
        {
            conformingTypeName = concreteRecord.CSharpTypeName.FullyQualifiedName;
        }

        // The per-member conformance-keep checks below use the static CanEmit* helpers, which carry
        // FEWER skip gates than the actual member-emission pipeline (ValidateMethodEmission /
        // ValidatePropertyEmission). When a witness passes CanEmit* but the emitter would Skip it —
        // or route it to a sibling static extension class (RoutedElsewhere), which cannot satisfy a
        // C# instance interface — keeping the conformance emits `: IFoo` with no satisfying member
        // (CS0535 at consumer compile). Re-running the emitter's own pipeline keeps the two decisions
        // in lockstep. The pipeline is a side-effect-free decision function (recording happens in the
        // caller, not here), so it is safe to invoke during conformance-list selection. The context
        // mirrors HandleBaseDecl's (per-type PInvokeHelperContext + emission context); when no
        // emission context is available (legacy/test paths) we pass null and the pipeline's
        // context-free gates still apply.
        var emissionPipeline = new MemberValidationPipeline(_typeDatabase);
        var emissionValidationCtx = _emissionContext == null
            ? null
            : new ValidationContext(
                _typeDatabase,
                PInvokeHelperContext.CreateIfGeneric(concreteType, _typeDatabase),
                _emissionContext,
                parentType: null,
                moduleDecl: null,
                siblingPropertyNames: null,
                conductor: null);

        // Track interface requirements (mirrors ProtocolHandler dedup)
        var requiredProperties = new HashSet<string>();
        var requiredSubscripts = new HashSet<string>();
        var requiredMethods = new HashSet<string>();

        // Lazy-init conformance names for extension default checks (reused for properties + methods)
        HashSet<string>? conformanceNames = null;

        // For each INTERFACE PROPERTY requirement (instance + static):
        // Static properties are emitted as static virtual (with throw body default) to
        // avoid CS8920 when the interface is used as a type argument. If a conforming type
        // has a matching static member, we validate type compatibility. If it doesn't, the
        // static virtual default satisfies the C# interface contract, so we don't drop the
        // conformance. This is conservative-safe: Swift guarantees the member exists (via
        // the type or extension default), and the C# interface is satisfied by the default.
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        foreach (var protoProperty in protocolDecl.Properties)
        {
            if (protoProperty.IsStatic) continue; // Static: validated below with lenient matching
            var propertyKey = protoProperty.Name;
            if (!requiredProperties.Add(propertyKey)) continue;

            // Skip properties that won't appear in the interface (mirrors ProtocolHandler gates)
            if (IsPropertySkippedFromInterface(protoProperty, boundGenericsHandler, protocolDecl))
                continue;

            // Find matching property in CONCRETE TYPE
            var concreteProperty = FindMatchingProperty(concreteType, protoProperty);
            if (concreteProperty == null)
            {
                // Check if a protocol extension provides a default implementation
                if (_extensionDefaultsIndex != null)
                {
                    conformanceNames ??= GetQualifiedConformanceNames(concreteType);
                    var protoRequiresSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
                    if (_extensionDefaultsIndex.HasPropertyDefault(qualifiedName, protoProperty.Name,
                        conformanceNames, protoRequiresSetter))
                        continue; // Satisfied by extension default
                }
                return false;  // CS0535: member not found
            }

            // Validate accessor contract: protocol { get set } requires concrete { get set }
            var protoHasGetter = protoProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var protoHasSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
            var concreteHasGetter = concreteProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var concreteHasSetter = concreteProperty.Accessors.OfType<SetAccessorDecl>().Any();
            if ((protoHasGetter && !concreteHasGetter) || (protoHasSetter && !concreteHasSetter))
                return false;  // CS0535: missing accessor
            // Setter-only closure: PropertyHandler strips getter from closure properties where
            // CanInvokeFromCSharp fails. If protocol requires getter, conformance can't be satisfied.
            if (protoHasGetter && MemberEmissionValidator.IsSetterOnlyClosureProperty(concreteProperty, _typeDatabase))
                return false;  // CS0535: getter stripped by setter-only closure logic

            // Validate CONCRETE property can be emitted
            var skipReason = MemberEmissionValidator.CanEmitProperty(
                concreteProperty, _typeDatabase, out _, out var concreteTypeProjected);
            if (skipReason != null)
            {
                // The concrete type has the property but can't emit it (e.g., AnyType fallback).
                // If the protocol interface will emit this as a DIM, the concrete type doesn't need
                // to provide it — the DIM satisfies the C# interface contract. Use the SAME predicate
                // the interface emitter uses for DIM emission — direct OR inherited sub-protocol
                // default — so the validator and emitter agree.
                if (_extensionDefaultsIndex != null)
                {
                    var protoRequiresSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
                    if (_extensionDefaultsIndex.HasPropertyDefault(qualifiedName, protoProperty.Name,
                        requiresSetter: protoRequiresSetter))
                        continue; // Satisfied by DIM in interface
                }
                return false;  // CS0535: member will be skipped
            }

            // Agreement gate: ValidatePropertyEmission carries constrained-extension and dependent-
            // member gates CanEmitProperty lacks. If the emitter would skip the accessor, a DIM
            // default can still satisfy the interface; otherwise the conformance must drop (CS0535).
            var propertyEmission = emissionPipeline.ValidatePropertyEmission(concreteProperty, emissionValidationCtx);
            if (!propertyEmission.ShouldEmit && !propertyEmission.IsSynthesized)
            {
                if (_extensionDefaultsIndex != null)
                {
                    // Same predicate the interface emitter uses for DIM emission (direct OR inherited
                    // sub-protocol default), so the validator and emitter agree.
                    var protoRequiresSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
                    if (_extensionDefaultsIndex.HasPropertyDefault(qualifiedName, protoProperty.Name,
                        requiresSetter: protoRequiresSetter))
                        continue; // Satisfied by DIM in interface
                }
                return false;  // CS0535: accessor will be skipped at emission
            }

            // Check type compatibility (CS0738)
            var interfaceType = GetInterfacePropertyType(protoProperty, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceType, concreteTypeProjected, conformingTypeName))
                return false;  // CS0738: types don't match
        }

        // Static properties: lenient validation. If the concrete type HAS the static member,
        // validate accessor parity and type compatibility (same checks as instance properties).
        // If it doesn't, the static virtual default satisfies the interface — we don't drop
        // conformances for missing statics because the C# compiler won't flag them (static
        // virtual has a default body). This is a deliberate compromise: Swift guarantees the
        // member exists (on the type or via extension default), so missing means our extension
        // default index has a coverage gap, not that the conformance is wrong.
        foreach (var protoProperty in protocolDecl.Properties)
        {
            if (!protoProperty.IsStatic) continue;
            if (!requiredProperties.Add(protoProperty.Name)) continue;
            if (IsPropertySkippedFromInterface(protoProperty, boundGenericsHandler, protocolDecl))
                continue;

            var concreteProperty = FindMatchingProperty(concreteType, protoProperty, isStatic: true);
            if (concreteProperty == null)
                continue; // Static virtual default satisfies the interface

            // Validate accessor contract: protocol { get set } requires concrete { get set }
            var protoHasGetter = protoProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var protoHasSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
            var concreteHasGetter = concreteProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var concreteHasSetter = concreteProperty.Accessors.OfType<SetAccessorDecl>().Any();
            if ((protoHasGetter && !concreteHasGetter) || (protoHasSetter && !concreteHasSetter))
                return false;  // CS0535: missing accessor
            // Setter-only closure: PropertyHandler strips getter from closure properties where
            // CanInvokeFromCSharp fails. If protocol requires getter, conformance can't be satisfied.
            if (protoHasGetter && MemberEmissionValidator.IsSetterOnlyClosureProperty(concreteProperty, _typeDatabase))
                return false;  // CS0535: getter stripped by setter-only closure logic

            // Validate CONCRETE property can be emitted
            var skipReason = MemberEmissionValidator.CanEmitProperty(
                concreteProperty, _typeDatabase, out _, out var concreteTypeProjected);
            if (skipReason != null)
                return false; // CS0535: member present but can't be emitted

            // Agreement gate (same rationale as the instance-property path). Static members have no
            // DIM fallback here, mirroring the CanEmitProperty branch directly above.
            var staticPropertyEmission = emissionPipeline.ValidatePropertyEmission(concreteProperty, emissionValidationCtx);
            if (!staticPropertyEmission.ShouldEmit && !staticPropertyEmission.IsSynthesized)
                return false; // CS0535: accessor will be skipped at emission

            // Check type compatibility (CS0738)
            var staticInterfaceType = GetInterfacePropertyType(protoProperty, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(staticInterfaceType, concreteTypeProjected, conformingTypeName))
            {
                // A static requirement whose Swift type references Self (`static var shared: Self`)
                // cannot be spelled in a non-Self-requirement interface — it degrades to AnyType
                // there, while the concrete type's member uses its real type. That mismatch is
                // compile-benign: statics emit as `static virtual` with a throwing default body,
                // so the mismatched concrete member simply doesn't override (through-interface
                // dispatch throws loudly; direct access on the concrete type works). Rejecting
                // the WHOLE conformance here instead breaks every generic constraint on the
                // protocol (CS0311: the conforming type never appears to implement it).
                var mismatchIsUnspellableSelf = !protocolDecl.HasSelfRequirement
                    && EveryProtocolEmitter.ContainsSelfTypeParam(protoProperty.SwiftTypeSpec);
                if (!mismatchIsUnspellableSelf)
                    return false; // CS0738: types don't match
            }
        }

        // For each INTERFACE SUBSCRIPT requirement:
        foreach (var protoSubscript in protocolDecl.Subscripts)
        {
            if (protoSubscript.IsStatic) continue;
            var subscriptKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(protoSubscript, _typeDatabase, protocolDecl);
            if (!requiredSubscripts.Add(subscriptKey)) continue;

            // Skip subscripts that won't appear in the interface (mirrors ProtocolHandler gates /
            // the property + method loops above). A dropped requirement is not a live `: IFoo` member.
            if (IsSubscriptSkippedFromInterface(protoSubscript, protocolDecl))
                continue;

            // Find matching subscript in CONCRETE TYPE
            var concreteSubscript = FindMatchingSubscript(concreteType, protoSubscript, protocolDecl);
            if (concreteSubscript == null)
                return false;

            // Validate accessor contract for subscript
            var protoHasGetter = protoSubscript.HasGetter;
            var protoHasSetter = protoSubscript.HasSetter;
            var concreteHasGetter = concreteSubscript.HasGetter;
            var concreteHasSetter = concreteSubscript.HasSetter;
            if ((protoHasGetter && !concreteHasGetter) || (protoHasSetter && !concreteHasSetter))
                return false;

            var skipReason = MemberEmissionValidator.CanEmitSubscript(
                concreteSubscript, _typeDatabase, out _, out var concreteReturnType);
            if (skipReason != null)
                return false;

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceSubscriptReturnType(protoSubscript, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType, conformingTypeName))
                return false;
        }

        // For each INTERFACE METHOD requirement (instance only):
        var emittedCSharpKeys = new HashSet<string>();
        var emittedResolvedSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var protoMethod in protocolDecl.Methods)
        {
            if (protoMethod.IsConstructor || protoMethod.MethodType == MethodType.Static) continue;
            // Effective* keys keep the validator's emitted-set iteration in lockstep with the emitter:
            // a label-only-overload pair iterates as two distinct requirements (distinct slot/projected
            // keys), so each is matched against its own concrete counterpart instead of collapsing to one.
            var methodKey = ProtocolMethodDisambiguator.EffectiveRawKey(protoMethod, protocolDecl, _typeDatabase);
            if (!requiredMethods.Add(methodKey)) continue;

            // Skip methods that won't appear in the interface (mirrors ProtocolHandler gates)
            if (IsMethodSkippedFromInterface(protoMethod, boundGenericsHandler, protocolDecl))
                continue;

            var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(protoMethod, protocolDecl, _typeDatabase, propertyNames: null);
            if (!emittedCSharpKeys.Add(projectedKey))
                continue;

            var resolvedSignature = BuildInterfaceMethodSignature(protoMethod, protocolDecl);
            if (!emittedResolvedSignatures.Add(resolvedSignature))
                continue;

            // Find matching method in CONCRETE TYPE
            var concreteMethod = FindMatchingMethod(concreteType, protoMethod, protocolDecl);
            if (concreteMethod == null)
            {
                // Check if a protocol extension provides a default implementation
                if (_extensionDefaultsIndex != null)
                {
                    conformanceNames ??= GetQualifiedConformanceNames(concreteType);
                    var extMethodKey = ProtocolExtensionEmitter.BuildMethodKey(protoMethod);
                    if (_extensionDefaultsIndex.HasMethodDefault(qualifiedName, extMethodKey, conformanceNames))
                        continue; // Satisfied by extension default
                }
                return false;
            }

            var skipReason = MemberEmissionValidator.CanEmitMethod(
                concreteMethod, _typeDatabase, out _, out var concreteReturnType);
            if (skipReason != null)
            {
                // The concrete type has the method but can't emit it. If the interface will carry a
                // DIM for this requirement, the conformer leans on it instead of providing a witness
                // (mirrors the instance-property CanEmit rescue below). Use the SAME predicate the
                // interface emitter uses to decide DIM emission — direct OR inherited sub-protocol
                // default — so the validator and emitter agree; otherwise drop the conformance (CS0535).
                if (_extensionDefaultsIndex != null &&
                    _extensionDefaultsIndex.HasMethodDefault(qualifiedName, ProtocolExtensionEmitter.BuildMethodKey(protoMethod)))
                    continue; // Satisfied by DIM in interface
                return false;
            }

            // Agreement gate: the witness passes the lightweight CanEmitMethod, but the real emitter
            // applies additional gates (async on an unspecialized generic parent, parent-internal
            // async/closure, variadic generic pack, tuple-element marshalling, constrained-extension
            // and CSM routing). A genuine Skip means the member won't be emitted; RoutedElsewhere
            // means it's emitted only as a sibling extension method (which doesn't implement the
            // instance interface). Either way the conformance must drop. Synthesized members ARE
            // emitted, so they still satisfy the requirement.
            var methodEmission = emissionPipeline.ValidateMethodEmission(concreteMethod, emissionValidationCtx);
            if (!methodEmission.ShouldEmit && !methodEmission.IsSynthesized)
            {
                // Agreement gate: the emitter would skip this witness. If the interface will carry a
                // DIM for this requirement, the conformer leans on it (mirrors the instance-property
                // agreement-gate rescue). Use the SAME predicate the interface emitter uses — direct
                // OR inherited sub-protocol default — so validator and emitter agree; else drop (CS0535).
                if (_extensionDefaultsIndex != null &&
                    _extensionDefaultsIndex.HasMethodDefault(qualifiedName, ProtocolExtensionEmitter.BuildMethodKey(protoMethod)))
                    continue; // Satisfied by DIM in interface
                return false;
            }

            // Handler-layer agreement: the async closure bridge gate deliberately does NOT live in
            // ValidateMethodEmission. The closure shape itself is supported — what decides whether
            // the (context, startFunc) bridge has a Swift adapter is the CONTAINING member's wrapper
            // flavor, which only the emitter handlers know. So the pipeline above says "emit" while
            // the handler skips, the interface keeps the requirement, and the conformer omits its
            // witness (CS0535). Mirror the handler gate here so the conformance list tracks what the
            // class body will actually contain.
            if (HasUnbridgeableAsyncClosure(concreteMethod))
            {
                if (_extensionDefaultsIndex != null &&
                    _extensionDefaultsIndex.HasMethodDefault(qualifiedName, ProtocolExtensionEmitter.BuildMethodKey(protoMethod)))
                    continue; // Satisfied by DIM in interface
                return false;
            }

            // Check C# name parity: the concrete type's method is emitted via GetPublicMethodName
            // with the concrete type's property names. If a property collision causes a "Method"
            // suffix, the emitted name won't match the interface member name → CS0535.
            // GetPublicMethodName accounts for Get prefix (noun-only + return value), Async suffix,
            // and property collision — so we must use it, not just ToPascalCase.
            var concreteProperties = concreteType switch
            {
                ClassDecl cd => cd.Properties,
                StructDecl sd => sd.Properties,
                EnumDecl ed => ed.Properties,
                _ => Enumerable.Empty<PropertyDecl>()
            };
            var concretePropertyNames = new HashSet<string>(
                concreteProperties.Select(p => NameProvider.GetPropertyName(p.Name)));
            // The emitter shapes this method's name against the NSObject properties an ObjC-rooted
            // class inherits, not just the type's own declared ones. Predicting the name from the
            // declared properties alone reads `handle(url:)` as `Handle`, matches the interface
            // slot, and keeps a conformance the emitter then contradicts by emitting `HandleMethod`
            // — the class ends up declaring an interface member it never defines (CS0535). Seed
            // from the same fact the emitter seeds from so the prediction tracks the emission.
            NameProvider.SeedObjCRootedInheritedPropertyNames(concretePropertyNames, concreteType);
            // Predict via ForMethod — the SAME context builder the emitted name
            // (MethodEnvironment.CSharpMethodName) derives from — so the prediction can never
            // silently drop a collision-shaping axis (e.g. ParentGenericParameterNames: a witness
            // `t(duration:)` on a generic `Container<T>` emits as `TMethod`; a hand-rolled
            // positional call here would predict `T`, keep the conformance, and strand the
            // interface member unimplemented — CS0535).
            var concreteEmittedName = NameProvider.GetPublicMethodName(
                PublicMethodNameContext.ForMethod(concreteMethod, concretePropertyNames));

            // Compare with the interface method name (computed without property collision context)
            var protoReturnTypeSpec = protoMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool protoHasReturn = protoReturnTypeSpec != null && !protoReturnTypeSpec.IsEmptyTuple;
            var protoIsSelfReturning = MethodEnvironment.IsSelfReturningMethod(protoMethod);
            var interfaceMethodName = NameProvider.GetPublicMethodName(
                ProtocolMethodDisambiguator.EffectiveNameInput(protoMethod, protocolDecl, _typeDatabase), protoMethod.IsAsync,
                hasReturnValue: protoHasReturn,
                isSelfReturning: protoIsSelfReturning,
                parameterCount: protoMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                isMutating: protoMethod.IsMutating);

            if (concreteEmittedName != interfaceMethodName)
                return false;  // CS0535: method names diverge due to collision resolution

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceMethodReturnType(protoMethod, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType, conformingTypeName))
                return false;

            // Check parameter type compatibility (CS0535/CS0738)
            // The interface emits projected types for protocol params (e.g., τ_0_0 → AnyType),
            // but the concrete type uses its actual types (e.g., AnyDifferentiable).
            // If these don't match, C# will reject the conformance.
            if (!AreMethodParamsCompatible(protoMethod, concreteMethod, protocolDecl, conformingTypeName))
                return false;
        }

        // Static methods: lenient validation. If the concrete type HAS a matching static method,
        // validate emitted name parity, return type, and parameter compatibility (same checks
        // as instance methods). If it doesn't, the static virtual default satisfies the
        // interface. Same rationale as static property validation above.
        foreach (var protoMethod in protocolDecl.Methods)
        {
            if (protoMethod.IsConstructor || protoMethod.MethodType != MethodType.Static) continue;
            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolDecl);
            if (!requiredMethods.Add(methodKey)) continue;
            if (IsMethodSkippedFromInterface(protoMethod, boundGenericsHandler, protocolDecl))
                continue;

            var concreteMethod = FindMatchingStaticMethod(concreteType, protoMethod, protocolDecl);
            if (concreteMethod == null)
                continue; // Static virtual default satisfies the interface

            var skipReason = MemberEmissionValidator.CanEmitMethod(
                concreteMethod, _typeDatabase, out _, out var concreteReturnType);
            if (skipReason != null)
                return false;

            // Agreement gate (same rationale as the instance-method path): drop the conformance if
            // the emitter would Skip or route the witness elsewhere.
            var staticMethodEmission = emissionPipeline.ValidateMethodEmission(concreteMethod, emissionValidationCtx);
            if (!staticMethodEmission.ShouldEmit && !staticMethodEmission.IsSynthesized)
                return false;

            // Handler-layer agreement — see the instance-method path for the rationale, including
            // its DIM rescue: a requirement the interface satisfies with a default interface member
            // does not need this witness at all, so an unbridgeable witness is no reason to drop the
            // conformance. Static requirements reach the interface through the same protocol-
            // extension default path (C# 11 static abstract members can carry a default body), so
            // the rescue applies identically here; omitting it made the static path drop
            // conformances the instance path would have kept for the same shape.
            if (HasUnbridgeableAsyncClosure(concreteMethod))
            {
                if (_extensionDefaultsIndex != null &&
                    _extensionDefaultsIndex.HasMethodDefault(qualifiedName, ProtocolExtensionEmitter.BuildMethodKey(protoMethod)))
                    continue; // Satisfied by DIM in interface
                return false;
            }

            // Check C# name parity (same logic as instance methods)
            var concreteProperties = concreteType switch
            {
                ClassDecl cd => cd.Properties,
                StructDecl sd => sd.Properties,
                EnumDecl ed => ed.Properties,
                _ => Enumerable.Empty<PropertyDecl>()
            };
            var concretePropertyNames = new HashSet<string>(
                concreteProperties.Select(p => NameProvider.GetPropertyName(p.Name)));
            // The emitter shapes this method's name against the NSObject properties an ObjC-rooted
            // class inherits, not just the type's own declared ones. Predicting the name from the
            // declared properties alone reads `handle(url:)` as `Handle`, matches the interface
            // slot, and keeps a conformance the emitter then contradicts by emitting `HandleMethod`
            // — the class ends up declaring an interface member it never defines (CS0535). Seed
            // from the same fact the emitter seeds from so the prediction tracks the emission.
            NameProvider.SeedObjCRootedInheritedPropertyNames(concretePropertyNames, concreteType);
            // Same ForMethod parity rationale as the instance-method path above: the prediction
            // must fold in every axis the emission folds in (ParentGenericParameterNames included).
            var concreteEmittedName = NameProvider.GetPublicMethodName(
                PublicMethodNameContext.ForMethod(concreteMethod, concretePropertyNames));

            var protoReturnTypeSpec = protoMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool protoHasReturn = protoReturnTypeSpec != null && !protoReturnTypeSpec.IsEmptyTuple;
            var protoIsSelfReturning = MethodEnvironment.IsSelfReturningMethod(protoMethod);
            var interfaceMethodName = NameProvider.GetPublicMethodName(
                protoMethod.Name, protoMethod.IsAsync,
                hasReturnValue: protoHasReturn,
                isSelfReturning: protoIsSelfReturning,
                parameterCount: protoMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                isMutating: protoMethod.IsMutating);

            // A diverged static name is compile-benign, unlike the instance path: the interface
            // member is a `static virtual` with a throwing default body, so a concrete member
            // emitted under a different C# name (e.g. `TMethod` after a generic-parameter
            // collision rename) simply leaves the default in place — the same shape as the
            // member-absent case above. Dropping the whole conformance here would trade a
            // compile-safe kept conformance for CS0311 on every generic constraint.
            if (concreteEmittedName != interfaceMethodName)
                continue;

            // Same unspellable-Self leniency as the static property path: a static requirement
            // that references Self degrades to AnyType in a non-Self-requirement interface, but
            // the `static virtual` throwing default makes the signature mismatch compile-benign,
            // so it must not sink the whole conformance (CS0311 on every generic constraint).
            var staticMismatchIsUnspellableSelf = !protocolDecl.HasSelfRequirement
                && protoMethod.CSSignature.Any(a => EveryProtocolEmitter.ContainsSelfTypeParam(a.SwiftTypeSpec));

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceMethodReturnType(protoMethod, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType, conformingTypeName)
                && !staticMismatchIsUnspellableSelf)
                return false;

            // Check parameter type compatibility (CS0535/CS0738)
            if (!AreMethodParamsCompatible(protoMethod, concreteMethod, protocolDecl, conformingTypeName)
                && !staticMismatchIsUnspellableSelf)
                return false;
        }

        // Recursively validate inherited protocol requirements.
        // When C# interface uses inheritance (IDrawable : IDescribable), the concrete type
        // must satisfy all inherited interface members too.
        foreach (var inheritedProtoSpec in protocolDecl.InheritedProtocols)
        {
            if (inheritedProtoSpec.Name is "Swift.AnyObject" or "AnyObject")
                continue;
            if (inheritedProtoSpec.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                continue;

            // Skip cross-module protocols — must match ProtocolHandler.GetInheritedInterfaceList
            var inheritedModule = inheritedProtoSpec.Module;
            var currentModule = protocolDecl.ModuleDecl?.Name;
            if (!string.IsNullOrEmpty(inheritedModule) && !string.IsNullOrEmpty(currentModule) &&
                inheritedModule != currentModule)
                continue;

            // Only validate same-module protocols (cross-module ones are resolved at link time)
            var inheritedDecl = FindProtocol(inheritedProtoSpec.NameWithoutModule)
                             ?? FindProtocol(inheritedProtoSpec.Name);
            if (inheritedDecl == null)
                continue;

            // Skip protocols with PAT/Self — their interfaces are generic and aren't
            // included in the C# interface inheritance list
            var swiftTypeName = SwiftTypeName.FromTypeSpec(inheritedProtoSpec);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var inheritedRecord))
            {
                if (inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                    inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    continue;
            }

            // Skip underscore-suppressed protocols — their interfaces aren't emitted
            if (_emissionContext != null && swiftTypeName != null &&
                _emissionContext.IsUnderscoreSuppressed(swiftTypeName.ToString()))
                continue;

            if (!CanFullyImplementProtocol(concreteType, inheritedDecl, visited))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets interface property type using SAME projection as ProtocolHandler.EmitInterfaceProperty.
    /// </summary>
    private string GetInterfacePropertyType(PropertyDecl protoProperty, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        if (protoProperty.SwiftTypeSpec is AssociatedTypeReferenceSpec)
            return "?";  // PAT - should have been filtered earlier

        // Use factory with GenericContext for all types including bound generics
        // For Self-requirement protocols, map τ_0_0 → TSelf
        var genericContext = protocolContext.HasSelfRequirement
            ? GenericContext.ForProtocolSelf()
            : GenericContext.Empty;

        var factory = new TypeProjectionFactory();
        var projection = factory.Project(protoProperty.SwiftTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = genericContext
        });
        if (projection != null)
            return projection.PublicType;

        // Bound generic fallback: produce full type name with generic args
        if (protoProperty.SwiftTypeSpec is NamedTypeSpec propBoundGeneric && propBoundGeneric.ContainsGenericParameters)
        {
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(protoProperty.SwiftTypeSpec, genericContext);
        }

        return _typeDatabase.GetTypeRecordOrAnyType(protoProperty.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Gets interface method return type using SAME projection as ProtocolHandler.EmitInterfaceMethod.
    /// </summary>
    private string GetInterfaceMethodReturnType(MethodDecl protoMethod, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        var returnType = "void";

        if (protoMethod.CSSignature.Count > 0)
        {
            var returnArg = protoMethod.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                // Try factory-based projection with GenericContext
                // For Self-requirement protocols, map τ_0_0 → TSelf
                var genericContext = protocolContext.HasSelfRequirement
                    ? GenericContext.ForProtocolSelf()
                    : GenericContext.Empty;

                var methodFactory = new TypeProjectionFactory();
                var methodProjection = methodFactory.Project(returnArg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _typeDatabase,
                    IsParameter = false,
                    GenericContext = genericContext
                });
                if (methodProjection != null)
                {
                    returnType = methodProjection.PublicType;
                }
                else if (returnArg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    returnType = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
                }
                else if (returnArg.SwiftTypeSpec is NamedTypeSpec retBoundGeneric && retBoundGeneric.ContainsGenericParameters)
                {
                    returnType = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg.SwiftTypeSpec, genericContext);
                }
                else
                {
                    returnType = _typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
                }
            }
        }

        // Handle async methods
        if (protoMethod.IsAsync)
        {
            if (returnType == "void")
                returnType = "Task";
            else
                returnType = $"Task<{returnType}>";
        }

        return returnType;
    }

    private string BuildInterfaceMethodSignature(MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        var returnTypeSpec = protoMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(protoMethod);
        var methodName = NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(protoMethod, protocolContext, _typeDatabase), protoMethod.IsAsync, hasReturnValue: hasReturnValue, isSelfReturning: isSelfReturning,
            parameterCount: protoMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
            isMutating: protoMethod.IsMutating);

        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(protoMethod);
        var parameterTypes = new List<string>();
        for (int i = 1; i < protoMethod.CSSignature.Count; i++)
        {
            var arg = protoMethod.CSSignature[i];
            // Skip debug params and empty tuple () params (zero-sized Void) — must match ProtocolHandler emission
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                arg.SwiftTypeSpec, _typeDatabase, visibleGenericNames);
            var projected = ResolveInterfaceMethodTypeName(typeSpecForKey, isParameter: true, protocolContext);
            parameterTypes.Add(ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(projected, arg.SwiftTypeSpec, _typeDatabase));
        }

        // Add CancellationToken to async method signatures (matches ProtocolHandler interface emission)
        if (protoMethod.IsAsync)
        {
            parameterTypes.Add("System.Threading.CancellationToken");
        }

        return $"{methodName}({string.Join(",", parameterTypes)})";
    }

    private string ResolveInterfaceMethodTypeName(TypeSpec swiftTypeSpec, bool isParameter, ProtocolDecl protocolContext)
    {
        return ProtocolSignatureHelper.ProjectTypeToCSharp(swiftTypeSpec, _typeDatabase, protocolContext, isParameter);
    }

    /// <summary>
    /// Gets interface subscript return type using SAME projection as ProtocolHandler.EmitInterfaceSubscript.
    /// </summary>
    private string GetInterfaceSubscriptReturnType(SubscriptDecl protoSubscript, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        // Factory-based projection with GenericContext
        // For Self-requirement protocols, map τ_0_0 → TSelf
        var genericContext = protocolContext.HasSelfRequirement
            ? GenericContext.ForProtocolSelf()
            : GenericContext.Empty;

        var subscriptFactory = new TypeProjectionFactory();
        var subscriptProjection = subscriptFactory.Project(protoSubscript.ReturnTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = genericContext
        });
        if (subscriptProjection != null)
            return subscriptProjection.PublicType;

        if (protoSubscript.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
            return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);

        // Bound generic fallback
        if (protoSubscript.ReturnTypeSpec is NamedTypeSpec subBoundGeneric && subBoundGeneric.ContainsGenericParameters)
        {
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(protoSubscript.ReturnTypeSpec, genericContext);
        }

        return _typeDatabase.GetTypeRecordOrAnyType(protoSubscript.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Yields the type itself, then walks the ResolvedSuperclass chain for class types.
    /// Stops at the first non-emittable ancestor (one with unsupported generic constraints),
    /// because flat emission means there is no valid C# inheritance chain beyond that point.
    /// For non-class types (structs, enums), yields only the type itself.
    /// </summary>
    internal static IEnumerable<TypeDecl> GetEmittableAncestors(TypeDecl type)
    {
        yield return type;

        if (type is not ClassDecl classDecl)
            yield break;

        var current = classDecl;
        while (current.HasResolvedSuperclass)
        {
            var ancestor = current.ResolvedSuperclass!;
            if (TypeSkipConditions.ClassAncestorWillBeSkipped(ancestor))
                yield break; // Stop — can't see past a non-emittable ancestor
            yield return ancestor;
            current = ancestor;
        }
    }

    /// <summary>
    /// Finds matching property in concrete type or its emittable ancestors by name.
    /// </summary>
    private static PropertyDecl? FindMatchingProperty(TypeDecl type, PropertyDecl protoProperty, bool isStatic = false)
    {
        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Properties.FirstOrDefault(p => p.Name == protoProperty.Name && p.IsStatic == isStatic);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Finds matching subscript in concrete type or its emittable ancestors by signature.
    /// </summary>
    private SubscriptDecl? FindMatchingSubscript(TypeDecl type, SubscriptDecl protoSubscript, ProtocolDecl protocolContext)
    {
        var protoKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(protoSubscript, _typeDatabase, protocolContext);

        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Subscripts.FirstOrDefault(s =>
                !s.IsStatic &&
                ProtocolSignatureHelper.GetSubscriptSignatureKey(s, _typeDatabase, null) == protoKey);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Finds matching method in concrete type or its emittable ancestors by signature.
    /// Falls back to position-aware Self matching for Self-requirement protocols where
    /// τ_0_0 in the protocol resolves to AnyType but the concrete type uses its actual name.
    /// </summary>
    private MethodDecl? FindMatchingMethod(TypeDecl type, MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        // Concrete-conformance matching stays async-agnostic (includeAsyncEffect: false) to preserve
        // the existing lenient witness matching — both sides of the comparison must opt out so a
        // requirement and its witness key alike regardless of the async effect.
        var protoKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolContext, includeAsyncEffect: false);

        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Methods.FirstOrDefault(m =>
                !m.IsConstructor && m.MethodType != MethodType.Static &&
                ProtocolSignatureHelper.GetMethodSignatureKey(m, _typeDatabase, null, includeAsyncEffect: false) == protoKey);
            if (match != null)
                return match;
        }

        // Fallback: position-aware matching with name normalization.
        // Handles two cases:
        // 1. Self-requirement protocols: τ_0_0 in protocol params resolves to AnyType, but the
        //    concrete type uses its actual type name. Self positions must equal conforming type's C# name.
        // 2. Non-Self protocols: method names differ (e.g., _interpolate vs interpolate) and
        //    name normalization via ToPascalCase resolves the mismatch.
        {
            var conformingRecord = _typeDatabase.GetTypeRecordOrAnyType(
                new NamedTypeSpec(type.SwiftTypeName?.ToString() ?? ""));
            var conformingFqn = conformingRecord.CSharpTypeName.FullyQualifiedName;

            // Only proceed if conforming type resolved (not AnyType)
            if (conformingFqn != "Swift.AnyType")
            {
                foreach (var ancestor in GetEmittableAncestors(type))
                {
                    var match = ancestor.Methods.FirstOrDefault(m =>
                        !m.IsConstructor && m.MethodType != MethodType.Static &&
                        MatchesWithSelfSubstitution(m, protoMethod, protocolContext, conformingFqn));
                    if (match != null)
                        return match;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds matching static method in concrete type or its emittable ancestors by signature.
    /// </summary>
    private MethodDecl? FindMatchingStaticMethod(TypeDecl type, MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        // Async-agnostic concrete matching, same rationale as FindMatchingMethod — both sides opt out.
        var protoKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolContext, includeAsyncEffect: false);

        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Methods.FirstOrDefault(m =>
                !m.IsConstructor && m.MethodType == MethodType.Static &&
                ProtocolSignatureHelper.GetMethodSignatureKey(m, _typeDatabase, null, includeAsyncEffect: false) == protoKey);
            if (match != null)
                return match;
        }

        return null;
    }

    /// <summary>
    /// Position-aware method matching for Self-requirement protocols.
    /// Self-typed params (τ_0_0) must equal conformingTypeFqn; non-Self must match exactly.
    /// </summary>
    private bool MatchesWithSelfSubstitution(
        MethodDecl concrete, MethodDecl proto, ProtocolDecl protocolContext, string conformingTypeFqn)
    {
        if (NameProvider.ToPascalCase(concrete.Name.TrimStart('_')) != NameProvider.ToPascalCase(proto.Name.TrimStart('_'))) return false;
        if (concrete.CSSignature.Count != proto.CSSignature.Count) return false;

        for (int i = 1; i < proto.CSSignature.Count; i++)
        {
            var protoArg = proto.CSSignature[i];
            var concreteArg = concrete.CSSignature[i];

            // τ_0_0 is the Self type parameter (depth 0, index 0).
            // Concrete param at this position MUST be the conforming type.
            if (protoArg.SwiftTypeSpec is NamedTypeSpec named && named.Name == "τ_0_0")
            {
                try
                {
                    var concreteType = _typeDatabase.GetTypeRecordOrAnyType(concreteArg.SwiftTypeSpec)
                        .CSharpTypeName.FullyQualifiedName;
                    if (concreteType != conformingTypeFqn)
                        return false;
                }
                catch { return false; }
                continue;
            }

            // Non-Self position: both must resolve to the same C# type
            try
            {
                var protoType = ResolveParamType(protoArg, protocolContext);
                var concreteType = ResolveParamType(concreteArg, null);
                if (protoType != concreteType)
                    return false;
            }
            catch { return false; }
        }
        return true;
    }

    private string ResolveParamType(ArgumentDecl arg, ProtocolDecl? protocolContext)
    {
        if (arg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
            return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
        return _typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Checks if interface and implementation types are compatible.
    /// </summary>
    private static bool AreTypesCompatible(string? interfaceType, string? implType)
    {
        if (interfaceType == null || implType == null) return false;
        // String comparison with normalization for now
        return NormalizeTypeName(interfaceType) == NormalizeTypeName(implType);
    }

    /// <summary>
    /// Checks if interface and implementation types are compatible, with TSelf awareness.
    /// TSelf anywhere in the interface type is substituted with the conforming type's C# name
    /// before comparison. This handles plain TSelf, TSelf?, Task&lt;TSelf&gt;,
    /// IReadOnlyList&lt;TSelf&gt;, Func&lt;TSelf, int&gt;, etc.
    /// </summary>
    private static bool AreTypesCompatible(string? interfaceType, string? implType, string? conformingTypeName)
    {
        if (interfaceType == null || implType == null) return false;
        var ni = NormalizeTypeName(interfaceType);
        var np = NormalizeTypeName(implType);
        // Substitute TSelf with the conforming type's projected name
        if (conformingTypeName != null && ni.Contains("TSelf"))
            ni = ni.Replace("TSelf", NormalizeTypeName(conformingTypeName));
        if (ni == np) return true;
        // Note: AnyType in the interface (from unresolved Self/generic param) is NOT
        // compatible with the concrete type's name. C# interface methods require exact
        // type match — a method signature with AnyType does NOT implement
        // the interface method with the concrete type. The conformance must be suppressed.
        return false;
    }

    private static string NormalizeTypeName(string typeName)
        => typeName.Replace(" ", "").Trim();

    /// <summary>
    /// Validates that the projected parameter types in the interface method match
    /// the concrete method's parameter types. This catches cases where the interface
    /// emits AnyType (from unresolved Self/τ_0_0) but the concrete type uses its actual type.
    /// </summary>
    private bool AreMethodParamsCompatible(
        MethodDecl protoMethod, MethodDecl concreteMethod,
        ProtocolDecl protocolDecl, string? conformingTypeName)
    {
        var genericContext = protocolDecl.HasSelfRequirement
            ? GenericContext.ForProtocolSelf()
            : GenericContext.Empty;

        // Skip return (index 0), compare parameter types
        for (int i = 1; i < protoMethod.CSSignature.Count && i < concreteMethod.CSSignature.Count; i++)
        {
            var protoArg = protoMethod.CSSignature[i];
            var concreteArg = concreteMethod.CSSignature[i];

            string interfaceParamType;
            string concreteParamType;

            try
            {
                var factory = new TypeProjectionFactory();
                var protoProjection = factory.Project(protoArg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _typeDatabase,
                    IsParameter = true,
                    GenericContext = genericContext
                });
                interfaceParamType = protoProjection?.PublicType
                    ?? _typeDatabase.GetTypeRecordOrAnyType(protoArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;

                var concreteProjection = factory.Project(concreteArg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _typeDatabase,
                    IsParameter = true,
                    GenericContext = GenericContext.Empty
                });
                concreteParamType = concreteProjection?.PublicType
                    ?? _typeDatabase.GetTypeRecordOrAnyType(concreteArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
            }
            catch
            {
                continue; // Can't project — skip this param check
            }

            if (!AreTypesCompatible(interfaceParamType, concreteParamType, conformingTypeName))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the set of module-qualified protocol names that a concrete type conforms to.
    /// </summary>
    private static HashSet<string> GetQualifiedConformanceNames(TypeDecl type)
    {
        IEnumerable<TypeConformance> conformances = type switch
        {
            ClassDecl cd => cd.Conformances,
            StructDecl sd => sd.Conformances,
            EnumDecl ed => ed.Conformances,
            _ => Enumerable.Empty<TypeConformance>()
        };
        return new HashSet<string>(conformances.Select(c => c.Protocol.ModuleQualifiedName));
    }

    /// <summary>
    /// Checks if a protocol property would be skipped from the interface.
    /// Delegates to MemberGateEvaluator for unified gate logic.
    /// </summary>
    private bool IsPropertySkippedFromInterface(PropertyDecl property, BoundGenericsHandler boundGenericsHandler, ProtocolDecl protocolDecl)
    {
        var evaluator = new MemberGateEvaluator(_typeDatabase);
        var result = evaluator.EvaluateProperty(property, _moduleDecl, protocolDecl);
        // InterfaceOnly (closure properties) → NOT skipped from interface (they ARE in the interface)
        return result.IsSkipped;
    }

    /// <summary>
    /// Mirrors the emitter handlers' async-closure bridge gate for a candidate witness. The
    /// gate is handler-layer by design (the closure shape is supported; only the containing
    /// member's wrapper flavor decides whether a Swift adapter exists for the
    /// <c>(context, startFunc)</c> pair), so the member-validation pipeline does not carry it
    /// and this validator has to ask the same question directly.
    /// <para>Asked through the BEFORE-EMISSION entry point, because that is when this validator
    /// runs. The plain overload reads <c>UsesCdeclMethodWrapper</c>, which is still false here for
    /// every method emission is going to promote — so it would answer "unbridgeable" for a witness
    /// that binds cleanly, and this validator would drop an entire conformance the type satisfies.
    /// The pre-emission entry point predicts the promotion so the two layers agree.</para>
    /// </summary>
    private bool HasUnbridgeableAsyncClosure(MethodDecl method) =>
        WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(
            new MethodEnvironment(method, _typeDatabase));

    /// <summary>
    /// Checks if a protocol method would be skipped from the interface.
    /// Delegates to MemberGateEvaluator for unified gate logic.
    /// Does NOT skip closure methods (they are emitted in the interface with stubs).
    /// Does NOT skip existential methods (they are emitted in the interface with stubs).
    /// </summary>
    private bool IsMethodSkippedFromInterface(MethodDecl method, BoundGenericsHandler boundGenericsHandler, ProtocolDecl protocolDecl)
    {
        var evaluator = new MemberGateEvaluator(_typeDatabase);
        var result = evaluator.EvaluateMethod(method, _moduleDecl, protocolDecl);
        // InterfaceOnly (closure/existential methods) → NOT skipped from interface (they ARE in the interface)
        return result.IsSkipped;
    }

    /// <summary>
    /// Checks if a protocol subscript would be skipped from the interface.
    /// Delegates to MemberGateEvaluator for unified gate logic — mirrors the property/method helpers so a
    /// requirement the interface dropped (e.g. an @objc-existential nested position) is not re-validated as
    /// a live `: IFoo` member here, which would spuriously drop the whole conformance (CS0535).
    /// </summary>
    private bool IsSubscriptSkippedFromInterface(SubscriptDecl subscript, ProtocolDecl protocolDecl)
    {
        var evaluator = new MemberGateEvaluator(_typeDatabase);
        var result = evaluator.EvaluateSubscript(subscript, _moduleDecl, protocolDecl);
        return result.IsSkipped;
    }
}
