// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class TypeSkipPrePassTests
{
    [Fact]
    public void Run_GenericParentWithSwiftUIConstraint_MarksSelfAndNestedAsSkipped()
    {
        // Codex P2 regression: before the propagation fix the pre-pass only recorded
        // the top-level generic type. A signature referencing Parent.Nested (e.g. a
        // typealias or enum case) therefore passed the member gate because
        // ReportCollector.IsTypeSkipped("Module.Parent.Nested") returned false — the
        // nested type was never marked. The fix walks descendants and records each
        // with SkipReason.AncestorSkipped when an ancestor was skipped.
        var moduleDecl = BuildModuleWithSwiftUIConstrainedGenericAndNested();

        ReportCollector.Start(moduleDecl);
        try
        {
            TypeSkipPrePass.Run(moduleDecl, new EmptyTypeDatabase());

            Assert.True(
                ReportCollector.IsTypeSkipped("TestModule.Parent"),
                "Generic parent with SwiftUI constraint must be skipped");
            Assert.True(
                ReportCollector.IsTypeSkipped("TestModule.Parent.Nested"),
                "Nested type must be marked skipped because its ancestor is skipped");
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void Run_GenericParentWithSwiftUICoreConstraint_MarksAsSwiftUIConstraint()
    {
        // SwiftUICore is the internal split-out of SwiftUI in newer SDKs. It must be
        // suppressed identically — same skip reason, same propagation behavior — so
        // that downstream gates and the cogater inventory don't see drift between
        // SwiftUI.View and SwiftUICore.View.
        var moduleDecl = BuildModuleWithSwiftUIConstrainedGenericAndNested("SwiftUICore.View");

        ReportCollector.Start(moduleDecl);
        try
        {
            TypeSkipPrePass.Run(moduleDecl, new EmptyTypeDatabase());

            Assert.True(ReportCollector.IsTypeSkipped("TestModule.Parent"));
            Assert.True(ReportCollector.IsTypeSkipped("TestModule.Parent.Nested"));
            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var parentSkip = report!.SkippedItems
                .First(s => s.Kind == BindingItemKind.Type && s.Name == "Parent");
            Assert.Equal(SkipReason.SwiftUIConstraint, parentSkip.Reason);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void Run_NoUnsupportedConstraints_LeavesDescendantsUnmarked()
    {
        // Negative case: when the parent passes all skip predicates, no propagation
        // should occur — nested types remain free to be emitted normally.
        var moduleDecl = BuildModuleWithPlainParentAndNested();

        ReportCollector.Start(moduleDecl);
        try
        {
            TypeSkipPrePass.Run(moduleDecl, new EmptyTypeDatabase());

            Assert.False(ReportCollector.IsTypeSkipped("TestModule.Parent"));
            Assert.False(ReportCollector.IsTypeSkipped("TestModule.Parent.Nested"));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    private static ModuleDecl BuildModuleWithSwiftUIConstrainedGenericAndNested(
        string constraintModuleQualifiedName = "SwiftUI.View")
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
            AvailabilityAnnotations = null
        };

        var swiftUIConstraint = SwiftTypeName.FromModuleQualifiedName(constraintModuleQualifiedName);
        var genericParam = new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "T",
            GenericConformances: new List<GenericParameterConformance>
            {
                new(Path: new[] { "T" }, ConformanceTarget: swiftUIConstraint, Kind: ConformanceKind.Protocol),
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());

        var nestedStruct = new StructDecl
        {
            Name = "Nested",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent.Nested"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null
        };

        var parentStruct = new StructDecl
        {
            Name = "Parent",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl> { genericParam },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStruct },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null
        };
        nestedStruct.ParentDecl = parentStruct;

        moduleDecl.Types.Add(parentStruct);
        return moduleDecl;
    }

    private static ModuleDecl BuildModuleWithPlainParentAndNested()
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
            AvailabilityAnnotations = null
        };

        var nestedStruct = new StructDecl
        {
            Name = "Nested",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent.Nested"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null
        };

        var parentStruct = new StructDecl
        {
            Name = "Parent",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStruct },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null
        };
        nestedStruct.ParentDecl = parentStruct;

        moduleDecl.Types.Add(parentStruct);
        return moduleDecl;
    }

    private class EmptyTypeDatabase : ITypeDatabase
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
