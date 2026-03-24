// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests constructor parameter marshalling through different generator code paths.
/// Exercises the GetCdeclParamMapping branches in ConstructorWrapperEmitter
/// and the ShouldEmitWrapper guards.
///
/// Coverage gaps addressed:
/// - Protocol existential param (GetCdeclParamMapping:727-732)
/// - Optional&lt;Class&gt; param (GetCdeclParamMapping:738-769)
/// - Complex enum param (GetCdeclParamMapping:951-956)
/// - Non-frozen failable init (ShouldEmitWrapper:43-45)
/// - Closure constructor param (ShouldEmitWrapper:59-65)
/// - Foundation.Date/Data params (GetCdeclParamMapping:824-841)
/// - Tag-only enum param (GetCdeclParamMapping:935-940)
/// </summary>
public class ConstructorParamTests : TestBase
{
    public ConstructorParamTests(TestResults results) : base(results) { }

    #region DescriptionPrinter — Protocol Existential Constructor Param

    [Skip("ExistentialContainer1 constructor param crashes Mono JIT — !ji->async assertion in existential boxing path")]
    public void TestProtocolExistentialParamConstruction()
    {
        // DescriptionPrinter(source: any Describable) — exercises IsProtocolExistentialType branch.
        // SimpleItem conforms to IDescribable (constructor takes id + label).
        var item = new SimpleItem(id: "test-1", label: "Hello from existential");
        var printer = new DescriptionPrinter(source: item);
        AssertNotNull(printer, "DescriptionPrinter constructed with existential param");
        TestLogger.Info("DescriptionPrinter(IDescribable) construction passed");
    }

    [Skip("ExistentialContainer1 constructor param crashes Mono JIT — !ji->async assertion in existential boxing path")]
    public void TestProtocolExistentialParamGetText()
    {
        var item = new SimpleItem(id: "ex-1", label: "Existential test");
        var printer = new DescriptionPrinter(source: item);
        var text = printer.GetText();
        AssertNotNull(text, "GetText returns non-null");
        TestLogger.Info($"DescriptionPrinter.GetText() = {text}");
    }

    #endregion

    #region LinkedNode — Optional<Class> Constructor Param

    public void TestOptionalClassParamNil()
    {
        // LinkedNode constructor with Optional<Animal> param — nullable pointer ABI.
        // @_cdecl wrapper accepts UnsafeMutableRawPointer? (nil for .none, object pointer for .some).
        var node = new LinkedNode(value: 1, previous: null);
        AssertNotNull(node, "LinkedNode constructed with nil previous");
        var desc = node.GetDescribe();
        AssertEqual("1 -> nil", desc, "Description with nil previous");
        TestLogger.Info($"LinkedNode(1, nil) = {desc}");
    }

    public void TestOptionalClassParamWithValue()
    {
        var animal = new Animal(name: "Rex", sound: "Woof");
        var node = new LinkedNode(value: 2, previous: animal);
        AssertNotNull(node, "LinkedNode constructed with previous");
        var desc = node.GetDescribe();
        AssertEqual("2 -> Rex", desc, "Description with previous");
        TestLogger.Info($"LinkedNode(2, Rex) = {desc}");
    }

    #endregion

    #region ShapeMetrics — Complex Enum Constructor Param

    public void TestComplexEnumParamConstruction()
    {
        var circle = Shape.Circle(radius: 5.0);
        var metrics = new ShapeMetrics(shape: circle);
        AssertNotNull(metrics, "ShapeMetrics constructed with complex enum");
        TestLogger.Info("ShapeMetrics(Shape.Circle) construction passed");
    }

    public void TestComplexEnumParamSummary()
    {
        var circle = Shape.Circle(radius: 5.0);
        var metrics = new ShapeMetrics(shape: circle);
        var summary = metrics.GetSummary();
        AssertTrue(summary.Contains("Circle"), "Summary mentions Circle");
        TestLogger.Info($"ShapeMetrics.GetSummary() = {summary}");
    }

    #endregion

    #region ValidatedName — Non-Frozen Failable Init

    public void TestFailableInitSuccess()
    {
        // Non-frozen struct failable init is emitted as TryCreate (CallConvSwift path).
        // The @_cdecl guard in ConstructorWrapperEmitter prevents wrapper emission to avoid
        // memory corruption, but CallConvSwift works correctly for failable inits.
        var success = ValidatedName.TryCreate(name: "Alice", out var validated);
        AssertTrue(success, "TryCreate should succeed for non-empty name");
        AssertNotNull(validated, "ValidatedName should be non-null");
        var desc = validated!.GetDescribe();
        AssertTrue(desc.Contains("Alice"), "Description contains name");
        TestLogger.Info($"ValidatedName.TryCreate succeeded: {desc}");
    }

    public void TestFailableInitFailure()
    {
        // Failable init with empty string should return nil (TryCreate returns false).
        var success = ValidatedName.TryCreate(name: "", out var validated);
        AssertFalse(success, "TryCreate should fail for empty name");
        TestLogger.Info("ValidatedName.TryCreate correctly failed for empty name");
    }

    #endregion

    #region CallbackHolder — Closure Constructor Param

    public void TestClosureConstructorParam()
    {
        // Constructor with @escaping closure param — generated as Action<int>.
        var captured = 0;
        var holder = new CallbackHolder(label: "test", callback: x => { captured = x; });
        AssertNotNull(holder, "CallbackHolder constructed with closure param");
        AssertEqual("test", holder.GetLabel(), "Label property");
        TestLogger.Info("CallbackHolder(Action<int>) construction passed");
    }

    #endregion

    #region TimestampedBlob — Foundation.Date and Foundation.Data

    public void TestFoundationDateDataParams()
    {
        // Constructor taking DateTimeOffset and byte[] — exercises Foundation type marshalling.
        var timestamp = DateTimeOffset.UtcNow;
        var contents = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var blob = new TimestampedBlob(timestamp: timestamp, contents: contents);
        AssertNotNull(blob, "TimestampedBlob constructed with Date + Data");
        AssertEqual(4, blob.GetContentsSize(), "Contents size = 4 bytes");
        TestLogger.Info($"TimestampedBlob.GetContentsSize() = {blob.GetContentsSize()}");
    }

    #endregion

    #region DirectionHolder — Tag-Only Enum Constructor Param

    public void TestTagOnlyEnumParamConstruction()
    {
        var holder = new DirectionHolder(direction: Direction.North, label: "up");
        AssertNotNull(holder, "DirectionHolder constructed");
        TestLogger.Info("DirectionHolder construction passed");
    }

    public void TestTagOnlyEnumParamDescribe()
    {
        var holder = new DirectionHolder(direction: Direction.East, label: "right");
        var desc = holder.GetDescribe();
        AssertTrue(desc.Contains("right"), "Description contains label");
        TestLogger.Info($"DirectionHolder.GetDescribe() = {desc}");
    }

    public void TestTagOnlyEnumParamProperties()
    {
        var holder = new DirectionHolder(direction: Direction.West, label: "left");
        AssertEqual(Direction.West, holder.Direction, "Direction property");
        AssertEqual("left", holder.Label, "Label property");
        TestLogger.Info("DirectionHolder property access passed");
    }

    #endregion
}
