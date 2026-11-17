// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The special-parameter order of a reverse Swift-convention callback: <c>SwiftError*</c> first,
/// <c>SwiftSelf</c> (the closure context) last. The .NET 11 CoreCLR interpreter stores the incoming
/// error register into the FIRST managed argument slot of a reverse Swift call and reads the context
/// from the LAST, so a throwing callback declared <c>(args…, SwiftError*, SwiftSelf)</c> had its first
/// argument overwritten with zero before its body ran. These tests pin the rule itself, every
/// emitter that renders a Swift-lane callback and its pointer type, and the C-convention lane, whose
/// order belongs to the Swift adapter and must not move.
/// </summary>
public class ReverseSwiftCallbackOrderTests
{
    [Fact]
    public void Rule_Throwing_PlacesErrorFirstAndContextLast()
    {
        var parameters = ReverseSwiftCallbackSignature.Parameters(new[] { "void* arg0", "nint arg1" }, throws: true);

        Assert.Equal(new[] { "SwiftError* errorOut", "void* arg0", "nint arg1", "SwiftSelf context" }, parameters);
    }

    [Fact]
    public void Rule_NonThrowing_PlacesOnlyTheContext_Last()
    {
        var parameters = ReverseSwiftCallbackSignature.Parameters(new[] { "void* arg0", "nint arg1" }, throws: false);

        Assert.Equal(new[] { "void* arg0", "nint arg1", "SwiftSelf context" }, parameters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rule_IndirectResult_IsTheSwiftIndirectResultRegister_AfterTheErrorAndBeforeTheArguments(bool throws)
    {
        // Swift passes a closure's result buffer in x8 / rax and the first argument in x0 / rdi; a
        // buffer declared as an ordinary leading pointer reads the first argument register instead.
        var parameters = ReverseSwiftCallbackSignature.Parameters(new[] { "nint arg0" }, throws, indirectResult: true);
        var pointer = ReverseSwiftCallbackSignature.FunctionPointerType(new[] { "nint" }, throws, "void", indirectResult: true);

        var expected = new List<string> { "SwiftIndirectResult", "nint", "SwiftSelf" };
        if (throws)
            expected.Insert(0, "SwiftError*");
        Assert.Equal(expected, ParameterTypes(parameters));
        Assert.Equal(expected.Append("void"), PointerTypeArguments(pointer, "Swift").Select(t => t.Replace(SwiftInteropNamespace, "", System.StringComparison.Ordinal)));
        Assert.DoesNotContain(parameters, p => p.StartsWith("void*", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SwiftLane_IndirectReturn_ReadsTheBufferFromTheIndirectResultRegister()
    {
        var (callback, _) = Emit("indirect", useCdecl: false);

        var types = ParameterTypes(UcoParameters(callback, out _)).ToList();
        Assert.Equal(new[] { "SwiftIndirectResult", "nint", "SwiftSelf" }, types);
        Assert.Contains(ReverseSwiftCallbackSignature.IndirectResultBuffer, callback);
    }

    [Fact]
    public void CdeclLane_IndirectReturn_KeepsTheAdapterBufferArgument()
    {
        // The @_cdecl adapter passes the buffer as its first C argument; that lane must not move.
        var (callback, _) = Emit("indirect", useCdecl: true);

        var parameters = UcoParameters(callback, out _);
        Assert.Equal("void* indirectResult", parameters[0].Trim());
        Assert.DoesNotContain("SwiftIndirectResult", callback);
    }

    [Fact]
    public void Rule_NoOrdinaryParameters_StillOrdersTheSpecialOnes()
    {
        Assert.Equal(new[] { "SwiftError* errorOut", "SwiftSelf context" },
            ReverseSwiftCallbackSignature.Parameters(new string[0], throws: true));
        Assert.Equal("delegate* unmanaged[Swift]<SwiftError*, SwiftSelf, void>",
            ReverseSwiftCallbackSignature.FunctionPointerType(new string[0], throws: true, "void"));
    }

    [Fact]
    public void Rule_PointerType_MirrorsTheParameterList()
    {
        var ordinary = new[] { ("void*", "arg0"), ("double", "arg1") };
        var parameters = ReverseSwiftCallbackSignature.Parameters(ordinary.Select(o => $"{o.Item1} {o.Item2}"), throws: true);
        var pointer = ReverseSwiftCallbackSignature.FunctionPointerType(ordinary.Select(o => o.Item1), throws: true, "byte");

        Assert.Equal(ParameterTypes(parameters).Append("byte"), PointerTypeArguments(pointer, "Swift"));
    }

    // ---- Every Swift-lane emitter ----

    public static IEnumerable<object[]> SwiftLaneEmitters() => new[]
    {
        new object[] { "throwing" },
        new object[] { "escaping" },
        new object[] { "indirect" },
    };

    [Theory]
    [MemberData(nameof(SwiftLaneEmitters))]
    public void SwiftLane_CallbackAndItsPointerType_FollowTheRule(string shape)
    {
        var (callback, pointerField) = Emit(shape, useCdecl: false);

        var types = ParameterTypes(UcoParameters(callback, out var convention)).ToList();
        Assert.Equal("CallConvSwift", convention);
        Assert.Equal("SwiftSelf", types[^1]);
        Assert.Equal(1, types.Count(t => t == "SwiftSelf"));
        if (shape == "throwing")
            Assert.Equal("SwiftError*", types[0]);
        Assert.True(types.Count(t => t == "SwiftError*") == (shape == "throwing" ? 1 : 0));
        Assert.True(types.Count(t => t == "SwiftIndirectResult") == (shape == "indirect" ? 1 : 0));

        // The static pointer field that hands the callback to Swift names the same signature.
        var pointerArgs = PointerTypeArguments(pointerField, "Swift")
            .Select(t => t.Replace(SwiftInteropNamespace, "", System.StringComparison.Ordinal)).ToList();
        Assert.Equal(types, pointerArgs.Take(pointerArgs.Count - 1));
    }

    [Fact]
    public void SwiftLane_Throwing_KeepsOrdinaryArgumentsInSwiftOrderBetweenTheSpecialOnes()
    {
        var (callback, _) = Emit("throwing", useCdecl: false);

        var names = UcoParameters(callback, out _).Select(p => p.Split(' ')[^1]).ToList();
        Assert.Equal(new[] { "errorOut", "arg0", "arg1", "context" }, names);
    }

    [Fact]
    public void CdeclLane_Throwing_KeepsTheAdapterOrder()
    {
        // The @_cdecl adapter calls (args…, errorOut, ctx); the rule must not reach this lane.
        var (callback, pointerField) = Emit("throwing", useCdecl: true);

        var parameters = UcoParameters(callback, out var convention);
        Assert.Equal("CallConvCdecl", convention);
        Assert.Equal(new[] { "nint", "nint", "SwiftError*", "IntPtr" }, ParameterTypes(parameters));
        Assert.Equal(new[] { "nint", "nint", "SwiftError*", "IntPtr", "nint" }, PointerTypeArguments(pointerField, "Cdecl"));
    }

    [Theory]
    [InlineData("escaping")]
    [InlineData("indirect")]
    public void CdeclLane_NonThrowing_ContextStaysATrailingIntPtr(string shape)
    {
        var (callback, pointerField) = Emit(shape, useCdecl: true);

        var types = ParameterTypes(UcoParameters(callback, out var convention)).ToList();
        Assert.Equal("CallConvCdecl", convention);
        Assert.Equal("IntPtr", types[^1]);
        Assert.DoesNotContain("SwiftSelf", types);
        var pointerArgs = PointerTypeArguments(pointerField, "Cdecl");
        Assert.Equal(types, pointerArgs.Take(pointerArgs.Count - 1));
    }

    // ---- Corpus invariant over the generated BindingTests output ----

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void EverySwiftConventionCallbackInTheCorpus_PlacesErrorFirstAndContextLast()
    {
        var outputDir = Path.Combine(LocateRepoRoot(), "BindingTests", "output");
        var corpus = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0,
            $"Generated bindings corpus not found under {outputDir}");

        int declared = 0, total = 0, otherCallbacks = 0;
        var violations = new List<string>();
        foreach (var file in corpus.OrderBy(f => f, System.StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            foreach (var line in text.Split('\n').Where(l => l.Contains("UnmanagedCallersOnly(", System.StringComparison.Ordinal)))
            {
                if (line.Contains("CallConvSwift", System.StringComparison.Ordinal))
                    declared++;
                else
                    otherCallbacks++;
            }
            foreach (Match match in SwiftUcoPattern.Matches(text))
            {
                total++;
                var types = ParameterTypes(SplitTopLevel(match.Groups["params"].Value)).ToList();
                var problem = CorpusOrderProblem(types)
                    ?? (match.Groups["params"].Value.Contains("void* indirectResult", System.StringComparison.Ordinal)
                        ? "the indirect result must arrive as a SwiftIndirectResult, not an ordinary pointer"
                        : null);
                if (problem is not null)
                    violations.Add($"{Path.GetFileName(file)} {match.Groups["name"].Value}({string.Join(", ", types)}): {problem}");
            }
        }

        // The generator moves every closure it can onto the @_cdecl lane, which every engine calls;
        // the corpus keeps Swift-convention callbacks only for shapes with no @_cdecl adapter
        // (ReverseCallbackShapes.swift). What must hold is that the scan read real callbacks, and
        // that every Swift-convention one it finds was parsed and checked. Whether the
        // signature pattern still matches what the Swift-lane emitters render is pinned on their own
        // output (CorpusPattern_MatchesEverySwiftLaneEmitter), so a return of the lane cannot slip
        // past this check unparsed.
        Assert.True(declared + otherCallbacks > 0, "The corpus declares no UnmanagedCallersOnly callback at all; the scan read nothing.");
        Assert.True(total == declared,
            $"Parsed {total} of the {declared} Swift-convention callbacks declared in the corpus; the signature pattern drifted.");
        Assert.True(violations.Count == 0,
            $"Swift-convention callbacks out of the reverse order ({violations.Count}):\n  " + string.Join("\n  ", violations));
    }

    [Theory]
    [MemberData(nameof(SwiftLaneEmitters))]
    public void CorpusPattern_MatchesEverySwiftLaneEmitter(string shape)
    {
        var (callback, _) = Emit(shape, useCdecl: false);

        var match = Assert.Single(SwiftUcoPattern.Matches(callback));
        var types = ParameterTypes(SplitTopLevel(match.Groups["params"].Value)).ToList();
        Assert.Null(CorpusOrderProblem(types));
        Assert.Equal(shape == "throwing", types.Contains("SwiftError*"));
        // A callback out of order must be one the corpus check reports.
        Assert.NotNull(CorpusOrderProblem(types.Skip(1).Append(types[0]).ToList()));
    }

    [Fact]
    public void CorpusPattern_IgnoresTheCdeclLane()
    {
        var (callback, _) = Emit("throwing", useCdecl: true);
        Assert.Empty(SwiftUcoPattern.Matches(callback));
    }

    private static string? CorpusOrderProblem(List<string> types)
    {
        if (types.Count(t => t == "SwiftSelf") != 1 || types[^1] != "SwiftSelf")
            return "SwiftSelf must be the last parameter, exactly once";
        int errors = types.Count(t => t == "SwiftError*");
        if (errors > 1 || (errors == 1 && types[0] != "SwiftError*"))
            return "SwiftError* must be the first parameter";
        int results = types.Count(t => t == "SwiftIndirectResult");
        if (results > 1 || (results == 1 && types[errors] != "SwiftIndirectResult"))
            return "SwiftIndirectResult must follow the error, before every ordinary parameter";
        return null;
    }

    // A Swift-convention UCO attribute, any further attribute lines, then the method's signature.
    private static readonly Regex SwiftUcoPattern = new(
        @"UnmanagedCallersOnly\(CallConvs = new\[\] \{ typeof\(global::System\.Runtime\.CompilerServices\.CallConvSwift\) \}\)\]\s*" +
        @"(?:\[[^\]]*\]\s*)*" +
        @"private static unsafe [^\(]+?\s(?<name>\w+)\((?<params>[^\)]*)\)",
        RegexOptions.Compiled);

    // ---- helpers ----

    private static (string Callback, string PointerField) Emit(string shape, bool useCdecl)
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var intType = new NamedTypeSpec("Swift.Int");
        var callbackWriter = new StringWriter();
        var pointerWriter = new StringWriter();
        var callback = new CSharpWriter(callbackWriter);
        var pointer = new CSharpWriter(pointerWriter);
        switch (shape)
        {
            case "throwing":
            {
                // (Int, Int) throws -> Int
                var closure = new ClosureTypeSpec(new TupleTypeSpec(new TypeSpec[] { intType, intType }), intType) { Throws = true };
                ClosureEmitter.EmitThrowingClosureCallbackPointer(pointer, "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
                ClosureEmitter.EmitThrowingClosureCallback(callback, "run", "body", closure, handler, "$s10TestModule3runyyF", "TestModule", useCdecl);
                break;
            }
            case "escaping":
            {
                // (Int, Int) -> Int
                var closure = new ClosureTypeSpec(new TupleTypeSpec(new TypeSpec[] { intType, intType }), intType);
                ClosureEmitter.EmitClosureCallbackPointer(pointer, "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
                ClosureEmitter.EmitEscapingClosureCallback(callback, "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
                break;
            }
            case "indirect":
            {
                // (Int) -> Loader, a class return the callback writes through a result buffer.
                var closure = new ClosureTypeSpec(intType, new NamedTypeSpec("TestModule.Loader"));
                ClosureEmitter.EmitIndirectReturnCallbackPointer(pointer, "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
                ClosureEmitter.EmitIndirectReturnCallback(callback, "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
                break;
            }
            default:
                throw new System.ArgumentException(shape);
        }
        return (callbackWriter.ToString(), pointerWriter.ToString());
    }

    /// <summary>The parameter declarations of the one UCO in <paramref name="emitted"/>, and its convention.</summary>
    private static List<string> UcoParameters(string emitted, out string convention)
    {
        var match = Regex.Match(emitted,
            @"CallConvs = new\[\] \{ typeof\(global::System\.Runtime\.CompilerServices\.(?<conv>\w+)\) \}\)\]\s*private static unsafe [^\(]+?\s\w+\((?<params>[^\)]*)\)");
        Assert.True(match.Success, "no [UnmanagedCallersOnly] method was emitted:\n" + emitted);
        convention = match.Groups["conv"].Value;
        return SplitTopLevel(match.Groups["params"].Value);
    }

    /// <summary>The type arguments (parameters, then result) of the <c>unmanaged[<paramref name="convention"/>]</c> pointer type in <paramref name="text"/>.</summary>
    private static List<string> PointerTypeArguments(string text, string convention)
    {
        var marker = $"delegate* unmanaged[{convention}]<";
        int start = text.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {marker}…> type in:\n{text}");
        start += marker.Length;
        int depth = 1, i = start;
        for (; i < text.Length && depth > 0; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
        }
        return SplitTopLevel(text.Substring(start, i - 1 - start));
    }

    // The finished binding spells the special types fully qualified; compare them by simple name.
    private const string SwiftInteropNamespace = "global::System.Runtime.InteropServices.Swift.";

    private static IEnumerable<string> ParameterTypes(IEnumerable<string> declarations) =>
        declarations.Select(d =>
        {
            var trimmed = d.Trim();
            int space = trimmed.LastIndexOf(' ');
            var type = space < 0 ? trimmed : trimmed.Substring(0, space).Trim();
            return type.Replace(SwiftInteropNamespace, "", System.StringComparison.Ordinal);
        });

    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(list))
            return parts;
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(list.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(list.Substring(start).Trim());
        return parts;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }
}
