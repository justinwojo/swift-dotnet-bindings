// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Frozen structs mixing Bool with other fields, passed and returned by value through every route
/// the generator takes for them: free functions and struct and class members through <c>@_cdecl</c>
/// wrappers, and a generic receiver, a protocol witness, a closure and an async member (fixture:
/// PackedBoolStructs.swift). None of these reaches a direct Swift-convention call or a native thunk,
/// so no bridge carries them and no P/Invoke carries the struct by value, which Mono's AOT compiler
/// cannot build a wrapper for.
///
/// <para>Each <c>Make*</c> builds Bool field i from bit i of <c>raw</c> and the other fields from
/// <c>raw &gt;&gt; 8</c>. The raws below set bits above a Bool's low byte while leaving the Bool false,
/// which on x86_64 leaves those bits in the Bool's register; every Bool read back must be exactly the
/// byte 0 or 1, never a value C# reads as true where Swift meant false.</para>
/// </summary>
public class PackedBoolStructTests : TestBase
{
    public PackedBoolStructTests(TestResults results) : base(results) { }

    static readonly nint[] Raws =
    {
        0, 1, 2, 4, 7,
        0x100,              // every Bool false, bit 8 set
        0x7FFF_FF00,        // every Bool false, bits 8-30 set
        unchecked((nint)0xFFFF_FFFF_FFFF_FF00), // every Bool false, every upper bit set
        unchecked((nint)0xFFFF_FFFF_FFFF_FF05),
        unchecked((nint)0x1234_5678_9A06L),
    };

    void AssertBool(bool expected, bool actual, string what)
    {
        byte raw = Unsafe.As<bool, byte>(ref actual);
        AssertTrue(raw <= 1, $"{what}: a Bool must arrive as the byte 0 or 1, got 0x{raw:X2}");
        AssertEqual(expected, actual, what);
    }

    void AssertPackedBoolFloat(PackedBoolFloat expected, PackedBoolFloat actual, string what)
    {
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertEqual(expected.F, actual.F, $"{what}.F");
    }
    static PackedBoolFloat ExpectedPackedBoolFloat(nint raw) => new PackedBoolFloat((raw & 1) != 0, (float)(raw >> 8));

