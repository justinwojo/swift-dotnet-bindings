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

    [TestTier(TestTier.Tier1)]
    public void TestFrozenPointCreation()
    {
        // Test creating a frozen point at the origin
        var origin = TestLibFunctions.MakeOrigin();
        AssertEqual(0.0, origin.X, "Origin X");
        AssertEqual(0.0, origin.Y, "Origin Y");
        TestLogger.Info($"MakeOrigin() = ({origin.X}, {origin.Y})");
    }

    [TestTier(TestTier.Tier1)]
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

    [TestTier(TestTier.Tier2)]
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

    #endregion

    #region Simple Class Tests

    [TestTier(TestTier.Tier1)]
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

    [TestTier(TestTier.Tier1)]
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

    [TestTier(TestTier.Tier1)]
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
}
