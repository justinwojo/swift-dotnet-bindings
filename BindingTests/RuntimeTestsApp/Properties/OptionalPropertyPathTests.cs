// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Tests Optional property getters/setters through different generator code paths.
/// Exercises both the IsDecomposedOptionalType and GetCdeclParamMapping branches
/// in PropertyWrapperEmitter.EmitSetterWrapper/EmitGetterWrapper.
///
/// Coverage gaps addressed:
/// - Non-decomposed Optional&lt;Double&gt; setter (PropertyWrapperEmitter:471-496)
/// - Decomposed Optional&lt;ComplexEnum&gt; setter (PropertyWrapperEmitter:461-469)
/// - Optional&lt;Class&gt; property (IsDecomposedOptionalType:250 → false)
/// - Optional&lt;BlittablePrimitive&gt; tag-byte fixup (PropertyWrapperEmitter:349-372)
/// - Static Optional setter (PropertyWrapperEmitter:564)
/// </summary>
public class OptionalPropertyPathTests : TestBase
{
    public OptionalPropertyPathTests(TestResults results) : base(results) { }

    #region CacheConfig — Non-decomposed Optional<Double> (GetCdeclParamMapping path)

    public void TestCacheConfigConstruction()
    {
        var config = new CacheConfig(ttl: 60.0, maxSize: 100);
        AssertNotNull(config, "CacheConfig constructed");
        AssertEqual(100, config.MaxSize, "maxSize");
        TestLogger.Info("CacheConfig construction passed");
    }

    public void TestCacheConfigTtlGetterWithValue()
    {
        var config = new CacheConfig(ttl: 60.0, maxSize: 100);
        var ttl = config.Ttl;
        AssertTrue(ttl.HasValue, "Ttl has value");
        AssertApproxEqual(60.0, ttl!.Value, message: "Ttl value");
        TestLogger.Info($"CacheConfig.Ttl = {ttl}");
    }

    public void TestCacheConfigTtlGetterNil()
    {
        var config = new CacheConfig(ttl: null, maxSize: 100);
        var ttl = config.Ttl;
        AssertFalse(ttl.HasValue, "Ttl should be null");
        TestLogger.Info("CacheConfig.Ttl = null passed");
    }

    public void TestCacheConfigTtlSetter()
    {
        var config = new CacheConfig(ttl: null, maxSize: 100);
        config.Ttl = 120.0;
        var ttl = config.Ttl;
        AssertTrue(ttl.HasValue, "Ttl has value after set");
        AssertApproxEqual(120.0, ttl!.Value, message: "Ttl value after set");
        TestLogger.Info("CacheConfig.Ttl setter passed");
    }

    public void TestCacheConfigTtlSetterToNil()
    {
        var config = new CacheConfig(ttl: 60.0, maxSize: 100);
        config.Ttl = null;
        var ttl = config.Ttl;
        AssertFalse(ttl.HasValue, "Ttl should be null after clear");
        TestLogger.Info("CacheConfig.Ttl set-to-nil passed");
    }

    public void TestCacheConfigEffectiveTtl()
    {
        var config = new CacheConfig(ttl: null, maxSize: 100);
        var effective = config.GetEffectiveTtl();
        AssertApproxEqual(300.0, effective, message: "Default effective TTL");
        TestLogger.Info($"CacheConfig.GetEffectiveTtl() = {effective}");
    }

    #endregion

    #region ShapeHolder — Decomposed Optional<ComplexEnum> (IsDecomposedOptionalType path)

    public void TestShapeHolderGetterWithValue()
    {
        var circle = Shape.Circle(radius: 5.0);
        var holder = new ShapeHolder(shape: circle);
        var shape = holder.CurrentShape;
        AssertNotNull(shape, "CurrentShape not null");
        TestLogger.Info("ShapeHolder getter with value passed");
    }

    public void TestShapeHolderGetterNil()
    {
        var holder = new ShapeHolder(shape: null);
        var shape = holder.CurrentShape;
        AssertNull(shape, "CurrentShape should be null");
        TestLogger.Info("ShapeHolder getter nil passed");
    }

    public void TestShapeHolderSetter()
    {
        var holder = new ShapeHolder(shape: null);
        var rect = Shape.Rectangle(width: 3.0, height: 4.0);
        holder.CurrentShape = rect;
        var shape = holder.CurrentShape;
        AssertNotNull(shape, "CurrentShape not null after set");
        TestLogger.Info("ShapeHolder setter passed");
    }

