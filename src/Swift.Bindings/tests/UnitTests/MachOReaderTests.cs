// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="MachOReader"/> — the dependency-free LC_ID_DYLIB install_name reader that
/// replaced the appstore-hygiene gate's <c>otool -D</c> text scrape (Finding 61). The reader is
/// link-compiled into this project so it can be exercised against hand-built Mach-O fixtures that run
/// in <c>nuke test</c>, independent of a signing host or any real framework binary. Fixtures are
/// constructed byte-by-byte here (no checked-in binary blob) so the structure under test is fully
/// transparent: thin 32/64-bit in both endiannesses, fat (universal) archives, and the malformed
/// inputs the gate must treat as "unreadable" (null), not as a pass.
/// </summary>
public class MachOReaderTests
{
    private const string RuntimeInstallName = "@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime";

    [Fact]
    public void ReadInstallName_ThinLittleEndian64_ReturnsInstallName()
    {
        var image = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_ThinBigEndian64_ReturnsInstallName()
    {
        // A byte-swapped (big-endian) image reads as MH_CIGAM_64; the reader must follow that endianness
        // through the header and load commands.
        var image = BuildThinMachO(RuntimeInstallName, Endian.Big, is64: true);
        Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_ThinLittleEndian32_ReturnsInstallName()
    {
        var image = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: false);
        Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_FatWrappingThinSlices_ReturnsInstallName()
    {
        // The install_name is identical across a fat binary's slices, so any readable slice suffices.
        var arm64 = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        var x86_64 = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        var fat = BuildFat((CpuTypeArm64, arm64), (CpuTypeX86_64, x86_64));
        Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(fat));
    }

    [Fact]
    public void ReadInstallName_Fat64WrappingThinSlices_ReturnsInstallName()
    {
        // FAT_MAGIC_64 uses 32-byte fat_arch_64 entries with 8-byte offsets — a distinct code path
        // from the 32-bit fat header. Exercise it so the reader's fat_arch_64 branch has coverage.
        var arm64 = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        var fat = BuildFat64((CpuTypeArm64, arm64));
        Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(fat));
    }

