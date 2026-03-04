// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ThemeBridgeEmitter detection logic and emission output.
/// </summary>
public class ThemeBridgeEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public ThemeBridgeEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ThemeBridgeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    #region Detection — Singleton

    [Fact]
    public void FindSingleton_Shared()
    {
        var cls = CreateThemeClass("MyTheme", "shared", new[] { ("alertColor", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Equal("shared", name);
    }

    [Fact]
    public void FindSingleton_Default()
    {
        var cls = CreateThemeClass("MyTheme", "default", new[] { ("primaryColor", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Equal("default", name);
    }

    [Fact]
    public void FindSingleton_Current()
    {
        var cls = CreateThemeClass("MyTheme", "current", new[] { ("bgColor", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Equal("current", name);
    }

    [Fact]
    public void FindSingleton_SharedInstance()
    {
        var cls = CreateThemeClass("MyTheme", "sharedInstance", new[] { ("x", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Equal("sharedInstance", name);
    }

    [Fact]
    public void FindSingleton_Instance()
    {
        var cls = CreateThemeClass("MyTheme", "instance", new[] { ("x", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Equal("instance", name);
    }

    [Fact]
    public void FindSingleton_Null_WhenNoSingleton()
    {
        var cls = CreateClassWithProperties("MyTheme", null, new[] { ("color", "SwiftUI.Color") });
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Null(name);
    }

    [Fact]
    public void FindSingleton_Null_WhenWrongName()
    {
        var cls = CreateThemeClass("MyTheme", "main", new[] { ("color", "SwiftUI.Color") });
        // "main" isn't a recognized singleton name
        var name = ThemeBridgeEmitter.FindSingletonProperty(cls);
        Assert.Null(name);
    }

    #endregion

    #region Detection — Property Classification

    [Fact]
    public void ClassifyProperty_Color()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("SwiftUI.Color"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.Color, kind);
    }

    [Fact]
    public void ClassifyProperty_Font()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("SwiftUI.Font"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.Font, kind);
    }

    [Fact]
    public void ClassifyProperty_SwiftUICore_Color()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("SwiftUICore.Color"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.Color, kind);
    }

    [Fact]
    public void ClassifyProperty_SwiftUICore_Font()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("SwiftUICore.Font"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.Font, kind);
    }

    [Fact]
    public void ClassifyProperty_Null_ForString()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("Swift.String"));
        Assert.Null(kind);
    }

    [Fact]
    public void ClassifyProperty_UIKitColor()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("UIKit.UIColor"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.UIKitColor, kind);
    }

    [Fact]
    public void ClassifyProperty_UIKitFont()
    {
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("UIKit.UIFont"));
        Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.UIKitFont, kind);
    }

    [Fact]
    public void IsColorKind_TrueForBothColorKinds()
    {
        Assert.True(ThemeBridgeEmitter.IsColorKind(ThemeBridgeEmitter.ThemePropertyKind.Color));
        Assert.True(ThemeBridgeEmitter.IsColorKind(ThemeBridgeEmitter.ThemePropertyKind.UIKitColor));
        Assert.False(ThemeBridgeEmitter.IsColorKind(ThemeBridgeEmitter.ThemePropertyKind.Font));
        Assert.False(ThemeBridgeEmitter.IsColorKind(ThemeBridgeEmitter.ThemePropertyKind.UIKitFont));
    }

    [Fact]
    public void IsFontKind_TrueForBothFontKinds()
    {
        Assert.True(ThemeBridgeEmitter.IsFontKind(ThemeBridgeEmitter.ThemePropertyKind.Font));
        Assert.True(ThemeBridgeEmitter.IsFontKind(ThemeBridgeEmitter.ThemePropertyKind.UIKitFont));
        Assert.False(ThemeBridgeEmitter.IsFontKind(ThemeBridgeEmitter.ThemePropertyKind.Color));
        Assert.False(ThemeBridgeEmitter.IsFontKind(ThemeBridgeEmitter.ThemePropertyKind.UIKitColor));
    }

    #endregion

    #region Detection — Full Analysis

    [Fact]
    public void AnalyzeClass_DetectsTheme_WithColorProperties()
    {
        var cls = CreateThemeClass("MyTheme", "shared", new[]
        {
            ("alertTitleColor", "SwiftUI.Color"),
            ("alertButtonColor", "SwiftUI.Color"),
        });

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        Assert.NotNull(info);
        Assert.Equal("MyTheme", info.ClassName);
        Assert.Equal("shared", info.SingletonName);
        Assert.Equal(2, info.Properties.Count);
        Assert.All(info.Properties, p => Assert.Equal(ThemeBridgeEmitter.ThemePropertyKind.Color, p.Kind));
    }

    [Fact]
    public void AnalyzeClass_DetectsTheme_WithMixedProperties()
    {
        var cls = CreateThemeClass("MyTheme", "shared", new[]
        {
            ("titleColor", "SwiftUI.Color"),
            ("titleFont", "SwiftUI.Font"),
        });

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        Assert.NotNull(info);
        Assert.Equal(2, info.Properties.Count);
        Assert.Contains(info.Properties, p => p.Kind == ThemeBridgeEmitter.ThemePropertyKind.Color);
        Assert.Contains(info.Properties, p => p.Kind == ThemeBridgeEmitter.ThemePropertyKind.Font);
    }

    [Fact]
    public void AnalyzeClass_SkipsStaticProperties()
    {
        // Static Color properties should not be included (they're not instance properties)
        var cls = CreateThemeClass("MyTheme", "shared", new[]
        {
            ("instanceColor", "SwiftUI.Color"),
        });
        // Add a static Color property
        cls.Properties.Add(CreateProperty("staticColor", "SwiftUI.Color", isStatic: true, hasSetter: true));

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        Assert.NotNull(info);
        Assert.Single(info.Properties);
        Assert.Equal("instanceColor", info.Properties[0].Name);
    }

    [Fact]
    public void AnalyzeClass_SkipsReadOnlyProperties()
    {
        var cls = CreateClassWithProperties("MyTheme", "shared", new[]
        {
            ("readOnlyColor", "SwiftUI.Color"),
        }, hasSetter: false);

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        // No settable Color/Font properties → null
        Assert.Null(info);
    }

    [Fact]
    public void AnalyzeClass_ReturnsNull_ForNoSingleton()
    {
        var cls = CreateClassWithProperties("MyTheme", null, new[]
        {
            ("color", "SwiftUI.Color"),
        });

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");
        Assert.Null(info);
    }

    [Fact]
    public void AnalyzeClass_ReturnsNull_ForNoBridgeableProperties()
    {
        var cls = CreateThemeClass("MyTheme", "shared", Array.Empty<(string, string)>());
        // Add a non-bridgeable property
        cls.Properties.Add(CreateProperty("title", "Swift.String", isStatic: false, hasSetter: true));

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");
        Assert.Null(info);
    }

    [Fact]
    public void DetectThemeBridgeableTypes_FromModule()
    {
        var moduleDecl = CreateModuleWithThemeClass("TestModule", "MyTheme", "shared", new[]
        {
            ("bgColor", "SwiftUI.Color"),
            ("headerFont", "SwiftUI.Font"),
        });

        var results = ThemeBridgeEmitter.DetectThemeBridgeableTypes(moduleDecl);

        Assert.Single(results);
        Assert.Equal("MyTheme", results[0].ClassName);
        Assert.Equal(2, results[0].Properties.Count);
    }

    [Fact]
    public void DetectThemeBridgeableTypes_IgnoresStructs()
    {
        // Only classes qualify — structs don't use singleton patterns
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>
            {
                // A struct with shared + Color (shouldn't match because it's not a class)
                new StructDecl
                {
                    Name = "ThemeStruct",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ThemeStruct"),
                    MangledName = "$s10TestModule11ThemeStructV",
                    Properties = new List<PropertyDecl>
                    {
                        CreateProperty("shared", "TestModule.ThemeStruct", isStatic: true, hasSetter: false),
                        CreateProperty("color", "SwiftUI.Color", isStatic: false, hasSetter: true),
                    },
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Conformances = new List<TypeConformance>(),
                    ParentDecl = null,
                    ModuleDecl = null,
                    IsFrozen = false,
                    MetadataAccessor = "",
                },
            },
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var results = ThemeBridgeEmitter.DetectThemeBridgeableTypes(moduleDecl);
        Assert.Empty(results);
    }

    #endregion

    #region Swift Emission

    [Fact]
    public void EmitThemeBridge_SwiftFile_ContainsCdeclSetters()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        Assert.True(File.Exists(swiftPath));
        var content = File.ReadAllText(swiftPath);

        Assert.Contains("@_cdecl(\"SBW_MyTheme_set_alertColor\")", content);
        Assert.Contains("Color(red: r, green: g, blue: b, opacity: a)", content);
        Assert.Contains("MyTheme.shared.alertColor =", content);
        Assert.Contains("SBW_onMainThread", content);

        Assert.Contains("@_cdecl(\"SBW_MyTheme_set_titleFont\")", content);
        Assert.Contains("SBW_fontWeight(weight)", content);
        Assert.Contains("SBW_fontDesign(design)", content);
        Assert.Contains("MyTheme.shared.titleFont = font", content);
    }

    [Fact]
    public void EmitThemeBridge_SwiftFile_ContainsFontHelpers()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("func SBW_fontWeight(_ raw: Int32) -> Font.Weight", content);
        Assert.Contains("func SBW_fontDesign(_ raw: Int32) -> Font.Design", content);
    }

    [Fact]
    public void EmitThemeBridge_SwiftFile_NoFontHelpers_WhenOnlyColors()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.DoesNotContain("SBW_fontWeight", content);
        Assert.DoesNotContain("SBW_fontDesign", content);
    }

    [Fact]
    public void EmitThemeBridge_Swift_DefensiveFontChecks_NoForceUnwrap()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // Defensive: uses if let, not force unwrap
        Assert.Contains("if let namePtr = namePtr", content);
        Assert.DoesNotContain("namePtr!", content);
        // Fallback to system font
        Assert.Contains("font = .system(size: CGFloat(size))", content);
    }

    #endregion

    #region C# Emission

    [Fact]
    public void EmitThemeBridge_CSharpFile_ContainsPartialClass()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        Assert.True(File.Exists(csPath));
        var content = File.ReadAllText(csPath);

        Assert.Contains("public partial class MyTheme", content);
        Assert.Contains("namespace TestModule", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharpFile_ColorSetter()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("public static void SetAlertColor(Swift.SwiftColor value)", content);
        Assert.Contains("value.R, value.G, value.B, value.A", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharpFile_FontSetter()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("public static unsafe void SetTitleFont(Swift.SwiftFont value)", content);
        Assert.Contains("fixed (byte* namePtr = nameBytes)", content);
        Assert.Contains("value.IsSystem ? 1 : 0", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharpFile_PInvokeSignatures()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("[LibraryImport(\"TestModuleSwiftBindings\"", content);
        Assert.Contains("ThemeBridgeNativeMethods", content);
        Assert.Contains("EntryPoint = \"SBW_MyTheme_set_alertColor\"", content);
        Assert.Contains("double r, double g, double b, double a", content);

        Assert.Contains("EntryPoint = \"SBW_MyTheme_set_titleFont\"", content);
        Assert.Contains("byte* namePtr, nint nameLen", content);
        Assert.Contains("double size, int weight, int design, int isSystem", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharpFile_UsesLibraryImport()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("[LibraryImport(", content);
        Assert.Contains("static partial void", content);
        Assert.Contains("[UnmanagedCallConv(CallConvs = new Type[]", content);
        Assert.Contains("typeof(CallConvCdecl)", content);
        // Must NOT use DllImport
        Assert.DoesNotContain("[DllImport(", content);
    }

    #endregion

    #region Standalone Bridge (no views)

    [Fact]
    public void EmitThemeBridge_CreatesStandaloneFiles_WhenNoViews()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        Assert.True(File.Exists(swiftPath));
        var swiftContent = File.ReadAllText(swiftPath);

        // Should include SBW_onMainThread even without views
        Assert.Contains("SBW_onMainThread", swiftContent);
        Assert.Contains("import SwiftUI", swiftContent);
        Assert.Contains("import TestModule", swiftContent);
    }

    #endregion

    #region Append to existing bridge files

    [Fact]
    public void EmitThemeBridge_AppendsToExistingSwiftFile()
    {
        // Simulate existing bridge file from view bridge
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nimport SwiftUI\n// existing content\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        var content = File.ReadAllText(swiftPath);
        // Original content preserved
        Assert.Contains("// existing content", content);
        // Theme bridge appended
        Assert.Contains("SBW_MyTheme_set_color", content);
    }

    #endregion

    #region Idempotency — no duplicate emission on rerun

    [Fact]
    public void EmitThemeBridge_IsIdempotent_StandaloneTheme()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        // Run #1
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        var swiftAfterFirst = File.ReadAllText(swiftPath);
        var csAfterFirst = File.ReadAllText(csPath);

        // Simulate rerun: ModuleEmitter cleans up auto-generated files first
        SwiftUIBridgeEmitter.CleanupAutoGeneratedBridgeFiles(_tempDir, "TestModule", NullLogger.Instance);

        // Run #2
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftAfterSecond = File.ReadAllText(swiftPath);
        var csAfterSecond = File.ReadAllText(csPath);

        // Content should be identical — no duplication
        Assert.Equal(swiftAfterFirst, swiftAfterSecond);
        Assert.Equal(csAfterFirst, csAfterSecond);
    }

    [Fact]
    public void EmitThemeBridge_IsIdempotent_WithViewBridge()
    {
        // Simulate view bridge creating the file first
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        File.WriteAllText(swiftPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nimport SwiftUI\nimport TestModule\n\n// view bridge content\n");
        File.WriteAllText(csPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nnamespace TestModule\n{\n    // view bridge content\n}\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        // Run #1 (appends theme bridge to view bridge files)
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        var swiftAfterFirst = File.ReadAllText(swiftPath);

        // Count @_cdecl occurrences — should be exactly 1
        var cdeclCount = CountOccurrences(swiftAfterFirst, "@_cdecl(\"SBW_MyTheme_set_bgColor\")");
        Assert.Equal(1, cdeclCount);
    }

    #endregion

    #region User-maintained file safety (pair-level)

    [Fact]
    public void EmitThemeBridge_SkipsBoth_WhenSwiftFileNotAutoGenerated()
    {
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, "// User-maintained bridge file\nimport SwiftUI\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        // Swift file preserved
        var swiftContent = File.ReadAllText(swiftPath);
        Assert.Equal("// User-maintained bridge file\nimport SwiftUI\n", swiftContent);
        Assert.DoesNotContain("SBW_MyTheme_set_color", swiftContent);

        // C# file must NOT be created (pair-level skip)
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        Assert.False(File.Exists(csPath), "C# bridge should not be created when Swift file is user-maintained");
    }

    [Fact]
    public void EmitThemeBridge_SkipsBoth_WhenCSharpFileNotAutoGenerated()
    {
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        File.WriteAllText(csPath, "// User-maintained bridge file\nnamespace TestModule { }\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        // C# file preserved
        var csContent = File.ReadAllText(csPath);
        Assert.Equal("// User-maintained bridge file\nnamespace TestModule { }\n", csContent);
        Assert.DoesNotContain("SetColor", csContent);

        // Swift file must NOT be created (pair-level skip)
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        Assert.False(File.Exists(swiftPath), "Swift bridge should not be created when C# file is user-maintained");
    }

    [Fact]
    public void EmitThemeBridge_SkipsBoth_WhenUserSwiftExists_CSharpMissing()
    {
        // User-maintained Swift file + no C# file → must skip both
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, "// Custom hand-written bridge\nimport SwiftUI\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        // Swift untouched
        Assert.Equal("// Custom hand-written bridge\nimport SwiftUI\n", File.ReadAllText(swiftPath));
        // C# never created — no orphan P/Invokes
        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void EmitThemeBridge_SkipsBoth_WhenUserCSharpExists_SwiftMissing()
    {
        // User-maintained C# file + no Swift file → must skip both
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        File.WriteAllText(csPath, "// Custom hand-written bridge\nnamespace TestModule { }\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        // C# untouched
        Assert.Equal("// Custom hand-written bridge\nnamespace TestModule { }\n", File.ReadAllText(csPath));
        // Swift never created — no orphan @_cdecl symbols
        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift")));
    }

    [Fact]
    public void EmitThemeBridge_SkipsBoth_WhenBothUserMaintained()
    {
        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        File.WriteAllText(swiftPath, "// Custom Swift bridge\n");
        File.WriteAllText(csPath, "// Custom C# bridge\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        Assert.Equal("// Custom Swift bridge\n", File.ReadAllText(swiftPath));
        Assert.Equal("// Custom C# bridge\n", File.ReadAllText(csPath));
    }

    #endregion

    #region Swift keyword escaping

    [Fact]
    public void EscapeSwiftKeyword_EscapesDefault()
    {
        Assert.Equal("`default`", ThemeBridgeEmitter.EscapeSwiftKeyword("default"));
    }

    [Fact]
    public void EscapeSwiftKeyword_DoesNotEscapeShared()
    {
        Assert.Equal("shared", ThemeBridgeEmitter.EscapeSwiftKeyword("shared"));
    }

    [Fact]
    public void EscapeSwiftKeyword_DoesNotEscapeCurrent()
    {
        Assert.Equal("current", ThemeBridgeEmitter.EscapeSwiftKeyword("current"));
    }

    [Fact]
    public void EmitThemeBridge_UsesBackticks_ForDefaultSingleton()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "default", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // Must use backtick-escaped `default`, not bare "default"
        Assert.Contains("MyTheme.`default`.bgColor", swiftContent);
        Assert.Contains("MyTheme.`default`.titleFont", swiftContent);
        // Must NOT contain bare ".default." (without backticks) for singleton access
        // (Note: ".default" also appears in SBW_fontDesign, so check the specific pattern)
        Assert.DoesNotContain("MyTheme.default.", swiftContent);
    }

    #endregion

    #region UIKit — Swift Emission

    [Fact]
    public void EmitThemeBridge_UIKitColor_SwiftSetter_UsesUIColorConstructor()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("@_cdecl(\"SBW_MyTheme_set_primaryColor\")", content);
        Assert.Contains("UIColor(red: CGFloat(r), green: CGFloat(g), blue: CGFloat(b), alpha: CGFloat(a))", content);
        Assert.Contains("MyTheme.shared.primaryColor =", content);
        // Should NOT contain SwiftUI Color constructor
        Assert.DoesNotContain("Color(red: r, green: g, blue: b, opacity: a)", content);
    }

    [Fact]
    public void EmitThemeBridge_UIKitFont_SwiftSetter_UsesUIFontConstructor()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("loadingFont", ThemeBridgeEmitter.ThemePropertyKind.UIKitFont),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("@_cdecl(\"SBW_MyTheme_set_loadingFont\")", content);
        Assert.Contains("let font: UIFont", content);
        Assert.Contains("UIFont.systemFont(ofSize: CGFloat(size), weight: SBW_uiFontWeight(weight))", content);
        Assert.Contains("UIFont(name: name, size: CGFloat(size))", content);
        Assert.Contains("MyTheme.shared.loadingFont = font", content);
        // Should NOT contain SwiftUI Font constructor
        Assert.DoesNotContain("let font: Font", content);
    }

    [Fact]
    public void EmitThemeBridge_UIKitFont_EmitsUIFontWeightHelper()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("loadingFont", ThemeBridgeEmitter.ThemePropertyKind.UIKitFont),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("func SBW_uiFontWeight(_ raw: Int32) -> UIFont.Weight", content);
        // Should NOT contain SwiftUI font helpers (no SwiftUI fonts)
        Assert.DoesNotContain("SBW_fontWeight", content);
        Assert.DoesNotContain("SBW_fontDesign", content);
    }

    [Fact]
    public void EmitThemeBridge_UIKitColor_NoFontHelpers()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.DoesNotContain("SBW_fontWeight", content);
        Assert.DoesNotContain("SBW_fontDesign", content);
        Assert.DoesNotContain("SBW_uiFontWeight", content);
    }

    #endregion

    #region UIKit — C# Emission

    [Fact]
    public void EmitThemeBridge_UIKitColor_CSharp_SameAsSwiftUIColor()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        // Same C# API shape as SwiftUI.Color — uses SwiftColor
        Assert.Contains("public static void SetPrimaryColor(Swift.SwiftColor value)", content);
        Assert.Contains("value.R, value.G, value.B, value.A", content);
        Assert.Contains("double r, double g, double b, double a", content);
    }

    [Fact]
    public void EmitThemeBridge_UIKitFont_CSharp_SameAsSwiftUIFont()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("loadingFont", ThemeBridgeEmitter.ThemePropertyKind.UIKitFont),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("public static unsafe void SetLoadingFont(Swift.SwiftFont value)", content);
        Assert.Contains("fixed (byte* namePtr = nameBytes)", content);
        Assert.Contains("byte* namePtr, nint nameLen", content);
    }

    #endregion

    #region Mixed UIKit/SwiftUI

    [Fact]
    public void EmitThemeBridge_MixedUIKitSwiftUI_BothHelpersEmitted()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
                new("loadingFont", ThemeBridgeEmitter.ThemePropertyKind.UIKitFont),
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("alertFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // Both font helper sets emitted
        Assert.Contains("SBW_fontWeight", swiftContent);
        Assert.Contains("SBW_fontDesign", swiftContent);
        Assert.Contains("SBW_uiFontWeight", swiftContent);

        // Both color constructor styles
        Assert.Contains("UIColor(red: CGFloat(r)", swiftContent);
        Assert.Contains("Color(red: r, green: g, blue: b, opacity: a)", swiftContent);

        // Both font constructor styles
        Assert.Contains("let font: Font", swiftContent);
        Assert.Contains("let font: UIFont", swiftContent);
    }

    [Fact]
    public void EmitThemeBridge_MixedUIKitSwiftUI_CSharp_AllMethodsPresent()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
                new("loadingFont", ThemeBridgeEmitter.ThemePropertyKind.UIKitFont),
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
                new("alertFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("SetPrimaryColor(Swift.SwiftColor value)", content);
        Assert.Contains("SetLoadingFont(Swift.SwiftFont value)", content);
        Assert.Contains("SetAlertColor(Swift.SwiftColor value)", content);
        Assert.Contains("SetAlertFont(Swift.SwiftFont value)", content);
    }

    [Fact]
    public void AnalyzeClass_DetectsUIKitProperties()
    {
        var cls = CreateThemeClass("MyTheme", "shared", new[]
        {
            ("primaryColor", "UIKit.UIColor"),
            ("loadingFont", "UIKit.UIFont"),
        });

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        Assert.NotNull(info);
        Assert.Equal(2, info.Properties.Count);
        Assert.Contains(info.Properties, p => p.Kind == ThemeBridgeEmitter.ThemePropertyKind.UIKitColor);
        Assert.Contains(info.Properties, p => p.Kind == ThemeBridgeEmitter.ThemePropertyKind.UIKitFont);
    }

    [Fact]
    public void AnalyzeClass_DetectsMixedUIKitAndSwiftUI()
    {
        var cls = CreateThemeClass("MyTheme", "shared", new[]
        {
            ("primaryColor", "UIKit.UIColor"),
            ("alertColor", "SwiftUI.Color"),
            ("loadingFont", "UIKit.UIFont"),
            ("alertFont", "SwiftUI.Font"),
        });

        var info = ThemeBridgeEmitter.AnalyzeClassForThemeBridge(cls, "TestModule");

        Assert.NotNull(info);
        Assert.Equal(4, info.Properties.Count);
    }

    #endregion

    #region Color Getters

    [Fact]
    public void EmitThemeBridge_Swift_ColorGetters_SwiftUI()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("@_cdecl(\"SBW_MyTheme_get_alertColor\")", content);
        Assert.Contains("UnsafeMutablePointer<Double>", content);
        // SwiftUI.Color getter converts to UIColor first
        Assert.Contains("let uiColor = UIColor(MyTheme.shared.alertColor)", content);
        Assert.Contains("uiColor.getRed(&r, &g, &b, &a)", content);
        Assert.Contains("rOut.pointee = Double(r)", content);
    }

    [Fact]
    public void EmitThemeBridge_Swift_ColorGetters_UIKit()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.UIKitColor),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        Assert.Contains("@_cdecl(\"SBW_MyTheme_get_primaryColor\")", content);
        // UIColor getter reads RGBA directly (no UIColor conversion)
        Assert.Contains("MyTheme.shared.primaryColor.getRed(&r, &g, &b, &a)", content);
        Assert.DoesNotContain("let uiColor = UIColor(MyTheme.shared.primaryColor)", content);
    }

    [Fact]
    public void EmitThemeBridge_Swift_NoGetters_ForFontOnly()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // No getter section for font-only themes
        Assert.DoesNotContain("theme getters", content);
        Assert.DoesNotContain("_get_", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharp_ColorGetterMethod()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("public static unsafe Swift.SwiftColor GetAlertColor()", content);
        Assert.Contains("ThemeBridgeNativeMethods.SBW_MyTheme_get_alertColor(&r, &g, &b, &a)", content);
        Assert.Contains("return new Swift.SwiftColor(r, g, b, a)", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharp_GetterPInvoke()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("alertColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.Contains("EntryPoint = \"SBW_MyTheme_get_alertColor\"", content);
        Assert.Contains("double* r, double* g, double* b, double* a", content);
        Assert.Contains("[LibraryImport(", content);
    }

    [Fact]
    public void EmitThemeBridge_CSharp_NoGetterForFont()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("titleFont", ThemeBridgeEmitter.ThemePropertyKind.Font),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        Assert.DoesNotContain("GetTitleFont", content);
        Assert.DoesNotContain("_get_titleFont", content);
    }

    [Fact]
    public void EmitThemeBridge_ColorGetters_UsesBackticks_ForDefaultSingleton()
    {
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "default", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // Getter should also use backtick-escaped singleton
        Assert.Contains("UIColor(MyTheme.`default`.bgColor)", content);
    }

    #endregion

    #region Report Integration

    [Fact]
    public void EmitThemeBridge_RecordsInReport()
    {
        var moduleDecl = CreateModuleWithThemeClass("TestModule", "MyTheme", "shared", new[]
        {
            ("alertColor", "SwiftUI.Color"),
            ("titleFont", "SwiftUI.Font"),
        });

        ReportCollector.Start(moduleDecl);
        try
        {
            var themeInfos = ThemeBridgeEmitter.DetectThemeBridgeableTypes(moduleDecl);

            ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
                viewBridgeExists: false, NullLogger.Instance);

            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            Assert.Equal(2, report.ThemeBridgedProperties.Count);
            Assert.Contains(report.ThemeBridgedProperties,
                p => p.ClassName == "MyTheme" && p.PropertyName == "alertColor" && p.PropertyType == "Color");
            Assert.Contains(report.ThemeBridgedProperties,
                p => p.ClassName == "MyTheme" && p.PropertyName == "titleFont" && p.PropertyType == "Font");
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void EmitThemeBridge_RecordsUIKitInReport()
    {
        var moduleDecl = CreateModuleWithThemeClass("TestModule", "MyTheme", "shared", new[]
        {
            ("primaryColor", "UIKit.UIColor"),
            ("loadingFont", "UIKit.UIFont"),
        });

        ReportCollector.Start(moduleDecl);
        try
        {
            var themeInfos = ThemeBridgeEmitter.DetectThemeBridgeableTypes(moduleDecl);

            ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
                viewBridgeExists: false, NullLogger.Instance);

            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            Assert.Equal(2, report.ThemeBridgedProperties.Count);
            Assert.Contains(report.ThemeBridgedProperties,
                p => p.PropertyType == "UIKitColor");
            Assert.Contains(report.ThemeBridgedProperties,
                p => p.PropertyType == "UIKitFont");
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void EmitThemeBridge_NoReportRecording_WhenNoSession()
    {
        // Verify no crash when reporting is not active
        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        // Should not throw — ReportCollector.RecordThemeBridged is no-op when not active
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);
    }

    #endregion

    #region Helpers

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static ClassDecl CreateThemeClass(string name, string? singletonName, (string Name, string TypeName)[] properties)
    {
        return CreateClassWithProperties(name, singletonName, properties, hasSetter: true);
    }

    private static ClassDecl CreateClassWithProperties(string name, string? singletonName, (string Name, string TypeName)[] properties, bool hasSetter = true)
    {
        var props = new List<PropertyDecl>();

        if (singletonName != null)
        {
            props.Add(CreateProperty(singletonName, $"TestModule.{name}", isStatic: true, hasSetter: false));
        }

        foreach (var (propName, typeName) in properties)
        {
            props.Add(CreateProperty(propName, typeName, isStatic: false, hasSetter: hasSetter));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = props,
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static MethodDecl CreateDummyMethod(string name, bool isStatic) => new MethodDecl
    {
        Name = name,
        MangledName = $"$s_{name}",
        CSSignature = new List<ArgumentDecl>(),
        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
        IsConstructor = false,
        Throws = false,
        IsAsync = false,
        GenericParameters = new List<GenericArgumentDecl>(),
        Visibility = Visibility.Public,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static PropertyDecl CreateProperty(string name, string typeName, bool isStatic, bool hasSetter)
    {
        var accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl { Method = CreateDummyMethod($"{name}_getter", isStatic) },
        };

        if (hasSetter)
        {
            accessors.Add(new SetAccessorDecl { Method = CreateDummyMethod($"{name}_setter", isStatic) });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static ModuleDecl CreateModuleWithThemeClass(string moduleName, string className, string? singletonName, (string Name, string TypeName)[] properties)
    {
        var cls = CreateThemeClass(className, singletonName, properties);
        return new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { cls },
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    #endregion
}
