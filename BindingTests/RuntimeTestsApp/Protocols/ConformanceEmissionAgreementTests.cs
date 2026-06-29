// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for the conformance-keep ↔ member-emission AGREEMENT fix.
///
/// <para>The Swift fixture (<c>ConformanceEmissionAgreement.swift</c>) declares a plain
/// (non-PAT) protocol <c>AsyncTagProvider</c> with a sync <c>label()</c> and an
/// <c>async currentTag()</c> requirement, projecting to a clean
/// <c>IAsyncTagProvider</c> interface. <c>GenericTagBox&lt;Element&gt;</c> witnesses
/// <c>currentTag()</c> as an async method on an unspecialized generic parent, which
/// the emitter cannot emit. Before the fix, the conformance-keep gate (the
/// lightweight <c>CanEmitMethod</c>) and the member-emission gate
/// (<c>ValidateMethodEmission</c>) disagreed: the binding declared
/// <c>GenericTagBox&lt;Element&gt; : IAsyncTagProvider</c> with no <c>CurrentTagAsync</c>
/// member, so the WHOLE module failed to compile (CS0535).</para>
///
/// <para>What this proves, and where:
/// <list type="bullet">
///   <item>COMPILE time — the module builds at all. The mere presence of this file
///     (with <c>GenericTagBox</c> emitted) means the agreement gate dropped the
///     unsatisfiable conformance instead of emitting uncompilable C#.</item>
///   <item>RUNTIME — the fix is surgical: the NON-generic <c>ConcreteTagBox</c> keeps
///     the conformance and round-trips both witnesses, while <c>GenericTagBox</c>
///     degrades gracefully (keeps its emittable <c>Label()</c>, loses only the
///     <c>IAsyncTagProvider</c> projection).</item>
/// </list></para>
/// </summary>
public class ConformanceEmissionAgreementTests : TestBase
{
    public ConformanceEmissionAgreementTests(TestResults results) : base(results) { }

    #region Emittable conformer — conformance KEPT, both witnesses round-trip

    public void TestConcreteConformerImplementsInterface()
    {
        var concrete = new ConcreteTagBox("hello", 7);
        AssertNotNull(concrete, "ConcreteTagBox constructed");

        var implementsInterface = concrete.GetType().GetInterfaces()
            .Any(i => i == typeof(IAsyncTagProvider));
        AssertTrue(implementsInterface,
            "ConcreteTagBox keeps the IAsyncTagProvider conformance — both witnesses are emittable on a non-generic parent");
    }

    public void TestConcreteConformerSyncWitness()
    {
        var concrete = new ConcreteTagBox("alpha", 3);
        AssertEqual("alpha", concrete.GetLabel(),
            "Sync label() witness round-trips on the emittable conformer");
    }

    public async Task TestConcreteConformerAsyncWitness()
    {
        var concrete = new ConcreteTagBox("beta", 42);
        var tag = await WithTimeout(concrete.GetCurrentTagAsync(), DefaultAsyncTimeout);
        AssertEqual(42, tag,
            "Async currentTag() witness round-trips on the emittable conformer");
        TestLogger.Info($"ConcreteTagBox.CurrentTagAsync() = {tag}");
    }

    public void TestInterfaceTypeStillEmitted()
    {
        // The fix must drop only the conformance the emitter can't satisfy, never the
        // interface itself — IAsyncTagProvider stays so the emittable conformer projects it.
        IAsyncTagProvider asInterface = new ConcreteTagBox("iface", 9);
        AssertEqual("iface", asInterface.GetLabel(),
            "IAsyncTagProvider interface is still emitted and dispatches to the conformer through the interface");
    }

    #endregion

    #region Generic conformer — conformance DROPPED, type degrades gracefully

    public void TestGenericConformerDropsConformance()
    {
        var genericBox = Functions.MakeGenericTagBox(99);
        AssertNotNull(genericBox, "GenericTagBox<Int> constructed via factory");

        var implementsInterface = genericBox.GetType().GetInterfaces()
            .Any(i => i == typeof(IAsyncTagProvider));
        AssertFalse(implementsInterface,
            "GenericTagBox<T> must NOT declare IAsyncTagProvider — its async-on-generic-parent witness is unemittable, so the conformance is dropped (graceful degradation) instead of producing CS0535");
    }

    public void TestGenericConformerKeepsEmittableSurface()
    {
        // Dropping the conformance must not strip the type's still-emittable members.
        var genericBox = Functions.MakeGenericTagBox(99);
        AssertEqual("generic", genericBox.GetLabel(),
            "GenericTagBox keeps its emittable sync Label() even after the async-driven conformance drop");
    }

    #endregion
}