    public void TestShapeHolderDescribeShape()
    {
        var circle = Shape.Circle(radius: 5.0);
        var holder = new ShapeHolder(shape: circle);
        var desc = holder.GetDescribeShape();
        AssertTrue(desc.Contains("Circle"), "Description mentions Circle");
        TestLogger.Info($"ShapeHolder.GetDescribeShape() = {desc}");
    }

    #endregion

    #region NodeWithParent — Optional<Class> Property

    public void TestNodeWithParentConstruction()
    {
        var node = new NodeWithParent(label: "child", parent: null);
        AssertNotNull(node, "NodeWithParent constructed");
        TestLogger.Info("NodeWithParent construction passed");
    }

    public void TestNodeWithParentGetterNil()
    {
        var node = new NodeWithParent(label: "orphan", parent: null);
        var name = node.GetParentName();
        AssertEqual("none", name, "ParentName when no parent");
        TestLogger.Info("NodeWithParent null parent passed");
    }

    public void TestNodeWithParentGetterWithValue()
    {
        var animal = new Animal(name: "Rex", sound: "Woof");
        var node = new NodeWithParent(label: "child", parent: animal);
        var name = node.GetParentName();
        AssertEqual("Rex", name, "ParentName with parent");
        TestLogger.Info("NodeWithParent with parent passed");
    }

    public void TestNodeWithParentSetThenGet()
    {
        // Roundtrip: construct with null, set parent, get parent back and verify
        // inner object properties aren't corrupted. Exercises the Optional<Class>
        // property getter return path (NewSome/NewNone vs VWT InitializeWithCopy).
        var node = new NodeWithParent(label: "child", parent: null);
        var parent = node.Parent;
        AssertTrue(parent == null, "Parent initially null");

        var animal = new Animal(name: "Rex", sound: "Woof");
        node.Parent = animal;

        var readBack = node.Parent;
        AssertNotNull(readBack, "Parent not null after set");
        AssertEqual("Rex", readBack!.Name, "Parent.Name preserved after roundtrip");
        AssertEqual("Woof", readBack!.Sound, "Parent.Sound preserved after roundtrip");
        TestLogger.Info($"NodeWithParent roundtrip: Name={readBack.Name}, Sound={readBack.Sound}");
    }

    public void TestNodeWithParentSetToNull()
    {
        // Set parent, then set back to null, verify getter returns null.
        var animal = new Animal(name: "Rex", sound: "Woof");
        var node = new NodeWithParent(label: "child", parent: animal);
        AssertNotNull(node.Parent, "Parent set via constructor");

        node.Parent = null;
        var readBack = node.Parent;
        AssertTrue(readBack == null, "Parent null after setting to null");
        TestLogger.Info("NodeWithParent set-to-null passed");
    }

    #endregion

    #region TaggedCounter — Optional<Int32> on Frozen Struct (tag-byte fixup)

    public void TestTaggedCounterGetterWithValue()
    {
        var counter = new TaggedCounter(count: 42, name: "hits");
        var count = counter.Count;
        AssertTrue(count.HasValue, "Count has value");
        AssertEqual(42, count!.Value, "Count value");
        TestLogger.Info($"TaggedCounter.Count = {count}");
    }

    public void TestTaggedCounterGetterNil()
    {
        var counter = new TaggedCounter(count: null, name: "empty");
        var count = counter.Count;
        AssertFalse(count.HasValue, "Count should be null");
        TestLogger.Info("TaggedCounter.Count = null passed");
    }

    #endregion

    #region GlobalSettings — Static Optional Property

    public void TestGlobalSettingsStaticOptionalProperty()
    {
        // Set static property
        GlobalSettings.DefaultTimeout = 60.0;
        var timeout = GlobalSettings.DefaultTimeout;
        AssertTrue(timeout.HasValue, "DefaultTimeout has value");
        AssertApproxEqual(60.0, timeout!.Value, message: "DefaultTimeout value");

        // Clear it
        GlobalSettings.DefaultTimeout = null;
        timeout = GlobalSettings.DefaultTimeout;
        AssertFalse(timeout.HasValue, "DefaultTimeout cleared");

        TestLogger.Info("GlobalSettings static Optional property passed");
    }

    public void TestGlobalSettingsEffectiveTimeout()
    {
        var effective = GlobalSettings.GetEffectiveTimeout();
        AssertApproxEqual(30.0, effective, message: "Default effective timeout");
        TestLogger.Info($"GlobalSettings.GetEffectiveTimeout() = {effective}");
    }

    #endregion
}
