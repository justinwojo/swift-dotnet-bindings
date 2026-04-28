// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// End-to-end coverage for custom-global-actor-isolated constructors and members.
///
/// A class annotated with @&lt;Actor&gt; (e.g., <c>@BindingsTestGlobalActor</c>) inherits
/// global-actor isolation on every member, including inits. Swift 6 rejects a
/// synchronous @_cdecl wrapper that calls such an init — <c>&lt;Actor&gt;.shared.assumeIsolated</c>
/// propagates *instance*-actor isolation, which Swift treats as a different domain
/// from @&lt;Actor&gt; *global-actor* isolation. The binding generator therefore emits the
/// constructor directly in C# (with the SB0001 [Obsolete] safety warning) and routes
/// it through CallConvSwift to the Swift-native init. The call works at runtime when
/// the caller is already on the actor's executor (the documented Swift contract).
///
/// The fixture's <c>BindingsTestGlobalActor</c> delegates its serial executor to
/// <c>MainActor</c>, so the runtime tests — which run on the main thread (iOS
/// UIApplication's entry point) — satisfy that contract for free. A real-world global
/// actor with its own queue would fatal-error if constructed off-actor, which is the
/// documented constraint on consumers. SWIFTBIND022 keeps its wholesale skip for the
/// fallback case where the actor TypeDecl isn't reachable in the bound module.
/// </summary>
public class CustomGlobalActorTests : TestBase
{
    public CustomGlobalActorTests(TestResults results) : base(results) { }

    /// <summary>
    /// The class type itself must appear in the bindings — the marker-protocol metadata
    /// gap historically dropped non-init members alongside the constructor skip. This
    /// guards against regressions in marker-protocol conformance resolution.
    /// </summary>
    public void TestGlobalActorIsolatedClass_TypeIsGenerated()
    {
        var type = typeof(GlobalActorIsolatedClass);
        AssertNotNull(type, "GlobalActorIsolatedClass should be present in generated bindings");
    }

    /// <summary>
    /// The constructor must reach C# even though the @_cdecl wrapper can't be emitted
    /// for a custom-global-actor-isolated init. The SWIFTBIND022 narrowing keeps the
    /// constructor in the C# binding (calling Swift's native init via CallConvSwift)
    /// when the actor TypeDecl is resolvable; only when the actor itself can't be
    /// found does the SWIFTBIND022 wholesale skip fire.
    /// </summary>
    public void TestGlobalActorIsolatedClass_ConstructorIsEmitted()
    {
        var declaredCtors = typeof(GlobalActorIsolatedClass)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.DeclaringType == typeof(GlobalActorIsolatedClass))
            .ToArray();

