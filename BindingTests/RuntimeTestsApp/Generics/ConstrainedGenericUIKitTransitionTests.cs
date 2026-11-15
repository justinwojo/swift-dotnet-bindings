// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if UIKIT_TRANSITIONS
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using UIKit;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// UIKit values through the direct Swift-convention transitions of a struct generic over a
/// protocol-constrained parameter and an Error (<c>UIKitTransitionBox</c>). A native bridge carries
/// such a transition only when the call facts know every type in it, and these UIKit types are not
/// in the bundled UIKit database: Objective-C classes (UIFont, UIBezierPath), which cross as one
/// object reference, and UIKit's imported C structs and option sets, which cross by their fixed
/// layouts. Each call checks the value Swift computed from what C# passed, and the object
/// references are taken and returned repeatedly so an unbalanced retain or release shows.
/// A member left on its guarded direct import would throw the guard's refusal on Mono and the
/// CoreCLR interpreter, so on those runtimes each call is also the evidence that a bridge carried it.
/// </summary>
public class ConstrainedGenericUIKitTransitionTests : TestBase
{
    public ConstrainedGenericUIKitTransitionTests(TestResults results) : base(results) { }

    private static UIKitTransitionBox<KeyedToken, DirectTransitionFailure> MakeBox(int key)
    {
        using var token = new KeyedToken(key);
        return new UIKitTransitionBox<KeyedToken, DirectTransitionFailure>(token);
    }

    public void TestObjCClassParameterAndResult()
    {
        using var box = MakeBox(4);
        using var font = UIFont.SystemFontOfSize(10) ?? throw new System.InvalidOperationException("UIKit returned no system font");
        for (int i = 0; i < 5; i++)
        {
            AssertApproxEqual(14.0, box.PointSize(font), 0.0001, $"pointSize(of:) reads the font C# passed (call {i})");
            using var scaled = box.Scaled(font);
            AssertApproxEqual(14.0, (double)scaled.PointSize, 0.0001, $"scaled(_:) returns a font Swift resized (call {i})");
        }
        AssertApproxEqual(10.0, (double)font.PointSize, 0.0001, "the font C# passed is untouched");
    }

    public void TestObjCClassResultFromConcreteSpecialization()
    {
        using var box = MakeBox(13);
        for (int i = 0; i < 5; i++)
        {
            using var font = box.Font();
            AssertApproxEqual(13.0, (double)font.PointSize, 0.0001, $"font() returns the font Swift built (call {i})");
            using var path = box.Path();
            AssertApproxEqual(13.0, (double)path.Bounds.Width, 0.0001, $"path() returns the path Swift built (call {i})");
        }
    }

    public void TestOptionalObjCClassResult()
    {
        using var box = MakeBox(17);
        for (int i = 0; i < 3; i++)
        {
            using var font = box.Font(present: true);
            AssertNotNull(font, $"font(present: true) returns a font (call {i})");
            AssertApproxEqual(17.0, (double)font!.PointSize, 0.0001, $"font(present: true) has the key's size (call {i})");
            AssertTrue(box.Font(present: false) is null, $"font(present: false) is nil (call {i})");
        }
    }

    public void TestNonMainActorObjCClassRoundTrip()
    {
        using var box = MakeBox(6);
        using var path = new UIBezierPath();
        path.MoveTo(new CoreGraphics.CGPoint(0, 0));
        path.AddLineTo(new CoreGraphics.CGPoint(20, 10));
        AssertApproxEqual(20.0, box.Width(path), 0.0001, "width(of:) reads the path C# built");
        using var widened = box.Widened(path);
        AssertApproxEqual(26.0, (double)widened.Bounds.Width, 0.0001, "widened(_:) returns a path wider by the key");
        AssertApproxEqual(10.0, (double)widened.Bounds.Height, 0.0001, "widened(_:) keeps the height");
    }

    public void TestEdgeInsetsStructRoundTrip()
    {
        using var box = MakeBox(2);
        var insets = box.GetInsets();
        AssertApproxEqual(2.0, (double)insets.Top, 0.0001, "insets().top");
        AssertApproxEqual(4.0, (double)insets.Left, 0.0001, "insets().left");
        AssertApproxEqual(6.0, (double)insets.Bottom, 0.0001, "insets().bottom");
        AssertApproxEqual(8.0, (double)insets.Right, 0.0001, "insets().right");
        AssertApproxEqual(1.0 + 20.0 + 300.0 + 4000.0, box.Weigh(new UIEdgeInsets(1, 2, 3, 4)), 0.0001,
            "weigh(_:) reads each inset from its own register");
    }

    public void TestOffsetStructResult()
    {
        using var box = MakeBox(5);
        var offset = box.GetOffset();
        AssertApproxEqual(5.0, (double)offset.Horizontal, 0.0001, "offset().horizontal");
        AssertApproxEqual(-5.0, (double)offset.Vertical, 0.0001, "offset().vertical");
    }

    public void TestControlStateOptionSetRoundTrip()
    {
        using var box = MakeBox(1);
        AssertEqual(UIControlState.Highlighted, box.State(selected: false), "state(selected: false) is .highlighted");
        AssertEqual(UIControlState.Highlighted | UIControlState.Selected, box.State(selected: true), "state(selected: true) adds .selected");
        AssertEqual((nuint)(ulong)(UIControlState.Highlighted | UIControlState.Disabled),
            box.RawValue(UIControlState.Highlighted | UIControlState.Disabled), "rawValue(of:) reads the option set C# passed");
    }
}
#endif
