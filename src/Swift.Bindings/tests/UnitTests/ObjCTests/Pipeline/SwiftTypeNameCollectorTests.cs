// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="BindingsGenerator.CollectSwiftEmittedTypeNames"/>, which sources
/// mixed-framework dedup from the structured <c>swift-types.json</c> ownership manifest
/// (Finding 23) — NOT from a regex scrape of emitted C#. The returned set is the Objective-C
/// runtime names the Swift pipeline owns, so an ObjC declaration whose name matches gets deduped.
/// </summary>
public class SwiftTypeNameCollectorTests
{
    private static string CreateTempDirWithManifest(string manifestJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"collector_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SwiftTypeOwnershipManifest.FileName), manifestJson);
        return dir;
    }

    [Fact]
    public void CollectsClassObjCRuntimeNames()
    {
        var dir = CreateTempDirWithManifest("""
            {
              "schemaVersion": 1,
              "module": "M",
              "types": [
                { "swiftName": "Foo", "objcRuntimeName": "Foo", "projectedCSharpName": "Foo", "kind": "class" },
                { "swiftName": "Bar", "objcRuntimeName": "Bar", "projectedCSharpName": "Bar", "kind": "class" }
              ]
            }
            """);
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("Foo", names);
            Assert.Contains("Bar", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectsProtocolByObjCRuntimeName_NotIPrefixedCSharpName()
    {
        // The protocol leg of the dedup contract: the manifest carries the ObjC runtime name
        // (Drawable), NOT the C# projection (IDrawable). The old scrape collected IDrawable and
        // could never match the ObjC @protocol name Drawable — an empty intersection by
        // construction. The manifest fixes it.
        var dir = CreateTempDirWithManifest("""
            {
              "schemaVersion": 1,
              "module": "M",
              "types": [
                { "swiftName": "Drawable", "objcRuntimeName": "Drawable", "projectedCSharpName": "IDrawable", "kind": "protocol" }
              ]
            }
            """);
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("Drawable", names);
            Assert.DoesNotContain("IDrawable", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectsCustomObjCRuntimeName_ForObjcRename()
    {
        // @objc(MOSWidget) class Widget — the manifest carries the custom runtime name, which the
        // ObjC -Swift.h declares the @interface under. The old scrape collected the C# name
        // Widget and missed the ObjC name MOSWidget entirely.
        var dir = CreateTempDirWithManifest("""
            {
              "schemaVersion": 1,
              "module": "M",
              "types": [
                { "swiftName": "Widget", "objcRuntimeName": "MOSWidget", "projectedCSharpName": "Widget", "kind": "class" }
              ]
            }
            """);
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("MOSWidget", names);
            Assert.DoesNotContain("Widget", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoManifest_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"collector_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Empty(names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingOutputDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"collector_missing_{Guid.NewGuid():N}");
        var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
        Assert.Empty(names);
    }

    [Fact]
    public void SchemaVersionMismatch_ThrowsLoudly()
    {
        var dir = CreateTempDirWithManifest("""
            {
              "schemaVersion": 999,
              "module": "M",
              "types": [
                { "swiftName": "Foo", "objcRuntimeName": "Foo", "projectedCSharpName": "Foo", "kind": "class" }
              ]
            }
            """);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => BindingsGenerator.CollectSwiftEmittedTypeNames(dir));
            Assert.Contains("SWIFTBIND105", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
