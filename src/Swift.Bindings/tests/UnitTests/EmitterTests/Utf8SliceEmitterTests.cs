// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

// Utf8SliceEmitter uses static mutable state that production emitters also touch.
// xUnit's default class-level parallelism causes races when handler tests (ClassHandler,
// WitnessDispatchEmitter, etc.) trigger Utf8SliceEmitter.EmitIfNeeded concurrently.
// The full suite runs in ~1s, so disabling parallelization has negligible cost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Utf8SliceEmitter — dedup logic, ResetForModule, per-type tracking.
/// </summary>
public class Utf8SliceEmitterTests
{
    public Utf8SliceEmitterTests()
    {
        // Reset shared static state before each test
        Utf8SliceEmitter.ResetForModule();
    }

    [Fact]
    public void EmitIfNeeded_FirstCall_EmitsStruct_ReturnsTrue()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        var result = Utf8SliceEmitter.EmitIfNeeded(swiftWriter);

        Assert.True(result);
        Assert.Contains("SBW_Utf8Slice", output.ToString());
        Assert.True(Utf8SliceEmitter.IsStructEmitted);
    }

    [Fact]
    public void EmitIfNeeded_SecondCall_SkipsStruct_ReturnsFalse()
    {
        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        Utf8SliceEmitter.EmitIfNeeded(swiftWriter1);

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var result = Utf8SliceEmitter.EmitIfNeeded(swiftWriter2);

        Assert.False(result);
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void EmitFreeIfNeeded_FirstCall_EmitsFree_ReturnsTrue()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        var result = Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, "TestModule");

        Assert.True(result);
        Assert.Contains("SBW_Free", output.ToString());
        Assert.Contains("deallocate()", output.ToString());
        Assert.True(Utf8SliceEmitter.IsFreeEmitted);
    }

    [Fact]
    public void EmitFreeIfNeeded_SecondCall_SkipsFree_ReturnsFalse()
    {
        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter1, "TestModule");

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var result = Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter2, "TestModule");

        Assert.False(result);
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void ResetForModule_ClearsAllState()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        Utf8SliceEmitter.EmitIfNeeded(swiftWriter);
        Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, "TestModule");
        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("MyType");

        Assert.True(Utf8SliceEmitter.IsStructEmitted);
        Assert.True(Utf8SliceEmitter.IsFreeEmitted);
        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("MyType"));

        Utf8SliceEmitter.ResetForModule();

        Assert.False(Utf8SliceEmitter.IsStructEmitted);
        Assert.False(Utf8SliceEmitter.IsFreeEmitted);
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("MyType"));
        Assert.Null(Utf8SliceEmitter.CurrentModuleName);
    }

    [Fact]
    public void HasFreePInvokeForType_AfterMark_ReturnsTrue()
    {
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.ErrorCode"));

        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("Swift.TestModule.ErrorCode");

        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.ErrorCode"));
        // Different type should still be false
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.Status"));
    }
}
