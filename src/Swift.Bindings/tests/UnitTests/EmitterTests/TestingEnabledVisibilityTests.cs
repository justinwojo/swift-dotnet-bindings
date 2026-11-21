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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A framework built with testing enabled (<c>-enable-testing</c>) lists and exports its internal
/// declarations; its public interface leaves them out. A member that is not public in the module's
/// public Swift interface is not projected as public API, whatever its shape, while the public
/// members beside it still are. The fixture is compiled by swiftc and read the way the generator
/// reads a binary framework: the ABI descriptor through the parser, the interface through the
/// SwiftSyntax facts producer.
/// </summary>
public class TestingEnabledVisibilityTests
{
    private const string Library = """
        public final class Ledger {
            public init() {}
            public func total() -> Int32 { 1 }
            public static func make() -> Ledger { Ledger() }
            init(seed: Int32) {}
            func audit(_ value: Int32) -> Int32 { value }
            static func reset(_ value: Int32) -> Int32 { value }
            func apply(_ transform: (Int32) -> Int32) -> Int32 { transform(1) }
        }
        public struct Entry {
            public init() {}
            public func amount() -> Int32 { 2 }
            func checksum() -> Int32 { 3 }
        }
        public func publicSum(_ a: Int32, _ b: Int32) -> Int32 { a + b }
        func internalSum(_ a: Int32, _ b: Int32) -> Int32 { a + b }
        """;

    private sealed record Fixture(ModuleDecl Module, TypeDatabase Database);

    private static readonly Lazy<Fixture> _fixture = new(Build);

    private static Fixture Build()
    {
        var root = Path.Combine(Path.GetTempPath(), $"testing_visibility_{Guid.NewGuid():N}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        };
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "LedgerKit.swift");
        File.WriteAllText(source, Library);
        var module = SwiftLoweringOracle.Compile("LedgerKit", [source], SwiftOracleTarget.SimulatorArm64, root, ["-enable-testing"]);
        var interfacePath = Path.Combine(SwiftLoweringOracle.InterfaceDirectory(root), "LedgerKit.swiftinterface");
        var facts = InterfaceFactsAggregator.CreateDefault(NullLogger.Instance).Aggregate(interfacePath, NullLogger.Instance);
        var database = new TypeDatabase();
        var parser = new SwiftABIParser(module.AbiJsonPath, database, EmptyDemangling(), NullLogger.Instance, facts);
        return new Fixture(parser.ParseModule().ModuleDecl, database);
    }

    private static DemanglingResults EmptyDemangling()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)], modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
    }

    private static void SkipUnlessToolchain()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var parser = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        Skip.IfNot(parser is not null && File.Exists(parser),
            "SwiftInterfaceParser binary not found. Run `nuke compile` (or set SWIFT_INTERFACE_PARSER_PATH).");
    }

    private static MethodDecl Member(string type, string name, int? arity = null)
    {
        var owner = _fixture.Value.Module.Types.Single(t => t.Name == type);
        var matches = owner.Methods
            .Where(m => m.Name == name && (arity is null || m.CSSignature.Count - 1 == arity))
            .ToList();
        return matches is [var single]
            ? single
            : throw new InvalidOperationException($"{type} declares {matches.Count} methods named {name}");
    }

    private static MethodDecl Global(string name) =>
        _fixture.Value.Module.Methods.Single(m => m.Name == name);

    private static ValidationResult Validate(MethodDecl method) =>
        new MemberValidationPipeline(_fixture.Value.Database).ValidateMethodEmission(method, null);

    public static TheoryData<string, string, int> InternalMembers => new()
    {
        { "Ledger", "audit", 1 },
        { "Ledger", "reset", 1 },
        { "Ledger", "apply", 1 },
        { "Ledger", "init", 1 },
        { "Entry", "checksum", 0 },
    };

    /// <summary>
    /// Every internal member the descriptor lists — instance, static, closure-bearing, initializer,
    /// on a class and a struct — is withdrawn with a ModuleInternal row that says why.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(InternalMembers))]
    public void InternalMember_OfATestingEnabledFramework_IsNotProjected(string type, string name, int arity)
    {
        SkipUnlessToolchain();

        var method = Member(type, name, arity);
        var result = Validate(method);

        Assert.True(method.IsModuleInternal, $"{type}.{name} should read as internal: the interface leaves it out");
        Assert.False(result.ShouldEmit, $"{type}.{name} is not public in the interface but would be projected");
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
        Assert.Contains("Not public in the module's public Swift interface", result.Details!);
    }

    [SkippableFact]
    public void InternalModuleFunction_OfATestingEnabledFramework_IsNotProjected()
    {
        SkipUnlessToolchain();

        var result = Validate(Global("internalSum"));

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
        Assert.Contains("module-level function", result.Details!);
    }

    public static TheoryData<string, string, int> PublicMembers => new()
    {
        { "Ledger", "total", 0 },
        { "Ledger", "make", 0 },
        { "Ledger", "init", 0 },
        { "Entry", "amount", 0 },
    };

    /// <summary>The public members beside them are still projected: the gate keys on the interface, not the build.</summary>
    [SkippableTheory]
    [MemberData(nameof(PublicMembers))]
    public void PublicMember_OfATestingEnabledFramework_IsStillProjected(string type, string name, int arity)
    {
        SkipUnlessToolchain();

        var method = Member(type, name, arity);
        var result = Validate(method);

        Assert.False(method.IsModuleInternal, $"{type}.{name} is public in the interface");
        Assert.True(result.ShouldEmit, $"{type}.{name} should be projected: {result.Reason} {result.Details}");
    }

    [SkippableFact]
    public void PublicModuleFunction_OfATestingEnabledFramework_IsStillProjected()
    {
        SkipUnlessToolchain();

        var result = Validate(Global("publicSum"));

        Assert.True(result.ShouldEmit, $"publicSum should be projected: {result.Reason} {result.Details}");
    }
}
