// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

// Utf8SliceEmitter state is now on ModuleEmissionContext, so tests use fresh contexts.
// Parallelization remains disabled for compatibility with other handler tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Utf8SliceEmitter — dedup logic, per-module context, per-type tracking.
/// </summary>
public class Utf8SliceEmitterTests
{
    [Fact]
    public void EmitIfNeeded_FirstCall_EmitsStruct_ReturnsTrue()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        var result = Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);

        Assert.True(result);
        Assert.Contains("SBW_Utf8Slice", output.ToString());
        Assert.True(Utf8SliceEmitter.IsStructEmitted(ctx));
    }

    [Fact]
    public void EmitIfNeeded_SecondCall_SkipsStruct_ReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        Utf8SliceEmitter.EmitIfNeeded(swiftWriter1, ctx);

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var result = Utf8SliceEmitter.EmitIfNeeded(swiftWriter2, ctx);

        Assert.False(result);
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void EmitFreeIfNeeded_FirstCall_EmitsFree_ReturnsTrue()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        var result = Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, "TestModule", ctx);

        Assert.True(result);
        Assert.Contains("SBW_Free", output.ToString());
        Assert.Contains("deallocate()", output.ToString());
        Assert.True(Utf8SliceEmitter.IsFreeEmitted(ctx));
    }

    [Fact]
    public void EmitFreeIfNeeded_SecondCall_SkipsFree_ReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter1, "TestModule", ctx);

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var result = Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter2, "TestModule", ctx);

        Assert.False(result);
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void FreshContext_HasCleanState()
    {
        // First context — populate state
        var ctx1 = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx1);
        Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, "TestModule", ctx1);
        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("MyType", ctx1);

        Assert.True(Utf8SliceEmitter.IsStructEmitted(ctx1));
        Assert.True(Utf8SliceEmitter.IsFreeEmitted(ctx1));
        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("MyType", ctx1));

        // Second fresh context — should be clean
        var ctx2 = new ModuleEmissionContext();

        Assert.False(Utf8SliceEmitter.IsStructEmitted(ctx2));
        Assert.False(Utf8SliceEmitter.IsFreeEmitted(ctx2));
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("MyType", ctx2));
        Assert.Null(Utf8SliceEmitter.CurrentModuleName(ctx2));
    }

    [Fact]
    public void HasFreePInvokeForType_AfterMark_ReturnsTrue()
    {
        var ctx = new ModuleEmissionContext();
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.ErrorCode", ctx));

        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("Swift.TestModule.ErrorCode", ctx);

        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.ErrorCode", ctx));
        // Different type should still be false
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("Swift.TestModule.Status", ctx));
    }
}
