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

    // --- macCatalyst-tracks-iOS floor lift: Swift @available side stays as it was ---

    [Fact]
    public void CollectStrictestAvailabilityKeys_LiftsExplicitCatalystToIOSFloor()
    {
        // iOS 18 + explicit macCatalyst 17 -> the Swift wrapper must require macCatalyst 18 (the
        // floor swiftc enforces for -target ...-macabi). This pins the previously-inline lift now
        // routed through AvailabilityHelpers.LiftMacCatalystFloorToIOS — output must be unchanged.
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        };
        var keys = WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations);
        Assert.Contains("macCatalyst 18.0", keys);
        Assert.DoesNotContain("macCatalyst 17.0", keys);
        Assert.Contains("iOS 18.0", keys);
    }

    // --- macCatalyst-tracks-iOS floor lift: C# [SupportedOSPlatform] side now matches ---

    [Fact]
    public void EmitAvailabilityAttributes_LiftsExplicitCatalystToIOSFloor()
    {
        // Without the lift the C# attribute advertises maccatalyst17.0 while the @_cdecl wrapper is
        // exported at maccatalyst18.0 — orphaning the symbol for a Catalyst consumer on 17.x.
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"maccatalyst18.0\")", output);
        Assert.DoesNotContain("maccatalyst17.0", output);
        Assert.Contains("SupportedOSPlatform(\"ios18.0\")", output);
    }

    [Fact]
    public void EmitAvailabilityAttributes_AbsentCatalyst_EmitsOnlyIOS()
    {
        // iOS-only API: .NET's ios->maccatalyst inheritance already narrows Catalyst consumers, so
        // no maccatalyst attribute should be invented (mirrors the Swift presence gate).
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, decl);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"ios18.0\")", output);
        Assert.DoesNotContain("maccatalyst", output);
    }

    [Fact]
    public void EmitAvailabilityAttributes_ParentAndChildCatalystMismatch_DedupesOnLiftedFloor()
    {
        // Parent type and child member both gate iOS 18 + macCatalyst 17. Both lift to maccatalyst
        // 18; lifting the parent for the dedup comparison lets the child correctly suppress the
        // repeated (lifted) floor instead of re-emitting a stale low one.
        var (csWriter, stringWriter) = CreateWriter();
        var parentDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        });
        var childDecl = CreateDecl(new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        });
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, childDecl, parentDecl);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.DoesNotContain("SupportedOSPlatform", output);
    }

    [Fact]
    public void EmitFromAnnotations_LiftsExplicitCatalystToIOSFloor()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        };
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(csWriter, annotations);
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"maccatalyst18.0\")", output);
        Assert.DoesNotContain("maccatalyst17.0", output);
    }

    [Fact]
    public void EmitSetterAccessorAvailability_LiftsExplicitCatalystToIOSFloor()
    {
        // Setter is gated iOS 18 + macCatalyst 17 above the property's iOS 16 floor; the accessor
        // attribute must advertise the lifted maccatalyst18.0, not the stale 17.0.
        var (csWriter, stringWriter) = CreateWriter();
        var propertyAvailability = new List<AvailabilityAnnotation>
        {
            new("iOS", "16.0", null, null, false, false, null, null),
        };
        var setterAvailability = new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        };
        var emitted = AvailabilityAttributeEmitter.EmitSetterAccessorAvailability(
            csWriter, propertyAvailability, setterAvailability);
        csWriter.Flush();
        Assert.True(emitted);
        var output = stringWriter.ToString();
        Assert.Contains("SupportedOSPlatform(\"maccatalyst18.0\")", output);
        Assert.DoesNotContain("maccatalyst17.0", output);
    }

    // --- Runtime OS-version guard (EmitRuntimeAvailabilityGuard) ---
    //
    // [SupportedOSPlatform] is a compile-time CA1416 hint only — it provides NO runtime guard.
    // A Swift symbol whose availability floor exceeds the binary's min-OS is weak-linked and
    // resolves to null on an older OS; the generated @_cdecl wrapper calls it unconditionally
    // and SIGSEGVs (pc=0) — uncatchable by C# try/catch. EmitRuntimeAvailabilityGuard throws a
    // managed PlatformNotSupportedException BEFORE that call. The guard keys on the member's
    // EFFECTIVE floor — its own availability MERGED with every enclosing type's — with NO
    // dedup against the parent: unlike the compile-time attribute (which C# nesting inherits),
    // there is no runtime attribute inheritance, so a member on an OS-gated type must guard the
    // full inherited floor even when it declares no stricter floor of its own. Callers pass the
    // already-merged effective annotations (via MergeAvailabilityFromAncestors).

    [Fact]
    public void RuntimeGuard_NoAnnotations_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, null, "TestType.member");
        csWriter.Flush();
        Assert.Equal("", stringWriter.ToString());
    }

    [Fact]
    public void RuntimeGuard_EmptyAnnotations_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
            csWriter, Array.Empty<AvailabilityAnnotation>(), "TestType.member");
        csWriter.Flush();
        Assert.Equal("", stringWriter.ToString());
    }

    [Fact]
    public void RuntimeGuard_DeprecationOnly_NoFloor_NoOutput()
    {
        // Unconditional deprecation carries no platform/introduced version, so there is nothing
        // to guard — the runtime guard must stay silent (no spurious always-false `if`).
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Use something else", null)
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "TestType.member");
        csWriter.Flush();
        Assert.Equal("", stringWriter.ToString());
    }

    [Fact]
    public void RuntimeGuard_iOSFloor_EmitsThrowingGuard()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.2", null, null, false, false, null, null)
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
            csWriter, annotations, "AppStore.someApi");
        csWriter.Flush();
        var output = stringWriter.ToString();
        // Fires only when running ON iOS BELOW the 26.2 floor; uses the platform-agnostic APIs.
        Assert.Contains("global::System.OperatingSystem.IsOSPlatform(\"ios\")", output);
        // Negated floor check — the guard throws when NOT at-least the floor.
        Assert.Contains("!global::System.OperatingSystem.IsOSPlatformVersionAtLeast(\"ios\", 26, 2)", output);
        Assert.Contains("throw new global::System.PlatformNotSupportedException(", output);
        // Message names the API and the required floor.
        Assert.Contains("AppStore.someApi", output);
        Assert.Contains("iOS 26.2", output);
    }

    [Fact]
    public void RuntimeGuard_EffectiveParentFloor_EmitsGuard_NoDedup()
    {
        // The CORE fix: a type-gated member with NO stricter floor of its own still inherits the
        // type's floor at runtime, so the merged effective floor (iOS 26.2) MUST emit a guard.
        // The old behavior deduped this against the parent and emitted nothing, leaving a
        // type-gated constructor/static/operator able to reach the weak-linked symbol and crash.
        var (csWriter, stringWriter) = CreateWriter();
        var effective = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.2", null, null, false, false, null, null)
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, effective, "T.m");
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("!global::System.OperatingSystem.IsOSPlatformVersionAtLeast(\"ios\", 26, 2)", output);
        Assert.Contains("throw new global::System.PlatformNotSupportedException(", output);
    }

    [Fact]
    public void RuntimeGuard_PatchVersionFloor_EmitsAllComponents()
    {
        // A patch-level floor (iOS 17.4.1) must guard on all three components, not round down to
        // 17.4 — otherwise the guard under-fires on 17.4.0 even though the named floor is 17.4.1.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.4.1", null, null, false, false, null, null)
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "T.m");
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("!global::System.OperatingSystem.IsOSPlatformVersionAtLeast(\"ios\", 17, 4, 1)", output);
        Assert.Contains("iOS 17.4.1", output);
    }

    [Fact]
    public void RuntimeGuard_StrictestPerPlatformWins()
    {
        // Stacked annotations (parent + method + conformer) can list the same platform twice;
        // the guard must keep the highest floor so it doesn't under-guard the call site.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "13.0", null, null, false, false, null, null),
            new("iOS", "26.0", null, null, false, false, null, null),
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "T.m");
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("IsOSPlatformVersionAtLeast(\"ios\", 26, 0)", output);
        Assert.DoesNotContain("13", output);
    }

    [Fact]
    public void RuntimeGuard_MultiPlatform_OrsOneClausePerPlatform()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.0", null, null, false, false, null, null),
            new("macOS", "15.0", null, null, false, false, null, null),
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "T.m");
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("IsOSPlatform(\"ios\")", output);
        Assert.Contains("IsOSPlatform(\"macos\")", output);
        Assert.Contains(" || ", output);
        // Both floors named in the message.
        Assert.Contains("iOS 26.0", output);
        Assert.Contains("macOS 15.0", output);
    }

    [Fact]
    public void RuntimeGuard_LiftsExplicitCatalystToIOSFloor()
    {
        // iOS 18 + explicit macCatalyst 17 — the guard must require maccatalyst 18 (the floor
        // swiftc enforces for -target ...-macabi), matching the [SupportedOSPlatform] lift, so a
        // Catalyst consumer on 17.x is guarded rather than crashing on the weak-linked symbol.
        var (csWriter, stringWriter) = CreateWriter();
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "18.0", null, null, false, false, null, null),
            new("macCatalyst", "17.0", null, null, false, false, null, null),
        };
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "T.m");
        csWriter.Flush();
        var output = stringWriter.ToString();
        Assert.Contains("IsOSPlatformVersionAtLeast(\"maccatalyst\", 18, 0)", output);
        Assert.DoesNotContain("17", output);
        Assert.Contains("IsOSPlatformVersionAtLeast(\"ios\", 18, 0)", output);
    }

    [Fact]
    public void RuntimeGuard_DoesNotDedupAgainstParent_UnlikeAttribute()
    {
        // The compile-time [SupportedOSPlatform] attribute dedups against the enclosing type (C#
        // nesting inherits it at compile time), but the runtime guard must NOT — there is no
        // runtime attribute inheritance. For a member whose effective floor is [iOS 26 (== parent),
        // tvOS 26 (member-only)], the attribute drops the parent-covered iOS clause while the guard
        // keeps it. This asserts that intentional divergence.
        var effective = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.0", null, null, false, false, null, null),
            new("tvOS", "26.0", null, null, false, false, null, null),
        };
        var parent = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.0", null, null, false, false, null, null), // iOS covered by parent
        };

        var (attrWriter, attrSw) = CreateWriter();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(attrWriter, effective, parent);
        attrWriter.Flush();
        var attrOut = attrSw.ToString();

        var (guardWriter, guardSw) = CreateWriter();
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(guardWriter, effective, "T.m");
        guardWriter.Flush();
        var guardOut = guardSw.ToString();

        // Attribute dedups the parent-covered iOS floor; the runtime guard does NOT.
        Assert.DoesNotContain("ios26.0", attrOut);
        Assert.Contains("IsOSPlatform(\"ios\")", guardOut);
        // tvOS (member-only) survives on both sides.
        Assert.Contains("tvos26.0", attrOut);
        Assert.Contains("IsOSPlatform(\"tvos\")", guardOut);
    }

    // --- Positive availability condition (BuildIsAvailableCondition) ---
    //
    // The module initializer wraps eager generic registration / metadata warmup of an
    // OS-gated ISwiftObject type in a POSITIVE availability check so launching on a host
    // OS below the type's floor cannot trigger an uncatchable native Mono generic-
    // instantiation abort. BuildIsAvailableCondition is the negation of the runtime guard's
    // below-floor condition: null when the type has no floor (always available → no guard).

    [Fact]
    public void IsAvailableCondition_NoAnnotations_ReturnsNull()
    {
        Assert.Null(AvailabilityAttributeEmitter.BuildIsAvailableCondition(null));
        Assert.Null(AvailabilityAttributeEmitter.BuildIsAvailableCondition(Array.Empty<AvailabilityAnnotation>()));
    }

    [Fact]
    public void IsAvailableCondition_DeprecationOnly_NoFloor_ReturnsNull()
    {
        // No introduced version → no floor to gate → no guard needed (an unconditionally
        // deprecated-but-always-present type must still register eagerly).
        var annotations = new List<AvailabilityAnnotation>
        {
            new(null, null, null, null, true, false, "Use something else", null)
        };
        Assert.Null(AvailabilityAttributeEmitter.BuildIsAvailableCondition(annotations));
    }

    [Fact]
    public void IsAvailableCondition_iOSFloor_NegatesBelowFloorCondition()
    {
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "99.0", null, null, false, false, null, null)
        };
        var condition = AvailabilityAttributeEmitter.BuildIsAvailableCondition(annotations);
        Assert.NotNull(condition);
        // Positive form: NOT (running on iOS below 99.0).
        Assert.StartsWith("!(", condition);
        Assert.Contains("global::System.OperatingSystem.IsOSPlatform(\"ios\")", condition);
        Assert.Contains("!global::System.OperatingSystem.IsOSPlatformVersionAtLeast(\"ios\", 99, 0)", condition);
    }

    [Fact]
    public void IsAvailableCondition_IsExactNegationOfRuntimeGuardCondition()
    {
        // The positive gate and the throwing member guard must agree on the same floor: the
        // module-init "run only if available" check is exactly the negation of the member
        // guard's "throw if below floor" check, so a type warmed at launch is precisely the
        // set of types whose members would NOT throw on that OS.
        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.2", null, null, false, false, null, null),
            new("macOS", "15.0", null, null, false, false, null, null),
        };
        var positive = AvailabilityAttributeEmitter.BuildIsAvailableCondition(annotations);

        var (csWriter, stringWriter) = CreateWriter();
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, annotations, "T.m");
        csWriter.Flush();
        var guardOutput = stringWriter.ToString();

        Assert.NotNull(positive);
        // Strip the leading "!(" and trailing ")" to recover the below-floor expression and
        // confirm the throwing guard's `if (...)` uses that exact same expression.
        var belowFloor = positive!.Substring(2, positive.Length - 3);
        Assert.Contains($"if ({belowFloor})", guardOutput);
    }
    private static List<AvailabilityAnnotation> Intro(string platform, string version)
        => new() { new(platform, version, null, null, false, false, null, null) };

    [Theory]
    // A requirement introduced after its enclosing type declares a floor the type does not guard.
    [InlineData("iOS", "17.0", "iOS", "16.0", true)]
    // Same floor as the parent: the type's own guard already covers it.
    [InlineData("iOS", "16.0", "iOS", "16.0", false)]
    // Older than the parent: the parent's floor still dominates.
    [InlineData("iOS", "15.0", "iOS", "16.0", false)]
    // A platform the parent says nothing about is a floor the parent cannot be guarding.
    [InlineData("macOS", "14.0", "iOS", "16.0", true)]
    public void DeclaresStricterFloorThanParent_ComparesMemberFloorAgainstParent(
        string memberPlatform, string memberVersion,
        string parentPlatform, string parentVersion,
        bool expected)
    {
        var parent = CreateDecl(Intro(parentPlatform, parentVersion));
        Assert.Equal(
            expected,
            AvailabilityAttributeEmitter.DeclaresStricterFloorThanParent(
                Intro(memberPlatform, memberVersion), parent));
    }

    [Fact]
    public void DeclaresStricterFloorThanParent_MemberWithoutAnnotations_IsNotStricter()
    {
        var parent = CreateDecl(Intro("iOS", "16.0"));
        Assert.False(AvailabilityAttributeEmitter.DeclaresStricterFloorThanParent(
            (IReadOnlyList<AvailabilityAnnotation>)null, parent));
    }

    [Fact]
    public void DeclaresStricterFloorThanParent_UnannotatedParent_MemberFloorIsStricter()
    {
        Assert.True(AvailabilityAttributeEmitter.DeclaresStricterFloorThanParent(
            Intro("iOS", "17.0"), CreateDecl(null)));
    }

    [Fact]
    public void DeclaresStricterFloorThanParent_DeclOverload_AgreesWithAnnotationOverload()
    {
        var parent = CreateDecl(Intro("iOS", "16.0"));
        var member = CreateDecl(Intro("iOS", "17.0"));
        Assert.Equal(
            AvailabilityAttributeEmitter.DeclaresStricterFloorThanParent(
                member.AvailabilityAnnotations, parent),
            AvailabilityAttributeEmitter.DeclaresStricterFloorThanParent(member, parent));
    }

    [Fact]
    public void BuildStricterFloorGuardStatement_StricterMember_ThrowsBelowMergedFloor()
    {
        var guard = AvailabilityAttributeEmitter.BuildStricterFloorGuardStatement(
            Intro("iOS", "17.0"), CreateDecl(Intro("iOS", "16.0")), "IProto.Newer");

        Assert.NotNull(guard);
        Assert.StartsWith("if (", guard);
        Assert.Contains("throw new global::System.PlatformNotSupportedException(", guard);
        // The thrown floor is the member's, not the enclosing type's.
        Assert.Contains("17", guard);
        Assert.DoesNotContain("16.0", guard);
        Assert.Contains("IProto.Newer", guard);
    }

    [Theory]
    // Not stricter than the enclosing type -> no redundant guard.
    [InlineData("16.0", false)]
    [InlineData("15.0", false)]
    [InlineData("17.0", true)]
    public void BuildStricterFloorGuardStatement_EmitsOnlyForStricterMembers(
        string memberVersion, bool expectGuard)
    {
        var guard = AvailabilityAttributeEmitter.BuildStricterFloorGuardStatement(
            Intro("iOS", memberVersion), CreateDecl(Intro("iOS", "16.0")), "IProto.Member");
        Assert.Equal(expectGuard, guard != null);
    }

    [Fact]
    public void BuildStricterFloorGuardStatement_MergesParentPlatformsIntoTheThrownFloor()
    {
        // The member raises only iOS; the parent's macOS floor still governs on macOS, so the
        // guard has to test both or a macOS 12 caller walks straight into the native symbol.
        var guard = AvailabilityAttributeEmitter.BuildStricterFloorGuardStatement(
            Intro("iOS", "17.0"),
            CreateDecl(new List<AvailabilityAnnotation>
            {
                new("iOS", "16.0", null, null, false, false, null, null),
                new("macOS", "13.0", null, null, false, false, null, null)
            }),
            "IProto.Newer");

        Assert.NotNull(guard);
        Assert.Contains("17", guard);
        Assert.Contains("13", guard);
    }

    [Fact]
    public void BuildStricterFloorGuardPrefix_NoGuard_IsEmptySoTheBodyIsUnchanged()
    {
        Assert.Equal(
            string.Empty,
            AvailabilityAttributeEmitter.BuildStricterFloorGuardPrefix(
                Intro("iOS", "16.0"), CreateDecl(Intro("iOS", "16.0")), "IProto.Member", "    "));
    }

    [Fact]
    public void BuildStricterFloorGuardPrefix_Guard_EndsWithTheContinuationIndent()
    {
        var prefix = AvailabilityAttributeEmitter.BuildStricterFloorGuardPrefix(
            Intro("iOS", "17.0"), CreateDecl(Intro("iOS", "16.0")), "IProto.Member", "    ");

        var statement = AvailabilityAttributeEmitter.BuildStricterFloorGuardStatement(
            Intro("iOS", "17.0"), CreateDecl(Intro("iOS", "16.0")), "IProto.Member");

        Assert.Equal(statement + "\n    ", prefix);
    }

    [Theory]
    // Setter introduced after the property -> the set accessor needs its own floor.
    [InlineData("16.0", "17.0", true)]
    // Setter list equal to the property's -> nothing extra to say on the accessor.
    [InlineData("16.0", "16.0", false)]
    // A setter list that is LOOSER than the property never widens the property's floor.
    [InlineData("17.0", "16.0", false)]
    public void SetterFloorIsStricterThanProperty_ComparesSetterFloorAgainstProperty(
        string propertyVersion, string setterVersion, bool expected)
    {
        Assert.Equal(
            expected,
            AvailabilityAttributeEmitter.SetterFloorIsStricterThanProperty(
                Intro("iOS", propertyVersion), Intro("iOS", setterVersion)));
    }

    [Fact]
    public void SetterFloorIsStricterThanProperty_NoSetterAnnotations_IsNotStricter()
    {
        Assert.False(AvailabilityAttributeEmitter.SetterFloorIsStricterThanProperty(
            Intro("iOS", "16.0"), null));
    }
}
