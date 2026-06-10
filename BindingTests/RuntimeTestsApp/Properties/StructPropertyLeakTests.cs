// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
using Swift;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Tests for memory safety during repeated non-frozen struct property access.
/// Covers the R1 regression where accessing struct-typed properties on
/// cached objects (lazy-populate-on-first-read pattern) crashes silently.
///
/// Tier structure:
/// - Tier 1: DataContainer construction, single property access
/// - Tier 2: Repeated property access, cached data access
/// - Tier 3: Lifecycle (dispose + recreate)
/// </summary>
public class StructPropertyLeakTests : TestBase
{
    public StructPropertyLeakTests(TestResults results) : base(results) { }

    #region Construction + Single Access (Tier 1)

    public void TestDataContainerConstruction()
    {
        var container = new DataContainer(42, "test");
        AssertNotNull(container, "DataContainer constructed");
        TestLogger.Info("DataContainer(42, \"test\") construction passed");
    }

    public void TestDataPropertySingleAccess()
    {
        var container = new DataContainer(42, "test");
        var data = container.Data;
        AssertNotNull(data, "Data property returned non-null");
        AssertEqual(42, data.Value, "Data.Value");
        AssertEqual("test", data.Name, "Data.Name");
        TestLogger.Info($"DataContainer.Data = ({data.Value}, \"{data.Name}\")");
    }

    #endregion

    #region Repeated Access (Tier 2 — R1 regression)

    public void TestRepeatedPropertyAccessNocrash()
    {
        var container = new DataContainer(1, "repeat");
        // Access the struct property many times — should not leak or crash
        for (int i = 0; i < 100; i++)
        {
            var data = container.Data;
            AssertEqual(1, data.Value, $"Data.Value on iteration {i}");
        }
        TestLogger.Info("100 repeated Data property accesses completed without crash");
    }

    public void TestCachedDataFirstAccess()
    {
        var container = new DataContainer(10, "cached");
        var data = container.CachedData;
        AssertNotNull(data, "CachedData returned non-null");
        AssertEqual(10, data.Value, "CachedData.Value");
        AssertEqual("cached", data.Name, "CachedData.Name");
        TestLogger.Info($"DataContainer.CachedData first access = ({data.Value}, \"{data.Name}\")");
    }

    public void TestCachedDataSecondAccess()
    {
        var container = new DataContainer(10, "cached");
        // First access populates cache
        var first = container.CachedData;
        // Second access should use cached value — exercises the lazy-cache-hit path
        var second = container.CachedData;
        AssertEqual(first.Value, second.Value, "CachedData consistent Value");
        AssertEqual(first.Name, second.Name, "CachedData consistent Name");
        TestLogger.Info("CachedData second access (cache hit) completed without crash");
    }

    #endregion

    #region Lifecycle (Tier 3)

    public void TestDisposeAndRecreate()
    {
        // Create, access, dispose, create new, access again — no use-after-free
        var container1 = new DataContainer(1, "first");
        var data1 = container1.Data;
        AssertEqual(1, data1.Value, "First container data");

        // Let first container be GC-eligible
        container1 = null!;

        var container2 = new DataContainer(2, "second");
        var data2 = container2.Data;
        AssertEqual(2, data2.Value, "Second container data after first disposed");
        TestLogger.Info("Dispose + recreate lifecycle passed");
    }

    #endregion
}
