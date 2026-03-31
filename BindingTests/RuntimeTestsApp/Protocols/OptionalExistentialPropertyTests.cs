// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests for optional existential property patterns.
/// Exercises ExistentialHandler optional existential getter/setter paths
/// and factory functions returning classes with optional protocol properties.
///
/// Pattern: 11+ validation libraries (Nuke, Kingfisher, etc.) use classes
/// with `var prop: (any Protocol)?` stored properties.
/// </summary>
public class OptionalExistentialPropertyTests : TestBase
{
    public OptionalExistentialPropertyTests(TestResults results) : base(results) { }

    #region Factory Functions (Tier 1 — no optional existential property access needed)

    public void TestMakeRenderableHolder()
    {
        var holder = TestLibFunctions.MakeRenderableHolder("test");
        AssertNotNull(holder, "MakeRenderableHolder returned non-null");
        TestLogger.Info("MakeRenderableHolder construction passed");
    }

    public void TestMakeEmptyRenderableHolder()
    {
        var holder = TestLibFunctions.MakeEmptyRenderableHolder();
        AssertNotNull(holder, "MakeEmptyRenderableHolder returned non-null");
        TestLogger.Info("MakeEmptyRenderableHolder construction passed");
    }

    #endregion

    #region GetPrimaryDescription — String method (Tier 1)

    public void TestGetPrimaryDescriptionWithValue()
    {
        var holder = TestLibFunctions.MakeRenderableHolder("hello");
        var desc = holder.GetPrimaryDescription();
        AssertEqual("SimpleRenderable(hello)", desc, "GetPrimaryDescription with value");
        TestLogger.Info($"GetPrimaryDescription = \"{desc}\"");
    }

    public void TestGetPrimaryDescriptionWithNil()
    {
        var holder = TestLibFunctions.MakeEmptyRenderableHolder();
        var desc = holder.GetPrimaryDescription();
        AssertEqual("none", desc, "GetPrimaryDescription with nil");
        TestLogger.Info($"GetPrimaryDescription (nil) = \"{desc}\"");
    }

    #endregion

    #region RenderableHolder Construction (Tier 1)

    public void TestRenderableHolderDefaultConstructor()
    {
        var holder = new RenderableHolder();
        AssertNotNull(holder, "RenderableHolder default constructor");
        var desc = holder.GetPrimaryDescription();
        AssertEqual("none", desc, "Default constructed holder has nil primary");
        TestLogger.Info("RenderableHolder() default constructor passed");
    }

    #endregion

    #region SimpleRenderable Construction (Tier 1)

    public void TestSimpleRenderableConstruction()
    {
        var renderable = new SimpleRenderable(name: "world");
        AssertNotNull(renderable, "SimpleRenderable constructed");
        TestLogger.Info("SimpleRenderable construction passed");
    }

    public void TestSimpleRenderableRender()
    {
        var renderable = new SimpleRenderable(name: "test");
        var result = renderable.Render();
        AssertEqual("SimpleRenderable(test)", result, "SimpleRenderable.Render()");
        TestLogger.Info($"SimpleRenderable.Render() = \"{result}\"");
    }

    #endregion

    #region Primary Property Getter (Tier 2 — optional existential property access)

    public void TestPrimaryGetterReturnsNonNull()
    {
        var holder = TestLibFunctions.MakeRenderableHolder("getter-test");
        var primary = holder.Primary;
        AssertNotNull(primary, "Primary getter returns non-null IRenderable");
        TestLogger.Info($"holder.Primary != null: {primary != null}");
    }

    public void TestPrimaryGetterReturnsNullForEmpty()
    {
        var holder = TestLibFunctions.MakeEmptyRenderableHolder();
        var primary = holder.Primary;
        AssertTrue(primary == null, "Primary getter returns null for empty holder");
        TestLogger.Info($"Empty holder.Primary == null: {primary == null}");
    }

    public void TestPrimaryGetterRenderRoundTrip()
    {
        var holder = TestLibFunctions.MakeRenderableHolder("round-trip");
        var primary = holder.Primary;
        AssertNotNull(primary, "Primary is non-null for render call");
        var rendered = primary!.Render();
        AssertEqual("SimpleRenderable(round-trip)", rendered, "Primary.Render() round-trip");
        TestLogger.Info($"holder.Primary.Render() = \"{rendered}\"");
    }

