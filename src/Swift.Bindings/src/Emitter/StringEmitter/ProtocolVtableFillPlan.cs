// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>Why a reverse-dispatch obligation did or did not receive a function pointer.</summary>
internal enum VtableFillDisposition
{
    /// <summary>The slot receives a <c>&amp;Receive_*</c> function pointer.</summary>
    Filled,

    /// <summary>
    /// The requirement occupies no vtable slot at all — <see cref="VtableLayoutBuilder"/> excluded it
    /// (non-dispatchable closure, method-level generic, Self-typed, mixed-generic protocol, nested
    /// <c>@objc</c> existential). The C# interface may still declare it; nothing can call it back.
    /// </summary>
    NoVtableSlot,

    /// <summary>
    /// The slot exists but no <c>Receive_*</c> trampoline was emitted for it: the member is in one of
    /// the interface-emission skip sets, so there is no C# surface to forward to.
    /// </summary>
    NoReceiverEmitted,

    /// <summary>
    /// An earlier overload already owns this member's raw-signature or projected-C# key, so the
    /// trampoline it would have used belongs to that sibling.
    /// </summary>
    CollapsedOntoOverload,
}

/// <summary>
/// One reverse-dispatch obligation: a protocol requirement a C# implementer would be expected to
/// satisfy, together with whether the proxy's vtable actually wires it back to Swift.
/// </summary>
/// <param name="Kind">Member family.</param>
/// <param name="Member">The declaration.</param>
/// <param name="DisplayName">Printed member name, as a consumer would look for it.</param>
/// <param name="Disposition">Whether — and if not, why not — the slot gets a function pointer.</param>
/// <param name="Verdict">The layout verdict this entry was derived from.</param>
/// <param name="SlotSuffixes">
/// The vtable field suffixes this entry fills, already deduped and in emission order (e.g.
/// <c>["foo_get", "foo_set"]</c>, <c>["subscript_0_get"]</c>, <c>["bar_2"]</c>). Empty unless
/// <see cref="Disposition"/> is <see cref="VtableFillDisposition.Filled"/>. The local vtable renders
/// each as <c>Func_{suffix} = &amp;Receive_{suffix}</c>, the Swift-facing mirror as
/// <c>func_{suffix} = (IntPtr)_localVTable.Func_{suffix}</c>.
/// </param>
internal sealed record VtableFillEntry(
    VtableMemberKind Kind,
    BaseDecl Member,
    string DisplayName,
    VtableFillDisposition Disposition,
    SlotVerdict Verdict,
    IReadOnlyList<string> SlotSuffixes);

/// <summary>
/// The fillability dual of <see cref="VtableLayout"/>. <see cref="VtableLayout"/> answers "which
/// members occupy which slot" (the ABI-positional question); this answers "which of those slots
/// actually receive a callback pointer" — the question the proxy's static constructor decides, the
/// consumer-facing honesty of <c>I{Protocol}</c> depends on, and nothing previously computed
/// anywhere a report or an attribute could read.
///
/// <para>
/// The distinction matters because a requirement can be present in the layout and still never be
/// filled: a closure-bearing requirement is gated <c>GateDisposition.InterfaceOnly</c>, so it is
/// declared on the interface but gets no <c>Receive_*</c> trampoline. When EVERY requirement lands
/// that way the proxy builds an all-null vtable and registers it, and a C# type implementing the
/// interface compiles, runs, and is never called — the failure this model exists to make visible.
/// </para>
///
/// <para>
/// Pure and stateless: a function of the protocol, the type database, and the three interface-emission
/// skip sets, all of which are settled before the interface declaration is written. That is what lets
/// the same plan be read by the attribute decision (before the declaration), the report row, and the
/// cctor assignment loops, instead of each re-deriving fillability inline and drifting.
/// </para>
/// </summary>
internal sealed record ProtocolVtableFillPlan(
    ProtocolDecl Protocol,
    IReadOnlyList<VtableFillEntry> Entries)
{
    /// <summary>Obligations whose slot receives a function pointer, in emission order.</summary>
    public IEnumerable<VtableFillEntry> FilledEntries =>
        Entries.Where(e => e.Disposition == VtableFillDisposition.Filled);

    /// <summary>Obligations that get no function pointer, in declaration order.</summary>
    public IEnumerable<VtableFillEntry> UnfilledEntries =>
        Entries.Where(e => e.Disposition != VtableFillDisposition.Filled);

    /// <summary>
    /// Every vtable field suffix that gets a pointer, deduped and in emission order (properties,
    /// then subscripts, then methods). Both cctor assignment loops render this one sequence, so the
    /// Swift-facing mirror cannot claim a pointer the local table left null.
    /// </summary>
    public IReadOnlyList<string> FilledSlotSuffixes =>
        FilledEntries.SelectMany(e => e.SlotSuffixes).ToList();

    /// <summary>
    /// How many requirements a C# implementer is expected to satisfy — direct, non-static,
    /// non-constructor, non-<c>@objc optional</c> instance requirements, counted once per vtable slot.
    /// Requirements the generator cannot lower are deliberately INCLUDED: they are precisely what
    /// makes the interface's implementability claim dishonest.
    /// </summary>
    public int ObligationCount => Entries.Count;

    /// <summary>
    /// The number of callback pointers the vtable actually carries. This is the fillability count,
    /// NOT <c>VtableLayout.IncludedSlots.Count</c> (membership) — a slot can be laid out and left
    /// null. Accessor-level: a read-write property contributes two.
    /// </summary>
    public int FilledCallbackCount => FilledSlotSuffixes.Count;

    /// <summary>
    /// True when the protocol declares at least one reverse-dispatch obligation and NONE of them is
    /// wired. A partially populated vtable is never hollow — the strict zero is the whole point: with
    /// one live slot the interface is honestly implementable for that member.
    /// </summary>
    public bool IsHollow => ObligationCount > 0 && FilledCallbackCount == 0;

    /// <summary>
    /// One line per unfilled obligation, for the report row's Details. Ordered as declared.
    /// </summary>
    public string DescribeUnfilled() =>
        string.Join("; ", UnfilledEntries.Select(e => $"{e.Kind.ToString().ToLowerInvariant()} {e.DisplayName}: {Describe(e.Disposition)} ({e.Verdict})"));

    private static string Describe(VtableFillDisposition disposition) => disposition switch
    {
        VtableFillDisposition.NoVtableSlot => "no vtable slot",
        VtableFillDisposition.NoReceiverEmitted => "no receiver emitted",
        VtableFillDisposition.CollapsedOntoOverload => "collapsed onto an earlier overload",
        _ => disposition.ToString(),
    };
}

