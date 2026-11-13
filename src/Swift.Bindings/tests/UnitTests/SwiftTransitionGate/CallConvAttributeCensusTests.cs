// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Linq;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The call-convention attribute census reads each P/Invoke's <c>[UnmanagedCallConv]</c> from
/// metadata: one naming <c>CallConvCdecl</c> is redundant and fails, the Swift convention and an
/// absent attribute pass, and an assembly with no Swift attribute fails rather than passing unread.
/// </summary>
public class CallConvAttributeCensusTests
{
    private const string Preamble = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;

        """;

    private const string SwiftImport = """
            [DllImport("lib", EntryPoint = "$s4Test5swiftyyF")]
            [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
            public static extern void SwiftCall();

        """;

    private static CallConvAttributeCensus Census(string members) =>
        CallConvAttributeCensus.Take(
            SwiftTransitionScannerTests.Compile(Preamble + "public static class C\n{\n" + members + "}\n", "Fixture"),
            "Fixture.dll");

    [Fact]
    public void SwiftAndUnattributedImports_Pass_WithTheirCounts()
    {
        var census = Census(SwiftImport + """
                [DllImport("lib", EntryPoint = "SBW_c")]
                public static extern void CCall();

                [DllImport("lib", EntryPoint = "SBW_d")]
                public static extern void OtherCCall();

            """);

        Assert.True(census.Passed);
        Assert.Equal(3, census.PInvokes);
        Assert.Equal(1, census.Attributed);
        Assert.Equal(1, census.Swift);
        Assert.Empty(census.Redundant);
    }

    [Theory]
    [InlineData("typeof(CallConvCdecl)")]
    [InlineData("typeof(CallConvCdecl), typeof(CallConvSuppressGCTransition)")]
    public void ACdeclAttribute_IsRedundant_AndFails(string conventions)
    {
        var census = Census(SwiftImport + $$"""
                [DllImport("lib", EntryPoint = "SBW_c")]
                [UnmanagedCallConv(CallConvs = new[] { {{conventions}} })]
                public static extern void CCall();

            """);

        Assert.False(census.Passed);
        Assert.Equal(2, census.Attributed);
        var site = Assert.Single(census.Redundant);
        Assert.Contains("C::CCall", site);
        Assert.Contains("SBW_c", site);
        Assert.Contains("CallConvCdecl", site);
    }

    [Fact]
    public void AnAttributeNamingNoConvention_IsRedundant()
    {
        var census = Census(SwiftImport + """
                [DllImport("lib", EntryPoint = "SBW_c")]
                [UnmanagedCallConv]
                public static extern void CCall();

            """);

        Assert.False(census.Passed);
        Assert.Contains("SBW_c", Assert.Single(census.Redundant));
    }

    [Fact]
    public void AModifierWithoutCdecl_IsNotRedundant()
    {
        var census = Census(SwiftImport + """
                [DllImport("lib", EntryPoint = "SBW_c")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSuppressGCTransition) })]
                public static extern void CCall();

            """);

        Assert.True(census.Passed);
        Assert.Equal(2, census.Attributed);
        Assert.Empty(census.Redundant);
    }

    [Fact]
    public void NoSwiftAttribute_Fails_AsTheCensusReadNothing()
    {
        var census = Census("""
                [DllImport("lib", EntryPoint = "SBW_c")]
                public static extern void CCall();

            """);

        Assert.False(census.Passed);
        Assert.Equal(1, census.PInvokes);
        Assert.Equal(0, census.Swift);
        Assert.Empty(census.Redundant);
    }

    [Fact]
    public void OnlyPInvokeImplMethodsAreCounted()
    {
        // The shape a LibraryImport generates: a managed stub forwarding to an inner pinvokeimpl
        // extern that carries the attribute. Only the extern is a P/Invoke.
        var census = Census(SwiftImport + """
                [DllImport("lib", EntryPoint = "$s4Test5otheryyF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                private static extern void __PInvoke();
                public static void Other() => __PInvoke();

            """);

        Assert.True(census.Passed);
        Assert.Equal(2, census.PInvokes);
        Assert.Equal(2, census.Swift);
    }
}
