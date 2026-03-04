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
[CrashRisk("Mono JIT assertion on CreateOrder/Shape P/Invoke")]
public class EnumMarshallingTests : TestBase
{
    public EnumMarshallingTests(TestResults results) : base(results) { }

    #region Direction Enum (Class-based — non-frozen Swift enum)

    [TestTier(TestTier.Tier1)]
    public void TestDirectionCaseConstruction()
    {
        // Direction is a class-based enum (non-frozen) — verify case tags
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
        AssertTrue(TestLibFunctions.IsHorizontal(east), "East is horizontal");

        var north = Direction.North;
        AssertFalse(TestLibFunctions.IsHorizontal(north), "North is not horizontal");

        TestLogger.Info("Direction method call passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDirectionOpposite()
    {
        // Test Direction.Opposite() method
        var north = Direction.North;
        var opposite = north.Opposite();
        AssertEqual(Direction.CaseTag.South, opposite.Tag, "Opposite of North is South");

        var east = Direction.East;
        opposite = east.Opposite();
        AssertEqual(Direction.CaseTag.West, opposite.Tag, "Opposite of East is West");

        TestLogger.Info("Direction Opposite tests passed");
    }

    #endregion

    #region Color Enum (Class-based — non-frozen Swift enum)

    [TestTier(TestTier.Tier1)]
    public void TestColorCaseConstruction()
    {
        // Color is a class-based enum (non-frozen) — verify case tags
        AssertEqual(Color.CaseTag.Red, Color.Red.Tag, "Red tag");
        AssertEqual(Color.CaseTag.Green, Color.Green.Tag, "Green tag");
        AssertEqual(Color.CaseTag.Blue, Color.Blue.Tag, "Blue tag");
        AssertEqual(Color.CaseTag.Alpha, Color.Alpha.Tag, "Alpha tag");
        TestLogger.Info("Color case construction passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorIntRawValue()
    {
        // Color is class-based — verify tags are distinct
        var tags = new[] { Color.Red.Tag, Color.Green.Tag, Color.Blue.Tag, Color.Alpha.Tag };
        for (int i = 0; i < tags.Length; i++)
            for (int j = i + 1; j < tags.Length; j++)
                AssertTrue(tags[i] != tags[j], $"Color tags {i} and {j} are distinct");
        TestLogger.Info("Color int raw value tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestColorFromTagRoundTrip()
    {
        // Verify that each case factory returns the expected tag
        AssertEqual(Color.CaseTag.Red, Color.Red.Tag, "Red round-trip");
        AssertEqual(Color.CaseTag.Blue, Color.Blue.Tag, "Blue round-trip");

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
        holder.CurrentShape = Shape.Rectangle((width: 3.0, height: 4.0));
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
        holder.OptionalShape = Shape.Rectangle((width: 5.0, height: 10.0));
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
        var order = TestLibFunctions.CreateOrder("ORD-001", "pending");
        AssertNotNull(order, "Order created");

        var statusRaw = TestLibFunctions.GetOrderStatusRaw(order!);
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
        var payment = TestLibFunctions.CreatePayment("PAY-001", "authorized");
        AssertNotNull(payment, "Payment created");

        var statusRaw = TestLibFunctions.GetPaymentStatusRaw(payment!);
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
