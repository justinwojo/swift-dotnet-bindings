// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Tests for SwiftArray with simple enum elements. Exercises the SwiftMarshal
/// simple enum narrowing path (C# enum : int = 4 bytes, Swift enum = 1 byte).
/// </summary>
public class EnumArrayTests : TestBase
{
    public EnumArrayTests(TestResults results) : base(results) { }

    #region Enum Array Pass-Through (C# → Swift)

    public void TestCountDirections()
    {
        var arr = new SwiftArray<Direction>();
        arr.Append(Direction.North);
        arr.Append(Direction.East);
        arr.Append(Direction.West);
        var count = Functions.CountDirections(arr);
        AssertEqual(3, count, "CountDirections should return 3");
        TestLogger.Info($"CountDirections({arr.Count} items) = {count}");
    }

    public void TestDirectionsContainTrue()
    {
        var arr = new SwiftArray<Direction>();
        arr.Append(Direction.North);
        arr.Append(Direction.South);
        var result = Functions.DirectionsContain(arr, Direction.South);
        AssertTrue(result, "Array should contain South");
        TestLogger.Info($"DirectionsContain(South) = {result}");
    }

    public void TestDirectionsContainFalse()
    {
        var arr = new SwiftArray<Direction>();
        arr.Append(Direction.North);
        arr.Append(Direction.South);
        var result = Functions.DirectionsContain(arr, Direction.West);
        AssertTrue(!result, "Array should not contain West");
        TestLogger.Info($"DirectionsContain(West) = {result}");
    }

    #endregion

    #region Enum Array Return (Swift → C#)

    public void TestAllDirectionsReturn()
    {
        var arr = Functions.GetAllDirections();
        AssertEqual(4, arr.Count, "GetAllDirections should return 4 items");
        AssertEqual(Direction.North, arr[0], "First should be North");
        AssertEqual(Direction.South, arr[1], "Second should be South");
        AssertEqual(Direction.East, arr[2], "Third should be East");
        AssertEqual(Direction.West, arr[3], "Fourth should be West");
        TestLogger.Info($"GetAllDirections() returned {arr.Count} items");
    }

    public void TestFirstDirection()
    {
        var arr = new SwiftArray<Direction>();
        arr.Append(Direction.West);
        arr.Append(Direction.East);
        var first = Functions.FirstDirection(arr);
        AssertEqual(Direction.West, first, "FirstDirection should be West");
        TestLogger.Info($"FirstDirection() = {first}");
    }

    #endregion
}
