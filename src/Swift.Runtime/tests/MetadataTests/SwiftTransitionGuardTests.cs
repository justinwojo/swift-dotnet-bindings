// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The transition guard's engine identification and per-runtime refusal table, as pure functions
/// of their inputs (the test host is one fixed runtime), plus the live answer on the host.
/// </summary>
public class SwiftTransitionGuardTests
{
    private const SwiftTransitionFacts Self = SwiftTransitionFacts.SelfRegister;
    private const SwiftTransitionFacts Special = SwiftTransitionFacts.SpecialBeforeArguments;
    private const SwiftTransitionFacts Lowered = SwiftTransitionFacts.LoweredSignature;
    private const SwiftTransitionFacts FpSelf = SwiftTransitionFacts.FunctionPointerSelfRegister;
    private const SwiftTransitionFacts FpSpecial = SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments;
    private const SwiftTransitionFacts FpLowered = SwiftTransitionFacts.FunctionPointerLoweredSignature;

    [Theory]
    [InlineData(true, false, true, "Mono")]
    [InlineData(true, false, false, "Mono")]
    [InlineData(false, true, true, "CoreClr")]
    [InlineData(false, true, false, "CoreClr")]
    [InlineData(false, false, false, "NativeAot")]
    // Neither marker while dynamic code is possible is not NativeAOT: it is unidentified.
    [InlineData(false, false, true, "Unknown")]
    // Both markers is a contradiction, not either engine.
    [InlineData(true, true, false, "Unknown")]
    [InlineData(true, true, true, "Unknown")]
    public void Identify_UsesCoreLibraryMarkersAndNeverGuessesSafe(bool mono, bool coreClr, bool dynamicCode, string expected)
    {
        Assert.Equal(Enum.Parse<SwiftTransitionGuard.Engine>(expected), SwiftTransitionGuard.Identify(mono, coreClr, dynamicCode));
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void Mono_RefusesOnlyTheSelfRegister_InEveryExecutionMode(Architecture architecture)
    {
        foreach (var compiles in new[] { true, false })
        {
            var refused = SwiftTransitionGuard.UnsupportedFactsFor(SwiftTransitionGuard.Engine.Mono, compiles, architecture);
            Assert.Equal(Self | FpSelf, refused & SwiftTransitionGuard.KnownFacts);
        }
    }

    [Fact]
    public void CoreClrInterpreter_Arm64_RefusesSelfOnPInvokeAndSpecialBeforeArgumentsOnBoth()
    {
        var refused = SwiftTransitionGuard.UnsupportedFactsFor(SwiftTransitionGuard.Engine.CoreClr, compilesDynamicCode: false, Architecture.Arm64);
        Assert.Equal(Self | Special | FpSpecial, refused & SwiftTransitionGuard.KnownFacts);
        // A call through a function pointer keeps the thread pointer off the self register.
        Assert.Equal(SwiftTransitionFacts.None, refused & FpSelf);
        // A trailing SwiftError or an aggregate is not refused on arm64.
        Assert.Equal(SwiftTransitionFacts.None, refused & (Lowered | FpLowered));
    }

    [Fact]
    public void CoreClrInterpreter_X64_RefusesEveryLoweredSignature()
    {
        var refused = SwiftTransitionGuard.UnsupportedFactsFor(SwiftTransitionGuard.Engine.CoreClr, compilesDynamicCode: false, Architecture.X64);
        Assert.Equal(Lowered | FpLowered, refused & SwiftTransitionGuard.KnownFacts);
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void NativeAotAndCoreClrJit_RefuseNoKnownShape(Architecture architecture)
    {
        Assert.Equal(SwiftTransitionFacts.None,
            SwiftTransitionGuard.UnsupportedFactsFor(SwiftTransitionGuard.Engine.NativeAot, compilesDynamicCode: false, architecture) & SwiftTransitionGuard.KnownFacts);
        Assert.Equal(SwiftTransitionFacts.None,
            SwiftTransitionGuard.UnsupportedFactsFor(SwiftTransitionGuard.Engine.CoreClr, compilesDynamicCode: true, architecture) & SwiftTransitionGuard.KnownFacts);
    }

    [Theory]
    [InlineData("Unknown", Architecture.Arm64)]
    [InlineData("CoreClr", Architecture.Arm)]
    public void UnidentifiedRuntimeOrArchitecture_RefusesEveryKnownShape(string engine, Architecture architecture)
    {
        var refused = SwiftTransitionGuard.UnsupportedFactsFor(Enum.Parse<SwiftTransitionGuard.Engine>(engine), compilesDynamicCode: false, architecture);
        Assert.Equal(SwiftTransitionGuard.KnownFacts, refused & SwiftTransitionGuard.KnownFacts);
    }

    [Theory]
    [InlineData("NativeAot")]
    [InlineData("Mono")]
    [InlineData("CoreClr")]
    public void AShapeThisRuntimeDoesNotKnow_IsRefusedEverywhere(string engine)
    {
        const SwiftTransitionFacts future = (SwiftTransitionFacts)(1UL << 40);
        var refused = SwiftTransitionGuard.UnsupportedFactsFor(Enum.Parse<SwiftTransitionGuard.Engine>(engine), compilesDynamicCode: true, Architecture.Arm64);
        Assert.Equal(future, refused & future);
    }

    [Fact]
    public void Host_IsCoreClrWithJit_SoEveryKnownShapeIsSupported()
    {
        // The unit-test host is desktop CoreCLR with its JIT: the guard must identify it and let
        // every measured shape through, and must still refuse a shape it does not know.
        Assert.True(SwiftTransitionGuard.IsSupported(SwiftTransitionGuard.KnownFacts));
        Assert.False(SwiftTransitionGuard.IsSupported((SwiftTransitionFacts)(1UL << 40)));
        SwiftTransitionGuard.ThrowIfUnsupported(SwiftTransitionGuard.KnownFacts, "Host.Member()");
        Assert.True(SwiftTransitionGuard.EnsureSupported(Self, "Host.Member()"));
    }

    [Fact]
    public void Refusal_NamesTheMemberTheRefusedShapesAndTheRuntime_AndIsAPlatformNotSupportedException()
    {
        const SwiftTransitionFacts future = (SwiftTransitionFacts)(1UL << 40);
        var ex = Assert.Throws<SwiftTransitionUnsupportedException>(
            () => SwiftTransitionGuard.ThrowIfUnsupported(Self | future, "Widget.Resize(nint)"));
        Assert.IsAssignableFrom<PlatformNotSupportedException>(ex);
        Assert.Equal("Widget.Resize(nint)", ex.Member);
        Assert.Equal(Self | future, ex.Facts);
        Assert.Equal(future, ex.RefusedFacts);
        Assert.Contains("Widget.Resize(nint)", ex.Message);
        Assert.Contains("CoreCLR", ex.Runtime);
    }

    [Fact]
    public void NoFacts_NeverThrows()
    {
        SwiftTransitionGuard.ThrowIfUnsupported(SwiftTransitionFacts.None, "Anything");
    }
}
