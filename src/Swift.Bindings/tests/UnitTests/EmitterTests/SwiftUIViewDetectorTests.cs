// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftUIViewDetector and SwiftUIBridgeCollector.
/// </summary>
public class SwiftUIViewDetectorTests
{
    #region IsSwiftUIView — StructDecl

    [Fact]
    public void IsSwiftUIView_True_SwiftUI_View()
    {
        var structDecl = CreateStructWithConformances("NoInternetView", new[] { "SwiftUI.View" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    [Fact]
    public void IsSwiftUIView_True_SwiftUICore_View()
    {
        var structDecl = CreateStructWithConformances("NoInternetView", new[] { "SwiftUICore.View" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    [Fact]
    public void IsSwiftUIView_False_NoConformance()
    {
        var structDecl = CreateStructWithConformances("PlainStruct", new[] { "Swift.Equatable" });

        Assert.False(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    [Fact]
    public void IsSwiftUIView_False_SwiftUI_Shape()
    {
        var structDecl = CreateStructWithConformances("MyShape", new[] { "SwiftUI.Shape" });

        Assert.False(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    [Fact]
    public void IsSwiftUIView_False_EmptyConformances()
    {
        var structDecl = CreateStructWithConformances("PlainStruct", Array.Empty<string>());

        Assert.False(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    [Fact]
    public void IsSwiftUIView_True_MultipleConformances()
    {
        var structDecl = CreateStructWithConformances("ViewStruct",
            new[] { "Swift.Equatable", "SwiftUI.View", "Swift.Sendable" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(structDecl));
    }

    #endregion

    #region IsSwiftUIView — ClassDecl

    [Fact]
    public void IsSwiftUIView_True_ClassDecl()
    {
        var classDecl = CreateClassWithConformances("ViewClass", new[] { "SwiftUI.View" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(classDecl));
    }

    [Fact]
    public void IsSwiftUIView_False_ClassDecl_NoConformance()
    {
        var classDecl = CreateClassWithConformances("PlainClass", new[] { "Swift.Equatable" });

        Assert.False(SwiftUIViewDetector.IsSwiftUIView(classDecl));
    }

    #endregion

    #region IsSwiftUIView — TypeDecl dispatch

    [Fact]
    public void IsSwiftUIView_TypeDecl_DispatchesToStruct()
    {
        TypeDecl typeDecl = CreateStructWithConformances("ViewStruct", new[] { "SwiftUI.View" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(typeDecl));
    }

    [Fact]
    public void IsSwiftUIView_TypeDecl_DispatchesToClass()
    {
        TypeDecl typeDecl = CreateClassWithConformances("ViewClass", new[] { "SwiftUI.View" });

        Assert.True(SwiftUIViewDetector.IsSwiftUIView(typeDecl));
    }

    [Fact]
    public void IsSwiftUIView_TypeDecl_ReturnsFalse_ForEnum()
    {
        TypeDecl typeDecl = new EnumDecl
        {
            Name = "MyEnum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyEnum"),
            MangledName = "$s10TestModule6MyEnumO",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6MyEnumOMa",
        };

        Assert.False(SwiftUIViewDetector.IsSwiftUIView(typeDecl));
    }

    #endregion

    #region SwiftUIBridgeCollector

    [Fact]
    public void BridgeCollector_CollectsViews()
    {
        var ctx = new ModuleEmissionContext();

        var view1 = CreateStructWithConformances("View1", new[] { "SwiftUI.View" });
        var view2 = CreateStructWithConformances("View2", new[] { "SwiftUI.View" });
        SwiftUIBridgeCollector.Collect(view1, ctx);
        SwiftUIBridgeCollector.Collect(view2, ctx);

        var collected = SwiftUIBridgeCollector.GetCollectedViews(ctx);
        Assert.Equal(2, collected.Count);
        Assert.Equal("View1", collected[0].Name);
        Assert.Equal("View2", collected[1].Name);
    }

    [Fact]
    public void BridgeCollector_FreshContextStartsEmpty()
    {
        var ctx = new ModuleEmissionContext();

        var view = CreateStructWithConformances("View1", new[] { "SwiftUI.View" });
        SwiftUIBridgeCollector.Collect(view, ctx);
        Assert.Single(SwiftUIBridgeCollector.GetCollectedViews(ctx));

        // A new context has its own independent state — no cross-run bleed.
        Assert.Empty(SwiftUIBridgeCollector.GetCollectedViews(new ModuleEmissionContext()));
    }

    [Fact]
    public void BridgeCollector_CollectsViews_IsolatedPerContext()
    {
        // Module 1
        var ctx1 = new ModuleEmissionContext();
        var view1 = CreateStructWithConformances("ViewA", new[] { "SwiftUI.View" });
        SwiftUIBridgeCollector.Collect(view1, ctx1);
        Assert.Single(SwiftUIBridgeCollector.GetCollectedViews(ctx1));

        // Module 2 — a separate context does not see module 1's views.
        var ctx2 = new ModuleEmissionContext();
        var view2 = CreateStructWithConformances("ViewB", new[] { "SwiftUI.View" });
        SwiftUIBridgeCollector.Collect(view2, ctx2);
        Assert.Single(SwiftUIBridgeCollector.GetCollectedViews(ctx2));
        Assert.Equal("ViewB", SwiftUIBridgeCollector.GetCollectedViews(ctx2)[0].Name);
    }

    #endregion

    #region BindingReport Schema

    [Fact]
    public void BindingReport_ContainsBridgedViewsList()
    {
        var report = new BindingReport { ModuleName = "TestModule" };

        report.BridgedViews.Add(new BridgedViewItem
        {
            ViewName = "NoInternetView",
            ModuleName = "DocScanUX",
            InitClassification = "Simple",
            BridgeStatus = "TemplatePending",
        });

        Assert.Single(report.BridgedViews);
        Assert.Equal("NoInternetView", report.BridgedViews[0].ViewName);
        Assert.Equal("DocScanUX", report.BridgedViews[0].ModuleName);
        Assert.Equal("Simple", report.BridgedViews[0].InitClassification);
        Assert.Equal("TemplatePending", report.BridgedViews[0].BridgeStatus);
    }

    [Fact]
    public void BindingReport_SerializesSwiftUIView_SkipReason()
    {
        var report = new BindingReport { ModuleName = "TestModule" };
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "NoInternetView",
            Reason = SkipReason.SwiftUIView,
            Details = "Type conforms to SwiftUI.View.",
        });

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(report,
            new Newtonsoft.Json.Converters.StringEnumConverter());

        Assert.Contains("SwiftUIView", json);
        Assert.Contains("NoInternetView", json);
    }

    #endregion

    #region Coexistence: SwiftUIConstraint still works for generic types

    [Fact]
    public void SwiftUIConstraint_StillWorks_ForGenericTypes()
    {
        // A generic type with SwiftUI.View constraint should NOT be caught by IsSwiftUIView
        // because the View conformance is on the generic parameter, not the type itself
        var structDecl = new StructDecl
        {
            Name = "GenericView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GenericView"),
            MangledName = "$s10TestModule11GenericViewV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(), // No direct View conformance
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule11GenericViewVMa",
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0", "T",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            }
        };

        // IsSwiftUIView checks direct conformances, not generic parameter constraints
        Assert.False(SwiftUIViewDetector.IsSwiftUIView(structDecl));

        // TryGetUnsupportedConstraint checks generic parameter constraints (existing path)
        Assert.True(GenericTypeEmitter.TryGetUnsupportedConstraint(structDecl, out var unsupportedConstraint));
        Assert.Equal("View", unsupportedConstraint!.Name);
    }

    #endregion

    #region Helpers

    private static StructDecl CreateStructWithConformances(string name, string[] protocolNames)
    {
        var conformances = protocolNames.Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformances,
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static ClassDecl CreateClassWithConformances(string name, string[] protocolNames)
    {
        var conformances = protocolNames.Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformances,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    #endregion
}