    public void TestPackedBoolFloat_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolFloat(ExpectedPackedBoolFloat(raw), TestLibFunctions.MakePackedBoolFloat(raw), $"free MakePackedBoolFloat(0x{raw:X})");
            AssertPackedBoolFloat(ExpectedPackedBoolFloat(raw), host.MakePackedBoolFloat(raw), $"PackedBoolHost.MakePackedBoolFloat(0x{raw:X})");
        }
    }

    public void TestPackedBoolFloat_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 2; variant++)
        {
            var value = new PackedBoolFloat(((variant >> 0) & 1) != 0, variant + 0.5f - 3f);
            AssertPackedBoolFloat(value, TestLibFunctions.EchoPackedBoolFloat(value), $"free EchoPackedBoolFloat variant {variant}");
            AssertPackedBoolFloat(value, host.EchoPackedBoolFloat(value), $"PackedBoolHost.EchoPackedBoolFloat variant {variant}");
        }
    }

    void AssertPackedFloatBool(PackedFloatBool expected, PackedFloatBool actual, string what)
    {
        AssertEqual(expected.F, actual.F, $"{what}.F");
        AssertBool(expected.A, actual.A, $"{what}.A");
    }
    static PackedFloatBool ExpectedPackedFloatBool(nint raw) => new PackedFloatBool((float)(raw >> 8), (raw & 2) != 0);

    public void TestPackedFloatBool_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedFloatBool(ExpectedPackedFloatBool(raw), TestLibFunctions.MakePackedFloatBool(raw), $"free MakePackedFloatBool(0x{raw:X})");
            AssertPackedFloatBool(ExpectedPackedFloatBool(raw), host.MakePackedFloatBool(raw), $"PackedBoolHost.MakePackedFloatBool(0x{raw:X})");
        }
    }

    public void TestPackedFloatBool_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 2; variant++)
        {
            var value = new PackedFloatBool(variant + 0.5f - 3f, ((variant >> 0) & 1) != 0);
            AssertPackedFloatBool(value, TestLibFunctions.EchoPackedFloatBool(value), $"free EchoPackedFloatBool variant {variant}");
            AssertPackedFloatBool(value, host.EchoPackedFloatBool(value), $"PackedBoolHost.EchoPackedFloatBool variant {variant}");
        }
    }

    void AssertPackedBoolFloatBool(PackedBoolFloatBool expected, PackedBoolFloatBool actual, string what)
    {
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertEqual(expected.F, actual.F, $"{what}.F");
        AssertBool(expected.C, actual.C, $"{what}.C");
    }
    static PackedBoolFloatBool ExpectedPackedBoolFloatBool(nint raw) => new PackedBoolFloatBool((raw & 1) != 0, (float)(raw >> 8), (raw & 4) != 0);

    public void TestPackedBoolFloatBool_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolFloatBool(ExpectedPackedBoolFloatBool(raw), TestLibFunctions.MakePackedBoolFloatBool(raw), $"free MakePackedBoolFloatBool(0x{raw:X})");
            AssertPackedBoolFloatBool(ExpectedPackedBoolFloatBool(raw), host.MakePackedBoolFloatBool(raw), $"PackedBoolHost.MakePackedBoolFloatBool(0x{raw:X})");
        }
    }

    public void TestPackedBoolFloatBool_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolFloatBool(((variant >> 0) & 1) != 0, variant + 0.5f - 3f, ((variant >> 1) & 1) != 0);
            AssertPackedBoolFloatBool(value, TestLibFunctions.EchoPackedBoolFloatBool(value), $"free EchoPackedBoolFloatBool variant {variant}");
            AssertPackedBoolFloatBool(value, host.EchoPackedBoolFloatBool(value), $"PackedBoolHost.EchoPackedBoolFloatBool variant {variant}");
        }
    }

    void AssertPackedBoolBoolInt32(PackedBoolBoolInt32 expected, PackedBoolBoolInt32 actual, string what)
    {
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertBool(expected.B, actual.B, $"{what}.B");
        AssertEqual(expected.N, actual.N, $"{what}.N");
    }
    static PackedBoolBoolInt32 ExpectedPackedBoolBoolInt32(nint raw) => new PackedBoolBoolInt32((raw & 1) != 0, (raw & 2) != 0, unchecked((int)(long)(raw >> 8)));

    public void TestPackedBoolBoolInt32_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolBoolInt32(ExpectedPackedBoolBoolInt32(raw), TestLibFunctions.MakePackedBoolBoolInt32(raw), $"free MakePackedBoolBoolInt32(0x{raw:X})");
            AssertPackedBoolBoolInt32(ExpectedPackedBoolBoolInt32(raw), host.MakePackedBoolBoolInt32(raw), $"PackedBoolHost.MakePackedBoolBoolInt32(0x{raw:X})");
        }
    }

    public void TestPackedBoolBoolInt32_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolBoolInt32(((variant >> 0) & 1) != 0, ((variant >> 1) & 1) != 0, variant * -40503);
            AssertPackedBoolBoolInt32(value, TestLibFunctions.EchoPackedBoolBoolInt32(value), $"free EchoPackedBoolBoolInt32 variant {variant}");
            AssertPackedBoolBoolInt32(value, host.EchoPackedBoolBoolInt32(value), $"PackedBoolHost.EchoPackedBoolBoolInt32 variant {variant}");
        }
    }

    void AssertPackedInt32Bool(PackedInt32Bool expected, PackedInt32Bool actual, string what)
    {
        AssertEqual(expected.N, actual.N, $"{what}.N");
        AssertBool(expected.A, actual.A, $"{what}.A");
    }
    static PackedInt32Bool ExpectedPackedInt32Bool(nint raw) => new PackedInt32Bool(unchecked((int)(long)(raw >> 8)), (raw & 2) != 0);

    public void TestPackedInt32Bool_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedInt32Bool(ExpectedPackedInt32Bool(raw), TestLibFunctions.MakePackedInt32Bool(raw), $"free MakePackedInt32Bool(0x{raw:X})");
            AssertPackedInt32Bool(ExpectedPackedInt32Bool(raw), host.MakePackedInt32Bool(raw), $"PackedBoolHost.MakePackedInt32Bool(0x{raw:X})");
        }
    }

    public void TestPackedInt32Bool_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 2; variant++)
        {
            var value = new PackedInt32Bool(variant * -40503, ((variant >> 0) & 1) != 0);
            AssertPackedInt32Bool(value, TestLibFunctions.EchoPackedInt32Bool(value), $"free EchoPackedInt32Bool variant {variant}");
            AssertPackedInt32Bool(value, host.EchoPackedInt32Bool(value), $"PackedBoolHost.EchoPackedInt32Bool variant {variant}");
        }
    }

    void AssertPackedBoolInt32Bool(PackedBoolInt32Bool expected, PackedBoolInt32Bool actual, string what)
    {
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertEqual(expected.B, actual.B, $"{what}.B");
        AssertBool(expected.C, actual.C, $"{what}.C");
    }
    static PackedBoolInt32Bool ExpectedPackedBoolInt32Bool(nint raw) => new PackedBoolInt32Bool((raw & 1) != 0, unchecked((int)(long)(raw >> 8)), (raw & 4) != 0);

    public void TestPackedBoolInt32Bool_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolInt32Bool(ExpectedPackedBoolInt32Bool(raw), TestLibFunctions.MakePackedBoolInt32Bool(raw), $"free MakePackedBoolInt32Bool(0x{raw:X})");
            AssertPackedBoolInt32Bool(ExpectedPackedBoolInt32Bool(raw), host.MakePackedBoolInt32Bool(raw), $"PackedBoolHost.MakePackedBoolInt32Bool(0x{raw:X})");
        }
    }

    public void TestPackedBoolInt32Bool_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolInt32Bool(((variant >> 0) & 1) != 0, variant * -40503, ((variant >> 1) & 1) != 0);
            AssertPackedBoolInt32Bool(value, TestLibFunctions.EchoPackedBoolInt32Bool(value), $"free EchoPackedBoolInt32Bool variant {variant}");
            AssertPackedBoolInt32Bool(value, host.EchoPackedBoolInt32Bool(value), $"PackedBoolHost.EchoPackedBoolInt32Bool variant {variant}");
        }
    }

    void AssertPackedBoolDoubleBool(PackedBoolDoubleBool expected, PackedBoolDoubleBool actual, string what)
    {
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertEqual(expected.D, actual.D, $"{what}.D");
        AssertBool(expected.B, actual.B, $"{what}.B");
    }
    static PackedBoolDoubleBool ExpectedPackedBoolDoubleBool(nint raw) => new PackedBoolDoubleBool((raw & 1) != 0, (double)(raw >> 8), (raw & 4) != 0);

    public void TestPackedBoolDoubleBool_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolDoubleBool(ExpectedPackedBoolDoubleBool(raw), TestLibFunctions.MakePackedBoolDoubleBool(raw), $"free MakePackedBoolDoubleBool(0x{raw:X})");
            AssertPackedBoolDoubleBool(ExpectedPackedBoolDoubleBool(raw), host.MakePackedBoolDoubleBool(raw), $"PackedBoolHost.MakePackedBoolDoubleBool(0x{raw:X})");
        }
    }

    public void TestPackedBoolDoubleBool_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolDoubleBool(((variant >> 0) & 1) != 0, variant + 0.25 - 7.0, ((variant >> 1) & 1) != 0);
            AssertPackedBoolDoubleBool(value, TestLibFunctions.EchoPackedBoolDoubleBool(value), $"free EchoPackedBoolDoubleBool variant {variant}");
            AssertPackedBoolDoubleBool(value, host.EchoPackedBoolDoubleBool(value), $"PackedBoolHost.EchoPackedBoolDoubleBool variant {variant}");
        }
    }

    void AssertPackedIntBoolInt(PackedIntBoolInt expected, PackedIntBoolInt actual, string what)
    {
        AssertEqual(expected.NNative, actual.NNative, $"{what}.NNative");
        AssertBool(expected.A, actual.A, $"{what}.A");
        AssertEqual(expected.MNative, actual.MNative, $"{what}.MNative");
    }
    static PackedIntBoolInt ExpectedPackedIntBoolInt(nint raw) => new PackedIntBoolInt(raw >> 8, (raw & 2) != 0, raw >> 8);

    public void TestPackedIntBoolInt_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedIntBoolInt(ExpectedPackedIntBoolInt(raw), TestLibFunctions.MakePackedIntBoolInt(raw), $"free MakePackedIntBoolInt(0x{raw:X})");
            AssertPackedIntBoolInt(ExpectedPackedIntBoolInt(raw), host.MakePackedIntBoolInt(raw), $"PackedBoolHost.MakePackedIntBoolInt(0x{raw:X})");
        }
    }

    public void TestPackedIntBoolInt_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 2; variant++)
        {
            var value = new PackedIntBoolInt((nint)(variant * -0x1_0000_0003L), ((variant >> 0) & 1) != 0, (nint)(variant * -0x1_0000_0003L));
            AssertPackedIntBoolInt(value, TestLibFunctions.EchoPackedIntBoolInt(value), $"free EchoPackedIntBoolInt variant {variant}");
            AssertPackedIntBoolInt(value, host.EchoPackedIntBoolInt(value), $"PackedBoolHost.EchoPackedIntBoolInt variant {variant}");
        }
    }

    void AssertPackedBoolPair(PackedBoolPair expected, PackedBoolPair actual, string what)
    {
        AssertBool(expected.Flag, actual.Flag, $"{what}.Flag");
        AssertEqual(expected.NNative, actual.NNative, $"{what}.NNative");
    }
    static PackedBoolPair ExpectedPackedBoolPair(nint raw) => new PackedBoolPair((raw & 1) != 0, raw >> 8);

    public void TestPackedBoolPair_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolPair(ExpectedPackedBoolPair(raw), TestLibFunctions.MakePackedBoolPair(raw), $"free MakePackedBoolPair(0x{raw:X})");
            AssertPackedBoolPair(ExpectedPackedBoolPair(raw), host.MakePackedBoolPair(raw), $"PackedBoolHost.MakePackedBoolPair(0x{raw:X})");
        }
    }

    public void TestPackedBoolPair_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 2; variant++)
        {
            var value = new PackedBoolPair(((variant >> 0) & 1) != 0, (nint)(variant * -0x1_0000_0003L));
            AssertPackedBoolPair(value, TestLibFunctions.EchoPackedBoolPair(value), $"free EchoPackedBoolPair variant {variant}");
            AssertPackedBoolPair(value, host.EchoPackedBoolPair(value), $"PackedBoolHost.EchoPackedBoolPair variant {variant}");
        }
    }

    void AssertPackedBoolQuad(PackedBoolQuad expected, PackedBoolQuad actual, string what)
    {
        AssertBool(expected.X, actual.X, $"{what}.X");
        AssertBool(expected.Y, actual.Y, $"{what}.Y");
        AssertEqual(expected.Z, actual.Z, $"{what}.Z");
    }
    static PackedBoolQuad ExpectedPackedBoolQuad(nint raw) => new PackedBoolQuad((raw & 1) != 0, (raw & 2) != 0, unchecked((ushort)(long)(raw >> 8)));

    public void TestPackedBoolQuad_ReturnedByValue()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertPackedBoolQuad(ExpectedPackedBoolQuad(raw), TestLibFunctions.MakePackedBoolQuad(raw), $"free MakePackedBoolQuad(0x{raw:X})");
            AssertPackedBoolQuad(ExpectedPackedBoolQuad(raw), host.MakePackedBoolQuad(raw), $"PackedBoolHost.MakePackedBoolQuad(0x{raw:X})");
        }
    }

    public void TestPackedBoolQuad_PassedAndReturnedByValue()
    {
        var host = new PackedBoolHost();
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolQuad(((variant >> 0) & 1) != 0, ((variant >> 1) & 1) != 0, (ushort)(variant * 4099));
            AssertPackedBoolQuad(value, TestLibFunctions.EchoPackedBoolQuad(value), $"free EchoPackedBoolQuad variant {variant}");
            AssertPackedBoolQuad(value, host.EchoPackedBoolQuad(value), $"PackedBoolHost.EchoPackedBoolQuad variant {variant}");
        }
    }

    public void TestScalarBool_FalseWithUpperBitsSet()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
        {
            AssertBool((raw & 1) != 0, TestLibFunctions.PackedBoolLowBit(raw), $"free PackedBoolLowBit(0x{raw:X})");
            AssertBool((raw & 1) != 0, host.LowBit(raw), $"PackedBoolHost.LowBit(0x{raw:X})");
            var pair = ExpectedPackedBoolPair(raw);
            AssertBool(pair.Flag, TestLibFunctions.PackedBoolPairFlag(pair), $"PackedBoolPairFlag(0x{raw:X})");
        }
    }

    public void TestStaticMemberAndStoredProperty()
    {
        foreach (var raw in Raws)
            AssertPackedBoolPair(ExpectedPackedBoolPair(raw), PackedBoolHost.MakePair(raw), $"PackedBoolHost.MakePair(0x{raw:X})");
        var host = new PackedBoolHost();
        AssertPackedBoolQuad(new PackedBoolQuad(false, true, 7), host.Stored, "initial stored value");
        for (int variant = 0; variant < 4; variant++)
        {
            var value = new PackedBoolQuad((variant & 1) != 0, (variant & 2) != 0, (ushort)(0xFF00 | variant));
            host.Stored = value;
            AssertPackedBoolQuad(value, host.Stored, $"stored value variant {variant}");
        }
    }

    public void TestStructMembers()
    {
        foreach (var raw in Raws)
        {
            var value = ExpectedPackedBoolInt32Bool(raw);
            AssertPackedBoolInt32Bool(new PackedBoolInt32Bool(!value.A, value.B, !value.C), value.Flipped(), $"Flipped(0x{raw:X})");
            AssertEqual((value.A ? 1 : 0) + (value.C ? 2 : 0) + value.B * 4L, (long)value.SummaryNative, $"Summary(0x{raw:X})");
        }
    }

    public void TestGenericReceiver()
    {
        var box = new PackedBoolGenericBox<long>(3);
        foreach (var raw in Raws)
        {
            var expected = ExpectedPackedBoolInt32Bool(raw);
            AssertPackedBoolInt32Bool(expected, box.Make(raw), $"GenericBox.Make(0x{raw:X})");
            AssertPackedBoolInt32Bool(expected, box.Echo(expected), $"GenericBox.Echo(0x{raw:X})");
        }
    }

    sealed class Source : IPackedBoolSource
    {
        public PackedBoolPair Pair(nint raw) => new PackedBoolPair((raw & 1) == 0, raw + 1);
    }

    public void TestProtocolWitnessAndClosure()
    {
        var source = new Source();
        foreach (var raw in Raws)
        {
            AssertPackedBoolPair(new PackedBoolPair((raw & 1) == 0, raw + 1), TestLibFunctions.PackedBoolFromSource(source, raw),
                $"PackedBoolFromSource(0x{raw:X})");
            var seen = ExpectedPackedBoolPair(raw);
            AssertPackedBoolPair(new PackedBoolPair(!seen.Flag, seen.NNative - 1),
                TestLibFunctions.PackedBoolThroughCallback(raw, p =>
                {
                    AssertPackedBoolPair(seen, p, $"callback argument (0x{raw:X})");
                    return new PackedBoolPair(!p.Flag, p.NNative - 1);
                }),
                $"PackedBoolThroughCallback(0x{raw:X})");
        }
    }

    public async Task TestAsyncMember()
    {
        var host = new PackedBoolHost();
        foreach (var raw in Raws)
            AssertPackedBoolInt32Bool(ExpectedPackedBoolInt32Bool(raw), await host.LaterAsync(raw), $"LaterAsync(0x{raw:X})");
    }
}
