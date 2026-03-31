// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Tests for types that accept collections in their constructors:
/// DataBuffer (int array), PathResolver (string array), LabeledBuffer (string + int array).
/// </summary>
public class ConstructorCollectionTests : TestBase
{
    public ConstructorCollectionTests(TestResults results) : base(results) { }

    #region Tier 1 — DataBuffer Blittable Properties

    public void TestDataBufferGetCount()
    {
        var arr = new SwiftArray<int>();
        arr.Append(10);
        arr.Append(20);
        arr.Append(30);
        var buffer = new DataBuffer(arr);
        AssertEqual(3, buffer.GetCount(), "DataBuffer count should be 3");
        TestLogger.Info($"DataBuffer.GetCount() = {buffer.GetCount()}");
    }

    public void TestDataBufferSum()
    {
        var arr = new SwiftArray<int>();
        arr.Append(1);
        arr.Append(2);
        arr.Append(3);
        arr.Append(4);
        arr.Append(5);
        var buffer = new DataBuffer(arr);
        AssertEqual(15, buffer.Sum(), "DataBuffer sum of [1..5] should be 15");
        TestLogger.Info($"DataBuffer.Sum() = {buffer.Sum()}");
    }

    public void TestDataBufferEmptyArray()
    {
        var arr = new SwiftArray<int>();
        var buffer = new DataBuffer(arr);
        AssertEqual(0, buffer.GetCount(), "Empty DataBuffer count should be 0");
        AssertEqual(0, buffer.Sum(), "Empty DataBuffer sum should be 0");
        TestLogger.Info("DataBuffer empty array passed");
    }

    public void TestDataBufferSingleElement()
    {
        var arr = new SwiftArray<int>();
        arr.Append(42);
        var buffer = new DataBuffer(arr);
        AssertEqual(1, buffer.GetCount(), "Single element count");
        AssertEqual(42, buffer.Sum(), "Single element sum");
        TestLogger.Info("DataBuffer single element passed");
    }

    #endregion

    #region Tier 2 — DataBuffer Optional, PathResolver, LabeledBuffer

    public void TestDataBufferGetFirstWithElements()
    {
        var arr = new SwiftArray<int>();
        arr.Append(99);
        arr.Append(100);
        var buffer = new DataBuffer(arr);
        var first = buffer.GetFirst();
        AssertTrue(first.HasValue, "GetFirst on non-empty buffer should have value");
        AssertEqual(99, first!.Value, "First element should be 99");
        TestLogger.Info($"DataBuffer.GetFirst() = {first}");
    }

    public void TestDataBufferGetFirstEmpty()
    {
        var arr = new SwiftArray<int>();
        var buffer = new DataBuffer(arr);
        var first = buffer.GetFirst();
        AssertFalse(first.HasValue, "GetFirst on empty buffer should not have value");
        TestLogger.Info("DataBuffer.GetFirst() on empty = null");
    }

    public void TestPathResolverFullPath()
    {
        var resolver = new PathResolver(new[] { "usr", "local", "bin" });
        var fullPath = resolver.GetFullPath();
        AssertEqual("usr.local.bin", fullPath, "Full path should be 'usr.local.bin'");
        TestLogger.Info($"PathResolver.GetFullPath() = \"{fullPath}\"");
    }

    public void TestPathResolverGetDepth()
    {
        var resolver = new PathResolver(new[] { "usr", "local", "bin" });
        AssertEqual(3, resolver.GetDepth(), "Depth should be 3");
        TestLogger.Info($"PathResolver.GetDepth() = {resolver.GetDepth()}");
    }

    public void TestPathResolverSingleComponent()
    {
        var resolver = new PathResolver(new[] { "root" });
        AssertEqual(1, resolver.GetDepth(), "Single component depth should be 1");
        AssertEqual("root", resolver.GetFullPath(), "Single component full path");
        TestLogger.Info($"PathResolver single component: \"{resolver.GetFullPath()}\"");
    }

    public void TestLabeledBufferDescribe()
    {
        var arr = new SwiftArray<int>();
        arr.Append(10);
        arr.Append(20);
        arr.Append(30);
        var buffer = new LabeledBuffer("Scores", arr);
        var desc = buffer.GetDescribe();
        AssertTrue(desc.Contains("Scores"), "Describe should contain label");
        AssertTrue(desc.Contains("3"), "Describe should contain count");
        TestLogger.Info($"LabeledBuffer.GetDescribe() = \"{desc}\"");
    }

    public void TestLabeledBufferEmptyData()
    {
        var arr = new SwiftArray<int>();
        var buffer = new LabeledBuffer("Empty", arr);
        var desc = buffer.GetDescribe();
        AssertTrue(desc.Contains("Empty"), "Describe should contain label even with no data");
        TestLogger.Info($"LabeledBuffer empty: \"{desc}\"");
    }

    #endregion

    #region Existential Array Constructor (Swinject NativeAOT regression)
    // Regression test: SwiftArray<ExistentialContainer1> type init must not throw
    // TypeInitializationException when NativeAotInitialize() fails for existential types.
    // ProcessingPipeline.init(modes: [any ProcessingMode]) uses SwiftArray<ExistentialContainer1>
    // internally — this is the same pattern as Swinject Container.init(behaviors: [any Behavior]).

    [SkipOnDevice("ExistentialArray constructor SIGKILL under NativeAOT — SwiftArray<EC1> type cast crash")]
    public void TestProcessingPipelineWithExistentialArray()
    {
        var modes = new IProcessingMode[] { new SimpleMode(), new StrictMode() };
        using var pipeline = new ProcessingPipeline(modes);
        AssertEqual(2, pipeline.GetModeCount(), "ProcessingPipeline should have 2 modes");
        TestLogger.Info($"ProcessingPipeline.GetModeCount() = {pipeline.GetModeCount()}");
    }

    [SkipOnDevice("ExistentialArray constructor SIGKILL under NativeAOT — SwiftArray<EC1> type cast crash")]
    public void TestProcessingPipelineEmptyExistentialArray()
    {
        var modes = Array.Empty<IProcessingMode>();
        using var pipeline = new ProcessingPipeline(modes);
        AssertEqual(0, pipeline.GetModeCount(), "ProcessingPipeline should have 0 modes");
        TestLogger.Info("ProcessingPipeline empty modes passed");
    }

    #endregion
}
