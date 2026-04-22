// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for fix #6 (commit <c>2c80b227</c>): parameter-position
/// opaque-type lowering. Fix #6 teaches the parser to rewrite
/// <c>some P</c> at a parameter position into a synthetic generic
/// parameter so the rest of the generic emission pipeline can handle it.
/// Before the fix, the StoreKit direct-mode snapshot failed because the
/// parser crashed or dropped methods with opaque parameters.
///
/// Exercises the common-case shape: a single-requirement user-defined
/// protocol (<see cref="OpaqueDescribable"/>). The Swift source also
/// declares an <c>opaqueEncodedByteCount(_: some Encodable)</c> stress
/// case, but standard-library PAT protocols like Encodable can't be
/// marshalled through a free-function P/Invoke without runtime PWT
/// lookup — the generator correctly reports it in the skip manifest
/// and the stdlib-path invariant is guarded at the generator unit-test
/// layer (see <c>MethodValidationGates.HasUnsupportedProtocolConstraints</c>).
///
/// Assertions are pure observable pass-through: the C# caller
/// constructs a Swift conformer, invokes the method, and verifies the
/// returned value. Per CLAUDE.md we do not inspect the generated C#
/// method signature to avoid coupling the test to the emitter's internal
/// lowering strategy.
/// </summary>
public class OpaqueParamTests : TestBase
{
    public OpaqueParamTests(TestResults results) : base(results) { }

    /// <summary>
    /// User-defined opaque protocol parameter: pass an <see cref="OpaqueTag"/>
    /// with a known label, verify the Swift side returns its character
    /// count unchanged. Proves the synthetic generic parameter produced by
    /// fix #6 is actually usable in the method body, not just elided.
    /// </summary>
    public void TestOpaqueLabelCharacterCount()
    {
        var tag = new OpaqueTag(label: "hello");
        var count = TestLibFunctions.OpaqueLabelCharacterCount(tag);
        TestLogger.Info($"opaqueLabelCharacterCount(OpaqueTag(\"hello\")) = {count}");
        AssertEqual(5, count,
            "opaqueLabelCharacterCount must return the Swift-side character count " +
            "of the OpaqueTag's opaqueLabel. Fix #6 must lower the opaque parameter " +
            "into a synthetic generic whose body can still access the conformer's " +
            "property.");
    }

    /// <summary>
    /// Empty-string edge case on the user-protocol opaque-parameter path.
    /// Rules out a regression where the generator emits an off-by-one
    /// probe of the conformer's requirement.
    /// </summary>
    public void TestOpaqueLabelCharacterCountEmpty()
    {
        var tag = new OpaqueTag(label: "");
        var count = TestLibFunctions.OpaqueLabelCharacterCount(tag);
        TestLogger.Info($"opaqueLabelCharacterCount(OpaqueTag(\"\")) = {count}");
        AssertEqual(0, count,
            "opaqueLabelCharacterCount must return 0 for an empty opaqueLabel.");
    }

}
