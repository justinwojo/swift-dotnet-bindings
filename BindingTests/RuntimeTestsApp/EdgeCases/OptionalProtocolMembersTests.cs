// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Runtime coverage for the fix where <c>@objc optional</c> protocol members
/// were incorrectly emitted as mandatory interface requirements.
///
/// The Swift fixture <c>OptionalCallbackDelegate</c> declares one mandatory
/// method, two `@objc optional` methods (one void, one returning Int32), and
/// one `@objc optional` get-only property. Pre-fix, the C# emitter lowered
/// every member as a mandatory interface requirement, forcing consumers to
/// write CS0535 stubs for every optional. After the fix, optionals must lower
/// to Default Interface Methods (DIMs) with a no-op / `default` body, so a
/// consumer can satisfy the interface by implementing only the mandatory
/// member.
///
/// The compile-time assertion is the strongest one: the
/// <see cref="MinimalCSharpConformer"/> nested below implements ONLY
/// <see cref="IOptionalCallbackDelegate.DidFireRequired"/>; any regression
/// that re-promotes the optional members to required would fail this file's
/// compilation, surfacing as a Layer-A `nuke binding-tests --compile-only`
/// failure rather than a runtime assertion.
/// </summary>
public class OptionalProtocolMembersTests : TestBase
{
    public OptionalProtocolMembersTests(TestResults results) : base(results) { }

    /// <summary>
    /// Minimal pure-C# conformer to <see cref="IOptionalCallbackDelegate"/>.
    /// Implements only the mandatory <c>DidFireRequired</c> method; relies on
    /// the optional DIMs for everything else. If the generator regresses and
    /// emits the optional members as required, this class fails to compile.
    /// </summary>
    private sealed class MinimalCSharpConformer : IOptionalCallbackDelegate
    {
        public int LastRequiredTag = -1;

        public void DidFireRequired(int tag)
        {
            LastRequiredTag = tag;
        }
    }

    public void TestMinimalCSharpConformerCompiles()
    {
        // Compilation alone proves the optional members lowered to DIMs.
        var conformer = new MinimalCSharpConformer();
        conformer.DidFireRequired(7);
        AssertEqual(7, conformer.LastRequiredTag, "Mandatory method dispatched");
    }

    public void TestOptionalVoidDIMIsNoOp()
    {
        // Calling the optional void method via the DIM should silently no-op.
        IOptionalCallbackDelegate iface = new MinimalCSharpConformer();
        iface.DidFireOptionalVoid(42); // must not throw
        AssertTrue(true, "Optional void DIM ran without throwing");
    }

    public void TestOptionalReturningDIMReturnsDefault()
    {
        // Calling the optional value-returning method via the DIM should yield
        // `default(int)` == 0. Pre-fix, this method either didn't exist on the
        // interface (because the consumer hadn't implemented it) or threw a
        // NotSupportedException — neither of which matches Swift semantics.
        IOptionalCallbackDelegate iface = new MinimalCSharpConformer();
        var observed = iface.DidReportProgress(99);
        AssertEqual(0, observed, "Optional returning DIM yields default(int32)");
    }

    public void TestOptionalGetterDIMReturnsDefault()
    {
        IOptionalCallbackDelegate iface = new MinimalCSharpConformer();
        var observed = iface.OptionalLabel;
        AssertEqual(0, observed, "Optional getter DIM yields default(int32)");
    }

    public void TestOptionalAsyncReturningDIMReturnsTaskFromResult()
    {
        // The DIM for `@objc optional func fetchValue() async -> Int32` must be
        // `=> Task.FromResult<int>(default!);`. The naive `=> default!;` form
        // returns null Task and any consumer that `await`s it NREs.
        IOptionalCallbackDelegate iface = new MinimalCSharpConformer();
        var task = iface.FetchValueAsync();
        AssertNotNull(task, "Optional async DIM must return a non-null Task");
        // Synchronously wait — Task.FromResult is already completed.
        var observed = task!.GetAwaiter().GetResult();
        AssertEqual(0, observed, "Optional async DIM yields default(int32)");
    }

    public void TestOptionalMembersAreDIMsViaReflection()
    {
        // Reflection-level guard: optional members must NOT carry the
        // `abstract` modifier (interface members default to abstract; DIMs
        // explicitly opt out by carrying a body). A regression that drops
        // the body would silently re-introduce the consumer-stub burden;
        // catch it with a structural check.
        var ifaceType = typeof(IOptionalCallbackDelegate);

        var requiredMethod = ifaceType.GetMethod(
            "DidFireRequired",
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(requiredMethod, "Mandatory method exists on interface");
        AssertTrue(requiredMethod!.IsAbstract,
            "Mandatory method stays abstract — consumers must implement it");

        var optionalVoid = ifaceType.GetMethod(
            "DidFireOptionalVoid",
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(optionalVoid, "Optional void method on interface");
        AssertTrue(!optionalVoid!.IsAbstract,
            "Optional void method must be a DIM (not abstract)");

        var optionalReturning = ifaceType.GetMethod(
            "DidReportProgress",
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(optionalReturning, "Optional returning method on interface");
        AssertTrue(!optionalReturning!.IsAbstract,
            "Optional returning method must be a DIM (not abstract)");

        var optionalProperty = ifaceType.GetProperty(
            "OptionalLabel",
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(optionalProperty, "Optional property on interface");
        var getter = optionalProperty!.GetMethod;
        AssertNotNull(getter, "Optional property has getter");
        AssertTrue(!getter!.IsAbstract,
            "Optional property getter must be a DIM (not abstract)");
    }
}
