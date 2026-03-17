// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Enum extension method tests — verifies that Swift extension methods on enums
/// are projected as C# extension methods and dispatch correctly.
///
/// Color extensions: Complementary (returns int), GetHexDescription (returns string).
/// Direction extensions: GetDescription (returns string).
///
/// Tier structure:
/// - Tier 1: Color.Complementary (blittable int return)
/// - Tier 2: Color.GetHexDescription, Direction.GetDescription (string returns)
/// </summary>
public class EnumExtensionTests : TestBase
{
    public EnumExtensionTests(TestResults results) : base(results) { }

    #region Color.Complementary — Blittable (Tier 1)

    public void TestColorRedComplementary()
    {
        // Swift: (rawValue + 3) % 6 → (0 + 3) % 6 = 3
        var result = Color.Red.Complementary();
        AssertEqual(3, result, "Red.Complementary() = (0+3)%6 = 3");
        TestLogger.Info($"Color.Red.Complementary() = {result}");
    }

    public void TestColorGreenComplementary()
    {
        // Swift: (rawValue + 3) % 6 → (1 + 3) % 6 = 4
        var result = Color.Green.Complementary();
        AssertEqual(4, result, "Green.Complementary() = (1+3)%6 = 4");
        TestLogger.Info($"Color.Green.Complementary() = {result}");
    }

    public void TestColorBlueComplementary()
    {
        // Swift: (rawValue + 3) % 6 → (2 + 3) % 6 = 5
        var result = Color.Blue.Complementary();
        AssertEqual(5, result, "Blue.Complementary() = (2+3)%6 = 5");
        TestLogger.Info($"Color.Blue.Complementary() = {result}");
    }

    public void TestColorAlphaComplementary()
    {
        // Swift: (rawValue + 3) % 6 → (3 + 3) % 6 = 0
        var result = Color.Alpha.Complementary();
        AssertEqual(0, result, "Alpha.Complementary() = (3+3)%6 = 0");
        TestLogger.Info($"Color.Alpha.Complementary() = {result}");
    }

    #endregion

    #region Color.GetHexDescription — String Return (Tier 2)

    public void TestColorRedHexDescription()
    {
        var desc = Color.Red.GetHexDescription();
        AssertEqual("#FF0000", desc, "Red.GetHexDescription() is #FF0000");
        TestLogger.Info($"Color.Red.GetHexDescription() = \"{desc}\"");
    }

    public void TestColorGreenHexDescription()
    {
        var desc = Color.Green.GetHexDescription();
        AssertEqual("#00FF00", desc, "Green.GetHexDescription() is #00FF00");
        TestLogger.Info($"Color.Green.GetHexDescription() = \"{desc}\"");
    }

    public void TestColorBlueHexDescription()
    {
        var desc = Color.Blue.GetHexDescription();
        AssertEqual("#0000FF", desc, "Blue.GetHexDescription() is #0000FF");
        TestLogger.Info($"Color.Blue.GetHexDescription() = \"{desc}\"");
    }

    public void TestColorAlphaHexDescription()
    {
        var desc = Color.Alpha.GetHexDescription();
        AssertEqual("#000000FF", desc, "Alpha.GetHexDescription() is #000000FF");
        TestLogger.Info($"Color.Alpha.GetHexDescription() = \"{desc}\"");
    }

    public void TestColorHexDescriptionsDistinct()
    {
        var red = Color.Red.GetHexDescription();
        var green = Color.Green.GetHexDescription();
        var blue = Color.Blue.GetHexDescription();
        AssertTrue(red != green, "Red and Green descriptions differ");
        AssertTrue(red != blue, "Red and Blue descriptions differ");
        AssertTrue(green != blue, "Green and Blue descriptions differ");
        TestLogger.Info("Color hex descriptions are distinct");
    }

    #endregion

    #region Direction.GetDescription — String Return (Tier 2)

    public void TestDirectionNorthDescription()
    {
        var desc = Direction.North.GetDescription();
        AssertEqual("North", desc, "North.GetDescription() is 'North'");
        TestLogger.Info($"Direction.North.GetDescription() = \"{desc}\"");
    }

    public void TestDirectionSouthDescription()
    {
        var desc = Direction.South.GetDescription();
        AssertEqual("South", desc, "South.GetDescription() is 'South'");
        TestLogger.Info($"Direction.South.GetDescription() = \"{desc}\"");
    }

    public void TestDirectionEastDescription()
    {
        var desc = Direction.East.GetDescription();
        AssertEqual("East", desc, "East.GetDescription() is 'East'");
        TestLogger.Info($"Direction.East.GetDescription() = \"{desc}\"");
    }

    public void TestDirectionWestDescription()
    {
        var desc = Direction.West.GetDescription();
        AssertEqual("West", desc, "West.GetDescription() is 'West'");
        TestLogger.Info($"Direction.West.GetDescription() = \"{desc}\"");
    }

    public void TestDirectionDescriptionsDistinct()
    {
        var north = Direction.North.GetDescription();
        var south = Direction.South.GetDescription();
        var east = Direction.East.GetDescription();
        var west = Direction.West.GetDescription();
        AssertTrue(north != south, "North and South descriptions differ");
        AssertTrue(east != west, "East and West descriptions differ");
        AssertTrue(north != east, "North and East descriptions differ");
        TestLogger.Info("Direction descriptions are distinct");
    }

    #endregion
}
