// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Disambiguates protocol-method C# names when two or more Swift requirements share a base name AND
/// project to the same C# parameter types but differ only by their argument LABELS — the delegate-callback
/// shape, e.g. <c>conversationManager(_:didActivate:)</c> / <c>conversationManager(_:didDeactivate:)</c>
/// or RoomPlan's <c>captureSession(_:didAdd:)</c> / <c>(_:didChange:)</c> / <c>(_:didUpdate:)</c>.
///
/// Without disambiguation those overloads collide once labels are erased (the projected/fillability axis
/// keys off <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/>, which drops labels), so all but
/// one are dropped as <c>DuplicateSignature</c> — a consumer can react to the event but cannot tell which
/// one fired. The reverse-dispatch vtable already gives such siblings DISTINCT slots (slot identity is the
/// label-inclusive <see cref="EveryProtocolEmitter.GetMethodKey"/>); only the interface / proxy / validator
/// axes collapsed them. This helper supplies a single deterministic per-protocol map from the label-inclusive
/// slot key to a label-derived base name (built ObjC-selector style by appending the capitalized argument
/// labels). Every emission and dedup site reads it through the <c>Effective*</c> helpers, so they all agree
/// on which methods emit and under what name.
///
/// SCOPE — only LABEL collisions are touched. A group is disambiguated only when its members yield DISTINCT
/// label-derived names; a pure type-erasure collision (same labels, different Swift types that project to the
/// same C# type, e.g. <c>add(any Expression)</c> / <c>add(any Sendable)</c>) yields identical label-derived
/// names and is left to collapse exactly as before — byte-identical output. Grouping is on the PROJECTED key,
/// so type-erasure overloads whose projected keys already differ never group here in the first place.
///
/// FORWARD (SBW witness) dispatch keys its slot index off the label-blind <see cref="WitnessDispatchEmitter.GetMethodKey"/>,
/// which collapses a label-only pair to one slot. That is correct ONLY while the pair also collapses to one
/// interface member; once both siblings survive as distinct C# members, a Swift-backed proxy must be able to
/// forward each one to its OWN Swift witness, so the three forward walks (Swift producer, C# P/Invoke decls, C#
/// call sites) take <see cref="EffectiveWitnessSlotKey"/> instead: the label-INCLUSIVE slot key for a
/// disambiguated method, the unchanged label-blind key otherwise. Because all three walks consult the same key,
/// the split (and the resulting one-position index shift of any trailing method) stays in lockstep — no SBW
/// index mismatch. The fallback is the label-blind RAW key, NOT the projected key, so a type-erasure pair whose
/// distinct Swift types already earn two forward slots keeps both, exactly as before.
/// </summary>
internal static class ProtocolMethodDisambiguator
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Memoize per (ProtocolDecl instance). The map is a pure function of the protocol's method set and the
    // type database (one db per emission run), so every site that recomputes resolves the SAME instance —
    // this is what lets the emitter and the separately-invoked ProtocolConformanceValidator agree without
    // threading state between them. Keyed by reference identity (ProtocolDecl is a record; value equality
    // would alias distinct protocols with equal contents).
    private static readonly ConditionalWeakTable<ProtocolDecl, IReadOnlyDictionary<string, string>> _cache = new();

    /// <summary>
    /// Returns a map from <see cref="EveryProtocolEmitter.GetMethodKey"/> (the label-inclusive slot key) to
    /// the disambiguated label-derived base name, for the instance methods that participate in a label-only
    /// collision group. Empty for protocols with no such collision (the common case) — every <c>Effective*</c>
    /// helper then falls through to the pre-existing key/name, so output is byte-identical.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compute(ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase)
        => protocolDecl == null
            ? EmptyMap
            : _cache.GetValue(protocolDecl, _ => ComputeCore(protocolDecl, typeDatabase));

    /// <summary>
    /// The base name input every protocol name-compute site should feed to <c>GetPublicMethodName</c>: the
    /// disambiguated label-derived name when this method is in a collision group, else the method's own name.
    /// </summary>
    public static string EffectiveNameInput(MethodDecl method, ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase)
        => Compute(protocolDecl, typeDatabase).TryGetValue(EveryProtocolEmitter.GetMethodKey(method), out var name)
            ? name
            : method.Name;

    /// <summary>
    /// The raw (label-erased) dedup key every fillability / skip-set site should use. For a disambiguated
    /// method this is the label-INCLUSIVE slot key (so siblings stay distinct and BOTH fill their slots);
    /// for every other method it is the unchanged <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/>.
    /// </summary>
    public static string EffectiveRawKey(MethodDecl method, ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase)
    {
        var slotKey = EveryProtocolEmitter.GetMethodKey(method);
        return Compute(protocolDecl, typeDatabase).ContainsKey(slotKey)
            ? slotKey
            : ProtocolSignatureHelper.GetMethodSignatureKey(method, typeDatabase, protocolDecl);
    }

    /// <summary>
    /// The projected C# overload key every member-dedup site should use. For a disambiguated method this is
    /// the projected key computed under the label-derived name override (so siblings produce DISTINCT projected
    /// keys and both emit); for every other method it is the unchanged
    /// <see cref="ProtocolSignatureHelper.GetProjectedCSharpMethodKey"/>.
    /// </summary>
    public static string EffectiveProjectedKey(MethodDecl method, ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? propertyNames)
    {
        if (Compute(protocolDecl, typeDatabase).TryGetValue(EveryProtocolEmitter.GetMethodKey(method), out var nameOverride))
            return BuildProjectedKeyWithOverride(method, typeDatabase, protocolDecl, propertyNames, nameOverride);
        return ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, typeDatabase, protocolDecl, propertyNames);
    }

    /// <summary>
    /// The forward witness-dispatch (SBW) slot key the three forward walks should use for index allocation. For a
    /// disambiguated method this is the label-INCLUSIVE <see cref="EveryProtocolEmitter.GetMethodKey"/> (so siblings
    /// get DISTINCT forward slots and a Swift-backed proxy can forward each to its own witness); for every other
    /// method it is the unchanged label-blind <see cref="WitnessDispatchEmitter.GetMethodKey"/>. The non-disambiguated
    /// fallback is deliberately the label-blind RAW key — NOT the projected <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/>
    /// — so a type-erasure overload pair whose distinct Swift types already earn two forward slots keeps both, with
    /// the producer/consumer index allocation byte-identical to today.
    /// </summary>
    public static string EffectiveWitnessSlotKey(MethodDecl method, ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase)
    {
        var slotKey = EveryProtocolEmitter.GetMethodKey(method);
        return Compute(protocolDecl, typeDatabase).ContainsKey(slotKey)
            ? slotKey
            : WitnessDispatchEmitter.GetMethodKey(method);
    }

    private static string BuildProjectedKeyWithOverride(MethodDecl method, ITypeDatabase typeDatabase, ProtocolDecl? protocolDecl, IReadOnlySet<string>? propertyNames, string nameOverride)
        => ProtocolSignatureHelper.BuildProjectedMethodKey(method, typeDatabase, new ProtocolSignatureHelper.ProjectedKeyOptions
        {
            PropertyNames = propertyNames,
            UseProtocolProjection = true,
            ProtocolContext = protocolDecl,
            IncludeParentTypeName = false,
            TreatAsClosureTombstone = false,
            Logger = null,
            NameOverride = nameOverride,
        });

    private static IReadOnlyDictionary<string, string> ComputeCore(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase)
    {
        // Eligible candidates mirror the ProtocolHandler method loop's own filters: instance methods only
        // (constructors and statics take separate emission paths and are out of slice — see the class remark).
        var candidates = protocolDecl.Methods
            .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static && !m.IsAccessor && !m.IsSubscriptAccessor)
            .ToList();
        if (candidates.Count < 2)
            return EmptyMap;

        // Group by the label-ERASED projected C# key — the overload identity the interface dedups on. Property
        // names are irrelevant to grouping (a Foo→FooMethod rename shifts every member uniformly, so it never
        // changes whether two members collide), so null is used for determinism. Type-erasure overloads whose
        // projected keys already differ (add(IExpression) vs add(object)) never share a group.
        var groups = new Dictionary<string, List<MethodDecl>>(StringComparer.Ordinal);
        foreach (var m in candidates)
        {
            var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(m, typeDatabase, protocolDecl, propertyNames: null);
            if (!groups.TryGetValue(projectedKey, out var list))
            {
                list = new List<MethodDecl>();
                groups[projectedKey] = list;
            }
            list.Add(m);
        }

        // A group needs disambiguation only when (a) it holds ≥2 DISTINCT slot keys (genuinely different
        // requirements, not a true duplicate) AND (b) those slot keys yield DISTINCT label-derived names
        // (i.e. the difference is in the LABELS). A type-only difference produces identical label-derived
        // names; we skip it so today's collapse is preserved byte-for-byte.
        var disambiguatedSlotKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(string slotKey, string nameInput)>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2)
                continue;

            // One representative (first in declaration order) per distinct slot key.
            var reps = new List<MethodDecl>();
            var seenSlotKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in group)
            {
                if (seenSlotKeys.Add(EveryProtocolEmitter.GetMethodKey(m)))
                    reps.Add(m);
            }
            if (reps.Count < 2)
                continue; // all members are true duplicates → collapse as before

            var named = reps.Select(m => (slotKey: EveryProtocolEmitter.GetMethodKey(m), name: BuildLabelDerivedNameInput(m))).ToList();
            var distinctNames = named.Select(x => x.name).Distinct(StringComparer.Ordinal).Count();
            if (distinctNames < named.Count)
                continue; // labels don't distinguish all siblings (pure type-only difference) → leave collapsed

            foreach (var (slotKey, nameInput) in named)
            {
                disambiguatedSlotKeys.Add(slotKey);
                pending.Add((slotKey, nameInput));
            }
        }

        if (pending.Count == 0)
            return EmptyMap;

        // Reserve the natural projected keys of every candidate that is NOT being disambiguated, so a derived
        // name can never silently collide with a sibling that emits under its natural name.
        //
        // This reservation is deliberately property-AGNOSTIC (propertyNames: null), matching the projected-key
        // axis everywhere else: the chosen disambiguated name is a pure function of the protocol's method set,
        // so EVERY walk (interface, proxy, receiver, cctor, validator) reads the SAME name for a given slot key
        // and the split stays in lockstep. Threading a property-name set in here would make the map content vary
        // by caller — sites that pass null would pick a different name than sites that pass the real set — which
        // is precisely the cross-walk inconsistency (CS0535 / dangling witness symbol) this whole map prevents.
        // The one residual it cannot see: a property whose emitted C# name equals a disambiguated name renames
        // the method Foo→FooMethod uniformly at every site, which can re-collide with a natural FooMethod sibling.
        // That is caught downstream by the orthogonal emitted-signature dedup (which IS property-aware), dropping
        // one member as a DuplicateSignature exactly like any other collapse — a graceful, consistent degradation,
        // not a compile error. Keep this null; do not make the map property-dependent.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in candidates)
        {
            if (disambiguatedSlotKeys.Contains(EveryProtocolEmitter.GetMethodKey(m)))
                continue;
            reserved.Add(ProtocolSignatureHelper.GetProjectedCSharpMethodKey(m, typeDatabase, protocolDecl, propertyNames: null));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slotKey, baseName) in pending)
        {
            if (result.ContainsKey(slotKey))
                continue;
            // Representative method for this slot key (any member with that key — they share projected shape).
            var rep = candidates.First(m => EveryProtocolEmitter.GetMethodKey(m) == slotKey);
            var nameInput = baseName;
            int suffix = 2;
            while (true)
            {
                var projected = BuildProjectedKeyWithOverride(rep, typeDatabase, protocolDecl, propertyNames: null, nameInput);
                if (reserved.Add(projected))
                    break;
                nameInput = baseName + suffix;
                suffix++;
            }
            result[slotKey] = nameInput;
        }
        return result;
    }

    /// <summary>
    /// Builds the ObjC-selector-style base name: the method's bare name followed by the capitalized external
    /// label of each non-empty, non-underscore, non-auto-generated argument. For
    /// <c>conversationManager(_:didActivate:)</c> this yields <c>conversationManagerDidActivate</c>; the
    /// downstream <c>GetPublicMethodName</c> pass PascalCases the first character to
    /// <c>ConversationManagerDidActivate</c>.
    ///
    /// Argument labels are Swift identifiers and flow into the C# name unsanitized — the same assumption the
    /// generator already makes for <c>method.Name</c> and every other Swift-identifier-derived C# name. A label
    /// that is not a legal C# identifier (e.g. an emoji) would emit uncompilable C#, but that fails closed at the
    /// compile gate and is the identical exposure as a method name in the same shape; it is not sanitized here in
    /// isolation (that would be a codebase-wide identifier-sanitization concern, not a label-path patch).
    /// </summary>
    private static string BuildLabelDerivedNameInput(MethodDecl method)
    {
        var sb = new StringBuilder(method.Name);
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var label = arg.GetSwiftName();
            if (string.IsNullOrEmpty(label) || label == "_" || SwiftBuilder.IsAutoGeneratedArgName(label))
                continue;
            sb.Append(char.ToUpperInvariant(label[0]));
            if (label.Length > 1)
                sb.Append(label.Substring(1));
        }
        return sb.ToString();
    }
}
