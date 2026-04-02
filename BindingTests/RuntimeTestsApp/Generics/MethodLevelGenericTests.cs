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
}
