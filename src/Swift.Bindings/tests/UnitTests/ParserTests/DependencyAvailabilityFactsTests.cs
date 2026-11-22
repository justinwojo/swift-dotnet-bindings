// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A dependency read from its xcframework takes its declarations' availability from its
/// <c>.swiftinterface</c>; the ABI JSON records none. Without it, the bound module does not know that
/// a dependency enum's case was added in a later OS, so it hands that case to Swift unrefused and
/// its wrapper's <c>init(rawValue:)</c> precondition traps on an older OS. Only availability is read:
/// every other dependency decision stays the one the ABI read makes.
/// </summary>
public class DependencyAvailabilityFactsTests
{
    private const string DepModule = "DepModule";

    private static readonly AvailabilityAnnotation Ios99 =
        new("iOS", "99.0", null, null, false, false, null, null);

    [Fact]
    public void DependencyWithAnInterface_ContributesItsAvailabilityAndNothingElse()
    {
        using var dir = new TempDir();
        var producer = new StubProducer(new PartialSwiftInterfaceFacts
        {
            AvailabilityAnnotations = new() { ["Mode.future"] = new() { Ios99 } },
            PublicTypeNames = new() { "Mode" },
            InternalMemberKeys = new() { "Mode.hidden" },
        });
        var dep = Dependency(dir.Interface());

        var facts = BindingsGenerator.ReadDependencyAvailabilityFacts(
            dep, () => new InterfaceFactsAggregator(new IInterfaceFactsProducer[] { producer }), NullLogger.Instance);

        Assert.Equal(1, producer.Calls);
        Assert.Equal(Ios99, Assert.Single(facts.AvailabilityAnnotations["Mode.future"]));
        Assert.Empty(facts.PublicTypeNames);
        Assert.Empty(facts.InternalMemberKeys);
    }

    [Fact]
    public void DependencyWithoutAnInterface_ReadsNoFacts()
    {
        var facts = BindingsGenerator.ReadDependencyAvailabilityFacts(
            Dependency(swiftInterfacePath: null),
            () => throw new InvalidOperationException("No interface, so no aggregator."),
            NullLogger.Instance);

        Assert.Empty(facts.AvailabilityAnnotations);
    }

    /// <summary>
    /// The end of the chain: the dependency's floored case gets its floor, so the dependency's type
    /// record names the guard the bound module routes the enum's values through. The negative
    /// control is the read the raw-ABI branch made before (no interface facts), which names none.
    /// </summary>
    [Fact]
    public void DependencyEnumParsedWithItsAvailability_NamesItsCaseFloorGuard()
    {
        using var dir = new TempDir();
        var producer = new StubProducer(new PartialSwiftInterfaceFacts
        {
            AvailabilityAnnotations = new() { ["Mode.future"] = new() { Ios99 } },
        });
        var facts = BindingsGenerator.ReadDependencyAvailabilityFacts(
            Dependency(dir.Interface()),
            () => new InterfaceFactsAggregator(new IInterfaceFactsProducer[] { producer }),
            NullLogger.Instance);

        Assert.Equal($"global::{DepModule}.__ModeCaseFloors.Require", FinalizedModeRecord(dir, facts).CaseFloorGuard);
        Assert.Null(FinalizedModeRecord(dir, SwiftInterfaceFacts.Empty).CaseFloorGuard);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static FrameworkDependencyInfo Dependency(string? swiftInterfacePath) => new()
    {
        XCFrameworkPath = $"/tmp/{DepModule}.xcframework",
        ModuleName = DepModule,
        SwiftInterfacePath = swiftInterfacePath,
    };

    private static TypeRecord FinalizedModeRecord(TempDir dir, SwiftInterfaceFacts facts)
    {
        var abiPath = Path.Combine(dir.Path, $"{Guid.NewGuid():N}.abi.json");
        File.WriteAllText(abiPath, JsonConvert.SerializeObject(new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new[] { ModeEnum() },
            },
        }));
        var typeDatabase = new TypeDatabase();
        var parser = new SwiftABIParser(abiPath, typeDatabase, EmptyDemangling(), NullLogger.Instance, facts);
        var parsed = parser.ParseModule();
        var processor = new ModuleProcessor(
            DepModule, "/tmp/DepModule.dylib", "/tmp/DepModule.dylib", parsed.TypeDecls, typeDatabase, NullLogger.Instance);
        var moduleDatabase = processor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
        Assert.True(moduleDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName($"{DepModule}.Mode"), out var record));
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        return record;
    }

    /// <summary><c>public enum Mode { case basic, future }</c>, as swift-api-digester writes it.</summary>
    private static Node ModeEnum()
    {
        var mode = Node("TypeDecl", "Enum", "Mode", $"$s9{DepModule}4ModeON");
        mode.Children = new[] { Case("basic"), Case("future") };
        return mode;
    }

    private static Node Case(string name)
    {
        var caseNode = Node("Var", "EnumElement", name, $"$s9{DepModule}4ModeO{name.Length}{name}yA2CmF");
        var returned = Node("TypeNominal", "", "Mode", "$s");
        returned.PrintedName = $"{DepModule}.Mode";
        var metatype = Node("TypeNominal", "", "Mode.Type", "$s");
        metatype.PrintedName = $"{DepModule}.Mode.Type";
        var function = Node("TypeFunc", "", "", "$s");
        function.Children = new[] { returned, metatype };
        caseNode.Children = new[] { function };
        return caseNode;
    }

    private static Node Node(string kind, string declKind, string name, string mangledName) => new()
    {
        Kind = kind,
        DeclKind = declKind,
        Name = name,
        MangledName = mangledName,
        PrintedName = name,
        ModuleName = DepModule,
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

    private static DemanglingResults EmptyDemangling()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
    }

    private sealed class StubProducer : IInterfaceFactsProducer
    {
        private readonly PartialSwiftInterfaceFacts _facts;

        public StubProducer(PartialSwiftInterfaceFacts facts) => _facts = facts;

        public int Calls { get; private set; }

        public string Name => "stub";

        public ProducerResult Produce(string swiftInterfacePath, ILogger logger)
        {
            Calls++;
            var covered = new HashSet<InterfaceFactKind>();
            if (_facts.AvailabilityAnnotations != null) covered.Add(InterfaceFactKind.AvailabilityAnnotations);
            if (_facts.PublicTypeNames != null) covered.Add(InterfaceFactKind.PublicTypeNames);
            if (_facts.InternalMemberKeys != null) covered.Add(InterfaceFactKind.InternalMemberKeys);
            return new ProducerResult(_facts, covered);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dep_availability_{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public string Interface()
        {
            var path = System.IO.Path.Combine(Path, $"{DepModule}.swiftinterface");
            File.WriteAllText(path, "// swift-module-flags: -module-name DepModule\n");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
