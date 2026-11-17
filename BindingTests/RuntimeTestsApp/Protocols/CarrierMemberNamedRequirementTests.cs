// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Protocol requirements named like the generated carrier class's own members
/// (<c>handle</c>, <c>onDeinit</c>, <c>onDeinitCtx</c>, the vtable global, and the
/// carrier's reserved deinit storage names). Before the fix each name was a
/// redeclaration or shadowed a generated global, and the wrapper module did not compile.
/// A C# class implements each protocol and Swift reads every requirement back through
/// the existential, so a wrong dispatch changes the folded result.
/// </summary>
public class CarrierMemberNamedRequirementTests : TestBase
{
    public CarrierMemberNamedRequirementTests(TestResults results) : base(results) { }

    public void TestPlainCarrierDispatchesEveryCarrierNamedRequirement()
    {
        var impl = new CarrierMemberNamedImpl();
        int result = Functions.ReadCarrierMemberNamed(impl);
        // handle 1, onDeinit 1 → 2, onDeinitCtx(3) = 6, vtable 7, callback 8, context 9.
        AssertEqual(126789, result, "Swift reads every carrier-named requirement from the C# conformer");
        AssertEqual(2, impl.OnDeinit, "Swift's write to the onDeinit requirement reaches the C# conformer");
        AssertEqual(3, impl.LastContextArgument, "onDeinitCtx(_:) receives Swift's argument");
    }

    public void TestObjCCarrierDispatchesCarrierNamedRequirements()
    {
        var impl = new ObjCCarrierMemberNamedImpl();
        int result = Functions.ReadObjCCarrierMemberNamed(impl);
        // handle 5, onDeinitCtx(4) = 7.
        AssertEqual(507, result, "Swift reads handle and onDeinitCtx(_:) from the C# conformer through EveryObjCProtocol");
    }
}

internal sealed class CarrierMemberNamedImpl : ICarrierMemberNamed
{
    public int LastContextArgument { get; private set; }
    public int Handle => 1;
    public int OnDeinit { get; set; } = 1;
    public int OnDeinitCtx(int value)
    {
        LastContextArgument = value;
        return value * 2;
    }
    public int _carrierMemberNamed_vtable => 7;
    public int _sbwDeinitCallback => 8;
    public int _sbwDeinitContext => 9;
}

internal sealed class ObjCCarrierMemberNamedImpl : IObjCCarrierMemberNamed
{
    public int Handle => 5;
    public int OnDeinitCtx(int value) => value + 3;
}
