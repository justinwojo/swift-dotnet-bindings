// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

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

    #region `get async throws` — top-level, nested type, and extension scope

    // These are the shapes where accessor async-ness is most easily lost. Async-ness is inferred
    // from two oracles (a `{getter}Tu`/`{getter}TjTu` sibling symbol in the .tbd, and the
    // .swiftinterface's literal `get async`), so a shape either oracle renders differently is
    // where a mis-detection lands. Reaching these members through the Task-returning projection
    // at all is the assertion that matters: had the getter been read as synchronous, the emitted
    // member would be a plain property backed by a `ref SwiftError` CallConvSwift P/Invoke aimed
    // at an async entry point — the same call, through the wrong ABI.
    //
    // What these tests pin is that end state, with BOTH oracles live: this build has a real .tbd
    // and a real .swiftinterface, so a fact that went missing or came back under the wrong key
    // would still leave the TBD probe answering, and these would stay green. Isolating one oracle
    // from the other is the unit tests' job (AsyncAccessorOracleTests drives an empty TBD symbol
    // set); what only a runtime leg can show is that the projected member actually works.

    public async Task TestAsyncThrowsGetterReturnsValue()
    {
        var analyzer = new AsyncImageAnalyzer(shouldFail: false);
        var label = await WithTimeout(analyzer.GetAnalyzedLabelAsync(), DefaultAsyncTimeout);
        AssertEqual("analyzed", label, "get async throws should return its value on the success path");
        TestLogger.Info($"AsyncImageAnalyzer.GetAnalyzedLabelAsync() = '{label}'");
    }

    public async Task TestAsyncThrowsGetterPropagatesError()
    {
        var analyzer = new AsyncImageAnalyzer(shouldFail: true);
        try
        {
            await WithTimeout(analyzer.GetAnalyzedLabelAsync(), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException from a failing `get async throws`");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("unavailable"),
                $"Expected the Swift error case name in the message, got: {ex.Message}");
            TestLogger.Info($"AsyncImageAnalyzer.GetAnalyzedLabelAsync() threw: {ex.Message}");
        }
    }

    public async Task TestNestedTypeAsyncThrowsGetterReturnsValue()
    {
        // Nested type: the async-throwing getter of a struct nested in a class, reached through
        // a construction path that doesn't depend on nested-type initializer emission.
        var region = new AsyncImageAnalyzer(shouldFail: false).MakeRegion(failing: false);
        var pixels = await WithTimeout(region.GetPixelsAsync(), DefaultAsyncTimeout);
        AssertEqual(42, pixels, "Nested-type get async throws should return 42");
        TestLogger.Info($"AsyncImageAnalyzer.Region.GetPixelsAsync() = {pixels}");
    }

    public async Task TestNestedTypeAsyncThrowsGetterPropagatesError()
    {
        var region = new AsyncImageAnalyzer(shouldFail: false).MakeRegion(failing: true);
        try
        {
            await WithTimeout(region.GetPixelsAsync(), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException from a failing nested `get async throws`");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("unavailable"),
                $"Expected the Swift error case name in the message, got: {ex.Message}");
            TestLogger.Info($"AsyncImageAnalyzer.Region.GetPixelsAsync() threw: {ex.Message}");
        }
    }

    public async Task TestExtensionDeclaredAsyncGetter()
    {
        // Declared in `extension AsyncConfig`, not in the type body — an extension member reaches
        // emission by a different route than a type-body one, and must still project as async.
        var config = new AsyncConfig(name: "Ext");
        var label = await WithTimeout(config.GetAsyncExtensionLabelAsync(), DefaultAsyncTimeout);
        AssertTrue(label.Contains("Ext"), $"Extension async label should contain 'Ext', got '{label}'");
        TestLogger.Info($"AsyncConfig.GetAsyncExtensionLabelAsync() = '{label}'");
    }

    #endregion

    #region Synchronous `get throws` — the wrapper-declined direct path

    // The @_cdecl property wrapper declines a throwing getter (it emits no try/catch), so this
    // property binds a direct CallConvSwift P/Invoke instead. That fall-through is ABI-correct
    // for a SYNCHRONOUS throwing getter: swiftcc returns a thrown error in the dedicated error
    // register, which the generated P/Invoke reads through its `ref SwiftError` out-parameter.
    // Both outcomes are asserted, because "declined the wrapper" must not mean "dropped the
    // error" — and this is also the control that keeps the rejection distinguishable from the
    // genuinely-unsound case of mistaking an async getter for a sync one.

    public void TestSyncThrowingGetterReturnsValue()
    {
        var box = new ThrowingGetterBox(value: 7, shouldFail: false);
        AssertEqual(7, box.CheckedValue, "A non-failing `get throws` should return its value");
        TestLogger.Info($"ThrowingGetterBox.CheckedValue = {box.CheckedValue}");
    }

    public void TestSyncThrowingGetterPropagatesError()
    {
        var box = new ThrowingGetterBox(value: 7, shouldFail: true);
        try
        {
            _ = box.CheckedValue;
            throw new AssertionException("Expected SwiftException from a failing `get throws`");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("unavailable"),
                $"Expected the Swift error case name in the message, got: {ex.Message}");
            TestLogger.Info($"ThrowingGetterBox.CheckedValue threw: {ex.Message}");
        }
    }

    #endregion
}
