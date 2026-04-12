// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Runtime coverage for fix #10 (commit <c>26f764f1</c>): nested-type /
/// method name collision detection. Before fix #10 the generator emitted a
/// C# type with BOTH a nested type and a method whose PascalCase name was
/// identical, tripping CS0102 at compile time. Fix #10 extends the
/// property/method rename collision set to include nested type names so
/// one of them is renamed and the emitted C# compiles.
///
/// The directly-observable consequence of fix #10 is "the binding compiles"
/// — if this test file compiles at all, fix #10 has done its job. The
/// runtime calls below prove both the nested type and the method are also
/// reachable through whatever rename the generator applied. We call them
/// via the free-function helpers defined in the Swift fixture so the test
/// does not have to know or hardcode which member was renamed.
/// </summary>
public class NestedTypeMethodCollisionTests : TestBase
{
    public NestedTypeMethodCollisionTests(TestResults results) : base(results) { }

    /// <summary>
    /// Exercises the nested type: constructs <c>Navigator.Route</c> via the
    /// Swift factory helper and verifies the returned value carries the
    /// expected destination string. Fix #10 has to emit the nested type's
    /// initializer without dropping it on the floor due to the name
    /// collision, or this call fails to compile.
    /// </summary>
    public void TestNavigatorRouteNestedTypeRoundTrip()
    {
        var route = TestLibFunctions.MakeNavigatorRoute("home");
        var destination = route.Destination.ToString();
        TestLogger.Info($"Navigator.Route.destination = \"{destination}\"");
        AssertEqual("home", destination,
            "Navigator.Route.destination must round-trip the constructor argument. " +
            "Fix #10 must preserve the nested type's initializer even after the " +
            "collision rename is applied.");
    }

    /// <summary>
    /// Exercises the colliding method: calls <c>Navigator.route(to:)</c>
    /// via a Swift free-function helper that takes a Navigator by value and
    /// a destination string. Proves the method was still emitted (renamed
    /// or not) and produces the expected output. If the method had been
    /// dropped entirely to "resolve" the collision, this call would fail to
    /// compile.
    /// </summary>
    public void TestNavigatorRouteMethodCallable()
    {
        var navigator = new Navigator("downtown");
        var result = TestLibFunctions.InvokeNavigatorRoute(navigator, "airport").ToString();
        TestLogger.Info($"invokeNavigatorRoute(downtown -> airport) = \"{result}\"");
        AssertEqual("downtown -> airport", result,
            "Navigator.route(to:) must preserve origin + destination across the " +
            "collision rename. If the method was dropped or its body lost during " +
            "rename, this assertion fails.");
    }

    /// <summary>
    /// Reflection sanity check: both a member named <c>Route</c>-ish and a
    /// method/nested-type sibling exist on <c>Navigator</c>. This is a
    /// belt-and-braces assertion that the collision was handled by rename
    /// rather than by dropping one of the two colliding members. We don't
    /// hardcode the post-rename name because the generator's rename policy
    /// may evolve — the invariant is "both members survive in some form."
    /// </summary>
    public void TestNavigatorBothMembersPresentAfterCollisionRename()
    {
        var navigatorType = typeof(Navigator);

        var nestedTypes = navigatorType.GetNestedTypes(BindingFlags.Public)
            .Where(t => t.Name.Contains("Route"))
            .ToArray();
        var routeMethods = navigatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.Contains("Route") || m.Name.Contains("route"))
            .ToArray();

        TestLogger.Info($"Navigator nested types containing 'Route': " +
            $"[{string.Join(", ", nestedTypes.Select(t => t.Name))}]");
        TestLogger.Info($"Navigator methods containing 'Route' or 'route': " +
            $"[{string.Join(", ", routeMethods.Select(m => m.Name))}]");

        AssertTrue(nestedTypes.Length > 0,
            "Navigator must have at least one nested type whose name contains 'Route'. " +
            "Fix #10 must rename rather than drop the nested type when it collides with " +
            "a method of the same PascalCase name.");
        AssertTrue(routeMethods.Length > 0,
            "Navigator must have at least one instance method whose name contains 'Route' " +
            "or 'route'. Fix #10 must rename rather than drop the method when it collides " +
            "with a nested type of the same PascalCase name.");
    }
}
