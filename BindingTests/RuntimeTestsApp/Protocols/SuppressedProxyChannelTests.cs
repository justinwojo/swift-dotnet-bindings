// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.Swift;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime gate for the emit-time suppressed-proxy reference decision that replaced the
/// <c>CSharpWrapperCoGater</c> regex post-pass (trigger #3). The Swift fixture lives in
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Protocols/SuppressedProxyChannels.swift</c>.
///
/// <para>
/// <c>Boxable</c> is a plain protocol with an <c>init()</c> requirement, so EveryProtocol
/// cannot conform and <c>BoxableProxy</c> is suppressed. The interface <c>IBoxable</c> is
/// still emitted, and the concrete conformer <c>BoxableIntCell</c> keeps its own witness
/// table — so a Swift-vended conformer round-trips through every CONSUME channel even with
/// the universal proxy gone, while every PRODUCE channel (where a standalone
/// <c>new BoxableProxy(...)</c> would be required) is re-emitted as a throw stub.
/// </para>
///
/// <list type="bullet">
///   <item><b>CONSUME</b> (parameter / array element / closure return / property set /
///   enum-case construction): the proxy wrap-fallback lambda is dropped, the member stays,
///   and a <c>BoxableIntCell</c> still boxes and round-trips.</item>
///   <item><b>PRODUCE</b> (return / array return / property get / enum-payload read): the
///   member body is replaced with <c>throw new NotSupportedException(...)</c>.</item>
/// </list>
/// </summary>
public class SuppressedProxyChannelTests : TestBase
{
    public SuppressedProxyChannelTests(TestResults results) : base(results) { }

    // ============================================================
    // PRODUCE-surface policy assertions (throwing-getter surface policy).
    //
    // The throwing-getter surface policy compile-poisons every suppressed-proxy PRODUCE-throw member:
    // the public read path (method return, property getter, subscript getter — sync AND async) carries
    // [Obsolete("…", error: true, DiagnosticId = "SB0006")], so a consumer's read is a COMPILE error,
    // not a silent runtime NotSupportedException trap. Because a direct read no longer compiles, these
    // channels are asserted structurally via reflection over the member's ObsoleteAttribute rather than
    // by AssertThrows. The [DynamicallyAccessedMembers] annotation roots the reflected members for
    // NativeAOT so the device leg reads real attributes instead of trimmed nulls.
    //
    // Interface-typed and Swift-vended reads (IBoxableVending / IBoxableSink / … obtained from a factory)
    // are NOT poisoned — an interface member cannot carry error:true without breaking its implementers,
    // and those reads throw the pre-existing "Swift-backed existential container" forward-dispatch
    // limitation, a distinct (out-of-scope) channel. Those tests keep their runtime AssertThrows.
    // ============================================================

    private void AssertObsoleteError(MemberInfo? member, string memberDesc, string context)
    {
        AssertNotNull(member, $"{memberDesc} must exist (the degraded member's surface is kept): {context}");
        var obs = member!.GetCustomAttribute<ObsoleteAttribute>();
        AssertNotNull(obs, $"{memberDesc} must carry an [Obsolete] poison so the read fails at compile time: {context}");
        AssertTrue(obs!.IsError,
            $"{memberDesc} [Obsolete] must be error:true — a compile-visible failure, never a silent runtime trap: {context}");
        AssertEqual("SB0006", obs.DiagnosticId,
            $"{memberDesc} poison DiagnosticId must be SB0006 (the suppressed-proxy read policy id): {context}");
    }

