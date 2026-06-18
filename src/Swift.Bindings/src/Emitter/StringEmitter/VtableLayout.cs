// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>The three member families that occupy reverse-dispatch vtable slots.</summary>
internal enum VtableMemberKind
{
    Property,
    Subscript,
    Method,
}

/// <summary>
/// Why a protocol member did or did not get a reverse-dispatch vtable slot. Excluded members are
/// RETAINED in the layout (with <see cref="VtableSlot.Included"/> == false) so a renderer that must
/// emit a fatalError/stub for a non-slot member can still iterate one ordered list. The specific
/// reason is preserved so diagnostics and tests can distinguish a pre-skipped member (no slot index
/// consumed) from a skip-but-consume member (index consumed, field omitted) from an overload that
/// collapsed onto an earlier slot.
/// </summary>
internal enum SlotVerdict
{
    /// <summary>Member occupies a Swift <c>{P}_vtable</c> slot AND the matching C# struct slot.</summary>
    Included,
    ExcludedConstructor,
    ExcludedStatic,
    ExcludedObjCOptional,
    ExcludedNonRequirement,
    ExcludedNonDispatchableClosure,
    ExcludedMethodLevelGeneric,
    ExcludedSelfTyped,
    ExcludedMixedGeneric,
    /// <summary>
    /// A raw-key duplicate of an earlier slot (an effect-INSENSITIVE overload collision). The
    /// producer walks collapse it onto the earlier slot's index and emit no new field, so it
    /// carries that earlier index and <see cref="VtableSlot.Included"/> == false.
    /// </summary>
    DuplicateOverload,
}

/// <summary>
/// One ordered record per protocol member, in the producer's declaration-walk order (properties,
/// then subscripts, then methods). This is the canonical reverse-dispatch slot ABI: every walk that
/// lays out a vtable struct renders this list rather than re-deriving membership/index/width, so the
/// Swift <c>{P}_vtable</c> struct, the C# <c>{P}SwiftVTable</c> / <c>{P}LocalVTable</c> mirrors, and
/// the EveryProtocol extension body cannot drift out of positional agreement (the Bug #21 class).
/// </summary>
/// <remarks>
/// Index allocation reproduces the producer EXACTLY:
///   • Properties are NAME-keyed (<c>func_{name}_get/set</c>); they carry no numeric index
///     (<see cref="SlotIndex"/> == -1).
///   • Subscripts are POSITION-keyed (<c>func_subscript_{index}_get/set</c>); a non-static subscript
///     consumes its index even when excluded (skip-but-consume), a static subscript consumes none.
///   • Methods are RAW-KEY-keyed (<see cref="VtableLayoutBuilder.GetSlotKey"/>, label-inclusive,
///     async-sensitive); constructors / static / @objc-optional methods consume NO index, every other
///     first-occurrence raw-distinct method consumes one even when excluded (skip-but-consume), and a
///     raw-key duplicate collapses onto the earlier slot.
/// This is the reverse/vtable index axis ONLY. The forward/SBW witness-dispatch axis
/// (<see cref="WitnessDispatchEmitter"/>) is a separate, label-blind index space and is not modeled here.
/// </remarks>
internal sealed record VtableSlot(
    VtableMemberKind Kind,
    BaseDecl Member,
    string IdentityKey,
    int SlotIndex,
    int Width,
    bool Included,
    SlotVerdict Verdict,
    bool IsDispatchableClosure)
{
    public MethodDecl? AsMethod => Member as MethodDecl;
    public PropertyDecl? AsProperty => Member as PropertyDecl;
    public SubscriptDecl? AsSubscript => Member as SubscriptDecl;
}

