// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration.ObjC;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="SwiftTypeOwnershipManifestEmitter.Build"/> — the structured
/// <c>swift-types.json</c> ownership manifest the Swift pipeline writes (Finding 23). The manifest
/// records, per public Swift type, the Objective-C runtime name it registers under (the
/// mixed-framework dedup key) alongside its C# projection and kind. The two defects of the old
/// regex scrape that these tests pin against:
/// <list type="number">
/// <item>a protocol emits as <c>IFoo</c> in C# but registers as <c>Foo</c> in the ObjC runtime —
/// the manifest must carry <c>Foo</c> as the match key, not <c>IFoo</c>;</item>
/// <item>an <c>@objc(CustomName)</c> rename changes the ObjC runtime name away from the Swift
/// source name — the manifest must carry the custom name.</item>
/// </list>
/// </summary>
public class SwiftTypeOwnershipManifestTests
{
    private static ModuleDecl NewModule(string name = "M") => new()
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ClassDecl NewClass(
        string name, ModuleDecl module, BaseDecl? parent = null, string? objcRuntimeName = null) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
        MangledName = $"$s1{module.Name}{name.Length}{name}C",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ObjCRuntimeName = objcRuntimeName,
        ParentDecl = parent ?? module,
        ModuleDecl = module,
    };

    private static StructDecl NewStruct(string name, ModuleDecl module) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
        MangledName = $"$s1{module.Name}{name.Length}{name}V",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = true,
        MetadataAccessor = $"$s1{module.Name}{name.Length}{name}VMa",
        ParentDecl = module,
        ModuleDecl = module,
    };

    private static EnumDecl NewEnum(string name, ModuleDecl module) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
        MangledName = $"$s1{module.Name}{name.Length}{name}O",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = true,
        MetadataAccessor = $"$s1{module.Name}{name.Length}{name}OMa",
        ParentDecl = module,
        ModuleDecl = module,
    };

    /// <summary>Builds a protocol and registers it in BOTH <c>module.Types</c> and
    /// <c>module.Protocols</c>, mirroring the parser (<c>OfType&lt;TypeDecl&gt;()</c> puts a
    /// <c>ProtocolDecl</c> in both lists). Used to prove the manifest does not double-count it.</summary>
    private static ProtocolDecl AddProtocol(string name, ModuleDecl module)
    {
        var protocolDecl = new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
            MangledName = $"$s1{module.Name}{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(protocolDecl);
        module.Protocols.Add(protocolDecl);
        return protocolDecl;
    }

    [Fact]
    public void Class_NoRename_ObjCRuntimeNameEqualsSwiftName()
    {
        var module = NewModule();
        module.Types.Add(NewClass("Foo", module));

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        var entry = Assert.Single(manifest.Types);
        Assert.Equal("Foo", entry.SwiftName);
        Assert.Equal("Foo", entry.ObjCRuntimeName);
        Assert.Equal("Foo", entry.ProjectedCSharpName);
        Assert.Equal("class", entry.Kind);
        Assert.Equal("M", manifest.Module);
        Assert.Equal(SwiftTypeOwnershipManifest.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    [Fact]
    public void Class_ObjcRename_CarriesCustomRuntimeName()
    {
        // @objc(MOSWidget) class Widget — the ObjC runtime name (dedup key) is the custom name,
        // while the C# projection stays the Swift source name.
        var module = NewModule();
        module.Types.Add(NewClass("Widget", module, objcRuntimeName: "MOSWidget"));

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        var entry = Assert.Single(manifest.Types);
        Assert.Equal("Widget", entry.SwiftName);
        Assert.Equal("MOSWidget", entry.ObjCRuntimeName);
        Assert.Equal("Widget", entry.ProjectedCSharpName);
    }

    [Fact]
    public void Protocol_ObjCRuntimeNameIsBareName_ProjectedCSharpNameIsIPrefixed()
    {
        // The protocol-leg fix: ObjC registers the protocol under its bare Swift name (Drawable),
        // but the Swift pipeline emits it as the C# interface IDrawable. The dedup key must be the
        // bare ObjC name, not the I-prefixed projection.
        var module = NewModule();
        AddProtocol("Drawable", module);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        var entry = Assert.Single(manifest.Types);
        Assert.Equal("Drawable", entry.SwiftName);
        Assert.Equal("Drawable", entry.ObjCRuntimeName);
        Assert.Equal("IDrawable", entry.ProjectedCSharpName);
        Assert.Equal("protocol", entry.Kind);
    }

    [Fact]
    public void Protocol_NotDoubleCounted_DespiteAppearingInTypesAndProtocols()
    {
        // The parser puts a ProtocolDecl in both module.Types and module.Protocols (Types is the
        // superset). Build() must walk Types alone — walking both lists would emit two entries.
        var module = NewModule();
        AddProtocol("Drawable", module);
        Assert.Single(module.Types);
        Assert.Single(module.Protocols);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        Assert.Single(manifest.Types);
        Assert.Single(manifest.Types, e => e.ObjCRuntimeName == "Drawable");
    }

    [Fact]
    public void AllFourKinds_AreEmittedWithCorrectKind()
    {
        var module = NewModule();
        module.Types.Add(NewClass("C", module));
        module.Types.Add(NewStruct("S", module));
        module.Types.Add(NewEnum("E", module));
        AddProtocol("P", module);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        Assert.Equal("class", manifest.Types.Single(e => e.SwiftName == "C").Kind);
        Assert.Equal("struct", manifest.Types.Single(e => e.SwiftName == "S").Kind);
        Assert.Equal("enum", manifest.Types.Single(e => e.SwiftName == "E").Kind);
        Assert.Equal("protocol", manifest.Types.Single(e => e.SwiftName == "P").Kind);
    }

    [Fact]
    public void ModuleInternalAndSpi_AreExcluded()
    {
        // The public-surface gate: @usableFromInline (IsModuleInternal) and @_spi (IsSpiProtected)
        // types are not consumer-visible ObjC surface, so they never collide and must not drive a
        // dedup drop.
        var module = NewModule();
        module.Types.Add(NewClass("Public", module));

        var internalClass = NewClass("Internal", module);
        internalClass.IsModuleInternal = true;
        module.Types.Add(internalClass);

        var spiClass = NewClass("Spi", module);
        spiClass.IsSpiProtected = true;
        module.Types.Add(spiClass);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        var entry = Assert.Single(manifest.Types);
        Assert.Equal("Public", entry.SwiftName);
    }

    [Fact]
    public void NestedTypes_AreRecursed()
    {
        // A nested @objc class is part of the ObjC surface; the manifest must include nested types.
        var module = NewModule();
        var outer = NewClass("Outer", module);
        var nested = NewClass("Inner", module, parent: outer, objcRuntimeName: "MOSInner");
        outer.Types.Add(nested);
        module.Types.Add(outer);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        Assert.Equal(2, manifest.Types.Count);
        Assert.Contains(manifest.Types, e => e.SwiftName == "Outer" && e.ObjCRuntimeName == "Outer");
        Assert.Contains(manifest.Types, e => e.SwiftName == "Inner" && e.ObjCRuntimeName == "MOSInner");
    }

    [Fact]
    public void EmptyModule_ProducesEmptyTypesButValidHeader()
    {
        var module = NewModule("Empty");

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module);

        Assert.Empty(manifest.Types);
        Assert.Equal("Empty", manifest.Module);
        Assert.Equal(SwiftTypeOwnershipManifest.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    // ──────────────────────────────────────────────
    // End-to-end round-trip: Emit (to disk) → ReadOwnedObjCRuntimeNames → FilterForMixedFramework.
    // Build() and FilterForMixedFramework() are each unit-covered above and in
    // MixedFrameworkDedupTests; nothing else wires them together across the on-disk swift-types.json
    // boundary. These tests pin the contract that actually runs in the mixed-framework pipeline: the
    // dedup key the Swift side hands the ObjC side is the ObjC *runtime* name (bare protocol name,
    // @objc-custom class name) — not the C# projection. This is a pure string-key-space agreement
    // (no ABI/calling-convention/marshalling), so the correct gate is in-process, not BindingTests.
    // ──────────────────────────────────────────────

    private static void WithTempDir(Action<string> body)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "swifttypes-manifest-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RoundTrip_EmitThenRead_KeySetIsObjCRuntimeNames_NotCSharpProjections()
    {
        // A module exercising both headline divergences:
        //   - Drawable (protocol)      → ObjC runtime "Drawable",  C# "IDrawable"
        //   - Widget @objc(MOSWidget)  → ObjC runtime "MOSWidget", C# "Widget"
        //   - Plain (class, no rename) → ObjC runtime "Plain"
        var module = NewModule();
        module.Types.Add(NewClass("Plain", module));
        module.Types.Add(NewClass("Widget", module, objcRuntimeName: "MOSWidget"));
        AddProtocol("Drawable", module);

        WithTempDir(dir =>
        {
            SwiftTypeOwnershipManifestEmitter.Emit(module, dir, ObjCTestHelpers.Logger);
            Assert.True(File.Exists(Path.Combine(dir, SwiftTypeOwnershipManifest.FileName)));

            var owned = SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(dir);

            // The match keys are the ObjC runtime names.
            Assert.Contains("Plain", owned);
            Assert.Contains("MOSWidget", owned);
            Assert.Contains("Drawable", owned);
            // The C# projections must NOT be in the key set — matching on them is the old-regex bug.
            Assert.DoesNotContain("IDrawable", owned);
            Assert.DoesNotContain("Widget", owned);
        });
    }

    [Fact]
    public void RoundTrip_EmitReadFilter_DropsSharedObjCDecls_ByObjCRuntimeName()
    {
        // Swift side owns: protocol Drawable (C# IDrawable) and class Widget renamed @objc(MOSWidget).
        var module = NewModule();
        module.Types.Add(NewClass("Widget", module, objcRuntimeName: "MOSWidget"));
        AddProtocol("Drawable", module);

        WithTempDir(dir =>
        {
            SwiftTypeOwnershipManifestEmitter.Emit(module, dir, ObjCTestHelpers.Logger);
            var owned = SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(dir);

            // The ObjC pipeline sees the SAME types under their ObjC runtime names, plus an
            // ObjC-only type that no Swift decl owns.
            var objcModule = new ObjCModule
            {
                ModuleName = "Mixed",
                Classes = [new() { Name = "MOSWidget" }, new() { Name = "ObjCOnlyManager" }],
                Protocols = [new() { Name = "Drawable" }],
                Enums = [],
                Structs = [],
                Functions = [],
                Constants = [],
                Categories = [],
            };

            var filtered = ObjCPipeline.FilterForMixedFramework(objcModule, owned, ObjCTestHelpers.Logger);

            // The @objc-renamed class is dropped by its runtime name, not its C# projection.
            Assert.Single(filtered.Classes);
            Assert.Equal("ObjCOnlyManager", filtered.Classes[0].Name);
            // The shared protocol is dropped by its bare runtime name (IDrawable would never match).
            Assert.Empty(filtered.Protocols);
        });
    }

    [Fact]
    public void RoundTrip_NoManifest_ReturnsEmptyKeySet()
    {
        // A non-mixed / legacy output dir has no swift-types.json; reading is a clean no-op so the
        // ObjC filter simply keeps everything.
        WithTempDir(dir =>
        {
            var owned = SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(dir);
            Assert.Empty(owned);
        });
    }

    [Fact]
    public void RoundTrip_SchemaVersionDrift_FailsLoud_SWIFTBIND105()
    {
        // The writer/reader handshake: if the on-disk schema version doesn't match the reader's
        // expectation, reading must throw rather than silently mis-map ownership (which would
        // re-emit duplicate ObjC classes and resurrect the issue #40 double-registration crash).
        var module = NewModule();
        module.Types.Add(NewClass("Plain", module));

        WithTempDir(dir =>
        {
            SwiftTypeOwnershipManifestEmitter.Emit(module, dir, ObjCTestHelpers.Logger);
            var path = Path.Combine(dir, SwiftTypeOwnershipManifest.FileName);

            // Tamper the on-disk schema version to a future, unrecognised value.
            var json = JObject.Parse(File.ReadAllText(path));
            json["schemaVersion"] = SwiftTypeOwnershipManifest.ExpectedSchemaVersion + 1;
            File.WriteAllText(path, json.ToString());

            var ex = Assert.Throws<InvalidOperationException>(
                () => SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(dir));
            Assert.Contains("SWIFTBIND105", ex.Message);
        });
    }

    [Fact]
    public void RoundTrip_CorruptJson_FailsLoud_SWIFTBIND106()
    {
        // A truncated / corrupt manifest must surface a labeled, actionable error rather than a
        // raw Newtonsoft parse exception — the same fail-loud contract as the schema-drift path,
        // so a bad on-disk manifest can never silently degrade dedup to a no-op.
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, SwiftTypeOwnershipManifest.FileName);
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"types\": [ ");  // truncated JSON

            var ex = Assert.Throws<InvalidOperationException>(
                () => SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(dir));
            Assert.Contains("SWIFTBIND106", ex.Message);
        });
    }
}
