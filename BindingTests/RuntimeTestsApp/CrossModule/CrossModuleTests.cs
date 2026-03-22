// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Tests for cross-module type references: types from SwiftBindingsTestLibDependency
/// used as parameters and return values in SwiftBindingsTestLib functions.
/// Also tests cross-module protocol conformance (LocalConformant).
/// </summary>
public class CrossModuleTests : TestBase
{
    public CrossModuleTests(TestResults results) : base(results) { }

    #region LocalConformant (Cross-Module Protocol Conformance)

    public void TestLocalConformantCreation()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("test-id", 5);
        AssertEqual("test-id", lc.Identifier.ToString(), "Identifier preserved");
        AssertEqual(5, lc.Tag, "Tag preserved");
    }

    public void TestLocalConformantDescribe()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("hello", 3);
        var desc = lc.GetDescribe();
        AssertTrue(desc.Contains("hello"), "Describe contains identifier");
        AssertTrue(desc.Contains("3"), "Describe contains tag");
    }

    #endregion

    #region Cross-Module Type References

    [Skip("Cross-module type references: DependencyPoint from external module not in generated bindings")]
    public void TestTransformDependencyPoint()
    {
        // transformDependencyPoint accepts DependencyPoint from dependency module
        // Type is external — not generated in SwiftBindingsTestLib.cs
    }

    [Skip("Cross-module type references: DependencyConfig from external module not in generated bindings")]
    public void TestUpgradeDependencyConfig()
    {
        // upgradeDependencyConfig uses DependencyConfig from dependency module
    }

    [Skip("Cross-module type references: DependencyService from external module not in generated bindings")]
    public void TestToggleDependencyService()
    {
        // toggleDependencyService uses DependencyService class from dependency module
    }

    #endregion
}