/// <summary>
/// Builds the <see cref="ProtocolVtableFillPlan"/> for a protocol by walking
/// <see cref="VtableLayout.Slots"/> and applying the same fillability filters the proxy's local
/// vtable assignment loop applies — the strictest of the walks, and therefore the one that decides
/// whether a pointer is genuinely non-null (the Swift-facing mirror only copies what the local table
/// holds, so a slot the local loop leaves null is null on both sides regardless).
/// </summary>
internal static class ProtocolVtableFillPlanBuilder
{
    public static ProtocolVtableFillPlan Build(
        ProtocolDecl protocol,
        ITypeDatabase typeDatabase,
        IReadOnlySet<string>? skippedMethodKeys = null,
        IReadOnlySet<string>? skippedPropertyNames = null,
        IReadOnlySet<int>? skippedSubscriptIndices = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(typeDatabase);

        var methodKeys = skippedMethodKeys ?? new HashSet<string>();
        var propertyNames = skippedPropertyNames ?? new HashSet<string>();
        var subscriptIndices = skippedSubscriptIndices ?? new HashSet<int>();

        var layout = new VtableLayoutBuilder(typeDatabase).Build(protocol);
        var entries = new List<VtableFillEntry>();
        var emittedSuffixes = new HashSet<string>(StringComparer.Ordinal);
        var emittedRawKeys = new HashSet<string>(StringComparer.Ordinal);
        var emittedProjectedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slot in layout.Slots)
        {
            switch (slot.Kind)
            {
                case VtableMemberKind.Property:
                    AddProperty(slot, propertyNames, entries, emittedSuffixes);
                    break;
                case VtableMemberKind.Subscript:
                    AddSubscript(slot, subscriptIndices, entries, emittedSuffixes);
                    break;
                case VtableMemberKind.Method:
                    AddMethod(slot, protocol, typeDatabase, methodKeys, entries,
                        emittedSuffixes, emittedRawKeys, emittedProjectedKeys);
                    break;
            }
        }

