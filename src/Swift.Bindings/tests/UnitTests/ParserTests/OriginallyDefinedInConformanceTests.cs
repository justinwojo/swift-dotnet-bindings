// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
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
/// Coverage for <see cref="SwiftABIParser.HandleConformance"/>'s <c>@_originallyDefinedIn</c>
/// conformance-descriptor fallback.
///
/// Apple umbrella frameworks re-export types from a sibling module with
/// <c>@_originallyDefinedIn</c> — e.g. <c>RealityFoundation</c> re-exports
/// <c>RealityKit</c>'s <c>AnchorEntity</c>. The type decl is attributed to its CURRENT
/// module via its USR (<c>RealityFoundation.AnchorEntity</c>), but the TBD's
/// conformance-descriptor symbol (<c>...Mc</c>) is mangled with the type's ORIGINAL
/// module (<c>RealityKit.AnchorEntity</c>). A current-module-only descriptor lookup
/// misses, the conformance descriptor comes out empty, and the existential box for
/// <c>RealityFoundation.HasAnchoring</c> later fails to resolve a witness table.
///
/// The fix: on a primary-lookup miss, derive the original module from the type's own
/// mangled name (<see cref="SwiftABIParser.TryGetModuleFromMangledName"/>) and retry the
/// descriptor lookup keyed under the original-module identity. The protocol identity is
/// unaffected (it already comes from the conformance's mangled name), so only the
/// implementing type's module diverges.
///
/// These are the durable in-repo gate for this parser-layer fix: a runtime BindingTests
/// fixture cannot reproduce it (a fake re-export module isn't constructable), so the real
/// end-to-end validation is RealityFoundation itself in swift-dotnet-packages.
/// </summary>
public class OriginallyDefinedInConformanceTests
{
    // A realistic AnchorEntity:HasAnchoring conformance-descriptor symbol, mangled with the
    // ORIGINAL module (RealityKit) per @_originallyDefinedIn. The exact value is opaque to
    // the parser — it only round-trips it from the matched reduction to the TypeConformance.
    private const string OriginalModuleDescriptorSymbol =
        "$s10RealityKit12AnchorEntityCAA12HasAnchoringAAMc";

    private const string CurrentModuleDescriptorSymbol =
        "$s17RealityFoundation12AnchorEntityCAA12HasAnchoringAAMc";

    // Conformance protocol mangled name → demangles to RealityFoundation.HasAnchoring.
    private const string ProtocolMangledName = "$s17RealityFoundation12HasAnchoringP";

    [Fact]
    public void OriginallyDefinedIn_DescriptorMangledWithOriginalModule_ResolvedViaFallback()
    {
        // The type's USR module is RealityFoundation (current) but its mangled name carries
        // RealityKit (original). The descriptor is keyed ONLY under RealityKit.AnchorEntity,
        // so the primary RealityFoundation.AnchorEntity lookup misses. The mangled-name
        // fallback must recover it — this is the core of the fix.
        var protocolModuleQualified = DemangleProtocolModuleQualifiedName(ProtocolMangledName);

        var classNode = CreateNode(
            kind: "TypeDecl", declKind: "Class", name: "AnchorEntity",
            moduleName: "RealityFoundation",
            mangledName: "$s10RealityKit12AnchorEntityCN");
        classNode.Conformances = new[] { CreateConformanceNode("HasAnchoring", ProtocolMangledName) };

        var reduction = new ProtocolConformanceDescriptorReduction
        {
            Symbol = OriginalModuleDescriptorSymbol,
            ImplementingType = new NamedTypeSpec("RealityKit.AnchorEntity"),
            ProtocolType = new NamedTypeSpec(protocolModuleQualified),
            Module = "RealityKit",
        };

        using var fixture = CreateParserWithReductions(new IReduction[] { reduction }, classNode);
        var result = fixture.Parser.ParseModule();

        var entity = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        Assert.Equal("AnchorEntity", entity.Name);
        Assert.Equal("RealityFoundation", entity.SwiftTypeName.Module);

        var conformance = Assert.Single(entity.Conformances);
        Assert.Equal(protocolModuleQualified, conformance.Protocol.ModuleQualifiedName);
        Assert.Equal(OriginalModuleDescriptorSymbol, conformance.ProtocolConformanceDescriptor);
    }