    [Theory]
    [InlineData("@rpath/Foo.framework/Foo")]
    [InlineData("@executable_path/Frameworks/Bar.framework/Bar")]
    [InlineData("/usr/lib/swift/libswiftCore.dylib")]
    [InlineData("a")]
    public void ReadInstallName_VariousNames_RoundTrip(string installName)
    {
        var image = BuildThinMachO(installName, Endian.Little, is64: true);
        Assert.Equal(installName, MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_FromTempFile_ReturnsInstallName()
    {
        var image = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, image);
            Assert.Equal(RuntimeInstallName, MachOReader.ReadInstallName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadInstallName_NoIdDylibCommand_ReturnsNull()
    {
        // A valid Mach-O whose only load command is not LC_ID_DYLIB carries no install_name.
        var image = BuildThinMachOWithSingleOtherCommand(LcUuid, Endian.Little);
        Assert.Null(MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_NotAMachO_ReturnsNull()
    {
        var garbage = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }; // ELF-ish
        Assert.Null(MachOReader.ReadInstallName(garbage));
    }

    [Fact]
    public void ReadInstallName_TooShort_ReturnsNull()
    {
        Assert.Null(MachOReader.ReadInstallName(Array.Empty<byte>()));
        Assert.Null(MachOReader.ReadInstallName(new byte[] { 0xCF, 0xFA, 0xED }));
    }

    [Fact]
    public void ReadInstallName_TruncatedLoadCommand_ReturnsNull()
    {
        // Header claims a load command, but the bytes stop inside it: the reader must bounds-check and
        // return null (unreadable), never read past the buffer.
        var image = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        var truncated = new byte[image.Length - 12];
        Array.Copy(image, truncated, truncated.Length);
        Assert.Null(MachOReader.ReadInstallName(truncated));
    }

    [Fact]
    public void ReadInstallName_OverflowingCmdSize_ReturnsNull()
    {
        // A corrupt LC_ID_DYLIB cmdsize > int.MaxValue must not wrap to a negative int and bypass the
        // bounds check; the reader must treat it as unparseable (null), never read past the buffer or
        // throw. The single load command starts at the 64-bit header end (32); cmdsize is the uint32
        // at command offset +4.
        var image = BuildThinMachO(RuntimeInstallName, Endian.Little, is64: true);
        const int cmdSizeFieldOffset = 32 + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cmdSizeFieldOffset, 4), 0x80000020u);
        Assert.Null(MachOReader.ReadInstallName(image));
    }

    [Fact]
    public void ReadInstallName_NonexistentPath_ReturnsNull()
    {
        Assert.Null(MachOReader.ReadInstallName(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    // ---- fixture construction ----

    private enum Endian { Little, Big }

    private const uint MhMagic64 = 0xFEEDFACF;
    private const uint MhMagic32 = 0xFEEDFACE;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint LcIdDylib = 0x0D;
    private const uint LcUuid = 0x1B;
    private const int CpuTypeArm64 = 0x0100000C;
    private const int CpuTypeX86_64 = 0x01000007;

    private static void WriteU32(List<byte> buf, uint value, Endian endian)
    {
        var tmp = new byte[4];
        if (endian == Endian.Little) BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        else BinaryPrimitives.WriteUInt32BigEndian(tmp, value);
        buf.AddRange(tmp);
    }

    private static void WriteU64(List<byte> buf, ulong value, Endian endian)
    {
        var tmp = new byte[8];
        if (endian == Endian.Little) BinaryPrimitives.WriteUInt64LittleEndian(tmp, value);
        else BinaryPrimitives.WriteUInt64BigEndian(tmp, value);
        buf.AddRange(tmp);
    }

    private static void AppendMachHeader(List<byte> image, uint ncmds, uint sizeofcmds, Endian endian, bool is64)
    {
        WriteU32(image, is64 ? MhMagic64 : MhMagic32, endian); // magic (read LE → MH_(CI)GAM_64/32)
        WriteU32(image, unchecked((uint)CpuTypeArm64), endian); // cputype
        WriteU32(image, 0, endian);                             // cpusubtype
        WriteU32(image, 6, endian);                             // filetype = MH_DYLIB
        WriteU32(image, ncmds, endian);                         // ncmds
        WriteU32(image, sizeofcmds, endian);                    // sizeofcmds
        WriteU32(image, 0, endian);                             // flags
        if (is64) WriteU32(image, 0, endian);                   // reserved (64-bit only)
    }

    private static byte[] IdDylibCommand(string installName, Endian endian, bool is64)
    {
        var nameBytes = Encoding.UTF8.GetBytes(installName);
        const int nameOffset = 24; // cmd(4) cmdsize(4) name(4) timestamp(4) current(4) compat(4)
        int align = is64 ? 8 : 4;
        int rawCmdSize = nameOffset + nameBytes.Length + 1; // + NUL
        int cmdSize = (rawCmdSize + (align - 1)) & ~(align - 1);

        var lc = new List<byte>();
        WriteU32(lc, LcIdDylib, endian);
        WriteU32(lc, (uint)cmdSize, endian);
        WriteU32(lc, nameOffset, endian);
        WriteU32(lc, 1, endian);            // timestamp
        WriteU32(lc, 0x00010000u, endian);  // current_version
        WriteU32(lc, 0x00010000u, endian);  // compatibility_version
        lc.AddRange(nameBytes);
        lc.Add(0);                          // NUL terminator
        while (lc.Count < cmdSize) lc.Add(0); // pad to alignment
        return lc.ToArray();
    }

    private static byte[] BuildThinMachO(string installName, Endian endian, bool is64)
    {
        var lc = IdDylibCommand(installName, endian, is64);
        var image = new List<byte>();
        AppendMachHeader(image, ncmds: 1, sizeofcmds: (uint)lc.Length, endian, is64);
        image.AddRange(lc);
        return image.ToArray();
    }

    private static byte[] BuildThinMachOWithSingleOtherCommand(uint cmd, Endian endian)
    {
        const int cmdSize = 16; // cmd(4) cmdsize(4) + 8 bytes payload, 8-aligned
        var lc = new List<byte>();
        WriteU32(lc, cmd, endian);
        WriteU32(lc, cmdSize, endian);
        while (lc.Count < cmdSize) lc.Add(0);
        var image = new List<byte>();
        AppendMachHeader(image, ncmds: 1, sizeofcmds: cmdSize, endian, is64: true);
        image.AddRange(lc);
        return image.ToArray();
    }

    // Build a FAT_MAGIC universal binary (big-endian header, 32-bit offsets) wrapping thin slices.
    private static byte[] BuildFat(params (int cpuType, byte[] slice)[] slices)
    {
        const int sliceAlign = 16;
        int headerSize = 8 + slices.Length * 20; // fat_header(8) + nfat * fat_arch(20)

        var body = new List<byte>();
        var entries = new List<(int cpuType, int offset, int size)>();
        int cursor = headerSize;
        foreach (var (cpuType, slice) in slices)
        {
            int pad = (sliceAlign - (cursor % sliceAlign)) % sliceAlign;
            for (int i = 0; i < pad; i++) body.Add(0);
            cursor += pad;
            entries.Add((cpuType, cursor, slice.Length));
            body.AddRange(slice);
            cursor += slice.Length;
        }

        var image = new List<byte>();
        WriteU32(image, FatMagic, Endian.Big);
        WriteU32(image, (uint)slices.Length, Endian.Big);
        foreach (var (cpuType, offset, size) in entries)
        {
            WriteU32(image, unchecked((uint)cpuType), Endian.Big); // cputype
            WriteU32(image, 0, Endian.Big);                        // cpusubtype
            WriteU32(image, (uint)offset, Endian.Big);             // offset
            WriteU32(image, (uint)size, Endian.Big);               // size
            WriteU32(image, 0, Endian.Big);                        // align (log2)
        }
        image.AddRange(body);
        return image.ToArray();
    }

    // Build a FAT_MAGIC_64 universal binary (big-endian header, fat_arch_64 entries: 8-byte offsets
    // and sizes, 32 bytes each) wrapping thin slices.
    private static byte[] BuildFat64(params (int cpuType, byte[] slice)[] slices)
    {
        const int sliceAlign = 16;
        int headerSize = 8 + slices.Length * 32; // fat_header(8) + nfat * fat_arch_64(32)

        var body = new List<byte>();
        var entries = new List<(int cpuType, long offset, long size)>();
        int cursor = headerSize;
        foreach (var (cpuType, slice) in slices)
        {
            int pad = (sliceAlign - (cursor % sliceAlign)) % sliceAlign;
            for (int i = 0; i < pad; i++) body.Add(0);
            cursor += pad;
            entries.Add((cpuType, cursor, slice.Length));
            body.AddRange(slice);
            cursor += slice.Length;
        }

        var image = new List<byte>();
        WriteU32(image, FatMagic64, Endian.Big);
        WriteU32(image, (uint)slices.Length, Endian.Big);
        foreach (var (cpuType, offset, size) in entries)
        {
            WriteU32(image, unchecked((uint)cpuType), Endian.Big); // cputype
            WriteU32(image, 0, Endian.Big);                        // cpusubtype
            WriteU64(image, (ulong)offset, Endian.Big);            // offset (8 bytes)
            WriteU64(image, (ulong)size, Endian.Big);              // size (8 bytes)
            WriteU32(image, 0, Endian.Big);                        // align (log2)
            WriteU32(image, 0, Endian.Big);                        // reserved
        }
        image.AddRange(body);
        return image.ToArray();
    }
}
