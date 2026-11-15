// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for instance members that return a non-frozen value type built from generic
/// parameters. Swift always returns such a value through the indirect-result register. A binding
/// that calls one directly, declaring a by-value result and passing no result buffer, compiles on
/// both sides and then reads its own stack slot as the value, so every callable member here
/// round-trips real fields.
/// </summary>
public class GenericInstanceIndirectResultTests : TestBase
{
    public GenericInstanceIndirectResultTests(TestResults results) : base(results) { }

    // ── Routed through the static-dispatch wrapper ──────────────────────

    public void TestBoundGenericStructArgumentAndResult()
    {
        using var table = new IndirectTable<long>(tag: 4);
        using var aggregate = new IndirectAggregate<long>(weight: 17);
        using var request = table.Having(aggregate);
        AssertEqual(417, request.Tag, "having(_:) returns the request it built");
        AssertEqual("having", request.Note, "having(_:) returns the request's string field");

        using var textTable = new IndirectTable<SwiftString>(tag: 2);
        using var textAggregate = new IndirectAggregate<SwiftString>(weight: 5);
        using var textRequest = textTable.Having(textAggregate);
        AssertEqual(205, textRequest.Tag, "having(_:) works for a reference-counted generic argument");
    }

    public void TestBoundGenericClassArgument()
    {
        using var table = new IndirectTable<long>(tag: 3);
        using var alias = new IndirectAlias<long>(offset: 9);
        using var request = table.Aliased(alias);
        AssertEqual(309, request.Tag, "aliased(_:) reads the class argument it was given");
        AssertEqual("aliased", request.Note, "aliased(_:) returns the request's string field");
    }

    public void TestNestedTypeResult()
    {
        using var table = new IndirectTable<int>(tag: 12);
        using var cursor = table.MakeCursor();
        AssertEqual(12, cursor.Position, "makeCursor() returns the nested struct's scalar field");
        AssertEqual("cursor", cursor.Label, "makeCursor() returns the nested struct's string field");
    }

    public void TestNestedTypeResultOnConstrainedParent()
    {
        using var table = new IndirectRankedTable<IndirectRankSeven>();
        using var cursor = table.MakeCursor();
        AssertEqual(21, cursor.Position, "makeCursor() reads the rank through the witness table");
    }

    // Reading a property must leave the value it was read from intact. A getter on a type nested
    // in a generic parent once went out as a direct call that loads self into the register the
    // interop stub also used for its own bookkeeping, so the stub's cleanup wrote a stack address
    // into the struct's first field: the first read was right and the second read that address.
    public void TestNestedTypePropertyReadsAreRepeatable()
    {
        using var ranked = new IndirectRankedTable<IndirectRankSeven>.Cursor(position: 21);
        AssertEqual((nint)21, ranked.PositionNative, "first read of a constrained parent's nested struct field");
        AssertEqual((nint)21, ranked.PositionNative, "second read sees the same field");

        using var table = new IndirectTable<int>(tag: 12);
        using var cursor = table.MakeCursor();
        AssertEqual((nint)12, cursor.PositionNative, "first read of an unconstrained parent's nested struct field");
        AssertEqual((nint)12, cursor.PositionNative, "second read sees the same field");
        AssertEqual("cursor", cursor.Label, "the nested struct's string field survives the scalar reads");
        AssertEqual("cursor", cursor.Label, "and survives its own read");
    }

    // Methods on a type nested in a generic parent take the same route as its property accessors,
    // so a call must return the right value and leave the receiver readable afterwards.
    public void TestNestedTypeMethodCallsAreRepeatable()
    {
        using var ranked = new IndirectRankedTable<IndirectRankSeven>.Cursor(position: 21);
        AssertEqual((nint)147, ranked.GetRanked(), "ranked() reads the rank through the parent parameter's witness table");
        AssertEqual((nint)147, ranked.GetRanked(), "a second call sees the same receiver");
        AssertEqual((nint)21, ranked.PositionNative, "the receiver's field survives the calls");

        using var table = new IndirectTable<int>(tag: 12);
        using var cursor = table.MakeCursor();
        AssertEqual((nint)15, cursor.Advanced(3), "advanced(by:) adds its argument to the nested struct's field");
        AssertEqual((nint)19, cursor.Advanced(7), "a second call sees the same receiver");
        AssertEqual("cursor", cursor.Label, "the nested struct's string field survives the calls");
    }

    public void TestRoutedMembersBindWithoutUnsafeMarkers()
    {
        AssertCallable(typeof(IndirectTable<long>), "Having", "Aliased", "MakeCursor");
        AssertCallable(typeof(IndirectRankedTable<IndirectRankSeven>), "MakeCursor");
        AssertCallable(typeof(IndirectTable<long>.Cursor), "Advanced");
        AssertCallable(typeof(IndirectRankedTable<IndirectRankSeven>.Cursor), "GetRanked");
    }

