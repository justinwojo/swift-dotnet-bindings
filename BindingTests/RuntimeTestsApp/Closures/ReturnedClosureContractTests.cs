// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Returned closures whose ARGUMENT needs marshalling: an existential, opaque or class-bound.
/// </summary>
/// <remarks>
/// The member hands the closure back through a typed buffer, where Swift keeps it in its most
/// general form: every argument by address, every result through an indirect-result buffer.
/// Each closure captures an offset, so a call that drops the closure context computes the wrong
/// value rather than passing by luck.
/// </remarks>
public class ReturnedClosureArgumentTests : TestBase
{
    public ReturnedClosureArgumentTests(TestResults results) : base(results) { }

    public void TestOpaqueExistentialArgument()
    {
        using var vendor = new ReturnedClosureVendor(7);
        var measure = vendor.MakeShapeMeasurer();
        using var square = new ReturnedClosureSquare(3);

        AssertEqual(16, measure(square), "area 9 plus the captured offset 7");
        AssertEqual(16, measure(square), "a second call through the same delegate");
    }

    public void TestClassBoundExistentialArgument()
    {
        using var vendor = new ReturnedClosureVendor(5);
        var measure = vendor.MakeObjectShapeMeasurer();
        using var square = new ReturnedClosureObjectSquare(4);

        AssertEqual(21, measure(square), "area 16 plus the captured offset 5");
    }

    public void TestThrowingClosureWithExistentialArgument()
    {
        using var vendor = new ReturnedClosureVendor(2);
        var measure = vendor.MakeThrowingShapeMeasurer();
        using var square = new ReturnedClosureSquare(6);

        var result = measure(square);
        AssertTrue(result.IsSuccess, "a non-negative area does not throw");
        AssertEqual(38, result.Success, "area 36 plus the captured offset 2");
    }

    public void TestClassBoundExistentialMethodArgument()
    {
        // The same class-bound conformer through an ordinary method parameter: the control for
        // the returned-closure argument above.
        using var square = new ReturnedClosureObjectSquare(4);

        AssertEqual(16, ReturnedClosureVendor.ObjectAreaOf(square), "area of a 4-sided square");
    }

    public void TestCSharpConformerAsExistentialArgument()
    {
        // A C# object has no Swift existential of its own; the invoker has to box it through the
        // protocol's proxy before Swift can call the requirement back into C#.
        using var vendor = new ReturnedClosureVendor(7);
        var measure = vendor.MakeShapeMeasurer();

        AssertEqual(18, measure(new ManagedShape(11)), "C# area 11 plus the captured offset 7");
    }

    public void TestOptionalClosurePropertyWithExistentialArgument()
    {
        using var vendor = new ReturnedClosureVendor(11);
        var measure = vendor.OptionalShapeMeasurer;
        AssertNotNull(measure, "the property holds a closure");
        using var square = new ReturnedClosureSquare(2);

        AssertEqual(15, measure!(square), "area 4 plus the captured offset 11");
    }

    private sealed class ManagedShape : IReturnedClosureShape
    {
        private readonly int _area;
        public ManagedShape(int area) => _area = area;
        public int GetArea() => _area;
    }
}

/// <summary>
/// Returned closures whose RESULT needs marshalling: an existential, an error or a tuple.
/// </summary>
public class ReturnedClosureResultTests : TestBase
{
    public ReturnedClosureResultTests(TestResults results) : base(results) { }

    public void TestOpaqueExistentialResult()
    {
        using var vendor = new ReturnedClosureVendor(1);
        var build = vendor.MakeShapeBuilder();

        var shape = build(4);
        AssertEqual(25, ReturnedClosureVendor.AreaOf(shape), "side 4 plus the captured offset 1, squared");
        (shape as global::System.IDisposable)?.Dispose();
    }

    public void TestClassBoundExistentialResult()
    {
        using var vendor = new ReturnedClosureVendor(2);
        var build = vendor.MakeObjectShapeBuilder();

        var shape = build(3);
        AssertEqual(25, shape.GetArea(), "side 3 plus the captured offset 2, squared");
        (shape as global::System.IDisposable)?.Dispose();
    }

    public void TestNonFrozenStructArgumentWithExistentialResult()
    {
        using var vendor = new ReturnedClosureVendor(3);
        var build = vendor.MakeConfiguredShapeBuilder();
        using var config = new ReturnedClosureConfig(2, "two");

        var shape = build(config);
        AssertEqual(25, ReturnedClosureVendor.AreaOf(shape), "scale 2 plus the captured offset 3, squared");
        (shape as global::System.IDisposable)?.Dispose();
    }

    public void TestErrorResult()
    {
        using var vendor = new ReturnedClosureVendor(40);
        var build = vendor.MakeErrorBuilder();

        using var error = build(2);
        AssertEqual(42, ReturnedClosureVendor.CodeOf(error), "code 2 plus the captured offset 40");
    }

    public void TestTupleResult()
    {
        using var vendor = new ReturnedClosureVendor(3);
        var make = vendor.MakePairMaker();

        var (sum, product) = make(5);
        AssertEqual(8, sum, "value plus the captured offset");
        AssertEqual(15, product, "value times the captured offset");
    }

    public void TestOptionalClosurePropertyWithTupleResult()
    {
        using var vendor = new ReturnedClosureVendor(4);
        var make = vendor.OptionalPairMaker;
        AssertNotNull(make, "the property holds a closure");

        var (low, high) = make!(10);
        AssertEqual(6, low, "value minus the captured offset");
        AssertEqual(14, high, "value plus the captured offset");
    }

    public void TestReturnedCFunctionPointer()
    {
        using var vendor = new ReturnedClosureVendor(1);
        var triple = vendor.MakeCTripler();

        AssertEqual(21, triple(7), "7 tripled");
        AssertEqual(-9, triple(-3), "-3 tripled");
    }
}
