// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if UIKIT_RAW_VALUES
using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using UIKit;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// UIKit typedefs Swift imports as structs over a raw value, on every route a binding reaches Swift
/// by: <c>UIKitRawValues</c> through its <c>@_cdecl</c> wrappers, <c>UIKitRawValueBox</c> (generic over
/// a protocol-constrained parameter and an Error) through direct Swift-convention transitions a native
/// bridge carries, and the box's concrete specializations. UIFont.Weight is a CGFloat, UILayoutPriority
/// a Float, UIFont.TextStyle and UIContentSizeCategory an NSString; the platform binding's types of the
/// same names number the constants instead. Each value is computed in Swift from what C# passed, so a
/// value carried in the wrong register or with the wrong width shows.
/// </summary>
public class UIKitRawValueTests : TestBase
{
    public UIKitRawValueTests(TestResults results) : base(results) { }

    // UIFontWeightBold as UIKit stores it: the Float 0.4 widened to a CGFloat (0.4000000059604645).
    private const double Bold = (double)0.4f;

    private static UIKitRawValueBox<KeyedToken, DirectTransitionFailure> MakeBox(int key)
    {
        using var token = new KeyedToken(key);
        return new UIKitRawValueBox<KeyedToken, DirectTransitionFailure>(token);
    }

    private static NSString Style(UIFontTextStyle style) => style.GetConstant()!;

    private static NSString Category(UIContentSizeCategory category) => category.GetConstant()!;

    // ---- the wrapper route ----

    public void TestWeightParameterAndResultThroughWrappers()
    {
        using var values = new UIKitRawValues();
        AssertApproxEqual(Bold, values.GetBoldWeight(), 1e-9, "boldWeight() is .bold");
        AssertApproxEqual(0.15, values.EchoWeight(0.15), 1e-9, "echoWeight(_:) returns the weight it was passed");
        AssertApproxEqual(0.15, values.RawValue(0.15), 1e-9, "rawValue(ofWeight:) reads the CGFloat C# passed");
        AssertApproxEqual(Bold, values.RawValue(values.GetBoldWeight()), 1e-9, ".bold round-trips");
    }

    public void TestWeightProperty()
    {
        using var values = new UIKitRawValues();
        AssertApproxEqual(0.0, values.Weight, 1e-9, "weight starts at .regular");
        values.Weight = 0.15;
        AssertApproxEqual(0.15, values.Weight, 1e-9, "weight reads back what was set");
        values.Weight = Bold;
        AssertApproxEqual(Bold, values.RawValue(values.Weight), 1e-9, "a stored .bold is .bold in Swift");
    }

    public void TestOptionalWeightThroughWrappers()
    {
        using var values = new UIKitRawValues();
        var bold = values.MaybeBoldWeight(true);
        AssertTrue(bold.HasValue, "maybeBoldWeight(true) has a value");
        AssertApproxEqual(Bold, bold!.Value, 1e-9, "maybeBoldWeight(true) is .bold");
        AssertFalse(values.MaybeBoldWeight(false).HasValue, "maybeBoldWeight(false) is nil");
        AssertApproxEqual(0.15, values.RawValue((double?)0.15), 1e-9, "rawValue(ofOptionalWeight:) reads a present weight");
        AssertApproxEqual(-9.0, values.RawValue((double?)null), 1e-9, "rawValue(ofOptionalWeight:) sees nil");
    }

    public void TestLayoutPriorityThroughWrappers()
    {
        using var values = new UIKitRawValues();
        AssertApproxEqual(999.0, values.Lowered(1000f), 1e-6, "lowered(_:) reads the Float C# passed");
        AssertApproxEqual(749.5, values.Lowered(750.5f), 1e-6, "a fractional priority keeps its fraction");
    }

    public void TestTextStyleThroughWrappers()
    {
        using var values = new UIKitRawValues();
        var body = Style(UIFontTextStyle.Body);
        using var echoed = values.EchoStyle(body);
        AssertEqual(body.ToString(), echoed.ToString(), "echoStyle(_:) returns the style it was passed");
        using var title = values.MaybeTitleStyle(true);
        AssertNotNull(title, "maybeTitleStyle(true) has a value");
        AssertEqual(Style(UIFontTextStyle.Title1).ToString(), title!.ToString(), "maybeTitleStyle(true) is .title1");
        AssertNull(values.MaybeTitleStyle(false), "maybeTitleStyle(false) is nil");
        AssertEqual(body.ToString(), values.Name(body), "name(ofStyle:) reads a present style");
        AssertEqual("none", values.Name(null), "name(ofStyle:) sees nil");
    }

    public void TestContentSizeCategoryThroughWrappers()
    {
        using var values = new UIKitRawValues();
        using var larger = values.Larger(Category(UIContentSizeCategory.Large));
        AssertEqual(Category(UIContentSizeCategory.ExtraLarge).ToString(), larger.ToString(), "larger(.large) is .extraLarge");
        using var same = values.Larger(Category(UIContentSizeCategory.Small));
        AssertEqual(Category(UIContentSizeCategory.Small).ToString(), same.ToString(), "larger(_:) keeps any other category");
    }

    // ---- the direct route a bridge carries ----

    public void TestWeightOnTheGenericBox()
    {
        using var box = MakeBox(5);
        AssertApproxEqual(Bold, box.GetBoldWeight(), 1e-9, "boldWeight() is .bold");
        AssertApproxEqual(0.15, box.RawValue(0.15), 1e-9, "rawValue(ofWeight:) reads the CGFloat C# passed");
        AssertApproxEqual(0.2, box.Raised(0.15), 1e-9, "raised(_:) adds a hundredth of the key");
        AssertApproxEqual(Bold + 0.05, box.Raised(box.GetBoldWeight()), 1e-9, ".bold round-trips through raised(_:)");
    }

    public void TestLayoutPriorityOnTheGenericBox()
    {
        using var box = MakeBox(3);
        AssertApproxEqual(997.0, box.Lowered(1000f), 1e-6, "lowered(_:) subtracts the key from the Float C# passed");
    }

    public void TestOptionalTextStyleOnTheGenericBox()
    {
        using var box = MakeBox(2);
        for (int i = 0; i < 3; i++)
        {
            using var title = box.MaybeTitleStyle(true);
            AssertNotNull(title, $"maybeTitleStyle(true) has a value (call {i})");
            AssertEqual(Style(UIFontTextStyle.Title1).ToString(), title!.ToString(), $"maybeTitleStyle(true) is .title1 (call {i})");
            AssertNull(box.MaybeTitleStyle(false), $"maybeTitleStyle(false) is nil (call {i})");
        }
        var headline = Style(UIFontTextStyle.Headline);
        AssertEqual(headline.ToString(), box.Name(headline), "name(ofStyle:) reads a present style");
        AssertEqual("none", box.Name(null), "name(ofStyle:) sees nil");
    }

    // ---- the concrete specialization ----

    public void TestTextStyleAndCategoryOnTheSpecialization()
    {
        using var box = MakeBox(1);
        using var headline = box.Toggled(Style(UIFontTextStyle.Body));
        AssertEqual(Style(UIFontTextStyle.Headline).ToString(), headline.ToString(), "toggled(.body) is .headline");
        using var body = box.Toggled(headline);
        AssertEqual(Style(UIFontTextStyle.Body).ToString(), body.ToString(), "toggled(.headline) is .body");
        using var larger = box.Larger(Category(UIContentSizeCategory.Large));
        AssertEqual(Category(UIContentSizeCategory.ExtraLarge).ToString(), larger.ToString(), "larger(.large) is .extraLarge");
    }
}
#endif