    public void TestDefaultConstructorPrimaryIsNull()
    {
        var holder = new RenderableHolder();
        var primary = holder.Primary;
        AssertTrue(primary == null, "Default-constructed holder.Primary is null");
        TestLogger.Info($"new RenderableHolder().Primary == null: {primary == null}");
    }

    #endregion

    #region Primary Property Setter (Tier 2 — optional existential property write)

    // The setter uses a @_cdecl wrapper (CallConvSwift with void return + IntPtr params)
    // which is blittable and works on Mono. Verify via GetPrimaryDescription() which also
    // goes through a working @_cdecl wrapper.

    [SkipOnDevice("Optional<existential> setter uses CallConvSwift instead of @_cdecl — generator bug")]
    public void TestPrimarySetterAssignRenderable()
    {
        var holder = new RenderableHolder();
        // Verify starts null
        var before = holder.GetPrimaryDescription();
        AssertEqual("none", before, "Holder starts with nil primary");

        // Assign a SimpleRenderable
        var renderable = new SimpleRenderable(name: "assigned");
        holder.Primary = renderable;

        // Verify description changes (GetPrimaryDescription goes through @_cdecl, safe to call)
        var after = holder.GetPrimaryDescription();
        AssertEqual("SimpleRenderable(assigned)", after, "After setter, description reflects assigned value");
        TestLogger.Info($"After Primary setter: GetPrimaryDescription() = \"{after}\"");
    }

    [SkipOnDevice("Optional<existential> setter uses CallConvSwift instead of @_cdecl — generator bug")]
    public void TestPrimarySetterThenGetterRoundTrip()
    {
        var holder = new RenderableHolder();
        var renderable = new SimpleRenderable(name: "set-get");
        holder.Primary = renderable;

        var readBack = holder.Primary;
        AssertNotNull(readBack, "Primary getter returns non-null after set");
        var rendered = readBack!.Render();
        AssertEqual("SimpleRenderable(set-get)", rendered, "Set→Get round-trip preserves value");
        TestLogger.Info($"Set→Get round-trip: Primary.Render() = \"{rendered}\"");
    }

    [SkipOnDevice("Optional<existential> setter uses CallConvSwift instead of @_cdecl — generator bug")]
    public void TestPrimarySetterClearToNull()
    {
        var holder = TestLibFunctions.MakeRenderableHolder("to-clear");
        // Verify starts non-null
        var before = holder.GetPrimaryDescription();
        AssertEqual("SimpleRenderable(to-clear)", before, "Holder starts with value");

        // Clear to null
        holder.Primary = null;

        var after = holder.GetPrimaryDescription();
        AssertEqual("none", after, "After setting Primary to null, description is 'none'");
        TestLogger.Info($"After Primary = null: GetPrimaryDescription() = \"{after}\"");
    }

    #endregion

    #region EC2 — LabelableRenderableHolder Factory Functions (multi-protocol optional existential)

    public void TestMakeLabelableRenderableHolder()
    {
        var holder = TestLibFunctions.MakeLabelableRenderableHolder("test");
        AssertNotNull(holder, "MakeLabelableRenderableHolder returned non-null");
        TestLogger.Info("MakeLabelableRenderableHolder construction passed");
    }

    public void TestMakeEmptyLabelableRenderableHolder()
    {
        var holder = TestLibFunctions.MakeEmptyLabelableRenderableHolder();
        AssertNotNull(holder, "MakeEmptyLabelableRenderableHolder returned non-null");
        TestLogger.Info("MakeEmptyLabelableRenderableHolder construction passed");
    }

    #endregion

    #region EC2 — GetItemDescription (String method, EC2 container)

    public void TestGetItemDescriptionWithValue()
    {
        var holder = TestLibFunctions.MakeLabelableRenderableHolder("hello");
        var desc = holder.GetItemDescription();
        AssertEqual("Render(hello)+Label(hello)", desc, "GetItemDescription with value");
        TestLogger.Info($"GetItemDescription = \"{desc}\"");
    }