        AssertTrue(declaredCtors.Length > 0,
            "Expected the SWIFTBIND022 narrowing to keep the constructor on the custom-" +
            $"global-actor-isolated class once the actor TypeDecl is reachable; found {declaredCtors.Length}.");
    }

    /// <summary>
    /// Construct via the no-arg path (Swift defaults supply both parameters) and read
    /// back the actor-isolated stored properties. The C# binding routes through
    /// CallConvSwift to the Swift-native init; the call lands on the actor's executor
    /// because the test runs on the main thread and the fixture's actor delegates its
    /// executor to MainActor. Without the SWIFTBIND022 narrowing, the entire constructor
    /// would be tombstoned by the wholesale skip and the test would fail to compile.
    /// </summary>
    public void TestGlobalActorIsolatedClass_DefaultConstruction_Succeeds()
    {
        var instance = new GlobalActorIsolatedClass();
        AssertNotNull(instance, "Default-parameter ctor must succeed when caller is on the actor's executor.");

        AssertEqual("default", instance.Label.ToString(), "Default label round-trip via direct Swift init.");
        AssertEqual(0, instance.Count, "Default count round-trip via direct Swift init.");
    }

    /// <summary>
    /// Construct via the explicit-args path and verify a non-default value survives the
    /// CallConvSwift call. Distinguishes correctness from any "default value" coincidence.
    /// </summary>
    public void TestGlobalActorIsolatedClass_ExplicitConstruction_Succeeds()
    {
        var instance = new GlobalActorIsolatedClass("custom", 42);
        AssertNotNull(instance, "Explicit-arg ctor must succeed when caller is on the actor's executor.");

        AssertEqual("custom", instance.Label.ToString(), "Explicit label round-trip.");
        AssertEqual(42, instance.Count, "Explicit count round-trip.");
    }

    /// <summary>
    /// Throwing init on a custom-global-actor-isolated class. The CallConvSwift call
    /// returns the SwiftError out-parameter the same way every other throwing init
    /// does — the marshalling code path is identical regardless of actor isolation.
    /// </summary>
    public void TestGlobalActorIsolatedThrowingClass_ThrowingInit_PropagatesError()
    {
        // Happy path — does not throw, value reaches storage.
        var ok = new GlobalActorIsolatedThrowingClass(5, false);
        AssertEqual(5, ok.Value, "Non-throwing path stores the explicit value.");

        // Defaults supply both args (negative=false) — exercises the default-parameter
        // path on a throwing init isolated to a custom global actor.
        var defaulted = new GlobalActorIsolatedThrowingClass();
        AssertEqual(0, defaulted.Value, "Throwing default-parameter ctor must reach the actor and store defaults.");

        // Failure path — the throw must surface as a managed exception.
        bool caught = false;
        try
        {
            _ = new GlobalActorIsolatedThrowingClass(-1, true);
        }
        catch (Exception ex) when (ex.Message.Contains("negative value rejected"))
        {
            caught = true;
        }
        AssertTrue(caught,
            "Throwing init's NSError must propagate to the C# caller via the standard " +
            "SwiftError marshalling path, even when the parent type is custom-global-actor-isolated.");
    }

    /// <summary>
    /// Custom-global-actor types declare conformance to the four compile-time marker
    /// protocols (Sendable / Copyable / Escapable / SendableMetatype) plus
    /// _Concurrency.Actor in their ABI conformance arrays, and they reference
    /// _Concurrency.UnownedSerialExecutor through the implicit unownedExecutor accessor.
    /// Before the typedb stubs landed, none of those names had TypeRecords, so
    /// MemberEmissionValidator dropped the non-init members (describe(), label, count)
    /// along with the constructor — leaving the class as a nominal-only shell.
    ///
    /// This regression guard asserts every declared non-init member survives emission.
    /// If the marker protocols revert to AnyType-fallback or _Concurrency.UnownedSerialExecutor
    /// disappears from _ConcurrencyDatabase.xml, the count below drops and this fails
    /// before the constructor-skip blame propagates downstream.
    /// </summary>
    public void TestGlobalActorIsolatedClass_NonInitMembersReachable()
    {
        var type = typeof(GlobalActorIsolatedClass);

        // describe() — instance method on a custom-global-actor-isolated class. The
        // actor isolation forces async wrapping, so the method emits as
        // DescribeAsync(CancellationToken). What this guards: the method must exist
        // regardless of how the actor isolation is surfaced. If the marker conformance
        // metadata regressed, MemberEmissionValidator would drop the method along with
        // the constructor and only the type shell would remain.
        var describeAsync = type.GetMethod("DescribeAsync", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(describeAsync,
            "GlobalActorIsolatedClass.DescribeAsync must survive emission — its disappearance " +
            "indicates the marker-protocol conformance metadata is no longer resolving and the " +
            "method is being tombstoned by a CanEmitMethod failure.");

        // label / count — stored properties whose getters are implicitly actor-isolated.
        // The emitter projects them as plain `string Label` / `int Count` (the getter
        // body is a direct memory read, not an actor hop), so PascalCase property
        // lookup is sufficient.
        var label = type.GetProperty("Label", BindingFlags.Public | BindingFlags.Instance);
        var count = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(label, "GlobalActorIsolatedClass.label getter must survive emission.");
        AssertNotNull(count, "GlobalActorIsolatedClass.count getter must survive emission.");
    }
}
