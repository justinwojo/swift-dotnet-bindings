// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Internal;

/// <summary>
/// End-to-end coverage for the Pattern 2 internal-type-reach emission gate.
/// Pairs with the Swift fixture at
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Internal/InternalTypeReach.swift</c>.
///
/// Several distinct gates participate in suppressing the fixture's internal
/// surface; the runtime absence assertions below are what matter, not which
/// gate fires for each member. The new <c>Pattern2InternalTypeReach</c>
/// emission-time gate is exercised end-to-end by three members on
/// <c>PublicHostWithInternalMembers</c>: <c>RegisterCarrier</c> (parameter
/// branch), <c>MakeCarrier</c> (return branch), and the
/// <c>subscript[carrier:]</c> indexer (subscript-index branch). The other
/// internal members (<c>InternalCarrier</c> the type, the free functions,
/// the internal-typed property) are caught by older gates that fire first;
/// see the fixture file's header comment for the per-member gate map.
///
/// Negative direction (gates do not over-strip): <c>DoesNotReachInternal</c>'s
/// public-only signature and <c>PublicWithInternalStored</c>'s public surface
/// must continue to emit and round-trip through the runtime exactly as before.
/// </summary>
public class InternalTypeReachTests : TestBase
{
    public InternalTypeReachTests(TestResults results) : base(results) { }

    private static readonly Assembly BindingsAssembly = typeof(DoesNotReachInternal).Assembly;

    private static Type? FindGeneratedType(string typeName)
    {
        foreach (var t in BindingsAssembly.GetTypes())
        {
            if (t.Name == typeName)
                return t;
        }
        return null;
    }

    #region Positive — gate fires; types/members must be absent

    public void TestInternalCarrierTypeIsAbsent()
    {
        // @usableFromInline internal struct — must not show up as a public
        // C# type. The pre-existing internal-type filter handles the type;
        // this assertion guards against a future regression that would re-emit
        // it (e.g. if IsModuleInternal classification drifted).
        var t = FindGeneratedType("InternalCarrier");
        AssertNull(t, "InternalCarrier should not be emitted to C# bindings");
        TestLogger.Info("InternalCarrier absent from binding surface (expected).");
    }

