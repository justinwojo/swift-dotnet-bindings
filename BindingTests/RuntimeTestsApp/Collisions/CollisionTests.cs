// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Tests for case-insensitive enum case collisions, nested type flattening,
/// non-ASCII identifiers, and property/method name collision patterns.
/// </summary>
public class CollisionTests : TestBase
{
    public CollisionTests(TestResults results) : base(results) { }

    #region DrawCommand (Case-Insensitive Enum Collisions)

    public void TestDrawCommandCasesExist()
    {
        // The generator must disambiguate Swift cases that differ only in capitalization
        var move = DrawCommand.Move;
        AssertEqual(0, (int)move, "move (lowercase) maps to 0");

        // The second case (Move) gets renamed to avoid collision
        var moveUpper = DrawCommand.Move2;
        AssertEqual(1, (int)moveUpper, "Move (uppercase) renamed to Move2, maps to 1");
    }

    public void TestDrawCommandDescribe()
    {
        var result = TestLibFunctions.DescribeDrawCommand(DrawCommand.Move);
        AssertEqual("move-lowercase", result, "Lowercase move described correctly");
    }

    #endregion

    #region CollisionStruct (Property Name Collisions)

    public void TestCollisionStructCreation()
    {
        using var cs = new CollisionStruct(42, "mytype", "shown");
        AssertEqual(42, cs.Value, "Value property accessible");
    }

    public void TestCollisionStructFormat()
    {
        using var cs = new CollisionStruct(1, "int", "hello");
        var result = cs.Format();
        AssertTrue(result.Contains("int"), "Format contains type field");
        AssertTrue(result.Contains("hello"), "Format contains display field");
    }

    #endregion

    #region AccentedConfig (Non-ASCII Identifiers)

    public void TestAccentedConfigCreation()
    {
        using var ac = TestLibFunctions.MakeAccentedConfig("TestName", "TestResume");
        AssertEqual("TestName", ac.Name.ToString(), "Name preserved");
        AssertEqual("TestResume", ac.Resume.ToString(), "Resume preserved");
    }

    public void TestAccentedConfigDescribe()
    {
        using var ac = TestLibFunctions.MakeAccentedConfig("Hello", "World");
        var desc = ac.GetDescribe();
        AssertTrue(desc.Contains("Hello"), "Describe contains name");
        AssertTrue(desc.Contains("World"), "Describe contains resume");
    }

    #endregion

    #region MarkupStyle (Non-ASCII Enum)

    public void TestMarkupStyleValues()
    {
        AssertEqual(0, (int)MarkupStyle.Plain, "Plain = 0");
        AssertEqual(1, (int)MarkupStyle.Bold, "Bold = 1");
        AssertEqual(2, (int)MarkupStyle.Italic, "Italic = 2");
        AssertEqual(3, (int)MarkupStyle.Strikethrough, "Strikethrough = 3");
    }

    public void TestFormatText()
    {
        var bold = TestLibFunctions.FormatText(MarkupStyle.Bold, "text");
        AssertEqual("**text**", bold, "Bold formatting applied");

        var italic = TestLibFunctions.FormatText(MarkupStyle.Italic, "word");
        AssertEqual("_word_", italic, "Italic formatting applied");
    }

    #endregion

    #region TypeContainer (Nested Type Flattening)

    public void TestTypeContainerCreation()
    {
        using var tc = new TypeContainer("test");
        AssertEqual("test", tc.Name.ToString(), "TypeContainer name preserved");
    }

    public void TestTypeContainerStateEnum()
    {
        AssertEqual(0, (int)TypeContainer.State.Empty, "Empty = 0");
        AssertEqual(1, (int)TypeContainer.State.Loading, "Loading = 1");
        AssertEqual(2, (int)TypeContainer.State.Loaded, "Loaded = 2");
        AssertEqual(3, (int)TypeContainer.State.Error, "Error = 3");
    }

    public void TestContainerStateName()
    {
        var name = TestLibFunctions.ContainerStateName(TypeContainer.State.Loaded);
        AssertEqual("loaded", name, "State name resolves correctly");
    }

    #endregion

    #region Outer.Inner (Multi-Level Nesting)

    public void TestOuterCreation()
    {
        using var outer = new Outer(42);
        AssertEqual(42, outer.Id, "Outer id preserved");
    }

    public void TestMakeOuterInner()
    {
        using var inner = TestLibFunctions.MakeOuterInner("test-label");
        AssertEqual("test-label", inner.Label.ToString(), "Inner label preserved");
    }

    public void TestMakeOuterInnerDetail()
    {
        using var detail = TestLibFunctions.MakeOuterInnerDetail("detail-info");
        AssertEqual("detail-info", detail.Info.ToString(), "Detail info preserved");
    }

    #endregion
}
