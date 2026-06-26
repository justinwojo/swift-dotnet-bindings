// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// End-to-end runtime coverage for the availability runtime guard — the fix for the
/// StoreKit2 TestFlight crash where an iOS-26-only member, reached on iOS 17/18,
/// called a weak-linked Swift symbol that resolved to NULL and SIGSEGV'd at pc=0
/// (uncatchable by C# try/catch).
///
/// The generator now emits a managed OS-version guard at the top of every member
/// whose own availability floor exceeds its enclosing type's, throwing
/// <see cref="PlatformNotSupportedException"/> BEFORE the P/Invoke. We cannot
/// reproduce a genuinely null symbol with our own test-lib types (only external
/// Apple-SDK symbols are weak-imported), but the guard fires purely on the running
/// OS version — so a future floor (iOS 99) makes it fire on any real simulator or
/// device, and a currently-satisfied floor proves it does not over-fire.
///
/// Members are invoked via reflection rather than direct calls: that sidesteps the
/// CA1416 diagnostic the iOS-99 <c>[SupportedOSPlatform]</c> attribute would raise
/// at this iOS-15-baseline call site, and it asserts behavior independent of the
/// generator's method-naming strategy. Reflection wraps a thrown exception in
/// <see cref="TargetInvocationException"/>, so the guard's exception surfaces as
/// the InnerException.
/// </summary>
public class RuntimeAvailabilityGuardTests : TestBase
{
    public RuntimeAvailabilityGuardTests(TestResults results) : base(results) { }

