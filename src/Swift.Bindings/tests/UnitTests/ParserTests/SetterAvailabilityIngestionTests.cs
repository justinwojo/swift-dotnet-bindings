// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Ingestion coverage for accessor-level availability: a property whose <c>set</c> accessor
/// declares a newer introduced version than the property itself.
///
/// The merge rule is unit-tested directly elsewhere; what these assert is the wiring around
/// it — that the ABI-JSON <c>accessors</c> array is actually read, that the <c>set</c> node's
/// <c>intro_*</c> fields land on <see cref="PropertyDecl.SetterAvailabilityAnnotations"/>, and
/// that the setter's own <see cref="MethodDecl"/> is retargeted to the tighter list while the
/// getter keeps the property's. Everything downstream (the Swift setter forwarder, the C#
/// setter P/Invoke, the <c>set</c> accessor attributes on the interface and the proxy) reads
/// one of those two places, so a break here silently annotates a setter at the wrong floor.
///
/// This shape reaches the parser when the ABI JSON is dumped from a compiled module. It does
/// NOT survive a textual <c>.swiftinterface</c> round-trip: that format prints the requirement
/// as <c>{ get set }</c> with no accessor attributes, so a binding generated from an interface
/// sees only the property's floor and the assertions below cannot be staged end-to-end there.
/// </summary>
public class SetterAvailabilityIngestionTests
{
    private const int AbiFormatVersion = 8; // mirrors SwiftABIParser.ExpectedAbiFormatVersion (internal)

    [Fact]
    public void ParseModule_SetAccessorIntroducedLater_PopulatesSetterAvailability()
    {
        using var fixture = CreateParser(ProtocolWithStaggeredSetter(setterIntroIos: "17.0"));

        var property = SingleProperty(fixture);

        Assert.NotNull(property.SetterAvailabilityAnnotations);
        var annotation = Assert.Single(property.SetterAvailabilityAnnotations!);
        Assert.Equal("iOS", annotation.Platform);
        Assert.Equal("17.0", annotation.IntroducedVersion);
    }

    [Fact]
    public void ParseModule_SetAccessorIntroducedLater_RetargetsOnlyTheSetterMethod()
    {
        using var fixture = CreateParser(ProtocolWithStaggeredSetter(setterIntroIos: "17.0"));

        var property = SingleProperty(fixture);

        var setter = Assert.Single(property.Accessors.OfType<SetAccessorDecl>());
        Assert.Contains(
            setter.Method.AvailabilityAnnotations ?? new List<AvailabilityAnnotation>(),
            a => a.Platform == "iOS" && a.IntroducedVersion == "17.0");

        var getter = Assert.Single(property.Accessors.OfType<GetAccessorDecl>());
        Assert.DoesNotContain(
            getter.Method.AvailabilityAnnotations ?? new List<AvailabilityAnnotation>(),
            a => a.Platform == "iOS" && a.IntroducedVersion == "17.0");
    }

    [Fact]
    public void ParseModule_SetAccessorWithoutOwnIntroducedVersion_LeavesSetterAvailabilityUnset()
    {
        using var fixture = CreateParser(ProtocolWithStaggeredSetter(setterIntroIos: null));

        var property = SingleProperty(fixture);

        Assert.Null(property.SetterAvailabilityAnnotations);
    }

