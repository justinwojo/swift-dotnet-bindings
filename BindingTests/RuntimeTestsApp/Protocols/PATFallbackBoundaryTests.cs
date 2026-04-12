// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime coverage for fix #7 (commit 4235d568): the "PAT / Self-requirement
/// protocol fallback to object" branch in
/// <c>ExistentialHandler.GetPublicExistentialType()</c>
/// (src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs:459-474).
///
/// <para>
/// Fix #7 has two observable halves and this test pins both:
/// </para>
/// <list type="number">
///   <item>
///     <b>Compile-time half (WORKING, pinned by <see cref="TestReadTaggedAssociatorIsLoweredToObjectParameter"/>).</b>
///     A free function that takes <c>any TaggedAssociator</c> where
///     <c>TaggedAssociator</c> has an <c>associatedtype Item</c> must lower to
///     a public C# function whose parameter type is the literal <c>object</c>
///     (not <c>ITaggedAssociator&lt;TSelf&gt;</c> which would need a type
///     argument that isn't in scope at the call site). This is the part of
///     fix #7 that TipKit and WeatherKit needed to compile at all.
///   </item>
///   <item>
///     <b>Runtime dispatch half (BROKEN, pinned by
///     <see cref="TestReadTaggedAssociatorDispatchLatentBug"/>).</b>
///     Passing a concrete conformer <i>value</i> through the <c>object</c>
///     parameter must box into an <c>ExistentialContainer1</c> pointing at
///     the conformer's <c>TaggedAssociator</c> witness table, so that Swift
///     can dispatch <c>.tag</c> back to the concrete type. Today this path
///     throws <see cref="InvalidCastException"/> from
///     <c>ExistentialContainerFactory.GetOrCreate&lt;object&gt;</c> because
///     the generator does not emit <c>IExistentialBoxable</c> on PAT-conformer
///     classes and does not populate the per-type protocol-conformance symbol
///     dictionary. Fix #7 only closed the emit-to-<c>object</c> half; the
///     runtime boxing half is the follow-up that needs to land for the
///     dispatch assertion to flip to asserting concrete tag values.
///   </item>
/// </list>
///
/// <para>
/// <b>Why this test exists even though dispatch is broken</b>: a BindingTests
/// runtime pin for the compile-time half (#1 above) prevents fix #7 from
/// regressing silently — reverting the <c>HasAssociatedTypes</c> branch would
/// emit <c>ITaggedAssociator&lt;TSelf&gt; assoc</c> at this call site and
/// <c>nuke binding-tests</c> would fail to compile the runtime test before
/// the simulator even launches. The latent-bug pin on #2 documents the
/// follow-up work and flips to a real dispatch assertion when the generator
/// fix lands.
/// </para>
/// </summary>
public class PATFallbackBoundaryTests : TestBase
{
    public PATFallbackBoundaryTests(TestResults results) : base(results) { }

    /// <summary>
    /// Fix #7 compile-time half: reflectively confirm that
    /// <c>TestLibFunctions.ReadTaggedAssociator</c> exposes its
    /// <c>any TaggedAssociator</c> parameter as the literal <c>object</c>
    /// type. If this assertion fails, the generator has regressed and the
    /// PAT/Self-requirement fallback in
    /// <c>ExistentialHandler.GetPublicExistentialType()</c> is emitting the
    /// generic interface name (e.g., <c>ITaggedAssociator&lt;TSelf&gt;</c>)
    /// which is an invalid C# reference at a free-function call site.
    /// </summary>
    public void TestReadTaggedAssociatorIsLoweredToObjectParameter()
    {
        var method = typeof(TestLibFunctions).GetMethod(
            "ReadTaggedAssociator",
            BindingFlags.Public | BindingFlags.Static);
        AssertTrue(method is not null,
            "TestLibFunctions.ReadTaggedAssociator must exist on the generated binding. " +
            "If this assertion fails, fix #7 has regressed — the free function taking " +
            "any TaggedAssociator was skipped entirely instead of emitted with the " +
            "object-parameter fallback.");

        var parameters = method!.GetParameters();
        AssertEqual(1, parameters.Length,
            "ReadTaggedAssociator must have exactly one parameter.");

        var paramType = parameters[0].ParameterType;
        TestLogger.Info($"ReadTaggedAssociator parameter[0] type = {paramType.FullName}");
        AssertEqual(typeof(object), paramType,
            "ReadTaggedAssociator's `any TaggedAssociator` parameter must lower to the " +
            "literal `object` C# type. Fix #7 (4235d568) rewrites PAT/Self-requirement " +
            "protocol parameters to `object` because `ITaggedAssociator<TSelf>` has no " +
            "type argument in scope at the call site. A regression here means the " +
            "emitted signature references an invalid generic interface and consumer " +
            "projects will not compile.");

        var returnType = method.ReturnType;
        AssertEqual(typeof(string), returnType,
            "ReadTaggedAssociator must return string (the dispatched `.tag`).");
    }

