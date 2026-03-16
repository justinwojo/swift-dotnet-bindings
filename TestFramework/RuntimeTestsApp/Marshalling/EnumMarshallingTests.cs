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

    [TestTier(TestTier.Tier1)]
    public void TestDirectionCaseConstruction()
    {
        // Direction is a simple C# enum (frozen, no raw value)
        AssertEqual(Direction.North, (Direction)0, "North is 0");
        AssertEqual(Direction.South, (Direction)1, "South is 1");
        AssertEqual(Direction.East, (Direction)2, "East is 2");
        AssertEqual(Direction.West, (Direction)3, "West is 3");
        TestLogger.Info("Direction case construction passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestDirectionMethodCall()
    {
        // Test calling a method on an enum value (extension method)
        var east = Direction.East;
        AssertTrue(TestLibFunctions.IsHorizontal(east), "East is horizontal");

        var north = Direction.North;
        AssertFalse(TestLibFunctions.IsHorizontal(north), "North is not horizontal");

        TestLogger.Info("Direction method call passed");
    }

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier1)]
    public void TestColorCaseConstruction()
    {
        // Color is a simple C# enum (frozen + Int32 raw value)
        AssertEqual(Color.Red, (Color)0, "Red is 0");
        AssertEqual(Color.Green, (Color)1, "Green is 1");
        AssertEqual(Color.Blue, (Color)2, "Blue is 2");
        AssertEqual(Color.Alpha, (Color)3, "Alpha is 3");
        TestLogger.Info("Color case construction passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorIntRawValue()
    {
        // Verify raw int values are correct and distinct
        AssertEqual(0, (int)Color.Red, "Red raw value");
        AssertEqual(1, (int)Color.Green, "Green raw value");
        AssertEqual(2, (int)Color.Blue, "Blue raw value");
        AssertEqual(3, (int)Color.Alpha, "Alpha raw value");
        TestLogger.Info("Color int raw value tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorFromTagRoundTrip()
    {
        // Verify round-trip through int cast
        AssertEqual(Color.Red, (Color)(int)Color.Red, "Red round-trip");
        AssertEqual(Color.Blue, (Color)(int)Color.Blue, "Blue round-trip");

        TestLogger.Info("Color FromRawValue tests passed");
    }

    [TestTier(TestTier.Tier2)]
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

        var rect = Shape.Rectangle(10.0, 20.0);
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

    [TestTier(TestTier.Tier2)]
    public void TestEnumPropertyHolder_GetCurrentShape()
    {
        // Create holder with circle, read back the property
        var holder = new EnumPropertyHolder(Shape.Circle(5.0));
        var shape = holder.CurrentShape;
        AssertEqual(Shape.CaseTag.Circle, shape.Tag, "CurrentShape is Circle");
        TestLogger.Info("EnumPropertyHolder.CurrentShape getter passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEnumPropertyHolder_SetCurrentShape()
    {
        // Create holder with circle, set to rectangle, verify
        var holder = new EnumPropertyHolder(Shape.Circle(5.0));
        holder.CurrentShape = Shape.Rectangle(3.0, 4.0);
        var shape = holder.CurrentShape;
        AssertEqual(Shape.CaseTag.Rectangle, shape.Tag, "CurrentShape updated to Rectangle");
        TestLogger.Info("EnumPropertyHolder.CurrentShape setter passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEnumPropertyHolder_GetShapeMethod()
    {
        // Test method returning non-simple enum
        var holder = new EnumPropertyHolder(Shape.Empty);
        var shape = holder.GetShape();
        AssertEqual(Shape.CaseTag.Empty, shape.Tag, "GetShape() returns Empty");
        TestLogger.Info("EnumPropertyHolder.GetShape() passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEnumPropertyHolder_OptionalShapeDefaultNull()
    {
        // optionalShape defaults to nil in init — verify null round-trip
        var holder = new EnumPropertyHolder(Shape.Empty);
        var optShape = holder.OptionalShape;
        AssertNull(optShape, "OptionalShape defaults to null");
        TestLogger.Info("EnumPropertyHolder.OptionalShape default null passed");
    }

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier2)]
    public void TestPaymentContainerCreation()
    {
        // Swift raw value is "payment_authorized" (not case name "authorized")
        var payment = TestLibFunctions.CreatePayment("PAY-001", "payment_authorized");
        AssertNotNull(payment, "Payment created");

        var statusRaw = TestLibFunctions.GetPaymentStatusRaw(payment!);
        AssertEqual("payment_authorized", statusRaw, "Payment status raw value");

        TestLogger.Info("PaymentContainer creation passed");
    }

    [TestTier(TestTier.Tier2)]
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
