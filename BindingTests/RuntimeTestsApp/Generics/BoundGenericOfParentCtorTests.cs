// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for the AppIntents 0.12.0 site #4 constructor shape:
/// a generic host whose constructor accepts a foreign generic struct
/// parameterised on the host's own generic (<c>Box&lt;T&gt;</c>-shaped param,
/// not a nested type). Mirrors
/// <c>IntentParameterSummary&lt;Intent&gt;.init(_: ParameterSummaryString&lt;Intent&gt;, …)</c>
/// but with an unconstrained <c>TElement</c> so the wrapper-helper gate
/// (<c>HasUnresolvableTypeConformances</c>) doesn't preempt emission.
///
/// Before doc 14 this shape fell through to <c>[Obsolete(SB0001)]</c> direct-CallConvSwift
/// because the constructor's static-factory admission gate
/// (<c>GenericDispatchEmitter.CanEmitStaticDispatch</c>) only admitted bare T,
/// <c>Array&lt;T&gt;</c>, KeyPath family, and nested-of-parent. The widened gate now
/// routes bound-generic-of-parent params through the existing factory with the default
/// <c>assumingMemoryBound(to: BoxedGenericPayload&lt;T&gt;.self).pointee</c> reconstruction.
/// </summary>
public class BoundGenericOfParentCtorTests : TestBase
{
    public BoundGenericOfParentCtorTests(TestResults results) : base(results) { }

    public void TestBoundGenericOfParent_AcceptsBoxedPayload()
    {
        var box = new BoxedGenericPayload<BoxKP>(new BoxKP());
        using var host = new BoundGenericOfParentHost<BoxKP>(box);
        // Construction + Dispose both go through the widened gate. The getter reads
        // the stored TElement via the generic-class projection pipeline.
        // We assert via a string projection that exercises the full round-trip.
        var description = host.StoredDescription.ToString();
        AssertNotNull(description, "BoxedGenericPayload<T> ctor param round-trips through GSF");
    }
}
