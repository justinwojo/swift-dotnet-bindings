// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
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

    #region SyntheticParamCollider (P1-22: user param spelled like an @_cdecl synthetic binding)

    // These exercise the highest-severity arm of the synthetic-name collision class: a USER
    // parameter spelled exactly like a synthetic binding the generator injects into the
    // @_cdecl/@_silgen_name wrapper (self_, resultPtr, errorOut, newValue, tag, _resultBuf).
    // Before the escape, swiftc rejected the duplicate-parameter wrapper, the entry point was
    // SILENTLY dropped from the dylib, and the call crashed at runtime. Each test round-trips a
    // value to prove the wrapper survives compilation AND forwards through the escaped binding.

    public void TestSyntheticAddSelf()
    {
        // `self_` collides with the injected instance-self pointer synthetic.
        using var c = new SyntheticParamCollider(10);
        AssertEqual(13, c.AddSelf(3), "addSelf(self_:) forwards through the escaped self_ binding");
    }

    public void TestSyntheticMakeWide()
    {
        // `resultPtr` collides with the indirect-result buffer synthetic (wide frozen return).
        using var c = new SyntheticParamCollider(0);
        using var wide = c.MakeWide(100);
        AssertEqual(100, wide.A, "makeWide(resultPtr:) a");
        AssertEqual(101, wide.B, "makeWide(resultPtr:) b");
        AssertEqual(102, wide.C, "makeWide(resultPtr:) c");
        AssertEqual(103, wide.D, "makeWide(resultPtr:) d");
        AssertEqual(104, wide.E, "makeWide(resultPtr:) e");
    }

    public void TestSyntheticMightFailSuccess()
    {
        // `errorOut` collides with the throwing error out-param synthetic.
        using var c = new SyntheticParamCollider(0);
        AssertEqual(14, c.MightFail(7), "mightFail(errorOut:) doubles a non-negative value");
    }

    public void TestSyntheticMightFailThrows()
    {
        using var c = new SyntheticParamCollider(0);
        try
        {
            c.MightFail(-1);
            AssertTrue(false, "mightFail(errorOut:) should throw on negative input");
        }
        catch (SwiftException)
        {
            AssertTrue(true, "mightFail(errorOut:) surfaces the Swift error across the escaped binding");
        }
    }

    public void TestSyntheticBump()
    {
        // `newValue` collides with the setter-value synthetic (here on a plain method).
        using var c = new SyntheticParamCollider(5);
        AssertEqual(8, c.Bump(3), "bump(newValue:) forwards through the escaped newValue binding");
    }

    public void TestSyntheticTagPairThunkControl()
    {
        // CONTROL: a plain blittable instance method takes the raw assembly register-shift thunk
        // path (no Swift @_cdecl wrapper, no parameter bindings), so the sibling rename never runs
        // here. Proves the thunk path forwards two params — one literally named `__tag` — correctly.
        using var c = new SyntheticParamCollider(0);
        AssertEqual(2003, c.TagPair(2, 3), "tagPair(tag:__tag:) = tag*1000 + __tag through the thunk path");
    }

    public void TestSyntheticTagPairWide()
    {
        // A REPRO: wide frozen return forces the @_cdecl wrapper. User param `tag` (reserved) is
        // escaped to `__tag2`, dodging the SIBLING param literally named `__tag` (which stays
        // `__tag`). Without sibling-awareness both bindings would be `__tag` → swiftc rejects →
        // wrapper silently dropped → missing entry point. Distinct positions/scales catch a
        // swapped or dropped forward.
        using var c = new SyntheticParamCollider(0);
        using var wide = c.TagPairWide(2, 3);
        AssertEqual(2003, wide.A, "tagPairWide a = tag*1000 + __tag");
        AssertEqual(2, wide.B, "tagPairWide b = tag (escaped binding `__tag2`)");
        AssertEqual(3, wide.C, "tagPairWide c = __tag (sibling binding `__tag`)");
        AssertEqual(-1, wide.D, "tagPairWide d = tag - __tag");
        AssertEqual(5, wide.E, "tagPairWide e = tag + __tag");
    }

    public void TestSyntheticTagPairThrowing()
    {
        // A REPRO on a distinct wrapper-forcing path: `throws` injects `errorOut`, forcing the
        // @_cdecl wrapper where `tag`→`__tag2` again dodges the sibling `__tag`.
        using var c = new SyntheticParamCollider(0);
        AssertEqual(2003, c.TagPairThrowing(2, 3), "tagPairThrowing(tag:__tag:) = tag*1000 + __tag");

        try
        {
            c.TagPairThrowing(-1, 3);
            AssertTrue(false, "tagPairThrowing should throw on negative tag");
        }
        catch (SwiftException)
        {
            AssertTrue(true, "tagPairThrowing surfaces the Swift error across the escaped bindings");
        }
    }

    public void TestSyntheticAddSelfAsync()
    {
        // `self_` collides with the injected self pointer on the async @_cdecl wrapper.
        using var c = new SyntheticParamCollider(10);
        var result = c.AddSelfAsync(3).GetAwaiter().GetResult();
        AssertEqual(13, result, "addSelfAsync(self_:) forwards through the escaped self_ binding");
    }

    public void TestSyntheticInitCollider()
    {
        // `resultPtr` and `self_` constructor params collide with init-wrapper synthetics.
        using var c = new SyntheticInitCollider(4, 6);
        AssertEqual(10, c.Total, "init(resultPtr:self_:) sums the escaped bindings");
    }

    public void TestKnobCombine()
    {
        // `tag` collides with the simple-enum discriminator synthetic on the enum @_cdecl wrapper.
        AssertEqual(5, Knob.On.Combine(4), "Knob.on.combine(tag:) = rawValue(1) + tag(4)");
        AssertEqual(4, Knob.Off.Combine(4), "Knob.off.combine(tag:) = rawValue(0) + tag(4)");
    }

    public void TestKnobFromTag()
    {
        // Static enum factory with a `tag` param (collides with the discriminator synthetic).
        // A Swift static method on an enum surfaces as a plain static on the extensions class.
        AssertEqual(Knob.Off, KnobExtensions.FromTag(0), "fromTag(0) -> off");
        AssertEqual(Knob.On, KnobExtensions.FromTag(1), "fromTag(1) -> on");
    }

    public void TestSyntheticDefaultCollider()
    {
        // `_resultBuf` default param collides with the default-overload result-buffer synthetic.
        using var c = new SyntheticDefaultCollider();
        AssertEqual(15, c.Go(), "go() uses both defaults (5 + 10)");
        AssertEqual(20, c.Go(10), "go(_resultBuf: 10) overrides the first default (10 + 10)");
        AssertEqual(3, c.Go(1, 2), "go(_resultBuf: 1, extra: 2) overrides both (1 + 2)");
    }

    #endregion
}
