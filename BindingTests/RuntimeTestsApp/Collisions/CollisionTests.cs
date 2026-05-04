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

    public void TestDrawCommandLineValues()
    {
        var line = DrawCommand.Line;
        AssertEqual(2, (int)line, "line (lowercase) maps to 2");

        var lineUpper = DrawCommand.Line2;
        AssertEqual(3, (int)lineUpper, "Line (uppercase) renamed to Line2, maps to 3");
    }

    public void TestDrawCommandCloseValue()
    {
        var close = DrawCommand.Close;
        AssertEqual(4, (int)close, "close maps to 4");
    }

    public void TestDrawCommandDescribeLine()
    {
        var result = TestLibFunctions.DescribeDrawCommand(DrawCommand.Line);
        AssertEqual("line-lowercase", result, "Lowercase line described correctly");

        var resultUpper = TestLibFunctions.DescribeDrawCommand(DrawCommand.Line2);
        AssertEqual("Line-uppercase", resultUpper, "Uppercase Line described correctly");
    }

    public void TestDrawCommandDescribeClose()
    {
        var result = TestLibFunctions.DescribeDrawCommand(DrawCommand.Close);
        AssertEqual("close", result, "Close described correctly");
    }

    public void TestDrawCommandDescribeMoveUpper()
    {
        var result = TestLibFunctions.DescribeDrawCommand(DrawCommand.Move2);
        AssertEqual("Move-uppercase", result, "Uppercase Move described correctly");
    }

    #endregion

    #region CSSProperty (String Enum Case Collisions)

    public void TestCSSPropertyCasesExist()
    {
        // CSSProperty is a string raw value enum → generated as a class with static properties.
        // Case-insensitive collision handling: color→Color, Color→Color2, background→Background, BACKGROUND→Background2
        using var color = CSSProperty.Color;
        AssertNotNull(color, "CSSProperty.Color exists");

        using var colorUpper = CSSProperty.Color2;
        AssertNotNull(colorUpper, "CSSProperty.Color2 exists (case collision renamed)");

        using var background = CSSProperty.Background;
        AssertNotNull(background, "CSSProperty.Background exists");

        using var backgroundUpper = CSSProperty.Background2;
        AssertNotNull(backgroundUpper, "CSSProperty.Background2 exists (SCREAMING_CASE collision renamed)");
    }

    public void TestDescribeCSSProperty()
    {
        // The DescribeCSSProperty function returns the rawValue via Swift wrapper.
        // Non-frozen string enums use RawRepresentable class path which calls actual Swift .rawValue.
        using var color = CSSProperty.Color;
        var desc = TestLibFunctions.DescribeCSSProperty(color);
        AssertEqual("color", desc, "DescribeCSSProperty returns raw value for color");
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

    #region ValidationStatus (Emoji Identifier Sanitization)

    public void TestValidationStatusCasesExist()
    {
        // Emoji in case names sanitized to underscores:
        // success → Success, error🚫 → Error__ (2 UTF-16 units = 2 underscores)
        var success = ValidationStatus.Success;
        AssertEqual(0, (int)success, "success maps to 0");

        var error = ValidationStatus.Error__;
        AssertEqual(1, (int)error, "error🚫 sanitized to Error__, maps to 1");

        var warning = ValidationStatus.Warning__;
        AssertEqual(2, (int)warning, "warning🔶 sanitized to Warning__, maps to 2");

        var pending = ValidationStatus.Pending__;
        AssertEqual(3, (int)pending, "pending🔄 sanitized to Pending__, maps to 3");
    }

    public void TestDescribeValidationStatus()
    {
        var result = TestLibFunctions.DescribeValidationStatus(ValidationStatus.Success);
        AssertEqual("success", result, "success described correctly");

        var errorResult = TestLibFunctions.DescribeValidationStatus(ValidationStatus.Error__);
        AssertEqual("error", errorResult, "error🚫 round-trips correctly");

        var warningResult = TestLibFunctions.DescribeValidationStatus(ValidationStatus.Warning__);
        AssertEqual("warning", warningResult, "warning🔶 round-trips correctly");

        var pendingResult = TestLibFunctions.DescribeValidationStatus(ValidationStatus.Pending__);
        AssertEqual("pending", pendingResult, "pending🔄 round-trips correctly");
    }

    public void TestValidationStatusRawValue()
    {
        var result = TestLibFunctions.ValidationStatusRawValue(ValidationStatus.Error__);
        AssertEqual(1, result, "error🚫 raw value is 1");
    }

    #endregion

    #region SearchService (Default Param Collision)

    public void TestSearchServiceFindWithQuery()
    {
        using var service = new SearchService();
        // The explicit 1-param find(query:) should be called (not the 2-param with default)
        var result = service.Find("test");
        AssertEqual("find(test)", result, "Explicit 1-param find(query:) called correctly");
    }

    public void TestSearchServiceFindWithQueryAndLimit()
    {
        using var service = new SearchService();
        // The 2-param find(query:limit:) should be callable with explicit limit
        var result = service.Find("test", 5);
        AssertEqual("find(test, limit=5)", result, "2-param find(query:limit:) called correctly");
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

    #region ResultParameterCollider (P/Invoke return-local rename)

    public void TestResultParamCollisionInstanceCompute()
    {
        // Method has a parameter named `result` and a non-void return — would have
        // produced `var result = PInvoke(... result ...)` (CS0841/CS0136) before the
        // return-local rename to `__result`.
        using var collider = new ResultParameterCollider();
        var value = collider.Compute(21);
        AssertEqual(42, value, "Compute(result: 21) round-trips through renamed return local");
    }

    public void TestResultParamCollisionInstanceDescribe()
    {
        using var collider = new ResultParameterCollider();
        var value = collider.Describe(7);
        AssertEqual("result=7", value, "Describe(result:) returns the formatted Swift string");
    }

    public void TestResultParamCollisionStaticCompute()
    {
        var value = ResultParameterCollider.StaticCompute(10);
        AssertEqual(11, value, "Static compute(result:) round-trips through renamed return local");
    }

    public void TestResultParamCollisionFailableInit()
    {
        // Failable init with a `result` parameter — the TryCreate factory's `out` parameter
        // would have duplicated the input parameter name before the rename.
        var ok = ResultFailable.TryCreate(7, out var success);
        AssertEqual(true, ok, "TryCreate succeeds for non-negative result");
        AssertEqual(7, success.Value, "Failable init preserves the input value");

        var failed = ResultFailable.TryCreate(-1, out var _);
        AssertEqual(false, failed, "TryCreate returns false for negative result");
    }

    #endregion
}
