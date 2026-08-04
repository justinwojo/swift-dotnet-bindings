// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The identity a type-level skip carries into the two report channels it writes: the structured
/// per-declaration <c>SkippedItems</c> list and the loud <c>// Unsupported:</c> comment-drop list.
/// </summary>
public class TypeSkipIdentityTests
{
    [Fact]
    public void TypeSkipCommentDrop_CarriesTheQualifiedIdentity_NotTheBareName()
    {
        // TypeSkipConditions stamps the subject on every match arm; the comment emitter has an
        // optional declId with a name-only fallback (no module, no containing-type chain). An arm
        // that forgets to forward the subject silently degrades the join key on the drop row while
        // the SkippedItems row beside it stays fully qualified — the two channels stop joining.
        var moduleDecl = BuildModuleWithVariadicNested("Outer");
        var nested = moduleDecl.Types[0].Types[0];

        ReportCollector.Start(moduleDecl);
        try
        {
            EmitSkip(nested);
            var report = ReportCollector.Complete();

            var drop = Assert.Single(report!.UnsupportedCommentDropDetails);
            Assert.NotNull(drop.DeclId);
            Assert.True(DeclId.TryParse(drop.DeclId, out var id), $"'{drop.DeclId}' must be a canonical DeclId");
            Assert.Equal("TestModule", id.Module);
            Assert.Equal("Outer", id.DeclPath);
            Assert.Equal(BindingItemKind.Type, id.Kind);
            Assert.Equal("Nested", id.Name);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void SameNamedNestedTypes_UnderDifferentParents_StayDistinctInBothChannels()
    {
        // The comment text qualifies the type by its enclosing-type path, so A.Nested and B.Nested
        // are distinct declarations in BOTH channels: each keeps its own drop row (with its own
        // DeclId) and its own SkippedItems row. Only a true same-type repeat collapses in the
        // description-keyed drop channel. Pinning both halves here keeps a future change to either
        // channel a deliberate decision rather than a silent regression.
        var moduleDecl = BuildModuleWithVariadicNested("A");
        AddVariadicNestedTo(moduleDecl, "B");
        var first = moduleDecl.Types[0].Types[0];
        var second = moduleDecl.Types[1].Types[0];

        ReportCollector.Start(moduleDecl);
        try
        {
            EmitSkip(first);
            EmitSkip(second);
            var report = ReportCollector.Complete();

            var dropPaths = report!.UnsupportedCommentDropDetails
                .Select(d => DeclId.TryParse(d.DeclId, out var dropId) ? dropId.DeclPath : null)
                .ToList();
            Assert.Equal(new[] { "A", "B" }, dropPaths.OrderBy(p => p, StringComparer.Ordinal));
            Assert.All(report.UnsupportedCommentDropDetails, d =>
                Assert.Contains(".Nested'", d.Description));

            var skipPaths = report.SkippedItems
                .Where(s => s.Kind == BindingItemKind.Type && s.Name == "Nested")
                .Select(s => DeclId.TryParse(s.DeclId, out var skipId) ? skipId.DeclPath : null)
                .ToList();
            Assert.Equal(new[] { "A", "B" }, skipPaths.OrderBy(p => p, StringComparer.Ordinal));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    /// <summary>
    /// Runs the real skip path — match the condition list, then emit through the handler-skip
    /// formatter — so the test observes the wiring rather than a hand-built id.
    /// </summary>
    private static void EmitSkip(TypeDecl typeDecl)
    {
        var match = TypeSkipConditions.FirstMatch(typeDecl, new SkipOnlyTypeDatabase(), out _);
        Assert.NotNull(match);
        Assert.Equal(TypeSkipConditionKind.VariadicGenericParameterPack, match!.Kind);

        var csWriter = new CSharpWriter(new StringWriter());
        TypeSkipConditions.EmitHandlerTypeSkip(csWriter, typeDecl, match, NullLogger.Instance);
    }

    private static ModuleDecl BuildModuleWithVariadicNested(string parentName)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null,
        };
        AddVariadicNestedTo(moduleDecl, parentName);
        return moduleDecl;
    }

    /// <summary>
    /// Adds <c>{parentName}.Nested</c>, where the NESTED type carries the variadic generic pack so
    /// it is the type that trips the skip condition. Both parents nest the same leaf name, which is
    /// what makes the comment text collide.
    /// </summary>
    private static void AddVariadicNestedTo(ModuleDecl moduleDecl, string parentName)
    {
        var nested = new StructDecl
        {
            Name = "Nested",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{parentName}.Nested"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(TypeName: "each T",
                    SugaredTypeName: "each T",
                    GenericConformances: new List<GenericParameterConformance>(),
                    AssosiatedTypeConformances: new List<GenericParameterConformance>()),
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null,
        };

        var parent = new StructDecl
        {
            Name = parentName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{parentName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nested },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null,
        };
        nested.ParentDecl = parent;
        moduleDecl.Types.Add(parent);
    }

    /// <summary>
    /// Resolves nothing. The variadic-pack condition is decl-only and matches before any arm
    /// consults the database, so an empty one is sufficient and keeps the fixture honest about
    /// which facts the skip decision actually reads.
    /// </summary>
    private sealed class SkipOnlyTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
