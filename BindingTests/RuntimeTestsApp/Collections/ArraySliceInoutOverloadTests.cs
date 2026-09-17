// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// ArraySlice overloads that also take <c>inout</c> parameters. The slice wrapper normalizes the
/// slice to an Array; the inout arguments have to survive that forwarding as writable references, so
/// state carried across calls (and a second out-flag on the longer overload) comes back to C#.
/// </summary>
public class ArraySliceInoutOverloadTests : TestBase
{
    public ArraySliceInoutOverloadTests(TestResults results) : base(results) { }

    public void TestInoutStateCarriesAcrossCalls()
    {
        using var text = new SliceTextAccumulator();
        bool lastWasWhite = false;

        SliceWhitespace.Normalise(text, Encoding.ASCII.GetBytes("  a "), true, ref lastWasWhite);
        AssertEqual("a ", text.Text, "leading spaces stripped, trailing space kept");
        AssertTrue(lastWasWhite, "the trailing space is written back");

        SliceWhitespace.Normalise(text, Encoding.ASCII.GetBytes(" b"), false, ref lastWasWhite);
        AssertEqual("a b", text.Text, "the space run continuing from the previous call collapses");
        AssertFalse(lastWasWhite, "the last byte was not a space");
    }

    public void TestLongerOverloadWritesBothInoutFlags()
    {
        using var text = new SliceTextAccumulator();
        bool lastWasWhite = true;
        bool sawWhite = false;

        SliceWhitespace.Normalise(text, Encoding.ASCII.GetBytes(" x y"), false, ref lastWasWhite, ref sawWhite);

        AssertEqual("x y", text.Text, "the incoming lastWasWhite suppresses the first space");
        AssertFalse(lastWasWhite, "the last byte was not a space");
        AssertTrue(sawWhite, "the second inout reports the spaces");
    }
}
