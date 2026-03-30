// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Tests for visibility, keywords, deprecation edge cases.
/// Unicode identifiers are skipped (needs emitter verification).
/// </summary>
public class EdgeCaseTests : TestBase
{
    public EdgeCaseTests(TestResults results) : base(results) { }

    #region Visibility

    public void TestVisibilityTestPublicValue()
    {
        using var v = new VisibilityTest(10);
        AssertEqual(10, v.PublicValue, "Public value accessible");
    }

    public void TestVisibilityTestGetPublic()
    {
        using var v = new VisibilityTest(5);
        AssertEqual(5, v.GetPublic(), "GetPublic returns public value");
    }

    #endregion

    #region Open Class + Derived Class

    public void TestOpenBaseClassCreation()
    {
        using var b = new OpenBaseClass("test-label");
        AssertEqual("test-label", b.Label.ToString(), "Base class label preserved");
    }

    public void TestOpenBaseClassProcess()
    {
        using var b = new OpenBaseClass("base");
        var result = b.Process();
        AssertTrue(result.Contains("base"), "Base process includes label");
    }

    public void TestDerivedClassCreation()
    {
        using var d = new DerivedClass();
        AssertNotNull(d, "Derived class created with parameterless constructor");
    }

    public void TestDerivedClassOverriddenProcess()
    {
        using var d = new DerivedClass();
        var result = d.Process();
        AssertNotNull(result, "Derived process returns value");
    }

    #endregion

    #region Keywords

    public void TestKeywordTestCreation()
    {
        using var kt = new KeywordTest("evt", "del", "op", "cls");
        AssertEqual("evt", kt.Event, "C# keyword 'event' as property works");
    }

    public void TestGetKeywordValue()
    {
        var result = TestLibFunctions.GetKeywordValue("mykey");
        AssertEqual("value-for-mykey", result, "getKeywordValue with backtick `for` param works");
    }

    public void TestProcessKeywordParam()
    {
        var result = TestLibFunctions.ProcessKeywordParam("MyClass", 42);
        AssertEqual("MyClass:42", result, "processKeywordParam with backtick `class` param works");
    }

    #endregion

    #region Deprecation

    public void TestDeprecationTestNormalMethod()
    {
        using var dt = new DeprecationTest(10);
        AssertEqual(10, dt.GetNormalMethod(), "NormalMethod returns value");
    }

    #endregion

    #region Unicode

    public void TestUnicodeStructCreation()
    {
        using var café = new Café("TestCafe", 5);
        AssertEqual(5, café.Rating, "Rating preserved for unicode-named struct");
    }

    public void TestUnicodeFunctionCall()
    {
        using var café = new Café("TestCafe", 5);
        var greeting = TestLibFunctions.GreetCafé(café);
        AssertTrue(greeting.Contains("TestCafe"), "greetCafé returns greeting containing name");
    }

    #endregion
}
