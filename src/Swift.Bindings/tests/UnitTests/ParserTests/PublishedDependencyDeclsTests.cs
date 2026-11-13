// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A dependency supplied as a published module database AND as its xcframework — the input every SDK
/// consumer of a dependency binding has — must still contribute its declarations to the bound module.
///
/// <para>The database supplies the dependency's type records, so the generator never finalizes the
/// dependency's ABI again. Before the declaration read, that also meant the dependency's protocols
/// never reached the bound module: a local protocol refining a dependency protocol got an
/// <c>EveryProtocol</c> conformance with none of the parent's witnesses, and the consumer's wrapper
/// compile failed. These tests pin the read itself; the end-to-end shape is the dependency-input
/// equivalence step of the BindingTests compile gate.</para>
/// </summary>
public class PublishedDependencyDeclsTests
{
    private const string DepModule = "DepModule";

    [Fact]
    public void PublishedDependency_ProtocolsAndModuleDeclReachTheBoundModule()
    {
        using var dep = DependencyInputs.Create(DepModule, "Parent");
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var dependencyProtocols = new Dictionary<string, List<ProtocolDecl>>();

        Read(dep, typeDatabase, dependencyProtocols, published: new[] { DepModule });

        var protocols = Assert.Single(dependencyProtocols).Value;
        var parent = Assert.Single(protocols);
        Assert.Equal("Parent", parent.Name);
        Assert.Contains(parent.Methods, m => m.Name == "requirement");

        var moduleDecl = Assert.Single(typeDatabase.GetDependencyModuleDecls());
        Assert.Equal(DepModule, moduleDecl.Name);

        // The manifest records where each half of the dependency came from.
        Assert.False(InputResolutionReport.HasDegradations);
        Assert.Contains(InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.Dependency && d.Detail.Contains("module database"));
    }

    [Fact]
    public void PublishedDependency_WhoseAbiIsDegraded_KeepsTheDegradationForStrictInputs()
    {
        // The bound module emits from these declarations, so a degraded read must reach
        // --strict-inputs exactly as the same dependency's raw-ABI parse would.
        using var dep = DependencyInputs.Create(DepModule, "Parent", formatVersion: null);
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");

        Read(dep, typeDatabase, new Dictionary<string, List<ProtocolDecl>>(), published: new[] { DepModule });

        Assert.Contains(InputResolutionReport.Decisions,
            d => d.Severity == InputResolutionSeverity.Degradation &&
                 d.Category == InputResolutionCategory.AbiJson && d.Detail.Contains(DepModule));
    }

    [Fact]
    public void PublishedDependency_ReadLeavesThePublishedRecordsAuthoritative()
    {
        // The read must not re-register or replace the database's records: the database is the
        // dependency's resolved input, and its C# names are what the dependency binding shipped.
        using var dep = DependencyInputs.Create(DepModule, "Parent");
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var name = SwiftTypeName.FromModuleQualifiedName($"{DepModule}.Parent");
        Assert.True(typeDatabase.TryGetTypeRecord(name, out var before));

        Read(dep, typeDatabase, new Dictionary<string, List<ProtocolDecl>>(), published: new[] { DepModule });

        Assert.True(typeDatabase.TryGetTypeRecord(name, out var after));
        Assert.Same(before, after);
    }

    [Fact]
    public void DependencyWithoutAPublishedDatabase_IsNotReadHere()
    {
        // A dependency supplied only as an xcframework already went through the raw-ABI branch,
        // which owns its declarations; reading it again here would register it twice.
        using var dep = DependencyInputs.Create(DepModule, "Parent");
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var dependencyProtocols = new Dictionary<string, List<ProtocolDecl>>();

        Read(dep, typeDatabase, dependencyProtocols, published: Array.Empty<string>());

        Assert.Empty(dependencyProtocols);
        Assert.Empty(typeDatabase.GetDependencyModuleDecls());
    }

    [Fact]
    public void PublishedDependency_ThatFailsToParse_IsRecordedAsADegradedInput()
    {
        // The records still resolve, but the declaration surface is gone and the bound module
        // emits less. That is a degraded input, which --strict-inputs must be able to fail.
        using var dep = DependencyInputs.Create(DepModule, "Parent");
        File.WriteAllText(dep.Info.AbiJsonPath!, "{ not json");
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var dependencyProtocols = new Dictionary<string, List<ProtocolDecl>>();

        Read(dep, typeDatabase, dependencyProtocols, published: new[] { DepModule });

        Assert.Empty(dependencyProtocols);
        Assert.True(InputResolutionReport.HasDegradations);
        Assert.Contains(InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.Dependency && d.Detail.Contains(DepModule));
    }

    [Fact]
    public void PlainParser_DropsEveryTypeOfAModuleWhoseRecordsAreAlreadyRegistered()
    {
        // The negative control for PublishedModuleDeclarationResolver: without it, re-reading a
        // module whose records came from its database meets each of its own types already
        // registered, faults it as a duplicate, and drops it — the read would yield an empty module.
        using var dep = DependencyInputs.Create(DepModule, "Parent");
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var tbd = Demangling.DemanglingResults.FromTbd(dep.Info.TbdPath!, NullLoggerFactory.Instance);
        InputResolutionReport.Reset();

        var parser = new SwiftABIParser(
            dep.Info.AbiJsonPath!, typeDatabase, tbd, NullLogger<SwiftABIParser>.Instance,
            SwiftInterfaceFacts.Empty, new LegacyCrossModuleFactResolver(typeDatabase, tbd), docComments: null);
        var moduleDecl = parser.ParseModule().ModuleDecl;

        Assert.Empty(moduleDecl.Protocols);
        Assert.Contains(InputResolutionReport.Ledger, e => e.Cause == IngestionCause.ParseFault);
        InputResolutionReport.Reset();
    }

