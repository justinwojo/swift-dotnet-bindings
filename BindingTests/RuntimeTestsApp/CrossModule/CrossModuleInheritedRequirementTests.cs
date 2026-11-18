// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// An empty main-actor child of a dependency protocol (<c>CrossModuleInheritedRequirement.swift</c>):
/// every requirement of <c>LocalAccepting</c> is inherited from <c>DepAccepting</c>. The parent's
/// module is loaded, so the child conformance must be kept, and Swift must dispatch the inherited
/// requirement into a C# conformer. Runs on the main thread, which the main-actor entry point checks.
/// </summary>
public class CrossModuleInheritedRequirementTests : TestBase
{
    public CrossModuleInheritedRequirementTests(TestResults results) : base(results) { }

    public void TestInheritedRequirement_DispatchesIntoTheCSharpConformer()
    {
        var acceptor = new ThresholdAcceptor(10);

        AssertTrue(Functions.AcceptThroughLocal(acceptor, 12), "12 is above the threshold");
        AssertFalse(Functions.AcceptThroughLocal(acceptor, 4), "4 is below the threshold");
        AssertEqual(2, acceptor.Calls, "both calls reached the conformer");
        AssertEqual(4, acceptor.LastValue, "the conformer received the value Swift passed");
    }
}

internal sealed class ThresholdAcceptor : ILocalAccepting
{
    private readonly int _threshold;

    public ThresholdAcceptor(int threshold) => _threshold = threshold;

    public int Calls { get; private set; }
    public int LastValue { get; private set; }

    public bool Accept(int value)
    {
        Calls++;
        LastValue = value;
        return value > _threshold;
    }
}
