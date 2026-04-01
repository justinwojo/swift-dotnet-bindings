// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public class AvailabilityAttributeEmitterTests
{
    private static (CSharpWriter, StringWriter) CreateWriter()
    {
        var stringWriter = new StringWriter();
        var csWriter = new CSharpWriter(stringWriter);
        return (csWriter, stringWriter);
    }

    private static BaseDecl CreateDecl(List<AvailabilityAnnotation> annotations = null)
    {
        return new StructDecl
        {
            Name = "TestType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestType"),
            MangledName = "$s",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            GenericParameters = new(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null!,
            ModuleDecl = null!,
            IsFrozen = true,
            MetadataAccessor = "",
            AvailabilityAnnotations = annotations
        };
    }

    [Fact]
    public void NoAnnotations_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(null);
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        Assert.Equal("", stringWriter.ToString());
    }

    [Fact]
    public void iOS16Introduced_EmitsSupportedOSPlatform()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios16.0\")]", stringWriter.ToString());
    }

    [Fact]
    public void macOS13Introduced_EmitsSupportedOSPlatform()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("macOS", "13", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"macos13.0\")]", stringWriter.ToString());
    }

    [Fact]
    public void UnconditionalDeprecated_TypeLevel_EmitsObsolete()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Use NewType instead", null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl, emitObsolete: true);
        csWriter.Flush();
        Assert.Contains("[Obsolete(\"Use NewType instead\")]", stringWriter.ToString());
    }

    [Fact]
    public void UnconditionalDeprecated_MethodLevel_NoObsolete()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Old API", null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl, emitObsolete: false);
        csWriter.Flush();
        Assert.DoesNotContain("[Obsolete", stringWriter.ToString());
    }

    [Fact]
    public void GetDeprecationMessage_ReturnsMessage()
    {
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Old API", null)
        });
        var msg = AvailabilityAttributeEmitter.GetDeprecationMessage(decl);
        Assert.Equal("Old API", msg);
    }

    [Fact]
    public void DeprecatedWithRenamed_EmitsUseXInstead()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, null, "newName")
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl, emitObsolete: true);
        csWriter.Flush();
        Assert.Contains("[Obsolete(\"Use newName instead.\")]", stringWriter.ToString());
    }

    [Fact]
    public void VisionOS_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("visionOS", "1.0", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        Assert.Equal("", stringWriter.ToString());
    }

    [Fact]
    public void iOSDeprecatedVersion_EmitsObsoletedOSPlatform()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "10", "12", null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios10.0\")]", output);
        Assert.Contains("[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"ios12.0\")]", output);
    }

    [Fact]
    public void ParentSameAnnotation_Deduplicated()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var parentAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13", null, null, false, false, null, null)
        };
        var parentDecl = CreateDecl(parentAnnotations);
        var childDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "13", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, childDecl, parentDecl);
        csWriter.Flush();
        // Should be deduplicated — no output for same platform+version
        Assert.DoesNotContain("SupportedOSPlatform", stringWriter.ToString());
    }

    [Fact]
    public void ParentLessRestrictive_MemberEmits()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var parentAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13", null, null, false, false, null, null)
        };
        var parentDecl = CreateDecl(parentAnnotations);
        var childDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "16", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, childDecl, parentDecl);
        csWriter.Flush();
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios16.0\")]", stringWriter.ToString());
    }

    [Fact]
    public void MultiplePlatforms_MultipleAttributes()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "13", null, null, false, false, null, null),
            new("macOS", "10.15", null, null, false, false, null, null),
            new("tvOS", "13", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("ios13.0", output);
        Assert.Contains("macos10.15", output);
        Assert.Contains("tvos13.0", output);
    }

    // --- Swift @available propagation to @_cdecl wrappers ---

    [Fact]
    public void EmitCdeclAnnotation_WithAvailability_EmitsSwiftAvailable()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false, annotations);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 16.0, *)", output);
        Assert.Contains("@_cdecl(\"SBW_Test_Symbol\")", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_NoAvailability_NoSwiftAvailable()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false);

        var output = sw.ToString();
        Assert.DoesNotContain("@available", output);
        Assert.Contains("@_cdecl(\"SBW_Test_Symbol\")", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_MultiplePlatforms_EmitsMultipleAvailable()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null),
            new("macOS", "13", null, null, false, false, null, null)
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false, annotations);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 16.0, *)", output);
        Assert.Contains("@available(macOS 13, *)", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_AvailabilityBeforeCdecl()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", true, annotations);

        var output = sw.ToString();
        // @available should appear before @MainActor and @_cdecl
        var availableIdx = output.IndexOf("@available");
        var mainActorIdx = output.IndexOf("@MainActor");
        var cdeclIdx = output.IndexOf("@_cdecl");
        Assert.True(availableIdx < mainActorIdx, "@available should come before @MainActor");
        Assert.True(mainActorIdx < cdeclIdx, "@MainActor should come before @_cdecl");
    }

    [Fact]
    public void EmitCdeclAnnotation_ParentTypeAvailability_Propagated()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        // Parent type has @available(iOS 16.0, *), member has no annotation
        var parentDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        });
        var merged = WrapperEmitterHelpers.MergeAvailability(null, parentDecl);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test", false, merged);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 16.0, *)", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_ParentAndMemberAvailability_BothEmitted()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        // Parent type: iOS 15, member: macOS 13
        var parentDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "15.0", null, null, false, false, null, null)
        });
        var memberAnnotations = new List<AvailabilityAnnotation>
        {
            new("macOS", "13", null, null, false, false, null, null)
        };
        var merged = WrapperEmitterHelpers.MergeAvailability(memberAnnotations, parentDecl);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test", false, merged);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 15.0, *)", output);
        Assert.Contains("@available(macOS 13, *)", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_DuplicatePlatform_Deduplicated()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        // Both parent and member have iOS 16.0
        var parentDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        });
        var memberAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        };
        var merged = WrapperEmitterHelpers.MergeAvailability(memberAnnotations, parentDecl);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test", false, merged);

        var output = sw.ToString();
        // Should only appear once despite being in both parent and member
        var count = output.Split("@available(iOS 16.0, *)").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void MergeAvailability_NoParentDecl_ReturnsMemberOnly()
    {
        var memberAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null)
        };
        var result = WrapperEmitterHelpers.MergeAvailability(memberAnnotations, null);
        Assert.Same(memberAnnotations, result);
    }

    [Fact]
    public void MergeAvailability_NoMemberAnnotations_ReturnsParentOnly()
    {
        var parentDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "15.0", null, null, false, false, null, null)
        });
        var result = WrapperEmitterHelpers.MergeAvailability(null, parentDecl);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("iOS", result![0].Platform);
    }

    [Fact]
    public void EmitCdeclAnnotation_DeprecationOnly_NoSwiftAvailable()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        // Unconditional deprecation without platform-specific introduced version
        var annotations = new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Use something else", null)
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false, annotations);

        var output = sw.ToString();
        // No @available emitted for deprecation-only annotations (no platform/version)
        Assert.DoesNotContain("@available", output);
    }
}
