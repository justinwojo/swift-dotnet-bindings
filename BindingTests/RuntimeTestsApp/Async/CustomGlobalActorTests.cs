// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for SWIFTBIND022: constructor skip on custom-global-actor-isolated types.
///
/// When a class is annotated with a custom global actor (e.g., <c>@BindingsTestGlobalActor</c>),
/// the wrapper generator can't safely emit a synchronous <c>@_cdecl</c> wrapper that calls into
/// the actor's executor. The skip leaves the type itself usable for whatever non-init APIs
/// remain, but no constructor is generated.
/// </summary>
public class CustomGlobalActorTests : TestBase
{
    public CustomGlobalActorTests(TestResults results) : base(results) { }

    /// <summary>
    /// The class type itself must still appear in the bindings — only the constructor
    /// is skipped. This guards against an over-broad skip that drops the whole type.
    /// </summary>
    public void TestGlobalActorIsolatedClass_TypeIsGenerated()
    {
        var type = typeof(GlobalActorIsolatedClass);
        AssertNotNull(type, "GlobalActorIsolatedClass should be present in generated bindings");
    }

    /// <summary>
    /// SWIFTBIND022 must skip every public constructor on a custom-global-actor-isolated class.
    /// If a constructor is found, the gate regressed and we'd hit the Nuke-style
    /// 'call to global actor X-isolated static method' compile failure once @_dbw_init_*
    /// extensions were emitted.
    /// </summary>
    public void TestGlobalActorIsolatedClass_NoPublicConstructor()
    {
        var ctors = typeof(GlobalActorIsolatedClass)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        // SwiftObject has its own constructors via inheritance; we want NO ctors declared
        // directly on the generated subclass.
        var declaredCtors = ctors.Where(c => c.DeclaringType == typeof(GlobalActorIsolatedClass)).ToArray();
        AssertEqual(0, declaredCtors.Length,
            $"Expected SWIFTBIND022 to skip all ctors on a custom-global-actor-isolated class; found {declaredCtors.Length}");
    }

    /// <summary>
    /// Custom-global-actor types declare conformance to the four compile-time marker protocols
    /// (Sendable / Copyable / Escapable / SendableMetatype) plus _Concurrency.Actor in their
    /// ABI conformance arrays, and they reference _Concurrency.UnownedSerialExecutor through
    /// the implicit unownedExecutor accessor. Before the typedb stubs landed, none of those
    /// names had TypeRecords, so MemberEmissionValidator dropped the non-init members
    /// (describe(), label, count) along with the constructor — leaving the class as a
    /// nominal-only shell and breaking every downstream call site.
    ///
    /// This regression guard asserts every declared non-init member survives emission. If
    /// the marker protocols revert to AnyType-fallback or _Concurrency.UnownedSerialExecutor
    /// disappears from _ConcurrencyDatabase.xml, the count below drops and this fails before
    /// the SWIFTBIND022 ctor-skip blame propagates downstream.
    /// </summary>
    public void TestGlobalActorIsolatedClass_NonInitMembersReachable()
    {
        var type = typeof(GlobalActorIsolatedClass);

        // describe() — instance method on a custom-global-actor-isolated class. The actor
        // hop forces async wrapping, so the method emits as DescribeAsync(CancellationToken).
        // What this guards: the method must exist regardless of how the actor isolation is
        // surfaced. If the marker conformance metadata regressed, MemberEmissionValidator
        // would drop the method along with the constructor and only the type shell would
        // remain.
        var describeAsync = type.GetMethod("DescribeAsync", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(describeAsync,
            "GlobalActorIsolatedClass.DescribeAsync must survive emission — its disappearance " +
            "indicates the marker-protocol conformance metadata is no longer resolving and the " +
            "method is being tombstoned by a CanEmitMethod failure.");

        // label / count — stored properties whose getters are implicitly actor-isolated.
        // The emitter projects them as plain `string Label` / `int Count` (the getter body
        // wraps the actor hop internally), so PascalCase property lookup is sufficient.
        var label = type.GetProperty("Label", BindingFlags.Public | BindingFlags.Instance);
        var count = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(label, "GlobalActorIsolatedClass.label getter must survive emission.");
        AssertNotNull(count, "GlobalActorIsolatedClass.count getter must survive emission.");
    }
}
