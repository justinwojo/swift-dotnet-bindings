// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// A <c>@frozen</c> struct storing a non-frozen enum — directly or as an Optional's payload — is
/// address-only to clients: Swift returns it through the indirect-result register and takes it, as an
/// argument or as <c>self</c>, by address, however few bytes it occupies in the defining module. Each
/// test hands such a struct across in both directions and has Swift name what it holds, so a crossing
/// that moved it in registers shows as a wrong value or a fault. <see cref="FrozenFieldPair"/>, the
/// same shape over a frozen enum, is the loadable control. Fixture: Types/FrozenResilientField.swift.
/// </summary>
public class FrozenResilientFieldTests : TestBase
{
    public FrozenResilientFieldTests(TestResults results) : base(results) { }

    public void TestASingleFieldStructCrossesByAddress()
    {
        using var fromSwift = TestLibFunctions.MakeResilientFieldCell(ResilientFieldMode.Stopped);
        AssertEqual(ResilientFieldMode.Stopped, fromSwift.Mode, "C# reads the returned field");
        AssertEqual("stopped", TestLibFunctions.ResilientFieldCellName(fromSwift), "Swift reads the returned struct back");

        using var fromCSharp = new ResilientFieldCell(ResilientFieldMode.Running);
        AssertEqual("running", TestLibFunctions.ResilientFieldCellName(fromCSharp), "Swift reads a C#-built struct");
    }

    public void TestATwoFieldStructCrossesByAddress()
    {
        using var fromSwift = TestLibFunctions.MakeResilientFieldPair(ResilientFieldMode.Running, 41);
        AssertEqual(ResilientFieldMode.Running, fromSwift.Mode, "C# reads the returned enum field");
        AssertEqual(41, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual("runningx41", TestLibFunctions.ResilientFieldPairName(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.ResilientFieldPairEcho(fromSwift);
        AssertEqual("runningx41", echoed.Summary, "a by-value argument returned unchanged");

        using var fromCSharp = new ResilientFieldPair(ResilientFieldMode.Stopped, 7);
        AssertEqual("stoppedx7", TestLibFunctions.ResilientFieldPairName(fromCSharp), "Swift reads a C#-built struct");
        fromCSharp.Mode = ResilientFieldMode.Idle;
        fromCSharp.Count = 9;
        AssertEqual("idlex9", TestLibFunctions.ResilientFieldPairName(fromCSharp), "Swift reads C#'s field writes");
    }

    public void TestATwoFieldStructCrossesAsSelf()
    {
        using var pair = new ResilientFieldPair(ResilientFieldMode.Running, 5);
        AssertEqual("runningx5", pair.Summary, "a getter on self");
        pair.Advance(3);
        AssertEqual("runningx8", pair.Summary, "a mutating method on self");
        using var scaled = pair.Scaled(4);
        AssertEqual("runningx32", scaled.Summary, "a method returning the struct");
        AssertEqual("runningx8", pair.Summary, "the receiver is unchanged");
    }

    public void TestAnOptionalPayloadFieldCrossesByAddress()
    {
        using var some = TestLibFunctions.MakeResilientFieldMaybe(ResilientFieldMode.Stopped, 2);
        AssertEqual<ResilientFieldMode?>(ResilientFieldMode.Stopped, some.Mode, "C# reads the returned payload");
        AssertEqual("stoppedx2", TestLibFunctions.ResilientFieldMaybeName(some), "Swift reads the returned struct back");

        using var none = TestLibFunctions.MakeResilientFieldMaybe(null, 6);
        AssertEqual<ResilientFieldMode?>(null, none.Mode, "C# reads the returned nil");
        AssertEqual("nonex6", TestLibFunctions.ResilientFieldMaybeName(none), "Swift reads nil back");

        using var fromCSharp = new ResilientFieldMaybe(ResilientFieldMode.Idle, 1);
        AssertEqual("idlex1", TestLibFunctions.ResilientFieldMaybeName(fromCSharp), "Swift reads a C#-built struct");
    }

    public void TestTheFrozenEnumControlStaysLoadable()
    {
        using var fromSwift = TestLibFunctions.MakeFrozenFieldPair(FrozenFieldMode.Stopped, 12);
        AssertEqual(FrozenFieldMode.Stopped, fromSwift.Mode, "C# reads the returned enum field");
        AssertEqual(12, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual("stoppedx12", TestLibFunctions.FrozenFieldPairName(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.FrozenFieldPairEcho(fromSwift);
        AssertEqual("stoppedx12", TestLibFunctions.FrozenFieldPairName(echoed), "a by-value argument returned unchanged");
    }
}
