// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async property getters emitted as Task-returning C# methods.
/// AsyncConfig (struct) and AsyncDataSource (class) both have async computed properties
/// that the generator routes through the method pipeline as GetXxxAsync() methods.
/// </summary>
public class AsyncPropertyTests : TestBase
{
    public AsyncPropertyTests(TestResults results) : base(results) { }

    #region AsyncConfig (struct) — Async Property Getters

    public async Task TestAsyncConfigLabel()
    {
        var config = new AsyncConfig(name: "TestLabel");
        var label = await WithTimeout(config.GetAsyncLabelAsync(), DefaultAsyncTimeout);
        AssertTrue(label.Contains("TestLabel"), $"Async label should contain 'TestLabel', got '{label}'");
        TestLogger.Info($"AsyncConfig.GetAsyncLabelAsync() = '{label}'");
    }

    public async Task TestAsyncConfigNameLength()
    {
        var config = new AsyncConfig(name: "ABC");
        var length = await WithTimeout(config.GetAsyncNameLengthAsync(), DefaultAsyncTimeout);
        AssertEqual(3, length, "Async name length should be 3");
        TestLogger.Info($"AsyncConfig.GetAsyncNameLengthAsync() = {length}");
    }

    #endregion

    #region AsyncDataSource (class) — Async Property Getters

    public async Task TestAsyncDataSourceItemCount()
    {
        var source = new AsyncDataSource(identifier: "Item123");
        var count = await WithTimeout(source.GetAsyncItemCountAsync(), DefaultAsyncTimeout);
        AssertEqual(14, count, "Async item count should be identifier.count * 2 = 14");
        TestLogger.Info($"AsyncDataSource.GetAsyncItemCountAsync() = {count}");
    }

    public async Task TestAsyncDataSourceSummary()
    {
        var source = new AsyncDataSource(identifier: "MySource");
        var summary = await WithTimeout(source.GetAsyncSummaryAsync(), DefaultAsyncTimeout);
        AssertTrue(summary.Contains("MySource"), $"Summary should contain 'MySource', got '{summary}'");
        TestLogger.Info($"AsyncDataSource.GetAsyncSummaryAsync() = '{summary}'");
    }

    #endregion
}
