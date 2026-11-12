// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>GridLayoutRegistry</c>'s generic <c>create</c>/<c>register</c> methods over a protocol with an
/// associated type, each with a defaulted parameter BEFORE the required <c>using:</c> module, closed
/// over both class conformers. The specialized primary omits every default, and the shorter overloads
/// built on it expose the interior default while Swift fills the trailing one. Every call checks
/// which Swift declaration ran and what it received, so a C# member bound to the wrong declaration,
/// or passing the wrong value, fails.
/// </summary>
public class CsmInteriorDefaultSiblingsTests : TestBase
{
    public CsmInteriorDefaultSiblingsTests(TestResults results) : base(results) { }

    public void TestCreate_PrimaryFillsEveryDefault()
    {
        using var registry = new GridLayoutRegistry();
        using var column = new ColumnGridModule();
        registry.Create("columns", column);
        AssertEqual(1, registry.LastOverload, "create(grid:options:using:_:) ran");
        AssertEqual("columns", registry.LastName, "name");
        AssertEqual(0, registry.LastOptionsRaw, "options defaulted to []");
        AssertEqual(1, registry.LastModuleTag, "ColumnGridModule");
        AssertFalse(registry.LastBodyRan, "body defaulted to nil");

        using var row = new RowGridModule();
        registry.Create("rows", row);
        AssertEqual(1, registry.LastOverload, "create(grid:options:using:_:) ran for RowGridModule");
        AssertEqual(2, registry.LastModuleTag, "RowGridModule");
    }

    public void TestCreate_InteriorDefaultOverloadPassesOptions()
    {
        using var registry = new GridLayoutRegistry();
        using var options = new GridLayoutOptions(3);
        using var column = new ColumnGridModule();
        registry.Create("columns", options, column);
        AssertEqual(1, registry.LastOverload, "create(grid:options:using:_:) ran");
        AssertEqual(3, registry.LastOptionsRaw, "options passed through");
        AssertEqual(1, registry.LastModuleTag, "ColumnGridModule");
        AssertFalse(registry.LastBodyRan, "body defaulted to nil");

        using var row = new RowGridModule();
        registry.Create("rows", options, row);
        AssertEqual(3, registry.LastOptionsRaw, "options passed through for RowGridModule");
        AssertEqual(2, registry.LastModuleTag, "RowGridModule");
    }

    public void TestCreateIfNotExists_InteriorDefaultOverloadReachesDisfavoredSibling()
    {
        using var registry = new GridLayoutRegistry();
        using var column = new ColumnGridModule();
        registry.CreateGridIfNotExistsUsing("columns", true, column);
        AssertEqual(2, registry.LastOverload, "create(grid:ifNotExists:using:_:) ran");
        AssertTrue(registry.LastIfNotExists, "ifNotExists passed through");
        AssertEqual(1, registry.LastModuleTag, "ColumnGridModule");

        using var row = new RowGridModule();
        registry.CreateGridIfNotExistsUsing("rows", true, row);
        AssertEqual(2, registry.LastOverload, "create(grid:ifNotExists:using:_:) ran for RowGridModule");
        AssertTrue(registry.LastIfNotExists, "ifNotExists passed through for RowGridModule");
        AssertEqual(2, registry.LastModuleTag, "RowGridModule");
    }

    public void TestCreateIfNotExists_PrimaryReachesDisfavoredSibling()
    {
        // The disfavored sibling's primary omits every default, so a bare `create(grid:using:)`
        // call would resolve to the favored `create(grid:options:using:_:)`. The binding named for
        // the disfavored sibling must run that sibling, with `ifNotExists` at its Swift default.
        using var registry = new GridLayoutRegistry();
        using var column = new ColumnGridModule();
        registry.CreateGridIfNotExistsUsing("columns", column);
        AssertEqual(2, registry.LastOverload, "create(grid:ifNotExists:using:_:) ran");
        AssertFalse(registry.LastIfNotExists, "ifNotExists defaulted to false");
        AssertEqual("columns", registry.LastName, "name");
        AssertEqual(1, registry.LastModuleTag, "ColumnGridModule");
        AssertFalse(registry.LastBodyRan, "body defaulted to nil");

        using var row = new RowGridModule();
        registry.CreateGridIfNotExistsUsing("rows", row);
        AssertEqual(2, registry.LastOverload, "create(grid:ifNotExists:using:_:) ran for RowGridModule");
        AssertEqual(2, registry.LastModuleTag, "RowGridModule");
    }

    public void TestRegister_PrimaryAndInteriorDefaultOverload()
    {
        using var registry = new GridLayoutRegistry();
        using var column = new ColumnGridModule();
        registry.Register("columns", column);
        AssertEqual(3, registry.LastOverload, "register(grid:options:using:aliases:) ran");
        AssertEqual(0, registry.LastOptionsRaw, "options defaulted to []");
        AssertEqual(0, registry.LastAliasCount, "aliases defaulted to []");

        using var options = new GridLayoutOptions(2);
        using var row = new RowGridModule();
        registry.Register("rows", options, row);
        AssertEqual(3, registry.LastOverload, "register ran with options");
        AssertEqual(2, registry.LastOptionsRaw, "options passed through");
        AssertEqual(0, registry.LastAliasCount, "aliases defaulted to []");
        AssertEqual(2, registry.LastModuleTag, "RowGridModule");
    }
}