    private static MethodInfo FindMethod(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type,
        string nameFragment,
        bool isStatic)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethods(flags)
            .First(m => m.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)
                        && m.GetParameters().Length == 0);
    }

    private static ConstructorInfo FindParameterlessCtor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        Type type)
    {
        return type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 0);
    }

    private static MethodInfo FindBinaryOperator(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        Type type,
        string opMethodName)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == opMethodName && m.GetParameters().Length == 2);
    }

    /// <summary>
    /// An ungated member must run normally — no guard is emitted for it (it carries
    /// no availability annotations), so the call path is unchanged.
    /// </summary>
    public void TestBaselineMember_NoGuard_Runs()
    {
        using var carrier = new RuntimeGuardCarrier();
        var method = FindMethod(typeof(RuntimeGuardCarrier), "Baseline", isStatic: false);
        var result = (int)method.Invoke(carrier, null)!;
        TestLogger.Info($"RuntimeGuardCarrier.baseline() = {result}");
        AssertEqual(1, result, "Ungated RuntimeGuardCarrier.baseline() must return 1.");
    }

    /// <summary>
    /// A member gated to a floor every current OS already satisfies carries a runtime
    /// guard, but the guard must evaluate false and the call must succeed — proving
    /// the guard does not over-fire and break legitimately-available members.
    /// </summary>
    public void TestCurrentlyAvailableMember_GuardDoesNotOverFire()
    {
        using var carrier = new RuntimeGuardCarrier();
        var method = FindMethod(typeof(RuntimeGuardCarrier), "CurrentlyAvailable", isStatic: false);
        var result = (int)method.Invoke(carrier, null)!;
        TestLogger.Info($"RuntimeGuardCarrier.currentlyAvailable() = {result}");
        AssertEqual(7, result,
            "RuntimeGuardCarrier.currentlyAvailable() is gated to a floor the current OS " +
            "satisfies; its runtime guard must evaluate false and the call must return 7. " +
            "If this throws PlatformNotSupportedException, the guard is over-firing.");
    }

    /// <summary>
    /// A future-gated instance member must have its runtime guard fire and throw
    /// PlatformNotSupportedException before the P/Invoke — converting what would be an
    /// uncatchable native crash (against a real weak-linked Apple symbol) into a
    /// catchable managed exception.
    /// </summary>
    public void TestFutureInstanceMember_GuardThrows()
    {
        using var carrier = new RuntimeGuardCarrier();
        var method = FindMethod(typeof(RuntimeGuardCarrier), "FutureOnlyInstance", isStatic: false);

        // Sanity: the member must carry the iOS 99 floor as a compile-time attribute too —
        // the guard and the [SupportedOSPlatform] attribute are emitted as a matched pair.
        var attrs = method.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"FutureOnlyInstance SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");
        AssertTrue(attrs.Any(a => string.Equals(a.PlatformName, "ios99.0", StringComparison.OrdinalIgnoreCase)),
            "FutureOnlyInstance must carry SupportedOSPlatform(\"ios99.0\"); the runtime guard " +
            "is meant to mirror exactly the members carrying that compile-time attribute.");

        AssertGuardThrows(() => method.Invoke(carrier, null), "RuntimeGuardCarrier.FutureOnlyInstance");
    }

    /// <summary>
    /// The static counterpart is the highest-risk slice: a static member is reachable
    /// without first obtaining an instance, so no metadata resolution stands between
    /// the caller and the weak-linked symbol. The runtime guard is the only protection,
    /// and it must fire.
    /// </summary>
    public void TestFutureStaticMember_GuardThrows()
    {
        var method = FindMethod(typeof(RuntimeGuardCarrier), "FutureOnlyStatic", isStatic: true);
        AssertGuardThrows(() => method.Invoke(null, null), "RuntimeGuardCarrier.FutureOnlyStatic");
    }

    /// <summary>
    /// A constructor of a future-gated TYPE inherits the type's floor even though it
    /// declares none of its own. This is the case the pre-fix emitter deduped to nothing
    /// (member floor == parent floor → no guard), leaving the type-gated constructor able
    /// to reach the weak-linked allocation symbol and crash. The guard must key on the
    /// merged effective floor, so the constructor must throw before its P/Invoke.
    /// </summary>
    public void TestGatedTypeConstructor_GuardThrows()
    {
        var ctor = FindParameterlessCtor(typeof(FutureGatedType));
        AssertGuardThrows(() => ctor.Invoke(null), "FutureGatedType..ctor");
    }

    /// <summary>
    /// A static member of a future-gated type — reachable with no instance and no metadata
    /// access in between — inherits the type's floor and must guard, even though it declares
    /// no floor of its own.
    /// </summary>
    public void TestGatedTypeStaticMember_GuardThrows()
    {
        var method = FindMethod(typeof(FutureGatedType), "StaticValue", isStatic: true);
        AssertGuardThrows(() => method.Invoke(null, null), "FutureGatedType.StaticValue");
    }

    /// <summary>
    /// A type gated to a floor the current OS already satisfies: every member inherits the
    /// satisfied floor, so the merged-floor guard must evaluate false and the constructor,
    /// instance member, and static member must all succeed — proving the inherited-floor
    /// guard does not over-fire and break legitimately-available type-gated members.
    /// </summary>
    public void TestCurrentlyGatedType_InheritedFloor_DoesNotOverFire()
    {
        var ctor = FindParameterlessCtor(typeof(CurrentlyGatedType));
        object instance;
        try
        {
            instance = ctor.Invoke(null)!;
        }
        catch (TargetInvocationException tie)
        {
            throw new AssertionException(
                "CurrentlyGatedType is gated to a floor the current OS satisfies; its constructor's " +
                "inherited-floor guard must evaluate false and construct normally. It threw " +
                $"{tie.InnerException?.GetType().Name} instead — the merged-floor guard is over-firing.");
        }

        try
        {
            var instanceMethod = FindMethod(typeof(CurrentlyGatedType), "InstanceValue", isStatic: false);
            var instanceResult = (int)instanceMethod.Invoke(instance, null)!;
            TestLogger.Info($"CurrentlyGatedType.instanceValue() = {instanceResult}");
            AssertEqual(7, instanceResult,
                "CurrentlyGatedType.instanceValue() inherits a satisfied floor; its guard must not " +
                "over-fire and it must return 7.");

            var staticMethod = FindMethod(typeof(CurrentlyGatedType), "StaticValue", isStatic: true);
            var staticResult = (int)staticMethod.Invoke(null, null)!;
            TestLogger.Info($"CurrentlyGatedType.staticValue() = {staticResult}");
            AssertEqual(11, staticResult,
                "CurrentlyGatedType.staticValue() inherits a satisfied floor; its guard must not " +
                "over-fire and it must return 11.");
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// An operator gated stricter than its (ungated) parent must carry a runtime guard that
    /// fires — the operator's <c>@_cdecl</c> symbol is weak-linked just like any other member,
    /// so reaching <c>a + b</c> on an older OS would otherwise SIGSEGV. The operands are built
    /// from the ungated constructor (which must remain callable); only the operator is gated.
    /// </summary>
    public void TestGatedOperator_GuardThrows()
    {
        var a = new GuardedOperand(5);
        var b = new GuardedOperand(9);
        var op = FindBinaryOperator(typeof(GuardedOperand), "op_Addition");
        AssertGuardThrows(() => op.Invoke(null, new object[] { a, b }), "GuardedOperand.operator +");
    }

    /// <summary>
    /// A generic non-frozen struct routes its <c>_payloadSize</c> through an EAGER
    /// static-field initializer (the generic path cannot use the non-generic lazy
    /// property — <c>SwiftObjectHelper&lt;Foo&lt;T&gt;&gt;</c> in a field initializer crashes
    /// Mono's generic sharing). When the type is availability-gated, that eager
    /// initializer is wrapped so its native metadata accessor is skipped below the
    /// floor; the registration itself stays eager. This type is gated to a floor the
    /// current OS satisfies, so the eager arm runs in full — proving the gated-generic
    /// restructuring did not regress the eager registration path on Mono: the closed
    /// <c>CurrentlyGatedBuffer&lt;int&gt;</c> must still round-trip a value.
    /// </summary>
    public void TestCurrentlyGatedGenericStruct_RoundTrips()
    {
        using var buffer = Functions.MakeCurrentlyGatedInt32Buffer(21);
        var value = Functions.CurrentlyGatedInt32BufferValue(buffer);
        TestLogger.Info($"CurrentlyGatedBuffer<Int32>(21).value = {value}");
        AssertEqual(21, value,
            "CurrentlyGatedBuffer<Int32> is gated to a floor the current OS satisfies; its " +
            "eager gated-generic _payloadSize registration must still run and the value must " +
            "round-trip to 21. A wrong value or crash means the gated-generic eager arm regressed.");
    }

    /// <summary>
    /// The EnumHandler sibling of the struct above: a generic payload enum flows its
    /// <c>_payloadSize</c> through the same eager helper-PInvoke + RegisterAndGetSize
    /// path, gated identically. A satisfied floor must round-trip a payload — proving the
    /// enum arm of the gated-generic restructuring is intact.
    /// </summary>
    public void TestCurrentlyGatedGenericEnum_RoundTrips()
    {
        using var box = Functions.MakeCurrentlyGatedInt32PayloadBox(34);
        var value = Functions.CurrentlyGatedInt32PayloadBoxValue(box);
        TestLogger.Info($"CurrentlyGatedPayloadBox<Int32>.filled(34) = {value}");
        AssertEqual(34, value,
            "CurrentlyGatedPayloadBox<Int32> is gated to a satisfied floor; its eager gated-generic " +
            "_payloadSize registration must still run and the payload must round-trip to 34.");
    }

    /// <summary>
    /// The crux of the gated-generic fix: the eager <c>_payloadSize</c> initializer runs in
    /// the static constructor on the FIRST reference to the closed type — BEFORE any member
    /// guard. For a future-gated generic, that cctor would otherwise resolve metadata at a
    /// floor no current OS satisfies. Invoking a static member of <c>FutureGatedBuffer&lt;int&gt;</c>
    /// forces that cctor to run on this OS (below the iOS 99 floor): with the fix the initializer
    /// short-circuits its native accessor, so the cctor completes, and the static member's own
    /// guard then throws a catchable <see cref="PlatformNotSupportedException"/> instead of the
    /// process crashing.
    ///
    /// As with the non-generic future-gated tests, this cannot reproduce a genuinely-null symbol
    /// — our own metadata accessor resolves even below the floor — so the discriminating proof
    /// that the native call is skipped below the floor is the emitter unit test. This asserts the
    /// end-to-end runtime behavior: the gated-generic cctor runs below the floor without crashing
    /// and the member guard converts the unavailable call into a managed exception. Reflection
    /// sidesteps the CA1416 the iOS-99 <c>[SupportedOSPlatform]</c> would raise here.
    /// </summary>
    public void TestFutureGatedGenericStatic_CctorRunsBelowFloor_GuardThrows()
    {
        var method = FindMethod(typeof(FutureGatedBuffer<int>), "FutureStatic", isStatic: true);
        AssertGuardThrows(() => method.Invoke(null, null), "FutureGatedBuffer<int>.futureStatic");
    }

    private void AssertGuardThrows(Func<object?> invoke, string memberLabel)
    {
        try
        {
            invoke();
            throw new AssertionException(
                $"Expected {memberLabel} to throw PlatformNotSupportedException from its runtime " +
                "availability guard, but it returned normally. The guard was not emitted or did not " +
                "fire — a consumer on an OS below the floor would crash natively.");
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException;
            TestLogger.Info($"{memberLabel} threw {inner?.GetType().Name}: {inner?.Message}");
            AssertTrue(inner is PlatformNotSupportedException,
                $"{memberLabel} must throw PlatformNotSupportedException from its runtime guard; " +
                $"got {inner?.GetType().Name ?? "null"} instead.");
            AssertTrue(inner!.Message.Contains("not available", StringComparison.OrdinalIgnoreCase),
                "The guard's exception message must explain the member is unavailable on this OS " +
                $"version (for diagnosability); got: {inner.Message}");
        }
    }
}
