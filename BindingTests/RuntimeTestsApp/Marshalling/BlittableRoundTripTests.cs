// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for basic blittable type round-trips.
/// Tier 1: Fast smoke tests for PR gate.
/// </summary>
public class BlittableRoundTripTests : TestBase
{
    public BlittableRoundTripTests(TestResults results) : base(results) { }

    #region FrozenPoint (Blittable Struct) Tests

    public void TestFrozenPointCreation()
    {
        // Test creating a frozen point at the origin
        var origin = TestLibFunctions.MakeOrigin();
        AssertEqual(0.0, origin.X, "Origin X");
        AssertEqual(0.0, origin.Y, "Origin Y");
        TestLogger.Info($"MakeOrigin() = ({origin.X}, {origin.Y})");
    }

    public void TestFrozenPointRoundTrip()
    {
        // Create a point in C#, pass to Swift, get back description
        var point = new FrozenPoint { X = 3.14, Y = 2.71 };
        var description = TestLibFunctions.DescribePoint(point);

        AssertNotNull(description, "Description not null");
        // Note: decimal separator varies by locale
        AssertTrue(description.Contains("3.14") || description.Contains("3,14"), "Description contains X");
        AssertTrue(description.Contains("2.71") || description.Contains("2,71"), "Description contains Y");
        TestLogger.Info($"DescribePoint((3.14, 2.71)) = \"{description}\"");
    }

    public void TestFrozenPointEdgeCases()
    {
        // Very small values
        var small = new FrozenPoint { X = 1e-10, Y = -1e-10 };
        var smallDesc = TestLibFunctions.DescribePoint(small);
        AssertNotNull(smallDesc, "Small point description");

        // Very large values
        var large = new FrozenPoint { X = 1e10, Y = -1e10 };
        var largeDesc = TestLibFunctions.DescribePoint(large);
        AssertNotNull(largeDesc, "Large point description");

        // Zero
        var zero = new FrozenPoint { X = 0.0, Y = 0.0 };
        var zeroDesc = TestLibFunctions.DescribePoint(zero);
        AssertNotNull(zeroDesc, "Zero point description");

        TestLogger.Info("FrozenPoint edge cases passed");
    }

    public void TestFrozenPointMethodWithStructParam()
    {
        // Tests frozen struct method parameter via @_cdecl UnsafeRawPointer path.
        // C# marshals FrozenPoint via stackalloc + MarshalToSwift, passes IntPtr to Swift wrapper.
        // Swift wrapper does .load(as: FrozenPoint.self) to reconstruct.
        var p1 = new FrozenPoint { X = 2.0, Y = 4.0 };
        var p2 = new FrozenPoint { X = 6.0, Y = 8.0 };
        var mid = p1.Midpoint(p2);
        AssertEqual(4.0, mid.X, "Midpoint X = (2+6)/2");
        AssertEqual(6.0, mid.Y, "Midpoint Y = (4+8)/2");
        TestLogger.Info($"Midpoint(({p1.X},{p1.Y}), ({p2.X},{p2.Y})) = ({mid.X},{mid.Y})");
    }

    public void TestFrozenPointTranslated()
    {
        // Tests method returning frozen struct after struct param marshalling.
        var point = new FrozenPoint { X = 1.0, Y = 2.0 };
        var translated = point.Translated(3.0, 4.0);
        AssertEqual(4.0, translated.X, "Translated X = 1+3");
        AssertEqual(6.0, translated.Y, "Translated Y = 2+4");
        TestLogger.Info($"Translated = ({translated.X},{translated.Y})");
    }

    #endregion

    #region Simple Class Tests

    public void TestClassCreation()
    {
        // Test creating a simple Swift class instance
        var animal = TestLibFunctions.CreateAnimal("Dog", "Bark");

        // Verify the object was created (not null)
        AssertNotNull(animal, "Animal created");
        TestLogger.Info("Class creation test passed");
    }

    #endregion

    #region Bool Tests

    public void TestBoolReturn()
    {
        // ValidateLogLevelRoundTrip returns bool
        var validResult = TestLibFunctions.ValidateLogLevelRoundTrip("[INFO]");
        AssertTrue(validResult, "Valid log level round-trip");

        var invalidResult = TestLibFunctions.ValidateLogLevelRoundTrip("INVALID");
        AssertFalse(invalidResult, "Invalid log level round-trip");

        TestLogger.Info("Bool return tests passed");
    }

