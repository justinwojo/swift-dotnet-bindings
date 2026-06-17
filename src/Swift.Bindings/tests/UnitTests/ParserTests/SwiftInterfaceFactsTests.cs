// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Contract tests for <see cref="SwiftInterfaceFacts"/>: drift-loud completeness,
/// with-mutation safety, and pass-through verification that each field reaches
/// its consumer in <see cref="SwiftABIParser"/>.
/// </summary>
public class SwiftInterfaceFactsTests
{
    #region Structural / drift-loud tests

    [Fact]
    public void Empty_PopulatesEveryRequiredCollection()
    {
        // Drift-loud guard: if a field is added to SwiftInterfaceFacts without updating Empty,
        // either compilation fails (required init property) or this count check trips.
        // 21 fact maps + 3 best-effort source-position maps + 3 non-fact migrations
        // (ProtocolNames, ProtocolExtensionMethods, ExtensionMemberCandidates) + 1 SDK 0.11.0 R2
        // SPI-only conformances + 1 AppIntents 0.12.0 ConstLiteralParameters + 1
        // ClosureParameterAttributes + 1 Finding 23 ObjCRuntimeNames = 31.
        var properties = typeof(SwiftInterfaceFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null)
            .ToList();

        Assert.Equal(31, properties.Count);

        // Every required property is populated on Empty (no nullable holes).
        foreach (var prop in properties)
        {
            var value = prop.GetValue(SwiftInterfaceFacts.Empty);
            Assert.NotNull(value);

            // Each is a collection with zero entries.
            var enumerable = (IEnumerable)value!;
            Assert.False(enumerable.GetEnumerator().MoveNext(),
                $"Empty.{prop.Name} should contain zero entries.");
        }
    }

    [Fact]
    public void Empty_AllFieldsAreCollectionTypes()
    {
        // Each fact field must be a HashSet<...>, Dictionary<...,...>, or List<...> — concrete
        // collection types that Program.GenerateBindings can populate without interface
        // conversions. List backs ExtensionMemberCandidates, which
        // is order-sensitive (regex producer walks the file top-to-bottom).
        var properties = typeof(SwiftInterfaceFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null);

        foreach (var prop in properties)
        {
            var typeName = prop.PropertyType.Name;
            Assert.True(typeName.StartsWith("HashSet") || typeName.StartsWith("Dictionary") || typeName.StartsWith("List"),
                $"{prop.Name} is {prop.PropertyType.FullName}; expected HashSet, Dictionary, or List.");
        }
    }

    [Fact]
    public void With_ReplacesSingleField_LeavesOthersUnchanged()
    {
        var customAvailability = new Dictionary<string, List<AvailabilityAnnotation>>
        {
            ["TestModule.Foo"] = new() { new AvailabilityAnnotation(
                Platform: "iOS",
                IntroducedVersion: "16.0",
                DeprecatedVersion: null,
                ObsoletedVersion: null,
                IsUnconditionallyDeprecated: false,
                IsUnconditionallyUnavailable: false,
                Message: null,
                Renamed: null) }
        };

        var baseline = SwiftInterfaceFacts.Empty;
        var facts = baseline with { AvailabilityAnnotations = customAvailability };

        Assert.Same(customAvailability, facts.AvailabilityAnnotations);
        // `with` re-uses the source's references for non-replaced fields.
        Assert.Same(baseline.InternalMemberKeys, facts.InternalMemberKeys);
        Assert.Same(baseline.PublicTypeNames, facts.PublicTypeNames);
        Assert.Same(baseline.MainActorTypes, facts.MainActorTypes);
    }