        return new ProtocolVtableFillPlan(protocol, entries);
    }

    private static void AddProperty(
        VtableSlot slot,
        IReadOnlySet<string> skippedPropertyNames,
        List<VtableFillEntry> entries,
        HashSet<string> emittedSuffixes)
    {
        // A static, @objc-optional or non-requirement property is not a reverse-dispatch obligation
        // at all: nothing about the interface claims a C# implementer will be called back through it.
        if (slot.Verdict is SlotVerdict.ExcludedStatic
            or SlotVerdict.ExcludedObjCOptional
            or SlotVerdict.ExcludedNonRequirement)
            return;

        var property = slot.AsProperty!;
        if (!slot.Included)
        {
            entries.Add(Unfilled(slot, property.Name, VtableFillDisposition.NoVtableSlot));
            return;
        }
        if (skippedPropertyNames.Contains(property.Name))
        {
            entries.Add(Unfilled(slot, property.Name, VtableFillDisposition.NoReceiverEmitted));
            return;
        }

        var suffixes = new List<string>();
        if (property.Accessors.OfType<GetAccessorDecl>().Any())
            TakeSuffix($"{property.Name}_get", suffixes, emittedSuffixes);
        if (property.Accessors.OfType<SetAccessorDecl>().Any())
            TakeSuffix($"{property.Name}_set", suffixes, emittedSuffixes);

        entries.Add(new VtableFillEntry(slot.Kind, property, property.Name,
            suffixes.Count > 0 ? VtableFillDisposition.Filled : VtableFillDisposition.CollapsedOntoOverload,
            slot.Verdict, suffixes));
    }

    private static void AddSubscript(
        VtableSlot slot,
        IReadOnlySet<int> skippedSubscriptIndices,
        List<VtableFillEntry> entries,
        HashSet<string> emittedSuffixes)
    {
        if (slot.Verdict == SlotVerdict.ExcludedStatic)
            return;

        var subscript = slot.AsSubscript!;
        var displayName = $"subscript_{slot.SlotIndex}";
        if (!slot.Included)
        {
            entries.Add(Unfilled(slot, displayName, VtableFillDisposition.NoVtableSlot));
            return;
        }
        if (skippedSubscriptIndices.Contains(slot.SlotIndex))
        {
            entries.Add(Unfilled(slot, displayName, VtableFillDisposition.NoReceiverEmitted));
            return;
        }

        var suffixes = new List<string>();
        if (subscript.HasGetter)
            TakeSuffix($"subscript_{slot.SlotIndex}_get", suffixes, emittedSuffixes);
        if (subscript.HasSetter)
            TakeSuffix($"subscript_{slot.SlotIndex}_set", suffixes, emittedSuffixes);

        entries.Add(new VtableFillEntry(slot.Kind, subscript, displayName,
            suffixes.Count > 0 ? VtableFillDisposition.Filled : VtableFillDisposition.CollapsedOntoOverload,
            slot.Verdict, suffixes));
    }

    private static void AddMethod(
        VtableSlot slot,
        ProtocolDecl protocol,
        ITypeDatabase typeDatabase,
        IReadOnlySet<string> skippedMethodKeys,
        List<VtableFillEntry> entries,
        HashSet<string> emittedSuffixes,
        HashSet<string> emittedRawKeys,
        HashSet<string> emittedProjectedKeys)
    {
        // Constructors, statics and @objc-optional requirements consume no slot and impose no
        // reverse-dispatch obligation. A raw-key duplicate is the SAME obligation as the slot it
        // collapsed onto — counting it twice would inflate the denominator.
        if (slot.Verdict is SlotVerdict.ExcludedConstructor
            or SlotVerdict.ExcludedStatic
            or SlotVerdict.ExcludedObjCOptional
            or SlotVerdict.DuplicateOverload)
            return;

        var method = slot.AsMethod!;
        if (!slot.Included)
        {
            entries.Add(Unfilled(slot, method.Name, VtableFillDisposition.NoVtableSlot));
            return;
        }

        var rawKey = ProtocolMethodDisambiguator.EffectiveRawKey(method, protocol, typeDatabase);
        if (skippedMethodKeys.Contains(rawKey))
        {
            entries.Add(Unfilled(slot, method.Name, VtableFillDisposition.NoReceiverEmitted));
            return;
        }
        // Mirrors the interface's one-method-per-raw-key invariant and the receiver loop: an overload
        // pair that collapses to one raw key but projects to distinct C# methods wires only the
        // surviving (first) overload's trampoline.
        if (!emittedRawKeys.Add(rawKey))
        {
            entries.Add(Unfilled(slot, method.Name, VtableFillDisposition.CollapsedOntoOverload));
            return;
        }
        var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(
            method, protocol, typeDatabase, propertyNames: null);
        if (!emittedProjectedKeys.Add(projectedKey))
        {
            entries.Add(Unfilled(slot, method.Name, VtableFillDisposition.CollapsedOntoOverload));
            return;
        }

        var suffixes = new List<string>();
        TakeSuffix($"{method.Name}_{slot.SlotIndex}", suffixes, emittedSuffixes);
        entries.Add(new VtableFillEntry(slot.Kind, method, method.Name,
            suffixes.Count > 0 ? VtableFillDisposition.Filled : VtableFillDisposition.CollapsedOntoOverload,
            slot.Verdict, suffixes));
    }

    private static void TakeSuffix(string suffix, List<string> into, HashSet<string> emittedSuffixes)
    {
        if (emittedSuffixes.Add(suffix))
            into.Add(suffix);
    }

    private static VtableFillEntry Unfilled(VtableSlot slot, string displayName, VtableFillDisposition disposition) =>
        new(slot.Kind, slot.Member, displayName, disposition, slot.Verdict, Array.Empty<string>());
}
