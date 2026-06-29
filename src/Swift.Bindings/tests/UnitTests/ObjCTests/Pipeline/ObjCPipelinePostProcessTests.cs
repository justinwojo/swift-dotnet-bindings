// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for ObjC pipeline post-processing passes:
/// delegate protocol detection and platform type stub filtering.
/// </summary>
public class ObjCPipelinePostProcessTests
{
    // ──────────────────────────────────────────────
    // Delegate protocol detection (Fix #7)
    // ──────────────────────────────────────────────

    [Fact]
    public void DetectDelegateProtocols_NameBasedDelegate_Detected()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl { Name = "MyViewDelegate" },
                new ObjCProtocolDecl { Name = "SomeOtherProtocol" }
            ]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
        Assert.False(result.Protocols[1].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_NameBasedDataSource_Detected()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl { Name = "UITableViewDataSource" },
                new ObjCProtocolDecl { Name = "Configurable" }
            ]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
        Assert.False(result.Protocols[1].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_UsageBased_DetectedFromClassProperty()
    {
        // Protocol "MyObserver" doesn't end with Delegate or DataSource,
        // but it's used as the type of a "delegate" property on a class.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyObserver" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyObserver"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_UsageBased_DataSourceProperty()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyProvider" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyTable",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "dataSource",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyProvider"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_UsageBased_DirectTypeName()
    {
        // Protocol used as a direct pointer type (not id<Proto>)
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "Listener" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "Listener",
                        IsPointer = true
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_NonDelegatePropertyName_NotDetected()
    {
        // A property named "handler" (not "delegate" or "dataSource")
        // should not trigger delegate detection.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "EventHandler" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "handler",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["EventHandler"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        // "EventHandler" doesn't end with Delegate/DataSource
        // and is NOT used as a "delegate" or "dataSource" property
        Assert.False(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_AlreadyFlagged_NoChange()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "CustomDelegate",
                IsDelegateProtocol = true
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
        // Should be same reference since no changes needed
        Assert.Same(module, result);
    }

    [Fact]
    public void DetectDelegateProtocols_NoProtocols_ReturnsSameModule()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [],
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.Same(module, result);
    }

    [Fact]
    public void DetectDelegateProtocols_ProtocolQualifiedNavigationDelegate_Detected()
    {
        // A property named "navigationDelegate" with protocol-qualified type
        // referencing WKNavigationDelegate — should be detected by protocol name suffix.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "WKNavigationDelegate" }],
            Classes = [new ObjCClassDecl
            {
                Name = "WKWebView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "navigationDelegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["WKNavigationDelegate"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_DirectTypeDelegateProperty_Detected()
    {
        // A property with a direct pointer type whose name ends with Delegate
        // (no protocol qualifications) — should be detected.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "DownloadDelegate" }],
            Classes = [new ObjCClassDecl
            {
                Name = "Downloader",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "downloadDelegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "DownloadDelegate",
                        IsPointer = true
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void IsDelegateProperty_ExactDelegateName_ReturnsTrue()
    {
        var prop = new ObjCPropertyDecl
        {
            Name = "delegate",
            Type = new ObjCTypeRef { Name = "id", IsPointer = true }
        };
        Assert.True(ObjCPipeline.IsDelegateProperty(prop));
    }

    [Fact]
    public void IsDelegateProperty_ExactDataSourceName_ReturnsTrue()
    {
        var prop = new ObjCPropertyDecl
        {
            Name = "dataSource",
            Type = new ObjCTypeRef { Name = "id", IsPointer = true }
        };
        Assert.True(ObjCPipeline.IsDelegateProperty(prop));
    }

    [Fact]
    public void IsDelegateProperty_ProtocolQualifiedDelegateSuffix_ReturnsTrue()
    {
        var prop = new ObjCPropertyDecl
        {
            Name = "UIDelegate",
            Type = new ObjCTypeRef
            {
                Name = "id",
                IsPointer = true,
                ProtocolQualifications = ["WKUIDelegate"]
            }
        };
        Assert.True(ObjCPipeline.IsDelegateProperty(prop));
    }

    [Fact]
    public void IsDelegateProperty_NonDelegatePropertyNonDelegateProtocol_ReturnsFalse()
    {
        var prop = new ObjCPropertyDecl
        {
            Name = "handler",
            Type = new ObjCTypeRef
            {
                Name = "id",
                IsPointer = true,
                ProtocolQualifications = ["EventHandler"]
            }
        };
        Assert.False(ObjCPipeline.IsDelegateProperty(prop));
    }

    [Fact]
    public void DetectDelegateProtocols_MultiProtocolQualification_PicksDelegateSuffix()
    {
        // id<NSObject, MyObserver> — should extract "MyObserver" not "NSObject"
        // when MyObserver doesn't end with Delegate/DataSource but is the non-first protocol.
        // Actually for usage-based detection, ExtractProtocolNameFromType prefers *Delegate/*DataSource suffix.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "CustomDelegate" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["NSCopying", "CustomDelegate"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        // Should detect CustomDelegate (suffix match) not NSCopying (first element)
        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_MultiProtocolNoSuffix_RecordsAllNonMarker()
    {
        // id<MyObserver, Configurable> — neither ends with Delegate/DataSource,
        // and neither is a marker protocol, so both are recorded.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl { Name = "MyObserver" },
                new ObjCProtocolDecl { Name = "Configurable" }
            ],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyObserver", "Configurable"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        // Both non-marker protocols are recorded
        Assert.True(result.Protocols[0].IsDelegateProtocol);
        Assert.True(result.Protocols[1].IsDelegateProtocol);
    }

    [Fact]
    public void DetectDelegateProtocols_MarkerPlusNonSuffix_SkipsMarker()
    {
        // id<NSObject, MyObserver> — NSObject is a marker, MyObserver is the real one.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyObserver" }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["NSObject", "MyObserver"]
                    }
                }]
            }]
        };

        var result = ObjCPipeline.DetectDelegateProtocols(module, Logger);

        Assert.True(result.Protocols[0].IsDelegateProtocol);
    }

    // ──────────────────────────────────────────────
    // Platform type stub filtering (Fix #5)
    // ──────────────────────────────────────────────

    [Fact]
    public void FilterPlatformTypeStubs_RemovesAppleSdkClasses()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string>
            {
                ["UNNotificationContent"] = "",
                ["UNMutableNotificationContent"] = "",
                ["UIView"] = ""
            },
            Classes =
            [
                new ObjCClassDecl { Name = "UNNotificationContent" },
                new ObjCClassDecl { Name = "UNMutableNotificationContent" },
                new ObjCClassDecl { Name = "MyCustomClass" }
            ]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        Assert.Single(result.Classes);
        Assert.Equal("MyCustomClass", result.Classes[0].Name);
    }

    [Fact]
    public void FilterPlatformTypeStubs_RemovesAppleSdkProtocols()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string>
            {
                ["UNUserNotificationCenterDelegate"] = ""
            },
            Protocols =
            [
                new ObjCProtocolDecl { Name = "UNUserNotificationCenterDelegate" },
                new ObjCProtocolDecl { Name = "MyAppDelegate" }
            ]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        Assert.Single(result.Protocols);
        Assert.Equal("MyAppDelegate", result.Protocols[0].Name);
    }

    [Fact]
    public void FilterPlatformTypeStubs_NoAppleSdkTypes_NoChange()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = null,
            Classes = [new ObjCClassDecl { Name = "MyClass" }],
            Protocols = [new ObjCProtocolDecl { Name = "MyProto" }]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        // Should be same reference
        Assert.Same(module, result);
    }

    [Fact]
    public void FilterPlatformTypeStubs_EmptyAppleSdkTypes_NoChange()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string>(),
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        Assert.Same(module, result);
    }

    [Fact]
    public void FilterPlatformTypeStubs_KeepsNonSdkTypes()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIView"] = "", ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl { Name = "MyWidget" },
                new ObjCClassDecl { Name = "MyController" }
            ],
            Protocols =
            [
                new ObjCProtocolDecl { Name = "MyCustomDelegate" }
            ]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        Assert.Equal(2, result.Classes.Count);
        Assert.Single(result.Protocols);
    }

    [Fact]
    public void FilterPlatformTypeStubs_MixedSdkAndCustom_FiltersCorrectly()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIView"] = "", ["UIViewController"] = "", ["NSCoding"] = "" },
            Classes =
            [
                new ObjCClassDecl { Name = "UIView" },        // SDK stub — filter
                new ObjCClassDecl { Name = "UIViewController" }, // SDK stub — filter
                new ObjCClassDecl { Name = "MyCustomView" }    // Custom — keep
            ],
            Protocols =
            [
                new ObjCProtocolDecl { Name = "NSCoding" },    // SDK stub — filter
                new ObjCProtocolDecl { Name = "MyDelegate" }   // Custom — keep
            ]
        };

        var result = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);

        Assert.Single(result.Classes);
        Assert.Equal("MyCustomView", result.Classes[0].Name);
        Assert.Single(result.Protocols);
        Assert.Equal("MyDelegate", result.Protocols[0].Name);
    }

    // ──────────────────────────────────────────────
    // Foreign-type category filtering (Fix #10)
    // ──────────────────────────────────────────────

    [Fact]
    public void FilterToForeignCategories_OwnTypeCategoriesRemoved()
    {
        // Categories on module's own classes should be removed (already merged by parser)
        var module = new ObjCModule
        {
            ModuleName = "ManagedObjectStore",
            Classes =
            [
                new ObjCClassDecl { Name = "MOSArray" },
                new ObjCClassDecl { Name = "MOSResults" }
            ],
            Categories =
            [
                new ObjCCategoryDecl { CategoryName = "Swift", ClassName = "MOSArray" },
                new ObjCCategoryDecl { CategoryName = "Sorting", ClassName = "MOSResults" }
            ]
        };

        var result = ObjCPipeline.FilterToForeignCategories(module, Logger);

        Assert.Empty(result.Categories);
    }

    [Fact]
    public void FilterToForeignCategories_ForeignTypeCategoriesPreserved()
    {
        // Categories on platform types (not in module.Classes) should be preserved
        var module = new ObjCModule
        {
            ModuleName = "ManagedObjectStore",
            Classes =
            [
                new ObjCClassDecl { Name = "MOSArray" }
            ],
            Categories =
            [
                new ObjCCategoryDecl { CategoryName = "MOSValue", ClassName = "NSNull", ProtocolNames = ["MOSValue"] },
                new ObjCCategoryDecl { CategoryName = "", ClassName = "NSNumber", ProtocolNames = ["MOSInt", "MOSBool"] },
                new ObjCCategoryDecl { CategoryName = "Swift", ClassName = "MOSArray" } // own-type — removed
            ]
        };

        var result = ObjCPipeline.FilterToForeignCategories(module, Logger);

        Assert.Equal(2, result.Categories.Count);
        Assert.Equal("NSNull", result.Categories[0].ClassName);
        Assert.Equal("NSNumber", result.Categories[1].ClassName);
    }

    [Fact]
    public void FilterToForeignCategories_NoCategoriesReturnsEmpty()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyClass" }],
            Categories = []
        };

        var result = ObjCPipeline.FilterToForeignCategories(module, Logger);

        Assert.Empty(result.Categories);
    }

    [Fact]
    public void FilterToForeignCategories_AllForeignPreservesAll()
    {
        // When no classes match any category base class, all categories are preserved
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyClass" }],
            Categories =
            [
                new ObjCCategoryDecl { CategoryName = "MOSValue", ClassName = "NSNull" },
                new ObjCCategoryDecl { CategoryName = "MOSValue", ClassName = "NSString" },
                new ObjCCategoryDecl { CategoryName = "MOSValue", ClassName = "NSData" }
            ]
        };

        var result = ObjCPipeline.FilterToForeignCategories(module, Logger);

        Assert.Equal(3, result.Categories.Count);
    }

    [Fact]
    public void FilterToForeignCategories_SdkStubClassTreatedAsForeign_WhenStubRemovedFirst()
    {
        // Simulates the correct pipeline ordering: FilterPlatformTypeStubs runs BEFORE
        // FilterToForeignCategories. When clang expands SDK types like UIButton into stub
        // classes, those stubs are removed first, so categories on UIButton become foreign.
        var module = new ObjCModule
        {
            ModuleName = "WebImageCache",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIButton"] = "", ["UIImage"] = "" },
            Classes =
            [
                new ObjCClassDecl { Name = "UIButton" },  // SDK stub — will be removed
                new ObjCClassDecl { Name = "UIImage" },   // SDK stub — will be removed
                new ObjCClassDecl { Name = "WebImageCacheManager" } // Real class — kept
            ],
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "WebCache",
                    ClassName = "UIButton",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "sd_setImageWithURL:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "url", Type = new ObjCTypeRef { Name = "NSURL", IsPointer = true } }]
                    }]
                },
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "WebImageCacheManager", // own-type — will be removed
                }
            ]
        };

        // Step 1: FilterPlatformTypeStubs removes UIButton and UIImage
        var afterStubFilter = ObjCPipeline.FilterPlatformTypeStubs(module, Logger);
        Assert.Single(afterStubFilter.Classes); // Only WebImageCacheManager remains
        Assert.Equal("WebImageCacheManager", afterStubFilter.Classes[0].Name);

        // Step 2: FilterToForeignCategories now sees UIButton as foreign (not in Classes)
        var afterCatFilter = ObjCPipeline.FilterToForeignCategories(afterStubFilter, Logger);
        Assert.Single(afterCatFilter.Categories); // UIButton category preserved
        Assert.Equal("UIButton", afterCatFilter.Categories[0].ClassName);
    }
}
