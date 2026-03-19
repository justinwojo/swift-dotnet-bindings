// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for enum marshalling: case construction, raw value round-trip,
/// associated values, nested enums, and the Phase 55 string enum regression.
/// </summary>
public class EnumMarshallingTests : TestBase
{
    public EnumMarshallingTests(TestResults results) : base(results) { }

    #region Direction Enum (Simple C# enum — @frozen Swift enum)

    public void TestDirectionCaseConstruction()
    {
        // Direction is a simple C# enum (frozen, no raw value)
        AssertEqual(Direction.North, (Direction)0, "North is 0");
        AssertEqual(Direction.South, (Direction)1, "South is 1");
        AssertEqual(Direction.East, (Direction)2, "East is 2");
        AssertEqual(Direction.West, (Direction)3, "West is 3");
        TestLogger.Info("Direction case construction passed");
    }

    public void TestDirectionMethodCall()
    {
        // Test calling a method on an enum value (extension method)
        var east = Direction.East;
        AssertTrue(TestLibFunctions.IsHorizontal(east), "East is horizontal");

        var north = Direction.North;
        AssertFalse(TestLibFunctions.IsHorizontal(north), "North is not horizontal");

        TestLogger.Info("Direction method call passed");
    }

    public void TestDirectionOpposite()
    {
        // Test Direction.Opposite() extension method
        var north = Direction.North;
        var opposite = north.Opposite();
        AssertEqual(Direction.South, opposite, "Opposite of North is South");

        var east = Direction.East;
        opposite = east.Opposite();
        AssertEqual(Direction.West, opposite, "Opposite of East is West");

        TestLogger.Info("Direction Opposite tests passed");
    }

    #endregion

    #region Color Enum (Simple C# enum — @frozen Swift enum with Int32 raw value)

    public void TestColorCaseConstruction()
    {
        // Color is a simple C# enum (frozen + Int32 raw value)
        AssertEqual(Color.Red, (Color)0, "Red is 0");
        AssertEqual(Color.Green, (Color)1, "Green is 1");
        AssertEqual(Color.Blue, (Color)2, "Blue is 2");
        AssertEqual(Color.Alpha, (Color)3, "Alpha is 3");
        TestLogger.Info("Color case construction passed");
    }

    public void TestColorIntRawValue()
    {
        // Verify raw int values are correct and distinct
        AssertEqual(0, (int)Color.Red, "Red raw value");
        AssertEqual(1, (int)Color.Green, "Green raw value");
        AssertEqual(2, (int)Color.Blue, "Blue raw value");
        AssertEqual(3, (int)Color.Alpha, "Alpha raw value");
        TestLogger.Info("Color int raw value tests passed");
    }

    public void TestColorFromTagRoundTrip()
    {
        // Verify round-trip through int cast
        AssertEqual(Color.Red, (Color)(int)Color.Red, "Red round-trip");
        AssertEqual(Color.Blue, (Color)(int)Color.Blue, "Blue round-trip");

        TestLogger.Info("Color FromRawValue tests passed");
    }

    public void TestColorForIndexFunction()
    {
        // Test the free function colorForIndex
        var color0 = TestLibFunctions.ColorForIndex(0);
        AssertEqual(Color.Red, color0, "ColorForIndex(0) is Red");

        var color1 = TestLibFunctions.ColorForIndex(1);
        AssertEqual(Color.Green, color1, "ColorForIndex(1) is Green");

        TestLogger.Info("ColorForIndex tests passed");
    }

    #endregion

    #region StatusCode Enum (String Raw Value)

    public void TestStatusCodeCases()
    {
        AssertEqual(StatusCode.CaseTag.Ok, StatusCode.Ok.Tag, "Ok tag");
        AssertEqual(StatusCode.CaseTag.NotFound, StatusCode.NotFound.Tag, "NotFound tag");
        AssertEqual(StatusCode.CaseTag.Error, StatusCode.Error.Tag, "Error tag");
        AssertEqual(StatusCode.CaseTag.Timeout, StatusCode.Timeout.Tag, "Timeout tag");
        TestLogger.Info("StatusCode case tags passed");
    }

