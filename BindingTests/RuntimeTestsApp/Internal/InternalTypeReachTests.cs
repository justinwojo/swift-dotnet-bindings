// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Internal;

/// <summary>
/// End-to-end coverage for the Pattern 2 internal-type-reach emission gate
/// AND the formally retained internal-receiver post-processing scope. Pairs
/// with the Swift fixture at
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Internal/InternalTypeReach.swift</c>.
///
/// Several distinct gates participate in suppressing the fixture's internal
/// surface; the runtime absence assertions below are what matter, not which
/// gate fires for each member. The <c>Pattern2InternalTypeReach</c>
/// emission-time gate is exercised end-to-end by three members on
/// <c>PublicHostWithInternalMembers</c> — <c>RegisterCarrier</c> (parameter
/// branch), <c>MakeCarrier</c> (return branch), <c>subscript[carrier:]</c>
/// (subscript-index branch) — and by the inits on <c>InternalCarrier</c> and
/// <c>InternalHolder</c>: the walker walks <c>CSSignature</c>, which carries
/// an init's implicit Self-return, so an init returning an internal type is
/// caught at emission time exactly like a method whose declared return reaches
/// an internal type. The body-reference shape (<c>InternalHolder.describe</c>)
/// is formally retained as post-processing scope: in real validation libraries
/// the <c>SwiftWrapperPostProcessor</c> Pattern 2 (B) body-reference scrub
/// strips the broken wrapper, and <c>CSharpWrapperCoGater</c> removes the
/// matching C# member (preserving interface-implementation members so types
/// conforming to public protocols still compile — see CryptoSwift
/// <c>BlockEncryptor : Cryptor</c>). End-to-end coverage of that path lives in
/// <c>nuke validate</c> against the four real libraries, not in BindingTests:
/// the wrapper-compile + post-processor pipeline does not run cleanly here,
/// so the fixture verifies what is reachable — the construction barrier (no
/// public init) renders any C# member that survives in source unreachable.
/// The free functions and the internal-typed property are caught by older
/// gates that fire first. The shell type itself is intentionally emitted (no
/// type-level filter exists for <c>@usableFromInline internal</c> — the type
/// can still be referenced through metadata accessors). See the fixture file's
/// header comment for the per-member gate map.
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

    public void TestInternalCarrierTypeIsUncreatable()
    {
        // @usableFromInline internal struct. The generator emits the type as
        // a shell (it can still be reached as a generic argument or via metadata
        // accessor for cross-module references) but every consumer-facing entry
        // point — most importantly the public init — is suppressed.
        //
        // The init's emitted CSSignature carries the implicit Self-return type
        // (InternalCarrier itself, which is in the module's InternalTypeNames
        // set), so Pattern2InternalTypeReach matches via the return-type branch
        // and the init is skipped at emission time. No wrapper is emitted and
        // no post-processor strip is needed for this case.
        //
        // The invariant we lock in here is "no consumer can construct an
        // InternalCarrier from C#": all public constructors must be absent.
        // If a future regression re-exposed `init`, the type would become
        // reachable from public code paths even though its members are gated.
        var t = FindGeneratedType("InternalCarrier");
        AssertNotNull(t, "InternalCarrier shell type expected (used as metadata anchor)");
        var publicCtors = t!
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        AssertEqual(0, publicCtors.Length,
            "InternalCarrier must not expose any public constructor — walker gate must suppress init");
        TestLogger.Info("InternalCarrier emitted as shell type with no public constructor (expected).");
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
        AssertEqual(14, holder.GetSeedDoubled(), "seedDoubled should round-trip the internal stored seed");
        TestLogger.Info("PublicWithInternalStored constructed; GetSeedDoubled() = 14 as expected.");
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

    public void TestInternalHolderPublicConstructorAbsent()
    {
        // InternalHolder is @usableFromInline internal but declares a
        // public func describe(). Two suppression mechanisms are in play:
        //
        //   * init: walker-suppressed via the same implicit Self-return path
        //     as InternalCarrier.init (see TestInternalCarrierTypeIsUncreatable).
        //
        //   * describe(): declared signature is purely public (() -> String),
        //     so the walker cannot catch it at emission time. The runtime
        //     suppression in real libraries is the post-processor + co-gater
        //     pair — Pattern 2 (B) body-reference scrub strips the broken
        //     @_cdecl wrapper, and CSharpWrapperCoGater removes the matching
        //     C# member when no public protocol requires it. (Where a protocol
        //     does require it — CryptoSwift BlockEncryptor : Cryptor — the
        //     co-gater's BuildTypeProtectedMembers exemption keeps the C#
        //     member to satisfy CS0535, which is why an emission-time receiver
        //     gate isn't safe.) That end-to-end path is validated by
        //     `nuke validate`, not here: BindingTests' wrapper-compile +
        //     post-processor pipeline does not run cleanly, so the C# member
        //     stays in source — but is unreachable because there is no
        //     instance to call it on (the construction barrier below).
        //
        // Regression signal: if a public constructor ever appears on
        // InternalHolder, the construction barrier has fallen and any
        // surviving describe-equivalent in the C# source becomes callable.
        var holderType = FindGeneratedType("InternalHolder");
        AssertNotNull(holderType, "InternalHolder shell type expected (used as metadata anchor)");

        var publicCtors = holderType!
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        AssertEqual(0, publicCtors.Length,
            "InternalHolder must not expose any public constructor — walker gate must suppress init");
        TestLogger.Info("InternalHolder emitted as shell with no public constructor (walker gate suppressed init).");
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
