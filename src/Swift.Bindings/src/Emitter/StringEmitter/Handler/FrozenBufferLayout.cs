// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// The members of a frozen struct's nested <c>Buffer</c>, laid out where Swift stores the value's
/// bytes. The Buffer is the value itself when it crosses by value (an argument, a return, an
/// <c>inout</c>), so .NET lowers it from these members: each one sits at its Swift offset, floating
/// point stays floating point, and integer data is cut into naturally aligned integers. The Buffer's
/// size is then Swift's stride, never more.
/// </summary>
internal static class FrozenBufferLayout
{
    /// <summary>One emitted member: <see cref="SwiftScalarKind.Float"/>, <see cref="SwiftScalarKind.Double"/>, or integer bytes.</summary>
    internal readonly record struct Slot(int Offset, int Size, SwiftScalarKind Kind)
    {
        public string CSharpType => Kind switch
        {
            SwiftScalarKind.Float => "float",
            SwiftScalarKind.Double => "double",
            _ => Size switch
            {
                1 => "byte",
                2 => "ushort",
                4 => "uint",
                _ => "global::System.IntPtr",
            },
        };
    }

    /// <summary>A stored field's emitted members, in order.</summary>
    internal sealed record FieldSlots(PropertyDecl Property, ImmutableArray<Slot> Slots);

    /// <summary>
    /// The Buffer of a struct whose every stored field Swift's layout is known for. When
    /// <see cref="Mismatch"/> is set, no member list reproduces Swift's layout and the struct must not
    /// be emitted.
    /// </summary>
    internal sealed record Plan(ImmutableArray<FieldSlots> Fields, int SwiftSize, string? Mismatch);

    /// <summary>
    /// Lays out <paramref name="structDecl"/>'s Buffer from Swift's layout of each stored field, or
    /// returns null when a field's layout is not known (a generic parameter, a resilient type).
    /// </summary>
    internal static Plan? TryPlan(StructDecl structDecl, ITypeDatabase typeDatabase)
    {
        var resolver = SwiftLayoutResolver.ForModuleOf(structDecl, typeDatabase);
        var fields = new List<(PropertyDecl Property, SwiftLeafLayout Layout)>();
        foreach (var property in structDecl.Properties)
        {
            // A static stored property lives in the type's metadata, not in the value.
            if (!property.HasStorage || property.IsStatic)
                continue;
            var layout = resolver.Resolve(property.SwiftTypeSpec).Layout;
            if (layout is null)
                return null;
            fields.Add((property, layout));
        }
        return PlanFor(fields);
    }

    /// <summary>The Buffer for fields with these Swift layouts.</summary>
    internal static Plan PlanFor(IReadOnlyList<(PropertyDecl Property, SwiftLeafLayout Layout)> fields)
    {
        var swift = SwiftLeafLayout.Sequence(fields.Select(f => f.Layout));
        var planned = ImmutableArray.CreateBuilder<FieldSlots>(fields.Count);
        int swiftCursor = 0;
        int managedCursor = 0;
        int managedAlignment = 1;
        string? mismatch = null;
        foreach (var (property, layout) in fields)
        {
            int start = SwiftLeafLayout.AlignUp(swiftCursor, layout.Alignment);
            swiftCursor = start + layout.Size;
            var slots = ImmutableArray.CreateBuilder<Slot>();
            foreach (var slot in SlotsOf(layout, start, layout.Alignment))
            {
                // A sequential struct starts each member at the next offset its own alignment allows.
                // Padding Swift leaves ahead of a member that alignment does not reach is held by
                // padding bytes; the lowering check below refuses them where they would take a register.
                int placed = SwiftLeafLayout.AlignUp(managedCursor, slot.Size);
                if (placed < slot.Offset)
                {
                    for (; managedCursor < slot.Offset; managedCursor++)
                        slots.Add(new Slot(managedCursor, 1, SwiftScalarKind.Opaque));
                    placed = slot.Offset;
                }
                if (placed != slot.Offset)
                {
                    mismatch ??= $"stored property '{property.Name}' has bytes at offset {slot.Offset} that a sequential Buffer can only place at {placed}";
                    break;
                }
                slots.Add(slot);
                managedCursor = placed + slot.Size;
                managedAlignment = Math.Max(managedAlignment, slot.Size);
            }
            planned.Add(new FieldSlots(property, slots.ToImmutable()));
        }

        if (mismatch is null)
        {
            var emitted = new SwiftLeafLayout(swift.Size, swift.Alignment,
                planned.SelectMany(f => f.Slots).Select(s => new SwiftLayoutLeaf(s.Offset, s.Size, s.Kind)).ToImmutableArray());
            var swiftPieces = SwiftAggregateLowering.Lower(swift);
            var emittedPieces = SwiftAggregateLowering.Lower(emitted);
            // A value both sides pass through an address crosses as its bytes, which already match.
            bool bothIndirect = SwiftAggregateLowering.PassesIndirectly(swiftPieces) && SwiftAggregateLowering.PassesIndirectly(emittedPieces);
            if (!bothIndirect && !SamePieces(swiftPieces, emittedPieces))
                mismatch = $"the Buffer's members would lower to registers other than the Swift value's ({swift})";
            else if (SwiftLeafLayout.AlignUp(managedCursor, managedAlignment) > SwiftLeafLayout.AlignUp(swift.Size, swift.Alignment))
                mismatch = $"the Buffer would be wider than the Swift value's stride ({swift})";
        }
        return new Plan(planned.ToImmutable(), swift.Size, mismatch);
    }