    [Fact]
    public void PublishedModuleResolver_HidesOnlyTheModuleBeingRead()
    {
        var typeDatabase = TypeDatabaseWithPublishedRecords(DepModule, "Parent");
        var other = new ModuleTypeDatabase("OtherModule", "/tmp/OtherModule.dylib");
        var otherName = SwiftTypeName.FromModuleQualifiedName("OtherModule.Thing");
        other.RegisterType(otherName, Record(otherName, "OtherModule", "Thing", TypeRecordKind.Struct));
        typeDatabase.AddModuleDatabase(other);
        var tbd = Demangling.DemanglingResults.FromTbd(WriteEmptyTbd(), NullLoggerFactory.Instance);

        var inner = new LegacyCrossModuleFactResolver(typeDatabase, tbd);
        var resolver = new PublishedModuleDeclarationResolver(inner, DepModule);
        var own = SwiftTypeName.FromModuleQualifiedName($"{DepModule}.Parent");

        Assert.True(inner.IsTypeRegistered(own));
        Assert.False(resolver.IsTypeRegistered(own));
        Assert.False(resolver.TryGetForeignTypeShape(own, out _));

        Assert.True(resolver.IsTypeRegistered(otherName));
        Assert.Equal(inner.TryGetForeignTypeShape(otherName, out var expected),
            resolver.TryGetForeignTypeShape(otherName, out var actual));
        Assert.Equal(expected, actual);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static void Read(
        DependencyInputs dep,
        TypeDatabase typeDatabase,
        Dictionary<string, List<ProtocolDecl>> dependencyProtocols,
        string[] published)
    {
        InputResolutionReport.Reset();
        BindingsGenerator.ReadPublishedDependencyDecls(
            new[] { dep.Info },
            new HashSet<string>(published, StringComparer.Ordinal),
            currentModuleName: "BoundModule",
            typeDatabase,
            ModuleFactIndexSet.Empty,
            dependencyProtocols,
            new HashSet<string>(StringComparer.Ordinal),
            new NominalSkeletonIndex(),
            NullLoggerFactory.Instance,
            NullLogger.Instance);
    }

    /// <summary>A type database holding the dependency's records, as <c>--module-database</c> loads them.</summary>
    private static TypeDatabase TypeDatabaseWithPublishedRecords(string module, string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var moduleDb = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        var name = SwiftTypeName.FromModuleQualifiedName($"{module}.{protocolName}");
        moduleDb.RegisterType(name, Record(name, module, $"I{protocolName}", TypeRecordKind.Protocol));
        typeDatabase.AddModuleDatabase(moduleDb);
        return typeDatabase;
    }

    private static TypeRecord Record(SwiftTypeName name, string module, string csharpName, TypeRecordKind kind) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, csharpName),
        SwiftTypeName = name,
        MetadataAccessor = "$sMa",
        Flags = TypeRecordFlags.Frozen,
        Kind = kind,
    };

    private static string WriteEmptyTbd()
    {
        var path = Path.Combine(Path.GetTempPath(), $"published_dep_{Guid.NewGuid():N}.tbd");
        File.WriteAllText(path, "--- !tapi-tbd\n");
        return path;
    }

    /// <summary>A dependency's xcframework-derived inputs: an ABI JSON declaring one protocol, and a TBD.</summary>
    private sealed class DependencyInputs : IDisposable
    {
        private readonly string _dir;

        private DependencyInputs(string dir, FrameworkDependencyInfo info)
        {
            _dir = dir;
            Info = info;
        }

        public FrameworkDependencyInfo Info { get; }

        public static DependencyInputs Create(
            string module, string protocolName, int? formatVersion = SwiftABIParser.ExpectedAbiFormatVersion)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"published_dep_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var abiPath = Path.Combine(dir, $"{module}.abi.json");
            var tbdPath = Path.Combine(dir, $"{module}.tbd");

            var protocolMangled = $"$s{module.Length}{module}{protocolName.Length}{protocolName}P";
            var method = Node("Function", "Func", "requirement", module, $"{protocolMangled}11requirementyyF");
            method.PrintedName = "requirement()";
            method.protocolReq = true;
            method.Children = new[] { Node("TypeNominal", "", "Void", module, "$s") };
            var protocol = Node("TypeDecl", "Protocol", protocolName, module, protocolMangled);
            protocol.Children = new[] { method };

            var root = new ABIRootNode
            {
                ABIRoot = new RootNode
                {
                    Kind = "Root",
                    Name = "Root",
                    PrintedName = "Root",
                    json_format_version = formatVersion,
                    Children = new[] { Node("Import", "", module, module, "$s"), protocol },
                },
            };
            File.WriteAllText(abiPath, JsonConvert.SerializeObject(root));
            File.WriteAllText(tbdPath, "--- !tapi-tbd\n");

            return new DependencyInputs(dir, new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(dir, $"{module}.xcframework"),
                ModuleName = module,
                AbiJsonPath = abiPath,
                TbdPath = tbdPath,
            });
        }

        private static Node Node(string kind, string declKind, string name, string module, string mangledName) => new()
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = module,
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

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}