/// <summary>One ordered slot model computed once per protocol; see <see cref="VtableSlot"/>.</summary>
internal sealed record VtableLayout(
    ProtocolDecl Protocol,
    IReadOnlyList<VtableSlot> Slots)
{
    /// <summary>Every member that occupies a slot AND emits a field (the ABI-positional set).</summary>
    public IEnumerable<VtableSlot> IncludedSlots => Slots.Where(s => s.Included);

    public IEnumerable<VtableSlot> IncludedProperties =>
        Slots.Where(s => s.Kind == VtableMemberKind.Property && s.Included);

    public IEnumerable<VtableSlot> IncludedSubscripts =>
        Slots.Where(s => s.Kind == VtableMemberKind.Subscript && s.Included);

    public IEnumerable<VtableSlot> IncludedMethods =>
        Slots.Where(s => s.Kind == VtableMemberKind.Method && s.Included);

    /// <summary>
    /// Raw-key → slot-index map for METHOD slots (the producer's <see cref="VtableLayoutBuilder.GetSlotKey"/>
    /// → vtable index). The FILLABILITY walks (receivers, cctor assignments, cross-module parents) and the
    /// EveryProtocol extension body look their slot index up here instead of running a parallel
    /// <c>methodIndex++</c> counter, so a receiver/assignment/dispatch-body can never drift from the struct
    /// field it targets (the Bug #21 class). The keying matches what those walks compute: a duplicate-overload
    /// slot shares the earlier slot's index, and a pre-skipped method (constructor / static / @objc-optional,
    /// <see cref="VtableSlot.SlotIndex"/> == -1) is ABSENT — the walks pre-skip those before the lookup too.
    /// This carries the index ONLY; the walks keep their own fillability filters (skip sets, raw/projected-key
    /// dedup) to decide whether to actually fill the slot.
    /// </summary>
    public IReadOnlyDictionary<string, int> MethodSlotIndexByKey =>
        Slots.Where(s => s.Kind == VtableMemberKind.Method && s.SlotIndex >= 0)
            .GroupBy(s => s.IdentityKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SlotIndex, StringComparer.Ordinal);
}

/// <summary>
/// Builds the single <see cref="VtableLayout"/> per protocol from ONE membership function
/// (<see cref="ClassifyMethod"/> / <see cref="ClassifyProperty"/> / <see cref="ClassifySubscript"/>),
/// ONE slot-identity key (<see cref="GetSlotKey"/>), and ONE width function (<see cref="GetWidth"/>).
///
/// The <c>Classify*</c> predicates are PURE and STATELESS — they are the canonical vtable-slot
/// membership oracle. <see cref="ProtocolVtableMembers"/> delegates to them, so the same-module struct
/// walks, the cross-module parent walks, and this builder all decide membership identically without a
/// stateful skip set. (The same-module <c>ProtocolHandler</c> skip sets remain in force for INTERFACE
/// emission — DIM/NotSupported/CS0535 — they are simply no longer a second, divergent vtable oracle.)
/// </summary>
internal sealed class VtableLayoutBuilder
{
    private readonly ClosureHandler _closureHandler;

    public VtableLayoutBuilder(ITypeDatabase typeDatabase)
    {
        _closureHandler = new ClosureHandler(typeDatabase);
    }

