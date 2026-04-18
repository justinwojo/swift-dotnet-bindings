// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime tests for ExistentialBypassEmitter non-void return support (#12). Covers
/// methods with an omittable existential-in-bound-generic parameter
/// (e.g. `[any Equatable] = []`) that ALSO return `any Describable`. Exercises both
/// the class and struct branches of the bypass emitter.
/// </summary>
public class ExistentialReturnBypassTests : TestBase
{
    public ExistentialReturnBypassTests(TestResults results) : base(results) { }

    public void TestClassHostMakeItemReturnsExistential()
    {
        var host = new ExistentialReturnBypassHost("classy");
        var item = host.MakeItem();
        AssertNotNull(item, "MakeItem returned non-null IDescribable");
    }

    public void TestClassHostMakeItemDescribe()
    {
        var host = new ExistentialReturnBypassHost("classy");
        var item = host.MakeItem();
        AssertEqual("[erb] classy", item.GetDescribe(), "Class host describe matches stored SimpleItem");
    }

    public void TestStructHostMakeItemReturnsExistential()
    {
        using var host = new ExistentialReturnBypassStructHost("structured");
        var item = host.MakeItem();
        AssertNotNull(item, "Struct host MakeItem returned non-null IDescribable");
    }

    public void TestStructHostMakeItemDescribe()
    {
        using var host = new ExistentialReturnBypassStructHost("structured");
        var item = host.MakeItem();
        AssertEqual("[erb-s] structured", item.GetDescribe(), "Struct host describe matches stored SimpleItem");
    }
}
