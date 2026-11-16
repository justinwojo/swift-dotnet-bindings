// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// A member that returns or takes an existential of a protocol another module declares. Its size
/// depends on what that protocol is: a class-bound Swift protocol's existential is a reference and
/// one witness table (two words, and its Optional the same two words), an <c>@objc</c> or class-bound
/// <c>@_marker</c> protocol's is one object pointer, and any other protocol's is an address-only box. A consumer of a
/// binding-backed dependency knows the dependency only through the module database its binding
/// published, so the prediction has to come out the same from those records as from the
/// declarations, and match swiftc either way.
/// </summary>
public class CrossModuleExistentialLoweringTests
{
    private const string DependencySource = """
        import Foundation
        public protocol ExistentialBound: AnyObject { func value() -> Int }
        @objc public protocol ExistentialObjC { func value() -> Int }
        public protocol ExistentialRefined: ExistentialBound { func extra() -> Int }
        public protocol ExistentialRefinesObjC: ExistentialObjC { func extra() -> Int }
        public protocol ExistentialOpaque { func value() -> Int }
        @_marker public protocol ExistentialMarker: AnyObject {}
        """;

    private const string MainSource = """
        import Foundation
        import ExistentialDependency
        public func vendBound(_ seed: Int) -> (any ExistentialBound)? { nil }
        public func vendObjC(_ seed: Int) -> (any ExistentialObjC)? { nil }
        public func vendRefined(_ seed: Int) -> (any ExistentialRefined)? { nil }
        public func vendRefinesObjC(_ seed: Int) -> (any ExistentialRefinesObjC)? { nil }
        public func vendOpaque(_ seed: Int) -> (any ExistentialOpaque)? { nil }
        public func vendMarker(_ seed: Int) -> (any ExistentialMarker)? { nil }
        public func takeMarker(_ value: any ExistentialMarker) -> Int { 1 }
        public func takeBound(_ value: (any ExistentialBound)?) -> Int { value == nil ? 0 : 1 }
        public func takeObjC(_ value: (any ExistentialObjC)?) -> Int { value == nil ? 0 : 1 }
        public final class ExistentialVendor {
            public init() {}
            public func bound() -> (any ExistentialBound)? { nil }
            public func objc() -> (any ExistentialObjC)? { nil }
            public func plainBound() -> any ExistentialBound { fatalError() }
            public func plainObjC() -> any ExistentialObjC { fatalError() }
        }
        """;

    // Every member above, by the name its demangling ends in; each must be predicted and match.
    private static readonly string[] Members =
    {
        "vendBound", "vendObjC", "vendRefined", "vendRefinesObjC", "vendOpaque", "vendMarker", "takeBound", "takeObjC", "takeMarker",
        "ExistentialVendor.bound", "ExistentialVendor.objc", "ExistentialVendor.plainBound", "ExistentialVendor.plainObjC",
    };

    private readonly ITestOutputHelper _output;

    public CrossModuleExistentialLoweringTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Shapes =>
        from shape in new[] { "declarations", "published database" }
        from triple in new[] { SwiftOracleTarget.SimulatorArm64.Triple, SwiftOracleTarget.SimulatorX64.Triple }
        select new object[] { shape, triple };

    [SkippableTheory]
    [MemberData(nameof(Shapes))]
    public void ExistentialOfAnotherModulesProtocol_IsPredictedAsSwiftcLowersIt(string dependencyShape, string triple)
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var directory = Path.Combine(Path.GetTempPath(), "cross-module-existential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = SwiftOracleTarget.ByTriple(triple);
            var dependencyDirectory = Path.Combine(directory, "dependency");
            var mainDirectory = Path.Combine(directory, "main");
            var dependencyPath = Path.Combine(directory, "ExistentialDependency.swift");
            var mainPath = Path.Combine(directory, "ExistentialMain.swift");
            File.WriteAllText(dependencyPath, DependencySource);
            File.WriteAllText(mainPath, MainSource);
            var dependency = SwiftLoweringOracle.Compile("ExistentialDependency", new[] { dependencyPath }, target, dependencyDirectory);
            var main = SwiftLoweringOracle.Compile("ExistentialMain", new[] { mainPath }, target, mainDirectory,
                new[] { "-I", dependencyDirectory });

            var report = dependencyShape == "declarations"
                ? SwiftLoweringOracle.Compare(main, target.Triple, new[] { dependency })
                : SwiftLoweringOracle.CompareWithPublishedDependencies(main, target.Triple, new[] { dependency }, directory);
            _output.WriteLine(report.Describe());

            var failures = new List<string>();
            foreach (var member in Members)
            {
                var entry = report.Entries.SingleOrDefault(e =>
                    e.Facts.Target.Symbol is { } symbol && SwiftLoweringOracle.Demangle(symbol).Contains("." + member + "(", StringComparison.Ordinal));
                if (entry is null)
                    failures.Add($"{member}: no callee facts");
                else if (entry.Outcome != SwiftOracleOutcome.Match)
                    failures.Add($"{member}: {entry.Outcome} predicted '{entry.Predicted}' swiftc '{entry.Actual?.Signature}' {entry.Refusal}");
            }
            Assert.True(failures.Count == 0, $"{dependencyShape} ({triple}):\n" + string.Join("\n", failures));
            Assert.Empty(report.With(SwiftOracleOutcome.Mismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
