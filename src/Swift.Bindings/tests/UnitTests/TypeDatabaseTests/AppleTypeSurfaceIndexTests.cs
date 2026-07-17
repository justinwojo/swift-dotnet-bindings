// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Coverage for <see cref="AppleTypeSurfaceIndex"/> — the reflection-built name→shape index of the
/// Microsoft.iOS surface. The classification is exercised by building an index over this test
/// assembly's own fixture types (a stand-in for the installed reference assembly), so the
/// enum/class/struct/static-constants/protocol shapes and the enum underlying-type mapping are
/// pinned without the workload. Resolution semantics use the internal constructor directly.
/// </summary>
public class AppleTypeSurfaceIndexTests
{
    private const string Ns = "BindingsGeneration.Tests";

    private static readonly AppleTypeSurfaceIndex FixtureIndex =
        AppleTypeSurfaceIndex.BuildFromAssembly(typeof(AtsiFixtureClass).Assembly);

    [Fact]
    public void BuildFromAssembly_PlainClass_ClassifiedAsClass()
    {
        Assert.True(FixtureIndex.TryResolveQualified(Ns, nameof(AtsiFixtureClass), out var e));
        Assert.Equal(AppleTypeSurfaceKind.Class, e!.Kind);
    }

    [Fact]
    public void BuildFromAssembly_Struct_ClassifiedAsStruct()
    {
        Assert.True(FixtureIndex.TryResolveQualified(Ns, nameof(AtsiFixtureStruct), out var e));
        Assert.Equal(AppleTypeSurfaceKind.Struct, e!.Kind);
    }

    [Fact]
    public void BuildFromAssembly_StaticClass_ClassifiedAsStaticConstants()
    {
        Assert.True(FixtureIndex.TryResolveQualified(Ns, nameof(AtsiFixtureStaticConstants), out var e));
        Assert.Equal(AppleTypeSurfaceKind.StaticConstants, e!.Kind);
    }

    [Fact]
    public void BuildFromAssembly_Interface_ClassifiedAsProtocol()
    {
        Assert.True(FixtureIndex.TryResolveQualified(Ns, nameof(AtsiFixtureProtocol), out var e));
        Assert.Equal(AppleTypeSurfaceKind.Protocol, e!.Kind);
    }

    [Theory]
    [InlineData(nameof(AtsiFixtureIntEnum), "Int32", false)]
    [InlineData(nameof(AtsiFixtureByteEnum), "UInt8", false)]
    [InlineData(nameof(AtsiFixtureShortEnum), "Int16", false)]
    [InlineData(nameof(AtsiFixtureFlagsEnum), "UInt32", true)]
    public void BuildFromAssembly_Enum_ReflectsUnderlyingTypeAndFlags(
        string name, string expectedUnderlying, bool expectedFlags)
    {
        Assert.True(FixtureIndex.TryResolveQualified(Ns, name, out var e));
        Assert.Equal(AppleTypeSurfaceKind.Enum, e!.Kind);
        Assert.Equal(expectedUnderlying, e.EnumUnderlyingType);
        Assert.Equal(expectedFlags, e.IsFlags);
    }

    [Fact]
    public void BuildFromAssembly_NestedType_NotIndexed()
    {
        // ObjC bindings flatten nested types into the parent name; the nested form is never a
        // reference target the synthesis produces, so it must be excluded from the index.
        Assert.False(FixtureIndex.TryResolveQualified(Ns, "AtsiFixtureNested", out _));
        Assert.False(FixtureIndex.TryResolveBare("AtsiFixtureNested", out _));
    }

    [Fact]
    public void TryResolveQualified_UnknownName_ReturnsFalse()
        => Assert.False(FixtureIndex.TryResolveQualified(Ns, "TotallyAbsentType", out _));

    [Fact]
    public void TryResolveBare_FirstRegisteredWins_AcrossNamespaces()
    {
        // A bare-name collision keeps the first-registered entry (matches BuildFromAssembly's
        // ContainsKey guard). The full-name key stays exact for both.
        var first = new AppleTypeSurfaceEntry("Color", "NsA", AppleTypeSurfaceKind.Class, null, false);
        var second = new AppleTypeSurfaceEntry("Color", "NsB", AppleTypeSurfaceKind.Struct, null, false);
        var byFull = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal)
        {
            ["NsA.Color"] = first,
            ["NsB.Color"] = second,
        };
        var byBare = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal)
        {
            ["Color"] = first, // first writer wins
        };
        var index = new AppleTypeSurfaceIndex(byFull, byBare);

        Assert.True(index.TryResolveBare("Color", out var bare));
        Assert.Equal("NsA", bare!.Namespace);
        Assert.True(index.TryResolveQualified("NsB", "Color", out var qualified));
        Assert.Equal(AppleTypeSurfaceKind.Struct, qualified!.Kind);
    }
}

// ---- Fixture types: a stand-in Microsoft.iOS surface, one per classification shape --------------

public class AtsiFixtureClass { }
public struct AtsiFixtureStruct { public int X; }
public static class AtsiFixtureStaticConstants { public const int K = 1; }
public interface AtsiFixtureProtocol { }
public enum AtsiFixtureIntEnum { None, One }
public enum AtsiFixtureByteEnum : byte { None, One }
public enum AtsiFixtureShortEnum : short { None, One }
[Flags]
public enum AtsiFixtureFlagsEnum : uint { None = 0, A = 1, B = 2 }

public class AtsiFixtureOuter
{
    public enum AtsiFixtureNested { None }
}
