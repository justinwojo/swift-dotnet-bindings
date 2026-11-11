// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Linq;
using System.Reflection.Metadata;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="LegacySwiftSelfPhysical"/>: where CoreCLR places a managed <c>SwiftSelf&lt;T&gt;</c>.
/// The measured pins are the probe's <c>f.selfT.small</c> (a two-long struct, lowered) and
/// <c>f.selfT.large</c> (a five-long struct, by address in the self register); the cutoff around them
/// is four lowered pieces, counted the runtime's way (floats one each, small integers merged per
/// pointer-sized block), not a byte size.
/// </summary>
public class LegacySwiftSelfPhysicalTests
{
    private const string Structs = """
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        public struct Pair { public long A; public long B; }
        public struct Four { public long A, B, C, D; }
        public struct Five { public long A, B, C, D, E; }
        public struct FourFloatsAndDouble { public float X, Y, Z, W; public double T; }
        public struct FourDoubles { public double A, B, C, D; }
        public struct FiveInts { public int A, B, C, D, E; }
        public struct NineBytes { public byte A, B, C, D, E, F, G, H, I; }
        public struct ByteThenLong { public byte A; public long B; }
        public struct Nested { public Pair P; public float F; public double D; }
        public struct NestedOver { public Pair P; public Pair Q; public int R; }
        public struct WithPointer { public nint Handle; public unsafe void* Ptr; }
        public enum Wide : long { A }
        public struct WithEnum { public Wide E; public short S; }
        public struct Empty { }
        [StructLayout(LayoutKind.Explicit)] public struct Overlay { [FieldOffset(0)] public long A; [FieldOffset(0)] public double B; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Packed { public byte A; public long B; }
        [StructLayout(LayoutKind.Sequential, Size = 64)] public struct Sized { public long A; }
        public struct WithReference { public object O; }
        [InlineArray(2)] public struct Two { private long _e; }
        """;

    private static LegacySelfPhysical Classify(string typeName)
    {
        var image = SwiftTransitionScannerTests.Compile(Structs, "Layouts");
        using var resolver = new AssemblyResolver(Array.Empty<string>());
        var reader = resolver.AddImage(image);
        var handle = reader.TypeDefinitions.Single(h => reader.GetString(reader.GetTypeDefinition(h).Name) == typeName);
        var type = new SigTypeProvider(reader).GetTypeFromDefinition(reader, handle, (byte)SignatureTypeKind.ValueType);
        return LegacySwiftSelfPhysical.Classify(type, resolver);
    }

    private static string Pieces(LegacySelfPhysical physical) =>
        string.Join(",", physical.Pieces.Select(p => $"{p.Kind}@{p.Offset}"));

    // ---- measured pins ----

    [Fact]
    public void SelfTSmall_TwoLongs_IsLowered()
    {
        var physical = Classify("Pair");
        Assert.Equal(LegacySelfPlacement.Lowered, physical.Placement);
        Assert.Equal("int64@0,int64@8", Pieces(physical));
    }

    [Fact]
    public void SelfTLarge_FiveLongs_IsByAddressInTheSelfRegister()
    {
        var physical = Classify("Five");
        Assert.Equal(LegacySelfPlacement.ByAddressInSelfRegister, physical.Placement);
        Assert.Contains("5 lowered pieces", physical.Detail);
    }

    // ---- the cutoff: four pieces, counted the runtime's way ----

    [Theory]
    [InlineData("Four", "int64@0,int64@8,int64@16,int64@24")]
    [InlineData("FourDoubles", "double@0,double@8,double@16,double@24")]
    [InlineData("FiveInts", "int64@0,int64@8,int32@16")]
    [InlineData("NineBytes", "int64@0,int8@8")]
    [InlineData("ByteThenLong", "int8@0,int64@8")]
    [InlineData("Nested", "int64@0,int64@8,float@16,double@24")]
    [InlineData("WithPointer", "int64@0,int64@8")]
    [InlineData("WithEnum", "int64@0,int16@8")]
    public void AtOrUnderFourPieces_IsLowered(string type, string pieces)
    {
        var physical = Classify(type);
        Assert.Equal(LegacySelfPlacement.Lowered, physical.Placement);
        Assert.Equal(pieces, Pieces(physical));
    }

    [Theory]
    [InlineData("FourFloatsAndDouble")] // 24 bytes, but five pieces: floats are not merged
    [InlineData("NestedOver")]          // two pairs and an int: five pieces
    [InlineData("Empty")]               // a lowering with no elements
    public void OverFourPiecesOrNone_IsByAddress(string type)
    {
        Assert.Equal(LegacySelfPlacement.ByAddressInSelfRegister, Classify(type).Placement);
    }

    [Theory]
    [InlineData("Overlay", "sequential layout")]
    [InlineData("Packed", "packing size")]
    [InlineData("Sized", "explicit size")]
    [InlineData("WithReference", "not a blittable")]
    [InlineData("Two", "inline array")]
    public void LayoutsItCannotReadWithCertainty_AreUnknown(string type, string why)
    {
        var physical = Classify(type);
        Assert.Equal(LegacySelfPlacement.Unknown, physical.Placement);
        Assert.Contains(why, physical.Detail);
    }

    // ---- through the gate ----

    [Fact]
    public void Gate_ReportsEachSwiftSelfOfTsPlacement()
    {
        var image = SwiftTransitionScannerTests.Compile("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Swift;
            [assembly: DisableRuntimeMarshalling]
            public struct Pair { public long A; public long B; }
            public struct Five { public long A, B, C, D, E; }
            public static class C
            {
                [DllImport("lib", EntryPoint = "small")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                public static extern long Small(long k, SwiftSelf<Pair> self);
                [DllImport("lib", EntryPoint = "large")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                public static extern long Large(long k, SwiftSelf<Five> self);
            }
            """, "Fixture");

        var report = SwiftTransitionScanner.Scan(image, "Fixture.dll", SwiftTransitionScannerTests.Options());

        Assert.Equal(2, report.Inventory.SwiftPInvokesWithSwiftSelfGeneric);
        Assert.Equal(1, report.Inventory.SwiftSelfGenericLowered);
        Assert.Equal(1, report.Inventory.SwiftSelfGenericByAddress);
        Assert.Equal(0, report.Inventory.SwiftSelfGenericPlacementUnknown);
        // Both stay R1 violations: a typed self is never a primitive carrier.
        Assert.Equal(2, report.Violations.Count(v => v.Rule == TransitionRule.R1));
        Assert.Contains(report.Violations, v => v.EntryPoint == "small" && v.Reason.Contains("lowered to 2 piece(s)"));
        Assert.Contains(report.Violations, v => v.EntryPoint == "large" && v.Reason.Contains("by address in the self register"));
    }
}
