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
}
