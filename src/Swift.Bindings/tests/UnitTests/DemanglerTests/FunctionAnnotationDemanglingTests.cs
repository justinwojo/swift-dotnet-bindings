// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the function-type annotation modifiers the demangler must tolerate so that an
/// enclosing function still reduces to a <see cref="FunctionReduction"/> (rather than a
/// <see cref="ReductionError"/>/<see cref="TypeSpecReduction"/>, which silently disables the
/// demangle-based detection tiers in <c>SwiftABIParser.CreateMethodDecl</c>):
///   * <c>Yb</c> — <c>@Sendable</c> closures (<c>ConcurrentFunctionType</c>)
///   * <c>YK</c> — typed throws, <c>throws(E)</c> (<c>TypedThrowsAnnotation</c> with an error child)
///   * <c>Ya</c> — <c>async</c> closures (regression guard for the pre-existing path)
///
/// The mangled symbols below were emitted by the Swift 6.2 toolchain for a module named "S8":
///   public func takesSendable(_ b: @Sendable () -> Void)
///   public func typedThrows() throws(MyErr) -> Int
///   public func sendableVariadic(_ b: @Sendable () -> Void, _ xs: Int...)
///   public func asyncClosure(_ b: () async -> Void)
///   public func plainClosure(_ b: () -> Void)
/// </summary>
public class FunctionAnnotationDemanglingTests
{
    [Fact]
    public void SendableClosureParameter_ReducesToFunction()
    {
        // @Sendable () -> Void  →  ...YbXE...  (ConcurrentFunctionType inside a NoEscapeFunctionType)
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s2S813takesSendableyyyyYbXEF");

        var fn = result as FunctionReduction;
        Assert.NotNull(fn);
        Assert.Equal("takesSendable", fn.Function.Name);
    }

    [Fact]
    public void TypedThrows_ReducesToFunction()
    {
        // throws(MyErr) -> Int  →  ...AA5MyErrVYK...  (TypedThrowsAnnotation with an error-type child)
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s2S811typedThrowsSiyAA5MyErrVYKF");

        var fn = result as FunctionReduction;
        Assert.NotNull(fn);
        Assert.Equal("typedThrows", fn.Function.Name);
    }

    [Fact]
    public void SendableClosureWithVariadic_ReducesToFunctionAndKeepsVariadic()
    {
        // @Sendable () -> Void, Int...  →  ...YbXE_SidtF
        // The leading @Sendable annotation must not disable reduction, otherwise the demangle-based
        // tier-2 variadic detection (HasVariadicElement) never sees the trailing Int... parameter.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s2S816sendableVariadicyyyyYbXE_SidtF");

        var fn = result as FunctionReduction;
        Assert.NotNull(fn);
        Assert.Equal("sendableVariadic", fn.Function.Name);
        Assert.True(SwiftABIParser.HasVariadicElement(fn.Function.ParameterList),
            "tier-2 variadic detection should still surface the trailing Int... parameter");
    }

    [Fact]
    public void AsyncClosureParameter_ReducesToFunction()
    {
        // Regression guard: () async -> Void  →  ...YaXE...  must keep reducing after the
        // four positional FunctionType rules were collapsed into one annotation-tolerant rule.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s2S812asyncClosureyyyyYaXEF");

        var fn = result as FunctionReduction;
        Assert.NotNull(fn);
        Assert.Equal("asyncClosure", fn.Function.Name);
    }

    [Fact]
    public void PlainClosureParameter_ReducesToFunction()
    {
        // Regression guard: an un-annotated () -> Void closure parameter still reduces.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s2S812plainClosureyyyyXEF");

        var fn = result as FunctionReduction;
        Assert.NotNull(fn);
        Assert.Equal("plainClosure", fn.Function.Name);
    }
}
