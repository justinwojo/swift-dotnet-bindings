// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for method-level generic parameters bridged via implicit existential opening.
/// These methods have their own generic type parameters (e.g., func foo&lt;T: Describable&gt;)
/// and are emitted with @_cdecl wrappers that load existential containers.
/// </summary>
public class MethodLevelGenericTests : TestBase
{
    public MethodLevelGenericTests(TestResults results) : base(results) { }

    public void TestGenericMethodHost_VoidGenericMethod()
    {
        var host = new GenericMethodHost(label: "test");
        var item = new SimpleDescribable(description: "hello");
        // Should not crash — void method with generic param
        host.PrintDescription(item);
        TestLogger.Info("GenericMethodHost.PrintDescription completed without crash");
    }

    public void TestGenericMethodHost_ReturnsString()
    {
        var host = new GenericMethodHost(label: "host");
        var item = new SimpleDescribable(description: "world");
        var result = host.GetDescription(item);
        AssertEqual("host: world", result, "GetDescription<T: Describable>");
        TestLogger.Info($"GenericMethodHost.GetDescription = {result}");
    }

    public void TestGenericMethodHost_StaticMethod()
    {
        var item = new SimpleDescribable(description: "static-test");
        var result = GenericMethodHost.StaticDescribe(item);
        AssertEqual("static: static-test", result, "StaticDescribe<T: Describable>");
        TestLogger.Info($"GenericMethodHost.StaticDescribe = {result}");
    }

    public void TestGenericMethodHost_MixedParams()
    {
        var host = new GenericMethodHost(label: "tagged");
        var item = new SimpleDescribable(description: "item");
        var result = host.DescribeWithTag(item, tag: 42);
        AssertEqual("[42] tagged: item", result, "DescribeWithTag<T>(_, tag:)");
        TestLogger.Info($"GenericMethodHost.DescribeWithTag = {result}");
    }

    /// Calls the fully generic arm of `SimpleRowAdapter.layoutedAdapter&lt;T&gt;` at a type
    /// argument the concrete-specialization engine declines to specialize, so there is no
    /// closed overload to fall back on. This is the shape that aborts under Mono full-AOT
    /// when the binding dispatches directly on the Swift ABI thunk.
    public void TestLayoutedAdapter_GenericArm_NoClosedSpecialization()
    {
        var adapter = Functions.MakeSimpleRowAdapter();
        var layout = new FrozenRowLayout(columnCount: 7);
        var result = adapter.LayoutedAdapter(layout);
        AssertEqual("adapted:7", result, "layoutedAdapter<T: RowLayout>(from:) generic arm");
        TestLogger.Info($"SimpleRowAdapter.LayoutedAdapter<FrozenRowLayout> = {result}");
    }

    /// Adversarial: a type argument that satisfies the binding's managed constraint but whose
    /// Swift metadata carries no `RowLayout` conformance. The opening wrapper casts the metadata
    /// before it reads the payload, so the call must come back as a typed managed exception
    /// rather than reinterpreting the bytes at the wrong type.
    public void TestLayoutedAdapter_NonConformingTypeArgument_Refused()
    {
        var adapter = Functions.MakeSimpleRowAdapter();
        var impostor = new NotARowLayout(columnCount: 3);
        try
        {
            var result = adapter.LayoutedAdapter(impostor);
            AssertTrue(false, $"expected a refusal for a non-conforming type argument, got '{result}'");
        }
        catch (Swift.Runtime.SwiftRuntimeException ex)
        {
            AssertTrue(ex.Message.Contains("NotARowLayout"),
                $"refusal names the offending type argument (was: {ex.Message})");
            // Module-qualified, and paired with the "does not conform" wording: a bare "RowLayout"
            // is already a substring of the impostor's own name, so it would pass on any message
            // that merely mentions the type argument.
            AssertTrue(ex.Message.Contains("does not conform to Swift protocol 'SwiftBindingsTestLib.RowLayout'"),
                $"refusal names the unsatisfied constraint (was: {ex.Message})");
            TestLogger.Info($"SimpleRowAdapter.LayoutedAdapter<NotARowLayout> refused: {ex.Message}");
        }
    }
}
