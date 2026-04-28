// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// End-to-end coverage for custom-global-actor-isolated types.
///
/// A class annotated with @&lt;Actor&gt; (e.g., <c>@BindingsTestGlobalActor</c>) inherits
/// global-actor isolation on every member, including inits. Swift 6 rejects the
/// synchronous @_cdecl wrapper that the binding generator would otherwise emit for
/// such a constructor — <c>&lt;Actor&gt;.shared.assumeIsolated</c> propagates *instance*-actor
/// isolation, which Swift treats as a different domain from @&lt;Actor&gt; *global-actor*
/// isolation, so neither wrapper form lands on the actor's executor at runtime.
/// SWIFTBIND022 therefore wholesale-skips every constructor on a custom-global-actor-
/// isolated parent type and emits the SB0022 diagnostic; consumers must construct
/// instances inside Swift (factory functions or Swift entry points) and hand the
/// instance back to C# for use.
///
/// Non-init members (methods, stored-property getters) are unaffected and must still
/// reach C#. The tests below pin both halves of that contract: the type itself and
/// its non-init members survive emission, while the constructors deliberately don't.
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
    /// SWIFTBIND022 wholesale-skips every constructor on a custom-global-actor-isolated
    /// parent. The C# binding therefore must NOT declare any public constructors of its
    /// own — only the implicit/default ones inherited from the SwiftObject base. This
    /// guards against accidental regressions that would re-enable a broken CallConvSwift
    /// path (the generator previously narrowed the skip on the assumption a direct
    /// CallConvSwift call would land on the actor's executor; on device that turned out
    /// to crash because the implicit metatype/self register layout doesn't survive the
    /// NativeAOT thunk).
    /// </summary>
    public void TestGlobalActorIsolatedClass_ConstructorIsSkipped()
    {
        var declaredCtors = typeof(GlobalActorIsolatedClass)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.DeclaringType == typeof(GlobalActorIsolatedClass))
            .ToArray();

        AssertEqual(0, declaredCtors.Length,
            "SWIFTBIND022 wholesale-skips constructors on custom-global-actor-isolated " +
            $"types; expected zero declared ctors on GlobalActorIsolatedClass, found {declaredCtors.Length}.");
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
