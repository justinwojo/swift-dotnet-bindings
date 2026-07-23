// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData(ApplePlatform.iOS, "Microsoft.iOS")]
    [InlineData(ApplePlatform.macOS, "Microsoft.macOS")]
    [InlineData(ApplePlatform.tvOS, "Microsoft.tvOS")]
    [InlineData(ApplePlatform.MacCatalyst, "Microsoft.MacCatalyst")]
    public void RefPackName_SelectsPerPlatformReferenceAssembly(ApplePlatform platform, string expected)
    {
        // The surface index is verified against the reference assembly for the target platform, not
        // always Microsoft.iOS — so a macOS/tvOS/MacCatalyst run resolves its own ref pack + dll token.
        Assert.Equal(expected, AppleTypeSurfaceIndex.RefPackName(platform));
    }

    [Theory]
    [InlineData(ApplePlatform.macOS)]
    [InlineData(ApplePlatform.tvOS)]
    public void GenerateBindings_PlatformParameter_BecomesAmbientSurfacePlatform(ApplePlatform platform)
    {
        // A library caller that passes `platform` without separately calling SetAmbientPlatform must
        // still have its ObjC-bridged references verified against that platform's surface — the
        // ambient the surface index resolves has to follow the parameter, not stay at the iOS default.
        AppleTypeSurfaceIndex.ResetAmbientPlatform();
        using var fixture = new GenerateBindingsFixture("AtsiAmbientModule");
        try
        {
            BindingsGenerator.GenerateBindings(
                fixture.AbiJsonPath, fixture.DylibPath, fixture.TbdPath, fixture.Dir,
                "AtsiAmbientModule", null, null, null, null, "{Module}",
                NullLogger.Instance, NullLoggerFactory.Instance,
                out _, out _, out _, out _,
                platform: platform);

            Assert.Equal(platform, AppleTypeSurfaceIndex.AmbientPlatform);
        }
        finally
        {
            AppleTypeSurfaceIndex.ResetAmbientPlatform();
        }
    }

    [Fact]
    public void GenerateBindings_NullPlatform_LeavesAmbientPlatformUntouched()
    {
        // The CLI records the ambient before GenerateBindings runs (surface reads happen during
        // dependency parsing and ObjC-bridge ingest); a run without a platform parameter must not
        // clobber that earlier decision.
        AppleTypeSurfaceIndex.SetAmbientPlatform(ApplePlatform.MacCatalyst);
        using var fixture = new GenerateBindingsFixture("AtsiAmbientNullModule");
        try
        {
            BindingsGenerator.GenerateBindings(
                fixture.AbiJsonPath, fixture.DylibPath, fixture.TbdPath, fixture.Dir,
                "AtsiAmbientNullModule", null, null, null, null, "{Module}",
                NullLogger.Instance, NullLoggerFactory.Instance,
                out _, out _, out _, out _,
                platform: null);

            Assert.Equal(ApplePlatform.MacCatalyst, AppleTypeSurfaceIndex.AmbientPlatform);
        }
        finally
        {
            AppleTypeSurfaceIndex.ResetAmbientPlatform();
        }
    }

    /// <summary>
    /// Minimal on-disk generation inputs (ABI JSON with a named empty module, stub dylib/TBD) — just
    /// enough for <see cref="BindingsGenerator.GenerateBindings"/> to run start to finish.
    /// </summary>
    private sealed class GenerateBindingsFixture : IDisposable
    {
        public string Dir { get; }
        public string AbiJsonPath { get; }
        public string DylibPath { get; }
        public string TbdPath { get; }

        public GenerateBindingsFixture(string moduleName)
        {
            Dir = Path.Combine(Path.GetTempPath(), $"atsi_ambient_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);

            AbiJsonPath = Path.Combine(Dir, "test.abi.json");
            DylibPath = Path.Combine(Dir, "test.dylib");
            TbdPath = Path.Combine(Dir, "test.tbd");

            File.WriteAllText(AbiJsonPath, $$"""
                {
                  "ABIRoot": {
                    "kind": "Root",
                    "name": "{{moduleName}}",
                    "printedName": "{{moduleName}}",
                    "children": [
                      {
                        "kind": "TypeDecl",
                        "declKind": "Import",
                        "name": "{{moduleName}}",
                        "printedName": "{{moduleName}}",
                        "moduleName": "{{moduleName}}",
                        "children": []
                      }
                    ]
                  }
                }
                """);
            File.WriteAllBytes(DylibPath, new byte[] { 0 });
            File.WriteAllText(TbdPath, "--- !tapi-tbd\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

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
