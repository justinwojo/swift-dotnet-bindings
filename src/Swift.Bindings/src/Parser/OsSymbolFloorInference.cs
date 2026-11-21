// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// One floor <see cref="OsSymbolFloorInference"/> inferred: the declaration, the symbol the generating
/// SDK exports that the inventory's OS does not, and the floor recorded on the declaration.
/// <see cref="Symbols"/> lists every exported symbol of the declaration (and of its accessors) the
/// inventory's OS lacks, the set a wrapper or a P/Invoke may import on its behalf.
/// </summary>
public sealed record InferredOsFloor(
    string Kind, string Declaration, string Symbol, string Platform, string Floor, string InventoryOsVersion)
{
    public IReadOnlyList<string> Symbols { get; init; } = new[] { Symbol };

    /// <summary>
    /// False when the declaration already had a floor above the inventory's OS (its own or an enclosing
    /// type's, inferred or declared), so nothing was raised; the entry only records that its absent
    /// symbols are guarded.
    /// </summary>
    public bool Raised { get; init; } = true;
}

/// <summary>
/// Floors every member of an OS framework that the generating SDK declares without a floor but an older
/// supported OS does not ship (load-safety property P2).
///
/// <para>An SDK can add a member without <c>@available</c> (Xcode 27.2's <c>ChaChaPoly.Nonce.count</c>,
/// absent from iOS 26). Swift then links it strongly, so the wrapper refuses to load on the older OS;
/// load-safety P1 weak-links the OS frameworks so the wrapper loads, and the symbol resolves to null.
/// This pass makes the binding refuse the call first: a declaration whose symbol the generating SDK
/// exports but the inventory's OS lacks, and whose declared floor is at or below that OS, is floored at
/// the generating SDK's full version (or a supplied provider's floor). Nothing earlier is known to ship
/// it, so the floor is never rounded down.</para>
///
/// <para>The floor is merged into the declaration's own availability, so every consumer of member
/// availability sees it: the <c>[SupportedOSPlatform]</c> a consumer reads, the managed guard that
/// throws <see cref="System.PlatformNotSupportedException"/> before any native call, the accessors'
/// own lists, a type's metadata accessor and its module-initializer registration. A type floored this
/// way floors its members through their ancestors. A conformance the inventory's OS lacks on a type it
/// does ship is recorded in <see cref="ModuleDecl.InferredConformanceFloors"/> so the module initializer
/// does not resolve its witness table there. The entries are flagged
/// <see cref="AvailabilityAnnotation.IsRuntimeSupportFloor"/> as well: Swift knows nothing of them, so a
/// protocol witness guards its body instead of declaring them.</para>
///
/// <para>Symbols are read in the form <see cref="ModuleDecl.ExportedSymbols"/> holds them (no leading
/// underscore). A declaration with no exported symbol (an always-emit-into-client member) is left alone:
/// its absence from the inventory says nothing about the OS.</para>
/// </summary>
public static class OsSymbolFloorInference
{
    internal static IReadOnlyList<InferredOsFloor> Apply(ModuleDecl module, OsSymbolFloorContext context)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(context);
        var inferred = new List<InferredOsFloor>();
        if (module.ExportedSymbols is not { Count: > 0 } exported)
            return inferred;

