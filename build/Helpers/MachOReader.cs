// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// MachOReader.cs — a tiny, dependency-free reader of a Mach-O's dylib load commands.
//
// The App Store hygiene gate (Build.BindingTests.AppStoreHygiene.cs) asserts that the embedded
// SwiftBindingsRuntime.framework binary carries the install_name
// `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`. That install_name is the dylib's
// LC_ID_DYLIB load command. The gate originally read it by scraping `otool -D` stdout by line
// position — fragile against tool-output format drift and locale. This reader walks the Mach-O
// load commands directly and decodes the names from structured bytes, so the assertions never
// trust column positions in another tool's text output. The Mac legs of the same gate also read
// the dependency commands (what `otool -L` lists) to check that every framework an embedded
// binary needs is one the app bundle carries or the OS provides.
//
// Scope: enough Mach-O to read the install_name and the dependency names of an Apple framework
// binary. Handles thin 32/64-bit images in either endianness and fat (universal) archives
// (FAT_MAGIC / FAT_MAGIC_64) — the install_name is identical across a fat binary's slices, so the
// first slice is read for it, while dependencies are gathered across every slice. Anything it
// cannot parse returns null, which the caller treats as a hygiene failure (a present-but-unreadable
// framework binary is a defect, not a pass).
//
// PURITY CONTRACT: this file is link-compiled into the unit-test project (Swift.Bindings.Unit.Tests),
// so it must depend only on the BCL — no Nuke, no Serilog. The byte[] overload is the testable core
// (fed hand-built fixtures); the path overload is a thin File.ReadAllBytes wrapper. #nullable enable is
// explicit so the file compiles under that project's Nullable=disable + warnings-as-errors (mirrors
// ArtifactParityGate.cs, the sibling link-compiled build helper).

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Reads the LC_ID_DYLIB install_name from a Mach-O image without shelling out to <c>otool</c>.
/// </summary>
internal static class MachOReader
{
    // Mach-O magic numbers, as the first 4 bytes appear when read little-endian.
    private const uint MH_MAGIC_64 = 0xFEEDFACF; // 64-bit, host-endian (LE on Apple silicon/x86_64)
    private const uint MH_CIGAM_64 = 0xCFFAEDFE; // 64-bit, byte-swapped
    private const uint MH_MAGIC = 0xFEEDFACE;    // 32-bit, host-endian
    private const uint MH_CIGAM = 0xCEFAEDFE;    // 32-bit, byte-swapped

    // Fat (universal) headers are always stored big-endian; these are their values read big-endian.
    private const uint FAT_MAGIC = 0xCAFEBABE;
    private const uint FAT_MAGIC_64 = 0xCAFEBABF;

    private const uint LC_ID_DYLIB = 0x0D;

    // The load commands through which an image names another dylib it needs at load time. All four
    // share the dylib_command layout, so one decoder serves them.
    private const uint LC_LOAD_DYLIB = 0x0C;
    private const uint LC_LOAD_WEAK_DYLIB = 0x80000018;
    private const uint LC_REEXPORT_DYLIB = 0x8000001F;
    private const uint LC_LOAD_UPWARD_DYLIB = 0x80000023;

    /// <summary>Reads the install_name of the Mach-O at <paramref name="path"/>, or null if unreadable.</summary>
    internal static string? ReadInstallName(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return ReadInstallName(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the install_name (LC_ID_DYLIB) from an in-memory Mach-O image. Returns null if the bytes
    /// are not a Mach-O this reader understands or carry no LC_ID_DYLIB.
    /// </summary>
    internal static string? ReadInstallName(byte[] image)
    {
        if (image is null || image.Length < 8) return null;

        // A fat header is big-endian. Peek the first word big-endian to detect it.
        uint fatMagic = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0, 4));
        if (fatMagic is FAT_MAGIC or FAT_MAGIC_64)
            return ReadFromFat(image, fatMagic == FAT_MAGIC_64);

        return ReadFromThin(image, sliceOffset: 0);
    }