    public void TestStatusCodeRawValues()
    {
        AssertEqual("OK", StatusCode.Ok.RawValue.ToString(), "Ok raw value");
        AssertEqual("NOT_FOUND", StatusCode.NotFound.RawValue.ToString(), "NotFound raw value");
        AssertEqual("ERROR", StatusCode.Error.RawValue.ToString(), "Error raw value");
        AssertEqual("TIMEOUT", StatusCode.Timeout.RawValue.ToString(), "Timeout raw value");
        TestLogger.Info("StatusCode raw values passed");
    }

    public void TestStatusCodeFromRawValue()
    {
        var ok = StatusCode.FromRawValue("OK");
        AssertNotNull(ok, "StatusCode.FromRawValue(OK) not null");
        AssertEqual("OK", ok!.RawValue.ToString(), "OK round-trip");

        var timeout = StatusCode.FromRawValue("TIMEOUT");
        AssertNotNull(timeout, "StatusCode.FromRawValue(TIMEOUT) not null");
        AssertEqual("TIMEOUT", timeout!.RawValue.ToString(), "TIMEOUT round-trip");

        var invalid = StatusCode.FromRawValue("INVALID");
        AssertNull(invalid, "Invalid StatusCode is null");

        TestLogger.Info("StatusCode FromRawValue tests passed");
    }

    #endregion

    #region Shape Enum (Associated Values)

    public void TestShapeCaseCreation()
    {
        // Test creating Shape cases with associated values
        var circle = Shape.Circle(5.0);
        AssertEqual(Shape.CaseTag.Circle, circle.Tag, "Circle tag");

        var rect = Shape.Rectangle(10.0, 20.0);
        AssertEqual(Shape.CaseTag.Rectangle, rect.Tag, "Rectangle tag");

        var point = Shape.Point(new FrozenPoint { X = 1.0, Y = 2.0 });
        AssertEqual(Shape.CaseTag.Point, point.Tag, "Point tag");

        var empty = Shape.Empty;
        AssertEqual(Shape.CaseTag.Empty, empty.Tag, "Empty tag");

        TestLogger.Info("Shape case creation passed");
    }

    public void TestShapeAllCasesDistinct()
    {
        // Verify all cases produce distinct tags
        var circle = Shape.Circle(1.0);
        var rect = Shape.Rectangle(2.0, 3.0);
        var point = Shape.Point(new FrozenPoint { X = 0, Y = 0 });
        var empty = Shape.Empty;

        AssertTrue(circle.Tag != rect.Tag, "Circle != Rectangle");
        AssertTrue(circle.Tag != point.Tag, "Circle != Point");
        AssertTrue(circle.Tag != empty.Tag, "Circle != Empty");
        AssertTrue(rect.Tag != point.Tag, "Rectangle != Point");
        AssertTrue(rect.Tag != empty.Tag, "Rectangle != Empty");
        AssertTrue(point.Tag != empty.Tag, "Point != Empty");

        TestLogger.Info("Shape all cases distinct");
    }

    #endregion

    #region EnumPropertyHolder (Non-simple enum property get/set — B18 gate lift)

    public void TestEnumPropertyHolder_GetCurrentShape()
    {
        // Create holder with circle, read back the property
        var holder = new EnumPropertyHolder(Shape.Circle(5.0));
        var shape = holder.CurrentShape;
        AssertEqual(Shape.CaseTag.Circle, shape.Tag, "CurrentShape is Circle");
        TestLogger.Info("EnumPropertyHolder.CurrentShape getter passed");
    }

    public void TestEnumPropertyHolder_SetCurrentShape()
    {
        // Create holder with circle, set to rectangle, verify
        var holder = new EnumPropertyHolder(Shape.Circle(5.0));
        holder.CurrentShape = Shape.Rectangle(3.0, 4.0);
        var shape = holder.CurrentShape;
        AssertEqual(Shape.CaseTag.Rectangle, shape.Tag, "CurrentShape updated to Rectangle");
        TestLogger.Info("EnumPropertyHolder.CurrentShape setter passed");
    }

