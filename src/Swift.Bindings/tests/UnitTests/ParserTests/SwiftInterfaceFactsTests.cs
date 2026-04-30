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
    public void Empty_ContainsTwentyOneRequiredCollections()
    {
        // Drift-loud guard: if a field is added to SwiftInterfaceFacts without updating Empty,
        // either compilation fails (required init property) or this count check trips.
        var properties = typeof(SwiftInterfaceFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null)
            .ToList();

        Assert.Equal(21, properties.Count);

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
        // Each fact field must be a HashSet<...> or Dictionary<...,...> — concrete collection
        // types that Program.GenerateBindings can populate without interface conversions.
        var properties = typeof(SwiftInterfaceFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null);

        foreach (var prop in properties)
        {
            var typeName = prop.PropertyType.Name;
            Assert.True(typeName.StartsWith("HashSet") || typeName.StartsWith("Dictionary"),
                $"{prop.Name} is {prop.PropertyType.FullName}; expected HashSet or Dictionary.");
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
    // Six of the 21 fact fields are covered directly here (the type-level ones whose consumer
    // effect is observable from a single TypeDecl). The remaining 15 fields plumb through to
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
    public void EachField_HasRequiredInitProperty(string propertyName)
    {
        // Each of the 21 fields must be a `required init` property — adding a new field without
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

    #region Helpers

    private static TypeDecl ParseSingleType(SwiftInterfaceFacts facts, string typeName, string declKind)
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
            var module = parser.ParseModule();
            return Assert.Single(module.ModuleDecl.Types);
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
