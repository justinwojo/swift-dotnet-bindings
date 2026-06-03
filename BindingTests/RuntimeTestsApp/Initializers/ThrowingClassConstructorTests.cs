// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// P0-05: a throwing CLASS constructor places the swifterror-out pointer in the FIRST integer
/// register, AHEAD of the value arguments — so on the cdecl side <c>lo</c>/<c>hi</c> shift up one
/// register. The thunk must capture the error-out from the leading register and shift the value
/// arguments back down for swiftcc. The earlier bug read the error pointer as the first value
/// argument (and never captured swifterror): on the success path it corrupted <c>lo</c>/<c>hi</c>,
/// and on the failure path it dropped the thrown error entirely.
///
/// <see cref="ValidatedConfig"/> in <see cref="InitializerTests"/> covers a throwing STRUCT init
/// (indirect-return self, different register layout). <c>RangeBox</c> is the distinct class shape
/// where the error-out leads two by-value arguments. Both the value round-trip (success) and the
/// error description (failure) are asserted, because each catches a different half of the bug.
/// </summary>
public class ThrowingClassConstructorTests : TestBase
{
    public ThrowingClassConstructorTests(TestResults results) : base(results) { }

    public void TestRangeBoxValidConstructionPreservesArgs()
    {
        // Success path: lo/hi must survive the error-leads register shift. A mis-shifted thunk would
        // read garbage (or the error pointer's bits) into one of them.
        using var box = new RangeBox(3, 11);
        AssertEqual(3, box.Lo, "lo survived the error-leads register shift");
        AssertEqual(11, box.Hi, "hi survived the error-leads register shift");
        AssertEqual(8, box.GetSpan(), "span = hi - lo = 8 (both args intact)");
        TestLogger.Info($"RangeBox(3, 11) → lo={box.Lo}, hi={box.Hi}, span={box.GetSpan()}");
    }

    public void TestRangeBoxBoundaryEqualLoHi()
    {
        // lo == hi is the success boundary (guard is lo <= hi). Confirms no off-by-one in the shift.
        using var box = new RangeBox(5, 5);
        AssertEqual(5, box.Lo, "lo == hi boundary: lo preserved");
        AssertEqual(5, box.Hi, "lo == hi boundary: hi preserved");
        AssertEqual(0, box.GetSpan(), "span = 0 at lo == hi");
    }

    public void TestRangeBoxInvalidRangeThrowsAndCapturesError()
    {
        // Failure path: lo > hi throws BoundsError.invalidRange. The thrown error must reach C# — the
        // earlier bug dropped it because swifterror was never captured from the leading register.
        SwiftException? caught = null;
        try
        {
            using var box = new RangeBox(11, 3);
        }
        catch (SwiftException ex)
        {
            caught = ex;
        }

        AssertNotNull(caught, "RangeBox(11, 3) must throw because lo > hi");
        AssertTrue(caught!.Message.Contains("invalidRange"),
            $"thrown BoundsError.invalidRange must survive the error-leads capture, got: {caught.Message}");
        TestLogger.Info($"RangeBox(11, 3) threw: {caught.Message}");
    }
}
