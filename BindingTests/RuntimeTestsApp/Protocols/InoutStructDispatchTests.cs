// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Guards the inout-non-frozen-struct protocol dispatch regression: a protocol requirement
/// taking an `inout` non-frozen struct (the GRDB row/statement-writer shape). The generated
/// C# interface must declare the parameter with `ref` so conformers can satisfy it — when the
/// interface omitted `ref`, every concrete conformer failed with CS0535.
///
/// <see cref="CSharpPointMutator"/> below is itself a consumer-side compile proof of the fix:
/// a hand-written C# class can only implement <c>IPointMutator</c> because the interface now
/// declares <c>void Mutate(ref NonFrozenPoint)</c>.
/// </summary>
public class InoutStructDispatchTests : TestBase
{
    public InoutStructDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// Forward dispatch (C# → Swift): call a Swift conformer through the generated interface.
    /// The `ref` parameter compiles (the fix), and Swift mutates the shared opaque payload in
    /// place, so the mutation is visible to C# after the call returns.
    /// </summary>
    public void TestForwardInoutNonFrozenStructDispatch()
    {
        IPointMutator mutator = new OriginShifter(dx: 10.0, dy: 20.0);
        // Not `using`: the inout parameter requires `ref point`, and a using-variable cannot be
        // passed by ref (CS1657). Dispose explicitly in finally.
        var point = new NonFrozenPoint(x: 1.0, y: 2.0);
        try
        {
            mutator.Mutate(ref point);

            AssertApproxEqual(11.0, point.X, 0.0001, "inout x mutated in place by Swift conformer");
            AssertApproxEqual(22.0, point.Y, 0.0001, "inout y mutated in place by Swift conformer");
        }
        finally
        {
            point.Dispose();
        }
    }

    /// <summary>
    /// Reverse dispatch (Swift → C#): a C# conformer is wrapped in the generated proxy and
    /// passed back into Swift, which calls <c>mutate(_:)</c> with an `inout NonFrozenPoint`.
    /// </summary>
    /// <remarks>
    /// Skipped: this exercises a PRE-EXISTING reverse-dispatch marshalling gap that is separate
    /// from the inout-`ref` interface fix. The generated proxy receiver materializes the param
    /// with <c>Unsafe.Read&lt;NonFrozenPoint&gt;(rawArg0)</c> (see the proxy-local
    /// <c>MarshalFromSwift&lt;T&gt;</c> in the generated bindings). NonFrozenPoint is an
    /// opaque-payload C# *class*, so reading it from Swift's raw value bytes reinterprets the
    /// first 8 bytes (the x double) as a managed object reference → corrupt wrapper / crash.
    /// Correctly materializing an opaque-payload param needs the NewFromPayload path, not
    /// Unsafe.Read; this affects every non-frozen-struct value param in reverse dispatch, not
    /// just inout. The interface/proxy/receiver `ref` consistency is still compile-tested by the
    /// generator's emission and by <see cref="CSharpPointMutator"/> below.
    /// </remarks>
    [Skip("Pre-existing reverse-dispatch limitation: proxy receiver materializes a non-frozen-struct (opaque-payload) param via Unsafe.Read<T>, which reads a managed reference from raw Swift value bytes instead of using NewFromPayload. Separate from the inout-ref interface fix.")]
    public void TestReverseInoutNonFrozenStructDispatch()
    {
        var impl = new CSharpPointMutator(dx: 100.0, dy: 200.0);
        var proxy = new PointMutatorProxy(impl);

        using var result = TestLibFunctions.DriveMutator(proxy, startX: 1.0, startY: 2.0);

        AssertTrue(impl.WasCalled, "reverse callback fired into the C# conformer");
        AssertApproxEqual(101.0, result.X, 0.0001, "C# conformer mutation written back through inout");
        AssertApproxEqual(202.0, result.Y, 0.0001, "C# conformer mutation written back through inout");
    }
}

/// <summary>
/// Hand-written C# conformer of the generated <c>IPointMutator</c> interface. Its mere
/// compilation proves the inout-`ref` interface fix end-to-end from the consumer side: before
/// the fix, implementing <c>Mutate</c> with a <c>ref</c> parameter did not satisfy the interface
/// (which declared the parameter without <c>ref</c>) → CS0535.
/// </summary>
internal sealed class CSharpPointMutator : IPointMutator
{
    private readonly double _dx;
    private readonly double _dy;

    public CSharpPointMutator(double dx, double dy)
    {
        _dx = dx;
        _dy = dy;
    }

    public bool WasCalled { get; private set; }

    public void Mutate(ref NonFrozenPoint point)
    {
        WasCalled = true;
        point.X += _dx;
        point.Y += _dy;
    }
}