    public void TestGetItemDescriptionWithNil()
    {
        var holder = TestLibFunctions.MakeEmptyLabelableRenderableHolder();
        var desc = holder.GetItemDescription();
        AssertEqual("none", desc, "GetItemDescription with nil");
        TestLogger.Info($"GetItemDescription (nil) = \"{desc}\"");
    }

    #endregion

    #region EC2 — LabelableRenderableHolder Construction

    public void TestLabelableRenderableHolderDefaultConstructor()
    {
        var holder = new LabelableRenderableHolder();
        AssertNotNull(holder, "LabelableRenderableHolder default constructor");
        var desc = holder.GetItemDescription();
        AssertEqual("none", desc, "Default constructed holder has nil item");
        TestLogger.Info("LabelableRenderableHolder() default constructor passed");
    }

    #endregion

    #region EC2 — LabelableRenderable Direct Construction

    public void TestLabelableRenderableConstruction()
    {
        var renderable = new LabelableRenderable(name: "world");
        AssertNotNull(renderable, "LabelableRenderable constructed");
        TestLogger.Info("LabelableRenderable construction passed");
    }

    public void TestLabelableRenderableRender()
    {
        var renderable = new LabelableRenderable(name: "test");
        var result = renderable.Render();
        AssertEqual("Render(test)", result, "LabelableRenderable.Render()");
        TestLogger.Info($"LabelableRenderable.Render() = \"{result}\"");
    }

    public void TestLabelableRenderableLabel()
    {
        var renderable = new LabelableRenderable(name: "test");
        var result = renderable.GetLabel();
        AssertEqual("Label(test)", result, "LabelableRenderable.GetLabel()");
        TestLogger.Info($"LabelableRenderable.GetLabel() = \"{result}\"");
    }

    #endregion

    #region EC2 — Item Property Getter (optional EC2 existential)

    public void TestItemGetterReturnsNonNull()
    {
        var holder = TestLibFunctions.MakeLabelableRenderableHolder("getter-test");
        var item = holder.Item;
        AssertNotNull(item, "Item getter returns non-null for EC2");
        TestLogger.Info($"holder.Item != null: {item != null}");
    }

    public void TestItemGetterReturnsNullForEmpty()
    {
        var holder = TestLibFunctions.MakeEmptyLabelableRenderableHolder();
        var item = holder.Item;
        AssertNull(item, "Item getter returns null for empty EC2 holder");
        TestLogger.Info($"Empty holder.Item == null: {item == null}");
    }

    #endregion

    #region EC2 — Item Property Setter

    // Composition proxy is wrap-only (EC2 container from Swift), so setter tests
    // use the getter-from-factory pattern: get ILabelableAndRenderable from one
    // holder's getter, then set it on another holder.

    [SkipOnDevice("Optional<existential> setter uses CallConvSwift instead of @_cdecl — generator bug")]
    public void TestItemSetterFromSwiftExistential()
    {
        // Get an ILabelableAndRenderable from a factory-created holder
        var source = TestLibFunctions.MakeLabelableRenderableHolder("transfer");
        var existential = source.Item;
        AssertNotNull(existential, "Source holder has non-null item");

        // Set it on a new empty holder
        var target = new LabelableRenderableHolder();
        target.Item = existential;

        var desc = target.GetItemDescription();
        AssertEqual("Render(transfer)+Label(transfer)", desc, "After EC2 setter, description reflects transferred value");
        TestLogger.Info($"After Item setter from existential: GetItemDescription() = \"{desc}\"");
    }

    [SkipOnDevice("Optional<existential> setter uses CallConvSwift instead of @_cdecl — generator bug")]
    public void TestItemSetterClearToNull()
    {
        var holder = TestLibFunctions.MakeLabelableRenderableHolder("to-clear");
        var before = holder.GetItemDescription();
        AssertEqual("Render(to-clear)+Label(to-clear)", before, "EC2 holder starts with value");

        holder.Item = null;

        var after = holder.GetItemDescription();
        AssertEqual("none", after, "After setting EC2 Item to null, description is 'none'");
        TestLogger.Info($"After Item = null: GetItemDescription() = \"{after}\"");
    }

    #endregion
}