    [Fact]
    public void Empty_ReturnsFreshInstance_CollectionsAreIsolated()
    {
        // Per-access isolation: a caller mutating Empty's collections cannot contaminate
        // another caller's baseline. Required because the field types are concrete mutable
        // HashSet/Dictionary (chosen to match producer return types — see SwiftInterfaceFacts.cs).
        var first = SwiftInterfaceFacts.Empty;
        var second = SwiftInterfaceFacts.Empty;

        Assert.NotSame(first.InternalMemberKeys, second.InternalMemberKeys);
        Assert.NotSame(first.PublicTypeNames, second.PublicTypeNames);
        Assert.NotSame(first.AvailabilityAnnotations, second.AvailabilityAnnotations);

        first.InternalMemberKeys.Add("Contaminator");
        Assert.Empty(second.InternalMemberKeys);
    }

    #endregion

    #region Pass-through tests — representative facts reach their consumer
    // Seven of the 21 fact fields are covered directly here (the type-level ones whose consumer
    // effect is observable from a single TypeDecl). The remaining 14 fields plumb through to
    // MethodDecl/ArgumentDecl/PropertyDecl populated during full ABI parses; their consumer
    // paths are exercised by the per-domain suites under tests/UnitTests/ParserTests
    // (ActorMetadataParserTests, VariadicTypeSpec..., ProtocolTbdDescriptor..., etc.), all of
    // which now construct SwiftABIParser via SwiftInterfaceFacts.Empty and pass.

    [Fact]
    public void MainActorTypes_FlagsTypeAsMainActorIsolated()
    {
        var facts = SwiftInterfaceFacts.Empty with
        {
            MainActorTypes = new HashSet<string> { "Widget" }
        };

        var typeDecl = ParseSingleType(facts, "Widget", "Struct");
        Assert.True(typeDecl.IsMainActorIsolated);
    }

    [Fact]
    public void CustomActorTypes_FlagsTypeAsCustomActor()
    {
        var facts = SwiftInterfaceFacts.Empty with
        {
            CustomActorTypes = new HashSet<string> { "Worker" }
        };

        var typeDecl = ParseSingleType(facts, "Worker", "Class");
        Assert.True(typeDecl.IsCustomActor);
    }

    [Fact]
    public void CustomActorIsolatorMap_SetsIsolatorName()
    {
        var facts = SwiftInterfaceFacts.Empty with
        {
            CustomActorIsolatorMap = new Dictionary<string, string>
            {
                ["Pipeline"] = "ImagePipelineActor"
            }
        };

        var typeDecl = ParseSingleType(facts, "Pipeline", "Class");
        Assert.True(typeDecl.IsCustomActorIsolated);
        Assert.Equal("ImagePipelineActor", typeDecl.CustomActorIsolatorName);
    }

    [Fact]
    public void AvailabilityAnnotations_OnType_ProducesAnnotationsOnTypeDecl()
    {
        var annotation = new AvailabilityAnnotation(
            Platform: "iOS",
            IntroducedVersion: "16.0",
            DeprecatedVersion: null,
            ObsoletedVersion: null,
            IsUnconditionallyDeprecated: false,
            IsUnconditionallyUnavailable: false,
            Message: null,
            Renamed: null);
        var facts = SwiftInterfaceFacts.Empty with
        {
            AvailabilityAnnotations = new Dictionary<string, List<AvailabilityAnnotation>>
            {
                ["Gadget"] = new() { annotation }
            }
        };

        var typeDecl = ParseSingleType(facts, "Gadget", "Struct");
        Assert.NotNull(typeDecl.AvailabilityAnnotations);
        var entry = Assert.Single(typeDecl.AvailabilityAnnotations!);
        Assert.Equal("iOS", entry.Platform);
        Assert.Equal("16.0", entry.IntroducedVersion);
    }

    [Fact]
    public void Empty_DoesNotPopulateAvailabilityOnType()
    {
        var typeDecl = ParseSingleType(SwiftInterfaceFacts.Empty, "Untouched", "Struct");
        Assert.Null(typeDecl.AvailabilityAnnotations);
        Assert.False(typeDecl.IsMainActorIsolated);
        Assert.False(typeDecl.IsCustomActor);
        Assert.False(typeDecl.IsCustomActorIsolated);
    }

