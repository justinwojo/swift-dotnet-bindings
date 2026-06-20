// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression test for the projected-key builder's protocol-path <c>parentTypeName</c> omission
/// (AF05 ruling a). The Swift protocol <c>KeyRegion</c> declares <c>func keyRegion(_:) -&gt; Int32</c>
/// — a method whose PascalCase name (<c>KeyRegion</c>) equals the protocol name.
///
/// On the CLASS path the emitted member name folds in the CS0542 parent-name rename
/// (<c>KeyRegion</c> → <c>GetKeyRegion</c>, because C# forbids a member named identically to its
/// enclosing type). On the PROTOCOL path the enclosing emitted type is the interface
/// <c>IKeyRegion</c>, so the member <c>KeyRegion</c> never collides with its container — the rename
/// must NOT fire. The shared key builder keeps the protocol path opted out of <c>parentTypeName</c>.
///
/// What this proves, and where (scoped precisely — these are two DISTINCT guards):
///   • COMPILE time — the emitted interface member NAME is <c>KeyRegion(int)</c>, NOT <c>GetKeyRegion</c>:
///     <see cref="KeyRegionImpl"/> implements <c>int KeyRegion(int)</c>, so a regression that re-applied
///     the CS0542 parent-name rename to the protocol member NAME — which protocol interface emission
///     computes INDEPENDENTLY of the projected key — would make <c>IKeyRegion</c> declare
///     <c>GetKeyRegion</c> and this file would fail to build (CS0535). This end-to-end test locks the
///     emitted name + reverse-dispatch; it does NOT (and cannot) catch a change to the projected-KEY
///     dedup flag, because that flag does not feed the member name. With a single requirement, flipping
///     <c>IncludeParentTypeName</c> on the protocol key would still emit <c>KeyRegion</c> here.
///   • The projected-KEY <c>parentTypeName</c> omission itself (ruling a's actual variable, consumed only
///     for overload-dedup) is proven directly by the unit test
///     <c>BaseHandlerDedupTests.GetProjectedCSharpMethodKey_ProtocolPath_OmitsParentTypeName_DivergesFromClassPath</c>.
///   • RUNTIME (simulator + device) — the member reverse-dispatches: <see cref="TestKeyRegion_RoundTrips"/>
///     drives the Swift free function back into the C# impl through the proxy vtable slot.
/// </summary>
public class KeyBuilderParentNameProtocolTests : TestBase
{
    public KeyBuilderParentNameProtocolTests(TestResults results) : base(results) { }

    /// <summary>
    /// Reverse-dispatch the <c>keyRegion(_:)</c> requirement: the Swift free function calls back into
    /// the C# impl's <c>int KeyRegion(int)</c>. Compiles only because the protocol path did not rename
    /// the member to <c>GetKeyRegion</c>; round-trips the value to prove the slot dispatches.
    /// </summary>
    public void TestKeyRegion_RoundTrips()
    {
        var impl = new KeyRegionImpl(multiplier: 3);
        var result = Functions.CallKeyRegion(impl, 14);
        AssertEqual(42, result,
            "Protocol member keeps its bare name (KeyRegion, not GetKeyRegion) and reverse-dispatches to the C# impl");
    }
}

// Implements IKeyRegion's single member. The member is named KeyRegion (NOT GetKeyRegion) because the
// protocol path's emitted enclosing type is the interface IKeyRegion, so the CS0542 parent-name rename
// has no collision to resolve and the name-computation path leaves it bare — so this `int KeyRegion(int)`
// satisfies the interface. A regression that re-applied the parent-name rename to the member NAME would
// make IKeyRegion declare GetKeyRegion, and this class would no longer compile (CS0535). (The projected-
// KEY parentTypeName omission — a separate axis used only for dedup — is pinned by the unit test, not here.)
internal class KeyRegionImpl : IKeyRegion
{
    private readonly int _multiplier;
    public KeyRegionImpl(int multiplier) => _multiplier = multiplier;

    public int KeyRegion(int x) => x * _multiplier;
}
