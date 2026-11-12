// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;

namespace SwiftBindings.TransitionGate;

/// <summary>Where CoreCLR puts a managed <c>SwiftSelf&lt;T&gt;</c>.</summary>
public enum LegacySelfPlacement
{
    /// <summary>The layout could not be read with certainty; nothing is claimed.</summary>
    Unknown,

    /// <summary>Lowered: T's pieces in ordinary argument registers, nothing in the self register.</summary>
    Lowered,

    /// <summary>By address: a pointer to the value in the self register (x20 / r13).</summary>
    ByAddressInSelfRegister,
}

/// <summary>One lowered piece: its native kind and byte offset in T.</summary>
public sealed record LegacyLoweredPiece(string Kind, int Offset);

/// <summary>The physical outcome of a <c>SwiftSelf&lt;T&gt;</c> and why.</summary>
public sealed record LegacySelfPhysical(LegacySelfPlacement Placement, IReadOnlyList<LegacyLoweredPiece> Pieces, string Detail)
{
    public string Describe() => Placement switch
    {
        LegacySelfPlacement.Lowered => $"lowered to {Pieces.Count} piece(s): {string.Join(", ", Pieces.Select(p => $"{p.Kind}@{p.Offset}"))}",
        LegacySelfPlacement.ByAddressInSelfRegister => $"by address in the self register ({Detail})",
        _ => $"placement unknown ({Detail})",
    };
}

/// <summary>
/// Models what CoreCLR does with a managed <c>SwiftSelf&lt;T&gt;</c>: it computes T's Swift physical
/// lowering (<c>GetNativeSwiftPhysicalLowering</c>) and passes T lowered when that lowering has
/// elements and is not by reference, otherwise by address in the self register. The generator never
/// emits <c>SwiftSelf&lt;T&gt;</c>; this exists so the gate and the consumer diagnostic can judge an
/// existing assembly, where only the by-address form reaches the self register the .NET 11 arm64
/// interpreter corrupts.
/// <para>
/// The lowering, as the runtime computes it:
/// </para>
/// <list type="number">
/// <item>Each byte of T is tagged by the field covering it: <c>float</c> Float, <c>double</c> Double,
/// 8-byte integers, <c>nint</c>/<c>nuint</c> and pointers Int64, every other primitive Opaque; nested
/// structs recurse. A field that is misaligned for its kind, or overlaps a byte of another kind, is
/// Opaque.</item>
/// <item>Consecutive bytes form intervals; a Float starts a new one at each 4-byte boundary, a Double
/// or Int64 at each 8-byte boundary, and any change of kind starts one. Empty bytes form none.</item>
/// <item>An Opaque interval merges into the previous Opaque one when the previous ends in the same
/// pointer-sized block this one starts in.</item>
/// <item>Each Opaque interval splits into integers: 8 bytes while more than 4 remain at an 8-aligned
/// offset, else 4 while more than 2 remain at a 4-aligned one, else 2 while more than 1 remain at a
/// 2-aligned one, else 1.</item>
/// <item>More than four pieces is by reference.</item>
/// </list>
/// <para>
/// Fail-closed: an explicit, auto or packed layout, an explicit size, an inline array, a reference or
/// generic field, or a type that does not resolve is <see cref="LegacySelfPlacement.Unknown"/>, never
/// a guess.
/// </para>
/// </summary>
public static class LegacySwiftSelfPhysical
{
    /// <summary>The runtime's limit on lowered elements.</summary>
    public const int MaxLoweredElements = 4;

    private const int PointerSize = 8;

    private enum ByteKind : byte { Empty, Opaque, Int64, Float, Double }

