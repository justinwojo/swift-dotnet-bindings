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
/// Runtime gate for static members declared on generic parents. Swift passes these differently
/// from instance members: a static on a generic struct or enum takes no self and each generic
/// parameter's own metadata, with a generic or address-only result through the indirect-result
/// register; a static on a generic class takes its metatype as self. A binding that calls them
/// with the instance-member shape compiles on both sides and then crashes or returns garbage, so
/// every test here round-trips a real value.
/// </summary>
public class GenericStaticMemberTests : TestBase
{
    public GenericStaticMemberTests(TestResults results) : base(results) { }

    // ── Generic enum ────────────────────────────────────────────────────

    public void TestEnumStaticFactoryWrapsScalar()
    {
        using var choice = StaticChoice<long>.Wrapping(42L);
        AssertFalse(choice.IsNothing, "wrapping(_:) builds the payload case");
        AssertEqual(42L, choice.Boxed, "wrapping(_:) stores the value it was given");
    }

    public void TestEnumStaticFactoryWrapsString()
    {
        using var input = new SwiftString("static factory");
        using var choice = StaticChoice<SwiftString>.Wrapping(input);
        AssertFalse(choice.IsNothing, "wrapping(_:) builds the payload case for a string payload");
        using var boxed = choice.Boxed;
        AssertEqual("static factory", boxed?.ToString(), "wrapping(_:) stores a reference-counted payload");
    }

    public void TestEnumStaticFactoryWithoutArguments()
    {
        using var choice = StaticChoice<SwiftString>.GetEmpty();
        AssertTrue(choice.IsNothing, "empty() builds the payload-free case");
        AssertNull(choice.Boxed, "the payload-free case has no value");

        using var scalar = StaticChoice<long>.GetEmpty();
        AssertTrue(scalar.IsNothing, "empty() builds the payload-free case for a scalar payload");
    }

    public void TestEnumStaticReturnsGenericParameter()
    {
        AssertEqual(1234L, StaticChoice<long>.Echo(1234L), "echo(_:) returns its argument");
        using var echoIn = new SwiftString("echoed");
        using var echoed = StaticChoice<SwiftString>.Echo(echoIn);
        AssertEqual("echoed", echoed.ToString(), "echo(_:) returns a string argument");
    }

    public void TestEnumStaticReadsGenericParameterMetadata()
    {
        AssertEqual(8L, (long)StaticChoice<long>.GetValueSize(), "valueSize() is MemoryLayout<Int64>.size");
        AssertEqual(1L, (long)StaticChoice<byte>.GetValueSize(), "valueSize() is MemoryLayout<UInt8>.size");
        AssertEqual(8, StaticChoice<long>.ValueStride, "valueStride is MemoryLayout<Int64>.stride");
        AssertEqual(2, StaticChoice<short>.ValueStride, "valueStride is MemoryLayout<Int16>.stride");
    }

    // ── Generic struct ──────────────────────────────────────────────────

    public void TestStructStaticFactory()
    {
        using var box = StaticBox<long>.Make(7L);
        AssertEqual(7L, box.Value, "make(_:) builds a box holding the value");

        using var textIn = new SwiftString("boxed");
        using var text = StaticBox<SwiftString>.Make(textIn);
        using var textValue = text.Value;
        AssertEqual("boxed", textValue.ToString(), "make(_:) builds a box holding a string");
    }

    public void TestStructStaticReturnsGenericParameter()
    {
        AssertEqual(3L, StaticBox<long>.Pick(3L, 4L, takeFirst: true), "pick returns the first argument");
        AssertEqual(4L, StaticBox<long>.Pick(3L, 4L, takeFirst: false), "pick returns the second argument");
        using var first = new SwiftString("first");
        using var second = new SwiftString("second");
        using var picked = StaticBox<SwiftString>.Pick(first, second, takeFirst: false);
        AssertEqual("second", picked.ToString(), "pick returns a string argument");
    }

    public void TestStructStaticReadsGenericParameterMetadata()
    {
        AssertEqual(4L, (long)StaticBox<int>.GetElementSize(), "elementSize() is MemoryLayout<Int32>.size");
        AssertEqual(2L, (long)StaticBox<short>.GetElementSize(), "elementSize() is MemoryLayout<Int16>.size");
        AssertEqual(8, StaticBox<long>.ElementAlignment, "elementAlignment is MemoryLayout<Int64>.alignment");
        AssertEqual(1, StaticBox<byte>.ElementAlignment, "elementAlignment is MemoryLayout<UInt8>.alignment");
    }

    public void TestStructStaticClosureBridge()
    {
        long reported = -1;
        StaticBox<int>.ReportSize(result =>
        {
            if (result.TryGetSuccess(out var v))
                reported = (long)v;
            result.Dispose();
        });
        AssertEqual(4L, reported, "reportSize(_:) reports MemoryLayout<Int32>.size through the callback");
    }

    // ── Generic class ───────────────────────────────────────────────────

    public void TestClassStaticReadsGenericParameterMetadata()
    {
        AssertEqual(8L, (long)StaticGen<long>.GetElementSize(), "elementSize() is MemoryLayout<Int64>.size");
        AssertEqual(2L, (long)StaticGen<short>.GetElementSize(), "elementSize() is MemoryLayout<Int16>.size");
        AssertEqual(4, StaticGen<int>.ElementStride, "elementStride is MemoryLayout<Int32>.stride");
        AssertEqual(1, StaticGen<byte>.ElementStride, "elementStride is MemoryLayout<UInt8>.stride");
    }

