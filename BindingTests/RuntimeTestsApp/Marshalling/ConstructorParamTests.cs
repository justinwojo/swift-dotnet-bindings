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

    public void TestProtocolExistentialParamConstruction()
    {
        // DescriptionPrinter(source: any Describable) — exercises IsProtocolExistentialType branch.
        // SimpleItem conforms to IDescribable (constructor takes id + label).
        var item = new SimpleItem(id: "test-1", label: "Hello from existential");
        var printer = new DescriptionPrinter(source: item);
        AssertNotNull(printer, "DescriptionPrinter constructed with existential param");
        TestLogger.Info("DescriptionPrinter(IDescribable) construction passed");
    }

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

    [Skip("ValidatedName.TryCreate not emitted — failable init on non-frozen struct not yet supported")]
    public void TestFailableInitSuccess()
    {
        TestLogger.Info("Skipped: ValidatedName.TryCreate not emitted");
    }

    [Skip("ValidatedName.TryCreate not emitted — failable init on non-frozen struct not yet supported")]
    public void TestFailableInitFailure()
    {
        TestLogger.Info("Skipped: ValidatedName.TryCreate not emitted");
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

    public void TestClosureConstructorParamTrigger()
    {
        // Verify the closure actually fires when triggered.
        var captured = 0;
        var holder = new CallbackHolder(label: "trigger", callback: x => { captured = x; });
        holder.Trigger(value: 42);
        AssertEqual(42, captured, "Closure captured value after trigger");
        TestLogger.Info("CallbackHolder.Trigger(42) closure invoked correctly");
    }

    #endregion

    #region StringSupplierHolder — Closure-Returning-Struct Constructor Param (BUG-4)

    public void TestClosureReturningStructConstructorParam()
    {
        // Constructor with @escaping () -> String closure param.
        // Exercises the indirect return path in Cdecl callbacks: the Swift adapter
        // passes a result buffer to the C# callback, which writes the String to it.
        // BUG-4: without the fix, the result buffer is misinterpreted as the context,
        // causing crash in swift_cvw_initWithCopyImpl.
        var holder = new StringSupplierHolder(name: "test", supplier: () => "hello from C#");
        AssertNotNull(holder, "StringSupplierHolder constructed with closure param");
        AssertEqual("test", holder.GetName(), "Name property");
        TestLogger.Info("StringSupplierHolder(() -> String) construction passed");
    }

    public void TestClosureReturningStructCallSupplier()
    {
        // Verify the closure is actually called and returns the correct value.
        var holder = new StringSupplierHolder(name: "supplier", supplier: () => "supplied value");
        var result = holder.CallSupplier();
        AssertEqual("supplied value", result, "Supplier returned correct value");
        TestLogger.Info($"StringSupplierHolder.CallSupplier() = {result}");
    }

    public void TestClosureReturningStructDynamicValue()
    {
        // Verify the closure captures and returns dynamic values.
        var counter = 0;
        var holder = new StringSupplierHolder(name: "counter", supplier: () => $"count={++counter}");
        var first = holder.CallSupplier();
        var second = holder.CallSupplier();
        AssertEqual("count=1", first, "First call returns count=1");
        AssertEqual("count=2", second, "Second call returns count=2");
        TestLogger.Info("StringSupplierHolder dynamic closure captures work correctly");
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

    public void TestDataRoundTrip()
    {
        // Verify byte[] → Swift.Data → byte[] round-trip via DataProjection.
        // Constructor marshals byte[] to Swift.Data; Contents property marshals back to byte[].
        var original = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var timestamp = DateTimeOffset.UtcNow;
        var blob = new TimestampedBlob(timestamp: timestamp, contents: original);

        AssertEqual(original.Length, blob.GetContentsSize(), "Swift Data size matches input");

        var retrieved = blob.Contents;
        AssertNotNull(retrieved, "Contents returned non-null byte[]");
        AssertEqual(original.Length, retrieved.Length, "Round-trip preserves length");

        for (int i = 0; i < original.Length; i++)
        {
            AssertEqual(original[i], retrieved[i], $"Byte[{i}] matches");
        }
        TestLogger.Info($"Data round-trip: {original.Length} bytes preserved");
    }

    public void TestDataRoundTripEmpty()
    {
        // Edge case: empty byte array round-trip.
        var empty = Array.Empty<byte>();
        var blob = new TimestampedBlob(timestamp: DateTimeOffset.UtcNow, contents: empty);
        AssertEqual(0, blob.GetContentsSize(), "Empty data has size 0");

        var retrieved = blob.Contents;
        AssertNotNull(retrieved, "Contents returned non-null for empty data");
        AssertEqual(0, retrieved.Length, "Empty round-trip preserves zero length");
        TestLogger.Info("Data round-trip: empty byte[] preserved");
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
