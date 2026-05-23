// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for AppIntents 0.12.0 sites #5 and #6: result-builder
/// static methods on a non-generic host whose only parameter is a variadic
/// pack. The two overloads exercise both shapes documented in
/// <c>VariadicResultBuilder.swift</c>:
/// <list type="bullet">
///   <item><c>buildBlock(_ components: T...) -&gt; [T]</c> (site #5)</item>
///   <item><c>buildBlock(_ components: [T]...) -&gt; [T]</c> (site #6)</item>
/// </list>
/// <para>Before doc 14 these signatures emitted with <c>[Obsolete(SB0001)]</c>
/// direct-CallConvSwift dispatch because <c>MethodWrapperEmitter</c> rejected
/// any method with a variadic parameter. The widened gate routes them through
/// an <c>@_cdecl</c> wrapper that uses <c>unsafeBitCast</c> to bridge the
/// type-system mismatch between <c>(T...) -&gt; R</c> and <c>([T]) -&gt; R</c> —
/// they share the same SIL-level ABI (Array&lt;T&gt;), but Swift refuses to
/// splat a runtime array into a variadic call expression without the cast.</para>
/// </summary>
public class VariadicResultBuilderTests : TestBase
{
    public VariadicResultBuilderTests(TestResults results) : base(results) { }

    public void TestVariadicBuildBlock_AcceptsElementSplat()
    {
        // Site #5 shape: static func buildBlock(_ components: T...) -> [T]
        // The runtime call passes a real Swift array — the @_cdecl wrapper's
        // unsafeBitCast bridges it to the variadic function reference.
        using var first = new VariadicSection("alpha");
        using var second = new VariadicSection("beta");

        var result = VariadicSectionBuilder.BuildBlock(new[] { first, second });

        AssertNotNull(result, "BuildBlock(_:...) returns a non-null array");
        AssertEqual(2, result.Count, "BuildBlock(_:...) preserves element count");
        AssertEqual("alpha", result[0].Title.ToString(), "First element preserved");
        AssertEqual("beta", result[1].Title.ToString(), "Second element preserved");
    }

    public void TestVariadicBuildBlock_AcceptsArrayOfArraysSplat()
    {
        // Site #6 shape: static func buildBlock(_ components: [T]...) -> [T]
        // Different ABI from site #5 (outer Array<Array<T>>) but the same
        // wrapper-emission obstacle: the variadic-vs-array type-checker
        // divergence, bridged by unsafeBitCast.
        using var a = new VariadicSection("a");
        using var b = new VariadicSection("b");
        using var c = new VariadicSection("c");

        var groupOne = new[] { a, b };
        var groupTwo = new[] { c };

        var result = VariadicSectionBuilder.BuildBlock(new[] { groupOne, groupTwo });

        AssertNotNull(result, "BuildBlock(_:[T]...) returns a non-null array");
        AssertEqual(3, result.Count, "BuildBlock(_:[T]...) flattens two groups into three elements");
        AssertEqual("a", result[0].Title.ToString(), "Flatten preserves order: a");
        AssertEqual("b", result[1].Title.ToString(), "Flatten preserves order: b");
        AssertEqual("c", result[2].Title.ToString(), "Flatten preserves order: c");
    }
}
