// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for enum marshalling: case construction, raw value round-trip,
/// associated values, nested enums, and the Phase 55 string enum regression.
/// </summary>
[CrashRisk("Mono JIT assertion on CreateOrder/Shape P/Invoke")]
public class EnumMarshallingTests : TestBase
{
    public EnumMarshallingTests(TestResults results) : base(results) { }

    #region Direction Enum (Simple Cases)

    [TestTier(TestTier.Tier1)]
    public void TestDirectionCaseConstruction()
    {
        // Verify all four Direction cases construct and have correct tags
        AssertEqual(Direction.CaseTag.North, Direction.North.Tag, "North tag");
        AssertEqual(Direction.CaseTag.South, Direction.South.Tag, "South tag");
        AssertEqual(Direction.CaseTag.East, Direction.East.Tag, "East tag");
        AssertEqual(Direction.CaseTag.West, Direction.West.Tag, "West tag");
        TestLogger.Info("Direction case construction passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestDirectionMethodCall()
    {
        // Test calling a method on an enum value
        var east = Direction.East;
        AssertTrue(SwiftBindingsTestLib.IsHorizontal(east), "East is horizontal");

        var north = Direction.North;
        AssertFalse(SwiftBindingsTestLib.IsHorizontal(north), "North is not horizontal");

        TestLogger.Info("Direction method call passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDirectionOpposite()
    {
        // Test Direction.Opposite() instance method
        var north = Direction.North;
        var opposite = north.Opposite();
        AssertEqual(Direction.CaseTag.South, opposite.Tag, "Opposite of North is South");

        var east = Direction.East;
        opposite = east.Opposite();
        AssertEqual(Direction.CaseTag.West, opposite.Tag, "Opposite of East is West");

        TestLogger.Info("Direction Opposite tests passed");
    }

    #endregion

    #region Color Enum (Int Raw Value)

    [TestTier(TestTier.Tier1)]
    public void TestColorCaseConstruction()
    {
        AssertEqual(Color.CaseTag.Red, Color.Red.Tag, "Red tag");
        AssertEqual(Color.CaseTag.Green, Color.Green.Tag, "Green tag");
        AssertEqual(Color.CaseTag.Blue, Color.Blue.Tag, "Blue tag");
        AssertEqual(Color.CaseTag.Alpha, Color.Alpha.Tag, "Alpha tag");
        TestLogger.Info("Color case construction passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorIntRawValue()
    {
        // Color has Int raw values: red=0, green=1, blue=2, alpha=3
        AssertEqual(0, Color.Red.RawValue, "Red raw value");
        AssertEqual(1, Color.Green.RawValue, "Green raw value");
        AssertEqual(2, Color.Blue.RawValue, "Blue raw value");
        AssertEqual(3, Color.Alpha.RawValue, "Alpha raw value");
        TestLogger.Info("Color int raw value tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorFromRawValue()
    {
        // Round-trip: int raw value -> Color -> int raw value
        var red = Color.FromRawValue(0);
        AssertNotNull(red, "Color.FromRawValue(0) not null");
        AssertEqual(0, red!.RawValue, "Red round-trip");

        var blue = Color.FromRawValue(2);
        AssertNotNull(blue, "Color.FromRawValue(2) not null");
        AssertEqual(2, blue!.RawValue, "Blue round-trip");

        // Invalid raw value
        var invalid = Color.FromRawValue(99);
        AssertNull(invalid, "Color.FromRawValue(99) is null");

        TestLogger.Info("Color FromRawValue tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorForIndexFunction()
    {
        // Test the free function colorForIndex
        var color0 = SwiftBindingsTestLib.ColorForIndex(0);
        AssertEqual(Color.CaseTag.Red, color0.Tag, "ColorForIndex(0) is Red");

        var color1 = SwiftBindingsTestLib.ColorForIndex(1);
        AssertEqual(Color.CaseTag.Green, color1.Tag, "ColorForIndex(1) is Green");

        TestLogger.Info("ColorForIndex tests passed");
    }

    #endregion

    #region StatusCode Enum (String Raw Value)

    [TestTier(TestTier.Tier2)]
    public void TestStatusCodeCases()
    {
        AssertEqual(StatusCode.CaseTag.Ok, StatusCode.Ok.Tag, "Ok tag");
        AssertEqual(StatusCode.CaseTag.NotFound, StatusCode.NotFound.Tag, "NotFound tag");
        AssertEqual(StatusCode.CaseTag.Error, StatusCode.Error.Tag, "Error tag");
        AssertEqual(StatusCode.CaseTag.Timeout, StatusCode.Timeout.Tag, "Timeout tag");
        TestLogger.Info("StatusCode case tags passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusCodeRawValues()
    {
        AssertEqual("OK", StatusCode.Ok.RawValue.ToString(), "Ok raw value");
        AssertEqual("NOT_FOUND", StatusCode.NotFound.RawValue.ToString(), "NotFound raw value");
        AssertEqual("ERROR", StatusCode.Error.RawValue.ToString(), "Error raw value");
        AssertEqual("TIMEOUT", StatusCode.Timeout.RawValue.ToString(), "Timeout raw value");
        TestLogger.Info("StatusCode raw values passed");
    }

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
    public void TestShapeCaseCreation()
    {
        // Test creating Shape cases with associated values
        var circle = Shape.Circle(5.0);
        AssertEqual(Shape.CaseTag.Circle, circle.Tag, "Circle tag");

        var rect = Shape.Rectangle((width: 10.0, height: 20.0));
        AssertEqual(Shape.CaseTag.Rectangle, rect.Tag, "Rectangle tag");

        var point = Shape.Point(new FrozenPoint { X = 1.0, Y = 2.0 });
        AssertEqual(Shape.CaseTag.Point, point.Tag, "Point tag");

        var empty = Shape.Empty;
        AssertEqual(Shape.CaseTag.Empty, empty.Tag, "Empty tag");

        TestLogger.Info("Shape case creation passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestShapeAllCasesDistinct()
    {
        // Verify all cases produce distinct tags
        var circle = Shape.Circle(1.0);
        var rect = Shape.Rectangle((2.0, 3.0));
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

    #region Nested Container Enums (Phase 55 regression area)

    [TestTier(TestTier.Tier2)]
    public void TestOrderContainerCreation()
    {
        // CreateOrder takes orderId + statusRaw strings, returns OrderContainer?
        var order = SwiftBindingsTestLib.CreateOrder("ORD-001", "pending");
        AssertNotNull(order, "Order created");

        var statusRaw = SwiftBindingsTestLib.GetOrderStatusRaw(order!);
        AssertEqual("pending", statusRaw, "Order status raw value");

        TestLogger.Info("OrderContainer creation passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOrderStatusFromRawValue()
    {
        // Test nested OrderContainer.Status enum
        var pending = OrderContainer.Status.FromRawValue("pending");
        AssertNotNull(pending, "OrderContainer.Status pending not null");
        AssertEqual("pending", pending!.RawValue.ToString(), "pending round-trip");

        var shipped = OrderContainer.Status.FromRawValue("shipped");
        AssertNotNull(shipped, "OrderContainer.Status shipped not null");
        AssertEqual("shipped", shipped!.RawValue.ToString(), "shipped round-trip");

        var invalid = OrderContainer.Status.FromRawValue("bogus");
        AssertNull(invalid, "Invalid order status is null");

        TestLogger.Info("OrderContainer.Status FromRawValue passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOrderStatusAllCases()
    {
        var cases = new[] { "pending", "processing", "shipped", "delivered", "cancelled" };
        foreach (var rawValue in cases)
        {
            var status = OrderContainer.Status.FromRawValue(rawValue);
            AssertNotNull(status, $"OrderContainer.Status {rawValue} not null");
            AssertEqual(rawValue, status!.RawValue.ToString(), $"{rawValue} round-trip");
        }
        TestLogger.Info("OrderContainer.Status all cases passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPaymentContainerCreation()
    {
        var payment = SwiftBindingsTestLib.CreatePayment("PAY-001", "authorized");
        AssertNotNull(payment, "Payment created");

        var statusRaw = SwiftBindingsTestLib.GetPaymentStatusRaw(payment!);
        AssertEqual("authorized", statusRaw, "Payment status raw value");

        TestLogger.Info("PaymentContainer creation passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPaymentStatusFromRawValue()
    {
        var cases = new[] { "pending", "authorized", "captured", "refunded", "failed" };
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

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
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
}
