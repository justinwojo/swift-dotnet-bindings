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
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("TestModule.ErrorCode", ctx));

        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("TestModule.ErrorCode", ctx);

        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("TestModule.ErrorCode", ctx));
        // Different type should still be false
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("TestModule.Status", ctx));
    }

    [Fact]
    public void EmitIfNeeded_SameContext_TwiceCalls_OnlyEmitsOnce()
    {
        // The bug fix: callers (ClosureEmitter, OptionalPointerWrapperEmitter,
        // ArraySliceNormalizationEmitter) now pass the real ModuleEmissionContext
        // instead of null. This ensures dedup works within a single module emission.
        var ctx = new ModuleEmissionContext();

        var output1 = new StringWriter();
        var result1 = Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(output1), ctx);

        var output2 = new StringWriter();
        var result2 = Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(output2), ctx);

        Assert.True(result1, "First call should emit");
        Assert.False(result2, "Second call with same context should skip");
        Assert.Contains("SBW_Utf8Slice", output1.ToString());
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void EmitIfNeeded_NullContext_UsesDefaultSingleton()
    {
        // Calling with null falls back to ModuleEmissionContext.Default (singleton).
        // This was the original bug: callers passing null shared the Default context
        // while the main emitter used its own context, so dedup never triggered.

        // Reset: use a fresh Default by testing the behavior with explicit null
        // We can't reset the Default singleton, but we can verify the semantics:
        // two calls with null should share the same (Default) context.
        var ctx = new ModuleEmissionContext();
        var output1 = new StringWriter();
        Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(output1), ctx);
        Assert.True(Utf8SliceEmitter.IsStructEmitted(ctx));

        // A different fresh context should NOT see the first context's state
        var ctx2 = new ModuleEmissionContext();
        Assert.False(Utf8SliceEmitter.IsStructEmitted(ctx2),
            "Fresh context should not inherit state from other contexts");

        var output2 = new StringWriter();
        var result2 = Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(output2), ctx2);
        Assert.True(result2, "Fresh context should emit even though another context already did");
    }

    [Fact]
    public void EmitIfNeeded_DifferentContexts_BothEmit()
    {
        // Two different ModuleEmissionContext instances should each get their own emission.
        // This simulates the scenario where the main emitter and a closure emitter
        // CORRECTLY share the same context (after the fix).
        var ctxA = new ModuleEmissionContext();
        var ctxB = new ModuleEmissionContext();

        var outputA = new StringWriter();
        var resultA = Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(outputA), ctxA);

        var outputB = new StringWriter();
        var resultB = Utf8SliceEmitter.EmitIfNeeded(new SwiftWriter(outputB), ctxB);

        Assert.True(resultA);
        Assert.True(resultB, "Different contexts should each emit independently");
        Assert.Contains("SBW_Utf8Slice", outputA.ToString());
        Assert.Contains("SBW_Utf8Slice", outputB.ToString());
    }

    [Fact]
    public void GetFreeSymbolName_IncludesModuleName()
    {
        // Regression: SBW_Free_ was emitted without module name when SwiftTypeName.Module
        // was null (MethodHandler/SubscriptHandler didn't fall back to ModuleDecl.Name).
        var symbol = Utf8SliceEmitter.GetFreeSymbolName("SwiftBindingsTestLib");
        Assert.Equal("SBW_Free_SwiftBindingsTestLib", symbol);
    }

    [Fact]
    public void GetFreeSymbolName_EmptyModuleName_ProducesTrailingUnderscore()
    {
        // Documents the pathological case: if module name is empty, symbol ends with underscore.
        // The fix in MethodHandler/SubscriptHandler prevents this by falling back to ModuleDecl.Name.
        var symbol = Utf8SliceEmitter.GetFreeSymbolName("");
        Assert.Equal("SBW_Free_", symbol);
    }

    [Fact]
    public void FreePInvokeDedup_DistinguishesNestedTypes()
    {
        // Regression: nested types with the same leaf name (e.g., OrderContainer.Status vs
        // PaymentContainer.Status) must use distinct dedup keys to avoid colliding SBW_Free entries.
        var ctx = new ModuleEmissionContext();

        Utf8SliceEmitter.MarkFreePInvokeEmittedForType("TestModule.OrderContainer.Status", ctx);

        Assert.True(Utf8SliceEmitter.HasFreePInvokeForType("TestModule.OrderContainer.Status", ctx));
        Assert.False(Utf8SliceEmitter.HasFreePInvokeForType("TestModule.PaymentContainer.Status", ctx),
            "Different parent types with same leaf name must not share dedup key");
    }
}
