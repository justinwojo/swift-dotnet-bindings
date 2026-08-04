// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace BindingsGeneration;

/// <summary>
/// Disambiguates protocol-method C# names when two or more Swift requirements share a base name AND project
/// to the same C# parameter types — whether they differ by their argument LABELS (the delegate-callback
/// shape, e.g. <c>conversationManager(_:didActivate:)</c> / <c>(_:didDeactivate:)</c>, or RoomPlan's
/// <c>captureSession(_:didAdd:)</c> / <c>(_:didChange:)</c> / <c>(_:didUpdate:)</c>) or only by their Swift
/// parameter TYPES, which the C# projection erases (<c>add(any Expression)</c> / <c>add(any Sendable)</c>).
///
/// Without disambiguation those overloads collide once labels are erased (the projected/fillability axis
/// keys off <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/>, which drops labels), so all but
/// one are dropped as <c>DuplicateSignature</c> — a consumer can react to the event but cannot tell which
/// one fired. The reverse-dispatch vtable already gives such siblings DISTINCT slots (slot identity is the
/// label-inclusive <see cref="EveryProtocolEmitter.GetMethodKey"/>); only the interface / proxy / validator
/// axes collapsed them. This helper supplies a single deterministic per-protocol map from the label-inclusive
/// slot key to a derived base name — ObjC-selector style from the capitalized argument labels, or from the
/// Swift parameter types when the labels do not separate the family. Every emission and dedup site reads it
/// through the <c>Effective*</c> helpers, so they all agree on which methods emit and under what name.
///
/// SCOPE — the same two-rung ladder the class lane walks, in the same per-family order. A group is
/// disambiguated on the LABEL rung when its members yield distinct label-derived names; a pure type-erasure
/// collision (same labels, different Swift types that project to the same C# type, e.g.
/// <c>add(any Expression)</c> / <c>add(any Sendable)</c>) yields identical label-derived names and drops to
/// the TYPE rung, exactly as a conforming class body would — the lanes have to agree here or the conformance
/// is dropped as unsatisfiable. When neither rung separates the family it is left to collapse through the
/// ordinary duplicate-signature dedup. Grouping is on the PROJECTED key, so type-erasure overloads whose
/// projected keys already differ never group here in the first place.
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
        //
        // Family order is tracked explicitly rather than taken from the dictionary's enumeration,
        // the same way the class/free-function lane does it. Both lanes accumulate a scope-wide
        // `reserved` set ACROSS families as they walk, so which family is visited first decides
        // which one gets to claim a contested name — enumeration order is load-bearing, and
        // Dictionary makes no promise about it beyond what today's insertion pattern happens to
        // produce. Insertion order is the order the emission walk visits the requirements, which is
        // the only order with a meaning behind it.
        var groups = new Dictionary<string, List<MethodDecl>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var m in candidates)
        {
            var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(m, typeDatabase, protocolDecl, propertyNames: null);
            if (!groups.TryGetValue(projectedKey, out var list))
            {
                groups[projectedKey] = list = new List<MethodDecl>();
                groupOrder.Add(projectedKey);
            }
            list.Add(m);
        }

        // A group needs disambiguation only when it holds ≥2 DISTINCT slot keys — genuinely different
        // requirements rather than a true duplicate. Which rung names them is decided per family below:
        // the labels when they separate every sibling, otherwise the Swift parameter types.
        var disambiguatedSlotKeys = new HashSet<string>(StringComparer.Ordinal);
        // The rung travels WITH each entry rather than being re-inferred later from string equality: once a
        // family drops to the type rung its type-derived input IS the base name the assignment loop tries
        // first, so comparing the accepted name against it would book every type-rung assignment as
        // LabelDerived and quietly lie to the ship-gate ledger.
        var pending = new List<(string slotKey, string nameInput, bool fromTypeRung)>();
        foreach (var groupKey in groupOrder)
        {
            var group = groups[groupKey];
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

            var named = reps
                .Select(m => (slotKey: EveryProtocolEmitter.GetMethodKey(m), name: OverloadNameDisambiguator.BuildLabelDerivedNameInput(m), bare: m.Name, decl: m))
                .ToList();
            // BARE-NAME OWNERSHIP — the same content rule the class lane applies, in the same ORDER. A
            // requirement whose labels add nothing to its name has nothing to be discriminated BY, so it is
            // the one that keeps the family's natural C# name; leaving it out of the map is exactly what
            // "keeps its own name" means here, since every Effective* helper then falls through to its
            // natural key. The rule fires only for a SOLE claimant, so the outcome is a fact about that
            // member rather than a pick among equals.
            //
            // Ownership is settled on the LABEL inputs and BEFORE a rung is chosen, because that is the
            // order the class lane walks: it awards the owner, drops it from the discriminands, and only
            // then picks a rung for what is left. Deciding the rung first instead lets a THIRD sibling's
            // duplicate label drag the label-less member down to the type rung — the class lane would still
            // hand that member the bare name, so the interface would require `TransformWithRefBox` while the
            // conforming class declares `Transform`, and the whole conformance is dropped as unsatisfiable.
            // The owner is a fact about one member; a rung is a fact about the rest of the family.
            var claimants = named.Where(x => string.Equals(x.name, x.bare, StringComparison.Ordinal)).ToList();
            var bareNameOwner = claimants.Count == 1 ? claimants[0].slotKey : null;
            var discriminands = named
                .Where(x => !string.Equals(x.slotKey, bareNameOwner, StringComparison.Ordinal))
                .ToList();
            if (discriminands.Count == 0)
                continue;

            var usedTypeRung = false;
            if (discriminands.Select(x => x.name).Distinct(StringComparer.Ordinal).Count() < discriminands.Count)
            {
                // TYPE RUNG — the labels do not separate every sibling (a pure type-erasure family:
                // identical labels, different Swift types, one projected C# key). The class lane resolves
                // exactly this shape on its type rung, so this lane has to as well.
                //
                // Leaving the family collapsed here while the class lane splits it is the one lane
                // divergence the conformance validator cannot repair. A conforming Swift class names these
                // members from its own type rung (`TransformWithRefBox` / `TransformWithOptionalRefBox`)
                // while the interface would still require a bare `Transform` that nobody declares, so the
                // whole conformance is dropped as unsatisfiable — losing an entire `: IFoo` the binding
                // used to carry. The retired numeric scheme papered over this by accident: its first
                // member kept the bare name, which happened to match the collapsed interface member.
                //
                // Slot identity is untouched. These siblings differ in their RAW Swift parameter types, so
                // EveryProtocolEmitter.GetMethodKey already allocates each one its own vtable slot; the
                // collapse only ever dropped the C# interface MEMBER. Naming them apart fills a slot the
                // collapse used to leave empty — it does not move, add, or remove one.
                //
                // Rung selection is per-FAMILY, mirroring the class lane: every discriminand moves down
                // together, so which rung a member lands on is a fact about the family rather than about
                // which one the walk reached first. The base input is each member's own label-derived input,
                // the same composition the class lane feeds BuildTypeDerivedNameInput, so both lanes land on
                // the identical string by construction rather than by agreement of two hand-kept ladders.
                //
                // Two label-less siblings never leave an owner behind: their identical label inputs make the
                // claimant count 2, so no one owns the bare name and both land here — matching the class
                // lane, which likewise leaves a pure type-erasure family ownerless.
                discriminands = discriminands
                    .Select(x => (x.slotKey, name: OverloadNameDisambiguator.BuildTypeDerivedNameInput(x.decl, x.name), x.bare, x.decl))
                    .ToList();
                if (discriminands.Select(x => x.name).Distinct(StringComparer.Ordinal).Count() < discriminands.Count)
                    continue; // neither rung separates the family → collapse through the emitted-signature dedup
                usedTypeRung = true;
            }

            foreach (var (slotKey, nameInput, _, _) in discriminands)
            {
                disambiguatedSlotKeys.Add(slotKey);
                pending.Add((slotKey, nameInput, usedTypeRung));
            }
        }

        if (pending.Count == 0)
            return EmptyMap;

        // FAMILY FOLD — uniform label-derived naming across a mixed renamed/bare family.
        //
        // The projected-key pass above renames ONLY the members that collide on the label-erased C# overload
        // (the delegate-callback pair/triple). A sibling requirement that shares the SAME Swift base name but
        // projects to a DISTINCT C# overload — a different parameter type or arity, e.g. RoomPlan's
        // captureSession(_:didProvide: Instruction) alongside the renamed captureSession(_:didAdd:/didChange:/
        // didUpdate:) triple — never entered a collision group, so it stays a bare `CaptureSession(...)` overload
        // and reads inconsistently next to its renamed siblings. Once at least one member of a base-name family is
        // label-derived, fold the labels into the whole family so it reads uniformly. The trigger is deliberately
        // narrow (a MIXED family — at least one already-renamed member), so a protocol whose base-name overloads
        // are ALL type-distinct with none colliding is untouched (byte-identical output). A sibling with no
        // foldable label (its label-derived name equals its bare name — e.g. a single unlabeled argument) is left
        // bare: that is the honest name, and folding it would only re-alias it onto its own natural key.
        //
        // Slot identity is unaffected: the reverse-dispatch vtable slot keys off the label-inclusive
        // EveryProtocolEmitter.GetMethodKey independently of this map, and a type-distinct sibling is already
        // raw/projected/witness-distinct, so adding it to the map only re-labels the C# member NAME every
        // Effective* site reads — it does not move, add, or drop a slot.
        var disambiguatedBaseNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in candidates)
        {
            if (disambiguatedSlotKeys.Contains(EveryProtocolEmitter.GetMethodKey(m)))
                disambiguatedBaseNames.Add(m.Name);
        }
        foreach (var m in candidates)
        {
            var slotKey = EveryProtocolEmitter.GetMethodKey(m);
            if (disambiguatedSlotKeys.Contains(slotKey))
                continue; // already label-derived by the projected-key pass
            if (!disambiguatedBaseNames.Contains(m.Name))
                continue; // not part of a mixed renamed/bare family
            var nameInput = OverloadNameDisambiguator.BuildLabelDerivedNameInput(m);
            if (string.Equals(nameInput, m.Name, StringComparison.Ordinal))
                continue; // no label to fold — honest bare name, leave it
            if (disambiguatedSlotKeys.Add(slotKey))
                pending.Add((slotKey, nameInput, fromTypeRung: false)); // the fold composes labels, never types
        }

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
        foreach (var (slotKey, baseName, fromTypeRung) in pending)
        {
            if (result.ContainsKey(slotKey))
                continue;
            // Representative method for this slot key (any member with that key — they share projected shape).
            var rep = candidates.First(m => EveryProtocolEmitter.GetMethodKey(m) == slotKey);

            // Same ladder the class lane walks, and deliberately NOT a numeric last resort. A protocol
            // requirement's C# name is the interface member every conformer must declare and every proxy
            // must forward, so an opaque `FooBar2` propagates the meaningless name across the whole
            // conformance surface. When neither the labels nor the parameter types free a name, leave the
            // slot out of the map entirely: the member then keeps its natural key and collapses through the
            // ordinary emitted-signature dedup as a DuplicateSignature — the same graceful degradation this
            // helper already applies to a pure type-erasure pair.
            // A family that already took the type rung has no rung left: its stored input IS the type-derived
            // name, so re-composing from it would append the parameter types a SECOND time
            // (`TransformWithRefBox` → `TransformWithRefBoxWithRefBox`) — an invented spelling no lane and no
            // conforming class would ever produce. The class lane refuses the member at that point instead;
            // here the equivalent is to leave the slot out of the map and let it collapse.
            var ladder = fromTypeRung
                ? new[] { baseName }
                : new[] { baseName, OverloadNameDisambiguator.BuildTypeDerivedNameInput(rep, baseName) };
            string? accepted = null;
            foreach (var candidateInput in ladder)
            {
                if (string.Equals(candidateInput, rep.Name, StringComparison.Ordinal))
                    continue;
                if (reserved.Add(BuildProjectedKeyWithOverride(rep, typeDatabase, protocolDecl, propertyNames: null, candidateInput)))
                {
                    accepted = candidateInput;
                    break;
                }
            }
            if (accepted != null)
            {
                result[slotKey] = accepted;
                // Same ship-gate ledger the class and free-function lanes feed: an interface requirement is
                // public surface too, so its disambiguation has to be auditable by the same records.
                OverloadNameDisambiguator.RecordProtocolAssignment(
                    rep,
                    !string.Equals(accepted, baseName, StringComparison.Ordinal) || fromTypeRung
                        ? OverloadNameOutcome.TypeDerived
                        : OverloadNameOutcome.LabelDerived,
                    accepted);
            }
        }
        return result;
    }
}
