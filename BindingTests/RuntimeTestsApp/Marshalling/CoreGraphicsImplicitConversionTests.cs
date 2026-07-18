// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// A Swift-ABI-bound library surfaces CoreGraphics geometry as the portable <c>Swift.CGPoint</c> /
/// <c>Swift.CGSize</c> / <c>Swift.CGRect</c> structs, while ordinary UIKit/AppKit APIs want the
/// platform <c>CoreGraphics.*</c> structs. The runtime ships hand-written implicit conversions between
/// the two families (Apple-TFM-conditional) so a consumer can cross that boundary without a manual
/// field-by-field copy. Those operators had no coverage anywhere in the repo; these tests exercise the
/// round trip in both directions and assert value preservation.
/// </summary>
public class CoreGraphicsImplicitConversionTests : TestBase
{
    public CoreGraphicsImplicitConversionTests(TestResults results) : base(results) { }

    public void TestCGPoint_ImplicitConversion_RoundTrips()
    {
        var swiftPoint = new Swift.CGPoint(3.0, 4.0);

        // Swift.CGPoint → CoreGraphics.CGPoint (the shape a UIKit `Center =` assignment needs).
        CoreGraphics.CGPoint cg = swiftPoint;
        AssertApproxEqual(3.0, cg.X, message: "Swift→CoreGraphics CGPoint.X preserved");
        AssertApproxEqual(4.0, cg.Y, message: "Swift→CoreGraphics CGPoint.Y preserved");

        // CoreGraphics.CGPoint → Swift.CGPoint (feeding a platform value back into a Swift-bound API).
        Swift.CGPoint back = cg;
        AssertApproxEqual(3.0, back.X, message: "CoreGraphics→Swift CGPoint.X preserved");
        AssertApproxEqual(4.0, back.Y, message: "CoreGraphics→Swift CGPoint.Y preserved");
    }

    public void TestCGSize_ImplicitConversion_RoundTrips()
    {
        var swiftSize = new Swift.CGSize(12.0, 34.0);

        CoreGraphics.CGSize cg = swiftSize;
        AssertApproxEqual(12.0, cg.Width, message: "Swift→CoreGraphics CGSize.Width preserved");
        AssertApproxEqual(34.0, cg.Height, message: "Swift→CoreGraphics CGSize.Height preserved");

        Swift.CGSize back = cg;
        AssertApproxEqual(12.0, back.Width, message: "CoreGraphics→Swift CGSize.Width preserved");
        AssertApproxEqual(34.0, back.Height, message: "CoreGraphics→Swift CGSize.Height preserved");
    }

    public void TestCGRect_ImplicitConversion_RoundTrips()
    {
        var swiftRect = new Swift.CGRect(1.0, 2.0, 30.0, 40.0);

        CoreGraphics.CGRect cg = swiftRect;
        AssertApproxEqual(1.0, cg.X, message: "Swift→CoreGraphics CGRect.X preserved");
        AssertApproxEqual(2.0, cg.Y, message: "Swift→CoreGraphics CGRect.Y preserved");
        AssertApproxEqual(30.0, cg.Width, message: "Swift→CoreGraphics CGRect.Width preserved");
        AssertApproxEqual(40.0, cg.Height, message: "Swift→CoreGraphics CGRect.Height preserved");

        Swift.CGRect back = cg;
        AssertApproxEqual(1.0, back.X, message: "CoreGraphics→Swift CGRect.X preserved");
        AssertApproxEqual(2.0, back.Y, message: "CoreGraphics→Swift CGRect.Y preserved");
        AssertApproxEqual(30.0, back.Width, message: "CoreGraphics→Swift CGRect.Width preserved");
        AssertApproxEqual(40.0, back.Height, message: "CoreGraphics→Swift CGRect.Height preserved");
    }
}
