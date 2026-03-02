// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the C1 post-processor table in MethodHandler.
/// Validates structural invariants, scope values, and immutability.
/// </summary>
public class PostProcessorTableTests
{
    // ─── Table Structure ──────────────────────────────────────────────

    [Fact]
    public void PostProcessors_HasExactly4Entries()
    {
        Assert.Equal(4, MethodHandler.PostProcessors.Count);
    }

    [Fact]
    public void PostProcessors_DefaultParameterIsFirst()
    {
        Assert.IsType<DefaultParameterOverloadPostProcessor>(MethodHandler.PostProcessors[0]);
    }

    [Fact]
    public void PostProcessors_ContainsAllExpectedAdapters()
    {
        var types = MethodHandler.PostProcessors.Select(p => p.GetType()).ToList();

        Assert.Contains(typeof(DefaultParameterOverloadPostProcessor), types);
        Assert.Contains(typeof(CompletionHandlerPostProcessor), types);
        Assert.Contains(typeof(MarkerProtocolOverloadPostProcessor), types);
        Assert.Contains(typeof(NativeIntOverloadPostProcessor), types);
    }

    [Fact]
    public void PostProcessors_PropertyTypeIsIReadOnlyList()
    {
        var prop = typeof(MethodHandler).GetProperty(
            nameof(MethodHandler.PostProcessors),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<IMethodPostProcessor>), prop!.PropertyType);
    }

    // ─── Scope Values ─────────────────────────────────────────────────

    [Fact]
    public void PostProcessors_DefaultParameterScope_IsAll()
    {
        var dp = MethodHandler.PostProcessors.OfType<DefaultParameterOverloadPostProcessor>().Single();
        Assert.Equal(PostProcessorScope.All, dp.Scope);
    }

    [Fact]
    public void PostProcessors_CompletionHandlerScope_IsMethodsOnly()
    {
        var ch = MethodHandler.PostProcessors.OfType<CompletionHandlerPostProcessor>().Single();
        Assert.Equal(PostProcessorScope.MethodsOnly, ch.Scope);
    }

    [Fact]
    public void PostProcessors_MarkerProtocolScope_IsMethodsOnly()
    {
        var mp = MethodHandler.PostProcessors.OfType<MarkerProtocolOverloadPostProcessor>().Single();
        Assert.Equal(PostProcessorScope.MethodsOnly, mp.Scope);
    }

    [Fact]
    public void PostProcessors_NativeIntScope_IsMethodsOnly()
    {
        var ni = MethodHandler.PostProcessors.OfType<NativeIntOverloadPostProcessor>().Single();
        Assert.Equal(PostProcessorScope.MethodsOnly, ni.Scope);
    }
}