    /// <summary>
    /// The placement of a <c>SwiftSelf&lt;T&gt;</c> carrier, given the carrier itself
    /// (<c>System.Runtime.InteropServices.Swift.SwiftSelf`1</c>) or its type argument.
    /// </summary>
    public static LegacySelfPhysical Classify(SigType type, AssemblyResolver resolver)
    {
        var unwrapped = type.Unwrapped();
        if (unwrapped is SigType.Named carrier && carrier.Is(CarrierClassifier.SwiftInteropNamespace, "SwiftSelf`1"))
        {
            if (carrier.GenericArguments.Length != 1)
                return Unknown("SwiftSelf<T> without exactly one type argument");
            unwrapped = carrier.GenericArguments[0].Unwrapped();
        }

        if (unwrapped is not SigType.Named named || !named.IsValueType)
            return Unknown($"T ({TypeNames.Display(unwrapped)}) is not a value type");

        var layout = new Layout(resolver);
        if (!layout.TryLayOut(named, out var size, out _, out var problem))
            return Unknown(problem!);
        var bytes = new ByteKind[size];
        if (!layout.TryTag(named, 0, bytes, out problem))
            return Unknown(problem!);
        return Lower(bytes);
    }

    private static LegacySelfPhysical Unknown(string why) =>
        new(LegacySelfPlacement.Unknown, Array.Empty<LegacyLoweredPiece>(), why);