    private void AssertMethodPoisoned(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string methodName, string context)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == methodName).ToArray();
        AssertTrue(methods.Length > 0, $"Method '{methodName}' must exist on {type.Name}: {context}");
        foreach (var m in methods)
            AssertObsoleteError(m, $"{type.Name}.{methodName}", context);
    }

    private void AssertGetterPoisoned(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
        string propertyName, string context)
    {
        var p = type.GetProperty(propertyName);
        AssertNotNull(p, $"Property '{propertyName}' must exist on {type.Name}: {context}");
        AssertObsoleteError(p!.GetGetMethod(), $"{type.Name}.{propertyName}.get", context);
    }

    private void AssertIndexerGetterPoisoned(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
        string context)
    {
        var indexer = type.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length > 0);
        AssertNotNull(indexer, $"{type.Name} must expose an indexer (the subscript surface is kept): {context}");
        AssertObsoleteError(indexer!.GetGetMethod(), $"{type.Name}.this[].get", context);
    }

    // ---- CONSUME: member stays, Swift-vended conformer round-trips ----

    /// <summary>
    /// CONSUME — method parameter. <c>AcceptBoxable(any Boxable)</c> boxes its argument
    /// through <c>GetOrCreate&lt;IBoxable&gt;(value)</c> (wrap-fallback lambda dropped) and
    /// dispatches <c>boxedValue()</c> on the concrete conformer.
    /// </summary>
    public void TestAcceptBoxableParameterRoundTrips()
    {
        using var cell = new BoxableIntCell(7);
        var result = TestLibFunctions.AcceptBoxable(cell);
        TestLogger.Info($"AcceptBoxable(BoxableIntCell(7)) = {result}");
        AssertEqual(7, result,
            "AcceptBoxable must box the BoxableIntCell parameter and dispatch boxedValue() " +
            "to it. If this throws EntryPointNotFoundException the CONSUME wrap-fallback drop " +
            "broke the existential boxing; a wrong value means the wrong witness table was used.");
    }

    /// <summary>
    /// CONSUME — array element conversion. <c>SumBoxables([any Boxable])</c> boxes each
    /// element through the per-element wrap fallback (lambda dropped).
    /// </summary>
    public void TestSumBoxablesArrayElementsRoundTrip()
    {
        var cells = new List<BoxableIntCell>
        {
            new BoxableIntCell(2), new BoxableIntCell(3), new BoxableIntCell(5),
        };
        try
        {
            var values = new List<IBoxable>(cells);
            var sum = TestLibFunctions.SumBoxables(values);
            TestLogger.Info($"SumBoxables([2,3,5]) = {sum}");
            AssertEqual(10, sum,
                "SumBoxables must box each [any Boxable] element via the per-element CONSUME " +
                "path and dispatch boxedValue() on each concrete conformer.");
        }
        finally
        {
            foreach (var c in cells) c.Dispose();
        }
    }

    /// <summary>
    /// CONSUME — closure return marshalling. <c>ApplyBoxableFactory(() -&gt; any Boxable)</c>
    /// wraps the managed factory result into an owned existential via
    /// <c>CreateOwnedExistential1&lt;IBoxable&gt;(result)</c> (lambda dropped).
    /// </summary>
    public void TestApplyBoxableFactoryClosureReturnRoundTrips()
    {
        var result = TestLibFunctions.ApplyBoxableFactory(() => new BoxableIntCell(42));
        TestLogger.Info($"ApplyBoxableFactory(() => BoxableIntCell(42)) = {result}");
        AssertEqual(42, result,
            "ApplyBoxableFactory must wrap the closure's BoxableIntCell result into an owned " +
            "existential container and dispatch boxedValue() Swift-side.");
    }

    /// <summary>
    /// CONSUME — property setter. <c>BoxableHolder.boxable</c> set boxes the value
    /// (wrap fallback dropped); the non-existential <c>currentValue()</c> read-back observes
    /// the round-trip without touching the throw-stub getter.
    /// </summary>
    public void TestBoxableHolderSetterRoundTrips()
    {
        using var holder = new BoxableHolder();
        using var cell = new BoxableIntCell(9);
        holder.Boxable = cell;
        var stored = holder.GetCurrentValue();
        TestLogger.Info($"BoxableHolder.Boxable = BoxableIntCell(9); GetCurrentValue() = {stored}");
        AssertEqual(9, stored,
            "BoxableHolder.Boxable setter must box and store the conformer; GetCurrentValue() " +
            "reads boxedValue() back Swift-side, proving the CONSUME setter round-trip.");
    }

    /// <summary>
    /// CONSUME — enum-case construction. <c>BoxableCarrier.Boxed(any Boxable)</c> boxes the
    /// payload (wrap fallback dropped); <c>payloadValue()</c> reads it Swift-side.
    /// </summary>
    public void TestBoxableCarrierConstructionRoundTrips()
    {
        using var cell = new BoxableIntCell(4);
        using var carrier = BoxableCarrier.Boxed(cell);
        var payload = carrier.GetPayloadValue();
        TestLogger.Info($"BoxableCarrier.Boxed(BoxableIntCell(4)).GetPayloadValue() = {payload}");
        AssertEqual(4, payload,
            "BoxableCarrier.Boxed must box the existential payload via the CONSUME case " +
            "construction path; payloadValue() reads boxedValue() back Swift-side.");
    }

    // ---- PRODUCE: member re-emitted as a throw stub ----

    /// <summary>
    /// PRODUCE — method return. <c>MakeBoxable(Int32) -&gt; any Boxable</c> would require a
    /// standalone <c>new BoxableProxy(...)</c>; with the proxy suppressed the member is a stub.
    /// </summary>
    public void TestMakeBoxableReturnThrows()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "MakeBoxable",
            "MakeBoxable returns `any Boxable`, which would need a suppressed BoxableProxy. The PRODUCE " +
            "arm keeps the member but poisons the read path with [Obsolete(error:true) SB0006].");
    }

    /// <summary>
    /// PRODUCE — array return. <c>MakeBoxables([Int32]) -&gt; [any Boxable]</c> stub.
    /// </summary>
    public void TestMakeBoxablesArrayReturnThrows()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "MakeBoxables",
            "MakeBoxables returns [any Boxable]; each element would wrap in a suppressed BoxableProxy, " +
            "so the PRODUCE arm keeps the member but poisons the read path (SB0006).");
    }

    /// <summary>
    /// PRODUCE — property getter. <c>BoxableHolder.boxable</c> get returns <c>any Boxable</c>,
    /// so the getter is a throw stub while the setter (CONSUME) stays usable.
    /// </summary>
    public void TestBoxableHolderGetterThrows()
    {
        AssertGetterPoisoned(typeof(BoxableHolder), "Boxable",
            "BoxableHolder.Boxable getter returns `any Boxable` (PRODUCE) and must be compile-poisoned " +
            "(SB0006) even though the setter on the same property stays usable (CONSUME).");
    }

    /// <summary>
    /// PRODUCE — enum-payload read. <c>BoxableCarrier.TryGetBoxed(out IBoxable)</c> reads the
    /// existential payload, which would materialize a suppressed proxy, so the whole TryGet
    /// member body is a throw stub.
    /// </summary>
    public void TestBoxableCarrierTryGetThrows()
    {
        AssertMethodPoisoned(typeof(BoxableCarrier), "TryGetBoxed",
            "BoxableCarrier.TryGetBoxed reads the `any Boxable` payload, which would build a suppressed " +
            "BoxableProxy; the whole TryGet read member must be compile-poisoned (SB0006).");
    }

    // ============================================================
    // Gap-shape channels (change-8 completion): PRODUCE/CONSUME sites that live OUTSIDE
    // the wrapper-method-body checkpoint — async completion callback, closure invoke-thunk +
    // callback helper classes, bound-generic enum payload projection, container-wrapped
    // property getter, reverse-dispatch proxy bodies, subscripts, existential-bypass adapter.
    // Each is the SAME trigger #3 (suppressed proxy references) at a distinct emit site.
    // ============================================================

    // ---- PRODUCE: async existential return (AsyncHarnessEmitter completion callback) ----

    /// <summary>
    /// PRODUCE — async scalar return. <c>MakeBoxableAsync(Int32) -&gt; any Boxable</c>: the async
    /// completion callback would wrap the Swift result in a suppressed <c>BoxableProxy</c>. The
    /// suppressed arm throws inside the callback's try, which faults the awaiting Task (never
    /// unwinding into native Swift) — so the await observes a <c>NotSupportedException</c>, not a hang.
    /// </summary>
    public void TestMakeBoxableAsyncReturnFaults()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "MakeBoxableAsync",
            "MakeBoxableAsync returns Task<any Boxable>; the completion callback cannot marshal the result " +
            "with BoxableProxy suppressed. An always-faulting Task is a runtime-only trap for a failure the " +
            "generator knows is total, so the async producer is compile-poisoned (SB0006) uniformly with the " +
            "sync produce-throw members. The faulting Task body is retained as a leak-correct backstop and is " +
            "exercised via reflection by SuppressedProxyAsyncCarrierLeakProbeTests.");
    }

    /// <summary>
    /// PRODUCE — async COLLECTION return. <c>MakeManyBoxablesAsync(Int32) -&gt; [any Boxable]</c>: the
    /// async completion callback marshals the collection return per element via the suppressed
    /// proxy, faulting the Task. Distinct emit path from the scalar async return above.
    /// </summary>
    public void TestMakeManyBoxablesAsyncReturnFaults()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "MakeManyBoxablesAsync",
            "MakeManyBoxablesAsync returns Task<[any Boxable]>; the async collection return projection needs " +
            "the suppressed BoxableProxy per element, so the async producer is compile-poisoned (SB0006) " +
            "uniformly with the scalar async arm. Distinct emit path from the scalar async return.");
    }

    // ---- CONSUME: bound-generic enum payload construction round-trips ----

    /// <summary>
    /// CONSUME — bound-generic enum case construction. <c>BoxableBoundCarrier.Many([any Boxable])</c>
    /// boxes each element through the bound-generic CONSUME projection (wrap fallback dropped);
    /// the non-existential <c>GetCount()</c> read-back observes the round-trip without touching the
    /// throw-stub <c>TryGetMany</c> reader.
    /// </summary>
    public void TestBoxableBoundCarrierManyConstructionRoundTrips()
    {
        var cells = new List<BoxableIntCell>
        {
            new BoxableIntCell(2), new BoxableIntCell(3), new BoxableIntCell(5),
        };
        try
        {
            using var carrier = BoxableBoundCarrier.Many(new List<IBoxable>(cells));
            var count = carrier.GetCount();
            TestLogger.Info($"BoxableBoundCarrier.Many([3 cells]).GetCount() = {count}");
            AssertEqual(3, count,
                "BoxableBoundCarrier.Many must box each [any Boxable] element via the bound-generic " +
                "CONSUME projection; GetCount() reads the count back Swift-side.");
        }
        finally
        {
            foreach (var c in cells) c.Dispose();
        }
    }

    /// <summary>
    /// CONSUME — bound-generic Optional case construction. <c>BoxableBoundCarrier.Maybe((any Boxable)?)</c>
    /// boxes the Optional existential payload; <c>None</c> and a nil payload read back as 0.
    /// </summary>
    public void TestBoxableBoundCarrierMaybeConstructionRoundTrips()
    {
        using (var cell = new BoxableIntCell(7))
        using (var present = BoxableBoundCarrier.Maybe(cell))
        {
            AssertEqual(1, present.GetCount(),
                "BoxableBoundCarrier.Maybe(cell) must box the Optional existential payload; GetCount() == 1.");
        }
        using (var absent = BoxableBoundCarrier.Maybe(null))
        {
            AssertEqual(0, absent.GetCount(),
                "BoxableBoundCarrier.Maybe(null) carries no payload; GetCount() == 0.");
        }
        using (var none = BoxableBoundCarrier.None)
        {
            AssertEqual(0, none.GetCount(),
                "BoxableBoundCarrier.None carries no payload; GetCount() == 0.");
        }
    }

    /// <summary>
    /// PRODUCE — bound-generic enum payload read. <c>BoxableBoundCarrier.TryGetMany(out [any Boxable])</c>
    /// would materialize a suppressed proxy per element, so the whole reader body is a throw stub.
    /// </summary>
    public void TestBoxableBoundCarrierTryGetManyThrows()
    {
        AssertMethodPoisoned(typeof(BoxableBoundCarrier), "TryGetMany",
            "BoxableBoundCarrier.TryGetMany reads [any Boxable], which would build a suppressed " +
            "BoxableProxy per element; the whole reader must be compile-poisoned (SB0006).");
    }

    /// <summary>
    /// PRODUCE — bound-generic Optional payload read. <c>BoxableBoundCarrier.TryGetMaybe(out (any Boxable)?)</c>
    /// is a throw stub for the same reason.
    /// </summary>
    public void TestBoxableBoundCarrierTryGetMaybeThrows()
    {
        AssertMethodPoisoned(typeof(BoxableBoundCarrier), "TryGetMaybe",
            "BoxableBoundCarrier.TryGetMaybe reads (any Boxable)?, which would build a suppressed " +
            "BoxableProxy; the whole reader must be compile-poisoned (SB0006).");
    }

    // ---- CONSUME (set) + PRODUCE (get) — container-wrapped Optional existential property ----

    /// <summary>
    /// CONSUME — container-wrapped Optional property setter. <c>OptionalBoxableHolder.MaybeBoxable</c>
    /// set boxes the value through the container-wrapped projection (wrap fallback dropped); the
    /// non-existential <c>HasValue()</c> read-back observes the round-trip.
    /// </summary>
    public void TestOptionalBoxableHolderSetterRoundTrips()
    {
        using var holder = new OptionalBoxableHolder();
        AssertFalse(holder.HasValue(), "Freshly-constructed OptionalBoxableHolder has no value.");
        using (var cell = new BoxableIntCell(11))
        {
            holder.MaybeBoxable = cell;
            AssertTrue(holder.HasValue(),
                "OptionalBoxableHolder.MaybeBoxable setter must box and store the conformer; " +
                "HasValue() reads true back Swift-side, proving the CONSUME setter round-trip.");
        }
        holder.MaybeBoxable = null;
        AssertFalse(holder.HasValue(),
            "Setting MaybeBoxable = null must clear the stored value; HasValue() reads false.");
    }

    /// <summary>
    /// PRODUCE — container-wrapped Optional property getter. <c>OptionalBoxableHolder.MaybeBoxable</c>
    /// get returns <c>(any Boxable)?</c> through the container-wrapped projection, so the getter is a
    /// throw stub while the setter (CONSUME) stays usable.
    /// </summary>
    public void TestOptionalBoxableHolderGetterThrows()
    {
        AssertGetterPoisoned(typeof(OptionalBoxableHolder), "MaybeBoxable",
            "OptionalBoxableHolder.MaybeBoxable getter returns (any Boxable)? (PRODUCE) and must be " +
            "compile-poisoned (SB0006) even though the setter on the same property stays usable (CONSUME).");
    }

    // ---- PRODUCE: closure ARG any Boxable (Swift→C# callback deserialization) ----

    /// <summary>
    /// PRODUCE — non-throwing closure ARG. <c>RunBoxableConsumer((any Boxable) -&gt; Void)</c> hands the
    /// C# callback a Swift-vended <c>any Boxable</c> it would deserialize via a suppressed proxy. The
    /// suppressed-arm <c>[UnmanagedCallersOnly]</c> callback must be a guarded no-op: the user delegate
    /// never fires and the call completes without throwing. Validates the ClosureEmitter escaping-consume
    /// no-op-stub-routed-through-the-UCO-guard fix.
    /// </summary>
    public void TestRunBoxableConsumerCallbackIsNoOp()
    {
        bool fired = false;
        TestLibFunctions.RunBoxableConsumer(_ => fired = true);
        AssertFalse(fired,
            "RunBoxableConsumer's [UnmanagedCallersOnly] callback must be a guarded no-op when " +
            "BoxableProxy is suppressed — the user delegate must never fire (the Swift-vended " +
            "existential cannot be deserialized), and the call must still complete without throwing.");
    }

    /// <summary>
    /// PRODUCE — throwing closure ARG. <c>RunThrowingBoxableConsumer((any Boxable) throws -&gt; Void)</c>:
    /// the suppressed-arm <c>[UnmanagedCallersOnly]</c> callback reports through the throwing channel
    /// (<c>*errorOut</c>) instead of a no-op; Swift rethrows it and C# surfaces a <c>SwiftException</c>.
    /// The user delegate never fires. Validates the ClosureEmitter throwing-consume cooperative-report fix.
    /// </summary>
    public void TestRunThrowingBoxableConsumerReportsThroughThrowingChannel()
    {
        bool fired = false;
        // The convenience Action<IBoxable> overload is emitted as an instance method (a pre-existing
        // throwing-closure-convenience-overload quirk in ThrowingClosureSimplificationEmitter,
        // orthogonal to proxy suppression), so it is not reachable via the static `Functions` type
        // alias. Call the canonical SwiftResult-returning static overload directly — either overload
        // routes through the same suppressed-arm UCO callback (the throwing channel under test).
        Func<IBoxable, Swift.SwiftResult<Swift.SwiftVoid, SwiftError>> cb =
            _ => { fired = true; return Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(Swift.SwiftVoid.Value); };
        AssertThrows<SwiftException>(
            () => TestLibFunctions.RunThrowingBoxableConsumer(cb),
            "RunThrowingBoxableConsumer's throwing [UnmanagedCallersOnly] callback must report the " +
            "suppressed proxy through *errorOut; Swift rethrows it and C# surfaces a SwiftException.");
        AssertFalse(fired,
            "The throwing consumer's user delegate must never fire — the callback reports through " +
            "*errorOut before any existential deserialization.");
    }

    // ---- PRODUCE: closure RETURN any Boxable (Swift-closure-as-delegate invoke thunk) ----

    /// <summary>
    /// PRODUCE — closure return. <c>GetBoxableProducer() -&gt; () -&gt; any Boxable</c>: the invoke-thunk
    /// helper would build a suppressed proxy per invocation, so the member is a throw stub.
    /// </summary>
    public void TestGetBoxableProducerThrows()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "GetBoxableProducer",
            "GetBoxableProducer returns a closure producing `any Boxable`; its invoke thunk would " +
            "build a suppressed BoxableProxy, so the member must be compile-poisoned (SB0006).");
    }

    /// <summary>
    /// PRODUCE — closure return with a struct parameter. Routes through the struct-param closure
    /// invoke-thunk emitter; still a throw stub.
    /// </summary>
    public void TestGetBoxableProducerWithParamThrows()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "GetBoxableProducerWithParam",
            "GetBoxableProducerWithParam returns a (BoxableTag) -> any Boxable closure; the struct-param " +
            "invoke thunk would build a suppressed BoxableProxy, so the member must be compile-poisoned (SB0006).");
    }

    /// <summary>
    /// PRODUCE — throwing closure return. Routes through the throwing-closure invoke-thunk emitter's
    /// success-payload path; still a throw stub.
    /// </summary>
    public void TestGetThrowingBoxableProducerThrows()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "GetThrowingBoxableProducer",
            "GetThrowingBoxableProducer returns a () throws -> any Boxable closure; the throwing invoke " +
            "thunk's success-payload path would build a suppressed BoxableProxy, so the member must be poisoned (SB0006).");
    }

    // ---- PRODUCE: indirect-return closure ARG (Category A) — not invocation-tested ----

    /// <summary>
    /// PRODUCE — indirect-return closure ARG (Category A). <c>RunIndirectBoxableConsumer((any Boxable)
    /// -&gt; Int32?)</c> forces the indirect-return callback emitter (the bound-generic <c>Int32?</c>
    /// return). With the proxy suppressed the helper <c>[UnmanagedCallersOnly]</c> callback cannot
    /// checkpoint-throw (Hazard D) and cannot leave the Swift-allocated indirect-result buffer
    /// uninitialized, so it FailFasts — which aborts the process by design. There is no observable
    /// managed exception to assert, so this channel is covered structurally (the generated callback
    /// routes through FailFast inside the UCO guard, asserted by CatchFreeUcoValidatorTests) rather
    /// than by invocation.
    /// </summary>
    [Skip("Category A indirect-return suppressed-arg callback FailFasts (process abort) by design — " +
          "not invocation-testable; covered structurally by CatchFreeUcoValidatorTests.")]
    public void TestRunIndirectBoxableConsumerFailFastsByDesign()
    {
        // Intentionally not invoked: RunIndirectBoxableConsumer would abort the process via FailFast.
        // See the doc comment above for why this is a structural-only gate.
    }

    // ---- PRODUCE: reverse-dispatch existential return proxy bodies (Category B) ----

    /// <summary>
    /// PRODUCE — reverse-dispatch existential return. <c>MakeBoxableVending(Int32) -&gt; any
    /// BoxableVending</c> returns a usable <c>BoxableVendingProxy</c> (BoxableVending has no init()
    /// requirement). Each of its existential-returning members — scalar method/property and the
    /// COLLECTION method/property — reverse-dispatches into Swift and would build a suppressed
    /// <c>BoxableProxy</c>, so every member body throws while the interface members stay.
    /// </summary>
    public void TestBoxableVendingProxyMembersThrow()
    {
        var vending = TestLibFunctions.MakeBoxableVending(3);
        try
        {
            AssertThrows<NotSupportedException>(
                () => vending.GetVendBoxable(),
                "BoxableVendingProxy.GetVendBoxable() returns `any Boxable` via reverse dispatch; " +
                "the proxy method body must throw NotSupportedException.");
            AssertThrows<NotSupportedException>(
                () => { var _ = vending.CurrentBoxable; },
                "BoxableVendingProxy.CurrentBoxable getter returns `any Boxable`; must throw.");
            AssertThrows<NotSupportedException>(
                () => vending.GetVendManyBoxables(),
                "BoxableVendingProxy.GetVendManyBoxables() returns [any Boxable] via the collection " +
                "reverse-dispatch path; must throw.");
            AssertThrows<NotSupportedException>(
                () => { var _ = vending.AllBoxables; },
                "BoxableVendingProxy.AllBoxables getter returns [any Boxable] via the collection " +
                "reverse-dispatch getter path; must throw.");
        }
        finally
        {
            (vending as IDisposable)?.Dispose();
        }
    }

    // ---- PRODUCE: concrete-type subscript existential return (Category B-collection + scalar) ----

    /// <summary>
    /// PRODUCE — concrete subscript returning a COLLECTION. <c>BoxableShelf[Int32] -&gt; [any Boxable]</c>:
    /// the indexer getter projects each element via a suppressed proxy, so the getter is a throw stub
    /// while the indexer member stays.
    /// </summary>
    public void TestBoxableShelfSubscriptThrows()
    {
        AssertIndexerGetterPoisoned(typeof(BoxableShelf),
            "BoxableShelf's subscript returns [any Boxable]; the indexer getter would project each " +
            "element via a suppressed BoxableProxy, so it must be compile-poisoned (SB0006).");
    }

    /// <summary>
    /// PRODUCE — concrete subscript returning a SCALAR. <c>BoxableRack[Int32] -&gt; any Boxable</c>:
    /// the scalar-existential indexer getter delegates to the wrapper cdecl accessor, whose existential
    /// PRODUCE is gated, so the getter is a throw stub.
    /// </summary>
    public void TestBoxableRackSubscriptThrows()
    {
        AssertIndexerGetterPoisoned(typeof(BoxableRack),
            "BoxableRack's subscript returns a scalar `any Boxable`; the indexer getter delegates to a " +
            "wrapper accessor that restubs to a produce-throw, so the public getter must be compile-poisoned (SB0006).");
    }

    // ---- PRODUCE: existential-bypass adapter existential return (Category C) ----

    /// <summary>
    /// PRODUCE — existential-bypass adapter. <c>BoxableBypassHost.produceBoxable(tag: any Boxable =
    /// BoxableIntCell()) -&gt; any Boxable</c>: the bypass adapter fires (HasExistentialArg) and owns
    /// the whole emission; its existential-return wrap would build a suppressed proxy, so both the
    /// defaulted and explicit overloads keep the public member but throw.
    /// </summary>
    public void TestBoxableBypassHostProduceThrows()
    {
        AssertMethodPoisoned(typeof(BoxableBypassHost), "ProduceBoxable",
            "Both BoxableBypassHost.ProduceBoxable overloads return `any Boxable` via the bypass adapter; " +
            "the existential-return wrap would build a suppressed BoxableProxy, so every overload must be " +
            "compile-poisoned (SB0006).");
    }

    // ============================================================
    // Change-8f gap-shape channels: the four ungated trigger-#3 sites the r2 reviews surfaced
    // (container property setter, container subscript setter, optional-collection async return,
    // container closure arg). Each is the SAME suppressed-proxy-reference decision at an emit site
    // the first change-8 passes missed and the CoGater still masked textually.
    // ============================================================

    // ---- CONSUME (set) + PRODUCE (get) — collection-valued existential PROPERTY ----

    /// <summary>
    /// CONSUME — collection property setter. <c>BoxableCollectionHolder.Boxables</c> set boxes each
    /// element through the general (container) projection path whose <c>ProjectionContext</c> now carries
    /// EmissionContext, so the per-element <c>CreateOwnedExistential1&lt;IBoxable&gt;(e)</c> drops its wrap
    /// lambda; the non-existential <c>GetCount()</c> read-back observes the round-trip.
    /// </summary>
    public void TestBoxableCollectionHolderSetterRoundTrips()
    {
        var cells = new List<BoxableIntCell> { new BoxableIntCell(2), new BoxableIntCell(3), new BoxableIntCell(5) };
        try
        {
            using var holder = new BoxableCollectionHolder();
            holder.Boxables = new List<IBoxable>(cells);
            var count = holder.GetCount();
            TestLogger.Info($"BoxableCollectionHolder.Boxables = [3 cells]; GetCount() = {count}");
            AssertEqual(3, count,
                "BoxableCollectionHolder.Boxables collection setter must box each element via the " +
                "EmissionContext-armed container projection (wrap lambda dropped) and store them; " +
                "GetCount() reads the stored count back Swift-side.");
        }
        finally
        {
            foreach (var c in cells) c.Dispose();
        }
    }

    /// <summary>
    /// PRODUCE — collection property getter. <c>BoxableCollectionHolder.Boxables</c> get returns
    /// <c>[any Boxable]</c>, so the getter is a throw stub while the setter (CONSUME) stays usable.
    /// </summary>
    public void TestBoxableCollectionHolderGetterThrows()
    {
        AssertGetterPoisoned(typeof(BoxableCollectionHolder), "Boxables",
            "BoxableCollectionHolder.Boxables getter returns [any Boxable] (PRODUCE) and must be " +
            "compile-poisoned (SB0006) even though the collection setter on the same property stays usable (CONSUME).");
    }

    // ---- CONSUME (set) + PRODUCE (get) — collection-valued existential SUBSCRIPT ----

    /// <summary>
    /// CONSUME — collection subscript setter. <c>BoxableSettableRack[Int32] = [any Boxable]</c> boxes each
    /// element through <c>SubscriptHandler.EmitIndexerSetter</c>'s now-EmissionContext-armed projection
    /// (wrap lambda dropped); the non-existential <c>Count(Int32)</c> read-back observes the round-trip
    /// without touching the throw-stub collection getter.
    /// </summary>
    public void TestBoxableSettableRackSubscriptSetterRoundTrips()
    {
        var cells = new List<BoxableIntCell> { new BoxableIntCell(1), new BoxableIntCell(2) };
        try
        {
            using var rack = new BoxableSettableRack();
            rack[0] = new List<IBoxable>(cells);
            var count = rack.Count(0);
            TestLogger.Info($"BoxableSettableRack[0] = [2 cells]; Count(0) = {count}");
            AssertEqual(2, count,
                "BoxableSettableRack collection subscript setter must box each element via the " +
                "EmissionContext-armed SubscriptHandler projection (wrap lambda dropped) and store them; " +
                "Count(0) reads the stored count back Swift-side.");
        }
        finally
        {
            foreach (var c in cells) c.Dispose();
        }
    }

    /// <summary>
    /// PRODUCE — collection subscript getter. <c>BoxableSettableRack[Int32] -&gt; [any Boxable]</c> get is a
    /// throw stub (the indexer getter probe restubs it) while the setter (CONSUME) stays usable.
    /// </summary>
    public void TestBoxableSettableRackSubscriptGetterThrows()
    {
        AssertIndexerGetterPoisoned(typeof(BoxableSettableRack),
            "BoxableSettableRack's subscript returns [any Boxable]; the indexer getter would project each " +
            "element via a suppressed BoxableProxy, so it must be compile-poisoned (SB0006) even though " +
            "the collection subscript setter stays usable.");
    }

    // ---- PRODUCE: async OPTIONAL-of-COLLECTION existential return ----

    /// <summary>
    /// PRODUCE — async optional-collection return. <c>MakeManyBoxablesAsyncOptional(Int32) -&gt;
    /// [any Boxable]?</c>: the optional projection is built by <c>AsyncHarnessEmitter.ProjectReturn</c>,
    /// whose inner container per-element PRODUCE now throws <c>SuppressedProxyReferenceException</c>
    /// (caught in <c>TryGetOptionalMarshalType</c>, surfaced as <c>proxySuppressed</c>); the completion
    /// callback faults the awaiting Task with <c>NotSupportedException</c> rather than referencing the
    /// absent proxy. Distinct emit path from the non-optional async collection return.
    /// </summary>
    public void TestMakeManyBoxablesAsyncOptionalReturnFaults()
    {
        AssertMethodPoisoned(typeof(TestLibFunctions), "MakeManyBoxablesAsyncOptionalAsync",
            "MakeManyBoxablesAsyncOptional returns Task<[any Boxable]?>; the async optional-collection " +
            "ProjectReturn path needs the suppressed BoxableProxy per element, so the async producer is " +
            "compile-poisoned (SB0006). Distinct emit path from the non-optional async collection return.");
    }

    // ---- PRODUCE: container closure ARG ([any Boxable]) -> Void ----

    /// <summary>
    /// PRODUCE — container closure ARG. <c>RunBoxableListConsumer(([any Boxable]) -&gt; Void)</c> hands the
    /// C# callback a Swift-vended array of existentials it would deserialize per element via a suppressed
    /// proxy. <c>IsProxyReferenceSuppressed</c> now recurses into the container element, so the
    /// suppressed-arm <c>[UnmanagedCallersOnly]</c> callback is a guarded no-op: the user delegate never
    /// fires and the call completes without throwing (matching the scalar <c>RunBoxableConsumer</c> twin).
    /// </summary>
    public void TestRunBoxableListConsumerCallbackIsNoOp()
    {
        bool fired = false;
        TestLibFunctions.RunBoxableListConsumer(_ => fired = true);
        AssertFalse(fired,
            "RunBoxableListConsumer's [UnmanagedCallersOnly] callback must be a guarded no-op when " +
            "BoxableProxy is suppressed — the container element existential cannot be deserialized, so " +
            "the user delegate must never fire and the call must still complete without throwing.");
    }

    // ============================================================
    // REVERSE-DISPATCH RECEIVER channels (B3, 2026-06-29)
    //
    // BoxableSink / BoxableAccepting / BoxableSubscriptSink have NO init() requirement, so their
    // OWN proxies (BoxableSinkProxy / BoxableAcceptingProxy / BoxableSubscriptSinkProxy) ARE emitted.
    // The suppressed proxy is the inner `any Boxable` payload's BoxableProxy. The proxy's
    // reverse-dispatch receiver trampolines — Receive_boxable_set (a settable existential property's
    // setter receiver), Receive_consume_* (an existential method param's receiver), and
    // Receive_subscript_*_set (a settable existential subscript's setter receiver) — would CONSUME a
    // Swift `any Boxable` into a C# IBoxable via `new BoxableProxy(...)`, which throws
    // SuppressedProxyReferenceException during string projection. BEFORE the B3 fix that
    // exception propagated uncaught to StringEmitter.EmitModule and aborted the WHOLE module
    // (no .cs produced) — the exact FBSDKShareKit SharingContentProxy failure. The fix degrades
    // just those receivers to a fail-fast stub (keeping the &Receive_* static-init address-take
    // valid) so the rest of the module ships.
    //
    // The reverse-dispatch FailFast itself is unreachable from these fixtures (no Swift entry
    // point hands a C# IBoxableSink/IBoxableAccepting back to Swift to call its setter/method),
    // and a degraded receiver aborts the process if ever invoked — so it is covered STRUCTURALLY
    // (compile gate: the receiver symbol stays defined, 0 CS errors; emission report:
    // degradedReverseDispatchReceivers lists both members; SWIFTBIND061 warns per member). What
    // these runtime tests prove is the GRACEFUL-DEGRADATION outcome the fix exists for: the module
    // emitted at all, and the FORWARD-dispatch surface on the proxy-emitted protocols still works.
    // ============================================================

    /// <summary>
    /// B3 graceful degradation — settable existential property. <c>BoxableSink</c> is the exact
    /// FBSDKShareKit <c>SharingContentProxy</c> shape (a settable <c>any Boxable</c> property on a
    /// proxy-emitted protocol). Before the fix the suppressed-proxy reference on the
    /// <c>Receive_boxable_set</c> receiver aborted the whole module. Now the module ships:
    /// <c>BoxableSinkImpl</c> constructs, the forward setter (CONSUME) round-trips through
    /// <c>GetCurrentValue()</c>, and the forward getter (PRODUCE) throws as a normal degraded member.
    /// </summary>
    public void TestBoxableSinkForwardRoundTripsModuleEmitted()
    {
        using var sink = new BoxableSinkImpl(3);
        AssertEqual(3, sink.GetCurrentValue(),
            "BoxableSinkImpl(3) must construct and read back boxedValue() == 3. If the module had " +
            "aborted on the suppressed-proxy Receive_boxable_set receiver (the pre-B3 behaviour), none " +
            "of these types would exist — this is the graceful-degradation headline assertion.");

        using var cell = new BoxableIntCell(9);
        sink.Boxable = cell;
        AssertEqual(9, sink.GetCurrentValue(),
            "BoxableSink.Boxable forward setter (CONSUME) must box and store the conformer; " +
            "GetCurrentValue() reads boxedValue() back Swift-side, proving the forward setter round-trip " +
            "survives even though the reverse-dispatch Receive_boxable_set receiver degraded.");

        AssertGetterPoisoned(typeof(BoxableSinkImpl), "Boxable",
            "BoxableSinkImpl.Boxable forward getter returns `any Boxable` (PRODUCE) and is a normal degraded " +
            "member, compile-poisoned (SB0006); the settable property still emits a usable setter.");
    }

    /// <summary>
    /// B3 graceful degradation — Swift-vended settable existential. <c>MakeBoxableSink(7)</c> returns a
    /// non-null <c>IBoxableSink</c> (the <c>BoxableSinkProxy</c>/factory emitted, not aborted on the
    /// suppressed <c>Receive_boxable_set</c> receiver) — that non-null return IS the graceful-degradation
    /// headline. The returned value is a Swift-VENDED existential, so a forward <c>Boxable</c> getter on it
    /// throws the PRE-EXISTING "Swift-backed existential container" limitation (forward member access is
    /// only supported when the proxy wraps a C# implementation — see ProtocolProxyEmitter.InterfaceImpl.cs),
    /// NOT a B3 abort.
    /// </summary>
    public void TestMakeBoxableSinkReturnsSwiftVendedExistential()
    {
        var sink = TestLibFunctions.MakeBoxableSink(7);
        try
        {
            AssertNotNull(sink,
                "MakeBoxableSink(7) must return a non-null IBoxableSink — proving the module emitted " +
                "(factory + BoxableSinkProxy present) instead of aborting on the suppressed " +
                "Receive_boxable_set receiver. This is the graceful-degradation headline assertion.");

            AssertThrows<NotSupportedException>(
                () => { var _ = sink.Boxable; },
                "The `any Boxable` getter on a SWIFT-VENDED existential hits the pre-existing 'Swift-backed " +
                "existential container' forward-dispatch limitation (only supported when the proxy wraps a " +
                "C# impl) — unrelated to B3.");
        }
        finally
        {
            (sink as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// B3 graceful degradation — existential method parameter. <c>BoxableAccepting.consume(any Boxable)</c>
    /// is a proxy-emitted protocol whose <c>Receive_consume_*</c> receiver would CONSUME a Swift
    /// existential via the suppressed proxy. The forward <c>Consume(IBoxable)</c> (CONSUME) round-trips:
    /// boxing a <c>BoxableIntCell(13)</c> and dispatching <c>boxedValue()</c> Swift-side returns 13.
    /// </summary>
    public void TestBoxableAcceptingForwardConsumeRoundTrips()
    {
        using var accepting = new BoxableAcceptingImpl();
        using var cell = new BoxableIntCell(13);
        var result = accepting.Consume(cell);
        TestLogger.Info($"BoxableAcceptingImpl().Consume(BoxableIntCell(13)) = {result}");
        AssertEqual(13, result,
            "BoxableAccepting.Consume forward method (CONSUME) must box the IBoxable arg and dispatch " +
            "boxedValue() Swift-side, returning 13 — proving the forward param channel survives even " +
            "though the reverse-dispatch Receive_consume_* receiver degraded to a fail-fast stub.");
    }

    /// <summary>
    /// B3 graceful degradation — Swift-vended method-param existential. <c>MakeBoxableAccepting()</c>
    /// returns a non-null <c>IBoxableAccepting</c> (the <c>BoxableAcceptingProxy</c>/factory emitted,
    /// not aborted on the suppressed <c>Receive_consume_*</c> receiver) — that non-null return IS the
    /// graceful-degradation headline. The returned value is a Swift-VENDED existential, so a forward
    /// <c>Consume</c> call on it throws the PRE-EXISTING "Swift-backed existential container" limitation
    /// (forward C#→Swift method dispatch is only supported when the proxy wraps a C# implementation —
    /// see ProtocolProxyEmitter.InterfaceImpl.cs), NOT a B3 abort. Contrast the sibling
    /// <see cref="TestBoxableAcceptingForwardConsumeRoundTrips"/>, which forward-calls <c>Consume</c> on a
    /// CONCRETE <c>BoxableAcceptingImpl</c> (not an existential) and round-trips.
    /// </summary>
    public void TestMakeBoxableAcceptingReturnsSwiftVendedExistential()
    {
        var accepting = TestLibFunctions.MakeBoxableAccepting();
        try
        {
            AssertNotNull(accepting,
                "MakeBoxableAccepting() must return a non-null IBoxableAccepting — proving the module " +
                "emitted (factory + BoxableAcceptingProxy present) instead of aborting on the suppressed " +
                "Receive_consume_* receiver. This is the graceful-degradation headline assertion.");

            using var cell = new BoxableIntCell(21);
            AssertThrows<NotSupportedException>(
                () => { var _ = accepting.Consume(cell); },
                "Consume on a SWIFT-VENDED any BoxableAccepting hits the pre-existing 'Swift-backed " +
                "existential container' forward-dispatch limitation (only supported when the proxy wraps a " +
                "C# impl) — unrelated to B3. The concrete-class path round-trips in " +
                "TestBoxableAcceptingForwardConsumeRoundTrips.");
        }
        finally
        {
            (accepting as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// B3 graceful degradation — settable existential SUBSCRIPT. <c>BoxableSubscriptSink</c> is the
    /// subscript analogue of <c>BoxableSink</c> (a settable <c>any Boxable</c> subscript on a
    /// proxy-emitted protocol), exercising the <c>Receive_subscript_*_set</c> receiver channel that the
    /// other reverse-dispatch fixtures don't reach. Before the fix the suppressed-proxy reference on that
    /// receiver aborted the whole module. Now the module ships: <c>BoxableSubscriptSinkImpl</c>
    /// constructs, the forward subscript setter (CONSUME) round-trips through <c>ValueAt(int)</c>, and the
    /// forward subscript getter (PRODUCE) throws as a normal degraded member.
    /// </summary>
    public void TestBoxableSubscriptSinkForwardRoundTripsModuleEmitted()
    {
        using var sink = new BoxableSubscriptSinkImpl(5);
        AssertEqual(5, sink.ValueAt(0),
            "BoxableSubscriptSinkImpl(5) must construct and read back boxedValue() == 5 at index 0. If the " +
            "module had aborted on the suppressed-proxy Receive_subscript_*_set receiver (the pre-B3 " +
            "behaviour), none of these types would exist — the graceful-degradation headline assertion.");

        using var cell = new BoxableIntCell(8);
        sink[1] = cell;
        AssertEqual(8, sink.ValueAt(1),
            "BoxableSubscriptSink subscript forward setter (CONSUME) must box and store the conformer; " +
            "ValueAt(1) reads boxedValue() back Swift-side, proving the forward subscript setter round-trip " +
            "survives even though the reverse-dispatch Receive_subscript_*_set receiver degraded.");

        AssertIndexerGetterPoisoned(typeof(BoxableSubscriptSinkImpl),
            "BoxableSubscriptSinkImpl subscript forward getter returns `any Boxable` (PRODUCE) and is a normal " +
            "degraded member, compile-poisoned (SB0006); the settable subscript still emits a usable setter.");
    }

    /// <summary>
    /// B3 graceful degradation — Swift-vended settable existential subscript.
    /// <c>MakeBoxableSubscriptSink(7)</c> returns a non-null <c>IBoxableSubscriptSink</c> (the
    /// <c>BoxableSubscriptSinkProxy</c>/factory emitted, not aborted on the suppressed
    /// <c>Receive_subscript_*_set</c> receiver) — that non-null return IS the graceful-degradation
    /// headline. The returned value is a Swift-VENDED existential, so a forward subscript getter on it
    /// throws the PRE-EXISTING "Swift-backed existential container" limitation (forward member access is
    /// only supported when the proxy wraps a C# implementation — see
    /// ProtocolProxyEmitter.InterfaceImpl.cs), NOT a B3 abort.
    /// </summary>
    public void TestMakeBoxableSubscriptSinkReturnsSwiftVendedExistential()
    {
        var sink = TestLibFunctions.MakeBoxableSubscriptSink(7);
        try
        {
            AssertNotNull(sink,
                "MakeBoxableSubscriptSink(7) must return a non-null IBoxableSubscriptSink — proving the " +
                "module emitted (factory + BoxableSubscriptSinkProxy present) instead of aborting on the " +
                "suppressed Receive_subscript_*_set receiver. This is the graceful-degradation headline.");

            AssertThrows<NotSupportedException>(
                () => { var _ = sink[0]; },
                "The `any Boxable` subscript getter on a SWIFT-VENDED existential hits the pre-existing " +
                "'Swift-backed existential container' forward-dispatch limitation (only supported when the " +
                "proxy wraps a C# impl) — unrelated to B3.");
        }
        finally
        {
            (sink as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// PRODUCE — CS0542-renamed explicit-interface bridge. <c>BoxableCollider</c>'s property
    /// <c>boxableCollider</c> projects to the SAME C# name as its enclosing type, so the generator
    /// CS0542-renames the public property to <c>BoxableColliderValue</c> and emits an explicit-interface
    /// bridge <c>IBoxableColliderProtocol.BoxableCollider</c>. Because the property is <c>any Boxable</c>
    /// (BoxableProxy suppressed) the public renamed getter is compile-poisoned (SB0006). The bridge getter
    /// must NOT read the poisoned public property, which would be a CS0619 compile error that fails the whole
    /// binding build — instead it emits a direct throw (routed on the property-level getter-poison flag, the
    /// same uniform path as the collection twin below). That the fixture compiles at all is the primary gate;
    /// this test additionally locks (a) the public renamed getter is poisoned, and (b) the interface-bridge
    /// read stays a *runtime* throw (interface-contract reads stay runtime-throws, never compile-poisoned).
    /// </summary>
    public void TestBoxableColliderRenamedBridgeStaysCompileSafe()
    {
        using var collider = TestLibFunctions.MakeBoxableCollider(9);

        // Forward-dispatch construction is observable without touching the suppressed-proxy read.
        AssertEqual(9, collider.GetColliderValue(),
            "MakeBoxableCollider(9).GetColliderValue() must round-trip the seed via forward dispatch — " +
            "if this throws, the fixture didn't construct the collider (BoxableIntCell not boxed in).");

        // (a) The CS0542-renamed PUBLIC getter is compile-poisoned. Read via reflection — a direct
        // `collider.BoxableColliderValue` read would itself be a CS0619 compile error.
        AssertGetterPoisoned(typeof(BoxableCollider), "BoxableColliderValue",
            "BoxableCollider.BoxableColliderValue reads `any Boxable` with BoxableProxy suppressed, so its " +
            "public getter must carry [Obsolete(error:true, DiagnosticId=SB0006)].");

        // (b) The explicit-interface bridge is NOT poisoned (interface contracts stay runtime-throws), so
        // reading it compiles and throws NotSupportedException at runtime via a direct throw — NOT by reading
        // the poisoned public property.
        var asProtocol = (IBoxableColliderProtocol)collider;
        AssertThrows<NotSupportedException>(
            () => { var _ = asProtocol.BoxableCollider; },
            "The IBoxableColliderProtocol.BoxableCollider bridge read must throw NotSupportedException at " +
            "runtime (a direct throw). If the bridge instead read the poisoned public property, the " +
            "generated binding would have failed to compile (CS0619).");
    }

    /// <summary>
    /// PRODUCE — CS0542-renamed explicit-interface bridge, COLLECTION-element poison twin of
    /// <see cref="TestBoxableColliderRenamedBridgeStaysCompileSafe"/>. <c>BoxableColliderList</c>'s
    /// <c>[any Boxable]</c> property collides with its type name → CS0542 rename → poisoned public getter +
    /// explicit-interface bridge. Unlike the scalar twin, this getter is poisoned by the collection-element
    /// projection catch (no accessor side-table entry; the private accessor returns the raw Swift array), so
    /// the bridge must route on the property-level getter-poison flag and emit a DIRECT throw — it cannot
    /// delegate to the raw accessor. Compilation is the primary gate; this test locks the poison + runtime
    /// throw.
    /// </summary>
    public void TestBoxableColliderListRenamedBridgeStaysCompileSafe()
    {
        using var list = TestLibFunctions.MakeBoxableColliderList(4);

        AssertEqual(2, list.GetListCount(),
            "MakeBoxableColliderList(4).GetListCount() must be 2 (the fixture boxes two BoxableIntCells) — " +
            "if this throws, forward-dispatch construction of the collection collider failed.");

        AssertGetterPoisoned(typeof(BoxableColliderList), "BoxableColliderListValue",
            "BoxableColliderList.BoxableColliderListValue reads `[any Boxable]` with BoxableProxy suppressed, " +
            "so its public getter must carry [Obsolete(error:true, DiagnosticId=SB0006)].");

        var asProtocol = (IBoxableColliderListProtocol)list;
        AssertThrows<NotSupportedException>(
            () => { var _ = asProtocol.BoxableColliderList; },
            "The IBoxableColliderListProtocol.BoxableColliderList bridge read must throw NotSupportedException " +
            "at runtime (direct throw). If the bridge read the poisoned public property, the generated binding " +
            "would have failed to compile (CS0619) — the collection-element poison path.");
    }
}
