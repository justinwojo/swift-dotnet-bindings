// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The platform bindings' types the managed carrier table lets a transition carry by value. No source
/// of ours declares them, so each entry is checked against the installed iOS reference assembly: a
/// struct entry must be exactly its NFloat fields, an enum entry must have the underlying type the
/// table names, and a mismatch entry must still be an enum, not the CGFloat value Swift passes.
/// </summary>
public class SwiftManagedCarrierPlatformTests
{
    [SkippableFact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reads a reference assembly's metadata through a MetadataLoadContext; nothing is trimmed or executed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reads a reference assembly's metadata through a MetadataLoadContext; nothing is trimmed or executed.")]
    public void PlatformCarriers_MatchThePlatformReferenceAssembly()
    {
        var references = ManagedReferenceContext.Resolve(PlatformInfoFactory.Create(ApplePlatform.iOS));
        Skip.IfNot(references.IsAvailable, "the iOS reference pack is not installed");

        var paths = references.ReferencePaths
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Runtime");
        var platform = context.LoadFromAssemblyPath(references.AppleReferencePath!);

        Type Find(string name) => platform.GetType(name) ?? throw new Xunit.Sdk.XunitException($"{name} is not in {platform.GetName().Name}");

        foreach (var (name, (layout, managedSize)) in SwiftManagedCarrierTable.PlatformEntries)
        {
            var type = Find(name);
            Assert.True(type.IsValueType && !type.IsEnum, $"{name} is a struct");
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(fields.Length == layout.Leaves.Length,
                $"{name}: {fields.Length} field(s), the table has {layout.Leaves.Length}");
            Assert.All(fields, f => Assert.Equal("System.Runtime.InteropServices.NFloat", f.FieldType.FullName));
            Assert.All(layout.Leaves, l => Assert.Equal(SwiftScalarKind.Double, l.Kind));
            Assert.Equal(fields.Length * 8, managedSize);
        }

        foreach (var (name, underlying) in SwiftManagedCarrierTable.PlatformEnums)
        {
            var type = Find(name);
            Assert.True(type.IsEnum, $"{name} is an enum");
            var keyword = type.GetEnumUnderlyingType().FullName switch
            {
                "System.UInt64" => "ulong",
                "System.Int64" => "long",
                "System.UInt32" => "uint",
                "System.Int32" => "int",
                var other => other,
            };
            Assert.Equal(underlying, keyword);
        }

        foreach (var name in SwiftManagedCarrierTable.PlatformMismatches.Keys)
        {
            var type = Find(name);
            Assert.True(type.IsEnum, $"{name} is still an enum, not the CGFloat-backed value Swift passes");
        }
    }

    [Fact]
    public void PlatformTables_DoNotOverlap()
    {
        var structs = SwiftManagedCarrierTable.PlatformEntries.Keys;
        var enums = SwiftManagedCarrierTable.PlatformEnums.Keys;
        var mismatches = SwiftManagedCarrierTable.PlatformMismatches.Keys;

        Assert.Empty(structs.Intersect(enums));
        Assert.Empty(structs.Intersect(mismatches));
        Assert.Empty(enums.Intersect(mismatches));
        Assert.Empty(structs.Intersect(SwiftManagedCarrierTable.Entries.Keys));
    }
}
