// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;
using DemanglerNode = BindingsGeneration.Demangling.Node;

namespace BindingsGeneration.Tests;

/// <summary>
/// The second-level <c>Sc</c> standard substitutions name the concurrency standard types
/// (<c>ScA</c> = Actor, <c>ScT</c> = Task, …). Every expected value below was read off
/// <c>swift-demangle --tree-only '$sSc&lt;X&gt;Mn'</c> from both the Xcode 26.3 and Xcode 27.2
/// toolchains; the two agree on all nineteen entries, and every other letter after <c>Sc</c>
/// is rejected by both. Like upstream, the demangled module is <c>Swift</c>, not
/// <c>_Concurrency</c>.
/// </summary>
public class ConcurrencySubstitutionTests
{
    public static TheoryData<char, NodeKind, string> ConcurrencyTable => new()
    {
        { 'A', NodeKind.Protocol, "Actor" },
        { 'C', NodeKind.Structure, "CheckedContinuation" },
        { 'c', NodeKind.Structure, "UnsafeContinuation" },
        { 'E', NodeKind.Structure, "CancellationError" },
        { 'e', NodeKind.Structure, "UnownedSerialExecutor" },
        { 'F', NodeKind.Protocol, "Executor" },
        { 'f', NodeKind.Protocol, "SerialExecutor" },
        { 'G', NodeKind.Structure, "TaskGroup" },
        { 'g', NodeKind.Structure, "ThrowingTaskGroup" },
        { 'h', NodeKind.Protocol, "TaskExecutor" },
        { 'I', NodeKind.Protocol, "AsyncIteratorProtocol" },
        { 'i', NodeKind.Protocol, "AsyncSequence" },
        { 'J', NodeKind.Structure, "UnownedJob" },
        { 'M', NodeKind.Class, "MainActor" },
        { 'P', NodeKind.Structure, "TaskPriority" },
        { 'S', NodeKind.Structure, "AsyncStream" },
        { 's', NodeKind.Structure, "AsyncThrowingStream" },
        { 'T', NodeKind.Structure, "Task" },
        { 't', NodeKind.Structure, "UnsafeCurrentTask" },
    };

    private static DemanglerNode FindNominal(DemanglerNode node)
    {
        if (node is null)
            return null;
        if (node.Kind is NodeKind.Structure or NodeKind.Class or NodeKind.Protocol or NodeKind.Enum)
            return node;
        foreach (var child in node.Children)
        {
            var found = FindNominal(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    [Theory]
    [MemberData(nameof(ConcurrencyTable))]
    public void SecondLevelSubstitution_DemanglesToConcurrencyType(char letter, NodeKind kind, string name)
    {
        var demangler = new Swift5Demangler();
        var tree = demangler.DemangleSymbol($"$sSc{letter}Mn");

        var nominal = FindNominal(tree);
        Assert.NotNull(nominal);
        Assert.Equal(kind, nominal.Kind);
        Assert.Equal(2, nominal.Children.Count);
        Assert.Equal(NodeKind.Module, nominal.Children[0].Kind);
        Assert.Equal("Swift", nominal.Children[0].Text);
        Assert.Equal(NodeKind.Identifier, nominal.Children[1].Kind);
        Assert.Equal(name, nominal.Children[1].Text);
    }

    [Theory]
    [InlineData('B')]
    [InlineData('D')]
    [InlineData('a')]
    [InlineData('q')]
    [InlineData('z')]
    public void SecondLevelSubstitution_UnknownLetter_IsRejected(char letter)
    {
        // Upstream rejects every letter outside the concurrency table; a first-level fallback
        // ('Sca' → Array) would silently name the wrong type.
        var demangler = new Swift5Demangler();
        var tree = demangler.DemangleSymbol($"$sSc{letter}Mn");
        Assert.Null(FindNominal(tree));
    }

    [Fact]
    public void FirstLevelSubstitution_IsUnchangedByTheConcurrencyTable()
    {
        // 'Si' still names Swift.Int; the 'c' prefix only applies to second-level lookups.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s1M9takesPrioyyScPF");
        var func = Assert.IsType<FunctionReduction>(result);
        var param = Assert.Single(func.Function.ParameterList.Elements);
        Assert.Equal("Swift.TaskPriority", Assert.IsType<NamedTypeSpec>(param).Name);
    }

    [Fact]
    public void ActorConformanceDescriptor_NamesActorProtocol()
    {
        // protocol conformance descriptor for M.Analyzer : Swift.Actor in M — the shape every
        // `public actor` emits. Read as UnicodeScalar before the 'Sc' table existed.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s1M8AnalyzerCScAAAMc");
        var conf = Assert.IsType<ProtocolConformanceDescriptorReduction>(result);
        Assert.Equal("M.Analyzer", conf.ImplementingType.Name);
        Assert.Equal("Swift.Actor", conf.ProtocolType.Name);
        Assert.Equal("M", conf.Module);
    }

    [Theory]
    [InlineData("_$s1M3SeqVSciAAMc", "M.Seq", "Swift.AsyncSequence")]
    [InlineData("_$s1M3SeqV2ItVScIAAMc", "M.Seq.It", "Swift.AsyncIteratorProtocol")]
    [InlineData("_$s1M4ExecCScFAAMc", "M.Exec", "Swift.Executor")]
    [InlineData("_$s1M4ExecCScfAAMc", "M.Exec", "Swift.SerialExecutor")]
    public void ConcurrencyProtocolConformanceDescriptors(string symbol, string implementing, string protocol)
    {
        var demangler = new Swift5Demangler();
        var result = demangler.Run(symbol);
        var conf = Assert.IsType<ProtocolConformanceDescriptorReduction>(result);
        Assert.Equal(implementing, conf.ImplementingType.Name);
        Assert.Equal(protocol, conf.ProtocolType.Name);
    }

    [Theory]
    [InlineData("_$s1M9takesTaskyyScTySis5NeverOGF", "Swift.Task<Swift.Int, Swift.Never>")]
    [InlineData("_$s1M11takesStreamyyScSySiGF", "Swift.AsyncStream<Swift.Int>")]
    [InlineData("_$s1M9takesContyyScCySis5NeverOGF", "Swift.CheckedContinuation<Swift.Int, Swift.Never>")]
    public void ConcurrencyGenericParameterTypes(string symbol, string expectedParam)
    {
        var demangler = new Swift5Demangler();
        var result = demangler.Run(symbol);
        var func = Assert.IsType<FunctionReduction>(result);
        var param = Assert.Single(func.Function.ParameterList.Elements);
        Assert.Equal(expectedParam, param.ToString());
    }

    [Fact]
    public void ConcurrencyReturnType_UnownedSerialExecutor()
    {
        // M.Exec.asUnownedSerialExecutor() -> Swift.UnownedSerialExecutor
        var demangler = new Swift5Demangler();
        var result = demangler.Run("_$s1M4ExecC23asUnownedSerialExecutorSceyF");
        var func = Assert.IsType<FunctionReduction>(result);
        Assert.Equal("Swift.UnownedSerialExecutor", func.Function.Return.ToString());
    }
}
