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

    // --- ApiDefinition.cs ---

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

    // --- StructsAndEnums.cs ---

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
}
