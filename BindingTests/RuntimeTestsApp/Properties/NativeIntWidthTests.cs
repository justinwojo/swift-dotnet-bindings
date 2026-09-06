// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Width behaviour for pointer-width Swift integers (Int/UInt) that enter through an initializer
/// and leave through a property. The constructor and the property are emitted by different paths,
/// so the two halves have to agree on the range a consumer may use.
///
/// The policy these tests pin has two halves. A narrowed 32-bit accessor reports a value it cannot
/// represent — it throws rather than handing back a wrapped number — and every narrowed property
/// is accompanied by a native-width sibling (<c>{Name}Native</c>) that carries the value whole. The
/// three initializer lanes (constructor, recovered static factory, failable factory) all offer the
/// same idiomatic 32-bit entry point, and method returns stay at their native width.
/// </summary>
public class NativeIntWidthTests : TestBase
{
    public NativeIntWidthTests(TestResults results) : base(results) { }

    public void TestResourceBudget_SmallValues_RoundTrip()
    {
        // Idiomatic 32-bit arguments through the constructor, read back through the narrowed
        // properties. (C# widens int to nint on its own, so the call binding is not what is being
        // observed here — the value surviving the round trip is.)
        using var budget = new ResourceBudget(5u, 7);

        AssertEqual(5u, budget.SizeLimit, "SizeLimit round-trips");
        AssertEqual(7, budget.Offset, "Offset round-trips");
        AssertEqual(14, budget.DoubledOffset, "DoubledOffset round-trips");
    }

    public void TestResourceBudget_FunctionAndConstructorAcceptSameRange()
    {
        // A value above int's range but inside uint's, entering through the free function and
        // through the constructor. Both halves of the API have to carry it the same distance.
        using var viaFunction = Functions.MakeResourceBudget(2147483648u, 100);
        using var viaConstructor = new ResourceBudget(2147483648u, 100);

        AssertEqual(2147483648u, viaFunction.SizeLimit, "function-built SizeLimit at 2^31");
        AssertEqual(2147483648u, viaConstructor.SizeLimit, "constructor-built SizeLimit at 2^31");
        AssertEqual(viaFunction.Offset, viaConstructor.Offset, "both offsets agree");
    }

    public void TestResourceBudget_SignedOnlyConstructorOverload()
    {
        using var budget = new ResourceBudget(9);

        AssertEqual(9, budget.Offset, "single-argument constructor offset");
        AssertEqual(0u, budget.SizeLimit, "single-argument constructor limit defaults to zero");
    }

    public void TestResourceBudget_AtNarrowedBoundary_ReturnsExactValue()
    {
        // The largest values the narrowed accessors can represent. These must come back exactly,
        // not be rejected by an off-by-one in the range check.
        using var budget = new ResourceBudget(uint.MaxValue, int.MaxValue);

        AssertEqual(uint.MaxValue, budget.SizeLimit, "SizeLimit at uint.MaxValue reads back exactly");
        AssertEqual(int.MaxValue, budget.Offset, "Offset at int.MaxValue reads back exactly");
        AssertEqual((long)uint.MaxValue, (long)budget.SizeLimitNative,
            "native sibling agrees at the boundary");
        AssertEqual((long)int.MaxValue, (long)budget.OffsetNative,
            "signed native sibling agrees at the boundary");
    }

    public void TestResourceBudget_LimitAboveNarrowedRange_Throws()
    {
        // 2^32 crosses the boundary intact — the Swift side still reads it back in full — but it
        // does not fit the narrowed 32-bit property, which says so instead of wrapping to 0.
        using var budget = Functions.MakeOversizedResourceBudget();

        AssertEqual("4294967296", Functions.ReadSizeLimitAsString(budget),
            "Swift still holds the full-width value");
        AssertThrows<OverflowException>(() => { _ = budget.SizeLimit; },
            "SizeLimit above uint range reports instead of wrapping");
    }

    public void TestResourceBudget_OffsetAboveNarrowedRange_Throws()
    {
        using var budget = Functions.MakeOverSignedResourceBudget();

        AssertEqual("2147483648", Functions.ReadOffsetAsString(budget),
            "Swift still holds the full-width offset");
        AssertThrows<OverflowException>(() => { _ = budget.Offset; },
            "Offset above int range reports instead of wrapping");
    }

    public void TestResourceBudget_NativeSiblings_CarryTheFullWidthValue()
    {
        using var oversized = Functions.MakeOversizedResourceBudget();
        using var overSigned = Functions.MakeOverSignedResourceBudget();

        AssertEqual(4294967296L, (long)oversized.SizeLimitNative,
            "SizeLimitNative carries 2^32 whole");
        AssertEqual(2147483648L, (long)overSigned.OffsetNative,
            "OffsetNative carries 2^31 whole");
    }