    /// <summary>Computes the ordered slot model for <paramref name="protocol"/>.</summary>
    public VtableLayout Build(ProtocolDecl protocol)
    {
        var slots = new List<VtableSlot>();

        // Properties — name-keyed, no numeric index.
        foreach (var property in protocol.Properties)
        {
            var verdict = ClassifyProperty(property, protocol, _closureHandler);
            bool included = verdict == SlotVerdict.Included;
            bool isClosure = included
                && EveryProtocolEmitter.HasClosureInPropertyType(property)
                && EveryProtocolEmitter.IsDispatchableClosureProperty(property, _closureHandler);
            slots.Add(new VtableSlot(
                VtableMemberKind.Property, property,
                IdentityKey: property.Name, SlotIndex: -1, Width: 0,
                Included: included, Verdict: verdict, IsDispatchableClosure: isClosure));
        }

        // Subscripts — declaration-position index, skip-but-consume for non-static excluded,
        // static consumes nothing (matches EveryProtocolEmitter.EmitProtocolVtableStruct).
        int subscriptIndex = 0;
        foreach (var subscript in protocol.Subscripts)
        {
            var verdict = ClassifySubscript(subscript, protocol);
            if (verdict == SlotVerdict.ExcludedStatic)
            {
                slots.Add(new VtableSlot(
                    VtableMemberKind.Subscript, subscript,
                    IdentityKey: "subscript:static", SlotIndex: -1, Width: 0,
                    Included: false, Verdict: verdict, IsDispatchableClosure: false));
                continue;
            }
            int idx = subscriptIndex++;
            slots.Add(new VtableSlot(
                VtableMemberKind.Subscript, subscript,
                IdentityKey: $"subscript:{idx}", SlotIndex: idx, Width: 0,
                Included: verdict == SlotVerdict.Included, Verdict: verdict, IsDispatchableClosure: false));
        }

        // Methods — raw-key dedup, skip-but-consume. Constructors / static / @objc-optional are
        // pre-skipped BEFORE the index increment (no slot index), exactly as the producer does.
        int methodIndex = 0;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var method in protocol.Methods)
        {
            var verdict = ClassifyMethod(method, protocol, _closureHandler);
            if (verdict is SlotVerdict.ExcludedConstructor
                or SlotVerdict.ExcludedStatic
                or SlotVerdict.ExcludedObjCOptional)
            {
                slots.Add(new VtableSlot(
                    VtableMemberKind.Method, method,
                    IdentityKey: $"{method.Name}:preskip", SlotIndex: -1, Width: 0,
                    Included: false, Verdict: verdict, IsDispatchableClosure: false));
                continue;
            }

            var key = GetSlotKey(method);
            if (seen.TryGetValue(key, out var existingIdx))
            {
                // Raw-key duplicate (effect-insensitive overload collision): collapses onto the
                // earlier slot, no new index, no field.
                slots.Add(new VtableSlot(
                    VtableMemberKind.Method, method,
                    IdentityKey: key, SlotIndex: existingIdx, Width: 0,
                    Included: false, Verdict: SlotVerdict.DuplicateOverload, IsDispatchableClosure: false));
                continue;
            }

            int idx = methodIndex++;
            seen[key] = idx;
            bool included = verdict == SlotVerdict.Included;
            bool isClosure = EveryProtocolEmitter.HasClosureInMethodSignature(method)
                && (EveryProtocolEmitter.IsDispatchableClosureMethod(method, _closureHandler)
                    || EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, _closureHandler)
                    || EveryProtocolEmitter.IsDispatchableClosureReturningMethod(method, _closureHandler));
            slots.Add(new VtableSlot(
                VtableMemberKind.Method, method,
                IdentityKey: key, SlotIndex: idx, Width: GetWidth(method),
                Included: included, Verdict: verdict, IsDispatchableClosure: isClosure));
        }

        return new VtableLayout(protocol, slots);
    }

    // ---- THE membership function (pure, stateless) -------------------------------------------

    /// <summary>
    /// Classifies a method's vtable-slot membership. Mirrors
    /// <see cref="ProtocolVtableMembers.IncludesMethod"/> branch-for-branch (which delegates here),
    /// returning the specific exclusion reason instead of a bare bool.
    /// </summary>
    internal static SlotVerdict ClassifyMethod(MethodDecl method, ProtocolDecl protocol, ClosureHandler closureHandler)
    {
        if (method.IsConstructor)
            return SlotVerdict.ExcludedConstructor;
        if (method.MethodType == MethodType.Static)
            return SlotVerdict.ExcludedStatic;
        if (method.IsObjCOptional)
            return SlotVerdict.ExcludedObjCOptional;
        if (EveryProtocolEmitter.HasClosureInMethodSignature(method)
            && !EveryProtocolEmitter.IsDispatchableClosureMethod(method, closureHandler)
            && !EveryProtocolEmitter.IsDispatchableClosureReturningMethod(method, closureHandler)
            && !EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, closureHandler))
            return SlotVerdict.ExcludedNonDispatchableClosure;
        if (EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method))
            return SlotVerdict.ExcludedMethodLevelGeneric;
        if (EveryProtocolEmitter.HasSelfTypeParamInSignature(method))
            return SlotVerdict.ExcludedSelfTyped;
        if (EveryProtocolEmitter.IsMixedGenericProtocol(protocol))
            return SlotVerdict.ExcludedMixedGeneric;
        return SlotVerdict.Included;
    }

    /// <summary>
    /// Classifies a property's vtable-slot membership. Mirrors
    /// <see cref="ProtocolVtableMembers.IncludesProperty"/> (which delegates here).
    /// </summary>
    internal static SlotVerdict ClassifyProperty(PropertyDecl property, ProtocolDecl protocol, ClosureHandler closureHandler)
    {
        if (property.IsStatic)
            return SlotVerdict.ExcludedStatic;
        if (property.IsObjCOptional)
            return SlotVerdict.ExcludedObjCOptional;
        if (!property.IsProtocolRequirement)
            return SlotVerdict.ExcludedNonRequirement;
        bool isMixedGeneric = EveryProtocolEmitter.IsMixedGenericProtocol(protocol);
        if (EveryProtocolEmitter.HasClosureInPropertyType(property))
        {
            if (!EveryProtocolEmitter.IsDispatchableClosureProperty(property, closureHandler))
                return SlotVerdict.ExcludedNonDispatchableClosure;
            if (isMixedGeneric)
                return SlotVerdict.ExcludedMixedGeneric;
            return SlotVerdict.Included;
        }
        if (EveryProtocolEmitter.ContainsSelfTypeParam(property.SwiftTypeSpec))
            return SlotVerdict.ExcludedSelfTyped;
        if (isMixedGeneric)
            return SlotVerdict.ExcludedMixedGeneric;
        return SlotVerdict.Included;
    }

    /// <summary>
    /// Classifies a subscript's vtable-slot membership. Mirrors
    /// <see cref="ProtocolVtableMembers.IncludesSubscript"/> (which delegates here).
    /// </summary>
    internal static SlotVerdict ClassifySubscript(SubscriptDecl subscript, ProtocolDecl protocol)
    {
        if (subscript.IsStatic)
            return SlotVerdict.ExcludedStatic;
        if (EveryProtocolEmitter.ContainsSelfTypeParam(subscript.ReturnTypeSpec))
            return SlotVerdict.ExcludedSelfTyped;
        if (subscript.IndexParameters.Any(p => EveryProtocolEmitter.ContainsSelfTypeParam(p.SwiftTypeSpec)))
            return SlotVerdict.ExcludedSelfTyped;
        if (EveryProtocolEmitter.IsMixedGenericProtocol(protocol))
            return SlotVerdict.ExcludedMixedGeneric;
        return SlotVerdict.Included;
    }

    // ---- THE slot-identity key + width -------------------------------------------------------

    /// <summary>
    /// The single reverse/vtable slot-identity key — label-inclusive, async-sensitive. Delegates to
    /// <see cref="EveryProtocolEmitter.GetMethodKey"/>, the producer's index key, so the model's slot
    /// ordering is byte-identical to the legacy walks.
    /// </summary>
    internal static string GetSlotKey(MethodDecl method) => EveryProtocolEmitter.GetMethodKey(method);

    /// <summary>
    /// Pointer-slot width a method contributes: a dispatchable closure param expands to two slots,
    /// every other (non-debug, non-empty-tuple) param to one. Mirrors the slot-count loop in
    /// <see cref="ProtocolProxyEmitter.EmitMethodLocalVtableField(CSharpWriter, MethodDecl, ProtocolDecl, int, HashSet{string})"/>.
    /// </summary>
    internal int GetWidth(MethodDecl method)
    {
        int width = 0;
        foreach (var p in method.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(p) || p.SwiftTypeSpec.IsEmptyTuple)
                continue;
            width += EveryProtocolEmitter.CountVtableSlots(p.SwiftTypeSpec, _closureHandler);
        }
        return width;
    }
}