    // ── Member generics: the direct import passes the result buffer ─────

    // A member generic of its own has no static-dispatch wrapper, so its import is the direct
    // Swift call. It passes the buffer the callee writes the result into. On iOS, Mac Catalyst and
    // tvOS a native bridge carries that call. On macOS it stays a guarded direct transition,
    // which must either return the value or refuse before Swift runs on an engine that
    // mishandles one of its shapes.

    private const SwiftTransitionFacts MemberGenericShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    public void TestMemberGenericOnGenericParentReturnsItsValue()
    {
        using var table = new IndirectTable<long>(tag: 6);
        for (int i = 0; i < 3; i++)
        {
            var request = ReturnedOrRefused(() => table.With(5L), MemberGenericShapes, ImportRoutes.BridgesRouted, "IndirectTable<T>.With");
            if (request is null)
                return;
            using (request)
            {
                AssertEqual(6, request.Tag, $"with(_:) returns the table's tag (call {i})");
                AssertEqual("with 5", request.Note, $"with(_:) returns the note it built from its argument (call {i})");
            }
        }
        AssertEqual(6, table.Tag, "the receiver is untouched");
    }

    public void TestMemberGenericOnPlainClassReturnsItsValue()
    {
        using var source = new IndirectSource();
        var request = ReturnedOrRefused(() => source.Wrap(5L, 2), MemberGenericShapes, ImportRoutes.BridgesRouted, "IndirectSource.Wrap");
        if (request is null)
            return;
        using (request)
        {
            AssertEqual(2, request.Tag, "wrap(_:tag:) returns the tag it was given");
            AssertEqual("wrap 5", request.Note, "wrap(_:tag:) returns the note it built from its argument");
        }
        using var word = new SwiftString("text");
        using var text = source.Wrap(word, 4);
        AssertEqual("wrap text", text.Note, "wrap(_:tag:) works for a reference-counted generic argument");
    }

    public void TestFreeGenericFunctionReturnsItsValue()
    {
        var request = ReturnedOrRefused(() => Functions.MakeIndirectRequest(5L, 9), SwiftTransitionFacts.LoweredSignature,
            ImportRoutes.BridgesRouted, "MakeIndirectRequest");
        if (request is null)
            return;
        using (request)
        {
            AssertEqual(9, request.Tag, "makeIndirectRequest(_:tag:) returns the tag it was given");
            AssertEqual("free 5", request.Note, "makeIndirectRequest(_:tag:) returns the note it built from its argument");
        }
    }

    /// <summary>
    /// The member generics no longer carry the ABI-floor tombstone (SB0009) that made every call
    /// throw. Like any member generic called directly, they keep the generator's SB0001 marker.
    /// </summary>
    public void TestMemberGenericsAreNoLongerTombstoned()
    {
        AssertNotTombstoned(typeof(IndirectTable<long>), "With");
        AssertNotTombstoned(typeof(IndirectSource), "Wrap");
    }

    private void AssertNotTombstoned(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type type, string name)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name).ToArray();
        AssertTrue(methods.Length > 0, $"{type.Name}.{name} is bound");
        foreach (var method in methods)
            AssertTrue(method.GetCustomAttribute<ObsoleteAttribute>()?.DiagnosticId != "SB0009",
                $"{type.Name}.{name} carries no ABI-floor tombstone");
    }

    /// <summary>
    /// Calls <paramref name="call"/> and returns its result, or, when the member keeps a guarded direct
    /// transition this engine mishandles, asserts that it refused before Swift ran and returns null.
    /// </summary>
    private T? ReturnedOrRefused<T>(Func<T> call, SwiftTransitionFacts shapes, bool bridged, string member) where T : class
    {
        if (bridged || SwiftTransitionGuard.IsSupported(shapes))
            return call();
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            (call() as IDisposable)?.Dispose();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on its guarded direct route");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal) || refusal.Member.Contains(member.Replace("<T>", ""), StringComparison.Ordinal),
            $"the refusal names the member: {refusal.Member}");
        AssertEqual(shapes, refusal.Facts, "the refusal carries the transition's shapes");
        return null;
    }

    private void AssertCallable(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type type,
        params string[] names)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var name in names)
        {
            // A member taking a native-int parameter also gets an int convenience overload, so
            // every same-named method is checked rather than assuming there is exactly one.
            var methods = type.GetMethods(flags).Where(m => m.Name == name).ToArray();
            AssertTrue(methods.Length > 0, $"{type.Name}.{name} is bound");
            foreach (var method in methods)
            {
                var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
                AssertNull(method.GetCustomAttribute<ObsoleteAttribute>()?.DiagnosticId, $"{type.Name}.{name}({parameters}) carries no unsafe-call marker");
            }
        }
    }
}
