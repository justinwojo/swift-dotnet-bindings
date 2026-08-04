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
/// SCOPE — the same two-rung ladder the class lane walks, chosen the same way: a rung is taken for a whole
/// FAMILY only when every discriminand's candidate key under that rung is distinct AND unoccupied
/// scope-wide. So a group takes the LABEL rung when its members yield distinct label-derived names that no
/// uncontested sibling already holds; identical label-derived names (a pure type-erasure collision — same
/// labels, different Swift types projecting to the same C# type, e.g. <c>add(any Expression)</c> /
/// <c>add(any Sendable)</c>) OR a label-derived name a sibling already emits under (<c>foo(bar:)</c>
/// alongside a real <c>fooBar(_:)</c>) moves the ENTIRE family to the TYPE rung, exactly as a conforming
/// class body would. Escalating only the blocked member — leaving its sibling on the label rung — is what
/// splits the lanes: the class body moves both, so the interface would require a name the conformer never
/// declares and the whole conformance is dropped as unsatisfiable. When neither rung separates the family,
/// each member takes its type-derived name if that key is still free and the rest are aliased onto the
/// survivor's name so the ordinary duplicate-signature dedup drops them — the class lane's refusal arm
/// reaches the same surviving names, and differs only in reporting the blocked members as refusals with a
/// report entry rather than collapsing them silently.
/// Grouping is on the PROJECTED key, so type-erasure overloads whose projected keys already differ never
/// group here in the first place.
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

    /// <summary>
    /// One requirement of a contested family, carried with the slot key it is keyed by and the
    /// label-rung input its own content yields. Grouping the three together is what lets rung selection
    /// consider the family as a unit instead of one member at a time.
    /// </summary>
    private readonly record struct Discriminand(string SlotKey, MethodDecl Decl, string LabelInput);

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

        // CONTENT PASS — settle, from the requirements alone, which groups are contested and who owns each
        // contested family's natural name, before any name is handed out. Splitting the walk this way is
        // what lets the scope-wide occupancy set below be seeded with every key that is already spoken for,
        // which in turn is what makes rung selection a family-wide decision rather than a per-member one.
        //
        // A group needs disambiguation only when it holds ≥2 DISTINCT slot keys — genuinely different
        // requirements rather than a true duplicate.
        //
        // Scope-wide occupancy: every requirement that emits under its natural projected key holds that key
        // for the rest of the walk, so a derived name can never silently land on one. This is the same set
        // the class lane calls `reserved`, seeded the same way — the uncontested groups up front, each
        // family's own key as soon as an owner claims it.
        //
        // The reservation is deliberately property-AGNOSTIC (propertyNames: null), matching the projected-key
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
        var contested = new List<(string GroupKey, List<Discriminand> Discriminands)>();
        foreach (var groupKey in groupOrder)
        {
            var group = groups[groupKey];

            // One representative (first in declaration order) per distinct slot key.
            var reps = new List<MethodDecl>();
            var seenSlotKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in group)
            {
                if (seenSlotKeys.Add(EveryProtocolEmitter.GetMethodKey(m)))
                    reps.Add(m);
            }
            if (reps.Count < 2)
            {
                // Uncontested, or a family of true duplicates that collapses as before. Either way every
                // member of the group emits under this key, so it is occupied.
                reserved.Add(groupKey);
                continue;
            }

            var named = reps
                .Select(m => new Discriminand(EveryProtocolEmitter.GetMethodKey(m), m, OverloadNameDisambiguator.BuildLabelDerivedNameInput(m)))
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
            //
            // One honest difference from the class lane, and the reason it is not a divergence here: the
            // class rule also requires the claimant's UN-renamed natural name to be the contested spelling,
            // which stops a member pushed onto that spelling by a property collision from out-claiming the
            // member actually named that way. This map is property-agnostic by construction, so no such
            // rename is visible on its keys and there is nothing for the extra clause to catch.
            var claimants = named.Where(x => string.Equals(x.LabelInput, x.Decl.Name, StringComparison.Ordinal)).ToList();
            var bareNameOwner = claimants.Count == 1 ? claimants[0].SlotKey : null;
            if (bareNameOwner != null)
                reserved.Add(groupKey);
            var discriminands = named
                .Where(x => !string.Equals(x.SlotKey, bareNameOwner, StringComparison.Ordinal))
                .ToList();
            if (discriminands.Count == 0)
                continue;
            contested.Add((groupKey, discriminands));
        }
        if (contested.Count == 0)
            return EmptyMap;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var disambiguatedSlotKeys = new HashSet<string>(StringComparer.Ordinal);

        // NAMING WALK — one rung per FAMILY, chosen the way the class lane chooses it: a rung is taken only
        // if EVERY discriminand's candidate key under that rung is distinct AND unoccupied. A member-by-member
        // acceptance instead lets one sibling keep the label rung while the sibling whose label-derived name
        // happens to be taken drops to the type rung — and since a conforming class body runs the class lane
        // over the same shape and moves the WHOLE family down together, the interface would then ask for
        // `ConfigureOther` while the class declares `ConfigureOtherWithIntAndInt`. The conformance validator
        // reads that as an emitted-name divergence and drops the whole `: IFoo`, so the two lanes agreeing on
        // the rung is what keeps the conformance rather than a cosmetic preference.
        //
        // Candidates are tested as projected KEYS, not as name strings: the family shares one projected
        // parameter list by construction, so the key is what the member will actually occupy, and two inputs
        // the name shaper folds together are caught here instead of colliding at emission.
        foreach (var (groupKey, discriminands) in contested)
        {
            var labelInputs = discriminands.Select(x => x.LabelInput).ToList();
            // The base input for the type rung is each member's own label-derived input, the same composition
            // the class lane feeds BuildTypeDerivedNameInput, so both lanes land on the identical string by
            // construction rather than by agreement of two hand-kept ladders.
            var typeInputs = discriminands
                .Select(x => OverloadNameDisambiguator.BuildTypeDerivedNameInput(x.Decl, x.LabelInput))
                .ToList();

            var chosen = RungFits(discriminands, labelInputs, typeDatabase, protocolDecl, reserved) ? labelInputs
                : RungFits(discriminands, typeInputs, typeDatabase, protocolDecl, reserved) ? typeInputs
                : null;

            if (chosen != null)
            {
                // The rung travels WITH the assignment rather than being re-inferred later from string
                // equality: a type-rung family's input IS the name it emits under, so comparing the accepted
                // name against its own base would book every type-rung assignment as LabelDerived and quietly
                // lie to the ship-gate ledger.
                var outcome = ReferenceEquals(chosen, labelInputs)
                    ? OverloadNameOutcome.LabelDerived
                    : OverloadNameOutcome.TypeDerived;
                for (int i = 0; i < discriminands.Count; i++)
                    Accept(discriminands[i], chosen[i], outcome);
                continue;
            }

            // NO RUNG SEPARATES THE FAMILY. Whatever distinguishes these requirements in Swift is invisible
            // at a C# call site, so they cannot all survive. Walk them in declaration order and let each take
            // its type-derived name if that key is still free — the same first-fit the class lane applies to
            // a conforming body, so the survivor carries the same name in both lanes.
            //
            // Where this lane deliberately parts company: the class lane books the blocked members as Refused
            // and drops them with a report entry. This map has no refusal channel — a slot key it omits reads
            // as "keeps its natural name", which would leave the blocked requirement standing as a SECOND
            // interface member under a different name, callable identically to the survivor and dispatching
            // to a vtable slot nothing fills. So a blocked member is instead aliased ONTO the survivor's
            // name: the family shares one projected parameter list by construction, so the alias reproduces
            // the survivor's key exactly and the ordinary duplicate-signature dedup drops the member, the way
            // it dropped the whole family before either lane named anything. Consumer-visible outcome matches
            // the class lane member for member — one surviving member under the same name — and the alias is
            // kept out of the rename ledger because nothing about it reaches a consumer.
            string? survivorName = null;
            for (int i = 0; i < discriminands.Count; i++)
            {
                var d = discriminands[i];
                var nameInput = typeInputs[i];
                if (reserved.Add(BuildProjectedKeyWithOverride(d.Decl, typeDatabase, protocolDecl, propertyNames: null, nameInput)))
                {
                    survivorName ??= nameInput;
                    if (string.Equals(nameInput, d.Decl.Name, StringComparison.Ordinal))
                        continue;
                    Record(d, nameInput, OverloadNameOutcome.TypeDerived);
                    continue;
                }
                // No survivor yet (the first member's own type key was already spoken for), or the survivor
                // kept its natural name: either way the natural key is what collapses them, which is what
                // omitting the member from the map already says.
                if (survivorName == null || string.Equals(survivorName, d.Decl.Name, StringComparison.Ordinal))
                    continue;
                Alias(d, survivorName);
            }
        }

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
                continue; // already label-derived by the naming walk
            if (!disambiguatedBaseNames.Contains(m.Name))
                continue; // not part of a mixed renamed/bare family
            var labelInput = OverloadNameDisambiguator.BuildLabelDerivedNameInput(m);
            if (string.Equals(labelInput, m.Name, StringComparison.Ordinal))
                continue; // no label to fold — honest bare name, leave it

            // The fold is per-MEMBER, not per-family, and that is not a lane divergence: a folded sibling is
            // alone in its projected group by definition (it never collided with anything), so there is no
            // family to move down together and no conforming class body that would move it. It walks the
            // ladder on its own — labels first, then its Swift parameter types — and when neither is free it
            // is simply left bare, which is the name it carried before the fold existed.
            var ladder = new[] { labelInput, OverloadNameDisambiguator.BuildTypeDerivedNameInput(m, labelInput) };
            foreach (var candidateInput in ladder)
            {
                if (string.Equals(candidateInput, m.Name, StringComparison.Ordinal))
                    continue;
                if (!reserved.Add(BuildProjectedKeyWithOverride(m, typeDatabase, protocolDecl, propertyNames: null, candidateInput)))
                    continue;
                Record(
                    new Discriminand(slotKey, m, labelInput),
                    candidateInput,
                    string.Equals(candidateInput, labelInput, StringComparison.Ordinal)
                        ? OverloadNameOutcome.LabelDerived
                        : OverloadNameOutcome.TypeDerived);
                break;
            }
        }

        return result;

        void Accept(Discriminand d, string nameInput, OverloadNameOutcome outcome)
        {
            // RungFits already proved this key free; claiming it here is what stops a LATER family in the
            // walk from landing on it.
            reserved.Add(BuildProjectedKeyWithOverride(d.Decl, typeDatabase, protocolDecl, propertyNames: null, nameInput));
            // A label-less member in an ownerless family reaches a rung that adds nothing to its own name.
            // Absent from the map IS how it keeps that name — every Effective* helper then falls through to
            // its natural key — so there is no consumer-visible rename to record either.
            if (string.Equals(nameInput, d.Decl.Name, StringComparison.Ordinal))
                return;
            Record(d, nameInput, outcome);
        }

        void Alias(Discriminand d, string nameInput)
        {
            if (!result.TryAdd(d.SlotKey, nameInput))
                return;
            // Counted as settled so the family fold leaves it alone: the alias exists to be collapsed, and
            // handing it a name of its own would resurrect the member the collapse is dropping.
            disambiguatedSlotKeys.Add(d.SlotKey);
        }

        void Record(Discriminand d, string nameInput, OverloadNameOutcome outcome)
        {
            if (!result.TryAdd(d.SlotKey, nameInput))
                return;
            disambiguatedSlotKeys.Add(d.SlotKey);
            // Same ship-gate ledger the class and free-function lanes feed: an interface requirement is
            // public surface too, so its disambiguation has to be auditable by the same records.
            OverloadNameDisambiguator.RecordProtocolAssignment(d.Decl, outcome, nameInput);
        }
    }

    /// <summary>
    /// Whether a whole family can be named on one rung: every discriminand's candidate must project to a key
    /// that is both distinct within the family and unoccupied scope-wide. This is the protocol-lane counterpart
    /// of the class lane's own rung test, and it is the reason the two lanes agree — a family blocked on one
    /// candidate moves down as a unit in BOTH lanes, so an interface never asks for a member name the
    /// conforming class body resolves differently.
    /// </summary>
    private static bool RungFits(
        IReadOnlyList<Discriminand> discriminands,
        IReadOnlyList<string> nameInputs,
        ITypeDatabase typeDatabase,
        ProtocolDecl protocolDecl,
        IReadOnlySet<string> reserved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < discriminands.Count; i++)
        {
            var key = BuildProjectedKeyWithOverride(discriminands[i].Decl, typeDatabase, protocolDecl, propertyNames: null, nameInputs[i]);
            if (reserved.Contains(key) || !seen.Add(key))
                return false;
        }
        return true;
    }
}