    [Fact]
    public void NativeType_DescriptorMangledWithCurrentModule_ResolvedViaPrimaryLookup()
    {
        // Baseline: a type whose mangled name matches its USR module (no @_originallyDefinedIn).
        // The descriptor is keyed under the current module, so the PRIMARY lookup hits and the
        // fallback never fires. Guards against the fix regressing the normal path.
        var protocolModuleQualified = DemangleProtocolModuleQualifiedName(ProtocolMangledName);

        var classNode = CreateNode(
            kind: "TypeDecl", declKind: "Class", name: "AnchorEntity",
            moduleName: "RealityFoundation",
            mangledName: "$s17RealityFoundation12AnchorEntityCN");
        classNode.Conformances = new[] { CreateConformanceNode("HasAnchoring", ProtocolMangledName) };

        var reduction = new ProtocolConformanceDescriptorReduction
        {
            Symbol = CurrentModuleDescriptorSymbol,
            ImplementingType = new NamedTypeSpec("RealityFoundation.AnchorEntity"),
            ProtocolType = new NamedTypeSpec(protocolModuleQualified),
            Module = "RealityFoundation",
        };

        using var fixture = CreateParserWithReductions(new IReduction[] { reduction }, classNode);
        var result = fixture.Parser.ParseModule();

        var entity = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        var conformance = Assert.Single(entity.Conformances);
        Assert.Equal(CurrentModuleDescriptorSymbol, conformance.ProtocolConformanceDescriptor);
    }

    [Fact]
    public void OriginallyDefinedIn_NoDescriptorForEitherModule_DescriptorEmptyAndTypeStillEmitted()
    {
        // Some conformances are inherent (e.g. Copyable) and never ship a TBD descriptor.
        // When neither the current- nor original-module lookup finds one, the fallback must
        // degrade gracefully: empty descriptor, conformance still carried, type not dropped.
        var protocolModuleQualified = DemangleProtocolModuleQualifiedName(ProtocolMangledName);

        var classNode = CreateNode(
            kind: "TypeDecl", declKind: "Class", name: "AnchorEntity",
            moduleName: "RealityFoundation",
            mangledName: "$s10RealityKit12AnchorEntityCN");
        classNode.Conformances = new[] { CreateConformanceNode("HasAnchoring", ProtocolMangledName) };

        using var fixture = CreateParserWithReductions(Array.Empty<IReduction>(), classNode);
        var result = fixture.Parser.ParseModule();

        var entity = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        var conformance = Assert.Single(entity.Conformances);
        Assert.Equal(protocolModuleQualified, conformance.Protocol.ModuleQualifiedName);
        Assert.Equal(string.Empty, conformance.ProtocolConformanceDescriptor);
    }

    #region Test Helpers

    /// <summary>
    /// Demangles the protocol mangled name through the same demangler the parser uses, so the
    /// reduction's <c>ProtocolType.Name</c> matches whatever module-qualified identity
    /// <see cref="SwiftABIParser.HandleConformance"/> derives — the descriptor lookup keys on it.
    /// </summary>
    private static string DemangleProtocolModuleQualifiedName(string mangledName)
    {
        var reduction = (TypeSpecReduction)new Swift5Demangler().Run(mangledName);
        var spec = (NamedTypeSpec)reduction.TypeSpec;
        return SwiftTypeName.FromTypeSpec(spec).ModuleQualifiedName;
    }

    private static Node CreateConformanceNode(string protocolName, string mangledName)
    {
        return new Node
        {
            Kind = "Conformance",
            DeclKind = "",
            Name = protocolName,
            MangledName = mangledName,
            PrintedName = protocolName,
            ModuleName = "RealityFoundation",
            DeclAttributes = [],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = [],
            Conformances = [],
            Accessors = [],
        };
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "RealityFoundation",
        string mangledName = "$s")
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
            DeclAttributes = [],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = [],
            Conformances = [],
            Accessors = [],
        };
    }

    private static ParserFixture CreateParserWithReductions(IReduction[] reductions, params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = nodes,
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateDemanglingResults(reductions),
            NullLogger<SwiftABIParser>.Instance,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateDemanglingResults(IReduction[] reductions)
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([reductions, null]);
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
