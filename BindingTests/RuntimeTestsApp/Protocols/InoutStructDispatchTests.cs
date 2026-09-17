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
    /// passed back into Swift, which calls <c>mutate(_:)</c> with an `inout NonFrozenPoint`. The
    /// receiver hands the conformer a copy of Swift's value and must write the mutated copy back
    /// into the slot Swift assigns from once the call returns.
    /// </summary>
    public void TestReverseInoutNonFrozenStructDispatch()
    {
        var impl = new CSharpPointMutator(dx: 100.0, dy: 200.0);
        var proxy = new PointMutatorProxy(impl);

        using var result = TestLibFunctions.DriveMutator(proxy, startX: 1.0, startY: 2.0);

        AssertTrue(impl.WasCalled, "reverse callback fired into the C# conformer");
        AssertApproxEqual(101.0, result.X, 0.0001, "C# conformer mutation written back through inout");
        AssertApproxEqual(202.0, result.Y, 0.0001, "C# conformer mutation written back through inout");
    }

    /// <summary>
    /// Reverse dispatch of an <c>inout Int</c> requirement: the C# conformer's increment must be
    /// visible to the Swift caller.
    /// </summary>
    public void TestReverseInoutNativeIntRequirement_WritesBack()
    {
        AssertEqual((nint)19, TestLibFunctions.DriveCursorAdvancer(new CSharpCursorAdvancer(), 12, 7),
            "C# conformer's inout Int mutation reached the Swift caller");
    }

    /// <summary>
    /// One reverse-dispatched <c>inout</c> requirement per lowering — scalar, narrow enum, string,
    /// optional string, array, dictionary, class reference and optional class reference. Every
    /// mutation the C# conformer stores into its <c>ref</c> parameter must be what the Swift caller
    /// observes afterwards.
    /// </summary>
    public void TestReverseInoutWriteBackMatrix()
    {
        var observed = TestLibFunctions.DriveInOutWriteBackMatrix(new CSharpInOutWriteBackMatrix());

        AssertEqual("42|swift!|named|1,2,3|armed|7|nil|a=1,b=2", observed,
            "every inout lowering wrote the C# conformer's value back to Swift");
    }

    /// <summary>
    /// The class instance a C# conformer stores into an <c>inout</c> reference is retained by the
    /// Swift slot, so it stays valid after the managed wrapper is collected.
    /// </summary>
    public void TestReverseInoutClassReference_SurvivesManagedCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            var observed = TestLibFunctions.DriveInOutWriteBackMatrix(new CSharpInOutWriteBackMatrix());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            AssertEqual("42|swift!|named|1,2,3|armed|7|nil|a=1,b=2", observed,
                $"write-back stable across collections (iteration {i})");
        }
    }

    /// <summary>
    /// Forward dispatch through the proxy (C# holds the existential over a Swift conformer): Swift
    /// mutates the storage C# lends it, and each <c>ref</c> argument must hold Swift's value after
    /// the call — a scalar, a string, a class reference and a non-frozen struct.
    /// </summary>
    public void TestForwardInoutThroughProxy_ReadsSwiftMutationBack()
    {
        var matrix = TestLibFunctions.MakeSwiftInOutWriteBackMatrix();

        nint count = 41;
        matrix.BumpCount(ref count);
        AssertEqual((nint)42, count, "inout Int mutated by the Swift conformer");

        var text = "csharp";
        matrix.AppendSuffix(ref text);
        AssertEqual("csharp?", text, "inout String mutated by the Swift conformer");

        var original = new InOutToken(1);
        var token = original;
        matrix.SwapToken(ref token);
        AssertEqual((nint)101, (nint)token.Id, "inout class reference replaced by the Swift conformer");
        AssertEqual((nint)1, (nint)original.Id, "the replaced instance stays valid for its other owner");
        GC.KeepAlive(original);

        var advancer = TestLibFunctions.MakeSwiftCursorAdvancer();
        nint cursor = 12;
        advancer.Advance(ref cursor, (nint)7);
        AssertEqual((nint)19, cursor, "inout Int beside a by-value sibling");

        var mutator = TestLibFunctions.MakeSwiftPointMutator(dx: 10.0, dy: 20.0);
        var point = new NonFrozenPoint(x: 1.0, y: 2.0);
        try
        {
            mutator.Mutate(ref point);
            AssertApproxEqual(11.0, point.X, 0.0001, "inout non-frozen struct mutated in place");
            AssertApproxEqual(22.0, point.Y, 0.0001, "inout non-frozen struct mutated in place");
        }
        finally
        {
            point.Dispose();
        }
    }

    /// <summary>
    /// The class reference Swift stores into an <c>inout</c> slot is owned by the C# wrapper it
    /// comes back as, so it stays valid across collections of every other wrapper involved.
    /// </summary>
    public void TestForwardInoutClassReference_SurvivesManagedCollection()
    {
        var matrix = TestLibFunctions.MakeSwiftInOutWriteBackMatrix();
        var token = new InOutToken(5);
        for (var i = 0; i < 3; i++)
        {
            matrix.SwapToken(ref token);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        AssertEqual((nint)305, (nint)token.Id, "swapped reference valid after three collections");
    }

    /// <summary>
    /// An <c>inout</c> requirement that also returns a value or throws: Swift mutates before either
    /// exit, so the <c>ref</c> argument carries the mutation on the return path and on the throw path.
    /// </summary>
    public void TestForwardInoutThroughProxy_MutationSurvivesReturnAndThrow()
    {
        var counter = TestLibFunctions.MakeSwiftInOutFallibleCounter();

        nint value = 10;
        var doubled = counter.Bump(ref value, (nint)5, false);
        AssertEqual((nint)15, value, "inout mutated on the returning path");
        AssertEqual((nint)30, doubled, "return value alongside the inout mutation");

        nint failing = 10;
        AssertThrows<Exception>(() => counter.Bump(ref failing, (nint)5, true), "Swift error surfaced");
        AssertEqual((nint)15, failing, "inout mutated before the throw");

        var name = "swift";
        AssertTrue(counter.Rename(ref name, false), "string requirement returned");
        AssertEqual("SWIFT", name, "inout String mutated on the returning path");

        var failingName = "dotnet";
        AssertThrows<Exception>(() => counter.Rename(ref failingName, true), "Swift error surfaced");
        AssertEqual("DOTNET", failingName, "inout String mutated before the throw");
    }

    /// <summary>
    /// A requirement pairing an <c>inout Int</c> with a plain <c>Int</c>. The plain parameter gets
    /// the 32-bit convenience overload; the <c>inout</c> one keeps its pointer width and its
    /// <c>ref</c>, because it carries a value back out and because a cast rvalue cannot be passed
    /// by reference at all. Calling with a bare <c>3</c> is what picks the convenience overload.
    /// </summary>
    public void TestInoutNativeIntRequirement_NarrowsOnlyTheByValueParameter()
    {
        using var advancer = new SteppingAdvancer();
        ICursorAdvancer contract = advancer;

        nint cursor = 5;
        contract.Advance(ref cursor, 3);
        AssertEqual((nint)8, cursor, "cursor advanced through the 32-bit convenience overload");

        // Past the 32-bit window, through the pointer-width member the convenience overload
        // forwards to — the value the narrowed view could not have carried.
        long twoToThe32 = 4294967296L;
        nint wideStep = (nint)twoToThe32;
        contract.Advance(ref cursor, wideStep);
        AssertEqual((nint)(8 + twoToThe32), cursor, "cursor advanced at native width");
    }

    /// <summary>
    /// Reverse dispatch (Swift → C#) is not what this checks; the point is that a hand-written C#
    /// conformer satisfies the requirement by declaring only the pointer-width member, and still
    /// answers a narrowed call through the interface's default implementation.
    /// </summary>
    public void TestInoutNativeIntRequirement_CSharpConformerInheritsTheNarrowedOverload()
    {
        ICursorAdvancer contract = new CSharpCursorAdvancer();

        nint cursor = 100;
        contract.Advance(ref cursor, 7);

        AssertEqual((nint)107, cursor, "C# conformer reached through the narrowed default implementation");
    }
}

