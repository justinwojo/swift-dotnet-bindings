// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime coverage for the PAT / Self-requirement protocol fallback to
/// <c>object</c> in <c>ExistentialHandler.GetPublicExistentialType()</c>.
///
/// <para>Two halves pinned here:</para>
/// <list type="number">
///   <item>
///     <b>Compile-time half (<see cref="TestReadTaggedAssociatorIsLoweredToObjectParameter"/>).</b>
///     A free function taking <c>any TaggedAssociator</c> (where TaggedAssociator
///     has <c>associatedtype Item</c>) must lower to <c>object</c> parameter.
///   </item>
///   <item>
///     <b>Runtime dispatch half (<see cref="TestReadTaggedAssociatorDispatch"/>).</b>
///     Passing a concrete conformer through the <c>object</c> parameter boxes
///     into an <c>ExistentialContainer1</c> with the conformer's witness table
///     so Swift dispatches <c>.tag</c> to the concrete type.
///   </item>
/// </list>
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
    /// Fix #7 runtime-dispatch half: verifies that passing an
    /// <see cref="IntTaggedAssociator"/> through the <c>object</c> parameter
    /// successfully boxes into an <c>ExistentialContainer1</c> and dispatches
    /// the protocol's <c>.tag</c> property back to the concrete conformer.
    /// Also tests <see cref="StringTaggedAssociator"/> to prove the dispatch
    /// routes to the concrete conformer, not a shared default implementation.
    /// </summary>
    public void TestReadTaggedAssociatorDispatch()
    {
        using var intAssoc = new IntTaggedAssociator();

        // The direct-dispatch C# getter on the concrete type must work
        // independently of the existential boundary. This is the control:
        // if `tag` doesn't round-trip *without* the existential boxing, the
        // test below is meaningless because we'd be blaming the wrong layer.
        AssertEqual("int-tagged-associator", intAssoc.Tag,
            "IntTaggedAssociator.Tag must return its direct string even before " +
            "the existential boundary path is exercised. If this fails the failure " +
            "is below the existential layer and the dispatch assertion below is noise.");

        var dispatched = TestLibFunctions.ReadTaggedAssociator(intAssoc);
        TestLogger.Info($"ReadTaggedAssociator(IntTaggedAssociator) returned \"{dispatched}\"");

        AssertEqual("int-tagged-associator", dispatched,
            "ReadTaggedAssociator must dispatch .tag through the PAT existential " +
            "container to the concrete IntTaggedAssociator conformer. The generator " +
            "emits IExistentialBoxable and populates _protocolConformanceSymbols with " +
            "a typeof(object) entry for PAT conformances.");

        // Second conformer proves the dispatch routes to the concrete type,
        // not a shared default implementation.
        using var stringAssoc = new StringTaggedAssociator();
        var dispatched2 = TestLibFunctions.ReadTaggedAssociator(stringAssoc);
        TestLogger.Info($"ReadTaggedAssociator(StringTaggedAssociator) returned \"{dispatched2}\"");

        AssertEqual("string-tagged-associator", dispatched2,
            "ReadTaggedAssociator must dispatch .tag to StringTaggedAssociator " +
            "(not IntTaggedAssociator), proving the existential container carries " +
            "the correct witness table for each concrete conformer.");
    }
}
