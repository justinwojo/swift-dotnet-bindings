// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for a protocol requirement satisfied by a default declared in a constrained
/// extension of a DIFFERENT protocol — <c>extension P where Self : Q { ... }</c>.
///
/// The default's member is spelled on <c>P</c>, which does not inherit <c>Q</c>, so attributing it
/// to the extended protocol alone left <c>Q</c>'s requirement looking unsatisfiable. It was emitted
/// as an abstract C# interface member, and the two conformers that rely on the default failed in
/// the two different ways that gap produces: one still inherited the requirement through an
/// umbrella protocol and did not compile, the other silently lost the conformance altogether.
///
/// Both halves assert the conformance survived and that Swift still dispatches through it —
/// referencing these types as their interfaces is what proves the C# side kept them.
/// </summary>
public class CrossProtocolExtensionDefaultTests : TestBase
{
    public CrossProtocolExtensionDefaultTests(TestResults results) : base(results) { }

    /// <summary>
    /// Both protocols come from the dependency module, so the requirement is emitted while
    /// generating that module — where nothing conforms to it — and only the downstream conformer
    /// can reveal the missing member.
    /// </summary>
    public void TestDependencyModuleConstrainedDefaultSatisfiesRequirement()
    {
        using var row = new DefaultedRow();

        var tag = SwiftBindingsTestLibDependency.Functions.ReadSectionTag(row);

        AssertEqual(7, tag,
            "Swift dispatched the constrained-extension default for a conformer that declares no member of its own");
    }

    /// <summary>
    /// The conformer is reached through its interface rather than its concrete type: a dropped
    /// conformance would not compile here, which is exactly how this half failed.
    /// </summary>
    public void TestDefaultedConformerStillDeclaresTheCapabilityInterface()
    {
        using var row = new DefaultedRow();

        ISectionTagging tagging = row;

        AssertNotNull(tagging, "Conformer relying on a cross-protocol extension default still implements the interface");
    }

    /// <summary>
    /// Mirror image: the extension is declared in the main module but extends a DEPENDENCY
    /// protocol, which classified it as a foreign-type extension and kept it out of this module's
    /// protocol-extension defaults entirely.
    /// </summary>
    public void TestForeignExtensionConstrainedDefaultSatisfiesLocalRequirement()
    {
        using var row = new DefaultedHeightRow();

        IRowHeighting heighting = row;
        var height = SwiftBindingsTestLib.Functions.ReadRowHeight(heighting);

        AssertEqual(44, height,
            "Default from an extension of a dependency protocol answered the local protocol's requirement");
    }

    /// <summary>
    /// A conformer with a real member of its own must keep winning over the default. Its presence
    /// is also what holds the fix honest: it keeps the phantom-default detector inert, so the
    /// requirement is only covered by the attribution under test.
    /// </summary>
    public void TestExplicitMemberStillOverridesTheExtensionDefault()
    {
        using var row = new ExplicitHeightRow();

        var height = SwiftBindingsTestLib.Functions.ReadRowHeight(row);

        AssertEqual(88, height,
            "Conformer's own member answered instead of the constrained-extension default");
    }
}