        var pass = new Pass(module, context, exported, inferred);
        foreach (var type in module.Types)
            pass.Type(type);
        foreach (var protocol in module.Protocols)
            pass.Type(protocol);
        foreach (var method in module.Methods)
            pass.Method(method);
        foreach (var property in module.Properties)
            pass.Property(property);
        return inferred;
    }

    private sealed class Pass(
        ModuleDecl module, OsSymbolFloorContext context, HashSet<string> exported, List<InferredOsFloor> inferred)
    {
        private readonly HashSet<BaseDecl> _visited = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The candidate symbols the generating SDK exports for a declaration, or empty when it exports
        /// none of them. The declaration is absent from the inventory's OS when none of these ship there.
        /// </summary>
        private List<string> Exported(IEnumerable<string?> candidates) =>
            candidates.Where(c => !string.IsNullOrEmpty(c) && exported.Contains(c!)).Select(c => c!).Distinct(StringComparer.Ordinal).ToList();

        private bool AbsentFromInventoryOs(List<string> exportedCandidates) =>
            exportedCandidates.Count > 0 && !exportedCandidates.Any(context.ShipsOnInventoryOs);

        internal void Type(TypeDecl type)
        {
            if (!_visited.Add(type))
                return;

            var candidates = Exported(TypeSymbols(type));
            if (AbsentFromInventoryOs(candidates))
            {
                var raised = WithFloor(type.AvailabilityAnnotations, type.ParentDecl, out var guardedAt);
                if (raised is not null)
                    type.AvailabilityAnnotations = raised;
                Record("type", type.SwiftTypeName?.ToString() ?? type.Name, candidates, raised is not null, guardedAt);
            }

            foreach (var conformance in Conformances(type))
            {
                var symbol = Exported(new[] { conformance.ProtocolConformanceDescriptor });
                if (!AbsentFromInventoryOs(symbol))
                    continue;
                var raised = WithFloor(null, type, out var guardedAt);
                if (raised is not null)
                    module.InferredConformanceFloors[symbol[0]] = raised;
                Record("conformance", $"{type.SwiftTypeName?.ToString() ?? type.Name}: {conformance.Protocol}", symbol, raised is not null, guardedAt);
            }

            foreach (var method in type.Methods)
                Method(method);
            foreach (var property in type.Properties)
                Property(property);
            foreach (var subscript in type.Subscripts)
                Subscript(subscript);
            foreach (var op in type.Operators)
                Operator(op);
            if (type is EnumDecl enumDecl)
            {
                foreach (var enumCase in enumDecl.Cases)
                    Case(enumCase, enumDecl);
            }
            foreach (var nested in type.Types)
                Type(nested);
        }

        /// <summary>An enum case its enum shipped without: its constructor (or resilient case tag) is absent.</summary>
        private void Case(EnumCaseDecl enumCase, EnumDecl enumDecl)
        {
            if (!_visited.Add(enumCase) || string.IsNullOrEmpty(enumCase.MangledName))
                return;
            var candidates = Exported(new[] { enumCase.MangledName, enumCase.MangledName + "WC" });
            if (!AbsentFromInventoryOs(candidates))
                return;
            var raised = WithFloor(enumCase.AvailabilityAnnotations, enumDecl, out var guardedAt);
            if (raised is not null)
                enumCase.AvailabilityAnnotations = raised;
            Record("enum case", Describe(enumDecl, enumCase.Name), candidates, raised is not null, guardedAt);
        }

        internal void Operator(OperatorDecl op)
        {
            if (!_visited.Add(op))
                return;
            var candidates = Exported(MethodSymbols(op.UnderlyingMethod.MangledName));
            if (!AbsentFromInventoryOs(candidates))
                return;
            // The operator handler guards on the operator's own list; its underlying method is raised too
            // for the paths that bind through the method.
            var raised = WithFloor(op.AvailabilityAnnotations, op.ParentDecl, out var guardedAt);
            if (raised is not null)
            {
                op.AvailabilityAnnotations = raised;
                if (WithFloor(op.UnderlyingMethod.AvailabilityAnnotations, op.ParentDecl, out _) is { } raisedMethod)
                    op.UnderlyingMethod.AvailabilityAnnotations = raisedMethod;
            }
            Record("operator", Describe(op.ParentDecl, op.OperatorSymbol), candidates, raised is not null, guardedAt);
        }

        internal void Method(MethodDecl method)
        {
            if (!_visited.Add(method))
                return;
            var candidates = Exported(MethodSymbols(method.MangledName));
            if (!AbsentFromInventoryOs(candidates))
                return;
            var raised = WithFloor(method.AvailabilityAnnotations, method.ParentDecl, out var guardedAt);
            if (raised is not null)
                method.AvailabilityAnnotations = raised;
            Record(method.IsConstructor ? "initializer" : "method", Describe(method.ParentDecl, method.Name), candidates, raised is not null, guardedAt);
        }

        internal void Property(PropertyDecl property)
        {
            if (!_visited.Add(property))
                return;

            var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            var getterCandidates = getter is null ? new List<string>() : Exported(MethodSymbols(getter.Method.MangledName));
            if (AbsentFromInventoryOs(getterCandidates))
            {
                var raised = WithFloor(property.AvailabilityAnnotations, property.ParentDecl, out var guardedAt);
                if (raised is not null)
                    property.AvailabilityAnnotations = raised;
                // A setter introduced after its property carries its own list, which replaces the
                // property's rather than adding to it, so it needs the floor too.
                if (property.SetterAvailabilityAnnotations is { Count: > 0 } setterOwn
                    && WithFloor(setterOwn, property.ParentDecl, out _) is { } raisedSetterList)
                    property.SetterAvailabilityAnnotations = raisedSetterList;
                foreach (var accessor in property.Accessors)
                    RaiseAccessor(accessor, property);
                Record("property", Describe(property.ParentDecl, property.Name),
                    getterCandidates.Concat(AbsentAccessorSymbols(property.Accessors)).Distinct(StringComparer.Ordinal).ToList(),
                    raised is not null, guardedAt);
                return;
            }

            // The property ships but a setter added later does not.
            var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
            if (setter is null)
                return;
            var setterCandidates = Exported(MethodSymbols(setter.Method.MangledName));
            if (!AbsentFromInventoryOs(setterCandidates))
                return;
            var setterBase = property.SetterAvailabilityAnnotations is { Count: > 0 } own
                ? own
                : (IReadOnlyList<AvailabilityAnnotation>?)property.AvailabilityAnnotations;
            var raisedSetter = WithFloor(setterBase, property.ParentDecl, out var setterGuardedAt);
            if (raisedSetter is not null)
            {
                property.SetterAvailabilityAnnotations = raisedSetter;
                RaiseAccessor(setter, property);
            }
            Record("property setter", Describe(property.ParentDecl, property.Name),
                setterCandidates.Concat(AbsentAccessorSymbols(property.Accessors.Where(a => a is not GetAccessorDecl)))
                    .Distinct(StringComparer.Ordinal).ToList(),
                raisedSetter is not null, setterGuardedAt);
        }

        internal void Subscript(SubscriptDecl subscript)
        {
            if (!_visited.Add(subscript))
                return;
            var getter = subscript.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            var candidates = Exported(getter is null
                ? MethodSymbols(subscript.MangledName)
                : MethodSymbols(getter.Method.MangledName).Concat(MethodSymbols(subscript.MangledName)));
            if (!AbsentFromInventoryOs(candidates))
            {
                SubscriptSetter(subscript);
                return;
            }
            var raised = WithFloor(subscript.AvailabilityAnnotations, subscript.ParentDecl, out var guardedAt);
            if (raised is not null)
            {
                subscript.AvailabilityAnnotations = raised;
                foreach (var accessor in subscript.Accessors)
                    RaiseAccessor(accessor, subscript);
            }
            Record("subscript", Describe(subscript.ParentDecl, "subscript"),
                candidates.Concat(AbsentAccessorSymbols(subscript.Accessors)).Distinct(StringComparer.Ordinal).ToList(),
                raised is not null, guardedAt);
        }

        /// <summary>The subscript ships but a setter added later does not: the setter accessor is floored.</summary>
        private void SubscriptSetter(SubscriptDecl subscript)
        {
            var setter = subscript.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
            if (setter is null)
                return;
            var setterCandidates = Exported(MethodSymbols(setter.Method.MangledName));
            if (!AbsentFromInventoryOs(setterCandidates))
                return;
            var setterBase = setter.Method.AvailabilityAnnotations is { Count: > 0 } own
                ? own
                : (IReadOnlyList<AvailabilityAnnotation>?)subscript.AvailabilityAnnotations;
            var raised = WithFloor(setterBase, subscript.ParentDecl, out var guardedAt);
            if (raised is not null)
                setter.Method.AvailabilityAnnotations = raised;
            Record("subscript setter", Describe(subscript.ParentDecl, "subscript"), setterCandidates, raised is not null, guardedAt);
        }

        private void RaiseAccessor(AccessorDecl accessor, BaseDecl owner)
        {
            var raised = WithFloor(accessor.Method.AvailabilityAnnotations, owner.ParentDecl, out _);
            if (raised is not null)
                accessor.Method.AvailabilityAnnotations = raised;
        }

        /// <summary>The exported symbols of <paramref name="accessors"/> the inventory's OS lacks.</summary>
        private IEnumerable<string> AbsentAccessorSymbols(IEnumerable<AccessorDecl> accessors) =>
            accessors.SelectMany(a => Exported(MethodSymbols(a.Method.MangledName)))
                .Where(s => !context.ShipsOnInventoryOs(s));

        /// <summary>
        /// Records a declaration whose exported symbols the inventory's OS lacks: a raised floor, or
        /// (<paramref name="raised"/> false) one already above that OS, at <paramref name="guardedAt"/>.
        /// An unavailable declaration (<paramref name="guardedAt"/> null) is not bound, so not recorded.
        /// </summary>
        private void Record(string kind, string declaration, List<string> symbols, bool raised, string? guardedAt)
        {
            if (guardedAt is null)
                return;
            inferred.Add(new InferredOsFloor(kind, declaration, symbols[0], context.AvailabilityPlatform,
                raised ? context.Floor : guardedAt, context.InventoryOsVersion)
            {
                Symbols = symbols,
                Raised = raised,
            });
        }

        /// <summary>
        /// <paramref name="own"/> plus the inferred floor, or null when the declaration's effective floor
        /// on the platform (its own merged with its enclosing types') already sits above the inventory's
        /// OS or the declaration is unavailable there. <paramref name="guardedAt"/> is the floor the
        /// declaration ends up with: the inferred one, the effective one already above the inventory's
        /// OS, or null when the declaration is unavailable on the platform.
        /// </summary>
        private List<AvailabilityAnnotation>? WithFloor(
            IReadOnlyList<AvailabilityAnnotation>? own, BaseDecl? parent, out string? guardedAt)
        {
            guardedAt = null;
            var effective = AvailabilityHelpers.MergeAvailabilityFromAncestors(own, parent);
            string? declared = null;
            bool catalystOwnFloor = false;
            if (effective is not null)
            {
                foreach (var annotation in effective)
                {
                    bool platformMatch = string.Equals(annotation.Platform, context.AvailabilityPlatform, StringComparison.Ordinal);
                    // Mac Catalyst inherits a declaration's iOS floor unless it names its own.
                    bool iosForCatalyst = context.AvailabilityPlatform == "macCatalyst"
                        && string.Equals(annotation.Platform, "iOS", StringComparison.Ordinal);
                    if (!platformMatch && !iosForCatalyst)
                        continue;
                    if (platformMatch && annotation.IsUnconditionallyUnavailable)
                        return null;
                    if (annotation.IntroducedVersion is not { } introduced)
                        continue;
                    if (platformMatch && context.AvailabilityPlatform == "macCatalyst" && !catalystOwnFloor)
                    {
                        catalystOwnFloor = true;
                        declared = introduced;
                        continue;
                    }
                    if (iosForCatalyst && catalystOwnFloor)
                        continue;
                    if (declared is null || AvailabilityHelpers.CompareOsVersions(introduced, declared) > 0)
                        declared = introduced;
                }
            }

            if (declared is not null && AvailabilityHelpers.CompareOsVersions(declared, context.InventoryOsVersion) > 0)
            {
                guardedAt = declared;
                return null;
            }
            guardedAt = context.Floor;

            var result = own is null ? new List<AvailabilityAnnotation>() : new List<AvailabilityAnnotation>(own);
            result.Add(new AvailabilityAnnotation(context.AvailabilityPlatform, context.Floor, null, null, false, false, null, null)
            {
                IsRuntimeSupportFloor = true,
                IsInferredOsFloor = true,
            });
            return result;
        }

        private static IEnumerable<TypeConformance> Conformances(TypeDecl type) => type switch
        {
            StructDecl s => s.Conformances,
            ClassDecl c => c.Conformances,
            EnumDecl e => e.Conformances,
            _ => Array.Empty<TypeConformance>(),
        };

        private static IEnumerable<string?> TypeSymbols(TypeDecl type)
        {
            if (string.IsNullOrEmpty(type.MangledName))
                yield break;
            if (type is ProtocolDecl)
            {
                yield return type.MangledName.EndsWith('P') ? type.MangledName[..^1] + "Mp" : type.MangledName + "Mp";
                yield break;
            }
            yield return type.MangledName + "Mn";
            yield return type.MangledName + "Ma";
        }

        /// <summary>
        /// A member's own symbol plus the dispatch thunk and requirement descriptor a class method or
        /// protocol requirement exports instead of (or beside) it.
        /// </summary>
        private static IEnumerable<string?> MethodSymbols(string? mangledName)
        {
            if (string.IsNullOrEmpty(mangledName))
                yield break;
            yield return mangledName;
            yield return mangledName + "Tj";
            yield return mangledName + "Tq";
        }

        private static string Describe(BaseDecl? parent, string name) =>
            parent is TypeDecl type ? $"{type.SwiftTypeName?.ToString() ?? type.Name}.{name}" : name;
    }
}
