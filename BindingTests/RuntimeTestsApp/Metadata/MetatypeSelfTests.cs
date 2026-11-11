// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Foundation;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Metadata;

/// <summary>
/// Static and class members of class-shaped parents called directly through the Swift calling
/// convention. Each receives its <c>@thick Self.Type</c> in the Swift self register, and each
/// Swift probe reports the metatype it saw, so these assertions fail when the binding leaves the
/// register holding anything other than the class metadata.
/// </summary>
public class MetatypeSelfStaticTests : TestBase
{
    public MetatypeSelfStaticTests(TestResults results) : base(results) { }

    public void TestClassStaticReceivesItsOwnMetatype()
    {
        using var mismatch = StaticSelfErrorProbe.Check();
        AssertTrue(mismatch is null,
            $"check() saw its own metatype in the self register (got: {mismatch?.LocalizedDescription ?? "nil"})");
    }

    public void TestClassFuncReceivesItsOwnMetatype()
    {
        AssertReport("base:SwiftBindingsTestLib.StaticSelfErrorProbe", StaticSelfErrorProbe.GetReport(),
            "class func on the base class");
    }

    public void TestOverriddenClassFuncCalledThroughSubclass()
    {
        AssertReport("derived:SwiftBindingsTestLib.StaticSelfErrorProbeDerived", StaticSelfErrorProbeDerived.GetReport(),
            "class func called through the subclass runs the override with the subclass metatype");
    }

    public void TestOverriddenClassPropertyReadThroughSubclass()
    {
        AssertEqual("base:SwiftBindingsTestLib.StaticSelfErrorProbe", StaticSelfErrorProbe.KindName,
            "class property read on the base class");
        AssertEqual("derived:SwiftBindingsTestLib.StaticSelfErrorProbeDerived", StaticSelfErrorProbeDerived.KindName,
            "class property read on the subclass runs the override");
    }

    public void TestOverriddenInstanceMethodDispatchesOnTheInstance()
    {
        using var plain = new StaticSelfErrorProbe();
        using var derived = new StaticSelfErrorProbeDerived();
        StaticSelfErrorProbe derivedAsBase = derived;

        AssertReport("base-instance:SwiftBindingsTestLib.StaticSelfErrorProbe", plain.GetInstanceReport(),
            "overridable method on a base instance");
        AssertReport("derived-instance:SwiftBindingsTestLib.StaticSelfErrorProbeDerived", derived.GetInstanceReport(),
            "override called on the subclass");
        AssertReport("derived-instance:SwiftBindingsTestLib.StaticSelfErrorProbeDerived", derivedAsBase.GetInstanceReport(),
            "override called through a base-typed reference");
    }

    public void TestActorStaticReceivesItsOwnMetatype()
    {
        AssertReport("SwiftBindingsTestLib.MetatypeSelfActor", MetatypeSelfActor.GetReport(), "actor static");
    }

    public void TestGlobalActorBoundStaticReceivesItsOwnMetatype()
    {
        AssertReport("SwiftBindingsTestLib.MetatypeSelfGlobalActorBound", MetatypeSelfGlobalActorBound.GetReport(),
            "nonisolated static on a global-actor-isolated class");
    }

    public void TestInternalParentStaticMethodReceivesItsOwnMetatype()
    {
        AssertEqual("SwiftBindingsTestLib.MetatypeSelfInternalHost", MetatypeSelfInternalHost.GetDescribeSelf().ToString(),
            "static method on a wrapper-ineligible class");
    }

    public void TestInternalParentStaticPropertyReceivesItsOwnMetatype()
    {
        AssertEqual("SwiftBindingsTestLib.MetatypeSelfInternalHost", MetatypeSelfInternalHost.SelfName.ToString(),
            "static property getter on a wrapper-ineligible class");
    }

    private void AssertReport(string expected, AnyError? report, string what)
    {
        using (report)
        {
            AssertNotNull(report, $"{what}: the probe returns a report");
            var description = report!.LocalizedDescription;
            TestLogger.Info($"{what}: {description}");
            AssertTrue(description.Contains($"metatype=<{expected}>", StringComparison.Ordinal),
                $"{what}: expected metatype=<{expected}>, got \"{description}\"");
        }
    }
}

/// <summary>
/// Class allocating initializers called directly through the Swift calling convention. The
/// initializer allocates from the metatype in the self register, so the instance's dynamic type —
/// read back through Swift — is wrong, or the object faults on release, when that register is
/// not the specialized class metadata.
/// </summary>
public class MetatypeSelfInitTests : TestBase
{
    public MetatypeSelfInitTests(TestResults results) : base(results) { }

    public void TestGenericNestedClassInitAllocatesEachSpecialization()
    {
        // Back to back, so a register still holding the first specialization's metadata would
        // build the second object as the first type.
        var wide = new StaticNestOuter<long>.Node(41);
        var narrow = new StaticNestOuter<int>.Node(42);

        AssertEqual("SwiftBindingsTestLib.StaticNestOuter<Swift.Int64>.Node", wide.DynamicTypeName.ToString(),
            "Node built for Int64 has the Int64 specialization");
        AssertEqual("SwiftBindingsTestLib.StaticNestOuter<Swift.Int32>.Node", narrow.DynamicTypeName.ToString(),
            "Node built for Int32 has the Int32 specialization");
        AssertEqual(41, wide.Value, "Int64 Node keeps its value");
        AssertEqual(42, narrow.Value, "Int32 Node keeps its value");

        wide.Dispose();
        narrow.Dispose();
    }
}