/// <summary>
/// Hand-written conformer of the generated <c>ICursorAdvancer</c>. It implements only the
/// pointer-width member: the narrowed sibling is a default implementation on the interface, so
/// declaring it here would be redundant, and a version of it that dropped <c>ref</c> would not
/// compile against this signature.
/// </summary>
internal sealed class CSharpCursorAdvancer : ICursorAdvancer
{
    public void Advance(ref nint cursor, nint step)
    {
        cursor += step;
    }
}

/// <summary>
/// C# conformer that replaces or mutates every <c>inout</c> value it is handed.
/// </summary>
internal sealed class CSharpInOutWriteBackMatrix : IInOutWriteBackMatrix
{
    public void BumpCount(ref nint value) => value += 1;

    public void AppendSuffix(ref string value) => value += "!";

    public void FillOptionalName(ref string? value) => value ??= "named";

    public void ExtendList(ref IEnumerable<nint> values) => values = values.Append(3).ToArray();

    public void AdvanceSignal(ref InOutSignal signal) => signal = InOutSignal.Armed;

    public void SwapToken(ref InOutToken token) => token = new InOutToken(7);

    public void ClearToken(ref InOutToken? token) => token = null;

    public void TagScores(ref IDictionary<string, nint> scores)
    {
        scores = new Dictionary<string, nint>(scores) { ["b"] = 2 };
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
