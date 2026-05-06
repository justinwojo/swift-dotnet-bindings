// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
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

    /// <summary>
    /// Bundle 05 #1 (some-protocol over-broad) regression: when Swift
    /// declares <c>some OpaqueDescribable</c> at parameter position the
    /// generator must emit the bound protocol type as a generic-parameter
    /// constraint. Pre-fix the constraint synthesizer dropped the protocol
    /// name and emitted <c>where T : ISwiftObject</c> only — letting
    /// callers pass any <see cref="ISwiftObject"/>, which would crash at
    /// runtime when Swift projected metadata against a missing witness
    /// table. Post-fix the constraint set must include
    /// <c>IOpaqueDescribable</c>.
    /// </summary>
    public void TestOpaqueLabelCharacterCount_ConstraintIncludesBoundProtocol()
    {
        // Filter on name + generic arity + parameter count rather than calling
        // GetMethod(name) directly. A bare GetMethod(name) lookup throws
        // AmbiguousMatchException the moment a future overload of
        // OpaqueLabelCharacterCount lands (e.g. an Encodable-stdlib variant or
        // a non-generic erased helper). The filter form keeps the assertion
        // narrowly targeted at the specific generated generic method we care
        // about — one generic parameter, one value parameter — so adding an
        // unrelated overload to the fixture does not silently break the test.
        var candidates = typeof(SwiftBindingsTestLib.Functions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(SwiftBindingsTestLib.Functions.OpaqueLabelCharacterCount))
            .Where(m => m.IsGenericMethodDefinition)
            .Where(m => m.GetGenericArguments().Length == 1)
            .Where(m => m.GetParameters().Length == 1)
            .ToArray();

        AssertEqual(1, candidates.Length,
            "Exactly one OpaqueLabelCharacterCount<T> method overload (1 generic, 1 parameter) " +
            "must be discoverable via reflection. A different count means the fixture has " +
            "drifted (overload added or removed) and the constraint assertion below is " +
            "no longer aimed at the generated some-protocol entry point.");

        var method = candidates[0];
        var generics = method.GetGenericArguments();
        var constraints = generics[0].GetGenericParameterConstraints();
        var names = constraints.Select(c => c.Name).ToArray();
        TestLogger.Info($"OpaqueLabelCharacterCount<T> constraints: [{string.Join(", ", names)}]");

        AssertTrue(
            constraints.Any(c => c == typeof(IOpaqueDescribable)),
            "Bundle 05 #1: generic-parameter constraint set must include IOpaqueDescribable. " +
            "Without it the over-broad ISwiftObject-only constraint allows mismatched " +
            "ISwiftObject types to compile and crash at runtime when Swift's witness-table " +
            "lookup fails.");
    }

}