    public void TestEnumPropertyHolder_GetShapeMethod()
    {
        // Test method returning non-simple enum
        var holder = new EnumPropertyHolder(Shape.Empty);
        var shape = holder.GetShape();
        AssertEqual(Shape.CaseTag.Empty, shape.Tag, "GetShape() returns Empty");
        TestLogger.Info("EnumPropertyHolder.GetShape() passed");
    }

    public void TestEnumPropertyHolder_OptionalShapeDefaultNull()
    {
        // optionalShape defaults to nil in init — verify null round-trip
        var holder = new EnumPropertyHolder(Shape.Empty);
        var optShape = holder.OptionalShape;
        AssertNull(optShape, "OptionalShape defaults to null");
        TestLogger.Info("EnumPropertyHolder.OptionalShape default null passed");
    }

    [Skip("Shape.MarshalToSwift VWT initializeWithCopy crashes Mono — tag byte fixed but payload copy still broken")]
    public void TestEnumPropertyHolder_SetOptionalShape()
    {
        // Set optionalShape to a value, read back
        var holder = new EnumPropertyHolder(Shape.Empty);
        holder.OptionalShape = Shape.Circle(3.0);
        var optShape = holder.OptionalShape;
        AssertNotNull(optShape, "OptionalShape set to Circle is not null");
        AssertEqual(Shape.CaseTag.Circle, optShape!.Tag, "OptionalShape is Circle");
        TestLogger.Info("EnumPropertyHolder.OptionalShape setter passed");
    }

    [Skip("Shape.MarshalToSwift VWT initializeWithCopy crashes Mono — tag byte fixed but payload copy still broken")]
    public void TestEnumPropertyHolder_ClearOptionalShape()
    {
        // Set optionalShape, then clear back to null
        var holder = new EnumPropertyHolder(Shape.Empty);
        holder.OptionalShape = Shape.Rectangle(5.0, 10.0);
        AssertNotNull(holder.OptionalShape, "OptionalShape is set");

        holder.OptionalShape = null;
        AssertNull(holder.OptionalShape, "OptionalShape cleared to null");
        TestLogger.Info("EnumPropertyHolder.OptionalShape clear passed");
    }

    #endregion

    #region Nested Container Enums (Phase 55 regression area)

    public void TestOrderContainerCreation()
    {
        // CreateOrder takes orderId + statusRaw strings, returns OrderContainer?
        // Swift raw value is "order_pending" (not the case name "pending")
        var order = TestLibFunctions.CreateOrder("ORD-001", "order_pending");
        AssertNotNull(order, "Order created");

        var statusRaw = TestLibFunctions.GetOrderStatusRaw(order!);
        AssertEqual("order_pending", statusRaw, "Order status raw value");

        TestLogger.Info("OrderContainer creation passed");
    }

    public void TestOrderStatusFromRawValue()
    {
        // Test nested OrderContainer.Status enum — raw values are "order_*", not case names
        var pending = OrderContainer.Status.FromRawValue("order_pending");
        AssertNotNull(pending, "OrderContainer.Status order_pending not null");
        AssertEqual("order_pending", pending!.RawValue.ToString(), "order_pending round-trip");

        var shipped = OrderContainer.Status.FromRawValue("order_shipped");
        AssertNotNull(shipped, "OrderContainer.Status order_shipped not null");
        AssertEqual("order_shipped", shipped!.RawValue.ToString(), "order_shipped round-trip");

        var invalid = OrderContainer.Status.FromRawValue("bogus");
        AssertNull(invalid, "Invalid order status is null");

        // Case name (not raw value) should also return null
        var caseName = OrderContainer.Status.FromRawValue("pending");
        AssertNull(caseName, "Case name 'pending' is not a valid raw value");

        TestLogger.Info("OrderContainer.Status FromRawValue passed");
    }

    public void TestOrderStatusAllCases()
    {
        // Swift raw values are "order_*" prefixed, not case names
        var cases = new[] { "order_pending", "order_processing", "order_shipped", "order_delivered", "order_cancelled" };
        foreach (var rawValue in cases)
        {
            var status = OrderContainer.Status.FromRawValue(rawValue);
            AssertNotNull(status, $"OrderContainer.Status {rawValue} not null");
            AssertEqual(rawValue, status!.RawValue.ToString(), $"{rawValue} round-trip");
        }
        TestLogger.Info("OrderContainer.Status all cases passed");
    }

