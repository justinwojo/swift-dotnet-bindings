// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for parent-generic constructors whose generic param is
/// constrained on a PAT / self-requirement protocol — the AppIntents 0.12.0
/// <c>IntentParameterSummary&lt;Intent: AppIntent&gt;.init(_: ParameterSummaryString&lt;Intent&gt;, …)</c>
/// shape. Before the dynamic-PWT extension to <c>MetatypeHelperEmitter</c>,
/// these constructors fell through to direct CallConvSwift (SB0001) because
/// the wrapper-helper path only threaded resolvable (no-associated-type,
/// no-self-requirement) PWTs.
///
/// With the extension landed, the generated <c>SelfReqHost_PInvoke</c> helper
/// class supplies the PAT witness table via
/// <c>SwiftConformance.GetWitnessTableOrThrow</c>, the @_cdecl wrapper signature
/// declares matching <c>_pwt0</c> param, and the <c>_sbw_meta_*</c> helper
/// forwards it into the dlsym'd <c>Ma</c> accessor.
/// </summary>
public class SelfReqProtocolCtorTests : TestBase
{
    public SelfReqProtocolCtorTests(TestResults results) : base(results) { }

    public void TestSelfReqProtocolCtor_AcceptsBoxedPayload_A()
    {
        var inner = new ConcreteSelfReqA();
        var box = new SelfReqBox<ConcreteSelfReqA>(inner);
        using var host = new SelfReqHost<ConcreteSelfReqA>(box);
        var description = host.LabelDescription.ToString();
        AssertEqual("host:A", description,
            "SelfReqHost<ConcreteSelfReqA>.init round-trips PAT-constrained parent generic");
    }

    public void TestSelfReqProtocolCtor_AcceptsBoxedPayload_B()
    {
        var inner = new ConcreteSelfReqB();
        var box = new SelfReqBox<ConcreteSelfReqB>(inner);
        using var host = new SelfReqHost<ConcreteSelfReqB>(box);
        var description = host.LabelDescription.ToString();
        AssertEqual("host:B", description,
            "SelfReqHost<ConcreteSelfReqB>.init round-trips PAT-constrained parent generic with different conformer");
    }
}
