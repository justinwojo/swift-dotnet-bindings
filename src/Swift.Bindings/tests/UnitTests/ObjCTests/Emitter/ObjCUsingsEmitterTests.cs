// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Header-using emission must drop namespaces unavailable on the target
/// platform. Concrete regression: <c>using UIKit;</c> on
/// <c>net10.0-macos</c> caused CS0246 on every generated ObjC binding
/// (Matter, MatterSupport) even when no UIKit type was referenced, which
/// kept those packages iOS-only despite Apple shipping the frameworks on
/// macOS 13.3+.
/// </summary>
public class ObjCUsingsEmitterTests
{
    static readonly PlatformInfo iOS = PlatformInfoFactory.Create(ApplePlatform.iOS);
    static readonly PlatformInfo macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
    static readonly PlatformInfo tvOS = PlatformInfoFactory.Create(ApplePlatform.tvOS);
    static readonly PlatformInfo MacCatalyst = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);

    static ObjCModule TrivialEnumModule() => new()
    {
        ModuleName = "TestLib",
        Enums = [new ObjCEnumDecl { Name = "TLFoo", Cases = [new ObjCEnumCaseDecl { Name = "TLFooBar" }] }]
    };

    static ObjCModule TrivialClassModule() => new()
    {
        ModuleName = "TestLib",
        Classes = [new ObjCClassDecl { Name = "TLObj" }]
    };

    /// <summary>
    /// Constructs a module with a block typedef referenced only from a protocol
    /// method (not a C function), which is how StructsAndEnumsEmitter decides to
    /// emit a separate BgenDelegates.cs file.
    /// </summary>
    static ObjCModule ModuleEmittingBgenDelegates() => new()
    {
        ModuleName = "TestLib",
        Typedefs =
        [
            new ObjCTypedefDecl
            {
                Name = "TLCallback",
                UnderlyingType = new ObjCTypeRef
                {
                    Name = "block",
                    IsBlock = true,
                    BlockReturnType = new ObjCTypeRef { Name = "void" },
                    BlockParams = [],
                },
            },
        ],
        Protocols =
        [
            new ObjCProtocolDecl
            {
                Name = "TLDelegate",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "fire:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "cb", Type = new ObjCTypeRef { Name = "TLCallback" } },
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>
    /// A class with a property typed by an Apple SDK type whose owning framework
    /// (<paramref name="referencedTypeNamespace"/>) is provided via header provenance. When
    /// <paramref name="reference"/> is false the type is declared in the provenance map but no member
    /// references it (the minimal-set case).
    /// </summary>
    static ObjCModule ModuleWithAppleSdkProvenance(string referencedTypeName, string referencedTypeNamespace, bool reference) => new()
    {
        ModuleName = "TestLib",
        Classes =
        [
            new ObjCClassDecl
            {
                Name = "TLObj",
                Properties = reference
                    ? [new ObjCPropertyDecl { Name = "txn", Type = new ObjCTypeRef { Name = referencedTypeName, IsPointer = true }, IsReadonly = true }]
                    : [],
            },
        ],
        AppleSdkTypeNamespaces = new Dictionary<string, string> { [referencedTypeName] = referencedTypeNamespace },
    };

    // --- ApiDefinition.cs ---

    [Fact]
    public void ApiDefinition_ReferencedAppleSdkType_EmitsProvenanceUsing()
    {
        // StoreKit is NOT in the curated baseline; a member referencing SKPaymentTransaction must
        // still get `using StoreKit;` from the type's .framework provenance (the Facebook CS0246 root
        // cause). Self-healing: the list is never hand-edited for a newly-referenced framework.
        var output = EmitApiDefinition(ModuleWithAppleSdkProvenance("SKPaymentTransaction", "StoreKit", reference: true));
        Assert.Contains("using StoreKit;", output);
    }

    [Fact]
    public void ApiDefinition_UnreferencedAppleSdkFramework_NotEmitted()
    {
        // Minimal-set proof: StoreKit is in the provenance map but no member references it, so its
        // `using` must NOT be emitted — the derived set tracks references, not the whole SDK surface.
        var output = EmitApiDefinition(ModuleWithAppleSdkProvenance("SKPaymentTransaction", "StoreKit", reference: false));
        Assert.DoesNotContain("using StoreKit;", output);
    }

    [Fact]
    public void ApiDefinition_NoProvenance_EmitsBaselineOnly()
    {
        // -fmodules mode (AppleSdkTypeNamespaces null): no derived usings, baseline intact.
        var output = EmitApiDefinition(TrivialClassModule());
        Assert.DoesNotContain("using StoreKit;", output);
        Assert.Contains("using Foundation;", output);
    }

    [Fact]
    public void ApiDefinition_ReferencedBaselineFramework_NotDuplicated()
    {
        // A provenance type whose namespace is already in the baseline (UIKit) must not produce a
        // second `using UIKit;` — the additive set excludes baseline namespaces.
        var output = EmitApiDefinition(ModuleWithAppleSdkProvenance("UIViewController", "UIKit", reference: true), platformInfo: iOS);
        var count = output.Split("using UIKit;").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void ApiDefinition_macOS_OmitsUIKit()
    {
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: macOS);
        Assert.DoesNotContain("using UIKit;", output);
    }

    [Fact]
    public void ApiDefinition_macOS_KeepsFoundation()
    {
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: macOS);
        Assert.Contains("using Foundation;", output);
        Assert.Contains("using ObjCRuntime;", output);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("tvOS")]
    [InlineData("MacCatalyst")]
    public void ApiDefinition_NonMacOSPlatforms_IncludeUIKit(string platformName)
    {
        var pi = platformName switch
        {
            "iOS" => iOS,
            "tvOS" => tvOS,
            "MacCatalyst" => MacCatalyst,
            _ => throw new System.ArgumentException(platformName),
        };
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: pi);
        Assert.Contains("using UIKit;", output);
    }

    [Fact]
    public void ApiDefinition_NullPlatformInfo_IncludesAllUsings()
    {
        // Null PlatformInfo (legacy CLI invocation) must not regress to dropping
        // any using — without a target platform we can't prove anything is unavailable.
        var output = EmitApiDefinition(TrivialClassModule());
        Assert.Contains("using UIKit;", output);
        Assert.Contains("using Foundation;", output);
    }

    [Fact]
    public void ApiDefinition_tvOS_OmitsWebKit()
    {
        // WebKit.framework does not ship on tvOS, so the kitchen-sink `using WebKit;`
        // caused CS0246 on every generated mixed-binding ObjC companion targeting
        // tvOS (surfaced by the four-platform mixed/multi-tfm PackGate leg).
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: tvOS);
        Assert.DoesNotContain("using WebKit;", output);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("macOS")]
    [InlineData("MacCatalyst")]
    public void ApiDefinition_WebKitPlatforms_IncludeWebKit(string platformName)
    {
        // WebKit IS available on iOS, macOS, and Mac Catalyst — only tvOS lacks it,
        // so the gate must keep `using WebKit;` everywhere except tvOS.
        var pi = platformName switch
        {
            "iOS" => iOS,
            "macOS" => macOS,
            "MacCatalyst" => MacCatalyst,
            _ => throw new System.ArgumentException(platformName),
        };
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: pi);
        Assert.Contains("using WebKit;", output);
    }

    [Theory]
    [InlineData("macOS")]
    [InlineData("MacCatalyst")]
    public void ApiDefinition_MacDerivedPlatforms_OmitOpenGLES(string platformName)
    {
        // OpenGLES.framework ships on neither macOS nor Mac Catalyst: macOS uses the deprecated
        // desktop OpenGL, and Catalyst runs on the Mac, so the OpenGLES namespace is absent from
        // both Microsoft.macOS and Microsoft.MacCatalyst (verified: EAGLContext resolves on
        // Microsoft.iOS but not Microsoft.MacCatalyst). The kitchen-sink `using OpenGLES;` must be
        // dropped on both or it CS0246s on every generated ObjC binding there — the same shape as
        // the UIKit/WebKit cross-TFM bug, and the exact failure the four-platform multi-TFM PackGate
        // leg surfaced on its Catalyst slice.
        var pi = platformName switch
        {
            "macOS" => macOS,
            "MacCatalyst" => MacCatalyst,
            _ => throw new System.ArgumentException(platformName),
        };
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: pi);
        Assert.DoesNotContain("using OpenGLES;", output);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("tvOS")]
    public void ApiDefinition_OpenGLESPlatforms_IncludeOpenGLES(string platformName)
    {
        // OpenGLES IS available on iOS and tvOS (but NOT the Mac-derived platforms above) — needed
        // so EAGLContext and the other GLKit/OpenGLES types referenced by bindings like MapLibre
        // resolve (CS0246 absent this using).
        var pi = platformName switch
        {
            "iOS" => iOS,
            "tvOS" => tvOS,
            _ => throw new System.ArgumentException(platformName),
        };
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: pi);
        Assert.Contains("using OpenGLES;", output);
    }

    [Theory]
    [InlineData("ARSession", "ARKit")]
    [InlineData("CPTemplate", "CarPlay")]
    public void ApiDefinition_AdditiveProvenance_CatalystUnavailableFramework_DroppedOnCatalystKeptOniOS(
        string referencedTypeName, string frameworkNamespace)
    {
        // ARKit and CarPlay are NOT in the curated baseline — unlike OpenGLES they reach output ONLY
        // through the additive header-provenance branch (a referenced Apple SDK class contributes its
        // owning `using`). Both ship on iOS but NOT on Mac Catalyst (Catalyst runs on the Mac, which
        // lacks ARKit/CarPlay), so the additive branch must gate them exactly as EmitFiltered gates the
        // baseline: emitted on iOS, dropped on Catalyst even though a member references the type. This
        // pins the additive path (not just the baseline OpenGLES omission and the raw availability
        // predicate) against the same cross-TFM CS0246 the Catalyst annotations exist to prevent.
        var iosOutput = EmitApiDefinition(
            ModuleWithAppleSdkProvenance(referencedTypeName, frameworkNamespace, reference: true), platformInfo: iOS);
        Assert.Contains($"using {frameworkNamespace};", iosOutput);

        var catalystOutput = EmitApiDefinition(
            ModuleWithAppleSdkProvenance(referencedTypeName, frameworkNamespace, reference: true), platformInfo: MacCatalyst);
        Assert.DoesNotContain($"using {frameworkNamespace};", catalystOutput);
    }

    // --- StructsAndEnums.cs ---

    /// <summary>
    /// A module whose StructsAndEnums surface references an Apple SDK type via a struct field.
    /// When <paramref name="reference"/> is false the type is only in the provenance map (minimal-set).
    /// </summary>
    static ObjCModule ModuleWithStructFieldAppleSdkProvenance(
        string referencedTypeName, string referencedTypeNamespace, bool reference) => new()
    {
        ModuleName = "TestLib",
        Structs =
        [
            new ObjCStructDecl
            {
                Name = "TLConfig",
                Fields = reference
                    ? [new ObjCStructField { Name = "pixelFormat", Type = new ObjCTypeRef { Name = referencedTypeName } }]
                    : [new ObjCStructField { Name = "width", Type = new ObjCTypeRef { Name = "int" } }],
            },
        ],
        AppleSdkTypeNamespaces = new Dictionary<string, string> { [referencedTypeName] = referencedTypeNamespace },
    };

    [Fact]
    public void StructsAndEnums_macOS_OmitsUIKit()
    {
        var output = EmitStructsAndEnums(TrivialEnumModule(), platformInfo: macOS);
        Assert.DoesNotContain("using UIKit;", output);
    }

    [Fact]
    public void StructsAndEnums_macOS_KeepsCoreAnimation()
    {
        var output = EmitStructsAndEnums(TrivialEnumModule(), platformInfo: macOS);
        Assert.Contains("using CoreAnimation;", output);
        Assert.Contains("using Foundation;", output);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("tvOS")]
    [InlineData("MacCatalyst")]
    public void StructsAndEnums_NonMacOSPlatforms_IncludeUIKit(string platformName)
    {
        var pi = platformName switch
        {
            "iOS" => iOS,
            "tvOS" => tvOS,
            "MacCatalyst" => MacCatalyst,
            _ => throw new System.ArgumentException(platformName),
        };
        var output = EmitStructsAndEnums(TrivialEnumModule(), platformInfo: pi);
        Assert.Contains("using UIKit;", output);
    }

    [Fact]
    public void StructsAndEnums_StructField_ReferencedAppleSdkType_EmitsProvenanceUsing()
    {
        // Metal is NOT in the StructsAndEnums curated baseline. A struct field typed MTLPixelFormat
        // (rive-ios SWIFTBIND113 / CS0246 root cause) must still get `using Metal;` from header
        // provenance — same self-healing mechanism as ApiDefinition, not a hardcoded baseline entry.
        var output = EmitStructsAndEnums(
            ModuleWithStructFieldAppleSdkProvenance("MTLPixelFormat", "Metal", reference: true),
            platformInfo: iOS);
        Assert.Contains("using Metal;", output);
    }

    [Fact]
    public void StructsAndEnums_StructField_ReferencedAppleSdkEnum_EmitsEnumChannelUsing()
    {
        // Proves the ENUM provenance channel (AppleSdkEnumNamespaces) drives usings when the
        // class/protocol map (AppleSdkTypeNamespaces) is null — the real parser shape for
        // NS_ENUM types like MTLPixelFormat. Metal is NOT in the StructsAndEnums baseline.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "TLConfig",
                    Fields =
                    [
                        new ObjCStructField
                        {
                            Name = "pixelFormat",
                            Type = new ObjCTypeRef { Name = "MTLPixelFormat" },
                        },
                    ],
                },
            ],
            AppleSdkTypeNamespaces = null,
            AppleSdkEnumNamespaces = new Dictionary<string, string>
            {
                ["MTLPixelFormat"] = "Metal",
            },
        };

        var output = EmitStructsAndEnums(module, platformInfo: iOS);
        Assert.Contains("using Metal;", output);
    }

    [Fact]
    public void StructsAndEnums_StructField_UnreferencedAppleSdkFramework_NotEmitted()
    {
        // Minimal-set: MTLPixelFormat→Metal is in the provenance map but no struct field (or other
        // surface) references it, so `using Metal;` must not appear.
        var output = EmitStructsAndEnums(
            ModuleWithStructFieldAppleSdkProvenance("MTLPixelFormat", "Metal", reference: false),
            platformInfo: iOS);
        Assert.DoesNotContain("using Metal;", output);
    }

    [Fact]
    public void StructsAndEnums_StructField_ReferencedBaselineFramework_NotDuplicated()
    {
        // A field typed by a namespace already in the StructsAndEnums baseline (UIKit) must not
        // produce a second `using UIKit;` — the additive set excludes baseline namespaces.
        var output = EmitStructsAndEnums(
            ModuleWithStructFieldAppleSdkProvenance("UIView", "UIKit", reference: true),
            platformInfo: iOS);
        var count = output.Split("using UIKit;").Length - 1;
        Assert.Equal(1, count);
    }

    // --- BgenDelegates.cs ---

    [Fact]
    public void BgenDelegates_macOS_OmitsUIKit()
    {
        var (_, bgen) = EmitStructsAndEnumsBoth(ModuleEmittingBgenDelegates(), platformInfo: macOS);
        Assert.NotNull(bgen);
        Assert.DoesNotContain("using UIKit;", bgen!);
        Assert.Contains("using Foundation;", bgen);
    }

    [Fact]
    public void BgenDelegates_iOS_IncludesUIKit()
    {
        var (_, bgen) = EmitStructsAndEnumsBoth(ModuleEmittingBgenDelegates(), platformInfo: iOS);
        Assert.NotNull(bgen);
        Assert.Contains("using UIKit;", bgen!);
    }

    // --- Registry invariant ---

    /// <summary>
    /// The <see cref="ObjCUsingsEmitter"/> static constructor asserts that every
    /// Apple-framework name it might emit is catalogued in apple-frameworks.json,
    /// otherwise the IsModuleAvailableOnPlatform gate silently passes the using
    /// through (the original CS0246 bug shape). Reaching any emit method without
    /// throwing proves the invariant holds for the current using lists.
    /// </summary>
    [Fact]
    public void RegistryInvariant_AllNonSystemUsings_AreRegistered()
    {
        var output = EmitApiDefinition(TrivialClassModule(), platformInfo: macOS);
        Assert.Contains("using Foundation;", output);
    }

    // --- System-enum namespace channel (provenance-independent) ---

    /// <summary>
    /// A member typed by a registered system enum contributes its owning namespace even when NEITHER
    /// AST provenance channel has anything to say. That is the <c>-fmodules</c> case, where SDK
    /// declarations come from precompiled module files and never reach the AST at all — the only
    /// mode a real pure-ObjC xcframework is parsed in — so without this channel the mapped enum name
    /// would be emitted with no <c>using</c> to resolve it.
    /// </summary>
    [Fact]
    public void CollectReferencedNamespaces_SystemEnumWithNoAstProvenance_YieldsOwningNamespace()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "currentAuthorization",
                            ReturnType = new ObjCTypeRef { Name = "CLAuthorizationStatus" },
                            IsInstanceMethod = true,
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl { Name = "interfaceStyle", Type = new ObjCTypeRef { Name = "UIUserInterfaceStyle" } }
                    ]
                }
            ]
        };

        var namespaces = ObjCUsingsEmitter.CollectReferencedNamespaces(module, appleSdkTypeNamespaces: null);

        Assert.Contains("CoreLocation", namespaces);
        Assert.Contains("UIKit", namespaces);
    }

    /// <summary>
    /// The channel is keyed on the registered vocabulary, not on a name-shape heuristic: an
    /// unregistered type contributes nothing, so an unrelated third-party name can never pull in a
    /// framework <c>using</c> that vouches for it.
    /// </summary>
    [Fact]
    public void CollectReferencedNamespaces_UnregisteredType_ContributesNothing()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl { Name = "vendorStatus", Type = new ObjCTypeRef { Name = "ZZVendorAuthorizationStatus" } }
                    ]
                }
            ]
        };

        Assert.Empty(ObjCUsingsEmitter.CollectReferencedNamespaces(module, appleSdkTypeNamespaces: null));
    }
}
