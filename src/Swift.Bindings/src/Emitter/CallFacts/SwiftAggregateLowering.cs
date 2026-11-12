// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>The machine type of one stored scalar, or of one lowered piece.</summary>
public enum SwiftScalarKind
{
    /// <summary><c>Bool</c>: a one-byte integer whose IR type is <c>i1</c>.</summary>
    Bool,

    Int8,
    Int16,
    Int32,
    Int64,

    /// <summary>A pointer: a class reference, a raw or typed pointer, a collection's buffer.</summary>
    Pointer,

    Float,
    Double,

    /// <summary>
    /// Bytes Swift stores as untyped integer data: an enum's tag or payload, an <c>Optional</c>'s
    /// payload. They merge with neighbouring integer data and lower to integers.
    /// </summary>
    Opaque,
}

/// <summary>One stored scalar of a value, at its byte offset from the value's start.</summary>
public readonly record struct SwiftLayoutLeaf(int Offset, int Size, SwiftScalarKind Kind)
{
    public int End => Offset + Size;

    public override string ToString() => $"{Kind}@{Offset}:{Size}";
}

/// <summary>
/// A value's inline layout as Swift stores it: its size (Swift's <c>MemoryLayout.size</c>, not the
/// stride), its alignment, and every stored scalar at its real offset. Nested aggregates are
/// flattened with their own alignment applied, so a field's offset is the one swiftc gives it.
/// </summary>
public sealed record SwiftLeafLayout(int Size, int Alignment, ImmutableArray<SwiftLayoutLeaf> Leaves)
{
    /// <summary>A value with no storage: an empty struct, an empty tuple, a single-case enum.</summary>
    public static readonly SwiftLeafLayout Empty = new(0, 1, ImmutableArray<SwiftLayoutLeaf>.Empty);

    /// <summary>A single scalar, aligned to its size.</summary>
    public static SwiftLeafLayout Scalar(SwiftScalarKind kind) => Scalar(kind, SwiftAggregateLowering.SizeOf(kind));

    /// <summary>A single scalar of <paramref name="size"/> bytes, aligned to its size.</summary>
    public static SwiftLeafLayout Scalar(SwiftScalarKind kind, int size) =>
        new(size, size, ImmutableArray.Create(new SwiftLayoutLeaf(0, size, kind)));

    /// <summary>Untyped bytes of <paramref name="size"/>, aligned to <paramref name="alignment"/>.</summary>
    public static SwiftLeafLayout OpaqueBytes(int size, int alignment) =>
        size == 0 ? Empty with { Alignment = alignment } : new(size, alignment, ImmutableArray.Create(new SwiftLayoutLeaf(0, size, SwiftScalarKind.Opaque)));

    /// <summary>
    /// The layout of fields stored one after another the way Swift lays out a struct or a tuple:
    /// each field starts at the next offset its own alignment allows, and the size is the end of the
    /// last field (no tail padding).
    /// </summary>
    public static SwiftLeafLayout Sequence(IEnumerable<SwiftLeafLayout> fields)
    {
        var leaves = ImmutableArray.CreateBuilder<SwiftLayoutLeaf>();
        int cursor = 0, alignment = 1;
        foreach (var field in fields)
        {
            cursor = AlignUp(cursor, field.Alignment);
            foreach (var leaf in field.Leaves)
                leaves.Add(leaf with { Offset = cursor + leaf.Offset });
            cursor += field.Size;
            alignment = Math.Max(alignment, field.Alignment);
        }
        return new SwiftLeafLayout(cursor, alignment, leaves.ToImmutable());
    }

    /// <summary>The same layout with every leaf turned into untyped bytes (an Optional's payload).</summary>
    public SwiftLeafLayout AsOpaque() => this with
    {
        Leaves = Leaves.Select(l => l with { Kind = SwiftScalarKind.Opaque }).ToImmutableArray(),
    };

    internal static int AlignUp(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    public override string ToString() => $"size {Size} align {Alignment} [{string.Join(", ", Leaves)}]";
}

/// <summary>One register-sized piece of a value Swift passes directly: its IR type and the bytes it carries.</summary>
public readonly record struct SwiftLoweredPiece(int Offset, int Size, SwiftScalarKind Kind)
{
    /// <summary>The LLVM IR type swiftc gives the piece.</summary>
    public string IrType => SwiftAggregateLowering.IrTypeOf(Kind, Size);

    public bool IsFloatingPoint => Kind is SwiftScalarKind.Float or SwiftScalarKind.Double;

    public override string ToString() => $"{IrType}@{Offset}";
}

/// <summary>
/// The swiftcc lowering of a loadable value into register pieces (the function L), the same rules
/// clang's Swift aggregate lowering applies for swiftc on both 64-bit Apple targets, arm64 and
/// x86_64:
/// <list type="bullet">
/// <item>Each stored scalar keeps its offset and type. A typed scalar not naturally aligned becomes untyped bytes.</item>
/// <item>Integer, pointer and untyped data sharing an 8-byte chunk merge into untyped bytes, which
/// then split into the smallest aligned power-of-two integer covering what lies in each chunk.</item>
/// <item>Floating-point data never merges.</item>
/// <item>More than four register-sized pieces means the value goes through an address.</item>
/// </list>
/// The swiftc lowering oracle pins it against the IR swiftc emits for both architectures.
/// </summary>
public static class SwiftAggregateLowering
{
    /// <summary>The chunk integer data merges within: one pointer.</summary>
    public const int ChunkSize = 8;

