// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Internal;

/// <summary>
/// End-to-end coverage for the emission-time parent-module-internal @_cdecl gate
/// (<c>WrapperValidation.GetMemberRejectionReason</c> arm 2b,
/// <c>parent_module_internal</c>). Pairs with the Swift fixture at
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Internal/InternalConformerToPublicProtocol.swift</c>.
///
/// <c>InternalContractConformer</c> is <c>@usableFromInline internal</c> and
/// conforms to the public protocol <c>InternalReceiverContract</c>, so the
/// generator projects it as a public class implementing
/// <c>IInternalReceiverContract</c>. Each requirement witness is <c>public</c>, so
/// it slips the member-keyed internal filter — but its @_cdecl wrapper would name
/// the internal parent by its module-qualified name to reconstruct <c>self</c>,
/// which the separate wrapper-compilation module cannot do. Arm 2b rejects that
/// wrapper at emission, so the member keeps a direct CallConvSwift / native-thunk
/// P/Invoke instead of emitting-then-stripping. That is what keeps the public
/// interface satisfied (no CS0535) AND drives the wrapper-strip count to 0.
///
/// The compile gate already proves no CS0535 (the binding would not compile if a
/// requirement were dropped). These runtime tests prove the kept members actually
/// resolve and round-trip. Every requirement here is an instance method or a
/// property getter on a *non-final* class, so the fallback targets that member's
/// exported <c>Tj</c> dispatch thunk (verified present in the dylib) and the calls
/// bind at runtime rather than throwing <c>EntryPointNotFoundException</c>. (The
/// fallback symbol is member-shape specific — a constructor or a struct/final
/// member resolves to its bare silgen symbol instead, with no <c>Tj</c> suffix;
/// that constructor shape is covered by the <c>WrapperValidationTests</c> unit
/// gate, not here.) Construction barrier: the internal type's init is
/// <c>@usableFromInline internal</c>, so no public C# constructor exists — the
/// instance is obtained through the public factory behind the existential.
///
/// Negative direction: <c>PublicContractConformer</c> has a public parent, so arm
/// 2b never fires; its members keep their normal wrapper path and round-trip
/// directly and through the interface exactly as before.
/// </summary>
public class InternalConformerToPublicProtocolTests : TestBase
{
    public InternalConformerToPublicProtocolTests(TestResults results) : base(results) { }

    private static readonly Assembly BindingsAssembly = typeof(PublicContractConformer).Assembly;

    private static System.Type? FindGeneratedType(string typeName)
        => BindingsAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName);

    #region CS0535-safety — the internal conformer keeps every requirement member

    // NativeAOT trims the reflection metadata of public members on a concrete type that is
    // never instantiated by name (the construction barrier means there is no public ctor, so
    // ILC sees no rooting use of the class itself). The member bodies survive and bind through
    // the interface — TestInternalConformerRoundTripsViaExistential proves that on device — but
    // GetMethod/GetProperty would return null without an explicit trim root. Root the conformer's
    // public surface so the presence assertions observe emission rather than trim-survival; it can
    // only root members that exist, so a real drop still surfaces as a null lookup here.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(InternalContractConformer))]
    public void TestInternalConformerImplementsPublicInterface()
    {
        // Projected as a public class implementing the public interface. If arm 2b
        // had dropped a requirement instead of falling back, the binding would not
        // have compiled (CS0535) — assert the surface explicitly so a future
        // regression that somehow still compiled is caught here too.
        var conformer = FindGeneratedType("InternalContractConformer");
        AssertNotNull(conformer, "InternalContractConformer must be emitted as a public type");
        AssertTrue(conformer!.IsPublic, "InternalContractConformer must be public");

        var iface = FindGeneratedType("IInternalReceiverContract");
        AssertNotNull(iface, "IInternalReceiverContract interface must be emitted");
        AssertTrue(iface!.IsAssignableFrom(conformer),
            "InternalContractConformer must implement IInternalReceiverContract");

        AssertNotNull(conformer.GetMethod("GetContractValue", BindingFlags.Public | BindingFlags.Instance),
            "GetContractValue must be kept (CallConvSwift fallback, not dropped)");
        AssertNotNull(conformer.GetMethod("Combined", BindingFlags.Public | BindingFlags.Instance),
            "Combined must be kept");
        AssertNotNull(conformer.GetProperty("ContractTag", BindingFlags.Public | BindingFlags.Instance),
            "ContractTag must be kept");
        TestLogger.Info("InternalContractConformer implements IInternalReceiverContract with all three members.");
    }

    public void TestInternalConformerHasNoPublicConstructor()
    {
        // Construction barrier: @usableFromInline internal init => no public C# ctor.
        var conformer = FindGeneratedType("InternalContractConformer");
        AssertNotNull(conformer, "InternalContractConformer must be emitted");
        var publicCtors = conformer!.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        AssertEqual(0, publicCtors.Length,
            "InternalContractConformer must expose no public constructor (internal init)");
        TestLogger.Info("InternalContractConformer has no public constructor (construction barrier intact).");
    }

    #endregion

    #region Runtime round-trip — kept members actually resolve and return

    public void TestInternalConformerRoundTripsViaExistential()
    {
        // Obtain the internal conformer behind the public existential (factory),
        // then call each kept requirement. Values must round-trip, proving the
        // CallConvSwift / native-thunk fallback binds at runtime against the
        // exported Tj dispatch thunks.
        IInternalReceiverContract c = Functions.MakeInternalContractConformer(seed: 5);
        AssertNotNull(c, "factory returned null existential");

        AssertEqual(5, c.GetContractValue(), "contractValue() should return the seed");
        AssertEqual(8, c.Combined(other: 3), "combined(with: 3) should be seed + 3");
        AssertEqual(50, c.ContractTag, "contractTag should be seed * 10");
        TestLogger.Info("InternalContractConformer round-trips contractValue/combined/contractTag via existential.");
    }

    #endregion

    #region Negative direction — the public conformer is unaffected by the gate

    public void TestPublicConformerRoundTripsDirectly()
    {
        // PublicContractConformer has a public parent, so arm 2b never fires — its
        // members keep their normal wrapper path. Constructible directly; values
        // round-trip identically to the internal conformer.
        using var p = new PublicContractConformer(seed: 4);
        AssertEqual(4, p.GetContractValue(), "public conformer contractValue");
        AssertEqual(10, p.Combined(other: 6), "public conformer combined(with: 6)");
        AssertEqual(40, p.ContractTag, "public conformer contractTag");
        TestLogger.Info("PublicContractConformer round-trips directly (public-parent path unchanged).");
    }

    public void TestPublicConformerRoundTripsViaInterface()
    {
        // Same instance through the interface — polymorphic dispatch must work so a
        // consumer can treat both conformers uniformly.
        using var p = new PublicContractConformer(seed: 9);
        IInternalReceiverContract c = p;
        AssertEqual(9, c.GetContractValue(), "interface dispatch contractValue");
        AssertEqual(11, c.Combined(other: 2), "interface dispatch combined");
        AssertEqual(90, c.ContractTag, "interface dispatch contractTag");
        TestLogger.Info("PublicContractConformer dispatches through IInternalReceiverContract.");
    }

    #endregion
}