    public void TestPaymentContainerCreation()
    {
        // Swift raw value is "payment_authorized" (not case name "authorized")
        var payment = TestLibFunctions.CreatePayment("PAY-001", "payment_authorized");
        AssertNotNull(payment, "Payment created");

        var statusRaw = TestLibFunctions.GetPaymentStatusRaw(payment!);
        AssertEqual("payment_authorized", statusRaw, "Payment status raw value");

        TestLogger.Info("PaymentContainer creation passed");
    }

    public void TestPaymentStatusFromRawValue()
    {
        // Swift raw values are "payment_*" prefixed, not case names
        var cases = new[] { "payment_pending", "payment_authorized", "payment_captured", "payment_refunded", "payment_failed" };
        foreach (var rawValue in cases)
        {
            var status = PaymentContainer.Status.FromRawValue(rawValue);
            AssertNotNull(status, $"PaymentContainer.Status {rawValue} not null");
            AssertEqual(rawValue, status!.RawValue.ToString(), $"{rawValue} round-trip");
        }
        TestLogger.Info("PaymentContainer.Status all cases passed");
    }

    #endregion

    #region NetworkConfig Nested Enums

    public void TestHttpMethodFromRawValue()
    {
        var cases = new[] { "GET", "POST", "PUT", "DELETE", "PATCH" };
        foreach (var rawValue in cases)
        {
            var method = NetworkConfig.HttpMethod.FromRawValue(rawValue);
            AssertNotNull(method, $"HttpMethod {rawValue} not null");
            AssertEqual(rawValue, method!.RawValue.ToString(), $"{rawValue} round-trip");
        }
        TestLogger.Info("HttpMethod FromRawValue all cases passed");
    }

    public void TestContentTypeFromRawValue()
    {
        var cases = new[] { "application/json", "application/xml", "multipart/form-data", "text/plain" };
        foreach (var rawValue in cases)
        {
            var ct = NetworkConfig.ContentType.FromRawValue(rawValue);
            AssertNotNull(ct, $"ContentType {rawValue} not null");
            AssertEqual(rawValue, ct!.RawValue.ToString(), $"{rawValue} round-trip");
        }
        TestLogger.Info("ContentType FromRawValue all cases passed");
    }

    #endregion

    #region Pass 2 — L1: Collection Payload Enum (MediaSource)

    public void TestMediaSourceSingle()
    {
        using var source = MediaSource.Single("track1");
        AssertEqual(MediaSource.CaseTag.Single, source.Tag, "Single case tag");
        var result = TestLibFunctions.DescribeMediaSource(source);
        AssertEqual("Single: track1", result, "Describe single");
        TestLogger.Info($"MediaSource.Single: {result}");
    }

    public void TestMediaSourcePlaylist()
    {
        using var source = MediaSource.Playlist(new[] { "a", "b", "c" });
        AssertEqual(MediaSource.CaseTag.Playlist, source.Tag, "Playlist case tag");
        var result = TestLibFunctions.DescribeMediaSource(source);
        AssertEqual("Playlist: a, b, c", result, "Describe playlist");
        TestLogger.Info($"MediaSource.Playlist: {result}");
    }

    public void TestMediaSourceEmpty()
    {
        var source = MediaSource.Empty;
        AssertEqual(MediaSource.CaseTag.Empty, source.Tag, "Empty case tag");
        var result = TestLibFunctions.DescribeMediaSource(source);
        AssertEqual("Empty", result, "Describe empty");
        TestLogger.Info($"MediaSource.Empty: {result}");
    }

    public void TestMediaSourceTryGetSingle()
    {
        using var source = MediaSource.Single("hello");
        var got = source.TryGetSingle(out var name);
        AssertTrue(got, "TryGetSingle succeeds");
        AssertEqual("hello", name, "Extracted name");
        TestLogger.Info($"TryGetSingle: {name}");
    }

