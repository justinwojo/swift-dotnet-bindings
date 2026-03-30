// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Tests for cross-module type references: types from SwiftBindingsTestLibDependency
/// used as parameters and return values in SwiftBindingsTestLib functions.
/// Also tests cross-module protocol conformance (LocalConformant).
/// </summary>
public class CrossModuleTests : TestBase
{
    public CrossModuleTests(TestResults results) : base(results) { }

    #region LocalConformant (Cross-Module Protocol Conformance)

    public void TestLocalConformantCreation()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("test-id", 5);
        AssertEqual("test-id", lc.Identifier.ToString(), "Identifier preserved");
        AssertEqual(5, lc.Tag, "Tag preserved");
    }

    public void TestLocalConformantDescribe()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("hello", 3);
        var desc = lc.GetDescribe();
        AssertTrue(desc.Contains("hello"), "Describe contains identifier");
        AssertTrue(desc.Contains("3"), "Describe contains tag");
    }

    #endregion

    #region Cross-Module Type References (Part A)

    public void TestTransformDependencyPoint()
    {
        var point = new DependencyPoint(3.0, 4.0);
        AssertEqual(3.0, point.X, "Initial X");
        AssertEqual(4.0, point.Y, "Initial Y");

        var scaled = TestLibFunctions.TransformDependencyPoint(point, 2.0);
        AssertEqual(6.0, scaled.X, "Scaled X = 3.0 * 2.0");
        AssertEqual(8.0, scaled.Y, "Scaled Y = 4.0 * 2.0");
    }

    public void TestUpgradeDependencyConfig()
    {
        using var config = SwiftBindingsTestLibDependency.Functions.MakeDependencyConfig("TestLib", 1);
        AssertEqual("TestLib", config.Name, "Initial name");
        AssertEqual(1, config.Version, "Initial version");

        using var upgraded = TestLibFunctions.UpgradeDependencyConfig(config);
        AssertEqual("TestLib", upgraded.Name, "Name preserved after upgrade");
        AssertEqual(2, upgraded.Version, "Version incremented");
    }

    public void TestToggleDependencyService()
    {
        using var service = new DependencyService("MyService");
        AssertTrue(service.IsActive, "Initially active");

        var status = TestLibFunctions.ToggleDependencyService(service);
        AssertTrue(status.Contains("MyService"), "Status contains service name");
        AssertTrue(status.Contains("inactive"), "Status reflects toggled state");
        AssertEqual(false, service.IsActive, "Service toggled to inactive");
    }

    #endregion

    #region Cross-Module Property Type (Part B-1)

    public void TestAnnotatedLocationCreation()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("Origin", 0.0, 0.0);
        AssertEqual("Origin", loc.Label, "Label preserved");
        AssertEqual(0.0, loc.Point.X, "Point X preserved");
        AssertEqual(0.0, loc.Point.Y, "Point Y preserved");
    }

    public void TestAnnotatedLocationPointProperty()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("TestPoint", 5.0, 10.0);
        var point = loc.Point;
        AssertEqual(5.0, point.X, "Property getter returns correct X");
        AssertEqual(10.0, point.Y, "Property getter returns correct Y");
    }

    public void TestGetLocationPointRoundTrip()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("RoundTrip", 7.5, 2.5);
        var point = TestLibFunctions.GetLocationPoint(loc);
        AssertEqual(7.5, point.X, "Round-trip X through cross-module function");
        AssertEqual(2.5, point.Y, "Round-trip Y through cross-module function");
    }

    #endregion

    #region Cross-Module Collection (Part B-2)

    public void TestSumDependencyPoints()
    {
        var points = new[]
        {
            new DependencyPoint(1.0, 2.0),
            new DependencyPoint(3.0, 4.0),
            new DependencyPoint(5.0, 6.0)
        };

        var sum = TestLibFunctions.SumDependencyPoints(points);
        AssertEqual(9.0, sum.X, "Sum X = 1+3+5");
        AssertEqual(12.0, sum.Y, "Sum Y = 2+4+6");
    }

    public void TestMakeDependencyPointGrid()
    {
        var grid = TestLibFunctions.MakeDependencyPointGrid(2, 3);
        AssertEqual(6, grid.Count, "2x3 grid = 6 points");

        // First row: (0,0), (1,0), (2,0)
        AssertEqual(0.0, grid[0].X, "Grid[0] X");
        AssertEqual(0.0, grid[0].Y, "Grid[0] Y");
        AssertEqual(1.0, grid[1].X, "Grid[1] X");
        AssertEqual(0.0, grid[1].Y, "Grid[1] Y");
        AssertEqual(2.0, grid[2].X, "Grid[2] X");
        AssertEqual(0.0, grid[2].Y, "Grid[2] Y");

        // Second row: (0,1), (1,1), (2,1)
        AssertEqual(0.0, grid[3].X, "Grid[3] X");
        AssertEqual(1.0, grid[3].Y, "Grid[3] Y");
        AssertEqual(1.0, grid[4].X, "Grid[4] X");
        AssertEqual(1.0, grid[4].Y, "Grid[4] Y");
        AssertEqual(2.0, grid[5].X, "Grid[5] X");
        AssertEqual(1.0, grid[5].Y, "Grid[5] Y");
    }

    public void TestSumEmptyCollection()
    {
        var empty = Array.Empty<DependencyPoint>();
        var sum = TestLibFunctions.SumDependencyPoints(empty);
        AssertEqual(0.0, sum.X, "Empty sum X = 0");
        AssertEqual(0.0, sum.Y, "Empty sum Y = 0");
    }

    #endregion

    #region Cross-Module Enum Usage (Part B-3)

    public void TestPromoteDependencyStatus()
    {
        var promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Unknown);
        AssertEqual(DependencyStatus.Pending, promoted, "Unknown promotes to Pending");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Pending);
        AssertEqual(DependencyStatus.Active, promoted, "Pending promotes to Active");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Active);
        AssertEqual(DependencyStatus.Active, promoted, "Active stays Active");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Inactive);
        AssertEqual(DependencyStatus.Pending, promoted, "Inactive promotes to Pending");
    }

    public void TestDescribeDependencyStatus()
    {
        var desc = TestLibFunctions.DescribeDependencyStatus(DependencyStatus.Active);
        AssertTrue(desc.Contains("Active"), "Description contains Active label");
    }

    public void TestDependencyStatusEnumValues()
    {
        AssertEqual(0, (int)DependencyStatus.Unknown, "Unknown = 0");
        AssertEqual(1, (int)DependencyStatus.Pending, "Pending = 1");
        AssertEqual(2, (int)DependencyStatus.Active, "Active = 2");
        AssertEqual(3, (int)DependencyStatus.Inactive, "Inactive = 3");
    }

    #endregion

    #region Cross-Module Closure (Part B-4)

    public void TestApplyToDependencyPoint()
    {
        double capturedX = 0;
        double capturedY = 0;

        TestLibFunctions.ApplyToDependencyPoint(3.0, 7.0, point =>
        {
            capturedX = point.X;
            capturedY = point.Y;
        });

        AssertEqual(3.0, capturedX, "Closure received correct X");
        AssertEqual(7.0, capturedY, "Closure received correct Y");
    }

    public void TestMapDependencyPoint()
    {
        var original = new DependencyPoint(2.0, 3.0);

        var doubled = TestLibFunctions.MapDependencyPoint(original, p =>
            new DependencyPoint(p.X * 2, p.Y * 2));

        AssertEqual(4.0, doubled.X, "Mapped X = 2*2");
        AssertEqual(6.0, doubled.Y, "Mapped Y = 3*2");
    }

    #endregion

    #region Cross-Module Extension (Part B-5)

    public void TestScaleDependencyPoint()
    {
        var point = new DependencyPoint(3.0, 4.0);

        var scaled = TestLibFunctions.ScaleDependencyPoint(point, 3.0);
        AssertEqual(9.0, scaled.X, "Scaled X = 3*3");
        AssertEqual(12.0, scaled.Y, "Scaled Y = 4*3");
    }

    public void TestScaleDependencyPointByZero()
    {
        var point = new DependencyPoint(5.0, 10.0);

        var scaled = TestLibFunctions.ScaleDependencyPoint(point, 0.0);
        AssertEqual(0.0, scaled.X, "Scaled by 0 gives X=0");
        AssertEqual(0.0, scaled.Y, "Scaled by 0 gives Y=0");
    }

    #endregion
}
