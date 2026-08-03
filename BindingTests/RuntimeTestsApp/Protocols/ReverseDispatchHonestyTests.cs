// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Type = System.Type;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning
// This file names the hollow interface on purpose — to assert the marker exists and that forward
// use still works. Consumers who genuinely implement it are exactly who the warning is for.
#pragma warning disable SB0010 // reverse-dispatch marker: asserted here, not heeded

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime gate for reverse-dispatch honesty. Declaring <c>: ISomeDelegate</c> in C# is a promise that
/// Swift will call the type back, and that promise is kept only for requirements that get a receiver
/// trampoline wired into the proxy vtable. When NO requirement does, the interface used to stay silently
/// conformable while the proxy registered an all-null table — a C# implementation compiled, linked, ran,
/// and was never once invoked.
///
/// <para>
/// The Swift fixture lives in
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolReverseDispatchHonesty.swift</c> and
/// pairs a negative control (<c>HollowUploadDelegate</c> — every requirement carries a closure the
/// dispatch path cannot marshal) with a positive control (<c>PartialUploadDelegate</c> — the same
/// non-dispatchable requirement plus one that does dispatch). The gate is strictly "zero callback slots
/// filled": the partial protocol does real reverse dispatch through its surviving slot and must be left
/// untouched, marker and all.
/// </para>
///
/// <para>
/// The hollow half is asserted STRUCTURALLY rather than by driving Swift into a null vtable — the
/// pre-fix behaviour was a silent no-op or a fault, neither of which a test can catch cleanly, and the
/// contract a consumer actually meets is the compile-time marker plus the absent registration.
/// </para>
/// </summary>
public class ReverseDispatchHonestyTests : TestBase
{
    public ReverseDispatchHonestyTests(TestResults results) : base(results) { }

    private const string ReverseDispatchDiagnosticId = "SB0010";

    /// <summary>
    /// The whole point of the marker: a consumer who writes <c>class MyDelegate : IHollowUploadDelegate</c>
    /// gets told, at compile time, that nothing will ever call it. Without this the only symptom is a
    /// callback that never fires.
    /// </summary>
    public void TestHollowProtocolInterfaceCarriesReverseDispatchMarker()
    {
        var obs = typeof(IHollowUploadDelegate).GetCustomAttribute<ObsoleteAttribute>();

        AssertNotNull(obs,
            "IHollowUploadDelegate must carry an [Obsolete] marker: none of its requirements can be " +
            "reverse-dispatched, so implementing it in C# has no effect and the consumer must be told.");
        AssertEqual(ReverseDispatchDiagnosticId, obs!.DiagnosticId,
            "The marker must use its own diagnostic id so a consumer can suppress it independently of " +
            "the other binding markers.");
        AssertFalse(obs.IsError,
            "The marker must be warning-level, not error:true — the interface is still legitimately " +
            "usable in the forward direction, so an error would break working code.");
    }

    /// <summary>
    /// The strict-zero-fill boundary. One dispatchable requirement means a C# implementation genuinely
    /// IS called back, so marking the interface would be a lie that costs working reverse dispatch.
    /// </summary>
    public void TestPartiallyDispatchableProtocolCarriesNoMarker()
    {
        var obs = typeof(IPartialUploadDelegate).GetCustomAttribute<ObsoleteAttribute>();

        AssertNull(obs,
            "IPartialUploadDelegate must NOT carry the reverse-dispatch marker. It shares the hollow " +
            "protocol's non-dispatchable requirement but also declares one that dispatches, so the gate " +
            "must key on zero FILLED callback slots — never on 'some requirement was dropped'.");
    }

    /// <summary>
    /// Positive control, end to end: the surviving slot must actually fire, not merely exist. A C#
    /// implementation is handed to Swift and Swift calls back into it.
    /// </summary>
    public void TestPartiallyDispatchableProtocolStillReverseDispatches()
    {
        using var coordinator = new UploadCoordinator();
        var impl = new PartialUploadDelegateImpl("csharp-upload-42");
        coordinator.PartialDelegate = impl;

        var identifier = coordinator.GetPartialIdentifier();
        TestLogger.Info($"UploadCoordinator.GetPartialIdentifier() = {identifier}");

        AssertEqual("csharp-upload-42", identifier,
            "Swift must call back into the C# implementation through the one filled vtable slot. " +
            "'none' means the delegate never arrived; a crash or empty string means the surviving slot " +
            "was dropped along with the unfillable ones.");
    }

