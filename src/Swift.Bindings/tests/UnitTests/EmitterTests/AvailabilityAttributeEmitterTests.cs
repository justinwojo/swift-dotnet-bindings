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
    public void VisionOS_EmitsSupportedOSPlatform()
    {
        // Family-F-5: visionOS was previously skipped wholesale. The
        // PlatformMapping now includes visionOS → visionos so 453 markers in
        // SwiftBindings.Apple.MusicKit lower correctly.
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("visionOS", "1.0", null, null, false, false, null, null)
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        Assert.Contains(
            "[global::System.Runtime.Versioning.SupportedOSPlatform(\"visionos1.0\")]",
            stringWriter.ToString());
    }

    [Fact]
    public void VisionOS_FromAnnotations_EmitsSupportedOSPlatform()
    {
        // Mirror EmitFromAnnotations path — specialization emitters that merge
        // availability from method + parent + conformer must also propagate
        // visionOS, not skip it.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("visionOS", "1.0", null, null, false, false, null, null)
        };
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(csWriter, annotations);
        csWriter.Flush();
        Assert.Contains(
            "[global::System.Runtime.Versioning.SupportedOSPlatform(\"visionos1.0\")]",
            stringWriter.ToString());
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
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("iOS", result![0].Platform);
        Assert.Equal("16.0", result[0].IntroducedVersion);
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
    public void EmitFromAnnotations_EmitsStrictestPerPlatform()
    {
        // When a specialization merges availability from method + parent + conformer,
        // the same platform can appear multiple times with different versions. The
        // emitter must keep the strictest (highest) version per platform to avoid
        // under-guarding a call site that needs the tighter floor.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
            new("iOS", "26.0", null, null, false, false, null, null),
        };

        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, annotations);
        csWriter.Flush();

        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"ios26.0\")", output);
        Assert.DoesNotContain("SupportedOSPlatform(\"ios13.0\")", output);
    }

    [Fact]
    public void EmitFromAnnotations_SkipsPlatformsCoveredByParent()
    {
        // Parent class carries iOS 13 — specialization doesn't need to repeat it.
        // But if the specialization adds iOS 26 (conformer floor), that MUST emit.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
            new("iOS", "26.0", null, null, false, false, null, null),
        };
        var parentAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
        };

        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, annotations, parentAnnotations);
        csWriter.Flush();

        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"ios26.0\")", output);
        Assert.DoesNotContain("SupportedOSPlatform(\"ios13.0\")", output);
    }

    [Fact]
    public void EmitFromAnnotations_EmptyOrNull_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(csWriter, null);
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, Array.Empty<AvailabilityAnnotation>());
        csWriter.Flush();
        Assert.Equal("", stringWriter.ToString());
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

    // --- Strictest-version collapse for stacked annotations (CSM wrappers) ---

    [Fact]
    public void EmitCdeclAnnotation_SamePlatformTwoVersions_EmitsStrictest()
    {
        // CSM wrappers stack parent + method + per-conformer annotations, which can produce
        // e.g. iOS 13 (HMAC) + iOS 26 (SHA3_256) on the same @_cdecl. The Swift compiler must
        // see the stricter floor so the wrapped call passes availability checking on device SDKs.
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
            new("iOS", "26.0", null, null, false, false, null, null),
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false, annotations);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 26.0, *)", output);
        Assert.DoesNotContain("@available(iOS 13.0, *)", output);
    }

    [Fact]
    public void EmitCdeclAnnotation_MultiPlatformStackedAnnotations_EmitsStrictestPerPlatform()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
            new("iOS", "26.0", null, null, false, false, null, null),
            new("macOS", "10.15", null, null, false, false, null, null),
            new("macOS", "15.0", null, null, false, false, null, null),
        };

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, "SBW_Test_Symbol", false, annotations);

        var output = sw.ToString();
        Assert.Contains("@available(iOS 26.0, *)", output);
        Assert.Contains("@available(macOS 15.0, *)", output);
        Assert.DoesNotContain("iOS 13.0", output);
        Assert.DoesNotContain("macOS 10.15", output);
    }

    [Fact]
    public void CollectStrictestAvailabilityKeys_NumericVersionCompare_NotLexicographic()
    {
        // "26.0" > "13.0" numerically but lexicographically "13.0" > "26.0" is false —
        // still, lexicographic compare of single-digit vs two-digit tens can flip (e.g.,
        // "9.0" vs "10.0" — lexicographic says "9.0" > "10.0"). Assert the helper uses
        // component-wise numeric compare so both decade boundaries are respected.
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "9.0", null, null, false, false, null, null),
            new("iOS", "10.0", null, null, false, false, null, null),
        };
        var keys = WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations);
        Assert.Single(keys);
        Assert.Equal("iOS 10.0", keys[0]);
    }

    [Fact]
    public void CollectStrictestAvailabilityKeys_SkipsEntriesMissingPlatformOrVersion()
    {
        var annotations = new List<AvailabilityAnnotation>
        {
            new(null, "16.0", null, null, false, false, null, null),
            new("iOS", null, null, null, false, false, null, null),
            new("iOS", "16.0", null, null, false, false, null, null),
        };
        var keys = WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations);
        Assert.Single(keys);
        Assert.Equal("iOS 16.0", keys[0]);
    }
}