    [Fact]
    public void EmptyFacts_LeavesAllConsumerFlagsAtDefaults()
    {
        // Smoke test: every fact field empty produces a clean parse with no flags set.
        var typeDecl = ParseSingleType(SwiftInterfaceFacts.Empty, "Plain", "Struct");

        Assert.False(typeDecl.IsMainActorIsolated);
        Assert.False(typeDecl.IsCustomActor);
        Assert.False(typeDecl.IsCustomActorIsolated);
        Assert.Null(typeDecl.CustomActorIsolatorName);
        Assert.Null(typeDecl.AvailabilityAnnotations);
        Assert.False(typeDecl.IsModuleInternal);
    }

    [Fact]
    public void PublicTypeNames_NonEmpty_FlipsAbsentTypeToInternal()
    {
        // When PublicTypeNames is non-empty, any type NOT present is treated as module-internal
        // (negative-space detection — the swiftinterface lists every public type, so absence
        // means the ABI-only type was internal).
        var facts = SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "PublicOne", "PublicTwo" }
        };

        var typeDecl = ParseSingleType(facts, "SecretImpl", "Struct");
        Assert.True(typeDecl.IsModuleInternal);
    }

    [Fact]
    public void PublicTypeNames_NonEmpty_KeepsListedTypePublic()
    {
        var facts = SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "Listed" }
        };

        var typeDecl = ParseSingleType(facts, "Listed", "Struct");
        Assert.False(typeDecl.IsModuleInternal);
    }

    [Fact]
    public void ConventionCProtocols_FlagsProtocolHasConventionCClosureParameters()
    {
        var facts = SwiftInterfaceFacts.Empty with
        {
            ConventionCProtocols = new HashSet<string> { "Callback" }
        };

        var typeDecl = ParseSingleType(facts, "Callback", "Protocol");
        var protoDecl = Assert.IsType<ProtocolDecl>(typeDecl);
        Assert.True(protoDecl.HasConventionCClosureParameters);
    }

    // Finding 23 — @objc(CustomName) end-to-end generator-golden (full chain, not just pass-through).
    [Fact]
    public void ObjCRuntimeName_RealSwiftInterface_FlowsThroughToEmittedSidecar()
    {
        // ISSUE C(#1) generator-golden for F23's linchpin (@objc(CustomName)). A true sim
        // "round-trip through ObjC registration" is infeasible AND meaningless here: the custom
        // runtime name never reaches the generated C# — the .cs is byte-identical with or without
        // the rename. objcRuntimeName lives ONLY in the swift-types.json sidecar, consumed at BUILD
        // time to drop ObjC @interface decls a mixed framework's Swift side already owns. BindingTests
        // is a pure-Swift pipeline, so that consumer never fires on-device. The single ABI-observable
        // artifact is therefore the emitted sidecar, asserted full-chain below:
        //   real .swiftinterface text → GetObjCRuntimeNames (regex parse)
        //     → SwiftInterfaceFacts.ObjCRuntimeNames
        //     → SwiftABIParser.ApplyObjCRuntimeName (qualified-path bridge → TypeDecl.ObjCRuntimeName)
        //     → SwiftTypeOwnershipManifestEmitter.Emit (swift-types.json) → ReadOwnedObjCRuntimeNames
        var ifacePath = Path.GetTempFileName();
        var outDir = Directory.CreateTempSubdirectory("sb-objc-customname-");
        try
        {
            File.WriteAllText(ifacePath,
                "import Foundation\n" +
                "@objc(MOSWidget) public class Widget {\n" +
                "  @objc public init()\n" +
                "}\n");

            // 1. Real regex parse of the .swiftinterface → runtime-name map (top-level key = "Widget").
            var runtimeNames = SwiftInterfaceAccessParser.GetObjCRuntimeNames(ifacePath);
            Assert.Equal("MOSWidget", runtimeNames["Widget"]);

            // 2. Facts → parser → model. Exercises ApplyObjCRuntimeName + BuildTypeQualifiedPath, the
            //    one bridge no other test covers (parser tests stop at the map; manifest tests start
            //    from a model with ObjCRuntimeName already set).
            var facts = SwiftInterfaceFacts.Empty with { ObjCRuntimeNames = runtimeNames };
            var module = ParseModuleDecl(facts, "Widget", "Class");
            var classDecl = Assert.Single(module.Types);
            Assert.Equal("MOSWidget", classDecl.ObjCRuntimeName);

            // 3. Emit the real sidecar and read the owned ObjC runtime names back out of it.
            SwiftTypeOwnershipManifestEmitter.Emit(module, outDir.FullName, NullLogger.Instance);
            Assert.True(File.Exists(Path.Combine(outDir.FullName, SwiftTypeOwnershipManifest.FileName)));
            var owned = SwiftTypeOwnershipManifestEmitter.ReadOwnedObjCRuntimeNames(outDir.FullName);

            // The @objc(CustomName) value is what survives into emitted output as the dedup key …
            Assert.Contains("MOSWidget", owned);
            // … and the C# projection (the Swift source name) is NOT the key.
            Assert.DoesNotContain("Widget", owned);
        }
        finally
        {
            File.Delete(ifacePath);
            outDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(SwiftInterfaceFacts.InternalMemberKeys))]
    [InlineData(nameof(SwiftInterfaceFacts.PublicMemberNames))]
    [InlineData(nameof(SwiftInterfaceFacts.ParameterNames))]
    [InlineData(nameof(SwiftInterfaceFacts.TypedThrowsErrors))]
    [InlineData(nameof(SwiftInterfaceFacts.EnumCaseLabels))]
    [InlineData(nameof(SwiftInterfaceFacts.EnumCaseRawValues))]
    [InlineData(nameof(SwiftInterfaceFacts.PublicTypeNames))]
    [InlineData(nameof(SwiftInterfaceFacts.MainActorTypes))]
    [InlineData(nameof(SwiftInterfaceFacts.CustomActorTypes))]
    [InlineData(nameof(SwiftInterfaceFacts.CustomActorIsolatorMap))]
    [InlineData(nameof(SwiftInterfaceFacts.ActorIsolatedMembers))]
    [InlineData(nameof(SwiftInterfaceFacts.MainActorIsolatedMembers))]
    [InlineData(nameof(SwiftInterfaceFacts.NonisolatedMembers))]
    [InlineData(nameof(SwiftInterfaceFacts.MarkerProtocolConformances))]
    [InlineData(nameof(SwiftInterfaceFacts.AvailabilityAnnotations))]
    [InlineData(nameof(SwiftInterfaceFacts.DefaultParameterValues))]
    [InlineData(nameof(SwiftInterfaceFacts.AutoclosureParameters))]
    [InlineData(nameof(SwiftInterfaceFacts.SubscriptLabels))]
    [InlineData(nameof(SwiftInterfaceFacts.VariadicMembers))]
    [InlineData(nameof(SwiftInterfaceFacts.ConventionCProtocols))]
    [InlineData(nameof(SwiftInterfaceFacts.HiddenRequirementProtocols))]
    [InlineData(nameof(SwiftInterfaceFacts.MainActorTypePositions))]
    [InlineData(nameof(SwiftInterfaceFacts.AvailabilityAnnotationPositions))]
    [InlineData(nameof(SwiftInterfaceFacts.ConventionCProtocolPositions))]
    [InlineData(nameof(SwiftInterfaceFacts.ProtocolNames))]
    [InlineData(nameof(SwiftInterfaceFacts.ProtocolExtensionMethods))]
    [InlineData(nameof(SwiftInterfaceFacts.ExtensionMemberCandidates))]
    public void EachField_HasRequiredInitProperty(string propertyName)
    {
        // Each fact field must be a `required init` property — adding a new field without
        // updating Empty (or removing one without updating consumers) fails compilation.
        var prop = typeof(SwiftInterfaceFacts).GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
        Assert.True(prop.CanRead);
        Assert.True(prop.CanWrite);

        var setter = prop.SetMethod!;
        Assert.True(setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(t => t == typeof(System.Runtime.CompilerServices.IsExternalInit)),
            $"{propertyName} should be init-only (immutable record property).");
    }

    #endregion

    #region ResolveForeignExtensions partition tests

    [Fact]
    public void ResolveForeignExtensions_QualifiedSameModule_IsExcluded()
    {
        // `Mod.LocalType` when moduleName="Mod" is owned by this module — not foreign.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("Mod.LocalType", "ping"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string> { "LocalType" });

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveForeignExtensions_QualifiedForeignModule_IsIncluded()
    {
        // `UIKit.UIView` when moduleName="Mod" is foreign — module prefix mismatches.
        // Result key is the verbatim ExtendedTypeName.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("UIKit.UIView", "addBorder"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string>());

        var entry = Assert.Single(result);
        Assert.Equal("UIKit.UIView", entry.Key);
        var decl = Assert.Single(entry.Value);
        Assert.Equal("UIKit.UIView", decl.ProtocolQualifiedName);
        Assert.Equal("addBorder", decl.MethodName);
    }

    [Fact]
    public void ResolveForeignExtensions_UnqualifiedOwnedType_IsExcluded()
    {
        // `MyType` (no dot) when moduleTypeNames contains it — owned, not foreign.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("MyType", "doStuff"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod",
            new HashSet<string> { "MyType" });

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveForeignExtensions_UnqualifiedUnknownType_IsIncluded()
    {
        // `Stranger` (no dot) — not in moduleTypeNames, not a protocol — treated as foreign.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("Stranger", "act"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string>());

        var entry = Assert.Single(result);
        Assert.Equal("Stranger", entry.Key);
        Assert.Equal("Stranger", entry.Value[0].ProtocolQualifiedName);
    }

    [Fact]
    public void ResolveForeignExtensions_UnqualifiedProtocol_IsExcluded()
    {
        // `MyProto` (no dot) listed in ProtocolNames — surfaced via ProtocolExtensionMethods,
        // never as a foreign-type extension.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ProtocolNames = new HashSet<string> { "MyProto" },
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("MyProto", "describe"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveForeignExtensions_QualifiedProtocol_IsExcluded()
    {
        // `Mod.MyProto` where the typePath segment ("MyProto") matches a ProtocolNames entry —
        // even though it's qualified, the protocol-exclusion check uses typePath after the
        // first dot. This is the same behavior as the legacy regex producer.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ProtocolNames = new HashSet<string> { "MyProto" },
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("Mod.MyProto", "describe"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveForeignExtensions_GroupsCandidates_BySameExtendedTypeName()
    {
        // Multiple candidates on the same foreign type accumulate under a single dictionary
        // entry, in source order.
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>
            {
                MakeCandidate("UIKit.UIView", "first"),
                MakeCandidate("UIKit.UIView", "second"),
                MakeCandidate("UIKit.UILabel", "third"),
            },
        };

        var result = facts.ResolveForeignExtensions("Mod", new HashSet<string>());

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "first", "second" },
            result["UIKit.UIView"].Select(d => d.MethodName).ToArray());
        Assert.Equal(new[] { "third" },
            result["UIKit.UILabel"].Select(d => d.MethodName).ToArray());
    }

    [Fact]
    public void ResolveForeignExtensions_CandidateToDecl_PreservesEveryField()
    {
        // The 1:1 candidate→decl conversion must carry every behavioral field through.
        // Drift-loud guard: if a field is added to ExtensionMemberCandidate without updating
        // CandidateToDecl, this assertion fails.
        var candidate = new ExtensionMemberCandidate
        {
            ExtendedTypeName = "Foreign.Type",
            MethodName = "exotic",
            RawSignature = "public mutating func exotic<T>() async throws -> Self where T : Bound",
            PrintedName = "exotic()",
            ReturnsSelf = true,
            IsMainActorIsolated = true,
            IsStatic = true,
            IsProperty = false,
            HasSetter = false,
            IsDeprecated = true,
            IsMutating = true,
            WhereConstraints = new List<string> { "T : Bound" },
        };
        var facts = SwiftInterfaceFacts.Empty with
        {
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate> { candidate },
        };

        var decl = facts.ResolveForeignExtensions("Mod", new HashSet<string>())["Foreign.Type"][0];

        Assert.Equal("Foreign.Type", decl.ProtocolQualifiedName);
        Assert.Equal(candidate.MethodName, decl.MethodName);
        Assert.Equal(candidate.RawSignature, decl.RawSignature);
        Assert.Equal(candidate.PrintedName, decl.PrintedName);
        Assert.Equal(candidate.ReturnsSelf, decl.ReturnsSelf);
        Assert.Equal(candidate.IsMainActorIsolated, decl.IsMainActorIsolated);
        Assert.Equal(candidate.IsStatic, decl.IsStatic);
        Assert.Equal(candidate.IsProperty, decl.IsProperty);
        Assert.Equal(candidate.HasSetter, decl.HasSetter);
        Assert.Equal(candidate.IsDeprecated, decl.IsDeprecated);
        Assert.Equal(candidate.IsMutating, decl.IsMutating);
        Assert.Equal(candidate.WhereConstraints, decl.WhereConstraints);
        // Defensive copy — mutating the source must not contaminate the decl.
        candidate.WhereConstraints.Add("INTRUDER");
        Assert.DoesNotContain("INTRUDER", decl.WhereConstraints);
    }

    private static ExtensionMemberCandidate MakeCandidate(string extendedTypeName, string methodName) =>
        new()
        {
            ExtendedTypeName = extendedTypeName,
            MethodName = methodName,
            RawSignature = $"public func {methodName}()",
            PrintedName = $"{methodName}()",
        };

    #endregion

    #region Helpers

    private static TypeDecl ParseSingleType(SwiftInterfaceFacts facts, string typeName, string declKind)
        => Assert.Single(ParseModuleDecl(facts, typeName, declKind).Types);

    private static ModuleDecl ParseModuleDecl(SwiftInterfaceFacts facts, string typeName, string declKind)
    {
        var moduleNode = new
        {
            Kind = "Import",
            DeclKind = "",
            Name = "TestModule",
            MangledName = "",
            PrintedName = "TestModule",
            ModuleName = "TestModule",
            DeclAttributes = System.Array.Empty<string>(),
            @static = false,
            IsInternal = false,
            GenericSig = "",
            sugared_genericSig = "",
            throwing = false,
            AccessorKind = "",
            EnumRawTypeName = "",
            paramValueOwnership = "",
            hasDefaultArg = false,
            Children = System.Array.Empty<object>(),
            Conformances = System.Array.Empty<object>(),
            Accessors = System.Array.Empty<object>(),
        };

        var typeNode = new
        {
            Kind = "TypeDecl",
            DeclKind = declKind,
            Name = typeName,
            MangledName = $"$s10TestModule{typeName.Length}{typeName}V",
            PrintedName = typeName,
            ModuleName = "TestModule",
            DeclAttributes = System.Array.Empty<string>(),
            @static = false,
            IsInternal = false,
            GenericSig = "",
            sugared_genericSig = "",
            throwing = false,
            AccessorKind = "",
            EnumRawTypeName = "",
            paramValueOwnership = "",
            hasDefaultArg = false,
            Children = System.Array.Empty<object>(),
            Conformances = System.Array.Empty<object>(),
            Accessors = System.Array.Empty<object>(),
        };

        var root = new
        {
            ABIRoot = new
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new object[] { moduleNode, typeNode },
            },
        };

        var path = Path.GetTempFileName();
        File.WriteAllText(path, JsonConvert.SerializeObject(root));
        try
        {
            var parser = new SwiftABIParser(
                path,
                new TypeDatabase(),
                CreateEmptyDemanglingResults(),
                NullLogger.Instance,
                facts);
            return parser.ParseModule().ModuleDecl;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), null!]);
    }

    #endregion
}