    /// <summary>
    /// Fix #7 runtime-dispatch half: pins the current broken state where
    /// passing an <see cref="IntTaggedAssociator"/> through the <c>object</c>
    /// parameter throws <see cref="InvalidCastException"/> from
    /// <c>Swift.Runtime.ExistentialContainerFactory.GetOrCreate&lt;object&gt;</c>.
    /// The generator currently does not emit <c>IExistentialBoxable</c> on
    /// PAT-conformer classes — its <c>_protocolConformanceSymbols</c>
    /// dictionary is empty on every conformer generated for a protocol with
    /// <c>hasAssociatedTypes=true</c> — so the factory's cascade (check for
    /// <c>ISwiftExistentialConvertible</c>, then <c>IExistentialBoxable</c>,
    /// otherwise throw) lands on the throw branch.
    /// </summary>
    public void TestReadTaggedAssociatorDispatchLatentBug()
    {
        using var intAssoc = new IntTaggedAssociator();

        // The direct-dispatch C# getter on the concrete type must work
        // independently of the existential boundary. This is the control:
        // if `tag` doesn't round-trip *without* the existential boxing, the
        // test below is meaningless because we'd be blaming the wrong layer.
        AssertEqual("int-tagged-associator", intAssoc.Tag,
            "IntTaggedAssociator.Tag must return its direct string even before " +
            "the existential boundary path is exercised. If this fails the failure " +
            "is below the existential layer and the latent-bug pin below is noise.");

        // Only IntTaggedAssociator is exercised here by design. The fixture
        // also defines StringTaggedAssociator specifically to prove that the
        // dispatch resolves to the *concrete* conformer's `.tag` and not a
        // shared default implementation — but asserting two conformers only
        // makes sense after the latent-bug pin flips. Until then a second
        // throw-assertion would be redundant noise.
        Exception? thrown = null;
        string? dispatched = null;
        try
        {
            dispatched = TestLibFunctions.ReadTaggedAssociator(intAssoc);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        TestLogger.Info(thrown is null
            ? $"ReadTaggedAssociator(IntTaggedAssociator) returned \"{dispatched}\""
            : $"ReadTaggedAssociator(IntTaggedAssociator) threw {thrown.GetType().Name}: {thrown.Message}");

        // LATENT-BUG PIN: flip to AssertEqual("int-tagged-associator", dispatched)
        // when the generator starts emitting IExistentialBoxable on PAT-conformer
        // classes AND populating the _protocolConformanceSymbols dictionary with
        // the TaggedAssociator conformance symbol. Until then, the factory cascade
        // in src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs:928-940
        // lands on the throw branch because IntTaggedAssociator implements neither
        // ISwiftExistentialConvertible<ExistentialContainer1> nor IExistentialBoxable.
        // The generator work is in the emitter/existential-boxing path that is
        // currently gated off for protocols with hasAssociatedTypes=true.
        //
        // Flip checklist — when the runtime half lands:
        //   1. Replace the InvalidCastException assertion below with
        //      AssertEqual("int-tagged-associator", dispatched).
        //   2. Add a StringTaggedAssociator sibling call and assert
        //      "string-tagged-associator" to prove the boxing routes to the
        //      concrete conformer rather than a default implementation.
        //   3. Delete the note above about only one conformer being exercised.
        AssertTrue(thrown is InvalidCastException,
            "Documents current broken dispatch for PAT fallback: passing an " +
            "IntTaggedAssociator value through ReadTaggedAssociator(object) must " +
            "throw InvalidCastException today because the factory cannot box a " +
            "concrete PAT-conformer into an ExistentialContainer1. When the " +
            "generator starts emitting IExistentialBoxable on PAT-conformer classes, " +
            "this assertion will start failing and the fixer must follow the flip " +
            "checklist above.");
    }
}
