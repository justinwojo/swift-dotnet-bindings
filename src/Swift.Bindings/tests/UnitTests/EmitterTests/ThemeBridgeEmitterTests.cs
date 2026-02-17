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
    public void ClassifyProperty_Null_ForUIKitColor()
    {
        // Phase 1: UIKit types not yet supported
        var kind = ThemeBridgeEmitter.ClassifyPropertyType(new NamedTypeSpec("UIKit.UIColor"));
        Assert.Null(kind);
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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var csPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        Assert.True(File.Exists(csPath));
        var content = File.ReadAllText(csPath);

        Assert.Contains("public partial class MyTheme", content);
        Assert.Contains("namespace Swift.TestModule", content);
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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var content = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

        Assert.Contains("[LibraryImport(", content);
        Assert.Contains("static partial void", content);
        Assert.Contains("[UnmanagedCallConv(CallConvs = new[]", content);
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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
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
        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nimport SwiftUI\n// existing content\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
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
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        var swiftAfterFirst = File.ReadAllText(swiftPath);
        var csAfterFirst = File.ReadAllText(csPath);

        // Simulate rerun: ModuleEmitter cleans up auto-generated files first
        SwiftUIBridgeEmitter.CleanupAutoGeneratedBridgeFiles(_tempDir, "Swift.TestModule", NullLogger.Instance);

        // Run #2
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
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
        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        File.WriteAllText(swiftPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nimport SwiftUI\nimport TestModule\n\n// view bridge content\n");
        File.WriteAllText(csPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nnamespace Swift.TestModule\n{\n    // view bridge content\n}\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("bgColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        // Run #1 (appends theme bridge to view bridge files)
        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        var swiftAfterFirst = File.ReadAllText(swiftPath);

        // Count @_cdecl occurrences — should be exactly 1
        var cdeclCount = CountOccurrences(swiftAfterFirst, "@_cdecl(\"SBW_MyTheme_set_bgColor\")");
        Assert.Equal(1, cdeclCount);
    }

    #endregion

    #region User-maintained file safety

    [Fact]
    public void EmitThemeBridge_SkipsAppend_WhenSwiftFileNotAutoGenerated()
    {
        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, "// User-maintained bridge file\nimport SwiftUI\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        var content = File.ReadAllText(swiftPath);
        // Original content preserved, no theme bridge appended
        Assert.Equal("// User-maintained bridge file\nimport SwiftUI\n", content);
        Assert.DoesNotContain("SBW_MyTheme_set_color", content);
    }

    [Fact]
    public void EmitThemeBridge_SkipsInsert_WhenCSharpFileNotAutoGenerated()
    {
        var csPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        File.WriteAllText(csPath, "// User-maintained bridge file\nnamespace Swift.TestModule { }\n");

        var themeInfos = new List<ThemeBridgeEmitter.ThemeBridgeInfo>
        {
            new("MyTheme", "TestModule", "shared", new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("color", ThemeBridgeEmitter.ThemePropertyKind.Color),
            })
        };

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: true, NullLogger.Instance);

        var content = File.ReadAllText(csPath);
        // Original content preserved, no theme bridge inserted
        Assert.Equal("// User-maintained bridge file\nnamespace Swift.TestModule { }\n", content);
        Assert.DoesNotContain("SetColor", content);
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

        ThemeBridgeEmitter.EmitThemeBridge(_tempDir, "Swift.TestModule", "TestModule", themeInfos,
            viewBridgeExists: false, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // Must use backtick-escaped `default`, not bare "default"
        Assert.Contains("MyTheme.`default`.bgColor", swiftContent);
        Assert.Contains("MyTheme.`default`.titleFont", swiftContent);
        // Must NOT contain bare ".default." (without backticks) for singleton access
        // (Note: ".default" also appears in SBW_fontDesign, so check the specific pattern)
        Assert.DoesNotContain("MyTheme.default.", swiftContent);
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