    private static LegacySelfPhysical Lower(ByteKind[] bytes)
    {
        var intervals = new List<(int Offset, int Size, ByteKind Kind)>();
        for (int i = 0; i < bytes.Length; i++)
        {
            var kind = bytes[i];
            if (kind == ByteKind.Empty)
                continue;
            bool start = i == 0
                || (i % 4 == 0 && kind == ByteKind.Float)
                || (i % 8 == 0 && kind is ByteKind.Double or ByteKind.Int64)
                || kind != bytes[i - 1];
            if (start)
                intervals.Add((i, 1, kind));
            else
                intervals[^1] = (intervals[^1].Offset, intervals[^1].Size + 1, kind);
        }

        var merged = new List<(int Offset, int Size, ByteKind Kind)>();
        for (int i = 0; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            if (i != 0 && interval.Kind == ByteKind.Opaque)
            {
                var previous = intervals[i - 1];
                if (previous.Kind == ByteKind.Opaque
                    && (previous.Offset + previous.Size) / PointerSize == interval.Offset / PointerSize)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Offset, interval.Offset + interval.Size - last.Offset, ByteKind.Opaque);
                    continue;
                }
            }
            merged.Add(interval);
        }

        var pieces = new List<LegacyLoweredPiece>();
        foreach (var interval in merged)
        {
            switch (interval.Kind)
            {
                case ByteKind.Int64:
                    pieces.Add(new("int64", interval.Offset));
                    break;
                case ByteKind.Float:
                    pieces.Add(new("float", interval.Offset));
                    break;
                case ByteKind.Double:
                    pieces.Add(new("double", interval.Offset));
                    break;
                case ByteKind.Opaque:
                    int offset = interval.Offset;
                    int remaining = interval.Size;
                    while (remaining > 0)
                    {
                        if (remaining > 4 && offset % 8 == 0) { pieces.Add(new("int64", offset)); offset += 8; remaining -= 8; }
                        else if (remaining > 2 && offset % 4 == 0) { pieces.Add(new("int32", offset)); offset += 4; remaining -= 4; }
                        else if (remaining > 1 && offset % 2 == 0) { pieces.Add(new("int16", offset)); offset += 2; remaining -= 2; }
                        else { pieces.Add(new("int8", offset)); offset += 1; remaining -= 1; }
                    }
                    break;
            }
        }

        if (pieces.Count > MaxLoweredElements)
            return new(LegacySelfPlacement.ByAddressInSelfRegister, Array.Empty<LegacyLoweredPiece>(),
                $"{pieces.Count} lowered pieces, over the runtime's limit of {MaxLoweredElements}");
        if (pieces.Count == 0)
            return new(LegacySelfPlacement.ByAddressInSelfRegister, Array.Empty<LegacyLoweredPiece>(),
                "the lowering has no elements");
        return new(LegacySelfPlacement.Lowered, pieces, $"{pieces.Count} piece(s)");
    }

    private static int Alignment(ByteKind kind) => kind switch
    {
        ByteKind.Int64 or ByteKind.Double => 8,
        ByteKind.Float => 4,
        _ => 1,
    };

    /// <summary>Tags <paramref name="size"/> bytes at <paramref name="start"/> with <paramref name="kind"/>, the runtime's way.</summary>
    private static void SetRange(ByteKind[] bytes, int start, int size, ByteKind kind)
    {
        bool opaque = start % Alignment(kind) != 0;
        for (int i = 0; i < size; i++)
        {
            var current = bytes[start + i];
            if (current != ByteKind.Empty && current != kind)
            {
                opaque = true;
                int align = Alignment(current);
                int end = start + size;
                start = start / align * align;
                size = (end + align - 1) / align * align - start;
                break;
            }
        }
        if (opaque)
            kind = ByteKind.Opaque;
        for (int i = 0; i < size && start + i < bytes.Length; i++)
            bytes[start + i] = kind;
    }

    /// <summary>Sequential, naturally aligned layout of blittable value types read from metadata.</summary>
    private sealed class Layout
    {
        private readonly AssemblyResolver _resolver;
        private int _depth;

        public Layout(AssemblyResolver resolver) => _resolver = resolver;

        public bool TryLayOut(SigType type, out int size, out int align, out string? problem)
        {
            size = align = 0;
            switch (type.Unwrapped())
            {
                case SigType.Primitive p:
                    return TryPrimitive(p.Code, out size, out align, out _, out problem);
                case SigType.Pointer or SigType.FunctionPointer:
                    size = align = PointerSize;
                    problem = null;
                    return true;
                case SigType.Named n when n.IsValueType:
                    return TryStruct(n, out size, out align, out problem, tag: null, offset: 0);
                default:
                    problem = $"field type {TypeNames.Display(type)} is not a blittable value";
                    return false;
            }
        }

        public bool TryTag(SigType type, int offset, ByteKind[] bytes, out string? problem)
        {
            switch (type.Unwrapped())
            {
                case SigType.Primitive p:
                    if (!TryPrimitive(p.Code, out var size, out _, out var kind, out problem))
                        return false;
                    SetRange(bytes, offset, size, kind);
                    return true;
                case SigType.Pointer or SigType.FunctionPointer:
                    SetRange(bytes, offset, PointerSize, ByteKind.Int64);
                    problem = null;
                    return true;
                case SigType.Named n when n.IsValueType:
                    return TryStruct(n, out _, out _, out problem, tag: bytes, offset: offset);
                default:
                    problem = $"field type {TypeNames.Display(type)} is not a blittable value";
                    return false;
            }
        }

        private static bool TryPrimitive(PrimitiveTypeCode code, out int size, out int align, out ByteKind kind, out string? problem)
        {
            problem = null;
            (size, kind) = code switch
            {
                PrimitiveTypeCode.Boolean or PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte => (1, ByteKind.Opaque),
                PrimitiveTypeCode.Char or PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 => (2, ByteKind.Opaque),
                PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => (4, ByteKind.Opaque),
                PrimitiveTypeCode.Single => (4, ByteKind.Float),
                PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => (8, ByteKind.Int64),
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => (PointerSize, ByteKind.Int64),
                PrimitiveTypeCode.Double => (8, ByteKind.Double),
                _ => (0, ByteKind.Empty),
            };
            align = size;
            if (size != 0)
                return true;
            problem = $"field type {code} is not a blittable primitive";
            return false;
        }

        private bool TryStruct(SigType.Named named, out int size, out int align, out string? problem, ByteKind[]? tag, int offset)
        {
            size = align = 0;
            if (!named.GenericArguments.IsDefaultOrEmpty)
            {
                problem = $"{named.FullName} is a generic instantiation";
                return false;
            }
            if (_depth > 16)
            {
                problem = $"{named.FullName} nests too deeply";
                return false;
            }
            var resolved = _resolver.Resolve(named);
            if (resolved is not { } r)
            {
                problem = $"{named.FullName} could not be resolved";
                return false;
            }
            var (reader, handle) = r;
            var def = reader.GetTypeDefinition(handle);

            if (IsEnum(reader, def))
            {
                var underlying = InstanceFields(reader, def).Select(f => f.Type).ToList();
                if (underlying.Count != 1)
                {
                    problem = $"enum {named.FullName} has no readable underlying integer";
                    return false;
                }
                _depth++;
                try
                {
                    return tag is null
                        ? TryLayOut(underlying[0], out size, out align, out problem)
                        : TryTag(underlying[0], offset, tag, out problem);
                }
                finally { _depth--; }
            }

            var layoutKind = def.Attributes & TypeAttributes.LayoutMask;
            if (layoutKind != TypeAttributes.SequentialLayout)
            {
                problem = $"{named.FullName} does not have sequential layout";
                return false;
            }
            var declared = def.GetLayout();
            // C# gives an empty struct an explicit size of one byte; that is the empty layout, not a choice.
            bool emptyStruct = !InstanceFields(reader, def).Any() && declared.PackingSize == 0 && declared.Size <= 1;
            if (!emptyStruct && (declared.PackingSize != 0 || declared.Size != 0))
            {
                problem = $"{named.FullName} declares a packing size or an explicit size";
                return false;
            }
            if (HasAttribute(reader, def, "System.Runtime.CompilerServices", "InlineArrayAttribute"))
            {
                problem = $"{named.FullName} is an inline array";
                return false;
            }

            int cursor = 0;
            int structAlign = 1;
            _depth++;
            try
            {
                foreach (var (_, fieldType) in InstanceFields(reader, def))
                {
                    if (!TryLayOut(fieldType, out var fieldSize, out var fieldAlign, out problem))
                        return false;
                    cursor = (cursor + fieldAlign - 1) / fieldAlign * fieldAlign;
                    if (tag is not null && !TryTag(fieldType, offset + cursor, tag, out problem))
                        return false;
                    cursor += fieldSize;
                    structAlign = Math.Max(structAlign, fieldAlign);
                }
            }
            finally { _depth--; }

            // An empty struct still occupies one byte.
            size = Math.Max(1, (cursor + structAlign - 1) / structAlign * structAlign);
            align = structAlign;
            problem = null;
            return true;
        }

        private static IEnumerable<(string Name, SigType Type)> InstanceFields(MetadataReader reader, TypeDefinition def)
        {
            var provider = new SigTypeProvider(reader);
            foreach (var fieldHandle in def.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) != 0)
                    continue;
                yield return (reader.GetString(field.Name), provider.DecodeField(field.Signature));
            }
        }

        private static bool IsEnum(MetadataReader reader, TypeDefinition def)
        {
            if (def.BaseType.IsNil)
                return false;
            var (ns, name) = def.BaseType.Kind switch
            {
                HandleKind.TypeReference => DropScope(TypeNames.OfReference(reader, (TypeReferenceHandle)def.BaseType)),
                HandleKind.TypeDefinition => TypeNames.OfDefinition(reader, (TypeDefinitionHandle)def.BaseType),
                _ => ("", ""),
            };
            return ns == "System" && name == "Enum";
        }

        private static (string, string) DropScope((string Namespace, string Name, string Scope) reference) =>
            (reference.Namespace, reference.Name);

        private static bool HasAttribute(MetadataReader reader, TypeDefinition def, string ns, string name)
        {
            foreach (var handle in def.GetCustomAttributes())
            {
                var ctor = reader.GetCustomAttribute(handle).Constructor;
                EntityHandle owner = ctor.Kind switch
                {
                    HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent,
                    HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
                    _ => default,
                };
                var (ownerNs, ownerName) = owner.Kind switch
                {
                    HandleKind.TypeReference => DropScope(TypeNames.OfReference(reader, (TypeReferenceHandle)owner)),
                    HandleKind.TypeDefinition => TypeNames.OfDefinition(reader, (TypeDefinitionHandle)owner),
                    _ => ("", ""),
                };
                if (ownerNs == ns && ownerName == name)
                    return true;
            }
            return false;
        }
    }
}