    /// <summary>Swift passes a value directly in at most this many registers.</summary>
    public const int MaxDirectPieces = 4;

    public static int SizeOf(SwiftScalarKind kind) => kind switch
    {
        SwiftScalarKind.Bool or SwiftScalarKind.Int8 => 1,
        SwiftScalarKind.Int16 => 2,
        SwiftScalarKind.Int32 or SwiftScalarKind.Float => 4,
        SwiftScalarKind.Int64 or SwiftScalarKind.Pointer or SwiftScalarKind.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "untyped bytes have no fixed size"),
    };

    internal static string IrTypeOf(SwiftScalarKind kind, int size) => kind switch
    {
        SwiftScalarKind.Bool => "i1",
        SwiftScalarKind.Pointer => "ptr",
        SwiftScalarKind.Float => "float",
        SwiftScalarKind.Double => "double",
        _ => $"i{size * 8}",
    };

    /// <summary>Lowers a value's layout into its register pieces, in order.</summary>
    /// <exception cref="ArgumentException">Two leaves overlap.</exception>
    public static ImmutableArray<SwiftLoweredPiece> Lower(SwiftLeafLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        // Typed entries keep their type (null marks untyped bytes); a typed scalar off its natural
        // alignment is untyped.
        var entries = new List<(int Begin, int End, SwiftScalarKind? Kind)>();
        foreach (var leaf in layout.Leaves.Where(l => l.Size > 0).OrderBy(l => l.Offset))
        {
            if (entries.Count > 0 && entries[^1].End > leaf.Offset)
                throw new ArgumentException($"leaves overlap at offset {leaf.Offset}: {layout}", nameof(layout));
            SwiftScalarKind? kind = leaf.Kind == SwiftScalarKind.Opaque || leaf.Offset % SizeOf(leaf.Kind) != 0 ? null : leaf.Kind;
            entries.Add((leaf.Offset, leaf.End, kind));
        }
        if (entries.Count == 0)
            return ImmutableArray<SwiftLoweredPiece>.Empty;

        // First pass: two neighbours in one chunk that are both mergeable become untyped, the first
        // stretched to meet the second.
        bool anyOpaque = entries[0].Kind is null;
        for (int i = 1; i < entries.Count; i++)
        {
            var (first, second) = (entries[i - 1], entries[i]);
            if (ChunkOf(first.End - 1) == ChunkOf(second.Begin) && Mergeable(first.Kind) && Mergeable(second.Kind))
            {
                entries[i - 1] = (first.Begin, second.Begin, null);
                entries[i] = (second.Begin, second.End, null);
                anyOpaque = true;
            }
            else if (second.Kind is null)
            {
                anyOpaque = true;
            }
        }

        var pieces = ImmutableArray.CreateBuilder<SwiftLoweredPiece>();
        if (!anyOpaque)
        {
            foreach (var e in entries)
                pieces.Add(new SwiftLoweredPiece(e.Begin, e.End - e.Begin, e.Kind!.Value));
            return pieces.ToImmutable();
        }

        // Second pass: each maximal run of contiguous untyped bytes becomes one integer per chunk it
        // touches, the smallest aligned power of two covering the run's bytes in that chunk.
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind is { } kind)
            {
                pieces.Add(new SwiftLoweredPiece(entries[i].Begin, entries[i].End - entries[i].Begin, kind));
                continue;
            }
            int begin = entries[i].Begin, end = entries[i].End;
            while (i + 1 < entries.Count && entries[i + 1].Kind is null && entries[i + 1].Begin == end)
                end = entries[++i].End;

            do
            {
                int chunkEnd = ChunkOf(begin) * ChunkSize + ChunkSize;
                int localEnd = Math.Min(end, chunkEnd);
                int unit = 1, unitBegin;
                while (true)
                {
                    unitBegin = begin / unit * unit;
                    if (unitBegin + unit >= localEnd)
                        break;
                    unit *= 2;
                }
                pieces.Add(new SwiftLoweredPiece(unitBegin, unit, IntegerKindOf(unit)));
                begin = localEnd;
            }
            while (begin != end);
        }
        return pieces.ToImmutable();
    }

    /// <summary>
    /// Whether Swift passes (or returns) a value with these pieces through an address: when they
    /// occupy more than <see cref="MaxDirectPieces"/> registers, an integer piece wider than a pointer
    /// counting once per pointer it spans.
    /// </summary>
    public static bool PassesIndirectly(ImmutableArray<SwiftLoweredPiece> pieces) =>
        pieces.Sum(p => p.IsFloatingPoint ? 1 : (p.Size + ChunkSize - 1) / ChunkSize) > MaxDirectPieces;

    private static int ChunkOf(int offset) => offset / ChunkSize;

    private static bool Mergeable(SwiftScalarKind? kind) => kind is not (SwiftScalarKind.Float or SwiftScalarKind.Double);

    private static SwiftScalarKind IntegerKindOf(int size) => size switch
    {
        1 => SwiftScalarKind.Int8,
        2 => SwiftScalarKind.Int16,
        4 => SwiftScalarKind.Int32,
        8 => SwiftScalarKind.Int64,
        _ => throw new InvalidOperationException($"an integer unit of {size} bytes is not a legal piece"),
    };
}
