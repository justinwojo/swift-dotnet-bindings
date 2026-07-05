// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for the cross-carrier INHERITED-requirement suppression fix (the inheritance
/// sibling of <see cref="CrossCarrierSignatureCollisionTests"/>).
///
/// A child protocol that refines a parent whose umbrella conformance lands on a DIFFERENT concrete
/// carrier class routes to <c>EveryObjCProtocol</c> (it lists <c>NSObjectProtocol</c>) while the
/// parent routes to plain <c>EveryProtocol</c>. Swift cannot satisfy the child's carrier against the
/// parent's witness (emitted on the other carrier), so pre-fix the wrapper failed to compile with
/// <c>type 'EveryObjCProtocol' does not conform to protocol '&lt;Parent&gt;'</c>. The fix suppresses the
/// child's umbrella conformance fail-closed while leaving the parent's conformance intact — both for a
/// same-module parent (<c>CarrierValidationParent</c>) and a cross-module dependency-module parent
/// (<c>SwiftBindingsTestLibDependency.CrossCarrierCrossModuleParent</c>).
///
/// Reaching either free function already proves the wrapper compiled (the bug was a compile failure).
/// The assertions additionally prove the parent's conformance still reverse-dispatches into the managed
/// implementation — i.e. the suppression is surgical, not a blanket drop of the whole inheritance chain.
/// </summary>
public class CrossCarrierInheritedProtocolTests : TestBase
{
    public CrossCarrierInheritedProtocolTests(TestResults results) : base(results) { }

    /// <summary>
    /// A C# class implementing the (same-module) parent interface round-trips through the parent's
    /// <c>EveryProtocol</c> conformance even though the refining child's conformance was suppressed.
    /// </summary>
    public void TestSameModuleParentConformanceStillDispatches()
    {
        var impl = new CarrierValidationParentImpl("same-module");
        var result = Functions.ReadValidationLabel(impl);
        AssertEqual("label:same-module", result,
            "Parent EveryProtocol conformance dispatched into the managed impl after child suppression");
    }

    /// <summary>
    /// The child interface still EXTENDS the parent, so an object implementing the child is a valid
    /// parent conformer and must round-trip through the parent path — proving the suppressed child's
    /// interface remains usable via its (unsuppressed) parent conformance.
    /// </summary>
    public void TestChildImplUpcastsToParentAndDispatches()
    {
        var impl = new CarrierValidationChildImpl("via-child");
        var result = Functions.ReadValidationLabel(impl);
        AssertEqual("label:via-child", result,
            "An impl of the suppressed child interface still dispatches through the parent conformance");
    }

    /// <summary>
    /// A C# class implementing the CROSS-MODULE (dependency-module) parent interface round-trips through
    /// the dependency module's own conformance — proving the local carrier-split suppression AND the
    /// orphaned-parent-scaffolding drop did not break the parent's reverse dispatch.
    /// </summary>
    public void TestCrossModuleParentConformanceStillDispatches()
    {
        var impl = new CrossCarrierCrossModuleParentImpl("cross-module");
        var result = Functions.ReadCrossModuleValidationLabel(impl);
        AssertEqual("xlabel:cross-module", result,
            "Cross-module parent conformance dispatched into the managed impl after child suppression + orphan drop");
    }
}

/// <summary>Same-module parent-only managed implementation.</summary>
internal class CarrierValidationParentImpl : ICarrierValidationParent
{
    private readonly string _tag;
    public CarrierValidationParentImpl(string tag) => _tag = tag;
    public string GetValidationLabel() => $"label:{_tag}";
}

/// <summary>
/// Implements the suppressed child interface (which still extends the parent). Used to prove the
/// child interface upcasts to and dispatches through the parent conformance.
/// </summary>
internal class CarrierValidationChildImpl : ICarrierValidationChild
{
    private readonly string _tag;
    public CarrierValidationChildImpl(string tag) => _tag = tag;
    public string GetValidationLabel() => $"label:{_tag}";
    public bool ChildFlag => true;
}

/// <summary>Cross-module (dependency-module) parent managed implementation.</summary>
internal class CrossCarrierCrossModuleParentImpl : SwiftBindingsTestLibDependency.ICrossCarrierCrossModuleParent
{
    private readonly string _tag;
    public CrossCarrierCrossModuleParentImpl(string tag) => _tag = tag;
    public string GetCrossCarrierLabel() => $"xlabel:{_tag}";
}
