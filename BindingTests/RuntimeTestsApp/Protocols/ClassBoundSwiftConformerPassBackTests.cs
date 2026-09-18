// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A Swift class conformer of a class-bound (<c>AnyObject</c>) protocol, held in C# and passed
/// back to Swift where the protocol existential is expected. Swift reads a class-bound existential
/// as <c>[reference][witness table]</c>, so every requirement call below goes through the witness
/// table the binding placed next to the reference.
/// </summary>
public class ClassBoundSwiftConformerPassBackTests : TestBase
{
    public ClassBoundSwiftConformerPassBackTests(TestResults results) : base(results) { }

    public void TestSwiftConformerPassedAsExistential()
    {
        using var counter = new SwiftPassBackCounter(5);
        AssertEqual(5, Functions.PassBackRead(counter), "Swift reads the requirement through the existential");
        AssertTrue(Functions.PassBackIsSame(counter, counter), "the existential refers to the same Swift object");
    }

    public void TestSwiftConformerPassedAsOptionalExistential()
    {
        using var counter = new SwiftPassBackCounter(8);
        AssertEqual(8, Functions.PassBackReadOptional(counter), "optional existential carrying a Swift conformer");
        AssertEqual(-1, Functions.PassBackReadOptional(null), "optional existential without a value");
    }

    public void TestCSharpConformerPassedAsOptionalExistential()
    {
        var counter = new CSharpPassBackCounter(9);
        AssertEqual(9, Functions.PassBackReadOptional(counter), "optional existential carrying a C# conformer");
        AssertEqual(9, Functions.PassBackRead(counter), "the same C# conformer as a plain existential");
    }

    public void TestSwiftConformerMutatedThroughMethodParameter()
    {
        using var counter = new SwiftPassBackCounter(1);
        using var holder = new PassBackHolder();
        AssertEqual(4, holder.Bump(counter, 3), "the requirement mutates the Swift object");
        AssertEqual(4, counter.Total, "the mutation is visible on the C# reference");
    }

    public void TestSwiftConformerStoredInExistentialProperty()
    {
        using var counter = new SwiftPassBackCounter(6);
        using var holder = new PassBackHolder();
        holder.Held = counter;
        AssertEqual(6, holder.ReadHeld(), "Swift reads the stored existential");
        counter.PassBackBump(1);
        AssertEqual(7, holder.ReadHeld(), "the stored existential refers to the same object");
        holder.Held = null;
        AssertEqual(-1, holder.ReadHeld(), "clearing the stored existential");
    }

    public void TestSwiftReturnedConformerPassedBack()
    {
        var returned = Functions.MakePassBackCounter(11);
        AssertEqual(11, returned.PassBackValue(), "C# calls the requirement on the returned existential");
        AssertEqual(11, Functions.PassBackRead(returned), "the returned existential passed back to Swift");
        using var holder = new PassBackHolder();
        AssertEqual(13, holder.Bump(returned, 2), "the returned existential mutated through a method parameter");
        (returned as IDisposable)?.Dispose();
    }
}

/// <summary>A C# conformer of the class-bound protocol, reached from Swift through its proxy.</summary>
sealed class CSharpPassBackCounter : IPassBackCounter
{
    private int _total;
    public CSharpPassBackCounter(int start) => _total = start;
    public int PassBackValue() => _total;
    public void PassBackBump(int amount) => _total += amount;
}