    public void TestResourceBudget_OverflowMessage_NamesTheNativeSibling()
    {
        // The message is the only place a caller learns there is a lossless way to read the same
        // value, so it has to name it.
        using var budget = Functions.MakeOversizedResourceBudget();

        string message = "";
        try
        {
            _ = budget.SizeLimit;
        }
        catch (OverflowException ex)
        {
            message = ex.Message;
        }

        AssertTrue(message.Contains("SizeLimitNative"),
            $"overflow message points at the native sibling (was: \"{message}\")");
    }

    public void TestResourceBudget_CursorSetter_RoundTripsThroughBothAccessors()
    {
        using var budget = new ResourceBudget(0u, 0);

        // The narrowed setter widens on the way in, so anything an int can hold round-trips.
        budget.Cursor = -12345;
        AssertEqual(-12345, budget.Cursor, "narrowed setter round-trips");
        AssertEqual(-12345L, (long)budget.CursorNative, "native getter sees the narrowed write");

        // The native setter reaches values the narrowed one cannot express; reading it back
        // through the narrowed accessor reports rather than truncating.
        // Hoisted out of the assignment: a constant expression cast to `nint` is a compile
        // error when it may not fit the target's width.
        long twoToThe31 = 2147483648L;
        budget.CursorNative = (nint)twoToThe31;
        AssertEqual("2147483648", Functions.ReadCursorAsString(budget),
            "Swift received the full-width write");
        AssertEqual(2147483648L, (long)budget.CursorNative, "native getter round-trips 2^31");
        AssertThrows<OverflowException>(() => { _ = budget.Cursor; },
            "narrowed getter reports the out-of-range cursor");
    }

    public void TestWidthGate_AllThreeInitializerLanes_ShareTheSameArgumentRange()
    {
        // Constructor lane, recovered static-factory lane (a labelled init whose projected C#
        // constructor signature was already taken), and failable-factory lane. All three exist and
        // carry a value to the same place; which overload the compiler picks is pinned by the
        // emitter's unit tests, since C# would widen these literals on its own regardless.
        using var viaConstructor = new WidthGate(7);
        using var viaFactory = WidthGate.CreateWithThreshold(9);
        AssertTrue(WidthGate.TryCreate(11u, out var viaFailable), "failable factory succeeds");
        using (viaFailable)
        {
            AssertEqual(7, viaConstructor.Capacity, "constructor lane capacity");
            AssertEqual(9, viaFactory.Threshold, "recovered-factory lane threshold");
            AssertEqual(11u, viaFailable.Ceiling, "failable-factory lane ceiling");
        }

        AssertFalse(WidthGate.TryCreate(0u, out _), "failable factory reports the nil case");
    }

    public void TestWidthGate_MethodReturns_KeepTheirNativeWidth()
    {
        // Narrowing a method return would bias C# overload resolution toward a truncating `int`
        // overload for literal arguments, so returns are left alone. `nint` here is the point.
        using var gate = new WidthGate(int.MaxValue);

        nint doubled = gate.GetWidenedCapacity();
        AssertEqual(4294967294L, (long)doubled, "method return carries the full 64-bit product");

        nint huge = Functions.GetHugeNativeCount();
        AssertEqual(2147483652L, (long)huge, "free-function return carries the full-width value");
    }

    public void TestPermissionMask_RawValueAboveNarrowedRange()
    {
        // An OptionSet's raw value narrows exactly like any other property, so an option above
        // bit 31 is unreadable through RawValue but whole through RawValueNative — and the set's
        // own operators keep working, because they read the native-width accessor.
        var mask = PermissionMask.ReadData | PermissionMask.AuditTrail;

        AssertTrue(mask.Contains(PermissionMask.AuditTrail), "high-bit option survives the union");
        AssertTrue(mask.Contains(PermissionMask.ReadData), "low-bit option survives the union");
        AssertEqual("readData, auditTrail", TestLibFunctions.DescribePermissionMask(mask),
            "Swift sees both options");
        AssertEqual((1L << 40) | 1L, (long)mask.RawValueNative, "RawValueNative carries both bits");
        AssertThrows<OverflowException>(() => { _ = mask.RawValue; },
            "RawValue reports the bits it cannot represent");

        var lowOnly = PermissionMask.ReadData | PermissionMask.Share;
        AssertEqual(5, lowOnly.RawValue, "RawValue still reads a value inside its range");
    }
}