    /// <summary>
    /// The members covering one field that starts at <paramref name="start"/>: each floating-point
    /// scalar as itself, and each run of integer data as naturally aligned integers no wider than the
    /// field's alignment, so no member asks for more alignment than Swift gives the field. Bytes no
    /// scalar stores are padding and get no member here.
    /// </summary>
    private static ImmutableArray<Slot> SlotsOf(SwiftLeafLayout layout, int start, int maxUnit)
    {
        var slots = ImmutableArray.CreateBuilder<Slot>();
        var leaves = layout.Leaves.Where(l => l.Size > 0).OrderBy(l => l.Offset).ToList();
        int i = 0;
        while (i < leaves.Count)
        {
            var leaf = leaves[i];
            if (leaf.Kind is SwiftScalarKind.Float or SwiftScalarKind.Double)
            {
                slots.Add(new Slot(start + leaf.Offset, leaf.Size, leaf.Kind));
                i++;
                continue;
            }
            // A run of integer data: leaves that touch, none of them floating point.
            int runBegin = start + leaf.Offset, runEnd = start + leaf.End;
            i++;
            while (i < leaves.Count && leaves[i].Kind is not (SwiftScalarKind.Float or SwiftScalarKind.Double)
                   && start + leaves[i].Offset == runEnd)
                runEnd = start + leaves[i++].End;
            for (int at = runBegin; at < runEnd;)
            {
                int unit = Math.Min(8, maxUnit);
                while (unit > 1 && (at % unit != 0 || at + unit > runEnd))
                    unit /= 2;
                slots.Add(new Slot(at, unit, SwiftScalarKind.Opaque));
                at += unit;
            }
        }
        return slots.ToImmutable();
    }

    /// <summary>
    /// Whether two lowerings use the same registers: the same pieces in order, a floating-point piece
    /// at the same offset and width, and an integer piece in the same 8-byte chunk. An integer that
    /// also covers padding is wider than Swift's, but it fills the same register, and the padding it
    /// carries is never read.
    /// </summary>
    private static bool SamePieces(ImmutableArray<SwiftLoweredPiece> swift, ImmutableArray<SwiftLoweredPiece> emitted) =>
        swift.Length == emitted.Length
        && swift.Zip(emitted).All(p => p.First.IsFloatingPoint == p.Second.IsFloatingPoint
                                       && (p.First.IsFloatingPoint
                                           ? p.First.Offset == p.Second.Offset && p.First.Size == p.Second.Size
                                           : p.First.Offset / SwiftAggregateLowering.ChunkSize == p.Second.Offset / SwiftAggregateLowering.ChunkSize));
}