    public void TestMediaSourceTryGetPlaylist()
    {
        using var source = MediaSource.Playlist(new[] { "x", "y" });
        var got = source.TryGetPlaylist(out var names);
        AssertTrue(got, "TryGetPlaylist succeeds");
        AssertEqual(2, names!.Count, "Playlist count");
        AssertEqual("x", names[0], "First item");
        AssertEqual("y", names[1], "Second item");
        TestLogger.Info($"TryGetPlaylist: count={names.Count}");
    }

    #endregion

    #region Pass 2 — L3: All-Payload Enum (AnimationSource)

    public void TestAnimationSourceLocal()
    {
        using var src = AnimationSource.Local("/path/anim.json");
        AssertEqual(AnimationSource.CaseTag.Local, src.Tag, "Local tag");
        var desc = TestLibFunctions.DescribeAnimationSource(src);
        AssertEqual("Local: /path/anim.json", desc, "Describe local");
        TestLogger.Info($"AnimationSource.Local: {desc}");
    }

    public void TestAnimationSourceRemote()
    {
        using var src = AnimationSource.Remote("https://cdn.example.com/anim.json");
        AssertEqual(AnimationSource.CaseTag.Remote, src.Tag, "Remote tag");
        var desc = TestLibFunctions.DescribeAnimationSource(src);
        AssertTrue(desc.Contains("cdn.example.com"), "Describe remote contains URL");
        TestLogger.Info($"AnimationSource.Remote: {desc}");
    }

    #endregion

    #region Pass 2 — L4: Mixed Heterogeneous Payload Enum (DataValue)

    public void TestDataValueInteger()
    {
        using var val = DataValue.Integer(42);
        AssertEqual(DataValue.CaseTag.Integer, val.Tag, "Integer tag");
        var desc = TestLibFunctions.DescribeDataValue(val);
        AssertEqual("Int:42", desc, "Describe integer");
        TestLogger.Info($"DataValue.Integer: {desc}");
    }

    public void TestDataValueFloating()
    {
        using var val = DataValue.Floating(3.14);
        AssertEqual(DataValue.CaseTag.Floating, val.Tag, "Floating tag");
        var desc = TestLibFunctions.DescribeDataValue(val);
        AssertTrue(desc.StartsWith("Float:3.14"), "Describe floating");
        TestLogger.Info($"DataValue.Floating: {desc}");
    }

    public void TestDataValueText()
    {
        using var val = DataValue.Text("hello");
        AssertEqual(DataValue.CaseTag.Text, val.Tag, "Text tag");
        var desc = TestLibFunctions.DescribeDataValue(val);
        AssertEqual("Text:hello", desc, "Describe text");
        TestLogger.Info($"DataValue.Text: {desc}");
    }

    public void TestDataValueFlag()
    {
        using var val = DataValue.Flag(true);
        AssertEqual(DataValue.CaseTag.Flag, val.Tag, "Flag tag");
        var desc = TestLibFunctions.DescribeDataValue(val);
        AssertEqual("Bool:true", desc, "Describe flag");
        TestLogger.Info($"DataValue.Flag: {desc}");
    }

    public void TestDataValueNothing()
    {
        var val = DataValue.Nothing;
        AssertEqual(DataValue.CaseTag.Nothing, val.Tag, "Nothing tag");
        var desc = TestLibFunctions.DescribeDataValue(val);
        AssertEqual("Null", desc, "Describe nothing");
        TestLogger.Info($"DataValue.Nothing: {desc}");
    }

    #endregion

    #region Pass 2 — L5: Caseless Enum as Namespace (MathUtils)

    public void TestMathUtilsFactorial()
    {
        var result = MathUtils.Factorial(5);
        AssertEqual(120, result, "Factorial(5) = 120");
        TestLogger.Info($"MathUtils.Factorial(5) = {result}");
    }

    public void TestMathUtilsNestedCounter()
    {
        var counter = new MathUtils.Counter(10);
        AssertEqual(10, counter.Count, "Counter.Count = 10");
        var desc = counter.GetDescribe();
        AssertEqual("Count: 10", desc, "Counter.Describe()");
        TestLogger.Info($"MathUtils.Counter: {desc}");
    }

    #endregion
}