    #endregion

    #region Direction Enum Tests

    public void TestEnumUsage()
    {
        // Test direction enum (cases: North, South, East, West)
        var eastIsHorizontal = TestLibFunctions.IsHorizontal(Direction.East);
        AssertTrue(eastIsHorizontal, "East is horizontal");

        var northIsNotHorizontal = !TestLibFunctions.IsHorizontal(Direction.North);
        AssertTrue(northIsNotHorizontal, "North is not horizontal");

        TestLogger.Info("Enum tests passed");
    }

    #endregion

    #region Pass 2 — W1: CoreGraphics Types (CGPoint/CGSize/CGRect)

    public void TestCreatePoint()
    {
        var pt = TestLibFunctions.CreatePoint(10.0, 20.0);
        AssertEqual(10.0, pt.X, "Point X");
        AssertEqual(20.0, pt.Y, "Point Y");
        TestLogger.Info($"CreatePoint(10,20) = ({pt.X},{pt.Y})");
    }

    public void TestCreateSize()
    {
        var sz = TestLibFunctions.CreateSize(100.0, 200.0);
        AssertEqual(100.0, sz.Width, "Size width");
        AssertEqual(200.0, sz.Height, "Size height");
        TestLogger.Info($"CreateSize(100,200) = ({sz.Width},{sz.Height})");
    }

    public void TestCreateRect()
    {
        var rect = TestLibFunctions.CreateRect(5.0, 10.0, 50.0, 30.0);
        AssertEqual(5.0, rect.Origin.X, "Rect origin X");
        AssertEqual(10.0, rect.Origin.Y, "Rect origin Y");
        AssertEqual(50.0, rect.Size.Width, "Rect width");
        AssertEqual(30.0, rect.Size.Height, "Rect height");
        TestLogger.Info("CreateRect passed");
    }

    public void TestRectArea()
    {
        var rect = TestLibFunctions.CreateRect(0, 0, 10.0, 5.0);
        var area = TestLibFunctions.RectArea(rect);
        AssertEqual(50.0, area, "RectArea(10x5) = 50");
        TestLogger.Info($"RectArea = {area}");
    }

    public void TestDescribeRect()
    {
        var rect = TestLibFunctions.CreateRect(1.0, 2.0, 3.0, 4.0);
        var desc = TestLibFunctions.DescribeRect(rect);
        AssertTrue(desc.Contains("1.0"), "Describe contains x");
        AssertTrue(desc.Contains("3.0"), "Describe contains width");
        TestLogger.Info($"DescribeRect = {desc}");
    }

    #endregion

    #region Pass 2 — W3: Float (32-bit) Properties (FloatHolder)

    public void TestFloatHolderCreation()
    {
        var holder = new FloatHolder(1.5f, 0.8f);
        AssertEqual(1.5f, holder.Radius, "FloatHolder radius");
        AssertEqual(0.8f, holder.Opacity, "FloatHolder opacity");
        TestLogger.Info($"FloatHolder: r={holder.Radius}, o={holder.Opacity}");
    }

    public void TestFloatHolderDescribe()
    {
        var holder = new FloatHolder(2.5f, 0.5f);
        var desc = holder.GetDescribe();
        AssertTrue(desc.Contains("2.5"), "Describe contains radius");
        AssertTrue(desc.Contains("0.5"), "Describe contains opacity");
        TestLogger.Info($"FloatHolder.Describe = {desc}");
    }

    #endregion

    #region Pass 2 — V1: Method Overloading (Converter)

    public void TestConverterInt()
    {
        var c = new Converter();
        var result = c.Convert(42);
        AssertEqual("int:42", result, "Convert(int)");
        TestLogger.Info($"Convert(int) = {result}");
    }

    public void TestConverterDouble()
    {
        var c = new Converter();
        var result = c.Convert(3.14);
        AssertTrue(result.StartsWith("double:3.14"), "Convert(double)");
        TestLogger.Info($"Convert(double) = {result}");
    }

    public void TestConverterBool()
    {
        var c = new Converter();
        var result = c.Convert(true);
        AssertEqual("bool:true", result, "Convert(bool)");
        TestLogger.Info($"Convert(bool) = {result}");
    }

    public void TestConverterString()
    {
        var c = new Converter();
        var result = c.Convert("hello");
        AssertEqual("string:hello", result, "Convert(string)");
        TestLogger.Info($"Convert(string) = {result}");
    }

    #endregion
}