    /// <summary>
    /// The ABI accessor node states an introduced version and nothing else, so a property that
    /// is ALSO deprecated/obsoleted must hand those fields down to the setter list unchanged —
    /// otherwise the setter's attributes advertise a later floor but drop the deprecation the
    /// getter still carries, and a consumer sees the same property described two ways.
    /// </summary>
    [Fact]
    public void ParseModule_SetAccessorIntroducedLater_SetterInheritsPropertyDeprecation()
    {
        using var fixture = CreateParser(
            DeprecatedProtocolFacts(),
            ProtocolWithStaggeredSetter(setterIntroIos: "17.0"));

        var property = SingleProperty(fixture);

        var setterAnnotation = Assert.Single(property.SetterAvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("17.0", setterAnnotation.IntroducedVersion);
        Assert.Equal("18.0", setterAnnotation.DeprecatedVersion);
        Assert.Equal("19.0", setterAnnotation.ObsoletedVersion);
        Assert.Equal("use valueAsync", setterAnnotation.Message);

        // The setter's own MethodDecl reads the same merged list — the async/subscript setter
        // paths take their availability from there rather than from the property.
        var setter = Assert.Single(property.Accessors.OfType<SetAccessorDecl>());
        var setterMethodAnnotation = Assert.Single(
            setter.Method.AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("17.0", setterMethodAnnotation.IntroducedVersion);
        Assert.Equal("18.0", setterMethodAnnotation.DeprecatedVersion);

        // The property (and so the getter) keeps its own, earlier floor.
        var propertyAnnotation = Assert.Single(property.AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("16.0", propertyAnnotation.IntroducedVersion);
    }

    #region Harness

    /// <summary>
    /// Interface facts putting the protocol's <c>value</c> requirement at iOS 16, deprecated at
    /// 18 and obsoleted at 19 with a message — the property-level half of the merge, which the
    /// ABI JSON does not carry.
    /// </summary>
    private static SwiftInterfaceFacts DeprecatedProtocolFacts()
    {
        return SwiftInterfaceFacts.Empty with
        {
            AvailabilityAnnotations = new Dictionary<string, List<AvailabilityAnnotation>>
            {
                ["Stagger.value"] = new()
                {
                    new(Platform: "iOS", IntroducedVersion: "16.0", DeprecatedVersion: "18.0",
                        ObsoletedVersion: "19.0", IsUnconditionallyDeprecated: false,
                        IsUnconditionallyUnavailable: false, Message: "use valueAsync", Renamed: null),
                },
            },
        };
    }

    private static PropertyDecl SingleProperty(ParserFixture fixture)
    {
        var result = fixture.Parser.ParseModule();
        var protocolDecl = Assert.Single(result.ModuleDecl.Protocols);
        return Assert.Single(protocolDecl.Properties);
    }

    /// <summary>
    /// A protocol carrying one <c>Int32</c> requirement with both accessors, where the
    /// <c>set</c> accessor optionally declares its own introduced iOS version.
    /// </summary>
    private static Node ProtocolWithStaggeredSetter(string? setterIntroIos)
    {
        var int32 = CreateNode("TypeNominal", name: "Int32", printedName: "Swift.Int32", usr: "s:s5Int32V");

        var getter = CreateNode(
            "Accessor", declKind: "Accessor", name: "Get", printedName: "Get()",
            mangledName: "$s10TestModule7StaggerP5values5Int32Vvg",
            children: new[] { int32 });
        getter.AccessorKind = "get";
        getter.protocolReq = true;

        var setter = CreateNode(
            "Accessor", declKind: "Accessor", name: "Set", printedName: "Set()",
            mangledName: "$s10TestModule7StaggerP5values5Int32Vvs",
            children: new[]
            {
                CreateNode("TypeNominal", name: "Void", printedName: "()"),
                int32,
            });
        setter.AccessorKind = "set";
        setter.protocolReq = true;
        if (setterIntroIos != null)
        {
            setter.intro_iOS = setterIntroIos;
            setter.DeclAttributes = new[] { "Available" };
        }

        var property = CreateNode(
            "Var", declKind: "Var", name: "value", printedName: "value",
            mangledName: "$s10TestModule7StaggerP5values5Int32Vvp",
            children: new[] { int32 });
        property.protocolReq = true;
        property.Accessors = new[] { getter, setter };

        return CreateNode(
            "TypeDecl", declKind: "Protocol", name: "Stagger", printedName: "Stagger",
            mangledName: "$s10TestModule7StaggerP",
            children: new[] { property });
    }

    private static ParserFixture CreateParser(params Node[] nodes)
        => CreateParser(SwiftInterfaceFacts.Empty, nodes);

    private static ParserFixture CreateParser(SwiftInterfaceFacts facts, params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                json_format_version = AbiFormatVersion,
                Children = nodes,
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            facts);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IReduction[]), typeof(HashSet<string>) },
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke(new object?[] { Array.Empty<IReduction>(), null });
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string? printedName = null,
        string moduleName = "TestModule",
        string mangledName = "$s",
        IEnumerable<Node>? children = null,
        string? usr = null)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = printedName ?? name,
            ModuleName = moduleName,
            usr = usr,
            isExternal = null,
            DeclAttributes = Array.Empty<string>(),
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = children ?? Array.Empty<Node>(),
            Conformances = Array.Empty<Node>(),
            Accessors = Array.Empty<Node>(),
        };
    }

    private sealed class ParserFixture : IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }

    #endregion
}