    public void TestInternalFreeFunctionsAreAbsent()
    {
        // makeCarrier + readCarrier are @usableFromInline internal free functions.
        // Caught by the pre-existing early gate at MemberValidationPipeline.cs:70
        // (`IsModuleInternal && parent is ModuleDecl`) BEFORE the new walker gate.
        // Kept as runtime absence assertion against an early-gate regression.
        var ns = typeof(DoesNotReachInternal).Namespace
            ?? throw new AssertionException("Cannot resolve binding namespace");
        var moduleType = BindingsAssembly.GetTypes()
            .FirstOrDefault(t => t.Namespace == ns && t.Name == "InternalTypeReach")
            ?? BindingsAssembly.GetTypes()
                .FirstOrDefault(t => t.Namespace == ns && t.Name.EndsWith("GlobalFunctions"));

        // Either way, neither MakeCarrier nor ReadCarrier should be findable
        // by name across any generated container in the binding namespace.
        foreach (var t in BindingsAssembly.GetTypes().Where(t => t.Namespace == ns))
        {
            var make = t.GetMethod("MakeCarrier", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            AssertNull(make, $"MakeCarrier should not be exposed (found on {t.FullName})");
            var read = t.GetMethod("ReadCarrier", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            AssertNull(read, $"ReadCarrier should not be exposed (found on {t.FullName})");
        }
        TestLogger.Info("makeCarrier/readCarrier absent from binding surface (gate suppressed).");
    }

    public void TestPublicWithInternalStoredConstructible()
    {
        // The public type still emits because the internal storage stays
        // an implementation detail behind the public init.
        using var holder = new PublicWithInternalStored(seed: 7);
        AssertNotNull(holder, "PublicWithInternalStored ctor returned null");
        AssertEqual(14, holder.SeedDoubled(), "seedDoubled should round-trip the internal stored seed");
        TestLogger.Info("PublicWithInternalStored constructed; SeedDoubled() = 14 as expected.");
    }

    public void TestPublicWithInternalStoredCarrierAccessorAbsent()
    {
        // The internal-typed stored property's accessor must not appear in C#.
        var prop = typeof(PublicWithInternalStored)
            .GetProperty("Carrier", BindingFlags.Public | BindingFlags.Instance);
        AssertNull(prop, "PublicWithInternalStored.Carrier accessor must stay suppressed");
        TestLogger.Info("PublicWithInternalStored.Carrier accessor absent (expected).");
    }

    public void TestPublicHostConstructibleAndPlainEmits()
    {
        // The host type itself is public — it must keep emitting along with its
        // public-only `Plain` method, even though it carries internal members
        // alongside. Catches the "host with at least one internal member"
        // over-stripping regression.
        using var host = new PublicHostWithInternalMembers();
        AssertNotNull(host, "PublicHostWithInternalMembers ctor returned null");
        AssertEqual(42, host.Plain(value: 21), "plain(21) doubled");
        TestLogger.Info("PublicHostWithInternalMembers public surface intact.");
    }

    public void TestPublicHostInternalMembersAbsent()
    {
        // All four @usableFromInline internal members on PublicHostWithInternalMembers
        // must be absent from the C# binding surface, but for different reasons:
        //   * RegisterCarrier / MakeCarrier — caught by the NEW walker gate via
        //     ValidateMethodEmission (parent is TypeDecl, no earlier IsModuleInternal
        //     filter for methods, signature reaches InternalCarrier).
        //   * subscript[carrier:] — caught by the NEW walker gate via
        //     ValidateSubscriptEmission, which SubscriptHandler.EmitSubscripts runs
        //     first thing (SubscriptHandler.cs:47).
        //   * FreshCarrier — caught by the pre-existing
        //     MemberEmissionValidator.CanEmitProperty IsModuleInternal filter.
        // The runtime assertions below treat all four uniformly; the gate-level
        // distinction is documented for readers diagnosing future regressions.
        var hostType = typeof(PublicHostWithInternalMembers);

        AssertNull(
            hostType.GetMethod("RegisterCarrier", BindingFlags.Public | BindingFlags.Instance),
            "RegisterCarrier must be suppressed (new walker gate, parameter branch)");
        AssertNull(
            hostType.GetMethod("MakeCarrier", BindingFlags.Public | BindingFlags.Instance),
            "MakeCarrier must be suppressed (new walker gate, return branch)");
        AssertNull(
            hostType.GetProperty("FreshCarrier", BindingFlags.Public | BindingFlags.Instance),
            "FreshCarrier must be suppressed (pre-existing CanEmitProperty filter)");

        // Subscript surfaces as a default indexer in C#; check both forms.
        var indexer = hostType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        AssertNull(indexer, "Subscript over InternalCarrier must be suppressed (new walker gate, subscript-index branch)");
        var subscriptByName = hostType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance);
        AssertNull(subscriptByName, "Subscript getter over InternalCarrier must be suppressed (new walker gate, subscript-index branch)");

        TestLogger.Info("PublicHostWithInternalMembers internal members absent.");
    }

    #endregion

    #region Negative — gate must not over-strip

    public void TestDoesNotReachInternalConstructible()
    {
        using var v = new DoesNotReachInternal(label: "alpha");
        AssertEqual("alpha", v.Label.ToString(), "Label round-trip");
        TestLogger.Info("DoesNotReachInternal ctor + Label getter pass.");
    }

    public void TestDoesNotReachInternalPlain()
    {
        using var v = new DoesNotReachInternal(label: "x");
        AssertEqual(42, v.Plain(value: 21), "plain(21) doubled");
        TestLogger.Info("DoesNotReachInternal.Plain emitted and called successfully.");
    }

    public void TestDoesNotReachInternalOptionalString()
    {
        using var v = new DoesNotReachInternal(label: "x");
        var some = v.OptionalString(value: 9);
        AssertNotNull(some, "Some-case should not be null");
        AssertEqual("9", some!.ToString(), "Some(9) -> \"9\"");

        var none = v.OptionalString(value: null);
        AssertNull(none, "None-case should be null");
        TestLogger.Info("DoesNotReachInternal.OptionalString round-trips Some/None.");
    }

    public void TestDoesNotReachInternalDescribeArray()
    {
        using var v = new DoesNotReachInternal(label: "x");
        var values = new int[] { 1, 2, 3 };
        var described = v.Describe(values: values);
        AssertNotNull(described, "Describe returned null");
        AssertEqual(3, described.Count, "Describe should map every element");
        AssertEqual("v=1", described[0].ToString(), "first element");
        AssertEqual("v=3", described[2].ToString(), "last element");
        TestLogger.Info("DoesNotReachInternal.Describe round-trips array map.");
    }

    public void TestDoesNotReachInternalSeedValue()
    {
        using var v = new DoesNotReachInternal(label: "x");
        AssertEqual(11, v.SeedValue, "SeedValue computed property");
        TestLogger.Info("DoesNotReachInternal.SeedValue accessor emitted.");
    }

    public void TestDoesNotReachInternalSubscript()
    {
        using var v = new DoesNotReachInternal(label: "x");
        var s = v[stringAt: 4];
        AssertEqual("[4]", s.ToString(), "Subscript should format index");
        TestLogger.Info("DoesNotReachInternal subscript emitted and called successfully.");
    }

    #endregion
}
