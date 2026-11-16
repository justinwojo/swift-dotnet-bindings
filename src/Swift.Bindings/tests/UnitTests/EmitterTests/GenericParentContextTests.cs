// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// A member of a generic type that the binding calls directly with the Swift convention takes only
/// the generic arguments Swift's polymorphic convention cannot recover from what it already has. A
/// class instance carries its class's generic arguments and declared witness tables in its isa; an
/// instance member of a generic struct or enum takes the parent's own metadata in the first metadata
/// slot, which carries the same. Only the method's own generic arguments, and a conformance a
/// constrained extension adds, are passed. These tests compile first-party shapes with swiftc, run
/// the generator's type handlers over the ABI descriptor, and compare every direct P/Invoke's
/// argument list with the function swiftc defined.
/// </summary>
public class GenericParentContextTests
{
    private const string Source = """
        public protocol ContextMarker { func mark() -> Int }
        public protocol ContextOther { func other() -> Int }

        public struct ContextHolder<T: ContextMarker> {
            public var seed: Int
            public init(seed: Int) { self.seed = seed }
            public func store<V>(_ v: V) -> Bool { seed > 0 }
            public func storeMarked<V: ContextMarker>(_ v: V) -> Bool { seed > 0 }
        }

        extension ContextHolder where T: ContextOther {
            public func storeOther<V>(_ v: V) -> Bool { seed > 0 }
        }

        public struct ContextPair<A: ContextMarker, B> {
            public var seed: Int
            public init(seed: Int) { self.seed = seed }
            public func store<V>(_ v: V) -> Bool { seed > 0 }
        }

        public enum ContextChoice<T: ContextMarker> {
            case some(Int)
            case none
            public func store<V>(_ v: V) -> Bool { if case .some(let s) = self { return s > 0 }; return false }
        }

        public final class ContextBox<T: ContextMarker> {
            public var seed: Int
            public init(seed: Int) { self.seed = seed }
            public func store<V>(_ v: V) -> Bool { seed > 0 }
        }

        open class ContextOpen<T: ContextMarker> {
            public var seed: Int = 0
            public init() {}
            open func store<V: ContextMarker>(_ v: V) -> Bool { seed > 0 }
        }
        """;

    // Every shape must reach a direct P/Invoke, or the comparison below checks nothing for it.
    private static readonly string[] RequiredDirectMembers =
    {
        "ContextHolder.store", "ContextHolder.storeMarked", "ContextPair.store", "ContextChoice.store",
        "ContextBox.store", "ContextOpen.store",
    };

    private static readonly Regex Import = new(
        @"EntryPoint = ""(?<symbol>\$s[^""]+)""\)\]\s*(?:\[[^\n]*\]\s*)*internal static (?:unsafe )?partial [^(]+\((?<parameters>[^)]*)\);",
        RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public GenericParentContextTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void EveryDirectMemberOfAGenericType_PassesTheArgumentsSwiftcTakes()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var directory = Path.Combine(Path.GetTempPath(), "generic-parent-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "GenericParentContext.swift");
            File.WriteAllText(sourcePath, Source);
            var module = SwiftLoweringOracle.Compile(
                "GenericParentContext", new[] { sourcePath }, SwiftOracleTarget.SimulatorArm64, directory);
            var db = SwiftLoweringOracle.LoadTypeDatabase();
            var decl = SwiftLoweringOracle.ParseAndRegisterModule(module, db);

            var compared = new List<string>();
            var mismatches = new List<string>();
            foreach (var type in decl.Types)
            {
                var output = Emit(type, db);
                foreach (Match import in Import.Matches(output))
                {
                    var symbol = import.Groups["symbol"].Value;
                    if (!module.Ir.Functions.TryGetValue(symbol, out var function))
                        continue;
                    var demangled = SwiftLoweringOracle.Demangle(symbol);
                    var parameters = SplitParameters(import.Groups["parameters"].Value);
                    var swiftc = IrParameters(function.Signature);
                    var line = $"{type.Name} {demangled}\n    P/Invoke ({string.Join(", ", parameters)})\n    swiftc   {function.Signature}";
                    _output.WriteLine(line);
                    compared.Add(demangled);
                    // A value type's initializer takes a thin metatype self, which occupies no
                    // register; the declaration passes it as a trailing `metatype` the callee never
                    // reads (SwiftCallPlan.ThinMetatypeReason). Compare the arguments before it.
                    if (parameters.Count == swiftc.Count + 1
                        && parameters[^1] == "IntPtr metatype"
                        && swiftc.LastOrDefault()?.Contains("swiftself", StringComparison.Ordinal) != true)
                        parameters.RemoveAt(parameters.Count - 1);
                    // One P/Invoke argument per IR parameter, the Swift self last on both.
                    if (parameters.Count != swiftc.Count
                        || parameters.LastOrDefault()?.Contains("SwiftSelf", StringComparison.Ordinal) != swiftc.LastOrDefault()?.Contains("swiftself", StringComparison.Ordinal))
                        mismatches.Add(line);
                }
            }

            foreach (var member in RequiredDirectMembers)
                Assert.True(
                    compared.Any(d => d.Contains("." + member + "<", StringComparison.Ordinal)),
                    $"{member} was not bound by a direct P/Invoke; compared:\n{string.Join("\n", compared)}");
            Assert.True(mismatches.Count == 0, "P/Invoke arguments differ from swiftc's:\n" + string.Join("\n", mismatches));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Emit(TypeDecl type, TypeDatabase db)
    {
        var conductor = new Conductor(new NullLoggerFactory());
        if (!conductor.TryGetTypeHandler(type, out var handler))
            return string.Empty;
        var csOutput = new StringWriter();
        var env = handler.Marshal(type, db);
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(new StringWriter()), env, conductor, TypeHandlerContext.Empty);
        return csOutput.ToString();
    }

    private static List<string> SplitParameters(string parameters) =>
        parameters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Regex.Replace(p, @"global::[\w.]+\.", ""))
            .ToList();

    // "i1 (ptr noalias, ptr, ptr, ptr noalias swiftself)" -> its parameters.
    private static List<string> IrParameters(string signature)
    {
        var open = signature.IndexOf('(');
        var inner = signature[(open + 1)..signature.LastIndexOf(')')];
        return inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