    /// <summary>
    /// Reads the dylibs the Mach-O at <paramref name="path"/> names as load-time dependencies
    /// (LC_LOAD_DYLIB, LC_LOAD_WEAK_DYLIB, LC_REEXPORT_DYLIB, LC_LOAD_UPWARD_DYLIB) — the names
    /// <c>otool -L</c> lists — across every slice of a fat image, deduplicated, in first-seen order.
    /// Returns null when the file is missing or is not a Mach-O this reader understands; an image
    /// with no dependency commands yields an empty list.
    /// </summary>
    internal static IReadOnlyList<string>? ReadLinkedDylibs(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return ReadLinkedDylibs(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="ReadLinkedDylibs(string)"/>
    internal static IReadOnlyList<string>? ReadLinkedDylibs(byte[] image)
    {
        if (image is null || image.Length < 8) return null;

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool anySlice = false;

        uint fatMagic = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0, 4));
        if (fatMagic is FAT_MAGIC or FAT_MAGIC_64)
        {
            foreach (var sliceOffset in FatSliceOffsets(image, fatMagic == FAT_MAGIC_64))
            {
                var slice = ReadLinkedDylibsFromThin(image, sliceOffset);
                if (slice is null) continue;
                anySlice = true;
                foreach (var n in slice) if (seen.Add(n)) names.Add(n);
            }
        }
        else
        {
            var slice = ReadLinkedDylibsFromThin(image, sliceOffset: 0);
            if (slice is not null)
            {
                anySlice = true;
                foreach (var n in slice) if (seen.Add(n)) names.Add(n);
            }
        }

        return anySlice ? names : null;
    }

    // Walk a fat archive's arch table and read the install_name from the first slice whose offset is
    // in range. The install_name is the same across slices, so the first readable one suffices.
    private static string? ReadFromFat(byte[] image, bool is64)
    {
        foreach (var sliceOffset in FatSliceOffsets(image, is64))
        {
            var name = ReadFromThin(image, sliceOffset);
            if (name is not null) return name;
        }
        return null;
    }

    // The in-range slice offsets a fat header's arch table names, in table order.
    private static IEnumerable<int> FatSliceOffsets(byte[] image, bool is64)
    {
        // fat_header: magic(4, BE), nfat_arch(4, BE).
        if (image.Length < 8) yield break;
        uint nfat = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(4, 4));

        // fat_arch:    cputype(4) cpusubtype(4) offset(4)  size(4)  align(4)            = 20 bytes
        // fat_arch_64: cputype(4) cpusubtype(4) offset(8)  size(8)  align(4) reserved(4)= 32 bytes
        int entrySize = is64 ? 32 : 20;
        int tableStart = 8;