    public void TestClassStaticFactory()
    {
        using var made = StaticGen<long>.Make(seed: 31);
        AssertEqual(31, made.Seed, "make(seed:) builds an instance with the given seed");
    }

    public void TestClassStaticReturnsGenericParameter()
    {
        AssertEqual(99L, StaticGen<long>.Echo(99L), "echo(_:) returns its argument");
        using var classEchoIn = new SwiftString("class echo");
        using var classEcho = StaticGen<SwiftString>.Echo(classEchoIn);
        AssertEqual("class echo", classEcho.ToString(), "echo(_:) returns a string argument");
    }

    public void TestClassFuncDispatchesOnMetatype()
    {
        AssertEqual(1, StaticGen<long>.GetKind(), "the base class func answers 1");
        AssertEqual(2, StaticGenSub<long>.GetKind(), "the subclass override answers 2");
    }

    public void TestClassStaticClosureBridge()
    {
        long reported = -1;
        StaticGen<long>.ReportStride(result =>
        {
            if (result.TryGetSuccess(out var v))
                reported = (long)v;
            result.Dispose();
        });
        AssertEqual(8L, reported, "reportStride(_:) reports MemoryLayout<Int64>.stride through the callback");
    }

    // ── Statics from an extension pinning the parameter ─────────────────

    public void TestPinnedExtensionStaticScalarMethod()
    {
        AssertEqual((nint)42, PinnedQuery<PinnedPayload<int>>.Doubled(21), "doubled(_:) round-trips through the pinned extension");
    }

    public void TestPinnedExtensionStaticProperty()
    {
        AssertEqual(17, PinnedQuery<PinnedPayload<int>>.PinnedLimit, "pinnedLimit reads through the pinned extension");
    }

    /// <summary>
    /// A pinned static returning the parent through the indirect result has no wrapper route (the
    /// wrapper's unconditional conformance extension cannot name it), so it is declared and
    /// tombstoned rather than dropped or called with the wrong convention.
    /// </summary>
    public void TestPinnedExtensionStaticIndirectResultIsTombstoned()
    {
        var make = typeof(PinnedQuery<PinnedPayload<int>>).GetMethod(
            "Make", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, new[] { typeof(nint) });
        AssertNotNull(make, "make(tag:) is declared");
        AssertEqual("SB0009", make?.GetCustomAttribute<ObsoleteAttribute>()?.DiagnosticId, "make(tag:) carries the ABI-floor marker");
#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.
        AssertThrows<NotSupportedException>(() => PinnedQuery<PinnedPayload<int>>.Make(3), "make(tag:) refuses the call");
#pragma warning restore SB0009
    }

    // ── Statics returning a type nested in the generic parent ───────────

    public void TestStaticReturnsNestedStruct()
    {
        using var leaf = StaticNestOuter<int>.MakeLeaf(50);
        AssertEqual(50, leaf.Value, "makeLeaf(value:) returns the nested struct through the indirect result");
        using var other = StaticNestOuter<SwiftString>.MakeLeaf(-7);
        AssertEqual(-7, other.Value, "makeLeaf(value:) works for a reference-counted outer argument");
    }

    public void TestStaticReturnsNestedClass()
    {
        using var node = StaticNestOuter<long>.MakeNode(51);
        AssertEqual(51, node.Value, "makeNode(value:) returns the nested class");
    }

    // ── Statics from an extension narrowing the parameter ───────────────

    public void TestNarrowedExtensionStaticScalar()
    {
        AssertEqual(21, StaticNarrowHolder<StaticNarrowBase>.Scaled(7), "scaled(_:) round-trips through the narrowed extension");
    }

    public void TestNarrowedExtensionStaticReturnsClass()
    {
        using var made = StaticNarrowHolder<StaticNarrowBase>.MakeBase(12);
        AssertNotNull(made, "makeBase(tag:) returns an instance");
        AssertEqual(12, made?.Tag, "makeBase(tag:) builds the instance with the given tag");
        AssertNull(StaticNarrowHolder<StaticNarrowBase>.MakeBase(-1), "makeBase(tag:) returns nil for a negative tag");
    }

    // ── Surface ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every static in the fixture has a sound route, so none may carry an unsafe-call marker or be
    /// dropped. A member that silently fell back to the direct instance-shaped call would carry
    /// SB0001; one that lost its route entirely would be missing.
    /// </summary>
    public void TestStaticMembersBindWithoutUnsafeMarkers()
    {
        AssertBoundCleanly(typeof(StaticChoice<long>), "Wrapping", "GetEmpty", "Echo", "GetValueSize", "ValueStride");
        AssertBoundCleanly(typeof(StaticBox<long>), "Make", "Pick", "GetElementSize", "ElementAlignment", "ReportSize");
        AssertBoundCleanly(typeof(StaticGen<long>), "GetElementSize", "Make", "Echo", "GetKind", "ElementStride", "ReportStride");
        AssertBoundCleanly(typeof(StaticGenSub<long>), "GetKind");
        AssertBoundCleanly(typeof(StaticNestOuter<long>), "MakeLeaf", "MakeNode");
        AssertBoundCleanly(typeof(StaticNarrowHolder<StaticNarrowBase>), "Scaled", "MakeBase");
    }

    private void AssertBoundCleanly(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties)] System.Type type,
        params string[] names)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var name in names)
        {
            MemberInfo? member = (MemberInfo?)type.GetMethod(name, flags) ?? type.GetProperty(name, flags);
            AssertNotNull(member, $"{type.Name}.{name} is bound");
            if (member == null)
                continue;
            var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
            AssertNull(obsolete?.DiagnosticId, $"{type.Name}.{name} carries no unsafe-call marker");
        }
    }
}
