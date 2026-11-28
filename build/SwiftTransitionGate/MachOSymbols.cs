// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// One architecture slice of a Mach-O image, as the transition evidence reads it: the symbols it
/// defines and exports, and the C string a defined data symbol holds.
/// </summary>
public sealed class MachOSlice
{
    private readonly byte[] _image;
    private readonly int _sliceOffset;
    private readonly ImmutableArray<(ulong Address, ulong Size, uint Offset)> _sections;
    private readonly ImmutableDictionary<string, (byte Section, ulong Value)> _defined;

    internal MachOSlice(string architecture, byte[] image, int sliceOffset,
        ImmutableArray<(ulong, ulong, uint)> sections, ImmutableDictionary<string, (byte, ulong)> defined)
    {
        Architecture = architecture;
        _image = image;
        _sliceOffset = sliceOffset;
        _sections = sections;
        _defined = defined;
        Exports = defined.Keys.ToImmutableSortedSet(StringComparer.Ordinal);
    }

    /// <summary><c>arm64</c>, <c>arm64e</c>, <c>x86_64</c>, or the CPU type in hex for any other.</summary>
    public string Architecture { get; }

    /// <summary>The external symbols the slice defines, without the Mach-O leading underscore.</summary>
    public ImmutableSortedSet<string> Exports { get; }

    /// <summary>
    /// The NUL-terminated string the exported data symbol <paramref name="symbol"/> (no leading
    /// underscore) holds, or null when the slice does not define it or it holds no terminated string
    /// within its section.
    /// </summary>
    public string? CString(string symbol)
    {
        if (!_defined.TryGetValue(symbol, out var definition) || definition.Section == 0 || definition.Section > _sections.Length)
            return null;
        var (address, size, offset) = _sections[definition.Section - 1];
        if (definition.Value < address || definition.Value >= address + size)
            return null;
        var start = (long)_sliceOffset + offset + (long)(definition.Value - address);
        var end = Math.Min((long)_sliceOffset + offset + (long)size, _image.Length);
        for (var i = start; i < end; i++)
            if (_image[i] == 0)
                return Encoding.UTF8.GetString(_image, (int)start, (int)(i - start));
        return null;
    }
}

/// <summary>
/// A dependency-free reader of the symbol tables of a 64-bit Mach-O image, thin or fat: enough to
/// say which symbols each slice exports and what string a data symbol holds. Base class library
/// only, so an MSBuild task can carry it.
/// </summary>
public static class MachOSymbols
{
    private const uint MhMagic64 = 0xFEEDFACF;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint LcSegment64 = 0x19;
    private const uint LcSymtab = 0x2;

    /// <summary>
    /// The slices of <paramref name="image"/>. Throws <see cref="BadImageFormatException"/> when it is
    /// not a 64-bit little-endian Mach-O image or a fat archive of them, or a slice does not read.
    /// </summary>
    public static ImmutableArray<MachOSlice> Read(byte[] image)
    {
        if (image.Length < 8)
            throw new BadImageFormatException("the image is too short to be Mach-O");
        var magic = BinaryPrimitives.ReadUInt32BigEndian(image);
        if (magic is FatMagic or FatMagic64)
        {
            var is64 = magic == FatMagic64;
            var count = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(4));
            var stride = is64 ? 32 : 20;
            if (count == 0 || 8 + (long)count * stride > image.Length)
                throw new BadImageFormatException("the fat header does not read");
            var slices = ImmutableArray.CreateBuilder<MachOSlice>();
            for (var i = 0; i < count; i++)
            {
                var entry = image.AsSpan(8 + i * stride);
                var offset = is64 ? BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                if (offset >= (ulong)image.Length)
                    throw new BadImageFormatException($"fat slice {i} lies outside the image");
                slices.Add(ReadThin(image, (int)offset));
            }
            return slices.ToImmutable();
        }
        return [ReadThin(image, 0)];
    }

    /// <summary>The architecture a Mach-O CPU type and subtype name, as the slices of <see cref="Read"/> are named.</summary>
    public static string ArchitectureName(uint cpuType, uint cpuSubtype) => cpuType switch
    {
        0x0100000C => (cpuSubtype & 0xFF) == 2 ? "arm64e" : "arm64",
        0x01000007 => "x86_64",
        _ => $"0x{cpuType:x}",
    };

    private static MachOSlice ReadThin(byte[] image, int slice)
    {
        Span<byte> span = image;
        if (slice + 32 > image.Length || BinaryPrimitives.ReadUInt32LittleEndian(span[slice..]) != MhMagic64)
            throw new BadImageFormatException("the slice is not a 64-bit little-endian Mach-O image");
        var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(span[(slice + 4)..]);
        var cpuSubtype = BinaryPrimitives.ReadUInt32LittleEndian(span[(slice + 8)..]);
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(span[(slice + 16)..]);
        var architecture = ArchitectureName(cpuType, cpuSubtype);

        var sections = ImmutableArray.CreateBuilder<(ulong, ulong, uint)>();
        (uint Offset, uint Count, uint Strings, uint StringsSize)? symtab = null;
        long at = slice + 32;
        for (var c = 0; c < commands; c++)
        {
            if (at + 8 > image.Length)
                throw new BadImageFormatException("a load command lies outside the image");
            var command = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)at..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 4)..]);
            if (size < 8 || at + size > image.Length)
                throw new BadImageFormatException("a load command does not read");
            if (command == LcSegment64)
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 64)..]);
                if (72 + (long)count * 80 > size)
                    throw new BadImageFormatException("a segment's sections overrun its command");
                for (var s = 0; s < count; s++)
                {
                    var section = (int)(at + 72 + s * 80);
                    sections.Add((
                        BinaryPrimitives.ReadUInt64LittleEndian(span[(section + 32)..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(span[(section + 40)..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(span[(section + 48)..])));
                }
            }
            else if (command == LcSymtab)
            {
                symtab = (BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 8)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 12)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 16)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(at + 20)..]));
            }
            at += size;
        }

        var defined = new Dictionary<string, (byte, ulong)>(StringComparer.Ordinal);
        if (symtab is { } table)
        {
            long symbols = slice + table.Offset, strings = slice + table.Strings;
            if (symbols + (long)table.Count * 16 > image.Length || strings + table.StringsSize > image.Length)
                throw new BadImageFormatException("the symbol table lies outside the image");
            for (var i = 0; i < table.Count; i++)
            {
                var entry = (int)(symbols + i * 16);
                var type = image[entry + 4];
                // External and not private-extern, defined in a section, not a debugging entry: what
                // dynamic lookup can find.
                if ((type & 0xE0) != 0 || (type & 0x10) != 0 || (type & 0x01) == 0 || (type & 0x0E) != 0x0E)
                    continue;
                var name = ReadName(image, strings, table.StringsSize, BinaryPrimitives.ReadUInt32LittleEndian(span[entry..]));
                if (name is null)
                    continue;
                defined[name.StartsWith('_') ? name[1..] : name] = (image[entry + 5], BinaryPrimitives.ReadUInt64LittleEndian(span[(entry + 8)..]));
            }
        }
        return new MachOSlice(architecture, image, slice, sections.ToImmutable(), defined.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static string? ReadName(byte[] image, long strings, uint size, uint index)
    {
        if (index >= size)
            return null;
        var start = strings + index;
        var end = strings + size;
        for (var i = start; i < end; i++)
            if (image[i] == 0)
                return Encoding.UTF8.GetString(image, (int)start, (int)(i - start));
        return null;
    }
}