        for (uint i = 0; i < nfat; i++)
        {
            int entry = tableStart + (int)(i * (uint)entrySize);
            if (entry + entrySize > image.Length) break;

            long sliceOffset;
            if (is64)
                sliceOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(entry + 8, 8));
            else
                sliceOffset = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(entry + 8, 4));

            if (sliceOffset < 0 || sliceOffset >= image.Length) continue;
            yield return (int)sliceOffset;
        }
    }

    // Collect every dependency dylib_command name from a thin Mach-O whose header starts at
    // sliceOffset. Null when the slice is not a readable Mach-O; a malformed command mid-walk ends
    // the walk with what was read so far, since the reader's contract is to never read past the
    // buffer rather than to certify the whole image.
    private static List<string>? ReadLinkedDylibsFromThin(byte[] image, int sliceOffset)
    {
        if (!TryReadHeader(image, sliceOffset, out bool bigEndian, out bool is64, out uint ncmds, out int headerSize))
            return null;

        var names = new List<string>();
        long lc = (long)sliceOffset + headerSize;
        for (uint i = 0; i < ncmds; i++)
        {
            if (lc + 8 > image.Length) break;
            int lcInt = (int)lc;
            uint cmd = ReadU32(image, lcInt, bigEndian);
            uint cmdSize = ReadU32(image, lcInt + 4, bigEndian);
            if (cmdSize < 8 || lc + cmdSize > image.Length) break;

            if (cmd is LC_LOAD_DYLIB or LC_LOAD_WEAK_DYLIB or LC_REEXPORT_DYLIB or LC_LOAD_UPWARD_DYLIB)
            {
                var name = ReadDylibCommandName(image, lc, cmdSize, bigEndian);
                if (name is null) break;
                names.Add(name);
            }

            lc += cmdSize;
        }
        return names;
    }

    // Decode the mach_header(_64) at sliceOffset: endianness, width, load-command count and size.
    private static bool TryReadHeader(byte[] image, int sliceOffset, out bool bigEndian, out bool is64, out uint ncmds, out int headerSize)
    {
        bigEndian = false; is64 = false; ncmds = 0; headerSize = 0;
        if (sliceOffset < 0 || sliceOffset + 4 > image.Length) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sliceOffset, 4));
        switch (magic)
        {
            case MH_MAGIC_64: bigEndian = false; is64 = true; break;
            case MH_CIGAM_64: bigEndian = true; is64 = true; break;
            case MH_MAGIC: bigEndian = false; is64 = false; break;
            case MH_CIGAM: bigEndian = true; is64 = false; break;
            default: return false;
        }

        // mach_header(_64): magic(4) cputype(4) cpusubtype(4) filetype(4) ncmds(4) sizeofcmds(4)
        //                   flags(4) [reserved(4) only on 64-bit]
        headerSize = is64 ? 32 : 28;
        if (sliceOffset + headerSize > image.Length) return false;
        ncmds = ReadU32(image, sliceOffset + 16, bigEndian);
        return true;
    }

    // dylib_command: cmd(4) cmdsize(4) | dylib { name(lc_str:uint32) timestamp(4) current_version(4)
    //   compatibility_version(4) }. name is a union holding a uint32 byte offset from the START of
    //   the load command to the NUL-terminated string.
    private static string? ReadDylibCommandName(byte[] image, long lc, uint cmdSize, bool bigEndian)
    {
        if (lc + 12 > image.Length) return null; // the lc_str name offset is a uint32 at +8
        uint nameOffset = ReadU32(image, (int)lc + 8, bigEndian);
        long strStart = lc + nameOffset;
        long strEndBound = lc + cmdSize;
        if (nameOffset < 8 || strStart >= strEndBound || strStart >= image.Length) return null;

        int end = (int)Math.Min(strEndBound, image.Length);
        int nul = end;
        for (int p = (int)strStart; p < end; p++)
        {
            if (image[p] == 0) { nul = p; break; }
        }
        return Encoding.UTF8.GetString(image, (int)strStart, nul - (int)strStart);
    }

    // Read the install_name from a thin Mach-O whose header starts at sliceOffset.
    private static string? ReadFromThin(byte[] image, int sliceOffset)
    {
        if (!TryReadHeader(image, sliceOffset, out bool bigEndian, out _, out uint ncmds, out int headerSize))
            return null;

        long lc = (long)sliceOffset + headerSize;
        for (uint i = 0; i < ncmds; i++)
        {
            if (lc + 8 > image.Length) return null;
            int lcInt = (int)lc; // safe: the guard above keeps lc < image.Length (≤ int.MaxValue)
            uint cmd = ReadU32(image, lcInt, bigEndian);
            uint cmdSize = ReadU32(image, lcInt + 4, bigEndian);
            // cmdSize is unsigned; do the bounds math in long so a >2 GiB value cannot wrap to a
            // negative int and slip past the check into an out-of-range read. The contract is
            // "anything we cannot parse returns null" — a malformed cmdSize must hit this, not throw.
            if (cmdSize < 8 || lc + cmdSize > image.Length) return null;

            if (cmd == LC_ID_DYLIB)
                return ReadDylibCommandName(image, lc, cmdSize, bigEndian);

            lc += cmdSize;
        }
        return null;
    }

    private static uint ReadU32(byte[] image, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
}