    /// <summary>
    /// Suppressing the registration must not cost the produce path, which never used the vtable. A
    /// non-null projection alone would not show that — an empty shell is also non-null — so the vended
    /// value is handed BACK to Swift, which dispatches it through the conformer's own witness table.
    /// C# cannot make that call itself here: every requirement of the hollow protocol carries a closure,
    /// which is exactly why it is hollow, and calling one through the existential is refused by its own
    /// separate marker.
    /// </summary>
    public void TestHollowProtocolProducePathStillYieldsALiveConformer()
    {
        using var coordinator = new UploadCoordinator();

        var vended = coordinator.GetVendHollowDelegate();
        AssertNotNull(vended, "The produce path must still project a Swift-vended conformer.");

        var probed = coordinator.ProbeHollowDelegate(vended);
        TestLogger.Info($"UploadCoordinator.ProbeHollowDelegate(vended) = {probed}");

        AssertEqual(7, probed,
            "Swift must reach the vended conformer's real implementation: 7 is what " +
            "HollowUploadDelegateImpl.beginUpload returns after invoking the progress closure. " +
            "-1 means the closure never fired, and anything else means the round-trip lost the value.");
    }

    // ============================================================
    // Member-kind parity for the CONSUME-degrade marker (SB0008).
    //
    // Every member of BoxableConsumerParity takes the same `any Boxable` parameter, so every one of them
    // degrades identically: BoxableProxy is suppressed, the C#→Swift wrap fallback is dropped, and a
    // C#-authored conformer handed to any of them silently never fires. They reach the emitter through
    // four different paths (constructor, failable initializer → static TryCreate, static factory,
    // instance method). A path that never reads the degrade record back emits a member that invites a
    // conformer it will ignore, with no warning, one line from an identically-typed sibling that has one.
    // ============================================================

    private void AssertConsumeDegradeMarked(MemberInfo? member, string memberDesc)
    {
        AssertNotNull(member, $"{memberDesc} must exist — the degraded member keeps its surface.");
        var obs = member!.GetCustomAttribute<ObsoleteAttribute>();
        AssertNotNull(obs,
            $"{memberDesc} takes an existential whose reverse-dispatch proxy was suppressed, so a " +
            "C#-authored conformer passed to it never fires. It must carry the consume-degrade marker.");
        AssertEqual("SB0008", obs!.DiagnosticId,
            $"{memberDesc} marker must use the consume-degrade diagnostic id.");
        AssertFalse(obs.IsError,
            $"{memberDesc} marker must be warning-level — a Swift-vended conformer still round-trips, " +
            "so the member genuinely works and an error would be wrong.");
    }

    public void TestConsumeDegradeMarkerOnConstructor()
    {
        AssertConsumeDegradeMarked(
            GetBoxableTakingConstructor(typeof(BoxableConsumerParity)),
            "BoxableConsumerParity(IBoxable)");
    }

    public void TestConsumeDegradeMarkerOnStaticFactory()
    {
        AssertConsumeDegradeMarked(
            GetSingleMethod(typeof(BoxableConsumerParity), "Make"),
            "BoxableConsumerParity.Make(IBoxable)");
    }

    public void TestConsumeDegradeMarkerOnFailableInitializerFactory()
    {
        AssertConsumeDegradeMarked(
            GetSingleMethod(typeof(BoxableConsumerParity), "TryCreateWithOptional"),
            "BoxableConsumerParity.TryCreateWithOptional(IBoxable, out …)");
    }

    public void TestConsumeDegradeMarkerOnInstanceMethod()
    {
        AssertConsumeDegradeMarked(
            GetSingleMethod(typeof(BoxableConsumerParity), "Combined"),
            "BoxableConsumerParity.Combined(IBoxable)");
    }

    // The annotated parameters are what keep these members alive under the device leg's NativeAOT
    // trimming — an unannotated lookup returns null there and the test fails for a reason that has
    // nothing to do with the marker it is asserting.
    private static MethodInfo? GetSingleMethod(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
               .FirstOrDefault(m => m.Name == name);

    private static ConstructorInfo? GetBoxableTakingConstructor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
        => type.GetConstructors()
               .FirstOrDefault(c => c.GetParameters() is { Length: 1 } p &&
                                    p[0].ParameterType == typeof(IBoxable));
}

/// <summary>
/// C#-authored conformer for the positive control. Implementing this interface is a real promise here:
/// <c>GetUploadIdentifier</c> owns a filled vtable slot, so Swift calls straight into it.
/// </summary>
internal sealed class PartialUploadDelegateImpl : IPartialUploadDelegate
{
    private readonly string _identifier;

    public PartialUploadDelegateImpl(string identifier) => _identifier = identifier;

    public int BeginUpload(Action<double> onProgress)
    {
        onProgress(1.0);
        return 1;
    }

    public string GetUploadIdentifier() => _identifier;
}
