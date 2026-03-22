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

    [SkipOnSimulator("VisibilityTest constructor uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestVisibilityTestPublicValue()
    {
        using var v = new VisibilityTest(10);
        AssertEqual(10, v.PublicValue, "Public value accessible");
    }

    [SkipOnSimulator("VisibilityTest constructor uses CallConvSwift (no @_cdecl wrapper)")]
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

    [Skip("KeywordTest wrapper stripped: Swift keyword params (operator:, class:) fail wrapper compilation")]
    public void TestKeywordTestCreation()
    {
        using var kt = new KeywordTest("evt", "del", "op", "cls");
        AssertEqual("evt", kt.Event, "C# keyword 'event' as property works");
    }

    #endregion

    #region Deprecation

    [Skip("DeprecationTest type not generated in current bindings")]
    public void TestDeprecationTestNormalMethod()
    {
        // DeprecationTest — not emitted by generator
    }

    #endregion

    #region Unicode (Skipped)

    [Skip("Unicode identifiers need emitter verification")]
    public void TestUnicodeStructCreation()
    {
        // Café struct with Unicode name — needs emitter verification
    }

    [Skip("Unicode identifiers need emitter verification")]
    public void TestUnicodeFunctionCall()
    {
        // greetCafé function — needs emitter verification
    }

    #endregion
}
