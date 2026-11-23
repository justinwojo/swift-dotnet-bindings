// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// How the binding report accounts for a member the pre-gate trailing-default rescue recovers. The
/// emission gate refuses the full member, so it keeps its skip row, but a trimmed form of it emits and
/// the consumer can call it, so the row is recovered rather than lost. A same-named sibling the rescue
/// declines shares the row's (type, name) key and must stay a loss: the recovery is keyed by the
/// refused declaration's own identity.
/// </summary>
public sealed class TrailingDefaultRescueReportingTests : IDisposable
{
    private const string ModuleName = "Rescue";
    private const string FavoredSymbol = "$s6Rescue4HostV3tip5edgesACSS_7SwiftUI4EdgeOtcfC";
    private const string TwinSymbol = "$s6Rescue4HostV3tip5edgesAC7SwiftUI4EdgeO_AGtcfC";
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RescuedInitializer_IsRecovered_AndItsRefusedTwinStaysLost(bool favoredDeclaredFirst)
    {
        // The gate refuses the favored init(tip: String, edges: SwiftUI.Edge = …) for its trailing
        // defaulted SwiftUI parameter, and the rescue emits it as init(tip:). The disfavored twin's
        // SwiftUI type is not trailing, so no trim clears the gate and it is really lost. Both rows
        // read (Rescue.Host, init).
        var favored = Init(new NamedTypeSpec("Swift.String"), FavoredSymbol, disfavored: false);
        var twin = Init(new NamedTypeSpec("SwiftUI.Edge"), TwinSymbol, disfavored: true);

        var (report, csharp) = Emit(favoredDeclaredFirst ? new[] { favored, twin } : new[] { twin, favored });

        // The rescue did emit: the trimmed constructor calls the favored declaration's wrapper.
        Assert.Matches(@"public Host\(\s*string tip\)", csharp);
        Assert.DoesNotContain("DBW_", csharp);

        var favoredRow = Assert.Single(report.SkippedItems, i => i.DeclId == DeclIdFactory.ForMethod(favored).Canonical);
        var twinRow = Assert.Single(report.SkippedItems, i => i.DeclId == DeclIdFactory.ForMethod(twin).Canonical);
        Assert.Equal(favoredRow.ContainingType, twinRow.ContainingType);
        Assert.Equal(favoredRow.Name, twinRow.Name);

        Assert.Equal(new[] { "Rescue.Host.init(tip:)" }, favoredRow.RecoveredBy);
        Assert.Equal(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(favoredRow));
        Assert.False(SkipAttributionLinker.IsLoss(favoredRow));

        Assert.Null(twinRow.RecoveredBy);
        Assert.NotEqual(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(twinRow));
        Assert.True(SkipAttributionLinker.IsLoss(twinRow));
    }

    [Fact]
    public void RescuedInitializer_NamesItsTrimmedFormWithSwiftLabels()
    {
        // The parser names an unlabeled parameter `arg0`; the recovering form is spelled as the Swift
        // call a reader would write, `init(_:)`.
        var favored = Init(new NamedTypeSpec("Swift.String"), FavoredSymbol, disfavored: false, tipLabel: "arg0");

        var (report, _) = Emit(new[] { favored });

        var row = Assert.Single(report.SkippedItems, i => i.DeclId == DeclIdFactory.ForMethod(favored).Canonical);
        Assert.Equal(new[] { "Rescue.Host.init(_:)" }, row.RecoveredBy);
    }

    [Fact]
    public void RecoveringForm_IsSpelledWithTheSwiftNameTheParserEscapedForCSharp()
    {
        // A Swift method named with a C# keyword reaches the model as `_params`; the wrapper calls
        // `params(value:)`, and the recovery names that call.
        var method = TestDecls.Method("_params", parameters: new[] { TestDecls.Param("value", new NamedTypeSpec("Swift.Int")) },
            module: ModuleName) with { OriginalSwiftName = "params" };
        method.ParentDecl = new StructDecl
        {
            Name = "Host",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ModuleName}.Host"),
            MangledName = "$s6Rescue4HostVN",
            MetadataAccessor = "$s6Rescue4HostVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

        Assert.Equal("Rescue.Host.params(value:)", BaseHandler.GateReducedRecoveryProjection(method));
    }

    [Fact]
    public void RefusedInitializerTheRescueCannotTrim_StaysLost()
    {
        // The control: with no rescued sibling at all, the refused initializer is a plain loss.
        var twin = Init(new NamedTypeSpec("SwiftUI.Edge"), TwinSymbol, disfavored: false);

        var (report, _) = Emit(new[] { twin });

        var row = Assert.Single(report.SkippedItems, i => i.DeclId == DeclIdFactory.ForMethod(twin).Canonical);
        Assert.Null(row.RecoveredBy);
        Assert.True(SkipAttributionLinker.IsLoss(row));
    }

    // `init(tip: <tipType>, edges: SwiftUI.Edge = …)` on Rescue.Host.
    private static MethodDecl Init(TypeSpec tipType, string symbol, bool disfavored, string tipLabel = "tip")
    {
        var init = TestDecls.Method(
            "init",
            methodType: MethodType.Static,
            isConstructor: true,
            parameters: new[]
            {
                TestDecls.Param(tipLabel, tipType),
                TestDecls.Param("edges", new NamedTypeSpec("SwiftUI.Edge"), hasDefault: true),
            },
            returnType: new NamedTypeSpec($"{ModuleName}.Host"),
            module: ModuleName) with
        {
            MangledName = symbol,
            IsDisfavoredOverload = disfavored,
        };
        return init;
    }

    private (BindingReport Report, string CSharp) Emit(IEnumerable<MethodDecl> inits)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = ModuleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var host = new StructDecl
        {
            Name = "Host",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ModuleName}.Host"),
            MangledName = "$s6Rescue4HostVN",
            MetadataAccessor = "$s6Rescue4HostVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        foreach (var init in inits)
        {
            init.ParentDecl = host;
            init.ModuleDecl = moduleDecl;
            host.Methods.Add(init);
        }
        moduleDecl.Types.Add(host);
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);
        // A wrapper library: the rescue emits only through a @_cdecl wrapper that lets Swift fill the
        // dropped default.
        typeDatabase.AsyncLibraryName = ModuleName + "SwiftBindings";

        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-rescue-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        BindingReport? report;
        try
        {
            new StringEmitter(scratch, typeDatabase, new NullLoggerFactory())
                .EmitModule(moduleDecl, new ModuleEmissionContext());
            report = ReportCollector.Complete();
        }
        finally
        {
            ReportCollector.Reset();
        }
        Assert.NotNull(report);

        var csharp = string.Concat(Directory.EnumerateFiles(scratch, "*.cs").Select(File.ReadAllText));
        return (report!, csharp);
    }
}
