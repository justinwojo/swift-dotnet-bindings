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
}
