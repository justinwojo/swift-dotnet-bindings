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
/// round-trips real fields, and every member with no sound route must declare that it throws.
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

    // ── No sound route: declared, marked and throwing ───────────────────

    public void TestMemberGenericOnGenericParentIsTombstoned()
    {
        var with = typeof(IndirectTable<long>).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(m => m.Name == "With");
        AssertNotNull(with, "with(_:) is declared");
        AssertEqual("SB0009", with?.GetCustomAttribute<ObsoleteAttribute>()?.DiagnosticId, "with(_:) carries the ABI-floor marker");
        using var table = new IndirectTable<long>(tag: 1);
#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.
        AssertThrows<NotSupportedException>(() => table.With(5L), "with(_:) refuses the call");
#pragma warning restore SB0009
    }

    public void TestMemberGenericOnPlainClassIsTombstoned()
    {
        var wrap = typeof(IndirectSource).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(m => m.Name == "Wrap");
        AssertNotNull(wrap, "wrap(_:tag:) is declared");
        AssertEqual("SB0009", wrap?.GetCustomAttribute<ObsoleteAttribute>()?.DiagnosticId, "wrap(_:tag:) carries the ABI-floor marker");
        using var source = new IndirectSource();
#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.
        AssertThrows<NotSupportedException>(() => source.Wrap(5L, 2), "wrap(_:tag:) refuses the call");
#pragma warning restore SB0009
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
